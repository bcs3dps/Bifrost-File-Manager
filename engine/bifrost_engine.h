/*----------------------------------------------------------------------------*/
/* bifrost_engine.h                                                           */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The public interface of the BifrostFM engine: a C library, built as        */
/* bifrost_engine.dll, that opens a filesystem in userspace -- ext2, ext3     */
/* or ext4 through the bundled lwext4 library, FAT12, FAT16, FAT32 or exFAT   */
/* through the bundled FatFs, squashfs (read-only) through the bundled        */
/* squashfuse -- in an image FILE or on a real DEVICE, whole or one           */
/* partition of it, and lets a caller browse it, edit it, and copy files and  */
/* trees between it and the Windows filesystem; and, beside the               */
/* session, the whole-device work: a device's facts, raw reads and writes,    */
/* the raw copy that images a device to a file and back (plain or             */
/* compressed), making a filesystem, checking and repairing an ext one        */
/* (e2fsck), growing one (resize2fs), and the capacity test.                  */
/*                                                                            */
/* EVERY CONSTANT AND STRUCTURE HERE IS MIRRORED BY HAND in the program --    */
/* gui\EngineNative.cs (the P/Invoke declarations) and gui\EngineSession.cs   */
/* -- so a change here is made there too, value for value and field for       */
/* field.  BFE_API_VERSION is the guard: the program refuses a DLL whose      */
/* BFE_ApiVersion differs from its own ENG_API_VERSION, checked at load.      */
/*                                                                            */
/* THE CONTRACT, in the order a caller meets it:                              */
/*                                                                            */
/*   - Every function returns an int status: BFE_OK (0) or a BFE_ERR_* code   */
/*     -- except BFE_ApiVersion, which returns the version itself.            */
/*     BFE_LastErrorText gives one line of detail for the last failure.       */
/*   - Paths INSIDE the image are UTF-8, absolute, and use '/', with no       */
/*     empty, "." or ".." component -- no "//" and no trailing '/', the root  */
/*     "/" excepted -- so each entry has one spelling (BFE_ERR_ARG            */
/*     otherwise).  On a FAT or exFAT session a name's trailing spaces and    */
/*     dots are dropped, as FAT and Windows drop them -- "/notes." names      */
/*     "/notes" -- and a component of spaces and dots alone is BFE_ERR_ARG,   */
/*     as is one holding a character FAT forbids: " * : < > ? | \, a          */
/*     control character or DEL (a ':' is a drive and a '\' a separator to    */
/*     FAT's path rules, never part of a name).  On ext2/3/4 every one of     */
/*     those is an ordinary name character.                                   */
/*     Paths on the HOST are UTF-16 and use '\'.  Neither has a length cap    */
/*     here.                                                                  */
/*   - ONE image is open at a time, and calls are serialised: a call made     */
/*     while another is still running returns BFE_ERR_BUSY.  Make every call  */
/*     from one worker thread; callbacks run on that same thread, inside the  */
/*     call, and must not call back into the engine.                          */
/*   - The calling convention is the platform default (cdecl; there is only   */
/*     one on x64).  Every structure below is laid out with its 64-bit        */
/*     members first so it has no hidden padding on any compiler.             */
/*----------------------------------------------------------------------------*/

/* Included once. */
#ifndef BIFROST_ENGINE_H
#define BIFROST_ENGINE_H

/* The fixed-width integers every field below uses, and stddef.h for size_t   */
/* and wchar_t (the host paths' type).                                        */
#include <stdint.h>
#include <stddef.h>

/* C linkage for a C++ includer, so the exported names are not mangled and   */
/* match what the DLL exports.                                               */
#ifdef __cplusplus
extern "C" {
#endif

/*----------------------------------------------------------------------------*/
/* Export decoration.  The engine's own sources define BFE_BUILDING_DLL so    */
/* the functions are exported; a C caller that includes this header imports   */
/* them.                                                                      */
/*----------------------------------------------------------------------------*/
#ifdef BFE_BUILDING_DLL
#define BFE_API __declspec(dllexport)    // building the DLL: export each function
#else
#define BFE_API __declspec(dllimport)    // using the DLL from C: import each function
#endif

/*----------------------------------------------------------------------------*/
/* Version -- documentation table.                                            */
/*----------------------------------------------------------------------------*/
#define BFE_API_VERSION         20       // bumped whenever a function is added, or a signature, structure layout or event set changes (2: BFE_MakeFs; 3: BFE_CopyWithin; 4: devices -- BFE_DeviceInfoGet, BFE_ReadRaw, BFE_CopyRaw, and BFE_Open taking a device path; 5: BFE_CheckFs; 6: BFE_MakeFsAt; 7: BFE_WriteRaw; 8: BFE_MakeFsAt's zero-fill flag and progress callback; 9: BFE_ResizeFs; 10: compressed images -- BFE_ImageInfoGet, BFE_RAW_DECOMPRESS, BFE_RAW_XZ_OUT; 11: device write read-back -- BFE_SetWriteVerify, BFE_TestCapacity; 12: the manifest's name taken by data -- BFE_EVENT_MANIFEST_CLASH, BFE_EVENT_NOT_MANIFEST, BFE_REPLY_KEEP_FILE, BFE_REPLY_KEEP_MANIFEST; 13: BFE_WriteRawBatch and its 24-byte BFE_RawPiece; 14: BFE_CopyRaw's BFE_RAW_IN_PLACE and BFE_RAW_ZERO_SOURCE, for one partition's read, write and erase; 15: FAT and exFAT -- BFE_Open mounting them, BFE_MakeFs / MakeFsAt making them (BFE_FSTYPE_FAT16 / FAT32 / EXFAT), BFE_VolumeInfo's uiVolumeType (88 bytes) and BFE_Entry's uiAttributes (312 bytes); 16: BFE_NAME_BYTES 768, so a FAT long name is listed whole (BFE_Entry 824 bytes); 17: BFE_RemoveTree's progress callback and cancel; 18: BFE_VOLUME_NAME_BYTES 40, so every label any type holds fits -- BFE_MakeFsOptions 64 bytes, BFE_VolumeInfo 104; 19: squashfs -- BFE_Open mounting it read-only, BFE_VOLTYPE_SQUASHFS; 20: BFE_Extract following symbolic links -- BFE_ExtractOptions.bFollowLinks (16 bytes), BFE_ERR_LINK_LOOP)

/*----------------------------------------------------------------------------*/
/* Volume types -- documentation table.  BFE_VolumeInfo.uiVolumeType carries  */
/* one: which kind of filesystem the session holds.  ext2, ext3 and ext4 are  */
/* one value, as one library reads them; their feature words tell them apart. */
/*----------------------------------------------------------------------------*/
#define BFE_VOLTYPE_EXT         1        // ext2, ext3 or ext4 (lwext4); the feature words say which
#define BFE_VOLTYPE_FAT12       12       // FAT12 (FatFs)
#define BFE_VOLTYPE_FAT16       16       // FAT16 (FatFs)
#define BFE_VOLTYPE_FAT32       32       // FAT32 (FatFs)
#define BFE_VOLTYPE_EXFAT       64       // exFAT (FatFs)
#define BFE_VOLTYPE_SQUASHFS    128      // squashfs 4.0 (squashfuse); always read-only

/*----------------------------------------------------------------------------*/
/* FAT attributes -- documentation table.  BFE_Entry.uiAttributes carries     */
/* these bits on a FAT or exFAT session, the values FAT stores on disk (the   */
/* FAT specification's DIR_Attr); always 0 on ext2/3/4, which has none.       */
/*----------------------------------------------------------------------------*/
#define BFE_ATTR_READONLY       0x01     // read-only: shown as the mode without its write bits
#define BFE_ATTR_HIDDEN         0x02     // hidden from an ordinary listing on Windows
#define BFE_ATTR_SYSTEM         0x04     // a system file
#define BFE_ATTR_DIRECTORY      0x10     // a directory (uiType says so too)
#define BFE_ATTR_ARCHIVE        0x20     // changed since a backup cleared the bit

/*----------------------------------------------------------------------------*/
/* Status codes -- documentation table.  Every function returns one of these. */
/*----------------------------------------------------------------------------*/
#define BFE_OK                  0        // success
#define BFE_ERR_ARG             1        // a NULL or malformed argument (image paths must start with '/')
#define BFE_ERR_NOT_OPEN        2        // no image is open
#define BFE_ERR_ALREADY_OPEN    3        // BFE_Open while an image is open; close it first
#define BFE_ERR_BUSY            4        // another engine call is still running
#define BFE_ERR_NO_MEMORY       5        // an allocation failed -- in a copy (BFE_Extract, BFE_Import, BFE_CopyWithin) it STOPS the whole copy at that item: its ERROR event first, then the call returns this
#define BFE_ERR_IMAGE_OPEN      6        // the file or device path could not be opened (missing, in use, read-only, or a device without Administrator); the text gives the Windows error.  A range outside the target is reported otherwise: BFE_ERR_MOUNT from BFE_Open, BFE_ERR_ARG / BFE_ERR_IO_IMAGE from the raw and format calls
#define BFE_ERR_MOUNT           7        // not an ext2/3/4 filesystem (or its superblock is damaged), nor a squashfs, FAT or exFAT one -- or a squashfs whose superblock or tables are damaged
#define BFE_ERR_JOURNAL         8        // the journal could not be started for a writable session -- or a flush could not checkpoint or restart it, after which the session refuses every change with this code and BFE_Close reports it again
#define BFE_ERR_READ_ONLY       9        // a modifying call on a read-only session (every squashfs session is one), or a writable BFE_Open of what can only be read
#define BFE_ERR_NOT_FOUND       10       // the path does not exist
#define BFE_ERR_EXISTS          11       // the destination name is already taken
#define BFE_ERR_NOT_DIR         12       // a directory was required and the path is something else
#define BFE_ERR_IS_DIR          13       // a non-directory was required and the path is a directory
#define BFE_ERR_NOT_EMPTY       14       // BFE_RemoveDir on a directory that has entries
#define BFE_ERR_NO_SPACE        15       // the filesystem in the image is full -- or, from BFE_CopyRaw, the destination device is smaller than the image
#define BFE_ERR_IO_IMAGE        16       // any other failure reading or writing the image
#define BFE_ERR_IO_HOST         17       // a failure reading or writing a host (Windows) file
#define BFE_ERR_CANCELLED       18       // a callback asked for the operation to stop
#define BFE_ERR_NAME            19       // a name cannot be represented on the other side (see BFE_EVENT_SKIPPED)
#define BFE_ERR_TYPE            20       // an entry type that cannot cross the boundary (symlink, device, fifo, socket)
#define BFE_ERR_PARTIAL         21       // a copy finished but at least one item failed; the events said which
#define BFE_ERR_BUFFER          22       // the caller's buffer is too small for the result
#define BFE_ERR_NOT_SYMLINK     23       // BFE_ReadLink on something that is not a symbolic link
#define BFE_ERR_SYSTEM_DISK     24       // a write to the physical disk Windows is running from (or to one whose disk cannot be told); never overridden
#define BFE_ERR_VOLUME_LOCK     25       // the disk about to be written could not be locked: a volume on it in use (something has files open on it), or a volume whose disk could not be told; the detail line says which, naming the volume
#define BFE_ERR_VERIFY          26       // what was read back differs from what was written: BFE_CopyRaw with BFE_RAW_VERIFY, or a device that did not keep a filesystem write (a format, a resize, a repair, or a session with BFE_SetWriteVerify on) -- a failing or counterfeit card
#define BFE_ERR_NEEDS_CHECK     27       // BFE_ResizeFs: the filesystem must be checked first (BFE_CheckFs, a repair if it finds anything): errors recorded, not cleanly unmounted, used since its last check, impossible free counts, or orphans or a journal to replay
#define BFE_ERR_UNSUPPORTED     28       // something this build does not handle: on a FAT or exFAT session, a call FAT has no place for (modes, owners, links -- see BFE_Open); from BFE_Open, a squashfs of a version other than 4.0 or a compression this build does not read (lz4, lzo); from BFE_ResizeFs, a filesystem it will not resize (bigalloc) or a new size it cannot express (too many blocks without 64bit, too many group descriptors without meta_bg); from the compressed-image reader (BFE_CopyRaw, BFE_ImageInfoGet), a zip method other than stored / deflate, or .xz headers or a filter this liblzma lacks
#define BFE_ERR_LINK_LOOP       29       // BFE_Extract following links (BFE_ExtractOptions.bFollowLinks): a link that leads back into a folder the copy is already inside, or a chain of more than BFE_LINK_HOPS_MAX links -- the link is SKIPPED with this status and the copy goes on

/*----------------------------------------------------------------------------*/
/* Entry types -- documentation table.  BFE_Entry.uiType and                  */
/* BFE_CopyEvent.uiType carry one of these.  The engine's own numbering: it   */
/* maps each from the inode's mode word (bifrost_engine.c), so nothing relies */
/* on these equalling ext4's directory-entry type byte, which they happen to. */
/* The ORDER does matter: bfe_copy.c's manifest type names are indexed by     */
/* these values.                                                              */
/*----------------------------------------------------------------------------*/
#define BFE_TYPE_UNKNOWN        0        // the type could not be determined
#define BFE_TYPE_FILE           1        // a regular file
#define BFE_TYPE_DIR            2        // a directory
#define BFE_TYPE_CHARDEV        3        // a character device node
#define BFE_TYPE_BLOCKDEV       4        // a block device node
#define BFE_TYPE_FIFO           5        // a named pipe
#define BFE_TYPE_SOCKET         6        // a unix socket
#define BFE_TYPE_SYMLINK        7        // a symbolic link (never followed by this engine)

/*----------------------------------------------------------------------------*/
/* Copy events -- documentation table.  BFE_CopyEvent.uiKind carries one.     */
/*----------------------------------------------------------------------------*/
#define BFE_EVENT_START         1        // an item is about to be copied; ullBytesTotal is its size (0 for a directory)
#define BFE_EVENT_PROGRESS      2        // more of the current file has been copied; ullBytesDone has grown
#define BFE_EVENT_DONE          3        // the item was copied and its metadata applied
#define BFE_EVENT_SKIPPED       4        // the item was NOT copied and that is not a failure; iStatus says why (BFE_ERR_TYPE, BFE_ERR_NAME, or BFE_ERR_EXISTS for the entry a MANIFEST_CLASH answered KEEP_MANIFEST left out)
#define BFE_EVENT_ERROR         5        // the item failed; iStatus is the BFE_ERR_* code; the copy goes on with the next item -- except for BFE_ERR_NO_MEMORY, which stops the whole copy there (a manifest that could not be written is reported this way too: the tree's image path, the manifest's host path)
#define BFE_EVENT_CONFLICT      6        // the destination FILE already exists; the reply decides (see below)
#define BFE_EVENT_MANIFEST_CLASH 7       // BFE_Extract of a tree with the manifest: the image folder's top holds an entry whose name is the manifest's own (_ext4meta.txt, letter case aside, as Windows folds it); raised ONCE, before anything is created; pszImagePath that entry, pwszHostPath where the manifest goes; the reply decides (see below)
#define BFE_EVENT_NOT_MANIFEST  8        // BFE_Import of a folder with the manifest option: its _ext4meta.txt does not begin with a manifest's first line, so it is imported as an ordinary file and restores nothing; raised ONCE, before anything is copied; pszImagePath the tree's destination, pwszHostPath that file; only CANCEL matters

/*----------------------------------------------------------------------------*/
/* Copy callback replies -- documentation table.  The value the callback      */
/* returns.  On every event except BFE_EVENT_CONFLICT and                     */
/* BFE_EVENT_MANIFEST_CLASH only CANCEL matters.  On a CONFLICT, OVERWRITE    */
/* replaces the destination, CANCEL stops the whole copy, and SKIP -- or      */
/* CONTINUE, which is read as the safe answer -- leaves the destination       */
/* alone.  On a MANIFEST_CLASH, KEEP_FILE extracts the entry and writes no    */
/* manifest, KEEP_MANIFEST writes the manifest and leaves the entry out       */
/* (SKIPPED, with its block in the manifest), and CANCEL stops before         */
/* anything is created; CONTINUE, or no callback, is read as KEEP_FILE -- the */
/* data asked for is copied, and the manifest, written only on request, is    */
/* the part given up.                                                         */
/*----------------------------------------------------------------------------*/
#define BFE_REPLY_CONTINUE      0        // carry on (on a CONFLICT: the same as SKIP; on a MANIFEST_CLASH: the same as KEEP_FILE)
#define BFE_REPLY_CANCEL        1        // stop the whole operation; the call returns BFE_ERR_CANCELLED
#define BFE_REPLY_SKIP          2        // CONFLICT only: keep the existing destination
#define BFE_REPLY_OVERWRITE     3        // CONFLICT only: replace the existing destination
#define BFE_REPLY_KEEP_FILE     4        // MANIFEST_CLASH only: extract the image's entry under that name; no manifest is written
#define BFE_REPLY_KEEP_MANIFEST 5        // MANIFEST_CLASH only: write the manifest; the image's entry is not extracted

/*----------------------------------------------------------------------------*/
/* Fixed sizes -- documentation table.                                        */
/*----------------------------------------------------------------------------*/
#define BFE_NAME_BYTES          768     // BFE_Entry.szName: the longest name either kind holds -- a FAT or exFAT long name of 255 UTF-16 units, 765 bytes of UTF-8 (an ext4 name is at most 255 bytes) -- plus the NUL, padded to 8
#define BFE_VOLUME_NAME_BYTES   40      // BFE_VolumeInfo.szVolumeName, BFE_MakeFsOptions.szLabel: the longest label any type holds -- an exFAT one of 11 UTF-16 units, or a FAT one of 11 code page 437 characters, up to 33 bytes of UTF-8 (an ext label is at most 16) -- plus the NUL, padded to 8
#define BFE_WRITE_BATCH_MAX_PIECES 128  // the most pieces BFE_WriteRawBatch takes: it tests every pair for overlap, so the count is bounded (a partition table is a handful)
#define BFE_LINK_HOPS_MAX       40      // BFE_Extract following links: the most links one target is resolved through (each link met on the way, at any level of its path, counts one), as Linux's own limit is -- past it the link is BFE_ERR_LINK_LOOP

/*----------------------------------------------------------------------------*/
/* Filesystem types for BFE_MakeFs -- documentation table.  None of the three */
/* ext types is given a journal; they differ in feature flags (see            */
/* BFE_MakeFs).  The three FAT types are made by FatFs's formatter, as one    */
/* volume over the whole range (no partition table of its own); FAT has no    */
/* journal.                                                                   */
/*----------------------------------------------------------------------------*/
#define BFE_FSTYPE_EXT2         2        // filetype, sparse_super, large_file
#define BFE_FSTYPE_EXT3         3        // the ext2 set plus dir_index
#define BFE_FSTYPE_EXT4         4        // the ext3 set plus extents, 64bit, uninit_bg, dir_nlink, extra_isize, huge_file
#define BFE_FSTYPE_FAT16        16       // FAT16, or FAT12 when the volume has too few clusters for FAT16 (FatFs decides by the cluster count, with its own bound, MAX_FAT12, which by its comment follows DOS and Windows rather than the FAT specification); two FATs
#define BFE_FSTYPE_FAT32        32       // FAT32; two FATs; refused when the size cannot hold the clusters FAT32 needs (about 32 MiB at the smallest)
#define BFE_FSTYPE_EXFAT        64       // exFAT; one FAT and its allocation bitmap, 64-bit file sizes

#define BFE_MAKEFS_MIN_BYTES    1048576  // the smallest image BFE_MakeFs will create: 1 MiB
#define BFE_MAKEFS_ZERO         0x0001   // BFE_MakeFsOptions.uiFlags, BFE_MakeFsAt: write zeros over the WHOLE range before formatting (slow on a large card; reported through the callback, and cancellable)

/*----------------------------------------------------------------------------*/
/* Devices -- documentation table.  A "device" is anything Windows opens as a */
/* disk or a volume: \\.\PhysicalDriveN, \\.\X:, a volume GUID path.  Every   */
/* function below that takes a path takes a file's or a device's alike.       */
/*----------------------------------------------------------------------------*/
#define BFE_NO_DISK             0xFFFFFFFFu  // BFE_DeviceInfo.uiDisk for a file, or a device that is not on a physical disk
#define BFE_MODEL_BYTES         64           // BFE_DeviceInfo.szModel: vendor and product text, NUL included
#define BFE_READ_RAW_MAX        16777216     // the most one BFE_ReadRaw may ask for, bytes (16 MiB)
#define BFE_WRITE_RAW_MAX       16777216     // the most one BFE_WriteRaw may write, bytes (16 MiB: a partition table is a few sectors; a bulk write is BFE_CopyRaw's job)
#define BFE_RAW_VERIFY          0x0001       // BFE_CopyRaw flag: after writing, read the destination back and compare it with the source
#define BFE_RAW_DECOMPRESS      0x0002       // BFE_CopyRaw flag: a FILE source in a compressed format (BFE_IMAGE_XZ / GZIP / ZSTD / ZIP, told by its first bytes) is decompressed as it is read; the offset and length then count the IMAGE's bytes.  A plain file or a device is read as it is
#define BFE_RAW_XZ_OUT          0x0004       // BFE_CopyRaw flag: the destination FILE is written compressed, as .xz (LZMA2, every processor, CRC64); BFE_ERR_ARG for a device destination
#define BFE_RAW_IN_PLACE        0x0008       // BFE_CopyRaw flag: write INTO an existing destination FILE -- it must exist, is neither created, truncated nor extended (the range must lie inside it, else BFE_ERR_NO_SPACE), and is KEPT, not removed, when the copy fails or is cancelled, as a device is (one partition of a disk image written in place); a device destination is unchanged by it; BFE_ERR_ARG with BFE_RAW_XZ_OUT
#define BFE_RAW_ZERO_SOURCE     0x0010       // BFE_CopyRaw flag: write ZEROS instead of reading a source -- pwszFrom and ullFromOffset are ignored (pwszFrom may be NULL), ullLength must be given (not 0), and the destination must be existing (BFE_RAW_IN_PLACE is implied for a file); the events keep the "copy" label; BFE_ERR_ARG with BFE_RAW_VERIFY, BFE_RAW_DECOMPRESS or BFE_RAW_XZ_OUT (an erase, like a format's zero-fill, is not read back)

/*----------------------------------------------------------------------------*/
/* Image file formats -- documentation table.  BFE_ImageInfo.uiKind carries   */
/* one; the format is told by the file's first bytes, never by its name.      */
/* bfe_compress.h's BFZ_KIND_* are defined AS these values, and               */
/* BFE_ImageInfoGet passes the reader's kind straight out -- so they are one  */
/* numbering, not two that map.                                               */
/*----------------------------------------------------------------------------*/
#define BFE_IMAGE_RAW           0                      // not one of the formats below: the file's bytes ARE the image (a device is always this)
#define BFE_IMAGE_XZ            1                      // .xz: FD 37 7A 58 5A 00; one or more streams, LZMA2 with or without a BCJ filter
#define BFE_IMAGE_GZIP          2                      // .gz: 1F 8B; one or more members
#define BFE_IMAGE_ZSTD          3                      // .zst: 28 B5 2F FD; one or more frames
#define BFE_IMAGE_ZIP           4                      // .zip: 50 4B 03 04; the archive's FIRST file is the image, stored or deflated (ZIP64 sizes read)
#define BFE_SIZE_UNKNOWN        0xFFFFFFFFFFFFFFFFull  // BFE_ImageInfo.ullImageBytes when the image's size cannot be told without decompressing all of it (gzip always; a .zst whose frames do not all record their sizes -- an .xz's index always records it, so an .xz either has its size or fails to open)

/*----------------------------------------------------------------------------*/
/* Filesystem check -- documentation table.  BFE_CheckFs runs e2fsprogs'      */
/* e2fsck (1.47.4, built into the DLL) over an image file or device.          */
/*----------------------------------------------------------------------------*/
#define BFE_CHECK_REPAIR        0x0001   // BFE_CheckFs flag: fix what is found, answering yes to every question (e2fsck -fy); without it nothing is written and every question is answered no (e2fsck -fn)

#define BFE_CHECK_EVENT_TEXT     1       // BFE_CheckEvent.uiKind: one line of e2fsck's report, as it would print it, without its line ending
#define BFE_CHECK_EVENT_PROGRESS 2       // BFE_CheckEvent.uiKind: a pass has advanced; uiPass, ullCurrent and ullTotal say how far (uiPass 0 with 0 / 0 marks the end of a pass)

/* The exit bits below are e2fsck's own FSCK_* values (e2fsprogs\e2fsck\       */
/* e2fsck.h), passed through unchanged; of its others, 2 (reboot: a mounted    */
/* root changed), 16 (usage) and 128 (library) are never set by this engine.   */
#define BFE_CHECK_OK            0        // BFE_CheckResult.uiExitCode: no errors
#define BFE_CHECK_CORRECTED     1        // ... errors were found and corrected (the image was changed)
#define BFE_CHECK_UNCORRECTED   4        // ... errors were found and left (a report-only run that found something, or a repair that could not finish)
#define BFE_CHECK_ERROR         8        // ... e2fsck itself failed (could not open the filesystem, ran out of memory, an unsupported feature); the text says why
#define BFE_CHECK_CANCELLED     32       // ... the callback asked for the check to stop

/*----------------------------------------------------------------------------*/
/* BFE_CheckEvent -- what a check tells its callback.  The text pointer is    */
/* valid ONLY for the duration of the callback.  32 bytes on x64, no padding. */
/*----------------------------------------------------------------------------*/
typedef struct BFE_CheckEvent
{
    uint64_t    ullCurrent;              // PROGRESS: how far the pass has got (inodes, directories or groups, by pass)
    uint64_t    ullTotal;                // PROGRESS: the pass's total in the same unit (0 when unknown)
    const char *pszText;                 // TEXT: the line, UTF-8 as e2fsck wrote it (names are the image's own bytes); NULL otherwise
    uint32_t    uiKind;                  // BFE_CHECK_EVENT_*
    uint32_t    uiPass;                  // PROGRESS: 1 to 5 (e2fsck's passes), or 0 at the end of one
} BFE_CheckEvent;

/*----------------------------------------------------------------------------*/
/* BFE_CheckResult -- what the check found, filled when BFE_CheckFs returns   */
/* BFE_OK.  40 bytes (two 8-byte and six 4-byte fields), no padding.  The     */
/* counts are e2fsck's own, as its closing "files / blocks" line prints them. */
/* gui\EngineNative.cs's ENG_CHECK_RESULT_BYTES is this size.                 */
/*----------------------------------------------------------------------------*/
typedef struct BFE_CheckResult
{
    uint64_t ullBlocksUsed;              // blocks in use
    uint64_t ullBlocksTotal;             // blocks in the filesystem
    uint32_t uiInodesUsed;               // inodes in use
    uint32_t uiInodesTotal;              // inodes in the filesystem
    uint32_t uiExitCode;                 // BFE_CHECK_* bits, exactly the exit status the e2fsck program would have returned
    uint32_t bModified;                  // 1 when the image was written to (only a BFE_CHECK_REPAIR run can set it)
    uint32_t bErrorsLeft;                // 1 when the filesystem still has errors after the run
    uint32_t uiReserved;                 // always 0
} BFE_CheckResult;

/*----------------------------------------------------------------------------*/
/* BFE_ResizeResult -- what BFE_ResizeFs did, filled whenever the filesystem  */
/* was opened (the block counts are equal when nothing was done).  24 bytes,  */
/* no padding.                                                                */
/*----------------------------------------------------------------------------*/
typedef struct BFE_ResizeResult
{
    uint64_t ullBlocksBefore;            // the filesystem's blocks when opened
    uint64_t ullBlocksAfter;             // its blocks now (equal to before: nothing to do, or it failed)
    uint32_t uiBlockSize;                // bytes per block
    uint32_t uiReserved;                 // always 0
} BFE_ResizeResult;

/*----------------------------------------------------------------------------*/
/* BFE_CapacityResult -- what BFE_TestCapacity found, filled when it returns  */
/* BFE_OK.  40 bytes, no padding.                                             */
/*----------------------------------------------------------------------------*/
typedef struct BFE_CapacityResult
{
    uint64_t ullReported;                // the device's length as Windows reports it: what the card claims
    uint64_t ullGood;                    // the bytes from the start that hold data, as found: equal to ullReported for a good card
    uint64_t ullFirstBad;                // the first byte found not to hold data (equal to ullReported when none was); for a wrapping card, where the addresses repeat
    uint32_t uiSamples;                  // test blocks written across the device (the refinement's single blocks not counted)
    uint32_t uiBadSamples;               // of those, how many did not read back as written
    uint32_t bWraps;                     // 1 when a block written high read back at a LOWER address: the card's addresses repeat (a counterfeit of another kind)
    uint32_t bCounterfeit;               // 1 when the device holds less than it reports (ullGood < ullReported, or bWraps) -- a genuine card with one bad stretch reads the same: the test cannot tell the two
} BFE_CapacityResult;

/*----------------------------------------------------------------------------*/
/* BFE_CheckCallback is called for every BFE_CHECK_EVENT_* of a check and     */
/* returns BFE_REPLY_CONTINUE, or BFE_REPLY_CANCEL to stop the check at the   */
/* next opportunity (the run then ends as e2fsck does on a cancel, with       */
/* BFE_CHECK_CANCELLED in the result).  It may be NULL: no text, no progress. */
/*----------------------------------------------------------------------------*/
typedef int (*BFE_CheckCallback)(const BFE_CheckEvent *pEvent, void *pvContext);

/*----------------------------------------------------------------------------*/
/* BFE_DeviceInfo -- what BFE_DeviceInfoGet reports about a path.  96 bytes,  */
/* no padding.  For a FILE: ullSize is its size, uiSectorBytes is 512, uiDisk */
/* is BFE_NO_DISK, every flag is 0 and szModel is "".                         */
/*----------------------------------------------------------------------------*/
typedef struct BFE_DeviceInfo
{
    uint64_t ullSize;                    // length in bytes (a device: of exactly what was opened -- the whole disk for \\.\PhysicalDriveN, the volume for \\.\X:)
    uint32_t uiSectorBytes;              // bytes per sector: the device's real figure (512 or 4096); 512 for a file
    uint32_t uiDisk;                     // the physical disk number a device is, or sits on; BFE_NO_DISK for a file
    uint32_t uiBus;                      // the device's bus as Windows numbers it (STORAGE_BUS_TYPE: 7 USB, 11 SATA, 12 SD, 13 MMC, 17 NVMe); 0 for a file
    uint32_t bDevice;                    // 1 when the path is a disk or volume, 0 when it is a file
    uint32_t bRemovable;                 // 1 when Windows reports removable media
    uint32_t bSystem;                    // 1 when the device is on the physical disk Windows is running from, or its disk cannot be told apart from it (as the write guards decide) -- such a device is never written
    char     szModel[BFE_MODEL_BYTES];   // "Vendor Product", UTF-8, trimmed; "" when unknown
} BFE_DeviceInfo;

/*----------------------------------------------------------------------------*/
/* BFE_ImageInfo -- what BFE_ImageInfoGet reports about an image file: its    */
/* format and the size of the image inside it.  24 bytes, no padding.         */
/*----------------------------------------------------------------------------*/
typedef struct BFE_ImageInfo
{
    uint64_t ullImageBytes;              // the image's own size -- the bytes a device receives -- or BFE_SIZE_UNKNOWN; for BFE_IMAGE_RAW the file's size
    uint64_t ullFileBytes;               // the file's size on disk (compressed, when it is)
    uint32_t uiKind;                     // BFE_IMAGE_*
    uint32_t uiReserved;                 // always 0
} BFE_ImageInfo;

/*----------------------------------------------------------------------------*/
/* BFE_MakeFsOptions -- what BFE_MakeFs creates.  64 bytes, no padding.       */
/*----------------------------------------------------------------------------*/
typedef struct BFE_MakeFsOptions
{
    uint64_t ullSizeBytes;                    // the image's size; rounded DOWN to a whole number of blocks (ext) or 512-byte sectors (FAT); at least BFE_MAKEFS_MIN_BYTES
    uint32_t uiFsType;                        // BFE_FSTYPE_*
    uint32_t uiBlockSize;                     // ext: 1024, 2048 or 4096; 0 to choose (1024 below 512 MiB, otherwise 4096, as mke2fs does -- except ext4 below 8 MiB, which gets 2048 because 1024 cannot be made there and is BFE_ERR_ARG if asked for).  FAT: the CLUSTER size in bytes, a power of two from the sector size up (FAT12 / 16 / 32 to 128 sectors, exFAT to 16 MiB), or 0 for FatFs's choice by the volume's size
    uint32_t bOverwrite;                      // 1: replace an existing file; 0: an existing file is BFE_ERR_EXISTS
    uint32_t uiFlags;                         // BFE_MAKEFS_* bits; BFE_MakeFsAt only (BFE_MakeFs ignores them: a new file is zeros already); 0 for none
    char     szLabel[BFE_VOLUME_NAME_BYTES];  // the volume label, UTF-8, NUL-terminated ("" for none).  ext: at most 16 bytes.  FAT12 / 16 / 32: at most 11 characters that code page 437 holds (accented letters too), none of + . , ; = [ ] / * : < > | \ " ? or a control character (stored in capitals in that code page, as FAT and Windows keep it).  exFAT: at most 11 UTF-16 units, none of / * : < > | \ " ? or a control character.  The field holds the longest of these (33 bytes of UTF-8) and its NUL
} BFE_MakeFsOptions;

/*----------------------------------------------------------------------------*/
/* BFE_Entry -- one directory entry's facts, everything a file-manager row    */
/* needs.  Filled by BFE_Stat and handed to the BFE_ListDir callback.         */
/* 824 bytes, no padding.  The field notes are ext2/3/4's; on a FAT or exFAT  */
/* session the facts FAT does not keep are made up, as BFE_Open says (the     */
/* type from FAT's directory bit, inode 0, owner 0, one link, one time); a    */
/* squashfs session's are its inodes' own, its one time (the modify time) in  */
/* all three time fields.                                                     */
/*----------------------------------------------------------------------------*/
typedef struct BFE_Entry
{
    uint64_t ullSize;                    // size in bytes (a symlink: the length of its target text)
    uint32_t uiInode;                    // inode number
    uint32_t uiType;                     // BFE_TYPE_*, taken from the inode's mode word
    uint32_t uiMode;                     // the 12 permission bits only (e.g. 0644); the type is uiType
    uint32_t uiUid;                      // owner user id (numeric; an image carries no names)
    uint32_t uiGid;                      // owner group id (numeric)
    uint32_t uiLinks;                    // hard-link count
    uint32_t uiAtime;                    // last access, UTC epoch seconds
    uint32_t uiMtime;                    // last content change, UTC epoch seconds
    uint32_t uiCtime;                    // last inode change, UTC epoch seconds
    uint32_t uiNameBytes;                // length of szName in bytes, without its NUL
    uint32_t uiAttributes;               // FAT and exFAT: the entry's BFE_ATTR_* bits as FAT stores them; ext2/3/4: 0
    uint32_t uiReserved;                 // always 0; keeps szName on an 8-byte boundary, the layout explicit
    char     szName[BFE_NAME_BYTES];     // the entry's name as stored: UTF-8 by convention, NUL-terminated
} BFE_Entry;

/*----------------------------------------------------------------------------*/
/* BFE_VolumeInfo -- facts about the open filesystem.  Filled by              */
/* BFE_VolumeInfoGet.  104 bytes, no padding.                                 */
/*----------------------------------------------------------------------------*/
typedef struct BFE_VolumeInfo
{
    uint64_t ullBlockCount;                        // total blocks in the filesystem
    uint64_t ullFreeBlocks;                        // free blocks, as the superblock records them
    uint32_t uiBlockSize;                          // bytes per block (1024, 2048 or 4096)
    uint32_t uiInodeCount;                         // total inodes
    uint32_t uiFreeInodes;                         // free inodes
    uint32_t uiFeatureCompat;                      // superblock compatible feature bits, raw
    uint32_t uiFeatureIncompat;                    // superblock incompatible feature bits, raw
    uint32_t uiFeatureRoCompat;                    // superblock read-only-compatible feature bits, raw
    uint32_t bMetadataCsum;                        // 1 when metadata_csum is on
    uint32_t bDirIndex;                            // 1 when dir_index is on (hashed directory lookups)
    uint32_t bReadOnly;                            // 1 when this session was opened read-only
    uint32_t bExternalJournal;                     // 1 when the journal is on another device (has_journal, no journal inode): BFE_Open refuses such a filesystem for writing (BFE_ERR_JOURNAL), read-only works; reserved and always 0 before
    uint32_t uiVolumeType;                         // BFE_VOLTYPE_*: ext2/3/4, FAT12, FAT16, FAT32, exFAT or squashfs
    uint32_t uiReserved;                           // always 0; keeps the label on an 8-byte boundary, the layout explicit
    char     szVolumeName[BFE_VOLUME_NAME_BYTES];  // the volume label, NUL-terminated ("" when unset)
} BFE_VolumeInfo;

/*----------------------------------------------------------------------------*/
/* BFE_CopyEvent -- what a copy tells its callback.  The two path pointers    */
/* are valid ONLY for the duration of the callback; copy them to keep them.   */
/* 48 bytes on x64, no padding.  BFE_CopyRaw, BFE_MakeFsAt's zero-fill and    */
/* BFE_TestCapacity reuse it: pszImagePath then carries a phase label         */
/* ("copy", "verify", "zero", "test" -- matched by gui\CopyForms.cs) and the  */
/* host path the destination or device; the test's counts are STEPS.          */
/*----------------------------------------------------------------------------*/
typedef struct BFE_CopyEvent
{
    uint64_t       ullBytesDone;         // bytes of the CURRENT file copied so far
    uint64_t       ullBytesTotal;        // size of the CURRENT file (0 for a directory; a symlink copied within the image reports its target's length)
    const char    *pszImagePath;         // the item's path inside the image, UTF-8 (a phase label for the raw copy, zero-fill and capacity test)
    const wchar_t *pwszHostPath;         // the item's path on the host, UTF-16; NULL when it has none (BFE_CopyWithin, a restored symlink, an item whose name cannot cross)
    uint32_t       uiKind;               // BFE_EVENT_*
    uint32_t       uiType;               // BFE_TYPE_* of the item
    int32_t        iStatus;              // BFE_ERR_* on ERROR and SKIPPED, otherwise BFE_OK
    uint32_t       uiReserved;           // always 0
} BFE_CopyEvent;

/*----------------------------------------------------------------------------*/
/* BFE_ExtractOptions -- how BFE_Extract treats metadata and symbolic links.  */
/* 16 bytes, no padding.                                                      */
/*----------------------------------------------------------------------------*/
typedef struct BFE_ExtractOptions
{
    uint32_t bWriteManifest;             // 1: record the Unix metadata NTFS cannot hold (a tree: <hostdir>\_ext4meta.txt; one file: <hostfile>.ext4meta)
    uint32_t bSetTimes;                  // 1: set each extracted FILE's access / modify times from its inode
    uint32_t bFollowLinks;               // 1: copy what each symbolic link points to, under the link's name, instead of skipping the link (see BFE_Extract); 0: links skipped, as before
    uint32_t uiReserved;                 // set to 0 (not read); makes the 16-byte layout explicit
} BFE_ExtractOptions;

/*----------------------------------------------------------------------------*/
/* BFE_ImportOptions -- the metadata BFE_Import gives what it creates.        */
/* 24 bytes.  The usual values are 0644 / 0755 / 0 / 0 / 1 / 1; passing NULL  */
/* instead gives 0644 / 0755 / 0 / 0 / 1 / 0 -- the manifest NOT used         */
/* (bfe_copy.c, BFE_Import).  The modes are used as given: a directory mode   */
/* without its execute bits makes directories that cannot be entered.         */
/*----------------------------------------------------------------------------*/
typedef struct BFE_ImportOptions
{
    uint32_t uiFileMode;                 // permission bits for an imported file with no manifest record (e.g. 0644)
    uint32_t uiDirMode;                  // permission bits for an imported directory with no manifest record (e.g. 0755, whose execute bits keep it traversable)
    uint32_t uiUid;                      // owner for anything with no manifest record (0 = root)
    uint32_t uiGid;                      // group for anything with no manifest record (0 = root)
    uint32_t bCopyMtime;                 // 1: give each item the host file's modify time when no manifest record supplies one
    uint32_t bUseManifest;               // 1: where a manifest written by BFE_Extract is present, restore mode / uid / gid / times from it and re-create the symlinks it recorded
} BFE_ImportOptions;

/*----------------------------------------------------------------------------*/
/* BFE_RawPiece -- one piece of a partition table for BFE_WriteRawBatch:      */
/* where it goes and the bytes that go there.  24 bytes on x64, with no       */
/* padding: the offset at 0, the data pointer at 8, the byte count at 16 and  */
/* the reserved word at 20 -- mirrored by hand, field for field, as           */
/* RWP_stRawPiece in gui\EngineNative.cs, so a change here is a change there  */
/* and a new BFE_API_VERSION.  The caller keeps the array and every buffer it */
/* points to readable and unchanged until the call returns -- documentation   */
/* table.                                                                     */
/*----------------------------------------------------------------------------*/
typedef struct BFE_RawPiece
{
    uint64_t ullOffset;                  // byte offset in the whole file or device, not from a partition's start
    const void *pvBytes;                 // the caller's bytes, not copied until written; never NULL
    uint32_t cbBytes;                    // 1 to BFE_WRITE_RAW_MAX; a whole number of sectors on a device
    uint32_t uiReserved;                 // must be 0 (refused otherwise); it makes the 24-byte layout explicit
} BFE_RawPiece;

/*----------------------------------------------------------------------------*/
/* Callback types.                                                            */
/*                                                                            */
/* BFE_ListCallback is called once per directory entry by BFE_ListDir; a      */
/* non-zero return stops the listing early (the call still returns BFE_OK).   */
/* BFE_CopyCallback is called for every BFE_EVENT_* of a copy and returns a   */
/* BFE_REPLY_* value.  pvContext is the pointer the caller passed in,         */
/* unchanged.  Either callback may be NULL only where the function says so.   */
/*----------------------------------------------------------------------------*/
typedef int (*BFE_ListCallback)(const BFE_Entry *pEntry, void *pvContext);
typedef int (*BFE_CopyCallback)(const BFE_CopyEvent *pEvent, void *pvContext);

/*-----------------------------------------------------------------------------*/
/* Session.                                                                    */
/*                                                                             */
/* BFE_ApiVersion    : returns BFE_API_VERSION as built into the DLL (the one  */
/*                     function that does not return a status).                */
/* BFE_Open          : open an image file OR A DEVICE.  ullOffset / ullLength  */
/*                     select a byte range inside it -- a partition in a raw   */
/*                     disk dump, or on a card -- and must be multiples of     */
/*                     512; pass 0 / 0 for a file that IS the filesystem.      */
/*                     bReadOnly = 1 opens the file or device itself           */
/*                     read-only and refuses every modifying call.  A device   */
/*                     needs Administrator to open for I/O at all.  A device   */
/*                     opened for WRITING is refused with BFE_ERR_SYSTEM_DISK  */
/*                     when it is on the disk Windows runs from (or its disk   */
/*                     cannot be told), and otherwise has every volume on its  */
/*                     disk locked and dismounted for the whole session        */
/*                     (BFE_ERR_VOLUME_LOCK when one cannot be) -- Windows     */
/*                     refuses raw writes under a mounted volume, and the      */
/*                     lock is what keeps it from remounting mid-session.      */
/*                     A filesystem whose journal is on another device (an     */
/*                     external journal) cannot have it replayed or written,   */
/*                     so a writable open of it is BFE_ERR_JOURNAL; it opens   */
/*                     read-only, with BFE_VolumeInfo.bExternalJournal set.    */
/*                     A range that holds no ext2/3/4 filesystem is tried as   */
/*                     FAT12, FAT16, FAT32 or exFAT (FatFs), read-only or      */
/*                     writable as asked (a writable device's volumes locked   */
/*                     as for ext4).  It has no journal: FatFs writes each     */
/*                     change through at once, so BFE_Flush has nothing to     */
/*                     do.  It answers every session call but BFE_SetMode,     */
/*                     BFE_SetOwner, BFE_HardLink, BFE_SymLink and             */
/*                     BFE_ReadLink, which are BFE_ERR_UNSUPPORTED (FAT has    */
/*                     no modes, owners or links); BFE_SetTimes keeps only     */
/*                     the modify time, and a copy's or an import's mode,      */
/*                     owner and other times go nowhere.  Its entries are      */
/*                     files and directories only, with a made-up mode         */
/*                     (0644 / 0755, the write bits gone when FAT marks it     */
/*                     read-only), owner and group 0, one link, inode 0, and   */
/*                     its one time -- FAT's local modify time, turned into    */
/*                     UTC through the host's zone -- in all three time        */
/*                     fields.                                                 */
/*                     A range that begins with a squashfs magic is mounted as */
/*                     squashfs (squashfuse, version 4.0; gzip, xz, lzma and   */
/*                     zstd compression), READ-ONLY ONLY: a writable open is   */
/*                     BFE_ERR_READ_ONLY, and so is every modifying call of    */
/*                     its session.  It keeps Unix facts as ext2/3/4 does --   */
/*                     modes, owners, links, device nodes -- with one time,    */
/*                     its modify time, in all three time fields.  Another     */
/*                     version or a compression this build lacks (lz4, lzo)    */
/*                     is BFE_ERR_UNSUPPORTED; a damaged one BFE_ERR_MOUNT.    */
/* BFE_Close         : flush and close the image.                              */
/* BFE_Flush         : commit everything a writable session has changed to     */
/*                     the image or device now, so a crash after it loses      */
/*                     nothing done before it (BFE_OK and nothing to do on a   */
/*                     read-only session).  BFE_ERR_VERIFY when a device did   */
/*                     not keep a write.  BFE_ERR_JOURNAL when the journal     */
/*                     cannot be checkpointed or restarted: no journal runs    */
/*                     then, so the session takes no more changes -- every     */
/*                     modifying call is refused with BFE_ERR_JOURNAL -- while */
/*                     it stays open for reading and copying out; BFE_Close    */
/*                     reports BFE_ERR_JOURNAL again (the image wants a        */
/*                     check), and later flushes have nothing to commit.       */
/* BFE_VolumeInfoGet : fill a BFE_VolumeInfo.  On a FAT or exFAT session its   */
/*                     blocks are the clusters (the block size the cluster's   */
/*                     bytes), the inode counts and the feature words and      */
/*                     flags are 0, and the volume's label is given whole (the */
/*                     field holds the longest label any type keeps).  On a    */
/*                     squashfs session the blocks are the image's used bytes  */
/*                     in its block size, the inode count its own, nothing     */
/*                     free, no label, no feature words, and bReadOnly 1.      */
/* BFE_LastErrorText : copy the last failure's detail line (UTF-8) into the    */
/*                     caller's buffer; BFE_ERR_BUFFER if it does not fit.     */
/* BFE_MakeFs        : create a NEW image file of the given size holding an    */
/*                     empty filesystem -- ext2/3/4 (a root directory and      */
/*                     lost+found, with NO journal) or FAT12/16/32 / exFAT     */
/*                     (an empty root, its label if one was given).  Allowed   */
/*                     only while no image is open; the new image is not       */
/*                     opened.  A failure leaves no file behind.  A FAT size   */
/*                     or cluster size FatFs's formatter cannot lay out is     */
/*                     BFE_ERR_ARG, and so is a label the type cannot hold     */
/*                     (refused before anything is written).                   */
/* BFE_MakeFsAt      : format a byte RANGE of an EXISTING file or device -- a  */
/*                     partition of a card, an eMMC or a disk image -- as the  */
/*                     same empty filesystem, in place (a FAT one at the       */
/*                     device's own sector size).  ullOffset and               */
/*                     ullLength are as for BFE_Open (multiples of the sector; */
/*                     length 0 means "to the end"); the options' size and     */
/*                     overwrite fields are not used, the range being the      */
/*                     size and overwritten by definition.  The first 4096     */
/*                     bytes of the range are zeroed first, so a signature of  */
/*                     whatever was there does not survive beside the new      */
/*                     superblock -- or, with BFE_MAKEFS_ZERO in the options'  */
/*                     uiFlags, the WHOLE range is, in 4 MiB writes reported   */
/*                     through pfnEvent like a copy (one START with the        */
/*                     range's length, PROGRESS per write, DONE; pszImagePath  */
/*                     "zero", pwszHostPath the path), where a CANCEL reply    */
/*                     stops before anything is formatted and returns          */
/*                     BFE_ERR_CANCELLED -- the range then holds zeros up to   */
/*                     where it stopped and what was there after that.         */
/*                     pfnEvent may be NULL (no progress, no cancel).  A       */
/*                     device gets the guards a writable BFE_Open gives it:    */
/*                     BFE_ERR_SYSTEM_DISK, and the volumes locked for the     */
/*                     duration (BFE_ERR_VOLUME_LOCK).                         */
/*                     Allowed only while no image is open; the new            */
/*                     filesystem is not opened -- BFE_Open it at the same     */
/*                     offset and length.  A failure once formatting began     */
/*                     leaves the range as it was left: nothing is restored.   */
/*-----------------------------------------------------------------------------*/
BFE_API int BFE_ApiVersion(void);
BFE_API int BFE_Open(const wchar_t *pwszImage, uint64_t ullOffset, uint64_t ullLength, int bReadOnly);
BFE_API int BFE_Close(void);
BFE_API int BFE_Flush(void);
BFE_API int BFE_VolumeInfoGet(BFE_VolumeInfo *pInfo);
BFE_API int BFE_LastErrorText(char *pszBuffer, size_t cbBuffer);
BFE_API int BFE_MakeFs(const wchar_t *pwszImage, const BFE_MakeFsOptions *pOptions);
BFE_API int BFE_MakeFsAt(const wchar_t *pwszPath, uint64_t ullOffset, uint64_t ullLength, const BFE_MakeFsOptions *pOptions,
                         BFE_CopyCallback pfnEvent, void *pvContext);

/*----------------------------------------------------------------------------*/
/* Browse (allowed on a read-only session).                                   */
/*                                                                            */
/* BFE_ListDir  : call pfnEntry once per entry of a directory, "." and ".."   */
/*                excluded, in on-disk order.  On a FAT session a long name   */
/*                of up to 255 characters (765 bytes of UTF-8) is listed      */
/*                whole: BFE_NAME_BYTES holds it.                             */
/* BFE_Stat     : fill a BFE_Entry for one path (the name is its last         */
/*                component; "/" for the root).  A symlink is NOT followed.   */
/*                On a FAT session the facts are made up as BFE_Open says;    */
/*                the root is a directory with no time; a name is matched     */
/*                letter case aside, as FAT does.                             */
/* BFE_ReadLink : copy a symlink's target text (UTF-8, NUL-terminated) into   */
/*                the caller's buffer.                                        */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_ListDir(const char *pszImageDir, BFE_ListCallback pfnEntry, void *pvContext);
BFE_API int BFE_Stat(const char *pszImagePath, BFE_Entry *pEntry);
BFE_API int BFE_ReadLink(const char *pszImagePath, char *pszBuffer, size_t cbBuffer);

/*----------------------------------------------------------------------------*/
/* Edit (BFE_ERR_READ_ONLY on a read-only session).                           */
/*                                                                            */
/* BFE_MakeDir    : create one directory; the parent must exist               */
/*                  (BFE_ERR_NOT_FOUND, BFE_ERR_NOT_DIR when it is not a      */
/*                  directory -- nothing is made either way, and no missing   */
/*                  level above it).  BFE_SymLink's parent likewise.          */
/* BFE_Remove     : remove one file or link (not a directory).                */
/* BFE_RemoveDir  : remove a directory ONLY if it is empty; never the root    */
/*                  (BFE_ERR_ARG).                                            */
/* BFE_RemoveTree : remove a directory and everything under it, entry by      */
/*                  entry: pfnEvent (may be NULL) gets a BFE_EVENT_DONE       */
/*                  after each entry removed -- pszImagePath the entry,       */
/*                  uiType its type, ullBytesDone the count of entries so     */
/*                  far, the top directory last -- and a BFE_REPLY_CANCEL     */
/*                  stops before the next (BFE_ERR_CANCELLED: what was        */
/*                  removed stays removed, the rest stays).                   */
/* BFE_Rename     : rename or move an entry within the image.  An existing    */
/*                  destination is refused (BFE_ERR_EXISTS), and so is a      */
/*                  directory moved into itself (BFE_ERR_ARG).  On a FAT      */
/*                  session names match letter case aside and without their   */
/*                  trailing spaces and dots, so a new spelling that differs  */
/*                  only in those (any letter's case, as FatFs folds it) is   */
/*                  the same entry, renamed in place.                         */
/* BFE_SetMode    : set the 12 permission bits; the type bits are kept.       */
/* BFE_SetOwner   : set the numeric owner and group.                          */
/* BFE_SetTimes   : set the access and modify times (UTC epoch seconds).      */
/* BFE_HardLink   : give an existing file a second name.                      */
/* BFE_SymLink    : create a symbolic link whose text is pszTarget, which     */
/*                  must be shorter than one block (BFE_ERR_ARG otherwise).   */
/*                                                                            */
/* Times, here and in the copies below: whatever is created gets all three    */
/* (lwext4 itself sets none), its inode-change time always the present; and   */
/* SetMode, SetOwner, SetTimes, Rename (the moved entry) and HardLink (the    */
/* file) set the entry's inode-change time to the present, as Linux does.     */
/* And as on Linux, a directory that gains or loses a name -- a create, a     */
/* remove, either side of a rename, a hard link -- has its modify and change  */
/* times set to the present (a copied or imported folder's own times are set  */
/* last, after its contents, so they end as the source's).                    */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_MakeDir(const char *pszImageDir);
BFE_API int BFE_Remove(const char *pszImagePath);
BFE_API int BFE_RemoveDir(const char *pszImageDir);
BFE_API int BFE_RemoveTree(const char *pszImageDir, BFE_CopyCallback pfnEvent, void *pvContext);
BFE_API int BFE_Rename(const char *pszImageFrom, const char *pszImageTo);
BFE_API int BFE_SetMode(const char *pszImagePath, uint32_t uiMode);
BFE_API int BFE_SetOwner(const char *pszImagePath, uint32_t uiUid, uint32_t uiGid);
BFE_API int BFE_SetTimes(const char *pszImagePath, uint32_t uiAtime, uint32_t uiMtime);
BFE_API int BFE_HardLink(const char *pszImageExisting, const char *pszImageNewName);
BFE_API int BFE_SymLink(const char *pszTarget, const char *pszImageLink);

/*----------------------------------------------------------------------------*/
/* Copy.  Both are recursive when the source is a directory, both report      */
/* through pfnEvent (which may be NULL: no progress, conflicts are skipped),  */
/* and both return BFE_OK, BFE_ERR_CANCELLED, BFE_ERR_PARTIAL, or the error   */
/* that stopped them before the first item -- or BFE_ERR_NO_MEMORY when an    */
/* item ran out of memory part-way: that item's ERROR event is raised, the    */
/* copy stops there (nothing after it is tried, as for a cancel, a manifest   */
/* being written thrown away), and the detail line names where.  So does      */
/* BFE_CopyWithin.                                                            */
/*                                                                            */
/* BFE_Extract : copy pszImagePath OUT to pwszHostPath, which names the       */
/*               destination ITEM itself, not the folder to put it in.  File  */
/*               bytes are exact.  A symlink, device, fifo or socket cannot   */
/*               exist on NTFS and is SKIPPED (and recorded in the manifest)  */
/*               -- a symlink unless links are followed (below) --,           */
/*               as is a name Windows cannot hold.  Extract works on a        */
/*               read-only session.  A manifest is written under a temporary  */
/*               name beside its final one and renamed into place only once   */
/*               it is complete, so one already there is replaced only by a   */
/*               whole new one: a manifest that cannot be finished or put in  */
/*               place is removed, leaves any earlier one as it was, and is   */
/*               an ERROR (the run BFE_ERR_PARTIAL); a cancelled run leaves   */
/*               the earlier one too.  A tree whose top holds an entry named  */
/*               as the manifest is asked about first                         */
/*               (BFE_EVENT_MANIFEST_CLASH).  On a FAT session NO manifest    */
/*               is written, whatever the options say (FAT has no Unix facts  */
/*               to record), so no clash is asked either; bSetTimes still     */
/*               stamps each file with its FAT time.                          */
/*               FOLLOWING LINKS (bFollowLinks): a symbolic link -- the top   */
/*               item or one met in the walk -- is copied as what it points   */
/*               to, under the link's own name: a file's bytes, or a folder   */
/*               and everything under it, the data duplicated where several   */
/*               links reach it.  The target is resolved inside the open      */
/*               filesystem as Linux would: an absolute target from its root, */
/*               a relative one from the link's folder, every folder on the   */
/*               way that is itself a link followed too, ".." at the root     */
/*               staying there, at most BFE_LINK_HOPS_MAX links in all.  A    */
/*               target not in this filesystem (missing, or another mount's   */
/*               such as /proc) is SKIPPED, BFE_ERR_NOT_FOUND (or NOT_DIR); a */
/*               folder target the copy is already inside, or too long a      */
/*               chain, is SKIPPED, BFE_ERR_LINK_LOOP; the copy goes on       */
/*               either way.  The manifest records a followed link as its     */
/*               target's facts under the link's path -- a file or a folder,  */
/*               so an import gives a real file, not a link -- and a skipped  */
/*               one as the link it is.  The events name the TARGET's image   */
/*               paths for what is copied, the link's for a skip.  FAT has    */
/*               no links, so the option changes nothing there.               */
/* BFE_Import  : copy pwszHostPath IN to pszImagePath, which likewise names   */
/*               the destination item.  A host symbolic link is followed.     */
/*               A tree's _ext4meta.txt is its manifest only when its first   */
/*               line is the one BFE_Extract writes, or the one other first   */
/*               line the same format is accepted with; otherwise it is an    */
/*               ordinary file, imported as one (BFE_EVENT_NOT_MANIFEST).     */
/*               A tree's manifest may name only places inside the tree: a    */
/*               record whose path is not "." or a relative path of plain     */
/*               names (none empty, "." or "..") is BFE_ERR_IO_HOST, before   */
/*               anything is copied.  A record's mode, owner and group are    */
/*               applied whenever it is for the same kind of item; a FILE     */
/*               record's times only while the host file still has the size   */
/*               and modify time the record holds (a file changed since its   */
/*               extract gets the host's modify time, as with no record).  A  */
/*               link's recorded TARGET is restored as written.               */
/*                                                                            */
/* In both, an existing destination DIRECTORY is merged into; an existing     */
/* destination FILE raises BFE_EVENT_CONFLICT.  BFE_Import's destination      */
/* must be in a folder that exists: a missing one is BFE_ERR_NOT_FOUND, one   */
/* that is not a directory BFE_ERR_NOT_DIR, before anything is copied -- no   */
/* missing folder above the destination is ever made (the copied item         */
/* itself, a folder and all under it, is made as always).                     */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_Extract(const char *pszImagePath, const wchar_t *pwszHostPath,
                        const BFE_ExtractOptions *pOptions,
                        BFE_CopyCallback pfnEvent, void *pvContext);
BFE_API int BFE_Import(const wchar_t *pwszHostPath, const char *pszImagePath,
                       const BFE_ImportOptions *pOptions,
                       BFE_CopyCallback pfnEvent, void *pvContext);

/*----------------------------------------------------------------------------*/
/* Copy inside the image (BFE_ERR_READ_ONLY on a read-only session).          */
/*                                                                            */
/* BFE_CopyWithin : copy pszImageFrom to pszImageTo, which names the          */
/*                  destination ITEM, in the same image; recursive when the   */
/*                  source is a directory.  The copy keeps its source's mode, */
/*                  owner, group, modify and access times.  A symbolic link   */
/*                  is copied as a link (never followed); a device, fifo or   */
/*                  socket is SKIPPED (BFE_ERR_TYPE); two names of one        */
/*                  hard-linked file become two separate files.  It reports   */
/*                  through pfnEvent exactly as BFE_Import does -- an         */
/*                  existing destination DIRECTORY is merged into, an         */
/*                  existing destination FILE or LINK raises                  */
/*                  BFE_EVENT_CONFLICT -- except that each event's            */
/*                  pszImagePath is the DESTINATION path and pwszHostPath is  */
/*                  NULL.  Copying an entry onto itself, a directory to a     */
/*                  path inside itself, or an entry onto a directory that     */
/*                  holds it (the root included: /a/a onto /a, whose merge    */
/*                  would write back into the source still being read) is     */
/*                  BFE_ERR_ARG -- on FAT in any spelling FAT takes as the    */
/*                  same (letter case, trailing spaces and dots).  The        */
/*                  destination's folder must exist, as for BFE_Import        */
/*                  (BFE_ERR_NOT_FOUND / BFE_ERR_NOT_DIR, nothing copied).    */
/*                  Returns as the two copies above do.  (To MOVE inside the  */
/*                  image, use BFE_Rename: nothing is copied.)                */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_CopyWithin(const char *pszImageFrom, const char *pszImageTo,
                           BFE_CopyCallback pfnEvent, void *pvContext);

/*----------------------------------------------------------------------------*/
/* Devices and raw images.  None of these needs an image to be open, and all  */
/* six may be called while one is -- they open their own paths and touch      */
/* nothing lwext4 holds: BFE_WriteRaw and BFE_CopyRaw refuse (BFE_ERR_ARG) a  */
/* destination that is the open session's own image file or disk, and         */
/* BFE_CopyRaw a destination that is its own source -- each by identity (the  */
/* file's volume and index, a device's disk), whatever path names it.         */
/*                                                                            */
/* BFE_DeviceInfoGet : fill a BFE_DeviceInfo for a file or device path.  A    */
/*                     device's facts need NO Administrator (the length of a  */
/*                     VOLUME does, and without it the disk's length is       */
/*                     reported instead), so a device list can be shown from  */
/*                     an ordinary program.  Fails BFE_ERR_IMAGE_OPEN when    */
/*                     the path cannot be opened at all.                      */
/* BFE_ReadRaw       : read cbBytes at a byte offset from a file or device    */
/*                     into the caller's buffer, up to BFE_READ_RAW_MAX, any  */
/*                     offset and length (the sector rules are met inside).   */
/*                     For reading a card's partition table.  Needs           */
/*                     Administrator on a device.  BFE_ERR_ARG for a bad      */
/*                     size, BFE_ERR_IMAGE_OPEN, BFE_ERR_IO_IMAGE for a read  */
/*                     past the end or a failed one.                          */
/* BFE_WriteRaw      : write cbBytes from the caller's buffer at a byte       */
/*                     offset of an EXISTING file or device, up to            */
/*                     BFE_WRITE_RAW_MAX -- for writing a partition table.    */
/*                     The range must lie inside what is there: a file is     */
/*                     never created or extended by it.  On a device the      */
/*                     offset and length must be sector multiples (nothing    */
/*                     is read to fill a partial sector), the system disk is  */
/*                     refused (BFE_ERR_SYSTEM_DISK), its disk's volumes are  */
/*                     locked for the write (BFE_ERR_VOLUME_LOCK), the write  */
/*                     is flushed to the media, and Windows is asked to       */
/*                     re-read the partition table afterwards.  Needs         */
/*                     Administrator on a device.  BFE_ERR_ARG for a bad      */
/*                     size or an unaligned device range, BFE_ERR_IMAGE_OPEN  */
/*                     when the path cannot be opened for writing,            */
/*                     BFE_ERR_IO_IMAGE for a range past the end,             */
/*                     BFE_ERR_IO_HOST for a failed write or flush.           */
/* BFE_CopyRaw       : copy ullLength bytes from byte ullFromOffset of one    */
/*                     path to byte ullToOffset of another -- a device to an  */
/*                     image file, an image file to a device, or file to      */
/*                     file -- reporting through pfnEvent like the copies     */
/*                     above (one START with the total, PROGRESS per chunk,   */
/*                     DONE; a second START / DONE pair for the read-back     */
/*                     when BFE_RAW_VERIFY is set; each event's pwszHostPath  */
/*                     is the destination and pszImagePath is "copy" or       */
/*                     "verify").  ullLength 0 means "to the end of the       */
/*                     source".  A DEVICE SOURCE's offset and length must be  */
/*                     sector multiples; a file source may be any length, and */
/*                     its last partial sector is padded with zeros when the  */
/*                     destination is a device.  A device destination: the    */
/*                     offset must be a sector multiple, the system disk is   */
/*                     refused (BFE_ERR_SYSTEM_DISK), a copy that would run   */
/*                     off its end is refused before anything is written      */
/*                     (BFE_ERR_NO_SPACE), its disk's volumes are locked for  */
/*                     the copy (BFE_ERR_VOLUME_LOCK), every write is made    */
/*                     real with a flush, and Windows is asked to re-read its */
/*                     partition table afterwards.  A file destination is     */
/*                     created, or truncated if it exists, and is removed     */
/*                     again on failure or cancel.  Returns BFE_OK,           */
/*                     BFE_ERR_CANCELLED, BFE_ERR_VERIFY, or the error that   */
/*                     stopped it (BFE_ERR_IO_IMAGE reading the source,       */
/*                     BFE_ERR_IO_HOST writing the destination).              */
/*                     COMPRESSED IMAGES: with BFE_RAW_DECOMPRESS a .xz,      */
/*                     .gz, .zst or .zip FILE source is decompressed as it is */
/*                     read (the offset and length count the image's bytes,   */
/*                     and the verify pass decompresses it again); with       */
/*                     BFE_RAW_XZ_OUT the destination file is written as .xz  */
/*                     (and the verify pass decompresses what was written).   */
/*                     When the image's size cannot be told up front (see     */
/*                     BFE_ImageInfoGet), a length of 0 copies to the end of  */
/*                     the image, the room on a device is checked chunk by    */
/*                     chunk instead (BFE_ERR_NO_SPACE before the chunk that  */
/*                     would not fit), and the copy's PROGRESS events count   */
/*                     the compressed FILE's bytes consumed against its size  */
/*                     -- the one fraction that is known -- while START and   */
/*                     DONE carry the image's bytes as usual (START 0 when    */
/*                     unknown).  A length longer than the image turns out to */
/*                     be is BFE_ERR_IO_IMAGE when the image ends.  A zip     */
/*                     image is judged against its directory when its end is  */
/*                     read -- its size and its CRC-32 -- and a copy that     */
/*                     reaches the end of one that disagrees fails with       */
/*                     BFE_ERR_IO_IMAGE (the file removed, as for any         */
/*                     failure); a copy of only part of a zip image cannot be */
/*                     checked that way, the CRC covering the whole.          */
/*                     With BFE_RAW_XZ_OUT, ullToOffset is ignored --         */
/*                     the .xz is written from the file's start -- and not    */
/*                     refused.  THE DEFECT EXISTS AND IS INTENTIONALLY       */
/*                     DEFERRED: the program's raw copies write their file    */
/*                     from offset 0.                                         */
/*                     IN PLACE (BFE_RAW_IN_PLACE): a FILE destination must   */
/*                     already exist and is written where it stands -- never  */
/*                     created, truncated or extended (a range past its end   */
/*                     is BFE_ERR_NO_SPACE before anything is written) and    */
/*                     never removed: a failure or cancel leaves what was     */
/*                     written, exactly as on a device.  This is how one      */
/*                     partition of a disk image FILE is written.  ZEROS      */
/*                     (BFE_RAW_ZERO_SOURCE): no source is opened; ullLength  */
/*                     bytes of zeros are written at ullToOffset of an        */
/*                     existing file or device (in place, as above) -- one    */
/*                     partition's erase.  Neither is read back.              */
/* BFE_ImageInfoGet  : fill a BFE_ImageInfo for a file (or device) path: its  */
/*                     format, told by its first bytes, and the size of the   */
/*                     image inside it -- from the zip's directory, the .xz   */
/*                     index, or the zstd frame headers; BFE_SIZE_UNKNOWN     */
/*                     for gzip, and for a .zst whose frames do not record    */
/*                     their sizes.  Reads only the headers and indexes, not  */
/*                     the data, so a damaged image can still say yes here.   */
/*                     A .xz whose index cannot be read (cut short, or not    */
/*                     .xz after all) is BFE_ERR_IO_IMAGE, which is the early */
/*                     warning a truncated download needs.  A device is       */
/*                     BFE_IMAGE_RAW at its own length.                       */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_DeviceInfoGet(const wchar_t *pwszPath, BFE_DeviceInfo *pInfo);
BFE_API int BFE_ReadRaw(const wchar_t *pwszPath, uint64_t ullOffset, void *pvBuffer, uint32_t cbBytes);
BFE_API int BFE_WriteRaw(const wchar_t *pwszPath, uint64_t ullOffset, const void *pvBuffer, uint32_t cbBytes);

/*----------------------------------------------------------------------------*/
/* BFE_WriteRawBatch:                                                         */
/*                                                                            */
/* Write a partition table -- several separate byte ranges -- into an         */
/* EXISTING file or device as one operation: one open handle and, on a        */
/* device, the disk's volumes locked from before the first piece until after  */
/* the flush and the request that Windows re-read the table, so no volume is  */
/* remounted over a table half written.  The guards are BFE_WriteRaw's (the   */
/* system disk refused, the open session's own file or disk refused by        */
/* identity).                                                                 */
/*                                                                            */
/* The whole set is checked before any of it is written: every buffer         */
/* present, every length 1 to BFE_WRITE_RAW_MAX, every range inside the       */
/* target and on a device's sector grid, and no two ranges overlapping.  The  */
/* engine cannot check that a buffer is really readable for its length --     */
/* that is the caller's to guarantee.  The pieces are written in the order    */
/* given.  Once writing has begun nothing is undone: a failure stops at that  */
/* piece and leaves the table incomplete on the disk, and a power cut         */
/* part-way does the same.                                                    */
/*                                                                            */
/* Arguments:                                                                 */
/*     pwszPath : the file or device; it must exist, and is never created,    */
/*                truncated or extended.                                      */
/*     pPieces  : the pieces (BFE_RawPiece above); the array and every buffer */
/*                stay readable and unchanged until the call returns.         */
/*     nPieces  : how many, 1 to BFE_WRITE_BATCH_MAX_PIECES.                  */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK only once every piece is written and the flush has        */
/*           confirmed it.  BFE_ERR_ARG for a bad, overlapping or off-grid    */
/*           piece or the session's own target; BFE_ERR_IO_IMAGE for a range  */
/*           past the end; BFE_ERR_IMAGE_OPEN, BFE_ERR_SYSTEM_DISK,           */
/*           BFE_ERR_NO_MEMORY, BFE_ERR_VOLUME_LOCK; BFE_ERR_IO_HOST for a    */
/*           failed write or flush; BFE_ERR_BUSY (the running call's error    */
/*           text is left as it is).                                          */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_WriteRawBatch(const wchar_t *pwszPath, const BFE_RawPiece *pPieces, uint32_t nPieces);
BFE_API int BFE_CopyRaw(const wchar_t *pwszFrom, uint64_t ullFromOffset,
                        const wchar_t *pwszTo, uint64_t ullToOffset,
                        uint64_t ullLength, uint32_t uiFlags,
                        BFE_CopyCallback pfnEvent, void *pvContext);
BFE_API int BFE_ImageInfoGet(const wchar_t *pwszPath, BFE_ImageInfo *pInfo);

/*------------------------------------------------------------------------------*/
/* Filesystem check.  Allowed only while no image is open (the check opens      */
/* the file or device itself, as BFE_Open would: the same path, offset and      */
/* length rules, read-only unless BFE_CHECK_REPAIR is given, a device needing   */
/* Administrator and, for a repair, the system-disk refusal and the volume      */
/* locks).                                                                      */
/*                                                                              */
/* BFE_CheckFs : run e2fsck over the filesystem -- a full check, never          */
/*               skipped for a clean flag (e2fsck -f) -- reporting its text     */
/*               line by line and its progress through pfnEvent, and the        */
/*               verdict in pResult.  Returns BFE_OK when e2fsck ran, WHATEVER  */
/*               it found: the result's uiExitCode is the verdict, and a        */
/*               filesystem e2fsck could not open or gave up on is              */
/*               BFE_CHECK_ERROR there, with the reason in the text.  Returns   */
/*               an error only when the check could not be started:             */
/*               BFE_ERR_ALREADY_OPEN, BFE_ERR_ARG, BFE_ERR_IMAGE_OPEN (the     */
/*               path, as for BFE_Open), BFE_ERR_SYSTEM_DISK /                  */
/*               BFE_ERR_VOLUME_LOCK (a repair on a device), BFE_ERR_NO_MEMORY, */
/*               BFE_ERR_IO_HOST (the report file in the temporary folder       */
/*               could not be made), or BFE_ERR_CANCELLED.  One exception to    */
/*               "BFE_OK whatever it found": a repair on a device whose         */
/*               writes read back different returns BFE_ERR_VERIFY, the         */
/*               result marked uncorrected.  A journal that needs recovery      */
/*               is replayed first by a repair run and left alone (with a       */
/*               warning) by a report-only one, as e2fsck does; every write     */
/*               a repair makes, the replay's included, is held to the range    */
/*               (one that would land past it is refused).                      */
/*------------------------------------------------------------------------------*/
BFE_API int BFE_CheckFs(const wchar_t *pwszImage, uint64_t ullOffset, uint64_t ullLength,
                        uint32_t uiFlags, BFE_CheckCallback pfnEvent, void *pvContext,
                        BFE_CheckResult *pResult);

/*----------------------------------------------------------------------------*/
/* Filesystem resize.  Allowed only while no image is open; the file or       */
/* device is opened for writing as BFE_Open would open it (the same path,     */
/* offset and length rules; a device needs Administrator, is refused when it  */
/* is the system disk, and has every volume on its disk locked for the run).  */
/*                                                                            */
/* BFE_ResizeFs : GROW the filesystem to fill the range -- after a partition  */
/*                has been made larger, or for a whole-device filesystem made */
/*                smaller than its card -- with e2fsprogs' resize2fs (1.47.4, */
/*                built into the DLL).  The new size is the range's length    */
/*                in whole blocks, trimmed as resize2fs trims it; a size not  */
/*                larger than the present one is "nothing to do" (BFE_OK,     */
/*                the result's two counts equal).  It never shrinks.  The     */
/*                filesystem must be known good first, as resize2fs requires: */
/*                BFE_ERR_NEEDS_CHECK says why it is not, and a full          */
/*                BFE_CheckFs (a repair if it finds anything) clears it.      */
/*                Text lines and progress go through pfnEvent (which may be   */
/*                NULL) as a check's do; its replies are ignored -- a resize  */
/*                is not stopped part-way.  A failure once the resize has     */
/*                begun (BFE_ERR_IO_IMAGE) leaves the filesystem needing a    */
/*                repair, and the text says so.  Returns BFE_OK,              */
/*                BFE_ERR_ALREADY_OPEN, _ARG, _IMAGE_OPEN, _SYSTEM_DISK,      */
/*                _VOLUME_LOCK, _MOUNT (not a filesystem it opens),           */
/*                _NEEDS_CHECK, _UNSUPPORTED, _IO_IMAGE, or _VERIFY (the      */
/*                resize finished but a device did not keep its writes).      */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_ResizeFs(const wchar_t *pwszImage, uint64_t ullOffset, uint64_t ullLength,
                         BFE_CheckCallback pfnEvent, void *pvContext, BFE_ResizeResult *pResult);

/*----------------------------------------------------------------------------*/
/* Device write read-back and the capacity test.                              */
/*                                                                            */
/* A counterfeit-capacity card reports a size it does not have, accepts every */
/* write, reports success, and keeps nothing past its real end -- reading     */
/* back filler (0x55 bytes, on one such card) or repeating its addresses.     */
/* Nothing but reading back tells.  So the filesystem writes of BFE_MakeFsAt, */
/* BFE_ResizeFs and a repairing BFE_CheckFs to a DEVICE are always read back  */
/* and compared (not the zero-fill, and never a file), and fail with          */
/* BFE_ERR_VERIFY when the device did not keep them -- even where e2fsck      */
/* chose to ignore the write error.                                           */
/*                                                                            */
/* BFE_SetWriteVerify : read an ordinary session's device writes back too     */
/*                      (BFE_Open's sessions; off by default -- it about      */
/*                      doubles the I/O).  Holds for later sessions and the   */
/*                      open one; a lost write then fails the call that made  */
/*                      it, or the next BFE_Flush / BFE_Close, with           */
/*                      BFE_ERR_VERIFY.  Returns BFE_OK or BFE_ERR_BUSY.      */
/* BFE_TestCapacity   : find how much of a DEVICE really holds data, without  */
/*                      destroying what is on it: test blocks, each tagged    */
/*                      with its own address, are written at points spread    */
/*                      over the device and read back (bfe_capacity.c's body  */
/*                      gives the steps), and what each replaced is put back. */
/*                      Progress through pfnEvent like a copy                 */
/*                      (START with the planned steps, PROGRESS, DONE;        */
/*                      pszImagePath "test", pwszHostPath the device; the     */
/*                      byte counts are STEPS), and a CANCEL reply stops it   */
/*                      after putting back what was written.  No image may be */
/*                      open; a device only (BFE_ERR_ARG for a file); the     */
/*                      system disk refused; the disk's volumes locked for    */
/*                      the test.  A crash part-way could leave test blocks   */
/*                      where data was -- keep a backup.  Returns BFE_OK with */
/*                      the result filled (the verdict is there, good or      */
/*                      not), BFE_ERR_ALREADY_OPEN, _ARG, _IMAGE_OPEN,        */
/*                      _SYSTEM_DISK, _VOLUME_LOCK, _NO_MEMORY, _CANCELLED,   */
/*                      _IO_IMAGE (a read failed) or _IO_HOST (a write or     */
/*                      the putting back failed -- the text says where).      */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_SetWriteVerify(uint32_t bOn);
BFE_API int BFE_TestCapacity(const wchar_t *pwszDevice, BFE_CopyCallback pfnEvent, void *pvContext,
                             BFE_CapacityResult *pResult);

/* The end of the C linkage block opened at the top. */
#ifdef __cplusplus
}
#endif

/* The end of the include guard. */
#endif // BIFROST_ENGINE_H
