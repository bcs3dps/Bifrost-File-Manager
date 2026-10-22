/*----------------------------------------------------------------------------*/
/* sys/ioctl.h                                                                */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* A stand-in for the POSIX <sys/ioctl.h>, which MinGW does not have.         */
/* e2fsck's pass5.c includes it unconditionally (not behind HAVE_SYS_IOCTL_H) */
/* and then uses nothing from it, so an empty header is the whole fix.        */
/* Upstream's own include/nonunix/sys/ioctl.h is the same empty file, but its */
/* MinGW build uses include/mingw, which lacks one; this is that gap filled.  */
/*----------------------------------------------------------------------------*/

/* Included once, under a guard name of its own. */
#ifndef BFE_E2FSPROGS_SYS_IOCTL_H_
#define BFE_E2FSPROGS_SYS_IOCTL_H_

/* Nothing: see the header above. */

/* The end of the once-only guard. */
#endif /* BFE_E2FSPROGS_SYS_IOCTL_H_ */
