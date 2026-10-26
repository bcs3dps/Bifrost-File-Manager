/*----------------------------------------------------------------------------*/
/* bfe_fsck.c                                                                 */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* BFE_CheckFs: e2fsprogs' e2fsck, run in-process over an image file or a     */
/* device.  This file is the engine's replacement for e2fsck's unix.c -- the  */
/* command-line front end that parses options, opens the filesystem, orders   */
/* the checks and prints the verdict -- and follows that file's main()        */
/* step for step, with the pieces a program has and a library must not:       */
/*                                                                            */
/*   - NO EXIT.  e2fsck's fatal_error() calls exit() unless the context says  */
/*     a setjmp target is armed, in which case it longjmps there.  The        */
/*     engine arms one around the whole run (and re-arms it after             */
/*     e2fsck_run, which disarms it on its way out), so a fatal error -- an   */
/*     unopenable filesystem, an unsupported feature, out of memory -- lands  */
/*     back here with the context flagged E2F_FLAG_ABORT, and the DLL's       */
/*     caller gets a result instead of a dead process.  The other exits of    */
/*     unix.c (usage, a clean filesystem skipped, a signal) belong to paths   */
/*     this file does not take: the check is always forced, and there is no   */
/*     command line.                                                          */
/*   - NO STDOUT.  Everything e2fsck prints goes to stdout AND to the log     */
/*     file in ctx->logf when one is set; the engine sets one -- a file of    */
/*     its own in the temporary folder -- and reads it back through a second  */
/*     handle, line by line, each time e2fsck reports progress and once more  */
/*     at the end, handing every line to the caller's callback.  Nothing in   */
/*     e2fsck is patched for this.  (The stdout copy still goes wherever the  */
/*     process's stdout goes: nowhere, in a GUI; the console, in a test.)     */
/*   - NO QUESTIONS.  e2fsck is run as "-fn" (report only; every answer no)   */
/*     or "-fy" (repair; every answer yes), the two modes that never prompt.  */
/*   - THE ENGINE'S OWN I/O.  The filesystem is opened through the io manager */
/*     in bfe_fsck_io.c over the engine's block-device target, so a device,   */
/*     a partition and a read-only open behave exactly as they do for lwext4, */
/*     and the system-disk refusal and the volume locks of a writable device  */
/*     session apply to a repair.                                             */
/*                                                                            */
/* What unix.c does that this file deliberately does not: the mounted-        */
/* filesystem check (nothing is mounted here; the engine knows no image is    */
/* open), bad-blocks scanning (-c / -l), the undo file (-z), the e2fsck.conf  */
/* profile (an empty one is used), the external-journal lookup through        */
/* libblkid (an external journal is reported as not found), MMP (always       */
/* skipped: a multi-mount protection block is a cluster's concern, and        */
/* honouring it means writing to the image), the "looks like a FAT / NTFS"    */
/* plausibility hints on a bad magic, and the resource-usage report (-t).     */
/*----------------------------------------------------------------------------*/

/* The raw I/O layer first (it brings windows.h, which must come before      */
/* e2fsprogs' headers); then e2fsprogs' build configuration, the C library   */
/* and e2fsck's own internals -- its context, problem codes, journal types   */
/* and the e2p feature names; then the engine's internals, its block-        */
/* device target and the libext2fs bridge.                                   */
#include "bfe_device.h"

#include "config.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <errno.h>
#include <time.h>
#include <setjmp.h>
#include <ctype.h>
#include <wchar.h>
#include <share.h>

#include <e2fsck/e2fsck.h>
#include <e2fsck/problem.h>
#include <e2fsck/jfs_user.h>
#include <e2p/e2p.h>
#include <et/com_err.h>

#include "bfe_internal.h"
#include "bfe_blockdev.h"
#include "bfe_fsck_io.h"

/*----------------------------------------------------------------------------*/
/* Definitions -- documentation table.                                        */
/*----------------------------------------------------------------------------*/
#define BFCK_PROGRAM_NAME  "e2fsck"         // what e2fsck calls itself in its messages
#define BFCK_LINE_BYTES    4096             // the longest report line delivered whole; a longer one is delivered in pieces
#define BFCK_READ_BYTES    4096             // how much of the report file is read per pass
#define BFCK_PAGE_BYTES    4096             // the page size e2fsck sizes its block buffers by (unix.c asks sysconf; Windows' is 4096)
#define BFCK_LOG_PREFIX    L"bifrost-fsck-" // the report file's name prefix in the temporary folder

/*----------------------------------------------------------------------------*/
/* The one run's state.  Static rather than local because a fatal error       */
/* inside e2fsck returns here by longjmp, and a local changed after the       */
/* setjmp has no defined value afterwards; everything the run touches after   */
/* arming the jump lives here instead.                                        */
/*----------------------------------------------------------------------------*/
typedef struct BFCK_Run
{
    e2fsck_t          ctx;                      // e2fsck's context; NULL outside a run
    ext2_filsys       fs;                       // the open filesystem while one is; NULL otherwise
    BFE_CheckCallback pfnEvent;                 // the caller's callback, or NULL
    void             *pvContext;                // handed back to it unchanged
    BFE_CheckResult  *pResult;                  // where the verdict goes
    uint32_t          uiFlags;                  // the BFE_CHECK_* flags asked for
    wchar_t          *pwszLog;                  // the report file's path (malloc'd by _wtempnam); NULL when none was made
    FILE             *pfRead;                   // the second handle on the report file, for reading it back
    size_t            cbLine;                   // bytes of a partial line held in szLine between reads
    char              szLine[BFCK_LINE_BYTES];  // the partial line
    char              szChunk[BFCK_READ_BYTES];// one read's worth of the report file
    bool              bCancelled;           // true once the callback asked to stop
    int               iExit;                // e2fsck's exit status so far (FSCK_* bits)
    bool              bChanged;             // true when the filesystem was written to
    bool              bErrorsLeft;          // true when it still has errors at the end
    int               iOpenFlags;           // the EXT2_FLAG_* set the filesystem was opened with (unix.c's flags)
    errcode_t         retval;               // unix.c's retval
    errcode_t         retval2;              // unix.c's retval2 (the descriptor check)
    errcode_t         origRetval;           // unix.c's orig_retval (the error before the backup superblock was tried)
    blk64_t           origSuperblock;       // unix.c's orig_superblock
    int               iJournalSize;         // unix.c's journal_size, in megabytes (-1 when unknown)
    uint64_t          ullLength;            // the filesystem range's length in bytes, from the target
    BFV_Locks         locks;                // the volumes locked for a repair on a device; empty otherwise
    uint32_t          uiLastPass;           // the last pass (1-5) the progress hook reported; a rebuild's spinner is delivered as progress within it
} BFCK_Run;

static BFCK_Run BFCK_run;

/*----------------------------------------------------------------------------*/
/* e2fsck's global context pointer.  unix.c defines it; util.c's prompt code  */
/* looks at it for a pending cancel, and nothing else that is compiled does.  */
/* Set for the duration of a run.                                             */
/*----------------------------------------------------------------------------*/
e2fsck_t e2fsck_global_ctx;

/*----------------------------------------------------------------------------*/
/* BFCK_Deliver:                                                              */
/*                                                                            */
/* Hand one event to the caller's callback, and act on a cancel reply.  With  */
/* no callback there is nothing to deliver and nothing can cancel.            */
/*                                                                            */
/* Arguments:                                                                 */
/*     uiKind     : BFE_CHECK_EVENT_TEXT or _PROGRESS.                        */
/*     pszText    : the line (TEXT), or NULL.                                 */
/*     uiPass     : the pass (PROGRESS), or 0.                                */
/*     ullCurrent : how far (PROGRESS).                                       */
/*     ullTotal   : out of how much (PROGRESS).                               */
/*                                                                            */
/* Returns:                                                                   */
/*     void : BFCK_run.bCancelled and the context's cancel flag are set when  */
/*            the callback answered BFE_REPLY_CANCEL.                         */
/*----------------------------------------------------------------------------*/
static void BFCK_Deliver(uint32_t uiKind, const char *pszText, uint32_t uiPass,
                         uint64_t ullCurrent, uint64_t ullTotal)
{
    BFE_CheckEvent event;
    int            iReply;

    if (BFCK_run.pfnEvent == NULL)
    {   /* Nobody is listening: nothing delivered, and so nothing can ever   */
        /* cancel -- a check with no callback runs to its end.               */
        return;
    }

    /* The event: every one of its five fields set (it has no reserved one),   */
    /* by name -- so a field added to BFE_CheckEvent must be set here too.     */
    event.ullCurrent = ullCurrent;
    event.ullTotal   = ullTotal;
    event.pszText    = pszText;
    event.uiKind     = uiKind;
    event.uiPass     = uiPass;

    /* Hand it over and take the reply. */
    iReply = BFCK_run.pfnEvent(&event, BFCK_run.pvContext);

    if (iReply == BFE_REPLY_CANCEL && !BFCK_run.bCancelled)
    {   /* The first cancel: e2fsck's own stop flag, which its passes test    */
        /* between units of work and honour at their next step, so the run    */
        /* winds down the way e2fsck does on Ctrl-C; bCancelled is what the   */
        /* result's return code is then taken from.                           */
        BFCK_run.bCancelled = true;

        if (BFCK_run.ctx != NULL)
        {   /* The context exists (it always does once events flow). */
            BFCK_run.ctx->flags |= E2F_FLAG_CANCEL;
        }
    }
}

/*----------------------------------------------------------------------------*/
/* BFCK_PumpLog:                                                              */
/*                                                                            */
/* Read what e2fsck has written to the report file since the last time, and   */
/* deliver it line by line, each without its newline.  A partial line is      */
/* held until the rest arrives, or until the final pump.                      */
/*                                                                            */
/* Arguments:                                                                 */
/*     bFinal : true at the end of the run, to deliver a last unterminated    */
/*              line.                                                         */
/*                                                                            */
/* Returns:                                                                   */
/*     void : every complete line has been delivered.                         */
/*----------------------------------------------------------------------------*/
static void BFCK_PumpLog(bool bFinal)
{
    size_t cbRead;
    size_t i;
    char   c;

    if (BFCK_run.pfRead == NULL)
    {   /* No report file (the callback is NULL, or the file could not be   */
        /* made -- in which case the run never started).                    */
        return;
    }

    if (BFCK_run.ctx != NULL && BFCK_run.ctx->logf != NULL)
    {   /* Push e2fsck's buffered output to the file before reading it: the   */
        /* writer is e2fsck's own stdio stream.                               */
        fflush(BFCK_run.ctx->logf);
    }

    /* Pull everything new, a chunk at a time, splitting at newlines.  The   */
    /* reader is this file's second handle on the file, with its own         */
    /* position, so it simply carries on from where the last pump got to.    */
    for (;;)
    {   /* One chunk per iteration, until the reader is at the end. */
        cbRead = fread(BFCK_run.szChunk, 1, sizeof(BFCK_run.szChunk), BFCK_run.pfRead);

        if (cbRead == 0)
        {   /* Nothing more has been written yet; clear the end-of-file   */
            /* state so a later pump can read what comes after it.        */
            clearerr(BFCK_run.pfRead);
            break;
        }

        for (i = 0; i < cbRead; i++)
        {   /* One byte per iteration: gathered into the line, or ending it. */
            c = BFCK_run.szChunk[i];

            if (c == '\n')
            {   /* The line is complete: deliver it without its newline. */
                BFCK_run.szLine[BFCK_run.cbLine] = '\0';
                BFCK_Deliver(BFE_CHECK_EVENT_TEXT, BFCK_run.szLine, 0, 0, 0);
                BFCK_run.cbLine = 0;
            }
            else if (c != '\r')
            {   /* An ordinary byte (a carriage return would be e2fsck's      */
                /* progress bar, which is never turned on; dropped anyway).   */
                if (BFCK_run.cbLine == sizeof(BFCK_run.szLine) - 1)
                {   /* The line is longer than the buffer: deliver what fits   */
                    /* as a line of its own and carry on.                      */
                    BFCK_run.szLine[BFCK_run.cbLine] = '\0';
                    BFCK_Deliver(BFE_CHECK_EVENT_TEXT, BFCK_run.szLine, 0, 0, 0);
                    BFCK_run.cbLine = 0;
                }

                /* The byte onto the line (room is always left for the NUL). */
                BFCK_run.szLine[BFCK_run.cbLine] = c;
                BFCK_run.cbLine++;
            }
        }
    }

    if (bFinal && BFCK_run.cbLine > 0)
    {   /* The run is over and the last line had no newline (e2fsck's final   */
        /* "files / blocks" line, for one): deliver it as it is.              */
        BFCK_run.szLine[BFCK_run.cbLine] = '\0';
        BFCK_Deliver(BFE_CHECK_EVENT_TEXT, BFCK_run.szLine, 0, 0, 0);
        BFCK_run.cbLine = 0;
    }
}

/*----------------------------------------------------------------------------*/
/* BFCK_Progress:                                                             */
/*                                                                            */
/* e2fsck's progress hook (ctx->progress), called by each pass as it goes     */
/* and once with pass 0 at the end of each -- the run's heartbeat, which      */
/* delivers e2fsck's text as it is written and the progress itself.  It never */
/* stops the run by its return value; a cancel goes through the flags.        */
/*                                                                            */
/* Arguments:                                                                 */
/*     ctx    : e2fsck's context (always the run's).                          */
/*     iPass  : 1 to 5, or 0 for "a pass has ended".                          */
/*     ulCur  : how far the pass has got.                                     */
/*     ulMax  : out of how much.                                              */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0, always.                                                       */
/*----------------------------------------------------------------------------*/
static int BFCK_Progress(e2fsck_t ctx, int iPass, unsigned long ulCur, unsigned long ulMax)
{
    e2fsck_t ctxUnused;

    /* The context is the run's; a dummy assignment keeps the parameter   */
    /* "used" (the hook's signature is e2fsck's).                         */
    ctxUnused = ctx;
    ctxUnused = ctxUnused;

    /* Text first, pumped here so the caller sees e2fsck's text as it is    */
    /* written and not only at the end; then where the pass has got to --   */
    /* remembering which pass it is, for a rebuild's spinner                */
    /* (e2fsck_simple_progress).                                            */
    BFCK_PumpLog(false);

    if (iPass != 0)
    {   /* A pass under way, not the end-of-pass marker. */
        BFCK_run.uiLastPass = (uint32_t)iPass;
    }

    /* Where the pass has got to; a cancel reply lands in the context flags. */
    BFCK_Deliver(BFE_CHECK_EVENT_PROGRESS, NULL, (uint32_t)iPass, ulCur, ulMax);

    /* Carry on, always: that is how e2fsck's own hook answers and how the   */
    /* passes expect to be stopped -- by the flag a cancel set (above, in    */
    /* BFCK_Deliver), at a safe point.                                       */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* e2fsck_clear_progbar:                                                      */
/*                                                                            */
/* One of the two progress-bar functions e2fsck.h declares from unix.c (which */
/* this file replaces), called by message.c before every message to clear     */
/* the bar off the terminal line.  There is no terminal and no bar, so it     */
/* does nothing -- but it must exist, or the link fails.                      */
/*                                                                            */
/* Arguments:                                                                 */
/*     ctx : e2fsck's context (unused).                                       */
/*                                                                            */
/* Returns:                                                                   */
/*     void : nothing.                                                        */
/*----------------------------------------------------------------------------*/
void e2fsck_clear_progbar(e2fsck_t ctx)
{
    e2fsck_t ctxUnused;

    /* Nothing to clear; a dummy assignment keeps the parameter "used". */
    ctxUnused = ctx;
    ctxUnused = ctxUnused;
}

/*----------------------------------------------------------------------------*/
/* e2fsck_simple_progress:                                                    */
/*                                                                            */
/* The other unix.c progress-bar function, called by rehash.c and extents.c   */
/* as a spinner while a directory or an extent tree is rebuilt: it keeps the  */
/* text flowing and moves the caller's bar through a long rebuild instead of  */
/* leaving it standing still.                                                 */
/*                                                                            */
/* Arguments:                                                                 */
/*     ctx      : e2fsck's context (unused).                                  */
/*     pszLabel : the sub-task's name (unused).                               */
/*     fPercent : how far it is, 0 to 100.                                    */
/*     uiDpynum : a number to show beside it (unused).                        */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0, as unix.c's version answers.                                  */
/*----------------------------------------------------------------------------*/
int e2fsck_simple_progress(e2fsck_t ctx, const char *pszLabel, float fPercent, unsigned int uiDpynum)
{
    e2fsck_t     ctxUnused;
    const char  *pszUnused;
    float        fUnused;
    unsigned int uiUnused;

    /* The sub-task's facts are not reported; dummy assignments keep the   */
    /* parameters "used".                                                  */
    ctxUnused = ctx;
    ctxUnused = ctxUnused;
    pszUnused = pszLabel;
    pszUnused = pszUnused;
    fUnused   = fPercent;
    fUnused   = fUnused;
    uiUnused  = uiDpynum;
    uiUnused  = uiUnused;

    /* Keep the text flowing while the rebuild runs. */
    BFCK_PumpLog(false);

    if (BFCK_run.uiLastPass != 0 && fPercent >= 0.0f && fPercent <= 100.0f)
    {   /* A pass is known and the figure is sane: the rebuild's share, in   */
        /* tenths of a percent (0 to 1000 of 1000), as PROGRESS within the   */
        /* last pass the hook reported.                                      */
        BFCK_Deliver(BFE_CHECK_EVENT_PROGRESS, NULL, BFCK_run.uiLastPass, (unsigned long)(fPercent * 10.0f), 1000);
    }

    /* As unix.c's version answers. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFCK_TryOpen:                                                              */
/*                                                                            */
/* unix.c's try_open_fs: open the filesystem through the engine's io manager, */
/* at the primary superblock or at a chosen backup, and tie it to the         */
/* context.                                                                   */
/*                                                                            */
/* Arguments:                                                                 */
/*     ctx    : e2fsck's context (superblock and blocksize are read from it). */
/*     iFlags : the EXT2_FLAG_* set to open with.                             */
/*     pFs    : receives the filesystem, or NULL.                             */
/*                                                                            */
/* Returns:                                                                   */
/*     errcode_t : 0, or libext2fs' reason.                                   */
/*----------------------------------------------------------------------------*/
static errcode_t BFCK_TryOpen(e2fsck_t ctx, int iFlags, ext2_filsys *pFs)
{
    errcode_t     retval;
    unsigned long ulBlocksize;

    /* No filesystem until an open succeeds. */
    *pFs   = NULL;
    retval = 0;

    if (ctx->superblock)
    {   /* A backup superblock: at a block size the caller gave, or at the   */
        /* first one that works.                                             */
        ulBlocksize = ctx->blocksize;

        if (ulBlocksize == 0)
        {   /* Not given: probe each size, keeping the first that opens -- a   */
            /* backup superblock's position depends on the block size.         */
            for (ulBlocksize = EXT2_MIN_BLOCK_SIZE; ulBlocksize <= EXT2_MAX_BLOCK_SIZE; ulBlocksize *= 2)
            {   /* One block size per iteration. */
                retval = ext2fs_open2(ctx->filesystem_name, ctx->io_options, iFlags,
                                      ctx->superblock, ulBlocksize, BFIO_Manager(), pFs);

                if (*pFs != NULL)
                {   /* Whatever the answer, this probe's filesystem is not   */
                    /* kept: the real open below makes the one that is.      */
                    ext2fs_free(*pFs);
                    *pFs = NULL;
                }

                if (retval == 0)
                {   /* This size works. */
                    break;
                }
            }

            if (retval != 0)
            {   /* No size did: the last probe's reason is returned.  Nothing   */
                /* is held -- every probe's filesystem was freed in the loop,   */
                /* and *pFs is NULL -- so the caller just sees a failed open.   */
                return(retval);
            }
        }

        /* The real open, at the backup and the size settled on. */
        retval = ext2fs_open2(ctx->filesystem_name, ctx->io_options, iFlags,
                              ctx->superblock, ulBlocksize, BFIO_Manager(), pFs);
    }
    else
    {   /* The primary superblock, at its fixed place. */
        retval = ext2fs_open2(ctx->filesystem_name, ctx->io_options, iFlags, 0, 0, BFIO_Manager(), pFs);
    }

    if (retval == 0)
    {   /* Open: tie it to the context, as e2fsck's code expects to find it,   */
        /* and give it e2fsck's preferred bitmap type.                         */
        (*pFs)->priv_data = ctx;
        e2fsck_set_bitmap_type(*pFs, EXT2FS_BMAP64_RBTREE, "default", NULL);
    }

    /* libext2fs' verdict. */
    return(retval);
}

/*----------------------------------------------------------------------------*/
/* BFCK_Stats:                                                                */
/*                                                                            */
/* unix.c's show_stats in its ordinary (non-verbose) form: the one closing    */
/* line -- "<name>: N/M files (P.Q% non-contiguous), B/C blocks" -- written   */
/* through e2fsck's own log_out so it reaches the report like every other     */
/* line, and the same figures into the caller's result.                       */
/*                                                                            */
/* Arguments:                                                                 */
/*     ctx : e2fsck's context, with the filesystem still open.                */
/*                                                                            */
/* Returns:                                                                   */
/*     void : the line is in the report; the result's counts are filled.      */
/*----------------------------------------------------------------------------*/
static void BFCK_Stats(e2fsck_t ctx)
{
    ext2_filsys fs;
    ext2_ino_t  inodes;
    ext2_ino_t  inodesUsed;
    blk64_t     blocks;
    blk64_t     blocksUsed;
    int         iFragTotal;

    /* The counts from the superblock as e2fsck leaves it: the used   */
    /* figures are the totals less the free ones.                     */
    fs         = ctx->fs;
    inodes     = fs->super->s_inodes_count;
    inodesUsed = fs->super->s_inodes_count - fs->super->s_free_inodes_count;
    blocks     = ext2fs_blocks_count(fs->super);
    blocksUsed = ext2fs_blocks_count(fs->super) - ext2fs_free_blocks_count(fs->super);
    iFragTotal = 0;

    if (inodesUsed > 0)
    {   /* The fragmented share of the inodes in use, in tenths of a per cent   */
        /* rounded as unix.c rounds it.                                         */
        iFragTotal = (int)((10000ULL * (ctx->fs_fragmented + ctx->fs_fragmented_dir)) / inodesUsed);
        iFragTotal = (iFragTotal + 5) / 10;
    }

    /* The closing line, into the report like every other e2fsck line. */
    log_out(ctx, "%s: %u/%u files (%0d.%d%% non-contiguous), %llu/%llu blocks\n",
            ctx->device_name, (unsigned)inodesUsed, (unsigned)inodes,
            iFragTotal / 10, iFragTotal % 10,
            (unsigned long long)blocksUsed, (unsigned long long)blocks);

    /* The same figures for the caller. */
    BFCK_run.pResult->ullBlocksUsed  = blocksUsed;
    BFCK_run.pResult->ullBlocksTotal = blocks;
    BFCK_run.pResult->uiInodesUsed   = inodesUsed;
    BFCK_run.pResult->uiInodesTotal  = inodes;
}

/*----------------------------------------------------------------------------*/
/* BFCK_DeviceName:                                                           */
/*                                                                            */
/* unix.c's naming of the filesystem for its messages: the volume label when  */
/* it has one, else the name it was opened by, with spaces and colons         */
/* replaced so the name cannot be mistaken for the "name: message" separator. */
/*                                                                            */
/* Arguments:                                                                 */
/*     ctx : e2fsck's context, with the filesystem open.                      */
/*                                                                            */
/* Returns:                                                                   */
/*     void : ctx->device_name is set (e2fsck frees it with the context).     */
/*----------------------------------------------------------------------------*/
static void BFCK_DeviceName(e2fsck_t ctx)
{
    struct ext2_super_block *sb;
    char                    *cp;

    /* The superblock, for the label. */
    sb = ctx->fs->super;

    if (ctx->device_name == NULL && sb->s_volume_name[0] != '\0')
    {   /* The label, which is what a user knows the filesystem by. */
        ctx->device_name = string_copy(ctx, (char *)sb->s_volume_name, sizeof(sb->s_volume_name));
    }

    if (ctx->device_name == NULL)
    {   /* No label: the name it was opened by. */
        ctx->device_name = string_copy(ctx, ctx->filesystem_name, 0);
    }

    for (cp = ctx->device_name; *cp != '\0'; cp++)
    {   /* One character per iteration: the separators become underscores. */
        if (isspace((unsigned char)*cp) || *cp == ':')
        {   /* A space or a colon would confuse "name: message". */
            *cp = '_';
        }
    }
}

/*----------------------------------------------------------------------------*/
/* BFCK_OpenReport:                                                           */
/*                                                                            */
/* Make the report file -- a uniquely named file in the temporary folder --   */
/* and open it twice: once for e2fsck to write, once for this file to read    */
/* back.  Skipped altogether when there is no callback to deliver to.         */
/*                                                                            */
/* Arguments:                                                                 */
/*     ctx : e2fsck's context, whose logf is set.                             */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_IO_HOST when the file could not be made or       */
/*           opened (the last-error text says which).                         */
/*----------------------------------------------------------------------------*/
static int BFCK_OpenReport(e2fsck_t ctx)
{
    FILE *pfWrite;

    if (BFCK_run.pfnEvent == NULL)
    {   /* Nobody to deliver to: e2fsck's text goes only to stdout. */
        return(BFE_OK);
    }

    /* A name free in the temporary folder (malloc'd; freed at the end). */
    BFCK_run.pwszLog = _wtempnam(NULL, BFCK_LOG_PREFIX);

    if (BFCK_run.pwszLog == NULL)
    {   /* No temporary folder, or no name free in it. */
        return(BFX_Fail(BFE_ERR_IO_HOST, "cannot name a report file in the temporary folder (errno %d)", errno));
    }

    /* The writing handle, shared with the reader below: both opens are   */
    /* _wfsopen with _SH_DENYNO, so each allows the other.                */
    pfWrite = _wfsopen(BFCK_run.pwszLog, L"wb", _SH_DENYNO);

    if (pfWrite == NULL)
    {   /* The folder refused the file.  The name stays in pwszLog for   */
        /* BFCK_Finish to free (its remove of a file never made is a     */
        /* harmless no-op), so nothing is released here.                 */
        return(BFX_Fail(BFE_ERR_IO_HOST, "cannot create the report file in the temporary folder (errno %d)", errno));
    }

    /* The reading handle, with its own position, from the file's start. */
    BFCK_run.pfRead = _wfsopen(BFCK_run.pwszLog, L"rb", _SH_DENYNO);

    if (BFCK_run.pfRead == NULL)
    {   /* The second open failed, which the first should have made          */
        /* impossible; undo the first (the empty file itself is removed by   */
        /* BFCK_Finish).  The errno shown is read after the fclose, which    */
        /* may have replaced the open's.                                     */
        fclose(pfWrite);
        return(BFX_Fail(BFE_ERR_IO_HOST, "cannot reopen the report file for reading (errno %d)", errno));
    }

    /* e2fsck writes here from now on; the handle is e2fsck's, closed with   */
    /* its context.                                                          */
    ctx->logf = pfWrite;
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFCK_CloseReport:                                                          */
/*                                                                            */
/* Deliver whatever is left in the report, close the reading handle and       */
/* delete the file.  The writing handle is e2fsck's and is closed with its    */
/* context, which must already have happened: a file still open for writing   */
/* cannot be deleted, so the order is context first, then this.               */
/*                                                                            */
/* Arguments:                                                                 */
/*     None.                                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     void : the report file is gone.                                        */
/*----------------------------------------------------------------------------*/
static void BFCK_CloseReport(void)
{
    /* The last lines, an unterminated one included. */
    BFCK_PumpLog(true);

    if (BFCK_run.pfRead != NULL)
    {   /* The reading handle. */
        fclose(BFCK_run.pfRead);
        BFCK_run.pfRead = NULL;
    }

    if (BFCK_run.pwszLog != NULL)
    {   /* The file itself, and the name _wtempnam allocated. */
        _wremove(BFCK_run.pwszLog);
        free(BFCK_run.pwszLog);
        BFCK_run.pwszLog = NULL;
    }
}

/*----------------------------------------------------------------------------*/
/* BFCK_ReportUnsupported:                                                    */
/*                                                                            */
/* unix.c's print_unsupp_features: name every feature bit the library does    */
/* not support, through log_err, the way e2fsck tells a user to get a newer   */
/* version.  The caller then aborts the run.                                  */
/*                                                                            */
/* Arguments:                                                                 */
/*     ctx       : e2fsck's context.                                          */
/*     auiFeature: the three unsupported-bit words, compat / incompat /       */
/*                 ro_compat.                                                 */
/*                                                                            */
/* Returns:                                                                   */
/*     void : the line is in the report.                                      */
/*----------------------------------------------------------------------------*/
static void BFCK_ReportUnsupported(e2fsck_t ctx, const __u32 *auiFeature)
{
    __u32 m;
    int   i;
    int   j;

    /* The line's start; each feature's name follows on the same line. */
    log_err(ctx, "%s has unsupported feature(s):", ctx->filesystem_name);

    for (i = 0; i < 3; i++)
    {   /* One feature word per iteration. */
        for (j = 0, m = 1; j < 32; j++, m <<= 1)
        {   /* One bit per iteration. */
            if (auiFeature[i] & m)
            {   /* Set and unsupported: its name. */
                log_err(ctx, " %s", e2p_feature2string(i, m));
            }
        }
    }

    /* End the line. */
    log_err(ctx, "\n");
}

/*----------------------------------------------------------------------------*/
/* BFCK_ReportOpenFailure:                                                    */
/*                                                                            */
/* unix.c's commentary on a filesystem that would not open, printed after     */
/* com_err's one line: e2fsck's hint for the reason, and, for a corrupt       */
/* superblock, the offer to try again ignoring its errors, which the caller   */
/* acts on.  The caller aborts when this returns false.                       */
/*                                                                            */
/* Arguments:                                                                 */
/*     ctx    : e2fsck's context.                                             */
/*     retval : the open's error.                                             */
/*     pctx   : a cleared problem context for the fix_problem calls.          */
/*                                                                            */
/* Returns:                                                                   */
/*     bool : true when the caller should retry the open with                 */
/*            EXT2_FLAG_IGNORE_SB_ERRORS added; false when it should abort.   */
/*----------------------------------------------------------------------------*/
static bool BFCK_ReportOpenFailure(e2fsck_t ctx, errcode_t retval, struct problem_context *pctx)
{
    /* e2fsck's one-line error first, in its own words; the hint follows. */
    com_err(ctx->program_name, retval, "while trying to open %s", ctx->filesystem_name);

    if (retval == EXT2_ET_REV_TOO_HIGH)
    {   /* A revision this e2fsck does not know. */
        log_out(ctx, "%s", "The filesystem revision is apparently too high for this version of e2fsck.\n"
                           "(Or the filesystem superblock is corrupt)\n\n");
        fix_problem(ctx, PR_0_SB_CORRUPT, pctx);
        return(false);
    }

    if (retval == EXT2_ET_SHORT_READ)
    {   /* Nothing where the superblock should be. */
        log_out(ctx, "%s", "Could this be a zero-length partition?\n");
        return(false);
    }

    if (retval == EPERM || retval == EACCES)
    {   /* The OS refused the access. */
        if (ctx->options & E2F_OPT_READONLY)
        {   /* Read access was asked for. */
            log_out(ctx, "You must have %s access to the filesystem or be root\n", "r/o");
        }
        else
        {   /* Write access was asked for. */
            log_out(ctx, "You must have %s access to the filesystem or be root\n", "r/w");
        }

        return(false);
    }

    if (retval == ENXIO)
    {   /* No device behind the name. */
        log_out(ctx, "%s", "Possibly non-existent or swap device?\n");
        return(false);
    }

    if (retval == EBUSY)
    {   /* Someone else has it. */
        log_out(ctx, "%s", "Filesystem mounted or opened exclusively by another program?\n");
        return(false);
    }

    if (retval == ENOENT)
    {   /* Nothing by that name. */
        log_out(ctx, "%s", "Possibly non-existent device?\n");
        return(false);
    }

    if (retval == EROFS)
    {   /* Write-protected media. */
        log_out(ctx, "%s", "Disk write-protected; use the -n option to do a read-only\ncheck of the device.\n");
        return(false);
    }

    if ((retval == EXT2_ET_CORRUPT_SUPERBLOCK || retval == EXT2_ET_BAD_DESC_SIZE) &&
        !(BFCK_run.iOpenFlags & EXT2_FLAG_IGNORE_SB_ERRORS))
    {   /* A superblock the library's consistency checks reject: e2fsck's    */
        /* answer is to load it anyway, errors and all, and let the passes   */
        /* repair what they can.  Once only.                                 */
        log_out(ctx, "%s: Trying to load superblock despite errors...\n", ctx->program_name);
        return(true);
    }

    /* Anything else: a corrupt superblock, reported as such (unix.c would     */
    /* also print its "looks like a FAT / NTFS" guesses on a bad magic; that   */
    /* code is not compiled into the engine).                                  */
    fix_problem(ctx, PR_0_SB_CORRUPT, pctx);
    return(false);
}

/*----------------------------------------------------------------------------*/
/* BFCK_Finish:                                                               */
/*                                                                            */
/* Everything a run must undo, whichever way it ended -- the filesystem, the  */
/* context, the error tables, the report file, the device locks and the       */
/* engine's gate -- with the filesystem merely freed, not closed, after a     */
/* fatal error.                                                               */
/*                                                                            */
/* Arguments:                                                                 */
/*     bFatal : true after a fatal error landed in the setjmp target.         */
/*                                                                            */
/* Returns:                                                                   */
/*     void : the engine is as it was before the run.                         */
/*----------------------------------------------------------------------------*/
static void BFCK_Finish(bool bFatal)
{
    e2fsck_t ctx;

    /* The run's context, if it got that far. */
    ctx = BFCK_run.ctx;

    if (ctx != NULL)
    {   /* A context was made. */
        if (ctx->fs != NULL)
        {   /* A filesystem is open. */
            if (bFatal)
            {   /* As e2fsck's exit leaves it: no close, so no final flush --   */
                /* a flush on the way out of a failure could write a            */
                /* half-repaired state.                                         */
                ext2fs_free(ctx->fs);
                ctx->fs = NULL;
            }
            else
            {   /* The ordinary end: close, which writes what is dirty -- a   */
                /* repair's last writes.                                      */
                ext2fs_close_free(&ctx->fs);
            }
        }

        /* The text e2fsck wrote on its way out, before its log file goes. */
        BFCK_PumpLog(false);

        /* The external-journal name, which unix.c also frees itself before   */
        /* the context (the context's free does not).                         */
        free(ctx->journal_name);
        ctx->journal_name = NULL;

        /* The context, and e2fsck's writing handle on the report with it. */
        e2fsck_free_context(ctx);
        BFCK_run.ctx     = NULL;
        e2fsck_global_ctx = NULL;

        /* The two error-message tables the run added. */
        remove_error_table(&et_ext2_error_table);
        remove_error_table(&et_prof_error_table);
    }

    /* The report file -- after the context, whose writing handle had to   */
    /* be closed first (an open file cannot be deleted).                   */
    BFCK_CloseReport();

    if (BFD_TargetIsOpen())
    {   /* A channel the fatal path left open (ext2fs_free closes the       */
        /* filesystem's, but not one get_backup_sb was in the middle of):   */
        /* the target is closed outright.                                   */
        BFD_TargetClose();
    }

    /* The device's volumes (none for a file, or for a report-only check),   */
    /* released after the target is closed; then the gate.                   */
    BFV_UnlockDisk(&BFCK_run.locks);
    BFX_Leave();
}

/*----------------------------------------------------------------------------*/
/* BFCK_NotKeptStatus:                                                        */
/*                                                                            */
/* The status a check that RAN returns: BFE_OK -- the verdict being in the    */
/* result -- unless a repair wrote to a device that did not keep the writes   */
/* (the block device reads every device write back).  e2fsck answers "yes"    */
/* to "Ignore error?" on a repair run and carries on, so it may report the    */
/* filesystem corrected when none of the corrections reached the card; this   */
/* is the one place that knows better.  In that case the result is marked     */
/* as still having errors, uncorrected, and BFE_ERR_VERIFY is returned with   */
/* words that say what happened.                                              */
/*                                                                            */
/* Arguments:                                                                 */
/*     pResult : the result already filled from e2fsck's run.                 */
/*                                                                            */
/* Every caller calls this AFTER BFCK_Finish, which releases the busy         */
/* flag, so the BFX_Fail below records its error after the release -- the     */
/* reverse of the order BFX_Fail requires.  THE DEFECT EXISTS AND IS          */
/* INTENTIONALLY DEFERRED: the program makes every engine call from its one   */
/* worker thread, so no second call can enter between the two.                */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK, or BFE_ERR_VERIFY when a repair's write was not kept.    */
/*----------------------------------------------------------------------------*/
static int BFCK_NotKeptStatus(BFE_CheckResult *pResult)
{
    uint64_t ullAt;

    if (!BFD_WriteNotKept(&ullAt))
    {   /* Every write the repair made is on the device (or none was made). */
        return(BFE_OK);
    }

    /* The repair did not reach the card: nothing it fixed is fixed. */
    pResult->uiExitCode  = (pResult->uiExitCode | BFE_CHECK_UNCORRECTED) & ~(uint32_t)BFE_CHECK_CORRECTED;
    pResult->bErrorsLeft = 1;

    /* Said in words, with where it first showed.  ullAt is the START of   */
    /* the first read-back piece that differed -- up to 1 MiB of one       */
    /* write's read-back, less for a small write -- not the first byte     */
    /* that differed, so the message names the piece, never a byte.        */
    return(BFX_Fail(BFE_ERR_VERIFY, "the device did not keep the repair's writes (a read-back mismatch in the piece "
                    "starting at byte %llu): the card is "
                    "failing, or holds less than it claims, and the filesystem on it is still damaged",
                    (unsigned long long)ullAt));
}

/*----------------------------------------------------------------------------*/
/* BFE_CheckFs:                                                               */
/*                                                                            */
/* The public entry: e2fsck's main(), as the file header describes it.  The   */
/* numbered steps are unix.c's in its order; the labels restart, failure,     */
/* no_journal, check_quotas, cleanup and skip_write are its labels, kept so   */
/* the two can be read side by side.                                          */
/*                                                                            */
/* Arguments:                                                                 */
/*     pwszImage  : the image file or device, as for BFE_Open.                */
/*     ullOffset  : byte offset of the filesystem inside it (a multiple of    */
/*                  512; 0 for a file that is the filesystem).                */
/*     ullLength  : its byte length (a multiple of 512), or 0 for "to the     */
/*                  end".                                                     */
/*     uiFlags    : BFE_CHECK_REPAIR, or 0 for a report-only check.           */
/*     pfnEvent   : the callback for text and progress, or NULL.              */
/*     pvContext  : handed to it unchanged.                                   */
/*     pResult    : receives the verdict.                                     */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK when e2fsck ran (its verdict is in pResult, whatever it   */
/*           found); otherwise why the check could not be started -- see the  */
/*           header's contract.                                               */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_CheckFs(const wchar_t *pwszImage, uint64_t ullOffset, uint64_t ullLength,
                        uint32_t uiFlags, BFE_CheckCallback pfnEvent, void *pvContext,
                        BFE_CheckResult *pResult)
{
    /* The scalars written after the setjmp below are volatile, so their        */
    /* values are defined when a fatal error comes back through it -- and so    */
    /* the compiler, which cannot see that the landing code never reads them,   */
    /* does not have to warn that it might.                                     */
    BFV_Target                        probe;
    struct problem_context            pctx;
    e2fsck_t                          ctx;
    char                             *pszUtf8;
    bool                              bNoMemory;
    uint32_t                          uiSystem;
    int                               iAccess;
    int                               iStatus;
    int                               iError;
    bool                              bRepair;
    int volatile                      iRun;
    __u32                             auiFeature[3];
    bool volatile                     bPrintUnsupported;
    struct ext2_super_block *volatile sb;
    struct ext2fs_journal_params      jparams;
    int                               iConfigure;
    enum quota_type volatile          qtype;
    int                               iNeedsWriteout;
    int volatile                      iOrphanRet;

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
        /* the gate so no open can start before the check does; only the    */
        /* flag is held.                                                    */
        iStatus = BFX_Fail(BFE_ERR_ALREADY_OPEN, "close the open image before checking one");
        BFX_Leave();
        return(iStatus);
    }

    if (pwszImage == NULL || pwszImage[0] == L'\0' || pResult == NULL)
    {   /* Nothing to check, or nowhere to put the answer: refuse.  Only   */
        /* the flag is held; pResult is not touched (it may be NULL).      */
        iStatus = BFX_Fail(BFE_ERR_ARG, "no image path or no result structure given");
        BFX_Leave();
        return(iStatus);
    }

    /* The run's state starts clean, the result empty. */
    memset(&BFCK_run, 0, sizeof(BFCK_run));
    memset(pResult, 0, sizeof(*pResult));
    BFCK_run.pfnEvent  = pfnEvent;
    BFCK_run.pvContext = pvContext;
    BFCK_run.pResult   = pResult;
    BFCK_run.uiFlags   = uiFlags;

    /* Repair, or report only: the one choice the rest turns on. */
    bRepair = ((uiFlags & BFE_CHECK_REPAIR) != 0);

    /* STEP 1: probe the path in the mode the run will use, exactly as         */
    /* BFE_Open does, so the reasons it cannot be opened are reported in the   */
    /* engine's words and a device is known to be one.                         */
    if (bRepair)
    {   /* A repair writes. */
        iAccess = BFV_ACCESS_WRITE;
    }
    else
    {   /* A report-only check needs only read access. */
        iAccess = BFV_ACCESS_READ;
    }

    /* The probe: open the path as the run will, and learn what it is. */
    iError = BFV_Open(pwszImage, iAccess, &probe);

    if (iError != 0)
    {   /* Missing, in use, not writable, or a device without Administrator: refuse. */

        /* The Windows number tells those apart; nothing was opened, so the   */
        /* flag is all there is to release.                                   */
        iStatus = BFX_Fail(BFE_ERR_IMAGE_OPEN, "cannot open the image: Windows error %d", iError);
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

    /* STEP 1a: a device opened for a repair gets the guards a writable device   */
    /* session gets: the system disk is never written, and every volume on the   */
    /* device's disk is locked for the run.                                      */
    if (probe.bDevice && bRepair)
    {   /* A repair on a real disk. */

        /* The system-disk guard, on the disk number the probe's handle had. */
        uiSystem = BFV_SystemDisk();

        if (uiSystem == BFV_NO_DISK || probe.uiDisk == BFV_NO_DISK || probe.uiDisk == uiSystem)
        {   /* The disk Windows runs from, or one that cannot be told apart: refuse. */

            /* The test comes BEFORE any lock is taken, because locking the   */
            /* system disk's volumes would dismount the running Windows; so   */
            /* there is no lock to give back, and the probe is closed.        */
            iStatus = BFX_Fail(BFE_ERR_SYSTEM_DISK, "the device is the disk Windows is running from, or cannot be told apart from it");
            BFX_Leave();
            return(iStatus);
        }

        /* Lock and dismount every volume on the disk for the run (only   */
        /* the volumes on it are opened for writing).                     */
        iError = BFV_LockDisk(probe.uiDisk, &BFCK_run.locks);

        if (iError != 0)
        {   /* The disk could not be locked -- a volume on it in use, or     */
            /* one whose disk could not be told: refuse with the lock's      */
            /* own sentence, which says which (BFV_LockDisk leaves nothing   */
            /* held).                                                        */
            iStatus = BFX_Fail(BFE_ERR_VOLUME_LOCK, "%s", BFCK_run.locks.szFailure);
            BFX_Leave();
            return(iStatus);
        }
    }

    /* STEP 2: point the block device at the path; the io manager opens it   */
    /* when libext2fs asks.                                                  */
    iConfigure = BFD_Configure(pwszImage, ullOffset, ullLength, !bRepair);

    if (iConfigure != 0)
    {   /* The range is not sector-aligned (EINVAL), or the path could not   */
        /* be copied (ENOMEM): each reported as itself, then the volume      */
        /* locks released and the gate opened.                               */
        if (iConfigure == ENOMEM)
        {   /* Out of memory: not the caller's argument. */
            iStatus = BFX_Fail(BFE_ERR_NO_MEMORY, "out of memory copying the image's path");
        }
        else
        {   /* Off the 512-byte grid. */
            iStatus = BFX_Fail(BFE_ERR_ARG, "the offset and length must be multiples of 512 bytes");
        }

        BFV_UnlockDisk(&BFCK_run.locks);
        BFX_Leave();
        return(iStatus);
    }

    /* A repair rewrites the filesystem's structure, so every write it makes   */
    /* to a device is read back (a report-only run writes nothing).            */
    BFD_SetVerify(true);

    /* STEP 3: e2fsck's context, set up as PRS() in unix.c sets it up for        */
    /* "-f" plus "-n" or "-y": no prompts, every answer decided, a full check.   */
    BFCK_run.retval = e2fsck_allocate_context(&ctx);

    if (BFCK_run.retval != 0)
    {   /* Out of memory before anything began: no context exists, so not     */
        /* BFCK_Finish -- the locks (a repair on a device) and the flag are   */
        /* all there is.  The block device is configured but never opened.    */
        iStatus = BFX_Fail(BFE_ERR_NO_MEMORY, "cannot allocate e2fsck's context: %s", error_message(BFCK_run.retval));
        BFV_UnlockDisk(&BFCK_run.locks);
        BFX_Leave();
        return(iStatus);
    }

    /* The context is the run's, and the global util.c's prompt code reads. */
    BFCK_run.ctx      = ctx;
    e2fsck_global_ctx = ctx;

    /* The message tables error_message() names libext2fs' and the profile   */
    /* library's codes from (removed again by BFCK_Finish).                  */
    add_error_table(&et_ext2_error_table);
    add_error_table(&et_prof_error_table);

    /* "-f": a full check, never skipped as clean; no prompts; no read-ahead   */
    /* (the target has no cache to fill); this file's progress hook.           */
    ctx->program_name = BFCK_PROGRAM_NAME;
    ctx->options      = E2F_OPT_FORCE;
    ctx->interactive  = 0;
    ctx->readahead_kb = 0;
    ctx->progress     = BFCK_Progress;

    if (bRepair)
    {   /* "-y": every question answered yes. */
        ctx->options |= E2F_OPT_YES;
    }
    else
    {   /* "-n": every question answered no, and nothing written. */
        ctx->options |= E2F_OPT_NO | E2F_OPT_READONLY;
    }

    /* The name e2fsck reports the filesystem by, until the label replaces it. */
    pszUtf8 = BFU_WideToUtf8(pwszImage, &bNoMemory);

    if (pszUtf8 == NULL && bNoMemory)
    {   /* Out of memory converting the path: refused as that.  BFCK_Finish   */
        /* undoes everything from here on.                                    */
        iStatus = BFX_Fail(BFE_ERR_NO_MEMORY, "out of memory converting the image path");
        BFCK_Finish(false);
        return(iStatus);
    }

    if (pszUtf8 == NULL)
    {   /* An unpaired surrogate in the path.  BFCK_Finish undoes   */
        /* everything from here on.                                 */
        iStatus = BFX_Fail(BFE_ERR_ARG, "the image path is not valid UTF-16");
        BFCK_Finish(false);
        return(iStatus);
    }

    /* Into e2fsck's own allocation, freed with the context; the engine's   */
    /* copy is done with.                                                   */
    ctx->filesystem_name = string_copy(ctx, pszUtf8, 0);
    free(pszUtf8);

    /* An empty profile: e2fsck reads its defaults from e2fsck.conf when there   */
    /* is one; here every option keeps its built-in default.                     */
    BFCK_run.retval = profile_init(NULL, &ctx->profile);

    if (BFCK_run.retval != 0)
    {   /* Out of memory.  The context exists now, so from here every   */
        /* refusal goes through BFCK_Finish, which undoes it all.       */
        iStatus = BFX_Fail(BFE_ERR_NO_MEMORY, "cannot allocate e2fsck's profile: %s", error_message(BFCK_run.retval));
        BFCK_Finish(false);
        return(iStatus);
    }

    /* The report file e2fsck writes to and this file reads back. */
    iStatus = BFCK_OpenReport(ctx);

    if (iStatus != BFE_OK)
    {   /* The temporary folder refused.  BFCK_OpenReport has recorded why   */
        /* (while the flag was held); BFCK_Finish then undoes the context,   */
        /* the error tables, any half-made report file, the locks and the    */
        /* flag.  No filesystem has been opened, so nothing was written.     */
        BFCK_Finish(false);
        return(iStatus);
    }

    /* STEP 4: arm the landing place for a fatal error.  From here on a       */
    /* fatal_error() inside e2fsck comes back to this line with the context   */
    /* flagged E2F_FLAG_ABORT instead of ending the process.                  */
    if (setjmp(ctx->abort_loc))
    {   /* A fatal error landed: e2fsck has already printed why.  Its own      */
        /* exit code would have been FSCK_ERROR plus whatever the filesystem   */
        /* state adds; the result says the same.                               */
        ctx->flags &= ~E2F_FLAG_SETJMP_OK;
        BFCK_run.iExit |= FSCK_ERROR;

        if (ctx->fs != NULL)
        {   /* The filesystem was open: what it was left as. */
            BFCK_run.bChanged    = (ext2fs_test_changed(ctx->fs) != 0);
            BFCK_run.bErrorsLeft = !ext2fs_test_valid(ctx->fs);
        }
        else
        {   /* Never got open: nothing was checked, so nothing is known good. */
            BFCK_run.bErrorsLeft = true;
        }

        /* The verdict into the result, then everything released, the   */
        /* filesystem freed without a closing flush.                    */
        pResult->uiExitCode  = (uint32_t)BFCK_run.iExit;
        pResult->bModified   = BFCK_run.bChanged;
        pResult->bErrorsLeft = BFCK_run.bErrorsLeft;

        BFCK_Finish(true);

        if (BFCK_run.bCancelled)
        {   /* The abort was the caller's own cancel.  The bare code is       */
            /* returned and no error text is recorded -- BFCK_Finish has      */
            /* already released the flag, and the caller, who asked for the   */
            /* stop, needs no words for it.  The result holds what the run    */
            /* left (a repair cancelled part-way may have changed things).    */
            return(BFE_ERR_CANCELLED);
        }

        /* e2fsck ran and gave up; the result and the text say why (or the   */
        /* device dropped the repair's writes, which says more).             */
        return(BFCK_NotKeptStatus(pResult));
    }

    /* Armed: fatal_error now jumps back above instead of exiting. */
    ctx->flags |= E2F_FLAG_SETJMP_OK;

    /* STEP 5: open the filesystem.  unix.c's "restart:" -- the journal replay   */
    /* and a pass that asks for a restart both come back here.                   */
    ctx->superblock          = ctx->use_superblock;
    BFCK_run.iOpenFlags      = EXT2_FLAG_SKIP_MMP;
    BFCK_run.origSuperblock  = ~(blk64_t)0;

restart:
    /* Each attempt: a clean problem context; keep a failed open's structure   */
    /* for inspection; 64-bit block numbers.                                   */
    clear_problem_context(&pctx);
    BFCK_run.iOpenFlags |= EXT2_FLAG_NOFREE_ON_ERROR | EXT2_FLAG_64BITS;

    if (!(ctx->options & E2F_OPT_READONLY))
    {   /* A repair opens to write, and asks to be alone, as unix.c does.      */
        /* EXCLUSIVE becomes IO_FLAG_EXCLUSIVE on the channel, which this      */
        /* io manager ignores (bfe_fsck_io.c, BFIO_Open) -- what keeps other   */
        /* programs off a device here is STEP 1a's volume locks, and no        */
        /* image may be open in the engine (tested at the start).              */
        BFCK_run.iOpenFlags |= EXT2_FLAG_RW | EXT2_FLAG_EXCLUSIVE;
    }

    /* The open, remembered in the context as unix.c does; the descriptor   */
    /* check's result and the unsupported-feature report start clear.       */
    ctx->openfs_flags = BFCK_run.iOpenFlags;
    BFCK_run.retval   = BFCK_TryOpen(ctx, BFCK_run.iOpenFlags, &BFCK_run.fs);
    BFCK_run.retval2  = 0;
    bPrintUnsupported = false;

    if (BFCK_run.retval == 0)
    {   /* Open: the group descriptors get e2fsck's stricter look. */
        BFCK_run.retval2 = ext2fs_check_desc(BFCK_run.fs);
    }

    if (!ctx->superblock && !(ctx->flags & E2F_FLAG_SB_SPECIFIED) &&
        (BFCK_run.retval == EXT2_ET_BAD_MAGIC || BFCK_run.retval == EXT2_ET_SB_CSUM_INVALID ||
         BFCK_run.retval == EXT2_ET_CORRUPT_SUPERBLOCK ||
         (BFCK_run.retval == 0 && BFCK_run.retval2 != 0)))
    {   /* The primary superblock or the descriptors are bad: say so, and try   */
        /* a backup superblock when the filesystem is big enough to have one.   */
        if (BFCK_run.retval != 0)
        {   /* The open itself failed. */
            pctx.errcode = BFCK_run.retval;
            fix_problem(ctx, PR_0_OPEN_FAILED, &pctx);
        }

        if (BFCK_run.retval2 != 0)
        {   /* The descriptors did. */
            pctx.errcode = BFCK_run.retval2;
            fix_problem(ctx, PR_0_CHECK_DESC_FAILED, &pctx);
        }

        /* The code is reported; clear it for the problems that follow. */
        pctx.errcode = 0;

        if (BFCK_run.retval2 == ENOMEM || BFCK_run.retval2 == EXT2_ET_NO_MEMORY)
        {   /* Not a filesystem problem at all: the descriptor check ran out   */
            /* of memory.  A backup superblock would not help, so it is not    */
            /* tried; the memory failure becomes the open's error and the      */
            /* run goes straight to the failure handling below.                */
            BFCK_run.retval = BFCK_run.retval2;
            goto failure;
        }

        if (BFCK_run.fs != NULL && (BFCK_run.fs->flags & EXT2_FLAG_NOFREE_ON_ERROR))
        {   /* The failed open left the structure for inspection; done with it. */
            ext2fs_free(BFCK_run.fs);
            BFCK_run.fs = NULL;
        }

        if (BFCK_run.fs == NULL || BFCK_run.fs->group_desc_count > 1)
        {   /* More than one group, or unknown: a backup superblock exists. */
            if (BFCK_run.retval != 0)
            {   /* The superblock was the problem. */
                log_out(ctx, "%s: %s trying backup blocks...\n", ctx->program_name, "Superblock invalid,");
            }
            else
            {   /* The descriptors were. */
                log_out(ctx, "%s: %s trying backup blocks...\n", ctx->program_name, "Group descriptors look bad...");
            }

            /* Remember where it started, then let e2fsck find the first     */
            /* backup (it opens a second channel through the io manager to   */
            /* look); ctx->superblock is set to it.                          */
            BFCK_run.origSuperblock = ctx->superblock;
            get_backup_sb(ctx, BFCK_run.fs, ctx->filesystem_name, BFIO_Manager());

            if (BFCK_run.fs != NULL)
            {   /* The first attempt's filesystem (it opened; only its          */
                /* descriptors were bad), closed with its channel before the    */
                /* backup's open -- unix.c's order.  Why unix.c closes rather   */
                /* than frees here is not stated there.                         */
                ext2fs_close_free(&BFCK_run.fs);
            }

            /* The first error kept for the report; then the backup's open. */
            BFCK_run.origRetval = BFCK_run.retval;
            BFCK_run.retval     = BFCK_TryOpen(ctx, BFCK_run.iOpenFlags, &BFCK_run.fs);

            if (BFCK_run.origRetval == 0 && BFCK_run.retval != 0)
            {   /* The primary opened and the backup did not: back to the   */
                /* primary, bad descriptors and all.                        */
                if (BFCK_run.fs != NULL)
                {   /* The backup attempt's filesystem. */
                    ext2fs_close_free(&BFCK_run.fs);
                }

                log_out(ctx, "%s: %s while using the backup blocks", ctx->program_name, error_message(BFCK_run.retval));
                log_out(ctx, "%s: going back to original superblock\n", ctx->program_name);
                ctx->superblock = BFCK_run.origSuperblock;
                BFCK_run.retval = BFCK_TryOpen(ctx, BFCK_run.iOpenFlags, &BFCK_run.fs);
            }
        }
    }

    if ((BFCK_run.retval == EXT2_ET_UNSUPP_FEATURE || BFCK_run.retval == EXT2_ET_RO_UNSUPP_FEATURE) &&
        BFCK_run.fs != NULL && BFCK_run.fs->super != NULL)
    {   /* The library refused a feature: which ones. */
        sb            = BFCK_run.fs->super;
        auiFeature[0] = sb->s_feature_compat & ~EXT2_LIB_FEATURE_COMPAT_SUPP;
        auiFeature[1] = sb->s_feature_incompat & ~EXT2_LIB_FEATURE_INCOMPAT_SUPP;
        auiFeature[2] = sb->s_feature_ro_compat & ~EXT2_LIB_FEATURE_RO_COMPAT_SUPP;

        if (auiFeature[0] || auiFeature[1] || auiFeature[2])
        {   /* At least one bit to name. */
            bPrintUnsupported = true;
        }
    }

failure:
    if (bPrintUnsupported)
    {   /* Name the unsupported features, then give up as e2fsck does.        */
        /* fatal_error does not return: it jumps to STEP 4's landing place,   */
        /* which records the verdict and frees the filesystem unflushed.      */
        BFCK_ReportUnsupported(ctx, auiFeature);
        fatal_error(ctx, "Get a newer version of e2fsck!");
    }

    if (BFCK_run.retval != 0)
    {   /* Not open: say why, and either retry once with the superblock's   */
        /* errors ignored or give up.                                       */
        if (BFCK_run.origRetval != 0)
        {   /* The first error is the one to report, not the backup's. */
            BFCK_run.retval = BFCK_run.origRetval;
        }

        if (BFCK_ReportOpenFailure(ctx, BFCK_run.retval, &pctx))
        {   /* Once more, ignoring the superblock's errors, from the primary   */
            /* superblock if a backup had been tried.                          */
            if (BFCK_run.fs != NULL)
            {   /* The failed attempt's filesystem. */
                ext2fs_close_free(&BFCK_run.fs);
            }

            /* Set for good: BFCK_ReportOpenFailure offers this once only. */
            BFCK_run.iOpenFlags |= EXT2_FLAG_IGNORE_SB_ERRORS;

            if (BFCK_run.origSuperblock != ~(blk64_t)0)
            {   /* A backup had been tried: back to the original. */
                ctx->superblock = BFCK_run.origSuperblock;
            }

            goto restart;
        }

        /* Give up: fatal_error jumps to the landing place above. */
        fatal_error(ctx, 0);
    }

    /* STEP 6: the filesystem is open.  Only the master superblock is updated    */
    /* (the backups are left alone, as e2fsck leaves them), the device size is   */
    /* what the target reported, and the context takes the filesystem.           */
    BFCK_run.fs->flags |= EXT2_FLAG_MASTER_SB_ONLY;

    if (!(ctx->flags & E2F_FLAG_GOT_DEVSIZE))
    {   /* The size, which unix.c asks the OS for; the target already knows. */
        BFCK_run.ullLength = 0;

        if (BFD_TargetIsOpen())
        {   /* The io manager opened it for the filesystem just opened. */
            BFCK_run.ullLength = BFIO_Length();
        }

        /* The device size e2fsck checks the filesystem against.  With it     */
        /* known, e2fsck's superblock check (STEP 9) answers a superblock     */
        /* claiming more blocks than the range with PR_0_FS_SIZE_WRONG,       */
        /* whose "Abort?" a repair answers yes -- so the passes never work    */
        /* on a filesystem larger than the range.  Every write is ALSO held   */
        /* to the range below libext2fs, by the io manager (bfe_fsck_io.c),   */
        /* which refuses any write reaching past it; that is what bounds      */
        /* the journal replay of STEP 7, which runs before this check (as     */
        /* in unix.c) and takes its block numbers from the journal's tags.    */
        /* A tag naming a block past the end is refused as a short write      */
        /* (EXT2_ET_SHORT_WRITE), with nothing of it written.                 */
        ctx->num_blocks = BFCK_run.ullLength / EXT2_BLOCK_SIZE(BFCK_run.fs->super);
        ctx->flags     |= E2F_FLAG_GOT_DEVSIZE;
    }

    /* The context takes the filesystem, with the run's "now". */
    ctx->fs           = BFCK_run.fs;
    BFCK_run.fs->now  = ctx->now;
    sb                = BFCK_run.fs->super;

    if (sb->s_rev_level > E2FSCK_CURRENT_REV)
    {   /* A revision this e2fsck does not know. */
        com_err(ctx->program_name, EXT2_ET_REV_TOO_HIGH, "while trying to open %s", ctx->filesystem_name);
        fatal_error(ctx, "Get a newer version of e2fsck!");
    }

    /* The name messages use, and e2fsck's read / write error hooks on the   */
    /* channel (BFIO_Transfer hands a failed block to them).                 */
    BFCK_DeviceName(ctx);
    ehandler_init(BFCK_run.fs->io);

    /* STEP 7: the journal.  Its superblock fields are checked first; then, if   */
    /* the filesystem needs recovery, a repair replays the journal and starts    */
    /* over on the recovered filesystem, while a report-only check says it is    */
    /* skipping the replay, as e2fsck -n does.                                   */
    BFCK_run.retval = e2fsck_check_ext3_journal(ctx);

    if (BFCK_run.retval != 0)
    {   /* The journal's own structures are wrong: say so and give up. */
        com_err(ctx->program_name, BFCK_run.retval, "while checking journal for %s", ctx->device_name);
        fatal_error(ctx, "Cannot proceed with file system check");
    }

    if (ext2fs_has_feature_journal_needs_recovery(sb))
    {   /* Transactions are waiting in the journal. */
        if (ctx->options & E2F_OPT_READONLY)
        {   /* Report only: the transactions are left as they are, and the    */
            /* passes then check the filesystem WITHOUT them -- so a report   */
            /* can list faults the replay would have repaired.  The flush     */
            /* is unix.c's, at the same place; on this channel nothing is     */
            /* pending (the io manager keeps no cache and a read-only run     */
            /* writes nothing), its result is not looked at, and why          */
            /* upstream flushes here is not stated there.                     */
            log_out(ctx, "%s", "Warning: skipping journal recovery because doing a read-only filesystem check.\n");
            io_channel_flush(ctx->fs->io);
        }
        else
        {   /* A repair replays them, once. */
            if (ctx->flags & E2F_FLAG_RESTARTED)
            {   /* The replay ran and the flag is still set: the superblock   */
                /* could not be written, or the media lies.                   */
                com_err(ctx->program_name, 0, "unable to set superblock flags on %s\n", ctx->device_name);
                fatal_error(ctx, 0);
            }

            /* The replay itself.  It writes every tagged block where the    */
            /* journal says, before STEP 9's size check; the io manager      */
            /* refuses any of those writes that would land past the range.   */
            BFCK_run.retval = e2fsck_run_ext3_journal(ctx);

            if (BFCK_run.retval == EFSBADCRC)
            {   /* A transaction's checksum was wrong. */
                log_out(ctx, "Journal checksum error found in %s\n", ctx->device_name);
            }
            else if (BFCK_run.retval == EFSCORRUPTED)
            {   /* The journal's structure was wrong. */
                log_out(ctx, "Journal corrupted in %s\n", ctx->device_name);
            }
            else if (BFCK_run.retval != 0)
            {   /* Anything else. */
                com_err(ctx->program_name, BFCK_run.retval, "while recovering journal of %s", ctx->device_name);
            }

            /* Whatever the replay said: close (writing what it did), mark   */
            /* the run restarted -- so a second need for recovery is fatal   */
            /* above -- and open the recovered filesystem afresh.            */
            ext2fs_close_free(&ctx->fs);
            BFCK_run.fs = NULL;
            ctx->flags |= E2F_FLAG_RESTARTED;
            goto restart;
        }
    }

    /* STEP 8: e2fsck is stricter than the library about features. */
    auiFeature[0] = sb->s_feature_compat & ~EXT2_LIB_FEATURE_COMPAT_SUPP;
    auiFeature[1] = sb->s_feature_incompat & ~EXT2_LIB_FEATURE_INCOMPAT_SUPP;
    auiFeature[2] = sb->s_feature_ro_compat & ~EXT2_LIB_FEATURE_RO_COMPAT_SUPP;

    if (auiFeature[0] || auiFeature[1] || auiFeature[2])
    {   /* Something it does not know. */
        BFCK_ReportUnsupported(ctx, auiFeature);
        fatal_error(ctx, "Get a newer version of e2fsck!");
    }

    if (ext2fs_has_feature_casefold(sb) && !BFCK_run.fs->encoding)
    {   /* A casefold filesystem whose encoding the library has no table for. */
        log_err(ctx, "%s has unsupported encoding: %0x\n", ctx->filesystem_name, sb->s_encoding);
        fatal_error(ctx, "Get a newer version of e2fsck!");
    }

    if (ctx->superblock && !(ctx->options & E2F_OPT_READONLY))
    {   /* Opened from a backup superblock: the primary is presumed bad, so   */
        /* the superblock is written out at the end whatever else happens.    */
        ext2fs_mark_super_dirty(BFCK_run.fs);
    }

    /* Blocks per page, which sizes some of e2fsck's buffers; 1 when a block   */
    /* is a page or bigger.                                                    */
    ctx->blocks_per_page = BFCK_PAGE_BYTES / BFCK_run.fs->blocksize;

    if (ctx->blocks_per_page == 0)
    {   /* A block bigger than a page. */
        ctx->blocks_per_page = 1;
    }

    /* STEP 9: the superblock's own checks, then the checks that need no pass. */
    if (ctx->superblock)
    {   /* From a backup: the relocation problems are latched, as unix.c does. */
        set_latch_flags(PR_LATCH_RELOC, PRL_LATCHED, 0);
    }

    /* Valid to start with; check_super_block marks it invalid as it finds   */
    /* faults (and aborts a repair on a size the range cannot hold).         */
    ext2fs_mark_valid(BFCK_run.fs);
    check_super_block(ctx);

    if (ctx->flags & E2F_FLAG_SIGNAL_MASK)
    {   /* Cancelled or aborted during the superblock checks. */
        fatal_error(ctx, 0);
    }

    /* (unix.c's check_if_skip would end a clean, unforced run here; the check   */
    /* is always forced, so it never would, and it is not called.)               */
    check_resize_inode(ctx);

    if (ctx->flags & E2F_FLAG_SIGNAL_MASK)
    {   /* Cancelled or aborted during the resize-inode check. */
        fatal_error(ctx, 0);
    }

    /* Valid again, "'til proven otherwise", at the same point unix.c marks   */
    /* it: the faults the two checks above found have been reported (and,     */
    /* in a repair, fixed), and from here the validity the end of the run     */
    /* tests is what the passes find.                                         */
    ext2fs_mark_valid(BFCK_run.fs);

    /* The bad-blocks list the filesystem records (a scan is not done here). */
    BFCK_run.retval = ext2fs_read_bb_inode(BFCK_run.fs, &BFCK_run.fs->badblocks);

    if (BFCK_run.retval != 0)
    {   /* The bad-blocks inode could not be read: noted, and the run goes on.   */
        /* preenhalt stops only a "-p" (preen) run, which this never is, so      */
        /* it returns at once; it is kept so the lines match unix.c's.           */
        log_out(ctx, "%s: %s while reading bad blocks inode\n", ctx->program_name, error_message(BFCK_run.retval));
        preenhalt(ctx);
        log_out(ctx, "%s", "This doesn't bode well, but we'll try to go on...\n");
    }

    /* The journal's size from the superblock's backup of it, for recreating   */
    /* one; unknown when fast commits are on, as the backup does not say how   */
    /* many of the blocks were theirs.                                         */
    if (sb->s_jnl_backup_type == EXT3_JNL_BACKUP_BLOCKS && !ext2fs_has_feature_fast_commit(sb))
    {   /* The backup's words 15 and 16 hold the journal inode's BYTE size     */
        /* (i_size_high, i_size -- lib\ext2fs\mkjournal.c), so this is that    */
        /* size in MiB: the high word moved up 12 bits, the low one down 20.   */
        BFCK_run.iJournalSize = (sb->s_jnl_blocks[15] << (32 - 20)) | (sb->s_jnl_blocks[16] >> 20);
    }
    else
    {   /* Unknown. */
        BFCK_run.iJournalSize = -1;
    }

    if (ext2fs_has_feature_quota(sb))
    {   /* Quotas are on: the passes account for them as they go. */
        clear_problem_context(&pctx);
        pctx.errcode = quota_init_context(&ctx->qctx, ctx->fs, 0);

        if (pctx.errcode != 0)
        {   /* The quota context could not be made. */
            fix_problem(ctx, PR_0_QUOTA_INIT_CTX, &pctx);
            fatal_error(ctx, 0);
        }
    }

    /* STEP 10: the five passes.  e2fsck_run arms its own setjmp on the same    */
    /* jump buffer and disarms it on the way out, so the engine's is re-armed   */
    /* straight after for the code below, which can still hit a fatal error.    */
    iRun = e2fsck_run(ctx);

    if (setjmp(ctx->abort_loc))
    {   /* A fatal error after the passes: the same landing as STEP 4's. */
        ctx->flags &= ~E2F_FLAG_SETJMP_OK;
        BFCK_run.iExit |= FSCK_ERROR;

        if (ctx->fs != NULL)
        {   /* What the filesystem was left as. */
            BFCK_run.bChanged    = (ext2fs_test_changed(ctx->fs) != 0);
            BFCK_run.bErrorsLeft = !ext2fs_test_valid(ctx->fs);
        }

        /* The verdict into the result, then everything released, the   */
        /* filesystem freed without a closing flush.                    */
        pResult->uiExitCode  = (uint32_t)BFCK_run.iExit;
        pResult->bModified   = BFCK_run.bChanged;
        pResult->bErrorsLeft = BFCK_run.bErrorsLeft;

        BFCK_Finish(true);

        if (BFCK_run.bCancelled)
        {   /* The caller's own cancel: the bare code, no text recorded, as at   */
            /* STEP 4's landing (the flag is already released).                  */
            return(BFE_ERR_CANCELLED);
        }

        /* e2fsck ran and gave up; the result and the text say why (or the   */
        /* device dropped the repair's writes, which says more).             */
        return(BFCK_NotKeptStatus(pResult));
    }

    /* Re-armed for the rest of the run. */
    ctx->flags |= E2F_FLAG_SETJMP_OK;

    /* STEP 11: after the passes -- a journal to recreate, an abort or cancel,   */
    /* the orphan file, the quotas, a restart.                                   */
    if (!ctx->invalid_bitmaps && (ctx->flags & E2F_FLAG_JOURNAL_INODE))
    {   /* The passes cleared a broken journal inode: a repair makes a new one. */
        if (fix_problem(ctx, PR_6_RECREATE_JOURNAL, &pctx))
        {   /* Yes (always, in a repair). */
            if (BFCK_run.iJournalSize < 1024)
            {   /* Under 1 GiB, or unknown (-1): the library's default journal   */
                /* for this filesystem's size, as unix.c chooses -- the old      */
                /* size is reused only from 1 GiB up.                            */
                ext2fs_get_journal_params(&jparams, BFCK_run.fs);
            }
            else
            {   /* The old size -- as unix.c does it, which passes the MiB        */
                /* figure where a count of BLOCKS is wanted.                      */
                /* A journal of N >= 1024 MiB is recreated with N blocks          */
                /* (1 GiB becomes 4 MiB at 4 KiB blocks).  THE DEFECT EXISTS      */
                /* AND IS INTENTIONALLY DEFERRED: the size is read after the      */
                /* journal check, and the one place that asks for a new journal   */
                /* zeroes the size's backup with it (e2fsck journal.c clears      */
                /* s_jnl_blocks where it sets E2F_FLAG_JOURNAL_INODE), so the     */
                /* size reads 0 and this branch is not reached in practice -- a   */
                /* recreated journal takes the library's default above.           */
                jparams.num_journal_blocks = BFCK_run.iJournalSize;
                jparams.num_fc_blocks      = 0;
            }

            /* Make it, saying so first, as e2fsck does (~0: wherever the   */
            /* library chooses to put it).                                  */
            log_out(ctx, "Creating journal (%lld blocks): ", (long long)jparams.num_journal_blocks);
            BFCK_run.retval = ext2fs_add_journal_inode3(BFCK_run.fs, &jparams, ~0ULL, 0);

            if (BFCK_run.retval != 0)
            {   /* Could not: said, and the run goes on without one. */
                log_out(ctx, "%s: while trying to create journal\n", error_message(BFCK_run.retval));
                goto no_journal;
            }

            /* Made: e2fsck's two lines, finishing "Creating journal ...". */
            log_out(ctx, "%s", " Done.\n");
            log_out(ctx, "%s", "\n*** journal has been regenerated ***\n");
        }
    }

no_journal:
    if (iRun & E2F_FLAG_ABORT)
    {   /* A pass gave up. */
        fatal_error(ctx, "aborted");
    }
    else if (iRun & E2F_FLAG_CANCEL)
    {   /* The caller cancelled. */
        log_out(ctx, "%s: e2fsck canceled.\n", ctx->device_name);
        BFCK_run.iExit |= FSCK_CANCELED;
        goto cleanup;
    }

    if (ext2fs_has_feature_orphan_file(BFCK_run.fs->super))
    {   /* The orphan file (ext4 since Linux 5.15): checked, or removed when   */
        /* there is no journal for it to serve.                                */
        if (!ext2fs_has_feature_journal(BFCK_run.fs->super) &&
            fix_problem(ctx, PR_6_ORPHAN_FILE_WITHOUT_JOURNAL, &pctx))
        {   /* No journal: the orphan file has no purpose; drop it. */
            BFCK_run.retval = ext2fs_truncate_orphan_file(BFCK_run.fs);

            if (BFCK_run.retval != 0)
            {   /* Could not be emptied: reported, the feature left on (the   */
                /* file is still there), and on to the quotas.                */
                fix_problem(ctx, PR_6_ORPHAN_FILE_TRUNC_FAILED, &pctx);
                goto check_quotas;
            }

            /* Emptied: the feature off, the superblock to be written with   */
            /* it, and on to the quotas.                                     */
            ext2fs_clear_feature_orphan_file(BFCK_run.fs->super);
            ext2fs_mark_super_dirty(BFCK_run.fs);
            goto check_quotas;
        }

        /* e2fsck's look at the orphan file: 0 fine, 1 corrupt, 2 cleaned. */
        iOrphanRet = check_init_orphan_file(ctx);

        if (iOrphanRet == 2 ||
            (iOrphanRet == 0 && ext2fs_has_feature_orphan_present(BFCK_run.fs->super) &&
             fix_problem(ctx, PR_6_ORPHAN_PRESENT_CLEAN_FILE, &pctx)))
        {   /* Clean, or cleaned: the "present" flag comes off. */
            ext2fs_clear_feature_orphan_present(BFCK_run.fs->super);
            ext2fs_mark_super_dirty(BFCK_run.fs);
        }
        else if (iOrphanRet == 1 && fix_problem(ctx, PR_6_ORPHAN_FILE_CORRUPTED, &pctx))
        {   /* Corrupt: recreated. */
            if (ctx->invalid_bitmaps)
            {   /* Not with the bitmaps in doubt. */
                fix_problem(ctx, PR_6_ORPHAN_FILE_BITMAP_INVALID, &pctx);
                goto check_quotas;
            }

            /* Recreate in two steps, as unix.c does: empty the corrupt file,   */
            /* then make a new one of the default size.                         */
            BFCK_run.retval = ext2fs_truncate_orphan_file(BFCK_run.fs);

            if (BFCK_run.retval != 0)
            {   /* Could not empty it: reported, left as it is, and on to the   */
                /* quotas.                                                      */
                fix_problem(ctx, PR_6_ORPHAN_FILE_TRUNC_FAILED, &pctx);
                goto check_quotas;
            }

            /* The new orphan file, announced first as e2fsck announces it. */
            log_out(ctx, "Creating orphan file (%lld blocks): ", (long long)ext2fs_default_orphan_file_blocks(BFCK_run.fs));
            BFCK_run.retval = ext2fs_create_orphan_file(BFCK_run.fs, ext2fs_default_orphan_file_blocks(BFCK_run.fs));

            if (BFCK_run.retval != 0)
            {   /* Could not make a new one: said and reported; the old one   */
                /* is already emptied, and the run goes on to the quotas.     */
                log_out(ctx, "%s: while trying to create orphan file\n", error_message(BFCK_run.retval));
                fix_problem(ctx, PR_6_ORPHAN_FILE_CREATE_FAILED, &pctx);
                goto check_quotas;
            }

            /* Made: the line it announced is finished. */
            log_out(ctx, "%s", " Done.\n");
        }
    }
    else if (ext2fs_has_feature_orphan_present(BFCK_run.fs->super) &&
             fix_problem(ctx, PR_6_ORPHAN_PRESENT_NO_FILE, &pctx))
    {   /* "Present" without a file: the flag comes off. */
        ext2fs_clear_feature_orphan_present(BFCK_run.fs->super);
        ext2fs_mark_super_dirty(BFCK_run.fs);
    }

check_quotas:
    if (ctx->qctx != NULL && !ctx->invalid_bitmaps)
    {   /* Quotas were accounted: each one that exists is compared with what   */
        /* the passes counted and rewritten when it differs.                   */
        for (qtype = 0; qtype < MAXQUOTAS; qtype++)
        {   /* One quota type per iteration. */
            if (*quota_sb_inump(sb, qtype) == 0)
            {   /* Not enabled. */
                continue;
            }

            /* Compare the stored quota with the passes' count: nothing to   */
            /* write out until the comparison says so, and the problem       */
            /* context names this quota type.                                */
            iNeedsWriteout  = 0;
            pctx.num        = qtype;
            BFCK_run.retval = quota_compare_and_update(ctx->qctx, qtype, &iNeedsWriteout);

            if ((BFCK_run.retval != 0 || iNeedsWriteout) && fix_problem(ctx, PR_6_UPDATE_QUOTAS, &pctx))
            {   /* Differs, and the answer is to fix it. */
                pctx.errcode = quota_write_inode(ctx->qctx, 1 << qtype);

                if (pctx.errcode != 0)
                {   /* The write failed. */
                    fix_problem(ctx, PR_6_WRITE_QUOTAS, &pctx);
                }
            }
        }

        /* The quota accounting is done with. */
        quota_release_context(&ctx->qctx);
    }

    if (iRun == E2F_FLAG_RESTART)
    {   /* A pass changed something the earlier passes must see: from the top. */
        log_out(ctx, "%s", "Restarting e2fsck from the beginning...\n");
        BFCK_run.retval = e2fsck_reset_context(ctx);

        if (BFCK_run.retval != 0)
        {   /* Could not. */
            com_err(ctx->program_name, BFCK_run.retval, "%s", "while resetting context");
            fatal_error(ctx, 0);
        }

        /* Close (writing what the passes changed) and open afresh. */
        ext2fs_close_free(&ctx->fs);
        BFCK_run.fs = NULL;
        goto restart;
    }

cleanup:
    /* STEP 12: the verdict.  A repair that finished marks the filesystem   */
    /* clean (or not), stamps the check time and writes everything out; a   */
    /* report-only run writes nothing.                                      */
    BFCK_run.bChanged = (ext2fs_test_changed(BFCK_run.fs) != 0);

    if (!(ctx->flags & E2F_FLAG_RUN_RETURN) && !(ctx->options & E2F_OPT_READONLY))
    {   /* A repair that ran to the end. */
        if (ext2fs_test_valid(BFCK_run.fs))
        {   /* Clean now. */
            if (!(sb->s_state & EXT2_VALID_FS))
            {   /* It was not before: that is a correction. */
                BFCK_run.iExit |= FSCK_NONDESTRUCT;
            }

            /* Recorded as clean in the superblock -- only this bit: the      */
            /* error bit a kernel may have set is replaced, as unix.c does.   */
            sb->s_state = EXT2_VALID_FS;

            if (check_backup_super_block(ctx))
            {   /* The backups differ from the primary: update them too. */
                BFCK_run.fs->flags &= ~EXT2_FLAG_MASTER_SB_ONLY;
            }
        }
        else
        {   /* Still has errors. */
            sb->s_state &= ~EXT2_VALID_FS;
        }

        if (!(ctx->flags & E2F_FLAG_TIME_INSANE))
        {   /* The clock is believable: the check time. */
            ext2fs_set_tstamp(sb, s_lastcheck, ctx->now);
        }

        /* As unix.c ends a repair: the mount count back to 0, the kernel's   */
        /* recorded-error fields cleared, the descriptor checksums set.       */
        sb->s_mnt_count = 0;
        memset(((char *)sb) + EXT4_S_ERR_START, 0, EXT4_S_ERR_LEN);
        pctx.errcode = ext2fs_set_gdt_csum(ctx->fs);

        if (pctx.errcode != 0)
        {   /* The descriptor checksums could not be set. */
            fix_problem(ctx, PR_6_SET_BG_CHECKSUM, &pctx);
        }

        /* So the superblock is written with these. */
        ext2fs_mark_super_dirty(BFCK_run.fs);
    }

    if (ext2fs_has_feature_shared_blocks(ctx->fs->super) &&
        (ctx->options & E2F_OPT_UNSHARE_BLOCKS) && (ctx->options & E2F_OPT_NO))
    {   /* unix.c's dry run of unsharing: nothing written (never set here,   */
        /* kept for fidelity).                                               */
        goto skip_write;
    }

    if (!(ctx->options & E2F_OPT_READONLY))
    {   /* A repair: bitmaps, then everything dirty, then the device. */
        e2fsck_write_bitmaps(ctx);

        if (BFCK_run.fs->flags & EXT2_FLAG_DIRTY)
        {   /* Something is still unwritten. */
            pctx.errcode = ext2fs_flush(ctx->fs);

            if (pctx.errcode != 0)
            {   /* The flush failed. */
                fix_problem(ctx, PR_6_FLUSH_FILESYSTEM, &pctx);
            }
        }

        /* The channel's flush: the target's, through the io manager. */
        pctx.errcode = io_channel_flush(ctx->fs->io);

        if (pctx.errcode != 0)
        {   /* The device did not confirm. */
            fix_problem(ctx, PR_6_IO_FLUSH, &pctx);
        }
    }

    if (BFCK_run.bChanged)
    {   /* Something was written: say so, as e2fsck does. */
        if (ctx->flags & E2F_FLAG_PROBLEMS_FIXED)
        {   /* By a correction. */
            BFCK_run.iExit |= FSCK_NONDESTRUCT;
        }

        /* e2fsck's own banner, which the caller's report shows as it is. */
        log_out(ctx, "\n%s: ***** FILE SYSTEM WAS MODIFIED *****\n", ctx->device_name);
    }

skip_write:
    if (!ext2fs_test_valid(BFCK_run.fs) ||
        ((BFCK_run.iExit & FSCK_CANCELED) && (sb->s_state & EXT2_ERROR_FS)))
    {   /* Errors remain -- every one a report-only run found, or what a   */
        /* repair could not fix.                                           */
        log_out(ctx, "\n%s: ********** WARNING: Filesystem still has errors **********\n\n", ctx->device_name);
        BFCK_run.iExit |= FSCK_UNCORRECTED;
        BFCK_run.iExit &= ~FSCK_NONDESTRUCT;
    }

    if (BFCK_run.iExit & FSCK_CANCELED)
    {   /* A cancel is not a correction, whatever else happened. */
        BFCK_run.iExit &= ~FSCK_NONDESTRUCT;
    }
    else
    {   /* The closing line and the figures. */
        BFCK_Stats(ctx);
    }

    /* The verdict into the caller's result: e2fsck's exit bits, whether   */
    /* anything was written, and whether errors remain.                    */
    BFCK_run.bErrorsLeft = !ext2fs_test_valid(BFCK_run.fs);

    /* Into the caller's structure.  The figures are already held in     */
    /* BFCK_run, so filling it before BFCK_Finish is not forced by the   */
    /* close; BFCK_NotKeptStatus may still amend it below.               */
    pResult->uiExitCode  = (uint32_t)BFCK_run.iExit;
    pResult->bModified   = BFCK_run.bChanged;
    pResult->bErrorsLeft = BFCK_run.bErrorsLeft;

    /* The filesystem is closed properly here -- a repair's last writes made   */
    /* real -- and everything else released.                                   */
    ctx->flags &= ~E2F_FLAG_SETJMP_OK;
    BFCK_Finish(false);

    if (BFCK_run.bCancelled)
    {   /* The caller's cancel: the result is filled, the code says cancelled. */
        return(BFE_ERR_CANCELLED);
    }

    /* e2fsck ran to the end; the verdict is in the result -- unless the   */
    /* device dropped the repair's writes.                                 */
    return(BFCK_NotKeptStatus(pResult));
}
