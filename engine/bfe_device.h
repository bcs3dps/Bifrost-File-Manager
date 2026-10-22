/*----------------------------------------------------------------------------*/
/* bfe_device.h                                                               */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The engine's raw I/O layer: one way of opening "the thing the bytes are    */
/* in", whether it is an image FILE or a real DEVICE (\\.\PhysicalDriveN, a   */
/* volume), and the Windows facts about a device that the open needs -- its   */
/* length, its sector size, which physical disk it is, whether that disk is   */
/* the one Windows is running from -- plus the locking of a disk's mounted    */
/* volumes that a write to it requires.                                       */
/*                                                                            */
/* Everything here is Win32.  The block device (bfe_blockdev.c) sits on top   */
/* of it for lwext4's benefit; the imaging functions (bfe_image.c) use it     */
/* directly.  Internal to the engine: nothing here is exported from the DLL.  */
/*                                                                            */
/* WHY A DEVICE IS NOT JUST A FILE.  A raw disk refuses any read or write     */
/* that is not a whole number of its sectors at a sector-aligned offset, and  */
/* with the no-buffering flag the buffer itself must be aligned too; it has   */
/* no "end of file" -- a read past its end fails whole rather than coming     */
/* back short; and its length is not a file length but an answer from a       */
/* device control.  BFV_Target records what kind of thing was opened so the   */
/* layers above can do the right one.                                         */
/*----------------------------------------------------------------------------*/

/* Include guard: the file is read once however many files include it. */
#ifndef BFE_DEVICE_H
#define BFE_DEVICE_H

/* Fixed-width integers, size_t, wchar_t, and Windows for HANDLE (which   */
/* BFV_Target and BFV_Locks hold).                                        */
#include <stdint.h>
#include <stddef.h>
#include <wchar.h>
#include <windows.h>

/*----------------------------------------------------------------------------*/
/* Definitions -- documentation table.                                        */
/*----------------------------------------------------------------------------*/
#define BFV_FILE_SECTOR     512             // the sector size an image FILE is treated as having (lwext4's unit, a disk image's convention)
#define BFV_MAX_LOCKS       64              // most volumes one disk write will lock; more than any real disk carries
#define BFV_NO_DISK         0xFFFFFFFFu     // "not on any physical disk" for a disk number
#define BFV_MODEL_BYTES     64              // room for a device's vendor and product text, NUL included
#define BFV_LOCK_TEXT_BYTES 192             // room for BFV_Locks.szFailure: one sentence naming a volume's GUID path (48 characters once BFV_LockDisk strips its trailing backslash) and two numbers, NUL included

/*----------------------------------------------------------------------------*/
/* Access modes for BFV_Open -- documentation table.                          */
/*----------------------------------------------------------------------------*/
#define BFV_ACCESS_QUERY    0               // learn the facts only: a device is opened with NO access (needs no Administrator) and the target is handed back CLOSED
#define BFV_ACCESS_READ     1               // read
#define BFV_ACCESS_WRITE    2               // read and write; a file must exist
#define BFV_ACCESS_CREATE   3               // read and write; a FILE is created, or truncated if it exists (a device is the same as WRITE)
#define BFV_ACCESS_OPENKEEP 4               // read and write; a FILE is created if missing but an existing one is KEPT as it is -- BFV_Truncate cuts it once the caller has checked what it opened (a device is the same as WRITE)

/*----------------------------------------------------------------------------*/
/* BFV_Target -- an open file or device and the facts I/O on it needs.        */
/*                                                                            */
/* THE FIELD ORDER IS RELIED ON ELSEWHERE: bfe_blockdev.c initialises its     */
/* static BFD_target with a positional list of these nine fields.  A field    */
/* added, removed or moved here must be matched there.                        */
/*----------------------------------------------------------------------------*/
typedef struct BFV_Target
{
    HANDLE   hFile;                         // the open handle; INVALID_HANDLE_VALUE when closed
    uint64_t ullSize;                       // its length in bytes: the file's size, or the device's reported length
    uint32_t uiSector;                      // bytes per sector: BFV_FILE_SECTOR for a file, the device's real figure for a device
    uint32_t uiDisk;                        // the physical disk number a device sits on; BFV_NO_DISK for a file
    uint32_t uiBus;                         // a device's bus, Windows' STORAGE_BUS_TYPE value (7 USB, 12 SD, 13 MMC, 11 SATA, 17 NVMe ...); 0 for a file
    int      bDevice;                       // TRUE when the handle is a raw device (sector-aligned, unbuffered I/O required)
    int      bRemovable;                    // TRUE when Windows calls the device's media removable; FALSE for a file
    int      bWritable;                     // TRUE when it was opened with write access
    char     szModel[BFV_MODEL_BYTES];      // a device's vendor and product, UTF-8, trimmed; "" for a file
} BFV_Target;

/*----------------------------------------------------------------------------*/
/* BFV_Locks -- the volume handles held locked and dismounted while a disk is */
/* written.  Closing a handle lets Windows remount that volume, so they are   */
/* kept until the write is over.  When BFV_LockDisk fails, szFailure says     */
/* why in one sentence, which every caller passes to BFX_Fail as the detail   */
/* line: the cases need different advice (a volume in use on the disk, a      */
/* volume whose disk could not be told, too many volumes, the volumes not     */
/* listed), and only the lock knows which it met.                             */
/*----------------------------------------------------------------------------*/
typedef struct BFV_Locks
{
    HANDLE ahVolume[BFV_MAX_LOCKS];         // the locked volumes' handles
    int    nVolumes;                        // how many of them are in use
    char   szFailure[BFV_LOCK_TEXT_BYTES];  // why the last BFV_LockDisk failed, ASCII, NUL-terminated; "" after a success; BFV_UnlockDisk leaves it alone
} BFV_Locks;

/*----------------------------------------------------------------------------*/
/* BFV_Open            : open a path for raw I/O in one of the BFV_ACCESS_*   */
/*                       modes -- a device with no buffering and share read + */
/*                       write, a file as an ordinary file -- and fill in its */
/*                       facts.  Returns 0, or the Windows error code.  In    */
/*                       QUERY mode the facts are filled in and the handle    */
/*                       is closed again before returning.                    */
/* BFV_Close           : close a target (safe on one never opened).           */
/* BFV_Read / BFV_Write: transfer cbBytes at a byte offset.  On a device the  */
/*                       offset and length must be sector multiples and the   */
/*                       buffer page-aligned (BFV_AllocAligned); on a file    */
/*                       anything goes.  Returns 0 or the Windows error:      */
/*                       ERROR_INVALID_PARAMETER for a misaligned device      */
/*                       transfer, ERROR_HANDLE_EOF for a read past the end   */
/*                       (or a short one) and a device write past its end,    */
/*                       ERROR_WRITE_FAULT for a short write, and             */
/*                       ERROR_ACCESS_DENIED for a write to a target not      */
/*                       opened writable.                                     */
/* BFV_Flush           : FlushFileBuffers, so a device write has reached the  */
/*                       media.  Returns 0 or the Windows error.              */
/* BFV_Truncate        : cut an open FILE to nothing (a device is left as it  */
/*                       is) -- the second half of a BFV_ACCESS_OPENKEEP      */
/*                       open.  Returns 0 or the Windows error.               */
/* BFV_SameObject      : TRUE when two open targets are the same thing, by    */
/*                       identity, not by path: two files with the same       */
/*                       volume serial and file index, or two devices on the  */
/*                       same physical disk.  A file and a device are never   */
/*                       the same.  When a file's identity cannot be read the */
/*                       answer is TRUE, so a caller that refuses on TRUE     */
/*                       refuses what it cannot tell apart.                   */
/* BFV_AllocAligned /  : a page-aligned buffer for device I/O, and its        */
/* BFV_FreeAligned       release.                                             */
/* BFV_SystemDisk      : the physical disk number holding the running         */
/*                       Windows, or BFV_NO_DISK if it cannot be told (the    */
/*                       caller treats "cannot be told" as "refuse").         */
/* BFV_LockDisk        : lock and dismount every mounted volume on a disk     */
/*                       (lettered or not), and keep the handles in pLocks.   */
/*                       A volume on another disk never stops it.  Returns    */
/*                       0, or a Windows                                      */
/*                       error with pLocks->szFailure saying why and          */
/*                       everything locked so far released again -- a volume  */
/*                       on the disk that is in use, or one whose disk could  */
/*                       not be told (refused rather than written blind).     */
/*                       pLocks is emptied WITHOUT closing what it held:      */
/*                       unlock it first.                                     */
/* BFV_UnlockDisk      : release what BFV_LockDisk holds (safe when empty).   */
/* BFV_RefreshDisk     : ask Windows to re-read a disk's partition table      */
/*                       after its sectors were written.                      */
/*----------------------------------------------------------------------------*/
int   BFV_Open(const wchar_t *pwszPath, int iAccess, BFV_Target *pTarget);
void  BFV_Close(BFV_Target *pTarget);
int   BFV_Read(BFV_Target *pTarget, uint64_t ullOffset, void *pvBuffer, uint32_t cbBytes);
int   BFV_Write(BFV_Target *pTarget, uint64_t ullOffset, const void *pvBuffer, uint32_t cbBytes);
int   BFV_Flush(BFV_Target *pTarget);
int   BFV_Truncate(BFV_Target *pTarget);
int   BFV_SameObject(const BFV_Target *pFirst, const BFV_Target *pSecond);
void *BFV_AllocAligned(size_t cbBytes);
void  BFV_FreeAligned(void *pvBuffer);
uint32_t BFV_SystemDisk(void);
int   BFV_LockDisk(uint32_t uiDisk, BFV_Locks *pLocks);
void  BFV_UnlockDisk(BFV_Locks *pLocks);
void  BFV_RefreshDisk(BFV_Target *pTarget);

/* The end of the include guard. */
#endif // BFE_DEVICE_H
