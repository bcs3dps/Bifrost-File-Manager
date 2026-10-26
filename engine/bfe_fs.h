/*----------------------------------------------------------------------------*/
/* bfe_fs.h                                                                   */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The engine's filesystem-neutral file calls: one function per file-level    */
/* operation the session code makes, each a switch on the open session's      */
/* kind (BFX_FsKind) that calls lwext4 for ext2/3/4, FatFs for FAT or exFAT   */
/* and squashfuse for squashfs.  The session code (bifrost_engine.c,          */
/* bfe_copy.c) calls these instead of lwext4's own file calls, so a walk      */
/* written once serves every kind.                                            */
/*                                                                            */
/* Every function returns lwext4's errno-style result -- 0, or ENOENT,        */
/* EEXIST, ENOSPC and the rest -- whichever library did the work, so a        */
/* caller maps it with BFX_MapErrno whichever kind the session is.  The       */
/* ext2/3/4 arm of each is the lwext4 call itself, its result passed on.      */
/*                                                                            */
/* On a FAT session the reads and writes are FatFs's; the mode, owner and     */
/* link calls are ENOTSUP (FAT has none), and the access and change times     */
/* succeed doing nothing (FAT keeps only a modify time).  On a squashfs       */
/* session the reads and the link read are squashfuse's (bfe_sqfs.c), and     */
/* every change is EROFS -- squashfs cannot be written.                       */
/*                                                                            */
/* Internal to the engine: nothing here is exported from the DLL.  Every      */
/* function is called with the engine's busy flag held.                       */
/*----------------------------------------------------------------------------*/

/* Include guard: the file is read once however many files include it. */
#ifndef BFE_FS_H
#define BFE_FS_H

/* Fixed-width integers and size_t. */
#include <stdint.h>
#include <stddef.h>

/* An open image file, of any kind, named but not defined: bfe_fs.c    */
/* holds what is inside, so the session files need no library's file   */
/* structure to hold one.                                              */
typedef struct BFS_File BFS_File;

/*----------------------------------------------------------------------------*/
/* BFS_FileOpen  : open an image file -- "rb" to read, "wb" to create or      */
/*                 truncate for writing -- into a new handle in *ppFile       */
/*                 (NULL on failure).  ENOMEM when the handle cannot be made. */
/* BFS_FileRead  : read up to cbWant bytes from the handle's position;        */
/*                 *pcbRead is 0 at the end of the file.                      */
/* BFS_FileWrite : write cbWant bytes at the handle's position; *pcbWritten   */
/*                 says how many went in.                                     */
/* BFS_FileClose : close the handle and free it (NULL is allowed).            */
/* BFS_Remove    : remove one file or link (lwext4's ext4_fremove).           */
/* BFS_DirMake   : create one directory (ext4_dir_mk).                        */
/* BFS_DirRemove : remove a directory and all under it (ext4_dir_rm).         */
/* BFS_Rename    : rename or move an entry (ext4_frename).                    */
/* BFS_SetMode   : set the 12 permission bits (ext4_chmod).                   */
/* BFS_SetOwner  : set the owner and group (ext4_chown).                      */
/* BFS_SetAtime  : set the access, modify or inode-change time, epoch         */
/* BFS_SetMtime    seconds (ext4_file_set_atime / mtime / ctime).             */
/* BFS_SetCtime                                                               */
/* BFS_ReadLink  : copy a link's target, unterminated, into the buffer        */
/*                 (ext4_readlink).                                           */
/* BFS_SymLink   : create a symbolic link (ext4_fsymlink).                    */
/* BFS_HardLink  : give a file a second name (ext4_flink).                    */
/*----------------------------------------------------------------------------*/
int BFS_FileOpen(const char *pszImagePath, const char *pszMode, BFS_File **ppFile);
int BFS_FileRead(BFS_File *pFile, void *pvBuffer, size_t cbWant, size_t *pcbRead);
int BFS_FileWrite(BFS_File *pFile, const void *pvBuffer, size_t cbWant, size_t *pcbWritten);
int BFS_FileClose(BFS_File *pFile);
int BFS_Remove(const char *pszImagePath);
int BFS_DirMake(const char *pszImageDir);
int BFS_DirRemove(const char *pszImageDir);
int BFS_Rename(const char *pszImageFrom, const char *pszImageTo);
int BFS_SetMode(const char *pszImagePath, uint32_t uiMode);
int BFS_SetOwner(const char *pszImagePath, uint32_t uiUid, uint32_t uiGid);
int BFS_SetAtime(const char *pszImagePath, uint32_t uiTime);
int BFS_SetMtime(const char *pszImagePath, uint32_t uiTime);
int BFS_SetCtime(const char *pszImagePath, uint32_t uiTime);
int BFS_ReadLink(const char *pszImagePath, char *pszBuffer, size_t cbBuffer, size_t *pcbRead);
int BFS_SymLink(const char *pszTarget, const char *pszImageLink);
int BFS_HardLink(const char *pszImageExisting, const char *pszImageNewName);

/* The end of the include guard. */
#endif  // BFE_FS_H
