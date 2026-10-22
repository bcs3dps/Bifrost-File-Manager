/*----------------------------------------------------------------------------*/
/* ImageSaveForm.cs                                                           */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The Save As dialog for saving an editor's file INTO the open image (an     */
/* editor's File > Save As > Image...), whatever the file was opened from: a  */
/* tree of the image's folders, the chosen folder's entries, and a name box,  */
/* laid out as Windows' own Save dialog is.  It only CHOOSES: the path and    */
/* name chosen go back to its caller (MainCommand.cs                          */
/* Main_PickEditorImageTarget), and the editor's save, after the dialog has   */
/* closed, is what writes -- on the command worker, which looks at the target */
/* again then and asks before it replaces anything.                           */
/*                                                                            */
/* NOTHING HERE CALLS THE ENGINE.  Every folder listing is a request to the   */
/* command worker (PickerList for one folder, PickerBranch for the starting   */
/* folder and those above it), and New Folder is one too (EditorMakeFolder:   */
/* the worker asks the name, makes the folder and lists it back).  Each       */
/* request is bound to the open filesystem the dialog was given, never to     */
/* whatever the main window shows later; each answer comes back here on the   */
/* UI thread, through a small record that remembers what the request was      */
/* for (SNR_clsNodeRead, SFC_clsFolderCreate), and is painted only while it   */
/* is still the newest for its folder or list.  A finished answer starts no   */
/* further request by itself.                                                 */
/*                                                                            */
/* The name box also takes a PATH inside the image -- "/etc/x.txt",           */
/* "../x.txt", "sub/", ".." -- resolved here against the folder shown (or the */
/* root), the picker moving to its folder and the file name then judged as    */
/* any name is (Isv_ResolveTypedPath); and a Windows drive path, which asks   */
/* for the save to go to Windows instead (Isv_TakeWindowsRequest).            */
/*----------------------------------------------------------------------------*/

/* The framework: the base types (the path split, the entries' times, Math,   */
/* the name comparer, event arguments), the lists of entries and names, the   */
/* font and sizes, the name's UTF-8 byte count, and the dialog with its       */
/* tree, list and controls.                                                   */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* SNR_clsNodeRead -- one folder listing's request as the dialog posted   */
    /* it: which node asked, and whether the entry list was to show the       */
    /* folder too.  Its SNR_OnResult is the reply the client calls, so the    */
    /* answer arrives with these values and no table of pending reads is      */
    /* kept.  The worker's request holds only the path and the filesystem,    */
    /* never a control -- documentation table.                                */
    /*------------------------------------------------------------------------*/
    internal class SNR_clsNodeRead
    {
        internal TreeNode SNR_nodDirectory;                   // the folder's node that asked; the answer is dropped if the node has left the tree since
        internal bool SNR_bShowList;                          // true when the listing was also for the entry list (a selection), false for the tree alone (an expansion)
        private readonly ISV_clsImageSaveForm Snr_frmPicker;  // the dialog that asked; borrowed

        /*------------------------------------------------------------------*/
        /* SNR_clsNodeRead:                                                 */
        /*                                                                  */
        /* One listing's record, made as its request is posted              */
        /* (Isv_LoadNode).                                                  */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     frmPicker    : the dialog.                                   */
        /*     nodDirectory : the folder's node.                            */
        /*     bShowList    : true when the entry list is to show it too.   */
        /*                                                                  */
        /* Returns:                                                         */
        /*     (constructor) : the record; it holds nothing to release.     */
        /*------------------------------------------------------------------*/
        internal SNR_clsNodeRead(ISV_clsImageSaveForm frmPicker,
            TreeNode nodDirectory, bool bShowList)
        {
            /* Kept as given.  The node is kept even if a later listing of   */
            /* its parent removes it from the tree: the dialog tests for     */
            /* that before it paints anything (ISV_ApplyNodeResult).         */
            Snr_frmPicker = frmPicker;
            SNR_nodDirectory = nodDirectory;
            SNR_bShowList = bShowList;
        }

        /*------------------------------------------------------------------*/
        /* SNR_OnResult:                                                    */
        /*                                                                  */
        /* The listing's answer, called by the client on the UI thread      */
        /* (CommandClient.cs CLI_Post), handed to the dialog with this      */
        /* record.                                                          */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objRequest : the request answered.                           */
        /*     objResult  : its outcome: the folder's entries, or why not   */
        /*                  (refused, cancelled, replaced, failed).         */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the dialog has it (ISV_ApplyNodeResult); no further   */
        /*            request is posted.                                    */
        /*------------------------------------------------------------------*/
        internal void SNR_OnResult(RQT_clsRequest objRequest, RSL_clsResult objResult)
        {
            /* This record, the request and the answer. */
            Snr_frmPicker.ISV_ApplyNodeResult(this, objRequest, objResult);
        }
    }

    /*------------------------------------------------------------------------*/
    /* SFC_clsFolderCreate -- one New Folder request as the dialog posted it: */
    /* the folder it was made in, and the dialog's navigation count at that   */
    /* moment, so the answer can tell whether the user has moved on since.    */
    /* Its SFC_OnResult is the reply the client calls -- documentation table. */
    /*------------------------------------------------------------------------*/
    internal class SFC_clsFolderCreate
    {
        internal TreeNode SFC_nodParent;                      // the folder's node selected when New Folder was clicked, the one the new folder goes in
        internal long SFC_lNavigation;                        // the dialog's selection count then (Isv_lNavigation); a newer selection keeps the answer from moving the view
        private readonly ISV_clsImageSaveForm Sfc_frmPicker;  // the dialog that asked; borrowed

        /*------------------------------------------------------------------*/
        /* SFC_clsFolderCreate:                                             */
        /*                                                                  */
        /* One New Folder's record, made as its request is posted           */
        /* (Isv_RequestNewFolder).                                          */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     frmPicker   : the dialog.                                    */
        /*     nodParent   : the folder's node the new one goes in.         */
        /*     lNavigation : the dialog's selection count now.              */
        /*                                                                  */
        /* Returns:                                                         */
        /*     (constructor) : the record; it holds nothing to release and  */
        /*                     grants no right to write.                    */
        /*------------------------------------------------------------------*/
        internal SFC_clsFolderCreate(ISV_clsImageSaveForm frmPicker,
            TreeNode nodParent, long lNavigation)
        {
            /* Kept as given.  They decide only where the answer may be        */
            /* shown: the folder, once made, stays made even if the user has   */
            /* browsed elsewhere meanwhile.                                    */
            Sfc_frmPicker = frmPicker;
            SFC_nodParent = nodParent;
            SFC_lNavigation = lNavigation;
        }

        /*--------------------------------------------------------------------*/
        /* SFC_OnResult:                                                      */
        /*                                                                    */
        /* The New Folder's answer, called by the client on the UI thread,    */
        /* handed to the dialog with this record.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the request answered (not needed: this record     */
        /*                  already names it).                                */
        /*     objResult  : its outcome; a made folder's record               */
        /*                  (MFV_clsMadeFolderView) even when its listings    */
        /*                  failed.                                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the dialog has it (ISV_ApplyFolderResult).              */
        /*--------------------------------------------------------------------*/
        internal void SFC_OnResult(RQT_clsRequest objRequest, RSL_clsResult objResult)
        {
            /* This record and the answer. */
            Sfc_frmPicker.ISV_ApplyFolderResult(this, objResult);
        }
    }

    /*------------------------------------------------------------------------*/
    /* ISV_clsImageSaveForm -- the Save As window that chooses a folder and a */
    /* name inside an image.  A plain Form: its base class is declared here   */
    /* with the rest of its UI.                                               */
    /*                                                                        */
    /* Derives from Form because the framework requires it: WinForms makes a  */
    /* window only as a Form subclass.  That is the one kind of inheritance   */
    /* allowed here -- from a framework base class, never from a class of     */
    /* ours.                                                                  */
    /*------------------------------------------------------------------------*/
    internal sealed class ISV_clsImageSaveForm : Form
    {
        /* The dialog's sizes, the longest name each kind of filesystem   */
        /* holds, and the text of a folder's stand-in child until it is   */
        /* listed -- documentation table.                                 */
        private const int    ISV_WIDTH          = 760;              // first client width, pixels
        private const int    ISV_HEIGHT         = 480;              // first client height, pixels
        private const int    ISV_MIN_WIDTH      = 520;              // smallest window width, pixels
        private const int    ISV_MIN_HEIGHT     = 340;              // smallest window height, pixels
        private const int    ISV_PAD            = 12;               // padding round the content, pixels
        private const int    ISV_GAP            = 6;                // between a caption and its box, pixels
        private const int    ISV_CELL_PAD       = 16;               // added to a column's measured text width for the cell's margins, pixels
        private const int    ISV_TREE_SHARE     = 3;                // the tree takes one part in this many of the width; the list the rest
        private const int    ISV_NAME_MAX_BYTES = 255;              // the longest name an ext filesystem holds, bytes of UTF-8
        private const int    ISV_FAT_NAME_MAX   = 255;              // the longest long name FAT and exFAT hold, UTF-16 units (a string's own Length), as Windows' names are
        private const string ISV_PLACEHOLDER    = "(reading ...)";  // the text of a directory's stand-in child until it has been listed

        /* What a typed name turned out to be (Isv_ResolveTypedPath) --   */
        /* documentation table.                                           */
        private const int ISV_PATH_NONE = 0;   // no path: the text is a name, judged as one
        private const int ISV_PATH_HERE = 1;   // a path that ends in the folder shown, with a name: the name is judged as one
        private const int ISV_PATH_DONE = 2;   // dealt with: the picker is moving to another folder, or the path was refused with a message

        /* The entry list's headings.  Isv_FitColumns and the size column's   */
        /* alignment address them by position, so the order is fixed.         */
        private static readonly string[] Isv_arrColumns = { "Name", "Size", "Modified" };   // the entry list's headings, left to right

        /* The outcome, which the caller reads after ShowDialog returns and    */
        /* before it disposes the dialog (MainCommand.cs                       */
        /* Main_PickEditorImageTarget).  The path and the name mean            */
        /* something only when ISV_bAccepted is true; the Windows folder and   */
        /* name only when ISV_bWindowsAsked is -- documentation table.         */
        internal bool   ISV_bAccepted;         // true when Save was pressed with a usable name; false for Cancel or a closed dialog
        internal string ISV_sPath;             // the chosen entry's full image path
        internal string ISV_sName;             // its name alone
        internal bool   ISV_bWindowsAsked;     // true when a Windows drive path (a drive letter and colon, alone or followed by a path) was typed as the name: a request to save to Windows there instead; ISV_bAccepted stays false
        internal string ISV_sWindowsFolder;    // that request's folder, with '\' separators (the drive's root, ending in its separator, for a drive alone)
        internal string ISV_sWindowsName;      // that request's file name; the offered name when only a folder was typed

        /* The dialog's controls, made in the constructor and laid out by      */
        /* docking: the Save in row at the top, the tree and the list side     */
        /* by side in the middle, then the status line, the name row and the   */
        /* buttons along the bottom.                                           */
        private TableLayoutPanel Isv_pnlWhere;     // the "Save in" row: caption, the directory shown, Up, New Folder
        private Label            Isv_lblWhere;     // "Save in:"
        private TextBox          Isv_txtWhere;     // the directory shown, read-only
        private Button           Isv_btnUp;        // goes to the parent directory
        private Button           Isv_btnNewFolder; // makes a directory in the one shown
        private SplitContainer   Isv_split;        // the tree at the left, the entry list at the right
        private TreeView         Isv_treFolders;   // the image's directories
        private ListView         Isv_lstEntries;   // the entries of the directory shown
        private Label            Isv_lblStatus;    // a listing under way or failed, and what a New Folder did
        private TableLayoutPanel Isv_pnlName;      // the "File name" row: caption and box
        private Label            Isv_lblName;      // "File name:"
        private TextBox          Isv_txtName;      // the name to save as
        private FlowLayoutPanel  Isv_pnlButtons;   // Save and Cancel, right-aligned
        private Button           Isv_btnSave;      // accepts the name
        private Button           Isv_btnCancel;    // abandons the save

        /* What the dialog was given, which holds for its whole life, and its   */
        /* browsing state -- documentation table.                               */
        private readonly CLI_clsClient Isv_objClient;                        // the command client every request is posted through (CommandClient.cs); borrowed
        private readonly STV_clsStateView Isv_objSource;                     // the open filesystem the input was accepted for (range, epoch); every request is bound to it, never to the main window's later state
        private readonly EDR_clsEditor Isv_objEditor;                        // the worker's record of the editor saving (its id, destination, save order), for New Folder; null disables New Folder
        private readonly long Isv_lWindow;                                   // this input's id, negative (the constructor refuses others): the origin of every listing request, and what the worker closes the dialog by
        private string[] Isv_arrStart;                                       // the folder the branch listing goes to, split into its names: the starting folder, or a typed path's (Isv_ResolveTypedPath); the branch listing and its answer walk it
        private readonly Dictionary<string, RQT_clsRequest> Isv_dictLatest;  // per folder path (case-sensitive), the newest listing request whose answer may merge that node's children
        private RQT_clsRequest Isv_objListRequest;                           // the newest listing whose answer may fill the entry list (a selection's); null when none is awaited
        private RQT_clsRequest Isv_objBranch;                                // the branch listing still awaited (the start-up one, or a typed path's); null once answered, given up or closed
        private bool Isv_bRenderingBranch;                                   // true while a finished answer is being painted into the tree, so the tree's own expand / select events post nothing
        private bool Isv_bStartupSuperseded;                                 // true once the user has expanded, selected or made a folder: the awaited branch's answer is then dropped; a typed path's move sets it false again for its own branch
        private long Isv_lNavigation;                                        // counted up by each selection the user makes; a New Folder's answer moves the view only if it has not changed
        private WQU_clsWindowQueue Isv_objQueue;                             // the dialog's own command queue (WindowQueue.cs): Save's, a row's activation's, Up's and New Folder's handlers post to it, and it runs each through Isv_RunItem, one at a time

        /* The dialog's own commands, as its queue (Isv_objQueue) carries   */
        /* them -- documentation table.                                     */
        private enum Isv_enmItem
        {
            Save = 1,           // Save, or Enter: the name in the box taken (Isv_AcceptName)
            Activate = 2,       // a row double-clicked or Enter on it: its argument the row's index into Isv_lstShown (Isv_ActivateEntry)
            Up = 3,             // Up: the parent folder shown (Isv_GoUp)
            NewFolder = 4       // New Folder: the worker asked to make one (Isv_RequestNewFolder)
        }
        /* The browsing state, continued: the dialog's own flags, the      */
        /* folder shown and its rows, and a typed path's waiting name --   */
        /* documentation table.                                            */
        private bool Isv_bClosed;                                            // true once the dialog has closed: answers arriving later touch no control
        private bool Isv_bSplitSet;                                          // true once the splitter has been placed; from then on the user's own position is kept
        private string Isv_sDir;                                             // the folder selected and shown in "Save in", whether or not its listing has come
        private List<EEN_stEntry> Isv_lstShown;                              // the entries in the list's row order; each row's Tag is its index here; Save checks names against it
        private string Isv_sListedDir;                                       // the folder whose entries the list now shows from its own listing; null while none -- Save is refused until it equals Isv_sDir
        private readonly bool Isv_bFat;                                      // true when the image's filesystem is FAT or exFAT (the caller's word): a typed name is then held to FAT's rules too (SYS_FatNameProblem) and to FAT's length, not ext's (Isv_NameProblem)
        private readonly string Isv_sStartName;                              // the name the dialog offered, kept for a Windows request that names a folder alone
        private string Isv_sKeptName;                                        // the name the box holds as a NAME (the offered one, or a file's picked in the list), put back when a typed path names a folder only
        private string Isv_sPendingName;                                     // a typed path's file name, taken once the picker has moved to its folder; null when none waits
        private RQT_clsRequest Isv_objPendingBranch;                         // the branch listing that move posted; only its own answer takes the pending name
        private long Isv_lPendingNavigation;                                 // the selection count when that answer was shown; a selection made before the name is taken drops it
        private string Isv_sListNote;                                        // a line for the status line once the entry list's awaited listing has filled it (a typed path's "no folder" stop); "" normally, and cleared by any new folder shown

        /*--------------------------------------------------------------------*/
        /* ISV_clsImageSaveForm (constructor):                                */
        /*                                                                    */
        /* Build the dialog for one accepted Save As input.  Nothing is       */
        /* listed yet: the first listing is posted when the dialog is shown   */
        /* (Isv_OnShown).  It writes nothing, ever -- New Folder is the       */
        /* worker's, and so is the editor's save after the dialog returns.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     fntDialog  : the program's dialog font (the caller's; not      */
        /*                  freed here).                                      */
        /*     objClient  : the command client requests are posted through.   */
        /*     objSource  : the open filesystem the input was accepted for.   */
        /*     objEditor  : the worker's record of the editor saving, for     */
        /*                  New Folder; null disables New Folder.             */
        /*     lWindow    : the input's id; must be negative.                 */
        /*     sStartDir  : the folder to open at ("/" for the root); its     */
        /*                  folders above are listed too, so the tree shows   */
        /*                  the way down to it.                               */
        /*     sStartName : the name offered in the name box.                 */
        /*     imlIcons   : the dialogs' icon list (MainForm.cs               */
        /*                  Main_imlDialogIcons), not owned here.             */
        /*     bFat       : true when the image's filesystem is FAT or exFAT, */
        /*                  so a typed name is held to FAT's rules as well,   */
        /*                  and to FAT's length rather than ext's.            */
        /*                  Optional, false: a caller that does not say so    */
        /*                  gets the ext rules alone.                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the dialog, not yet shown.  ArgumentException  */
        /*                     for a missing client or source, a non-negative */
        /*                     id, or an editor record with no id.            */
        /*--------------------------------------------------------------------*/
        internal ISV_clsImageSaveForm(Font fntDialog, CLI_clsClient objClient,
            STV_clsStateView objSource, EDR_clsEditor objEditor, long lWindow,
            string sStartDir, string sStartName, ImageList imlIcons, bool bFat = false)
        {
            TreeNode nodRoot;
            int i;

            if (objClient == null || objSource == null ||
                objSource.STV_objTarget == null || lWindow >= 0)
            {   /* No client, no filesystem, or an id that is not an        */
                /* input's (zero is the main window's): the dialog could    */
                /* post nothing it could answer for, and nothing from the   */
                /* main window's later state may stand in.  Refused by      */
                /* throwing, before any control is made; the caller ends    */
                /* the input (MainCommand.cs Main_RenderEditorTarget).      */
                throw new ArgumentException("The Save As browser needs its accepted command source and input identity.");
            }

            if (objEditor != null && objEditor.EDR_lId == 0)
            {   /* An editor record with no id: New Folder would post for no   */
                /* editor the worker knows.  Refused the same way.             */
                throw new ArgumentException("The Save As picker has no registered editor.");
            }

            /* What the dialog was given, kept for its life.  The worker       */
            /* checks every request against the filesystem it names when it    */
            /* takes the request, so a stale one is refused there, not here.   */
            /* The latest-listing table matches paths case-sensitively, as     */
            /* ext names are.                                                  */
            Isv_objClient = objClient;
            Isv_objSource = objSource;
            Isv_objEditor = objEditor;
            Isv_lWindow = lWindow;
            Isv_bFat = bFat;
            Isv_sStartName = sStartName;

            /* The box's name is the offered one, and no typed path's name   */
            /* waits for its folder.                                         */
            Isv_sKeptName = sStartName;
            Isv_sPendingName = null;
            Isv_objPendingBranch = null;
            Isv_lPendingNavigation = 0;
            Isv_sListNote = "";
            Isv_dictLatest = new Dictionary<string, RQT_clsRequest>(StringComparer.Ordinal);

            /* The starting state: no outcome yet; no splitter placed; no       */
            /* answer being painted and the start-up branch not yet given up;   */
            /* no selection counted; the root shown, with no listing in the     */
            /* list, so Save refuses until one comes.                           */
            ISV_bAccepted = false;
            ISV_sPath = "";
            ISV_sName = "";
            ISV_bWindowsAsked = false;
            ISV_sWindowsFolder = "";
            ISV_sWindowsName = "";
            Isv_bClosed = false;
            Isv_bSplitSet = false;
            Isv_bRenderingBranch = false;
            Isv_bStartupSuperseded = false;
            Isv_lNavigation = 0;
            Isv_sDir = "/";
            Isv_sListedDir = null;
            Isv_lstShown = new List<EEN_stEntry>();

            /* The starting folder's names, split once: empty pieces are      */
            /* dropped, so a doubled or trailing slash changes nothing, and   */
            /* the root gives none.  The branch listing and its answer both   */
            /* walk this one array (Isv_LoadBranch, Isv_OnBranchListed).      */
            Isv_arrStart = sStartDir.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            /* The caller's dialog font, on the form itself: the controls   */
            /* below are given no font of their own, so each takes its      */
            /* parent's (as found), and the whole dialog follows the        */
            /* program's text size.                                         */
            this.Font = fntDialog;

            /* A sizable dialog titled with the file's name.  It can be        */
            /* maximised, for a deep tree or long names, but not minimised:    */
            /* it is modal over the editor, and minimised it would leave the   */
            /* editor blocked with nothing on screen to answer.  Centred on    */
            /* its owner and kept out of the taskbar, as a child dialog is.    */
            /* The minimum size is a chosen value, not a derived one: below    */
            /* it the two lists would have no useful room.                     */
            this.Text          = "Save " + sStartName + " As";
            this.MinimizeBox   = false;
            this.MaximizeBox   = true;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Padding       = new Padding(ISV_PAD);
            this.MinimumSize   = new Size(ISV_MIN_WIDTH, ISV_MIN_HEIGHT);

            /* The dialog's own queue, made before any control can post to it:   */
            /* Save, a row's activation, Up and New Folder each run there, one   */
            /* at a time, never in their handlers (Isv_RunItem).                 */
            Isv_objQueue = new WQU_clsWindowQueue(this, Isv_RunItem);

            /* Cancel, made and added first: the button row flows right to     */
            /* left (below), so the first added is the rightmost, where the    */
            /* Windows dialogs put Cancel.  Sized to its caption, so it fits   */
            /* at any text size.                                               */
            Isv_btnCancel          = new Button();
            Isv_btnCancel.Text     = "Cancel";
            Isv_btnCancel.AutoSize = true;
            Isv_btnCancel.Click   += Isv_OnCancelClicked;

            /* Save, left of Cancel, with Alt+S; it is also the dialog's   */
            /* Accept button (below), so Enter anywhere presses it.        */
            Isv_btnSave          = new Button();
            Isv_btnSave.Text     = "&Save";
            Isv_btnSave.AutoSize = true;
            Isv_btnSave.Click   += Isv_OnSaveClicked;

            /* Their row along the bottom, half a pad above, flowing right   */
            /* to left so Cancel is rightmost.  Added to the form last, it   */
            /* docks first and so is the lowest row (see the adds below).    */
            Isv_pnlButtons               = new FlowLayoutPanel();
            Isv_pnlButtons.Dock          = DockStyle.Bottom;
            Isv_pnlButtons.FlowDirection = FlowDirection.RightToLeft;
            Isv_pnlButtons.AutoSize      = true;
            Isv_pnlButtons.Padding       = new Padding(0, ISV_PAD / 2, 0, 0);
            Isv_pnlButtons.Controls.Add(Isv_btnCancel);
            Isv_pnlButtons.Controls.Add(Isv_btnSave);

            /* The name row's caption.  Its "&n" gives Alt+N, which moves    */
            /* the focus on to the box after it (a label's mnemonic, as      */
            /* found); anchored left and centred so it sits level with the   */
            /* box's text.                                                   */
            Isv_lblName           = new Label();
            Isv_lblName.Text      = "File &name:";
            Isv_lblName.AutoSize  = true;
            Isv_lblName.Anchor    = AnchorStyles.Left;
            Isv_lblName.TextAlign = ContentAlignment.MiddleLeft;

            /* The box, offering the file's present name, filling the rest   */
            /* of the row with a gap after the caption.                      */
            Isv_txtName        = new TextBox();
            Isv_txtName.Text   = sStartName;
            Isv_txtName.Dock   = DockStyle.Fill;
            Isv_txtName.Margin = new Padding(ISV_GAP, 0, 0, 0);

            /* The two in a one-row table (docked to the bottom, the caption   */
            /* column sized to the caption, the box's column the rest), half   */
            /* a pad above it to part it from the status line.                 */
            Isv_pnlName          = Isv_NewRow();
            Isv_pnlName.Padding  = new Padding(0, ISV_PAD / 2, 0, 0);
            Isv_pnlName.Controls.Add(Isv_lblName, 0, 0);
            Isv_pnlName.Controls.Add(Isv_txtName, 1, 0);

            /* The status line, between the lists and the name row.  Not      */
            /* auto-sized: its height is set once, after it is parented       */
            /* (below), to one line and half a pad, and kept whether or not   */
            /* it holds a message; of a message longer than the width, only   */
            /* what one line holds is seen.                                   */
            Isv_lblStatus           = new Label();
            Isv_lblStatus.Dock      = DockStyle.Bottom;
            Isv_lblStatus.AutoSize  = false;
            Isv_lblStatus.Text      = "";
            Isv_lblStatus.TextAlign = ContentAlignment.MiddleLeft;

            /* The "Save in" row's caption, with Alt+I to the box after it,   */
            /* anchored and centred as the name row's is.                     */
            Isv_lblWhere           = new Label();
            Isv_lblWhere.Text      = "Save &in:";
            Isv_lblWhere.AutoSize  = true;
            Isv_lblWhere.Anchor    = AnchorStyles.Left;
            Isv_lblWhere.TextAlign = ContentAlignment.MiddleLeft;

            /* The directory shown, read-only (it changes by browsing, not   */
            /* typing), starting at the root, with a gap on either side to   */
            /* part it from the caption and from Up.                         */
            Isv_txtWhere          = new TextBox();
            Isv_txtWhere.ReadOnly = true;
            Isv_txtWhere.Text     = "/";
            Isv_txtWhere.Dock     = DockStyle.Fill;
            Isv_txtWhere.Margin   = new Padding(ISV_GAP, 0, ISV_GAP, 0);

            /* Up, with the up-arrow icon (the picture the image pane gives   */
            /* its ".." row) left of its caption.  It is enabled or not by    */
            /* the folder shown (Isv_SetDirectory: not at the root); before   */
            /* any folder has been shown, the click handler's own test        */
            /* covers it.                                                     */
            Isv_btnUp            = new Button();
            Isv_btnUp.Text       = "&Up";
            Isv_btnUp.AutoSize   = true;
            Isv_btnUp.ImageList  = imlIcons;
            Isv_btnUp.ImageIndex = EIC_EntryIcons.EIC_ICON_UP;
            Isv_btnUp.ImageAlign = ContentAlignment.MiddleLeft;
            Isv_btnUp.TextAlign  = ContentAlignment.MiddleRight;
            Isv_btnUp.Click     += Isv_OnUpClicked;

            /* New Folder, with the folder icon, a gap after Up.  Enabled   */
            /* only with an editor record, which its request must carry     */
            /* (Isv_RequestNewFolder); an editor's Save As input has one    */
            /* (CommandInputs.cs ICV_objEditor).                            */
            Isv_btnNewFolder            = new Button();
            Isv_btnNewFolder.Text       = "New &Folder";
            Isv_btnNewFolder.AutoSize   = true;
            Isv_btnNewFolder.ImageList  = imlIcons;
            Isv_btnNewFolder.ImageIndex = EIC_EntryIcons.EIC_ICON_FOLDER;
            Isv_btnNewFolder.ImageAlign = ContentAlignment.MiddleLeft;
            Isv_btnNewFolder.TextAlign  = ContentAlignment.MiddleRight;
            Isv_btnNewFolder.Margin     = new Padding(ISV_GAP, 0, 0, 0);
            Isv_btnNewFolder.Enabled    = objEditor != null;
            Isv_btnNewFolder.Click     += Isv_OnNewFolderClicked;

            /* The four in a one-row table re-docked to the top, half a pad   */
            /* below.  Isv_NewRow gives two columns, caption and box; two     */
            /* more, sized to their buttons, are added after them, so the     */
            /* box still takes whatever width the other three leave.          */
            Isv_pnlWhere         = Isv_NewRow();
            Isv_pnlWhere.Dock    = DockStyle.Top;
            Isv_pnlWhere.Padding = new Padding(0, 0, 0, ISV_PAD / 2);
            Isv_pnlWhere.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Isv_pnlWhere.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Isv_pnlWhere.ColumnCount = 4;
            Isv_pnlWhere.Controls.Add(Isv_lblWhere, 0, 0);
            Isv_pnlWhere.Controls.Add(Isv_txtWhere, 1, 0);
            Isv_pnlWhere.Controls.Add(Isv_btnUp, 2, 0);
            Isv_pnlWhere.Controls.Add(Isv_btnNewFolder, 3, 0);

            /* The tree.  Its selection stays highlighted while the focus is   */
            /* in the name box, so the directory being saved into stays        */
            /* marked.  Every node, selected or not, shows the one folder      */
            /* icon.  The "/" separator matters only to FullPath, which        */
            /* nothing here reads: each node's image path is in its Tag.       */
            /* Expanding a node lists it if it has not been listed             */
            /* (Isv_OnBeforeExpand); selecting one shows it                    */
            /* (Isv_OnAfterSelect).                                            */
            Isv_treFolders                    = new TreeView();
            Isv_treFolders.Dock               = DockStyle.Fill;
            Isv_treFolders.HideSelection      = false;
            Isv_treFolders.ImageList          = imlIcons;
            Isv_treFolders.ImageIndex         = EIC_EntryIcons.EIC_ICON_FOLDER;
            Isv_treFolders.SelectedImageIndex = EIC_EntryIcons.EIC_ICON_FOLDER;
            Isv_treFolders.PathSeparator      = "/";
            Isv_treFolders.BeforeExpand      += Isv_OnBeforeExpand;
            Isv_treFolders.AfterSelect       += Isv_OnAfterSelect;

            /* The root, its Tag its image path.  Its placeholder child      */
            /* (no Tag) gives it an expand box before anything is known of   */
            /* it, and marks it as not yet listed (Isv_NeedsLoading).        */
            nodRoot     = new TreeNode("/");
            nodRoot.Tag = "/";
            nodRoot.Nodes.Add(new TreeNode(ISV_PLACEHOLDER));
            Isv_treFolders.Nodes.Add(nodRoot);

            /* The entry list, in columns: name with its icon, size, modify   */
            /* time.  One whole row selected at a time, since selecting and   */
            /* activating each act on exactly one entry; the selection        */
            /* stays visible when the focus moves to the name box.            */
            Isv_lstEntries                = new ListView();
            Isv_lstEntries.Dock           = DockStyle.Fill;
            Isv_lstEntries.View           = View.Details;
            Isv_lstEntries.FullRowSelect  = true;
            Isv_lstEntries.MultiSelect    = false;
            Isv_lstEntries.HideSelection  = false;
            Isv_lstEntries.SmallImageList = imlIcons;

            for (i = 0; i < Isv_arrColumns.Length; i++)
            {   /* One column heading per iteration. */
                Isv_lstEntries.Columns.Add(Isv_arrColumns[i]);
            }

            /* The size column right-aligned, so the figures line up; a        */
            /* double-click or Enter on an entry (as found), and a change of   */
            /* the selection, each have a handler.                             */
            Isv_lstEntries.Columns[1].TextAlign    = HorizontalAlignment.Right;
            Isv_lstEntries.ItemActivate           += Isv_OnEntryActivated;
            Isv_lstEntries.SelectedIndexChanged   += Isv_OnEntrySelected;

            /* The two side by side, divided by a splitter the user can     */
            /* drag; the tree's share of the width is set once there is a   */
            /* width to share (Isv_PlaceSplitter).                          */
            Isv_split              = new SplitContainer();
            Isv_split.Dock         = DockStyle.Fill;
            Isv_split.Orientation  = Orientation.Vertical;
            Isv_split.Panel1.Controls.Add(Isv_treFolders);
            Isv_split.Panel2.Controls.Add(Isv_lstEntries);

            /* Docking runs in reverse order of adding (as found): the       */
            /* buttons dock first and are lowest, the name row above them,   */
            /* the status line above that; the Save in row takes the top;    */
            /* and the split, added first, fills what the rows leave.        */
            this.Controls.Add(Isv_split);
            this.Controls.Add(Isv_pnlWhere);
            this.Controls.Add(Isv_lblStatus);
            this.Controls.Add(Isv_pnlName);
            this.Controls.Add(Isv_pnlButtons);

            /* The status line's height is measured only now, once it has its   */
            /* parent and so the dialog's font; before, it would be the         */
            /* default's and clip a message.                                    */
            Isv_lblStatus.Height = Isv_lblStatus.PreferredHeight + (ISV_PAD / 2);

            /* Enter presses Save wherever the focus is, Escape presses    */
            /* Cancel; and the dialog's first client size, which docking   */
            /* then shares out among the rows and the split.               */
            this.AcceptButton = Isv_btnSave;
            this.CancelButton = Isv_btnCancel;
            this.ClientSize   = new Size(ISV_WIDTH, ISV_HEIGHT);

            /* The three hooks, last, so the building above runs none of        */
            /* them.  Shown posts the first listing (Isv_OnShown) -- nothing    */
            /* is listed for a dialog that is never shown.  Resize places the   */
            /* splitter the first time there is a width, fits the columns and   */
            /* keeps the selected directory in view (Isv_OnResize).             */
            /* FormClosed raises the flag that makes a listing answering        */
            /* after the close be dropped (Isv_OnFormClosed).                   */
            this.Shown      += Isv_OnShown;
            this.Resize     += Isv_OnResize;
            this.FormClosed += Isv_OnFormClosed;
        }

        /*--------------------------------------------------------------------*/
        /* Isv_NewRow:                                                        */
        /*                                                                    */
        /* A one-row table for a caption and a box: the first column takes    */
        /* its caption's width, the second the rest.  The "Save in" row adds  */
        /* two more columns for its buttons.                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     TableLayoutPanel : the row, docked to the bottom and sized to  */
        /*                        its content; the caller re-docks the top    */
        /*                        one.                                        */
        /*--------------------------------------------------------------------*/
        private static TableLayoutPanel Isv_NewRow()
        {
            TableLayoutPanel pnlRow;

            /* One row of two columns, as tall as its content: the first      */
            /* sized to the caption, so the caption is never cut at a large   */
            /* text size; the second taking every pixel left, so the box      */
            /* widens with the dialog.  Docked to the bottom, where the       */
            /* name row goes; the Save in row, its other user, re-docks it.   */
            pnlRow             = new TableLayoutPanel();
            pnlRow.Dock        = DockStyle.Bottom;
            pnlRow.AutoSize    = true;
            pnlRow.ColumnCount = 2;
            pnlRow.RowCount    = 1;
            pnlRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pnlRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            /* Made, empty; the caller adds its controls by cell. */
            return(pnlRow);
        }

        /*------------------------------------------------------------------*/
        /* Isv_OnShown:                                                     */
        /*                                                                  */
        /* The dialog is on screen: place the splitter, put the focus in    */
        /* the name box, and post the first listing -- the starting folder  */
        /* and every folder above it, as one request.                       */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objSender : the dialog (unused).                             */
        /*     evArgs    : unused.                                          */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the name selected for typing over; the branch posted. */
        /*------------------------------------------------------------------*/
        private void Isv_OnShown(object objSender, EventArgs evArgs)
        {
            /* Now that the dialog has its layout: the splitter's first      */
            /* place (if the split has a width yet -- Resize tries again),   */
            /* and the offered name selected, so typing replaces it.         */
            Isv_PlaceSplitter();
            Isv_txtName.SelectAll();
            Isv_txtName.Focus();

            /* The start-up listing.  Posted here, not in the constructor,   */
            /* so a dialog that is never shown asks for nothing.             */
            Isv_LoadBranch();
        }

        /*------------------------------------------------------------------*/
        /* Isv_LoadBranch:                                                  */
        /*                                                                  */
        /* Post a branch listing -- the start-up one, or a typed path's     */
        /* (Isv_ResolveTypedPath): the root, each folder down to the one    */
        /* Isv_arrStart names, and that one itself, as ONE request          */
        /* (PickerBranch, CommandPickerBranch.cs) -- the worker lists them  */
        /* in order and stops at the first that fails, so the dialog never  */
        /* has to choose the next folder to ask for from an answer.         */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     None.                                                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the request posted; its answer comes to               */
        /*            Isv_OnBranchListed.                                   */
        /*------------------------------------------------------------------*/
        private void Isv_LoadBranch()
        {
            List<string> lstPaths;
            string sPath;
            int i;

            /* The paths, root first: "/" alone for a start at the root,      */
            /* otherwise one more per name of the starting folder, built up   */
            /* from the names split in the constructor.  The last is the      */
            /* starting folder itself.                                        */
            lstPaths = new List<string>();
            lstPaths.Add("/");
            sPath = "";
            for (i = 0; i < Isv_arrStart.Length; i++)
            {   /* One folder further down per iteration. */
                sPath += "/" + Isv_arrStart[i];
                lstPaths.Add(sPath);
            }

            /* The request, kept as the one awaited: from this input, bound   */
            /* to the dialog's filesystem, with BRV_PARENT_DIRECTORIES --     */
            /* the folders above keep their subfolders only (all the tree     */
            /* needs), the starting folder everything, for the entry list.    */
            /* Being a kind of its own, it is never replaced by one of the    */
            /* dialog's single-folder listings (the worker replaces only a    */
            /* waiting request of the same kind and stream).                  */
            Isv_objBranch = CFC_clsCommands.CFC_Create(KND_enmKind.PickerBranch,
                Isv_lWindow, Isv_objSource, Isv_objSource.STV_objTarget,
                lstPaths.ToArray(), 0, BRV_clsBranchView.BRV_PARENT_DIRECTORIES,
                null, "", "initial-branch");
            Isv_objClient.CLI_Post(Isv_objBranch, Isv_OnBranchListed);
        }

        /*------------------------------------------------------------------*/
        /* Isv_SetDirectory:                                                */
        /*                                                                  */
        /* Show a folder as the one selected, BEFORE its entries are known: */
        /* the old folder's rows go at once, so the dialog never shows one  */
        /* folder's path with another folder's entries, and Save is refused */
        /* until this folder's own listing fills the list.                  */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     nodDirectory : the folder's node (its Tag is its path).      */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the path shown, the list empty, "Reading ..." said.   */
        /*------------------------------------------------------------------*/
        private void Isv_SetDirectory(TreeNode nodDirectory)
        {
            /* The folder recorded first, and no listing counted as shown or   */
            /* awaited for the list, BEFORE any control is touched, so         */
            /* nothing that runs from here on -- an event the clear below      */
            /* raises, a Save pressed before the listing comes -- finds the    */
            /* old folder's listing still counted (Isv_AcceptName tests        */
            /* Isv_sListedDir).  Then the path in "Save in", and Up enabled    */
            /* except at the root.                                             */
            Isv_sDir = (string)nodDirectory.Tag;
            Isv_sListedDir = null;
            Isv_objListRequest = null;
            Isv_sListNote = "";
            Isv_txtWhere.Text = Isv_sDir;
            Isv_btnUp.Enabled = nodDirectory.Parent != null;

            /* The old rows and the entries behind them cleared together     */
            /* (each row's Tag indexes Isv_lstShown), the drawing held for   */
            /* the clear, and the columns refitted to the empty list.        */
            Isv_lstShown.Clear();
            Isv_lstEntries.BeginUpdate();
            Isv_lstEntries.Items.Clear();
            Isv_lstEntries.EndUpdate();
            Isv_FitColumns();

            /* Said on the status line until the listing answers. */
            Isv_lblStatus.Text = "Reading " + Isv_sDir + " ...";
        }

        /*--------------------------------------------------------------------*/
        /* Isv_ReadRows:                                                      */
        /*                                                                    */
        /* Whether a listing's answer holds this folder's entries -- a        */
        /* success, with rows, for exactly the path asked -- and, if not,     */
        /* why, in words.  A refusal or a "success" without the folder's      */
        /* rows is never taken for an empty folder: that would let Save       */
        /* check a name against nothing.                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objResult  : the answer.                                       */
        /*     sDirectory : the folder asked for (compared case-sensitively). */
        /*     objRows    : receives the answer's own rows (not a copy);      */
        /*                  to be used only when true is returned.            */
        /*     sFailure   : receives "" on success, otherwise "Cannot read    */
        /*                  <folder>: <why>".                                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the rows are this folder's.                   */
        /*--------------------------------------------------------------------*/
        private static bool Isv_ReadRows(RSL_clsResult objResult, string sDirectory,
            out ELR_clsListResult objRows, out string sFailure)
        {
            int iStatus;
            string sDetail;

            /* The answer's status and reason, and its rows if it carries    */
            /* any.  The status is kept apart from the rows: a refusal has   */
            /* none, and rows for another path are not this folder's.        */
            iStatus = objResult.RSL_iStatus;
            sDetail = objResult.RSL_sDetail;
            objRows = objResult.RSL_objPayload as ELR_clsListResult;
            if (objRows == null || objRows.ELR_lstEntries == null ||
                !String.Equals(objRows.ELR_sImageDir, sDirectory, StringComparison.Ordinal))
            {   /* No rows, or rows for another folder.  A failure keeps its   */
                /* own status and reason; a "success" like this is a fault     */
                /* in the answer, made a failure here.                         */
                if (iStatus == SYS_clsSystem.SYS_OK)
                {   /* Said to have succeeded: counted as internal. */
                    iStatus = SYS_clsSystem.SYS_INTERNAL;
                }

                if (String.IsNullOrEmpty(sDetail))
                {   /* No reason given: this one, which says what was wrong. */
                    sDetail = "No matching directory data was returned.";
                }
            }

            /* Nothing to say yet.  (SYS_OK and ENG_OK are both 0, so the    */
            /* test below takes a command status and an engine one alike.)   */
            sFailure = "";
            if (iStatus != ENG_clsNative.ENG_OK)
            {   /* Not this folder's entries: the reason, for the caller to   */
                /* show -- or to put after its own message, as a New          */
                /* Folder's answer does when the folder was made but not      */
                /* listed.                                                    */
                if (String.IsNullOrEmpty(sDetail))
                {   /* No reason given: the status code's own sentence --   */
                    /* ENG_StatusText words a command status (SYS_*) as     */
                    /* well as an engine one.                               */
                    sDetail = ENG_clsNative.ENG_StatusText(iStatus);
                }

                /* The folder named, then why. */
                sFailure = "Cannot read " + sDirectory + ": " + sDetail;
                return(false);
            }

            /* This folder's entries, in objRows. */
            return(true);
        }

        /*------------------------------------------------------------------*/
        /* Isv_OnBranchListed:                                              */
        /*                                                                  */
        /* A branch listing's answer (Isv_LoadBranch): open the tree down   */
        /* to the folder it went to -- the starting one, or a typed path's  */
        /* -- one listed folder at a time, and show that folder's entries,  */
        /* then take a typed path's waiting file name (Isv_RunPendingName)  */
        /* -- unless the dialog has closed or the user has already gone     */
        /* elsewhere.  Stops, saying why, at the first folder that failed   */
        /* or is not there.                                                 */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objRequest : the request answered.                           */
        /*     objResult  : the folders' listings, root first, ending at    */
        /*                  the first that failed (BRV_clsBranchView), or   */
        /*                  none when the request itself was refused.       */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the tree opened as far as the listings go; Save       */
        /*            usable only once the starting folder's own entries    */
        /*            are shown.                                            */
        /*------------------------------------------------------------------*/
        private void Isv_OnBranchListed(RQT_clsRequest objRequest, RSL_clsResult objResult)
        {
            BRV_clsBranchView objBranch;
            ELR_clsListResult objRows;
            TreeNode nodCurrent;
            TreeNode nodNext;
            TreeNode nodChild;
            StringComparison enmCompare;
            string sFailure;
            int i;
            int j;

            if (Isv_bClosed || IsDisposed || Isv_objBranch != objRequest || Isv_bStartupSuperseded)
            {   /* The dialog has closed, this is not the branch awaited, or   */
                /* the user has expanded, selected or made a folder since:     */
                /* the answer would move the view away from the user's own     */
                /* choice, so it is dropped.                                   */
                return;
            }

            /* No longer awaited, cleared before any control is touched;   */
            /* then the listings, if the answer carries any.               */
            Isv_objBranch = null;
            objBranch = objResult.RSL_objPayload as BRV_clsBranchView;
            if (objBranch == null || objBranch.BRV_lstFolders.Count == 0)
            {   /* No listing at all (the request was refused or cancelled):    */
                /* the reason on the status line, or a plain one if none was    */
                /* given.  The tree stays at its root, to be browsed by hand.   */
                Isv_lblStatus.Text = objResult.RSL_sDetail;
                if (String.IsNullOrEmpty(objResult.RSL_sDetail))
                {   /* No reason given. */
                    Isv_lblStatus.Text = "The folder was not read.";
                }

                /* Nothing shown, so Save stays refused. */
                return;
            }

            /* How the walk matches a folder's path and name: exactly on       */
            /* ext2/3/4, whose names are case-sensitive; letter case aside     */
            /* on FAT and exFAT, which match names so -- a typed "SUB" finds   */
            /* the folder "sub", and the worker lists "/SUB" as that folder    */
            /* (the engine folds it the same way), so the listing's path is    */
            /* compared the same way too.                                      */
            enmCompare = StringComparison.Ordinal;
            if (Isv_bFat)
            {   /* FAT or exFAT: ASCII letters' case aside. */
                enmCompare = StringComparison.OrdinalIgnoreCase;
            }

            /* The listings painted with the flag up, so the selections and   */
            /* expansions made below post no listing of their own (the        */
            /* tree's handlers test it): their folders are already listed.    */
            Isv_bRenderingBranch = true;
            try
            {   /* Released in the finally, however the walk ends. */
                nodCurrent = Isv_treFolders.Nodes[0];
                for (i = 0; i < objBranch.BRV_lstFolders.Count; i++)
                {   /* One folder of the branch per iteration, root first: its   */
                    /* listing goes on the node the walk has reached.            */
                    if (!string.Equals((string)nodCurrent.Tag, objBranch.BRV_lstFolders[i].FVW_sFolder, enmCompare) ||
                        objBranch.BRV_lstFolders[i].FVW_lEpoch != Isv_objSource.STV_lEpoch)
                    {   /* The listing is of another path, or of another opening   */
                        /* of the image (its epoch): not this node's.  Said, and   */
                        /* the walk stops.                                         */
                        Isv_lblStatus.Text = "The folder listing did not match its source.";
                        return;
                    }

                    /* The node selected and shown as the folder, the old rows     */
                    /* gone -- even when its listing turns out to have failed --   */
                    /* then its listing taken.  A failure is the branch's end      */
                    /* (the worker lists nothing below a failed folder): said,     */
                    /* and the walk stops here, with nothing shown, so Save        */
                    /* stays refused.                                              */
                    Isv_treFolders.SelectedNode = nodCurrent;
                    Isv_SetDirectory(nodCurrent);
                    if (!Isv_ReadRows(objBranch.BRV_lstFolders[i].FVW_objResult,
                        objBranch.BRV_lstFolders[i].FVW_sFolder, out objRows, out sFailure))
                    {   /* Not listed: why, on the status line.  The rows are   */
                        /* matched against the path the worker was asked for,   */
                        /* which the test above has found to be this node's.    */
                        Isv_lblStatus.Text = sFailure;
                        return;
                    }

                    /* Its subfolders into the tree, merged with what is there   */
                    /* (Isv_MergeNode), the node opened.                         */
                    Isv_MergeNode(nodCurrent, objRows);
                    if (i == Isv_arrStart.Length)
                    {   /* The starting folder (one listing per name, after the   */
                        /* root's): its listing alone carries the files too, so   */
                        /* it fills the entry list and makes Save usable; the     */
                        /* status line cleared, the node scrolled into view.      */
                        Isv_FillEntries(objRows.ELR_lstEntries);
                        Isv_sListedDir = Isv_sDir;
                        Isv_lblStatus.Text = "";
                        nodCurrent.EnsureVisible();

                        if (Isv_sPendingName != null && Isv_objPendingBranch == objRequest)
                        {   /* A typed path's folder, reached, with a     */
                            /* file name waiting: taken on a later        */
                            /* turn, once this painting is over and the   */
                            /* tree's handlers post again -- a name       */
                            /* that is a folder here must be entered,     */
                            /* which lists it (Isv_RunPendingName).       */
                            /* The selection count is noted, so a click   */
                            /* made first drops the name.                 */
                            Isv_lPendingNavigation = Isv_lNavigation;
                            this.BeginInvoke(new MethodInvoker(Isv_RunPendingName));
                        }

                        return;
                    }

                    /* The next node: the child, in the tree just merged, that   */
                    /* has the next name of the starting path -- a folder the    */
                    /* listing found, never one made up from the path.           */
                    nodNext = null;
                    for (j = 0; j < nodCurrent.Nodes.Count; j++)
                    {   /* One child per iteration; the placeholder (no Tag) is   */
                        /* passed over.                                           */
                        nodChild = nodCurrent.Nodes[j];
                        if (nodChild.Tag != null && string.Equals(nodChild.Text, Isv_arrStart[i], enmCompare))
                        {   /* The name, compared exactly on ext2/3/4,   */
                            /* whose names are case-sensitive, and       */
                            /* letter case aside on FAT (enmCompare,     */
                            /* above).                                   */
                            nodNext = nodChild;
                            break;
                        }
                    }

                    if (nodNext == null)
                    {   /* The folder above does not hold the next one     */
                        /* (removed, never there, a typed name that is     */
                        /* no folder): said, naming it, and the user       */
                        /* chooses a folder by hand.  The answer's later   */
                        /* listings, if any, are not used, as they would   */
                        /* have no node.                                   */
                        Isv_lblStatus.Text = "There is no folder " + Isv_JoinPath((string)nodCurrent.Tag, Isv_arrStart[i]) +
                            ". Choose another folder in the tree.";

                        /* The folder the walk stopped in is the one shown,   */
                        /* so its entries are listed for the list: the        */
                        /* branch's rows for it are its subfolders only       */
                        /* (BRV_PARENT_DIRECTORIES), which Save could not     */
                        /* check a name against, so a full listing is         */
                        /* posted (Isv_LoadNode).  The line above is kept     */
                        /* as the note that listing's answer shows, and a     */
                        /* waiting file name is dropped -- its folder was     */
                        /* not reached, and it stays in the box for the       */
                        /* user to save here or elsewhere.                    */
                        Isv_sListNote = Isv_lblStatus.Text;
                        Isv_sPendingName = null;
                        Isv_objPendingBranch = null;
                        Isv_LoadNode(nodCurrent, true);
                        return;
                    }

                    /* On down: its listing is the next in the answer. */
                    nodCurrent = nodNext;
                }
            }
            finally
            {   /* The flag down: the tree's own events post listings again. */
                Isv_bRenderingBranch = false;
            }
        }

        /*------------------------------------------------------------------*/
        /* Isv_LoadNode:                                                    */
        /*                                                                  */
        /* Post one folder's listing (PickerList): for the tree alone (an   */
        /* expansion), or for the tree and the entry list (a selection).    */
        /* The request is recorded as the newest for its folder, and for    */
        /* the list when it is a selection's, so an older answer arriving   */
        /* later is not painted over it.                                    */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     nodDirectory : the folder's node.                            */
        /*     bShowList    : true when the entry list is to show it too.   */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : posted; the answer comes to ISV_ApplyNodeResult.      */
        /*------------------------------------------------------------------*/
        private void Isv_LoadNode(TreeNode nodDirectory, bool bShowList)
        {
            string sDirectory;
            string sStream;
            RQT_clsRequest objRequest;
            SNR_clsNodeRead objRead;

            /* The stream the request joins.  The worker keeps only the        */
            /* newest waiting request of a stream and answers the one it       */
            /* replaces as cancelled (CommandSystem.cs, the Latest class).     */
            /* An expansion's stream is its folder's, so expansions of         */
            /* different folders all run; every selection shares one stream,   */
            /* so clicking through several folders lists only the last one     */
            /* still waiting.                                                  */
            sDirectory = (string)nodDirectory.Tag;
            sStream = "node:" + sDirectory;
            if (bShowList)
            {   /* A selection: the one stream all selections share. */
                sStream = "visible-list";
            }

            /* The request: from this input, bound to the dialog's             */
            /* filesystem, one folder, in its stream -- path and filesystem    */
            /* only; the node and the list stay here, in the reply's record.   */
            /* Recorded as its folder's newest (any later listing of the       */
            /* folder, either kind, replaces it there).                        */
            objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.PickerList,
                Isv_lWindow, Isv_objSource, Isv_objSource.STV_objTarget,
                new string[] { sDirectory }, 0, 0, null, "", sStream);
            Isv_dictLatest[sDirectory] = objRequest;
            if (bShowList)
            {   /* A selection's: also the one whose answer may fill the   */
                /* entry list.                                             */
                Isv_objListRequest = objRequest;
            }

            /* Posted, with the record whose SNR_OnResult the client calls   */
            /* with the answer, so the node and the list intent come back    */
            /* with it and no table of pending reads is needed.              */
            objRead = new SNR_clsNodeRead(this, nodDirectory, bShowList);
            Isv_objClient.CLI_Post(objRequest, objRead.SNR_OnResult);
        }

        /*------------------------------------------------------------------*/
        /* ISV_ApplyNodeResult:                                             */
        /*                                                                  */
        /* A folder listing's answer (SNR_OnResult): its subfolders into    */
        /* the tree if it is still the folder's newest listing, its entries */
        /* into the list if it is still the list's -- each tested on its    */
        /* own, since one can be current while the other is not.  Dropped   */
        /* when the dialog has closed or the node has left the tree.        */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objRead    : the listing's record (its node, its list        */
        /*                  intent).                                        */
        /*     objRequest : the request answered.                           */
        /*     objResult  : the entries, or a refusal, a cancellation or a  */
        /*                  failure.                                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : what is still current painted, the rest dropped; no   */
        /*            further request posted.                               */
        /*------------------------------------------------------------------*/
        internal void ISV_ApplyNodeResult(SNR_clsNodeRead objRead,
            RQT_clsRequest objRequest, RSL_clsResult objResult)
        {
            RQT_clsRequest objLatest;
            ELR_clsListResult objRows;
            string sDirectory;
            string sFailure;
            bool bTree;
            bool bList;

            if (Isv_bClosed || IsDisposed || objRead.SNR_nodDirectory.TreeView != Isv_treFolders)
            {   /* The dialog has closed, or the node has left the tree (a   */
                /* listing of its parent no longer found it): nowhere to     */
                /* paint.                                                    */
                return;
            }

            /* Which of the two the answer is still current for: the tree,   */
            /* if it is the folder's newest listing; the list, if it is a    */
            /* selection's, still the one awaited, and the folder is still   */
            /* the one shown.  Tested apart, so a newer expansion of the     */
            /* folder cannot keep a selection's answer from the list.        */
            sDirectory = objRequest.RQT_lstPaths[0];
            bTree = Isv_dictLatest.TryGetValue(sDirectory, out objLatest) && objLatest == objRequest;
            bList = objRead.SNR_bShowList && Isv_objListRequest == objRequest && Isv_sDir == sDirectory;
            if (!bTree && !bList)
            {   /* Current for neither: a newer listing has taken both. */
                return;
            }

            if (bTree)
            {   /* No longer the folder's awaited listing, cleared before   */
                /* anything is painted, so this answer is applied once.     */
                Isv_dictLatest.Remove(sDirectory);
            }

            if (bList)
            {   /* Likewise for the list. */
                Isv_objListRequest = null;
            }

            if (objResult.RSL_iStatus == SYS_clsSystem.SYS_CANCELLED ||
                objResult.RSL_iStatus == SYS_clsSystem.SYS_DROPPED)
            {   /* Cancelled -- by a Cancel or an Exit, or replaced in its    */
                /* stream by a newer selection (whose own answer then fills   */
                /* the list, while this one can still be its folder's         */
                /* newest for the tree) -- or dropped as a duplicate: no      */
                /* entries.  The node keeps its placeholder, and a list       */
                /* still waiting stays empty, so Save stays refused.          */
                if (bList)
                {   /* The list's: the "Reading ..." line replaced by what   */
                    /* happened and what to do.                              */
                    Isv_lblStatus.Text = "The folder read was cancelled. Choose a folder again.";
                }

                /* Nothing more from this answer. */
                return;
            }

            /* The entries, if the answer holds this folder's (Isv_ReadRows)   */
            /* -- one set of rows, used by both painters below, not copied.    */
            if (!Isv_ReadRows(objResult, sDirectory, out objRows, out sFailure))
            {   /* A failure (or a "success" without the rows): why, on the   */
                /* status line; nothing painted, so Save stays refused if     */
                /* this was the list's.                                       */
                Isv_lblStatus.Text = sFailure;
                return;
            }

            if (bTree)
            {   /* The tree's: the subfolders merged under the node. */
                Isv_MergeNode(objRead.SNR_nodDirectory, objRows);
            }

            if (bList)
            {   /* The list's: the entries shown, the status line cleared,    */
                /* and the folder recorded as listed, which makes Save        */
                /* usable for it.  A note waiting for this listing (a typed   */
                /* path's "no folder" stop) takes the status line instead     */
                /* of the blank, once.                                        */
                Isv_lblStatus.Text = Isv_sListNote;
                Isv_sListNote = "";
                Isv_FillEntries(objRows.ELR_lstEntries);
                Isv_sListedDir = sDirectory;
            }
        }

        /*--------------------------------------------------------------------*/
        /* Isv_PlaceSplitter:                                                 */
        /*                                                                    */
        /* Give the tree its share of the width, once: after that the user's  */
        /* own placing of the splitter is kept through resizes.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the splitter is placed, or was already.                 */
        /*--------------------------------------------------------------------*/
        private void Isv_PlaceSplitter()
        {
            if (Isv_bSplitSet || Isv_split.Width <= 0)
            {   /* Already placed -- a later call must not undo where the user   */
                /* has dragged it -- or the split has no width yet (the first    */
                /* Shown can come before the layout gives it one); Resize        */
                /* calls again once there is one.                                */
                return;
            }

            /* The tree's share of the width, and the flag that keeps any   */
            /* later placing the user's own.                                */
            Isv_split.SplitterDistance = Isv_split.Width / ISV_TREE_SHARE;
            Isv_bSplitSet              = true;
        }

        /*--------------------------------------------------------------------*/
        /* Isv_NeedsLoading:                                                  */
        /*                                                                    */
        /* Whether a directory's node still holds its placeholder, that is,   */
        /* has not been listed yet.  The placeholder is told apart by its     */
        /* null Tag; a real directory's Tag is its image path.                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     nodDir : a directory's node.                                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when its one child is the placeholder.             */
        /*--------------------------------------------------------------------*/
        private static bool Isv_NeedsLoading(TreeNode nodDir)
        {
            /* Exactly the placeholder, and nothing else.  A listed directory   */
            /* has either no children (no subdirectories) or real ones, each    */
            /* with its path in Tag, so only a node never listed has a lone     */
            /* child without a Tag.                                             */
            return(nodDir.Nodes.Count == 1 && nodDir.Nodes[0].Tag == null);
        }

        /*------------------------------------------------------------------*/
        /* Isv_OnBeforeExpand:                                              */
        /*                                                                  */
        /* A node is about to open.  One never listed is not opened: its    */
        /* listing is posted instead, and the answer opens it once its real */
        /* children are in (Isv_MergeNode).  One already listed opens as it */
        /* is.                                                              */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objSender : the tree (unused).                               */
        /*     evExpand  : the node, and the Cancel that keeps it shut.     */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the node opens, or stays shut with its listing        */
        /*            posted.                                               */
        /*------------------------------------------------------------------*/
        private void Isv_OnBeforeExpand(object objSender, TreeViewCancelEventArgs evExpand)
        {
            if (Isv_bRenderingBranch)
            {   /* An answer being painted opened it (Isv_MergeNode's own    */
                /* Expand): its folder has just been listed, so nothing is   */
                /* posted.                                                   */
                return;
            }

            if (Isv_NeedsLoading(evExpand.Node))
            {   /* Not listed yet: kept shut, so the placeholder is never      */
                /* seen as a child, and listed for the tree alone.  The user   */
                /* has now chosen where to look, so the start-up branch's      */
                /* answer, if still to come, is dropped.                       */
                evExpand.Cancel = true;
                Isv_bStartupSuperseded = true;
                Isv_LoadNode(evExpand.Node, false);
            }
        }

        /*------------------------------------------------------------------*/
        /* Isv_OnAfterSelect:                                               */
        /*                                                                  */
        /* The user selected a folder: show it at once, its old entries     */
        /* gone (Isv_SetDirectory), and post its listing for the tree and   */
        /* the list.  Save is usable only once that listing's own answer    */
        /* has filled the list.                                             */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objSender : the tree (unused).                               */
        /*     evSelect  : the node selected.                               */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the folder shown and its listing posted, or nothing   */
        /*            done for a selection the program made.                */
        /*------------------------------------------------------------------*/
        private void Isv_OnAfterSelect(object objSender, TreeViewEventArgs evSelect)
        {
            if (Isv_bRenderingBranch)
            {   /* The program selecting the folders of an answer it is   */
                /* painting: shown by that painting, already listed.      */
                return;
            }

            if (evSelect.Node == null || evSelect.Node.Tag == null)
            {   /* No node, or the placeholder (no Tag, no path): nothing to   */
                /* show.  A guard: the placeholder is never visible, as a      */
                /* node holding it is kept shut.                               */
                return;
            }

            /* The user's own choice: the start-up branch's answer, if still   */
            /* to come, is dropped, and the selection counted, so a New        */
            /* Folder answered later does not move the view back               */
            /* (ISV_ApplyFolderResult).  Then shown, and its listing posted.   */
            Isv_bStartupSuperseded = true;
            Isv_lNavigation = checked(Isv_lNavigation + 1);
            Isv_SetDirectory(evSelect.Node);
            Isv_LoadNode(evSelect.Node, true);
        }

        /*--------------------------------------------------------------------*/
        /* Isv_MergeNode:                                                     */
        /*                                                                    */
        /* Put a listed folder's subfolders under its node and open it --     */
        /* merged with the children already there, not rebuilt, so the        */
        /* subfolders the user has opened beneath it stay open.  The caller   */
        /* has checked the rows are this folder's (Isv_ReadRows).             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     nodDir  : the folder's node, in the tree.                      */
        /*     objRows : its listing's rows (read, not changed).              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the node's children match the listing, and it is        */
        /*            open; nothing is selected or posted.                    */
        /*--------------------------------------------------------------------*/
        private void Isv_MergeNode(TreeNode nodDir, ELR_clsListResult objRows)
        {
            List<string> lstNames;
            TreeNode nodChild;
            int i;

            /* The node's children are its listed subdirectories.  The   */
            /* engine leaves "." and ".." out of every listing           */
            /* (bifrost_engine.c), so each directory entry here is a     */
            /* real child.                                               */
            lstNames = new List<string>();

            for (i = 0; i < objRows.ELR_lstEntries.Count; i++)
            {   /* One directory entry per iteration. */
                if (objRows.ELR_lstEntries[i].EEN_uiType == ENG_clsNative.ENG_TYPE_DIR)
                {   /* A subdirectory: it belongs in the tree. */
                    lstNames.Add(ENG_clsNative.ENG_EntryName(objRows.ELR_lstEntries[i]));
                }
            }

            /* In name order by ordinal value, as the image pane sorts     */
            /* names (Isv_CompareNames); the merge below depends on this   */
            /* order and on the removal's search using the same one.       */
            lstNames.Sort(string.CompareOrdinal);

            /* The tree's drawing held until the children are settled        */
            /* (EndUpdate below), so it is not redrawn once per change (as   */
            /* found).                                                       */
            Isv_treFolders.BeginUpdate();

            if (Isv_NeedsLoading(nodDir))
            {   /* The first listing: the placeholder goes, and the loop below   */
                /* makes every child new.                                        */
                nodDir.Nodes.Clear();
            }
            else
            {   /* Listed before (every click lists again, for the entry list):   */
                /* the children are MERGED, not rebuilt.  A rebuild would close   */
                /* every subfolder open beneath this one; so a child still        */
                /* there is kept as it is -- open or not, listed or not -- and    */
                /* only a folder that has gone is taken out.                      */
                for (i = nodDir.Nodes.Count - 1; i >= 0; i--)
                {   /* One existing child per iteration, from the end so removal is safe. */

                    /* Looked up in the sorted names with the same ordinal   */
                    /* rule they were sorted by -- a binary search under a   */
                    /* different rule could miss a name that is there and    */
                    /* remove a folder still present.                        */
                    if (lstNames.BinarySearch(nodDir.Nodes[i].Text, StringComparer.Ordinal) < 0)
                    {   /* No longer in the directory: its node, and anything   */
                        /* opened beneath it, goes.                             */
                        nodDir.Nodes.RemoveAt(i);
                    }
                }
            }

            for (i = 0; i < lstNames.Count; i++)
            {   /* One subdirectory per iteration, in order: the i-th child is     */
                /* this name, kept when it is there already, made when it is not   */
                /* (a folder made since the last listing, or the first time).      */
                /* This works because the kept children are already in the same    */
                /* order: they were inserted in it, and the tree does not sort     */
                /* its nodes itself (Sorted is never set) -- so walking the two    */
                /* lists together finds each kept child at index i.                */
                if (i < nodDir.Nodes.Count && nodDir.Nodes[i].Text == lstNames[i])
                {   /* Already there, in its place: keep it, open state and all. */
                    nodChild = nodDir.Nodes[i];
                }
                else
                {   /* New here: a node with its own placeholder, until it is opened in its turn. */
                    nodChild     = new TreeNode(lstNames[i]);
                    nodChild.Tag = Isv_JoinPath((string)nodDir.Tag, lstNames[i]);
                    nodChild.Nodes.Add(new TreeNode(ISV_PLACEHOLDER));
                    nodDir.Nodes.Insert(i, nodChild);
                }
            }

            /* The children settled: the tree draws again, once. */
            Isv_treFolders.EndUpdate();

            /* Listed now, so this expands without being cancelled again (a leaf simply has nothing to show). */
            nodDir.Expand();
        }

        /*--------------------------------------------------------------------*/
        /* Isv_FillEntries:                                                   */
        /*                                                                    */
        /* Show a directory's entries in the list: the directories first and  */
        /* then everything else, each group in name order by ordinal value,   */
        /* with the icon the image pane gives that kind of entry, the size    */
        /* (blank for a directory) and the modify time.  The entries are kept */
        /* as listed, since Save checks the typed name against them.          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstEntries : the directory's entries, in on-disk order.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the list shows the entries.                             */
        /*--------------------------------------------------------------------*/
        private void Isv_FillEntries(List<EEN_stEntry> lstEntries)
        {
            List<EEN_stEntry> lstDirs;
            List<EEN_stEntry> lstFiles;
            ListViewItem    lviRow;
            EEN_stEntry stEntry;
            int             i;

            /* The two groups the list shows, directories first, as the   */
            /* image pane and the Windows dialog show them.               */
            lstDirs  = new List<EEN_stEntry>();
            lstFiles = new List<EEN_stEntry>();

            for (i = 0; i < lstEntries.Count; i++)
            {   /* One entry per iteration: directories apart from the rest. */
                if (lstEntries[i].EEN_uiType == ENG_clsNative.ENG_TYPE_DIR)
                {   /* A directory. */
                    lstDirs.Add(lstEntries[i]);
                }
                else
                {   /* A file, a link, a special node. */
                    lstFiles.Add(lstEntries[i]);
                }
            }

            /* Each group in name order, by the same ordinal rule the tree   */
            /* uses, so a folder sits in the same place in both.             */
            lstDirs.Sort(Isv_CompareNames);
            lstFiles.Sort(Isv_CompareNames);

            /* The entries kept, in exactly the order of the rows: each row   */
            /* carries its index into this list in its Tag, which is how      */
            /* selecting, activating and Save get an entry's type back from   */
            /* a row -- so this list and the rows must always be replaced     */
            /* together, as here.                                             */
            Isv_lstShown = new List<EEN_stEntry>();
            Isv_lstShown.AddRange(lstDirs);
            Isv_lstShown.AddRange(lstFiles);

            /* The list emptied of the last directory's rows, its drawing   */
            /* held until every new row is in (EndUpdate below), so it is   */
            /* not redrawn once per row (as found).                         */
            Isv_lstEntries.BeginUpdate();
            Isv_lstEntries.Items.Clear();

            for (i = 0; i < Isv_lstShown.Count; i++)
            {   /* One row per entry, directories first. */

                /* The row: the name, the picture the image pane gives its   */
                /* type, and in Tag the entry's index into Isv_lstShown.     */
                stEntry             = Isv_lstShown[i];
                lviRow            = new ListViewItem(ENG_clsNative.ENG_EntryName(stEntry));
                lviRow.ImageIndex = Isv_IconIndex(stEntry.EEN_uiType);
                lviRow.Tag        = i;

                if (stEntry.EEN_uiType == ENG_clsNative.ENG_TYPE_DIR)
                {   /* A directory shows no size: its size is that of its    */
                    /* own blocks, which says nothing about what it holds.   */
                    lviRow.SubItems.Add("");
                }
                else
                {   /* Anything else: its size in bytes, grouped by      */
                    /* thousands with the user's own separator ("N0").   */
                    lviRow.SubItems.Add(stEntry.EEN_ullSize.ToString("N0"));
                }

                /* The modify time, then the row into the list. */
                lviRow.SubItems.Add(Isv_FormatEpoch(stEntry.EEN_uiMtime));
                Isv_lstEntries.Items.Add(lviRow);
            }

            /* Every row in: the list draws again, and the columns are   */
            /* sized to what they now hold.                              */
            Isv_lstEntries.EndUpdate();
            Isv_FitColumns();
        }

        /*--------------------------------------------------------------------*/
        /* Isv_CompareNames:                                                  */
        /*                                                                    */
        /* The order of two entries: by name, by ordinal value, as the image  */
        /* pane sorts names.                                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stEntryA : one entry.                                          */
        /*     stEntryB : the other.                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : negative, zero or positive as A sorts before, with or    */
        /*           after B.                                                 */
        /*--------------------------------------------------------------------*/
        private static int Isv_CompareNames(EEN_stEntry stEntryA, EEN_stEntry stEntryB)
        {
            /* Ordinal: by UTF-16 code unit, with no locale and no case     */
            /* folding.  That is the byte order of the UTF-8 names (as ls   */
            /* sorts in the C locale) for every character but those above   */
            /* U+FFFF, which sort before U+E000 to U+FFFF here.  The same   */
            /* rule as the image pane (PaneSorter.cs) and the tree here.    */
            return(string.CompareOrdinal(ENG_clsNative.ENG_EntryName(stEntryA), ENG_clsNative.ENG_EntryName(stEntryB)));
        }

        /*--------------------------------------------------------------------*/
        /* Isv_IconIndex:                                                     */
        /*                                                                    */
        /* Which of the program's own icons an entry gets, by its type -- the */
        /* same choice the image pane makes.                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uiType : an ENG_TYPE_* value.                                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : an EIC_ICON_* index into the image list.                 */
        /*--------------------------------------------------------------------*/
        private static int Isv_IconIndex(uint uiType)
        {
            switch (uiType)
            {   /* One entry type per case, the same choice MainForm.cs makes   */
                /* for the image pane: the two device kinds share a picture,    */
                /* as do a pipe and a socket, and a regular file -- or a type   */
                /* not known here -- gets the page.                             */
                case ENG_clsNative.ENG_TYPE_DIR:
                    /* A directory: the folder, the tree's own picture. */
                    return(EIC_EntryIcons.EIC_ICON_FOLDER);
                case ENG_clsNative.ENG_TYPE_SYMLINK:
                    /* A link: the link's own picture. */
                    return(EIC_EntryIcons.EIC_ICON_SYMLINK);
                case ENG_clsNative.ENG_TYPE_CHARDEV:
                case ENG_clsNative.ENG_TYPE_BLOCKDEV:
                    /* Either device node: the one device picture. */
                    return(EIC_EntryIcons.EIC_ICON_DEVICE);
                case ENG_clsNative.ENG_TYPE_FIFO:
                case ENG_clsNative.ENG_TYPE_SOCKET:
                    /* A pipe or a socket: the one pipe picture. */
                    return(EIC_EntryIcons.EIC_ICON_PIPE);
                default:
                    /* A regular file, or a type not known here: the page. */
                    return(EIC_EntryIcons.EIC_ICON_FILE);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Isv_FormatEpoch:                                                   */
        /*                                                                    */
        /* An inode time as the list shows it: local time, to the minute.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uiEpoch : seconds since 1970-01-01 UTC.                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : "yyyy-MM-dd HH:mm" in local time.                     */
        /*--------------------------------------------------------------------*/
        private static string Isv_FormatEpoch(uint uiEpoch)
        {
            DateTime dtUtc;

            /* The inode's seconds counted on from the Unix epoch, as UTC.   */
            /* The count is 32 bits unsigned, so no time past 2106 can be    */
            /* shown, although ext4 can store one.                           */
            dtUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds((double)uiEpoch);

            /* Shown in the user's own time zone. */
            return(dtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        }

        /*--------------------------------------------------------------------*/
        /* Isv_JoinPath:                                                      */
        /*                                                                    */
        /* A directory and a name made into one image path.  The root is the  */
        /* one directory that already ends in its slash.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sDir  : the directory, "/" or "/a/b".                          */
        /*     sName : the name under it.                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : "/name" or "/a/b/name".                               */
        /*--------------------------------------------------------------------*/
        private static string Isv_JoinPath(string sDir, string sName)
        {
            if (sDir == "/")
            {   /* The root already ends in its slash. */
                return("/" + sName);
            }

            /* Anywhere else: directory, slash, name. */
            return(sDir + "/" + sName);
        }

        /*--------------------------------------------------------------------*/
        /* Isv_FitColumns:                                                    */
        /*                                                                    */
        /* Size the Size and Modified columns to their content, measured, and */
        /* give the Name column whatever width is left, so the list is filled */
        /* edge to edge.                                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the columns fit the list's width.                       */
        /*--------------------------------------------------------------------*/
        private void Isv_FitColumns()
        {
            int iColumn;
            int iRow;
            int iWidest;
            int iOthers;

            if (Isv_lstEntries.Columns.Count == 0)
            {   /* No columns to fit.  A guard only: the columns are added in   */
                /* the constructor before Resize is hooked, so it is not        */
                /* expected to fire.                                            */
                return;
            }

            /* The width the measured columns take, summed as each is sized. */
            iOthers = 0;

            for (iColumn = 1; iColumn < Isv_lstEntries.Columns.Count; iColumn++)
            {   /* One column per iteration, the Name column left for last. */

                /* Start from the heading's own width, so a column is never   */
                /* narrower than its heading, even when it is empty.          */
                iWidest = TextRenderer.MeasureText(Isv_lstEntries.Columns[iColumn].Text, Isv_lstEntries.Font).Width;

                for (iRow = 0; iRow < Isv_lstEntries.Items.Count; iRow++)
                {   /* One row's cell in this column per iteration. */
                    iWidest = Math.Max(iWidest, TextRenderer.MeasureText(Isv_lstEntries.Items[iRow].SubItems[iColumn].Text, Isv_lstEntries.Font).Width);
                }

                /* The widest text plus the cell's margins, which the        */
                /* measurement leaves out; counted into the others' total.   */
                Isv_lstEntries.Columns[iColumn].Width = iWidest + ISV_CELL_PAD;
                iOthers = iOthers + Isv_lstEntries.Columns[iColumn].Width;
            }

            /* The Name column takes the rest, never less than its heading.   */
            /* A vertical scroll bar's width is kept back whether or not      */
            /* one shows, so one appearing does not push the row past the     */
            /* edge and bring a horizontal bar with it.                       */
            iWidest = TextRenderer.MeasureText(Isv_lstEntries.Columns[0].Text, Isv_lstEntries.Font).Width + ISV_CELL_PAD;
            Isv_lstEntries.Columns[0].Width = Math.Max(iWidest, Isv_lstEntries.ClientSize.Width - iOthers - SystemInformation.VerticalScrollBarWidth);
        }

        /*--------------------------------------------------------------------*/
        /* Isv_OnEntrySelected:                                               */
        /*                                                                    */
        /* A row was selected in the entry list: a file's name goes into the  */
        /* name box, as the Windows dialog does, so Save would save over it   */
        /* (after the worker's own question).  A directory's does not --      */
        /* entering it is what a directory is for.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the list.                                          */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the name box may hold the selected file's name.         */
        /*--------------------------------------------------------------------*/
        private void Isv_OnEntrySelected(object objSender, EventArgs evArgs)
        {
            EEN_stEntry stEntry;

            if (Isv_lstEntries.SelectedIndices.Count != 1)
            {   /* No single row selected: the selection is being cleared on   */
                /* its way to another row (this event fires for both halves    */
                /* of a change, as found), or a click on empty space cleared   */
                /* it.  The name box keeps what it holds.                      */
                return;
            }

            /* The row's entry, by the index its Tag holds into Isv_lstShown. */
            stEntry = Isv_lstShown[(int)Isv_lstEntries.SelectedItems[0].Tag];

            if (stEntry.EEN_uiType != ENG_clsNative.ENG_TYPE_DIR)
            {   /* A file (or a link or node, which Save will refuse by name):   */
                /* its name replaces whatever was typed, as in the Windows       */
                /* dialog -- and kept as the box's name, which a typed path      */
                /* naming a folder alone later puts back (Isv_sKeptName).        */
                Isv_txtName.Text = ENG_clsNative.ENG_EntryName(stEntry);
                Isv_sKeptName = Isv_txtName.Text;
            }
        }

        /*--------------------------------------------------------------------*/
        /* Isv_OnEntryActivated:                                              */
        /*                                                                    */
        /* A row was double-clicked (or Enter pressed on it): its entry,      */
        /* copied as it is now, posted to the dialog's own queue, which acts  */
        /* on it (Isv_ActivateEntry).  Nothing else is done here.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the list.                                          */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the activation posted, or nothing with no single row.   */
        /*--------------------------------------------------------------------*/
        private void Isv_OnEntryActivated(object objSender, EventArgs evArgs)
        {
            if (Isv_lstEntries.SelectedItems.Count != 1)
            {   /* No single row: nothing to act on. */
                return;
            }

            /* The row's entry, by the index its Tag holds into Isv_lstShown,   */
            /* copied into the item (a struct): a listing that replaces the     */
            /* rows before the item runs cannot change which entry it is.       */
            Isv_objQueue.WQU_Post((int)Isv_enmItem.Activate,
                Isv_lstShown[(int)Isv_lstEntries.SelectedItems[0].Tag]);
        }

        /*--------------------------------------------------------------------*/
        /* Isv_RunItem:                                                       */
        /*                                                                    */
        /* The dialog's own queue's runner (WindowQueue.cs): one item the     */
        /* handlers posted, run to its end -- the name taken, a row acted on, */
        /* the parent shown, a new folder asked for.  The queue calls it once */
        /* per item and never inside another, so a second click while one     */
        /* runs (inside a message box, say) is never run inside it.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iItem   : the item, an Isv_enmItem value.                      */
        /*     objArgs : Activate's entry (an EEN_stEntry); unused otherwise. */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item's work done, or nothing for an unknown item.   */
        /*--------------------------------------------------------------------*/
        private void Isv_RunItem(int iItem, object objArgs)
        {
            switch ((Isv_enmItem)iItem)
            {   /* One case per item: its own function. */
                case Isv_enmItem.Save:
                    /* The name in the box, as Save takes it. */
                    Isv_AcceptName();
                    break;
                case Isv_enmItem.Activate:
                    /* The activated row's entry entered or chosen. */
                    Isv_ActivateEntry((EEN_stEntry)objArgs);
                    break;
                case Isv_enmItem.Up:
                    /* The parent folder. */
                    Isv_GoUp();
                    break;
                case Isv_enmItem.NewFolder:
                    /* The worker asked to make a folder. */
                    Isv_RequestNewFolder();
                    break;
                default:
                    /* No such item (a guard: only the handlers post):   */
                    /* nothing done.                                     */
                    break;
            }
        }

        /*--------------------------------------------------------------------*/
        /* Isv_ActivateEntry:                                                 */
        /*                                                                    */
        /* A row activated, run by the dialog's queue: a directory is         */
        /* entered -- its node in the tree is selected, which shows it -- and */
        /* a file is chosen through the same checks Save makes.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stEntry : the row's entry, as it was when it was activated.    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the directory is being listed, or the save is answered. */
        /*--------------------------------------------------------------------*/
        private void Isv_ActivateEntry(EEN_stEntry stEntry)
        {
            if (stEntry.EEN_uiType == ENG_clsNative.ENG_TYPE_DIR)
            {   /* A directory: entered, through its node in the tree. */
                Isv_EnterChild(ENG_clsNative.ENG_EntryName(stEntry));
                return;
            }

            /* A file: its name is normally in the box already              */
            /* (Isv_OnEntrySelected), but the box can have been edited      */
            /* since the row was selected, so the activated row's name is   */
            /* put back before Save's checks (Isv_AcceptName): the file     */
            /* activated is the one chosen.  Nothing is replaced here --    */
            /* the worker asks before it replaces anything, when the        */
            /* editor saves.                                                */
            Isv_txtName.Text = ENG_clsNative.ENG_EntryName(stEntry);
            Isv_AcceptName();
        }

        /*--------------------------------------------------------------------*/
        /* Isv_EnterChild:                                                    */
        /*                                                                    */
        /* Show a subdirectory of the directory shown: select its node in the */
        /* tree, which lists it.  The node normally exists, because the       */
        /* directory shown has been listed and its children made from that    */
        /* listing.  It can be missing when the entry list and the tree came  */
        /* from different listings of the folder (a later tree listing found  */
        /* it gone); nothing then changes -- no other folder is guessed at.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sName : the subdirectory's name.                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the subdirectory is being listed, or nothing changed.   */
        /*--------------------------------------------------------------------*/
        private void Isv_EnterChild(string sName)
        {
            TreeNode nodHere;
            int      i;

            /* The directory shown is the tree's selected node. */
            nodHere = Isv_treFolders.SelectedNode;

            if (nodHere == null)
            {   /* No directory shown. */
                return;
            }

            for (i = 0; i < nodHere.Nodes.Count; i++)
            {   /* One child per iteration, its name compared exactly: every   */
                /* caller passes the folder's name as listed, which is the     */
                /* node's own text, on FAT as on ext.  The Tag test passes     */
                /* over the placeholder, whose text "(reading ...)" is also    */
                /* a name a folder could have.                                 */
                if (nodHere.Nodes[i].Tag != null && nodHere.Nodes[i].Text == sName)
                {   /* The subdirectory's node: selecting it shows it and lists   */
                    /* it (Isv_OnAfterSelect); it is scrolled into view, and      */
                    /* the search ends.                                           */
                    Isv_treFolders.SelectedNode = nodHere.Nodes[i];
                    nodHere.Nodes[i].EnsureVisible();
                    return;
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* Isv_OnUpClicked:                                                   */
        /*                                                                    */
        /* Up: posted to the dialog's own queue, which shows the parent       */
        /* (Isv_GoUp).  Nothing else is done here.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Up posted.                                              */
        /*--------------------------------------------------------------------*/
        private void Isv_OnUpClicked(object objSender, EventArgs evArgs)
        {
            /* Posted, with nothing to carry. */
            Isv_objQueue.WQU_Post((int)Isv_enmItem.Up, null);
        }

        /*--------------------------------------------------------------------*/
        /* Isv_GoUp:                                                          */
        /*                                                                    */
        /* Up, run by the dialog's queue: show the parent of the directory    */
        /* shown, by selecting its node.  At the root there is no parent and  */
        /* the button is disabled.                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the parent is being listed, or nothing at the root.     */
        /*--------------------------------------------------------------------*/
        private void Isv_GoUp()
        {
            if (Isv_treFolders.SelectedNode != null && Isv_treFolders.SelectedNode.Parent != null)
            {   /* There is a parent to go to: selecting its node shows it and   */
                /* lists it again (Isv_OnAfterSelect), so the entry list is      */
                /* fresh.  The test repeats the button's disabling at the        */
                /* root, for a click that arrives before that has happened.      */
                Isv_treFolders.SelectedNode = Isv_treFolders.SelectedNode.Parent;
            }
        }

        /*------------------------------------------------------------------*/
        /* Isv_OnNewFolderClicked:                                          */
        /*                                                                  */
        /* New Folder: posted to the dialog's own queue, which asks the     */
        /* worker for it (Isv_RequestNewFolder).  Nothing else is done      */
        /* here.                                                            */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objSender : the button (unused).                             */
        /*     evArgs    : unused.                                          */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : New Folder posted.                                    */
        /*------------------------------------------------------------------*/
        private void Isv_OnNewFolderClicked(object objSender, EventArgs evArgs)
        {
            /* Posted, with nothing to carry. */
            Isv_objQueue.WQU_Post((int)Isv_enmItem.NewFolder, null);
        }

        /*------------------------------------------------------------------*/
        /* Isv_RequestNewFolder:                                            */
        /*                                                                  */
        /* New Folder, run by the dialog's queue: post one request          */
        /* (EditorMakeFolder, CommandPickerFolder.cs) that does all of it   */
        /* on the worker -- the name asked for and checked, the filesystem  */
        /* made writable if it is not yet, the folder made, then it and the */
        /* folder it went in listed back -- in the folder selected now.     */
        /* Nothing is asked or written here.                                */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     None.                                                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the request posted, or nothing done; the answer comes */
        /*            to ISV_ApplyFolderResult.                             */
        /*------------------------------------------------------------------*/
        private void Isv_RequestNewFolder()
        {
            TreeNode nodParent;
            RQT_clsRequest objRequest;
            SFC_clsFolderCreate objCreate;

            /* The folder to make it in: the one selected in the tree. */
            nodParent = Isv_treFolders.SelectedNode;
            if (Isv_objEditor == null || nodParent == null || nodParent.Tag == null)
            {   /* No editor record (the request is the editor's, and binds   */
                /* to its destination -- the button is disabled without       */
                /* one), or no folder selected yet: nothing to post.          */
                return;
            }

            /* The user's own act, so the start-up branch's answer is          */
            /* dropped.  The record keeps the folder and the selection count   */
            /* for the answer; the request carries only paths and facts:       */
            /* from the editor (its id), bound to the dialog's filesystem,     */
            /* in that folder, with the dialog's id (which owns the worker's   */
            /* questions) and the editor's destination and save order as       */
            /* they stand -- the worker refuses if either has changed by       */
            /* the time it runs (PFA_clsPickerFolderArgs).                     */
            Isv_bStartupSuperseded = true;
            objCreate = new SFC_clsFolderCreate(this, nodParent, Isv_lNavigation);
            objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.EditorMakeFolder,
                Isv_objEditor.EDR_lId, Isv_objSource, Isv_objSource.STV_objTarget,
                new string[] { (string)nodParent.Tag }, 0, 0,
                new PFA_clsPickerFolderArgs(Isv_lWindow, Isv_objEditor.EDR_sEntry,
                    Isv_objEditor.EDR_lSaveOrder), "/", "");
            Isv_objClient.CLI_Post(objRequest, objCreate.SFC_OnResult);
        }

        /*------------------------------------------------------------------*/
        /* ISV_ApplyFolderResult:                                           */
        /*                                                                  */
        /* A New Folder's answer (SFC_OnResult): say what happened, and --  */
        /* if the folder was made and the user is still where they clicked  */
        /* -- merge the parent's listing, enter the new folder and show its */
        /* (empty) entries, all from the answer's own listings.  A folder   */
        /* made but not listed is still said to be made: it exists, and a   */
        /* second try would be refused for the name.                        */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objCreate : the request's record (the folder clicked in, the */
        /*                 selection count then).                           */
        /*     objResult : the outcome; MFV_clsMadeFolderView whenever the  */
        /*                 folder was made, with the listings that          */
        /*                 finished.                                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the outcome said; the new folder entered when the     */
        /*            view had not moved; nothing posted.                   */
        /*------------------------------------------------------------------*/
        internal void ISV_ApplyFolderResult(SFC_clsFolderCreate objCreate, RSL_clsResult objResult)
        {
            MFV_clsMadeFolderView objMade;
            FVW_clsFolderView objFolder;
            ELR_clsListResult objRows;
            TreeNode nodCurrent;
            TreeNode nodMatch;
            TreeNode nodChild;
            string sFailure;
            int i;
            int j;

            if (Isv_bClosed || IsDisposed)
            {   /* The dialog has closed: nothing to show it in.  A folder   */
                /* made stays made.                                          */
                return;
            }

            /* The answer's own words on the status line first -- the       */
            /* worker's sequence says "made, but not listed" itself when    */
            /* only the listings failed -- then the made folder's record,   */
            /* present whenever the folder was made.                        */
            objMade = objResult.RSL_objPayload as MFV_clsMadeFolderView;
            Isv_lblStatus.Text = objResult.RSL_sDetail;
            if (objMade == null || objCreate.SFC_nodParent.TreeView != Isv_treFolders ||
                Isv_lNavigation != objCreate.SFC_lNavigation ||
                Isv_treFolders.SelectedNode != objCreate.SFC_nodParent)
            {   /* Not made (the words above say why), or the user has moved   */
                /* on -- the folder clicked in has left the tree, or another   */
                /* has been selected since: the view stays where the user      */
                /* put it.                                                     */
                return;
            }

            /* The listings painted with the flag up, as the start-up        */
            /* branch's are (Isv_OnBranchListed), so the selections below    */
            /* post nothing: the answer already holds the parent's listing   */
            /* (folders only) and the new folder's.                          */
            Isv_bRenderingBranch = true;
            try
            {   /* Released in the finally, however the walk ends. */
                nodCurrent = objCreate.SFC_nodParent;
                for (i = 0; i < objMade.MFV_objBranch.BRV_lstFolders.Count; i++)
                {   /* One listing per iteration: the parent's, then the new   */
                    /* folder's, each on the node the walk has reached.        */
                    objFolder = objMade.MFV_objBranch.BRV_lstFolders[i];
                    if ((string)nodCurrent.Tag != objFolder.FVW_sFolder ||
                        objFolder.FVW_lEpoch != Isv_objSource.STV_lEpoch)
                    {   /* Of another path, or another opening of the image: not   */
                        /* this node's.  Said; the folder is still made.           */
                        Isv_lblStatus.Text = "The folder was created, but its readback did not match this picker.";
                        return;
                    }

                    /* The node selected and shown, its old rows gone, then its   */
                    /* listing taken -- as in the start-up branch, so even a      */
                    /* failed listing leaves no stale rows Save could use.        */
                    Isv_treFolders.SelectedNode = nodCurrent;
                    Isv_SetDirectory(nodCurrent);
                    if (!Isv_ReadRows(objFolder.FVW_objResult, objFolder.FVW_sFolder,
                        out objRows, out sFailure))
                    {   /* Not listed.  The answer's own words, which say the     */
                        /* folder was made, are put back over the "Reading ..."   */
                        /* Isv_SetDirectory wrote; or, if it gave none, this      */
                        /* listing's failure after the folder's name.             */
                        Isv_lblStatus.Text = objResult.RSL_sDetail;
                        if (String.IsNullOrEmpty(objResult.RSL_sDetail))
                        {   /* No words given: made, and why it was not listed. */
                            Isv_lblStatus.Text = "Created " + objMade.MFV_sCreated +
                                ", but its picker readback did not finish: " + sFailure;
                        }

                        /* The folder stays made; nothing is tried again. */
                        return;
                    }

                    /* Its subfolders into the tree, merged (Isv_MergeNode). */
                    Isv_MergeNode(nodCurrent, objRows);
                    if (objFolder.FVW_sFolder == objMade.MFV_sCreated)
                    {   /* The new folder's own listing: its entries shown, Save   */
                        /* usable in it, the answer's words on the status line     */
                        /* (the reading line replaced), the node in view.          */
                        Isv_FillEntries(objRows.ELR_lstEntries);
                        Isv_sListedDir = Isv_sDir;
                        Isv_lblStatus.Text = objResult.RSL_sDetail;
                        nodCurrent.EnsureVisible();
                        return;
                    }

                    /* The parent's listing done: the new folder's node, found    */
                    /* by its full path among the children the listing gave --    */
                    /* exactly that path, so a folder differing only in case is   */
                    /* not taken for it.                                          */
                    nodMatch = null;
                    for (j = 0; j < nodCurrent.Nodes.Count; j++)
                    {   /* One child per iteration (the placeholder, no Tag, is   */
                        /* passed over).                                          */
                        nodChild = nodCurrent.Nodes[j];
                        if (nodChild.Tag != null && (string)nodChild.Tag == objMade.MFV_sCreated)
                        {   /* The new folder's node. */
                            nodMatch = nodChild;
                            break;
                        }
                    }

                    if (nodMatch == null)
                    {   /* Made, but not among the children the parent's listing   */
                        /* gave (how that can happen is not established -- a       */
                        /* removal in between is one way): said, with the          */
                        /* folder's name.                                          */
                        Isv_lblStatus.Text = "Created " + objMade.MFV_sCreated +
                            ", but it was absent from the directory readback.";
                        return;
                    }

                    /* On into it: its listing is the next in the answer. */
                    nodCurrent = nodMatch;
                }
            }
            finally
            {   /* The flag down: the tree's own events post listings again. */
                Isv_bRenderingBranch = false;
            }
        }

        /*--------------------------------------------------------------------*/
        /* Isv_NameProblem:                                                   */
        /*                                                                    */
        /* What is wrong with a typed name as the name of an entry in the     */
        /* image: empty, a slash in it (a separator, never part of a name),   */
        /* the two names every directory already has, or longer than the      */
        /* filesystem allows -- 255 bytes of UTF-8 on ext2/3/4, 255 UTF-16    */
        /* units on FAT and exFAT, as Windows allows on a FAT volume.         */
        /* Windows' own character rules do not apply here: a colon or a       */
        /* trailing dot is a fine Linux name, and on a FAT image the caller   */
        /* holds the name to FAT's rules as well (Isv_SessionNameProblem).    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sName : the name, already trimmed.                             */
        /*     bFat  : true when the image's filesystem is FAT or exFAT, so   */
        /*             the length is FAT's rather than ext's.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the problem in words, or "" when the name will do.    */
        /*--------------------------------------------------------------------*/
        private static string Isv_NameProblem(string sName, bool bFat)
        {
            if (sName.Length == 0)
            {   /* Nothing typed (or only spaces, which the caller trimmed). */
                return("Type a name.");
            }

            if (sName.IndexOf('/') >= 0)
            {   /* A slash cannot be part of a name.  A guard: a typed path   */
                /* is split at its slashes before any part of it comes here   */
                /* (Isv_ResolveTypedPath).                                    */
                return("A name cannot contain a slash.");
            }

            if (sName == "." || sName == "..")
            {   /* The two names every directory holds already, which mean   */
                /* the directory itself and its parent: never an entry.  A   */
                /* guard as well: a typed "." or ".." is a step, taken       */
                /* before this (Isv_ResolveTypedPath).                       */
                return("\"" + sName + "\" is not a name a file can have.");
            }

            /* On ext2/3/4, counted in bytes, not characters: ext stores a   */
            /* name as at most 255 bytes, and the name goes into the image   */
            /* in UTF-8, where a character outside ASCII takes two to four   */
            /* bytes -- so a count of characters would pass a name of        */
            /* multi-byte characters that does not fit.                      */
            if (!bFat && Encoding.UTF8.GetByteCount(sName) > ISV_NAME_MAX_BYTES)
            {   /* Too long for ext. */
                return("The name is too long: an ext filesystem holds at most " + ISV_NAME_MAX_BYTES + " bytes of a name.");
            }

            /* On FAT and exFAT, counted in UTF-16 units -- the string's      */
            /* own length -- which is how a long name is stored and how       */
            /* Windows limits one on a FAT volume.  ext's byte limit does     */
            /* not bind here: a name of 255 characters outside ASCII (up to   */
            /* 765 bytes of UTF-8) is one FAT holds, and the engine lists     */
            /* and makes it whole.                                            */
            if (bFat && sName.Length > ISV_FAT_NAME_MAX)
            {   /* Too long for FAT. */
                return("The name is too long: a FAT filesystem holds at most " + ISV_FAT_NAME_MAX + " characters of a name.");
            }

            /* Anything else will do here -- a Windows device name or a      */
            /* trailing dot included, both fine on Linux.  The save's own    */
            /* checks come later, when the editor saves, after this dialog   */
            /* has returned.                                                 */
            return("");
        }

        /*--------------------------------------------------------------------*/
        /* Isv_OnSaveClicked:                                                 */
        /*                                                                    */
        /* The Save button (and Enter, Save being the dialog's Accept         */
        /* button): posted to the dialog's own queue, which checks and takes  */
        /* the typed name (Isv_AcceptName).  Nothing else is done here.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Save posted (a second press while one waits or runs is  */
        /*            dropped by the queue).                                  */
        /*--------------------------------------------------------------------*/
        private void Isv_OnSaveClicked(object objSender, EventArgs evArgs)
        {
            /* Posted, with nothing to carry: the box is read when it runs. */
            Isv_objQueue.WQU_Post((int)Isv_enmItem.Save, null);
        }

        /*--------------------------------------------------------------------*/
        /* Isv_AcceptName:                                                    */
        /*                                                                    */
        /* Save (or Enter, or a double-click on a file): decide what the      */
        /* typed name means in the directory shown, as the Windows dialog     */
        /* does -- enter a directory of that name, refuse a link or a special */
        /* node, or choose the name (a new one, or a regular file's).  A      */
        /* Windows drive path typed instead (a drive letter and colon, alone  */
        /* or followed by a path) is a request to save to Windows there, as   */
        /* typing one in the Windows dialog's name box changes drive; a path  */
        /* inside the image ("/etc/x.txt", "../x.txt", "sub/") moves the      */
        /* picker to its folder first (Isv_ResolveTypedPath).  Nothing is     */
        /* replaced here: the worker looks at the target again when the       */
        /* editor saves, and asks before replacing it.  The dialog's queue    */
        /* runs it for Save (Isv_RunItem) and for a file row's activation     */
        /* (Isv_ActivateEntry).                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the outcome fields are set and the dialog closes (a     */
        /*            name chosen, or a Windows place asked for), or a        */
        /*            directory is being entered, or a message says what to   */
        /*            correct and the dialog stays open.                      */
        /*--------------------------------------------------------------------*/
        private void Isv_AcceptName()
        {
            EEN_stEntry stEntry;
            string    sName;
            string    sTypedName;
            string    sWhy;
            int       iPath;
            int       i;

            if (Isv_lstEntries.Focused && Isv_lstEntries.SelectedItems.Count == 1)
            {   /* Enter on a row of the list: Save is the Accept button, so   */
                /* Enter presses it wherever the focus is, and in the list     */
                /* it must act on the row rather than on the typed name.       */
                stEntry = Isv_lstShown[(int)Isv_lstEntries.SelectedItems[0].Tag];

                if (stEntry.EEN_uiType == ENG_clsNative.ENG_TYPE_DIR)
                {   /* A directory: entered, as Enter does in the Windows   */
                    /* dialog, and nothing is saved.                        */
                    Isv_EnterChild(ENG_clsNative.ENG_EntryName(stEntry));
                    return;
                }

                /* A file's row falls through: selecting it put its name   */
                /* in the box (Isv_OnEntrySelected), so the checks below   */
                /* apply to it.                                            */
            }

            /* The typed name, trimmed of spaces at either end (so a name    */
            /* cannot begin or end with one here, though Linux allows it).   */
            sName = Isv_txtName.Text.Trim();

            if (Isv_TakeWindowsRequest(sName))
            {   /* A Windows drive path typed in the name box: a request to    */
                /* change drive, as the Windows dialog takes one -- the save   */
                /* goes to Windows, at that place, instead.  The outcome is    */
                /* set (Isv_TakeWindowsRequest) and the dialog closes; the     */
                /* caller shows the Windows dialog there.  Nothing is chosen   */
                /* in the image.                                               */
                this.Close();
                return;
            }

            /* A typed PATH -- "/etc/x.txt", "../x.txt", "sub/", ".." -- is    */
            /* resolved here, in the picker, before anything is posted         */
            /* (Isv_ResolveTypedPath): one that ends in another folder moves   */
            /* the picker there, and its name, if it has one, is taken once    */
            /* that folder's listing is shown; one that is refused has said    */
            /* why.  One that ends in the folder shown hands its name back,    */
            /* and a plain name comes back as it was, both judged below.       */
            iPath = Isv_ResolveTypedPath(sName, out sTypedName);
            if (iPath == ISV_PATH_DONE)
            {   /* Moving, or refused: nothing more here. */
                return;
            }

            /* The name, checked by the session's rules (Isv_SessionNameProblem). */
            sName = sTypedName;
            sWhy  = Isv_SessionNameProblem(sName);

            if (sWhy.Length != 0)
            {   /* Not a name the image can hold: said, and the focus put   */
                /* back in the box for the user to correct it.              */
                MSG_clsMessageForm.MSG_Show(this, this.Font, sWhy, "Save As", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
                Isv_txtName.Focus();
                return;
            }

            if (!string.Equals(Isv_sListedDir, Isv_sDir, StringComparison.Ordinal))
            {   /* The folder shown has no listing of its own in the list     */
                /* (still reading, or the listing failed): the checks below   */
                /* need its entries, and another folder's would be wrong.     */
                /* Said, with what to do; the dialog stays open.              */
                MSG_clsMessageForm.MSG_Show(this, this.Font, "The contents of " + Isv_sDir + " have not been read yet.\n" +
                                         "Choose the folder again, and save once its contents are shown.",
                                         "Save As", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
                return;
            }

            if (Isv_bFat)
            {   /* FAT or exFAT: the name as the filesystem will hold it --   */
                /* its trailing spaces and dots dropped, as the engine and    */
                /* Windows drop them -- so it is matched below, and chosen,   */
                /* in the one spelling the entry will really have.  A name    */
                /* of spaces and dots alone never gets here: FAT's rules      */
                /* refused it above.                                          */
                sName = sName.TrimEnd(' ', '.');
            }

            /* Is the name taken?  It is looked for among the entries the     */
            /* list shows -- the chosen folder's own listing, as tested       */
            /* above -- by the filesystem's own way of matching names         */
            /* (Isv_SameName): exactly on ext, letter case aside on FAT.      */
            /* The listing may be out of date by the time the editor saves;   */
            /* the worker looks again then.                                   */
            for (i = 0; i < Isv_lstShown.Count; i++)
            {   /* One entry of the directory shown per iteration: is the name taken? */
                if (!Isv_SameName(ENG_clsNative.ENG_EntryName(Isv_lstShown[i]), sName))
                {   /* Not this one. */
                    continue;
                }

                /* Taken: what kind of entry holds the name decides. */
                stEntry = Isv_lstShown[i];

                if (stEntry.EEN_uiType == ENG_clsNative.ENG_TYPE_DIR)
                {   /* A directory of that name: entered rather than saved      */
                    /* over, as the Windows dialog does; the user then types    */
                    /* or picks a name inside it.  Entered by the directory's   */
                    /* own name, which the tree's nodes carry -- on FAT the     */
                    /* typed one can differ from it in letter case.             */
                    Isv_EnterChild(ENG_clsNative.ENG_EntryName(stEntry));
                    return;
                }

                if (stEntry.EEN_uiType != ENG_clsNative.ENG_TYPE_FILE)
                {   /* A link or a special node: refused, and the user chooses   */
                    /* another name, because this dialog offers to replace       */
                    /* regular files only.  Why other kinds are not offered      */
                    /* (rather than, say, a link being followed) is not          */
                    /* recorded.                                                 */
                    MSG_clsMessageForm.MSG_Show(this, this.Font, sName + " is not a regular file and cannot be saved over. Choose another name.",
                                             "Save As", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
                    Isv_txtName.Focus();
                    return;
                }

                /* A regular file: the name is chosen, with no question       */
                /* here.  Replacing it is asked by the worker when the        */
                /* editor saves, after looking at the target again -- some    */
                /* other request may have changed the folder since this       */
                /* listing.  The file's own spelling is taken: the same       */
                /* text on ext; on FAT the entry as it is named, so the       */
                /* save is asked for under the name the file already has      */
                /* and the editor then names the file that is really there.   */
                sName = ENG_clsNative.ENG_EntryName(stEntry);
                break;
            }

            /* The outcome set BEFORE the close, since the caller reads it   */
            /* once ShowDialog returns (the form's DialogResult is not       */
            /* used).  Choosing grants nothing: no write, no consent to      */
            /* replace.                                                      */
            ISV_sName     = sName;
            ISV_sPath     = Isv_JoinPath(Isv_sDir, sName);
            ISV_bAccepted = true;
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Isv_SameName:                                                      */
        /*                                                                    */
        /* Whether a listed entry's name and a typed name name the same entry */
        /* in this dialog's filesystem.  On ext2/3/4 only the same text does: */
        /* its names are case-sensitive, so "a.txt" and "A.txt" are two.  On  */
        /* FAT and exFAT letter case is set aside, as FatFs matches a long    */
        /* name through its own upper-casing (ff.c, cmp_lfn) -- so a typed    */
        /* "readme.txt" is the "README.TXT" already there, and saving under   */
        /* it would replace that file, not make a second.  The framework's    */
        /* ordinal case folding is used, as the worker's own FAT path match   */
        /* is (Sys_FatPathKey, CommandOperations.cs); that it folds every     */
        /* character exactly as FatFs's table does is not established.  The   */
        /* caller has already dropped the typed name's trailing spaces and    */
        /* dots on FAT, and a listed FAT name never has them.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sListed : the entry's name as listed.                          */
        /*     sTyped  : the name typed, as the caller has made it.           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the two name one entry.                       */
        /*--------------------------------------------------------------------*/
        private bool Isv_SameName(string sListed, string sTyped)
        {
            if (Isv_bFat)
            {   /* FAT or exFAT: letter case aside. */
                return(String.Equals(sListed, sTyped, StringComparison.OrdinalIgnoreCase));
            }

            /* ext2/3/4: exactly. */
            return(String.Equals(sListed, sTyped, StringComparison.Ordinal));
        }

        /*--------------------------------------------------------------------*/
        /* Isv_SessionNameProblem:                                            */
        /*                                                                    */
        /* What is wrong with one name in this dialog's filesystem: the rules */
        /* every image name is held to (Isv_NameProblem, its length measured  */
        /* as this filesystem measures it), and on a FAT or exFAT image FAT's */
        /* own as well -- the characters it forbids, a name of spaces and     */
        /* dots alone -- as every image name box holds them there             */
        /* (SYS_FatNameProblem, CommandOperations.cs).  A typed path's every  */
        /* part, and a plain name, are judged by it.                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sName : one name, not a path.                                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the problem in words, or "" when the name will do.    */
        /*--------------------------------------------------------------------*/
        private string Isv_SessionNameProblem(string sName)
        {
            string sWhy;

            /* The rules every filesystem shares first, the length by this   */
            /* one's own measure (bytes on ext, characters on FAT).          */
            sWhy = Isv_NameProblem(sName, Isv_bFat);

            if (sWhy.Length == 0 && Isv_bFat)
            {   /* An ext name, on a FAT or exFAT image: FAT's rules too. */
                sWhy = SYS_clsSystem.SYS_FatNameProblem(sName);
            }

            /* The first problem found, or "". */
            return(sWhy);
        }

        /*--------------------------------------------------------------------*/
        /* Isv_ResolveTypedPath:                                              */
        /*                                                                    */
        /* Whether the name box holds a PATH inside the image rather than a   */
        /* name, and if so what it names -- resolved here, in the picker, so  */
        /* only whole, plain paths reach the worker and the engine (which     */
        /* refuses "." and ".." in a path).  The text is a path when it holds */
        /* a separator -- '/' on ext2/3/4, where '\' is an ordinary name      */
        /* character; '/' or '\' on FAT and exFAT, which take both -- or is   */
        /* "." or ".." alone.  It starts from the image's root when it begins */
        /* with a separator, otherwise from the folder shown.  Its parts are  */
        /* taken in order: an empty one ("//") and "." are passed over, ".."  */
        /* goes up one folder and never above the root, and any other is a    */
        /* folder to go into, held to the session's name rules.  The last     */
        /* part is the file's name, unless the path ends in a separator, "."  */
        /* or "..": then it names a folder only, and the box gets back the    */
        /* name it held (Isv_sKeptName).  A folder other than the one shown   */
        /* is moved to by one branch listing (Isv_LoadBranch), whose answer   */
        /* says if a folder is not there, and a file name waits until the     */
        /* folder is shown (Isv_RunPendingName).                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sTyped : the name box's text, trimmed; not a drive path (the   */
        /*              caller takes those first).                            */
        /*     sName  : receives the name to judge: the text itself for       */
        /*              ISV_PATH_NONE, the path's file name for               */
        /*              ISV_PATH_HERE, "" for ISV_PATH_DONE.                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : ISV_PATH_NONE (a name), ISV_PATH_HERE (a path ending in  */
        /*           the folder shown, with a name) or ISV_PATH_DONE (the     */
        /*           picker moving, or the path refused with a message).      */
        /*--------------------------------------------------------------------*/
        private int Isv_ResolveTypedPath(string sTyped, out string sName)
        {
            char[]       achSeparators;
            string[]     arrParts;
            List<string> lstFolders;
            string       sFileName;
            string       sWhy;
            string       sTarget;
            int          iSteps;
            int          i;

            /* Not a path until it is found to be one: the text is the name. */
            sName = sTyped;

            /* The separators: '/' always, and '\' too on FAT, where FatFs    */
            /* reads it as one (and FAT forbids it in a name); on ext it is   */
            /* a name character like any other.                               */
            achSeparators = new char[] { '/' };
            if (Isv_bFat)
            {   /* FAT or exFAT: both. */
                achSeparators = new char[] { '/', '\\' };
            }

            if (sTyped.IndexOfAny(achSeparators) < 0 && sTyped != "." && sTyped != "..")
            {   /* No separator, and not one of the two steps: a name, judged   */
                /* as one by the caller.                                        */
                return(ISV_PATH_NONE);
            }

            /* A path.  Its parts, the empty ones kept for now (they mark a    */
            /* leading or trailing separator), and the folder it starts in:    */
            /* the root for a leading separator, else the folder shown, as a   */
            /* list of its names.                                              */
            arrParts   = sTyped.Split(achSeparators);
            lstFolders = new List<string>();
            if (sTyped.IndexOfAny(achSeparators) != 0)
            {   /* Relative: from the folder shown. */
                lstFolders.AddRange(Isv_sDir.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries));
            }

            /* The last part is the file's name when it is a real one; a path   */
            /* ending in a separator, "." or ".." names a folder only, and      */
            /* every part of it is a step.                                      */
            sFileName = arrParts[arrParts.Length - 1];
            iSteps    = arrParts.Length - 1;
            if (sFileName.Length == 0 || sFileName == "." || sFileName == "..")
            {   /* A folder only: no file name, and the last part a step too. */
                sFileName = "";
                iSteps    = arrParts.Length;
            }

            for (i = 0; i < iSteps; i++)
            {   /* One part of the folder's path per iteration, in order. */
                if (arrParts[i].Length == 0 || arrParts[i] == ".")
                {   /* "//" or ".": this folder, nothing to do. */
                    continue;
                }

                if (arrParts[i] == "..")
                {   /* Up one folder.  At the root it stays there, as a ".."   */
                    /* at a root does in Windows and Linux alike.              */
                    if (lstFolders.Count != 0)
                    {   /* Below the root: the last folder dropped. */
                        lstFolders.RemoveAt(lstFolders.Count - 1);
                    }

                    continue;
                }

                /* A folder to go into, held to the session's name rules, so   */
                /* a part no folder could be named is refused here, by name,   */
                /* rather than looked for.                                     */
                sWhy = Isv_SessionNameProblem(arrParts[i]);
                if (sWhy.Length != 0)
                {   /* Not a folder name: said, the box left for correcting. */
                    MSG_clsMessageForm.MSG_Show(this, this.Font, "\"" + arrParts[i] + "\": " + sWhy, "Save As",
                        MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
                    Isv_txtName.Focus();
                    sName = "";
                    return(ISV_PATH_DONE);
                }

                if (Isv_bFat)
                {   /* On FAT its trailing spaces and dots are dropped, as the   */
                    /* engine drops them, so "sub." is the folder "sub".         */
                    lstFolders.Add(arrParts[i].TrimEnd(' ', '.'));
                }
                else
                {   /* On ext the name as typed. */
                    lstFolders.Add(arrParts[i]);
                }
            }

            if (sFileName.Length != 0)
            {   /* The file's name, held to the same rules now, before the      */
                /* picker moves for it: a move is wasted on a name that would   */
                /* then be refused.                                             */
                sWhy = Isv_SessionNameProblem(sFileName);
                if (sWhy.Length != 0)
                {   /* Not a name the image can hold: said, as for a folder. */
                    MSG_clsMessageForm.MSG_Show(this, this.Font, sWhy, "Save As",
                        MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
                    Isv_txtName.Focus();
                    sName = "";
                    return(ISV_PATH_DONE);
                }
            }

            /* The folder the path names, as an image path. */
            sTarget = "/" + string.Join("/", lstFolders.ToArray());

            if (string.Equals(sTarget, Isv_sDir, StringComparison.Ordinal))
            {   /* The folder shown: nothing to move. */
                if (sFileName.Length != 0)
                {   /* With a name: into the box, and judged as a name, here,   */
                    /* by the caller.                                           */
                    Isv_txtName.Text = sFileName;
                    sName = sFileName;
                    return(ISV_PATH_HERE);
                }

                /* A folder only, and it is this one: the box gets its name   */
                /* back, selected for typing over.                            */
                Isv_txtName.Text = Isv_sKeptName;
                Isv_txtName.SelectAll();
                Isv_txtName.Focus();
                sName = "";
                return(ISV_PATH_DONE);
            }

            /* Another folder: moved to.  The box shows the file's name, or      */
            /* the kept name for a folder alone; a file name waits for the       */
            /* folder's listing (Isv_OnBranchListed posts Isv_RunPendingName).   */
            Isv_sPendingName = null;
            if (sFileName.Length != 0)
            {   /* A file name: shown, and waiting. */
                Isv_txtName.Text = sFileName;
                Isv_sPendingName = sFileName;
            }
            else
            {   /* A folder alone: the kept name. */
                Isv_txtName.Text = Isv_sKeptName;
            }

            /* The target shown at once as the folder being read, the old       */
            /* folder's rows gone and nothing counted as listed -- so a Save    */
            /* pressed meanwhile is refused rather than taken in the old        */
            /* folder -- as Isv_SetDirectory does for a folder with a node;     */
            /* the branch's answer selects the real nodes as it goes.  The      */
            /* move counts as the user's own: a start-up branch still to come   */
            /* is replaced (only the newest is awaited), and a New Folder       */
            /* answered later does not move the view back.                      */
            Isv_arrStart           = lstFolders.ToArray();
            Isv_bStartupSuperseded = false;
            Isv_lNavigation        = checked(Isv_lNavigation + 1);
            Isv_sDir               = sTarget;
            Isv_sListedDir         = null;
            Isv_objListRequest     = null;
            Isv_txtWhere.Text      = sTarget;
            Isv_lstShown.Clear();
            Isv_lstEntries.BeginUpdate();
            Isv_lstEntries.Items.Clear();
            Isv_lstEntries.EndUpdate();
            Isv_FitColumns();
            Isv_lblStatus.Text = "Reading " + sTarget + " ...";

            /* One branch listing, root to the folder (Isv_LoadBranch), kept   */
            /* as the one whose answer may take the waiting name.              */
            Isv_LoadBranch();
            Isv_objPendingBranch = Isv_objBranch;
            sName = "";
            return(ISV_PATH_DONE);
        }

        /*--------------------------------------------------------------------*/
        /* Isv_RunPendingName:                                                */
        /*                                                                    */
        /* A typed path's file name, taken once the picker has moved to its   */
        /* folder and shown that folder's own listing: put in the box and     */
        /* judged as Save judges any name (Isv_AcceptName) -- chosen, a       */
        /* folder of that name entered, or refused with its reason.  Posted   */
        /* by the branch's answer (Isv_OnBranchListed), so it runs once that  */
        /* painting is over.  Dropped when the dialog has closed or the user  */
        /* has chosen another folder since.                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the name judged, or dropped (it stays in the box).      */
        /*--------------------------------------------------------------------*/
        private void Isv_RunPendingName()
        {
            string sName;

            /* Taken off first, so it is judged at most once. */
            sName                = Isv_sPendingName;
            Isv_sPendingName     = null;
            Isv_objPendingBranch = null;

            if (sName == null || Isv_bClosed || IsDisposed ||
                Isv_lNavigation != Isv_lPendingNavigation ||
                !string.Equals(Isv_sListedDir, Isv_sDir, StringComparison.Ordinal))
            {   /* Nothing waiting, the dialog gone, or the user has moved     */
                /* on (another folder chosen, or this one no longer listed):   */
                /* nothing judged; the name stays in the box for the user.     */
                return;
            }

            /* The name in the box, then Save's own judgement of it. */
            Isv_txtName.Text = sName;
            Isv_AcceptName();
        }

        /*--------------------------------------------------------------------*/
        /* Isv_TakeWindowsRequest:                                            */
        /*                                                                    */
        /* Whether a typed name is a Windows drive path -- a letter, a colon, */
        /* and then nothing or a separator of either kind and a path -- and,  */
        /* when it is, the outcome that asks the caller to save to Windows    */
        /* there: the folder and the file name, split at the last             */
        /* separator.  A path ending in a separator, or the drive alone,      */
        /* names a folder, and the name offered by the dialog is kept for     */
        /* the file.  A colon anywhere else ("a:b") is not a drive change: it */
        /* stays a name, an ordinary one on ext2/3/4 and one FAT's rules      */
        /* refuse (Isv_AcceptName).  The path is not checked here: the caller */
        /* hands it to the Windows dialog, and what that dialog does with a   */
        /* folder that does not exist is its own (not established here).      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sTyped : the name box's text, trimmed.                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true, with ISV_bWindowsAsked, ISV_sWindowsFolder and    */
        /*            ISV_sWindowsName set, for a drive path; false, with     */
        /*            nothing set, for anything else.                         */
        /*--------------------------------------------------------------------*/
        private bool Isv_TakeWindowsRequest(string sTyped)
        {
            string sPath;
            int    iSplit;

            if (sTyped.Length < 2 || sTyped[1] != ':' ||
                !((sTyped[0] >= 'A' && sTyped[0] <= 'Z') || (sTyped[0] >= 'a' && sTyped[0] <= 'z')))
            {   /* Not a letter and a colon: no drive named. */
                return(false);
            }

            if (sTyped.Length > 2 && sTyped[2] != '\\' && sTyped[2] != '/')
            {   /* "D:name": a drive-relative name, which Windows resolves   */
                /* against that drive's own current folder -- a place the    */
                /* user cannot see -- so it is not taken as a request, and   */
                /* the name rules below judge it.                            */
                return(false);
            }

            /* The path with Windows' separator throughout; a drive alone   */
            /* gets its root's, so a letter and colon alone is the top of   */
            /* that drive and not the program's current folder there.       */
            sPath = sTyped.Replace('/', '\\');
            if (sPath.Length == 2)
            {   /* The drive alone. */
                sPath = sPath + "\\";
            }

            /* Split after the last separator: the folder keeps its own          */
            /* trailing separator (so a drive's root stays the root), the rest   */
            /* is the file name -- empty when the path ends in a separator,      */
            /* and then the name the dialog offered is kept.                     */
            iSplit             = sPath.LastIndexOf('\\');
            ISV_sWindowsFolder = sPath.Substring(0, iSplit + 1);
            ISV_sWindowsName   = sPath.Substring(iSplit + 1);
            if (ISV_sWindowsName.Length == 0)
            {   /* A folder alone: the file keeps the offered name. */
                ISV_sWindowsName = Isv_sStartName;
            }

            /* Asked for; ISV_bAccepted stays false, so nothing in the   */
            /* image is chosen.                                          */
            ISV_bWindowsAsked = true;
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* Isv_OnCancelClicked:                                               */
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
        private void Isv_OnCancelClicked(object objSender, EventArgs evArgs)
        {
            /* Closed with ISV_bAccepted still false: the editor saves   */
            /* nothing.                                                  */
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Isv_OnResize:                                                      */
        /*                                                                    */
        /* The dialog's size changed.  Docking has already refitted the rows  */
        /* and the split; what is left is the splitter's first placing (when  */
        /* the first Shown found no width yet), the entry list's columns, and */
        /* keeping the selected directory in view.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the dialog.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the layout fits the new size.                           */
        /*--------------------------------------------------------------------*/
        private void Isv_OnResize(object objSender, EventArgs evArgs)
        {
            /* The splitter (placed once, the first time there is a width),   */
            /* then the entry list's columns at the new width.                */
            Isv_PlaceSplitter();
            Isv_FitColumns();

            if (Isv_treFolders.SelectedNode != null)
            {   /* There is a selection to keep in view. */
                Isv_treFolders.SelectedNode.EnsureVisible();
            }
        }

        /*--------------------------------------------------------------------*/
        /* Isv_OnFormClosed:                                                  */
        /*                                                                    */
        /* The dialog has closed: note it, so a listing or New Folder still   */
        /* running when it closed touches no control when its answer arrives. */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the dialog.                                        */
        /*     evClosed  : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : later answers are dropped.                              */
        /*--------------------------------------------------------------------*/
        private void Isv_OnFormClosed(object objSender, FormClosedEventArgs evClosed)
        {
            /* Closed: every answer that arrives from now on finds this       */
            /* flag and is dropped (a New Folder's answer is stopped by the   */
            /* flag alone); and no listing is counted as awaited any more,    */
            /* so a listing's answer would not match even without it.  The    */
            /* requests themselves are the worker's to end (the caller        */
            /* ends the input once the dialog has returned).                  */
            Isv_bClosed = true;
            Isv_dictLatest.Clear();
            Isv_objListRequest = null;
            Isv_objBranch = null;
        }
    }
}
