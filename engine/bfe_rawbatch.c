/*----------------------------------------------------------------------------*/
/* bfe_rawbatch.c                                                             */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* BFE_WriteRawBatch: a partition table written as ONE operation.  A table    */
/* is several separate byte ranges -- an MBR sector, a GPT's primary header   */
/* and entry array, the backup array and header at the disk's end.  Written   */
/* one engine call per piece, each call would lock and unlock the disk's      */
/* volumes on its own, and Windows could remount a volume between two pieces  */
/* and read a half-written table.  Here the target is opened once, every      */
/* piece is checked before the first byte is written, and on a device the     */
/* volumes stay locked from before the first write until after the last       */
/* write's flush and the request that Windows re-read the table.              */
/*----------------------------------------------------------------------------*/

/* memcpy and memset (string.h); the Windows handle types the target and the    */
/* lock table hold (windows.h); the public status codes, BFE_RawPiece and the   */
/* limits (bifrost_engine.h); the busy gate and BFX_Fail (bfe_internal.h);      */
/* the raw target and the volume locks (bfe_device.h); and the session's own    */
/* target, for the identity test (bfe_blockdev.h).                              */
#include <string.h>
#include <windows.h>
#include "bifrost_engine.h"
#include "bfe_internal.h"
#include "bfe_device.h"
#include "bfe_blockdev.h"

/*----------------------------------------------------------------------------*/
/* BFE_WriteRawBatch:                                                         */
/*                                                                            */
/* Write every piece of a set of byte ranges into an EXISTING file or device, */
/* under one open handle and, on a device, one holding of the disk's volume   */
/* locks.  It exists so a partition table reaches the disk with no moment in  */
/* which Windows can remount a volume over a table only partly written.       */
/*                                                                            */
/* Every refusal of the input comes before the first write: a bad piece, a    */
/* range past the end, a device range off the sector grid, two pieces that    */
/* overlap, the session's own target, the system disk, a lack of memory or a  */
/* volume that will not lock.  Once writing has begun nothing is undone: a    */
/* failure stops at the piece that failed, and the pieces before it -- and    */
/* possibly part of the failed one -- are already on the disk.  The caller's  */
/* order is kept, so the caller decides which copy of a table goes first.     */
/*                                                                            */
/* Arguments:                                                                 */
/*     pwszPath : the Windows file or device path; it must exist, and is      */
/*                never created, truncated or extended.                       */
/*     pPieces  : the pieces, each an offset in the whole target, a buffer    */
/*                and its length; the array and every buffer must stay        */
/*                readable and unchanged until the call returns.              */
/*     nPieces  : how many, 1 to BFE_WRITE_BATCH_MAX_PIECES.                  */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK once every piece is written and the flush has confirmed   */
/*           it.  BFE_ERR_BUSY when another call holds the engine (its error  */
/*           text is left alone); otherwise the first failure's code, with    */
/*           its text recorded -- BFE_ERR_ARG, BFE_ERR_IMAGE_OPEN,            */
/*           BFE_ERR_IO_IMAGE (a range past the end), BFE_ERR_SYSTEM_DISK,    */
/*           BFE_ERR_NO_MEMORY, BFE_ERR_VOLUME_LOCK, or BFE_ERR_IO_HOST (a    */
/*           write or the flush failed).                                      */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_WriteRawBatch(const wchar_t *pwszPath, const BFE_RawPiece *pPieces, uint32_t nPieces)
{
    BFV_Target stTarget;
    BFV_Locks stLocks;
    uint8_t *pbAligned;
    const void *pvWrite;
    uint32_t iPiece;
    uint32_t iEarlier;
    uint32_t cbLargest;
    uint32_t uiSystem;
    uint64_t ullEnd;
    uint64_t ullEarlierEnd;
    int iStatus;
    int iError;
    bool bWriteAttempted;

    /* Take the engine's busy flag first, before anything is opened or any     */
    /* error is written: the last-error text belongs to whichever call holds   */
    /* the flag.  No session is needed (false, false) -- a table is written    */
    /* with or without an image open, and the identity test below keeps it     */
    /* off the session's own target.                                           */
    iStatus = BFX_Enter(false, false);
    if (iStatus != BFE_OK)
    {   /* Another call is inside the engine.  BFX_Enter returned BUSY without   */
        /* taking the flag or touching that call's error text, so there is       */
        /* nothing to release and nothing is written here either.                */
        return(iStatus);
    }

    /* Put everything the release at the end frees into its "nothing held"     */
    /* state BEFORE the first refusal can jump there: a closed target, no      */
    /* volume locks, no scratch buffer.  The release then frees exactly what   */
    /* was taken, whichever refusal it is reached from.  The largest piece     */
    /* and the "a write was tried" mark start at zero and false.               */
    memset(&stTarget, 0, sizeof(stTarget));
    stTarget.hFile = INVALID_HANDLE_VALUE;
    memset(&stLocks, 0, sizeof(stLocks));
    pbAligned = NULL;
    cbLargest = 0;
    bWriteAttempted = false;

    if (pwszPath == NULL || pwszPath[0] == L'\0' || pPieces == NULL ||
        nPieces == 0 || nPieces > BFE_WRITE_BATCH_MAX_PIECES)
    {   /* No path, no pieces, or more than the limit -- which also bounds the   */
        /* overlap test below, whose cost grows with the square of the count.    */
        /* Refused with nothing open yet; the release frees nothing.             */
        iStatus = BFX_Fail(BFE_ERR_ARG, "a raw batch needs a path and 1 to %u pieces",
            (unsigned)BFE_WRITE_BATCH_MAX_PIECES);
        goto release;
    }

    /* Open the target to read and write what is there: BFV_ACCESS_WRITE       */
    /* opens an existing file or device and never creates or cuts one.  The    */
    /* open also gives what the checks below need -- the size, whether it is   */
    /* a device, its sector size and its disk number -- and a handle the       */
    /* identity test can ask.                                                  */
    iError = BFV_Open(pwszPath, BFV_ACCESS_WRITE, &stTarget);
    if (iError != 0)
    {   /* Missing, in use, read-only, or a device opened without               */
        /* Administrator.  The Windows error is in the text, since it is what   */
        /* tells those apart.  BFV_Open leaves the target closed on failure.    */
        iStatus = BFX_Fail(BFE_ERR_IMAGE_OPEN, "cannot open %ls for writing (Windows error %d)", pwszPath, iError);
        goto release;
    }

    if (BFD_TargetIsObject(&stTarget))
    {   /* The very file or disk the open session is using, under whatever       */
        /* path -- decided by identity, never by spelling.  Writing it would     */
        /* change bytes lwext4 holds in its cache and believes only it writes.   */
        /* Refused; the session must be closed first.                            */
        iStatus = BFX_Fail(BFE_ERR_ARG, "%ls is the mounted image or device; close it first", pwszPath);
        goto release;
    }

    /* Check every piece before any lock is taken or any byte written, so a     */
    /* bad piece anywhere in the set refuses the whole set with the target      */
    /* untouched.  The largest piece is found on the way, for the one scratch   */
    /* buffer a device needs.                                                   */
    for (iPiece = 0; iPiece < nPieces; iPiece++)
    {   /* One piece per iteration: its buffer, its range, its grid, and every   */
        /* earlier piece it might overlap.                                       */
        if (pPieces[iPiece].pvBytes == NULL || pPieces[iPiece].cbBytes == 0 ||
            pPieces[iPiece].cbBytes > BFE_WRITE_RAW_MAX || pPieces[iPiece].uiReserved != 0)
        {   /* No buffer, no length, longer than BFE_WRITE_RAW_MAX, or the        */
            /* reserved field set.  An empty piece is an error, not skipped: a    */
            /* table piece that came out empty means the table was built wrong.   */
            iStatus = BFX_Fail(BFE_ERR_ARG, "raw batch piece %u has invalid bytes, size or reserved bits", iPiece);
            goto release;
        }

        if (pPieces[iPiece].ullOffset > stTarget.ullSize ||
            pPieces[iPiece].cbBytes > stTarget.ullSize - pPieces[iPiece].ullOffset)
        {   /* The range runs past the end of the file or device.  Two tests,     */
            /* with a subtraction, so the sum offset + length is never formed:    */
            /* a huge offset would wrap that sum to a small number and pass.      */
            /* A file is never extended by this call, so a slip in an offset      */
            /* cannot grow an image.  BFE_ERR_IO_IMAGE, as BFE_WriteRaw reports   */
            /* the same refusal -- and it stays that whatever an earlier          */
            /* operation's device lost: this call drives no block device, so      */
            /* BFX_Fail does not read a lost write into it.                       */
            iStatus = BFX_Fail(BFE_ERR_IO_IMAGE, "raw batch piece %u runs past the end of %ls", iPiece, pwszPath);
            goto release;
        }

        if (stTarget.bDevice && (stTarget.uiSector == 0 ||
            pPieces[iPiece].ullOffset % stTarget.uiSector != 0 || pPieces[iPiece].cbBytes % stTarget.uiSector != 0))
        {   /* A device piece that is not whole sectors at a sector boundary --   */
            /* or a device that reported no sector size, which would make the     */
            /* test divide by zero.  The device is opened unbuffered, so the      */
            /* driver would refuse a partial sector; and nothing is read to       */
            /* fill one, since a table writer never needs that.                   */
            iStatus = BFX_Fail(BFE_ERR_ARG, "raw batch piece %u is not on the device's sector grid", iPiece);
            goto release;
        }

        /* This piece's end, then every earlier piece's.  Both ends are now     */
        /* known to be at or below the target's size, so the additions cannot   */
        /* wrap.  The ranges are half-open (the end byte is not included), so   */
        /* two pieces that only touch -- one ending where the next begins --    */
        /* are allowed; any shared byte is not.                                 */
        ullEnd = pPieces[iPiece].ullOffset + pPieces[iPiece].cbBytes;
        for (iEarlier = 0; iEarlier < iPiece; iEarlier++)
        {   /* One earlier, already checked piece per iteration. */
            ullEarlierEnd = pPieces[iEarlier].ullOffset + pPieces[iEarlier].cbBytes;
            if (pPieces[iPiece].ullOffset < ullEarlierEnd && pPieces[iEarlier].ullOffset < ullEnd)
            {   /* Each starts before the other ends: they share at least one    */
                /* byte.  Refused, because which piece's bytes the disk would    */
                /* end up holding would depend on the order -- a table builder   */
                /* that produced this has put two structures in one place.       */
                iStatus = BFX_Fail(BFE_ERR_ARG, "raw batch pieces %u and %u overlap", iEarlier, iPiece);
                goto release;
            }
        }

        if (pPieces[iPiece].cbBytes > cbLargest)
        {   /* The largest piece so far: the scratch buffer must hold it. */
            cbLargest = pPieces[iPiece].cbBytes;
        }
    }

    if (stTarget.bDevice)
    {   /* A device: the system-disk guard, the scratch buffer and the volume   */
        /* locks, in that order -- the two that can refuse without harm come    */
        /* before the lock, which dismounts the disk's volumes.                 */
        /*                                                                      */
        /* First: never the disk Windows runs from, nor a disk that cannot be   */
        /* told apart from it (either number unknown).  Tested BEFORE the       */
        /* lock: locking the system disk's volumes would dismount the running   */
        /* Windows.                                                             */
        uiSystem = BFV_SystemDisk();
        if (uiSystem == BFV_NO_DISK || stTarget.uiDisk == BFV_NO_DISK || stTarget.uiDisk == uiSystem)
        {   /* The system disk, or not provably another one.  Refused with no   */
            /* lock taken; the release closes the target.                       */
            iStatus = BFX_Fail(BFE_ERR_SYSTEM_DISK, "%ls is the Windows disk or cannot be distinguished from it", pwszPath);
            goto release;
        }

        /* One page-aligned scratch buffer, the size of the largest piece.  An   */
        /* unbuffered device write needs an aligned buffer address, which the    */
        /* caller's buffers (the program's byte arrays, pinned for the call in   */
        /* EngineSession.cs) cannot be relied on to have; each piece is copied   */
        /* into it before it is written.  Allocated once, here, so running out   */
        /* of memory can only refuse the whole batch -- never stop it after      */
        /* some pieces have landed.                                              */
        pbAligned = (uint8_t *)BFV_AllocAligned(cbLargest);
        if (pbAligned == NULL)
        {   /* Out of memory.  Nothing locked or written yet; the release   */
            /* closes the target.                                           */
            iStatus = BFX_Fail(BFE_ERR_NO_MEMORY, "out of memory for raw batch scratch");
            goto release;
        }

        /* Lock and dismount every volume on the disk, and hold the locks         */
        /* through every write, the flush and the re-read request below:          */
        /* while they are held Windows cannot mount a volume and read the         */
        /* table part-way through.                                                */
        /* The target's own handle stays open through the lock.  That is right    */
        /* for a whole disk (\\.\PhysicalDriveN), the only device path the        */
        /* program writes, whose handle holds no volume; a VOLUME path (\\.\X:)   */
        /* would have its handle open on a volume being locked, and Windows       */
        /* would refuse that lock (BFE_ERR_VOLUME_LOCK).  Such a caller would     */
        /* need BFE_Open's order: close, lock, then reopen.                       */
        iError = BFV_LockDisk(stTarget.uiDisk, &stLocks);
        if (iError != 0)
        {   /* The disk could not be locked: a program with files open on one    */
            /* of its volumes, or a volume whose disk could not be told -- the   */
            /* lock's own sentence says which.  No unlock is needed for the      */
            /* locks taken before it: BFV_LockDisk has already released them.    */
            /* The release frees the buffer and closes the target.               */
            iStatus = BFX_Fail(BFE_ERR_VOLUME_LOCK, "%s", stLocks.szFailure);
            goto release;
        }
    }

    /* Every piece has passed and, on a device, the volumes are held.  The    */
    /* pieces are written in the order given, never sorted or regrouped:      */
    /* which copy of a table goes to the disk first is the caller's choice,   */
    /* and it matters only if a write fails part-way, when the pieces after   */
    /* the failed one are never written.                                      */
    for (iPiece = 0; iPiece < nPieces; iPiece++)
    {   /* One piece written per iteration, under the same handle and locks.   */
        /* A file is written straight from the caller's buffer: it is opened   */
        /* buffered, so any address will do.                                   */
        pvWrite = pPieces[iPiece].pvBytes;

        if (pbAligned != NULL)
        {   /* A device (the only case with a scratch buffer): the piece is     */
            /* copied into the aligned buffer, which is then what is written.   */
            memcpy(pbAligned, pvWrite, pPieces[iPiece].cbBytes);
            pvWrite = pbAligned;
        }

        /* Mark that a write was tried BEFORE making it: a write that fails   */
        /* may still have changed part of its range, so the flush and the     */
        /* re-read request after the loop must run on that path too.          */
        bWriteAttempted = true;
        iError = BFV_Write(&stTarget, pPieces[iPiece].ullOffset, pvWrite, pPieces[iPiece].cbBytes);
        if (iError != 0)
        {   /* The write failed.  Stop at once, leaving the later pieces       */
            /* unwritten.  The offset given is the piece's start -- where in   */
            /* the piece the failure came is not known -- and the text warns   */
            /* that the table may now be incomplete on the disk.               */
            iStatus = BFX_Fail(BFE_ERR_IO_HOST,
                "writing raw batch piece %u of %ls at byte %llu failed (Windows error %d); the table may be incomplete",
                iPiece, pwszPath, (unsigned long long)pPieces[iPiece].ullOffset, iError);
            break;
        }
    }

    if (bWriteAttempted)
    {   /* At least one write was tried, whether all succeeded or one failed.   */
        /* Flush, so the bytes are on the media rather than in a cache -- on    */
        /* a failure path too, since what did land should be made durable.      */
        /* A flush failure is reported only when nothing failed before it:      */
        /* the first failure is the one the caller is told about.               */
        iError = BFV_Flush(&stTarget);
        if (iError != 0 && iStatus == BFE_OK)
        {   /* Every write succeeded but the media did not confirm them, so   */
            /* the table cannot be called written.                            */
            iStatus = BFX_Fail(BFE_ERR_IO_HOST, "%ls did not confirm the raw batch (Windows error %d)", pwszPath, iError);
        }

        /* Ask Windows to re-read the disk's partition table, while the        */
        /* volumes are still locked, and on a failure path too, since a        */
        /* failed write can still have changed the table.  Its result is not   */
        /* looked at: the bytes are already written, and a stale display is    */
        /* put right by re-seating the card.  On a file it does nothing.       */
        BFV_RefreshDisk(&stTarget);
    }

release:
    /* Every exit after the gate comes here, success or refusal, and frees     */
    /* what was taken: the scratch buffer, the target, the volume locks, and   */
    /* last the busy flag.  The target is closed BEFORE the locks go, so       */
    /* nothing of this call is still open on the disk when Windows remounts    */
    /* its volumes, which it does the moment a lock is released (what an       */
    /* open raw handle would cause at that moment is not established).         */
    /* Every failure's text was written by BFX_Fail above, while this call     */
    /* still held the flag, so no other call can have entered and              */
    /* overwritten it (BFX_Fail's rule).  Each of these is safe on something   */
    /* never taken.                                                            */
    BFV_FreeAligned(pbAligned);
    BFV_Close(&stTarget);
    BFV_UnlockDisk(&stLocks);
    BFX_Leave();

    /* BFE_OK only when every piece was written and the flush confirmed it;   */
    /* otherwise the first failure's code.                                    */
    return(iStatus);
}
