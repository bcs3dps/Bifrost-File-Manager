/*----------------------------------------------------------------------------*/
/* mingw_missing.c                                                            */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The functions mingw_missing.h declares: Unix calls the mirrored e2fsprogs  */
/* sources make without a feature test and MinGW does not have.  Each is the  */
/* smallest honest answer -- never a fake that could change what e2fsck       */
/* decides about a filesystem, only what it reports about itself.             */
/*----------------------------------------------------------------------------*/

/* size_t, intptr_t and NULL, and the C runtime's qsort_s (stdlib.h). */
#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>

/* The prototypes these definitions must match. */
#include "mingw_missing.h"

/*----------------------------------------------------------------------------*/
/* The comparator and its context, carried through qsort_s as one pointer so  */
/* the thunk below can call the caller's comparator in glibc's argument       */
/* order.  Lives on qsort_r's stack for the duration of the sort: qsort_s     */
/* does not keep the pointer.                                                 */
/*----------------------------------------------------------------------------*/
typedef struct MGM_SortContext
{
    int (*pfnCompare)(const void *, const void *, void *);   // the caller's comparator, glibc order: a, b, context
    void *pvArg;                                             // the caller's context
} MGM_SortContext;

/*----------------------------------------------------------------------------*/
/* MGM_SortThunk:                                                             */
/*                                                                            */
/* The comparator qsort_s calls.  Microsoft's qsort_s passes the context      */
/* FIRST and the two elements after it, the mirror image of glibc's qsort_r,  */
/* so this turns the one order into the other and nothing else.               */
/*                                                                            */
/* Arguments:                                                                 */
/*     pvContext : the MGM_SortContext qsort_r handed to qsort_s.             */
/*     pvA       : the first element.                                         */
/*     pvB       : the second element.                                        */
/*                                                                            */
/* Returns:                                                                   */
/*     int : the caller's comparator's answer, negative / zero / positive.    */
/*----------------------------------------------------------------------------*/
static int MGM_SortThunk(void *pvContext, const void *pvA, const void *pvB)
{
    MGM_SortContext *pSort;                          // the comparator and context qsort_r packed

    /* qsort_s hands back exactly the pointer qsort_r gave it. */
    pSort = (MGM_SortContext *)pvContext;

    /* The caller's comparator, in its own argument order. */
    return(pSort->pfnCompare(pvA, pvB, pSort->pvArg));
}

/*----------------------------------------------------------------------------*/
/* qsort_r:                                                                   */
/*                                                                            */
/* Stand in for glibc's qsort_r: sort nel elements of width bytes at base    */
/* with a comparator that takes a context.  Implemented over the C runtime's */
/* qsort_s, which offers the same thing in Microsoft's argument order; the   */
/* thunk above bridges the two.  Reentrant: the context travels on this      */
/* function's stack, not in a static.  Used by e2fsck's rehash.c to order a   */
/* directory's entries by hash when it rebuilds an index.                    */
/*                                                                            */
/* Arguments:                                                                 */
/*     base   : the array.                                                    */
/*     nel    : how many elements.                                            */
/*     width  : bytes per element.                                            */
/*     compar : the comparator, called as compar(a, b, arg).                  */
/*     arg    : passed through to every comparator call.                      */
/*                                                                            */
/* Returns:                                                                   */
/*     void : the array is sorted in place.                                   */
/*----------------------------------------------------------------------------*/
void qsort_r(void *base, size_t nel, size_t width,
             int (*compar)(const void *, const void *, void *), void *arg)
{
    MGM_SortContext sort;                            // the comparator and its context, for the thunk

    /* Packed into one structure, since qsort_s carries a single context  */
    /* pointer; it lives on this stack until the sort returns.            */
    sort.pfnCompare = compar;
    sort.pvArg      = arg;

    /* The runtime's sort, with the thunk as its comparator.  Microsoft's */
    /* qsort_s returns nothing, like glibc's qsort_r (MinGW's stdlib.h    */
    /* declares it void), so there is no result to pass on.               */
    qsort_s(base, nel, width, MGM_SortThunk, &sort);
}

/*----------------------------------------------------------------------------*/
/* sbrk:                                                                      */
/*                                                                            */
/* Stand in for the Unix data-segment probe.  e2fsck calls sbrk(0) at the    */
/* start and end of each pass (util.c, init_resource_track and                */
/* print_resource_track) and prints the difference as the pass's heap growth  */
/* when the -t timing report is on.  Windows has no brk and the figure is     */
/* only ever printed, so both calls get NULL and the growth reads as zero.    */
/* The report is off in the engine's runs anyway (print_resource_track        */
/* returns at once unless E2F_OPT_TIME or E2F_OPT_TIME2 is set, and           */
/* bfe_fsck.c sets neither -- checked).                                       */
/*                                                                            */
/* Arguments:                                                                 */
/*     increment : ignored (e2fsck only ever asks for 0, the probe).          */
/*                                                                            */
/* Returns:                                                                   */
/*     void * : NULL, always.                                                 */
/*----------------------------------------------------------------------------*/
void *sbrk(intptr_t increment)
{
    intptr_t unused;                                 // a dummy, so the ignored argument counts as used

    /* The argument is the Unix "grow by this much"; a dummy assignment keeps  */
    /* it "used" without a (void) cast.                                        */
    unused = increment;
    unused = unused;

    /* No data segment to report. */
    return(NULL);
}
