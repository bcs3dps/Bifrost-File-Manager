/*----------------------------------------------------------------------------*/
/* win32.h (squashfuse's Windows types)                                       */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The types squashfuse's common.h takes from <win32.h> on Windows, written   */
/* for this engine in place of squashfuse's own win/win32.h, which is not     */
/* built.  Two reasons: its handle type is a Windows HANDLE read through      */
/* ReadFile, where this engine reads through its own block device (an image   */
/* file or a card, a byte range of either, sector rules met underneath); and  */
/* it defines the file-type macros again over MinGW's own, so every file      */
/* would warn.                                                                */
/*                                                                            */
/* So the handle is a pointer to the engine's reader context (bfe_sqfs.c      */
/* holds what is inside, and defines sqfs_pread over it), and the file-type   */
/* macros MinGW lacks are added only where MinGW has none.  MinGW's own       */
/* S_IFBLK is not POSIX's (0x3000, not 0x6000): the engine never reads an     */
/* entry's type from squashfuse's mode words for that reason, only from the   */
/* inode's own type field (bfe_sqfs.c).                                       */
/*----------------------------------------------------------------------------*/

/* Included once. */
#ifndef SQFS_WIN32_H
#define SQFS_WIN32_H

/* Fixed-width integers; ssize_t and dev_t (MinGW's sys/types.h); the   */
/* file-type macros MinGW defines (sys/stat.h).                         */
#include <stdint.h>
#include <sys/types.h>
#include <sys/stat.h>

/*----------------------------------------------------------------------------*/
/* File-type macros MinGW lacks -- documentation table (POSIX's values).      */
/*----------------------------------------------------------------------------*/
#ifndef S_IFLNK
#define S_IFLNK  0120000                       // a symbolic link
#endif
#ifndef S_IFSOCK
#define S_IFSOCK 0140000                       // a unix socket
#endif
#ifndef S_ISLNK
#define S_ISLNK(m) (((m) & S_IFMT) == S_IFLNK) // true for a symbolic link's mode
#endif

/*----------------------------------------------------------------------------*/
/* squashfuse's portable types -- documentation table.                        */
/*----------------------------------------------------------------------------*/
typedef unsigned short            sqfs_mode_t;  // a mode word (type and permission bits), as squashfuse's own Windows port has it
typedef uint32_t                  sqfs_id_t;    // a user or group id: squashfs keeps 32 bits
typedef uint64_t                  sqfs_off_t;   // a byte position in the filesystem, from its start
typedef struct BFQ_strctSource   *sqfs_fd_t;    // the engine's reader context (bfe_sqfs.c); sqfs_pread reads through it

/* The end of the include guard. */
#endif // SQFS_WIN32_H
