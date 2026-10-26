/*----------------------------------------------------------------------------*/
/* bfe_sqfs.c                                                                 */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The squashfs session bfe_sqfs.h declares: a squashfs 4.0 filesystem read   */
/* through squashfuse's reader (engine\squashfuse), and the one function      */
/* squashfuse expects its host to supply for reading -- sqfs_pread -- written */
/* here over the engine's block device.                                       */
/*                                                                            */
/* HOW IT FITS.  squashfuse keeps everything about one filesystem in a        */
/* struct sqfs and reads the image through sqfs_pread, handing it the         */
/* sqfs_fd_t it was given at sqfs_init.  sqfs_port\win32.h makes that a       */
/* pointer to this file's reader context, so every byte squashfuse reads      */
/* comes through BFD_TargetRead -- the block device's own path, which meets   */
/* a device's sector rules and works for an image file and a card alike --    */
/* and no read reaches past the filesystem's range (a damaged table pointing  */
/* past it fails the read, never reaching the next partition of a card).      */
/*                                                                            */
/* WHAT IS NOT USED.  squashfuse's path lookup (sqfs_lookup_path) is not      */
/* called: this file walks a path itself with squashfuse's directory reader,  */
/* so a component that is not a directory is told apart from one that does    */
/* not exist (squashfuse answers both as a failure).  Its extended            */
/* attributes are read only as far as sqfs_init does to set itself up; none   */
/* is reported.  Its FUSE program and its threaded caches are not built.      */
/*                                                                            */
/* squashfs keeps one time per inode (its modify time), so all three of a     */
/* BFE_Entry's times carry it; its owners are indexes into an id table,       */
/* looked up here; its entry types come from the inode's type field, never    */
/* from squashfuse's mode word, whose block-device bits on this build are     */
/* MinGW's (0x3000, not POSIX's 0x6000).                                      */
/*----------------------------------------------------------------------------*/

/* squashfuse's reader: the filesystem state and inodes (fs.h, which brings    */
/* the on-disk layout, the caches and the decompressor table), the directory   */
/* walk, the file reads, the platform hooks (nonstd.h -- sqfs_pread's          */
/* prototype, which the definition below is checked against), the              */
/* compression names, and the byte-swapped magic (swap.h).                     */
#include "fs.h"
#include "dir.h"
#include "file.h"
#include "nonstd.h"
#include "decompress.h"
#include "swap.h"

/* The C library: errno values for the errno-style results, memory for the   */
/* name lists, handles and link targets, and the string copies.              */
#include <errno.h>
#include <stdlib.h>
#include <string.h>

/* The engine's own: the shared internals (the error recorder, bool, the   */
/* permission mask, the name list's release), the block device             */
/* squashfuse reads through, and this file's own header.                   */
#include "bfe_internal.h"
#include "bfe_blockdev.h"
#include "bfe_sqfs.h"

/*----------------------------------------------------------------------------*/
/* Definitions -- documentation table.                                        */
/*----------------------------------------------------------------------------*/
#define BFQ_MAGIC_BYTES    4                     // the squashfs magic's length, at the range's first byte
#define BFQ_NAMES_FIRST    64                    // a collected name list's first capacity, in names; doubles as it fills

/*----------------------------------------------------------------------------*/
/* struct BFQ_strctSource -- the reader context squashfuse's sqfs_fd_t        */
/* points to (sqfs_port\win32.h names it by its tag), handed to sqfs_pread    */
/* with every read -- documentation table.                                    */
/*----------------------------------------------------------------------------*/
struct BFQ_strctSource
{
    uint64_t ullLength;                          // the filesystem range's length in bytes, as BFD_TargetOpen gave it: no read reaches past it
};

/*----------------------------------------------------------------------------*/
/* struct BFQ_strctFile -- one file open for reading (bfe_sqfs.h names it     */
/* for bfe_fs.c) -- documentation table.                                      */
/*----------------------------------------------------------------------------*/
struct BFQ_strctFile
{
    sqfs_inode inode;                            // the file's inode as squashfuse read it at the open: its size and where its blocks and fragment are
    uint64_t   ullPos;                           // the next byte to read, from the file's start
};

/*----------------------------------------------------------------------------*/
/* The session's state -- documentation table.  Guarded by the engine's busy  */
/* flag: only the call holding it touches any of this.                        */
/*----------------------------------------------------------------------------*/
static sqfs                   Bfq_fs;            // squashfuse's state for the mounted filesystem (superblock, tables, caches); valid only while Bfq_bMounted
static struct BFQ_strctSource Bfq_source;        // the reader context Bfq_fs reads through (its fd)
static bool                   Bfq_bMounted;      // true from a BFQ_Mount that succeeded until BFQ_Unmount

/* The file's private functions -- documentation table. */
static bool     Bfq_IsDir(const sqfs_inode *pInode);                                                      // true for a directory inode, of either form
static uint32_t Bfq_TypeFromInode(const sqfs_inode *pInode);                                              // the BFE_TYPE_* of an inode, from its type field
static int      Bfq_FindInDir(sqfs_inode *pDir, const char *pszName, size_t cbName, sqfs_inode_id *pId);  // one name's inode id in a directory; 0, ENOENT, EIO
static int      Bfq_LookupPath(const char *pszPath, sqfs_inode *pInode);                                  // a path's inode, walked from the root; 0, ENOENT, ENOTDIR, EIO

/*----------------------------------------------------------------------------*/
/* sqfs_pread:                                                                */
/*                                                                            */
/* squashfuse's one read of the image, which its host supplies (nonstd.h      */
/* declares it; the name is squashfuse's, not this engine's).  Every read it  */
/* makes -- the superblock, the tables, every metadata and data block --      */
/* comes here, with an offset counted from the filesystem's first byte.       */
/* The read is made through the block device, from the filesystem's place     */
/* in the file or device, and only when it lies wholly inside the range: an   */
/* offset taken from a damaged superblock or table is refused here rather     */
/* than read from beyond the range, which on a card is another partition.     */
/*                                                                            */
/* Arguments:                                                                 */
/*     pSource   : the reader context sqfs_init was given (Bfq_source).       */
/*     pvBuffer  : receives the bytes.                                        */
/*     cbCount   : how many.                                                  */
/*     ullOffset : the first byte, from the filesystem's start.               */
/*                                                                            */
/* Returns:                                                                   */
/*     ssize_t : cbCount when every byte was read; -1 when the range would be */
/*               left or the read failed -- squashfuse compares the result    */
/*               with what it asked for, so either is its read failure.       */
/*----------------------------------------------------------------------------*/
ssize_t sqfs_pread(sqfs_fd_t pSource, void *pvBuffer, size_t cbCount, sqfs_off_t ullOffset)
{
    /* Inside the range, tested as subtractions so a huge offset or count   */
    /* cannot wrap round: the offset first, then the room left after it.    */
    if (ullOffset > pSource->ullLength || (uint64_t)cbCount > pSource->ullLength - ullOffset)
    {   /* It would read past the filesystem's end: refused, nothing read. */
        return(-1);
    }

    /* The read itself, from the start of the file or device -- the range's   */
    /* own offset added, as BFD_TargetRead counts from there.  A failure is   */
    /* noted by the block device for the detail line of whatever the caller   */
    /* reports.                                                               */
    if (BFD_TargetRead(BFD_TargetOffset() + ullOffset, pvBuffer, (uint64_t)cbCount) != 0)
    {   /* A short read (the image is cut short) or the media failed. */
        return(-1);
    }

    /* Every byte asked for was read. */
    return((ssize_t)cbCount);
}

/*----------------------------------------------------------------------------*/
/* Bfq_IsDir:                                                                 */
/*                                                                            */
/* Whether an inode is a directory.  squashfs has two forms of one -- the     */
/* basic and the extended (with an index and attributes) -- told by the       */
/* inode's type field, which is the one source of a type here.                */
/*                                                                            */
/* Arguments:                                                                 */
/*     pInode : the inode, as squashfuse read it.                             */
/*                                                                            */
/* Returns:                                                                   */
/*     bool : true for a directory of either form, false otherwise.           */
/*----------------------------------------------------------------------------*/
static bool Bfq_IsDir(const sqfs_inode *pInode)
{
    if (pInode->base.inode_type == SQUASHFS_DIR_TYPE || pInode->base.inode_type == SQUASHFS_LDIR_TYPE)
    {   /* The basic or the extended directory inode. */
        return(true);
    }

    /* Any other type of inode. */
    return(false);
}

/*----------------------------------------------------------------------------*/
/* Bfq_TypeFromInode:                                                         */
/*                                                                            */
/* The BFE_TYPE_* of an inode, from its type field.  Each kind of entry has   */
/* a basic and an extended form in squashfs (the extended one carrying a      */
/* link count or attributes), and both forms are the same type here.  The     */
/* mode word is not used: squashfuse ORs in a type from the host's own        */
/* S_IF* values, and MinGW's block-device value is not POSIX's.               */
/*                                                                            */
/* Arguments:                                                                 */
/*     pInode : the inode, as squashfuse read it.                             */
/*                                                                            */
/* Returns:                                                                   */
/*     uint32_t : the BFE_TYPE_* value; BFE_TYPE_UNKNOWN for any other field  */
/*                value (squashfuse refuses such an inode as it reads it, so  */
/*                this is a guard).                                           */
/*----------------------------------------------------------------------------*/
static uint32_t Bfq_TypeFromInode(const sqfs_inode *pInode)
{
    switch (pInode->base.inode_type)
    {   /* One squashfs inode type, in either form, per case. */

        /* A directory. */
        case SQUASHFS_DIR_TYPE:
        case SQUASHFS_LDIR_TYPE:     return(BFE_TYPE_DIR);

        /* A regular file. */
        case SQUASHFS_REG_TYPE:
        case SQUASHFS_LREG_TYPE:     return(BFE_TYPE_FILE);

        /* A symbolic link. */
        case SQUASHFS_SYMLINK_TYPE:
        case SQUASHFS_LSYMLINK_TYPE: return(BFE_TYPE_SYMLINK);

        /* A block device node. */
        case SQUASHFS_BLKDEV_TYPE:
        case SQUASHFS_LBLKDEV_TYPE:  return(BFE_TYPE_BLOCKDEV);

        /* A character device node. */
        case SQUASHFS_CHRDEV_TYPE:
        case SQUASHFS_LCHRDEV_TYPE:  return(BFE_TYPE_CHARDEV);

        /* A named pipe. */
        case SQUASHFS_FIFO_TYPE:
        case SQUASHFS_LFIFO_TYPE:    return(BFE_TYPE_FIFO);

        /* A unix socket. */
        case SQUASHFS_SOCKET_TYPE:
        case SQUASHFS_LSOCKET_TYPE:  return(BFE_TYPE_SOCKET);

        /* Not a type squashfs defines. */
        default:                     return(BFE_TYPE_UNKNOWN);
    }
}

/*----------------------------------------------------------------------------*/
/* Bfq_FindInDir:                                                             */
/*                                                                            */
/* Find one name in a directory and give back its inode's id.  The whole      */
/* directory is walked in order and every entry compared, byte for byte and   */
/* by length: squashfs names are bytes, matched exactly, as ext2/3/4's are.   */
/* mksquashfs writes a directory's entries sorted, which squashfuse's own     */
/* lookup relies on to stop early and to use the directory's index; this      */
/* walk relies on no order, so an image written out of order still finds      */
/* every name, at the cost of reading the whole directory.                    */
/*                                                                            */
/* Arguments:                                                                 */
/*     pDir    : the directory's inode (the caller has checked it is one).    */
/*     pszName : the name -- not terminated: a path component.                */
/*     cbName  : its length in bytes.                                         */
/*     pId     : receives the entry's inode id when found.                    */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0 when found; ENOENT when no entry has the name; EIO when the    */
/*           directory could not be read through.                             */
/*----------------------------------------------------------------------------*/
static int Bfq_FindInDir(sqfs_inode *pDir, const char *pszName, size_t cbName, sqfs_inode_id *pId)
{
    sqfs_dir       dir;
    sqfs_dir_entry entry;
    sqfs_err       err;
    char           szEntryName[SQUASHFS_NAME_LEN + 1];

    /* Open the directory for a walk from its start (offset 0). */
    if (sqfs_dir_open(&Bfq_fs, pDir, &dir, 0) != SQFS_OK)
    {   /* Its listing could not be opened: a damaged inode. */
        return(EIO);
    }

    /* Each entry's name is read into the buffer given here -- one byte         */
    /* longer than the longest name squashfs allows, which squashfuse refuses   */
    /* to read past.  The result starts as success: sqfs_dir_next sets it on    */
    /* every call, and says only through it whether a walk ended in error.      */
    sqfs_dentry_init(&entry, szEntryName);
    err = SQFS_OK;

    while (sqfs_dir_next(&Bfq_fs, &dir, &entry, &err))
    {   /* One directory entry per iteration; the walk ends at the last one,   */
        /* or at a read that failed.                                           */
        if (sqfs_dentry_name_size(&entry) == cbName && memcmp(szEntryName, pszName, cbName) == 0)
        {   /* The same length and the same bytes: this entry is the name. */
            *pId = sqfs_dentry_inode(&entry);
            return(0);
        }
    }

    if (err != SQFS_OK)
    {   /* The walk stopped on a read that failed, not at the end: whether   */
        /* the name is there cannot be said.                                 */
        return(EIO);
    }

    /* Every entry was read and none has the name. */
    return(ENOENT);
}

/*----------------------------------------------------------------------------*/
/* Bfq_LookupPath:                                                            */
/*                                                                            */
/* Resolve an engine path -- absolute, canonical, '/' between names, as       */
/* BFX_PathValid admits -- to its inode, walking from the root one component  */
/* at a time.  No symbolic link is followed at any point, as lwext4's lookup  */
/* follows none: a link is described, never resolved, and a path running      */
/* through one does not resolve.                                              */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszPath : the path ("/" for the root).                                 */
/*     pInode  : receives the inode.                                          */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0; ENOENT when a component does not exist; ENOTDIR when one      */
/*           before the last is not a directory; EIO when an inode or a       */
/*           directory could not be read.                                     */
/*----------------------------------------------------------------------------*/
static int Bfq_LookupPath(const char *pszPath, sqfs_inode *pInode)
{
    const char   *pszScan;
    const char   *pszName;
    size_t        cbName;
    sqfs_inode_id id;
    int           rc;

    /* Start at the root, whose id the superblock records. */
    if (sqfs_inode_get(&Bfq_fs, pInode, sqfs_inode_root(&Bfq_fs)) != SQFS_OK)
    {   /* The root inode could not be read: a damaged image. */
        return(EIO);
    }

    /* Walk the path's components, each one's inode replacing the last. */
    pszScan = pszPath;

    for (;;)
    {   /* One path component per iteration. */

        /* Step over the separator in front of the component (only the     */
        /* leading "/" and one between names, as the path is canonical).   */
        while (*pszScan == '/')
        {   /* A separator: not part of any name. */
            pszScan++;
        }

        if (*pszScan == '\0')
        {   /* No component left: the inode in hand is the path's -- the   */
            /* root's for "/".                                             */
            break;
        }

        /* The component runs to the next separator or the path's end. */
        pszName = pszScan;

        while (*pszScan != '\0' && *pszScan != '/')
        {   /* One byte of the component per iteration. */
            pszScan++;
        }

        /* Its length, the separator not included. */
        cbName = (size_t)(pszScan - pszName);

        if (!Bfq_IsDir(pInode))
        {   /* A name to look up inside something that is not a directory. */
            return(ENOTDIR);
        }

        /* The component's entry in the directory in hand. */
        rc = Bfq_FindInDir(pInode, pszName, cbName, &id);

        if (rc != 0)
        {   /* Not there (ENOENT), or the directory could not be read (EIO). */
            return(rc);
        }

        /* Its inode becomes the one in hand for the next component. */
        if (sqfs_inode_get(&Bfq_fs, pInode, id) != SQFS_OK)
        {   /* The entry names an inode that could not be read. */
            return(EIO);
        }
    }

    /* The path resolved; *pInode is its inode. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFQ_Mount:                                                                 */
/*                                                                            */
/* Mount the range BFD_Configure recorded when it holds a squashfs            */
/* filesystem.  Its first four bytes decide: squashfs begins with its magic,  */
/* so a range without it is not squashfs and is left at once, unopened, for   */
/* the caller's next try (FAT).  With the magic, the range IS squashfs, and   */
/* anything that stops it mounting is reported as this kind's failure --      */
/* never handed on to FAT, which would only say "no filesystem".              */
/*                                                                            */
/* squashfs cannot be written, so a writable open is refused -- after the     */
/* filesystem is known to mount, so a damaged image is reported as damaged    */
/* rather than as "open it read-only".                                        */
/*                                                                            */
/* Arguments:                                                                 */
/*     bReadOnly : true when the session is to be read-only.                  */
/*     pbFound   : receives true when the range begins with a squashfs magic. */
/*                                                                            */
/* Returns:                                                                   */
/*     int : with *pbFound false, BFE_OK -- nothing open and nothing recorded */
/*           (a range that could not be opened or read is left for the next   */
/*           try to report).  With *pbFound true: BFE_OK with the filesystem  */
/*           mounted and the target open; or, recorded with BFX_Fail and      */
/*           nothing left open, BFE_ERR_MOUNT (a damaged superblock or        */
/*           tables), BFE_ERR_UNSUPPORTED (another squashfs version, or a     */
/*           compression this build does not read), or BFE_ERR_READ_ONLY (a   */
/*           writable open).                                                  */
/*----------------------------------------------------------------------------*/
int BFQ_Mount(bool bReadOnly, bool *pbFound)
{
    uint8_t     abMagic[BFQ_MAGIC_BYTES];
    uint32_t    uiMagic;
    uint64_t    ullLength;
    const char *pszCompression;
    sqfs_err    err;
    int         iMajor;
    int         iMinor;
    int         iStatus;

    /* Not squashfs until its magic says so. */
    *pbFound = false;

    /* Open the target in the session's mode, learning the range's length.   */
    /* lwext4's failed mount closed it, so it is opened here afresh.         */
    if (BFD_TargetOpen(&ullLength) != 0)
    {   /* It cannot be opened.  Not this kind's to report: the next try   */
        /* opens it the same way and says why.                             */
        return(BFE_OK);
    }

    /* The reader context's one fact: how far a read may go. */
    Bfq_source.ullLength = ullLength;

    /* The first four bytes, through the same read squashfuse will use. */
    if (sqfs_pread(&Bfq_source, abMagic, BFQ_MAGIC_BYTES, 0) != (ssize_t)BFQ_MAGIC_BYTES)
    {   /* Shorter than a magic, or unreadable: not squashfs as far as can   */
        /* be told.  The target is closed again, as the next try expects.    */
        BFD_TargetClose();
        return(BFE_OK);
    }

    /* The magic as a little-endian word, squashfs 4.0's order ("hsqs" on      */
    /* disk).  Its byte-swapped form ("sqsh") is accepted too, as squashfuse   */
    /* accepts it: it is a squashfs written big-endian, which sqfs_init then   */
    /* reports by its version.                                                 */
    uiMagic = (uint32_t)abMagic[0] | ((uint32_t)abMagic[1] << 8) |
              ((uint32_t)abMagic[2] << 16) | ((uint32_t)abMagic[3] << 24);

    if (uiMagic != SQUASHFS_MAGIC && uiMagic != SQFS_MAGIC_SWAP)
    {   /* Not squashfs: closed again and left for the next try. */
        BFD_TargetClose();
        return(BFE_OK);
    }

    /* From here on the range is squashfs, whatever else is wrong with it. */
    *pbFound = true;

    /* squashfuse reads the superblock and checks it, chooses the              */
    /* decompressor, reads the id, fragment, export and attribute tables and   */
    /* makes its caches.  The offset is 0: sqfs_pread adds the range's own.    */
    err = sqfs_init(&Bfq_fs, &Bfq_source, 0);

    if (err != SQFS_OK)
    {   /* It does not mount.  sqfs_init has released whatever it made   */
        /* (only its table failure had made anything, and it destroys    */
        /* that itself), so only the target is left to close.            */
        switch (err)
        {   /* One reason per case, each worded for the user. */

            case SQFS_BADVERSION:
                /* Another version of the format -- squashfs before 4.0, or   */
                /* written big-endian -- which squashfuse does not read.      */
                sqfs_version(&Bfq_fs, &iMajor, &iMinor);
                iStatus = BFX_Fail(BFE_ERR_UNSUPPORTED, "squashfs version %d.%d: only version 4.0 is read", iMajor, iMinor);
                break;

            case SQFS_BADCOMP:
                /* A compression whose decompressor this build leaves out   */
                /* (lz4 and lzo), or one squashfs does not define.  The     */
                /* name is squashfuse's own ("zlib" for gzip); NULL for a   */
                /* number it has no name for.                               */
                pszCompression = sqfs_compression_name(sqfs_compression(&Bfq_fs));

                if (pszCompression == NULL)
                {   /* A compression number squashfs does not define. */
                    iStatus = BFX_Fail(BFE_ERR_UNSUPPORTED, "squashfs compressed by an unknown method (%d)",
                                       (int)sqfs_compression(&Bfq_fs));
                }
                else
                {   /* A known compression this build does not read. */
                    iStatus = BFX_Fail(BFE_ERR_UNSUPPORTED, "squashfs compressed with %s, which this build does not read "
                                       "(gzip, xz, lzma and zstd are read)", pszCompression);
                }
                break;

            case SQFS_BADFORMAT:
                /* The superblock could not be read, or its block size is     */
                /* not one squashfs allows: a damaged image, or a range cut   */
                /* short.                                                     */
                iStatus = BFX_Fail(BFE_ERR_MOUNT, "a squashfs magic is there, but its superblock is not valid (damaged, or cut short)");
                break;

            default:
                /* The tables could not be read, or memory ran out making   */
                /* the caches -- squashfuse reports both alike.             */
                iStatus = BFX_Fail(BFE_ERR_MOUNT, "squashfs found, but its tables could not be read (damaged, or cut short) "
                                   "or memory ran out");
                break;
        }

        /* The target is closed; nothing is left open. */
        BFD_TargetClose();
        return(iStatus);
    }

    if (!bReadOnly)
    {   /* A writable open of a filesystem nothing can write. */

        /* Refuse it, after undoing the mount: squashfuse's state is let go   */
        /* and the target closed, so nothing is left open.                    */
        sqfs_destroy(&Bfq_fs);
        iStatus = BFX_Fail(BFE_ERR_READ_ONLY, "the range holds a squashfs filesystem, which cannot be written; open it read-only");
        BFD_TargetClose();
        return(iStatus);
    }

    /* Mounted: the session's calls may use Bfq_fs until BFQ_Unmount. */
    Bfq_bMounted = true;
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFQ_Unmount:                                                               */
/*                                                                            */
/* Let the filesystem go: squashfuse's tables and caches are freed and the    */
/* target is closed.  Nothing was ever written, so nothing can fail here.     */
/* Safe to call when nothing is mounted (the target close is safe closed).    */
/*                                                                            */
/* Arguments:                                                                 */
/*     None.                                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     void : nothing is mounted or open afterwards.                          */
/*----------------------------------------------------------------------------*/
void BFQ_Unmount(void)
{
    if (Bfq_bMounted)
    {   /* A mounted filesystem: its state is freed, once. */
        sqfs_destroy(&Bfq_fs);
        Bfq_bMounted = false;
    }

    /* The file or device is closed. */
    BFD_TargetClose();
}

/*----------------------------------------------------------------------------*/
/* BFQ_VolumeInfo:                                                            */
/*                                                                            */
/* Describe the mounted filesystem for BFE_VolumeInfoGet, from its            */
/* superblock.  squashfs has no free space -- it is written whole and never   */
/* added to -- and no label; its size is the bytes the image uses, given as   */
/* blocks of its block size so the caller's size arithmetic is the one it     */
/* uses for every kind.  The feature words are ext4's and stay 0.             */
/*                                                                            */
/* Arguments:                                                                 */
/*     pInfo : receives the facts.                                            */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK -- everything comes from the superblock, already read.    */
/*----------------------------------------------------------------------------*/
int BFQ_VolumeInfo(BFE_VolumeInfo *pInfo)
{
    uint64_t ullBlockBytes;

    /* Everything not set below is 0: no free blocks or inodes, no feature   */
    /* words or flags, and an empty label (the zeros terminate it).          */
    memset(pInfo, 0, sizeof(*pInfo));

    /* The kind and the block size (a power of two, checked by sqfs_init). */
    pInfo->uiVolumeType = BFE_VOLTYPE_SQUASHFS;
    pInfo->uiBlockSize  = Bfq_fs.sb.block_size;

    /* The bytes the image uses, in whole blocks, rounded up: the last part   */
    /* of a block is used space too.  Divided before the rounding is added,   */
    /* so a byte count near the type's limit cannot wrap.                     */
    ullBlockBytes        = (uint64_t)Bfq_fs.sb.block_size;
    pInfo->ullBlockCount = Bfq_fs.sb.bytes_used / ullBlockBytes;

    if (Bfq_fs.sb.bytes_used % ullBlockBytes != 0)
    {   /* A part block at the end. */
        pInfo->ullBlockCount++;
    }

    /* The inode count, and the session's mode: always read-only. */
    pInfo->uiInodeCount = Bfq_fs.sb.inodes;
    pInfo->bReadOnly    = 1;

    /* The structure is filled. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFQ_StatEntry:                                                             */
/*                                                                            */
/* BFX_FillEntry's squashfs arm: one path's facts, laid out as a BFE_Entry.   */
/* squashfs keeps Unix facts as ext2/3/4 does, with two differences: one      */
/* time per inode (its modify time, given in all three fields), and owners    */
/* kept as indexes into the filesystem's id table, looked up here.            */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImagePath : the path to describe.                                   */
/*     pEntry       : the entry to fill -- zeroed and named by the caller, so */
/*                    on a failure it holds the name only.                    */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0; ENOENT / ENOTDIR as the lookup gives them; EIO when an inode  */
/*           or an owner could not be read.                                   */
/*----------------------------------------------------------------------------*/
int BFQ_StatEntry(const char *pszImagePath, BFE_Entry *pEntry)
{
    sqfs_inode inode;
    sqfs_id_t  uiUid;
    sqfs_id_t  uiGid;
    int        rc;

    /* The path's inode. */
    rc = Bfq_LookupPath(pszImagePath, &inode);

    if (rc != 0)
    {   /* Not there, through a non-directory, or unreadable. */
        return(rc);
    }

    /* The owner and group: the inode holds each as an index into the id    */
    /* table, whose entry is the number itself.  Both are read before the   */
    /* entry is filled, so a failure leaves it holding the name only.       */
    if (sqfs_id_get(&Bfq_fs, inode.base.uid, &uiUid) != SQFS_OK ||
        sqfs_id_get(&Bfq_fs, inode.base.guid, &uiGid) != SQFS_OK)
    {   /* An index past the table, or the table's block unreadable. */
        return(EIO);
    }

    /* The identity, the type (from the type field) and the 12 permission   */
    /* bits (the mode word holds only those on disk; squashfuse's OR of a   */
    /* type into it is masked away).                                        */
    pEntry->uiInode = inode.base.inode_number;
    pEntry->uiType  = Bfq_TypeFromInode(&inode);
    pEntry->uiMode  = (uint32_t)inode.base.mode & BFX_PERM_MASK;
    pEntry->uiUid   = (uint32_t)uiUid;
    pEntry->uiGid   = (uint32_t)uiGid;
    pEntry->uiLinks = (uint32_t)inode.nlink;

    /* The one time squashfs keeps, in all three fields, as FAT's one time   */
    /* is: a listing's modify time is right, and the other two are the       */
    /* nearest true answer there is.                                         */
    pEntry->uiAtime = inode.base.mtime;
    pEntry->uiMtime = inode.base.mtime;
    pEntry->uiCtime = inode.base.mtime;

    /* The size, by type: a file's bytes, a link's target length, a       */
    /* directory's listing size as squashfs records it; a node, pipe or   */
    /* socket has none (the zeroing gave 0).                              */
    switch (pEntry->uiType)
    {   /* One entry type per case. */

        case BFE_TYPE_FILE:
            /* A regular file's size, 64 bits in either form of inode. */
            pEntry->ullSize = inode.xtra.reg.file_size;
            break;

        case BFE_TYPE_SYMLINK:
            /* A link: the length of its target text, as on ext2/3/4. */
            pEntry->ullSize = (uint64_t)inode.xtra.symlink_size;
            break;

        case BFE_TYPE_DIR:
            /* A directory: the size its inode records, squashfs's own   */
            /* figure -- three more than its listing's bytes, which      */
            /* squashfuse's dir.c takes off to walk it.  Whether Linux   */
            /* shows the same figure is not established here.            */
            pEntry->ullSize = (uint64_t)inode.xtra.dir.dir_size;
            break;

        default:
            /* Nothing else has a size. */
            break;
    }

    /* The entry is complete. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFQ_CollectNames:                                                          */
/*                                                                            */
/* BFX_CollectNames's squashfs arm: every name in a directory, into a         */
/* malloc'd array of malloc'd names, under the same promise -- every name or, */
/* on any failure, none.  squashfs lists no "." or ".." entries, so there is  */
/* nothing to leave out.  Nothing stays open: squashfuse's walk holds no      */
/* handle, only a cursor in this function.                                    */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImageDir : the directory.                                           */
/*     pppszNames  : receives the array (NULL when the directory is empty).   */
/*     pnNames     : receives the count.                                      */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0 with every name collected; ENOENT / ENOTDIR / EIO from the     */
/*           lookup, ENOTDIR for a path that is not a directory, EIO when the */
/*           listing could not be read through, ENOMEM -- no names handed     */
/*           back on any of them.                                             */
/*----------------------------------------------------------------------------*/
int BFQ_CollectNames(const char *pszImageDir, char ***pppszNames, int *pnNames)
{
    sqfs_inode       inode;
    sqfs_dir         dir;
    sqfs_dir_entry   entry;
    sqfs_err         err;
    char           **ppszList;
    char           **ppszGrown;
    char            *pszName;
    size_t           cbName;
    int              nCapacity;
    int              nNewCapacity;
    int              nUsed;
    int              rc;
    char             szEntryName[SQUASHFS_NAME_LEN + 1];

    /* The outputs start as "no names", which is also what a failure leaves. */
    *pppszNames = NULL;
    *pnNames    = 0;

    /* The directory's inode. */
    rc = Bfq_LookupPath(pszImageDir, &inode);

    if (rc != 0)
    {   /* Not there, through a non-directory, or unreadable. */
        return(rc);
    }

    if (!Bfq_IsDir(&inode))
    {   /* The path names something other than a directory. */
        return(ENOTDIR);
    }

    /* Open the listing for a walk from its start. */
    if (sqfs_dir_open(&Bfq_fs, &inode, &dir, 0) != SQFS_OK)
    {   /* Its listing could not be opened: a damaged inode. */
        return(EIO);
    }

    /* No array yet: it is made at the first name, so an empty directory    */
    /* costs nothing and hands back NULL.  Each entry's name is read into   */
    /* the buffer, then copied out; the result starts as success, as        */
    /* sqfs_dir_next sets it on each call.                                  */
    ppszList  = NULL;
    nCapacity = 0;
    nUsed     = 0;
    err       = SQFS_OK;
    sqfs_dentry_init(&entry, szEntryName);

    while (sqfs_dir_next(&Bfq_fs, &dir, &entry, &err))
    {   /* One directory entry per iteration; the walk ends at the last one,   */
        /* or at a read that failed (told after the loop).                     */

        if (nUsed == nCapacity)
        {   /* The array is full, or not made yet: it grows. */

            /* The first array holds BFQ_NAMES_FIRST names; each later one   */
            /* twice the last, so the copying stays proportional to the      */
            /* names.                                                        */
            if (nCapacity == 0)
            {   /* The first name. */
                nNewCapacity = BFQ_NAMES_FIRST;
            }
            else
            {   /* Twice the room. */
                nNewCapacity = nCapacity * 2;
            }

            /* The bigger array, the names so far carried over by realloc. */
            ppszGrown = (char **)realloc(ppszList, (size_t)nNewCapacity * sizeof(char *));

            if (ppszGrown == NULL)
            {   /* Out of memory: the names so far are released (realloc    */
                /* left the old array as it was) and none is handed back.   */
                BFX_FreeNames(ppszList, nUsed);
                return(ENOMEM);
            }

            /* The array in use is the bigger one. */
            ppszList  = ppszGrown;
            nCapacity = nNewCapacity;
        }

        /* The entry's own copy of its name, terminated: squashfuse read the   */
        /* bytes into the buffer without a terminator of its own.              */
        cbName  = sqfs_dentry_name_size(&entry);
        pszName = (char *)malloc(cbName + 1);

        if (pszName == NULL)
        {   /* Out of memory: every name so far released, none handed back. */
            BFX_FreeNames(ppszList, nUsed);
            return(ENOMEM);
        }

        /* The name's bytes and the terminator, then into the list. */
        memcpy(pszName, szEntryName, cbName);
        pszName[cbName]   = '\0';
        ppszList[nUsed++] = pszName;
    }

    if (err != SQFS_OK)
    {   /* The walk stopped on a read that failed: the names so far are not   */
        /* the whole directory, so none is handed back.                       */
        BFX_FreeNames(ppszList, nUsed);
        return(EIO);
    }

    /* Every name, handed back with its count. */
    *pppszNames = ppszList;
    *pnNames    = nUsed;
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFQ_ReadLink:                                                              */
/*                                                                            */
/* A symbolic link's target, for bfe_fs.c's BFS_ReadLink: copied UNTERMINATED */
/* into the buffer, up to its size, as lwext4's ext4_readlink gives it on     */
/* ext2/3/4 -- the caller terminates it and has already checked the target    */
/* fits (BFE_ReadLink), or copies it into a manifest by length.  squashfuse's */
/* own read always terminates, so it reads the whole target into a buffer of  */
/* its own first and the bytes are copied from there.                         */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImagePath : the link.                                               */
/*     pszBuffer    : receives the target's bytes.                            */
/*     cbBuffer     : the most to copy.                                       */
/*     pcbRead      : receives how many were copied.                          */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0; ENOENT / ENOTDIR / EIO from the lookup; EINVAL when the path  */
/*           is not a link; ENOMEM; EIO when the target could not be read.    */
/*----------------------------------------------------------------------------*/
int BFQ_ReadLink(const char *pszImagePath, char *pszBuffer, size_t cbBuffer, size_t *pcbRead)
{
    sqfs_inode inode;
    char      *pszTarget;
    size_t     cbTarget;
    size_t     cbCopy;
    int        rc;

    /* Nothing copied until the target is in hand. */
    *pcbRead = 0;

    /* The link's inode. */
    rc = Bfq_LookupPath(pszImagePath, &inode);

    if (rc != 0)
    {   /* Not there, through a non-directory, or unreadable. */
        return(rc);
    }

    if (Bfq_TypeFromInode(&inode) != BFE_TYPE_SYMLINK)
    {   /* Not a link: it has no target, as lwext4 answers. */
        return(EINVAL);
    }

    /* The room the whole target needs: squashfuse answers a NULL buffer   */
    /* with the target's length plus its terminator.                       */
    cbTarget = 0;

    if (sqfs_readlink(&Bfq_fs, &inode, NULL, &cbTarget) != SQFS_OK)
    {   /* A guard: the inode is a link, so the size is always answered. */
        return(EIO);
    }

    /* A buffer of that size for squashfuse's terminated copy. */
    pszTarget = (char *)malloc(cbTarget);

    if (pszTarget == NULL)
    {   /* Out of memory. */
        return(ENOMEM);
    }

    if (sqfs_readlink(&Bfq_fs, &inode, pszTarget, &cbTarget) != SQFS_OK)
    {   /* The target's metadata could not be read; the buffer goes. */
        free(pszTarget);
        return(EIO);
    }

    /* The target's bytes without the terminator, held to the caller's room. */
    cbCopy = cbTarget - 1;

    if (cbCopy > cbBuffer)
    {   /* More than the caller's buffer takes: only what fits is copied. */
        cbCopy = cbBuffer;
    }

    /* Copy, count, and let squashfuse's copy go. */
    memcpy(pszBuffer, pszTarget, cbCopy);
    *pcbRead = cbCopy;
    free(pszTarget);

    /* The target is in the caller's buffer. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFQ_FileOpen:                                                              */
/*                                                                            */
/* Open a regular file for reading, for bfe_fs.c's BFS_FileOpen.  A squashfs  */
/* file needs nothing opened: the handle is its inode, read once here, and a  */
/* position, and every read goes to squashfuse with both.                     */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImagePath : the file.                                               */
/*     ppFile       : receives the handle, or NULL on failure.                */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0; ENOENT / ENOTDIR / EIO from the lookup; EISDIR for a          */
/*           directory; EINVAL for a link, node, pipe or socket; ENOMEM.      */
/*----------------------------------------------------------------------------*/
int BFQ_FileOpen(const char *pszImagePath, BFQ_strctFile **ppFile)
{
    BFQ_strctFile *pFile;
    sqfs_inode     inode;
    uint32_t       uiType;
    int            rc;

    /* Nothing handed back until the open succeeds. */
    *ppFile = NULL;

    /* The file's inode. */
    rc = Bfq_LookupPath(pszImagePath, &inode);

    if (rc != 0)
    {   /* Not there, through a non-directory, or unreadable. */
        return(rc);
    }

    /* Only a regular file has bytes to read. */
    uiType = Bfq_TypeFromInode(&inode);

    if (uiType == BFE_TYPE_DIR)
    {   /* A directory, refused as lwext4 refuses one. */
        return(EISDIR);
    }

    if (uiType != BFE_TYPE_FILE)
    {   /* A link, node, pipe or socket: nothing to read as a file. */
        return(EINVAL);
    }

    /* The handle: the inode, and the position at the file's start. */
    pFile = (BFQ_strctFile *)malloc(sizeof(*pFile));

    if (pFile == NULL)
    {   /* Out of memory for the handle. */
        return(ENOMEM);
    }

    /* Fill it and hand it back; BFQ_FileClose frees it. */
    pFile->inode  = inode;
    pFile->ullPos = 0;
    *ppFile       = pFile;
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFQ_FileRead:                                                              */
/*                                                                            */
/* Read from an open file at its position, which moves on by what was read.   */
/* The request is held to what is left of the file before squashfuse sees     */
/* it: squashfuse stops at the file's end by itself, but treats a read that   */
/* yields nothing as a failure, and this way a read at the end is simply 0.   */
/*                                                                            */
/* Arguments:                                                                 */
/*     pFile    : the handle.                                                 */
/*     pvBuffer : receives the bytes.                                         */
/*     cbWant   : the most to read.                                           */
/*     pcbRead  : receives how many were read; 0 at the file's end.           */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0, or EIO when a block could not be read or decompressed.        */
/*----------------------------------------------------------------------------*/
int BFQ_FileRead(BFQ_strctFile *pFile, void *pvBuffer, size_t cbWant, size_t *pcbRead)
{
    uint64_t   ullSize;
    uint64_t   ullLeft;
    sqfs_off_t cbRange;

    /* Nothing read yet. */
    *pcbRead = 0;

    /* The file's size, from the inode the open read. */
    ullSize = pFile->inode.xtra.reg.file_size;

    if (pFile->ullPos >= ullSize || cbWant == 0)
    {   /* At the end, or nothing asked for: 0 bytes, no failure. */
        return(0);
    }

    /* What is left of the file, and the request held to it. */
    ullLeft = ullSize - pFile->ullPos;
    cbRange = (sqfs_off_t)cbWant;

    if ((uint64_t)cbWant > ullLeft)
    {   /* More asked for than is left: only the rest. */
        cbRange = (sqfs_off_t)ullLeft;
    }

    /* squashfuse's read of the range: the blocks it covers are found from    */
    /* the inode's block list (through its index on a large file), read,      */
    /* decompressed and copied out, a hole given as zeros and the tail from   */
    /* its fragment.  It sets cbRange to what it copied.                      */
    if (sqfs_read_range(&Bfq_fs, &pFile->inode, (sqfs_off_t)pFile->ullPos, &cbRange, pvBuffer) != SQFS_OK)
    {   /* A block or its list could not be read, or would not decompress. */
        return(EIO);
    }

    /* The position moves on by what was read, which is handed back. */
    pFile->ullPos += (uint64_t)cbRange;
    *pcbRead       = (size_t)cbRange;
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFQ_FileClose:                                                             */
/*                                                                            */
/* Close a handle: nothing was opened for it and nothing written, so only     */
/* the handle itself is freed.                                                */
/*                                                                            */
/* Arguments:                                                                 */
/*     pFile : the handle, or NULL (nothing to do).                           */
/*                                                                            */
/* Returns:                                                                   */
/*     void : the handle is gone.                                             */
/*----------------------------------------------------------------------------*/
void BFQ_FileClose(BFQ_strctFile *pFile)
{
    /* free(NULL) does nothing, so a handle never made needs no test. */
    free(pFile);
}
