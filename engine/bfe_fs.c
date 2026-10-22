/*----------------------------------------------------------------------------*/
/* bfe_fs.c                                                                   */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The filesystem-neutral file calls bfe_fs.h declares: each a switch on the  */
/* open session's kind (BFX_FsKind), its ext2/3/4 arm the lwext4 call itself  */
/* with its result passed on, and its FAT arm the FatFs call that does the    */
/* same -- or, for what FAT has no place for, ENOTSUP (a mode, an owner, a    */
/* link) or success doing nothing (the access and change times).  A switch    */
/* rather than a table of function pointers, so which library runs is         */
/* written in the code.                                                       */
/*                                                                            */
/* Every result is lwext4's errno-style code; FatFs's results are translated  */
/* by BFT_FatErrno (bfe_fat.c), the engine's one such translation, so a       */
/* caller's BFX_MapErrno reads both alike.                                    */
/*----------------------------------------------------------------------------*/

/* Windows first, as the engine's other files that include lwext4 have it;    */
/* then lwext4's file API, FatFs's, the C library (errno values, memory,      */
/* strings), the engine's internals (the session's kind), the FAT session's   */
/* header (its FatFs-to-errno translation) and this file's own header.        */
#include <windows.h>
#include <ext4.h>
#include "ff.h"

#include <errno.h>
#include <stdlib.h>
#include <string.h>

#include "bfe_internal.h"
#include "bfe_fat.h"
#include "bfe_fs.h"

/*----------------------------------------------------------------------------*/
/* BFS_File -- one open image file, of either kind.  Only the member its      */
/* kind names is in use.                                                      */
/*----------------------------------------------------------------------------*/
struct BFS_File
{
    int       iKind;                 // BFX_FS_EXT or BFX_FS_FAT: the session's kind when the file was opened
    ext4_file ext;                   // lwext4's handle, when iKind is BFX_FS_EXT
    FIL       fat;                   // FatFs's handle, when iKind is BFX_FS_FAT
};

/*----------------------------------------------------------------------------*/
/* BFS_FileOpen:                                                              */
/*                                                                            */
/* Open an image file into a new handle, through lwext4 or FatFs as the       */
/* session's kind says; "wb" creates the file or truncates the one there.     */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImagePath : the file, an absolute engine path.                      */
/*     pszMode      : "rb" to read, "wb" to create or truncate for writing.   */
/*     ppFile       : receives the handle, or NULL on failure.                */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0 with *ppFile open; ENOMEM, or the library's result.            */
/*----------------------------------------------------------------------------*/
int BFS_FileOpen(const char *pszImagePath, const char *pszMode, BFS_File **ppFile)
{
    BFS_File *pFile;
    int       rc;

    /* Nothing handed back until the open succeeds. */
    *ppFile = NULL;

    /* The handle, zeroed, recording which library will hold the file. */
    pFile = (BFS_File *)calloc(1, sizeof(*pFile));

    if (pFile == NULL)
    {   /* Out of memory for the handle. */
        return(ENOMEM);
    }

    /* The session's kind, kept in the handle: every later call on it        */
    /* (read, write, close) picks its library from this, so the file stays   */
    /* with the library that opened it.                                      */
    pFile->iKind = BFX_FsKind();

    if (pFile->iKind == BFX_FS_FAT)
    {   /* FAT: "rb" opens for reading at the start; "wb" creates the file,   */
        /* or truncates one that exists, for writing -- FA_CREATE_ALWAYS,     */
        /* lwext4's "wb".                                                     */
        if (strcmp(pszMode, "rb") == 0)
        {   /* Opened for reading at its start. */
            rc = BFT_FatErrno((int)f_open(&pFile->fat, pszImagePath, FA_READ));
        }
        else
        {   /* Created or truncated for writing. */
            rc = BFT_FatErrno((int)f_open(&pFile->fat, pszImagePath, FA_WRITE | FA_CREATE_ALWAYS));
        }
    }
    else
    {   /* ext2/3/4: lwext4's own open, which takes these same "rb" / "wb"   */
        /* mode strings (ext4_parse_flags), its result passed on.            */
        rc = ext4_fopen(&pFile->ext, pszImagePath, pszMode);
    }

    if (rc != 0)
    {   /* Not opened: the handle is let go. */
        free(pFile);
        return(rc);
    }

    /* Open; the caller closes it with BFS_FileClose. */
    *ppFile = pFile;
    return(0);
}

/*----------------------------------------------------------------------------*/
/* BFS_FileRead:                                                              */
/*                                                                            */
/* Read from an open handle at its position, which moves on by what was read. */
/*                                                                            */
/* Arguments:                                                                 */
/*     pFile    : the handle.                                                 */
/*     pvBuffer : where the bytes go.                                         */
/*     cbWant   : the most to read (a buffer's size; FatFs counts in UINT,    */
/*                which every caller's buffer fits).                          */
/*     pcbRead  : receives how many were read; 0 at the end of the file.      */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0, or the library's result.                                      */
/*----------------------------------------------------------------------------*/
int BFS_FileRead(BFS_File *pFile, void *pvBuffer, size_t cbWant, size_t *pcbRead)
{
    UINT uiRead;
    int  rc;

    if (pFile->iKind == BFX_FS_FAT)
    {   /* FatFs reads up to the file's size and says how much it gave. */
        uiRead   = 0;
        rc       = BFT_FatErrno((int)f_read(&pFile->fat, pvBuffer, (UINT)cbWant, &uiRead));
        *pcbRead = (size_t)uiRead;
    }
    else
    {   /* ext2/3/4: lwext4's read, its result passed on.  It zeroes         */
        /* *pcbRead only after its early refusals (a handle not open, one    */
        /* opened for writing, a request of 0 bytes), which leave it as it   */
        /* was -- why bfe_copy.c's callers clear the count first.            */
        rc = ext4_fread(&pFile->ext, pvBuffer, cbWant, pcbRead);
    }

    /* 0, or the failure. */
    return(rc);
}

/*----------------------------------------------------------------------------*/
/* BFS_FileWrite:                                                             */
/*                                                                            */
/* Write to an open handle at its position.  On FAT a short write -- a full   */
/* volume, or a FAT12 / 16 / 32 file reaching 4 GiB -- is ENOSPC, as lwext4   */
/* reports a full filesystem.                                                 */
/*                                                                            */
/* Arguments:                                                                 */
/*     pFile      : the handle.                                               */
/*     pvBuffer   : the bytes.                                                */
/*     cbWant     : how many.                                                 */
/*     pcbWritten : receives how many went in.                                */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0, ENOSPC (a short FAT write), or the library's result.          */
/*----------------------------------------------------------------------------*/
int BFS_FileWrite(BFS_File *pFile, const void *pvBuffer, size_t cbWant, size_t *pcbWritten)
{
    UINT uiWritten;
    int  rc;

    if (pFile->iKind == BFX_FS_FAT)
    {   /* FatFs's write.  A full volume -- or a FAT12 / 16 / 32 file     */
        /* reaching 4 GiB, its size field's limit -- is a SHORT write     */
        /* with FR_OK, so a short count is turned into ENOSPC here, the   */
        /* answer lwext4 gives for a full filesystem.                     */
        uiWritten   = 0;
        rc          = BFT_FatErrno((int)f_write(&pFile->fat, pvBuffer, (UINT)cbWant, &uiWritten));
        *pcbWritten = (size_t)uiWritten;

        if (rc == 0 && (size_t)uiWritten < cbWant)
        {   /* Not all of it went in: no room. */
            rc = ENOSPC;
        }

        /* 0, ENOSPC, or FatFs's failure. */
        return(rc);
    }

    /* ext2/3/4: lwext4's write, its result passed on; lwext4 reports a   */
    /* full filesystem as ENOSPC itself, so nothing is turned here.       */
    return(ext4_fwrite(&pFile->ext, pvBuffer, cbWant, pcbWritten));
}

/*----------------------------------------------------------------------------*/
/* BFS_FileClose:                                                             */
/*                                                                            */
/* Close a handle and free it.                                                */
/*                                                                            */
/* Arguments:                                                                 */
/*     pFile : the handle, or NULL (nothing to do).                           */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 0, or the library's result for the close (the handle is freed    */
/*           either way).                                                     */
/*----------------------------------------------------------------------------*/
int BFS_FileClose(BFS_File *pFile)
{
    int rc;

    if (pFile == NULL)
    {   /* Never opened. */
        return(0);
    }

    if (pFile->iKind == BFX_FS_FAT)
    {   /* FatFs's close (a read handle has nothing to write back). */
        rc = BFT_FatErrno((int)f_close(&pFile->fat));
    }
    else
    {   /* ext2/3/4: lwext4's close, its result passed on. */
        rc = ext4_fclose(&pFile->ext);
    }

    /* The handle goes whatever the close said. */
    free(pFile);
    return(rc);
}

/*----------------------------------------------------------------------------*/
/* BFS_Remove:                                                                */
/*                                                                            */
/* Remove one file or link: lwext4's ext4_fremove on ext2/3/4; on FAT,        */
/* BFT_Unlink (FatFs's f_unlink, the read-only attribute cleared first).      */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImagePath : the entry to remove.                                    */
/*                                                                            */
/* Returns:                                                                   */
/*     int : the library's result.                                            */
/*----------------------------------------------------------------------------*/
int BFS_Remove(const char *pszImagePath)
{
    /* FAT: FatFs's unlink (its read-only attribute cleared first, in   */
    /* BFT_Unlink); ext2/3/4: lwext4.                                   */
    if (BFX_FsKind() == BFX_FS_FAT)
    {   /* A FAT session. */
        return(BFT_Unlink(pszImagePath));
    }

    /* ext2/3/4: lwext4's own call, its result passed on. */
    return(ext4_fremove(pszImagePath));
}

/*----------------------------------------------------------------------------*/
/* BFS_DirMake:                                                               */
/*                                                                            */
/* Create one directory: lwext4's ext4_dir_mk; on FAT, FatFs's f_mkdir.       */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImageDir : the directory to create; its parent must exist.          */
/*                                                                            */
/* Returns:                                                                   */
/*     int : the library's result, or the FAT answer described above.         */
/*----------------------------------------------------------------------------*/
int BFS_DirMake(const char *pszImageDir)
{
    /* FAT: FatFs's mkdir; ext2/3/4: lwext4. */
    if (BFX_FsKind() == BFX_FS_FAT)
    {   /* A FAT session. */
        return(BFT_FatErrno((int)f_mkdir(pszImageDir)));
    }

    /* ext2/3/4: lwext4's own call, its result passed on. */
    return(ext4_dir_mk(pszImageDir));
}

/*----------------------------------------------------------------------------*/
/* BFS_DirRemove:                                                             */
/*                                                                            */
/* Remove a directory and everything under it: lwext4's ext4_dir_rm, which    */
/* is recursive; on FAT, BFT_RemoveTree, the recursive remove FatFs lacks.    */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImageDir : the directory to remove.                                 */
/*                                                                            */
/* Returns:                                                                   */
/*     int : the library's result, or the FAT answer described above.         */
/*----------------------------------------------------------------------------*/
int BFS_DirRemove(const char *pszImageDir)
{
    /* FAT: the recursive remove FatFs lacks (BFT_RemoveTree); ext2/3/4:   */
    /* lwext4's, which is recursive itself.                                */
    if (BFX_FsKind() == BFX_FS_FAT)
    {   /* A FAT session. */
        return(BFT_RemoveTree(pszImageDir));
    }

    /* ext2/3/4: lwext4's own call, its result passed on. */
    return(ext4_dir_rm(pszImageDir));
}

/*----------------------------------------------------------------------------*/
/* BFS_Rename:                                                                */
/*                                                                            */
/* Rename or move an entry: lwext4's ext4_frename; on FAT, FatFs's f_rename.  */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImageFrom : the existing path.                                      */
/*     pszImageTo   : the new path.                                           */
/*                                                                            */
/* Returns:                                                                   */
/*     int : the library's result, or the FAT answer described above.         */
/*----------------------------------------------------------------------------*/
int BFS_Rename(const char *pszImageFrom, const char *pszImageTo)
{
    /* FAT: FatFs's rename (which refuses a destination that exists);   */
    /* ext2/3/4: lwext4.                                                */
    if (BFX_FsKind() == BFX_FS_FAT)
    {   /* A FAT session. */
        return(BFT_FatErrno((int)f_rename(pszImageFrom, pszImageTo)));
    }

    /* ext2/3/4: lwext4's own call, its result passed on. */
    return(ext4_frename(pszImageFrom, pszImageTo));
}

/*----------------------------------------------------------------------------*/
/* BFS_SetMode:                                                               */
/*                                                                            */
/* Set an entry's permission bits: lwext4's ext4_chmod, which keeps the type  */
/* bits; ENOTSUP on FAT, which has no permission bits.                        */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImagePath : the entry.                                              */
/*     uiMode       : the bits, passed as the caller gives them.              */
/*                                                                            */
/* Returns:                                                                   */
/*     int : the library's result, or the FAT answer described above.         */
/*----------------------------------------------------------------------------*/
int BFS_SetMode(const char *pszImagePath, uint32_t uiMode)
{
    /* FAT: no permission bits to set -- Set Mode on a FAT session is   */
    /* refused, and the walks skip it there; ext2/3/4: lwext4.          */
    if (BFX_FsKind() == BFX_FS_FAT)
    {   /* A FAT session. */
        return(ENOTSUP);
    }

    /* ext2/3/4: lwext4's own call, its result passed on. */
    return(ext4_chmod(pszImagePath, uiMode));
}

/*----------------------------------------------------------------------------*/
/* BFS_SetOwner:                                                              */
/*                                                                            */
/* Set an entry's owner and group: lwext4's ext4_chown; ENOTSUP on FAT,       */
/* which has no owners.                                                       */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImagePath : the entry.                                              */
/*     uiUid        : the owner id.                                           */
/*     uiGid        : the group id.                                           */
/*                                                                            */
/* Returns:                                                                   */
/*     int : the library's result, or the FAT answer described above.         */
/*----------------------------------------------------------------------------*/
int BFS_SetOwner(const char *pszImagePath, uint32_t uiUid, uint32_t uiGid)
{
    /* FAT: no owners -- refused; the walks skip it there; ext2/3/4:   */
    /* lwext4.                                                         */
    if (BFX_FsKind() == BFX_FS_FAT)
    {   /* A FAT session. */
        return(ENOTSUP);
    }

    /* ext2/3/4: lwext4's own call, its result passed on. */
    return(ext4_chown(pszImagePath, uiUid, uiGid));
}

/*----------------------------------------------------------------------------*/
/* BFS_SetAtime:                                                              */
/*                                                                            */
/* Set an entry's access time: lwext4's ext4_file_set_atime; on FAT, success  */
/* doing nothing (FAT keeps no access time FatFs writes).                     */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImagePath : the entry.                                              */
/*     uiTime       : the time, epoch seconds.                                */
/*                                                                            */
/* Returns:                                                                   */
/*     int : the library's result, or the FAT answer described above.         */
/*----------------------------------------------------------------------------*/
int BFS_SetAtime(const char *pszImagePath, uint32_t uiTime)
{
    /* FAT: no access time to set (FAT keeps an access DATE that FatFs   */
    /* does not write), so it succeeds doing nothing, and a caller       */
    /* setting all three times goes on; ext2/3/4: lwext4.                */
    if (BFX_FsKind() == BFX_FS_FAT)
    {   /* A FAT session. */
        return(0);
    }

    /* ext2/3/4: lwext4's own call, its result passed on. */
    return(ext4_file_set_atime(pszImagePath, uiTime));
}

/*----------------------------------------------------------------------------*/
/* BFS_SetMtime:                                                              */
/*                                                                            */
/* Set an entry's modify time: lwext4's ext4_file_set_mtime; on FAT,          */
/* BFT_SetModifyTime (FAT's one time, in local time; the root's a no-op).     */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImagePath : the entry.                                              */
/*     uiTime       : the time, epoch seconds.                                */
/*                                                                            */
/* Returns:                                                                   */
/*     int : the library's result, or the FAT answer described above.         */
/*----------------------------------------------------------------------------*/
int BFS_SetMtime(const char *pszImagePath, uint32_t uiTime)
{
    /* FAT: its one time, in local time (BFT_SetModifyTime); ext2/3/4:   */
    /* lwext4.                                                           */
    if (BFX_FsKind() == BFX_FS_FAT)
    {   /* A FAT session. */
        return(BFT_SetModifyTime(pszImagePath, uiTime));
    }

    /* ext2/3/4: lwext4's own call, its result passed on. */
    return(ext4_file_set_mtime(pszImagePath, uiTime));
}

/*----------------------------------------------------------------------------*/
/* BFS_SetCtime:                                                              */
/*                                                                            */
/* Set an entry's inode-change time: lwext4's ext4_file_set_ctime; on FAT,    */
/* success doing nothing (FAT has no inode-change time).                      */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImagePath : the entry.                                              */
/*     uiTime       : the time, epoch seconds.                                */
/*                                                                            */
/* Returns:                                                                   */
/*     int : the library's result, or the FAT answer described above.         */
/*----------------------------------------------------------------------------*/
int BFS_SetCtime(const char *pszImagePath, uint32_t uiTime)
{
    /* FAT: no inode-change time exists, so it succeeds doing nothing;   */
    /* ext2/3/4: lwext4.                                                 */
    if (BFX_FsKind() == BFX_FS_FAT)
    {   /* A FAT session. */
        return(0);
    }

    /* ext2/3/4: lwext4's own call, its result passed on. */
    return(ext4_file_set_ctime(pszImagePath, uiTime));
}

/*----------------------------------------------------------------------------*/
/* BFS_ReadLink:                                                              */
/*                                                                            */
/* Copy a symbolic link's target, unterminated: lwext4's ext4_readlink.  On   */
/* FAT, ENOTSUP: FAT has no links of either kind, and a FAT listing never     */
/* reports one, so this is never asked for there.                             */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImagePath : the link.                                               */
/*     pszBuffer    : where the target goes.                                  */
/*     cbBuffer     : the buffer's size.                                      */
/*     pcbRead      : receives how many bytes were copied.                    */
/*                                                                            */
/* Returns:                                                                   */
/*     int : lwext4's result, or ENOTSUP on FAT.                              */
/*----------------------------------------------------------------------------*/
int BFS_ReadLink(const char *pszImagePath, char *pszBuffer, size_t cbBuffer, size_t *pcbRead)
{
    /* FAT: no links; ext2/3/4: lwext4. */
    if (BFX_FsKind() == BFX_FS_FAT)
    {   /* FAT has none. */
        return(ENOTSUP);
    }

    /* ext2/3/4: lwext4's own call, its result passed on. */
    return(ext4_readlink(pszImagePath, pszBuffer, cbBuffer, pcbRead));
}

/*----------------------------------------------------------------------------*/
/* BFS_SymLink:                                                               */
/*                                                                            */
/* Create a symbolic link: lwext4's ext4_fsymlink; ENOTSUP on FAT, which has  */
/* no links.                                                                  */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszTarget    : the text the link holds.                                */
/*     pszImageLink : the link to create.                                     */
/*                                                                            */
/* Returns:                                                                   */
/*     int : lwext4's result, or ENOTSUP on FAT.                              */
/*----------------------------------------------------------------------------*/
int BFS_SymLink(const char *pszTarget, const char *pszImageLink)
{
    /* FAT: no links; ext2/3/4: lwext4. */
    if (BFX_FsKind() == BFX_FS_FAT)
    {   /* FAT has none. */
        return(ENOTSUP);
    }

    /* ext2/3/4: lwext4's own call, its result passed on. */
    return(ext4_fsymlink(pszTarget, pszImageLink));
}

/*----------------------------------------------------------------------------*/
/* BFS_HardLink:                                                              */
/*                                                                            */
/* Give a file a second name: lwext4's ext4_flink; ENOTSUP on FAT, which has  */
/* no links.                                                                  */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImageExisting : the file that exists.                               */
/*     pszImageNewName  : the second name.                                    */
/*                                                                            */
/* Returns:                                                                   */
/*     int : lwext4's result, or ENOTSUP on FAT.                              */
/*----------------------------------------------------------------------------*/
int BFS_HardLink(const char *pszImageExisting, const char *pszImageNewName)
{
    /* FAT: no links; ext2/3/4: lwext4. */
    if (BFX_FsKind() == BFX_FS_FAT)
    {   /* FAT has none. */
        return(ENOTSUP);
    }

    /* ext2/3/4: lwext4's own call, its result passed on. */
    return(ext4_flink(pszImageExisting, pszImageNewName));
}
