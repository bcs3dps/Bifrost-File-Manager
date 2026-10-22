/*----------------------------------------------------------------------------*/
/* PropertiesForm.cs                                                          */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The properties of one entry in the image: its facts (type, size, inode,    */
/* link count, the three times, a symlink's target) shown read-only, and the  */
/* three things that can be changed here -- the permission bits, the owner    */
/* and the group -- in text boxes.                                            */
/*                                                                            */
/* The permission bits are shown TWICE: as the octal number, and broken out   */
/* into a grid of check boxes (owner / group / others by read / write /       */
/* execute, plus set-user-id, set-group-id and sticky) with the symbolic form */
/* ("rwxr-xr-x") beside them.  The two views are kept in step both ways: a    */
/* box ticked rewrites the octal text, and octal text that parses rewrites    */
/* the boxes.  Which view is edited is the user's choice; what is handed back */
/* is the octal box's value, so the boxes never carry a state of their own.   */
/*                                                                            */
/* An entry on a FAT or exFAT session has none of the three: FAT keeps no     */
/* mode, owner, group, inode or link count, and one time.  Its dialog shows   */
/* the path, type, size, that time and FAT's own attributes (read-only,       */
/* hidden, system, archive), read-only, with one Close button.                */
/*                                                                            */
/* The dialog only COLLECTS the new values.  It validates them when OK is     */
/* pressed and hands them back in fields; the main window                     */
/* (Main_RenderPropertyInput, MainCommand.cs) reads them, decides whether     */
/* anything changed, and posts the change to the command worker, which makes  */
/* it.  DialogResult is not used: PROP_bAccepted says whether OK was pressed  */
/* with valid values.                                                         */
/*                                                                            */
/* The layout is a two-column table (caption, value) that sizes itself to its */
/* content, over a row of buttons; nothing carries a coordinate.  The grid of */
/* boxes is one row of that table spanning both columns.                      */
/*                                                                            */
/* Its text is in the program's dialog font, which the caller passes in (the  */
/* dialog neither chooses nor frees it); the symbolic form is in a bold       */
/* fixed-pitch face PROP_SYMBOLIC_SCALE times that size, made here from it    */
/* and released when the dialog is disposed.                                  */
/*----------------------------------------------------------------------------*/

/* The framework: the base types (Convert for the octal text, event         */
/* arguments), fonts, sizes and colours, and the dialog and its controls.   */
using System;
using System.Drawing;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* PROP_clsPropertiesForm -- one image entry's Properties dialog (the     */
    /* file header describes it).  A plain Form: its base class is declared   */
    /* here with the rest of its UI.                                          */
    /*                                                                        */
    /* Derives from Form because the framework requires it: WinForms makes a  */
    /* window only as a Form subclass.  That is the one kind of inheritance   */
    /* allowed here -- from a framework base class, never from a class of     */
    /* ours.                                                                  */
    /*------------------------------------------------------------------------*/
    internal sealed class PROP_clsPropertiesForm : Form
    {
        /* The dialog's sizes -- documentation table. */
        private const int PROP_PAD         = 12;        // padding round the content, pixels
        private const int PROP_MIN_WIDTH   = 420;       // least client width, pixels
        private const int PROP_EDIT_CHARS  = 12;        // width of the three edit boxes, in average characters
        private const int PROP_MODE_DIGITS = 4;         // the octal box always shows this many digits (0644, 4755)
        private const int PROP_VALUE_SCREEN_SHARE = 2;  // a fact's value is at most the screen's working width divided by this; a longer one wraps (Prop_AddFact)
        private const int PROP_VALUE_LINES = 6;         // the most text lines a wrapped value shows before it scrolls

        /* The permission word's layout, as ext4 (and every Unix) stores it:   */
        /* three special bits above three groups of three rights, read /       */
        /* write / execute, for the owner, the group and everyone else.        */
        private const int  PROP_CLASSES    = 3;           // owner, group, others -- the three who-columns of the grid
        private const int  PROP_RIGHTS     = 3;           // read, write, execute -- the three what-columns of the grid
        private const int  PROP_SPECIALS   = 3;           // set-user-id, set-group-id, sticky
        private const uint PROP_BIT_SETUID = 0x800;       // 04000: run as the file's owner (a directory: ignored)
        private const uint PROP_BIT_SETGID = 0x400;       // 02000: run as the file's group; a directory: new entries take its group
        private const uint PROP_BIT_STICKY = 0x200;       // 01000: a directory: only an entry's owner may remove it
        private const uint PROP_MODE_MASK  = 0xFFF;       // 07777: every bit the dialog may set
        private const float PROP_SYMBOLIC_SCALE = 1.25f;  // the symbolic form's font size, as a multiple of the dialog's

        /* The answer, read by the main window once the dialog has closed   */
        /* (Main_RenderPropertyInput, MainCommand.cs) -- documentation      */
        /* table.                                                           */
        internal bool PROP_bAccepted;                // true when OK was pressed and every value parsed
        internal uint PROP_uiMode;                   // the permission bits typed, valid when PROP_bAccepted
        internal uint PROP_uiUid;                    // the owner id typed, valid when PROP_bAccepted
        internal uint PROP_uiGid;                    // the group id typed, valid when PROP_bAccepted

        /* The controls and the two views' state -- documentation table. */
        private TableLayoutPanel Prop_tblFacts;      // the caption / value rows
        private TextBox          Prop_txtMode;       // permission bits, octal
        private TextBox          Prop_txtUid;        // owner id, decimal
        private TextBox          Prop_txtGid;        // group id, decimal
        private GroupBox         Prop_grpBits;       // the permission grid's frame, one table row spanning both columns
        private TableLayoutPanel Prop_tblBits;       // the grid: who down the side, read / write / execute across
        private CheckBox[]       Prop_achkRwx;       // the nine right boxes, indexed class * PROP_RIGHTS + right
        private CheckBox[]       Prop_achkSpecial;   // the three special-bit boxes: set-user-id, set-group-id, sticky
        private Label            Prop_lblSymbolic;   // the mode in ls form, "-rwxr-xr-x", following the boxes
        private char             Prop_cTypeChar;     // the entry's type as ls shows it in the first column: '-', 'd', 'l', 'c', 'b', 'p', 's'
        private bool             Prop_bSyncing;      // true while one view is being written from the other, so the write does not echo back
        private Label            Prop_lblError;      // says which value would not parse; empty otherwise
        private FlowLayoutPanel  Prop_pnlButtons;    // OK and Cancel, right-aligned
        private Button           Prop_btnOk;         // validates and accepts
        private Button           Prop_btnCancel;     // abandons
        private Font             Prop_fntSymbolic;   // the symbolic form's bold fixed-pitch font, PROP_SYMBOLIC_SCALE of the dialog's; owned here, disposed with the dialog

        /*--------------------------------------------------------------------*/
        /* PROP_clsPropertiesForm (constructor):                              */
        /*                                                                    */
        /* Build the dialog for one entry, from facts already read.  It only  */
        /* collects the new mode, owner and group; changing the entry is the  */
        /* caller's, once the dialog has been accepted.  For an entry on a    */
        /* FAT or exFAT session it only shows: FAT keeps none of the three.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     fntDialog  : the program's dialog font (the caller's; not      */
        /*                  freed here).                                      */
        /*     sImagePath : the entry's path in the image, shown as the first */
        /*                  row (the caption is the fixed "Properties").      */
        /*     stEntry    : the entry's facts.                                */
        /*     sTypeText  : its type, as the main list words it.              */
        /*     sTarget    : a symlink's target, or "" for anything else.      */
        /*     sAccessed  : its access time, formatted (not shown on FAT).    */
        /*     sModified  : its modify time, formatted.                       */
        /*     sChanged   : its inode-change time, formatted (not shown on    */
        /*                  FAT).                                             */
        /*     bFat       : true when the entry is on a FAT or exFAT          */
        /*                  session: its attributes are shown and nothing can */
        /*                  be changed.                                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the dialog, not yet shown.                     */
        /*--------------------------------------------------------------------*/
        internal PROP_clsPropertiesForm(Font fntDialog, string sImagePath, EEN_stEntry stEntry, string sTypeText, string sTarget,
                                     string sAccessed, string sModified, string sChanged, bool bFat)
        {
            int iNeeded;

            /* Not accepted until OK says so; the answer fields start at     */
            /* the entry's own values; and the entry's type character, for   */
            /* the symbolic form's first column.                             */
            PROP_bAccepted = false;
            PROP_uiMode    = stEntry.EEN_uiMode;
            PROP_uiUid     = stEntry.EEN_uiUid;
            PROP_uiGid     = stEntry.EEN_uiGid;
            Prop_cTypeChar = Prop_TypeChar(stEntry.EEN_uiType);

            /* The caller's dialog font, set before any control is made (the   */
            /* edit boxes and the symbolic form are measured from it).  The    */
            /* symbolic form's own font is made later, and released with the   */
            /* dialog.                                                         */
            this.Font        = fntDialog;
            Prop_fntSymbolic = null;
            this.Disposed   += Prop_OnDisposed;

            /* A fixed dialog centred on the main window, out of the    */
            /* taskbar, that sizes itself to its content but is never   */
            /* narrower than PROP_MIN_WIDTH.                            */
            this.Text            = "Properties";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox     = false;
            this.MinimizeBox     = false;
            this.ShowInTaskbar   = false;
            this.StartPosition   = FormStartPosition.CenterParent;
            this.Padding         = new Padding(PROP_PAD);
            this.AutoSize        = true;
            this.AutoSizeMode    = AutoSizeMode.GrowAndShrink;
            this.MinimumSize     = new Size(PROP_MIN_WIDTH, 0);

            /* The table: captions in the first column, values in the       */
            /* second, both sized to their widest cell; docked to the top   */
            /* and growing with its rows, which is what sizes the dialog.   */
            Prop_tblFacts              = new TableLayoutPanel();
            Prop_tblFacts.Dock         = DockStyle.Top;
            Prop_tblFacts.AutoSize     = true;
            Prop_tblFacts.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Prop_tblFacts.ColumnCount  = 2;
            Prop_tblFacts.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Prop_tblFacts.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            /* The read-only facts, from the top: where the entry is, and   */
            /* what kind of entry it is in the main list's own words.       */
            Prop_AddFact("Path:", sImagePath);
            Prop_AddFact("Type:", sTypeText);

            if (sTarget.Length != 0)
            {   /* A symbolic link: where it points. */
                Prop_AddFact("Points to:", sTarget);
            }

            /* No view is being written from the other yet. */
            Prop_bSyncing = false;

            if (bFat)
            {   /* A FAT or exFAT entry.  FAT has no inode, link count, mode,   */
                /* owner or group: the engine reports made-up values for        */
                /* them (0644 / 0755, ids 0, one link, inode 0) and refuses     */
                /* to set a mode or an owner.  It keeps one time, the modify    */
                /* time, which the engine copies into all three.  So the        */
                /* size, that one time and the attributes FAT does keep are     */
                /* shown, read-only, and nothing is offered for editing --      */
                /* the edit boxes, the grid and OK are not made, and the        */
                /* answer stays "not accepted", so the caller changes           */
                /* nothing.                                                     */
                Prop_AddFact("Size:", DVF_clsDeviceForm.DVF_SizeText(stEntry.EEN_ullSize));
                Prop_AddFact("Modified:", sModified);
                Prop_AddFact("Attributes:", Prop_AttributesText(stEntry.EEN_uiAttributes));
            }
            else
            {   /* An ext2/3/4 entry: every fact, and the three values that   */
                /* can be changed.                                            */

                /* The size in the program's one written form, as every       */
                /* dialog gives it (PartitionForm.cs's DVF_SizeText); then    */
                /* the inode number, the link count and the three times, as   */
                /* the caller formatted them.                                 */
                Prop_AddFact("Size:", DVF_clsDeviceForm.DVF_SizeText(stEntry.EEN_ullSize));
                Prop_AddFact("Inode:", stEntry.EEN_uiInode.ToString());
                Prop_AddFact("Links:", stEntry.EEN_uiLinks.ToString());
                Prop_AddFact("Modified:", sModified);
                Prop_AddFact("Accessed:", sAccessed);
                Prop_AddFact("Changed:", sChanged);

                /* The three editable values.  The permission grid goes      */
                /* straight under the octal box it mirrors, before the two   */
                /* ids.                                                      */
                Prop_txtMode = Prop_AddEdit("Mode (octal):", Prop_OctalText(stEntry.EEN_uiMode));
                Prop_BuildBits();
                Prop_txtUid  = Prop_AddEdit("Owner (uid):", stEntry.EEN_uiUid.ToString());
                Prop_txtGid  = Prop_AddEdit("Group (gid):", stEntry.EEN_uiGid.ToString());

                /* The boxes start from the entry's mode, and from here on     */
                /* each view follows the other.  The octal box is wired only   */
                /* now, so the initial text above did not run the handler      */
                /* on half-built controls.                                     */
                Prop_BoxesFromMode(stEntry.EEN_uiMode);
                Prop_txtMode.TextChanged += Prop_OnModeTextChanged;
            }

            /* The error line, under the table; empty until OK finds a fault. */
            Prop_lblError           = new Label();
            Prop_lblError.Dock      = DockStyle.Top;
            Prop_lblError.AutoSize  = false;
            Prop_lblError.ForeColor = Color.Firebrick;
            Prop_lblError.TextAlign = ContentAlignment.MiddleLeft;
            Prop_lblError.Text      = "";

            /* Cancel, made first: the button row below flows right to left,   */
            /* so the first added is the rightmost, where Cancel goes.         */
            Prop_btnCancel          = new Button();
            Prop_btnCancel.Text     = "Cancel";
            Prop_btnCancel.AutoSize = true;
            Prop_btnCancel.Click   += Prop_OnCancelClicked;

            if (bFat)
            {   /* Nothing to change on FAT, so nothing to cancel: the one   */
                /* button only closes the dialog.                            */
                Prop_btnCancel.Text = "Close";
            }

            /* OK, left of Cancel: it validates before it closes. */
            Prop_btnOk          = new Button();
            Prop_btnOk.Text     = "OK";
            Prop_btnOk.AutoSize = true;
            Prop_btnOk.Click   += Prop_OnOkClicked;

            /* Their row, under the error line, flowing right to left so   */
            /* Cancel, added first, is rightmost.                          */
            Prop_pnlButtons               = new FlowLayoutPanel();
            Prop_pnlButtons.Dock          = DockStyle.Top;
            Prop_pnlButtons.FlowDirection = FlowDirection.RightToLeft;
            Prop_pnlButtons.AutoSize      = true;
            Prop_pnlButtons.Controls.Add(Prop_btnCancel);

            if (!bFat)
            {   /* An ext entry: OK beside Cancel.  On FAT it is made but    */
                /* never shown, so it cannot be pressed -- its handler       */
                /* reads the edit boxes, which a FAT dialog does not have.   */
                Prop_pnlButtons.Controls.Add(Prop_btnOk);
            }

            /* All three are docked to the top, so they stack; docking runs in   */
            /* reverse order of adding (the framework's rule, as found),         */
            /* hence the bottom-most is added first.                             */
            this.Controls.Add(Prop_pnlButtons);
            this.Controls.Add(Prop_lblError);
            this.Controls.Add(Prop_tblFacts);

            /* The error line's height is measured only now, once it has its   */
            /* parent and so the dialog's font; before, it would be the        */
            /* default's and clip a message.                                   */
            Prop_lblError.Height = Prop_lblError.PreferredHeight + PROP_PAD;

            /* The dialog at least as wide as the facts table wants: the       */
            /* table's preferred width (its captions and its widest value,     */
            /* a wrapped one's box included -- Prop_AddFact holds that to a    */
            /* share of the screen), the padding either side, and the          */
            /* frame.  The dialog sizes itself to its content, but the table   */
            /* is docked, so the dialog lays it out at its own width rather    */
            /* than the table's, and without this floor a wrapped value's      */
            /* box is clipped at the dialog's edge.  The fixed floor,          */
            /* PROP_MIN_WIDTH, still holds when the table wants less.          */
            iNeeded = Prop_tblFacts.PreferredSize.Width + this.Padding.Horizontal + (this.Width - this.ClientSize.Width);

            if (iNeeded > this.MinimumSize.Width)
            {   /* The table wants more than the fixed floor: that is the   */
                /* floor, and the dialog grows to it.                       */
                this.MinimumSize = new Size(iNeeded, 0);
            }

            /* Enter presses OK (which validates), Escape presses Cancel. */
            this.AcceptButton = Prop_btnOk;
            this.CancelButton = Prop_btnCancel;

            if (bFat)
            {   /* No OK on FAT: Enter closes, as Escape does.  The form's   */
                /* accept key would otherwise press a button that is not     */
                /* there and run its handler on edit boxes that do not       */
                /* exist.                                                    */
                this.AcceptButton = Prop_btnCancel;
            }
            /* A resize re-fits the error line's height (Prop_OnResize). */
            this.Resize      += Prop_OnResize;
        }

        /*--------------------------------------------------------------------*/
        /* Prop_AddFact:                                                      */
        /*                                                                    */
        /* Add one read-only row to the table: a caption and its value.  A    */
        /* value too wide for its share of the screen wraps, and scrolls      */
        /* past PROP_VALUE_LINES lines, so a long path or link target never   */
        /* widens the dialog off the screen.                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sCaption : the left-hand text.                                 */
        /*     sValue   : the right-hand text.                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the row is in the table.                                */
        /*--------------------------------------------------------------------*/
        private void Prop_AddFact(string sCaption, string sValue)
        {
            Label lblCaption;
            Label lblValue;
            TextBox txtValue;
            Size sizWrapped;
            int iWrap;
            int iMaxHeight;

            /* The caption, sized to its text, with a third of a pad above   */
            /* and below (the rows' spacing) and a pad to its right, the     */
            /* gap between the columns.                                      */
            lblCaption          = new Label();
            lblCaption.Text     = sCaption;
            lblCaption.AutoSize = true;
            lblCaption.Margin   = new Padding(0, PROP_PAD / 3, PROP_PAD, PROP_PAD / 3);

            /* The widest a value may be: a share of the working area of the   */
            /* screen holding the dialog's present bounds (it is centred on    */
            /* its owner only when shown), so the dialog -- which sizes        */
            /* itself to its content -- stays on the screen with its OK and    */
            /* Cancel whatever the path or link target.                        */
            iWrap = Screen.GetWorkingArea(this).Width / PROP_VALUE_SCREEN_SHARE;

            /* The caption first: a control added to a table goes in the   */
            /* next free cell, so the pair fills one row, caption then     */
            /* value.                                                      */
            Prop_tblFacts.Controls.Add(lblCaption);

            if (TextRenderer.MeasureText(sValue, this.Font).Width <= iWrap)
            {   /* Fits on one line: the value as a plain label, sized to   */
                /* its text, as every short fact is.                        */
                lblValue          = new Label();
                lblValue.Text     = sValue;
                lblValue.AutoSize = true;
                lblValue.Margin   = new Padding(0, PROP_PAD / 3, 0, PROP_PAD / 3);
                Prop_tblFacts.Controls.Add(lblValue);
                return;
            }

            /* Longer: a read-only text box with no border on the dialog's    */
            /* own colour, so it reads like the labels, held to the width     */
            /* and wrapping.  A text box (not a wrapped label) because a      */
            /* path has few spaces: an edit control breaks a word too long    */
            /* for the line between its characters, where a label's word      */
            /* break leaves it running off its edge -- and the text can be    */
            /* selected and copied.  Its height is what the wrapped text      */
            /* needs, measured as an edit control lays it out, but never      */
            /* more than PROP_VALUE_LINES lines: past that it scrolls, so a   */
            /* value thousands of characters long cannot push the buttons     */
            /* off the screen either.                                         */
            txtValue             = new TextBox();
            txtValue.Text        = sValue;
            txtValue.Multiline   = true;
            txtValue.ReadOnly    = true;
            txtValue.WordWrap    = true;
            txtValue.TabStop     = false;
            txtValue.BorderStyle = BorderStyle.None;
            txtValue.BackColor   = this.BackColor;
            txtValue.Margin      = new Padding(0, PROP_PAD / 3, 0, PROP_PAD / 3);
            sizWrapped = TextRenderer.MeasureText(sValue, this.Font, new Size(iWrap, 0),
                                                  TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            iMaxHeight = TextRenderer.MeasureText("X", this.Font).Height * PROP_VALUE_LINES;
            txtValue.Size = new Size(iWrap, sizWrapped.Height);
            if (sizWrapped.Height > iMaxHeight)
            {   /* Too many lines: held to PROP_VALUE_LINES, the rest   */
                /* reached by its scroll bar.                           */
                txtValue.Size       = new Size(iWrap, iMaxHeight);
                txtValue.ScrollBars = ScrollBars.Vertical;
            }

            /* The value in the row's second cell. */
            Prop_tblFacts.Controls.Add(txtValue);
        }

        /*--------------------------------------------------------------------*/
        /* Prop_AddEdit:                                                      */
        /*                                                                    */
        /* Add one editable row to the table: a caption and a text box sized  */
        /* to the dialog's font.                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sCaption : the left-hand text.                                 */
        /*     sValue   : the text the box starts with.                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     TextBox : the box, so the caller can read it back.             */
        /*--------------------------------------------------------------------*/
        private TextBox Prop_AddEdit(string sCaption, string sValue)
        {
            Label   lblCaption;
            TextBox txtValue;

            /* The caption, as Prop_AddFact's, but with half a pad above    */
            /* rather than a third (why is not recorded; the effect is to   */
            /* lower it toward the text inside the taller box beside it).   */
            lblCaption          = new Label();
            lblCaption.Text     = sCaption;
            lblCaption.AutoSize = true;
            lblCaption.Margin   = new Padding(0, PROP_PAD / 2, PROP_PAD, PROP_PAD / 3);

            /* The box, holding its starting value. */
            txtValue        = new TextBox();
            txtValue.Text   = sValue;

            /* PROP_EDIT_CHARS digits wide -- from the font, not a pixel   */
            /* constant -- measured in the DIALOG's font: the box has no   */
            /* parent yet, so its own Font is still the small default.     */
            txtValue.Width  = TextRenderer.MeasureText(new string('0', PROP_EDIT_CHARS), this.Font).Width;
            txtValue.Margin = new Padding(0, PROP_PAD / 4, 0, PROP_PAD / 4);

            /* The pair as one row of the table. */
            Prop_tblFacts.Controls.Add(lblCaption);
            Prop_tblFacts.Controls.Add(txtValue);

            /* The caller keeps the box to read the value from. */
            return(txtValue);
        }

        /*--------------------------------------------------------------------*/
        /* Prop_BuildBits:                                                    */
        /*                                                                    */
        /* Build the permission grid -- a box for each right of each class,   */
        /* the three special bits, and the symbolic form -- in a frame, and   */
        /* add it to the facts table as one row spanning both columns.  Every */
        /* box reports to one handler, which rewrites the octal text.         */
        /*                                                                    */
        /* The boxes are created UNTICKED here; Prop_BoxesFromMode sets them  */
        /* once the octal box exists, so nothing here depends on the mode.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Prop_grpBits is in the table; Prop_achkRwx,             */
        /*            Prop_achkSpecial and Prop_lblSymbolic are built.        */
        /*--------------------------------------------------------------------*/
        private void Prop_BuildBits()
        {
            string[] asClass;
            string[] asRight;
            string[] asSpecial;
            FlowLayoutPanel pnlSpecial;
            CheckBox chk;
            int iClass;
            int iRight;
            int i;

            /* The grid's words: the three classes down the side, the three   */
            /* rights across the top, the special bits' names on their        */
            /* boxes -- each in the order its index means elsewhere.          */
            asClass   = new string[] { "Owner:", "Group:", "Others:" };
            asRight   = new string[] { "Read", "Write", "Execute" };
            asSpecial = new string[] { "Set user ID", "Set group ID", "Sticky" };

            /* The grid: one caption column and one column per right.  Both   */
            /* dimensions size to content, so a larger font widens it.        */
            Prop_tblBits              = new TableLayoutPanel();
            Prop_tblBits.AutoSize     = true;
            Prop_tblBits.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Prop_tblBits.ColumnCount  = 1 + PROP_RIGHTS;
            Prop_tblBits.Dock         = DockStyle.Fill;

            for (i = 0; i < Prop_tblBits.ColumnCount; i++)
            {   /* One column per iteration: all size to their content. */
                Prop_tblBits.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            }

            /* The heading row: an empty corner, then the three right names. */
            Prop_AddBitsLabel("");

            for (iRight = 0; iRight < PROP_RIGHTS; iRight++)
            {   /* One heading per right. */
                Prop_AddBitsLabel(asRight[iRight]);
            }

            /* The class rows: the class name, then a bare box per right.     */
            /* No box carries its index: the handler reads every box back     */
            /* (Prop_ModeFromBoxes), so it never needs to know which fired.   */
            /* The array is indexed class * PROP_RIGHTS + right.              */
            Prop_achkRwx = new CheckBox[PROP_CLASSES * PROP_RIGHTS];

            for (iClass = 0; iClass < PROP_CLASSES; iClass++)
            {   /* One class per iteration: its caption and its three boxes. */
                Prop_AddBitsLabel(asClass[iClass]);

                for (iRight = 0; iRight < PROP_RIGHTS; iRight++)
                {   /* One right per iteration: a bare box (its column's     */
                    /* heading names it), spaced to sit under the heading,   */
                    /* reporting to the one handler; kept in the array at    */
                    /* its index and placed in the grid's next cell.         */
                    chk                 = new CheckBox();
                    chk.Text            = "";
                    chk.AutoSize        = true;
                    chk.Margin          = new Padding(PROP_PAD / 2, PROP_PAD / 4, PROP_PAD / 2, PROP_PAD / 4);
                    chk.CheckedChanged += Prop_OnBitChanged;

                    Prop_achkRwx[(iClass * PROP_RIGHTS) + iRight] = chk;
                    Prop_tblBits.Controls.Add(chk);
                }
            }

            /* The special bits: three named boxes in a row, spanning the   */
            /* grid's width.                                                */
            pnlSpecial               = new FlowLayoutPanel();
            pnlSpecial.AutoSize      = true;
            pnlSpecial.AutoSizeMode  = AutoSizeMode.GrowAndShrink;
            pnlSpecial.FlowDirection = FlowDirection.LeftToRight;
            pnlSpecial.WrapContents  = false;
            pnlSpecial.Margin        = new Padding(0, PROP_PAD / 2, 0, 0);

            /* Their array, in Prop_SpecialBit's order: set-user-id,   */
            /* set-group-id, sticky.                                   */
            Prop_achkSpecial = new CheckBox[PROP_SPECIALS];

            for (i = 0; i < PROP_SPECIALS; i++)
            {   /* One special bit per iteration: a box with its name on it,   */
                /* a pad to its right, reporting to the one handler.           */
                chk                 = new CheckBox();
                chk.Text            = asSpecial[i];
                chk.AutoSize        = true;
                chk.Margin          = new Padding(0, 0, PROP_PAD, 0);
                chk.CheckedChanged += Prop_OnBitChanged;

                Prop_achkSpecial[i] = chk;
                pnlSpecial.Controls.Add(chk);
            }

            /* The row of special bits into the grid's next cell, spanning   */
            /* every column of it.                                           */
            Prop_tblBits.Controls.Add(pnlSpecial);
            Prop_tblBits.SetColumnSpan(pnlSpecial, Prop_tblBits.ColumnCount);

            /* The symbolic form: a caption in the first column, then the       */
            /* text across the rest, in a larger fixed-pitch font so the ten    */
            /* columns line up as ls prints them and read at a glance; filled   */
            /* by Prop_BoxesFromMode.                                           */
            Prop_AddBitsLabel("As ls shows it:");

            /* Its label: sized to its text, in a bold monospace face       */
            /* PROP_SYMBOLIC_SCALE times the dialog's size (made here,      */
            /* released by Prop_OnDisposed), left empty until the mode is   */
            /* shown.                                                       */
            Prop_lblSymbolic           = new Label();
            Prop_lblSymbolic.AutoSize  = true;
            Prop_fntSymbolic           = new Font(FontFamily.GenericMonospace, this.Font.Size * PROP_SYMBOLIC_SCALE, FontStyle.Bold);
            Prop_lblSymbolic.Font      = Prop_fntSymbolic;
            Prop_lblSymbolic.Margin    = new Padding(PROP_PAD / 2, PROP_PAD / 3, 0, PROP_PAD / 3);
            Prop_lblSymbolic.TextAlign = ContentAlignment.MiddleLeft;
            Prop_lblSymbolic.Text      = "";

            /* Beside its caption, across the three right columns. */
            Prop_tblBits.Controls.Add(Prop_lblSymbolic);
            Prop_tblBits.SetColumnSpan(Prop_lblSymbolic, Prop_tblBits.ColumnCount - 1);

            /* The frame round the grid, titled "Permissions", so the twelve    */
            /* boxes and the ls form read as one control for the mode and are   */
            /* set apart from the facts above and the ids below.  It sizes      */
            /* itself to the grid inside it (which sizes to its content), so    */
            /* a larger font grows the frame with it; half a pad inside, and    */
            /* half a pad above and below to part it from the rows round it.    */
            Prop_grpBits              = new GroupBox();
            Prop_grpBits.Text         = "Permissions";
            Prop_grpBits.AutoSize     = true;
            Prop_grpBits.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Prop_grpBits.Padding      = new Padding(PROP_PAD / 2);
            Prop_grpBits.Margin       = new Padding(0, PROP_PAD / 2, 0, PROP_PAD / 2);
            Prop_grpBits.Controls.Add(Prop_tblBits);

            /* The frame into the facts table's next free cell -- the row       */
            /* after "Mode (octal):", since the constructor calls this just     */
            /* after adding that row -- spanning both columns, so the grid      */
            /* gets the table's full width instead of being squeezed into the   */
            /* value column, and the caption column stays as the facts set      */
            /* it.  The owner and group rows the constructor adds next then     */
            /* follow below it.                                                 */
            Prop_tblFacts.Controls.Add(Prop_grpBits);
            Prop_tblFacts.SetColumnSpan(Prop_grpBits, Prop_tblFacts.ColumnCount);
        }

        /*--------------------------------------------------------------------*/
        /* Prop_AddBitsLabel:                                                 */
        /*                                                                    */
        /* Add one caption to the permission grid, in its next free cell: a   */
        /* column heading or a class name.  The grid's cells are filled in    */
        /* reading order, so the caller adds them in the order they appear.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sText : the caption; "" for the empty corner cell.             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the label is in the grid.                               */
        /*--------------------------------------------------------------------*/
        private void Prop_AddBitsLabel(string sText)
        {
            Label lbl;

            /* A label sized to its text, spaced like the grid's boxes,   */
            /* into the grid's next free cell.                            */
            lbl           = new Label();
            lbl.Text      = sText;
            lbl.AutoSize  = true;
            lbl.Margin    = new Padding(0, PROP_PAD / 3, PROP_PAD / 2, PROP_PAD / 3);
            lbl.TextAlign = ContentAlignment.MiddleLeft;

            Prop_tblBits.Controls.Add(lbl);
        }

        /*--------------------------------------------------------------------*/
        /* Prop_OctalText:                                                    */
        /*                                                                    */
        /* The mode as the octal box shows it: always four digits, so 0644    */
        /* and 4755 read alike and a leading special digit is never missed.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uiMode : the twelve permission bits.                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the octal text, zero-padded to PROP_MODE_DIGITS.      */
        /*--------------------------------------------------------------------*/
        private static string Prop_OctalText(uint uiMode)
        {
            /* Masked to the twelve permission bits first, so nothing above   */
            /* them in the word shows; then base 8 (Convert.ToString takes    */
            /* a long for that), padded to four digits.                       */
            return(Convert.ToString((long)(uiMode & PROP_MODE_MASK), 8).PadLeft(PROP_MODE_DIGITS, '0'));
        }

        /*--------------------------------------------------------------------*/
        /* Prop_AttributesText:                                               */
        /*                                                                    */
        /* A FAT entry's attributes in words, for the Attributes row: what    */
        /* FAT keeps in place of a mode.  The directory bit is left out --    */
        /* the Type row already says so -- and so are bits this program does  */
        /* not name (a volume label's, a long-name entry's), which never      */
        /* reach a listed entry.                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uiAttributes : the entry's ENG_ATTR_* bits, as FAT stores      */
        /*                    them.                                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the set attributes, comma-separated, always in the    */
        /*              order read-only, hidden, system, archive ("Read-only, */
        /*              Archive"); "None" when none is set.                   */
        /*--------------------------------------------------------------------*/
        private static string Prop_AttributesText(uint uiAttributes)
        {
            string sText;

            /* Nothing named until a set bit adds its word. */
            sText = "";

            if ((uiAttributes & ENG_clsNative.ENG_ATTR_READONLY) != 0)
            {   /* Read-only: the engine shows it as the mode without its   */
                /* write bits too.                                          */
                sText = Prop_JoinWord(sText, "Read-only");
            }

            if ((uiAttributes & ENG_clsNative.ENG_ATTR_HIDDEN) != 0)
            {   /* Hidden from an ordinary Windows listing. */
                sText = Prop_JoinWord(sText, "Hidden");
            }

            if ((uiAttributes & ENG_clsNative.ENG_ATTR_SYSTEM) != 0)
            {   /* A system file. */
                sText = Prop_JoinWord(sText, "System");
            }

            if ((uiAttributes & ENG_clsNative.ENG_ATTR_ARCHIVE) != 0)
            {   /* Changed since a backup last cleared the bit. */
                sText = Prop_JoinWord(sText, "Archive");
            }

            if (sText.Length == 0)
            {   /* No attribute set: said, rather than an empty row. */
                return("None");
            }

            /* The words set, in order. */
            return(sText);
        }

        /*--------------------------------------------------------------------*/
        /* Prop_JoinWord:                                                     */
        /*                                                                    */
        /* Append one word to a comma-separated list, for                     */
        /* Prop_AttributesText.                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sList : the list so far; "" for none.                          */
        /*     sWord : the word to add.                                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the list with the word at its end.                    */
        /*--------------------------------------------------------------------*/
        private static string Prop_JoinWord(string sList, string sWord)
        {
            if (sList.Length == 0)
            {   /* The first word: no separator before it. */
                return(sWord);
            }

            /* A later word, after a comma. */
            return(sList + ", " + sWord);
        }

        /*--------------------------------------------------------------------*/
        /* Prop_RightBit:                                                     */
        /*                                                                    */
        /* The mode bit one grid box stands for.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iClass : 0 owner, 1 group, 2 others.                           */
        /*     iRight : 0 read, 1 write, 2 execute.                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     uint : the single bit, 0400 for the owner's read down to 01    */
        /*            for others' execute.                                    */
        /*--------------------------------------------------------------------*/
        private static uint Prop_RightBit(int iClass, int iRight)
        {
            int iShift;

            /* The bit's number.  The owner's rights are the top three of     */
            /* the nine, others' the bottom three, and within a class read    */
            /* is the highest bit -- so the number counts DOWN as the class   */
            /* and right indices count up: (2 - class) * 3 is where the       */
            /* class's group of three starts (owner 6, group 3, others 0),    */
            /* and (2 - right) picks the bit within it -- so owner read is    */
            /* bit 8 and others' execute bit 0.                               */
            iShift = ((PROP_CLASSES - 1 - iClass) * PROP_RIGHTS) + (PROP_RIGHTS - 1 - iRight);

            /* The one bit this box controls. */
            return(1u << iShift);
        }

        /*--------------------------------------------------------------------*/
        /* Prop_SpecialBit:                                                   */
        /*                                                                    */
        /* The mode bit one special-bit box stands for, in the order the      */
        /* boxes are laid out and the octal digit is read: set-user-id is the */
        /* 4, set-group-id the 2, sticky the 1.                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iSpecial : 0 set-user-id, 1 set-group-id, 2 sticky.            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     uint : PROP_BIT_SETUID, PROP_BIT_SETGID or PROP_BIT_STICKY.    */
        /*--------------------------------------------------------------------*/
        private static uint Prop_SpecialBit(int iSpecial)
        {
            if (iSpecial == 0)
            {   /* The first box, set-user-id: the special digit's 4 (04000). */
                return(PROP_BIT_SETUID);
            }

            if (iSpecial == 1)
            {   /* The second box, set-group-id: the special digit's 2 (02000). */
                return(PROP_BIT_SETGID);
            }

            /* The third box (or any other index -- the callers pass only 0   */
            /* to 2), sticky: the special digit's 1 (01000).                  */
            return(PROP_BIT_STICKY);
        }

        /*--------------------------------------------------------------------*/
        /* Prop_ModeFromBoxes:                                                */
        /*                                                                    */
        /* Read the twelve boxes back into a mode word.  The boxes are the    */
        /* whole truth here: a box is one bit, ticked is set.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     uint : the mode the boxes show, within PROP_MODE_MASK.         */
        /*--------------------------------------------------------------------*/
        private uint Prop_ModeFromBoxes()
        {
            uint uiMode;
            int  iClass;
            int  iRight;
            int  i;

            /* No bit set until a ticked box adds it. */
            uiMode = 0;

            for (iClass = 0; iClass < PROP_CLASSES; iClass++)
            {   /* One class per iteration. */
                for (iRight = 0; iRight < PROP_RIGHTS; iRight++)
                {   /* One right per iteration: its bit if its box is ticked. */
                    if (Prop_achkRwx[(iClass * PROP_RIGHTS) + iRight].Checked)
                    {   /* Ticked: the right is granted. */
                        uiMode |= Prop_RightBit(iClass, iRight);
                    }
                }
            }

            for (i = 0; i < PROP_SPECIALS; i++)
            {   /* One special bit per iteration. */
                if (Prop_achkSpecial[i].Checked)
                {   /* Ticked: the special bit is set. */
                    uiMode |= Prop_SpecialBit(i);
                }
            }

            /* The twelve bits as the boxes show them. */
            return(uiMode);
        }

        /*--------------------------------------------------------------------*/
        /* Prop_BoxesFromMode:                                                */
        /*                                                                    */
        /* Show a mode word in the twelve boxes and the symbolic label,       */
        /* without the boxes' own change events writing back to the octal     */
        /* text it came from.                                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uiMode : the twelve permission bits to show.                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the boxes and the symbolic label show uiMode.           */
        /*--------------------------------------------------------------------*/
        private void Prop_BoxesFromMode(uint uiMode)
        {
            int iClass;
            int iRight;
            int i;

            /* The guard up for the whole update: each box's CheckedChanged   */
            /* fires as it is set, and Prop_OnBitChanged must not rewrite     */
            /* the octal text while it is still the source of what is shown   */
            /* -- it would rewrite it from a half-updated set of boxes.       */
            Prop_bSyncing = true;

            for (iClass = 0; iClass < PROP_CLASSES; iClass++)
            {   /* One class per iteration. */
                for (iRight = 0; iRight < PROP_RIGHTS; iRight++)
                {   /* One right per iteration: ticked when its bit is set. */
                    Prop_achkRwx[(iClass * PROP_RIGHTS) + iRight].Checked =
                        ((uiMode & Prop_RightBit(iClass, iRight)) != 0);
                }
            }

            for (i = 0; i < PROP_SPECIALS; i++)
            {   /* One special bit per iteration. */
                Prop_achkSpecial[i].Checked = ((uiMode & Prop_SpecialBit(i)) != 0);
            }

            /* The symbolic form for the same mode, then the guard down:   */
            /* from here a user's tick writes the text again.              */
            Prop_lblSymbolic.Text = Prop_SymbolicText(uiMode);
            Prop_bSyncing         = false;
        }

        /*--------------------------------------------------------------------*/
        /* Prop_TypeChar:                                                     */
        /*                                                                    */
        /* The character ls puts in the first column of a long listing for an */
        /* entry's type: a dash for a regular file, d for a directory, l for  */
        /* a symbolic link, c and b for the two device kinds, p for a fifo    */
        /* and s for a socket.  A type the engine could not determine shows   */
        /* as a question mark, which says more than a dash would (whether ls  */
        /* itself prints one for such an entry is not established here).      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uiType : the entry's ENG_TYPE_* value.                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     char : the type column's character.                            */
        /*--------------------------------------------------------------------*/
        private static char Prop_TypeChar(uint uiType)
        {
            switch (uiType)
            {   /* One case per entry type the engine reports. */
                case ENG_clsNative.ENG_TYPE_FILE:     return('-');
                case ENG_clsNative.ENG_TYPE_DIR:      return('d');
                case ENG_clsNative.ENG_TYPE_SYMLINK:  return('l');
                case ENG_clsNative.ENG_TYPE_CHARDEV:  return('c');
                case ENG_clsNative.ENG_TYPE_BLOCKDEV: return('b');
                case ENG_clsNative.ENG_TYPE_FIFO:     return('p');
                case ENG_clsNative.ENG_TYPE_SOCKET:   return('s');
            }

            /* ENG_TYPE_UNKNOWN, or a value this build does not know. */
            return('?');
        }

        /*--------------------------------------------------------------------*/
        /* Prop_SymbolicText:                                                 */
        /*                                                                    */
        /* The mode in the ten-character form ls prints, special bits         */
        /* included as ls shows them: 4755 on a file is "-rwsr-xr-x", 1777    */
        /* on a directory is "drwxrwxrwt", and 4644 is "-rwSr--r--".          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uiMode : the twelve permission bits.                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : ten characters, "-rwxr-xr-x" and the like.            */
        /*--------------------------------------------------------------------*/
        private string Prop_SymbolicText(uint uiMode)
        {
            char[] acText;
            char[] acRight;
            char[] acSpecialOn;
            char[] acSpecialOff;
            int    iClass;
            int    iRight;
            int    iPos;
            bool   bGranted;
            bool   bSpecial;

            /* The letters: a right's own (r, w, x); a special bit's when    */
            /* the class can also execute (s, s, t -- set-user-id in the     */
            /* owner's column, set-group-id in the group's, sticky in        */
            /* others'); and the upper-case form for a special bit with no   */
            /* execute (S, S, T), which is legal and worth seeing.  Then     */
            /* the ten characters: the type and three groups of three.       */
            acRight      = new char[] { 'r', 'w', 'x' };
            acSpecialOn  = new char[] { 's', 's', 't' };
            acSpecialOff = new char[] { 'S', 'S', 'T' };
            acText       = new char[1 + (PROP_CLASSES * PROP_RIGHTS)];

            /* The type column comes first. */
            acText[0] = Prop_cTypeChar;

            for (iClass = 0; iClass < PROP_CLASSES; iClass++)
            {   /* One class per iteration: its three characters. */
                for (iRight = 0; iRight < PROP_RIGHTS; iRight++)
                {   /* One right per iteration: the letter or a dash.  Its    */
                    /* place is after the type character, its class's group   */
                    /* of three, and its right within the group.              */
                    iPos     = 1 + (iClass * PROP_RIGHTS) + iRight;
                    bGranted = ((uiMode & Prop_RightBit(iClass, iRight)) != 0);

                    if (iRight == PROP_RIGHTS - 1)
                    {   /* The execute column, where a special bit shows      */
                        /* instead.  Prop_SpecialBit's order matches the      */
                        /* classes': class 0 (owner) set-user-id, 1 (group)   */
                        /* set-group-id, 2 (others) sticky -- which is what   */
                        /* ls does.                                           */
                        bSpecial = ((uiMode & Prop_SpecialBit(iClass)) != 0);

                        if (bSpecial && bGranted)
                        {   /* Special bit and execute: the lower-case letter. */
                            acText[iPos] = acSpecialOn[iClass];
                        }
                        else if (bSpecial)
                        {   /* Special bit without execute: the upper-case letter. */
                            acText[iPos] = acSpecialOff[iClass];
                        }
                        else if (bGranted)
                        {   /* Plain execute. */
                            acText[iPos] = acRight[iRight];
                        }
                        else
                        {   /* Neither. */
                            acText[iPos] = '-';
                        }
                    }
                    else if (bGranted)
                    {   /* Read or write, granted. */
                        acText[iPos] = acRight[iRight];
                    }
                    else
                    {   /* Read or write, not granted. */
                        acText[iPos] = '-';
                    }
                }
            }

            /* The ten characters as one string. */
            return(new string(acText));
        }

        /*--------------------------------------------------------------------*/
        /* Prop_OnBitChanged:                                                 */
        /*                                                                    */
        /* A box was ticked or cleared by the user: rewrite the octal text    */
        /* and the symbolic label from the boxes.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the box.                                           */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the octal text and the symbolic label match the boxes.  */
        /*--------------------------------------------------------------------*/
        private void Prop_OnBitChanged(object objSender, EventArgs evArgs)
        {
            uint uiMode;

            if (Prop_bSyncing)
            {   /* The boxes are being set from the text (Prop_BoxesFromMode);   */
                /* the text is already right.  Without this guard the two        */
                /* views would write each other in a loop.                       */
                return;
            }

            /* The mode the boxes now show, all twelve read back. */
            uiMode = Prop_ModeFromBoxes();

            /* The text is rewritten under the guard -- its TextChanged     */
            /* fires on this write, finds the guard up, and does not come   */
            /* back round to the boxes -- and the symbolic label too.       */
            Prop_bSyncing         = true;
            Prop_txtMode.Text     = Prop_OctalText(uiMode);
            Prop_lblSymbolic.Text = Prop_SymbolicText(uiMode);
            Prop_bSyncing         = false;
        }

        /*--------------------------------------------------------------------*/
        /* Prop_OnModeTextChanged:                                            */
        /*                                                                    */
        /* The octal box was typed in: when its text is a clean octal number  */
        /* within the twelve bits, set the boxes and the symbolic label from  */
        /* it.  Text that does not parse -- half-typed, an 8 or a 9, too long */
        /* -- changes nothing: the boxes keep showing the last good value and */
        /* OK will refuse the text with its own message.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the octal box.                                     */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the boxes follow the text when the text is a mode.      */
        /*--------------------------------------------------------------------*/
        private void Prop_OnModeTextChanged(object objSender, EventArgs evArgs)
        {
            uint uiMode;

            if (Prop_bSyncing)
            {   /* The text was written from the boxes (Prop_OnBitChanged);   */
                /* they already match.                                        */
                return;
            }

            if (!Prop_ParseNumber(Prop_txtMode.Text, 8, PROP_MODE_MASK, out uiMode))
            {   /* Not (yet) a mode; leave the boxes as they are. */
                return;
            }

            /* A clean mode: the boxes and the symbolic form follow it. */
            Prop_BoxesFromMode(uiMode);
        }

        /*--------------------------------------------------------------------*/
        /* Prop_ParseNumber:                                                  */
        /*                                                                    */
        /* Parse one of the three boxes: digits only, in the given base, no   */
        /* sign, no spaces inside, within the given limit.  Anything else is  */
        /* refused, so a typing slip can never reach the image as a number.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sText    : the box's text.                                     */
        /*     iBase    : 8 for the mode, 10 for the ids.                     */
        /*     uiLimit  : the largest value allowed.                          */
        /*     uiValue  : receives the value on success.                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the text is a clean number within the limit.  */
        /*--------------------------------------------------------------------*/
        private static bool Prop_ParseNumber(string sText, int iBase, uint uiLimit, out uint uiValue)
        {
            ulong ullValue;
            int   iDigit;
            int   i;

            /* The out value 0 until a number is accepted; the running       */
            /* value kept in 64 bits, so one more digit after a value just   */
            /* under the 32-bit limit cannot wrap before the limit test;     */
            /* spaces at either end ignored (inside, they are refused by     */
            /* the digit test).                                              */
            uiValue  = 0;
            ullValue = 0;
            sText    = sText.Trim();

            if (sText.Length == 0)
            {   /* Nothing was typed. */
                return(false);
            }

            for (i = 0; i < sText.Length; i++)
            {   /* One character per iteration; each must be a digit of the base. */
                iDigit = sText[i] - '0';

                if (iDigit < 0 || iDigit >= iBase)
                {   /* Not a digit in this base (a letter, a sign, an 8 in octal). */
                    return(false);
                }

                /* The digit appended to the running value. */
                ullValue = (ullValue * (ulong)iBase) + (ulong)iDigit;

                if (ullValue > uiLimit)
                {   /* Past the limit; stop before the running value can wrap. */
                    return(false);
                }
            }

            /* Every character a digit and the value within the limit, so   */
            /* it fits the 32-bit out value.                                */
            uiValue = (uint)ullValue;

            /* A clean number within the limit. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* Prop_OnOkClicked:                                                  */
        /*                                                                    */
        /* OK: parse the three boxes.  If any is not a clean number, say      */
        /* which and stay open; otherwise keep the values and close.          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the dialog closes accepted, or shows what to correct.   */
        /*--------------------------------------------------------------------*/
        private void Prop_OnOkClicked(object objSender, EventArgs evArgs)
        {
            uint uiMode;
            uint uiUid;
            uint uiGid;

            /* The three boxes in the order they appear, the first fault   */
            /* stopping the check: the message says which box, and the     */
            /* focus goes to it, so the user fixes one thing at a time.    */
            /* The mode's limit is 07777, the twelve permission bits.      */
            if (!Prop_ParseNumber(Prop_txtMode.Text, 8, PROP_MODE_MASK, out uiMode))
            {   /* The mode is not octal digits up to 7777. */
                Prop_lblError.Text = "The mode must be octal digits, 0000 to 7777.";
                Prop_txtMode.Focus();
                return;
            }

            /* The ids' limit is the full 32 bits an ext4 inode records     */
            /* (both 16-bit halves -- lwext4's ext4_inode_set_uid / _gid,   */
            /* BifrostFM patch 25).                                         */
            if (!Prop_ParseNumber(Prop_txtUid.Text, 10, uint.MaxValue, out uiUid))
            {   /* The owner is not a plain number. */
                Prop_lblError.Text = "The owner must be a number (a numeric user id).";
                Prop_txtUid.Focus();
                return;
            }

            if (!Prop_ParseNumber(Prop_txtGid.Text, 10, uint.MaxValue, out uiGid))
            {   /* The group is not a plain number. */
                Prop_lblError.Text = "The group must be a number (a numeric group id).";
                Prop_txtGid.Focus();
                return;
            }

            /* All three clean: the values handed back, accepted, and the   */
            /* dialog closed.  Whether anything actually changed is the     */
            /* caller's to decide (MainForm.cs).                            */
            PROP_uiMode    = uiMode;
            PROP_uiUid     = uiUid;
            PROP_uiGid     = uiGid;
            PROP_bAccepted = true;
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Prop_OnCancelClicked:                                              */
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
        private void Prop_OnCancelClicked(object objSender, EventArgs evArgs)
        {
            /* Closed with PROP_bAccepted still false, so the caller   */
            /* changes nothing.                                        */
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Prop_OnResize:                                                     */
        /*                                                                    */
        /* The dialog sizes itself to its content (AutoSize), so there is     */
        /* nothing to reposition here; the handler keeps the error line's     */
        /* height matched to the font, which AutoSize does not do for a label */
        /* whose own AutoSize is off.                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the dialog.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the error line's height fits the font.                  */
        /*--------------------------------------------------------------------*/
        private void Prop_OnResize(object objSender, EventArgs evArgs)
        {
            /* One line of the error's font plus a pad, as the constructor   */
            /* sets it.                                                      */
            Prop_lblError.Height = Prop_lblError.PreferredHeight + PROP_PAD;
        }

        /*--------------------------------------------------------------------*/
        /* Prop_OnDisposed:                                                   */
        /*                                                                    */
        /* The dialog is gone: release the symbolic form's font, the one font */
        /* made for it (the dialog font is the caller's and stays).           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the dialog.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the font is released.                                   */
        /*--------------------------------------------------------------------*/
        private void Prop_OnDisposed(object objSender, EventArgs evArgs)
        {
            if (Prop_fntSymbolic != null)
            {   /* The symbolic form's face; released once only. */
                Prop_fntSymbolic.Dispose();
                Prop_fntSymbolic = null;
            }
        }
    }
}
