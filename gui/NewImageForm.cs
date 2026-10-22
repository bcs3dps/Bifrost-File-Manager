/*----------------------------------------------------------------------------*/
/* NewImageForm.cs                                                            */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* Two small dialogs used by File > New Image and by the partition window:    */
/*                                                                            */
/*   NIM_clsNewImageForm : asks what to create -- the file, its size, the     */
/*                      filesystem type (ext4, ext3, ext2, FAT16, FAT32 or    */
/*                      exFAT), the block size (a FAT type's cluster size)    */
/*                      and the label -- and checks the answers before it     */
/*                      will close on OK.  It has two narrower modes, one     */
/*                      for each half of that:                                */
/*                      put into size-only mode by NIM_SizeOnly, it asks the  */
/*                      file and its size alone (New Image, which creates an  */
/*                      empty file and hands it to the partition window);     */
/*                      put into partition mode by NIM_FixTarget, it asks     */
/*                      only the type, block size and label for a range       */
/*                      whose place and size are given (the window's Make     */
/*                      Filesystem).  Nothing uses the full form any more.    */
/*   NBF_clsBusyForm     : a "working" box with a moving bar, shown while the */
/*                      engine works with no progress to report -- made by    */
/*                      the progress windows (CommandProgressWindows.cs)      */
/*                      when a command's progress view asks for it            */
/*                      (PGV_bBusy; which commands do is the worker's         */
/*                      choice).  It has no buttons: the work cannot be       */
/*                      cancelled here, and the box closes only when the      */
/*                      progress windows call NBF_Finish at the work's end.   */
/*                                                                            */
/* Neither dialog touches the engine.  The first is shown as a question of    */
/* the command worker's sequence (CommandQuestionPresenter.cs), which reads   */
/* its outcome fields and hands them to that sequence to do the work.  Both   */
/* show their text in the program's dialog font, which the caller passes in;  */
/* neither chooses a font nor frees the one it is given.                      */
/*----------------------------------------------------------------------------*/

/* The framework: the base types (Math, event arguments, the exceptions      */
/* caught), fonts and sizes, the path, folder and file tests on the          */
/* image's path, and the dialogs and their controls.  The number parsing's   */
/* culture, the label's UTF-8 count and the FAT label's code page are        */
/* named in full where used.                                                 */
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace BifrostFM
{
    /*-------------------------------------------------------------------------*/
    /* NIM_clsNewImageForm -- asks for a new image file and its size, or for a */
    /* filesystem's type, block size and label; checks the answers on OK.      */
    /*                                                                         */
    /* Derives from Form because the framework requires it: WinForms makes a   */
    /* window only as a Form subclass.  That is the one kind of inheritance    */
    /* allowed here -- from a framework base class, never from a class of      */
    /* ours.                                                                   */
    /*-------------------------------------------------------------------------*/
    internal sealed class NIM_clsNewImageForm : Form
    {
        /* The dialog's sizes and the limits its checks use --   */
        /* documentation table.                                  */
        private const int     NIM_WIDTH        = 560;                                                                               // client width, pixels
        private const int     NIM_PAD          = 12;                                                                                // padding round the content, pixels
        private const decimal NIM_SIZE_DEFAULT = 64;                                                                                // the size the dialog opens with, in the default unit (MiB)
        internal const decimal NIM_SIZE_MAX    = 1125899906842624m;                                                                 // the largest number the size box takes, in any unit (2^50, so a byte count of any real card fits); Match a Device refuses a disk longer than this (Qpr_NewImageDisk)
        private const ulong   NIM_BYTES_MAX    = 1125899906842624UL;                                                                // the largest image accepted, bytes (1 PiB); a larger number in a larger unit is refused with a message
        private const ulong   NIM_SECTOR       = 512;                                                                               // a size is rounded DOWN to a whole number of these, so the file is a disk of whole 512-byte sectors
        private const ulong   NIM_EXT4_1K_MIN  = 8 * 1048576;                                                                       // ext4 with 1024-byte blocks cannot be made below this size (the engine refuses it)
        private const int     NIM_UNIT_MIB     = 3;                                                                                 // index of MiB in the unit tables below: the default
        private const int     NIM_UNIT_GIB     = 4;                                                                                 // index of GiB in the unit tables below
        private const int     NIM_TYPE_FIRST_FAT = 3;                                                                               // the type list's first FAT row (FAT16); the rows from it on are FAT, those before it ext (Nim_TypeFromIndex)
        private const int     NIM_CLUSTER_ROWS = 8;                                                                                 // the cluster-size list's sizes after Automatic: 512 bytes doubling to 64 KiB, the most FAT12 / 16 / 32 takes at 512-byte sectors
        private const int     NIM_FAT_LABEL_CHARS = 11;                                                                             // the most characters a FAT or exFAT label holds (FAT: 11 bytes; exFAT: 11 UTF-16 units) -- the engine refuses more
        private const string  NIM_FAT_LABEL_BAD   = "+.,;=[]/*:<>|\\\"?";                                                           // the characters a FAT12 / 16 / 32 label may not hold (the engine's own list)
        private const int     NIM_FAT_CODE_PAGE   = 437;                                                                            // the OEM code page a FAT12 / 16 / 32 label is stored in (the engine's FatFs is built for it); a character it lacks is refused
        private const char    NIM_DEL             = (char)0x7F;                                                                     // DEL, refused in a FAT label as the control characters are
        private const string  NIM_EXFAT_LABEL_BAD = "/*:<>|\\\"?";                                                                  // the characters an exFAT label may not hold, beside control characters
        private const string  NIM_NOTE_EXT     = "The new filesystem is empty apart from lost+found, and has NO journal.";          // the note's first sentence for an ext type
        private const string  NIM_NOTE_FAT     = "The new filesystem is empty; FAT has no journal, no owners and no permissions.";  // the same for a FAT type

        /* The size units, read index for index -- documentation table.  An    */
        /* image must be makeable EXACTLY the size of a given card, so bytes   */
        /* and sectors are units too, and a fraction of a larger unit is       */
        /* allowed (0.8 GiB).                                                  */
        private static readonly string[] Nim_asUnits         = { "bytes", "sectors", "KiB", "MiB", "GiB" };      // as the unit box shows them; "sectors" are 512 bytes
        private static readonly ulong[]  Nim_aullUnitBytes   = { 1UL, 512UL, 1024UL, 1048576UL, 1073741824UL };  // bytes in one of each
        private static readonly int[]    Nim_aiUnitDecimals  = { 0, 0, 1, 2, 3 };                                // decimal places the number box takes in each: enough for about a sector in KiB, and steps of about 10 KiB in MiB and 1 MiB in GiB

        /* The outcome, which the caller reads after ShowDialog returns --   */
        /* CommandQuestionPresenter.cs, New Image's question                 */
        /* (Qpr_NewImageFile) and the partition window's format options      */
        /* (Qpr_MakeFsOptions); the rest means something only when           */
        /* NIM_bAccepted is true.                                            */
        internal bool   NIM_bAccepted;      // true when OK was pressed and every answer passed its check
        internal string NIM_sImagePath;     // the image file to create, a full path
        internal ulong  NIM_ullBytes;       // its size in bytes
        internal uint   NIM_uiFsType;       // ENG_FSTYPE_*
        internal uint   NIM_uiBlockSize;    // ext: the block size, 1024, 2048 or 4096; FAT: the cluster size in bytes; 0 for the engine's choice
        internal string NIM_sLabel;         // the volume label; "" for none
        internal bool   NIM_bOverwrite;     // true when the file exists and the user agreed to replace it
        internal bool   NIM_bZeroFill;      // partition mode: true when "Erase the whole partition first" was ticked (ENG_MAKEFS_ZERO)

        /* With NIM_AskOutside: Browse or Match ends the dialog instead of     */
        /* running its own work, and these say which, with what was typed so   */
        /* far, for the caller to ask the picker and show the dialog again     */
        /* (NIM_SetTyped) -- documentation table.                              */
        internal bool    NIM_bBrowseAsked;  // true when Browse ended the dialog
        internal bool    NIM_bMatchAsked;   // true when Match a Device ended the dialog
        internal string  NIM_sTypedPath;    // the path box's text as it stood
        internal decimal NIM_decTypedSize;  // the number in the size box as it stood
        internal int     NIM_iTypedUnit;    // the unit box's index as it stood (into Nim_asUnits)

        /* The dialog's controls, and the state its three modes set --   */
        /* documentation table.                                          */
        private TableLayoutPanel Nim_tblFields;        // the label / field grid
        private TextBox          Nim_txtPath;          // the image file's path
        private Button           Nim_btnBrowse;        // ends the dialog so the asking sequence asks the file with the standard save dialog (off in partition mode)
        private NumericUpDown    Nim_numSize;          // the size, in the unit beside it
        private ComboBox         Nim_cmbUnit;          // the unit the number is in: one of Nim_asUnits
        private int              Nim_iUnitShown;       // the unit the number box's value is in now, so a unit change can convert FROM it
        private TextBox          Nim_txtBytes;         // the exact size in bytes, read-only, in the space beside the unit box: what the typed number comes to, or a partition's real length
        private ComboBox         Nim_cmbType;          // ext4 / ext3 / ext2 / FAT16 / FAT32 / exFAT
        private ComboBox         Nim_cmbBlock;         // ext: Automatic / 1024 / 2048 / 4096; FAT: Automatic / 512 bytes to 64 KiB (Nim_FillBlockList)
        private bool             Nim_bFatShown;        // true while the block list holds FAT's cluster sizes (a FAT type is chosen)
        private TextBox          Nim_txtLabel;         // the volume label
        private Label            Nim_lblTypeCap;       // the type row's caption (taken out in size-only mode)
        private Label            Nim_lblBlockCap;      // the block-size row's caption (likewise)
        private Label            Nim_lblLabelCap;      // the label row's caption (likewise)
        private CheckBox         Nim_chkZero;          // "Erase the whole partition first": zeros over the whole range before formatting (taken out in size-only mode)
        private Label            Nim_lblZeroCap;       // its row's empty caption (likewise)
        private Label            Nim_lblNote;          // the standing note about what is created
        private Button           Nim_btnMatch;         // "Match a Device...": ends the dialog so the asking sequence fills the size from a disk's (hidden until NIM_AskOutside)
        private bool             Nim_bAskOutside;      // true once NIM_AskOutside has offered Match a Device: the size row leaves room for its button
        private FlowLayoutPanel  Nim_pnlButtons;       // OK and Cancel, right-aligned
        private Button           Nim_btnOk;            // asks for the answers to be checked and accepted (Nim_AcceptAnswers, from the dialog's queue)
        private Button           Nim_btnCancel;        // abandons the dialog
        private bool             Nim_bFixed;           // true in partition mode (NIM_FixTarget): the place and the size are given, not typed
        private bool             Nim_bSmallerAllowed;  // with Nim_bFixed: a whole table-less disk, whose size may be typed LOWER (NIM_AllowSmaller)
        private bool             Nim_bSizeOnly;        // true in size-only mode (NIM_SizeOnly): the type, block size and label are not asked
        private string           Nim_sFixedTarget;     // partition mode: the text that names the place, handed back as NIM_sImagePath
        private ulong            Nim_ullFixedBytes;    // partition mode: the partition's length, handed back as NIM_ullBytes

        /* The work the dialog's handlers post to its own queue, which   */
        /* Nim_RunItem runs one at a time -- documentation table.        */
        private enum Nim_enmItem
        {
            Accept = 1     // Create (Format in partition mode) or Enter: every answer checked and, when all stand, taken (Nim_AcceptAnswers)
        }

        /* The dialog's own queue: its handlers post, never show a box. */
        private WQU_clsWindowQueue Nim_objQueue;   // made in the constructor before any control is hooked to post to it

        /*--------------------------------------------------------------------*/
        /* NIM_AskOutside:                                                    */
        /*                                                                    */
        /* Offer "Match a Device..." beside the size -- the way to make an    */
        /* image EXACTLY the size of a given card -- for the dialog shown as  */
        /* New Image's question of the command worker's sequence.  Browse     */
        /* and Match a Device end the dialog, saying which and what was typed */
        /* (NIM_bBrowseAsked, NIM_bMatchAsked, NIM_sTypedPath,                */
        /* NIM_decTypedSize, NIM_iTypedUnit), so the sequence asks the file   */
        /* picker or the disk picker itself and shows the dialog again with   */
        /* NIM_SetTyped; a disk's length is only filled in, never fixed.  No  */
        /* dialog is opened, and nothing is read, by a button of this one.    */
        /* Partition mode's size is the partition's, so Match is not offered  */
        /* there.                                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Match is shown and the size row refitted round it.      */
        /*--------------------------------------------------------------------*/
        internal void NIM_AskOutside()
        {
            /* The flag Nim_FitLayout tests to leave room for the button,   */
            /* then the button shown and the size row refitted round it.    */
            Nim_bAskOutside      = true;
            Nim_btnMatch.Visible = true;
            Nim_FitLayout();
        }

        /*--------------------------------------------------------------------*/
        /* NIM_SetTyped:                                                      */
        /*                                                                    */
        /* Put back what was typed before Browse or Match ended the dialog,   */
        /* with the picker's answer in its place when there was one, before   */
        /* the dialog is shown again.                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sPath    : the path box's text.                                */
        /*     decSize  : the number for the size box, in iUnit.              */
        /*     iUnit    : the unit's index (into Nim_asUnits).                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the path, unit and number shown, and the bytes beside   */
        /*            them worked out again.                                  */
        /*--------------------------------------------------------------------*/
        internal void NIM_SetTyped(string sPath, decimal decSize, int iUnit)
        {
            /* The path as given. */
            Nim_txtPath.Text = sPath;

            /* The unit first -- its change converts the number already       */
            /* there, which the next line then replaces -- then the number,   */
            /* held inside the box's own range, then the bytes beside it.     */
            /* The hold is a guard: the number put back is one the box        */
            /* itself gave, or a disk's length, and Match a Device refuses    */
            /* a disk longer than the box takes (Qpr_NewImageDisk).           */
            if (iUnit >= 0 && iUnit < Nim_asUnits.Length)
            {   /* A unit the box has: chosen. */
                Nim_cmbUnit.SelectedIndex = iUnit;
            }

            if (decSize < Nim_numSize.Minimum)
            {   /* Below the box's floor: its floor. */
                decSize = Nim_numSize.Minimum;
            }

            if (decSize > Nim_numSize.Maximum)
            {   /* Above its ceiling: its ceiling. */
                decSize = Nim_numSize.Maximum;
            }

            Nim_numSize.Value = decSize;
            Nim_UpdateBytes();
        }

        /*--------------------------------------------------------------------*/
        /* Nim_EndForOutside:                                                 */
        /*                                                                    */
        /* Browse's or Match's press: what was typed is recorded and the      */
        /* dialog closes, not accepted, so the sequence that asked it runs    */
        /* the picker and shows it again.                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bBrowse : true for Browse, false for Match a Device.           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the fields recorded and the dialog closing.             */
        /*--------------------------------------------------------------------*/
        private void Nim_EndForOutside(bool bBrowse)
        {
            /* Which button, and the three fields as they stand. */
            NIM_bBrowseAsked = bBrowse;
            NIM_bMatchAsked  = !bBrowse;
            NIM_sTypedPath   = Nim_txtPath.Text;
            NIM_decTypedSize = Nim_numSize.Value;
            NIM_iTypedUnit   = Nim_cmbUnit.SelectedIndex;

            /* Closed not accepted: NIM_bAccepted stays false, as Cancel   */
            /* leaves it, and the two flags above tell the two apart.      */
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Nim_OnMatchClicked:                                                */
        /*                                                                    */
        /* Match a Device: the dialog ends with the typed fields, for the     */
        /* sequence that asked it to list the disks and ask the disk picker   */
        /* (Sys_AskNewImageFile).  Nothing is read or opened here.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the dialog is closing.                                  */
        /*--------------------------------------------------------------------*/
        private void Nim_OnMatchClicked(object objSender, EventArgs evArgs)
        {
            /* The fields recorded and the dialog closed, Match named. */
            Nim_EndForOutside(false);
        }

        /*--------------------------------------------------------------------*/
        /* NIM_AllowSmaller:                                                  */
        /*                                                                    */
        /* After NIM_FixTarget, for a WHOLE DISK with no partition table:     */
        /* the size stays the user's to lower, so a filesystem can be made    */
        /* smaller than the disk.  The rest of the disk is left as it is, and */
        /* Expand Filesystem can grow the filesystem into it later.  OK then  */
        /* holds the typed size between the engine's floor                    */
        /* (ENG_MAKEFS_MIN_BYTES) and the disk's length (Nim_AcceptAnswers).  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the size is editable, at most the disk's length.        */
        /*--------------------------------------------------------------------*/
        internal void NIM_AllowSmaller()
        {
            /* The flag Nim_UpdateBytes and OK read; the size box and its     */
            /* unit enabled again, starting at the disk's exact length in     */
            /* bytes (the unit change converts the old figure, which the      */
            /* next line replaces); the bytes field brought up to date; the   */
            /* note told what a smaller size means; the layout refitted for   */
            /* the longer note.                                               */
            Nim_bSmallerAllowed       = true;
            Nim_numSize.Enabled       = true;
            Nim_cmbUnit.Enabled       = true;
            Nim_cmbUnit.SelectedIndex = 0;
            Nim_numSize.Value         = (decimal)Nim_ullFixedBytes;
            Nim_UpdateBytes();
            Nim_lblNote.Text          = Nim_lblNote.Text + " A smaller size leaves the rest of the disk unused (Expand Filesystem can grow into it later).";
            Nim_FitLayout();
        }

        /*--------------------------------------------------------------------*/
        /* NIM_FixTarget:                                                     */
        /*                                                                    */
        /* Turn the dialog into Make Filesystem for an EXISTING partition of  */
        /* a device or a disk image (the partition window's format options,   */
        /* Qpr_MakeFsOptions): the place and the size are the partition's and */
        /* cannot be changed here, and OK hands them back as given; the type, */
        /* block size and label stay the user's to choose.  The "everything   */
        /* on it will be lost" question is asked elsewhere, not by this       */
        /* dialog.                                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sTarget  : the place's name alone, shown in the place box and  */
        /*                the title and handed back (the device or file and   */
        /*                the partition; no size -- the bytes field has it).  */
        /*     ullBytes : the partition's length in bytes.                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the dialog is in partition mode.                        */
        /*--------------------------------------------------------------------*/
        internal void NIM_FixTarget(string sTarget, ulong ullBytes)
        {
            /* Partition mode on, with the place and the length kept for OK   */
            /* and for the bytes field.                                       */
            Nim_bFixed        = true;
            Nim_sFixedTarget  = sTarget;
            Nim_ullFixedBytes = ullBytes;

            /* The title and the button say what will happen; the file row   */
            /* shows the partition's name, read-only, with Browse off.       */
            this.Text         = "Make filesystem in " + sTarget;
            Nim_btnOk.Text    = "Format";
            Nim_txtPath.Text  = sTarget;
            Nim_txtPath.ReadOnly   = true;
            Nim_btnBrowse.Enabled  = false;

            /* The size in GiB from one gibibyte up, else in MiB, rounded to   */
            /* the unit's places for show; the bytes field beside it gives     */
            /* the exact length (Nim_UpdateBytes reads Nim_ullFixedBytes in    */
            /* this mode), which is what the engine is handed.                 */
            if (ullBytes >= Nim_aullUnitBytes[NIM_UNIT_GIB])
            {   /* A gibibyte or more. */
                Nim_cmbUnit.SelectedIndex = NIM_UNIT_GIB;
            }
            else
            {   /* Less: mebibytes. */
                Nim_cmbUnit.SelectedIndex = NIM_UNIT_MIB;
            }

            /* The rounded figure for show, then both boxes off (the size    */
            /* is the partition's), the bytes field with the exact length,   */
            /* the note's warning, and the layout refitted.                  */
            Nim_numSize.Value = Math.Round((decimal)ullBytes / (decimal)Nim_aullUnitBytes[Nim_cmbUnit.SelectedIndex],
                                           Nim_aiUnitDecimals[Nim_cmbUnit.SelectedIndex]);
            Nim_numSize.Enabled = false;
            Nim_cmbUnit.Enabled = false;
            Nim_UpdateBytes();
            Nim_lblNote.Text    = Nim_lblNote.Text + "\nEverything in the partition is lost.";
            Nim_FitLayout();
        }

        /*---------------------------------------------------------------------*/
        /* NIM_SizeOnly:                                                       */
        /*                                                                     */
        /* Turn the dialog into New Image's first step (Qpr_NewImageFile): the */
        /* file and its size, nothing else.  OK leaves the type, block size    */
        /* and label at their defaults, which the caller does not read; the    */
        /* file checks and the "replace it?" question are unchanged.           */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     None.                                                           */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : the dialog is in size-only mode.                         */
        /*---------------------------------------------------------------------*/
        internal void NIM_SizeOnly()
        {
            /* Size-only mode on (OK skips the type / block check). */
            Nim_bSizeOnly = true;

            /* The four filesystem rows taken out of the grid, captions and   */
            /* fields alike; their rows collapse to nothing.                  */
            Nim_tblFields.Controls.Remove(Nim_lblTypeCap);
            Nim_tblFields.Controls.Remove(Nim_cmbType);
            Nim_tblFields.Controls.Remove(Nim_lblBlockCap);
            Nim_tblFields.Controls.Remove(Nim_cmbBlock);
            Nim_tblFields.Controls.Remove(Nim_lblLabelCap);
            Nim_tblFields.Controls.Remove(Nim_txtLabel);
            Nim_tblFields.Controls.Remove(Nim_lblZeroCap);
            Nim_tblFields.Controls.Remove(Nim_chkZero);

            /* The title and the note say that an empty file is made and    */
            /* the partition window follows; the layout refits the longer   */
            /* note (it can wrap to two lines).                             */
            this.Text        = "New image file";
            Nim_lblNote.Text = "An empty file of that size is made. The partition window opens over it next, to partition it or to make one filesystem over the whole of it.";
            Nim_FitLayout();
        }

        /*--------------------------------------------------------------------*/
        /* NIM_clsNewImageForm (constructor):                                 */
        /*                                                                    */
        /* Build the dialog, in its full mode until a mode setter narrows it; */
        /* nothing here carries a coordinate.                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     fntDialog    : the program's dialog font (the caller's; not    */
        /*                    freed here); the message boxes this dialog      */
        /*                    opens are handed it too.                        */
        /*     sStartFolder : the folder the file box starts in (New Image's  */
        /*                    last folder, remembered by the main window), or */
        /*                    "" for none.                                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the dialog, not yet shown.                     */
        /*--------------------------------------------------------------------*/
        internal NIM_clsNewImageForm(Font fntDialog, string sStartFolder)
        {
            FlowLayoutPanel pnlPath;
            FlowLayoutPanel pnlSize;

            /* The outcome at "not accepted" with ext4 and the engine's     */
            /* block size as the defaults, and the full mode until a mode   */
            /* setter says otherwise.                                       */
            NIM_bAccepted   = false;
            NIM_sImagePath  = "";
            NIM_ullBytes    = 0;
            NIM_uiFsType    = ENG_clsNative.ENG_FSTYPE_EXT4;
            NIM_uiBlockSize = 0;
            NIM_sLabel      = "";
            NIM_bOverwrite  = false;
            NIM_bZeroFill   = false;
            NIM_bBrowseAsked = false;
            NIM_bMatchAsked  = false;
            NIM_sTypedPath   = "";
            NIM_decTypedSize = 0;
            NIM_iTypedUnit   = 0;
            Nim_bAskOutside   = false;
            Nim_bFixed        = false;
            Nim_bSizeOnly     = false;
            Nim_sFixedTarget  = "";
            Nim_ullFixedBytes = 0;

            /* The caller's dialog font, set FIRST: every control inherits     */
            /* it once it is added (as found), and the number and unit boxes   */
            /* below are given widths measured in this.Font before they have   */
            /* a parent -- set later, those widths would be the default        */
            /* font's.                                                         */
            this.Font = fntDialog;

            /* A fixed dialog centred on the main window, out of the   */
            /* taskbar, with a pad round everything.                   */
            this.Text            = "New image";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox     = false;
            this.MinimizeBox     = false;
            this.ShowInTaskbar   = false;
            this.StartPosition   = FormStartPosition.CenterParent;
            this.Padding         = new Padding(NIM_PAD);

            /* The file: a path box and a Browse button on one row.  The box   */
            /* is given its width in Nim_FitLayout, once the button's is       */
            /* known.                                                          */
            Nim_txtPath = new TextBox();

            if (sStartFolder.Length != 0)
            {   /* Suggest a name in the folder the caller chose. */
                Nim_txtPath.Text = Path.Combine(sStartFolder, "new-image.img");
            }

            /* Browse, sized to its caption.  It opens nothing itself: it   */
            /* ends the dialog, so the sequence that asked it shows the     */
            /* save dialog and then this one again (Nim_OnBrowseClicked).   */
            Nim_btnBrowse          = new Button();
            Nim_btnBrowse.Text     = "&Browse...";
            Nim_btnBrowse.AutoSize = true;
            Nim_btnBrowse.Click   += Nim_OnBrowseClicked;

            /* The two on one unwrapped row, with no margin of their own,   */
            /* as the grid's field for the file.                            */
            pnlPath              = new FlowLayoutPanel();
            pnlPath.AutoSize     = true;
            pnlPath.WrapContents = false;
            pnlPath.Margin       = new Padding(0);
            pnlPath.Controls.Add(Nim_txtPath);
            pnlPath.Controls.Add(Nim_btnBrowse);

            /* The size: a number, its unit, and what the two come to in        */
            /* bytes.  The number box takes a fraction (0.8 GiB) and a whole    */
            /* card's byte count; its minimum is 0 so typing is never fought,   */
            /* and OK refuses what is too small.  Widths are measured in the    */
            /* DIALOG's font: these boxes have no parent yet, so their own      */
            /* Font would still be the small default.  No thousands             */
            /* separator: the raw number in the box, while the bytes field      */
            /* beside it keeps its commas.  Both its text and its value         */
            /* changes update the bytes field, since the value lags the         */
            /* typing (Nim_TypedBytes): each event has a handler of its own,    */
            /* and both call Nim_UpdateBytes.                                   */
            Nim_numSize                    = new NumericUpDown();
            Nim_numSize.Minimum            = 0;
            Nim_numSize.Maximum            = NIM_SIZE_MAX;
            Nim_numSize.ThousandsSeparator = false;
            Nim_numSize.DecimalPlaces      = Nim_aiUnitDecimals[NIM_UNIT_MIB];
            Nim_numSize.Value              = NIM_SIZE_DEFAULT;
            Nim_numSize.Width              = TextRenderer.MeasureText("8,888,888,888,888", this.Font).Width + SystemInformation.VerticalScrollBarWidth + NIM_PAD;
            Nim_numSize.TextChanged       += Nim_OnSizeTextChanged;
            Nim_numSize.ValueChanged      += Nim_OnSizeValueChanged;

            /* The unit, a fixed list opening at MiB, as wide as its longest   */
            /* entry ("sectors") plus the drop button.                         */
            Nim_cmbUnit               = new ComboBox();
            Nim_cmbUnit.DropDownStyle = ComboBoxStyle.DropDownList;
            Nim_cmbUnit.Items.AddRange(Nim_asUnits);
            Nim_cmbUnit.SelectedIndex = NIM_UNIT_MIB;
            Nim_cmbUnit.Width         = TextRenderer.MeasureText("sectors", this.Font).Width + SystemInformation.VerticalScrollBarWidth + NIM_PAD;

            /* The exact size, read-only, in the space beside the unit: what    */
            /* the typed number comes to -- rounded down to whole 512-byte      */
            /* sectors -- or, in partition mode, the partition's real length.   */
            /* Given its width by Nim_FitLayout.                                */
            Nim_txtBytes          = new TextBox();
            Nim_txtBytes.ReadOnly = true;
            Nim_txtBytes.TabStop  = false;

            /* Wired only now, so the unit set above did not run the handler   */
            /* on half-built boxes; then the bytes field shows the default.    */
            Nim_iUnitShown                    = Nim_cmbUnit.SelectedIndex;
            Nim_cmbUnit.SelectedIndexChanged += Nim_OnUnitChanged;
            Nim_UpdateBytes();

            /* The three on one unwrapped row, as the grid's size field. */
            pnlSize              = new FlowLayoutPanel();
            pnlSize.AutoSize     = true;
            pnlSize.WrapContents = false;
            pnlSize.Margin       = new Padding(0);
            pnlSize.Controls.Add(Nim_numSize);
            pnlSize.Controls.Add(Nim_cmbUnit);
            pnlSize.Controls.Add(Nim_txtBytes);

            /* Match a Device, at the row's end; hidden until NIM_AskOutside,   */
            /* so the bytes field takes the room when it is not offered.        */
            Nim_btnMatch          = new Button();
            Nim_btnMatch.Text     = "&Match a Device...";
            Nim_btnMatch.AutoSize = true;
            Nim_btnMatch.Visible  = false;
            Nim_btnMatch.Click   += Nim_OnMatchClicked;
            pnlSize.Controls.Add(Nim_btnMatch);

            /* The type.  The order here is the order of Nim_TypeFromIndex. */
            Nim_cmbType               = new ComboBox();
            Nim_cmbType.DropDownStyle = ComboBoxStyle.DropDownList;
            Nim_cmbType.Items.Add("ext4 (extents; no journal)");
            Nim_cmbType.Items.Add("ext3 features (directory index; no journal)");
            Nim_cmbType.Items.Add("ext2");
            Nim_cmbType.Items.Add("FAT16 (FAT12 when it is small)");
            Nim_cmbType.Items.Add("FAT32");
            Nim_cmbType.Items.Add("exFAT");
            Nim_cmbType.SelectedIndex = 0;
            Nim_cmbType.Dock          = DockStyle.Fill;

            /* The block size -- or, for a FAT type, the cluster size --     */
            /* filled by Nim_FillBlockList, starting with the ext list for   */
            /* ext4, the type selected above.                                */
            Nim_cmbBlock               = new ComboBox();
            Nim_cmbBlock.DropDownStyle = ComboBoxStyle.DropDownList;
            Nim_cmbBlock.Dock          = DockStyle.Fill;

            /* The label.  The limit is ENG_LABEL_MAX_BYTES BYTES of UTF-8,     */
            /* checked on OK; the box's own limit, the same number of           */
            /* CHARACTERS, only stops the obvious case.  A FAT type's tighter   */
            /* rules are checked on OK as well.                                 */
            Nim_txtLabel           = new TextBox();
            Nim_txtLabel.MaxLength = ENG_clsNative.ENG_LABEL_MAX_BYTES;
            Nim_txtLabel.Dock      = DockStyle.Fill;

            /* The note: what the user is about to get, said before they get   */
            /* it.  Its first sentence is the ext one until a FAT type is      */
            /* chosen (Nim_OnTypeChanged swaps it).                            */
            Nim_lblNote          = new Label();
            Nim_lblNote.AutoSize = true;
            Nim_lblNote.Text     = NIM_NOTE_EXT;
            Nim_lblNote.Margin   = new Padding(3, NIM_PAD, 3, 0);

            /* The grid, which sizes its own rows: the caption column as   */
            /* wide as its widest caption, the field column the rest.      */
            Nim_tblFields             = new TableLayoutPanel();
            Nim_tblFields.Dock        = DockStyle.Top;
            Nim_tblFields.AutoSize    = true;
            Nim_tblFields.ColumnCount = 2;
            Nim_tblFields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Nim_tblFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            /* The rows, top down: the file, the size, then the three       */
            /* filesystem rows, whose captions are kept so size-only mode   */
            /* can take those rows out (NIM_SizeOnly).                      */
            Nim_AddRow("Image &file:", pnlPath);
            Nim_AddRow("&Size:", pnlSize);
            Nim_lblTypeCap  = Nim_AddRow("File&system:", Nim_cmbType);
            Nim_lblBlockCap = Nim_AddRow("Bloc&k size:", Nim_cmbBlock);
            Nim_lblLabelCap = Nim_AddRow("Volume &label:", Nim_txtLabel);

            /* The block list filled for the ext type now selected, its    */
            /* caption in the grid to be retitled with it; then the type   */
            /* list's handler hooked, so a FAT type swaps the list, the    */
            /* caption and the note.                                       */
            Nim_FillBlockList(false);
            Nim_cmbType.SelectedIndexChanged += Nim_OnTypeChanged;

            /* The zero-fill, an erase of the whole partition before it is    */
            /* formatted: unticked, since on a large card it takes minutes,   */
            /* and saying so on the box itself.  Only partition mode hands    */
            /* it back (Nim_AcceptAnswers).                                   */
            Nim_chkZero          = new CheckBox();
            Nim_chkZero.Text     = "&Erase the whole partition first (writes zeros; slow on a large card)";
            Nim_chkZero.AutoSize = true;
            Nim_chkZero.Checked  = false;
            Nim_lblZeroCap       = Nim_AddRow("", Nim_chkZero);

            /* The note as the grid's last row, across both columns. */
            Nim_tblFields.Controls.Add(Nim_lblNote, 0, Nim_tblFields.RowCount);
            Nim_tblFields.SetColumnSpan(Nim_lblNote, 2);
            Nim_tblFields.RowCount = Nim_tblFields.RowCount + 1;

            /* Cancel, made first: the button row below flows right to left,   */
            /* so the first added is the rightmost, where Cancel goes.         */
            Nim_btnCancel          = new Button();
            Nim_btnCancel.Text     = "Cancel";
            Nim_btnCancel.AutoSize = true;
            Nim_btnCancel.Click   += Nim_OnCancelClicked;

            /* The dialog's own queue, made before OK, which posts to it, is   */
            /* hooked: OK asks for the answers to be checked and taken, and    */
            /* Nim_RunItem does it, so a refusal or the replace question is    */
            /* never shown from inside the button's handler.                   */
            Nim_objQueue = new WQU_clsWindowQueue(this, Nim_RunItem);

            /* OK, left of Cancel, captioned for New Image (partition mode   */
            /* recaptions it "Format").                                      */
            Nim_btnOk          = new Button();
            Nim_btnOk.Text     = "Create";
            Nim_btnOk.AutoSize = true;
            Nim_btnOk.Click   += Nim_OnOkClicked;

            /* Their row along the bottom, a pad above, flowing right to   */
            /* left so Cancel is rightmost.                                */
            Nim_pnlButtons               = new FlowLayoutPanel();
            Nim_pnlButtons.Dock          = DockStyle.Bottom;
            Nim_pnlButtons.FlowDirection = FlowDirection.RightToLeft;
            Nim_pnlButtons.AutoSize      = true;
            Nim_pnlButtons.Padding       = new Padding(0, NIM_PAD, 0, 0);
            Nim_pnlButtons.Controls.Add(Nim_btnCancel);
            Nim_pnlButtons.Controls.Add(Nim_btnOk);

            /* Docking runs in reverse order of adding (as found): the       */
            /* button row, added last, takes the bottom; the grid the top.   */
            this.Controls.Add(Nim_tblFields);
            this.Controls.Add(Nim_pnlButtons);

            /* Enter presses OK (which checks), Escape presses Cancel. */
            this.AcceptButton = Nim_btnOk;
            this.CancelButton = Nim_btnCancel;

            /* The design width with a starting height; the resize handler   */
            /* hooked; then Nim_FitLayout sizes the fields and fits the      */
            /* height to the content.                                        */
            this.ClientSize = new Size(NIM_WIDTH, NIM_WIDTH / 2);
            this.Resize    += Nim_OnResize;
            Nim_FitLayout();
        }

        /*--------------------------------------------------------------------*/
        /* Nim_AddRow:                                                        */
        /*                                                                    */
        /* Add one caption-and-field row to the grid.  The caption is a label */
        /* whose ampersand is the access key for the field after it.  The     */
        /* caption is handed back so a mode can take the row out again.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sCaption : the caption, with its access-key ampersand.         */
        /*     ctlField : the field, or the panel holding the row's fields.   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Label : the caption, in the grid.                              */
        /*--------------------------------------------------------------------*/
        private Label Nim_AddRow(string sCaption, Control ctlField)
        {
            Label lblCaption;
            int   iRow;

            /* The caption, sized to its text, held to the left of its cell   */
            /* and centred on the row's height beside a taller field.         */
            lblCaption           = new Label();
            lblCaption.Text      = sCaption;
            lblCaption.AutoSize  = true;
            lblCaption.Anchor    = AnchorStyles.Left;
            lblCaption.TextAlign = ContentAlignment.MiddleLeft;

            /* The caption goes in BEFORE its field, so Alt+key lands on the field. */
            iRow = Nim_tblFields.RowCount;
            Nim_tblFields.Controls.Add(lblCaption, 0, iRow);
            Nim_tblFields.Controls.Add(ctlField, 1, iRow);
            Nim_tblFields.RowCount = iRow + 1;

            /* The caption, for a mode that removes the row. */
            return(lblCaption);
        }

        /*--------------------------------------------------------------------*/
        /* Nim_FitLayout:                                                     */
        /*                                                                    */
        /* Size what docking cannot -- the path box, the bytes field, the     */
        /* note's wrap -- and make the dialog exactly as tall as its content. */
        /* Run after every mode change and on every resize.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the path box and the client height fit.                 */
        /*--------------------------------------------------------------------*/
        private void Nim_FitLayout()
        {
            int iFieldWidth;
            int iShort;

            /* Let the grid settle its column widths before reading them. */
            Nim_tblFields.PerformLayout();

            /* The field column's width: what the caption column leaves. */
            iFieldWidth = Nim_tblFields.GetColumnWidths()[1];

            /* The path box: that width less the Browse button and both   */
            /* their margins.                                             */
            Nim_txtPath.Width = iFieldWidth - Nim_btnBrowse.PreferredSize.Width - Nim_btnBrowse.Margin.Horizontal - Nim_txtPath.Margin.Horizontal;

            if (Nim_txtPath.Width < NIM_WIDTH / 4)
            {   /* Never squeezed to nothing, whatever the font. */
                Nim_txtPath.Width = NIM_WIDTH / 4;
            }

            /* The bytes field takes what the number and the unit leave of the   */
            /* row.  Always, not only once it reports itself visible: a          */
            /* control on a dialog not yet shown reports Visible false (as       */
            /* found), so a Visible test would leave this field at its default   */
            /* width, clipped.                                                   */
            Nim_txtBytes.Width = iFieldWidth - Nim_numSize.Width - Nim_numSize.Margin.Horizontal -
                                 Nim_cmbUnit.Width - Nim_cmbUnit.Margin.Horizontal - Nim_txtBytes.Margin.Horizontal;

            if (Nim_bAskOutside)
            {   /* Match a Device is offered: its button takes its own width   */
                /* from the end of the row.  Read from the flag, not the       */
                /* button's Visible, which an unshown dialog's control         */
                /* reports as false (as found) -- this runs before the         */
                /* dialog is shown.                                            */
                Nim_txtBytes.Width = Nim_txtBytes.Width - Nim_btnMatch.PreferredSize.Width - Nim_btnMatch.Margin.Horizontal;
            }

            /* The bytes field must hold a terabyte card's count; when the      */
            /* row leaves it less, the dialog widens by the shortfall and the   */
            /* layout runs once more (it then finds no shortfall).              */
            iShort = TextRenderer.MeasureText("8,888,888,888,888 bytes", this.Font).Width + NIM_PAD - Nim_txtBytes.Width;

            if (iShort > 0)
            {   /* Too narrow: widen, and lay out again at the new width. */
                this.ClientSize = new Size(this.ClientSize.Width + iShort, this.ClientSize.Height);
                Nim_FitLayout();
                return;
            }

            /* The note spans both columns and can need two lines (size-only   */
            /* mode's does).  Left to itself the grid draws it wrapped but     */
            /* MEASURES it as one line (as found), which would make the        */
            /* dialog a line short and put the buttons over its second line.   */
            /* Capping its width at the grid's own makes its preferred size    */
            /* the wrapped one, which the grid's preferred height then         */
            /* includes.                                                       */
            Nim_lblNote.MaximumSize = new Size(Nim_tblFields.ClientSize.Width - Nim_lblNote.Margin.Horizontal, 0);
            Nim_tblFields.PerformLayout();

            /* The width kept; the height the grid and the button row with    */
            /* the dialog's pad above and below.  This raises Resize: from    */
            /* Nim_OnResize, whose handler is detached, it is ignored; from   */
            /* anywhere else the handler runs this fit once more, to the      */
            /* same height.                                                   */
            this.ClientSize = new Size(this.ClientSize.Width,
                                       Nim_tblFields.PreferredSize.Height + Nim_pnlButtons.PreferredSize.Height + (2 * NIM_PAD));
        }

        /*--------------------------------------------------------------------*/
        /* Nim_TypeFromIndex:                                                 */
        /*                                                                    */
        /* The engine's filesystem type for a row of the type list.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iIndex : the selected row (0 ext4, 1 ext3, 2 ext2, 3 FAT16,    */
        /*              4 FAT32, 5 exFAT).                                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     uint : an ENG_FSTYPE_* value.                                  */
        /*--------------------------------------------------------------------*/
        private static uint Nim_TypeFromIndex(int iIndex)
        {
            if (iIndex == 1)
            {   /* The ext3 feature set. */
                return(ENG_clsNative.ENG_FSTYPE_EXT3);
            }

            if (iIndex == 2)
            {   /* ext2. */
                return(ENG_clsNative.ENG_FSTYPE_EXT2);
            }

            if (iIndex == NIM_TYPE_FIRST_FAT)
            {   /* FAT16 -- which the engine's formatter makes FAT12 when   */
                /* the size gives too few clusters for FAT16, as the FAT    */
                /* specification decides the width by the cluster count.    */
                return(ENG_clsNative.ENG_FSTYPE_FAT16);
            }

            if (iIndex == NIM_TYPE_FIRST_FAT + 1)
            {   /* FAT32. */
                return(ENG_clsNative.ENG_FSTYPE_FAT32);
            }

            if (iIndex == NIM_TYPE_FIRST_FAT + 2)
            {   /* exFAT. */
                return(ENG_clsNative.ENG_FSTYPE_EXFAT);
            }

            /* The first row, and the default. */
            return(ENG_clsNative.ENG_FSTYPE_EXT4);
        }

        /*--------------------------------------------------------------------*/
        /* Nim_IsFatType:                                                     */
        /*                                                                    */
        /* Whether an engine type is one of the FAT family, which takes a     */
        /* cluster size rather than a block size and its own label rules.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uiType : an ENG_FSTYPE_* value.                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true for FAT16, FAT32 or exFAT.                         */
        /*--------------------------------------------------------------------*/
        private static bool Nim_IsFatType(uint uiType)
        {
            /* The three FAT values the type list can give. */
            return(uiType == ENG_clsNative.ENG_FSTYPE_FAT16 || uiType == ENG_clsNative.ENG_FSTYPE_FAT32 ||
                uiType == ENG_clsNative.ENG_FSTYPE_EXFAT);
        }

        /*--------------------------------------------------------------------*/
        /* Nim_BlockFromIndex:                                                */
        /*                                                                    */
        /* The block size -- or a FAT type's cluster size -- for a row of the */
        /* block-size list, as Nim_FillBlockList filled it.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iIndex : the selected row (0 Automatic; then 1024, 2048, 4096  */
        /*              for ext, or 512 bytes doubling to 64 KiB for FAT).    */
        /*     bFat   : true when the list holds FAT's cluster sizes.         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     uint : the size in bytes, or 0 for the engine's choice.        */
        /*--------------------------------------------------------------------*/
        private static uint Nim_BlockFromIndex(int iIndex, bool bFat)
        {
            if (iIndex <= 0)
            {   /* Automatic. */
                return(0);
            }

            if (bFat)
            {   /* Rows 1 to NIM_CLUSTER_ROWS are 512 bytes doubling: 256   */
                /* shifted by the row.                                      */
                return(256u << iIndex);
            }

            /* Rows 1, 2 and 3 are 1024, 2048 and 4096: each double the last. */
            return(512u << iIndex);
        }

        /*--------------------------------------------------------------------*/
        /* Nim_FillBlockList:                                                 */
        /*                                                                    */
        /* Fill the block-size list for an ext type or a FAT type, and title  */
        /* its row to match: an ext filesystem has a block size (1024, 2048   */
        /* or 4096 bytes), a FAT one a cluster size (512 bytes up to 64 KiB   */
        /* here; the engine refuses a size the volume's sector size or type   */
        /* cannot take, and says why).  Automatic is the first row either     */
        /* way and is selected.                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bFat : true for FAT's cluster sizes, false for ext's blocks.   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the list, its caption and Nim_bFatShown match bFat.     */
        /*--------------------------------------------------------------------*/
        private void Nim_FillBlockList(bool bFat)
        {
            int i;

            /* The old rows gone and Automatic first, whichever list. */
            Nim_cmbBlock.Items.Clear();
            Nim_cmbBlock.Items.Add("Automatic");

            if (bFat)
            {   /* FAT's cluster sizes, in the order Nim_BlockFromIndex reads. */
                for (i = 1; i <= NIM_CLUSTER_ROWS; i++)
                {   /* One size per iteration: bytes up to 1 KiB's rows,   */
                    /* KiB after.                                          */
                    if (Nim_BlockFromIndex(i, true) < 1024)
                    {   /* Under a kibibyte: in bytes. */
                        Nim_cmbBlock.Items.Add(Nim_BlockFromIndex(i, true) + " bytes");
                    }
                    else
                    {   /* A kibibyte or more: in KiB. */
                        Nim_cmbBlock.Items.Add((Nim_BlockFromIndex(i, true) / 1024) + " KiB");
                    }
                }

                /* The row named for what it now holds. */
                Nim_lblBlockCap.Text = "Cl&uster size:";
            }
            else
            {   /* ext's block sizes, in the order Nim_BlockFromIndex reads. */
                Nim_cmbBlock.Items.Add("1024 bytes");
                Nim_cmbBlock.Items.Add("2048 bytes");
                Nim_cmbBlock.Items.Add("4096 bytes");
                Nim_lblBlockCap.Text = "Bloc&k size:";
            }

            /* Automatic selected, and which list is shown recorded. */
            Nim_cmbBlock.SelectedIndex = 0;
            Nim_bFatShown = bFat;
        }

        /*--------------------------------------------------------------------*/
        /* Nim_OnTypeChanged:                                                 */
        /*                                                                    */
        /* Another filesystem type was chosen: moving between the ext family  */
        /* and the FAT family swaps the block list for the cluster list (or   */
        /* back) and the note's first sentence; moving within a family keeps  */
        /* the size the user picked.                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the type list.                                     */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the block list, its caption and the note match the      */
        /*            type.                                                   */
        /*--------------------------------------------------------------------*/
        private void Nim_OnTypeChanged(object objSender, EventArgs evArgs)
        {
            bool bFat;

            /* Which family the type now chosen is. */
            bFat = Nim_IsFatType(Nim_TypeFromIndex(Nim_cmbType.SelectedIndex));

            if (bFat == Nim_bFatShown)
            {   /* The same family: the list already fits, and the size   */
                /* chosen in it stands.                                   */
                return;
            }

            /* The other family: its list, and the note's first sentence      */
            /* swapped -- the sentences a mode appended after it (partition   */
            /* mode's warning) are kept -- then the layout refitted for the   */
            /* note's new length.                                             */
            Nim_FillBlockList(bFat);

            if (bFat)
            {   /* To FAT: no lost+found, no journal, no owners. */
                Nim_lblNote.Text = Nim_lblNote.Text.Replace(NIM_NOTE_EXT, NIM_NOTE_FAT);
            }
            else
            {   /* Back to ext. */
                Nim_lblNote.Text = Nim_lblNote.Text.Replace(NIM_NOTE_FAT, NIM_NOTE_EXT);
            }

            Nim_FitLayout();
        }

        /*--------------------------------------------------------------------*/
        /* Nim_FatLabelProblem:                                               */
        /*                                                                    */
        /* Why a label cannot be a FAT or exFAT volume's, by the rules the    */
        /* engine applies before it formats (bfe_fat.c BFT_CheckMakeFs): so   */
        /* the user is told here, beside the box, rather than by a failed     */
        /* format.  FAT12 / 16 / 32: at most 11 characters that the OEM code  */
        /* page (NIM_FAT_CODE_PAGE) holds once in capitals -- accented        */
        /* letters among them, as Windows takes them for a FAT label -- none  */
        /* of NIM_FAT_LABEL_BAD, no control character or DEL (FAT stores it   */
        /* in capitals).  exFAT: at most 11 UTF-16 units, none of             */
        /* NIM_EXFAT_LABEL_BAD and no control character.  The capitals here   */
        /* are the framework's, the engine's FatFs's own table's; the two     */
        /* agree on the letters a code page holds, and the engine judges      */
        /* again before it writes anything.                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sLabel : the label, trimmed; "" is always allowed.             */
        /*     uiType : the FAT type chosen (ENG_FSTYPE_FAT16, _FAT32 or      */
        /*              _EXFAT).                                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the reason, or "" when the label is allowed.          */
        /*--------------------------------------------------------------------*/
        private static string Nim_FatLabelProblem(string sLabel, uint uiType)
        {
            System.Text.Encoding encOem;
            byte[]               abOem;
            int                  i;

            if (sLabel.Length > NIM_FAT_LABEL_CHARS)
            {   /* Too long for either: a C# string's length is its UTF-16   */
                /* units, exFAT's measure, and on FAT, whose code page has   */
                /* one byte per character, the bytes.                        */
                return("A FAT or exFAT volume label is limited to 11 characters.");
            }

            /* The OEM code page, with a character it lacks turned into no   */
            /* bytes at all rather than a '?', so "no bytes" is the test     */
            /* below and no exception is needed to find one.                 */
            encOem = System.Text.Encoding.GetEncoding(NIM_FAT_CODE_PAGE,
                                                      new System.Text.EncoderReplacementFallback(""),
                                                      new System.Text.DecoderReplacementFallback(""));

            for (i = 0; i < sLabel.Length; i++)
            {   /* One character per iteration, against the type's rules. */
                if (uiType == ENG_clsNative.ENG_FSTYPE_EXFAT)
                {   /* exFAT: any character but a control or a reserved one. */
                    if (sLabel[i] < ' ' || NIM_EXFAT_LABEL_BAD.IndexOf(sLabel[i]) >= 0)
                    {   /* A character exFAT refuses in a label. */
                        return("An exFAT volume label may not hold control characters or any of  / * : < > | \\ \" ?");
                    }
                }
                else
                {   /* FAT12 / 16 / 32: the character in capitals, in the   */
                    /* code page -- one byte, or none when it lacks it.     */
                    abOem = encOem.GetBytes(new string(char.ToUpperInvariant(sLabel[i]), 1));

                    if (sLabel[i] < ' ' || sLabel[i] == NIM_DEL || char.IsSurrogate(sLabel[i]) || abOem.Length != 1 ||
                        NIM_FAT_LABEL_BAD.IndexOf(sLabel[i]) >= 0)
                    {   /* A control character or DEL, half of a character    */
                        /* past U+FFFF, one the code page lacks, or one FAT   */
                        /* reserves.                                          */
                        return("A FAT volume label may hold letters (accented ones too), digits and the punctuation a FAT label takes -- none of  + . , ; = [ ] / * : < > | \\ \" ?  and no symbol outside the FAT code page (437)");
                    }
                }
            }

            /* Every character allowed. */
            return("");
        }

        /*--------------------------------------------------------------------*/
        /* Nim_TypedBytes:                                                    */
        /*                                                                    */
        /* What the number box and the unit come to in bytes, rounded DOWN to */
        /* a whole number of 512-byte sectors, following the typing as it     */
        /* happens.                                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     ullBytes : receives the byte count.                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the size is a number no larger than           */
        /*            NIM_BYTES_MAX; false when it is too large to accept.    */
        /*--------------------------------------------------------------------*/
        private bool Nim_TypedBytes(out ulong ullBytes)
        {
            decimal decNumber;
            decimal decBytes;
            int     iUnit;

            /* Nothing until a size is accepted; the unit as chosen. */
            ullBytes = 0;
            iUnit    = Nim_cmbUnit.SelectedIndex;

            if (iUnit < 0)
            {   /* No unit chosen (cannot normally happen): treat as bytes. */
                iUnit = 0;
            }

            /* The number from the box's TEXT when that parses: a             */
            /* NumericUpDown updates its Value only when it loses the focus   */
            /* or Enter is pressed (as found), and the bytes field is to      */
            /* follow the typing.  Otherwise from Value.                      */
            if (!decimal.TryParse(Nim_numSize.Text, System.Globalization.NumberStyles.Number,
                                  System.Globalization.CultureInfo.CurrentCulture, out decNumber) || decNumber < 0)
            {   /* Half-typed or not a number: what the box last accepted. */
                decNumber = Nim_numSize.Value;
            }

            /* The bytes, in decimal arithmetic, so 0.8 GiB is exact before   */
            /* it is rounded; the unit table's largest times the box's        */
            /* largest stays within decimal's range.                          */
            decBytes = decNumber * (decimal)Nim_aullUnitBytes[iUnit];

            if (decBytes > (decimal)NIM_BYTES_MAX)
            {   /* More than an image may be. */
                return(false);
            }

            /* Rounded DOWN to whole 512-byte sectors, so the file is a   */
            /* disk of whole sectors and never larger than asked.         */
            ullBytes = ((ulong)decimal.Floor(decBytes / (decimal)NIM_SECTOR)) * NIM_SECTOR;

            /* A size in whole sectors. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* Nim_UpdateBytes:                                                   */
        /*                                                                    */
        /* Show the exact size in the bytes field: in partition mode the      */
        /* partition's own length (the number box holds only a rounded        */
        /* figure for show), otherwise what the typed number and unit come    */
        /* to, or that it is too large.                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the bytes field is current.                             */
        /*--------------------------------------------------------------------*/
        private void Nim_UpdateBytes()
        {
            ulong ullBytes;

            if (Nim_bFixed && !Nim_bSmallerAllowed)
            {   /* A partition: its exact length, whatever the boxes show. */
                Nim_txtBytes.Text = Nim_ullFixedBytes.ToString("N0") + " bytes";
                return;
            }

            if (!Nim_TypedBytes(out ullBytes))
            {   /* Out of range: say so rather than show a wrong figure. */
                Nim_txtBytes.Text = "too large";
                return;
            }

            /* The typed size in bytes, with thousands separators. */
            Nim_txtBytes.Text = ullBytes.ToString("N0") + " bytes";
        }

        /*--------------------------------------------------------------------*/
        /* Nim_OnSizeTextChanged:                                             */
        /*                                                                    */
        /* The number box's TEXT changed -- a keystroke in it: the bytes      */
        /* field follows the typing at once.  The box's Value does not move   */
        /* until it loses the focus or Enter is pressed (as found), so        */
        /* without this handler the bytes field would lag what is typed.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the number box.                                    */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the bytes field is current.                             */
        /*--------------------------------------------------------------------*/
        private void Nim_OnSizeTextChanged(object objSender, EventArgs evArgs)
        {
            /* Nim_UpdateBytes reads the box's text first (Nim_TypedBytes),   */
            /* so the half-typed figure is what it shows.                     */
            Nim_UpdateBytes();
        }

        /*--------------------------------------------------------------------*/
        /* Nim_OnSizeValueChanged:                                            */
        /*                                                                    */
        /* The number box's VALUE changed -- the arrows, the box accepting    */
        /* what was typed, or the program setting it (a unit change, Match a  */
        /* Device, a mode setter): the bytes field follows.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the number box.                                    */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the bytes field is current.                             */
        /*--------------------------------------------------------------------*/
        private void Nim_OnSizeValueChanged(object objSender, EventArgs evArgs)
        {
            /* The same refresh the text's handler makes; a value set by    */
            /* the program is then shown in bytes without waiting for its   */
            /* caller's own Nim_UpdateBytes.                                */
            Nim_UpdateBytes();
        }

        /*--------------------------------------------------------------------*/
        /* Nim_OnUnitChanged:                                                 */
        /*                                                                    */
        /* A different unit was chosen: the number is CONVERTED to it -- the  */
        /* same size, now counted in the new unit, rounded to the new unit's  */
        /* decimal places -- rather than kept as typed, which would change    */
        /* the size.  A large unit's rounding can lose a few bytes, which the */
        /* bytes field beside it shows at once.                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the unit box.                                      */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the number box and the bytes field fit the new unit.    */
        /*--------------------------------------------------------------------*/
        private void Nim_OnUnitChanged(object objSender, EventArgs evArgs)
        {
            decimal decNumber;
            decimal decBytes;
            decimal decNew;
            int     iUnit;
            int     iOld;

            /* The unit now chosen. */
            iUnit = Nim_cmbUnit.SelectedIndex;

            if (iUnit < 0)
            {   /* Nothing chosen (cannot normally happen). */
                return;
            }

            /* The unit the number was in until now -- the combo box has      */
            /* already moved, so it is remembered separately -- and the new   */
            /* one recorded for next time.  The conversion starts from the    */
            /* exact byte count in the OLD unit, so 64 MiB to bytes is        */
            /* 67108864 and back is 64.00.                                    */
            iOld           = Nim_iUnitShown;
            Nim_iUnitShown = iUnit;

            /* The number as typed (the box's Value lags the typing), in the   */
            /* unit it was typed in.                                           */
            if (!decimal.TryParse(Nim_numSize.Text, System.Globalization.NumberStyles.Number,
                                  System.Globalization.CultureInfo.CurrentCulture, out decNumber) || decNumber < 0)
            {   /* Half-typed or not a number: what the box last accepted. */
                decNumber = Nim_numSize.Value;
            }

            /* The same size in the new unit, rounded to its places. */
            decBytes = decNumber * (decimal)Nim_aullUnitBytes[iOld];
            decNew   = Math.Round(decBytes / (decimal)Nim_aullUnitBytes[iUnit], Nim_aiUnitDecimals[iUnit]);

            if (decNew > NIM_SIZE_MAX)
            {   /* More than the box holds: its largest. */
                decNew = NIM_SIZE_MAX;
            }

            /* The box takes the new unit's places, then the converted   */
            /* value (already rounded to them), then the bytes field.    */
            Nim_numSize.DecimalPlaces = Nim_aiUnitDecimals[iUnit];
            Nim_numSize.Value         = decNew;
            Nim_UpdateBytes();
        }

        /*--------------------------------------------------------------------*/
        /* Nim_Complain:                                                      */
        /*                                                                    */
        /* Tell the user why OK was not accepted, and put the focus on the    */
        /* field to correct.  The dialog stays open.                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sWhy     : the reason.                                         */
        /*     ctlField : the field at fault.                                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : a message was shown and the field has the focus.        */
        /*--------------------------------------------------------------------*/
        private void Nim_Complain(string sWhy, Control ctlField)
        {
            /* The reason in the program's message box, in the dialog's      */
            /* font; then the focus on the field, so the fix starts there.   */
            MSG_clsMessageForm.MSG_Show(this, this.Font, sWhy, "New image", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
            ctlField.Focus();
        }

        /*--------------------------------------------------------------------*/
        /* Nim_OnBrowseClicked:                                               */
        /*                                                                    */
        /* Browse: the dialog ends with the typed fields, for the sequence    */
        /* that asked it to ask the file with the standard save dialog        */
        /* (Sys_AskNewImageFile, Qpr_NewImagePath).  Nothing is opened here.  */
        /* The button is off in partition mode, so it is pressed only in New  */
        /* Image's dialog.                                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the dialog is closing.                                  */
        /*--------------------------------------------------------------------*/
        private void Nim_OnBrowseClicked(object objSender, EventArgs evArgs)
        {
            /* The fields recorded and the dialog closed, Browse named. */
            Nim_EndForOutside(true);
        }

        /*--------------------------------------------------------------------*/
        /* Nim_OnOkClicked:                                                   */
        /*                                                                    */
        /* Create (Format in partition mode), or Enter, the dialog's accept   */
        /* key: asks for the answers to be checked and taken.  The handler    */
        /* only posts: the checks can show a refusal box or the replace       */
        /* question, which are never shown from a handler.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : an Accept item posted to the dialog's queue.            */
        /*--------------------------------------------------------------------*/
        private void Nim_OnOkClicked(object objSender, EventArgs evArgs)
        {
            /* Posted to the dialog's own queue; a second press while one   */
            /* waits is dropped there.                                      */
            Nim_objQueue.WQU_Post((int)Nim_enmItem.Accept, null);
        }

        /*--------------------------------------------------------------------*/
        /* Nim_RunItem:                                                       */
        /*                                                                    */
        /* The dialog's queue runner: one posted item at a time, after the    */
        /* handler that posted it has returned, so a refusal box or the       */
        /* replace question is shown from here and never from inside a        */
        /* handler.                                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iItem   : the Nim_enmItem posted.                              */
        /*     objArgs : the item's argument (none for Accept).               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item's work done.                                   */
        /*--------------------------------------------------------------------*/
        private void Nim_RunItem(int iItem, object objArgs)
        {
            switch ((Nim_enmItem)iItem)
            {   /* Which work was asked for. */
                case Nim_enmItem.Accept:
                    /* The answers checked and, when all stand, taken. */
                    Nim_AcceptAnswers();
                    break;
                default:
                    /* No such item (a guard: only OK's handler posts):   */
                    /* nothing done.                                      */
                    break;
            }
        }

        /*--------------------------------------------------------------------*/
        /* Nim_AcceptAnswers:                                                 */
        /*                                                                    */
        /* OK's work, run from the dialog's queue: check every answer, in the */
        /* order the fields appear, and close only when all of them stand.    */
        /* The checks repeat what the engine would refuse, so the user is     */
        /* told here, in words, beside the field -- not afterwards by a       */
        /* failed operation.  An existing file is replaced only on a clear    */
        /* Yes to the replace question asked here.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the outcome fields are set and the dialog closes, or a  */
        /*            message says what to correct and it stays open.         */
        /*--------------------------------------------------------------------*/
        private void Nim_AcceptAnswers()
        {
            string sPath;
            string sFolder;
            string sLabel;
            ulong  ullBytes;
            uint   uiType;
            uint   uiBlock;
            bool   bOverwrite;

            if (Nim_bFixed)
            {   /* A partition: the place and the size were fixed by   */
                /* NIM_FixTarget and are not checked as a file path    */
                /* would be.                                           */
                sPath    = Nim_sFixedTarget;
                ullBytes = Nim_ullFixedBytes;

                if (Nim_bSmallerAllowed)
                {   /* A whole disk: the typed size, no more than the disk   */
                    /* and no less than the engine's ENG_MAKEFS_MIN_BYTES.   */
                    if (!Nim_TypedBytes(out ullBytes) || ullBytes > Nim_ullFixedBytes)
                    {   /* Past the end of the disk. */
                        Nim_Complain("The size is more than the disk holds (" + DVF_clsDeviceForm.DVF_SizeText(Nim_ullFixedBytes) + ").", Nim_numSize);
                        return;
                    }

                    if (ullBytes < ENG_clsNative.ENG_MAKEFS_MIN_BYTES)
                    {   /* Below the engine's floor. */
                        Nim_Complain("A filesystem needs at least 1 MiB.", Nim_numSize);
                        return;
                    }
                }
            }
            else
            {   /* A new file: the path and the size come from the boxes. */
                sPath = Nim_txtPath.Text.Trim();

                try
                {   /* A malformed path throws rather than returning null. */
                    if (sPath.Length == 0 || !Path.IsPathRooted(sPath) || Path.GetFileName(sPath).Length == 0)
                    {   /* Nothing typed, a relative path, or a folder with no file name. */
                        Nim_Complain("Give the image file's full path: its drive or share, its folder and its file name.", Nim_txtPath);
                        return;
                    }

                    /* The path made full (a "." or ".." resolved) and its   */
                    /* folder taken off it, for the checks below; either     */
                    /* call throws on a path Windows cannot take.            */
                    sPath   = Path.GetFullPath(sPath);
                    sFolder = Path.GetDirectoryName(sPath);
                }
                catch (Exception)
                {   /* A path Windows cannot take: characters it does not        */
                    /* allow, a colon past the drive, or one too long -- every   */
                    /* exception the path calls above raise is taken as "not     */
                    /* a valid path", since none can be fixed but by retyping.   */
                    Nim_Complain("That is not a valid Windows path.", Nim_txtPath);
                    return;
                }

                if (sFolder == null || !Directory.Exists(sFolder))
                {   /* The folder has to be there; this dialog does not create folders. */
                    Nim_Complain("The folder does not exist:\n" + sFolder, Nim_txtPath);
                    return;
                }

                if (Directory.Exists(sPath))
                {   /* A folder of that name is in the way. */
                    Nim_Complain("That name is a folder, not a file.", Nim_txtPath);
                    return;
                }

                /* The size: the number in its unit, in whole sectors. */
                if (!Nim_TypedBytes(out ullBytes))
                {   /* More than an image may be. */
                    Nim_Complain("That size is too large; the most this program makes is " + DVF_clsDeviceForm.DVF_SizeText(NIM_BYTES_MAX) + ".", Nim_numSize);
                    return;
                }
            }

            if (ullBytes < ENG_clsNative.ENG_MAKEFS_MIN_BYTES)
            {   /* Too small for a filesystem (a fraction, or a few bytes typed). */
                Nim_Complain("The image must be at least 1 MiB (1,048,576 bytes).", Nim_numSize);
                return;
            }

            /* The type and the block size, and the one pair that cannot be   */
            /* made.  In size-only mode the rows are gone and the boxes sit   */
            /* at their defaults, so this check passes and the values are     */
            /* handed back unread.                                            */
            uiType  = Nim_TypeFromIndex(Nim_cmbType.SelectedIndex);
            uiBlock = Nim_BlockFromIndex(Nim_cmbBlock.SelectedIndex, Nim_bFatShown);

            if (!Nim_bSizeOnly && uiType == ENG_clsNative.ENG_FSTYPE_EXT4 && uiBlock == 1024 && ullBytes < NIM_EXT4_1K_MIN)
            {   /* The engine refuses this combination. */
                Nim_Complain("ext4 with 1024-byte blocks needs at least 8 MiB.\nChoose Automatic or 2048 bytes, or a larger size.", Nim_cmbBlock);
                return;
            }

            /* The label.  On an ext type it is at most ENG_LABEL_MAX_BYTES    */
            /* once encoded, which can be fewer characters than bytes; a FAT   */
            /* or exFAT one is held to its own 11 characters below             */
            /* (Nim_FatLabelProblem), as Windows holds it -- 33 bytes of       */
            /* UTF-8 at the most, which the options' label field               */
            /* (ENG_LABEL_FIELD) holds, so no byte limit is applied to it.     */
            sLabel = Nim_txtLabel.Text.Trim();

            if (!Nim_IsFatType(uiType) && System.Text.Encoding.UTF8.GetByteCount(sLabel) > ENG_clsNative.ENG_LABEL_MAX_BYTES)
            {   /* An ext label too long for the superblock's 16 bytes. */
                Nim_Complain("The volume label is limited to 16 bytes; accented and non-Latin letters take more than one.", Nim_txtLabel);
                return;
            }

            if (!Nim_bSizeOnly && Nim_IsFatType(uiType) && Nim_FatLabelProblem(sLabel, uiType).Length != 0)
            {   /* A FAT type, and a label the engine would refuse for it:   */
                /* said here, with the rule, before anything is formatted.   */
                Nim_Complain(Nim_FatLabelProblem(sLabel, uiType), Nim_txtLabel);
                return;
            }

            /* An existing file is replaced only on a clear Yes (a partition   */
            /* is a place, not a file: its own question is asked elsewhere).   */
            bOverwrite = false;

            if (!Nim_bFixed && File.Exists(sPath))
            {   /* Something is there already. */
                if (MSG_clsMessageForm.MSG_Show(this, this.Font, sPath + "\n\nalready exists. REPLACE it with a new, empty image?\n\nEverything in the existing file will be lost.",
                                             "Replace the file?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                                             MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {   /* Keep the file; let the user pick another name. */
                    Nim_txtPath.Focus();
                    return;
                }

                /* A clear Yes: the caller replaces the file. */
                bOverwrite = true;
            }

            /* Every answer stands: the outcome fields for the caller        */
            /* (CommandQuestionPresenter.cs: New Image's question,           */
            /* Qpr_NewImageFile, or the format's options question,           */
            /* Qpr_MakeFsOptions), the zero-fill only in partition           */
            /* mode (its box is gone in size-only mode), accepted, and the   */
            /* dialog closed.                                                */
            NIM_sImagePath  = sPath;
            NIM_ullBytes    = ullBytes;
            NIM_uiFsType    = uiType;
            NIM_uiBlockSize = uiBlock;
            NIM_sLabel      = sLabel;
            NIM_bOverwrite  = bOverwrite;
            NIM_bZeroFill   = (Nim_bFixed && Nim_chkZero.Checked);
            NIM_bAccepted   = true;
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Nim_OnCancelClicked:                                               */
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
        private void Nim_OnCancelClicked(object objSender, EventArgs evArgs)
        {
            /* Closed with NIM_bAccepted still false: the caller makes   */
            /* nothing.                                                  */
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Nim_OnResize:                                                      */
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
        private void Nim_OnResize(object objSender, EventArgs evArgs)
        {
            /* Detached for the refit, whose own ClientSize change would   */
            /* otherwise come straight back here; hooked again after.      */
            this.Resize -= Nim_OnResize;
            Nim_FitLayout();
            this.Resize += Nim_OnResize;
        }
    }

    /*------------------------------------------------------------------------*/
    /* NBF_clsBusyForm -- the "working" box shown while the engine works with */
    /* no progress to report; it closes when the progress windows say the     */
    /* work has ended (NBF_Finish).                                           */
    /*                                                                        */
    /* Derives from Form because the framework requires it: WinForms makes a  */
    /* window only as a Form subclass.  That is the one kind of inheritance   */
    /* allowed here -- from a framework base class, never from a class of     */
    /* ours.                                                                  */
    /*------------------------------------------------------------------------*/
    internal sealed class NBF_clsBusyForm : Form
    {
        /* The box's sizes -- documentation table. */
        private const int NBF_BUSY_WIDTH   = 480;    // client width, pixels
        private const int NBF_BUSY_PAD     = 12;     // padding round the content, pixels
        private const int NBF_BUSY_BAR     = 18;     // the moving bar's height, pixels
        private const int NBF_BUSY_SPEED   = 30;     // the marquee's animation step, milliseconds

        /* Its two controls and its one flag -- documentation table. */
        private Label       Nbf_lblWhat;       // what is being done
        private ProgressBar Nbf_prgBar;        // a marquee: the work has no measurable progress
        private bool        Nbf_bFinished;     // true once the work is done; until then the box refuses to close

        /*--------------------------------------------------------------------*/
        /* NBF_clsBusyForm (constructor):                                     */
        /*                                                                    */
        /* Build the box: one line of text over a moving bar, no buttons and  */
        /* no close box.                                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     fntDialog : the program's dialog font (the caller's; not       */
        /*                 freed here).                                       */
        /*     sTitle    : the caption.                                       */
        /*     sWhat     : the line saying what is being done.                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the box, not yet shown.                        */
        /*--------------------------------------------------------------------*/
        internal NBF_clsBusyForm(Font fntDialog, string sTitle, string sWhat)
        {
            /* Not finished: the box refuses to close until NBF_Finish. */
            Nbf_bFinished = false;

            /* The caller's dialog font, as every dialog of the program has it. */
            this.Font = fntDialog;

            /* A fixed dialog with the caller's caption and NO close box --   */
            /* there is nothing to cancel -- centred on its owner, out of     */
            /* the taskbar.                                                   */
            this.Text            = sTitle;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.ControlBox      = false;
            this.ShowInTaskbar   = false;
            this.StartPosition   = FormStartPosition.CenterParent;
            this.Padding         = new Padding(NBF_BUSY_PAD);

            /* The bar: a marquee, moving on its own, since the work it   */
            /* stands for reports no progress.                            */
            Nbf_prgBar                       = new ProgressBar();
            Nbf_prgBar.Dock                  = DockStyle.Top;
            Nbf_prgBar.Height                = NBF_BUSY_BAR;
            Nbf_prgBar.Style                 = ProgressBarStyle.Marquee;
            Nbf_prgBar.MarqueeAnimationSpeed = NBF_BUSY_SPEED;

            /* The line saying what is being done: one line, its height set   */
            /* below once it has the dialog's font, a long one cut with an    */
            /* ellipsis rather than wrapped.                                  */
            Nbf_lblWhat              = new Label();
            Nbf_lblWhat.Dock         = DockStyle.Top;
            Nbf_lblWhat.AutoSize     = false;
            Nbf_lblWhat.AutoEllipsis = true;
            Nbf_lblWhat.Text         = sWhat;
            Nbf_lblWhat.TextAlign    = ContentAlignment.MiddleLeft;

            /* Docking runs in reverse order of adding: the text ends up above the bar. */
            this.Controls.Add(Nbf_prgBar);
            this.Controls.Add(Nbf_lblWhat);

            /* The line's height is measured only now, once the label has its   */
            /* parent: before that it reports the small default font's height   */
            /* and the larger text would be clipped.                            */
            Nbf_lblWhat.Height = Nbf_lblWhat.PreferredHeight + NBF_BUSY_PAD;

            /* The box sized to the line, the bar and the pads; then the   */
            /* close guard hooked.                                         */
            this.ClientSize   = new Size(NBF_BUSY_WIDTH, Nbf_lblWhat.Height + Nbf_prgBar.Height + (2 * NBF_BUSY_PAD));
            this.FormClosing += Nbf_OnBusyClosing;
        }

        /*--------------------------------------------------------------------*/
        /* NBF_Finish:                                                        */
        /*                                                                    */
        /* The work is done: let the box close, and close it.  Called on the  */
        /* UI thread by the progress windows when the command's progress      */
        /* window is to close (CommandProgressWindows.cs); the box itself     */
        /* never knows when the work ends.                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the box closes, and its ShowDialog returns.             */
        /*--------------------------------------------------------------------*/
        internal void NBF_Finish()
        {
            /* The flag FIRST: Close raises FormClosing, which                */
            /* Nbf_OnBusyClosing refuses until the flag is set -- the other   */
            /* order would leave the box up for good.                         */
            Nbf_bFinished = true;
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Nbf_OnBusyClosing:                                                 */
        /*                                                                    */
        /* Refuse to close while the work is still running (Alt+F4 would      */
        /* otherwise hand control back to a window whose engine is busy).     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the box.                                           */
        /*     evClosing : the closing event; cancelled until the work ends.  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : evClosing.Cancel says whether the box may close.        */
        /*--------------------------------------------------------------------*/
        private void Nbf_OnBusyClosing(object objSender, FormClosingEventArgs evClosing)
        {
            if (!Nbf_bFinished)
            {   /* Still working: the close is refused.  The box offers no   */
                /* Cancel, because the work cannot be stopped part-way, so   */
                /* only NBF_Finish, once the work is over, closes it.        */
                evClosing.Cancel = true;
            }
        }
    }
}
