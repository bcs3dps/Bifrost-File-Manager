/*----------------------------------------------------------------------------*/
/* bifrost_engine.c                                                           */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The BifrostFM engine's session, browse and edit functions, and the         */
/* internals every engine file shares (the BFX_ functions bfe_internal.h      */
/* declares).  The rest of the public API lives beside it: the three copies   */
/* (BFE_Extract, BFE_Import, BFE_CopyWithin) in bfe_copy.c, formatting in     */
/* bfe_mkfs.c, the device and raw-image calls in bfe_image.c, the batched     */
/* raw write in bfe_rawbatch.c, the check in bfe_fsck.c, the resize in        */
/* bfe_resize.c and the capacity test in bfe_capacity.c.  Every one of them   */
/* passes the gate here first (BFX_Enter, or BFX_EnterAnyFs for a call that   */
/* answers for a FAT session too).                                            */
/*                                                                            */
/* HOW A SESSION WORKS.  BFE_Open mounts the image ONCE and it stays mounted  */
/* until BFE_Close, so browsing costs a directory read rather than a mount    */
/* per click.  A writable session recovers and starts the journal and puts    */
/* lwext4's block cache in write-back mode, so a batch of changes is fast;    */
/* BFE_Flush (and BFE_Close) is what commits them to the image file.          */
/*                                                                            */
/* WHY EVERY CALL IS SERIALISED.  lwext4 keeps the mount, the block cache and */
/* the journal in static state and is given no lock functions, so two calls   */
/* at once would corrupt it.  Rather than trust every caller, each public     */
/* function passes BFX_Enter first, which takes one atomic busy flag and      */
/* returns BFE_ERR_BUSY if it is already held.                                */
/*                                                                            */
/* A FAT SESSION.  A range that holds no ext2/3/4 filesystem is tried as FAT  */
/* or exFAT, through FatFs (bfe_fat.c), read-only or writable as asked.  It   */
/* answers every session call taught FAT (they pass BFX_EnterAnyFs and reach  */
/* FatFs through bfe_fs.c and the FAT arms); the gate (BFX_Enter) refuses the */
/* rest -- modes, owners, links -- with BFE_ERR_UNSUPPORTED, so a call that   */
/* has not been taught FAT can never reach lwext4 with no lwext4 mount        */
/* behind it.                                                                 */
/*                                                                            */
/* A DEVICE AS THE IMAGE.  Since API version 4 the path BFE_Open takes may    */
/* name a real disk or volume.  The block device underneath treats the two    */
/* alike; what this file adds for a device opened for WRITING is the guard    */
/* that the system disk is never written and the locking of every volume on   */
/* the device's disk for the whole session (bfe_device.c has both), released  */
/* at BFE_Close.                                                              */
/*                                                                            */
/* HOW A FAILURE IS REPORTED.  Every failing public call records a detail     */
/* line with BFX_Fail and then gives the busy flag back -- see BFX_Fail.      */
/*----------------------------------------------------------------------------*/

/* Windows first: bfe_device.h uses its types (HANDLE and the rest).  Why   */
/* lwext4's headers are kept after it rather than before is not recorded.   */
#include <windows.h>

/* lwext4: the mount, journal and cache calls, the directory walk and the     */
/* raw inode read, the inode and superblock accessors, and the on-disk        */
/* structure and constant definitions they use.  Its file-level changes and   */
/* link read are reached through bfe_fs.h instead.                            */
#include <ext4.h>
#include <ext4_inode.h>
#include <ext4_super.h>
#include <ext4_types.h>
#include <ext4_misc.h>

/* The C library: formatted text (the error line), memory, strings, the        */
/* variable argument list BFX_Fail takes, bool for lwext4's calls, the errno   */
/* values lwext4 returns, the clock for new times, and wide strings.           */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdarg.h>
#include <stdbool.h>
#include <errno.h>
#include <time.h>
#include <wchar.h>

/* The engine's own: the shared internals, the block device lwext4 reads and   */
/* writes through, the raw device layer (its system-disk guard and volume      */
/* locks), the FAT session's calls (bfe_fat.c), and the filesystem-neutral     */
/* file calls (bfe_fs.c) every file-level change and link read goes through.   */
#include "bfe_internal.h"
#include "bfe_blockdev.h"
#include "bfe_device.h"
#include "bfe_fat.h"
#include "bfe_fs.h"

/*----------------------------------------------------------------------------*/
/* Definitions -- documentation table.                                        */
/*----------------------------------------------------------------------------*/
#define BFX_DEVICE_NAME    "bifrost"        // the name the block device is registered under in lwext4
#define BFX_ERROR_BYTES    512              // room for the last-error detail line, NUL included
#define BFX_NAMES_FIRST    128              // initial capacity of a collected name list; doubles as it fills

/*----------------------------------------------------------------------------*/
/* Session state.  Guarded by the busy flag: only the call that holds it      */
/* reads or writes any of this.  (The flag itself is the exception: it is     */
/* touched only through GCC's atomic builtins, never by a plain read.)        */
/*                                                                            */
/* BFX_Enter's own refusals write BFX_szLastError just AFTER releasing        */
/* the flag, so for that one write the rule above does not hold.  THE DEFECT  */
/* EXISTS AND IS INTENTIONALLY DEFERRED: the program makes every engine call  */
/* from its one worker thread, so no second call can enter in between.        */
/*----------------------------------------------------------------------------*/
static volatile int        BFX_iBusy;                        // 1 while a public call is running; taken and released atomically
static int                 BFX_bOpen;                        // TRUE while an image is mounted
static int                 BFX_iFsKind;                      // BFX_FS_EXT or BFX_FS_FAT: which library holds the open session's filesystem; read only while BFX_bOpen
static int                 BFX_bReadOnly;                    // TRUE when the open session was opened read-only
static int                 BFX_bJournalOn;                   // TRUE while a writable session's journal is started and not stopped since -- what BFE_Flush and BFE_Close stop; FALSE on a read-only session, and once a stop or restart failed (lwext4 has then let the journal go)
static int                 BFX_bChangesBarred;               // TRUE once a flush could not checkpoint or restart the journal: every further change of the session is refused (BFX_Enter), until it is closed
static struct ext4_sblock *BFX_pSuperblock;                  // the mounted superblock; valid only while BFX_bOpen
static BFV_Locks           BFX_locks;                        // the volumes locked for a writable DEVICE session; empty otherwise
static int                 BFX_bVerifySessions;              // TRUE: a session's device writes are read back (BFE_SetWriteVerify); FALSE by default
static int                 BFX_bSessionCall;                 // TRUE while the running call works inside the open session (BFX_Enter's bNeedOpen); read by BFX_Fail
static uint32_t            BFX_uiEnterGeneration;            // BFD_Generation() as the running call entered: a different value later means the call configured the block device itself; read by BFX_Fail
static char                BFX_szLastError[BFX_ERROR_BYTES]; // detail line of the last failure, UTF-8

/*------------------------------------------------------------------------------*/
/* BFX_Fail:                                                                    */
/*                                                                              */
/* Record a detail line for the failure about to be returned, and hand the      */
/* status code back so a failure path is a single statement.  The line is       */
/* what BFE_LastErrorText gives the GUI to show beside the generic message      */
/* for the code: it names the path and lwext4's own reason.                     */
/*                                                                              */
/* Three facts lwext4 would hide (it passes only an errno up) are folded in,    */
/* each at its own lines below: the device's account of a failed read or        */
/* write, an lwext4 consistency check that failed, and a device write that      */
/* was not kept -- which can turn the code handed back into BFE_ERR_VERIFY,     */
/* so the user is told the card did not keep what was written rather than       */
/* that the filesystem is damaged.                                              */
/*                                                                              */
/* THE RULE EVERY FAILURE PATH IN THE ENGINE FOLLOWS: call this BEFORE giving   */
/* the busy flag back (BFX_Leave), never after.  While the flag is held no      */
/* other call can write the line, so the line the caller then reads is its      */
/* own call's.  Written after the release, another call could overwrite it in   */
/* between and the user would be shown the wrong reason.  (BFX_Enter's refusals */
/* are the one place the order is reversed -- a defect that exists and is       */
/* intentionally deferred: the program's one worker thread never meets it.)     */
/*                                                                              */
/* Arguments:                                                                   */
/*     iCode     : the BFE_ERR_* code being returned.                           */
/*     pszFormat : printf-style format for the detail line.                     */
/*     ...       : the format's arguments.                                      */
/*                                                                              */
/* Returns:                                                                     */
/*     int : iCode, or BFE_ERR_VERIFY in its place when a device write of       */
/*           this operation was not kept (the lost-write test in the body).     */
/*------------------------------------------------------------------------------*/
int BFX_Fail(int iCode, const char *pszFormat, ...)
{
    va_list     args;
    size_t      cbUsed;
    const char *pszAssertFile;   // the failed lwext4 assertion's __FILE__, or NULL for none
    const char *pszAssertName;   // that path's last component
    const char *pszScan;         // walks the path looking for separators
    int         iAssertLine;     // the assertion's __LINE__

    /* Format the caller's line into the last-error buffer, replacing the    */
    /* previous one; vsnprintf cuts it at the buffer's size, which can       */
    /* split a UTF-8 character in a long path -- the reader then shows one   */
    /* garbled character at the end, nothing worse.                          */
    va_start(args, pszFormat);
    vsnprintf(BFX_szLastError, sizeof(BFX_szLastError), pszFormat, args);
    va_end(args);

    /* vsnprintf terminates within the size given, but say so explicitly. */
    BFX_szLastError[sizeof(BFX_szLastError) - 1] = '\0';

    /* A device-level failure underneath -- which lwext4 reports only as   */
    /* "I/O error" -- is appended in its own words, then forgotten.        */
    if (BFD_LastIoText()[0] != '\0')
    {   /* Something failed in the block device since the last report. */

        /* Append it in brackets after the caller's line, in whatever room   */
        /* is left (it is cut, never allowed to overrun), then clear it so   */
        /* the next failure does not repeat it.                              */
        cbUsed = strlen(BFX_szLastError);
        snprintf(BFX_szLastError + cbUsed, sizeof(BFX_szLastError) - cbUsed, " [%s]", BFD_LastIoText());
        BFX_szLastError[sizeof(BFX_szLastError) - 1] = '\0';
        BFD_ClearIoText();
    }

    /* Likewise a consistency check inside lwext4 that failed: lwext4's   */
    /* assertion records its site and lets the code go on to its error    */
    /* return, never stopping there, so the site is shown here, once,     */
    /* after the caller's line -- the file's own name only, not the       */
    /* build's path.                                                      */
    pszAssertFile = ext4_assert_last(&iAssertLine);

    if (pszAssertFile != NULL)
    {   /* An assertion failed since the last report. */

        /* The name after the last separator of either kind. */
        pszAssertName = pszAssertFile;

        for (pszScan = pszAssertFile; *pszScan != '\0'; pszScan++)
        {   /* One character of the recorded path per iteration. */
            if (*pszScan == '\\' || *pszScan == '/')
            {   /* A separator: the name starts after it. */
                pszAssertName = pszScan + 1;
            }
        }

        /* Append it as the device's account is appended, then forget it. */
        cbUsed = strlen(BFX_szLastError);
        snprintf(BFX_szLastError + cbUsed, sizeof(BFX_szLastError) - cbUsed,
                 " [lwext4 consistency check failed at %s line %d]", pszAssertName, iAssertLine);
        BFX_szLastError[sizeof(BFX_szLastError) - 1] = '\0';
        ext4_assert_clear();
    }

    /* The dropped-write verdict is sticky until the block device is next     */
    /* configured, so a failure reported long after the write that was lost   */
    /* is still traced back to it -- but only in a call that the lost write   */
    /* belongs to.  That is a call inside the open session (a session's       */
    /* calls share its one configuration, from BFE_Open to BFE_Close), or a   */
    /* call that configured the block device after it entered (an open, a     */
    /* format, a check, a resize), whose configuration cleared the verdict,   */
    /* so a lost write found now is its own.  Any other call -- the raw       */
    /* reads, writes and copies, which never drive the block device, and an   */
    /* operation's own refusals before it configures it -- keeps its code     */
    /* whatever an earlier operation left in the verdict.  Only the three     */
    /* codes a lost write can produce are rewritten; a refusal such as a      */
    /* missing path or a full filesystem keeps its own meaning.  NULL: the    */
    /* offset of the lost write is not wanted here.                           */
    if (BFD_WriteNotKept(NULL) &&
        (BFX_bSessionCall || BFD_Generation() != BFX_uiEnterGeneration) &&
        (iCode == BFE_ERR_IO_IMAGE || iCode == BFE_ERR_JOURNAL || iCode == BFE_ERR_MOUNT))
    {   /* An I/O failure in an operation where the device dropped a write:   */
        /* the dropped write is the cause, and the caller is told so by       */
        /* the code that means "what was read back differs".                  */
        iCode = BFE_ERR_VERIFY;
    }

    /* The caller returns this code to the GUI. */
    return(iCode);
}

/*----------------------------------------------------------------------------*/
/* Bfx_EnterGate:                                                             */
/*                                                                            */
/* The gate at the top of every public function, reached through BFX_Enter    */
/* or BFX_EnterAnyFs.  It takes the busy flag, then checks that the session   */
/* is in the state the function needs.  If either fails it releases the flag  */
/* again itself, so the caller's whole failure path is "return the code".     */
/*                                                                            */
/* A call that needs the open session and has not said it handles FAT         */
/* (bAnyFs FALSE) is refused on a FAT session, so code written for lwext4     */
/* alone never runs with no lwext4 mount behind it.                           */
/*                                                                            */
/* Arguments:                                                                 */
/*     bNeedOpen  : TRUE when the function needs an image to be open.         */
/*     bNeedWrite : TRUE when the function modifies the image.                */
/*     bAnyFs     : TRUE when the function answers for a FAT session as well  */
/*                  as an ext2/3/4 one.                                       */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK with the busy flag HELD (the caller must BFX_Leave), or   */
/*           BFE_ERR_BUSY / BFE_ERR_NOT_OPEN / BFE_ERR_READ_ONLY with the     */
/*           flag not held -- or BFE_ERR_JOURNAL for a modifying call on a    */
/*           session whose journal a flush lost (BFX_bChangesBarred), or      */
/*           BFE_ERR_UNSUPPORTED for a call not taught FAT on a FAT session.  */
/*----------------------------------------------------------------------------*/
static int Bfx_EnterGate(int bNeedOpen, int bNeedWrite, int bAnyFs)
{
    /* Take the flag: set it to 1 atomically and look at what it was.  A       */
    /* plain "if (!busy) busy = 1" would let two threads both see 0 and both   */
    /* enter; the builtin makes the test and the set one indivisible step,     */
    /* with an acquire barrier so nothing the call does moves before it.       */
    if (__sync_lock_test_and_set(&BFX_iBusy, 1) != 0)
    {   /* The flag was already 1: another call is inside the engine.  The   */
        /* last-error text is that call's to write, so it is left alone.     */
        return(BFE_ERR_BUSY);
    }

    /* The call owns the engine now, so the two notes BFX_Fail folds into a   */
    /* failure's detail line start empty: the block device's account of a     */
    /* failed read or write, and lwext4's failed-check site.  BFX_Fail        */
    /* clears each only when it reports a failure, so without this a note     */
    /* left by an earlier call that SUCCEEDED (or reported through a          */
    /* result, not a status) would be appended to this call's failure,        */
    /* however unrelated.  Cleared here -- after the flag is won, never by    */
    /* a busy refusal, and before this call's own refusals below write        */
    /* theirs.                                                                */
    BFD_ClearIoText();
    ext4_assert_clear();

    /* What this call is, for BFX_Fail's lost-write rule: a call that needs   */
    /* an open image works inside the session, and shares the session's       */
    /* lost-write verdict; any other call owns only a verdict from a          */
    /* configuration it makes itself, which the count taken here reveals.     */
    /* Set before the refusals below, which report through BFX_Fail too.      */
    BFX_bSessionCall      = bNeedOpen;
    BFX_uiEnterGeneration = BFD_Generation();

    /* Every refusal below releases the flag BEFORE BFX_Fail writes the         */
    /* last-error line, so another thread could enter in between.  THE DEFECT   */
    /* EXISTS AND IS INTENTIONALLY DEFERRED: the program makes every engine     */
    /* call from its one worker thread.                                         */
    if (bNeedOpen && !BFX_bOpen)
    {   /* No image is open. */

        /* Refuse, releasing the flag here so the caller has nothing to   */
        /* give back.                                                     */
        __sync_lock_release(&BFX_iBusy);
        return(BFX_Fail(BFE_ERR_NOT_OPEN, "no image is open"));
    }

    if (bNeedOpen && !bAnyFs && BFX_iFsKind == BFX_FS_FAT)
    {   /* A session call written for lwext4 alone, on a FAT session: it      */
        /* would ask lwext4 about a mount that does not exist.  The calls     */
        /* left are those for modes, owners and links, which FAT has no       */
        /* place for, and the message says so.  Refused before the            */
        /* read-only test below, so the user is told what is missing rather   */
        /* than only that the session cannot be written.                      */

        /* Refuse, releasing the flag here as above. */
        __sync_lock_release(&BFX_iBusy);
        return(BFX_Fail(BFE_ERR_UNSUPPORTED, "FAT and exFAT have no Unix modes, owners or links"));
    }

    if (bNeedWrite && BFX_bReadOnly)
    {   /* A modifying call on a session opened to look, not to change. */

        /* Refuse, releasing the flag here as above. */
        __sync_lock_release(&BFX_iBusy);
        return(BFX_Fail(BFE_ERR_READ_ONLY, "the image is open read-only"));
    }

    if (bNeedWrite && BFX_bChangesBarred)
    {   /* A modifying call after a flush lost the journal (BFE_Flush).  A    */
        /* change made now would be written with no journal -- and over one   */
        /* that may still hold transactions a later replay would lay over     */
        /* it -- so none is made.  JOURNAL, not READ_ONLY: the session was    */
        /* opened for writing, and what stopped it is the journal.            */

        /* Refuse, releasing the flag here as above. */
        __sync_lock_release(&BFX_iBusy);
        return(BFX_Fail(BFE_ERR_JOURNAL, "the journal stopped at the last flush, so this session takes no more changes; "
                                         "close the image and check it before writing to it again"));
    }

    /* The call may proceed; it owns the engine until BFX_Leave. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFX_Enter:                                                                 */
/*                                                                            */
/* The gate for a public function that works on ext2/3/4 alone -- the calls   */
/* for modes, owners and links, which FAT has no place for, and the calls     */
/* that need no session -- so on a FAT session a session call is refused at   */
/* the door (Bfx_EnterGate says how and why).                                 */
/*                                                                            */
/* Arguments:                                                                 */
/*     bNeedOpen  : TRUE when the function needs an image to be open.         */
/*     bNeedWrite : TRUE when the function modifies the image.                */
/*                                                                            */
/* Returns:                                                                   */
/*     int : Bfx_EnterGate's result: BFE_OK with the busy flag held, or a     */
/*           refusal with it released.                                        */
/*----------------------------------------------------------------------------*/
int BFX_Enter(int bNeedOpen, int bNeedWrite)
{
    /* The gate, refusing a FAT session for a call that needs one open. */
    return(Bfx_EnterGate(bNeedOpen, bNeedWrite, FALSE));
}

/*----------------------------------------------------------------------------*/
/* BFX_EnterAnyFs:                                                            */
/*                                                                            */
/* The gate for a public function that answers for a FAT session as well as   */
/* an ext2/3/4 one -- it tests BFX_iFsKind itself and sends a FAT session's   */
/* work to bfe_fat.c.                                                         */
/*                                                                            */
/* Arguments:                                                                 */
/*     bNeedOpen  : TRUE when the function needs an image to be open.         */
/*     bNeedWrite : TRUE when the function modifies the image.                */
/*                                                                            */
/* Returns:                                                                   */
/*     int : Bfx_EnterGate's result: BFE_OK with the busy flag held, or a     */
/*           refusal with it released.                                        */
/*----------------------------------------------------------------------------*/
int BFX_EnterAnyFs(int bNeedOpen, int bNeedWrite)
{
    /* The gate, admitting either kind of session. */
    return(Bfx_EnterGate(bNeedOpen, bNeedWrite, TRUE));
}

/*----------------------------------------------------------------------------*/
/* BFX_Leave:                                                                 */
/*                                                                            */
/* Release the busy flag taken by a successful BFX_Enter.                     */
/*                                                                            */
/* Arguments:                                                                 */
/*     None.                                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     void : the engine is free for the next call.                           */
/*----------------------------------------------------------------------------*/
void BFX_Leave(void)
{
    /* Store 0 with a release barrier: everything the call wrote is visible   */
    /* before another call can see the flag free.                             */
    __sync_lock_release(&BFX_iBusy);
}

/*----------------------------------------------------------------------------*/
/* BFX_IsOpen:                                                                */
/*                                                                            */
/* Whether an image is mounted.  BFX_Enter can demand that one IS open; this  */
/* is for the functions that need the opposite -- a format, a check, a resize */
/* and the capacity test (bfe_mkfs.c, bfe_fsck.c, bfe_resize.c,               */
/* bfe_capacity.c) all use the block device a session would be holding.       */
/* Call it only while holding the busy flag, like every other read of the     */
/* session state.                                                             */
/*                                                                            */
/* Arguments:                                                                 */
/*     None.                                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     int : TRUE while an image is open, FALSE otherwise.                    */
/*----------------------------------------------------------------------------*/
int BFX_IsOpen(void)
{
    /* The flag BFE_Open sets and BFE_Close clears. */
    return(BFX_bOpen);
}

/*----------------------------------------------------------------------------*/
/* BFX_FsKind:                                                                */
/*                                                                            */
/* Which library holds the open session's filesystem, for the switches of     */
/* bfe_fs.c, bfe_copy.c's FAT tests and this file's own.  With no session     */
/* open the answer is ext2/3/4, the kind every                                */
/* call that runs without a session (a format, a check) works on.  Call it    */
/* only while holding the busy flag, like every other read of the state.      */
/*                                                                            */
/* Arguments:                                                                 */
/*     None.                                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFX_FS_FAT for an open FAT or exFAT session, else BFX_FS_EXT.    */
/*----------------------------------------------------------------------------*/
int BFX_FsKind(void)
{
    if (BFX_bOpen && BFX_iFsKind == BFX_FS_FAT)
    {   /* A FAT session is open. */
        return(BFX_FS_FAT);
    }

    /* An ext2/3/4 session, or none. */
    return(BFX_FS_EXT);
}

/*----------------------------------------------------------------------------*/
/* BFX_MapErrno:                                                              */
/*                                                                            */
/* Translate an lwext4 result (an errno value, 0 for success) into the        */
/* engine's own status code.  Only the results a file manager acts on         */
/* differently get their own code; everything else is "image I/O failure",    */
/* with lwext4's wording kept in the detail line by the caller.               */
/*                                                                            */
/* EINVAL -- which lwext4 uses for a caller's mistake, such as a hard         */
/* link to a directory -- has no case and so becomes BFE_ERR_IO_IMAGE; the    */
/* FAT side gives EINVAL too, for a name or an argument FatFs refuses         */
/* (bfe_fat.c BFT_FatErrno).  THE DEFECT EXISTS AND IS INTENTIONALLY          */
/* DEFERRED: the program reaches neither -- it makes no hard links, and       */
/* BFX_PathValid refuses a name FAT forbids before FatFs sees it.             */
/*                                                                            */
/* Arguments:                                                                 */
/*     rc : an lwext4 return value.                                           */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK for 0, otherwise the matching BFE_ERR_* code.             */
/*----------------------------------------------------------------------------*/
int BFX_MapErrno(int rc)
{
    switch (rc)
    {   /* One lwext4 result per case. */

        /* Success. */
        case 0:         return(BFE_OK);

        /* No such path. */
        case ENOENT:    return(BFE_ERR_NOT_FOUND);

        /* The name is taken. */
        case EEXIST:    return(BFE_ERR_EXISTS);

        /* A path component is not a directory. */
        case ENOTDIR:   return(BFE_ERR_NOT_DIR);

        /* A file operation on a directory. */
        case EISDIR:    return(BFE_ERR_IS_DIR);

        /* Removing a directory that has entries. */
        case ENOTEMPTY: return(BFE_ERR_NOT_EMPTY);

        /* No free blocks or inodes. */
        case ENOSPC:    return(BFE_ERR_NO_SPACE);

        /* lwext4 itself refused a write. */
        case EROFS:     return(BFE_ERR_READ_ONLY);

        /* lwext4 could not allocate. */
        case ENOMEM:    return(BFE_ERR_NO_MEMORY);

        /* Anything else: an I/O or consistency failure (EINVAL too:   */
        /* a defect intentionally deferred -- see the header).         */
        default:        return(BFE_ERR_IO_IMAGE);
    }
}

/*----------------------------------------------------------------------------*/
/* BFX_PathValid:                                                             */
/*                                                                            */
/* Check an image path argument before it reaches the filesystem library, so  */
/* that every path the engine works with is ABSOLUTE and CANONICAL -- one     */
/* spelling per entry, which the engine's tests that one path is, or lies     */
/* inside, another (a copy onto itself or into its own subtree, a rename into */
/* its own subtree) rely on, as they compare paths as text.  Refusing here,   */
/* once, covers every public call that takes an image path, whoever built     */
/* it.  On a FAT or exFAT session FAT's own name rules apply as well; on      */
/* ext2/3/4 a name is its bytes.                                              */
/*                                                                            */
/* This is the one read of the session's state made before a call's gate:     */
/* the engine is driven from one worker thread (bifrost_engine.h's contract), */
/* so nothing can change it in between.                                       */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImagePath : the caller's path; may be NULL.                         */
/*                                                                            */
/* Returns:                                                                   */
/*     int : TRUE when the path is non-NULL, absolute, and is "/" or has no   */
/*           empty, "." or ".." component -- nor, on a FAT session, one made  */
/*           of spaces and dots alone or one holding a character FAT forbids; */
/*           FALSE otherwise.                                                 */
/*----------------------------------------------------------------------------*/
int BFX_PathValid(const char *pszImagePath)
{
    const char   *pszComponent;
    size_t        cbComponent;
    size_t        i;
    unsigned char ucChar;
    int           bFat;

    if (pszImagePath == NULL || pszImagePath[0] != '/')
    {   /* Missing, empty, or relative -- refused.  On ext2/3/4 lwext4 finds   */
        /* the mount by matching the START of the path against the mount       */
        /* point "/", so a relative path would match no mount and come back    */
        /* as "not found", telling the user a file is missing when the         */
        /* fault is the caller's argument.                                     */
        return(FALSE);
    }

    if (pszImagePath[1] == '\0')
    {   /* The root, "/": valid, and the only path that ends in '/'. */
        return(TRUE);
    }

    /* Whether the FAT rule applies: a FAT or exFAT session is open. */
    bFat = (BFX_FsKind() == BFX_FS_FAT);

    /* Each component in turn: the text after a '/' up to the next '/' or   */
    /* the end.  Every '/' must be followed by a component, so the loop     */
    /* runs at least once and ends only at the end of the string.           */
    pszComponent = pszImagePath + 1;

    for (;;)
    {   /* One component per iteration: the one that starts here. */
        cbComponent = strcspn(pszComponent, "/");

        if (cbComponent == 0)
        {   /* An empty component: "//" inside the path, or a trailing "/" --     */
            /* another spelling of an entry, refused.  Inside, lwext4's walk      */
            /* (ext4.c, ext4_generic_open2) would refuse it anyway with ENOENT;   */
            /* at the END it would not -- an empty last component ends the        */
            /* walk, so "/d/" opens "/d", a second spelling the text tests miss   */
            /* ("/d/" to "/d/x" is not "the source plus '/'"), and the walk's     */
            /* name then points at the empty name, so a rename (ext4_frename)     */
            /* adds the new link and fails to remove the old one, and a copy      */
            /* from "/d/" onto "/d" copies each file onto itself.                 */
            return(FALSE);
        }

        if ((cbComponent == 1 && pszComponent[0] == '.') ||
            (cbComponent == 2 && pszComponent[0] == '.' && pszComponent[1] == '.'))
        {   /* "." or "..": another spelling of an entry, refused.  lwext4    */
            /* resolves them through the directories' own "." and ".."        */
            /* entries, so "/d/./f" and "/d/x/../f" would name "/d/f" under   */
            /* other strings.                                                 */
            return(FALSE);
        }

        if (bFat && strspn(pszComponent, " .") >= cbComponent)
        {   /* A FAT name of spaces and dots alone ("...", " ", ". ").  On FAT   */
            /* a name's TRAILING spaces and dots are not part of it -- FatFs     */
            /* drops them (ff.c, create_name), as Windows' path handling does,   */
            /* so "/notes." names "/notes" and is accepted, the text tests       */
            /* comparing BFX_PathCanonical forms -- but here nothing would be    */
            /* left: an empty name, which FatFs refuses too (FR_INVALID_NAME)    */
            /* and the canonical form could not spell.  Refused.  strspn         */
            /* counts from the component's start and may run on past its '/',    */
            /* so it is the count reaching the component's length that says      */
            /* "all".                                                            */
            return(FALSE);
        }

        if (bFat)
        {   /* A FAT name: each of its bytes held to the characters FAT          */
            /* forbids in a name -- " * : < > ? | \, a control character or      */
            /* DEL -- the whole set the program's own name boxes hold a FAT      */
            /* name to.  Three FatFs reads as something other than a name: a     */
            /* ':' marks a drive (ff.c, get_ldnumber: the text before the        */
            /* path's first ':' is a drive number, so "/a:b" would fail as "no   */
            /* such drive", an I/O error, not a refusal of the name); a '\' is   */
            /* a separator, as '/' is (IsSeparator), so "/a\b" would name        */
            /* "/a/b", a spelling the text tests miss; and a control character   */
            /* ends the path (create_name stops below a space), what follows     */
            /* dropped without a word.  The rest FatFs refuses itself.  Only     */
            /* bytes below 0x80 can be one of them --                            */
            /* every byte of a UTF-8 character outside ASCII is 0x80 or          */
            /* more -- so a name in any script passes on its own merits.         */
            for (i = 0; i < cbComponent; i++)
            {   /* One byte of the component per iteration. */
                ucChar = (unsigned char)pszComponent[i];

                if (ucChar < 0x20 || ucChar == 0x7F ||
                    strchr("\"*:<>?|\\", (int)ucChar) != NULL)
                {   /* A character FAT forbids: ':' a drive to FatFs, '\' a    */
                    /* separator, a control character the path's end, the      */
                    /* rest names FatFs refuses -- refused.  The byte is       */
                    /* never 0 here (the component stops before the NUL), so   */
                    /* strchr cannot match the string's own terminator.        */
                    return(FALSE);
                }
            }
        }

        /* On past the component. */
        pszComponent += cbComponent;

        if (*pszComponent == '\0')
        {   /* The last component ended the string: all checked. */
            break;
        }

        /* A '/': the next component starts after it (and must exist). */
        pszComponent++;
    }

    /* An absolute, canonical path the session's library can resolve. */
    return(TRUE);
}

/*----------------------------------------------------------------------------*/
/* BFX_PathCanonical:                                                         */
/*                                                                            */
/* The one spelling of an entry's path that its filesystem matches it by, for */
/* the engine's tests that compare paths as text (a copy onto itself, a copy  */
/* or a move into its own subtree, a copy onto a folder that holds it, a      */
/* rename to another spelling of the same entry).  On a FAT or exFAT session  */
/* each component's trailing spaces and dots are dropped, as FatFs and        */
/* Windows drop them, so "/Notes./a " and "/Notes/a" come out alike and the   */
/* tests cannot miss that they name one entry.  Letter case is left as it     */
/* is: the FAT callers compare the canonical forms with FatFs's own fold      */
/* (bfe_fat.c BFT_PathFoldPrefix, ff_wtoupper), which covers every letter     */
/* FatFs folds, not only ASCII.  On ext2/3/4 the copy is exact, a name there  */
/* being its bytes.                                                           */
/*                                                                            */
/* The path must already have passed BFX_PathValid, which on FAT refuses a    */
/* component of spaces and dots alone -- so no component here comes out       */
/* empty.  The copy is for comparing only: the call itself goes on with the   */
/* caller's own path, which FatFs reads the same way.  Call it holding the    */
/* busy flag, like every other read of the session's state.                   */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImagePath : a path BFX_PathValid accepted.                          */
/*                                                                            */
/* Returns:                                                                   */
/*     char * : the canonical copy, malloc'd (the caller frees it), never     */
/*              longer than the path; NULL when out of memory.                */
/*----------------------------------------------------------------------------*/
char *BFX_PathCanonical(const char *pszImagePath)
{
    char       *pszCanonical;
    char       *pszOut;          // where the next character of the copy goes
    char       *pszComponent;    // where the component being copied starts in the copy
    const char *pszIn;           // walks the caller's path
    size_t      cbPath;

    /* Room for the whole path and its terminator: the copy is never longer. */
    cbPath       = strlen(pszImagePath);
    pszCanonical = (char *)malloc(cbPath + 1);

    if (pszCanonical == NULL)
    {   /* Out of memory for the copy: the caller refuses its call. */
        return(NULL);
    }

    if (BFX_FsKind() != BFX_FS_FAT)
    {   /* ext2/3/4, or no session: every byte is part of a name, so the   */
        /* canonical form is the path itself.                              */
        memcpy(pszCanonical, pszImagePath, cbPath + 1);
        return(pszCanonical);
    }

    /* FAT: the path copied character by character, and at the end of each     */
    /* component -- its '/' or the string's end -- the copy backed up over     */
    /* the spaces and dots it ended with, never past the component's start.    */
    /* The leading '/' ends an empty first "component", which loses nothing.   */
    pszOut       = pszCanonical;
    pszComponent = pszCanonical;

    for (pszIn = pszImagePath; ; pszIn++)
    {   /* One character of the caller's path per iteration. */
        if (*pszIn != '/' && *pszIn != '\0')
        {   /* Inside a name: copied as it is.  A space or a dot stays until   */
            /* the name's end shows whether anything follows it.  A UTF-8      */
            /* character's bytes are never 0x20 or 0x2E, so only real spaces   */
            /* and dots are dropped below.                                     */
            *pszOut = *pszIn;
            pszOut++;
            continue;
        }

        /* A component has ended: its trailing spaces and dots go. */
        while (pszOut > pszComponent && (pszOut[-1] == ' ' || pszOut[-1] == '.'))
        {   /* One trailing space or dot per iteration, from the end. */
            pszOut--;
        }

        /* The separator, or the terminator, after the name as FAT keeps it. */
        *pszOut = *pszIn;

        if (*pszIn == '\0')
        {   /* The last component: the copy is finished. */
            break;
        }

        /* Past the '/': the next component starts here. */
        pszOut++;
        pszComponent = pszOut;
    }

    /* The path in the one spelling FAT matches it by. */
    return(pszCanonical);
}

/*----------------------------------------------------------------------------*/
/* BFX_Superblock:                                                            */
/*                                                                            */
/* The mounted filesystem's superblock, which lwext4's inode accessors need   */
/* in order to read a size or a mode word.                                    */
/*                                                                            */
/* Arguments:                                                                 */
/*     None.                                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     struct ext4_sblock * : the superblock; valid while an image is open.   */
/*----------------------------------------------------------------------------*/
struct ext4_sblock *BFX_Superblock(void)
{
    /* Fetched once by BFE_Open and held for the session. */
    return(BFX_pSuperblock);
}

/*----------------------------------------------------------------------------*/
/* BFX_TypeFromMode:                                                          */
/*                                                                            */
/* Turn the type bits of an inode mode word into a BFE_TYPE_* value.  The     */
/* type is read from the INODE rather than from a directory entry's type      */
/* byte, because that byte is optional on disk (an ext2 image without the     */
/* filetype feature leaves it 0) while the inode's mode is always present.    */
/*                                                                            */
/* Arguments:                                                                 */
/*     uiMode : the full inode mode word.                                     */
/*                                                                            */
/* Returns:                                                                   */
/*     uint32_t : the BFE_TYPE_* value; BFE_TYPE_UNKNOWN for anything else.   */
/*----------------------------------------------------------------------------*/
static uint32_t BFX_TypeFromMode(uint32_t uiMode)
{
    switch (uiMode & EXT4_INODE_MODE_TYPE_MASK)
    {   /* One inode type per case. */

        /* 0x8000: a regular file. */
        case EXT4_INODE_MODE_FILE:      return(BFE_TYPE_FILE);

        /* 0x4000: a directory. */
        case EXT4_INODE_MODE_DIRECTORY: return(BFE_TYPE_DIR);

        /* 0xA000: a symbolic link. */
        case EXT4_INODE_MODE_SOFTLINK:  return(BFE_TYPE_SYMLINK);

        /* 0x2000: a character device node. */
        case EXT4_INODE_MODE_CHARDEV:   return(BFE_TYPE_CHARDEV);

        /* 0x6000: a block device node. */
        case EXT4_INODE_MODE_BLOCKDEV:  return(BFE_TYPE_BLOCKDEV);

        /* 0x1000: a named pipe. */
        case EXT4_INODE_MODE_FIFO:      return(BFE_TYPE_FIFO);

        /* 0xC000: a unix socket. */
        case EXT4_INODE_MODE_SOCKET:    return(BFE_TYPE_SOCKET);

        /* Not a type this engine knows. */
        default:                        return(BFE_TYPE_UNKNOWN);
    }
}

/*----------------------------------------------------------------------------*/
/* BFX_FillEntry:                                                             */
/*                                                                            */
/* Read one path's inode and lay its facts out as a BFE_Entry.  lwext4        */
/* resolves the path without following a final symbolic link, so a link is    */
/* reported as a link.  The caller must hold the busy flag.                   */
/*                                                                            */
/* lwext4's path lookup follows NO symbolic link at any point, so a path      */
/* that runs THROUGH a link to a directory does not resolve (ENOENT or        */
/* ENOTDIR).  The engine's paths come from listing the real tree, so this     */
/* does not arise from the program's own browsing.                            */
/*                                                                            */
/* A time before 1970 is shown as a date near 2106 (see the time reads).  On  */
/* a FAT session there is no inode: the facts come from FatFs, made up as a   */
/* FAT listing shows them, with an errno-style result as here.                */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImagePath : the path whose inode to read.                           */
/*     pszName      : the name to record in the entry (the path's last        */
/*                    component, or a directory entry's name).                */
/*     pEntry       : receives the facts; zeroed first, so on failure it      */
/*                    holds the name only.                                    */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0 on success, otherwise lwext4's errno-style result (ENOENT      */
/*           when the path does not exist).                                   */
/*----------------------------------------------------------------------------*/
int BFX_FillEntry(const char *pszImagePath, const char *pszName, BFE_Entry *pEntry)
{
    struct ext4_inode inode;
    uint32_t          uiIno;
    uint32_t          uiMode;
    size_t            cbName;
    int               rc;

    /* Start from zeros and record the name, so even a failed read hands back   */
    /* an entry the caller can show.                                            */
    memset(pEntry, 0, sizeof(*pEntry));

    /* The name, clamped to what szName holds (BFX_ENTRY_NAME_MAX bytes and   */
    /* its NUL).                                                              */
    cbName = strlen(pszName);

    if (cbName > BFX_ENTRY_NAME_MAX)
    {   /* Longer than any name either filesystem holds (ext4's 255 bytes,    */
        /* FAT's 765 -- szName holds the longer whole, BFX_ENTRY_NAME_MAX):   */
        /* a guard, clamped so the copy cannot overrun.  It would cut only    */
        /* the entry's shown name, never a path, which the caller holds       */
        /* whole.                                                             */
        cbName = BFX_ENTRY_NAME_MAX;
    }

    /* Copy it in, terminate it, and record its length. */
    memcpy(pEntry->szName, pszName, cbName);
    pEntry->szName[cbName] = '\0';
    pEntry->uiNameBytes    = (uint32_t)cbName;

    if (BFX_FsKind() == BFX_FS_FAT)
    {   /* A FAT session: no inode to read.  FatFs's item for the path       */
        /* fills the facts (the root described without it), and its result   */
        /* is errno-style, as the lwext4 read below gives -- ENOENT for a    */
        /* path that does not exist.                                         */
        return(BFT_StatEntry(pszImagePath, pEntry));
    }

    /* Read the raw inode: lwext4 resolves the path and copies the inode   */
    /* out, giving its number.  Both outputs start cleared.                */
    uiIno = 0;
    memset(&inode, 0, sizeof(inode));

    /* The lookup and the copy, in one lwext4 call (no link followed). */
    rc = ext4_fill_raw_inode(pszImagePath, &uiIno, &inode);

    if (rc != 0)
    {   /* The path does not resolve, or its inode could not be read. */
        return(rc);
    }

    /* Lay the inode's fields out.  The mode word is split: its type nibble   */
    /* becomes uiType and its 12 permission bits become uiMode.               */
    uiMode = ext4_inode_get_mode(BFX_pSuperblock, &inode);

    /* The rest of the facts, straight from the inode.  The three times are   */
    /* the inode's 32-bit seconds fields as lwext4 returns them, read as      */
    /* unsigned: right up to 2106, but a time before 1970 (stored negative)   */
    /* comes out near the end of that range, and the extra epoch bits a       */
    /* large inode can carry are not read.  Size and mode go                  */
    /* through the superblock because lwext4 decides from it which on-disk    */
    /* fields hold them: the size's high 32 bits count for a regular file     */
    /* on a revision-1 filesystem, and for a directory only with largedir     */
    /* (BifrostFM patch 5); the mode has extra high bits only on a            */
    /* filesystem created by the Hurd.                                        */
    pEntry->ullSize = ext4_inode_get_size(BFX_pSuperblock, &inode);
    pEntry->uiInode = uiIno;
    pEntry->uiType  = BFX_TypeFromMode(uiMode);
    pEntry->uiMode  = uiMode & BFX_PERM_MASK;
    pEntry->uiUid   = ext4_inode_get_uid(&inode);
    pEntry->uiGid   = ext4_inode_get_gid(&inode);
    pEntry->uiLinks = ext4_inode_get_links_cnt(&inode);
    pEntry->uiAtime = ext4_inode_get_access_time(&inode);
    pEntry->uiMtime = ext4_inode_get_modif_time(&inode);
    pEntry->uiCtime = ext4_inode_get_change_inode_time(&inode);

    /* The entry is complete. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFX_FreeNames:                                                             */
/*                                                                            */
/* Free a name list returned by BFX_CollectNames.                             */
/*                                                                            */
/* Arguments:                                                                 */
/*     ppszNames : the array (may be NULL when the list was empty).           */
/*     nNames    : how many names it holds.                                   */
/*                                                                            */
/* Returns:                                                                   */
/*     void : every name and the array itself are freed.                      */
/*----------------------------------------------------------------------------*/
void BFX_FreeNames(char **ppszNames, int nNames)
{
    int i;

    for (i = 0; i < nNames; i++)
    {   /* One collected name per iteration. */
        free(ppszNames[i]);
    }

    /* Then the array that held them; free(NULL) is harmless for an empty list. */
    free(ppszNames);
}

/*----------------------------------------------------------------------------*/
/* BFX_CollectNames:                                                          */
/*                                                                            */
/* Read every real entry name of an image directory into a malloc'd array     */
/* and CLOSE the directory before returning.  Everything that walks a         */
/* directory goes through here, for one reason: the work done per entry       */
/* (reading its inode, recursing into it, removing it) itself opens files     */
/* and directories inside lwext4, and doing that while an iteration is still  */
/* open -- or changing a directory while iterating it -- is how an iteration  */
/* goes wrong.  Collect first, then act on a closed directory.                */
/*                                                                            */
/* This is the ONLY place the engine iterates an ext2/3/4 directory (the one  */
/* caller of lwext4's ext4_dir_entry_next), so listing, every copy's walk and */
/* the tree removal all see exactly what it returns: every child's name, or   */
/* none -- a directory that cannot be read through is a failure, never a      */
/* short list handed back as the whole.  On a FAT session the names come      */
/* from FatFs instead (BFT_CollectNames, bfe_fat.c), under the same promise.  */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImageDir : the directory to read.                                   */
/*     pppszNames  : receives the array of malloc'd names (NULL when empty).  */
/*     pnNames     : receives the count.                                      */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0 on success, every name collected; lwext4's result when the     */
/*           directory cannot be opened, or a read failed part-way (no names  */
/*           are handed back then); ENOMEM when the list cannot be allocated. */
/*----------------------------------------------------------------------------*/
int BFX_CollectNames(const char *pszImageDir, char ***pppszNames, int *pnNames)
{
    ext4_dir              dir;
    const ext4_direntry  *pDirent;
    char                **ppszList;
    char                **ppszGrown;
    char                 *pszName;
    int                   nCapacity;
    int                   nUsed;
    int                   rc;

    /* The outputs start as "no names", which is also what a failure leaves. */
    *pppszNames = NULL;
    *pnNames    = 0;

    if (BFX_FsKind() == BFX_FS_FAT)
    {   /* A FAT session: FatFs reads the directory, with the same outputs   */
        /* and errno-style result (BFT_CollectNames); FatFs leaves out "."   */
        /* and ".." itself, so no test for them is needed there.             */
        return(BFT_CollectNames(pszImageDir, pppszNames, pnNames));
    }

    /* Open the directory for iteration. */
    rc = ext4_dir_open(&dir, pszImageDir);

    if (rc != 0)
    {   /* Not a directory, or it does not exist. */
        return(rc);
    }

    /* No array yet: it is allocated at the first name, so an empty   */
    /* directory costs no allocation and returns NULL.                */
    ppszList  = NULL;
    nCapacity = 0;
    nUsed     = 0;

    for (;;)
    {   /* One directory slot per iteration. */

        /* The next slot (lwext4's own copy, valid until the next call). */
        pDirent = ext4_dir_entry_next(&dir);

        if (pDirent == NULL)
        {   /* The walk has ended: at the directory's end, or because its   */
            /* inode or a block could not be read -- dir.last_err says      */
            /* which, below (lwext4 patch 35).                              */
            break;
        }

        if (pDirent->inode == 0)
        {   /* An unused slot. */
            continue;
        }

        if (pDirent->name_length == 1 && pDirent->name[0] == '.')
        {   /* The "." self link is not a child. */
            continue;
        }

        if (pDirent->name_length == 2 && pDirent->name[0] == '.' && pDirent->name[1] == '.')
        {   /* The ".." parent link is not a child. */
            continue;
        }

        if (nUsed == nCapacity)
        {   /* The array is full: double it (a first allocation starts at   */
            /* BFX_NAMES_FIRST), so any directory size is handled.          */
            if (nCapacity == 0)
            {   /* First allocation. */
                nCapacity = BFX_NAMES_FIRST;
            }
            else
            {   /* Every later growth doubles. */
                nCapacity = nCapacity * 2;
            }

            /* Grow the array to the new capacity. */
            ppszGrown = (char **)realloc(ppszList, (size_t)nCapacity * sizeof(char *));

            if (ppszGrown == NULL)
            {   /* Out of memory: release what was collected and stop. */

                /* Free what was collected, close the directory, fail.     */
                /* The old array is still valid (a failed realloc leaves   */
                /* it), which is why the result went into a second         */
                /* pointer; the outputs still say "none" from the start.   */
                BFX_FreeNames(ppszList, nUsed);
                ext4_dir_close(&dir);
                return(ENOMEM);
            }

            /* The grown array replaces the old (realloc moved or kept it). */
            ppszList = ppszGrown;
        }

        /* The on-disk name is counted, not terminated: copy and terminate it.   */
        /* name_length is one byte, so this is at most 256 bytes.                */
        pszName = (char *)malloc((size_t)pDirent->name_length + 1);

        if (pszName == NULL)
        {   /* Out of memory for this name. */

            /* Free what was collected, close the directory, fail. */
            BFX_FreeNames(ppszList, nUsed);
            ext4_dir_close(&dir);
            return(ENOMEM);
        }

        /* The name's bytes, then the NUL the disk does not store. */
        memcpy(pszName, pDirent->name, pDirent->name_length);
        pszName[pDirent->name_length] = '\0';

        /* Into the next free slot. */
        ppszList[nUsed] = pszName;
        nUsed++;
    }

    /* Close the directory BEFORE the caller acts on any name (see above). */
    ext4_dir_close(&dir);

    if (dir.last_err != EOK)
    {   /* The walk was cut short by a read that failed: the names so far   */
        /* are NOT the directory, and a listing, a copy or an emptiness     */
        /* test acting on them would report a partial directory as whole.   */
        /* They are freed, and lwext4's error is the result.                */
        BFX_FreeNames(ppszList, nUsed);
        return(dir.last_err);
    }

    /* Hand the list over. */
    *pppszNames = ppszList;
    *pnNames    = nUsed;

    /* The directory is closed and its names are the caller's to free. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFX_TouchParent:                                                           */
/*                                                                            */
/* Set the modify and inode-change times of the directory that holds an       */
/* entry to the present -- what Linux does whenever a name is added to or     */
/* taken from a directory, and what lwext4, which has no clock, never does.   */
/* A backup or sync tool that looks at a folder's times to see whether        */
/* anything inside it changed depends on it.  Called after the change has     */
/* been made, so a failure here leaves the change in place and is reported    */
/* by the caller as the operation's.                                          */
/*                                                                            */
/* On a FAT session the same calls go to FatFs through bfe_fs.c.              */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImagePath : the entry added or removed (absolute, canonical); its   */
/*                    parent is what is touched -- "/" for a top-level entry. */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0, ENOMEM when the parent's path cannot be made, or the          */
/*           library's errno-style result for the first time that could not   */
/*           be set.                                                          */
/*----------------------------------------------------------------------------*/
int BFX_TouchParent(const char *pszImagePath)
{
    char     *pszParent;
    char     *pszSlash;
    uint32_t  uiNow;
    size_t    cbPath;
    int       rc;

    /* A copy of the path, to cut at its last '/'. */
    cbPath    = strlen(pszImagePath);
    pszParent = (char *)malloc(cbPath + 1);

    if (pszParent == NULL)
    {   /* Out of memory for the parent's path. */
        return(ENOMEM);
    }

    /* The path with its terminator, and its last separator, which every   */
    /* caller's absolute path has.                                         */
    memcpy(pszParent, pszImagePath, cbPath + 1);
    pszSlash = strrchr(pszParent, '/');

    if (pszSlash == pszParent)
    {   /* The entry is at the top: its parent is the root, "/" itself. */
        pszParent[1] = '\0';
    }
    else if (pszSlash != NULL)
    {   /* Deeper: the parent is everything before the last '/'. */
        *pszSlash = '\0';
    }

    /* Both times are the one present moment, modify first.  On a FAT        */
    /* session the modify time is the parent's one FAT time                  */
    /* (BFT_SetModifyTime, which does nothing for the root, FAT keeping no   */
    /* time for it), and the change time, which FAT has no field for, is a   */
    /* call that succeeds doing nothing -- so the pair runs alike on both.   */
    uiNow = (uint32_t)time(NULL);
    rc    = BFS_SetMtime(pszParent, uiNow);

    if (rc == 0)
    {   /* The modify time went in; the change time follows it. */
        rc = BFS_SetCtime(pszParent, uiNow);
    }

    /* The path is done with, whatever happened. */
    free(pszParent);

    /* 0, or the first failure. */
    return(rc);
}

/*----------------------------------------------------------------------------*/
/* BFX_CheckParent:                                                           */
/*                                                                            */
/* Whether the directory that is to hold a new entry exists and is a          */
/* directory -- the test a create makes before it reaches the library.        */
/* lwext4's create path (ext4_generic_open2) MAKES every missing level of a   */
/* path, as directories of its own default mode, so without this test a       */
/* mkdir or a symbolic link under a missing folder would quietly create the   */
/* folders above it, where the engine's contract is to refuse.  The caller    */
/* must hold the busy flag, so nothing can change the parent between this     */
/* test and the create.                                                       */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImagePath : the entry about to be created (absolute, canonical);    */
/*                    its parent is what is tested -- "/" for a top-level     */
/*                    entry, which always exists.                             */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0 when the parent is a directory; ENOTDIR when it is something   */
/*           else; ENOMEM when the parent's path cannot be made; otherwise    */
/*           the lookup's errno-style result (ENOENT when it is missing).     */
/*----------------------------------------------------------------------------*/
int BFX_CheckParent(const char *pszImagePath)
{
    BFE_Entry  entry;
    char      *pszParent;
    char      *pszSlash;
    size_t     cbPath;
    int        rc;

    /* A copy of the path, to cut at its last '/'. */
    cbPath    = strlen(pszImagePath);
    pszParent = (char *)malloc(cbPath + 1);

    if (pszParent == NULL)
    {   /* Out of memory for the parent's path. */
        return(ENOMEM);
    }

    /* The path with its terminator, and its last separator, which every   */
    /* caller's absolute path has.                                         */
    memcpy(pszParent, pszImagePath, cbPath + 1);
    pszSlash = strrchr(pszParent, '/');

    if (pszSlash == pszParent || pszSlash == NULL)
    {   /* The entry is at the top: its parent is the root, which always     */
        /* exists and is a directory, so there is nothing to look up.  (No   */
        /* '/' at all is a guard: BFX_PathValid admits only absolute         */
        /* paths, so every caller's has one.)                                */
        free(pszParent);
        return(0);
    }

    /* Deeper: the parent is everything before the last '/', read as any   */
    /* other path is (on a FAT session through FatFs, as FAT matches       */
    /* names).                                                             */
    *pszSlash = '\0';
    rc        = BFX_FillEntry(pszParent, BFU_LastComponent(pszParent), &entry);

    if (rc == 0 && entry.uiType != BFE_TYPE_DIR)
    {   /* There, but a file or a link: nothing can be created inside it.   */
        /* A link to a directory counts as not one -- lwext4 follows no     */
        /* link in a path, so the create could not go through it either.    */
        rc = ENOTDIR;
    }

    /* The parent's path is done with. */
    free(pszParent);

    /* 0, ENOTDIR, or the lookup's failure. */
    return(rc);
}

/*----------------------------------------------------------------------------*/
/* BFX_NewLinkFinish:                                                         */
/*                                                                            */
/* Finish a symbolic link lwext4 has just created, giving it what Linux would */
/* -- the mode every Linux link has, an owner, all three times, and its       */
/* parent directory's times -- none of which lwext4 sets as Linux does.  A    */
/* failure leaves the link created but only partly finished; the caller       */
/* reports it.                                                                */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImageLink : the link just created.                                  */
/*     uiUid        : the owner to give it.                                   */
/*     uiGid        : the group to give it.                                   */
/*     uiTime       : its access and modify time, epoch seconds.              */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0, or lwext4's result for the first step that failed.            */
/*----------------------------------------------------------------------------*/
int BFX_NewLinkFinish(const char *pszImageLink, uint32_t uiUid, uint32_t uiGid, uint32_t uiTime)
{
    int rc;

    /* The mode first: lwext4 made it 0666, while on Linux a symbolic link is   */
    /* always 0777 (its own bits are never consulted).  Each step below runs    */
    /* only when the one before it succeeded, so the first failure is the       */
    /* result.                                                                  */
    rc = BFS_SetMode(pszImageLink, BFX_NEW_LINK_MODE);

    if (rc == 0)
    {   /* The mode went in; set the owner. */
        rc = BFS_SetOwner(pszImageLink, uiUid, uiGid);
    }

    if (rc == 0)
    {   /* Then the access time.  lwext4 left every time zero (it has no      */
        /* clock; Linux shows a zero as 1970).  The access and modify times   */
        /* are the caller's: the present for a new link, the original's for   */
        /* one restored or copied.                                            */
        rc = BFS_SetAtime(pszImageLink, uiTime);
    }

    if (rc == 0)
    {   /* And the modify time. */
        rc = BFS_SetMtime(pszImageLink, uiTime);
    }

    if (rc == 0)
    {   /* And the inode-change time: the present, whatever uiTime is --     */
        /* it records when this inode was made here, which no earlier time   */
        /* can stand for, even when the link's other times are older.        */
        rc = BFS_SetCtime(pszImageLink, (uint32_t)time(NULL));
    }

    if (rc == 0)
    {   /* Finally the directory the link was added to, as Linux updates it. */
        rc = BFX_TouchParent(pszImageLink);
    }

    /* 0, or the first failure. */
    return(rc);
}

/*----------------------------------------------------------------------------*/
/* BFX_NewDirFinish:                                                          */
/*                                                                            */
/* Finish a directory lwext4 has just created: settle its block checksum,     */
/* which the bundled lwext4 can leave stale on a new directory (e2fsck would  */
/* then report it), and give it the caller's mode, all three times and its    */
/* parent directory's times, as Linux would.  On a FAT session only the time  */
/* and the parent apply.  A failure leaves the directory created but only     */
/* partly finished; the caller reports it.                                    */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImageDir : the directory just created.                              */
/*     uiMode      : the permission bits to give it.                          */
/*     uiTime      : its access and modify time, epoch seconds.               */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0, or lwext4's result for the first step that failed.            */
/*----------------------------------------------------------------------------*/
int BFX_NewDirFinish(const char *pszImageDir, uint32_t uiMode, uint32_t uiTime)
{
    BFS_File  *pFile;
    char      *pszProbe;
    int        rc;

    if (BFX_FsKind() == BFX_FS_FAT)
    {   /* A FAT session: no checksum to settle (that is lwext4's) and no    */
        /* mode to hold, so only the time is set -- the modify time, FAT's   */
        /* one -- and the parent touched, as below.                          */
        rc = BFS_SetMtime(pszImageDir, uiTime);

        if (rc == 0)
        {   /* The directory it was made in, as Linux updates it. */
            rc = BFX_TouchParent(pszImageDir);
        }

        /* 0, or the first failure. */
        return(rc);
    }

    /* STEP 1: add and remove one entry, so the directory block is rewritten.   */
    /* On a filesystem with BOTH metadata_csum and dir_index, the bundled       */
    /* lwext4 leaves a brand-new directory's block checksum stale until the     */
    /* directory is next modified, and e2fsck then reports "directory passes    */
    /* checks but fails checksum" for every directory created and left          */
    /* empty.  Modifying it makes lwext4 write the checksum properly.  The      */
    /* step is harmless where it is not needed, so it is not made               */
    /* conditional on the feature flags.  First the probe entry's path.         */
    pszProbe = BFU_JoinImage(pszImageDir, BFX_HEAL_NAME);

    if (pszProbe == NULL)
    {   /* Out of memory composing the temporary entry's path. */
        return(ENOMEM);
    }

    /* Create the probe as an ordinary empty file under its fixed name: this   */
    /* adds the entry, which is the modification that makes lwext4 rewrite     */
    /* the block and its checksum.  The directory is brand new and so empty,   */
    /* which is why the name cannot already be taken.                          */
    rc = BFS_FileOpen(pszProbe, "wb", &pFile);

    if (rc == 0)
    {   /* The entry exists; close it and take it away again. */

        /* Close the probe and remove it.  The removal rewrites the block   */
        /* again, leaving the directory empty with its checksum right.      */
        BFS_FileClose(pFile);
        rc = BFS_Remove(pszProbe);
    }

    /* The probe's path is no longer needed, whatever happened. */
    free(pszProbe);

    if (rc != 0)
    {   /* The directory could not be written to (typically a full filesystem).   */
        /* A failure partway -- the probe created, its removal failed --          */
        /* leaves the probe behind; the error is returned either way.             */
        return(rc);
    }

    /* STEP 2: the mode, then the times, each step only when the one before   */
    /* it succeeded.  lwext4 makes a new directory 0777; the caller's mode    */
    /* replaces that.                                                         */
    rc = BFS_SetMode(pszImageDir, uiMode & BFX_PERM_MASK);

    if (rc == 0)
    {   /* The mode went in; set the access time.  lwext4 has no clock, so    */
        /* what it creates carries zero times, which a file manager would     */
        /* show as 1970.  The access and modify times are the caller's: the   */
        /* present for a new folder, or a copy's or an import's provisional   */
        /* time, which the caller replaces once the folder is filled.         */
        rc = BFS_SetAtime(pszImageDir, uiTime);
    }

    if (rc == 0)
    {   /* And the modify time. */
        rc = BFS_SetMtime(pszImageDir, uiTime);
    }

    if (rc == 0)
    {   /* And the inode-change time: the present, whatever uiTime is --   */
        /* the moment this inode was made here.                            */
        rc = BFS_SetCtime(pszImageDir, (uint32_t)time(NULL));
    }

    if (rc == 0)
    {   /* Finally the directory it was made in, as Linux updates it. */
        rc = BFX_TouchParent(pszImageDir);
    }

    /* 0, or the first failure. */
    return(rc);
}

/*----------------------------------------------------------------------------*/
/* BFX_Finish:                                                                */
/*                                                                            */
/* The common tail of the simple edit functions: turn one lwext4 result into  */
/* the status to return, recording a detail line on failure, and release the  */
/* busy flag either way.                                                      */
/*                                                                            */
/* Arguments:                                                                 */
/*     rc       : the lwext4 call's result.                                   */
/*     pszVerb  : what was being done, for the detail line ("remove").        */
/*     pszPath  : the image path it was being done to.                        */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK, or the mapped BFE_ERR_* code.                            */
/*----------------------------------------------------------------------------*/
static int BFX_Finish(int rc, const char *pszVerb, const char *pszPath)
{
    int iStatus;

    /* Success unless lwext4 said otherwise. */
    iStatus = BFE_OK;

    /* A failure is recorded before the release below (BFX_Fail's rule). */
    if (rc != 0)
    {   /* lwext4 refused: keep its reason next to the path. */
        iStatus = BFX_Fail(BFX_MapErrno(rc), "cannot %s %s: %s", pszVerb, pszPath, strerror(rc));
    }

    /* The edit is over either way; free the engine. */
    BFX_Leave();

    /* BFE_OK, or the code recorded above. */
    return(iStatus);
}

/*----------------------------------------------------------------------------*/
/* BFX_HasExternalJournal:                                                    */
/*                                                                            */
/* Whether a filesystem keeps its journal on ANOTHER device.  The superblock  */
/* then has has_journal set and names no journal inode (the journal is found  */
/* instead by the UUID and device number it records), which is how mke2fs     */
/* -J device= leaves it.  Neither lwext4 nor the e2fsck built in here can     */
/* reach such a journal from Windows.                                         */
/*                                                                            */
/* Arguments:                                                                 */
/*     pSb : the mounted filesystem's superblock.                             */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 1 for an external journal, 0 for an internal one or none.        */
/*----------------------------------------------------------------------------*/
static int BFX_HasExternalJournal(struct ext4_sblock *pSb)
{
    if (!ext4_sb_feature_com(pSb, EXT4_FCOM_HAS_JOURNAL))
    {   /* No journal at all (ext2, or one made without). */
        return(0);
    }

    if (ext4_get32(pSb, journal_inode_number) != 0)
    {   /* The usual case: the journal is an inode of this filesystem. */
        return(0);
    }

    /* A journal, and no inode for it: it lives on another device. */
    return(1);
}

/*----------------------------------------------------------------------------*/
/* BFE_ApiVersion:                                                            */
/*                                                                            */
/* Report the interface version this DLL was built with, so the GUI can       */
/* refuse to run against a DLL whose structures it does not match.  The one   */
/* public function that returns a value rather than a status, and the one     */
/* that does not pass the gate: it touches no state.                          */
/*                                                                            */
/* Arguments:                                                                 */
/*     None.                                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_API_VERSION.                                                 */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_ApiVersion(void)
{
    /* A compile-time constant of this build. */
    return(BFE_API_VERSION);
}

/*-----------------------------------------------------------------------------*/
/* BFE_Open:                                                                   */
/*                                                                             */
/* Open an image file or a device and mount the filesystem in it, read-only    */
/* or for writing with the journal running.  A device opened for writing is    */
/* refused when it is the system disk, and otherwise has its disk's volumes    */
/* locked for the session.  A range lwext4 will not mount is tried as FAT or   */
/* exFAT (bfe_fat.c), opened read-only or writable as asked.                   */
/*                                                                             */
/* Arguments:                                                                  */
/*     pwszImage : the image file's or device's host path, UTF-16.             */
/*     ullOffset : byte offset of the filesystem inside it (0 for a file that  */
/*                 is the filesystem); a multiple of 512.                      */
/*     ullLength : byte length of the filesystem, or 0 for "to the end";       */
/*                 a multiple of 512.                                          */
/*     bReadOnly : non-zero to open read-only.                                 */
/*                                                                             */
/* Returns:                                                                    */
/*     int : BFE_OK with the image open.  Otherwise, with nothing left open    */
/*           or locked: BFE_ERR_ALREADY_OPEN, BFE_ERR_ARG, BFE_ERR_IMAGE_OPEN, */
/*           BFE_ERR_SYSTEM_DISK, BFE_ERR_VOLUME_LOCK, BFE_ERR_NO_MEMORY,      */
/*           BFE_ERR_MOUNT, BFE_ERR_JOURNAL, BFE_ERR_IO_IMAGE (the FAT try's   */
/*           read of the range failed -- BFT_Mount), or -- a read-write open   */
/*           of an image whose features the engine can only read --            */
/*           BFE_ERR_READ_ONLY.                                                */
/*-----------------------------------------------------------------------------*/
BFE_API int BFE_Open(const wchar_t *pwszImage, uint64_t ullOffset, uint64_t ullLength, int bReadOnly)
{
    BFV_Target probe;
    uint32_t   uiSystem;
    int        iAccess;
    int        iStatus;
    int        iError;
    int        rc;

    /* The gate: the flag only.  "No image open" is tested just below with   */
    /* its own message, so the gate is not asked to demand one either way.   */
    iStatus = BFX_Enter(FALSE, FALSE);

    if (iStatus != BFE_OK)
    {   /* Another call is running. */
        return(iStatus);
    }

    if (BFX_bOpen)
    {   /* One image at a time: the caller closes the current one first. */

        /* Refuse.  The session already open is the caller's and is left   */
        /* exactly as it is; only the busy flag was taken.                 */
        iStatus = BFX_Fail(BFE_ERR_ALREADY_OPEN, "an image is already open");
        BFX_Leave();
        return(iStatus);
    }

    if (pwszImage == NULL || pwszImage[0] == L'\0')
    {   /* No path given. */

        /* Refuse; only the busy flag has been taken. */
        iStatus = BFX_Fail(BFE_ERR_ARG, "no image path given");
        BFX_Leave();
        return(iStatus);
    }

    /* STEP 1: probe the path in the mode the session will use, so a missing   */
    /* or locked or write-protected file, or a device that needs               */
    /* Administrator, is reported as exactly that -- lwext4 would report it    */
    /* from inside its mount as the same I/O failure as "not an ext            */
    /* filesystem" -- and so a device is known to be one before anything is    */
    /* mounted on it.                                                          */
    if (bReadOnly)
    {   /* A read-only session needs only read access. */
        iAccess = BFV_ACCESS_READ;
    }
    else
    {   /* A writable session needs it writable in place. */
        iAccess = BFV_ACCESS_WRITE;
    }

    /* The probe: open the path as the session will, and learn what it is. */
    iError = BFV_Open(pwszImage, iAccess, &probe);

    if (iError != 0)
    {   /* Missing, in use by another program, not writable, or a device   */
        /* without Administrator.                                          */

        /* Report it with the Windows error number, which is what tells       */
        /* "access denied" from "in use by another program" -- the status     */
        /* code alone cannot.  A failed BFV_Open leaves no handle to close.   */
        iStatus = BFX_Fail(BFE_ERR_IMAGE_OPEN, "cannot open the image: Windows error %d", iError);
        BFX_Leave();
        return(iStatus);
    }

    /* The probe's handle is closed at once -- the block device opens the path   */
    /* again for itself -- and only what it learned (device or file, which       */
    /* disk) is used below.  It is closed BEFORE the volumes are locked: when    */
    /* the path names a volume (\\.\X:), the probe's handle is open on that      */
    /* very volume, and Windows refuses to lock a volume while other handles     */
    /* are open on it.                                                           */
    BFV_Close(&probe);

    /* STEP 1a: a device opened for writing gets the guards.  The system disk    */
    /* is never written; every volume on the device's disk is locked for the     */
    /* session, since Windows refuses raw writes under a mounted volume.         */
    /*                                                                           */
    /* First, empty the lock list, so this session starts with none: BFE_Close   */
    /* releases a session's locks, so there should be nothing here, and every    */
    /* failure path below unlocks before it returns.                             */
    BFV_UnlockDisk(&BFX_locks);

    if (probe.bDevice && !bReadOnly)
    {   /* A writable device session.  A disk that cannot be identified is   */
        /* refused as if it WERE the system disk: the guard fails closed.    */

        /* The number of the disk Windows itself runs from. */
        uiSystem = BFV_SystemDisk();

        if (uiSystem == BFV_NO_DISK || probe.uiDisk == BFV_NO_DISK || probe.uiDisk == uiSystem)
        {   /* The disk Windows runs from, or one that cannot be told apart. */

            /* Refuse.  The test comes BEFORE any lock is taken, because      */
            /* locking the system disk's volumes would dismount the running   */
            /* Windows; so there is no lock to give back.                     */
            iStatus = BFX_Fail(BFE_ERR_SYSTEM_DISK, "the device is the disk Windows is running from, or cannot be told apart from it");
            BFX_Leave();
            return(iStatus);
        }

        /* Lock and dismount every volume on the device's disk, held in   */
        /* BFX_locks until BFE_Close.                                     */
        iError = BFV_LockDisk(probe.uiDisk, &BFX_locks);

        if (iError != 0)
        {   /* The disk could not be locked: a volume on it in use, or one   */
            /* whose disk could not be told.                                 */

            /* Report it with the lock's own sentence, which names the case,   */
            /* the volume and -- for one in use -- the remedy: close           */
            /* whatever has it open.  No unlock here: BFV_LockDisk has         */
            /* already released the locks it took before the one that          */
            /* failed, so the card's volumes are mounted again, as they        */
            /* were.                                                           */
            iStatus = BFX_Fail(BFE_ERR_VOLUME_LOCK, "%s", BFX_locks.szFailure);
            BFX_Leave();
            return(iStatus);
        }
    }

    /* STEP 2: point the block device at the path and register it. */
    rc = BFD_Configure(pwszImage, ullOffset, ullLength, bReadOnly);

    if (rc != 0)
    {   /* The range is not sector-aligned (EINVAL), or the path could not   */
        /* be copied (ENOMEM): each reported as itself.                      */
        if (rc == ENOMEM)
        {   /* Out of memory: not the caller's argument. */
            iStatus = BFX_Fail(BFE_ERR_NO_MEMORY, "out of memory copying the image's path");
        }
        else
        {   /* Off the 512-byte grid. */
            iStatus = BFX_Fail(BFE_ERR_ARG, "the offset and length must be multiples of 512 bytes");
        }

        /* Give the card back to Windows by releasing the volume locks of   */
        /* STEP 1a -- left held, its volumes would stay dismounted until    */
        /* the program ended.  (For a file or a read-only device no lock    */
        /* was taken and the release does nothing.)                         */
        BFV_UnlockDisk(&BFX_locks);
        BFX_Leave();
        return(iStatus);
    }

    /* A session's device writes are read back only when the caller asked   */
    /* (BFE_SetWriteVerify) -- an ordinary edit is not slowed by default.   */
    BFD_SetVerify(BFX_bVerifySessions);

    /* Register the block device with lwext4 under its fixed name (NULL: no   */
    /* cache of the caller's own; lwext4 makes one at mount).  It is the      */
    /* same static device every time (BFD_Get), and lwext4 answers a second   */
    /* registration of a name it already holds with success, changing         */
    /* nothing -- which is why the engine never unregisters it, not even      */
    /* after a failed mount below.                                            */
    rc = ext4_device_register(BFD_Get(), NULL, BFX_DEVICE_NAME);

    if (rc != 0)
    {   /* lwext4 has no free device slot (its only failure, ENOSPC). */

        /* Report it and give the card back to Windows (release the volume   */
        /* locks).  The block device's configuration is left as it is: it    */
        /* holds nothing open, and the next operation configures it anew.    */
        iStatus = BFX_Fail(BFE_ERR_MOUNT, "cannot register the block device: %s", strerror(rc));
        BFV_UnlockDisk(&BFX_locks);
        BFX_Leave();
        return(iStatus);
    }

    /* STEP 3: mount.  lwext4 opens the device here, so an offset or length   */
    /* outside the file also surfaces at this point.                          */
    if (bReadOnly)
    {   /* Mount read-only: lwext4 refuses every write itself. */
        rc = ext4_mount(BFX_DEVICE_NAME, BFX_MOUNT_POINT, true);
    }
    else
    {   /* Mount read-write -- which lwext4 may still quietly turn into a   */
        /* read-only mount, the case STEP 3a below catches.                 */
        rc = ext4_mount(BFX_DEVICE_NAME, BFX_MOUNT_POINT, false);
    }

    if (rc != 0)
    {   /* Not an ext2/3/4 filesystem at that offset, or a damaged superblock:   */
        /* tried as FAT or exFAT.  lwext4 has left nothing mounted -- it         */
        /* closes the device it opened, and BifrostFM patch 4 (ext4.c) frees     */
        /* the mount point -- so FatFs finds the block device configured and     */
        /* its target closed, and opens it for itself.  FatFs counts in the      */
        /* device's own sector size (a file's is 512), which the probe of        */
        /* STEP 1 learned.                                                       */
        iStatus = BFT_Mount(probe.uiSector, strerror(rc));

        if (iStatus != BFE_OK)
        {   /* Neither library mounts the range: BFT_Mount has recorded why,   */
            /* naming lwext4's reason too, and left nothing open.              */

            /* Give the card back to Windows. */
            BFV_UnlockDisk(&BFX_locks);
            BFX_Leave();
            return(iStatus);
        }

        /* A FAT session, read-only or writable as asked: no journal (FAT   */
        /* has none -- FatFs writes each change through at once), nothing   */
        /* barred, no ext4 superblock.  A writable device's volumes were    */
        /* locked in STEP 1a, as for ext4.  Marked open only now, as the    */
        /* ext4 path below is.                                              */
        BFX_iFsKind        = BFX_FS_FAT;
        BFX_bReadOnly      = (bReadOnly != 0);
        BFX_bJournalOn     = FALSE;
        BFX_bChangesBarred = FALSE;
        BFX_pSuperblock    = NULL;
        BFX_bOpen          = TRUE;

        /* The open is over; free the engine for the session's calls. */
        BFX_Leave();

        /* The FAT volume is open and mounted. */
        return(BFE_OK);
    }

    /* STEP 3a: a read-only-compatible feature the library can READ but not      */
    /* write -- bigalloc (clusters), quota and project (the quota inodes),       */
    /* verity (files that must not change) -- makes lwext4 mount read-only       */
    /* whatever was asked, silently.  A read-write open would then look          */
    /* successful and fail at its first write with "read-only session", which    */
    /* is the wrong story; so the open itself is refused, naming the bits, and   */
    /* a read-only open of the same image works as it always did.                */
    /*                                                                           */
    /* The test uses EXT4_SUPPORTED_FRO_COM; lwext4's own fallback uses          */
    /* CONFIG_SUPPORTED_FRO_COM, which is the same set only while lwext4 is      */
    /* built at its default ext4 feature level (ext4_config.h; the build does    */
    /* not override it).  Should the build ever choose a lower level, the two    */
    /* would part and this refusal would miss bits lwext4 still falls back on.   */
    /*                                                                           */
    /* If the superblock cannot be fetched here, all three tests are skipped;    */
    /* STEP 5 fetches it again and fails the open cleanly.                       */
    if (!bReadOnly)
    {   /* Asked for read-write: compare the image's bits with the supported set. */
        struct ext4_sblock *pSbCheck;
        uint32_t            uiRoUnsupported;

        /* The mounted superblock, to read its feature words. */
        pSbCheck = NULL;
        rc       = ext4_get_sblock(BFX_MOUNT_POINT, &pSbCheck);

        if (rc == 0 && pSbCheck != NULL)
        {   /* The superblock is readable; the bits outside the set are the problem. */

            /* The image's read-only-compatible feature word (little-endian    */
            /* on disk; ext4_get32 converts it), with every bit this build     */
            /* of lwext4 can WRITE masked out: what is left is the reason it   */
            /* would have mounted read-only.                                   */
            uiRoUnsupported = ext4_get32(pSbCheck, features_read_only) & ~(uint32_t)EXT4_SUPPORTED_FRO_COM;

            if (uiRoUnsupported != 0)
            {   /* Readable, not writable: say so and leave nothing open. */

                /* Report the refusal -- naming the offending bits, with a     */
                /* legend for the four that occur, so the user can see which   */
                /* feature stands in the way -- and close the session down.    */
                /* The unmount comes before the volume locks are released:     */
                /* Windows remounts a volume the moment its lock goes, and     */
                /* lwext4 must be off the device first.                        */
                iStatus = BFX_Fail(BFE_ERR_READ_ONLY,
                                   "the filesystem has features this engine can read but not write (read-only feature bits 0x%x: bigalloc 0x200, quota 0x100, project 0x2000, verity 0x8000); open it read-only",
                                   (unsigned)uiRoUnsupported);
                ext4_umount(BFX_MOUNT_POINT);
                BFV_UnlockDisk(&BFX_locks);
                BFX_Leave();
                return(iStatus);
            }

            /* An EXTERNAL journal -- one kept on another device, named in the   */
            /* superblock by UUID and device number -- cannot be reached from    */
            /* here, and lwext4 would look for it at inode 0: so it can be       */
            /* neither replayed nor written, and writing past it while it may    */
            /* hold changes is not safe.  The open is refused, naming the        */
            /* reason; read-only works, and BFE_VolumeInfo says why.             */
            if (BFX_HasExternalJournal(pSbCheck))
            {   /* The journal is somewhere else. */

                /* Report the refusal and close the session down; the unmount   */
                /* comes before the volume locks are released, as above.        */
                iStatus = BFX_Fail(BFE_ERR_JOURNAL,
                                   "the filesystem's journal is on a separate device (an external journal), which this engine cannot reach; it can be opened read-only");
                ext4_umount(BFX_MOUNT_POINT);
                BFV_UnlockDisk(&BFX_locks);
                BFX_Leave();
                return(iStatus);
            }

            /* fast_commit (Linux 5.10 onward) keeps a second log of small    */
            /* changes at the end of the journal that only the Linux driver   */
            /* can replay.  While the filesystem is marked as needing         */
            /* recovery those fast commits may be the newest truth, and a     */
            /* replay of the ordinary journal alone -- which is what STEP 4   */
            /* would do -- would write over them: so such an image is not     */
            /* opened for writing until a Linux mount has replayed it.  A     */
            /* cleanly unmounted fast_commit filesystem has nothing pending   */
            /* and opens as usual.                                            */
            if (ext4_sb_feature_com(pSbCheck, EXT4_FCOM_FAST_COMMIT) &&
                ext4_sb_feature_incom(pSbCheck, EXT4_FINCOM_RECOVER))
            {   /* Pending fast commits: refuse rather than replay half of them. */

                /* Report the refusal and close the session down, unmounting   */
                /* before the volume locks go, as above.  The journal has      */
                /* not been touched, so the fast commits are still there for   */
                /* a Linux mount to replay.                                    */
                iStatus = BFX_Fail(BFE_ERR_JOURNAL,
                                   "the filesystem was not cleanly unmounted and may hold fast commits this engine cannot replay; mount it on Linux once (or open it read-only)");
                ext4_umount(BFX_MOUNT_POINT);
                BFV_UnlockDisk(&BFX_locks);
                BFX_Leave();
                return(iStatus);
            }
        }
    }

    /* STEP 4: a writable session brings the journal up before anything is   */
    /* written.                                                              */
    if (!bReadOnly)
    {   /* Recover, start the journal, enable write-back. */

        /* Replay whatever transactions the journal still holds -- changes   */
        /* a previous writer committed to the journal but had not yet put    */
        /* in place, because it was unplugged or crashed.  This must come    */
        /* BEFORE anything new is written, or the replay would later write   */
        /* those older changes over the new ones.                            */
        rc = ext4_recover(BFX_MOUNT_POINT);

        if (rc != 0 && rc != ENOTSUP)
        {   /* A real replay failure -- ENOTSUP only means the filesystem has   */
            /* no journal (ext2), which is fine.  Writing on top of a journal   */
            /* that could not be replayed is not safe, so the open fails.       */

            /* Report the failure and close the session down, unmounting   */
            /* before the volume locks go, as above.  No journal to stop   */
            /* and no cache to drain: neither has been started yet.  Any   */
            /* part of the replay already written stays written.           */
            iStatus = BFX_Fail(BFE_ERR_JOURNAL, "journal recovery failed: %s", strerror(rc));
            ext4_umount(BFX_MOUNT_POINT);
            BFV_UnlockDisk(&BFX_locks);
            BFX_Leave();
            return(iStatus);
        }

        /* Start journaling, so every change of this session is written to   */
        /* the journal first and a crash mid-session can be replayed.        */
        rc = ext4_journal_start(BFX_MOUNT_POINT);

        if (rc != 0)
        {   /* Starting is transparent on a journal-less filesystem, so a   */
            /* failure here is real.                                        */

            /* Report the failure and close the session down, unmounting      */
            /* before the volume locks go, as above.  The journal did not     */
            /* start and write-back is still off, so neither needs undoing.   */
            iStatus = BFX_Fail(BFE_ERR_JOURNAL, "cannot start the journal: %s", strerror(rc));
            ext4_umount(BFX_MOUNT_POINT);
            BFV_UnlockDisk(&BFX_locks);
            BFX_Leave();
            return(iStatus);
        }

        /* Batch writes behind the cache until BFE_Flush or BFE_Close.        */
        /* Without write-back, lwext4 writes every changed block through at   */
        /* once, which on a card means many small writes per change; with     */
        /* it, a batch of edits costs one drain.  The price is that nothing   */
        /* is on the media until the drain -- which is what BFE_Flush is      */
        /* for, and why BFE_Close drains before anything else.                */
        ext4_cache_write_back(BFX_MOUNT_POINT, true);
    }

    /* STEP 5: keep the superblock for the inode accessors.  It is lwext4's   */
    /* own copy inside the mount, so it stays valid until the unmount.        */
    BFX_pSuperblock = NULL;
    rc = ext4_get_sblock(BFX_MOUNT_POINT, &BFX_pSuperblock);

    if (rc != 0 || BFX_pSuperblock == NULL)
    {   /* Mounted, yet no superblock: undo the open rather than run without.   */
        /* Every inode read needs it, so a session without it could not         */
        /* list or stat anything.                                               */

        /* Report the failure. */
        iStatus = BFX_Fail(BFE_ERR_MOUNT, "cannot read the superblock");

        if (!bReadOnly)
        {   /* A writable session got as far as STEP 4. */

            /* Stop what STEP 4 started, in BFE_Close's order: write-back    */
            /* off (it drains the cache), then the journal.  Their results   */
            /* are not checked -- the open is failing anyway, and the        */
            /* reason already recorded is the one to report.                 */
            ext4_cache_write_back(BFX_MOUNT_POINT, false);
            ext4_journal_stop(BFX_MOUNT_POINT);
        }

        /* Close the session down, unmounting before the volume locks go,   */
        /* as above.                                                        */
        ext4_umount(BFX_MOUNT_POINT);
        BFV_UnlockDisk(&BFX_locks);
        BFX_Leave();
        return(iStatus);
    }

    /* The session's mode, for BFX_Enter's write check on every later call,   */
    /* and its journal state: a writable session's journal was started in     */
    /* STEP 4 (on a filesystem with none, the start did nothing and the       */
    /* stop will do nothing), and no change is barred yet.                    */
    BFX_bChangesBarred = FALSE;

    if (bReadOnly)
    {   /* Remember the mode for BFX_Enter's write check; no journal runs. */
        BFX_bReadOnly  = TRUE;
        BFX_bJournalOn = FALSE;
    }
    else
    {   /* A writable session, its journal running. */
        BFX_bReadOnly  = FALSE;
        BFX_bJournalOn = TRUE;
    }

    /* Only now, with everything in place, is the session marked open --    */
    /* every failure above leaves BFX_bOpen FALSE and nothing held -- and   */
    /* as lwext4's.                                                         */
    BFX_iFsKind = BFX_FS_EXT;
    BFX_bOpen   = TRUE;

    /* The open is over; free the engine for the session's calls. */
    BFX_Leave();

    /* The image is open and mounted. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFE_Close:                                                                 */
/*                                                                            */
/* Flush and close the open image, of either kind, leaving the image file     */
/* consistent and closed, and give a device's volumes back to Windows.        */
/*                                                                            */
/* Arguments:                                                                 */
/*     None.                                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_NOT_OPEN when nothing was open; BFE_ERR_IO_IMAGE */
/*           when the final flush or unmount failed (on FAT, a guard);        */
/*           BFE_ERR_VERIFY when a device write of the session read back      */
/*           different (ext2/3/4); BFE_ERR_JOURNAL                            */
/*           when a flush of the session lost the journal (the image wants a  */
/*           check).  The session is closed regardless -- there is nothing a  */
/*           retry could do.                                                  */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_Close(void)
{
    int iStatus;
    int rcFlush;
    int rcJournal;
    int rcUmount;

    /* The gate: the busy flag, and an image must be open -- of either kind. */
    iStatus = BFX_EnterAnyFs(TRUE, FALSE);

    if (iStatus != BFE_OK)
    {   /* Busy, or nothing open. */
        return(iStatus);
    }

    if (BFX_iFsKind == BFX_FS_FAT)
    {   /* A FAT session: nothing to drain -- FatFs wrote and synced a      */
        /* writable session's every change as it was made, so a write the   */
        /* device dropped failed that change's own call, not this one --    */
        /* and no journal.  FatFs lets go of the volume and the target is   */
        /* closed                                                           */
        /* (its close flushes it); a failure is recorded by BFT_Unmount.    */
        /* The volume locks of a writable device session go after it, as    */
        /* below; a read-only session took none.                            */
        iStatus = BFT_Unmount();
        BFV_UnlockDisk(&BFX_locks);
        BFX_bOpen   = FALSE;
        BFX_iFsKind = BFX_FS_EXT;

        /* Free the engine; the session is over either way. */
        BFX_Leave();

        /* BFE_OK, or the unmount's failure. */
        return(iStatus);
    }

    /* A read-only session has no cache to drain and no journal running, so   */
    /* both results start as success and only a writable session sets them.   */
    rcFlush   = 0;
    rcJournal = 0;

    if (!BFX_bReadOnly)
    {   /* Writable session: drain the cache FIRST -- turning write-back off   */
        /* is what makes lwext4 write every cached block -- then close the     */
        /* journal, before the unmount below.  Each step runs even if the      */
        /* one before failed -- the session ends regardless, and every         */
        /* result is reported together below.                                  */
        rcFlush = ext4_cache_write_back(BFX_MOUNT_POINT, false);

        if (BFX_bJournalOn)
        {   /* The journal is running: stop it.  Only then -- once a flush    */
            /* lost it, lwext4 has already let it go, and stopping it again   */
            /* would work on journal state that no longer exists.             */
            rcJournal = ext4_journal_stop(BFX_MOUNT_POINT);
        }
    }

    /* No journal runs past this point, whatever the stop returned. */
    BFX_bJournalOn = FALSE;

    /* Unmount, which also closes the block device beneath. */
    rcUmount = ext4_umount(BFX_MOUNT_POINT);

    /* The session is over whatever those returned.  The volume locks of a     */
    /* device session go AFTER the unmount, which is what closed the device:   */
    /* Windows remounts the volumes the moment the locks go, and must not do   */
    /* so before the last write has reached the media.                         */
    /* BFX_bReadOnly is left as it was: it is read only while a session is     */
    /* open (no caller asks to write with no image open), and the next         */
    /* BFE_Open sets it again.  The superblock pointer is cleared because it   */
    /* pointed into the mount just taken down.                                 */
    BFV_UnlockDisk(&BFX_locks);
    BFX_bOpen       = FALSE;
    BFX_pSuperblock = NULL;

    /* Now the verdict, worst first.  A failed step of the close means the   */
    /* image may not hold everything: e2fsck should look at it.  Failing     */
    /* that, a write the device did not keep -- which the steps themselves   */
    /* cannot see, since the card reported each write as done.               */
    if (rcFlush != 0 || rcJournal != 0 || rcUmount != 0)
    {   /* Something in the final commit failed: the image may be incomplete. */
        iStatus = BFX_Fail(BFE_ERR_IO_IMAGE,
                           "closing the image failed (flush %d, journal %d, unmount %d); run e2fsck on it",
                           rcFlush, rcJournal, rcUmount);
    }
    else if (BFD_WriteNotKept(NULL))
    {   /* The close's own writes reported success, but a device write of   */
        /* this session read back different: the card did not keep it.      */
        iStatus = BFX_Fail(BFE_ERR_VERIFY, "the device did not keep what was written to it; the filesystem on it is damaged");
    }
    else if (BFX_bChangesBarred)
    {   /* The close went through, but a flush of this session could not       */
        /* checkpoint or restart the journal: it may still hold transactions   */
        /* the next mount replays, and what was changed before then was not    */
        /* all checkpointed.  Said once more here, as the session ends.        */
        iStatus = BFX_Fail(BFE_ERR_JOURNAL, "the session's journal stopped at a flush; run e2fsck on the image before using it");
    }

    /* Free the engine; iStatus is still BFX_Enter's BFE_OK if nothing failed. */
    BFX_Leave();

    /* BFE_OK, or the failure recorded above. */
    return(iStatus);
}

/*----------------------------------------------------------------------------*/
/* BFE_Flush:                                                                 */
/*                                                                            */
/* Commit everything a writable session has changed to the image file, and    */
/* leave the session open.  The GUI calls this after each completed           */
/* operation so that a crash -- of the GUI or of the machine -- cannot lose   */
/* work the user was told had finished.                                       */
/*                                                                            */
/* A journal that cannot be checkpointed or restarted here is GONE, and the   */
/* session then takes no more changes: they would be written unjournaled.     */
/* It stays open for reading                                                  */
/* and copying out, every modifying call is refused with BFE_ERR_JOURNAL      */
/* (BFX_Enter), and BFE_Close says once more that the image wants a check.    */
/*                                                                            */
/* Arguments:                                                                 */
/*     None.                                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK (also on a read-only session, or one whose journal an     */
/*           earlier flush lost -- neither has anything to commit);           */
/*           BFE_ERR_NOT_OPEN; BFE_ERR_IO_IMAGE when the cache could not be   */
/*           written; BFE_ERR_JOURNAL when the journal could not be           */
/*           checkpointed or restarted (changes barred from then on);         */
/*           BFE_ERR_VERIFY when a device write of the session read back      */
/*           different.                                                       */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_Flush(void)
{
    int iStatus;
    int rc;

    /* The gate: the busy flag, and an image must be open -- of either kind,   */
    /* since a FAT session has nothing to commit and is answered just below.   */
    iStatus = BFX_EnterAnyFs(TRUE, FALSE);

    if (iStatus != BFE_OK)
    {   /* Busy, or nothing open. */
        return(iStatus);
    }

    if (BFX_bReadOnly || BFX_bChangesBarred || BFX_iFsKind == BFX_FS_FAT)
    {   /* Nothing to commit -- a read-only session; one whose journal an      */
        /* earlier flush lost, which has drained its cache and refused every   */
        /* change since; or a FAT session, whose every change FatFs has        */
        /* already written through (each file close, mkdir, unlink and         */
        /* rename syncs the volume).                                           */

        /* Free the engine and report success. */
        BFX_Leave();
        return(BFE_OK);
    }

    /* lwext4 has no "flush" call as such: it writes the cache when        */
    /* write-back mode is turned OFF.  So a flush is BFE_Close's own       */
    /* shutdown pair -- write-back off, journal stop, the sequence         */
    /* already known to leave a consistent image -- followed by starting   */
    /* both again.  Turning write-back off writes every cached block.      */
    rc = ext4_cache_write_back(BFX_MOUNT_POINT, false);

    if (rc != 0)
    {   /* The cache could not be written out (a full or failing host disk). */

        /* Report it, and turn write-back on again so the session stays in    */
        /* the mode BFE_Close expects to undo; the journal was not touched.   */
        iStatus = BFX_Fail(BFE_ERR_IO_IMAGE, "cannot write the cache to the image: %s", strerror(rc));
        ext4_cache_write_back(BFX_MOUNT_POINT, true);
        BFX_Leave();
        return(iStatus);
    }

    /* Stop the journal as a close would -- the stop checkpoints it, so      */
    /* every committed change is in its final place -- then start it again   */
    /* for what follows.  (On a filesystem with no journal both do nothing   */
    /* and succeed.)                                                         */
    rc = ext4_journal_stop(BFX_MOUNT_POINT);

    if (rc == 0)
    {   /* The checkpoint succeeded; resume journaling. */
        rc = ext4_journal_start(BFX_MOUNT_POINT);
    }

    if (rc != 0)
    {   /* The journal could not be checkpointed, or not restarted.  Either    */
        /* way lwext4 has let it go (a failed stop and a failed start both     */
        /* release it), so NO journal runs now.  The session is not left       */
        /* writable on those terms: every further change is refused            */
        /* (BFX_bChangesBarred, tested by BFX_Enter), since it would go        */
        /* unjournaled -- and over a journal that may still hold               */
        /* transactions a later replay would lay over it.  It stays open, so   */
        /* the caller can read and copy out, and BFE_Close knows not to stop   */
        /* a journal that is gone.  Write-back stays OFF: nothing more is      */
        /* written, and BFE_Close's turning it off again only drains an        */
        /* empty cache.  No retry: a journal block that would not write a      */
        /* moment ago is not expected to now, and the image needs a check      */
        /* either way.                                                         */
        BFX_bJournalOn     = FALSE;
        BFX_bChangesBarred = TRUE;
        iStatus = BFX_Fail(BFE_ERR_JOURNAL, "cannot checkpoint or restart the journal: %s; this session takes no more changes "
                                            "-- close the image and check it before writing to it again", strerror(rc));
    }
    else
    {   /* The journal runs again: write-back back on, so the cache mode is   */
        /* what BFE_Close expects to undo.                                    */
        ext4_cache_write_back(BFX_MOUNT_POINT, true);

        if (BFD_WriteNotKept(NULL))
        {   /* Everything reported success, yet a device write of this       */
            /* session read back different: the change is NOT on the card.   */
            iStatus = BFX_Fail(BFE_ERR_VERIFY, "the device did not keep what was written to it");
        }
    }

    /* Free the engine; the session stays open either way. */
    BFX_Leave();

    /* BFE_OK, or the failure recorded above. */
    return(iStatus);
}

/*----------------------------------------------------------------------------*/
/* BFE_SetWriteVerify:                                                        */
/*                                                                            */
/* Whether an ordinary session's writes to a DEVICE are read back and         */
/* compared, the check that catches a card which accepts writes and keeps     */
/* none of them (a counterfeit, or one failing).  Off by default: it roughly  */
/* doubles a device session's I/O, which is too high a price on every write   */
/* of normal browsing and editing, so it is an option the user turns on.      */
/* A format, a resize and a repair read back whatever this says.  The setting */
/* holds for every session opened afterwards, and takes effect at once on a   */
/* session already open.  A file is never read back.                          */
/*                                                                            */
/* Arguments:                                                                 */
/*     bOn : non-zero to read a session's device writes back, 0 not to.       */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK, or BFE_ERR_BUSY while another call runs.                 */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_SetWriteVerify(uint32_t bOn)
{
    int iStatus;

    /* The gate: the busy flag only -- the setting may be made with or   */
    /* without an image open.                                            */
    iStatus = BFX_Enter(FALSE, FALSE);

    if (iStatus != BFE_OK)
    {   /* Another call is running. */
        return(iStatus);
    }

    /* Remember the choice for every later BFE_Open (which hands it to the   */
    /* block device as it configures it).                                    */
    BFX_bVerifySessions = (bOn != 0);

    if (BFX_bOpen)
    {   /* The open session was configured already: it changes now too. */
        BFD_SetVerify(BFX_bVerifySessions);
    }

    /* Free the engine. */
    BFX_Leave();

    /* Recorded. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFE_VolumeInfoGet:                                                         */
/*                                                                            */
/* Report the open filesystem's size, free space, label and feature flags.    */
/* The facts a caller most often wants -- metadata_csum, dir_index, the       */
/* session's mode, an external journal -- are picked out as flags so it need  */
/* not decode the raw feature words, which are reported as well.  On a FAT    */
/* session the facts come from FatFs.                                         */
/*                                                                            */
/* Arguments:                                                                 */
/*     pInfo : receives the facts.                                            */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_ARG for a NULL pointer; BFE_ERR_NOT_OPEN;        */
/*           BFE_ERR_IO_IMAGE when lwext4 cannot supply the statistics, or    */
/*           FatFs the free count or the label (BFT_VolumeInfo's mapped       */
/*           code).                                                           */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_VolumeInfoGet(BFE_VolumeInfo *pInfo)
{
    struct ext4_mount_stats stats;
    int                     iStatus;
    int                     rc;

    if (pInfo == NULL)
    {   /* Nowhere to put the answer (refused before the gate, so no   */
        /* last-error line is written for it).                         */
        return(BFE_ERR_ARG);
    }

    /* The gate: the busy flag, and an image must be open -- of either kind. */
    iStatus = BFX_EnterAnyFs(TRUE, FALSE);

    if (iStatus != BFE_OK)
    {   /* Busy, or nothing open. */
        return(iStatus);
    }

    if (BFX_iFsKind == BFX_FS_FAT)
    {   /* A FAT session: its clusters as blocks, no inodes and no ext4   */
        /* feature words (BFT_VolumeInfo fills it, recording any          */
        /* failure); the session's mode is set here.                      */
        iStatus = BFT_VolumeInfo(pInfo);

        if (iStatus == BFE_OK && BFX_bReadOnly)
        {   /* The session's own mode, which bfe_fat.c does not hold. */
            pInfo->bReadOnly = 1;
        }

        /* Free the engine, the facts filled or the failure recorded. */
        BFX_Leave();

        /* BFE_OK, or the failure recorded. */
        return(iStatus);
    }

    /* Both structures start at zero: every flag below is set only when     */
    /* true, and the zeros terminate the label (see its copy at the end).   */
    memset(pInfo, 0, sizeof(*pInfo));
    memset(&stats, 0, sizeof(stats));

    /* lwext4's own summary of the mounted filesystem. */
    rc = ext4_mount_point_stats(BFX_MOUNT_POINT, &stats);

    if (rc != 0)
    {   /* lwext4 could not read its own mount statistics. */

        /* Report it; the session itself is unaffected and stays open. */
        iStatus = BFX_Fail(BFE_ERR_IO_IMAGE, "cannot read the filesystem statistics: %s", strerror(rc));
        BFX_Leave();
        return(iStatus);
    }

    /* The kind -- one value for ext2/3/4, whose feature words below tell   */
    /* them apart -- then the sizes and counts, straight from lwext4.       */
    pInfo->uiVolumeType  = BFE_VOLTYPE_EXT;
    pInfo->ullBlockCount = stats.blocks_count;
    pInfo->ullFreeBlocks = stats.free_blocks_count;
    pInfo->uiBlockSize   = stats.block_size;
    pInfo->uiInodeCount  = stats.inodes_count;
    pInfo->uiFreeInodes  = stats.free_inodes_count;

    /* The raw feature words (stored little-endian on disk), then the four     */
    /* facts the GUI shows as plain flags, so it need not decode the words:    */
    /* metadata_csum, dir_index, the session's own mode, and an external       */
    /* journal.  Each flag is set only when true; the zeroing gave the rest.   */
    pInfo->uiFeatureCompat   = to_le32(BFX_pSuperblock->features_compatible);
    pInfo->uiFeatureIncompat = to_le32(BFX_pSuperblock->features_incompatible);
    pInfo->uiFeatureRoCompat = to_le32(BFX_pSuperblock->features_read_only);

    if ((pInfo->uiFeatureRoCompat & EXT4_FRO_COM_METADATA_CSUM) != 0)
    {   /* metadata_csum is on. */
        pInfo->bMetadataCsum = 1;
    }

    if ((pInfo->uiFeatureCompat & EXT4_FCOM_DIR_INDEX) != 0)
    {   /* dir_index is on. */
        pInfo->bDirIndex = 1;
    }

    if (BFX_bReadOnly)
    {   /* Report the session's own mode too. */
        pInfo->bReadOnly = 1;
    }

    if (BFX_HasExternalJournal(BFX_pSuperblock))
    {   /* The journal is on another device: the reason a writable open is   */
        /* refused, worth showing to whoever asks why.                       */
        pInfo->bExternalJournal = 1;
    }

    /* The label is 16 bytes on disk and need not be terminated; the     */
    /* structure was zeroed, so copying 16 bytes leaves it terminated.   */
    memcpy(pInfo->szVolumeName, stats.volume_name, sizeof(stats.volume_name));

    /* Free the engine. */
    BFX_Leave();

    /* The structure is filled. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFE_LastErrorText:                                                         */
/*                                                                            */
/* Copy the detail line of the most recent failure into the caller's buffer.  */
/* It does not pass the gate, deliberately: the GUI asks for the text right   */
/* after a failed call, and the text is only ever written by a call that      */
/* holds the busy flag -- so, called from the one worker thread as the        */
/* contract requires, it cannot be read half-written.  (BFX_Enter's refusals  */
/* are the exception to "holds the flag", and the one-thread contract is what */
/* makes it harmless: the defect exists and is intentionally deferred.)       */
/*                                                                            */
/* The text is the LAST failure's, whatever call made it: a successful call   */
/* does not clear it, so it is meaningful only straight after a call that     */
/* failed.                                                                    */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszBuffer : receives the UTF-8 text, NUL-terminated.                   */
/*     cbBuffer  : the buffer's size in bytes.                                */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_ARG for a NULL or zero-size buffer;              */
/*           BFE_ERR_BUFFER when the text does not fit (nothing is copied).   */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_LastErrorText(char *pszBuffer, size_t cbBuffer)
{
    size_t cbText;

    if (pszBuffer == NULL || cbBuffer == 0)
    {   /* Nowhere to put the text. */
        return(BFE_ERR_ARG);
    }

    /* The stored line's length; it is always terminated (BFX_Fail). */
    cbText = strlen(BFX_szLastError);

    if (cbText + 1 > cbBuffer)
    {   /* The text and its NUL do not fit. */

        /* Leave the caller an empty string, never a cut-off line, and   */
        /* say the buffer was too small.                                 */
        pszBuffer[0] = '\0';
        return(BFE_ERR_BUFFER);
    }

    /* The whole line with its NUL. */
    memcpy(pszBuffer, BFX_szLastError, cbText + 1);

    /* The text was copied. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFE_ListDir:                                                               */
/*                                                                            */
/* Hand the caller every entry of a directory, one callback each, with the    */
/* full inode facts a file-manager row shows.  A FAT session's listing is     */
/* BFT_ListDir's, under the same every-entry-or-none promise.                 */
/*                                                                            */
/* An entry whose inode cannot be read is STILL reported -- with its name,    */
/* type BFE_TYPE_UNKNOWN and zeros -- because a damaged entry the user cannot */
/* see is one they cannot delete either.                                      */
/*                                                                            */
/* The entries come in the order the directory holds them (no sorting here;   */
/* the GUI sorts).  A directory block that cannot be read fails the listing   */
/* (BFX_CollectNames): no entry is reported, so a partial directory is never  */
/* shown as the whole.                                                        */
/*                                                                            */
/* The callback runs INSIDE this call, with the busy flag held, so it must    */
/* not call back into the engine (it would get BFE_ERR_BUSY).                 */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImageDir : the directory to list.                                   */
/*     pfnEntry    : called once per entry; a non-zero return stops early.    */
/*     pvContext   : passed to pfnEntry unchanged.                            */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK (also when stopped early by the callback); BFE_ERR_ARG;   */
/*           BFE_ERR_NOT_OPEN; BFE_ERR_NOT_FOUND / BFE_ERR_NOT_DIR when the   */
/*           path is not a readable directory; BFE_ERR_NO_MEMORY;             */
/*           BFE_ERR_IO_IMAGE when a block of it cannot be read; on FAT,      */
/*           BFT_ListDir's codes (BFE_ERR_NAME among them, a guard).          */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_ListDir(const char *pszImageDir, BFE_ListCallback pfnEntry, void *pvContext)
{
    BFE_Entry   entry;
    char      **ppszNames;
    char       *pszChild;
    int         nNames;
    int         iStatus;
    int         rc;
    int         i;

    if (!BFX_PathValid(pszImageDir) || pfnEntry == NULL)
    {   /* A path BFX_PathValid refuses (missing, relative, not canonical, or a   */
        /* FAT name FAT cannot hold), or nobody to tell.                          */
        return(BFE_ERR_ARG);
    }

    /* The gate: the busy flag, and an image must be open -- of either kind. */
    iStatus = BFX_EnterAnyFs(TRUE, FALSE);

    if (iStatus != BFE_OK)
    {   /* Busy, or nothing open. */
        return(iStatus);
    }

    if (BFX_iFsKind == BFX_FS_FAT)
    {   /* A FAT session: BFT_ListDir keeps the same promise -- every entry,   */
        /* or none -- and records any failure.                                 */
        iStatus = BFT_ListDir(pszImageDir, pfnEntry, pvContext);
        BFX_Leave();

        /* BFE_OK, or the failure recorded. */
        return(iStatus);
    }

    /* Every name first, with the directory closed again (BFX_CollectNames). */
    rc = BFX_CollectNames(pszImageDir, &ppszNames, &nNames);

    if (rc != 0)
    {   /* The directory could not be read. */
        return(BFX_Finish(rc, "list", pszImageDir));
    }

    for (i = 0; i < nNames; i++)
    {   /* One directory entry per iteration: its full path, its inode's   */
        /* facts, then the caller's callback.                              */
        pszChild = BFU_JoinImage(pszImageDir, ppszNames[i]);

        if (pszChild == NULL)
        {   /* Out of memory composing the child's path. */

            /* Record it and stop listing; the names and the engine are   */
            /* released after the loop, as on every way out of it.        */
            iStatus = BFX_Fail(BFE_ERR_NO_MEMORY, "out of memory listing %s", pszImageDir);
            break;
        }

        /* Read the entry's facts.  Its result is deliberately ignored: a      */
        /* failed inode read leaves the entry holding the name and zeros,      */
        /* which is exactly what is reported for it.  The path is not needed   */
        /* after the read.                                                     */
        BFX_FillEntry(pszChild, ppszNames[i], &entry);
        free(pszChild);

        /* Hand the entry to the caller, who may ask to stop. */
        if (pfnEntry(&entry, pvContext) != 0)
        {   /* The caller has seen enough. */
            break;
        }
    }

    /* Release the names and the engine, however the loop ended. */
    BFX_FreeNames(ppszNames, nNames);
    BFX_Leave();

    /* BFE_OK, or the out-of-memory failure recorded in the loop. */
    return(iStatus);
}

/*----------------------------------------------------------------------------*/
/* BFE_Stat:                                                                  */
/*                                                                            */
/* Fill a BFE_Entry for one path.  A final symbolic link is described, not    */
/* followed.  On a FAT session the facts are FatFs's, laid out as a FAT       */
/* listing shows them (BFX_FillEntry's FAT arm).                              */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImagePath : the path to describe.                                   */
/*     pEntry       : receives the facts; its name is the path's last         */
/*                    component ("/" for the root).                           */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_ARG; BFE_ERR_NOT_OPEN; BFE_ERR_NOT_FOUND when    */
/*           the path does not exist; BFE_ERR_IO_IMAGE.                       */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_Stat(const char *pszImagePath, BFE_Entry *pEntry)
{
    int iStatus;
    int rc;

    if (!BFX_PathValid(pszImagePath) || pEntry == NULL)
    {   /* A path BFX_PathValid refuses (missing, relative, not canonical, or a   */
        /* FAT name FAT cannot hold), or nowhere to put the answer.               */
        return(BFE_ERR_ARG);
    }

    /* The gate: the busy flag, and an image must be open -- of either kind,   */
    /* since BFX_FillEntry answers for a FAT session as well.                  */
    iStatus = BFX_EnterAnyFs(TRUE, FALSE);

    if (iStatus != BFE_OK)
    {   /* Busy, or nothing open. */
        return(iStatus);
    }

    /* The facts, under the path's last component as the name. */
    rc = BFX_FillEntry(pszImagePath, BFU_LastComponent(pszImagePath), pEntry);

    /* Map the result, record a detail line on failure, release the gate. */
    return(BFX_Finish(rc, "stat", pszImagePath));
}

/*----------------------------------------------------------------------------*/
/* BFE_ReadLink:                                                              */
/*                                                                            */
/* Copy a symbolic link's target text into the caller's buffer.               */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImagePath : the symbolic link.                                      */
/*     pszBuffer    : receives the target, UTF-8, NUL-terminated.             */
/*     cbBuffer     : the buffer's size in bytes.                             */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_ARG; BFE_ERR_NOT_OPEN; BFE_ERR_NOT_FOUND;        */
/*           BFE_ERR_NOT_SYMLINK; BFE_ERR_BUFFER when the target and its NUL  */
/*           do not fit; BFE_ERR_IO_IMAGE; BFE_ERR_UNSUPPORTED on a FAT       */
/*           session, which has no links (the gate, BFX_Enter).               */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_ReadLink(const char *pszImagePath, char *pszBuffer, size_t cbBuffer)
{
    BFE_Entry entry;
    size_t    cbRead;
    int       iStatus;
    int       rc;

    if (!BFX_PathValid(pszImagePath) || pszBuffer == NULL || cbBuffer == 0)
    {   /* A path BFX_PathValid refuses (missing, relative, not canonical, or a   */
        /* FAT name FAT cannot hold), or nowhere to put the target.               */
        return(BFE_ERR_ARG);
    }

    /* The gate: the busy flag, and an image must be open. */
    iStatus = BFX_Enter(TRUE, FALSE);

    if (iStatus != BFE_OK)
    {   /* Busy, or nothing open. */
        return(iStatus);
    }

    /* The inode first: its type and its size -- a link's size IS the   */
    /* length of its target -- so that "this is not a link" and "the    */
    /* buffer is too small" are reported as themselves.                 */
    rc = BFX_FillEntry(pszImagePath, BFU_LastComponent(pszImagePath), &entry);

    if (rc != 0)
    {   /* The path does not exist or cannot be read. */
        return(BFX_Finish(rc, "read the link", pszImagePath));
    }

    if (entry.uiType != BFE_TYPE_SYMLINK)
    {   /* A file or directory has no target. */

        /* Refuse it. */
        iStatus = BFX_Fail(BFE_ERR_NOT_SYMLINK, "%s is not a symbolic link", pszImagePath);
        BFX_Leave();
        return(iStatus);
    }

    if (entry.ullSize + 1 > (uint64_t)cbBuffer)
    {   /* The target and its NUL need more room than the caller gave. */

        /* Refuse it, saying how many bytes are needed, so the caller can   */
        /* ask again with a buffer that size.                               */
        iStatus = BFX_Fail(BFE_ERR_BUFFER, "the link target of %s needs %llu bytes",
                           pszImagePath, (unsigned long long)(entry.ullSize + 1));
        BFX_Leave();
        return(iStatus);
    }

    /* The read (lwext4's, through BFS_ReadLink) copies the target without        */
    /* terminating it, so the last byte of the buffer is held back for the NUL.   */
    cbRead = 0;
    rc     = BFS_ReadLink(pszImagePath, pszBuffer, cbBuffer - 1, &cbRead);

    if (rc == 0)
    {   /* Terminate what was read (cbRead is at most cbBuffer - 1). */
        pszBuffer[cbRead] = '\0';
    }

    /* Map the result, record a detail line on failure, release the gate. */
    return(BFX_Finish(rc, "read the link", pszImagePath));
}

/*----------------------------------------------------------------------------*/
/* BFE_MakeDir:                                                               */
/*                                                                            */
/* Create one directory.  The parent must already exist, and is tested here   */
/* (BFX_CheckParent): lwext4's mkdir would make every missing level of the    */
/* path, FatFs's refuses a missing one.  A file manager only ever makes a     */
/* folder inside the one being shown, so the test guards other callers.       */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImageDir : the directory to create.                                 */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_ARG; BFE_ERR_NOT_OPEN; BFE_ERR_READ_ONLY;        */
/*           BFE_ERR_EXISTS when the name is taken; BFE_ERR_NOT_FOUND when    */
/*           the parent is missing, BFE_ERR_NOT_DIR when it is not a          */
/*           directory (nothing made either way); BFE_ERR_NO_SPACE (on FAT    */
/*           also for a full directory); BFE_ERR_IO_IMAGE.                    */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_MakeDir(const char *pszImageDir)
{
    BFE_Entry entry;
    int       iStatus;
    int       rc;

    if (!BFX_PathValid(pszImageDir))
    {   /* A path BFX_PathValid refuses: missing, relative, not canonical, or a FAT name FAT cannot hold. */
        return(BFE_ERR_ARG);
    }

    /* The gate: the busy flag, an open image of either kind (a FAT one   */
    /* through bfe_fs.c's FAT arms), and a writable session.              */
    iStatus = BFX_EnterAnyFs(TRUE, TRUE);

    if (iStatus != BFE_OK)
    {   /* Busy, nothing open, or read-only. */
        return(iStatus);
    }

    /* The test and the create below both run under the busy flag, so        */
    /* nothing can take the name between them.  Any failure to read the      */
    /* name -- not only "not found" -- lets the create go ahead and report   */
    /* for itself.                                                           */
    if (BFX_FillEntry(pszImageDir, BFU_LastComponent(pszImageDir), &entry) == 0)
    {   /* Something already has that name.  lwext4's own mkdir quietly        */
        /* succeeds on an existing directory, which would let the GUI report   */
        /* "created" for a folder it did not create -- so test first.          */

        /* Refuse it. */
        iStatus = BFX_Fail(BFE_ERR_EXISTS, "%s already exists", pszImageDir);
        BFX_Leave();
        return(iStatus);
    }

    /* The folder it is to go in must be there, and be a folder: lwext4's   */
    /* mkdir would otherwise make every missing level above it.  Tested     */
    /* under the busy flag, as the name was.                                */
    rc = BFX_CheckParent(pszImageDir);

    if (rc == ENOENT || rc == ENOTDIR)
    {   /* No folder to put it in: refused as the contract says, NOT_FOUND   */
        /* or NOT_DIR, with nothing made.                                    */
        iStatus = BFX_Fail(BFX_MapErrno(rc), "cannot create the directory %s: the folder it is to go in %s",
                           pszImageDir, (rc == ENOENT) ? "does not exist" : "is not a directory");
        BFX_Leave();
        return(iStatus);
    }

    if (rc != 0)
    {   /* The parent could not be looked at: that failure, mapped. */
        return(BFX_Finish(rc, "create the directory", pszImageDir));
    }

    /* Create the one directory. */
    rc = BFS_DirMake(pszImageDir);

    if (rc == 0)
    {   /* Created: settle its checksum and give it a mode and the time.    */
        /* If this part fails the directory still exists, unfinished, and   */
        /* the failure is what the caller is told.                          */
        rc = BFX_NewDirFinish(pszImageDir, BFX_NEW_DIR_MODE, (uint32_t)time(NULL));
    }

    /* Map the result, record a detail line on failure, release the gate. */
    return(BFX_Finish(rc, "create the directory", pszImageDir));
}

/*----------------------------------------------------------------------------*/
/* BFE_Remove:                                                                */
/*                                                                            */
/* Remove one file, symbolic link or special node.  A directory is refused    */
/* here so that "delete" on a folder is always a deliberate choice between    */
/* BFE_RemoveDir (empty only) and BFE_RemoveTree (everything under it).  A    */
/* file with other names keeps them, and its inode-change time becomes the    */
/* present, its link count having changed, as on Linux.                       */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImagePath : the entry to remove.                                    */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_ARG; BFE_ERR_NOT_OPEN; BFE_ERR_READ_ONLY;        */
/*           BFE_ERR_NOT_FOUND; BFE_ERR_IS_DIR; BFE_ERR_IO_IMAGE.             */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_Remove(const char *pszImagePath)
{
    BFE_Entry entry;
    int       iStatus;
    int       rc;

    if (!BFX_PathValid(pszImagePath))
    {   /* A path BFX_PathValid refuses: missing, relative, not canonical, or a FAT name FAT cannot hold. */
        return(BFE_ERR_ARG);
    }

    /* The gate: the busy flag, an open image of either kind, and a   */
    /* writable session.                                              */
    iStatus = BFX_EnterAnyFs(TRUE, TRUE);

    if (iStatus != BFE_OK)
    {   /* Busy, nothing open, or read-only. */
        return(iStatus);
    }

    /* Look first: the entry must exist and must not be a directory. */
    rc = BFX_FillEntry(pszImagePath, BFU_LastComponent(pszImagePath), &entry);

    if (rc != 0)
    {   /* Nothing there to remove. */
        return(BFX_Finish(rc, "remove", pszImagePath));
    }

    if (entry.uiType == BFE_TYPE_DIR)
    {   /* A directory goes through BFE_RemoveDir or BFE_RemoveTree. */

        /* Refuse it. */
        iStatus = BFX_Fail(BFE_ERR_IS_DIR, "%s is a directory", pszImagePath);
        BFX_Leave();
        return(iStatus);
    }

    if (entry.uiLinks > 1)
    {   /* The file has other names, which remain: its link count is about   */
        /* to drop, and Linux sets its inode-change time for that, as        */
        /* BFE_HardLink does when a name is added.  Set NOW, through the     */
        /* name about to go -- once it is gone no path to the inode is       */
        /* known here, and lwext4's unlink sets no time, so the time set     */
        /* is the survivor's.  Should the remove below then fail, the time   */
        /* has moved with nothing else changed, which is harmless.           */
        rc = BFS_SetCtime(pszImagePath, (uint32_t)time(NULL));

        if (rc != 0)
        {   /* The time could not be set: refused before anything is removed. */
            return(BFX_Finish(rc, "remove", pszImagePath));
        }
    }

    /* Remove the name. */
    rc = BFS_Remove(pszImagePath);

    if (rc == 0)
    {   /* Removed: the directory it was in changed, so its times move. */
        rc = BFX_TouchParent(pszImagePath);
    }

    /* Map the result, record a detail line on failure, release the gate. */
    return(BFX_Finish(rc, "remove", pszImagePath));
}

/*----------------------------------------------------------------------------*/
/* BFE_RemoveDir:                                                             */
/*                                                                            */
/* Remove a directory ONLY if it is empty.  A directory that cannot be read   */
/* through is refused, never taken for an empty one, and the root is never    */
/* removed (BFE_ERR_ARG), empty or not.                                       */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImageDir : the directory to remove.                                 */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_ARG; BFE_ERR_NOT_OPEN; BFE_ERR_READ_ONLY;        */
/*           BFE_ERR_NOT_FOUND / BFE_ERR_NOT_DIR; BFE_ERR_NOT_EMPTY;          */
/*           BFE_ERR_IO_IMAGE.                                                */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_RemoveDir(const char *pszImageDir)
{
    char **ppszNames;
    int    nNames;
    int    iStatus;
    int    rc;

    if (!BFX_PathValid(pszImageDir) || pszImageDir[1] == '\0')
    {   /* A path BFX_PathValid refuses (missing, relative, not canonical,     */
        /* or a FAT name FAT cannot hold), or the root itself, refused as      */
        /* BFE_RemoveTree refuses it: an empty root would otherwise pass the   */
        /* emptiness test below and reach the libraries' recursive remove,     */
        /* which is never asked to remove a filesystem's own root.             */
        return(BFE_ERR_ARG);
    }

    /* The gate: the busy flag, an open image of either kind (a FAT one   */
    /* through bfe_fs.c's FAT arms), and a writable session.              */
    iStatus = BFX_EnterAnyFs(TRUE, TRUE);

    if (iStatus != BFE_OK)
    {   /* Busy, nothing open, or read-only. */
        return(iStatus);
    }

    /* The emptiness test, made here: lwext4 has no empty-only remove -- its   */
    /* directory remove is recursive, and the FAT side's (BFT_RemoveTree) is   */
    /* too -- so the names are collected, and the recursive remove below is    */
    /* called only once the list has come back empty.  The "empty only"        */
    /* promise is therefore exactly as good as this list, and a block that     */
    /* cannot be read fails the collection itself (BFX_CollectNames), so a     */
    /* damaged directory never reaches the recursive remove.                   */
    rc = BFX_CollectNames(pszImageDir, &ppszNames, &nNames);

    if (rc != 0)
    {   /* Not a directory, it does not exist, a block of it cannot be read   */
        /* (so whether it is empty cannot be told), or no memory: refused,    */
        /* nothing removed.                                                   */
        return(BFX_Finish(rc, "remove the directory", pszImageDir));
    }

    /* Only the count matters; the names themselves are not needed. */
    BFX_FreeNames(ppszNames, nNames);

    if (nNames != 0)
    {   /* It has entries: refuse, which is the whole point of this call. */

        /* Refuse it, with the count, so the user sees how much is there. */
        iStatus = BFX_Fail(BFE_ERR_NOT_EMPTY, "%s is not empty (%d entries)", pszImageDir, nNames);
        BFX_Leave();
        return(iStatus);
    }

    /* Empty: the recursive remove -- lwext4's ext4_dir_rm, or on FAT     */
    /* bfe_fat.c's BFT_RemoveTree, through BFS_DirRemove -- has nothing   */
    /* beneath to take, so it removes just the directory.                 */
    rc = BFS_DirRemove(pszImageDir);

    if (rc == 0)
    {   /* Removed: the directory that held it changed, so its times move. */
        rc = BFX_TouchParent(pszImageDir);
    }

    /* Map the result, record a detail line on failure, release the gate. */
    return(BFX_Finish(rc, "remove the directory", pszImageDir));
}

/*----------------------------------------------------------------------------*/
/* Bfx_RemoveTreeWalk:                                                        */
/*                                                                            */
/* Remove a directory and everything under it, one entry at a time, telling   */
/* the caller of each entry removed and stopping between entries when the     */
/* caller asks -- which lwext4's own recursive remove (ext4_dir_rm), doing    */
/* the whole tree inside one call, has no place for, so the engine walks the  */
/* tree itself on both kinds of filesystem.  The first failure stops the      */
/* walk, leaving what was not yet reached; what was removed stays removed.    */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImageDir : the directory, absolute and canonical (never the root).  */
/*     pfnEvent    : called with BFE_EVENT_DONE after each entry is removed   */
/*                   (the directories included, the top one last); NULL for   */
/*                   none.                                                    */
/*     pvContext   : handed to pfnEvent unchanged.                            */
/*     pnRemoved   : the running count of entries removed, kept across the    */
/*                   recursion; the caller sets it to 0 first.                */
/*     pbCancelled : set to 1 when the callback answered BFE_REPLY_CANCEL;    */
/*                   the caller sets it to 0 first.                           */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0 when the walk ended without a failure (a cancel included --    */
/*           *pbCancelled says which), ENOMEM, or the errno-style result of   */
/*           the first read or removal that failed.                           */
/*----------------------------------------------------------------------------*/
static int Bfx_RemoveTreeWalk(const char *pszImageDir, BFE_CopyCallback pfnEvent, void *pvContext,
                              uint64_t *pnRemoved, int *pbCancelled)
{
    BFE_CopyEvent event;
    BFE_Entry     entry;
    char        **ppszNames;
    char         *pszChild;
    int           nNames;
    int           rc;
    int           i;

    /* The directory's names, read whole and the directory closed, so   */
    /* nothing is removed from under an open directory.                 */
    rc = BFX_CollectNames(pszImageDir, &ppszNames, &nNames);

    if (rc != 0)
    {   /* Unreadable, or out of memory: nothing removed here. */
        return(rc);
    }

    for (i = 0; i < nNames && rc == 0 && *pbCancelled == 0; i++)
    {   /* One child per iteration, until one fails or the caller stops. */
        pszChild = BFU_JoinImage(pszImageDir, ppszNames[i]);

        if (pszChild == NULL)
        {   /* Out of memory for the child's path: the walk stops. */
            rc = ENOMEM;
            break;
        }

        /* What the child is: a directory is emptied by recursion, which      */
        /* also removes it and reports it; anything else -- a file, a link,   */
        /* a device node -- is one removal, reported here.                    */
        rc = BFX_FillEntry(pszChild, ppszNames[i], &entry);

        if (rc != 0)
        {   /* The child cannot be looked at: the walk stops there. */
        }
        else if (entry.uiType == BFE_TYPE_DIR)
        {   /* A directory: everything under it, then itself. */
            rc = Bfx_RemoveTreeWalk(pszChild, pfnEvent, pvContext, pnRemoved, pbCancelled);
        }
        else
        {   /* Not a directory: removed by its name (a hard-linked file   */
            /* loses this name only).                                     */
            rc = BFS_Remove(pszChild);

            if (rc == 0)
            {   /* Removed: counted, and the caller told, who may stop. */
                *pnRemoved = *pnRemoved + 1;

                if (pfnEvent != NULL)
                {   /* A caller to tell: the entry, its type, the count. */
                    memset(&event, 0, sizeof(event));
                    event.uiKind = BFE_EVENT_DONE;
                    event.uiType = entry.uiType;
                    event.pszImagePath = pszChild;
                    event.ullBytesDone = *pnRemoved;
                    if (pfnEvent(&event, pvContext) == BFE_REPLY_CANCEL)
                    {   /* Asked to stop: nothing more is removed. */
                        *pbCancelled = 1;
                    }
                }
            }
        }

        /* The child's path is done with. */
        free(pszChild);
    }

    /* The names are done with, however the walk ended. */
    BFX_FreeNames(ppszNames, nNames);

    if (rc != 0 || *pbCancelled != 0)
    {   /* A failure or a stop: the directory stays, with what was not   */
        /* reached.                                                      */
        return(rc);
    }

    /* Every child is gone: the directory itself, removed last and now   */
    /* empty, so lwext4's or FatFs's directory remove (BFS_DirRemove),   */
    /* recursive though it is, has nothing beneath to take unreported.   */
    rc = BFS_DirRemove(pszImageDir);

    if (rc == 0)
    {   /* Removed: counted, and the caller told, who may stop the rest. */
        *pnRemoved = *pnRemoved + 1;

        if (pfnEvent != NULL)
        {   /* A caller to tell: the directory and the count. */
            memset(&event, 0, sizeof(event));
            event.uiKind = BFE_EVENT_DONE;
            event.uiType = BFE_TYPE_DIR;
            event.pszImagePath = pszImageDir;
            event.ullBytesDone = *pnRemoved;
            if (pfnEvent(&event, pvContext) == BFE_REPLY_CANCEL)
            {   /* Asked to stop: the directories above stay. */
                *pbCancelled = 1;
            }
        }
    }

    /* 0, or the failure that kept the directory. */
    return(rc);
}

/*----------------------------------------------------------------------------*/
/* BFE_RemoveTree:                                                            */
/*                                                                            */
/* Remove a directory and everything under it, reporting each entry removed   */
/* and stopping between entries when asked (Bfx_RemoveTreeWalk).  This        */
/* cannot be undone; the GUI confirms before calling (the delete question of  */
/* CommandQuestionPresenter.cs, warning that a directory goes with            */
/* everything inside it).  A call on a file fails rather than doing something */
/* else.  A stop or a failure part-way leaves what was not yet reached; what  */
/* was removed stays removed.                                                 */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImageDir : the directory to remove with its contents.               */
/*     pfnEvent    : called with BFE_EVENT_DONE after each entry is removed   */
/*                   (pszImagePath the entry, uiType its type, ullBytesDone   */
/*                   the count so far); BFE_REPLY_CANCEL stops before the     */
/*                   next.  NULL for none.                                    */
/*     pvContext   : handed to pfnEvent unchanged.                            */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_CANCELLED; BFE_ERR_ARG (also for the root,       */
/*           which cannot be removed); BFE_ERR_NOT_OPEN; BFE_ERR_READ_ONLY;   */
/*           BFE_ERR_NOT_FOUND; BFE_ERR_NOT_DIR; BFE_ERR_NO_MEMORY;           */
/*           BFE_ERR_IO_IMAGE.                                                */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_RemoveTree(const char *pszImageDir, BFE_CopyCallback pfnEvent, void *pvContext)
{
    BFE_Entry entry;
    uint64_t  nRemoved;
    int       bCancelled;
    int       iStatus;
    int       rc;

    if (!BFX_PathValid(pszImageDir) || pszImageDir[1] == '\0')
    {   /* A path BFX_PathValid refuses (missing, relative, not canonical, or a   */
        /* FAT name FAT cannot hold), or the root itself.                         */
        return(BFE_ERR_ARG);
    }

    /* The gate: the busy flag, an open image of either kind, and a   */
    /* writable session.                                              */
    iStatus = BFX_EnterAnyFs(TRUE, TRUE);

    if (iStatus != BFE_OK)
    {   /* Busy, nothing open, or read-only. */
        return(iStatus);
    }

    /* Look first: it must exist and BE a directory, so a mistaken call on   */
    /* a file fails instead of removing something else.                      */
    rc = BFX_FillEntry(pszImageDir, BFU_LastComponent(pszImageDir), &entry);

    if (rc != 0)
    {   /* Nothing there to remove. */
        return(BFX_Finish(rc, "remove the tree", pszImageDir));
    }

    if (entry.uiType != BFE_TYPE_DIR)
    {   /* Not a directory: BFE_Remove is the call for it. */

        /* Refuse it. */
        iStatus = BFX_Fail(BFE_ERR_NOT_DIR, "%s is not a directory", pszImageDir);
        BFX_Leave();
        return(iStatus);
    }

    /* The engine's own walk, entry by entry: the count and the stop flag   */
    /* start clear.  A failure part-way leaves what it had not reached.     */
    nRemoved = 0;
    bCancelled = 0;
    rc = Bfx_RemoveTreeWalk(pszImageDir, pfnEvent, pvContext, &nRemoved, &bCancelled);

    if (rc == 0 && bCancelled != 0)
    {   /* Stopped by the caller between entries: what was removed stays   */
        /* removed, the rest stays, and the directory with it.             */
        iStatus = BFX_Fail(BFE_ERR_CANCELLED, "removing %s stopped after %llu entries",
                           pszImageDir, (unsigned long long)nRemoved);
        BFX_Leave();
        return(iStatus);
    }

    if (rc == 0)
    {   /* Removed: the directory that held the tree changed, so its times   */
        /* move.  (The directories inside went with it; nothing to touch.)   */
        rc = BFX_TouchParent(pszImageDir);
    }

    /* Map the result, record a detail line on failure, release the gate. */
    return(BFX_Finish(rc, "remove the tree", pszImageDir));
}

/*-----------------------------------------------------------------------------*/
/* BFE_Rename:                                                                 */
/*                                                                             */
/* Rename or move an entry within the image.  The destination is tested        */
/* first and an existing one is refused, so a rename never replaces            */
/* anything: a caller that wants to replace an entry removes it first.  On a   */
/* FAT session a rename to another spelling of the same entry is allowed and   */
/* made in place.  A directory is never moved into its own subtree.  The moved */
/* entry's inode-change time is set to the present, as Linux sets it.          */
/*                                                                             */
/* Arguments:                                                                  */
/*     pszImageFrom : the existing path.                                       */
/*     pszImageTo   : the new path; its parent must exist.                     */
/*                                                                             */
/* Returns:                                                                    */
/*     int : BFE_OK; BFE_ERR_ARG; BFE_ERR_NOT_OPEN; BFE_ERR_READ_ONLY;         */
/*           BFE_ERR_NOT_FOUND; BFE_ERR_EXISTS; BFE_ERR_NO_MEMORY;             */
/*           BFE_ERR_IO_IMAGE.                                                 */
/*-----------------------------------------------------------------------------*/
BFE_API int BFE_Rename(const char *pszImageFrom, const char *pszImageTo)
{
    BFE_Entry  entry;
    char      *pszFromCanon;     // the source as its filesystem spells it (BFX_PathCanonical), for the text tests
    char      *pszToCanon;       // the destination likewise
    size_t     cbFromCanon;
    size_t     cbMatched;
    int        bSameEntry;
    int        bInside;
    int        iStatus;
    int        rc;

    if (!BFX_PathValid(pszImageFrom) || !BFX_PathValid(pszImageTo))
    {   /* A path BFX_PathValid refuses: missing, relative, not canonical, or a FAT name FAT cannot hold. */
        return(BFE_ERR_ARG);
    }

    /* The gate: the busy flag, an open image of either kind, and a   */
    /* writable session.                                              */
    iStatus = BFX_EnterAnyFs(TRUE, TRUE);

    if (iStatus != BFE_OK)
    {   /* Busy, nothing open, or read-only. */
        return(iStatus);
    }

    /* The two paths in the one spelling their filesystem matches them by   */
    /* (BFX_PathCanonical: on FAT, each name's trailing spaces and dots     */
    /* dropped; on ext2/3/4, the paths themselves), so the two text tests   */
    /* below cannot be passed by spelling one entry two ways.  The rename   */
    /* itself goes on with the caller's paths, which FatFs reads alike.     */
    pszFromCanon = BFX_PathCanonical(pszImageFrom);
    pszToCanon   = BFX_PathCanonical(pszImageTo);

    if (pszFromCanon == NULL || pszToCanon == NULL)
    {   /* Out of memory for either copy: refused with nothing changed,   */
        /* whichever copy was made freed (free of NULL is allowed).       */
        free(pszFromCanon);
        free(pszToCanon);
        iStatus = BFX_Fail(BFE_ERR_NO_MEMORY, "out of memory renaming %s", pszImageFrom);
        BFX_Leave();
        return(iStatus);
    }

    /* ANOTHER SPELLING OF THE SAME ENTRY, ON FAT.  FAT matches names letter   */
    /* case aside and without their trailing spaces and dots, so a             */
    /* destination that differs from the source only in those ("/a.txt" to     */
    /* "/A.txt", or to "/a.txt.") names the source itself: the existence       */
    /* test below would find the very entry being moved and refuse the name    */
    /* as taken, and Explorer makes the case-only rename on FAT every day.     */
    /* FatFs's f_rename makes it in place -- it finds the new name on the      */
    /* entry it is moving and registers the new spelling for it, with its      */
    /* trailing spaces and dots dropped -- so such a rename skips the test.    */
    /* The canonical forms are compared with letter case folded by FatFs's     */
    /* own table (BFT_PathFoldPrefix), the fold FatFs finds names by, so a     */
    /* change of case in any letter FatFs folds -- an accented one too -- is   */
    /* recognised, as Windows makes such a rename; and nothing FatFs would     */
    /* see as another entry is.  Matched to the destination's end: the same    */
    /* path.  On ext2/3/4, where names are bytes, no two different             */
    /* spellings are one entry.                                                */
    bSameEntry = FALSE;

    if (BFX_iFsKind == BFX_FS_FAT && strcmp(pszImageFrom, pszImageTo) != 0 &&
        BFT_PathFoldPrefix(pszToCanon, pszFromCanon, &cbMatched) && pszToCanon[cbMatched] == '\0')
    {   /* The source itself, spelt another way. */
        bSameEntry = TRUE;
    }

    /* The destination must not exist; the test and the rename both run   */
    /* under the busy flag, so nothing can take the name in between.  A   */
    /* rename to another spelling of the source on FAT is not tested      */
    /* (above); a path renamed to its own exact spelling is, and is       */
    /* refused as EXISTS.                                                 */
    if (!bSameEntry && BFX_FillEntry(pszImageTo, BFU_LastComponent(pszImageTo), &entry) == 0)
    {   /* The destination name is taken. */

        /* Refuse it, the two copies freed with nothing changed. */
        free(pszFromCanon);
        free(pszToCanon);
        iStatus = BFX_Fail(BFE_ERR_EXISTS, "%s already exists", pszImageTo);
        BFX_Leave();
        return(iStatus);
    }

    /* A directory moved INTO ITS OWN SUBTREE -- the destination is the        */
    /* source followed by '/' -- would be unlinked from its parent and         */
    /* linked under itself: an orphaned loop that no path reaches, its         */
    /* contents lost.  lwext4 does not refuse it (upstream lwext4 PR #146),    */
    /* so the engine does, as BFE_CopyWithin already does; only a whole        */
    /* component counts, so "/a" to "/ab" is an ordinary rename.  The test     */
    /* is exact because it compares the canonical forms, and BFX_PathValid     */
    /* allows no empty, "." or ".." component -- no trailing "/" -- so "/d"    */
    /* cannot reach here as "/d/", nor on FAT escape it as "/d./x".  On a      */
    /* FAT session the comparison folds letter case too, by FatFs's table      */
    /* (BFT_PathFoldPrefix), as FAT's names do, so "/D" into "/d/x" is         */
    /* caught there as well -- and since a letter and its capital may differ   */
    /* in UTF-8 length, the '/' is looked for where the fold's match ended,    */
    /* not at the source's own length.                                         */
    bInside = FALSE;

    if (BFX_iFsKind == BFX_FS_FAT)
    {   /* FAT: the destination begins with the source, letter case aside,   */
        /* and a '/' follows.                                                */
        if (BFT_PathFoldPrefix(pszToCanon, pszFromCanon, &cbMatched) && pszToCanon[cbMatched] == '/')
        {   /* Under it. */
            bInside = TRUE;
        }
    }
    else
    {   /* ext2/3/4: the same byte for byte. */
        cbFromCanon = strlen(pszFromCanon);

        if (strncmp(pszToCanon, pszFromCanon, cbFromCanon) == 0 && pszToCanon[cbFromCanon] == '/')
        {   /* Under it. */
            bInside = TRUE;
        }
    }

    if (bInside)
    {   /* Into itself. */

        /* Refuse it, the two copies freed with nothing changed. */
        free(pszFromCanon);
        free(pszToCanon);
        iStatus = BFX_Fail(BFE_ERR_ARG, "cannot move %s into itself (%s)", pszImageFrom, pszImageTo);
        BFX_Leave();
        return(iStatus);
    }

    /* The tests are done; the copies are not needed by the rename. */
    free(pszFromCanon);
    free(pszToCanon);

    /* Move the entry. */
    rc = BFS_Rename(pszImageFrom, pszImageTo);

    if (rc == 0)
    {   /* Moved: the entry's inode-change time becomes the present, as on   */
        /* Linux (lwext4 sets no time; the name and its parent are inode     */
        /* facts that changed).  A failure here leaves the entry moved and   */
        /* is still reported, naming the move.                               */
        rc = BFS_SetCtime(pszImageTo, (uint32_t)time(NULL));
    }

    if (rc == 0)
    {   /* The directory the name left changed... */
        rc = BFX_TouchParent(pszImageFrom);
    }

    if (rc == 0)
    {   /* ...and so did the one it went to (the same one for a plain      */
        /* rename, touched twice to the same moment, which is harmless).   */
        rc = BFX_TouchParent(pszImageTo);
    }

    /* Map the result, record a detail line on failure, release the gate. */
    return(BFX_Finish(rc, "rename", pszImageFrom));
}

/*----------------------------------------------------------------------------*/
/* BFE_SetMode:                                                               */
/*                                                                            */
/* Set an entry's permission bits.  Only the low 12 bits of the argument are  */
/* used; lwext4 keeps the inode's type bits, so this can never turn a file    */
/* into something else.  The inode-change time is set to the present.         */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImagePath : the entry to change.                                    */
/*     uiMode       : the new permission bits (e.g. 0644).                    */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_ARG; BFE_ERR_NOT_OPEN; BFE_ERR_READ_ONLY;        */
/*           BFE_ERR_NOT_FOUND; BFE_ERR_IO_IMAGE; BFE_ERR_UNSUPPORTED on a    */
/*           FAT session, which keeps no modes (the gate, BFX_Enter).         */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_SetMode(const char *pszImagePath, uint32_t uiMode)
{
    int iStatus;
    int rc;

    if (!BFX_PathValid(pszImagePath))
    {   /* A path BFX_PathValid refuses: missing, relative, not canonical, or a FAT name FAT cannot hold. */
        return(BFE_ERR_ARG);
    }

    /* The gate: the busy flag, an open image, and a writable session. */
    iStatus = BFX_Enter(TRUE, TRUE);

    if (iStatus != BFE_OK)
    {   /* Busy, nothing open, or read-only. */
        return(iStatus);
    }

    /* Only the 12 permission bits are passed on; lwext4 keeps the type   */
    /* bits itself.                                                       */
    rc = BFS_SetMode(pszImagePath, uiMode & BFX_PERM_MASK);

    if (rc == 0)
    {   /* Changed: the inode-change time becomes the present, as on Linux   */
        /* (lwext4's chmod sets no time).                                    */
        rc = BFS_SetCtime(pszImagePath, (uint32_t)time(NULL));
    }

    /* Map the result, record a detail line on failure, release the gate. */
    return(BFX_Finish(rc, "change the mode of", pszImagePath));
}

/*----------------------------------------------------------------------------*/
/* BFE_SetOwner:                                                              */
/*                                                                            */
/* Set an entry's owner and group.  Both are numeric: an image holds ids,     */
/* and the names they map to live in files inside it that this engine does    */
/* not interpret.  The inode-change time is set to the present.               */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImagePath : the entry to change.                                    */
/*     uiUid        : the new owner id.                                       */
/*     uiGid        : the new group id.                                       */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_ARG; BFE_ERR_NOT_OPEN; BFE_ERR_READ_ONLY;        */
/*           BFE_ERR_NOT_FOUND; BFE_ERR_IO_IMAGE; BFE_ERR_UNSUPPORTED on a    */
/*           FAT session, which keeps no owners (the gate, BFX_Enter).        */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_SetOwner(const char *pszImagePath, uint32_t uiUid, uint32_t uiGid)
{
    int iStatus;
    int rc;

    if (!BFX_PathValid(pszImagePath))
    {   /* A path BFX_PathValid refuses: missing, relative, not canonical, or a FAT name FAT cannot hold. */
        return(BFE_ERR_ARG);
    }

    /* The gate: the busy flag, an open image, and a writable session. */
    iStatus = BFX_Enter(TRUE, TRUE);

    if (iStatus != BFE_OK)
    {   /* Busy, nothing open, or read-only. */
        return(iStatus);
    }

    /* Set both ids in one call (lwext4's ext4_chown, through BFS_SetOwner). */
    rc = BFS_SetOwner(pszImagePath, uiUid, uiGid);

    if (rc == 0)
    {   /* Changed: the inode-change time becomes the present, as on Linux   */
        /* (lwext4's chown sets no time).                                    */
        rc = BFS_SetCtime(pszImagePath, (uint32_t)time(NULL));
    }

    /* Map the result, record a detail line on failure, release the gate. */
    return(BFX_Finish(rc, "change the owner of", pszImagePath));
}

/*----------------------------------------------------------------------------*/
/* BFE_SetTimes:                                                              */
/*                                                                            */
/* Set an entry's access and modify times.  The inode-change time is not      */
/* given as an argument, as on Linux, and as on Linux it becomes the present: */
/* the times are inode facts, and changing them changes the inode.            */
/*                                                                            */
/* A failure part-way leaves the times before it already written.  On a FAT   */
/* session only the modify time is kept.                                      */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImagePath : the entry to change.                                    */
/*     uiAtime      : the new access time, UTC epoch seconds.                 */
/*     uiMtime      : the new modify time, UTC epoch seconds.                 */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_ARG; BFE_ERR_NOT_OPEN; BFE_ERR_READ_ONLY;        */
/*           BFE_ERR_NOT_FOUND; BFE_ERR_IO_IMAGE.                             */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_SetTimes(const char *pszImagePath, uint32_t uiAtime, uint32_t uiMtime)
{
    int iStatus;
    int rc;

    if (!BFX_PathValid(pszImagePath))
    {   /* A path BFX_PathValid refuses: missing, relative, not canonical, or a FAT name FAT cannot hold. */
        return(BFE_ERR_ARG);
    }

    /* The gate: the busy flag, an open image of either kind (on FAT only   */
    /* the modify time is kept), and a writable session.                    */
    iStatus = BFX_EnterAnyFs(TRUE, TRUE);

    if (iStatus != BFE_OK)
    {   /* Busy, nothing open, or read-only. */
        return(iStatus);
    }

    /* Three separate calls, access, modify, then change, each only when the    */
    /* one before it succeeded -- so a later failure leaves the earlier times   */
    /* written.  On a FAT session only the modify call writes anything --       */
    /* FAT's one time, in local time -- and the access and change calls         */
    /* succeed doing nothing (bfe_fs.c).  The access time first.                */
    rc = BFS_SetAtime(pszImagePath, uiAtime);

    if (rc == 0)
    {   /* The access time went in; set the modify time as well. */
        rc = BFS_SetMtime(pszImagePath, uiMtime);
    }

    if (rc == 0)
    {   /* Both went in: the inode-change time becomes the present. */
        rc = BFS_SetCtime(pszImagePath, (uint32_t)time(NULL));
    }

    /* Map the result, record a detail line on failure, release the gate. */
    return(BFX_Finish(rc, "change the times of", pszImagePath));
}

/*----------------------------------------------------------------------------*/
/* BFE_HardLink:                                                              */
/*                                                                            */
/* Give an existing file a second name: both names then refer to one inode,   */
/* and the data is freed only when the last is removed.  The file's           */
/* inode-change time is set to the present, its link count having changed.    */
/*                                                                            */
/* A DIRECTORY cannot be hard-linked; lwext4 refuses it with EINVAL, and      */
/* BFX_MapErrno has no case for EINVAL, so that refusal reaches the caller as */
/* BFE_ERR_IO_IMAGE -- "image I/O failure" -- with "Invalid argument" in the  */
/* detail line.  THE DEFECT EXISTS AND IS INTENTIONALLY DEFERRED: the         */
/* program makes no hard links, so only an engine caller meets it.            */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImageExisting : the file that exists.                               */
/*     pszImageNewName  : the additional name to create.                      */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_ARG; BFE_ERR_NOT_OPEN; BFE_ERR_READ_ONLY;        */
/*           BFE_ERR_NOT_FOUND; BFE_ERR_EXISTS; BFE_ERR_IO_IMAGE;             */
/*           BFE_ERR_UNSUPPORTED on a FAT session (the gate, BFX_Enter).      */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_HardLink(const char *pszImageExisting, const char *pszImageNewName)
{
    int iStatus;
    int rc;

    if (!BFX_PathValid(pszImageExisting) || !BFX_PathValid(pszImageNewName))
    {   /* A path BFX_PathValid refuses: missing, relative, not canonical, or a FAT name FAT cannot hold. */
        return(BFE_ERR_ARG);
    }

    /* The gate: the busy flag, an open image, and a writable session. */
    iStatus = BFX_Enter(TRUE, TRUE);

    if (iStatus != BFE_OK)
    {   /* Busy, nothing open, or read-only. */
        return(iStatus);
    }

    /* Add the second name. */
    rc = BFS_HardLink(pszImageExisting, pszImageNewName);

    if (rc == 0)
    {   /* Linked: the file's link count changed, so its inode-change time   */
        /* becomes the present, as on Linux (lwext4 sets no time).  A        */
        /* failure here leaves the new name in place, reported failed.       */
        rc = BFS_SetCtime(pszImageNewName, (uint32_t)time(NULL));
    }

    if (rc == 0)
    {   /* And the directory that gained the name. */
        rc = BFX_TouchParent(pszImageNewName);
    }

    /* Map the result, record a detail line on failure, release the gate. */
    return(BFX_Finish(rc, "hard-link", pszImageNewName));
}

/*----------------------------------------------------------------------------*/
/* BFE_SymLink:                                                               */
/*                                                                            */
/* Create a symbolic link.  The target is stored as the link's text exactly   */
/* as given -- it may be relative, and it need not exist -- so it is NOT      */
/* validated as an image path the way the link's own path is.  It must be     */
/* shorter than one block of the filesystem.                                  */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszTarget    : the text the link holds (where it points).              */
/*     pszImageLink : the link to create.                                     */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_ARG (also: a target of a block or more);         */
/*           BFE_ERR_NOT_OPEN; BFE_ERR_READ_ONLY;                             */
/*           BFE_ERR_EXISTS; BFE_ERR_NOT_FOUND when the link's parent is      */
/*           missing, BFE_ERR_NOT_DIR when it is not a directory (nothing     */
/*           made either way); BFE_ERR_IO_IMAGE; BFE_ERR_UNSUPPORTED on a     */
/*           FAT session, which has no links (the gate, BFX_Enter).           */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_SymLink(const char *pszTarget, const char *pszImageLink)
{
    int iStatus;
    int rc;

    if (pszTarget == NULL || pszTarget[0] == '\0' || !BFX_PathValid(pszImageLink))
    {   /* No target text, or a relative or missing link path. */
        return(BFE_ERR_ARG);
    }

    /* The gate: the busy flag, an open image, and a writable session. */
    iStatus = BFX_Enter(TRUE, TRUE);

    if (iStatus != BFE_OK)
    {   /* Busy, nothing open, or read-only. */
        return(iStatus);
    }

    /* The folder it is to go in must be there, and be a folder: lwext4's    */
    /* create path would otherwise make every missing level above the link   */
    /* (BFX_CheckParent says why).  Tested under the busy flag.              */
    rc = BFX_CheckParent(pszImageLink);

    if (rc == ENOENT || rc == ENOTDIR)
    {   /* No folder to put it in: refused as the contract says, NOT_FOUND   */
        /* or NOT_DIR, with nothing made.                                    */
        iStatus = BFX_Fail(BFX_MapErrno(rc), "cannot create the link %s: the folder it is to go in %s",
                           pszImageLink, (rc == ENOENT) ? "does not exist" : "is not a directory");
        BFX_Leave();
        return(iStatus);
    }

    if (rc != 0)
    {   /* The parent could not be looked at: that failure, mapped. */
        return(BFX_Finish(rc, "create the link", pszImageLink));
    }

    /* Create it.  The bundled lwext4 (BifrostFM patch 12 in ext4.c) tests   */
    /* both of its own refusals BEFORE it creates anything: a name already   */
    /* taken, of any type, is EEXIST, and a target of a block or more is     */
    /* EINVAL -- so neither leaves a half-made link behind.  The call is     */
    /* lwext4's ext4_fsymlink, through BFS_SymLink, whose result is          */
    /* lwext4's own.                                                         */
    rc = BFS_SymLink(pszTarget, pszImageLink);

    if (rc == EINVAL)
    {   /* The one EINVAL ext4_fsymlink gives: the text does not fit in      */
        /* one block with room to end.  Under 60 bytes a link's text lives   */
        /* in the inode; longer, in one data block of its own -- and a       */
        /* whole block e2fsck would call an invalid link.  That is the       */
        /* caller's argument, not an I/O failure, so it is reported as ARG   */
        /* rather than through the shared mapping.                           */

        /* Refuse it, giving the target's length. */
        iStatus = BFX_Fail(BFE_ERR_ARG, "cannot create the link %s: a target of %u bytes does not fit in one block",
                           pszImageLink, (unsigned)strlen(pszTarget));
        BFX_Leave();
        return(iStatus);
    }

    if (rc == 0)
    {   /* Created.  lwext4 leaves a new link with mode 0666 and zero times;   */
        /* a symbolic link is 0777 on Linux, and it was made now.  The owner   */
        /* and group are always 0 (root), whoever owns the directory it is     */
        /* in; why root rather than the parent's owner is not recorded.        */
        rc = BFX_NewLinkFinish(pszImageLink, 0, 0, (uint32_t)time(NULL));
    }

    /* Map the result, record a detail line on failure, release the gate. */
    return(BFX_Finish(rc, "create the link", pszImageLink));
}
