/*----------------------------------------------------------------------------*/
/* FsPropertiesForm.cs                                                        */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The Filesystem Properties window: the facts about the filesystem that is   */
/* open, in one place -- where it is (the image file or the device, and the   */
/* byte range of it the filesystem occupies), what it is (ext2, ext3, ext4,   */
/* FAT12, FAT16, FAT32 or exFAT, and its label), how big it is and how much   */
/* of it is used, its block and inode counts and its feature list as dumpe2fs */
/* prints it -- or, for FAT, its cluster size and count.  Opened, modal,      */
/* from File > Filesystem Properties and from the image pane's right-click    */
/* menu when no entry is selected -- only while an image is open              */
/* (MainForm.cs).                                                             */
/*                                                                            */
/* It shows; it changes nothing, so there is one Close button and a Copy      */
/* button that puts every row on the clipboard as text.  The facts are the    */
/* ones the caller read from the engine just before opening it (the free      */
/* counts move with every change, so the ones taken when the image was        */
/* opened would be stale).  The window does not touch the engine itself.      */
/*                                                                            */
/* The layout is a two-column table (caption, value) that sizes itself to its */
/* content, with the used-space bar and the wrapped feature list as rows of   */
/* their own, over the row of buttons; nothing carries a coordinate.  Its     */
/* text is in the program's dialog font, which the caller passes in; the      */
/* window neither chooses a font nor frees the one it is given.               */
/*----------------------------------------------------------------------------*/

/* The framework: the base types (Math, event arguments), the list the    */
/* feature names are gathered in, fonts and sizes, the copy text built    */
/* up, and the window and its controls.  The clipboard's exception type   */
/* is named in full where it is caught.                                   */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* FSP_clsFsPropertiesForm -- the Filesystem Properties window (the file  */
    /* header describes it).  A plain Form: its base class is declared here   */
    /* with the rest of its UI.                                               */
    /*                                                                        */
    /* Derives from Form because the framework requires it: WinForms makes a  */
    /* window only as a Form subclass.  That is the one kind of inheritance   */
    /* allowed here -- from a framework base class, never from a class of     */
    /* ours.                                                                  */
    /*------------------------------------------------------------------------*/
    internal sealed class FSP_clsFsPropertiesForm : Form
    {
        /* The window's sizes and one feature bit -- documentation table. */
        private const int FSP_PAD            = 12;         // padding round the content, pixels
        private const int FSP_MIN_WIDTH      = 420;        // least client width, pixels (the entry Properties window's, so the two read alike)
        private const int FSP_FEATURE_CHARS  = 44;         // the feature list wraps at about this many average characters
        private const int FSP_BAR_MAX        = 1000;       // the used-space bar runs 0..this, so a step is a tenth of a percent
        private const uint FSP_FCOM_FAST_COMMIT = 0x0400;  // the compatible feature bit fast_commit (the table below names it): earns a "Journal:" fact with the interrupted-write warning

        /*--------------------------------------------------------------------*/
        /* The superblock's three feature words, in the order dumpe2fs lists  */
        /* them (compatible, incompatible, read-only-compatible; each from    */
        /* its lowest bit up), each bit with the name e2fsprogs prints for it */
        /* -- the FIRST name its table gives a bit, so "extent" (not its      */
        /* alias "extents"), "uninit_bg" for the group-descriptor checksum    */
        /* and "needs_recovery" for a journal that has to be replayed.  A bit */
        /* not in a table is shown as dumpe2fs shows it: FEATURE_ with C, I   */
        /* or R and the bit's number.  Taken from e2fsprogs 1.47.4's          */
        /* lib/e2p/feature.c and lib/ext2fs/ext2_fs.h, and checked bit for    */
        /* bit against them: the three bits ext2_fs.h defines but feature.c   */
        /* names nowhere -- compat 0x0080 (exclude_inode), ro_compat 0x0004   */
        /* (btree_dir) and 0x0080 (has_snapshot) -- are left out here too, so */
        /* they read FEATURE_C7, FEATURE_R2 and FEATURE_R7 exactly as         */
        /* dumpe2fs prints them.  Each name table must stay in step with its  */
        /* bit table, entry for entry: Fsp_NameBits pairs them by index.      */
        /*--------------------------------------------------------------------*/
        private static readonly uint[] Fsp_auiCompatBits = // compatible superblock bits, paired with Fsp_asCompatNames
        {
            0x0001,                                    // dir_prealloc: preallocate directory blocks
            0x0002,                                    // imagic_inodes: AFS server inodes
            0x0004,                                    // has_journal: an ext3/ext4 journal
            0x0008,                                    // ext_attr: extended attributes
            0x0010,                                    // resize_inode: blocks reserved for growing online
            0x0020,                                    // dir_index: hashed (htree) directories
            0x0040,                                    // lazy_bg: groups left uninitialised (old)
            0x0100,                                    // snapshot_bitmap: the exclude bitmap of snapshot patches
            0x0200,                                    // sparse_super2: at most two backup superblocks
            0x0400,                                    // fast_commit: the fast-commit journal area
            0x0800,                                    // stable_inodes: inode numbers never change
            0x1000                                     // orphan_file: the orphan list kept in a file
        };
        private static readonly string[] Fsp_asCompatNames = // names at the same indices as Fsp_auiCompatBits
        {
            "dir_prealloc", "imagic_inodes", "has_journal", "ext_attr", "resize_inode", "dir_index",
            "lazy_bg", "snapshot_bitmap", "sparse_super2", "fast_commit", "stable_inodes", "orphan_file"
        };

        private static readonly uint[] Fsp_auiIncompatBits = // incompatible superblock bits, paired with Fsp_asIncompatNames
        {
            0x00001,                                   // compression: per-file compression (never finished)
            0x00002,                                   // filetype: the entry type kept in directory entries
            0x00004,                                   // needs_recovery: the journal must be replayed before use
            0x00008,                                   // journal_dev: this is an external journal, not a filesystem
            0x00010,                                   // meta_bg: meta block groups
            0x00040,                                   // extent: files mapped by extents
            0x00080,                                   // 64bit: block numbers past 2^32
            0x00100,                                   // mmp: multiple-mount protection
            0x00200,                                   // flex_bg: groups' metadata packed together
            0x00400,                                   // ea_inode: large extended attributes in inodes of their own
            0x01000,                                   // dirdata: data in directory entries (Lustre)
            0x02000,                                   // metadata_csum_seed: the checksum seed kept in the superblock
            0x04000,                                   // large_dir: directories past 2 GiB or a three-level htree
            0x08000,                                   // inline_data: small files' data in the inode
            0x10000,                                   // encrypt: per-directory encryption
            0x20000                                    // casefold: case-insensitive directories
        };
        private static readonly string[] Fsp_asIncompatNames = // names at the same indices as Fsp_auiIncompatBits
        {
            "compression", "filetype", "needs_recovery", "journal_dev", "meta_bg", "extent", "64bit", "mmp",
            "flex_bg", "ea_inode", "dirdata", "metadata_csum_seed", "large_dir", "inline_data", "encrypt", "casefold"
        };

        private static readonly uint[] Fsp_auiRoCompatBits = // read-only-compatible superblock bits, paired with Fsp_asRoCompatNames
        {
            0x00001,                                   // sparse_super: backup superblocks in a few groups only
            0x00002,                                   // large_file: files past 2 GiB
            0x00008,                                   // huge_file: files past 2 TiB
            0x00010,                                   // uninit_bg: group descriptor checksums (gdt_csum)
            0x00020,                                   // dir_nlink: more than 65,000 subdirectories
            0x00040,                                   // extra_isize: room for the larger inode's extra fields
            0x00100,                                   // quota: quotas kept in hidden inodes
            0x00200,                                   // bigalloc: allocation in clusters of blocks
            0x00400,                                   // metadata_csum: checksums on all metadata
            0x00800,                                   // replica: replicas (never finished)
            0x01000,                                   // read-only: the filesystem may only be mounted read-only
            0x02000,                                   // project: project quotas
            0x04000,                                   // shared_blocks: blocks shared between files (deduplicated)
            0x08000,                                   // verity: fs-verity files
            0x10000                                    // orphan_present: the orphan file has entries
        };
        private static readonly string[] Fsp_asRoCompatNames = // names at the same indices as Fsp_auiRoCompatBits
        {
            "sparse_super", "large_file", "huge_file", "uninit_bg", "dir_nlink", "extra_isize", "quota",
            "bigalloc", "metadata_csum", "replica", "read-only", "project", "shared_blocks", "verity", "orphan_present"
        };

        /*--------------------------------------------------------------------*/
        /* What blkid counts as ext3's own features; anything beyond them     */
        /* makes a filesystem ext4 -- blkid's test, from e2fsprogs'           */
        /* lib/blkid/probe.h (EXT3_FEATURE_INCOMPAT_SUPP / _RO_COMPAT_SUPP),  */
        /* checked.  That mount and Linux's file managers name a filesystem   */
        /* as blkid does is as found, not checked here.                       */
        /*--------------------------------------------------------------------*/
        private const uint FSP_EXT3_INCOMPAT   = 0x0002 | 0x0004 | 0x0010;   // filetype, needs_recovery, meta_bg
        private const uint FSP_EXT3_RO_COMPAT  = 0x0001 | 0x0002 | 0x0004;   // sparse_super, large_file, btree_dir
        private const uint FSP_HAS_JOURNAL     = 0x0004;                     // compat: has_journal (ext3, unless ext4 features say more)
        private const uint FSP_JOURNAL_DEV     = 0x0008;                     // incompat: journal_dev -- an external journal, no files at all

        /* The controls and the copy text -- documentation table. */
        private TableLayoutPanel Fsp_tblFacts;       // the caption / value rows
        private ProgressBar      Fsp_prgUsed;        // the used share of the capacity, as a bar
        private Label            Fsp_lblFeatures;    // the feature list, wrapped to the table's width
        private FlowLayoutPanel  Fsp_pnlButtons;     // Copy and Close, right-aligned
        private Button           Fsp_btnCopy;        // puts every row on the clipboard
        private Button           Fsp_btnClose;       // closes the window
        private StringBuilder    Fsp_sbText;         // every row as "Caption value" lines, for Copy
        private int              Fsp_iWrap;          // the width every value wraps at, pixels: FSP_FEATURE_CHARS average characters of the dialog font, or wider when the Capacity, Used or Free text needs it, as those never wrap (a top-docked table does not widen the window, so a longer value would be cut off)
        private WQU_clsWindowQueue Fsp_objQueue;     // the window's own command queue (WindowQueue.cs): Copy's handler posts to it, and it runs the copy through Fsp_RunItem, one item at a time

        /* The window's own commands, as its queue (Fsp_objQueue) carries   */
        /* them -- documentation table.                                     */
        private enum Fsp_enmItem
        {
            Copy = 1            // Copy: every row onto the clipboard (Fsp_CopyRows)
        }

        /*--------------------------------------------------------------------*/
        /* FSP_clsFsPropertiesForm (constructor):                             */
        /*                                                                    */
        /* Build the window over facts the caller has already read, and the   */
        /* file or device range they came from.  The window reads and         */
        /* changes nothing itself; its Copy gives exactly the rows it shows.  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     fntDialog   : the program's dialog font (the caller's; not     */
        /*                   freed here).                                     */
        /*     sSourcePath : the image file's path, or the device's path      */
        /*                   (\\.\PhysicalDriveN) when it is a device.        */
        /*     sDeviceText : the device's description (model, size, bus,      */
        /*                   path) when it is a device; "" for a file.        */
        /*     ullOffset   : where the filesystem starts in the file or       */
        /*                   device, bytes; 0 at its start.                   */
        /*     ullLength   : the length of the range opened, bytes; 0 for     */
        /*                   "to the end".                                    */
        /*     stVolume    : the filesystem's facts, read just now.           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the window, not yet shown.                     */
        /*--------------------------------------------------------------------*/
        internal FSP_clsFsPropertiesForm(Font fntDialog, string sSourcePath, string sDeviceText, ulong ullOffset, ulong ullLength,
                                      VLI_stVolumeInfo stVolume)
        {
            ulong  ullTotal;
            ulong  ullFree;
            ulong  ullUsed;
            uint   uiUsedInodes;
            string sLabel;
            string sWhole;
            string sCapacity;
            string sUsed;
            string sFree;
            int    iSlack;
            int    iWidth;

            /* The caller's dialog font, on the form for the controls below    */
            /* to take once added (as found); the copy text starts empty and   */
            /* gains a line with every row; and the width every value wraps    */
            /* at, FSP_FEATURE_CHARS of the font's "x" -- measured from the    */
            /* font given, so it follows the text size rather than a pixel     */
            /* constant.                                                       */
            this.Font  = fntDialog;
            Fsp_sbText = new StringBuilder();
            Fsp_iWrap  = TextRenderer.MeasureText(new string('x', FSP_FEATURE_CHARS), fntDialog).Width;

            /* HOW BIG, AND HOW FULL, worked out before any row is added:    */
            /* their texts set the wrap width below.  Free is as the         */
            /* superblock records it, which counts the blocks reserved for   */
            /* root as free; a free count larger than the total can only     */
            /* come from a damaged superblock, and is clamped so the sums    */
            /* stay sane.                                                    */
            ullTotal = stVolume.VLI_ullBlockCount * stVolume.VLI_uiBlockSize;
            ullFree  = stVolume.VLI_ullFreeBlocks * stVolume.VLI_uiBlockSize;

            if (ullFree > ullTotal)
            {   /* Impossible on a sound filesystem: show it as empty, not negative. */
                ullFree = ullTotal;
            }

            /* Used is what is not free, so it cannot wrap below zero now. */
            ullUsed = ullTotal - ullFree;

            /* Capacity, Used with its share, and Free, in the written size   */
            /* form the device windows use (PartitionForm.cs's                */
            /* DVF_SizeText).                                                 */
            sCapacity = DVF_clsDeviceForm.DVF_SizeText(ullTotal);
            sUsed     = DVF_clsDeviceForm.DVF_SizeText(ullUsed) + ", " + FSP_PercentText(ullUsed, ullTotal);
            sFree     = DVF_clsDeviceForm.DVF_SizeText(ullFree);

            /* Those three rows are never wrapped: a size broken across two   */
            /* lines ("100.0" above its "%") reads as two values.  So the     */
            /* wrap width every value takes is widened, when one of them is   */
            /* wider, to that one's measured width and one "x" more -- the    */
            /* slack, so the label's own measuring, which may differ from     */
            /* this one by a pixel or two, still finds room for it.  The      */
            /* window sizes itself to the table, so it widens with it, and    */
            /* every other value then wraps at the same, wider width.         */
            iSlack = TextRenderer.MeasureText("x", fntDialog).Width;
            iWidth = TextRenderer.MeasureText(sCapacity, fntDialog).Width + iSlack;
            if (iWidth > Fsp_iWrap)
            {   /* The capacity is wider than the wrap: the wrap widened. */
                Fsp_iWrap = iWidth;
            }

            iWidth = TextRenderer.MeasureText(sUsed, fntDialog).Width + iSlack;
            if (iWidth > Fsp_iWrap)
            {   /* The used row is wider: the wrap widened to it. */
                Fsp_iWrap = iWidth;
            }

            iWidth = TextRenderer.MeasureText(sFree, fntDialog).Width + iSlack;
            if (iWidth > Fsp_iWrap)
            {   /* The free row is wider: the wrap widened to it. */
                Fsp_iWrap = iWidth;
            }

            /* A fixed dialog centred on the main window, out of the          */
            /* taskbar, that SIZES ITSELF to its content (AutoSize, growing   */
            /* and shrinking) but is never narrower than FSP_MIN_WIDTH, the   */
            /* entry Properties window's width.                               */
            this.Text            = "Filesystem Properties";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox     = false;
            this.MinimizeBox     = false;
            this.ShowInTaskbar   = false;
            this.StartPosition   = FormStartPosition.CenterParent;
            this.Padding         = new Padding(FSP_PAD);
            this.AutoSize        = true;
            this.AutoSizeMode    = AutoSizeMode.GrowAndShrink;
            this.MinimumSize     = new Size(FSP_MIN_WIDTH, 0);

            /* The table: captions in the first column, values in the       */
            /* second, both sized to their widest cell; docked to the top   */
            /* and growing with its rows, which is what sizes the window.   */
            Fsp_tblFacts              = new TableLayoutPanel();
            Fsp_tblFacts.Dock         = DockStyle.Top;
            Fsp_tblFacts.AutoSize     = true;
            Fsp_tblFacts.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Fsp_tblFacts.ColumnCount  = 2;
            Fsp_tblFacts.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Fsp_tblFacts.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            /* WHERE IT IS.  A device is named by its description, which   */
            /* already carries its path; a file by its path.               */
            if (sDeviceText.Length != 0)
            {   /* A real card, stick or disk. */
                Fsp_AddFact("Device:", sDeviceText);
                sWhole = "the whole device";
            }
            else
            {   /* An image file. */
                Fsp_AddFact("Image file:", sSourcePath);
                sWhole = "the whole file";
            }

            /* The byte range the filesystem was opened at: the layout part     */
            /* of the request.  0 / 0 is the whole file or device; a            */
            /* partition has a start, and a length unless it runs to the end.   */
            if (ullOffset == 0 && ullLength == 0)
            {   /* The whole file or device: one row says so.  The     */
                /* filesystem's capacity has a row of its own below.   */
                Fsp_AddFact("Place:", sWhole);
            }
            else
            {   /* A partition (or a range typed by hand). */
                Fsp_AddFact("Starts at:", "byte " + ullOffset.ToString("N0") + " (" + DVF_clsDeviceForm.DVF_SizeShortText(ullOffset) + " in)");

                if (ullLength == 0)
                {   /* Opened as running to the end: "to the end of the file"   */
                    /* or "... of the device", the noun taken from sWhole       */
                    /* past its "the whole " so the two rows can never name     */
                    /* different things.                                        */
                    Fsp_AddFact("Range:", "to the end of the " + sWhole.Substring("the whole ".Length));
                }
                else
                {   /* A range of a stated length. */
                    Fsp_AddFact("Range:", DVF_clsDeviceForm.DVF_SizeText(ullLength));
                }
            }

            /* WHAT IT IS.  The type is named as blkid would name it (a FAT    */
            /* volume's as the engine reports it); the label is the volume's   */
            /* own (an ext superblock's, a FAT root's); the access mode is     */
            /* this session's, since every image opens read-only until the     */
            /* first change.                                                   */
            sLabel = ENG_clsNative.ENG_FromUtf8(stVolume.VLI_abVolumeName);

            if (sLabel.Length == 0)
            {   /* An unlabelled filesystem: said, rather than an empty row. */
                sLabel = "(none)";
            }

            /* The type and the label rows. */
            Fsp_AddFact("Type:", FSP_TypeName(stVolume));
            Fsp_AddFact("Label:", sLabel);

            if (stVolume.VLI_uiReadOnly != 0)
            {   /* The engine's facts say the session is read-only. */
                Fsp_AddFact("Opened:", "read-only");
            }
            else
            {   /* Reopened for writing by a change or Enable Writing. */
                Fsp_AddFact("Opened:", "for writing");
            }

            /* An EXTERNAL journal -- kept on another device, which cannot be   */
            /* reached from Windows -- is why such a filesystem never opens     */
            /* for writing: said, beside the access mode, so the read-only      */
            /* row has its reason.                                              */
            if (stVolume.VLI_uiExternalJournal != 0)
            {   /* The journal is somewhere else. */
                Fsp_AddFact("Journal:", "on a separate device (an external journal), which cannot be reached from here: read-only");
            }
            else if (stVolume.VLI_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_SQUASHFS)
            {   /* squashfs is why this filesystem never opens for writing:   */
                /* said beside the access mode, as an external journal is.    */
                Fsp_AddFact("Writable:", "never: squashfs is a compressed filesystem written whole, and nothing in it can be changed");
            }
            else if ((stVolume.VLI_uiFeatureCompat & FSP_FCOM_FAST_COMMIT) != 0)
            {   /* fast_commit: the warning the first write gives, kept here too. */
                Fsp_AddFact("Journal:", "with fast_commit: if writing here is interrupted, Linux may not replay the changes in progress (e2fsck may be needed); close cleanly");
            }

            /* HOW BIG, AND HOW FULL: Capacity, Used with its share (and the   */
            /* bar under it), Free -- their texts made at the top, where       */
            /* they set the wrap width so none of them wraps.                  */
            Fsp_AddFact("Capacity:", sCapacity);
            Fsp_AddFact("Used:", sUsed);
            Fsp_AddUsedBar(ullUsed, ullTotal);
            Fsp_AddFact("Free:", sFree);

            /* THE COUNTS, as e2fsck and dumpe2fs speak of them. */
            if (stVolume.VLI_uiFreeInodes > stVolume.VLI_uiInodeCount)
            {   /* A damaged count again: none used rather than a wrapped number. */
                uiUsedInodes = 0;
            }
            else
            {   /* The ordinary case. */
                uiUsedInodes = stVolume.VLI_uiInodeCount - stVolume.VLI_uiFreeInodes;
            }

            if (stVolume.VLI_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_SQUASHFS)
            {   /* squashfs: its own block size (the unit its data is           */
                /* compressed in), the blocks the image uses -- none free, as   */
                /* it is written whole -- and its inode count; no feature       */
                /* words (the engine reports 0s), so no features row.           */
                Fsp_AddFact("Block size:", stVolume.VLI_uiBlockSize.ToString("N0") + " bytes");
                Fsp_AddFact("Blocks:", stVolume.VLI_ullBlockCount.ToString("N0"));
                Fsp_AddFact("Inodes:", stVolume.VLI_uiInodeCount.ToString("N0"));
            }
            else if (stVolume.VLI_uiVolumeType != ENG_clsNative.ENG_VOLTYPE_EXT)
            {   /* A FAT volume: the engine gives its clusters as the blocks,   */
                /* so they are named as FAT names them; FAT has no inodes and   */
                /* no feature words (the engine reports 0s), so those rows      */
                /* are left out rather than shown empty.                        */
                Fsp_AddFact("Cluster size:", stVolume.VLI_uiBlockSize.ToString("N0") + " bytes");
                Fsp_AddFact("Clusters:", stVolume.VLI_ullBlockCount.ToString("N0") + " (" + stVolume.VLI_ullFreeBlocks.ToString("N0") + " free)");
            }
            else
            {   /* An ext filesystem: the block size, the block count with its   */
                /* free blocks, and the inode count with its used and free --    */
                /* the raw figures behind the sizes above, as the tools print    */
                /* them; then the features, wrapped, as the list can run to      */
                /* twenty names.                                                 */
                Fsp_AddFact("Block size:", stVolume.VLI_uiBlockSize.ToString("N0") + " bytes");
                Fsp_AddFact("Blocks:", stVolume.VLI_ullBlockCount.ToString("N0") + " (" + stVolume.VLI_ullFreeBlocks.ToString("N0") + " free)");
                Fsp_AddFact("Inodes:", stVolume.VLI_uiInodeCount.ToString("N0") + " (" + uiUsedInodes.ToString("N0") + " used, " +
                                       stVolume.VLI_uiFreeInodes.ToString("N0") + " free)");
                Fsp_AddFeatures(FSP_FeatureText(stVolume));
            }

            /* The window's own queue, made before any button can post to it:   */
            /* each click is run there, one at a time, never in its handler.    */
            Fsp_objQueue = new WQU_clsWindowQueue(this, Fsp_RunItem);

            /* Close, made first: the button row below flows right to left,     */
            /* so the first added is the rightmost.  It is both Enter and       */
            /* Escape (set at the end): there is nothing to accept or cancel.   */
            Fsp_btnClose          = new Button();
            Fsp_btnClose.Text     = "Close";
            Fsp_btnClose.AutoSize = true;
            Fsp_btnClose.Click   += Fsp_OnCloseClicked;

            /* Copy, with Alt+C, left of Close. */
            Fsp_btnCopy          = new Button();
            Fsp_btnCopy.Text     = "&Copy";
            Fsp_btnCopy.AutoSize = true;
            Fsp_btnCopy.Click   += Fsp_OnCopyClicked;

            /* Their row, under the table (docked to the top as well, with   */
            /* a pad above), flowing right to left so Close is rightmost.    */
            Fsp_pnlButtons               = new FlowLayoutPanel();
            Fsp_pnlButtons.Dock          = DockStyle.Top;
            Fsp_pnlButtons.FlowDirection = FlowDirection.RightToLeft;
            Fsp_pnlButtons.AutoSize      = true;
            Fsp_pnlButtons.Padding       = new Padding(0, FSP_PAD, 0, 0);
            Fsp_pnlButtons.Controls.Add(Fsp_btnClose);
            Fsp_pnlButtons.Controls.Add(Fsp_btnCopy);

            /* Both are docked to the top, so they stack; docking runs in   */
            /* reverse order of adding (the framework's rule, as found),    */
            /* hence the bottom one is added first.                         */
            this.Controls.Add(Fsp_pnlButtons);
            this.Controls.Add(Fsp_tblFacts);

            /* Enter and Escape both close (see the buttons above), and a   */
            /* resize -- the window settling to its content -- gives the    */
            /* used-space bar the value column's width.                     */
            this.AcceptButton = Fsp_btnClose;
            this.CancelButton = Fsp_btnClose;
            this.Resize      += Fsp_OnResize;
        }

        /*---------------------------------------------------------------------*/
        /* FSP_TypeName:                                                       */
        /*                                                                     */
        /* Name the filesystem as blkid would, from its feature bits rather    */
        /* than from how it was made: what this program's own formatter calls  */
        /* "ext3" has no journal (bfe_mkfs.c makes none) and reads as ext2     */
        /* here, as blkid names it too.  Used for the Type row only.           */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     stVolume : the filesystem's facts.                              */
        /*                                                                     */
        /* Returns:                                                            */
        /*     string : "ext2", "ext3", "ext4", "ext journal device", a FAT    */
        /*              volume's "FAT12", "FAT16", "FAT32" or "exFAT", or      */
        /*              "squashfs".                                            */
        /*---------------------------------------------------------------------*/
        internal static string FSP_TypeName(VLI_stVolumeInfo stVolume)
        {
            if (stVolume.VLI_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_FAT12)
            {   /* A FAT volume: the engine names its width, as FatFs decided   */
                /* it by the cluster count -- there are no feature bits.        */
                return("FAT12");
            }

            if (stVolume.VLI_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_FAT16)
            {   /* FAT16. */
                return("FAT16");
            }

            if (stVolume.VLI_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_FAT32)
            {   /* FAT32. */
                return("FAT32");
            }

            if (stVolume.VLI_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_EXFAT)
            {   /* exFAT. */
                return("exFAT");
            }

            if (stVolume.VLI_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_SQUASHFS)
            {   /* squashfs, as blkid names it. */
                return("squashfs");
            }

            /* An ext filesystem from here: named from its feature bits.    */
            /* The tests run from the most particular to the least, as      */
            /* blkid's probe table does (lib/blkid/probe.c: jbd, ext4dev,   */
            /* ext4, ext3, ext2): a journal device first, then ext4's       */
            /* features, then the journal that makes ext3 -- so an ext4     */
            /* with a journal is ext4, not ext3.  Not followed here:        */
            /* blkid's ext4dev (a test-filesystem flag) and its checks of   */
            /* what the running Linux kernel supports.                      */
            if ((stVolume.VLI_uiFeatureIncompat & FSP_JOURNAL_DEV) != 0)
            {   /* Not a filesystem: another filesystem's external journal,   */
                /* which carries no files at all, and is named as such.       */
                return("ext journal device");
            }

            if ((stVolume.VLI_uiFeatureIncompat & ~FSP_EXT3_INCOMPAT) != 0 || (stVolume.VLI_uiFeatureRoCompat & ~FSP_EXT3_RO_COMPAT) != 0)
            {   /* An incompat or ro_compat feature outside ext3's set: ext4,   */
                /* journal or not.  The compat word is not tested -- its        */
                /* features do not decide the type in blkid's test either.      */
                return("ext4");
            }

            if ((stVolume.VLI_uiFeatureCompat & FSP_HAS_JOURNAL) != 0)
            {   /* ext2's layout with a journal: ext3. */
                return("ext3");
            }

            /* No journal and nothing newer: ext2. */
            return("ext2");
        }

        /*--------------------------------------------------------------------*/
        /* FSP_FeatureText:                                                   */
        /*                                                                    */
        /* The feature list as dumpe2fs prints it ("Filesystem features:"),   */
        /* the names separated by spaces, so it can be compared line for line */
        /* with dumpe2fs's output on Linux.  Nothing set is hidden.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stVolume : the filesystem's facts.                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : "has_journal ext_attr ... extra_isize", or "(none)".  */
        /*--------------------------------------------------------------------*/
        internal static string FSP_FeatureText(VLI_stVolumeInfo stVolume)
        {
            List<string> lstNames;

            /* The names, gathered in order, then joined. */
            lstNames = new List<string>();

            /* The three words in the superblock's order -- compatible,    */
            /* incompatible, read-only-compatible -- which is the order    */
            /* dumpe2fs walks them in (e2fsprogs lib/e2p/ls.c,             */
            /* print_features), each from bit 0 up; an unnamed bit is      */
            /* written FEATURE_ with C, I or R and its number, as there.   */
            Fsp_NameBits(stVolume.VLI_uiFeatureCompat,   'C', Fsp_auiCompatBits,   Fsp_asCompatNames,   lstNames);
            Fsp_NameBits(stVolume.VLI_uiFeatureIncompat, 'I', Fsp_auiIncompatBits, Fsp_asIncompatNames, lstNames);
            Fsp_NameBits(stVolume.VLI_uiFeatureRoCompat, 'R', Fsp_auiRoCompatBits, Fsp_asRoCompatNames, lstNames);

            if (lstNames.Count == 0)
            {   /* An ext2 with no features at all (revision 0, or one made   */
                /* bare): said, rather than an empty row.  dumpe2fs prints    */
                /* "(none)" there too (lib/e2p/ls.c).                         */
                return("(none)");
            }

            /* The names in order, one space between each, as dumpe2fs   */
            /* writes them.                                              */
            return(string.Join(" ", lstNames.ToArray()));
        }

        /*---------------------------------------------------------------------*/
        /* Fsp_NameBits:                                                       */
        /*                                                                     */
        /* Add the name of every set bit of one feature word, bit 0 first.     */
        /* The two tables are parallel arrays (a bit, its name); a bit set in  */
        /* the word but absent from them gets dumpe2fs's FEATURE_ form.        */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     uiWord   : the feature word from the superblock.                */
        /*     cWord    : 'C', 'I' or 'R', for an unnamed bit's FEATURE_ form. */
        /*     auiBits  : the word's named bits.                               */
        /*     asNames  : their names, in the same order.                      */
        /*     lstNames : the list the names are added to.                     */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : the word's names are on the end of the list.             */
        /*---------------------------------------------------------------------*/
        private static void Fsp_NameBits(uint uiWord, char cWord, uint[] auiBits, string[] asNames, List<string> lstNames)
        {
            int    iBit;
            int    i;
            uint   uiMask;
            string sName;

            for (iBit = 0; iBit < 32; iBit++)
            {   /* One bit of the word per iteration, lowest first. */
                uiMask = 1u << iBit;

                if ((uiWord & uiMask) == 0)
                {   /* Not set: nothing to name. */
                    continue;
                }

                /* Its name from the table, or the FEATURE_ form when it has none. */
                sName = "FEATURE_" + cWord + iBit;

                for (i = 0; i < auiBits.Length; i++)
                {   /* One named bit per iteration. */
                    if (auiBits[i] == uiMask)
                    {   /* This is the bit: e2fsprogs' name for it, from the    */
                        /* name table's entry at the same index.  Each bit is   */
                        /* in a table once, so the search stops here.           */
                        sName = asNames[i];
                        break;
                    }
                }

                /* The bit's name, after the lower bits' names. */
                lstNames.Add(sName);
            }
        }

        /*--------------------------------------------------------------------*/
        /* FSP_PercentText:                                                   */
        /*                                                                    */
        /* A share as a percentage to one decimal place, "0.0 %" for an       */
        /* empty total rather than a division by zero.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     ullPart  : the part.                                           */
        /*     ullWhole : the whole.                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : "42.7 %" and the like.                                */
        /*--------------------------------------------------------------------*/
        internal static string FSP_PercentText(ulong ullPart, ulong ullWhole)
        {
            if (ullWhole == 0)
            {   /* Nothing to be a share of. */
                return("0.0 %");
            }

            /* Worked in floating point: the byte counts can pass 2^53 only on   */
            /* a filesystem of eight petabytes, where a tenth of a percent is    */
            /* still exact enough.                                               */
            return((100.0 * (double)ullPart / (double)ullWhole).ToString("F1") + " %");
        }

        /*--------------------------------------------------------------------*/
        /* Fsp_AddFact:                                                       */
        /*                                                                    */
        /* Add one row to the table, a caption and its value, and the same    */
        /* row to the text Copy puts on the clipboard.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sCaption : the left-hand text.                                 */
        /*     sValue   : the right-hand text.                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the row is in the table and in the copy text.           */
        /*--------------------------------------------------------------------*/
        private void Fsp_AddFact(string sCaption, string sValue)
        {
            Label lblCaption;
            Label lblValue;

            /* The caption, sized to its text, with a third of a pad above   */
            /* and below (the rows' spacing) and a full pad to its right,    */
            /* which is the gap between the two columns.                     */
            lblCaption          = new Label();
            lblCaption.Text     = sCaption;
            lblCaption.AutoSize = true;
            lblCaption.Margin   = new Padding(0, FSP_PAD / 3, FSP_PAD, FSP_PAD / 3);

            /* The value, sized to its text but never wider than Fsp_iWrap,   */
            /* so a long one (a device's description) wraps onto more lines   */
            /* -- the table, docked to                                        */
            /* the top, would not widen the window for it (as found), and     */
            /* the text would be cut off.                                     */
            lblValue             = new Label();
            lblValue.Text        = sValue;
            lblValue.AutoSize    = true;
            lblValue.MaximumSize = new Size(Fsp_iWrap, 0);
            lblValue.Margin      = new Padding(0, FSP_PAD / 3, 0, FSP_PAD / 3);

            /* A control added to a table goes in the next free cell, so    */
            /* the caption and value fill one row, left to right -- every   */
            /* row is added as such a pair, which keeps the columns true.   */
            Fsp_tblFacts.Controls.Add(lblCaption);
            Fsp_tblFacts.Controls.Add(lblValue);

            /* The clipboard's line: the caption, a space, the value. */
            Fsp_sbText.Append(sCaption + " " + sValue + "\r\n");
        }

        /*--------------------------------------------------------------------*/
        /* Fsp_AddUsedBar:                                                    */
        /*                                                                    */
        /* Add the used-space bar as a table row of its own under the Used    */
        /* row, in the value column, so it lines up with the numbers it       */
        /* pictures.  It is set once and never animated.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     ullUsed  : the bytes used.                                     */
        /*     ullTotal : the capacity.                                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the bar is in the table.                                */
        /*--------------------------------------------------------------------*/
        private void Fsp_AddUsedBar(ulong ullUsed, ulong ullTotal)
        {
            Label lblEmpty;
            int   iValue;

            /* The share as 0..FSP_BAR_MAX; an empty capacity shows nothing used. */
            iValue = 0;

            if (ullTotal != 0)
            {   /* Rounded to the nearest step, a tenth of a percent, worked   */
                /* in floating point so no product can overflow.               */
                iValue = (int)Math.Round((double)FSP_BAR_MAX * (double)ullUsed / (double)ullTotal);
            }

            /* The caption cell stays empty: a blank label holds it, so the   */
            /* bar lands in the value column of its own row.                  */
            lblEmpty          = new Label();
            lblEmpty.AutoSize = true;
            lblEmpty.Text     = "";

            /* A plain continuous bar over 0..FSP_BAR_MAX, set to the share   */
            /* -- clamped to the range, since the bar throws on a value       */
            /* outside it (as found) -- one text line tall, spaced like the   */
            /* rows, and anchored left and right; its width is the value      */
            /* column's, which Fsp_OnResize gives it once the table has       */
            /* laid out (an autosized column gives it none of its own).       */
            Fsp_prgUsed         = new ProgressBar();
            Fsp_prgUsed.Style   = ProgressBarStyle.Continuous;
            Fsp_prgUsed.Minimum = 0;
            Fsp_prgUsed.Maximum = FSP_BAR_MAX;
            Fsp_prgUsed.Value   = Math.Min(Math.Max(iValue, 0), FSP_BAR_MAX);
            Fsp_prgUsed.Height  = this.Font.Height;
            Fsp_prgUsed.Margin  = new Padding(0, FSP_PAD / 3, 0, FSP_PAD / 3);
            Fsp_prgUsed.Anchor  = AnchorStyles.Left | AnchorStyles.Right;

            /* The pair as one row, as Fsp_AddFact adds its rows.  Nothing   */
            /* goes into the copy text: the Used row says the share.         */
            Fsp_tblFacts.Controls.Add(lblEmpty);
            Fsp_tblFacts.Controls.Add(Fsp_prgUsed);
        }

        /*--------------------------------------------------------------------*/
        /* Fsp_AddFeatures:                                                   */
        /*                                                                    */
        /* Add the feature list as the table's last row, wrapped, so a long   */
        /* list (it can run to twenty names) grows the window downwards       */
        /* rather than off the screen sideways.                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sFeatures : the names, space-separated.                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the row is in the table and in the copy text.           */
        /*--------------------------------------------------------------------*/
        private void Fsp_AddFeatures(string sFeatures)
        {
            Label lblCaption;

            /* The caption, as Fsp_AddFact makes its captions. */
            lblCaption         = new Label();
            lblCaption.Text     = "Features:";
            lblCaption.AutoSize = true;
            lblCaption.Margin   = new Padding(0, FSP_PAD / 3, FSP_PAD, FSP_PAD / 3);

            /* The names, wrapping at Fsp_iWrap like every value -- the      */
            /* names are separated by spaces, so they break between names.   */
            /* Kept in a field, though nothing else here reads it.           */
            Fsp_lblFeatures             = new Label();
            Fsp_lblFeatures.Text        = sFeatures;
            Fsp_lblFeatures.AutoSize    = true;
            Fsp_lblFeatures.MaximumSize = new Size(Fsp_iWrap, 0);
            Fsp_lblFeatures.Margin      = new Padding(0, FSP_PAD / 3, 0, FSP_PAD / 3);

            /* The pair as the table's last row. */
            Fsp_tblFacts.Controls.Add(lblCaption);
            Fsp_tblFacts.Controls.Add(Fsp_lblFeatures);

            /* The clipboard's line, unwrapped. */
            Fsp_sbText.Append("Features: " + sFeatures + "\r\n");
        }

        /*--------------------------------------------------------------------*/
        /* Fsp_OnCopyClicked:                                                 */
        /*                                                                    */
        /* Copy: posted to the window's own queue, which runs it              */
        /* (Fsp_CopyRows).  Nothing else is done here.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the copy posted (a second click while one waits is      */
        /*            dropped by the queue).                                  */
        /*--------------------------------------------------------------------*/
        private void Fsp_OnCopyClicked(object objSender, EventArgs evArgs)
        {
            /* Posted, with nothing to carry. */
            Fsp_objQueue.WQU_Post((int)Fsp_enmItem.Copy, null);
        }

        /*--------------------------------------------------------------------*/
        /* Fsp_RunItem:                                                       */
        /*                                                                    */
        /* The window's own queue's runner (WindowQueue.cs): one item the     */
        /* handlers posted, run to its end.  The queue calls it once per item */
        /* and never inside another.                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iItem   : the item, an Fsp_enmItem value.                      */
        /*     objArgs : unused.                                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item's work done, or nothing for an unknown item.   */
        /*--------------------------------------------------------------------*/
        private void Fsp_RunItem(int iItem, object objArgs)
        {
            switch ((Fsp_enmItem)iItem)
            {   /* One case per item: its own function. */
                case Fsp_enmItem.Copy:
                    /* The rows onto the clipboard. */
                    Fsp_CopyRows();
                    break;
                default:
                    /* No such item (a guard: only the handler above posts):   */
                    /* nothing done.                                           */
                    break;
            }
        }

        /*--------------------------------------------------------------------*/
        /* Fsp_CopyRows:                                                      */
        /*                                                                    */
        /* Every row, "Caption value", one per line, onto the clipboard as    */
        /* text -- for a bug report or a note, where a picture of the window  */
        /* would not paste.  Run by the window's queue (Fsp_RunItem).         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the clipboard holds the text, or the reason it could    */
        /*            not was said.                                           */
        /*--------------------------------------------------------------------*/
        private void Fsp_CopyRows()
        {
            try
            {   /* Every row's line, as gathered while the rows were added --   */
                /* never empty, since the window always has rows.  Another      */
                /* program can hold the clipboard open for a moment.            */
                Clipboard.SetText(Fsp_sbText.ToString());
            }
            catch (System.Runtime.InteropServices.ExternalException exClipboard)
            {   /* The clipboard was busy (the exception the framework         */
                /* raises then -- as found): said, with the reason, rather     */
                /* than failing silently.  No retry; the user can press Copy   */
                /* again.  Any other exception is not caught here.             */
                MSG_clsMessageForm.MSG_Show(this, this.Font, "The clipboard is in use by another program: " + exClipboard.Message,
                                         "Filesystem Properties", MessageBoxButtons.OK, MessageBoxIcon.Warning,
                                         MessageBoxDefaultButton.Button1);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Fsp_OnCloseClicked:                                                */
        /*                                                                    */
        /* Close: the window shows only, so there is nothing to keep.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window closes.                                      */
        /*--------------------------------------------------------------------*/
        private void Fsp_OnCloseClicked(object objSender, EventArgs evArgs)
        {
            /* Closed; ShowDialog returns in MainForm.cs, whose using block   */
            /* disposes the window.                                           */
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Fsp_OnResize:                                                      */
        /*                                                                    */
        /* The window's size settled (it sizes itself to the table): the      */
        /* used-space bar takes the width of the value column, which is only  */
        /* known once the table has laid out its other rows -- an autosized   */
        /* column gives a bar no width of its own.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the window.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the bar spans the value column.                         */
        /*--------------------------------------------------------------------*/
        private void Fsp_OnResize(object objSender, EventArgs evArgs)
        {
            int[] aiWidths;

            /* The table's column widths as laid out at this moment. */
            aiWidths = Fsp_tblFacts.GetColumnWidths();

            if (aiWidths.Length == 2 && aiWidths[1] > 0)
            {   /* Both columns laid out: the bar takes the value column's   */
                /* width, less its own margins.  Before the table has laid   */
                /* out, the bar is left as it is until the next resize.      */
                Fsp_prgUsed.Width = aiWidths[1] - Fsp_prgUsed.Margin.Horizontal;
            }
        }
    }
}
