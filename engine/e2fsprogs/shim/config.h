/*----------------------------------------------------------------------------*/
/* config.h                                                                   */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The stand-in for e2fsprogs' generated lib/config.h, for the libext2fs,     */
/* libcom_err, libe2p, libuuid, libblkid, libsupport and e2fsck sources       */
/* mirrored verbatim from e2fsprogs 1.47.4 (the folders beside this one).    */
/* Every one of those files includes "config.h" first; this file sits on the  */
/* include path ahead of them so it is the one found.                         */
/*                                                                            */
/* WHERE THE VALUES COME FROM: they are the lib/config.h e2fsprogs' own       */
/* configure script writes for the WinLibs MinGW-w64 gcc this engine is       */
/* built with (configured with --disable-nls): its 70 defines, minus the      */
/* seven listed below -- one of which, TLS, is then defined differently --    */
/* so 64 here.  When the header was made, upstream's own makefiles built all  */
/* six libraries clean with it (as found; that build is not in this tree).    */
/* The symbols configure left #undef'd are simply absent here: a feature      */
/* test that failed defines nothing, and the sources test them with #ifdef.   */
/*                                                                            */
/* DELIBERATELY NOT DEFINED, though configure defined them:                   */
/*   CONFIG_TESTIO_DEBUG   -- test_io.c is not compiled (it is a debugging    */
/*                            io manager unix.c selects by environment;       */
/*                            build_engine.bat leaves both out).              */
/*   HAVE_DLOPEN           -- plausible.c, the one user, is not compiled      */
/*                            (build_engine.bat).                             */
/*   HAVE_PTHREAD, HAVE_PTHREAD_H, HAVE_PTHREAD_PRIO_INHERIT, HAVE_SEMAPHORE_H */
/*                         -- the engine's io manager is single-threaded and  */
/*                            serialised by the engine, so rw_bitmaps.c's     */
/*                            threaded bitmap reads and com_err's semaphore   */
/*                            lock are compiled out; EXT2_FLAG_THREADS is     */
/*                            never passed to ext2fs_open2 for the same       */
/*                            reason.                                         */
/*   TLS thread_local      -- replaced by gcc's __thread below, which does    */
/*                            not depend on the -std level the file is        */
/*                            compiled at (thread_local is a C23 keyword).    */
/*----------------------------------------------------------------------------*/

/* Included once, under a guard name of its own (not e2fsprogs' real one). */
#ifndef BFE_E2FSPROGS_CONFIG_H_
#define BFE_E2FSPROGS_CONFIG_H_

/*----------------------------------------------------------------------------*/
/* Build options -- documentation table.                                      */
/*----------------------------------------------------------------------------*/
#define CONFIG_BUILD_FINDFS     1   // configure's default; names a findfs tool this tree does not build (unused by the sources compiled here)
#define CONFIG_MMP              1   // multi-mount protection support in libext2fs (ext2fs.h, mmp.c); the check opens with EXT2_FLAG_SKIP_MMP but the code must exist
#define DISABLE_BACKTRACE       1   // no glibc backtrace() in a crash handler (sigcatcher.c, which is not compiled anyway)
#define ENABLE_BMAP_STATS       1   // configure's default: the 64-bit bitmap code keeps hit / miss counters (gen_bitmap64.c, blkmap64_*.c)

/*----------------------------------------------------------------------------*/
/* Headers present on this compiler -- documentation table.  Each HAVE_x_H    */
/* turns on an #include of that header in the sources.                        */
/*----------------------------------------------------------------------------*/
#define HAVE_DIRENT_H           1   // <dirent.h>: MinGW has it
#define HAVE_ERRNO_H            1   // <errno.h>
#define HAVE_GETOPT_H           1   // <getopt.h>: MinGW has it (only unix.c, not compiled, uses getopt)
#define HAVE_INTTYPES_H         1   // <inttypes.h>
#define HAVE_LINUX_TYPES_H      1   // <linux/types.h>: satisfied by upstream's own include/mingw/linux/types.h, which is on the include path as it is in upstream's MinGW build
#define HAVE_MALLOC_H           1   // <malloc.h>
#define HAVE_SETJMP_H           1   // <setjmp.h>: e2fsck's abort path longjmps out of a fatal error instead of exiting -- essential inside a DLL
#define HAVE_SIGNAL_H           1   // <signal.h> (only unix.c and sigcatcher.c act on it; neither is compiled)
#define HAVE_STDARG_H           1   // <stdarg.h>
#define HAVE_STDINT_H           1   // <stdint.h>
#define HAVE_STDIO_H            1   // <stdio.h>
#define HAVE_STDLIB_H           1   // <stdlib.h>
#define HAVE_STRINGS_H          1   // <strings.h>: MinGW has it
#define HAVE_STRING_H           1   // <string.h>
#define HAVE_SYS_FILE_H         1   // <sys/file.h>: MinGW has it (gen_uuid.c)
#define HAVE_SYS_STAT_H         1   // <sys/stat.h>: through upstream's include/mingw/sys/stat.h, which adds lstat and S_ISLNK
#define HAVE_SYS_SYSMACROS_H    1   // <sys/sysmacros.h>: upstream's include/mingw copy, giving makedev
#define HAVE_SYS_TIME_H         1   // <sys/time.h>: MinGW has it (gettimeofday)
#define HAVE_SYS_TYPES_H        1   // <sys/types.h>: through upstream's include/mingw copy, which adds __uid_t / __gid_t
#define HAVE_UNISTD_H           1   // <unistd.h>: through upstream's include/mingw copy, which stubs getuid and friends
#define HAVE_UTIME_H            1   // <utime.h>: MinGW has it (tdb.c only, not compiled)
#define HAVE_WCHAR_H            1   // <wchar.h>
#define STDC_HEADERS            1   // the ANSI C headers are all present

/*----------------------------------------------------------------------------*/
/* Functions and types present -- documentation table.                        */
/*----------------------------------------------------------------------------*/
#define HAVE_FSTAT64            1   // fstat64(): the large-file stat the io managers use
#define HAVE_FTRUNCATE64        1   // ftruncate64()
#define HAVE_GETCWD             1   // getcwd()
#define HAVE_INTPTR_T           1   // intptr_t exists (jfs_user.h, pass1b.c)
#define HAVE_LSEEK64            1   // lseek64() (llseek.c)
#define HAVE_LSEEK64_PROTOTYPE  1   // and the headers declare it
#define HAVE_MBSTOWCS           1   // mbstowcs()
#define HAVE_MEMPCPY            1   // mempcpy()
#define HAVE_NANOSLEEP          1   // nanosleep()
#define HAVE_OPEN64             1   // open64(): the large-file open the io managers use
#define HAVE_RECLEN_DIRENT      1   // struct dirent has d_reclen (e2p/iod.c)
#define HAVE_SNPRINTF           1   // snprintf()
#define HAVE_STRCASECMP         1   // strcasecmp()
#define HAVE_STRDUP             1   // strdup()
#define HAVE_STRNLEN            1   // strnlen(): without it e2fsck.h substitutes its own
#define HAVE_STRTOULL           1   // strtoull()
#define HAVE_TYPE_SSIZE_T       1   // ssize_t exists
#define HAVE_USLEEP             1   // usleep()
#define HAVE_UTIME              1   // utime()
#define HAVE_VPRINTF            1   // vprintf(): without it com_err compiles its own vfprintf

/*----------------------------------------------------------------------------*/
/* Sizes measured by configure on this compiler -- documentation table.       */
/* ext2_types.h (in shim\ext2fs) was generated from the same measurements.    */
/*----------------------------------------------------------------------------*/
#define SIZEOF_INT              4   // int is 32 bits
#define SIZEOF_LONG             4   // long is 32 bits on 64-bit Windows (LLP64), unlike Linux
#define SIZEOF_LONG_LONG        8   // long long is 64 bits
#define SIZEOF_OFF_T            8   // off_t is 64 bits with _FILE_OFFSET_BITS 64 below
#define SIZEOF_SHORT            2   // short is 16 bits
#define SIZEOF_TIME_T           8   // time_t is 64 bits
#define SIZEOF_VOID_P           8   // pointers are 64 bits

/*----------------------------------------------------------------------------*/
/* Package identity -- documentation table, as configure wrote it (the        */
/* strings are autoconf's; nothing compiled here reads them).                 */
/*----------------------------------------------------------------------------*/
#define PACKAGE                 "e2fsprogs"  // autoconf package name
#define PACKAGE_BUGREPORT       ""           // autoconf boilerplate, empty in this release
#define PACKAGE_NAME            ""           // autoconf boilerplate, empty in this release
#define PACKAGE_STRING          ""           // autoconf boilerplate, empty in this release
#define PACKAGE_TARNAME         ""           // autoconf boilerplate, empty in this release
#define PACKAGE_URL             ""           // autoconf boilerplate, empty in this release
#define PACKAGE_VERSION         ""           // autoconf boilerplate, empty in this release
#define VERSION                 "0.14.1"     // as configure wrote it (an autoconf default, not e2fsprogs' version -- that is version.h)

/*----------------------------------------------------------------------------*/
/* The two that change how the compiler reads the sources.                    */
/*----------------------------------------------------------------------------*/
#define _FILE_OFFSET_BITS       64           // 64-bit off_t throughout: an image or card can exceed 2 GiB
#define TLS                     __thread     // thread-local storage for com_err's and libuuid's per-thread state (gcc's keyword; configure chose C23's thread_local, see the header)

/* What the sources call without a feature test and MinGW lacks (sbrk,      */
/* qsort_r): declared here, because this header is the one every mirrored   */
/* source includes first, and defined in mingw_missing.c.                   */
#include "mingw_missing.h"

/* The end of the once-only guard. */
#endif /* BFE_E2FSPROGS_CONFIG_H_ */
