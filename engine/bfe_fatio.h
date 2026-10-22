/*----------------------------------------------------------------------------*/
/* bfe_fatio.h                                                                */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The engine's device layer for FatFs (engine\fatfs): the five disk_*        */
/* functions FatFs's diskio.h declares, and its get_fattime, are defined in   */
/* bfe_fatio.c over the engine's own block device (bfe_blockdev.h's target    */
/* functions), so a FAT filesystem in an image file, in one partition of a    */
/* disk image, or on a card is reached through the same range, bounce         */
/* buffer, read-only refusal and write read-back as an ext4 one.  This        */
/* header carries only what the engine itself calls besides those.            */
/*                                                                            */
/* Internal to the engine: nothing here is exported from the DLL.             */
/*----------------------------------------------------------------------------*/

/* Include guard: the file is read once however many files include it. */
#ifndef BFE_FATIO_H
#define BFE_FATIO_H

/* Fixed-width integers. */
#include <stdint.h>

/*----------------------------------------------------------------------------*/
/* BFA_SetSectorSize : the sector size FatFs is told (GET_SECTOR_SIZE) and    */
/*                     the unit its sector numbers count in, for the NEXT     */
/*                     mount or format: 512 for a file, the device's own      */
/*                     figure (512 or 4096) for a card.  Called before        */
/*                     f_mount / f_mkfs; a value FatFs is not built for       */
/*                     (outside 512 to 4096, or not a power of two) is        */
/*                     refused with -1 and the size is left as it was.        */
/*                     Returns 0 when taken.                                  */
/*----------------------------------------------------------------------------*/
int BFA_SetSectorSize(uint32_t uiSectorBytes);

/* The end of the include guard. */
#endif // BFE_FATIO_H
