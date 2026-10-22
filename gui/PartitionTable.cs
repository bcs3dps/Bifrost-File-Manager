/*-----------------------------------------------------------------------------*/
/* PartitionTable.cs                                                           */
/*                                                                             */
/* SPDX-License-Identifier: GPL-2.0-or-later                                   */
/* Copyright (c) 2026 B. C. Services                                           */
/*                                                                             */
/* A disk's partition table as bytes: the model of one (PTB_Table and its      */
/* PTB_Entry slots), a reader that fills the model from a disk image or        */
/* device with every field a writer needs to put the same table back           */
/* UNCHANGED (one exception: a GPT whose entries are larger than 128 bytes     */
/* loses the bytes past 128 of each, PTB_BuildGptEntries), the writer that     */
/* turns the model into the sectors to write, the                              */
/* checks a table must pass before it is written, and the helpers the layout   */
/* window builds on: the free gaps, a new empty table of either kind, the      */
/* names of the common partition types, and the probe that says what a         */
/* partition's first page holds.                                               */
/*                                                                             */
/* Two table kinds are handled.  A GUID partition table (GPT): a protective    */
/* MBR in sector 0, a header in sector 1 that says where the entries are and   */
/* how many, the entries, and a second copy of the entries and the header at   */
/* the END of the disk, every piece checksummed with CRC-32.  Each entry       */
/* carries a type GUID, the partition's own GUID, its first and last sector,   */
/* attribute bits and a name of up to 36 UTF-16 characters -- and a vendor's   */
/* table (a printer's eMMC) uses type GUIDs that name nothing and names that   */
/* mean everything, so every one of those is kept exactly.  An MBR: four       */
/* 16-byte entries in sector 0 after 440 bytes of boot code and a 4-byte disk  */
/* signature, each with a type byte, an active flag, and a 32-bit start and    */
/* count; the boot code and the signature are kept and written back as read,   */
/* so a boot loader living there survives a table edit.  Only the four         */
/* PRIMARY entries are modelled: an extended partition is kept as the primary  */
/* it is, and the logical chain inside it is neither read nor written here     */
/* (the Open dialogs' reader in PartitionForm.cs walks the logical chain and   */
/* lists them; this file does not edit them).                                  */
/* It is never silent about that: an extended entry's contents read            */
/* "EXTENDED: logical partitions not shown", and the account line says it.     */
/*                                                                             */
/* Nothing here touches a window or the engine.  Reads go through a reader     */
/* the caller supplies (PRT_Reader -- in the program, CommandLayoutReader.cs's */
/* LDR_Read over the engine's raw read, on the command worker), and a          */
/* write is handed back as pieces for the caller to place (PTB_Build -- in     */
/* the program, the command worker builds them from a copy of the window's     */
/* table (PTB_CopyTable; CommandOperations.cs), puts the old GPT's erase in    */
/* front (PTB_AddOldGptErase; CommandNativeSteps.cs), and writes them in one   */
/* batch through EngineSession.cs's ENGS_DoWritePieces).  So the same code     */
/* serves an image file and a real card, and it uses nothing else of the       */
/* program.  PTB_Write and its PTB_Writer delegate, which write the pieces     */
/* through a writer the caller supplies, are not called by the program.        */
/* The sector size is a parameter throughout: a 4096-byte                      */
/* sector disk has its GPT at byte 4096, not 512, and the table is laid out    */
/* for whatever sector the caller names.                                       */
/*-----------------------------------------------------------------------------*/

/* The framework: the base types (the GPT's GUIDs, the byte-array copies),   */
/* the lists of entries and pieces, and the text encodings a partition's     */
/* name is read and written in, and the contents probe's labels and          */
/* signature texts are read in.                                              */
using System;
using System.Collections.Generic;
using System.Text;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* PTB_Entry -- one used slot of a partition table, with every field the  */
    /* table kind stores for it, so a table read can be written back as it    */
    /* was.  The MBR fields are zero on a GPT entry and the GPT fields are    */
    /* empty on an MBR entry.                                                 */
    /*------------------------------------------------------------------------*/
    internal sealed class PTB_Entry
    {
        public int    iSlot;           // the slot's index in the table, 0-based: an MBR primary 0 to 3, a GPT entry 0 to nSlots - 1
        public ulong  ullFirstLba;     // the first sector of the partition
        public ulong  ullLastLba;      // the last sector of the partition, INCLUSIVE
        public Guid   guidType;        // GPT: the type GUID (any value; a vendor's is kept as is); MBR: Guid.Empty
        public Guid   guidUnique;      // GPT: the partition's own GUID; MBR: Guid.Empty
        public ulong  ullAttributes;   // GPT: the attribute bits (bit 0 platform required, bit 2 legacy BIOS bootable, bits 48 to 63 the type's own); MBR: 0
        public string sName;           // GPT: the name, up to PTB_GPT_NAME_CHARS UTF-16 characters, "" for none; MBR: ""
        public byte   bType;           // MBR: the type byte (0x83 Linux, 0x0C FAT32 LBA, ...); GPT: 0
        public bool   bBoot;           // MBR: the active (bootable) flag; GPT: false
        public string sContent;        // what the partition's first page holds, from PTB_Probe ("ext4  \"rootfs\"", "FAT32", ...); "" when unknown; never written to the table
    }

    /*------------------------------------------------------------------------*/
    /* PTB_Range -- a run of sectors, first and last INCLUSIVE: a free gap.   */
    /*------------------------------------------------------------------------*/
    internal sealed class PTB_Range
    {
        public ulong ullFirstLba;      // the first sector of the run
        public ulong ullLastLba;       // the last sector of the run, inclusive
    }

    /*------------------------------------------------------------------------*/
    /* PTB_Piece -- one run of bytes the writer wants on the disk: where, and */
    /* what.  A table is a handful of these (one for an MBR; five for a GPT;  */
    /* one or two zero-filled ones for an erase), and the write adds a        */
    /* one-sector zero piece for each old GPT header it erases                */
    /* (PTB_AddOldGptErase).  Every piece starts on a                         */
    /* sector and is a whole number of sectors long, so a device takes it as  */
    /* it is -- the engine's raw write refuses anything else on a device.     */
    /*------------------------------------------------------------------------*/
    internal sealed class PTB_Piece
    {
        public ulong  ullOffset;       // where the bytes go, bytes from the start of the disk
        public byte[] abBytes;         // the bytes; a whole number of sectors
    }

    /*------------------------------------------------------------------------*/
    /* PTB_TypeName -- one of the partition types worth naming: its words,    */
    /* and its value in each table kind (one of the two is unused).           */
    /*------------------------------------------------------------------------*/
    internal sealed class PTB_TypeName
    {
        public string sName;           // the type in words
        public byte   bMbr;            // the MBR type byte; 0 when the type has no MBR form
        public Guid   guidGpt;         // the GPT type GUID; Guid.Empty when the type has no GPT form
    }

    /*-------------------------------------------------------------------------*/
    /* PTB_Table -- a whole partition table: its kind, the disk's geometry,    */
    /* the sector-0 bytes kept verbatim, the GPT's header facts, and the used  */
    /* entries in slot order.  Made by PTB_Read, or by PTB_NewGpt / PTB_NewMbr */
    /* / PTB_NewNone, edited in place, and written by PTB_Build.               */
    /*-------------------------------------------------------------------------*/
    internal sealed class PTB_Table
    {
        public int             iKind;            // PTB_KIND_NONE, PTB_KIND_MBR or PTB_KIND_GPT
        public uint            uiSector;         // bytes per sector the table is laid out for
        public ulong           ullDiskBytes;     // the disk's or image's length, bytes
        public byte[]          abBoot;           // the first PTB_MBR_BOOT_BYTES bytes of sector 0 (boot code), kept as read; zeros for a new table
        public uint            uiDiskSignature;  // the 32-bit disk signature at byte 440 of sector 0, kept as read (an MBR's identity; a GPT's protective MBR keeps one too)
        public Guid            guidDisk;         // GPT: the disk GUID
        public ulong           ullFirstUsable;   // GPT: the first sector a partition may use
        public ulong           ullLastUsable;    // GPT: the last sector a partition may use, inclusive
        public ulong           ullEntriesLba;    // GPT: the sector the primary entry array starts at (2 on a standard table; as read, it must lie between the header and the first usable sector -- PTB_GptLayoutProblem)
        public uint            nSlots;           // GPT: how many entry slots the array holds (128 on a standard table; 1 to PTB_GPT_MAX_SLOTS accepted on a read)
        public uint            cbEntry;          // GPT: bytes per entry (128 on a standard table; PTB_GPT_MIN_ENTRY to PTB_GPT_MAX_ENTRY accepted on a read)
        public List<PTB_Entry> lstEntries;       // the used slots, in slot order; never null
    }

    /*------------------------------------------------------------------------*/
    /* PRT_Reader -- how a table is read: fill the buffer from a byte offset  */
    /* of the image or device, returning false when the range is not all      */
    /* there.  In the program, PTB_Read and PTB_Probe are given               */
    /* CommandLayoutReader.cs's LDR_Read (the layout read, Nst_ReadLayout in  */
    /* CommandNativeSteps.cs), over the engine's raw read, for a file and a   */
    /* device alike.  Shared with the                                         */
    /* Open dialogs' table reader in PartitionForm.cs, which takes the same   */
    /* shape -- a change to it changes both.                                  */
    /*------------------------------------------------------------------------*/
    internal delegate bool PRT_Reader(ulong ullOffset, byte[] abBuffer);

    /*-------------------------------------------------------------------------*/
    /* PTB_Writer -- how PTB_Write puts the bytes on the disk: write the       */
    /* buffer at a byte offset of the image or device, returning false (with   */
    /* the reason) when it could not be done.  NOTHING IN THE PROGRAM SUPPLIES */
    /* ONE: the program writes PTB_Build's pieces itself (EngineSession.cs's   */
    /* ENGS_DoWritePieces), and PTB_Write is not called.                       */
    /*-------------------------------------------------------------------------*/
    internal delegate bool PTB_Writer(ulong ullOffset, byte[] abBytes, out string sError);

    /*------------------------------------------------------------------------*/
    /* PTB_Tables -- the work on the model above (the file header describes   */
    /* it): the layout constants and the named types, reading a table,        */
    /* checking it, building its sectors, the free gaps and new entries, and  */
    /* the contents probe.  Static, since every function works on the table   */
    /* it is given; the class's only data are its constant tables (the        */
    /* named types and the CRC table).                                        */
    /*------------------------------------------------------------------------*/
    internal static class PTB_Tables
    {
        /* The table kinds -- documentation table; PTB_Table.iKind holds one. */
        internal const int   PTB_KIND_NONE          = 0;          // no table: the whole disk is one filesystem (or empty)
        internal const int   PTB_KIND_MBR           = 1;          // an MBR with up to four primary partitions
        internal const int   PTB_KIND_GPT           = 2;          // a GUID partition table behind a protective MBR

        /* The MBR's layout -- documentation table. */
        internal const int   PTB_MBR_BOOT_BYTES     = 440;           // the boot code: bytes 0 to 439 of sector 0, kept verbatim
        internal const int   PTB_MBR_SIGNATURE_AT   = 440;           // the 32-bit disk signature
        internal const int   PTB_MBR_TABLE          = 446;           // the four 16-byte entries
        internal const int   PTB_MBR_ENTRY          = 16;            // one entry's size
        internal const int   PTB_MBR_ENTRIES        = 4;             // how many primaries an MBR holds
        internal const int   PTB_MBR_BOOT_SIG       = 510;           // the 0x55 0xAA signature
        internal const byte  PTB_MBR_ACTIVE         = 0x80;          // the status byte's active (bootable) flag
        internal const byte  PTB_TYPE_PROTECTIVE    = 0xEE;          // the MBR type that says "this disk has a GPT"
        internal const ulong PTB_MBR_MAX_LBA        = 0xFFFFFFFFUL;  // the last sector a 32-bit MBR entry can reach
        internal const uint  PTB_CHS_HEADS          = 255;           // the geometry assumed when a CHS address is derived from an LBA (what every modern tool assumes)
        internal const uint  PTB_CHS_SECTORS        = 63;            // sectors per track of that geometry
        internal const uint  PTB_CHS_MAX_CYLINDER   = 1023;          // the largest cylinder a CHS field holds; beyond it the field is pinned to its maximum

        /* The GPT's layout -- documentation table.  Offsets are into the   */
        /* header sector and into one entry.                                */
        internal const int   PTB_GPT_SIGNATURE_BYTES = 8;         // "EFI PART" at byte 0 of the header
        internal const int   PTB_GPT_REVISION_AT    = 8;          // the revision, 0x00010000 (1.0)
        internal const int   PTB_GPT_HEADER_SIZE_AT = 12;         // the header's size in bytes (92)
        internal const int   PTB_GPT_HEADER_CRC_AT  = 16;         // the header's CRC-32, computed with this field zero
        internal const int   PTB_GPT_CURRENT_AT     = 24;         // the sector this header is in
        internal const int   PTB_GPT_ALTERNATE_AT   = 32;         // the sector the other header is in
        internal const int   PTB_GPT_FIRST_USABLE_AT = 40;        // the first sector a partition may use
        internal const int   PTB_GPT_LAST_USABLE_AT = 48;         // the last sector a partition may use, inclusive
        internal const int   PTB_GPT_DISK_GUID_AT   = 56;         // the disk GUID
        internal const int   PTB_GPT_ENTRIES_LBA_AT = 72;         // the sector the entry array starts at
        internal const int   PTB_GPT_SLOTS_AT       = 80;         // how many entries the array holds
        internal const int   PTB_GPT_ENTRY_SIZE_AT  = 84;         // bytes per entry
        internal const int   PTB_GPT_ENTRIES_CRC_AT = 88;         // the entry array's CRC-32 over nSlots * cbEntry bytes
        internal const int   PTB_GPT_HEADER_BYTES   = 92;         // the header's size; the rest of its sector is zero
        internal const uint  PTB_GPT_REVISION       = 0x00010000; // the one revision there is
        internal const int   PTB_GPT_ENTRY_TYPE_AT  = 0;          // in an entry: the type GUID
        internal const int   PTB_GPT_ENTRY_UNIQUE_AT = 16;        // in an entry: the partition's own GUID
        internal const int   PTB_GPT_ENTRY_FIRST_AT = 32;         // in an entry: the first sector
        internal const int   PTB_GPT_ENTRY_LAST_AT  = 40;         // in an entry: the last sector, inclusive
        internal const int   PTB_GPT_ENTRY_ATTRS_AT = 48;         // in an entry: the attribute bits
        internal const int   PTB_GPT_ENTRY_NAME_AT  = 56;         // in an entry: the name, UTF-16LE, NUL-padded
        internal const int   PTB_GPT_NAME_CHARS     = 36;         // the name's capacity in UTF-16 characters
        internal const int   PTB_GPT_NAME_BYTES     = 72;         // the name field's size
        internal const uint  PTB_GPT_STD_SLOTS      = 128;        // the entry count a new table is given (what every tool makes)
        internal const uint  PTB_GPT_STD_ENTRY      = 128;        // the entry size a new table is given
        internal const ulong PTB_GPT_STD_ENTRIES_LBA = 2;         // where a new table's entries start: right after the header
        internal const uint  PTB_GPT_MIN_ENTRY      = 128;        // the smallest entry size accepted on a read
        internal const uint  PTB_GPT_MAX_ENTRY      = 4096;       // the largest entry size accepted on a read
        internal const uint  PTB_GPT_MAX_SLOTS      = 1024;       // the most slots accepted on a read (a sane table has 128)
        internal const int   PTB_GUID_BYTES         = 16;         // a GUID's size

        /* The sizes a new partition is aligned to, and a sector's bounds. */
        internal const ulong PTB_ALIGN_BYTES        = 4194304;    // a new partition starts on a 4 MiB boundary, a multiple of every tool's 1 MiB alignment; the reasons as found -- the start Raspberry Pi and Armbian images use, and an SD card's allocation unit being often 4 MiB, so a partition straddling one costs writes and wear -- are not checkable in this tree
        internal const uint  PTB_MIN_SECTOR         = 512;        // the smallest sector size handled
        internal const uint  PTB_MAX_SECTOR         = 4096;       // the largest
        internal const ulong PTB_MIN_DISK_SECTORS   = 2;          // the fewest sectors any table needs: an MBR's sector 0 and a GPT header's sector 1 (a GPT needs more -- PTB_GptLayoutProblem)

        /* The contents probe: where each signature sits in a partition's   */
        /* first page.                                                      */
        internal const int   PTB_PROBE_BYTES        = 4096;       // read at each partition's start: one page, which holds every signature looked for
        internal const int   PTB_EXT_SB             = 1024;       // the ext superblock's offset from the start of its filesystem
        internal const int   PTB_EXT_MAGIC          = 56;         // in the superblock: the 16-bit magic, 0xEF53 little-endian
        internal const int   PTB_EXT_COMPAT         = 92;         // in the superblock: the compatible feature word
        internal const int   PTB_EXT_INCOMPAT       = 96;         // in the superblock: the incompatible feature word
        internal const int   PTB_EXT_RO_COMPAT      = 100;        // in the superblock: the read-only-compatible feature word
        internal const int   PTB_EXT_LABEL          = 120;        // in the superblock: the volume label, 16 bytes, NUL-padded
        internal const int   PTB_EXT_LABEL_BYTES    = 16;         // the ext label's size
        internal const uint  PTB_EXT_COMPAT_JOURNAL = 0x0004;     // compat: has_journal -- set by ext3 and ext4, never by ext2
        internal const uint  PTB_EXT4_INCOMPAT      = 0x3FFC0;    // incompat bits only ext4 sets: extents, 64bit, mmp, flex_bg, ea_inode, dirdata, csum_seed, largedir, inline_data, encrypt, casefold (e2fsprogs ext2_fs.h); the mask also covers 0x800, which no feature uses, so it changes nothing
        internal const uint  PTB_EXT4_RO_COMPAT     = 0xA778;     // ro_compat bits only ext4 sets: huge_file, gdt_csum, dir_nlink, extra_isize, quota, bigalloc, metadata_csum, project, verity (e2fsprogs ext2_fs.h, bit for bit); replica, readonly, shared_blocks and orphan_present are left out
        internal const int   PTB_FAT_OEM            = 3;          // in a boot sector: the 8-byte OEM name ("EXFAT   ", "NTFS    " for those two; anything for a FAT)
        internal const int   PTB_FAT_TYPE16         = 54;         // in a FAT12 / FAT16 boot sector: the 8-byte "FAT12   " / "FAT16   " text
        internal const int   PTB_FAT_LABEL16        = 43;         // in a FAT12 / FAT16 boot sector: the 11-byte volume label
        internal const int   PTB_FAT_TYPE32         = 82;         // in a FAT32 boot sector: the 8-byte "FAT32   " text
        internal const int   PTB_FAT_LABEL32        = 71;         // in a FAT32 boot sector: the 11-byte volume label
        internal const int   PTB_FAT_LABEL_BYTES    = 11;         // a FAT label's size, space-padded
        internal const int   PTB_FAT_CODE_PAGE      = 437;        // the OEM code page a FAT12 / 16 / 32 label is read in: the engine's (FatFs FF_CODE_PAGE, engine\fatfs\source\ffconf.h); Windows writes one in the system's OEM code page, which is not 437 everywhere
        internal const int   PTB_SQUASHFS_MAGIC     = 0;          // a squashfs image starts with "hsqs" (little-endian) or "sqsh" (big-endian)
        internal const int   PTB_SWAP_SIGNATURE     = 4086;       // Linux swap: "SWAPSPACE2" or "SWAP-SPACE" in the last ten bytes of the first page
        internal const int   PTB_SIGNATURE_BYTES    = 8;          // the length of the FAT and OEM texts compared
        internal const int   PTB_SWAP_BYTES         = 10;         // the length of the swap signature

        /* The named types -- documentation table, in the order the layout     */
        /* window's Add dialog (DiskLayoutForm.cs) lists them: it takes the    */
        /* rows the table's kind has, in this order, and selects the first,    */
        /* so the first row of each kind is the default for a new partition.   */
        /* A GPT type with no MBR form has bMbr 0 and an MBR type with no      */
        /* GPT form has an empty GUID -- which is how the dialog leaves each   */
        /* out of the other kind's list.                                       */
        internal static readonly PTB_TypeName[] PTB_aTypes =
        {
            PTB_MakeType("Linux filesystem",      0x83, "0FC63DAF-8483-4772-8E79-3D69D8477DE4"),  // the general Linux data partition
            PTB_MakeType("Linux swap",            0x82, "0657FD6D-A4AB-43C4-84E5-0933C84B4F4F"),  // swap
            PTB_MakeType("EFI system",            0xEF, "C12A7328-F81F-11D2-BA4B-00A0C93EC93B"),  // the EFI system partition (FAT)
            PTB_MakeType("FAT32 (LBA)",           0x0C, ""),                                      // a FAT32 data partition (MBR only: on a GPT the same data is "Basic")
            PTB_MakeType("NTFS / exFAT",          0x07, ""),                                      // an NTFS or exFAT data partition (MBR only: on a GPT the same data is "Basic"); the words PTB_MbrTypeText gives 0x07
            PTB_MakeType("Basic",                 0x00, "EBD0A0A2-B9E5-4433-87C0-68B6B72699C7"),  // the basic-data type, for FAT, exFAT or NTFS data and used by many vendors' layouts (GPT only) -- Windows' "Basic data partition", gdisk's "Microsoft basic data"; the one row with this GUID, so PTB_GptTypeText names it
            PTB_MakeType("Linux LVM",             0x8E, "E6D6D379-F507-44C2-A23C-238F2A3DF928"),  // an LVM physical volume
            PTB_MakeType("Linux /home",           0x00, "933AC7E1-2EB4-4F13-B844-0E14E2AEF915"),  // the discoverable-partitions /home
            PTB_MakeType("Linux root (ARM 64)",   0x00, "B921B045-1DF0-41C3-AF44-4C6F280D3FAE"),  // the discoverable-partitions root for AArch64
            PTB_MakeType("Linux root (ARM 32)",   0x00, "69DAD710-2CE4-4E3C-B16C-21A1D49ABED3"),  // the discoverable-partitions root for 32-bit ARM
            PTB_MakeType("Linux root (x86-64)",   0x00, "4F68BCE3-E8CD-4DB1-96E7-FBCAF984B709"),  // the discoverable-partitions root for x86-64
            PTB_MakeType("FAT16 (LBA)",           0x0E, ""),                                      // a FAT16 data partition (MBR only)
            PTB_MakeType("Extended (LBA)",        0x0F, ""),                                      // an MBR container for logical partitions (not edited here)
        };

        /* The CRC-32 table, built once from the IEEE polynomial. */
        private static readonly uint[] PTB_auiCrcTable = PTB_MakeCrcTable();

        /*--------------------------------------------------------------------*/
        /* PTB_MakeType:                                                      */
        /*                                                                    */
        /* One row of the named-types table, from its words, its MBR byte     */
        /* and its GPT GUID text ("" for none).                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sName : the type in words.                                     */
        /*     bMbr  : the MBR type byte, or 0.                               */
        /*     sGuid : the GPT type GUID as text, or "".                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     PTB_TypeName : the row.                                        */
        /*--------------------------------------------------------------------*/
        private static PTB_TypeName PTB_MakeType(string sName, byte bMbr, string sGuid)
        {
            PTB_TypeName type;

            /* The words and the MBR byte as given; the GUID below. */
            type       = new PTB_TypeName();
            type.sName = sName;
            type.bMbr  = bMbr;

            if (sGuid.Length == 0)
            {   /* No GPT form: the empty GUID, which PTB_Validate refuses as   */
                /* a GPT entry's type, so an MBR-only row cannot be written     */
                /* into a GPT by mistake.                                       */
                type.guidGpt = Guid.Empty;
            }
            else
            {   /* The GUID as written in the table above.  A mistyped one      */
                /* throws here, while the static table is being built -- so     */
                /* the whole class fails at its first use (a type-initializer   */
                /* failure), not just the one row: a typo cannot go unseen.     */
                type.guidGpt = new Guid(sGuid);
            }

            /* The row, for the PTB_aTypes initializer. */
            return(type);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_MakeCrcTable:                                                  */
        /*                                                                    */
        /* The 256-entry lookup table for the IEEE CRC-32 -- the polynomial   */
        /* 0x04C11DB7 in its bit-reflected form, 0xEDB88320, since the GPT's  */
        /* CRC processes each byte low bit first -- the checksum a GPT uses   */
        /* for its header and its entry array.  Each entry is the remainder   */
        /* of one byte value, so PTB_Crc32 handles a byte in one step.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     uint[] : the table.                                            */
        /*--------------------------------------------------------------------*/
        private static uint[] PTB_MakeCrcTable()
        {
            uint[] auiTable;
            uint   uiValue;
            int    i;
            int    iBit;

            /* One entry for every byte value. */
            auiTable = new uint[256];

            for (i = 0; i < 256; i++)
            {   /* One table entry per iteration: the byte's remainder. */
                uiValue = (uint)i;

                for (iBit = 0; iBit < 8; iBit++)
                {   /* One bit per iteration. */
                    if ((uiValue & 1) != 0)
                    {   /* The low bit (the reflected top bit) is set: shift   */
                        /* and subtract the polynomial, which in this          */
                        /* carry-less arithmetic is an exclusive or.           */
                        uiValue = (uiValue >> 1) ^ 0xEDB88320u;
                    }
                    else
                    {   /* The bit is clear: nothing to subtract, shift only. */
                        uiValue = uiValue >> 1;
                    }
                }

                /* After eight bits, the byte's remainder. */
                auiTable[i] = uiValue;
            }

            /* The table. */
            return(auiTable);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_Crc32:                                                         */
        /*                                                                    */
        /* The IEEE CRC-32 of a run of bytes, as a GPT stores it: initial     */
        /* value all ones, final complement.  ("123456789" gives 0xCBF43926.) */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     abData  : the buffer.                                          */
        /*     iOffset : where the run starts.                                */
        /*     cbBytes : its length.                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     uint : the checksum.                                           */
        /*--------------------------------------------------------------------*/
        internal static uint PTB_Crc32(byte[] abData, int iOffset, int cbBytes)
        {
            uint uiCrc;
            int  i;

            /* The register starts at all ones, as the GPT's CRC requires. */
            uiCrc = 0xFFFFFFFFu;

            for (i = 0; i < cbBytes; i++)
            {   /* One byte per iteration through the table. */
                uiCrc = PTB_auiCrcTable[(uiCrc ^ abData[iOffset + i]) & 0xFF] ^ (uiCrc >> 8);
            }

            /* The complement is the stored value. */
            return(uiCrc ^ 0xFFFFFFFFu);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_ReadU32:                                                       */
        /*                                                                    */
        /* A 32-bit little-endian value out of a buffer, as every table field */
        /* is stored -- assembled byte by byte, so the result does not depend */
        /* on the machine's own byte order.  No bounds check: every caller    */
        /* reads at an offset inside a buffer it has already sized, and an    */
        /* offset past the end would throw (the array's own check) rather     */
        /* than read anything wrong.                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     abData  : the buffer.                                          */
        /*     iOffset : where the four bytes start.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     uint : the value.                                              */
        /*--------------------------------------------------------------------*/
        internal static uint PTB_ReadU32(byte[] abData, int iOffset)
        {
            /* Least significant byte first. */
            return((uint)abData[iOffset] | ((uint)abData[iOffset + 1] << 8) | ((uint)abData[iOffset + 2] << 16) |
                   ((uint)abData[iOffset + 3] << 24));
        }

        /*--------------------------------------------------------------------*/
        /* PTB_ReadU64:                                                       */
        /*                                                                    */
        /* A 64-bit little-endian value out of a buffer (the GPT's sector     */
        /* numbers and attribute bits), as two PTB_ReadU32 halves.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     abData  : the buffer.                                          */
        /*     iOffset : where the eight bytes start.                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ulong : the value.                                             */
        /*--------------------------------------------------------------------*/
        internal static ulong PTB_ReadU64(byte[] abData, int iOffset)
        {
            /* Two 32-bit halves, low half first. */
            return((ulong)PTB_ReadU32(abData, iOffset) | ((ulong)PTB_ReadU32(abData, iOffset + 4) << 32));
        }

        /*--------------------------------------------------------------------*/
        /* PTB_WriteU32:                                                      */
        /*                                                                    */
        /* A 32-bit value into a buffer, little-endian as the tables store    */
        /* it, byte by byte so the machine's own order does not matter.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     abData  : the buffer.                                          */
        /*     iOffset : where the four bytes go.                             */
        /*     uiValue : the value.                                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the bytes are in place.                                 */
        /*--------------------------------------------------------------------*/
        internal static void PTB_WriteU32(byte[] abData, int iOffset, uint uiValue)
        {
            /* Least significant byte first, each masked to its eight bits. */
            abData[iOffset]     = (byte)(uiValue & 0xFF);
            abData[iOffset + 1] = (byte)((uiValue >> 8) & 0xFF);
            abData[iOffset + 2] = (byte)((uiValue >> 16) & 0xFF);
            abData[iOffset + 3] = (byte)((uiValue >> 24) & 0xFF);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_WriteU64:                                                      */
        /*                                                                    */
        /* A 64-bit value into a buffer, little-endian, as two PTB_WriteU32   */
        /* halves.                                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     abData   : the buffer.                                         */
        /*     iOffset  : where the eight bytes go.                           */
        /*     ullValue : the value.                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the bytes are in place.                                 */
        /*--------------------------------------------------------------------*/
        internal static void PTB_WriteU64(byte[] abData, int iOffset, ulong ullValue)
        {
            /* The low half first, then the high half. */
            PTB_WriteU32(abData, iOffset, (uint)(ullValue & 0xFFFFFFFFUL));
            PTB_WriteU32(abData, iOffset + 4, (uint)(ullValue >> 32));
        }

        /*--------------------------------------------------------------------*/
        /* PTB_ReadGuid:                                                      */
        /*                                                                    */
        /* A GUID out of a buffer in the GPT's on-disk form: the first three  */
        /* fields little-endian and the last two as bytes -- which is exactly */
        /* the layout System.Guid's byte-array form uses, so no swapping is   */
        /* needed and the text of a GUID read here matches what every         */
        /* partition tool prints.                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     abData  : the buffer.                                          */
        /*     iOffset : where the 16 bytes are.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Guid : the GUID.                                               */
        /*--------------------------------------------------------------------*/
        internal static Guid PTB_ReadGuid(byte[] abData, int iOffset)
        {
            byte[] abGuid;

            /* The 16 bytes copied out on their own, the form Guid's   */
            /* constructor takes.                                      */
            abGuid = new byte[PTB_GUID_BYTES];
            Array.Copy(abData, iOffset, abGuid, 0, PTB_GUID_BYTES);

            /* System.Guid's byte order is the on-disk order. */
            return(new Guid(abGuid));
        }

        /*--------------------------------------------------------------------*/
        /* PTB_WriteGuid:                                                     */
        /*                                                                    */
        /* A GUID into a buffer in the GPT's on-disk form -- the same layout  */
        /* as System.Guid's byte-array form (PTB_ReadGuid), so its bytes are  */
        /* copied straight in.                                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     abData  : the buffer.                                          */
        /*     iOffset : where the 16 bytes go.                               */
        /*     guid    : the GUID.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the bytes are in place.                                 */
        /*--------------------------------------------------------------------*/
        internal static void PTB_WriteGuid(byte[] abData, int iOffset, Guid guid)
        {
            /* The GUID's 16 bytes, in on-disk order, at the offset. */
            Array.Copy(guid.ToByteArray(), 0, abData, iOffset, PTB_GUID_BYTES);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_NewNone:                                                       */
        /*                                                                    */
        /* A fresh "no table" for a disk of a given size and sector: no boot  */
        /* code, no signature, no entries.  Written, it erases whatever table */
        /* the disk had (PTB_Build).  It is also the base PTB_NewMbr and      */
        /* PTB_NewGpt build on, so the three kinds start from the same        */
        /* fields.  The layout window's new-table command (DiskLayoutForm.cs) */
        /* calls it for "no table".                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     ullDiskBytes : the disk's length.                              */
        /*     uiSector     : bytes per sector.                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     PTB_Table : the table, with no entries.                        */
        /*--------------------------------------------------------------------*/
        internal static PTB_Table PTB_NewNone(ulong ullDiskBytes, uint uiSector)
        {
            PTB_Table table;

            /* The shared fields: the kind, the geometry, zeroed boot code     */
            /* and signature, no disk GUID, and an empty entry list -- never   */
            /* null, since PTB_Validate and PTB_Build walk it without a        */
            /* test.  The GPT header's fields (ullFirstUsable to cbEntry)      */
            /* are left at the zero a new object has: only PTB_NewGpt fills    */
            /* them, and nothing reads them for the other two kinds.           */
            table                 = new PTB_Table();
            table.iKind           = PTB_KIND_NONE;
            table.uiSector        = uiSector;
            table.ullDiskBytes    = ullDiskBytes;
            table.abBoot          = new byte[PTB_MBR_BOOT_BYTES];
            table.uiDiskSignature = 0;
            table.guidDisk        = Guid.Empty;
            table.lstEntries      = new List<PTB_Entry>();

            /* A table of kind "none", which, if written, is PTB_Build's   */
            /* erase.                                                      */
            return(table);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_NewMbr:                                                        */
        /*                                                                    */
        /* A fresh, empty MBR for a disk of a given size and sector: no boot  */
        /* code and a random, non-zero disk signature (PTB_RandomSignature    */
        /* says why it must not be zero); its four slots empty.  Its one      */
        /* caller is DiskLayoutForm.cs's new-table command.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     ullDiskBytes : the disk's length.                              */
        /*     uiSector     : bytes per sector.                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     PTB_Table : the table, with no entries.                        */
        /*--------------------------------------------------------------------*/
        internal static PTB_Table PTB_NewMbr(ulong ullDiskBytes, uint uiSector)
        {
            PTB_Table table;

            /* The empty base, made an MBR with a signature of its own. */
            table                 = PTB_NewNone(ullDiskBytes, uiSector);
            table.iKind           = PTB_KIND_MBR;
            table.uiDiskSignature = PTB_RandomSignature();

            /* An MBR with four empty slots. */
            return(table);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_NewGpt:                                                        */
        /*                                                                    */
        /* A fresh, empty GPT for a disk of a given size and sector, in the   */
        /* standard layout every partitioning tool makes, with a disk GUID    */
        /* and protective-MBR signature of its own.  Its one caller is        */
        /* DiskLayoutForm.cs's new-table command.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     ullDiskBytes : the disk's length.                              */
        /*     uiSector     : bytes per sector.                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     PTB_Table : the table, with no entries.                        */
        /*--------------------------------------------------------------------*/
        internal static PTB_Table PTB_NewGpt(ulong ullDiskBytes, uint uiSector)
        {
            PTB_Table table;
            ulong     ullLastLba;
            ulong     ullEntrySectors;

            /* The empty base made a GPT: a random signature and disk GUID,   */
            /* so the new table is not taken for any other disk; 128 slots    */
            /* of 128 bytes, the size every tool makes; the entries at        */
            /* sector 2, right after the header in sector 1; the usable       */
            /* range starting after that array (sector 34 at 512-byte         */
            /* sectors, 6 at 4096), its end set below.                        */
            table                 = PTB_NewNone(ullDiskBytes, uiSector);
            table.iKind           = PTB_KIND_GPT;
            table.uiDiskSignature = PTB_RandomSignature();
            table.guidDisk        = Guid.NewGuid();
            table.nSlots          = PTB_GPT_STD_SLOTS;
            table.cbEntry         = PTB_GPT_STD_ENTRY;
            table.ullEntriesLba   = PTB_GPT_STD_ENTRIES_LBA;
            ullEntrySectors       = PTB_EntrySectors(PTB_GPT_STD_SLOTS, PTB_GPT_STD_ENTRY, uiSector);
            table.ullFirstUsable  = PTB_GPT_STD_ENTRIES_LBA + ullEntrySectors;
            table.ullLastUsable   = 0;

            if (PTB_LastLba(table, out ullLastLba) && ullLastLba >= PTB_GptMinLastLba(table))
            {   /* The disk holds the whole layout: the usable range ends    */
                /* below the backup array, which takes the ullEntrySectors   */
                /* sectors just under the backup header in the last          */
                /* sector -- so the "- 1" is the backup header and the       */
                /* subtraction after it the backup array.                    */
                table.ullLastUsable = ullLastLba - 1 - ullEntrySectors;
            }

            /* Otherwise the usable range stays empty (its end, 0, below      */
            /* its start), nothing is subtracted, and PTB_Validate refuses    */
            /* the table as too small for a GPT before anything is written.   */

            /* A GPT with 128 empty slots. */
            return(table);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_RandomSignature:                                               */
        /*                                                                    */
        /* A non-zero 32-bit disk signature for a new MBR.  Windows keys its  */
        /* record of a disk on this value and treats zero as "not yet         */
        /* initialised" (Windows' behaviour as understood when this was       */
        /* written; not checkable here), so a fresh table gets a random one.  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     uint : the signature, never 0.                                 */
        /*--------------------------------------------------------------------*/
        private static uint PTB_RandomSignature()
        {
            byte[] abGuid;
            uint   uiSignature;

            /* The first four bytes of a fresh GUID.  A new GUID is a         */
            /* random (version 4) one, whose fixed version and variant bits   */
            /* sit in bytes 7 and 8 (RFC 4122), so bytes 0 to 3 are all       */
            /* random -- enough for a value that only has to differ from      */
            /* other disks' and from zero; no secrecy rests on it.            */
            abGuid      = Guid.NewGuid().ToByteArray();
            uiSignature = PTB_ReadU32(abGuid, 0);

            if (uiSignature == 0)
            {   /* The one value that would read as "uninitialised". */
                uiSignature = 1;
            }

            /* The signature. */
            return(uiSignature);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_CopyTable:                                                     */
        /*                                                                    */
        /* A deep copy of a table: every field, the boot code's bytes and     */
        /* every entry copied, nothing shared.  The partition window hands    */
        /* the worker a copy of the table it shows, so the worker can edit    */
        /* or build it on its own thread while the window's own table stays   */
        /* untouched until the edited copy comes back to be shown.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     table : the table to copy; not changed.                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     PTB_Table : the copy.                                          */
        /*--------------------------------------------------------------------*/
        internal static PTB_Table PTB_CopyTable(PTB_Table table)
        {
            PTB_Table objCopy;
            PTB_Entry objEntry;
            int       i;

            /* The table's own fields, every one of PTB_Table's: values, the   */
            /* GUID a value too -- a field added to PTB_Table must be added    */
            /* here, or the copy silently loses it.  The boot code is an       */
            /* array, so its bytes are copied rather than the reference: a     */
            /* write built from the copy then never depends on the window      */
            /* leaving its own array alone.                                    */
            objCopy = new PTB_Table();
            objCopy.iKind           = table.iKind;
            objCopy.uiSector        = table.uiSector;
            objCopy.ullDiskBytes    = table.ullDiskBytes;
            objCopy.abBoot          = (byte[])table.abBoot.Clone();
            objCopy.uiDiskSignature = table.uiDiskSignature;
            objCopy.guidDisk        = table.guidDisk;
            objCopy.ullFirstUsable  = table.ullFirstUsable;
            objCopy.ullLastUsable   = table.ullLastUsable;
            objCopy.ullEntriesLba   = table.ullEntriesLba;
            objCopy.nSlots          = table.nSlots;
            objCopy.cbEntry         = table.cbEntry;
            objCopy.lstEntries      = new List<PTB_Entry>();

            for (i = 0; i < table.lstEntries.Count; i++)
            {   /* One entry per iteration, copied field by field -- every     */
                /* one of PTB_Entry's, which a new field must join -- in the   */
                /* same slot order.  Nothing is shared that could change:      */
                /* GUIDs are values and strings cannot be altered.             */
                objEntry = new PTB_Entry();
                objEntry.iSlot         = table.lstEntries[i].iSlot;
                objEntry.ullFirstLba   = table.lstEntries[i].ullFirstLba;
                objEntry.ullLastLba    = table.lstEntries[i].ullLastLba;
                objEntry.guidType      = table.lstEntries[i].guidType;
                objEntry.guidUnique    = table.lstEntries[i].guidUnique;
                objEntry.ullAttributes = table.lstEntries[i].ullAttributes;
                objEntry.sName         = table.lstEntries[i].sName;
                objEntry.bType         = table.lstEntries[i].bType;
                objEntry.bBoot         = table.lstEntries[i].bBoot;
                objEntry.sContent      = table.lstEntries[i].sContent;
                objCopy.lstEntries.Add(objEntry);
            }

            /* The copy. */
            return(objCopy);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_EntrySectors:                                                  */
        /*                                                                    */
        /* How many sectors a GPT entry array of nSlots entries of cbEntry    */
        /* bytes occupies, rounded up to whole sectors -- the room between    */
        /* the header and the first usable sector, and below the backup       */
        /* header.                                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     nSlots   : entries in the array.                               */
        /*     cbEntry  : bytes per entry.                                    */
        /*     uiSector : bytes per sector.                                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ulong : the sector count.                                      */
        /*--------------------------------------------------------------------*/
        internal static ulong PTB_EntrySectors(uint nSlots, uint cbEntry, uint uiSector)
        {
            ulong cbArray;

            /* The array's bytes, in 64 bits so a large slot count cannot   */
            /* overflow the product.                                        */
            cbArray = (ulong)nSlots * (ulong)cbEntry;

            /* Round up to whole sectors. */
            return((cbArray + uiSector - 1) / uiSector);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_LastLba:                                                       */
        /*                                                                    */
        /* The disk's last sector, the bound every placement test and every   */
        /* builder uses -- worked out in this one place, so that no caller    */
        /* divides by a sector size nothing has checked or subtracts 1 from a */
        /* sector count of zero.  A disk shorter than PTB_MIN_DISK_SECTORS    */
        /* holds no table of either kind, so it has no last sector here.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     table      : the table (its sector size and disk length).      */
        /*     ullLastLba : receives the last sector; 0 when there is none.   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : false when the sector size is not one handled (512 to   */
        /*            4096) or the disk is shorter than two sectors.          */
        /*--------------------------------------------------------------------*/
        internal static bool PTB_LastLba(PTB_Table table, out ulong ullLastLba)
        {
            /* None until the two tests pass. */
            ullLastLba = 0;

            if (table.uiSector < PTB_MIN_SECTOR || table.uiSector > PTB_MAX_SECTOR)
            {   /* No sector size this code lays out for -- tested before the   */
                /* division, so a zero size cannot reach it.                    */
                return(false);
            }

            if (table.ullDiskBytes / table.uiSector < PTB_MIN_DISK_SECTORS)
            {   /* Too short for any table: sector 0 alone, or less than one. */
                return(false);
            }

            /* Sectors are numbered from 0, so the last is the count less one. */
            ullLastLba = (table.ullDiskBytes / table.uiSector) - 1;
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_GptMinLastLba:                                                 */
        /*                                                                    */
        /* The smallest last sector a disk can have and still hold the GPT    */
        /* this table describes: the protective MBR (sector 0), the header    */
        /* (sector 1), the primary entry array, at least one usable sector,   */
        /* the backup entry array and the backup header in the last sector -- */
        /* 2 x the array's sectors + 4 sectors in all, so the last is that    */
        /* less one.  A smaller disk would leave the usable range's end, the  */
        /* backup array's start, below its first usable sector or wrapped.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     table : the table (its slot count, entry size and sector, the  */
        /*             sector size already checked by the caller).  Any slot  */
        /*             count and entry size give a true answer: their product */
        /*             is worked in 64 bits (PTB_EntrySectors), where even    */
        /*             the largest pair of 32-bit values cannot overflow.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ulong : the least last sector.                                 */
        /*--------------------------------------------------------------------*/
        private static ulong PTB_GptMinLastLba(PTB_Table table)
        {
            ulong ullEntrySectors;

            /* Two arrays, two headers, the MBR and one usable sector, less one. */
            ullEntrySectors = PTB_EntrySectors(table.nSlots, table.cbEntry, table.uiSector);
            return((2 * ullEntrySectors) + 3);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_GptLayoutProblem:                                              */
        /*                                                                    */
        /* Whether a GPT's own sectors fit THIS disk with room between them:  */
        /* the one test of a GPT's layout, run by PTB_Validate before any     */
        /* write and by PTB_ReadGpt so a table that cannot be written back    */
        /* says so when it is read.  PTB_BuildGpt writes the protective MBR   */
        /* to sector 0, the header to sector 1, the primary entry array where */
        /* the header says, and the backup array and backup header into the   */
        /* last (array sectors + 1) sectors of the disk as it is NOW -- so    */
        /* each of those must lie outside the usable range, or a write of the */
        /* table would land on a partition's data.  The tests run in the      */
        /* order their own arithmetic needs, and the first that fails gives   */
        /* the words.                                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     table      : the GPT.                                          */
        /*     ullLastLba : the disk's last sector (PTB_LastLba).             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the first problem found, in words; "" when the layout */
        /*              fits.                                                 */
        /*--------------------------------------------------------------------*/
        internal static string PTB_GptLayoutProblem(PTB_Table table, ulong ullLastLba)
        {
            ulong ullEntrySectors;
            ulong ullBackupEntries;
            ulong ullMinLastLba;

            if (table.cbEntry < PTB_GPT_MIN_ENTRY || table.cbEntry > PTB_GPT_MAX_ENTRY || (table.cbEntry % 8) != 0 ||
                table.nSlots == 0 || table.nSlots > PTB_GPT_MAX_SLOTS)
            {   /* An entry size or slot count no GPT has -- the limits        */
                /* PTB_ReadGpt accepts on a read: the array's size, which      */
                /* everything below is measured with, means nothing.  Tested   */
                /* first for that reason.                                      */
                return("The table's entry size or count is not one a GUID partition table has.");
            }

            /* The array's sectors, and the least last sector the layout      */
            /* needs (PTB_GptMinLastLba) -- tested next, before anything is   */
            /* subtracted from the last sector, so nothing below can wrap.    */
            ullEntrySectors = PTB_EntrySectors(table.nSlots, table.cbEntry, table.uiSector);
            ullMinLastLba   = PTB_GptMinLastLba(table);

            if (ullLastLba < ullMinLastLba)
            {   /* The disk cannot hold both headers, both arrays and one    */
                /* usable sector.  The count is said, so "how much bigger"   */
                /* is answered too.                                          */
                return("The disk is too small for a GUID partition table: it needs at least " + (ullMinLastLba + 1) +
                       " sectors of " + table.uiSector + " bytes.");
            }

            /* Where the backup array goes: just below the backup header in   */
            /* the last sector (no wrap: the test above leaves room).         */
            ullBackupEntries = ullLastLba - ullEntrySectors;

            if (table.ullFirstUsable < 2 || table.ullLastUsable < table.ullFirstUsable)
            {   /* The usable range starts on the protective MBR or the    */
                /* header, or ends before it starts: a header damaged or   */
                /* made by hand, against which no partition test means     */
                /* anything.                                               */
                return("The table's usable range does not fit the disk.");
            }

            if (table.ullEntriesLba < 2 || table.ullEntriesLba > table.ullFirstUsable ||
                ullEntrySectors > table.ullFirstUsable - table.ullEntriesLba)
            {   /* The primary array starts on the MBR or the header, or does   */
                /* not end before the first usable sector: written there, it    */
                /* would cover a header or a partition's first sectors.  The    */
                /* end is compared by subtraction (the first usable sector      */
                /* less the array's start, which the test before it keeps       */
                /* from wrapping), so a huge sector number in the header        */
                /* cannot wrap the sum and pass.                                */
                return("The table's entry array, at sector " + table.ullEntriesLba + ", does not lie between its header (sector 1) and its first usable sector (" +
                       table.ullFirstUsable + ").");
            }

            if (table.ullLastUsable >= ullBackupEntries)
            {   /* The usable range reaches the sectors the backup array and     */
                /* header are written to on this disk: written, they would       */
                /* cover the end of any partition there.  This is where an       */
                /* image cut shorter than the disk its table was made for        */
                /* fails, and the range is deliberately NOT pulled in to make    */
                /* room: a partition may already occupy those sectors.  So the   */
                /* table is refused, and the words say why.                      */
                return("The table's usable range ends at sector " + table.ullLastUsable + ", but its backup copy goes in sectors " + ullBackupEntries +
                       " to " + ullLastLba + " at this disk's end -- the disk may be shorter than the one the table was made for.");
            }

            /* Every sector of the layout has its place.  With the usable      */
            /* range below the backup array, the primary array's sector lies   */
            /* below the disk's end, so its byte offset (sector x size) is     */
            /* below the disk's length and cannot wrap where PTB_BuildGpt      */
            /* multiplies it.  (PTB_ReadGpt's read of the array comes before   */
            /* this test and has an on-disk test of its own.)                  */
            return("");
        }

        /*--------------------------------------------------------------------*/
        /* PTB_Read:                                                          */
        /*                                                                    */
        /* Fill a table from whatever an image or device starts with, through */
        /* the reader given: no table, an MBR or a GPT, with every field a    */
        /* later write needs kept as read, and one line saying what was       */
        /* found.  The reader may be called from any thread (the program      */
        /* reads on the command worker, CommandNativeSteps.cs), so nothing    */
        /* here touches a window.  The steps are in the body.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pfnRead      : how to read the image or device.                */
        /*     ullDiskBytes : its length, bytes.                              */
        /*     uiSector     : its bytes per sector.                           */
        /*     table        : receives the table; its kind is PTB_KIND_NONE   */
        /*                    and its entry list empty when there is none.    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : one line saying what was found.                       */
        /*--------------------------------------------------------------------*/
        internal static string PTB_Read(PRT_Reader pfnRead, ulong ullDiskBytes, uint uiSector, PTB_Table table)
        {
            byte[] abSector;
            byte[] abProbe;
            string sAccount;
            string sWhole;
            bool   bProtective;
            int    iEntry;

            /* Start as "no table", every shared field set, BEFORE any         */
            /* refusal: each early return below then leaves a consistent       */
            /* empty table (the layout read relies on that -- its result's     */
            /* table is never half-filled, CommandNativeSteps.cs).  The        */
            /* GPT header's fields are not reset: only a GPT reads them, and   */
            /* PTB_ReadGpt sets them all before the kind becomes GPT.          */
            table.iKind           = PTB_KIND_NONE;
            table.uiSector        = uiSector;
            table.ullDiskBytes    = ullDiskBytes;
            table.abBoot          = new byte[PTB_MBR_BOOT_BYTES];
            table.uiDiskSignature = 0;
            table.guidDisk        = Guid.Empty;
            table.lstEntries      = new List<PTB_Entry>();

            if (uiSector < PTB_MIN_SECTOR || uiSector > PTB_MAX_SECTOR || ullDiskBytes / uiSector < PTB_MIN_DISK_SECTORS)
            {   /* A sector size outside 512 to 4096, which nothing here lays   */
                /* a table out for, or a disk shorter than two sectors: an      */
                /* MBR needs sector 0 and a GPT sector 1 as well, so neither    */
                /* kind fits.  The table stays the empty "none" set above,      */
                /* and PTB_Validate refuses any table on such a disk            */
                /* (PTB_LastLba).                                               */
                return("Too small to hold a partition table.");
            }

            /* STEP 1: sector 0, read whole at the disk's sector size -- on a   */
            /* 4096-byte-sector disk the MBR fields still sit in its first      */
            /* 512 bytes, at the same offsets.                                  */
            abSector = new byte[uiSector];

            if (!pfnRead(0, abSector))
            {   /* Could not be read at all: the table stays "none", and the   */
                /* account says why.                                           */
                return("The first sector could not be read.");
            }

            /* The boot code and disk signature kept as read, so a table    */
            /* written back later puts them back unchanged (a boot loader   */
            /* living there survives).                                      */
            Array.Copy(abSector, 0, table.abBoot, 0, PTB_MBR_BOOT_BYTES);
            table.uiDiskSignature = PTB_ReadU32(abSector, PTB_MBR_SIGNATURE_AT);

            if (abSector[PTB_MBR_BOOT_SIG] != 0x55 || abSector[PTB_MBR_BOOT_SIG + 1] != 0xAA)
            {   /* No 0x55 0xAA at bytes 510-511, the mark every MBR (and boot   */
                /* sector) carries: the 64 bytes where entries would be are      */
                /* whatever happens to be there, so there is no table.  The      */
                /* boot bytes and signature read are not a table's either, so    */
                /* they are dropped and a new table starts clean.                */
                table.abBoot          = new byte[PTB_MBR_BOOT_BYTES];
                table.uiDiskSignature = 0;
                return("No partition table.");
            }

            /* STEP 2: a protective entry means a GPT. */
            bProtective = false;

            for (iEntry = 0; iEntry < PTB_MBR_ENTRIES; iEntry++)
            {   /* One primary entry per iteration: its type byte, at +4.    */
                /* Any slot counts, not only slot 0 (a hybrid MBR puts the   */
                /* 0xEE entry beside real ones); the loop does not stop at   */
                /* the first, which changes nothing but costs three tests.   */
                if (abSector[PTB_MBR_TABLE + (iEntry * PTB_MBR_ENTRY) + 4] == PTB_TYPE_PROTECTIVE)
                {   /* Type 0xEE: this MBR only guards the disk, and its real   */
                    /* table is a GPT -- read below, after STEP 2a.             */
                    bProtective = true;
                }
            }

            /* STEP 2a: a filesystem over the WHOLE disk, with no table.  A      */
            /* FAT, exFAT or NTFS boot sector ends in the same 0x55 0xAA an      */
            /* MBR does, so without this test a card formatted whole as FAT16    */
            /* would read as an MBR whose four "entries" were the boot           */
            /* sector's own bytes -- shown as an empty MBR and free space.       */
            /* So the start of the disk is probed first: when it names a         */
            /* filesystem and the four slots do NOT form a believable table,     */
            /* it is no table, and the layout window (DiskLayoutForm.cs) shows   */
            /* the one "whole disk" block with what it holds.  A protective      */
            /* entry wins (a GPT), and so                                        */
            /* does a believable MBR, so a real table is never taken for a       */
            /* filesystem by a chance string in its boot code.                   */
            if (!bProtective && !PTB_MbrLooksReal(abSector, ullDiskBytes / uiSector))
            {   /* No believable table: is there a filesystem at sector 0? */
                abProbe = new byte[PTB_PROBE_BYTES];

                if (ullDiskBytes >= (ulong)abProbe.Length && pfnRead(0, abProbe))
                {   /* The disk's first page, as the probe reads a partition's. */
                    sWhole = PTB_ContentText(abProbe);

                    if (sWhole.Length != 0)
                    {   /* A filesystem over the whole disk: no table, and    */
                        /* none of sector 0 kept as a table's (it is the      */
                        /* filesystem's boot sector).  The account names      */
                        /* what is there, the label's two-space gap           */
                        /* (PTB_LabelText) made one space for the sentence.   */
                        table.abBoot          = new byte[PTB_MBR_BOOT_BYTES];
                        table.uiDiskSignature = 0;
                        return("No partition table: the whole disk holds " + sWhole.Replace("  ", " ") + ".");
                    }
                }
            }

            if (bProtective)
            {   /* Read the GPT.  PTB_ReadGpt sets the kind to GPT only when it   */
                /* has read the whole table; any refusal leaves it NONE, and      */
                /* the MBR below is read instead.  Its account line -- the        */
                /* refusal's own reason -- is then dropped for the fallback's     */
                /* more general one.                                              */
                sAccount = PTB_ReadGpt(pfnRead, table);

                if (table.iKind == PTB_KIND_GPT)
                {   /* The GPT is the table. */
                    return(sAccount);
                }
            }

            /* STEP 3: an MBR, its four primaries as they are. */
            table.iKind = PTB_KIND_MBR;

            for (iEntry = 0; iEntry < PTB_MBR_ENTRIES; iEntry++)
            {   /* One primary entry per iteration. */
                PTB_ReadMbrEntry(abSector, iEntry, table);
            }

            if (bProtective)
            {   /* A protective MBR whose GPT could not be read: say so, since   */
                /* what is shown is the guard, not the disk's real table --      */
                /* the 0xEE entry is among the entries listed.                   */
                return("A protective MBR, but its GPT could not be read; the MBR's " + table.lstEntries.Count + " entry(ies) are shown.");
            }

            /* An ordinary MBR: its account, with or without the note below. */
            if (PTB_CountExtended(table) != 0)
            {   /* One or more extended containers, whose logical partitions   */
                /* this file does not read: say so, and where they can be      */
                /* seen, so their absence is not mistaken for free space.      */
                return("MBR partition table: " + table.lstEntries.Count + " primary partition(s), " + PTB_CountExtended(table) +
                       " of them EXTENDED -- its logical partitions are not shown or edited here (File > Open lists them).");
            }

            /* A plain MBR: the account counts its primaries. */
            return("MBR partition table: " + table.lstEntries.Count + " primary partition(s).");
        }

        /*--------------------------------------------------------------------*/
        /* PTB_ReadMbrEntry:                                                  */
        /*                                                                    */
        /* One primary slot of sector 0's MBR added to the table as an entry, */
        /* when the slot is in use; an unused slot adds nothing.              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     abSector : sector 0.                                           */
        /*     iSlot    : the slot, 0 to 3.                                   */
        /*     table    : receives the entry.                                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the entry is added when the slot is used.               */
        /*--------------------------------------------------------------------*/
        private static void PTB_ReadMbrEntry(byte[] abSector, int iSlot, PTB_Table table)
        {
            PTB_Entry entry;
            uint      uiStart;
            uint      uiCount;
            int       iAt;

            /* The slot's 16 bytes start at 446 + 16 * slot; its start sector   */
            /* is at +8 and its sector count at +12, both 32-bit.  The two      */
            /* CHS addresses (+1 to +3, +5 to +7) are not read: nothing in      */
            /* this program uses them, the LBA fields say the same thing        */
            /* without CHS's 8 GB limit, and a write derives them again from    */
            /* the sectors (PTB_WriteChs).                                      */
            iAt     = PTB_MBR_TABLE + (iSlot * PTB_MBR_ENTRY);
            uiStart = PTB_ReadU32(abSector, iAt + 8);
            uiCount = PTB_ReadU32(abSector, iAt + 12);

            if (abSector[iAt + 4] == 0 || uiCount == 0)
            {   /* Type 0, or no sectors at all: how an MBR marks a slot      */
                /* unused.  Nothing is added, so the slot reads as free and   */
                /* PTB_FreeSlot may give its number to a new partition.       */
                return;
            }

            /* The used slot as an entry: its sectors (the last worked out     */
            /* in 64 bits, so start + count cannot wrap), no GPT fields, its   */
            /* type byte and its active flag, and no contents yet (PTB_Probe   */
            /* fills those).  Active is the 0x80 bit alone: a malformed        */
            /* status byte (neither 0 nor 0x80) is not refused here, and is    */
            /* written back as 0x80 or 0.  A start of 0 is kept as read too;   */
            /* PTB_Validate refuses it before any write.                       */
            entry               = new PTB_Entry();
            entry.iSlot         = iSlot;
            entry.ullFirstLba   = uiStart;
            entry.ullLastLba    = (ulong)uiStart + (ulong)uiCount - 1;
            entry.guidType      = Guid.Empty;
            entry.guidUnique    = Guid.Empty;
            entry.ullAttributes = 0;
            entry.sName         = "";
            entry.bType         = abSector[iAt + 4];
            entry.bBoot         = ((abSector[iAt] & PTB_MBR_ACTIVE) != 0);
            entry.sContent      = "";
            table.lstEntries.Add(entry);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_MbrLooksReal:                                                  */
        /*                                                                    */
        /* Whether sector 0's four slots form a believable partition table.   */
        /* Used only to decide between "an MBR" and "a filesystem over the    */
        /* whole disk" when sector 0 also names a filesystem, since both      */
        /* carry the same 0x55 0xAA.                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     abSector       : sector 0.                                     */
        /*     ullDiskSectors : the disk's length in sectors.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the slots read as a real table.               */
        /*--------------------------------------------------------------------*/
        internal static bool PTB_MbrLooksReal(byte[] abSector, ulong ullDiskSectors)
        {
            uint uiStart;
            uint uiCount;
            int  nUsed;
            int  iAt;
            int  iSlot;

            /* No used slot seen yet. */
            nUsed = 0;

            for (iSlot = 0; iSlot < PTB_MBR_ENTRIES; iSlot++)
            {   /* One slot per iteration. */

                /* The slot's start and count, as PTB_ReadMbrEntry reads them. */
                iAt     = PTB_MBR_TABLE + (iSlot * PTB_MBR_ENTRY);
                uiStart = PTB_ReadU32(abSector, iAt + 8);
                uiCount = PTB_ReadU32(abSector, iAt + 12);

                if (abSector[iAt + 4] == 0 || uiCount == 0)
                {   /* Empty: says nothing either way. */
                    continue;
                }

                if ((abSector[iAt] != 0x00 && abSector[iAt] != PTB_MBR_ACTIVE) || uiStart == 0 ||
                    (ulong)uiStart + (ulong)uiCount > ullDiskSectors)
                {   /* A status byte other than 0 or 0x80, a start on sector 0   */
                    /* (the table's own sector), or a partition running past     */
                    /* the disk's end: no real table's slot holds any of         */
                    /* those, so one such slot is enough to say these bytes      */
                    /* are a boot sector's code, not a table.  (An MBR whose     */
                    /* partition runs past the end of a cut-short image fails    */
                    /* here too.)                                                */
                    return(false);
                }

                /* A used slot that passed all three tests. */
                nUsed++;
            }

            /* Believable only with something in it: an empty MBR is not       */
            /* proof of a table on its own -- and where the probe then finds   */
            /* no filesystem either, the caller still reads it as an MBR.      */
            return(nUsed != 0);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_ReadGpt:                                                       */
        /*                                                                    */
        /* The GPT behind a protective MBR, read into the table with every    */
        /* field of its header and of each used slot that a later write       */
        /* needs.  The table becomes a GPT only once all of it has been       */
        /* read: anything that stops the read leaves the kind at NONE, and    */
        /* the caller then reads the MBR instead.  A table whose checksums    */
        /* are wrong is still read, with the fault in the account, and so is  */
        /* one whose layout could not be written back to this disk            */
        /* (PTB_GptLayoutProblem), the reason in the account too.  Only the   */
        /* primary copy is read, never the backup at the end of the disk.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pfnRead : how to read the image or device.                     */
        /*     table   : receives the GPT; its kind becomes PTB_KIND_GPT      */
        /*               when one was read.                                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the account of what was found.                        */
        /*--------------------------------------------------------------------*/
        private static string PTB_ReadGpt(PRT_Reader pfnRead, PTB_Table table)
        {
            PTB_Entry entry;
            byte[]    abHeader;
            byte[]    abEntries;
            string    sNote;
            string    sProblem;
            ulong     cbArray;
            ulong     ullDiskSectors;
            ulong     ullEntrySectors;
            ulong     ullLastLba;
            uint      cbHeader;
            uint      uiStoredCrc;
            uint      i;
            int       iAt;

            /* Sector 1 -- at byte uiSector, so a 4096-byte-sector disk's      */
            /* header is found at 4096, not 512.  This primary header is the   */
            /* only one read: the backup header at the disk's last sector is   */
            /* never consulted, so when the primary (or its entries) is        */
            /* refused below, a good backup does not rescue it -- the          */
            /* refusal stands and the MBR is shown.  When the primary IS       */
            /* read, a write puts both copies back (PTB_BuildGpt), so a        */
            /* damaged backup is repaired by the next write.                   */
            abHeader = new byte[table.uiSector];

            if (!pfnRead(table.uiSector, abHeader))
            {   /* Sector 1 could not be read (the reader's range check or a   */
                /* read failure): no header to check, so the kind stays NONE   */
                /* and the caller shows the MBR instead.                       */
                return("The GPT header sector could not be read.");
            }

            if (Encoding.ASCII.GetString(abHeader, 0, PTB_GPT_SIGNATURE_BYTES) != "EFI PART")
            {   /* Sector 1 does not begin with "EFI PART", the GPT header's   */
                /* signature: the 0xEE entry is a leftover, or the header      */
                /* was wiped.  There is no GPT to read; the MBR is shown.      */
                return("No GPT header behind the protective MBR.");
            }

            /* The header's own size: the checksum below covers exactly that   */
            /* many bytes, so it must be at least the 92 defined and fit the   */
            /* sector it was read from.                                        */
            cbHeader = PTB_ReadU32(abHeader, PTB_GPT_HEADER_SIZE_AT);

            if (cbHeader < PTB_GPT_HEADER_BYTES || cbHeader > table.uiSector)
            {   /* Under 92 bytes (the fields read below would not all be    */
                /* covered by the checksum) or over a sector (the checksum   */
                /* would run past what was read): the header cannot be       */
                /* checked, so none of it is trusted and the MBR is shown.   */
                return("The GPT header's size field is not believable.");
            }

            /* Every field a later write needs, kept as read.  They are set   */
            /* before the geometry test below, so a refusal there leaves      */
            /* them filled while the kind stays NONE; the MBR the caller      */
            /* falls back to does not use them.                               */
            table.guidDisk       = PTB_ReadGuid(abHeader, PTB_GPT_DISK_GUID_AT);
            table.ullFirstUsable = PTB_ReadU64(abHeader, PTB_GPT_FIRST_USABLE_AT);
            table.ullLastUsable  = PTB_ReadU64(abHeader, PTB_GPT_LAST_USABLE_AT);
            table.ullEntriesLba  = PTB_ReadU64(abHeader, PTB_GPT_ENTRIES_LBA_AT);
            table.nSlots         = PTB_ReadU32(abHeader, PTB_GPT_SLOTS_AT);
            table.cbEntry        = PTB_ReadU32(abHeader, PTB_GPT_ENTRY_SIZE_AT);

            if (table.cbEntry < PTB_GPT_MIN_ENTRY || table.cbEntry > PTB_GPT_MAX_ENTRY || (table.cbEntry % 8) != 0 ||
                table.nSlots == 0 || table.nSlots > PTB_GPT_MAX_SLOTS)
            {   /* An entry size under 128 (an entry's fields would not fit)   */
                /* or over 4 KiB or not a multiple of 8, or a slot count of    */
                /* 0 or over 1024: no real table has these.  The array is      */
                /* about to be allocated and read at count x size, so they     */
                /* are refused first -- which also holds that read to 4 MiB.   */
                return("The GPT's entry size or count is not believable.");
            }

            /* The header's checksum is computed with its own field zeroed,    */
            /* as the GPT defines it, so the stored value is taken out first   */
            /* and the field cleared in the copy read (nothing is written      */
            /* back from this buffer).  No fault noted yet.                    */
            uiStoredCrc = PTB_ReadU32(abHeader, PTB_GPT_HEADER_CRC_AT);
            PTB_WriteU32(abHeader, PTB_GPT_HEADER_CRC_AT, 0);
            sNote = "";

            if (PTB_Crc32(abHeader, 0, (int)cbHeader) != uiStoredCrc)
            {   /* The stored checksum does not match: the header is damaged    */
                /* or was changed by hand.  Its fields are still used -- they   */
                /* passed the tests above -- with the fault noted in the        */
                /* account; the next write puts a right checksum on them.       */
                /* A wrong field that passed those tests is used as it is.      */
                sNote = " (the header's checksum is wrong)";
            }

            /* Where the header puts the entry array must be on this disk,     */
            /* past the header, before its byte offset is even worked out:     */
            /* the sector number is the header's own and could be anything,    */
            /* and multiplied by the sector size a huge one wraps 64 bits to   */
            /* a small offset -- which would read (and a later write would     */
            /* put) the array somewhere else entirely.  The test is by         */
            /* subtraction, so it cannot wrap itself.  (PTB_Read has held      */
            /* the disk to at least two sectors and the sector size to 512     */
            /* to 4096; the array's sectors are within 4 MiB, by the test      */
            /* above.)                                                         */
            ullDiskSectors  = table.ullDiskBytes / table.uiSector;
            ullEntrySectors = PTB_EntrySectors(table.nSlots, table.cbEntry, table.uiSector);

            if (table.ullEntriesLba < 2 || table.ullEntriesLba > ullDiskSectors || ullEntrySectors > ullDiskSectors - table.ullEntriesLba)
            {   /* The array would start on the MBR or the header, or run off   */
                /* the disk's end: nothing there can be trusted as entries,     */
                /* so the kind stays NONE and the MBR is shown.                 */
                return("The GPT's entry array lies outside the disk.");
            }

            /* The entry array, in one read, from where the header says it     */
            /* is (at most 1024 slots of 4 KiB: 4 MiB, by the test above).     */
            /* Whether that place leaves the usable range clear is the         */
            /* layout test's question, asked once the table is read (below).   */
            cbArray   = (ulong)table.nSlots * (ulong)table.cbEntry;
            abEntries = new byte[cbArray];

            if (!pfnRead(table.ullEntriesLba * table.uiSector, abEntries))
            {   /* The array could not be read -- a cut-short image, or a   */
                /* failing device -- so there are no entries to read; the   */
                /* kind stays NONE and the MBR is shown.                    */
                return("The GPT's entry array could not be read.");
            }

            if (PTB_Crc32(abEntries, 0, (int)cbArray) != PTB_ReadU32(abHeader, PTB_GPT_ENTRIES_CRC_AT))
            {   /* The entries' checksum does not match: noted the same way.   */
                /* The slots are still read as they are, and a write puts      */
                /* the right checksum on what was read.                        */
                sNote = sNote + " (the entries' checksum is wrong)";
            }

            for (i = 0; i < table.nSlots; i++)
            {   /* One slot per iteration. */

                /* The slot's offset in the array (within 4 MiB, so the int   */
                /* cast is safe).                                             */
                iAt = (int)(i * table.cbEntry);

                if (PTB_ReadGuid(abEntries, iAt + PTB_GPT_ENTRY_TYPE_AT) == Guid.Empty)
                {   /* An unused slot: a GPT marks one by an all-zero type. */
                    continue;
                }

                /* The used slot as an entry, every GPT field kept as read so    */
                /* a write puts it back unchanged; the name is UTF-16, its       */
                /* NUL padding trimmed; no MBR fields; contents for PTB_Probe.   */
                entry               = new PTB_Entry();
                entry.iSlot         = (int)i;
                entry.guidType      = PTB_ReadGuid(abEntries, iAt + PTB_GPT_ENTRY_TYPE_AT);
                entry.guidUnique    = PTB_ReadGuid(abEntries, iAt + PTB_GPT_ENTRY_UNIQUE_AT);
                entry.ullFirstLba   = PTB_ReadU64(abEntries, iAt + PTB_GPT_ENTRY_FIRST_AT);
                entry.ullLastLba    = PTB_ReadU64(abEntries, iAt + PTB_GPT_ENTRY_LAST_AT);
                entry.ullAttributes = PTB_ReadU64(abEntries, iAt + PTB_GPT_ENTRY_ATTRS_AT);
                entry.sName         = Encoding.Unicode.GetString(abEntries, iAt + PTB_GPT_ENTRY_NAME_AT, PTB_GPT_NAME_BYTES).TrimEnd('\0');
                entry.bType         = 0;
                entry.bBoot         = false;
                entry.sContent      = "";
                table.lstEntries.Add(entry);
            }

            /* Only now, with everything read, is the table a GPT -- every   */
            /* refusal above left the kind at NONE for the MBR fallback.     */
            table.iKind = PTB_KIND_GPT;

            /* A table read whole is shown even when it could not be written   */
            /* back as it stands -- an array inside the usable area, or a      */
            /* usable range reaching where this disk's backup copy goes (an    */
            /* image cut shorter than its disk).  The layout test that         */
            /* refuses its write (PTB_Validate) is asked now, so the account   */
            /* says so at once rather than only at Write Table.  The last      */
            /* sector is always there: PTB_Read refused a disk shorter than    */
            /* two sectors or with a sector size outside 512 to 4096.          */
            PTB_LastLba(table, out ullLastLba);
            sProblem = PTB_GptLayoutProblem(table, ullLastLba);

            if (sProblem.Length != 0)
            {   /* Readable, not writable as it is: the reason follows. */
                return("GUID partition table: " + table.lstEntries.Count + " partition(s)" + sNote + ". It cannot be written back as it is: " + sProblem);
            }

            /* A GPT, with its notes. */
            return("GUID partition table: " + table.lstEntries.Count + " partition(s)" + sNote + ".");
        }

        /*--------------------------------------------------------------------*/
        /* PTB_Validate:                                                      */
        /*                                                                    */
        /* Whether a table can be written: a disk of at least two sectors,    */
        /* for a GPT its own sectors laid out to fit this disk                */
        /* (PTB_GptLayoutProblem), every entry inside the usable area, none   */
        /* overlapping another, a legal slot for each, and the kind's own     */
        /* limits -- four primaries and 32-bit sectors for an MBR, the name   */
        /* length and a non-empty type and GUID for a GPT entry.              */
        /* PTB_Build runs it before any write.  The layout window also runs   */
        /* it in its Add dialog (DiskLayoutForm.cs), on a trial copy of the   */
        /* table with the new partition in it, before it accepts the add;     */
        /* its other edits (Delete Partition, Expand Filesystem's grow of a   */
        /* partition into the free sectors after it, New Table) are first     */
        /* tested here at the write.                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     table    : the table.                                          */
        /*     sProblem : receives the first problem in words, or "".         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the table may be written.                     */
        /*--------------------------------------------------------------------*/
        internal static bool PTB_Validate(PTB_Table table, out string sProblem)
        {
            PTB_Entry entry;
            PTB_Entry other;
            ulong     ullLastLba;
            ulong     ullLow;
            ulong     ullHigh;
            int       nSlots;
            int       i;
            int       j;

            /* No problem until one is found; the first found is the one said. */
            sProblem = "";

            if (table.uiSector < PTB_MIN_SECTOR || table.uiSector > PTB_MAX_SECTOR)
            {   /* No sector size this code lays out for. */
                sProblem = "The sector size " + table.uiSector + " is not one a table is written for.";
                return(false);
            }

            if (!PTB_LastLba(table, out ullLastLba))
            {   /* A disk shorter than two sectors -- the layout window's     */
                /* Partition File command takes any file, an empty one        */
                /* included -- holds no table of either kind, and writing     */
                /* "no table" over it (an erase) has nothing to erase.  The   */
                /* last sector every check below uses does not exist.         */
                sProblem = "The disk is too small to hold a partition table.";
                return(false);
            }

            if (table.iKind == PTB_KIND_NONE)
            {   /* No table: nothing may be in it. */
                if (table.lstEntries.Count != 0)
                {   /* Entries with no table to hold them. */
                    sProblem = "There is no partition table to hold a partition.";
                    return(false);
                }

                /* An erase has no entries to test, and its places come from   */
                /* the standard layout in PTB_Build, not from the table: it    */
                /* may be written.                                             */
                return(true);
            }

            if (table.iKind == PTB_KIND_MBR)
            {   /* The MBR's bounds: its four slots, and sector 1 onward --      */
                /* sector 0 is the table itself -- to the disk's end or as far   */
                /* as a 32-bit field reaches, whichever is lower.                */
                nSlots  = PTB_MBR_ENTRIES;
                ullLow  = 1;
                ullHigh = ullLastLba;

                if (ullHigh > PTB_MBR_MAX_LBA)
                {   /* A 32-bit entry cannot reach past 2 TiB at 512-byte sectors. */
                    ullHigh = PTB_MBR_MAX_LBA;
                }
            }
            else
            {   /* The GPT's own sectors first (PTB_GptLayoutProblem): the     */
                /* disk long enough for both copies, the usable range past     */
                /* the header and below the backup array, and the primary      */
                /* array between the header and the usable range -- so no      */
                /* piece PTB_BuildGpt writes can land on a partition.  Every   */
                /* partition test below is against that usable range, so a     */
                /* layout that fails refuses the table as a whole.             */
                sProblem = PTB_GptLayoutProblem(table, ullLastLba);

                if (sProblem.Length != 0)
                {   /* The layout does not fit this disk: its reason stands. */
                    return(false);
                }

                /* The bounds, from its header: its slot count and its usable   */
                /* range.                                                       */
                nSlots  = (int)table.nSlots;
                ullLow  = table.ullFirstUsable;
                ullHigh = table.ullLastUsable;
            }

            for (i = 0; i < table.lstEntries.Count; i++)
            {   /* One entry per iteration: its own facts, then against every later one. */

                /* The entry to test.  Two terms the tests below use:          */
                /*                                                             */
                /* Its SLOT (iSlot) is its place in the table's list of        */
                /* entries ON THE DISK, counted from 0: for an MBR, which      */
                /* of the four 16-byte entries in sector 0 it is written       */
                /* to (0 to 3); for a GPT, which entry of the entry array      */
                /* (0 to nSlots - 1, so 0 to 127 on a standard table).  The    */
                /* slot is the partition's NUMBER -- slot 0 is partition 1     */
                /* -- and the messages count from 1 (PTB_SlotText).            */
                /*                                                             */
                /* The TABLE's limits are the ones set above for its kind:     */
                /* nSlots, how many slots it has, and ullLow to ullHigh,       */
                /* the sectors a partition may occupy.                         */
                /*                                                             */
                /* Each test stops at the first problem, so the user is told   */
                /* one thing to fix at a time.                                 */
                entry = table.lstEntries[i];

                if (entry.iSlot < 0 || entry.iSlot >= nSlots)
                {   /* The slot number is outside the table: below 0, or past    */
                    /* its last slot (MBR 3; GPT nSlots - 1).  Nothing in this   */
                    /* file makes one -- both readers and PTB_AddEntry stay      */
                    /* inside -- so this guards an entry its caller changed.     */
                    /* The builders do not check, they index: an MBR slot 4      */
                    /* starts on the 0x55 0xAA signature, which it would         */
                    /* overwrite -- silently on a 4096-byte sector, and on a     */
                    /* 512-byte one until the entry runs off the buffer and      */
                    /* throws; a GPT slot past the array throws at once.         */
                    sProblem = "Partition " + PTB_SlotText(table, entry) + " has no slot in the table.";
                    return(false);
                }

                if (entry.ullFirstLba > entry.ullLastLba)
                {   /* Its first sector is after its last: the range, which is   */
                    /* inclusive, would hold no sectors at all, and the count    */
                    /* an MBR stores (last - first + 1) would wrap round to an   */
                    /* enormous number.                                          */
                    sProblem = "Partition " + PTB_SlotText(table, entry) + " ends before it starts.";
                    return(false);
                }

                if (entry.ullFirstLba < ullLow || entry.ullLastLba > ullHigh)
                {   /* It starts before the first sector the table lets a       */
                    /* partition use, or ends after the last: on an MBR, over   */
                    /* sector 0 (the table itself) or past the disk's end or    */
                    /* the 32-bit reach; on a GPT, over its header and entry    */
                    /* array at the front, or over the backup copies at the     */
                    /* end.  The message gives the allowed sectors, so the      */
                    /* user can see by how much.                                */
                    sProblem = "Partition " + PTB_SlotText(table, entry) + " lies outside the usable area (sectors " + ullLow + " to " + ullHigh + ").";
                    return(false);
                }

                if (table.iKind == PTB_KIND_MBR && entry.bType == 0)
                {   /* An MBR entry of type 0 is how an EMPTY slot is marked:   */
                    /* written, the partition would vanish from the table at    */
                    /* the next read (PTB_ReadMbrEntry skips type 0).           */
                    sProblem = "Partition " + PTB_SlotText(table, entry) + " has no type.";
                    return(false);
                }

                if (table.iKind == PTB_KIND_GPT)
                {   /* The three rules only a GPT entry has: its type GUID,   */
                    /* its own GUID, and the length of its name.              */
                    if (entry.guidType == Guid.Empty)
                    {   /* An all-zero type GUID is how a GPT marks an UNUSED   */
                        /* slot: written, the partition would vanish at the     */
                        /* next read (PTB_ReadGpt skips such slots).            */
                        sProblem = "Partition " + PTB_SlotText(table, entry) + " has no type GUID.";
                        return(false);
                    }

                    if (entry.guidUnique == Guid.Empty)
                    {   /* A partition needs a GUID of its own; only an     */
                        /* empty one is refused.  Two entries sharing one   */
                        /* are NOT looked for -- a table read that way is   */
                        /* written back as read.                            */
                        sProblem = "Partition " + PTB_SlotText(table, entry) + " has no partition GUID.";
                        return(false);
                    }

                    if (entry.sName.Length > PTB_GPT_NAME_CHARS)
                    {   /* Longer than the 36 UTF-16 units the 72-byte name       */
                        /* field holds (Length counts those units, so a           */
                        /* character outside the basic plane counts as two).      */
                        /* PTB_BuildGptEntries copies the name with no limit of   */
                        /* its own, so this test is what stops a long name        */
                        /* running on into the next entry.                        */
                        sProblem = "Partition " + PTB_SlotText(table, entry) + "'s name is longer than " + PTB_GPT_NAME_CHARS + " characters.";
                        return(false);
                    }
                }

                for (j = i + 1; j < table.lstEntries.Count; j++)
                {   /* One later entry per iteration: no shared slot, no overlap. */

                    /* Each pair is met once: j starts after i. */
                    other = table.lstEntries[j];

                    if (other.iSlot == entry.iSlot)
                    {   /* Two entries with one slot number: written, the later   */
                        /* one would overwrite the earlier in that slot and one   */
                        /* partition would be lost from the table.  The message   */
                        /* counts the slot from 1, as everywhere.                 */
                        sProblem = "Two partitions share slot " + (entry.iSlot + 1) + ".";
                        return(false);
                    }

                    if (entry.ullFirstLba <= other.ullLastLba && other.ullFirstLba <= entry.ullLastLba)
                    {   /* The two inclusive ranges share at least one sector --   */
                        /* each starts no later than the other ends.  Written,     */
                        /* both partitions would claim those sectors, and a        */
                        /* filesystem made in one would overwrite the other's.     */
                        sProblem = "Partitions " + PTB_SlotText(table, entry) + " and " + PTB_SlotText(table, other) + " overlap.";
                        return(false);
                    }
                }
            }

            /* Every entry passed every test: the table may be written. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_SlotText:                                                      */
        /*                                                                    */
        /* How a message names an entry: its slot number counted from 1, and  */
        /* for a GPT entry its name in quotes when it has one.                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     table : the table (for its kind).                              */
        /*     entry : the entry.                                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : "3", or "3 \"rootfs\"".                               */
        /*--------------------------------------------------------------------*/
        internal static string PTB_SlotText(PTB_Table table, PTB_Entry entry)
        {
            if (table.iKind == PTB_KIND_GPT && entry.sName.Length != 0)
            {   /* A named GPT partition. */
                return((entry.iSlot + 1) + " \"" + entry.sName + "\"");
            }

            /* The number alone. */
            return((entry.iSlot + 1).ToString());
        }

        /*--------------------------------------------------------------------*/
        /* PTB_Build:                                                         */
        /*                                                                    */
        /* The sectors that put a table on the disk, as pieces for a writer   */
        /* to place, in the order to write them: an erase for "no table", one */
        /* sector for an MBR, five pieces for a GPT.  The table is validated  */
        /* first, and a table that fails produces nothing.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     table     : the table, as PTB_Validate accepts it.             */
        /*     lstPieces : receives the pieces, in the order to write them.   */
        /*     sProblem  : receives why nothing was built, or "".             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true with the pieces made; false with the problem.      */
        /*--------------------------------------------------------------------*/
        internal static bool PTB_Build(PTB_Table table, List<PTB_Piece> lstPieces, out string sProblem)
        {
            ulong ullLastLba;
            ulong ullEntrySectors;

            /* An empty list first, so a refusal hands back no pieces at all   */
            /* and never a stale list from an earlier build.                   */
            lstPieces.Clear();

            if (!PTB_Validate(table, out sProblem))
            {   /* Refused, with the reason: nothing is built. */
                return(false);
            }

            /* The disk's last sector, where a GPT's backup header goes --    */
            /* always there, since PTB_Validate refused a disk without one.   */
            PTB_LastLba(table, out ullLastLba);

            if (table.iKind == PTB_KIND_NONE)
            {   /* Erase: the places a table of either kind would be found. */

                /* The front: sector 0 (an MBR or protective MBR), sector 1    */
                /* (a GPT header) and the standard entry array after it, in    */
                /* one piece -- written first, so a stale table is gone even   */
                /* if the second piece then fails.  The standard layout is     */
                /* used, not the table's, since a "none" table has none:       */
                /* sectors 0 to 33 at 512-byte sectors (0 to 5 at 4096).  On   */
                /* a disk shorter than that the piece runs past the end and    */
                /* the engine refuses it, so nothing is erased -- a disk       */
                /* that small holds no filesystem anyway.                      */
                ullEntrySectors = PTB_EntrySectors(PTB_GPT_STD_SLOTS, PTB_GPT_STD_ENTRY, table.uiSector);
                PTB_AddZeros(lstPieces, 0, (PTB_GPT_STD_ENTRIES_LBA + ullEntrySectors) * table.uiSector);

                if (ullLastLba >= PTB_GPT_STD_ENTRIES_LBA + (2 * ullEntrySectors) + 1)
                {   /* Room for the back too: the last sector (a backup GPT      */
                    /* header) and the entry sectors below it.  The test keeps   */
                    /* the two pieces apart, with a sector to spare; on a disk   */
                    /* too small for both, the front alone is erased.            */
                    PTB_AddZeros(lstPieces, (ullLastLba - ullEntrySectors) * table.uiSector, (ullEntrySectors + 1) * table.uiSector);
                }

                /* The erase's one or two pieces. */
                return(true);
            }

            if (table.iKind == PTB_KIND_MBR)
            {   /* An MBR is wholly in sector 0: one piece.  A GPT the disk   */
                /* holds is not this function's to know of -- the worker's    */
                /* table write reads the disk and adds its invalidation       */
                /* (PTB_AddOldGptErase), so the old headers do not outlive    */
                /* the protective MBR this replaces.                          */
                PTB_AddPiece(lstPieces, 0, PTB_BuildMbrSector(table, false));

                /* The MBR's one piece. */
                return(true);
            }

            /* A GPT: PTB_BuildGpt adds its five pieces in their write order   */
            /* (protective MBR first, backup header last), the backup copies   */
            /* placed from this disk's last sector, ullLastLba.                */
            PTB_BuildGpt(table, ullLastLba, lstPieces);

            /* The GPT's five pieces. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_AddOldGptErase:                                                */
        /*                                                                    */
        /* When the disk holds a GPT, put in front of a table's pieces a      */
        /* sector of zeros at each of that GPT's two HEADERS that the new     */
        /* pieces do not themselves rewrite: the primary in sector 1, and the */
        /* backup where the primary header says it is.  Without it,           */
        /* an MBR written over a GPT leaves the old headers on the disk with  */
        /* good checksums -- a tool that looks for a GPT whatever the MBR     */
        /* says (a repair tool restoring one from its backup, say) finds the  */
        /* OLD partitions over sectors the new ones may use -- and a table    */
        /* moved to a different backup place (a                               */
        /* GPT read from a smaller disk, rewritten on this one) leaves its    */
        /* old backup behind the same way.                                    */
        /*                                                                    */
        /* Only the headers are erased: they are what makes a GPT one (no     */
        /* header, no table), and they lie outside every partition the old    */
        /* GPT described, so no data it held is touched.  The entry arrays    */
        /* are left, unreadable without a header, rather than erased at a     */
        /* standard size and place they may not have.  Called on the worker   */
        /* by every table write whose disk holds a GPT (CommandNativeSteps.cs */
        /* Nst_WriteTable, after a checksum-checked read of the primary       */
        /* header), with what that header says at the moment of the write --  */
        /* never by the window, whose last read can be stale.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     ullDiskBytes    : the disk's or image's length, bytes.         */
        /*     uiSector        : bytes per sector, which every LBA counts.    */
        /*     ullAlternateLba : the sector the disk's primary GPT header     */
        /*                       names for its backup header -- the caller    */
        /*                       calls only when the disk holds a GPT.        */
        /*     lstPieces       : the new table's pieces from PTB_Build; the   */
        /*                       erases go in front of them, so a stop        */
        /*                       part-way leaves a disk with no valid GPT     */
        /*                       rather than an old one behind a new MBR.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the list holds the erases it needed (none, one or two). */
        /*--------------------------------------------------------------------*/
        internal static void PTB_AddOldGptErase(ulong ullDiskBytes, uint uiSector, ulong ullAlternateLba,
            List<PTB_Piece> lstPieces)
        {
            List<PTB_Piece> lstErase;
            ulong           ullSectors;
            ulong           ullSector;
            int             i;

            /* The disk's sectors, to judge the recorded backup place by. */
            ullSectors = ullDiskBytes / uiSector;

            /* The erases, collected apart and then put in front. */
            lstErase = new List<PTB_Piece>();

            /* The primary header, in sector 1 on every GPT.  A GPT written   */
            /* over this one rewrites it, so it is erased only when no new    */
            /* piece covers it (an MBR's one sector does not).                */
            if (!PTB_SectorCovered(lstPieces, 1, uiSector))
            {   /* Not rewritten: its sector of zeros. */
                PTB_AddZeros(lstErase, uiSector, uiSector);
            }

            /* The backup header, where the primary header says it is -- the    */
            /* last sector on a table made for this disk, earlier on one made   */
            /* for a smaller disk.  Only a place on this disk past the header   */
            /* and the sector-0 table is believed (a recorded place off the     */
            /* disk's end holds nothing here to erase), and only one no new     */
            /* piece rewrites (a GPT written now puts its own backup in the     */
            /* last sector).                                                    */
            ullSector = ullAlternateLba;

            if (ullSector > 1 && ullSector < ullSectors &&
                !PTB_SectorCovered(lstPieces, ullSector, uiSector))
            {   /* On this disk and not rewritten: its sector of zeros. */
                PTB_AddZeros(lstErase, ullSector * uiSector, uiSector);
            }

            /* In front of the new table's pieces, in the order made. */
            for (i = lstErase.Count - 1; i >= 0; i--)
            {   /* One erase per iteration, taken from the back so the first   */
                /* collected ends up first.                                    */
                lstPieces.Insert(0, lstErase[i]);
            }
        }

        /*--------------------------------------------------------------------*/
        /* PTB_SectorCovered:                                                 */
        /*                                                                    */
        /* Whether any piece on a list writes one given sector -- so an erase */
        /* is never added over a sector the list already rewrites, which the  */
        /* engine's batch would refuse as overlapping pieces.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstPieces : the pieces.                                        */
        /*     ullSector : the sector, counted from the disk's start.         */
        /*     uiSector  : bytes per sector.                                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when some piece's bytes reach into that sector.    */
        /*--------------------------------------------------------------------*/
        private static bool PTB_SectorCovered(List<PTB_Piece> lstPieces, ulong ullSector, uint uiSector)
        {
            ulong ullStart;
            ulong ullEnd;
            int   i;

            /* The sector's byte range, end exclusive. */
            ullStart = ullSector * uiSector;
            ullEnd   = ullStart + uiSector;

            for (i = 0; i < lstPieces.Count; i++)
            {   /* One piece per iteration: does its range meet the sector's? */
                if (lstPieces[i].ullOffset < ullEnd &&
                    ullStart < lstPieces[i].ullOffset + (ulong)lstPieces[i].abBytes.Length)
                {   /* It does: the sector is written by this piece. */
                    return(true);
                }
            }

            /* No piece reaches it. */
            return(false);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_AddPiece:                                                      */
        /*                                                                    */
        /* One piece onto the list: the bytes given, to go at the offset.     */
        /* The list's order is the order they are written (ENGS_DoWritePieces */
        /* in EngineSession.cs writes them as listed).                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstPieces : the list.                                          */
        /*     ullOffset : where the piece goes, bytes from the disk's start. */
        /*     abBytes   : the bytes, a whole number of sectors.              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the piece is on the list.                               */
        /*--------------------------------------------------------------------*/
        private static void PTB_AddPiece(List<PTB_Piece> lstPieces, ulong ullOffset, byte[] abBytes)
        {
            PTB_Piece piece;

            /* The piece, appended: the buffer is kept, not copied. */
            piece           = new PTB_Piece();
            piece.ullOffset = ullOffset;
            piece.abBytes   = abBytes;
            lstPieces.Add(piece);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_AddZeros:                                                      */
        /*                                                                    */
        /* A piece of zeros onto the list -- how an erase is written.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstPieces : the list.                                          */
        /*     ullOffset : where the zeros go, bytes from the disk's start.   */
        /*     ullBytes  : how many, a whole number of sectors.               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the piece is on the list.                               */
        /*--------------------------------------------------------------------*/
        private static void PTB_AddZeros(List<PTB_Piece> lstPieces, ulong ullOffset, ulong ullBytes)
        {
            /* A new array is all zeros already. */
            PTB_AddPiece(lstPieces, ullOffset, new byte[ullBytes]);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_BuildMbrSector:                                                */
        /*                                                                    */
        /* Sector 0 built from the table: an MBR table's own sector, or the   */
        /* protective MBR in front of a GPT.  Either carries the boot code    */
        /* and disk signature the table holds, so a rewrite keeps a boot      */
        /* loader and the disk's identity; an MBR's sector carries its        */
        /* partitions, a GPT's only the one entry that tells an MBR-only      */
        /* tool the disk is taken.                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     table       : the table, already accepted by PTB_Validate --   */
        /*                   the 32-bit fields written here rely on its       */
        /*                   bounds.                                          */
        /*     bProtective : true for a GPT's protective MBR; false for an    */
        /*                   MBR table's own sector 0.                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte[] : the sector, one disk sector long, new on every call   */
        /*              (the piece list keeps it rather than a copy).         */
        /*--------------------------------------------------------------------*/
        private static byte[] PTB_BuildMbrSector(PTB_Table table, bool bProtective)
        {
            PTB_Entry entry;
            byte[]    abSector;
            ulong     ullLastLba;
            ulong     ullCount;
            int       i;

            /* The sector starts as zeros of the disk's own sector size, so     */
            /* every slot not written below reads as empty, the two bytes       */
            /* after the signature stay zero, and on a larger sector (4096)     */
            /* every byte past 512 is zero.  Into it go the boot code and the   */
            /* disk signature as the table holds them -- as read from the       */
            /* disk, so rewriting the table leaves a boot loader and the        */
            /* disk's identity as they were; zeros and a fresh random           */
            /* signature for a new table (PTB_NewMbr / PTB_NewGpt) -- and the   */
            /* 0x55 0xAA at bytes 510 and 511, without which no reader (this    */
            /* file's PTB_Read included) takes the sector as a table.           */
            abSector = new byte[table.uiSector];
            Array.Copy(table.abBoot, 0, abSector, 0, PTB_MBR_BOOT_BYTES);
            PTB_WriteU32(abSector, PTB_MBR_SIGNATURE_AT, table.uiDiskSignature);
            abSector[PTB_MBR_BOOT_SIG]     = 0x55;
            abSector[PTB_MBR_BOOT_SIG + 1] = 0xAA;

            if (bProtective)
            {   /* A GPT's sector 0: one protective entry in slot 0, and none    */
                /* of the GPT's own partitions, which live in its entry array.   */

                /* The entry claims sector 1 to the disk's last sector, so a    */
                /* tool that knows only MBRs sees the whole disk taken by one   */
                /* partition of type 0xEE and leaves it alone.  Sectors 1 to    */
                /* N are N sectors, so the count is the last sector's number.   */
                /* PTB_Validate has required the disk to hold the GPT's whole   */
                /* layout (PTB_GptLayoutProblem), so the last sector is there   */
                /* and well past sector 1.                                      */
                PTB_LastLba(table, out ullLastLba);
                ullCount = ullLastLba;

                if (ullCount > PTB_MBR_MAX_LBA)
                {   /* More sectors than a 32-bit count holds (past 2 TiB at   */
                    /* 512-byte sectors): the entry stops where its field      */
                    /* does, and the GPT itself describes the rest.            */
                    ullCount = PTB_MBR_MAX_LBA;
                }

                /* Not active (nothing boots from the 0xEE entry), from sector   */
                /* 1 -- sector 0 is this MBR itself, and the GPT header at       */
                /* sector 1 is inside what the entry claims.  Its CHS fields     */
                /* are derived from those sectors like any other entry's.        */
                PTB_WriteMbrEntry(abSector, 0, false, PTB_TYPE_PROTECTIVE, 1, ullCount);

                /* The protective MBR, which PTB_BuildGpt lists first. */
                return(abSector);
            }

            for (i = 0; i < table.lstEntries.Count; i++)
            {   /* One partition per iteration; slots with none stay zero (empty). */

                /* The entry goes in its own slot, not the next free one, so   */
                /* each partition keeps its number across a rewrite.  The      */
                /* count is the inclusive range's length.  PTB_Validate has    */
                /* kept every entry between sector 1 and 0xFFFFFFFF, so the    */
                /* start and the count both fit the 32-bit fields that         */
                /* PTB_WriteMbrEntry casts them into, with nothing lost.       */
                entry = table.lstEntries[i];
                PTB_WriteMbrEntry(abSector, entry.iSlot, entry.bBoot, entry.bType, entry.ullFirstLba, entry.ullLastLba - entry.ullFirstLba + 1);
            }

            /* The MBR sector, which PTB_Build hands on as its one piece. */
            return(abSector);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_WriteMbrEntry:                                                 */
        /*                                                                    */
        /* One 16-byte MBR entry written into its slot in sector 0, from a    */
        /* partition's sectors, type and active flag -- every field of the    */
        /* entry, the CHS addresses included.                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     abSector    : sector 0.                                        */
        /*     iSlot       : the slot, 0 to 3.                                */
        /*     bBoot       : the active flag.                                 */
        /*     bType       : the type byte.                                   */
        /*     ullFirstLba : the first sector; must fit 32 bits.              */
        /*     ullCount    : how many sectors, at least 1; must fit 32 bits.  */
        /*                   Both callers in PTB_BuildMbrSector guarantee     */
        /*                   the fit (PTB_Validate's bound, the protective    */
        /*                   cap); nothing here checks it.                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the entry is in place.                                  */
        /*--------------------------------------------------------------------*/
        private static void PTB_WriteMbrEntry(byte[] abSector, int iSlot, bool bBoot, byte bType, ulong ullFirstLba, ulong ullCount)
        {
            int iAt;

            /* The slot's 16 bytes start at 446 + 16 * slot: status at +0,   */
            /* start CHS at +1, type at +4, end CHS at +5, start sector at   */
            /* +8 and count at +12 -- the order the lines below fill them.   */
            iAt = PTB_MBR_TABLE + (iSlot * PTB_MBR_ENTRY);

            if (bBoot)
            {   /* Active: 0x80, the only status besides 0 that is valid --   */
                /* PTB_MbrLooksReal refuses any other value on a read.        */
                abSector[iAt] = PTB_MBR_ACTIVE;
            }
            else
            {   /* Not active: 0. */
                abSector[iAt] = 0;
            }

            /* The rest of the entry in field order.  The end CHS is the      */
            /* LAST sector's (start + count - 1), not one past it.  The two   */
            /* 32-bit casts drop the high halves, which the callers have      */
            /* already made zero (see Arguments).                             */
            PTB_WriteChs(abSector, iAt + 1, ullFirstLba);
            abSector[iAt + 4] = bType;
            PTB_WriteChs(abSector, iAt + 5, ullFirstLba + ullCount - 1);
            PTB_WriteU32(abSector, iAt + 8, (uint)ullFirstLba);
            PTB_WriteU32(abSector, iAt + 12, (uint)ullCount);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_WriteChs:                                                      */
        /*                                                                    */
        /* The three CHS (cylinder / head / sector) bytes of an MBR entry for */
        /* a sector, in the geometry every LBA-era tool assumes.  This        */
        /* program never reads CHS back (PTB_ReadMbrEntry ignores it); the    */
        /* fields are filled so the entry is consistent for a tool that does  */
        /* look.                                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     abSector : sector 0.                                           */
        /*     iAt      : where the three bytes go.                           */
        /*     ullLba   : the sector.                                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the bytes are in place.                                 */
        /*--------------------------------------------------------------------*/
        private static void PTB_WriteChs(byte[] abSector, int iAt, ulong ullLba)
        {
            ulong ullCylinder;
            uint  uiHead;
            uint  uiSector;

            /* The cylinder first, in the assumed geometry of 255 heads of   */
            /* 63 sectors (PTB_CHS_HEADS, PTB_CHS_SECTORS): 16 065 sectors   */
            /* to a cylinder.  It is kept in 64 bits, since a large disk's   */
            /* cylinder number is far beyond what the field holds.           */
            ullCylinder = ullLba / (PTB_CHS_HEADS * PTB_CHS_SECTORS);

            if (ullCylinder > PTB_CHS_MAX_CYLINDER)
            {   /* Past cylinder 1023 (8.4 GB at 512-byte sectors): no CHS      */
                /* address exists, so the field is pinned at the largest one,   */
                /* which is what marks "beyond CHS" -- 0xFE 0xFF 0xFF, packed   */
                /* as below: head 254, sector 63 with the cylinder's top two    */
                /* bits set, and the cylinder's low eight bits all set, so      */
                /* cylinder 1023.                                               */
                abSector[iAt]     = 0xFE;
                abSector[iAt + 1] = 0xFF;
                abSector[iAt + 2] = 0xFF;
                return;
            }

            /* Within the cylinder: the head is the track number modulo the   */
            /* 255 heads, and the sector counts from 1 -- CHS sectors are     */
            /* 1-based, so sector 0 of the disk is 0 / 0 / 1.                 */
            uiHead   = (uint)((ullLba / PTB_CHS_SECTORS) % PTB_CHS_HEADS);
            uiSector = (uint)((ullLba % PTB_CHS_SECTORS) + 1);

            /* Packed as the entry stores them: the head; the sector in bits   */
            /* 0-5 with the cylinder's bits 8-9 in bits 6-7; the cylinder's    */
            /* bits 0-7.  The head (at most 254) and the cylinder (at most     */
            /* 1023 here) fit their bits exactly.                              */
            abSector[iAt]     = (byte)uiHead;
            abSector[iAt + 1] = (byte)((uiSector & 0x3F) | (((uint)ullCylinder >> 8) << 6));
            abSector[iAt + 2] = (byte)(ullCylinder & 0xFF);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_BuildGpt:                                                      */
        /*                                                                    */
        /* The five pieces of a GPT: the protective MBR, the primary header   */
        /* and entries, and their backup copies at the end of the disk, so    */
        /* either copy can stand in for the other.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     table      : the table.                                        */
        /*     ullLastLba : the disk's last sector.                           */
        /*     lstPieces  : receives the five pieces.                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the pieces are on the list.                             */
        /*--------------------------------------------------------------------*/
        private static void PTB_BuildGpt(PTB_Table table, ulong ullLastLba, List<PTB_Piece> lstPieces)
        {
            byte[] abEntries;
            byte[] abPadded;
            ulong  ullEntrySectors;
            ulong  ullBackupEntries;
            ulong  cbArray;
            uint   uiEntriesCrc;

            /* The table's own array size, in sectors and in bytes -- its     */
            /* nSlots and cbEntry, not the standard ones, so a table read     */
            /* with another geometry is written back with it -- and where     */
            /* the backup array goes: directly below the backup header in     */
            /* the last sector of the disk the table NOW describes            */
            /* (ullLastLba, from ullDiskBytes), not where an old header       */
            /* said -- so on a disk grown since the table was made the        */
            /* backup moves to the new end, and the usable range is left as   */
            /* it was.  PTB_Validate has held the usable range below these    */
            /* backup sectors and the disk long enough for them               */
            /* (PTB_GptLayoutProblem), so the subtraction cannot wrap and     */
            /* the backup copy covers no partition.  The byte count fits an   */
            /* int: at most 4 MiB (PTB_ReadGpt's limits; 16 KiB for a new     */
            /* table).                                                        */
            ullEntrySectors  = PTB_EntrySectors(table.nSlots, table.cbEntry, table.uiSector);
            ullBackupEntries = ullLastLba - ullEntrySectors;
            cbArray          = (ulong)table.nSlots * (ulong)table.cbEntry;

            /* The entry array, checksummed over exactly its bytes, then padded   */
            /* to whole sectors for the write (the padding is not in the CRC).    */
            abEntries    = PTB_BuildGptEntries(table);
            uiEntriesCrc = PTB_Crc32(abEntries, 0, (int)cbArray);
            abPadded     = new byte[ullEntrySectors * table.uiSector];
            Array.Copy(abEntries, 0, abPadded, 0, (int)cbArray);

            /* Sector 0, the primary header, the primary entries, the backup   */
            /* entries, the backup header: in the order written.  The two      */
            /* headers point at each other (sectors 1 and the last) and each   */
            /* at its own entry copy; both carry the same entries' checksum,   */
            /* since the copies are the same bytes.  The same padded buffer    */
            /* serves both entry pieces.  The primary copy goes where the      */
            /* table's header says, which PTB_Validate has held between the    */
            /* header and the first usable sector.                             */
            PTB_AddPiece(lstPieces, 0, PTB_BuildMbrSector(table, true));
            PTB_AddPiece(lstPieces, table.uiSector, PTB_BuildGptHeader(table, 1, ullLastLba, table.ullEntriesLba, uiEntriesCrc));
            PTB_AddPiece(lstPieces, table.ullEntriesLba * table.uiSector, abPadded);
            PTB_AddPiece(lstPieces, ullBackupEntries * table.uiSector, abPadded);
            PTB_AddPiece(lstPieces, ullLastLba * table.uiSector, PTB_BuildGptHeader(table, ullLastLba, 1, ullBackupEntries, uiEntriesCrc));
        }

        /*--------------------------------------------------------------------*/
        /* PTB_BuildGptEntries:                                               */
        /*                                                                    */
        /* The GPT entry array as it is written: each used slot from its      */
        /* entry, each unused slot all zeros.  A table whose entries are      */
        /* larger than 128 bytes is NOT written back exactly: the bytes past  */
        /* 128 of each entry, which PTB_ReadGpt does not keep, come back as   */
        /* zeros -- the one exception to the file header's "unchanged".       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     table : the table.                                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte[] : the array, exactly nSlots * cbEntry bytes.            */
        /*--------------------------------------------------------------------*/
        private static byte[] PTB_BuildGptEntries(PTB_Table table)
        {
            PTB_Entry entry;
            byte[]    abEntries;
            byte[]    abName;
            int       iAt;
            int       i;

            /* The whole array zeroed: every slot without an entry is then   */
            /* the all-zero slot a GPT reads as unused, and every name is    */
            /* already NUL-padded to its field.  The fields written below    */
            /* end at byte 128 of an entry, so in a table with larger        */
            /* entries the rest of each stays zero here.                     */
            abEntries = new byte[(ulong)table.nSlots * (ulong)table.cbEntry];

            for (i = 0; i < table.lstEntries.Count; i++)
            {   /* One used slot per iteration. */

                /* The entry, and where its slot starts.  PTB_Validate has   */
                /* kept iSlot below nSlots, so the slot lies inside the      */
                /* array, and the product fits an int (at most 4 MiB).       */
                entry = table.lstEntries[i];
                iAt   = (int)((uint)entry.iSlot * table.cbEntry);

                /* The fields at their offsets, every value as the entry   */
                /* holds it -- for a table read, exactly as it was read.   */
                PTB_WriteGuid(abEntries, iAt + PTB_GPT_ENTRY_TYPE_AT, entry.guidType);
                PTB_WriteGuid(abEntries, iAt + PTB_GPT_ENTRY_UNIQUE_AT, entry.guidUnique);
                PTB_WriteU64(abEntries, iAt + PTB_GPT_ENTRY_FIRST_AT, entry.ullFirstLba);
                PTB_WriteU64(abEntries, iAt + PTB_GPT_ENTRY_LAST_AT, entry.ullLastLba);
                PTB_WriteU64(abEntries, iAt + PTB_GPT_ENTRY_ATTRS_AT, entry.ullAttributes);

                /* The name as UTF-16LE.  PTB_Validate has held it to 36        */
                /* UTF-16 units, so its bytes fit the 72-byte field; a longer   */
                /* name would run on into the next slot, since the copy has     */
                /* no limit of its own.                                         */
                abName = Encoding.Unicode.GetBytes(entry.sName);
                Array.Copy(abName, 0, abEntries, iAt + PTB_GPT_ENTRY_NAME_AT, abName.Length);
            }

            /* The array, exactly nSlots * cbEntry bytes, for PTB_BuildGpt   */
            /* to checksum and pad.                                          */
            return(abEntries);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_BuildGptHeader:                                                */
        /*                                                                    */
        /* One GPT header sector, primary or backup: the table's facts, where */
        /* this copy and the other copy are, where this copy's entries are,   */
        /* and the checksums that let a reader trust both the header and the  */
        /* entries.  Called twice by PTB_BuildGpt, once for each copy.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     table        : the table.                                      */
        /*     ullCurrent   : the sector this header is written to.           */
        /*     ullAlternate : the sector the other header is in.              */
        /*     ullEntries   : the sector this header's entry copy starts at.  */
        /*     uiEntriesCrc : the entry array's checksum.                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte[] : the sector.                                           */
        /*--------------------------------------------------------------------*/
        private static byte[] PTB_BuildGptHeader(PTB_Table table, ulong ullCurrent, ulong ullAlternate, ulong ullEntries, uint uiEntriesCrc)
        {
            byte[] abHeader;

            /* A zeroed sector -- so the bytes past 92 are zero, and so is     */
            /* the checksum field, which the CRC below must find zero --       */
            /* then every field at its offset (the layout table above).  The   */
            /* header size is always written as 92: a header read with a       */
            /* larger size is written back in the standard size, which every   */
            /* field it had still fits.  The usable range, disk GUID, slot     */
            /* count and entry size come from the table unchanged.             */
            abHeader = new byte[table.uiSector];
            Array.Copy(Encoding.ASCII.GetBytes("EFI PART"), 0, abHeader, 0, PTB_GPT_SIGNATURE_BYTES);
            PTB_WriteU32(abHeader, PTB_GPT_REVISION_AT, PTB_GPT_REVISION);
            PTB_WriteU32(abHeader, PTB_GPT_HEADER_SIZE_AT, PTB_GPT_HEADER_BYTES);
            PTB_WriteU64(abHeader, PTB_GPT_CURRENT_AT, ullCurrent);
            PTB_WriteU64(abHeader, PTB_GPT_ALTERNATE_AT, ullAlternate);
            PTB_WriteU64(abHeader, PTB_GPT_FIRST_USABLE_AT, table.ullFirstUsable);
            PTB_WriteU64(abHeader, PTB_GPT_LAST_USABLE_AT, table.ullLastUsable);
            PTB_WriteGuid(abHeader, PTB_GPT_DISK_GUID_AT, table.guidDisk);
            PTB_WriteU64(abHeader, PTB_GPT_ENTRIES_LBA_AT, ullEntries);
            PTB_WriteU32(abHeader, PTB_GPT_SLOTS_AT, table.nSlots);
            PTB_WriteU32(abHeader, PTB_GPT_ENTRY_SIZE_AT, table.cbEntry);
            PTB_WriteU32(abHeader, PTB_GPT_ENTRIES_CRC_AT, uiEntriesCrc);

            /* The header's own checksum goes in last, over the 92 bytes the   */
            /* size field names with the checksum field still zero -- the      */
            /* way PTB_ReadGpt checks it.  Any later change to the header      */
            /* would need it computed again.                                   */
            PTB_WriteU32(abHeader, PTB_GPT_HEADER_CRC_AT, PTB_Crc32(abHeader, 0, PTB_GPT_HEADER_BYTES));

            /* The header sector, one disk sector long, for PTB_BuildGpt. */
            return(abHeader);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_Write:                                                         */
        /*                                                                    */
        /* Build a table's pieces and write each through the writer given, in */
        /* order, stopping at the first failure.  NOTHING IN THE PROGRAM      */
        /* CALLS THIS: the program builds the pieces (DiskLayoutForm.cs) and  */
        /* writes them itself, all of them in one engine call under one lock  */
        /* (EngineSession.cs's ENGS_DoWritePieces, over the engine's batch    */
        /* write).                                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     table    : the table.                                          */
        /*     pfnWrite : how to write the image or device.                   */
        /*     sError   : receives why it failed, or "".                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when every piece was written.                      */
        /*--------------------------------------------------------------------*/
        internal static bool PTB_Write(PTB_Table table, PTB_Writer pfnWrite, out string sError)
        {
            List<PTB_Piece> lstPieces;
            int             i;

            /* A list of its own for the pieces; PTB_Build empties and fills it. */
            lstPieces = new List<PTB_Piece>();

            if (!PTB_Build(table, lstPieces, out sError))
            {   /* The table is not one that can be written: PTB_Validate's   */
                /* reason is in sError, and nothing has been written.         */
                return(false);
            }

            for (i = 0; i < lstPieces.Count; i++)
            {   /* One piece per iteration, in PTB_Build's order. */
                if (!pfnWrite(lstPieces[i].ullOffset, lstPieces[i].abBytes, out sError))
                {   /* The writer failed and said why.  The pieces before      */
                    /* this one are on the disk and are not undone, so what    */
                    /* a later read finds depends on how far the write got,    */
                    /* in PTB_Build's order.  An erase writes its front        */
                    /* first, so a stale table at the start is gone even if    */
                    /* the back fails.  A GPT writes sector 0 first: stopped   */
                    /* before its header, the disk shows the new protective    */
                    /* MBR over whatever sector 1 held -- nothing, which       */
                    /* PTB_ReadGpt refuses (the MBR is shown), or an older     */
                    /* GPT header, which it reads; stopped after the header    */
                    /* but before the entries, the new header is read over     */
                    /* whatever entries were there before, with "the           */
                    /* entries' checksum is wrong" in the account when they    */
                    /* differ from the new ones.                               */
                    return(false);
                }
            }

            /* No failure: PTB_Build's "" may have been replaced by the last   */
            /* writer call, so the empty reason is set again for the caller.   */
            sError = "";

            /* Every piece is on the disk. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_UsableRange:                                                   */
        /*                                                                    */
        /* The sectors a partition may occupy in a table of the given kind:   */
        /* the GPT's usable range from its header; an MBR's from sector 1 to  */
        /* the end of the disk or as far as 32 bits reach; nothing with no    */
        /* table, or on a disk too small for the table's kind -- so the free  */
        /* gaps (PTB_FreeGaps) offer no room there either.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     table : the table.                                             */
        /*     range : receives the range; 0 to 0 when there is none.         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : false when no partition can go anywhere: no table, a    */
        /*            disk too small for the kind, or an empty GPT range.     */
        /*--------------------------------------------------------------------*/
        internal static bool PTB_UsableRange(PTB_Table table, PTB_Range range)
        {
            ulong ullLastLba;

            if (table.iKind == PTB_KIND_NONE || !PTB_LastLba(table, out ullLastLba))
            {   /* No table, or a disk too small for one (PTB_LastLba):     */
                /* nowhere a partition can go, and no free space offered.   */
                range.ullFirstLba = 0;
                range.ullLastLba  = 0;
                return(false);
            }

            if (table.iKind == PTB_KIND_GPT)
            {   /* From the header, as it stands -- the same bounds            */
                /* PTB_Validate holds the entries to.  A GPT the disk is too   */
                /* small for has no usable range at all (PTB_NewGpt leaves     */
                /* its end below its start), so it is refused here as well.    */
                /* A range that fails PTB_GptLayoutProblem's other tests (an   */
                /* image cut short, its range reaching the backup copy) is     */
                /* still given as the header has it: PTB_Validate refuses      */
                /* that table as a whole before anything is written.           */
                if (ullLastLba < PTB_GptMinLastLba(table) || table.ullLastUsable < table.ullFirstUsable)
                {   /* Too small for the layout, or an empty range: no room. */
                    range.ullFirstLba = 0;
                    range.ullLastLba  = 0;
                    return(false);
                }

                range.ullFirstLba = table.ullFirstUsable;
                range.ullLastLba  = table.ullLastUsable;
                return(true);
            }

            if (table.iKind == PTB_KIND_MBR)
            {   /* After sector 0, within 32 bits -- PTB_Validate's MBR bounds. */
                range.ullFirstLba = 1;
                range.ullLastLba  = ullLastLba;

                if (range.ullLastLba > PTB_MBR_MAX_LBA)
                {   /* As far as an entry reaches. */
                    range.ullLastLba = PTB_MBR_MAX_LBA;
                }

                return(true);
            }

            /* A guard: "no table" returned at the top, and the two kinds     */
            /* that hold partitions above, so only a kind value outside the   */
            /* three reaches here -- and it is given no room.                 */
            range.ullFirstLba = 0;
            range.ullLastLba  = 0;
            return(false);
        }

        /*----------------------------------------------------------------------*/
        /* PTB_FreeGaps:                                                        */
        /*                                                                      */
        /* The runs of unused sectors inside the usable range, in disk order:   */
        /* where a new partition can go.  The layout window (DiskLayoutForm.cs) */
        /* draws them, and its Add Partition and Expand Filesystem commands     */
        /* place a partition, or grow one, into them.                           */
        /*                                                                      */
        /* Arguments:                                                           */
        /*     table   : the table.                                             */
        /*     lstGaps : receives the gaps; empty when there is no room or no   */
        /*               table.                                                 */
        /*                                                                      */
        /* Returns:                                                             */
        /*     void : the list is filled.                                       */
        /*----------------------------------------------------------------------*/
        internal static void PTB_FreeGaps(PTB_Table table, List<PTB_Range> lstGaps)
        {
            List<PTB_Entry> lstSorted;
            PTB_Range       range;
            PTB_Range       gap;
            ulong           ullNext;
            int             i;

            /* An empty list first, so "no table" hands back no gaps and   */
            /* never an earlier call's; then the range the gaps lie in.    */
            lstGaps.Clear();
            range = new PTB_Range();

            if (!PTB_UsableRange(table, range))
            {   /* No table, a disk too small for the table's kind, or an       */
                /* empty GPT range: nothing can be placed, so no gaps at all.   */
                return;
            }

            /* The partitions in disk order, and the walk's cursor: the     */
            /* first sector not yet known to be taken, starting where the   */
            /* usable range does.                                           */
            lstSorted = PTB_SortedByStart(table);
            ullNext   = range.ullFirstLba;

            for (i = 0; i < lstSorted.Count; i++)
            {   /* One partition per iteration: the gap before it, if any. */
                if (lstSorted[i].ullFirstLba > ullNext)
                {   /* Free sectors before this partition.  The gap is not    */
                    /* clipped to the usable range: a partition that starts   */
                    /* past the range's end (a table read that way) makes a   */
                    /* gap that runs past it too.  PTB_Validate refuses a     */
                    /* partition put in the part outside, so nothing wrong    */
                    /* is written.                                            */
                    gap             = new PTB_Range();
                    gap.ullFirstLba = ullNext;
                    gap.ullLastLba  = lstSorted[i].ullFirstLba - 1;
                    lstGaps.Add(gap);
                }

                if (lstSorted[i].ullLastLba + 1 > ullNext)
                {   /* The next free sector is after this partition.  The      */
                    /* test moves the cursor only forward, so a partition      */
                    /* inside or overlapping an earlier one (a damaged table   */
                    /* read as it is) cannot make a gap appear inside it.      */
                    ullNext = lstSorted[i].ullLastLba + 1;
                }
            }

            if (ullNext <= range.ullLastLba)
            {   /* Free sectors after the last partition. */
                gap             = new PTB_Range();
                gap.ullFirstLba = ullNext;
                gap.ullLastLba  = range.ullLastLba;
                lstGaps.Add(gap);
            }
        }

        /*--------------------------------------------------------------------*/
        /* PTB_SortedByStart:                                                 */
        /*                                                                    */
        /* The table's entries in disk order (by first sector), as a new      */
        /* list; the table's own list stays in slot order.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     table : the table.                                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     List<PTB_Entry> : the entries, sorted.                         */
        /*--------------------------------------------------------------------*/
        internal static List<PTB_Entry> PTB_SortedByStart(PTB_Table table)
        {
            List<PTB_Entry> lstSorted;

            /* A copy, so the table's own list keeps the slot order it is   */
            /* kept in (PTB_AddEntry inserts by slot), sorted by first      */
            /* sector.  Two entries starting at the same sector keep no     */
            /* particular order between them -- the sort is not stable --   */
            /* which PTB_FreeGaps does not mind.                            */
            lstSorted = new List<PTB_Entry>(table.lstEntries);
            lstSorted.Sort(PTB_CompareByFirstLba);

            /* The sorted copy; the entries themselves are shared, not copied. */
            return(lstSorted);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_CompareByFirstLba:                                             */
        /*                                                                    */
        /* PTB_SortedByStart's sort order: two entries by their first sector, */
        /* lower first -- disk order.  Nothing else is compared, so two       */
        /* entries starting at the same sector compare equal.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     entryA : one entry.                                            */
        /*     entryB : the other.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : below zero when entryA starts first, zero when both      */
        /*           start at the same sector, above zero when entryB does.   */
        /*--------------------------------------------------------------------*/
        private static int PTB_CompareByFirstLba(PTB_Entry entryA, PTB_Entry entryB)
        {
            /* The two first sectors compared, as unsigned 64-bit numbers. */
            return(entryA.ullFirstLba.CompareTo(entryB.ullFirstLba));
        }

        /*--------------------------------------------------------------------*/
        /* PTB_AlignUp:                                                       */
        /*                                                                    */
        /* The first sector at or after the given one that sits on a          */
        /* PTB_ALIGN_BYTES boundary -- where a new partition is started so    */
        /* its filesystem's blocks line up with the media's erase blocks.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     ullLba   : the sector.                                         */
        /*     uiSector : bytes per sector.                                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ulong : the aligned sector.                                    */
        /*--------------------------------------------------------------------*/
        internal static ulong PTB_AlignUp(ulong ullLba, uint uiSector)
        {
            ulong ullAlignSectors;

            /* The boundary in sectors: 8192 at 512 bytes, 1024 at 4096 --    */
            /* exact, since 4 MiB is a multiple of every sector size taken.   */
            ullAlignSectors = PTB_ALIGN_BYTES / uiSector;

            /* Round up to the next multiple; a sector already on one stays. */
            return(((ullLba + ullAlignSectors - 1) / ullAlignSectors) * ullAlignSectors);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_FreeSlot:                                                      */
        /*                                                                    */
        /* The lowest slot no entry uses, for a new partition; -1 when the    */
        /* table is full.                                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     table : the table.                                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : the slot, or -1.                                         */
        /*--------------------------------------------------------------------*/
        internal static int PTB_FreeSlot(PTB_Table table)
        {
            int nSlots;
            int iSlot;
            int i;
            bool bUsed;

            if (table.iKind == PTB_KIND_MBR)
            {   /* Four primaries. */
                nSlots = PTB_MBR_ENTRIES;
            }
            else if (table.iKind == PTB_KIND_GPT)
            {   /* As many as the header says. */
                nSlots = (int)table.nSlots;
            }
            else
            {   /* No table: no slot. */
                return(-1);
            }

            for (iSlot = 0; iSlot < nSlots; iSlot++)
            {   /* One slot per iteration: is it taken?  The lowest free   */
                /* slot is the one wanted, so the walk starts at 0.        */
                bUsed = false;

                for (i = 0; i < table.lstEntries.Count; i++)
                {   /* One entry per iteration. */
                    if (table.lstEntries[i].iSlot == iSlot)
                    {   /* Taken.  The search goes on past the match -- a   */
                        /* few more tests, which change nothing.            */
                        bUsed = true;
                    }
                }

                if (!bUsed)
                {   /* The first free one. */
                    return(iSlot);
                }
            }

            /* Every slot is used. */
            return(-1);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_AddEntry:                                                      */
        /*                                                                    */
        /* A new partition added to the table in the lowest free slot, with   */
        /* the sectors, type and name given.  Nothing is validated here, so   */
        /* a caller may add and then test: the layout window's Add dialog     */
        /* (DiskLayoutForm.cs) adds to a trial copy and runs PTB_Validate on  */
        /* it, and PTB_Build validates again before any write.                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     table       : the table.                                       */
        /*     ullFirstLba : the first sector.                                */
        /*     ullLastLba  : the last sector, inclusive.                      */
        /*     type        : the named type to give it.                       */
        /*     sName       : a GPT partition's name ("" for none; ignored on  */
        /*                   an MBR).                                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     PTB_Entry : the entry added, or null when no slot was free.    */
        /*--------------------------------------------------------------------*/
        internal static PTB_Entry PTB_AddEntry(PTB_Table table, ulong ullFirstLba, ulong ullLastLba, PTB_TypeName type, string sName)
        {
            PTB_Entry entry;
            int       iSlot;
            int       i;

            /* The lowest free slot, so a new partition takes the smallest   */
            /* number available.                                             */
            iSlot = PTB_FreeSlot(table);

            if (iSlot < 0)
            {   /* Full, or no table: nothing is added, and the caller is   */
                /* told by the null.                                        */
                return(null);
            }

            /* The fields both kinds share: the slot, the sectors as given,   */
            /* no attributes, and no contents (it holds nothing yet).         */
            entry               = new PTB_Entry();
            entry.iSlot         = iSlot;
            entry.ullFirstLba   = ullFirstLba;
            entry.ullLastLba    = ullLastLba;
            entry.ullAttributes = 0;
            entry.sContent      = "";

            if (table.iKind == PTB_KIND_GPT)
            {   /* The GPT fields: the type's GUID, a GUID of its own made   */
                /* fresh (random, so in practice unlike any other entry's    */
                /* -- nothing checks), the name, and the MBR fields empty.   */
                /* A type with no GPT form (an MBR-only row) has the empty   */
                /* GUID, which PTB_Validate refuses as "no type GUID".       */
                entry.guidType   = type.guidGpt;
                entry.guidUnique = Guid.NewGuid();
                entry.sName      = sName;
                entry.bType      = 0;
                entry.bBoot      = false;
            }
            else
            {   /* The MBR fields: the type byte, not active, and the GPT   */
                /* fields empty; a name is not kept, an MBR having none.    */
                /* A type with no MBR form (a GPT-only row) has byte 0,     */
                /* which PTB_Validate refuses as "no type".  The active     */
                /* flag is the caller's to set afterwards (the layout       */
                /* window sets it from its Add dialog's checkbox,           */
                /* DiskLayoutForm.cs).                                      */
                entry.guidType   = Guid.Empty;
                entry.guidUnique = Guid.Empty;
                entry.sName      = "";
                entry.bType      = type.bMbr;
                entry.bBoot      = false;
            }

            /* Into the list in slot order. */
            for (i = 0; i < table.lstEntries.Count; i++)
            {   /* One existing entry per iteration: the first with a higher slot. */
                if (table.lstEntries[i].iSlot > iSlot)
                {   /* Goes before this one. */
                    break;
                }
            }

            /* In at that place -- or at the end when no slot is higher,    */
            /* the loop having run out with i at the count -- so the list   */
            /* stays in slot order.                                         */
            table.lstEntries.Insert(i, entry);

            /* The new entry, already in the table. */
            return(entry);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_MbrTypeText:                                                   */
        /*                                                                    */
        /* The words for an MBR partition type byte.  Only the types likely   */
        /* to be met beside a Linux filesystem are named; any other is shown  */
        /* as its number.                                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bType : the type byte.                                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the type in words.                                    */
        /*--------------------------------------------------------------------*/
        internal static string PTB_MbrTypeText(byte bType)
        {
            switch (bType)
            {   /* One known type per case. */
                case 0x83: return("Linux");
                case 0x82: return("Linux swap");
                case 0x8E: return("Linux LVM");
                case 0x85: return("Linux extended");
                case 0x05: return("Extended");
                case 0x0F: return("Extended (LBA)");
                case 0x0B: return("FAT32");
                case 0x0C: return("FAT32 (LBA)");
                case 0x0E: return("FAT16 (LBA)");
                case 0x06: return("FAT16");
                case 0x07: return("NTFS / exFAT");
                case 0xEF: return("EFI system");
                case 0xEE: return("GPT protective");
                default:   return("type 0x" + bType.ToString("X2"));
            }
        }

        /*--------------------------------------------------------------------*/
        /* PTB_GptTypeText:                                                   */
        /*                                                                    */
        /* The words for a GPT partition type GUID: a named type's words, or  */
        /* for any other the GUID itself, so a vendor's type can be looked up */
        /* as shown.  Used by the layout window and the Open dialogs          */
        /* (DiskLayoutForm.cs, PartitionForm.cs).                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     guidType : the type GUID.                                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the type in words.                                    */
        /*--------------------------------------------------------------------*/
        internal static string PTB_GptTypeText(Guid guidType)
        {
            int i;

            for (i = 0; i < PTB_aTypes.Length; i++)
            {   /* One named type per iteration.  An MBR-only row's empty   */
                /* GUID never matches a used entry, whose type is never     */
                /* empty (PTB_ReadGpt skips such slots).                    */
                if (PTB_aTypes[i].guidGpt == guidType)
                {   /* A named one -- the first row with this GUID, and each   */
                    /* GUID is given to one row only, so the name is never     */
                    /* another kind's.  A type says what a partition was       */
                    /* meant for; what it holds is the probe's contents.       */
                    return(PTB_aTypes[i].sName);
                }
            }

            /* Not one of the named types: the GUID in its usual written     */
            /* form, hyphenated and upper-case as partition tools commonly   */
            /* print it, so it can be searched for as shown.                 */
            return(guidType.ToString("D").ToUpperInvariant());
        }

        /*--------------------------------------------------------------------*/
        /* PTB_IsExtendedType:                                                */
        /*                                                                    */
        /* Whether an MBR type byte is an EXTENDED container -- DOS's 0x05,   */
        /* Windows' 0x0F (LBA) or Linux's 0x85 -- whose logical partitions    */
        /* live in a chain inside it that this model does not read.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bType : the entry's type byte.                                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true for the three container types.                     */
        /*--------------------------------------------------------------------*/
        internal static bool PTB_IsExtendedType(byte bType)
        {
            /* The three container types in use. */
            return(bType == 0x05 || bType == 0x0F || bType == 0x85);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_CountExtended:                                                 */
        /*                                                                    */
        /* How many of an MBR's primaries are extended containers -- for the  */
        /* account line, which must say that their logical partitions are     */
        /* not shown.                                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     table : the table.                                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : the count; 0 for a GPT or a table without one.           */
        /*--------------------------------------------------------------------*/
        internal static int PTB_CountExtended(PTB_Table table)
        {
            int nExtended;
            int i;

            /* None counted yet. */
            nExtended = 0;

            /* The kind test is in the loop's condition, so a GPT ends the   */
            /* loop before its first pass: a GPT's entries carry type byte   */
            /* 0, never an extended one, but the count is MBR-only anyway.   */
            for (i = 0; table.iKind == PTB_KIND_MBR && i < table.lstEntries.Count; i++)
            {   /* One primary per iteration. */
                if (PTB_IsExtendedType(table.lstEntries[i].bType))
                {   /* A container. */
                    nExtended++;
                }
            }

            /* The containers found. */
            return(nExtended);
        }

        /*--------------------------------------------------------------------*/
        /* PTB_Probe:                                                         */
        /*                                                                    */
        /* Set each entry's sContent to what its bytes show it HOLDS.  The    */
        /* table's type says only what the partition was meant for, and a     */
        /* vendor GPT uses type GUIDs that name nothing, so the bytes are the */
        /* only honest answer.  An entry whose contents cannot be told is     */
        /* left "".  It runs on the reader it is given, where the table was   */
        /* read (the layout read in CommandNativeSteps.cs, on the command     */
        /* worker), so nothing here touches a window.                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pfnRead : how to read the image or device.                     */
        /*     table   : the table; each entry gets its sContent.             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : every entry's sContent is set ("" when unknown).        */
        /*--------------------------------------------------------------------*/
        internal static void PTB_Probe(PRT_Reader pfnRead, PTB_Table table)
        {
            PTB_Entry entry;
            byte[]    abStart;
            ulong     ullBytes;
            int       i;

            /* One page buffer, reused for every partition: each read fills   */
            /* it whole or fails, and a failed read's bytes are not looked    */
            /* at, so nothing of one partition is taken for the next's.       */
            abStart = new byte[PTB_PROBE_BYTES];

            for (i = 0; i < table.lstEntries.Count; i++)
            {   /* One partition per iteration. */

                /* The entry, its contents blank until a test below says      */
                /* otherwise, and its length in bytes.  On a damaged entry    */
                /* (last before first) the length wraps; the only effect is   */
                /* a probe read at its start, which is harmless.              */
                entry          = table.lstEntries[i];
                entry.sContent = "";
                ullBytes       = (entry.ullLastLba - entry.ullFirstLba + 1) * table.uiSector;

                if (table.iKind == PTB_KIND_MBR && PTB_IsExtendedType(entry.bType))
                {   /* An extended container: its first page is a link of the   */
                    /* logical chain, not a filesystem, and the chain is not    */
                    /* read here -- say exactly that instead of "nothing".      */
                    entry.sContent = "EXTENDED: logical partitions not shown";
                    continue;
                }

                /* One read of a page (PTB_PROBE_BYTES) at the partition's   */
                /* start: every signature PTB_ContentText looks for lies     */
                /* inside it, so one read per partition is all it takes.     */
                if (ullBytes < (ulong)PTB_PROBE_BYTES || !pfnRead(entry.ullFirstLba * table.uiSector, abStart))
                {   /* Shorter than one page -- the read would run into the    */
                    /* next partition, and nothing looked for fits anyway --   */
                    /* or not readable (outside a cut-short image): left       */
                    /* blank, "unknown".                                       */
                    continue;
                }

                /* The page's verdict, "" when it matches nothing known. */
                entry.sContent = PTB_ContentText(abStart);
            }
        }

        /*--------------------------------------------------------------------*/
        /* PTB_ExtBytes:                                                      */
        /*                                                                    */
        /* The length of the ext filesystem whose superblock is in a first    */
        /* page, from the superblock itself.  This is the FILESYSTEM'S size,  */
        /* which may be less than the disk or partition it sits in -- a       */
        /* filesystem made smaller than its card, before it is grown to fill  */
        /* it.  Its one caller is the layout read (Nst_ReadLayout,            */
        /* CommandNativeSteps.cs), for a disk with no table and a filesystem  */
        /* over the whole of it; the layout                                   */
        /* window (DiskLayoutForm.cs) shows that size on its whole-disk row.  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     abStart : the first PTB_PROBE_BYTES bytes.                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ulong : the filesystem's length in bytes, or 0 when the page   */
        /*             holds no ext superblock (or a block size past 64 KiB). */
        /*--------------------------------------------------------------------*/
        internal static ulong PTB_ExtBytes(byte[] abStart)
        {
            ulong ullBlocks;
            uint  uiLog;

            if (abStart.Length < PTB_EXT_SB + 1024 ||
                abStart[PTB_EXT_SB + PTB_EXT_MAGIC] != 0x53 || abStart[PTB_EXT_SB + PTB_EXT_MAGIC + 1] != 0xEF)
            {   /* The buffer does not reach past the superblock (1024 to   */
                /* 2047), or the magic 0xEF53 is not at its place: no ext   */
                /* filesystem starts here, which the 0 tells the caller.    */
                return(0);
            }

            /* The block count's low 32 bits (s_blocks_count, offset 4 --   */
            /* e2fsprogs ext2_fs.h, as are the offsets below).              */
            ullBlocks = PTB_ReadU32(abStart, PTB_EXT_SB + 4);

            if ((PTB_ReadU32(abStart, PTB_EXT_SB + PTB_EXT_INCOMPAT) & 0x80) != 0)
            {   /* The 64bit feature (incompat 0x0080): the count's high    */
                /* half is in s_blocks_count_hi at 0x150.  Without the      */
                /* feature that field is not the count's and is not read.   */
                ullBlocks = ullBlocks | ((ulong)PTB_ReadU32(abStart, PTB_EXT_SB + 0x150) << 32);
            }

            /* The block size as a shift: 1024 << s_log_block_size (24). */
            uiLog = PTB_ReadU32(abStart, PTB_EXT_SB + 24);

            if (uiLog > 6)
            {   /* A block size past 64 KiB (a shift over 6), which this     */
                /* code takes as a damaged field: nothing it says is used,   */
                /* since a large shift would make the size meaningless.      */
                return(0);
            }

            /* Blocks times the block size.  A damaged count could make this   */
            /* larger than the partition; the result is the superblock's       */
            /* claim, not a measured size.                                     */
            return(ullBlocks * (1024UL << (int)uiLog));
        }

        /*--------------------------------------------------------------------*/
        /* PTB_ContentText:                                                   */
        /*                                                                    */
        /* The words for what a partition's first page says it holds -- the   */
        /* filesystem and its label, read from the bytes, since a partition's */
        /* type says only what it was meant for.  Every place that names a    */
        /* partition's or a disk's contents calls this one function, so the   */
        /* words agree everywhere: in this file PTB_Read and PTB_Probe;       */
        /* outside it CommandNativeSteps.cs (the layout read),                */
        /* EngineSession.cs (the open that says what a disk holds when the    */
        /* engine opens neither an ext nor a FAT filesystem there),           */
        /* CommandImageOpening.cs (an image file's open) and                  */
        /* PartitionForm.cs (the Open dialogs).  The first test that matches  */
        /* decides, so the tests run in an order that matters; each step says */
        /* why it sits where it does.                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     abStart : the partition's first PTB_PROBE_BYTES bytes.         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the contents in words, or "" when nothing matched.    */
        /*--------------------------------------------------------------------*/
        internal static string PTB_ContentText(byte[] abStart)
        {
            string sOem;
            string sSquash;
            string sSwap;
            uint   uiCompat;
            uint   uiIncompat;
            uint   uiRoCompat;

            /* STEP 1: the ext family, by the superblock magic -- FIRST,       */
            /* because an ext filesystem leaves its first 1024 bytes unused,   */
            /* and a boot sector left there from an earlier format (with its   */
            /* 0x55 0xAA) would otherwise make it read as FAT in STEP 3.       */
            if (abStart[PTB_EXT_SB + PTB_EXT_MAGIC] == 0x53 && abStart[PTB_EXT_SB + PTB_EXT_MAGIC + 1] == 0xEF)
            {   /* An ext2, ext3 or ext4 superblock: all three share the one   */
                /* magic, so the feature words say which -- the way blkid      */
                /* tells them apart.                                           */
                uiCompat   = PTB_ReadU32(abStart, PTB_EXT_SB + PTB_EXT_COMPAT);
                uiIncompat = PTB_ReadU32(abStart, PTB_EXT_SB + PTB_EXT_INCOMPAT);
                uiRoCompat = PTB_ReadU32(abStart, PTB_EXT_SB + PTB_EXT_RO_COMPAT);

                if ((uiIncompat & PTB_EXT4_INCOMPAT) != 0 || (uiRoCompat & PTB_EXT4_RO_COMPAT) != 0)
                {   /* An incompat or ro_compat bit only ext4 sets (the two   */
                    /* masks): ext4, whatever else is set.  The compat bits   */
                    /* only ext4 uses (fast_commit, orphan_file and others)   */
                    /* are not consulted; an ext4 normally carries one of     */
                    /* these as well (extents), so that misses little.        */
                    return("ext4" + PTB_LabelText(abStart, PTB_EXT_SB + PTB_EXT_LABEL, PTB_EXT_LABEL_BYTES, false));
                }

                if ((uiCompat & PTB_EXT_COMPAT_JOURNAL) != 0)
                {   /* has_journal (compat 0x0004) and no ext4-only bit: ext3. */
                    return("ext3" + PTB_LabelText(abStart, PTB_EXT_SB + PTB_EXT_LABEL, PTB_EXT_LABEL_BYTES, false));
                }

                /* No journal and no ext4-only bit: ext2, the original. */
                return("ext2" + PTB_LabelText(abStart, PTB_EXT_SB + PTB_EXT_LABEL, PTB_EXT_LABEL_BYTES, false));
            }

            /* STEP 2: squashfs, by its magic at the very start.  Its place    */
            /* in the order costs nothing: a page that begins with the magic   */
            /* cannot also begin with a boot sector.                           */
            sSquash = Encoding.ASCII.GetString(abStart, PTB_SQUASHFS_MAGIC, 4);

            if (sSquash == "hsqs" || sSquash == "sqsh")
            {   /* A squashfs image (little- or big-endian). */
                return("squashfs");
            }

            /* STEP 3: a boot sector -- FAT by its type text, exFAT and NTFS   */
            /* by the OEM name -- behind the 0x55 0xAA signature every one     */
            /* of them carries.                                                */
            if (abStart[PTB_MBR_BOOT_SIG] == 0x55 && abStart[PTB_MBR_BOOT_SIG + 1] == 0xAA)
            {   /* A boot sector of some kind. */
                sOem = Encoding.ASCII.GetString(abStart, PTB_FAT_OEM, PTB_SIGNATURE_BYTES);

                if (sOem == "EXFAT   ")
                {   /* exFAT names itself in the OEM field. */
                    return("exFAT");
                }

                if (sOem == "NTFS    ")
                {   /* So does NTFS. */
                    return("NTFS");
                }

                if (Encoding.ASCII.GetString(abStart, PTB_FAT_TYPE32, PTB_SIGNATURE_BYTES) == "FAT32   ")
                {   /* FAT32: its type text and label sit further in. */
                    return("FAT32" + PTB_LabelText(abStart, PTB_FAT_LABEL32, PTB_FAT_LABEL_BYTES, true));
                }

                if (Encoding.ASCII.GetString(abStart, PTB_FAT_TYPE16, PTB_SIGNATURE_BYTES).StartsWith("FAT"))
                {   /* FAT12 or FAT16: the 8-byte text there reads "FAT12",   */
                    /* "FAT16", or just "FAT" as some formatters write it;    */
                    /* it is shown as written, its space padding trimmed.     */
                    return(Encoding.ASCII.GetString(abStart, PTB_FAT_TYPE16, PTB_SIGNATURE_BYTES).Trim() +
                           PTB_LabelText(abStart, PTB_FAT_LABEL16, PTB_FAT_LABEL_BYTES, true));
                }
            }

            /* STEP 4: Linux swap, by the signature in the last ten bytes of   */
            /* the page.  It is tested after the boot-sector kinds; why is     */
            /* not recorded.  A swap area whose first sector still held an     */
            /* old boot sector would read as that filesystem instead --        */
            /* whether a swap area's maker clears that sector is not           */
            /* established here.                                               */
            sSwap = Encoding.ASCII.GetString(abStart, PTB_SWAP_SIGNATURE, PTB_SWAP_BYTES);

            if (sSwap == "SWAPSPACE2" || sSwap == "SWAP-SPACE")
            {   /* A swap area. */
                return("Linux swap");
            }

            /* Nothing this program knows. */
            return("");
        }

        /*--------------------------------------------------------------------*/
        /* PTB_LabelText:                                                     */
        /*                                                                    */
        /* A volume label out of a probe buffer, ready to show after the      */
        /* filesystem's type ("ext4  \"rootfs\""), or "" when there is none.  */
        /* PTB_Read, and outside this file CommandImageOpening.cs,            */
        /* CommandOperations.cs, EngineSession.cs and PartitionForm.cs, rely  */
        /* on the two-space gap: each turns it into one space to put the      */
        /* words in a sentence.                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     abStart : the probe buffer.                                    */
        /*     iAt     : where the label starts in it.                        */
        /*     cbLabel : the label field's size.                              */
        /*     bFatOem : true for a FAT12 / 16 / 32 boot sector's label, in   */
        /*               the OEM code page (PTB_FAT_CODE_PAGE); false for an  */
        /*               ext label, UTF-8.                                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : "  \"label\"", or "".                                 */
        /*--------------------------------------------------------------------*/
        internal static string PTB_LabelText(byte[] abStart, int iAt, int cbLabel, bool bFatOem)
        {
            string sLabel;

            /* The field decoded as its filesystem writes it: a FAT label in     */
            /* the OEM code page the engine writes one in (PTB_FAT_CODE_PAGE;    */
            /* an accented capital is one byte there, which UTF-8 would show     */
            /* as a replacement character -- a label Windows wrote in another    */
            /* OEM code page can still show wrong characters); an ext label as   */
            /* UTF-8, what it is normally written in.  NULs and spaces at the    */
            /* end are padding, and leading spaces are trimmed too.              */
            if (bFatOem)
            {   /* FAT12 / 16 / 32: the OEM code page. */
                sLabel = Encoding.GetEncoding(PTB_FAT_CODE_PAGE).GetString(abStart, iAt, cbLabel);
            }
            else
            {   /* ext2 / 3 / 4: UTF-8. */
                sLabel = Encoding.UTF8.GetString(abStart, iAt, cbLabel);
            }

            /* The padding off both ends. */
            sLabel = sLabel.TrimEnd('\0', ' ').TrimStart(' ');

            if (sLabel.Length == 0 || sLabel == "NO NAME")
            {   /* Empty, or the "NO NAME" a FAT formatter writes for none:   */
                /* nothing to show after the type.                            */
                return("");
            }

            /* The label, quoted, with the two-space gap that parts it from   */
            /* the type ("ext4  \"rootfs\"").                                 */
            return("  \"" + sLabel + "\"");
        }
    }
}
