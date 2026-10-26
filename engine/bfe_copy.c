/*----------------------------------------------------------------------------*/
/* bfe_copy.c                                                                 */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The BifrostFM engine's copy functions: BFE_Extract (image -> Windows),     */
/* BFE_Import (Windows -> image) and BFE_CopyWithin (image -> the same        */
/* image), all recursive, all reporting through one event callback.           */
/*                                                                            */
/* The boundary rules and the manifest below concern the first two.  A copy   */
/* inside the image crosses no boundary: it carries every entry's metadata    */
/* across directly and copies a symbolic link as a link.                      */
/*                                                                            */
/* THE BOUNDARY RULES.  NTFS cannot hold a Unix mode, owner, group or         */
/* symbolic link, and Windows refuses names ext4 allows.  So:                 */
/*                                                                            */
/*   - File bytes cross exactly, both ways; nothing is ever translated.       */
/*   - On EXTRACT, a symlink, device, fifo or socket is SKIPPED, and so is an */
/*     entry whose name Windows cannot hold.  With the manifest option every  */
/*     entry -- skipped ones included -- has its Unix facts written as text   */
/*     beside the data, so nothing about the tree is lost.  With the          */
/*     follow-links option a symlink is copied as what it names instead,      */
/*     resolved inside the open filesystem (Bfc_ResolveLink), and recorded    */
/*     as that file or folder; a link leading nowhere in it, or back into a   */
/*     folder being copied (Bfc_InsideWalk), is skipped as before.            */
/*   - On IMPORT, an item gets its mode, owner and times from the manifest    */
/*     when one is present and wanted, and otherwise the caller's defaults    */
/*     (0644 files, 0755 directories, root, the host file's modify time);     */
/*     the symlinks a manifest recorded are created again.                    */
/*                                                                            */
/* THE MANIFEST is plain "key: value" lines in blocks separated by a blank    */
/* line.  A tree's manifest is <hostdir>\_ext4meta.txt with paths relative    */
/* to the tree (the tree's own directory is "."), and its FIRST LINE is a     */
/* comment naming it -- the mark an import requires before it treats a file   */
/* of that name as a manifest rather than as data.  A single file's is        */
/* <hostfile>.ext4meta holding one block.  Keys: path, type, then either      */
/* target, uid, gid and mtime (a symlink) or mode (octal), uid, gid, mtime,   */
/* atime, ctime (epoch seconds) and size.  A name containing a line break     */
/* cannot be recorded in                                                      */
/* this format and is the one thing a manifest round trip does not preserve.  */
/* An import reads the size back only to tell whether a file has changed      */
/* since it was extracted (its recorded times are then not applied), and      */
/* never reads ctime: an imported item's inode-change time is the moment of   */
/* the import, which no recorded time can stand for.                          */
/*                                                                            */
/* A manifest is written under a temporary name and renamed over its final    */
/* one only once it is complete, so a manifest already there is never         */
/* replaced by part of one.                                                   */
/*----------------------------------------------------------------------------*/

/* The C library and Windows for the host side (the wide stat, utime and    */
/* mkdir, and the folder walk; errno's values, which the image calls        */
/* return); then the engine's shared internals and its filesystem-neutral   */
/* file calls (bfe_fs.h), through which every image file and directory      */
/* is reached -- this file includes no filesystem library's header, so      */
/* the same walks can serve an ext2/3/4 session and a FAT one.              */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <errno.h>
#include <time.h>
#include <wchar.h>
#include <sys/types.h>
#include <sys/stat.h>
#include <sys/utime.h>
#include <direct.h>
#include <windows.h>

#include "bfe_internal.h"
#include "bfe_fs.h"

/* The FAT session's one fact an import needs before it copies: the largest   */
/* file the volume holds (BFT_MaxFileBytes).                                  */
#include "bfe_fat.h"

/*----------------------------------------------------------------------------*/
/* Definitions -- documentation table.                                        */
/*----------------------------------------------------------------------------*/
#define BFC_MANIFEST_NAME   L"_ext4meta.txt"   // a tree's manifest, at the top of the host folder
#define BFC_MANIFEST_NAME8  "_ext4meta.txt"    // the same name as UTF-8, to compare with the image's names
#define BFC_SIDECAR_SUFFIX  L".ext4meta"       // appended to a single file's host path for its one-block sidecar
#define BFC_ROOT_RELATIVE   "."                // the manifest path of the copied tree's own top directory

#define BFC_MANIFEST_MARK   "# BifrostFM metadata manifest -- one block per entry"       // a tree manifest's first line, as BFE_Extract writes it
#define BFC_ALT_MARK        "# ext4edit rdump metadata manifest -- one block per entry"  // the same format's one other accepted first line, the one another writer of the format puts first: accepted as a manifest too (never written here)
#define BFC_TEMP_TRIES      100                                                          // most temporary names tried for a manifest before giving up (each one taken already)

#define BFC_HOST_NONE       0                  // BFC_HostKind: nothing exists at the host path
#define BFC_HOST_FILE       1                  // BFC_HostKind: a regular file
#define BFC_HOST_DIR        2                  // BFC_HostKind: a directory
#define BFC_HOST_OTHER      3                  // BFC_HostKind: something else (a device)
#define BFC_LINK_TARGET_MAX 65536              // a symlink's target is shorter than one block, and no ext4 block is larger: an inode claiming more is damaged, and its target is not read (no allocation of a damaged size)
#define BFC_STOP_PATH_BYTES 512                // BFC_Run.szStopPath: room for the image path a copy stopped at when memory ran out, cut beyond it -- a fixed buffer, as nothing may be allocated then

/*----------------------------------------------------------------------------*/
/* BFM_Record -- one parsed manifest block.  The bHas* flags say which keys   */
/* the block actually carried, so an absent key falls back to the import      */
/* default instead of being read as zero.                                     */
/*----------------------------------------------------------------------------*/
struct BFM_Record
{
    char     *pszPath;      // the block's path: value (malloc'd); NULL only while a block is being read
    char     *pszTarget;    // a symlink's target: value (malloc'd), or NULL
    uint32_t  uiType;       // BFE_TYPE_* parsed from the type: value
    uint32_t  uiMode;       // permission bits, when bHasMode
    uint32_t  uiUid;        // owner id, when bHasUid
    uint32_t  uiGid;        // group id, when bHasGid
    uint32_t  uiMtime;      // modify time, epoch seconds, when bHasMtime
    uint32_t  uiAtime;      // access time, epoch seconds, when bHasAtime
    uint64_t  ullSize;      // the entry's size in bytes when it was extracted, when bHasSize
    bool      bHasMode;     // true when the block had a mode: line
    bool      bHasUid;      // true when the block had a uid: line
    bool      bHasGid;      // true when the block had a gid: line
    bool      bHasMtime;    // true when the block had an mtime: line
    bool      bHasAtime;    // true when the block had an atime: line
    bool      bHasSize;     // true when the block had a size: line
};

/*----------------------------------------------------------------------------*/
/* Bfc_strctWalkDir -- one folder an extract is inside, as a link chain on    */
/* the C stack: each directory level of BFC_ExtractNode keeps one in its own  */
/* frame while it walks its children, linked to the level above.  Following   */
/* links needs it: a link whose folder target is one of these, or holds one,  */
/* would lead the walk back into itself without end (Bfc_InsideWalk).         */
/*----------------------------------------------------------------------------*/
struct Bfc_strctWalkDir
{
    const char              *pszImage;    // the folder's image path as read -- a link already resolved, so no level's path holds a link
    struct Bfc_strctWalkDir *pParent;     // the level above, or NULL at the copy's top
};

/*----------------------------------------------------------------------------*/
/* BFC_Run -- everything one BFE_Extract or BFE_Import carries down its       */
/* recursion: who to tell, what options apply, and the running outcome.       */
/*----------------------------------------------------------------------------*/
struct BFC_Run
{
    struct Bfc_strctWalkDir *pWalkDirs;                   // extract: the innermost folder being walked (its chain the folders above it), or NULL outside every folder -- the loop test of a followed link
    BFE_CopyCallback    pfnEvent;                         // the caller's event callback, or NULL for none
    void               *pvContext;                        // passed to pfnEvent unchanged
    FILE               *pManifest;                        // extract: the open manifest being written (under its temporary name), or NULL
    wchar_t            *pwszManifest;                     // extract: the manifest's final host path (malloc'd) while pManifest is open, else NULL
    wchar_t            *pwszManifestTmp;                  // extract: the temporary host path pManifest is written to (malloc'd), else NULL
    struct BFM_Record  *pRecords;                         // import: the loaded manifest, sorted by path, or NULL
    int                 nRecords;                         // import: how many records pRecords holds
    BFE_ExtractOptions  optExtract;                       // extract options (zeros when the caller passed NULL)
    BFE_ImportOptions   optImport;                        // import options (the usual defaults when the caller passed NULL)
    bool                bManifestEmpty;                   // extract: true until the first block is written (no blank line before it)
    bool                bLeaveOutClash;                   // extract: true when a MANIFEST_CLASH was answered KEEP_MANIFEST -- the top entries named as the manifest are left out
    bool                bTreeManifest;                    // import: true when the tree's _ext4meta.txt was recognised as its manifest -- the walk then does not import it
    bool                bCancelled;                       // true once the run must stop -- a callback replied BFE_REPLY_CANCEL, or memory ran out (iStopStatus); every loop and level then stops and returns
    int                 iStopStatus;                      // BFE_OK, or BFE_ERR_NO_MEMORY once an item ran out of memory: the whole run stopped at that item, and BFC_RunResult returns it
    char                szStopPath[BFC_STOP_PATH_BYTES];  // the image path of the item that ran out of memory, for the detail line; "" until then
    int                 nErrors;                          // how many BFE_EVENT_ERROR events were raised
    int                 nDone;                            // how many items completed (BFE_EVENT_DONE)
};

/*----------------------------------------------------------------------------*/
/* The type names a manifest uses, indexed by BFE_TYPE_* -- so the order must */
/* follow the BFE_TYPE_* values.  The names are part of the manifest's file   */
/* format, shared with other tools that write the same format: a manifest     */
/* written earlier must still read, so they are never renamed.                */
/*----------------------------------------------------------------------------*/
static const char *BFM_apszTypeNames[] = {
    "?",            // BFE_TYPE_UNKNOWN
    "file",         // BFE_TYPE_FILE
    "dir",          // BFE_TYPE_DIR
    "chardev",      // BFE_TYPE_CHARDEV
    "blockdev",     // BFE_TYPE_BLOCKDEV
    "fifo",         // BFE_TYPE_FIFO
    "socket",       // BFE_TYPE_SOCKET
    "symlink"       // BFE_TYPE_SYMLINK
};

/*----------------------------------------------------------------------------*/
/* BFC_Emit:                                                                  */
/*                                                                            */
/* Raise one copy event and take the caller's reply.  This is the single      */
/* place the run's outcome is counted, so the walkers only say what happened  */
/* and never keep score themselves -- and the one place an item that ran out  */
/* of memory (an ERROR with BFE_ERR_NO_MEMORY, from any walker) stops the     */
/* whole run, extract, import and copy within alike, at that item.            */
/*                                                                            */
/* Arguments:                                                                 */
/*     pRun       : the running copy.                                         */
/*     uiKind     : BFE_EVENT_*.                                              */
/*     uiType     : BFE_TYPE_* of the item.                                   */
/*     iStatus    : BFE_ERR_* for ERROR / SKIPPED, otherwise BFE_OK.          */
/*     pszImage   : the item's image path.                                    */
/*     pwszHost   : the item's host path, or NULL when it has none.           */
/*     ullDone    : bytes of the current file copied so far.                  */
/*     ullTotal   : the current file's size.                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     int : the callback's BFE_REPLY_* value; BFE_REPLY_CONTINUE when there  */
/*           is no callback.                                                  */
/*----------------------------------------------------------------------------*/
static int BFC_Emit(struct BFC_Run *pRun, uint32_t uiKind, uint32_t uiType, int iStatus,
                    const char *pszImage, const wchar_t *pwszHost,
                    uint64_t ullDone, uint64_t ullTotal)
{
    BFE_CopyEvent event;
    int           iReply;

    /* The score is kept FIRST, before the test for a callback, because     */
    /* the run's result (BFC_RunResult) is computed from these counts and   */
    /* must be right whether or not anyone is listening.                    */
    if (uiKind == BFE_EVENT_ERROR)
    {   /* A failed item makes the whole run "partial". */
        pRun->nErrors++;
    }

    if (uiKind == BFE_EVENT_ERROR && iStatus == BFE_ERR_NO_MEMORY && pRun->iStopStatus == BFE_OK)
    {   /* The first item to run out of memory: the whole run stops here.      */
        /* Going on would only meet the shortage again, item after item,       */
        /* and end "partial" with an unknown part missing; stopping gives      */
        /* one clear failure the caller can report.  The stop latches          */
        /* bCancelled, the flag every walker's loop and level already tests,   */
        /* so extract, import and copy within all unwind as for a cancel (a    */
        /* manifest being written is thrown away, BFM_Publish) -- and          */
        /* iStopStatus makes BFC_RunResult report                              */
        /* it as the failure it is, not a cancel.  The item's path is kept     */
        /* for the detail line in the run's own fixed buffer, cut if it is     */
        /* longer: nothing is allocated now.  The item's own ERROR still       */
        /* goes to the callback below, so the caller lists it.                 */
        pRun->iStopStatus = BFE_ERR_NO_MEMORY;
        pRun->bCancelled  = true;

        if (pszImage != NULL)
        {   /* The item has an image path to name (every walker passes one). */
            snprintf(pRun->szStopPath, sizeof(pRun->szStopPath), "%s", pszImage);
        }
    }

    if (uiKind == BFE_EVENT_DONE)
    {   /* A completed item. */
        pRun->nDone++;
    }

    if (pRun->pfnEvent == NULL)
    {   /* Nobody is listening; the copy just carries on. */
        return(BFE_REPLY_CONTINUE);
    }

    /* The event, every field set (the reserved one to 0, as the interface    */
    /* promises).  The two path pointers are the walker's own strings,        */
    /* valid only for the call -- the interface tells callers to copy them.   */
    /* Each field is set BY NAME rather than the struct being zeroed, so a    */
    /* field added to BFE_CopyEvent must be added here too, or it reaches     */
    /* the program as stack garbage (bfe_image.c's BFI_Report zeroes it).     */
    event.ullBytesDone  = ullDone;
    event.ullBytesTotal = ullTotal;
    event.pszImagePath  = pszImage;
    event.pwszHostPath  = pwszHost;
    event.uiKind        = uiKind;
    event.uiType        = uiType;
    event.iStatus       = iStatus;
    event.uiReserved    = 0;

    /* Hand it over and take the reply. */
    iReply = pRun->pfnEvent(&event, pRun->pvContext);

    if (iReply == BFE_REPLY_CANCEL)
    {   /* Latch it: every loop and every level checks this flag and returns. */
        pRun->bCancelled = true;
    }

    /* The caller acts on the reply only for a CONFLICT. */
    return(iReply);
}

/*----------------------------------------------------------------------------*/
/* BFC_HostKind:                                                              */
/*                                                                            */
/* Say what, if anything, exists at a host path, and hand back its size and   */
/* modify time.  stat follows a Windows symbolic link, which is what makes an */
/* import follow one.                                                         */
/*                                                                            */
/* Arguments:                                                                 */
/*     pwszHost  : the host path to examine.                                  */
/*     pullSize  : receives the size in bytes (may be NULL).                  */
/*     puiMtime  : receives the modify time, epoch seconds (may be NULL).     */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFC_HOST_NONE, BFC_HOST_FILE, BFC_HOST_DIR or BFC_HOST_OTHER.    */
/*----------------------------------------------------------------------------*/
static int BFC_HostKind(const wchar_t *pwszHost, uint64_t *pullSize, uint32_t *puiMtime)
{
    struct __stat64 st;

    if (_wstat64(pwszHost, &st) != 0)
    {   /* Nothing there (or it cannot be examined, which is treated alike). */

        /* "Cannot be examined" -- no permission, or a Windows symbolic link   */
        /* whose target is gone -- therefore reads as "nothing there": an      */
        /* import reports such a source as missing, and an extract treats      */
        /* such a destination as free and finds out at its open.  The out      */
        /* values are left untouched; no caller reads them for NONE.           */
        return(BFC_HOST_NONE);
    }

    if (pullSize != NULL)
    {   /* The caller wants the size. */
        *pullSize = (uint64_t)st.st_size;
    }

    if (puiMtime != NULL)
    {   /* The caller wants the modify time; ext4's field is 32-bit seconds,   */
        /* so a time before 1970 or after 2106 wraps here.                     */
        *puiMtime = (uint32_t)st.st_mtime;
    }

    if ((st.st_mode & _S_IFMT) == _S_IFDIR)
    {   /* A directory. */
        return(BFC_HOST_DIR);
    }

    if ((st.st_mode & _S_IFMT) == _S_IFREG)
    {   /* A regular file. */
        return(BFC_HOST_FILE);
    }

    /* A device or anything else stat can name -- which no copy reads or   */
    /* writes; the callers refuse it.                                      */
    return(BFC_HOST_OTHER);
}

/*----------------------------------------------------------------------------*/
/* BFM_WriteBlock:                                                            */
/*                                                                            */
/* Append one entry's block to the manifest being written.  A symlink records */
/* where it pointed (it is never created on NTFS), its owner and its modify   */
/* time -- not its permission bits, which mean nothing for a link -- and      */
/* everything else records its full Unix facts.  A link whose target cannot   */
/* be read gets no target line and raises an ERROR for the run instead.       */
/*                                                                            */
/* Arguments:                                                                 */
/*     pRun     : the running extract (holds the open manifest).              */
/*     pszRel   : the path to record for the entry.                           */
/*     pEntry   : the entry's facts.                                          */
/*     pszImage : the entry's image path (to read a symlink's target).        */
/*                                                                            */
/* Returns:                                                                   */
/*     void : one block is appended; nothing happens when no manifest is      */
/*            being written.                                                  */
/*----------------------------------------------------------------------------*/
static void BFM_WriteBlock(struct BFC_Run *pRun, const char *pszRel, const BFE_Entry *pEntry,
                           const char *pszImage)
{
    char   *pszTarget;
    size_t  cbRead;
    int     rc;

    if (pRun->pManifest == NULL)
    {   /* The caller did not ask for a manifest. */
        return;
    }

    if (!pRun->bManifestEmpty)
    {   /* Not the first block: separate it from the one before.  The blank   */
        /* line is written BEFORE every block but the first, so the file      */
        /* never ends in a stray separator.                                   */
        fputc('\n', pRun->pManifest);
    }

    /* Every later block now needs its separator. */
    pRun->bManifestEmpty = false;

    /* Every block names the entry and its type first.  The type indexes     */
    /* the name table directly: the directory reader only ever reports the   */
    /* eight BFE_TYPE_* values the table holds.  The writes here are not     */
    /* tested one by one: a write that fails (a full disk) sets the          */
    /* stream's error flag, which stays set, and BFM_Publish tests it once   */
    /* before the manifest is put in place -- so a manifest cut short is     */
    /* thrown away rather than published.                                    */
    fprintf(pRun->pManifest, "path: %s\n", pszRel);
    fprintf(pRun->pManifest, "type: %s\n", BFM_apszTypeNames[pEntry->uiType]);

    if (pEntry->uiType == BFE_TYPE_SYMLINK)
    {   /* A symlink: its target text is the whole of what it was.  The      */
        /* inode's size is the target's length, so the buffer is exact --    */
        /* but only a size a symlink can have (BFC_LINK_TARGET_MAX) is       */
        /* trusted: a damaged inode's huge size is never allocated, and is   */
        /* taken as an unreadable target (EIO).                              */
        pszTarget = NULL;
        rc        = EIO;
        cbRead    = 0;

        if (pEntry->ullSize < BFC_LINK_TARGET_MAX)
        {   /* A size a target can have: the buffer, then the read. */
            pszTarget = (char *)malloc((size_t)pEntry->ullSize + 1);

            if (pszTarget == NULL)
            {   /* Out of memory: the target cannot be read. */
                rc = ENOMEM;
            }
            else
            {   /* The target's bytes, at most the inode's size; the read     */
                /* (lwext4's through BFS_ReadLink) does not terminate them,   */
                /* so the count it hands back places the NUL.                 */
                rc = BFS_ReadLink(pszImage, pszTarget, (size_t)pEntry->ullSize, &cbRead);
            }
        }

        if (rc == 0)
        {   /* Terminate what was read and record it. */
            pszTarget[cbRead] = '\0';
            fprintf(pRun->pManifest, "target: %s\n", pszTarget);
        }
        else
        {   /* The target could not be read.  The block gets NO target line    */
            /* -- the format has no "unknown", and any text there would be     */
            /* restored as the link's real target -- only a '#' line, which    */
            /* every reader skips, saying why; an import then leaves this      */
            /* link out, as it does any link record with no target.  And the   */
            /* run is told, as an ERROR: IO_IMAGE ends it partial, the         */
            /* metadata it promised not all there; NO_MEMORY stops it at       */
            /* this entry (BFC_Emit).                                          */
            fprintf(pRun->pManifest, "# target unreadable: %s\n", strerror(rc));

            if (rc == ENOMEM)
            {   /* Out of memory. */
                BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_SYMLINK, BFE_ERR_NO_MEMORY, pszImage, NULL, 0, 0);
            }
            else
            {   /* The read failed, or the inode is damaged. */
                BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_SYMLINK, BFE_ERR_IO_IMAGE, pszImage, NULL, 0, 0);
            }
        }

        /* The text is in the manifest; the buffer (if any) is done with. */
        free(pszTarget);

        /* A link's owner and times are real facts too (its mode is not: it   */
        /* is always 0777), so they are recorded and restored with it.        */
        fprintf(pRun->pManifest, "uid: %lu\n", (unsigned long)pEntry->uiUid);
        fprintf(pRun->pManifest, "gid: %lu\n", (unsigned long)pEntry->uiGid);
        fprintf(pRun->pManifest, "mtime: %lu\n", (unsigned long)pEntry->uiMtime);
        return;
    }

    /* A file, directory or special node: the facts NTFS has no place for.        */
    /* The mode is the 12 permission bits only, as octal (the type is its         */
    /* own line).  ctime is written for the record only: an import never          */
    /* applies it (an imported inode changed when it was imported).  The          */
    /* size lets an import tell whether the file changed since (BFC_ApplyMeta).   */
    fprintf(pRun->pManifest, "mode: %04lo\n", (unsigned long)pEntry->uiMode);
    fprintf(pRun->pManifest, "uid: %lu\n", (unsigned long)pEntry->uiUid);
    fprintf(pRun->pManifest, "gid: %lu\n", (unsigned long)pEntry->uiGid);
    fprintf(pRun->pManifest, "mtime: %lu\n", (unsigned long)pEntry->uiMtime);
    fprintf(pRun->pManifest, "atime: %lu\n", (unsigned long)pEntry->uiAtime);
    fprintf(pRun->pManifest, "ctime: %lu\n", (unsigned long)pEntry->uiCtime);
    fprintf(pRun->pManifest, "size: %llu\n", (unsigned long long)pEntry->ullSize);
}

/*----------------------------------------------------------------------------*/
/* BFC_NameIsManifest:                                                        */
/*                                                                            */
/* Whether an image name, extracted to the top of a host folder, would land   */
/* on the tree manifest's own file.  NTFS folds letter case, so               */
/* "_EXT4META.TXT" lands there as surely as "_ext4meta.txt" does; the fold is */
/* _stricmp's, which in the C locale the engine runs in folds the ASCII       */
/* letters only -- all the manifest's name has.                               */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszName : one entry name from the image, UTF-8.                        */
/*                                                                            */
/* Returns:                                                                   */
/*     bool : true when the name is the manifest's, letter case aside.        */
/*----------------------------------------------------------------------------*/
static bool BFC_NameIsManifest(const char *pszName)
{
    /* Equal but for ASCII letter case. */
    return(_stricmp(pszName, BFC_MANIFEST_NAME8) == 0);
}

/*----------------------------------------------------------------------------*/
/* BFM_TempPath:                                                              */
/*                                                                            */
/* Choose the temporary host path a manifest is written under before it is    */
/* renamed onto its final one: "<final>.<n>.tmp", n counting up from 0 past   */
/* every name already taken.  Taken means something exists at that host path  */
/* -- a file of the user's, or one a crashed run left -- or, for a tree's     */
/* manifest, an entry at the top of the image folder would land on it as the  */
/* walk extracts it.  The second test is what keeps the open temporary file   */
/* out of the walk's way: an entry extracted onto it would raise a CONFLICT   */
/* whose "overwrite" opened the same file a second time.                      */
/*                                                                            */
/* Arguments:                                                                 */
/*     pwszFinal    : the manifest's final host path.                         */
/*     ppszTopNames : the names at the top of the image folder being          */
/*                    extracted (a tree's manifest), or NULL (a sidecar,      */
/*                    which no extracted entry can meet).                     */
/*     nTopNames    : how many names ppszTopNames holds.                      */
/*                                                                            */
/* Returns:                                                                   */
/*     wchar_t * : the temporary path (malloc'd; the caller frees it), or     */
/*                 NULL when memory ran out or BFC_TEMP_TRIES names were all  */
/*                 taken.                                                     */
/*----------------------------------------------------------------------------*/
static wchar_t *BFM_TempPath(const wchar_t *pwszFinal, char **ppszTopNames, int nTopNames)
{
    wchar_t  awszSuffix[24];
    char     aszLeaf[48];
    wchar_t *pwszTemp;
    bool     bTaken;
    int      n;
    int      i;

    for (n = 0; n < BFC_TEMP_TRIES; n++)
    {   /* One candidate name per iteration. */

        /* The suffix, ".<n>.tmp", and the whole candidate path.  The buffer    */
        /* holds the longest suffix BFC_TEMP_TRIES allows with room to spare;   */
        /* _snwprintf does not terminate a string that fills it, so the last    */
        /* slot is set by hand.                                                 */
        _snwprintf(awszSuffix, sizeof(awszSuffix) / sizeof(awszSuffix[0]), L".%d.tmp", n);
        awszSuffix[sizeof(awszSuffix) / sizeof(awszSuffix[0]) - 1] = L'\0';
        pwszTemp = BFU_AppendHost(pwszFinal, awszSuffix);

        if (pwszTemp == NULL)
        {   /* Out of memory composing the path. */
            return(NULL);
        }

        /* Taken on the host: anything there at all. */
        bTaken = (BFC_HostKind(pwszTemp, NULL, NULL) != BFC_HOST_NONE);

        if (!bTaken && ppszTopNames != NULL)
        {   /* Free on the host; for a tree's manifest, also free of the       */
            /* image's top names -- compared as the manifest's name with the   */
            /* same suffix, letter case aside, as NTFS would place them.       */
            snprintf(aszLeaf, sizeof(aszLeaf), "%s.%d.tmp", BFC_MANIFEST_NAME8, n);

            for (i = 0; i < nTopNames; i++)
            {   /* One image name per iteration. */
                if (_stricmp(ppszTopNames[i], aszLeaf) == 0)
                {   /* An entry of the image would be extracted onto it. */
                    bTaken = true;
                    break;
                }
            }
        }

        if (!bTaken)
        {   /* Nothing has this name, on either side: use it. */
            return(pwszTemp);
        }

        /* Taken: let it go and try the next number. */
        free(pwszTemp);
    }

    /* Every candidate was taken (a folder full of them is the leavings of   */
    /* many crashed runs); the caller refuses to write a manifest.           */
    return(NULL);
}

/*----------------------------------------------------------------------------*/
/* BFM_OpenManifest:                                                          */
/*                                                                            */
/* Start writing a manifest: choose its temporary name (BFM_TempPath) and     */
/* create the file under it, leaving any manifest already at the final path   */
/* untouched until BFM_Publish renames the new one over it.  The run then     */
/* holds the open stream and both paths.                                      */
/*                                                                            */
/* Arguments:                                                                 */
/*     pRun         : the running extract; pManifest must be NULL.            */
/*     pwszFinal    : the manifest's final host path, malloc'd.  OWNERSHIP    */
/*                    PASSES HERE, success or failure: the run keeps it, or   */
/*                    it is freed.                                            */
/*     ppszTopNames : as BFM_TempPath: the image folder's top names, or NULL. */
/*     nTopNames    : how many names ppszTopNames holds.                      */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_NO_MEMORY; BFE_ERR_IO_HOST when no temporary     */
/*           name was free or the file could not be created.  Bare codes:     */
/*           nothing is recorded, and the caller reports the failure.         */
/*----------------------------------------------------------------------------*/
static int BFM_OpenManifest(struct BFC_Run *pRun, wchar_t *pwszFinal, char **ppszTopNames, int nTopNames)
{
    wchar_t *pwszTemp;

    /* The temporary name, free on the host and of the image's top names. */
    pwszTemp = BFM_TempPath(pwszFinal, ppszTopNames, nTopNames);

    if (pwszTemp == NULL)
    {   /* No name to write under: the final path is let go, and nothing   */
        /* was created.                                                    */
        free(pwszFinal);
        return(BFE_ERR_IO_HOST);
    }

    /* Create it -- a new file, since the name was free a moment ago.   */
    /* Binary, so every line ends in a bare LF as on Linux.             */
    pRun->pManifest = _wfopen(pwszTemp, L"wb");

    if (pRun->pManifest == NULL)
    {   /* The folder is not there or not writable: both paths let go. */
        free(pwszTemp);
        free(pwszFinal);
        return(BFE_ERR_IO_HOST);
    }

    /* The run keeps both paths until BFM_Publish. */
    pRun->pwszManifest    = pwszFinal;
    pRun->pwszManifestTmp = pwszTemp;

    /* Open, under its temporary name. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFM_Publish:                                                               */
/*                                                                            */
/* Finish the manifest the run is writing, if any: close it and, when every   */
/* write and the close succeeded, rename it over its final path in one step,  */
/* so the final name only ever holds a whole manifest -- the earlier one      */
/* until the rename, the new one after.  A manifest that could not be         */
/* finished or put in place is removed and reported as one ERROR, which       */
/* makes the run partial; the data already extracted stays.  A stopped run    */
/* (cancelled, or out of memory) removes its manifest without an event: the   */
/* stop is reported once, by the run's result, and an earlier manifest stays  */
/* as it was.                                                                 */
/*                                                                            */
/* Arguments:                                                                 */
/*     pRun     : the running extract.                                        */
/*     pszImage : the image path the extract started from, which the ERROR    */
/*                event names (the manifest itself has no image path).        */
/*                                                                            */
/* Returns:                                                                   */
/*     void : the run's manifest stream and paths are released either way;    */
/*            a failure is in the events raised and in pRun's counters.       */
/*----------------------------------------------------------------------------*/
static void BFM_Publish(struct BFC_Run *pRun, const char *pszImage)
{
    bool bWhole;

    if (pRun->pManifest == NULL)
    {   /* No manifest is being written. */
        return;
    }

    /* Whole only if no write failed (the error flag is tested BEFORE the   */
    /* close, which clears it with the stream) and the close's flush of     */
    /* the last buffer succeeded too.                                       */
    bWhole = (ferror(pRun->pManifest) == 0);

    if (fclose(pRun->pManifest) != 0)
    {   /* The final flush failed: the file on disk is short. */
        bWhole = false;
    }

    /* The stream is gone either way. */
    pRun->pManifest = NULL;

    if (pRun->bCancelled)
    {   /* Stopped -- cancelled, or out of memory, which BFC_Emit latches    */
        /* into the same flag: the new manifest would describe a part of     */
        /* the tree as if it were the whole, so it is thrown away; nothing   */
        /* is reported here (the run's result says why it stopped).          */
        _wremove(pRun->pwszManifestTmp);
    }
    else if (!bWhole)
    {   /* Cut short: thrown away, and the run is partial for it. */
        _wremove(pRun->pwszManifestTmp);
        BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_FILE, BFE_ERR_IO_HOST, pszImage, pRun->pwszManifest, 0, 0);
    }
    else if (!MoveFileExW(pRun->pwszManifestTmp, pRun->pwszManifest, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
    {   /* Whole, but it cannot take its final name (a folder or a           */
        /* read-only file holds it): removed rather than left under a name   */
        /* no import looks for, and the run is partial.  What holds the      */
        /* final name is left as it is.  MoveFileExW replaces an ordinary    */
        /* file in one step, which is what keeps the final name whole.       */
        /* MoveFileExW's answer is a Windows BOOL, tested in its own form,   */
        /* on purpose -- not as the engine's bool.                           */
        _wremove(pRun->pwszManifestTmp);
        BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_FILE, BFE_ERR_IO_HOST, pszImage, pRun->pwszManifest, 0, 0);
    }

    /* Both paths are done with (after the event, which names the final one). */
    free(pRun->pwszManifest);
    free(pRun->pwszManifestTmp);
    pRun->pwszManifest    = NULL;
    pRun->pwszManifestTmp = NULL;
}

/*----------------------------------------------------------------------------*/
/* BFM_Free:                                                                  */
/*                                                                            */
/* Free a manifest loaded by BFM_Load.                                        */
/*                                                                            */
/* Arguments:                                                                 */
/*     pRecords : the record array (may be NULL).                             */
/*     nRecords : how many records it holds.                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     void : every string and the array itself are freed.                    */
/*----------------------------------------------------------------------------*/
static void BFM_Free(struct BFM_Record *pRecords, int nRecords)
{
    int i;

    /* Only the first nRecords are touched: BFM_Load calls this on a list   */
    /* it is still building, whose slots past nUsed were never filled.      */
    for (i = 0; i < nRecords; i++)
    {   /* One record per iteration: its two strings (free of NULL is allowed). */
        free(pRecords[i].pszPath);
        free(pRecords[i].pszTarget);
    }

    /* Then the array itself. */
    free(pRecords);
}

/*----------------------------------------------------------------------------*/
/* BFM_ComparePaths:                                                          */
/*                                                                            */
/* Order two manifest records by path, byte for byte -- the comparison both   */
/* qsort (when a manifest is loaded) and bsearch (when a record is looked     */
/* up) use, so the two always agree.  Byte order, not a case-folded one: ext4 */
/* names are case-sensitive and "Makefile" and "makefile" are two records.    */
/*                                                                            */
/* Arguments:                                                                 */
/*     pvLeft  : a struct BFM_Record.                                         */
/*     pvRight : a struct BFM_Record.                                         */
/*                                                                            */
/* Returns:                                                                   */
/*     int : negative, zero or positive, as strcmp on the two paths.          */
/*----------------------------------------------------------------------------*/
static int BFM_ComparePaths(const void *pvLeft, const void *pvRight)
{
    const struct BFM_Record *pLeft;
    const struct BFM_Record *pRight;

    /* qsort and bsearch hand over untyped pointers to two records. */
    pLeft  = (const struct BFM_Record *)pvLeft;
    pRight = (const struct BFM_Record *)pvRight;

    /* strcmp's ordering is the records' ordering. */
    return(strcmp(pLeft->pszPath, pRight->pszPath));
}

/*----------------------------------------------------------------------------*/
/* BFM_TypeFromName:                                                          */
/*                                                                            */
/* Turn a manifest type: value back into a BFE_TYPE_* value.                  */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszName : the value text ("file", "dir", "symlink", ...).              */
/*                                                                            */
/* Returns:                                                                   */
/*     uint32_t : the matching type; BFE_TYPE_UNKNOWN for any other text.     */
/*----------------------------------------------------------------------------*/
static uint32_t BFM_TypeFromName(const char *pszName)
{
    uint32_t i;

    for (i = 1; i < sizeof(BFM_apszTypeNames) / sizeof(BFM_apszTypeNames[0]); i++)
    {   /* One known type name per iteration (index 0 is the unknown mark). */
        if (strcmp(pszName, BFM_apszTypeNames[i]) == 0)
        {   /* The index IS the BFE_TYPE_* value. */
            return(i);
        }
    }

    /* Not a name this engine writes (a typing slip in a hand-edited file,   */
    /* or "?"): unknown, which the import treats as "no type recorded".      */
    return(BFE_TYPE_UNKNOWN);
}

/*----------------------------------------------------------------------------*/
/* BFM_RecordPathValid:                                                       */
/*                                                                            */
/* Whether a tree manifest record's path: value names a place INSIDE the      */
/* imported tree, in the one spelling BFE_Extract writes.  That is either the */
/* tree's own top, written exactly ".", or one or more names joined by '/'    */
/* (BFU_JoinRelative's form: "name", "dir/name"), where no name is empty, "." */
/* or "..".  An ext4 name can never be any of those three, nor hold a '/', so */
/* no manifest BFE_Extract writes is refused here.                            */
/*                                                                            */
/* Why it matters: a record's path is JOINED to the import's destination to   */
/* make the symbolic link it records (BFC_RestoreLinks), and lwext4 resolves  */
/* a ".." component through the directory's own ".." entry -- so a ".." could */
/* climb out of the tree, and lwext4 given a ".." in the middle of a NEW      */
/* link's path does worse still: it makes directory entries whose names       */
/* contain the slash, until the filesystem has no inodes left.  An absolute   */
/* path, a "." component or an empty one does not climb, but it names an      */
/* entry under a second spelling (or nothing at all), which no manifest       */
/* BFE_Extract wrote can contain.  Only the PATH is judged: a link's TARGET   */
/* text is Linux data and is restored as written, however it climbs.          */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszPath : the record's path: value.                                    */
/*                                                                            */
/* Returns:                                                                   */
/*     bool : true when the path is "." or a relative path of non-empty names */
/*            none of which is "." or ".."; false otherwise.                  */
/*----------------------------------------------------------------------------*/
static bool BFM_RecordPathValid(const char *pszPath)
{
    const char *pszComponent;
    size_t      cbComponent;

    if (strcmp(pszPath, BFC_ROOT_RELATIVE) == 0)
    {   /* The tree's own top: the one place "." is the whole path. */
        return(true);
    }

    if (pszPath[0] == '\0' || pszPath[0] == '/')
    {   /* Empty, or absolute: neither names a place inside the tree. */
        return(false);
    }

    /* Each name in turn: the text up to the next '/' or the end. */
    pszComponent = pszPath;

    for (;;)
    {   /* One name of the path per iteration. */
        cbComponent = strcspn(pszComponent, "/");

        if (cbComponent == 0 ||
            (cbComponent == 1 && pszComponent[0] == '.') ||
            (cbComponent == 2 && pszComponent[0] == '.' && pszComponent[1] == '.'))
        {   /* An empty name (a leading, doubled or trailing '/'), a ".", or   */
            /* a "..": not a path BFE_Extract writes.                          */
            return(false);
        }

        if (pszComponent[cbComponent] == '\0')
        {   /* That was the last name, and every name passed. */
            return(true);
        }

        /* Past the name and its '/', to the next one. */
        pszComponent += cbComponent + 1;
    }
}

/*----------------------------------------------------------------------------*/
/* BFM_Load:                                                                  */
/*                                                                            */
/* Read a manifest file into a sorted array of records.  A manifest that is   */
/* simply not there is NOT an error -- it yields zero records, and the import */
/* then uses its defaults -- because "restore metadata where a manifest is    */
/* present" is exactly the behaviour the option promises.                     */
/*                                                                            */
/* Any manifest, a tree's or a sidecar, may begin with a UTF-8 byte-order     */
/* mark that an editor added when it saved the file; it is looked past        */
/* before anything is read, so the first line is read as it was written.      */
/*                                                                            */
/* A TREE's manifest must first prove it is one: its first line must be the   */
/* mark BFE_Extract writes (BFC_MANIFEST_MARK) or the format's one other      */
/* accepted mark (BFC_ALT_MARK).  A file of that name without either is a     */
/* user's own data, not a manifest, and is reported as such with zero         */
/* records rather than parsed -- a stray "path:" line in it would otherwise   */
/* be taken for a record.  A sidecar is not asked for the mark: its name is   */
/* the extracted file's own with .ext4meta added, and BFE_Extract writes it   */
/* with no mark line, so its first line is its "path:" line.                  */
/*                                                                            */
/* The parse is forgiving on purpose, since a person may have edited the      */
/* file: a "path:" line starts a record, later keys attach to the record in   */
/* progress, '#' lines and unknown keys are ignored, and CRLF line ends are   */
/* accepted.  A key before any "path:" line has no record to attach to and    */
/* is dropped.                                                                */
/*                                                                            */
/* Arguments:                                                                 */
/*     pwszManifest  : the manifest's host path.                              */
/*     bNeedMark     : true for a tree's manifest, which must begin with one  */
/*                     of the two mark lines; false for a sidecar.            */
/*     ppRecords     : receives the malloc'd, path-sorted record array (NULL  */
/*                     when there are none).                                  */
/*     pnRecords     : receives the record count.                             */
/*     pbNotManifest : receives true when a file was read and lacks the mark  */
/*                     (bNeedMark only), false otherwise; may be NULL when    */
/*                     bNeedMark is false.                                    */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK (also for an absent manifest -- one not there at all --   */
/*           and for a file without the mark: *pbNotManifest says which);     */
/*           BFE_ERR_IO_HOST when a manifest that is there cannot be opened   */
/*           (locked, no access), measured or read;                           */
/*           BFE_ERR_NO_MEMORY.  These are bare codes: nothing is recorded,   */
/*           and the caller records the failure.                              */
/*----------------------------------------------------------------------------*/
static int BFM_Load(const wchar_t *pwszManifest, bool bNeedMark, struct BFM_Record **ppRecords, int *pnRecords,
                    bool *pbNotManifest)
{
    struct BFM_Record *pList;
    struct BFM_Record *pGrown;
    FILE              *pFile;
    char              *pszText;
    char              *pszLine;
    char              *pszNext;
    char              *pszValue;
    char              *pszCopy;
    char              *pszFirst;
    long long          llSize;
    size_t             cbText;
    size_t             cbLine;
    size_t             cbMark;
    int                nCapacity;
    int                nUsed;

    /* No records until the file is parsed, and nothing yet found that is   */
    /* not a manifest.                                                      */
    *ppRecords = NULL;
    *pnRecords = 0;

    if (pbNotManifest != NULL)
    {   /* The caller wants to know about a file without the mark. */
        *pbNotManifest = false;
    }

    /* Binary, so the CR of a CRLF file reaches the parse, which drops it. */
    pFile = _wfopen(pwszManifest, L"rb");

    if (pFile == NULL)
    {   /* Not opened.  Only a manifest that is not there (ENOENT, read at   */
        /* once, before anything can change errno) is the optional absence   */
        /* the contract allows; one that is there but locked or unreadable   */
        /* is a failure, never taken for absent -- else an import would      */
        /* silently restore nothing the manifest holds.                      */
        if (errno == ENOENT)
        {   /* No manifest beside the data: nothing to restore, and that is fine. */
            return(BFE_OK);
        }

        /* There but not readable: the host failure, bare as every code here. */
        return(BFE_ERR_IO_HOST);
    }

    /* STEP 1: read the whole file into one NUL-terminated buffer.  Every   */
    /* failure from here closes the file it opened; the codes are bare,     */
    /* and the caller records the message.                                  */
    if (_fseeki64(pFile, 0, SEEK_END) != 0)
    {   /* The file cannot be measured: close it and fail. */
        fclose(pFile);
        return(BFE_ERR_IO_HOST);
    }

    /* Its size is the end's position; then back to the start to read. */
    llSize = _ftelli64(pFile);

    if (llSize < 0 || _fseeki64(pFile, 0, SEEK_SET) != 0)
    {   /* The size or the rewind failed: close it and fail. */
        fclose(pFile);
        return(BFE_ERR_IO_HOST);
    }

    /* Room for the whole text plus its terminator.  A manifest is small   */
    /* text, so holding all of it is simpler than parsing across reads.    */
    cbText  = (size_t)llSize;
    pszText = (char *)malloc(cbText + 1);

    if (pszText == NULL)
    {   /* Out of memory for the file's text: close it and fail. */
        fclose(pFile);
        return(BFE_ERR_NO_MEMORY);
    }

    if (fread(pszText, 1, cbText, pFile) != cbText)
    {   /* A short read: free the buffer, close the file, and fail. */
        free(pszText);
        fclose(pFile);
        return(BFE_ERR_IO_HOST);
    }

    /* The file is done with; the text becomes one string the line walk   */
    /* below cuts up in place.                                            */
    fclose(pFile);
    pszText[cbText] = '\0';

    /* Where the text proper begins: past a UTF-8 byte-order mark (EF BB    */
    /* BF) when it starts with one, for every manifest.  Left in, the       */
    /* mark would join the first line -- a tree's mark line would not       */
    /* match, and a sidecar's first key would read as "<mark>path", begin   */
    /* no record, and leave the whole file unused with nothing said.  The   */
    /* three tests stop at the first byte that differs, so a text shorter   */
    /* than the mark is never read past its terminator.                     */
    pszFirst = pszText;

    if ((unsigned char)pszFirst[0] == 0xEF && (unsigned char)pszFirst[1] == 0xBB &&
        (unsigned char)pszFirst[2] == 0xBF)
    {   /* A byte-order mark an editor put first: look past it. */
        pszFirst += 3;
    }

    /* STEP 2: for a tree's manifest, the mark.  The first line must be one   */
    /* of the two mark lines exactly, ended by LF, CR or the end of the       */
    /* text -- not merely begin with one, so a line that only starts the      */
    /* same way does not pass.                                                */
    if (bNeedMark)
    {   /* A tree's manifest: test its first line. */

        /* The length of the first line, up to its LF or CR. */
        cbLine = strcspn(pszFirst, "\r\n");

        /* Compared with each mark in turn: equal length and equal bytes. */
        cbMark = strlen(BFC_MANIFEST_MARK);

        if (!(cbLine == cbMark && memcmp(pszFirst, BFC_MANIFEST_MARK, cbMark) == 0))
        {   /* Not this engine's own mark; try the format's other one. */
            cbMark = strlen(BFC_ALT_MARK);

            if (!(cbLine == cbMark && memcmp(pszFirst, BFC_ALT_MARK, cbMark) == 0))
            {   /* Neither: a file of the manifest's name that is not one.    */
                /* Nothing is parsed; the caller is told, and imports it as   */
                /* the data it is.                                            */
                free(pszText);
                *pbNotManifest = true;
                return(BFE_OK);
            }
        }
    }

    /* STEP 3: walk the lines, building records -- an empty list, grown on   */
    /* the first "path:" line -- from the first byte past any byte-order     */
    /* mark (the text is still freed by its own start, pszText).             */
    pList     = NULL;
    nCapacity = 0;
    nUsed     = 0;
    pszLine   = pszFirst;

    while (*pszLine != '\0')
    {   /* One line of the manifest per iteration. */

        /* Cut the line at its LF and step past it; drop a CR before the LF.   */
        /* A NUL byte inside the file ends the text early (the walk stops      */
        /* there), which is harmless for a text manifest.                      */
        pszNext = strchr(pszLine, '\n');

        if (pszNext != NULL)
        {   /* Terminate this line and remember where the next begins. */
            *pszNext = '\0';
            pszNext++;
        }
        else
        {   /* The last line has no LF: the next "line" is the end of text. */
            pszNext = pszLine + strlen(pszLine);
        }

        /* The line's length, now that it is cut at its LF: what finds the   */
        /* last character, which is a CR in a file saved with CRLF ends.     */
        cbLine = strlen(pszLine);

        if (cbLine > 0 && pszLine[cbLine - 1] == '\r')
        {   /* A CRLF file: drop the CR. */
            pszLine[cbLine - 1] = '\0';
        }

        /* Split "key: value" at the FIRST ": ", so a value may itself hold    */
        /* a colon (a path can).  A line without one -- blank, a comment, or   */
        /* damage -- carries nothing.                                          */
        pszValue = strstr(pszLine, ": ");

        if (pszLine[0] == '#' || pszValue == NULL)
        {   /* A comment, a blank separator, or not a key line. */
            pszLine = pszNext;
            continue;
        }

        /* End the key at the ": " and point the value past it. */
        *pszValue = '\0';
        pszValue += 2;

        if (strcmp(pszLine, "path") == 0)
        {   /* A new record begins.  Grow the array when it is full. */
            if (nUsed == nCapacity)
            {   /* The array is full: double its capacity (64 to begin with).    */
                /* Doubling, not one more slot each time, so a manifest of N     */
                /* records costs about log2(N) reallocs and copies rather than   */
                /* N; the starting 64 is a size that holds a small tree in one   */
                /* allocation (why 64 in particular is not recorded).            */
                if (nCapacity == 0)
                {   /* First allocation. */
                    nCapacity = 64;
                }
                else
                {   /* Every later growth doubles. */
                    nCapacity = nCapacity * 2;
                }

                pGrown = (struct BFM_Record *)realloc(pList, (size_t)nCapacity * sizeof(*pList));

                if (pGrown == NULL)
                {   /* Out of memory growing the list: the old list is still   */
                    /* valid (realloc left it), so free it and the text.       */
                    BFM_Free(pList, nUsed);
                    free(pszText);
                    return(BFE_ERR_NO_MEMORY);
                }

                /* The list at its new size. */
                pList = pGrown;
            }

            /* The path, copied out of the text (which is freed at the end). */
            pszCopy = (char *)malloc(strlen(pszValue) + 1);

            if (pszCopy == NULL)
            {   /* Out of memory for the path: free what is built, and fail. */
                BFM_Free(pList, nUsed);
                free(pszText);
                return(BFE_ERR_NO_MEMORY);
            }

            /* The copy itself: the buffer was sized from the value. */
            strcpy(pszCopy, pszValue);

            /* The new record: zeroed, so every bHas* flag starts false and the   */
            /* target NULL, with only its path set; its type is UNKNOWN (0)       */
            /* until a type: line says otherwise.  Two blocks with the same       */
            /* path both stay -- a lookup then finds either one.                  */
            memset(&pList[nUsed], 0, sizeof(pList[nUsed]));
            pList[nUsed].pszPath = pszCopy;
            nUsed++;
        }
        else if (nUsed > 0)
        {   /* A key of the record in progress (the last one started). */
            if (strcmp(pszLine, "type") == 0)
            {   /* The entry's type. */
                pList[nUsed - 1].uiType = BFM_TypeFromName(pszValue);
            }
            else if (strcmp(pszLine, "target") == 0)
            {   /* A symlink's target text. */
                pszCopy = (char *)malloc(strlen(pszValue) + 1);

                if (pszCopy == NULL)
                {   /* Out of memory for the target: free what is built, and fail. */
                    BFM_Free(pList, nUsed);
                    free(pszText);
                    return(BFE_ERR_NO_MEMORY);
                }

                /* A second target: line in one block replaces the first. */
                strcpy(pszCopy, pszValue);
                free(pList[nUsed - 1].pszTarget);
                pList[nUsed - 1].pszTarget = pszCopy;
            }
            else if (strcmp(pszLine, "mode") == 0)
            {   /* Permission bits, written in octal; masked to the 12 a mode   */
                /* holds, so a hand-edited value cannot set the type bits.      */
                /* (The numbers below are not range-checked: text that is not   */
                /* a number reads as 0.)                                        */
                pList[nUsed - 1].uiMode   = (uint32_t)strtoul(pszValue, NULL, 8) & BFX_PERM_MASK;
                pList[nUsed - 1].bHasMode = true;
            }
            else if (strcmp(pszLine, "uid") == 0)
            {   /* Owner id, decimal. */
                pList[nUsed - 1].uiUid   = (uint32_t)strtoul(pszValue, NULL, 10);
                pList[nUsed - 1].bHasUid = true;
            }
            else if (strcmp(pszLine, "gid") == 0)
            {   /* Group id, decimal. */
                pList[nUsed - 1].uiGid   = (uint32_t)strtoul(pszValue, NULL, 10);
                pList[nUsed - 1].bHasGid = true;
            }
            else if (strcmp(pszLine, "mtime") == 0)
            {   /* Modify time, epoch seconds. */
                pList[nUsed - 1].uiMtime   = (uint32_t)strtoul(pszValue, NULL, 10);
                pList[nUsed - 1].bHasMtime = true;
            }
            else if (strcmp(pszLine, "atime") == 0)
            {   /* Access time, epoch seconds. */
                pList[nUsed - 1].uiAtime   = (uint32_t)strtoul(pszValue, NULL, 10);
                pList[nUsed - 1].bHasAtime = true;
            }
            else if (strcmp(pszLine, "size") == 0)
            {   /* The size when extracted, decimal: compared with the host       */
                /* file's to tell whether it has changed since (BFC_ApplyMeta).   */
                pList[nUsed - 1].ullSize  = (uint64_t)strtoull(pszValue, NULL, 10);
                pList[nUsed - 1].bHasSize = true;
            }

            /* Any other key -- ctime, which this engine writes, or one a   */
            /* person added -- falls through and is ignored.                */
        }

        /* On to the next line. */
        pszLine = pszNext;
    }

    /* Every string needed was copied out; the text is done with. */
    free(pszText);

    /* STEP 4: sort by path so a lookup is a binary search. */
    if (nUsed > 1)
    {   /* Two or more records: put them in path order. */
        qsort(pList, (size_t)nUsed, sizeof(*pList), BFM_ComparePaths);
    }

    /* Hand the list over (the caller frees it with BFM_Free). */
    *ppRecords = pList;
    *pnRecords = nUsed;

    /* The manifest is loaded (possibly with zero records). */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFM_Find:                                                                  */
/*                                                                            */
/* Look up the manifest record for one relative path.                         */
/*                                                                            */
/* Arguments:                                                                 */
/*     pRun   : the running import (holds the loaded manifest).               */
/*     pszRel : the item's manifest path.                                     */
/*                                                                            */
/* Returns:                                                                   */
/*     const struct BFM_Record * : the record, or NULL when there is no       */
/*                                 manifest or no block for that path.        */
/*----------------------------------------------------------------------------*/
static const struct BFM_Record *BFM_Find(const struct BFC_Run *pRun, const char *pszRel)
{
    struct BFM_Record key;

    if (pRun->pRecords == NULL || pRun->nRecords == 0)
    {   /* No manifest was loaded (or it held no blocks): every item gets   */
        /* the import's defaults.                                           */
        return(NULL);
    }

    /* bsearch compares paths only, so the key needs just that field; the      */
    /* cast drops const for the struct's sake and the string is not written.   */
    memset(&key, 0, sizeof(key));
    key.pszPath = (char *)pszRel;

    /* NULL when no record has that path. */
    return((const struct BFM_Record *)bsearch(&key, pRun->pRecords, (size_t)pRun->nRecords,
                                              sizeof(key), BFM_ComparePaths));
}

/*----------------------------------------------------------------------------*/
/* BFC_ExtractBytes:                                                          */
/*                                                                            */
/* Stream one image file's bytes out to a host file, exactly, raising a       */
/* progress event per buffer.  The host file is created or truncated.  On     */
/* any failure or a cancel the partly written host file is REMOVED: a         */
/* truncated copy that looks like the real file is worse than no file.        */
/*                                                                            */
/* Arguments:                                                                 */
/*     pRun     : the running extract.                                        */
/*     pszImage : the image file to read.                                     */
/*     pwszHost : the host file to write.                                     */
/*     ullTotal : the image file's size, for the progress events.             */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK on a complete copy; BFE_ERR_IO_IMAGE / BFE_ERR_NOT_FOUND  */
/*           for a read-side failure; BFE_ERR_IO_HOST for a write-side one;   */
/*           BFE_ERR_NO_MEMORY; BFE_ERR_CANCELLED.                            */
/*----------------------------------------------------------------------------*/
static int BFC_ExtractBytes(struct BFC_Run *pRun, const char *pszImage, const wchar_t *pwszHost,
                            uint64_t ullTotal)
{
    BFS_File  *pFile;
    FILE      *pHost;
    char      *pBuffer;
    size_t     cbRead;
    uint64_t   ullDone;
    int        iStatus;
    int        rc;

    /* The streaming buffer, one per file copy.  The failures below return   */
    /* bare codes: the caller turns them into this item's ERROR event.       */
    pBuffer = (char *)malloc(BFX_IO_BUFFER);

    if (pBuffer == NULL)
    {   /* Out of memory for the copy buffer: nothing else is held yet and   */
        /* the host is untouched, so the bare code is all there is to do.    */
        return(BFE_ERR_NO_MEMORY);
    }

    /* The image file, for reading, through the filesystem-neutral layer    */
    /* (bfe_fs.c) -- opened BEFORE the host file, for the reason given at   */
    /* the host file's open below.                                          */
    rc = BFS_FileOpen(pszImage, "rb", &pFile);

    if (rc != 0)
    {   /* The image file cannot be opened for reading (gone, or damaged).   */
        /* Free the buffer -- the only thing held -- and fail with the       */
        /* errno-style result mapped to the engine's code (bfe_fs.c gives    */
        /* lwext4's, or FatFs's translated to the same); the host is         */
        /* untouched.                                                        */
        free(pBuffer);
        return(BFX_MapErrno(rc));
    }

    /* The host file, created or truncated, binary so no byte is translated.   */
    /* Opened only after the image file, so an image file that will not        */
    /* open never costs the host file its old contents.  From the moment       */
    /* this succeeds, an existing host file (the caller answered OVERWRITE)    */
    /* is gone: a failure below removes the partial one, and the old one       */
    /* cannot come back.                                                       */
    pHost = _wfopen(pwszHost, L"wb");

    if (pHost == NULL)
    {   /* The host file cannot be created (no permission, a bad folder). */

        /* Give back what is held -- the image file, open only for reading,   */
        /* so its close loses nothing, and the buffer -- and fail.  Nothing   */
        /* at the host path is removed: the open did not succeed, so what     */
        /* is there (if anything) is not this copy's to delete.               */
        BFS_FileClose(pFile);
        free(pBuffer);
        return(BFE_ERR_IO_HOST);
    }

    /* Nothing wrong and nothing copied yet. */
    iStatus = BFE_OK;
    ullDone = 0;

    /* Until the image file's end, a failure, or a cancel. */
    for (;;)
    {   /* One buffer of file data per iteration. */

        /* The next buffer from the image file.  Either library reads from   */
        /* the handle's own position and stops at the file's size, so a      */
        /* short count comes only at the end.  cbRead is cleared here        */
        /* although both clear it too -- FatFs's f_read first thing          */
        /* (bfe_fs.c copies its count out), lwext4's ext4_fread only AFTER   */
        /* its two early returns (a write-only handle, a zero-length         */
        /* request), neither of which can occur on this "rb" open of a       */
        /* whole buffer -- so the clear is a guard, not a need.              */
        cbRead = 0;
        rc     = BFS_FileRead(pFile, pBuffer, BFX_IO_BUFFER, &cbRead);

        if (rc != 0)
        {   /* The image could not be read.  Leave the loop with the reason;   */
            /* the one clean-up below the loop handles every way out.          */
            iStatus = BFX_MapErrno(rc);
            break;
        }

        if (cbRead == 0)
        {   /* End of file: the copy is complete.  The read stops at the   */
            /* file's size (an inode's, or a FAT directory entry's), so    */
            /* this is the whole file.                                     */
            break;
        }

        if (fwrite(pBuffer, 1, cbRead, pHost) != cbRead)
        {   /* The host refused the bytes (a full disk).  No Windows error   */
            /* is kept, so a full disk and a write error read the same.      */
            iStatus = BFE_ERR_IO_HOST;
            break;
        }

        /* One progress event per buffer; a cancel reply latches the flag. */
        ullDone += cbRead;
        BFC_Emit(pRun, BFE_EVENT_PROGRESS, BFE_TYPE_FILE, BFE_OK, pszImage, pwszHost, ullDone, ullTotal);

        if (pRun->bCancelled)
        {   /* The caller asked to stop in the middle of this file. */
            iStatus = BFE_ERR_CANCELLED;
            break;
        }
    }

    /* The image file is only read: its close cannot lose anything. */
    BFS_FileClose(pFile);

    /* The host file's close IS checked: the C library's last buffer is   */
    /* written there, and a full disk can refuse it.                      */
    if (fclose(pHost) != 0 && iStatus == BFE_OK)
    {   /* The final flush to the host failed after an otherwise clean copy. */
        iStatus = BFE_ERR_IO_HOST;
    }

    /* The buffer is done with. */
    free(pBuffer);

    if (iStatus != BFE_OK)
    {   /* Do not leave a partial file behind looking like a whole one.       */
        /* After the close, so the copy's own handle does not hold it; the    */
        /* result is not checked -- the item is reported failed either way.   */
        _wremove(pwszHost);
    }

    /* BFE_OK, or the reason the copy stopped. */
    return(iStatus);
}

/*----------------------------------------------------------------------------*/
/* Bfc_ReadLinkText:                                                          */
/*                                                                            */
/* A symbolic link's target text, NUL-terminated, in a buffer of its own.     */
/* The inode's size is the text's length, so the buffer is exact -- but only  */
/* a size a link can have (BFC_LINK_TARGET_MAX) is trusted, as                */
/* BFM_WriteBlock trusts it: a damaged inode's huge size is never allocated.  */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImage : the link's image path.                                      */
/*     pEntry   : the link's facts (its size is the text's length).           */
/*     ppszText : receives the text (malloc'd; the caller frees it), or NULL  */
/*                on a failure.                                               */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0, or an errno value -- EIO for a size no link has, ENOMEM, or   */
/*           the read's own failure.                                          */
/*----------------------------------------------------------------------------*/
static int Bfc_ReadLinkText(const char *pszImage, const BFE_Entry *pEntry, char **ppszText)
{
    char   *pszText;
    size_t  cbRead;
    int     rc;

    /* Nothing handed back until the text is whole. */
    *ppszText = NULL;

    if (pEntry->ullSize >= BFC_LINK_TARGET_MAX)
    {   /* A size no link's target can have: the inode is damaged, and the   */
        /* text is taken as unreadable rather than allocated.                */
        return(EIO);
    }

    /* The buffer, one byte over the text for its NUL. */
    pszText = (char *)malloc((size_t)pEntry->ullSize + 1);
    if (pszText == NULL)
    {   /* Out of memory: the caller stops the run (an ERROR, NO_MEMORY). */
        return(ENOMEM);
    }

    /* The text's bytes, at most the inode's size.  The read (lwext4's or    */
    /* squashfuse's, through BFS_ReadLink) does not terminate them, so the   */
    /* count it hands back places the NUL.                                   */
    rc = BFS_ReadLink(pszImage, pszText, (size_t)pEntry->ullSize, &cbRead);
    if (rc != 0)
    {   /* The read failed: the buffer goes, and the failure is the result. */
        free(pszText);
        return(rc);
    }

    /* Terminated and handed to the caller, who owns it now. */
    pszText[cbRead] = '\0';
    *ppszText = pszText;

    /* The text is the caller's. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* Bfc_ResolveLink:                                                           */
/*                                                                            */
/* Where a symbolic link leads, inside the open filesystem: the image path    */
/* of what it finally names, with that entry's facts.  Resolved as Linux      */
/* resolves a path, component by component -- an absolute target from the     */
/* filesystem's root, a relative one from the link's own folder; "." staying  */
/* put and ".." going up (the root's ".." being the root); every component    */
/* that is itself a link replaced by its own target where it stands, at any   */
/* level, so a folder reached through a link is resolved too.  Every link     */
/* met counts one hop, the first included; past BFE_LINK_HOPS_MAX the link    */
/* is a loop (ELOOP), which is also what stops a chain of links that point    */
/* at one another.  The path handed back holds no link, so the walk that      */
/* copies from it, and the loop test against the folders being walked,        */
/* compare real paths.                                                        */
/*                                                                            */
/* A target outside this filesystem cannot be told from a missing one -- the  */
/* image holds only its own files -- so /proc/self, a path on another         */
/* partition or a name that is simply gone all answer ENOENT, and the caller  */
/* skips the link.  The resolution is done here, not by the path walks of     */
/* lwext4 or squashfuse, because neither follows a link in a path.            */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszLink  : the link's image path (one with no link in its folders --   */
/*                every path the extract walk holds is).                      */
/*     pLink    : the link's facts (its target text's length).                */
/*     ppszReal : receives the image path of what the link names (malloc'd;   */
/*                the caller frees it), or NULL on a failure.                 */
/*     pTarget  : receives that entry's facts.                                */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0, or an errno value -- ENOENT (not in this filesystem),         */
/*           ENOTDIR (a file used as a folder on the way), ELOOP (too many    */
/*           links), ENOMEM, or a read's own failure.                         */
/*----------------------------------------------------------------------------*/
static int Bfc_ResolveLink(const char *pszLink, const BFE_Entry *pLink, char **ppszReal, BFE_Entry *pTarget)
{
    BFE_Entry  entry;
    char      *pszCur;
    char      *pszRest;
    char      *pszText;
    char      *pszNext;
    char      *pszCandidate;
    char      *pszCut;
    size_t     cbText;
    size_t     cbTail;
    size_t     p;
    size_t     q;
    size_t     r;
    char       cSave;
    int        iHops;
    bool       bMore;
    int        rc;

    /* Nothing handed back until the whole path is resolved. */
    *ppszReal = NULL;

    /* The link's own text is what is resolved, and it is the first hop. */
    iHops = 1;
    rc = Bfc_ReadLinkText(pszLink, pLink, &pszRest);
    if (rc != 0)
    {   /* The text cannot be read: nothing to resolve. */
        return(rc);
    }

    /* The folder a relative target starts from: the link's own folder,     */
    /* its path cut at the last '/' -- the root itself when the link sits   */
    /* at the top.  A copy of the path, as it is cut and grown below.       */
    pszCur = (char *)malloc(strlen(pszLink) + 1);
    if (pszCur == NULL)
    {   /* Out of memory: the text goes with the failure. */
        free(pszRest);
        return(ENOMEM);
    }

    strcpy(pszCur, pszLink);
    pszCut = strrchr(pszCur, '/');
    if (pszCut == NULL || pszCut == pszCur)
    {   /* A link at the root (or a path with no '/', which the walk never   */
        /* gives): its folder is the root.                                   */
        strcpy(pszCur, "/");
    }
    else
    {   /* A link deeper down: its folder is everything before its name. */
        *pszCut = '\0';
    }

    /* An absolute target starts from the root instead. */
    if (pszRest[0] == '/')
    {   /* Absolute: the open filesystem's root, as the link's own system   */
        /* would start from its root.                                       */
        strcpy(pszCur, "/");
    }

    /* The components, one at a time, from the front of what is left.  The   */
    /* text is cut IN PLACE: a component's end is overwritten with a NUL     */
    /* while it is looked at, and put back after, so no copy of each name    */
    /* is made.                                                              */
    p = 0;
    for (;;)
    {   /* One component of the remaining path per iteration. */

        /* Any run of '/' between components is one separator. */
        while (pszRest[p] == '/')
        {   /* One more '/' passed over. */
            p++;
        }

        if (pszRest[p] == '\0')
        {   /* Nothing left: pszCur is the answer. */
            break;
        }

        /* The component: from p to the next '/' or the end.  Whether more   */
        /* follow it is needed below -- a file may only end a path.          */
        q = p;
        while (pszRest[q] != '\0' && pszRest[q] != '/')
        {   /* One more byte of the name. */
            q++;
        }

        cSave = pszRest[q];
        r = q;
        while (pszRest[r] == '/')
        {   /* The separator after it, passed over to see what follows. */
            r++;
        }

        bMore = (pszRest[r] != '\0');
        pszRest[q] = '\0';

        if (strcmp(pszRest + p, ".") == 0)
        {   /* "." names the folder it is in: nothing to do. */
            pszRest[q] = cSave;
            p = q;
            continue;
        }

        if (strcmp(pszRest + p, "..") == 0)
        {   /* ".." goes up one folder: pszCur cut at its last '/'.  The     */
            /* root's ".." is the root, as on Linux -- never a step out of   */
            /* the filesystem.                                               */
            pszCut = strrchr(pszCur, '/');
            if (pszCut == NULL || pszCut == pszCur)
            {   /* At the root, or one level under it: the root. */
                strcpy(pszCur, "/");
            }
            else
            {   /* Deeper: the parent folder. */
                *pszCut = '\0';
            }

            pszRest[q] = cSave;
            p = q;
            continue;
        }

        /* A name: the path it gives under the current folder, and its        */
        /* facts -- not followed (BFX_FillEntry reads the entry itself), so   */
        /* a link is seen as one.                                             */
        pszCandidate = BFU_JoinImage(pszCur, pszRest + p);
        if (pszCandidate == NULL)
        {   /* Out of memory: both strings go with the failure. */
            free(pszCur);
            free(pszRest);
            return(ENOMEM);
        }

        rc = BFX_FillEntry(pszCandidate, pszRest + p, &entry);
        pszRest[q] = cSave;
        if (rc != 0)
        {   /* Not there: missing, or outside this filesystem altogether --   */
            /* the image cannot tell the two apart.  The failure is the       */
            /* answer (ENOENT, or ENOTDIR when a file stood where a folder    */
            /* was needed), and the caller skips the link.                    */
            free(pszCandidate);
            free(pszCur);
            free(pszRest);
            return(rc);
        }

        if (entry.uiType == BFE_TYPE_SYMLINK)
        {   /* Another link on the way: its text replaces it where it stands.   */
            /* One more hop -- and past the limit, a loop: a chain that never   */
            /* reaches a real entry would otherwise go on for ever.             */
            iHops++;
            if (iHops > BFE_LINK_HOPS_MAX)
            {   /* Too many: refused as a loop. */
                free(pszCandidate);
                free(pszCur);
                free(pszRest);
                return(ELOOP);
            }

            rc = Bfc_ReadLinkText(pszCandidate, &entry, &pszText);
            free(pszCandidate);
            if (rc != 0)
            {   /* That link's text cannot be read: nothing further to follow. */
                free(pszCur);
                free(pszRest);
                return(rc);
            }

            /* The new remaining path: the link's text, a '/', and whatever   */
            /* followed the link's name (the tail from r), in one buffer.     */
            cbText = strlen(pszText);
            cbTail = strlen(pszRest + r);
            pszNext = (char *)malloc(cbText + 1 + cbTail + 1);
            if (pszNext == NULL)
            {   /* Out of memory: every string goes with the failure. */
                free(pszText);
                free(pszCur);
                free(pszRest);
                return(ENOMEM);
            }

            memcpy(pszNext, pszText, cbText);
            pszNext[cbText] = '/';
            memcpy(pszNext + cbText + 1, pszRest + r, cbTail + 1);

            /* An absolute text restarts from the root; a relative one goes    */
            /* on from the folder the link was found in (pszCur, unchanged).   */
            if (pszText[0] == '/')
            {   /* Absolute: back to the root. */
                strcpy(pszCur, "/");
            }

            /* The old remaining path and the text are spent; resolution   */
            /* starts again at the front of the new one.                   */
            free(pszText);
            free(pszRest);
            pszRest = pszNext;
            p = 0;
            continue;
        }

        if (bMore && entry.uiType != BFE_TYPE_DIR)
        {   /* A file (or a device) with more of the path after it: a path   */
            /* cannot go through it, as on Linux.                            */
            free(pszCandidate);
            free(pszCur);
            free(pszRest);
            return(ENOTDIR);
        }

        /* An ordinary entry: the walk is now at it. */
        free(pszCur);
        pszCur = pszCandidate;
        p = q;
    }

    /* The path is spent; what it named is pszCur.  Its facts are read once   */
    /* more here, whatever the last step was -- a name, or a "." or ".."      */
    /* that moved the walk to a folder whose facts were not read.             */
    free(pszRest);
    rc = BFX_FillEntry(pszCur, BFU_LastComponent(pszCur), pTarget);
    if (rc != 0)
    {   /* Gone between the steps (or the root unreadable): the failure. */
        free(pszCur);
        return(rc);
    }

    /* The real path, the caller's to free. */
    *ppszReal = pszCur;

    /* Resolved. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* Bfc_InsideWalk:                                                            */
/*                                                                            */
/* Whether copying a followed link's folder target would lead the extract     */
/* back into itself: the target is a folder the walk is already inside, or    */
/* holds one -- its copy would meet the same link again, without end.  Each   */
/* folder being walked is in pRun's chain (Bfc_strctWalkDir), every path in   */
/* it real (no link in it).  Paths are compared byte for byte: only ext2/3/4  */
/* and squashfs hold links, and both match names exactly.  Copying the same   */
/* data twice through two links that are NOT a loop is allowed and goes on.   */
/*                                                                            */
/* Arguments:                                                                 */
/*     pRun     : the running extract.                                        */
/*     pszReal  : the folder target's real image path.                        */
/*                                                                            */
/* Returns:                                                                   */
/*     bool : true for a loop (the link is skipped), false otherwise.         */
/*----------------------------------------------------------------------------*/
static bool Bfc_InsideWalk(const struct BFC_Run *pRun, const char *pszReal)
{
    const struct Bfc_strctWalkDir *pDir;
    size_t                         cbReal;

    if (strcmp(pszReal, "/") == 0 && pRun->pWalkDirs != NULL)
    {   /* The root holds every folder, so a link to it from anywhere inside   */
        /* a walk is a loop.                                                   */
        return(true);
    }

    /* Each folder being walked, from the innermost out. */
    cbReal = strlen(pszReal);
    for (pDir = pRun->pWalkDirs; pDir != NULL; pDir = pDir->pParent)
    {   /* One folder of the walk per iteration. */
        if (strcmp(pDir->pszImage, pszReal) == 0 ||
            (strncmp(pDir->pszImage, pszReal, cbReal) == 0 && pDir->pszImage[cbReal] == '/'))
        {   /* The target is this folder, or holds it (its path followed by   */
            /* a '/' begins the folder's -- the '/' keeps "/a/b" from         */
            /* counting as holding "/a/bc").                                  */
            return(true);
        }
    }

    /* No folder of the walk is the target or under it. */
    return(false);
}

/*----------------------------------------------------------------------------*/
/* BFC_ExtractNode:                                                           */
/*                                                                            */
/* Extract one image entry to its host path, and -- for a directory -- every  */
/* entry beneath it.  A child whose name Windows cannot hold, or that is not  */
/* valid UTF-8, is recorded and SKIPPED without touching the host.  A failure */
/* on one item is reported and the walk continues; only a cancel, or an item  */
/* that ran out of memory (BFC_Emit), stops it.  With links followed, a       */
/* symbolic link is resolved and its target extracted in its place by this    */
/* same function, under the link's host and manifest paths; a directory       */
/* level keeps its record in pRun's chain of folders being walked while its   */
/* children are extracted, which is what the loop test reads.                 */
/*                                                                            */
/* Arguments:                                                                 */
/*     pRun     : the running extract.                                        */
/*     pszImage : the entry's image path.                                     */
/*     pwszHost : the host path to create for it.                             */
/*     pszRel   : the entry's manifest path.                                  */
/*     pEntry   : the entry's facts, already read by the caller.              */
/*                                                                            */
/* Returns:                                                                   */
/*     void : the outcome is in the events raised and in pRun's counters.     */
/*----------------------------------------------------------------------------*/
static void BFC_ExtractNode(struct BFC_Run *pRun, const char *pszImage, const wchar_t *pwszHost,
                            const char *pszRel, const BFE_Entry *pEntry)
{
    struct __utimbuf64      times;
    struct Bfc_strctWalkDir walkDir;
    BFE_Entry            child;
    BFE_Entry            target;
    char                *pszReal;
    char               **ppszNames;
    char                *pszChildImage;
    char                *pszChildRel;
    wchar_t             *pwszName;
    wchar_t             *pwszChildHost;
    int                  nNames;
    bool                 bNoMemory;
    int                  iKind;
    int                  iReply;
    int                  iStatus;
    int                  rc;
    int                  i;

    if (pRun->bCancelled)
    {   /* The run was cancelled: do nothing more at any level. */
        return;
    }

    if (pEntry->uiType == BFE_TYPE_SYMLINK && pRun->optExtract.bFollowLinks)
    {   /* A symbolic link, and the caller asked for links to be followed:    */
        /* what it names is copied under the link's own name and manifest     */
        /* path, from the target's real image path, with the target's facts   */
        /* -- so the manifest records a file or a folder, and an import       */
        /* gives a real one back, not a link.                                 */
        rc = Bfc_ResolveLink(pszImage, pEntry, &pszReal, &target);

        if (rc == ENOENT || rc == ENOTDIR || rc == ELOOP)
        {   /* Nothing in this filesystem to copy: the target is missing or    */
            /* outside it (/proc, another partition), runs through a file,     */
            /* or the chain of links is a loop.  The link is recorded as the   */
            /* link it is and SKIPPED with the reason, and the copy goes on    */
            /* -- as an unfollowed link would be.                              */
            BFM_WriteBlock(pRun, pszRel, pEntry, pszImage);
            if (rc == ELOOP)
            {   /* Too many links: reported as a loop. */
                BFC_Emit(pRun, BFE_EVENT_SKIPPED, BFE_TYPE_SYMLINK, BFE_ERR_LINK_LOOP, pszImage, pwszHost, 0, 0);
            }
            else
            {   /* Not found, or not a folder where one was needed. */
                BFC_Emit(pRun, BFE_EVENT_SKIPPED, BFE_TYPE_SYMLINK, BFX_MapErrno(rc), pszImage, pwszHost, 0, 0);
            }

            return;
        }

        if (rc != 0)
        {   /* A real failure -- the link's text or an entry on the way could   */
            /* not be read, or memory ran out (NO_MEMORY, which stops the       */
            /* run, BFC_Emit): an ERROR, not a skip, and no manifest block,     */
            /* nothing having been learned.                                     */
            BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_SYMLINK, BFX_MapErrno(rc), pszImage, pwszHost, 0, 0);
            return;
        }

        if (target.uiType == BFE_TYPE_DIR && Bfc_InsideWalk(pRun, pszReal))
        {   /* A folder the copy is already inside, or one that holds it:      */
            /* copying it would meet this link again, without end.  The link   */
            /* is recorded and SKIPPED as a loop, and the copy goes on.        */
            BFM_WriteBlock(pRun, pszRel, pEntry, pszImage);
            BFC_Emit(pRun, BFE_EVENT_SKIPPED, BFE_TYPE_SYMLINK, BFE_ERR_LINK_LOOP, pszImage, pwszHost, 0, 0);
            free(pszReal);
            return;
        }

        /* The target, copied as any entry of its type is -- a file's bytes,   */
        /* a folder and everything under it (its own links followed in         */
        /* turn), anything else skipped as its type is -- to the link's host   */
        /* path and manifest path.  The real path is freed after, the copy     */
        /* done with it.                                                       */
        BFC_ExtractNode(pRun, pszReal, pwszHost, pszRel, &target);
        free(pszReal);
        return;
    }

    if (pEntry->uiType == BFE_TYPE_DIR)
    {   /* A directory: make the host folder, record it, extract its children. */

        /* The directory's START.  A cancel reply here is seen by the child   */
        /* loop's test below.                                                 */
        BFC_Emit(pRun, BFE_EVENT_START, BFE_TYPE_DIR, BFE_OK, pszImage, pwszHost, 0, 0);

        /* Make the folder; an existing one is merged into rather than refused. */
        if (_wmkdir(pwszHost) != 0)
        {   /* The folder was not created: fine if it is already a folder. */
            iKind = BFC_HostKind(pwszHost, NULL, NULL);

            if (iKind != BFC_HOST_DIR)
            {   /* A file is in the way, or the folder cannot be made at all. */
                if (iKind == BFC_HOST_NONE)
                {   /* Nothing there and it could not be created. */
                    iStatus = BFE_ERR_IO_HOST;
                }
                else
                {   /* Something that is not a folder holds the name. */
                    iStatus = BFE_ERR_EXISTS;
                }

                /* Report the directory failed; none of its children are tried.   */
                /* Its manifest block is not written either, so the manifest      */
                /* never describes a folder that is not on the host.              */
                BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_DIR, iStatus, pszImage, pwszHost, 0, 0);
                return;
            }
        }

        /* The directory's own manifest block, before its children's. */
        BFM_WriteBlock(pRun, pszRel, pEntry, pszImage);

        /* Its children's names, all read before any is extracted, so the     */
        /* walk does not hold a directory open across the recursion.  A       */
        /* directory block that cannot be read fails the collection, so the   */
        /* directory is reported failed rather than copied in part.           */
        rc = BFX_CollectNames(pszImage, &ppszNames, &nNames);

        if (rc != 0)
        {   /* The image directory cannot be read (opened, or memory for the   */
            /* list): one ERROR for the directory, no DONE.  The host folder   */
            /* and its manifest block stay -- the folder does exist.           */
            BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_DIR, BFX_MapErrno(rc), pszImage, pwszHost, 0, 0);
            return;
        }

        /* This folder joins the chain of folders being walked while its       */
        /* children are copied, so a followed link below that leads back to    */
        /* it, or to a folder above it, is seen as a loop (Bfc_InsideWalk).    */
        /* The record lives in this frame; it leaves the chain when the loop   */
        /* below ends, before anything else returns.                           */
        walkDir.pszImage = pszImage;
        walkDir.pParent  = pRun->pWalkDirs;
        pRun->pWalkDirs  = &walkDir;

        for (i = 0; i < nNames && !pRun->bCancelled; i++)
        {   /* One child of this directory per iteration. */

            /* The child's image path and manifest path.  A failure on one   */
            /* child is reported and the loop goes on to the next -- bar     */
            /* memory running out, which stops the whole run (BFC_Emit).     */
            pszChildImage = BFU_JoinImage(pszImage, ppszNames[i]);
            pszChildRel   = BFU_JoinRelative(pszRel, ppszNames[i]);

            if (pszChildImage == NULL || pszChildRel == NULL)
            {   /* Out of memory composing the child's paths: reported against   */
                /* the parent (the child has no path to name), and whichever     */
                /* came is freed.  The ERROR stops the run (BFC_Emit), so the    */
                /* loop ends at its test.                                        */
                BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_UNKNOWN, BFE_ERR_NO_MEMORY, pszImage, pwszHost, 0, 0);
                free(pszChildImage);
                free(pszChildRel);
                continue;
            }

            /* The child's facts -- type, size, mode, owner, times -- from its   */
            /* INODE (BFX_FillEntry, bifrost_engine.c; on a squashfs session     */
            /* through squashfuse, BFQ_StatEntry; on a FAT one from its          */
            /* directory entry, BFT_StatEntry), which is what decides below      */
            /* whether it is descended into, copied or skipped, and what its     */
            /* manifest block records.                                           */
            rc = BFX_FillEntry(pszChildImage, ppszNames[i], &child);

            if (rc != 0)
            {   /* The child's inode cannot be read (a damaged entry).  Its type   */
                /* is unknown, so it is reported as UNKNOWN, by its image path     */
                /* only -- its host name is not worked out until after this.       */
                /* No manifest block: there are no facts to record.  Its two       */
                /* paths are freed and the walk goes on to the next child, as      */
                /* for any one item's failure -- unless the read ran out of        */
                /* memory (ENOMEM, mapped to NO_MEMORY), which stops the run.      */
                BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_UNKNOWN, BFX_MapErrno(rc), pszChildImage, NULL, 0, 0);
                free(pszChildImage);
                free(pszChildRel);
                continue;
            }

            if (pRun->bLeaveOutClash && strcmp(pszRel, BFC_ROOT_RELATIVE) == 0 &&
                BFC_NameIsManifest(ppszNames[i]))
            {   /* At the tree's top, an entry named as the manifest, and the     */
                /* caller chose to keep the manifest (BFE_EVENT_MANIFEST_CLASH,   */
                /* answered KEEP_MANIFEST): it is not extracted, so nothing       */
                /* lands on the manifest's file.  Its facts are recorded as a     */
                /* skipped entry's are, and the skip is reported -- EXISTS:       */
                /* the name on the host is the manifest's.                        */
                BFM_WriteBlock(pRun, pszChildRel, &child, pszChildImage);
                BFC_Emit(pRun, BFE_EVENT_SKIPPED, child.uiType, BFE_ERR_EXISTS, pszChildImage, NULL, 0, 0);
                free(pszChildImage);
                free(pszChildRel);
                continue;
            }

            /* The name must be one Windows can hold AND valid UTF-8; the       */
            /* conversion is only attempted for a name that passed the rules,   */
            /* and it says apart (bNoMemory) a malformed name from memory       */
            /* running out.                                                     */
            pwszName  = NULL;
            bNoMemory = false;

            if (BFU_HostNameValid(ppszNames[i]))
            {   /* Allowed by Windows' naming rules: convert it. */
                pwszName = BFU_Utf8ToWide(ppszNames[i], &bNoMemory);
            }

            if (pwszName == NULL && bNoMemory)
            {   /* Out of memory converting the name: an ERROR, never a skip   */
                /* (a skip would let the copy end "done" without the item).    */
                /* The ERROR stops the whole run at this item (BFC_Emit), so   */
                /* the loop ends at its test and the run reports NO_MEMORY.    */
                BFC_Emit(pRun, BFE_EVENT_ERROR, child.uiType, BFE_ERR_NO_MEMORY, pszChildImage, NULL, 0, 0);
                free(pszChildImage);
                free(pszChildRel);
                continue;
            }

            if (pwszName == NULL)
            {   /* The name cannot cross: record the entry, skip it.  The     */
                /* manifest keeps its facts, so nothing about it is lost; a   */
                /* directory skipped this way is not descended into.          */
                BFM_WriteBlock(pRun, pszChildRel, &child, pszChildImage);
                BFC_Emit(pRun, BFE_EVENT_SKIPPED, child.uiType, BFE_ERR_NAME, pszChildImage, NULL, 0, 0);
                free(pszChildImage);
                free(pszChildRel);
                continue;
            }

            /* The child's host path. */
            pwszChildHost = BFU_JoinHost(pwszHost, pwszName);

            if (pwszChildHost == NULL)
            {   /* Out of memory composing the child's host path. */
                BFC_Emit(pRun, BFE_EVENT_ERROR, child.uiType, BFE_ERR_NO_MEMORY, pszChildImage, NULL, 0, 0);
            }
            else
            {   /* Extract the child (recursing when it is a directory). */
                BFC_ExtractNode(pRun, pszChildImage, pwszChildHost, pszChildRel, &child);
            }

            /* This child's four strings (free of NULL is allowed). */
            free(pwszChildHost);
            free(pwszName);
            free(pszChildImage);
            free(pszChildRel);
        }

        /* The children are done: this folder leaves the chain, the one   */
        /* above becoming the innermost again.                            */
        pRun->pWalkDirs = walkDir.pParent;

        /* The name list is done with. */
        BFX_FreeNames(ppszNames, nNames);

        /* A directory's own times are not stamped, even with bSetTimes:      */
        /* only files are.  Why is not recorded; a stamp would have to come   */
        /* after the children, whose creation changes the folder's time.      */
        if (!pRun->bCancelled)
        {   /* The whole directory was walked (a child's own failure does not   */
            /* stop its parent's DONE: each item reports for itself).           */
            BFC_Emit(pRun, BFE_EVENT_DONE, BFE_TYPE_DIR, BFE_OK, pszImage, pwszHost, 0, 0);
        }

        /* The directory is finished, one way or the other. */
        return;
    }

    if (pEntry->uiType == BFE_TYPE_FILE)
    {   /* A regular file: settle any conflict, then copy the bytes. */

        /* What the host already has at that name. */
        iKind = BFC_HostKind(pwszHost, NULL, NULL);

        if (iKind == BFC_HOST_DIR || iKind == BFC_HOST_OTHER)
        {   /* A folder (or a device) holds the name; a file cannot replace it.   */
            /* An ERROR rather than a CONFLICT: there is no "overwrite" that      */
            /* could be answered, since a folder is never deleted for a file.     */
            BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_FILE, BFE_ERR_EXISTS, pszImage, pwszHost, 0, pEntry->ullSize);
            return;
        }

        if (iKind == BFC_HOST_FILE)
        {   /* The host file exists: the caller decides.  This is also where   */
            /* two image names that differ only in letter case meet, since     */
            /* NTFS folds them onto one file -- the second raises a conflict   */
            /* instead of silently overwriting the first.                      */
            iReply = BFC_Emit(pRun, BFE_EVENT_CONFLICT, BFE_TYPE_FILE, BFE_OK, pszImage, pwszHost, 0, pEntry->ullSize);

            if (iReply != BFE_REPLY_OVERWRITE)
            {   /* Skip (or cancel, already latched): leave the host file.       */
                /* No SKIPPED event -- the caller chose it -- and no manifest    */
                /* block: the host file kept is not this entry's data, so an     */
                /* import would attach this entry's facts to a different file.   */
                return;
            }
        }

        /* The file's START, which the caller may answer with cancel. */
        BFC_Emit(pRun, BFE_EVENT_START, BFE_TYPE_FILE, BFE_OK, pszImage, pwszHost, 0, pEntry->ullSize);

        if (pRun->bCancelled)
        {   /* Cancelled on the START event, before anything was written. */
            return;
        }

        /* The bytes; a failure or cancel removes the partial host file. */
        iStatus = BFC_ExtractBytes(pRun, pszImage, pwszHost, pEntry->ullSize);

        if (iStatus == BFE_ERR_CANCELLED)
        {   /* Cancelled mid-file; the partial host file is already removed.   */
            /* No ERROR event: a cancel is the caller's, not a failure, and    */
            /* BFC_RunResult reports it from the latched flag.                 */
            return;
        }

        if (iStatus != BFE_OK)
        {   /* The copy failed: one ERROR with the reason, no manifest block. */
            BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_FILE, iStatus, pszImage, pwszHost, 0, pEntry->ullSize);
            return;
        }

        if (pRun->optExtract.bSetTimes)
        {   /* Stamp the host file from the inode.  Best effort: the data is   */
            /* already safely out, so a refused timestamp is not a failure,    */
            /* and _wutime64's result is not looked at.  After the close in    */
            /* BFC_ExtractBytes, since a write through an open handle would    */
            /* move the modify time again.                                     */
            times.actime  = (__time64_t)pEntry->uiAtime;
            times.modtime = (__time64_t)pEntry->uiMtime;
            _wutime64(pwszHost, &times);
        }

        /* Record the file and report it done -- only now, so a file that   */
        /* failed has no manifest block claiming it is on the host.         */
        BFM_WriteBlock(pRun, pszRel, pEntry, pszImage);
        BFC_Emit(pRun, BFE_EVENT_DONE, BFE_TYPE_FILE, BFE_OK, pszImage, pwszHost, pEntry->ullSize, pEntry->ullSize);
        return;
    }

    /* A symlink (links not followed), device, fifo or socket: NTFS has no       */
    /* form for it.  Record it so the manifest still describes the whole tree,   */
    /* and report the skip.                                                      */
    BFM_WriteBlock(pRun, pszRel, pEntry, pszImage);
    BFC_Emit(pRun, BFE_EVENT_SKIPPED, pEntry->uiType, BFE_ERR_TYPE, pszImage, pwszHost, 0, 0);
}

/*----------------------------------------------------------------------------*/
/* BFC_RunResult:                                                             */
/*                                                                            */
/* Turn a finished run's counters into the status the public function         */
/* returns.  Memory running out outranks everything: it stopped the run at    */
/* one item, and the items after it were never tried.  A cancel outranks      */
/* errors: the caller stopped the copy, and the items never reached are not   */
/* failures.                                                                  */
/*                                                                            */
/* Arguments:                                                                 */
/*     pRun : the finished run.                                               */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_ERR_NO_MEMORY, BFE_ERR_CANCELLED, BFE_ERR_PARTIAL or BFE_OK. */
/*----------------------------------------------------------------------------*/
static int BFC_RunResult(const struct BFC_Run *pRun)
{
    if (pRun->iStopStatus != BFE_OK)
    {   /* Memory ran out at one item (BFC_Emit latched the stop): the run   */
        /* ended there, whatever had been copied before it.  Tested before   */
        /* the cancel, whose flag that stop also set.                        */
        return(BFX_Fail(pRun->iStopStatus, "out of memory at %s: the copy stopped there, and nothing after it was copied",
                        pRun->szStopPath));
    }

    if (pRun->bCancelled)
    {   /* A callback asked for the copy to stop. */
        return(BFX_Fail(BFE_ERR_CANCELLED, "the copy was cancelled"));
    }

    /* nDone is not looked at: a run that copied nothing and failed     */
    /* nothing (an empty folder; every conflict answered "skip") is a   */
    /* success.                                                         */
    if (pRun->nErrors > 0)
    {   /* It ran to the end, but not every item made it. */
        return(BFX_Fail(BFE_ERR_PARTIAL, "%d item(s) could not be copied", pRun->nErrors));
    }

    /* Every item was copied or deliberately skipped. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFC_ManifestClash:                                                         */
/*                                                                            */
/* Before a tree is extracted with a manifest, find whether the top of the    */
/* image folder holds an entry that would land on the manifest's own file --  */
/* a file or directory named _ext4meta.txt, letter case aside -- and if so    */
/* ask the caller, ONCE, which of the two to keep (BFE_EVENT_MANIFEST_CLASH). */
/* Such a folder is most likely one extracted before, manifest and all, and   */
/* copied into an image: either answer is plausible, so neither is assumed.   */
/* Only a file or a directory counts, since only those make something on the  */
/* host; a link or node of that name is recorded, never created.              */
/*                                                                            */
/* The answer is left in the run: KEEP_MANIFEST sets bLeaveOutClash, so the   */
/* walk records that entry and reports it SKIPPED; CANCEL is latched by       */
/* BFC_Emit; anything else -- KEEP_FILE, CONTINUE, or no callback at all --   */
/* turns the manifest off for this extract, so the entry is extracted like    */
/* any other.  Called before anything is created on the host, so a cancel     */
/* here leaves nothing behind.                                                */
/*                                                                            */
/* Arguments:                                                                 */
/*     pRun         : the running extract (manifest asked for).               */
/*     pszImagePath : the image folder being extracted.                       */
/*     pwszManifest : where the manifest would go, for the event.             */
/*     ppszNames    : the names at the image folder's top.                    */
/*     nNames       : how many names ppszNames holds.                         */
/*                                                                            */
/* Returns:                                                                   */
/*     void : the outcome is in pRun (bLeaveOutClash, bCancelled,             */
/*            optExtract.bWriteManifest).                                     */
/*----------------------------------------------------------------------------*/
static void BFC_ManifestClash(struct BFC_Run *pRun, const char *pszImagePath, const wchar_t *pwszManifest,
                              char **ppszNames, int nNames)
{
    BFE_Entry  child;
    char      *pszChild;
    int        iReply;
    int        rc;
    int        i;

    for (i = 0; i < nNames; i++)
    {   /* One name at the folder's top per iteration. */
        if (!BFC_NameIsManifest(ppszNames[i]))
        {   /* Not the manifest's name: no clash from this one. */
            continue;
        }

        /* Its type decides whether it would make anything on the host.  An   */
        /* entry whose path cannot be made or whose inode cannot be read is   */
        /* passed over: the walk reports it failed and creates nothing.       */
        pszChild = BFU_JoinImage(pszImagePath, ppszNames[i]);

        if (pszChild == NULL)
        {   /* Out of memory for its path: no clash to raise from it. */
            continue;
        }

        rc = BFX_FillEntry(pszChild, ppszNames[i], &child);

        if (rc != 0 || (child.uiType != BFE_TYPE_FILE && child.uiType != BFE_TYPE_DIR))
        {   /* Unreadable, or a link or node: nothing lands on the host. */
            free(pszChild);
            continue;
        }

        /* The one question: which to keep. */
        iReply = BFC_Emit(pRun, BFE_EVENT_MANIFEST_CLASH, child.uiType, BFE_OK, pszChild, pwszManifest, 0, child.ullSize);
        free(pszChild);

        if (iReply == BFE_REPLY_KEEP_MANIFEST)
        {   /* The manifest is kept: every top entry of that name is left out. */
            pRun->bLeaveOutClash = true;
        }
        else if (iReply != BFE_REPLY_CANCEL)
        {   /* The data is kept (KEEP_FILE, or no answer that says otherwise):    */
            /* no manifest is written at all, so nothing contends for the name.   */
            pRun->optExtract.bWriteManifest = 0;
        }

        /* Asked once; a second entry of the name (another letter case) is   */
        /* covered by the same answer.  A CANCEL is already latched.         */
        return;
    }
}

/*----------------------------------------------------------------------------*/
/* BFE_Extract:                                                               */
/*                                                                            */
/* Copy one image entry -- a file, or a directory with everything under it -- */
/* out to the host.  pwszHostPath names the destination ITEM, so the caller   */
/* chooses the name it lands under.                                           */
/*                                                                            */
/* The manifest is opened here rather than in the walker, because where it    */
/* goes depends on what the top item is: a tree's inside its host folder, a   */
/* single file's as a one-block sidecar beside it.  Either is written under a */
/* temporary name and renamed into place at the end (BFM_OpenManifest,        */
/* BFM_Publish).  A tree whose top holds an entry named as the manifest is    */
/* asked about first (BFC_ManifestClash), before anything is created.         */
/*                                                                            */
/* A FAT or exFAT session extracts too -- the walk reaches it through the     */
/* filesystem-neutral calls and the FAT arms of BFX_FillEntry and             */
/* BFX_CollectNames -- but writes NO manifest, whatever the options say: FAT  */
/* has no modes, owners or links, so a manifest could only record the         */
/* made-up mode and owner a FAT entry is shown with, and an import would      */
/* then restore those as if they were real.  The data is all there is.  Its   */
/* names are still tested as an ext session's are: FAT forbids the same       */
/* characters Windows does, but a volume written on another system can hold   */
/* a name Windows reserves ("con.txt") or one ending in a dot.                */
/*                                                                            */
/* A squashfs session extracts as an ext2/3/4 one does, manifest included:    */
/* squashfs keeps real modes, owners and links, read through its arms of the  */
/* same calls (bfe_sqfs.c); its three times are its one modify time.          */
/*                                                                            */
/* With links followed, a top item that is itself a link is resolved first,   */
/* so its target decides the shape: a folder is extracted as a tree, a file   */
/* as one file whose sidecar records the file's facts.                        */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImagePath : the image entry to extract.                             */
/*     pwszHostPath : the host path to create for it.                         */
/*     pOptions     : manifest / time / follow-links options, or NULL for     */
/*                    none of them.                                           */
/*     pfnEvent     : the event callback, or NULL.                            */
/*     pvContext    : passed to pfnEvent unchanged.                           */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_CANCELLED (also when the clash question was      */
/*           answered so, with nothing created); BFE_ERR_PARTIAL (an item, or */
/*           the manifest, could not be written); BFE_ERR_NO_MEMORY when      */
/*           memory ran out at an item, which stopped the whole extract       */
/*           there (BFC_Emit); or, before anything was copied, BFE_ERR_ARG,   */
/*           BFE_ERR_NOT_OPEN, BFE_ERR_BUSY, BFE_ERR_NOT_FOUND (or another    */
/*           error the top item's inode read, or a tree's top folder read,    */
/*           maps to), BFE_ERR_NAME (the destination's name is one Windows    */
/*           cannot hold), BFE_ERR_IO_HOST or BFE_ERR_NO_MEMORY.              */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_Extract(const char *pszImagePath, const wchar_t *pwszHostPath,
                        const BFE_ExtractOptions *pOptions,
                        BFE_CopyCallback pfnEvent, void *pvContext)
{
    struct BFC_Run  run;
    BFE_Entry       entry;
    BFE_Entry       topTarget;
    const wchar_t  *pwszLeaf;
    const char     *pszSource;
    wchar_t        *pwszManifest;
    char          **ppszNames;
    char           *pszTopReal;
    bool            bNoMemory;
    char           *pszLeaf;
    size_t          i;
    int             nNames;
    bool            bLeafValid;
    int             iStatus;
    int             rc;

    if (!BFX_PathValid(pszImagePath) || pwszHostPath == NULL || pwszHostPath[0] == L'\0')
    {   /* A relative or missing image path, or no destination: refused   */
        /* before the gate, so no error text is recorded for it -- the    */
        /* text may only be written while the busy flag is held.          */
        return(BFE_ERR_ARG);
    }

    /* The gate: an image must be open -- of a kind the walk reads (a FAT   */
    /* or squashfs session through its own arms) -- and reading it needs    */
    /* no write access.                                                     */
    iStatus = BFX_EnterKinds(false, BFX_FSMASK_EXT | BFX_FSMASK_FAT | BFX_FSMASK_SQFS);

    if (iStatus != BFE_OK)
    {   /* Busy, or nothing open: the gate has said which, and holds   */
        /* nothing for this call to release.                           */
        return(iStatus);
    }

    /* The destination's own NAME must be one Windows can hold.  The walker    */
    /* tests every child's name before creating it, but the top item's name    */
    /* is the caller's, and Windows does not refuse a bad one -- it quietly    */
    /* does something else: "trail." is created as "trail", "a:b" as a         */
    /* stream on a file called "a", "con" as whatever the device layer makes   */
    /* of it.  So the last component of the host path is put through the       */
    /* same test here, and a name that fails it stops the extract before       */
    /* anything is created.                                                    */
    /* The leaf: whatever follows the last separator of either kind (the       */
    /* whole path when there is none).  A path ending in a separator has an    */
    /* empty leaf, which the naming test refuses.                              */
    pwszLeaf = pwszHostPath;

    for (i = 0; pwszHostPath[i] != L'\0'; i++)
    {   /* One character of the host path per iteration. */
        if (pwszHostPath[i] == L'\\' || pwszHostPath[i] == L'/')
        {   /* A separator: the leaf starts after the last one. */
            pwszLeaf = pwszHostPath + i + 1;
        }
    }

    /* The rules are written for UTF-8 names, so the leaf is converted;   */
    /* one that will not convert counts as invalid, while memory          */
    /* running out (bNoMemory) is refused as itself, below.               */
    bLeafValid = false;
    pszLeaf    = BFU_WideToUtf8(pwszLeaf, &bNoMemory);

    if (pszLeaf != NULL)
    {   /* The leaf has a UTF-8 form; apply the Windows naming rules to it. */
        bLeafValid = BFU_HostNameValid(pszLeaf);
        free(pszLeaf);
    }

    if (pszLeaf == NULL && bNoMemory)
    {   /* Out of memory converting the leaf: refused as that, never as a    */
        /* name Windows cannot hold.  Nothing has been created yet, so the   */
        /* flag is all there is to release.                                  */
        iStatus = BFX_Fail(BFE_ERR_NO_MEMORY, "out of memory converting the destination's name");
        BFX_Leave();
        return(iStatus);
    }

    if (!bLeafValid)
    {   /* Windows cannot hold an item of that name: refuse, and let the next call in. */

        /* Nothing has been created yet -- the test is placed before every   */
        /* host write for exactly that reason -- so the flag is all there    */
        /* is to release.                                                    */
        iStatus = BFX_Fail(BFE_ERR_NAME, "Windows cannot hold an item with the name of %s", pszImagePath);
        BFX_Leave();
        return(iStatus);
    }

    /* Set the run up: zero counters, the caller's callback and options. */
    memset(&run, 0, sizeof(run));
    run.pfnEvent       = pfnEvent;
    run.pvContext      = pvContext;
    run.bManifestEmpty = true;

    if (pOptions != NULL)
    {   /* The caller supplied options; otherwise both stay off (the         */
        /* memset above).  Copied, so the caller's struct need not outlive   */
        /* the call's start.                                                 */
        run.optExtract = *pOptions;
    }

    if (!BFX_FsTraits()->bUnixMeta)
    {   /* A kind with no Unix facts to record (FAT and exFAT): no manifest,   */
        /* whatever was asked (the header says why).  Turned off here,         */
        /* before the tree's shape is looked at, so neither the manifest's     */
        /* file nor its clash question is reached.  The time stamping option   */
        /* stays: a FAT file's time is real.                                   */
        run.optExtract.bWriteManifest = 0;
    }

    /* The top item's facts, which decide the shape of the whole extract. */
    rc = BFX_FillEntry(pszImagePath, BFU_LastComponent(pszImagePath), &entry);

    if (rc != 0)
    {   /* The source does not exist in the image (or its inode cannot be   */
        /* read): refuse with the mapped error, and let the next call in.   */
        iStatus = BFX_Fail(BFX_MapErrno(rc), "cannot extract %s: %s", pszImagePath, strerror(rc));
        BFX_Leave();
        return(iStatus);
    }

    /* Where the data is read from: the item itself -- or, when it is a      */
    /* link and links are followed, what it names, resolved now so that a    */
    /* link to a folder is extracted as the tree it leads to (its manifest   */
    /* inside the host folder) and a link to a file as one file (its         */
    /* sidecar recording the file's facts, not the link's).  A link that     */
    /* cannot be resolved is left as it is: the walk below meets it, and     */
    /* skips or reports it as it does any link (BFC_ExtractNode).  The       */
    /* real path is this call's to free, at every return from here on.       */
    pszSource  = pszImagePath;
    pszTopReal = NULL;
    if (entry.uiType == BFE_TYPE_SYMLINK && run.optExtract.bFollowLinks)
    {   /* A link at the top, to be followed. */
        if (Bfc_ResolveLink(pszImagePath, &entry, &pszTopReal, &topTarget) == 0)
        {   /* Resolved: the target's path and facts stand in for the    */
            /* link's from here on.  A failure leaves pszTopReal NULL.   */
            pszSource = pszTopReal;
            entry     = topTarget;
        }
    }

    if (entry.uiType == BFE_TYPE_DIR)
    {   /* A TREE: its manifest lives inside the host folder, so the folder   */
        /* is made first and the manifest opened before the walk begins.      */
        if (run.optExtract.bWriteManifest)
        {   /* STEP 1: the manifest's full path: the host folder and   */
            /* _ext4meta.txt.                                          */
            pwszManifest = BFU_JoinHost(pwszHostPath, BFC_MANIFEST_NAME);

            if (pwszManifest == NULL)
            {   /* Out of memory composing the manifest's path: refuse, with   */
                /* nothing created yet (the resolved path, if any, freed).     */
                free(pszTopReal);
                iStatus = BFX_Fail(BFE_ERR_NO_MEMORY, "out of memory");
                BFX_Leave();
                return(iStatus);
            }

            /* STEP 2: the names at the image folder's top, which both the     */
            /* clash question and the temporary name are settled against.      */
            /* A folder that cannot be read is refused here, before anything   */
            /* is made -- the walk could not have copied it either.            */
            rc = BFX_CollectNames(pszSource, &ppszNames, &nNames);

            if (rc != 0)
            {   /* The top folder cannot be read: refuse, freeing both paths. */
                free(pwszManifest);
                iStatus = BFX_Fail(BFX_MapErrno(rc), "cannot read %s: %s", pszSource, strerror(rc));
                free(pszTopReal);
                BFX_Leave();
                return(iStatus);
            }

            /* STEP 3: an entry there named as the manifest is asked about,   */
            /* once, before anything is created (BFC_ManifestClash).          */
            BFC_ManifestClash(&run, pszSource, pwszManifest, ppszNames, nNames);

            if (run.bCancelled)
            {   /* Cancelled at the question: nothing was made.  The run's   */
                /* result reports the cancel.                                */
                BFX_FreeNames(ppszNames, nNames);
                free(pwszManifest);
                free(pszTopReal);
                iStatus = BFC_RunResult(&run);
                BFX_Leave();
                return(iStatus);
            }

            if (run.optExtract.bWriteManifest)
            {   /* STEP 4: still wanted.  Make the folder (tolerating one        */
                /* already there; anything worse fails the open below, which     */
                /* is reported), then open the manifest under a temporary name   */
                /* free of the host's names and the image's top names -- the     */
                /* manifest already at the final name, if any, stays until the   */
                /* new one is renamed over it (BFM_Publish).                     */
                _wmkdir(pwszHostPath);
                iStatus = BFM_OpenManifest(&run, pwszManifest, ppszNames, nNames);
            }
            else
            {   /* The data was kept instead: no manifest, and its path is let go. */
                free(pwszManifest);
                iStatus = BFE_OK;
            }

            /* The names are done with either way. */
            BFX_FreeNames(ppszNames, nNames);

            if (iStatus != BFE_OK)
            {   /* The manifest cannot be created (the folder not writable, or     */
                /* no temporary name free): refuse before a single entry is        */
                /* copied.  BFM_OpenManifest has freed the path; only the flag     */
                /* is held.  The folder may have been made; it is left, an empty   */
                /* folder being harmless.  The resolved path, if any, is freed.    */
                free(pszTopReal);
                iStatus = BFX_Fail(iStatus, "cannot create the manifest in the destination folder");
                BFX_Leave();
                return(iStatus);
            }

            if (run.pManifest != NULL)
            {   /* The mark line first.  It tells an import this file IS a    */
                /* manifest, not data that happens to share the name, and a   */
                /* person who opens it what it is; BFM_Load skips it as a     */
                /* comment once it has checked it.                            */
                fprintf(run.pManifest, "%s\n", BFC_MANIFEST_MARK);
            }
        }

        /* The walk, from the top folder (a followed link's target, when the   */
        /* top item was one), whose manifest path is ".".                      */
        BFC_ExtractNode(&run, pszSource, pwszHostPath, BFC_ROOT_RELATIVE, &entry);
    }
    else
    {   /* ONE ITEM: copy it with no manifest open, then write its sidecar. */

        /* The copy (or the record-only skip); with no manifest open, the   */
        /* walker's block writes do nothing.  Read from pszSource (a        */
        /* followed link's target file, when the item was one).             */
        BFC_ExtractNode(&run, pszSource, pwszHostPath, pszImagePath, &entry);

        /* The sidecar's condition: asked for, not cancelled, nothing failed,   */
        /* and either the file really landed (nDone counts its DONE -- a        */
        /* conflict answered "skip" leaves it 0, so it leaves no stray          */
        /* sidecar) or the item is a kind that is recorded instead of           */
        /* copied.  Written AFTER the copy, so that is known.                   */
        if (run.optExtract.bWriteManifest && !run.bCancelled && run.nErrors == 0 &&
            (run.nDone > 0 || entry.uiType != BFE_TYPE_FILE))
        {   /* The file was extracted, or the item is one that is recorded   */
            /* rather than copied: write "<hostfile>.ext4meta", under a      */
            /* temporary name until it is whole, as a tree's manifest is.    */
            pwszManifest = BFU_AppendHost(pwszHostPath, BFC_SIDECAR_SUFFIX);
            iStatus      = BFE_ERR_NO_MEMORY;

            if (pwszManifest != NULL)
            {   /* Open the sidecar; BFM_OpenManifest takes the path either    */
                /* way.  No image names to avoid: nothing else is extracted.   */
                iStatus = BFM_OpenManifest(&run, pwszManifest, NULL, 0);
            }

            if (iStatus != BFE_OK)
            {   /* The sidecar could not be created: the data is out, but the     */
                /* metadata the caller asked for is not, so the run is partial.   */
                /* (No memory for its path lands here too, reported likewise      */
                /* as a host failure.)                                            */
                BFC_Emit(&run, BFE_EVENT_ERROR, entry.uiType, BFE_ERR_IO_HOST, pszImagePath, pwszHostPath, 0, 0);
            }
            else
            {   /* The sidecar's path is the in-image path.  An import does   */
                /* not look it up by that path: with a sidecar it takes the   */
                /* one block it holds, whatever its path says (BFE_Import).   */
                /* The facts are the entry's -- a followed link's target's,   */
                /* read from pszSource.                                       */
                BFM_WriteBlock(&run, pszImagePath, &entry, pszSource);
            }
        }
    }

    /* The resolved path of a followed top link is done with. */
    free(pszTopReal);

    /* The manifest or sidecar, if one is being written: closed, and renamed   */
    /* into place only when whole; one that is not is removed and reported     */
    /* (BFM_Publish).  Before the result, so a failure here counts in it.      */
    BFM_Publish(&run, pszImagePath);

    /* The run's outcome, recorded by BFC_RunResult before the gate opens. */
    iStatus = BFC_RunResult(&run);
    BFX_Leave();

    /* BFE_OK, BFE_ERR_PARTIAL, BFE_ERR_CANCELLED or BFE_ERR_NO_MEMORY. */
    return(iStatus);
}

/*----------------------------------------------------------------------------*/
/* BFC_FreeHostNames:                                                         */
/*                                                                            */
/* Free a list returned by BFC_CollectHostNames.                              */
/*                                                                            */
/* Arguments:                                                                 */
/*     ppwszNames : the array (may be NULL).                                  */
/*     nNames     : how many names it holds.                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     void : every name and the array itself are freed.                      */
/*----------------------------------------------------------------------------*/
static void BFC_FreeHostNames(wchar_t **ppwszNames, int nNames)
{
    int i;

    for (i = 0; i < nNames; i++)
    {   /* One collected name per iteration. */
        free(ppwszNames[i]);
    }

    /* Then the array itself (free of NULL is allowed: an empty folder). */
    free(ppwszNames);
}

/*----------------------------------------------------------------------------*/
/* BFC_CollectHostNames:                                                      */
/*                                                                            */
/* Read a host folder's entry names ("." and ".." excluded) into a malloc'd   */
/* array and close the folder -- the host-side twin of BFX_CollectNames, so   */
/* an import never holds a folder handle open across its recursion (a deep    */
/* tree would otherwise hold one open handle per level).                      */
/*                                                                            */
/* Arguments:                                                                 */
/*     pwszDir     : the host folder to read.                                 */
/*     pppwszNames : receives the array of malloc'd names (NULL when empty).  */
/*     pnNames     : receives the count.                                      */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK with every name; BFE_ERR_IO_HOST when the folder cannot   */
/*           be read, or its listing fails part-way (no partial list is       */
/*           handed back); BFE_ERR_NO_MEMORY.                                 */
/*----------------------------------------------------------------------------*/
static int BFC_CollectHostNames(const wchar_t *pwszDir, wchar_t ***pppwszNames, int *pnNames)
{
    WIN32_FIND_DATAW find;
    HANDLE           hFind;
    wchar_t         *pwszPattern;
    wchar_t        **ppwszList;
    wchar_t        **ppwszGrown;
    wchar_t         *pwszName;
    size_t           cchDir;
    size_t           cchName;
    DWORD            dwError;
    int              nCapacity;
    int              nUsed;
    bool             bFirst;

    /* No names until the folder is read. */
    *pppwszNames = NULL;
    *pnNames     = 0;

    /* THE FOLDER IS READ WITH THE SYSTEM'S OWN FindFirstFileW, not with the    */
    /* C library's _wopendir.  The C library's version first turns the path     */
    /* into a full path in a buffer of 260 characters; given a longer path      */
    /* (which Windows itself accepts in the "\\?\" form) that step fails        */
    /* QUIETLY and the folder that gets read is the process's CURRENT           */
    /* DIRECTORY -- found by test: an import from a long path listed the        */
    /* wrong folder's names.  The pattern is the folder, a backslash and "*".   */
    cchDir      = wcslen(pwszDir);
    pwszPattern = (wchar_t *)malloc((cchDir + 3) * sizeof(wchar_t));

    if (pwszPattern == NULL)
    {   /* Out of memory for the search pattern: nothing is held yet, and the   */
        /* code is bare -- the caller reports it against the folder.            */
        return(BFE_ERR_NO_MEMORY);
    }

    /* The folder, then "\*" and its terminator: the three extra characters. */
    memcpy(pwszPattern, pwszDir, cchDir * sizeof(wchar_t));
    pwszPattern[cchDir]     = L'\\';
    pwszPattern[cchDir + 1] = L'*';
    pwszPattern[cchDir + 2] = L'\0';

    /* Open the search, which also hands over the first entry; the pattern   */
    /* is not needed after.                                                  */
    hFind = FindFirstFileW(pwszPattern, &find);
    free(pwszPattern);

    if (hFind == INVALID_HANDLE_VALUE)
    {   /* The folder cannot be opened.  (A folder that opens always has at   */
        /* least its "." entry, so "nothing found" is a failure too.)         */
        return(BFE_ERR_IO_HOST);
    }

    /* An empty list, grown on the first name; the first entry is in hand. */
    ppwszList = NULL;
    nCapacity = 0;
    nUsed     = 0;
    bFirst    = true;

    for (;;)
    {   /* One host folder entry per iteration: the one FindFirstFileW gave,   */
        /* then each that FindNextFileW gives.                                 */
        if (bFirst)
        {   /* The first entry is already in hand. */
            bFirst = false;
        }
        else if (!FindNextFileW(hFind, &find))
        {   /* No further entry.  Windows' reason is taken AT ONCE, before    */
            /* any other call can replace it: only ERROR_NO_MORE_FILES is     */
            /* the folder's end.  Any other failure part-way through means    */
            /* the listing is short, and an import of a short listing would   */
            /* report success for a copy that left names out -- so the        */
            /* names gathered are freed, the search ended, and the folder     */
            /* reported unreadable, as a folder that cannot be opened is.     */
            /* FindNextFileW's answer is a Windows BOOL, tested in its own    */
            /* form, on purpose -- not as the engine's bool.                  */
            dwError = GetLastError();

            if (dwError != ERROR_NO_MORE_FILES)
            {   /* A real failure, not the end: nothing is handed back. */
                BFC_FreeHostNames(ppwszList, nUsed);
                FindClose(hFind);
                return(BFE_ERR_IO_HOST);
            }

            /* The end of the folder: every entry was listed. */
            break;
        }

        if (wcscmp(find.cFileName, L".") == 0 || wcscmp(find.cFileName, L"..") == 0)
        {   /* The self and parent links are not children. */
            continue;
        }

        if (nUsed == nCapacity)
        {   /* The array is full: double it (128 to begin with).  Doubling, as   */
            /* BFM_Load does, so a folder of N names costs about log2(N)         */
            /* reallocs rather than N; why 128 here and 64 there is not          */
            /* recorded.                                                         */
            if (nCapacity == 0)
            {   /* First allocation. */
                nCapacity = 128;
            }
            else
            {   /* Every later growth doubles. */
                nCapacity = nCapacity * 2;
            }

            ppwszGrown = (wchar_t **)realloc(ppwszList, (size_t)nCapacity * sizeof(wchar_t *));

            if (ppwszGrown == NULL)
            {   /* Out of memory growing the list: the old list is still valid,   */
                /* so free it, end the search, and fail.                          */
                BFC_FreeHostNames(ppwszList, nUsed);
                FindClose(hFind);
                return(BFE_ERR_NO_MEMORY);
            }

            /* The list at its new size. */
            ppwszList = ppwszGrown;
        }

        /* A copy of the name, since find is overwritten by the next entry. */
        cchName  = wcslen(find.cFileName);
        pwszName = (wchar_t *)malloc((cchName + 1) * sizeof(wchar_t));

        if (pwszName == NULL)
        {   /* Out of memory for this name: free the list, end the search, fail. */
            BFC_FreeHostNames(ppwszList, nUsed);
            FindClose(hFind);
            return(BFE_ERR_NO_MEMORY);
        }

        /* The name with its terminator -- hence cchName + 1 characters. */
        memcpy(pwszName, find.cFileName, (cchName + 1) * sizeof(wchar_t));

        /* Into the list. */
        ppwszList[nUsed] = pwszName;
        nUsed++;
    }

    /* The search is finished: close it before handing the names over. */
    FindClose(hFind);

    /* Hand the list and its count over; the list is NULL when the folder   */
    /* held nothing but "." and "..", which BFC_FreeHostNames accepts.      */
    *pppwszNames = ppwszList;
    *pnNames     = nUsed;

    /* The folder is closed and its names are the caller's to free. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFC_ApplyMeta:                                                             */
/*                                                                            */
/* Give a freshly imported file or directory its Unix metadata.  A manifest   */
/* record is used only when it is for the SAME KIND of entry -- a "dir"       */
/* record never dresses a file that now has its name -- and each field it     */
/* lacks falls back to the import default individually.                       */
/*                                                                            */
/* A record is matched by NAME, so it may describe a file that has been       */
/* edited, or replaced by another of the same name, since it was extracted.   */
/* The mode, owner and group are applied anyway -- they are what the entry    */
/* was, and an edit does not change them -- but a FILE record's TIMES only    */
/* while the host file still has the size and modify time the record holds    */
/* (each compared when the record has it): setting an edited file's modify    */
/* time back to before the edit would hide the edit.  A changed file gets the */
/* host's modify time instead, as with no record.  A directory's recorded     */
/* times are always applied.                                                  */
/*                                                                            */
/* Arguments:                                                                 */
/*     pRun        : the running import (options and loaded manifest).        */
/*     pszImage    : the imported item's image path.                          */
/*     pszRel      : its manifest path.                                       */
/*     uiType      : BFE_TYPE_FILE or BFE_TYPE_DIR.                           */
/*     uiHostMtime : the host item's modify time, epoch seconds.              */
/*     ullHostSize : the host file's size in bytes (not used for a folder).   */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0 when every attribute was applied, otherwise the errno-style    */
/*           result (bfe_fs.c's, either library's) of the first that failed.  */
/*----------------------------------------------------------------------------*/
static int BFC_ApplyMeta(struct BFC_Run *pRun, const char *pszImage, const char *pszRel,
                         uint32_t uiType, uint32_t uiHostMtime, uint64_t ullHostSize)
{
    const struct BFM_Record *pRecord;
    uint32_t                 uiMode;
    uint32_t                 uiUid;
    uint32_t                 uiGid;
    uint32_t                 uiMtime;
    uint32_t                 uiAtime;
    uint32_t                 uiNow;          // the moment of this import, epoch seconds: the change time, and any time no source supplies
    bool                     bSetMtime;
    bool                     bSetAtime;
    bool                     bTimesHold;
    int                      rc;

    /* STEP 1: the defaults -- what an item with no manifest record gets. */
    if (uiType == BFE_TYPE_DIR)
    {   /* A directory takes the directory mode -- 0755 by default, whose   */
        /* execute bits keep it traversable; a caller's own value is used   */
        /* as given.                                                        */
        uiMode = pRun->optImport.uiDirMode;
    }
    else
    {   /* A file takes the file mode. */
        uiMode = pRun->optImport.uiFileMode;
    }

    /* The caller's owner and group, and the host's modify time -- used      */
    /* only when asked for (bCopyMtime); a time no source supplies becomes   */
    /* the moment of the import in STEP 3.                                   */
    uiUid     = pRun->optImport.uiUid;
    uiGid     = pRun->optImport.uiGid;
    uiMtime   = uiHostMtime;
    uiAtime   = 0;
    bSetMtime = false;
    bSetAtime = false;

    if (pRun->optImport.bCopyMtime)
    {   /* The caller wants the host file's modify time carried over. */
        bSetMtime = true;
    }

    /* STEP 2: a manifest record of the same kind overrides, field by field. */
    pRecord = BFM_Find(pRun, pszRel);

    if (pRecord != NULL && pRecord->uiType == uiType)
    {   /* A matching record: take each fact it actually carries. */
        if (pRecord->bHasMode)
        {   /* The recorded permission bits. */
            uiMode = pRecord->uiMode;
        }

        if (pRecord->bHasUid)
        {   /* The recorded owner. */
            uiUid = pRecord->uiUid;
        }

        if (pRecord->bHasGid)
        {   /* The recorded group. */
            uiGid = pRecord->uiGid;
        }

        /* Whether the record's times still describe this item: always for    */
        /* a directory -- its host modify time moves with every file put in   */
        /* it, and its size is not comparable across the two filesystems --   */
        /* and for a file only while the size and modify time it recorded     */
        /* are still the host file's (a fact it lacks is not held against     */
        /* it).  An extract that did not stamp the files' times leaves them   */
        /* unequal to the record, so their times come from the host too;      */
        /* and a host filesystem keeping coarser times than a second (FAT     */
        /* keeps two) can make an untouched file read as changed.             */
        bTimesHold = true;

        if (uiType == BFE_TYPE_FILE)
        {   /* A file: compare what the record has with the host file now. */
            if (pRecord->bHasSize && pRecord->ullSize != ullHostSize)
            {   /* Its length changed since the extract. */
                bTimesHold = false;
            }

            if (pRecord->bHasMtime && pRecord->uiMtime != uiHostMtime)
            {   /* Its modify time is not the one the extract stamped. */
                bTimesHold = false;
            }
        }

        if (bTimesHold && pRecord->bHasMtime)
        {   /* The recorded modify time beats the host file's. */
            uiMtime   = pRecord->uiMtime;
            bSetMtime = true;
        }

        if (bTimesHold && pRecord->bHasAtime)
        {   /* The recorded access time; nothing else supplies one. */
            uiAtime   = pRecord->uiAtime;
            bSetAtime = true;
        }
    }

    /* STEP 3: apply.  The first failure is returned; the rest are skipped,   */
    /* since the item is then reported failed and its metadata is unsure      */
    /* anyway.  The five calls do not depend on one another's order; each     */
    /* writes the inode for itself.  BFS_SetMode (lwext4's ext4_chmod)        */
    /* keeps the inode's type bits, so only the 12 permission bits are        */
    /* passed.  lwext4 has no clock and leaves every time 0 (1970 on          */
    /* Linux), so a time no source                                            */
    /* supplies is the moment of this import -- and the inode-CHANGE time     */
    /* is always that moment: it records when this inode was made here,       */
    /* never an old time copied from the host or a manifest.                  */
    uiNow = (uint32_t)time(NULL);
    rc    = 0;

    if (BFX_FsTraits()->bUnixMeta)
    {   /* A kind with Unix facts (ext2/3/4): the mode, then the owner.  A   */
        /* FAT session holds neither, so a record's mode and owner have      */
        /* nowhere to go there and are passed over; only the modify time     */
        /* below is kept.                                                    */
        rc = BFS_SetMode(pszImage, uiMode & BFX_PERM_MASK);

        if (rc == 0)
        {   /* The mode went in; set the owner. */
            rc = BFS_SetOwner(pszImage, uiUid, uiGid);
        }
    }

    if (!bSetMtime)
    {   /* No modify time asked for or recorded: the moment of the import. */
        uiMtime = uiNow;
    }

    if (!bSetAtime)
    {   /* Only a manifest supplies an access time (the host's is not used):   */
        /* without one, the moment of the import.                              */
        uiAtime = uiNow;
    }

    if (rc == 0)
    {   /* The modify time. */
        rc = BFS_SetMtime(pszImage, uiMtime);
    }

    if (rc == 0)
    {   /* The access time. */
        rc = BFS_SetAtime(pszImage, uiAtime);
    }

    if (rc == 0)
    {   /* The inode-change time: always now. */
        rc = BFS_SetCtime(pszImage, uiNow);
    }

    if (rc == 0)
    {   /* And the directory the item was put in, as Linux updates it.  An   */
        /* imported folder's own times are set after its children's, so      */
        /* this never undoes the times it is given here.                     */
        rc = BFX_TouchParent(pszImage);
    }

    /* 0, or the first failure, errno-style. */
    return(rc);
}

/*----------------------------------------------------------------------------*/
/* BFC_ImportBytes:                                                           */
/*                                                                            */
/* Stream one host file's bytes into an image file, exactly, raising a        */
/* progress event per buffer.  The image file is created or truncated.  On    */
/* any failure or a cancel the partly written image file is REMOVED, for the  */
/* same reason an extract removes a partial host file.                        */
/*                                                                            */
/* Arguments:                                                                 */
/*     pRun     : the running import.                                         */
/*     pwszHost : the host file to read.                                      */
/*     pszImage : the image file to write.                                    */
/*     ullTotal : the host file's size, for the progress events.              */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK on a complete copy; BFE_ERR_IO_HOST for a read-side       */
/*           failure; BFE_ERR_NO_SPACE / BFE_ERR_IO_IMAGE and friends for a   */
/*           write-side one; BFE_ERR_NO_MEMORY; BFE_ERR_CANCELLED.            */
/*----------------------------------------------------------------------------*/
static int BFC_ImportBytes(struct BFC_Run *pRun, const wchar_t *pwszHost, const char *pszImage,
                           uint64_t ullTotal)
{
    BFS_File  *pFile;
    FILE      *pHost;
    char      *pBuffer;
    size_t     cbRead;
    size_t     cbWritten;
    uint64_t   ullDone;
    int        iStatus;
    int        rc;

    /* The streaming buffer, one per file copy.  The failures below return   */
    /* bare codes: the caller turns them into this item's ERROR event.       */
    pBuffer = (char *)malloc(BFX_IO_BUFFER);

    if (pBuffer == NULL)
    {   /* Out of memory for the copy buffer: nothing else is held yet and   */
        /* the image is untouched, so the bare code is all there is to do.   */
        return(BFE_ERR_NO_MEMORY);
    }

    /* The host file, binary (no CRLF translation), for reading -- opened   */
    /* BEFORE the image file, so a host file that will not open never       */
    /* costs an existing image file its old contents.                       */
    pHost = _wfopen(pwszHost, L"rb");

    if (pHost == NULL)
    {   /* The host file cannot be opened (in use, no permission).  Free the   */
        /* buffer -- the only thing held -- and fail; nothing in the image     */
        /* has been touched.                                                   */
        free(pBuffer);
        return(BFE_ERR_IO_HOST);
    }

    /* The image file, created -- or TRUNCATED, when the caller chose to   */
    /* overwrite: from here an existing file's old bytes are gone.         */
    rc = BFS_FileOpen(pszImage, "wb", &pFile);

    if (rc != 0)
    {   /* The image file cannot be created (no space, an I/O error; a   */
        /* missing parent folder was refused before the walk began).     */

        /* Give back what is held -- the host file, open only for reading,   */
        /* so its close loses nothing, and the buffer -- and fail with       */
        /* the layer's errno mapped.  No BFS_Remove: the open did not        */
        /* succeed, so there is no file of this copy's to remove.            */
        fclose(pHost);
        free(pBuffer);
        return(BFX_MapErrno(rc));
    }

    /* Nothing wrong and nothing copied yet. */
    iStatus = BFE_OK;
    ullDone = 0;

    /* Until the host file's end, a failure, or a cancel. */
    for (;;)
    {   /* One buffer of file data per iteration. */

        /* The next buffer from the host file.  fread gives fewer than a    */
        /* buffer only at the end or on an error, and 0 for both once       */
        /* nothing more comes -- which is why the test below asks ferror.   */
        cbRead = fread(pBuffer, 1, BFX_IO_BUFFER, pHost);

        if (cbRead == 0)
        {   /* End of the host file -- or a read error, which looks the same. */
            if (ferror(pHost))
            {   /* A genuine read error on the host. */
                iStatus = BFE_ERR_IO_HOST;
            }

            /* Either way the loop ends; iStatus says which it was. */
            break;
        }

        /* Into the image file, at its current end. */
        cbWritten = 0;
        rc        = BFS_FileWrite(pFile, pBuffer, cbRead, &cbWritten);

        if (rc != 0)
        {   /* The write was refused and said why -- ENOSPC for a full     */
            /* filesystem (on FAT also a file reaching 4 GiB, bfe_fs.c),   */
            /* mapped to BFE_ERR_NO_SPACE.  What it did write is part of   */
            /* the file, which the clean-up removes.                       */
            iStatus = BFX_MapErrno(rc);
            break;
        }

        if (cbWritten != cbRead)
        {   /* A short write with no error code.  Neither library gives one:   */
            /* lwext4 reports a full filesystem through rc above --            */
            /* ext4_fwrite returns the block append's ENOSPC after updating    */
            /* the size, and its source shows no path that returns EOK         */
            /* short -- and bfe_fs.c turns a short FatFs write into ENOSPC.    */
            /* So this is a guard, reported as "no space" as the likeliest     */
            /* reading.                                                        */
            iStatus = BFE_ERR_NO_SPACE;
            break;
        }

        /* One progress event per buffer; a cancel reply latches the flag. */
        ullDone += cbRead;
        BFC_Emit(pRun, BFE_EVENT_PROGRESS, BFE_TYPE_FILE, BFE_OK, pszImage, pwszHost, ullDone, ullTotal);

        if (pRun->bCancelled)
        {   /* The caller asked to stop in the middle of this file. */
            iStatus = BFE_ERR_CANCELLED;
            break;
        }
    }

    /* The image file's close, its result checked.  On ext2/3/4 that is a   */
    /* guard -- lwext4's ext4_fclose only clears the handle and returns     */
    /* EOK, every byte and the size having gone in with each ext4_fwrite    */
    /* -- but on FAT the close is where FatFs writes the file's directory   */
    /* entry (its size and time) back, which can fail.                      */
    rc = BFS_FileClose(pFile);

    if (rc != 0 && iStatus == BFE_OK)
    {   /* The close failed after an otherwise clean copy. */
        iStatus = BFX_MapErrno(rc);
    }

    /* The host file is only read, so its close cannot lose anything; then   */
    /* the buffer.                                                           */
    fclose(pHost);
    free(pBuffer);

    if (iStatus != BFE_OK)
    {   /* Do not leave a partial file in the image looking like a whole one.   */
        /* After an overwrite that failed, neither the old bytes nor the        */
        /* new ones remain.  The removal's own result is not looked at.         */
        BFS_Remove(pszImage);
    }

    /* BFE_OK, or the reason the copy stopped. */
    return(iStatus);
}

/*----------------------------------------------------------------------------*/
/* BFC_ImportNode:                                                            */
/*                                                                            */
/* Import one host item to its image path, and -- for a folder -- everything  */
/* beneath it.                                                                */
/*                                                                            */
/* A host name that has no UTF-8 form, or is longer than the open             */
/* filesystem's names (ext4's 255 bytes once converted; FAT's 255 UTF-16      */
/* units, which no Windows name passes), is SKIPPED.  At the top of a tree    */
/* imported with the                                                          */
/* manifest option, the manifest file itself is not imported: it describes    */
/* the tree, it is not part of it.  A file of that name without the mark      */
/* line is no manifest, and is imported like any other.                       */
/*                                                                            */
/* Arguments:                                                                 */
/*     pRun     : the running import.                                         */
/*     pwszHost : the host item.                                              */
/*     pszImage : the image path to create for it.                            */
/*     pszRel   : the item's manifest path.                                   */
/*                                                                            */
/* Returns:                                                                   */
/*     void : the outcome is in the events raised and in pRun's counters.     */
/*----------------------------------------------------------------------------*/
static void BFC_ImportNode(struct BFC_Run *pRun, const wchar_t *pwszHost, const char *pszImage,
                           const char *pszRel)
{
    BFE_Entry   existing;
    bool        bNoMemory;
    wchar_t   **ppwszNames;
    wchar_t    *pwszChildHost;
    char       *pszName;
    char       *pszChildImage;
    char       *pszChildRel;
    uint64_t    ullSize;
    uint32_t    uiMtime;
    int         nNames;
    int         iKind;
    int         iReply;
    int         iStatus;
    int         rc;
    int         i;

    if (pRun->bCancelled)
    {   /* The run was cancelled: do nothing more at any level. */
        return;
    }

    /* What the host item is, its size and its modify time (zero until       */
    /* BFC_HostKind fills them, so a vanished item leaves no stale value).   */
    ullSize = 0;
    uiMtime = 0;
    iKind   = BFC_HostKind(pwszHost, &ullSize, &uiMtime);

    if (iKind == BFC_HOST_NONE)
    {   /* The host item vanished or cannot be examined (no permission, or   */
        /* a Windows link whose target is gone): an ERROR, since the user    */
        /* asked for it -- it was listed a moment ago -- and it did not      */
        /* arrive.  Its type is unknown, so the event says so.               */
        BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_UNKNOWN, BFE_ERR_IO_HOST, pszImage, pwszHost, 0, 0);
        return;
    }

    if (iKind == BFC_HOST_OTHER)
    {   /* Not a file or folder: nothing an image entry can be made from.   */
        /* SKIPPED, not ERROR: nothing failed, the kind simply has no       */
        /* image form this engine makes, on ext2/3/4 or on FAT.             */
        BFC_Emit(pRun, BFE_EVENT_SKIPPED, BFE_TYPE_UNKNOWN, BFE_ERR_TYPE, pszImage, pwszHost, 0, 0);
        return;
    }

    /* What, if anything, already has the destination name in the image. */
    rc = BFX_FillEntry(pszImage, BFU_LastComponent(pszImage), &existing);

    /* rc is kept for both branches below: 0 means something has the name   */
    /* (its type in existing), ENOENT means the name is free.               */
    if (rc != 0 && rc != ENOENT)
    {   /* The destination could not even be looked up (a damaged parent    */
        /* directory or inode): nothing is created over an unknown state.   */
        BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_UNKNOWN, BFX_MapErrno(rc), pszImage, pwszHost, 0, 0);
        return;
    }

    if (iKind == BFC_HOST_DIR)
    {   /* A folder: make the image directory, import the children, then set   */
        /* the directory's own metadata.                                       */

        /* The folder's START; a cancel reply is seen by the child loop. */
        BFC_Emit(pRun, BFE_EVENT_START, BFE_TYPE_DIR, BFE_OK, pszImage, pwszHost, 0, 0);

        if (rc == 0 && existing.uiType != BFE_TYPE_DIR)
        {   /* A file or link holds the name; a directory cannot replace it.   */
            /* An ERROR, not a CONFLICT, as for a file meeting a directory     */
            /* below: no answer could make the entry in the way go, since      */
            /* nothing is deleted to make room for a folder.  Nothing is       */
            /* created, and none of the folder's children are tried.           */
            BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_DIR, BFE_ERR_EXISTS, pszImage, pwszHost, 0, 0);
            return;
        }

        if (rc == ENOENT)
        {   /* Not there yet: create it.  (An existing directory is merged.) */
            rc = BFS_DirMake(pszImage);

            if (rc == 0)
            {   /* Created: settle its checksum now, so a directory that ends   */
                /* up empty is still clean.  The mode and time given here are   */
                /* provisional; BFC_ApplyMeta sets the final ones below.        */
                rc = BFX_NewDirFinish(pszImage, pRun->optImport.uiDirMode, uiMtime);
            }

            if (rc != 0)
            {   /* No space, or an I/O error (a missing parent folder was      */
                /* refused before the walk began).  If the mkdir itself        */
                /* succeeded and only the finish failed, the directory stays   */
                /* in the image, reported failed; none of the children are     */
                /* tried either way.                                           */
                BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_DIR, BFX_MapErrno(rc), pszImage, pwszHost, 0, 0);
                return;
            }
        }

        /* The folder's names, all read and the folder closed before any is   */
        /* imported; a listing that fails part-way is a failure here, never   */
        /* a short list.                                                      */
        iStatus = BFC_CollectHostNames(pwszHost, &ppwszNames, &nNames);

        if (iStatus != BFE_OK)
        {   /* The host folder cannot be read.  The image directory exists    */
            /* (made above, or already there) with the provisional mode and   */
            /* time; it is reported failed and left, empty of this import.    */
            BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_DIR, iStatus, pszImage, pwszHost, 0, 0);
            return;
        }

        for (i = 0; i < nNames && !pRun->bCancelled; i++)
        {   /* One child of this folder per iteration. */

            /* Only at the top of the tree, and only when the file there was   */
            /* taken as the tree's manifest (BFE_Import: the option on, and    */
            /* its first line the mark).  Without the option, or without the   */
            /* mark, _ext4meta.txt is ordinary data and is imported.  Letter   */
            /* case aside, as Windows opened it under that name for loading.   */
            if (pRun->bTreeManifest && strcmp(pszRel, BFC_ROOT_RELATIVE) == 0 &&
                _wcsicmp(ppwszNames[i], BFC_MANIFEST_NAME) == 0)
            {   /* The tree's own manifest: it describes the tree, it is not   */
                /* part of it.                                                 */
                continue;
            }

            /* The child's host path. */
            pwszChildHost = BFU_JoinHost(pwszHost, ppwszNames[i]);

            if (pwszChildHost == NULL)
            {   /* Out of memory composing the child's host path: reported   */
                /* against the folder.  The ERROR stops the whole run        */
                /* (BFC_Emit), so the loop ends at its test.                 */
                BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_UNKNOWN, BFE_ERR_NO_MEMORY, pszImage, pwszHost, 0, 0);
                continue;
            }

            /* The name must have a UTF-8 form, and fit the open filesystem's   */
            /* names: ext4's are at most BFX_NAME_MAX (255) bytes of UTF-8,     */
            /* FAT's and exFAT's at most BFX_FAT_NAME_UNITS (255) UTF-16        */
            /* units -- Windows' own limit, so on FAT every Windows name fits   */
            /* and that test is a guard.  The conversion says apart             */
            /* (bNoMemory) an unpaired surrogate from memory running out.       */
            pszName = BFU_WideToUtf8(ppwszNames[i], &bNoMemory);

            if (pszName == NULL && bNoMemory)
            {   /* Out of memory converting the name: an ERROR, never a     */
                /* skip, reported against the folder's image path, as the   */
                /* child has none yet.  The ERROR stops the whole run at    */
                /* this item (BFC_Emit), so the loop ends at its test.      */
                BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_UNKNOWN, BFE_ERR_NO_MEMORY, pszImage, pwszChildHost, 0, 0);
                free(pwszChildHost);
                continue;
            }

            if (pszName == NULL ||
                (!BFX_FsTraits()->bFatNames && strlen(pszName) > BFX_NAME_MAX) ||
                (BFX_FsTraits()->bFatNames && wcslen(ppwszNames[i]) > BFX_FAT_NAME_UNITS))
            {   /* An unpaired surrogate, or longer than the filesystem holds:   */
                /* it has no name for it.  SKIPPED, not ERROR -- the item is     */
                /* left on the host, nothing failed -- reported against the      */
                /* folder's image path, since the child has none.  Its two       */
                /* strings are freed and the loop goes on.                       */
                BFC_Emit(pRun, BFE_EVENT_SKIPPED, BFE_TYPE_UNKNOWN, BFE_ERR_NAME, pszImage, pwszChildHost, 0, 0);
                free(pszName);
                free(pwszChildHost);
                continue;
            }

            /* The child's image path and manifest path. */
            pszChildImage = BFU_JoinImage(pszImage, pszName);
            pszChildRel   = BFU_JoinRelative(pszRel, pszName);

            if (pszChildImage == NULL || pszChildRel == NULL)
            {   /* Out of memory composing the child's image paths. */
                BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_UNKNOWN, BFE_ERR_NO_MEMORY, pszImage, pwszChildHost, 0, 0);
            }
            else
            {   /* Import the child (recursing when it is a folder). */
                BFC_ImportNode(pRun, pwszChildHost, pszChildImage, pszChildRel);
            }

            /* This child's four strings (free of NULL is allowed). */
            free(pszChildImage);
            free(pszChildRel);
            free(pszName);
            free(pwszChildHost);
        }

        /* The name list is done with. */
        BFC_FreeHostNames(ppwszNames, nNames);

        if (pRun->bCancelled)
        {   /* Cancelled part-way through this folder: its metadata is left   */
            /* as created (the provisional mode and time).                    */
            return;
        }

        /* The folder's own metadata, now that its children are in -- last,   */
        /* so nothing done to its children can disturb the times just set.    */
        rc = BFC_ApplyMeta(pRun, pszImage, pszRel, BFE_TYPE_DIR, uiMtime, 0);

        if (rc != 0)
        {   /* The directory is there but its metadata could not be set: an   */
            /* ERROR for the folder (no DONE), though its children are in.    */
            BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_DIR, BFX_MapErrno(rc), pszImage, pwszHost, 0, 0);
            return;
        }

        /* The folder is done (its children reported for themselves). */
        BFC_Emit(pRun, BFE_EVENT_DONE, BFE_TYPE_DIR, BFE_OK, pszImage, pwszHost, 0, 0);
        return;
    }

    /* A regular host file. */
    if (ullSize > BFX_FsMaxFileBytes())
    {   /* Larger than the volume can hold one file (on FAT12 / 16 / 32,      */
        /* 4 GiB less one byte; ext2/3/4 sets no such limit, so never         */
        /* there -- BFX_FsMaxFileBytes): refused BEFORE anything is           */
        /* touched, ahead of the conflict question, so an existing entry is   */
        /* not removed for a file that could never take its place.            */
        /* NO_SPACE: the volume has no room for a file of that size.          */
        BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_FILE, BFE_ERR_NO_SPACE, pszImage, pwszHost, 0, ullSize);
        return;
    }

    if (rc == 0 && existing.uiType == BFE_TYPE_DIR)
    {   /* A directory holds the name; a file cannot replace it.  An ERROR,   */
        /* not a CONFLICT: no answer could make a directory go away here.     */
        BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_FILE, BFE_ERR_EXISTS, pszImage, pwszHost, 0, ullSize);
        return;
    }

    if (rc == 0)
    {   /* A file, link or node already has the name: the caller decides. */
        iReply = BFC_Emit(pRun, BFE_EVENT_CONFLICT, BFE_TYPE_FILE, BFE_OK, pszImage, pwszHost, 0, ullSize);

        if (iReply != BFE_REPLY_OVERWRITE)
        {   /* Skip (or cancel, already latched): leave the image entry.  No   */
            /* SKIPPED event -- the caller chose it, as on an extract.         */
            return;
        }

        /* Overwrite means REPLACE: remove the old entry first, so a symlink   */
        /* or a hard-linked file is replaced rather than written through.      */
        rc = BFS_Remove(pszImage);

        if (rc != 0)
        {   /* The old entry could not be removed: it stays as it was, and   */
            /* nothing is written -- the item is reported failed.            */
            BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_FILE, BFX_MapErrno(rc), pszImage, pwszHost, 0, ullSize);
            return;
        }
    }

    /* The file's START, which the caller may answer with cancel. */
    BFC_Emit(pRun, BFE_EVENT_START, BFE_TYPE_FILE, BFE_OK, pszImage, pwszHost, 0, ullSize);

    if (pRun->bCancelled)
    {   /* Cancelled on the START event, before anything was written.  (An   */
        /* entry removed for an overwrite above stays removed.)              */
        return;
    }

    /* The bytes; a failure or cancel removes the partial image file. */
    iStatus = BFC_ImportBytes(pRun, pwszHost, pszImage, ullSize);

    if (iStatus == BFE_ERR_CANCELLED)
    {   /* Cancelled mid-file; the partial image file is already removed.   */
        /* No ERROR: the cancel is reported once, by the run's result.      */
        return;
    }

    if (iStatus != BFE_OK)
    {   /* The copy failed: the partial file is already removed, so the     */
        /* image holds nothing at that name (after an overwrite, not even   */
        /* the old entry).                                                  */
        BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_FILE, iStatus, pszImage, pwszHost, 0, ullSize);
        return;
    }

    /* The file's metadata: the manifest's, or the import defaults. */
    rc = BFC_ApplyMeta(pRun, pszImage, pszRel, BFE_TYPE_FILE, uiMtime, ullSize);

    if (rc != 0)
    {   /* The bytes are in but the metadata could not be set: the file   */
        /* stays (its data is whole) and the item is reported failed.     */
        BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_FILE, BFX_MapErrno(rc), pszImage, pwszHost, 0, ullSize);
        return;
    }

    /* The file is in, with its metadata. */
    BFC_Emit(pRun, BFE_EVENT_DONE, BFE_TYPE_FILE, BFE_OK, pszImage, pwszHost, ullSize, ullSize);
}

/*----------------------------------------------------------------------------*/
/* BFC_RetimeParent:                                                          */
/*                                                                            */
/* Put a folder's RECORDED access and modify times back after a link was      */
/* restored into it.  The links come back after the walk has already given    */
/* every folder its recorded times, and adding a name moves a folder's times  */
/* to the present (BFX_TouchParent, as on Linux) -- so without this an        */
/* extracted tree would come back with the folders holding links stamped      */
/* with the import's time instead of their own.  A folder with no record, or  */
/* one that recorded no modify time, keeps the present.                       */
/*                                                                            */
/* Arguments:                                                                 */
/*     pRun         : the running import (holds the loaded manifest).         */
/*     pszImageRoot : the image path the tree was imported to.                */
/*     pszRel       : the restored link's manifest path.                      */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0, ENOMEM, or the errno-style result for the first time not set. */
/*----------------------------------------------------------------------------*/
static int BFC_RetimeParent(struct BFC_Run *pRun, const char *pszImageRoot, const char *pszRel)
{
    const struct BFM_Record *pParent;
    char                    *pszParentRel;
    char                    *pszParentImage;
    char                    *pszSlash;
    int                      rc;

    /* The folder's manifest path: the link's, cut at its last '/' -- or   */
    /* "." for a link at the tree's top.                                   */
    pszParentRel = (char *)malloc(strlen(pszRel) + 2);

    if (pszParentRel == NULL)
    {   /* Out of memory for the folder's path. */
        return(ENOMEM);
    }

    /* The link's path copied in (the buffer's two spare bytes leave room   */
    /* for "." even when that path is shorter), and its last '/' found.     */
    strcpy(pszParentRel, pszRel);
    pszSlash = strrchr(pszParentRel, '/');

    if (pszSlash != NULL)
    {   /* A deeper link: its folder is what comes before the '/'. */
        *pszSlash = '\0';
    }
    else
    {   /* A link at the top: its folder is the tree itself. */
        strcpy(pszParentRel, BFC_ROOT_RELATIVE);
    }

    /* The folder's record; nothing to put back without a directory record   */
    /* that holds a modify time.                                             */
    pParent = BFM_Find(pRun, pszParentRel);

    if (pParent == NULL || pParent->uiType != BFE_TYPE_DIR || !pParent->bHasMtime)
    {   /* No recorded time: the present the touch gave it stays. */
        free(pszParentRel);
        return(0);
    }

    /* The folder's image path: the root itself, or the root joined to it. */
    if (strcmp(pszParentRel, BFC_ROOT_RELATIVE) == 0)
    {   /* The tree's own top folder: a copy of the root's path. */
        pszParentImage = (char *)malloc(strlen(pszImageRoot) + 1);

        if (pszParentImage != NULL)
        {   /* Copied whole, its terminator included. */
            strcpy(pszParentImage, pszImageRoot);
        }
    }
    else
    {   /* A folder inside it. */
        pszParentImage = BFU_JoinImage(pszImageRoot, pszParentRel);
    }

    /* The manifest path has served both lookups and is freed now, before   */
    /* the test below, so neither of its exits has to free it.              */
    free(pszParentRel);

    if (pszParentImage == NULL)
    {   /* Out of memory for the folder's image path. */
        return(ENOMEM);
    }

    /* The recorded modify time, then the recorded access time if any; its   */
    /* change time stays the present, as for every imported inode.           */
    rc = BFS_SetMtime(pszParentImage, pParent->uiMtime);

    if (rc == 0 && pParent->bHasAtime)
    {   /* An access time was recorded too. */
        rc = BFS_SetAtime(pszParentImage, pParent->uiAtime);
    }

    /* The path is done with. */
    free(pszParentImage);

    /* 0, or the first failure. */
    return(rc);
}

/*----------------------------------------------------------------------------*/
/* BFC_RestoreLinks:                                                          */
/*                                                                            */
/* After a tree has been imported with its manifest, create the symbolic      */
/* links the manifest recorded -- the entries an extract could only describe, */
/* never copy.  This runs LAST, once every directory exists, so a link's      */
/* parent is always there.  A link whose name is already taken is left alone  */
/* (the import does not overwrite what it did not just create), and a         */
/* recorded device, fifo or socket is reported SKIPPED: this engine has no    */
/* way to create one.                                                         */
/*                                                                            */
/* Arguments:                                                                 */
/*     pRun         : the running import (holds the loaded manifest).         */
/*     pszImageRoot : the image path the tree was imported to.                */
/*                                                                            */
/* Returns:                                                                   */
/*     void : the outcome is in the events raised and in pRun's counters.     */
/*----------------------------------------------------------------------------*/
static void BFC_RestoreLinks(struct BFC_Run *pRun, const char *pszImageRoot)
{
    const struct BFM_Record *pRecord;
    BFE_Entry                existing;
    char                    *pszLink;
    uint32_t                 uiUid;
    uint32_t                 uiGid;
    uint32_t                 uiTime;
    int                      rc;
    int                      i;

    for (i = 0; i < pRun->nRecords && !pRun->bCancelled; i++)
    {   /* One manifest record per iteration (in path order, as loaded). */
        pRecord = &pRun->pRecords[i];

        if (pRecord->uiType == BFE_TYPE_FILE || pRecord->uiType == BFE_TYPE_DIR ||
            pRecord->uiType == BFE_TYPE_UNKNOWN)
        {   /* Files and directories were handled by the walk itself; a      */
            /* record with no (or an unknown) type says nothing to create,   */
            /* and is passed over without an event.                          */
            continue;
        }

        /* A manifest path is relative with '/' separators, so joining it to   */
        /* the root gives the entry's image path whatever its depth.  Every    */
        /* record's path was checked when the manifest was loaded              */
        /* (BFE_Import, BFM_RecordPathValid), so the join names a place        */
        /* inside the imported tree.                                           */
        pszLink = BFU_JoinImage(pszImageRoot, pRecord->pszPath);

        if (pszLink == NULL)
        {   /* Out of memory composing the path: reported against the root      */
            /* (the link has no path to name).  The ERROR stops the whole run   */
            /* (BFC_Emit), so the loop ends at its test.                        */
            BFC_Emit(pRun, BFE_EVENT_ERROR, pRecord->uiType, BFE_ERR_NO_MEMORY, pszImageRoot, NULL, 0, 0);
            continue;
        }

        if (pRecord->uiType != BFE_TYPE_SYMLINK || pRecord->pszTarget == NULL)
        {   /* A device, fifo or socket -- or a link whose target was never   */
            /* recorded: nothing can be created from it.                      */
            BFC_Emit(pRun, BFE_EVENT_SKIPPED, pRecord->uiType, BFE_ERR_TYPE, pszLink, NULL, 0, 0);
            free(pszLink);
            continue;
        }

        if (BFX_FillEntry(pszLink, BFU_LastComponent(pszLink), &existing) == 0)
        {   /* The name is already taken in the image; leave it as it is.   */
            /* No event: nothing was attempted, and nothing failed.         */
            free(pszLink);
            continue;
        }

        /* The link's START (a cancel reply is seen by the loop's test). */
        BFC_Emit(pRun, BFE_EVENT_START, BFE_TYPE_SYMLINK, BFE_OK, pszLink, NULL, 0, 0);

        /* Create it, pointing where the manifest says, the target used as   */
        /* written.  An extract that could not read a link's target writes   */
        /* no target line for it, so such a record never reaches here.       */
        rc = BFS_SymLink(pRecord->pszTarget, pszLink);

        if (rc == 0)
        {   /* Created: give it the recorded owner and time, falling back to   */
            /* the import's default owner and the present moment for a         */
            /* manifest that did not record them.                              */
            uiUid  = pRun->optImport.uiUid;
            uiGid  = pRun->optImport.uiGid;
            uiTime = (uint32_t)time(NULL);

            if (pRecord->bHasUid)
            {   /* The recorded owner. */
                uiUid = pRecord->uiUid;
            }

            if (pRecord->bHasGid)
            {   /* The recorded group. */
                uiGid = pRecord->uiGid;
            }

            if (pRecord->bHasMtime)
            {   /* The recorded modify time. */
                uiTime = pRecord->uiMtime;
            }

            /* The link's fixed mode, its owner and group, and both its      */
            /* times, in one step; its failure counts as the link's below.   */
            rc = BFX_NewLinkFinish(pszLink, uiUid, uiGid, uiTime);
        }

        if (rc == 0)
        {   /* Its folder's recorded times back, which adding the link moved. */
            rc = BFC_RetimeParent(pRun, pszImageRoot, pRecord->pszPath);
        }

        if (rc != 0)
        {   /* The link could not be created (its parent was not imported,   */
            /* no space, or a FAT session, which has no links: ENOTSUP) --   */
            /* or it was created and its owner and times could not be set,   */
            /* in which case it stays, reported failed.                      */
            BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_SYMLINK, BFX_MapErrno(rc), pszLink, NULL, 0, 0);
        }
        else
        {   /* The link is back. */
            BFC_Emit(pRun, BFE_EVENT_DONE, BFE_TYPE_SYMLINK, BFE_OK, pszLink, NULL, 0, 0);
        }

        /* The path is done with, either way. */
        free(pszLink);
    }
}

/*----------------------------------------------------------------------------*/
/* BFE_Import:                                                                */
/*                                                                            */
/* Copy one host item -- a file, or a folder with everything under it -- into */
/* the image.  pszImagePath names the destination ITEM, so the caller chooses */
/* the name it lands under.                                                   */
/*                                                                            */
/* With the manifest option, the manifest that BFE_Extract wrote for this     */
/* item is looked for in the place BFE_Extract put it -- _ext4meta.txt inside */
/* a folder, <file>.ext4meta beside a single file -- and, when found,         */
/* supplies mode, owner and times (a file's times only while it is unchanged  */
/* since its extract, BFC_ApplyMeta), and the tree's symlinks.  A folder's    */
/* _ext4meta.txt is its manifest only when its first line is the mark         */
/* (BFM_Load); without it the file is a user's own, imported as data, and the */
/* caller is told once (BFE_EVENT_NOT_MANIFEST).  A single file's             */
/* sidecar holds one block under the file's ORIGINAL image path; that block   */
/* is used whatever the path says, since the file may be going somewhere new. */
/* A tree's manifest must name only places inside the tree: a record whose    */
/* path is not "." or a relative path of plain names refuses the whole        */
/* import, before anything is copied (BFM_RecordPathValid).                   */
/*                                                                            */
/* Arguments:                                                                 */
/*     pwszHostPath : the host item to import.                                */
/*     pszImagePath : the image path to create for it.                        */
/*     pOptions     : metadata options, or NULL for the usual defaults        */
/*                    (0644 files, 0755 directories, root, host modify time,  */
/*                    no manifest).                                           */
/*     pfnEvent     : the event callback, or NULL.                            */
/*     pvContext    : passed to pfnEvent unchanged.                           */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_CANCELLED; BFE_ERR_PARTIAL; BFE_ERR_NO_MEMORY    */
/*           when memory ran out at an item, which stopped the whole import   */
/*           there (BFC_Emit); or, before anything was copied, BFE_ERR_ARG,   */
/*           BFE_ERR_NOT_OPEN, BFE_ERR_READ_ONLY, BFE_ERR_BUSY,               */
/*           BFE_ERR_IO_HOST, BFE_ERR_NO_MEMORY, or BFE_ERR_NOT_FOUND /       */
/*           BFE_ERR_NOT_DIR when the destination's folder is missing or not  */
/*           a directory.                                                     */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_Import(const wchar_t *pwszHostPath, const char *pszImagePath,
                       const BFE_ImportOptions *pOptions,
                       BFE_CopyCallback pfnEvent, void *pvContext)
{
    struct BFC_Run  run;
    wchar_t        *pwszManifest;
    const char     *pszRel;
    bool            bNotManifest;
    int             iKind;
    int             iStatus;
    int             rc;
    int             i;

    if (!BFX_PathValid(pszImagePath) || pwszHostPath == NULL || pwszHostPath[0] == L'\0')
    {   /* A relative or missing image path, or no source: refused before   */
        /* the gate, so no error text is recorded for it -- the text may    */
        /* only be written while the busy flag is held.                     */
        return(BFE_ERR_ARG);
    }

    /* The gate: an image must be open -- of a kind this function writes,   */
    /* a FAT one through bfe_fs.c's FAT arms -- and open for writing.       */
    iStatus = BFX_EnterKinds(true, BFX_FSMASK_EXT | BFX_FSMASK_FAT);

    if (iStatus != BFE_OK)
    {   /* Busy, nothing open, or read-only: BFX_EnterKinds has said which,   */
        /* and this call holds nothing to release.                            */
        return(iStatus);
    }

    /* Set the run up: zero counters, the caller's callback, the options. */
    memset(&run, 0, sizeof(run));
    run.pfnEvent  = pfnEvent;
    run.pvContext = pvContext;

    if (pOptions != NULL)
    {   /* The caller supplied options: copied, so the caller's struct need   */
        /* not outlive the call's start, and used exactly as given (modes     */
        /* included -- nothing here corrects a directory mode without its     */
        /* execute bits).                                                     */
        run.optImport = *pOptions;
    }
    else
    {   /* The usual defaults for something arriving from Windows: a file   */
        /* readable by all and writable by its owner, a directory also      */
        /* traversable, both owned by root, the host's modify time kept,    */
        /* and no manifest (NTFS holds none of these facts itself).         */
        run.optImport.uiFileMode   = 0644;
        run.optImport.uiDirMode    = 0755;
        run.optImport.uiUid        = 0;
        run.optImport.uiGid        = 0;
        run.optImport.bCopyMtime   = 1;
        run.optImport.bUseManifest = 0;
    }

    /* What the source is decides where its manifest would be.  (A device    */
    /* path, BFC_HOST_OTHER, is let through here and skipped by the walk.)   */
    iKind = BFC_HostKind(pwszHostPath, NULL, NULL);

    if (iKind == BFC_HOST_NONE)
    {   /* The source is not there: refuse, and let the next call in.       */
        /* Nothing is loaded or created yet, so the flag is all there is.   */
        iStatus = BFX_Fail(BFE_ERR_IO_HOST, "the source to import does not exist or cannot be read");
        BFX_Leave();
        return(iStatus);
    }

    /* The folder the copy is to go in must be there, and be a folder.  A   */
    /* copy never makes missing folders above its destination: lwext4's     */
    /* create path would make them (of its own default mode), FatFs would   */
    /* refuse, and a mistyped destination should fail, not grow a tree.     */
    /* The copied item itself -- a folder and all under it -- is made by    */
    /* the walk as always.                                                  */
    rc = BFX_CheckParent(pszImagePath);

    if (rc != 0)
    {   /* No folder to put it in, or it could not be looked at: refused   */
        /* with nothing loaded or copied, only the flag held.              */
        if (rc == ENOENT || rc == ENOTDIR)
        {   /* Missing, or not a folder: NOT_FOUND or NOT_DIR, said plainly. */
            iStatus = BFX_Fail(BFX_MapErrno(rc), "cannot import into %s: the folder it is to go in %s",
                               pszImagePath, (rc == ENOENT) ? "does not exist" : "is not a directory");
        }
        else
        {   /* The lookup itself failed: that failure, mapped. */
            iStatus = BFX_Fail(BFX_MapErrno(rc), "cannot import into %s: %s", pszImagePath, strerror(rc));
        }

        BFX_Leave();
        return(iStatus);
    }

    /* The manifest path of the top item: "." for a tree; for a single file    */
    /* it becomes the sidecar's one recorded path, set below when one loads.   */
    pszRel = BFC_ROOT_RELATIVE;

    if (run.optImport.bUseManifest)
    {   /* Look for the manifest where an extract would have put it. */
        if (iKind == BFC_HOST_DIR)
        {   /* A tree: _ext4meta.txt inside the folder. */
            pwszManifest = BFU_JoinHost(pwszHostPath, BFC_MANIFEST_NAME);
        }
        else
        {   /* A single file: "<file>.ext4meta" beside it. */
            pwszManifest = BFU_AppendHost(pwszHostPath, BFC_SIDECAR_SUFFIX);
        }

        if (pwszManifest == NULL)
        {   /* Out of memory composing the manifest's path: refuse before   */
            /* anything is imported; only the flag is held.                 */
            iStatus = BFX_Fail(BFE_ERR_NO_MEMORY, "out of memory");
            BFX_Leave();
            return(iStatus);
        }

        /* Load it (absent is fine: zero records).  A tree's must carry the   */
        /* mark line to count as a manifest at all; a sidecar is not asked.   */
        /* A failed load has freed what it built, so the refusal below has    */
        /* no records to free.                                                */
        bNotManifest = false;
        iStatus      = BFM_Load(pwszManifest, iKind == BFC_HOST_DIR, &run.pRecords, &run.nRecords, &bNotManifest);

        if (iStatus != BFE_OK)
        {   /* A manifest is there but could not be opened or read: stop,      */
            /* rather than import with defaults the caller asked not to get.   */
            /* BFM_Load records nothing, so its code is recorded here.         */
            free(pwszManifest);
            iStatus = BFX_Fail(iStatus, "the metadata manifest could not be read");
            BFX_Leave();
            return(iStatus);
        }

        if (iKind == BFC_HOST_DIR && bNotManifest)
        {   /* The folder's _ext4meta.txt is not a manifest -- no mark line, so   */
            /* a user's own file of that name.  It restores nothing and is        */
            /* imported as the data it is; the caller is told once, before        */
            /* anything is copied, and may cancel here (latched by BFC_Emit,      */
            /* which the walk then sees at its first test).                       */
            BFC_Emit(&run, BFE_EVENT_NOT_MANIFEST, BFE_TYPE_FILE, BFE_OK, pszImagePath, pwszManifest, 0, 0);
        }
        else if (iKind == BFC_HOST_DIR)
        {   /* A manifest with its mark, or none there at all: the walk leaves   */
            /* out the file of that name at the tree's top, which is the         */
            /* manifest whenever it exists.                                      */
            run.bTreeManifest = true;
        }

        /* The manifest's path is done with (after the event, which names it). */
        free(pwszManifest);

        if (iKind == BFC_HOST_DIR)
        {   /* A tree's manifest: every record must name a place inside the     */
            /* tree (BFM_RecordPathValid), checked here, before anything is     */
            /* copied, so one bad record refuses the whole import rather than   */
            /* being found after the walk has written the rest.  A single       */
            /* file's sidecar is not checked: its one path is the file's old    */
            /* image path, used only as a lookup key, never joined.             */
            for (i = 0; i < run.nRecords; i++)
            {   /* One record per iteration. */
                if (!BFM_RecordPathValid(run.pRecords[i].pszPath))
                {   /* A record outside the tree, or spelled as no extract       */
                    /* writes it: refused, naming it; the records are freed      */
                    /* and the flag is all that is held.  The text is recorded   */
                    /* first, while the record's path is still allocated.        */
                    iStatus = BFX_Fail(BFE_ERR_IO_HOST, "the metadata manifest records a path that is not inside the folder: \"%s\"",
                                       run.pRecords[i].pszPath);
                    BFM_Free(run.pRecords, run.nRecords);
                    BFX_Leave();
                    return(iStatus);
                }
            }
        }

        if (iKind != BFC_HOST_DIR && run.nRecords > 0)
        {   /* A sidecar's single block applies to this file whatever path it   */
            /* was extracted from.                                              */
            pszRel = run.pRecords[0].pszPath;
        }
    }

    /* The walk.  pszRel stays valid through it: it points into the loaded   */
    /* records, which are freed only after.                                  */
    BFC_ImportNode(&run, pwszHostPath, pszImagePath, pszRel);

    if (iKind == BFC_HOST_DIR && run.nRecords > 0)
    {   /* A tree with a manifest: put back the links it recorded -- after   */
        /* the walk, so every directory a link sits in already exists.  A    */
        /* cancel during the walk is honoured here too: its loop tests it.   */
        BFC_RestoreLinks(&run, pszImagePath);
    }

    /* The manifest is done with (free of none is allowed). */
    BFM_Free(run.pRecords, run.nRecords);

    /* The run's outcome, recorded by BFC_RunResult before the gate opens. */
    iStatus = BFC_RunResult(&run);
    BFX_Leave();

    /* BFE_OK, BFE_ERR_PARTIAL, BFE_ERR_CANCELLED or BFE_ERR_NO_MEMORY. */
    return(iStatus);
}

/*----------------------------------------------------------------------------*/
/* BFC_WithinBytes:                                                           */
/*                                                                            */
/* Stream one image file's bytes into another image file, exactly, raising a  */
/* progress event per buffer, both files through the filesystem-neutral layer */
/* (bfe_fs.c).  Both are open at once -- lwext4 and FatFs allow that -- and   */
/* the                                                                        */
/* destination is created or truncated.  On any failure or a cancel the       */
/* partly written destination is REMOVED, as an import removes a partial      */
/* file.                                                                      */
/*                                                                            */
/* Arguments:                                                                 */
/*     pRun     : the running copy.                                           */
/*     pszFrom  : the image file to read.                                     */
/*     pszTo    : the image file to write.                                    */
/*     ullTotal : the source's size, for the progress events.                 */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK on a complete copy; BFE_ERR_NO_SPACE / BFE_ERR_IO_IMAGE   */
/*           and friends for a failure on either side; BFE_ERR_NO_MEMORY;     */
/*           BFE_ERR_CANCELLED.                                               */
/*----------------------------------------------------------------------------*/
static int BFC_WithinBytes(struct BFC_Run *pRun, const char *pszFrom, const char *pszTo, uint64_t ullTotal)
{
    BFS_File  *pFileFrom;
    BFS_File  *pFileTo;
    char      *pBuffer;
    size_t     cbRead;
    size_t     cbWritten;
    uint64_t   ullDone;
    int        iStatus;
    int        rc;

    /* The streaming buffer, one per file copy.  As in the other two byte    */
    /* copies, the failures below return bare codes: the caller turns them   */
    /* into this item's ERROR event.                                         */
    pBuffer = (char *)malloc(BFX_IO_BUFFER);

    if (pBuffer == NULL)
    {   /* Out of memory for the copy buffer: nothing opened yet. */
        return(BFE_ERR_NO_MEMORY);
    }

    /* The source, for reading -- opened first, so a source that will not   */
    /* open never costs a destination being created.                        */
    rc = BFS_FileOpen(pszFrom, "rb", &pFileFrom);

    if (rc != 0)
    {   /* The source cannot be opened for reading (gone, or damaged).  Free   */
        /* the buffer -- the only thing held -- and fail with the layer's      */
        /* errno mapped; no destination has been created.                      */
        free(pBuffer);
        return(BFX_MapErrno(rc));
    }

    /* The destination, created.  An existing one was already removed by   */
    /* the walk after the caller chose to overwrite.                       */
    rc = BFS_FileOpen(pszTo, "wb", &pFileTo);

    if (rc != 0)
    {   /* The destination cannot be created (no space, an I/O error; a   */
        /* missing parent folder was refused before the walk began).      */

        /* Give back what is held -- the source, open only for reading, so   */
        /* its close loses nothing, and the buffer -- and fail.  No          */
        /* BFS_Remove: the open did not succeed, so there is no file of      */
        /* this copy's to remove.                                            */
        BFS_FileClose(pFileFrom);
        free(pBuffer);
        return(BFX_MapErrno(rc));
    }

    /* Nothing wrong and nothing copied yet. */
    iStatus = BFE_OK;
    ullDone = 0;

    /* Until the source's end, a failure, or a cancel. */
    for (;;)
    {   /* One buffer of file data per iteration. */

        /* The next buffer from the source, which the read stops at the     */
        /* file's size; cbRead is cleared first for the same reason as in   */
        /* BFC_ExtractBytes (a guard against ext4_fread's early returns).   */
        cbRead = 0;
        rc     = BFS_FileRead(pFileFrom, pBuffer, BFX_IO_BUFFER, &cbRead);

        if (rc != 0)
        {   /* The source could not be read: leave the loop with the reason;   */
            /* the one clean-up below the loop removes the partial copy.       */
            iStatus = BFX_MapErrno(rc);
            break;
        }

        if (cbRead == 0)
        {   /* End of file: the copy is complete. */
            break;
        }

        /* Into the destination, at its current end. */
        cbWritten = 0;
        rc        = BFS_FileWrite(pFileTo, pBuffer, cbRead, &cbWritten);

        if (rc != 0)
        {   /* The write was refused and said why -- ENOSPC for a full   */
            /* filesystem (or a FAT file at 4 GiB), mapped to            */
            /* BFE_ERR_NO_SPACE.                                         */
            iStatus = BFX_MapErrno(rc);
            break;
        }

        if (cbWritten != cbRead)
        {   /* A short write with no error code: a guard, as in           */
            /* BFC_ImportBytes -- neither library returns one (bfe_fs.c   */
            /* turns a short FatFs write into ENOSPC).                    */
            iStatus = BFE_ERR_NO_SPACE;
            break;
        }

        /* One progress event per buffer, naming the destination (there is   */
        /* no host path); a cancel reply latches the flag.                   */
        ullDone += cbRead;
        BFC_Emit(pRun, BFE_EVENT_PROGRESS, BFE_TYPE_FILE, BFE_OK, pszTo, NULL, ullDone, ullTotal);

        if (pRun->bCancelled)
        {   /* The caller asked to stop in the middle of this file. */
            iStatus = BFE_ERR_CANCELLED;
            break;
        }
    }

    /* Both closes; only the destination's result is kept -- a guard on    */
    /* ext2/3/4, where lwext4's close only clears the handle, but on FAT   */
    /* the close writes the copy's directory entry and can fail, as in     */
    /* BFC_ImportBytes.                                                    */
    BFS_FileClose(pFileFrom);
    rc = BFS_FileClose(pFileTo);

    if (rc != 0 && iStatus == BFE_OK)
    {   /* The close failed after an otherwise clean copy. */
        iStatus = BFX_MapErrno(rc);
    }

    /* The buffer is done with. */
    free(pBuffer);

    if (iStatus != BFE_OK)
    {   /* Do not leave a partial file in the image looking like a whole one.   */
        /* The removal's own result is not looked at.                           */
        BFS_Remove(pszTo);
    }

    /* BFE_OK, or the reason the copy stopped. */
    return(iStatus);
}

/*----------------------------------------------------------------------------*/
/* BFC_WithinMeta:                                                            */
/*                                                                            */
/* Give a freshly copied file or directory the metadata of the entry it was   */
/* copied from: permission bits, owner, group, modify time and access time.   */
/* Nothing crosses a boundary in a copy inside the image, so nothing is       */
/* defaulted -- the copy is to be the same as its source in everything but    */
/* its name, its inode and its inode-change time, which is the moment of the  */
/* copy, as Linux's cp -p leaves it: the copy's inode changed now.            */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszTo   : the copy's image path.                                       */
/*     pSource : the source entry's facts.                                    */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0 when every attribute was applied, otherwise the errno-style    */
/*           result (bfe_fs.c's, either library's) of the first that failed.  */
/*----------------------------------------------------------------------------*/
static int BFC_WithinMeta(const char *pszTo, const BFE_Entry *pSource)
{
    int rc;

    /* In order -- mode, owner, modify, access and change time --          */
    /* stopping at the first failure.  BFS_SetMode (lwext4's ext4_chmod)   */
    /* keeps the type bits, so only the 12 permission bits are passed.     */
    rc = 0;

    if (BFX_FsTraits()->bUnixMeta)
    {   /* A kind with Unix facts (ext2/3/4): the mode, then the owner.  A   */
        /* FAT session has neither (its entries' are made up), so they are   */
        /* passed over there.                                                */
        rc = BFS_SetMode(pszTo, pSource->uiMode & BFX_PERM_MASK);

        if (rc == 0)
        {   /* The mode went in; set the owner. */
            rc = BFS_SetOwner(pszTo, pSource->uiUid, pSource->uiGid);
        }
    }

    if (rc == 0)
    {   /* Set the modify time. */
        rc = BFS_SetMtime(pszTo, pSource->uiMtime);
    }

    if (rc == 0)
    {   /* Set the access time. */
        rc = BFS_SetAtime(pszTo, pSource->uiAtime);
    }

    if (rc == 0)
    {   /* And the inode-change time: the present, never the source's. */
        rc = BFS_SetCtime(pszTo, (uint32_t)time(NULL));
    }

    if (rc == 0)
    {   /* And the directory the copy was put in, as Linux updates it (a   */
        /* copied folder's own times are set after its children's).        */
        rc = BFX_TouchParent(pszTo);
    }

    /* 0, or the first failure, errno-style. */
    return(rc);
}

/*----------------------------------------------------------------------------*/
/* BFC_WithinNode:                                                            */
/*                                                                            */
/* Copy one image entry to another image path, and -- for a directory --      */
/* everything beneath it.  The rules are an import's, with the image on both  */
/* sides; a symbolic link is copied as a link, never followed.                */
/*                                                                            */
/* Two names of one hard-linked file are copied as two separate files.  The   */
/* events name the DESTINATION path; there is no host path.  A failure on one */
/* item is reported and the walk continues; only a cancel, or an item that    */
/* ran out of memory (BFC_Emit), stops it.                                    */
/*                                                                            */
/* Arguments:                                                                 */
/*     pRun    : the running copy.                                            */
/*     pszFrom : the source entry's image path.                               */
/*     pszTo   : the image path to create for the copy.                       */
/*     pSource : the source entry's facts, already read by the caller.        */
/*                                                                            */
/* Returns:                                                                   */
/*     void : the outcome is in the events raised and in pRun's counters.     */
/*----------------------------------------------------------------------------*/
static void BFC_WithinNode(struct BFC_Run *pRun, const char *pszFrom, const char *pszTo, const BFE_Entry *pSource)
{
    BFE_Entry    existing;
    BFE_Entry    child;
    char       **ppszNames;
    char        *pszChildFrom;
    char        *pszChildTo;
    char        *pszTarget;
    size_t       cbRead;
    int          nNames;
    int          iReply;
    int          iStatus;
    int          rcExisting;
    int          rc;
    int          i;

    if (pRun->bCancelled)
    {   /* The run was cancelled: do nothing more at any level. */
        return;
    }

    if (pSource->uiType != BFE_TYPE_DIR && pSource->uiType != BFE_TYPE_FILE && pSource->uiType != BFE_TYPE_SYMLINK)
    {   /* A device, fifo or socket: it cannot be created here -- lwext4    */
        /* has no call that creates one (ext4.h has no mknod).  Tested      */
        /* first, before the destination is even looked up, since nothing   */
        /* about the destination could change the answer.                   */
        BFC_Emit(pRun, BFE_EVENT_SKIPPED, pSource->uiType, BFE_ERR_TYPE, pszTo, NULL, 0, 0);
        return;
    }

    /* What, if anything, already has the destination name. */
    rcExisting = BFX_FillEntry(pszTo, BFU_LastComponent(pszTo), &existing);

    if (rcExisting != 0 && rcExisting != ENOENT)
    {   /* The destination could not even be looked up (a damaged parent):   */
        /* nothing is created over an unknown state.  rcExisting is kept     */
        /* for the branches below: 0 = taken (type in existing), ENOENT =    */
        /* free.                                                             */
        BFC_Emit(pRun, BFE_EVENT_ERROR, pSource->uiType, BFX_MapErrno(rcExisting), pszTo, NULL, 0, 0);
        return;
    }

    if (pSource->uiType == BFE_TYPE_DIR)
    {   /* A directory: make the destination, copy the children, then set the   */
        /* destination's own metadata.                                          */

        /* The directory's START; a cancel reply is seen by the child loop. */
        BFC_Emit(pRun, BFE_EVENT_START, BFE_TYPE_DIR, BFE_OK, pszTo, NULL, 0, 0);

        if (rcExisting == 0 && existing.uiType != BFE_TYPE_DIR)
        {   /* A file or link holds the name; a directory cannot replace it.   */
            /* An ERROR, not a CONFLICT, as in BFC_ImportNode: nothing is      */
            /* deleted to make room for a directory.  Nothing is created and   */
            /* the source's children are not even listed.                      */
            BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_DIR, BFE_ERR_EXISTS, pszTo, NULL, 0, 0);
            return;
        }

        /* The source's children are read BEFORE the destination is created,     */
        /* so that a destination made inside the source could never be seen      */
        /* as one of its children (the public function refuses that case; this   */
        /* order makes the walk safe on its own account as well).  A damaged     */
        /* source directory fails the collection rather than copying short.      */
        rc = BFX_CollectNames(pszFrom, &ppszNames, &nNames);

        if (rc != 0)
        {   /* The source directory cannot be read: nothing has been created   */
            /* at the destination yet (the names come first), so there is      */
            /* nothing to undo.                                                */
            BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_DIR, BFX_MapErrno(rc), pszTo, NULL, 0, 0);
            return;
        }

        if (rcExisting == ENOENT)
        {   /* Not there yet: create it.  (An existing directory is merged.) */
            rc = BFS_DirMake(pszTo);

            if (rc == 0)
            {   /* Created: settle its checksum now, so a directory that ends   */
                /* up empty is still clean.  The mode and time given here are   */
                /* provisional; BFC_WithinMeta sets the final ones below.       */
                rc = BFX_NewDirFinish(pszTo, pSource->uiMode, pSource->uiMtime);
            }

            if (rc != 0)
            {   /* No space, or an I/O error (a missing parent folder was      */
                /* refused before the walk began).  As in BFC_ImportNode,      */
                /* a directory whose mkdir succeeded and only its finish       */
                /* failed stays, reported failed; none of the children are     */
                /* copied.  The names collected first are the one thing this   */
                /* path holds, so they are freed here.                         */
                BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_DIR, BFX_MapErrno(rc), pszTo, NULL, 0, 0);
                BFX_FreeNames(ppszNames, nNames);
                return;
            }
        }

        for (i = 0; i < nNames && !pRun->bCancelled; i++)
        {   /* One child of the source directory per iteration. */

            /* The child's source path and its destination path (the same   */
            /* name: a copy inside the image needs no name check).          */
            pszChildFrom = BFU_JoinImage(pszFrom, ppszNames[i]);
            pszChildTo   = BFU_JoinImage(pszTo, ppszNames[i]);

            if (pszChildFrom == NULL || pszChildTo == NULL)
            {   /* Out of memory composing the child's paths: reported against   */
                /* the directory, and whichever came is freed.  The ERROR        */
                /* stops the whole run (BFC_Emit), so the loop ends at its       */
                /* test.                                                         */
                BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_UNKNOWN, BFE_ERR_NO_MEMORY, pszTo, NULL, 0, 0);
                free(pszChildFrom);
                free(pszChildTo);
                continue;
            }

            /* The child's facts, which decide how it is copied. */
            rc = BFX_FillEntry(pszChildFrom, ppszNames[i], &child);

            if (rc != 0)
            {   /* The child's inode cannot be read: reported under the path   */
                /* it would have had, and on to the next child -- unless the   */
                /* read ran out of memory (NO_MEMORY), which stops the run.    */
                BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_UNKNOWN, BFX_MapErrno(rc), pszChildTo, NULL, 0, 0);
            }
            else
            {   /* Copy the child (recursing when it is a directory). */
                BFC_WithinNode(pRun, pszChildFrom, pszChildTo, &child);
            }

            /* This child's two paths. */
            free(pszChildFrom);
            free(pszChildTo);
        }

        /* The name list is done with. */
        BFX_FreeNames(ppszNames, nNames);

        if (pRun->bCancelled)
        {   /* Cancelled part-way through this directory: its metadata is   */
            /* left as created (the provisional mode and time).             */
            return;
        }

        /* The directory's own metadata, now that its children are in --   */
        /* last, so nothing done to its children disturbs the times just   */
        /* set.                                                            */
        rc = BFC_WithinMeta(pszTo, pSource);

        if (rc != 0)
        {   /* The directory is there but its metadata could not be set: an   */
            /* ERROR for it (no DONE), though its children are in.            */
            BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_DIR, BFX_MapErrno(rc), pszTo, NULL, 0, 0);
            return;
        }

        /* The directory is done (its children reported for themselves). */
        BFC_Emit(pRun, BFE_EVENT_DONE, BFE_TYPE_DIR, BFE_OK, pszTo, NULL, 0, 0);
        return;
    }

    /* A regular file or a symbolic link: the destination name must be free,   */
    /* or the caller agrees to replace what holds it.                          */
    if (rcExisting == 0 && existing.uiType == BFE_TYPE_DIR)
    {   /* A directory holds the name; a file or link cannot replace it.   */
        /* An ERROR, not a CONFLICT: no answer would remove a directory.   */
        BFC_Emit(pRun, BFE_EVENT_ERROR, pSource->uiType, BFE_ERR_EXISTS, pszTo, NULL, 0, pSource->ullSize);
        return;
    }

    if (rcExisting == 0)
    {   /* A file, link or node already has the name: the caller decides. */
        iReply = BFC_Emit(pRun, BFE_EVENT_CONFLICT, pSource->uiType, BFE_OK, pszTo, NULL, 0, pSource->ullSize);

        if (iReply != BFE_REPLY_OVERWRITE)
        {   /* Skip (or cancel, already latched): leave the existing entry.   */
            /* No SKIPPED event: the caller chose it and already knows.       */
            return;
        }

        /* Overwrite means REPLACE: remove the old entry first, so a symlink   */
        /* or a hard-linked file is replaced rather than written through.      */
        /* pszTo is never this item's own source: the walk adds the same       */
        /* name to both paths at every level, and BFE_CopyWithin refuses top   */
        /* paths that are one entry -- compared in their canonical forms       */
        /* (BFX_PathCanonical: on FAT without the trailing spaces and dots     */
        /* FAT drops, letter case folded), with no empty, "." or ".."          */
        /* component allowed (BFX_PathValid: "/d/" is refused, never read as   */
        /* "/d") -- so the two paths here name two different entries.          */
        rc = BFS_Remove(pszTo);

        if (rc != 0)
        {   /* The old entry could not be removed: it stays as it was, and   */
            /* nothing is written -- the item is reported failed.            */
            BFC_Emit(pRun, BFE_EVENT_ERROR, pSource->uiType, BFX_MapErrno(rc), pszTo, NULL, 0, pSource->ullSize);
            return;
        }
    }

    /* The item's START, which the caller may answer with cancel. */
    BFC_Emit(pRun, BFE_EVENT_START, pSource->uiType, BFE_OK, pszTo, NULL, 0, pSource->ullSize);

    if (pRun->bCancelled)
    {   /* Cancelled on the START event, before anything was written.  (An   */
        /* entry removed for an overwrite above stays removed.)              */
        return;
    }

    if (pSource->uiType == BFE_TYPE_SYMLINK)
    {   /* A symbolic link: read its target text and make a new link with it. */

        if (pSource->ullSize >= BFC_LINK_TARGET_MAX)
        {   /* A size no symlink can have: the inode is damaged.  Refused      */
            /* before anything is allocated, as BFM_WriteBlock refuses it --   */
            /* the size is never trusted as a buffer's: 2^64 - 1 would wrap    */
            /* the "+ 1" below to malloc(0), and lwext4's read would then be   */
            /* handed that size and write past the block it got.  Nothing is   */
            /* created; the item is reported failed as an unreadable image     */
            /* entry, and the copy goes on with the next.                      */
            BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_SYMLINK, BFE_ERR_IO_IMAGE, pszTo, NULL, 0, 0);
            return;
        }

        /* The buffer: the target's length and one byte more for its NUL.   */
        /* The read (lwext4's, through BFS_ReadLink) copies the target      */
        /* without terminating it, so the NUL is put in by hand after it.   */
        pszTarget = (char *)malloc((size_t)pSource->ullSize + 1);

        if (pszTarget == NULL)
        {   /* Out of memory for the target text: nothing created, the item   */
            /* reported failed.                                               */
            BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_SYMLINK, BFE_ERR_NO_MEMORY, pszTo, NULL, 0, 0);
            return;
        }

        /* The target's bytes, at most the link's size. */
        cbRead = 0;
        rc     = BFS_ReadLink(pszFrom, pszTarget, (size_t)pSource->ullSize, &cbRead);

        if (rc == 0)
        {   /* Terminate what was read, and create the copy. */
            pszTarget[cbRead] = '\0';
            rc                = BFS_SymLink(pszTarget, pszTo);
        }

        if (rc == 0)
        {   /* Created: lwext4 leaves a new link with mode 0666 and zero      */
            /* times; give it 0777, the source's owner and its modify time.   */
            rc = BFX_NewLinkFinish(pszTo, pSource->uiUid, pSource->uiGid, pSource->uiMtime);
        }

        /* The target text is done with, either way. */
        free(pszTarget);

        if (rc != 0)
        {   /* The link could not be read or made -- or was made and its   */
            /* owner and time could not be set, in which case it stays,    */
            /* reported failed (it is not removed: its target is right).   */
            BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_SYMLINK, BFX_MapErrno(rc), pszTo, NULL, 0, 0);
            return;
        }

        /* The link is copied. */
        BFC_Emit(pRun, BFE_EVENT_DONE, BFE_TYPE_SYMLINK, BFE_OK, pszTo, NULL, 0, 0);
        return;
    }

    /* A regular file: the bytes, then the metadata.  A failure or cancel   */
    /* removes the partial copy.                                            */
    iStatus = BFC_WithinBytes(pRun, pszFrom, pszTo, pSource->ullSize);

    if (iStatus == BFE_ERR_CANCELLED)
    {   /* Cancelled mid-file; the partial copy is already removed.  No   */
        /* ERROR: the cancel is reported once, by the run's result.       */
        return;
    }

    if (iStatus != BFE_OK)
    {   /* The copy failed: the partial copy is removed, so nothing holds   */
        /* the name (after an overwrite, not even the old entry).           */
        BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_FILE, iStatus, pszTo, NULL, 0, pSource->ullSize);
        return;
    }

    /* The source's metadata onto the copy. */
    rc = BFC_WithinMeta(pszTo, pSource);

    if (rc != 0)
    {   /* The bytes are in but the metadata could not be set: the copy   */
        /* stays (its data is whole) and the item is reported failed.     */
        BFC_Emit(pRun, BFE_EVENT_ERROR, BFE_TYPE_FILE, BFX_MapErrno(rc), pszTo, NULL, 0, pSource->ullSize);
        return;
    }

    /* The file is copied, with its metadata. */
    BFC_Emit(pRun, BFE_EVENT_DONE, BFE_TYPE_FILE, BFE_OK, pszTo, NULL, pSource->ullSize, pSource->ullSize);
}

/*----------------------------------------------------------------------------*/
/* Bfc_PathsSame:                                                             */
/*                                                                            */
/* Whether two image paths name the same entry, as the open filesystem        */
/* matches names: byte for byte on ext2/3/4, whose names are case-sensitive,  */
/* and letter case aside on FAT, whose names are not -- so "/A" and "/a" are  */
/* one entry there, and BFE_CopyWithin's refusals must see them as one.  The  */
/* FAT fold is FatFs's own (BFT_PathFoldPrefix), the one FatFs finds names    */
/* by, so accented letters fold as FatFs folds them.  The paths given are     */
/* canonical (BFX_PathCanonical), so FAT's other way of spelling one name     */
/* twice -- trailing spaces and dots, which it drops -- is already gone.      */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszLeft  : one path.                                                   */
/*     pszRight : the other.                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     bool : true when they name one entry, false otherwise.                 */
/*----------------------------------------------------------------------------*/
static bool Bfc_PathsSame(const char *pszLeft, const char *pszRight)
{
    size_t cbMatched;

    if (BFX_FsTraits()->bFatNames)
    {   /* FAT's names: the right path matches the left, letter case aside,   */
        /* all the way to the left one's end.                                 */
        return(BFT_PathFoldPrefix(pszLeft, pszRight, &cbMatched) && pszLeft[cbMatched] == '\0');
    }

    /* ext2/3/4: byte for byte. */
    return(strcmp(pszLeft, pszRight) == 0);
}

/*----------------------------------------------------------------------------*/
/* Bfc_PathUnder:                                                             */
/*                                                                            */
/* Whether one image path lies under another: it begins with the other's      */
/* whole path and a '/' follows -- the slash is what keeps "/mp/ab" from      */
/* being taken as under "/mp/a", which a bare prefix test would do.  Matched  */
/* as Bfc_PathsSame matches: byte for byte on ext2/3/4, letter case aside by  */
/* FatFs's fold on FAT.  On FAT the '/' is looked for where the fold's match  */
/* ended, because a letter and its capital may differ in UTF-8 length.  The   */
/* root is not handled here; the callers test it first.                       */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszInner : the path that may lie under the other.                      */
/*     pszOuter : the path it may lie under.                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     bool : true when pszInner lies under pszOuter, false otherwise.        */
/*----------------------------------------------------------------------------*/
static bool Bfc_PathUnder(const char *pszInner, const char *pszOuter)
{
    size_t cbMatched;

    if (BFX_FsTraits()->bFatNames)
    {   /* FAT's names: the outer path matches the inner one's start,   */
        /* letter case aside, and a '/' follows there.                  */
        return(BFT_PathFoldPrefix(pszInner, pszOuter, &cbMatched) && pszInner[cbMatched] == '/');
    }

    /* ext2/3/4: the outer path's bytes begin the inner one, then a '/'. */
    cbMatched = strlen(pszOuter);
    return(strncmp(pszInner, pszOuter, cbMatched) == 0 && pszInner[cbMatched] == '/');
}

/*----------------------------------------------------------------------------*/
/* BFE_CopyWithin:                                                            */
/*                                                                            */
/* Copy one image entry -- a file, a symbolic link, or a directory with       */
/* everything under it -- to another path in the SAME image.  pszImageTo      */
/* names the destination ITEM, so the caller chooses the name the copy gets.  */
/* The copy keeps its source's mode, owner, group and times.                  */
/*                                                                            */
/* Three requests are refused before anything is touched: copying an entry    */
/* onto itself; copying a directory to a path inside itself (the copy would   */
/* be reading what it had just written, without end); and copying an entry    */
/* onto a directory that holds it, the root included (the merge would write   */
/* back into the source it is still reading).  On FAT each is recognised in   */
/* any spelling FAT matches as the same -- other letter case, or trailing     */
/* spaces and dots added -- so "/a.txt" onto "/a.txt." is "onto itself".      */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImageFrom : the entry to copy.                                      */
/*     pszImageTo   : the image path to create for the copy.                  */
/*     pfnEvent     : the event callback, or NULL.                            */
/*     pvContext    : passed to pfnEvent unchanged.                           */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_CANCELLED; BFE_ERR_PARTIAL; BFE_ERR_NO_MEMORY    */
/*           when memory ran out at an item, which stopped the whole copy     */
/*           there (BFC_Emit); or, before anything was copied, BFE_ERR_ARG    */
/*           (a bad path, the same path twice, a                              */
/*           directory into itself, or an entry onto a directory that holds   */
/*           it), BFE_ERR_NOT_OPEN, BFE_ERR_READ_ONLY, BFE_ERR_NO_MEMORY,     */
/*           BFE_ERR_BUSY or BFE_ERR_NOT_FOUND (or another error the source's */
/*           inode read maps to); BFE_ERR_NOT_FOUND / BFE_ERR_NOT_DIR also    */
/*           when the destination's folder is missing or not a directory.     */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_CopyWithin(const char *pszImageFrom, const char *pszImageTo,
                           BFE_CopyCallback pfnEvent, void *pvContext)
{
    struct BFC_Run  run;
    BFE_Entry       entry;
    char           *pszFromCanon;   // the source as its filesystem spells it (BFX_PathCanonical), for the three refusals
    char           *pszToCanon;     // the destination likewise
    int             iStatus;
    int             rc;

    if (!BFX_PathValid(pszImageFrom) || !BFX_PathValid(pszImageTo))
    {   /* A relative or missing path: refused before the gate, so no error   */
        /* text is recorded for it -- the text may only be written while      */
        /* the busy flag is held.                                             */
        return(BFE_ERR_ARG);
    }

    /* The gate: an image must be open -- of a kind this function writes,   */
    /* a FAT one through bfe_fs.c's FAT arms -- and open for writing.       */
    iStatus = BFX_EnterKinds(true, BFX_FSMASK_EXT | BFX_FSMASK_FAT);

    if (iStatus != BFE_OK)
    {   /* Busy, nothing open, or read-only: BFX_EnterKinds has said which,   */
        /* and this call holds nothing to release.                            */
        return(iStatus);
    }

    /* The two paths in the one spelling their filesystem matches them by     */
    /* (BFX_PathCanonical: on FAT, each name's trailing spaces and dots       */
    /* dropped, as FAT drops them; on ext2/3/4, the paths themselves).  The   */
    /* refusals below test these copies; the walk goes on with the caller's   */
    /* own paths, which FatFs reads the same way.                             */
    pszFromCanon = BFX_PathCanonical(pszImageFrom);
    pszToCanon   = BFX_PathCanonical(pszImageTo);

    if (pszFromCanon == NULL || pszToCanon == NULL)
    {   /* Out of memory for either copy: refused with nothing touched,   */
        /* whichever copy was made freed (free of NULL is allowed).       */
        free(pszFromCanon);
        free(pszToCanon);
        iStatus = BFX_Fail(BFE_ERR_NO_MEMORY, "out of memory copying %s", pszImageFrom);
        BFX_Leave();
        return(iStatus);
    }

    /* The three refusals below compare the canonical paths as text --      */
    /* letter case aside on FAT, as its names are (Bfc_PathsSame,           */
    /* Bfc_PathUnder) --                                                    */
    /* which is exact because BFX_PathValid has refused any empty, "." or   */
    /* ".." component -- no "//" and no trailing '/', the root "/"          */
    /* excepted -- and the canonical form has dropped what else FAT does    */
    /* not count: each entry has one spelling, so equal strings are the     */
    /* same entry and a string prefix ending at a '/' is a real ancestor.   */
    /* Each refusal frees the two copies before it returns.                 */
    if (Bfc_PathsSame(pszFromCanon, pszToCanon))
    {   /* The same entry: there is nothing to copy it onto.  Refused with   */
        /* only the flag held; the image has not been touched.               */
        free(pszFromCanon);
        free(pszToCanon);
        iStatus = BFX_Fail(BFE_ERR_ARG, "%s cannot be copied onto itself", pszImageFrom);
        BFX_Leave();
        return(iStatus);
    }

    /* "Inside itself" means the destination lies under the source   */
    /* (Bfc_PathUnder) -- and the root contains every path.          */
    if (strcmp(pszFromCanon, BFX_MOUNT_POINT) == 0 || Bfc_PathUnder(pszToCanon, pszFromCanon))
    {   /* The destination lies under the source: refused, like the test   */
        /* above, with only the flag held and the image untouched.         */
        free(pszFromCanon);
        free(pszToCanon);
        iStatus = BFX_Fail(BFE_ERR_ARG, "%s cannot be copied into itself", pszImageFrom);
        BFX_Leave();
        return(iStatus);
    }

    /* "Onto a directory that holds it" is the same test the other way       */
    /* round: the SOURCE begins with the destination's whole path and a      */
    /* slash, or the destination is the root, which holds every entry.       */
    /* Such a copy merges the source into a directory above it, and a        */
    /* child whose name repeats the source's own then lands back INSIDE      */
    /* the source: copying /a/a onto /a sends /a/a/a onto /a/a, the source   */
    /* itself, and /a/a/a/x onto /a/a/x -- a source file the walk has not    */
    /* copied yet, which an Overwrite answer replaces, so its contents are   */
    /* lost with nothing reported.  The whole request is refused, not        */
    /* only the children that would land inside the source: what such a      */
    /* merge leaves behind would otherwise depend on the order the names     */
    /* are walked in.                                                        */
    if (strcmp(pszToCanon, BFX_MOUNT_POINT) == 0 || Bfc_PathUnder(pszFromCanon, pszToCanon))
    {   /* The source lies under the destination: refused like the two     */
        /* tests above, with only the flag held and the image untouched.   */
        free(pszFromCanon);
        free(pszToCanon);
        iStatus = BFX_Fail(BFE_ERR_ARG, "%s cannot be copied onto %s, a folder that holds it",
                           pszImageFrom, pszImageTo);
        BFX_Leave();
        return(iStatus);
    }

    /* The refusals are passed; the copies are not needed by the walk. */
    free(pszFromCanon);
    free(pszToCanon);

    /* The source's facts, which the walk starts from. */
    rc = BFX_FillEntry(pszImageFrom, BFU_LastComponent(pszImageFrom), &entry);

    if (rc != 0)
    {   /* The source does not exist in the image (or its inode cannot be   */
        /* read): refuse with the mapped error, and let the next call in.   */
        iStatus = BFX_Fail(BFX_MapErrno(rc), "cannot copy %s: %s", pszImageFrom, strerror(rc));
        BFX_Leave();
        return(iStatus);
    }

    /* The folder the copy is to go in must be there, and be a folder, as    */
    /* for an import: no missing folder above the destination is made.       */
    /* lwext4's create path would make them, FatFs would refuse, and a       */
    /* mistyped destination should fail, not grow a tree.  The copied item   */
    /* itself -- a folder and all under it -- is made by the walk.           */
    rc = BFX_CheckParent(pszImageTo);

    if (rc != 0)
    {   /* No folder to put it in, or it could not be looked at: refused   */
        /* with nothing copied, only the flag held.                        */
        if (rc == ENOENT || rc == ENOTDIR)
        {   /* Missing, or not a folder: NOT_FOUND or NOT_DIR, said plainly. */
            iStatus = BFX_Fail(BFX_MapErrno(rc), "cannot copy %s to %s: the folder it is to go in %s",
                               pszImageFrom, pszImageTo, (rc == ENOENT) ? "does not exist" : "is not a directory");
        }
        else
        {   /* The lookup itself failed: that failure, mapped. */
            iStatus = BFX_Fail(BFX_MapErrno(rc), "cannot copy %s to %s: %s", pszImageFrom, pszImageTo, strerror(rc));
        }

        BFX_Leave();
        return(iStatus);
    }

    /* Set the run up: zero counters and the caller's callback.  The extract   */
    /* and import options are not used by a copy inside the image.             */
    memset(&run, 0, sizeof(run));
    run.pfnEvent  = pfnEvent;
    run.pvContext = pvContext;

    /* The walk.  Unlike an extract or an import, nothing is opened or   */
    /* loaded around it, so there is nothing to close after it.          */
    BFC_WithinNode(&run, pszImageFrom, pszImageTo, &entry);

    /* The run's outcome, recorded by BFC_RunResult before the gate opens. */
    iStatus = BFC_RunResult(&run);
    BFX_Leave();

    /* BFE_OK, BFE_ERR_PARTIAL, BFE_ERR_CANCELLED or BFE_ERR_NO_MEMORY. */
    return(iStatus);
}
