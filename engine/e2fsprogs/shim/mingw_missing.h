/*----------------------------------------------------------------------------*/
/* mingw_missing.h                                                            */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* What the mirrored e2fsprogs sources call without a feature test, that      */
/* neither MinGW nor upstream's own include/mingw stand-ins supply.  The      */
/* functions are defined in mingw_missing.c beside this file; this header is  */
/* pulled in by the shim's config.h, which every mirrored source includes     */
/* first, so the declarations are in scope wherever the calls are.  Each      */
/* definition is the least the caller can be given without changing its      */
/* behaviour -- see the comment on each in the source.                        */
/*----------------------------------------------------------------------------*/

/* Included once (config.h includes it, and config.h is itself included by */
/* every mirrored source).                                                   */
#ifndef BFE_E2FSPROGS_MINGW_MISSING_H_
#define BFE_E2FSPROGS_MINGW_MISSING_H_

/* intptr_t, and size_t, for the two prototypes. */
#include <stdint.h>
#include <stddef.h>

/*----------------------------------------------------------------------------*/
/* sbrk    : the Unix "current end of the data segment" probe e2fsck's        */
/*           resource tracking reads at the start of each pass and, when its  */
/*           timing report is on (never, in the engine), again at the end to  */
/*           report heap growth.  Windows has no brk; the stand-in answers    */
/*           NULL every time, so the difference would read as zero.          */
/* qsort_r : glibc's sort with a context argument for the comparator, which  */
/*           e2fsck's rehash.c reaches through support/sort_r.h -- that       */
/*           header picks the glibc spelling whenever __MINGW32__ is defined, */
/*           though MinGW's C runtime has only Microsoft's qsort_s.  The      */
/*           stand-in is qsort_r in glibc's signature over qsort_s.           */
/*----------------------------------------------------------------------------*/
void *sbrk(intptr_t increment);
void  qsort_r(void *base, size_t nel, size_t width,
              int (*compar)(const void *, const void *, void *), void *arg);

/* The end of the once-only guard. */
#endif /* BFE_E2FSPROGS_MINGW_MISSING_H_ */
