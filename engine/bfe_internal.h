/*----------------------------------------------------------------------------*/
/* bfe_internal.h                                                             */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* What the engine's own source files share with each other and export to     */
/* nobody: the call gate that serialises the public functions, the error      */
/* recorder, the inode-to-entry reader, the directory collector, and the      */
/* text helpers (path joining, UTF-8 <-> UTF-16, Windows name rules).         */
/*                                                                            */
/* Prefixes: BFE_ is the public API (bifrost_engine.h), BFX_ is shared engine */
/* internals (bifrost_engine.c), BFU_ is text utilities (bfe_util.c), BFD_ is */
/* the block device (bfe_blockdev.c), BFV_ is the raw file-or-device I/O      */
/* layer beneath it (bfe_device.c), BFC_ and BFM_ are the copy engine and its */
/* manifest (bfe_copy.c), BFK_ is the formatter's helpers (bfe_mkfs.c), BFI_  */
/* is raw imaging (bfe_image.c), BFIO_ is the e2fsprogs io manager over the   */
/* block device (bfe_fsck_io.c) and BFCK_ is the filesystem check that drives */
/* e2fsck through it (bfe_fsck.c), BFZ_ is the compressed-image reader and    */
/* writer (bfe_compress.c), BFRZ_ is the resize that drives resize2fs         */
/* (bfe_resize.c), BFA_ is FatFs's device layer over the block device         */
/* (bfe_fatio.c), BFT_ is the FAT session's calls into FatFs (bfe_fat.c),     */
/* and BFQ_ is the squashfs session's calls into squashfuse (bfe_sqfs.c).     */
/*                                                                            */
/* BFC_ is used TWICE: by the copy engine and by the capacity test            */
/* (bfe_capacity.c).  Every function of both is static and no name occurs in  */
/* both files, so nothing collides; a grep for BFC_ simply finds both.        */
/*                                                                            */
/* Only the BFX_ and BFU_ groups are declared here; each other group has its  */
/* own header beside its .c file, or is static to it.                         */
/*----------------------------------------------------------------------------*/

/* Include guard: the file is read once however many headers include it. */
#ifndef BFE_INTERNAL_H
#define BFE_INTERNAL_H

/* Fixed-width integers, size_t, wchar_t for the host-path helpers, and     */
/* bool: the engine's one boolean form is <stdbool.h>'s, because lwext4,    */
/* built into the engine, requires that header -- so every flag here is a   */
/* bool holding true or false, and the engine defines no TRUE or FALSE.     */
#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>
#include <wchar.h>

/* The public interface: BFE_Entry and the status codes the internals use. */
#include "bifrost_engine.h"

/*----------------------------------------------------------------------------*/
/* Definitions -- documentation table.                                        */
/*----------------------------------------------------------------------------*/
#define BFX_MOUNT_POINT    "/"                   // lwext4 mount point; image paths are absolute beneath it
#define BFX_PERM_MASK      0x0FFFu               // the 12 permission bits of an inode mode word
#define BFX_IO_BUFFER      65536                 // streaming buffer for file copies, bytes (64 KiB, malloc'd per file by bfe_copy.c's three byte-copy loops); one progress event per buffer
#define BFX_NAME_MAX       255                   // longest ext4 entry name, bytes -- ext4's own limit, which a FAT name may pass
#define BFX_ENTRY_NAME_MAX (BFE_NAME_BYTES - 1)  // longest name a BFE_Entry's szName holds, bytes of UTF-8 with its NUL beside it: 767, past a FAT long name's 765
#define BFX_FAT_NAME_UNITS 255                   // longest FAT or exFAT long name, UTF-16 units (FatFs's FF_MAX_LFN; Windows' limit for a name too)
#define BFX_NEW_DIR_MODE   0755u                 // permission bits BFE_MakeDir gives a new directory
#define BFX_NEW_LINK_MODE  0777u                 // permission bits of a symbolic link (always 0777 on Linux)
#define BFX_HEAL_NAME      ".bifrost-heal"       // the entry briefly created in a new directory to settle its checksum

/*----------------------------------------------------------------------------*/
/* The filesystem kinds a session can hold -- documentation table.  Which     */
/* library holds the open session's filesystem (BFX_FsKind).  ADDING A KIND:  */
/* a value here, its mask below, its row in bifrost_engine.c's traits table   */
/* (BFX_FS_TRAITS, in this order), a case in every switch on the kind --      */
/* each switch has a default that refuses, so a case not yet written fails    */
/* closed -- and its mask in the kinds of every session call taught it        */
/* (BFX_EnterKinds); a call not taught it refuses it at the gate.             */
/*----------------------------------------------------------------------------*/
typedef enum BFX_enmFsKind
{
    BFX_FS_EXT = 0,                                  // ext2/3/4, mounted by lwext4 -- also the answer with no session open
    BFX_FS_FAT = 1,                                  // FAT12/16/32 or exFAT, mounted by FatFs (bfe_fat.c)
    BFX_FS_SQFS = 2,                                 // squashfs 4.0, read by squashfuse (bfe_sqfs.c); never writable
    BFX_FS_COUNT = 3                                 // how many kinds there are: the traits table's length, never a kind
} BFX_enmFsKind;

/* Each kind's bit, for the set of kinds a session call handles (BFX_EnterKinds). */
#define BFX_FSMASK_EXT     (1u << BFX_FS_EXT)    // the call handles an ext2/3/4 session
#define BFX_FSMASK_FAT     (1u << BFX_FS_FAT)    // the call handles a FAT or exFAT session
#define BFX_FSMASK_SQFS    (1u << BFX_FS_SQFS)   // the call handles a squashfs session

/*----------------------------------------------------------------------------*/
/* BFX_strctFsTraits -- what a kind of filesystem is like, for the code that  */
/* tests a property rather than calling a library: one row per kind in        */
/* bifrost_engine.c's BFX_FS_TRAITS, read through BFX_FsTraits.  Plain data,  */
/* no function pointers: which library runs is decided by the switches on     */
/* the kind, written out in the code.                                         */
/*----------------------------------------------------------------------------*/
typedef struct BFX_strctFsTraits
{
    bool        bWritable;                           // true when a session of the kind can ever be opened for writing; false refuses every change READ_ONLY at the gate
    bool        bUnixMeta;                           // true when entries carry Unix modes, owners and links -- so a copy sets them and an extract writes the manifest
    bool        bFatNames;                           // true when FAT's naming rules hold: names matched letter case aside and without trailing spaces and dots, some characters forbidden, up to 255 UTF-16 units
    const char *pszUntaught;                         // the detail line for a session call not taught the kind (the gate, behind BFX_Enter and BFX_EnterKinds) -- what the kind lacks
} BFX_strctFsTraits;

/* lwext4's superblock, named but not defined: BFX_Superblock below hands   */
/* out a pointer to one, and a file that only passes the pointer on need    */
/* not include lwext4's headers to do so.                                   */
struct ext4_sblock;

/*----------------------------------------------------------------------------*/
/* Shared engine internals -- defined in bifrost_engine.c.                    */
/*                                                                            */
/* BFX_Enter        : the gate a public function passes first when it handles */
/*                    ext2/3/4 alone or needs no session (BFX_EnterKinds is   */
/*                    the other way through it).  Takes the busy flag and     */
/*                    checks the session state; on any failure it has         */
/*                    ALREADY released the flag, so the caller just returns   */
/*                    the code.  On BFE_OK the caller must BFX_Leave.         */
/*                    It also notes, for BFX_Fail, whether the call works     */
/*                    inside the open session (bNeedOpen).  A call that needs */
/*                    the session handles ext2/3/4 alone: any other kind is   */
/*                    refused (BFX_EnterKinds says how), so code written for  */
/*                    lwext4 never runs without an lwext4 mount.              */
/* BFX_EnterKinds   : the gate for a call that needs the open session and     */
/*                    names, as BFX_FSMASK_* bits, every kind it handles; a   */
/*                    session of any other kind is refused -- a change on a   */
/*                    kind that is never writable as READ_ONLY, anything      */
/*                    else as UNSUPPORTED -- with the kind's own reason.      */
/* BFX_FsKind       : the open session's kind (BFX_FS_EXT when none is open)  */
/*                    -- what every switch on the kind reads.                 */
/* BFX_FsTraits     : the open session's kind's row of the traits table (the  */
/*                    ext2/3/4 row when none is open); never NULL.            */
/* BFX_FsMaxFileBytes: the largest file the open session's filesystem holds,  */
/*                    for a copy's refusal before anything is written         */
/*                    (UINT64_MAX when the kind sets no limit of its own; 0   */
/*                    for a kind no copy may write, squashfs).                */
/* BFX_Leave        : release the busy flag.                                  */
/* BFX_IsOpen       : true while an image is mounted (for a function that     */
/*                    must run only when none is; call it holding the flag).  */
/* BFX_Fail         : record a printf-style detail line as the last error and */
/*                    hand the code back, so a failure is one statement.      */
/*                    An I/O, journal or mount code comes back as             */
/*                    BFE_ERR_VERIFY instead when a device write was not kept */
/*                    by the open session (in a call inside it) or by an      */
/*                    operation the call began on the block device -- never   */
/*                    in a raw call, which drives none.  EVERY CALLER, in     */
/*                    every engine file, calls it BEFORE BFX_Leave: written   */
/*                    after the release, another call could overwrite the     */
/*                    line and the user would be shown the wrong reason.      */
/* BFX_MapErrno     : translate an lwext4 errno-style result to BFE_ERR_*.    */
/* BFX_PathValid    : true when an image path is non-NULL, absolute and       */
/*                    canonical: "/" itself, or no empty, "." or ".."         */
/*                    component (so no "//" and no trailing '/') -- and, on   */
/*                    a FAT session, no component of spaces and dots alone,   */
/*                    which would be empty once FAT drops a name's trailing   */
/*                    spaces and dots, and none holding a character FAT       */
/*                    forbids (" * : < > ? | \, a control character, DEL) --  */
/*                    FatFs reads ':' as a drive, '\' as a separator and a    */
/*                    control character as the path's end.                    */
/* BFX_PathCanonical: a malloc'd copy of a valid path in the one spelling its */
/*                    filesystem matches it by -- on FAT each name's trailing */
/*                    spaces and dots dropped, as FatFs and Windows drop      */
/*                    them; on ext2/3/4 and squashfs the path itself -- for   */
/*                    the tests that compare paths as text.  NULL when out    */
/*                    of memory.                                              */
/* BFX_Superblock   : the mounted filesystem's superblock (never NULL while   */
/*                    an ext2/3/4 image is open; NULL on a FAT or squashfs    */
/*                    session, whose gate refuses every call that would ask   */
/*                    for it).                                                */
/* BFX_FillEntry    : read one path's inode into a BFE_Entry; pszName becomes */
/*                    the entry's name.  Returns an lwext4 errno-style code.  */
/*                    On a FAT session FatFs supplies the facts instead       */
/*                    (BFT_StatEntry), and on a squashfs one squashfuse       */
/*                    (BFQ_StatEntry), with the same kind of result.          */
/* BFX_CollectNames : read a directory's entry names ("." and ".." excluded)  */
/*                    into a malloc'd array and CLOSE the directory, so no    */
/*                    lwext4 directory handle is open while the caller works  */
/*                    on the entries.  Returns an lwext4 errno-style code.    */
/*                    A directory block that cannot be read is a failure      */
/*                    (lwext4's error), with no names handed back.  On a FAT  */
/*                    session FatFs reads the directory instead               */
/*                    (BFT_CollectNames), and on a squashfs one squashfuse    */
/*                    (BFQ_CollectNames), under the same promise.             */
/* BFX_FreeNames    : free what BFX_CollectNames returned.                    */
/* BFX_NewDirFinish : after lwext4 creates a directory -- settle its block    */
/*                    checksum, set its mode and its times.  Returns an       */
/*                    lwext4 errno-style code.  On a FAT session only its     */
/*                    modify time is set (FAT's one) and its parent touched;  */
/*                    on squashfs it answers EROFS, a guard.                  */
/* BFX_NewLinkFinish: after lwext4 creates a symbolic link -- set mode 0777,  */
/*                    its owner and its times.  Returns an lwext4 errno-style */
/*                    code.                                                   */
/* BFX_TouchParent  : after an entry is added to or removed from a directory  */
/*                    -- set that directory's modify and change times to the  */
/*                    present, as Linux does (on FAT, the modify time only).  */
/*                    Returns an errno-style code (ENOMEM for the parent's    */
/*                    path).                                                  */
/* BFX_CheckParent  : before a create -- whether the directory to hold the    */
/*                    new entry exists and is a directory, since lwext4's     */
/*                    create path would make a missing one.  Returns 0,       */
/*                    ENOTDIR, ENOMEM, or the lookup's errno-style result     */
/*                    (ENOENT when missing).                                  */
/*----------------------------------------------------------------------------*/
int   BFX_TouchParent(const char *pszImagePath);
int   BFX_CheckParent(const char *pszImagePath);
int   BFX_NewDirFinish(const char *pszImageDir, uint32_t uiMode, uint32_t uiTime);
int   BFX_NewLinkFinish(const char *pszImageLink, uint32_t uiUid, uint32_t uiGid, uint32_t uiTime);
int   BFX_Enter(bool bNeedOpen, bool bNeedWrite);
int   BFX_EnterKinds(bool bNeedWrite, uint32_t uiFsKinds);
BFX_enmFsKind BFX_FsKind(void);
const BFX_strctFsTraits *BFX_FsTraits(void);
uint64_t BFX_FsMaxFileBytes(void);
void  BFX_Leave(void);
bool  BFX_IsOpen(void);
int   BFX_Fail(int iCode, const char *pszFormat, ...);
int   BFX_MapErrno(int rc);
bool  BFX_PathValid(const char *pszImagePath);
char *BFX_PathCanonical(const char *pszImagePath);
struct ext4_sblock *BFX_Superblock(void);
int   BFX_FillEntry(const char *pszImagePath, const char *pszName, BFE_Entry *pEntry);
int   BFX_CollectNames(const char *pszImageDir, char ***pppszNames, int *pnNames);
void  BFX_FreeNames(char **ppszNames, int nNames);

/*----------------------------------------------------------------------------*/
/* Text utilities -- defined in bfe_util.c.  Every function that returns a    */
/* pointer returns malloc'd memory the caller frees, or NULL on failure --    */
/* except BFU_LastComponent, which points INTO its argument and must not be   */
/* freed.  For the two converters NULL also means out of memory, which their  */
/* pbNoMemory argument tells apart, so a caller reports it as an error.       */
/*                                                                            */
/* BFU_JoinImage     : "<dir>/<name>" for an image path ("/" + name at root). */
/* BFU_JoinHost      : "<dir>\<name>" for a host path.                        */
/* BFU_JoinRelative  : the manifest path of a child: just <name> when the     */
/*                     parent is "." (the root of the copy), else             */
/*                     "<parent>/<name>".                                     */
/* BFU_AppendHost    : <path> with a suffix appended (a sidecar's name).      */
/* BFU_Utf8ToWide    : UTF-8 -> UTF-16; NULL on malformed input, or out of    */
/*                     memory with *pbNoMemory true.                          */
/* BFU_WideToUtf8    : UTF-16 -> UTF-8; NULL on an unpaired surrogate, or out */
/*                     of memory with *pbNoMemory true.                       */
/* BFU_HostNameValid : true when a UTF-8 name can be a Windows file name.     */
/* BFU_LastComponent : pointer to the final component inside an image path    */
/*                     (the whole path for the root); nothing to free.        */
/*----------------------------------------------------------------------------*/
char       *BFU_JoinImage(const char *pszDir, const char *pszName);
wchar_t    *BFU_JoinHost(const wchar_t *pwszDir, const wchar_t *pwszName);
char       *BFU_JoinRelative(const char *pszParent, const char *pszName);
wchar_t    *BFU_AppendHost(const wchar_t *pwszPath, const wchar_t *pwszSuffix);
wchar_t    *BFU_Utf8ToWide(const char *pszUtf8, bool *pbNoMemory);
char       *BFU_WideToUtf8(const wchar_t *pwszWide, bool *pbNoMemory);
bool        BFU_HostNameValid(const char *pszName);
const char *BFU_LastComponent(const char *pszImagePath);

/* The end of the include guard. */
#endif // BFE_INTERNAL_H
