/*----------------------------------------------------------------------------*/
/* termios.h                                                                  */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* A stand-in for the POSIX <termios.h>, which MinGW does not have.           */
/* e2fsck's ehandler.c, problem.c and message.c include it unconditionally;   */
/* the terminal handling that would use it (raw mode for the y/n prompt in    */
/* util.c's ask) is compiled out because HAVE_TERMIOS_H is not defined        */
/* (config.h) -- and the engine never asks: it runs e2fsck with every answer  */
/* decided in advance (-n or -y), so no prompt is ever shown.                 */
/* Upstream's include/nonunix has the same empty file; include/mingw does not.*/
/*----------------------------------------------------------------------------*/

/* Included once, under a guard name of its own. */
#ifndef BFE_E2FSPROGS_TERMIOS_H_
#define BFE_E2FSPROGS_TERMIOS_H_

/* Nothing: see the header above. */

/* The end of the once-only guard. */
#endif /* BFE_E2FSPROGS_TERMIOS_H_ */
