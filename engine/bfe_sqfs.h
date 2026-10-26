/*----------------------------------------------------------------------------*/
/* bfe_sqfs.h                                                                 */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The engine's squashfs session: a squashfs 4.0 filesystem, found by its     */
/* magic where lwext4 mounts nothing, read through squashfuse's reader        */
/* (engine\squashfuse) -- and the session calls that answer for it.           */
/* bifrost_engine.c decides which filesystem a session holds and sends the    */
/* call here, as it does to bfe_fat.c for FAT; bfe_fs.c's file calls reach    */
/* the file handles below.  Beneath squashfuse is bfe_sqfs.c's sqfs_pread,    */
/* which reads through the engine's block device and never past the range.    */
/*                                                                            */
/* squashfs is read-only by design -- a compressed image, made whole by       */
/* mksquashfs -- so a session of it is read-only: a writable open is refused  */
/* (BFE_ERR_READ_ONLY), and every change is refused at the gate by the        */
/* kind's traits.  It keeps Unix facts (modes, owners, links), so an extract  */
/* writes the manifest as on ext2/3/4.                                        */
/*                                                                            */
/* Every function below is called with the engine's busy flag HELD.           */
/* BFQ_Mount records its own failure with BFX_Fail before returning; the      */
/* errno-style ones record nothing, as the lwext4 code they stand beside      */
/* records nothing: their callers decide what a failure means.                */
/*                                                                            */
/* Internal to the engine: nothing here is exported from the DLL.             */
/*----------------------------------------------------------------------------*/

/* Included once. */
#ifndef BFQ_BFE_SQFS_H
#define BFQ_BFE_SQFS_H

/* Fixed-width integers, size_t, bool (BFQ_Mount's flags), and the public   */
/* interface for BFE_Entry, BFE_VolumeInfo and the status codes.            */
#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>
#include "bifrost_engine.h"

/* An open squashfs file, named but not defined: bfe_sqfs.c holds what is   */
/* inside, so bfe_fs.c can hold one without squashfuse's headers.           */
typedef struct BFQ_strctFile BFQ_strctFile;

/* The session's calls, in the order a session meets them -- documentation table. */
int  BFQ_Mount(bool bReadOnly, bool *pbFound);                                                   // mount the range BFD_Configure recorded when it holds squashfs: *pbFound false (and BFE_OK, nothing open, nothing recorded) when its first bytes are not a squashfs magic; with *pbFound true, BFE_OK mounted, or the failure recorded and nothing left open (BFE_ERR_READ_ONLY for a writable open)
void BFQ_Unmount(void);                                                                          // let the filesystem go and close the target; safe when nothing is mounted
int  BFQ_VolumeInfo(BFE_VolumeInfo *pInfo);                                                      // fill a BFE_VolumeInfo: BFE_VOLTYPE_SQUASHFS, the block size and the blocks the image uses, the inode count, nothing free, no label, read-only; BFE_OK
int  BFQ_StatEntry(const char *pszImagePath, BFE_Entry *pEntry);                                 // BFX_FillEntry's squashfs arm: fill a zeroed, named BFE_Entry with one path's inode facts; 0, ENOENT, ENOTDIR, EIO
int  BFQ_CollectNames(const char *pszImageDir, char ***pppszNames, int *pnNames);                // BFX_CollectNames's squashfs arm: every name of a directory into a malloc'd array (freed with BFX_FreeNames), every name or none; 0, ENOENT, ENOTDIR, ENOMEM, EIO
int  BFQ_ReadLink(const char *pszImagePath, char *pszBuffer, size_t cbBuffer, size_t *pcbRead);  // a link's target, unterminated, up to cbBuffer bytes, as lwext4's ext4_readlink gives it; 0, ENOENT, EINVAL (not a link), ENOMEM, EIO
int  BFQ_FileOpen(const char *pszImagePath, BFQ_strctFile **ppFile);                             // a regular file opened for reading at its start, into a new handle (NULL on failure); 0, ENOENT, EISDIR, EINVAL (not a regular file), ENOMEM, EIO
int  BFQ_FileRead(BFQ_strctFile *pFile, void *pvBuffer, size_t cbWant, size_t *pcbRead);         // up to cbWant bytes from the handle's position, which moves on; *pcbRead 0 at the file's end; 0 or EIO
void BFQ_FileClose(BFQ_strctFile *pFile);                                                        // free the handle (NULL allowed); a read handle holds nothing to write back

/* The end of the include guard. */
#endif // BFQ_BFE_SQFS_H
