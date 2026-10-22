/*----------------------------------------------------------------------------*/
/* bfe_fat.h                                                                  */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The engine's FAT session: a FAT12, FAT16, FAT32 or exFAT filesystem opened */
/* through FatFs (engine\fatfs) where BFE_Open finds no ext2/3/4 one, and the */
/* session calls that answer for it; and the making of such a volume for      */
/* BFE_MakeFs / BFE_MakeFsAt.  bfe_fat.c holds the FatFs side of each call;   */
/* bifrost_engine.c decides which filesystem a session holds and sends the    */
/* call here, as bfe_mkfs.c does for a format.  Beneath FatFs is bfe_fatio.c, */
/* its device layer over the engine's block device.                           */
/*                                                                            */
/* A FAT session is read-only or writable as opened.  It answers every        */
/* session call but those for modes, owners and links, which the gate         */
/* refuses (bifrost_engine.c, BFX_Enter); the walks reach it through          */
/* BFT_StatEntry and BFT_CollectNames, and bfe_fs.c's file calls through the  */
/* write helpers below.                                                       */
/*                                                                            */
/* Every function below is called with the engine's busy flag HELD.  The      */
/* session calls (BFT_Mount to BFT_VolumeInfo) record their own failure with  */
/* BFX_Fail before returning -- the caller only releases the flag and returns */
/* the code.  The three errno-style ones (BFT_FatErrno, BFT_StatEntry,        */
/* BFT_CollectNames) record nothing, as the lwext4 code they stand beside     */
/* records nothing: their callers decide what a failure means.                */
/*                                                                            */
/* Internal to the engine: nothing here is exported from the DLL.             */
/*----------------------------------------------------------------------------*/

/* Include guard: the file is read once however many files include it. */
#ifndef BFE_FAT_H
#define BFE_FAT_H

/* Fixed-width integers, and the public interface for BFE_Entry,   */
/* BFE_VolumeInfo, the list callback and the status codes.         */
#include <stdint.h>
#include "bifrost_engine.h"

/*----------------------------------------------------------------------------*/
/* BFT_Mount      : mount the range BFD_Configure recorded as FAT or exFAT,   */
/*                  counting in uiSectorBytes-byte sectors (512 for a file,   */
/*                  the device's own figure for a card).  FatFs opens the     */
/*                  block device's target itself.  Returns BFE_OK with the    */
/*                  volume mounted and the target open; on any failure        */
/*                  nothing is left mounted or open, and the code is          */
/*                  BFE_ERR_MOUNT when the range holds no FAT or exFAT        */
/*                  filesystem either -- its detail line naming lwext4's      */
/*                  reason, pszExtReason, beside FatFs's -- or another code   */
/*                  for a range that could not be read.                       */
/* BFT_Unmount    : unmount the volume and close the target.  Safe to call    */
/*                  when nothing is mounted.  Returns BFE_OK, or              */
/*                  BFE_ERR_IO_IMAGE when FatFs refused the unmount (the      */
/*                  target is closed regardless).                             */
/* BFT_ListDir    : hand pfnEntry every entry of a directory, as BFE_ListDir  */
/*                  promises: all of them or, on a failure part-way, none.    */
/*                  Returns BFE_OK (also when the callback stopped early), or */
/*                  the failure's code.                                       */
/* BFT_VolumeInfo : fill a BFE_VolumeInfo for the mounted volume: clusters as */
/*                  its blocks, the cluster size as its block size, no inodes */
/*                  and no ext4 feature bits, its label; bReadOnly left for   */
/*                  the caller, which holds the session's mode.               */
/*                  Returns BFE_OK or the failure's code.                     */
/*                                                                            */
/* BFT_FatErrno     : a FatFs FRESULT (passed as an int, so this header needs */
/*                    no FatFs header) as the errno lwext4 gives for the same */
/*                    failure: 0, ENOENT, EEXIST, EINVAL, EROFS, ENOMEM, or   */
/*                    EIO for the rest.  The one such translation; bfe_fs.c's */
/*                    FAT arms use it too.                                    */
/* BFT_StatEntry    : BFX_FillEntry's FAT arm -- fill a zeroed, named         */
/*                    BFE_Entry with one path's facts, as a listing shows     */
/*                    them ("/" described without FatFs, which has no item    */
/*                    for the root).  Returns 0 or an errno (ENOENT: absent). */
/* BFT_CollectNames : BFX_CollectNames's FAT arm -- every name of a directory */
/*                    into a malloc'd array (freed with BFX_FreeNames), the   */
/*                    directory closed before returning; every name or, on a  */
/*                    failure, none.  Returns 0, ENOMEM, or an errno for      */
/*                    FatFs's refusal.                                        */
/*----------------------------------------------------------------------------*/
int BFT_Mount(uint32_t uiSectorBytes, const char *pszExtReason);
int BFT_Unmount(void);
int BFT_ListDir(const char *pszImageDir, BFE_ListCallback pfnEntry, void *pvContext);
int BFT_VolumeInfo(BFE_VolumeInfo *pInfo);
int BFT_FatErrno(int iResult);
int BFT_StatEntry(const char *pszImagePath, BFE_Entry *pEntry);
int BFT_CollectNames(const char *pszImageDir, char ***pppszNames, int *pnNames);

/*----------------------------------------------------------------------------*/
/* The write side, errno-style like the three above and recording nothing,    */
/* for bfe_fs.c's FAT arms:                                                   */
/* BFT_Unlink        : remove one file or empty directory (its read-only      */
/*                     attribute cleared first).                              */
/* BFT_RemoveTree    : remove a directory and all under it (FatFs has no      */
/*                     recursive remove).                                     */
/* BFT_SetModifyTime : set an entry's one FAT time from UTC epoch seconds,    */
/*                     in local time; the root's is a no-op.                  */
/*----------------------------------------------------------------------------*/
int BFT_Unlink(const char *pszImagePath);
int BFT_RemoveTree(const char *pszImageDir);
int BFT_SetModifyTime(const char *pszImagePath, uint32_t uiTime);

/* BFT_MaxFileBytes : the largest file the mounted volume holds -- 4 GiB     */
/*                    less one byte on FAT12 / 16 / 32, no limit on exFAT.   */
uint64_t BFT_MaxFileBytes(void);

/* BFT_PathFoldPrefix : whether a FAT path begins with another, character     */
/*                      by character with letter case folded by FatFs's own   */
/*                      table (ff_wtoupper), as FatFs matches names; on       */
/*                      TRUE, how many bytes of the first path matched --     */
/*                      a NUL there means the same path, a '/' that it lies   */
/*                      under the other.  Needs no volume mounted.            */
int BFT_PathFoldPrefix(const char *pszPath, const char *pszPrefix, size_t *pcbMatched);

/*-----------------------------------------------------------------------------*/
/* Making a FAT volume (bfe_mkfs.c's BFE_MakeFs and BFE_MakeFsAt, with no      */
/* session open), recording their failures with BFX_Fail:                      */
/* BFT_CheckMakeFs : whether a volume of the type -- BFE_FSTYPE_FAT16 /        */
/*                   FAT32 / EXFAT -- can be asked for with this cluster size  */
/*                   (0, or a power of two from one uiSectorBytes sector to    */
/*                   128 of them, or to 16 MiB on exFAT: FatFs would           */
/*                   otherwise silently choose its own) and this label         */
/*                   (UTF-8, by FatFs's own rules: on FAT12 / 16 / 32 the      */
/*                   characters code page 437 holds, stored in capitals; a     */
/*                   control character refused, not cut; "" for none).  Called */
/*                   before anything is written, so a refusal leaves the file  */
/*                   or range untouched.  Returns BFE_OK, or BFE_ERR_ARG with  */
/*                   the reason recorded.                                      */
/* BFT_MakeFs      : format the range BFD_Configure recorded as one FAT12 /    */
/*                   16, FAT32 or exFAT volume (no partition table of its      */
/*                   own), counting in uiSectorBytes-byte sectors, with        */
/*                   uiClusterBytes clusters (0: FatFs's choice), then give    */
/*                   it pszLabel -- both already passed by BFT_CheckMakeFs.    */
/*                   The target is opened by FatFs and closed again before     */
/*                   returning, on every path.  Returns BFE_OK; BFE_ERR_ARG    */
/*                   when FatFs cannot lay that type out in that size with     */
/*                   that cluster size (nothing of the volume written);        */
/*                   BFE_ERR_NO_MEMORY; BFE_ERR_IO_IMAGE when a write fails    */
/*                   (the range then holds a part-made volume).                */
/*-----------------------------------------------------------------------------*/
int BFT_CheckMakeFs(uint32_t uiFsType, uint32_t uiSectorBytes, uint32_t uiClusterBytes, const char *pszLabel);
int BFT_MakeFs(uint32_t uiFsType, uint32_t uiSectorBytes, uint32_t uiClusterBytes, const char *pszLabel);

/* The end of the include guard. */
#endif // BFE_FAT_H
