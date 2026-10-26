/*----------------------------------------------------------------------------*/
/* config.h (squashfuse's build configuration)                                */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* What squashfuse's configure script would have written for this engine:     */
/* which decompressors to build and which system headers exist.  squashfuse   */
/* includes it as "config.h" from common.h and cache.c, so it is found on     */
/* the include path build_engine.bat gives the squashfuse files alone --      */
/* never the path of liblzma's own config.h, which shares the name.           */
/*                                                                            */
/* Only the defines squashfuse's reader looks at are set; its FUSE, threading */
/* and extended-attribute-to-host settings concern programs this engine does  */
/* not build.  lz4 and LZO are not built in: an image made with either is     */
/* refused when opened (sqfs_decompressor_get answers NULL).                  */
/*----------------------------------------------------------------------------*/

/* Included once. */
#ifndef SQFS_CONFIG_H
#define SQFS_CONFIG_H

/*----------------------------------------------------------------------------*/
/* Decompressors and system headers -- documentation table.                   */
/*----------------------------------------------------------------------------*/
#define HAVE_ZLIB_H     1    // zlib 1.3.1 is built into the engine: squashfs "gzip" blocks (BifrostFM squashfuse patch 1 keeps the Windows port's own tinfl out)
#define HAVE_LZMA_H     1    // liblzma (xz 5.8.1) is built into the engine: squashfs "xz" and "lzma" blocks
#define HAVE_ZSTD_H     1    // zstd 1.5.7 is built into the engine: squashfs "zstd" blocks
#define HAVE_STDINT_H   1    // MinGW has <stdint.h>
#define HAVE_STDLIB_H   1    // MinGW has <stdlib.h>
#define HAVE_STRING_H   1    // MinGW has <string.h>
#define HAVE_SYS_STAT_H 1    // MinGW has <sys/stat.h>
#define HAVE_SYS_TYPES_H 1   // MinGW has <sys/types.h>

/* The end of the include guard. */
#endif // SQFS_CONFIG_H
