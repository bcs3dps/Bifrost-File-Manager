/*----------------------------------------------------------------------------*/
/* bfe_fsck_io.h                                                              */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The engine's e2fsprogs io manager: libext2fs' way of reaching a disk,      */
/* implemented over the engine's own block-device target (bfe_blockdev.c), so */
/* e2fsck built into the DLL reads and writes exactly what lwext4 does -- an  */
/* image file, a real device, or a partition inside either -- with the same   */
/* sector rules and the same read-only refusal, and never opens a path of its */
/* own.  Internal to the engine: nothing here is exported from the DLL.       */
/*----------------------------------------------------------------------------*/

/* Included once. */
#ifndef BFE_FSCK_IO_H
#define BFE_FSCK_IO_H

/* e2fsprogs' build configuration first, as its headers require; then the   */
/* io_manager type (which also brings the fixed-width integers).            */
#include "config.h"
#include <ext2fs/ext2_io.h>

/*----------------------------------------------------------------------------*/
/* BFIO_Manager : the io manager to hand ext2fs_open2 (and get_backup_sb).    */
/*                Its open ignores the name it is given and opens what        */
/*                BFD_Configure recorded; several channels may be open on it  */
/*                at once (e2fsck opens a second one to look for a backup     */
/*                superblock), all on the one target, which is opened by the  */
/*                first and closed by the last.  It never returns NULL; its   */
/*                open refuses a writable channel on a read-only              */
/*                configuration -- the guarantee a report-only check relies   */
/*                on.  Every transfer is held to the range: a read past its   */
/*                end comes back short, and a write reaching past it is       */
/*                refused whole, before anything is written -- so no writer   */
/*                above, a journal replay included, can reach outside it.     */
/*----------------------------------------------------------------------------*/
io_manager BFIO_Manager(void);

/*----------------------------------------------------------------------------*/
/* BFIO_Length : the filesystem range's length in bytes, as the target        */
/*               reported it when the first channel opened; 0 while nothing   */
/*               is open.  e2fsck wants the device size, which unix.c asks    */
/*               the OS for; the engine already has it here.  Its callers --  */
/*               bfe_fsck.c (the check's device size) and bfe_resize.c (the   */
/*               size to grow to) -- must ask while a channel is OPEN, i.e.   */
/*               after ext2fs_open2 and before the close: a 0 from a closed   */
/*               target would read as an empty device.                        */
/*----------------------------------------------------------------------------*/
uint64_t BFIO_Length(void);

/* The end of the include guard. */
#endif // BFE_FSCK_IO_H
