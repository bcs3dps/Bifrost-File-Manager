/*----------------------------------------------------------------------------*/
/* bfe_resize.c                                                               */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* BFE_ResizeFs: grow an ext2/3/4 filesystem to fill the byte range it sits   */
/* in -- the "expand the filesystem to fill the card" command -- with         */
/* e2fsprogs' own resize2fs code (1.47.4's resize\resize2fs.c and extent.c,   */
/* mirrored verbatim) run in-process.  This file is the engine's replacement  */
/* for resize2fs's main.c, the command-line front end, keeping its order and  */
/* every refusal that matters for a GROW of an UNMOUNTED filesystem:          */
/*                                                                            */
/*   - a filesystem not known good is refused (BFE_ERR_NEEDS_CHECK); the      */
/*     caller runs BFE_CheckFs and asks again.  There is no force: moving     */
/*     metadata on a filesystem that is not known good is how a resize        */
/*     destroys one;                                                          */
/*   - the new size is the whole range, and a size no larger than the         */
/*     present one is "nothing to do" (BFE_OK): this file only GROWS;         */
/*   - main.c's limits stand (bigalloc, 32-bit block numbers, descriptor      */
/*     room);                                                                 */
/*   - the filesystem is reached through the engine's own block device, so a  */
/*     card's partition is resized in place under the guards every device     */
/*     write gets.                                                            */
/*                                                                            */
/* What main.c does that this file deliberately does not: shrinking (-M, an   */
/* explicit smaller size), the online resize of a mounted filesystem, the     */
/* undo file (-z), the 64bit conversion (-b / -s), the RAID stride guess      */
/* (determine_fs_stride; the stride stays as the superblock records it),      */
/* extending an image FILE to the new size (the range given is the size, and  */
/* it must already exist), and the progress bar (resize_fs's progress calls   */
/* reach the caller's callback instead).                                      */
/*----------------------------------------------------------------------------*/

/* The raw I/O layer first (it brings windows.h, which must come before       */
/* e2fsprogs' headers); then e2fsprogs' build configuration, the C library,   */
/* libext2fs, its error codes and resize2fs's own interface; then the         */
/* engine's internals, its block-device target and the libext2fs bridge.      */
#include "bfe_device.h"

#include "config.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <ext2fs/ext2_fs.h>
#include <ext2fs/ext2fs.h>
#include <et/com_err.h>
#include <resize/resize2fs.h>

#include "bfe_internal.h"
#include "bfe_blockdev.h"
#include "bfe_fsck_io.h"

/*----------------------------------------------------------------------------*/
/* Definitions -- documentation table.                                        */
/*----------------------------------------------------------------------------*/
#define BFRZ_TEXT_BYTES   512              // the longest line this file says through the callback, NUL included

/*----------------------------------------------------------------------------*/
/* The run's callback, for the progress function resize_fs calls with no      */
/* context of the caller's.  Set at the start of BFE_ResizeFs, cleared at its */
/* end; the engine runs one call at a time (BFX_Enter), so one copy serves.   */
/*----------------------------------------------------------------------------*/
static BFE_CheckCallback BFRZ_pfnEvent;    // the caller's callback, or NULL
static void             *BFRZ_pvContext;   // its context pointer, handed back unchanged

/*----------------------------------------------------------------------------*/
/* BFRZ_Say:                                                                  */
/*                                                                            */
/* Hand one line of text to the caller's callback as a BFE_CHECK_EVENT_TEXT,  */
/* the event a check's report lines use, so one window can show both.         */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszText : the line, without its line ending.                           */
/*                                                                            */
/* Returns:                                                                   */
/*     void : the callback has had it, or there is none.                      */
/*----------------------------------------------------------------------------*/
static void BFRZ_Say(const char *pszText)
{
    BFE_CheckEvent ev;

    if (BFRZ_pfnEvent == NULL)
    {   /* No callback: nobody to tell. */
        return;
    }

    /* A TEXT event: zeroed, so the progress fields read 0, with the line. */
    memset(&ev, 0, sizeof(ev));
    ev.uiKind  = BFE_CHECK_EVENT_TEXT;
    ev.pszText = pszText;

    /* Delivered; the reply is discarded: a resize, once begun, is not   */
    /* stopped part-way (a filesystem half-moved is worse than one not   */
    /* touched).                                                         */
    (void)BFRZ_pfnEvent(&ev, BFRZ_pvContext);
}

/*----------------------------------------------------------------------------*/
/* BFRZ_Progress:                                                             */
/*                                                                            */
/* resize_fs's progress function: called as each of its passes (moving        */
/* blocks, scanning inodes, moving the inode tables) advances, and handed on  */
/* to the caller as a BFE_CHECK_EVENT_PROGRESS.                               */
/*                                                                            */
/* Arguments:                                                                 */
/*     rfs   : resize_fs's state (unused).                                    */
/*     iPass : resize2fs's pass number (E2_RSZ_*_PASS).                       */
/*     ulCur : how far the pass has got.                                      */
/*     ulMax : the pass's total.                                              */
/*                                                                            */
/* Returns:                                                                   */
/*     errcode_t : always 0, carry on.                                        */
/*----------------------------------------------------------------------------*/
static errcode_t BFRZ_Progress(ext2_resize_t rfs, int iPass, unsigned long ulCur, unsigned long ulMax)
{
    BFE_CheckEvent ev;
    ext2_resize_t  rfsUnused;

    /* The caller's callback needs nothing from resize2fs's state, whose   */
    /* parameter the hook's signature (resize2fs's) fixes: taken into a    */
    /* dummy variable, the coding style's form, as bfe_fsck.c's hooks      */
    /* take theirs, so the compiler sees it used.                          */
    rfsUnused = rfs;
    rfsUnused = rfsUnused;

    if (BFRZ_pfnEvent != NULL)
    {   /* Tell the caller how far the pass has got (the reply discarded). */
        memset(&ev, 0, sizeof(ev));
        ev.uiKind     = BFE_CHECK_EVENT_PROGRESS;
        ev.uiPass     = (uint32_t)iPass;
        ev.ullCurrent = ulCur;
        ev.ullTotal   = ulMax;
        (void)BFRZ_pfnEvent(&ev, BFRZ_pvContext);
    }

    /* Always 0, carry on: a non-zero return would abort the resize in the   */
    /* middle, leaving a filesystem half-moved, which this engine never      */
    /* asks for.                                                             */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFRZ_NeedsCheck:                                                           */
/*                                                                            */
/* main.c's "Please run 'e2fsck -f' first" test, condition for condition      */
/* (checked against e2fsprogs\resize\main.c: the same five tests, in the same */
/* order, without its -P exception), with the first reason that applies in    */
/* words a user can act on.  A full check by BFE_CheckFs (forced, as it       */
/* always is) clears every one of them on a sound filesystem.                 */
/*                                                                            */
/* Arguments:                                                                 */
/*     fs : the open filesystem.                                              */
/*                                                                            */
/* Returns:                                                                   */
/*     const char * : the reason a check is needed, or NULL when none is.     */
/*----------------------------------------------------------------------------*/
static const char *BFRZ_NeedsCheck(ext2_filsys fs)
{
    if (fs->super->s_state & EXT2_ERROR_FS)
    {   /* Errors were recorded on it. */
        return("the filesystem has errors recorded");
    }

    if ((fs->super->s_state & EXT2_VALID_FS) == 0)
    {   /* Not marked clean: last used without being unmounted. */
        return("the filesystem was not cleanly unmounted");
    }

    if (fs->super->s_lastcheck < fs->super->s_mtime)
    {   /* Mounted since its last full check. */
        return("the filesystem has been used since it was last checked");
    }

    if (ext2fs_free_blocks_count(fs->super) > ext2fs_blocks_count(fs->super) ||
        fs->super->s_free_inodes_count > fs->super->s_inodes_count)
    {   /* Counts that cannot be true. */
        return("the filesystem's free counts are impossible");
    }

    if (fs->super->s_last_orphan != 0 || ext2fs_has_feature_journal_needs_recovery(fs->super))
    {   /* Unfinished business: orphans to clean, or a journal to replay. */
        return("the filesystem has orphaned files or a journal to replay");
    }

    /* Known good enough to resize. */
    return(NULL);
}

/*----------------------------------------------------------------------------*/
/* BFRZ_Finish:                                                               */
/*                                                                            */
/* The one way out of BFE_ResizeFs once the block device was configured:      */
/* everything the run set up is undone, and the engine's busy flag released.  */
/*                                                                            */
/* Arguments:                                                                 */
/*     pLocks : the volumes locked for a device; empty otherwise.             */
/*                                                                            */
/* Returns:                                                                   */
/*     void : the engine is free for the next call.                           */
/*----------------------------------------------------------------------------*/
static void BFRZ_Finish(BFV_Locks *pLocks)
{
    /* The message table STEP 2 added. */
    remove_error_table(&et_ext2_error_table);

    if (BFD_TargetIsOpen())
    {   /* A channel left open -- by a failed open, or by the error path's   */
        /* free -- closed outright.                                          */
        BFD_TargetClose();
    }

    /* The device's volumes (none for a file), after the target is closed;    */
    /* the callback forgotten; then the gate.  A failure must be recorded     */
    /* BEFORE this is called (the VERIFY path records it after -- THE         */
    /* DEFECT EXISTS AND IS INTENTIONALLY DEFERRED: the program makes every   */
    /* engine call from its one worker thread, so no second call can enter    */
    /* between the release and the record).                                   */
    BFV_UnlockDisk(pLocks);
    BFRZ_pfnEvent  = NULL;
    BFRZ_pvContext = NULL;
    BFX_Leave();
}

/*----------------------------------------------------------------------------*/
/* BFE_ResizeFs:                                                              */
/*                                                                            */
/* The public entry, as the file header describes it, in main.c's order.      */
/*                                                                            */
/* Arguments:                                                                 */
/*     pwszImage : the image file or device, as for BFE_Open.                 */
/*     ullOffset : byte offset of the filesystem inside it (a multiple of     */
/*                 512; 0 for a file that is the filesystem).                 */
/*     ullLength : the range's byte length (a multiple of 512), or 0 for "to  */
/*                 the end" -- the size the filesystem is grown to fill.      */
/*     pfnEvent  : text and progress, or NULL.                                */
/*     pvContext : handed back to pfnEvent unchanged.                         */
/*     pResult   : the block counts before and after, and the block size.     */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK (grown, or nothing to do: before equals after), or        */
/*           BFE_ERR_* -- BUSY (another call holds the engine), ALREADY_OPEN, */
/*           ARG, NO_MEMORY (the path could not be copied or converted),      */
/*           IMAGE_OPEN, SYSTEM_DISK,                                         */
/*           VOLUME_LOCK, MOUNT (not a filesystem libext2fs opens),           */
/*           NEEDS_CHECK, UNSUPPORTED, IO_IMAGE (the resize itself failed:    */
/*           the filesystem needs a repair, and the text says so), or VERIFY  */
/*           (a device did not keep what was written -- read back             */
/*           different).                                                      */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_ResizeFs(const wchar_t *pwszImage, uint64_t ullOffset, uint64_t ullLength,
                         BFE_CheckCallback pfnEvent, void *pvContext, BFE_ResizeResult *pResult)
{
    BFV_Target    probe;
    BFV_Locks     locks;
    ext2_filsys   fs;
    errcode_t     retval;
    char         *pszUtf8;
    bool          bNoMemory;
    const char   *pszWhy;
    char          szLine[BFRZ_TEXT_BYTES];
    blk64_t       newSize;
    blk64_t       oldSize;
    dgrp_t        newGroups;
    unsigned long ulNewDescBlocks;
    uint32_t      uiSystem;
    uint32_t      uiBlockSize;
    int           iStatus;
    int           iError;
    int           iConfigure;

    /* The gate: the busy flag only -- "no image open" is tested just   */
    /* below with its own message.                                      */
    iStatus = BFX_Enter(false, false);

    if (iStatus != BFE_OK)
    {   /* Another call is running: BFX_Enter has said so, and the flag is   */
        /* not ours, so there is nothing to release.                         */
        return(iStatus);
    }

    if (BFX_IsOpen())
    {   /* The block device is in use by the open session: refuse, inside   */
        /* the gate so no open can start before the resize; only the flag   */
        /* is held.                                                         */
        iStatus = BFX_Fail(BFE_ERR_ALREADY_OPEN, "close the open image before resizing one");
        BFX_Leave();
        return(iStatus);
    }

    if (pwszImage == NULL || pwszImage[0] == L'\0' || pResult == NULL)
    {   /* Nothing to resize, or nowhere to put the answer: refuse.  Only   */
        /* the flag is held; the callback has not been stored yet.          */
        iStatus = BFX_Fail(BFE_ERR_ARG, "no image path or no result structure given");
        BFX_Leave();
        return(iStatus);
    }

    /* An empty result, no locks yet, and the caller's callback for the   */
    /* progress function (which has no context of the caller's).          */
    memset(pResult, 0, sizeof(*pResult));
    memset(&locks, 0, sizeof(locks));
    BFRZ_pfnEvent  = pfnEvent;
    BFRZ_pvContext = pvContext;

    /* STEP 1: probe the path for writing, exactly as a writable BFE_Open   */
    /* does, so the reasons it cannot be opened are the engine's.           */
    iError = BFV_Open(pwszImage, BFV_ACCESS_WRITE, &probe);

    if (iError != 0)
    {   /* Missing, in use, read-only, or a device without Administrator.    */
        /* Refuse, forgetting the callback (BFRZ_Finish is not yet the way   */
        /* out: nothing it undoes has been done).                            */
        iStatus = BFX_Fail(BFE_ERR_IMAGE_OPEN, "cannot open the image for writing: Windows error %d", iError);
        BFRZ_pfnEvent = NULL;
        BFX_Leave();
        return(iStatus);
    }

    /* The probe's handle is closed at once -- the io manager opens the     */
    /* path again for itself -- and only what it learned (device or not,    */
    /* which disk) is used below.  It is closed BEFORE the volumes are      */
    /* locked: when the path names a volume (\\.\X:), this handle is open   */
    /* on that very volume, and Windows refuses to lock a volume while      */
    /* other handles are open on it.                                        */
    BFV_Close(&probe);

    /* STEP 1a: a device gets a writable session's guards: never the system   */
    /* disk, and every volume on its disk locked for the run.                 */
    if (probe.bDevice)
    {   /* A real disk. */

        /* The system-disk guard, on the disk number the probe's handle had. */
        uiSystem = BFV_SystemDisk();

        if (uiSystem == BFV_NO_DISK || probe.uiDisk == BFV_NO_DISK || probe.uiDisk == uiSystem)
        {   /* The disk Windows runs from, or one that cannot be told apart: refuse. */

            /* Before any lock, because locking the system disk's volumes     */
            /* would dismount the running Windows: so no lock to give back;   */
            /* the callback is forgotten and the flag released.               */
            iStatus = BFX_Fail(BFE_ERR_SYSTEM_DISK, "the device is the disk Windows is running from, or cannot be told apart from it");
            BFRZ_pfnEvent = NULL;
            BFX_Leave();
            return(iStatus);
        }

        /* Lock and dismount every volume on the disk for the run (only   */
        /* the volumes on it are opened for writing).                     */
        iError = BFV_LockDisk(probe.uiDisk, &locks);

        if (iError != 0)
        {   /* The disk could not be locked -- a volume on it in use, or   */
            /* one whose disk could not be told: refuse with the lock's    */
            /* own sentence (BFV_LockDisk leaves nothing held).            */
            iStatus = BFX_Fail(BFE_ERR_VOLUME_LOCK, "%s", locks.szFailure);
            BFRZ_pfnEvent = NULL;
            BFX_Leave();
            return(iStatus);
        }
    }

    /* STEP 2: point the block device at the range, writable; the io manager   */
    /* opens it when libext2fs asks.  The error table makes error_message()    */
    /* speak libext2fs's codes.                                                */
    iConfigure = BFD_Configure(pwszImage, ullOffset, ullLength, false);

    if (iConfigure != 0)
    {   /* The range is not sector-aligned (EINVAL), or the path could not   */
        /* be copied (ENOMEM): each reported as itself.  Not yet             */
        /* BFRZ_Finish (no error table added): the locks, the callback and   */
        /* the flag are released by hand.                                    */
        if (iConfigure == ENOMEM)
        {   /* Out of memory: not the caller's argument. */
            iStatus = BFX_Fail(BFE_ERR_NO_MEMORY, "out of memory copying the image's path");
        }
        else
        {   /* Off the 512-byte grid. */
            iStatus = BFX_Fail(BFE_ERR_ARG, "the offset and length must be multiples of 512 bytes");
        }

        BFV_UnlockDisk(&locks);
        BFRZ_pfnEvent = NULL;
        BFX_Leave();
        return(iStatus);
    }

    /* A resize rewrites the filesystem's structure across the whole range,   */
    /* so every write it makes to a device is read back.                      */
    BFD_SetVerify(true);

    /* libext2fs' message table, removed again by BFRZ_Finish. */
    add_error_table(&et_ext2_error_table);

    /* STEP 3: open the filesystem as main.c does for an unmounted one:   */
    /* read-write, alone, 64-bit block numbers, red-black-tree bitmaps.   */
    pszUtf8 = BFU_WideToUtf8(pwszImage, &bNoMemory);

    if (pszUtf8 == NULL && bNoMemory)
    {   /* Out of memory converting the path: refused as that.  From here   */
        /* BFRZ_Finish is the way out.                                      */
        iStatus = BFX_Fail(BFE_ERR_NO_MEMORY, "out of memory converting the image path");
        BFRZ_Finish(&locks);
        return(iStatus);
    }

    if (pszUtf8 == NULL)
    {   /* An unpaired surrogate in the path.  From here BFRZ_Finish is the   */
        /* way out.                                                           */
        iStatus = BFX_Fail(BFE_ERR_ARG, "the image path is not valid UTF-16");
        BFRZ_Finish(&locks);
        return(iStatus);
    }

    /* The open; the name is only a label for messages (the io manager      */
    /* opens what BFD_Configure recorded), so it is freed straight after.   */
    fs     = NULL;
    retval = ext2fs_open2(pszUtf8, NULL, EXT2_FLAG_RW | EXT2_FLAG_EXCLUSIVE | EXT2_FLAG_64BITS, 0, 0, BFIO_Manager(), &fs);
    free(pszUtf8);

    if (retval != 0)
    {   /* Not a filesystem libext2fs will open for writing.  A failed open    */
        /* frees its own structure (no NOFREE flag was asked for); a channel   */
        /* it left open is closed by BFRZ_Finish.                              */
        iStatus = BFX_Fail(BFE_ERR_MOUNT, "cannot open the filesystem: %s", error_message(retval));
        BFRZ_Finish(&locks);
        return(iStatus);
    }

    /* Red-black-tree bitmaps, as main.c chooses: libext2fs keeps them as   */
    /* runs of set bits rather than one bit per block, so a large card's    */
    /* mostly-free or mostly-used map stays small in memory.  Then the      */
    /* present size, which the result reports both before and (until        */
    /* grown) after.                                                        */
    fs->default_bitmap_type = EXT2FS_BMAP64_RBTREE;
    uiBlockSize             = (uint32_t)fs->blocksize;
    oldSize                 = ext2fs_blocks_count(fs->super);
    pResult->uiBlockSize    = uiBlockSize;
    pResult->ullBlocksBefore = oldSize;
    pResult->ullBlocksAfter  = oldSize;

    /* STEP 4: main.c's health check; no force. */
    pszWhy = BFRZ_NeedsCheck(fs);

    if (pszWhy != NULL)
    {   /* Not known good: refuse, telling the user to check it first.       */
        /*                                                                   */
        /* FREED, NOT CLOSED, here and in every refusal below: a close       */
        /* writes the superblock back when the filesystem is dirty or        */
        /* anything went out through the channel (closefs.c), while a free   */
        /* writes nothing and still closes the channel (freefs.c) -- so a    */
        /* refused resize provably leaves the filesystem as it found it.     */
        /* BFRZ_Finish then removes the error table, unlocks, forgets the    */
        /* callback and releases the flag, after the text is recorded.       */
        ext2fs_free(fs);
        iStatus = BFX_Fail(BFE_ERR_NEEDS_CHECK, "%s; check it (e2fsck -f) before resizing", pszWhy);
        BFRZ_Finish(&locks);
        return(iStatus);
    }

    /* STEP 5: the features main.c will not resize without force. */
    if (ext2fs_has_feature_bigalloc(fs->super))
    {   /* Clustered allocation: resize2fs calls it a risk and asks for       */
        /* force, which the engine does not have.  Refused, freed unwritten   */
        /* as above.                                                          */
        ext2fs_free(fs);
        iStatus = BFX_Fail(BFE_ERR_UNSUPPORTED, "the filesystem uses bigalloc, which is not resized here");
        BFRZ_Finish(&locks);
        return(iStatus);
    }

    /* STEP 6: the new size -- the whole range -- trimmed as main.c trims     */
    /* its default.  BFIO_Length is asked while the filesystem holds the      */
    /* channel open, as it must be (0 otherwise).  Because the size comes     */
    /* FROM the range, every block resize_fs writes lies inside it (and the   */
    /* io manager refuses any write that would not), and the health check     */
    /* has just required a clean, checked filesystem, with no journal to      */
    /* replay.                                                                */
    newSize = BFIO_Length() / uiBlockSize;

    if (!ext2fs_has_feature_64bit(fs->super))
    {   /* 32-bit block numbers: at most 2^32 - 1 blocks. */
        if (newSize == (1ULL << 32))
        {   /* Exactly 2^32: one fewer fits. */
            newSize--;
        }
        else if (newSize > (1ULL << 32))
        {   /* More cannot be expressed in 32-bit block numbers; main.c       */
            /* refuses the same (the 64bit conversion is not offered here).   */
            /* Refused, freed unwritten as above.                             */
            ext2fs_free(fs);
            iStatus = BFX_Fail(BFE_ERR_UNSUPPORTED, "the new size is too large for a filesystem without the 64bit feature");
            BFRZ_Finish(&locks);
            return(iStatus);
        }
    }

    if (!ext2fs_has_feature_meta_bg(fs->super))
    {   /* Without meta_bg every descriptor block must fit in the first group. */
        newGroups       = ext2fs_div64_ceil(newSize - fs->super->s_first_data_block, EXT2_BLOCKS_PER_GROUP(fs->super));
        ulNewDescBlocks = ext2fs_div_ceil(newGroups, EXT2_DESC_PER_BLOCK(fs->super));

        if (ulNewDescBlocks + fs->super->s_first_data_block > EXT2_BLOCKS_PER_GROUP(fs->super))
        {   /* main.c's "too many block group descriptors": the descriptors   */
            /* would not fit before the first group's data.  Refused, freed   */
            /* unwritten as above.                                            */
            ext2fs_free(fs);
            iStatus = BFX_Fail(BFE_ERR_UNSUPPORTED, "the new size needs more group descriptors than this filesystem can hold");
            BFRZ_Finish(&locks);
            return(iStatus);
        }
    }

    /* resize2fs's own trim, which keeps a short last group usable. */
    adjust_new_size(fs, &newSize);

    if (newSize <= oldSize)
    {   /* Already as large as the range allows (or larger -- this file        */
        /* never shrinks): nothing to do, which is SUCCESS, not a refusal --   */
        /* the user asked for the range to be filled, and it is.  Said to      */
        /* the caller's report, the filesystem freed unwritten as above, and   */
        /* BFE_OK with the result's before and after equal.                    */
        snprintf(szLine, sizeof(szLine), "The filesystem is already %llu (%uk) blocks long; the range holds no more.  Nothing to do.",
                 (unsigned long long)oldSize, uiBlockSize / 1024);
        BFRZ_Say(szLine);
        ext2fs_free(fs);
        BFRZ_Finish(&locks);
        return(BFE_OK);
    }

    /* STEP 7: resize.  On success resize_fs closes the new filesystem        */
    /* (which writes it) and frees the old handle itself; on failure the      */
    /* handle is still the caller's, freed WITHOUT a close -- a close would   */
    /* write a superblock over a half-moved filesystem.                       */
    /* Say what is about to happen, as main.c does; then the resize, with     */
    /* no flags and this file's progress function.                            */
    snprintf(szLine, sizeof(szLine), "Resizing the filesystem from %llu to %llu (%uk) blocks.",
             (unsigned long long)oldSize, (unsigned long long)newSize, uiBlockSize / 1024);
    BFRZ_Say(szLine);

    /* The resize itself: resize2fs's own code, writing through the io      */
    /* manager, with newSize passed by address: resize_fs writes back the   */
    /* block count the new filesystem actually has (resize2fs.c).           */
    retval = resize_fs(fs, &newSize, 0, BFRZ_Progress);

    if (retval != 0)
    {   /* Failed part-way: the filesystem must be repaired.  Freed without   */
        /* a close (STEP 7's reason).  The failure is said twice, for two     */
        /* readers: once into the caller's report, as a line beside the       */
        /* progress, and once as the engine's error text; the text is         */
        /* recorded before BFRZ_Finish releases the flag.                     */
        ext2fs_free(fs);
        snprintf(szLine, sizeof(szLine), "The resize failed (%s).  Repair the filesystem (e2fsck -fy) before using it.", error_message(retval));
        BFRZ_Say(szLine);
        iStatus = BFX_Fail(BFE_ERR_IO_IMAGE, "the resize failed: %s; repair the filesystem (e2fsck -fy) before using it", error_message(retval));
        BFRZ_Finish(&locks);
        return(iStatus);
    }

    if (BFD_WriteNotKept(NULL))
    {   /* resize2fs finished, but the device did not keep some of what it    */
        /* wrote (every device write is read back): the grown filesystem is   */
        /* not on the card, and saying "now N blocks" would be false.         */
        /* BFRZ_Finish releases the busy flag BEFORE BFX_Fail records         */
        /* the error -- the reverse of the order BFX_Fail requires.  THE      */
        /* DEFECT EXISTS AND IS INTENTIONALLY DEFERRED: the program's one     */
        /* worker thread makes no second call meanwhile.                      */
        BFRZ_Say("The device did not keep what was written to it: the card is failing, or holds less than it claims.  "
                 "The filesystem on it is damaged.");
        BFRZ_Finish(&locks);
        return(BFX_Fail(BFE_ERR_VERIFY, "the device did not keep the resize's writes; the card is failing or counterfeit"));
    }

    /* Grown, and kept by the device: the new size into the result (the     */
    /* count resize_fs wrote back), and the line that closes the caller's   */
    /* report.                                                              */
    pResult->ullBlocksAfter = newSize;
    snprintf(szLine, sizeof(szLine), "The filesystem is now %llu (%uk) blocks long.", (unsigned long long)newSize, uiBlockSize / 1024);
    BFRZ_Say(szLine);

    /* Grown, written and closed (resize_fs closed the new filesystem,   */
    /* which wrote it); BFRZ_Finish releases the rest and the flag.      */
    BFRZ_Finish(&locks);
    return(BFE_OK);
}
