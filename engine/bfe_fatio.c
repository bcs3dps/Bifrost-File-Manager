/*----------------------------------------------------------------------------*/
/* bfe_fatio.c                                                                */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* FatFs's device layer: the five functions its diskio.h declares             */
/* (disk_initialize, disk_status, disk_read, disk_write, disk_ioctl) and the  */
/* timestamp source ff.h asks for (get_fattime), written over the engine's    */
/* own block device rather than over a card driver.  FatFs's example glue,    */
/* upstream's diskio.c, is not compiled: this file replaces it.               */
/*                                                                            */
/* WHY THE BLOCK DEVICE'S TARGET FUNCTIONS (bfe_blockdev.h): they are the     */
/* interface its second user, the filesystem check, already drives, and they  */
/* carry every device rule in one place -- the configured byte range (a       */
/* partition inside a disk image or on a card), the aligned bounce buffer a   */
/* device needs, the read-only refusal, and the read-back of device writes    */
/* that catches a counterfeit card.  So a FAT filesystem gets all of them     */
/* exactly as an ext4 one does, and none is written twice.  The caller        */
/* configures the target (BFD_Configure) before FatFs mounts or formats, and  */
/* closes it (BFD_TargetClose) once FatFs is done with it.                    */
/*                                                                            */
/* ONE DRIVE.  FatFs is built with one volume (ffconf.h FF_VOLUMES 1) and the */
/* range is applied here, not by FatFs's own partition support                */
/* (FF_MULTI_PARTITION 0), so drive 0 is the only one; any other drive        */
/* number is refused.                                                         */
/*----------------------------------------------------------------------------*/

/* FatFs's types and its device-layer declarations (ff.h first: diskio.h      */
/* uses its BYTE, UINT and LBA_t); the engine's block device for the target   */
/* functions; this file's own header; and the C library's clock for the       */
/* timestamps.                                                                */
#include "ff.h"
#include "diskio.h"
#include "bfe_blockdev.h"
#include "bfe_fatio.h"

#include <stdint.h>
#include <time.h>

/*----------------------------------------------------------------------------*/
/* Definitions -- documentation table.                                        */
/*----------------------------------------------------------------------------*/
#define BFA_DRIVE           0        // the one drive number FatFs is built with (FF_VOLUMES 1)
#define BFA_SECTOR_DEFAULT  512      // the sector size until a caller sets the device's own
#define BFA_FAT_EPOCH_YEAR  1980     // the first year a FAT date can hold (its year field counts from here)
#define BFA_FAT_LAST_YEAR   2107     // the last: the year field is seven bits, 1980 + 127

/*----------------------------------------------------------------------------*/
/* Globals -- documentation table.  One worker thread drives the engine, so   */
/* these need no lock (FatFs is built non-reentrant, FF_FS_REENTRANT 0).      */
/*----------------------------------------------------------------------------*/
static uint32_t BFA_uiSectorBytes = BFA_SECTOR_DEFAULT;  // the sector size FatFs counts in; set by BFA_SetSectorSize
static uint64_t BFA_ullRangeBytes;                       // the open range's length in bytes, from BFD_TargetOpen; 0 while nothing is open

/*----------------------------------------------------------------------------*/
/* BFA_SetSectorSize:                                                         */
/*                                                                            */
/* Set the sector size the next mount or format is told and counts in.  A     */
/* file is 512; a device reports its own (bfe_device.c), and a 4K-sector      */
/* card must be told 4096 or FatFs would read a FAT in the wrong units.       */
/*                                                                            */
/* Arguments:                                                                 */
/*     uiSectorBytes : 512, 1024, 2048 or 4096 -- the range ffconf.h's        */
/*                     FF_MIN_SS / FF_MAX_SS allow.                           */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0 when taken; -1 for any other value, the size left as it was.   */
/*----------------------------------------------------------------------------*/
int BFA_SetSectorSize(uint32_t uiSectorBytes)
{
    if (uiSectorBytes < FF_MIN_SS || uiSectorBytes > FF_MAX_SS ||
        (uiSectorBytes & (uiSectorBytes - 1)) != 0)
    {   /* Outside the sizes FatFs is built for, or not a power of two:   */
        /* refused, so a mount never runs with a size FatFs cannot use.   */
        return(-1);
    }

    /* Taken; the next disk_initialize and every sector number after it   */
    /* count in it.                                                       */
    BFA_uiSectorBytes = uiSectorBytes;

    /* Set. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* disk_initialize:                                                           */
/*                                                                            */
/* FatFs's first call on a drive at a mount or a format: open the configured  */
/* target, if it is not open already, and learn the range's length.  The      */
/* target is opened here, by FatFs's own request, and not before, so a        */
/* caller that only configures and then mounts needs nothing else.            */
/*                                                                            */
/* Arguments:                                                                 */
/*     pdrv : the drive number; only 0 exists.                                */
/*                                                                            */
/* Returns:                                                                   */
/*     DSTATUS : 0 for a writable target, STA_PROTECT for a read-only one,    */
/*               STA_NOINIT when the drive does not exist or the target       */
/*               could not be opened.                                         */
/*----------------------------------------------------------------------------*/
DSTATUS disk_initialize(BYTE pdrv)
{
    uint64_t ullLength;

    if (pdrv != BFA_DRIVE)
    {   /* A drive FatFs is not built with: not ready. */
        return(STA_NOINIT);
    }

    if (!BFD_TargetIsOpen())
    {   /* Not open yet: open what BFD_Configure recorded, in its mode.   */
        /* A failure (no file, no Administrator for a device, a range     */
        /* past the end) leaves nothing open and the drive not ready,     */
        /* which FatFs reports as FR_NOT_READY.                           */
        ullLength = 0;
        if (BFD_TargetOpen(&ullLength) != 0)
        {   /* Not opened. */
            BFA_ullRangeBytes = 0;
            return(STA_NOINIT);
        }

        /* The range's length bounds every sector FatFs may ask for. */
        BFA_ullRangeBytes = ullLength;
    }

    /* Open: say whether it may be written, which FatFs checks before a   */
    /* write or a format and answers FR_WRITE_PROTECTED.                  */
    return(disk_status(pdrv));
}

/*----------------------------------------------------------------------------*/
/* disk_status:                                                               */
/*                                                                            */
/* The drive's state as FatFs asks for it before its work: open or not, and   */
/* write-protected or not.                                                    */
/*                                                                            */
/* Arguments:                                                                 */
/*     pdrv : the drive number; only 0 exists.                                */
/*                                                                            */
/* Returns:                                                                   */
/*     DSTATUS : STA_NOINIT when not open (or no such drive), STA_PROTECT     */
/*               when open read-only, 0 when open writable.                   */
/*----------------------------------------------------------------------------*/
DSTATUS disk_status(BYTE pdrv)
{
    if (pdrv != BFA_DRIVE || !BFD_TargetIsOpen())
    {   /* No such drive, or nothing open on it. */
        return(STA_NOINIT);
    }

    if (BFD_TargetReadOnly())
    {   /* Opened read-only, as a read-only session's target always is. */
        return(STA_PROTECT);
    }

    /* Open and writable. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* disk_read:                                                                 */
/*                                                                            */
/* Read whole sectors, counted from the start of the RANGE, into FatFs's      */
/* buffer.  The range's own offset is added here, so FatFs's sector 0 is the  */
/* partition's first sector and nothing outside the range is ever read.       */
/*                                                                            */
/* Arguments:                                                                 */
/*     pdrv   : the drive number; only 0 exists.                              */
/*     buff   : where the bytes go, count sectors long.                       */
/*     sector : the first sector, counted from the range's start.             */
/*     count  : how many sectors.                                             */
/*                                                                            */
/* Returns:                                                                   */
/*     DRESULT : RES_OK; RES_NOTRDY when nothing is open; RES_PARERR for no   */
/*               such drive, no sectors, or sectors past the range's end;     */
/*               RES_ERROR when the read itself failed (the block device has  */
/*               noted the Windows error for the engine's message).           */
/*----------------------------------------------------------------------------*/
DRESULT disk_read(BYTE pdrv, BYTE *buff, LBA_t sector, UINT count)
{
    uint64_t ullFirst;
    uint64_t ullBytes;

    if (pdrv != BFA_DRIVE || count == 0)
    {   /* No such drive, or nothing asked for. */
        return(RES_PARERR);
    }

    if (!BFD_TargetIsOpen())
    {   /* Nothing open to read. */
        return(RES_NOTRDY);
    }

    /* Where the read starts in the range, and how long it is, in bytes.   */
    /* 64-bit arithmetic, so a large sector number cannot wrap.            */
    ullFirst = (uint64_t)sector * BFA_uiSectorBytes;
    ullBytes = (uint64_t)count * BFA_uiSectorBytes;

    if (ullFirst > BFA_ullRangeBytes || ullBytes > BFA_ullRangeBytes - ullFirst)
    {   /* Past the range's end: refused, so a damaged FAT pointing past     */
        /* its own volume can never read the next partition.  The first      */
        /* test comes first, so the subtraction in the second cannot wrap.   */
        return(RES_PARERR);
    }

    if (BFD_TargetRead(BFD_TargetOffset() + ullFirst, buff, ullBytes) != 0)
    {   /* The read failed, or came back short. */
        return(RES_ERROR);
    }

    /* Read. */
    return(RES_OK);
}

/*----------------------------------------------------------------------------*/
/* disk_write:                                                                */
/*                                                                            */
/* Write whole sectors, counted from the start of the RANGE, from FatFs's     */
/* buffer -- through the block device, so a device's write is read back and   */
/* compared when the session asked for that, and a write the card did not     */
/* keep fails here and stays noted for the call's end (BFD_WriteNotKept).     */
/*                                                                            */
/* Arguments:                                                                 */
/*     pdrv   : the drive number; only 0 exists.                              */
/*     buff   : the bytes, count sectors long.                                */
/*     sector : the first sector, counted from the range's start.             */
/*     count  : how many sectors.                                             */
/*                                                                            */
/* Returns:                                                                   */
/*     DRESULT : RES_OK; RES_NOTRDY when nothing is open; RES_WRPRT on a      */
/*               read-only target; RES_PARERR for no such drive, no sectors,  */
/*               or sectors past the range's end; RES_ERROR when the write    */
/*               failed or was not kept.                                      */
/*----------------------------------------------------------------------------*/
DRESULT disk_write(BYTE pdrv, const BYTE *buff, LBA_t sector, UINT count)
{
    uint64_t ullFirst;
    uint64_t ullBytes;

    if (pdrv != BFA_DRIVE || count == 0)
    {   /* No such drive, or nothing to write. */
        return(RES_PARERR);
    }

    if (!BFD_TargetIsOpen())
    {   /* Nothing open to write. */
        return(RES_NOTRDY);
    }

    if (BFD_TargetReadOnly())
    {   /* A read-only target: refused before the block device is asked,   */
        /* as FatFs expects of a write-protected drive.                    */
        return(RES_WRPRT);
    }

    /* Where the write starts in the range, and how long it is, in bytes. */
    ullFirst = (uint64_t)sector * BFA_uiSectorBytes;
    ullBytes = (uint64_t)count * BFA_uiSectorBytes;

    if (ullFirst > BFA_ullRangeBytes || ullBytes > BFA_ullRangeBytes - ullFirst)
    {   /* Past the range's end: refused, so nothing outside the volume's   */
        /* own range -- the next partition -- is ever written.              */
        return(RES_PARERR);
    }

    if (BFD_TargetWrite(BFD_TargetOffset() + ullFirst, buff, ullBytes) != 0)
    {   /* The write failed, or (with the read-back on) the device did not   */
        /* keep it.                                                          */
        return(RES_ERROR);
    }

    /* Written. */
    return(RES_OK);
}

/*----------------------------------------------------------------------------*/
/* disk_ioctl:                                                                */
/*                                                                            */
/* The four controls FatFs uses: make the writes real (CTRL_SYNC, at every    */
/* f_sync and close), and, for a format, the range's size in sectors, the     */
/* sector size, and the erase-block size.  Anything else is refused.          */
/*                                                                            */
/* Arguments:                                                                 */
/*     pdrv : the drive number; only 0 exists.                                */
/*     cmd  : the control (diskio.h's CTRL_* / GET_*).                        */
/*     buff : where a GET_* answer goes, in the type FatFs reads it as.       */
/*                                                                            */
/* Returns:                                                                   */
/*     DRESULT : RES_OK; RES_NOTRDY when nothing is open; RES_ERROR when the  */
/*               flush failed; RES_PARERR for no such drive or another        */
/*               control.                                                     */
/*----------------------------------------------------------------------------*/
DRESULT disk_ioctl(BYTE pdrv, BYTE cmd, void *buff)
{
    if (pdrv != BFA_DRIVE)
    {   /* No such drive. */
        return(RES_PARERR);
    }

    if (!BFD_TargetIsOpen())
    {   /* Nothing open to ask about. */
        return(RES_NOTRDY);
    }

    switch (cmd)
    {   /* One control per case. */
        case CTRL_SYNC:
            /* The writes made real.  A read-only target has none, and its   */
            /* flush is harmless.                                            */
            if (BFD_TargetFlush() != 0)
            {   /* The flush failed: what FatFs wrote may not have landed. */
                return(RES_ERROR);
            }

            /* Flushed. */
            return(RES_OK);

        case GET_SECTOR_COUNT:
            /* The range's whole sectors, the volume f_mkfs may fill; a      */
            /* ragged end smaller than a sector is not offered.  LBA_t is    */
            /* 64-bit in this build (ffconf.h FF_LBA64), so every range's    */
            /* count fits and the clamp below cannot fire; it is kept so a   */
            /* build with FF_LBA64 off, where LBA_t is 32-bit, answers a     */
            /* larger range with the most LBA_t holds -- a format then       */
            /* filling only that much -- instead of a count that wraps.      */
            if (BFA_ullRangeBytes / BFA_uiSectorBytes > (uint64_t)(LBA_t)~(LBA_t)0)
            {   /* More sectors than LBA_t can number (a 32-bit build only). */
                *(LBA_t *)buff = (LBA_t)~(LBA_t)0;
            }
            else
            {   /* Fits: the exact count. */
                *(LBA_t *)buff = (LBA_t)(BFA_ullRangeBytes / BFA_uiSectorBytes);
            }

            /* Answered. */
            return(RES_OK);

        case GET_SECTOR_SIZE:
            /* The size set for this mount (BFA_SetSectorSize), which FatFs   */
            /* reads as a WORD.                                               */
            *(WORD *)buff = (WORD)BFA_uiSectorBytes;
            return(RES_OK);

        case GET_BLOCK_SIZE:
            /* The erase-block size, in sectors, which f_mkfs aligns the      */
            /* data area to.  A file has none and a card does not report      */
            /* its own through Windows, so 1 -- FatFs's documented answer     */
            /* for "unknown" -- and the format chooses its usual alignment.   */
            *(DWORD *)buff = 1;
            return(RES_OK);

        default:
            /* A control FatFs does not use in this build (CTRL_TRIM is off,   */
            /* FF_USE_TRIM 0) or a card-specific one: refused.                 */
            return(RES_PARERR);
    }
}

/*----------------------------------------------------------------------------*/
/* get_fattime:                                                               */
/*                                                                            */
/* The present moment, packed as FAT stores a timestamp, for every entry      */
/* FatFs creates or changes.  FAT keeps LOCAL time, not UTC -- which is what  */
/* Windows and Linux's vfat both write -- to two-second resolution, from      */
/* 1980 to 2107; a clock outside that is held at the nearer end.              */
/*                                                                            */
/* Arguments:                                                                 */
/*     None.                                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     DWORD : bits 31-25 the year from 1980, 24-21 the month (1-12), 20-16   */
/*             the day, 15-11 the hour, 10-5 the minute, 4-0 the seconds      */
/*             halved.                                                        */
/*----------------------------------------------------------------------------*/
DWORD get_fattime(void)
{
    time_t     tNow;
    struct tm *pLocal;
    int        iYear;

    /* The local calendar time.  localtime's buffer is static, which is safe   */
    /* here: the engine runs on one worker thread.                             */
    tNow   = time(NULL);
    pLocal = localtime(&tNow);

    if (pLocal == NULL)
    {   /* The clock cannot be read: the first moment FAT can hold, 1980-01-01   */
        /* 00:00, rather than a field of garbage.                                */
        return((DWORD)((1u << 21) | (1u << 16)));
    }

    /* The year, held inside what the seven-bit field can count. */
    iYear = pLocal->tm_year + 1900;

    if (iYear < BFA_FAT_EPOCH_YEAR)
    {   /* A clock set before 1980: the earliest year FAT holds. */
        iYear = BFA_FAT_EPOCH_YEAR;
    }

    if (iYear > BFA_FAT_LAST_YEAR)
    {   /* Past 2107: the last year FAT holds. */
        iYear = BFA_FAT_LAST_YEAR;
    }

    /* Packed field by field; tm_mon counts from 0, FAT's month from 1. */
    return((DWORD)((uint32_t)(iYear - BFA_FAT_EPOCH_YEAR) << 25 |
                   (uint32_t)(pLocal->tm_mon + 1) << 21 |
                   (uint32_t)pLocal->tm_mday << 16 |
                   (uint32_t)pLocal->tm_hour << 11 |
                   (uint32_t)pLocal->tm_min << 5 |
                   (uint32_t)pLocal->tm_sec / 2));
}
