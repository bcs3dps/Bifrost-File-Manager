/*-------------------------------------------------------------------------------*/
/* EngineNative.cs                                                               */
/*                                                                               */
/* SPDX-License-Identifier: GPL-2.0-or-later                                     */
/* Copyright (c) 2026 B. C. Services                                             */
/*                                                                               */
/* The managed side of bifrost_engine.dll: it finds the DLL, loads it, binds     */
/* every engine function to a delegate, and mirrors the engine's structures      */
/* and constants (engine\bifrost_engine.h is the contract; this file follows     */
/* it member for member).                                                        */
/*                                                                               */
/* WHY THE DLL IS LOADED BY HAND rather than through DllImport.  A DLL the       */
/* runtime loads for a DllImport is never unloaded while the process lives.      */
/* The release build carries the engine embedded in the exe, extracts it to a    */
/* temporary folder at start-up and deletes it on exit -- and Windows will       */
/* not delete a loaded DLL.  Loading it here with LoadLibrary means this         */
/* class holds the ONLY reference, so one Eng_FreeLibrary at exit really unloads */
/* it and the file can then be removed.                                          */
/*                                                                               */
/* WHERE THE DLL IS LOOKED FOR.  Beside the exe first: that is the               */
/* development arrangement, and on a machine whose policy forbids loading        */
/* code from a temporary folder it is how a release can still be made to run     */
/* (put the DLL beside the exe).  When none is there, the copy embedded in       */
/* the exe as a Win32 resource is extracted to a folder of this run's own        */
/* under %TEMP% and loaded from that; ENG_Unload removes the folder.             */
/*                                                                               */
/* THREADING.  The engine is called from one thread only, as its contract        */
/* requires (bifrost_engine.h): the command system's worker                      */
/* (CommandSystem.cs), which makes every call and receives every callback.       */
/* Loading is Program.cs's, at start-up, before the worker exists; unloading     */
/* is the main window's, on its way out, once the worker has stopped -- so       */
/* no call or callback can still be inside the DLL when it goes.                 */
/*-------------------------------------------------------------------------------*/

/* The framework: the base types, file and folder calls (the DLL's          */
/* extraction and the %TEMP% sweep), the running exe's location, P/Invoke   */
/* and the marshalling of the engine's structures and strings, UTF-8, and   */
/* the StringBuilder that collects the names of any functions missing       */
/* from the DLL.                                                            */
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* EEN_stEntry -- mirror of BFE_Entry (824 bytes): one directory entry.   */
    /*------------------------------------------------------------------------*/
    [StructLayout(LayoutKind.Sequential)]
    internal struct EEN_stEntry
    {
        public ulong  EEN_ullSize;         // size in bytes (a symlink: the length of its target)
        public uint   EEN_uiInode;         // inode number (0 on FAT, which has none)
        public uint   EEN_uiType;          // ENG_TYPE_*
        public uint   EEN_uiMode;          // the 12 permission bits (made up on FAT: 0644 / 0755, the write bits gone when read-only)
        public uint   EEN_uiUid;           // owner id
        public uint   EEN_uiGid;           // group id
        public uint   EEN_uiLinks;         // hard-link count
        public uint   EEN_uiAtime;         // access time, UTC epoch seconds
        public uint   EEN_uiMtime;         // modify time, UTC epoch seconds
        public uint   EEN_uiCtime;         // inode-change time, UTC epoch seconds
        public uint   EEN_uiNameBytes;     // length of EEN_abName in bytes, without the NUL
        public uint   EEN_uiAttributes;    // FAT and exFAT: the ENG_ATTR_* bits as FAT stores them; ext2/3/4: 0
        public uint   EEN_uiReserved;      // always 0 (BFE_Entry's uiReserved)
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 768)]
        public byte[] EEN_abName;          // the name, UTF-8, NUL-terminated (BFE_NAME_BYTES: room for a FAT long name's 765 bytes)
    }

    /*------------------------------------------------------------------------*/
    /* VLI_stVolumeInfo -- mirror of BFE_VolumeInfo (104 bytes).              */
    /*------------------------------------------------------------------------*/
    [StructLayout(LayoutKind.Sequential)]
    internal struct VLI_stVolumeInfo
    {
        public ulong  VLI_ullBlockCount;      // total blocks
        public ulong  VLI_ullFreeBlocks;      // free blocks
        public uint   VLI_uiBlockSize;        // bytes per block
        public uint   VLI_uiInodeCount;       // total inodes
        public uint   VLI_uiFreeInodes;       // free inodes
        public uint   VLI_uiFeatureCompat;    // compatible feature bits, raw
        public uint   VLI_uiFeatureIncompat;  // incompatible feature bits, raw
        public uint   VLI_uiFeatureRoCompat;  // read-only-compatible feature bits, raw
        public uint   VLI_uiMetadataCsum;     // 1 when metadata_csum is on
        public uint   VLI_uiDirIndex;         // 1 when dir_index is on
        public uint   VLI_uiReadOnly;         // 1 when the session is read-only
        public uint   VLI_uiExternalJournal;  // 1 when the journal is on another device (such a filesystem opens read-only only)
        public uint   VLI_uiVolumeType;       // ENG_VOLTYPE_*: ext2/3/4 (the feature words say which), FAT12, FAT16, FAT32 or exFAT
        public uint   VLI_uiReserved;         // always 0 (BFE_VolumeInfo's uiReserved)
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 40)]
        public byte[] VLI_abVolumeName;        // the volume label, NUL-terminated (ENG_LABEL_FIELD bytes)
    }

    /*------------------------------------------------------------------------*/
    /* CEV_stCopyEvent -- mirror of BFE_CopyEvent (48 bytes on x64).  The two */
    /* pointers are valid only while the callback runs.                       */
    /*------------------------------------------------------------------------*/
    [StructLayout(LayoutKind.Sequential)]
    internal struct CEV_stCopyEvent
    {
        public ulong  CEV_ullBytesDone;   // bytes of the current file copied so far
        public ulong  CEV_ullBytesTotal;  // the current file's size
        public IntPtr CEV_pImagePath;     // the item's image path, UTF-8
        public IntPtr CEV_pHostPath;      // the item's host path, UTF-16, or zero
        public uint   CEV_uiKind;         // ENG_EVENT_*
        public uint   CEV_uiType;         // ENG_TYPE_*
        public int    CEV_iStatus;        // ENG_ERR_* on ERROR and SKIPPED
        public uint   CEV_uiReserved;     // always 0
    }

    /*-------------------------------------------------------------------------*/
    /* EXO_stExtractOptions / IMO_stImportOptions -- mirrors of the two option */
    /* structures.                                                             */
    /*-------------------------------------------------------------------------*/
    [StructLayout(LayoutKind.Sequential)]
    internal struct EXO_stExtractOptions
    {
        public uint EXO_uiWriteManifest;     // 1: write the metadata manifest beside the data
        public uint EXO_uiSetTimes;          // 1: stamp each extracted file from its inode
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IMO_stImportOptions
    {
        public uint IMO_uiFileMode;     // permission bits for a file with no manifest record
        public uint IMO_uiDirMode;      // permission bits for a directory with no manifest record
        public uint IMO_uiUid;          // owner for anything with no manifest record
        public uint IMO_uiGid;          // group for anything with no manifest record
        public uint IMO_uiCopyMtime;    // 1: carry the host modify time over
        public uint IMO_uiUseManifest;  // 1: restore metadata and symlinks from a manifest when present
    }

    /*-------------------------------------------------------------------------*/
    /* MFO_stMakeFsOptions -- the mirror of BFE_MakeFsOptions (64 bytes): what */
    /* a new image is created as.                                              */
    /*-------------------------------------------------------------------------*/
    [StructLayout(LayoutKind.Sequential)]
    internal struct MFO_stMakeFsOptions
    {
        public ulong  MFO_ullSizeBytes;  // the image's size; the engine rounds it down to whole blocks
        public uint   MFO_uiFsType;      // ENG_FSTYPE_*
        public uint   MFO_uiBlockSize;   // ext: 1024, 2048 or 4096; FAT: the cluster size, a power of two; 0 lets the engine choose either
        public uint   MFO_uiOverwrite;   // 1: replace a file that is already there
        public uint   MFO_uiFlags;       // ENG_MAKEFS_* bits; BFE_MakeFsAt only (ENG_MAKEFS_ZERO: zero the whole range first); 0 for none
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 40)]
        public byte[] MFO_arrLabel;          // the volume label, UTF-8, NUL-terminated (ext: at most 16 bytes; FAT: 11 characters of code page 437, stored in capitals; exFAT: 11 UTF-16 units); MUST be an ENG_LABEL_FIELD-byte array
    }

    /*------------------------------------------------------------------------*/
    /* DVI_stDeviceInfo -- the mirror of BFE_DeviceInfo (96 bytes): what the  */
    /* engine reports about a file or device path.                            */
    /*------------------------------------------------------------------------*/
    [StructLayout(LayoutKind.Sequential)]
    internal struct DVI_stDeviceInfo
    {
        public ulong  DVI_ullSize;        // length in bytes (a device: of what was opened)
        public uint   DVI_uiSectorBytes;  // bytes per sector; 512 for a file
        public uint   DVI_uiDisk;         // the physical disk number, or ENG_NO_DISK
        public uint   DVI_uiBus;          // the device's bus as Windows numbers it (ENG_BUS_*); 0 for a file
        public uint   DVI_uiDevice;       // 1 for a disk or volume, 0 for a file
        public uint   DVI_uiRemovable;    // 1 when Windows reports removable media
        public uint   DVI_uiSystem;       // 1 when on the disk Windows runs from, or its disk cannot be told apart from it: never written
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] DVI_abModel;          // "Vendor Product", UTF-8, NUL-terminated; MUST be a 64-byte array
    }

    /*------------------------------------------------------------------------*/
    /* IFI_stImageInfo -- the mirror of BFE_ImageInfo (24 bytes): an image    */
    /* file's format and the size of the image inside it.                     */
    /*------------------------------------------------------------------------*/
    [StructLayout(LayoutKind.Sequential)]
    internal struct IFI_stImageInfo
    {
        public ulong  IFI_ullImageBytes;    // the image's own size, or ENG_SIZE_UNKNOWN; for a plain file its size
        public ulong  IFI_ullFileBytes;     // the file's size on disk (compressed, when it is)
        public uint   IFI_uiKind;           // ENG_IMAGE_*
        public uint   IFI_uiReserved;       // always 0
    }

    /*------------------------------------------------------------------------*/
    /* RWP_stRawPiece -- the mirror of BFE_RawPiece (24 bytes on x64): one    */
    /* run of bytes for ENG_pfnWriteRawBatch to write.  The bytes are the     */
    /* caller's, reached through a raw pointer, so the caller pins each       */
    /* buffer for the whole batch call (EngineSession.cs, ENGS_DoWritePieces) */
    /* -- the engine only reads them, and keeps nothing after it returns.     */
    /*------------------------------------------------------------------------*/
    [StructLayout(LayoutKind.Sequential)]
    internal struct RWP_stRawPiece
    {
        internal ulong RWP_ullOffset;  // where the bytes go, bytes from the start of the whole target (not of a partition)
        internal IntPtr RWP_pBytes;    // the bytes: a pinned buffer of the caller's, which the engine only reads
        internal uint RWP_uiBytes;     // how many bytes: 1 to ENG_WRITE_RAW_MAX, a whole number of sectors on a device (BFE_RawPiece's cbBytes)
        internal uint RWP_uiReserved;  // always 0: keeps the layout 24 bytes, as BFE_RawPiece's is
    }

    /*------------------------------------------------------------------------*/
    /* The engine's callback shapes, and one delegate type per distinct       */
    /* function SIGNATURE.  Several engine functions share a signature, so    */
    /* they share a delegate type; each still has its own field below.        */
    /*------------------------------------------------------------------------*/

    /*------------------------------------------------------------------------*/
    /* ENG_ListCallback:                                                      */
    /*                                                                        */
    /* BFE_ListCallback's shape: the engine calls it once per directory       */
    /* entry, inside BFE_ListDir, on the calling thread.                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     pEntry    : the entry, a BFE_Entry (EEN_stEntry), valid only while */
    /*                 the call runs -- copy what is wanted.                  */
    /*     pvContext : the value given to BFE_ListDir, passed back as it is.  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : 0 to go on; non-zero stops the listing.                      */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_ListCallback(IntPtr pEntry, IntPtr pvContext);

    /*------------------------------------------------------------------------*/
    /* ENG_CopyCallback:                                                      */
    /*                                                                        */
    /* BFE_CopyCallback's shape: the engine calls it with each event of a     */
    /* copy (and of a raw copy, a zero-fill and the capacity test), inside    */
    /* the call, on the calling thread.                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     pEvent    : the event, a BFE_CopyEvent (CEV_stCopyEvent), valid    */
    /*                 only while the call runs -- its two strings too.       */
    /*     pvContext : the value given to the engine call, passed back.       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : an ENG_REPLY_* value: go on, cancel, or a conflict's or a    */
    /*           manifest clash's answer.                                     */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_CopyCallback(IntPtr pEvent, IntPtr pvContext);

    /*------------------------------------------------------------------------*/
    /* ENG_FnNoArgs:                                                          */
    /*                                                                        */
    /* The engine functions that take nothing: BFE_ApiVersion, BFE_Close and  */
    /* BFE_Flush.                                                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : BFE_ApiVersion's interface version; the others' status.      */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_FnNoArgs();

    /*------------------------------------------------------------------------*/
    /* ENG_FnOpen:                                                            */
    /*                                                                        */
    /* BFE_Open: open the filesystem in a range of an image file or device    */
    /* -- the session every later file call works on.                         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sImage    : the file or device path (UTF-16).                      */
    /*     ullOffset : where the filesystem starts, bytes.                    */
    /*     ullLength : the range's length, bytes; 0 for "to the end".         */
    /*     iReadOnly : 1 to open read-only, 0 to open for writing.            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the status; on ENG_OK the session is open until BFE_Close.   */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_FnOpen([MarshalAs(UnmanagedType.LPWStr)] string sImage, ulong ullOffset, ulong ullLength, int iReadOnly);

    /*------------------------------------------------------------------------*/
    /* ENG_FnMakeFs:                                                          */
    /*                                                                        */
    /* BFE_MakeFs: create a new image file holding an empty filesystem.  The  */
    /* options go by reference: the engine reads the structure where it       */
    /* lies, for the call's length only.                                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sImage    : the new image's path (UTF-16).                         */
    /*     stOptions : its size, type, block size, label and overwrite flag.  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the status.                                                  */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_FnMakeFs([MarshalAs(UnmanagedType.LPWStr)] string sImage, ref MFO_stMakeFsOptions stOptions);

    /*------------------------------------------------------------------------*/
    /* ENG_FnMakeFsAt:                                                        */
    /*                                                                        */
    /* BFE_MakeFsAt: make an empty filesystem in a range of an existing file  */
    /* or device -- a partition -- in place, the zero-fill (ENG_MAKEFS_ZERO)  */
    /* reporting through the callback.                                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sPath     : the file or device path (UTF-16).                      */
    /*     ullOffset : the range's start, bytes.                              */
    /*     ullLength : the range's length, bytes; 0 for "to the end".         */
    /*     stOptions : type, block size, label and flags, by reference.       */
    /*     pfnEvent  : the zero-fill's progress callback, or null; kept alive */
    /*                 by the caller until the call returns.                  */
    /*     pvContext : passed back to the callback.                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the status.                                                  */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_FnMakeFsAt([MarshalAs(UnmanagedType.LPWStr)] string sPath, ulong ullOffset, ulong ullLength, ref MFO_stMakeFsOptions stOptions,
                                         ENG_CopyCallback pfnEvent, IntPtr pvContext);

    /*------------------------------------------------------------------------*/
    /* ENG_FnVolume:                                                          */
    /*                                                                        */
    /* BFE_VolumeInfoGet: the open filesystem's figures.                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     stInfo : receives them; meaningful only on ENG_OK.                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the status.                                                  */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_FnVolume(out VLI_stVolumeInfo stInfo);

    /*------------------------------------------------------------------------*/
    /* ENG_FnText:                                                            */
    /*                                                                        */
    /* BFE_LastErrorText: the last failure's detail line, copied into the     */
    /* caller's buffer.  The length is a size_t, hence UIntPtr.               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     abBuffer : receives the line, UTF-8, NUL-terminated.               */
    /*     cbBuffer : the buffer's size, bytes.                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the status; ENG_ERR_BUFFER when the line does not fit.       */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_FnText(byte[] abBuffer, UIntPtr cbBuffer);

    /*------------------------------------------------------------------------*/
    /* ENG_FnList:                                                            */
    /*                                                                        */
    /* BFE_ListDir: list one directory of the open filesystem, one callback   */
    /* per entry.  The path, like every image path below, is passed as        */
    /* NUL-terminated UTF-8 bytes (ENG_Utf8).                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     abImageDir : the directory's image path.                           */
    /*     pfnEntry   : the per-entry callback; kept alive by the caller      */
    /*                  until the call returns.                               */
    /*     pvContext  : passed back to the callback.                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the status.                                                  */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_FnList(byte[] abImageDir, ENG_ListCallback pfnEntry, IntPtr pvContext);

    /*------------------------------------------------------------------------*/
    /* ENG_FnStat:                                                            */
    /*                                                                        */
    /* BFE_Stat: one entry's facts.  A symlink is described itself, not what  */
    /* it points to.                                                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     abImagePath : the entry's image path.                              */
    /*     stEntry     : receives the facts; meaningful only on ENG_OK.       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the status.                                                  */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_FnStat(byte[] abImagePath, out EEN_stEntry stEntry);

    /*------------------------------------------------------------------------*/
    /* ENG_FnReadLink:                                                        */
    /*                                                                        */
    /* BFE_ReadLink: a symlink's target text, into the caller's buffer.       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     abImagePath : the link's image path.                               */
    /*     abBuffer    : receives the target, UTF-8, with room for its NUL.   */
    /*     cbBuffer    : the buffer's size, bytes (a size_t).                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the status.                                                  */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_FnReadLink(byte[] abImagePath, byte[] abBuffer, UIntPtr cbBuffer);

    /*------------------------------------------------------------------------*/
    /* ENG_FnPath:                                                            */
    /*                                                                        */
    /* The engine functions that take one image path: BFE_MakeDir,            */
    /* BFE_Remove, BFE_RemoveDir and BFE_RemoveTree.                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     abImagePath : the image path.                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the status.  The change is committed (safe from a crash)     */
    /*           only by a later BFE_Flush, which the caller makes.           */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_FnPath(byte[] abImagePath);

    /*------------------------------------------------------------------------*/
    /* ENG_FnTwoPaths:                                                        */
    /*                                                                        */
    /* The engine functions that take two UTF-8 strings: BFE_Rename and       */
    /* BFE_HardLink (two image paths), and BFE_SymLink (the target TEXT       */
    /* first, then the link's image path).                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     abFirst  : the first path, or the symlink's target text.           */
    /*     abSecond : the second image path.                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the status.                                                  */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_FnTwoPaths(byte[] abFirst, byte[] abSecond);

    /*------------------------------------------------------------------------*/
    /* ENG_FnPathUint:                                                        */
    /*                                                                        */
    /* The engine function that takes an image path and one number:           */
    /* BFE_SetMode.                                                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     abImagePath : the entry's image path.                              */
    /*     uiValue     : the permission bits to set.                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the status.                                                  */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_FnPathUint(byte[] abImagePath, uint uiValue);

    /*------------------------------------------------------------------------*/
    /* ENG_FnPathTwoUints:                                                    */
    /*                                                                        */
    /* The engine functions that take an image path and two numbers:          */
    /* BFE_SetOwner (uid, gid) and BFE_SetTimes (atime, mtime).               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     abImagePath : the entry's image path.                              */
    /*     uiFirst     : the uid, or the access time.                         */
    /*     uiSecond    : the gid, or the modify time.                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the status.                                                  */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_FnPathTwoUints(byte[] abImagePath, uint uiFirst, uint uiSecond);

    /*------------------------------------------------------------------------*/
    /* ENG_FnExtract:                                                         */
    /*                                                                        */
    /* BFE_Extract: copy an item (a tree, for a folder) out of the image to   */
    /* Windows, each event through the callback.                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     abImagePath : the item's image path.                               */
    /*     sHostPath   : where it goes on Windows (UTF-16).                   */
    /*     stOptions   : the manifest and times flags, by reference.          */
    /*     pfnEvent    : the copy callback, or null; kept alive by the        */
    /*                   caller until the call returns.                       */
    /*     pvContext   : passed back to the callback.                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the status -- ENG_ERR_PARTIAL when some items failed,        */
    /*           ENG_ERR_CANCELLED when the callback stopped it.              */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_FnExtract(byte[] abImagePath, [MarshalAs(UnmanagedType.LPWStr)] string sHostPath,
                                        ref EXO_stExtractOptions stOptions, ENG_CopyCallback pfnEvent, IntPtr pvContext);

    /*------------------------------------------------------------------------*/
    /* ENG_FnImport:                                                          */
    /*                                                                        */
    /* BFE_Import: copy a Windows file or folder into the image, each event   */
    /* through the callback.                                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sHostPath   : the Windows path (UTF-16).                           */
    /*     abImagePath : where it goes in the image.                          */
    /*     stOptions   : the defaults and the manifest flag, by reference.    */
    /*     pfnEvent    : the copy callback, or null; kept alive by the        */
    /*                   caller until the call returns.                       */
    /*     pvContext   : passed back to the callback.                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the status.  What was written is committed only by a later   */
    /*           BFE_Flush, which the caller makes.                           */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_FnImport([MarshalAs(UnmanagedType.LPWStr)] string sHostPath, byte[] abImagePath,
                                       ref IMO_stImportOptions stOptions, ENG_CopyCallback pfnEvent, IntPtr pvContext);

    /*------------------------------------------------------------------------*/
    /* ENG_FnCopyWithin:                                                      */
    /*                                                                        */
    /* BFE_CopyWithin: copy an item from one place in the image to another,   */
    /* each event through the callback.                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     abImageFrom : the source's image path.                             */
    /*     abImageTo   : the destination's image path.                        */
    /*     pfnEvent    : the copy callback, or null; kept alive by the        */
    /*                   caller until the call returns.                       */
    /*     pvContext   : passed back to the callback.                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the status; committed only by a later BFE_Flush.             */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_FnCopyWithin(byte[] abImageFrom, byte[] abImageTo, ENG_CopyCallback pfnEvent, IntPtr pvContext);

    /*------------------------------------------------------------------------*/
    /* ENG_FnRemoveTree:                                                      */
    /*                                                                        */
    /* BFE_RemoveTree: remove a directory and everything under it, entry by   */
    /* entry, each entry removed reported through the callback as a DONE      */
    /* event (its path, its type, the count so far in the done-bytes field),  */
    /* a CANCEL reply stopping before the next entry.                         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     abImageDir : the directory's image path.                           */
    /*     pfnEvent   : the copy callback, or null; kept alive by the caller  */
    /*                  until the call returns.                               */
    /*     pvContext  : passed back to the callback.                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the status (CANCELLED when stopped: what was removed stays   */
    /*           removed); committed only by a later BFE_Flush.               */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_FnRemoveTree(byte[] abImageDir, ENG_CopyCallback pfnEvent, IntPtr pvContext);

    /*------------------------------------------------------------------------*/
    /* ENG_FnDeviceInfo:                                                      */
    /*                                                                        */
    /* BFE_DeviceInfoGet: what a file or device path is -- its size, sector,  */
    /* disk, bus, model, and whether it is the system's disk.                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sPath  : the path (UTF-16).                                        */
    /*     stInfo : receives the facts; meaningful only on ENG_OK.            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the status.                                                  */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_FnDeviceInfo([MarshalAs(UnmanagedType.LPWStr)] string sPath, out DVI_stDeviceInfo stInfo);

    /*------------------------------------------------------------------------*/
    /* ENG_FnImageInfo:                                                       */
    /*                                                                        */
    /* BFE_ImageInfoGet: an image file's format (plain or compressed) and     */
    /* the size of the image inside it, from its headers -- the data itself   */
    /* is not read through.                                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sPath  : the image file's path (UTF-16).                           */
    /*     stInfo : receives the facts; meaningful only on ENG_OK.            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the status.                                                  */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_FnImageInfo([MarshalAs(UnmanagedType.LPWStr)] string sPath, out IFI_stImageInfo stInfo);

    /*------------------------------------------------------------------------*/
    /* ENG_FnReadRaw:                                                         */
    /*                                                                        */
    /* BFE_ReadRaw: read bytes straight from a file or device, at any offset, */
    /* no filesystem involved.                                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sPath     : the file or device path (UTF-16).                      */
    /*     ullOffset : the first byte to read.                                */
    /*     abBuffer  : receives the bytes; only on ENG_OK do they mean        */
    /*                 anything.                                              */
    /*     cbBytes   : how many, at most ENG_READ_RAW_MAX.                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the status.                                                  */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_FnReadRaw([MarshalAs(UnmanagedType.LPWStr)] string sPath, ulong ullOffset, byte[] abBuffer, uint cbBytes);

    /*------------------------------------------------------------------------*/
    /* ENG_FnWriteRaw:                                                        */
    /*                                                                        */
    /* BFE_WriteRaw: write bytes straight to an existing file or device.  A   */
    /* failure part-way leaves what was written; nothing is put back.         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sPath     : the file or device path (UTF-16).                      */
    /*     ullOffset : the first byte to write.                               */
    /*     abBuffer  : the bytes.                                             */
    /*     cbBytes   : how many, at most ENG_WRITE_RAW_MAX.                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the status.                                                  */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_FnWriteRaw([MarshalAs(UnmanagedType.LPWStr)] string sPath, ulong ullOffset, byte[] abBuffer, uint cbBytes);

    /*------------------------------------------------------------------------*/
    /* ENG_FnWriteRawBatch:                                                   */
    /*                                                                        */
    /* BFE_WriteRawBatch: write several runs of bytes to an existing file or  */
    /* device in ONE call -- on a device, its volumes locked throughout --    */
    /* how a partition table's pieces are written together, in the order      */
    /* given.  The pieces are checked as a set                                */
    /* before anything is written (a malformed set writes nothing); a write   */
    /* that then fails part-way leaves what was written.                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sPath     : the file or device path (UTF-16); never created or     */
    /*                 extended.                                              */
    /*     arrPieces : the pieces, marshalled as a plain array of             */
    /*                 RWP_stRawPiece; each one's bytes pinned by the caller. */
    /*     uiPieces  : the array's length, 1 to ENG_WRITE_BATCH_MAX_PIECES.   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the status.                                                  */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_FnWriteRawBatch([MarshalAs(UnmanagedType.LPWStr)] string sPath,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] RWP_stRawPiece[] arrPieces, uint uiPieces);

    /*------------------------------------------------------------------------*/
    /* ENG_FnCopyRaw:                                                         */
    /*                                                                        */
    /* BFE_CopyRaw: copy a range from one file or device to another --        */
    /* reading a card to an image, writing one back, a partition's read,      */
    /* write or erase -- with the ENG_RAW_* options, each event through the   */
    /* callback.                                                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sFrom         : the source path (UTF-16); may be empty with        */
    /*                     ENG_RAW_ZERO_SOURCE.                               */
    /*     ullFromOffset : where the source range starts, bytes.              */
    /*     sTo           : the destination path (UTF-16).                     */
    /*     ullToOffset   : where it goes in the destination, bytes.           */
    /*     ullLength     : how many bytes; 0 for the whole source.            */
    /*     uiFlags       : ENG_RAW_* bits.                                    */
    /*     pfnEvent      : the copy callback, or null; kept alive by the      */
    /*                     caller until the call returns.                     */
    /*     pvContext     : passed back to the callback.                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the status; ENG_ERR_VERIFY when a read-back differed.        */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_FnCopyRaw([MarshalAs(UnmanagedType.LPWStr)] string sFrom, ulong ullFromOffset,
                                        [MarshalAs(UnmanagedType.LPWStr)] string sTo, ulong ullToOffset,
                                        ulong ullLength, uint uiFlags, ENG_CopyCallback pfnEvent, IntPtr pvContext);

    /*------------------------------------------------------------------------*/
    /* ENG_CheckCallback:                                                     */
    /*                                                                        */
    /* BFE_CheckCallback's shape: the engine calls it with each event of a    */
    /* check -- a line of e2fsck's report, or a pass's progress -- and of a   */
    /* resize (its lines), inside the call, on the calling thread.  The event */
    /* is read field by field from the pointer.                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     pEvent    : the event, a BFE_CheckEvent, valid only while the call */
    /*                 runs -- its text too.                                  */
    /*     pvContext : the value given to the engine call, passed back.       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : for a check, ENG_REPLY_CONTINUE or ENG_REPLY_CANCEL; a       */
    /*           resize ignores it.                                           */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_CheckCallback(IntPtr pEvent, IntPtr pvContext);

    /*------------------------------------------------------------------------*/
    /* ENG_FnCheckFs:                                                         */
    /*                                                                        */
    /* BFE_CheckFs: run e2fsck over a filesystem in a range of a file or      */
    /* device that is NOT open as the session -- report only, or repair.      */
    /* Its result (BFE_CheckResult, ENG_CHECK_RESULT_BYTES) is written into   */
    /* unmanaged memory the caller allocates, and read from there field by    */
    /* field.                                                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sImage    : the file or device path (UTF-16).                      */
    /*     ullOffset : where the filesystem starts, bytes.                    */
    /*     ullLength : the range's length, bytes; 0 for "to the end".         */
    /*     uiFlags   : ENG_CHECK_* bits (ENG_CHECK_REPAIR).                   */
    /*     pfnEvent  : the check callback, or null; kept alive by the caller  */
    /*                 until the call returns.                                */
    /*     pvContext : passed back to the callback.                           */
    /*     pResult   : the caller's memory for the BFE_CheckResult.           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the call's status.  What the check found is in the result    */
    /*           (its exit bits), not in this -- a check can run cleanly and  */
    /*           find errors.                                                 */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_FnCheckFs([MarshalAs(UnmanagedType.LPWStr)] string sImage, ulong ullOffset, ulong ullLength,
                                        uint uiFlags, ENG_CheckCallback pfnEvent, IntPtr pvContext, IntPtr pResult);

    /*------------------------------------------------------------------------*/
    /* ENG_FnResizeFs:                                                        */
    /*                                                                        */
    /* BFE_ResizeFs: grow a filesystem that is NOT open as the session to     */
    /* fill its range.  Its lines come through the check's callback shape;    */
    /* its result (BFE_ResizeResult, 24 bytes) is written into unmanaged      */
    /* memory the caller allocates and read field by field.                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sImage    : the file or device path (UTF-16).                      */
    /*     ullOffset : where the filesystem starts, bytes.                    */
    /*     ullLength : the range to fill, bytes.                              */
    /*     pfnEvent  : the line callback (its replies ignored), or null;      */
    /*                 kept alive by the caller until the call returns.       */
    /*     pvContext : passed back to the callback.                           */
    /*     pResult   : the caller's memory for the BFE_ResizeResult.          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the status -- ENG_ERR_NEEDS_CHECK when the filesystem must   */
    /*           be checked first, ENG_ERR_VERIFY when a device did not keep  */
    /*           a write.                                                     */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_FnResizeFs([MarshalAs(UnmanagedType.LPWStr)] string sImage, ulong ullOffset, ulong ullLength,
                                         ENG_CheckCallback pfnEvent, IntPtr pvContext, IntPtr pResult);

    /*------------------------------------------------------------------------*/
    /* ENG_FnSetWriteVerify:                                                  */
    /*                                                                        */
    /* BFE_SetWriteVerify: whether an open device session's writes are read   */
    /* back and compared.                                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     uiOn : 1 to read writes back, 0 not to.                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the status.                                                  */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_FnSetWriteVerify(uint uiOn);

    /*------------------------------------------------------------------------*/
    /* ENG_FnTestCapacity:                                                    */
    /*                                                                        */
    /* BFE_TestCapacity: find how much of a card really holds data -- a       */
    /* counterfeit's claimed size is larger -- writing test blocks and        */
    /* putting back what they covered.  Its progress comes through the copy   */
    /* callback, counted in steps; its result (BFE_CapacityResult, 40 bytes)  */
    /* is written into unmanaged memory the caller allocates and read field   */
    /* by field.                                                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sDevice   : the device path (UTF-16).                              */
    /*     pfnEvent  : the copy callback, for progress and Cancel; kept alive */
    /*                 by the caller until the call returns.                  */
    /*     pvContext : passed back to the callback.                           */
    /*     pResult   : the caller's memory for the BFE_CapacityResult.        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the status.                                                  */
    /*------------------------------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ENG_FnTestCapacity([MarshalAs(UnmanagedType.LPWStr)] string sDevice, ENG_CopyCallback pfnEvent, IntPtr pvContext,
                                             IntPtr pResult);

    /*------------------------------------------------------------------------*/
    /* ENG_clsNative -- the loaded engine.                                    */
    /*------------------------------------------------------------------------*/
    internal static class ENG_clsNative
    {
        /* The engine's file name and the interface version this build mirrors. */
        internal const string ENG_DLL_NAME    = "bifrost_engine.dll";  // looked for beside the exe
        internal const int    ENG_API_VERSION = 18;                    // the engine interface version these mirrors match (BFE_API_VERSION); ENG_Load refuses a DLL of any other. 13 added BFE_WriteRawBatch and its 24-byte piece, 14 BFE_CopyRaw's in-place and zero-source flags, 15 FAT and exFAT (the FAT make types, the volume type, the entry's attributes), 16 the entry's 768-byte name (a FAT long name whole), 17 BFE_RemoveTree's progress callback and cancel, 18 the 40-byte label field (every label any type holds)

        /* The filesystem check (BFE_CHECK_*): the one flag, the two event kinds   */
        /* and the result's exit bits, which are e2fsck's own.                     */
        internal const uint ENG_CHECK_REPAIR         = 0x0001;  // BFE_CheckFs: fix what is found (e2fsck -fy); without it report only (-fn)
        internal const uint ENG_CHECK_EVENT_TEXT     = 1;       // BFE_CheckEvent.uiKind: one line of e2fsck's report
        internal const uint ENG_CHECK_EVENT_PROGRESS = 2;       // BFE_CheckEvent.uiKind: a pass has advanced
        internal const uint ENG_CHECK_OK             = 0;       // BFE_CheckResult.uiExitCode: no errors
        internal const uint ENG_CHECK_CORRECTED      = 1;       // errors were corrected
        internal const uint ENG_CHECK_UNCORRECTED    = 4;       // errors remain
        internal const uint ENG_CHECK_ERROR          = 8;       // e2fsck itself failed
        internal const uint ENG_CHECK_CANCELLED      = 32;      // the callback stopped it
        internal const int  ENG_CHECK_RESULT_BYTES   = 40;      // sizeof(BFE_CheckResult): two 8-byte and six 4-byte fields, no padding -- the bytes allocated for one

        /* Devices and raw access -- documentation table: BFE_NO_DISK, the raw   */
        /* calls' limits, and the BFE_RAW_* flags of ENG_pfnCopyRaw.  (The       */
        /* bus numbers of DVI_stDeviceInfo.DVI_uiBus are at the end of the       */
        /* next table.)                                                          */
        internal const uint ENG_NO_DISK      = 0xFFFFFFFFu;  // DVI_stDeviceInfo.DVI_uiDisk for a file
        internal const uint ENG_READ_RAW_MAX = 16777216;     // the most one ENG_pfnReadRaw may ask for
        internal const uint ENG_WRITE_RAW_MAX = 16777216;    // the most one ENG_pfnWriteRaw may write, and the most in one ENG_pfnWriteRawBatch piece
        internal const int ENG_WRITE_BATCH_MAX_PIECES = 128; // the most pieces one ENG_pfnWriteRawBatch takes (BFE_WRITE_BATCH_MAX_PIECES: the engine tests every pair for overlap, so the count is bounded)
        internal const uint ENG_RAW_VERIFY   = 0x0001;       // ENG_pfnCopyRaw: read the destination back and compare
        internal const uint ENG_RAW_DECOMPRESS = 0x0002;     // ENG_pfnCopyRaw: a compressed FILE source (.xz .gz .zst .zip, by its first bytes) is decompressed as it is read
        internal const uint ENG_RAW_XZ_OUT   = 0x0004;       // ENG_pfnCopyRaw: the destination FILE is written as .xz (never a device)
        internal const uint ENG_RAW_IN_PLACE = 0x0008;       // ENG_pfnCopyRaw: write INTO an existing destination file -- never created, cut, extended or deleted (one partition of a disk image)
        internal const uint ENG_RAW_ZERO_SOURCE = 0x0010;    // ENG_pfnCopyRaw: write zeros over the range instead of reading a source (an erase; the source path may be empty; never verified)

        /* Image file formats (BFE_IMAGE_*) and the unknown-size marker, then   */
        /* the bus numbers Windows uses in DVI_stDeviceInfo.DVI_uiBus --        */
        /* documentation table.                                                 */
        internal const uint  ENG_IMAGE_RAW    = 0;                     // plain: the file's bytes are the image
        internal const uint  ENG_IMAGE_XZ     = 1;                     // .xz
        internal const uint  ENG_IMAGE_GZIP   = 2;                     // .gz
        internal const uint  ENG_IMAGE_ZSTD   = 3;                     // .zst
        internal const uint  ENG_IMAGE_ZIP    = 4;                     // .zip (its first file is the image)
        internal const ulong ENG_SIZE_UNKNOWN = 0xFFFFFFFFFFFFFFFFUL;  // IFI_stImageInfo.IFI_ullImageBytes when the format does not record the size
        internal const uint ENG_BUS_USB      = 7;                      // STORAGE_BUS_TYPE BusTypeUsb
        internal const uint ENG_BUS_SATA     = 11;                     // BusTypeSata
        internal const uint ENG_BUS_SD       = 12;                     // BusTypeSd
        internal const uint ENG_BUS_MMC      = 13;                     // BusTypeMmc
        internal const uint ENG_BUS_NVME     = 17;                     // BusTypeNvme

        /* Filesystem types for ENG_pfnMakeFs (BFE_FSTYPE_*), and its size floor. */
        internal const uint  ENG_FSTYPE_EXT2      = 2;          // filetype, sparse_super, large_file
        internal const uint  ENG_FSTYPE_EXT3      = 3;          // the ext2 set plus dir_index (no journal is created)
        internal const uint  ENG_FSTYPE_EXT4      = 4;          // the ext3 set plus extents, 64bit, uninit_bg and more (no journal is created)
        internal const uint  ENG_FSTYPE_FAT16     = 16;         // FAT16 -- or FAT12, when the volume has too few clusters for FAT16 (the engine's formatter decides by the count); MFO_uiBlockSize is then the cluster size, 0 for the formatter's choice
        internal const uint  ENG_FSTYPE_FAT32     = 32;         // FAT32 (refused when the size cannot hold FAT32's clusters, about 32 MiB at the least); the cluster size as above
        internal const uint  ENG_FSTYPE_EXFAT     = 64;         // exFAT; the cluster size as above

        /* Volume types (BFE_VOLTYPE_*), VLI_stVolumeInfo.VLI_uiVolumeType, and   */
        /* FAT's attributes (BFE_ATTR_*), EEN_stEntry.EEN_uiAttributes --         */
        /* documentation table.                                                   */
        internal const uint  ENG_VOLTYPE_EXT       = 1;         // ext2, ext3 or ext4; the feature words say which
        internal const uint  ENG_VOLTYPE_FAT12     = 12;        // FAT12
        internal const uint  ENG_VOLTYPE_FAT16     = 16;        // FAT16
        internal const uint  ENG_VOLTYPE_FAT32     = 32;        // FAT32
        internal const uint  ENG_VOLTYPE_EXFAT     = 64;        // exFAT
        internal const uint  ENG_ATTR_READONLY     = 0x01;      // read-only (the mode is shown without its write bits)
        internal const uint  ENG_ATTR_HIDDEN       = 0x02;      // hidden
        internal const uint  ENG_ATTR_SYSTEM       = 0x04;      // a system file
        internal const uint  ENG_ATTR_DIRECTORY    = 0x10;      // a directory
        internal const uint  ENG_ATTR_ARCHIVE      = 0x20;      // changed since a backup cleared the bit
        internal const ulong ENG_MAKEFS_MIN_BYTES = 1048576;    // the smallest image the engine will create: 1 MiB
        internal const uint  ENG_MAKEFS_ZERO      = 0x0001;     // MFO_stMakeFsOptions.MFO_uiFlags, BFE_MAKEFS_ZERO: BFE_MakeFsAt writes zeros over the whole range first, reporting through its callback
        internal const int   ENG_LABEL_MAX_BYTES  = 16;         // the longest ext volume label, in UTF-8 bytes; applied to the ext types only (a FAT or exFAT one is judged by its own rules, NewImageForm.cs and the engine)
        internal const int   ENG_LABEL_FIELD      = 40;         // the size of MFO_stMakeFsOptions.MFO_arrLabel and VLI_stVolumeInfo.VLI_abVolumeName (BFE_VOLUME_NAME_BYTES): the longest label any type holds, 33 bytes of UTF-8, its NUL, padded to 8

        /* Status codes (BFE_OK and BFE_ERR_*). */
        internal const int ENG_OK               = 0;     // success
        internal const int ENG_ERR_ARG          = 1;     // a NULL or malformed argument
        internal const int ENG_ERR_NOT_OPEN     = 2;     // no image is open
        internal const int ENG_ERR_ALREADY_OPEN = 3;     // an image is already open
        internal const int ENG_ERR_BUSY         = 4;     // another engine call is running
        internal const int ENG_ERR_NO_MEMORY    = 5;     // an allocation failed
        internal const int ENG_ERR_IMAGE_OPEN   = 6;     // the file or device path could not be opened (the detail line has the Windows error)
        internal const int ENG_ERR_MOUNT        = 7;     // not an ext2/3/4 filesystem, nor a FAT or exFAT one
        internal const int ENG_ERR_JOURNAL      = 8;     // the journal could not be recovered or started -- or a flush lost it, and the session refuses every change after
        internal const int ENG_ERR_INTERNAL     = -100;  // NOT an engine status: one of EngineSession.cs's operations threw (it records this in the operation's result); kept apart from the engine's codes (0 and up) and from the command system's own (CommandSystem.cs, -1 to -3), so a status names one cause
        internal const int ENG_ERR_READ_ONLY    = 9;     // a modifying call on a read-only session
        internal const int ENG_ERR_NOT_FOUND    = 10;    // the path does not exist
        internal const int ENG_ERR_EXISTS       = 11;    // the destination name is taken
        internal const int ENG_ERR_NOT_DIR      = 12;    // a directory was required
        internal const int ENG_ERR_IS_DIR       = 13;    // a non-directory was required
        internal const int ENG_ERR_NOT_EMPTY    = 14;    // the directory has entries
        internal const int ENG_ERR_NO_SPACE     = 15;    // the filesystem in the image is full -- or, from ENG_pfnCopyRaw, the device is smaller than the image
        internal const int ENG_ERR_IO_IMAGE     = 16;    // a failure reading or writing the image
        internal const int ENG_ERR_IO_HOST      = 17;    // a failure reading or writing a Windows file
        internal const int ENG_ERR_CANCELLED    = 18;    // the operation was cancelled
        internal const int ENG_ERR_NAME         = 19;    // a name cannot exist on the other side
        internal const int ENG_ERR_TYPE         = 20;    // an entry type that cannot cross the boundary
        internal const int ENG_ERR_PARTIAL      = 21;    // the copy finished but some items failed
        internal const int ENG_ERR_BUFFER       = 22;    // the buffer is too small
        internal const int ENG_ERR_NOT_SYMLINK  = 23;    // not a symbolic link
        internal const int ENG_ERR_SYSTEM_DISK  = 24;    // a write to the disk Windows runs from; never overridden
        internal const int ENG_ERR_VOLUME_LOCK  = 25;    // a volume on the target disk could not be locked
        internal const int ENG_ERR_VERIFY       = 26;    // the data read back differs from what was written
        internal const int ENG_ERR_NEEDS_CHECK  = 27;    // BFE_ResizeFs: check (and repair) the filesystem first
        internal const int ENG_ERR_UNSUPPORTED  = 28;    // something this build does not handle: on a FAT or exFAT session a mode, owner or link call; a filesystem or size BFE_ResizeFs will not resize; or a compressed image's zip method / xz filter

        /* Entry types (BFE_TYPE_*). */
        internal const uint ENG_TYPE_UNKNOWN  = 0;       // could not be determined
        internal const uint ENG_TYPE_FILE     = 1;       // regular file
        internal const uint ENG_TYPE_DIR      = 2;       // directory
        internal const uint ENG_TYPE_CHARDEV  = 3;       // character device
        internal const uint ENG_TYPE_BLOCKDEV = 4;       // block device
        internal const uint ENG_TYPE_FIFO     = 5;       // named pipe
        internal const uint ENG_TYPE_SOCKET   = 6;       // unix socket
        internal const uint ENG_TYPE_SYMLINK  = 7;       // symbolic link

        /* Copy events (BFE_EVENT_*) and callback replies (BFE_REPLY_*). */
        internal const uint ENG_EVENT_START     = 1;       // an item is about to be copied
        internal const uint ENG_EVENT_PROGRESS  = 2;       // more of the current file has been copied
        internal const uint ENG_EVENT_DONE      = 3;       // the item was copied
        internal const uint ENG_EVENT_SKIPPED   = 4;       // the item was deliberately not copied
        internal const uint ENG_EVENT_ERROR     = 5;       // the item failed
        internal const uint ENG_EVENT_CONFLICT  = 6;       // the destination file exists
        internal const uint ENG_EVENT_MANIFEST_CLASH = 7;  // an extract with the manifest: the image folder holds an entry of the manifest's own name; asked once, before anything is made
        internal const uint ENG_EVENT_NOT_MANIFEST   = 8;  // an import with the manifest: the folder's _ext4meta.txt has no manifest's first line, so it is imported as a file and restores nothing
        internal const int  ENG_REPLY_CONTINUE  = 0;       // carry on (a conflict: skip; a manifest clash: keep the file)
        internal const int  ENG_REPLY_CANCEL    = 1;       // stop the whole operation
        internal const int  ENG_REPLY_SKIP      = 2;       // conflict: keep the existing destination
        internal const int  ENG_REPLY_OVERWRITE = 3;       // conflict: replace the existing destination
        internal const int  ENG_REPLY_KEEP_FILE = 4;       // manifest clash: extract the image's entry; no manifest is written
        internal const int  ENG_REPLY_KEEP_MANIFEST = 5;   // manifest clash: write the manifest; the image's entry is not extracted

        /* The loaded module, and one bound delegate per engine function --    */
        /* documentation table.  ENG_Load fills them all, then checks that     */
        /* none is missing and the version is right; only after it returns     */
        /* true may any be called.  ENG_Unload sets them back to null -- all   */
        /* but two (a defect intentionally deferred, said at that list).       */
        private  static IntPtr             Eng_hModule;              // the DLL's module handle; zero while not loaded
        internal static ENG_FnNoArgs       ENG_pfnApiVersion;        // BFE_ApiVersion
        internal static ENG_FnOpen         ENG_pfnOpen;              // BFE_Open
        internal static ENG_FnNoArgs       ENG_pfnClose;             // BFE_Close
        internal static ENG_FnNoArgs       ENG_pfnFlush;             // BFE_Flush
        internal static ENG_FnVolume       ENG_pfnVolumeInfoGet;     // BFE_VolumeInfoGet
        internal static ENG_FnText         ENG_pfnLastErrorText;     // BFE_LastErrorText
        internal static ENG_FnMakeFs       ENG_pfnMakeFs;            // BFE_MakeFs
        internal static ENG_FnMakeFsAt     ENG_pfnMakeFsAt;          // BFE_MakeFsAt
        internal static ENG_FnList         ENG_pfnListDir;           // BFE_ListDir
        internal static ENG_FnStat         ENG_pfnStat;              // BFE_Stat
        internal static ENG_FnReadLink     ENG_pfnReadLink;          // BFE_ReadLink
        internal static ENG_FnPath         ENG_pfnMakeDir;           // BFE_MakeDir
        internal static ENG_FnPath         ENG_pfnRemove;            // BFE_Remove
        internal static ENG_FnPath         ENG_pfnRemoveDir;         // BFE_RemoveDir
        internal static ENG_FnRemoveTree   ENG_pfnRemoveTree;        // BFE_RemoveTree (API 17: with its progress callback)
        internal static ENG_FnTwoPaths     ENG_pfnRename;            // BFE_Rename
        internal static ENG_FnPathUint     ENG_pfnSetMode;           // BFE_SetMode
        internal static ENG_FnPathTwoUints ENG_pfnSetOwner;          // BFE_SetOwner
        internal static ENG_FnPathTwoUints ENG_pfnSetTimes;          // BFE_SetTimes
        internal static ENG_FnTwoPaths     ENG_pfnHardLink;          // BFE_HardLink
        internal static ENG_FnTwoPaths     ENG_pfnSymLink;           // BFE_SymLink
        internal static ENG_FnExtract      ENG_pfnExtract;           // BFE_Extract
        internal static ENG_FnImport       ENG_pfnImport;            // BFE_Import
        internal static ENG_FnCopyWithin   ENG_pfnCopyWithin;        // BFE_CopyWithin
        internal static ENG_FnDeviceInfo   ENG_pfnDeviceInfoGet;     // BFE_DeviceInfoGet
        internal static ENG_FnReadRaw      ENG_pfnReadRaw;           // BFE_ReadRaw
        internal static ENG_FnWriteRaw     ENG_pfnWriteRaw;          // BFE_WriteRaw
        internal static ENG_FnWriteRawBatch ENG_pfnWriteRawBatch;    // BFE_WriteRawBatch (its pieces' buffers pinned by the caller until it returns)
        internal static ENG_FnCopyRaw      ENG_pfnCopyRaw;           // BFE_CopyRaw
        internal static ENG_FnImageInfo    ENG_pfnImageInfoGet;      // BFE_ImageInfoGet
        internal static ENG_FnCheckFs      ENG_pfnCheckFs;           // BFE_CheckFs
        internal static ENG_FnResizeFs     ENG_pfnResizeFs;          // BFE_ResizeFs
        internal static ENG_FnSetWriteVerify ENG_pfnSetWriteVerify;  // BFE_SetWriteVerify
        internal static ENG_FnTestCapacity ENG_pfnTestCapacity;      // BFE_TestCapacity

        /* The embedded engine: where it is in the exe and where it goes. */
        private const int    ENG_RESOURCE_ID   = 100;                   // the RCDATA resource holding the DLL (gui\engine.rc)
        internal const int   ENG_PICTURE_ID    = 101;                   // the RCDATA resource holding the About window's picture, a PNG (gui\engine.rc)
        private const int    ENG_RT_RCDATA     = 10;                    // the Win32 resource type "raw data"
        private const string ENG_TEMP_PREFIX   = "BifrostFM-engine-";   // prefix of the per-run folder under %TEMP%

        private static string Eng_sTempDir = "";    // the folder this run extracted the DLL into; "" when it was found beside the exe

        /*--------------------------------------------------------------------*/
        /* Eng_LoadLibraryW:                                                  */
        /*                                                                    */
        /* kernel32: load a DLL into this process.  Called with the engine's  */
        /* FULL path, so Windows does not search for it and no other copy can */
        /* be picked up.                                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sPath : the DLL's full path.                                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     IntPtr : the module handle, or zero on failure (the reason is  */
        /*              the last Win32 error).                                */
        /*--------------------------------------------------------------------*/
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LoadLibraryW")]
        private static extern IntPtr Eng_LoadLibraryW(string sPath);

        /*--------------------------------------------------------------------*/
        /* Eng_GetProcAddress:                                                */
        /*                                                                    */
        /* kernel32: the address of a function a loaded DLL exports.  Export  */
        /* names are single-byte text, hence the Ansi marshalling; best-fit   */
        /* mapping is off so a name can never be silently altered on the way. */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hModule : the DLL's module handle.                             */
        /*     sName   : the exported name.                                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     IntPtr : the function's address, or zero when the DLL exports  */
        /*              no such name.                                         */
        /*--------------------------------------------------------------------*/
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, BestFitMapping = false, EntryPoint = "GetProcAddress")]
        private static extern IntPtr Eng_GetProcAddress(IntPtr hModule, string sName);

        /*--------------------------------------------------------------------*/
        /* Eng_FreeLibrary:                                                   */
        /*                                                                    */
        /* kernel32: drop one reference to a loaded DLL; the last reference   */
        /* unloads it, after which its file can be deleted.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hModule : the DLL's module handle.                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : non-zero on success.                                     */
        /*--------------------------------------------------------------------*/
        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "FreeLibrary")]
        private static extern int Eng_FreeLibrary(IntPtr hModule);

        /*--------------------------------------------------------------------*/
        /* Eng_FindResourceW:                                                 */
        /*                                                                    */
        /* kernel32: locate a Win32 resource in a module.  An integer id or   */
        /* type is passed as a pointer whose value IS the number, which is    */
        /* how the Windows API tells an id from a name string.                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hModule : the module; zero means the exe of this process.      */
        /*     pName   : the resource's id, as a pointer-sized integer.       */
        /*     pType   : the resource's type, as a pointer-sized integer.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     IntPtr : a handle to the resource's information block, or zero */
        /*              when the module has no such resource.                 */
        /*--------------------------------------------------------------------*/
        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "FindResourceW")]
        private static extern IntPtr Eng_FindResourceW(IntPtr hModule, IntPtr pName, IntPtr pType);

        /*--------------------------------------------------------------------*/
        /* Eng_LoadResource:                                                  */
        /*                                                                    */
        /* kernel32: get a handle to a resource's data.  Nothing is copied:   */
        /* the data stays where it is in the exe's mapped image.              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hModule       : the module; zero means this exe.               */
        /*     hResourceInfo : the handle Eng_FindResourceW returned.         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     IntPtr : a handle to the data, or zero on failure.             */
        /*--------------------------------------------------------------------*/
        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "LoadResource")]
        private static extern IntPtr Eng_LoadResource(IntPtr hModule, IntPtr hResourceInfo);

        /*--------------------------------------------------------------------*/
        /* Eng_SizeofResource:                                                */
        /*                                                                    */
        /* kernel32: the size of a resource's data.                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hModule       : the module; zero means this exe.               */
        /*     hResourceInfo : the handle Eng_FindResourceW returned.         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     uint : the size in bytes, or zero on failure.                  */
        /*--------------------------------------------------------------------*/
        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SizeofResource")]
        private static extern uint Eng_SizeofResource(IntPtr hModule, IntPtr hResourceInfo);

        /*--------------------------------------------------------------------*/
        /* Eng_LockResource:                                                  */
        /*                                                                    */
        /* kernel32: the address of a loaded resource's first byte.  Despite  */
        /* the name nothing is locked and nothing needs unlocking; the        */
        /* address is valid for as long as the module is loaded.              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hResourceData : the handle Eng_LoadResource returned.          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     IntPtr : the address of the data, or zero on failure.          */
        /*--------------------------------------------------------------------*/
        [DllImport("kernel32.dll", EntryPoint = "LockResource")]
        private static extern IntPtr Eng_LockResource(IntPtr hResourceData);

        /*--------------------------------------------------------------------*/
        /* Eng_SweepStaleFolders:                                             */
        /*                                                                    */
        /* Remove engine folders earlier runs left under %TEMP%.  A run       */
        /* deletes its own folder on exit, but a run that crashed or was      */
        /* killed never got the chance.  A folder belonging to a copy of the  */
        /* program that is STILL RUNNING cannot be removed -- Windows holds   */
        /* its DLL locked -- and that failure is exactly how such a folder is */
        /* told apart and left alone.                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : every removable leftover folder is gone.                */
        /*--------------------------------------------------------------------*/
        private static void Eng_SweepStaleFolders()
        {
            string[] asFolders;
            int      i;

            try
            {   /* Listing %TEMP% can itself be refused; then nothing is swept. */
                asFolders = Directory.GetDirectories(Path.GetTempPath(), ENG_TEMP_PREFIX + "*");
            }
            catch (Exception)
            {   /* %TEMP% is unreadable. */
                return;
            }

            for (i = 0; i < asFolders.Length; i++)
            {   /* One leftover folder per iteration. */
                try
                {   /* Fails, harmlessly, when a running copy has the DLL loaded. */
                    Directory.Delete(asFolders[i], true);
                }
                catch (Exception)
                {   /* In use by another running copy, or not removable; leave it. */
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* Eng_ExtractEmbedded:                                               */
        /*                                                                    */
        /* Write the engine DLL embedded in this exe out to a fresh folder    */
        /* of this run's own under %TEMP%, so it can be loaded; the DLL       */
        /* inside keeps its real name.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sDllPath : receives the extracted DLL's full path.             */
        /*     sError   : receives a message on failure; "" on success.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the DLL was written.  False when this exe     */
        /*            carries no embedded engine (a development build) or     */
        /*            %TEMP% could not be written.                            */
        /*--------------------------------------------------------------------*/
        private static bool Eng_ExtractEmbedded(out string sDllPath, out string sError)
        {
            IntPtr hInfo;
            IntPtr hData;
            IntPtr pBytes;
            byte[] abDll;
            string sFolder;
            uint   cbDll;

            /* No path and no error until one is found. */
            sDllPath = "";
            sError   = "";

            /* STEP 1: find the resource in this exe. */
            hInfo = Eng_FindResourceW(IntPtr.Zero, (IntPtr)ENG_RESOURCE_ID, (IntPtr)ENG_RT_RCDATA);

            if (hInfo == IntPtr.Zero)
            {   /* This build has no engine embedded in it. */
                sError = "this build does not carry the engine inside it";
                return(false);
            }

            /* Its data handle and size, and -- only when the handle came --    */
            /* the address of its bytes; zero stands for "could not be read".   */
            hData  = Eng_LoadResource(IntPtr.Zero, hInfo);
            cbDll  = Eng_SizeofResource(IntPtr.Zero, hInfo);
            pBytes = IntPtr.Zero;

            if (hData != IntPtr.Zero)
            {   /* The resource is loaded; get the address of its bytes. */
                pBytes = Eng_LockResource(hData);
            }

            if (pBytes == IntPtr.Zero || cbDll == 0)
            {   /* The resource is there but cannot be read. */
                sError = "the engine embedded in the program could not be read";
                return(false);
            }

            /* STEP 2: copy the bytes out of the exe's image. */
            abDll = new byte[cbDll];
            Marshal.Copy(pBytes, abDll, 0, (int)cbDll);

            /* STEP 3: write them to a fresh per-run folder.  Its name carries   */
            /* the process id and a random part, so two copies of the program    */
            /* started together cannot collide and nothing can be planted at     */
            /* a predictable path ahead of time.                                 */
            sFolder = Path.Combine(Path.GetTempPath(),
                                   ENG_TEMP_PREFIX + System.Diagnostics.Process.GetCurrentProcess().Id + "-" +
                                   Guid.NewGuid().ToString("N"));

            try
            {   /* Creating the folder or the file can fail on a locked-down machine. */
                Directory.CreateDirectory(sFolder);
                sDllPath = Path.Combine(sFolder, ENG_DLL_NAME);
                File.WriteAllBytes(sDllPath, abDll);
            }
            catch (Exception exWrite)
            {   /* %TEMP% is not writable.  The folder may have been made, and a   */
                /* part of the DLL written, before the failure: Eng_sTempDir is    */
                /* not set yet, so ENG_Unload will not remove them -- the next     */
                /* start's Eng_SweepStaleFolders does.                             */
                sError = "the engine could not be written to the temporary folder: " + exWrite.Message;
                return(false);
            }

            /* Remember the folder so ENG_Unload can remove it. */
            Eng_sTempDir = sFolder;

            /* The DLL is on disk and ready to load. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* ENG_ReadResource:                                                  */
        /*                                                                    */
        /* Copy one raw-data resource out of this exe into managed memory --  */
        /* the About window's picture.  (The engine DLL is read by            */
        /* Eng_ExtractEmbedded, which has its own reporting to do.)           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iResourceId : the RCDATA resource's number.                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte[] : the resource's bytes, or null when this exe holds no  */
        /*              such resource or it cannot be read.                   */
        /*--------------------------------------------------------------------*/
        internal static byte[] ENG_ReadResource(int iResourceId)
        {
            IntPtr hInfo;
            IntPtr hData;
            IntPtr pBytes;
            byte[] abData;
            uint   cbData;

            /* The resource, by number, among this exe's raw-data resources. */
            hInfo = Eng_FindResourceW(IntPtr.Zero, (IntPtr)iResourceId, (IntPtr)ENG_RT_RCDATA);

            if (hInfo == IntPtr.Zero)
            {   /* Not in this exe. */
                return(null);
            }

            /* Its data handle, size and address, as Eng_ExtractEmbedded gets them. */
            hData  = Eng_LoadResource(IntPtr.Zero, hInfo);
            cbData = Eng_SizeofResource(IntPtr.Zero, hInfo);
            pBytes = IntPtr.Zero;

            if (hData != IntPtr.Zero)
            {   /* Loaded; get the address of its bytes. */
                pBytes = Eng_LockResource(hData);
            }

            if (pBytes == IntPtr.Zero || cbData == 0)
            {   /* There, but unreadable. */
                return(null);
            }

            /* Copied into managed memory, so the caller holds no pointer into   */
            /* the exe's mapped image.                                           */
            abData = new byte[cbData];
            Marshal.Copy(pBytes, abData, 0, (int)cbData);

            /* The resource, copied out of the exe's image. */
            return(abData);
        }

        /*--------------------------------------------------------------------*/
        /* Eng_Bind:                                                          */
        /*                                                                    */
        /* Resolve one exported function and wrap its address in a delegate   */
        /* of the given type.  A missing export means the DLL is not the      */
        /* engine this build was written against; the name is appended to the */
        /* caller's error text and null is returned, so ENG_Load can report   */
        /* every missing function at once instead of only the first.          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sExport     : the exported name ("BFE_Open").                  */
        /*     typDelegate : the delegate type matching its signature.        */
        /*     sbMissing   : collects the names that could not be found.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Delegate : the bound delegate, or null when the export is      */
        /*                absent.                                             */
        /*--------------------------------------------------------------------*/
        private static Delegate Eng_Bind(string sExport, Type typDelegate, StringBuilder sbMissing)
        {
            IntPtr pFunction;

            /* The export's address in the loaded DLL, by its plain C name. */
            pFunction = Eng_GetProcAddress(Eng_hModule, sExport);

            if (pFunction == IntPtr.Zero)
            {   /* The DLL does not export this name. */
                sbMissing.Append(" " + sExport);
                return(null);
            }

            /* The address as a callable delegate. */
            return(Marshal.GetDelegateForFunctionPointer(pFunction, typDelegate));
        }

        /*--------------------------------------------------------------------*/
        /* ENG_Load:                                                          */
        /*                                                                    */
        /* Find and load the engine DLL, bind every function, and confirm the */
        /* DLL speaks the interface version this build mirrors.  On false no  */
        /* engine function may be called.  A copy extracted to %TEMP% and     */
        /* then refused is left there, for the next start's sweep.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sError : receives a message fit to show the user on failure;   */
        /*              "" on success.                                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the engine is loaded and ready.               */
        /*--------------------------------------------------------------------*/
        internal static bool ENG_Load(out string sError)
        {
            StringBuilder sbMissing;
            string        sExeDir;
            string        sDllPath;
            string        sExtractError;
            int           iVersion;

            /* No error until one happens. */
            sError = "";

            if (Eng_hModule != IntPtr.Zero)
            {   /* Already loaded; nothing to do. */
                return(true);
            }

            /* STEP 1: locate the DLL beside the exe. */
            sExeDir  = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            sDllPath = Path.Combine(sExeDir, ENG_DLL_NAME);

            /* Clear out what crashed runs left behind before adding to it. */
            Eng_SweepStaleFolders();

            if (!File.Exists(sDllPath))
            {   /* Not beside the exe: use the copy embedded in the exe,   */
                /* extracted to a folder of this run's own under %TEMP%.   */
                if (!Eng_ExtractEmbedded(out sDllPath, out sExtractError))
                {   /* Neither beside the exe nor inside it. */
                    sError = ENG_DLL_NAME + " was not found beside the program, and " + sExtractError + ".";
                    return(false);
                }
            }

            /* STEP 2: load it by its FULL path, so no other copy on the   */
            /* search path can be picked up instead.                       */
            Eng_hModule = Eng_LoadLibraryW(sDllPath);

            if (Eng_hModule == IntPtr.Zero)
            {   /* Windows refused it: wrong architecture, blocked, or damaged.   */
                /* Nothing was loaded, so there is nothing to unload; an          */
                /* extracted copy stays in its temporary folder (ENG_Unload is    */
                /* not called) until the next start's sweep removes it.           */
                sError = ENG_DLL_NAME + " could not be loaded (Windows error " +
                         Marshal.GetLastWin32Error() + "):\n" + sDllPath;
                return(false);
            }

            /* STEP 3: bind every function, collecting any that are missing. */
            sbMissing = new StringBuilder();

            ENG_pfnApiVersion    = (ENG_FnNoArgs)Eng_Bind("BFE_ApiVersion", typeof(ENG_FnNoArgs), sbMissing);
            ENG_pfnOpen          = (ENG_FnOpen)Eng_Bind("BFE_Open", typeof(ENG_FnOpen), sbMissing);
            ENG_pfnClose         = (ENG_FnNoArgs)Eng_Bind("BFE_Close", typeof(ENG_FnNoArgs), sbMissing);
            ENG_pfnFlush         = (ENG_FnNoArgs)Eng_Bind("BFE_Flush", typeof(ENG_FnNoArgs), sbMissing);
            ENG_pfnVolumeInfoGet = (ENG_FnVolume)Eng_Bind("BFE_VolumeInfoGet", typeof(ENG_FnVolume), sbMissing);
            ENG_pfnLastErrorText = (ENG_FnText)Eng_Bind("BFE_LastErrorText", typeof(ENG_FnText), sbMissing);
            ENG_pfnMakeFs        = (ENG_FnMakeFs)Eng_Bind("BFE_MakeFs", typeof(ENG_FnMakeFs), sbMissing);
            ENG_pfnMakeFsAt      = (ENG_FnMakeFsAt)Eng_Bind("BFE_MakeFsAt", typeof(ENG_FnMakeFsAt), sbMissing);
            ENG_pfnListDir       = (ENG_FnList)Eng_Bind("BFE_ListDir", typeof(ENG_FnList), sbMissing);
            ENG_pfnStat          = (ENG_FnStat)Eng_Bind("BFE_Stat", typeof(ENG_FnStat), sbMissing);
            ENG_pfnReadLink      = (ENG_FnReadLink)Eng_Bind("BFE_ReadLink", typeof(ENG_FnReadLink), sbMissing);
            ENG_pfnMakeDir       = (ENG_FnPath)Eng_Bind("BFE_MakeDir", typeof(ENG_FnPath), sbMissing);
            ENG_pfnRemove        = (ENG_FnPath)Eng_Bind("BFE_Remove", typeof(ENG_FnPath), sbMissing);
            ENG_pfnRemoveDir     = (ENG_FnPath)Eng_Bind("BFE_RemoveDir", typeof(ENG_FnPath), sbMissing);
            ENG_pfnRemoveTree    = (ENG_FnRemoveTree)Eng_Bind("BFE_RemoveTree", typeof(ENG_FnRemoveTree), sbMissing);
            ENG_pfnRename        = (ENG_FnTwoPaths)Eng_Bind("BFE_Rename", typeof(ENG_FnTwoPaths), sbMissing);
            ENG_pfnSetMode       = (ENG_FnPathUint)Eng_Bind("BFE_SetMode", typeof(ENG_FnPathUint), sbMissing);
            ENG_pfnSetOwner      = (ENG_FnPathTwoUints)Eng_Bind("BFE_SetOwner", typeof(ENG_FnPathTwoUints), sbMissing);
            ENG_pfnSetTimes      = (ENG_FnPathTwoUints)Eng_Bind("BFE_SetTimes", typeof(ENG_FnPathTwoUints), sbMissing);
            ENG_pfnHardLink      = (ENG_FnTwoPaths)Eng_Bind("BFE_HardLink", typeof(ENG_FnTwoPaths), sbMissing);
            ENG_pfnSymLink       = (ENG_FnTwoPaths)Eng_Bind("BFE_SymLink", typeof(ENG_FnTwoPaths), sbMissing);
            ENG_pfnExtract       = (ENG_FnExtract)Eng_Bind("BFE_Extract", typeof(ENG_FnExtract), sbMissing);
            ENG_pfnImport        = (ENG_FnImport)Eng_Bind("BFE_Import", typeof(ENG_FnImport), sbMissing);
            ENG_pfnCopyWithin    = (ENG_FnCopyWithin)Eng_Bind("BFE_CopyWithin", typeof(ENG_FnCopyWithin), sbMissing);
            ENG_pfnDeviceInfoGet = (ENG_FnDeviceInfo)Eng_Bind("BFE_DeviceInfoGet", typeof(ENG_FnDeviceInfo), sbMissing);
            ENG_pfnReadRaw       = (ENG_FnReadRaw)Eng_Bind("BFE_ReadRaw", typeof(ENG_FnReadRaw), sbMissing);
            ENG_pfnWriteRaw      = (ENG_FnWriteRaw)Eng_Bind("BFE_WriteRaw", typeof(ENG_FnWriteRaw), sbMissing);
            ENG_pfnWriteRawBatch = (ENG_FnWriteRawBatch)Eng_Bind("BFE_WriteRawBatch", typeof(ENG_FnWriteRawBatch), sbMissing);
            ENG_pfnCopyRaw       = (ENG_FnCopyRaw)Eng_Bind("BFE_CopyRaw", typeof(ENG_FnCopyRaw), sbMissing);
            ENG_pfnImageInfoGet  = (ENG_FnImageInfo)Eng_Bind("BFE_ImageInfoGet", typeof(ENG_FnImageInfo), sbMissing);
            ENG_pfnCheckFs       = (ENG_FnCheckFs)Eng_Bind("BFE_CheckFs", typeof(ENG_FnCheckFs), sbMissing);
            ENG_pfnResizeFs      = (ENG_FnResizeFs)Eng_Bind("BFE_ResizeFs", typeof(ENG_FnResizeFs), sbMissing);
            ENG_pfnSetWriteVerify = (ENG_FnSetWriteVerify)Eng_Bind("BFE_SetWriteVerify", typeof(ENG_FnSetWriteVerify), sbMissing);
            ENG_pfnTestCapacity  = (ENG_FnTestCapacity)Eng_Bind("BFE_TestCapacity", typeof(ENG_FnTestCapacity), sbMissing);

            if (sbMissing.Length != 0)
            {   /* Not the engine this build expects. */
                sError = ENG_DLL_NAME + " is missing functions this program needs:" + sbMissing.ToString();
                ENG_Unload();
                return(false);
            }

            /* STEP 4: the structures here match one interface version only. */
            iVersion = ENG_pfnApiVersion();

            if (iVersion != ENG_API_VERSION)
            {   /* A DLL from a different build of the program. */
                sError = ENG_DLL_NAME + " is interface version " + iVersion + "; this program needs version " +
                         ENG_API_VERSION + ".";
                ENG_Unload();
                return(false);
            }

            /* The engine is loaded, bound and of the right version. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* ENG_Unload:                                                        */
        /*                                                                    */
        /* Drop the bound functions, unload the DLL, and remove the folder    */
        /* this run extracted it to.  The caller must have closed any open    */
        /* image and stopped the command worker first: nothing may still be   */
        /* inside the DLL, or about to call into it.  Also called by ENG_Load */
        /* itself when a DLL it loaded is refused.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the DLL is unloaded and its folder removed as far as    */
        /*            Windows allows; ENG_Load may be called again.           */
        /*--------------------------------------------------------------------*/
        internal static void ENG_Unload()
        {
            /* Clear these bound entries FIRST: each delegate holds an            */
            /* address inside the module, and calling one after Eng_FreeLibrary   */
            /* would jump into unmapped memory.                                   */
            ENG_pfnApiVersion    = null;
            ENG_pfnOpen          = null;
            ENG_pfnClose         = null;
            ENG_pfnFlush         = null;
            ENG_pfnVolumeInfoGet = null;
            ENG_pfnLastErrorText = null;
            ENG_pfnMakeFs        = null;
            ENG_pfnMakeFsAt      = null;
            ENG_pfnListDir       = null;
            ENG_pfnStat          = null;
            ENG_pfnReadLink      = null;
            ENG_pfnMakeDir       = null;
            ENG_pfnRemove        = null;
            ENG_pfnRemoveDir     = null;
            ENG_pfnRemoveTree    = null;
            ENG_pfnRename        = null;
            ENG_pfnSetMode       = null;
            ENG_pfnSetOwner      = null;
            ENG_pfnSetTimes      = null;
            ENG_pfnHardLink      = null;
            ENG_pfnSymLink       = null;
            ENG_pfnExtract       = null;
            ENG_pfnImport        = null;
            ENG_pfnCopyWithin    = null;
            ENG_pfnDeviceInfoGet = null;
            ENG_pfnReadRaw       = null;
            ENG_pfnWriteRaw      = null;
            ENG_pfnWriteRawBatch = null;
            ENG_pfnCopyRaw       = null;
            ENG_pfnImageInfoGet  = null;
            ENG_pfnCheckFs       = null;
            ENG_pfnResizeFs      = null;
            /* ENG_pfnSetWriteVerify and ENG_pfnTestCapacity (API 11)         */
            /* are not cleared here, so after an unload -- including          */
            /* ENG_Load's own on a failure -- they still point into the       */
            /* freed DLL.  THE DEFECT EXISTS AND IS INTENTIONALLY DEFERRED:   */
            /* the unload happens only at the program's end or on a failed    */
            /* load, and nothing calls them after it.                         */

            if (Eng_hModule != IntPtr.Zero)
            {   /* Release the one reference this class holds. */
                Eng_FreeLibrary(Eng_hModule);
                Eng_hModule = IntPtr.Zero;
            }

            if (Eng_sTempDir.Length != 0)
            {   /* This run extracted the DLL: now that it is unloaded,     */
                /* Windows will let it be deleted, so clean up behind us.   */
                try
                {   /* The folder holds nothing but the DLL. */
                    Directory.Delete(Eng_sTempDir, true);
                }
                catch (Exception)
                {   /* Could not be removed now; the next start's sweep will. */
                }

                Eng_sTempDir = "";
            }
        }

        /*--------------------------------------------------------------------*/
        /* ENG_Utf8:                                                          */
        /*                                                                    */
        /* Encode a string as the NUL-terminated UTF-8 bytes the engine takes */
        /* for a path inside the image.                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sText : the path or text.                                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte[] : the UTF-8 bytes followed by one zero byte.            */
        /*--------------------------------------------------------------------*/
        internal static byte[] ENG_Utf8(string sText)
        {
            byte[] abText;
            byte[] abOut;

            /* The text's bytes, then a copy one byte longer for the NUL. */
            abText = Encoding.UTF8.GetBytes(sText);
            abOut  = new byte[abText.Length + 1];
            Array.Copy(abText, abOut, abText.Length);

            /* A new array is zero-filled, so the last byte is the terminator. */
            return(abOut);
        }

        /*--------------------------------------------------------------------*/
        /* ENG_FromUtf8:                                                      */
        /*                                                                    */
        /* Decode a NUL-terminated UTF-8 buffer the engine filled.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     abBuffer : the buffer.                                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the text up to the first zero byte.                   */
        /*--------------------------------------------------------------------*/
        internal static string ENG_FromUtf8(byte[] abBuffer)
        {
            int iLength;

            /* The text's length: up to the first zero byte, or the whole   */
            /* buffer when the engine left it unterminated.                 */
            iLength = 0;

            while (iLength < abBuffer.Length && abBuffer[iLength] != 0)
            {   /* One byte of text per iteration, up to the terminator. */
                iLength++;
            }

            /* The decoded text. */
            return(Encoding.UTF8.GetString(abBuffer, 0, iLength));
        }

        /*--------------------------------------------------------------------*/
        /* ENG_PtrToUtf8:                                                     */
        /*                                                                    */
        /* Decode a NUL-terminated UTF-8 string from native memory -- the     */
        /* image path inside a copy event.  The framework this targets has no */
        /* UTF-8 pointer marshaller (its "Ansi" one would read the bytes in   */
        /* the system code page and mangle anything outside ASCII), so the    */
        /* bytes are measured and copied here.                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pszText : the native string; may be zero.                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the text, or "" for a zero pointer.                   */
        /*--------------------------------------------------------------------*/
        internal static string ENG_PtrToUtf8(IntPtr pszText)
        {
            byte[] abText;
            int    iLength;

            if (pszText == IntPtr.Zero)
            {   /* No string was supplied. */
                return("");
            }

            /* Measure up to the terminator, reading native memory a byte at a   */
            /* time -- the engine's strings are always NUL-terminated.           */
            iLength = 0;

            while (Marshal.ReadByte(pszText, iLength) != 0)
            {   /* One byte per iteration, up to the terminator. */
                iLength++;
            }

            /* The bytes, copied into managed memory for the decoder. */
            abText = new byte[iLength];
            Marshal.Copy(pszText, abText, 0, iLength);

            /* The decoded text. */
            return(Encoding.UTF8.GetString(abText));
        }

        /*--------------------------------------------------------------------*/
        /* ENG_EntryName:                                                     */
        /*                                                                    */
        /* The name carried by an EEN_stEntry, decoded.  A name that is not   */
        /* valid UTF-8 decodes with replacement characters, which is the      */
        /* honest thing to SHOW for it; the engine still handles the entry by */
        /* its real bytes.                                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stEntry : the entry.                                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the entry's name.                                     */
        /*--------------------------------------------------------------------*/
        internal static string ENG_EntryName(EEN_stEntry stEntry)
        {
            /* uiNameBytes is the exact length; the array is 768 bytes. */
            return(Encoding.UTF8.GetString(stEntry.EEN_abName, 0, (int)stEntry.EEN_uiNameBytes));
        }

        /*--------------------------------------------------------------------*/
        /* ENG_ErrorDetail:                                                   */
        /*                                                                    */
        /* Fetch the engine's detail line for the failure that has just been  */
        /* returned.  Called on the command worker -- the one thread every    */
        /* engine call is made from -- straight after the failing call, so    */
        /* the line is that failure's and no later call's.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the detail line, or "" when there is none.            */
        /*--------------------------------------------------------------------*/
        internal static string ENG_ErrorDetail()
        {
            byte[] abBuffer;

            /* The engine's line is at most 511 bytes and its NUL (BFX_ERROR_   */
            /* BYTES, bifrost_engine.c), so 600 always holds it -- which is     */
            /* why the call's result is not looked at: BFE_ERR_BUFFER cannot    */
            /* happen, and an empty line comes back as "".                      */
            abBuffer = new byte[600];
            ENG_pfnLastErrorText(abBuffer, (UIntPtr)(uint)abBuffer.Length);

            /* Empty when the engine recorded nothing. */
            return(ENG_FromUtf8(abBuffer));
        }

        /*--------------------------------------------------------------------*/
        /* ENG_StatusText:                                                    */
        /*                                                                    */
        /* A short plain-language sentence for a status code, for the user.   */
        /* The engine's own detail line (which names the path) is shown       */
        /* beside it by the caller.                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iStatus : an ENG_OK / ENG_ERR_* value, or a command result's   */
        /*               status (SYS_*, CommandSystem.cs).                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the sentence.                                         */
        /*--------------------------------------------------------------------*/
        internal static string ENG_StatusText(int iStatus)
        {
            /* Each sentence covers every place the engine returns its code,    */
            /* since the caller shows it whatever the call was: IMAGE_OPEN      */
            /* comes for a file or a device path; NO_SPACE for a full           */
            /* filesystem or, from ENG_pfnCopyRaw, a device smaller than the    */
            /* image; UNSUPPORTED for a FAT call FAT has no place for (modes,   */
            /* owners, links), a resize it will not make, or a compressed       */
            /* image's method or filter this build lacks.  The engine's         */
            /* detail line beside it says which.  Callers also pass a           */
            /* command result's status, which can be one of the command         */
            /* system's own (CommandSystem.cs): those have sentences of their   */
            /* own below, their values apart from every engine code and from    */
            /* ENG_ERR_INTERNAL.                                                */
            switch (iStatus)
            {   /* One status code per case: each case returns its sentence. */
                case ENG_OK:               return("Done.");
                case ENG_ERR_ARG:          return("The request was not valid.");
                case ENG_ERR_NOT_OPEN:     return("No image is open.");
                case ENG_ERR_ALREADY_OPEN: return("An image is already open.");
                case ENG_ERR_BUSY:         return("The engine is busy with another operation.");
                case ENG_ERR_NO_MEMORY:    return("Out of memory.");
                case ENG_ERR_IMAGE_OPEN:   return("The image file or device could not be opened.");
                case ENG_ERR_MOUNT:        return("This does not look like an ext2, ext3, ext4, FAT or exFAT filesystem.");
                case ENG_ERR_JOURNAL:      return("The filesystem's journal could not be recovered, started or kept running.");
                case ENG_ERR_READ_ONLY:    return("The image is open read-only.");
                case ENG_ERR_NOT_FOUND:    return("The item does not exist.");
                case ENG_ERR_EXISTS:       return("Something with that name already exists.");
                case ENG_ERR_NOT_DIR:      return("That is not a directory.");
                case ENG_ERR_IS_DIR:       return("That is a directory.");
                case ENG_ERR_NOT_EMPTY:    return("The directory is not empty.");
                case ENG_ERR_NO_SPACE:     return("There is not enough room: the filesystem is full, or the device is smaller than the image.");
                case ENG_ERR_IO_IMAGE:     return("The image could not be read or written.");
                case ENG_ERR_IO_HOST:      return("A Windows file could not be read or written.");
                case ENG_ERR_CANCELLED:    return("Cancelled.");
                case ENG_ERR_NAME:         return("The name cannot be used on the other side.");
                case ENG_ERR_TYPE:         return("This kind of item cannot be copied across.");
                case ENG_ERR_PARTIAL:      return("Finished, but some items could not be copied.");
                case ENG_ERR_BUFFER:       return("An internal buffer was too small.");
                case ENG_ERR_NOT_SYMLINK:  return("That is not a symbolic link.");
                case ENG_ERR_SYSTEM_DISK:  return("That device is the disk Windows is running from (or cannot be told apart from it). It is never written.");
                case ENG_ERR_VOLUME_LOCK:  return("The disk's volumes could not be locked for writing. Usually something has files open on one of them: close them and try again. The detail names the volume.");
                case ENG_ERR_VERIFY:       return("The data read back differs from what was written: the device did not keep it.\n" +
                                                  "The card is failing, or holds less than it claims (a counterfeit). Test Capacity in the partition window will tell which.");
                case ENG_ERR_NEEDS_CHECK:  return("The filesystem must be checked (and repaired if need be) before it is resized.");
                case ENG_ERR_UNSUPPORTED:  return("That is not supported here: FAT has no permissions, owners or links, and some filesystems, sizes and compressed formats are beyond this program.");
                case ENG_ERR_INTERNAL:     return("The operation stopped on an internal error in the program.");
                case SYS_clsSystem.SYS_REFUSED:  return("The request was refused.");
                case SYS_clsSystem.SYS_INTERNAL: return("The request stopped on an internal error in the program.");
                case SYS_clsSystem.SYS_DROPPED:  return("The request was not run: the same request was already waiting.");
                default:                   return("Unexpected engine status " + iStatus + ".");
            }
        }
    }
}
