/*----------------------------------------------------------------------------*/
/* bfe_blockdev.c                                                             */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The engine's own lwext4 block device (see bfe_blockdev.h for why it        */
/* exists).  lwext4 drives a device through four functions -- open, read      */
/* blocks, write blocks, close -- and addresses it in PHYSICAL blocks of      */
/* ph_bsize bytes counted from the start of the whole thing: it adds the      */
/* partition offset itself before calling here, so these functions never      */
/* apply it.                                                                  */
/*                                                                            */
/* The thing underneath is a BFV_Target (bfe_device.c): an image FILE or a    */
/* real DEVICE, opened the same way.  To lwext4 both look alike -- 512-byte   */
/* physical blocks, because that is a disk image's convention and every       */
/* partition table counts in them -- and this file is where the difference    */
/* is absorbed.  A file takes a transfer of any shape, so lwext4's buffer is  */
/* handed straight through.  A device takes only whole real sectors (512 or   */
/* 4096 bytes) at sector boundaries, into a page-aligned buffer; so a device  */
/* transfer goes through an aligned BOUNCE buffer, widened at each end to the */
/* device's sector, and a device WRITE whose ends do not fall on real sectors */
/* reads those sectors first and writes them back with lwext4's bytes laid    */
/* over the middle (a read-modify-write).  That case arises on a 4096-byte-   */
/* sector disk whose partition starts at an old-style 512-byte boundary.      */
/*                                                                            */
/* No buffering anywhere: lwext4 has its own block cache, and a second one    */
/* would only delay the moment a write reaches the image or the card.         */
/*----------------------------------------------------------------------------*/

/* lwext4: its build configuration, the block-device interface this file   */
/* implements, and its errno values (EOK, EIO, ...).                       */
#include <ext4_config.h>
#include <ext4_blockdev.h>
#include <ext4_errno.h>

/* The C library: the failure line's formatting, memory, strings, paths. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

/* This file's own interface, and the raw file-or-device layer beneath it   */
/* (which also brings windows.h: HANDLE, INVALID_HANDLE_VALUE, ERROR_*).    */
#include "bfe_blockdev.h"
#include "bfe_device.h"

/*----------------------------------------------------------------------------*/
/* Definitions -- documentation table.                                        */
/*----------------------------------------------------------------------------*/
#define BFD_SECTOR_BYTES   512              // physical block size presented to lwext4; a disk image's sector
#define BFD_BOUNCE_BYTES   1048576          // the aligned bounce buffer for device I/O, bytes (1 MiB: a whole number of any real sector)
#define BFD_ERROR_NOT_KEPT ERROR_CRC        // what BFD_TargetWrite returns when a device write read back different: Windows' "data error", the nearest of its codes; BFD_szLastIo carries the real words

/*----------------------------------------------------------------------------*/
/* The device's configuration and its open target.  Set by BFD_Configure,     */
/* used by the four interface functions.  The engine serialises every call,   */
/* so nothing here needs a lock.                                              */
/*                                                                            */
/* BFD_target's initialiser lists BFV_Target's nine fields IN ORDER           */
/* (bfe_device.h): handle, size, sector, disk, bus, the four flags, model.    */
/* A field added or moved there must be matched here, or the "closed" state   */
/* the program starts in is wrong.                                            */
/*----------------------------------------------------------------------------*/
static wchar_t    *BFD_pwszImage;                                                             // malloc'd copy of the image or device path; NULL until configured
static uint64_t    BFD_ullOffset;                                                             // byte offset of the filesystem inside the file or device
static uint64_t    BFD_ullLength;                                                             // byte length of the filesystem; 0 means "to the end"
static int         BFD_bReadOnly;                                                             // TRUE: open read-only and refuse every write
static BFV_Target  BFD_target = { INVALID_HANDLE_VALUE, 0, 0, BFV_NO_DISK, 0, 0, 0, 0, "" };  // the open file or device; its hFile is INVALID_HANDLE_VALUE while closed
static uint8_t    *BFD_pbBounce;                                                              // the page-aligned bounce buffer, allocated while a DEVICE is open; NULL otherwise
static uint8_t    *BFD_pbVerify;                                                              // receives a device write's read-back (BFD_BOUNCE_BYTES), allocated while a DEVICE is open writable; NULL otherwise
static int         BFD_bVerify;                                                               // TRUE: read every device write back (BFD_SetVerify); cleared by BFD_Configure, so each operation decides
static int         BFD_bNotKept;                                                              // TRUE once a device write in this operation read back different; cleared by BFD_Configure, read by BFD_WriteNotKept
static uint64_t    BFD_ullNotKeptAt;                                                          // the byte offset of the first write that read back different (meaningful while BFD_bNotKept)
static uint32_t    BFD_uiGeneration;                                                          // how many configurations BFD_Configure has recorded (0 at load, wrapping); read by BFD_Generation
static char        BFD_szLastIo[256];                                                         // one line about the last failed device-level open, read or write; "" when none since it was taken

/*----------------------------------------------------------------------------*/
/* BFD_NoteIo:                                                                */
/*                                                                            */
/* Record one device-level failure in the words the engine's detail line      */
/* will carry: what was being done, where, how much, and the Windows error.   */
/* lwext4 collapses every failure from here to "I/O error", so this is the    */
/* only place the real reason survives.                                       */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszWhat   : "open", "read" or "write".                                 */
/*     ullOffset : the byte offset of the transfer (0 for an open).           */
/*     cbBytes   : its length (0 for an open).                                */
/*     iError    : the Windows error code.                                    */
/*                                                                            */
/* Returns:                                                                   */
/*     void : BFD_szLastIo holds the line.                                    */
/*----------------------------------------------------------------------------*/
static void BFD_NoteIo(const char *pszWhat, uint64_t ullOffset, uint64_t cbBytes, int iError)
{
    /* One line, replacing any earlier one: the operation, where, how much,   */
    /* the Windows error, and the target's sector size and kind (device 1,    */
    /* file 0) -- the two facts that decide how a transfer is shaped.         */
    snprintf(BFD_szLastIo, sizeof(BFD_szLastIo), "device %s at %llu for %llu bytes: Windows error %d (sector %u, device %d)",
             pszWhat, (unsigned long long)ullOffset, (unsigned long long)cbBytes, iError,
             (unsigned)BFD_target.uiSector, BFD_target.bDevice);

    /* snprintf terminates within the size given; said explicitly. */
    BFD_szLastIo[sizeof(BFD_szLastIo) - 1] = '\0';
}

/*----------------------------------------------------------------------------*/
/* BFD_LastIoText:                                                            */
/*                                                                            */
/* Hand out the recorded line WITHOUT forgetting it.  Reading and clearing    */
/* are two functions so a caller can look first and decide; the engine's      */
/* error recorder (BFX_Fail) reads, appends, then clears it, and every public */
/* call's gate (BFX_Enter) clears it once the call owns the engine -- so the  */
/* line a failure reports is always its own call's.                           */
/*                                                                            */
/* Arguments:                                                                 */
/*     None.                                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     const char * : the line, or "" when none is held; this file's buffer,  */
/*                    not to be freed.                                        */
/*----------------------------------------------------------------------------*/
const char *BFD_LastIoText(void)
{
    /* Empty until a failure is noted. */
    return(BFD_szLastIo);
}

/*----------------------------------------------------------------------------*/
/* BFD_ClearIoText:                                                           */
/*                                                                            */
/* Forget the recorded line, once it has been taken into a detail line.       */
/*                                                                            */
/* Arguments:                                                                 */
/*     None.                                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     void : BFD_LastIoText answers "" until the next failure.               */
/*----------------------------------------------------------------------------*/
void BFD_ClearIoText(void)
{
    /* An empty string is "nothing held". */
    BFD_szLastIo[0] = '\0';
}

/*----------------------------------------------------------------------------*/
/* The four interface functions are defined below; lwext4's instance macro    */
/* needs their names first.  Each is described at its definition.             */
/*----------------------------------------------------------------------------*/
static int BFD_DevOpen(struct ext4_blockdev *pDev);
static int BFD_DevRead(struct ext4_blockdev *pDev, void *pvBuf, uint64_t ullBlock, uint32_t uiCount);
static int BFD_DevWrite(struct ext4_blockdev *pDev, const void *pvBuf, uint64_t ullBlock, uint32_t uiCount);
static int BFD_DevClose(struct ext4_blockdev *pDev);

/*----------------------------------------------------------------------------*/
/* The one static device instance: 512-byte physical blocks, a block count    */
/* filled in at open, no lock functions (the engine serialises its calls).    */
/* The macro's arguments, in order: the instance's name, the physical block   */
/* size, the block count (0 here), the open / read / write / close hooks, and */
/* the lock / unlock hooks (none).                                            */
/*----------------------------------------------------------------------------*/
EXT4_BLOCKDEV_STATIC_INSTANCE(BFD_device, BFD_SECTOR_BYTES, 0, BFD_DevOpen,
                              BFD_DevRead, BFD_DevWrite, BFD_DevClose, 0, 0);

/*----------------------------------------------------------------------------*/
/* BFD_TargetOpen:                                                            */
/*                                                                            */
/* Open the configured target and hand back the filesystem range's length,    */
/* refusing a range that does not fit inside it.  Both users of the target -- */
/* lwext4's hook below and the filesystem check's io manager -- open through  */
/* here, so the rules are decided once.                                       */
/*                                                                            */
/* Arguments:                                                                 */
/*     pullLength : receives the filesystem range's length in bytes: the      */
/*                  configured length, or everything after the offset when    */
/*                  none was configured.                                      */
/*                                                                            */
/* Returns:                                                                   */
/*     int : EOK when the target is open and the range lies inside it; EIO    */
/*           when it cannot be opened; ENOMEM when the bounce buffer cannot   */
/*           be allocated; EINVAL when the configured range runs past the     */
/*           end, or the target is not a whole number of 512-byte blocks.     */
/*----------------------------------------------------------------------------*/
int BFD_TargetOpen(uint64_t *pullLength)
{
    uint64_t ullSize;
    uint64_t ullLength;
    int      iAccess;
    int      iError;

    if (BFD_pwszImage == NULL)
    {   /* BFD_Configure was never called; there is nothing to open. */
        return(EIO);
    }

    /* The access asked of Windows follows the session's mode. */
    if (BFD_bReadOnly)
    {   /* A read-only session asks for read access only, so an image the   */
        /* user cannot write -- a read-only file, a write-protected card    */
        /* -- can still be browsed and extracted from.                      */
        iAccess = BFV_ACCESS_READ;
    }
    else
    {   /* A writable session reads and writes what is there. */
        iAccess = BFV_ACCESS_WRITE;
    }

    /* Open it; BFV_Open fills in what it is (file or device, size, sector). */
    iError = BFV_Open(BFD_pwszImage, iAccess, &BFD_target);

    if (iError != 0)
    {   /* Missing, locked by another program, no permission, or a device   */
        /* that needs Administrator.                                        */

        /* Keep the Windows error for the detail line, and fail with the   */
        /* one code lwext4 understands for it.                             */
        BFD_NoteIo("open", 0, 0, iError);
        return(EIO);
    }

    /* The range checks below are made against the whole target's length.   */
    /* None of their refusals is noted for BFD_LastIoText: they are not     */
    /* device failures, and the caller reports them as its own (EINVAL).    */
    ullSize = BFD_target.ullSize;

    if ((ullSize % BFD_SECTOR_BYTES) != 0)
    {   /* A tail shorter than one block cannot be addressed by lwext4; an   */
        /* image file of odd length is not an image.                         */

        /* Close what was just opened and refuse. */
        BFV_Close(&BFD_target);
        return(EINVAL);
    }

    /* A length of 0 means "from the offset to the end". */
    ullLength = BFD_ullLength;

    if (BFD_ullOffset > ullSize)
    {   /* The partition would start beyond the end. */

        /* Close what was just opened and refuse. */
        BFV_Close(&BFD_target);
        return(EINVAL);
    }

    if (ullLength == 0)
    {   /* No explicit length: the filesystem runs to the end. */
        ullLength = ullSize - BFD_ullOffset;
    }

    if (ullLength > ullSize - BFD_ullOffset)
    {   /* The partition would run past the end. */

        /* Close what was just opened and refuse. */
        BFV_Close(&BFD_target);
        return(EINVAL);
    }

    if (BFD_target.bDevice)
    {   /* A device: every transfer goes through the aligned bounce buffer,   */
        /* page-aligned because unbuffered device I/O requires it.            */
        BFD_pbBounce = (uint8_t *)BFV_AllocAligned(BFD_BOUNCE_BYTES);

        if (BFD_pbBounce == NULL)
        {   /* Out of memory for the buffer. */

            /* Close the device again and fail: it cannot be used without   */
            /* the bounce.                                                  */
            BFV_Close(&BFD_target);
            return(ENOMEM);
        }

        if (!BFD_bReadOnly)
        {   /* A writable device: every write is read back (BFD_VerifyWrite),   */
            /* into a buffer of its own because the read goes through the       */
            /* bounce.                                                          */
            BFD_pbVerify = (uint8_t *)malloc(BFD_BOUNCE_BYTES);

            if (BFD_pbVerify == NULL)
            {   /* Out of memory for the read-back buffer. */

                /* Give back the bounce just allocated (and forget it, so   */
                /* a later close does not free it twice), close the         */
                /* device, and fail.                                        */
                BFV_FreeAligned(BFD_pbBounce);
                BFD_pbBounce = NULL;
                BFV_Close(&BFD_target);
                return(ENOMEM);
            }
        }
    }

    /* The target is open and the range is good. */
    *pullLength = ullLength;
    return(EOK);
}

/*----------------------------------------------------------------------------*/
/* BFD_DevOpen:                                                               */
/*                                                                            */
/* lwext4's "open device" hook: open the target (above) and tell lwext4 which */
/* byte range of it is the filesystem.                                        */
/*                                                                            */
/* Arguments:                                                                 */
/*     pDev : the device being opened (always BFD_device).                    */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFD_TargetOpen's result.                                         */
/*----------------------------------------------------------------------------*/
static int BFD_DevOpen(struct ext4_blockdev *pDev)
{
    uint64_t ullLength;
    int      iError;

    iError = BFD_TargetOpen(&ullLength);

    if (iError != EOK)
    {   /* Not open: the reason is in the code, and in BFD_LastIoText for an   */
        /* open the OS refused.                                                */
        return(iError);
    }

    /* Hand lwext4 the range.  ph_bcnt is the whole target in 512-byte        */
    /* blocks, because lwext4 addresses physical blocks from its start and    */
    /* places the partition with part_offset / part_size.  The same two       */
    /* fields are what BFD_DevRead and BFD_DevWrite hold every request to     */
    /* (BFD_InRange), so whatever lwext4 path a transfer comes by, it stays   */
    /* inside this range.                                                     */
    pDev->part_offset     = BFD_ullOffset;
    pDev->part_size       = ullLength;
    pDev->bdif->ph_bcnt   = BFD_target.ullSize / pDev->bdif->ph_bsize;

    /* The target is open and its range is set. */
    return(EOK);
}

/*----------------------------------------------------------------------------*/
/* BFD_DeviceRead:                                                            */
/*                                                                            */
/* Read a byte range from an open DEVICE into lwext4's buffer, through the    */
/* bounce buffer, whatever the range's alignment to the device's sector.      */
/*                                                                            */
/* Arguments:                                                                 */
/*     ullOffset : the first byte wanted, from the start of the device.       */
/*     pbOut     : receives cbBytes bytes.                                    */
/*     cbBytes   : how many.                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0 when every byte was read; otherwise the Windows error.         */
/*----------------------------------------------------------------------------*/
static int BFD_DeviceRead(uint64_t ullOffset, uint8_t *pbOut, uint64_t cbBytes)
{
    uint64_t ullPos;
    uint64_t ullStart;
    uint64_t ullEnd;
    uint64_t cbLeft;
    uint64_t cbPiece;
    uint32_t uiSector;
    int      iError;

    /* The device's real sector, and a cursor over the range wanted. */
    uiSector = BFD_target.uiSector;
    ullPos   = ullOffset;
    cbLeft   = cbBytes;

    while (cbLeft > 0)
    {   /* One bounce-sized aligned read per iteration. */

        /* The piece: as much as leaves room for the widening at both ends.    */
        /* Each end widens by less than one sector, so a piece of the bounce   */
        /* less two sectors always fits once widened.                          */
        cbPiece = cbLeft;

        if (cbPiece > BFD_BOUNCE_BYTES - (2 * uiSector))
        {   /* More than one pass can hold. */
            cbPiece = BFD_BOUNCE_BYTES - (2 * uiSector);
        }

        /* Widen to whole sectors: down at the start, up at the end. */
        ullStart = ullPos - (ullPos % uiSector);
        ullEnd   = ullPos + cbPiece;

        if ((ullEnd % uiSector) != 0)
        {   /* The end is inside a sector: take the whole sector. */
            ullEnd += uiSector - (ullEnd % uiSector);
        }

        if (ullEnd > BFD_target.ullSize)
        {   /* The widened end runs off the device -- the device's tail is   */
            /* shorter than a sector, which a real disk never is.            */
            return(ERROR_HANDLE_EOF);
        }

        /* Read the widened, aligned range into the bounce (at most 1 MiB,   */
        /* so the 32-bit length cannot overflow).                            */
        iError = BFV_Read(&BFD_target, ullStart, BFD_pbBounce, (uint32_t)(ullEnd - ullStart));

        if (iError != 0)
        {   /* The media failed or the device went away. */
            return(iError);
        }

        /* The wanted bytes sit at the offset the widening pushed them to. */
        memcpy(pbOut, BFD_pbBounce + (ullPos - ullStart), (size_t)cbPiece);

        /* Advance past the piece. */
        pbOut  += cbPiece;
        ullPos += cbPiece;
        cbLeft -= cbPiece;
    }

    /* Every byte was read. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFD_DeviceWrite:                                                           */
/*                                                                            */
/* Write a byte range to an open DEVICE from lwext4's buffer, through the     */
/* bounce buffer, whatever the range's alignment to the device's sector --    */
/* the bytes around a ragged edge are preserved.                              */
/*                                                                            */
/* The read-modify-write is not atomic: a failure between the read and the    */
/* write leaves the device as it was, but a write that fails partway may      */
/* leave the edge sectors part old, part new, as any interrupted write can.   */
/* A failure stops at that pass; earlier passes are already written.          */
/*                                                                            */
/* Arguments:                                                                 */
/*     ullOffset : the first byte to write, from the start of the device.     */
/*     pbIn      : the cbBytes bytes.                                         */
/*     cbBytes   : how many.                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0 when every byte was written; otherwise the Windows error.      */
/*----------------------------------------------------------------------------*/
static int BFD_DeviceWrite(uint64_t ullOffset, const uint8_t *pbIn, uint64_t cbBytes)
{
    uint64_t ullPos;
    uint64_t ullStart;
    uint64_t ullEnd;
    uint64_t cbLeft;
    uint64_t cbPiece;
    uint32_t uiSector;
    int      bRagged;
    int      iError;

    /* The device's real sector, and a cursor over the range to write. */
    uiSector = BFD_target.uiSector;
    ullPos   = ullOffset;
    cbLeft   = cbBytes;

    while (cbLeft > 0)
    {   /* One bounce-sized aligned write per iteration. */

        /* The piece: the bounce less two sectors, room for the widening. */
        cbPiece = cbLeft;

        if (cbPiece > BFD_BOUNCE_BYTES - (2 * uiSector))
        {   /* More than one pass can hold. */
            cbPiece = BFD_BOUNCE_BYTES - (2 * uiSector);
        }

        /* Widen to whole sectors, noting whether either end had to move:    */
        /* a moved end shares its sector with bytes lwext4 is not writing.   */
        ullStart = ullPos - (ullPos % uiSector);
        ullEnd   = ullPos + cbPiece;
        bRagged  = (ullStart != ullPos);

        if ((ullEnd % uiSector) != 0)
        {   /* The end is inside a sector. */
            ullEnd += uiSector - (ullEnd % uiSector);
            bRagged = 1;
        }

        if (ullEnd > BFD_target.ullSize)
        {   /* The widened end runs off the device. */
            return(ERROR_HANDLE_EOF);
        }

        if (bRagged)
        {   /* An edge sector is shared with bytes lwext4 is not writing:   */
            /* read the aligned range first so they survive the write.      */
            iError = BFV_Read(&BFD_target, ullStart, BFD_pbBounce, (uint32_t)(ullEnd - ullStart));

            if (iError != 0)
            {   /* The read-before-write failed: nothing is written. */
                return(iError);
            }
        }

        /* lwext4's bytes over the middle -- over everything when aligned,   */
        /* the normal case, every lwext4 block being a multiple of 512 on    */
        /* a 512-byte disk.                                                  */
        memcpy(BFD_pbBounce + (ullPos - ullStart), pbIn, (size_t)cbPiece);

        /* The whole aligned range goes out in one write. */
        iError = BFV_Write(&BFD_target, ullStart, BFD_pbBounce, (uint32_t)(ullEnd - ullStart));

        if (iError != 0)
        {   /* The media refused. */
            return(iError);
        }

        /* Advance past the piece. */
        pbIn   += cbPiece;
        ullPos += cbPiece;
        cbLeft -= cbPiece;
    }

    /* Every byte was written. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFD_FileTransfer:                                                          */
/*                                                                            */
/* Read or write a byte range on an open FILE straight from or into lwext4's  */
/* buffer -- a file has no alignment rule, so no bounce is needed.            */
/*                                                                            */
/* Arguments:                                                                 */
/*     ullOffset : the first byte, from the start of the file.                */
/*     pbBuf     : lwext4's buffer.                                           */
/*     cbBytes   : how many bytes.                                            */
/*     bWrite    : TRUE to write from the buffer, FALSE to read into it.      */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0 when every byte was transferred; otherwise the Windows error.  */
/*----------------------------------------------------------------------------*/
static int BFD_FileTransfer(uint64_t ullOffset, uint8_t *pbBuf, uint64_t cbBytes, int bWrite)
{
    uint64_t cbLeft;
    uint64_t cbPiece;
    int      iError;

    /* Everything is still to do. */
    cbLeft = cbBytes;

    while (cbLeft > 0)
    {   /* One piece per iteration. */
        cbPiece = cbLeft;

        if (cbPiece > BFD_BOUNCE_BYTES)
        {   /* Keep each call to a size the Win32 calls' 32-bit count can     */
            /* carry, however many blocks lwext4 asked for (the bounce size   */
            /* serves as the piece size, though no bounce is used here).      */
            cbPiece = BFD_BOUNCE_BYTES;
        }

        if (bWrite)
        {   /* Into the file. */
            iError = BFV_Write(&BFD_target, ullOffset, pbBuf, (uint32_t)cbPiece);
        }
        else
        {   /* Out of the file. */
            iError = BFV_Read(&BFD_target, ullOffset, pbBuf, (uint32_t)cbPiece);
        }

        if (iError != 0)
        {   /* Short, full, or failed. */
            return(iError);
        }

        /* Advance past the piece. */
        pbBuf     += cbPiece;
        ullOffset += cbPiece;
        cbLeft    -= cbPiece;
    }

    /* Every byte was transferred. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFD_TargetRead:                                                            */
/*                                                                            */
/* Read a byte range of the open target, counted from its start, a device or  */
/* a file alike.  A failure is noted for BFD_LastIoText, because lwext4       */
/* passes only "I/O error" up and e2fsck prints only its own code.            */
/*                                                                            */
/* Arguments:                                                                 */
/*     ullOffset : the first byte, from the start of the file or device.      */
/*     pvBuf     : receives cbBytes bytes.                                    */
/*     cbBytes   : how many; 0 is a legal no-op.                              */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0 when every byte was read; ERROR_INVALID_HANDLE when nothing is */
/*           open; otherwise the Windows error, ERROR_HANDLE_EOF for a read   */
/*           past the end.                                                    */
/*----------------------------------------------------------------------------*/
int BFD_TargetRead(uint64_t ullOffset, void *pvBuf, uint64_t cbBytes)
{
    int iError;

    if (BFD_target.hFile == INVALID_HANDLE_VALUE)
    {   /* Nothing is open. */
        return(ERROR_INVALID_HANDLE);
    }

    if (cbBytes == 0)
    {   /* Nothing to transfer. */
        return(0);
    }

    if (BFD_target.bDevice)
    {   /* A real device: through the aligned bounce. */
        iError = BFD_DeviceRead(ullOffset, (uint8_t *)pvBuf, cbBytes);
    }
    else
    {   /* A file: straight in. */
        iError = BFD_FileTransfer(ullOffset, (uint8_t *)pvBuf, cbBytes, 0);
    }

    if (iError != 0)
    {   /* A short read (the image is truncated) or the media failed. */

        /* Keep it, in the device's own terms, for the detail line. */
        BFD_NoteIo("read", ullOffset, cbBytes, iError);
    }

    /* 0, or the error. */
    return(iError);
}

/*----------------------------------------------------------------------------*/
/* BFD_VerifyWrite:                                                           */
/*                                                                            */
/* Read a device write straight back and compare it with what was written --  */
/* the check that a card actually KEPT the data.  A counterfeit-capacity      */
/* card accepts every write and reports success while storing nothing past    */
/* its real end (one sold as 1 GB held about 120 MiB and read back 0x55       */
/* beyond it), so a write's own success proves nothing; only reading it back  */
/* does.  Which writes come here is BFD_SetVerify's to decide.  The first     */
/* mismatch of the operation is remembered for BFD_WriteNotKept, placed at    */
/* the start of the read-back piece that differed.                            */
/*                                                                            */
/* Arguments:                                                                 */
/*     ullOffset : the first byte written, from the start of the device.      */
/*     pbWritten : the bytes the caller asked to write.                       */
/*     cbBytes   : how many.                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0 when every byte read back the same; BFD_ERROR_NOT_KEPT when    */
/*           any differs (the detail line says where); otherwise the read's   */
/*           Windows error.                                                   */
/*----------------------------------------------------------------------------*/
static int BFD_VerifyWrite(uint64_t ullOffset, const uint8_t *pbWritten, uint64_t cbBytes)
{
    uint64_t ullPos;
    uint64_t cbLeft;
    uint64_t cbPiece;
    int      iError;

    /* A cursor over the range just written. */
    ullPos = ullOffset;
    cbLeft = cbBytes;

    while (cbLeft > 0)
    {   /* One read-back buffer's worth per iteration. */
        cbPiece = cbLeft;

        if (cbPiece > BFD_BOUNCE_BYTES)
        {   /* More than the buffer holds: the rest on the next pass. */
            cbPiece = BFD_BOUNCE_BYTES;
        }

        /* Read the piece back through the bounce into the verify buffer.     */
        /* The handle is unbuffered, so the bytes come from the device, not   */
        /* from a Windows cache that would hand back what was just written.   */
        iError = BFD_DeviceRead(ullPos, BFD_pbVerify, cbPiece);

        if (iError != 0)
        {   /* The read-back itself failed: the media is in trouble too. */

            /* Keep the failure for the detail line, named as a read-back   */
            /* so it is not mistaken for an ordinary read, and return it.   */
            BFD_NoteIo("read-back", ullPos, cbPiece, iError);
            return(iError);
        }

        if (memcmp(BFD_pbVerify, pbWritten, (size_t)cbPiece) != 0)
        {   /* The device said it wrote these bytes and did not keep them. */
            if (!BFD_bNotKept)
            {   /* The first such write of this operation: remember where.     */
                /* Remembered, not only returned, because e2fsck can be told   */
                /* to ignore a write error and carry on, and the operation's   */
                /* end must still fail (BFD_WriteNotKept).  The place is the   */
                /* START of this piece (pieces are up to 1 MiB) -- not the     */
                /* first differing byte, which is not looked for.              */
                BFD_bNotKept     = TRUE;
                BFD_ullNotKeptAt = ullPos;
            }

            /* The failure line in plain words, written directly rather      */
            /* than through BFD_NoteIo: there is no Windows error to name.   */
            snprintf(BFD_szLastIo, sizeof(BFD_szLastIo),
                     "the device accepted a write at byte %llu (%llu bytes) but did not keep it: reading it back gave "
                     "different data -- the card is failing, or holds less than it claims (a counterfeit)",
                     (unsigned long long)ullPos, (unsigned long long)cbPiece);
            BFD_szLastIo[sizeof(BFD_szLastIo) - 1] = '\0';
            return(BFD_ERROR_NOT_KEPT);
        }

        /* This piece matched; on to the next. */
        pbWritten += cbPiece;
        ullPos    += cbPiece;
        cbLeft    -= cbPiece;
    }

    /* Every byte is on the device as written. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFD_WriteNotKept:                                                          */
/*                                                                            */
/* Whether a device write in the current operation read back different, and   */
/* where the first one was -- the start of the read-back piece that differed  */
/* (see BFD_VerifyWrite).  Only writes made with the read-back on count.      */
/* The operations that write a device (a session's                            */
/* flush and close, a format, a repair, a resize) ask at their end, so a      */
/* lost write fails them even where the library underneath chose to carry     */
/* on past the error.  Cleared by BFD_Configure, at the start of the next     */
/* operation.                                                                 */
/*                                                                            */
/* Arguments:                                                                 */
/*     pullAt : receives the byte offset of the first lost write; may be      */
/*              NULL.                                                         */
/*                                                                            */
/* Returns:                                                                   */
/*     int : TRUE when a write was not kept, FALSE otherwise.                 */
/*----------------------------------------------------------------------------*/
int BFD_WriteNotKept(uint64_t *pullAt)
{
    if (pullAt != NULL)
    {   /* The caller wants the place too. */
        *pullAt = BFD_ullNotKeptAt;
    }

    /* The sticky verdict. */
    return(BFD_bNotKept);
}

/*----------------------------------------------------------------------------*/
/* BFD_TargetWrite:                                                           */
/*                                                                            */
/* Write a byte range to the open target, counted from its start, a device or */
/* a file alike; a device write is read back when the operation asked for it  */
/* (BFD_SetVerify).  A read-only target is refused.  A failure is noted for   */
/* BFD_LastIoText.                                                            */
/*                                                                            */
/* Arguments:                                                                 */
/*     ullOffset : the first byte, from the start of the file or device.      */
/*     pvBuf     : the cbBytes bytes.                                         */
/*     cbBytes   : how many; 0 is a legal no-op.                              */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0 when every byte was handed to the OS or kept by the device;    */
/*           ERROR_INVALID_HANDLE when nothing is open; ERROR_ACCESS_DENIED   */
/*           on a read-only target; BFD_ERROR_NOT_KEPT when a device read     */
/*           back different; otherwise the Windows error, ERROR_HANDLE_EOF    */
/*           for a short write (a full host disk).                            */
/*----------------------------------------------------------------------------*/
int BFD_TargetWrite(uint64_t ullOffset, const void *pvBuf, uint64_t cbBytes)
{
    int iError;

    if (BFD_target.hFile == INVALID_HANDLE_VALUE)
    {   /* Nothing is open. */
        return(ERROR_INVALID_HANDLE);
    }

    if (BFD_bReadOnly)
    {   /* The target was opened without write access; a write could not       */
        /* succeed anyway.  Refusing here as well as at the mount and in the   */
        /* io manager means no path through either library can modify an       */
        /* image that was opened to look at.                                   */
        return(ERROR_ACCESS_DENIED);
    }

    if (cbBytes == 0)
    {   /* Nothing to transfer. */
        return(0);
    }

    if (BFD_target.bDevice)
    {   /* A real device: through the aligned bounce, read-modify-write at   */
        /* ragged ends, then read back to prove the device kept it.          */
        iError = BFD_DeviceWrite(ullOffset, (const uint8_t *)pvBuf, cbBytes);

        if (iError == 0 && BFD_bVerify)
        {   /* Handed over, and this operation reads back: check it is really   */
            /* there, so a card that accepts writes and drops them fails this   */
            /* write rather than a later read of the data.                      */
            iError = BFD_VerifyWrite(ullOffset, (const uint8_t *)pvBuf, cbBytes);

            if (iError != 0)
            {   /* Not kept, or the read-back failed; the detail line is   */
                /* already written in its own words.                       */
                return(iError);
            }
        }
    }
    else
    {   /* A file: straight out.  The buffer is the caller's and is not       */
        /* modified; the cast only satisfies the shared transfer function's   */
        /* signature.                                                         */
        iError = BFD_FileTransfer(ullOffset, (uint8_t *)pvBuf, cbBytes, 1);
    }

    if (iError != 0)
    {   /* A short write (the host disk is full) or the media refused. */

        /* Keep it, in the device's own terms, for the detail line. */
        BFD_NoteIo("write", ullOffset, cbBytes, iError);
    }

    /* 0, or the error. */
    return(iError);
}

/*----------------------------------------------------------------------------*/
/* BFD_TargetFlush:                                                           */
/*                                                                            */
/* Make the target's writes real: FlushFileBuffers, which on a device waits   */
/* for the media.  Nothing to do when nothing is open.                        */
/*                                                                            */
/* Arguments:                                                                 */
/*     None.                                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0, or the Windows error when the media did not confirm.          */
/*----------------------------------------------------------------------------*/
int BFD_TargetFlush(void)
{
    if (BFD_target.hFile == INVALID_HANDLE_VALUE)
    {   /* Nothing open, nothing pending. */
        return(0);
    }

    /* The OS's flush, with its own verdict. */
    return(BFV_Flush(&BFD_target));
}

/*----------------------------------------------------------------------------*/
/* BFD_TargetClose:                                                           */
/*                                                                            */
/* Flush and close the target and release the bounce buffer.  The flush's     */
/* verdict is not reported here -- a caller that needs it calls               */
/* BFD_TargetFlush first (BFD_DevClose does) -- so the close itself cannot    */
/* fail and is safe to call on a target that is already closed.               */
/*                                                                            */
/* Arguments:                                                                 */
/*     None.                                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     void : the target is closed and the bounce released.                   */
/*----------------------------------------------------------------------------*/
void BFD_TargetClose(void)
{
    if (BFD_target.hFile != INVALID_HANDLE_VALUE)
    {   /* Something is open: make its writes real, then close it. */
        BFV_Flush(&BFD_target);
        BFV_Close(&BFD_target);
    }

    /* The bounce buffer, when a device had one (BFV_FreeAligned ignores   */
    /* NULL, so a file's close or a second close is harmless).             */
    BFV_FreeAligned(BFD_pbBounce);
    BFD_pbBounce = NULL;

    /* And the read-back buffer, when a writable device had one. */
    free(BFD_pbVerify);
    BFD_pbVerify = NULL;
}

/*----------------------------------------------------------------------------*/
/* BFD_TargetIsOpen:                                                          */
/*                                                                            */
/* Whether the target is open -- the first of four facts the e2fsprogs io     */
/* manager (bfe_fsck_io.c) asks about the target, since it opens and uses it  */
/* directly rather than through lwext4.                                       */
/*                                                                            */
/* Arguments:                                                                 */
/*     None.                                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     int : TRUE while a handle is open, FALSE otherwise.                    */
/*----------------------------------------------------------------------------*/
int BFD_TargetIsOpen(void)
{
    /* The handle is the state. */
    return(BFD_target.hFile != INVALID_HANDLE_VALUE);
}

/*----------------------------------------------------------------------------*/
/* BFD_TargetOffset:                                                          */
/*                                                                            */
/* Where the filesystem starts inside the target.  The io manager adds it to  */
/* every offset it is given, because e2fsprogs counts from the filesystem's   */
/* start while BFD_TargetRead / Write count from the target's.                */
/*                                                                            */
/* Arguments:                                                                 */
/*     None.                                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     uint64_t : the configured byte offset; valid once configured.          */
/*----------------------------------------------------------------------------*/
uint64_t BFD_TargetOffset(void)
{
    /* As configured. */
    return(BFD_ullOffset);
}

/*----------------------------------------------------------------------------*/
/* BFD_TargetIsDevice:                                                        */
/*                                                                            */
/* Whether the open target is a real device, so the io manager's channel can  */
/* say so.                                                                    */
/*                                                                            */
/* Arguments:                                                                 */
/*     None.                                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     int : TRUE for a device, FALSE for a file; meaningful only while open  */
/*           (it is learned at the open).                                     */
/*----------------------------------------------------------------------------*/
int BFD_TargetIsDevice(void)
{
    /* Learned at the open. */
    return(BFD_target.bDevice);
}

/*----------------------------------------------------------------------------*/
/* BFD_TargetIsObject:                                                        */
/*                                                                            */
/* Whether another open target is the very file or disk the session has open  */
/* -- by identity (BFV_SameObject), not by path.  The raw calls run while a   */
/* session may be open, and one writing to the session's own image file or    */
/* disk would change bytes lwext4 holds in its cache and believes it alone    */
/* writes; they ask this first and refuse.                                    */
/*                                                                            */
/* Arguments:                                                                 */
/*     pOther : the other target, open.                                       */
/*                                                                            */
/* Returns:                                                                   */
/*     int : TRUE when a session's target is open and pOther is (or may be)   */
/*           the same thing; FALSE when no target is open or they differ.     */
/*----------------------------------------------------------------------------*/
int BFD_TargetIsObject(const BFV_Target *pOther)
{
    if (BFD_target.hFile == INVALID_HANDLE_VALUE)
    {   /* No session target open: nothing to collide with. */
        return(FALSE);
    }

    /* The session's target against the other, by identity. */
    return(BFV_SameObject(&BFD_target, pOther));
}

/*----------------------------------------------------------------------------*/
/* BFD_TargetReadOnly:                                                        */
/*                                                                            */
/* Whether the configuration is read-only, so the io manager can refuse a     */
/* request for a writable channel before anything is opened.                  */
/*                                                                            */
/* Arguments:                                                                 */
/*     None.                                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     int : TRUE when configured read-only; valid once configured.           */
/*----------------------------------------------------------------------------*/
int BFD_TargetReadOnly(void)
{
    /* As configured. */
    return(BFD_bReadOnly);
}

/*----------------------------------------------------------------------------*/
/* BFD_InRange:                                                               */
/*                                                                            */
/* Turn one of lwext4's block requests into a byte offset and a length, and   */
/* say whether the whole of it lies inside the filesystem's range -- the part */
/* of the file or device this session was given, which BFD_DevOpen hands      */
/* lwext4 as part_offset / part_size.                                         */
/*                                                                            */
/* Every block lwext4 reads or writes, by whichever of its paths, arrives     */
/* through BFD_DevRead or BFD_DevWrite, so this is the one place that can     */
/* hold ALL of its I/O inside the range.  lwext4 bounds some of its own paths */
/* (its cached block calls and its byte calls) but not its direct             */
/* whole-block calls, which file reads and writes use with block numbers      */
/* taken from the file's extents; a damaged extent pointing past the          */
/* partition's end must not reach another partition of the same card, so the  */
/* test is made here, below every path, and does not depend on lwext4's.      */
/*                                                                            */
/* Nothing in the arithmetic can wrap: the block number is tested against     */
/* what fits BEFORE it is multiplied out to bytes, and the span is compared   */
/* as subtractions from the range -- offset below the range's start first,    */
/* then the room left -- never as a sum that a huge number would wrap round.  */
/*                                                                            */
/* Arguments:                                                                 */
/*     pDev       : the device: its physical block size and the range.        */
/*     ullBlock   : the first physical block, from the start of the target.   */
/*     uiCount    : how many blocks.                                          */
/*     pszWhat    : "read" or "write", for the detail line.                   */
/*     pullOffset : receives the first byte, from the start of the target.    */
/*     pcbBytes   : receives the length in bytes.                             */
/*                                                                            */
/* Returns:                                                                   */
/*     int : TRUE when the whole request lies inside the range; FALSE when    */
/*           any part of it does not, with the detail line                    */
/*           (BFD_LastIoText) saying where it was and what the range is.      */
/*----------------------------------------------------------------------------*/
static int BFD_InRange(struct ext4_blockdev *pDev, uint64_t ullBlock, uint32_t uiCount, const char *pszWhat,
                       uint64_t *pullOffset, uint64_t *pcbBytes)
{
    uint64_t ullBlockSize;
    uint64_t ullRelative;
    int      bInside;

    ullBlockSize = pDev->bdif->ph_bsize;

    /* The length: a 32-bit count times a block size of a few hundred       */
    /* bytes, widened before the multiply, so it cannot overflow 64 bits.   */
    *pcbBytes   = ullBlockSize * (uint64_t)uiCount;
    *pullOffset = 0;
    bInside     = FALSE;

    if (ullBlock <= UINT64_MAX / ullBlockSize)
    {   /* The block's byte offset fits in 64 bits: work it out, and test   */
        /* the span against the range in two steps that cannot wrap.        */
        *pullOffset = ullBlock * ullBlockSize;

        if (*pullOffset >= pDev->part_offset)
        {   /* At or after the range's start: how far in, and whether the   */
            /* request's length fits in what is left after that point.      */
            ullRelative = *pullOffset - pDev->part_offset;

            if (ullRelative <= pDev->part_size && *pcbBytes <= pDev->part_size - ullRelative)
            {   /* Every byte asked for lies inside the range. */
                bInside = TRUE;
            }
        }
    }

    if (!bInside)
    {   /* Some or all of it lies outside: say where, and what the range   */
        /* is, on the detail line the engine's failure report carries --   */
        /* lwext4 itself only passes an error code up.                     */
        snprintf(BFD_szLastIo, sizeof(BFD_szLastIo),
                 "device %s of block %llu (%lu block(s)) refused: outside the filesystem's range (%llu bytes at %llu)",
                 pszWhat, (unsigned long long)ullBlock, (unsigned long)uiCount,
                 (unsigned long long)pDev->part_size, (unsigned long long)pDev->part_offset);
        BFD_szLastIo[sizeof(BFD_szLastIo) - 1] = '\0';
    }

    /* Inside, or not (with the line written). */
    return(bInside);
}

/*----------------------------------------------------------------------------*/
/* BFD_DevRead:                                                               */
/*                                                                            */
/* lwext4's "read blocks" hook: read uiCount physical blocks starting at      */
/* block ullBlock, counted from the start of the file or device.  A request   */
/* that does not lie wholly inside the filesystem's range is refused before   */
/* the target is touched (BFD_InRange).                                       */
/*                                                                            */
/* Arguments:                                                                 */
/*     pDev     : the device (supplies the physical block size).              */
/*     pvBuf    : receives uiCount * ph_bsize bytes.                          */
/*     ullBlock : first physical block, from the start.                       */
/*     uiCount  : how many blocks; 0 is a legal no-op lwext4 does issue.      */
/*                                                                            */
/* Returns:                                                                   */
/*     int : EOK on a complete read; ERANGE for a request outside the range   */
/*           (lwext4's own word for a block past the end); EIO on any other   */
/*           failure or a short read.                                         */
/*----------------------------------------------------------------------------*/
static int BFD_DevRead(struct ext4_blockdev *pDev, void *pvBuf, uint64_t ullBlock, uint32_t uiCount)
{
    uint64_t ullOffset;
    uint64_t cbWanted;
    int      iError;

    if (!BFD_InRange(pDev, ullBlock, uiCount, "read", &ullOffset, &cbWanted))
    {   /* Outside the filesystem's range (the detail line says where):   */
        /* nothing is read, and the buffer is left as lwext4 passed it.   */
        return(ERANGE);
    }

    /* The read itself; a failure is noted there for the detail line. */
    iError = BFD_TargetRead(ullOffset, pvBuf, cbWanted);

    if (iError != 0)
    {   /* Not open, a short read (the image is truncated) or the media   */
        /* failed: lwext4 has one word for all of them.                   */
        return(EIO);
    }

    /* Every requested byte was read. */
    return(EOK);
}

/*----------------------------------------------------------------------------*/
/* BFD_DevWrite:                                                              */
/*                                                                            */
/* lwext4's "write blocks" hook: write uiCount physical blocks starting at    */
/* block ullBlock, counted from the start of the file or device.  A read-only */
/* session is refused, and so is a request that does not lie wholly inside    */
/* the filesystem's range (BFD_InRange) -- before anything is written, so a   */
/* damaged block number can never put data into another partition.            */
/*                                                                            */
/* Arguments:                                                                 */
/*     pDev     : the device (supplies the physical block size and range).    */
/*     pvBuf    : the uiCount * ph_bsize bytes to write.                      */
/*     ullBlock : first physical block, from the start.                       */
/*     uiCount  : how many blocks; 0 is a legal no-op.                        */
/*                                                                            */
/* Returns:                                                                   */
/*     int : EOK on a complete write, EROFS on a read-only session, ERANGE    */
/*           for a request outside the range, EIO on any other failure or a   */
/*           short write (a full host disk).                                  */
/*----------------------------------------------------------------------------*/
static int BFD_DevWrite(struct ext4_blockdev *pDev, const void *pvBuf, uint64_t ullBlock, uint32_t uiCount)
{
    uint64_t ullOffset;
    uint64_t cbWanted;
    int      iError;

    if (BFD_bReadOnly)
    {   /* Refused in lwext4's own word for it, before the target is asked.   */
        /* Refusing here as well as at the mount means no path through        */
        /* lwext4 can modify an image that was opened to look at.             */
        return(EROFS);
    }

    if (!BFD_InRange(pDev, ullBlock, uiCount, "write", &ullOffset, &cbWanted))
    {   /* Outside the filesystem's range (the detail line says where):   */
        /* refused whole, before a byte reaches the target.               */
        return(ERANGE);
    }

    /* The write itself, read back when the operation asked for it. */
    iError = BFD_TargetWrite(ullOffset, pvBuf, cbWanted);

    if (iError != 0)
    {   /* Not open, a short write (the host disk is full) or the media   */
        /* refused.                                                       */
        return(EIO);
    }

    /* Every byte was handed to the OS or the device. */
    return(EOK);
}

/*----------------------------------------------------------------------------*/
/* BFD_DevClose:                                                              */
/*                                                                            */
/* lwext4's "close device" hook: flush and close the target and release the   */
/* bounce buffer.  The flush is what makes a device write real; its failure   */
/* is reported, which lwext4 passes up from its unmount.                      */
/*                                                                            */
/* Arguments:                                                                 */
/*     pDev : the device being closed (unused; there is only one).            */
/*                                                                            */
/* Returns:                                                                   */
/*     int : EOK, or EIO when the final flush failed -- the handle is closed  */
/*           either way.                                                      */
/*----------------------------------------------------------------------------*/
static int BFD_DevClose(struct ext4_blockdev *pDev)
{
    struct ext4_blockdev *pUnused;
    int                   iError;

    /* The interface supplies the device; this single-device file needs no   */
    /* part of it.  A dummy assignment keeps the parameter "used".           */
    pUnused = pDev;
    pUnused = pUnused;

    /* The flush's verdict first, then the close, which cannot fail. */
    iError = BFD_TargetFlush();
    BFD_TargetClose();

    if (iError != 0)
    {   /* The media did not confirm the last writes. */
        return(EIO);
    }

    /* The device is closed. */
    return(EOK);
}

/*-----------------------------------------------------------------------------*/
/* BFD_Configure:                                                              */
/*                                                                             */
/* Record the path, byte range and access mode for the next open.  Each        */
/* operation that uses the block device begins here, which is why the          */
/* per-operation state -- the lost-write verdict and the read-back setting --  */
/* is reset here too.                                                          */
/*                                                                             */
/* The target is marked closed WITHOUT being closed: every user closes it at   */
/* the end of its operation, and a target still open here would have its       */
/* handle (and a device's bounce buffer) lost.                                 */
/*                                                                             */
/* Its two failures return different codes, so each caller reports the one     */
/* that happened: a range off the grid is the caller's argument, memory        */
/* running out is not.                                                         */
/*                                                                             */
/* Arguments:                                                                  */
/*     pwszImage : the image file's or device's path, UTF-16.                  */
/*     ullOffset : byte offset of the filesystem inside it; a multiple of 512. */
/*     ullLength : byte length of the filesystem; a multiple of 512, or 0 for  */
/*                 "to the end".                                               */
/*     bReadOnly : TRUE to open read-only and refuse writes.                   */
/*                                                                             */
/* Returns:                                                                    */
/*     int : 0 when recorded; EINVAL when the range is not sector-aligned;     */
/*           ENOMEM when the path copy could not be allocated.  Nothing is     */
/*           recorded on either failure.                                       */
/*-----------------------------------------------------------------------------*/
int BFD_Configure(const wchar_t *pwszImage, uint64_t ullOffset, uint64_t ullLength, int bReadOnly)
{
    wchar_t *pwszCopy;
    size_t   cchImage;

    if ((ullOffset % BFD_SECTOR_BYTES) != 0 || (ullLength % BFD_SECTOR_BYTES) != 0)
    {   /* lwext4 divides the offset by the sector size; a remainder would be   */
        /* silently dropped and the filesystem read from the wrong place.       */
        return(EINVAL);
    }

    /* Copy the path, NUL included.  A copy, because the caller's string   */
    /* (marshalled from the GUI) does not outlive the call, while lwext4   */
    /* opens the device later, at mount.                                   */
    cchImage = wcslen(pwszImage);
    pwszCopy = (wchar_t *)malloc((cchImage + 1) * sizeof(wchar_t));

    if (pwszCopy == NULL)
    {   /* Out of memory for the path copy: the earlier configuration, if   */
        /* any, is left as it was.                                          */
        return(ENOMEM);
    }

    /* The path and its NUL into the copy. */
    memcpy(pwszCopy, pwszImage, (cchImage + 1) * sizeof(wchar_t));

    /* Replace any earlier configuration.  The target starts closed. */
    free(BFD_pwszImage);
    BFD_pwszImage     = pwszCopy;
    BFD_ullOffset     = ullOffset;
    BFD_ullLength     = ullLength;
    BFD_bReadOnly     = bReadOnly;
    BFD_target.hFile  = INVALID_HANDLE_VALUE;

    /* A new operation: no write of it has been lost yet, and it reads back   */
    /* only if it says so (BFD_SetVerify).                                    */
    BFD_bNotKept      = FALSE;
    BFD_ullNotKeptAt  = 0;
    BFD_bVerify       = FALSE;

    /* And it is counted, so the engine's error recorder can tell that this    */
    /* operation began inside the call it is reporting for (BFD_Generation).   */
    /* Only a recorded configuration counts: a refused one (above) leaves      */
    /* the verdict and the count as they were.                                 */
    BFD_uiGeneration++;

    /* The next BFD_DevOpen uses these values. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFD_Generation:                                                            */
/*                                                                            */
/* How many configurations BFD_Configure has recorded.  The engine's gate     */
/* (BFX_Enter) takes it as a call begins; the error recorder (BFX_Fail)       */
/* compares, so a different count means the call itself began an operation    */
/* on the block device -- and the lost-write verdict (BFD_WriteNotKept),      */
/* which that configuration cleared, is then the call's own.                  */
/*                                                                            */
/* Arguments:                                                                 */
/*     None.                                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     uint32_t : the count; it wraps after 2^32 configurations, which an     */
/*                equality test across one call does not notice.              */
/*----------------------------------------------------------------------------*/
uint32_t BFD_Generation(void)
{
    /* As counted by BFD_Configure. */
    return(BFD_uiGeneration);
}

/*----------------------------------------------------------------------------*/
/* BFD_SetVerify:                                                             */
/*                                                                            */
/* Turn the read-back of device writes on or off for the operation just       */
/* configured.  BFD_Configure turns it off, so an operation that wants it     */
/* says so after configuring: a format, a resize and a repair always do --    */
/* they rewrite the filesystem's structure, where a lost write damages        */
/* everything -- and a session does when the caller asked for it (the user's  */
/* option, off by default because it about doubles the I/O of every write).   */
/* It has no effect on a file, which is never read back, nor on the           */
/* zero-fill and the raw copies, which write through bfe_device.c directly:   */
/* reading a whole card's zeros back would take as long again as writing      */
/* them, and a raw copy has its own whole-image verify.                       */
/*                                                                            */
/* Arguments:                                                                 */
/*     bOn : TRUE to read every device write back, FALSE not to.              */
/*                                                                            */
/* Returns:                                                                   */
/*     void : the setting holds until the next BFD_Configure.                 */
/*----------------------------------------------------------------------------*/
void BFD_SetVerify(int bOn)
{
    /* Recorded; BFD_TargetWrite reads it. */
    BFD_bVerify = bOn;
}

/*----------------------------------------------------------------------------*/
/* BFD_Get:                                                                   */
/*                                                                            */
/* Hand out the one static device for ext4_device_register.  BFE_Open         */
/* registers it on every open; lwext4 accepts the same name again without     */
/* change, so it is never unregistered.                                       */
/*                                                                            */
/* Arguments:                                                                 */
/*     None.                                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     struct ext4_blockdev * : the device; never NULL.                       */
/*----------------------------------------------------------------------------*/
struct ext4_blockdev *BFD_Get(void)
{
    /* The single static instance defined above. */
    return(&BFD_device);
}
