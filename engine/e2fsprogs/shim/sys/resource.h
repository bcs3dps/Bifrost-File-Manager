/*----------------------------------------------------------------------------*/
/* sys/resource.h                                                             */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* A stand-in for the POSIX <sys/resource.h>, which MinGW does not have.      */
/* e2fsck's util.c and ehandler.c include it unconditionally; the one thing   */
/* they would take from it, getrusage() for the per-pass resource report, is  */
/* already compiled out because HAVE_GETRUSAGE is not defined (config.h).     */
/* Upstream's include/nonunix has the same empty file; include/mingw, which   */
/* its MinGW build uses, does not.                                            */
/*----------------------------------------------------------------------------*/

/* Included once, under a guard name of its own. */
#ifndef BFE_E2FSPROGS_SYS_RESOURCE_H_
#define BFE_E2FSPROGS_SYS_RESOURCE_H_

/* Nothing: see the header above. */

/* The end of the once-only guard. */
#endif /* BFE_E2FSPROGS_SYS_RESOURCE_H_ */
