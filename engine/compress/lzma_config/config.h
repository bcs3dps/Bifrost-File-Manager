/*----------------------------------------------------------------------------*/
/* config.h -- liblzma's build configuration for BifrostFM                    */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* liblzma (xz-5.8.1, mirrored under ..\xz-5.8.1 with only the files this     */
/* build compiles) normally gets this file from its configure script.  This   */
/* one is written by hand: .xz streams with the LZMA2 filter, read and        */
/* written; the BCJ filters (x86, PowerPC, IA-64, ARM, ARM-Thumb, ARM64,      */
/* SPARC, RISC-V) READ only -- an .xz image made with one, and a squashfs     */
/* "xz" block, which mksquashfs's -Xbcj option writes with one before         */
/* LZMA2; the LZMA1 decoder alone also serves a squashfs "lzma" block (the    */
/* old .lzma format); the CRC32, CRC64 and SHA-256 integrity checks (a file   */
/* may carry any of the three); the multi-threaded encoder on Windows         */
/* threads.  No delta filter is built, and no BCJ encoder: a .xz using delta  */
/* is refused by the decoder with LZMA_OPTIONS_ERROR, and the .xz writer      */
/* uses LZMA2 alone.  When the file was first written, a link test of the     */
/* LZMA2 file set and this configuration encoded and decoded a round trip     */
/* (as found; that test is not in this tree).  There is no include guard: the */
/* one include is sysdefs.h's, under HAVE_CONFIG_H (which build_engine.bat    */
/* defines), inside sysdefs.h's own guard -- so once per source file.         */
/*----------------------------------------------------------------------------*/

#define HAVE_ENCODER_LZMA1 1                   // LZMA1 encoder: LZMA2 is built on it
#define HAVE_ENCODER_LZMA2 1                   // LZMA2 encoder: the .xz writer's filter
#define HAVE_DECODER_LZMA1 1                   // LZMA1 decoder: LZMA2 is built on it
#define HAVE_DECODER_LZMA2 1                   // LZMA2 decoder: the .xz reader's filter
#define HAVE_DECODER_X86 1                     // BCJ decoder for x86 code (simple\x86.c), ahead of LZMA2 in a stream that uses it
#define HAVE_DECODER_POWERPC 1                 // BCJ decoder for PowerPC (big-endian) code (simple\powerpc.c)
#define HAVE_DECODER_IA64 1                    // BCJ decoder for IA-64 (Itanium) code (simple\ia64.c)
#define HAVE_DECODER_ARM 1                     // BCJ decoder for 32-bit ARM code (simple\arm.c)
#define HAVE_DECODER_ARMTHUMB 1                // BCJ decoder for ARM-Thumb code (simple\armthumb.c)
#define HAVE_DECODER_ARM64 1                   // BCJ decoder for ARM64 code (simple\arm64.c)
#define HAVE_DECODER_SPARC 1                   // BCJ decoder for SPARC code (simple\sparc.c)
#define HAVE_DECODER_RISCV 1                   // BCJ decoder for RISC-V code (simple\riscv.c)
#define HAVE_CHECK_CRC32 1                     // a .xz's integrity check may be CRC32
#define HAVE_CHECK_CRC64 1                     // ... CRC64 (xz's default)
#define HAVE_CHECK_SHA256 1                    // ... or SHA-256
#define HAVE_MF_HC3 1                          // match finder: hash chain, 3 bytes -- preset level 0 (lzma_encoder_presets.c)
#define HAVE_MF_HC4 1                          // match finder: hash chain, 4 bytes -- preset levels 1 to 3
#define HAVE_MF_BT2 1                          // match finder: binary tree, 2 bytes -- no preset uses it; why it is built is not recorded
#define HAVE_MF_BT3 1                          // match finder: binary tree, 3 bytes -- no preset uses it either
#define HAVE_MF_BT4 1                          // match finder: binary tree, 4 bytes -- preset levels 4 to 9
#define MYTHREAD_VISTA 1                       // threads and condition variables from Windows Vista on (mythread.h)
#define TUKLIB_PHYSMEM_WINDOWS 1               // physical memory from GlobalMemoryStatusEx
#define TUKLIB_CPUCORES_SYSCONF 1              // unused on Windows (tuklib_cpucores.c tests _WIN32 first and asks GetSystemInfo)
#define HAVE_STDBOOL_H 1                       // <stdbool.h> is there (sysdefs.h includes it)
#define HAVE__BOOL 1                           // and the _Bool type (sysdefs.h's fallback for a missing stdbool.h)
#define HAVE_STDINT_H 1                        // <stdint.h> is there
#define HAVE_INTTYPES_H 1                      // <inttypes.h> is there
#define HAVE_VISIBILITY 0                      // read only off Windows: common.h takes its _WIN32 branch first, which exports nothing unless DLL_EXPORT (never defined here) -- liblzma stays inside the engine DLL
#define SIZEOF_SIZE_T 8                        // x64 only, as the engine is; sysdefs.h reads it only to define SIZE_MAX when no header has (MinGW's stdint.h does)
#define ASSUME_RAM 128                         // read by nothing compiled here (no mirrored xz file names it)
#define PACKAGE_NAME "XZ Utils"                // read by nothing compiled here (no compiled liblzma source names it)
#define PACKAGE_URL "https://tukaani.org/xz/"  // read by nothing compiled here either
