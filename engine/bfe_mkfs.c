/*----------------------------------------------------------------------------*/
/* bfe_mkfs.c                                                                 */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* BFE_MakeFs: create a new image file of a chosen size and write an EMPTY    */
/* ext2 / ext3 / ext4 filesystem into it, using the bundled lwext4 library's  */
/* own formatter (ext4_mkfs).  BFE_MakeFsAt: the same formatting over a byte  */
/* range of a file or device that EXISTS -- a partition of a card, an eMMC    */
/* or a disk image -- in place, with the guards a write to a device needs.    */
/*                                                                            */
/* FAT12/16, FAT32 AND exFAT.  Both entries also make a FAT volume, through   */
/* FatFs's formatter in bfe_fat.c (BFT_CheckMakeFs before anything is         */
/* written, BFT_MakeFs for the format and the label).  Everything around the  */
/* formatter -- the gate, the file's creation and its removal on failure, the */
/* range checks, the wipe or zero-fill, a device's system-disk refusal, its   */
/* volume locks and the read-back of its writes -- is this file's and is the  */
/* same for both kinds; the rest of this header is about the ext formatter.   */
/*                                                                            */
/* WHAT THE FORMATTER DOES AND DOES NOT DO.  lwext4's ext4_mkfs lays down the */
/* superblock, the group descriptors, the bitmaps and inode tables, the root  */
/* directory and lost+found.  It does NOT create a journal, whatever type is  */
/* asked for, and it never turns on metadata_csum, flex_bg or meta_bg.  So    */
/* the three types differ in their feature flags only:                        */
/*                                                                            */
/*     ext2 : filetype, sparse_super, large_file                              */
/*     ext3 : the ext2 set plus dir_index -- but with no journal              */
/*     ext4 : the ext3 set plus extents, 64bit, uninit_bg (group descriptor   */
/*            checksums), dir_nlink, extra_isize, huge_file -- no journal     */
/*                                                                            */
/* A journal can be added afterwards on Linux (tune2fs -j).                   */
/*                                                                            */
/* WHAT IS PUT RIGHT AFTERWARDS, AND WHY IT IS DONE HERE.  As the formatter   */
/* leaves it, a filesystem is not one e2fsck accepts: every group is counted  */
/* as a FULL group, so a short last group claims free blocks it does not      */
/* have; the reserved inodes (quota, resize, journal and the rest) are left   */
/* holding an extent header where e2fsck requires them empty; the UUID is all */
/* zeros, so every image would carry the same identity; and the root and      */
/* lost+found are mode 0777 with a 1970 date.  BFK_Repair corrects each of    */
/* those through lwext4's own block-group and inode accessors.  It is done    */
/* after the formatter, in this file, which kept the bundled lwext4 source as */
/* published when it was written.  That no longer holds: lwext4 now carries   */
/* numbered "BifrostFM patch" blocks, two of them in the formatter itself     */
/* (patch 9, the incompatible features a new ext4 gets; patch 33, the inode   */
/* count rounded down to a multiple of 8).  The repairs here have not been    */
/* moved into it.                                                             */
/*                                                                            */
/* THIS FILE INCLUDES windows.h, as several engine files do.  The image file  */
/* is created with the Win32 calls rather than the C runtime for one reason:  */
/* SetEndOfFile gives a file its full size at once, where the C runtime's way */
/* of extending a file writes every byte of it as zeros -- minutes for an     */
/* image of several gigabytes.  The bytes lwext4 does not write read back as  */
/* zeros either way.                                                          */
/*----------------------------------------------------------------------------*/

#define _CRT_RAND_S                      // ask the C runtime's stdlib.h for rand_s, its operating-system random number source

/* Windows: the image file's creation at its full size (CreateFileW,   */
/* SetFilePointerEx, SetEndOfFile) and its removal on failure.         */
#include <windows.h>

/* lwext4: its formatter and what the repair reaches through -- the          */
/* filesystem object, the superblock, the inode and block-group accessors,   */
/* the bitmap test, the block cache and the block layer.                     */
#include <ext4.h>
#include <ext4_config.h>
#include <ext4_fs.h>
#include <ext4_mkfs.h>
#include <ext4_types.h>
#include <ext4_super.h>
#include <ext4_inode.h>
#include <ext4_bitmap.h>
#include <ext4_bcache.h>
#include <ext4_blockdev.h>
#include <ext4_block_group.h>

/* The C library: rand_s, malloc and free (stdlib.h, with _CRT_RAND_S   */
/* above), memory and strerror (string.h), ENOMEM (errno.h -- which     */
/* lwext4's ext4_errno.h brings in as well), and the clock (time.h).    */
/* stdio.h is not used in this file.                                    */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <errno.h>
#include <time.h>

/* The engine's own: the gate and error recorder, the block device, the    */
/* raw I/O layer (the range's open, the wipe, the guards and the locks),   */
/* and the FAT formatter.                                                  */
#include "bfe_internal.h"
#include "bfe_blockdev.h"
#include "bfe_device.h"
#include "bfe_fat.h"

/*----------------------------------------------------------------------------*/
/* Definitions -- documentation table.                                        */
/*----------------------------------------------------------------------------*/
#define BFK_WIPE_BYTES        4096u                         // BFE_MakeFsAt zeroes this much at the start of the range before formatting: the boot sector and any signature of what was there (a sector multiple for 512- and 4096-byte sectors, and under the size floor)
#define BFK_ZERO_CHUNK        (4u * 1024u * 1024u)          // with BFE_MAKEFS_ZERO, the whole range is zeroed in writes of this size, one progress event each (4 MiB: a sector multiple for any sector size, and the size BFE_CopyRaw reports by)
#define BFK_SMALL_FS_BYTES    (512ull * 1024ull * 1024ull)  // below this size the default block is 1024 bytes, as mke2fs chooses; at or above it, 4096
#define BFK_BLOCK_SMALL       1024u                         // default block size for a small filesystem, bytes
#define BFK_BLOCK_MEDIUM      2048u                         // the block size chosen for an ext4 filesystem too small for 1024-byte blocks
#define BFK_BLOCK_LARGE       4096u                         // default block size otherwise, bytes
#define BFK_EXT4_1K_MIN_BYTES (8ull * 1024ull * 1024ull)    // ext4 with 1024-byte blocks below this -- less than one full block group -- fails in lwext4's formatter (it reaches for a block past the end); found by test, every other type and block size formats from 1 MiB up
#define BFK_LABEL_MAX         16                            // an ext volume label is at most 16 bytes
#define BFK_LABEL_ROOM        (BFE_VOLUME_NAME_BYTES + 1)   // room for the options' label copied out and terminated, whatever its type: the whole field (an exFAT label can fill it) and a NUL
#define BFK_FILE_SECTOR_BYTES 512u                          // the sector a new image FILE counts in (as BFE_Open counts a file): what a FAT volume made by BFE_MakeFs is laid out in

#define BFK_ROOT_INODE        2u                            // the root directory's inode number, fixed by the format
#define BFK_FIRST_INODE       11u                           // the first non-reserved inode; the formatter makes it lost+found
#define BFK_ROOT_MODE         0755u                         // permission bits given to the root directory, as mke2fs gives
#define BFK_LOSTFOUND_MODE    0700u                         // permission bits given to lost+found, as mke2fs gives
#define BFK_UUID_BYTES        16                            // a superblock UUID
#define BFK_UUID_VERSION_4    0x40                          // the version nibble of a random (version 4) UUID, in byte 6
#define BFK_UUID_VARIANT      0x80                          // the variant bits of an RFC 4122 UUID, in byte 8

#define BFK_CREATE_OK         0  // BFK_CreateSized: the file exists at its full size
#define BFK_CREATE_EXISTS     1  // BFK_CreateSized: the file is already there and overwriting was not allowed
#define BFK_CREATE_FAILED     2  // BFK_CreateSized: Windows refused; *puiError holds its error code

/*----------------------------------------------------------------------------*/
/* The filesystem object lwext4's formatter works in.  It is large, so it is  */
/* kept out of the stack; only the call holding the busy flag touches it.     */
/*----------------------------------------------------------------------------*/
static struct ext4_fs BFK_fs;    // scratch state for ext4_mkfs; meaningless between calls

/*----------------------------------------------------------------------------*/
/* BFK_CreateSized:                                                           */
/*                                                                            */
/* Create the image file and give it its full size, without writing its       */
/* bytes.  An existing file is replaced only when overwriting was allowed.    */
/*                                                                            */
/* Arguments:                                                                 */
/*     pwszImage  : the image file's path.                                    */
/*     ullBytes   : the size to give it.                                      */
/*     bOverwrite : TRUE to replace a file that is already there.             */
/*     puiError   : receives the Win32 error code on BFK_CREATE_FAILED.       */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFK_CREATE_OK, BFK_CREATE_EXISTS or BFK_CREATE_FAILED.  On       */
/*           anything but OK no file is left behind that was not there        */
/*           before.                                                          */
/*----------------------------------------------------------------------------*/
static int BFK_CreateSized(const wchar_t *pwszImage, uint64_t ullBytes, int bOverwrite, uint32_t *puiError)
{
    HANDLE        hFile;
    LARGE_INTEGER liSize;
    DWORD         dwDisposition;
    DWORD         dwError;

    /* No Windows error unless one happens. */
    *puiError = 0;

    if (bOverwrite)
    {   /* Replace whatever is there, emptied: the truncation is what makes   */
        /* the new image read back as zeros wherever the formatter does not   */
        /* write.                                                             */
        dwDisposition = CREATE_ALWAYS;
    }
    else
    {   /* Only ever a new file, so an existing one can never be replaced by   */
        /* accident.                                                           */
        dwDisposition = CREATE_NEW;
    }

    /* Opened unshared: nothing else may use the file while it is sized. */
    hFile = CreateFileW(pwszImage, GENERIC_READ | GENERIC_WRITE, 0, NULL, dwDisposition, FILE_ATTRIBUTE_NORMAL, NULL);

    if (hFile == INVALID_HANDLE_VALUE)
    {   /* Already there, or the folder cannot be written. */

        /* Which, from Windows' own code. */
        dwError = GetLastError();

        if (dwError == ERROR_FILE_EXISTS || dwError == ERROR_ALREADY_EXISTS)
        {   /* CREATE_NEW met an existing file: told apart from other        */
            /* refusals so the caller can say "it exists" rather than give   */
            /* a Windows number.  Nothing was opened, so nothing to undo.    */
            return(BFK_CREATE_EXISTS);
        }

        /* Any other refusal: hand its code back. */
        *puiError = (uint32_t)dwError;
        return(BFK_CREATE_FAILED);
    }

    /* Move the end-of-file mark to the full size.  Nothing is written: the   */
    /* file system hands back zeros for every byte never stored.              */
    liSize.QuadPart = (LONGLONG)ullBytes;

    if (!SetFilePointerEx(hFile, liSize, NULL, FILE_BEGIN) || !SetEndOfFile(hFile))
    {   /* Not enough room on the disk, or a size the file system cannot hold. */

        /* The error first (CloseHandle could overwrite it), then remove   */
        /* the file just made.  An overwritten file is gone too: it was    */
        /* emptied when it was opened.                                     */
        *puiError = (uint32_t)GetLastError();
        CloseHandle(hFile);
        DeleteFileW(pwszImage);
        return(BFK_CREATE_FAILED);
    }

    /* Sized; the block device opens it again for the formatter -- which is   */
    /* why this handle is closed now: opened unshared, it would make that     */
    /* second open fail with a sharing violation.                             */
    CloseHandle(hFile);

    /* The file exists, empty of data, at its full size. */
    return(BFK_CREATE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFK_RandomWord:                                                            */
/*                                                                            */
/* Thirty-two random bits from the operating system (rand_s), for the UUID    */
/* and the directory hash seed, with a weaker stand-in if that source fails.  */
/*                                                                            */
/* Arguments:                                                                 */
/*     None.                                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     uint32_t : the random word.                                            */
/*----------------------------------------------------------------------------*/
static uint32_t BFK_RandomWord(void)
{
    static uint32_t uiFallback;    // steps on every fallback, so two calls in one second still differ
    unsigned int    uiValue;

    if (rand_s(&uiValue) != 0)
    {   /* The system source is unavailable (it does not fail, in practice). */

        /* The clock and a counter stand in: a poorer number, but still one   */
        /* that differs from image to image, which is all either use needs.   */
        /* Step the counter (a linear congruential step, the common           */
        /* 1664525 / 1013904223 pair) and mix in the clock.                   */
        uiFallback = uiFallback * 1664525u + 1013904223u;
        uiValue    = (unsigned int)time(NULL) ^ uiFallback;
    }

    /* Thirty-two bits. */
    return((uint32_t)uiValue);
}

/*----------------------------------------------------------------------------*/
/* BFK_RepairGroups:                                                          */
/*                                                                            */
/* Give every block group its TRUE free-block count and the superblock their  */
/* sum, with every group descriptor's checksum recomputed over the new UUID.  */
/*                                                                            */
/* Arguments:                                                                 */
/*     pFs : the filesystem, initialised on the new image.                    */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0, or an lwext4 errno-style code.                                */
/*----------------------------------------------------------------------------*/
static int BFK_RepairGroups(struct ext4_fs *pFs)
{
    struct ext4_block_group_ref ref;
    struct ext4_block           blkBitmap;
    uint64_t                    ullBlocks;
    uint64_t                    ullFreeTotal;
    uint32_t                    uiFirstData;
    uint32_t                    uiPerGroup;
    uint32_t                    uiGroupBlocks;
    uint32_t                    uiFree;
    uint32_t                    nGroups;
    uint32_t                    iGroup;
    uint32_t                    iBit;
    int                         rc;

    /* The geometry from the superblock: the total blocks, where block 0     */
    /* of group 0 is (block 1 with 1 KiB blocks, else block 0), the blocks   */
    /* per group and the group count; and nothing counted free yet.          */
    ullBlocks    = ext4_sb_get_blocks_cnt(&pFs->sb);
    uiFirstData  = ext4_get32(&pFs->sb, first_data_block);
    uiPerGroup   = ext4_get32(&pFs->sb, blocks_per_group);
    nGroups      = ext4_block_group_cnt(&pFs->sb);
    ullFreeTotal = 0;

    for (iGroup = 0; iGroup < nGroups; iGroup++)
    {   /* One block group per iteration. */

        /* Every group but the last is full; the last holds what is left. */
        uiGroupBlocks = uiPerGroup;

        if (iGroup == nGroups - 1)
        {   /* The last group: the blocks from its first one to the end. */
            uiGroupBlocks = (uint32_t)(ullBlocks - uiFirstData - (uint64_t)iGroup * uiPerGroup);
        }

        /* The group's descriptor (the PRIMARY copy; BFK_Repair's STEP 6     */
        /* copies the repaired table to every backup group afterwards),      */
        /* then its block bitmap.                                            */
        /*                                                                   */
        /* THE GET DOES MORE THAN READ, AND THE COUNT BELOW DEPENDS ON IT.   */
        /* The formatter leaves EVERY group flagged BLOCK_UNINIT and         */
        /* INODE_UNINIT with zeroed bitmaps (ext4_mkfs.c, fill_bgroups /     */
        /* write_bgroups), so a raw count of such a bitmap would call the    */
        /* group's own superblock copy, descriptors, bitmaps and inode       */
        /* table free.  ext4_fs_get_block_group_ref initialises an uninit    */
        /* group's bitmaps -- marking those blocks in use -- writes its      */
        /* inode table zeroed, and clears both flags (ext4_fs.c), so the     */
        /* bitmap read next is the true one.  It is also why a large image   */
        /* takes a moment here: every group's inode table is written.        */
        rc = ext4_fs_get_block_group_ref(pFs, iGroup, &ref);

        if (rc != 0)
        {   /* The descriptor could not be read (or the uninit group could   */
            /* not be initialised): the get has released its own block, so   */
            /* there is nothing to put.  Groups already done stay done.      */
            return(rc);
        }

        /* The group's block bitmap, through the cache. */
        rc = ext4_block_get(pFs->bdev, &blkBitmap, ext4_bg_get_block_bitmap(ref.block_group, &pFs->sb));

        if (rc != 0)
        {   /* The bitmap could not be read.  The descriptor is the one thing   */
            /* held; it is put back unchanged -- not marked dirty, so the put   */
            /* only releases its block (ext4_fs.c) -- and the failure passed    */
            /* on.  Groups already done stay done; the format fails anyway.     */
            ext4_fs_put_block_group_ref(&ref);
            return(rc);
        }

        /* Count the clear bits over the blocks the group REALLY has -- the   */
        /* bits past a short last group's end are padding, not free blocks.   */
        uiFree = 0;

        for (iBit = 0; iBit < uiGroupBlocks; iBit++)
        {   /* One block of the group per iteration. */
            if (ext4_bmap_is_bit_clr(blkBitmap.data, iBit))
            {   /* Not in use. */
                uiFree++;
            }
        }

        /* The bitmap was only read: hand the block back to the cache. */
        rc = ext4_block_set(pFs->bdev, &blkBitmap);

        if (rc != 0)
        {   /* The bitmap block could not be released.  As above, the          */
            /* descriptor is put back unchanged and the failure passed on --   */
            /* the count just made is not written.                             */
            ext4_fs_put_block_group_ref(&ref);
            return(rc);
        }

        /* Marking it dirty is what makes the put recompute the checksum.   */
        /* Every group is rewritten, even one whose count was already       */
        /* right, because a group descriptor's checksum covers the          */
        /* superblock's UUID, and the UUID has just been changed.           */
        ext4_bg_set_free_blocks_count(ref.block_group, &pFs->sb, uiFree);
        ref.dirty = true;

        /* The put writes the descriptor back with its new checksum. */
        rc = ext4_fs_put_block_group_ref(&ref);

        if (rc != 0)
        {   /* The descriptor's block could not be put back.  The put has   */
            /* already given its block up either way, so nothing is held;   */
            /* the failure is passed on and the format fails.               */
            return(rc);
        }

        /* Into the filesystem's total. */
        ullFreeTotal += uiFree;
    }

    /* The superblock's free count is the groups' sum (written to disk when   */
    /* the caller closes the filesystem object).                              */
    ext4_sb_set_free_blocks_cnt(&pFs->sb, ullFreeTotal);

    /* Every group and the superblock's total agree with the bitmaps. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFK_RepairInodes:                                                          */
/*                                                                            */
/* Empty the reserved inodes and dress the two directories.  Inodes 1 to 10   */
/* other than the root are reserved by the format (bad blocks, quotas, the    */
/* resize inode, the journal ...); none is in use in a filesystem made here,  */
/* and e2fsck requires an unused one to be all zeros.  The root and           */
/* lost+found get the modes mke2fs gives them and the present time.           */
/*                                                                            */
/* Arguments:                                                                 */
/*     pFs    : the filesystem, initialised on the new image.                 */
/*     uiTime : the time to stamp the two directories with, epoch seconds.    */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0, or an lwext4 errno-style code.                                */
/*----------------------------------------------------------------------------*/
static int BFK_RepairInodes(struct ext4_fs *pFs, uint32_t uiTime)
{
    struct ext4_inode_ref ref;
    uint32_t              uiInodeSize;
    uint32_t              uiMode;
    uint32_t              iInode;
    int                   rc;

    /* The on-disk inode's size, from the superblock: what "the whole   */
    /* inode" is (larger than the structure lwext4 names).              */
    uiInodeSize = ext4_get16(&pFs->sb, inode_size);

    for (iInode = 1; iInode <= BFK_FIRST_INODE; iInode++)
    {   /* One of the first eleven inodes per iteration. */

        /* The inode, read for changing. */
        rc = ext4_fs_get_inode_ref(pFs, iInode, &ref);

        if (rc != 0)
        {   /* The inode could not be read: nothing is held (the get         */
            /* releases its block on failure), and the inodes already done   */
            /* stay done -- the caller fails the whole format anyway.        */
            return(rc);
        }

        if (iInode == BFK_ROOT_INODE || iInode == BFK_FIRST_INODE)
        {   /* A directory: keep its contents, set its mode and times. */

            /* The root's mode, unless this is lost+found. */
            uiMode = BFK_ROOT_MODE;

            if (iInode == BFK_FIRST_INODE)
            {   /* lost+found is private to root. */
                uiMode = BFK_LOSTFOUND_MODE;
            }

            /* The type bits stay "directory"; all three times are the      */
            /* present, the change time included -- written straight into   */
            /* the inode here, as lwext4's formatter leaves them 0.         */
            ext4_inode_set_mode(&pFs->sb, ref.inode, EXT4_INODE_MODE_DIRECTORY | uiMode);
            ext4_inode_set_access_time(ref.inode, uiTime);
            ext4_inode_set_change_inode_time(ref.inode, uiTime);
            ext4_inode_set_modif_time(ref.inode, uiTime);
        }
        else
        {   /* Reserved and unused: all zeros, the whole on-disk inode.  The   */
            /* size comes from the superblock, not sizeof: ref.inode points    */
            /* into the inode table's block, where each inode occupies the     */
            /* full on-disk size, so the whole of it is cleared and nothing    */
            /* past it.                                                        */
            memset(ref.inode, 0, uiInodeSize);
        }

        /* Dirty, so the put writes the inode back (no inode checksum:   */
        /* metadata_csum is off).                                        */
        ref.dirty = true;
        rc        = ext4_fs_put_inode_ref(&ref);

        if (rc != 0)
        {   /* The inode could not be written: the put has released its   */
            /* block either way, so there is nothing more to release.     */
            return(rc);
        }
    }

    /* The reserved inodes are empty and the directories are dressed. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFK_Repair:                                                                */
/*                                                                            */
/* Put right what lwext4's formatter leaves wrong (this file's header lists   */
/* what and why), on the device brought up as the formatter itself brings it  */
/* up, and rewrite the backup superblocks and the backup group-descriptor     */
/* tables from the repaired primary, so a recovery from a backup finds the    */
/* same UUID and the same counts.  The device is closed again on every exit.  */
/*                                                                            */
/* Arguments:                                                                 */
/*     pDevice : the block device, configured on the new image and closed.    */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0, or an lwext4 errno-style code.                                */
/*----------------------------------------------------------------------------*/
static int BFK_Repair(struct ext4_blockdev *pDevice)
{
    struct ext4_bcache cache;
    uint64_t           ullOffset;
    uint64_t           ullTableBytes;
    uint8_t           *pbTable;
    uint32_t           uiBlockSize;
    uint32_t           uiPerGroup;
    uint32_t           uiFirstData;
    uint32_t           uiNow;
    uint32_t           nGroups;
    uint32_t           iGroup;
    int                rc;
    int                rcDown;
    int                i;

    /* STEP 1: the block layer, with the filesystem's own block size read    */
    /* from the superblock the formatter wrote.  Up in the formatter's own   */
    /* order -- block layer, a block cache in write-back mode, then the      */
    /* filesystem object -- and torn down in the reverse order in STEP 5.    */
    rc = ext4_block_init(pDevice);

    if (rc != 0)
    {   /* The image file could not be reopened: nothing is open (a failed   */
        /* init leaves the device closed), so there is nothing to undo.      */
        return(rc);
    }

    /* A clean filesystem object, which the device points back at (lwext4   */
    /* reaches the filesystem through its device).                          */
    memset(&BFK_fs, 0, sizeof(BFK_fs));
    pDevice->fs = &BFK_fs;

    /* The superblock the formatter wrote, into the object. */
    rc = ext4_sb_read(pDevice, &BFK_fs.sb);

    if (rc != 0)
    {   /* No superblock to work from (the formatter's write did not hold).   */
        /* The block layer is the one thing up -- no cache, no filesystem     */
        /* object -- so closing it, which closes the device, is the whole     */
        /* release; BFK_fs is static scratch and needs no undoing.            */
        ext4_block_fini(pDevice);
        return(rc);
    }

    /* The size every later block access uses. */
    uiBlockSize = ext4_sb_get_block_size(&BFK_fs.sb);

    /* STEP 2: a block cache, bound and in write-back mode.  First the block   */
    /* layer is told the filesystem's block size: lwext4 then counts blocks    */
    /* of that size over the partition (ext4_block_set_lb_size), so every      */
    /* block number below means a filesystem block, as the formatter did.      */
    memset(&cache, 0, sizeof(cache));
    rc = ext4_block_set_lb_size(pDevice, uiBlockSize);

    if (rc != 0)
    {   /* A guard only: lwext4 refuses a size that is 0 or not a multiple     */
        /* of the 512-byte physical sector, and the block size the formatter   */
        /* just wrote is 1, 2 or 4 KiB.  Were it refused, the block layer is   */
        /* the one thing up, so closing it is the whole release.               */
        ext4_block_fini(pDevice);
        return(rc);
    }

    /* The cache itself: lwext4's configured number of blocks of that size. */
    rc = ext4_bcache_init_dynamic(&cache, CONFIG_BLOCK_DEV_CACHE_SIZE, uiBlockSize);

    if (rc != 0)
    {   /* A guard only: in this lwext4 the init refuses only a zero count     */
        /* or size, neither possible here -- it records the two and returns    */
        /* EOK, allocating nothing (ext4_bcache.c; the cache's blocks are      */
        /* allocated as they are used).  Were it to fail, the block layer is   */
        /* the one thing up, so closing it -- which closes the device -- is    */
        /* the whole release.                                                  */
        ext4_block_fini(pDevice);
        return(rc);
    }

    /* Bind the cache.  From here every exit runs STEP 5, which frees it.   */
    /* The bind cannot actually fail -- it only links the two and returns   */
    /* EOK (ext4_blockdev.c) -- so STEP 5's drain never meets a device      */
    /* with no cache bound.                                                 */
    rc = ext4_block_bind_bcache(pDevice, &cache);

    if (rc == 0)
    {   /* Bound: batch the writes.  Write-back holds changed blocks in the    */
        /* cache until it is turned off again in STEP 5, which flushes them    */
        /* (ext4_block_cache_write_back, ext4_blockdev.c) -- so a descriptor   */
        /* block shared by many groups is written once at the drain rather     */
        /* than once per group, as the formatter itself works.                 */
        rc = ext4_block_cache_write_back(pDevice, 1);
    }

    /* STEP 3: the filesystem object, and the repairs. */
    if (rc == 0)
    {   /* The cache is up: the filesystem object over it, writable --      */
        /* writable matters, because a read-only object would leave every   */
        /* uninit group as it is (BFK_RepairGroups relies on the init).     */
        /* A failed init has no ext4_fs_fini: the object never came up.     */
        rc = ext4_fs_init(&BFK_fs, pDevice, false);
    }

    if (rc == 0)
    {   /* The filesystem is up: repair it. */

        /* One "now" for the superblock and both directories. */
        uiNow = (uint32_t)time(NULL);

        /* The superblock first: the group checksums written below cover   */
        /* its UUID.  A random (version 4) UUID, a random hash seed, the   */
        /* creation time, and no checksum algorithm named (metadata_csum   */
        /* is off).                                                        */
        for (i = 0; i < BFK_UUID_BYTES; i++)
        {   /* One UUID byte per iteration. */
            BFK_fs.sb.uuid[i] = (uint8_t)(BFK_RandomWord() & 0xFFu);
        }

        /* Stamp it as version 4 (random) and the RFC 4122 variant: the   */
        /* top nibble of byte 6 and the top two bits of byte 8.           */
        BFK_fs.sb.uuid[6] = (uint8_t)((BFK_fs.sb.uuid[6] & 0x0F) | BFK_UUID_VERSION_4);
        BFK_fs.sb.uuid[8] = (uint8_t)((BFK_fs.sb.uuid[8] & 0x3F) | BFK_UUID_VARIANT);

        for (i = 0; i < 4; i++)
        {   /* One word of the directory hash seed per iteration. */
            BFK_fs.sb.hash_seed[i] = to_le32(BFK_RandomWord());
        }

        /* Made and last written now; no checksum algorithm named. */
        BFK_fs.sb.mkfs_time     = to_le32(uiNow);
        BFK_fs.sb.write_time    = to_le32(uiNow);
        BFK_fs.sb.checksum_type = 0;

        /* The groups' free counts (and their checksums, over the new UUID). */
        rc = BFK_RepairGroups(&BFK_fs);

        if (rc == 0)
        {   /* The groups are right: now the inodes. */
            rc = BFK_RepairInodes(&BFK_fs, uiNow);
        }

        /* Closing the filesystem object writes the primary superblock. */
        rcDown = ext4_fs_fini(&BFK_fs);

        if (rc == 0)
        {   /* The repairs worked; report the close's own result. */
            rc = rcDown;
        }
    }

    /* STEP 4: the backup superblocks, copied from the primary as it now     */
    /* stands.  With sparse_super they sit at the start of groups 1 and      */
    /* the powers of 3, 5 and 7; each records its own group's number.        */
    /* sparse_super is always on here -- every type lwext4's formatter       */
    /* makes sets it (EXT2 / EXT3 / EXT4_SUPPORTED_FRO_COM, ext4_types.h)    */
    /* -- so ext4_sb_sparse alone is the test, here and in STEP 6, without   */
    /* the feature check ext4_sb_is_super_in_bg adds.                        */
    if (rc == 0)
    {   /* The primary is written.  The in-memory copy is still valid after   */
        /* the close, and is what the backups are made from.                  */
        uiPerGroup  = ext4_get32(&BFK_fs.sb, blocks_per_group);
        uiFirstData = ext4_get32(&BFK_fs.sb, first_data_block);
        nGroups     = ext4_block_group_cnt(&BFK_fs.sb);

        for (iGroup = 1; iGroup < nGroups && rc == 0; iGroup++)
        {   /* One group per iteration; most hold no backup. */
            if (!ext4_sb_sparse(iGroup))
            {   /* No superblock copy in this group. */
                continue;
            }

            /* The group's first block; a backup starts there (only the        */
            /* primary sits 1024 bytes in).  Written straight to the device,   */
            /* past the cache -- the repairs never touched these blocks.       */
            ullOffset = (uint64_t)uiBlockSize * ((uint64_t)uiFirstData + (uint64_t)iGroup * uiPerGroup);

            /* The copy, stamped with this group's number, written there.   */
            /* The field is 16 bits on disk, so past 65 535 groups (8 TiB   */
            /* at 4 KiB blocks) the number wraps -- far beyond any card.    */
            BFK_fs.sb.block_group_index = to_le16((uint16_t)iGroup);
            rc = ext4_block_writebytes(pDevice, ullOffset, &BFK_fs.sb, EXT4_SUPERBLOCK_SIZE);
        }
    }

    /* STEP 5: tear down in reverse.  Turning write-back off drains the   */
    /* cache to the file.                                                 */
    rcDown = ext4_block_cache_write_back(pDevice, 0);

    if (rc == 0)
    {   /* Nothing failed before the drain; report the drain's result. */
        rc = rcDown;
    }

    /* STEP 6: the backup DESCRIPTOR tables, copied from the primary now    */
    /* that the drain has put the repaired primary on the device (taken     */
    /* before it, the copy could miss repairs still in the cache).  Each    */
    /* group holding a backup superblock holds a copy of the table in the   */
    /* blocks right after it, as the primary does; left as the formatter    */
    /* wrote them, they disagree with the primary, and a recovery from a    */
    /* backup would bring the old counts back.  The formatter never sets    */
    /* meta_bg, so the table lies whole after each superblock.  STEP 4      */
    /* ran whenever rc is still 0 here, so its group figures are set.       */
    if (rc == 0)
    {   /* Everything written: the table's size, rounded up to whole   */
        /* blocks, then read once from the primary.                    */
        ullTableBytes = ((uint64_t)nGroups * ext4_sb_get_desc_size(&BFK_fs.sb) + uiBlockSize - 1) / uiBlockSize * uiBlockSize;
        pbTable       = (uint8_t *)malloc((size_t)ullTableBytes);

        if (pbTable == NULL)
        {   /* No room for the copy: the format fails with ENOMEM, which   */
            /* both callers report as BFE_ERR_NO_MEMORY.                   */
            rc = ENOMEM;
        }
        else
        {   /* The primary table: the block after the primary superblock's. */
            rc = ext4_block_readbytes(pDevice, (uint64_t)uiBlockSize * ((uint64_t)uiFirstData + 1), pbTable, (uint32_t)ullTableBytes);

            for (iGroup = 1; iGroup < nGroups && rc == 0; iGroup++)
            {   /* One group per iteration; only those holding a backup. */
                if (!ext4_sb_sparse(iGroup))
                {   /* No backup in this group. */
                    continue;
                }

                /* The block after this group's backup superblock, then the   */
                /* table written there; a failed write stops the loop and     */
                /* is the format's result.                                    */
                ullOffset = (uint64_t)uiBlockSize * ((uint64_t)uiFirstData + (uint64_t)iGroup * uiPerGroup + 1);
                rc = ext4_block_writebytes(pDevice, ullOffset, pbTable, (uint32_t)ullTableBytes);
            }

            /* The copy's buffer is done with. */
            free(pbTable);
        }
    }

    /* Then the cache's memory, then the block layer (which closes the   */
    /* device).                                                          */
    ext4_bcache_fini_dynamic(&cache);
    ext4_block_fini(pDevice);

    /* 0, or the first failure. */
    return(rc);
}

/*----------------------------------------------------------------------------*/
/* BFK_IsExtType:                                                             */
/*                                                                            */
/* Whether a BFE_FSTYPE_* value names one of the ext types, which lwext4's    */
/* formatter makes (and BFK_Repair finishes).                                 */
/*                                                                            */
/* Arguments:                                                                 */
/*     uiFsType : the type asked for.                                         */
/*                                                                            */
/* Returns:                                                                   */
/*     int : TRUE for ext2, ext3 or ext4; FALSE otherwise.                    */
/*----------------------------------------------------------------------------*/
static int BFK_IsExtType(uint32_t uiFsType)
{
    if (uiFsType == BFE_FSTYPE_EXT2 || uiFsType == BFE_FSTYPE_EXT3 || uiFsType == BFE_FSTYPE_EXT4)
    {   /* One of the three. */
        return(TRUE);
    }

    /* Not an ext type. */
    return(FALSE);
}

/*----------------------------------------------------------------------------*/
/* BFK_IsFatType:                                                             */
/*                                                                            */
/* Whether a BFE_FSTYPE_* value names one of the FAT types, which FatFs's     */
/* formatter makes (bfe_fat.c, BFT_MakeFs).                                   */
/*                                                                            */
/* Arguments:                                                                 */
/*     uiFsType : the type asked for.                                         */
/*                                                                            */
/* Returns:                                                                   */
/*     int : TRUE for FAT16 (or FAT12), FAT32 or exFAT; FALSE otherwise.      */
/*----------------------------------------------------------------------------*/
static int BFK_IsFatType(uint32_t uiFsType)
{
    if (uiFsType == BFE_FSTYPE_FAT16 || uiFsType == BFE_FSTYPE_FAT32 || uiFsType == BFE_FSTYPE_EXFAT)
    {   /* One of the three. */
        return(TRUE);
    }

    /* Not a FAT type. */
    return(FALSE);
}

/*----------------------------------------------------------------------------*/
/* BFK_CheckOptions:                                                          */
/*                                                                            */
/* The checks the two formatting entries share, made before anything is       */
/* written: the type; for ext, the block size (chosen here, as mke2fs would,  */
/* when the caller leaves it open), the size in whole blocks and the label;   */
/* for FAT, the cluster size and the label by FatFs's rules                   */
/* (BFT_CheckMakeFs) and the size in whole sectors.  Each refusal is recorded */
/* as the last error with its reason, so the caller only passes the status    */
/* on.                                                                        */
/*                                                                            */
/* Arguments:                                                                 */
/*     pOptions      : the caller's options: type, block or cluster size,     */
/*                     label.                                                 */
/*     ullSizeBytes  : the size to format -- the file size asked for, or the  */
/*                     range's length.                                        */
/*     uiSectorBytes : the sector the volume will count in (FAT only: 512 for */
/*                     a file, the device's own for a card).                  */
/*     puiBlock      : receives the block size settled on (ext), or the       */
/*                     cluster size asked for, 0 for FatFs's choice (FAT).    */
/*     pullBytes     : receives the size in whole blocks (ext) or sectors     */
/*                     (FAT).                                                 */
/*     pszLabel      : receives the label, NUL-terminated; BFK_LABEL_ROOM     */
/*                     bytes of room.                                         */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK, or BFE_ERR_ARG with the reason recorded.                 */
/*----------------------------------------------------------------------------*/
static int BFK_CheckOptions(const BFE_MakeFsOptions *pOptions, uint64_t ullSizeBytes, uint32_t uiSectorBytes, uint32_t *puiBlock,
                            uint64_t *pullBytes, char *pszLabel)
{
    uint32_t uiBlock;
    uint64_t ullBytes;
    size_t   cbLabel;
    int      iStatus;

    if (!BFK_IsExtType(pOptions->uiFsType) && !BFK_IsFatType(pOptions->uiFsType))
    {   /* Not one of the six types. */
        return(BFX_Fail(BFE_ERR_ARG, "the filesystem type must be ext2, ext3, ext4, FAT16, FAT32 or exFAT"));
    }

    /* The label, measured and copied out first, terminated: the options    */
    /* field may not be terminated if a caller filled all of it, and both   */
    /* kinds' checks below read the copy.                                   */
    cbLabel = 0;

    while (cbLabel < sizeof(pOptions->szLabel) && pOptions->szLabel[cbLabel] != '\0')
    {   /* One label byte per iteration. */
        cbLabel++;
    }

    /* The copy, and its terminator (pszLabel has BFK_LABEL_ROOM bytes, the   */
    /* whole field and one more).  The field holds the longest label any      */
    /* type keeps, so a label a type can hold always reaches its check.       */
    memcpy(pszLabel, pOptions->szLabel, cbLabel);
    pszLabel[cbLabel] = '\0';

    if (BFK_IsFatType(pOptions->uiFsType))
    {   /* A FAT type.  The cluster size and the label are judged by the    */
        /* rules FatFs formats and labels by (bfe_fat.c), so nothing that   */
        /* FatFs would quietly change or refuse once the range is written   */
        /* gets this far.                                                   */
        iStatus = BFT_CheckMakeFs(pOptions->uiFsType, uiSectorBytes, pOptions->uiBlockSize, pszLabel);

        if (iStatus != BFE_OK)
        {   /* Refused, the reason recorded by BFT_CheckMakeFs. */
            return(iStatus);
        }

        /* A whole number of sectors, as FatFs counts the volume. */
        ullBytes = ullSizeBytes - (ullSizeBytes % uiSectorBytes);

        if (ullBytes < BFE_MAKEFS_MIN_BYTES)
        {   /* Below the floor every type shares. */
            return(BFX_Fail(BFE_ERR_ARG, "the filesystem must be at least %u bytes", (unsigned)BFE_MAKEFS_MIN_BYTES));
        }

        /* Settled: the cluster size as asked (0: FatFs chooses by the    */
        /* size) and the whole-sector size.  Whether the type fits that   */
        /* size is for f_mkfs to say, before it writes anything.          */
        *puiBlock  = pOptions->uiBlockSize;
        *pullBytes = ullBytes;
        return(BFE_OK);
    }

    /* An ext type from here.  The block size asked for; 0 leaves the   */
    /* choice to this function.                                         */
    uiBlock = pOptions->uiBlockSize;

    if (uiBlock == 0)
    {   /* The caller left the choice here: what mke2fs would pick -- 1024   */
        /* under 512 MiB, else 4096 -- with the one ext4 exception below.    */
        if (ullSizeBytes < BFK_SMALL_FS_BYTES)
        {   /* A small filesystem wastes less with small blocks. */
            uiBlock = BFK_BLOCK_SMALL;

            if (pOptions->uiFsType == BFE_FSTYPE_EXT4 && ullSizeBytes < BFK_EXT4_1K_MIN_BYTES)
            {   /* The one combination lwext4 cannot format (see the       */
                /* definition): step up to the next block size, which it   */
                /* can.                                                    */
                uiBlock = BFK_BLOCK_MEDIUM;
            }
        }
        else
        {   /* The usual block size. */
            uiBlock = BFK_BLOCK_LARGE;
        }
    }

    if (uiBlock != 1024u && uiBlock != 2048u && uiBlock != 4096u)
    {   /* ext block sizes this engine reads. */
        return(BFX_Fail(BFE_ERR_ARG, "the block size must be 1024, 2048 or 4096 bytes"));
    }

    if (uiBlock == BFK_BLOCK_SMALL && pOptions->uiFsType == BFE_FSTYPE_EXT4 && ullSizeBytes < BFK_EXT4_1K_MIN_BYTES)
    {   /* Asked for by name, and it cannot be made: say so rather than   */
        /* fail later with a meaningless I/O error.                       */
        return(BFX_Fail(BFE_ERR_ARG, "ext4 with 1024-byte blocks needs at least 8 MiB; use 2048-byte blocks or leave the choice open"));
    }

    /* A whole number of blocks, so the filesystem fills the space exactly   */
    /* and no tail of it lies outside the filesystem.                        */
    ullBytes = ullSizeBytes - (ullSizeBytes % uiBlock);

    if (ullBytes < BFE_MAKEFS_MIN_BYTES)
    {   /* Too small to hold the structures and anything else. */
        return(BFX_Fail(BFE_ERR_ARG, "the filesystem must be at least %u bytes", (unsigned)BFE_MAKEFS_MIN_BYTES));
    }

    /* The label, copied out above: at most 16 bytes. */
    if (cbLabel > BFK_LABEL_MAX)
    {   /* Longer than a superblock can record. */
        return(BFX_Fail(BFE_ERR_ARG, "the volume label is longer than %d bytes", BFK_LABEL_MAX));
    }

    /* Settled: hand the block size and the whole-block size back. */
    *puiBlock  = uiBlock;
    *pullBytes = ullBytes;

    /* The options are good. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFE_MakeFs:                                                                */
/*                                                                            */
/* Create an image file and format it as an empty filesystem -- ext2/3/4 by   */
/* lwext4's formatter and the repair, or FAT12/16, FAT32 or exFAT by FatFs's  */
/* (bfe_fat.c).  No image may be open: either formatter uses the same single  */
/* block device a session does.  The new image is NOT opened; the caller      */
/* opens it with BFE_Open like any other.                                     */
/*                                                                            */
/* If formatting fails the half-made file is deleted, so a failed call never  */
/* leaves something that looks like an image and is not one.                  */
/*                                                                            */
/* Arguments:                                                                 */
/*     pwszImage : the image file to create.                                  */
/*     pOptions  : its size, type, block size, label and whether an existing  */
/*                 file may be replaced (see BFE_MakeFsOptions).              */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_BUSY; BFE_ERR_ALREADY_OPEN while an image is     */
/*           open; BFE_ERR_ARG for a bad size, type, block or cluster size or */
/*           label, or a FAT type FatFs cannot lay out in that size;          */
/*           BFE_ERR_EXISTS when the file is there and bOverwrite is 0;       */
/*           BFE_ERR_IO_HOST when the file cannot be created at that size;    */
/*           BFE_ERR_NO_MEMORY when the path cannot be copied for the block   */
/*           device or the finished format's table copies cannot be had;      */
/*           BFE_ERR_IO_IMAGE when the formatter fails.                       */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_MakeFs(const wchar_t *pwszImage, const BFE_MakeFsOptions *pOptions)
{
    struct ext4_mkfs_info info;
    char                  szLabel[BFK_LABEL_ROOM];
    uint32_t              uiBlock;
    uint32_t              uiError;
    uint64_t              ullBytes;
    int                   iStatus;
    int                   iCreated;
    int                   rc;

    /* The gate: the busy flag only -- "no image open" is tested just   */
    /* below with its own message.                                      */
    iStatus = BFX_Enter(FALSE, FALSE);

    if (iStatus != BFE_OK)
    {   /* Another call is running: BFX_Enter has said so, and the flag is   */
        /* not ours, so there is nothing to release.                         */
        return(iStatus);
    }

    if (BFX_IsOpen())
    {   /* The block device is in use by the open session: refuse.  Tested   */
        /* inside the gate, so no open can start between this test and the   */
        /* format; only the flag is held.                                    */
        iStatus = BFX_Fail(BFE_ERR_ALREADY_OPEN, "close the open image before creating a new one");
        BFX_Leave();
        return(iStatus);
    }

    if (pwszImage == NULL || pwszImage[0] == L'\0' || pOptions == NULL)
    {   /* Nothing to create, or no description of it: refuse.  Only the   */
        /* flag is held, and nothing on the disk has been touched.         */
        iStatus = BFX_Fail(BFE_ERR_ARG, "no image path or no options given");
        BFX_Leave();
        return(iStatus);
    }

    /* STEP 1: settle the type, the block (or cluster) size, the size in     */
    /* whole blocks (or sectors -- a file counts in 512-byte ones) and the   */
    /* label (the file is made that size, so the filesystem fills it).       */
    iStatus = BFK_CheckOptions(pOptions, pOptions->ullSizeBytes, BFK_FILE_SECTOR_BYTES, &uiBlock, &ullBytes, szLabel);

    if (iStatus != BFE_OK)
    {   /* Refused, with the reason recorded (BFK_CheckOptions records      */
        /* before returning); nothing created yet, so only the flag goes.   */
        BFX_Leave();
        return(iStatus);
    }

    /* STEP 2: create the file at its size. */
    iCreated = BFK_CreateSized(pwszImage, ullBytes, pOptions->bOverwrite != 0, &uiError);

    if (iCreated == BFK_CREATE_EXISTS)
    {   /* There already, and the caller did not allow replacing it: the   */
        /* existing file is untouched (CREATE_NEW opened nothing).         */
        iStatus = BFX_Fail(BFE_ERR_EXISTS, "the image file already exists");
        BFX_Leave();
        return(iStatus);
    }

    if (iCreated != BFK_CREATE_OK)
    {   /* No permission, no room, or a path that cannot be created.  The    */
        /* Windows number tells which; BFK_CreateSized has already removed   */
        /* anything it made, so only the flag is left to release.            */
        iStatus = BFX_Fail(BFE_ERR_IO_HOST, "cannot create the image file at that size (Windows error %u)", (unsigned)uiError);
        BFX_Leave();
        return(iStatus);
    }

    /* STEP 3: point the block device at the whole file, read-write, and   */
    /* format.  Both formatters open and close the device themselves.      */
    if (BFD_Configure(pwszImage, 0, 0, FALSE) != 0)
    {   /* The path could not be copied (ENOMEM).  BFD_Configure's other   */
        /* failure, an offset or length off the 512-byte grid (EINVAL),    */
        /* cannot arise with 0 and 0, so out of memory is the reason.      */

        /* Refuse, removing the file just made: it holds no filesystem,     */
        /* only its size, and a file left there would look like an image.   */
        /* (A file it replaced was emptied by the create already.)          */
        iStatus = BFX_Fail(BFE_ERR_NO_MEMORY, "out of memory");
        DeleteFileW(pwszImage);
        BFX_Leave();
        return(iStatus);
    }

    if (BFK_IsFatType(pOptions->uiFsType))
    {   /* A FAT type: FatFs's formatter lays the volume over the whole     */
        /* file in 512-byte sectors and names it (bfe_fat.c), closing the   */
        /* device on every path.  There is nothing to repair afterwards.    */
        iStatus = BFT_MakeFs(pOptions->uiFsType, BFK_FILE_SECTOR_BYTES, uiBlock, szLabel);

        if (iStatus != BFE_OK)
        {   /* Not made, the reason recorded: remove the file, which holds   */
            /* no usable volume (no handle of ours is open on it).           */
            DeleteFileW(pwszImage);
            BFX_Leave();
            return(iStatus);
        }

        /* Let the next call in; the block device stays configured on this   */
        /* file, harmlessly, as after an ext format.                         */
        BFX_Leave();

        /* The image file holds an empty FAT volume. */
        return(BFE_OK);
    }

    /* An ext type from here, made by lwext4's formatter.  Its request:   */
    /* the size, the block size and the label, everything else zero for   */
    /* it to compute; and a clean scratch object.                         */
    memset(&info, 0, sizeof(info));
    memset(&BFK_fs, 0, sizeof(BFK_fs));
    info.len        = ullBytes;
    info.block_size = uiBlock;
    info.label      = szLabel;

    /* The type is passed through as lwext4's own number: BFE_FSTYPE_EXT2   */
    /* / 3 / 4 are 2 / 3 / 4, equal to lwext4's F_SET_EXT2 / 3 / 4          */
    /* (ext4_config.h) -- a change to either set breaks this cast.          */
    rc = ext4_mkfs(&BFK_fs, BFD_Get(), &info, (int)pOptions->uiFsType);

    if (rc != 0)
    {   /* The formatter failed: remove what it left.  ext4_mkfs closes the    */
        /* device on every exit (ext4_mkfs.c, its fini labels), so no handle   */
        /* of ours holds the file and the delete can succeed.                  */
        iStatus = BFX_Fail(BFE_ERR_IO_IMAGE, "formatting failed: %s", strerror(rc));
        DeleteFileW(pwszImage);
        BFX_Leave();
        return(iStatus);
    }

    /* STEP 4: correct what the formatter leaves wrong. */
    rc = BFK_Repair(BFD_Get());

    if (rc != 0)
    {   /* Half-repaired is not an image: remove it.  BFK_Repair, too,   */
        /* closes the block layer on every exit before returning.  Its   */
        /* ENOMEM (a buffer for the descriptor table's copies) is        */
        /* reported as the out-of-memory it is; any other failure is     */
        /* the image's.                                                  */
        if (rc == ENOMEM)
        {   /* Memory ran out finishing it. */
            iStatus = BFX_Fail(BFE_ERR_NO_MEMORY, "finishing the new filesystem failed: out of memory");
        }
        else
        {   /* Reading or writing the new image failed. */
            iStatus = BFX_Fail(BFE_ERR_IO_IMAGE, "finishing the new filesystem failed: %s", strerror(rc));
        }

        DeleteFileW(pwszImage);
        BFX_Leave();
        return(iStatus);
    }

    /* Let the next call in; the block device stays configured on this   */
    /* file, harmlessly -- BFE_Open configures it afresh.                */
    BFX_Leave();

    /* The image file holds an empty ext filesystem. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFK_ZeroEvent:                                                             */
/*                                                                            */
/* Tell the caller's callback how a zero-fill stands, as a copy's event.      */
/* With no callback there is nothing to tell and nothing can cancel.          */
/*                                                                            */
/* Arguments:                                                                 */
/*     pfnEvent   : the callback, or NULL.                                    */
/*     pvContext  : its context, passed through unchanged.                    */
/*     uiKind     : BFE_EVENT_START, BFE_EVENT_PROGRESS or BFE_EVENT_DONE.    */
/*     ullDone    : bytes zeroed so far.                                      */
/*     ullTotal   : the range's length.                                       */
/*     pwszPath   : the file or device.                                       */
/*                                                                            */
/* Returns:                                                                   */
/*     int : the callback's BFE_REPLY_* answer; BFE_REPLY_CONTINUE without    */
/*           one.                                                             */
/*----------------------------------------------------------------------------*/
static int BFK_ZeroEvent(BFE_CopyCallback pfnEvent, void *pvContext, uint32_t uiKind, uint64_t ullDone, uint64_t ullTotal,
                         const wchar_t *pwszPath)
{
    BFE_CopyEvent event;

    if (pfnEvent == NULL)
    {   /* Nobody to tell: carry on. */
        return(BFE_REPLY_CONTINUE);
    }

    /* A copy's event, filled for the zero-fill: the kind, the bytes done   */
    /* and the range's length, "zero" as the image path and the file or     */
    /* device as the host path.  The "zero" label is matched by the         */
    /* program (gui\CopyForms.cs, CPY_LABEL_ZERO) to word its progress      */
    /* window, so a change here is made there too.                          */
    memset(&event, 0, sizeof(event));
    event.ullBytesDone  = ullDone;
    event.ullBytesTotal = ullTotal;
    event.pszImagePath  = "zero";
    event.pwszHostPath  = pwszPath;
    event.uiKind        = uiKind;
    event.uiType        = BFE_TYPE_FILE;
    event.iStatus       = BFE_OK;

    /* The caller's answer. */
    return(pfnEvent(&event, pvContext));
}

/*----------------------------------------------------------------------------*/
/* BFK_ZeroRange:                                                             */
/*                                                                            */
/* BFE_MAKEFS_ZERO: write zeros over the whole range, telling the callback    */
/* how it stands as it goes; a CANCEL reply stops it.  Nothing is flushed     */
/* here -- the caller flushes the target whatever happened.                   */
/*                                                                            */
/* Arguments:                                                                 */
/*     pTarget   : the open file or device.                                   */
/*     ullOffset : where the range starts.                                    */
/*     ullLength : its length.                                                */
/*     pfnEvent  : the callback, or NULL.                                     */
/*     pvContext : its context.                                               */
/*     pwszPath  : the path, for the events.                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_CANCELLED; BFE_ERR_NO_MEMORY; BFE_ERR_IO_HOST    */
/*           for a failed write -- each recorded with BFX_Fail.               */
/*----------------------------------------------------------------------------*/
static int BFK_ZeroRange(BFV_Target *pTarget, uint64_t ullOffset, uint64_t ullLength, BFE_CopyCallback pfnEvent, void *pvContext,
                         const wchar_t *pwszPath)
{
    uint8_t  *pbZeros;
    uint64_t  ullDone;
    uint32_t  cbWrite;
    int       iError;

    /* One aligned chunk of zeros (BFK_ZERO_CHUNK), written over and over:   */
    /* an unbuffered device write needs an aligned buffer.                   */
    pbZeros = (uint8_t *)BFV_AllocAligned(BFK_ZERO_CHUNK);

    if (pbZeros == NULL)
    {   /* No room for the chunk: nothing written yet, nothing to free. */
        return(BFX_Fail(BFE_ERR_NO_MEMORY, "out of memory"));
    }

    /* The zeros themselves.  BFV_AllocAligned already hands back zeroed     */
    /* pages -- Windows documents that newly committed VirtualAlloc pages    */
    /* are zero-filled -- so this clear repeats it; why it is here as well   */
    /* is not recorded.                                                      */
    memset(pbZeros, 0, BFK_ZERO_CHUNK);

    /* The zero-fill's START, which the caller may answer with cancel. */
    if (BFK_ZeroEvent(pfnEvent, pvContext, BFE_EVENT_START, 0, ullLength, pwszPath) == BFE_REPLY_CANCEL)
    {   /* Cancelled before a byte was written: free the chunk, record it. */
        BFV_FreeAligned(pbZeros);
        return(BFX_Fail(BFE_ERR_CANCELLED, "cancelled before zeroing began; nothing was changed"));
    }

    /* Nothing zeroed yet. */
    ullDone = 0;

    while (ullDone < ullLength)
    {   /* One chunk per iteration, the last one perhaps shorter (still   */
        /* whole sectors: the range is held to sector boundaries).        */
        cbWrite = BFK_ZERO_CHUNK;

        if (ullLength - ullDone < (uint64_t)cbWrite)
        {   /* What is left is less than a chunk. */
            cbWrite = (uint32_t)(ullLength - ullDone);
        }

        /* The chunk's zeros, at their place in the range. */
        iError = BFV_Write(pTarget, ullOffset + ullDone, pbZeros, cbWrite);

        if (iError != 0)
        {   /* The media refused the write: free the chunk, record it.  The   */
            /* byte is the chunk's start within the range (one write, one     */
            /* result); the range holds zeros up to about there.              */
            BFV_FreeAligned(pbZeros);
            return(BFX_Fail(BFE_ERR_IO_HOST, "cannot write zeros at byte %llu of the range: Windows error %d",
                            (unsigned long long)ullDone, iError));
        }

        /* Count it, and report -- one event per chunk; the caller may   */
        /* cancel, which stops the fill after the write just made.       */
        ullDone = ullDone + cbWrite;

        if (BFK_ZeroEvent(pfnEvent, pvContext, BFE_EVENT_PROGRESS, ullDone, ullLength, pwszPath) == BFE_REPLY_CANCEL)
        {   /* Cancelled part-way: the range holds zeros up to here and its old bytes after. */
            BFV_FreeAligned(pbZeros);
            return(BFX_Fail(BFE_ERR_CANCELLED, "cancelled after %llu bytes were zeroed; nothing was formatted", (unsigned long long)ullDone));
        }
    }

    /* The chunk is done with; then the DONE event, whose reply is not   */
    /* looked at -- the zero-fill is complete, and the format follows.   */
    BFV_FreeAligned(pbZeros);
    BFK_ZeroEvent(pfnEvent, pvContext, BFE_EVENT_DONE, ullDone, ullLength, pwszPath);

    /* The whole range is zeros. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFE_MakeFsAt:                                                              */
/*                                                                            */
/* Format a byte range of an EXISTING file or device -- a partition of a card */
/* or an eMMC, or of a disk image -- as an empty filesystem, in place, with   */
/* the guards a writable BFE_Open gives a device.  Nothing is created or      */
/* sized: the range is there already, and the caller has asked for what is    */
/* in it to be lost.  The checks and the formatting are BFE_MakeFs's; what    */
/* differs is the way in.  No image may be open: the formatter uses the       */
/* block device a session does.  The new filesystem is NOT opened; the        */
/* caller opens it with BFE_Open at the same offset and length.  A failure    */
/* after the wipe leaves the range as the failure left it -- there is no file */
/* to delete, and what was there before is gone from its first sectors        */
/* already.                                                                   */
/*                                                                            */
/* Arguments:                                                                 */
/*     pwszPath  : the file or device.                                        */
/*     ullOffset : where the range starts, bytes; a multiple of the sector.   */
/*     ullLength : the range's length, bytes; a multiple of the sector, or 0  */
/*                 for "to the end".                                          */
/*     pOptions  : the type, block size and label (see BFE_MakeFsOptions);    */
/*                 its ullSizeBytes and bOverwrite are not used -- the range  */
/*                 is the size, and it is overwritten by definition; its      */
/*                 uiFlags may hold BFE_MAKEFS_ZERO, which makes STEP 5 zero  */
/*                 the WHOLE range (BFK_ZeroRange) instead of its first page. */
/*     pfnEvent  : told how a zero-fill stands, as a copy is; may cancel it.  */
/*                 NULL for no progress.  Without BFE_MAKEFS_ZERO it is never */
/*                 called.                                                    */
/*     pvContext : passed to pfnEvent unchanged.                              */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_CANCELLED when the zero-fill was cancelled       */
/*           (nothing formatted); BFE_ERR_BUSY; BFE_ERR_ALREADY_OPEN while an */
/*           image is open; BFE_ERR_ARG for a bad type, block size or label,  */
/*           a range not on sector boundaries or outside the file or device,  */
/*           or one too short; BFE_ERR_IMAGE_OPEN when the path cannot be     */
/*           opened for writing; BFE_ERR_SYSTEM_DISK and BFE_ERR_VOLUME_LOCK  */
/*           as BFE_Open gives them for a device; BFE_ERR_NO_MEMORY;          */
/*           BFE_ERR_IO_HOST when                                             */
/*           the wipe cannot be written; BFE_ERR_IO_IMAGE when the formatter  */
/*           fails (or, for FAT, cannot lay the type out in the range: then   */
/*           BFE_ERR_ARG, nothing past the wipe written); BFE_ERR_VERIFY when */
/*           a device did not keep the formatter's writes (each is read back; */
/*           the zero-fill is not).                                           */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_MakeFsAt(const wchar_t *pwszPath, uint64_t ullOffset, uint64_t ullLength, const BFE_MakeFsOptions *pOptions,
                         BFE_CopyCallback pfnEvent, void *pvContext)
{
    struct ext4_mkfs_info info;
    BFV_Target            target;
    BFV_Locks             locks;
    char                  szLabel[BFK_LABEL_ROOM];
    uint8_t              *pbZeros;
    uint32_t              uiBlock;
    uint32_t              uiSystem;
    uint64_t              ullBytes;
    int                   iStatus;
    int                   iError;
    int                   rc;

    /* The gate: the busy flag only -- "no image open" is tested just   */
    /* below with its own message.                                      */
    iStatus = BFX_Enter(FALSE, FALSE);

    if (iStatus != BFE_OK)
    {   /* Another call is running: BFX_Enter has said so, and the flag is   */
        /* not ours, so there is nothing to release.                         */
        return(iStatus);
    }

    if (BFX_IsOpen())
    {   /* The block device is in use by the open session: refuse, inside   */
        /* the gate so no open can slip in before the format; only the      */
        /* flag is held.                                                    */
        iStatus = BFX_Fail(BFE_ERR_ALREADY_OPEN, "close the open image before formatting a partition");
        BFX_Leave();
        return(iStatus);
    }

    if (pwszPath == NULL || pwszPath[0] == L'\0' || pOptions == NULL)
    {   /* Nothing to format, or no description of it: refuse.  Only the   */
        /* flag is held, and nothing on the disk has been touched.         */
        iStatus = BFX_Fail(BFE_ERR_ARG, "no path or no options given");
        BFX_Leave();
        return(iStatus);
    }

    /* STEP 1: open the path for writing through the raw I/O layer and learn   */
    /* what it is -- a file or a device, its length, its sector size, which    */
    /* disk it is on -- before anything is written.  The locks start empty:    */
    /* every exit below releases them, a file's included.                      */
    memset(&locks, 0, sizeof(locks));
    iError = BFV_Open(pwszPath, BFV_ACCESS_WRITE, &target);

    if (iError != 0)
    {   /* Missing, in use, write-protected, or a device without Administrator. */

        /* Refuse with the Windows error -- the number tells those apart --   */
        /* and let the next call in.  Nothing opened, no lock taken yet.      */
        iStatus = BFX_Fail(BFE_ERR_IMAGE_OPEN, "cannot open the file or device for writing: Windows error %d", iError);
        BFX_Leave();
        return(iStatus);
    }

    /* STEP 2: the range, on sector boundaries and inside what was opened.     */
    /* The block device divides by 512 and a device transfers whole sectors,   */
    /* so a range off a boundary would be read from the wrong place.           */
    if ((ullOffset % target.uiSector) != 0 || (ullLength % target.uiSector) != 0)
    {   /* Not on the sector grid: refuse.  The target is the one thing     */
        /* open -- no lock is taken until STEP 4 and nothing has been       */
        /* written -- so closing it is the whole release.  The same holds   */
        /* for the two range refusals below.                                */
        iStatus = BFX_Fail(BFE_ERR_ARG, "the offset and length must be multiples of the sector size (%u bytes)", (unsigned)target.uiSector);
        BFV_Close(&target);
        BFX_Leave();
        return(iStatus);
    }

    if (ullOffset >= target.ullSize)
    {   /* Nothing lies at or past the end: refuse, closing the target     */
        /* (tested before "to the end" below, which would otherwise make   */
        /* a range of length 0 -- or wrap -- from such an offset).         */
        iStatus = BFX_Fail(BFE_ERR_ARG, "the offset is at or past the end of the file or device");
        BFV_Close(&target);
        BFX_Leave();
        return(iStatus);
    }

    if (ullLength == 0)
    {   /* To the end. */
        ullLength = target.ullSize - ullOffset;
    }

    /* The length against the room left after the offset.  Compared as a     */
    /* subtraction -- the offset is known to be inside, so size - offset     */
    /* cannot wrap -- never as offset + length, which a length near 2^64     */
    /* would wrap to a small sum that passes.  Everything after this point   */
    /* (the zero-fill, the formatter, the block device's range) works on     */
    /* this length, so it is held to what the file or device really has,     */
    /* which also keeps every offset within Windows' signed 64-bit range.    */
    if (ullLength > target.ullSize - ullOffset)
    {   /* The range runs off the end: refuse, closing the target. */
        iStatus = BFX_Fail(BFE_ERR_ARG, "the range runs past the end of the file or device");
        BFV_Close(&target);
        BFX_Leave();
        return(iStatus);
    }

    /* STEP 3: settle the type, the block (or cluster) size, the size in       */
    /* whole blocks (or sectors) and the label against the range's length --   */
    /* a FAT volume counting in the device's own sector, as a mount of it      */
    /* will.                                                                   */
    iStatus = BFK_CheckOptions(pOptions, ullLength, target.uiSector, &uiBlock, &ullBytes, szLabel);

    if (iStatus != BFE_OK)
    {   /* Refused, with the reason recorded: close the target -- the one   */
        /* thing open, since the guards and their locks come after.         */
        BFV_Close(&target);
        BFX_Leave();
        return(iStatus);
    }

    /* STEP 4: a device gets the guards.  The system disk is never written;   */
    /* every volume on the device's disk is locked and dismounted until the   */
    /* format is over, since Windows refuses raw writes under a mounted       */
    /* volume.                                                                */
    if (target.bDevice)
    {   /* A real device. */

        /* The system-disk guard, on the disk number of the open handle --    */
        /* before the lock, because locking the system disk's volumes would   */
        /* dismount the running Windows.                                      */
        uiSystem = BFV_SystemDisk();

        if (uiSystem == BFV_NO_DISK || target.uiDisk == BFV_NO_DISK || target.uiDisk == uiSystem)
        {   /* The disk Windows runs from, or one that cannot be told apart:    */
            /* refuse.  No lock is taken yet (the test comes before it, for     */
            /* the reason above), so closing the device is the whole release.   */
            iStatus = BFX_Fail(BFE_ERR_SYSTEM_DISK, "the device is the disk Windows is running from, or cannot be told apart from it");
            BFV_Close(&target);
            BFX_Leave();
            return(iStatus);
        }

        /* Lock and dismount every volume on the disk (only the volumes   */
        /* on it are opened for writing).                                 */
        iError = BFV_LockDisk(target.uiDisk, &locks);

        if (iError != 0)
        {   /* The disk could not be locked -- a volume on it in use, or   */
            /* one whose disk could not be told: refuse with the lock's    */
            /* own sentence, closing the device (BFV_LockDisk leaves       */
            /* nothing held).                                              */
            iStatus = BFX_Fail(BFE_ERR_VOLUME_LOCK, "%s", locks.szFailure);
            BFV_Close(&target);
            BFX_Leave();
            return(iStatus);
        }
    }

    /* STEP 5: clear what was there.  With BFE_MAKEFS_ZERO the WHOLE range is   */
    /* zeroed, reported and cancellable (BFK_ZeroRange); without it only the    */
    /* first BFK_WIPE_BYTES are, from a page-aligned buffer (a device's         */
    /* unbuffered write needs the alignment; the length is a multiple of any    */
    /* sector size met here and well under the size floor).  Either way what    */
    /* was written is flushed and the target closed before the outcome is       */
    /* acted on -- the block device reopens the same path for the formatter     */
    /* -- and a cancel or a failure stops here: nothing is formatted over a     */
    /* half-cleared range.                                                      */
    if ((pOptions->uiFlags & BFE_MAKEFS_ZERO) != 0)
    {   /* The caller asked for an erase: every byte of the range zeroed   */
        /* before the format, so no old data survives in the free space.   */
        iStatus = BFK_ZeroRange(&target, ullOffset, ullLength, pfnEvent, pvContext, pwszPath);
    }
    else
    {   /* The default: the first page only. */
        pbZeros = (uint8_t *)BFV_AllocAligned(BFK_WIPE_BYTES);

        if (pbZeros == NULL)
        {   /* No room for one page of zeros: recorded, and the shared exit   */
            /* below (flush, close, unlock) runs as for any other outcome.    */
            iStatus = BFX_Fail(BFE_ERR_NO_MEMORY, "out of memory");
        }
        else
        {   /* Zeros over the boot sector and any signature, so a signature    */
            /* of what was there before (a FAT, a squashfs) does not survive   */
            /* beside the new filesystem to confuse a probe -- the ext         */
            /* formatter writes nothing in the first kilobyte itself (FatFs    */
            /* writes its own boot sector there).                              */

            /* Fill the page, write it at the range's start, free it; the     */
            /* status is OK unless the write's result below says otherwise.   */
            memset(pbZeros, 0, BFK_WIPE_BYTES);
            iError = BFV_Write(&target, ullOffset, pbZeros, BFK_WIPE_BYTES);
            BFV_FreeAligned(pbZeros);
            iStatus = BFE_OK;

            if (iError != 0)
            {   /* The first sectors of the range could not be written: the    */
                /* format must not go ahead over an old signature it may not   */
                /* have cleared, so this stops the call below.                 */
                iStatus = BFX_Fail(BFE_ERR_IO_HOST, "cannot write the start of the range: Windows error %d", iError);
            }
        }
    }

    /* On the media, and the path free for the block device.  The flush's   */
    /* own result is not looked at: a device the format then writes is      */
    /* read back (STEP 6), which is the check that matters.                 */
    BFV_Flush(&target);
    BFV_Close(&target);

    if (iStatus != BFE_OK)
    {   /* Cancelled or failed, the reason already recorded: release the   */
        /* locks (the target is already closed).                           */
        BFV_UnlockDisk(&locks);
        BFX_Leave();
        return(iStatus);
    }

    /* STEP 6: point the block device at the range, read-write, and format.     */
    /* Every block either formatter writes (and BFK_Repair after an ext one)    */
    /* goes through the block device, which refuses any request not wholly      */
    /* inside this range (bfe_blockdev.c's BFD_InRange) -- lwext4 through its   */
    /* hooks, FatFs through bfe_fatio.c's disk_* functions -- so nothing they   */
    /* do can stray outside it.                                                 */
    if (BFD_Configure(pwszPath, ullOffset, ullLength, FALSE) != 0)
    {   /* The path could not be copied (ENOMEM).  The range was checked     */
        /* above, so BFD_Configure's other failure, off the 512-byte grid    */
        /* (EINVAL), cannot arise here: every sector size is a multiple of   */
        /* 512.                                                              */
        iStatus = BFX_Fail(BFE_ERR_NO_MEMORY, "out of memory");
        BFV_UnlockDisk(&locks);
        BFX_Leave();
        return(iStatus);
    }

    /* The format's own writes are read back on a device (the zero-fill     */
    /* above, BFK_ZeroRange, was not: it writes past the block device).     */
    /* (A file is not read back: BFD_SetVerify applies to a device only.)   */
    /* The setting lasts until the next BFD_Configure, which resets it.     */
    BFD_SetVerify(TRUE);

    if (BFK_IsFatType(pOptions->uiFsType))
    {   /* A FAT type: FatFs's formatter lays the volume over the range in     */
        /* the device's own sector and names it (bfe_fat.c), its writes        */
        /* going through the same range-bounded, read-back block device, and   */
        /* closes the device on every path.  Nothing to repair afterwards.     */
        iStatus = BFT_MakeFs(pOptions->uiFsType, target.uiSector, uiBlock, szLabel);

        if (iStatus != BFE_OK)
        {   /* Not made, the reason recorded.  A layout FatFs refused wrote   */
            /* nothing past the wipe; a failed write leaves a part-made       */
            /* volume.  The locks are released (the device is closed) and     */
            /* there is no file to delete.                                    */
            BFV_UnlockDisk(&locks);
            BFX_Leave();
            return(iStatus);
        }
    }
    else
    {   /* An ext type: lwext4's formatter, then the repair. */

        /* The formatter's request, as in BFE_MakeFs (the type cast relies   */
        /* on the same equal numbering).                                     */
        memset(&info, 0, sizeof(info));
        memset(&BFK_fs, 0, sizeof(BFK_fs));
        info.len        = ullBytes;
        info.block_size = uiBlock;
        info.label      = szLabel;

        /* The format, over the block device configured on the range.  As     */
        /* in BFE_MakeFs, ext4_mkfs opens the device itself and closes it     */
        /* on every exit, so nothing of ours is left open on the disk after   */
        /* it.                                                                */
        rc = ext4_mkfs(&BFK_fs, BFD_Get(), &info, (int)pOptions->uiFsType);

        if (rc != 0)
        {   /* The formatter failed partway: the range holds what it left. */

            /* Refuse; the device's locks are released (the block device   */
            /* closed itself), and there is no file to delete.             */
            iStatus = BFX_Fail(BFE_ERR_IO_IMAGE, "formatting failed: %s", strerror(rc));
            BFV_UnlockDisk(&locks);
            BFX_Leave();
            return(iStatus);
        }

        /* STEP 7: correct what the formatter leaves wrong, as for a new file. */
        rc = BFK_Repair(BFD_Get());

        if (rc != 0)
        {   /* Half-repaired: say so; the range is not a filesystem e2fsck accepts. */

            /* Refuse, releasing the locks (BFK_Repair has closed the block   */
            /* layer, so nothing of ours is open on the disk as they go):     */
            /* its ENOMEM as the out-of-memory it is, any other failure as    */
            /* the image's.                                                   */
            if (rc == ENOMEM)
            {   /* Memory ran out finishing it. */
                iStatus = BFX_Fail(BFE_ERR_NO_MEMORY, "finishing the new filesystem failed: out of memory");
            }
            else
            {   /* Reading or writing the range failed. */
                iStatus = BFX_Fail(BFE_ERR_IO_IMAGE, "finishing the new filesystem failed: %s", strerror(rc));
            }

            BFV_UnlockDisk(&locks);
            BFX_Leave();
            return(iStatus);
        }
    }

    if (BFD_WriteNotKept(NULL))
    {   /* The format (and an ext one's repair) reported success, but a      */
        /* device write read back different (every one is checked): the      */
        /* filesystem is not on the card, whatever the formatter believes.   */
        iStatus = BFX_Fail(BFE_ERR_VERIFY, "the device did not keep the new filesystem's writes; the card is failing or counterfeit");
        BFV_UnlockDisk(&locks);
        BFX_Leave();
        return(iStatus);
    }

    /* Done: the volumes may remount (the block device is closed already),   */
    /* and the next call may come in.                                        */
    BFV_UnlockDisk(&locks);
    BFX_Leave();

    /* The range holds an empty filesystem. */
    return(BFE_OK);
}
