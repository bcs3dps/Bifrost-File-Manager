/*----------------------------------------------------------------------------*/
/* TextEditForm.cs                                                            */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The program's own text editor, for a text file that lives inside an image  */
/* -- or a Windows file opened directly from the Windows pane:                */
/* a plain window with the text, a menu and a status bar.  It exists so that  */
/* a configuration file can be read and changed without leaving the program,  */
/* and so that what is written back is EXACTLY the file that was there apart  */
/* from what the user changed.                                                */
/*                                                                            */
/* THREE THINGS ABOUT A LINUX TEXT FILE ARE KEPT, NOT NORMALISED:             */
/*                                                                            */
/*   - Its LINE ENDINGS.  A Linux file ends its lines with a line feed, and   */
/*     that is what is expected and written.  A file that arrives with        */
/*     carriage return + line feed keeps them.  The Windows text box shows    */
/*     line breaks only as CR LF, so the file's own endings are taken off as  */
/*     it is loaded and put back as it is saved (Txe_Load, Txe_Compose).      */
/*   - Its ENCODING.  Valid UTF-8 is edited as UTF-8.  Anything else is       */
/*     edited as Latin-1, in which every byte is one character and every      */
/*     character one byte, so bytes the editor does not understand come back  */
/*     out exactly as they went in.                                           */
/*   - Whether it ENDS WITH a line ending, and whether it starts with a       */
/*     UTF-8 byte-order mark.                                                 */
/*                                                                            */
/* A SAVE IS CAPTURED WHOLE WHEN IT IS ASKED FOR: the text composed to its    */
/* bytes, with the destination and the text's revision at that moment, so     */
/* nothing typed afterwards changes what that save writes.  A save into the   */
/* image is handed to the command worker as a staged file; a save to a        */
/* Windows file is written here through a temporary file that then replaces   */
/* it, never in place.  When a save is acknowledged, the file it wrote        */
/* becomes the one Save writes to and the one a close asks about; the record  */
/* of where the text was first opened from is not changed by it.              */
/*----------------------------------------------------------------------------*/

/* The framework: the base types (the exceptions caught, event arguments),   */
/* the lists of the file's lines and their endings, sizes and colours, the   */
/* file read and written, the UTF-8 and Latin-1 encodings and the text       */
/* built for a save, and the window with its menus and controls.             */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* TSC_clsTextSaveCapture -- one save as it was asked for, kept from the  */
    /* request until its acknowledgement: what it writes, where, and which    */
    /* revision of the text that was.  Its methods are what the writer, the   */
    /* answer and a deferred close call, so each acts on THIS save, not on    */
    /* whichever save happens to be newest -- documentation table.            */
    /*------------------------------------------------------------------------*/
    internal sealed class TSC_clsTextSaveCapture
    {
        internal OFD_clsOpenedFile TSC_objSnapshot;          // the destination as captured for this save: a copy, not the editor's own record, so a later Save As does not move it
        internal List<string> TSC_lstLines;                  // the lines as composed for this save; they become the editor's "as saved" lines only if the save succeeds
        internal List<int> TSC_lstEols;                      // each of those lines' ending (TXE_EOL_*)
        internal byte[] TSC_abFile;                          // the whole file's bytes, encoded once; not copied again (nothing changes them after)
        internal long TSC_lRevision;                         // the text's revision when the save was asked for (Txe_lRevision)
        internal long TSC_lSaveOrder;                        // this save's place among the editor's saves (Txe_lSaveOrder), so a late answer cannot undo a newer one
        internal bool TSC_bCloseWhenSaved;                   // true when this save was asked for by a close: the window closes once it is acknowledged (if nothing changed since)
        private readonly TXE_clsTextEditForm Tsc_frmEditor;  // the editor that asked; borrowed

        /*------------------------------------------------------------------*/
        /* TSC_clsTextSaveCapture:                                          */
        /*                                                                  */
        /* One save, captured.  The lists and bytes are kept by reference:  */
        /* the editor made them for this save and does not change them      */
        /* afterwards.                                                      */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     frmEditor       : the editor asking.                         */
        /*     objSnapshot     : the destination, captured.                 */
        /*     lstLines        : the composed lines.                        */
        /*     lstEols         : their endings.                             */
        /*     abFile          : the encoded file.                          */
        /*     lRevision       : the text's revision.                       */
        /*     lSaveOrder      : this save's order.                         */
        /*     bCloseWhenSaved : true when a close asked for it.            */
        /*                                                                  */
        /* Returns:                                                         */
        /*     (constructor) : the capture; it holds no staged file itself. */
        /*------------------------------------------------------------------*/
        internal TSC_clsTextSaveCapture(TXE_clsTextEditForm frmEditor,
            OFD_clsOpenedFile objSnapshot, List<string> lstLines, List<int> lstEols,
            byte[] abFile, long lRevision, long lSaveOrder, bool bCloseWhenSaved)
        {
            /* Every value kept as given.  The writer then uses only these,   */
            /* never the text box, so the save writes what was asked for.     */
            Tsc_frmEditor = frmEditor;
            TSC_objSnapshot = objSnapshot;
            TSC_lstLines = lstLines;
            TSC_lstEols = lstEols;
            TSC_abFile = abFile;
            TSC_lRevision = lRevision;
            TSC_lSaveOrder = lSaveOrder;
            TSC_bCloseWhenSaved = bCloseWhenSaved;
        }

        /*------------------------------------------------------------------*/
        /* TSC_WriteStage:                                                  */
        /*                                                                  */
        /* Write this save's bytes into the staged file's stream -- the     */
        /* writer an image save hands to the stage (CommandStage.cs,        */
        /* STG_WriteSnapshot), which opens, flushes and closes the stream   */
        /* and deletes the file if the write fails.                         */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     stmOutput : the staged file's stream, the stage's.           */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the bytes are written; a failure throws to the stage. */
        /*------------------------------------------------------------------*/
        internal void TSC_WriteStage(FileStream stmOutput)
        {
            /* The bytes encoded when the save was asked for, as they are. */
            stmOutput.Write(TSC_abFile, 0, TSC_abFile.Length);
        }

        /*------------------------------------------------------------------*/
        /* TSC_OnSaved:                                                     */
        /*                                                                  */
        /* The save's answer, handed to the editor with this capture, so    */
        /* the editor settles THIS save's revision and order.               */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     bSaved : true only when the file was written and the write   */
        /*              acknowledged.                                       */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the editor has the answer (TXE_SaveAcknowledged).     */
        /*------------------------------------------------------------------*/
        internal void TSC_OnSaved(bool bSaved)
        {
            /* This capture and the answer. */
            Tsc_frmEditor.TXE_SaveAcknowledged(this, bSaved);
        }

        /*------------------------------------------------------------------*/
        /* TSC_RunClose:                                                    */
        /*                                                                  */
        /* The close a close's save asked for, run later on the UI thread   */
        /* once that save has succeeded: the editor closes only if the text */
        /* has not changed since this save was captured.                    */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     None.                                                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the editor decides and closes, or not                 */
        /*            (TXE_CompleteDeferredClose).                          */
        /*------------------------------------------------------------------*/
        internal void TSC_RunClose()
        {
            /* This capture, so the test is against ITS revision and order. */
            Tsc_frmEditor.TXE_CompleteDeferredClose(this);
        }
    }

    /*------------------------------------------------------------------------*/
    /* TPI_clsTextPickIntent -- one Save As target choice still to come back  */
    /* from the picker: the editor's destination when it was asked for, and   */
    /* whether a close asked for it, kept apart from any other choice still   */
    /* to come -- documentation table.                                        */
    /*------------------------------------------------------------------------*/
    internal sealed class TPI_clsTextPickIntent
    {
        internal OFD_clsOpenedFile TPI_objBinding;           // the editor's destination record when the choice was asked for; the answer is used only if it is still the editor's
        internal bool TPI_bCloseWhenSaved;                   // true when the user's own close asked for the choice: the save it leads to closes the window, and no choice asks the close again
        private readonly TXE_clsTextEditForm Tpi_frmEditor;  // the editor that asked; borrowed

        /*------------------------------------------------------------------*/
        /* TPI_clsTextPickIntent:                                           */
        /*                                                                  */
        /* One choice asked for.  It captures no text: the save is captured */
        /* only once a target has been chosen.                              */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     frmEditor       : the editor asking.                         */
        /*     objBinding      : its destination record now.                */
        /*     bCloseWhenSaved : true when a close asked for it.            */
        /*                                                                  */
        /* Returns:                                                         */
        /*     (constructor) : the intent.                                  */
        /*------------------------------------------------------------------*/
        internal TPI_clsTextPickIntent(TXE_clsTextEditForm frmEditor,
            OFD_clsOpenedFile objBinding, bool bCloseWhenSaved)
        {
            /* Kept as given; nothing about the editor is changed yet. */
            Tpi_frmEditor = frmEditor;
            TPI_objBinding = objBinding;
            TPI_bCloseWhenSaved = bCloseWhenSaved;
        }

        /*------------------------------------------------------------------*/
        /* TPI_OnChosen:                                                    */
        /*                                                                  */
        /* The picker's answer, handed to the editor with this intent.      */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objTarget : the target chosen, or null when the choice was   */
        /*                 cancelled or refused.                            */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the editor saves to it, if its destination is still   */
        /*            the one recorded; a null saves nothing, and asks the  */
        /*            close again when a close asked for the choice         */
        /*            (TXE_ApplyTargetChoice).                              */
        /*------------------------------------------------------------------*/
        internal void TPI_OnChosen(OFD_clsOpenedFile objTarget)
        {
            /* This intent and the answer. */
            Tpi_frmEditor.TXE_ApplyTargetChoice(this, objTarget);
        }
    }

    /*------------------------------------------------------------------------*/
    /* TXE_clsTextEditForm -- the text editor's window.  A plain Form: its    */
    /* base class is declared here with the rest of its UI.                   */
    /*                                                                        */
    /* Derives from Form because the framework requires it: WinForms makes a  */
    /* window only as a Form subclass.  That is the one kind of inheritance   */
    /* allowed here -- from a framework base class, never from a class of     */
    /* ours.                                                                  */
    /*------------------------------------------------------------------------*/
    internal sealed class TXE_clsTextEditForm : Form
    {
        /* The window's first size and the text's face -- documentation   */
        /* table.                                                         */
        private const int    TXE_DEFAULT_WIDTH  = 900;           // first window width, pixels
        private const int    TXE_DEFAULT_HEIGHT = 640;           // first window height, pixels
        private const string TXE_FONT_NAME      = "Consolas";    // the text's typeface: fixed-width, as a configuration file wants
        private const int    TXE_FONT_DEFAULT   = 10;            // the text's size on a first run, points

        /* The sizes View > Text Size offers, points. */
        private static readonly int[] Txe_aiFontSizes = { 8, 9, 10, 11, 12, 14, 16, 18, 20, 24 };   // fixed offered sizes, points; includes TXE_FONT_DEFAULT

        /* The two fixed values the load and the save turn on --   */
        /* documentation table.                                    */
        private const int    TXE_LATIN1         = 28591;         // the Windows code page number of ISO 8859-1 (Latin-1)
        private const string TXE_BREAK          = "\r\n";        // what the Windows text box needs between two lines to show them as two

        /* How a line ends -- documentation table. */
        private const int TXE_EOL_NONE = 0;    // no ending: only the last line of a file can have this
        private const int TXE_EOL_LF   = 1;    // line feed (0x0A): the Linux ending
        private const int TXE_EOL_CRLF = 2;    // carriage return + line feed (0x0D 0x0A): the Windows ending
        private const int TXE_EOL_CR   = 3;    // carriage return alone (0x0D): the old Macintosh ending

        /* The window's own commands, as its queue (Txe_objQueue) carries   */
        /* them: each handler posts one and Txe_RunItem runs it --          */
        /* documentation table.                                             */
        private enum Txe_enmItem
        {
            Find = 1,           // Edit > Find: ask what to look for, then go to it (Txe_AskFind)
            FindNext = 2,       // Edit > Find Next: the last search again, or ask when there is none (Txe_FindNextOrAsk)
            SaveAsImage = 3,    // File > Save As > Image...: an image destination asked for (Txe_RequestSaveAs)
            SaveAsWindows = 4,  // File > Save As > Windows...: a Windows destination asked for (Txe_RequestSaveAs)
            CloseAgain = 5,     // the window's own close asked again: a Save As chosen from its close question was cancelled, so the question shows once more (TXE_ApplyTargetChoice)
            FindAnswer = 6,     // the Find prompt has closed: its answer applied and the window enabled again (Txe_ApplyFindAnswer); its argument the prompt
            AskClose = 7,       // a close found unsaved text: the close question shown (Txe_ShowCloseQuestion); its argument the worker's close question's display, or null for the window's own close
            CloseAnswer = 8,    // the close question has closed: the window enabled again and its choice acted on (Txe_ApplyCloseAnswer); its argument the question's form
            Save = 9,           // File > Save: a save where Save writes now, if one may start (Txe_RunSave)
            Close = 10,         // File > Close: the window closed, Txe_OnFormClosing asking about unsaved changes
            Undo = 11,          // Edit > Undo: the text box's own undo
            Cut = 12,           // Edit > Cut: the text box's own cut
            Copy = 13,          // Edit > Copy: the text box's own copy
            Paste = 14,         // Edit > Paste: the text box's own paste
            SelectAll = 15,     // Edit > Select All: the whole text selected
            Wrap = 16,          // View > Word Wrap ticked or unticked: the box set to the tick (Txe_ApplyWrap)
            TextSize = 17       // a View > Text Size item chosen: the size remembered and shown (Txe_ApplyTextSize); its argument the size in points, boxed
        }

        /* The load's outcome, which the caller reads before it shows the   */
        /* window (the constructor loads the file).                         */
        private string Txe_sLoadError;        // "" when the file was loaded; otherwise why it could not be, and the window is not to be shown

        /* The window's controls -- documentation table. */
        private MenuStrip            Txe_mnuMain;        // the menu bar
        private ToolStripMenuItem    Txe_tsmSave;        // File > Save
        private ToolStripMenuItem    Txe_tsmSaveAs;      // File > Save As; its two choices, Image... and Windows..., are added by SVM_clsSaveMenu (EditorSaveMenu.cs)
        private ToolStripMenuItem    Txe_tsmWrap;        // View > Word Wrap (a tick mark)
        private ToolStripMenuItem    Txe_tsmTextSize;    // View > Text Size: a sub-menu of sizes; each item's Tag is its size in points
        private TextBox              Txe_txtBody;        // the text
        private StatusStrip          Txe_stsStatus;      // the status bar
        private ToolStripStatusLabel Txe_tslMessage;     // its message, taking the width the others leave
        private ToolStripStatusLabel Txe_tslCaret;       // the caret's line and column
        private ToolStripStatusLabel Txe_tslEol;         // the file's line endings
        private ToolStripStatusLabel Txe_tslEncoding;    // the file's encoding

        /* What the window is editing, the file's facts as loaded, and the   */
        /* window's own state -- documentation table.                        */
        private OFD_clsOpenedFile  Txe_file;                           // where Save writes: an image entry and its read-out copy, or a Windows file opened directly; replaced by a save's own record once that save succeeds (TXE_SaveAcknowledged)
        private OFD_SaveRequest Txe_pfnSave;                           // the main window's call a save is handed to (MainForm.cs Main_SaveOpenedFile, then the coordinator): an image save's stage, or the fact that a Windows file was written; this window never calls the engine
        private Font            Txe_fntDialog;                         // the program's dialog font, given by the main window, for every dialog this window opens (not owned here)
        private bool            Txe_bUtf8;                             // true: edited as UTF-8; false: as Latin-1, one character per byte
        private bool            Txe_bBom;                              // true when the file starts with the UTF-8 byte-order mark (kept on save)
        private List<string>    Txe_lstLines;                          // the file's lines as last loaded or saved, without their endings
        private List<int>       Txe_lstEols;                           // each of those lines' ending, a TXE_EOL_* value
        private int             Txe_iMainEol;                          // the ending most of the file's lines have; what a new line is given
        private bool            Txe_bReadOnly;                         // true when the text cannot be saved from here (it holds NUL characters)
        private bool            Txe_bDirty;                            // true when the text has changed since it was loaded or saved
        private bool            Txe_bLoading;                          // true while the text box is being filled by the program (that is not an edit)
        private long Txe_lRevision;                                    // the text's revision: 0 as loaded, one up for every edit the user makes (Txe_OnTextChanged); a save records it, so its answer can tell whether the text changed since
        private long Txe_lSaveOrder;                                   // the number of the last save asked for, counted up from 1 (0 before any); unrelated to the revision
        private long Txe_lAcknowledgedOrder;                           // the number of the newest save that succeeded and was applied (0 before any); an older save answered later is not applied over it
        private int Txe_iPendingSaves;                                 // saves handed over and not yet answered; a close is held while any is (Txe_PresentClose)
        private int Txe_iLocalWork;                                    // how deep the window is inside a save of its own (Txe_SaveTo); while not 0 the coordinator holds back a close question (TXE_HasLocalWork)
        private int Txe_iClosing;                                      // how deep the window is inside its own closing handler (Txe_OnFormClosing); counted the same way
        private List<TSC_clsTextSaveCapture> Txe_lstCloseEvaluations;  // succeeded saves that asked for the window to close, held until its own work and closing handler have returned (Txe_PostReadyCloses)
        private EUI_PostFact Txe_pfnPostFact;                          // the coordinator's call the window reports its returns through (EUI_PostEditorFact); set at registration (TXE_BindCommandFacts), null before
        private string          Txe_sFind;                             // what Find last looked for; "" before the first search
        private PRM_clsPromptForm Txe_frmPrompt;                       // the Find prompt while it is up -- modeless, over this window, which is disabled meanwhile (Txe_AskFind); null otherwise.  Counted as the window's own work (TXE_HasLocalWork)
        private WQU_clsWindowQueue Txe_objQueue;                       // the window's own command queue (WindowQueue.cs): every menu handler posts to it (Save, Save As, Close, the Edit and View items, Find, Find Next), and so do the Find prompt's and close question's closings and a close whose Save As was cancelled; it runs each through Txe_RunItem, one at a time
        private bool Txe_bCloseAsked;                                  // true from the closing handler's post of the close question (Txe_PresentClose) until the queue shows it (Txe_ShowCloseQuestion).  Counted as the window's own work (TXE_HasLocalWork)
        private RCD_clsRecoveryDialog Txe_objCloseQuestion;            // the close question while it is up -- modeless, over this window, which is disabled meanwhile (Txe_ShowCloseQuestion); null otherwise.  Counted as the window's own work (TXE_HasLocalWork)
        private PRS_clsPresentation Txe_objCloseShown;                 // the worker's close question that close question was shown under, taken when it was shown; null for the window's own close
        private bool Txe_bDiscardOnClose;                              // true only across the Close() a Discard answer makes (Txe_ApplyCloseAnswer): that close lets the changed text go without asking again

        /*--------------------------------------------------------------------*/
        /* TXE_clsTextEditForm (constructor):                                 */
        /*                                                                    */
        /* Build the window and load the file into it.  A file that cannot be */
        /* read leaves ReadLoadError saying why; the caller then shows that   */
        /* and disposes the window without showing it.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     fntDialog : the program's dialog font, which every dialog and  */
        /*                 message box this window opens is given (the        */
        /*                 caller's; not freed here).  The text itself is in  */
        /*                 the editor's own size setting, not this.           */
        /*     objFile   : the file to edit: an image entry, whose read-out   */
        /*                 copy is OFD_sTempFile, or a Windows file opened    */
        /*                 directly, which is that file itself.               */
        /*     pfnSave   : the call each save is handed to (MainForm.cs       */
        /*                 Main_SaveOpenedFile): an image save as a staged    */
        /*                 file, a Windows save after this window has written */
        /*                 the file itself.                                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the window, not yet shown.                     */
        /*--------------------------------------------------------------------*/
        internal TXE_clsTextEditForm(Font fntDialog, OFD_clsOpenedFile objFile, OFD_SaveRequest pfnSave)
        {
            /* The starting state: no load error yet; the main window's font,   */
            /* the file and its save route kept; the file's facts at the        */
            /* defaults Txe_Load corrects (UTF-8, no byte-order mark, no        */
            /* lines, line feed the common ending); editable, unchanged,        */
            /* nothing loading; no close waiting on a save; nothing searched    */
            /* for; no close question up and no discard in hand.  The           */
            /* revision and the save counts are not set here: they              */
            /* start at the fields' default, 0, as does the coordinator's       */
            /* call until registration sets it.                                 */
            Txe_sLoadError      = "";
            Txe_fntDialog       = fntDialog;
            Txe_file            = objFile;
            Txe_pfnSave         = pfnSave;
            Txe_bUtf8           = true;
            Txe_bBom            = false;
            Txe_lstLines        = new List<string>();
            Txe_lstEols         = new List<int>();
            Txe_iMainEol        = TXE_EOL_LF;
            Txe_bReadOnly       = false;
            Txe_bDirty          = false;
            Txe_bLoading        = false;
            Txe_lstCloseEvaluations = new List<TSC_clsTextSaveCapture>();
            Txe_sFind           = "";
            Txe_bCloseAsked     = false;
            Txe_objCloseQuestion = null;
            Txe_objCloseShown   = null;
            Txe_bDiscardOnClose = false;

            /* The window's own command queue, its items run by Txe_RunItem. */
            Txe_objQueue = new WQU_clsWindowQueue(this, Txe_RunItem);

            /* In this order: the controls, then the file read into them      */
            /* (the load fills the text box and the status bar, so they       */
            /* must exist), then the title and the caret's place, which       */
            /* show what the load found.  A failed load sets Txe_sLoadError   */
            /* and the caller disposes the window unshown.                    */
            Txe_BuildControls();
            Txe_Load();
            Txe_UpdateTitle();
            Txe_UpdateCaretStatus();

            /* Hooked last, so the building above runs neither: a close is    */
            /* checked for unsaved changes and a save in flight               */
            /* (Txe_OnFormClosing), and a resize shows the caret's line and   */
            /* column again, since word wrap may have moved them              */
            /* (Txe_OnResize).                                                */
            this.FormClosing += Txe_OnFormClosing;
            this.Resize      += Txe_OnResize;
        }

        /*--------------------------------------------------------------------*/
        /* Txe_BuildControls:                                                 */
        /*                                                                    */
        /* Create the menu, the text box and the status bar.  Everything is   */
        /* placed by docking: the text box fills what the two bars leave.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window's controls exist.                            */
        /*--------------------------------------------------------------------*/
        private void Txe_BuildControls()
        {
            ToolStripMenuItem tsmFile;
            ToolStripMenuItem tsmEdit;
            ToolStripMenuItem tsmView;
            ToolStripMenuItem tsmItem;
            int               i;

            /* Centred on the SCREEN at its first size; the window keeps   */
            /* WinForms' sizable border, so the user may resize it.        */
            this.StartPosition = FormStartPosition.CenterScreen;
            this.ClientSize    = new Size(TXE_DEFAULT_WIDTH, TXE_DEFAULT_HEIGHT);

            /* File > Save, with Ctrl+S as a real shortcut: writes the text   */
            /* back -- into the image, or, for a Windows file, through a      */
            /* temporary file that then replaces it (Txe_OnSaveClicked).      */
            Txe_tsmSave              = new ToolStripMenuItem("&Save");
            Txe_tsmSave.ShortcutKeys = Keys.Control | Keys.S;
            Txe_tsmSave.Click       += Txe_OnSaveClicked;

            /* File > Save As.  SVM_clsSaveMenu (EditorSaveMenu.cs) gives the   */
            /* item its text, its Ctrl+Shift+S shortcut and its two choices,    */
            /* Image... and Windows..., each wired to its own handler here,     */
            /* whatever the file was opened from; it asks Txe_ReadCurrentFile   */
            /* where Save writes now to enable them.  The object is not kept    */
            /* in a field: the item's own events hold it.  Choosing a           */
            /* destination moves nothing -- only a save to it that succeeds     */
            /* does (TXE_SaveAcknowledged).                                     */
            Txe_tsmSaveAs = new ToolStripMenuItem("Save &As...");
            new SVM_clsSaveMenu(Txe_tsmSaveAs, Txe_ReadCurrentFile, Txe_OnSaveAsImageClicked,
                Txe_OnSaveAsWindowsClicked);

            /* Close, with Ctrl+W: the window's ordinary close, so unsaved   */
            /* changes are asked about (Txe_OnFormClosing).                  */
            tsmItem              = new ToolStripMenuItem("&Close");
            tsmItem.ShortcutKeys = Keys.Control | Keys.W;
            tsmItem.Click       += Txe_OnCloseClicked;

            /* The File menu: the two saves, a separator, Close. */
            tsmFile = new ToolStripMenuItem("&File");
            tsmFile.DropDownItems.Add(Txe_tsmSave);
            tsmFile.DropDownItems.Add(Txe_tsmSaveAs);
            tsmFile.DropDownItems.Add(new ToolStripSeparator());
            tsmFile.DropDownItems.Add(tsmItem);

            /* Edit: the text box's own commands, then the search. */
            tsmEdit = new ToolStripMenuItem("&Edit");

            /* Undo, then a separator.  Its key is shown for display only     */
            /* (ShortcutKeyDisplayString, not ShortcutKeys): the text box     */
            /* takes Ctrl+Z itself, and a real menu shortcut would take the   */
            /* key from it first (as found).  Cut, Copy and Paste below are   */
            /* the same, and Select All's key is display only too; each       */
            /* click calls the text box's own command.                        */
            tsmItem                          = new ToolStripMenuItem("&Undo");
            tsmItem.ShortcutKeyDisplayString = "Ctrl+Z";
            tsmItem.Click                   += Txe_OnUndoClicked;
            tsmEdit.DropDownItems.Add(tsmItem);
            tsmEdit.DropDownItems.Add(new ToolStripSeparator());

            /* Cut, its key shown only (as Undo's). */
            tsmItem                          = new ToolStripMenuItem("Cu&t");
            tsmItem.ShortcutKeyDisplayString = "Ctrl+X";
            tsmItem.Click                   += Txe_OnCutClicked;
            tsmEdit.DropDownItems.Add(tsmItem);

            /* Copy, its key shown only. */
            tsmItem                          = new ToolStripMenuItem("&Copy");
            tsmItem.ShortcutKeyDisplayString = "Ctrl+C";
            tsmItem.Click                   += Txe_OnCopyClicked;
            tsmEdit.DropDownItems.Add(tsmItem);

            /* Paste, its key shown only. */
            tsmItem                          = new ToolStripMenuItem("&Paste");
            tsmItem.ShortcutKeyDisplayString = "Ctrl+V";
            tsmItem.Click                   += Txe_OnPasteClicked;
            tsmEdit.DropDownItems.Add(tsmItem);

            /* Select All, its key shown only -- Ctrl+A itself is handled   */
            /* in Txe_OnBodyKeyDown, since this text box does not take it   */
            /* (as found); then a separator before the search.              */
            tsmItem                          = new ToolStripMenuItem("Select &All");
            tsmItem.ShortcutKeyDisplayString = "Ctrl+A";
            tsmItem.Click                   += Txe_OnSelectAllClicked;
            tsmEdit.DropDownItems.Add(tsmItem);
            tsmEdit.DropDownItems.Add(new ToolStripSeparator());

            /* Find, with Ctrl+F as a real shortcut: the text box has no   */
            /* use for the key.                                            */
            tsmItem              = new ToolStripMenuItem("&Find...");
            tsmItem.ShortcutKeys = Keys.Control | Keys.F;
            tsmItem.Click       += Txe_OnFindClicked;
            tsmEdit.DropDownItems.Add(tsmItem);

            /* Find Next, with F3, as Windows' own editors have it. */
            tsmItem              = new ToolStripMenuItem("Find &Next");
            tsmItem.ShortcutKeys = Keys.F3;
            tsmItem.Click       += Txe_OnFindNextClicked;
            tsmEdit.DropDownItems.Add(tsmItem);

            /* View > Word Wrap, a tick that toggles itself on a click; its   */
            /* change re-flows the text box (Txe_OnWrapChanged).  It starts   */
            /* off, matching the box's WordWrap set below.                    */
            Txe_tsmWrap                 = new ToolStripMenuItem("&Word Wrap");
            Txe_tsmWrap.CheckOnClick    = true;
            Txe_tsmWrap.CheckedChanged += Txe_OnWrapChanged;

            /* The size of the text, remembered in the registry (the setting   */
            /* is shared with the hex editor, AppSettings.cs): one item per    */
            /* size, its Tag the size in points; Txe_ApplyFontSize ticks the   */
            /* one in use.                                                     */
            Txe_tsmTextSize = new ToolStripMenuItem("&Text Size");

            for (i = 0; i < Txe_aiFontSizes.Length; i++)
            {   /* One size per iteration. */
                tsmItem        = new ToolStripMenuItem(Txe_aiFontSizes[i] + " point");
                tsmItem.Tag    = Txe_aiFontSizes[i];
                tsmItem.Click += Txe_OnTextSizeClicked;
                Txe_tsmTextSize.DropDownItems.Add(tsmItem);
            }

            /* The View menu: Word Wrap, then Text Size. */
            tsmView = new ToolStripMenuItem("&View");
            tsmView.DropDownItems.Add(Txe_tsmWrap);
            tsmView.DropDownItems.Add(Txe_tsmTextSize);

            /* The menu bar: File, Edit, View. */
            Txe_mnuMain = new MenuStrip();
            Txe_mnuMain.Items.Add(tsmFile);
            Txe_mnuMain.Items.Add(tsmEdit);
            Txe_mnuMain.Items.Add(tsmView);

            /* The text: multi-line, filling the window, scrollable both      */
            /* ways and unwrapped (lines run on, as a configuration file's    */
            /* do), with no length limit of the box's own (0 means none, as   */
            /* found).  Tab and Enter are typed into the text rather than     */
            /* moving the focus or pressing a button; the selection stays     */
            /* shown while Find's prompt has the focus.  Its size is the      */
            /* remembered one (absent: TXE_FONT_DEFAULT).  A change marks     */
            /* the window dirty (Txe_OnTextChanged); Ctrl+A is handled by     */
            /* hand (Txe_OnBodyKeyDown); a key or a click released updates    */
            /* the caret's place on the status bar (Txe_OnBodyKeyUp and       */
            /* Txe_OnBodyMouseUp, one handler for each event).                */
            Txe_txtBody               = new TextBox();
            Txe_txtBody.Multiline     = true;
            Txe_txtBody.Dock          = DockStyle.Fill;
            Txe_txtBody.ScrollBars    = ScrollBars.Both;
            Txe_txtBody.WordWrap      = false;
            Txe_txtBody.AcceptsReturn = true;
            Txe_txtBody.AcceptsTab    = true;
            Txe_txtBody.MaxLength     = 0;
            Txe_txtBody.HideSelection = false;
            Txe_ApplyFontSize(SET_Settings.SET_ReadInt(SET_Settings.SET_EDITOR_FONT, TXE_FONT_DEFAULT));
            Txe_txtBody.TextChanged  += Txe_OnTextChanged;
            Txe_txtBody.KeyDown      += Txe_OnBodyKeyDown;
            Txe_txtBody.KeyUp        += Txe_OnBodyKeyUp;
            Txe_txtBody.MouseUp      += Txe_OnBodyMouseUp;

            /* The status bar's message: it takes the width the others        */
            /* leave (Spring), left-aligned, empty until there is something   */
            /* to say.                                                        */
            Txe_tslMessage           = new ToolStripStatusLabel("");
            Txe_tslMessage.Spring    = true;
            Txe_tslMessage.TextAlign = ContentAlignment.MiddleLeft;

            /* The three facts after it -- the caret's place, the line        */
            /* endings, the encoding -- filled by Txe_UpdateCaretStatus and   */
            /* Txe_ShowFileFacts.                                             */
            Txe_tslCaret    = new ToolStripStatusLabel("");
            Txe_tslEol      = new ToolStripStatusLabel("");
            Txe_tslEncoding = new ToolStripStatusLabel("");

            /* Each fact with a rule at its left, parting it from the one   */
            /* before.                                                      */
            Txe_tslCaret.BorderSides    = ToolStripStatusLabelBorderSides.Left;
            Txe_tslEol.BorderSides      = ToolStripStatusLabelBorderSides.Left;
            Txe_tslEncoding.BorderSides = ToolStripStatusLabelBorderSides.Left;

            /* The bar, the message first and the facts after it. */
            Txe_stsStatus = new StatusStrip();
            Txe_stsStatus.Items.Add(Txe_tslMessage);
            Txe_stsStatus.Items.Add(Txe_tslCaret);
            Txe_stsStatus.Items.Add(Txe_tslEol);
            Txe_stsStatus.Items.Add(Txe_tslEncoding);

            /* Docking runs in reverse order of adding (as found): the menu,   */
            /* added last, takes the top, the status bar the bottom, and the   */
            /* text box, added first, fills what they leave.  MainMenuStrip    */
            /* makes the menu the window's own, so Alt reaches it.             */
            this.Controls.Add(Txe_txtBody);
            this.Controls.Add(Txe_stsStatus);
            this.Controls.Add(Txe_mnuMain);
            this.MainMenuStrip = Txe_mnuMain;
        }

        /*--------------------------------------------------------------------*/
        /* Txe_Load:                                                          */
        /*                                                                    */
        /* Read the file -- an entry's read-out copy, or the Windows file     */
        /* itself -- and show it, remembering its                             */
        /* encoding, byte-order mark and every line's ending so a save can    */
        /* put the file back exactly.  A file holding NUL characters is shown */
        /* READ-ONLY, since the text box cannot hold one.                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the text is showing, or Txe_sLoadError says why not.    */
        /*--------------------------------------------------------------------*/
        private void Txe_Load()
        {
            byte[] abFile;
            string sText;
            int    iStart;

            try
            {   /* The whole file read in one go -- the entry's temporary   */
                /* copy, or the Windows file itself -- since the text box   */
                /* holds all of it anyway; it may have been removed or be   */
                /* unreadable, or too large to read whole.                  */
                abFile = File.ReadAllBytes(Txe_file.OFD_sTempFile);
            }
            catch (Exception exRead)
            {   /* Nothing to show: the reason, for the caller to report   */
                /* before it disposes the window unshown.                  */
                Txe_sLoadError = exRead.Message;
                return;
            }

            /* STEP 1: the encoding.  The text starts at byte 0 unless a   */
            /* byte-order mark comes first.                                */
            iStart = 0;

            if (abFile.Length >= 3 && abFile[0] == 0xEF && abFile[1] == 0xBB && abFile[2] == 0xBF)
            {   /* A UTF-8 byte-order mark: not part of the text, so skipped,   */
                /* but noted so a save puts it back.                            */
                Txe_bBom = true;
                iStart   = 3;
            }

            try
            {   /* Taken as UTF-8 only if it is VALID UTF-8: the strict       */
                /* decoder (the second argument) throws on any byte           */
                /* sequence that is not, where the ordinary one would put a   */
                /* replacement character in and so change the file on save.   */
                sText     = new UTF8Encoding(false, true).GetString(abFile, iStart, abFile.Length - iStart);
                Txe_bUtf8 = true;
            }
            catch (DecoderFallbackException)
            {   /* Not UTF-8: Latin-1, one character per byte and every      */
                /* byte a character, so nothing is lost or altered and the   */
                /* save turns each character back into its byte.             */
                sText     = Encoding.GetEncoding(TXE_LATIN1).GetString(abFile, iStart, abFile.Length - iStart);
                Txe_bUtf8 = false;
            }

            /* STEP 2: the text taken apart into lines, each line's ending    */
            /* remembered (Txe_SplitLines), and the commonest ending, which   */
            /* a new line is given.                                           */
            Txe_SplitLines(sText, Txe_lstLines, Txe_lstEols);
            Txe_iMainEol = Txe_CommonEol(Txe_lstEols);

            if (sText.IndexOf('\0') >= 0)
            {   /* NUL characters: the text box's text stops at the first       */
                /* one (as found), so saving from here would cut the file       */
                /* short.  The window becomes read-only, each NUL is shown as   */
                /* a dot, and the lines are taken again from that text (the     */
                /* endings are unchanged, so the common ending stands).  The    */
                /* dotted lines are never saved -- saving is switched off       */
                /* below.                                                       */
                Txe_bReadOnly = true;
                sText         = sText.Replace('\0', '.');
                Txe_SplitLines(sText, Txe_lstLines, Txe_lstEols);
            }

            /* STEP 3: shown, the lines joined by CR LF, the one break the    */
            /* Windows text box displays; the caret at the start.  The load   */
            /* flag is up meanwhile, so the box's TextChanged does not mark   */
            /* the window as changed (Txe_OnTextChanged).                     */
            Txe_bLoading = true;
            Txe_txtBody.Text = string.Join(TXE_BREAK, Txe_lstLines.ToArray());
            Txe_txtBody.Select(0, 0);
            Txe_bLoading = false;

            if (Txe_bReadOnly)
            {   /* Look, do not touch: the box read-only (its background set    */
                /* back to the window colour, which read-only would grey, so    */
                /* it stays readable), both saves off, and the status bar       */
                /* says why and points to the hex editor, the tool for such     */
                /* a file, by the menu command that reaches it on every route   */
                /* here -- Open As > Hex -- rather than the binary-file         */
                /* question's Edit Hex button, which an Open As > Text never    */
                /* showed.                                                      */
                Txe_txtBody.ReadOnly  = true;
                Txe_txtBody.BackColor = SystemColors.Window;
                Txe_tsmSave.Enabled   = false;
                Txe_tsmSaveAs.Enabled = false;
                Txe_tslMessage.Text   = "Read-only here: the file holds NUL characters (shown as dots). Use Open As > Hex to change it.";
            }

            /* The line endings and the encoding found, on the status bar. */
            Txe_ShowFileFacts();
        }

        /*--------------------------------------------------------------------*/
        /* Txe_SplitLines:                                                    */
        /*                                                                    */
        /* Take a text apart into its lines, recording how each one ended.    */
        /* All three endings are recognised: line feed, carriage return +     */
        /* line feed, and a carriage return alone.  What follows the last     */
        /* ending is the last line, which has no ending -- and is an empty    */
        /* line when the text ends with one -- so the lines joined with their */
        /* endings are always exactly the text that came in.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sText    : the text.                                           */
        /*     lstLines : receives the lines without their endings (emptied   */
        /*                first).                                             */
        /*     lstEols  : receives each line's TXE_EOL_* value (emptied       */
        /*                first).                                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the two lists hold the text's lines; there is always at */
        /*            least one.                                              */
        /*--------------------------------------------------------------------*/
        private static void Txe_SplitLines(string sText, List<string> lstLines, List<int> lstEols)
        {
            int iLineStart;
            int i;

            /* Both lists emptied (the caller may pass the ones it filled   */
            /* before); the first line starts at the text's start.          */
            lstLines.Clear();
            lstEols.Clear();
            iLineStart = 0;
            i          = 0;

            while (i < sText.Length)
            {   /* One character per iteration (two, at a CR LF).  Each   */
                /* ending closes the line before it and starts the next   */
                /* after it.                                              */
                if (sText[i] == '\n')
                {   /* A line feed alone. */
                    lstLines.Add(sText.Substring(iLineStart, i - iLineStart));
                    lstEols.Add(TXE_EOL_LF);
                    i++;
                    iLineStart = i;
                }
                else if (sText[i] == '\r' && i + 1 < sText.Length && sText[i + 1] == '\n')
                {   /* Carriage return + line feed: one ending, two characters.   */
                    /* Tested before the bare CR below, which would otherwise     */
                    /* take the CR and leave the LF as a second, empty line.      */
                    lstLines.Add(sText.Substring(iLineStart, i - iLineStart));
                    lstEols.Add(TXE_EOL_CRLF);
                    i          = i + 2;
                    iLineStart = i;
                }
                else if (sText[i] == '\r')
                {   /* A carriage return alone. */
                    lstLines.Add(sText.Substring(iLineStart, i - iLineStart));
                    lstEols.Add(TXE_EOL_CR);
                    i++;
                    iLineStart = i;
                }
                else
                {   /* An ordinary character of the line. */
                    i++;
                }
            }

            /* What is left after the last ending: the last line, with no    */
            /* ending -- empty when the text ends with one, so joining the   */
            /* lines with their endings gives the text back exactly.         */
            lstLines.Add(sText.Substring(iLineStart));
            lstEols.Add(TXE_EOL_NONE);
        }

        /*--------------------------------------------------------------------*/
        /* Txe_CommonEol:                                                     */
        /*                                                                    */
        /* The ending most lines of a file have -- the one a line the user    */
        /* adds is given.  A file with no line ending at all (one line, or    */
        /* empty) is a Linux file until shown otherwise: line feed.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstEols : each line's TXE_EOL_* value.                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : TXE_EOL_LF, TXE_EOL_CRLF or TXE_EOL_CR.                  */
        /*--------------------------------------------------------------------*/
        private static int Txe_CommonEol(List<int> lstEols)
        {
            int[] aiCount;
            int   iBest;
            int   i;

            /* A count for each TXE_EOL_* value, indexed by it (NONE too,   */
            /* which is counted but never chosen).                          */
            aiCount = new int[TXE_EOL_CR + 1];

            for (i = 0; i < lstEols.Count; i++)
            {   /* One line per iteration: its ending counted. */
                aiCount[lstEols[i]]++;
            }

            /* Line feed to start with, so it wins a tie and a file with no   */
            /* ending at all: each other kind must have STRICTLY more lines   */
            /* to take over (the tests below use >).                          */
            iBest = TXE_EOL_LF;

            if (aiCount[TXE_EOL_CRLF] > aiCount[iBest])
            {   /* More CR LF lines than LF lines. */
                iBest = TXE_EOL_CRLF;
            }

            if (aiCount[TXE_EOL_CR] > aiCount[iBest])
            {   /* More bare-CR lines than either. */
                iBest = TXE_EOL_CR;
            }

            /* The commonest ending. */
            return(iBest);
        }

        /*--------------------------------------------------------------------*/
        /* Txe_ShowFileFacts:                                                 */
        /*                                                                    */
        /* Put the file's line endings and encoding in the status bar.  The   */
        /* endings read "mixed" when the file has more than one kind, with    */
        /* the commonest named, since that is what a new line will get.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the two status labels are set.                          */
        /*--------------------------------------------------------------------*/
        private void Txe_ShowFileFacts()
        {
            string sEol;
            bool   bMixed;
            int    i;

            /* The common ending's name: LF unless one of the others is. */
            sEol = "LF";

            if (Txe_iMainEol == TXE_EOL_CRLF)
            {   /* Mostly Windows endings. */
                sEol = "CRLF";
            }
            else if (Txe_iMainEol == TXE_EOL_CR)
            {   /* Mostly bare carriage returns. */
                sEol = "CR";
            }

            /* Mixed when any line ends otherwise.  The last line's NONE is   */
            /* not a kind of ending, so it is passed over.  The search runs   */
            /* on past the first hit, which changes nothing.                  */
            bMixed = false;

            for (i = 0; i < Txe_lstEols.Count; i++)
            {   /* One line per iteration, looking for an ending that is not the common one. */
                if (Txe_lstEols[i] != TXE_EOL_NONE && Txe_lstEols[i] != Txe_iMainEol)
                {   /* A second kind of ending. */
                    bMixed = true;
                }
            }

            if (bMixed)
            {   /* Say so, and which kind a new line gets. */
                sEol = "mixed (new lines " + sEol + ")";
            }

            /* The endings onto the status bar; then the encoding's label. */
            Txe_tslEol.Text = sEol;

            if (!Txe_bUtf8)
            {   /* Not UTF-8: every byte is kept as it is. */
                Txe_tslEncoding.Text = "8-bit (Latin-1)";
            }
            else if (Txe_bBom)
            {   /* UTF-8 with the byte-order mark. */
                Txe_tslEncoding.Text = "UTF-8 with BOM";
            }
            else
            {   /* Plain UTF-8, which pure ASCII also is. */
                Txe_tslEncoding.Text = "UTF-8";
            }
        }

        /*--------------------------------------------------------------------*/
        /* Txe_Compose:                                                       */
        /*                                                                    */
        /* Turn the text now in the box back into the file's bytes, in the    */
        /* file's own encoding and with its own byte-order mark.  THE LINE    */
        /* ENDINGS are the point: the box holds only lines, so each must be   */
        /* given an ending again.  The untouched lines -- the identical runs  */
        /* at the top and the bottom -- keep their own; every line between    */
        /* them gets the file's commonest ending, even one that happens to be */
        /* unchanged.  Whether the file ends with a line ending follows the   */
        /* text: it does when the box's last line is empty.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstNewLines : receives the box's lines (emptied first).        */
        /*     lstNewEols  : receives the ending given to each.               */
        /*     sProblem    : receives "" when the bytes were made; otherwise  */
        /*                   why the text cannot be written in the file's     */
        /*                   encoding.                                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte[] : the file's new content; null when sProblem is set.    */
        /*--------------------------------------------------------------------*/
        private byte[] Txe_Compose(List<string> lstNewLines, List<int> lstNewEols, out string sProblem)
        {
            StringBuilder sbText;
            List<int>     lstIgnored;
            byte[]        abText;
            byte[]        abFile;
            string        sText;
            int           iOld;
            int           iNew;
            int           iTop;
            int           iBottom;
            int           i;

            /* No problem until the encoding step finds one. */
            sProblem = "";

            /* The box's lines.  Its own breaks are CR LF, but text pasted in   */
            /* can carry a bare line feed, so all three endings are split on;   */
            /* the endings found in the box are of no use (they are the         */
            /* box's, not the file's) and are ignored.                          */
            lstIgnored = new List<int>();
            Txe_SplitLines(Txe_txtBody.Text, lstNewLines, lstIgnored);

            /* How many lines the file had as loaded (or last saved), and   */
            /* how many the box has now.                                    */
            iOld = Txe_lstLines.Count;
            iNew = lstNewLines.Count;

            /* Which lines are untouched is found by matching the box's       */
            /* lines against the old ones from BOTH ENDS: the run of          */
            /* identical lines at the top and the run at the bottom keep      */
            /* their endings, and whatever lies between them is what the      */
            /* user changed.  So a file whose endings are all alike comes     */
            /* back all alike, and one with mixed endings keeps them in the   */
            /* two untouched runs, the lines between taking the common one.   */
            iTop = 0;

            while (iTop < iOld && iTop < iNew && Txe_lstLines[iTop] == lstNewLines[iTop])
            {   /* One identical line per iteration. */
                iTop++;
            }

            /* ... then the one at the bottom, not reaching into the top run   */
            /* in either list (so a line is never counted in both).            */
            iBottom = 0;

            while (iBottom < iOld - iTop && iBottom < iNew - iTop &&
                   Txe_lstLines[iOld - 1 - iBottom] == lstNewLines[iNew - 1 - iBottom])
            {   /* One identical line per iteration. */
                iBottom++;
            }

            /* The endings, one per line of the box, built afresh. */
            lstNewEols.Clear();

            for (i = 0; i < iNew; i++)
            {   /* One line of the box per iteration: its ending. */
                if (i < iTop)
                {   /* In the untouched top run: the ending it arrived with. */
                    lstNewEols.Add(Txe_lstEols[i]);
                }
                else if (i >= iNew - iBottom)
                {   /* In the untouched bottom run: likewise, the old line at    */
                    /* the same distance from the end (iNew - i lines from the   */
                    /* end in both lists).                                       */
                    lstNewEols.Add(Txe_lstEols[iOld - (iNew - i)]);
                }
                else
                {   /* New or changed: the file's commonest ending. */
                    lstNewEols.Add(Txe_iMainEol);
                }

                /* Then the rule that overrides the matching: the last line   */
                /* never has an ending and every other line always has one.   */
                /* That is what makes "the file ends with a line ending"      */
                /* follow the text -- it does exactly when the box's last     */
                /* line is empty.                                             */
                if (i == iNew - 1)
                {   /* The last line has no ending. */
                    lstNewEols[i] = TXE_EOL_NONE;
                }
                else if (lstNewEols[i] == TXE_EOL_NONE)
                {   /* What was the file's last line now has lines after it:   */
                    /* it gets the commonest ending, as a new line would.      */
                    lstNewEols[i] = Txe_iMainEol;
                }
            }

            /* The text, each line followed by its ending. */
            sbText = new StringBuilder();

            for (i = 0; i < iNew; i++)
            {   /* One line per iteration, then its ending's characters (none   */
                /* for the last line, whose NONE matches no branch below).      */
                sbText.Append(lstNewLines[i]);

                if (lstNewEols[i] == TXE_EOL_LF)
                {   /* Line feed. */
                    sbText.Append('\n');
                }
                else if (lstNewEols[i] == TXE_EOL_CRLF)
                {   /* Carriage return + line feed. */
                    sbText.Append("\r\n");
                }
                else if (lstNewEols[i] == TXE_EOL_CR)
                {   /* Carriage return alone. */
                    sbText.Append('\r');
                }
            }

            /* The whole file's text, endings included. */
            sText = sbText.ToString();

            /* The bytes, in the encoding the file arrived in. */
            if (Txe_bUtf8)
            {   /* UTF-8.  The encoder adds no byte-order mark (its false):   */
                /* the mark, when the file had one, is put in front below.    */
                /* It is the replacing encoder, not the strict one: a lone    */
                /* surrogate the box may hold becomes a replacement           */
                /* character rather than a failed save (as found).            */
                abText = new UTF8Encoding(false).GetBytes(sText);
            }
            else
            {   /* Latin-1 holds only characters up to 255. */
                for (i = 0; i < sText.Length; i++)
                {   /* One character per iteration, looking for one that will not fit. */
                    if (sText[i] > (char)0xFF)
                    {   /* It would be written as a question mark (the encoder's   */
                        /* stand-in, as found), silently changing the file:        */
                        /* refused instead, naming the character, and nothing      */
                        /* is made.                                                */
                        sProblem = "This file is not UTF-8, so it is edited one byte per character, and the text now holds " +
                                   "a character that does not fit in one byte: \"" + sText[i] + "\". Remove it and save again.";
                        return(null);
                    }
                }

                /* Every character fits: each becomes its one byte. */
                abText = Encoding.GetEncoding(TXE_LATIN1).GetBytes(sText);
            }

            if (!Txe_bBom)
            {   /* No byte-order mark to put back: the text is the file. */
                return(abText);
            }

            /* The byte-order mark the file arrived with, then the text. */
            abFile    = new byte[abText.Length + 3];
            abFile[0] = 0xEF;
            abFile[1] = 0xBB;
            abFile[2] = 0xBF;
            Array.Copy(abText, 0, abFile, 3, abText.Length);

            /* The file's new content. */
            return(abFile);
        }

        /*--------------------------------------------------------------------*/
        /* Txe_WriteStaged:                                                   */
        /*                                                                    */
        /* Write a Windows file whole without ever writing it in place: the   */
        /* new content goes into a temporary file beside it, which then       */
        /* replaces it, so a failure part-way leaves the old file as it was.  */
        /* A failure throws, after the temporary file is removed.  What       */
        /* File.Replace leaves when IT fails is Windows' (documented: the     */
        /* file may be left under either name), so the caller keeps the text  */
        /* unsaved in the window.                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sPath  : the file to write.                                    */
        /*     abFile : its whole new contents.                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the file holds the new contents; a failure throws.      */
        /*--------------------------------------------------------------------*/
        private static void Txe_WriteStaged(string sPath, byte[] abFile)
        {
            string sStage;
            FileStream stmStage;

            /* The temporary file's name: the target's own, with 8 random     */
            /* hex digits and ".tmp" -- beside it, so on the same volume,     */
            /* where a replace is a rename.  CreateNew below refuses a name   */
            /* that is already taken.                                         */
            sStage = sPath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";

            try
            {   /* The write and the replace, one catch for both. */

                /* A NEW file (it never replaces anything), written and   */
                /* flushed to the disk itself before it takes the name.   */
                using (stmStage = new FileStream(sStage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {   /* Write, then flush through to the disk; closed by the using. */
                    stmStage.Write(abFile, 0, abFile.Length);
                    stmStage.Flush(true);
                }

                if (File.Exists(sPath))
                {   /* The file exists: replaced by the temporary one, with no   */
                    /* backup copy (null).  The true lets the replace go on      */
                    /* when the old file's attributes or ACLs cannot be          */
                    /* carried over (documented), so they may be lost.           */
                    File.Replace(sStage, sPath, null, true);
                }
                else
                {   /* A new file: the staged one simply takes the name. */
                    File.Move(sStage, sPath);
                }
            }
            catch
            {   /* The write or the replace failed: the temporary file          */
                /* removed, then the failure thrown on as it was.  The          */
                /* delete is by name: it does not know whether CreateNew        */
                /* made the file -- were one of that random name there          */
                /* already, CreateNew refused it and this deletes it (not       */
                /* guarded against; the chance is about one in four billion).   */
                try
                {   /* The removal, which must not hide the real failure. */
                    if (File.Exists(sStage))
                    {   /* There: deleted. */
                        File.Delete(sStage);
                    }
                }
                catch
                {   /* Not removable: left, and the real failure thrown below. */
                }

                throw;
            }
        }

        /* Saves, and what the window tells the command system. */

        /*------------------------------------------------------------------*/
        /* TXE_ReadLoadError:                                               */
        /*                                                                  */
        /* Why the file could not be loaded, read by the caller before it   */
        /* shows the window (the constructor loads it).                     */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     None.                                                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     string : "" when it loaded; otherwise the reason, and the    */
        /*              window is to be disposed unshown.                   */
        /*------------------------------------------------------------------*/
        internal string TXE_ReadLoadError()
        {
            /* Set by Txe_Load. */
            return(Txe_sLoadError);
        }

        /*------------------------------------------------------------------*/
        /* Txe_ReadCurrentFile:                                             */
        /*                                                                  */
        /* The editor's destination record as it stands -- the one the last */
        /* acknowledged save made current, never a target still being       */
        /* chosen -- for the Save As menu to offer its choices from.        */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     None.                                                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     OFD_clsOpenedFile : the record, or null once the window has  */
        /*                         been disposed.                           */
        /*------------------------------------------------------------------*/
        private OFD_clsOpenedFile Txe_ReadCurrentFile()
        {
            if (IsDisposed)
            {   /* The window is gone: no menu to offer anything. */
                return(null);
            }

            /* The record itself (not a copy). */
            return(Txe_file);
        }

        /*------------------------------------------------------------------*/
        /* TXE_BindCommandFacts:                                            */
        /*                                                                  */
        /* Give the window the call it reports its returns through, when    */
        /* the editors' coordinator registers it                            */
        /* (CommandEditorCoordinator.cs).                                   */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     pfnPost : the call, the coordinator's; it does no engine     */
        /*               work.                                              */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : kept; Txe_PostFact uses it from now on.               */
        /*------------------------------------------------------------------*/
        internal void TXE_BindCommandFacts(EUI_PostFact pfnPost)
        {
            /* Kept for Txe_PostFact. */
            Txe_pfnPostFact = pfnPost;
        }

        /*------------------------------------------------------------------*/
        /* TXE_HasLocalWork:                                                */
        /*                                                                  */
        /* Whether the window is still inside work of its own -- a save's   */
        /* compose or write, its own close, or its own prompt (Find) or     */
        /* close question still up or asked for -- which can have a message */
        /* box or a prompt open: the coordinator then holds back a close    */
        /* the command system asks for until it has returned, and waits for */
        /* the close question's answer rather than take the window for      */
        /* declining.                                                       */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     None.                                                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     bool : true while either count is not 0, or the prompt or    */
        /*            the close question is asked for or up.                */
        /*------------------------------------------------------------------*/
        internal bool TXE_HasLocalWork()
        {
            /* The local-work count, the closing count, the prompt, or the   */
            /* close question, asked for or up.                              */
            return(Txe_iLocalWork != 0 || Txe_iClosing != 0 || Txe_frmPrompt != null ||
                Txe_bCloseAsked || Txe_objCloseQuestion != null);
        }

        /*------------------------------------------------------------------*/
        /* Txe_PostFact:                                                    */
        /*                                                                  */
        /* Report through the coordinator's call that the window's own work */
        /* (or a deferred close) has returned, naming the window by its     */
        /* editor id.                                                       */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     lCloseOrder : the deferred close's save order, or            */
        /*                   EUI_LOCAL_RETURN for the window's own work.    */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : reported, when a call was given.                      */
        /*------------------------------------------------------------------*/
        private void Txe_PostFact(long lCloseOrder)
        {
            if (Txe_pfnPostFact != null)
            {   /* Registered: reported.  (Unregistered, nothing waits on it.) */
                Txe_pfnPostFact(Txe_file.OFD_lEditorId, lCloseOrder);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Txe_Save:                                                          */
        /*                                                                    */
        /* File > Save: save to the editor's own destination.  When that is   */
        /* an image entry whose image is no longer open, it is refused (it    */
        /* cannot be written); Save As can then choose a Windows file, and    */
        /* Save's own destination changes only once that save is              */
        /* acknowledged.                                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when a save was started; false when it was         */
        /*            refused or failed before it could start.                */
        /*--------------------------------------------------------------------*/
        private bool Txe_Save()
        {
            /* Save to the editor's own destination record, as it stands:   */
            /* it changes only when a save to another place is              */
            /* acknowledged, never here.                                    */
            return(Txe_SaveTo(Txe_file, false));
        }

        /*--------------------------------------------------------------------*/
        /* Txe_SaveTo:                                                        */
        /*                                                                    */
        /* Capture one save of the text as it is now and send it on: into     */
        /* the image as a staged file for the command worker, or to a         */
        /* Windows file written here (Txe_WriteStaged).  The editor's         */
        /* destination record and its "as saved" lines change only once       */
        /* the save is acknowledged (TXE_SaveAcknowledged); whether an        */
        /* image save may run is the coordinator's and the worker's to        */
        /* check, not this function's.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objTarget       : where to save: the editor's own record or    */
        /*                       a target just chosen.                        */
        /*     bCloseWhenSaved : true when a close asked for this save: the   */
        /*                       window closes once it is acknowledged.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the save was sent on (a Windows one may be    */
        /*            acknowledged already); false when it was refused or     */
        /*            failed before that.                                     */
        /*--------------------------------------------------------------------*/
        private bool Txe_SaveTo(OFD_clsOpenedFile objTarget, bool bCloseWhenSaved)
        {
            OFD_clsOpenedFile objSnapshot;
            TSC_clsTextSaveCapture objCapture;
            STG_clsStage objUnsent;
            List<string> lstLines;
            List<int> lstEols;
            byte[] abFile;
            string sProblem;
            long lRevision;
            long lOrder;

            if (Txe_bReadOnly || objTarget == null)
            {   /* A read-only text (it held NUL characters, shown as dots --   */
                /* saving would write the dots), or no target: no save.         */
                return(false);
            }

            if (!objTarget.OFD_bHostFile && objTarget.OFD_bDetached)
            {   /* An image entry whose image is no longer open: nowhere to   */
                /* write it.  Said on the status bar, with the way out.       */
                Txe_tslMessage.Text = "The filesystem is unavailable. Use Save As to choose Windows.";
                return(false);
            }

            /* No captured destination yet (set in the try below). */
            objSnapshot = null;

            /* The window's own work begins: counted until the finally, so   */
            /* a message box this save opens (an encoding refusal, a write   */
            /* failure) holds back any close question the coordinator has    */
            /* waiting (TXE_HasLocalWork).  checked: an overflow throws.     */
            Txe_iLocalWork = checked(Txe_iLocalWork + 1);
            try
            {   /* The capture comes first -- the revision, this save's      */
                /* order, the destination copied for it -- before anything   */
                /* that could show a message or answer the save at once.     */
                lRevision = Txe_lRevision;
                lOrder = checked(++Txe_lSaveOrder);
                objSnapshot = objTarget.OFD_ForSave(lRevision);
                objSnapshot.OFD_lSaveOrder = lOrder;

                /* The text composed to the file's bytes, with its lines and   */
                /* their endings (Txe_Compose); the lists become the "as       */
                /* saved" lines only when this save is acknowledged.           */
                lstLines = new List<string>();
                lstEols = new List<int>();
                abFile = Txe_Compose(lstLines, lstEols, out sProblem);
                if (abFile == null)
                {   /* The text cannot be written in the file's encoding: no   */
                    /* save, the text left as it is, and the reason shown.     */
                    MSG_clsMessageForm.MSG_Show(this, Txe_fntDialog, sProblem,
                        "Save " + objSnapshot.OFD_sName, MessageBoxButtons.OK,
                        MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
                    return(false);
                }

                /* The capture: everything this save is, in one record   */
                /* (TSC_clsTextSaveCapture), the lists and bytes by      */
                /* reference -- nothing changes them after this.         */
                objCapture = new TSC_clsTextSaveCapture(this, objSnapshot,
                    lstLines, lstEols, abFile, lRevision, lOrder, bCloseWhenSaved);
                try
                {   /* The bytes written, by the destination's side.  A failure    */
                    /* in either branch has cleaned up after itself before it      */
                    /* reaches the catch below: Txe_WriteStaged removes its        */
                    /* temporary file, and STG_WriteSnapshot deletes its partial   */
                    /* stage and throws before OFD_objStage is set.                */
                    if (objSnapshot.OFD_bHostFile)
                    {   /* A Windows file (the one opened, or a Save As       */
                        /* target -- OFD_sTempFile is that file): written     */
                        /* here, whole, through a temporary file that         */
                        /* replaces it, so a failure leaves the old file as   */
                        /* it was.  What is handed over below is then only    */
                        /* the fact.                                          */
                        Txe_WriteStaged(objSnapshot.OFD_sTempFile, abFile);
                    }
                    else
                    {   /* An image entry: the bytes into a new staged file of     */
                        /* this save's own, in the stage folder the registration   */
                        /* set (CommandStage.cs), for the worker to put in the     */
                        /* image -- never the read-out copy, which another save    */
                        /* may be using.  The stage writes through                 */
                        /* TSC_WriteStage, so only the captured bytes go in.       */
                        objSnapshot.OFD_objStage = STG_clsStage.STG_WriteSnapshot(
                            objSnapshot.OFD_sStageRoot, objCapture.TSC_WriteStage);
                    }
                }
                catch (Exception exWrite)
                {   /* Not written: said, with the reason, and nothing handed   */
                    /* over -- the text stays as it is and unsaved, and Save    */
                    /* still writes where it did.                               */
                    MSG_clsMessageForm.MSG_Show(this, Txe_fntDialog,
                        "The text could not be written for\n" + objSnapshot.OFD_Title() +
                        "\n\n" + exWrite.Message, "Save " + objSnapshot.OFD_sName,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
                    return(false);
                }

                /* The hand-over: counted as waiting BEFORE it, since a     */
                /* save can be answered inside the call -- an ordinary      */
                /* Windows save always is, and so is a refusal (the         */
                /* coordinator's EUI_Save).  An image save's answer comes   */
                /* with the worker's result.  Whether it may write to the   */
                /* image is the worker's to decide, not this window's.      */
                Txe_iPendingSaves = checked(Txe_iPendingSaves + 1);
                Txe_tslMessage.Text = "Saving ...";
                Txe_pfnSave(objSnapshot, objCapture.TSC_OnSaved);

                /* Handed over; the answer comes through the capture's   */
                /* TSC_OnSaved, and may already have come.               */
                return(true);
            }
            finally
            {   /* Every way out of the save's own work, the refusals and     */
                /* failures above included.                                   */
                /*                                                            */
                /* A stage still named here was not taken.  The coordinator   */
                /* clears the field whenever it takes one, refusing or not    */
                /* (EUI_Save); the main window, when it has no coordinator,   */
                /* deletes the stage without clearing it (MainForm.cs         */
                /* Main_SaveOpenedFile), and the release below then does      */
                /* nothing, as a second STG_Release does.  Otherwise it is    */
                /* reached only when something between the write and the      */
                /* hand-over threw (the checked count, for one), and the      */
                /* stage's file is deleted here.                              */
                if (objSnapshot != null && objSnapshot.OFD_objStage != null)
                {   /* Not taken: no longer named, then deleted. */
                    objUnsent = objSnapshot.OFD_objStage;
                    objSnapshot.OFD_objStage = null;
                    try
                    {   /* The stage's file removed (STG_Release). */
                        objUnsent.STG_Release();
                    }
                    catch (Exception exCleanup)
                    {   /* A file that will not go is logged, and does not   */
                        /* replace the save's own outcome.                   */
                        if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                        {   /* Only while the debug log is kept: otherwise the line is never built. */
                            DBGLOG_clsDebugLog.DBGLOG_Write("text snapshot cleanup", exCleanup.ToString());
                        }
                    }
                }

                /* The window's own work has returned: counted down, and the   */
                /* coordinator told, so a close question it held back can      */
                /* go on.                                                      */
                Txe_iLocalWork--;
                Txe_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);

                /* Any close a save asked for, posted now if nothing else of   */
                /* the window's is still running -- a Windows save answered    */
                /* inside the hand-over above left its close waiting here.     */
                Txe_PostReadyCloses();
            }
        }

        /*------------------------------------------------------------------*/
        /* TXE_SaveAcknowledged:                                            */
        /*                                                                  */
        /* One save's answer (TSC_OnSaved).  A save that succeeded becomes  */
        /* where Save writes next, and its lines the text "as saved" --     */
        /* unless a newer save has already been applied, which an older     */
        /* answer must not undo.  Text typed since the save began is kept,  */
        /* and still counts as unsaved.  Where the file was opened from is  */
        /* not touched (OFD_objOriginal).                                   */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objCapture : the save, as captured.                          */
        /*     bSaved     : true when it succeeded.                         */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the save no longer waits; a success is applied.       */
        /*------------------------------------------------------------------*/
        internal void TXE_SaveAcknowledged(TSC_clsTextSaveCapture objCapture, bool bSaved)
        {
            /* No longer waiting: every save handed over is answered     */
            /* exactly once (EUI_Save), so the count comes down first,   */
            /* whatever follows.                                         */
            Txe_iPendingSaves--;
            if (IsDisposed)
            {   /* The window is gone (disposed before the answer came):   */
                /* nothing left to update.                                 */
                return;
            }

            if (objCapture.TSC_lSaveOrder < Txe_lAcknowledgedOrder)
            {   /* An older save, answered after a newer one was applied:   */
                /* where Save writes and the "as saved" lines stay the      */
                /* newer one's.                                             */
                if (bSaved && objCapture.TSC_bCloseWhenSaved)
                {   /* It asked to close the window: its close is still       */
                    /* decided and reported with its number, which a close    */
                    /* question may be waiting for -- though the newer save   */
                    /* keeps the window open (TXE_CompleteDeferredClose).     */
                    Txe_lstCloseEvaluations.Add(objCapture);
                    Txe_PostReadyCloses();
                }

                /* Nothing else of it is applied. */
                return;
            }

            /* The window brought forward, for a failure as for a success:   */
            /* a question or report the save showed (the worker's, or the    */
            /* coordinator's) may have been in front of it.                  */
            Activate();
            if (!bSaved)
            {   /* Failed: where Save writes and the text stay as they were,   */
                /* still unsaved.  How it failed is shown by whoever           */
                /* reported it (the coordinator's report,                      */
                /* CommandEditorCoordinator.cs); the status bar only says      */
                /* that it did.                                                */
                Txe_tslMessage.Text = "Not saved.";
                return;
            }

            /* A success.  Save writes to this save's own record from now    */
            /* on, expecting its own path next time (a Save As's record      */
            /* still names the entry saved to before); its lines are the     */
            /* "as saved" lines, kept by reference, not copied; its number   */
            /* the one an older answer must beat; and the text is dirty      */
            /* only if it changed since this save captured it.  Where the    */
            /* file was opened from is the shared OFD_objOriginal,           */
            /* untouched.                                                    */
            objCapture.TSC_objSnapshot.OFD_sExpectedEntry = objCapture.TSC_objSnapshot.OFD_sImagePath;
            Txe_file = objCapture.TSC_objSnapshot;
            Txe_lstLines = objCapture.TSC_lstLines;
            Txe_lstEols = objCapture.TSC_lstEols;
            Txe_lAcknowledgedOrder = objCapture.TSC_lSaveOrder;
            Txe_bDirty = Txe_lRevision != objCapture.TSC_lRevision;

            /* The status bar's time; the endings recounted from the saved   */
            /* lines (a save can make mixed endings uniform); the caption,   */
            /* which names the destination and loses its asterisk when       */
            /* nothing was typed since.                                      */
            Txe_tslMessage.Text = "Saved at " + DateTime.Now.ToString("HH:mm:ss") + ".";
            Txe_ShowFileFacts();
            Txe_UpdateTitle();
            if (objCapture.TSC_bCloseWhenSaved)
            {   /* It asked to close the window: kept for its close to be     */
                /* decided on a later turn -- always, since the decision is   */
                /* reported even when newer typing keeps the window open --   */
                /* and posted once the window's own work has returned.        */
                Txe_lstCloseEvaluations.Add(objCapture);
                Txe_PostReadyCloses();
            }
        }

        /*------------------------------------------------------------------*/
        /* Txe_PostReadyCloses:                                             */
        /*                                                                  */
        /* Post the closes that successful saves asked for -- but only once */
        /* the window's own work and its closing handler have returned.  A  */
        /* Windows save is answered at once, inside the save's call, and    */
        /* possibly while FormClosing is still running (its Yes saves); a   */
        /* Close from in there would nest one close in another, so the post */
        /* lets that stack unwind first.                                    */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     None.                                                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : each waiting close posted once, or left waiting.      */
        /*------------------------------------------------------------------*/
        private void Txe_PostReadyCloses()
        {
            TSC_clsTextSaveCapture objCapture;

            if (Txe_iLocalWork != 0 || Txe_iClosing != 0)
            {   /* Still inside the window's own work or its closing        */
                /* handler: left waiting; their finally calls this again.   */
                return;
            }

            while (Txe_lstCloseEvaluations.Count != 0)
            {   /* One waiting close per iteration, taken off the list        */
                /* BEFORE it is posted so it is posted once; it reports its   */
                /* save's number even if newer typing keeps the window open   */
                /* (TSC_RunClose, TXE_CompleteDeferredClose).                 */
                objCapture = Txe_lstCloseEvaluations[0];
                Txe_lstCloseEvaluations.RemoveAt(0);
                BeginInvoke(new Action(objCapture.TSC_RunClose));
            }
        }

        /*------------------------------------------------------------------*/
        /* TXE_CompleteDeferredClose:                                       */
        /*                                                                  */
        /* The close a successful save asked for, now that the stack it was */
        /* asked from has unwound (TSC_RunClose, posted): the window closes */
        /* only if nothing has changed since -- no typing, no other save    */
        /* waiting or applied, no work of its own running.  Either way the  */
        /* save's number is reported to the coordinator, whose close        */
        /* question may wait for it (EDP_lCloseReturnOrder).                */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objCapture : the save, as captured.                          */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the window closed, or kept; the report made.          */
        /*------------------------------------------------------------------*/
        internal void TXE_CompleteDeferredClose(TSC_clsTextSaveCapture objCapture)
        {
            try
            {   /* The decision, on a later turn of the UI thread, never   */
                /* inside the FormClosing that asked for it.               */
                if (!IsDisposed && !Txe_bDirty && Txe_iPendingSaves == 0 &&
                    Txe_iLocalWork == 0 && Txe_iClosing == 0 && Txe_lRevision == objCapture.TSC_lRevision &&
                    Txe_lAcknowledgedOrder == objCapture.TSC_lSaveOrder)
                {   /* Still exactly this save's text, nothing waiting:   */
                    /* closed.  FormClosing then finds nothing unsaved.   */
                    Close();
                }
            }
            finally
            {   /* Reported whether or not it closed: a window kept open   */
                /* makes the coordinator ask the worker to look again      */
                /* (Eui_OnEditorFact).                                     */
                Txe_PostFact(objCapture.TSC_lSaveOrder);
            }
        }

        /*------------------------------------------------------------------*/
        /* TXE_ApplyTargetChoice:                                           */
        /*                                                                  */
        /* A Save As's destination has arrived (TPI_OnChosen): save to it   */
        /* -- but only if the window still saves where it did when the Save */
        /* As was asked.  A closed window, or a window a save has moved     */
        /* since, drops it, silently; the text is not touched.  A cancelled */
        /* or refused picker drops it too -- and when that Save As was      */
        /* chosen from the window's own close question, the close is asked  */
        /* again, so the question shows once more and its own Cancel is     */
        /* what keeps the window.                                           */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objIntent : the Save As, as asked.                           */
        /*     objTarget : the destination; null when the picker was        */
        /*                 cancelled or refused.                            */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the save started, the close asked again, or the       */
        /*            answer dropped.                                       */
        /*------------------------------------------------------------------*/
        internal void TXE_ApplyTargetChoice(TPI_clsTextPickIntent objIntent, OFD_clsOpenedFile objTarget)
        {
            if (objTarget == null && objIntent.TPI_bCloseWhenSaved && !IsDisposed &&
                Txe_file == objIntent.TPI_objBinding)
            {   /* No destination for a Save As the user's own close asked    */
                /* for: the close posted to the window's queue, never made    */
                /* from here -- this answer can arrive inside a picker's      */
                /* call -- so the close question shows again once the stack   */
                /* has unwound.  (Under the worker's close question the       */
                /* worker asks again itself; that intent does not close on    */
                /* save, so it never reaches here.)                           */
                Txe_objQueue.WQU_Post((int)Txe_enmItem.CloseAgain, null);
                return;
            }

            if (objTarget == null || IsDisposed || Txe_bReadOnly ||
                Txe_file != objIntent.TPI_objBinding)
            {   /* No destination, no window, or a window moved since: the   */
                /* answer belongs to a state that is gone.  The read-only    */
                /* test is a guard: such a text has Save As switched off     */
                /* and no recovery question (Txe_Load, Txe_PresentClose).    */
                return;
            }

            /* The text captured NOW, when the destination is known -- not   */
            /* when the Save As was asked.  The menu's Save As leaves the    */
            /* window open; the recovery question's, when it was the         */
            /* window's own close, asks to close it once the save succeeds   */
            /* (TPI_bCloseWhenSaved).                                        */
            Txe_SaveTo(objTarget, objIntent.TPI_bCloseWhenSaved);
        }

        /*------------------------------------------------------------------*/
        /* TXE_SetSourceUnavailable:                                        */
        /*                                                                  */
        /* The coordinator's word that the entry's image has gone or come   */
        /* back (CommandEditorCoordinator.cs, Eui_SetUnavailable): marked   */
        /* on the window's record, so Save refuses the image and the close  */
        /* offers the recovery question instead.  It decides nothing about  */
        /* what the worker may do.                                          */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     bUnavailable : true when the image is not open.              */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : OFD_bDetached set; never for a Windows file.          */
        /*------------------------------------------------------------------*/
        internal void TXE_SetSourceUnavailable(bool bUnavailable)
        {
            /* On the window's own record only (a save already on its way   */
            /* has its own copy); it only sets the flag, so it is safe      */
            /* inside the coordinator's walk of its editors.                */
            Txe_file.OFD_bDetached = !Txe_file.OFD_bHostFile && bUnavailable;
        }

        /*--------------------------------------------------------------------*/
        /* Txe_UpdateTitle:                                                   */
        /*                                                                    */
        /* Set the window's caption: an asterisk while the text has unsaved   */
        /* changes, then the file -- the entry's path and the image it is in, */
        /* or the Windows path of a file opened directly.                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the caption is set.                                     */
        /*--------------------------------------------------------------------*/
        private void Txe_UpdateTitle()
        {
            string sTitle;

            /* The file as the viewers name it (OpenScan.cs's OFD_Title: the   */
            /* entry's path and its image, or the Windows path), then the      */
            /* program's name for this window.                                 */
            sTitle = Txe_file.OFD_Title() + " - BifrostFM Text";

            if (Txe_bDirty)
            {   /* Unsaved changes: an asterisk in front, as most editors show it. */
                sTitle = "* " + sTitle;
            }

            /* Onto the window. */
            this.Text = sTitle;
        }

        /*--------------------------------------------------------------------*/
        /* Txe_UpdateCaretStatus:                                             */
        /*                                                                    */
        /* Show the caret's line and column in the status bar, both counted   */
        /* from 1.  The text box is asked, so with word wrap on the line is   */
        /* the line as displayed, not the line of the file.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the status label is set.                                */
        /*--------------------------------------------------------------------*/
        private void Txe_UpdateCaretStatus()
        {
            int iCaret;
            int iLine;

            /* The caret is the selection's start (with nothing selected,   */
            /* where typing goes); the box says which line holds it.        */
            iCaret = Txe_txtBody.SelectionStart;
            iLine  = Txe_txtBody.GetLineFromCharIndex(iCaret);

            /* The column is its distance from that line's first character;   */
            /* both counted from 1 for the reader.                            */
            Txe_tslCaret.Text = "Ln " + (iLine + 1) + ", Col " + (iCaret - Txe_txtBody.GetFirstCharIndexFromLine(iLine) + 1);
        }

        /*--------------------------------------------------------------------*/
        /* Txe_FindNext:                                                      */
        /*                                                                    */
        /* Select the next place the search text occurs, looking from the end */
        /* of the present selection and carrying on from the top when the     */
        /* bottom is reached.  Letter case is ignored.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the occurrence is selected and scrolled into view, or   */
        /*            the status bar says there is none.                      */
        /*--------------------------------------------------------------------*/
        private void Txe_FindNext()
        {
            string sBody;
            int    iFrom;
            int    iFound;

            if (Txe_sFind.Length == 0)
            {   /* Nothing has been searched for yet: nothing to find (the   */
                /* callers ask first, so this is a guard).                   */
                return;
            }

            /* The search starts just past the present selection -- after     */
            /* the last occurrence found, so repeating moves on to the next   */
            /* -- and nothing is found yet.                                   */
            sBody  = Txe_txtBody.Text;
            iFrom  = Txe_txtBody.SelectionStart + Txe_txtBody.SelectionLength;
            iFound = -1;

            if (iFrom <= sBody.Length)
            {   /* From the caret to the end, letter case ignored (ordinal,   */
                /* so no locale's case rules apply).  The test is a guard:    */
                /* a selection never ends past the text, so it always         */
                /* passes, and IndexOf accepts a start at the very end.       */
                iFound = sBody.IndexOf(Txe_sFind, iFrom, StringComparison.OrdinalIgnoreCase);
            }

            if (iFound < 0)
            {   /* Not below the caret: from the top. */
                iFound = sBody.IndexOf(Txe_sFind, StringComparison.OrdinalIgnoreCase);
            }

            if (iFound < 0)
            {   /* Nowhere: said on the status bar, the selection unmoved. */
                Txe_tslMessage.Text = "\"" + Txe_sFind + "\" was not found.";
                return;
            }

            /* Found: selected, scrolled into view, the status message        */
            /* cleared of any earlier "not found", the caret's place shown.   */
            Txe_txtBody.Select(iFound, Txe_sFind.Length);
            Txe_txtBody.ScrollToCaret();
            Txe_tslMessage.Text = "";
            Txe_UpdateCaretStatus();
        }

        /*--------------------------------------------------------------------*/
        /* Txe_OnSaveClicked:                                                 */
        /*                                                                    */
        /* File > Save (Ctrl+S): the Save item posted to the window's queue,  */
        /* which saves where Save writes now when it runs it (Txe_RunSave).   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item posted (or dropped as one already in hand).    */
        /*--------------------------------------------------------------------*/
        private void Txe_OnSaveClicked(object objSender, EventArgs evArgs)
        {
            /* Posted, as Find and Save As are: the composing, the write,   */
            /* any message box and the hand-over to the main window all     */
            /* run from the queue, never inside the menu's click.           */
            Txe_objQueue.WQU_Post((int)Txe_enmItem.Save, null);
        }

        /*--------------------------------------------------------------------*/
        /* Txe_RunSave:                                                       */
        /*                                                                    */
        /* The Save item, run from the window's queue: save where Save writes */
        /* now (Txe_file), if a save may start at all.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the save started, or nothing done; the text is kept     */
        /*            either way.                                             */
        /*--------------------------------------------------------------------*/
        private void Txe_RunSave()
        {
            if (!Txe_file.OFD_CanRequestUserFile())
            {   /* Not now -- the worker is busy, exiting or holding a         */
                /* picker (OFD_CanRequestUserFile), judged as the item runs:   */
                /* nothing is done, as a pane's own command would not start    */
                /* either.                                                     */
                return;
            }

            /* The save; whether one started is not needed here -- a   */
            /* refusal has said why itself.                            */
            Txe_Save();
        }

        /*--------------------------------------------------------------------*/
        /* Txe_OnSaveAsImageClicked:                                          */
        /*                                                                    */
        /* File > Save As > Image...: an image destination asked for          */
        /* (Txe_RequestSaveAs).                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the request passed on, or nothing done; nothing is      */
        /*            saved or moved until a destination arrives.             */
        /*--------------------------------------------------------------------*/
        private void Txe_OnSaveAsImageClicked(object objSender, EventArgs evArgs)
        {
            /* Posted to the window's own queue, which asks when it runs   */
            /* the item (Txe_RunItem); a second click while it waits or    */
            /* runs is dropped there.                                      */
            Txe_objQueue.WQU_Post((int)Txe_enmItem.SaveAsImage, null);
        }

        /*--------------------------------------------------------------------*/
        /* Txe_OnSaveAsWindowsClicked:                                        */
        /*                                                                    */
        /* File > Save As > Windows...: a Windows destination asked for       */
        /* (Txe_RequestSaveAs).                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the request passed on, or nothing done; nothing is      */
        /*            saved or moved until a destination arrives.             */
        /*--------------------------------------------------------------------*/
        private void Txe_OnSaveAsWindowsClicked(object objSender, EventArgs evArgs)
        {
            /* Posted to the window's own queue, as the Image choice is. */
            Txe_objQueue.WQU_Post((int)Txe_enmItem.SaveAsWindows, null);
        }

        /*------------------------------------------------------------------*/
        /* Txe_RequestSaveAs:                                               */
        /*                                                                  */
        /* Save As: ask for a destination (EditorSaveMenu.cs SVM_Request,   */
        /* which passes it to the editor's target picker); the save is made */
        /* only when the destination arrives (TXE_ApplyTargetChoice).  The  */
        /* window's queue runs it for the two Save As items, which say      */
        /* which side (Txe_RunItem).                                        */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     bWindows : true for a Windows file, false for a place in the */
        /*                image.                                            */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the request passed on, or nothing done.               */
        /*------------------------------------------------------------------*/
        private void Txe_RequestSaveAs(bool bWindows)
        {
            TPI_clsTextPickIntent objIntent;

            if (!Txe_file.OFD_CanRequestUserFile())
            {   /* Not now: as for Save, the click does nothing -- before   */
                /* any name is asked for or anything written.               */
                return;
            }

            if (Txe_bReadOnly)
            {   /* A read-only text shows its NULs as dots, and those must   */
                /* never be saved.  A guard: such a text has Save As         */
                /* switched off (Txe_Load), and a disabled item takes no     */
                /* shortcut either (as found).                               */
                return;
            }

            /* The Save As's record -- where Save writes now, which must      */
            /* still be it when the answer comes, and no close to follow --   */
            /* and the request, answered through its TPI_OnChosen.  Nothing   */
            /* is captured or counted as waiting while the destination is     */
            /* chosen, so a close the worker asks for meanwhile is not held   */
            /* up by it.                                                      */
            objIntent = new TPI_clsTextPickIntent(this, Txe_file, false);
            SVM_clsSaveMenu.SVM_Request(this, Txe_file, bWindows, objIntent.TPI_OnChosen);
        }

        /*--------------------------------------------------------------------*/
        /* Txe_OnCloseClicked:                                                */
        /*                                                                    */
        /* File > Close (Ctrl+W): the Close item posted; the window's queue   */
        /* closes the window when it runs it, Txe_OnFormClosing asking about  */
        /* unsaved changes.                                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item posted (or dropped as one already in hand).    */
        /*--------------------------------------------------------------------*/
        private void Txe_OnCloseClicked(object objSender, EventArgs evArgs)
        {
            /* Posted: the close runs from the queue (Txe_RunItem). */
            Txe_objQueue.WQU_Post((int)Txe_enmItem.Close, null);
        }

        /*--------------------------------------------------------------------*/
        /* Txe_OnUndoClicked:                                                 */
        /*                                                                    */
        /* Edit > Undo: the Undo item posted; the queue runs the text box's   */
        /* own undo (a single level, as found).                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item posted (or dropped as one already in hand).    */
        /*--------------------------------------------------------------------*/
        private void Txe_OnUndoClicked(object objSender, EventArgs evArgs)
        {
            /* Posted: the undo runs from the queue (Txe_RunItem). */
            Txe_objQueue.WQU_Post((int)Txe_enmItem.Undo, null);
        }

        /*--------------------------------------------------------------------*/
        /* Txe_OnCutClicked:                                                  */
        /*                                                                    */
        /* Edit > Cut: the Cut item posted; the queue moves the selection to  */
        /* the clipboard and out of the text.                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item posted (or dropped as one already in hand).    */
        /*--------------------------------------------------------------------*/
        private void Txe_OnCutClicked(object objSender, EventArgs evArgs)
        {
            /* Posted: the cut runs from the queue (Txe_RunItem). */
            Txe_objQueue.WQU_Post((int)Txe_enmItem.Cut, null);
        }

        /*--------------------------------------------------------------------*/
        /* Txe_OnCopyClicked:                                                 */
        /*                                                                    */
        /* Edit > Copy: the Copy item posted; the queue puts the selection on */
        /* the clipboard.                                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item posted (or dropped as one already in hand).    */
        /*--------------------------------------------------------------------*/
        private void Txe_OnCopyClicked(object objSender, EventArgs evArgs)
        {
            /* Posted: the copy runs from the queue (Txe_RunItem). */
            Txe_objQueue.WQU_Post((int)Txe_enmItem.Copy, null);
        }

        /*--------------------------------------------------------------------*/
        /* Txe_OnPasteClicked:                                                */
        /*                                                                    */
        /* Edit > Paste: the Paste item posted; the queue puts the            */
        /* clipboard's text in place of the selection.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item posted (or dropped as one already in hand).    */
        /*--------------------------------------------------------------------*/
        private void Txe_OnPasteClicked(object objSender, EventArgs evArgs)
        {
            /* Posted: the paste runs from the queue (Txe_RunItem). */
            Txe_objQueue.WQU_Post((int)Txe_enmItem.Paste, null);
        }

        /*--------------------------------------------------------------------*/
        /* Txe_OnSelectAllClicked:                                            */
        /*                                                                    */
        /* Edit > Select All: the SelectAll item posted; the queue selects    */
        /* the whole text.                                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item posted (or dropped as one already in hand).    */
        /*--------------------------------------------------------------------*/
        private void Txe_OnSelectAllClicked(object objSender, EventArgs evArgs)
        {
            /* Posted: the selection is made from the queue (Txe_RunItem). */
            Txe_objQueue.WQU_Post((int)Txe_enmItem.SelectAll, null);
        }

        /*--------------------------------------------------------------------*/
        /* Txe_OnFindClicked:                                                 */
        /*                                                                    */
        /* Edit > Find (Ctrl+F): the Find item posted to the window's queue,  */
        /* which asks what to look for and goes to it (Txe_AskFind).          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item posted (or dropped as one already in hand).    */
        /*--------------------------------------------------------------------*/
        private void Txe_OnFindClicked(object objSender, EventArgs evArgs)
        {
            /* Posted to the window's own queue, which shows the prompt and   */
            /* searches when it runs the item (Txe_RunItem).                  */
            Txe_objQueue.WQU_Post((int)Txe_enmItem.Find, null);
        }

        /*--------------------------------------------------------------------*/
        /* Txe_AskFind:                                                       */
        /*                                                                    */
        /* Ask what to look for -- offering the selected text when it lies    */
        /* within one line.  The prompt is EDITOR-MODAL: shown modeless over  */
        /* this window, which is disabled while it is up, so the main window  */
        /* and every other editor carry on; its answer comes back through     */
        /* the window's queue (Txe_OnFindPromptClosed, Txe_ApplyFindAnswer),  */
        /* which searches.  The window's queue runs this for Edit > Find's    */
        /* item (Txe_RunItem), and for Find Next's when nothing has been      */
        /* searched for yet (Txe_FindNextOrAsk).                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the prompt shown, or nothing when one is already up.    */
        /*--------------------------------------------------------------------*/
        private void Txe_AskFind()
        {
            string sStart;

            if (Txe_frmPrompt != null)
            {   /* A guard: the window is disabled while its prompt is up, so   */
                /* no second Find can be asked for -- nothing more is shown.    */
                return;
            }

            /* What the prompt opens with: the last search, unless ... */
            sStart = Txe_sFind;

            if (Txe_txtBody.SelectionLength != 0 && Txe_txtBody.SelectedText.IndexOf('\r') < 0)
            {   /* ... a selection within one line (no CR: the box breaks     */
                /* lines with CR LF), which is offered as what to find -- a   */
                /* many-line selection would not fit the one-line prompt.     */
                sStart = Txe_txtBody.SelectedText;
            }

            /* The prompt, under this window's (the program's) icon, kept as   */
            /* the window's own work until its answer is applied -- so a       */
            /* close the worker asks for waits for it (TXE_HasLocalWork) and   */
            /* the window's own close keeps it open (Txe_PresentClose).  Its   */
            /* closing only posts (Txe_OnFindPromptClosed).                    */
            Txe_frmPrompt = new PRM_clsPromptForm(Txe_fntDialog, "Find", "Text to find:", sStart);
            Txe_frmPrompt.Icon = this.Icon;
            Txe_frmPrompt.FormClosed += Txe_OnFindPromptClosed;

            /* Placed over the middle of this window (a modeless window is       */
            /* not centred by the framework), this window disabled, then the     */
            /* prompt shown modeless and owned by it -- so it stays above this   */
            /* window, and it alone of the program is held up.                   */
            PRM_clsPromptForm.PRM_CentreOver(Txe_frmPrompt, this);
            this.Enabled = false;
            Txe_frmPrompt.Show(this);
        }

        /*--------------------------------------------------------------------*/
        /* Txe_OnFindPromptClosed:                                            */
        /*                                                                    */
        /* The Find prompt has closed (OK, Cancel, Escape or its close box):  */
        /* its answer posted to the window's queue, which applies it          */
        /* (Txe_ApplyFindAnswer).  Nothing else is done here.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the prompt.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the answer posted.                                      */
        /*--------------------------------------------------------------------*/
        private void Txe_OnFindPromptClosed(object objSender, FormClosedEventArgs evArgs)
        {
            /* Posted with the prompt it came from. */
            Txe_objQueue.WQU_Post((int)Txe_enmItem.FindAnswer, objSender);
        }

        /*--------------------------------------------------------------------*/
        /* Txe_ApplyFindAnswer:                                               */
        /*                                                                    */
        /* The Find prompt's answer, run by the window's queue: the window    */
        /* enabled again and the prompt released, then -- when a text was     */
        /* given -- kept for Find Next and its next occurrence selected.  A   */
        /* close that waited for the prompt is then told it may go on.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmPrompt : the prompt that closed.                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the search made, or nothing for a cancel.               */
        /*--------------------------------------------------------------------*/
        private void Txe_ApplyFindAnswer(PRM_clsPromptForm frmPrompt)
        {
            if (frmPrompt != Txe_frmPrompt)
            {   /* A guard: not the prompt this window has up -- nothing to   */
                /* apply.                                                     */
                return;
            }

            /* The prompt released and the window enabled again, before any   */
            /* search, so the selection it makes is shown in a live window.   */
            Txe_frmPrompt = null;
            frmPrompt.Dispose();
            this.Enabled = true;
            this.Activate();

            if (frmPrompt.PRM_bAccepted && frmPrompt.PRM_sText.Length != 0)
            {   /* A text given: kept, for Find Next too, and its next   */
                /* occurrence from the selection on.                     */
                Txe_sFind = frmPrompt.PRM_sText;
                Txe_FindNext();
            }

            /* The window's own work has returned: a close question that   */
            /* waited for the prompt is looked at again.                   */
            Txe_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
        }

        /*--------------------------------------------------------------------*/
        /* Txe_OnFindNextClicked:                                             */
        /*                                                                    */
        /* Edit > Find Next (F3): the Find Next item posted to the window's   */
        /* queue, which finds the next occurrence of what was last searched   */
        /* for, or asks when nothing has been (Txe_FindNextOrAsk).            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item.                                     */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item posted (or dropped as one already in hand).    */
        /*--------------------------------------------------------------------*/
        private void Txe_OnFindNextClicked(object objSender, EventArgs evArgs)
        {
            /* Posted to the window's own queue (Txe_RunItem runs it). */
            Txe_objQueue.WQU_Post((int)Txe_enmItem.FindNext, null);
        }

        /*--------------------------------------------------------------------*/
        /* Txe_FindNextOrAsk:                                                 */
        /*                                                                    */
        /* Find Next as the window's queue runs it: the last search again,    */
        /* or -- with nothing searched for yet -- the Find prompt first.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the next occurrence is selected, or nothing was asked.  */
        /*--------------------------------------------------------------------*/
        private void Txe_FindNextOrAsk()
        {
            if (Txe_sFind.Length == 0)
            {   /* Nothing to repeat: ask, as Find does (which then finds). */
                Txe_AskFind();
                return;
            }

            /* The last search again, from past its last find. */
            Txe_FindNext();
        }

        /*--------------------------------------------------------------------*/
        /* Txe_RunItem:                                                       */
        /*                                                                    */
        /* The window's own queue's runner (WindowQueue.cs): one item the     */
        /* handlers posted, run to its end -- the prompt shown, the search    */
        /* made, the Save As destination asked for, the close asked again.    */
        /* The queue calls it once per item and never inside another, so a    */
        /* second click while one runs is never run inside it.                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iItem   : the item, a Txe_enmItem value.                       */
        /*     objArgs : FindAnswer's prompt, AskClose's question display     */
        /*               (or null), CloseAnswer's question form, TextSize's   */
        /*               size in points (boxed); unused by every other item.  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item's work done, or nothing for an unknown item.   */
        /*--------------------------------------------------------------------*/
        private void Txe_RunItem(int iItem, object objArgs)
        {
            switch ((Txe_enmItem)iItem)
            {   /* One case per item: its own function. */
                case Txe_enmItem.Find:
                    /* The prompt, then the search. */
                    Txe_AskFind();
                    break;
                case Txe_enmItem.FindNext:
                    /* The last search again, or the prompt. */
                    Txe_FindNextOrAsk();
                    break;
                case Txe_enmItem.SaveAsImage:
                    /* An image destination asked for (false: not Windows). */
                    Txe_RequestSaveAs(false);
                    break;
                case Txe_enmItem.SaveAsWindows:
                    /* A Windows destination asked for. */
                    Txe_RequestSaveAs(true);
                    break;
                case Txe_enmItem.CloseAgain:
                    /* The close asked again: Close() raises FormClosing,        */
                    /* whose question (Txe_PresentClose) shows once more while   */
                    /* the text is still unsaved -- or, saved or discarded       */
                    /* meanwhile, simply closes.  A window already gone is       */
                    /* left alone: there is nothing to ask.                      */
                    if (!IsDisposed)
                    {   /* Still open: closed, or asked about, as any close is. */
                        Close();
                    }
                    break;
                case Txe_enmItem.FindAnswer:
                    /* The Find prompt's answer: the window enabled, then the   */
                    /* search.                                                  */
                    Txe_ApplyFindAnswer(objArgs as PRM_clsPromptForm);
                    break;
                case Txe_enmItem.AskClose:
                    /* The close question, shown editor-modal. */
                    Txe_ShowCloseQuestion(objArgs as PRS_clsPresentation);
                    break;
                case Txe_enmItem.CloseAnswer:
                    /* The close question's answer: the window enabled, then   */
                    /* the choice acted on.                                    */
                    Txe_ApplyCloseAnswer(objArgs as Form);
                    break;
                case Txe_enmItem.Save:
                    /* File > Save, if a save may start now. */
                    Txe_RunSave();
                    break;
                case Txe_enmItem.Close:
                    /* File > Close: the ordinary close, which                   */
                    /* Txe_OnFormClosing may refuse.  A window already gone is   */
                    /* left alone.                                               */
                    if (!IsDisposed)
                    {   /* Still open: closed, or asked about, as any close is. */
                        Close();
                    }
                    break;
                case Txe_enmItem.Undo:
                    /* The text box's own undo; its TextChanged marks the   */
                    /* window changed as any edit does.                     */
                    Txe_txtBody.Undo();
                    break;
                case Txe_enmItem.Cut:
                    /* The text box's own cut, the same as its Ctrl+X. */
                    Txe_txtBody.Cut();
                    break;
                case Txe_enmItem.Copy:
                    /* The text box's own copy, the same as its Ctrl+C. */
                    Txe_txtBody.Copy();
                    break;
                case Txe_enmItem.Paste:
                    /* The text box's own paste, the same as its Ctrl+V;   */
                    /* pasted text may carry bare line feeds, which        */
                    /* Txe_Compose allows.                                 */
                    Txe_txtBody.Paste();
                    break;
                case Txe_enmItem.SelectAll:
                    /* The whole text selected, as Ctrl+A does   */
                    /* (Txe_OnBodyKeyDown).                      */
                    Txe_txtBody.SelectAll();
                    break;
                case Txe_enmItem.Wrap:
                    /* The box set to Word Wrap's tick as it stands now. */
                    Txe_ApplyWrap();
                    break;
                case Txe_enmItem.TextSize:
                    /* The size the chosen item stood for, remembered and shown. */
                    Txe_ApplyTextSize((int)objArgs);
                    break;
                default:
                    /* No such item (a guard: only the handlers above post):   */
                    /* nothing done.                                           */
                    break;
            }
        }

        /*--------------------------------------------------------------------*/
        /* Txe_OnWrapChanged:                                                 */
        /*                                                                    */
        /* View > Word Wrap was ticked or unticked: the Wrap item posted; the */
        /* queue sets the box to the tick (Txe_ApplyWrap).                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item posted (or dropped as one already in hand).    */
        /*--------------------------------------------------------------------*/
        private void Txe_OnWrapChanged(object objSender, EventArgs evArgs)
        {
            /* Posted: the re-flow runs from the queue (Txe_RunItem), which   */
            /* reads the tick as it stands when it runs.                      */
            Txe_objQueue.WQU_Post((int)Txe_enmItem.Wrap, null);
        }

        /*--------------------------------------------------------------------*/
        /* Txe_ApplyWrap:                                                     */
        /*                                                                    */
        /* Wrap long lines at the window's edge, or let them run on with a    */
        /* horizontal scroll bar, as View > Word Wrap's tick says.  Wrapping  */
        /* only changes how the text is shown; no line break is put into the  */
        /* text.  Changing it makes the box rebuild itself, which is not an   */
        /* edit.                                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the box wraps or does not, as ticked.                   */
        /*--------------------------------------------------------------------*/
        private void Txe_ApplyWrap()
        {
            /* The box set to the tick, under the load flag: the rebuild   */
            /* can raise TextChanged (as found), which must not mark the   */
            /* window as changed.                                          */
            Txe_bLoading         = true;
            Txe_txtBody.WordWrap = Txe_tsmWrap.Checked;
            Txe_bLoading         = false;

            /* The caret's displayed line may have moved with the re-flow. */
            Txe_UpdateCaretStatus();
        }

        /*--------------------------------------------------------------------*/
        /* Txe_ApplyFontSize:                                                 */
        /*                                                                    */
        /* Show the text at a given size, and move the mark in View > Text    */
        /* Size to it.  A size that is not one the menu offers (a damaged     */
        /* stored value) is replaced by the default.  Only how the text is    */
        /* shown changes: nothing is edited, so the window is not marked as   */
        /* changed.                                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iPoints : the text size, points.                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the text box shows its text at that size.               */
        /*--------------------------------------------------------------------*/
        private void Txe_ApplyFontSize(int iPoints)
        {
            bool bOffered;
            int  i;

            /* Is the size one the menu offers?  Not until one matches (the   */
            /* loop runs on past a match, which changes nothing).             */
            bOffered = false;

            for (i = 0; i < Txe_aiFontSizes.Length; i++)
            {   /* One offered size per iteration. */
                if (Txe_aiFontSizes[i] == iPoints)
                {   /* The size asked for is one of them. */
                    bOffered = true;
                }
            }

            if (!bOffered)
            {   /* Not a size the menu has (a damaged or hand-edited stored   */
                /* value): the default, so a menu item is always ticked.      */
                iPoints = TXE_FONT_DEFAULT;
            }

            /* The text box in the fixed-width face at that size, under the   */
            /* load flag so the change is not taken for an edit.  The font    */
            /* it had is not disposed here: it is left to the collector (no   */
            /* reference to it is kept; a font in use elsewhere is not        */
            /* checked for).                                                  */
            Txe_bLoading     = true;
            Txe_txtBody.Font = new Font(TXE_FONT_NAME, (float)iPoints);
            Txe_bLoading     = false;

            /* The mark moved: exactly the item whose size this is ticked. */
            for (i = 0; i < Txe_tsmTextSize.DropDownItems.Count; i++)
            {   /* One size item per iteration: marked when it is the size in use. */
                ((ToolStripMenuItem)Txe_tsmTextSize.DropDownItems[i]).Checked = ((int)Txe_tsmTextSize.DropDownItems[i].Tag == iPoints);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Txe_OnTextSizeClicked:                                             */
        /*                                                                    */
        /* One of View > Text Size's items was chosen: the TextSize item      */
        /* posted with the size it stands for; the queue remembers and shows  */
        /* it (Txe_ApplyTextSize).  The size items are made in a loop and     */
        /* share this handler, so the size is read from the sender's Tag --   */
        /* the one place the item's identity is needed.                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item; its Tag is the size in points.      */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item posted (or dropped as one already in hand).    */
        /*--------------------------------------------------------------------*/
        private void Txe_OnTextSizeClicked(object objSender, EventArgs evArgs)
        {
            int iPoints;

            /* The size the item stands for, from the Tag the menu gave it,   */
            /* posted with the item.                                          */
            iPoints = (int)((ToolStripMenuItem)objSender).Tag;
            Txe_objQueue.WQU_Post((int)Txe_enmItem.TextSize, iPoints);
        }

        /*--------------------------------------------------------------------*/
        /* Txe_ApplyTextSize:                                                 */
        /*                                                                    */
        /* A View > Text Size choice, run from the window's queue: remember   */
        /* the size (the hex editor uses the same setting) and show the text  */
        /* at it.                                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iPoints : the size chosen, points.                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the text is the chosen size.                            */
        /*--------------------------------------------------------------------*/
        private void Txe_ApplyTextSize(int iPoints)
        {
            /* Remembered for the next text or hex editor opened (an open   */
            /* hex editor keeps its size until it is reopened), shown       */
            /* here, and the caret's place shown again, since a new size    */
            /* re-flows wrapped lines.                                      */
            SET_Settings.SET_WriteInt(SET_Settings.SET_EDITOR_FONT, iPoints);
            Txe_ApplyFontSize(iPoints);
            Txe_UpdateCaretStatus();
        }

        /*--------------------------------------------------------------------*/
        /* Txe_OnTextChanged:                                                 */
        /*                                                                    */
        /* The text changed.  Each edit the user makes moves the revision on  */
        /* -- every one, not only the first, so a save's answer can tell the  */
        /* text it saved from text typed after it, and an older save cannot   */
        /* clear newer changes -- and the first marks the window as having    */
        /* unsaved changes.                                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the text box.                                      */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the revision moved on; the caption shows the asterisk.  */
        /*--------------------------------------------------------------------*/
        private void Txe_OnTextChanged(object objSender, EventArgs evArgs)
        {
            if (Txe_bLoading)
            {   /* The program filling or re-flowing the box (the load, word   */
                /* wrap, a size change): not an edit, and no new revision.     */
                return;
            }

            /* A new revision for every edit, a save waiting or not; checked   */
            /* so an overflow throws rather than wraps back onto an old one.   */
            Txe_lRevision = checked(Txe_lRevision + 1);
            if (Txe_bDirty)
            {   /* Already marked: the asterisk is there, only the revision   */
                /* moved.                                                     */
                return;
            }

            /* The user's first edit since the load or the last save:      */
            /* marked, and the caption gains its asterisk.  Changing the   */
            /* text back does not clear it -- only a save does.            */
            Txe_bDirty = true;
            Txe_UpdateTitle();
        }

        /*--------------------------------------------------------------------*/
        /* Txe_OnBodyKeyDown:                                                 */
        /*                                                                    */
        /* A key went down in the text box.  Ctrl+A selects everything: the   */
        /* multi-line text box of the framework this program targets does not */
        /* do that by itself (as found).                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the text box.                                      */
        /*     evKey     : the key event.                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Ctrl+A is handled; every other key passes through.      */
        /*--------------------------------------------------------------------*/
        private void Txe_OnBodyKeyDown(object objSender, KeyEventArgs evKey)
        {
            if (evKey.Control && !evKey.Alt && !evKey.Shift && evKey.KeyCode == Keys.A)
            {   /* Ctrl+A, and only that (not with Alt or Shift): taken here   */
                /* -- handled, and its key press suppressed so the box does    */
                /* not beep or type it (as found) -- and the text selected.    */
                evKey.Handled          = true;
                evKey.SuppressKeyPress = true;
                Txe_txtBody.SelectAll();
            }
        }

        /*--------------------------------------------------------------------*/
        /* Txe_OnBodyKeyUp:                                                   */
        /*                                                                    */
        /* A key was released in the text box -- an arrow, Home, End, a page  */
        /* key, or a typed character -- any of which may have moved the       */
        /* caret: show where it is now.                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the text box.                                      */
        /*     evKey     : unused; every key is treated alike.                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the status bar shows the caret's line and column.       */
        /*--------------------------------------------------------------------*/
        private void Txe_OnBodyKeyUp(object objSender, KeyEventArgs evKey)
        {
            /* Wherever the caret is now, onto the status bar.  Asking the   */
            /* box costs little, so no key is told apart from the others.    */
            Txe_UpdateCaretStatus();
        }

        /*--------------------------------------------------------------------*/
        /* Txe_OnBodyMouseUp:                                                 */
        /*                                                                    */
        /* A mouse button was released in the text box -- a click that placed */
        /* the caret, or the end of a drag that selected text: show where the */
        /* caret is now.                                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the text box.                                      */
        /*     evMouse   : unused; every button is treated alike.             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the status bar shows the caret's line and column.       */
        /*--------------------------------------------------------------------*/
        private void Txe_OnBodyMouseUp(object objSender, MouseEventArgs evMouse)
        {
            /* Wherever the caret is now, onto the status bar -- the same   */
            /* refresh a released key makes.                                */
            Txe_UpdateCaretStatus();
        }

        /*--------------------------------------------------------------------*/
        /* Txe_OnResize:                                                      */
        /*                                                                    */
        /* The window changed size.  Docking has already placed the controls; */
        /* with word wrap on, the lines have re-flowed and the caret's        */
        /* displayed line may have changed, so it is shown again.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the window.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the status bar matches the new layout.                  */
        /*--------------------------------------------------------------------*/
        private void Txe_OnResize(object objSender, EventArgs evArgs)
        {
            /* The caret's line and column again, as re-flowed. */
            Txe_UpdateCaretStatus();
        }

        /*--------------------------------------------------------------------*/
        /* Txe_OnFormClosing:                                                 */
        /*                                                                    */
        /* The window is asked to close: decide, about unsaved changes and    */
        /* work still running (Txe_PresentClose), counting the handler as     */
        /* the window's own work while it runs.  It shows nothing: unsaved    */
        /* text only posts the close question to the window's queue, so a     */
        /* close a save asked for is never posted from inside this handler.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the window (unused).                               */
        /*     evClosing : the closing event; cancelled to keep the window.   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the close allowed or cancelled; any close a save        */
        /*            asked for is posted once this handler has returned.     */
        /*--------------------------------------------------------------------*/
        private void Txe_OnFormClosing(object objSender, FormClosingEventArgs evClosing)
        {
            /* Inside the handler from here to the finally (a count of the   */
            /* UI's own nesting; nothing to do with what the worker runs).   */
            Txe_iClosing = checked(Txe_iClosing + 1);
            try
            {   /* The decision, released in the finally however it ends. */
                Txe_PresentClose(evClosing);
            }
            finally
            {   /* Out of the handler: any close a save asked for may be   */
                /* posted now, as it can no longer nest in this one.       */
                Txe_iClosing--;
                Txe_PostReadyCloses();

                /* The coordinator told, so a close question that waited   */
                /* while this handler ran is looked at again (it finds     */
                /* the close question asked for, and waits on).            */
                Txe_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
            }
        }

        /*------------------------------------------------------------------*/
        /* Txe_PresentClose:                                                */
        /*                                                                  */
        /* Whether the window may close now -- a decision only, made inside */
        /* the closing handler, which shows nothing.  It keeps itself open  */
        /* while a save waits or its own work runs (its close question      */
        /* among it); closes at once when nothing is unsaved, or when a     */
        /* Discard answer is closing it; and otherwise keeps the window     */
        /* open and posts the close question to the window's queue          */
        /* (Txe_ShowCloseQuestion), with the worker's close question's      */
        /* display when one is on the stack -- taken now, the one moment it */
        /* can be.                                                          */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     evClosing : the closing event; cancelled to keep the window. */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the close allowed (nothing unsaved, or an explicit    */
        /*            discard) or cancelled, the question posted when there */
        /*            is unsaved text to ask about.                         */
        /*------------------------------------------------------------------*/
        private void Txe_PresentClose(FormClosingEventArgs evClosing)
        {
            PRS_clsPresentation objPresentation;

            if (Txe_iPendingSaves != 0 || Txe_iLocalWork != 0 || Txe_frmPrompt != null ||
                Txe_bCloseAsked || Txe_objCloseQuestion != null)
            {   /* A save's answer is still to come, or the window's own      */
                /* work is running -- its Find prompt or its close question   */
                /* (asked for or up) among it: kept open, as each still       */
                /* needs the window.  No question is asked; a close the       */
                /* worker asks for is held back by the coordinator until      */
                /* they return.                                               */
                evClosing.Cancel = true;
                return;
            }

            if (Txe_bDiscardOnClose)
            {   /* The close a Discard answer makes (Txe_ApplyCloseAnswer):   */
                /* closes, the changed text let go -- that answer was the     */
                /* question, so it is not asked again.                        */
                return;
            }

            if (!Txe_bDirty || Txe_bReadOnly)
            {   /* Nothing unsaved: closes, no question.  The read-only       */
                /* test is a guard -- such a text takes no typing, so it is   */
                /* never marked as changed.                                   */
                return;
            }

            /* The worker's close question for this editor, if one is       */
            /* showing (an image close's or an Exit's): its display is on   */
            /* the stack only while this handler runs, so it is taken now   */
            /* and carried with the post -- the question it answers.        */
            objPresentation = QWN_clsQuestionWindows.QWN_CurrentEditorQuestion(Txe_file.OFD_lEditorId);
            if (objPresentation == null && !Txe_file.OFD_CanRequestUserFile())
            {   /* The user's own close (no worker question) while a save   */
                /* could not start (OFD_CanRequestUserFile): kept open,     */
                /* with no question.  A close the worker asks for still     */
                /* gets its answers -- the worker asked for them.           */
                evClosing.Cancel = true;
                return;
            }

            /* Kept open, and the question posted to the window's queue,      */
            /* which shows it once this handler has returned -- a handler     */
            /* opens nothing.  Marked as asked for meanwhile, so the window   */
            /* counts it as its own work (TXE_HasLocalWork) and a second      */
            /* close posts no second question.                                */
            evClosing.Cancel = true;
            Txe_bCloseAsked = true;
            Txe_objQueue.WQU_Post((int)Txe_enmItem.AskClose, objPresentation);
        }

        /*--------------------------------------------------------------------*/
        /* Txe_ShowCloseQuestion:                                             */
        /*                                                                    */
        /* The close question, run by the window's queue for a close that     */
        /* found unsaved text (Txe_PresentClose): Save / Save As Image /      */
        /* Save As Windows / Discard / Cancel, or, when the entry's image     */
        /* is gone, the recovery question (Save As to Windows, Discard,       */
        /* Cancel).  It is EDITOR-MODAL: shown modeless over this window,     */
        /* which is disabled while it is up, so the main window and every     */
        /* other editor carry on; its choice comes back through the queue     */
        /* (Txe_OnCloseQuestionClosed, Txe_ApplyCloseAnswer).  Kept as the    */
        /* window's own work while it is up, so a close the worker asks for   */
        /* waits for its answer.  Under the worker's close question it is     */
        /* kept under that question too, so an Exit taking its place takes    */
        /* it down unanswered.                                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objPresentation : the worker's close question's display, as    */
        /*                       the closing handler took it; null for the    */
        /*                       window's own close.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the question shown; or, when there is nothing left to   */
        /*            ask, the close made again or the coordinator told.      */
        /*--------------------------------------------------------------------*/
        private void Txe_ShowCloseQuestion(PRS_clsPresentation objPresentation)
        {
            Form frmQuestion;
            bool bOrdinary;
            bool bImageOpen;

            /* No longer only asked for: shown below, or not at all. */
            Txe_bCloseAsked = false;

            if (IsDisposed)
            {   /* A guard: the window is gone -- nothing to ask about. */
                return;
            }

            if (objPresentation != null &&
                !objPresentation.PRS_objQuestion.QST_IsCurrent(objPresentation))
            {   /* The worker's question was answered or replaced (an Exit)   */
                /* since the close was asked: nothing is shown for it.  The   */
                /* coordinator is told, and a question shown again asks the   */
                /* close again under its new display.                         */
                Txe_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
                return;
            }

            if (!Txe_bDirty)
            {   /* The text was saved since the close was asked: nothing to   */
                /* ask.  The close made again, which now finds nothing        */
                /* unsaved and closes -- its FormClosed answering the         */
                /* worker's question, if there is one.                        */
                Close();
                return;
            }

            /* Which question: the ordinary one, unless this is an image   */
            /* entry whose image is gone, which cannot be saved there --   */
            /* then the recovery question (EditorSaveMenu.cs).             */
            bOrdinary = Txe_file.OFD_bHostFile || !Txe_file.OFD_bDetached;

            /* Save As Image is offered only by the ordinary question, only   */
            /* on the user's own close, and only while the Save As menu       */
            /* would offer its Image item -- the target picker and an image   */
            /* open.  Under the worker's close question it is greyed: the     */
            /* worker runs only a Windows Save As inside a close              */
            /* (Sys_CloseEditors), and an image Save As posted then could     */
            /* run only after that close had ended.                           */
            bImageOpen = bOrdinary && objPresentation == null && Txe_file.OFD_pfnPickTarget != null &&
                Txe_file.OFD_pfnImageAvailable != null && Txe_file.OFD_pfnImageAvailable();

            /* The question, kept with the display it answers, under this   */
            /* window's icon; its closing only posts                        */
            /* (Txe_OnCloseQuestionClosed).                                 */
            Txe_objCloseQuestion = RCD_clsRecoveryDialog.RCD_Create(Txe_fntDialog, Txe_file, bOrdinary, bImageOpen);
            Txe_objCloseShown = objPresentation;
            frmQuestion = Txe_objCloseQuestion.RCD_ReadPopup();
            frmQuestion.Icon = this.Icon;
            frmQuestion.FormClosed += Txe_OnCloseQuestionClosed;
            if (objPresentation != null)
            {   /* Under the worker's question: kept under it, so an Exit   */
                /* taking its place closes this one (QWN_Supersede).        */
                QWN_clsQuestionWindows.QWN_AttachEditorModal(objPresentation, frmQuestion);
            }

            /* Placed over the middle of this window (a modeless window is     */
            /* not centred by the framework), this window disabled, then the   */
            /* question shown modeless and owned by it -- so it stays above    */
            /* this window, and it alone of the program is held up.            */
            PRM_clsPromptForm.PRM_CentreOver(frmQuestion, this);
            this.Enabled = false;
            frmQuestion.Show(this);
        }

        /*------------------------------------------------------------------*/
        /* Txe_OnCloseQuestionClosed:                                       */
        /*                                                                  */
        /* The close question has closed (a button, Escape, its close box,  */
        /* or an Exit taking it down): posted to the window's queue, which  */
        /* acts on its choice (Txe_ApplyCloseAnswer).  Nothing else is done */
        /* here.                                                            */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objSender : the question's form.                             */
        /*     evArgs    : unused.                                          */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the answer posted.                                    */
        /*------------------------------------------------------------------*/
        private void Txe_OnCloseQuestionClosed(object objSender, FormClosedEventArgs evArgs)
        {
            /* Posted with the form it came from. */
            Txe_objQueue.WQU_Post((int)Txe_enmItem.CloseAnswer, objSender);
        }

        /*------------------------------------------------------------------*/
        /* Txe_ApplyCloseAnswer:                                            */
        /*                                                                  */
        /* The close question's choice, run by the window's queue: the      */
        /* window enabled again and the question released, then the choice  */
        /* acted on -- Discard closes, the changes dropped; Save saves and  */
        /* closes once that save succeeds; either Save As asks for its      */
        /* destination (closing afterwards only on the user's own close --  */
        /* under the worker's question that question goes on once the save  */
        /* is applied); Cancel keeps the window, and under the worker's     */
        /* question answers it Cancel (EUI_LOCAL_DECLINED).  A question an  */
        /* Exit took down, its display no longer current, acts on nothing:  */
        /* the worker asks again under its new display.  A save or Save As  */
        /* under the worker's question is made inside that display's        */
        /* record (QWN_Begin), so it answers that question as it did when   */
        /* the question was modal (OFD_ForSave, Main_RequestEditorTarget).  */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     frmQuestion : the question's form that closed.               */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the choice acted on, or nothing for a stale one.      */
        /*------------------------------------------------------------------*/
        private void Txe_ApplyCloseAnswer(Form frmQuestion)
        {
            RCD_clsRecoveryDialog objQuestion;
            PRS_clsPresentation objPresentation;
            QSC_clsWindowScope objScope;
            TPI_clsTextPickIntent objIntent;
            RCA_enmRecoveryChoice enmChoice;
            bool bOwnClose;

            if (Txe_objCloseQuestion == null || frmQuestion != Txe_objCloseQuestion.RCD_ReadPopup())
            {   /* A guard: not the question this window has up -- nothing   */
                /* to act on.                                                */
                return;
            }

            /* The question released and the window enabled again, before   */
            /* anything is acted on, so a save's messages and a Save As's   */
            /* picker meet a live window: its fields cleared, its record    */
            /* under the worker's question dropped, its choice read, its    */
            /* form disposed.                                               */
            objQuestion = Txe_objCloseQuestion;
            objPresentation = Txe_objCloseShown;
            Txe_objCloseQuestion = null;
            Txe_objCloseShown = null;
            if (objPresentation != null)
            {   /* It was kept under the worker's question: no longer. */
                QWN_clsQuestionWindows.QWN_DetachEditorModal(objPresentation, frmQuestion);
            }
            enmChoice = objQuestion.RCD_ReadAnswer();
            frmQuestion.Dispose();
            this.Enabled = true;
            this.Activate();

            if (objPresentation != null &&
                !objPresentation.PRS_objQuestion.QST_IsCurrent(objPresentation))
            {   /* The worker's question was answered or replaced while this    */
                /* was up -- an Exit took it down: no choice is acted on, the   */
                /* text stays, and the coordinator is told; an Exit declined    */
                /* shows the question again, which asks this close again.       */
                Txe_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
                return;
            }

            if (enmChoice == RCA_enmRecoveryChoice.Discard)
            {   /* Discard, said in so many words: the close made again,         */
                /* marked so it closes without asking (Txe_PresentClose) --      */
                /* its FormClosed answering the worker's question, if there is   */
                /* one.  The mark is taken off whatever the close did, so it     */
                /* can never let a later close go unasked.                       */
                Txe_bDiscardOnClose = true;
                try
                {   /* The close, which lets the changes go. */
                    Close();
                }
                finally
                {   /* Unmarked (harmless on a window now closed). */
                    Txe_bDiscardOnClose = false;
                }
                return;
            }

            if (enmChoice == RCA_enmRecoveryChoice.Cancel)
            {   /* Cancel (Escape and the close box included): the window and   */
                /* its text stay.                                               */
                if (objPresentation != null)
                {   /* Under the worker's question: it is answered Cancel, so   */
                    /* its close stops (Eui_OnEditorFact).                      */
                    Txe_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_DECLINED);
                }
                else
                {   /* The window's own close: only the coordinator told that   */
                    /* this window's own work has returned.                     */
                    Txe_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
                }
                return;
            }

            /* A save or a Save As.  Under the worker's question it is made   */
            /* inside a record of that display (QWN_Begin), the one moment    */
            /* its question is again on the stack, so the save is captured    */
            /* as that question's answer and a Windows Save As goes back      */
            /* inside it -- as when the question was modal.                   */
            objScope = null;
            if (objPresentation != null)
            {   /* The worker's question: its record begun. */
                objScope = QWN_clsQuestionWindows.QWN_Begin(objPresentation);
            }
            try
            {   /* The choice, by kind. */
                if (enmChoice == RCA_enmRecoveryChoice.Save)
                {   /* The save, asking to close once it succeeds; the close     */
                    /* is posted after this call has returned.  A save refused   */
                    /* here (an encoding the text no longer fits) has said       */
                    /* why, and the window simply stays.                         */
                    Txe_SaveTo(Txe_file, true);
                }
                else
                {   /* A Save As -- to the image or to Windows from the         */
                    /* ordinary question, to Windows from the recovery one --   */
                    /* its destination asked for through the Save As menu's     */
                    /* own request, as File > Save As asks it.  It closes the   */
                    /* window afterwards only when this is the user's own       */
                    /* close; a picker cancelled then posts the close again     */
                    /* (TXE_ApplyTargetChoice), and under the worker's          */
                    /* question the worker asks again itself.                   */
                    bOwnClose = objPresentation == null;
                    objIntent = new TPI_clsTextPickIntent(this, Txe_file, bOwnClose);
                    SVM_clsSaveMenu.SVM_Request(this, Txe_file,
                        enmChoice != RCA_enmRecoveryChoice.SaveAsImage, objIntent.TPI_OnChosen);
                }
            }
            finally
            {   /* The display's record ended, and the coordinator told that   */
                /* this window's own work has returned.                        */
                if (objScope != null)
                {   /* Begun above: ended. */
                    QWN_clsQuestionWindows.QWN_End(objScope);
                }
                Txe_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
            }
        }
    }
}
