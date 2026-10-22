/*----------------------------------------------------------------------------*/
/* bfe_fat.c                                                                  */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The FatFs side of a FAT session: mounting a FAT12, FAT16, FAT32 or exFAT   */
/* filesystem where BFE_Open found no ext2/3/4 one, unmounting it, listing a  */
/* directory and reporting the volume's facts.  bifrost_engine.c owns the     */
/* session and its gate and calls these with the busy flag held; each         */
/* records its own failure with BFX_Fail, so the caller only releases the     */
/* flag.  Beside them are the FAT halves of the session's two shared helpers  */
/* -- one path's facts (BFX_FillEntry) and a directory's names                */
/* (BFX_CollectNames) -- which answer in lwext4's errno terms, as those       */
/* helpers do, and record nothing; the one translation of a FatFs result      */
/* into such an errno, which bfe_fs.c uses too; and two facts the engine's    */
/* FAT paths need -- the letter-case fold FatFs matches names by              */
/* (BFT_PathFoldPrefix) and the largest file the volume holds                 */
/* (BFT_MaxFileBytes).  FatFs reaches the image or card through bfe_fatio.c,  */
/* its device layer over the engine's block device, so the configured range,  */
/* the read-only refusal and a device's alignment hold here as they do for    */
/* ext4.                                                                      */
/*                                                                            */
/* WHAT A FAT ENTRY LOOKS LIKE TO THE PROGRAM.  FAT has no owners, modes,     */
/* links or inode numbers, and only files and directories.  So an entry is    */
/* reported as a file or a directory; its mode is made up -- 0644 for a file, */
/* 0755 for a directory, with the write bits taken away when the entry has    */
/* FAT's read-only attribute -- its owner and group are 0, its link count 1   */
/* and its inode 0; and its one time, the last modification, stands for all   */
/* three.  FAT keeps that time in LOCAL time to two seconds, so it is turned  */
/* into UTC through the host's own time zone.                                 */
/*                                                                            */
/* WRITES.  A FAT session may be writable; the writes are bfe_fs.c's FAT      */
/* arms over FatFs, with the helpers here for what FatFs has no single call   */
/* for (a remove that clears the read-only attribute, a recursive remove, a   */
/* time set in local time).  The calls for modes, owners and links are        */
/* refused at the gate before they could reach FatFs.                         */
/*                                                                            */
/* MAKING A VOLUME.  Last in the file, outside any session: BFE_MakeFs and    */
/* BFE_MakeFsAt (bfe_mkfs.c) judge a FAT format's cluster size and label here */
/* before anything is written, then have FatFs's f_mkfs lay the volume over   */
/* the configured range and f_setlabel name it.                               */
/*----------------------------------------------------------------------------*/

/* FatFs's API and types, and its device-layer functions (diskio.h), through    */
/* which a new volume's boot sector label is written; the engine's internals    */
/* (the error recorder, the longest name a BFE_Entry holds, the root's path,    */
/* and the name-list free BFX_CollectNames's callers use); the block device's   */
/* target, which this file closes after an unmount, a failed mount and a        */
/* format; FatFs's device layer, told the sector size before a mount or a       */
/* format; and this file's own header.                                          */
#include "ff.h"
#include "diskio.h"
#include "bfe_internal.h"
#include "bfe_blockdev.h"
#include "bfe_fatio.h"
#include "bfe_fat.h"

/* The C library: memory and strings for the entry and name lists, the       */
/* clock's calendar conversion for FAT's local times, and the errno values   */
/* a FatFs result is translated into.                                        */
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <errno.h>

/*----------------------------------------------------------------------------*/
/* Definitions -- documentation table.                                        */
/*----------------------------------------------------------------------------*/
#define BFT_DRIVE           ""       // FatFs's path for its one drive: no volume prefix, so drive 0 (ffconf.h FF_VOLUMES 1)
#define BFT_MOUNT_NOW       1        // f_mount's option: mount at once, so a range that holds no FAT volume fails here rather than at the first listing
#define BFT_ENTRIES_FIRST   64       // initial capacity of a collected entry or name list; doubles as it fills
#define BFT_LABEL_BYTES     48       // room for a label from f_getlabel in UTF-8: exFAT's 11 UTF-16 units are at most 33 bytes and a NUL, and FatFs asks for 34 at least
#define BFT_MODE_FILE       0644u    // the mode a FAT file is shown with
#define BFT_MODE_DIR        0755u    // the mode a FAT directory is shown with
#define BFT_MODE_WRITE_BITS 0222u    // the write bits taken away for an entry with FAT's read-only attribute
#define BFT_FAT_YEAR_BASE   1980     // a FAT date's year field counts from 1980

#define BFT_MKFS_WORK_BYTES   (512u * 1024u)               // f_mkfs's working buffer: FatFs writes the FATs, the bitmap and the up-case table a buffer at a time, so a larger one means fewer writes; a multiple of every sector size
#define BFT_FAT_COPIES        2                            // FATs on a FAT12 / 16 / 32 volume made here: two, as Windows' and dosfstools' formatters make (as found, not checkable here; FatFs's own default is one, ff.c f_mkfs); exFAT always has one
#define BFT_AU_MAX_SECTORS    128u                         // FAT12 / 16 / 32's largest cluster, in sectors; f_mkfs silently clamps a larger one to it
#define BFT_EXFAT_AU_MAX      (16u * 1024u * 1024u)        // exFAT's largest cluster, bytes: f_mkfs's own ceiling, past which it silently chooses its own
#define BFT_FAT_LABEL_MAX     11                           // a FAT12 / 16 / 32 label's most characters: the 11 bytes of a short-name entry
#define BFT_EXFAT_LABEL_UNITS 11                           // an exFAT label's most UTF-16 units (a character past U+FFFF takes two)
#define BFT_LABEL_BAD_FAT     "+.,;=[]/*:<>|\\\"?"         // the characters f_setlabel refuses in a FAT12 / 16 / 32 label (ff.c's badchr, its DEL apart, tested separately)
#define BFT_LABEL_BAD_EXFAT   "/*:<>|\\\"?"                // the characters it refuses in an exFAT label (badchr from its eighth, DEL apart)
#define BFT_LABEL_FREE_MARK   0xE5                         // a directory entry whose first byte is this is a deleted one (FatFs's DDEM), so f_setlabel refuses a FAT label starting with it
#define BFT_DEL               0x7F                         // DEL: a control character as far as a label is concerned, refused as f_setlabel refuses it

#define BFT_LABEL_OK          0                            // Bft_LabelToOem: the label converts, at most BFT_FAT_LABEL_MAX bytes
#define BFT_LABEL_NOT_UTF8    1                            // Bft_LabelToOem: the label is not well-formed UTF-8
#define BFT_LABEL_BAD_CHAR    2                            // Bft_LabelToOem: a control character, DEL, a character past U+FFFF or outside the code page, one FAT refuses, or a deleted-entry mark first
#define BFT_LABEL_TOO_LONG    3                            // Bft_LabelToOem: more than BFT_FAT_LABEL_MAX bytes in the code page

#define BFT_CODE_NOT_UTF8     0x80000000u                  // Bft_NextCode's code for a byte that does not start a well-formed UTF-8 character, OR-ed with that byte: above every Unicode code point, so it matches only the same byte

#define BFT_PDRV              0                            // FatFs's physical drive number for its one drive, which bfe_fatio.c's disk_* functions answer for
#define BFT_BS_BOOTSIG        38                           // FAT12 / 16 boot sector: the extended boot signature byte, 0x29 when the label field below exists (FAT specification, BS_BootSig)
#define BFT_BS_VOLLAB         43                           // FAT12 / 16 boot sector: the 11-byte volume label field (BS_VolLab)
#define BFT_BS_BKBOOTSEC32    50                           // FAT32 boot sector: the sector number of the backup boot sector, 16 bits little-endian, 0 for none (BPB_BkBootSec)
#define BFT_BS_BOOTSIG32      66                           // FAT32 boot sector: the extended boot signature byte (BS_BootSig)
#define BFT_BS_VOLLAB32       71                           // FAT32 boot sector: the 11-byte volume label field (BS_VolLab)
#define BFT_EXT_BOOT_SIG      0x29                         // the extended boot signature's value: the serial number, label and type fields follow

/*----------------------------------------------------------------------------*/
/* Globals -- documentation table.  Touched only by a call holding the        */
/* engine's busy flag, so they need no lock of their own.                     */
/*----------------------------------------------------------------------------*/
static FATFS Bft_fs;                 // FatFs's work area for the mounted volume; registered with FatFs from BFT_Mount to BFT_Unmount, and by BFT_MakeFs for a new volume's label (no session open then)

/*----------------------------------------------------------------------------*/
/* Bft_MapResult:                                                             */
/*                                                                            */
/* Translate a FatFs result into the engine's status code, as BFX_MapErrno    */
/* does for lwext4's: only the results a caller acts on differently get       */
/* their own code, and FatFs's wording goes in the detail line (Bft_Text).    */
/*                                                                            */
/* Arguments:                                                                 */
/*     fr : a FatFs result.                                                   */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK for FR_OK, otherwise the matching BFE_ERR_* code.         */
/*----------------------------------------------------------------------------*/
static int Bft_MapResult(FRESULT fr)
{
    switch (fr)
    {   /* One FatFs result per case. */

        /* Success. */
        case FR_OK:              return(BFE_OK);

        /* No such file, or a missing directory on the way to it. */
        case FR_NO_FILE:         return(BFE_ERR_NOT_FOUND);
        case FR_NO_PATH:         return(BFE_ERR_NOT_FOUND);

        /* A name FAT cannot hold, or a path FatFs cannot parse. */
        case FR_INVALID_NAME:    return(BFE_ERR_ARG);

        /* The name is taken. */
        case FR_EXIST:           return(BFE_ERR_EXISTS);

        /* The drive is write-protected: the target was opened read-only. */
        case FR_WRITE_PROTECTED: return(BFE_ERR_READ_ONLY);

        /* The device layer could not open the target at all. */
        case FR_NOT_READY:       return(BFE_ERR_IMAGE_OPEN);

        /* No FAT or exFAT volume in the range. */
        case FR_NO_FILESYSTEM:   return(BFE_ERR_MOUNT);

        /* FatFs could not allocate its long-name buffer. */
        case FR_NOT_ENOUGH_CORE: return(BFE_ERR_NO_MEMORY);

        /* Anything else: a read that failed, a damaged volume, an         */
        /* internal check -- all failures of the image, as for lwext4 --   */
        /* and FR_DENIED, a refusal (no room, a full directory), which     */
        /* this file's own calls on a mounted volume are not expected to   */
        /* meet: they list, stat and read the volume's facts; a format's   */
        /* label goes into a root just made empty.                         */
        default:                 return(BFE_ERR_IO_IMAGE);
    }
}

/*----------------------------------------------------------------------------*/
/* Bft_Text:                                                                  */
/*                                                                            */
/* A FatFs result in words, for a detail line -- FatFs has no message table   */
/* of its own, only the enumeration's names and their comments in ff.h.       */
/*                                                                            */
/* Arguments:                                                                 */
/*     fr : a FatFs result.                                                   */
/*                                                                            */
/* Returns:                                                                   */
/*     const char * : a fixed phrase; never NULL, never to be freed.          */
/*----------------------------------------------------------------------------*/
static const char *Bft_Text(FRESULT fr)
{
    switch (fr)
    {   /* One FatFs result per case, worded after ff.h's own comment on it. */
        case FR_OK:                  return("success");
        case FR_DISK_ERR:            return("a read or write of the image failed");
        case FR_INT_ERR:             return("FatFs found its own state inconsistent (a damaged volume)");
        case FR_NOT_READY:           return("the image or device could not be opened");
        case FR_NO_FILE:             return("no such file");
        case FR_NO_PATH:             return("no such directory");
        case FR_INVALID_NAME:        return("the name is not valid on FAT");
        case FR_DENIED:              return("access denied, or the directory is full");
        case FR_EXIST:               return("the name is already taken");
        case FR_INVALID_OBJECT:      return("the file or directory object is not valid");
        case FR_WRITE_PROTECTED:     return("the volume is open read-only");
        case FR_INVALID_DRIVE:       return("no such drive");
        case FR_NOT_ENABLED:         return("no volume is mounted");
        case FR_NO_FILESYSTEM:       return("no FAT or exFAT volume found");
        case FR_MKFS_ABORTED:        return("the format could not be made");
        case FR_TIMEOUT:             return("timed out waiting for the volume");
        case FR_LOCKED:              return("the file is in use");
        case FR_NOT_ENOUGH_CORE:     return("out of memory for a long name");
        case FR_TOO_MANY_OPEN_FILES: return("too many open files");
        case FR_INVALID_PARAMETER:   return("an argument is not valid");

        /* A value this build of FatFs does not define. */
        default:                     return("an unknown FatFs result");
    }
}

/*----------------------------------------------------------------------------*/
/* BFT_FatErrno:                                                              */
/*                                                                            */
/* Translate a FatFs result into an errno value -- the one lwext4 gives for   */
/* the same failure, where FatFs's result names it -- so code written for     */
/* lwext4's results (a test for ENOENT, BFX_MapErrno's mapping to a           */
/* BFE_ERR_* code) reads a FAT session's alike.  The one place this           */
/* translation is made: bfe_fs.c's FAT arms and this file's errno-style       */
/* helpers (BFT_StatEntry, BFT_CollectNames, BFT_Unlink and the remove and    */
/* time helpers below) all call it, so a FatFs result is never turned into    */
/* an errno two ways.  It takes the result as an int so that its callers'     */
/* headers need not include FatFs's to declare it.                            */
/*                                                                            */
/* FR_DENIED becomes ENOSPC, as lwext4 reports a full filesystem: on every    */
/* route the engine takes to FatFs it means no room -- no free cluster for a  */
/* new directory, no free entry in a directory (a FAT12 / 16 root's fixed     */
/* size reaches it) -- since the other refusals FatFs gives it are kept off   */
/* those routes: a read-only entry has the attribute cleared before it is     */
/* removed (BFT_Unlink), every overwrite removes the old entry first, and a   */
/* directory is removed only once it is empty.  BFT_Unlink, whose FR_DENIED   */
/* can only mean a directory with entries left, answers ENOTEMPTY itself.     */
/*                                                                            */
/* INTENTIONALLY DEFERRED: FR_INVALID_NAME and FR_INVALID_PARAMETER           */
/* become EINVAL, which BFX_MapErrno reads as an I/O failure.  Only an        */
/* engine caller reaches it: BFX_PathValid refuses a name holding a           */
/* character FAT forbids before FatFs sees it, and the program caps a FAT     */
/* name at the 255 units FatFs takes.                                         */
/*                                                                            */
/* Arguments:                                                                 */
/*     iResult : a FatFs FRESULT, as an int.                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0 for FR_OK, otherwise the matching errno value (EIO for any     */
/*           failure with no closer match).                                   */
/*----------------------------------------------------------------------------*/
int BFT_FatErrno(int iResult)
{
    switch ((FRESULT)iResult)
    {   /* One FatFs result per case. */

        /* Success. */
        case FR_OK:              return(0);

        /* No such file, or a missing directory on the way to it. */
        case FR_NO_FILE:         return(ENOENT);
        case FR_NO_PATH:         return(ENOENT);

        /* The name is taken. */
        case FR_EXIST:           return(EEXIST);

        /* A name FAT cannot hold, or an argument FatFs refuses.  lwext4    */
        /* has no such refusal to copy; BFX_MapErrno reads EINVAL as an     */
        /* I/O failure (a defect intentionally deferred: see the header).   */
        case FR_INVALID_NAME:    return(EINVAL);
        case FR_INVALID_PARAMETER: return(EINVAL);

        /* The volume is open read-only. */
        case FR_WRITE_PROTECTED: return(EROFS);

        /* FatFs could not allocate its long-name buffer. */
        case FR_NOT_ENOUGH_CORE: return(ENOMEM);

        /* No room: no free cluster, or no free entry in the directory --   */
        /* the only FR_DENIED the engine's routes can meet (the header      */
        /* says why), so it reads as the full filesystem it is.             */
        case FR_DENIED:          return(ENOSPC);

        /* Anything else: a failed read or a damaged volume, an I/O   */
        /* failure as lwext4 reports its own.                         */
        default:                 return(EIO);
    }
}

/*----------------------------------------------------------------------------*/
/* Bft_FatTimeToEpoch:                                                        */
/*                                                                            */
/* Turn a FAT date and time -- LOCAL time, packed into two 16-bit words -- to */
/* UTC epoch seconds, the unit every BFE_Entry time is in.  A card written in */
/* another time zone shows that zone's clock, which FAT cannot record.        */
/*                                                                            */
/* Arguments:                                                                 */
/*     wDate : bits 15-9 the year from 1980, 8-5 the month (1-12), 4-0 the    */
/*             day (1-31).                                                    */
/*     wTime : bits 15-11 the hour, 10-5 the minute, 4-0 the seconds halved.  */
/*                                                                            */
/* Returns:                                                                   */
/*     uint32_t : the epoch seconds; 0 when the date holds no month or day    */
/*                (an entry written with no date), when a field holds a       */
/*                value no date or time has (a damaged entry), or when the    */
/*                host cannot convert it; the largest uint32_t for a time     */
/*                past 2106, which FAT's 2107 can reach and a BFE_Entry       */
/*                cannot hold.                                                */
/*----------------------------------------------------------------------------*/
static uint32_t Bft_FatTimeToEpoch(WORD wDate, WORD wTime)
{
    struct tm local;
    time_t    tEpoch;
    int       iYear;
    int       iMonth;
    int       iDay;
    int       iHour;
    int       iMinute;
    int       iSecondsHalved;
    int       iMonthDays;

    /* The packed fields, taken apart: the year from 1980, the month and   */
    /* the day; the hour, the minute and the seconds halved (FAT keeps     */
    /* time to two seconds).                                               */
    iYear          = (int)((wDate >> 9) & 0x7F) + BFT_FAT_YEAR_BASE;
    iMonth         = (int)((wDate >> 5) & 0x0F);
    iDay           = (int)(wDate & 0x1F);
    iHour          = (int)((wTime >> 11) & 0x1F);
    iMinute        = (int)((wTime >> 5) & 0x3F);
    iSecondsHalved = (int)(wTime & 0x1F);

    if (iMonth == 0 || iDay == 0)
    {   /* Month or day 0: no date was recorded.  0 is reported rather than   */
        /* a date mktime would invent by rolling the fields over.             */
        return(0);
    }

    /* The length of the date's month: February by the Gregorian leap rule   */
    /* (2000 is a leap year, 2100 is not -- both in FAT's range), April,     */
    /* June, September and November 30 days, every other month 31.           */
    if (iMonth == 2)
    {   /* February: 29 days in a leap year, 28 otherwise. */
        if ((iYear % 4 == 0 && iYear % 100 != 0) || iYear % 400 == 0)
        {   /* A leap year. */
            iMonthDays = 29;
        }
        else
        {   /* An ordinary year. */
            iMonthDays = 28;
        }
    }
    else if (iMonth == 4 || iMonth == 6 || iMonth == 9 || iMonth == 11)
    {   /* A month of 30 days. */
        iMonthDays = 30;
    }
    else
    {   /* A month of 31 days (or a month past 12, refused just below). */
        iMonthDays = 31;
    }

    if (iMonth > 12 || iDay > iMonthDays || iHour > 23 || iMinute > 59 || iSecondsHalved > 29)
    {   /* A value no date or time has -- month 13 to 15, a day past its        */
        /* month's end, hour 24 to 31, minute 60 to 63, 60 or 62 seconds: a     */
        /* damaged entry, or one a careless tool wrote.  0, "no time", as for   */
        /* no date at all: handed to mktime, the fields would roll over into    */
        /* some other, real date (month 13 of 2020 becomes January 2021) that   */
        /* the listing would then show as the entry's time.                     */
        return(0);
    }

    /* The local calendar time the two words hold, field by field.  tm's     */
    /* month counts from 0 and its year from 1900; tm_isdst -1 lets mktime   */
    /* decide from the date whether daylight saving was in force.            */
    memset(&local, 0, sizeof(local));
    local.tm_year  = iYear - 1900;
    local.tm_mon   = iMonth - 1;
    local.tm_mday  = iDay;
    local.tm_hour  = iHour;
    local.tm_min   = iMinute;
    local.tm_sec   = iSecondsHalved * 2;
    local.tm_isdst = -1;

    /* Local to UTC epoch seconds, through the host's own time zone and      */
    /* daylight-saving rules -- daylight saving decided by the date itself   */
    /* (tm_isdst -1 above).  Whether that matches how Windows shows the      */
    /* same entry is not established.                                        */
    tEpoch = mktime(&local);

    if (tEpoch == (time_t)-1 || tEpoch < 0)
    {   /* The host could not convert it: reported as no time. */
        return(0);
    }

    if ((uint64_t)tEpoch > 0xFFFFFFFFull)
    {   /* Past what a 32-bit time holds (early 2106): held at its last second. */
        return(0xFFFFFFFFu);
    }

    /* The UTC time. */
    return((uint32_t)tEpoch);
}

/*----------------------------------------------------------------------------*/
/* Bft_FillFacts:                                                             */
/*                                                                            */
/* Lay a FatFs item's facts -- everything but the name -- into a BFE_Entry,   */
/* made up where FAT has no such fact (the file header says how).  Shared by  */
/* the listing (Bft_FillEntry) and one path's facts (BFT_StatEntry), which    */
/* take the name from different places, so a FAT entry reads the same both    */
/* ways.                                                                      */
/*                                                                            */
/* Arguments:                                                                 */
/*     pInfo  : FatFs's item, from f_readdir or f_stat.                       */
/*     pEntry : receives the facts; the caller has zeroed it, so the inode,   */
/*              owner and group stay 0, which is what FAT has for them.       */
/*                                                                            */
/* Returns:                                                                   */
/*     void : the type, mode, size, link count and three times are set.       */
/*----------------------------------------------------------------------------*/
static void Bft_FillFacts(const FILINFO *pInfo, BFE_Entry *pEntry)
{
    if ((pInfo->fattrib & AM_DIR) != 0)
    {   /* A directory: FAT records no size for one, so 0 is reported. */
        pEntry->uiType = BFE_TYPE_DIR;
        pEntry->uiMode = BFT_MODE_DIR;
    }
    else
    {   /* A file, with its size (64-bit: exFAT's can pass 4 GiB). */
        pEntry->uiType  = BFE_TYPE_FILE;
        pEntry->uiMode  = BFT_MODE_FILE;
        pEntry->ullSize = (uint64_t)pInfo->fsize;
    }

    if ((pInfo->fattrib & AM_RDO) != 0)
    {   /* FAT's read-only attribute: shown as the write bits taken away. */
        pEntry->uiMode &= ~BFT_MODE_WRITE_BITS;
    }

    /* FAT's attributes themselves, for whoever shows them: the five bits   */
    /* BFE_ATTR_* names, which are FatFs's AM_* and FAT's on-disk values    */
    /* alike, so they pass through unchanged.                               */
    pEntry->uiAttributes = (uint32_t)(pInfo->fattrib & (AM_RDO | AM_HID | AM_SYS | AM_DIR | AM_ARC));

    /* One name, no inode of its own to count links in. */
    pEntry->uiLinks = 1;

    /* The modification time stands for all three: FAT keeps an access   */
    /* DATE only, and no inode-change time.                              */
    pEntry->uiMtime = Bft_FatTimeToEpoch(pInfo->fdate, pInfo->ftime);
    pEntry->uiAtime = pEntry->uiMtime;
    pEntry->uiCtime = pEntry->uiMtime;
}

/*----------------------------------------------------------------------------*/
/* Bft_FillEntry:                                                             */
/*                                                                            */
/* Lay one FatFs directory item out as a BFE_Entry, made up where FAT has no  */
/* such fact (the file header says how; Bft_FillFacts fills them).            */
/*                                                                            */
/* An entry whose name FatFs could not convert is still listed, with type     */
/* BFE_TYPE_UNKNOWN: shown, but not offered as a file or folder.              */
/*                                                                            */
/* Arguments:                                                                 */
/*     pInfo  : FatFs's item, from f_readdir.                                 */
/*     pEntry : receives the facts; zeroed first.                             */
/*                                                                            */
/* Returns:                                                                   */
/*     int : TRUE when filled; FALSE when the name is longer than a BFE_Entry */
/*           holds (BFX_ENTRY_NAME_MAX bytes of UTF-8), and nothing is        */
/*           filled.  A guard: a FAT long name is at most 255 characters,     */
/*           765 bytes, which FatFs's long-name buffer (ffconf.h FF_LFN_BUF)  */
/*           is sized for and szName holds.                                   */
/*----------------------------------------------------------------------------*/
static int Bft_FillEntry(const FILINFO *pInfo, BFE_Entry *pEntry)
{
    size_t cbName;

    /* Start from zeros: the inode, the owner, the group and the reserved   */
    /* parts stay 0, which is what FAT has for them.                        */
    memset(pEntry, 0, sizeof(*pEntry));

    /* The long name, UTF-8 (ffconf.h FF_LFN_UNICODE 2), whole: FF_LFN_BUF   */
    /* is sized so FatFs never cuts it.                                      */
    cbName = strlen(pInfo->fname);

    if (cbName > BFX_ENTRY_NAME_MAX)
    {   /* Longer than szName holds -- which no FAT name is, so a guard.  Not   */
        /* cut: a cut name would be the path of a different entry, or of        */
        /* none.                                                                */
        return(FALSE);
    }

    /* The name, its terminator and its length. */
    memcpy(pEntry->szName, pInfo->fname, cbName + 1);
    pEntry->uiNameBytes = (uint32_t)cbName;

    /* Everything else: type, mode, size, links and times. */
    Bft_FillFacts(pInfo, pEntry);

    if (strcmp(pInfo->fname, "?") == 0)
    {   /* FatFs's mark for a name it could not convert to UTF-8: on exFAT   */
        /* a broken surrogate pair, or a name record a damaged entry set     */
        /* cuts short (ff.c get_fileinfo; a FAT12 / 16 / 32 long name that   */
        /* fails falls back to its short name instead).  No FAT name can     */
        /* be "?", so no path reaches the entry by it, and it is not         */
        /* offered as a file or a folder.  Set after the facts, which set    */
        /* the type this replaces.                                           */
        pEntry->uiType = BFE_TYPE_UNKNOWN;
    }

    /* Filled. */
    return(TRUE);
}

/*----------------------------------------------------------------------------*/
/* BFT_Mount:                                                                 */
/*                                                                            */
/* Mount the configured range as FAT or exFAT -- the volume at the range's    */
/* first sector or nothing, so a whole partitioned disk is refused as lwext4  */
/* refuses it.  The caller has configured the block device (BFD_Configure)    */
/* and lwext4 has just failed to mount the same range, which left the target  */
/* closed.                                                                    */
/*                                                                            */
/* Arguments:                                                                 */
/*     uiSectorBytes : the sector size to count in: 512 for a file, the       */
/*                     device's own figure for a card.                        */
/*     pszExtReason  : lwext4's reason for refusing the range, for the        */
/*                     detail line when FatFs refuses it too.                 */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK with the volume mounted; otherwise the failure's code,    */
/*           recorded, with nothing mounted or open.                          */
/*----------------------------------------------------------------------------*/
int BFT_Mount(uint32_t uiSectorBytes, const char *pszExtReason)
{
    FRESULT fr;

    if (BFA_SetSectorSize(uiSectorBytes) != 0)
    {   /* A sector size FatFs is not built for (outside 512 to 4096, or not   */
        /* a power of two).  Refused before FatFs is called, so nothing is     */
        /* registered or opened: lwext4's failed mount closed the target.      */
        return(BFX_Fail(BFE_ERR_MOUNT, "cannot mount: not an ext2/3/4 filesystem (%s), and %u-byte sectors are outside what FAT is built for here",
                        pszExtReason, (unsigned)uiSectorBytes));
    }

    /* A clean work area, registered and mounted at once (BFT_MOUNT_NOW).   */
    /* FatFs opens the block device's target itself (disk_initialize) and   */
    /* reads the boot sector at the range's first sector -- there only:     */
    /* BifrostFM FatFs patch 2 (ff.c, find_volume) stops it searching a     */
    /* partition table found there.  It decides FAT12, FAT16 or FAT32 by    */
    /* the cluster count (its own bounds, ff.c MAX_FAT12 and MAX_FAT16,     */
    /* which by its comment follow DOS and Windows where they differ from   */
    /* the specification), or finds exFAT's own signature.                  */
    memset(&Bft_fs, 0, sizeof(Bft_fs));
    fr = f_mount(&Bft_fs, BFT_DRIVE, BFT_MOUNT_NOW);

    if (fr != FR_OK)
    {   /* No FAT or exFAT volume there, or the range could not be read. */

        /* Undo what f_mount left: it registers the work area before it   */
        /* tries the volume, and FatFs has no call that closes a drive,   */
        /* so the target disk_initialize opened is closed here.           */
        f_mount(NULL, BFT_DRIVE, 0);
        BFD_TargetClose();

        if (fr == FR_NO_FILESYSTEM)
        {   /* Neither library found its filesystem: the usual "not a     */
            /* filesystem" answer, with lwext4's reason kept beside it.   */
            return(BFX_Fail(BFE_ERR_MOUNT, "cannot mount: not an ext2/3/4 filesystem (%s), nor FAT or exFAT", pszExtReason));
        }

        /* FatFs could not get as far as looking: the range itself failed. */
        return(BFX_Fail(Bft_MapResult(fr), "cannot mount: not an ext2/3/4 filesystem (%s), and trying FAT: %s",
                        pszExtReason, Bft_Text(fr)));
    }

    /* Mounted, until BFT_Unmount. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFT_Unmount:                                                               */
/*                                                                            */
/* End the FAT session: unregister the volume from FatFs and close the block  */
/* device's target.  Nothing is left for it to write.                         */
/*                                                                            */
/* Arguments:                                                                 */
/*     None.                                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK.  BFE_ERR_IO_IMAGE when FatFs refused the unmount -- a    */
/*           guard: f_mount with no work area fails only for an unknown       */
/*           drive, and drive 0 always exists.                                */
/*----------------------------------------------------------------------------*/
int BFT_Unmount(void)
{
    FRESULT fr;

    /* Unregister the work area: FatFs's unmount, which writes nothing.     */
    /* Nothing is pending: FatFs writes its sector window back (sync_fs)    */
    /* at the end of each call that changes the volume -- a file's close,   */
    /* a mkdir, unlink, rename, attribute or time change.                   */
    fr = f_mount(NULL, BFT_DRIVE, 0);

    /* Close the target, which FatFs has no call to do -- its close   */
    /* flushes the file or device (BFD_TargetClose) -- whatever the   */
    /* unmount said, as the session is over either way.               */
    BFD_TargetClose();

    if (fr != FR_OK)
    {   /* FatFs refused -- a guard, as the header says: reported, with the   */
        /* target already closed, as nothing more can be done.                */
        return(BFX_Fail(BFE_ERR_IO_IMAGE, "closing the FAT volume failed: %s", Bft_Text(fr)));
    }

    /* Unmounted and closed. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFT_ListDir:                                                               */
/*                                                                            */
/* List a directory of the FAT volume, keeping BFE_ListDir's promise: every   */
/* entry, or -- when a read fails part-way -- none, so a damaged directory is */
/* never shown as a shorter whole.                                            */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImageDir : the directory, an absolute engine path ("/" for the      */
/*                   root); FatFs reads it from the drive's root.             */
/*     pfnEntry    : called once per entry; a non-zero return stops early.    */
/*     pvContext   : passed to pfnEntry unchanged.                            */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK (also when stopped early); BFE_ERR_NOT_FOUND when the     */
/*           directory does not exist or the path names a file (FatFs's       */
/*           FR_NO_PATH); BFE_ERR_ARG for a path FAT cannot hold;             */
/*           BFE_ERR_NAME when a name is longer than a BFE_Entry holds (a     */
/*           guard: none is); BFE_ERR_NO_MEMORY; BFE_ERR_IO_IMAGE.            */
/*----------------------------------------------------------------------------*/
int BFT_ListDir(const char *pszImageDir, BFE_ListCallback pfnEntry, void *pvContext)
{
    DIR        dir;
    FILINFO    info;
    BFE_Entry *pList;
    BFE_Entry *pGrown;
    int        nCapacity;
    int        nUsed;
    int        iStatus;
    int        i;
    FRESULT    fr;

    /* Open the directory for reading. */
    fr = f_opendir(&dir, pszImageDir);

    if (fr != FR_OK)
    {   /* Missing, not a directory, or unreadable: nothing was opened. */
        return(BFX_Fail(Bft_MapResult(fr), "cannot list %s: %s", pszImageDir, Bft_Text(fr)));
    }

    /* The entries are COLLECTED first and handed to the callback only      */
    /* once the whole directory has been read, so a read that fails         */
    /* part-way hands on none.  No list yet: it is allocated at the first   */
    /* entry, so an empty directory costs no allocation.                    */
    pList     = NULL;
    nCapacity = 0;
    nUsed     = 0;
    iStatus   = BFE_OK;

    for (;;)
    {   /* One directory item per iteration.  Nothing is left out here:    */
        /* FAT has no "." and ".." on its root, and FatFs skips those of   */
        /* a subdirectory itself, as it skips the volume label's entry.    */
        fr = f_readdir(&dir, &info);

        if (fr != FR_OK || info.fname[0] == '\0')
        {   /* A read failed, or the directory has ended (an empty name). */
            break;
        }

        if (nUsed == nCapacity)
        {   /* The list is full: double it (a first allocation starts at   */
            /* BFT_ENTRIES_FIRST), so any directory size is handled.       */
            if (nCapacity == 0)
            {   /* First allocation. */
                nCapacity = BFT_ENTRIES_FIRST;
            }
            else
            {   /* Every later growth doubles. */
                nCapacity = nCapacity * 2;
            }

            /* Grow it; the old list stays valid if this fails. */
            pGrown = (BFE_Entry *)realloc(pList, (size_t)nCapacity * sizeof(BFE_Entry));

            if (pGrown == NULL)
            {   /* Out of memory: stop collecting; the list is freed below. */
                iStatus = BFX_Fail(BFE_ERR_NO_MEMORY, "out of memory listing %s", pszImageDir);
                break;
            }

            /* The grown list replaces the old one. */
            pList = pGrown;
        }

        if (!Bft_FillEntry(&info, &pList[nUsed]))
        {   /* A name a BFE_Entry cannot carry (a guard, Bft_FillEntry says   */
            /* why): the listing fails rather than leave out an entry the     */
            /* user would then never see.                                     */
            iStatus = BFX_Fail(BFE_ERR_NAME, "cannot list %s: the name of an entry in it is longer than %d bytes of UTF-8", pszImageDir, BFX_ENTRY_NAME_MAX);
            break;
        }

        /* Counted in. */
        nUsed++;
    }

    /* Close the directory before the callback sees anything, so nothing   */
    /* the callback does can meet an open FatFs directory.                 */
    f_closedir(&dir);

    if (iStatus == BFE_OK && fr != FR_OK)
    {   /* The walk ended on a failed read: what was collected is not the   */
        /* directory, so none of it is handed on.                           */
        iStatus = BFX_Fail(Bft_MapResult(fr), "cannot list %s: %s", pszImageDir, Bft_Text(fr));
    }

    if (iStatus == BFE_OK)
    {   /* The whole directory was read: hand it over, entry by entry. */
        for (i = 0; i < nUsed; i++)
        {   /* One collected entry per iteration; the caller may stop early. */
            if (pfnEntry(&pList[i], pvContext) != 0)
            {   /* The caller has seen enough. */
                break;
            }
        }
    }

    /* The list is done with, however the listing ended (free(NULL) is   */
    /* harmless for an empty directory).                                 */
    free(pList);

    /* BFE_OK, or the failure recorded above. */
    return(iStatus);
}

/*----------------------------------------------------------------------------*/
/* BFT_StatEntry:                                                             */
/*                                                                            */
/* BFX_FillEntry's FAT arm: one path's facts, laid out as a FAT directory     */
/* item is in a listing (Bft_FillFacts).  The caller has zeroed the entry and */
/* put the name in it, so only the facts are filled here.  Like the lwext4    */
/* arm it answers in errno terms and records nothing -- its callers test for  */
/* ENOENT and map the rest with BFX_MapErrno.                                 */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImagePath : the path, absolute and canonical ("/" for the root).    */
/*     pEntry       : already zeroed and named by the caller; receives the    */
/*                    facts.                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0 when filled; otherwise BFT_FatErrno's errno for FatFs's        */
/*           refusal -- ENOENT when the path does not exist.                  */
/*----------------------------------------------------------------------------*/
int BFT_StatEntry(const char *pszImagePath, BFE_Entry *pEntry)
{
    FILINFO info;
    FRESULT fr;

    if (strcmp(pszImagePath, BFX_MOUNT_POINT) == 0)
    {   /* The root.  FatFs has no directory item for it -- f_stat refuses   */
        /* the root ("origin directory") with FR_INVALID_NAME -- so it is    */
        /* described here: a directory, shown with the directory mode, one   */
        /* link, and no time, which a FAT root does not record.              */
        pEntry->uiType       = BFE_TYPE_DIR;
        pEntry->uiMode       = BFT_MODE_DIR;
        pEntry->uiLinks      = 1;
        pEntry->uiAttributes = BFE_ATTR_DIRECTORY;
        return(0);
    }

    /* The item FatFs finds at the path (a long name matched letter case   */
    /* aside, as FAT names are).                                           */
    memset(&info, 0, sizeof(info));
    fr = f_stat(pszImagePath, &info);

    if (fr != FR_OK)
    {   /* Not there (FR_NO_FILE, or FR_NO_PATH for a missing folder on the   */
        /* way: both ENOENT), or the volume could not be read.  Nothing is    */
        /* recorded: the caller decides what the failure means.               */
        return(BFT_FatErrno((int)fr));
    }

    /* The facts, as a listing shows them. */
    Bft_FillFacts(&info, pEntry);

    /* Filled. */
    return(0);
}

/*-----------------------------------------------------------------------------*/
/* BFT_CollectNames:                                                           */
/*                                                                             */
/* BFX_CollectNames's FAT arm: every entry name of a directory, read into a    */
/* malloc'd array with the directory CLOSED before returning, under the same   */
/* promise -- every name, or (a read failing part-way) none, never a short     */
/* list handed back as the whole.                                              */
/*                                                                             */
/* Arguments:                                                                  */
/*     pszImageDir : the directory, absolute and canonical ("/" for the root). */
/*     pppszNames  : receives the array of malloc'd names (NULL when empty);   */
/*                   freed with BFX_FreeNames.                                 */
/*     pnNames     : receives the count.                                       */
/*                                                                             */
/* Returns:                                                                    */
/*     int : 0 with every name; ENOMEM when the list cannot be allocated;      */
/*           otherwise BFT_FatErrno's errno for FatFs's refusal (no names      */
/*           are handed back on any failure).                                  */
/*-----------------------------------------------------------------------------*/
int BFT_CollectNames(const char *pszImageDir, char ***pppszNames, int *pnNames)
{
    DIR       dir;
    FILINFO   info;
    char    **ppszList;
    char    **ppszGrown;
    char     *pszName;
    size_t    cbName;
    int       nCapacity;
    int       nUsed;
    FRESULT   fr;

    /* The outputs start as "no names", which is also what a failure leaves. */
    *pppszNames = NULL;
    *pnNames    = 0;

    /* Open the directory for reading. */
    fr = f_opendir(&dir, pszImageDir);

    if (fr != FR_OK)
    {   /* Missing, not a directory, or unreadable: nothing was opened.  A   */
        /* path that names a file is ENOENT here (FatFs's FR_NO_PATH),       */
        /* where lwext4 says ENOTDIR.                                        */
        return(BFT_FatErrno((int)fr));
    }

    /* No array yet: it is allocated at the first name, so an empty   */
    /* directory costs no allocation and hands back NULL.             */
    ppszList  = NULL;
    nCapacity = 0;
    nUsed     = 0;

    for (;;)
    {   /* One directory item per iteration.  FatFs has already left out   */
        /* "." and ".." (a FAT root has neither) and the volume label's    */
        /* entry.                                                          */
        fr = f_readdir(&dir, &info);

        if (fr != FR_OK || info.fname[0] == '\0')
        {   /* A read failed, or the directory has ended (an empty name);   */
            /* which of the two is tested once the directory is closed.     */
            break;
        }

        if (nUsed == nCapacity)
        {   /* The array is full: double it (a first allocation starts at   */
            /* BFT_ENTRIES_FIRST), so any directory size is handled.        */
            if (nCapacity == 0)
            {   /* First allocation. */
                nCapacity = BFT_ENTRIES_FIRST;
            }
            else
            {   /* Every later growth doubles. */
                nCapacity = nCapacity * 2;
            }

            /* Grow it; the old array stays valid if this fails. */
            ppszGrown = (char **)realloc(ppszList, (size_t)nCapacity * sizeof(char *));

            if (ppszGrown == NULL)
            {   /* Out of memory: what was collected is freed, the directory   */
                /* closed, and the outputs still say "none".                   */
                BFX_FreeNames(ppszList, nUsed);
                f_closedir(&dir);
                return(ENOMEM);
            }

            /* The grown array replaces the old one. */
            ppszList = ppszGrown;
        }

        /* A copy of the name, since info is overwritten by the next item.    */
        /* Kept whole, however long: a FAT long name can pass ext4's          */
        /* BFX_NAME_MAX bytes of UTF-8, and a cut name would be the path of   */
        /* another entry, or of none.  A name FatFs could not convert ("?",   */
        /* see Bft_FillEntry) is kept too, so the walk that uses the list     */
        /* reports that entry rather than passing over it: no path reaches    */
        /* it, so its facts fail to read.                                     */
        cbName  = strlen(info.fname);
        pszName = (char *)malloc(cbName + 1);

        if (pszName == NULL)
        {   /* Out of memory for this name: free, close, fail, as above. */
            BFX_FreeNames(ppszList, nUsed);
            f_closedir(&dir);
            return(ENOMEM);
        }

        /* The name with its terminator, into the next slot. */
        memcpy(pszName, info.fname, cbName + 1);
        ppszList[nUsed] = pszName;
        nUsed++;
    }

    /* Close the directory BEFORE the caller acts on any name. */
    f_closedir(&dir);

    if (fr != FR_OK)
    {   /* The walk was cut short by a read that failed: the names so far   */
        /* are not the directory, so they are freed and none handed back.   */
        BFX_FreeNames(ppszList, nUsed);
        return(BFT_FatErrno((int)fr));
    }

    /* Hand the list over. */
    *pppszNames = ppszList;
    *pnNames    = nUsed;

    /* The directory is closed and its names are the caller's to free. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFT_Unlink:                                                                */
/*                                                                            */
/* Remove one file or one EMPTY directory from the FAT volume, read-only or   */
/* not -- the engine shows a read-only entry as a mode without its write      */
/* bits, and on Linux its owner may remove a 0444 file all the same.          */
/* errno-style, recording nothing, as lwext4's ext4_fremove.                  */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImagePath : the entry, absolute and canonical.                      */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0, ENOTEMPTY for a directory that still holds entries, or        */
/*           BFT_FatErrno's errno for FatFs's other refusals.                 */
/*----------------------------------------------------------------------------*/
int BFT_Unlink(const char *pszImagePath)
{
    FRESULT fr;

    /* The read-only attribute off first: FatFs refuses to remove an       */
    /* entry that has it (FR_DENIED).  Its result is not looked at -- a    */
    /* missing entry is reported by the unlink itself, just below -- and   */
    /* if the removal then fails, the attribute stays cleared.             */
    f_chmod(pszImagePath, 0, AM_RDO);

    /* The removal. */
    fr = f_unlink(pszImagePath);

    if (fr == FR_DENIED)
    {   /* With the read-only attribute cleared, FatFs's one other refusal   */
        /* here is a directory that still holds entries (ff.c, f_unlink):    */
        /* ENOTEMPTY, as lwext4 says it, and never BFT_FatErrno's ENOSPC,    */
        /* which reads FR_DENIED as no room.                                 */
        return(ENOTEMPTY);
    }

    /* Removed (0), or FatFs's refusal as its errno. */
    return(BFT_FatErrno((int)fr));
}

/*----------------------------------------------------------------------------*/
/* BFT_RemoveTree:                                                            */
/*                                                                            */
/* Remove a FAT directory and everything under it -- what lwext4's            */
/* ext4_dir_rm does for ext4, which FatFs has no call for (its f_unlink takes */
/* an empty directory only).  The first failure stops it, leaving what was    */
/* not yet reached, as ext4_dir_rm does (lwext4 ext4.c: its loop ends at the  */
/* first failure).                                                            */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImageDir : the directory, absolute and canonical (never the root,   */
/*                   which the public calls refuse).                          */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0, ENOMEM, or the errno of the first removal that failed.        */
/*----------------------------------------------------------------------------*/
int BFT_RemoveTree(const char *pszImageDir)
{
    FILINFO   info;
    char    **ppszNames;
    char     *pszChild;
    int       nNames;
    int       rc;
    int       i;
    FRESULT   fr;

    /* The directory's names, read whole and the directory closed BEFORE    */
    /* anything is removed (BFT_CollectNames), so nothing is taken from     */
    /* under an open directory; each child is then removed -- a directory   */
    /* by recursion -- and the emptied directory last.                      */
    rc = BFT_CollectNames(pszImageDir, &ppszNames, &nNames);

    if (rc != 0)
    {   /* Missing, not a directory, or unreadable: nothing removed. */
        return(rc);
    }

    for (i = 0; i < nNames && rc == 0; i++)
    {   /* One child per iteration, until one fails. */
        pszChild = BFU_JoinImage(pszImageDir, ppszNames[i]);

        if (pszChild == NULL)
        {   /* Out of memory for the child's path: the walk stops. */
            rc = ENOMEM;
            break;
        }

        /* A directory is emptied first (recursion); a file goes at once. */
        memset(&info, 0, sizeof(info));
        fr = f_stat(pszChild, &info);

        if (fr != FR_OK)
        {   /* The child cannot be looked at (a failed read, or a "?" name     */
            /* f_stat refuses as FR_INVALID_NAME -- so a folder holding one    */
            /* is never removed): the walk stops there, the loop's test ends   */
            /* it, and the child's path is still freed below.                  */
            rc = BFT_FatErrno((int)fr);
        }
        else if ((info.fattrib & AM_DIR) != 0)
        {   /* A directory: everything under it, then itself. */
            rc = BFT_RemoveTree(pszChild);
        }
        else
        {   /* A file. */
            rc = BFT_Unlink(pszChild);
        }

        /* The child's path is done with. */
        free(pszChild);
    }

    /* The names are done with, however the walk ended. */
    BFX_FreeNames(ppszNames, nNames);

    if (rc == 0)
    {   /* Every child is gone: the directory itself, now empty. */
        rc = BFT_Unlink(pszImageDir);
    }

    /* 0, or the first failure. */
    return(rc);
}

/*----------------------------------------------------------------------------*/
/* BFT_SetModifyTime:                                                         */
/*                                                                            */
/* Set a FAT entry's one time, its last modification, from UTC epoch seconds  */
/* -- the inverse of Bft_FatTimeToEpoch, to FAT's two seconds.                */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImagePath : the entry, absolute and canonical ("/" for the root).   */
/*     uiTime       : the time, UTC epoch seconds.                            */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0, or BFT_FatErrno's errno for FatFs's refusal (EINVAL when the  */
/*           host cannot convert the time).                                   */
/*----------------------------------------------------------------------------*/
int BFT_SetModifyTime(const char *pszImagePath, uint32_t uiTime)
{
    FILINFO    info;
    struct tm *pLocal;
    time_t     tEpoch;
    int        iYear;

    if (strcmp(pszImagePath, BFX_MOUNT_POINT) == 0)
    {   /* The root: FAT records no time for it, so setting one does       */
        /* nothing and succeeds, as a time set on a directory that holds   */
        /* no field for it should.                                         */
        return(0);
    }

    /* The time in the host's zone (localtime), as FAT keeps LOCAL time. */
    tEpoch = (time_t)uiTime;
    pLocal = localtime(&tEpoch);

    if (pLocal == NULL)
    {   /* The host cannot convert it. */
        return(EINVAL);
    }

    /* Packed as FAT packs it: the date's year from 1980, month, day; the   */
    /* time's hour, minute and seconds halved.  The year is held to what    */
    /* the date's seven bits can say.                                       */
    memset(&info, 0, sizeof(info));
    iYear = pLocal->tm_year + 1900;

    if (iYear < BFT_FAT_YEAR_BASE)
    {   /* Before FAT's first day: its first moment, 1980-01-01 00:00:00. */
        info.fdate = (WORD)((1 << 5) | 1);
        info.ftime = 0;
    }
    else if (iYear > BFT_FAT_YEAR_BASE + 127)
    {   /* Past FAT's last year (2107): its last moment.  A guard: a 32-bit   */
        /* time ends early in 2106, so no uiTime reaches it.                  */
        info.fdate = (WORD)((127 << 9) | (12 << 5) | 31);
        info.ftime = (WORD)((23 << 11) | (59 << 5) | 29);
    }
    else
    {   /* Inside FAT's range: the fields as they are, the seconds halved   */
        /* -- an odd second is set as the even one below it.                */
        info.fdate = (WORD)(((iYear - BFT_FAT_YEAR_BASE) << 9) | ((pLocal->tm_mon + 1) << 5) | pLocal->tm_mday);
        info.ftime = (WORD)((pLocal->tm_hour << 11) | (pLocal->tm_min << 5) | (pLocal->tm_sec / 2));
    }

    /* The entry's time, written. */
    return(BFT_FatErrno((int)f_utime(pszImagePath, &info)));
}

/*----------------------------------------------------------------------------*/
/* BFT_MaxFileBytes:                                                          */
/*                                                                            */
/* The largest file the mounted volume can hold: FAT12, FAT16 and FAT32 keep  */
/* a file's size in 32 bits, so 4 GiB less one byte; exFAT keeps 64 bits, so  */
/* no limit a copy could meet.  An import tests a file against it before      */
/* copying anything (bfe_copy.c, BFC_ImportNode's file branch), rather than   */
/* fail at 4 GiB part-way.                                                    */
/*                                                                            */
/* Arguments:                                                                 */
/*     None.                                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     uint64_t : the limit in bytes.                                         */
/*----------------------------------------------------------------------------*/
uint64_t BFT_MaxFileBytes(void)
{
    if (Bft_fs.fs_type == FS_EXFAT)
    {   /* exFAT: 64-bit sizes. */
        return(0xFFFFFFFFFFFFFFFFull);
    }

    /* FAT12 / 16 / 32: 32-bit sizes. */
    return(0xFFFFFFFFull);
}

/*----------------------------------------------------------------------------*/
/* Bft_NextCode:                                                              */
/*                                                                            */
/* Read one character -- one Unicode code point -- from UTF-8 text, and step  */
/* past it.  The engine's paths and labels are UTF-8, while FatFs's name      */
/* matching and label conversion work on code points, so the two FAT name     */
/* tests below read their text through this.  A byte that does not start a    */
/* character is handed back alone, marked, never skipped.                     */
/*                                                                            */
/* Arguments:                                                                 */
/*     ppbText : in, where the character starts (not at the terminating NUL); */
/*               out, the byte after it.                                      */
/*     puiCode : receives the code point, or BFT_CODE_NOT_UTF8 | the byte.    */
/*                                                                            */
/* Returns:                                                                   */
/*     int : TRUE for a well-formed character, FALSE for a lone byte.         */
/*----------------------------------------------------------------------------*/
static int Bft_NextCode(const unsigned char **ppbText, uint32_t *puiCode)
{
    const unsigned char *pb;
    uint32_t             uiCode;
    int                  nMore;
    int                  i;

    /* The character's first byte. */
    pb = *ppbText;

    /* The lead byte says how many continuation bytes follow and gives the      */
    /* code point's top bits.  The character is then held to what FatFs's own   */
    /* decoder (ff.c tchar2uni) takes, below the loop, so a label this file     */
    /* accepts is one f_setlabel accepts too.                                   */
    if (pb[0] < 0x80)
    {   /* ASCII: the byte is the character. */
        uiCode = pb[0];
        nMore  = 0;
    }
    else if ((pb[0] & 0xE0) == 0xC0)
    {   /* Two bytes. */
        uiCode = pb[0] & 0x1Fu;
        nMore  = 1;
    }
    else if ((pb[0] & 0xF0) == 0xE0)
    {   /* Three bytes. */
        uiCode = pb[0] & 0x0Fu;
        nMore  = 2;
    }
    else if ((pb[0] & 0xF8) == 0xF0)
    {   /* Four bytes: past U+FFFF. */
        uiCode = pb[0] & 0x07u;
        nMore  = 3;
    }
    else
    {   /* A continuation byte where a character should start, or a lead      */
        /* of the form 11111xxx, which UTF-8 never uses: that byte alone,     */
        /* marked as not text -- BFT_CODE_NOT_UTF8 with the byte in its low   */
        /* bits, a value no character has, so a comparison still sees it      */
        /* and matches it only with the same byte.                            */
        *puiCode = BFT_CODE_NOT_UTF8 | (uint32_t)pb[0];
        *ppbText = pb + 1;
        return(FALSE);
    }

    for (i = 1; i <= nMore; i++)
    {   /* One continuation byte per iteration: six more bits.  The         */
        /* terminating NUL is not a continuation byte, so a character cut   */
        /* short by the end of the text stops here and nothing past the     */
        /* NUL is read.                                                     */
        if ((pb[i] & 0xC0) != 0x80)
        {   /* Cut short: the lead byte alone, marked as not text; the    */
            /* byte that broke the run is read again as the next start.   */
            *puiCode = BFT_CODE_NOT_UTF8 | (uint32_t)pb[0];
            *ppbText = pb + 1;
            return(FALSE);
        }

        /* Its six bits joined on. */
        uiCode = (uiCode << 6) | (uint32_t)(pb[i] & 0x3Fu);
    }

    if (nMore > 0 && (uiCode < 0x80 || (uiCode >= 0xD800 && uiCode <= 0xDFFF) || uiCode >= 0x110000))
    {   /* Well shaped, but a code FatFs's decoder refuses: an overlong form   */
        /* of an ASCII character (C1 81 for "A"), a surrogate's code, or a     */
        /* value past U+10FFFF.  Taken as a lone lead byte, marked as not      */
        /* text, as a broken run is -- so a label holding one is refused       */
        /* here, before a format, rather than by f_setlabel after the volume   */
        /* is made.                                                            */
        *puiCode = BFT_CODE_NOT_UTF8 | (uint32_t)pb[0];
        *ppbText = pb + 1;
        return(FALSE);
    }

    /* The whole character, and the byte after it. */
    *puiCode = uiCode;
    *ppbText = pb + 1 + nMore;
    return(TRUE);
}

/*----------------------------------------------------------------------------*/
/* BFT_PathFoldPrefix:                                                        */
/*                                                                            */
/* Whether a FAT path begins with another, letter case aside exactly as       */
/* FatFs matches names.  The engine's FAT rename and copy-within tests -- the */
/* same entry spelt another way, a folder moved or copied into itself, a copy */
/* onto a folder that holds it (bifrost_engine.c's rename, bfe_copy.c's copy  */
/* within the image) -- use this, so they agree with what FatFs will treat as */
/* one entry.  Folding with anything else would let the two disagree: a       */
/* case-only rename of a non-ASCII letter refused as taken, or a copy onto    */
/* its own source let through.                                                */
/*                                                                            */
/* The caller passes canonical paths (BFX_PathCanonical), so trailing spaces  */
/* and dots, FAT's other way of spelling one name twice, are already gone;    */
/* and it tests the byte where the match ended -- a NUL for "the same path",  */
/* a '/' for "the other lies under it".                                       */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszPath    : the longer path, or one as long.                          */
/*     pszPrefix  : the path it may begin with.                               */
/*     pcbMatched : receives, on TRUE, how many bytes of pszPath matched the  */
/*                  whole of pszPrefix; untouched on FALSE.                   */
/*                                                                            */
/* Returns:                                                                   */
/*     int : TRUE when every character of pszPrefix matches the character at  */
/*           the same place in pszPath, FALSE otherwise.                      */
/*----------------------------------------------------------------------------*/
int BFT_PathFoldPrefix(const char *pszPath, const char *pszPrefix, size_t *pcbMatched)
{
    const unsigned char *pbPath;
    const unsigned char *pbPrefix;
    uint32_t             uiPath;
    uint32_t             uiPrefix;
    int                  bPathText;
    int                  bPrefixText;

    /* Both from their first byte. */
    pbPath   = (const unsigned char *)pszPath;
    pbPrefix = (const unsigned char *)pszPrefix;

    while (*pbPrefix != '\0')
    {   /* One character of the prefix, and the one at the same place in   */
        /* the path, per iteration.                                        */
        if (*pbPath == '\0')
        {   /* The path ended first: it is shorter than the prefix. */
            return(FALSE);
        }

        /* The two characters as code points, each side stepped on by its       */
        /* own length: a letter and its capital need not be the same number     */
        /* of UTF-8 bytes (U+0250 is two, its capital U+2C6F three), which is   */
        /* why the match's end in pszPath is handed back below rather than      */
        /* the prefix's length assumed.                                         */
        bPathText   = Bft_NextCode(&pbPath, &uiPath);
        bPrefixText = Bft_NextCode(&pbPrefix, &uiPrefix);

        if (bPathText)
        {   /* A character: its capital, by FatFs's own table (ff_wtoupper,    */
            /* which FatFs uses to find a long name and an exFAT name), so a   */
            /* small e-acute (U+00E9) and a capital one (U+00C9) match, as     */
            /* FatFs finds the one entry under either.  A character past       */
            /* U+FFFF comes back unchanged, as FatFs compares its UTF-16       */
            /* halves unchanged.  A lone byte (not text) is left as marked,    */
            /* so it matches only the same byte.                               */
            uiPath = (uint32_t)ff_wtoupper((DWORD)uiPath);
        }

        if (bPrefixText)
        {   /* Likewise. */
            uiPrefix = (uint32_t)ff_wtoupper((DWORD)uiPrefix);
        }

        if (uiPath != uiPrefix)
        {   /* Different characters, letter case aside. */
            return(FALSE);
        }
    }

    /* The whole prefix matched; this much of the path matched it. */
    *pcbMatched = (size_t)(pbPath - (const unsigned char *)pszPath);
    return(TRUE);
}

/*----------------------------------------------------------------------------*/
/* BFT_VolumeInfo:                                                            */
/*                                                                            */
/* The mounted volume's facts in a BFE_VolumeInfo, which was laid out for     */
/* ext4: a cluster stands for a block, and the fields FAT has nothing for --  */
/* inodes, the three ext4 feature words and their flags, an external journal  */
/* -- are 0.  On a large volume the free count can take a moment.             */
/*                                                                            */
/* Arguments:                                                                 */
/*     pInfo : receives the facts; zeroed first.                              */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_IO_IMAGE (or another mapped code) when FatFs     */
/*           cannot count the free clusters or read the label.                */
/*----------------------------------------------------------------------------*/
int BFT_VolumeInfo(BFE_VolumeInfo *pInfo)
{
    FATFS  *pfs;
    DWORD   dwFree;
    char    szLabel[BFT_LABEL_BYTES];
    size_t  cbLabel;
    FRESULT fr;

    /* Zeros for every field FAT has no value for. */
    memset(pInfo, 0, sizeof(*pInfo));

    /* The free clusters, and FatFs's work area for the volume's geometry.    */
    /* f_getfree takes FAT32's FSINFO figure when it is valid, otherwise it   */
    /* scans the FAT (or exFAT's allocation bitmap) -- the slow case.         */
    pfs    = NULL;
    dwFree = 0;
    fr     = f_getfree(BFT_DRIVE, &dwFree, &pfs);

    if (fr != FR_OK || pfs == NULL)
    {   /* The FAT or the bitmap could not be read. */
        return(BFX_Fail(Bft_MapResult(fr), "cannot read the FAT volume's free space: %s", Bft_Text(fr)));
    }

    /* Clusters as blocks: FatFs counts two reserved FAT entries before      */
    /* the first cluster, so the volume holds n_fatent - 2.  The cluster's   */
    /* bytes are its sectors times the sector size of this mount.            */
    pInfo->ullBlockCount = (uint64_t)pfs->n_fatent - 2;
    pInfo->ullFreeBlocks = (uint64_t)dwFree;
    pInfo->uiBlockSize   = (uint32_t)pfs->csize * (uint32_t)pfs->ssize;

    /* Which FAT, as FatFs decided it at the mount (by the cluster count,   */
    /* or exFAT's own signature).                                           */
    pInfo->uiVolumeType = BFE_VOLTYPE_EXFAT;

    if (pfs->fs_type == FS_FAT12)
    {   /* FAT12. */
        pInfo->uiVolumeType = BFE_VOLTYPE_FAT12;
    }
    else if (pfs->fs_type == FS_FAT16)
    {   /* FAT16. */
        pInfo->uiVolumeType = BFE_VOLTYPE_FAT16;
    }
    else if (pfs->fs_type == FS_FAT32)
    {   /* FAT32. */
        pInfo->uiVolumeType = BFE_VOLTYPE_FAT32;
    }

    /* bReadOnly is left 0 here: whether the session is read-only is the    */
    /* session's own state, which BFE_VolumeInfoGet sets after this call.   */

    /* The label, UTF-8; "" when the volume has none.  NULL: the serial   */
    /* number is not wanted.                                              */
    memset(szLabel, 0, sizeof(szLabel));
    fr = f_getlabel(BFT_DRIVE, szLabel, NULL);

    if (fr != FR_OK)
    {   /* The label's entry could not be read. */
        return(BFX_Fail(Bft_MapResult(fr), "cannot read the FAT volume's label: %s", Bft_Text(fr)));
    }

    /* szVolumeName (BFE_VOLUME_NAME_BYTES, its NUL included) is sized for      */
    /* the longest label FatFs keeps -- an exFAT one of 11 UTF-16 units, or a   */
    /* FAT one of 11 code page 437 characters, 33 bytes of UTF-8 -- so every    */
    /* label is copied whole.  The cut below is a guard: should a               */
    /* label ever arrive longer than the field, it is cut back to the last      */
    /* whole character that fits -- the byte at the cut, the first one left     */
    /* out, must not be a continuation byte, or a character would be split --   */
    /* rather than refused, since the label is only shown.                      */
    cbLabel = strlen(szLabel);

    if (cbLabel > sizeof(pInfo->szVolumeName) - 1)
    {   /* Too long: the most that fits, then back to a character's start. */
        cbLabel = sizeof(pInfo->szVolumeName) - 1;

        while (cbLabel > 0 && ((unsigned char)szLabel[cbLabel] & 0xC0) == 0x80)
        {   /* One byte back per iteration while the cut would split a character. */
            cbLabel--;
        }
    }

    /* The label's bytes; the zeroing above terminates them. */
    memcpy(pInfo->szVolumeName, szLabel, cbLabel);

    /* The structure is filled. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* Bft_TypeName:                                                              */
/*                                                                            */
/* A BFE_FSTYPE_* FAT type in words, for a detail line.                       */
/*                                                                            */
/* Arguments:                                                                 */
/*     uiFsType : BFE_FSTYPE_FAT16, BFE_FSTYPE_FAT32 or BFE_FSTYPE_EXFAT.     */
/*                                                                            */
/* Returns:                                                                   */
/*     const char * : a fixed phrase; never NULL, never to be freed.          */
/*----------------------------------------------------------------------------*/
static const char *Bft_TypeName(uint32_t uiFsType)
{
    if (uiFsType == BFE_FSTYPE_FAT32)
    {   /* FAT32. */
        return("FAT32");
    }

    if (uiFsType == BFE_FSTYPE_EXFAT)
    {   /* exFAT. */
        return("exFAT");
    }

    /* The one left: FAT16, which FatFs makes FAT12 when the clusters are   */
    /* too few for FAT16 (ff.c f_mkfs, MAX_FAT12).                          */
    return("FAT16 (or FAT12)");
}

/*----------------------------------------------------------------------------*/
/* Bft_LabelToOem:                                                            */
/*                                                                            */
/* A FAT12 / 16 / 32 label as FAT stores it -- in capitals, in the OEM code   */
/* page (ffconf.h's FF_CODE_PAGE, 437) -- converted exactly as FatFs's        */
/* f_setlabel converts it, and as Windows stores a FAT label (as found, not   */
/* checkable here).  So a label refused here is one f_setlabel would refuse,  */
/* and the bytes made here are the ones it writes, which is what lets         */
/* Bft_WriteBootLabel give the boot sector the same 11 bytes as the root's    */
/* label entry.  Text that is not proper UTF-8 -- broken, overlong, a         */
/* surrogate's code -- is refused here as f_setlabel refuses it.              */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszLabel : the label, UTF-8, NUL-terminated.                           */
/*     abOem    : receives the converted bytes, BFT_FAT_LABEL_MAX of room;    */
/*                not terminated and not padded.                              */
/*     pnBytes  : receives how many bytes abOem holds (on BFT_LABEL_OK).      */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFT_LABEL_OK, BFT_LABEL_NOT_UTF8, BFT_LABEL_BAD_CHAR or          */
/*           BFT_LABEL_TOO_LONG.                                              */
/*----------------------------------------------------------------------------*/
static int Bft_LabelToOem(const char *pszLabel, BYTE *abOem, UINT *pnBytes)
{
    const unsigned char *pbLabel;
    uint32_t             uiCode;
    WCHAR                wcOem;
    UINT                 nBytes;

    /* From the label's first byte, nothing converted yet. */
    pbLabel = (const unsigned char *)pszLabel;
    nBytes  = 0;

    while (*pbLabel != '\0')
    {   /* One character per iteration. */
        if (!Bft_NextCode(&pbLabel, &uiCode))
        {   /* Not proper UTF-8 -- broken, or a code FatFs's decoder refuses:   */
            /* not text at all, refused before anything is formatted.           */
            return(BFT_LABEL_NOT_UTF8);
        }

        if (uiCode < 0x20 || uiCode == BFT_DEL || uiCode >= 0x10000)
        {   /* A control character -- refused here where f_setlabel would      */
            /* silently END the label at it -- or DEL, which f_setlabel        */
            /* refuses too; or a character past U+FFFF, which no single-byte   */
            /* code page holds (f_setlabel turns it into 0 and refuses it as   */
            /* one the code page lacks).                                       */
            return(BFT_LABEL_BAD_CHAR);
        }

        /* Its capital, as FatFs's table makes it, then its byte in the      */
        /* code page -- the same two FatFs calls f_setlabel makes,           */
        /* ff_wtoupper then ff_uni2oem, in that order -- and 0 means the     */
        /* code page has no such character.  A code                          */
        /* page this file is built for is single-byte (437), so every byte   */
        /* that comes back is below 0x100.                                   */
        wcOem = ff_uni2oem((DWORD)ff_wtoupper((DWORD)uiCode), FF_CODE_PAGE);

        if (wcOem == 0 || wcOem == BFT_DEL || (wcOem < 0x80 && strchr(BFT_LABEL_BAD_FAT, (int)wcOem) != NULL))
        {   /* Not in the code page, or a character FAT refuses in a label   */
            /* (BFT_LABEL_BAD_FAT, and DEL) -- each as f_setlabel refuses.   */
            return(BFT_LABEL_BAD_CHAR);
        }

        if (nBytes >= BFT_FAT_LABEL_MAX)
        {   /* No room left in the 11-byte field. */
            return(BFT_LABEL_TOO_LONG);
        }

        /* Its byte, kept. */
        abOem[nBytes] = (BYTE)wcOem;
        nBytes++;
    }

    if (nBytes > 0 && abOem[0] == BFT_LABEL_FREE_MARK)
    {   /* A first byte that would read as a deleted entry; f_setlabel   */
        /* refuses it.  (Code page 437's 0xE5 is a small sigma, whose    */
        /* capital is another byte, so no label typed here starts with   */
        /* it -- a guard.)                                               */
        return(BFT_LABEL_BAD_CHAR);
    }

    /* Converted. */
    *pnBytes = nBytes;
    return(BFT_LABEL_OK);
}

/*----------------------------------------------------------------------------*/
/* BFT_CheckMakeFs:                                                           */
/*                                                                            */
/* Judge a FAT format's cluster size and label BEFORE anything is written,    */
/* so a refusal leaves the file or range as it was.  Both are judged here     */
/* because FatFs judges neither in a way the caller could act on: f_mkfs      */
/* silently replaces a cluster size it does not take with its own choice (or  */
/* clamps a FAT one to 128 sectors), and f_setlabel runs only on a volume     */
/* already made -- and silently cuts a label at its first control character.  */
/* The label rules are FatFs's own (ff.c, f_setlabel), refusing outright      */
/* what f_setlabel would quietly cut.                                         */
/*                                                                            */
/* Arguments:                                                                 */
/*     uiFsType       : BFE_FSTYPE_FAT16, BFE_FSTYPE_FAT32 or                 */
/*                      BFE_FSTYPE_EXFAT.                                     */
/*     uiSectorBytes  : the sector size the volume will count in.             */
/*     uiClusterBytes : the cluster size asked for, bytes; 0 for FatFs's      */
/*                      choice.                                               */
/*     pszLabel       : the label, UTF-8, NUL-terminated; "" for none.        */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_ARG with the reason recorded.                    */
/*----------------------------------------------------------------------------*/
int BFT_CheckMakeFs(uint32_t uiFsType, uint32_t uiSectorBytes, uint32_t uiClusterBytes, const char *pszLabel)
{
    const unsigned char *pbLabel;
    const unsigned char *pbChar;
    uint32_t             uiMaxCluster;
    uint32_t             uiCode;
    int                  nUnits;
    int                  nMore;
    BYTE                 abOem[BFT_FAT_LABEL_MAX];
    UINT                 nBytes;
    int                  iLabel;

    if (uiClusterBytes != 0)
    {   /* A cluster size was asked for.  A power of two from one sector     */
        /* up to the type's largest -- 128 sectors on FAT12 / 16 / 32,       */
        /* 16 MiB on exFAT -- is what f_mkfs takes as given; anything else   */
        /* it would quietly replace, and the volume would not be what was    */
        /* asked for.  (Whether the volume's size can hold the type at all   */
        /* with it is for f_mkfs to say, at the format.)                     */
        uiMaxCluster = uiSectorBytes * BFT_AU_MAX_SECTORS;

        if (uiFsType == BFE_FSTYPE_EXFAT)
        {   /* exFAT's ceiling is in bytes, not sectors. */
            uiMaxCluster = BFT_EXFAT_AU_MAX;
        }

        if ((uiClusterBytes & (uiClusterBytes - 1)) != 0 || uiClusterBytes < uiSectorBytes || uiClusterBytes > uiMaxCluster)
        {   /* Not one f_mkfs would use as given: refused, naming the range. */
            return(BFX_Fail(BFE_ERR_ARG, "the cluster size must be 0 (chosen by the size) or a power of two from %u to %u bytes for %s",
                            (unsigned)uiSectorBytes, (unsigned)uiMaxCluster, Bft_TypeName(uiFsType)));
        }
    }

    /* The label, byte by byte from its start; no units counted yet.  It      */
    /* arrives from the options' BFE_VOLUME_NAME_BYTES field (bfe_mkfs.c,     */
    /* BFK_CheckOptions copies at most that), which holds the longest label   */
    /* either kind keeps -- 33 bytes of UTF-8 -- so every limit is the one    */
    /* judged below, never the field's.                                       */
    pbLabel = (const unsigned char *)pszLabel;
    nUnits  = 0;

    if (uiFsType != BFE_FSTYPE_EXFAT)
    {   /* FAT12 / 16 / 32: converted as f_setlabel will convert it, and      */
        /* refused for whatever that conversion refuses, with its reason.     */
        /* Any character the OEM code page (437) holds is taken -- accented   */
        /* letters among them, as Windows takes its OEM code page for a FAT   */
        /* label (as found) -- and stored in FatFs's capitals.                */
        iLabel = Bft_LabelToOem(pszLabel, abOem, &nBytes);

        if (iLabel == BFT_LABEL_NOT_UTF8)
        {   /* Not text. */
            return(BFX_Fail(BFE_ERR_ARG, "the volume label is not valid UTF-8"));
        }

        if (iLabel == BFT_LABEL_BAD_CHAR)
        {   /* A character the code page lacks, or one FAT refuses. */
            return(BFX_Fail(BFE_ERR_ARG, "a FAT volume label takes the characters code page 437 holds, none of + . , ; = [ ] / * : < > | \\ \" ? or a control character"));
        }

        if (iLabel == BFT_LABEL_TOO_LONG)
        {   /* Longer than the label's 11-byte field. */
            return(BFX_Fail(BFE_ERR_ARG, "a FAT volume label is at most %d characters", BFT_FAT_LABEL_MAX));
        }

        /* Usable. */
        return(BFE_OK);
    }

    /* exFAT, which keeps a label in UTF-16 and in its own letter case:      */
    /* the label is read as UTF-8, one code point at a time, and             */
    /* counted in the UTF-16 units it will be stored as.  Its own decoder    */
    /* rather than Bft_NextCode, so a broken byte is refused at once; it     */
    /* holds each character to what FatFs's decoder (ff.c tchar2uni)         */
    /* takes -- the shape, an ASCII character's overlong forms, and the      */
    /* code point's range -- so f_setlabel never refuses what passes here.   */
    while (*pbLabel != '\0')
    {   /* One character per iteration: its lead byte says how many more      */
        /* bytes follow and gives the top bits of the code point.  Where it   */
        /* starts is kept, to tell a one-byte character from a longer one.    */
        pbChar = pbLabel;

        if (*pbLabel < 0x80)
        {   /* ASCII: the byte is the character. */
            uiCode = *pbLabel;
            nMore  = 0;
        }
        else if ((*pbLabel & 0xE0) == 0xC0)
        {   /* Two bytes. */
            uiCode = *pbLabel & 0x1Fu;
            nMore  = 1;
        }
        else if ((*pbLabel & 0xF0) == 0xE0)
        {   /* Three bytes. */
            uiCode = *pbLabel & 0x0Fu;
            nMore  = 2;
        }
        else if ((*pbLabel & 0xF8) == 0xF0)
        {   /* Four bytes: past U+FFFF. */
            uiCode = *pbLabel & 0x07u;
            nMore  = 3;
        }
        else
        {   /* A continuation byte where a character should start, or a byte   */
            /* UTF-8 never uses: not text.                                     */
            return(BFX_Fail(BFE_ERR_ARG, "the volume label is not valid UTF-8"));
        }

        /* Past the lead byte. */
        pbLabel++;

        while (nMore > 0)
        {   /* One continuation byte per iteration: six more bits.  The    */
            /* terminator is not a continuation byte, so a character cut   */
            /* short by the end of the string is caught here.              */
            if ((*pbLabel & 0xC0) != 0x80)
            {   /* Cut short, or a lead byte where a continuation belongs. */
                return(BFX_Fail(BFE_ERR_ARG, "the volume label is not valid UTF-8"));
            }

            /* Its bits, then the next byte. */
            uiCode = (uiCode << 6) | (uint32_t)(*pbLabel & 0x3Fu);
            pbLabel++;
            nMore--;
        }

        if (pbLabel - pbChar > 1 && uiCode < 0x80)
        {   /* An overlong form of an ASCII character (C1 81 for "A"): well   */
            /* shaped, but FatFs's decoder refuses it, and f_setlabel would   */
            /* then fail after the volume is made.  Refused here, first.      */
            return(BFX_Fail(BFE_ERR_ARG, "the volume label is not valid UTF-8"));
        }

        if (uiCode < 0x20 || uiCode == 0x7F || (uiCode < 0x80 && strchr(BFT_LABEL_BAD_EXFAT, (int)uiCode) != NULL) ||
            (uiCode >= 0xD800 && uiCode <= 0xDFFF) || uiCode > 0x10FFFF)
        {   /* A control character (f_setlabel would silently cut the label     */
            /* there), DEL or one exFAT refuses (f_setlabel refuses those       */
            /* too), or not a character at all (a lone surrogate's code, or     */
            /* past Unicode's last, which f_setlabel's decoder also refuses).   */
            return(BFX_Fail(BFE_ERR_ARG, "an exFAT volume label may not hold / * : < > | \\ \" ? or a control character"));
        }

        /* Its UTF-16 units: one, or a surrogate pair past U+FFFF. */
        nUnits++;

        if (uiCode >= 0x10000)
        {   /* The pair's second unit. */
            nUnits++;
        }
    }

    if (nUnits > BFT_EXFAT_LABEL_UNITS)
    {   /* Longer than the label's eleven units. */
        return(BFX_Fail(BFE_ERR_ARG, "an exFAT volume label is at most %d UTF-16 units (characters, or two for one past U+FFFF)",
                        BFT_EXFAT_LABEL_UNITS));
    }

    /* Usable. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* Bft_WriteBootLabel:                                                        */
/*                                                                            */
/* Copy a new FAT12 / 16 / 32 volume's label into its boot sector.  FAT keeps */
/* the label twice -- an entry in the root directory, and an 11-byte field in */
/* the boot sector -- and FatFs's f_setlabel writes the entry only, leaving   */
/* the field as f_mkfs wrote it ("NO NAME", ff.c f_mkfs).  Windows' format    */
/* and dosfstools' mkfs.fat write both, and fsck.fat reports a volume whose   */
/* two disagree (and copies the entry over on a repair) -- as found, not      */
/* checkable here.  So, after f_setlabel,                                     */
/* the field gets the same 11 bytes -- and on FAT32 so does the backup boot   */
/* sector, which a repair may restore from.  exFAT's boot sector has no label */
/* field: it is never called for one.                                         */
/*                                                                            */
/* Call it only with the volume UNMOUNTED (BFT_MakeFs unregisters it first),  */
/* so FatFs holds no copy of the boot sector in its sector window that a      */
/* later sync could write back over this one.                                 */
/*                                                                            */
/* Arguments:                                                                 */
/*     bFat32   : TRUE on FAT32, whose fields sit further in and which has a  */
/*                backup boot sector; FALSE on FAT12 / 16.                    */
/*     pszLabel : the label as given, already passed by BFT_CheckMakeFs.      */
/*                                                                            */
/* Returns:                                                                   */
/*     FRESULT : FR_OK; FR_INVALID_NAME for a label that does not convert (a  */
/*               guard: BFT_CheckMakeFs refused any such label before the     */
/*               format); FR_DISK_ERR when a read or write failed.            */
/*----------------------------------------------------------------------------*/
static FRESULT Bft_WriteBootLabel(int bFat32, const char *pszLabel)
{
    BYTE  abSector[FF_MAX_SS];
    BYTE  abLabel[BFT_FAT_LABEL_MAX];
    BYTE  abOem[BFT_FAT_LABEL_MAX];
    LBA_t aSector[2];
    UINT  uiSigAt;
    UINT  uiLabelAt;
    UINT  nSectors;
    UINT  nBytes;
    UINT  i;
    WORD  wBackup;

    /* The label in the bytes f_setlabel gave the root's entry: capitals,   */
    /* in the OEM code page.                                                */
    nBytes = 0;

    if (Bft_LabelToOem(pszLabel, abOem, &nBytes) != BFT_LABEL_OK)
    {   /* Does not convert: a guard, as BFT_CheckMakeFs refused such a   */
        /* label before anything was written.  Nothing written here.      */
        return(FR_INVALID_NAME);
    }

    /* The field's 11 bytes: those, then spaces to the end, as FAT pads a   */
    /* label.                                                               */
    memset(abLabel, ' ', sizeof(abLabel));
    memcpy(abLabel, abOem, nBytes);

    /* Where the signature and the field are: further in on FAT32, whose   */
    /* boot sector has more fields before them.                            */
    uiSigAt   = BFT_BS_BOOTSIG;
    uiLabelAt = BFT_BS_VOLLAB;

    if (bFat32)
    {   /* FAT32's places. */
        uiSigAt   = BFT_BS_BOOTSIG32;
        uiLabelAt = BFT_BS_VOLLAB32;
    }

    /* The boot sector, the volume's sector 0 -- the one sector to write    */
    /* unless FAT32 names a backup in it.  Every sector here goes through   */
    /* FatFs's device layer (bfe_fatio.c), so the range, the sector size    */
    /* and a device's read-back hold as for FatFs's own writes.             */
    if (disk_read(BFT_PDRV, abSector, 0, 1) != RES_OK)
    {   /* Could not be read: nothing written. */
        return(FR_DISK_ERR);
    }

    aSector[0] = 0;
    nSectors   = 1;

    /* FAT32's backup boot sector number, a little-endian 16-bit field   */
    /* (read on FAT12 / 16 too, where it is not used).                   */
    wBackup = (WORD)(abSector[BFT_BS_BKBOOTSEC32] | (abSector[BFT_BS_BKBOOTSEC32 + 1] << 8));

    if (bFat32 && wBackup != 0)
    {   /* FAT32 with a backup boot sector (f_mkfs puts one at sector 6,   */
        /* ff.c's "Offset of backup VBR"): it is written as well, the      */
        /* field being read rather than 6 assumed, so a volume laid out    */
        /* otherwise is still right.                                       */
        aSector[1] = (LBA_t)wBackup;
        nSectors   = 2;
    }

    for (i = 0; i < nSectors; i++)
    {   /* One boot sector (the primary, then the backup) per iteration. */
        if (i > 0 && disk_read(BFT_PDRV, abSector, aSector[i], 1) != RES_OK)
        {   /* The backup could not be read (the primary, sector 0, is in   */
            /* abSector already, so only the backup is read here): the      */
            /* primary is already right; the backup keeps "NO NAME", and    */
            /* the caller reports the label as not written.                 */
            return(FR_DISK_ERR);
        }

        if (abSector[uiSigAt] != BFT_EXT_BOOT_SIG)
        {   /* No extended boot signature, so no label field to write.  A   */
            /* guard: f_mkfs always writes the signature, so a sector it    */
            /* made never lacks it.                                         */
            continue;
        }

        /* The 11 bytes into the field, and the sector written back. */
        memcpy(abSector + uiLabelAt, abLabel, sizeof(abLabel));

        if (disk_write(BFT_PDRV, abSector, aSector[i], 1) != RES_OK)
        {   /* The write failed (or, on a device with the read-back on,       */
            /* did not read back -- BFD_TargetWrite): the label is reported   */
            /* as not written; no flush is attempted.                         */
            return(FR_DISK_ERR);
        }
    }

    /* Made real on the media, as FatFs syncs its own writes (CTRL_SYNC,   */
    /* which bfe_fatio.c answers with the target's flush).                 */
    if (disk_ioctl(BFT_PDRV, CTRL_SYNC, NULL) != RES_OK)
    {   /* The flush failed. */
        return(FR_DISK_ERR);
    }

    /* The boot sector (and FAT32's backup) carries the label. */
    return(FR_OK);
}

/*----------------------------------------------------------------------------*/
/* BFT_MakeFs:                                                                */
/*                                                                            */
/* Format the configured range as one FAT12 / 16, FAT32 or exFAT volume with  */
/* FatFs's formatter, then give it its label.  The caller has configured the  */
/* block device on the range (BFD_Configure, its target closed), has passed   */
/* the cluster size and label through BFT_CheckMakeFs, holds the busy flag    */
/* and has no session open; a device's guards (the system disk, the volume    */
/* locks, the read-back) are the caller's, around this call.                  */
/*                                                                            */
/* The volume covers the whole range, with no partition table of its own, and */
/* a FAT12 / 16 / 32 label is written in both places FAT keeps it.  The       */
/* volume serial number and the times are FatFs's, from the host clock        */
/* (get_fattime).                                                             */
/*                                                                            */
/* Arguments:                                                                 */
/*     uiFsType       : BFE_FSTYPE_FAT16, BFE_FSTYPE_FAT32 or                 */
/*                      BFE_FSTYPE_EXFAT.                                     */
/*     uiSectorBytes  : the sector size to count in: 512 for a file, the      */
/*                      device's own figure for a card.                       */
/*     uiClusterBytes : the cluster size, bytes; 0 for FatFs's choice by the  */
/*                      volume's size.                                        */
/*     pszLabel       : the label, UTF-8; "" for none.                        */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_ARG when f_mkfs cannot lay the type out in this  */
/*           size with this cluster size (nothing of the volume written);     */
/*           BFE_ERR_NO_MEMORY; BFE_ERR_IO_IMAGE (or another mapped code)     */
/*           when the format or the label could not be written.  Each         */
/*           failure recorded; the target closed on every path.               */
/*----------------------------------------------------------------------------*/
int BFT_MakeFs(uint32_t uiFsType, uint32_t uiSectorBytes, uint32_t uiClusterBytes, const char *pszLabel)
{
    MKFS_PARM parm;
    void     *pvWork;
    BYTE      bFsKind;
    FRESULT   fr;

    if (BFA_SetSectorSize(uiSectorBytes) != 0)
    {   /* A sector size FatFs is not built for (outside 512 to 4096, or   */
        /* not a power of two): nothing has been opened.  Set here as in   */
        /* BFT_Mount, so a size left by an earlier call never governs      */
        /* this one -- and the label's mount below counts in it too.       */
        return(BFX_Fail(BFE_ERR_ARG, "%u-byte sectors are outside what FAT is built for here", (unsigned)uiSectorBytes));
    }

    /* The format's parameters.  FM_SFD: the volume covers the whole       */
    /* range with no partition table of its own -- without it f_mkfs       */
    /* would write an MBR into the range and put the volume inside that,   */
    /* a partition within the partition -- so the boot sector's            */
    /* hidden-sector count is 0 whatever the range's place on the disk     */
    /* (that Windows, Linux and fsck.fat locate the volume by the          */
    /* partition table, not by that field, is as found, not checkable      */
    /* here).  Two FATs on FAT12 / 16 / 32, as Windows' and dosfstools'    */
    /* formatters give (FatFs's default would be one, BFT_FAT_COPIES).     */
    /* FatFs's own alignment (from                                         */
    /* the device layer's GET_BLOCK_SIZE) and root size (0 for both),      */
    /* and the cluster size, already judged.                               */
    memset(&parm, 0, sizeof(parm));
    parm.n_fat   = BFT_FAT_COPIES;
    parm.au_size = uiClusterBytes;

    if (uiFsType == BFE_FSTYPE_FAT32)
    {   /* FAT32 only: f_mkfs refuses rather than fall back to FAT16 when   */
        /* the size cannot hold FAT32's clusters.                           */
        parm.fmt = FM_FAT32 | FM_SFD;
    }
    else if (uiFsType == BFE_FSTYPE_EXFAT)
    {   /* exFAT, which has one FAT and its allocation bitmap. */
        parm.fmt   = FM_EXFAT | FM_SFD;
        parm.n_fat = 1;
    }
    else
    {   /* FAT16 -- which f_mkfs makes FAT12 when the clusters are too few   */
        /* for FAT16, by FatFs's bound (MAX_FAT12), as Bft_TypeName says.    */
        parm.fmt = FM_FAT | FM_SFD;
    }

    /* The working buffer, given to f_mkfs rather than left for it to   */
    /* allocate, so its size (and so the number of writes) is ours.     */
    pvWork = malloc(BFT_MKFS_WORK_BYTES);

    if (pvWork == NULL)
    {   /* No room for it: nothing opened or written. */
        return(BFX_Fail(BFE_ERR_NO_MEMORY, "out of memory"));
    }

    /* The format.  f_mkfs opens the block device's target itself          */
    /* (disk_initialize), lays the volume out, writes it, and syncs.  It   */
    /* computes the whole layout before its first write, so a size or a    */
    /* cluster size it cannot use is refused with nothing written (every   */
    /* FR_MKFS_ABORTED in ff.c's f_mkfs comes before its type's first      */
    /* disk_write).  The working buffer is freed at once: f_mkfs keeps     */
    /* no pointer to it.                                                   */
    fr = f_mkfs(BFT_DRIVE, &parm, pvWork, BFT_MKFS_WORK_BYTES);
    free(pvWork);

    if (fr != FR_OK)
    {   /* Not made.  The target f_mkfs opened is closed -- FatFs has no   */
        /* call that closes it -- before the failure is recorded.          */
        BFD_TargetClose();

        if (fr == FR_MKFS_ABORTED || fr == FR_INVALID_PARAMETER)
        {   /* f_mkfs could not lay the type out in this size with this    */
            /* cluster size: too small (or too large) a volume for the     */
            /* type, or too few or too many clusters for it.  Nothing of   */
            /* the volume was written.                                     */
            return(BFX_Fail(BFE_ERR_ARG, "%s cannot be laid out in this size with this cluster size (FatFs: %s)",
                            Bft_TypeName(uiFsType), Bft_Text(fr)));
        }

        /* A write that failed, or the target that would not open. */
        return(BFX_Fail(Bft_MapResult(fr), "formatting as %s failed: %s", Bft_TypeName(uiFsType), Bft_Text(fr)));
    }

    if (pszLabel[0] != '\0')
    {   /* A label: written by mounting the new volume and setting it,       */
        /* since f_mkfs makes no label entry (its boot sector says "NO       */
        /* NAME", as Windows' formatter leaves an unlabelled volume -- as    */
        /* found).                                                           */
        /* A clean work area, mounted at once; then unregistered whatever    */
        /* happened, FatFs's unmount -- after the type is noted, which the   */
        /* unmount clears.                                                   */
        memset(&Bft_fs, 0, sizeof(Bft_fs));
        fr       = f_mount(&Bft_fs, BFT_DRIVE, BFT_MOUNT_NOW);
        bFsKind  = Bft_fs.fs_type;

        if (fr == FR_OK)
        {   /* Mounted: the label's entry in the root, synced at once. */
            fr = f_setlabel(pszLabel);
        }

        /* FatFs's unmount. */
        f_mount(NULL, BFT_DRIVE, 0);

        if (fr == FR_OK && bFsKind != FS_EXFAT)
        {   /* FAT12 / 16 / 32: the boot sector's label field made to match    */
            /* the entry (Bft_WriteBootLabel says why), the volume no longer   */
            /* mounted so FatFs cannot write its own copy back over it.        */
            fr = Bft_WriteBootLabel(bFsKind == FS_FAT32, pszLabel);
        }
    }

    /* The target closed: FatFs has finished with it, and the next call   */
    /* configures the block device afresh.                                */
    BFD_TargetClose();

    if (fr != FR_OK)
    {   /* The volume is made but its label could not be written (the     */
        /* label itself was judged usable, so this is a write or a read   */
        /* that failed).                                                  */
        return(BFX_Fail(BFE_ERR_IO_IMAGE, "the %s volume was made, but its label could not be written: %s",
                        Bft_TypeName(uiFsType), Bft_Text(fr)));
    }

    /* The range holds the new, empty volume. */
    return(BFE_OK);
}
