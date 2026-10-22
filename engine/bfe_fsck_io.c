/*----------------------------------------------------------------------------*/
/* bfe_fsck_io.c                                                              */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The engine's e2fsprogs io manager (see bfe_fsck_io.h for why it exists).   */
/* libext2fs talks to a disk through a struct_io_manager: open a channel,     */
/* set its block size, read and write runs of blocks counted from the START   */
/* OF THE FILESYSTEM, write a few bytes, flush, close.  This file supplies    */
/* those over BFD_Target* (bfe_blockdev.c), which addresses bytes from the    */
/* start of the file or device, so every call here adds the partition offset  */
/* -- the one piece of arithmetic that is this file's own.                    */
/*                                                                            */
/* No cache: libext2fs and e2fsck keep their own buffers (and the block       */
/* device below keeps none, for the same reason lwext4's path keeps none).    */
/* What upstream's unix_io.c and windows_io.c add on top -- an eight-block    */
/* cache, O_DIRECT alignment, discard and zero-out -- is either unneeded here */
/* or answered "not supported", which both libraries handle.                  */
/*                                                                            */
/* Errors are reported the way libext2fs expects: an errno value, or one of   */
/* its own EXT2_ET_* codes, and a failed block read or write is first offered */
/* to the channel's read_error / write_error hook, which e2fsck installs to   */
/* print "Error reading block N" and decide whether to go on.                 */
/*----------------------------------------------------------------------------*/

/* Windows first (its error codes are what the target hands back); then    */
/* e2fsprogs' build configuration, which must precede its headers; the C   */
/* library; libext2fs' types, its channel interface and its error codes;   */
/* and the engine's block-device target and this file's own interface.     */
#include <windows.h>

#include "config.h"
#include <errno.h>
#include <string.h>
#include <stdlib.h>

#include <ext2fs/ext2_fs.h>
#include <ext2fs/ext2fs.h>
#include <ext2fs/ext2_io.h>

#include "bfe_blockdev.h"
#include "bfe_fsck_io.h"

/*----------------------------------------------------------------------------*/
/* Definitions -- documentation table.                                        */
/*----------------------------------------------------------------------------*/
#define BFIO_MAGIC         0x42464349       // marks a channel's private data as this manager's ("BFCI"); EXT2_CHECK_MAGIC-style guard
#define BFIO_DEFAULT_BLOCK 1024             // a fresh channel's block size, as every libext2fs manager starts (ext2fs_open2 sets the real one)

/*----------------------------------------------------------------------------*/
/* BFIO_Channel -- what a channel keeps for itself: the guard and the byte    */
/* counters libext2fs may ask for through get_stats.  The target is shared    */
/* by every channel and lives in bfe_blockdev.c.                              */
/*----------------------------------------------------------------------------*/
typedef struct BFIO_Channel
{
    int                    iMagic;          // BFIO_MAGIC
    struct struct_io_stats stats;           // bytes read and written through this channel (num_fields 2, as upstream reports it)
} BFIO_Channel;

/*----------------------------------------------------------------------------*/
/* The channels open on the one target.  The first open opens the target, the */
/* last close closes it; in between every channel reads and writes the same   */
/* bytes.  The filesystem's byte length, learned at the open, bounds every    */
/* transfer (BFIO_Transfer, BFIO_WriteByte): a read past the end is reported  */
/* short, the part past it reading as zeros, and a write that would reach     */
/* past the end is refused whole, before anything is written.  So nothing     */
/* e2fsck or resize2fs does -- a journal replay included -- can write outside */
/* the range it was given; on a card, the next partition is safe.  lwext4's   */
/* sessions are held to the same range by bfe_blockdev.c's own hooks.         */
/*----------------------------------------------------------------------------*/
static int      BFIO_nOpen;                 // channels open on the target
static uint64_t BFIO_ullLength;             // the filesystem range's length in bytes, from BFD_TargetOpen

/*----------------------------------------------------------------------------*/
/* The manager's functions are defined below; the manager table at the end    */
/* needs their names first.                                                   */
/*----------------------------------------------------------------------------*/
static errcode_t BFIO_Open(const char *pszName, int iFlags, io_channel *pChannel);
static errcode_t BFIO_Close(io_channel channel);
static errcode_t BFIO_SetBlksize(io_channel channel, int iBlksize);
static errcode_t BFIO_ReadBlk(io_channel channel, unsigned long ulBlock, int iCount, void *pvData);
static errcode_t BFIO_WriteBlk(io_channel channel, unsigned long ulBlock, int iCount, const void *pvData);
static errcode_t BFIO_Flush(io_channel channel);
static errcode_t BFIO_WriteByte(io_channel channel, unsigned long ulOffset, int iCount, const void *pvData);
static errcode_t BFIO_SetOption(io_channel channel, const char *pszOption, const char *pszArg);
static errcode_t BFIO_GetStats(io_channel channel, io_stats *pStats);
static errcode_t BFIO_ReadBlk64(io_channel channel, unsigned long long ullBlock, int iCount, void *pvData);
static errcode_t BFIO_WriteBlk64(io_channel channel, unsigned long long ullBlock, int iCount, const void *pvData);
static errcode_t BFIO_Discard(io_channel channel, unsigned long long ullBlock, unsigned long long ullCount);
static errcode_t BFIO_CacheReadahead(io_channel channel, unsigned long long ullBlock, unsigned long long ullCount);
static errcode_t BFIO_Zeroout(io_channel channel, unsigned long long ullBlock, unsigned long long ullCount);

/*----------------------------------------------------------------------------*/
/* BFIO_Check:                                                                */
/*                                                                            */
/* The guard every function runs first: is this channel one of ours?  Both    */
/* magics are tested, the channel's own and the private data's, exactly as    */
/* upstream's managers do with their EXT2_CHECK_MAGIC.                        */
/*                                                                            */
/* Arguments:                                                                 */
/*     channel : the channel handed in.                                       */
/*                                                                            */
/* Returns:                                                                   */
/*     BFIO_Channel * : its private data, or NULL when either magic is wrong. */
/*----------------------------------------------------------------------------*/
static BFIO_Channel *BFIO_Check(io_channel channel)
{
    BFIO_Channel *pPrivate;

    if (channel == NULL || channel->magic != EXT2_ET_MAGIC_IO_CHANNEL)
    {   /* Not a channel at all. */
        return(NULL);
    }

    pPrivate = (BFIO_Channel *)channel->private_data;

    if (pPrivate == NULL || pPrivate->iMagic != BFIO_MAGIC)
    {   /* A channel, but another manager's. */
        return(NULL);
    }

    /* Ours. */
    return(pPrivate);
}

/*----------------------------------------------------------------------------*/
/* BFIO_MapError:                                                             */
/*                                                                            */
/* Turn a BFD_Target* result -- a Windows error code -- into what libext2fs   */
/* expects: its own short-read / short-write codes for a transfer that ran    */
/* off the end, an errno for the rest.  libext2fs' error_message() names an   */
/* errno value by its C text and an EXT2_ET_ code by its table entry, so      */
/* e2fsck's "Error reading block N (<reason>)" reads sensibly either way; the */
/* Windows detail is kept by bfe_blockdev.c for the engine's own error line.  */
/*                                                                            */
/* Arguments:                                                                 */
/*     iError : the Windows error (never 0).                                  */
/*     bWrite : TRUE for a write, FALSE for a read -- picks the short code.   */
/*                                                                            */
/* Returns:                                                                   */
/*     errcode_t : EXT2_ET_SHORT_READ / EXT2_ET_SHORT_WRITE, EROFS, ENXIO     */
/*                 (nothing open) or EIO.                                     */
/*----------------------------------------------------------------------------*/
static errcode_t BFIO_MapError(int iError, int bWrite)
{
    if (iError == ERROR_HANDLE_EOF)
    {   /* The transfer ran off the end.  For a READ that is BFIO_Transfer's   */
        /* own range test or the target's; for a WRITE it is BFIO_Transfer     */
        /* refusing a write that would pass the filesystem's range, or a       */
        /* write that would pass a DEVICE's end (bfe_device.c) -- a write      */
        /* cut short otherwise comes back as ERROR_WRITE_FAULT, which falls    */
        /* to EIO below.                                                       */
        if (bWrite)
        {   /* A write that did not complete. */
            return(EXT2_ET_SHORT_WRITE);
        }

        /* A read that did not complete. */
        return(EXT2_ET_SHORT_READ);
    }

    if (iError == ERROR_ACCESS_DENIED)
    {   /* A write on a read-only target. */
        return(EROFS);
    }

    if (iError == ERROR_INVALID_HANDLE)
    {   /* The target is not open: a channel used after its close. */
        return(ENXIO);
    }

    /* Anything else the media or the OS reported -- a bad sector, a card   */
    /* pulled out, a write cut short.  EIO is what e2fsck's error hooks     */
    /* treat as a real I/O failure; the Windows number itself is kept by    */
    /* bfe_blockdev.c for the engine's message.                             */
    return(EIO);
}

/*----------------------------------------------------------------------------*/
/* BFIO_Open:                                                                 */
/*                                                                            */
/* Open a channel on the target BFD_Configure recorded (the name libext2fs    */
/* passes is only the engine's label for messages).  A writable channel on a  */
/* read-only configuration is refused, so a report-only check cannot be       */
/* turned into a write by any path.                                           */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszName  : ignored (see above); must not be NULL, as upstream rules.   */
/*     iFlags   : IO_FLAG_RW for a writable channel; the other IO_FLAG_       */
/*                values (exclusive, direct, threads) have no meaning here.   */
/*     pChannel : receives the channel.                                       */
/*                                                                            */
/* Returns:                                                                   */
/*     errcode_t : 0; EXT2_ET_BAD_DEVICE_NAME for a NULL name; EROFS for a    */
/*                 writable channel on a read-only target; EXT2_ET_NO_MEMORY; */
/*                 or BFD_TargetOpen's errno (EIO, ENOMEM, EINVAL).           */
/*----------------------------------------------------------------------------*/
static errcode_t BFIO_Open(const char *pszName, int iFlags, io_channel *pChannel)
{
    io_channel    io;
    BFIO_Channel *pPrivate;
    errcode_t     retval;
    uint64_t      ullLength;
    int           iError;

    if (pszName == NULL)
    {   /* libext2fs never does this, but its managers all guard it. */
        return(EXT2_ET_BAD_DEVICE_NAME);
    }

    if ((iFlags & IO_FLAG_RW) && BFD_TargetReadOnly())
    {   /* The configuration is read-only: no writable channel on it. */
        return(EROFS);
    }

    if (BFIO_nOpen == 0)
    {   /* The first channel opens the target.  Later ones -- get_backup_sb's   */
        /* probe (e2fsck\util.c), or an external journal's channel; an          */
        /* internal journal reuses the filesystem's own -- share it, so they    */
        /* see the same bytes and nothing is opened twice.                      */
        iError = BFD_TargetOpen(&ullLength);

        if (iError != 0)
        {   /* Could not be opened, or the configured range is bad: the       */
            /* errno-style code says which, and BFD_LastIoText has the OS's   */
            /* words for an open the OS refused.                              */
            return(iError);
        }

        /* The range's length, which bounds every read from now on. */
        BFIO_ullLength = ullLength;
    }

    /* The channel and its private data, zeroed. */
    io       = NULL;
    pPrivate = NULL;
    retval   = ext2fs_get_memzero(sizeof(struct struct_io_channel), &io);

    if (retval == 0)
    {   /* The channel exists: now its private data. */
        retval = ext2fs_get_memzero(sizeof(BFIO_Channel), &pPrivate);
    }

    if (retval == 0)
    {   /* Both exist: the name, copied as upstream's managers copy it. */
        retval = ext2fs_get_mem(strlen(pszName) + 1, &io->name);
    }

    if (retval != 0)
    {   /* One of the three allocations failed: undo what was done, the   */
        /* target included when this would have been its first channel.   */
        if (io != NULL)
        {   /* The channel (its name is NULL or not yet allocated). */
            ext2fs_free_mem(&io);
        }

        if (pPrivate != NULL)
        {   /* The private data. */
            ext2fs_free_mem(&pPrivate);
        }

        if (BFIO_nOpen == 0)
        {   /* The target was opened for this channel alone. */
            BFD_TargetClose();
        }

        /* libext2fs' own out-of-memory code, from the failed allocation. */
        return(retval);
    }

    /* The name, into the room just made for it. */
    strcpy(io->name, pszName);

    /* The channel as libext2fs expects it. */
    io->magic        = EXT2_ET_MAGIC_IO_CHANNEL;
    io->manager      = BFIO_Manager();
    io->block_size   = BFIO_DEFAULT_BLOCK;
    io->read_error   = NULL;
    io->write_error  = NULL;
    io->refcount     = 1;
    io->flags        = 0;
    io->align        = 0;
    io->private_data = pPrivate;

    if (BFD_TargetIsDevice())
    {   /* A real disk: libext2fs may then decline things a file allows   */
        /* (zero-out by punching holes, for one).                         */
        io->flags |= CHANNEL_FLAGS_BLOCK_DEVICE;
    }

    /* The private data: marked as ours, and counters that report two   */
    /* fields (bytes read, bytes written), as upstream's managers do.   */
    pPrivate->iMagic           = BFIO_MAGIC;
    pPrivate->stats.num_fields = 2;

    /* One more channel on the target: the last close closes it. */
    BFIO_nOpen++;

    /* The channel is open. */
    *pChannel = io;
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFIO_Close:                                                                */
/*                                                                            */
/* Close a channel: drop a reference, and when it was the last reference and  */
/* the last channel, flush and close the target.  The flush's verdict is the  */
/* close's, as upstream's managers report it, because it is what makes a      */
/* repair's writes real.                                                      */
/*                                                                            */
/* Arguments:                                                                 */
/*     channel : the channel.                                                 */
/*                                                                            */
/* Returns:                                                                   */
/*     errcode_t : 0, EXT2_ET_MAGIC_IO_CHANNEL for a channel that is not      */
/*                 ours, or EIO when the final flush failed.                  */
/*----------------------------------------------------------------------------*/
static errcode_t BFIO_Close(io_channel channel)
{
    BFIO_Channel *pPrivate;
    errcode_t     retval;
    int           iError;

    /* Ours, or refused. */
    pPrivate = BFIO_Check(channel);

    if (pPrivate == NULL)
    {   /* Not ours. */
        return(EXT2_ET_MAGIC_IO_CHANNEL);
    }

    if (--channel->refcount > 0)
    {   /* Other holders remain (libext2fs bumps the count when it hands a   */
        /* channel to a second user).                                        */
        return(0);
    }

    /* The last reference: this channel goes, and one fewer is open. */
    retval = 0;
    BFIO_nOpen--;

    if (BFIO_nOpen == 0)
    {   /* The last channel: make the writes real and close the target.  The   */
        /* target is closed whatever the flush said; its verdict is kept.      */
        iError = BFD_TargetFlush();
        BFD_TargetClose();

        if (iError != 0)
        {   /* The media did not confirm the last writes. */
            retval = EIO;
        }
    }

    /* The channel's memory, in the order upstream frees it.  The magic is   */
    /* cleared first, so a stale pointer to this private data fails the      */
    /* check rather than passing it.                                         */
    pPrivate->iMagic = 0;
    ext2fs_free_mem(&channel->private_data);

    if (channel->name != NULL)
    {   /* The copied name. */
        ext2fs_free_mem(&channel->name);
    }

    /* Then the channel itself (the pointer is a local copy: the caller's   */
    /* handle is now dangling, as with every libext2fs manager).            */
    ext2fs_free_mem(&channel);

    /* Closed; the flush's verdict if it was the last. */
    return(retval);
}

/*----------------------------------------------------------------------------*/
/* BFIO_SetBlksize:                                                           */
/*                                                                            */
/* Record the filesystem's block size, which every block number from now on   */
/* is counted in.  With no cache there is nothing to flush or re-allocate.    */
/*                                                                            */
/* Arguments:                                                                 */
/*     channel  : the channel.                                                */
/*     iBlksize : bytes per block (1024, 2048 or 4096 for ext4; libext2fs     */
/*                also probes with others when it is looking for a            */
/*                superblock).                                                */
/*                                                                            */
/* Returns:                                                                   */
/*     errcode_t : 0, or EXT2_ET_MAGIC_IO_CHANNEL.                            */
/*----------------------------------------------------------------------------*/
static errcode_t BFIO_SetBlksize(io_channel channel, int iBlksize)
{
    if (BFIO_Check(channel) == NULL)
    {   /* Not ours. */
        return(EXT2_ET_MAGIC_IO_CHANNEL);
    }

    /* Taken as given, unchecked, as upstream's unix_io.c takes it (which   */
    /* only has a cache to rebuild besides).                                */
    channel->block_size = iBlksize;

    /* Recorded. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFIO_Transfer:                                                             */
/*                                                                            */
/* The one read-or-write behind the four block functions: a run of blocks     */
/* moved between libext2fs' buffer and the target, held to the filesystem's   */
/* range (a read cut short at its end, a write refused if it would pass it),  */
/* a failure offered to the channel's error hook when one is installed.       */
/*                                                                            */
/* Arguments:                                                                 */
/*     channel  : the channel (checked already).                              */
/*     pPrivate : its private data, for the counters.                         */
/*     ullBlock : the first block, from the start of the filesystem.          */
/*     iCount   : blocks to transfer, or minus a byte count.                  */
/*     pvData   : the buffer.                                                 */
/*     bWrite   : TRUE to write from it, FALSE to read into it.               */
/*                                                                            */
/* Returns:                                                                   */
/*     errcode_t : 0, or the mapped error -- or the error hook's answer.      */
/*----------------------------------------------------------------------------*/
static errcode_t BFIO_Transfer(io_channel channel, BFIO_Channel *pPrivate,
                               unsigned long long ullBlock, int iCount, void *pvData, int bWrite)
{
    uint64_t  cbWanted;
    uint64_t  cbInside;
    uint64_t  ullStart;
    errcode_t retval;
    int       iError;

    if (iCount < 0)
    {   /* A byte count, as libext2fs spells an odd-sized transfer.  Negated   */
        /* in 64 bits, so even the most negative int becomes its magnitude     */
        /* rather than overflowing.                                            */
        cbWanted = (uint64_t)(-(int64_t)iCount);
    }
    else
    {   /* Whole blocks: a 32-bit count times the block size, widened first. */
        cbWanted = (uint64_t)iCount * (uint64_t)channel->block_size;
    }

    /* Where it sits, and how much of it lies inside the filesystem's range    */
    /* (BFIO_ullLength bytes from its start).  The block's byte offset is      */
    /* formed only when it fits in 64 bits -- a block number from damaged      */
    /* metadata or a journal tag can be anything -- and the room is taken      */
    /* as length minus start, never start plus count, so nothing can wrap.     */
    /* The block-size test is a guard: BFIO_SetBlksize takes whatever it is    */
    /* given, but libext2fs and e2fsck pass only a filesystem's or a probe's   */
    /* block size, always positive (openfs.c, util.c).  It keeps the same      */
    /* test's division from dividing by zero and leaves nothing inside; it     */
    /* does not make the cbWanted above meaningful for such a size.            */
    ullStart = 0;
    cbInside = 0;

    if (channel->block_size > 0 && ullBlock <= UINT64_MAX / (uint64_t)channel->block_size)
    {   /* The offset fits: what of the request lies inside the range. */
        ullStart = ullBlock * (uint64_t)channel->block_size;

        if (ullStart < BFIO_ullLength)
        {   /* It starts inside: as much of it as there is room for. */
            cbInside = BFIO_ullLength - ullStart;

            if (cbInside > cbWanted)
            {   /* All of it fits. */
                cbInside = cbWanted;
            }
        }
    }

    if (bWrite)
    {   /* A WRITE is held to the range whole: one that reaches past the      */
        /* filesystem's end, by any amount, is refused before a byte is       */
        /* written, never cut short to the part that fits, so what lies       */
        /* beyond the end (on a card, the next partition) is never touched    */
        /* and no write is left half done by this test.  This covers every    */
        /* writer above, the journal replay included, whose block numbers     */
        /* come from the journal's own tags and are checked against nothing   */
        /* else before it runs.                                               */
        if (cbInside < cbWanted)
        {   /* Past the range: refused as a short write, nothing moved. */
            iError   = ERROR_HANDLE_EOF;
            cbInside = 0;
        }
        else
        {   /* Inside: written in full; the target reports a short write itself. */
            pPrivate->stats.bytes_written += cbWanted;
            iError = BFD_TargetWrite(BFD_TargetOffset() + ullStart, pvData, cbWanted);
        }
    }
    else
    {   /* Read, but only the part inside the filesystem's range. */
        pPrivate->stats.bytes_read += cbWanted;
        iError = BFD_TargetRead(BFD_TargetOffset() + ullStart, pvData, cbInside);

        if (iError == 0 && cbInside < cbWanted)
        {   /* The range ran off the end: the rest reads as zeros and the   */
            /* transfer is reported short, as a read past a file's end is   */
            /* -- which is what e2fsck expects of a truncated image.        */
            memset((uint8_t *)pvData + cbInside, 0, (size_t)(cbWanted - cbInside));
            iError = ERROR_HANDLE_EOF;
        }
    }

    if (iError == 0)
    {   /* Every byte moved. */
        return(0);
    }

    /* The failure in libext2fs' terms, before the hook sees it. */
    retval = BFIO_MapError(iError, bWrite);

    /* The hooks take the block as unsigned long -- 32 bits on Windows -- so   */
    /* past block 2^32 (16 TiB at 4 KiB) their message would name the wrong    */
    /* block; far beyond a card.  For a write the "actual" count passed is     */
    /* 0 for one refused at the range, else the whole span: the target does    */
    /* not say how much of a failed write reached the media.                   */
    if (bWrite && channel->write_error != NULL)
    {   /* e2fsck's handler: prints the block and the reason and decides. */
        return(channel->write_error(channel, (unsigned long)ullBlock, iCount, pvData,
                                    (size_t)cbWanted, (int)cbInside, retval));
    }

    if (!bWrite && channel->read_error != NULL)
    {   /* Likewise for a read. */
        return(channel->read_error(channel, (unsigned long)ullBlock, iCount, pvData,
                                   (size_t)cbWanted, (int)cbInside, retval));
    }

    /* No hook: the mapped error itself. */
    return(retval);
}

/*----------------------------------------------------------------------------*/
/* BFIO_ReadBlk64:                                                            */
/*                                                                            */
/* Read a run of blocks -- the 64-bit block read libext2fs calls (the 32-bit  */
/* BFIO_ReadBlk forwards here).  A run past the filesystem's end comes back   */
/* zero-filled and short, through BFIO_Transfer.                              */
/*                                                                            */
/* Arguments:                                                                 */
/*     channel  : the channel.                                                */
/*     ullBlock : the first block, from the start of the filesystem.          */
/*     iCount   : blocks, or minus a byte count.                              */
/*     pvData   : receives the bytes.                                         */
/*                                                                            */
/* Returns:                                                                   */
/*     errcode_t : 0, EXT2_ET_MAGIC_IO_CHANNEL, or BFIO_Transfer's result.    */
/*----------------------------------------------------------------------------*/
static errcode_t BFIO_ReadBlk64(io_channel channel, unsigned long long ullBlock, int iCount, void *pvData)
{
    BFIO_Channel *pPrivate;

    /* Ours, or refused. */
    pPrivate = BFIO_Check(channel);

    if (pPrivate == NULL)
    {   /* Not ours. */
        return(EXT2_ET_MAGIC_IO_CHANNEL);
    }

    /* Into the caller's buffer. */
    return(BFIO_Transfer(channel, pPrivate, ullBlock, iCount, pvData, FALSE));
}

/*----------------------------------------------------------------------------*/
/* BFIO_WriteBlk64:                                                           */
/*                                                                            */
/* Write a run of blocks -- the 64-bit block write libext2fs calls (the       */
/* 32-bit BFIO_WriteBlk forwards here).                                       */
/*                                                                            */
/* Arguments:                                                                 */
/*     channel  : the channel.                                                */
/*     ullBlock : the first block, from the start of the filesystem.          */
/*     iCount   : blocks, or minus a byte count.                              */
/*     pvData   : the bytes to write.                                         */
/*                                                                            */
/* Returns:                                                                   */
/*     errcode_t : 0, EXT2_ET_MAGIC_IO_CHANNEL, or BFIO_Transfer's result.    */
/*----------------------------------------------------------------------------*/
static errcode_t BFIO_WriteBlk64(io_channel channel, unsigned long long ullBlock, int iCount, const void *pvData)
{
    BFIO_Channel *pPrivate;

    /* Ours, or refused. */
    pPrivate = BFIO_Check(channel);

    if (pPrivate == NULL)
    {   /* Not ours. */
        return(EXT2_ET_MAGIC_IO_CHANNEL);
    }

    /* Out of the caller's buffer, which a write leaves untouched; the cast   */
    /* only fits the shared transfer function's signature.                    */
    return(BFIO_Transfer(channel, pPrivate, ullBlock, iCount, (void *)pvData, TRUE));
}

/*----------------------------------------------------------------------------*/
/* BFIO_ReadBlk:                                                              */
/*                                                                            */
/* The 32-bit block read older libext2fs code paths still call; the block     */
/* number widens without loss, and the 64-bit form does the work.             */
/*                                                                            */
/* Arguments:                                                                 */
/*     channel : the channel.                                                 */
/*     ulBlock : the first block, from the start of the filesystem.           */
/*     iCount  : blocks, or minus a byte count.                               */
/*     pvData  : receives the bytes.                                          */
/*                                                                            */
/* Returns:                                                                   */
/*     errcode_t : as BFIO_ReadBlk64.                                         */
/*----------------------------------------------------------------------------*/
static errcode_t BFIO_ReadBlk(io_channel channel, unsigned long ulBlock, int iCount, void *pvData)
{
    /* The 64-bit form does the work. */
    return(BFIO_ReadBlk64(channel, ulBlock, iCount, pvData));
}

/*----------------------------------------------------------------------------*/
/* BFIO_WriteBlk:                                                             */
/*                                                                            */
/* The 32-bit block write older libext2fs code paths still call; the 64-bit   */
/* form does the work.                                                        */
/*                                                                            */
/* Arguments:                                                                 */
/*     channel : the channel.                                                 */
/*     ulBlock : the first block, from the start of the filesystem.           */
/*     iCount  : blocks, or minus a byte count.                               */
/*     pvData  : the bytes to write.                                          */
/*                                                                            */
/* Returns:                                                                   */
/*     errcode_t : as BFIO_WriteBlk64.                                        */
/*----------------------------------------------------------------------------*/
static errcode_t BFIO_WriteBlk(io_channel channel, unsigned long ulBlock, int iCount, const void *pvData)
{
    /* The 64-bit form does the work. */
    return(BFIO_WriteBlk64(channel, ulBlock, iCount, pvData));
}

/*----------------------------------------------------------------------------*/
/* BFIO_WriteByte:                                                            */
/*                                                                            */
/* Write a few bytes at a byte offset from the start of the filesystem.       */
/* libext2fs uses this to write just the changed words of the primary         */
/* superblock when the manager offers it (upstream's Windows manager does     */
/* not, and the whole block is rewritten instead); here it costs nothing      */
/* extra, since the target is byte-addressed and a device's sector rules are  */
/* met below it.  Held to the filesystem's range like every block write: a    */
/* span reaching past the end is refused whole, before anything is written.   */
/*                                                                            */
/* Arguments:                                                                 */
/*     channel  : the channel.                                                */
/*     ulOffset : the byte offset from the start of the filesystem.           */
/*     iCount   : how many bytes.                                             */
/*     pvData   : them.                                                       */
/*                                                                            */
/* Returns:                                                                   */
/*     errcode_t : 0, EXT2_ET_MAGIC_IO_CHANNEL, EXT2_ET_INVALID_ARGUMENT (a   */
/*                 negative count), EXT2_ET_SHORT_WRITE (past the range), or  */
/*                 the mapped error (no hook is consulted: upstream's         */
/*                 managers do not for this call).                            */
/*----------------------------------------------------------------------------*/
static errcode_t BFIO_WriteByte(io_channel channel, unsigned long ulOffset, int iCount, const void *pvData)
{
    BFIO_Channel *pPrivate;
    int           iError;

    /* Ours, or refused. */
    pPrivate = BFIO_Check(channel);

    if (pPrivate == NULL)
    {   /* Not ours. */
        return(EXT2_ET_MAGIC_IO_CHANNEL);
    }

    if (iCount < 0)
    {   /* No such thing as a negative byte count. */
        return(EXT2_ET_INVALID_ARGUMENT);
    }

    /* Inside the range, or refused whole.  Compared as the room left after   */
    /* the offset, so neither a huge offset nor a huge count can wrap.        */
    if ((uint64_t)ulOffset > BFIO_ullLength || (uint64_t)iCount > BFIO_ullLength - (uint64_t)ulOffset)
    {   /* Some of it would land past the filesystem's end: nothing written. */
        return(EXT2_ET_SHORT_WRITE);
    }

    /* Counted, then written at the filesystem's start plus the offset. */
    pPrivate->stats.bytes_written += (uint64_t)iCount;
    iError = BFD_TargetWrite(BFD_TargetOffset() + (uint64_t)ulOffset, pvData, (uint64_t)iCount);

    if (iError != 0)
    {   /* Refused or failed: the mapped code, with no hook consulted (as   */
        /* upstream).  libext2fs falls back to a whole-superblock write     */
        /* only for EXT2_ET_UNIMPLEMENTED (closefs.c), which this never     */
        /* returns -- so any failure here is the superblock write's own     */
        /* failure, passed up as it is.                                     */
        return(BFIO_MapError(iError, TRUE));
    }

    /* Written. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFIO_Flush:                                                                */
/*                                                                            */
/* Make the writes real.  With no cache of its own, the channel only asks the */
/* target, which asks the OS.                                                 */
/*                                                                            */
/* Arguments:                                                                 */
/*     channel : the channel.                                                 */
/*                                                                            */
/* Returns:                                                                   */
/*     errcode_t : 0, EXT2_ET_MAGIC_IO_CHANNEL, or EIO.                       */
/*----------------------------------------------------------------------------*/
static errcode_t BFIO_Flush(io_channel channel)
{
    if (BFIO_Check(channel) == NULL)
    {   /* Not ours. */
        return(EXT2_ET_MAGIC_IO_CHANNEL);
    }

    if (BFD_TargetFlush() != 0)
    {   /* The media did not confirm. */
        return(EIO);
    }

    /* Flushed. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFIO_SetOption:                                                            */
/*                                                                            */
/* The "name?option=value" settings a command line can attach to a device     */
/* (an offset, for one).  The engine's offset is configured, not parsed, and  */
/* nothing here takes an option, so every one is refused.                     */
/*                                                                            */
/* Arguments:                                                                 */
/*     channel   : the channel.                                               */
/*     pszOption : the option's name.                                         */
/*     pszArg    : its value, or NULL.                                        */
/*                                                                            */
/* Returns:                                                                   */
/*     errcode_t : EXT2_ET_INVALID_ARGUMENT, or EXT2_ET_MAGIC_IO_CHANNEL.     */
/*----------------------------------------------------------------------------*/
static errcode_t BFIO_SetOption(io_channel channel, const char *pszOption, const char *pszArg)
{
    const char *pszUnused;

    if (BFIO_Check(channel) == NULL)
    {   /* Not ours. */
        return(EXT2_ET_MAGIC_IO_CHANNEL);
    }

    /* The option and its value are not consulted; dummy assignments keep the   */
    /* parameters "used".                                                       */
    pszUnused = pszOption;
    pszUnused = pszArg;
    pszUnused = pszUnused;

    /* No option is taken. */
    return(EXT2_ET_INVALID_ARGUMENT);
}

/*-----------------------------------------------------------------------------*/
/* BFIO_GetStats:                                                              */
/*                                                                             */
/* Hand out the channel's byte counters (e2fsck's -t report reads them).       */
/*                                                                             */
/* Arguments:                                                                  */
/*     channel : the channel.                                                  */
/*     pStats  : receives a pointer to the counters, which stay the channel's. */
/*                                                                             */
/* Returns:                                                                    */
/*     errcode_t : 0, or EXT2_ET_MAGIC_IO_CHANNEL.                             */
/*-----------------------------------------------------------------------------*/
static errcode_t BFIO_GetStats(io_channel channel, io_stats *pStats)
{
    BFIO_Channel *pPrivate;

    /* Ours, or refused. */
    pPrivate = BFIO_Check(channel);

    if (pPrivate == NULL)
    {   /* Not ours. */
        return(EXT2_ET_MAGIC_IO_CHANNEL);
    }

    if (pStats != NULL)
    {   /* The caller wants them. */
        *pStats = &pPrivate->stats;
    }

    /* Handed out. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFIO_Discard:                                                              */
/*                                                                            */
/* Discard (TRIM) of freed blocks: not offered.  It is an optimisation a file */
/* or a card does not need, and upstream's Windows manager declines it too;   */
/* the answer is the code libext2fs treats as "not here", after which it does */
/* without.                                                                   */
/*                                                                            */
/* Arguments:                                                                 */
/*     channel  : the channel.                                                */
/*     ullBlock : the first block (unused).                                   */
/*     ullCount : how many (unused).                                          */
/*                                                                            */
/* Returns:                                                                   */
/*     errcode_t : EXT2_ET_UNIMPLEMENTED, as upstream answers, or             */
/*                 EXT2_ET_MAGIC_IO_CHANNEL.                                  */
/*----------------------------------------------------------------------------*/
static errcode_t BFIO_Discard(io_channel channel, unsigned long long ullBlock, unsigned long long ullCount)
{
    unsigned long long ullUnused;

    if (BFIO_Check(channel) == NULL)
    {   /* Not ours. */
        return(EXT2_ET_MAGIC_IO_CHANNEL);
    }

    /* The range is not consulted; dummy assignments keep the parameters   */
    /* "used".                                                             */
    ullUnused = ullBlock;
    ullUnused = ullCount;
    ullUnused = ullUnused;

    /* Not offered. */
    return(EXT2_ET_UNIMPLEMENTED);
}

/*----------------------------------------------------------------------------*/
/* BFIO_Zeroout:                                                              */
/*                                                                            */
/* Zero a run of blocks cheaply (by punching holes, say): not offered.        */
/* libext2fs falls back to writing zeros itself; upstream's Windows manager   */
/* ends by answering the same code for files and devices alike.               */
/*                                                                            */
/* Arguments:                                                                 */
/*     channel  : the channel.                                                */
/*     ullBlock : the first block (unused).                                   */
/*     ullCount : how many (unused).                                          */
/*                                                                            */
/* Returns:                                                                   */
/*     errcode_t : EXT2_ET_UNIMPLEMENTED, or EXT2_ET_MAGIC_IO_CHANNEL.        */
/*----------------------------------------------------------------------------*/
static errcode_t BFIO_Zeroout(io_channel channel, unsigned long long ullBlock, unsigned long long ullCount)
{
    unsigned long long ullUnused;

    if (BFIO_Check(channel) == NULL)
    {   /* Not ours. */
        return(EXT2_ET_MAGIC_IO_CHANNEL);
    }

    /* As for discard. */
    ullUnused = ullBlock;
    ullUnused = ullCount;
    ullUnused = ullUnused;

    /* Not offered. */
    return(EXT2_ET_UNIMPLEMENTED);
}

/*----------------------------------------------------------------------------*/
/* BFIO_CacheReadahead:                                                       */
/*                                                                            */
/* A hint to read blocks ahead into a cache: there is no cache here to warm.  */
/*                                                                            */
/* Arguments:                                                                 */
/*     channel  : the channel.                                                */
/*     ullBlock : the first block (unused).                                   */
/*     ullCount : how many (unused).                                          */
/*                                                                            */
/* Returns:                                                                   */
/*     errcode_t : EXT2_ET_OP_NOT_SUPPORTED -- what upstream's managers       */
/*                 answer, and what its callers test for -- or                */
/*                 EXT2_ET_MAGIC_IO_CHANNEL.                                  */
/*----------------------------------------------------------------------------*/
static errcode_t BFIO_CacheReadahead(io_channel channel, unsigned long long ullBlock, unsigned long long ullCount)
{
    unsigned long long ullUnused;

    if (BFIO_Check(channel) == NULL)
    {   /* Not ours. */
        return(EXT2_ET_MAGIC_IO_CHANNEL);
    }

    /* As for discard. */
    ullUnused = ullBlock;
    ullUnused = ullCount;
    ullUnused = ullUnused;

    /* No cache to warm. */
    return(EXT2_ET_OP_NOT_SUPPORTED);
}

/*----------------------------------------------------------------------------*/
/* The manager table, in libext2fs' layout, and its accessor.  POSITIONAL:    */
/* the entries must follow struct struct_io_manager's member order in         */
/* e2fsprogs\lib\ext2fs\ext2_io.h (magic, name, open ... zeroout, then        */
/* reserved[14]), so an e2fsprogs update that adds a member before the        */
/* reserved words needs its entry added here at the same place.  "{ 0 }"      */
/* zeroes the reserved words.                                                 */
/*----------------------------------------------------------------------------*/
static struct struct_io_manager BFIO_manager =
{
    EXT2_ET_MAGIC_IO_MANAGER,               // magic
    "BifrostFM I/O Manager",                // name
    BFIO_Open,                              // open
    BFIO_Close,                             // close
    BFIO_SetBlksize,                        // set_blksize
    BFIO_ReadBlk,                           // read_blk
    BFIO_WriteBlk,                          // write_blk
    BFIO_Flush,                             // flush
    BFIO_WriteByte,                         // write_byte
    BFIO_SetOption,                         // set_option
    BFIO_GetStats,                          // get_stats
    BFIO_ReadBlk64,                         // read_blk64
    BFIO_WriteBlk64,                        // write_blk64
    BFIO_Discard,                           // discard
    BFIO_CacheReadahead,                    // cache_readahead
    BFIO_Zeroout,                           // zeroout
    { 0 }                                   // reserved
};

/*----------------------------------------------------------------------------*/
/* BFIO_Manager:                                                              */
/*                                                                            */
/* Hand out the manager for ext2fs_open2 and whoever else opens a channel.    */
/*                                                                            */
/* Arguments:                                                                 */
/*     None.                                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     io_manager : the table above; never NULL.                              */
/*----------------------------------------------------------------------------*/
io_manager BFIO_Manager(void)
{
    /* The one static table. */
    return(&BFIO_manager);
}

/*----------------------------------------------------------------------------*/
/* BFIO_Length:                                                               */
/*                                                                            */
/* The filesystem range's length, learned when the first channel opened the   */
/* target (see the header).                                                   */
/*                                                                            */
/* Arguments:                                                                 */
/*     None.                                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     uint64_t : the length in bytes, or 0 while no channel is open.         */
/*----------------------------------------------------------------------------*/
uint64_t BFIO_Length(void)
{
    if (BFIO_nOpen == 0)
    {   /* Nothing open: the figure would be stale. */
        return(0);
    }

    /* As the open reported it. */
    return(BFIO_ullLength);
}
