/*-----------------------------------------------------------------------------*/
/* bfe_image.c                                                                 */
/*                                                                             */
/* SPDX-License-Identifier: GPL-2.0-or-later                                   */
/* Copyright (c) 2026 B. C. Services                                           */
/*                                                                             */
/* The engine's device and raw-image functions: the facts about a file or a    */
/* device (BFE_DeviceInfoGet), a bounded raw read of either (BFE_ReadRaw), a   */
/* bounded raw write of either -- the partition-table writer's way in          */
/* (BFE_WriteRaw) -- and the raw copy that images a device to a file or        */
/* writes an image file back to a device (BFE_CopyRaw) -- what Win32DiskImager */
/* or dd does, inside the same engine the file manager uses, so the GUI has    */
/* one progress window and one callback for both.  The raw copy also reads a   */
/* compressed image (.xz, .gz, .zst, .zip) and writes a .xz, through the       */
/* reader and writer of bfe_compress.c, and BFE_ImageInfoGet says which a      */
/* file is and how large its image.                                            */
/*                                                                             */
/* None of this touches lwext4.  The functions take the engine's busy flag     */
/* like every public call, so they run one at a time, and they may run while   */
/* an image is mounted: they open their own                                    */
/* targets through the raw I/O layer (bfe_device.c) and close them again.      */
/*                                                                             */
/* THE GUARDS ON A DEVICE WRITE, and why they are here and not in the GUI:     */
/* they key on the disk number Windows reports for the OPEN HANDLE, so no      */
/* spelling of a path (\\.\physicaldrive0, //./PhysicalDrive0, a volume        */
/* letter) reaches a disk the guard would refuse.  The system disk is never    */
/* written, a disk whose number cannot be told is treated as the system        */
/* disk, every volume on the target disk is locked for the duration (found     */
/* by enumerating volumes, so one without a drive letter is not missed), and   */
/* a copy that would run off the end of the device is refused before a         */
/* sector is written.                                                          */
/*-----------------------------------------------------------------------------*/

/* The C library and Windows; then the engine's shared internals (the gate,   */
/* the error recorder), the raw I/O layer, and the compressed-image reader    */
/* and writer.                                                                */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>
#include <windows.h>

#include "bfe_internal.h"
#include "bfe_device.h"
#include "bfe_blockdev.h"
#include "bfe_compress.h"

/*----------------------------------------------------------------------------*/
/* Definitions -- documentation table.  The two labels are matched by the     */
/* program (gui\CopyForms.cs, CPY_LABEL_COPY / CPY_LABEL_VERIFY) to word its  */
/* progress window, so a change here is made there too.                       */
/*----------------------------------------------------------------------------*/
#define BFI_CHUNK_BYTES    4194304          // one transfer of a raw copy, bytes (4 MiB: a whole number of any sector, one progress event each)
#define BFI_LABEL_COPY     "copy"           // pszImagePath of a raw copy's events during the copy
#define BFI_LABEL_VERIFY   "verify"         // pszImagePath of a raw copy's events during the read-back

/*----------------------------------------------------------------------------*/
/* BFI_Copy -- everything one raw copy holds open, so a single function can   */
/* release all of it on every exit.                                           */
/*----------------------------------------------------------------------------*/
typedef struct BFI_Copy
{
    BFV_Target       source;                // the source, open for reading
    BFV_Target       dest;                  // the destination, open for writing
    BFV_Locks        locks;                 // the destination disk's volumes, when the destination is a device
    uint8_t         *pbRead;                // the aligned chunk buffer for the source
    uint8_t         *pbBack;                // a second aligned chunk buffer, for the read-back of the verify pass
    const wchar_t   *pwszTo;                // the destination path (for the events, and for deleting a failed file)
    bool             bDeleteFile;           // true when the destination is a file this copy created and has not finished
    BFE_CopyCallback pfnEvent;              // the caller's callback, or NULL
    void            *pvContext;             // the caller's context for it
    BFZ_Reader      *pReader;               // the source's decompressor, when BFE_RAW_DECOMPRESS found a compressed file; NULL when the source is read as it is
    BFZ_Reader      *pBack;                 // the verify pass's decompressor of an .xz destination; NULL otherwise
    BFZ_Writer      *pWriter;               // the destination's .xz compressor, with BFE_RAW_XZ_OUT; NULL otherwise
    bool             bXzOut;                // true when the destination is written as .xz (and read back through pBack)
    bool             bSizeKnown;            // false when a compressed source's size cannot be told until it ends
} BFI_Copy;

/*----------------------------------------------------------------------------*/
/* BFI_Report:                                                                */
/*                                                                            */
/* Send one event to the copy's callback, if there is one, and hand back its  */
/* reply.                                                                     */
/*                                                                            */
/* Arguments:                                                                 */
/*     pCopy    : the copy.                                                   */
/*     uiKind   : BFE_EVENT_START, PROGRESS or DONE.                          */
/*     pszLabel : the phase label.                                            */
/*     ullDone  : bytes done so far in this phase.                            */
/*     ullTotal : bytes in this phase.                                        */
/*                                                                            */
/* Returns:                                                                   */
/*     int : the callback's BFE_REPLY_* value; BFE_REPLY_CONTINUE when there  */
/*           is no callback.                                                  */
/*----------------------------------------------------------------------------*/
static int BFI_Report(BFI_Copy *pCopy, uint32_t uiKind, const char *pszLabel, uint64_t ullDone, uint64_t ullTotal)
{
    BFE_CopyEvent event;

    if (pCopy->pfnEvent == NULL)
    {   /* Nobody is listening: carry on, so a caller with no callback        */
        /* simply gets a copy that runs to its end and cannot be cancelled.   */
        return(BFE_REPLY_CONTINUE);
    }

    /* The event, zeroed first so its reserved field is 0 as the interface    */
    /* promises: the phase's progress; the phase label ("copy" or "verify")   */
    /* where a copy's image path would be, since a raw copy has none; the     */
    /* destination as the host path; and always a plain file that has not     */
    /* failed (a failure is the return value, not an event).                  */
    memset(&event, 0, sizeof(event));
    event.ullBytesDone  = ullDone;
    event.ullBytesTotal = ullTotal;
    event.pszImagePath  = pszLabel;
    event.pwszHostPath  = pCopy->pwszTo;
    event.uiKind        = uiKind;
    event.uiType        = BFE_TYPE_FILE;
    event.iStatus       = BFE_OK;

    /* The caller's answer: carry on, or cancel.  The event lives on this    */
    /* stack frame, so the callback must copy anything it keeps -- the two   */
    /* string pointers in it are the label constant and the caller's own     */
    /* path, both valid for the whole copy.                                  */
    return(pCopy->pfnEvent(&event, pCopy->pvContext));
}

/*----------------------------------------------------------------------------*/
/* BFI_Finish:                                                                */
/*                                                                            */
/* The one exit of BFE_CopyRaw: release everything the copy holds, remove a   */
/* destination FILE the copy did not finish, release the busy flag, and hand  */
/* the status back, so a failure path is a single statement.  Every release   */
/* is safe on something never opened or already released, which is what lets  */
/* a failure at any step come here.                                           */
/*                                                                            */
/* Arguments:                                                                 */
/*     pCopy   : the copy.                                                    */
/*     iStatus : the status to return; a failure unless BFE_OK.               */
/*                                                                            */
/* Returns:                                                                   */
/*     int : iStatus, unchanged.                                              */
/*----------------------------------------------------------------------------*/
static int BFI_Finish(BFI_Copy *pCopy, int iStatus)
{
    /* The decoders and the encoder (each is NULL when not used, which the   */
    /* closes allow).  They hold the file handles but never close them, so   */
    /* the order against the handles below is not required; freeing them     */
    /* first leaves nothing pointing at a handle that is gone.  An encoder   */
    /* closed here unfinished drops its output -- the file it was writing    */
    /* is then deleted below.                                                */
    BFZ_ReaderClose(pCopy->pReader);
    BFZ_ReaderClose(pCopy->pBack);
    BFZ_WriterClose(pCopy->pWriter);
    pCopy->pReader = NULL;
    pCopy->pBack   = NULL;
    pCopy->pWriter = NULL;

    /* The two chunk buffers (freeing NULL is allowed).  Cleared after, as   */
    /* the decoders are, so nothing here can be freed twice.                 */
    BFV_FreeAligned(pCopy->pbRead);
    BFV_FreeAligned(pCopy->pbBack);
    pCopy->pbRead = NULL;
    pCopy->pbBack = NULL;

    /* Both targets, then the volume locks -- THE ORDER MATTERS: the         */
    /* destination is closed BEFORE the locks go, because Windows remounts   */
    /* a volume the moment its lock is released, and the copy's own handle   */
    /* must not still be open on the disk then.  BFV_Close only closes the   */
    /* handle (it does not flush; BFE_CopyRaw has flushed the destination    */
    /* on success, before it reports done) and is a no-op on a target        */
    /* never opened; BFV_UnlockDisk likewise on no locks.                    */
    BFV_Close(&pCopy->source);
    BFV_Close(&pCopy->dest);
    BFV_UnlockDisk(&pCopy->locks);

    if (iStatus != BFE_OK && pCopy->bDeleteFile)
    {   /* A file left half written is worse than none: remove it.  A device   */
        /* cannot be un-written, and bDeleteFile is never set for one.         */

        /* AFTER the close above, so the copy's own handle no longer holds   */
        /* the file.  The result is not checked: if something else holds     */
        /* it and the delete fails, the half file stays, but the caller is   */
        /* still told the copy failed -- the status is not changed.          */
        DeleteFileW(pCopy->pwszTo);
    }

    /* Let the next engine call in -- last, after every release, so the     */
    /* next call cannot find a target or lock of this copy still held.  A   */
    /* failure's text was recorded by BFX_Fail while the flag was still     */
    /* held, which is BFX_Fail's rule.                                      */
    BFX_Leave();

    /* The status the caller returns. */
    return(iStatus);
}

/*----------------------------------------------------------------------------*/
/* BFI_OpenDestination:                                                       */
/*                                                                            */
/* Open the destination of a raw copy for writing, as the right kind of       */
/* thing.  A path that is a DEVICE is opened as one; anything else -- an      */
/* existing file, or no file yet -- is opened as a file, created if missing   */
/* but NOT truncated: BFE_CopyRaw checks what it opened (not the source, not  */
/* the open session's file) and only then cuts it, with BFV_Truncate.         */
/*                                                                            */
/* Arguments:                                                                 */
/*     pwszTo  : the destination path.                                        */
/*     pTarget : receives the open target.                                    */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0 with the target open; otherwise the Windows error.             */
/*----------------------------------------------------------------------------*/
static int BFI_OpenDestination(const wchar_t *pwszTo, BFV_Target *pTarget)
{
    BFV_Target probe;
    int        iAccess;

    /* A file to open or create, kept as it is, unless the probe finds a     */
    /* device.  The caller (the program) asks before it overwrites a file;   */
    /* BFE_CopyRaw truncates it after its identity checks.                   */
    iAccess = BFV_ACCESS_OPENKEEP;

    /* The facts-only open, which tells the two apart: it succeeds on a       */
    /* device without Administrator and on a file, and fails on a path that   */
    /* does not exist.  It hands the target back already CLOSED, so there     */
    /* is no BFV_Close of the probe here.  A probe that fails for any         */
    /* reason leaves the choice at "create": a path that does not exist is    */
    /* the usual cause, and the real open below reports anything else.        */
    if (BFV_Open(pwszTo, BFV_ACCESS_QUERY, &probe) == 0)
    {   /* Something is there. */
        if (probe.bDevice)
        {   /* A device (a disk or a volume path): opened as it is, never   */
            /* "created" -- a create on a device path is not what Windows   */
            /* means by opening a disk for writing.                         */
            iAccess = BFV_ACCESS_WRITE;
        }
    }

    /* Open for real in the mode decided; its Windows error, or 0, is the   */
    /* answer.                                                              */
    return(BFV_Open(pwszTo, iAccess, pTarget));
}

/*----------------------------------------------------------------------------*/
/* BFE_DeviceInfoGet:                                                         */
/*                                                                            */
/* Report the facts about a file or device path: its length, sector size,     */
/* disk number, bus, model, whether its media is removable, and whether it    */
/* is on the disk Windows runs from.  A device's facts are read through a     */
/* zero-access open, so this works without Administrator and is what a        */
/* device picker can be built on from an ordinary program.                    */
/*                                                                            */
/* Arguments:                                                                 */
/*     pwszPath : the file or device path.                                    */
/*     pInfo    : receives the facts; zeroed first.                           */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_ARG for a missing argument; BFE_ERR_IMAGE_OPEN   */
/*           when the path cannot be opened at all (no such device, no such   */
/*           file); BFE_ERR_BUSY.                                             */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_DeviceInfoGet(const wchar_t *pwszPath, BFE_DeviceInfo *pInfo)
{
    BFV_Target target;
    uint32_t   uiSystem;
    int        iStatus;
    int        iError;

    if (pInfo == NULL)
    {   /* Nowhere to put the answer: refused before the gate, so no error   */
        /* text is recorded for it -- the text may only be written while     */
        /* the busy flag is held (BFX_Fail's rule), and it is not yet.       */
        return(BFE_ERR_ARG);
    }

    /* No facts until they are found: everything zero, and the disk number   */
    /* "none" (0 would be a real disk -- usually the system disk).           */
    memset(pInfo, 0, sizeof(*pInfo));
    pInfo->uiDisk = BFE_NO_DISK;

    /* The gate: the busy flag only -- facts may be asked for with or   */
    /* without an image open, and nothing is written.                   */
    iStatus = BFX_Enter(false, false);

    if (iStatus != BFE_OK)
    {   /* Another call is running: BFX_Enter has said so, and the flag is   */
        /* not ours, so there is nothing to release.  pInfo stays zeroed.    */
        return(iStatus);
    }

    if (pwszPath == NULL || pwszPath[0] == L'\0')
    {   /* No path given: refuse, and let the next call in.  The flag is   */
        /* the only thing held; the text is recorded before it goes.       */
        iStatus = BFX_Fail(BFE_ERR_ARG, "no path given");
        BFX_Leave();
        return(iStatus);
    }

    /* The facts-only open: no access is asked for a device, so this works   */
    /* without Administrator, and the target comes back closed -- which is   */
    /* why nothing below closes it.                                          */
    iError = BFV_Open(pwszPath, BFV_ACCESS_QUERY, &target);

    if (iError != 0)
    {   /* Nothing of that name, or it cannot be opened even for its facts. */

        /* Refuse with the Windows error -- the number tells "not found"    */
        /* from "access denied" -- and let the next call in.  Nothing was   */
        /* opened, so the flag is all there is to release.                  */
        iStatus = BFX_Fail(BFE_ERR_IMAGE_OPEN, "cannot open %ls (Windows error %d)", pwszPath, iError);
        BFX_Leave();
        return(iStatus);
    }

    /* The facts as the open found them, in the public structure's form      */
    /* (the two flags as 0 or 1).  The model string is copied whole, with    */
    /* its terminator, at the PUBLIC field's size: BFE_MODEL_BYTES and       */
    /* bfe_device.h's BFV_MODEL_BYTES are separate constants, both 64, and   */
    /* the device one must never be made the smaller or this copy reads      */
    /* past its source.                                                      */
    pInfo->ullSize       = target.ullSize;
    pInfo->uiSectorBytes = target.uiSector;
    pInfo->uiDisk        = target.uiDisk;
    pInfo->uiBus         = target.uiBus;
    pInfo->bDevice       = (target.bDevice != 0);
    pInfo->bRemovable    = (target.bRemovable != 0);
    memcpy(pInfo->szModel, target.szModel, sizeof(pInfo->szModel));

    if (target.bDevice)
    {   /* On the system disk?  "Cannot tell" counts as yes, as the guards do   */
        /* in BFE_WriteRaw and BFE_CopyRaw -- so a picker built on this never   */
        /* offers a disk those calls would then refuse.  (A FILE is never the   */
        /* system disk here, even one stored on it: writing a file is safe.)    */
        uiSystem = BFV_SystemDisk();

        if (uiSystem == BFV_NO_DISK || target.uiDisk == BFV_NO_DISK || target.uiDisk == uiSystem)
        {   /* The disk Windows runs from, or one that cannot be told apart. */
            pInfo->bSystem = 1;
        }
    }

    /* Let the next call in. */
    BFX_Leave();

    /* The facts are filled in. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFE_ReadRaw:                                                               */
/*                                                                            */
/* Read a byte range from a file or device into the caller's buffer, at any   */
/* offset and length -- the caller need not know a device's sector size.  The */
/* point: the GUI can read a card's first sectors with the same call it uses  */
/* on an image file and parse the partition table from them.                  */
/*                                                                            */
/* Arguments:                                                                 */
/*     pwszPath  : the file or device path.                                   */
/*     ullOffset : the first byte wanted.                                     */
/*     pvBuffer  : receives cbBytes bytes.                                    */
/*     cbBytes   : how many, 1 to BFE_READ_RAW_MAX.                           */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK with the buffer filled; BFE_ERR_ARG; BFE_ERR_IMAGE_OPEN;  */
/*           BFE_ERR_NO_MEMORY; BFE_ERR_IO_IMAGE when the range runs past the */
/*           end or the read fails; BFE_ERR_BUSY.                             */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_ReadRaw(const wchar_t *pwszPath, uint64_t ullOffset, void *pvBuffer, uint32_t cbBytes)
{
    BFV_Target target;
    uint8_t   *pbAligned;
    uint64_t   ullStart;
    uint64_t   ullEnd;
    int        iStatus;
    int        iError;

    /* The gate: the busy flag only -- a read may be made with or without   */
    /* an image open, and changes nothing.                                  */
    iStatus = BFX_Enter(false, false);

    if (iStatus != BFE_OK)
    {   /* Another call is running: BFX_Enter has said so, and the flag is   */
        /* not ours, so there is nothing to release.                         */
        return(iStatus);
    }

    if (pwszPath == NULL || pwszPath[0] == L'\0' || pvBuffer == NULL)
    {   /* No path, or nowhere to put the bytes: refuse, and let the next   */
        /* call in.  Only the flag is held; the text is recorded first.     */
        iStatus = BFX_Fail(BFE_ERR_ARG, "no path or no buffer given");
        BFX_Leave();
        return(iStatus);
    }

    if (cbBytes == 0 || cbBytes > BFE_READ_RAW_MAX)
    {   /* Nothing, or more than one call carries (16 MiB -- enough for any   */
        /* table or header, and the bound that keeps the widened device       */
        /* read below inside 32 bits): refuse, and let the next call in.      */
        iStatus = BFX_Fail(BFE_ERR_ARG, "a raw read is 1 to %u bytes", (unsigned)BFE_READ_RAW_MAX);
        BFX_Leave();
        return(iStatus);
    }

    /* Open for reading -- a device unbuffered, so the media itself is read   */
    /* and not a copy Windows cached from before another program wrote it.    */
    iError = BFV_Open(pwszPath, BFV_ACCESS_READ, &target);

    if (iError != 0)
    {   /* Missing, in use, or a device that needs Administrator. */

        /* Refuse with the Windows error -- the number is what tells those   */
        /* three apart -- and let the next call in.  Nothing was opened.     */
        iStatus = BFX_Fail(BFE_ERR_IMAGE_OPEN, "cannot open %ls for reading (Windows error %d)", pwszPath, iError);
        BFX_Leave();
        return(iStatus);
    }

    /* The range must lie inside the file or device.  Tested as two parts    */
    /* so the sum ullOffset + cbBytes is never formed: an offset near 2^64   */
    /* would wrap it and pass.                                               */
    if (ullOffset > target.ullSize || cbBytes > target.ullSize - ullOffset)
    {   /* The range is not all there: refuse, closing the target.  The    */
        /* whole read is refused rather than cut short, because a caller   */
        /* parsing a table from a short buffer would read stale bytes as   */
        /* entries.  Record, close, then let the next call in.             */
        iStatus = BFX_Fail(BFE_ERR_IO_IMAGE, "the range runs past the end of %ls", pwszPath);
        BFV_Close(&target);
        BFX_Leave();
        return(iStatus);
    }

    if (!target.bDevice)
    {   /* A file takes the read as asked: a buffered file handle reads at   */
        /* any offset and length, so no widening and no aligned copy.        */

        /* Read straight into the caller's buffer, then close: the target   */
        /* is not needed past this read either way, so it is closed         */
        /* before the result is even looked at.                             */
        iError = BFV_Read(&target, ullOffset, pvBuffer, cbBytes);
        BFV_Close(&target);

        if (iError != 0)
        {   /* Short, or failed: refuse, and let the next call in.  The   */
            /* target is already closed; the caller's buffer holds        */
            /* whatever arrived and must not be used.                     */
            iStatus = BFX_Fail(BFE_ERR_IO_IMAGE, "reading %ls failed (Windows error %d)", pwszPath, iError);
            BFX_Leave();
            return(iStatus);
        }

        /* Let the next call in. */
        BFX_Leave();

        /* The bytes are in the caller's buffer. */
        return(BFE_OK);
    }

    /* A device: widen the range to whole sectors -- the start rounded down,   */
    /* the end rounded up -- since an unbuffered device read must be.  The     */
    /* sector size is never 0 here: bfe_device.c refuses a device that         */
    /* reports none.  The sum cannot wrap: the range test above passed.        */
    ullStart = ullOffset - (ullOffset % target.uiSector);
    ullEnd   = ullOffset + cbBytes;

    if ((ullEnd % target.uiSector) != 0)
    {   /* The end is inside a sector: take the whole sector. */
        ullEnd += target.uiSector - (ullEnd % target.uiSector);
    }

    if (ullEnd > target.ullSize)
    {   /* The device's tail is shorter than a sector, which a real disk never is. */

        /* Refuse, closing the target, and let the next call in -- as for   */
        /* the range test above, and in the same order.                     */
        iStatus = BFX_Fail(BFE_ERR_IO_IMAGE, "the range runs past the end of %ls", pwszPath);
        BFV_Close(&target);
        BFX_Leave();
        return(iStatus);
    }

    /* The page-aligned buffer the device read needs (the caller's buffer    */
    /* cannot be relied on to be aligned); under BFE_READ_RAW_MAX plus two   */
    /* sectors, so the 32-bit length below cannot overflow.                  */
    pbAligned = (uint8_t *)BFV_AllocAligned((size_t)(ullEnd - ullStart));

    if (pbAligned == NULL)
    {   /* Out of memory for the aligned copy: refuse, closing the target     */
        /* (the one thing held besides the flag), and let the next call in.   */
        iStatus = BFX_Fail(BFE_ERR_NO_MEMORY, "out of memory");
        BFV_Close(&target);
        BFX_Leave();
        return(iStatus);
    }

    /* The widened read, then close: the device is not needed past it. */
    iError = BFV_Read(&target, ullStart, pbAligned, (uint32_t)(ullEnd - ullStart));
    BFV_Close(&target);

    if (iError != 0)
    {   /* The media failed or the device went away: refuse, freeing the     */
        /* buffer -- the target was closed straight after the read, so the   */
        /* buffer and the flag are all that is left.                         */
        iStatus = BFX_Fail(BFE_ERR_IO_IMAGE, "reading %ls failed (Windows error %d)", pwszPath, iError);
        BFV_FreeAligned(pbAligned);
        BFX_Leave();
        return(iStatus);
    }

    /* The wanted bytes sit at the offset the widening pushed them to; copy   */
    /* them out, free the aligned buffer, and let the next call in.           */
    memcpy(pvBuffer, pbAligned + (ullOffset - ullStart), cbBytes);
    BFV_FreeAligned(pbAligned);
    BFX_Leave();

    /* The bytes are in the caller's buffer. */
    return(BFE_OK);
}

/*-----------------------------------------------------------------------------*/
/* BFE_WriteRaw:                                                               */
/*                                                                             */
/* Write a byte range from the caller's buffer into an EXISTING file or        */
/* device -- the way a partition table reaches the media.  It is deliberately  */
/* small and strict where BFE_ReadRaw is forgiving: a file is never created    */
/* or extended, a device takes whole sectors only, and a device gets every     */
/* guard a device write gets here.  The write is flushed before success is     */
/* claimed, and Windows is told to re-read a device's partition table --       */
/* which is the point of the call.                                             */
/*                                                                             */
/* Arguments:                                                                  */
/*     pwszPath  : the file or device path.                                    */
/*     ullOffset : the first byte to write.                                    */
/*     pvBuffer  : the bytes.                                                  */
/*     cbBytes   : how many, 1 to BFE_WRITE_RAW_MAX.                           */
/*                                                                             */
/* Returns:                                                                    */
/*     int : BFE_OK with the bytes written and flushed; BFE_ERR_ARG for no     */
/*           path or buffer, a bad size, or a device range off the sector      */
/*           grid; BFE_ERR_IMAGE_OPEN when the path cannot be opened for       */
/*           writing (missing, read-only, in use, a device without             */
/*           Administrator); BFE_ERR_IO_IMAGE when the range runs past the     */
/*           end; BFE_ERR_SYSTEM_DISK; BFE_ERR_VOLUME_LOCK; BFE_ERR_NO_MEMORY; */
/*           BFE_ERR_IO_HOST when the write or the flush fails; BFE_ERR_BUSY.  */
/*-----------------------------------------------------------------------------*/
BFE_API int BFE_WriteRaw(const wchar_t *pwszPath, uint64_t ullOffset, const void *pvBuffer, uint32_t cbBytes)
{
    BFV_Target target;
    BFV_Locks  locks;
    uint8_t   *pbAligned;
    const void *pvWrite;
    uint32_t   uiSystem;
    int        iStatus;
    int        iError;

    /* The gate: the busy flag only -- the target is a path, not the open     */
    /* session, so an image may be open or not.  When one is, the session's   */
    /* own file or disk is refused below, by identity.                        */
    iStatus = BFX_Enter(false, false);

    if (iStatus != BFE_OK)
    {   /* Another call is running: BFX_Enter has said so, and the flag is   */
        /* not ours, so there is nothing to release.                         */
        return(iStatus);
    }

    if (pwszPath == NULL || pwszPath[0] == L'\0' || pvBuffer == NULL)
    {   /* No path, or nothing to write: refuse, and let the next call in.   */
        /* Only the flag is held; the text is recorded first.                */
        iStatus = BFX_Fail(BFE_ERR_ARG, "no path or no buffer given");
        BFX_Leave();
        return(iStatus);
    }

    if (cbBytes == 0 || cbBytes > BFE_WRITE_RAW_MAX)
    {   /* Nothing, or more than one call carries -- 16 MiB, far more than   */
        /* a partition table; anything bigger is BFE_CopyRaw's job, with     */
        /* its progress and cancel.  Refuse, and let the next call in.       */
        iStatus = BFX_Fail(BFE_ERR_ARG, "a raw write is 1 to %u bytes", (unsigned)BFE_WRITE_RAW_MAX);
        BFX_Leave();
        return(iStatus);
    }

    /* STEP 1: open for writing, and learn what it is.  WRITE, not CREATE: a   */
    /* path with nothing behind it is an error, never a new file.  The locks   */
    /* start empty because the release at the end runs for a file too.         */
    memset(&locks, 0, sizeof(locks));
    iError = BFV_Open(pwszPath, BFV_ACCESS_WRITE, &target);

    if (iError != 0)
    {   /* Missing, read-only, in use, or a device that needs Administrator. */

        /* Refuse with the Windows error -- the number tells those apart --   */
        /* and let the next call in.  Nothing was opened, and the locks       */
        /* were never taken (they are only taken for a device, below).        */
        iStatus = BFX_Fail(BFE_ERR_IMAGE_OPEN, "cannot open %ls for writing (Windows error %d)", pwszPath, iError);
        BFX_Leave();
        return(iStatus);
    }

    if (BFD_TargetIsObject(&target))
    {   /* The open session's own image file or disk, under whatever path:   */
        /* writing it would change bytes lwext4 holds cached and believes    */
        /* only it writes.  Refused -- the session must be closed first --   */
        /* closing the target, the one thing open.                           */
        iStatus = BFX_Fail(BFE_ERR_ARG, "%ls is the image or device the open session is using; close it first", pwszPath);
        BFV_Close(&target);
        BFX_Leave();
        return(iStatus);
    }

    /* STEP 2: the range must already be there -- inside the file or device.   */
    /* Two tests, so the wrapping sum ullOffset + cbBytes is never formed.     */
    if (ullOffset > target.ullSize || cbBytes > target.ullSize - ullOffset)
    {   /* Past the end: a file is not extended by this call, so a slip in   */
        /* an offset cannot grow an image.  Refuse, closing it.              */

        /* Record, close the target (the only thing open: no locks yet),      */
        /* then let the next call in.  Nothing has been written.  The code    */
        /* stays IO_IMAGE whatever an earlier operation's device lost: this   */
        /* call drives no block device, so BFX_Fail does not read a lost      */
        /* write into it.                                                     */
        iStatus = BFX_Fail(BFE_ERR_IO_IMAGE, "the range runs past the end of %ls", pwszPath);
        BFV_Close(&target);
        BFX_Leave();
        return(iStatus);
    }

    /* A file is written straight from the caller's buffer; a device swaps   */
    /* in an aligned copy below.  NULL means no copy to free at the end.     */
    pbAligned = NULL;
    pvWrite   = pvBuffer;

    /* STEP 3: a device -- whole sectors only, the guards, and a copy of the   */
    /* bytes into a page-aligned buffer, which an unbuffered device write      */
    /* requires and the caller's managed array cannot be relied on to be.      */
    if (target.bDevice)
    {   /* A real device.  The grid test comes first because it costs       */
        /* nothing; the system-disk test before any lock, because locking   */
        /* the system disk's volumes would dismount the running Windows.    */
        if ((ullOffset % target.uiSector) != 0 || (cbBytes % target.uiSector) != 0)
        {   /* Not on the sector grid: nothing is read to fill the gap -- a   */
            /* partial sector would need the rest of it read first and        */
            /* written back, and a table writer never needs that.  Refuse,    */
            /* closing it.                                                    */

            /* The device is the only thing open; no lock is held yet. */
            iStatus = BFX_Fail(BFE_ERR_ARG, "a device is written in whole sectors of %u bytes", target.uiSector);
            BFV_Close(&target);
            BFX_Leave();
            return(iStatus);
        }

        /* The system-disk guard, on the disk number of the open handle. */
        uiSystem = BFV_SystemDisk();

        if (uiSystem == BFV_NO_DISK || target.uiDisk == BFV_NO_DISK || target.uiDisk == uiSystem)
        {   /* The disk Windows runs from, or one that cannot be told apart   */
            /* from it: never written, whatever the caller says.  Refuse.     */

            /* No lock was taken (the test is before the lock, for the      */
            /* reason above), so closing the device is the whole release.   */
            iStatus = BFX_Fail(BFE_ERR_SYSTEM_DISK, "%ls is the disk Windows is running from, or cannot be told apart from it", pwszPath);
            BFV_Close(&target);
            BFX_Leave();
            return(iStatus);
        }

        /* Lock and dismount every volume on the disk for the write: Windows   */
        /* refuses a raw write under a mounted volume, and the lock keeps      */
        /* it from remounting mid-write (only the volumes on the disk are      */
        /* opened for writing; one elsewhere is only asked where it lies).     */
        /* The target's own handle stays open through the lock.  That is       */
        /* right for a whole disk (\\.\PhysicalDriveN), the only device path   */
        /* the program writes, whose handle holds no volume; a VOLUME path     */
        /* (\\.\X:) would have its handle open on a volume being locked,       */
        /* and Windows would refuse that lock (BFE_ERR_VOLUME_LOCK).  Such a   */
        /* caller would need BFE_Open's order: close, lock, then reopen.       */
        iError = BFV_LockDisk(target.uiDisk, &locks);

        if (iError != 0)
        {   /* The disk could not be locked -- a volume on it in use, or   */
            /* one whose disk could not be told.  Refuse with the lock's   */
            /* own sentence, which says which, closing the device          */
            /* (BFV_LockDisk leaves nothing held).                         */
            iStatus = BFX_Fail(BFE_ERR_VOLUME_LOCK, "%s", locks.szFailure);
            BFV_Close(&target);
            BFX_Leave();
            return(iStatus);
        }

        /* The aligned copy of the caller's bytes.  Taken after the locks      */
        /* only because the guards come first; it holds nothing on the disk.   */
        pbAligned = (uint8_t *)BFV_AllocAligned(cbBytes);

        if (pbAligned == NULL)
        {   /* Out of memory for the aligned copy: refuse, closing the device   */
            /* before the locks go, as at the end.                              */
            iStatus = BFX_Fail(BFE_ERR_NO_MEMORY, "out of memory");
            BFV_Close(&target);
            BFV_UnlockDisk(&locks);
            BFX_Leave();
            return(iStatus);
        }

        /* Fill it, and write from it instead of the caller's buffer. */
        memcpy(pbAligned, pvBuffer, cbBytes);
        pvWrite = pbAligned;
    }

    /* STEP 4: the write, then the flush -- on the media, or in the file, before   */
    /* success is claimed.  iStatus still holds the gate's BFE_OK, and only a      */
    /* failure below changes it: that is how STEP 5 knows the write succeeded.     */
    iError = BFV_Write(&target, ullOffset, pvWrite, cbBytes);

    if (iError == 0)
    {   /* Written; now make it real -- a file too, so the table is then   */
        /* durable.                                                        */
        iError = BFV_Flush(&target);

        if (iError != 0)
        {   /* The flush is the failure: the bytes may not be on the media.   */
            /* Recorded, and the release below runs as on success.            */
            iStatus = BFX_Fail(BFE_ERR_IO_HOST, "%ls did not confirm the write (Windows error %d)", pwszPath, iError);
        }
    }
    else
    {   /* Refused: write-protected, failed, or pulled out.  The byte in      */
        /* the message is where the write BEGAN -- one call, one result, so   */
        /* where inside the range it failed is not known; part of the range   */
        /* may already be on the media.  No flush is tried after a failed     */
        /* write.                                                             */
        iStatus = BFX_Fail(BFE_ERR_IO_HOST, "writing %ls failed at byte %llu (Windows error %d)", pwszPath,
                           (unsigned long long)ullOffset, iError);
    }

    /* STEP 5: on success a device's table is re-read by Windows.  Then the     */
    /* target is closed before the locks go, so the volumes remount only once   */
    /* the write has reached the media.                                         */
    if (iStatus == BFE_OK)
    {   /* Everything was written and confirmed: ask Windows to re-read       */
        /* the table.  BFV_RefreshDisk does nothing for a file, and its       */
        /* own failure is not reported -- the write itself succeeded and is   */
        /* on the media; only Windows' view of the table may lag behind it.   */
        BFV_RefreshDisk(&target);
    }

    /* The release, the same on every path from here: the aligned copy (if   */
    /* any), the target, then the locks (none for a file), then the gate.    */
    /* Each is safe on what was never taken: NULL buffer, empty locks.       */
    BFV_FreeAligned(pbAligned);
    BFV_Close(&target);
    BFV_UnlockDisk(&locks);
    BFX_Leave();

    /* BFE_OK, or the failure recorded above. */
    return(iStatus);
}

/*----------------------------------------------------------------------------*/
/* BFI_ReadSource:                                                            */
/*                                                                            */
/* The next piece of the source's IMAGE bytes, from a plain or a compressed   */
/* source alike.  The piece comes back short only where the image ends.       */
/*                                                                            */
/* Arguments:                                                                 */
/*     pCopy     : the copy, its source open.                                 */
/*     ullOffset : the image byte this piece starts at (used for a plain      */
/*                 source; a decoder is already there).                       */
/*     pbBuffer  : receives the bytes (page-aligned: a chunk buffer).         */
/*     cbWant    : how many, at most one chunk.                               */
/*     pcbGot    : receives how many came back.                               */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_IO_IMAGE when the read fails or the compressed   */
/*           data is damaged; BFE_ERR_UNSUPPORTED for an .xz filter not built */
/*           in.                                                              */
/*----------------------------------------------------------------------------*/
static int BFI_ReadSource(BFI_Copy *pCopy, uint64_t ullOffset, uint8_t *pbBuffer, uint32_t cbWant, uint32_t *pcbGot)
{
    int iError;

    /* Nothing read until a read succeeds. */
    *pcbGot = 0;

    if (pCopy->pReader != NULL)
    {   /* Compressed: the decoder hands the image's bytes back in order.    */
        /* ullOffset is not used: the decoder can only go forward, and the   */
        /* callers ask for the pieces in order (the copy and the verify      */
        /* pass both read front to back), so it is already there.            */
        /* Its failure is recorded by the reader and passed on as it is.     */
        return(BFZ_ReaderRead(pCopy->pReader, pbBuffer, cbWant, pcbGot));
    }

    /* Read as it is: exactly the range asked.  For a device source that is   */
    /* always whole sectors -- BFE_CopyRaw refuses an offset or length off    */
    /* the grid, and a chunk is a whole number of any sector.                 */
    iError = BFV_Read(&pCopy->source, ullOffset, pbBuffer, cbWant);

    if (iError != 0)
    {   /* The source failed part-way: a bad sector, a card pulled out, or   */
        /* a short read at an end the size did not predict.  The byte is     */
        /* the piece's START (one read, one result), and the Windows error   */
        /* tells which.  Nothing to release here: the caller's BFI_Finish    */
        /* closes everything.                                                */
        return(BFX_Fail(BFE_ERR_IO_IMAGE, "reading the source failed at byte %llu (Windows error %d)",
                        (unsigned long long)ullOffset, iError));
    }

    /* A plain read is all or nothing: the whole range came. */
    *pcbGot = cbWant;

    /* The whole range, as asked. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFI_SkipImage:                                                             */
/*                                                                            */
/* Decode and drop the first ullBytes of a compressed image, so a copy can    */
/* start at an offset into it.  A decoder cannot seek: the bytes before the   */
/* offset are produced and thrown away, a chunk at a time.                    */
/*                                                                            */
/* Arguments:                                                                 */
/*     pReader   : the decoder, at the image's start.                         */
/*     pbScratch : a chunk buffer to decode into.                             */
/*     ullBytes  : how many to drop.                                          */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_ARG when the image ends before the offset; the   */
/*           decoder's error otherwise.                                       */
/*----------------------------------------------------------------------------*/
static int BFI_SkipImage(BFZ_Reader *pReader, uint8_t *pbScratch, uint64_t ullBytes)
{
    uint32_t cbAsk;
    uint32_t cbGot;
    int      iStatus;

    while (ullBytes != 0)
    {   /* One chunk per iteration, decoded and dropped. */

        /* A chunk, or what is left of the skip if less. */
        cbAsk = BFI_CHUNK_BYTES;

        if (ullBytes < cbAsk)
        {   /* Less than a chunk to go. */
            cbAsk = (uint32_t)ullBytes;
        }

        /* Decode into the scratch buffer; the bytes are not looked at.  The   */
        /* scratch is the copy's own chunk buffer, free at both callers'       */
        /* moments (before the copy starts, and before the verify reads).      */
        iStatus = BFZ_ReaderRead(pReader, pbScratch, cbAsk, &cbGot);

        if (iStatus != BFE_OK)
        {   /* Damaged, or unreadable: the failure is already recorded, and   */
            /* the reader is the caller's to close.                           */
            return(iStatus);
        }

        if (cbGot == 0)
        {   /* The image ended first: the caller asked for an offset it does not have. */

            /* An argument error, not damage: the data decoded cleanly to its   */
            /* end.  BFE_CopyRaw checks the offset against a KNOWN size         */
            /* before it gets here, so this is the case of a size the format    */
            /* does not record (gzip, some .zst), found only by decoding.       */
            return(BFX_Fail(BFE_ERR_ARG, "the source offset is past the end of the image"));
        }

        /* Fewer than asked only at the image's end, which the next pass   */
        /* then finds as 0; so count down by what actually came.           */
        ullBytes -= cbGot;
    }

    /* The decoder now stands at the offset. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFI_Verify:                                                                */
/*                                                                            */
/* The read-back pass of a raw copy: compare the destination with the source, */
/* chunk by chunk, reporting progress as the copy did.  What is compared is   */
/* the IMAGE at both ends -- a compressed source or an .xz destination is     */
/* decoded again -- never the files.                                          */
/*                                                                            */
/* Arguments:                                                                 */
/*     pCopy         : the copy, both targets still open.                     */
/*     ullFromOffset : where the source range starts (in the image).          */
/*     ullToOffset   : where the destination range starts.                    */
/*     ullLength     : the bytes to compare -- what the copy wrote.           */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK when every byte matches; BFE_ERR_VERIFY at the first      */
/*           difference, or for an .xz that does not read back whole;         */
/*           BFE_ERR_CANCELLED; BFE_ERR_IO_IMAGE / IO_HOST when a read fails. */
/*----------------------------------------------------------------------------*/
static int BFI_Verify(BFI_Copy *pCopy, uint64_t ullFromOffset, uint64_t ullToOffset, uint64_t ullLength)
{
    uint64_t ullDone;
    uint64_t cbPiece;
    uint64_t cbBack;
    uint64_t i;
    uint32_t cbGot;
    uint8_t  bExtra;
    int      iStatus;
    int      iError;

    /* The verify phase begins: its own START event, which the caller may   */
    /* answer with cancel before anything is read.                          */
    if (BFI_Report(pCopy, BFE_EVENT_START, BFI_LABEL_VERIFY, 0, ullLength) == BFE_REPLY_CANCEL)
    {   /* Cancelled before it began.  Every failure and cancel in this      */
        /* function has the same consequence for a destination FILE: the     */
        /* copy's BFI_Finish deletes it, since bDeleteFile is cleared only   */
        /* once the verify has passed -- an unverified file is not kept.     */
        /* Nothing is released here; BFI_Finish does that too.               */
        return(BFX_Fail(BFE_ERR_CANCELLED, "cancelled"));
    }

    if (pCopy->pReader != NULL)
    {   /* A compressed source: a fresh decoder from its start, moved to the offset. */

        /* The copy's decoder stands at the image's end, and a decoder      */
        /* cannot be rewound; close it and open another on the same         */
        /* handle, which reads from the file's start -- so what is          */
        /* compared is the image, not the compressed file.                  */
        /* pReader is cleared between the two, so if the reopen fails       */
        /* (leaving its out-parameter NULL) BFI_Finish does not close the   */
        /* old reader a second time.                                        */
        BFZ_ReaderClose(pCopy->pReader);
        pCopy->pReader = NULL;
        iStatus        = BFZ_ReaderOpen(pCopy->source.hFile, &pCopy->pReader);

        if (iStatus == BFE_OK)
        {   /* Open again: on to the offset. */
            iStatus = BFI_SkipImage(pCopy->pReader, pCopy->pbRead, ullFromOffset);
        }

        if (iStatus != BFE_OK)
        {   /* The source would not decode a second time -- the file changed   */
            /* or the read failed; the reader's own text says which.  The      */
            /* reader, if open, is BFI_Finish's to close.                      */
            return(iStatus);
        }
    }

    if (pCopy->bXzOut)
    {   /* An .xz destination: read back through a decoder of its own, which   */
        /* must find .xz -- so the verify also proves the file written is a    */
        /* whole, readable .xz, and (the test after the loop) holds nothing    */
        /* past the image.                                                     */

        /* The file's own handle, opened for reading as well as writing (a    */
        /* CREATE open asks for both -- bfe_device.c) and buffered, as the    */
        /* reader requires; the reader starts at the file's first byte,       */
        /* wherever the writer left the position.  Any open failure is a      */
        /* failed verify, whatever liblzma said: the file is what is wrong.   */
        iStatus = BFZ_ReaderOpen(pCopy->dest.hFile, &pCopy->pBack);

        if (iStatus != BFE_OK)
        {   /* Not readable as written.  The reader's own text is replaced   */
            /* by the verify's, which is the fact the user needs; pBack is   */
            /* NULL, so there is nothing to close.                           */
            return(BFX_Fail(BFE_ERR_VERIFY, "the .xz file written does not read back"));
        }

        if (BFZ_ReaderKind(pCopy->pBack) != BFZ_KIND_XZ)
        {   /* Something other than .xz is on the disk -- the writer always   */
            /* writes an xz header first, so this means the file was          */
            /* replaced or damaged.  pBack is open: BFI_Finish closes it.     */
            return(BFX_Fail(BFE_ERR_VERIFY, "the file written does not read back as .xz"));
        }
    }

    /* Nothing compared yet. */
    ullDone = 0;

    while (ullDone < ullLength)
    {   /* One chunk per iteration: read both, compare, report. */

        /* A chunk, or what is left if less. */
        cbPiece = ullLength - ullDone;

        if (cbPiece > BFI_CHUNK_BYTES)
        {   /* More than one chunk holds. */
            cbPiece = BFI_CHUNK_BYTES;
        }

        /* The source's piece, again: into pbRead, which the copy has done    */
        /* with.  It must come back whole -- the copy already read it once.   */
        iStatus = BFI_ReadSource(pCopy, ullFromOffset + ullDone, pCopy->pbRead, (uint32_t)cbPiece, &cbGot);

        if (iStatus != BFE_OK)
        {   /* The source could not be read again: its failure is recorded   */
            /* (by BFI_ReadSource or the reader) and passed on as it is --   */
            /* a source failure, not a mismatch.                             */
            return(iStatus);
        }

        if (cbGot != cbPiece)
        {   /* Shorter the second time: the file changed under the copy (a     */
            /* decoder is deterministic, and a plain read is all or nothing,   */
            /* so nothing else explains it).                                   */
            return(BFX_Fail(BFE_ERR_IO_IMAGE, "the source ended early when it was read again"));
        }

        if (pCopy->bXzOut)
        {   /* The .xz destination's next piece, decoded. */
            iStatus = BFZ_ReaderRead(pCopy->pBack, pCopy->pbBack, (uint32_t)cbPiece, &cbGot);

            if (iStatus != BFE_OK)
            {   /* Damaged as written.  As at the open, the reader's text is   */
                /* replaced by the verify's: the file is what failed.  The     */
                /* byte is the start of the piece that would not decode.       */
                return(BFX_Fail(BFE_ERR_VERIFY, "the .xz file written does not decompress at byte %llu", (unsigned long long)ullDone));
            }

            if (cbGot != cbPiece)
            {   /* It holds less than was copied. */
                return(BFX_Fail(BFE_ERR_VERIFY, "the .xz file written ends at byte %llu, before the image does",
                                (unsigned long long)(ullDone + cbGot)));
            }
        }
        else
        {   /* A plain destination: the destination's chunk, widened to whole sectors on a device. */
            cbBack = cbPiece;

            if (pCopy->dest.bDevice && (cbBack % pCopy->dest.uiSector) != 0)
            {   /* The last chunk of a padded file source.  Only a partial last    */
                /* chunk is ever widened -- a whole chunk is whole sectors -- so   */
                /* the read still fits pbBack, a chunk long.                       */
                cbBack += pCopy->dest.uiSector - (cbBack % pCopy->dest.uiSector);
            }

            /* The destination's piece, at the same distance into its range.    */
            /* A device is read unbuffered, so this is the media itself, not    */
            /* the copy's own writes still held in Windows' cache -- which is   */
            /* what makes the comparison worth doing.  A FILE destination is    */
            /* read through the cache, so for a file it proves the bytes        */
            /* reached Windows intact, not that the disk holds them.            */
            iError = BFV_Read(&pCopy->dest, ullToOffset + ullDone, pCopy->pbBack, (uint32_t)cbBack);

            if (iError != 0)
            {   /* The destination could not be read back: IO_HOST, the   */
                /* destination's side, with the Windows error.            */
                return(BFX_Fail(BFE_ERR_IO_HOST, "reading the destination back failed (Windows error %d)", iError));
            }
        }

        /* Compared over the source's bytes only: a widened device read's   */
        /* extra tail (the padding the copy wrote) is not image.            */
        if (memcmp(pCopy->pbRead, pCopy->pbBack, (size_t)cbPiece) != 0)
        {   /* A difference: find its offset so the report can name it.  The   */
            /* fast memcmp decides; the byte walk runs only on a mismatch,     */
            /* so a good copy pays nothing for the exact offset.               */
            for (i = 0; i < cbPiece; i++)
            {   /* One byte per iteration until the first mismatch. */
                if (pCopy->pbRead[i] != pCopy->pbBack[i])
                {   /* This is the byte. */
                    break;
                }
            }

            return(BFX_Fail(BFE_ERR_VERIFY, "the data read back differs from the source at byte %llu",
                            (unsigned long long)(ullDone + i)));
        }

        /* The piece matched: count it, and report -- the caller may cancel. */
        ullDone += cbPiece;

        if (BFI_Report(pCopy, BFE_EVENT_PROGRESS, BFI_LABEL_VERIFY, ullDone, ullLength) == BFE_REPLY_CANCEL)
        {   /* The caller stopped it. */
            return(BFX_Fail(BFE_ERR_CANCELLED, "cancelled"));
        }
    }

    if (pCopy->bXzOut)
    {   /* The .xz must end where the image did: one more byte must not come. */
        iStatus = BFZ_ReaderRead(pCopy->pBack, &bExtra, 1, &cbGot);

        if (iStatus != BFE_OK || cbGot != 0)
        {   /* Damaged at its end, or more in it than was copied. */
            return(BFX_Fail(BFE_ERR_VERIFY, "the .xz file written does not end where the image does"));
        }
    }

    /* The verify phase's DONE event.  Its reply is not looked at: there is   */
    /* nothing left to cancel.                                                */
    BFI_Report(pCopy, BFE_EVENT_DONE, BFI_LABEL_VERIFY, ullLength, ullLength);

    /* Every byte matched. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFE_CopyRaw:                                                               */
/*                                                                            */
/* Copy a byte range from one file or device to another, as dd does -- the    */
/* imaging call -- with the device guards, progress and cancel, a flush       */
/* before success is claimed, and a read-back when asked.  Every exit goes    */
/* through BFI_Finish, which releases whatever was taken and removes a        */
/* destination FILE the copy did not finish.                                  */
/*                                                                            */
/* COMPRESSED IMAGES ride the same steps.  With BFE_RAW_DECOMPRESS a .xz /    */
/* .gz / .zst / .zip file source is read through its decoder, and the offset  */
/* and length then count the image's bytes.  With BFE_RAW_XZ_OUT the          */
/* destination file is written through the .xz encoder.  The verify pass      */
/* compares IMAGES, never files.                                              */
/*                                                                            */
/* ONE PARTITION'S READ, WRITE AND ERASE ride them too.  A read is a plain    */
/* copy from the disk's partition range into a new file.  A write into a      */
/* disk image FILE uses BFE_RAW_IN_PLACE: the destination must exist, is      */
/* opened without truncation, the range must lie inside it, and a failure     */
/* leaves it as written -- the treatment a device always gets, since cutting  */
/* the file or deleting it would destroy every other partition in it.  An     */
/* erase uses BFE_RAW_ZERO_SOURCE: no source at all, a buffer of zeros        */
/* written over the range, in place; it is not read back, as a format's       */
/* zero-fill is not either -- why is not recorded (a counterfeit card can     */
/* drop zeros as silently as any other bytes, so it is not that zeros cannot  */
/* be lost).                                                                  */
/*                                                                            */
/* Arguments:                                                                 */
/*     pwszFrom      : the source path; ignored (may be NULL) with            */
/*                     BFE_RAW_ZERO_SOURCE.                                   */
/*     ullFromOffset : where in the source to start.                          */
/*     pwszTo        : the destination path.                                  */
/*     ullToOffset   : where in the destination to start.                     */
/*     ullLength     : bytes to copy; 0 for "to the end of the source" (never */
/*                     with BFE_RAW_ZERO_SOURCE, which has no source end).    */
/*     uiFlags       : BFE_RAW_VERIFY, BFE_RAW_DECOMPRESS, BFE_RAW_XZ_OUT,    */
/*                     BFE_RAW_IN_PLACE, BFE_RAW_ZERO_SOURCE, together as     */
/*                     wanted (the refused pairs are in the header), or 0.    */
/*     pfnEvent      : the progress callback, or NULL.                        */
/*     pvContext     : handed to the callback.                                */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_ARG; BFE_ERR_IMAGE_OPEN (either end could not be */
/*           opened); BFE_ERR_SYSTEM_DISK; BFE_ERR_NO_SPACE; BFE_ERR_VOLUME_  */
/*           LOCK; BFE_ERR_NO_MEMORY; BFE_ERR_IO_IMAGE (a source read, or     */
/*           compressed data that is damaged or cut short);                   */
/*           BFE_ERR_UNSUPPORTED (a compressed source this build cannot       */
/*           decode: an .xz filter other than LZMA2, a zip method other than  */
/*           stored or deflate); BFE_ERR_IO_HOST (a destination write or      */
/*           flush, or the .xz encoder); BFE_ERR_VERIFY; BFE_ERR_CANCELLED;   */
/*           BFE_ERR_BUSY.                                                    */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_CopyRaw(const wchar_t *pwszFrom, uint64_t ullFromOffset,
                        const wchar_t *pwszTo, uint64_t ullToOffset,
                        uint64_t ullLength, uint32_t uiFlags,
                        BFE_CopyCallback pfnEvent, void *pvContext)
{
    BFI_Copy copy;
    uint64_t ullDone;
    uint64_t ullNeeded;
    uint64_t ullSize;
    uint64_t ullStartTotal;
    uint64_t cbPiece;
    uint64_t cbWrite;
    uint32_t cbGot;
    uint32_t uiSystem;
    bool     bToEnd;
    bool     bEnded;
    bool     bInPlace;
    bool     bZero;
    int      iStatus;
    int      iError;

    /* The gate: the busy flag only -- both ends are paths, not the open      */
    /* session, so an image may be open or not.  When one is, a destination   */
    /* that is the session's own file or disk is refused (STEP 2).            */
    iStatus = BFX_Enter(false, false);

    if (iStatus != BFE_OK)
    {   /* Another call is running: BFX_Enter has said so.  Returned        */
        /* directly, NOT through BFI_Finish -- the flag is not ours, and    */
        /* BFI_Finish would release it from under the call that holds it.   */
        return(iStatus);
    }

    /* Nothing is held yet; BFI_Finish can run from here on.  Zeroed, so   */
    /* every pointer is NULL and the lock table empty; the two handles     */
    /* "not open"; the caller's path and callback kept; the size known     */
    /* until a decoder says otherwise; and whether the output is .xz.      */
    memset(&copy, 0, sizeof(copy));
    copy.source.hFile = INVALID_HANDLE_VALUE;
    copy.dest.hFile   = INVALID_HANDLE_VALUE;
    copy.pwszTo       = pwszTo;
    copy.pfnEvent     = pfnEvent;
    copy.pvContext    = pvContext;
    copy.bSizeKnown   = true;
    copy.bXzOut       = ((uiFlags & BFE_RAW_XZ_OUT) != 0);
    bToEnd            = false;

    /* The two partition modes.  Zeros are always written in place: there is   */
    /* no source to make a new file FROM, and an erase exists to clear part    */
    /* of something that is already there.                                     */
    bZero    = ((uiFlags & BFE_RAW_ZERO_SOURCE) != 0);
    bInPlace = ((uiFlags & BFE_RAW_IN_PLACE) != 0) || bZero;

    /* EVERY REFUSAL BELOW IS ONE STATEMENT: return(BFI_Finish(&copy,       */
    /* BFX_Fail(...))).  BFX_Fail is the argument, so C evaluates it        */
    /* first -- the text is recorded while the flag is still held, which    */
    /* is BFX_Fail's rule -- and BFI_Finish then releases whatever this     */
    /* copy has taken by that point, safely, since every release in it is   */
    /* a no-op on something not yet taken.                                  */
    if ((!bZero && (pwszFrom == NULL || pwszFrom[0] == L'\0')) || pwszTo == NULL || pwszTo[0] == L'\0')
    {   /* A path is missing (an erase needs only the destination): nothing   */
        /* is open yet.                                                       */
        return(BFI_Finish(&copy, BFX_Fail(BFE_ERR_ARG, "both a source and a destination path are needed")));
    }

    if (bZero && ((uiFlags & (BFE_RAW_VERIFY | BFE_RAW_DECOMPRESS | BFE_RAW_XZ_OUT)) != 0 || ullLength == 0))
    {   /* Zeros have no source to decompress, nothing to read back against,   */
        /* no file to compress into, and no source end for "length 0" to       */
        /* mean -- each a caller's mistake, refused before anything opens.     */
        return(BFI_Finish(&copy, BFX_Fail(BFE_ERR_ARG, "an erase takes a length and none of verify, decompress or .xz")));
    }

    if (bInPlace && copy.bXzOut)
    {   /* An .xz is a new file written from its start; writing one into a     */
        /* range of an existing file would leave neither a valid .xz nor the   */
        /* file as it was.                                                     */
        return(BFI_Finish(&copy, BFX_Fail(BFE_ERR_ARG, "an in-place write cannot be compressed as .xz")));
    }

    /* (A source and destination that are the same file or disk are refused   */
    /* in STEP 2, by identity, once both are open -- before anything at the   */
    /* destination is truncated or written.)                                  */

    /* STEP 1: the source, and the length.  Opened for reading; a device   */
    /* unbuffered, so the media itself is what is copied.                  */
    if (bZero)
    {   /* An erase: no source is opened (copy.source stays "not open", so   */
        /* every later test of it -- a device source's sectors, the          */
        /* identity check, BFI_Finish's close -- sees nothing).  The zeros   */
        /* are exactly the length asked for, counted from 0, so the range    */
        /* checks below pass as for a source of that size.                   */
        ullFromOffset = 0;
        ullSize       = ullLength;
    }
    else
    {   /* A real source to read. */
        iError = BFV_Open(pwszFrom, BFV_ACCESS_READ, &copy.source);

        if (iError != 0)
        {   /* Missing, in use, or a device that needs Administrator -- the   */
            /* Windows error in the text tells which.  Nothing is open yet.   */
            return(BFI_Finish(&copy, BFX_Fail(BFE_ERR_IMAGE_OPEN, "cannot open the source %ls (Windows error %d)", pwszFrom, iError)));
        }

        /* The image's size: the source's own, unless it is a compressed    */
        /* file being decompressed, when it is what the format records --   */
        /* or not known until the image ends (both below).                  */
        ullSize = copy.source.ullSize;
    }

    if ((uiFlags & BFE_RAW_DECOMPRESS) != 0 && !copy.source.bDevice)
    {   /* A file that may be compressed: its first bytes decide.  Never a   */
        /* device: a card holds an image, not a compressed file, and the     */
        /* reader needs a buffered handle, which a device's is not.          */
        iStatus = BFZ_ReaderOpen(copy.source.hFile, &copy.pReader);

        if (iStatus != BFE_OK)
        {   /* Unreadable, or a compressed file too damaged to start (the text says which). */

            /* Already recorded by the reader, so passed straight to          */
            /* BFI_Finish; pReader is NULL, and the source is closed there.   */
            return(BFI_Finish(&copy, iStatus));
        }

        if (BFZ_ReaderKind(copy.pReader) == BFZ_KIND_RAW)
        {   /* Plain after all: read as it is, exactly as without the flag.   */
            /* The reader is dropped rather than used, because a plain read   */
            /* goes to any offset directly instead of decoding up to it.      */
            BFZ_ReaderClose(copy.pReader);
            copy.pReader = NULL;
        }
        else
        {   /* Compressed: the image's size, if the format says. */
            ullSize = BFZ_ReaderSize(copy.pReader);

            if (ullSize == BFZ_SIZE_UNKNOWN)
            {   /* Known only once the image ends (gzip, and a .zst without   */
                /* frame sizes): a length of 0 then runs the copy to the      */
                /* image's end, with the device's room checked chunk by       */
                /* chunk and the progress events counting the compressed      */
                /* file -- each said below.                                   */
                copy.bSizeKnown = false;
            }
        }
    }

    /* The range against the size: refused now when the size is known; when   */
    /* it is not, a length of 0 means "run until the image ends" (bToEnd),    */
    /* and a length given is held to when the image turns out shorter.        */
    if (copy.bSizeKnown)
    {   /* The range is checked against the size now, before anything at   */
        /* the destination is opened -- so a bad range never truncates a   */
        /* destination file.                                               */
        if (ullFromOffset > ullSize)
        {   /* Starting beyond the end. */
            return(BFI_Finish(&copy, BFX_Fail(BFE_ERR_ARG, "the source offset is past the end of %ls", pwszFrom)));
        }

        if (ullLength == 0)
        {   /* To the end of the source. */
            ullLength = ullSize - ullFromOffset;
        }

        /* Subtracted rather than added, so a huge length cannot wrap the sum. */
        if (ullLength > ullSize - ullFromOffset)
        {   /* More than the source holds from there. */
            return(BFI_Finish(&copy, BFX_Fail(BFE_ERR_ARG, "the range runs past the end of the source %ls", pwszFrom)));
        }
    }
    else if (ullLength == 0)
    {   /* Size unknown, and "to the end": the copy runs until the image stops. */
        bToEnd = true;
    }

    if (copy.source.bDevice)
    {   /* A device is read in whole sectors, so the range must be sectors.   */
        /* (A device source is never decoded, so its size is always known     */
        /* and ullLength has been settled above.)                             */
        if ((ullFromOffset % copy.source.uiSector) != 0 || (ullLength % copy.source.uiSector) != 0)
        {   /* Not sector-aligned: refused rather than widened, because a   */
            /* raw copy promises exactly the bytes asked for.               */
            return(BFI_Finish(&copy, BFX_Fail(BFE_ERR_ARG, "a device is read in whole sectors of %u bytes", copy.source.uiSector)));
        }
    }

    /* STEP 2: the destination, and the guards on a device.  It is opened     */
    /* WITHOUT truncating an existing file, so that what it is can be         */
    /* checked first: it must not be the source itself, nor the open          */
    /* session's file or disk -- each by identity, not by path, since one     */
    /* file has many spellings.  Only then is a file cut to nothing, on the   */
    /* same handle, so the file checked is the file cut.  From that point     */
    /* what was in an existing file is gone, whether or not the copy then     */
    /* succeeds (the program's save dialog asks before overwriting) --        */
    /* except in place, where nothing is cut.  The guards after it are a      */
    /* device's, and the room of a file written in place.                     */
    if (bInPlace)
    {   /* In place: the destination must already exist, file or device,    */
        /* and is opened as it stands -- BFV_ACCESS_WRITE never creates a   */
        /* file, and nothing below cuts it.                                 */
        iError = BFV_Open(pwszTo, BFV_ACCESS_WRITE, &copy.dest);
    }
    else
    {   /* A new copy: a file is created if missing, then cut below. */
        iError = BFI_OpenDestination(pwszTo, &copy.dest);
    }

    if (iError != 0)
    {   /* Cannot be created, in use, or a device that needs Administrator.   */
        /* bDeleteFile is not set yet, so nothing at that path is deleted:    */
        /* a file this open could not make is not this copy's to remove.      */
        return(BFI_Finish(&copy, BFX_Fail(BFE_ERR_IMAGE_OPEN, "cannot open the destination %ls (Windows error %d)", pwszTo, iError)));
    }

    if (!bZero && BFV_SameObject(&copy.source, &copy.dest))
    {   /* The destination IS the source, under another path: the copy       */
        /* would overwrite what it is reading.  Refused before anything is   */
        /* cut or written; bDeleteFile is not set, so the file stays.        */
        return(BFI_Finish(&copy, BFX_Fail(BFE_ERR_ARG, "the source and the destination are the same file or disk")));
    }

    if (BFD_TargetIsObject(&copy.dest))
    {   /* The open session's own image file or disk: writing it would   */
        /* change bytes lwext4 holds cached.  Refused untouched; the     */
        /* session must be closed first.                                 */
        return(BFI_Finish(&copy, BFX_Fail(BFE_ERR_ARG, "%ls is the image or device the open session is using; close it first", pwszTo)));
    }

    if (!bInPlace)
    {   /* A new copy: an existing file is cut to nothing now, on the        */
        /* handle just checked.  (In place it is written where it stands.)   */
        iError = BFV_Truncate(&copy.dest);

        if (iError != 0)
        {   /* A file that could not be cut to nothing: refused before       */
            /* writing.  bDeleteFile is not set, so the file is left as it   */
            /* was.                                                          */
            return(BFI_Finish(&copy, BFX_Fail(BFE_ERR_IO_HOST, "cannot empty the destination %ls (Windows error %d)", pwszTo, iError)));
        }
    }

    if (copy.bXzOut && copy.dest.bDevice)
    {   /* A device takes the image itself; compressing is for a file.    */
        /* Tested only now because only the open can tell a device path   */
        /* from a file.  The device is open but nothing is written or     */
        /* locked; BFI_Finish closes it.                                  */
        return(BFI_Finish(&copy, BFX_Fail(BFE_ERR_ARG, "%ls is a device; only a file can be written as .xz", pwszTo)));
    }

    /* The bytes the destination must hold: the length, rounded up to the     */
    /* destination's sector when a file source's tail is padded.  (With the   */
    /* size unknown there is no length yet; see the room check below.)        */
    ullNeeded = ullLength;

    if (copy.dest.bDevice)
    {   /* A device destination: every guard, in the order that costs least. */
        if ((ullToOffset % copy.dest.uiSector) != 0)
        {   /* The write would start inside a sector -- refused, since   */
            /* nothing here reads a sector to merge into it.             */
            return(BFI_Finish(&copy, BFX_Fail(BFE_ERR_ARG, "a device is written in whole sectors of %u bytes", copy.dest.uiSector)));
        }

        if ((ullNeeded % copy.dest.uiSector) != 0)
        {   /* The last partial sector is padded to a whole one -- only a      */
            /* FILE source can end off the grid (a device source was held to   */
            /* it above), and STEP 3 pads that tail with zeros.                */
            ullNeeded += copy.dest.uiSector - (ullNeeded % copy.dest.uiSector);
        }

        /* The system-disk guard, on the disk number of the open handle --    */
        /* before the lock, because locking the system disk's volumes would   */
        /* dismount the running Windows.                                      */
        uiSystem = BFV_SystemDisk();

        if (uiSystem == BFV_NO_DISK || copy.dest.uiDisk == BFV_NO_DISK || copy.dest.uiDisk == uiSystem)
        {   /* The disk Windows runs from, or one that cannot be told apart    */
            /* from it: never written, whatever the caller says.  No lock is   */
            /* taken yet; BFI_Finish closes both targets.                      */
            return(BFI_Finish(&copy, BFX_Fail(BFE_ERR_SYSTEM_DISK, "%ls is the disk Windows is running from, or cannot be told apart from it", pwszTo)));
        }

        if (ullToOffset > copy.dest.ullSize)
        {   /* The destination offset lies past the device's end: nothing of    */
            /* the image could be written there.  Refused before a write, and   */
            /* tested on its own, ahead of the room test below, so that test    */
            /* only ever subtracts an offset inside the device -- the room it   */
            /* reports is then a true figure -- and so the message names the    */
            /* real reason, the offset, rather than a room.  BFE_ERR_NO_SPACE   */
            /* still, as the contract gives every copy that would run off a     */
            /* device's end.                                                    */
            return(BFI_Finish(&copy, BFX_Fail(BFE_ERR_NO_SPACE, "the destination offset %llu is past the end of %ls (%llu bytes)",
                                              (unsigned long long)ullToOffset, pwszTo,
                                              (unsigned long long)copy.dest.ullSize)));
        }

        if (!bToEnd && ullNeeded > copy.dest.ullSize - ullToOffset)
        {   /* The image is larger than the room from that offset to the       */
            /* device's end: refused before a write.  (An image of unknown     */
            /* size is checked chunk by chunk instead.)  Written as a          */
            /* subtraction so the offset-plus-length sum is never formed and   */
            /* cannot wrap; the test above has held the offset inside the      */
            /* device, so the subtraction -- and the room the message          */
            /* reports -- cannot go below zero.                                */
            return(BFI_Finish(&copy, BFX_Fail(BFE_ERR_NO_SPACE, "%llu bytes do not fit on %ls (%llu bytes from that offset)",
                                              (unsigned long long)ullNeeded, pwszTo,
                                              (unsigned long long)(copy.dest.ullSize - ullToOffset))));
        }

        /* Last, the costliest guard: lock and dismount every volume on the   */
        /* disk for the whole copy (only the volumes on it are opened for     */
        /* writing).                                                          */
        iError = BFV_LockDisk(copy.dest.uiDisk, &copy.locks);

        if (iError != 0)
        {   /* The disk could not be locked -- a volume on it in use, or one   */
            /* whose disk could not be told.  BFV_LockDisk has already         */
            /* released the locks it took before the one that failed, so       */
            /* BFI_Finish has none to release; the lock's own sentence names   */
            /* the case, the volume and the Windows error, which tells "in     */
            /* use" from "access denied".                                      */
            return(BFI_Finish(&copy, BFX_Fail(BFE_ERR_VOLUME_LOCK, "%s", copy.locks.szFailure)));
        }
    }
    else if (bInPlace)
    {   /* A file written in place -- one partition of a disk image.  It is   */
        /* never extended, so the range must lie inside it: refused before    */
        /* a write, as a device that is too small is (tested as two parts     */
        /* so the sum is never formed).  An image of unknown size is held     */
        /* to the same room chunk by chunk in STEP 3.  bDeleteFile is never   */
        /* set: the file holds the rest of the disk, so a failure leaves it   */
        /* as written, exactly as a device is left.                           */
        if (ullToOffset > copy.dest.ullSize || (!bToEnd && ullNeeded > copy.dest.ullSize - ullToOffset))
        {   /* The range runs past the file's end. */
            return(BFI_Finish(&copy, BFX_Fail(BFE_ERR_NO_SPACE, "%llu bytes do not fit in %ls at byte %llu (the file is %llu bytes)",
                                              (unsigned long long)ullNeeded, pwszTo, (unsigned long long)ullToOffset,
                                              (unsigned long long)copy.dest.ullSize)));
        }
    }
    else
    {   /* A new file destination -- made, or emptied, by this copy --     */
        /* removed if it fails.                                            */
        /* Set only now, after the open succeeded -- from here on any      */
        /* failure, cancel or failed verify deletes it (BFI_Finish), and   */
        /* only a fully verified success (STEP 6) clears the flag.         */
        copy.bDeleteFile = true;

        if (copy.bXzOut)
        {   /* Written through the .xz encoder, from the file's start.           */
            /* ullToOffset is not used on this path, and not refused.  THE       */
            /* DEFECT EXISTS AND IS INTENTIONALLY DEFERRED: every raw copy the   */
            /* program asks for writes its file from offset 0.                   */
            iStatus = BFZ_WriterOpenXz(copy.dest.hFile, &copy.pWriter);

            if (iStatus != BFE_OK)
            {   /* The encoder would not start (memory; recorded by the    */
                /* writer).  pWriter is NULL; the empty file is deleted.   */
                return(BFI_Finish(&copy, iStatus));
            }
        }
    }

    /* The chunk buffers: one for the data, one more for the read-back.     */
    /* Page-aligned, because an unbuffered device read or write needs it;   */
    /* taken after the guards, so a refused copy never allocates 8 MiB.     */
    copy.pbRead = (uint8_t *)BFV_AllocAligned(BFI_CHUNK_BYTES);

    if ((uiFlags & BFE_RAW_VERIFY) != 0)
    {   /* The verify pass compares two chunks at once. */
        copy.pbBack = (uint8_t *)BFV_AllocAligned(BFI_CHUNK_BYTES);
    }

    /* Both are checked at once: BFI_Finish frees whichever did come. */
    if (copy.pbRead == NULL || ((uiFlags & BFE_RAW_VERIFY) != 0 && copy.pbBack == NULL))
    {   /* Out of memory for the buffers: nothing written yet; the locks   */
        /* (a device) are released and a new file deleted by BFI_Finish.   */
        return(BFI_Finish(&copy, BFX_Fail(BFE_ERR_NO_MEMORY, "out of memory")));
    }

    /* A compressed source starting at an offset: the decoder is moved there. */
    if (copy.pReader != NULL && ullFromOffset != 0)
    {   /* Decode and drop what comes before it. */
        iStatus = BFI_SkipImage(copy.pReader, copy.pbRead, ullFromOffset);

        if (iStatus != BFE_OK)
        {   /* Past the image's end, or damaged before it: recorded by   */
            /* BFI_SkipImage or the reader; nothing has been written.    */
            return(BFI_Finish(&copy, iStatus));
        }
    }

    /* STEP 3: the copy.  START names the length, or 0 when it is unknown   */
    /* -- 0 being the event's way of saying no total exists yet; the        */
    /* PROGRESS events then carry the compressed file's fraction instead.   */
    ullStartTotal = ullLength;

    if (bToEnd)
    {   /* The image's length is not known yet. */
        ullStartTotal = 0;
    }

    /* The copy phase's START event; the caller may cancel before a write. */
    if (BFI_Report(&copy, BFE_EVENT_START, BFI_LABEL_COPY, 0, ullStartTotal) == BFE_REPLY_CANCEL)
    {   /* Cancelled before it began: nothing has been written.  BFI_Finish   */
        /* releases the locks, so a card's volumes come back unchanged, and   */
        /* deletes a NEW destination file -- created or cut to nothing in     */
        /* STEP 2, so empty: what an existing file held is already gone.      */
        /* A file written in place is not deleted and is untouched.           */
        return(BFI_Finish(&copy, BFX_Fail(BFE_ERR_CANCELLED, "cancelled")));
    }

    /* Nothing copied, and the image not yet found to have ended. */
    ullDone = 0;
    bEnded  = false;

    /* To the length, or -- size unknown -- until the image itself ends. */
    while (!bEnded && (bToEnd || ullDone < ullLength))
    {   /* One chunk per iteration: read, pad if need be, write, report. */

        /* A chunk, or what is left of a known length if less. */
        cbPiece = BFI_CHUNK_BYTES;

        if (!bToEnd && ullLength - ullDone < cbPiece)
        {   /* Less than a chunk of the range is left. */
            cbPiece = ullLength - ullDone;
        }

        if (bZero)
        {   /* An erase: the "source" is a chunk of zeros, set each pass   */
            /* (the buffer is the one a copy reads into, so nothing else   */
            /* touches it between passes, but setting it here keeps the    */
            /* erase from depending on that).  Always the whole piece.     */
            memset(copy.pbRead, 0, (size_t)cbPiece);
            cbGot   = (uint32_t)cbPiece;
            iStatus = BFE_OK;
        }
        else
        {   /* The source's next piece of IMAGE bytes; only a decoder can   */
            /* come back short, and only at the image's end.                */
            iStatus = BFI_ReadSource(&copy, ullFromOffset + ullDone, copy.pbRead, (uint32_t)cbPiece, &cbGot);
        }

        if (iStatus != BFE_OK)
        {   /* The source failed part-way, or its compressed data is damaged.   */
            /* Already recorded.  What was written so far stays on a device     */
            /* and in a file written in place; a new destination file is        */
            /* deleted.  (An erase never comes here: its zeros cannot fail.)    */
            return(BFI_Finish(&copy, iStatus));
        }

        if (cbGot < cbPiece)
        {   /* The image ended inside this chunk. */
            if (!bToEnd)
            {   /* Before the length asked for: the length was wrong, not the image. */
                return(BFI_Finish(&copy, BFX_Fail(BFE_ERR_IO_IMAGE, "the image ends after %llu bytes, before the %llu asked for",
                                                  (unsigned long long)(ullFromOffset + ullDone + cbGot),
                                                  (unsigned long long)(ullFromOffset + ullLength))));
            }

            /* Running to the end: this is the last pass, carrying only what   */
            /* came.                                                           */
            bEnded  = true;
            cbPiece = cbGot;

            if (cbPiece == 0)
            {   /* It ended exactly on the last chunk: nothing more to write.   */
                /* (Leaving here, rather than writing 0 bytes, also keeps a     */
                /* zero-length write and a needless report out of the loop.)    */
                break;
            }
        }

        /* A device takes whole sectors: a file source's last partial chunk   */
        /* is padded with zeros up to the sector (the room was checked).      */
        /* An erase's zeros are padded the same way, so an erase whose        */
        /* length were not whole sectors would zero past its range, to the    */
        /* end of that sector: nothing here refuses such a length.  The       */
        /* program's erases never have one -- each is a partition's range,    */
        /* or from a whole sector to its end, in the device's own sector      */
        /* units -- and a caller that might would need the length refused.    */
        cbWrite = cbPiece;

        if (copy.dest.bDevice && (cbWrite % copy.dest.uiSector) != 0)
        {   /* Pad the tail.  Only a partial LAST piece reaches here (a whole   */
            /* chunk is whole sectors), so the padding fits in pbRead.          */
            memset(copy.pbRead + cbPiece, 0, (size_t)(copy.dest.uiSector - (cbWrite % copy.dest.uiSector)));
            cbWrite += copy.dest.uiSector - (cbWrite % copy.dest.uiSector);
        }

        /* The room test for an image of unknown size, written as a          */
        /* subtraction: ullToOffset was held inside the device above, and    */
        /* every earlier pass passed this test, so neither subtraction can   */
        /* go below zero.                                                    */
        if ((copy.dest.bDevice || bInPlace) && bToEnd && cbWrite > copy.dest.ullSize - ullToOffset - ullDone)
        {   /* An image of unknown size has outgrown the device (or the file   */
            /* written in place, which is never extended): stop BEFORE the     */
            /* chunk that would not fit (what was written stays written: the   */
            /* destination holds a partial image, which the message says).     */
            return(BFI_Finish(&copy, BFX_Fail(BFE_ERR_NO_SPACE, "the image is larger than %ls: it does not fit after %llu bytes",
                                              pwszTo, (unsigned long long)ullDone)));
        }

        if (copy.pWriter != NULL)
        {   /* .xz: through the encoder, which writes the file as its output    */
            /* fills.  cbPiece, not cbWrite: a file is never padded, and the    */
            /* encoder copies the bytes, so pbRead is free for the next read.   */
            iStatus = BFZ_WriterWrite(copy.pWriter, copy.pbRead, (uint32_t)cbPiece);

            if (iStatus != BFE_OK)
            {   /* The encoder failed or the disk refused; the text says which.   */
                /* BFI_Finish closes the writer, dropping its output, and         */
                /* deletes the incomplete file.                                   */
                return(BFI_Finish(&copy, iStatus));
            }
        }
        else
        {   /* The bytes themselves, at the same distance into the destination   */
            /* range as into the source's.                                       */
            iError = BFV_Write(&copy.dest, ullToOffset + ullDone, copy.pbRead, (uint32_t)cbWrite);

            if (iError != 0)
            {   /* The destination refused: full, failed, or pulled out.  The    */
                /* byte is this chunk's start; part of the chunk may be on the   */
                /* media.  A device, or a file written in place, keeps what      */
                /* was written; a new file is deleted.                           */
                return(BFI_Finish(&copy, BFX_Fail(BFE_ERR_IO_HOST, "writing the destination failed at byte %llu (Windows error %d)",
                                                  (unsigned long long)(ullToOffset + ullDone), iError)));
            }
        }

        /* Count the IMAGE bytes copied (padding is not image), then report   */
        /* progress; the caller may cancel.                                   */
        ullDone += cbPiece;

        if (bToEnd)
        {   /* Size unknown: the one known fraction is the compressed file's.   */
            /* bToEnd is only ever set for a decoded source, so pReader is      */
            /* there to ask.                                                    */
            iStatus = BFI_Report(&copy, BFE_EVENT_PROGRESS, BFI_LABEL_COPY,
                                 BFZ_ReaderConsumed(copy.pReader), BFZ_ReaderFileSize(copy.pReader));
        }
        else
        {   /* The image's bytes against its length. */
            iStatus = BFI_Report(&copy, BFE_EVENT_PROGRESS, BFI_LABEL_COPY, ullDone, ullLength);
        }

        if (iStatus == BFE_REPLY_CANCEL)
        {   /* The caller stopped it; a new file destination is removed.  A    */
            /* device, or a file written in place, keeps the part already      */
            /* written -- a half-written card or partition, which the cancel   */
            /* was the user's choice to make.                                  */
            return(BFI_Finish(&copy, BFX_Fail(BFE_ERR_CANCELLED, "cancelled")));
        }
    }

    /* What was copied is the length from here on: the DONE event and the   */
    /* verify pass use it (it differs from the argument only when the       */
    /* image's size was unknown).                                           */
    ullLength = ullDone;

    if (copy.pWriter != NULL)
    {   /* The .xz's index and footer: without them the file is not .xz. */
        iStatus = BFZ_WriterFinish(copy.pWriter);

        if (iStatus != BFE_OK)
        {   /* The last of it could not be written (recorded by the writer);   */
            /* the file is not a valid .xz and is deleted by BFI_Finish.       */
            return(BFI_Finish(&copy, iStatus));
        }
    }

    /* STEP 4: on the media before anyone is told it is done -- for a file,   */
    /* out of Windows' cache too, the .xz's last bytes included.              */
    iError = BFV_Flush(&copy.dest);

    if (iError != 0)
    {   /* The device did not confirm the writes: a failure, not a warning,   */
        /* since what is on the media is then unknown.  A new file is         */
        /* deleted; a device, or a file written in place, keeps whatever      */
        /* did reach it.                                                      */
        return(BFI_Finish(&copy, BFX_Fail(BFE_ERR_IO_HOST, "the destination did not confirm the write (Windows error %d)", iError)));
    }

    /* The copy phase's DONE event (its reply is not looked at: the copy is   */
    /* complete, and a verify that follows asks again at its own START).      */
    BFI_Report(&copy, BFE_EVENT_DONE, BFI_LABEL_COPY, ullLength, ullLength);

    /* STEP 5: the read-back, when asked. */
    if ((uiFlags & BFE_RAW_VERIFY) != 0)
    {   /* Compare every byte written with the source. */
        iStatus = BFI_Verify(&copy, ullFromOffset, ullToOffset, ullLength);

        if (iStatus != BFE_OK)
        {   /* Differs, failed or cancelled; the detail line is written.  A   */
            /* new destination file is deleted even though every byte was     */
            /* written: bDeleteFile is still set, and a file that did not     */
            /* verify is not kept.  A device, or a file written in place      */
            /* (bDeleteFile never set), keeps what was written.               */
            return(BFI_Finish(&copy, iStatus));
        }
    }

    /* STEP 6: the destination file is complete and stays; a device gets its     */
    /* partition table re-read by Windows (BFV_RefreshDisk does nothing for a    */
    /* file).  Done while the volumes are still locked; BFI_Finish then closes   */
    /* the device before releasing them.                                         */
    copy.bDeleteFile = false;
    BFV_RefreshDisk(&copy.dest);

    /* Everything was copied (and verified, if asked). */
    return(BFI_Finish(&copy, BFE_OK));
}

/*----------------------------------------------------------------------------*/
/* BFE_ImageInfoGet:                                                          */
/*                                                                            */
/* Say what an image file is before anything is done with it: plain, or one   */
/* of the compressed formats, and how large the image inside it is.  This is  */
/* what lets the GUI size a "write only the first N bytes" choice and word    */
/* its warning by the IMAGE rather than by a compressed file's length, and    */
/* catch a .xz cut short before a card is touched.  Only headers and indexes  */
/* are read -- the zip directory, the .xz index, the .zst frame headers --    */
/* never the data, so it is quick on any size.  A device is plain, at its     */
/* own length (it is opened for its facts only).                              */
/*                                                                            */
/* Arguments:                                                                 */
/*     pwszPath : the image file (or device) path.                            */
/*     pInfo    : receives the facts; zeroed first.                           */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_ARG for a missing argument; BFE_ERR_IMAGE_OPEN   */
/*           when the path cannot be opened; BFE_ERR_IO_IMAGE for a file      */
/*           that cannot be read, a .xz whose index cannot, or a damaged zip  */
/*           directory; BFE_ERR_UNSUPPORTED for a zip compressed with another */
/*           method or a .xz with headers this liblzma does not know;         */
/*           BFE_ERR_NO_MEMORY; BFE_ERR_BUSY.                                 */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_ImageInfoGet(const wchar_t *pwszPath, BFE_ImageInfo *pInfo)
{
    BFV_Target  target;
    BFZ_Reader *pReader;
    int         iStatus;
    int         iError;

    if (pInfo == NULL)
    {   /* Nowhere to put the answer: refused before the gate, so no error   */
        /* text is recorded for it -- the text may only be written while     */
        /* the busy flag is held (BFX_Fail's rule), and it is not yet.       */
        return(BFE_ERR_ARG);
    }

    /* No facts until they are found (0 is BFE_IMAGE_RAW and size 0) -- so   */
    /* every failure below also leaves this zeroed.                          */
    memset(pInfo, 0, sizeof(*pInfo));

    /* The gate: the busy flag only -- an image may be open or not, and   */
    /* nothing is written.                                                */
    iStatus = BFX_Enter(false, false);

    if (iStatus != BFE_OK)
    {   /* Another call is running: BFX_Enter has said so, and the flag is   */
        /* not ours, so there is nothing to release.                         */
        return(iStatus);
    }

    if (pwszPath == NULL || pwszPath[0] == L'\0')
    {   /* No path given: refuse, and let the next call in.  Only the flag   */
        /* is held; the text is recorded first.                              */
        iStatus = BFX_Fail(BFE_ERR_ARG, "no path given");
        BFX_Leave();
        return(iStatus);
    }

    /* The facts first: a device is answered from them, without Administrator.   */
    /* A READ open of a device would need Administrator, so asking for the       */
    /* facts first is what lets the program size a card from an ordinary         */
    /* account.                                                                  */
    iError = BFV_Open(pwszPath, BFV_ACCESS_QUERY, &target);

    if (iError != 0)
    {   /* Nothing of that name, or it cannot be opened even for its facts. */

        /* Refuse with the Windows error, and let the next call in.  The   */
        /* query opened nothing, so the flag is all there is to release.   */
        iStatus = BFX_Fail(BFE_ERR_IMAGE_OPEN, "cannot open %ls (Windows error %d)", pwszPath, iError);
        BFX_Leave();
        return(iStatus);
    }

    if (target.bDevice)
    {   /* A device holds a plain image, at its own length.  Its bytes are   */
        /* not looked at: a device is never decompressed (BFE_CopyRaw        */
        /* decodes a FILE source only), so whatever it holds is "plain".     */
        pInfo->uiKind        = BFE_IMAGE_RAW;
        pInfo->ullImageBytes = target.ullSize;
        pInfo->ullFileBytes  = target.ullSize;

        /* The query handed the target back closed; let the next call in. */
        BFX_Leave();

        /* The facts are filled in. */
        return(BFE_OK);
    }

    /* A file: opened for reading, and its first bytes, directory or index read. */
    iError = BFV_Open(pwszPath, BFV_ACCESS_READ, &target);

    if (iError != 0)
    {   /* In use, or no permission to read it. */

        /* Refuse with the Windows error, and let the next call in.  The   */
        /* open failed, so nothing is held but the flag.                   */
        iStatus = BFX_Fail(BFE_ERR_IMAGE_OPEN, "cannot open %ls for reading (Windows error %d)", pwszPath, iError);
        BFX_Leave();
        return(iStatus);
    }

    /* The reader's open does all the work: it tells the format and learns   */
    /* the image's size from the headers alone.  Its decoder is never run,   */
    /* so a .xz cut short is caught (its index is gone) but damage inside    */
    /* the data is not -- only a copy or a verify would find that.           */
    iStatus = BFZ_ReaderOpen(target.hFile, &pReader);

    if (iStatus == BFE_OK)
    {   /* What it is, and how large the image inside it.  The kind is    */
        /* passed out as the public BFE_IMAGE_* value -- bfe_compress.h   */
        /* keeps the two equal.                                           */
        pInfo->uiKind        = (uint32_t)BFZ_ReaderKind(pReader);
        pInfo->ullImageBytes = BFZ_ReaderSize(pReader);
        pInfo->ullFileBytes  = BFZ_ReaderFileSize(pReader);
        BFZ_ReaderClose(pReader);
    }

    /* Close the file (a failed reader open has freed itself, and recorded   */
    /* its failure while the flag was held), and let the next call in --     */
    /* the same two steps whether the reader opened or not.                  */
    BFV_Close(&target);
    BFX_Leave();

    /* BFE_OK, or why the file could not be read (the text is set). */
    return(iStatus);
}
