/*----------------------------------------------------------------------------*/
/* PartitionForm.cs                                                           */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* Two dialogs that choose WHERE to work, and the table reader the first is   */
/* filled from:                                                               */
/*                                                                            */
/*   - PRF_clsPartitionForm: which partition of an image file or a device to  */
/*     open, or a byte offset typed by hand.  Its list comes from a           */
/*     partition-table read made BEFORE the dialog is built, on the command   */
/*     worker -- an image file's by PRF_ReadTableFile                         */
/*     (CommandImageOpening.cs), a device's through the worker's engine       */
/*     reader (CommandNativeSteps.cs) -- and what is chosen goes back to the  */
/*     command sequence that asked, which checks it before using it.          */
/*   - DVF_clsDeviceForm: which disk, and how much of it, for the device      */
/*     commands.                                                              */
/*                                                                            */
/* The reader here only LISTS (MBR, the logical chain, GPT, with no GPT CRC   */
/* check); the table the partition window edits is PartitionTable.cs's own    */
/* model, read and checked there.                                             */
/*----------------------------------------------------------------------------*/

/* The framework: the base types (Math, event arguments, the exceptions   */
/* caught), the lists of partitions and disks, the font and sizes, the    */
/* image file's table read, and the two dialogs and their controls.       */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* PFR_clsFileReader -- the table reader's access to an image FILE: the   */
    /* open stream, read at the offsets the reader asks for.  Its PFR_Read is */
    /* the reader function PRF_ReadTableFile hands the table reader (the same */
    /* shape as the engine-backed reader a device's read uses) --             */
    /* documentation table.                                                   */
    /*------------------------------------------------------------------------*/
    internal class PFR_clsFileReader
    {
        private readonly FileStream Pfr_stmImage; // the image file, opened by PRF_ReadTableFile and closed by it after the read; borrowed

        /*------------------------------------------------------------------*/
        /* PFR_clsFileReader:                                               */
        /*                                                                  */
        /* A reader over an open image file, for one table read.            */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     stmImage : the open file (the caller's; not closed here).    */
        /*                                                                  */
        /* Returns:                                                         */
        /*     (constructor) : the reader; it opens and holds nothing of    */
        /*                     its own.                                     */
        /*------------------------------------------------------------------*/
        internal PFR_clsFileReader(FileStream stmImage)
        {
            /* Kept for PFR_Read.  The caller's using block encloses the   */
            /* whole table read, every call of PFR_Read included.          */
            Pfr_stmImage = stmImage;
        }

        /*------------------------------------------------------------------*/
        /* PFR_Read:                                                        */
        /*                                                                  */
        /* Fill a buffer from the image file at a byte offset -- all of it, */
        /* or report that the file cannot supply it.                        */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     ullOffset : where to read from, bytes from the file's start. */
        /*     abBuffer  : the buffer; its length is how much to read.      */
        /*                                                                  */
        /* Returns:                                                         */
        /*     bool : true with the buffer full; false when the run passes  */
        /*            the file's end or the file ends early.  A read error  */
        /*            throws (PRF_ReadTableFile catches it).                */
        /*------------------------------------------------------------------*/
        internal bool PFR_Read(ulong ullOffset, byte[] abBuffer)
        {
            int iRead;
            int iGot;

            if (ullOffset + (ulong)abBuffer.Length > (ulong)Pfr_stmImage.Length)
            {   /* The run would pass the file's end: not read.  The sum is   */
                /* not checked for wrapping -- an offset near 2^64 would      */
                /* pass, and the Seek below would then throw on its           */
                /* negative long, which the caller's catch takes as a         */
                /* failed read.                                               */
                return(false);
            }

            /* To the offset, nothing read yet. */
            Pfr_stmImage.Seek((long)ullOffset, SeekOrigin.Begin);
            iRead = 0;

            while (iRead < abBuffer.Length)
            {   /* One read per iteration, for what is still missing: a   */
                /* stream may hand back fewer bytes than asked for.       */
                iGot = Pfr_stmImage.Read(abBuffer, iRead, abBuffer.Length - iRead);
                if (iGot <= 0)
                {   /* Nothing more: the file ended before the buffer was   */
                    /* full.                                                */
                    return(false);
                }

                /* What came, counted. */
                iRead += iGot;
            }

            /* The buffer full: the reader only ever sees whole runs. */
            return(true);
        }
    }

    /*------------------------------------------------------------------------*/
    /* PRT_clsPartition -- one partition found in a disk image's table.       */
    /*------------------------------------------------------------------------*/
    internal sealed class PRT_clsPartition
    {
        public string PRT_sNumber;   // how the table numbers it ("1", "5", "GPT 3")
        public string PRT_sType;     // its type, in words where the type is a known one; a GPT partition's name follows in quotes
        public string PRT_sContent;  // what its first page says it holds ("ext4  \"rootfs\"", "FAT32", "squashfs", ...); "" when nothing was recognised or it could not be read
        public ulong PRT_ullOffset;  // where it starts in the file, bytes
        public ulong PRT_ullLength;  // its length, bytes
        public string PRT_sInvalid;  // why the row cannot be opened, as a phrase after "it" ("has no length", "runs past the end of the device"); set by the device open (Sys_DeviceOpenSequence, CommandDeviceOpening.cs) before the dialog is built; null for a row that can be -- every row the table reader makes
    }

    /*------------------------------------------------------------------------*/
    /* PRF_clsPartitionForm -- the partition dialog itself, and (static) the  */
    /* table reader it is filled from, which runs before it is built.         */
    /*                                                                        */
    /* Derives from Form because the framework requires it: WinForms makes a  */
    /* window only as a Form subclass.  That is the one kind of inheritance   */
    /* allowed here -- from a framework base class, never from a class of     */
    /* ours.                                                                  */
    /*------------------------------------------------------------------------*/
    internal sealed class PRF_clsPartitionForm : Form
    {
        /* The dialog's sizes, and the MBR's layout and type bytes this       */
        /* file's own table reader works with -- documentation table.  This   */
        /* reader is a second one beside PartitionTable.cs's (PTB_Read),      */
        /* simpler: it lists, and checks no GPT CRC.                          */
        private const int   PRF_WIDTH          = 760;     // client width, pixels (room for five columns, a GPT partition's name among them)
        private const int   PRF_LIST_ROWS      = 8;       // the partition list shows this many rows without scrolling, at whatever the dialog's font is (the height is measured from the font, not fixed in pixels)
        private const int   PRF_PAD            = 12;      // padding round the content, pixels
        private const int   PRF_CELL_PAD       = 16;      // added to a column's measured text width for the cell's margins, pixels
        private const int   PRF_SECTOR         = 512;     // bytes per sector of an image FILE's table, and the grid a typed offset must sit on (a device's table is read in the device's own sectors)
        private const int   PRF_MBR_TABLE      = 446;     // offset of the four 16-byte partition entries in an MBR sector
        private const int   PRF_MBR_ENTRY      = 16;      // size of one MBR partition entry
        private const int   PRF_MBR_SIGNATURE  = 510;     // offset of the 0x55 0xAA boot signature
        private const int   PRF_MAX_LOGICAL    = 64;      // most logical partitions followed in an extended partition (a damaged chain can loop)
        private const int   PRF_MAX_GPT        = 256;     // most GPT entries read
        private const byte  PRF_TYPE_EXTENDED  = 0x05;    // MBR type: extended partition (CHS)
        private const byte  PRF_TYPE_EXT_LBA   = 0x0F;    // MBR type: extended partition (LBA)
        private const byte  PRF_TYPE_EXT_LINUX = 0x85;    // MBR type: Linux extended partition
        private const byte  PRF_TYPE_GPT       = 0xEE;    // MBR type: the protective entry that says "this disk has a GPT"

        /* The list's columns; Prf_FitLayout and the row fill index them,   */
        /* column 1 (Type) taking the width the others leave.               */
        private static readonly string[] Prf_arrColumns = { "Partition", "Type", "Contents", "Start (bytes)", "Size (bytes)" };   // the list's headings, left to right

        /* The work the dialog's handlers post to its own queue, which   */
        /* Prf_RunItem runs one at a time -- documentation table.        */
        private enum Prf_enmItem
        {
            Accept = 1     // Open, Enter or a row's double click: the offset taken, or refused with its reason (Prf_AcceptOffset)
        }

        /* The dialog's own queue: its handlers post, never show a box. */
        private WQU_clsWindowQueue Prf_objQueue;   // made in the constructor before any control is hooked

        /* The outcome, valid when PRF_bAccepted. */
        internal bool  PRF_bAccepted;    // true when Open was pressed with a usable offset
        internal ulong PRF_ullOffset;    // where the filesystem starts in the file, bytes
        internal ulong PRF_ullLength;    // its length in bytes, or 0 for "to the end of the file"

        /* The dialog's controls -- documentation table. */
        private Label           Prf_lblFound;      // says what kind of table was found
        private ListView        Prf_lstParts;      // the partitions
        private Label           Prf_lblOffset;     // the caption of the offset box
        private TextBox         Prf_txtOffset;     // the offset to open at: filled by a click on a row, or typed
        private FlowLayoutPanel Prf_pnlOffset;     // the caption and the box on one row
        private FlowLayoutPanel Prf_pnlButtons;    // Open and Cancel, right-aligned
        private Button          Prf_btnOpen;       // accepts
        private Button          Prf_btnCancel;     // abandons

        /* What the dialog was given to list. */
        private List<PRT_clsPartition> Prf_lstFound;  // what the table held, in table order; the list rows are in the same order, so a row's index is its partition's

        /*--------------------------------------------------------------------*/
        /* PRF_ReadOffsetDraft:                                               */
        /*                                                                    */
        /* The offset box's text as it stands, typed but not accepted, for    */
        /* the question presenter to keep when an Exit takes the question     */
        /* down, and put back if the question is shown again                  */
        /* (CommandQuestionPresenter.cs).                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the text exactly, a half-typed or invalid number      */
        /*              included.                                             */
        /*--------------------------------------------------------------------*/
        internal string PRF_ReadOffsetDraft()
        {
            /* The box's text; it is not the outcome (PRF_ullOffset). */
            return(Prf_txtOffset.Text);
        }

        /*--------------------------------------------------------------------*/
        /* PRF_SetOffsetDraft:                                                */
        /*                                                                    */
        /* Put a kept draft back in the offset box when the question is shown */
        /* again.  Text only: nothing is accepted -- Open still checks it.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sText : the draft; null for none, which gives the box's first  */
        /*             text, "0".                                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the box holds the text.                                 */
        /*--------------------------------------------------------------------*/
        internal void PRF_SetOffsetDraft(string sText)
        {
            if (sText == null)
            {   /* No draft kept: the box's own first text. */
                sText = "0";
            }

            /* Into the box; the outcome fields are not touched. */
            Prf_txtOffset.Text = sText;
        }

        /*--------------------------------------------------------------------*/
        /* PRF_clsPartitionForm (constructor):                                */
        /*                                                                    */
        /* Build the dialog round a partition table already read (by          */
        /* PRF_ReadTableFile, or by the engine for a device).  An image that  */
        /* could not be read, or holds no table, still gets the dialog: the   */
        /* list is empty and the offset is typed.  A row the caller marked    */
        /* invalid (PRT_sInvalid: a device's partition that does not fit the  */
        /* device) is listed greyed with its reason, and Open refuses it.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     fntDialog : the program's dialog font (the caller's; not       */
        /*                 freed here).                                       */
        /*     sName     : what to call the image or device in the title.     */
        /*     lstFound  : the partitions, in table order; may be empty.      */
        /*     sFound    : the one-line account of what the read found.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the dialog, not yet shown.                     */
        /*--------------------------------------------------------------------*/
        internal PRF_clsPartitionForm(Font fntDialog, string sName, List<PRT_clsPartition> lstFound, string sFound)
        {
            ListViewItem lviRow;
            string       sContent;
            int          i;

            /* Nothing accepted yet -- a dialog closed any other way than   */
            /* Open leaves these as they are -- and the partitions kept.    */
            PRF_bAccepted = false;
            PRF_ullOffset = 0;
            PRF_ullLength = 0;
            Prf_lstFound  = lstFound;

            /* The caller's dialog font, set before any control is made:    */
            /* each control takes it once parented, and the two             */
            /* measurements below (the offset box, the list's height) are   */
            /* taken in it.                                                 */
            this.Font = fntDialog;

            /* A fixed dialog, centred on its owner, with no maximise,      */
            /* minimise or taskbar button, and PRF_PAD round its content.   */
            this.Text            = "Open a partition in " + sName;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox     = false;
            this.MinimizeBox     = false;
            this.ShowInTaskbar   = false;
            this.StartPosition   = FormStartPosition.CenterParent;
            this.Padding         = new Padding(PRF_PAD);

            /* Cancel, sized to its text in the dialog's font; it closes   */
            /* with nothing accepted.                                      */
            Prf_btnCancel          = new Button();
            Prf_btnCancel.Text     = "Cancel";
            Prf_btnCancel.AutoSize = true;
            Prf_btnCancel.Click   += Prf_OnCancelClicked;

            /* The dialog's own queue, made before the controls that post   */
            /* to it are hooked: Open and a row's double click post their   */
            /* acceptance there, and Prf_RunItem runs it.                   */
            Prf_objQueue = new WQU_clsWindowQueue(this, Prf_RunItem);

            /* Open, likewise; it asks for the offset to be checked and taken. */
            Prf_btnOpen          = new Button();
            Prf_btnOpen.Text     = "Open";
            Prf_btnOpen.AutoSize = true;
            Prf_btnOpen.Click   += Prf_OnOpenClicked;

            /* The button row along the bottom, a pad above it, flowing   */
            /* right to left -- so Cancel, added first, is at the right   */
            /* edge, and Open to its left, as Windows dialogs put them.   */
            Prf_pnlButtons               = new FlowLayoutPanel();
            Prf_pnlButtons.Dock          = DockStyle.Bottom;
            Prf_pnlButtons.FlowDirection = FlowDirection.RightToLeft;
            Prf_pnlButtons.AutoSize      = true;
            Prf_pnlButtons.Padding       = new Padding(0, PRF_PAD, 0, 0);
            Prf_pnlButtons.Controls.Add(Prf_btnCancel);
            Prf_pnlButtons.Controls.Add(Prf_btnOpen);

            /* The offset row: a caption and a box.  Hexadecimal is accepted   */
            /* with a 0x prefix, since that is how tools print offsets.        */
            Prf_lblOffset           = new Label();
            Prf_lblOffset.Text      = "Open at byte &offset (decimal, or hexadecimal with 0x):";
            Prf_lblOffset.AutoSize  = true;
            Prf_lblOffset.Anchor    = AnchorStyles.Left;
            Prf_lblOffset.TextAlign = ContentAlignment.MiddleLeft;

            /* The box, starting at 0 (the whole file) until a row is   */
            /* selected and fills it.                                   */
            Prf_txtOffset       = new TextBox();
            Prf_txtOffset.Text  = "0";

            /* Wide enough for the longest offset typed the hexadecimal way   */
            /* (0x and sixteen digits), measured in the DIALOG's font: the    */
            /* box has no parent yet, so its own Font is still the small      */
            /* default.                                                       */
            Prf_txtOffset.Width = TextRenderer.MeasureText("0x0000000000000000", this.Font).Width + PRF_CELL_PAD;

            /* The caption and the box side by side on one row, under the   */
            /* list, a pad above, never wrapped.                            */
            Prf_pnlOffset              = new FlowLayoutPanel();
            Prf_pnlOffset.Dock         = DockStyle.Top;
            Prf_pnlOffset.AutoSize     = true;
            Prf_pnlOffset.WrapContents = false;
            Prf_pnlOffset.Padding      = new Padding(0, PRF_PAD, 0, 0);
            Prf_pnlOffset.Controls.Add(Prf_lblOffset);
            Prf_pnlOffset.Controls.Add(Prf_txtOffset);

            /* The list of partitions: details view, whole rows selected,   */
            /* one at a time, the selection still shown when the focus is   */
            /* in the offset box; as tall as PRF_LIST_ROWS rows in the      */
            /* dialog's font (DVF_ListHeightForRows, below).                */
            Prf_lstParts               = new ListView();
            Prf_lstParts.Dock          = DockStyle.Top;
            Prf_lstParts.Height        = DVF_clsDeviceForm.DVF_ListHeightForRows(this.Font, PRF_LIST_ROWS);
            Prf_lstParts.View          = View.Details;
            Prf_lstParts.FullRowSelect = true;
            Prf_lstParts.MultiSelect   = false;
            Prf_lstParts.HideSelection = false;

            for (i = 0; i < Prf_arrColumns.Length; i++)
            {   /* One column heading per iteration. */
                Prf_lstParts.Columns.Add(Prf_arrColumns[i]);
            }

            /* The two byte counts (Start, Size) right-aligned, so their   */
            /* digits line up.                                             */
            Prf_lstParts.Columns[3].TextAlign = HorizontalAlignment.Right;
            Prf_lstParts.Columns[4].TextAlign = HorizontalAlignment.Right;

            for (i = 0; i < Prf_lstFound.Count; i++)
            {   /* One partition per iteration, in table order: its number,   */
                /* type, contents, and start and size with thousands          */
                /* separators -- for reading; the offset box gets the plain   */
                /* number (Prf_OnRowSelected).                                */
                sContent = Prf_lstFound[i].PRT_sContent;
                if (Prf_lstFound[i].PRT_sInvalid != null)
                {   /* A row marked invalid (a device's partition that does    */
                    /* not fit the device): still listed, so the user sees     */
                    /* the whole table, with the reason after whatever its     */
                    /* first page held -- a partition that starts inside the   */
                    /* device and runs past its end can still name its         */
                    /* filesystem.                                             */
                    if (sContent.Length != 0)
                    {   /* Something was recognised: kept, the reason after it. */
                        sContent = sContent + "; ";
                    }

                    sContent = sContent + "Invalid: " + Prf_lstFound[i].PRT_sInvalid;
                }

                /* The row's cells, in Prf_arrColumns' order. */
                lviRow = new ListViewItem(Prf_lstFound[i].PRT_sNumber);
                lviRow.SubItems.Add(Prf_lstFound[i].PRT_sType);
                lviRow.SubItems.Add(sContent);
                lviRow.SubItems.Add(Prf_lstFound[i].PRT_ullOffset.ToString("N0"));
                lviRow.SubItems.Add(Prf_lstFound[i].PRT_ullLength.ToString("N0"));
                if (Prf_lstFound[i].PRT_sInvalid != null)
                {   /* Drawn in the system's greyed-text colour, as a disabled   */
                    /* item is, so it reads as one that cannot be opened.        */
                    /* The row's own ForeColor covers its cells (the list's      */
                    /* UseItemStyleForSubItems is left at its default, true).    */
                    lviRow.ForeColor = SystemColors.GrayText;
                }

                /* The row into the list, in table order. */
                Prf_lstParts.Items.Add(lviRow);
            }

            /* A row selected fills the offset box; a row double-clicked     */
            /* (or Enter on it) is Open: the list's own handler calls what   */
            /* the button's does (Prf_AcceptOffset).                         */
            Prf_lstParts.SelectedIndexChanged += Prf_OnRowSelected;
            Prf_lstParts.ItemActivate         += Prf_OnRowActivated;

            /* What was found, in a line above the list. */
            Prf_lblFound           = new Label();
            Prf_lblFound.Dock      = DockStyle.Top;
            Prf_lblFound.AutoSize  = false;
            Prf_lblFound.Text      = sFound;
            Prf_lblFound.TextAlign = ContentAlignment.MiddleLeft;

            /* Docking runs in reverse order of adding (the framework's    */
            /* rule, as found): the buttons, added last, take the bottom   */
            /* first; then the found line, the list and the offset row     */
            /* stack down from the top in that order.                      */
            this.Controls.Add(Prf_pnlOffset);
            this.Controls.Add(Prf_lstParts);
            this.Controls.Add(Prf_lblFound);
            this.Controls.Add(Prf_pnlButtons);

            /* The line's height is measured only now, once it has its parent   */
            /* and so the dialog's font; before, it would be the default's.     */
            Prf_lblFound.Height = Prf_lblFound.PreferredHeight + PRF_PAD;

            /* Enter is Open and Escape is Cancel. */
            this.AcceptButton = Prf_btnOpen;
            this.CancelButton = Prf_btnCancel;

            /* A first size -- its height only a placeholder, which        */
            /* Prf_FitLayout replaces at once with what the rows need --   */
            /* then refitted on every resize from here on.                 */
            this.ClientSize = new Size(PRF_WIDTH, PRF_WIDTH / 2);
            this.Resize    += Prf_OnResize;
            Prf_FitLayout();

            if (Prf_lstParts.Items.Count != 0)
            {   /* Start on the first partition that holds an ext filesystem,   */
                /* else the first that looks like Linux, else the first.        */
                /* Selecting it fills the offset box (Prf_OnRowSelected), so    */
                /* Enter alone opens the likeliest partition.                   */
                Prf_lstParts.Items[Prf_FirstExtRow()].Selected = true;
            }
        }

        /*--------------------------------------------------------------------*/
        /* Prf_FirstExtRow:                                                   */
        /*                                                                    */
        /* The row to select when the dialog opens: the first partition whose */
        /* CONTENTS are an ext filesystem, since that is what this program    */
        /* opens; failing that, the first whose table type names Linux (the   */
        /* contents could not be read, or the dump is truncated); failing     */
        /* that, the first row.  A row marked invalid (PRT_sInvalid) is       */
        /* passed over at every stage, so Enter alone never lands on one --   */
        /* unless every row is invalid, when the first is selected and Open   */
        /* says why (the device open refuses such a table before it gets      */
        /* here, so that is a guard).                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : that row's index.                                        */
        /*--------------------------------------------------------------------*/
        private int Prf_FirstExtRow()
        {
            int i;

            for (i = 0; i < Prf_lstFound.Count; i++)
            {   /* One partition per iteration: by contents, a valid row only. */
                if (Prf_lstFound[i].PRT_sInvalid == null && Prf_lstFound[i].PRT_sContent.StartsWith("ext"))
                {   /* An ext2, ext3 or ext4 filesystem is there. */
                    return(i);
                }
            }

            for (i = 0; i < Prf_lstFound.Count; i++)
            {   /* One partition per iteration: by the table's type, a valid   */
                /* row only.                                                   */
                if (Prf_lstFound[i].PRT_sInvalid == null && Prf_lstFound[i].PRT_sType.StartsWith("Linux") &&
                    !Prf_lstFound[i].PRT_sType.StartsWith("Linux swap") && !Prf_lstFound[i].PRT_sType.StartsWith("Linux extended"))
                {   /* A Linux data partition by type. */
                    return(i);
                }
            }

            for (i = 0; i < Prf_lstFound.Count; i++)
            {   /* One partition per iteration: the first valid row of any kind. */
                if (Prf_lstFound[i].PRT_sInvalid == null)
                {   /* Valid: the likeliest left. */
                    return(i);
                }
            }

            /* No valid row at all: the first row. */
            return(0);
        }

        /*--------------------------------------------------------------------*/
        /* Prf_FitLayout:                                                     */
        /*                                                                    */
        /* Size the list's columns from their content -- the Type column      */
        /* takes whatever the others leave -- give the found line the height  */
        /* its wrapped text needs, and make the dialog exactly as tall as its */
        /* rows need.                                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the columns and the client height fit.                  */
        /*--------------------------------------------------------------------*/
        private void Prf_FitLayout()
        {
            int iOthers;
            int iWidth;
            int iLineWidth;
            int iCell;
            int iColumn;
            int iRow;

            /* The width the measured columns take, added up as each is   */
            /* sized.                                                     */
            iOthers = 0;

            for (iColumn = 0; iColumn < Prf_lstParts.Columns.Count; iColumn++)
            {   /* One column per iteration; column 1 (Type) is sized last. */
                if (iColumn == 1)
                {   /* The Type column takes the remainder. */
                    continue;
                }

                /* The heading's own width first: a column is never   */
                /* narrower than its heading.                         */
                iWidth = TextRenderer.MeasureText(Prf_lstParts.Columns[iColumn].Text, Prf_lstParts.Font).Width;

                for (iRow = 0; iRow < Prf_lstParts.Items.Count; iRow++)
                {   /* One row's cell in this column per iteration. */
                    iCell = TextRenderer.MeasureText(Prf_lstParts.Items[iRow].SubItems[iColumn].Text, Prf_lstParts.Font).Width;

                    if (iCell > iWidth)
                    {   /* The widest so far. */
                        iWidth = iCell;
                    }
                }

                /* The widest text plus the cell's margins is the column's   */
                /* width, and it counts toward what the others take.         */
                iWidth                              = iWidth + PRF_CELL_PAD;
                Prf_lstParts.Columns[iColumn].Width = iWidth;
                iOthers                             = iOthers + iWidth;
            }

            /* What is left for Type: the list's inside width less the        */
            /* other columns, and less a vertical scroll bar's width always   */
            /* -- room kept for one whether or not the rows need it, so the   */
            /* columns still fit inside the list when one appears.            */
            iWidth = Prf_lstParts.ClientSize.Width - iOthers - SystemInformation.VerticalScrollBarWidth;

            if (iWidth > 0)
            {   /* There is room left for the type.  When there is none, the   */
                /* column keeps the width it had, and the list can scroll      */
                /* sideways (the list view's own behaviour, as found).         */
                Prf_lstParts.Columns[1].Width = iWidth;
            }

            /* The found line as tall as its text needs at the dialog's        */
            /* present width, so a long line -- the no-table account names     */
            /* every filesystem and then asks for the offset -- wraps onto     */
            /* further lines instead of being cut at its first.  The line is   */
            /* a label that is not auto-sized, which breaks its text at word   */
            /* boundaries to its width but keeps whatever height it is         */
            /* given; it is measured by the same rules (word breaks, the       */
            /* access-key "&" taken as the label takes it) and given a pad     */
            /* of room, as the constructor's one-line height has.              */
            iLineWidth = Prf_lblFound.ClientSize.Width - Prf_lblFound.Padding.Horizontal;

            if (iLineWidth > 0)
            {   /* The line has a width (it is docked, so it has one once the   */
                /* dialog is laid out): its wrapped height.  Before then the    */
                /* constructor's one-line height stands, and the next fit,      */
                /* at the first resize, measures it.                            */
                Prf_lblFound.Height = TextRenderer.MeasureText(Prf_lblFound.Text, Prf_lblFound.Font,
                                                               new Size(iLineWidth, int.MaxValue),
                                                               TextFormatFlags.WordBreak).Height + PRF_PAD;
            }

            /* The dialog exactly as tall as its parts stacked: the found    */
            /* line, the list, the offset row, the buttons and the padding   */
            /* above and below; the width is left as it is.                  */
            this.ClientSize = new Size(this.ClientSize.Width,
                                       Prf_lblFound.Height + Prf_lstParts.Height + Prf_pnlOffset.PreferredSize.Height +
                                       Prf_pnlButtons.PreferredSize.Height + (2 * PRF_PAD));
        }

        /*--------------------------------------------------------------------*/
        /* Prf_ReadU32:                                                       */
        /*                                                                    */
        /* A little-endian 32-bit value out of a sector buffer.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     abData  : the buffer.                                          */
        /*     iOffset : where the four bytes start.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     uint : the value.                                              */
        /*--------------------------------------------------------------------*/
        private static uint Prf_ReadU32(byte[] abData, int iOffset)
        {
            /* Least significant byte first. */
            return((uint)abData[iOffset] | ((uint)abData[iOffset + 1] << 8) | ((uint)abData[iOffset + 2] << 16) |
                   ((uint)abData[iOffset + 3] << 24));
        }

        /*--------------------------------------------------------------------*/
        /* Prf_ReadU64:                                                       */
        /*                                                                    */
        /* A little-endian 64-bit value out of a sector buffer.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     abData  : the buffer.                                          */
        /*     iOffset : where the eight bytes start.                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ulong : the value.                                             */
        /*--------------------------------------------------------------------*/
        private static ulong Prf_ReadU64(byte[] abData, int iOffset)
        {
            /* Two 32-bit halves, low half first. */
            return((ulong)Prf_ReadU32(abData, iOffset) | ((ulong)Prf_ReadU32(abData, iOffset + 4) << 32));
        }

        /*--------------------------------------------------------------------*/
        /* Prf_ReadLogical:                                                   */
        /*                                                                    */
        /* Follow the chain of logical partitions inside an extended          */
        /* partition.  Each link is a sector laid out like an MBR whose first */
        /* entry is one logical partition, placed relative to THAT sector,    */
        /* and whose second entry points at the next link, placed relative to */
        /* the START OF THE EXTENDED PARTITION.  Logical partitions are       */
        /* numbered from 5, whatever the primary table holds.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pfnRead     : how to read the image or device.                 */
        /*     ullExtStart : the extended partition's first sector.           */
        /*     uiSector    : bytes per sector of what is being read.          */
        /*     lstFound    : receives each logical partition.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the chain is followed until it ends, leaves the file,   */
        /*            or reaches PRF_MAX_LOGICAL links.                       */
        /*--------------------------------------------------------------------*/
        private static void Prf_ReadLogical(PRT_Reader pfnRead, ulong ullExtStart, uint uiSector, List<PRT_clsPartition> lstFound)
        {
            PRT_clsPartition objPart;
            byte[]        abSector;
            ulong         ullLink;
            uint          uiStart;
            uint          uiCount;
            uint          uiNext;
            int           iNumber;
            int           iLinks;

            /* One sector's buffer, reused for every link; the first link   */
            /* is the extended partition's own first sector; and logical    */
            /* partitions are numbered from 5, as Linux names them.         */
            abSector = new byte[uiSector];
            ullLink  = ullExtStart;
            iNumber  = 5;

            for (iLinks = 0; iLinks < PRF_MAX_LOGICAL; iLinks++)
            {   /* One link of the chain per iteration -- at most            */
                /* PRF_MAX_LOGICAL, so a damaged chain that points back on   */
                /* itself ends instead of looping (its partitions may then   */
                /* be listed more than once).                                */
                if (!pfnRead(ullLink * uiSector, abSector) ||
                    abSector[PRF_MBR_SIGNATURE] != 0x55 || abSector[PRF_MBR_SIGNATURE + 1] != 0xAA)
                {   /* Outside the file, or not a table sector: the chain ends here. */
                    return;
                }

                /* The first entry: this link's logical partition. */
                uiStart = Prf_ReadU32(abSector, PRF_MBR_TABLE + 8);
                uiCount = Prf_ReadU32(abSector, PRF_MBR_TABLE + 12);

                if (abSector[PRF_MBR_TABLE + 4] != 0 && uiCount != 0)
                {   /* A real partition (a type and a length): listed, its    */
                    /* start counted from THIS link's sector, its type in     */
                    /* words marked "(logical)", and the next number taken.   */
                    objPart = new PRT_clsPartition();
                    objPart.PRT_sNumber   = iNumber.ToString();
                    objPart.PRT_sType     = PTB_Tables.PTB_MbrTypeText(abSector[PRF_MBR_TABLE + 4]) + " (logical)";
                    objPart.PRT_ullOffset = (ullLink + uiStart) * uiSector;
                    objPart.PRT_ullLength = (ulong)uiCount * uiSector;
                    lstFound.Add(objPart);
                    iNumber++;
                }

                /* The second entry: the next link, or zero at the end. */
                uiNext = Prf_ReadU32(abSector, PRF_MBR_TABLE + PRF_MBR_ENTRY + 8);

                if (abSector[PRF_MBR_TABLE + PRF_MBR_ENTRY + 4] == 0 || uiNext == 0)
                {   /* No further link. */
                    return;
                }

                /* The next link, counted from the START of the extended   */
                /* partition, not from this link.                          */
                ullLink = ullExtStart + uiNext;
            }

            /* PRF_MAX_LOGICAL links followed: stopped there, whatever the   */
            /* chain still says.                                             */
        }

        /*--------------------------------------------------------------------*/
        /* Prf_ReadGpt:                                                       */
        /*                                                                    */
        /* Read a GUID partition table: the header in sector 1 says where the */
        /* entries are, how many there are and how big each is; an entry      */
        /* whose type GUID is all zeros is an unused slot.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pfnRead  : how to read the image or device.                    */
        /*     uiSector : bytes per sector: the header is in sector 1 and     */
        /*                every LBA counts these.                             */
        /*     lstFound : receives each partition.                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when a GPT header was found (even an empty one).   */
        /*--------------------------------------------------------------------*/
        private static bool Prf_ReadGpt(PRT_Reader pfnRead, uint uiSector, List<PRT_clsPartition> lstFound)
        {
            PRT_clsPartition objPart;
            byte[]        abHeader;
            byte[]        abEntry;
            byte[]        abGuid;
            string        sName;
            ulong         ullEntriesLba;
            ulong         ullFirst;
            ulong         ullLast;
            uint          uiEntries;
            uint          uiEntryBytes;
            uint          i;

            /* The header's sector: LBA 1, whatever the sector size. */
            abHeader = new byte[uiSector];

            if (!pfnRead(uiSector, abHeader))
            {   /* No second sector. */
                return(false);
            }

            if (System.Text.Encoding.ASCII.GetString(abHeader, 0, 8) != "EFI PART")
            {   /* Not a GPT header: the caller lists the MBR instead. */
                return(false);
            }

            /* Where the entries are (LBA), how many, and each one's size   */
            /* (UEFI spec, the GPT header: offsets 72, 80, 84).  The        */
            /* header's CRC and the backup header are not checked: this     */
            /* reader only lists, and a row is a proposal -- the command    */
            /* that opens what is chosen checks the range itself.           */
            ullEntriesLba = Prf_ReadU64(abHeader, 72);
            uiEntries      = Prf_ReadU32(abHeader, 80);
            uiEntryBytes       = Prf_ReadU32(abHeader, 84);

            if (uiEntryBytes < 128 || uiEntryBytes > 4096)
            {   /* An entry size no GPT has (128 is the UEFI minimum; over    */
                /* 4096 would not even fit a 4K sector): a damaged header,    */
                /* and an entry buffer of that size is not made.  Still a     */
                /* GPT by its signature, so the caller lists no MBR -- with   */
                /* nothing in it.                                             */
                return(true);
            }

            if (uiEntries > PRF_MAX_GPT)
            {   /* Read no more than the cap (a damaged count could ask for   */
                /* billions of reads).                                        */
                uiEntries = PRF_MAX_GPT;
            }

            /* One entry's buffer, and one for its type GUID. */
            abEntry = new byte[uiEntryBytes];
            abGuid  = new byte[16];

            for (i = 0; i < uiEntries; i++)
            {   /* One table slot per iteration, read on its own at its   */
                /* place in the array.                                    */
                if (!pfnRead((ullEntriesLba * uiSector) + ((ulong)i * uiEntryBytes), abEntry))
                {   /* The table runs off the end of the file. */
                    break;
                }

                /* The entry's type GUID (bytes 0-15) and its first and   */
                /* last LBA (32 and 40; the last is inclusive).           */
                Array.Copy(abEntry, 0, abGuid, 0, 16);
                ullFirst = Prf_ReadU64(abEntry, 32);
                ullLast  = Prf_ReadU64(abEntry, 40);

                if (new Guid(abGuid) == Guid.Empty || ullLast < ullFirst)
                {   /* An unused slot, or a nonsensical one. */
                    continue;
                }

                /* The name: up to 36 UTF-16 characters, NUL-padded. */
                sName = System.Text.Encoding.Unicode.GetString(abEntry, 56, 72).TrimEnd('\0');

                /* The partition, numbered by its slot ("GPT 3" is the third   */
                /* slot, counting unused ones); its type in words; start and   */
                /* length in bytes from the LBAs.                              */
                objPart = new PRT_clsPartition();
                objPart.PRT_sNumber   = "GPT " + (i + 1);
                objPart.PRT_sType     = PTB_Tables.PTB_GptTypeText(new Guid(abGuid));
                objPart.PRT_ullOffset = ullFirst * uiSector;
                objPart.PRT_ullLength = (ullLast - ullFirst + 1) * uiSector;

                if (sName.Length != 0)
                {   /* The partition was given a name: show it after the type. */
                    objPart.PRT_sType = objPart.PRT_sType + "  \"" + sName + "\"";
                }

                /* Listed, in slot order. */
                lstFound.Add(objPart);
            }

            /* A GPT was read. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* PRF_ReadTable:                                                     */
        /*                                                                    */
        /* Read whatever partition table an image or device starts with,      */
        /* through the reader given.  A GPT is preferred when the MBR holds a */
        /* protective entry; otherwise the four primary entries are listed,   */
        /* and an extended one is followed for its logical partitions.  It    */
        /* runs on the command worker's thread (CommandImageOpening.cs for a  */
        /* file, CommandNativeSteps.cs for a device), so nothing here touches */
        /* a window.                                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pfnRead  : how to read the image or device.                    */
        /*     uiSector : bytes per sector the table counts in: a device's    */
        /*                own (512 or 4096), PRF_SECTOR for an image file.    */
        /*     lstFound : receives the partitions, in table order.            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : one line saying what was found, for the dialog.       */
        /*--------------------------------------------------------------------*/
        internal static string PRF_ReadTable(PRT_Reader pfnRead, uint uiSector, List<PRT_clsPartition> lstFound)
        {
            PRT_clsPartition objPart;
            byte[]        abSector;
            byte[]        abProbe;
            string        sWhole;
            bool          bGpt;
            byte          bType;
            uint          uiStart;
            uint          uiCount;
            int           iEntry;
            int           iAt;

            /* Sector 0, where an MBR (or a GPT's protective MBR) is; no   */
            /* GPT seen yet.                                               */
            abSector = new byte[uiSector];
            bGpt     = false;

            if (!pfnRead(0, abSector) ||
                abSector[PRF_MBR_SIGNATURE] != 0x55 || abSector[PRF_MBR_SIGNATURE + 1] != 0xAA)
            {   /* No boot signature: it does not start with a table (or could not be read). */
                return("No partition table was found. Type the offset the filesystem starts at (0 for the whole thing).");
            }

            /* STEP 1: is this a GPT disk?  Its MBR carries one entry of type 0xEE. */
            for (iEntry = 0; iEntry < 4; iEntry++)
            {   /* One primary entry per iteration. */
                if (abSector[PRF_MBR_TABLE + (iEntry * PRF_MBR_ENTRY) + 4] == PRF_TYPE_GPT)
                {   /* The protective entry. */
                    bGpt = true;
                }
            }

            /* STEP 1a: a filesystem over the WHOLE disk, with no table -- the    */
            /* rule the partition window reads by (PTB_Read's STEP 2a).  A FAT,   */
            /* exFAT or NTFS boot sector ends in the same 0x55 0xAA an MBR        */
            /* does, so a card formatted whole would be listed as an MBR of       */
            /* garbage entries.  With no protective entry and slots that are      */
            /* not a believable table, a filesystem found at the start means      */
            /* no table.  The disk's length is not known here, so a slot's        */
            /* end is not checked against it (ulong.MaxValue); the flag and       */
            /* start tests still tell a boot sector's code from a table.          */
            if (!bGpt && !PTB_Tables.PTB_MbrLooksReal(abSector, ulong.MaxValue))
            {   /* No believable table: is there a filesystem at the start? */
                abProbe = new byte[PTB_Tables.PTB_PROBE_BYTES];

                if (pfnRead(0, abProbe))
                {   /* The disk's first page. */
                    sWhole = PTB_Tables.PTB_ContentText(abProbe);

                    if (sWhole.Length != 0)
                    {   /* A filesystem over the whole disk: nothing to list. */
                        return("No partition table: the whole disk holds " + sWhole.Replace("  ", " ") + ". Type an offset, or 0 for the whole disk.");
                    }
                }
            }

            if (bGpt && Prf_ReadGpt(pfnRead, uiSector, lstFound))
            {   /* The GPT is the real table: what each partition holds,     */
                /* then the line for the dialog.  (A protective entry with   */
                /* no GPT header behind it falls through to the MBR, whose   */
                /* 0xEE entry is then listed as it is.)                      */
                Prf_ProbeContents(pfnRead, lstFound);
                return("GUID partition table: " + lstFound.Count + " partition(s). Choose one, or type an offset.");
            }

            /* STEP 2: an MBR table.  The four primaries, then any logical ones. */
            for (iEntry = 0; iEntry < 4; iEntry++)
            {   /* One primary entry per iteration: where it is in the      */
                /* sector, its type byte (offset 4), its first sector and   */
                /* its sector count (offsets 8 and 12).                     */
                iAt     = PRF_MBR_TABLE + (iEntry * PRF_MBR_ENTRY);
                bType   = abSector[iAt + 4];
                uiStart = Prf_ReadU32(abSector, iAt + 8);
                uiCount = Prf_ReadU32(abSector, iAt + 12);

                if (bType == 0 || uiCount == 0)
                {   /* An empty slot. */
                    continue;
                }

                /* Listed, numbered 1 to 4 by its slot, start and length   */
                /* in bytes.                                               */
                objPart = new PRT_clsPartition();
                objPart.PRT_sNumber   = (iEntry + 1).ToString();
                objPart.PRT_sType     = PTB_Tables.PTB_MbrTypeText(bType);
                objPart.PRT_ullOffset = (ulong)uiStart * uiSector;
                objPart.PRT_ullLength = (ulong)uiCount * uiSector;
                lstFound.Add(objPart);

                if (bType == PRF_TYPE_EXTENDED || bType == PRF_TYPE_EXT_LBA || bType == PRF_TYPE_EXT_LINUX)
                {   /* A container: list what is inside it too. */
                    Prf_ReadLogical(pfnRead, uiStart, uiSector, lstFound);
                }
            }

            if (lstFound.Count == 0)
            {   /* A boot signature and four empty slots: often a filesystem image with a boot sector. */
                return("The partition table is empty. Type the offset the filesystem starts at.");
            }

            /* An MBR with at least one partition: say what each holds. */
            Prf_ProbeContents(pfnRead, lstFound);
            return("MBR partition table: " + lstFound.Count + " partition(s). Choose one, or type an offset.");
        }

        /*--------------------------------------------------------------------*/
        /* Prf_ProbeContents:                                                 */
        /*                                                                    */
        /* Say what each listed partition HOLDS, from its first page.  The    */
        /* table's type says only what the partition was meant for, and a     */
        /* vendor GPT uses type GUIDs that name nothing, so the bytes are the */
        /* only honest answer.  One read of PTB_PROBE_BYTES at each           */
        /* partition's start covers every signature looked for (see           */
        /* PTB_ContentText in PartitionTable.cs, which names them).  A        */
        /* partition too short for that read, or one the reader cannot reach  */
        /* (a truncated dump), is left blank; so is one whose bytes match     */
        /* nothing.  Runs where the table was read, on the same reader -- the */
        /* command worker's thread -- so nothing here touches a window.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pfnRead  : how to read the image or device.                    */
        /*     lstFound : the partitions; each gets its sContent.             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : every partition's sContent is set ("" when unknown).    */
        /*--------------------------------------------------------------------*/
        private static void Prf_ProbeContents(PRT_Reader pfnRead, List<PRT_clsPartition> lstFound)
        {
            byte[] abStart;
            int    i;

            /* One page's buffer, reused for every partition. */
            abStart = new byte[PTB_Tables.PTB_PROBE_BYTES];

            for (i = 0; i < lstFound.Count; i++)
            {   /* One partition per iteration: blank until its bytes name   */
                /* something.                                                */
                lstFound[i].PRT_sContent = "";

                if (lstFound[i].PRT_ullLength < (ulong)PTB_Tables.PTB_PROBE_BYTES || !pfnRead(lstFound[i].PRT_ullOffset, abStart))
                {   /* Too short to hold a filesystem, or not readable. */
                    continue;
                }

                /* What the page holds, in words ("" when nothing matched). */
                lstFound[i].PRT_sContent = PTB_Tables.PTB_ContentText(abStart);
            }
        }

        /*--------------------------------------------------------------------*/
        /* PRF_ReadTableFile:                                                 */
        /*                                                                    */
        /* Read a disk IMAGE FILE's partition table, with ordinary file       */
        /* calls: the file is opened read-only and shared, PRF_ReadTable is   */
        /* run over it, and the file is closed again.  A file that cannot be  */
        /* opened at all gives an empty list and says so.                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sImageFile : the disk image's path.                            */
        /*     lstFound   : receives the partitions, in table order.          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : one line saying what was found, for the dialog.       */
        /*--------------------------------------------------------------------*/
        internal static string PRF_ReadTableFile(string sImageFile, List<PRT_clsPartition> lstFound)
        {
            string sFound;
            FileStream stmImage;
            PFR_clsFileReader objReader;

            try
            {   /* The file may be missing or locked, or a read may fail.     */
                /* (A file shorter than a sector is no failure: the reader    */
                /* answers false, and PRF_ReadTable says no table.)  Opened   */
                /* read-only and shared, so another program may go on using   */
                /* it meanwhile.                                              */
                using (stmImage = new FileStream(sImageFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {   /* Closed at this block's one exit; the reader captures it. */
                    objReader = new PFR_clsFileReader(stmImage);
                    sFound = PRF_ReadTable(objReader.PFR_Read, PRF_SECTOR, lstFound);
                }
            }
            catch (Exception exRead)
            {   /* Could not be opened or read at all: anything listed   */
                /* before the failure is dropped -- a half table would   */
                /* mislead -- and the offset is to be typed.             */
                lstFound.Clear();
                return("The file could not be read (" + exRead.Message + "). Type the offset the filesystem starts at.");
            }

            /* What the table held. */
            return(sFound);
        }

        /*--------------------------------------------------------------------*/
        /* Prf_OnRowSelected:                                                 */
        /*                                                                    */
        /* A partition was selected: put its start in the offset box, so the  */
        /* box always shows what Open will use.                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the list.                                          */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the offset box holds the selected partition's start.    */
        /*--------------------------------------------------------------------*/
        private void Prf_OnRowSelected(object objSender, EventArgs evArgs)
        {
            if (Prf_lstParts.SelectedIndices.Count != 1)
            {   /* The selection is being cleared on its way to another row. */
                return;
            }

            /* The row's partition (rows are in Prf_lstFound's order), its   */
            /* start as a plain decimal number -- what Open parses.          */
            Prf_txtOffset.Text = Prf_lstFound[Prf_lstParts.SelectedIndices[0]].PRT_ullOffset.ToString();
        }

        /*--------------------------------------------------------------------*/
        /* Prf_OnOpenClicked:                                                 */
        /*                                                                    */
        /* The Open button (or Enter, the dialog's accept key): asks for the  */
        /* offset in the box to be taken.  The handler only posts: the        */
        /* acceptance can show a refusal box, which is never shown from a     */
        /* handler.                                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : an Accept item posted to the dialog's queue.            */
        /*--------------------------------------------------------------------*/
        private void Prf_OnOpenClicked(object objSender, EventArgs evArgs)
        {
            /* The same acceptance a double-clicked row asks for; a second   */
            /* press while one waits is dropped by the queue.                */
            Prf_objQueue.WQU_Post((int)Prf_enmItem.Accept, null);
        }

        /*--------------------------------------------------------------------*/
        /* Prf_OnRowActivated:                                                */
        /*                                                                    */
        /* A partition's row was double-clicked (or Enter pressed on it):     */
        /* that is Open.  Selecting the row has already put its start in the  */
        /* offset box (Prf_OnRowSelected), so the box is what is taken.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the list.                                          */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : an Accept item posted to the dialog's queue.            */
        /*--------------------------------------------------------------------*/
        private void Prf_OnRowActivated(object objSender, EventArgs evArgs)
        {
            /* The same acceptance the Open button asks for, posted. */
            Prf_objQueue.WQU_Post((int)Prf_enmItem.Accept, null);
        }

        /*--------------------------------------------------------------------*/
        /* Prf_RunItem:                                                       */
        /*                                                                    */
        /* The dialog's queue runner: one posted item at a time, after the    */
        /* handler that posted it has returned, so a refusal box is shown     */
        /* from here and never from inside a handler.                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iItem   : the Prf_enmItem posted.                              */
        /*     objArgs : the item's argument (none for Accept).               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item's work done.                                   */
        /*--------------------------------------------------------------------*/
        private void Prf_RunItem(int iItem, object objArgs)
        {
            switch ((Prf_enmItem)iItem)
            {   /* Which work was asked for. */
                case Prf_enmItem.Accept:
                    /* The offset taken, or refused with its reason. */
                    Prf_AcceptOffset();
                    break;
                default:
                    /* No such item (a guard: only the handlers above post):   */
                    /* nothing done.                                           */
                    break;
            }
        }

        /*--------------------------------------------------------------------*/
        /* Prf_AcceptOffset:                                                  */
        /*                                                                    */
        /* Open, from the button or a row: take the offset from the box.  It  */
        /* must be a number and a whole number of 512-byte sectors, which is  */
        /* what the engine requires.  When the offset is exactly where a      */
        /* listed partition starts, that partition's LENGTH is used as well,  */
        /* so the engine is held inside the partition -- and a partition      */
        /* marked invalid is refused, with its reason; a typed offset that    */
        /* matches none runs to the end of the file.                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the outcome fields are set and the dialog closes, or a  */
        /*            message says what to correct and it stays open.         */
        /*--------------------------------------------------------------------*/
        private void Prf_AcceptOffset()
        {
            string sTyped;
            ulong  ullOffset;
            bool   bParsed;
            int    i;

            /* The text typed, without surrounding spaces and with any       */
            /* commas taken out, so a number written with comma separators   */
            /* (as the list shows them where the culture groups with         */
            /* commas) still parses.  Only commas: a culture that groups     */
            /* with "." or a space is not handled.                           */
            sTyped = Prf_txtOffset.Text.Trim().Replace(",", "");

            if (sTyped.StartsWith("0x") || sTyped.StartsWith("0X"))
            {   /* Hexadecimal. */
                bParsed = ulong.TryParse(sTyped.Substring(2), System.Globalization.NumberStyles.AllowHexSpecifier,
                                         System.Globalization.CultureInfo.InvariantCulture, out ullOffset);
            }
            else
            {   /* Decimal. */
                bParsed = ulong.TryParse(sTyped, System.Globalization.NumberStyles.None,
                                         System.Globalization.CultureInfo.InvariantCulture, out ullOffset);
            }

            if (!bParsed || (ullOffset % PRF_SECTOR) != 0)
            {   /* Not a number, or not on a 512-byte boundary: said in the   */
                /* dialog's font, the box focused for the correction, and     */
                /* the dialog stays.  (Only 512 is checked here, even for a   */
                /* device with 4096-byte sectors; what the engine's open      */
                /* does with an offset off that device's sectors is its own   */
                /* business, not this dialog's.)                              */
                MSG_clsMessageForm.MSG_Show(this, this.Font, "The offset must be a whole number of bytes that is a multiple of 512.", "Open a partition",
                                         MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
                Prf_txtOffset.Focus();
                return;
            }

            /* The offset taken, and "to the end" until a listed partition   */
            /* is found to start there.                                      */
            PRF_ullOffset = ullOffset;
            PRF_ullLength = 0;

            for (i = 0; i < Prf_lstFound.Count; i++)
            {   /* One listed partition per iteration; the first that starts    */
                /* at the offset gives its length -- the same first match the   */
                /* worker makes (FCD_SelectedTarget, CommandImageOpening.cs).   */
                if (Prf_lstFound[i].PRT_ullOffset == ullOffset)
                {   /* The offset is this partition's start. */
                    if (Prf_lstFound[i].PRT_sInvalid != null)
                    {   /* A partition marked invalid: not opened, whether it   */
                        /* was picked from the list or its start typed.  Said   */
                        /* with its reason, nothing taken (the outcome fields   */
                        /* are not accepted until the end), and the dialog      */
                        /* stays for another choice.  The worker refuses the    */
                        /* same choice again should one reach it.               */
                        MSG_clsMessageForm.MSG_Show(this, this.Font, "Partition " + Prf_lstFound[i].PRT_sNumber +
                                                 " cannot be opened: it " + Prf_lstFound[i].PRT_sInvalid + ".\n" +
                                                 "Choose another partition.", "Open a partition",
                                                 MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
                        Prf_lstParts.Focus();
                        return;
                    }

                    /* A partition that can be opened: its length too. */
                    PRF_ullLength = Prf_lstFound[i].PRT_ullLength;
                    break;
                }
            }

            /* Accepted, and closed; the caller reads the outcome fields. */
            PRF_bAccepted = true;
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Prf_OnCancelClicked:                                               */
        /*                                                                    */
        /* Cancel: close with the outcome left at "not accepted".             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the dialog closes.                                      */
        /*--------------------------------------------------------------------*/
        private void Prf_OnCancelClicked(object objSender, EventArgs evArgs)
        {
            /* Closed; PRF_bAccepted is still false from the constructor. */
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Prf_OnResize:                                                      */
        /*                                                                    */
        /* The dialog's size changed: refit.  Detached while it does so,      */
        /* because setting ClientSize raises Resize again.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the dialog.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the layout fits the new size.                           */
        /*--------------------------------------------------------------------*/
        private void Prf_OnResize(object objSender, EventArgs evArgs)
        {
            /* Unhooked, refitted, hooked again: the refit's own ClientSize   */
            /* raises Resize, which must not come back here.                  */
            this.Resize -= Prf_OnResize;
            Prf_FitLayout();
            this.Resize += Prf_OnResize;
        }
    }

    /*------------------------------------------------------------------------*/
    /* DVF_clsDeviceForm -- the first dialog of File > Open > Device (and of  */
    /* Save Device to Image File, Write Image File to Device and              */
    /* Partition/Format > Device, of Check Filesystem's device choice, and    */
    /* of New Image's Match a Device): the physical disks, for the user to    */
    /* pick one.  The list is handed in already made (the command worker has  */
    /* the engine describe each disk -- the ListDevices request -- which      */
    /* needs no Administrator).  By default only the disks a                  */
    /* card or stick appears as are shown -- removable media, or a USB, SD    */
    /* or MMC bus -- and the fixed internal disks are behind a tick box that  */
    /* is remembered between runs.  The disk Windows is running from is       */
    /* marked; it may be opened to look at, and the engine refuses to write   */
    /* it whatever is picked here.  The partition on the chosen disk is the   */
    /* next dialog's business (PRF_clsPartitionForm, above).                  */
    /*                                                                        */
    /* For the two imaging commands the dialog also asks HOW MUCH: the whole  */
    /* device (Read) or the whole image (Write) by default, or only the       */
    /* first so many bytes, sectors, KiB, MiB or GiB -- to capture just the   */
    /* front of a card, or overwrite just part of one; everything is the      */
    /* default, a shorter region the choice.  The length is rounded down to   */
    /* the chosen disk's own sector size, since a device is read and written  */
    /* in whole sectors, and checked against what there is before the dialog  */
    /* closes.  Read has one more choice, the only shorter region the         */
    /* program itself offers: for a disk that holds a GPT (DVF_SetGptEnds     */
    /* names them), a box -- unticked whenever it is shown -- to read only    */
    /* through the sector of the backup GPT header, for a table written to a  */
    /* card larger than the disk it was made for.                             */
    /*                                                                        */
    /* Derives from Form because the framework requires it: WinForms makes a  */
    /* window only as a Form subclass.  That is the one kind of inheritance   */
    /* allowed here -- from a framework base class, never from a class of     */
    /* ours.                                                                  */
    /*------------------------------------------------------------------------*/
    internal sealed class DVF_clsDeviceForm : Form
    {
        /* The dialog's sizes -- documentation table. */
        private const int DVF_WIDTH       = 640;     // client width, pixels
        private const int DVF_LIST_ROWS   = 9;       // the disk list shows this many rows without scrolling, at whatever the dialog's font is (the height is measured from the font, not fixed in pixels)
        private const int DVF_ROW_PAD     = 4;       // added to a line of text's measured height for a details-view row's margins, and again for the header's, pixels
        private const int DVF_PAD         = 12;      // padding round the content, pixels
        private const int DVF_CELL_PAD    = 16;      // added to a column's measured text width for the cell's margins, pixels

        /* What the disk is picked for, which the caller says -- the   */
        /* constructor's iRange -- documentation table.                */
        internal const int DVF_RANGE_NONE  = 0;      // the constructor's iRange: no "how much" question (Open Device, Partition Device)
        internal const int DVF_RANGE_READ  = 1;      // ... how much of the DEVICE to read into an image file
        internal const int DVF_RANGE_WRITE = 2;      // ... how much of the IMAGE to write to the device
        internal const int DVF_RANGE_SIZE  = 3;      // no "how much" row: the disk is picked only for its SIZE (New Image's Match a Device)

        /* The list's columns, which the row fill and the fitting index. */
        private static readonly string[] Dvf_arrColumns = { "Disk", "Model", "Bus", "Size", "Bytes", "Media" };   // the list's headings, left to right: Size the short form ("971.0 MiB / 1.0 GB"), Bytes the exact count -- kept in a column of its own, so the real count stays visible without crowding the short form

        /* The "how much" row's units -- documentation table; the three   */
        /* arrays are indexed alike.                                      */
        private static readonly string[] Dvf_arrRangeUnits     = { "bytes", "sectors", "KiB", "MiB", "GiB" };  // the length's unit box, as New Image's size box has them; "sectors" are the chosen disk's own
        private static readonly ulong[]  Dvf_arrUnitBytes   = { 1UL, 0UL, 1024UL, 1048576UL, 1073741824UL };   // bytes in one of each; 0 for "sectors", which is the chosen disk's sector size
        private const int                DVF_RANGE_UNIT_MIB   = 3;                                             // the unit box's starting choice
        private static readonly int[]    Dvf_arrRangeDecimals  = { 0, 0, 1, 2, 3 };                            // decimal places a converted length is rounded to in each unit, as New Image's size box

        /* The work the dialog's handlers post to its own queue, which   */
        /* Dvf_RunItem runs one at a time -- documentation table.        */
        private enum Dvf_enmItem
        {
            Accept = 1     // Open (or its other caption), Enter or a row's double click: the selected disk taken, or refused with its reason (Dvf_AcceptDisk)
        }

        /* The dialog's own queue: its handlers post, never show a box. */
        private WQU_clsWindowQueue Dvf_objQueue;   // made in the constructor before any control is hooked

        /* The outcome, valid when DVF_bAccepted. */
        internal bool     DVF_bAccepted;         // true when Open was pressed with a disk selected
        internal DEV_clsDisk DVF_objDiskChosen;  // the disk picked
        internal ulong    DVF_ullRangeBytes;     // the imaging commands only: the bytes to copy, a whole number of the disk's sectors; 0 for everything
        internal bool     DVF_bCompressXz;       // DVF_RANGE_READ only: the image is to be written compressed, as .xz
        internal bool     DVF_bThroughGpt;       // DVF_RANGE_READ only: read from the start through the backup GPT header's sector, not the whole device (DVF_ullRangeBytes is then 0)

        /* The "how much" row's state and controls -- documentation table. */
        private int             Dvf_iRange;                 // DVF_RANGE_*: what the disk is picked for, and so the wording
        private bool            Dvf_bRangeRow;              // true for DVF_RANGE_READ and _WRITE: the "how much" row is there
        private ulong           Dvf_ullImageBytes;          // DVF_RANGE_WRITE: the image's length, the most that can be written (ulong.MaxValue when a compressed image's size is not known: no limit but the device's)
        private CheckBox        Dvf_chkXz;                  // DVF_RANGE_READ only: "Compress the image as .xz"; null otherwise
        private CheckBox        Dvf_chkGpt;                 // DVF_RANGE_READ only: "Read only up to the end of the GPT, including its backup table"; shown only for a disk in Dvf_dictGptEnds; null otherwise
        private Dictionary<string, ulong> Dvf_dictGptEnds;  // DVF_RANGE_READ: each GPT disk's path to the bytes through its backup header's sector (DVF_SetGptEnds); null until given, and a disk not in it shows no GPT box
        private bool            Dvf_bGptShown;              // true while the GPT box is shown for the selected disk (a child's Visible reads false until the dialog itself is shown, so it cannot be asked)
        private ulong           Dvf_ullGptEnd;              // the selected disk's GPT end in bytes while the box is shown; 0 otherwise
        private Label           Dvf_lblRange;               // "How much of the device to read:" / "... of the image to write:"
        private FlowLayoutPanel Dvf_pnlRange;               // the row: Everything, Only the first, the number, the unit, the exact bytes
        private RadioButton     Dvf_radAll;                 // everything (the default)
        private RadioButton     Dvf_radFirst;               // only the first ...
        private TextBox         Dvf_txtRange;               // ... this many
        private ComboBox        Dvf_cmbRangeUnit;           // ... of this unit: one of Dvf_arrRangeUnits
        private int             Dvf_iRangeUnitShown;        // the unit the typed number is in now, so a unit change can convert FROM it
        private Label           Dvf_lblRangeBytes;          // what that comes to in bytes, rounded down to the selected disk's sectors
        private Label           Dvf_lblDetails;             // the selected disk's length (exact and large units), sector size and sector count, above the buttons

        /* The disks and the rest of the dialog -- documentation table. */
        private List<DEV_clsDisk>  Dvf_lstAll;   // every disk the engine described
        private Label           Dvf_lblHint;     // one line above the list
        private ListView        Dvf_lstDisks;    // the disks shown, filtered by the tick box
        private CheckBox        Dvf_chkFixed;    // "Show fixed (internal) disks"
        private FlowLayoutPanel Dvf_pnlButtons;  // Open and Cancel, right-aligned
        private Button          Dvf_btnOpen;     // accepts
        private Button          Dvf_btnCancel;   // abandons

        /*---------------------------------------------------------------------*/
        /* DVF_clsDeviceForm (constructor):                                    */
        /*                                                                     */
        /* Build the dialog round a list of disks already described, with the  */
        /* "how much" row under the list when an imaging command asks for it.  */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     fntDialog     : the program's dialog font (the caller's; not    */
        /*                     freed here).                                    */
        /*     lstDisks      : the disks, in drive-number order; may be empty. */
        /*     sTitle        : the window title, which says what the disk is   */
        /*                     for (opening, reading to an image, writing an   */
        /*                     image).                                         */
        /*     iRange        : DVF_RANGE_NONE, DVF_RANGE_READ / _WRITE for     */
        /*                     the "how much" row, or DVF_RANGE_SIZE (picked   */
        /*                     for its size; the wording says so).             */
        /*     ullImageBytes : DVF_RANGE_WRITE: the image's length (the most   */
        /*                     that can be written -- for a compressed file    */
        /*                     the image inside it, and ulong.MaxValue when    */
        /*                     that is not known); otherwise unused.           */
        /*                                                                     */
        /* Returns:                                                            */
        /*     (constructor) : the dialog, not yet shown.                      */
        /*---------------------------------------------------------------------*/
        internal DVF_clsDeviceForm(Font fntDialog, List<DEV_clsDisk> lstDisks, string sTitle, int iRange, ulong ullImageBytes)
        {
            int i;
            int iRangeHeight;

            /* Nothing chosen yet (a close any other way than Open leaves    */
            /* these); no compress box until Read makes one; the disks and   */
            /* what they are picked for kept; the "how much" row only for    */
            /* the two imaging commands.                                     */
            DVF_bAccepted     = false;
            DVF_objDiskChosen    = null;
            DVF_ullRangeBytes = 0;
            DVF_bCompressXz   = false;
            DVF_bThroughGpt   = false;
            Dvf_chkXz         = null;
            Dvf_chkGpt        = null;
            Dvf_dictGptEnds   = null;
            Dvf_bGptShown     = false;
            Dvf_ullGptEnd     = 0;
            Dvf_lstAll        = lstDisks;
            Dvf_iRange        = iRange;
            Dvf_bRangeRow     = (iRange == DVF_RANGE_READ || iRange == DVF_RANGE_WRITE);
            Dvf_ullImageBytes = ullImageBytes;

            /* The caller's dialog font, set before any control is made:    */
            /* each control takes it once parented, and the list's height   */
            /* below is measured in it.                                     */
            this.Font = fntDialog;

            /* A fixed dialog, centred on its owner, with no maximise,      */
            /* minimise or taskbar button, and DVF_PAD round its content.   */
            this.Text            = sTitle;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox     = false;
            this.MinimizeBox     = false;
            this.ShowInTaskbar   = false;
            this.StartPosition   = FormStartPosition.CenterParent;
            this.Padding         = new Padding(DVF_PAD);

            /* Cancel, sized to its text; it closes with nothing chosen. */
            Dvf_btnCancel          = new Button();
            Dvf_btnCancel.Text     = "Cancel";
            Dvf_btnCancel.AutoSize = true;
            Dvf_btnCancel.Click   += Dvf_OnCancelClicked;

            /* The dialog's own queue, made before the controls that post   */
            /* to it are hooked: Open and a row's double click post their   */
            /* acceptance there, and Dvf_RunItem runs it.                   */
            Dvf_objQueue = new WQU_clsWindowQueue(this, Dvf_RunItem);

            /* Open, likewise (renamed below for the commands that do not   */
            /* open); it asks for the choice to be checked and taken.       */
            Dvf_btnOpen          = new Button();
            Dvf_btnOpen.Text     = "Open";
            Dvf_btnOpen.AutoSize = true;
            Dvf_btnOpen.Click   += Dvf_OnOpenClicked;

            /* The button row along the bottom, a pad above it, flowing   */
            /* right to left -- Cancel, added first, at the right edge.   */
            Dvf_pnlButtons               = new FlowLayoutPanel();
            Dvf_pnlButtons.Dock          = DockStyle.Bottom;
            Dvf_pnlButtons.FlowDirection = FlowDirection.RightToLeft;
            Dvf_pnlButtons.AutoSize      = true;
            Dvf_pnlButtons.Padding       = new Padding(0, DVF_PAD, 0, 0);
            Dvf_pnlButtons.Controls.Add(Dvf_btnCancel);
            Dvf_pnlButtons.Controls.Add(Dvf_btnOpen);

            /* The tick box for the internal disks, remembered between runs   */
            /* (SET_OPT_SHOW_FIXED; absent means off, so the fixed disks      */
            /* are hidden at first).  Changing it refills the list.           */
            Dvf_chkFixed                 = new CheckBox();
            Dvf_chkFixed.Text            = "Show &fixed (internal) disks too";
            Dvf_chkFixed.AutoSize        = true;
            Dvf_chkFixed.Dock            = DockStyle.Top;
            Dvf_chkFixed.Padding         = new Padding(0, DVF_PAD / 2, 0, 0);
            Dvf_chkFixed.Checked         = (SET_Settings.SET_ReadInt(SET_Settings.SET_OPT_SHOW_FIXED, 0) != 0);
            Dvf_chkFixed.CheckedChanged += Dvf_OnFixedChanged;

            /* The list of disks: details view, whole rows, one at a time,   */
            /* the selection still shown without the focus; as tall as       */
            /* DVF_LIST_ROWS rows in the dialog's font.                      */
            Dvf_lstDisks               = new ListView();
            Dvf_lstDisks.Dock          = DockStyle.Top;
            Dvf_lstDisks.Height        = DVF_ListHeightForRows(this.Font, DVF_LIST_ROWS);
            Dvf_lstDisks.View          = View.Details;
            Dvf_lstDisks.FullRowSelect = true;
            Dvf_lstDisks.MultiSelect   = false;
            Dvf_lstDisks.HideSelection = false;

            for (i = 0; i < Dvf_arrColumns.Length; i++)
            {   /* One column heading per iteration. */
                Dvf_lstDisks.Columns.Add(Dvf_arrColumns[i]);
            }

            /* Size and Bytes right-aligned, so the digits line up; a row   */
            /* double-clicked (or Enter on it) is Open: the list's own      */
            /* handler calls what the button's does (Dvf_AcceptDisk).       */
            Dvf_lstDisks.Columns[3].TextAlign = HorizontalAlignment.Right;
            Dvf_lstDisks.Columns[4].TextAlign = HorizontalAlignment.Right;
            Dvf_lstDisks.ItemActivate        += Dvf_OnDiskActivated;

            /* The hint above the list, in Open Device's words until the   */
            /* test below gives another command its own.                   */
            Dvf_lblHint           = new Label();
            Dvf_lblHint.Dock      = DockStyle.Top;
            Dvf_lblHint.AutoSize  = false;
            Dvf_lblHint.TextAlign = ContentAlignment.MiddleLeft;
            Dvf_lblHint.Text      = "Choose the disk to open. A disk marked (Windows) can be looked at but never written.";

            /* The imaging commands' own words: the dialog is not opening a     */
            /* disk there, and its button leads on to the next step (the file   */
            /* to save, or the warning), hence the ellipsis.                    */
            if (Dvf_iRange == DVF_RANGE_READ)
            {   /* Save Device to Image File. */
                Dvf_lblHint.Text = "Choose the disk to save to an image file.";
                Dvf_btnOpen.Text = "&Save...";
            }
            else if (Dvf_iRange == DVF_RANGE_WRITE)
            {   /* Write Image File to Device. */
                Dvf_lblHint.Text = "Choose the disk to write the image to. A disk marked (Windows) is never written.";
                Dvf_btnOpen.Text = "&Write...";
            }
            else if (Dvf_iRange == DVF_RANGE_SIZE)
            {   /* New Image's Match a Device: nothing is opened or written, only the size is read. */
                Dvf_lblHint.Text = "Choose the disk whose size the new image should have. Nothing on it is read or written.";
                Dvf_btnOpen.Text = "&Use Its Size";
            }

            /* The selected disk's details, just above the buttons: its size   */
            /* exactly and in large units, its sector (block) size and how     */
            /* many sectors it has -- what a length typed in sectors, or       */
            /* checked against the card, is worked out from, so the "how       */
            /* much" row's sectors can be typed knowingly.  Two lines high     */
            /* (set below, once parented).                                     */
            Dvf_lblDetails           = new Label();
            Dvf_lblDetails.Dock      = DockStyle.Bottom;
            Dvf_lblDetails.AutoSize  = false;
            Dvf_lblDetails.TextAlign = ContentAlignment.BottomLeft;

            /* Read Device's GPT choice, the one exception to reading the     */
            /* whole device: through the backup GPT header's sector only.     */
            /* Added before the compress box, so it docks below it; hidden    */
            /* until DVF_SetGptEnds says the selected disk holds a GPT, and   */
            /* unticked whenever it is shown.  Ticking it greys the "how      */
            /* much" row (Dvf_UpdateRange).                                   */
            if (Dvf_iRange == DVF_RANGE_READ)
            {   /* Save Device to Image File only. */
                Dvf_chkGpt                 = new CheckBox();
                Dvf_chkGpt.Text            = "Read only up to the end of the &GPT, including its backup table";
                Dvf_chkGpt.AutoSize        = true;
                Dvf_chkGpt.Dock            = DockStyle.Top;
                Dvf_chkGpt.Padding         = new Padding(0, DVF_PAD / 2, 0, 0);
                Dvf_chkGpt.Checked         = false;
                Dvf_chkGpt.Visible         = false;
                Dvf_chkGpt.CheckedChanged += Dvf_OnGptChanged;
                this.Controls.Add(Dvf_chkGpt);
            }

            /* Read Device's choice to compress, under the "how much" row:   */
            /* added first so it docks below it.  It starts as it was last   */
            /* left.                                                         */
            if (Dvf_iRange == DVF_RANGE_READ)
            {   /* Save Device to Image File only. */
                Dvf_chkXz          = new CheckBox();
                Dvf_chkXz.Text     = "&Compress the image as .xz (much smaller; slower to read)";
                Dvf_chkXz.AutoSize = true;
                Dvf_chkXz.Dock     = DockStyle.Top;
                Dvf_chkXz.Padding  = new Padding(0, DVF_PAD / 2, 0, 0);
                Dvf_chkXz.Checked  = (SET_Settings.SET_ReadInt(SET_Settings.SET_OPT_READ_XZ, 0) != 0);
                this.Controls.Add(Dvf_chkXz);
            }

            /* The "how much" row, for the imaging commands only. */
            if (Dvf_bRangeRow)
            {   /* Read or Write: build it (it is docked below the tick box). */
                Dvf_BuildRange();
                this.Controls.Add(Dvf_pnlRange);
                this.Controls.Add(Dvf_lblRange);
            }

            /* Docked top-down: the hint, the list, the tick box (then the      */
            /* "how much" row, added above); at the bottom the buttons and      */
            /* the details line above them.  Docking runs in reverse order of   */
            /* adding.                                                          */
            this.Controls.Add(Dvf_chkFixed);
            this.Controls.Add(Dvf_lstDisks);
            this.Controls.Add(Dvf_lblHint);
            this.Controls.Add(Dvf_lblDetails);
            this.Controls.Add(Dvf_pnlButtons);

            /* The hint's height is measured only now, once it has its parent   */
            /* and so the dialog's font; before, it would be the default's.     */
            /* The details line holds two lines of text.                        */
            Dvf_lblHint.Height    = Dvf_lblHint.PreferredHeight + DVF_PAD;
            Dvf_lblDetails.Height = TextRenderer.MeasureText("Ag\nAg", this.Font).Height + DVF_PAD;

            /* The same for the "how much" row's two parts, when there. */
            iRangeHeight = 0;

            if (Dvf_bRangeRow)
            {   /* Measured in the dialog's font, now that they are parented. */
                Dvf_lblRange.Height = Dvf_lblRange.PreferredHeight + DVF_PAD;
                Dvf_pnlRange.Height = Dvf_pnlRange.PreferredSize.Height;
                iRangeHeight        = Dvf_lblRange.Height + Dvf_pnlRange.Height;
            }

            if (Dvf_chkXz != null)
            {   /* The compress box below it, measured the same way. */
                Dvf_chkXz.Height = Dvf_chkXz.PreferredSize.Height;
                iRangeHeight    += Dvf_chkXz.Height;
            }

            if (Dvf_chkGpt != null)
            {   /* The GPT box, measured the same way.  It is hidden now and    */
                /* so not in the height below; Dvf_UpdateGpt adds its height    */
                /* when it is shown and takes it off when it is hidden again.   */
                Dvf_chkGpt.Height = Dvf_chkGpt.PreferredSize.Height;
            }

            /* The rows the tick box allows, the first selected. */
            Dvf_FillList();

            /* The dialog as tall as its parts stacked, at DVF_WIDTH; Enter    */
            /* is Open and Escape Cancel; refitted on every resize; then the   */
            /* columns sized to their content and the width widened if the     */
            /* columns or the "how much" row need more.                        */
            this.ClientSize   = new Size(DVF_WIDTH, Dvf_lblHint.Height + Dvf_lstDisks.Height + Dvf_chkFixed.Height + iRangeHeight +
                                                    Dvf_lblDetails.Height + Dvf_pnlButtons.Height + (2 * DVF_PAD));
            this.AcceptButton = Dvf_btnOpen;
            this.CancelButton = Dvf_btnCancel;
            this.Resize      += Dvf_OnResize;
            Dvf_FitColumns();
            Dvf_FitWidth();

            /* The details line, and the "how much" row's exact count, follow   */
            /* the selected disk; both start right for the first selection.     */
            Dvf_lstDisks.SelectedIndexChanged += Dvf_OnDiskSelected;
            Dvf_UpdateDetails();

            if (Dvf_bRangeRow)
            {   /* The exact-bytes field starts right too. */
                Dvf_UpdateRange();
            }
        }

        /*--------------------------------------------------------------------*/
        /* Dvf_UpdateDetails:                                                 */
        /*                                                                    */
        /* Fill the details line for the selected disk: on the first line its */
        /* length in the program's one written form (DVF_SizeText: binary,    */
        /* decimal, exact bytes); on the second its sector size -- the block  */
        /* a device is read and written in, and the "sectors" of the length   */
        /* box -- and how many whole sectors it holds.  With nothing          */
        /* selected it says so.                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the line describes the selection.                       */
        /*--------------------------------------------------------------------*/
        private void Dvf_UpdateDetails()
        {
            DEV_clsDisk objDisk;
            ulong    ullSector;

            if (Dvf_lstDisks.SelectedItems.Count == 0)
            {   /* Nothing to describe. */
                Dvf_lblDetails.Text = "No disk selected.";
                return;
            }

            /* The selected row's disk (each row's Tag is its DEV_clsDisk) and   */
            /* the sector size the engine reported for it.                       */
            objDisk = (DEV_clsDisk)Dvf_lstDisks.SelectedItems[0].Tag;
            ullSector = objDisk.DEV_stInfo.DVI_uiSectorBytes;

            if (ullSector == 0)
            {   /* A disk that reported no sector size: the usual 512, as   */
                /* the length box assumes -- and so no division by zero.    */
                ullSector = 512;
            }

            /* Named as the list names it, then its size in the one written   */
            /* form; on the second line the sector size and how many WHOLE    */
            /* sectors (a part sector at the end is not counted).             */
            Dvf_lblDetails.Text = "PhysicalDrive" + objDisk.DEV_iNumber + ":  " + DVF_SizeText(objDisk.DEV_stInfo.DVI_ullSize) + "\n" +
                                  "Sector (block) size " + ullSector.ToString("N0") + " bytes  x  " +
                                  (objDisk.DEV_stInfo.DVI_ullSize / ullSector).ToString("N0") + " sectors";
        }

        /*--------------------------------------------------------------------*/
        /* Dvf_OnDiskSelected:                                                */
        /*                                                                    */
        /* The selected disk changed: the details line, and (for an imaging   */
        /* command) the exact count, which depends on its sector size.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the list.                                          */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : both follow the selection.                              */
        /*--------------------------------------------------------------------*/
        private void Dvf_OnDiskSelected(object objSender, EventArgs evArgs)
        {
            /* The details line first, always; then the GPT box (Read only),    */
            /* shown or hidden for the new disk and unticked either way, so a   */
            /* choice made for one disk never carries over to another.          */
            Dvf_UpdateDetails();
            Dvf_UpdateGpt();

            if (Dvf_bRangeRow)
            {   /* The "how much" row is there. */
                Dvf_UpdateRange();
            }
        }

        /*--------------------------------------------------------------------*/
        /* DVF_SetGptEnds:                                                    */
        /*                                                                    */
        /* Give Read Device's picker the disks that hold a GPT, each with the */
        /* bytes from its start through its backup header's sector, as the    */
        /* worker read them before the dialog was built.  Called once, before */
        /* the dialog is shown; the GPT box follows the selection from then   */
        /* on.  The other pickers never call it, and a disk not in the        */
        /* dictionary shows no GPT box.                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     dictGptEnds : device path to GPT end in bytes; the caller's,   */
        /*                   only read here.                                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the GPT box is right for the selected disk.             */
        /*--------------------------------------------------------------------*/
        internal void DVF_SetGptEnds(Dictionary<string, ulong> dictGptEnds)
        {
            /* Kept, then applied to the disk already selected. */
            Dvf_dictGptEnds = dictGptEnds;
            Dvf_UpdateGpt();
        }

        /*--------------------------------------------------------------------*/
        /* Dvf_UpdateGpt:                                                     */
        /*                                                                    */
        /* Show the GPT box for the selected disk when it holds a GPT, with   */
        /* the size the read would come to -- or "the whole device" when the  */
        /* backup header is in the disk's last sector, as on a disk whose     */
        /* table was made for it -- and hide it otherwise.  It is unticked    */
        /* every time, which is what keeps it off by default.  The dialog     */
        /* grows by the box's height when it appears and shrinks when it      */
        /* goes, and is widened if the box's line needs more room.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the box, and the dialog's size, fit the selection.      */
        /*--------------------------------------------------------------------*/
        private void Dvf_UpdateGpt()
        {
            DEV_clsDisk objDisk;
            ulong       ullEnd;
            bool        bShow;

            if (Dvf_chkGpt == null)
            {   /* Not Read Device: there is no GPT box. */
                return;
            }

            /* Shown only for a selected disk the dictionary names. */
            bShow  = false;
            ullEnd = 0;
            objDisk = null;
            if (Dvf_dictGptEnds != null && Dvf_lstDisks.SelectedItems.Count != 0)
            {   /* A disk is selected and the GPT facts have been given. */
                objDisk = (DEV_clsDisk)Dvf_lstDisks.SelectedItems[0].Tag;
                bShow   = Dvf_dictGptEnds.TryGetValue(objDisk.DEV_sPath, out ullEnd);
            }

            /* Off, whatever the box showed before -- the one exception to     */
            /* reading everything is never carried from one disk to another.   */
            Dvf_chkGpt.Checked = false;

            if (bShow)
            {   /* The box's words name the size the read would come to. */
                if (ullEnd == objDisk.DEV_stInfo.DVI_ullSize)
                {   /* The backup is in the last sector: the same as reading everything. */
                    Dvf_chkGpt.Text = "Read only up to the end of the &GPT, including its backup table (the whole device)";
                }
                else
                {   /* Shorter, or past the end (refused at Read). */
                    Dvf_chkGpt.Text = "Read only up to the end of the &GPT, including its backup table (" + DVF_SizeShortText(ullEnd) + ")";
                }
            }

            if (bShow != Dvf_bGptShown)
            {   /* The box appears or goes: the dialog grows or shrinks by its   */
                /* height, the other parts staying as they are.                  */
                if (bShow)
                {   /* Appearing. */
                    this.ClientSize = new Size(this.ClientSize.Width, this.ClientSize.Height + Dvf_chkGpt.Height);
                }
                else
                {   /* Going. */
                    this.ClientSize = new Size(this.ClientSize.Width, this.ClientSize.Height - Dvf_chkGpt.Height);
                }
            }

            /* The box's state recorded, its visibility set, and the dialog   */
            /* widened (never narrowed) if the box's line needs it.           */
            Dvf_bGptShown      = bShow;
            Dvf_ullGptEnd      = ullEnd;
            Dvf_chkGpt.Visible = bShow;
            Dvf_FitWidth();
        }

        /*--------------------------------------------------------------------*/
        /* Dvf_OnGptChanged:                                                  */
        /*                                                                    */
        /* The GPT box was ticked or unticked: the "how much" row is greyed   */
        /* while it is ticked, since the box decides the length then.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the GPT box.                                       */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the row agrees with the box.                            */
        /*--------------------------------------------------------------------*/
        private void Dvf_OnGptChanged(object objSender, EventArgs evArgs)
        {
            /* The row worked out again from the controls as they stand. */
            Dvf_UpdateRange();
        }

        /*--------------------------------------------------------------------*/
        /* DVF_ListHeightForRows:                                             */
        /*                                                                    */
        /* The height a details-view list needs to show a number of rows      */
        /* without scrolling, in a font: the header and each row are a line   */
        /* of that font's text with their margins, plus the list's 3-D border */
        /* top and bottom.  Both pickers size their lists with it (this one   */
        /* and PRF_clsPartitionForm), so a larger dialog font gives a taller  */
        /* list instead of fewer visible rows -- it is ONE calculation for    */
        /* the two, which is why it is shared rather than copied.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     fntDialog : the font the list will be drawn in (the dialog's). */
        /*     iRows : the rows to show.                                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : the list's height, pixels.                               */
        /*--------------------------------------------------------------------*/
        internal static int DVF_ListHeightForRows(Font fntDialog, int iRows)
        {
            int iRow;

            /* One row: a line of text in the font -- "Ag" has a capital's   */
            /* height and a descender -- and the row's margin.               */
            iRow = TextRenderer.MeasureText("Ag", fntDialog).Height + DVF_ROW_PAD;

            /* The header (a row and a little more), the rows, the border. */
            return((iRow + DVF_ROW_PAD) + (iRows * iRow) + (2 * SystemInformation.Border3DSize.Height));
        }

        /*--------------------------------------------------------------------*/
        /* Dvf_BuildRange:                                                    */
        /*                                                                    */
        /* Make the "how much" question: its label, and one row holding       */
        /* "Everything" (ticked), "Only the first", the number, its unit and  */
        /* the exact byte count it comes to.  The number and unit are greyed  */
        /* until "Only the first" is ticked.  Widths are measured in the      */
        /* DIALOG's font: these controls have no parent yet, so their own     */
        /* Font would still be the small default.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the label and the row exist, not yet parented.          */
        /*--------------------------------------------------------------------*/
        private void Dvf_BuildRange()
        {
            /* The question, worded for which way the copy goes. */
            Dvf_lblRange           = new Label();
            Dvf_lblRange.Dock      = DockStyle.Top;
            Dvf_lblRange.AutoSize  = false;
            Dvf_lblRange.TextAlign = ContentAlignment.BottomLeft;

            if (Dvf_iRange == DVF_RANGE_READ)
            {   /* Read Device: a front part of the device. */
                Dvf_lblRange.Text = "How much of the device to read into the image file:";
            }
            else
            {   /* Write Image: a front part of the image. */
                Dvf_lblRange.Text = "How much of the image file to write to the device:";
            }

            /* Everything, the default.  A change of either choice brings     */
            /* the row up to date (both report it -- each through a handler   */
            /* of its own -- and the update is cheap).                        */
            Dvf_radAll                 = new RadioButton();
            Dvf_radAll.Text            = "&Everything";
            Dvf_radAll.AutoSize        = true;
            Dvf_radAll.Checked         = true;
            Dvf_radAll.CheckedChanged += Dvf_OnAllChanged;

            /* Only the first ..., the other choice: the two share their      */
            /* parent row, so ticking one unticks the other (radio buttons,   */
            /* as found).                                                     */
            Dvf_radFirst                 = new RadioButton();
            Dvf_radFirst.Text            = "Only the f&irst";
            Dvf_radFirst.AutoSize        = true;
            Dvf_radFirst.CheckedChanged += Dvf_OnFirstChanged;

            /* The number, typed (a fraction is allowed: 0.5 GiB), right-    */
            /* aligned, wide enough for a seventeen-character count with     */
            /* separators, starting at 0; every keystroke updates the row.   */
            Dvf_txtRange              = new TextBox();
            Dvf_txtRange.Width        = TextRenderer.MeasureText("8,888,888,888,888", this.Font).Width + DVF_PAD;
            Dvf_txtRange.TextAlign    = HorizontalAlignment.Right;
            Dvf_txtRange.Text         = "0";
            Dvf_txtRange.TextChanged += Dvf_OnRangeTextChanged;

            /* Its unit: a fixed list (no typing), starting at MiB, wide     */
            /* enough for "sectors" and the drop-down arrow.  The unit now   */
            /* shown is remembered, so a change can convert the number       */
            /* FROM it (Dvf_OnRangeUnitChanged).                             */
            Dvf_cmbRangeUnit               = new ComboBox();
            Dvf_cmbRangeUnit.DropDownStyle = ComboBoxStyle.DropDownList;
            Dvf_cmbRangeUnit.Items.AddRange(Dvf_arrRangeUnits);
            Dvf_cmbRangeUnit.SelectedIndex = DVF_RANGE_UNIT_MIB;
            Dvf_cmbRangeUnit.Width         = TextRenderer.MeasureText("sectors", this.Font).Width + SystemInformation.VerticalScrollBarWidth + DVF_PAD;
            Dvf_iRangeUnitShown            = DVF_RANGE_UNIT_MIB;
            Dvf_cmbRangeUnit.SelectedIndexChanged += Dvf_OnRangeUnitChanged;

            /* What it comes to, exactly: sized to its text, with a third     */
            /* of a pad above it, which lowers it beside the box (the exact   */
            /* aim is not recorded).                                          */
            Dvf_lblRangeBytes           = new Label();
            Dvf_lblRangeBytes.AutoSize  = true;
            Dvf_lblRangeBytes.Padding   = new Padding(0, DVF_PAD / 3, 0, 0);

            /* The row, left to right, one line, sized to its content. */
            Dvf_pnlRange               = new FlowLayoutPanel();
            Dvf_pnlRange.Dock          = DockStyle.Top;
            Dvf_pnlRange.FlowDirection = FlowDirection.LeftToRight;
            Dvf_pnlRange.WrapContents  = false;
            Dvf_pnlRange.AutoSize      = true;
            Dvf_pnlRange.Controls.Add(Dvf_radAll);
            Dvf_pnlRange.Controls.Add(Dvf_radFirst);
            Dvf_pnlRange.Controls.Add(Dvf_txtRange);
            Dvf_pnlRange.Controls.Add(Dvf_cmbRangeUnit);
            Dvf_pnlRange.Controls.Add(Dvf_lblRangeBytes);
        }

        /*--------------------------------------------------------------------*/
        /* Dvf_RangeBytes:                                                    */
        /*                                                                    */
        /* What the number and unit come to in bytes for a disk, rounded DOWN */
        /* to a whole number of that disk's sectors (a device is read and     */
        /* written in whole sectors; a sector is also the "sectors" unit).    */
        /* The arithmetic is in decimal, so 0.8 GiB is exact before it is     */
        /* rounded, and a number too large is refused before it is            */
        /* multiplied.                                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objDisk : the disk the length is for (its sector size).        */
        /*     ullBytes : receives the byte count (0 on false).               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the text is a number, not negative, small     */
        /*            enough to count in bytes; false otherwise.              */
        /*--------------------------------------------------------------------*/
        private bool Dvf_RangeBytes(DEV_clsDisk objDisk, out ulong ullBytes)
        {
            decimal decNumber;
            decimal decBytes;
            ulong   ullSector;
            ulong   ullUnit;
            int     iUnit;

            /* No length until one is worked out, and the disk's sector    */
            /* size, which both the "sectors" unit and the rounding use.   */
            ullBytes  = 0;
            ullSector = objDisk.DEV_stInfo.DVI_uiSectorBytes;

            if (ullSector == 0)
            {   /* A disk that reported no sector size: the usual 512. */
                ullSector = 512;
            }

            /* The number in the user's own culture (its decimal mark and   */
            /* separators), as Dvf_OnRangeUnitChanged writes it back.       */
            if (!decimal.TryParse(Dvf_txtRange.Text, System.Globalization.NumberStyles.Number,
                                  System.Globalization.CultureInfo.CurrentCulture, out decNumber) || decNumber < 0)
            {   /* Not a number (or half-typed): no length. */
                return(false);
            }

            /* The unit's size; "sectors" are the disk's own. */
            iUnit = Dvf_cmbRangeUnit.SelectedIndex;

            if (iUnit < 0)
            {   /* No unit chosen (cannot normally happen): treat as bytes. */
                iUnit = 0;
            }

            /* The unit in bytes (0 in the table stands for "sectors"). */
            ullUnit = Dvf_arrUnitBytes[iUnit];

            if (ullUnit == 0)
            {   /* "sectors": this disk's own. */
                ullUnit = ullSector;
            }

            /* Too large to count at all, tested BEFORE the multiply: the       */
            /* number against the most bytes a length can hold divided by the   */
            /* unit (never 0 -- a sector is at least 512).  Multiplied first,   */
            /* a number long enough (20 digits in GiB, 23 in MiB) would pass    */
            /* decimal's own range and throw out of the TextChanged handler.    */
            if (decNumber > (decimal)ulong.MaxValue / (decimal)ullUnit)
            {   /* More bytes than a length can hold: no length. */
                return(false);
            }

            /* The length in bytes, still in decimal, now known to fit. */
            decBytes = decNumber * (decimal)ullUnit;

            /* Rounded DOWN to whole sectors: divided, floored, multiplied   */
            /* back -- so a length never asks for part of a sector.          */
            ullBytes = ((ulong)decimal.Floor(decBytes / (decimal)ullSector)) * ullSector;

            /* A length in whole sectors of this disk. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* Dvf_UpdateRange:                                                   */
        /*                                                                    */
        /* Bring the "how much" row up to date: all of it greyed while Read   */
        /* Device's GPT box is ticked, the number and unit usable only with   */
        /* "Only the first" ticked, and the exact-bytes label                 */
        /* saying what the length comes to for the selected disk (or that a   */
        /* disk must be chosen, or that the text is not a number).            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the row agrees with the choices.                        */
        /*--------------------------------------------------------------------*/
        private void Dvf_UpdateRange()
        {
            ulong ullBytes;
            bool  bGpt;

            /* Read Device's GPT box, ticked, decides the length itself: the   */
            /* whole row is greyed then, and says nothing.                     */
            bGpt = (Dvf_chkGpt != null && Dvf_chkGpt.Checked);
            Dvf_radAll.Enabled   = !bGpt;
            Dvf_radFirst.Enabled = !bGpt;

            /* The number and its unit only mean something with "Only the   */
            /* first" ticked (and the GPT box not): greyed otherwise.       */
            Dvf_txtRange.Enabled     = Dvf_radFirst.Checked && !bGpt;
            Dvf_cmbRangeUnit.Enabled = Dvf_radFirst.Checked && !bGpt;

            if (bGpt)
            {   /* The GPT box's length applies: nothing to work out here. */
                Dvf_lblRangeBytes.Text = "";
                return;
            }

            if (!Dvf_radFirst.Checked)
            {   /* Everything: nothing to work out. */
                Dvf_lblRangeBytes.Text = "";
                return;
            }

            if (Dvf_lstDisks.SelectedItems.Count == 0)
            {   /* The sector size, and so the exact count, depends on the disk. */
                Dvf_lblRangeBytes.Text = "(choose a disk)";
                return;
            }

            if (!Dvf_RangeBytes((DEV_clsDisk)Dvf_lstDisks.SelectedItems[0].Tag, out ullBytes))
            {   /* Not something that can be counted. */
                Dvf_lblRangeBytes.Text = "(not a number)";
                return;
            }

            /* The exact count, as the copy will use it. */
            Dvf_lblRangeBytes.Text = "= " + ullBytes.ToString("N0") + " bytes";
        }

        /*--------------------------------------------------------------------*/
        /* Dvf_OnAllChanged:                                                  */
        /*                                                                    */
        /* "Everything" was ticked or unticked: bring the "how much" row up   */
        /* to date.  Ticking one of the pair unticks the other, so a choice   */
        /* raises this and Dvf_OnFirstChanged both; the update is cheap and   */
        /* reads the controls as they stand, so running twice does no harm.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the "Everything" choice.                           */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the row agrees with the choices.                        */
        /*--------------------------------------------------------------------*/
        private void Dvf_OnAllChanged(object objSender, EventArgs evArgs)
        {
            /* The whole row worked out again from the controls. */
            Dvf_UpdateRange();
        }

        /*--------------------------------------------------------------------*/
        /* Dvf_OnFirstChanged:                                                */
        /*                                                                    */
        /* "Only the first" was ticked or unticked: bring the "how much" row  */
        /* up to date -- the number and its unit become usable, or are        */
        /* greyed, with it.                                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the "Only the first" choice.                       */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the row agrees with the choices.                        */
        /*--------------------------------------------------------------------*/
        private void Dvf_OnFirstChanged(object objSender, EventArgs evArgs)
        {
            /* The whole row worked out again from the controls. */
            Dvf_UpdateRange();
        }

        /*--------------------------------------------------------------------*/
        /* Dvf_OnRangeTextChanged:                                            */
        /*                                                                    */
        /* The length's number changed -- a keystroke, or the unit change     */
        /* rewriting it (Dvf_OnRangeUnitChanged): the exact-bytes label       */
        /* beside it follows.                                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the number box.                                    */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the row agrees with the typed number.                   */
        /*--------------------------------------------------------------------*/
        private void Dvf_OnRangeTextChanged(object objSender, EventArgs evArgs)
        {
            /* The whole row worked out again from the controls, the exact   */
            /* count from the text as typed so far.                          */
            Dvf_UpdateRange();
        }

        /*--------------------------------------------------------------------*/
        /* Dvf_OnRangeUnitChanged:                                            */
        /*                                                                    */
        /* The length's unit changed: the typed number is CONVERTED to it --  */
        /* the same length counted in the new unit and rounded to that unit's */
        /* places, rather than left to mean a different length -- then the    */
        /* row is brought up to date.  A number that does not parse is left   */
        /* as it is.                                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the unit box.                                      */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the number is in the new unit and the row is current.   */
        /*--------------------------------------------------------------------*/
        private void Dvf_OnRangeUnitChanged(object objSender, EventArgs evArgs)
        {
            decimal decNumber;
            decimal decBytes;
            ulong   ullSector;
            ulong   ullOldUnit;
            ulong   ullNewUnit;
            int     iUnit;
            int     iOld;

            /* The unit chosen now. */
            iUnit = Dvf_cmbRangeUnit.SelectedIndex;

            if (iUnit < 0)
            {   /* Nothing chosen (cannot normally happen). */
                return;
            }

            /* The unit the number was in, and the new one recorded as       */
            /* shown; "sectors" are the selected disk's own, 512 with none   */
            /* selected (or none reported).                                  */
            iOld                = Dvf_iRangeUnitShown;
            Dvf_iRangeUnitShown = iUnit;
            ullSector           = 512;

            if (Dvf_lstDisks.SelectedItems.Count != 0 && ((DEV_clsDisk)Dvf_lstDisks.SelectedItems[0].Tag).DEV_stInfo.DVI_uiSectorBytes != 0)
            {   /* The selected disk's own sector. */
                ullSector = ((DEV_clsDisk)Dvf_lstDisks.SelectedItems[0].Tag).DEV_stInfo.DVI_uiSectorBytes;
            }

            /* Each unit's bytes; 0 in the table means "sectors". */
            ullOldUnit = Dvf_arrUnitBytes[iOld];
            ullNewUnit = Dvf_arrUnitBytes[iUnit];

            if (ullOldUnit == 0)
            {   /* From sectors. */
                ullOldUnit = ullSector;
            }

            if (ullNewUnit == 0)
            {   /* To sectors. */
                ullNewUnit = ullSector;
            }

            if (decimal.TryParse(Dvf_txtRange.Text, System.Globalization.NumberStyles.Number,
                                 System.Globalization.CultureInfo.CurrentCulture, out decNumber) && decNumber >= 0 &&
                decNumber <= (decimal)ulong.MaxValue / (decimal)ullOldUnit)
            {   /* A number a length can hold (one larger is left as typed,     */
                /* tested before the multiply, which it would overflow -- the   */
                /* row then shows no length, as Dvf_RangeBytes refuses it):     */
                /* the same length in the new unit, rounded to                  */
                /* the unit's places (which can round UP -- the Open check      */
                /* catches a length that then exceeds the disk), written        */
                /* raw in the user's culture, with no thousands separators:     */
                /* the exact-bytes label beside it has those.  Setting the      */
                /* text raises TextChanged, which updates the row once more.    */
                decBytes          = decNumber * (decimal)ullOldUnit;
                Dvf_txtRange.Text = Math.Round(decBytes / (decimal)ullNewUnit, Dvf_arrRangeDecimals[iUnit]).ToString(System.Globalization.CultureInfo.CurrentCulture);
            }

            /* The row brought up to date for the new unit (also when the   */
            /* number did not parse and so was not rewritten).              */
            Dvf_UpdateRange();
        }

        /*--------------------------------------------------------------------*/
        /* Dvf_MeasureColumn:                                                 */
        /*                                                                    */
        /* The width one column needs: the widest of its heading and its      */
        /* cells, measured in the list's font, plus the cell margins.  Shared */
        /* by Dvf_FitColumns, which sets the widths, and Dvf_FitWidth, which  */
        /* sizes the dialog to their sum.                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iColumn : the column.                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : its width, pixels.                                       */
        /*--------------------------------------------------------------------*/
        private int Dvf_MeasureColumn(int iColumn)
        {
            int iRow;
            int iWidest;

            /* The heading first: a column is never narrower than it. */
            iWidest = TextRenderer.MeasureText(Dvf_lstDisks.Columns[iColumn].Text, Dvf_lstDisks.Font).Width;

            for (iRow = 0; iRow < Dvf_lstDisks.Items.Count; iRow++)
            {   /* One row's cell per iteration. */
                iWidest = Math.Max(iWidest, TextRenderer.MeasureText(Dvf_lstDisks.Items[iRow].SubItems[iColumn].Text, Dvf_lstDisks.Font).Width);
            }

            /* The text and its margins. */
            return(iWidest + DVF_CELL_PAD);
        }

        /*--------------------------------------------------------------------*/
        /* Dvf_FitWidth:                                                      */
        /*                                                                    */
        /* Widen the dialog, never narrow it, until every column fits its     */
        /* measured content with no horizontal scroll bar: at the dialog-box  */
        /* font six columns do not fit DVF_WIDTH, and a Media column cut off  */
        /* at the edge hides a fact the choice rests on.  Called after the    */
        /* list is filled, since the widest row decides it.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the dialog is wide enough for the columns.              */
        /*--------------------------------------------------------------------*/
        private void Dvf_FitWidth()
        {
            int iNeeded;
            int iColumn;

            /* The columns' MEASURED widths -- not their present ones, since    */
            /* the Model column has been stretched to fill the list, which      */
            /* would make the sum the dialog's own width and grow it every      */
            /* time -- plus the list's border and scroll bar, and the padding   */
            /* either side of the list.                                         */
            iNeeded = (2 * DVF_PAD) + SystemInformation.VerticalScrollBarWidth + (2 * SystemInformation.Border3DSize.Width);

            for (iColumn = 0; iColumn < Dvf_lstDisks.Columns.Count; iColumn++)
            {   /* One column per iteration. */
                iNeeded = iNeeded + Dvf_MeasureColumn(iColumn);
            }

            if (Dvf_bRangeRow)
            {   /* The "how much" row is one line: the dialog is at least as wide as it, with its exact-bytes label at its longest. */
                Dvf_lblRangeBytes.Text = "= " + ulong.MaxValue.ToString("N0") + " bytes";
                iNeeded                = Math.Max(iNeeded, (2 * DVF_PAD) + Dvf_pnlRange.PreferredSize.Width);
                Dvf_UpdateRange();
            }

            if (Dvf_chkXz != null)
            {   /* Read Device's compress box: its one line must fit too. */
                iNeeded = Math.Max(iNeeded, (2 * DVF_PAD) + Dvf_chkXz.PreferredSize.Width);
            }

            if (Dvf_chkGpt != null && Dvf_bGptShown)
            {   /* Read Device's GPT box, while shown: its one line, with the size in it, must fit too. */
                iNeeded = Math.Max(iNeeded, (2 * DVF_PAD) + Dvf_chkGpt.PreferredSize.Width);
            }

            if (iNeeded > this.ClientSize.Width)
            {   /* Wider than the dialog: grow it, then refit the Model column into the new width. */
                this.ClientSize = new Size(iNeeded, this.ClientSize.Height);
                Dvf_FitColumns();
            }
        }

        /*--------------------------------------------------------------------*/
        /* DVF_BusText:                                                       */
        /*                                                                    */
        /* The words for a bus number as Windows reports it in a device's     */
        /* storage descriptor.  Only the buses a card or disk is likely to be */
        /* on are named; any other is shown as its number.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uiBus : the STORAGE_BUS_TYPE value.                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the bus in words.                                     */
        /*--------------------------------------------------------------------*/
        internal static string DVF_BusText(uint uiBus)
        {
            switch (uiBus)
            {   /* One named bus per case.  The numbers are Windows'        */
                /* documented STORAGE_BUS_TYPE values; 4 to 6 (1394, SSA,   */
                /* Fibre Channel) and 16                                    */
                /* (Storage Spaces) are left as numbers.                    */
                case 1:
                    /* The SCSI storage-bus name. */
                    return("SCSI");
                case 2:
                    /* The ATAPI storage-bus name. */
                    return("ATAPI");
                case 3:
                    /* The ATA storage-bus name. */
                    return("ATA");
                case 7:
                    /* The USB storage-bus name. */
                    return("USB");
                case 8:
                    /* The RAID storage-bus name. */
                    return("RAID");
                case 9:
                    /* The iSCSI storage-bus name. */
                    return("iSCSI");
                case 10:
                    /* The serial-attached SCSI storage-bus name. */
                    return("SAS");
                case 11:
                    /* The serial ATA storage-bus name. */
                    return("SATA");
                case 12:
                    /* The SD storage-bus name used by card readers. */
                    return("SD");
                case 13:
                    /* The MMC storage-bus name. */
                    return("MMC");
                case 14:
                    /* A virtual bus without the file-backed distinction. */
                    return("Virtual");
                case 15:
                    /* Retain the explicit file-backed virtual-bus wording. */
                    return("Virtual (file)");
                case 17:
                    /* The NVMe storage-bus name. */
                    return("NVMe");
            }

            /* Anything else: the number Windows gave. */
            return("bus " + uiBus);
        }

        /*--------------------------------------------------------------------*/
        /* DVF_SizeText:                                                      */
        /*                                                                    */
        /* A size in THE program's one written form, with the exact byte      */
        /* count added: the binary size, a slash, the decimal size, then the  */
        /* bytes in parentheses -- "971.0 MiB / 1.0 GB (1,018,167,296         */
        /* bytes)".  The slash rather than a second pair of parentheses is    */
        /* because callers put the whole in parentheses after a place name,   */
        /* and nested ones read badly.  Every dialog that names a size in     */
        /* units goes through this or DVF_SizeShortText, so the two units     */
        /* appear together and in the same order on every dialog.  The        */
        /* binary size leads because it is the unit the size boxes are typed  */
        /* in (KiB, MiB, GiB) and the figure Windows shows; the decimal one   */
        /* is what the card is sold as.  Under one KiB there is nothing to    */
        /* scale and the bytes alone are given.                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     ullBytes : the size.                                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : "29.7 GiB / 31.9 GB (31,914,983,424 bytes)" and the   */
        /*              like; "512 bytes" below one KiB.                      */
        /*--------------------------------------------------------------------*/
        internal static string DVF_SizeText(ulong ullBytes)
        {
            if (ullBytes < 1024UL)
            {   /* Too small to scale: the count says it all. */
                return(ullBytes.ToString("N0") + " bytes");
            }

            /* Both units, binary first, then the exact count. */
            return(DVF_SizeShortText(ullBytes) + " (" + ullBytes.ToString("N0") + " bytes)");
        }

        /*--------------------------------------------------------------------*/
        /* Dvf_SizeBinaryText:                                                */
        /*                                                                    */
        /* The binary half of the written size: the largest of KiB, MiB, GiB  */
        /* and TiB that the size reaches, to one decimal place with thousands */
        /* separators.  Shared by DVF_SizeText and DVF_SizeShortText so the   */
        /* two cannot drift apart in their thresholds or precision.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     ullBytes : the size, one KiB or more.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : "971.0 MiB" and the like.                             */
        /*--------------------------------------------------------------------*/
        private static string Dvf_SizeBinaryText(ulong ullBytes)
        {
            if (ullBytes >= 1099511627776UL)
            {   /* A tebibyte or more. */
                return(((double)ullBytes / 1099511627776.0).ToString("N1") + " TiB");
            }

            if (ullBytes >= 1073741824UL)
            {   /* A gibibyte or more. */
                return(((double)ullBytes / 1073741824.0).ToString("N1") + " GiB");
            }

            if (ullBytes >= 1048576UL)
            {   /* A mebibyte or more. */
                return(((double)ullBytes / 1048576.0).ToString("N1") + " MiB");
            }

            /* Under a mebibyte: kibibytes. */
            return(((double)ullBytes / 1024.0).ToString("N1") + " KiB");
        }

        /*--------------------------------------------------------------------*/
        /* Dvf_SizeDecimalText:                                               */
        /*                                                                    */
        /* The decimal half of the written size, in the units a disk is sold  */
        /* in: the largest of KB, MB, GB and TB the size reaches, to one      */
        /* decimal place with thousands separators.  Shared as the binary     */
        /* half is.                                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     ullBytes : the size, one KiB or more.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : "1.0 GB" and the like.                                */
        /*--------------------------------------------------------------------*/
        private static string Dvf_SizeDecimalText(ulong ullBytes)
        {
            if (ullBytes >= 1000000000000UL)
            {   /* A terabyte or more. */
                return(((double)ullBytes / 1000000000000.0).ToString("N1") + " TB");
            }

            if (ullBytes >= 1000000000UL)
            {   /* A gigabyte or more, as the label on the card says. */
                return(((double)ullBytes / 1000000000.0).ToString("N1") + " GB");
            }

            if (ullBytes >= 1000000UL)
            {   /* A megabyte or more. */
                return(((double)ullBytes / 1000000.0).ToString("N1") + " MB");
            }

            /* Under a megabyte: kilobytes. */
            return(((double)ullBytes / 1000.0).ToString("N1") + " KB");
        }

        /*--------------------------------------------------------------------*/
        /* DVF_ModelText:                                                     */
        /*                                                                    */
        /* A device's model as the program shows it: the engine's vendor and  */
        /* product text, with runs of spaces made single.  The two fields     */
        /* arrive space-padded ("Generic- USB3.0 CRW   -SD"), which reads as  */
        /* a gap in the middle of a name, hard to read.  Shared by the disk   */
        /* list and the partition window's title, so the one name is spelt    */
        /* one way.                                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stInfo : the device's facts.                                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the model, or "" when the device gave none.           */
        /*--------------------------------------------------------------------*/
        internal static string DVF_ModelText(DVI_stDeviceInfo stInfo)
        {
            string sModel;

            /* The engine's text (UTF-8, as the engine fills it), with the   */
            /* padding at either end taken off.                              */
            sModel = ENG_clsNative.ENG_FromUtf8(stInfo.DVI_abModel).Trim();

            while (sModel.IndexOf("  ") >= 0)
            {   /* One doubled space per iteration, until none is left. */
                sModel = sModel.Replace("  ", " ");
            }

            /* The model as words. */
            return(sModel);
        }

        /*--------------------------------------------------------------------*/
        /* DVF_SizeShortText:                                                 */
        /*                                                                    */
        /* The written size WITHOUT the exact count: the binary size, a       */
        /* slash, the decimal one -- "971.0 MiB / 1.0 GB" -- which            */
        /* DVF_SizeText then extends with the bytes.  For a sentence          */
        /* that names a place and its size in passing -- a warning, an error, */
        /* a list cell -- where the exact count only buries the name, and the */
        /* shortened form is enough.  The kilo units are there because a      */
        /* partition can be                                                   */
        /* smaller than a megabyte (a quarter-megabyte boot environment).     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     ullBytes : the size.                                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : "971.0 MiB / 1.0 GB" and the like; "512 bytes" below  */
        /*              one KiB.                                              */
        /*--------------------------------------------------------------------*/
        internal static string DVF_SizeShortText(ulong ullBytes)
        {
            if (ullBytes < 1024UL)
            {   /* Too small to scale: the count says it all. */
                return(ullBytes.ToString("N0") + " bytes");
            }

            /* Both units, binary first. */
            return(Dvf_SizeBinaryText(ullBytes) + " / " + Dvf_SizeDecimalText(ullBytes));
        }

        /*--------------------------------------------------------------------*/
        /* DVF_IsPortable:                                                    */
        /*                                                                    */
        /* Whether a disk is one of the kinds shown by default: removable     */
        /* media, or on a USB, SD or MMC bus.  A fixed internal disk is not.  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stInfo : the disk's facts.                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true for a card, stick or adapter.                      */
        /*--------------------------------------------------------------------*/
        internal static bool DVF_IsPortable(DVI_stDeviceInfo stInfo)
        {
            if (stInfo.DVI_uiRemovable != 0)
            {   /* Windows says the media comes out. */
                return(true);
            }

            if (stInfo.DVI_uiBus == ENG_clsNative.ENG_BUS_USB || stInfo.DVI_uiBus == ENG_clsNative.ENG_BUS_SD || stInfo.DVI_uiBus == ENG_clsNative.ENG_BUS_MMC)
            {   /* A bus that cards and sticks sit on, removable flag or not. */
                return(true);
            }

            /* Neither removable media nor one of the default portable buses. */
            return(false);
        }

        /*-----------------------------------------------------------------------*/
        /* Dvf_FillList:                                                         */
        /*                                                                       */
        /* Fill the list from the disks, applying the tick box: fixed disks      */
        /* are left out unless it is ticked.  Each row's Tag is its DEV_clsDisk. */
        /* The first row is selected, so Enter opens something.                  */
        /*                                                                       */
        /* Arguments:                                                            */
        /*     None.                                                             */
        /*                                                                       */
        /* Returns:                                                              */
        /*     void : the list shows the disks that pass the filter.             */
        /*-----------------------------------------------------------------------*/
        private void Dvf_FillList()
        {
            ListViewItem lviRow;
            DEV_clsDisk objDisk;
            string       sModel;
            string       sMedia;
            int          i;

            /* The list rebuilt from scratch (the tick box can add or take   */
            /* away rows anywhere), so the selection starts afresh too.      */
            Dvf_lstDisks.Items.Clear();

            for (i = 0; i < Dvf_lstAll.Count; i++)
            {   /* One disk per iteration, in the order the engine gave. */
                objDisk = Dvf_lstAll[i];

                if (!Dvf_chkFixed.Checked && !DVF_IsPortable(objDisk.DEV_stInfo))
                {   /* An internal disk, and they are not wanted. */
                    continue;
                }

                /* The model as the program writes it everywhere. */
                sModel = DVF_ModelText(objDisk.DEV_stInfo);

                if (sModel.Length == 0)
                {   /* No model text from the device. */
                    sModel = "(unknown)";
                }

                if (objDisk.DEV_stInfo.DVI_uiSystem != 0)
                {   /* The disk Windows runs from: say so beside the model. */
                    sModel = sModel + "  (Windows)";
                }

                if (objDisk.DEV_stInfo.DVI_uiRemovable != 0)
                {   /* Windows' own word for the media. */
                    sMedia = "removable";
                }
                else
                {   /* Fixed media -- which a USB card reader or adapter can   */
                    /* also report (as found), hence the bus test in           */
                    /* DVF_IsPortable.                                         */
                    sMedia = "fixed";
                }

                /* The row: the disk's Windows name, its DEV_clsDisk in the   */
                /* Tag (what the selection hands back), then model, bus,      */
                /* short size, exact bytes and media -- Dvf_arrColumns'       */
                /* order.                                                     */
                lviRow     = new ListViewItem("PhysicalDrive" + objDisk.DEV_iNumber);
                lviRow.Tag = objDisk;
                lviRow.SubItems.Add(sModel);
                lviRow.SubItems.Add(DVF_BusText(objDisk.DEV_stInfo.DVI_uiBus));
                lviRow.SubItems.Add(DVF_SizeShortText(objDisk.DEV_stInfo.DVI_ullSize));
                lviRow.SubItems.Add(objDisk.DEV_stInfo.DVI_ullSize.ToString("N0"));
                lviRow.SubItems.Add(sMedia);
                Dvf_lstDisks.Items.Add(lviRow);
            }

            if (Dvf_lstDisks.Items.Count != 0)
            {   /* Something to open: start on the first row.  (Selecting it   */
                /* raises the selection change, which once hooked updates      */
                /* the details line and the "how much" row.)                   */
                Dvf_lstDisks.Items[0].Selected = true;
            }

            /* The columns refitted to the rows now listed. */
            Dvf_FitColumns();
        }

        /*--------------------------------------------------------------------*/
        /* Dvf_FitColumns:                                                    */
        /*                                                                    */
        /* Size every column to its content, measured, and give the Model     */
        /* column whatever width is left so the list is filled edge to edge.  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the columns fit the list's width.                       */
        /*--------------------------------------------------------------------*/
        private void Dvf_FitColumns()
        {
            int iColumn;
            int iOthers;

            if (Dvf_lstDisks.Columns.Count == 0)
            {   /* Not built yet. */
                return;
            }

            /* The width every column but Model takes, added up. */
            iOthers = 0;

            for (iColumn = 0; iColumn < Dvf_lstDisks.Columns.Count; iColumn++)
            {   /* One column per iteration: the widest of its heading and its cells. */
                Dvf_lstDisks.Columns[iColumn].Width = Dvf_MeasureColumn(iColumn);

                if (iColumn != 1)
                {   /* Everything but the Model column keeps its measured width. */
                    iOthers = iOthers + Dvf_lstDisks.Columns[iColumn].Width;
                }
            }

            /* The Model column takes the rest, but never less than it measured. */
            Dvf_lstDisks.Columns[1].Width = Math.Max(Dvf_lstDisks.Columns[1].Width, Dvf_lstDisks.ClientSize.Width - iOthers);
        }

        /*--------------------------------------------------------------------*/
        /* Dvf_OnFixedChanged:                                                */
        /*                                                                    */
        /* The tick box changed: remember it and refill the list.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the tick box.                                      */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the list and the setting follow the box.                */
        /*--------------------------------------------------------------------*/
        private void Dvf_OnFixedChanged(object objSender, EventArgs evArgs)
        {
            int iValue;

            /* Unticked is stored as 0. */
            iValue = 0;

            if (Dvf_chkFixed.Checked)
            {   /* Stored as 1. */
                iValue = 1;
            }

            /* Remembered for the next time the dialog opens, then the list   */
            /* refilled with the disks the box now allows, and the dialog     */
            /* widened if a fixed disk's row needs more room (it is never     */
            /* narrowed again when they go).                                  */
            SET_Settings.SET_WriteInt(SET_Settings.SET_OPT_SHOW_FIXED, iValue);
            Dvf_FillList();
            Dvf_FitWidth();
        }

        /*--------------------------------------------------------------------*/
        /* Dvf_OnOpenClicked:                                                 */
        /*                                                                    */
        /* The Open button -- captioned Save..., Write... or Use Its Size for */
        /* the other commands -- or Enter, the dialog's accept key: asks for  */
        /* the selected disk to be taken.  The handler only posts: the        */
        /* acceptance can show a refusal box, which is never shown from a     */
        /* handler.                                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : an Accept item posted to the dialog's queue.            */
        /*--------------------------------------------------------------------*/
        private void Dvf_OnOpenClicked(object objSender, EventArgs evArgs)
        {
            /* The same acceptance a double-clicked row asks for; a second   */
            /* press while one waits is dropped by the queue.                */
            Dvf_objQueue.WQU_Post((int)Dvf_enmItem.Accept, null);
        }

        /*--------------------------------------------------------------------*/
        /* Dvf_OnDiskActivated:                                               */
        /*                                                                    */
        /* A disk's row was double-clicked (or Enter pressed on it): that is  */
        /* the Open button.  The row is the selected one by then, which is    */
        /* what the acceptance takes.                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the list.                                          */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : an Accept item posted to the dialog's queue.            */
        /*--------------------------------------------------------------------*/
        private void Dvf_OnDiskActivated(object objSender, EventArgs evArgs)
        {
            /* The same acceptance the Open button asks for, posted. */
            Dvf_objQueue.WQU_Post((int)Dvf_enmItem.Accept, null);
        }

        /*--------------------------------------------------------------------*/
        /* Dvf_RunItem:                                                       */
        /*                                                                    */
        /* The dialog's queue runner: one posted item at a time, after the    */
        /* handler that posted it has returned, so a refusal box is shown     */
        /* from here and never from inside a handler.                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iItem   : the Dvf_enmItem posted.                              */
        /*     objArgs : the item's argument (none for Accept).               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item's work done.                                   */
        /*--------------------------------------------------------------------*/
        private void Dvf_RunItem(int iItem, object objArgs)
        {
            switch ((Dvf_enmItem)iItem)
            {   /* Which work was asked for. */
                case Dvf_enmItem.Accept:
                    /* The selected disk taken, or refused with its reason. */
                    Dvf_AcceptDisk();
                    break;
                default:
                    /* No such item (a guard: only the handlers above post):   */
                    /* nothing done.                                           */
                    break;
            }
        }

        /*---------------------------------------------------------------------*/
        /* Dvf_AcceptDisk:                                                     */
        /*                                                                     */
        /* Open, from the button or a row: take the selected disk and close    */
        /* accepted.  With nothing selected the dialog stays open.             */
        /* With "Only the first" ticked the length is checked here, so a       */
        /* wrong one is put right in the dialog rather than found by the copy: */
        /* it must be a number, more than nothing once rounded down to the     */
        /* disk's sectors, and no more than there is -- the device's length    */
        /* for a read; for a write the image's length AND the device's (the    */
        /* engine would refuse the second too, but only after the warning).    */
        /* With Read's GPT box ticked the box decides instead, refused only    */
        /* when the backup header lies past the device's end.                  */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     None.                                                           */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : the dialog closes accepted, or stays.                    */
        /*---------------------------------------------------------------------*/
        private void Dvf_AcceptDisk()
        {
            DEV_clsDisk objDisk;
            ulong    ullBytes;
            string   sProblem;
            int      iRemember;

            if (Dvf_lstDisks.SelectedItems.Count == 0)
            {   /* Nothing picked: the dialog stays, silently (the details   */
                /* line already says "No disk selected.").                   */
                return;
            }

            /* The disk picked; "everything" (0) and no problem until the   */
            /* "how much" test below says otherwise.                        */
            objDisk = (DEV_clsDisk)Dvf_lstDisks.SelectedItems[0].Tag;
            ullBytes = 0;
            sProblem = "";

            if (Dvf_chkGpt != null && Dvf_chkGpt.Checked)
            {   /* Read Device through the backup GPT: the length is the box's,    */
                /* and only refused when the table puts its backup header past     */
                /* the device's end (a table made for a larger disk) -- there is   */
                /* nothing there to read.                                          */
                if (Dvf_ullGptEnd > objDisk.DEV_stInfo.DVI_ullSize)
                {   /* Past the end. */
                    sProblem = "This disk's GPT puts its backup table past the end of the device: it would end at " +
                               DVF_SizeText(Dvf_ullGptEnd) + ", and the device holds " + DVF_SizeText(objDisk.DEV_stInfo.DVI_ullSize) + ".\n" +
                               "Untick the GPT box to read the whole device.";
                }
            }
            else if (Dvf_bRangeRow && Dvf_radFirst.Checked)
            {   /* A shorter region was asked for: is it one that can be copied? */
                if (!Dvf_RangeBytes(objDisk, out ullBytes))
                {   /* Not a number. */
                    sProblem = "The length is not a number.";
                }
                else if (ullBytes == 0)
                {   /* Nothing, once rounded down to the disk's sectors.  (A   */
                    /* disk that reported no sector size is rounded at 512,    */
                    /* but the message then names "0 bytes".)                  */
                    sProblem = "The length is less than one sector of this disk (" + objDisk.DEV_stInfo.DVI_uiSectorBytes + " bytes).";
                }
                else if (ullBytes > objDisk.DEV_stInfo.DVI_ullSize)
                {   /* Past the end of the device, either way. */
                    sProblem = "The length is more than the device holds (" + DVF_SizeText(objDisk.DEV_stInfo.DVI_ullSize) + ").";
                }
                else if (Dvf_iRange == DVF_RANGE_WRITE && ullBytes > Dvf_ullImageBytes)
                {   /* Past the end of the image being written (never true when its size is unknown). */
                    sProblem = "The length is more than the image holds (" + DVF_SizeText(Dvf_ullImageBytes) + ").";
                }
            }

            if (sProblem.Length != 0)
            {   /* Say so, and leave the dialog open to change it. */
                MSG_clsMessageForm.MSG_Show(this, this.Font, sProblem, this.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning,
                                         MessageBoxDefaultButton.Button1);

                if (Dvf_chkGpt != null && Dvf_chkGpt.Checked)
                {   /* The GPT box is what to change. */
                    Dvf_chkGpt.Focus();
                }
                else
                {   /* The typed length is. */
                    Dvf_txtRange.Focus();
                }

                return;
            }

            /* Accepted: the disk, the length (0 for everything, and for the   */
            /* GPT box, whose length the read works out again from the disk    */
            /* itself) and the GPT choice, for the caller to read once the     */
            /* dialog has closed.                                              */
            DVF_objDiskChosen = objDisk;
            DVF_ullRangeBytes = ullBytes;
            DVF_bThroughGpt   = (Dvf_chkGpt != null && Dvf_chkGpt.Checked);
            DVF_bAccepted     = true;

            if (Dvf_chkXz != null)
            {   /* Read Device: the compress choice, kept as the next run's starting state. */
                DVF_bCompressXz = Dvf_chkXz.Checked;
                iRemember       = 0;

                if (DVF_bCompressXz)
                {   /* Stored as 1. */
                    iRemember = 1;
                }

                SET_Settings.SET_WriteInt(SET_Settings.SET_OPT_READ_XZ, iRemember);
            }

            /* Closed accepted.  (The compress choice is remembered only   */
            /* here, so a Cancel does not change it.)                      */
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Dvf_OnCancelClicked:                                               */
        /*                                                                    */
        /* Cancel: close with the outcome left at "not accepted".             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the dialog closes.                                      */
        /*--------------------------------------------------------------------*/
        private void Dvf_OnCancelClicked(object objSender, EventArgs evArgs)
        {
            /* Closed; DVF_bAccepted is still false from the constructor. */
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Dvf_OnResize:                                                      */
        /*                                                                    */
        /* The dialog's size changed -- the user cannot resize it (a fixed    */
        /* border), but Dvf_FitWidth widens it from code: refit the columns   */
        /* to the list's new width.                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the dialog.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the columns fit.                                        */
        /*--------------------------------------------------------------------*/
        private void Dvf_OnResize(object objSender, EventArgs evArgs)
        {
            /* Model takes the new width; the rest keep their measure. */
            Dvf_FitColumns();
        }
    }
}
