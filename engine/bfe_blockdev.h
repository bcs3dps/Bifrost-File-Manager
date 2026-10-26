/*----------------------------------------------------------------------------*/
/* bfe_blockdev.h                                                             */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The engine's own lwext4 block device.  It stands in for lwext4's bundled   */
/* ext4_filedev.c, which always opens the image read-write through C stdio    */
/* and takes a narrow path; this one takes a UTF-16 path that may name an     */
/* image FILE or a real DEVICE (\\.\PhysicalDriveN, a volume), opens it       */
/* read-only for a read-only session, does a device's sector-aligned          */
/* unbuffered I/O through a bounce buffer, and can present a byte range of    */
/* either -- a partition inside a raw disk dump, or on a card -- as the       */
/* device.                                                                    */
/*                                                                            */
/* Internal to the engine: nothing here is exported from the DLL.             */
/*----------------------------------------------------------------------------*/

/* Include guard: the file is read once however many files include it. */
#ifndef BFE_BLOCKDEV_H
#define BFE_BLOCKDEV_H

/* Fixed-width integers, size_t (wchar_t comes with stddef.h too), and   */
/* bool for the flag arguments and answers below.                        */
#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>

/* lwext4's block device, named but not defined: BFD_Get hands out a   */
/* pointer to one, and a file that only passes it on to ext4_device_   */
/* register need not include lwext4's headers to do so.                */
struct ext4_blockdev;

/*----------------------------------------------------------------------------*/
/* BFD_Configure : record which file or device, which byte range and which    */
/*                 access mode the NEXT device open uses.  Returns 0, EINVAL  */
/*                 when the range is not a multiple of the 512-byte sector,   */
/*                 or ENOMEM when the path cannot be copied -- each caller    */
/*                 reports the one it got.  Every operation that uses         */
/*                 the block device starts here, because this is also what    */
/*                 resets the per-operation state -- the lost-write verdict   */
/*                 and the read-back setting.  Call it only with the target   */
/*                 CLOSED: it marks the target closed without closing it.     */
/* BFD_Get       : the one static lwext4 block device, for                    */
/*                 ext4_device_register.                                      */
/*----------------------------------------------------------------------------*/
int BFD_Configure(const wchar_t *pwszImage, uint64_t ullOffset, uint64_t ullLength, bool bReadOnly);
struct ext4_blockdev *BFD_Get(void);

/*------------------------------------------------------------------------------*/
/* The target itself, for a second user of the same file or device: the         */
/* filesystem check (bfe_fsck_io.c) drives e2fsprogs' libext2fs over these      */
/* instead of over lwext4's hooks, so the device rules -- the aligned bounce,   */
/* the read-modify-write at a ragged sector, the read-only refusal -- live in   */
/* one place.  FatFs's device layer (bfe_fatio.c) is the third user, for a      */
/* FAT filesystem.  Only one user has the target open at a time: the engine     */
/* never runs a check while an image is mounted, and lwext4 and FatFs never     */
/* hold one session's target together.                                          */
/*                                                                              */
/* BFD_TargetOpen   : open what BFD_Configure recorded, in its mode, and hand   */
/*                    back the byte length of the filesystem range (the         */
/*                    configured length, or what remains after the offset).     */
/*                    Returns EOK, or the lwext4-style code BFD_DevOpen         */
/*                    returns for the same failures (EIO, ENOMEM, EINVAL).      */
/* BFD_TargetRead   : read cbBytes at a byte offset counted from the START of   */
/* BFD_TargetWrite    the file or device (the caller adds the partition         */
/*                    offset), through the bounce when it is a device.  With    */
/*                    BFD_SetVerify on, a device WRITE is read back and         */
/*                    compared: one the device did not keep returns ERROR_CRC   */
/*                    and is remembered for BFD_WriteNotKept.  Returns 0,       */
/*                    ERROR_INVALID_HANDLE when nothing is open,                */
/*                    ERROR_ACCESS_DENIED for a write on a read-only target     */
/*                    (lwext4's own hook turns that into EROFS before it        */
/*                    gets here), or the Windows error; a short transfer is     */
/*                    ERROR_HANDLE_EOF.  A read or write failure is also        */
/*                    noted for BFD_LastIoText.                                 */
/* BFD_TargetFlush  : make the writes real (FlushFileBuffers).  Returns 0 or    */
/*                    the Windows error.                                        */
/* BFD_TargetClose  : flush, close and release the bounce; safe when closed.    */
/* BFD_TargetIsOpen : true between a successful open and the close.             */
/* BFD_TargetOffset : the configured byte offset of the filesystem.             */
/* BFD_TargetIsDevice / BFD_TargetReadOnly : the open target's kind and mode.   */
/* BFD_TargetIsObject : true when another open target (bfe_device.h's           */
/*                    BFV_Target, named here by its structure tag) is the       */
/*                    session's own file or disk, by identity -- what the raw   */
/*                    calls refuse to write.  false when no target is open.     */
/*------------------------------------------------------------------------------*/
/* The raw target is declared by its structure tag only, so this header need    */
/* not include bfe_device.h (and windows.h with it) for the one prototype       */
/* that takes it; a file calling BFD_TargetIsObject includes bfe_device.h.      */
struct BFV_Target;
/* The session target's functions, in the order the box above lists them. */
int      BFD_TargetOpen(uint64_t *pullLength);
int      BFD_TargetRead(uint64_t ullOffset, void *pvBuf, uint64_t cbBytes);
int      BFD_TargetWrite(uint64_t ullOffset, const void *pvBuf, uint64_t cbBytes);
int      BFD_TargetFlush(void);
void     BFD_TargetClose(void);
bool     BFD_TargetIsOpen(void);
uint64_t BFD_TargetOffset(void);
bool     BFD_TargetIsDevice(void);
bool     BFD_TargetReadOnly(void);
bool     BFD_TargetIsObject(const struct BFV_Target *pOther);

/*----------------------------------------------------------------------------*/
/* BFD_LastIoText : one line about the last device-level failure -- the       */
/*                  Windows error, the offset and the length -- or "" when    */
/*                  none has happened since it was last taken.  lwext4 only   */
/*                  passes "I/O error" up, so the engine's error recorder     */
/*                  appends this to its detail line and clears it.            */
/* BFD_ClearIoText: forget the line.  Called by the error recorder after it   */
/*                  reports one, and by every public call's gate when the     */
/*                  call begins, so a line never outlives its own call.       */
/*----------------------------------------------------------------------------*/
const char *BFD_LastIoText(void);
void        BFD_ClearIoText(void);

/*----------------------------------------------------------------------------*/
/* BFD_WriteNotKept : true when a device write of the current operation read  */
/*                    back different from what was written (a counterfeit or  */
/*                    failing card), with the byte offset where the first     */
/*                    differing read-back piece (up to 1 MiB) starts in       */
/*                    *pullAt (which may be NULL).  Sticky until the next     */
/*                    BFD_Configure, so an operation whose library carried on */
/*                    past the write error still fails at its end.            */
/*----------------------------------------------------------------------------*/
bool BFD_WriteNotKept(uint64_t *pullAt);

/*----------------------------------------------------------------------------*/
/* BFD_Generation : how many configurations BFD_Configure has recorded -- a   */
/*                  count that moves on each time an operation begins on the  */
/*                  block device, and only then.  The engine's gate takes it  */
/*                  when a call enters, so the error recorder can tell        */
/*                  whether the lost-write verdict above belongs to an        */
/*                  operation that call began, or was left by an earlier      */
/*                  one.  It wraps after 2^32 configurations, harmlessly: it  */
/*                  is only ever compared for equality with a recent value.   */
/*----------------------------------------------------------------------------*/
uint32_t BFD_Generation(void);

/*----------------------------------------------------------------------------*/
/* BFD_SetVerify : turn the read-back of device writes on or off for the      */
/*                 operation just configured (BFD_Configure turns it off).    */
/*                 A format, a resize and a repair turn it on; a session      */
/*                 when its caller asked.                                     */
/*----------------------------------------------------------------------------*/
void BFD_SetVerify(bool bOn);

/* The end of the include guard. */
#endif // BFE_BLOCKDEV_H
