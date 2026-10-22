/*-----------------------------------------------------------------------------*/
/* bfe_device.c                                                                */
/*                                                                             */
/* SPDX-License-Identifier: GPL-2.0-or-later                                   */
/* Copyright (c) 2026 B. C. Services                                           */
/*                                                                             */
/* The engine's raw I/O layer (see bfe_device.h for what it is for).  It       */
/* opens an image file or a real device through one call, learns which it      */
/* got and the facts that decide how it may be read and written, and does      */
/* the reads and writes; it also answers "which disk is Windows on" and locks  */
/* a disk's volumes before the disk is written.                                */
/*                                                                             */
/* HOW A DEVICE IS RECOGNISED.  The path is opened once as an ordinary file    */
/* and asked for its drive geometry.  A file cannot answer that control; a     */
/* disk or a volume can, and the answer carries the one fact a file never      */
/* has, the real sector size.  A device is then closed and opened AGAIN with   */
/* no buffering -- the flag cannot be added to an open handle -- so every      */
/* transfer goes straight to the media in whole sectors, which is what a raw   */
/* disk requires anyway and what makes a flush mean something.                 */
/*                                                                             */
/* WHY THE LENGTH IS ASKED FOR TWICE.  IOCTL_DISK_GET_LENGTH_INFO gives the    */
/* length of the thing that was opened -- the whole disk for \\.\PhysicalDrive */
/* and just the volume for \\.\C: -- but needs read access, which on a disk    */
/* means Administrator.  The geometry's DiskSize needs nothing, but for a      */
/* volume it is the size of the DISK the volume sits on.  So the length is     */
/* taken from the first when it answers and from the second when it does       */
/* not, and a volume opened without Administrator reports its disk's size,     */
/* which is wrong only in a case no I/O can follow anyway.                     */
/*-----------------------------------------------------------------------------*/

/* The C library (memory, strings, and snprintf for the lock's failure    */
/* sentence), then Windows: the file and device API, and winioctl.h for   */
/* the disk and storage control codes and structures.                     */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <windows.h>
#include <winioctl.h>

/* This file's own interface: BFV_Target, BFV_Locks and the prototypes. */
#include "bfe_device.h"

/* The project's boolean pair, where windows.h has not already defined it. */
#ifndef FALSE
#define FALSE               0               // boolean false
#endif
#ifndef TRUE
#define TRUE                (!FALSE)        // boolean true
#endif

/*----------------------------------------------------------------------------*/
/* Definitions -- documentation table.                                        */
/*----------------------------------------------------------------------------*/
#define BFV_EXTENTS_FIRST   8               // disk extents allowed for in the first query of a volume; grown on ERROR_MORE_DATA
#define BFV_VOLUME_PATH_MAX 64              // room for a volume GUID path ("\\?\Volume{...}\" is 49 characters) plus its NUL

/*----------------------------------------------------------------------------*/
/* BFV_VolumeOnDisk's three answers -- documentation table.  Three, not two,  */
/* because "could not be told" must not pass for "elsewhere": BFV_LockDisk    */
/* refuses on it rather than leave a volume perhaps on the disk mounted.      */
/*----------------------------------------------------------------------------*/
#define BFV_DISK_ELSEWHERE  0               // the volume has no extent on the disk (or is on no physical disk at all)
#define BFV_DISK_ON         1               // at least one of the volume's extents lies on the disk
#define BFV_DISK_UNKNOWN    2               // its extents could not be read, so where it lies is not known

/*----------------------------------------------------------------------------*/
/* BFV_QueryGeometry:                                                         */
/*                                                                            */
/* Ask an open handle for its drive geometry.  Success is what marks the      */
/* handle as a DEVICE: a regular file fails the control with "invalid         */
/* function" or "invalid parameter".  On success the sector size and the      */
/* disk's size are handed back.                                               */
/*                                                                            */
/* Arguments:                                                                 */
/*     hFile       : the open handle.                                         */
/*     puiSector   : receives bytes per sector.                               */
/*     pullDisk    : receives the size of the disk the handle is on.          */
/*                                                                            */
/* Returns:                                                                   */
/*     int : TRUE when the handle is a device and both facts are filled in.   */
/*----------------------------------------------------------------------------*/
static int BFV_QueryGeometry(HANDLE hFile, uint32_t *puiSector, uint64_t *pullDisk)
{
    DISK_GEOMETRY_EX geometry;
    DWORD            dwBytes;

    /* The answer and its byte count start cleared. */
    memset(&geometry, 0, sizeof(geometry));
    dwBytes = 0;

    /* Ask; a file fails here, which is the test. */
    if (!DeviceIoControl(hFile, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, NULL, 0,
                         &geometry, sizeof(geometry), &dwBytes, NULL))
    {   /* Not a disk or a volume: a file, a pipe, something else. */
        return(FALSE);
    }

    if (geometry.Geometry.BytesPerSector == 0)
    {   /* A device that reports no sector size cannot be driven. */
        return(FALSE);
    }

    /* Hand back the two facts the caller needs. */
    *puiSector = (uint32_t)geometry.Geometry.BytesPerSector;
    *pullDisk  = (uint64_t)geometry.DiskSize.QuadPart;

    /* A device, with its geometry known. */
    return(TRUE);
}

/*----------------------------------------------------------------------------*/
/* BFV_QueryLength:                                                           */
/*                                                                            */
/* Ask an open device for the length of what was opened (see the file         */
/* header for why this differs from the geometry's size).  It needs read      */
/* access, so it can fail where the geometry did not; the caller then falls   */
/* back to the geometry.                                                      */
/*                                                                            */
/* Arguments:                                                                 */
/*     hFile      : the open handle.                                          */
/*     pullLength : receives the length in bytes.                             */
/*                                                                            */
/* Returns:                                                                   */
/*     int : TRUE when the length was answered.                               */
/*----------------------------------------------------------------------------*/
static int BFV_QueryLength(HANDLE hFile, uint64_t *pullLength)
{
    GET_LENGTH_INFORMATION length;
    DWORD                  dwBytes;

    /* The answer and its byte count start cleared. */
    memset(&length, 0, sizeof(length));
    dwBytes = 0;

    /* Ask for the length of exactly what the handle names. */
    if (!DeviceIoControl(hFile, IOCTL_DISK_GET_LENGTH_INFO, NULL, 0,
                         &length, sizeof(length), &dwBytes, NULL))
    {   /* Refused -- access denied when not elevated, or not a disk device. */
        return(FALSE);
    }

    /* Hand it back. */
    *pullLength = (uint64_t)length.Length.QuadPart;

    /* The length of exactly what was opened. */
    return(TRUE);
}

/*----------------------------------------------------------------------------*/
/* BFV_QueryDiskNumber:                                                       */
/*                                                                            */
/* Ask an open device which physical disk it is, or sits on.  This answers    */
/* from the HANDLE, so it is the same whatever spelling the path used --      */
/* \\.\PhysicalDrive0, \\.\physicaldrive0, \\?\PhysicalDrive0 and \\.\C: all  */
/* say "disk 0" here -- which is what makes the system-disk guard built on it */
/* impossible to sidestep by typing the name differently.                     */
/*                                                                            */
/* Arguments:                                                                 */
/*     hFile : the open handle.                                               */
/*                                                                            */
/* Returns:                                                                   */
/*     uint32_t : the disk number, or BFV_NO_DISK when the control is refused */
/*                or the device is not a disk.                                */
/*----------------------------------------------------------------------------*/
static uint32_t BFV_QueryDiskNumber(HANDLE hFile)
{
    STORAGE_DEVICE_NUMBER number;
    DWORD                 dwBytes;

    /* The answer and its byte count start cleared. */
    memset(&number, 0, sizeof(number));
    dwBytes = 0;

    /* Ask which device, of which type, the handle is. */
    if (!DeviceIoControl(hFile, IOCTL_STORAGE_GET_DEVICE_NUMBER, NULL, 0,
                         &number, sizeof(number), &dwBytes, NULL))
    {   /* Not a storage device. */
        return(BFV_NO_DISK);
    }

    if (number.DeviceType != FILE_DEVICE_DISK)
    {   /* A CD-ROM, a tape: not a disk this engine writes. */
        return(BFV_NO_DISK);
    }

    /* The physical disk number Windows gives it. */
    return((uint32_t)number.DeviceNumber);
}

/*----------------------------------------------------------------------------*/
/* BFV_CopyTrimmed:                                                           */
/*                                                                            */
/* Append one text field of a device descriptor to the model string, trimmed  */
/* of its padding and separated from what came before by one space.  Nothing  */
/* is written past the string's room, and nothing is read past the            */
/* descriptor's end.                                                          */
/*                                                                            */
/* Arguments:                                                                 */
/*     pbDescriptor : the descriptor buffer.                                  */
/*     cbDescriptor : its size, so an offset outside it is ignored.           */
/*     uiOffset     : the field's byte offset, or 0 for none.                 */
/*     pszModel     : the model string being built, NUL-terminated.           */
/*     cbModel      : its room.                                               */
/*                                                                            */
/* Returns:                                                                   */
/*     void : the field's trimmed text is appended, if there was any.         */
/*----------------------------------------------------------------------------*/
static void BFV_CopyTrimmed(const uint8_t *pbDescriptor, size_t cbDescriptor, uint32_t uiOffset,
                            char *pszModel, size_t cbModel)
{
    const char *pszField;
    size_t      cbField;
    size_t      cbUsed;
    size_t      i;

    if (uiOffset == 0 || uiOffset >= cbDescriptor)
    {   /* No such field (offset 0), or one that points outside the buffer. */
        return;
    }

    /* The field's text starts at its byte offset inside the descriptor. */
    pszField = (const char *)pbDescriptor + uiOffset;

    /* Its length up to its NUL, bounded by the buffer so a missing NUL   */
    /* cannot run on.                                                     */
    cbField = 0;

    while (uiOffset + cbField < cbDescriptor && pszField[cbField] != '\0')
    {   /* One character per iteration. */
        cbField++;
    }

    /* Trim trailing spaces, then leading ones: the fields are padded with   */
    /* spaces, the way SCSI inquiry strings are.                             */
    while (cbField > 0 && pszField[cbField - 1] == ' ')
    {   /* A padding space at the end. */
        cbField--;
    }

    while (cbField > 0 && pszField[0] == ' ')
    {   /* A padding space at the start. */
        pszField++;
        cbField--;
    }

    if (cbField == 0)
    {   /* Nothing but padding. */
        return;
    }

    /* Where the model string ends now: the field is appended there. */
    cbUsed = strlen(pszModel);

    if (cbUsed > 0 && cbUsed + 1 < cbModel)
    {   /* Something is there already: a space between the two fields. */
        pszModel[cbUsed] = ' ';
        cbUsed++;
        pszModel[cbUsed] = '\0';
    }

    for (i = 0; i < cbField && cbUsed + 1 < cbModel; i++)
    {   /* One character per iteration, while there is room for it and a NUL. */
        pszModel[cbUsed] = pszField[i];
        cbUsed++;
    }

    /* Terminate what was appended; the loop always left room for it. */
    pszModel[cbUsed] = '\0';
}

/*----------------------------------------------------------------------------*/
/* BFV_QueryModel:                                                            */
/*                                                                            */
/* Ask an open device for its model text, bus and whether its media is        */
/* removable, through the storage property query.  A refusal leaves the       */
/* facts at their "unknown" values; nothing fails over a missing model name.  */
/*                                                                            */
/* Arguments:                                                                 */
/*     hFile   : the open handle.                                             */
/*     pTarget : receives uiBus, bRemovable and szModel.                      */
/*                                                                            */
/* Returns:                                                                   */
/*     void : the three facts are filled in, or left as "unknown".            */
/*----------------------------------------------------------------------------*/
static void BFV_QueryModel(HANDLE hFile, BFV_Target *pTarget)
{
    STORAGE_PROPERTY_QUERY     query;
    STORAGE_DEVICE_DESCRIPTOR *pDescriptor;
    uint8_t                    abBuffer[1024];
    DWORD                      dwBytes;

    /* Ask for the standard device descriptor.  The 1 KiB buffer holds the   */
    /* descriptor and the vendor / product strings it points at with room    */
    /* to spare; a device whose answer is larger fails the call, and its     */
    /* facts simply stay unknown.                                            */
    memset(&query, 0, sizeof(query));
    memset(abBuffer, 0, sizeof(abBuffer));
    query.PropertyId = StorageDeviceProperty;
    query.QueryType  = PropertyStandardQuery;
    dwBytes          = 0;

    if (!DeviceIoControl(hFile, IOCTL_STORAGE_QUERY_PROPERTY, &query, sizeof(query),
                         abBuffer, sizeof(abBuffer), &dwBytes, NULL))
    {   /* No descriptor: the facts stay unknown. */
        return;
    }

    if (dwBytes < sizeof(STORAGE_DEVICE_DESCRIPTOR))
    {   /* Too short to be a descriptor. */
        return;
    }

    /* The bus and the removable flag, straight from the descriptor. */
    pDescriptor = (STORAGE_DEVICE_DESCRIPTOR *)abBuffer;

    pTarget->uiBus      = (uint32_t)pDescriptor->BusType;
    pTarget->bRemovable = (pDescriptor->RemovableMedia != 0);

    /* "Vendor Product", each trimmed; either may be absent.  The two        */
    /* fields are given as BYTE OFFSETS from the start of the descriptor,    */
    /* not as pointers -- reading them as pointers is a classic mistake --   */
    /* so the buffer and the offset are passed, and the length the call      */
    /* returned bounds them.                                                 */
    pTarget->szModel[0] = '\0';
    BFV_CopyTrimmed(abBuffer, dwBytes, pDescriptor->VendorIdOffset,  pTarget->szModel, sizeof(pTarget->szModel));
    BFV_CopyTrimmed(abBuffer, dwBytes, pDescriptor->ProductIdOffset, pTarget->szModel, sizeof(pTarget->szModel));
}

/*----------------------------------------------------------------------------*/
/* BFV_Open:                                                                  */
/*                                                                            */
/* Open a path for raw I/O and learn what it is: a file or a device, with     */
/* the facts that decide how it may be read and written (its size, sector,    */
/* disk and model).  A device is left open unbuffered, so every transfer      */
/* reaches the media.  QUERY mode reads the facts without needing             */
/* Administrator and hands the target back CLOSED, since no I/O is wanted.    */
/*                                                                            */
/* Arguments:                                                                 */
/*     pwszPath : the file or device path, UTF-16.                            */
/*     iAccess  : BFV_ACCESS_QUERY, READ, WRITE, CREATE or OPENKEEP.          */
/*     pTarget  : receives the handle and the facts; on failure its handle is */
/*                INVALID_HANDLE_VALUE and the rest is zero.                  */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0 with the target open (QUERY: filled in and closed); otherwise  */
/*           the Windows error code of the step that failed, with nothing     */
/*           left open.                                                       */
/*----------------------------------------------------------------------------*/
int BFV_Open(const wchar_t *pwszPath, int iAccess, BFV_Target *pTarget)
{
    HANDLE        hFile;
    LARGE_INTEGER liSize;
    DWORD         dwAccess;
    DWORD         dwDisposition;
    DWORD         dwBytes;
    DWORD         dwError;
    uint64_t      ullDiskSize;
    uint64_t      ullLength;
    uint32_t      uiSector;

    /* The target starts as "nothing open, no disk", which is also what   */
    /* every failure below leaves.                                        */
    memset(pTarget, 0, sizeof(*pTarget));
    pTarget->hFile  = INVALID_HANDLE_VALUE;
    pTarget->uiDisk = BFV_NO_DISK;

    /* The access and disposition each mode asks for. */
    dwAccess      = GENERIC_READ;
    dwDisposition = OPEN_EXISTING;

    if (iAccess == BFV_ACCESS_QUERY)
    {   /* Facts only: no access, which a disk grants to anyone.  Every   */
        /* control STEP 3 uses accepts a zero-access handle except the    */
        /* length, which then falls back to the geometry's size.          */
        dwAccess = 0;
    }
    else if (iAccess == BFV_ACCESS_WRITE)
    {   /* Read and write what is there. */
        dwAccess |= GENERIC_WRITE;
    }
    else if (iAccess == BFV_ACCESS_CREATE || iAccess == BFV_ACCESS_OPENKEEP)
    {   /* Read and write; a FILE is made if missing (CREATE then cuts an    */
        /* existing one to nothing below; OPENKEEP leaves it as it is, for   */
        /* BFV_Truncate once the caller has checked it).  On a device the    */
        /* disposition is meaningless and OPEN_EXISTING is what Windows      */
        /* wants, which STEP 4 uses -- this first open only has to succeed   */
        /* on both.                                                          */
        dwAccess     |= GENERIC_WRITE;
        dwDisposition = OPEN_ALWAYS;
    }

    /* STEP 1: open as a file, with the mode's access, sharing read and     */
    /* write: an image file may be read by another program meanwhile, and   */
    /* a disk is always shared with Windows.                                */
    hFile = CreateFileW(pwszPath, dwAccess, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL,
                        dwDisposition, FILE_ATTRIBUTE_NORMAL, NULL);

    if (hFile == INVALID_HANDLE_VALUE)
    {   /* Missing, no permission, or a device that needs Administrator. */
        return((int)GetLastError());
    }

    /* STEP 2: a device, or a file?  No geometry answer means a FILE.  The   */
    /* geometry's two answers start at 0.                                    */
    uiSector    = 0;
    ullDiskSize = 0;

    if (!BFV_QueryGeometry(hFile, &uiSector, &ullDiskSize))
    {   /* A file. */
        if (iAccess == BFV_ACCESS_QUERY)
        {   /* A zero-access file handle cannot report a size: reopen for its   */
            /* attributes, which is all a size needs.                           */

            /* Close the zero-access handle and open again for attributes. */
            CloseHandle(hFile);
            hFile = CreateFileW(pwszPath, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL,
                                OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);

            if (hFile == INVALID_HANDLE_VALUE)
            {   /* Gone, or not even its attributes may be read. */
                return((int)GetLastError());
            }
        }

        /* The file's size. */
        liSize.QuadPart = 0;

        if (!GetFileSizeEx(hFile, &liSize))
        {   /* Not something with a size -- a pipe, a console. */

            /* Take the error BEFORE closing -- CloseHandle can overwrite   */
            /* the thread's last error -- then close and return it.         */
            dwError = GetLastError();
            CloseHandle(hFile);
            return((int)dwError);
        }

        if (iAccess == BFV_ACCESS_CREATE)
        {   /* Made anew: whatever was there is cut to nothing. */
            if (!SetEndOfFile(hFile))
            {   /* The file could not be truncated. */

                /* Take the error before closing, as above; close; return. */
                dwError = GetLastError();
                CloseHandle(hFile);
                return((int)dwError);
            }

            /* Cut at the file pointer, which a fresh open leaves at 0. */
            liSize.QuadPart = 0;
        }

        /* A file's facts: its size, the conventional 512-byte sector, no   */
        /* disk, not a device, and writable when opened to write.           */
        pTarget->ullSize   = (uint64_t)liSize.QuadPart;
        pTarget->uiSector  = BFV_FILE_SECTOR;
        pTarget->uiDisk    = BFV_NO_DISK;
        pTarget->bDevice   = FALSE;
        pTarget->bWritable = (iAccess == BFV_ACCESS_WRITE || iAccess == BFV_ACCESS_CREATE || iAccess == BFV_ACCESS_OPENKEEP);

        if (iAccess == BFV_ACCESS_QUERY)
        {   /* Facts only: the handle is not wanted. */

            /* Close it; the target stays marked closed. */
            CloseHandle(hFile);
            return(0);
        }

        /* Keep the handle: the target is now open. */
        pTarget->hFile = hFile;

        /* An ordinary file, open. */
        return(0);
    }

    /* STEP 3: the device's length, disk number and model, while the handle   */
    /* is open.                                                               */
    ullLength = 0;

    if (!BFV_QueryLength(hFile, &ullLength))
    {   /* Not elevated, so the exact length is refused: the disk's size   */
        /* stands in (right for a whole disk, over-long for a volume).     */
        ullLength = ullDiskSize;
    }

    /* A device's facts, all read from this first handle -- the disk number   */
    /* and the model need it, and the unbuffered reopen below does not        */
    /* change any of them.  Then the handle is closed: the no-buffering       */
    /* flag cannot be added to an open handle.                                */
    pTarget->ullSize   = ullLength;
    pTarget->uiSector  = uiSector;
    pTarget->uiDisk    = BFV_QueryDiskNumber(hFile);
    pTarget->bDevice   = TRUE;
    pTarget->bWritable = (iAccess == BFV_ACCESS_WRITE || iAccess == BFV_ACCESS_CREATE || iAccess == BFV_ACCESS_OPENKEEP);
    BFV_QueryModel(hFile, pTarget);
    CloseHandle(hFile);

    if (iAccess == BFV_ACCESS_QUERY)
    {   /* Facts only: done, and nothing is open. */
        return(0);
    }

    /* STEP 4: reopen unbuffered, so the media is what is read and written. */
    hFile = CreateFileW(pwszPath, dwAccess, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL,
                        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_NO_BUFFERING, NULL);

    if (hFile == INVALID_HANDLE_VALUE)
    {   /* It opened a moment ago; something changed (the card was pulled). */

        /* Take the error first; then wipe the facts gathered in STEP 3, so    */
        /* a failed open leaves the target exactly as the start did; return.   */
        dwError = GetLastError();
        memset(pTarget, 0, sizeof(*pTarget));
        pTarget->hFile  = INVALID_HANDLE_VALUE;
        pTarget->uiDisk = BFV_NO_DISK;
        return((int)dwError);
    }

    /* A volume may be read to its very end -- without this, a volume read   */
    /* stops where the filesystem says it ends, one sector short on NTFS.    */
    /* On a disk this is refused and that is fine, so the result is not      */
    /* looked at.                                                            */
    dwBytes = 0;
    DeviceIoControl(hFile, FSCTL_ALLOW_EXTENDED_DASD_IO, NULL, 0, NULL, 0, &dwBytes, NULL);

    /* Keep the handle: the target is now open. */
    pTarget->hFile = hFile;

    /* A device, open for sector-aligned unbuffered I/O. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFV_Close:                                                                 */
/*                                                                            */
/* Close a target's handle and mark it closed.  Safe on a target that was     */
/* never opened or is already closed, so every failure path can call it.      */
/*                                                                            */
/* Arguments:                                                                 */
/*     pTarget : the target.                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     void : the handle is closed and INVALID_HANDLE_VALUE.                  */
/*----------------------------------------------------------------------------*/
void BFV_Close(BFV_Target *pTarget)
{
    if (pTarget->hFile != INVALID_HANDLE_VALUE)
    {   /* Something is open: close it, and mark it closed so a second call   */
        /* does nothing.                                                      */
        CloseHandle(pTarget->hFile);
        pTarget->hFile = INVALID_HANDLE_VALUE;
    }
}

/*----------------------------------------------------------------------------*/
/* BFV_CheckAligned:                                                          */
/*                                                                            */
/* The alignment rule a device enforces, checked before the call so the       */
/* error is the engine's own rather than a cryptic "invalid parameter" from   */
/* the driver: the offset and the length must be whole sectors.  A file has   */
/* no such rule.                                                              */
/*                                                                            */
/* Arguments:                                                                 */
/*     pTarget   : the target.                                                */
/*     ullOffset : the byte offset of the transfer.                           */
/*     cbBytes   : its length.                                                */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0 when the transfer is allowed, ERROR_INVALID_PARAMETER when a   */
/*           device would refuse it.                                          */
/*----------------------------------------------------------------------------*/
static int BFV_CheckAligned(const BFV_Target *pTarget, uint64_t ullOffset, uint32_t cbBytes)
{
    if (!pTarget->bDevice)
    {   /* A file takes any offset and length. */
        return(0);
    }

    if ((ullOffset % pTarget->uiSector) != 0 || (cbBytes % pTarget->uiSector) != 0)
    {   /* Not whole sectors: the driver would refuse. */
        return(ERROR_INVALID_PARAMETER);
    }

    /* Whole sectors at a sector boundary. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFV_Seek:                                                                  */
/*                                                                            */
/* Position the handle for the next transfer.  The handles are synchronous,   */
/* so a seek-then-transfer pair is the way to address a byte offset.          */
/*                                                                            */
/* Arguments:                                                                 */
/*     pTarget   : the target.                                                */
/*     ullOffset : the byte offset to go to.                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0, or the Windows error.                                         */
/*----------------------------------------------------------------------------*/
static int BFV_Seek(BFV_Target *pTarget, uint64_t ullOffset)
{
    LARGE_INTEGER liOffset;

    /* Windows takes the offset as a signed 64-bit value; every offset the    */
    /* engine uses is far below 2^63, so the conversion cannot go negative.   */
    liOffset.QuadPart = (LONGLONG)ullOffset;

    if (!SetFilePointerEx(pTarget->hFile, liOffset, NULL, FILE_BEGIN))
    {   /* Beyond what the handle can address. */
        return((int)GetLastError());
    }

    /* Positioned. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFV_Read:                                                                  */
/*                                                                            */
/* Read cbBytes at a byte offset, from a file or a device alike.  A range     */
/* that is not all there reads as ERROR_HANDLE_EOF on both.                   */
/*                                                                            */
/* Arguments:                                                                 */
/*     pTarget   : the target.                                                */
/*     ullOffset : where to read from.                                        */
/*     pvBuffer  : receives the bytes (page-aligned for a device).            */
/*     cbBytes   : how many; whole sectors on a device.                       */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0 with every byte read; ERROR_HANDLE_EOF when the range is not   */
/*           all there; otherwise the Windows error.                          */
/*----------------------------------------------------------------------------*/
int BFV_Read(BFV_Target *pTarget, uint64_t ullOffset, void *pvBuffer, uint32_t cbBytes)
{
    DWORD dwRead;
    int   iError;

    /* A device's sector rule, tested here so the error is the engine's. */
    iError = BFV_CheckAligned(pTarget, ullOffset, cbBytes);

    if (iError != 0)
    {   /* A device would refuse the shape of this read. */
        return(iError);
    }

    if (ullOffset > pTarget->ullSize || cbBytes > pTarget->ullSize - ullOffset)
    {   /* Past the end.  Tested BEFORE the read because a raw disk refuses    */
        /* a read that runs past its end rather than returning what is         */
        /* there, so the caller sees a clean "end" error instead of a driver   */
        /* failure on the last block.                                          */
        return(ERROR_HANDLE_EOF);
    }

    if (cbBytes == 0)
    {   /* Nothing to transfer. */
        return(0);
    }

    /* Go to the offset, then read; the count of bytes read starts at 0. */
    iError = BFV_Seek(pTarget, ullOffset);

    if (iError != 0)
    {   /* The offset could not be set. */
        return(iError);
    }

    dwRead = 0;

    if (!ReadFile(pTarget->hFile, pvBuffer, cbBytes, &dwRead, NULL))
    {   /* The media failed, or the device went away. */
        return((int)GetLastError());
    }

    if (dwRead != cbBytes)
    {   /* Fewer bytes than asked: the end of a file, or a truncated one --   */
        /* reported as the same "end" as a range test that failed above.      */
        return(ERROR_HANDLE_EOF);
    }

    /* Every byte is in the buffer. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFV_Write:                                                                 */
/*                                                                            */
/* Write cbBytes at a byte offset.  A device's end is fixed and a write past  */
/* it is refused; a file grows as needed, and its recorded size with it.      */
/*                                                                            */
/* Arguments:                                                                 */
/*     pTarget   : the target, opened writable.                               */
/*     ullOffset : where to write.                                            */
/*     pvBuffer  : the bytes (page-aligned for a device).                     */
/*     cbBytes   : how many; whole sectors on a device.                       */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0 with every byte written; ERROR_ACCESS_DENIED on a read-only    */
/*           target; ERROR_HANDLE_EOF when a device write would run past its  */
/*           end; ERROR_WRITE_FAULT on a short write; otherwise the Windows   */
/*           error.                                                           */
/*----------------------------------------------------------------------------*/
int BFV_Write(BFV_Target *pTarget, uint64_t ullOffset, const void *pvBuffer, uint32_t cbBytes)
{
    DWORD dwWritten;
    int   iError;

    if (!pTarget->bWritable)
    {   /* Opened to read: the handle has no write access, and the layer above   */
        /* has already promised not to write.                                    */
        return(ERROR_ACCESS_DENIED);
    }

    /* A device's sector rule, tested here so the error is the engine's. */
    iError = BFV_CheckAligned(pTarget, ullOffset, cbBytes);

    if (iError != 0)
    {   /* A device would refuse the shape of this write. */
        return(iError);
    }

    if (pTarget->bDevice)
    {   /* A device has a fixed end; a file does not. */
        if (ullOffset > pTarget->ullSize || cbBytes > pTarget->ullSize - ullOffset)
        {   /* The write would run off the end of the device: refused whole,   */
            /* before any of it is written, rather than failing part-way       */
            /* inside the driver.  Earlier writes of the same operation have   */
            /* already landed -- each call is checked on its own.              */
            return(ERROR_HANDLE_EOF);
        }
    }

    if (cbBytes == 0)
    {   /* Nothing to transfer. */
        return(0);
    }

    /* Go to the offset, then write; the count written starts at 0. */
    iError = BFV_Seek(pTarget, ullOffset);

    if (iError != 0)
    {   /* The offset could not be set. */
        return(iError);
    }

    dwWritten = 0;

    if (!WriteFile(pTarget->hFile, pvBuffer, cbBytes, &dwWritten, NULL))
    {   /* The media refused, the disk is full, or the device went away. */
        return((int)GetLastError());
    }

    if (dwWritten != cbBytes)
    {   /* Fewer bytes than given, with no error raised: say so in those   */
        /* words, rather than with whatever GetLastError holds, which is   */
        /* stale after a call that succeeded.                              */
        return(ERROR_WRITE_FAULT);
    }

    if (!pTarget->bDevice && ullOffset + cbBytes > pTarget->ullSize)
    {   /* A file grows under a write past its end; the recorded length      */
        /* follows, so a later read of what was just written is in bounds.   */
        pTarget->ullSize = ullOffset + cbBytes;
    }

    /* Every byte was handed to the device or the file. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFV_Flush:                                                                 */
/*                                                                            */
/* Make sure what was written has reached the media.  On a device this is     */
/* the one call that says the data is on the card rather than in a cache; a   */
/* failure here is therefore an error the caller must report, never a         */
/* warning.                                                                   */
/*                                                                            */
/* Arguments:                                                                 */
/*     pTarget : the target.                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0, or the Windows error.                                         */
/*----------------------------------------------------------------------------*/
int BFV_Flush(BFV_Target *pTarget)
{
    if (!pTarget->bWritable)
    {   /* Nothing was written through this handle. */
        return(0);
    }

    if (!FlushFileBuffers(pTarget->hFile))
    {   /* The media did not confirm the write. */
        return((int)GetLastError());
    }

    /* On the media. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFV_Truncate:                                                              */
/*                                                                            */
/* Cut an open FILE to nothing -- the second half of a BFV_ACCESS_OPENKEEP    */
/* open, done once the caller has checked that the file it opened is one it   */
/* may overwrite (BFV_SameObject).  Doing it on the same handle, rather than  */
/* reopening the path, means the file checked is the file cut: nothing can    */
/* be put in its place between the two.  A device is left as it is (a device  */
/* is overwritten, never truncated).                                          */
/*                                                                            */
/* Arguments:                                                                 */
/*     pTarget : the target, open writable.                                   */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0, with a file's size now 0; otherwise the Windows error.        */
/*----------------------------------------------------------------------------*/
int BFV_Truncate(BFV_Target *pTarget)
{
    LARGE_INTEGER liStart;

    if (pTarget->bDevice)
    {   /* A device has no length to cut: nothing to do. */
        return(0);
    }

    /* To the start, then end the file there. */
    liStart.QuadPart = 0;

    if (!SetFilePointerEx(pTarget->hFile, liStart, NULL, FILE_BEGIN) || !SetEndOfFile(pTarget->hFile))
    {   /* The file could not be cut: Windows' reason. */
        return((int)GetLastError());
    }

    /* Empty now: its recorded size follows. */
    pTarget->ullSize = 0;
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFV_SameObject:                                                            */
/*                                                                            */
/* Whether two open targets are the same thing.  Decided by IDENTITY, never   */
/* by path, because one file has many spellings -- a relative path, '/' for   */
/* '\', an 8.3 name, a "\\?\" prefix, a hard link -- and one disk many paths  */
/* (\\.\PhysicalDriveN and every volume on it).  Two devices are the same     */
/* when they sit on one physical disk, the unit a write can damage; a file    */
/* and a device never are.  Two files that cannot be told apart are taken to  */
/* be the same, so a caller refusing on TRUE refuses rather than risks it.    */
/* Its callers: BFD_TargetIsObject (the raw writers' test against the open    */
/* session) and BFE_CopyRaw's source-is-destination test.                     */
/*                                                                            */
/* Arguments:                                                                 */
/*     pFirst  : one open target.                                             */
/*     pSecond : the other.                                                   */
/*                                                                            */
/* Returns:                                                                   */
/*     int : TRUE when they are (or may be) the same; FALSE when they are     */
/*           known to differ.                                                 */
/*----------------------------------------------------------------------------*/
int BFV_SameObject(const BFV_Target *pFirst, const BFV_Target *pSecond)
{
    BY_HANDLE_FILE_INFORMATION infoFirst;
    BY_HANDLE_FILE_INFORMATION infoSecond;

    if (pFirst->bDevice != pSecond->bDevice)
    {   /* A file and a device: different things. */
        return(FALSE);
    }

    if (pFirst->bDevice)
    {   /* Two devices: the same physical disk is the same thing for every     */
        /* purpose a write cares about -- a volume and the disk it sits on     */
        /* included, since both report the disk's number.  Two devices         */
        /* whose numbers are BOTH unknown (BFV_NO_DISK) compare equal, so      */
        /* are treated as the same.  One unknown and one known number          */
        /* compare DIFFERENT, so a device whose disk number Windows refused    */
        /* (a volume spanning disks is the likely case -- not established)     */
        /* is not held apart from a disk it lies on.  No caller meets that:    */
        /* a DESTINATION of unknown disk is refused anyway by the system-      */
        /* disk guard, whatever this answers, and the other side -- the open   */
        /* session's target, or a raw copy's source -- would have to be a      */
        /* VOLUME path, which the program never passes (its devices are        */
        /* whole disks, \\.\PhysicalDriveN, which always report a number).     */
        /* A caller that did would want TRUE when EITHER number is unknown.    */
        return(pFirst->uiDisk == pSecond->uiDisk);
    }

    if (!GetFileInformationByHandle(pFirst->hFile, &infoFirst) ||
        !GetFileInformationByHandle(pSecond->hFile, &infoSecond))
    {   /* Two files, one of which will not say what it is: treated as the   */
        /* same, so the caller refuses rather than guesses.                  */
        return(TRUE);
    }

    /* Two files: the same when their volume serial number and file index    */
    /* on it are equal -- Windows' own file identity, which every spelling   */
    /* of the one file shares.                                               */
    return(infoFirst.dwVolumeSerialNumber == infoSecond.dwVolumeSerialNumber &&
           infoFirst.nFileIndexHigh       == infoSecond.nFileIndexHigh &&
           infoFirst.nFileIndexLow        == infoSecond.nFileIndexLow);
}

/*----------------------------------------------------------------------------*/
/* BFV_AllocAligned:                                                          */
/*                                                                            */
/* A zero-filled buffer for device I/O, aligned as the no-buffering flag      */
/* demands of a transfer's buffer address.                                    */
/*                                                                            */
/* Arguments:                                                                 */
/*     cbBytes : the size wanted; rounded up to whole pages by Windows.       */
/*                                                                            */
/* Returns:                                                                   */
/*     void * : the buffer, zero-filled, or NULL when memory is short.        */
/*----------------------------------------------------------------------------*/
void *BFV_AllocAligned(size_t cbBytes)
{
    /* Committed and zeroed in one step.  VirtualAlloc hands back whole    */
    /* pages, and a page boundary is aligned to every sector size a disk   */
    /* uses, which is the alignment the no-buffering flag requires.        */
    return(VirtualAlloc(NULL, cbBytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
}

/*----------------------------------------------------------------------------*/
/* BFV_FreeAligned:                                                           */
/*                                                                            */
/* Release a buffer from BFV_AllocAligned.  Safe on NULL.                     */
/*                                                                            */
/* Arguments:                                                                 */
/*     pvBuffer : the buffer, or NULL.                                        */
/*                                                                            */
/* Returns:                                                                   */
/*     void : the pages are released.                                         */
/*----------------------------------------------------------------------------*/
void BFV_FreeAligned(void *pvBuffer)
{
    if (pvBuffer != NULL)
    {   /* A real allocation: release the whole reservation. */
        VirtualFree(pvBuffer, 0, MEM_RELEASE);
    }
}

/*----------------------------------------------------------------------------*/
/* BFV_FirstExtentDisk:                                                       */
/*                                                                            */
/* The physical disk a volume's first extent lies on, asked of an open        */
/* volume handle.  A volume that spans several disks (a striped or spanned    */
/* volume) has several extents; the caller that needs them all uses           */
/* BFV_VolumeOnDisk, which walks every extent.  This is for the system        */
/* volume, which is never spanned.                                            */
/*                                                                            */
/* Arguments:                                                                 */
/*     hVolume : the open volume.                                             */
/*                                                                            */
/* Returns:                                                                   */
/*     uint32_t : the disk number, or BFV_NO_DISK when the volume has no      */
/*                extents to report.                                          */
/*----------------------------------------------------------------------------*/
static uint32_t BFV_FirstExtentDisk(HANDLE hVolume)
{
    VOLUME_DISK_EXTENTS extents;
    DWORD               dwBytes;

    /* The answer and its byte count start cleared. */
    memset(&extents, 0, sizeof(extents));
    dwBytes = 0;

    /* One extent fits the fixed structure; ERROR_MORE_DATA still fills the   */
    /* first extent, which is all this needs.                                 */
    if (!DeviceIoControl(hVolume, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, NULL, 0,
                         &extents, sizeof(extents), &dwBytes, NULL))
    {   /* Refused outright, unless it is only saying there were more. */
        if (GetLastError() != ERROR_MORE_DATA)
        {   /* No extent information at all. */
            return(BFV_NO_DISK);
        }
    }

    if (extents.NumberOfDiskExtents == 0)
    {   /* A volume on no disk (a RAM disk, a mounted image). */
        return(BFV_NO_DISK);
    }

    /* The disk the first extent is on. */
    return((uint32_t)extents.Extents[0].DiskNumber);
}

/*----------------------------------------------------------------------------*/
/* BFV_SystemDisk:                                                            */
/*                                                                            */
/* Which physical disk the running Windows is on -- the disk under the volume */
/* that holds the Windows directory.  The answer is what the write guards     */
/* compare a target's disk number against.                                    */
/*                                                                            */
/* Arguments:                                                                 */
/*     None.                                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     uint32_t : the system disk's number, or BFV_NO_DISK if it cannot be    */
/*                found -- in which case a caller must treat EVERY disk as    */
/*                possibly the system disk and refuse to write.               */
/*----------------------------------------------------------------------------*/
uint32_t BFV_SystemDisk(void)
{
    wchar_t  wszWindows[MAX_PATH];
    wchar_t  wszVolume[8];
    HANDLE   hVolume;
    uint32_t uiDisk;

    /* The Windows directory's path, which begins with its drive letter and   */
    /* a colon; the buffer is cleared first, so it holds a terminated         */
    /* string whatever the call below leaves in it.                           */
    memset(wszWindows, 0, sizeof(wszWindows));

    if (GetWindowsDirectoryW(wszWindows, MAX_PATH) == 0 || wszWindows[1] != L':')
    {   /* No drive-letter path for Windows: nothing to open. */
        return(BFV_NO_DISK);
    }

    /* The volume's device path from the directory's drive letter: two   */
    /* backslashes, a dot, a backslash, the letter and a colon.          */
    wszVolume[0] = L'\\';
    wszVolume[1] = L'\\';
    wszVolume[2] = L'.';
    wszVolume[3] = L'\\';
    wszVolume[4] = wszWindows[0];
    wszVolume[5] = L':';
    wszVolume[6] = L'\0';

    /* Open that volume with no access: enough to ask for its extents, and   */
    /* allowed without Administrator.                                        */
    hVolume = CreateFileW(wszVolume, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL,
                          OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);

    if (hVolume == INVALID_HANDLE_VALUE)
    {   /* The system volume could not be opened even for nothing. */
        return(BFV_NO_DISK);
    }

    /* Its disk, then the handle is closed: only the number is needed. */
    uiDisk = BFV_FirstExtentDisk(hVolume);
    CloseHandle(hVolume);

    /* The disk Windows runs from, or BFV_NO_DISK. */
    return(uiDisk);
}

/*----------------------------------------------------------------------------*/
/* BFV_VolumeOnDisk:                                                          */
/*                                                                            */
/* Where an open volume lies relative to one disk -- on it, elsewhere, or not */
/* known -- judged by every extent the volume has, however many a spanned     */
/* volume has.  The handle may have been opened with NO access: the extents   */
/* request asks for none.                                                     */
/*                                                                            */
/* "Not known" is a separate answer, never folded into "elsewhere": a volume  */
/* that is in fact on the disk and is left mounted makes Windows refuse the   */
/* raw writes into its sectors part-way through the disk, so BFV_LockDisk     */
/* refuses the whole operation on this answer instead.                        */
/*                                                                            */
/* Arguments:                                                                 */
/*     hVolume   : the open volume.                                           */
/*     uiDisk    : the disk number to look for.                               */
/*     pdwError  : receives, for BFV_DISK_UNKNOWN, the Windows error that     */
/*                 stopped the question (never 0); untouched otherwise.       */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFV_DISK_ON, BFV_DISK_ELSEWHERE or BFV_DISK_UNKNOWN.             */
/*----------------------------------------------------------------------------*/
static int BFV_VolumeOnDisk(HANDLE hVolume, uint32_t uiDisk, DWORD *pdwError)
{
    VOLUME_DISK_EXTENTS *pExtents;
    DWORD                dwBytes;
    DWORD                cbBuffer;
    DWORD                dwError;
    DWORD                i;
    int                  iWhere;

    /* Room for BFV_EXTENTS_FIRST extents to begin with. */
    cbBuffer = (DWORD)(sizeof(VOLUME_DISK_EXTENTS) + (BFV_EXTENTS_FIRST - 1) * sizeof(DISK_EXTENT));
    pExtents = (VOLUME_DISK_EXTENTS *)malloc(cbBuffer);

    if (pExtents == NULL)
    {   /* Out of memory: where the volume lies cannot be asked, so it is   */
        /* not known -- the lock refuses rather than guess.                 */
        *pdwError = ERROR_NOT_ENOUGH_MEMORY;
        return(BFV_DISK_UNKNOWN);
    }

    /* Ask, into the cleared buffer. */
    memset(pExtents, 0, cbBuffer);
    dwBytes = 0;

    if (!DeviceIoControl(hVolume, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, NULL, 0,
                         pExtents, cbBuffer, &dwBytes, NULL))
    {   /* Too small, or refused.  The error is taken at once, before the   */
        /* frees below can change it.                                       */
        dwError = GetLastError();

        if (dwError == ERROR_INVALID_FUNCTION || dwError == ERROR_NOT_SUPPORTED || dwError == ERROR_NOT_READY)
        {   /* The volume's driver does not answer the request at all (it      */
            /* has no disk extents to report: a volume on no physical disk),   */
            /* or the drive has no media in it.  Either way nothing of it      */
            /* lies on a disk the caller is about to write, which is a disk    */
            /* with media in it -- elsewhere.                                  */

            /* The buffer goes; the answer is "elsewhere". */
            free(pExtents);
            return(BFV_DISK_ELSEWHERE);
        }

        if (dwError != ERROR_MORE_DATA)
        {   /* Refused for any other reason: the volume may well be on a   */
            /* disk, and which one cannot be told.                         */

            /* The buffer goes; the answer is "not known", with the error   */
            /* (never 0: a refusal that set none is a general failure).     */
            free(pExtents);
            if (dwError == 0)
            {   /* No error was set: the general failure stands for it. */
                dwError = ERROR_GEN_FAILURE;
            }
            *pdwError = dwError;
            return(BFV_DISK_UNKNOWN);
        }

        /* The count came back; size the buffer for all of them and ask        */
        /* again, so a spanned volume with many extents is examined in full.   */
        cbBuffer = (DWORD)(sizeof(VOLUME_DISK_EXTENTS) + (pExtents->NumberOfDiskExtents - 1) * sizeof(DISK_EXTENT));
        free(pExtents);
        pExtents = (VOLUME_DISK_EXTENTS *)malloc(cbBuffer);

        if (pExtents == NULL)
        {   /* Out of memory on the second try: not known, as above. */
            *pdwError = ERROR_NOT_ENOUGH_MEMORY;
            return(BFV_DISK_UNKNOWN);
        }

        /* Ask again, into the bigger cleared buffer. */
        memset(pExtents, 0, cbBuffer);

        if (!DeviceIoControl(hVolume, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, NULL, 0,
                             pExtents, cbBuffer, &dwBytes, NULL))
        {   /* Refused the second time, though it answered the first: not   */
            /* known.  The error is taken before the free.                  */
            dwError = GetLastError();

            /* The buffer goes; the answer is "not known", never 0 as the      */
            /* error (a refusal that set none is reported as a general one).   */
            free(pExtents);
            if (dwError == 0)
            {   /* No error was set: the general failure stands for it. */
                dwError = ERROR_GEN_FAILURE;
            }
            *pdwError = dwError;
            return(BFV_DISK_UNKNOWN);
        }
    }

    /* Look through every extent; elsewhere until one is on the disk. */
    iWhere = BFV_DISK_ELSEWHERE;

    for (i = 0; i < pExtents->NumberOfDiskExtents; i++)
    {   /* One extent per iteration. */
        if ((uint32_t)pExtents->Extents[i].DiskNumber == uiDisk)
        {   /* Part of this volume is on the disk. */
            iWhere = BFV_DISK_ON;
        }
    }

    /* The extent list is no longer needed. */
    free(pExtents);

    /* On the disk, or elsewhere (no extent at all is elsewhere too: a   */
    /* volume on no disk).                                               */
    return(iWhere);
}

/*----------------------------------------------------------------------------*/
/* Bfv_AsciiVolumePath:                                                       */
/*                                                                            */
/* A volume GUID path ("\\?\Volume{...}") as plain ASCII, for BFV_LockDisk's  */
/* failure sentence.  Such a path is ASCII by its form, so the copy is exact; */
/* a character outside ASCII, which the form never holds, would be written    */
/* as '?' rather than as a byte the reader's code page might misshow.         */
/* Copying it to a char buffer of fixed size, rather than printing the wide   */
/* string with %ls, is what lets every sentence be proved to fit its buffer.  */
/*                                                                            */
/* Arguments:                                                                 */
/*     pwszVolume : the volume path, wide.                                    */
/*     pszOut     : receives the ASCII copy, NUL-terminated.                  */
/*     cbOut      : pszOut's size in bytes, at least 1; a longer path is cut. */
/*                                                                            */
/* Returns:                                                                   */
/*     void : pszOut holds the copy.                                          */
/*----------------------------------------------------------------------------*/
static void Bfv_AsciiVolumePath(const wchar_t *pwszVolume, char *pszOut, size_t cbOut)
{
    size_t i;

    for (i = 0; i + 1 < cbOut && pwszVolume[i] != L'\0'; i++)
    {   /* One character per iteration, while room for it and the NUL is left. */
        if (pwszVolume[i] < 0x80)
        {   /* ASCII: copied as it is. */
            pszOut[i] = (char)pwszVolume[i];
        }
        else
        {   /* Outside ASCII (a GUID path never is): a question mark. */
            pszOut[i] = '?';
        }
    }

    /* The terminator, after the last character copied. */
    pszOut[i] = '\0';
}

/*----------------------------------------------------------------------------*/
/* BFV_LockDisk:                                                              */
/*                                                                            */
/* Lock and dismount every mounted volume on one disk, before that disk is    */
/* written underneath them.  Windows refuses a raw write to sectors inside a  */
/* mounted volume unless the volume is locked or dismounted, so a write that  */
/* skipped this would die with access denied part-way through, leaving the    */
/* disk half written.  The handles that hold the locks are handed back in     */
/* pLocks and are the locks: they stay open until BFV_UnlockDisk.             */
/*                                                                            */
/* pLocks is emptied WITHOUT closing what it held: call BFV_UnlockDisk on it  */
/* first, or those handles -- and their locks -- are lost until the program   */
/* ends.  BFE_Open does.                                                      */
/*                                                                            */
/* A volume on another disk never stops the lock; a volume whose disk cannot  */
/* be told does, since left mounted on the disk it would turn the write into  */
/* a half-written disk.                                                       */
/*                                                                            */
/* Arguments:                                                                 */
/*     uiDisk : the physical disk about to be written.                        */
/*     pLocks : receives the handles held; emptied first.  On a failure its   */
/*              szFailure says why, for the caller's detail line.             */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0 with every volume on the disk locked; otherwise a Windows      */
/*           error -- the volumes could not be listed, a volume's disk could  */
/*           not be told, the disk has more volumes than BFV_MAX_LOCKS, or a  */
/*           volume on the disk could not be opened for writing or locked --  */
/*           with every lock taken so far released again.                     */
/*----------------------------------------------------------------------------*/
int BFV_LockDisk(uint32_t uiDisk, BFV_Locks *pLocks)
{
    wchar_t wszVolume[BFV_VOLUME_PATH_MAX];
    char    szVolume[BFV_VOLUME_PATH_MAX];
    HANDLE  hFind;
    HANDLE  hVolume;
    DWORD   dwBytes;
    DWORD   dwError;
    size_t  cchVolume;
    int     bMore;
    int     iWhere;

    /* No volume held yet and no failure recorded; then the first volume    */
    /* of the enumeration.  The volumes are found by ENUMERATING VOLUMES,   */
    /* not by walking the drive letters A to Z: a volume mounted on a       */
    /* folder, a system partition Windows has touched, or a volume whose    */
    /* letter was removed has no letter and would be missed -- and then     */
    /* not locked.                                                          */
    memset(pLocks, 0, sizeof(*pLocks));
    memset(wszVolume, 0, sizeof(wszVolume));

    hFind = FindFirstVolumeW(wszVolume, BFV_VOLUME_PATH_MAX);

    if (hFind == INVALID_HANDLE_VALUE)
    {   /* The volume manager could not be asked: which volumes are on the   */
        /* disk cannot be known, so nothing is locked and the caller         */
        /* refuses.  The error is taken before the sentence is written.      */
        dwError = GetLastError();
        snprintf(pLocks->szFailure, sizeof(pLocks->szFailure),
                 "the volumes could not be listed to lock those on disk %u (Windows error %lu)",
                 uiDisk, (unsigned long)dwError);
        return((int)dwError);
    }

    /* The first volume is in hand; the loop takes the rest in turn. */
    bMore = TRUE;

    while (bMore)
    {   /* One volume GUID path per iteration. */

        /* The enumeration gives "\\?\Volume{...}\"; the trailing backslash    */
        /* would open the volume's ROOT DIRECTORY rather than the volume, so   */
        /* it is removed before the open.                                      */
        cchVolume = wcslen(wszVolume);

        if (cchVolume > 0 && wszVolume[cchVolume - 1] == L'\\')
        {   /* Strip it. */
            wszVolume[cchVolume - 1] = L'\0';
        }

        /* Its path in ASCII too, ready for a failure sentence below. */
        Bfv_AsciiVolumePath(wszVolume, szVolume, sizeof(szVolume));

        /* STEP 1: where the volume lies, asked through a handle opened       */
        /* with NO access -- enough for the extents request, allowed on a     */
        /* volume whatever holds it, and touching nothing.  Every volume on   */
        /* the system passes here, but the read-write open a lock needs is    */
        /* made in STEP 2, for a volume on the disk only, so a volume on      */
        /* another disk that would refuse it -- an empty card-reader slot,    */
        /* a volume another program holds, a locked BitLocker volume --       */
        /* never stops the lock.                                              */
        hVolume = CreateFileW(wszVolume, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
                              NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);

        if (hVolume == INVALID_HANDLE_VALUE)
        {   /* Not even that open: why decides it.  The error is taken at   */
            /* once.                                                        */
            dwError = GetLastError();

            if (dwError == ERROR_FILE_NOT_FOUND || dwError == ERROR_PATH_NOT_FOUND || dwError == ERROR_NOT_READY)
            {   /* Gone since the enumeration listed it (a card pulled), or   */
                /* a drive with no media: nothing of it is on the disk.       */
                iWhere = BFV_DISK_ELSEWHERE;
            }
            else
            {   /* Refused for another reason: where it lies is not known. */
                iWhere = BFV_DISK_UNKNOWN;
            }
        }
        else
        {   /* Open: asked, then closed -- only the answer is needed here.   */
            /* (STEP 2 opens a volume on the disk again, for writing.)       */
            iWhere = BFV_VolumeOnDisk(hVolume, uiDisk, &dwError);
            CloseHandle(hVolume);
        }

        if (iWhere == BFV_DISK_UNKNOWN)
        {   /* Its disk could not be told.  It may be on the disk being       */
            /* written, and left mounted there it would make Windows refuse   */
            /* the writes into its sectors part-way through: refuse now,      */
            /* before anything is written, naming the volume.                 */

            /* Say which and why; end the enumeration; release every lock   */
            /* taken so far; return the error.                              */
            snprintf(pLocks->szFailure, sizeof(pLocks->szFailure),
                     "could not tell whether volume %s is on disk %u (Windows error %lu)",
                     szVolume, uiDisk, (unsigned long)dwError);
            FindVolumeClose(hFind);
            BFV_UnlockDisk(pLocks);
            return((int)dwError);
        }

        if (iWhere == BFV_DISK_ON)
        {   /* STEP 2: on the target disk -- opened for writing, locked,   */
            /* dismounted, and its handle kept.                            */
            if (pLocks->nVolumes >= BFV_MAX_LOCKS)
            {   /* More volumes than the table holds: refuse rather than   */
                /* leave one unlocked.                                     */

                /* Say so; end the enumeration; release every lock taken   */
                /* so far; refuse.                                         */
                snprintf(pLocks->szFailure, sizeof(pLocks->szFailure),
                         "disk %u holds more than %d volumes; they could not all be locked",
                         uiDisk, BFV_MAX_LOCKS);
                FindVolumeClose(hFind);
                BFV_UnlockDisk(pLocks);
                return(ERROR_TOO_MANY_OPEN_FILES);
            }

            /* Opened read-write: the access a lock needs. */
            hVolume = CreateFileW(wszVolume, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                  NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);

            if (hVolume == INVALID_HANDLE_VALUE)
            {   /* A volume on the disk that will not open for writing        */
                /* cannot be locked, and the write must not go on under it.   */

                /* Take the error first; say which volume; end the            */
                /* enumeration; release every lock taken so far; return it.   */
                dwError = GetLastError();
                snprintf(pLocks->szFailure, sizeof(pLocks->szFailure),
                         "volume %s on disk %u could not be opened to lock it (Windows error %lu); close what is using it",
                         szVolume, uiDisk, (unsigned long)dwError);
                FindVolumeClose(hFind);
                BFV_UnlockDisk(pLocks);
                return((int)dwError);
            }

            /* Lock it: refused while anything else has it open. */
            dwBytes = 0;

            if (!DeviceIoControl(hVolume, FSCTL_LOCK_VOLUME, NULL, 0, NULL, 0, &dwBytes, NULL))
            {   /* Something has files open on it: the user must close them. */

                /* Take the error first; say which volume; close it; end      */
                /* the enumeration; release every lock taken so far; return   */
                /* the error.                                                 */
                dwError = GetLastError();
                snprintf(pLocks->szFailure, sizeof(pLocks->szFailure),
                         "volume %s on disk %u could not be locked (Windows error %lu); close what is using it",
                         szVolume, uiDisk, (unsigned long)dwError);
                CloseHandle(hVolume);
                FindVolumeClose(hFind);
                BFV_UnlockDisk(pLocks);
                return((int)dwError);
            }

            /* Dismounting a locked volume cannot fail for want of users; the   */
            /* result is not looked at.                                         */
            DeviceIoControl(hVolume, FSCTL_DISMOUNT_VOLUME, NULL, 0, NULL, 0, &dwBytes, NULL);

            /* Keep the handle in the table: it IS the lock.  Closing it   */
            /* would let Windows remount the volume at once, mid-write.    */
            pLocks->ahVolume[pLocks->nVolumes] = hVolume;
            pLocks->nVolumes++;
        }

        /* A volume elsewhere needs nothing more: its query handle is   */
        /* already closed, and it is not ours to touch.                 */

        /* The next volume, into a cleared buffer; FALSE ends the loop. */
        memset(wszVolume, 0, sizeof(wszVolume));
        bMore = FindNextVolumeW(hFind, wszVolume, BFV_VOLUME_PATH_MAX);

        if (!bMore)
        {   /* No next volume.  Only ERROR_NO_MORE_FILES means the list is    */
            /* done; any other failure stops the walk with the volumes        */
            /* after this point never looked at, and one of them may lie on   */
            /* the disk -- left mounted, it would make Windows refuse the     */
            /* writes into its sectors part-way through.  So that is          */
            /* refused, as a volume whose disk cannot be told is.  The        */
            /* error is taken at once, before anything else can change it.    */
            dwError = GetLastError();

            if (dwError != ERROR_NO_MORE_FILES)
            {   /* The listing failed part-way: say why, end the enumeration,   */
                /* release every lock taken so far, return the error.           */
                snprintf(pLocks->szFailure, sizeof(pLocks->szFailure),
                         "the volumes could not all be listed to lock those on disk %u (Windows error %lu)",
                         uiDisk, (unsigned long)dwError);
                FindVolumeClose(hFind);
                BFV_UnlockDisk(pLocks);
                return((int)dwError);
            }
        }
    }

    /* The enumeration is done; its handle goes. */
    FindVolumeClose(hFind);

    /* Every volume on the disk is locked and held. */
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFV_UnlockDisk:                                                            */
/*                                                                            */
/* Release the volumes BFV_LockDisk holds.  Closing a locked volume's handle  */
/* releases the lock, and Windows then remounts the volume (re-reading what   */
/* was written).                                                              */
/*                                                                            */
/* Arguments:                                                                 */
/*     pLocks : the held handles; emptied.                                    */
/*                                                                            */
/* Returns:                                                                   */
/*     void : no handle is held.                                              */
/*----------------------------------------------------------------------------*/
void BFV_UnlockDisk(BFV_Locks *pLocks)
{
    int i;

    for (i = 0; i < pLocks->nVolumes; i++)
    {   /* One held volume per iteration: closing its handle releases the   */
        /* lock; the slot is then marked empty.                             */
        CloseHandle(pLocks->ahVolume[i]);
        pLocks->ahVolume[i] = INVALID_HANDLE_VALUE;
    }

    /* None held, so a second call does nothing. */
    pLocks->nVolumes = 0;
}

/*----------------------------------------------------------------------------*/
/* BFV_RefreshDisk:                                                           */
/*                                                                            */
/* Ask Windows to re-read a disk's partition table.  After an image is        */
/* written to a card its layout has changed under Windows' feet, and without  */
/* this Explorer goes on showing the old partitions until the card is pulled  */
/* and re-seated.  Nothing to report on failure: the data is already written  */
/* and a re-seat fixes the display.                                           */
/*                                                                            */
/* Arguments:                                                                 */
/*     pTarget : the written device.                                          */
/*                                                                            */
/* Returns:                                                                   */
/*     void : the request was made.                                           */
/*----------------------------------------------------------------------------*/
void BFV_RefreshDisk(BFV_Target *pTarget)
{
    DWORD dwBytes;

    if (!pTarget->bDevice)
    {   /* A file has no partition table Windows is watching. */
        return;
    }

    /* Ask; the result is not looked at (see above). */
    dwBytes = 0;
    DeviceIoControl(pTarget->hFile, IOCTL_DISK_UPDATE_PROPERTIES, NULL, 0, NULL, 0, &dwBytes, NULL);
}
