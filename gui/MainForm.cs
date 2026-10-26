/*----------------------------------------------------------------------------*/
/* MainForm.cs                                                                */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* BifrostFM's window: two panes side by side.  The LEFT pane shows the       */
/* filesystem -- ext2, ext3 or ext4, FAT12, FAT16, FAT32 or exFAT -- inside   */
/* an image file, or on a device (a card's partition, opened through File >   */
/* Open > Device); the RIGHT pane shows the Windows filesystem.  Each pane is */
/* a path box over a list that can be shown five ways (large icons, small     */
/* icons, list, details, tiles), chosen per pane from the View menu and       */
/* remembered.                                                                */
/*                                                                            */
/* EVERY ROW CARRIES AN ICON, in every view.  The Windows pane's are the      */
/* shell's own -- the list draws straight from the system image lists, so a   */
/* file shows the icon Explorer shows for it (ShellIcons.cs).  The image      */
/* pane's entries are not Windows files and the shell has nothing for them,   */
/* so the program draws its own, one per kind of entry (EntryIcons.cs).       */
/*                                                                            */
/* The Windows pane is read on the UI thread, its slower shell icons filled   */
/* in a few at a time on timer ticks.  Everything that touches the image is   */
/* posted to the command system as a request (MainCommand.cs): its one        */
/* worker thread is the only one that calls the engine, runs each command's   */
/* whole sequence -- its questions, its writes, its result -- one at a time,  */
/* and hands the finished listing or result back to this window to show.      */
/*                                                                            */
/* An image is always opened READ-ONLY.  The first command that would change  */
/* it asks before reopening for writing when risk warnings are enabled.       */
/*                                                                            */
/* New Folder, Rename and Delete act on whichever pane the focus was last in. */
/*                                                                            */
/* OPEN.  A double-click (or Enter, or Open on a menu) goes into a folder in  */
/* either pane.  On a Windows file it hands the file to Windows' own default  */
/* program.  On a file INSIDE THE IMAGE it copies the file out to a temporary */
/* folder and shows it in one of the program's own windows, chosen from the   */
/* file's content (OpenScan.cs): the picture viewer, the structured editor    */
/* (json\) when the text is a config format the program can identify -- by    */
/* the file's name, or JSON by its content, while the Options tick for that   */
/* is on -- the text editor for any other text, or -- after a question -- the */
/* hex editor.  OPEN AS (a sub-menu on the Commands menu and the right-click  */
/* menu, in either pane) names the window instead: the text editor, the hex   */
/* editor, or the structured editor as one of its formats -- on a Windows     */
/* file too, which the editor then works on in place.  NEW (a sub-menu        */
/* beside it, and a pull-down on each pane's toolbar) makes a new text, hex,  */
/* JSON or INI file in the active pane -- in the image after the same         */
/* write-mode question a new folder asks -- and then opens it in its editor.  */
/* Those windows are separate, modeless windows; a save from one comes back   */
/* through Main_SaveOpenedFile to the registered editor coordinator.          */
/*                                                                            */
/* CHECK FILESYSTEM (File menu) runs the engine's e2fsck over the open image  */
/* or device, report-only, in a window of its own (CheckForm.cs) -- or, with  */
/* nothing open, over an image file or device picked for it.  e2fsck reads    */
/* only ext2/3/4, so Check is greyed while a FAT or exFAT filesystem is open  */
/* (MainCommand.cs) and refused by the worker too.  An open image is CLOSED   */
/* for the check -- the engine checks a filesystem it does not have open --   */
/* and reopened read-only when that window closes.  The window's Repair runs  */
/* the check again, fixing what it finds, after the same kind of question a   */
/* first write asks: it is posted as a request for the same target and        */
/* window (Main_OnCheckRepair), and the worker asks and runs it.              */
/*----------------------------------------------------------------------------*/

/* The framework's namespaces.  gui\lib's classes -- DBGLOG_clsDebugLog (the log   */
/* every error line goes to) and the debug log window (Help > Debug Log, on a      */
/* DEBUG build's menu only) -- are in this program's own namespace.                */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* MAIN_clsMainForm -- the main window (the file header describes it).    */
    /* Its base class is declared here; the class is partial because its      */
    /* command-system side lives in MainCommand.cs.                           */
    /*                                                                        */
    /* Derives from Form because the framework requires it: WinForms makes a  */
    /* window only as a Form subclass.  That is the one kind of inheritance   */
    /* allowed here -- from a framework base class, never from a class of     */
    /* ours.                                                                  */
    /*------------------------------------------------------------------------*/
    internal sealed partial class MAIN_clsMainForm : Form
    {
        /*--------------------------------------------------------------------*/
        /* Dgm_clsDragMaterializer -- one drag out of the image pane's        */
        /* read-out, held for the moment an outside program (Explorer, say)   */
        /* takes the drop and asks for the files.  The drag data can only     */
        /* call a method with no arguments (DRG_Materialize, DragData.cs),    */
        /* so everything the read-out needs is captured here when the drag    */
        /* starts (Main_OnImageItemDrag) and handed to Main_MaterializeDrag   */
        /* by DGM_Run.  Nested in the window so it can call that private      */
        /* function.                                                          */
        /*--------------------------------------------------------------------*/
        private class Dgm_clsDragMaterializer
        {
            /* What the read-out needs -- documentation table. */
            private MAIN_clsMainForm Dgm_objOwner;       // the window whose Main_MaterializeDrag runs the read-out
            private string Dgm_sFolder;                  // the drag's own temporary folder under %TEMP%; only named so far -- the read-out creates it, if there is one
            private string[] Dgm_arrImagePaths;          // the selected entries whose names Windows can hold, as image paths; the drag's own array, kept as given
            private string[] Dgm_arrHostPaths;           // the path each will have in that folder, index for index -- the same array the drag data offers as its file plan
            private STV_clsStateView Dgm_objSource;      // the state when the drag began: the open, its range and epoch; the read-out's request is made against it
            private long Dgm_lToken;                     // the worker's hold for this drag (SYS_ReserveDrag), carried by the read-out's request
            private EXO_stExtractOptions Dgm_stOptions;  // the manifest and modify-time options as they were when the drag began (a copy: a change in the Options menu during the drag does not reach it)
            internal string DGM_sProblem;                // what went wrong with a read-out that still handed files over (some skipped or failed), for the status line once the drag has returned (Main_OnImageItemDrag); "" when nothing did, or no read-out ran
            internal bool DGM_bHandedOver;               // true once a read-out has handed at least one file to the program that asked; false when none ran, or it was cancelled or refused and handed nothing

            /*-----------------------------------------------------------------*/
            /* Dgm_clsDragMaterializer:                                        */
            /*                                                                 */
            /* Capture one drag's read-out.  Nothing is created or posted      */
            /* here: the folder is made and the extract posted only if an      */
            /* outside program asks for the files (DGM_Run).                   */
            /*                                                                 */
            /* Arguments:                                                      */
            /*     objOwner      : the window.                                 */
            /*     sFolder       : the drag's temporary folder (not yet made). */
            /*     arrImagePaths : the entries to read out, as image paths.    */
            /*     arrHostPaths  : each one's path in that folder.             */
            /*     objSource     : the state when the drag began.              */
            /*     lToken        : the worker's hold for the drag.             */
            /*     stOptions     : the extract options at the drag's start.    */
            /*                                                                 */
            /* Returns:                                                        */
            /*     (constructor) : the read-out, not yet run.                  */
            /*-----------------------------------------------------------------*/
            internal Dgm_clsDragMaterializer(MAIN_clsMainForm objOwner, string sFolder,
                string[] arrImagePaths, string[] arrHostPaths, STV_clsStateView objSource,
                long lToken, EXO_stExtractOptions stOptions)
            {
                /* Kept as given.  The two arrays are the drag's own, made     */
                /* for it alone by Main_OnImageItemDrag and never changed,     */
                /* so no copy is taken; the options are a struct, so this is   */
                /* already a copy of them.                                     */
                Dgm_objOwner = objOwner;
                Dgm_sFolder = sFolder;
                Dgm_arrImagePaths = arrImagePaths;
                Dgm_arrHostPaths = arrHostPaths;
                Dgm_objSource = objSource;
                Dgm_lToken = lToken;
                Dgm_stOptions = stOptions;

                /* No read-out has run, so there is no problem to report and   */
                /* nothing has been handed over.                               */
                DGM_sProblem = "";
                DGM_bHandedOver = false;
            }

            /*----------------------------------------------------------------*/
            /* DGM_Run:                                                       */
            /*                                                                */
            /* The drag data's read-out (its DRG_Materialize): called on the  */
            /* UI thread, inside the drop, when an outside program asks for   */
            /* the files -- once at most, since the drag data keeps the       */
            /* answer for any later request (DragData.cs).                    */
            /*                                                                */
            /* Arguments:                                                     */
            /*     None.                                                      */
            /*                                                                */
            /* Returns:                                                       */
            /*     string[] : the paths of the items now in the folder;       */
            /*                empty when nothing was handed over.  A          */
            /*                read-out that handed files over with some       */
            /*                skipped or failed leaves its sentence in        */
            /*                DGM_sProblem; whether any file was handed over  */
            /*                is left in DGM_bHandedOver.                     */
            /*----------------------------------------------------------------*/
            internal string[] DGM_Run()
            {
                string[] arrHanded;

                /* The values captured at the drag's start, never the pane as   */
                /* it is now: what is read out must be what was dragged.  The   */
                /* problem sentence is kept here, for the drag's end to show:   */
                /* the main window paints nothing until the drop returns.       */
                arrHanded = Dgm_objOwner.Main_MaterializeDrag(Dgm_sFolder, Dgm_arrImagePaths,
                    Dgm_arrHostPaths, Dgm_objSource, Dgm_lToken, Dgm_stOptions, out DGM_sProblem);

                /* Whether anything went to the asking program: a cancelled or   */
                /* refused read-out hands over nothing, and its own status       */
                /* line must then stand (Main_OnImageItemDrag reads this).       */
                DGM_bHandedOver = (arrHanded.Length != 0);

                /* The paths handed over, for the drag data to give the drop. */
                return(arrHanded);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Dmc_clsDragCallbacks -- the two calls made while the main window   */
        /* is held inside a drop, waiting for the drag's read-out             */
        /* (Main_MaterializeDrag): the feedback window's Cancel, and the      */
        /* read-out's progress.  Neither may touch the main window, whose     */
        /* thread runs nothing posted to it until the read-out ends, so both  */
        /* go straight to the command system and to the feedback window.      */
        /*--------------------------------------------------------------------*/
        private class Dmc_clsDragCallbacks
        {
            /* Where the two calls go -- documentation table. */
            private SYS_clsSystem Dmc_objSystem;        // the command system: the Cancel is posted on it directly (its posting is thread-safe), never through the command client, whose replies belong to the main window's thread
            private RQT_clsRequest Dmc_objRequest;      // the read-out's request; the serial its post gives it is what the Cancel names
            private DFW_clsFeedbackWindow Dmc_objView;  // the drag's feedback window, on a thread of its own (DragData.cs), where the progress is shown

            /*----------------------------------------------------------------*/
            /* Dmc_clsDragCallbacks:                                          */
            /*                                                                */
            /* Hold the three for the two calls below.                        */
            /*                                                                */
            /* Arguments:                                                     */
            /*     objSystem  : the command system.                           */
            /*     objRequest : the read-out's request, not yet posted.       */
            /*     objView    : the feedback window.                          */
            /*                                                                */
            /* Returns:                                                       */
            /*     (constructor) : the calls' holder; nothing is started.     */
            /*----------------------------------------------------------------*/
            internal Dmc_clsDragCallbacks(SYS_clsSystem objSystem, RQT_clsRequest objRequest,
                DFW_clsFeedbackWindow objView)
            {
                /* Kept as given.  The request has no serial yet; its post   */
                /* gives it one before the feedback window's Cancel is       */
                /* turned on (DFW_ArmCommand), so the Cancel always has a    */
                /* serial to name.                                           */
                Dmc_objSystem = objSystem;
                Dmc_objRequest = objRequest;
                Dmc_objView = objView;
            }

            /*----------------------------------------------------------------*/
            /* DMC_RequestCancel:                                             */
            /*                                                                */
            /* The feedback window's Cancel (its DFW_CancelRequest): a Cancel */
            /* of the read-out, by its serial.  Called on the feedback        */
            /* window's own thread.                                           */
            /*                                                                */
            /* Arguments:                                                     */
            /*     None.                                                      */
            /*                                                                */
            /* Returns:                                                       */
            /*     void : the Cancel is posted; the worker stops the read-out */
            /*            at its next check.                                  */
            /*----------------------------------------------------------------*/
            internal void DMC_RequestCancel()
            {
                /* Posted on the command system directly, for the main        */
                /* window (origin 0), naming the read-out by its serial.  A   */
                /* Cancel that arrives after the read-out has finished is     */
                /* refused by SYS_Post ("no longer running"), so it can       */
                /* never stop the request that came after it.                 */
                Dmc_objSystem.SYS_Post(CFC_clsCommands.CFC_Create(KND_enmKind.Cancel,
                    0, null, null, null, Dmc_objRequest.RQT_lSerial, 0, null, "", ""));
            }

            /*----------------------------------------------------------------*/
            /* DMC_Report:                                                    */
            /*                                                                */
            /* The read-out's progress (its DWT_Progress), called on the      */
            /* thread that publishes it -- the worker -- through the command  */
            /* client's drag channel (CommandClient.cs, DWT_Forward), never   */
            /* on the main window's.                                          */
            /*                                                                */
            /* Arguments:                                                     */
            /*     objProgress : the event's facts, already copied out of     */
            /*                   the engine's event.                          */
            /*                                                                */
            /* Returns:                                                       */
            /*     void : the update is handed to the feedback window, which  */
            /*            posts it to its own thread.                         */
            /*----------------------------------------------------------------*/
            internal void DMC_Report(PGD_clsProgressData objProgress)
            {
                /* The item's path and its bytes done and total.  An item's    */
                /* START event forces the update past the window's rate        */
                /* limit (DFW_REPORT_MS), so each new item is shown at once.   */
                Dmc_objView.DFW_Report(objProgress.PGD_sImage,
                    objProgress.PGD_ullDone, objProgress.PGD_ullTotal,
                    objProgress.PGD_uiKind == ENG_clsNative.ENG_EVENT_START);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Mpb_clsPaneBinding -- one pane, held for the handlers of controls  */
        /* made in loops for that pane: its New pull-down's kinds, its view   */
        /* items, the right-click menu's view and sort items, and its posted  */
        /* tick sync.  Its named methods are those handlers, each knowing its */
        /* pane from here rather than from a closure.  (A pane's own toolbar  */
        /* buttons are not among them: each has a named handler of its own    */
        /* in the window, Main_BuildToolbar.)  Nested in the window so the    */
        /* handlers can call its private functions.                           */
        /*--------------------------------------------------------------------*/
        private class Mpb_clsPaneBinding
        {
            private MAIN_clsMainForm Mpb_objOwner;       // the window the pane belongs to
            private int              Mpb_iSide;          // MAIN_SIDE_IMAGE or MAIN_SIDE_HOST

            /*----------------------------------------------------------------*/
            /* Mpb_clsPaneBinding:                                            */
            /*                                                                */
            /* Holds the pane for the handlers below.                         */
            /*                                                                */
            /* Arguments:                                                     */
            /*     objOwner : the window.                                     */
            /*     iSide    : MAIN_SIDE_IMAGE or MAIN_SIDE_HOST.              */
            /*                                                                */
            /* Returns:                                                       */
            /*     (constructor) : the binding.                               */
            /*----------------------------------------------------------------*/
            internal Mpb_clsPaneBinding(MAIN_clsMainForm objOwner, int iSide)
            {
                /* Kept as given, for the handlers below. */
                Mpb_objOwner = objOwner;
                Mpb_iSide    = iSide;
            }

            /*----------------------------------------------------------------*/
            /* MPB_OnNewKindClick:                                            */
            /*                                                                */
            /* One kind on a pane's New pull-down: its pane made the active   */
            /* one, then the file of that kind made there.  The four items    */
            /* are built in a loop, so they share this handler and each       */
            /* carries its MAIN_NEW_* kind in its Tag.                        */
            /*                                                                */
            /* Arguments:                                                     */
            /*     objSender : the menu item; its Tag is the MAIN_NEW_* kind. */
            /*     evArgs    : unused.                                        */
            /*                                                                */
            /* Returns:                                                       */
            /*     void : a New File request for that kind is posted.         */
            /*----------------------------------------------------------------*/
            internal void MPB_OnNewKindClick(object objSender, EventArgs evArgs)
            {
                /* This pane first, then the clicked kind. */
                Mpb_objOwner.Main_ActivatePane(Mpb_iSide == MAIN_SIDE_HOST);
                Mpb_objOwner.Main_NewFile((int)((ToolStripMenuItem)objSender).Tag);
            }

            /*----------------------------------------------------------------*/
            /* MPB_OnViewClick:                                               */
            /*                                                                */
            /* One of a pane's five view items -- in its View sub-menu, on    */
            /* its toolbar's pull-down, or in the right-click menu's View:    */
            /* that view chosen for this pane.  Built in loops, they share    */
            /* this handler, each carrying its View in its Tag.               */
            /*                                                                */
            /* Arguments:                                                     */
            /*     objSender : the menu item; its Tag is the View.            */
            /*     evArgs    : unused.                                        */
            /*                                                                */
            /* Returns:                                                       */
            /*     void : the pane shows the chosen view.                     */
            /*----------------------------------------------------------------*/
            internal void MPB_OnViewClick(object objSender, EventArgs evArgs)
            {
                /* This pane, the clicked item's view. */
                Mpb_objOwner.Main_ChooseView(Mpb_iSide, (View)((ToolStripMenuItem)objSender).Tag);
            }

            /*----------------------------------------------------------------*/
            /* MPB_OnSortColumnClick:                                         */
            /*                                                                */
            /* One column item of the right-click menu's Sort By: this pane   */
            /* sorted by that column, the direction in force kept.  Built in  */
            /* a loop, they share this handler, each carrying its column's    */
            /* index in its Tag.                                              */
            /*                                                                */
            /* Arguments:                                                     */
            /*     objSender : the menu item; its Tag is the column index.    */
            /*     evArgs    : unused.                                        */
            /*                                                                */
            /* Returns:                                                       */
            /*     void : the pane is sorted by that column.                  */
            /*----------------------------------------------------------------*/
            internal void MPB_OnSortColumnClick(object objSender, EventArgs evArgs)
            {
                /* This pane, the clicked column, the direction kept. */
                Mpb_objOwner.Main_SetSort(Mpb_iSide, (int)((ToolStripMenuItem)objSender).Tag,
                    Mpb_objOwner.Main_asrtSide[Mpb_iSide].PSR_bDescending);
            }

            /*----------------------------------------------------------------*/
            /* MPB_OnSortAscendingClick:                                      */
            /*                                                                */
            /* Sort By's Ascending: this pane sorted ascending, by the column */
            /* in force.                                                      */
            /*                                                                */
            /* Arguments:                                                     */
            /*     objSender : the menu item (unused).                        */
            /*     evArgs    : unused.                                        */
            /*                                                                */
            /* Returns:                                                       */
            /*     void : the pane is sorted ascending.                       */
            /*----------------------------------------------------------------*/
            internal void MPB_OnSortAscendingClick(object objSender, EventArgs evArgs)
            {
                /* The column kept, ascending. */
                Mpb_objOwner.Main_SetSort(Mpb_iSide, Mpb_objOwner.Main_asrtSide[Mpb_iSide].PSR_iColumn, false);
            }

            /*----------------------------------------------------------------*/
            /* MPB_OnSortDescendingClick:                                     */
            /*                                                                */
            /* Sort By's Descending: this pane sorted descending, by the      */
            /* column in force.                                               */
            /*                                                                */
            /* Arguments:                                                     */
            /*     objSender : the menu item (unused).                        */
            /*     evArgs    : unused.                                        */
            /*                                                                */
            /* Returns:                                                       */
            /*     void : the pane is sorted descending.                      */
            /*----------------------------------------------------------------*/
            internal void MPB_OnSortDescendingClick(object objSender, EventArgs evArgs)
            {
                /* The column kept, descending. */
                Mpb_objOwner.Main_SetSort(Mpb_iSide, Mpb_objOwner.Main_asrtSide[Mpb_iSide].PSR_iColumn, true);
            }

            /*----------------------------------------------------------------*/
            /* MPB_RunItemChecks:                                             */
            /*                                                                */
            /* The tick sync Main_PostItemChecks posts for this pane, run     */
            /* once the burst of selection events is over.                    */
            /*                                                                */
            /* Arguments:                                                     */
            /*     None.                                                      */
            /*                                                                */
            /* Returns:                                                       */
            /*     void : the pane's ticks match its selection.               */
            /*----------------------------------------------------------------*/
            internal void MPB_RunItemChecks()
            {
                /* The flag cleared first, so a change made while this runs   */
                /* posts again; then the ticks set from the selection.        */
                Mpb_objOwner.Main_abChecksPending[Mpb_iSide] = false;
                Mpb_objOwner.Main_ApplyItemChecks(Mpb_objOwner.Main_SideList(Mpb_iSide));
            }
        }

        /* Layout constants.  The divider's position is kept as a share of     */
        /* the width, in thousandths, so the panes keep their proportions at   */
        /* any window size; the columns are sized from their content.          */
        private const int MAIN_DEFAULT_WIDTH     = 1280;   // first-run window width, pixels (wide enough that the image pane's six columns fit without scrolling)
        private const int MAIN_DEFAULT_HEIGHT    = 650;    // first-run window height, pixels
        private const int MAIN_MIN_WIDTH         = 640;    // smallest window width, pixels
        private const int MAIN_MIN_HEIGHT        = 360;    // smallest window height, pixels
        private const int MAIN_DEFAULT_SPLIT     = 500;    // first-run image-pane share of the width, thousandths
        private const int MAIN_REPORT_LINES      = 15;     // most problem lines listed in a copy's closing message

        /* Fixed names and texts -- documentation table: the up row, the      */
        /* empty path box's prompt, the private drag and clipboard formats,   */
        /* and the temporary folders' naming.                                 */
        private const string MAIN_PARENT_NAME    = "..";                                                              // the row that goes up one level, in both panes
        private const string MAIN_NO_IMAGE_TEXT  = "(no image open -- click the folder icon to open one)";            // what the image path box shows while no image is open
        private const string MAIN_NO_IMAGE_SIDE  = "(IMAGE)";                                                         // the image path row's side label while no image is open (Main_ShowNoImage); an open filesystem's type replaces it (Main_ApplyOpenResult)
        private const string MAIN_NO_IMAGE_STATUS = "Open an ext2 / ext3 / ext4, squashfs, FAT or exFAT image file to begin.";  // the status line with nothing open: at start-up, and in place of a closed image's summary (Main_ShowNoImage)
        private const string MAIN_DRAG_IMAGE_FORMAT = "BifrostFM.ImagePaneSelection";                                 // private drag format: "the image pane's selection"
        private const string MAIN_CLIP_EFFECT_FORMAT = "Preferred DropEffect";                                        // the shell's clipboard format saying whether a paste should copy or move (how a cut is told from a copy)
        private const string MAIN_DRAG_TEMP_PREFIX  = "BifrostFM-drag-";                                              // prefix of a temporary folder under %TEMP% -- a drag's, or an opened file's: then the process id, a dash, a random part
        private const string MAIN_OPEN_TEMP_NAME    = "content";                                                      // the name an opened image entry's content is given in its temporary folder (its own name might be one Windows cannot hold)

        /* How an image entry is opened, and the two sizes Open decides by   */
        /* -- documentation table.                                           */
        private const int  MAIN_OPEN_AUTO        = 0;                               // open an image entry in whichever window its content calls for
        private const int  MAIN_OPEN_TEXT        = (int)VWH_clsViewerHow.VWH_TEXT;  // open it in the text editor -- the command system's own value (CommandOperations.cs), so an Open As choice is handed to it unchanged
        private const int  MAIN_OPEN_HEX         = (int)VWH_clsViewerHow.VWH_HEX;   // open it in the hex editor, likewise the command system's value
        private const int  MAIN_OPEN_EDITOR      = 100;                             // plus a VoorheesFormat.VF_ id: open it in the structured editor as that format (which refuses a file not valid in it; the text editor is offered then)
        private const long MAIN_DETECT_MAX_BYTES = 4L * 1024 * 1024;                // a text file no larger than this, with no telling name, is parsed to see whether it is JSON before a plain Open chooses its window; a larger one goes to the text editor
        private const long MAIN_OPEN_BUSY_BYTES  = 8L * 1024 * 1024;                // an entry of this size or more is read out of the image under a "working" box; a smaller one is too quick to need it

        /* What Commands > New makes, and the limits it works with --   */
        /* documentation table.                                         */
        private const int  MAIN_NEW_TEXT         = 0;                         // Commands > New > Text File: an empty file, in the text editor
        private const int  MAIN_NEW_HEX          = 1;                         // Commands > New > Hex File: a file of zeros of a size the user gives, in the hex editor
        private const int  MAIN_NEW_JSON         = 2;                         // Commands > New > JSON File: an empty JSON document, in the structured editor
        private const int  MAIN_NEW_INI          = 3;                         // Commands > New > INI File: an empty INI document, in the structured editor
        private const long MAIN_NEW_HEX_MAX      = 32L * 1024 * 1024 * 1024;  // the largest hex file New makes, bytes (32 GiB): the file's length is set, not written (SetLength -- the disk must still have room for it), the hex editor pages (its scroll bar scales past 16 GiB), and in the image the zeros are imported under the copy window

        /* Column sizing -- documentation table. */
        private const int MAIN_MEASURE_ROWS      = 300;    // most rows measured when sizing columns to their content
        private const int MAIN_CELL_PAD          = 16;     // added to a column's measured text width for the cell's margins, pixels
        private const int MAIN_NAME_MIN_CHARS    = 14;     // the Name column is never sized automatically below its icon plus this many average characters of the list's font

        /* The Tiles view's tile size -- documentation table. */
        private const int MAIN_TILE_NAME_CHARS   = 28;     // a tile is made wide enough for a name of this many average characters beside its icon
        private const int MAIN_TILE_LINES        = 3;      // text lines a tile has room for: the name and two details
        private const int MAIN_TILE_PAD          = 8;      // added to a tile's width and height for its margins, pixels

        /* The two panes' indexes, the toolbars' layout and the lists'   */
        /* first text size -- documentation table.                       */
        private const int MAIN_SIDE_IMAGE        = 0;   // index of the image (left) pane in the per-pane toolbar arrays
        private const int MAIN_SIDE_HOST         = 1;   // index of the Windows (right) pane in the per-pane toolbar arrays
        private const int MAIN_SIDES             = 2;   // how many panes there are
        private const int MAIN_PATH_ICON_PAD     = 6;   // added to the small icon's width for the path row's folder icon, pixels
        private const int MAIN_SHARED_SEPARATOR  = 4;   // position, on the shared toolbar, of the separator between the two copy buttons and Refresh (after the two that open it and the two buttons)
        private const int MAIN_FONT_DEFAULT      = 10;  // the lists' text size on a first run, points
        private const int MAIN_PANE_OUTLINE      = 2;   // thickness of the outline round each pane, which is coloured on the active one, pixels

        /* The Windows pane's Type column, and its icon work, done a   */
        /* little at a time -- documentation table.                    */
        private const int MAIN_HOST_COLUMN_TYPE  = 2;      // the Windows pane's Type column (see Main_asHostColumns)
        private const int MAIN_ICON_TICK_MS      = 15;     // how often the Windows pane's stand-in icons are worked on, milliseconds
        private const int MAIN_ICON_SLICE_MS     = 10;     // longest one such turn asks the shell for, milliseconds (the rest of each tick is the user's)

        /* The Back history's length and the toolbar pictures' scale --   */
        /* documentation table.                                           */
        private const int MAIN_BACK_LIMIT        = 50;     // most folders a pane's Back history keeps; the oldest is dropped beyond it
        private const int MAIN_TOOL_ICON_NUM     = 3;      // toolbar pictures are this many ...
        private const int MAIN_TOOL_ICON_DEN     = 2;      // ... over this many times the system's small icon size (one and a half times)

        /* The two panes' column headings, left to right.  Column 0 is the   */
        /* name and column 1 the size in both.                               */
        private static readonly string[] Main_asImageColumns = { "Name", "Size", "Type", "Mode", "Owner", "Modified" };   // the image pane's: an ext entry's mode and owner have columns of their own
        private static readonly string[] Main_asHostColumns  = { "Name", "Size", "Type", "Modified" };                    // the Windows pane's: no mode or owner (Type is MAIN_HOST_COLUMN_TYPE)

        /* The five views, in the order every view menu lists them, and each   */
        /* one's menu text (an ampersand marks its access key).                */
        private static readonly View[]   Main_aenmViewOrder  = { View.LargeIcon, View.SmallIcon, View.List, View.Details, View.Tile };  // the views, in menu order
        private static readonly string[] Main_asViewTexts = { "Lar&ge Icons", "S&mall Icons", "&List", "&Details", "&Tiles" };          // each one's menu text, indexed alike

        /* The sizes View > Text Size offers for the lists' text, points. */
        private static readonly int[] Main_aiFontSizes = { 9, 10, 11, 12, 14 };   // MAIN_FONT_DEFAULT is one of them

        /* The structured editor's formats as Open As offers them, in menu    */
        /* order, and each one's menu text (an ampersand marks its access     */
        /* key).  The registry format is left out: the editor reads it, but   */
        /* Open As does not offer it.                                         */
        private static readonly int[]    Main_aiEditorFormats = { VoorheesFormat.VF_JSON, VoorheesFormat.VF_INI, VoorheesFormat.VF_KLIPPER,
                                                                  VoorheesFormat.VF_MOONRAKER, VoorheesFormat.VF_KLIPPERSCREEN };                      // the formats, in menu order (each item's Tag is MAIN_OPEN_EDITOR plus its format's id)
        private static readonly string[] Main_asEditorTexts   = { "&JSON", "&INI", "&Klipper config", "&Moonraker config", "Klipper&Screen config" };  // each one's menu text, indexed alike

        /* The kinds of file Commands > New makes, in menu order (indexed by   */
        /* MAIN_NEW_*): each one's menu text and the name offered for it.      */
        private static readonly string[] Main_asNewTexts = { "&Text File...", "&Hex File...", "&JSON File...", "&INI File..." };  // each kind's menu text
        private static readonly string[] Main_asNewNames = { "new.txt", "new.bin", "new.json", "new.ini" };                       // the name first offered for each

        /* Controls. */
        private MenuStrip            Main_mnuMain;             // the menu bar
        private ToolStripMenuItem    Main_tsmFile;             // File menu
        private ToolStripMenuItem    Main_tsmNewImage;         // File > New Image File...
        private ToolStripMenuItem    Main_tsmPartition;        // File > Partition/Format (the submenu; greyed only while the worker is busy or a check or partition window is up -- with an image or device open it asks to close it)
        private ToolStripMenuItem    Main_tsmPartitionFile;    // File > Partition/Format > Image File...
        private ToolStripMenuItem    Main_tsmPartitionDevice;  // File > Partition/Format > Device...
        private ToolStripMenuItem    Main_tsmOpenMenu;         // File > Open (the submenu; Main_tsmOpen is the panes' own Open item)
        private ToolStripMenuItem    Main_tsmOpenImage;        // File > Open > Image File... (a filesystem image or a whole-disk dump alike: the table is detected)
        private ToolStripMenuItem    Main_tsmOpenDevice;       // File > Open > Device...
        private ToolStripMenuItem    Main_tsmReadDevice;       // File > Save Device to Image File...
        private ToolStripMenuItem    Main_tsmWriteDevice;      // File > Write Image File to Device...
        private ToolStripMenuItem    Main_tsmCloseImage;       // File > Close Image
        private ToolStripMenuItem    Main_tsmEnableWrite;      // File > Enable Writing to Image... (enabled only while an image is open read-only)
        private ToolStripMenuItem    Main_tsmCheckFs;          // File > Check Filesystem... (enabled while no check window is, greyed while a FAT or exFAT filesystem is open; with nothing open it asks for an image file or a device)
        private ToolStripMenuItem    Main_tsmFsProperties;     // File > Filesystem Properties... (enabled only while an image is open)
        private CHK_clsCheckForm        Main_frmCheck;         // the open Check Filesystem window, or null when there is none; one at a time
        private ToolStripMenuItem    Main_tsmExit;             // File > Exit
        private ToolStripMenuItem    Main_tsmEdit;             // Edit menu
        private ToolStripMenuItem    Main_tsmSelectAll;        // Edit > Select All (Ctrl+A shown; the lists take the key themselves)
        private ToolStripMenuItem    Main_tsmItemChecks;       // View > Item Check Boxes (a remembered tick mark)
        private bool                 Main_bSyncingChecks;      // true while Main_ApplyItemChecks or a tick handler is setting ticks or selection, so the other handler does not answer back
        private bool[]               Main_abChecksPending;     // per pane (MAIN_SIDE_*): true while a tick sync is posted and has not yet run (selection changes arrive one per row)
        private ToolStripMenuItem    Main_tsmCut;              // Edit > Cut (greyed for the image pane unless the image is open for writing)
        private ToolStripMenuItem    Main_tsmCopy;             // Edit > Copy
        private ToolStripMenuItem    Main_tsmPaste;            // Edit > Paste
        private ContextMenuStrip     Main_cmsPane;             // the right-click menu both lists share; it acts on the list it was opened over
        private ToolStripMenuItem    Main_tsmCtxCut;           // right-click > Cut
        private ToolStripMenuItem    Main_tsmCtxCopy;          // right-click > Copy
        private ToolStripMenuItem    Main_tsmCtxPaste;         // right-click > Paste
        private ToolStripMenuItem    Main_tsmCtxDelete;        // right-click > Delete
        private ToolStripMenuItem    Main_tsmCtxRename;        // right-click > Rename...
        private ToolStripMenuItem    Main_tsmCtxNewFolder;     // right-click > New Folder...
        private ToolStripMenuItem    Main_tsmCtxTransfer;      // right-click > Extract to Windows (image pane) or Import into Image (Windows pane)
        private ToolStripMenuItem    Main_tsmCtxProperties;    // right-click > Properties... (either pane; in the image pane with nothing selected it is Filesystem Properties...)
        private ToolStripMenuItem    Main_tsmCtxRefresh;       // right-click > Refresh
        private ToolStripMenuItem    Main_tsmCtxSort;          // right-click > Sort By: a sub-menu of the pane's columns and the two directions, refilled as the menu opens
        private ToolStripMenuItem    Main_tsmCtxView;          // right-click > View: a sub-menu of the five views for the pane the menu opened over, refilled as the menu opens
        private PSR_clsPaneSorter[]     Main_asrtSide;         // per pane: the order of its list's rows (the column and the direction)
        private ToolStripMenuItem    Main_tsmView;             // View menu
        private ToolStripMenuItem    Main_tsmViewImage;        // View > Left Pane (Image): a sub-menu of the five views, for the image pane
        private ToolStripMenuItem    Main_tsmViewHost;         // View > Right Pane (Windows): a sub-menu of the five views, for the Windows pane
        private ToolStripMenuItem    Main_tsmRefresh;          // View > Refresh
        private ToolStripMenuItem    Main_tsmCommands;         // Commands menu
        private ToolStripMenuItem    Main_tsmExtract;          // Commands > Extract to Windows
        private ToolStripMenuItem    Main_tsmImport;           // Commands > Import into Image
        private ToolStripMenuItem    Main_tsmNewFolder;        // Commands > New Folder... (in the pane the focus was last in, image or Windows)
        private ToolStripMenuItem    Main_tsmRename;           // Commands > Rename... (likewise either pane)
        private ToolStripMenuItem    Main_tsmDelete;           // Commands > Delete... (likewise either pane)
        private ToolStripMenuItem    Main_tsmProperties;       // Commands > Properties
        private ToolStripMenuItem    Main_tsmOpen;             // Commands > Open (what a double-click does, in the active pane)
        private ToolStripMenuItem    Main_tsmOpenAs;           // Commands > Open As: a sub-menu naming the window for the active pane's selected file (text, hex, or the structured editor as one of its formats); each item's Tag is its MAIN_OPEN_* value
        private ToolStripMenuItem    Main_tsmNew;              // Commands > New: a sub-menu of the kinds of file to make in the active pane; each item's Tag is its MAIN_NEW_* value
        private ToolStripMenuItem    Main_tsmCtxOpen;          // right-click > Open
        private ToolStripMenuItem    Main_tsmCtxOpenAs;        // right-click > Open As (the same sub-menu, made again: a menu item has one parent)
        private ToolStripMenuItem    Main_tsmCtxNew;           // right-click > New (likewise)
        private ToolStripMenuItem    Main_tsmHelp;             // Help menu
        private ToolStripMenuItem    Main_tsmAbout;            // Help > About BifrostFM
        private ABT_clsAboutForm        Main_frmAbout;         // the open About window, or null when it is not open
        private ToolStripMenuItem    Main_tsmDebugLog;         // Help > Debug Log
        private DBGLOGF_clsDebugLogForm Main_frmDebugLog;      // the open debug log window, or null when it is not open
        private ToolStripMenuItem    Main_tsmOptions;          // Options menu
        private ToolStripMenuItem    Main_tsmOptMetaOut;       // Options > write the metadata manifest when extracting (a tick mark)
        private ToolStripMenuItem    Main_tsmOptMetaIn;        // Options > restore metadata from a manifest when importing (a tick mark)
        private ToolStripMenuItem    Main_tsmOptSetTimes;      // Options > give extracted files the image's modify times (a tick mark)
        private ToolStripMenuItem    Main_tsmOptFollowLinks;   // Options > extracting follows symbolic links (a tick mark, off by default)
        private ToolStripMenuItem    Main_tsmOptEditorOpen;    // Options > a plain Open of a text file the program can identify as a config format (by its name, or JSON by its content) uses the structured editor (a tick mark)
        private ToolStripMenuItem    Main_tsmOptVerify;        // Options > an ordinary session's writes to a device are read back (a tick mark; off by default)
        private ToolStripMenuItem    Main_tsmOptWarnings;      // Options > Risk Warning Enable (a tick mark set by hand, not on click: unticking asks twice)
        private ToolStripMenuItem    Main_tsmBack;             // View > Back (the active pane's previous folder)
        private ToolStripMenuItem    Main_tsmUp;               // View > Up One Level (in the active pane)
        private ToolStripMenuItem    Main_tsmTextSize;         // View > Text Size: a sub-menu of the sizes the lists' text can be shown at
        private ToolStripMenuItem    Main_tsmShowHost;         // View > Show Windows Pane (a tick mark; unticked hides the right pane and its toolbar)
        private ToolStripMenuItem    Main_tsmToolIcons;        // View > Toolbar Icons: a sub-menu of the two choices below
        private ToolStripMenuItem    Main_tsmIconsAuto;        // View > Toolbar Icons > Automatic (small pictures only when the window is too narrow for the large ones)
        private ToolStripMenuItem    Main_tsmIconsSmall;       // View > Toolbar Icons > Always Small
        private ToolStripMenuItem    Main_tsmChooseFolder;     // View > Choose Folder... (the active pane's folder picker; what clicking its folder icon does)
        /* The toolbar row, under the menu bar: THREE toolbars on one panel.   */
        /* Each pane has its own (the image pane's flush left, the Windows     */
        /* pane's flush right) whose buttons always act on THAT pane; the      */
        /* buttons that concern both panes sit in a third, centred between     */
        /* them.  The per-pane controls are pairs indexed by MAIN_SIDE_*.      */
        private Panel                     Main_pnlTools;        // the row the three toolbars sit on
        private ToolStrip[]               Main_atspSide;        // each pane's own toolbar
        private ToolStrip                 Main_tspShared;       // the centred toolbar of buttons that concern both panes
        private ToolStripButton[]         Main_atsbBack;        // per pane: back
        private ToolStripButton[]         Main_atsbUp;          // per pane: up one level
        private ToolStripButton[]         Main_atsbCut;         // per pane: cut
        private ToolStripButton[]         Main_atsbCopy;        // per pane: copy
        private ToolStripButton[]         Main_atsbPaste;       // per pane: paste
        private ToolStripButton[]         Main_atsbNewFolder;   // per pane: new folder
        private ToolStripDropDownButton[] Main_atsdNew;         // per pane: New, a pull-down of the kinds of file to make there (the same four as Commands > New)
        private ToolStripButton[]         Main_atsbRename;      // per pane: rename
        private ToolStripButton[]         Main_atsbDelete;      // per pane: delete (files and folders alike)
        private ToolStripDropDownButton[] Main_atsdViews;       // per pane: a pull-down of the five views, its text the view now showing
        private ToolStripDropDownButton   Main_tsdOpen;         // image pane's toolbar: Open, a pull-down of Image File... and Device..., the two File > Open items
        private ToolStripButton           Main_tsbClose;        // image pane's toolbar: close the open image or device (File > Close Image/Device)
        private ToolStripButton           Main_tsbWrite;        // image pane's toolbar: enable writing to the image
        private ToolStripButton[]         Main_atsbProperties;  // per pane: properties (the image entry's own window, or Windows' for a Windows item)
        private ToolStripButton           Main_tsbNewImage;     // Windows pane's toolbar: create a new image file
        private ToolStripDropDownButton   Main_tsdPlaces;       // Windows pane's toolbar: a pull-down of common locations (This PC, the user's folders, the drives), refilled before each showing (Main_ShowPlaces)
        private bool                      Main_bPlacesShowing;  // Main_ShowPlaces is opening the Places pull-down itself: its Opening is let through (set and lowered round the show)
        private ToolStripButton           Main_tsbToImage;      // shared toolbar: copy to the left pane (import the Windows selection)
        private ToolStripButton           Main_tsbToHost;       // shared toolbar: copy to the right pane (extract the image selection)
        private ToolStripButton           Main_tsbRefresh;      // shared toolbar: refresh both panes
        private Panel                     Main_pnlImagePath;    // the image pane's path row: the folder icon, the path box, its side label (Main_lblImageSide)
        private Label                     Main_lblImageSide;    // the image pane's side label: the open filesystem's type in capitals, "(EXT4)", "(FAT32)" ..., or MAIN_NO_IMAGE_SIDE with nothing open
        private Panel                     Main_pnlHostPath;     // the Windows pane's path row: the folder icon, the path box, "(WINDOWS)"
        private PictureBox                Main_picImagePath;    // the folder icon before the image path box; click it for the picker, or (nothing open) the open menu
        private ContextMenuStrip          Main_cmsOpenChoice;   // that icon's menu with nothing open: Image File... / Device..., the two File > Open items
        private ToolStripMenuItem         Main_tsmCheckImage;   // File > Check Filesystem > Image File..., the sub-menu it has only while nothing is open
        private ToolStripMenuItem         Main_tsmCheckDevice;  // File > Check Filesystem > Device..., likewise
        private PictureBox                Main_picHostPath;     // the folder icon before the Windows path box; click it for the picker
        private ToolTip                   Main_ttTips;          // the tool tips of the two folder icons
        private SplitContainer       Main_splPanes;             // holds the two panes; the user drags the divider
        private TextBox              Main_txtImagePath;         // the image pane's current directory
        private ListView             Main_lstImage;             // the image pane's entries
        private ImageList            Main_imlEntrySmall;        // the image pane's own icons at the panes' small size, which follows View > Text Size (details, list, small icons); remade when the size changes
        private ImageList            Main_imlEntryLarge;        // the same at the panes' large size (large icons, tiles)
        private ImageList            Main_imlDialogIcons;       // the program's own icons at the SYSTEM's small size, made once, for the dialogs that show image entries (the Save and folder pickers): a dialog's icons follow its own font, not the panes' Text Size
        private int                  Main_iHostUpIcon;          // the Windows pane's ".." picture: its index in SHI_ShellIcons' lists, which redraw it at every size change
        private Font                 Main_fntList;              // the panes' text font -- both lists and both path rows -- at View > Text Size; owned here, replaced (and the old one disposed) when the size changes
        private TextBox              Main_txtHostPath;          // the host pane's current folder
        private ListView             Main_lstHost;              // the host pane's entries
        private StatusStrip          Main_stsStatus;            // the status bar
        private ToolStripStatusLabel Main_tslStatus;            // its one text label

        /* State.  All of it belongs to the UI thread. */
        private Font         Main_fntDialog;      // the program's dialog font, given by Main; handed to every dialog and editor this window opens (not owned here)
        private Font         Main_fntHeading;     // the program's warning-heading font (bold, larger), given by Main; handed to a message box that opens with a warning heading (not owned here)
        private bool         Main_bImageOpen;     // true while an image is open in the engine
        private bool         Main_bReadOnly;      // true while the open image is open read-only (how every image starts)
        private bool         Main_bHostPaneActive;// true when the focus was last in the Windows pane; New Folder / Rename / Delete act on that pane
        private string       Main_sImageFile;      // the open image's host path, or the open device's path; "" when none
        private string       Main_sDeviceText;     // when the open image is a real device: its model, size, bus and path for the title and the write warning; "" for a file
        private ulong        Main_ullImageOffset;  // where the open filesystem starts in that file, bytes (0: the file is the filesystem)
        private ulong        Main_ullImageLength;  // the open filesystem's length, bytes (0: to the end of the file)
        private string       Main_sImageDir;       // the image directory shown in the left pane
        private string       Main_sHostDir;        // the Windows folder shown in the right pane; "" means the list of drives
        private string       Main_sDriveRequest;   // a Windows folder typed into the image path box, recorded by its key handler for Main_RunDriveRequest to go to from the loop; "" when none waits
        private string       Main_sVolumeText;     // the open image's summary line for the status bar
        private string       Main_sStartImage;     // an image named on the command line, opened once the window is up; "" for none
        private int          Main_iSplitPermille;  // the image pane's share of the width, thousandths; changed ONLY by the user dragging the divider
        private bool         Main_bUserSplitting;  // true from the moment the USER starts moving the divider until that move is recorded; a move with it false is the program's own
        private bool         Main_bSizingColumns;  // true while Main_SizeColumns is running (the widths it sets can change a list's client size, which would call it again)
        private List<string> Main_lstDragFolders;  // the %TEMP% folders this run made: drops on other programs, and every Main_NewTempFolder (the command system's work folder, New File's stages); removed only at the end of Main_OnFormClosing
        private List<Form>   Main_lstViewers;      // the editor and viewer windows now open (on image entries, or Windows files opened directly); each takes itself off when it closes
        private string       Main_sCheckFile;      // the file or device the open check window is checking: the image that was closed for it, or one picked with nothing open; "" when no check window is open
        private bool         Main_bCheckReopen;    // true when the check closed an open image, which is reopened when its window closes; false when it was picked with nothing open, so nothing is opened afterwards
        private ulong        Main_ullCheckOffset;  // its filesystem's byte offset, as it was open
        private ulong        Main_ullCheckLength;  // its filesystem's byte length, as it was open (0: to the end)
        private string       Main_sCheckDeviceText;// its device description when it is a device (Main_sDeviceText as it was); "" for a file
        private List<string> Main_lstImageBack;       // the image directories Back returns to, oldest first; emptied when an image is opened or closed
        private List<string> Main_lstHostBack;        // the Windows folders Back returns to, oldest first ("" is the drive list)
        private bool         Main_bImageBackNav;      // true while the image listing in flight was asked for by Back (so it is not itself added to the history)
        private bool         Main_bHostBackNav;       // the same, for the Windows pane's navigation
        private bool         Main_bHostRefusalShown;  // true while the status line shows the Windows pane's own "Cannot show ..." refusal; a later successful navigation clears it, any other status line replaces it (Main_SetStatus)
        private bool         Main_bToolbarPending;// true while a toolbar state update is posted and has not yet run (selection changes arrive in bursts)
        private List<ToolStripMenuItem>[] Main_alstViewItems;  // per pane: its view items, in its View sub-menu and in its toolbar's pull-down; the mark is set on all that match
        private List<ToolStripItem> Main_lstToolButtons;       // every picture button and picture pull-down on the three toolbars; each one's Tag is its TIC_ICON_* value, so its picture can be drawn again at another size
        private bool               Main_bToolIconsSmall;       // true while the toolbars are showing the small pictures
        private bool               Main_bToolLayoutBusy;       // true while Main_LayoutToolbars is running (it changes sizes that would otherwise call it again)
        private int[][]            Main_aaiDetailWidths;       // per pane: the measured width of each detail column (index 0, the Name column, is unused); null while stale -- the rows or the font changed since it was measured
        private int[][]            Main_aaiUserWidths;         // per pane: the width the USER gave each column by dragging its divider, pixels; 0 for a column still sized automatically (remembered between runs)
        private ToolStripMenuItem  Main_tsmResetColumns;       // View > Reset Column Widths (back to automatic, in both panes)
        private Timer              Main_tmrIcons;              // ticks while Windows-pane rows still show a stand-in icon; each tick asks the shell about a few
        private List<ListViewItem> Main_lstIconPending;        // the Windows pane's rows whose own icon has not been asked for yet, in the order they will be
        private int                Main_iIconNext;             // index in Main_lstIconPending of the next row to ask about

        /*--------------------------------------------------------------------*/
        /* MAIN_clsMainForm (constructor):                                    */
        /*                                                                    */
        /* Build the window and restore its remembered placement.  Nothing    */
        /* that needs the window handle happens here -- the command worker is */
        /* started, and a start-up image opened, from Main_OnShown.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sStartImage : an image file to open at start-up, or "".        */
        /*     fntDialog   : the program's dialog font, made once by Main     */
        /*                   (APP_DIALOG_POINTS_UP over the system message    */
        /*                   box font); every dialog and editor this window   */
        /*                   opens is handed it.  Main owns it.               */
        /*     fntHeading  : the program's warning-heading font, made once by */
        /*                   Main (APP_HEADING_POINTS_UP, bold); handed to a  */
        /*                   message box that opens with a warning heading.   */
        /*                   Main owns it.                                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the form, not yet shown.                       */
        /*--------------------------------------------------------------------*/
        internal MAIN_clsMainForm(string sStartImage, Font fntDialog, Font fntHeading)
        {
            byte[] abIcon;

            /* The starting state, every field set before any control can     */
            /* raise an event that reads one: the two fonts kept (Main owns   */
            /* them); nothing open (read-only is how every image starts);     */
            /* no viewers, check window or drag folders; empty Back           */
            /* histories; the remembered column widths loaded; the image      */
            /* pane at "/" and the Windows pane at the drive list until the   */
            /* placement and the views are restored.                          */
            Main_fntDialog      = fntDialog;
            Main_fntHeading     = fntHeading;
            Main_frmAbout       = null;
            Main_frmDebugLog    = null;
            Main_bImageOpen     = false;
            Main_bReadOnly      = true;
            Main_sImageFile     = "";
            Main_sDeviceText    = "";
            Main_ullImageOffset = 0;
            Main_ullImageLength = 0;
            Main_bHostPaneActive = false;
            Main_lstDragFolders = new List<string>();
            Main_lstViewers     = new List<Form>();
            Main_frmCheck       = null;
            Main_sCheckFile     = "";
            Main_ullCheckOffset = 0;
            Main_ullCheckLength = 0;
            Main_sCheckDeviceText = "";
            Main_lstImageBack   = new List<string>();
            Main_lstHostBack    = new List<string>();
            Main_alstViewItems  = new List<ToolStripMenuItem>[MAIN_SIDES];
            Main_alstViewItems[MAIN_SIDE_IMAGE] = new List<ToolStripMenuItem>();
            Main_alstViewItems[MAIN_SIDE_HOST]  = new List<ToolStripMenuItem>();
            Main_bImageBackNav  = false;
            Main_bHostBackNav   = false;
            Main_bToolbarPending = false;
            Main_lstToolButtons  = new List<ToolStripItem>();
            Main_bToolIconsSmall = false;
            Main_bToolLayoutBusy = false;
            Main_bUserSplitting  = false;
            Main_bSizingColumns  = false;
            Main_aaiDetailWidths = new int[MAIN_SIDES][];
            Main_aaiUserWidths   = new int[MAIN_SIDES][];
            Main_aaiUserWidths[MAIN_SIDE_IMAGE] = Main_LoadUserWidths(SET_Settings.SET_IMAGE_COLUMNS, Main_asImageColumns.Length);
            Main_aaiUserWidths[MAIN_SIDE_HOST]  = Main_LoadUserWidths(SET_Settings.SET_HOST_COLUMNS, Main_asHostColumns.Length);
            Main_lstIconPending  = new List<ListViewItem>();
            Main_iIconNext       = 0;
            Main_sImageDir   = "/";
            Main_sHostDir    = "";
            Main_sDriveRequest = "";
            Main_sVolumeText = "";
            Main_sStartImage = sStartImage;

            /* The panes' font is made by Main_RestoreListFont, below. */
            Main_fntList = null;

            /* The controls built, then the remembered state put back in     */
            /* this order: the window's place and size, the lists' text      */
            /* size, each pane's view, and whether the Windows pane shows.   */
            Main_BuildControls();
            Main_RestorePlacement();
            Main_RestoreListFont();
            Main_RestoreViews();
            Main_ApplyHostShown();

            /* The window's icon: the program icon's own .ico bytes, every       */
            /* size in it (ENG_ICON_DATA_ID in engine.rc), copied out of the     */
            /* running exe's resources -- no file is read, so a copy run from    */
            /* a network share has it too.  Built from the WHOLE .ico, the       */
            /* Icon keeps every size, and the framework takes the size each      */
            /* place needs from it: the 16-pixel picture (the sword) for the     */
            /* title bar's corner, the 32-pixel one (the penguin) for the        */
            /* taskbar and Alt+Tab.  An icon loaded as ONE picture -- read       */
            /* back from the exe's file, or one size of its ICON resource --     */
            /* is the alternative passed over: the corner would be that one      */
            /* picture shrunk.  The program's dialogs take this window's icon    */
            /* (frmX.Icon = this.Icon, or their owner's, which is this one's),   */
            /* so this one load gives them theirs.  The Icon reads all the       */
            /* bytes in, so the stream goes at once (framework behaviour, as     */
            /* found).                                                           */
            abIcon = ENG_clsNative.ENG_ReadResource(ENG_clsNative.ENG_ICON_DATA_ID);

            if (abIcon != null)
            {   /* Read: the window shows it.  Failing that (a build with no   */
                /* such resource, or one that cannot be read), the             */
                /* framework's default icon is kept.                           */
                try
                {   /* Bytes that are there but not an icon the framework can   */
                    /* read make new Icon throw (as found; the exception's      */
                    /* type not established here) -- caught below, so a bad     */
                    /* icon can never stop the program starting.                */
                    using (MemoryStream stmIcon = new MemoryStream(abIcon))
                    {   /* The bytes as a stream, released when the block ends. */
                        this.Icon = new Icon(stmIcon);
                    }
                }
                catch (Exception)
                {   /* Not a readable icon: the framework's default icon is   */
                    /* kept, as for a missing resource.                       */
                }
            }

            /* The events that drive the window's own life. */
            this.Shown       += Main_OnShown;
            this.Resize      += Main_OnResize;
            this.FormClosing += Main_OnFormClosing;

            /* Activation: a list's focus, and an editor's place in front,   */
            /* given back after a command (Main_OnMainActivated).            */
            this.Activated   += Main_OnMainActivated;
        }

        /*--------------------------------------------------------------------*/
        /* Main_BuildControls:                                                */
        /*                                                                    */
        /* Create every control and parent each to the container it visibly   */
        /* sits in.  Everything is placed by DOCKING, so no control carries a */
        /* coordinate and the layout follows the window at any size.          */
        /*                                                                    */
        /* The order controls are added in is the order they are docked in    */
        /* REVERSE: the control meant to FILL what is left is added first,    */
        /* the edge-docked ones after it.                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the form's controls exist.                              */
        /*--------------------------------------------------------------------*/
        private void Main_BuildControls()
        {
            Label lblHostSide;

            /* The caption; a position the program sets itself (the       */
            /* remembered one, Main_RestorePlacement); and the smallest   */
            /* size the window can be made.                               */
            this.Text          = "BifrostFM";
            this.StartPosition = FormStartPosition.Manual;
            this.MinimumSize   = new Size(MAIN_MIN_WIDTH, MAIN_MIN_HEIGHT);

            /* The menu.  The ampersands are access keys (Alt+F, Alt+V).  The    */
            /* File menu's order: New Image File, the Partition/Format           */
            /* submenu, the Open submenu, Close.                                 */
            /*                                                                   */
            /* New Image File (Ctrl+N) first: it asks for a file and a size      */
            /* (NewImageForm.cs in its size-only mode), creates the empty file   */
            /* and hands it to the partition window, where its table and         */
            /* filesystem are made.                                              */
            Main_tsmNewImage              = new ToolStripMenuItem("&New Image File...");
            Main_tsmNewImage.ShortcutKeys = Keys.Control | Keys.N;
            Main_tsmNewImage.Click       += Main_OnNewImageClicked;

            /* The partition window, over a disk image or a device: where     */
            /* every filesystem is made, a new image's included.  Usable      */
            /* whenever the worker is idle and no check or partition window   */
            /* is up, and greyed otherwise (Main_ApplyCommandAvailability,    */
            /* MainCommand.cs); with an image or device open, its sequence    */
            /* asks to close it -- each editor on an image entry asked to     */
            /* save, discard or cancel -- once the disk has been chosen.      */
            Main_tsmPartitionFile        = new ToolStripMenuItem("&Image File...");
            Main_tsmPartitionFile.Click += Main_OnPartitionFileClicked;

            /* ... and over a device. */
            Main_tsmPartitionDevice        = new ToolStripMenuItem("&Device...");
            Main_tsmPartitionDevice.Click += Main_OnPartitionDeviceClicked;

            /* The Partition/Format submenu holding the two. */
            Main_tsmPartition = new ToolStripMenuItem("&Partition/Format");
            Main_tsmPartition.DropDownItems.Add(Main_tsmPartitionFile);
            Main_tsmPartition.DropDownItems.Add(Main_tsmPartitionDevice);

            /* Open: an image file, or a real device (an SD card, a USB stick,   */
            /* an eMMC adapter).  The shortcuts stay on the submenu's items.     */
            Main_tsmOpenImage              = new ToolStripMenuItem("&Image File...");
            Main_tsmOpenImage.ShortcutKeys = Keys.Control | Keys.O;
            Main_tsmOpenImage.Click       += Main_OnOpenImageClicked;

            /* Open > Device (Ctrl+D). */
            Main_tsmOpenDevice              = new ToolStripMenuItem("&Device...");
            Main_tsmOpenDevice.ShortcutKeys = Keys.Control | Keys.D;
            Main_tsmOpenDevice.Click       += Main_OnOpenDeviceClicked;

            /* The Open submenu holding the two. */
            Main_tsmOpenMenu = new ToolStripMenuItem("&Open");
            Main_tsmOpenMenu.DropDownItems.Add(Main_tsmOpenImage);
            Main_tsmOpenMenu.DropDownItems.Add(Main_tsmOpenDevice);

            /* Close Image/Device: greyed until something is open, while     */
            /* Partition/Format starts enabled, as it stays whether or not   */
            /* something is open (the worker's state narrows both).          */
            Main_tsmCloseImage         = new ToolStripMenuItem("&Close Image/Device");
            Main_tsmCloseImage.Enabled = false;
            Main_tsmPartition.Enabled  = true;
            Main_tsmCloseImage.Click  += Main_OnCloseImageClicked;

            /* Exit. */
            Main_tsmExit        = new ToolStripMenuItem("E&xit");
            Main_tsmExit.Click += Main_OnExitClicked;

            /* Imaging: a device to a file, a file to a device. */
            Main_tsmReadDevice         = new ToolStripMenuItem("&Save Device to Image File...");
            Main_tsmReadDevice.Click  += Main_OnReadDeviceClicked;
            Main_tsmWriteDevice        = new ToolStripMenuItem("&Write Image File to Device...");
            Main_tsmWriteDevice.Click += Main_OnWriteDeviceClicked;

            /* The File menu, its first group: New Image, Partition/Format,   */
            /* Open, Close.                                                   */
            Main_tsmFile = new ToolStripMenuItem("&File");
            Main_tsmFile.DropDownItems.Add(Main_tsmNewImage);
            Main_tsmFile.DropDownItems.Add(Main_tsmPartition);
            Main_tsmFile.DropDownItems.Add(Main_tsmOpenMenu);
            Main_tsmFile.DropDownItems.Add(Main_tsmCloseImage);

            /* The engine's e2fsck over the open image or device, in its own   */
            /* window.  Enabled only while it would do something (set as the   */
            /* File menu opens).                                               */
            Main_tsmCheckFs         = new ToolStripMenuItem("Chec&k Filesystem (e2fsck)...");
            Main_tsmCheckFs.Enabled = true;
            Main_tsmCheckFs.Click  += Main_OnCheckFsClicked;

            /* The open filesystem's facts -- size, use, layout, features --    */
            /* in a window of their own; greyed with nothing open (set as the   */
            /* File menu opens).                                                */
            Main_tsmFsProperties         = new ToolStripMenuItem("Filesystem Propert&ies...");
            Main_tsmFsProperties.Enabled = false;
            Main_tsmFsProperties.Click  += Main_OnFsPropertiesClicked;

            /* The File menu's next groups, each after a divider: Check and   */
            /* Properties; then the two imaging commands.                     */
            Main_tsmFile.DropDownItems.Add(new ToolStripSeparator());
            Main_tsmFile.DropDownItems.Add(Main_tsmCheckFs);
            Main_tsmFile.DropDownItems.Add(Main_tsmFsProperties);
            Main_tsmFile.DropDownItems.Add(new ToolStripSeparator());
            Main_tsmFile.DropDownItems.Add(Main_tsmReadDevice);
            Main_tsmFile.DropDownItems.Add(Main_tsmWriteDevice);

            /* Every image opens read-only.  This is the way to make it     */
            /* writable on purpose, ahead of the first change -- which is   */
            /* what un-greys Cut in the image pane.  It is enabled only     */
            /* while it would do something (set as the File menu opens).    */
            Main_tsmEnableWrite        = new ToolStripMenuItem("Enable &Writing to Image...");
            Main_tsmEnableWrite.Click += Main_OnEnableWriteClicked;

            /* The File menu's last groups: Enable Writing, then Exit; and   */
            /* the items' enabled states set each time the menu opens.       */
            Main_tsmFile.DropDownItems.Add(new ToolStripSeparator());
            Main_tsmFile.DropDownItems.Add(Main_tsmEnableWrite);
            Main_tsmFile.DropDownItems.Add(new ToolStripSeparator());
            Main_tsmFile.DropDownItems.Add(Main_tsmExit);
            Main_tsmFile.DropDownOpening += Main_OnFileMenuOpening;

            /* Edit: cut, copy and paste, acting on the pane the user was   */
            /* last in.  The keys shown beside them are DISPLAY ONLY: the   */
            /* lists handle Ctrl+X / C / V themselves (Main_PaneKeyDown),   */
            /* so the same keys still edit text in the path boxes.          */
            Main_tsmCut                           = new ToolStripMenuItem("Cu&t");
            Main_tsmCut.ShortcutKeyDisplayString  = "Ctrl+X";
            Main_tsmCut.Click                    += Main_OnCutClicked;

            /* Copy, its key likewise for display. */
            Main_tsmCopy                          = new ToolStripMenuItem("&Copy");
            Main_tsmCopy.ShortcutKeyDisplayString = "Ctrl+C";
            Main_tsmCopy.Click                   += Main_OnCopyClicked;

            /* Paste, likewise. */
            Main_tsmPaste                          = new ToolStripMenuItem("&Paste");
            Main_tsmPaste.ShortcutKeyDisplayString = "Ctrl+V";
            Main_tsmPaste.Click                   += Main_OnPasteClicked;

            /* Select All: Ctrl+A, display only like the three above (the       */
            /* lists take the key themselves, so it still selects the text in   */
            /* a path box).                                                     */
            Main_tsmSelectAll                          = new ToolStripMenuItem("Select &All");
            Main_tsmSelectAll.ShortcutKeyDisplayString = "Ctrl+A";
            Main_tsmSelectAll.Click                   += Main_OnSelectAllClicked;

            /* The Edit menu: the three, a divider, Select All; the items'   */
            /* enabled states set each time the menu opens.                  */
            Main_tsmEdit = new ToolStripMenuItem("&Edit");
            Main_tsmEdit.DropDownItems.Add(Main_tsmCut);
            Main_tsmEdit.DropDownItems.Add(Main_tsmCopy);
            Main_tsmEdit.DropDownItems.Add(Main_tsmPaste);
            Main_tsmEdit.DropDownItems.Add(new ToolStripSeparator());
            Main_tsmEdit.DropDownItems.Add(Main_tsmSelectAll);
            Main_tsmEdit.DropDownOpening += Main_OnEditMenuOpening;

            /* View > Refresh: BOTH panes read again where they stand (the   */
            /* image pane only with an image open), not just the active      */
            /* one (Main_OnRefreshClicked).  F5 is a real shortcut here,     */
            /* not display only: the menu takes the key itself.              */
            Main_tsmRefresh              = new ToolStripMenuItem("&Refresh");
            Main_tsmRefresh.ShortcutKeys = Keys.F5;
            Main_tsmRefresh.Click       += Main_OnRefreshClicked;

            /* View: the five ways a pane's list can be shown, in a sub-menu    */
            /* for EACH pane, so a view is always chosen for a named pane and   */
            /* the mark in each sub-menu shows that pane's current view.        */
            Main_tsmViewImage = new ToolStripMenuItem("&Left Pane (Image)");
            Main_AddViewItems(Main_tsmViewImage.DropDownItems, MAIN_SIDE_IMAGE);

            /* The Windows pane's own sub-menu of the same five. */
            Main_tsmViewHost = new ToolStripMenuItem("&Right Pane (Windows)");
            Main_AddViewItems(Main_tsmViewHost.DropDownItems, MAIN_SIDE_HOST);

            /* View: the two ways to move about that are not a double-click.   */
            /* Their keys are display only; the lists handle them              */
            /* (Main_PaneKeyDown).                                             */
            Main_tsmBack                          = new ToolStripMenuItem("&Back");
            Main_tsmBack.ShortcutKeyDisplayString = "Alt+Left";
            Main_tsmBack.Click                   += Main_OnBackClicked;

            /* Up One Level, its key likewise for display. */
            Main_tsmUp                          = new ToolStripMenuItem("&Up One Level");
            Main_tsmUp.ShortcutKeyDisplayString = "Alt+Up";
            Main_tsmUp.Click                   += Main_OnUpClicked;

            /* The path rows' folder icon, reachable from the keyboard: the   */
            /* folder picker for the active pane.                             */
            Main_tsmChooseFolder              = new ToolStripMenuItem("&Choose Folder...");
            Main_tsmChooseFolder.ShortcutKeys = Keys.Control | Keys.G;
            Main_tsmChooseFolder.Click       += Main_OnChooseFolderClicked;

            /* The View menu's first groups: moving about, a divider, the   */
            /* two panes' view sub-menus; the rest is added below.          */
            Main_tsmView = new ToolStripMenuItem("&View");
            Main_tsmView.DropDownItems.Add(Main_tsmBack);
            Main_tsmView.DropDownItems.Add(Main_tsmUp);
            Main_tsmView.DropDownItems.Add(Main_tsmChooseFolder);
            Main_tsmView.DropDownItems.Add(new ToolStripSeparator());
            Main_tsmView.DropDownItems.Add(Main_tsmViewImage);
            Main_tsmView.DropDownItems.Add(Main_tsmViewHost);

            /* The Windows pane can be hidden altogether, for work that stays   */
            /* inside the image; the tick mark is remembered.                   */
            Main_tsmShowHost                 = new ToolStripMenuItem("Show &Windows Pane");
            Main_tsmShowHost.ShortcutKeys    = Keys.F8;
            Main_tsmShowHost.CheckOnClick    = true;
            Main_tsmShowHost.Checked         = (SET_Settings.SET_ReadInt(SET_Settings.SET_SHOW_HOST, 1) == 1);
            Main_tsmShowHost.CheckedChanged += Main_OnShowHostChanged;
            Main_tsmView.DropDownItems.Add(Main_tsmShowHost);

            /* Item check boxes, as Explorer's View option: a box on every      */
            /* row of both panes, the tick and the selection one and the same   */
            /* (Main_ApplyItemChecks); remembered.                              */
            Main_tsmItemChecks                 = new ToolStripMenuItem("Item &Check Boxes");
            Main_tsmItemChecks.CheckOnClick    = true;
            Main_tsmItemChecks.Checked         = (SET_Settings.SET_ReadInt(SET_Settings.SET_ITEM_CHECKS, 0) == 1);
            Main_tsmItemChecks.CheckedChanged += Main_OnItemChecksChanged;
            Main_tsmView.DropDownItems.Add(Main_tsmItemChecks);

            /* The size of the text in both lists, remembered; each item's   */
            /* Tag is its size in points.                                    */
            Main_BuildTextSizeMenu();
            Main_tsmView.DropDownItems.Add(Main_tsmTextSize);

            /* The size of the toolbars' pictures, remembered: the large ones   */
            /* with the small ones taking over when the window is too narrow    */
            /* to show every button, or the small ones always.  Exactly one     */
            /* of the two items carries the mark.  Each has a handler of its    */
            /* own, both calling Main_SetToolIconsSmall with the choice.        */
            Main_tsmIconsAuto         = new ToolStripMenuItem("&Automatic (Small When the Window Is Narrow)");
            Main_tsmIconsAuto.Click  += Main_OnIconsAutoClicked;
            Main_tsmIconsSmall        = new ToolStripMenuItem("Always &Small");
            Main_tsmIconsSmall.Click += Main_OnIconsSmallClicked;

            /* The mark from the remembered choice (absent: Automatic);   */
            /* the two are always opposite.                               */
            Main_tsmIconsSmall.Checked = (SET_Settings.SET_ReadInt(SET_Settings.SET_TOOL_SMALL, 0) == 1);
            Main_tsmIconsAuto.Checked  = !Main_tsmIconsSmall.Checked;

            /* Their sub-menu, into the View menu. */
            Main_tsmToolIcons = new ToolStripMenuItem("Toolbar &Icons");
            Main_tsmToolIcons.DropDownItems.Add(Main_tsmIconsAuto);
            Main_tsmToolIcons.DropDownItems.Add(Main_tsmIconsSmall);
            Main_tsmView.DropDownItems.Add(Main_tsmToolIcons);

            /* The way back from column widths set by dragging to the widths   */
            /* the program works out.                                          */
            Main_tsmResetColumns        = new ToolStripMenuItem("Reset Column Wi&dths");
            Main_tsmResetColumns.Click += Main_OnResetColumnsClicked;
            Main_tsmView.DropDownItems.Add(Main_tsmResetColumns);
            Main_tsmView.DropDownItems.Add(new ToolStripSeparator());
            Main_tsmView.DropDownItems.Add(Main_tsmRefresh);

            /* Commands: the two copy directions. */
            Main_tsmExtract              = new ToolStripMenuItem("&Extract to Windows");
            Main_tsmExtract.ShortcutKeys = Keys.Control | Keys.E;
            Main_tsmExtract.Click       += Main_OnExtractClicked;

            /* Import into Image (Ctrl+I), the other direction. */
            Main_tsmImport              = new ToolStripMenuItem("&Import into Image");
            Main_tsmImport.ShortcutKeys = Keys.Control | Keys.I;
            Main_tsmImport.Click       += Main_OnImportClicked;

            /* Commands: the three edits, which act on whichever pane the   */
            /* user was last in -- the image or the Windows folder.         */
            Main_tsmNewFolder              = new ToolStripMenuItem("&New Folder...");
            Main_tsmNewFolder.ShortcutKeys = Keys.F7;
            Main_tsmNewFolder.Click       += Main_OnNewFolderClicked;

            /* Rename (F2). */
            Main_tsmRename              = new ToolStripMenuItem("&Rename...");
            Main_tsmRename.ShortcutKeys = Keys.F2;
            Main_tsmRename.Click       += Main_OnRenameClicked;

            /* Delete, with Shift+Delete as the menu's real key.  The plain   */
            /* Delete key cannot be a menu shortcut: a menu shortcut takes    */
            /* the key before the focused control sees it, so Delete would    */
            /* stop deleting text in the path boxes -- the lists take plain   */
            /* Delete themselves (Main_PaneKeyDown), which is why the         */
            /* right-click menu shows "Del".  Why Shift+Delete was chosen     */
            /* for the menu is not recorded.                                  */
            Main_tsmDelete              = new ToolStripMenuItem("&Delete...");
            Main_tsmDelete.ShortcutKeys = Keys.Shift | Keys.Delete;
            Main_tsmDelete.Click       += Main_OnDeleteClicked;

            /* Properties (Alt+Enter), of the active pane's selection. */
            Main_tsmProperties              = new ToolStripMenuItem("&Properties...");
            Main_tsmProperties.ShortcutKeys = Keys.Alt | Keys.Enter;
            Main_tsmProperties.Click       += Main_OnPropertiesClicked;

            /* Commands: Open, in the active pane -- what a double-click does   */
            /* (the key shown is display only: the lists handle Enter) -- and   */
            /* Open As, the ways to open a file in a window of the user's       */
            /* choosing rather than the one its content calls for.              */
            Main_tsmOpen                          = new ToolStripMenuItem("&Open");
            Main_tsmOpen.ShortcutKeyDisplayString = "Enter";
            Main_tsmOpen.Click                   += Main_OnOpenClicked;

            /* Open As's sub-menu, made by its builder. */
            Main_tsmOpenAs = Main_NewOpenAsMenu();

            /* Commands: New, a file of a chosen kind in the active pane,   */
            /* opened in its editor.                                        */
            Main_tsmNew = Main_NewNewMenu();

            /* The Commands menu: Open and Open As; the two copies; the     */
            /* edits with New among them; Properties -- dividers between;   */
            /* the items' enabled states set each time the menu opens.      */
            Main_tsmCommands = new ToolStripMenuItem("&Commands");
            Main_tsmCommands.DropDownItems.Add(Main_tsmOpen);
            Main_tsmCommands.DropDownItems.Add(Main_tsmOpenAs);
            Main_tsmCommands.DropDownItems.Add(new ToolStripSeparator());
            Main_tsmCommands.DropDownItems.Add(Main_tsmExtract);
            Main_tsmCommands.DropDownItems.Add(Main_tsmImport);
            Main_tsmCommands.DropDownItems.Add(new ToolStripSeparator());
            Main_tsmCommands.DropDownItems.Add(Main_tsmNewFolder);
            Main_tsmCommands.DropDownItems.Add(Main_tsmNew);
            Main_tsmCommands.DropDownItems.Add(Main_tsmRename);
            Main_tsmCommands.DropDownItems.Add(Main_tsmDelete);
            Main_tsmCommands.DropDownItems.Add(new ToolStripSeparator());
            Main_tsmCommands.DropDownItems.Add(Main_tsmProperties);
            Main_tsmCommands.DropDownOpening += Main_OnCommandsMenuOpening;

            /* Options: five remembered tick marks, each made by Main_NewOption   */
            /* from its menu text, its registry value, its default when the       */
            /* value is absent, and a handler of its own.  CheckOnClick           */
            /* toggles the mark; each handler only has to store the new state     */
            /* (Main_StoreOption).  The editor option's registry value is         */
            /* SET_OPT_JSON_OPEN although it covers INI and Klipper too: the      */
            /* name is kept so a setting already stored still counts.             */
            Main_tsmOptMetaOut = Main_NewOption("Extracting writes a Unix &metadata manifest", SET_Settings.SET_OPT_META_OUT, 0,
                                                Main_OnOptMetaOutChanged);
            Main_tsmOptMetaIn  = Main_NewOption("Importing &restores metadata from a manifest", SET_Settings.SET_OPT_META_IN, 1,
                                                Main_OnOptMetaInChanged);
            Main_tsmOptSetTimes = Main_NewOption("Extracted files keep the image's modify &times", SET_Settings.SET_OPT_SET_TIMES, 1,
                                                 Main_OnOptSetTimesChanged);
            Main_tsmOptFollowLinks = Main_NewOption("Extracting &follows symbolic links", SET_Settings.SET_OPT_FOLLOW_LINKS, 0,
                                                    Main_OnOptFollowLinksChanged);
            Main_tsmOptEditorOpen = Main_NewOption("Open config files (JSON, INI, Klipper) in the &editor", SET_Settings.SET_OPT_JSON_OPEN, 1,
                                                   Main_OnOptEditorOpenChanged);

            /* The device options: reading back an ordinary session's device     */
            /* writes (off by default; a format, an expand and a repair          */
            /* always read back), and the risk warnings --                       */
            /* a tick mark like the rest, but one the handler sets itself, not   */
            /* on click, because unticking it asks twice.                        */
            Main_tsmOptVerify = Main_NewOption("&Verify writes to devices (slower)", SET_Settings.SET_OPT_VERIFY, 0,
                                               Main_OnOptVerifyChanged);
            Main_tsmOptWarnings              = new ToolStripMenuItem("Risk &Warning Enable");
            Main_tsmOptWarnings.CheckOnClick = false;
            Main_tsmOptWarnings.Click       += Main_OnWarningsClicked;
            Main_UpdateWarningsItem();

            /* The Options menu, the items in order. */
            Main_tsmOptions = new ToolStripMenuItem("&Options");
            Main_tsmOptions.DropDownItems.Add(Main_tsmOptMetaOut);
            Main_tsmOptions.DropDownItems.Add(Main_tsmOptMetaIn);
            Main_tsmOptions.DropDownItems.Add(Main_tsmOptSetTimes);
            Main_tsmOptions.DropDownItems.Add(Main_tsmOptFollowLinks);
            Main_tsmOptions.DropDownItems.Add(new ToolStripSeparator());
            Main_tsmOptions.DropDownItems.Add(Main_tsmOptEditorOpen);
            Main_tsmOptions.DropDownItems.Add(new ToolStripSeparator());
            Main_tsmOptions.DropDownItems.Add(Main_tsmOptVerify);
            Main_tsmOptions.DropDownItems.Add(Main_tsmOptWarnings);

            /* The menu bar, left to right (Help is added after it is made,   */
            /* below).                                                        */
            Main_mnuMain = new MenuStrip();
            Main_mnuMain.Items.Add(Main_tsmFile);
            Main_mnuMain.Items.Add(Main_tsmEdit);
            Main_mnuMain.Items.Add(Main_tsmView);
            Main_mnuMain.Items.Add(Main_tsmCommands);
            Main_mnuMain.Items.Add(Main_tsmOptions);

            /* Help: the debug log viewer and the About window.  The viewer's   */
            /* item is made in every build, but put on the menu only in a       */
            /* DEBUG build (below).                                             */
            Main_tsmDebugLog        = new ToolStripMenuItem("&Debug Log");
            Main_tsmDebugLog.Click += Main_OnDebugLogClicked;

            /* About BifrostFM. */
            Main_tsmAbout        = new ToolStripMenuItem("&About BifrostFM");
            Main_tsmAbout.Click += Main_OnAboutClicked;

            /* The Help menu, last on the bar: Debug Log and a divider, then    */
            /* About, in a DEBUG build; About alone in a release build, which   */
            /* leaves the Debug Log item off (why is not recorded here).        */
            Main_tsmHelp = new ToolStripMenuItem("&Help");
#if DEBUG
            Main_tsmHelp.DropDownItems.Add(Main_tsmDebugLog);
            Main_tsmHelp.DropDownItems.Add(new ToolStripSeparator());
#endif
            Main_tsmHelp.DropDownItems.Add(Main_tsmAbout);
            Main_mnuMain.Items.Add(Main_tsmHelp);

            /* The status bar: one label that takes the whole width, starting   */
            /* with the nothing-open prompt (MAIN_NO_IMAGE_STATUS), which       */
            /* Main_ShowNoImage puts back when an image closes.                 */
            Main_tslStatus           = new ToolStripStatusLabel(MAIN_NO_IMAGE_STATUS);
            Main_tslStatus.Spring    = true;
            Main_tslStatus.TextAlign = ContentAlignment.MiddleLeft;

            /* The status bar holding it. */
            Main_stsStatus = new StatusStrip();
            Main_stsStatus.Items.Add(Main_tslStatus);

            /* The two panes, in a splitter filling the window.  The       */
            /* divider is dragged, not tabbed to: Tab goes pane to pane.   */
            Main_splPanes         = new SplitContainer();
            Main_splPanes.Dock    = DockStyle.Fill;
            Main_splPanes.TabStop = false;

            /* LEFT pane: the image.  Its list, made with the image columns,   */
            /* and its handlers: a double-click or Enter opens, the keys and   */
            /* the focus go to the handlers both lists share (the last list    */
            /* entered is the active pane), and dragging out of and dropping   */
            /* onto it go to the image side's own handlers, as the two         */
            /* sides' drags carry different things.  MouseDown is hooked on    */
            /* this list alone: a right-click on its empty space clears the    */
            /* selection, so the right-click menu's Properties there is the    */
            /* filesystem's (Main_OnImageMouseDown).                           */
            Main_lstImage = Main_NewPaneList(Main_asImageColumns);
            Main_lstImage.ItemActivate += Main_OnImageItemActivated;
            Main_lstImage.AllowDrop     = true;
            Main_lstImage.ItemDrag     += Main_OnImageItemDrag;
            Main_lstImage.DragEnter    += Main_OnImageDragEnter;
            Main_lstImage.DragDrop     += Main_OnImageDragDrop;
            Main_lstImage.KeyDown      += Main_OnImageKeyDown;
            Main_lstImage.Enter        += Main_OnImageListEntered;
            Main_lstImage.MouseDown    += Main_OnImageMouseDown;

            /* The image pane's icons are the program's own.  They are made     */
            /* here at the system's two icon sizes only so the list has some;   */
            /* Main_ApplyListFont remakes them at the sizes View > Text Size    */
            /* gives, the same sizes as the Windows pane's.  The dialogs that   */
            /* show image entries get a list of their own at the system's       */
            /* small size, made once and never remade under them.               */
            Main_imlEntrySmall           = EIC_EntryIcons.EIC_NewList(SystemInformation.SmallIconSize);
            Main_imlEntryLarge           = EIC_EntryIcons.EIC_NewList(SystemInformation.IconSize);
            Main_imlDialogIcons          = EIC_EntryIcons.EIC_NewList(SystemInformation.SmallIconSize);
            Main_lstImage.SmallImageList = Main_imlEntrySmall;
            Main_lstImage.LargeImageList = Main_imlEntryLarge;

            /* The image pane's path box: the no-image prompt until an        */
            /* image opens; Enter goes to the path typed; entering it makes   */
            /* the image pane the active one.                                 */
            Main_txtImagePath          = new TextBox();
            Main_txtImagePath.Dock     = DockStyle.Fill;
            Main_txtImagePath.Text     = MAIN_NO_IMAGE_TEXT;
            Main_txtImagePath.KeyDown += Main_OnImagePathKeyDown;
            Main_txtImagePath.Enter   += Main_OnImagePathEntered;

            /* The path row: a folder icon, then the path box.  Click the     */
            /* ICON for the picker; the box itself is for typing a path.      */
            /* Its side label is kept: it names the open filesystem's type,   */
            /* so it changes as images open and close -- MAIN_NO_IMAGE_SIDE   */
            /* until one is open (Main_ApplyOpenResult, Main_ShowNoImage).    */
            Main_ttTips       = new ToolTip();
            Main_picImagePath = Main_NewPathIcon("Choose a folder in the image (or open an image file or a device)",
                                                 Main_OnImagePathIconClicked);
            Main_pnlImagePath = Main_NewPathRow(Main_picImagePath, Main_txtImagePath, MAIN_NO_IMAGE_SIDE,
                                                out Main_lblImageSide);

            /* The icon's menu for when nothing is open: an image file or a     */
            /* device -- a choice rather than a default to an image file --     */
            /* each item with a handler of its own that calls the same picker   */
            /* as its File > Open twin.  Made once and kept; the shortcut       */
            /* text only shows the File menu's keys, which stay registered      */
            /* there alone.                                                     */
            Main_cmsOpenChoice = new ContextMenuStrip();
            Main_cmsOpenChoice.Items.Add(Main_NewOpenChoiceItem("Open &Image File...", "Ctrl+O", Main_OnChoiceImageClicked));
            Main_cmsOpenChoice.Items.Add(Main_NewOpenChoiceItem("Open &Device...", "Ctrl+D", Main_OnChoiceDeviceClicked));

            /* Check Filesystem's sub-menu for when nothing is open -- a       */
            /* context-sensitive pop-out of the same two choices: made once,   */
            /* and hung under File > Check Filesystem by                       */
            /* Main_OnFileMenuOpening only while no image is open.             */
            Main_tsmCheckImage  = Main_NewOpenChoiceItem("&Image File...", "", Main_OnCheckImageChoice);
            Main_tsmCheckDevice = Main_NewOpenChoiceItem("&Device...", "", Main_OnCheckDeviceChoice);

            /* Into the splitter's left half: the list first (it fills),   */
            /* the path row after (docked at the top).                     */
            Main_splPanes.Panel1.Controls.Add(Main_lstImage);
            Main_splPanes.Panel1.Controls.Add(Main_pnlImagePath);

            /* RIGHT pane: Windows.  Its list, made with the Windows columns,   */
            /* and the same handlers as the image list's but for the drag       */
            /* and drop, which are the Windows side's own.                      */
            Main_lstHost = Main_NewPaneList(Main_asHostColumns);
            Main_lstHost.ItemActivate += Main_OnHostItemActivated;
            Main_lstHost.AllowDrop     = true;
            Main_lstHost.ItemDrag     += Main_OnHostItemDrag;
            Main_lstHost.DragEnter    += Main_OnHostDragEnter;
            Main_lstHost.DragDrop     += Main_OnHostDragDrop;
            Main_lstHost.KeyDown      += Main_OnHostKeyDown;
            Main_lstHost.Enter        += Main_OnHostListEntered;

            /* The Windows pane's icons are the shell's, copied into               */
            /* SHI_ShellIcons' own lists at the panes' sizes (Main_ApplyListFont   */
            /* gives the list those lists).  Its ".." row -- the way up -- is      */
            /* the image pane's folder-and-up-arrow picture, added to the          */
            /* same lists ONCE, with the drawing that redraws it at each size      */
            /* change, so it is always the size of the rows around it --           */
            /* never a fixed large or small bitmap that a row's height would       */
            /* have to choose between.                                             */
            Main_iHostUpIcon = SHI_ShellIcons.SHI_AddPicture(Main_MakeHostUpPicture);

            /* The rows' own icons are asked of the shell a few at a time, on   */
            /* this timer, after a folder is shown (Main_ResolveHostIcons).     */
            Main_tmrIcons          = new Timer();
            Main_tmrIcons.Interval = MAIN_ICON_TICK_MS;
            Main_tmrIcons.Tick    += Main_OnIconTick;

            /* The Windows pane's path box: its keys are handled by         */
            /* Main_OnHostPathKeyDown; entering it makes the Windows pane   */
            /* the active one.                                              */
            Main_txtHostPath          = new TextBox();
            Main_txtHostPath.Dock     = DockStyle.Fill;
            Main_txtHostPath.KeyDown += Main_OnHostPathKeyDown;
            Main_txtHostPath.Enter   += Main_OnHostPathEntered;

            /* Its path row: the folder icon (the picker), the box, and    */
            /* "(WINDOWS)" -- a label that never changes, so the one the   */
            /* row hands back is not kept (lblHostSide goes unused).       */
            Main_picHostPath = Main_NewPathIcon("Choose a Windows folder", Main_OnHostPathIconClicked);
            Main_pnlHostPath = Main_NewPathRow(Main_picHostPath, Main_txtHostPath, "(WINDOWS)", out lblHostSide);

            /* Into the splitter's right half, as the left: the list first   */
            /* (it fills), the path row after (docked at the top).           */
            Main_splPanes.Panel2.Controls.Add(Main_lstHost);
            Main_splPanes.Panel2.Controls.Add(Main_pnlHostPath);

            /* A margin round each pane's contents, through which the pane's   */
            /* background shows as an outline: the active pane's is coloured   */
            /* (Main_MarkActivePane).                                          */
            Main_splPanes.Panel1.Padding = new Padding(MAIN_PANE_OUTLINE);
            Main_splPanes.Panel2.Padding = new Padding(MAIN_PANE_OUTLINE);
            Main_MarkActivePane();

            /* The right-click menu, shared by the two lists. */
            Main_BuildPaneMenu();
            Main_lstImage.ContextMenuStrip = Main_cmsPane;
            Main_lstHost.ContextMenuStrip  = Main_cmsPane;

            /* The order of each list's rows, as it was last left; a click on   */
            /* a column heading changes it, as "Sort By" does.                  */
            Main_asrtSide                  = new PSR_clsPaneSorter[MAIN_SIDES];
            Main_asrtSide[MAIN_SIDE_IMAGE] = Main_NewSorter(MAIN_SIDE_IMAGE);
            Main_asrtSide[MAIN_SIDE_HOST]  = Main_NewSorter(MAIN_SIDE_HOST);
            Main_lstImage.ColumnClick     += Main_OnImageColumnClicked;
            Main_lstHost.ColumnClick      += Main_OnHostColumnClicked;

            /* A column divider the user drags keeps the width it was left at.   */
            /* Each list's callback is its own, naming its pane.                 */
            ((PLV_PaneListView)Main_lstImage).PLV_pfnUserResized = Main_OnImageColumnResized;
            ((PLV_PaneListView)Main_lstHost).PLV_pfnUserResized  = Main_OnHostColumnResized;

            /* The List and Small Icons views' name cap, in average characters:   */
            /* a registry value with no menu item -- a single tunable setting     */
            /* -- the lists' own default when it is absent.                       */
            ((PLV_PaneListView)Main_lstImage).PLV_iNameMaxChars = SET_Settings.SET_ReadInt(SET_Settings.SET_NAME_MAX_CHARS, PLV_PaneListView.PLV_GRID_NAME_MAX_CHARS);
            ((PLV_PaneListView)Main_lstHost).PLV_iNameMaxChars  = SET_Settings.SET_ReadInt(SET_Settings.SET_NAME_MAX_CHARS, PLV_PaneListView.PLV_GRID_NAME_MAX_CHARS);

            /* The Name heading's select-all box (item check boxes on) selects   */
            /* all or none; each list's callback is its own, naming its pane.    */
            ((PLV_PaneListView)Main_lstImage).PLV_pfnHeaderBoxClicked = Main_OnImageHeaderBoxClicked;
            ((PLV_PaneListView)Main_lstHost).PLV_pfnHeaderBoxClicked  = Main_OnHostHeaderBoxClicked;

            /* The columns fill each list's width exactly, so they are sized   */
            /* again whenever that width changes -- the window resized, the    */
            /* divider moved, a vertical scroll bar come or gone.              */
            Main_lstImage.ClientSizeChanged += Main_OnImageClientSizeChanged;
            Main_lstHost.ClientSizeChanged  += Main_OnHostClientSizeChanged;

            /* The divider between the panes: only the USER moving it changes   */
            /* the panes' shares of the width.                                  */
            Main_splPanes.DoubleClick    += Main_OnSplitterDoubleClicked;
            Main_splPanes.SplitterMoving += Main_OnSplitterMoving;
            Main_splPanes.SplitterMoved  += Main_OnSplitterMoved;

            /* A change of selection changes which toolbar buttons apply. */
            Main_lstImage.SelectedIndexChanged += Main_OnImageSelectionChanged;
            Main_lstHost.SelectedIndexChanged  += Main_OnHostSelectionChanged;

            /* Item check boxes: the selection following a tick, and the       */
            /* boxes as the option was left (the ".." row's box is refused     */
            /* by the list itself).  No tick sync is waiting for either pane   */
            /* yet.                                                            */
            Main_abChecksPending      = new bool[MAIN_SIDES];
            Main_lstImage.ItemChecked += Main_OnImageItemChecked;
            Main_lstHost.ItemChecked  += Main_OnHostItemChecked;
            Main_ApplyItemChecks(Main_lstImage);
            Main_ApplyItemChecks(Main_lstHost);

            /* The toolbar row: the two panes' own toolbars and the shared   */
            /* one between them, on Main_pnlTools.                           */
            Main_BuildToolbar();

            /* The form: the splitter fills, the bars dock to the edges.  The   */
            /* menu is added LAST so it docks first and sits above the          */
            /* toolbar.                                                         */
            this.Controls.Add(Main_splPanes);
            this.Controls.Add(Main_stsStatus);
            this.Controls.Add(Main_pnlTools);
            this.Controls.Add(Main_mnuMain);
            this.MainMenuStrip = Main_mnuMain;
        }

        /*--------------------------------------------------------------------*/
        /* Main_MakeHostUpPicture:                                            */
        /*                                                                    */
        /* The Windows pane's ".." picture -- the image pane's folder-and-up- */
        /* arrow -- drawn at a given size.  SHI_ShellIcons keeps this as the  */
        /* picture's maker and calls it again at every size change, so the    */
        /* row's picture always matches the rows around it.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sizPicture : the size the lists are now.                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Bitmap : the picture, new on every call.                       */
        /*--------------------------------------------------------------------*/
        private static Bitmap Main_MakeHostUpPicture(Size sizPicture)
        {
            /* The way-up picture, at whatever size the lists are now. */
            return(EIC_EntryIcons.EIC_NewPicture(EIC_EntryIcons.EIC_ICON_UP, sizPicture));
        }

        /*--------------------------------------------------------------------*/
        /* Main_BuildPaneMenu:                                                */
        /*                                                                    */
        /* Create the right-click menu the two lists share.  Every item does  */
        /* what its counterpart in the menu bar does, so a command behaves    */
        /* identically however it is reached -- each item with a menu-bar     */
        /* twin through a handler of its own that calls the same function;    */
        /* which pane it acts on                                              */
        /* is settled when the menu opens (Main_OnPaneMenuOpening), from the  */
        /* list it was opened over.                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Main_cmsPane and its items exist.                       */
        /*--------------------------------------------------------------------*/
        private void Main_BuildPaneMenu()
        {
            /* The clipboard three, Cut first: each has a handler of its own   */
            /* that does what its Edit menu twin does, its key shown for       */
            /* display only (the lists take the keys themselves,               */
            /* Main_PaneKeyDown).                                              */
            Main_tsmCtxCut                          = new ToolStripMenuItem("Cu&t");
            Main_tsmCtxCut.ShortcutKeyDisplayString = "Ctrl+X";
            Main_tsmCtxCut.Click                   += Main_OnCtxCutClicked;

            /* Copy, its own handler; its key shown for display. */
            Main_tsmCtxCopy                          = new ToolStripMenuItem("&Copy");
            Main_tsmCtxCopy.ShortcutKeyDisplayString = "Ctrl+C";
            Main_tsmCtxCopy.Click                   += Main_OnCtxCopyClicked;

            /* Paste, likewise. */
            Main_tsmCtxPaste                          = new ToolStripMenuItem("&Paste");
            Main_tsmCtxPaste.ShortcutKeyDisplayString = "Ctrl+V";
            Main_tsmCtxPaste.Click                   += Main_OnCtxPasteClicked;

            /* The edits. */
            Main_tsmCtxDelete                          = new ToolStripMenuItem("&Delete...");
            Main_tsmCtxDelete.ShortcutKeyDisplayString = "Del";
            Main_tsmCtxDelete.Click                   += Main_OnCtxDeleteClicked;

            /* Rename, its own handler; F2 shown for display. */
            Main_tsmCtxRename                          = new ToolStripMenuItem("Rena&me...");
            Main_tsmCtxRename.ShortcutKeyDisplayString = "F2";
            Main_tsmCtxRename.Click                   += Main_OnCtxRenameClicked;

            /* New Folder, likewise; F7 shown. */
            Main_tsmCtxNewFolder                          = new ToolStripMenuItem("&New Folder...");
            Main_tsmCtxNewFolder.ShortcutKeyDisplayString = "F7";
            Main_tsmCtxNewFolder.Click                   += Main_OnCtxNewFolderClicked;

            /* The copy across to the other pane; its text and direction are   */
            /* set when the menu opens.                                        */
            Main_tsmCtxTransfer        = new ToolStripMenuItem("&Extract to Windows");
            Main_tsmCtxTransfer.Click += Main_OnTransferClicked;

            /* Properties, its own handler; Alt+Enter shown. */
            Main_tsmCtxProperties                          = new ToolStripMenuItem("P&roperties...");
            Main_tsmCtxProperties.ShortcutKeyDisplayString = "Alt+Enter";
            Main_tsmCtxProperties.Click                   += Main_OnCtxPropertiesClicked;

            /* Refresh, its own handler doing what View > Refresh does; F5   */
            /* shown.                                                        */
            Main_tsmCtxRefresh                          = new ToolStripMenuItem("Re&fresh");
            Main_tsmCtxRefresh.ShortcutKeyDisplayString = "F5";
            Main_tsmCtxRefresh.Click                   += Main_OnCtxRefreshClicked;

            /* Open, first and in bold as the default action (it is what a    */
            /* double-click does); then Open As, the other ways to open a     */
            /* file; and New beside New Folder.  The two sub-menus are made   */
            /* again for this menu: a menu item has one parent.               */
            Main_tsmCtxOpen        = new ToolStripMenuItem("&Open");
            Main_tsmCtxOpen.Font   = new Font(Main_tsmCtxOpen.Font, FontStyle.Bold);
            Main_tsmCtxOpen.Click += Main_OnCtxOpenClicked;

            /* The two sub-menus, made afresh by their builders. */
            Main_tsmCtxOpenAs = Main_NewOpenAsMenu();
            Main_tsmCtxNew    = Main_NewNewMenu();

            /* The menu: Open and Open As; the clipboard three; the edits      */
            /* with New; the copy across and Properties -- dividers between.   */
            Main_cmsPane = new ContextMenuStrip();
            Main_cmsPane.Items.Add(Main_tsmCtxOpen);
            Main_cmsPane.Items.Add(Main_tsmCtxOpenAs);
            Main_cmsPane.Items.Add(new ToolStripSeparator());
            Main_cmsPane.Items.Add(Main_tsmCtxCut);
            Main_cmsPane.Items.Add(Main_tsmCtxCopy);
            Main_cmsPane.Items.Add(Main_tsmCtxPaste);
            Main_cmsPane.Items.Add(new ToolStripSeparator());
            Main_cmsPane.Items.Add(Main_tsmCtxDelete);
            Main_cmsPane.Items.Add(Main_tsmCtxRename);
            Main_cmsPane.Items.Add(Main_tsmCtxNewFolder);
            Main_cmsPane.Items.Add(Main_tsmCtxNew);
            Main_cmsPane.Items.Add(new ToolStripSeparator());
            Main_cmsPane.Items.Add(Main_tsmCtxTransfer);
            Main_cmsPane.Items.Add(Main_tsmCtxProperties);
            /* View and Sort By: their items depend on the pane, so they are    */
            /* made as the menu opens (Main_FillViewMenu, Main_FillSortMenu).   */
            Main_tsmCtxView = new ToolStripMenuItem("&View");
            Main_tsmCtxSort = new ToolStripMenuItem("S&ort By");

            /* The last group, after a divider: View, Sort By, Refresh; and   */
            /* the opening handler that fits every item to the pane.          */
            Main_cmsPane.Items.Add(new ToolStripSeparator());
            Main_cmsPane.Items.Add(Main_tsmCtxView);
            Main_cmsPane.Items.Add(Main_tsmCtxSort);
            Main_cmsPane.Items.Add(Main_tsmCtxRefresh);
            Main_cmsPane.Opening += Main_OnPaneMenuOpening;
        }

        /*--------------------------------------------------------------------*/
        /* Main_NewPathIcon:                                                  */
        /*                                                                    */
        /* Create the folder icon that sits before a pane's path box.  It is  */
        /* the program's own folder picture, at the panes' small icon size    */
        /* (Main_SizePathRow sets that, and sets it again whenever View >     */
        /* Text Size changes it), and a single CLICK on it brings up that     */
        /* pane's folder picker, as a button would (a single click, not a     */
        /* double-click).  The path box beside it is left as an               */
        /* ordinary text box.                                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sTip     : the tool tip.                                       */
        /*     pfnClick : the handler that runs the pane's picker.            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     PictureBox : the icon, docked to the left of its row.          */
        /*--------------------------------------------------------------------*/
        private PictureBox Main_NewPathIcon(string sTip, EventHandler pfnClick)
        {
            PictureBox picIcon;

            /* No picture and no width yet: Main_SizePathRow gives it both,   */
            /* at the panes' size, before the window is first shown.          */
            picIcon              = new PictureBox();
            picIcon.Dock         = DockStyle.Left;
            picIcon.SizeMode     = PictureBoxSizeMode.CenterImage;
            picIcon.Cursor       = Cursors.Hand;
            picIcon.Click       += pfnClick;

            /* Its tool tip, from the window's one ToolTip. */
            Main_ttTips.SetToolTip(picIcon, sTip);

            /* The caller puts it in a path row. */
            return(picIcon);
        }

        /*--------------------------------------------------------------------*/
        /* Main_NewPathRow:                                                   */
        /*                                                                    */
        /* Create a pane's path row: the folder icon, the path box, and the   */
        /* pane's side in words -- over the image the open filesystem's type, */
        /* "(EXT4)" or "(FAT32)" say, or MAIN_NO_IMAGE_SIDE with nothing      */
        /* open; over Windows "(WINDOWS)" -- which makes it plain to a user   */
        /* which filesystem each pane shows.  The row's font, height and      */
        /* icon follow the pane below it, including the folder icon's size,   */
        /* so they are set by Main_SizePathRow (at every View > Text Size     */
        /* change), not here.                                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     picIcon : the folder icon (docked left).                       */
        /*     txtPath : the path box (docked to fill).                       */
        /*     sSide   : the side label's first text.                         */
        /*     lblSide : receives the side label, for a caller whose label    */
        /*               changes later (the image pane's).                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Panel : the row, docked to the top of its pane.                */
        /*--------------------------------------------------------------------*/
        private static Panel Main_NewPathRow(PictureBox picIcon, TextBox txtPath, string sSide, out Label lblSide)
        {
            Panel pnlRow;

            /* The row, across the top of its pane.  It is given a            */
            /* background of its own, the ordinary control colour: the        */
            /* pane's background is the outline colour (Main_MarkActivePane   */
            /* colours the active one), which would otherwise show behind     */
            /* the icon.                                                      */
            pnlRow           = new Panel();
            pnlRow.Dock      = DockStyle.Top;
            pnlRow.BackColor = SystemColors.Control;

            /* The side label: as wide as its text (AutoSize) with a little   */
            /* room either side, as tall as the row (the dock), the text      */
            /* centred in it.                                                 */
            lblSide           = new Label();
            lblSide.Text      = sSide;
            lblSide.AutoSize  = true;
            lblSide.Dock      = DockStyle.Right;
            lblSide.Padding   = new Padding(MAIN_PATH_ICON_PAD, 0, MAIN_PATH_ICON_PAD / 2, 0);
            lblSide.TextAlign = ContentAlignment.MiddleCenter;

            /* Docking runs in reverse order of adding: the icon takes the     */
            /* left, the label the right, and the box fills what they leave.   */
            pnlRow.Controls.Add(txtPath);
            pnlRow.Controls.Add(lblSide);
            pnlRow.Controls.Add(picIcon);

            /* The caller parents it to the pane. */
            return(pnlRow);
        }

        /*--------------------------------------------------------------------*/
        /* Main_SizePathRow:                                                  */
        /*                                                                    */
        /* Size a pane's path row like the list beneath it -- its text, its   */
        /* folder icon and its height.  Called whenever View > Text Size      */
        /* changes the panes' size (Main_ApplyListFont), which includes once  */
        /* before the window is first shown.                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pnlRow  : the row (Main_NewPathRow's).                         */
        /*     picIcon : its folder icon.                                     */
        /*     txtPath : its path box.                                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the row matches the pane's text and icon size.          */
        /*--------------------------------------------------------------------*/
        private void Main_SizePathRow(Panel pnlRow, PictureBox picIcon, TextBox txtPath)
        {
            Image imgOld;
            Size  sizIcon;
            int   iHeight;

            /* The panes' small icon size (the image pane's list was remade   */
            /* at it), and the panes' text font on the row: the path box      */
            /* and the side label inherit it, neither having a font of its    */
            /* own.                                                           */
            sizIcon     = Main_imlEntrySmall.ImageSize;
            pnlRow.Font = Main_fntList;

            /* The program's folder at the new size, with a little room to     */
            /* its right (MAIN_PATH_ICON_PAD) before the path box.  An image   */
            /* list hands out a NEW copy each time it is asked, and a          */
            /* picture box never disposes the picture it is given (both as     */
            /* found), so the old copy is disposed here.                       */
            imgOld        = picIcon.Image;
            picIcon.Image = Main_imlEntrySmall.Images[EIC_EntryIcons.EIC_ICON_FOLDER];
            picIcon.Width = sizIcon.Width + MAIN_PATH_ICON_PAD;

            if (imgOld != null)
            {   /* Not on the first call, when there is none yet. */
                imgOld.Dispose();
            }

            /* The path box's own height at the new font (read after the   */
            /* font above is in place), or the icon's if that is taller.   */
            iHeight = txtPath.PreferredHeight;

            if (iHeight < sizIcon.Height)
            {   /* The icon is taller than a line of text. */
                iHeight = sizIcon.Height;
            }

            /* The row that tall, so neither the box nor the icon is cut. */
            pnlRow.Height = iHeight;
        }

        /*--------------------------------------------------------------------*/
        /* Main_NewOpenChoiceItem:                                            */
        /*                                                                    */
        /* Create one item of the image pane's open menus -- the folder       */
        /* icon's menu when nothing is open, and the toolbar's Open pull-down */
        /* (Main_NewToolOpenPullDown) -- or of Check Filesystem's like menu.  */
        /* Its shortcut is SHOWN only, through the display string: the keys   */
        /* themselves belong to the File > Open items, and registering them a */
        /* second time here would make two items answer one key.              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sText     : the item's text, its access key marked with &.     */
        /*     sShortcut : the File menu's key for the same command, shown;   */
        /*                 "" for none (the check's items have no key).       */
        /*     pfnClick  : the item's own handler, hooked to it alone.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ToolStripMenuItem : the item, for the caller to add.           */
        /*--------------------------------------------------------------------*/
        private static ToolStripMenuItem Main_NewOpenChoiceItem(string sText, string sShortcut, EventHandler pfnClick)
        {
            ToolStripMenuItem tsmItem;

            /* The item: its text, its key shown but not registered (the      */
            /* File menu's item already has it, and two items registered      */
            /* for one key would both answer it), and the handler given for   */
            /* it alone -- never one shared with another item.                */
            tsmItem                          = new ToolStripMenuItem(sText);
            tsmItem.ShortcutKeyDisplayString = sShortcut;
            tsmItem.Click                   += pfnClick;

            /* The caller adds it to the menu. */
            return(tsmItem);
        }

        /*--------------------------------------------------------------------*/
        /* Main_NewToolButton:                                                */
        /*                                                                    */
        /* Create one toolbar button: a picture only, with its meaning in the */
        /* tool tip.  The picture is drawn at the toolbars' picture size.     */
        /* Nothing is hooked here: the caller hooks the button's Click to the */
        /* named handler that is its alone (Main_BuildToolbar), so every      */
        /* button's event is traceable to one handler.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iIcon : a TIC_ICON_* value, the picture.                       */
        /*     sTip  : the tool tip (what the button does, and its key).      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ToolStripButton : the button; the caller hooks its handler and */
        /*                       adds it to a bar.                            */
        /*--------------------------------------------------------------------*/
        private ToolStripButton Main_NewToolButton(int iIcon, string sTip)
        {
            ToolStripButton tsbNew;

            /* A picture-only button, drawn at the toolbars' present        */
            /* picture size (Main_ToolIconSize: small or large), with its   */
            /* meaning in the tool tip, since it carries no text.           */
            tsbNew              = new ToolStripButton();
            tsbNew.DisplayStyle = ToolStripItemDisplayStyle.Image;
            tsbNew.Image        = TIC_ToolIcons.TIC_NewPicture(iIcon, Main_ToolIconSize());
            tsbNew.ToolTipText  = sTip;

            /* Which picture it is rides in its Tag, and the button is kept     */
            /* on a list, so the picture can be drawn again at the small size   */
            /* (Main_ApplyToolIconSize).                                        */
            tsbNew.Tag = iIcon;
            Main_lstToolButtons.Add(tsbNew);

            /* The caller hooks its handler and places it. */
            return(tsbNew);
        }

        /*--------------------------------------------------------------------*/
        /* Main_NewToolPullDown:                                              */
        /*                                                                    */
        /* Create a pane's New pull-down for its toolbar: a picture that      */
        /* drops down the four kinds of file Commands > New makes, each made  */
        /* in THIS pane, whichever pane the user was last in.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iIcon : a TIC_ICON_* value, the picture.                       */
        /*     sTip  : the tool tip.                                          */
        /*     iSide : MAIN_SIDE_IMAGE or MAIN_SIDE_HOST, the pane it is on.  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ToolStripDropDownButton : the pull-down; the caller places it. */
        /*--------------------------------------------------------------------*/
        private ToolStripDropDownButton Main_NewToolPullDown(int iIcon, string sTip, int iSide)
        {
            ToolStripDropDownButton tsdNew;
            ToolStripMenuItem       tsmItem;
            Mpb_clsPaneBinding      objBinding;
            int                     i;

            /* A picture-only pull-down at the toolbars' picture size, its    */
            /* meaning in the tool tip.  Its picture's TIC_ICON_* rides in    */
            /* its Tag and it is kept on the picture list with the buttons,   */
            /* so its picture is drawn again with theirs at the other size    */
            /* (Main_ApplyToolIconSize).                                      */
            tsdNew              = new ToolStripDropDownButton();
            tsdNew.DisplayStyle = ToolStripItemDisplayStyle.Image;
            tsdNew.Image        = TIC_ToolIcons.TIC_NewPicture(iIcon, Main_ToolIconSize());
            tsdNew.ToolTipText  = sTip;
            tsdNew.Tag          = iIcon;
            Main_lstToolButtons.Add(tsdNew);

            /* The pane, held for the four items' shared click handler. */
            objBinding = new Mpb_clsPaneBinding(this, iSide);

            for (i = 0; i < Main_asNewTexts.Length; i++)
            {   /* One kind of file per iteration. */
                tsmItem     = new ToolStripMenuItem(Main_asNewTexts[i]);
                tsmItem.Tag = i;

                /* Its click: as a pane's own buttons' handlers do, this pane   */
                /* made the active one, then the same code the menu's item      */
                /* runs (Main_NewFile), the kind read back from the Tag.        */
                tsmItem.Click += objBinding.MPB_OnNewKindClick;

                /* Into the pull-down, in MAIN_NEW_* order. */
                tsdNew.DropDownItems.Add(tsmItem);
            }

            /* The caller places it. */
            return(tsdNew);
        }

        /*--------------------------------------------------------------------*/
        /* Main_NewToolOpenPullDown:                                          */
        /*                                                                    */
        /* Create the image pane's Open pull-down for its toolbar: the drive  */
        /* picture, dropping down the two things that can be opened -- an     */
        /* image file or a device -- the same choice the image path icon      */
        /* offers with nothing open, and the two File > Open items.  The      */
        /* framework drops the list down on a click, so nothing of ours runs  */
        /* until an item is chosen, and each item has its own handler.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ToolStripDropDownButton : the pull-down; the caller places it. */
        /*--------------------------------------------------------------------*/
        private ToolStripDropDownButton Main_NewToolOpenPullDown()
        {
            ToolStripDropDownButton tsdOpen;

            /* A picture-only pull-down at the toolbars' picture size, its       */
            /* meaning in the tool tip.  Its TIC_ICON_* rides in its Tag and     */
            /* it is kept on the picture list with the buttons, so its picture   */
            /* is drawn again with theirs at the other size                      */
            /* (Main_ApplyToolIconSize).                                         */
            tsdOpen              = new ToolStripDropDownButton();
            tsdOpen.DisplayStyle = ToolStripItemDisplayStyle.Image;
            tsdOpen.Image        = TIC_ToolIcons.TIC_NewPicture(TIC_ToolIcons.TIC_ICON_OPEN, Main_ToolIconSize());
            tsdOpen.ToolTipText  = "Open an image file (Ctrl+O) or a device (Ctrl+D)";
            tsdOpen.Tag          = TIC_ToolIcons.TIC_ICON_OPEN;
            Main_lstToolButtons.Add(tsdOpen);

            /* The two items, made as the path icon's are: each shows the File   */
            /* menu's key without registering it a second time, and each has a   */
            /* handler of its own that makes the image pane the active one and   */
            /* then calls the picker its File > Open twin calls.                 */
            tsdOpen.DropDownItems.Add(Main_NewOpenChoiceItem("Open &Image File...", "Ctrl+O", Main_OnOpenButtonImageClicked));
            tsdOpen.DropDownItems.Add(Main_NewOpenChoiceItem("Open &Device...", "Ctrl+D", Main_OnOpenButtonDeviceClicked));

            /* The caller places it. */
            return(tsdOpen);
        }

        /*--------------------------------------------------------------------*/
        /* Main_ToolIconSize:                                                 */
        /*                                                                    */
        /* The size of a toolbar picture: one and a half times the system's   */
        /* small icon, so the buttons are an easy target without making the   */
        /* row tall.                                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Size : the picture size, pixels.                               */
        /*--------------------------------------------------------------------*/
        private static Size Main_ToolIconSize()
        {
            Size sizSmall;

            /* The system's small icon size, the base the toolbar pictures   */
            /* are scaled from.                                              */
            sizSmall = SystemInformation.SmallIconSize;

            /* Scaled by MAIN_TOOL_ICON_NUM / MAIN_TOOL_ICON_DEN (three   */
            /* halves), multiplied first so the integer division drops    */
            /* only the final fraction.                                   */
            return(new Size((sizSmall.Width * MAIN_TOOL_ICON_NUM) / MAIN_TOOL_ICON_DEN,
                            (sizSmall.Height * MAIN_TOOL_ICON_NUM) / MAIN_TOOL_ICON_DEN));
        }

        /*--------------------------------------------------------------------*/
        /* Main_ActivatePane:                                                 */
        /*                                                                    */
        /* Make one pane the active one, as if the user had clicked in its    */
        /* list: the focus goes to that list (which is what tells the user    */
        /* where the command they just pressed is acting), and the menus'     */
        /* marks follow.  A toolbar never takes the focus itself, so a pane's */
        /* own toolbar button's handler calls this first, and the function it */
        /* then calls -- one that acts on the active pane -- acts on its      */
        /* pane.                                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bHostPane : true for the Windows pane, false for the image     */
        /*                 pane.                                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : that pane is the active one.                            */
        /*--------------------------------------------------------------------*/
        private void Main_ActivatePane(bool bHostPane)
        {
            /* Recorded first -- the menus and the shared handlers read     */
            /* Main_bHostPaneActive -- and the outline moved to the pane.   */
            Main_bHostPaneActive = bHostPane;
            Main_MarkActivePane();

            if (bHostPane)
            {   /* The Windows pane's list takes the focus. */
                Main_lstHost.Focus();
            }
            else
            {   /* The image pane's list takes the focus. */
                Main_lstImage.Focus();
            }

            /* The view marks follow the active pane. */
            Main_UpdateViewMenu();
        }

        /*--------------------------------------------------------------------*/
        /* Main_MarkActivePane:                                               */
        /*                                                                    */
        /* Show which pane is the ACTIVE one -- the pane the Commands and     */
        /* Edit menus and the keys act on -- by an outline round it in the    */
        /* system's highlight colour; the other pane's outline is the         */
        /* ordinary control colour, so it does not show.  The outline is each */
        /* pane's own background showing through a margin left round its      */
        /* contents (MAIN_PANE_OUTLINE), so nothing is drawn by hand.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the active pane, and only it, is outlined in the        */
        /*            highlight colour.                                       */
        /*--------------------------------------------------------------------*/
        private void Main_MarkActivePane()
        {
            if (Main_bHostPaneActive)
            {   /* The Windows pane is the active one. */
                Main_splPanes.Panel1.BackColor = SystemColors.Control;
                Main_splPanes.Panel2.BackColor = SystemColors.Highlight;
            }
            else
            {   /* The image pane is the active one. */
                Main_splPanes.Panel1.BackColor = SystemColors.Highlight;
                Main_splPanes.Panel2.BackColor = SystemColors.Control;
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_NewToolStrip:                                                 */
        /*                                                                    */
        /* Create one of the three toolbars.  It is NOT docked: it sizes      */
        /* itself to its buttons and Main_LayoutToolbars places it on the     */
        /* row, which is how one can be flush left, one centred and one flush */
        /* right.                                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ToolStrip : an empty toolbar; the caller adds its buttons.     */
        /*--------------------------------------------------------------------*/
        private static ToolStrip Main_NewToolStrip()
        {
            ToolStrip tspNew;

            /* Undocked and sized to its buttons, so Main_LayoutToolbars     */
            /* can place it flush left, centred or flush right;              */
            /* no grip, since a bar is never dragged; the system's flat      */
            /* look, so the three read as one row on the panel (as found);   */
            /* the toolbars' picture size; and no padding of its own.        */
            tspNew                  = new ToolStrip();
            tspNew.Dock             = DockStyle.None;
            tspNew.AutoSize         = true;
            tspNew.GripStyle        = ToolStripGripStyle.Hidden;
            tspNew.RenderMode       = ToolStripRenderMode.System;
            tspNew.ImageScalingSize = Main_ToolIconSize();
            tspNew.Padding          = new Padding(0);

            /* The caller fills and parents it. */
            return(tspNew);
        }

        /*--------------------------------------------------------------------*/
        /* Main_BuildToolbar:                                                 */
        /*                                                                    */
        /* Create the toolbar row: three toolbars on one panel.               */
        /*                                                                    */
        /*   the image pane's   (flush left)  : open image, enable writing,   */
        /*                                      then the per-pane set, then   */
        /*                                      properties.                   */
        /*   the shared one     (centred)     : copy to the left, copy to the */
        /*                                      right, refresh.               */
        /*   the Windows pane's (flush right) : the per-pane set, then create */
        /*                                      image.                        */
        /*                                                                    */
        /* The PER-PANE SET is the same on both sides -- back, up, cut, copy, */
        /* paste, new folder, rename, delete, and a pull-down of the views    */
        /* whose text is the view showing -- and each of its buttons acts on  */
        /* its own pane only, whichever pane the user was last in: its        */
        /* handler makes that pane the active one first.  EVERY BUTTON HAS A  */
        /* NAMED HANDLER OF ITS OWN, hooked here, which calls the SAME        */
        /* function its menu item's handler calls, so the toolbars add no     */
        /* behaviour of their own and each button's click is traceable to     */
        /* one handler.                                                       */
        /*                                                                    */
        /* "Copy to left" and "copy to right" are Import and Extract: the     */
        /* image is the left pane.                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Main_pnlTools, the three toolbars and their buttons     */
        /*            exist.                                                  */
        /*--------------------------------------------------------------------*/
        private void Main_BuildToolbar()
        {
            ToolStrip tspSide;
            string    sPane;
            int       iSide;

            /* The per-pane arrays, one slot per pane (MAIN_SIDE_*), which   */
            /* the loop below fills.                                         */
            Main_atspSide      = new ToolStrip[MAIN_SIDES];
            Main_atsbBack      = new ToolStripButton[MAIN_SIDES];
            Main_atsbUp        = new ToolStripButton[MAIN_SIDES];
            Main_atsbCut       = new ToolStripButton[MAIN_SIDES];
            Main_atsbCopy      = new ToolStripButton[MAIN_SIDES];
            Main_atsbPaste     = new ToolStripButton[MAIN_SIDES];
            Main_atsbNewFolder = new ToolStripButton[MAIN_SIDES];
            Main_atsdNew       = new ToolStripDropDownButton[MAIN_SIDES];
            Main_atsbRename    = new ToolStripButton[MAIN_SIDES];
            Main_atsbDelete    = new ToolStripButton[MAIN_SIDES];
            Main_atsbProperties = new ToolStripButton[MAIN_SIDES];
            Main_atsdViews     = new ToolStripDropDownButton[MAIN_SIDES];

            /* The buttons only one pane has, each hooked to its own handler.   */
            /* Open is a pull-down of the same two choices the image path       */
            /* icon offers with nothing open -- an image file or a device --    */
            /* which the framework drops down itself, so its click runs no      */
            /* code of ours; each item has a handler of its own.  Close is      */
            /* File > Close Image/Device's twin.                                */
            Main_tsdOpen            = Main_NewToolOpenPullDown();
            Main_tsbClose           = Main_NewToolButton(TIC_ToolIcons.TIC_ICON_CLOSE, "Close the image or device");
            Main_tsbClose.Enabled   = false;
            Main_tsbClose.Click    += Main_OnCloseButtonClicked;
            Main_tsbWrite          = Main_NewToolButton(TIC_ToolIcons.TIC_ICON_WRITE, "Enable writing to the image");
            Main_tsbWrite.Click   += Main_OnWriteButtonClicked;
            Main_tsbNewImage        = Main_NewToolButton(TIC_ToolIcons.TIC_ICON_NEW_IMAGE, "Create a new image file (Ctrl+N)");
            Main_tsbNewImage.Click += Main_OnNewImageButtonClicked;

            for (iSide = 0; iSide < MAIN_SIDES; iSide++)
            {   /* One pane's toolbar per iteration. */
                sPane = "image pane";

                if (iSide == MAIN_SIDE_HOST)
                {   /* The tool tips name the pane a button acts on. */
                    sPane = "Windows pane";
                }

                /* The per-pane set, each button's tool tip naming the pane;   */
                /* their handlers are hooked after the loop, one named         */
                /* handler per button per pane.                                */
                Main_atsbBack[iSide]      = Main_NewToolButton(TIC_ToolIcons.TIC_ICON_BACK, "Back, in the " + sPane);
                Main_atsbUp[iSide]        = Main_NewToolButton(TIC_ToolIcons.TIC_ICON_UP, "Up one level, in the " + sPane);
                Main_atsbCut[iSide]       = Main_NewToolButton(TIC_ToolIcons.TIC_ICON_CUT, "Cut the " + sPane + "'s selection");
                Main_atsbCopy[iSide]      = Main_NewToolButton(TIC_ToolIcons.TIC_ICON_COPY, "Copy the " + sPane + "'s selection");
                Main_atsbPaste[iSide]     = Main_NewToolButton(TIC_ToolIcons.TIC_ICON_PASTE, "Paste into the " + sPane);
                Main_atsbNewFolder[iSide] = Main_NewToolButton(TIC_ToolIcons.TIC_ICON_NEW_FOLDER, "New folder in the " + sPane);
                Main_atsdNew[iSide]       = Main_NewToolPullDown(TIC_ToolIcons.TIC_ICON_NEW_FILE, "New file in the " + sPane, iSide);
                Main_atsbRename[iSide]    = Main_NewToolButton(TIC_ToolIcons.TIC_ICON_RENAME, "Rename the " + sPane + "'s selected item");
                Main_atsbDelete[iSide]    = Main_NewToolButton(TIC_ToolIcons.TIC_ICON_DELETE,
                                                               "Delete the " + sPane + "'s selected files and folders");
                Main_atsbProperties[iSide] = Main_NewToolButton(TIC_ToolIcons.TIC_ICON_PROPERTIES,
                                                                "Properties of the " + sPane + "'s selected item");

                /* The view pull-down: its text is the view now showing   */
                /* (Main_UpdateViewMenu keeps it so).                     */
                Main_atsdViews[iSide]              = new ToolStripDropDownButton();
                Main_atsdViews[iSide].DisplayStyle = ToolStripItemDisplayStyle.Text;
                Main_atsdViews[iSide].ToolTipText  = "How the " + sPane + "'s list is shown";
                Main_AddViewItems(Main_atsdViews[iSide].DropDownItems, iSide);

                /* Left to right, a separator between groups. */
                tspSide = Main_NewToolStrip();

                if (iSide == MAIN_SIDE_IMAGE)
                {   /* The image pane's toolbar starts with its own three: Open,   */
                    /* Close and Enable Writing, for the image or device.          */
                    tspSide.Items.Add(Main_tsdOpen);
                    tspSide.Items.Add(Main_tsbClose);
                    tspSide.Items.Add(Main_tsbWrite);
                    tspSide.Items.Add(new ToolStripSeparator());
                }
                else
                {   /* The Windows pane's begins with Places: the quick way to     */
                    /* the common locations.  Its items are made afresh each       */
                    /* time it is asked to open (the drives present can change),   */
                    /* by a posted call, never by the handler                      */
                    /* (Main_OnPlacesOpening, Main_ShowPlaces); it starts with     */
                    /* one stand-in item -- a pull-down with none would not        */
                    /* open at all (as found).                                     */
                    Main_tsdPlaces              = new ToolStripDropDownButton("Places");
                    Main_tsdPlaces.DisplayStyle = ToolStripItemDisplayStyle.Text;
                    Main_tsdPlaces.ToolTipText  = "Go to This PC, one of your folders, or a drive";
                    Main_tsdPlaces.DropDownItems.Add(new ToolStripMenuItem("This PC"));
                    Main_tsdPlaces.DropDown.Opening += Main_OnPlacesOpening;

                    tspSide.Items.Add(Main_tsdPlaces);
                    tspSide.Items.Add(new ToolStripSeparator());
                }

                /* Then the per-pane set, in groups parted by separators:   */
                /* moving about; the clipboard; the edits, with New and     */
                /* Properties; and the view pull-down.                      */
                tspSide.Items.Add(Main_atsbBack[iSide]);
                tspSide.Items.Add(Main_atsbUp[iSide]);
                tspSide.Items.Add(new ToolStripSeparator());
                tspSide.Items.Add(Main_atsbCut[iSide]);
                tspSide.Items.Add(Main_atsbCopy[iSide]);
                tspSide.Items.Add(Main_atsbPaste[iSide]);
                tspSide.Items.Add(new ToolStripSeparator());
                tspSide.Items.Add(Main_atsbNewFolder[iSide]);
                tspSide.Items.Add(Main_atsdNew[iSide]);
                tspSide.Items.Add(Main_atsbRename[iSide]);
                tspSide.Items.Add(Main_atsbDelete[iSide]);
                tspSide.Items.Add(Main_atsbProperties[iSide]);
                tspSide.Items.Add(new ToolStripSeparator());
                tspSide.Items.Add(Main_atsdViews[iSide]);

                if (iSide == MAIN_SIDE_HOST)
                {   /* The Windows pane's toolbar ends with Create Image: a new   */
                    /* image is a file made in a Windows folder.                  */
                    tspSide.Items.Add(new ToolStripSeparator());
                    tspSide.Items.Add(Main_tsbNewImage);
                }

                /* Kept as this pane's toolbar, for the row below and the   */
                /* layout (Main_LayoutToolbars).                            */
                Main_atspSide[iSide] = tspSide;
            }

            /* Each pane's buttons hooked, one named handler per button per      */
            /* pane: the handler makes its pane the active one, then calls       */
            /* what the button's menu twin's handler calls.  The image pane's:   */
            Main_atsbBack[MAIN_SIDE_IMAGE].Click       += Main_OnImageBackButtonClicked;
            Main_atsbUp[MAIN_SIDE_IMAGE].Click         += Main_OnImageUpButtonClicked;
            Main_atsbCut[MAIN_SIDE_IMAGE].Click        += Main_OnImageCutButtonClicked;
            Main_atsbCopy[MAIN_SIDE_IMAGE].Click       += Main_OnImageCopyButtonClicked;
            Main_atsbPaste[MAIN_SIDE_IMAGE].Click      += Main_OnImagePasteButtonClicked;
            Main_atsbNewFolder[MAIN_SIDE_IMAGE].Click  += Main_OnImageNewFolderButtonClicked;
            Main_atsbRename[MAIN_SIDE_IMAGE].Click     += Main_OnImageRenameButtonClicked;
            Main_atsbDelete[MAIN_SIDE_IMAGE].Click     += Main_OnImageDeleteButtonClicked;
            Main_atsbProperties[MAIN_SIDE_IMAGE].Click += Main_OnImagePropertiesButtonClicked;

            /* ... and the Windows pane's. */
            Main_atsbBack[MAIN_SIDE_HOST].Click        += Main_OnHostBackButtonClicked;
            Main_atsbUp[MAIN_SIDE_HOST].Click          += Main_OnHostUpButtonClicked;
            Main_atsbCut[MAIN_SIDE_HOST].Click         += Main_OnHostCutButtonClicked;
            Main_atsbCopy[MAIN_SIDE_HOST].Click        += Main_OnHostCopyButtonClicked;
            Main_atsbPaste[MAIN_SIDE_HOST].Click       += Main_OnHostPasteButtonClicked;
            Main_atsbNewFolder[MAIN_SIDE_HOST].Click   += Main_OnHostNewFolderButtonClicked;
            Main_atsbRename[MAIN_SIDE_HOST].Click      += Main_OnHostRenameButtonClicked;
            Main_atsbDelete[MAIN_SIDE_HOST].Click      += Main_OnHostDeleteButtonClicked;
            Main_atsbProperties[MAIN_SIDE_HOST].Click  += Main_OnHostPropertiesButtonClicked;

            /* The shared toolbar: what concerns both panes at once.  Each     */
            /* button has a handler of its own, calling what its menu twin's   */
            /* handler calls (Import, Extract, View > Refresh).                */
            Main_tsbToImage        = Main_NewToolButton(TIC_ToolIcons.TIC_ICON_TO_IMAGE,
                                                        "Copy to the left: import the Windows selection into the image (Ctrl+I)");
            Main_tsbToImage.Click += Main_OnToImageButtonClicked;
            Main_tsbToHost         = Main_NewToolButton(TIC_ToolIcons.TIC_ICON_TO_HOST,
                                                        "Copy to the right: extract the image selection to Windows (Ctrl+E)");
            Main_tsbToHost.Click  += Main_OnToHostButtonClicked;
            Main_tsbRefresh        = Main_NewToolButton(TIC_ToolIcons.TIC_ICON_REFRESH, "Refresh both panes (F5)");
            Main_tsbRefresh.Click += Main_OnRefreshButtonClicked;

            /* A DOUBLE separator line closes each end of the shared toolbar,   */
            /* so the centred group reads as set apart from the two panes'.     */
            Main_tspShared = Main_NewToolStrip();
            Main_tspShared.Items.Add(new ToolStripSeparator());
            Main_tspShared.Items.Add(new ToolStripSeparator());
            Main_tspShared.Items.Add(Main_tsbToImage);
            Main_tspShared.Items.Add(Main_tsbToHost);
            Main_tspShared.Items.Add(new ToolStripSeparator());
            Main_tspShared.Items.Add(Main_tsbRefresh);
            Main_tspShared.Items.Add(new ToolStripSeparator());
            Main_tspShared.Items.Add(new ToolStripSeparator());

            /* The row.  Its height is the toolbars'; where each sits across   */
            /* it is recomputed whenever its width changes.                    */
            Main_pnlTools      = new Panel();
            Main_pnlTools.Dock = DockStyle.Top;
            Main_pnlTools.Controls.Add(Main_atspSide[MAIN_SIDE_IMAGE]);
            Main_pnlTools.Controls.Add(Main_tspShared);
            Main_pnlTools.Controls.Add(Main_atspSide[MAIN_SIDE_HOST]);
            Main_pnlTools.Height  = Main_tspShared.PreferredSize.Height;
            Main_pnlTools.Resize += Main_OnToolRowResize;
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnOpenButtonImageClicked:                                     */
        /*                                                                    */
        /* The image pane's Open pull-down > Open Image File: the image pane  */
        /* made the active one, then the image-file picker, as File > Open >  */
        /* Image File runs it.                                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the pull-down's item (unused).                     */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : an open request is posted, or a dialog was cancelled.   */
        /*--------------------------------------------------------------------*/
        private void Main_OnOpenButtonImageClicked(object objSender, EventArgs evArgs)
        {
            /* Its pane, then the picker, for opening (false: not a check). */
            Main_ActivatePane(false);
            Main_PickImageFile(false);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnOpenButtonDeviceClicked:                                    */
        /*                                                                    */
        /* The image pane's Open pull-down > Open Device: the image pane made */
        /* the active one, then the device picker, as File > Open > Device    */
        /* runs it, its restart as Administrator included.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the pull-down's item (unused).                     */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the picker is asked for, a restart asked for, or the    */
        /*            status line says why not.                               */
        /*--------------------------------------------------------------------*/
        private void Main_OnOpenButtonDeviceClicked(object objSender, EventArgs evArgs)
        {
            /* Its pane, then the device picker, for opening (false: not a   */
            /* check).                                                       */
            Main_ActivatePane(false);
            Main_PickDevice(false);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnCloseButtonClicked:                                         */
        /*                                                                    */
        /* The image pane's Close button: the image pane made the active one, */
        /* then the same close File > Close Image/Device posts.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the close is posted; the pane empties when the worker's */
        /*            state says nothing is open.                             */
        /*--------------------------------------------------------------------*/
        private void Main_OnCloseButtonClicked(object objSender, EventArgs evArgs)
        {
            /* Its pane, then the close, posted as one command: the worker    */
            /* asks about unsaved editors on the image, closes them and the   */
            /* session, and a Cancel leaves everything as it was.             */
            Main_ActivatePane(false);
            Main_PostLifecycleCommand(KND_enmKind.Close);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnWriteButtonClicked:                                         */
        /*                                                                    */
        /* The image pane's Enable Writing button: the image pane made the    */
        /* active one, then the same toggle File > Enable Writing to Image    */
        /* requests (Main_RequestWriteToggle).                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : one desired-access request is posted.                   */
        /*--------------------------------------------------------------------*/
        private void Main_OnWriteButtonClicked(object objSender, EventArgs evArgs)
        {
            /* Its pane, then the toggle. */
            Main_ActivatePane(false);
            Main_RequestWriteToggle();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnNewImageButtonClicked:                                      */
        /*                                                                    */
        /* The Windows pane's Create Image button: the Windows pane made the  */
        /* active one, then the same request File > New Image File makes      */
        /* (Main_RequestNewImage).                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the New Image request is posted.                        */
        /*--------------------------------------------------------------------*/
        private void Main_OnNewImageButtonClicked(object objSender, EventArgs evArgs)
        {
            /* Its pane, then the request. */
            Main_ActivatePane(true);
            Main_RequestNewImage();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnImageBackButtonClicked:                                     */
        /*                                                                    */
        /* The image pane's Back button: that pane made the active one, then  */
        /* back in it (Main_GoBack), as View > Back does for the active pane. */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the image pane is going back, if it has anywhere to.    */
        /*--------------------------------------------------------------------*/
        private void Main_OnImageBackButtonClicked(object objSender, EventArgs evArgs)
        {
            /* The image pane, then back in it. */
            Main_ActivatePane(false);
            Main_GoBack(true);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnHostBackButtonClicked:                                      */
        /*                                                                    */
        /* The same for the Windows pane's Back button.                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the Windows pane is going back, if it has anywhere to.  */
        /*--------------------------------------------------------------------*/
        private void Main_OnHostBackButtonClicked(object objSender, EventArgs evArgs)
        {
            /* The Windows pane, then back in it. */
            Main_ActivatePane(true);
            Main_GoBack(false);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnImageUpButtonClicked:                                       */
        /*                                                                    */
        /* The image pane's Up button: that pane made the active one, then up */
        /* one level in it (Main_GoUp), as View > Up One Level does.          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the image pane is going up, if there is a level above.  */
        /*--------------------------------------------------------------------*/
        private void Main_OnImageUpButtonClicked(object objSender, EventArgs evArgs)
        {
            /* The image pane, then up in it. */
            Main_ActivatePane(false);
            Main_GoUp(true);
        }

        /*---------------------------------------------------------------------*/
        /* Main_OnHostUpButtonClicked:                                         */
        /*                                                                     */
        /* The same for the Windows pane's Up button.                          */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     objSender : the button (unused).                                */
        /*     evArgs    : unused.                                             */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : the Windows pane is going up, if there is a level above. */
        /*---------------------------------------------------------------------*/
        private void Main_OnHostUpButtonClicked(object objSender, EventArgs evArgs)
        {
            /* The Windows pane, then up in it. */
            Main_ActivatePane(true);
            Main_GoUp(false);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnImageCutButtonClicked:                                      */
        /*                                                                    */
        /* The image pane's Cut button: that pane made the active one, then   */
        /* its selection on the clipboard as cut (Main_CopyToClipboard), as   */
        /* Edit > Cut does.                                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the clipboard request is posted.                        */
        /*--------------------------------------------------------------------*/
        private void Main_OnImageCutButtonClicked(object objSender, EventArgs evArgs)
        {
            /* The image pane, then its selection marked as a cut (true). */
            Main_ActivatePane(false);
            Main_CopyToClipboard(true, true);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnHostCutButtonClicked:                                       */
        /*                                                                    */
        /* The same for the Windows pane's Cut button.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the clipboard request is posted.                        */
        /*--------------------------------------------------------------------*/
        private void Main_OnHostCutButtonClicked(object objSender, EventArgs evArgs)
        {
            /* The Windows pane, then its selection marked as a cut (true). */
            Main_ActivatePane(true);
            Main_CopyToClipboard(false, true);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnImageCopyButtonClicked:                                     */
        /*                                                                    */
        /* The image pane's Copy button: that pane made the active one, then  */
        /* its selection on the clipboard (Main_CopyToClipboard), as Edit >   */
        /* Copy does.                                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the clipboard request is posted.                        */
        /*--------------------------------------------------------------------*/
        private void Main_OnImageCopyButtonClicked(object objSender, EventArgs evArgs)
        {
            /* The image pane, then its selection as a copy (false: not a cut). */
            Main_ActivatePane(false);
            Main_CopyToClipboard(true, false);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnHostCopyButtonClicked:                                      */
        /*                                                                    */
        /* The same for the Windows pane's Copy button.                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the clipboard request is posted.                        */
        /*--------------------------------------------------------------------*/
        private void Main_OnHostCopyButtonClicked(object objSender, EventArgs evArgs)
        {
            /* The Windows pane, then its selection as a copy (false: not a cut). */
            Main_ActivatePane(true);
            Main_CopyToClipboard(false, false);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnImagePasteButtonClicked:                                    */
        /*                                                                    */
        /* The image pane's Paste button: that pane made the active one, then */
        /* the clipboard pasted into it (Main_PasteFromClipboard), as Edit >  */
        /* Paste does.                                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the paste is requested, or the status bar says why not. */
        /*--------------------------------------------------------------------*/
        private void Main_OnImagePasteButtonClicked(object objSender, EventArgs evArgs)
        {
            /* The image pane, then the paste into it. */
            Main_ActivatePane(false);
            Main_PasteFromClipboard(true);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnHostPasteButtonClicked:                                     */
        /*                                                                    */
        /* The same for the Windows pane's Paste button.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the paste is requested, or the status bar says why not. */
        /*--------------------------------------------------------------------*/
        private void Main_OnHostPasteButtonClicked(object objSender, EventArgs evArgs)
        {
            /* The Windows pane, then the paste into it. */
            Main_ActivatePane(true);
            Main_PasteFromClipboard(false);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnImageNewFolderButtonClicked:                                */
        /*                                                                    */
        /* The image pane's New Folder button: that pane made the active one, */
        /* then the same request Commands > New Folder makes                  */
        /* (Main_RequestNewFolder, which acts on the active pane).            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the new folder is requested.                            */
        /*--------------------------------------------------------------------*/
        private void Main_OnImageNewFolderButtonClicked(object objSender, EventArgs evArgs)
        {
            /* The image pane, then the request. */
            Main_ActivatePane(false);
            Main_RequestNewFolder();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnHostNewFolderButtonClicked:                                 */
        /*                                                                    */
        /* The same for the Windows pane's New Folder button.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the new folder is requested.                            */
        /*--------------------------------------------------------------------*/
        private void Main_OnHostNewFolderButtonClicked(object objSender, EventArgs evArgs)
        {
            /* The Windows pane, then the request. */
            Main_ActivatePane(true);
            Main_RequestNewFolder();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnImageRenameButtonClicked:                                   */
        /*                                                                    */
        /* The image pane's Rename button: that pane made the active one,     */
        /* then the same request Commands > Rename makes (Main_RequestRename, */
        /* which acts on the active pane).                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the rename is requested.                                */
        /*--------------------------------------------------------------------*/
        private void Main_OnImageRenameButtonClicked(object objSender, EventArgs evArgs)
        {
            /* The image pane, then the request. */
            Main_ActivatePane(false);
            Main_RequestRename();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnHostRenameButtonClicked:                                    */
        /*                                                                    */
        /* The same for the Windows pane's Rename button.                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the rename is requested.                                */
        /*--------------------------------------------------------------------*/
        private void Main_OnHostRenameButtonClicked(object objSender, EventArgs evArgs)
        {
            /* The Windows pane, then the request. */
            Main_ActivatePane(true);
            Main_RequestRename();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnImageDeleteButtonClicked:                                   */
        /*                                                                    */
        /* The image pane's Delete button: that pane made the active one,     */
        /* then the same request Commands > Delete makes (Main_RequestDelete, */
        /* which acts on the active pane).                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the delete is requested.                                */
        /*--------------------------------------------------------------------*/
        private void Main_OnImageDeleteButtonClicked(object objSender, EventArgs evArgs)
        {
            /* The image pane, then the request. */
            Main_ActivatePane(false);
            Main_RequestDelete();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnHostDeleteButtonClicked:                                    */
        /*                                                                    */
        /* The same for the Windows pane's Delete button.                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the delete is requested.                                */
        /*--------------------------------------------------------------------*/
        private void Main_OnHostDeleteButtonClicked(object objSender, EventArgs evArgs)
        {
            /* The Windows pane, then the request. */
            Main_ActivatePane(true);
            Main_RequestDelete();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnImagePropertiesButtonClicked:                               */
        /*                                                                    */
        /* The image pane's Properties button: that pane made the active one, */
        /* then the same request Commands > Properties makes                  */
        /* (Main_RequestProperties, which acts on the active pane -- the      */
        /* filesystem's window when nothing is selected there).               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the properties are requested.                           */
        /*--------------------------------------------------------------------*/
        private void Main_OnImagePropertiesButtonClicked(object objSender, EventArgs evArgs)
        {
            /* The image pane, then the request. */
            Main_ActivatePane(false);
            Main_RequestProperties();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnHostPropertiesButtonClicked:                                */
        /*                                                                    */
        /* The same for the Windows pane's Properties button.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the properties are requested.                           */
        /*--------------------------------------------------------------------*/
        private void Main_OnHostPropertiesButtonClicked(object objSender, EventArgs evArgs)
        {
            /* The Windows pane, then the request. */
            Main_ActivatePane(true);
            Main_RequestProperties();
        }

        /*--------------------------------------------------------------------*/
        /* Main_BuildTextSizeMenu:                                            */
        /*                                                                    */
        /* Create View > Text Size: one item per size the lists' text can be  */
        /* shown at.  Each item's Tag is its size in points, which is how one */
        /* handler serves them all and how the mark is set.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Main_tsmTextSize and its items exist, unmarked.         */
        /*--------------------------------------------------------------------*/
        private void Main_BuildTextSizeMenu()
        {
            ToolStripMenuItem tsmSize;
            int               i;

            /* The sub-menu, filled with Main_aiFontSizes' sizes below;   */
            /* the caller adds it to the View menu.                       */
            Main_tsmTextSize = new ToolStripMenuItem("Te&xt Size");

            for (i = 0; i < Main_aiFontSizes.Length; i++)
            {   /* One size per iteration. */
                tsmSize        = new ToolStripMenuItem(Main_aiFontSizes[i] + " point");
                tsmSize.Tag    = Main_aiFontSizes[i];
                tsmSize.Click += Main_OnTextSizeClicked;
                Main_tsmTextSize.DropDownItems.Add(tsmSize);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_ApplyListFont:                                                */
        /*                                                                    */
        /* Show both panes at a given text size: the lists' text, their       */
        /* ICONS, and the path rows above them all follow it, so a larger     */
        /* text size makes the whole pane larger, not only its words.  The    */
        /* panes keep the form's typeface; only the size changes.  The menus  */
        /* and toolbars do not follow it: they stay at the system's size.     */
        /* Everything computed from the old size is computed again, and the   */
        /* mark in View > Text Size moves to the size now in use.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iPoints : the text size, points.                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : both panes show their text and icons at that size.      */
        /*--------------------------------------------------------------------*/
        private void Main_ApplyListFont(int iPoints)
        {
            Font      fntOld;
            ImageList imlOldSmall;
            ImageList imlOldLarge;
            Size      sizSmall;
            Size      sizLarge;
            int       iSmall;
            int       i;

            /* The new font, in the form's typeface at the size asked for.   */
            /* The old one is kept aside and disposed below, once both       */
            /* lists and both path rows have moved off it (null on the       */
            /* first call, from Main_RestoreListFont).                       */
            fntOld       = Main_fntList;
            Main_fntList = new Font(this.Font.FontFamily, (float)iPoints);

            /* THE ICON SIZE.  A small icon is as tall as a line of the new   */
            /* text (the font's own height), so a row's icon and its words    */
            /* grow together.                                                 */
            iSmall = Main_fntList.Height;

            if (iSmall < SystemInformation.SmallIconSize.Height)
            {   /* Never below the system's small icon. */
                iSmall = SystemInformation.SmallIconSize.Height;
            }

            /* Small icons square at that height; large ones keep the   */
            /* system's large-to-small ratio (multiplied before the     */
            /* division, so only the final fraction is dropped).        */
            sizSmall = new Size(iSmall, iSmall);
            sizLarge = new Size((iSmall * SystemInformation.IconSize.Width) / SystemInformation.SmallIconSize.Width,
                                (iSmall * SystemInformation.IconSize.Height) / SystemInformation.SmallIconSize.Height);

            /* The image pane's pictures at those sizes.  The list moves to   */
            /* the new lists BEFORE the old ones are disposed, so it never    */
            /* draws from one that has gone; its rows' indexes are the same   */
            /* in every list EIC_NewList makes.                               */
            imlOldSmall                  = Main_imlEntrySmall;
            imlOldLarge                  = Main_imlEntryLarge;
            Main_imlEntrySmall           = EIC_EntryIcons.EIC_NewList(sizSmall);
            Main_imlEntryLarge           = EIC_EntryIcons.EIC_NewList(sizLarge);
            Main_lstImage.SmallImageList = Main_imlEntrySmall;
            Main_lstImage.LargeImageList = Main_imlEntryLarge;
            imlOldSmall.Dispose();
            imlOldLarge.Dispose();

            /* The Windows pane's: the shell's icons drawn again at them. */
            SHI_ShellIcons.SHI_SetIconSizes(Main_lstHost, sizSmall, sizLarge);

            /* The text, in both lists and both path rows. */
            Main_lstImage.Font = Main_fntList;
            Main_lstHost.Font  = Main_fntList;
            Main_SizePathRow(Main_pnlImagePath, Main_picImagePath, Main_txtImagePath);
            Main_SizePathRow(Main_pnlHostPath, Main_picHostPath, Main_txtHostPath);

            if (fntOld != null)
            {   /* Everything has moved to the new font. */
                fntOld.Dispose();
            }

            /* Widths measured in the old size no longer hold -- the columns'   */
            /* and the grid views' alike.                                       */
            Main_aaiDetailWidths[MAIN_SIDE_IMAGE] = null;
            Main_aaiDetailWidths[MAIN_SIDE_HOST]  = null;
            Main_GridChanged(Main_lstImage, true);
            Main_GridChanged(Main_lstHost, true);

            /* Each pane's view set again, as it is: Main_SetPaneView         */
            /* recomputes what depends on the size -- a tile's size and the   */
            /* details view's column widths.                                  */
            Main_SetPaneView(Main_lstImage, Main_lstImage.View);
            Main_SetPaneView(Main_lstHost, Main_lstHost.View);

            /* The mark moved: the item of the size in use ticked, the rest   */
            /* not.                                                           */
            for (i = 0; i < Main_tsmTextSize.DropDownItems.Count; i++)
            {   /* One size item per iteration. */
                ((ToolStripMenuItem)Main_tsmTextSize.DropDownItems[i]).Checked =
                    ((int)Main_tsmTextSize.DropDownItems[i].Tag == iPoints);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_RestoreListFont:                                              */
        /*                                                                    */
        /* Show the lists' text at the size it was last left at; the default  */
        /* on a first run, or when the stored number is not one of the sizes  */
        /* the menu offers.                                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : both lists show their text at the remembered size.      */
        /*--------------------------------------------------------------------*/
        private void Main_RestoreListFont()
        {
            int  iPoints;
            bool bOffered;
            int  i;

            /* The stored size (absent: MAIN_FONT_DEFAULT), and whether it    */
            /* is one the menu offers -- not until a size matches (the loop   */
            /* runs on past a match, which changes nothing).                  */
            iPoints  = SET_Settings.SET_ReadInt(SET_Settings.SET_LIST_FONT, MAIN_FONT_DEFAULT);
            bOffered = false;

            for (i = 0; i < Main_aiFontSizes.Length; i++)
            {   /* One offered size per iteration. */
                if (Main_aiFontSizes[i] == iPoints)
                {   /* The stored size is one of them. */
                    bOffered = true;
                }
            }

            if (!bOffered)
            {   /* A damaged or hand-edited value: the default, so a menu   */
                /* item is always ticked.                                   */
                iPoints = MAIN_FONT_DEFAULT;
            }

            /* Both panes at that size -- this first call also makes the   */
            /* panes' font, which the constructor left null.               */
            Main_ApplyListFont(iPoints);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnTextSizeClicked:                                            */
        /*                                                                    */
        /* One of View > Text Size's items was chosen: remember the size and  */
        /* show both lists at it.                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item; its Tag is the size in points.      */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the lists' text is the chosen size.                     */
        /*--------------------------------------------------------------------*/
        private void Main_OnTextSizeClicked(object objSender, EventArgs evArgs)
        {
            int iPoints;

            /* The size the item stands for, from the Tag   */
            /* Main_BuildTextSizeMenu gave it.              */
            iPoints = (int)((ToolStripMenuItem)objSender).Tag;

            /* Remembered for the next run, then shown. */
            SET_Settings.SET_WriteInt(SET_Settings.SET_LIST_FONT, iPoints);
            Main_ApplyListFont(iPoints);
        }

        /*--------------------------------------------------------------------*/
        /* Main_ApplyHostShown:                                               */
        /*                                                                    */
        /* Show or hide the whole Windows pane, as View > Show Windows Pane   */
        /* is ticked or not.  Hidden, the image pane takes the full width and */
        /* everything that only concerns the Windows pane goes with it.       */
        /* Nothing about the Windows pane is forgotten: it comes back showing */
        /* the folder it had.                                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window matches the tick mark.                       */
        /*--------------------------------------------------------------------*/
        private void Main_ApplyHostShown()
        {
            bool bShown;

            /* The tick mark decides. */
            bShown = Main_tsmShowHost.Checked;

            /* The splitter's right half collapsed or shown -- collapsed,     */
            /* the image pane takes the whole width, and the right half's     */
            /* list and path keep their contents -- with the Windows pane's   */
            /* toolbar and its View sub-menu following it.                    */
            Main_splPanes.Panel2Collapsed         = !bShown;
            Main_atspSide[MAIN_SIDE_HOST].Visible = bShown;
            Main_tsmViewHost.Enabled              = bShown;

            /* With one pane there is nothing to copy across to or from, so    */
            /* the two copy buttons (and the separator after them) are taken   */
            /* off the shared toolbar altogether, and Refresh says what it     */
            /* now refreshes.                                                  */
            Main_tsbToImage.Visible                               = bShown;
            Main_tsbToHost.Visible                                = bShown;
            Main_tspShared.Items[MAIN_SHARED_SEPARATOR].Visible   = bShown;

            if (bShown)
            {   /* Both panes are showing. */
                Main_tsbRefresh.ToolTipText = "Refresh both panes (F5)";
            }
            else
            {   /* Only the image pane is showing. */
                Main_tsbRefresh.ToolTipText = "Refresh pane (F5)";
            }

            if (!bShown && Main_bHostPaneActive)
            {   /* The active pane is the one being hidden: the image pane takes over. */
                Main_ActivatePane(false);
            }

            /* The buttons' states for the pane now active, the toolbars   */
            /* placed again on the row (one fewer, or one more), and the   */
            /* window's layout redone for the change (Main_ApplyLayout).   */
            Main_UpdateToolbar();
            Main_LayoutToolbars();
            Main_ApplyLayout();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnShowHostChanged:                                            */
        /*                                                                    */
        /* View > Show Windows Pane was ticked or unticked: remember it and   */
        /* show or hide the pane.                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item.                                     */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the setting is stored and the window matches it.        */
        /*--------------------------------------------------------------------*/
        private void Main_OnShowHostChanged(object objSender, EventArgs evArgs)
        {
            int iShown;

            /* The registry's 1 / 0 for the tick: unticked unless ... */
            iShown = 0;

            if (Main_tsmShowHost.Checked)
            {   /* ... ticked. */
                iShown = 1;
            }

            /* Remembered for the next run (the constructor reads it, absent   */
            /* meaning shown), then the window made to match.                  */
            SET_Settings.SET_WriteInt(SET_Settings.SET_SHOW_HOST, iShown);
            Main_ApplyHostShown();
        }

        /*--------------------------------------------------------------------*/
        /* Main_AddPlace:                                                     */
        /*                                                                    */
        /* Add one folder or drive to the Places pull-down: an item that      */
        /* takes the Windows pane there when chosen.  A location that is not  */
        /* there on this machine is left out -- a user folder that was never  */
        /* created, whose path the framework answers as "", or a drive that   */
        /* is not ready.  This PC, which has no path, is never added here:    */
        /* Main_AddThisPcPlace adds it, so an empty path can only mean a      */
        /* folder that is missing.                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sText : the item's text.                                       */
        /*     sPath : the folder or drive root to go to; "" (a user folder   */
        /*             the framework could not find) leaves the item out.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item is added, or the location was not there.       */
        /*--------------------------------------------------------------------*/
        private void Main_AddPlace(string sText, string sPath)
        {
            ToolStripMenuItem tsmPlace;

            if (sPath == null || sPath.Length == 0 || !Directory.Exists(sPath))
            {   /* No path at all (a guard: no caller passes null); an empty   */
                /* one -- what Environment.GetFolderPath answers, as it is     */
                /* documented to, for a user folder that does not exist on     */
                /* this machine; or a folder that is not there, or a drive     */
                /* that is not ready, which Directory.Exists also answers      */
                /* false for (as found): left out.  None of them is a place    */
                /* the pane could go to.                                       */
                return;
            }

            /* The item, its path in the Tag, where the click reads it. */
            tsmPlace     = new ToolStripMenuItem(sText);
            tsmPlace.Tag = sPath;

            /* The icon Windows itself shows for that folder or drive. */
            tsmPlace.Image = SHI_ShellIcons.SHI_SmallPicture(sPath);

            /* Its click: the Windows pane made the active one -- Places is   */
            /* on its toolbar -- and sent to the item's folder.               */
            tsmPlace.Click += Main_OnPlaceClicked;

            /* Into the pull-down, in the order the caller adds the places. */
            Main_tsdPlaces.DropDownItems.Add(tsmPlace);
        }

        /*--------------------------------------------------------------------*/
        /* Main_AddThisPcPlace:                                               */
        /*                                                                    */
        /* Add This PC -- the list of drives -- to the Places pull-down.  It  */
        /* has no folder of its own: its item carries "" as its path, which   */
        /* the click hands on as the drive list.  Kept apart from             */
        /* Main_AddPlace, which leaves out every empty path as a missing      */
        /* folder, so the one deliberate empty path is added only here.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the This PC item is added.                              */
        /*--------------------------------------------------------------------*/
        private void Main_AddThisPcPlace()
        {
            ToolStripMenuItem tsmPlace;

            /* The item, "" in its Tag: the drive list, where the click reads   */
            /* it.                                                              */
            tsmPlace     = new ToolStripMenuItem("This PC");
            tsmPlace.Tag = "";

            /* This PC's own icon, asked for in the shell's other way, since   */
            /* there is no path to ask by.                                     */
            tsmPlace.Image = SHI_ShellIcons.SHI_ThisPcPicture();

            /* Its click is every place's: the Windows pane made the active   */
            /* one and sent to the drive list.                                */
            tsmPlace.Click += Main_OnPlaceClicked;

            /* Into the pull-down, where the caller adds it: at the top. */
            Main_tsdPlaces.DropDownItems.Add(tsmPlace);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnPlacesOpening:                                              */
        /*                                                                    */
        /* The Places pull-down is about to open.  It opens only when         */
        /* Main_ShowPlaces shows it, once it has filled it afresh: that       */
        /* opening is let through.  Any other -- a click on the button -- is  */
        /* kept closed and the fill and show posted to the window's loop, so  */
        /* nothing is built here.                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the pull-down's drop-down.                         */
        /*     evArgs    : its Cancel set to keep it closed.                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the opening let through, or cancelled and the fill      */
        /*            posted.                                                 */
        /*--------------------------------------------------------------------*/
        private void Main_OnPlacesOpening(object objSender, System.ComponentModel.CancelEventArgs evArgs)
        {
            if (Main_bPlacesShowing)
            {   /* Main_ShowPlaces's own showing: the list is filled; let it   */
                /* open.  It lowers the mark once its show returns.            */
                return;
            }

            /* Kept closed; the fill and the show posted. */
            evArgs.Cancel = true;
            BeginInvoke(new MethodInvoker(Main_ShowPlaces));
        }

        /*--------------------------------------------------------------------*/
        /* Main_ShowPlaces:                                                   */
        /*                                                                    */
        /* Fill the Places pull-down afresh and open it.  First This PC (the  */
        /* list of drives), then the user's own folders as Windows reports    */
        /* them for this user -- so a Documents folder that has been moved is */
        /* still found -- then each drive that is present now.  Posted by     */
        /* Main_OnPlacesOpening, which keeps the button's own opening closed. */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the pull-down lists the places and is open.             */
        /*--------------------------------------------------------------------*/
        private void Main_ShowPlaces()
        {
            DriveInfo[] adrvDrives;
            string      sHome;
            int         i;

            /* Emptied first -- the list is rebuilt at every opening, since   */
            /* the drives present can change -- which also removes the        */
            /* stand-in item Main_BuildToolbar gave it.                       */
            Main_tsdPlaces.DropDownItems.Clear();

            /* The top: what sits above every drive. */
            Main_AddThisPcPlace();
            Main_tsdPlaces.DropDownItems.Add(new ToolStripSeparator());

            /* The user's own folders.  The profile folder first, the one   */
            /* Home goes to.                                                */
            sHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            /* Home, then the user's folders as the framework reports them   */
            /* for this user (Downloads, which it has no name for, from      */
            /* the shell), then a separator before the drives.  A folder     */
            /* that does not exist comes back "", and Main_AddPlace leaves   */
            /* it out.                                                       */
            Main_AddPlace("Home (" + Environment.UserName + ")", sHome);
            Main_AddPlace("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            Main_AddPlace("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            Main_AddPlace("Downloads", SHI_ShellIcons.SHI_DownloadsFolder());
            Main_AddPlace("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
            Main_AddPlace("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
            Main_AddPlace("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
            Main_tsdPlaces.DropDownItems.Add(new ToolStripSeparator());

            /* The drives present now. */
            try
            {   /* Listing the drives can be refused. */
                adrvDrives = DriveInfo.GetDrives();
            }
            catch (Exception)
            {   /* No drive items this time. */
                adrvDrives = new DriveInfo[0];
            }

            for (i = 0; i < adrvDrives.Length; i++)
            {   /* One drive per iteration, named by its root path, which   */
                /* Main_AddPlace keeps only when the drive is ready.        */
                Main_AddPlace(adrvDrives[i].Name, adrvDrives[i].Name);
            }

            /* Opened, the mark letting its own Opening through              */
            /* (Main_OnPlacesOpening) and lowered once the show has raised   */
            /* it, so no later opening is let through by mistake.            */
            Main_bPlacesShowing = true;
            Main_tsdPlaces.ShowDropDown();
            Main_bPlacesShowing = false;
        }

        /*--------------------------------------------------------------------*/
        /* Main_LayoutToolbars:                                               */
        /*                                                                    */
        /* Place the three toolbars across their row: the image pane's flush  */
        /* left, the Windows pane's flush right, the shared one centred in    */
        /* the window.  It also settles the two things that depend on the     */
        /* window's width: the SIZE OF THE TOOLBARS' PICTURES (View > Toolbar */
        /* Icons), and the WINDOW'S MINIMUM WIDTH, so that resizing cannot    */
        /* hide a button.  Called whenever the row's width or a toolbar's     */
        /* contents change.                                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the three toolbars are placed.                          */
        /*--------------------------------------------------------------------*/
        private void Main_LayoutToolbars()
        {
            int iLeftEnd;
            int iRightStart;
            int iRightWidth;
            int iSharedWidth;
            int iSharedLeft;
            int iNeeded;
            int iScreen;
            int iBorders;
            int iRowHeight;
            bool bWantSmall;

            if (Main_bToolLayoutBusy)
            {   /* Already running: what it changes (the pictures' size, the    */
                /* row's height, the window's minimum) raises the very events   */
                /* that call it.                                                */
                return;
            }

            /* Busy from here to the end of the function, which has no   */
            /* other exit.                                               */
            Main_bToolLayoutBusy = true;

            /* WHICH SIZE OF PICTURE.  Always small when the user chose that;   */
            /* otherwise the large ones, unless the window is too narrow to     */
            /* show every button at that size.                                  */
            iBorders   = this.Width - this.ClientSize.Width;
            bWantSmall = Main_tsmIconsSmall.Checked;

            if (!bWantSmall && Main_ToolbarsNeed(false) + iBorders > this.Width)
            {   /* Automatic, and the large pictures do not fit. */
                bWantSmall = true;
            }

            /* The pictures drawn at that size (nothing done if it is      */
            /* already showing); every width below is measured after it.   */
            Main_ApplyToolIconSize(bWantSmall);

            /* The three toolbars' widths as they now are: where the image   */
            /* pane's ends, the shared one's, and the Windows pane's --      */
            /* nothing until it is known to be showing.                      */
            iLeftEnd     = Main_atspSide[MAIN_SIDE_IMAGE].PreferredSize.Width;
            iSharedWidth = Main_tspShared.PreferredSize.Width;
            iRightWidth  = 0;

            if (Main_tsmShowHost.Checked)
            {   /* The Windows pane (and so its toolbar) is showing.  The tick   */
                /* mark is asked, not the toolbar's Visible: a control reports   */
                /* itself invisible until its form is on screen, which would     */
                /* place the toolbar off the row's edge at start-up.             */
                iRightWidth = Main_atspSide[MAIN_SIDE_HOST].PreferredSize.Width;
            }

            /* THE WINDOW MAY NOT BE MADE NARROWER THAN THE TOOLBARS NEED WITH   */
            /* THEIR SMALL PICTURES: the form's minimum width is the three       */
            /* widths at that size plus the window's own borders, so every       */
            /* button stays in view however the window is dragged.  It is        */
            /* recomputed here because the widths change (a view's name on a     */
            /* pull-down, the Windows pane hidden).  It is never set wider       */
            /* than the screen's working area, on which the window must still    */
            /* fit.                                                              */
            iNeeded = Main_ToolbarsNeed(true) + iBorders;
            iScreen = Screen.FromControl(this).WorkingArea.Width;

            if (iNeeded > iScreen)
            {   /* A small screen: the window must fit it, even if a toolbar is then cut short. */
                iNeeded = iScreen;
            }

            if (this.MinimumSize.Width != iNeeded)
            {   /* Changed: apply it (this widens the window at once if it is narrower). */
                this.MinimumSize = new Size(iNeeded, MAIN_MIN_HEIGHT);
            }

            /* The Windows pane's toolbar flush against the row's right   */
            /* edge.                                                      */
            iRightStart = Main_pnlTools.ClientSize.Width - iRightWidth;

            if (iRightStart < iLeftEnd + iSharedWidth)
            {   /* Too narrow for all three side by side (only on a screen    */
                /* smaller than the minimum): kept in order, unclipped on     */
                /* the left, so the Windows pane's toolbar is pushed past     */
                /* the row's right edge rather than laid over the others --   */
                /* its buttons are all in the menus too.                      */
                iRightStart = iLeftEnd + iSharedWidth;
            }

            /* Centred in the row ... */
            iSharedLeft = (Main_pnlTools.ClientSize.Width - iSharedWidth) / 2;

            if (iSharedLeft < iLeftEnd || iSharedLeft + iSharedWidth > iRightStart)
            {   /* ... unless that would cover a neighbour: then midway in the gap between them. */
                iSharedLeft = iLeftEnd + (iRightStart - iLeftEnd - iSharedWidth) / 2;
            }

            /* The row is as tall as its tallest toolbar (with the small      */
            /* pictures that is a pane's, whose pull-downs carry text), and   */
            /* each toolbar sits centred in that height.                      */
            iRowHeight = Main_tspShared.PreferredSize.Height;

            if (Main_atspSide[MAIN_SIDE_IMAGE].PreferredSize.Height > iRowHeight)
            {   /* The image pane's toolbar is the taller. */
                iRowHeight = Main_atspSide[MAIN_SIDE_IMAGE].PreferredSize.Height;
            }

            if (Main_pnlTools.Height != iRowHeight)
            {   /* Changed (the pictures' size did). */
                Main_pnlTools.Height = iRowHeight;
            }

            /* Each toolbar to its place: across, as worked out above;   */
            /* down, centred in the row's height.                        */
            Main_atspSide[MAIN_SIDE_IMAGE].Location = new Point(0, (iRowHeight - Main_atspSide[MAIN_SIDE_IMAGE].PreferredSize.Height) / 2);
            Main_tspShared.Location                 = new Point(iSharedLeft, (iRowHeight - Main_tspShared.PreferredSize.Height) / 2);
            Main_atspSide[MAIN_SIDE_HOST].Location  = new Point(iRightStart, (iRowHeight - Main_atspSide[MAIN_SIDE_HOST].PreferredSize.Height) / 2);

            /* Done: the events this run raised have come and gone, and the   */
            /* next change may lay the row out again.                         */
            Main_bToolLayoutBusy = false;
        }

        /*--------------------------------------------------------------------*/
        /* Main_ToolbarsNeed:                                                 */
        /*                                                                    */
        /* How wide the toolbars that are showing would be, side by side,     */
        /* with their pictures at one of the two sizes -- the size showing or */
        /* the other, without redrawing anything to find out.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bSmall : true for the small pictures, false for the large.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : the combined width, pixels.                              */
        /*--------------------------------------------------------------------*/
        private int Main_ToolbarsNeed(bool bSmall)
        {
            int iWidth;
            int iStep;
            int iButtons;
            int i;

            /* The measured widths of the toolbars showing, at the size now   */
            /* in use: the image pane's and the shared one always ...         */
            iWidth = Main_atspSide[MAIN_SIDE_IMAGE].PreferredSize.Width + Main_tspShared.PreferredSize.Width;

            if (Main_tsmShowHost.Checked)
            {   /* The Windows pane's toolbar is showing too (the tick mark is asked, as in Main_LayoutToolbars). */
                iWidth = iWidth + Main_atspSide[MAIN_SIDE_HOST].PreferredSize.Width;
            }

            if (bSmall == Main_bToolIconsSmall)
            {   /* The size in use: the measured widths are the answer. */
                return(iWidth);
            }

            /* The other size: count the picture buttons that are showing ... */
            iButtons = 0;

            for (i = 0; i < Main_lstToolButtons.Count; i++)
            {   /* One picture button per iteration. */
                if (!Main_lstToolButtons[i].Available)
                {   /* Taken off its toolbar (the copy buttons, while the Windows pane is hidden). */
                    continue;
                }

                if (Main_lstToolButtons[i].Owner == Main_atspSide[MAIN_SIDE_HOST] && !Main_tsmShowHost.Checked)
                {   /* On the hidden Windows pane's toolbar. */
                    continue;
                }

                /* A picture button that is showing: counted. */
                iButtons++;
            }

            /* ... and allow each the difference between the two picture   */
            /* widths: a picture button is wider or narrower by exactly    */
            /* that, and nothing else on a toolbar (a separator, a text    */
            /* pull-down) changes with the picture size.                   */
            iStep = Main_ToolIconSize().Width - SystemInformation.SmallIconSize.Width;

            if (bSmall)
            {   /* Large now: small would be narrower. */
                return(iWidth - (iButtons * iStep));
            }

            /* Small now: large would be wider. */
            return(iWidth + (iButtons * iStep));
        }

        /*--------------------------------------------------------------------*/
        /* Main_ApplyToolIconSize:                                            */
        /*                                                                    */
        /* Show the toolbars' pictures at the small size (the system's small  */
        /* icon) or the large (one and a half times that).  Every picture is  */
        /* DRAWN again at the new size rather than scaled, so it stays sharp. */
        /* Nothing is done when the size asked for is already showing.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bSmall : true for the small pictures.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the toolbars show that size (their widths have changed; */
        /*            the caller places them).                                */
        /*--------------------------------------------------------------------*/
        private void Main_ApplyToolIconSize(bool bSmall)
        {
            Image imgOld;
            Size  sizPicture;
            int   i;

            if (bSmall == Main_bToolIconsSmall)
            {   /* Already showing. */
                return;
            }

            /* The size wanted: the large pictures' (Main_ToolIconSize) ... */
            sizPicture = Main_ToolIconSize();

            if (bSmall)
            {   /* ... or the system's small icon size. */
                sizPicture = SystemInformation.SmallIconSize;
            }

            /* A toolbar scales its pictures to this size, so it is set first. */
            Main_atspSide[MAIN_SIDE_IMAGE].ImageScalingSize = sizPicture;
            Main_atspSide[MAIN_SIDE_HOST].ImageScalingSize  = sizPicture;
            Main_tspShared.ImageScalingSize                 = sizPicture;

            for (i = 0; i < Main_lstToolButtons.Count; i++)
            {   /* One picture button per iteration: a new picture, and the old one released. */
                imgOld                       = Main_lstToolButtons[i].Image;
                Main_lstToolButtons[i].Image = TIC_ToolIcons.TIC_NewPicture((int)Main_lstToolButtons[i].Tag, sizPicture);
                imgOld.Dispose();
            }

            /* Recorded: Main_ToolbarsNeed reads it to tell the measured   */
            /* size from the other.                                        */
            Main_bToolIconsSmall = bSmall;
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnIconsAutoClicked:                                           */
        /*                                                                    */
        /* View > Toolbar Icons > Automatic was chosen: the large pictures,   */
        /* the small ones taking over only while the window is too narrow     */
        /* for them (Main_SetToolIconsSmall).                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the toolbars follow the automatic choice.               */
        /*--------------------------------------------------------------------*/
        private void Main_OnIconsAutoClicked(object objSender, EventArgs evArgs)
        {
            /* Automatic: not always small. */
            Main_SetToolIconsSmall(false);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnIconsSmallClicked:                                          */
        /*                                                                    */
        /* View > Toolbar Icons > Always Small was chosen: the small pictures */
        /* whatever the window's width (Main_SetToolIconsSmall).              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the toolbars show the small pictures.                   */
        /*--------------------------------------------------------------------*/
        private void Main_OnIconsSmallClicked(object objSender, EventArgs evArgs)
        {
            /* Always small. */
            Main_SetToolIconsSmall(true);
        }

        /*--------------------------------------------------------------------*/
        /* Main_SetToolIconsSmall:                                            */
        /*                                                                    */
        /* Take one of View > Toolbar Icons' two choices: move the mark to    */
        /* its item, remember the choice, and lay the toolbars out again,     */
        /* which is where the choice takes effect.  The two items' handlers   */
        /* (Main_OnIconsAutoClicked, Main_OnIconsSmallClicked) name the       */
        /* choice; nothing here asks which item was clicked.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bAlwaysSmall : true for Always Small, false for Automatic.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the toolbars show the pictures as chosen.               */
        /*--------------------------------------------------------------------*/
        private void Main_SetToolIconsSmall(bool bAlwaysSmall)
        {
            int iAlwaysSmall;

            /* The mark onto the item chosen and off the other: the two   */
            /* are always opposite (they are not CheckOnClick items).     */
            Main_tsmIconsSmall.Checked = bAlwaysSmall;
            Main_tsmIconsAuto.Checked  = !Main_tsmIconsSmall.Checked;

            /* The registry's value: 0 for Automatic unless ... */
            iAlwaysSmall = 0;

            if (Main_tsmIconsSmall.Checked)
            {   /* ... Always Small, stored as 1. */
                iAlwaysSmall = 1;
            }

            /* Remembered for the next run, then the toolbars laid out   */
            /* again, which is where the choice takes effect.            */
            SET_Settings.SET_WriteInt(SET_Settings.SET_TOOL_SMALL, iAlwaysSmall);
            Main_LayoutToolbars();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnToolRowResize:                                              */
        /*                                                                    */
        /* The toolbar row changed width (the window was resized): place the  */
        /* three toolbars again.                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the row's panel.                                   */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the toolbars are placed for the new width.              */
        /*--------------------------------------------------------------------*/
        private void Main_OnToolRowResize(object objSender, EventArgs evArgs)
        {
            /* The layout redone for the new width -- which may also change   */
            /* the pictures' size and the row's height; the layout's own      */
            /* busy flag stops the resize that causes from looping back.      */
            Main_LayoutToolbars();
        }

        /*--------------------------------------------------------------------*/
        /* Main_UpdateToolbar:                                                */
        /*                                                                    */
        /* Grey out or enable every toolbar button, and the View menu's Back  */
        /* and Up, for the present state.  Each pane's own toolbar follows    */
        /* THAT pane -- its selection, its folder, its Back history, and for  */
        /* the image pane whether an image is open and writable -- whichever  */
        /* pane is the active one.  The rules are the ones the menus use      */
        /* (Main_UpdateEditCommands), with one difference: Paste here does    */
        /* not look at the clipboard -- this runs on every change of          */
        /* selection, and asking the clipboard that often is not worth it --  */
        /* so it is enabled whenever the pane could take a paste, and a       */
        /* paste of something unusable says so in the status bar.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : every button's Enabled matches the present state.       */
        /*--------------------------------------------------------------------*/
        private void Main_UpdateToolbar()
        {
            int[]  aiSelected;
            bool[] abUsable;
            bool[] abCanCut;
            bool[] abCanBack;
            bool[] abCanUp;
            bool   bBothSides;
            int    iActive;
            int    iSide;

            /* Each pane's facts, one slot per pane (MAIN_SIDE_*): how many   */
            /* items are selected, whether the pane is in use at all, and     */
            /* whether Cut, Back and Up can act there.                        */
            aiSelected = new int[MAIN_SIDES];
            abUsable   = new bool[MAIN_SIDES];
            abCanCut   = new bool[MAIN_SIDES];
            abCanBack  = new bool[MAIN_SIDES];
            abCanUp    = new bool[MAIN_SIDES];

            /* The image pane: usable while an image is open; Cut needs it writable too. */
            aiSelected[MAIN_SIDE_IMAGE] = Main_SelectedCount(Main_lstImage);
            abUsable[MAIN_SIDE_IMAGE]   = Main_bImageOpen;
            abCanCut[MAIN_SIDE_IMAGE]   = Main_bImageOpen && aiSelected[MAIN_SIDE_IMAGE] != 0 && !Main_bReadOnly;
            abCanBack[MAIN_SIDE_IMAGE]  = Main_bImageOpen && Main_lstImageBack.Count != 0;
            abCanUp[MAIN_SIDE_IMAGE]    = Main_bImageOpen && Main_sImageDir != "/";

            /* The Windows pane: usable while it shows a folder, not the drive list. */
            aiSelected[MAIN_SIDE_HOST] = Main_SelectedCount(Main_lstHost);
            abUsable[MAIN_SIDE_HOST]   = (Main_sHostDir.Length != 0);
            abCanCut[MAIN_SIDE_HOST]   = abUsable[MAIN_SIDE_HOST] && aiSelected[MAIN_SIDE_HOST] != 0;
            abCanBack[MAIN_SIDE_HOST]  = (Main_lstHostBack.Count != 0);
            abCanUp[MAIN_SIDE_HOST]    = (Main_sHostDir.Length != 0);

            /* Each pane's own toolbar from that pane's facts alone: Copy,    */
            /* Delete and Rename / Properties need a selection (Rename and    */
            /* Properties exactly one item); Paste, New Folder and New need   */
            /* only a pane in use.                                            */
            for (iSide = 0; iSide < MAIN_SIDES; iSide++)
            {   /* One pane's own toolbar per iteration. */
                Main_atsbBack[iSide].Enabled      = abCanBack[iSide];
                Main_atsbUp[iSide].Enabled        = abCanUp[iSide];
                Main_atsbCut[iSide].Enabled       = abCanCut[iSide];
                Main_atsbCopy[iSide].Enabled      = abUsable[iSide] && aiSelected[iSide] != 0;
                Main_atsbPaste[iSide].Enabled     = abUsable[iSide];
                Main_atsbNewFolder[iSide].Enabled = abUsable[iSide];
                Main_atsdNew[iSide].Enabled       = abUsable[iSide];
                Main_atsbRename[iSide].Enabled    = abUsable[iSide] && aiSelected[iSide] == 1;
                Main_atsbDelete[iSide].Enabled    = abUsable[iSide] && aiSelected[iSide] != 0;
                Main_atsbProperties[iSide].Enabled = abUsable[iSide] && aiSelected[iSide] == 1;
            }

            /* A copy across needs an open image AND a real Windows folder, in a pane that is showing. */
            bBothSides = Main_bImageOpen && Main_sHostDir.Length != 0 && Main_tsmShowHost.Checked;

            /* Enable Writing only while an image is open read-only, and never   */
            /* on squashfs, which cannot be written; each copy across only       */
            /* with a selection in the pane it copies FROM (Import from the      */
            /* Windows pane, Extract from the image).                            */
            Main_tsbWrite.Enabled      = Main_bImageOpen && Main_bReadOnly && !Main_OpenIsSquashfs();
            Main_tsbToImage.Enabled    = bBothSides && aiSelected[MAIN_SIDE_HOST] != 0;
            Main_tsbToHost.Enabled     = bBothSides && aiSelected[MAIN_SIDE_IMAGE] != 0;

            /* View > Back and Up act on the ACTIVE pane, so they follow it. */
            iActive = MAIN_SIDE_IMAGE;

            if (Main_bHostPaneActive)
            {   /* The Windows pane was the last one used. */
                iActive = MAIN_SIDE_HOST;
            }

            /* The two items from the active pane's facts. */
            Main_tsmBack.Enabled = abCanBack[iActive];
            Main_tsmUp.Enabled   = abCanUp[iActive];

            /* The Commands menu from the same facts -- and it ends by        */
            /* narrowing everything by the worker's state                     */
            /* (Main_ApplyCommandAvailability).  It runs AFTER the buttons    */
            /* above are set, so a selection event arriving while a command   */
            /* runs cannot leave an image button enabled: the narrowing has   */
            /* the last word.                                                 */
            Main_UpdateCommandsMenu();
        }

        /*--------------------------------------------------------------------*/
        /* Main_UpdateCommandsMenu:                                           */
        /*                                                                    */
        /* Grey out or enable every item of the Commands menu for the present */
        /* state: a command that needs a selected item is greyed while        */
        /* nothing is selected in the pane it acts on.  Open, Open As, Rename */
        /* and Properties need exactly one; Delete needs at least one; New    */
        /* and New Folder need only a usable pane; Extract and Import act on  */
        /* the image pane's and the Windows pane's selection respectively,    */
        /* whichever pane is the active one, and need both panes in use.      */
        /*                                                                    */
        /* THIS RUNS ON EVERY CHANGE OF SELECTION OR PANE, not only as the    */
        /* menu opens (it is called from Main_UpdateToolbar), and it has to:  */
        /* a greyed menu item's shortcut key does nothing, so a state left    */
        /* stale until the menu was next opened would leave F2 or Ctrl+E dead */
        /* after an item had been selected.                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : every Commands item's Enabled matches the present       */
        /*            state.                                                  */
        /*--------------------------------------------------------------------*/
        private void Main_UpdateCommandsMenu()
        {
            int  iSelected;
            bool bPaneUsable;
            bool bBothSides;

            /* How many items are selected in the active pane (the ".."    */
            /* row is not an item, Main_SelectedCount), and whether that   */
            /* pane is in use at all.                                      */
            iSelected = Main_SelectedCount(Main_ActiveList());

            if (Main_bHostPaneActive)
            {   /* The Windows pane: usable when it shows a folder, not the drive list. */
                bPaneUsable = (Main_sHostDir.Length != 0);
            }
            else
            {   /* The image pane: usable when an image is open. */
                bPaneUsable = Main_bImageOpen;
            }

            /* Open takes one ROW -- the ".." row included, which opens the folder above; Open As one ITEM. */
            Main_tsmOpen.Enabled   = (Main_ActiveList().SelectedItems.Count == 1);
            Main_tsmOpenAs.Enabled = bPaneUsable && iSelected == 1;
            Main_tsmNew.Enabled    = bPaneUsable;

            /* A copy across needs an open image AND a real Windows folder, in a pane that is showing. */
            bBothSides = Main_bImageOpen && Main_sHostDir.Length != 0 && Main_tsmShowHost.Checked;

            /* Each copy from its own pane's selection, whichever pane is   */
            /* active: Extract from the image, Import from Windows.         */
            Main_tsmExtract.Enabled = bBothSides && Main_SelectedCount(Main_lstImage) != 0;
            Main_tsmImport.Enabled  = bBothSides && Main_SelectedCount(Main_lstHost) != 0;

            /* The edits, in the active pane: New Folder needs only the   */
            /* pane, Rename exactly one item, Delete at least one.        */
            Main_tsmNewFolder.Enabled  = bPaneUsable;
            Main_tsmRename.Enabled     = bPaneUsable && iSelected == 1;
            Main_tsmDelete.Enabled     = bPaneUsable && iSelected != 0;

            /* Properties: one item, in either pane -- or, in the image pane   */
            /* with no row selected at all, the filesystem itself.             */
            if (Main_FsPropertiesApply())
            {   /* Nothing picked in the image pane: the filesystem's window. */
                Main_tsmProperties.Text    = "Filesystem &Properties...";
                Main_tsmProperties.Enabled = true;
            }
            else
            {   /* An entry's properties. */
                Main_tsmProperties.Text    = "&Properties...";
                Main_tsmProperties.Enabled = bPaneUsable && iSelected == 1;
            }

            /* Then everything narrowed by the worker's state -- greyed      */
            /* while it runs, an Exit is pending, or nothing is open         */
            /* (Main_ApplyCommandAvailability).  Done here and not only in   */
            /* Main_UpdateToolbar because the menu's opening calls this      */
            /* alone; and since a greyed item's shortcut does nothing, the   */
            /* keys follow the same state.  It is advice only: the worker    */
            /* checks every request again when it takes it.                  */
            Main_ApplyCommandAvailability();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnCommandsMenuOpening:                                        */
        /*                                                                    */
        /* The Commands menu is about to drop down: set its items' states     */
        /* once more, so they are right even if a change slipped past the     */
        /* events that normally keep them so.                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the Commands menu.                                 */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the items are enabled or greyed as they apply.          */
        /*--------------------------------------------------------------------*/
        private void Main_OnCommandsMenuOpening(object objSender, EventArgs evArgs)
        {
            /* The same update the selection changes run, once more. */
            Main_UpdateCommandsMenu();
        }

        /*----------------------------------------------------------------------*/
        /* Main_OnImageSelectionChanged:                                        */
        /*                                                                      */
        /* The image pane's list's selection changed (Main_PaneSelectionChanged */
        /* says what follows).                                                  */
        /*                                                                      */
        /* Arguments:                                                           */
        /*     objSender : the image pane's list (unused).                      */
        /*     evArgs    : unused.                                              */
        /*                                                                      */
        /* Returns:                                                             */
        /*     void : a toolbar update is waiting to run.                       */
        /*----------------------------------------------------------------------*/
        private void Main_OnImageSelectionChanged(object objSender, EventArgs evArgs)
        {
            /* The image pane's list, named. */
            Main_PaneSelectionChanged(Main_lstImage);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnHostSelectionChanged:                                       */
        /*                                                                    */
        /* The same for the Windows pane's list.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the Windows pane's list (unused).                  */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : a toolbar update is waiting to run.                     */
        /*--------------------------------------------------------------------*/
        private void Main_OnHostSelectionChanged(object objSender, EventArgs evArgs)
        {
            /* The Windows pane's list, named. */
            Main_PaneSelectionChanged(Main_lstHost);
        }

        /*--------------------------------------------------------------------*/
        /* Main_PaneSelectionChanged:                                         */
        /*                                                                    */
        /* The selection changed in one of the lists.  The toolbar's state    */
        /* and the item check boxes' ticks depend on it, but this event       */
        /* arrives once PER ROW when many rows are selected at once, so each  */
        /* update is POSTED and only one of each is ever waiting: a burst of  */
        /* events becomes a single update after the last.                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstPane : the list whose selection changed.                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : a toolbar update is waiting to run.                     */
        /*--------------------------------------------------------------------*/
        private void Main_PaneSelectionChanged(ListView lstPane)
        {
            /* Item check boxes: the ticks follow the new selection, once the    */
            /* burst is over (Main_PostItemChecks does nothing without boxes).   */
            Main_PostItemChecks(lstPane);

            if (Main_bToolbarPending || !this.IsHandleCreated)
            {   /* One is already waiting, or the window is not up yet (nothing to update for). */
                return;
            }

            /* One update now waiting: the events still to come in this   */
            /* burst find the flag up and add nothing.                    */
            Main_bToolbarPending = true;

            /* Posted to the window's message loop, behind the rest of the   */
            /* burst's events, so it runs after the last of them.            */
            this.BeginInvoke(new MethodInvoker(Main_RunToolbarUpdate));
        }

        /*--------------------------------------------------------------------*/
        /* Main_RunToolbarUpdate:                                             */
        /*                                                                    */
        /* The toolbar update Main_PaneSelectionChanged posts, run once the   */
        /* burst of selection events has been handled.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the toolbar matches the selection.                      */
        /*--------------------------------------------------------------------*/
        private void Main_RunToolbarUpdate()
        {
            /* The flag is lowered first, so a change made later posts afresh. */
            Main_bToolbarPending = false;
            Main_UpdateToolbar();
        }

        /*--------------------------------------------------------------------*/
        /* Main_PushBack:                                                     */
        /*                                                                    */
        /* Add a folder to a pane's Back history, as the pane leaves it.  The */
        /* history is capped: beyond the cap the oldest entry is dropped.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstHistory : the pane's history, oldest first.                 */
        /*     sLeaving   : the folder being left.                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the folder is the newest entry.                         */
        /*--------------------------------------------------------------------*/
        private static void Main_PushBack(List<string> lstHistory, string sLeaving)
        {
            /* The newest at the end, where Back takes from. */
            lstHistory.Add(sLeaving);

            if (lstHistory.Count > MAIN_BACK_LIMIT)
            {   /* Over the cap: forget the oldest. */
                lstHistory.RemoveAt(0);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_GoBack:                                                       */
        /*                                                                    */
        /* Back, in one pane: show the folder the pane was showing before     */
        /* the present one.  The entry is taken off the history only when     */
        /* that folder is shown (the image pane's, by Main_ShowListResult;    */
        /* the Windows pane's, when its pane action runs), so a Back that is  */
        /* refused or overtaken loses nothing.  The navigation is flagged as  */
        /* a Back so that it does not put the                                 */
        /* folder being left onto the history in its turn (Back would then    */
        /* just bounce between two folders).                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bImagePane : true for the image pane, false for the Windows    */
        /*                  pane.                                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the pane is going back, or the status bar says there is */
        /*            nowhere to go back to.                                  */
        /*--------------------------------------------------------------------*/
        private void Main_GoBack(bool bImagePane)
        {
            string sTarget;

            if (bImagePane)
            {   /* The image pane. */
                if (!Main_bImageOpen || Main_lstImageBack.Count == 0)
                {   /* No image, or no history. */
                    Main_SetStatus("There is no earlier folder to go back to.");
                    return;
                }

                /* The newest entry read, NOT taken off, and the listing   */
                /* asked for as a Back: the flag goes onto the request     */
                /* (Main_NavigateImage), and the entry is spent when the   */
                /* listing is shown.                                       */
                sTarget = Main_lstImageBack[Main_lstImageBack.Count - 1];
                Main_bImageBackNav = true;
                Main_NavigateImage(sTarget);
                return;
            }

            if (Main_lstHostBack.Count == 0)
            {   /* The Windows pane has no history. */
                Main_SetStatus("There is no earlier folder to go back to.");
                return;
            }

            /* The Windows pane: the newest entry read, not taken off --     */
            /* the pane action takes it off when it runs, if it still        */
            /* heads the history (Main_ExecutePaneAction, MainCommand.cs).   */
            sTarget = Main_lstHostBack[Main_lstHostBack.Count - 1];
            Main_RequestPaneAction(HPA_enmHostAction.NavigateBack, null, sTarget);
        }

        /*--------------------------------------------------------------------*/
        /* Main_GoUp:                                                         */
        /*                                                                    */
        /* Up one level, in one pane.  In the image pane that is the parent   */
        /* directory, and nothing at the root.  In the Windows pane it is the */
        /* parent folder, the list of drives above a drive's root, and        */
        /* nothing above the list of drives.                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bImagePane : true for the image pane, false for the Windows    */
        /*                  pane.                                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the pane is going up, or is already at the top.         */
        /*--------------------------------------------------------------------*/
        private void Main_GoUp(bool bImagePane)
        {
            DirectoryInfo dirParent;

            if (bImagePane)
            {   /* The image pane: the parent directory, unless at the root. */
                if (Main_bImageOpen && Main_sImageDir != "/")
                {   /* There is a level above. */
                    Main_NavigateImage(Main_ImageParentDir(Main_sImageDir));
                }

                return;
            }

            if (Main_sHostDir.Length == 0)
            {   /* The list of drives is the top. */
                return;
            }

            /* The folder's parent, which the framework answers null for a   */
            /* drive's root.                                                 */
            dirParent = Directory.GetParent(Main_sHostDir);

            if (dirParent == null)
            {   /* A drive's root: go up to the list of drives. */
                Main_RequestPaneAction(HPA_enmHostAction.Navigate, null, "");
            }
            else
            {   /* An ordinary parent folder. */
                Main_RequestPaneAction(HPA_enmHostAction.Navigate, null, dirParent.FullName);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnBackClicked:                                                */
        /*                                                                    */
        /* View > Back: back, in the active pane.  (Each pane's Back button   */
        /* has its own handler, Main_OnImageBackButtonClicked and             */
        /* Main_OnHostBackButtonClicked.)                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the active pane is going back, if it has anywhere to.   */
        /*--------------------------------------------------------------------*/
        private void Main_OnBackClicked(object objSender, EventArgs evArgs)
        {
            /* In the active pane. */
            Main_GoBack(!Main_bHostPaneActive);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnUpClicked:                                                  */
        /*                                                                    */
        /* View > Up One Level: up, in the active pane.  (Each pane's Up      */
        /* button has its own handler.)                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the active pane is going up, if there is a level above. */
        /*--------------------------------------------------------------------*/
        private void Main_OnUpClicked(object objSender, EventArgs evArgs)
        {
            /* In the active pane, as Back is. */
            Main_GoUp(!Main_bHostPaneActive);
        }

        /*--------------------------------------------------------------------*/
        /* Main_UpdateEditCommands:                                           */
        /*                                                                    */
        /* Grey out or enable every command whose availability depends on     */
        /* the active pane, its selection, the clipboard and the image's      */
        /* state -- in the Edit menu and in the right-click menu alike.       */
        /* Called as either menu opens, so the state is never stale.          */
        /*                                                                    */
        /* THE RULE FOR CUT: in the image pane it is greyed unless the image  */
        /* is open FOR WRITING, because a cut is the start of a move and a    */
        /* move changes the image.  In the Windows pane cutting needs only a  */
        /* selection.                                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : each item's Enabled, and the transfer item's text,      */
        /*            match the present state.                                */
        /*--------------------------------------------------------------------*/
        private void Main_UpdateEditCommands()
        {
            int  iSelected;
            bool bPaneUsable;
            bool bCanCut;
            bool bCanPaste;

            /* How many items are selected in the active pane. */
            iSelected = Main_SelectedCount(Main_ActiveList());

            if (Main_bHostPaneActive)
            {   /* The Windows pane: usable when it shows a folder, not the drive list. */
                bPaneUsable = (Main_sHostDir.Length != 0);
                bCanCut     = bPaneUsable && iSelected != 0;
            }
            else
            {   /* The image pane: usable when an image is open; Cut needs it writable too. */
                bPaneUsable = Main_bImageOpen;
                bCanCut     = bPaneUsable && iSelected != 0 && !Main_bReadOnly;
            }

            /* Paste needs a destination and something this program can paste. */
            bCanPaste = bPaneUsable &&
                        (Clipboard.ContainsFileDropList() || Clipboard.ContainsData(MAIN_DRAG_IMAGE_FORMAT));

            /* The Edit menu's three: Copy needs a selection in a usable   */
            /* pane, Cut and Paste as worked out above.                    */
            Main_tsmCut.Enabled      = bCanCut;
            Main_tsmCopy.Enabled     = bPaneUsable && iSelected != 0;
            Main_tsmPaste.Enabled    = bCanPaste;

            /* The right-click menu's: the same three, then the edits --   */
            /* Delete needs a selection, Rename exactly one item, New      */
            /* Folder only a usable pane.                                  */
            Main_tsmCtxCut.Enabled       = bCanCut;
            Main_tsmCtxCopy.Enabled      = bPaneUsable && iSelected != 0;
            Main_tsmCtxPaste.Enabled     = bCanPaste;
            Main_tsmCtxDelete.Enabled    = bPaneUsable && iSelected != 0;
            Main_tsmCtxRename.Enabled    = bPaneUsable && iSelected == 1;
            Main_tsmCtxNewFolder.Enabled = bPaneUsable;

            /* The copy across goes the other way from each pane, and needs   */
            /* both an open image and a real Windows folder to go between.    */
            if (Main_bHostPaneActive)
            {   /* From the Windows pane: into the image. */
                Main_tsmCtxTransfer.Text = "&Import into Image";
            }
            else
            {   /* From the image pane: out to Windows. */
                Main_tsmCtxTransfer.Text = "&Extract to Windows";
            }

            /* Enabled with an image open, a real Windows folder, a         */
            /* selection and the Windows pane showing -- as the Commands    */
            /* menu and the toolbar require -- so a copy never lands in a   */
            /* folder that is not on show.                                  */
            Main_tsmCtxTransfer.Enabled = Main_bImageOpen && Main_sHostDir.Length != 0 && iSelected != 0 &&
                                          Main_tsmShowHost.Checked;

            /* Properties: one item, in either pane (the image's own window, or   */
            /* Windows') -- or, right-clicked on the image pane with no row       */
            /* selected (a click on empty space clears the selection), the        */
            /* filesystem itself.                                                 */
            if (Main_FsPropertiesApply())
            {   /* The image pane's background: the filesystem's window. */
                Main_tsmCtxProperties.Text    = "Filesystem P&roperties...";
                Main_tsmCtxProperties.Enabled = true;
            }
            else
            {   /* An entry's properties. */
                Main_tsmCtxProperties.Text    = "P&roperties...";
                Main_tsmCtxProperties.Enabled = bPaneUsable && iSelected == 1;
            }

            /* Open: one row, the ".." row included (it opens the folder        */
            /* above), so the rows are counted, not the items.  Open As takes   */
            /* one item, in either pane; New only a usable pane.                */
            Main_tsmCtxOpen.Enabled   = (Main_ActiveList().SelectedItems.Count == 1);
            Main_tsmCtxOpenAs.Enabled = bPaneUsable && iSelected == 1;
            Main_tsmCtxNew.Enabled    = bPaneUsable;

            /* Then narrowed by the worker's state, after the selection and   */
            /* clipboard rules above (Main_ApplyCommandAvailability), as      */
            /* the Commands menu is.  An enabled item grants nothing: the     */
            /* worker checks the request again when it takes it.              */
            Main_ApplyCommandAvailability();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnPaneMenuOpening:                                            */
        /*                                                                    */
        /* The right-click menu is about to appear over one of the lists:     */
        /* make that list's pane the active one (a right click does not       */
        /* always move the focus first), then set every item's state for it.  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the context menu; its SourceControl is the list    */
        /*                 it was opened over.                                */
        /*     evCancel  : unused (the menu is never cancelled).              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the active pane and the items' states are set.          */
        /*--------------------------------------------------------------------*/
        private void Main_OnPaneMenuOpening(object objSender, System.ComponentModel.CancelEventArgs evCancel)
        {
            /* The pane the menu opened over is the active one from now:   */
            /* recorded and outlined (the focus is not moved -- the menu   */
            /* is about to take it).                                       */
            Main_bHostPaneActive = (Main_cmsPane.SourceControl == Main_lstHost);
            Main_MarkActivePane();

            /* Everything that follows the active pane brought up to date   */
            /* for it, the shared items' states (UpdateToolbar also runs    */
            /* the Commands menu's) and then this menu's own.               */
            Main_UpdateViewMenu();
            Main_UpdateToolbar();
            Main_UpdateEditCommands();

            if (Main_bHostPaneActive)
            {   /* View and Sort By are the Windows pane's. */
                Main_FillViewMenu(MAIN_SIDE_HOST);
                Main_FillSortMenu(MAIN_SIDE_HOST);
            }
            else
            {   /* View and Sort By are the image pane's. */
                Main_FillViewMenu(MAIN_SIDE_IMAGE);
                Main_FillSortMenu(MAIN_SIDE_IMAGE);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_FillViewMenu:                                                 */
        /*                                                                    */
        /* Fill the right-click menu's View sub-menu for one pane: the five   */
        /* views in the order the View menu lists them, with a mark on the    */
        /* one that pane shows now.  It is rebuilt each time the menu opens   */
        /* because the menu is shared by both panes and the mark belongs to   */
        /* whichever it opened over; the items are therefore NOT added to     */
        /* Main_alstViewItems, which holds the permanent items the mark is    */
        /* kept right on between openings.                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iSide : MAIN_SIDE_IMAGE or MAIN_SIDE_HOST.                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the sub-menu lists that pane's views.                   */
        /*--------------------------------------------------------------------*/
        private void Main_FillViewMenu(int iSide)
        {
            ToolStripMenuItem  tsmItem;
            ListView           lstPane;
            Mpb_clsPaneBinding objBinding;
            int                i;

            /* The pane's list, whose view is marked; the sub-menu emptied   */
            /* of the last opening's items.                                  */
            lstPane = Main_SideList(iSide);
            Main_tsmCtxView.DropDownItems.Clear();

            /* The pane, held for the items' shared click handler. */
            objBinding = new Mpb_clsPaneBinding(this, iSide);

            for (i = 0; i < Main_aenmViewOrder.Length; i++)
            {   /* One view per iteration, marked when it is the pane's current one. */
                tsmItem         = new ToolStripMenuItem(Main_asViewTexts[i]);
                tsmItem.Tag     = Main_aenmViewOrder[i];
                tsmItem.Checked = (lstPane.View == Main_aenmViewOrder[i]);

                /* Its click chooses its view for THIS pane, the view read   */
                /* back from the Tag (the pane's binding, made above).       */
                tsmItem.Click += objBinding.MPB_OnViewClick;

                /* Into the sub-menu, in menu order. */
                Main_tsmCtxView.DropDownItems.Add(tsmItem);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_NewSorter:                                                    */
        /*                                                                    */
        /* Create one pane's row sorter from its remembered column and        */
        /* direction; by name, ascending, on a first run or when the stored   */
        /* column is not one the pane has.                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iSide : MAIN_SIDE_IMAGE or MAIN_SIDE_HOST.                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     PSR_clsPaneSorter : the sorter (not yet given to the list; the */
        /*                      list takes it each time it is filled).        */
        /*--------------------------------------------------------------------*/
        private PSR_clsPaneSorter Main_NewSorter(int iSide)
        {
            int iColumn;
            int iDescending;

            if (iSide == MAIN_SIDE_HOST)
            {   /* The Windows pane's remembered order. */
                iColumn     = SET_Settings.SET_ReadInt(SET_Settings.SET_HOST_SORT, 0);
                iDescending = SET_Settings.SET_ReadInt(SET_Settings.SET_HOST_SORT_DESC, 0);
            }
            else
            {   /* The image pane's remembered order. */
                iColumn     = SET_Settings.SET_ReadInt(SET_Settings.SET_IMAGE_SORT, 0);
                iDescending = SET_Settings.SET_ReadInt(SET_Settings.SET_IMAGE_SORT_DESC, 0);
            }

            if (iColumn < 0 || iColumn >= Main_SideList(iSide).Columns.Count)
            {   /* A damaged value: fall back to the Name column. */
                iColumn = 0;
            }

            /* The sorter, as remembered. */
            return(new PSR_clsPaneSorter(iSide == MAIN_SIDE_IMAGE, iColumn, iDescending == 1));
        }

        /*--------------------------------------------------------------------*/
        /* Main_SetSort:                                                      */
        /*                                                                    */
        /* Sort one pane's list by a column, in a direction; do it now, and   */
        /* remember it for that pane.                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iSide       : MAIN_SIDE_IMAGE or MAIN_SIDE_HOST.               */
        /*     iColumn     : the column to sort by.                           */
        /*     bDescending : true for descending order.                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the list is in the new order.                           */
        /*--------------------------------------------------------------------*/
        private void Main_SetSort(int iSide, int iColumn, bool bDescending)
        {
            int iDescending;

            /* The pane's sorter given the new column and direction. */
            Main_asrtSide[iSide].PSR_iColumn     = iColumn;
            Main_asrtSide[iSide].PSR_bDescending = bDescending;

            /* The list already holds this sorter; Sort applies its new      */
            /* settings.  The grid views must then place the rows again in   */
            /* their new order (the names themselves are unchanged).         */
            Main_SideList(iSide).Sort();
            Main_GridChanged(Main_SideList(iSide), false);
            Main_SizeColumns(Main_SideList(iSide));

            /* The registry's value for the direction: 0 ascending unless ... */
            iDescending = 0;

            if (bDescending)
            {   /* ... descending, stored as 1. */
                iDescending = 1;
            }

            if (iSide == MAIN_SIDE_HOST)
            {   /* Remember it as the Windows pane's order. */
                SET_Settings.SET_WriteInt(SET_Settings.SET_HOST_SORT, iColumn);
                SET_Settings.SET_WriteInt(SET_Settings.SET_HOST_SORT_DESC, iDescending);
            }
            else
            {   /* Remember it as the image pane's order. */
                SET_Settings.SET_WriteInt(SET_Settings.SET_IMAGE_SORT, iColumn);
                SET_Settings.SET_WriteInt(SET_Settings.SET_IMAGE_SORT_DESC, iDescending);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_ColumnClicked:                                                */
        /*                                                                    */
        /* A column heading was clicked in one pane's list (details view):    */
        /* sort by that column.  Clicking the column the list is already      */
        /* sorted by reverses the direction, as in Explorer; a different      */
        /* column starts ascending.  Each pane's list has its own handler     */
        /* (Main_OnImageColumnClicked, Main_OnHostColumnClicked), which names */
        /* the pane.                                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iSide   : MAIN_SIDE_IMAGE or MAIN_SIDE_HOST.                   */
        /*     iColumn : the column clicked.                                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the list is sorted by the clicked column.               */
        /*--------------------------------------------------------------------*/
        private void Main_ColumnClicked(int iSide, int iColumn)
        {
            bool bDescending;

            /* A new column starts ascending ... */
            bDescending = false;

            if (Main_asrtSide[iSide].PSR_iColumn == iColumn)
            {   /* ... the same column again: the other direction. */
                bDescending = !Main_asrtSide[iSide].PSR_bDescending;
            }

            /* Sorted now, and remembered for the pane. */
            Main_SetSort(iSide, iColumn, bDescending);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnImageColumnClicked:                                         */
        /*                                                                    */
        /* A column heading of the image pane's list was clicked.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the image pane's list (unused).                    */
        /*     evColumn  : the event; its Column is the one clicked.          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the image pane is sorted by that column.                */
        /*--------------------------------------------------------------------*/
        private void Main_OnImageColumnClicked(object objSender, ColumnClickEventArgs evColumn)
        {
            /* The image pane, by that column. */
            Main_ColumnClicked(MAIN_SIDE_IMAGE, evColumn.Column);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnHostColumnClicked:                                          */
        /*                                                                    */
        /* A column heading of the Windows pane's list was clicked.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the Windows pane's list (unused).                  */
        /*     evColumn  : the event; its Column is the one clicked.          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the Windows pane is sorted by that column.              */
        /*--------------------------------------------------------------------*/
        private void Main_OnHostColumnClicked(object objSender, ColumnClickEventArgs evColumn)
        {
            /* The Windows pane, by that column. */
            Main_ColumnClicked(MAIN_SIDE_HOST, evColumn.Column);
        }

        /*--------------------------------------------------------------------*/
        /* Main_FillSortMenu:                                                 */
        /*                                                                    */
        /* Fill the right-click menu's Sort By sub-menu for one pane: an item */
        /* for each of that pane's columns, then Ascending and Descending,    */
        /* with a mark on the column and the direction now in force.  It is   */
        /* rebuilt each time the menu opens because the two panes have        */
        /* different columns.                                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iSide : MAIN_SIDE_IMAGE or MAIN_SIDE_HOST.                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the sub-menu lists that pane's sort choices.            */
        /*--------------------------------------------------------------------*/
        private void Main_FillSortMenu(int iSide)
        {
            ToolStripMenuItem  tsmItem;
            ListView           lstPane;
            Mpb_clsPaneBinding objBinding;
            int                iColumn;

            /* The pane's list, whose columns are offered; the sub-menu   */
            /* emptied of the last opening's items.                       */
            lstPane = Main_SideList(iSide);
            Main_tsmCtxSort.DropDownItems.Clear();

            /* The pane, held for every item's click handler. */
            objBinding = new Mpb_clsPaneBinding(this, iSide);

            for (iColumn = 0; iColumn < lstPane.Columns.Count; iColumn++)
            {   /* One column per iteration. */
                tsmItem         = new ToolStripMenuItem(lstPane.Columns[iColumn].Text);
                tsmItem.Tag     = iColumn;
                tsmItem.Checked = (Main_asrtSide[iSide].PSR_iColumn == iColumn);

                /* Its click sorts THIS pane by the column read back from   */
                /* the Tag, keeping the direction in force.                 */
                tsmItem.Click += objBinding.MPB_OnSortColumnClick;

                /* Into the sub-menu, in column order. */
                Main_tsmCtxSort.DropDownItems.Add(tsmItem);
            }

            /* A divider between the columns and the two directions. */
            Main_tsmCtxSort.DropDownItems.Add(new ToolStripSeparator());

            /* Ascending, marked when it is the direction in force ... */
            tsmItem         = new ToolStripMenuItem("&Ascending");
            tsmItem.Checked = !Main_asrtSide[iSide].PSR_bDescending;

            /* ... its click keeping the column and sorting ascending ... */
            tsmItem.Click += objBinding.MPB_OnSortAscendingClick;

            /* ... into the sub-menu. */
            Main_tsmCtxSort.DropDownItems.Add(tsmItem);

            /* Descending likewise, marked when it is in force ... */
            tsmItem         = new ToolStripMenuItem("&Descending");
            tsmItem.Checked = Main_asrtSide[iSide].PSR_bDescending;

            /* ... its click keeping the column and sorting descending ... */
            tsmItem.Click += objBinding.MPB_OnSortDescendingClick;

            /* ... into the sub-menu, last. */
            Main_tsmCtxSort.DropDownItems.Add(tsmItem);
        }

        /*---------------------------------------------------------------------*/
        /* Main_OnEditMenuOpening:                                             */
        /*                                                                     */
        /* The Edit menu is about to drop down: set its items' states.         */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     objSender : the Edit menu.                                      */
        /*     evArgs    : unused.                                             */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : Cut, Copy and Paste are enabled or greyed as they apply. */
        /*---------------------------------------------------------------------*/
        private void Main_OnEditMenuOpening(object objSender, EventArgs evArgs)
        {
            /* Set as the menu opens, since Paste depends on the clipboard,   */
            /* which no event here follows.                                   */
            Main_UpdateEditCommands();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnFileMenuOpening:                                            */
        /*                                                                    */
        /* The File menu is about to drop down: the items whose state or      */
        /* words depend on what is open set for the present state.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the File menu.                                     */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the items are enabled or greyed.                        */
        /*--------------------------------------------------------------------*/
        private void Main_OnFileMenuOpening(object objSender, EventArgs evArgs)
        {
            STV_clsStateView objSource;

            /* The worker's last published state, read once: null before the   */
            /* command system has started (the sub-menu is then left off).     */
            objSource = null;
            if (Main_objCommandClient != null)
            {   /* The state record itself -- it is never changed, so no copy   */
                /* is needed.                                                   */
                objSource = Main_objCommandClient.CLI_ReadState();
            }

            /* Check Filesystem is given its Image File / Device sub-menu   */
            /* only while NOTHING is open (mode None): then a click picks   */
            /* what to check.  With an image open the item itself checks    */
            /* that image (Main_OnCheckFsClicked).  With a check or         */
            /* partition window up, that window owns the disk although no   */
            /* image is mounted, so no sub-menu is offered for picking      */
            /* another -- and the item is greyed below.                     */
            Main_tsmCheckFs.DropDownItems.Clear();
            if (objSource != null && objSource.STV_enmMode == MOD_enmMode.None)
            {   /* Nothing open and no window owning a disk. */
                Main_tsmCheckFs.DropDownItems.Add(Main_tsmCheckImage);
                Main_tsmCheckFs.DropDownItems.Add(Main_tsmCheckDevice);
            }

            /* Every item narrowed by the worker's state, as the toolbars    */
            /* are (Main_ApplyCommandAvailability): what would change the    */
            /* target is greyed while a check or partition window owns the   */
            /* disk, even with the worker idle -- and Exit stays enabled     */
            /* while work runs, since its own question is how a running      */
            /* command is stopped for good.                                  */
            Main_ApplyCommandAvailability();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnEnableWriteClicked:                                         */
        /*                                                                    */
        /* File > Enable / Disable Writing to Image: the toggle               */
        /* (Main_RequestWriteToggle).                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the request is posted; the item and the pane change     */
        /*            when the worker's next state says the mode changed.     */
        /*--------------------------------------------------------------------*/
        private void Main_OnEnableWriteClicked(object objSender, EventArgs evArgs)
        {
            /* The same toggle the image pane's Write button requests. */
            Main_RequestWriteToggle();
        }

        /*--------------------------------------------------------------------*/
        /* Main_RequestWriteToggle:                                           */
        /*                                                                    */
        /* Enable / Disable Writing to Image -- a toggle.  Read-only: make    */
        /* the image writable now, ahead of the first change, after the same  */
        /* one question every first change asks -- nothing is changed by this */
        /* itself; it makes Cut available, and later changes go ahead         */
        /* unasked.  Writable: back to read-only, staying where the pane is,  */
        /* so the next change asks again.  File > Enable Writing to Image's   */
        /* handler and the image pane's Write button's call it.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the request is posted; the item and the pane change     */
        /*            when the worker's next state says the mode changed.     */
        /*--------------------------------------------------------------------*/
        private void Main_RequestWriteToggle()
        {
            STV_clsStateView objSource;
            KND_enmKind enmKind;

            /* The mode asked for follows the access the worker last           */
            /* reported (read once; null before the command system starts):    */
            /* writable, unless the session is writable now.  The item's       */
            /* text follows that same state (Main_ApplyCommandAvailability),   */
            /* so the request is what the user clicked.  Nothing local is      */
            /* changed: a second click before the first is done asks for       */
            /* the same mode again rather than flipping it back.               */
            objSource = null;
            enmKind = KND_enmKind.MakeWritable;
            if (Main_objCommandClient != null)
            {   /* The state record itself (it is never changed). */
                objSource = Main_objCommandClient.CLI_ReadState();
            }

            if (objSource != null && objSource.STV_bWritable)
            {   /* Writable now: back to read-only. */
                enmKind = KND_enmKind.MakeReadOnly;
            }

            /* Posted as one command (Main_PostLifecycleCommand): the worker   */
            /* asks the first-write question, reopens the image in the new     */
            /* mode and lists the folder again -- the whole sequence.          */
            Main_PostLifecycleCommand(enmKind);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnCutClicked:                                                 */
        /*                                                                    */
        /* Edit > Cut: mark the active pane's selection on the clipboard as   */
        /* cut.  (Each pane's Cut button has its own handler.)                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item.                                     */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the clipboard holds the selection, marked as cut.       */
        /*--------------------------------------------------------------------*/
        private void Main_OnCutClicked(object objSender, EventArgs evArgs)
        {
            /* The active pane's selection, marked as a cut (true). */
            Main_CopyToClipboard(!Main_bHostPaneActive, true);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnCopyClicked:                                                */
        /*                                                                    */
        /* Edit > Copy: put the active pane's selection on the clipboard.     */
        /* (Each pane's Copy button has its own handler.)                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item.                                     */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the clipboard holds the selection.                      */
        /*--------------------------------------------------------------------*/
        private void Main_OnCopyClicked(object objSender, EventArgs evArgs)
        {
            /* The active pane's selection, as a copy (false: not a cut). */
            Main_CopyToClipboard(!Main_bHostPaneActive, false);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnPasteClicked:                                               */
        /*                                                                    */
        /* Edit > Paste: paste the clipboard into the active pane.  (Each     */
        /* pane's Paste button has its own handler.)                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item.                                     */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the paste has run, or the status bar says why not.      */
        /*--------------------------------------------------------------------*/
        private void Main_OnPasteClicked(object objSender, EventArgs evArgs)
        {
            /* Into the active pane; what the clipboard holds is looked at   */
            /* there.                                                        */
            Main_PasteFromClipboard(!Main_bHostPaneActive);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnCtxCutClicked:                                              */
        /*                                                                    */
        /* The right-click menu's Cut: the same cut as Edit > Cut, through    */
        /* the same function, from a handler of its own so each control's     */
        /* event is told apart.                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the clipboard holds the selection, marked as cut.       */
        /*--------------------------------------------------------------------*/
        private void Main_OnCtxCutClicked(object objSender, EventArgs evArgs)
        {
            /* The active pane's selection -- the pane the menu opened over   */
            /* (Main_OnPaneMenuOpening) -- marked as a cut (true).            */
            Main_CopyToClipboard(!Main_bHostPaneActive, true);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnCtxCopyClicked:                                             */
        /*                                                                    */
        /* The right-click menu's Copy: the same copy as Edit > Copy, from a  */
        /* handler of its own.                                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the clipboard holds the selection.                      */
        /*--------------------------------------------------------------------*/
        private void Main_OnCtxCopyClicked(object objSender, EventArgs evArgs)
        {
            /* The active pane's selection, as a copy (false: not a cut). */
            Main_CopyToClipboard(!Main_bHostPaneActive, false);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnCtxPasteClicked:                                            */
        /*                                                                    */
        /* The right-click menu's Paste: the same paste as Edit > Paste, from */
        /* a handler of its own.                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the paste has run, or the status bar says why not.      */
        /*--------------------------------------------------------------------*/
        private void Main_OnCtxPasteClicked(object objSender, EventArgs evArgs)
        {
            /* Into the active pane; what the clipboard holds is looked at   */
            /* there.                                                        */
            Main_PasteFromClipboard(!Main_bHostPaneActive);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnTransferClicked:                                            */
        /*                                                                    */
        /* The right-click menu's copy across to the other pane: Import into  */
        /* Image when opened over the Windows pane, Extract to Windows when   */
        /* opened over the image pane -- the same two requests the Commands   */
        /* menu's items make (Main_RequestImport, Main_RequestExtract).       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the copy has run, or the status bar says why not.       */
        /*--------------------------------------------------------------------*/
        private void Main_OnTransferClicked(object objSender, EventArgs evArgs)
        {
            if (Main_bHostPaneActive)
            {   /* Over the Windows pane: its selection goes into the image. */
                Main_RequestImport();
            }
            else
            {   /* Over the image pane: its selection goes out to Windows. */
                Main_RequestExtract();
            }
        }

        /*---------------------------------------------------------------------*/
        /* Main_NewOption:                                                     */
        /*                                                                     */
        /* Create one Options menu item: a tick mark that toggles when         */
        /* clicked, starts from its remembered state, and has its own          */
        /* handler to store its new state whenever it changes.                 */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     sText      : the menu text (an ampersand marks the access key). */
        /*     sSetting   : the registry value that remembers it.              */
        /*     iDefault   : 1 or 0, the state on a first run.                  */
        /*     pfnChanged : the item's own CheckedChanged handler, hooked to   */
        /*                  it alone.                                          */
        /*                                                                     */
        /* Returns:                                                            */
        /*     ToolStripMenuItem : the item, ticked or not as remembered.      */
        /*---------------------------------------------------------------------*/
        private ToolStripMenuItem Main_NewOption(string sText, string sSetting, int iDefault, EventHandler pfnChanged)
        {
            ToolStripMenuItem tsmOption;

            /* A self-toggling tick, its mark from the registry (absent:   */
            /* iDefault), and its own handler -- hooked last, so setting   */
            /* the mark here stores nothing.                               */
            tsmOption              = new ToolStripMenuItem(sText);
            tsmOption.CheckOnClick = true;
            tsmOption.Checked      = (SET_Settings.SET_ReadInt(sSetting, iDefault) == 1);
            tsmOption.CheckedChanged += pfnChanged;

            /* The caller adds it to the Options menu. */
            return(tsmOption);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnOptMetaOutChanged:                                          */
        /*                                                                    */
        /* Options > Extracting writes a Unix metadata manifest was ticked or */
        /* unticked: remember it.  The tick itself is read as each copy is    */
        /* posted (Main_PostCommandCopy) and as a drag out begins, so nothing */
        /* else has to be told.                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the setting is stored.                                  */
        /*--------------------------------------------------------------------*/
        private void Main_OnOptMetaOutChanged(object objSender, EventArgs evArgs)
        {
            /* The tick, under its registry value. */
            Main_StoreOption(Main_tsmOptMetaOut, SET_Settings.SET_OPT_META_OUT);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnOptMetaInChanged:                                           */
        /*                                                                    */
        /* Options > Importing restores metadata from a manifest was ticked   */
        /* or unticked: remember it.                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the setting is stored.                                  */
        /*--------------------------------------------------------------------*/
        private void Main_OnOptMetaInChanged(object objSender, EventArgs evArgs)
        {
            /* The tick, under its registry value. */
            Main_StoreOption(Main_tsmOptMetaIn, SET_Settings.SET_OPT_META_IN);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnOptSetTimesChanged:                                         */
        /*                                                                    */
        /* Options > Extracted files keep the image's modify times was ticked */
        /* or unticked: remember it.                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the setting is stored.                                  */
        /*--------------------------------------------------------------------*/
        private void Main_OnOptSetTimesChanged(object objSender, EventArgs evArgs)
        {
            /* The tick, under its registry value. */
            Main_StoreOption(Main_tsmOptSetTimes, SET_Settings.SET_OPT_SET_TIMES);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnOptFollowLinksChanged:                                      */
        /*                                                                    */
        /* Options > Extracting follows symbolic links was ticked or          */
        /* unticked: remember it, as the other extract options are.  The      */
        /* tick is read when each copy out is posted (Main_PostCommandCopy)   */
        /* or a drag out begins, so it reaches every way out of the image.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the setting is stored.                                  */
        /*--------------------------------------------------------------------*/
        private void Main_OnOptFollowLinksChanged(object objSender, EventArgs evArgs)
        {
            /* The tick, under its registry value. */
            Main_StoreOption(Main_tsmOptFollowLinks, SET_Settings.SET_OPT_FOLLOW_LINKS);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnOptEditorOpenChanged:                                       */
        /*                                                                    */
        /* Options > Open config files in the editor was ticked or unticked:  */
        /* remember it, under SET_OPT_JSON_OPEN -- a JSON-only name for an    */
        /* option that covers INI and Klipper too, kept so a setting already  */
        /* stored still counts.                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the setting is stored.                                  */
        /*--------------------------------------------------------------------*/
        private void Main_OnOptEditorOpenChanged(object objSender, EventArgs evArgs)
        {
            /* The tick, under its registry value. */
            Main_StoreOption(Main_tsmOptEditorOpen, SET_Settings.SET_OPT_JSON_OPEN);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnOptVerifyChanged:                                           */
        /*                                                                    */
        /* Options > Verify writes to devices was ticked or unticked:         */
        /* remember it, and tell the engine at once -- the read-back is the   */
        /* engine's to do (Main_ApplyWriteVerify), and it holds for the open  */
        /* session too.                                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the setting is stored and its change is posted.         */
        /*--------------------------------------------------------------------*/
        private void Main_OnOptVerifyChanged(object objSender, EventArgs evArgs)
        {
            /* The tick, under its registry value, then the engine told. */
            Main_StoreOption(Main_tsmOptVerify, SET_Settings.SET_OPT_VERIFY);
            Main_ApplyWriteVerify();
        }

        /*--------------------------------------------------------------------*/
        /* Main_StoreOption:                                                  */
        /*                                                                    */
        /* Remember an Options tick mark's state in the registry, 1 for       */
        /* ticked and 0 for not -- the work every option's handler shares.    */
        /* Each handler names its own item and setting.                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     tsmOption : the option's menu item.                            */
        /*     sSetting  : the registry value that remembers it.              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the setting is stored.                                  */
        /*--------------------------------------------------------------------*/
        private static void Main_StoreOption(ToolStripMenuItem tsmOption, string sSetting)
        {
            int iValue;

            /* The registry's 1 / 0 for the tick: 0 unless ... */
            iValue = 0;

            if (tsmOption.Checked)
            {   /* ... ticked. */
                iValue = 1;
            }

            /* Stored under the item's setting. */
            SET_Settings.SET_WriteInt(sSetting, iValue);
        }

        /*--------------------------------------------------------------------*/
        /* Main_ApplyWriteVerify:                                             */
        /*                                                                    */
        /* Tell the engine whether an ordinary session's device writes are    */
        /* read back, as Options > Verify Writes now says: a SetVerify        */
        /* request posted to the worker, the only thread that calls the       */
        /* engine.  Settings of this kind keep only the newest one waiting    */
        /* (SYS_Policy's Latest class), so quick clicks leave the last.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the setting is posted, or nothing is done before the    */
        /*            command system exists.                                  */
        /*--------------------------------------------------------------------*/
        private void Main_ApplyWriteVerify()
        {
            RQT_clsRequest objRequest;
            uint uiFlags;

            if (Main_objCommandClient == null)
            {   /* No command system yet: nothing to post to. */
                return;
            }

            /* The tick read now, into the request: what runs later is this   */
            /* click's setting, whatever the item says by then.               */
            uiFlags = 0;
            if (Main_tsmOptVerify.Checked)
            {   /* Ticked: 1 asks the engine to read back device writes. */
                uiFlags = 1;
            }

            /* Posted for the main window (origin 0) with the state it has;   */
            /* only a failure comes back, to the status line                  */
            /* (Main_ReportCommandFailure).                                   */
            objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.SetVerify,
                0, Main_objCommandClient.CLI_ReadState(), null, null, 0,
                uiFlags, null, "", "");
            Main_objCommandClient.CLI_Post(objRequest, Main_ReportCommandFailure);
        }

        /*--------------------------------------------------------------------*/
        /* Main_RiskWarningsOn:                                               */
        /*                                                                    */
        /* Whether the red "at your own risk" questions are asked: before the */
        /* first change to an image or device, and before the partition and   */
        /* check windows open.  On unless turned off (Main_OnWarningsClicked, */
        /* after two confirmations).                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the warnings are to be shown.                 */
        /*--------------------------------------------------------------------*/
        private bool Main_RiskWarningsOn()
        {
            /* On by default: only an explicit 0 turns them off. */
            return(SET_Settings.SET_ReadInt(SET_Settings.SET_OPT_WARNINGS, 1) != 0);
        }

        /*--------------------------------------------------------------------*/
        /* Main_UpdateWarningsItem:                                           */
        /*                                                                    */
        /* The Options item's tick follows the stored setting: ticked while   */
        /* the risk warnings are on.  The item is an ordinary tick mark with  */
        /* a fixed name, Risk Warning Enable, rather than text that changes   */
        /* with the state.                                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the tick matches the setting.                           */
        /*--------------------------------------------------------------------*/
        private void Main_UpdateWarningsItem()
        {
            /* Ticked means enabled. */
            Main_tsmOptWarnings.Checked = Main_RiskWarningsOn();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnWarningsClicked:                                            */
        /*                                                                    */
        /* Options > Risk Warning Enable: ask the worker to turn the risk     */
        /* warnings the other way.  Turning them OFF asks twice, and those    */
        /* questions are the worker's (CommandSequences.cs, SetRiskWarnings); */
        /* turning them on asks nothing.  The tick is not a CheckOnClick      */
        /* one: it changes only when the worker has accepted the change       */
        /* (Main_OnRiskWarningsResult, MainCommand.cs).                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the change is posted, or the status line says the       */
        /*            command system is not ready.                            */
        /*--------------------------------------------------------------------*/
        private void Main_OnWarningsClicked(object objSender, EventArgs evArgs)
        {
            RQT_clsRequest objRequest;
            uint uiFlags;

            if (Main_objCommandClient == null)
            {   /* No command system yet: refused with a reason.  The       */
                /* questions are the worker's, so there is no way to turn   */
                /* the warnings off without it.                             */
                Main_SetStatus("The warning-option command endpoint is not initialized.");
                return;
            }

            /* The mode asked for is the other of what the tick shows.  The   */
            /* tick shows the setting the worker last accepted and does not   */
            /* move while its questions wait, so a second click meanwhile     */
            /* asks for the same change again rather than undoing it.         */
            uiFlags = 0;
            if (!Main_tsmOptWarnings.Checked)
            {   /* Off now: 1 asks for them on, which needs no question. */
                uiFlags = 1;
            }

            /* Posted as fresh user work (true), with its own completion:    */
            /* only an accepted change is remembered and ticked.  Settings   */
            /* of this kind keep only the newest one waiting (Latest).       */
            objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.SetRiskWarnings, 0,
                Main_objCommandClient.CLI_ReadState(), null, null, 0,
                uiFlags, null, "", "");
            Main_objCommandClient.CLI_Post(objRequest, Main_OnRiskWarningsResult, true);
        }


        /*--------------------------------------------------------------------*/
        /* Main_AddViewItems:                                                 */
        /*                                                                    */
        /* Add the five view items for ONE pane to a menu -- that pane's      */
        /* sub-menu under View, or that pane's pull-down on its toolbar.      */
        /* Each item is bound to its pane when it is made, so choosing one    */
        /* changes that pane whichever pane the user was last in.  The view   */
        /* an item stands for rides in its Tag, which is how the mark is set  */
        /* (Main_UpdateViewMenu); every item made is remembered per pane so   */
        /* the mark can be kept right in both places.                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     items : the menu's item collection to add to.                  */
        /*     iSide : MAIN_SIDE_IMAGE or MAIN_SIDE_HOST.                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : five items are added, unmarked.                         */
        /*--------------------------------------------------------------------*/
        private void Main_AddViewItems(ToolStripItemCollection items, int iSide)
        {
            ToolStripMenuItem  tsmView;
            Mpb_clsPaneBinding objBinding;
            int                i;

            /* The pane, held for the five items' shared click handler. */
            objBinding = new Mpb_clsPaneBinding(this, iSide);

            for (i = 0; i < Main_aenmViewOrder.Length; i++)
            {   /* One view per iteration, in the order the menus list them. */
                tsmView     = new ToolStripMenuItem(Main_asViewTexts[i]);
                tsmView.Tag = Main_aenmViewOrder[i];

                /* Its click chooses its view for THIS pane, read back from   */
                /* the Tag.                                                   */
                tsmView.Click += objBinding.MPB_OnViewClick;

                /* Remembered with the pane's other view items, for the   */
                /* mark, and added to the menu given.                     */
                Main_alstViewItems[iSide].Add(tsmView);
                items.Add(tsmView);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_SideList:                                                     */
        /*                                                                    */
        /* The list of one pane, by its index.                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iSide : MAIN_SIDE_IMAGE or MAIN_SIDE_HOST.                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ListView : that pane's list.                                   */
        /*--------------------------------------------------------------------*/
        private ListView Main_SideList(int iSide)
        {
            if (iSide == MAIN_SIDE_HOST)
            {   /* The Windows pane. */
                return(Main_lstHost);
            }

            /* The image pane. */
            return(Main_lstImage);
        }

        /*--------------------------------------------------------------------*/
        /* Main_ActiveList:                                                   */
        /*                                                                    */
        /* The list of the pane the user was last in -- the one the menus'    */
        /* clipboard and edit commands act on.                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ListView : the Windows pane's list or the image pane's.        */
        /*--------------------------------------------------------------------*/
        private ListView Main_ActiveList()
        {
            if (Main_bHostPaneActive)
            {   /* The Windows pane was the last one used. */
                return(Main_lstHost);
            }

            /* Otherwise the image pane (also the answer before either is used). */
            return(Main_lstImage);
        }

        /*--------------------------------------------------------------------*/
        /* Main_ChooseView:                                                   */
        /*                                                                    */
        /* A view was chosen for one pane, from its View sub-menu or from its */
        /* toolbar's pull-down: show that pane's list that way and remember   */
        /* it for that pane.                                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iSide      : MAIN_SIDE_IMAGE or MAIN_SIDE_HOST.                */
        /*     enmViewChosen : the view to show it in.                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : that pane shows the chosen view.                        */
        /*--------------------------------------------------------------------*/
        private void Main_ChooseView(int iSide, View enmViewChosen)
        {
            /* Shown now. */
            Main_SetPaneView(Main_SideList(iSide), enmViewChosen);

            if (iSide == MAIN_SIDE_HOST)
            {   /* Remember it as the Windows pane's view. */
                SET_Settings.SET_WriteInt(SET_Settings.SET_HOST_VIEW, (int)enmViewChosen);
            }
            else
            {   /* Remember it as the image pane's view. */
                SET_Settings.SET_WriteInt(SET_Settings.SET_IMAGE_VIEW, (int)enmViewChosen);
            }

            /* The marks, in the sub-menu and on the pull-down, moved to it. */
            Main_UpdateViewMenu();
        }

        /*--------------------------------------------------------------------*/
        /* Main_SetPaneView:                                                  */
        /*                                                                    */
        /* Show one pane's list in a given view, with whatever that view      */
        /* needs worked out for the list's present font and contents.  Also   */
        /* called with the view unchanged, to redo that work after a text     */
        /* size change (Main_ApplyListFont).                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstPane : the pane's list.                                     */
        /*     enmViewNew : the view to show it in.                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the list shows that view.                               */
        /*--------------------------------------------------------------------*/
        private void Main_SetPaneView(ListView lstPane, View enmViewNew)
        {
            int iTileWidth;
            int iTileHeight;

            if (enmViewNew == View.Tile && lstPane.CheckBoxes)
            {   /* WinForms throws on Tile view with check boxes (as found):   */
                /* they go first (Main_ApplyItemChecks keeps them off in       */
                /* Tile).                                                      */
                lstPane.CheckBoxes = false;
            }

            if (enmViewNew == View.Tile)
            {   /* Tiles: the size worked out here, since a list view's own   */
                /* default tile is too narrow for a file name.  The icon --   */
                /* the pane's large picture, whose size follows View > Text   */
                /* Size -- then room for a name of MAIN_TILE_NAME_CHARS       */
                /* average letters ('n' is measured as the average) and the   */
                /* detail lines beneath it.                                   */
                iTileWidth  = lstPane.LargeImageList.ImageSize.Width + MAIN_TILE_PAD +
                              TextRenderer.MeasureText(new string('n', MAIN_TILE_NAME_CHARS), lstPane.Font).Width;
                iTileHeight = MAIN_TILE_LINES * lstPane.Font.Height;

                if (iTileHeight < lstPane.LargeImageList.ImageSize.Height)
                {   /* The icon is taller than the text beside it. */
                    iTileHeight = lstPane.LargeImageList.ImageSize.Height;
                }

                /* The tile, with its margin below as well as beside. */
                lstPane.TileSize = new Size(iTileWidth, iTileHeight + MAIN_TILE_PAD);
            }

            /* Small Icons is laid out on the program's own grid, so the      */
            /* control must not re-arrange the rows itself there; the other   */
            /* icon views arrange themselves as usual.                        */
            lstPane.AutoArrange = (enmViewNew != View.SmallIcon);
            lstPane.View        = enmViewNew;

            /* Item check boxes back on (or kept off) for the new view, before   */
            /* the grid is laid out, so a box-widened cell is measured.          */
            Main_ApplyItemChecks(lstPane);

            /* Fit the new view to what the list holds: the columns in         */
            /* details -- skipped while another view was showing -- and the    */
            /* grid in List and Small Icons (Main_SizeColumns does whichever   */
            /* applies).                                                       */
            Main_GridChanged(lstPane, false);
            Main_SizeColumns(lstPane);

            if ((enmViewNew == View.SmallIcon || enmViewNew == View.List) && lstPane is PLV_PaneListView)
            {   /* A grid view: lay it out once more after the control has   */
                /* finished switching (PLV_ArrangeGridLater says why).       */
                ((PLV_PaneListView)lstPane).PLV_ArrangeGridLater();
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_GridChanged:                                                  */
        /*                                                                    */
        /* Tell a pane's list that its List / Small Icons grid must be laid   */
        /* out afresh (PLV_PaneListView.PLV_GridChanged): after new rows or a */
        /* new font (bNamesChanged, which also re-measures the widest name),  */
        /* or a new order or view.  Both panes are PLV_PaneListViews; a plain */
        /* list view (none today) is simply left alone.                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstPane       : the pane's list.                               */
        /*     bNamesChanged : true when the rows or the font changed.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the next Main_SizeColumns lays the grid out again.      */
        /*--------------------------------------------------------------------*/
        private static void Main_GridChanged(ListView lstPane, bool bNamesChanged)
        {
            PLV_PaneListView plvPane;

            plvPane = lstPane as PLV_PaneListView;

            if (plvPane != null)
            {   /* One of the panes. */
                plvPane.PLV_GridChanged(bNamesChanged);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_UpdateViewMenu:                                               */
        /*                                                                    */
        /* For each pane: put the mark on the view items that match that      */
        /* pane's current view -- in its View sub-menu and in its toolbar's   */
        /* pull-down alike -- clear it from the others, and make the          */
        /* pull-down's own text the name of that view.  The text's width      */
        /* changes with the name, so the toolbars are placed again after.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : each pane's view is marked and named on its pull-down.  */
        /*--------------------------------------------------------------------*/
        private void Main_UpdateViewMenu()
        {
            View enmViewNow;
            int  iSide;
            int  i;

            for (iSide = 0; iSide < MAIN_SIDES; iSide++)
            {   /* One pane per iteration. */
                enmViewNow = Main_SideList(iSide).View;

                /* The mark on every one of this pane's view items that   */
                /* stands for that view, off the rest.                    */
                for (i = 0; i < Main_alstViewItems[iSide].Count; i++)
                {   /* One of this pane's view items per iteration: its sub-menu's five, then its pull-down's five. */
                    Main_alstViewItems[iSide][i].Checked = ((View)Main_alstViewItems[iSide][i].Tag == enmViewNow);
                }

                /* The pull-down's text: the view's name. */
                for (i = 0; i < Main_aenmViewOrder.Length; i++)
                {   /* One view per iteration, looking for the one showing. */
                    if (Main_aenmViewOrder[i] == enmViewNow)
                    {   /* Its name, without the access-key mark, is the pull-down's text. */
                        Main_atsdViews[iSide].Text = Main_asViewTexts[i].Replace("&", "");
                    }
                }
            }

            /* The pull-downs' widths changed with their text: the   */
            /* toolbars placed again.                                */
            Main_LayoutToolbars();
        }

        /*--------------------------------------------------------------------*/
        /* Main_RestoreViews:                                                 */
        /*                                                                    */
        /* Put each pane's list in the view it was last left in; details on a */
        /* first run, or when the stored number is not one of the five views. */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : both lists are in their remembered views and the View   */
        /*            menu's mark matches the active pane.                    */
        /*--------------------------------------------------------------------*/
        private void Main_RestoreViews()
        {
            int iImageView;
            int iHostView;

            /* Each pane's stored view, as the framework's View number   */
            /* (absent: details).                                        */
            iImageView = SET_Settings.SET_ReadInt(SET_Settings.SET_IMAGE_VIEW, (int)View.Details);
            iHostView  = SET_Settings.SET_ReadInt(SET_Settings.SET_HOST_VIEW, (int)View.Details);

            if (!Enum.IsDefined(typeof(View), iImageView))
            {   /* A damaged value: fall back to details. */
                iImageView = (int)View.Details;
            }

            if (!Enum.IsDefined(typeof(View), iHostView))
            {   /* A damaged value: fall back to details. */
                iHostView = (int)View.Details;
            }

            /* Both lists put in their views, and the marks set to match. */
            Main_SetPaneView(Main_lstImage, (View)iImageView);
            Main_SetPaneView(Main_lstHost, (View)iHostView);
            Main_UpdateViewMenu();
        }

        /*--------------------------------------------------------------------*/
        /* Main_ImageIconIndex:                                               */
        /*                                                                    */
        /* Which of the program's own icons an image entry gets, by its type. */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uiType : an ENG_TYPE_* value.                                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : an EIC_ICON_* index into the image pane's image lists.   */
        /*--------------------------------------------------------------------*/
        private static int Main_ImageIconIndex(uint uiType)
        {
            /* A folder and a link have pictures of their own; a character   */
            /* and a block device share the device picture, a FIFO and a     */
            /* socket the pipe picture; a regular file, and any type not     */
            /* listed, get the plain file picture.                           */
            switch (uiType)
            {   /* One entry type per case. */
                case ENG_clsNative.ENG_TYPE_DIR:      return(EIC_EntryIcons.EIC_ICON_FOLDER);
                case ENG_clsNative.ENG_TYPE_SYMLINK:  return(EIC_EntryIcons.EIC_ICON_SYMLINK);
                case ENG_clsNative.ENG_TYPE_CHARDEV:  return(EIC_EntryIcons.EIC_ICON_DEVICE);
                case ENG_clsNative.ENG_TYPE_BLOCKDEV: return(EIC_EntryIcons.EIC_ICON_DEVICE);
                case ENG_clsNative.ENG_TYPE_FIFO:     return(EIC_EntryIcons.EIC_ICON_PIPE);
                case ENG_clsNative.ENG_TYPE_SOCKET:   return(EIC_EntryIcons.EIC_ICON_PIPE);
                default:                           return(EIC_EntryIcons.EIC_ICON_FILE);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_NewPaneList:                                                  */
        /*                                                                    */
        /* Create one pane's list: the given column headings (shown in the    */
        /* details view, and as a tile's detail lines), whole-row selection,  */
        /* and several rows selectable at once.  It starts in the details     */
        /* view; Main_RestoreViews then puts it in its remembered one.  The   */
        /* column WIDTHS are not set here; Main_SizeColumns does that from    */
        /* the list's real width once it has one.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     asColumns : the column headings, left to right.                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ListView : the list, docked to fill its pane.                  */
        /*--------------------------------------------------------------------*/
        private ListView Main_NewPaneList(string[] asColumns)
        {
            ListView lstPane;
            int      i;

            /* The program's own list class: a list view that paints itself      */
            /* double-buffered, so dragging a column divider does not flicker.   */
            /* It fills its pane, starts in details, selects whole rows, any     */
            /* number at once, and keeps the selection shown when the focus      */
            /* leaves it: the copy-across commands act on a pane's selection     */
            /* whichever pane has the focus.                                     */
            lstPane               = new PLV_PaneListView();
            lstPane.Dock          = DockStyle.Fill;
            lstPane.View          = View.Details;
            lstPane.FullRowSelect = true;
            lstPane.MultiSelect   = true;
            lstPane.HideSelection = false;

            /* The headings, left to right; their widths come later. */
            for (i = 0; i < asColumns.Length; i++)
            {   /* One column heading per iteration. */
                lstPane.Columns.Add(asColumns[i]);
            }

            /* Size columns (index 1 in both panes) read better right-aligned. */
            lstPane.Columns[1].TextAlign = HorizontalAlignment.Right;

            /* The caller parents it and wires its events. */
            return(lstPane);
        }

        /*--------------------------------------------------------------------*/
        /* Main_SizeColumns:                                                  */
        /*                                                                    */
        /* Size a list's columns from what they actually hold, so nothing in  */
        /* a detail column is cut off whatever the font or the display        */
        /* scaling, and the last column ends flush with the list's right      */
        /* edge.  In the List and Small Icons views, which have no columns    */
        /* showing, it fits their grid to the width instead.  It runs on      */
        /* every step of a window resize, so it is kept cheap.                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstPane : the list.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : every column's width is set.                            */
        /*--------------------------------------------------------------------*/
        private void Main_SizeColumns(ListView lstPane)
        {
            int[] aiWidths;
            int   iSide;
            int   iUsable;
            int   iOthers;
            int   iName;
            int   iNameMin;
            int   iWidth;
            int   iColumn;
            PLV_PaneListView plvPane;

            if (lstPane.View != View.Details)
            {   /* No columns are showing (Main_SetPaneView sizes them when    */
                /* details comes back); the List and Small Icons views' grid   */
                /* is fitted to the width instead (PLV_ArrangeGrid) -- it      */
                /* does nothing when neither the rows nor the cells across     */
                /* have changed.                                               */
                plvPane = lstPane as PLV_PaneListView;

                if (plvPane != null)
                {   /* One of the panes. */
                    plvPane.PLV_ArrangeGrid();
                }

                return;
            }

            /* The columns are fitted to the list's CLIENT width -- what is    */
            /* left of it beside a vertical scroll bar, when there is one --   */
            /* so the last column's right edge is the list's own.              */
            iUsable = lstPane.ClientSize.Width;

            if (iUsable <= 0 || Main_bSizingColumns)
            {   /* The pane is collapsed to nothing, or this is already running   */
                /* (a width it set has changed the client size): leave it.        */
                return;
            }

            /* Running from here to the end, which has no other exit; and    */
            /* the least the Name column may be given, in the list's font.   */
            Main_bSizingColumns = true;
            iNameMin            = Main_NameMinWidth(lstPane);

            /* The detail columns' widths are MEASURED only when the rows or     */
            /* the font have changed -- whoever changes them sets the pane's     */
            /* Main_aaiDetailWidths entry to null; a change of the pane's        */
            /* width alone -- every step of a window resize -- reuses the last   */
            /* measurement.  Each is as wide as its widest text, heading or      */
            /* cell, in the list's own font (Main_MeasureColumns).               */
            iSide = Main_ListSide(lstPane);

            if (Main_aaiDetailWidths[iSide] == null)
            {   /* Stale: measure now, and keep the result. */
                Main_aaiDetailWidths[iSide] = Main_MeasureColumns(lstPane);
            }

            /* Every column but the Name set -- to the user's dragged width   */
            /* where there is one, else the measured -- and their total       */
            /* kept, for the Name column to take what is left.                */
            aiWidths = Main_aaiDetailWidths[iSide];
            iOthers  = 0;

            for (iColumn = 1; iColumn < lstPane.Columns.Count; iColumn++)
            {   /* One detail column per iteration. */
                iWidth = aiWidths[iColumn];

                if (Main_aaiUserWidths[iSide][iColumn] > 0)
                {   /* The user dragged this column to a width: that width stands. */
                    iWidth = Main_aaiUserWidths[iSide][iColumn];
                }

                if (lstPane.Columns[iColumn].Width != iWidth)
                {   /* Only a real change is applied: setting a width repaints the list. */
                    lstPane.Columns[iColumn].Width = iWidth;
                }

                iOthers = iOthers + iWidth;
            }

            /* THE NAME COLUMN IS THE ELASTIC ONE: it gives way so that the     */
            /* other columns stay on screen.  Left to itself it takes exactly   */
            /* what they leave, but never less than its minimum -- in a pane    */
            /* too narrow for that the list scrolls sideways rather than        */
            /* squeezing the names away.                                        */
            iName = iUsable - iOthers;

            if (iName < iNameMin)
            {   /* Too little left over for names. */
                iName = iNameMin;
            }

            /* A width the user dragged it to is the width it PREFERS, not one   */
            /* it is held at: it has that width while there is room for it,      */
            /* and shrinks below it -- as far as the room there is -- when       */
            /* keeping it would push the other columns out of view.  It grows    */
            /* back to it as the pane widens again.                              */
            if (Main_aaiUserWidths[iSide][0] > 0 && Main_aaiUserWidths[iSide][0] < iName)
            {   /* There is room for the user's width (narrower than the fill). */
                iName = Main_aaiUserWidths[iSide][0];
            }

            if (lstPane.Columns[0].Width != iName)
            {   /* Changed. */
                lstPane.Columns[0].Width = iName;
            }

            /* Done: the client-size changes those widths caused have come   */
            /* and gone, and the next one sizes the columns again.           */
            Main_bSizingColumns = false;
        }

        /*--------------------------------------------------------------------*/
        /* Main_NameMinWidth:                                                 */
        /*                                                                    */
        /* The least width the Name column is given automatically: room for   */
        /* the row's icon and a short name, measured in the list's own font,  */
        /* so the minimum grows and shrinks with View > Text Size.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstPane : the list.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : the minimum width, pixels.                               */
        /*--------------------------------------------------------------------*/
        private static int Main_NameMinWidth(ListView lstPane)
        {
            int iIcon;

            /* The icon: the pane's small picture, which follows View > Text   */
            /* Size -- or, should the columns be sized before the panes'       */
            /* sizes are first applied (a resize while the window is being     */
            /* built), the system's small icon.                                */
            iIcon = SystemInformation.SmallIconSize.Width;

            if (lstPane.SmallImageList != null)
            {   /* The pane's own size is known. */
                iIcon = lstPane.SmallImageList.ImageSize.Width;
            }

            /* The icon, MAIN_NAME_MIN_CHARS average characters, the margins. */
            return(iIcon + TextRenderer.MeasureText(new string('n', MAIN_NAME_MIN_CHARS), lstPane.Font).Width + MAIN_CELL_PAD);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnImageClientSizeChanged:                                     */
        /*                                                                    */
        /* The width the image pane's list has for its columns changed -- the */
        /* window was resized, the divider moved, or a vertical scroll bar    */
        /* appeared or went: fit the columns to the new width, so the last    */
        /* one's edge stays at the list's.                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the image pane's list (unused).                    */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the list's columns fit its width.                       */
        /*--------------------------------------------------------------------*/
        private void Main_OnImageClientSizeChanged(object objSender, EventArgs evArgs)
        {
            /* Main_SizeColumns refuses to run inside itself, which this   */
            /* event would otherwise cause.                                */
            Main_SizeColumns(Main_lstImage);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnHostClientSizeChanged:                                      */
        /*                                                                    */
        /* The same for the Windows pane's list.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the Windows pane's list (unused).                  */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the list's columns fit its width.                       */
        /*--------------------------------------------------------------------*/
        private void Main_OnHostClientSizeChanged(object objSender, EventArgs evArgs)
        {
            /* As for the image pane's list. */
            Main_SizeColumns(Main_lstHost);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnSplitterMoved:                                              */
        /*                                                                    */
        /* The divider between the panes moved.  When the USER moved it, the  */
        /* image pane's new share of the width is recorded and remembered;    */
        /* that share is then kept through every later resize                 */
        /* (Main_ApplyLayout).  A move the program or the split container     */
        /* made itself -- putting the divider back at the share, or scaling   */
        /* it as the window is resized -- changes nothing: the share is the   */
        /* user's alone to change.  The two are told apart by the "moving"    */
        /* event, which only a user's move of the divider raises and which    */
        /* comes before this one (both as found).                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the split container.                               */
        /*     evMoved   : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : a user's move is recorded; any other is ignored.        */
        /*--------------------------------------------------------------------*/
        private void Main_OnSplitterMoved(object objSender, SplitterEventArgs evMoved)
        {
            if (!Main_bUserSplitting)
            {   /* Not the user's move. */
                return;
            }

            /* The user's move is being recorded: the flag down, so the   */
            /* next program-made move is not taken for the user's.        */
            Main_bUserSplitting = false;

            if (Main_splPanes.Panel2Collapsed || Main_splPanes.Width <= 0)
            {   /* There is no divider showing to have been moved. */
                return;
            }

            /* Rounded to the nearest thousandth, so placing the divider from   */
            /* the share puts it back where the user left it.                   */
            Main_iSplitPermille = ((Main_splPanes.SplitterDistance * 1000) + (Main_splPanes.Width / 2)) / Main_splPanes.Width;
            SET_Settings.SET_WriteInt(SET_Settings.SET_SPLIT_PERMILLE, Main_iSplitPermille);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnSplitterDoubleClicked:                                      */
        /*                                                                    */
        /* The divider between the panes was double-clicked: put it back in   */
        /* the middle -- the two panes equal, as on a first run -- and        */
        /* remember that as the user's choice of share.  (The only part of    */
        /* the split container a click can land on is the divider: its two    */
        /* panels cover the rest.)                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the split container.                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the panes share the width equally.                      */
        /*--------------------------------------------------------------------*/
        private void Main_OnSplitterDoubleClicked(object objSender, EventArgs evArgs)
        {
            /* A move the first click may have begun is not to be recorded over this. */
            Main_bUserSplitting = false;

            /* The first run's share (the middle), remembered as the   */
            /* user's, and the divider put there now.                  */
            Main_iSplitPermille = MAIN_DEFAULT_SPLIT;
            SET_Settings.SET_WriteInt(SET_Settings.SET_SPLIT_PERMILLE, Main_iSplitPermille);
            Main_ApplyLayout();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnSplitterMoving:                                             */
        /*                                                                    */
        /* The user is moving the divider between the panes (this event is    */
        /* raised only by the user's move, never by the program setting the   */
        /* divider's position -- as found): note it, so the "moved" event     */
        /* that follows is known to be the user's.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the split container.                               */
        /*     evMoving  : unused (the move is never cancelled).              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the flag is set.                                        */
        /*--------------------------------------------------------------------*/
        private void Main_OnSplitterMoving(object objSender, SplitterCancelEventArgs evMoving)
        {
            /* Read and lowered by Main_OnSplitterMoved. */
            Main_bUserSplitting = true;
        }

        /*--------------------------------------------------------------------*/
        /* Main_LoadUserWidths:                                               */
        /*                                                                    */
        /* Read one pane's remembered user-set column widths: a string of     */
        /* numbers, one per column, separated by commas.  Anything missing,   */
        /* unreadable or negative is 0, which means "sized automatically".    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sSetting : the registry value's name.                          */
        /*     iColumns : how many columns the pane has.                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int[] : a width per column, pixels; 0 for automatic.           */
        /*--------------------------------------------------------------------*/
        private static int[] Main_LoadUserWidths(string sSetting, int iColumns)
        {
            string[] asParts;
            int[]    aiWidths;
            int      iWidth;
            int      i;

            /* All automatic (0) to start; the stored string split at its   */
            /* commas ("" when absent, which splits to one empty part).     */
            aiWidths = new int[iColumns];
            asParts  = SET_Settings.SET_ReadString(sSetting, "").Split(',');

            for (i = 0; i < iColumns && i < asParts.Length; i++)
            {   /* One column's stored number per iteration. */
                if (int.TryParse(asParts[i], out iWidth) && iWidth > 0)
                {   /* A usable width. */
                    aiWidths[i] = iWidth;
                }
            }

            /* The widths; a new array's other elements are already 0. */
            return(aiWidths);
        }

        /*--------------------------------------------------------------------*/
        /* Main_SaveUserWidths:                                               */
        /*                                                                    */
        /* Remember one pane's user-set column widths, in the form            */
        /* Main_LoadUserWidths reads.                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iSide : MAIN_SIDE_IMAGE or MAIN_SIDE_HOST.                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the widths are stored.                                  */
        /*--------------------------------------------------------------------*/
        private void Main_SaveUserWidths(int iSide)
        {
            string sJoined;
            int    i;

            /* The widths joined by commas, one number per column. */
            sJoined = "";

            for (i = 0; i < Main_aaiUserWidths[iSide].Length; i++)
            {   /* One column per iteration. */
                if (i != 0)
                {   /* A comma between numbers. */
                    sJoined = sJoined + ",";
                }

                /* Its width, 0 for automatic. */
                sJoined = sJoined + Main_aaiUserWidths[iSide][i].ToString();
            }

            if (iSide == MAIN_SIDE_HOST)
            {   /* The Windows pane's. */
                SET_Settings.SET_WriteString(SET_Settings.SET_HOST_COLUMNS, sJoined);
            }
            else
            {   /* The image pane's. */
                SET_Settings.SET_WriteString(SET_Settings.SET_IMAGE_COLUMNS, sJoined);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnImageColumnResized:                                         */
        /*                                                                    */
        /* The image pane's list reports that the user resized one of its     */
        /* columns (its PLV_pfnUserResized; Main_UserColumnResized says what  */
        /* follows).                                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstSource : the reporting list (unused: this is the image      */
        /*                 pane's callback, which names its list).            */
        /*     iColumn   : the column the user resized.                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the user's width is recorded and remembered.            */
        /*--------------------------------------------------------------------*/
        private void Main_OnImageColumnResized(ListView lstSource, int iColumn)
        {
            /* The image pane's list, named. */
            Main_UserColumnResized(Main_lstImage, iColumn);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnHostColumnResized:                                          */
        /*                                                                    */
        /* The same for the Windows pane's list.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstSource : the reporting list (unused: this is the Windows    */
        /*                 pane's callback, which names its list).            */
        /*     iColumn   : the column the user resized.                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the user's width is recorded and remembered.            */
        /*--------------------------------------------------------------------*/
        private void Main_OnHostColumnResized(ListView lstSource, int iColumn)
        {
            /* The Windows pane's list, named. */
            Main_UserColumnResized(Main_lstHost, iColumn);
        }

        /*----------------------------------------------------------------------*/
        /* Main_UserColumnResized:                                              */
        /*                                                                      */
        /* The USER has resized a column of one of the lists -- dragged its     */
        /* divider, or double-clicked it (PaneListView.cs reports exactly       */
        /* those, never a width the program set).  The width is recorded as     */
        /* that column's and remembered, and from then on the program leaves    */
        /* the column at it through refreshes, folder changes and window        */
        /* resizes (View > Reset Column Widths undoes that) -- but for the      */
        /* Name column, whose width is only a preference.                       */
        /*                                                                      */
        /* Arguments:                                                           */
        /*     lstPane : the list, named by its pane's callback                 */
        /*               (Main_OnImageColumnResized, Main_OnHostColumnResized). */
        /*     iColumn : the column the user resized.                           */
        /*                                                                      */
        /* Returns:                                                             */
        /*     void : the user's width is recorded and remembered.              */
        /*----------------------------------------------------------------------*/
        private void Main_UserColumnResized(ListView lstPane, int iColumn)
        {
            int iSide;
            int iFill;
            int i;

            /* Which pane's widths. */
            iSide = Main_ListSide(lstPane);

            if (iColumn < 0 || iColumn >= Main_aaiUserWidths[iSide].Length)
            {   /* Not one of the pane's columns (a guard). */
                return;
            }

            /* The width the user left it at, recorded as that column's. */
            Main_aaiUserWidths[iSide][iColumn] = lstPane.Columns[iColumn].Width;

            if (iColumn == 0)
            {   /* The Name column, whose width is a PREFERENCE that          */
                /* Main_SizeColumns gives way on to keep the other columns    */
                /* in view.  Dragged out to the width it would fill anyway,   */
                /* or past it (which pushes the other columns out of view),   */
                /* it is taken as "fill the pane" again: no preference is     */
                /* kept, and it goes back to being sized automatically.       */
                /* The fill width: the client width less every other          */
                /* column ...                                                 */
                iFill = lstPane.ClientSize.Width;

                for (i = 1; i < lstPane.Columns.Count; i++)
                {   /* One detail column per iteration: what it takes of the pane's width. */
                    iFill = iFill - lstPane.Columns[i].Width;
                }

                if (iFill < Main_NameMinWidth(lstPane))
                {   /* ... but never below the Name column's minimum. */
                    iFill = Main_NameMinWidth(lstPane);
                }

                if (Main_aaiUserWidths[iSide][0] >= iFill)
                {   /* At or past the fill width: automatic again. */
                    Main_aaiUserWidths[iSide][0] = 0;
                }
            }

            /* Remembered between runs. */
            Main_SaveUserWidths(iSide);

            /* The columns sized again, so the Name column takes up or     */
            /* gives back the difference -- unless it has a width of its   */
            /* own.                                                        */
            Main_SizeColumns(lstPane);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnResetColumnsClicked:                                        */
        /*                                                                    */
        /* View > Reset Column Widths: forget every width the user set by     */
        /* dragging, in both panes, and size the columns automatically again. */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item.                                     */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : both panes' columns are sized from their content.       */
        /*--------------------------------------------------------------------*/
        private void Main_OnResetColumnsClicked(object objSender, EventArgs evArgs)
        {
            int iSide;

            for (iSide = 0; iSide < MAIN_SIDES; iSide++)
            {   /* One pane per iteration: every width back to 0           */
                /* (automatic) in a fresh array, stored, and the columns   */
                /* sized again from their content.                         */
                Main_aaiUserWidths[iSide] = new int[Main_aaiUserWidths[iSide].Length];
                Main_SaveUserWidths(iSide);
                Main_SizeColumns(Main_SideList(iSide));
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_ListSide:                                                     */
        /*                                                                    */
        /* The index of the pane a list belongs to.                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstPane : one of the two panes' lists.                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : MAIN_SIDE_HOST for the Windows pane's list, otherwise    */
        /*           MAIN_SIDE_IMAGE.                                         */
        /*--------------------------------------------------------------------*/
        private int Main_ListSide(ListView lstPane)
        {
            if (lstPane == Main_lstHost)
            {   /* The Windows pane's. */
                return(MAIN_SIDE_HOST);
            }

            /* The image pane's. */
            return(MAIN_SIDE_IMAGE);
        }

        /*--------------------------------------------------------------------*/
        /* Main_MeasureColumns:                                               */
        /*                                                                    */
        /* Measure how wide each DETAIL column of a list must be to show its  */
        /* widest text -- heading or cell -- in the list's own font, plus the */
        /* cell's margins.  This is the costly half of sizing the columns     */
        /* (hundreds of text measurements), so Main_SizeColumns keeps the     */
        /* answer and asks again only when the rows or the font change.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstPane : the list.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int[] : a width per column, pixels; element 0 (the Name        */
        /*             column, which takes what the others leave) is 0.       */
        /*--------------------------------------------------------------------*/
        private static int[] Main_MeasureColumns(ListView lstPane)
        {
            Dictionary<string, int> dictMeasured;
            int[]                   aiWidths;
            string                  sCell;
            int                     iWidth;
            int                     iCell;
            int                     iRows;
            int                     iColumn;
            int                     iRow;

            /* A width per column (0 to start, and 0 stays the Name's), and   */
            /* the widths of texts already measured: a text met again is      */
            /* not measured again -- a Type column holds a handful of         */
            /* different words across hundreds of rows.                       */
            aiWidths     = new int[lstPane.Columns.Count];
            dictMeasured = new Dictionary<string, int>();

            /* Measuring every row of a huge directory would stall the UI for   */
            /* no visible gain, so only the first rows are measured.            */
            iRows = lstPane.Items.Count;

            if (iRows > MAIN_MEASURE_ROWS)
            {   /* A long list: measure its head only. */
                iRows = MAIN_MEASURE_ROWS;
            }

            for (iColumn = 1; iColumn < lstPane.Columns.Count; iColumn++)
            {   /* One detail column per iteration, starting from its      */
                /* heading's own width, which a narrow column must still   */
                /* show.                                                   */
                iWidth = TextRenderer.MeasureText(lstPane.Columns[iColumn].Text, lstPane.Font).Width;

                /* Then each measured row's cell, the widest kept. */
                for (iRow = 0; iRow < iRows; iRow++)
                {   /* One row's cell in this column per iteration. */
                    if (iColumn >= lstPane.Items[iRow].SubItems.Count)
                    {   /* The ".." row has no detail cells. */
                        continue;
                    }

                    /* The cell's text. */
                    sCell = lstPane.Items[iRow].SubItems[iColumn].Text;

                    if (!dictMeasured.TryGetValue(sCell, out iCell))
                    {   /* A text not met before: measure it once. */
                        iCell = TextRenderer.MeasureText(sCell, lstPane.Font).Width;
                        dictMeasured.Add(sCell, iCell);
                    }

                    if (iCell > iWidth)
                    {   /* The widest so far. */
                        iWidth = iCell;
                    }
                }

                /* The widest text plus the cell's margins. */
                aiWidths[iColumn] = iWidth + MAIN_CELL_PAD;
            }

            /* The widths, for Main_SizeColumns to keep. */
            return(aiWidths);
        }

        /*--------------------------------------------------------------------*/
        /* Main_RestorePlacement:                                             */
        /*                                                                    */
        /* Put the window where it was last time.  A remembered position is   */
        /* used only if it still lands on a screen: a monitor that has since  */
        /* been unplugged must not leave the window where it cannot be seen.  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the form's bounds, state and split share are set.       */
        /*--------------------------------------------------------------------*/
        private void Main_RestorePlacement()
        {
            Rectangle rcSaved;
            Rectangle rcScreen;
            int       iWidth;
            int       iHeight;

            /* The remembered size (absent: the first run's). */
            iWidth  = SET_Settings.SET_ReadInt(SET_Settings.SET_WINDOW_WIDTH, MAIN_DEFAULT_WIDTH);
            iHeight = SET_Settings.SET_ReadInt(SET_Settings.SET_WINDOW_HEIGHT, MAIN_DEFAULT_HEIGHT);

            if (iWidth < MAIN_MIN_WIDTH)
            {   /* A stored width below the minimum (a damaged value). */
                iWidth = MAIN_DEFAULT_WIDTH;
            }

            if (iHeight < MAIN_MIN_HEIGHT)
            {   /* A stored height below the minimum. */
                iHeight = MAIN_DEFAULT_HEIGHT;
            }

            /* Default position: centred on the primary screen's working area. */
            rcScreen = Screen.PrimaryScreen.WorkingArea;
            rcSaved  = new Rectangle(SET_Settings.SET_ReadInt(SET_Settings.SET_WINDOW_LEFT, rcScreen.Left + (rcScreen.Width - iWidth) / 2),
                                     SET_Settings.SET_ReadInt(SET_Settings.SET_WINDOW_TOP, rcScreen.Top + (rcScreen.Height - iHeight) / 2),
                                     iWidth, iHeight);

            if (!Screen.GetWorkingArea(rcSaved).IntersectsWith(rcSaved))
            {   /* The remembered spot is on no screen now: centre it instead. */
                rcSaved.X = rcScreen.Left + (rcScreen.Width - iWidth) / 2;
                rcSaved.Y = rcScreen.Top + (rcScreen.Height - iHeight) / 2;
            }

            /* Placed (StartPosition is Manual, Main_BuildControls). */
            this.Bounds = rcSaved;

            if (SET_Settings.SET_ReadInt(SET_Settings.SET_WINDOW_MAX, 0) == 1)
            {   /* It was maximized when last closed. */
                this.WindowState = FormWindowState.Maximized;
            }

            /* The image pane's remembered share of the width, thousandths   */
            /* (absent: the middle); Main_ApplyLayout places the divider     */
            /* from it once the window has a size.                           */
            Main_iSplitPermille = SET_Settings.SET_ReadInt(SET_Settings.SET_SPLIT_PERMILLE, MAIN_DEFAULT_SPLIT);

            if (Main_iSplitPermille < 100 || Main_iSplitPermille > 900)
            {   /* A share that would hide a pane: use the default. */
                Main_iSplitPermille = MAIN_DEFAULT_SPLIT;
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_ApplyLayout:                                                  */
        /*                                                                    */
        /* Recompute everything that depends on the window's size: the        */
        /* divider's position from the remembered share, then both lists'     */
        /* column widths.  Docking has already placed the controls            */
        /* themselves.                                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the divider and the columns fit the current size.       */
        /*--------------------------------------------------------------------*/
        private void Main_ApplyLayout()
        {
            int iDistance;

            if (this.WindowState == FormWindowState.Minimized || Main_splPanes.Width <= 0)
            {   /* Nothing is visible to lay out. */
                return;
            }

            /* The divider's position from the share, in pixels from the   */
            /* left.                                                       */
            iDistance = (Main_splPanes.Width * Main_iSplitPermille) / 1000;

            if (!Main_splPanes.Panel2Collapsed &&
                iDistance >= Main_splPanes.Panel1MinSize &&
                iDistance <= Main_splPanes.Width - Main_splPanes.Panel2MinSize - Main_splPanes.SplitterWidth)
            {   /* A position the splitter will accept (one outside its     */
                /* minimums, or with the right pane collapsed, is left      */
                /* alone -- the split container throws on an out-of-range   */
                /* distance, as found).                                     */
                Main_splPanes.SplitterDistance = iDistance;
            }

            /* Both lists' columns fitted to their new widths. */
            Main_SizeColumns(Main_lstImage);
            Main_SizeColumns(Main_lstHost);
        }

        /*--------------------------------------------------------------------*/
        /* Main_SetStatus:                                                    */
        /*                                                                    */
        /* Show a line in the status bar.  Any new line replaces a Windows-   */
        /* pane refusal that was showing, so that refusal is no longer there  */
        /* for a later successful navigation to clear (Main_NavigateHost      */
        /* sets the flag again right after it shows one).                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sText : the line.                                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the status bar shows it.                                */
        /*--------------------------------------------------------------------*/
        private void Main_SetStatus(string sText)
        {
            /* Its one label; the line stays until the next replaces it. */
            Main_tslStatus.Text    = sText;
            Main_bHostRefusalShown = false;
        }

        /*--------------------------------------------------------------------*/
        /* Main_ShowEngineError:                                              */
        /*                                                                    */
        /* Tell the user an engine operation failed: the plain sentence for   */
        /* the status code, and the engine's own detail line beneath it.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sAction : what was being done ("Opening the image").           */
        /*     iStatus : the operation's status (a result's RSL_iStatus).     */
        /*     sDetail : the detail line its result carries -- taken on the   */
        /*               worker with the status, so it is that operation's    */
        /*               and not a later one's; "" for none.                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : a message box was shown and dismissed.                  */
        /*--------------------------------------------------------------------*/
        private void Main_ShowEngineError(string sAction, int iStatus, string sDetail)
        {
            string sMessage;

            /* What failed, then the status code's plain sentence   */
            /* (ENG_StatusText, EngineNative.cs).                   */
            sMessage = sAction + " failed.\n\n" + ENG_clsNative.ENG_StatusText(iStatus);

            if (sDetail.Length != 0)
            {   /* The engine said more; show it. */
                sMessage = sMessage + "\n\n" + sDetail;
            }

            /* Shown as a warning box over this window, in the dialog font. */
            MSG_clsMessageForm.MSG_Show(this, Main_fntDialog, sMessage, "BifrostFM", MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
        }


        /*--------------------------------------------------------------------*/
        /* Main_FormatSize:                                                   */
        /*                                                                    */
        /* A byte count with thousands separators, as a file manager shows a  */
        /* size.                                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     ullBytes : the size in bytes.                                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : for example "3,145,851".                              */
        /*--------------------------------------------------------------------*/
        private static string Main_FormatSize(ulong ullBytes)
        {
            /* "N0" groups the digits in the user's own number format. */
            return(ullBytes.ToString("N0"));
        }

        /*--------------------------------------------------------------------*/
        /* Main_FormatEpoch:                                                  */
        /*                                                                    */
        /* A Unix timestamp as local date and time.                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uiEpoch : seconds since 1970-01-01 UTC.                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : "yyyy-MM-dd HH:mm:ss" in local time.                  */
        /*--------------------------------------------------------------------*/
        private static string Main_FormatEpoch(uint uiEpoch)
        {
            DateTime dtUtc;

            /* The epoch, 1970-01-01 in UTC, plus the seconds.  An unsigned   */
            /* 32-bit count, so it reaches to 2106 and has no times before    */
            /* 1970.                                                          */
            dtUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds((double)uiEpoch);

            /* Shown in the user's own time zone. */
            return(dtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        }

        /*--------------------------------------------------------------------*/
        /* Main_TypeText:                                                     */
        /*                                                                    */
        /* The word shown in the image pane's Type column.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uiType : an ENG_TYPE_* value.                                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the word.                                             */
        /*--------------------------------------------------------------------*/
        private static string Main_TypeText(uint uiType)
        {
            /* A word per entry type, as the Type column shows it (the      */
            /* Sort By menu orders that column by these words as text); a   */
            /* type not listed shows as "Unknown".                          */
            switch (uiType)
            {   /* One entry type per case. */
                case ENG_clsNative.ENG_TYPE_FILE:     return("File");
                case ENG_clsNative.ENG_TYPE_DIR:      return("Directory");
                case ENG_clsNative.ENG_TYPE_SYMLINK:  return("Symlink");
                case ENG_clsNative.ENG_TYPE_CHARDEV:  return("Char device");
                case ENG_clsNative.ENG_TYPE_BLOCKDEV: return("Block device");
                case ENG_clsNative.ENG_TYPE_FIFO:     return("FIFO");
                case ENG_clsNative.ENG_TYPE_SOCKET:   return("Socket");
                default:                           return("Unknown");
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_ModeText:                                                     */
        /*                                                                    */
        /* Permission bits the way "ls -l" writes them, with the octal form   */
        /* beside it: "rwxr-xr-x (0755)".  The three special bits are shown   */
        /* as ls shows them -- s / S in the owner and group execute places    */
        /* for set-uid and set-gid, t / T in the others' place for sticky,    */
        /* lower case when the execute bit beneath is also set.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uiMode : the 12 permission bits.                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the nine letters and the four octal digits.           */
        /*--------------------------------------------------------------------*/
        private static string Main_ModeText(uint uiMode)
        {
            char[] achBits;
            string sLetters;
            int    i;

            /* The nine places, and the letter each shows when its bit is   */
            /* set: owner, group, others, each read / write / execute,      */
            /* matching the bits from 0400 down to 0001.                    */
            achBits  = new char[9];
            sLetters = "rwxrwxrwx";

            for (i = 0; i < 9; i++)
            {   /* One permission bit per iteration, most significant first. */
                if ((uiMode & (0x100u >> i)) != 0)
                {   /* The bit is set: its letter. */
                    achBits[i] = sLetters[i];
                }
                else
                {   /* The bit is clear: a dash. */
                    achBits[i] = '-';
                }
            }

            if ((uiMode & 0x800u) != 0)
            {   /* Set-uid replaces the owner's execute place. */
                if (achBits[2] == 'x')
                {   /* Execute is also set. */
                    achBits[2] = 's';
                }
                else
                {   /* Set-uid without execute. */
                    achBits[2] = 'S';
                }
            }

            if ((uiMode & 0x400u) != 0)
            {   /* Set-gid replaces the group's execute place. */
                if (achBits[5] == 'x')
                {   /* Execute is also set. */
                    achBits[5] = 's';
                }
                else
                {   /* Set-gid without execute. */
                    achBits[5] = 'S';
                }
            }

            if ((uiMode & 0x200u) != 0)
            {   /* Sticky replaces the others' execute place. */
                if (achBits[8] == 'x')
                {   /* Execute is also set. */
                    achBits[8] = 't';
                }
                else
                {   /* Sticky without execute. */
                    achBits[8] = 'T';
                }
            }

            /* The letters, then the octal digits, zero-padded to four. */
            return(new string(achBits) + " (" + Convert.ToString(uiMode, 8).PadLeft(4, '0') + ")");
        }

        /*--------------------------------------------------------------------*/
        /* Main_JoinImagePath:                                                */
        /*                                                                    */
        /* The image path of a directory's child.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sDir  : the directory ("/" or "/a/b").                         */
        /*     sName : the child's name.                                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : "/name" at the root, otherwise "dir/name".            */
        /*--------------------------------------------------------------------*/
        private static string Main_JoinImagePath(string sDir, string sName)
        {
            if (sDir == "/")
            {   /* The root already ends in its slash. */
                return("/" + sName);
            }

            /* Anywhere else: directory, slash, name. */
            return(sDir + "/" + sName);
        }

        /*--------------------------------------------------------------------*/
        /* Main_JoinHostPath:                                                 */
        /*                                                                    */
        /* The Windows path an image entry's name takes in a Windows folder,  */
        /* made by joining the two strings and nothing more.  Path.Combine is */
        /* not used: it throws for a name holding a character of              */
        /* Path.GetInvalidPathChars (" < > | and the control characters),     */
        /* which an ext name may hold -- and such a name must reach the       */
        /* engine, whose extract refuses it as a name Windows cannot hold     */
        /* and reports it skipped, so the other items still go.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sDir  : the Windows folder: a drive's root, or a folder in it. */
        /*     sName : the image entry's name, as it stands.                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the folder, one backslash, the name.                  */
        /*--------------------------------------------------------------------*/
        private static string Main_JoinHostPath(string sDir, string sName)
        {
            if (sDir.EndsWith("\\", StringComparison.Ordinal))
            {   /* A drive's root already ends in its backslash. */
                return(sDir + sName);
            }

            /* Any other folder: folder, backslash, name. */
            return(sDir + "\\" + sName);
        }

        /*--------------------------------------------------------------------*/
        /* Main_ImageParentDir:                                               */
        /*                                                                    */
        /* The directory above an image directory.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sDir : an image directory other than the root.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : its parent ("/" for a top-level directory).           */
        /*--------------------------------------------------------------------*/
        private static string Main_ImageParentDir(string sDir)
        {
            int iSlash;

            /* The last slash: what follows it is the directory's own name. */
            iSlash = sDir.LastIndexOf('/');

            if (iSlash <= 0)
            {   /* The only slash is the leading one: the parent is the root. */
                return("/");
            }

            /* Everything before the last slash. */
            return(sDir.Substring(0, iSlash));
        }

        /*--------------------------------------------------------------------*/
        /* Main_CompareImageEntries:                                          */
        /*                                                                    */
        /* The image pane's order: directories first, then everything else,   */
        /* each group by name.  Names are compared by ORDINAL value, not by   */
        /* the user's language rules, because ext4 names are case-sensitive   */
        /* bytes and "Makefile" and "makefile" are different entries that     */
        /* must not be shown as if they were interchangeable.  On FAT and     */
        /* exFAT, whose names match letter case aside, one folder never holds */
        /* two such names, so the same ordinal order is exact there too.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stEntryLeft  : one entry.                                      */
        /*     stEntryRight : another.                                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : negative, zero or positive, as a comparison delegate.    */
        /*--------------------------------------------------------------------*/
        private static int Main_CompareImageEntries(EEN_stEntry stEntryLeft, EEN_stEntry stEntryRight)
        {
            bool bLeftDir;
            bool bRightDir;

            /* Which of the two are directories (a link to one is not). */
            bLeftDir  = (stEntryLeft.EEN_uiType == ENG_clsNative.ENG_TYPE_DIR);
            bRightDir = (stEntryRight.EEN_uiType == ENG_clsNative.ENG_TYPE_DIR);

            if (bLeftDir && !bRightDir)
            {   /* A directory sorts before a non-directory. */
                return(-1);
            }

            if (!bLeftDir && bRightDir)
            {   /* A non-directory sorts after a directory. */
                return(1);
            }

            /* The same group: by name. */
            return(string.CompareOrdinal(ENG_clsNative.ENG_EntryName(stEntryLeft), ENG_clsNative.ENG_EntryName(stEntryRight)));
        }

        /*--------------------------------------------------------------------*/
        /* Main_ApplyOpenResult:                                              */
        /*                                                                    */
        /* Take a SUCCESSFUL open into the window's state: the image's name   */
        /* in the title, its access mode, and the volume summary the status   */
        /* bar shows.  It lists nothing: the listing comes as the open's next */
        /* view -- a fresh open's of the root, a reopen's (for writing or     */
        /* reading) of the folder the pane is in.  Its one caller is          */
        /* Main_RenderCommandLifecycle (MainCommand.cs), which has checked    */
        /* the facts are this open's.                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objResult : the open's outcome (its status is ENG_OK).         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window's image state matches the open session.      */
        /*--------------------------------------------------------------------*/
        private void Main_ApplyOpenResult(EOR_clsOpenResult objResult)
        {
            /* The open's facts into the window's state: open, its mode,     */
            /* its file (or device path), offset and length; Close usable,   */
            /* and Partition/Format too (it asks to close the open image).   */
            /* The caller narrows both again by the worker's state.          */
            Main_bImageOpen            = true;
            Main_bReadOnly             = objResult.EOR_bReadOnly;
            Main_sImageFile            = objResult.EOR_sImagePath;
            Main_ullImageOffset        = objResult.EOR_ullOffset;
            Main_ullImageLength        = objResult.EOR_ullLength;
            Main_tsmCloseImage.Enabled = true;
            Main_tsbClose.Enabled      = true;
            Main_tsmPartition.Enabled  = true;

            if (Main_sDeviceText.Length != 0)
            {   /* A real device: its model is the name, with a reminder   */
                /* that it is one.  The description was set from the       */
                /* worker's state before this runs                         */
                /* (Main_ObserveCommandState, MainCommand.cs).             */
                this.Text = "DEVICE " + Main_sDeviceText + " - BifrostFM";
            }
            else
            {   /* An image file: its name.  A device is not remembered as the   */
                /* last image, since it may not be there next time.              */
                this.Text = Path.GetFileName(objResult.EOR_sImagePath) + " - BifrostFM";
                SET_Settings.SET_WriteString(SET_Settings.SET_LAST_IMAGE, objResult.EOR_sImagePath);
            }

            /* The volume summary: label, used and total space, access mode. */
            Main_sVolumeText = Main_ComposeVolumeText(objResult.EOR_stVolume, objResult.EOR_ullOffset,
                objResult.EOR_bReadOnly);

            /* The image path row's side label: the filesystem's type in    */
            /* capitals -- "(EXT4)", "(FAT32)", "(EXFAT)" -- in the words   */
            /* the summary and Filesystem Properties use (FSP_TypeName),    */
            /* so the three never disagree.  Set here, at every open and    */
            /* reopen, since a session's type never changes while it is     */
            /* open; Main_ShowNoImage puts back MAIN_NO_IMAGE_SIDE.         */
            Main_lblImageSide.Text = "(" + FSP_clsFsPropertiesForm.FSP_TypeName(objResult.EOR_stVolume).ToUpper() + ")";

            /* Writable or not changes which toolbar buttons apply. */
            Main_UpdateToolbar();
        }

        /*--------------------------------------------------------------------*/
        /* Main_ComposeVolumeText:                                            */
        /*                                                                    */
        /* The status line's summary of the open filesystem: its label, its   */
        /* type (ext2 / ext3 / ext4, FAT12 / 16 / 32 or exFAT, in             */
        /* FsPropertiesForm.cs's words), the bytes in use of the total, where */
        /* in a disk image it starts, and the access mode.  Made when the     */
        /* image opens, and again from the fresh facts the worker reads with  */
        /* each re-list after a change (Main_RenderCommandNavigation,         */
        /* MainCommand.cs), so the usage follows copies and edits instead of  */
        /* staying at what it was when the image was opened.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stVolume  : the filesystem's facts (block size and counts,     */
        /*                 label, type, feature words, external journal).     */
        /*     ullOffset : where the filesystem starts in its file or device. */
        /*     bReadOnly : true while the session is read-only.               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the summary.                                          */
        /*--------------------------------------------------------------------*/
        private string Main_ComposeVolumeText(VLI_stVolumeInfo stVolume, ulong ullOffset, bool bReadOnly)
        {
            ulong  ullTotal;
            ulong  ullFree;
            string sLabel;
            string sText;

            /* The sizes from the block counts, and the label. */
            ullTotal = stVolume.VLI_ullBlockCount * stVolume.VLI_uiBlockSize;
            ullFree  = stVolume.VLI_ullFreeBlocks * stVolume.VLI_uiBlockSize;
            sLabel   = ENG_clsNative.ENG_FromUtf8(stVolume.VLI_abVolumeName);

            if (sLabel.Length == 0)
            {   /* An unlabelled filesystem. */
                sLabel = "(no label)";
            }

            /* The summary's first part: the label, the type -- named by the   */
            /* one function Filesystem Properties names it with                */
            /* (FSP_TypeName), so the two never disagree -- and the bytes in   */
            /* use of the total.  On FAT the blocks are clusters, so the       */
            /* sizes above are worked out the same way.                        */
            sText = sLabel + "   " + FSP_clsFsPropertiesForm.FSP_TypeName(stVolume) + "   " +
                Main_FormatSize(ullTotal - ullFree) + " of " + Main_FormatSize(ullTotal) + " bytes used";

            if (ullOffset != 0)
            {   /* A partition inside a disk image: say where it starts. */
                sText = sText + "   at offset " + Main_FormatSize(ullOffset);
            }

            /* The access mode, in one word.  THE else BELONGS TO THIS TEST:   */
            /* the external-journal note is a separate test AFTER it; put      */
            /* between the two halves, the else would hang off the journal     */
            /* test and every read-only image would read "read-only            */
            /* READ-WRITE".                                                    */
            if (bReadOnly)
            {   /* Say so, since nothing can be changed in this mode. */
                sText = sText + "   read-only";
            }
            else
            {   /* Say so just as plainly: changes now go to the image file. */
                sText = sText + "   READ-WRITE";
            }

            if (stVolume.VLI_uiExternalJournal != 0)
            {   /* Its journal is on another device, which is why it can never   */
                /* be opened for writing here: say so beside the mode, so the    */
                /* refusal of the first change does not come as a surprise.      */
                sText = sText + " (external journal)";
            }

            /* The whole summary. */
            return(sText);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnExtractClicked:                                             */
        /*                                                                    */
        /* Commands > Extract to Windows (Main_RequestExtract).               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the extract is posted, or the status line says why not; */
        /*            its report comes when the copy ends.                    */
        /*--------------------------------------------------------------------*/
        private void Main_OnExtractClicked(object objSender, EventArgs evArgs)
        {
            /* The image pane's selection out to the Windows folder. */
            Main_RequestExtract();
        }

        /*--------------------------------------------------------------------*/
        /* Main_RequestExtract:                                               */
        /*                                                                    */
        /* Copy the rows selected in the image pane into the folder the       */
        /* Windows pane is showing, each under its own name.  Called by       */
        /* Commands > Extract to Windows, the shared toolbar's copy-to-the-   */
        /* right button, and the right-click menu's copy across from the      */
        /* image pane (Main_OnTransferClicked), each from its own handler.    */
        /* All three are enabled only while the Windows pane is showing       */
        /* (Main_UpdateCommandsMenu, Main_UpdateToolbar,                      */
        /* Main_UpdateEditCommands), so the folder copied into is the one on  */
        /* show; nothing here tests it again.                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the extract is posted, or the status line says why not; */
        /*            its report comes when the copy ends.                    */
        /*--------------------------------------------------------------------*/
        private void Main_RequestExtract()
        {
            STV_clsStateView objSource;
            List<string> lstImage;
            List<string> lstHost;
            ListViewItem lviRow;
            int          i;

            /* The worker's last published state, read before the paths are   */
            /* made: the request names that open, and the worker's gate       */
            /* checks it is still the one open when it takes the request,     */
            /* after whatever was queued before it.                           */
            objSource = null;
            if (Main_objCommandClient != null)
            {   /* The state record itself (it is never changed). */
                objSource = Main_objCommandClient.CLI_ReadState();
            }

            if (Main_sHostDir.Length == 0)
            {   /* The Windows pane is showing the drive list, not a folder. */
                Main_SetStatus("Choose a Windows folder to extract into first.");
                return;
            }

            /* The pairs to copy: each item's image path, and the Windows   */
            /* path it goes to, by index.                                   */
            lstImage = new List<string>();
            lstHost  = new List<string>();

            for (i = 0; i < Main_lstImage.SelectedItems.Count; i++)
            {   /* One selected row per iteration. */
                lviRow = Main_lstImage.SelectedItems[i];

                if (lviRow.Tag == null)
                {   /* The ".." row is not an item. */
                    continue;
                }

                /* From the folder on show in the image, to the same name in   */
                /* the Windows folder; a name Windows cannot hold is the       */
                /* engine's to refuse, as a skipped item -- which is why the   */
                /* Windows path is joined by Main_JoinHostPath, never by       */
                /* Path.Combine, which would throw for such a name first.      */
                lstImage.Add(Main_JoinImagePath(Main_sImageDir, lviRow.Text));
                lstHost.Add(Main_JoinHostPath(Main_sHostDir, lviRow.Text));
            }

            if (lstImage.Count == 0)
            {   /* Nothing was selected. */
                Main_SetStatus("Select one or more items in the image pane to extract.");
                return;
            }

            /* Posted (Main_PostCommandCopy): the worker copies, with its     */
            /* questions and progress, and the report -- the counts and any   */
            /* problems -- comes when it ends.                                */
            Main_PostCommandCopy(KND_enmKind.Extract, lstImage.ToArray(), lstHost.ToArray(), objSource);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnImportClicked:                                              */
        /*                                                                    */
        /* Commands > Import into Image (Main_RequestImport).                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the import is posted, or the status line says why not;  */
        /*            its report comes when the copy ends.                    */
        /*--------------------------------------------------------------------*/
        private void Main_OnImportClicked(object objSender, EventArgs evArgs)
        {
            /* The Windows pane's selection into the image's folder. */
            Main_RequestImport();
        }

        /*--------------------------------------------------------------------*/
        /* Main_RequestImport:                                                */
        /*                                                                    */
        /* Copy the rows selected in the Windows pane into the directory the  */
        /* image pane is showing.  This is one command, including any         */
        /* first-write question and reopen.  Called by Commands > Import into */
        /* Image, the shared toolbar's copy-to-the-left button, and the       */
        /* right-click menu's copy across from the Windows pane               */
        /* (Main_OnTransferClicked), each from its own handler.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the import is posted, or the status line says why not;  */
        /*            its report comes when the copy ends.                    */
        /*--------------------------------------------------------------------*/
        private void Main_RequestImport()
        {
            STV_clsStateView   objSource;
            List<string>    lstImage;
            List<string>    lstHost;
            ListViewItem    lviRow;
            string          sHostPath;
            string[]        asImage;
            string[]        asHost;
            int             i;

            /* The worker's last published state, read before the paths are    */
            /* made, as for an extract: the open the items go into.  Whether   */
            /* the image must first be made writable, and the question that    */
            /* asks it, are the worker's, when the import runs.                */
            objSource = null;
            if (Main_objCommandClient != null)
            {   /* The state record itself (it is never changed). */
                objSource = Main_objCommandClient.CLI_ReadState();
            }

            if (Main_sHostDir.Length == 0)
            {   /* The drive list: a whole drive is not something to import. */
                Main_SetStatus("Go into a Windows folder and select files or folders to import.");
                return;
            }

            /* The pairs to copy: each item's image destination and its   */
            /* Windows source, by index.                                  */
            lstImage = new List<string>();
            lstHost  = new List<string>();

            for (i = 0; i < Main_lstHost.SelectedItems.Count; i++)
            {   /* One selected row per iteration. */
                lviRow = Main_lstHost.SelectedItems[i];

                if (lviRow.Tag == null)
                {   /* The ".." row is not an item. */
                    continue;
                }

                /* A folder row's Tag is its path; a file row's is a FileInfo. */
                if (lviRow.Tag is string)
                {   /* A folder. */
                    sHostPath = (string)lviRow.Tag;
                }
                else
                {   /* A file. */
                    sHostPath = ((FileInfo)lviRow.Tag).FullName;
                }

                /* From that path, to the same name in the image folder on   */
                /* show.                                                     */
                lstHost.Add(sHostPath);
                lstImage.Add(Main_JoinImagePath(Main_sImageDir, lviRow.Text));
            }

            if (lstHost.Count == 0)
            {   /* Nothing was selected. */
                Main_SetStatus("Select one or more items in the Windows pane to import.");
                return;
            }

            /* The pairs into the request's arrays: both ends fixed now, so   */
            /* going to another folder before the import runs changes         */
            /* neither.                                                       */
            asImage = lstImage.ToArray();
            asHost  = lstHost.ToArray();

            /* Posted (Main_PostCommandCopy): the worker asks the first-    */
            /* write question if the image is read-only, then copies with   */
            /* its progress, and the report comes when it ends.             */
            Main_PostCommandCopy(KND_enmKind.Import, asImage, asHost, objSource);
        }

        /*--------------------------------------------------------------------*/
        /* Main_ImportPaths:                                                  */
        /*                                                                    */
        /* Import a list of Windows files and folders into the directory the  */
        /* image pane is showing, each under its own name.  This is what a    */
        /* DROP onto the image pane runs -- from the Windows pane or from     */
        /* Explorer -- and what a PASTE into it runs, so the paths come from  */
        /* the drop or the clipboard, not from a selection.                   */
        /*                                                                    */
        /* When the items were CUT, the import is the first half of a move:   */
        /* once every item is in the image the Windows originals go to the    */
        /* Recycle Bin.  If anything was skipped, failed or cancelled the     */
        /* originals are all left where they are.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     asHostPaths  : the full paths of the items to import.          */
        /*     bMoveSources : true when the items were cut, not copied.       */
        /*     objSource    : the state the drop or paste was made against,   */
        /*                    read before the other program's data was asked  */
        /*                    for; optional, null to read it here.            */
        /*     uiClipboard  : a clipboard cut's sequence number, so the       */
        /*                    clipboard is cleared after the move only if it  */
        /*                    still holds that cut; optional, 0 for anything  */
        /*                    else.                                           */
        /*     sImageFolder : the image folder to import into, read with      */
        /*                    objSource; optional, null for the folder on     */
        /*                    show now.                                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the import is posted -- a cut's removal of the          */
        /*            originals included -- or the status line says why not.  */
        /*--------------------------------------------------------------------*/
        private void Main_ImportPaths(string[] asHostPaths, bool bMoveSources,
            STV_clsStateView objSource = null, uint uiClipboard = 0, string sImageFolder = null)
        {
            string[] asImage;
            string   sName;
            int      i;

            if (asHostPaths == null || asHostPaths.Length == 0)
            {   /* An empty drop. */
                return;
            }

            /* A drop or a paste reads the state and the folder BEFORE it      */
            /* asks the other program for its data -- a call that, made from   */
            /* this single-threaded apartment, can let other window messages   */
            /* run before it returns -- and passes what it read, so the        */
            /* import goes where the drop or paste was made.  Any other        */
            /* caller has them read now.                                       */
            if (objSource == null && Main_objCommandClient != null)
            {   /* No state given: read once now. */
                objSource = Main_objCommandClient.CLI_ReadState();
            }

            if (sImageFolder == null)
            {   /* No folder given: the one on show now. */
                sImageFolder = Main_sImageDir;
            }

            /* Each item's image path, made now, so the request is fixed   */
            /* whatever the pane does afterwards.                          */
            asImage = new string[asHostPaths.Length];

            for (i = 0; i < asHostPaths.Length; i++)
            {   /* One dropped item per iteration: its own name, a trailing   */
                /* slash taken off first so a folder's path gives its name.   */
                sName = Path.GetFileName(asHostPaths[i].TrimEnd('\\', '/'));

                if (sName.Length == 0)
                {   /* A drive root has no name to land under. */
                    Main_SetStatus("A whole drive cannot be imported; drop folders or files instead.");
                    return;
                }

                /* That name in the image folder on show. */
                asImage[i] = Main_JoinImagePath(sImageFolder, sName);
            }

            /* Posted as one command: the copy, and for a cut the originals    */
            /* sent to the Recycle Bin afterwards -- only when every item      */
            /* was copied, which is the worker's test                          */
            /* (CommandRecycleSources.cs) -- and the clipboard cleared if it   */
            /* still holds this cut.                                           */
            Main_PostCommandCopy(KND_enmKind.Import, asImage, asHostPaths, objSource,
                bMoveSources, null, uiClipboard);
        }

        /*--------------------------------------------------------------------*/
        /* Main_SelectedHostPaths:                                            */
        /*                                                                    */
        /* The full paths of the rows selected in the Windows pane, leaving   */
        /* out the ".." row.  A folder row's Tag is its path and a file row's */
        /* is a FileInfo.                                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string[] : the selected items' paths; empty when none.         */
        /*--------------------------------------------------------------------*/
        private string[] Main_SelectedHostPaths()
        {
            List<string> lstPaths;
            ListViewItem lviRow;
            int          i;

            /* The paths gathered here, none yet. */
            lstPaths = new List<string>();

            for (i = 0; i < Main_lstHost.SelectedItems.Count; i++)
            {   /* One selected row per iteration. */
                lviRow = Main_lstHost.SelectedItems[i];

                if (lviRow.Tag == null)
                {   /* The ".." row is not an item. */
                    continue;
                }

                if (lviRow.Tag is string)
                {   /* A folder (or a drive, in the drive list). */
                    lstPaths.Add((string)lviRow.Tag);
                }
                else
                {   /* A file. */
                    lstPaths.Add(((FileInfo)lviRow.Tag).FullName);
                }
            }

            /* The paths, in the order shown. */
            return(lstPaths.ToArray());
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnImageDragEnter:                                             */
        /*                                                                    */
        /* Something is being dragged over the image pane: accept it as a     */
        /* copy when it is a list of files and an image is open.              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the image list.                                    */
        /*     evDrag    : the drag event; its Effect is the answer.          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : evDrag.Effect says whether a drop is allowed.           */
        /*--------------------------------------------------------------------*/
        private void Main_OnImageDragEnter(object objSender, DragEventArgs evDrag)
        {
            if (Main_bImageOpen && !evDrag.Data.GetDataPresent(MAIN_DRAG_IMAGE_FORMAT) &&
                evDrag.Data.GetDataPresent(DataFormats.FileDrop))
            {   /* Files from the Windows pane or from Explorer. */
                evDrag.Effect = DragDropEffects.Copy;
            }
            else
            {   /* Not something this pane can take. */
                evDrag.Effect = DragDropEffects.None;
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnImageDragDrop:                                              */
        /*                                                                    */
        /* Files dropped on the image pane -- from the Windows pane or from   */
        /* Explorer -- are imported into the folder it shows, as a copy:      */
        /* the originals stay (Main_ImportPaths).  The handler only posts,    */
        /* so the drop returns at once and the other program is not kept      */
        /* waiting for the copy, whose questions and progress come later.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the image list.                                    */
        /*     evDrag    : the drop event, carrying the paths.                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the import is posted.                                   */
        /*--------------------------------------------------------------------*/
        private void Main_OnImageDragDrop(object objSender, DragEventArgs evDrag)
        {
            STV_clsStateView objSource;
            string sImageFolder;
            string[] asPaths;

            /* The worker's state read FIRST, before the drop's data is       */
            /* asked for: a call to another program, made from this           */
            /* single-threaded apartment, can let other window messages run   */
            /* -- a state view among them -- before it returns.               */
            objSource = null;
            if (Main_objCommandClient != null)
            {   /* The state record itself (it is never changed). */
                objSource = Main_objCommandClient.CLI_ReadState();
            }

            /* The folder too, for the same reason: the import goes where   */
            /* the drop was made.                                           */
            sImageFolder = Main_sImageDir;
            if (evDrag.Data.GetDataPresent(MAIN_DRAG_IMAGE_FORMAT))
            {   /* The image pane's own drag, dropped back on it: ignored.    */
                /* Its file list would read the entries out to a temporary    */
                /* folder only to import them again.  Main_OnImageDragEnter   */
                /* already answers None for it, so this is a guard.           */
                return;
            }

            /* The dropped paths (cloned below, so the request holds an   */
            /* array of its own).                                         */
            asPaths = evDrag.Data.GetData(DataFormats.FileDrop) as string[];

            if (asPaths == null)
            {   /* The data was not a file list after all. */
                return;
            }

            /* Imported as a copy (false: the originals stay where they   */
            /* are), against the state and into the folder read above.    */
            Main_ImportPaths((string[])asPaths.Clone(), false, objSource, 0, sImageFolder);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnHostDragEnter:                                              */
        /*                                                                    */
        /* Something is being dragged over the Windows pane: accept it as a   */
        /* copy only when it is this program's own image-pane marker and the  */
        /* pane is showing a real folder to extract into.                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the host list.                                     */
        /*     evDrag    : the drag event; its Effect is the answer.          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : evDrag.Effect says whether a drop is allowed.           */
        /*--------------------------------------------------------------------*/
        private void Main_OnHostDragEnter(object objSender, DragEventArgs evDrag)
        {
            DGI_clsDragInput objDrag;

            /* The drag of ours under way, if any.  Only its own marker is     */
            /* taken: a marker from another copy of the program, or one left   */
            /* from an older drag, does not match, so this pane never          */
            /* extracts anything but the selection being dragged.              */
            objDrag = Main_objDragInput;

            if (objDrag != null && Main_sHostDir.Length != 0 && evDrag.Data.GetDataPresent(MAIN_DRAG_IMAGE_FORMAT) &&
                evDrag.Data.GetData(MAIN_DRAG_IMAGE_FORMAT) as string == objDrag.DGI_sMarker)
            {   /* The image pane's selection, over a folder. */
                evDrag.Effect = DragDropEffects.Copy;
            }
            else
            {   /* Anything else: this pane is not a general drop target. */
                evDrag.Effect = DragDropEffects.None;
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnHostDragDrop:                                               */
        /*                                                                    */
        /* The image pane's selection was dropped on the Windows pane: the    */
        /* selection as it was when the drag began is extracted into the      */
        /* folder this pane shows (Main_ExtractPaths) -- straight from the    */
        /* image, not through the drag's file list, which would read it out   */
        /* to a temporary folder first.                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the host list.                                     */
        /*     evDrag    : the drop event.                                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the extract is posted.                                  */
        /*--------------------------------------------------------------------*/
        private void Main_OnHostDragDrop(object objSender, DragEventArgs evDrag)
        {
            DGI_clsDragInput objDrag;
            string sHostFolder;

            /* The drag and this pane's folder read FIRST, before the drop's   */
            /* data is asked for (that can let other messages run, as for a    */
            /* drop on the image pane).  Reading the marker gives back the     */
            /* stored text only; it never makes the drag data read anything    */
            /* out of the image.                                               */
            objDrag = Main_objDragInput;
            sHostFolder = Main_sHostDir;

            if (objDrag == null || sHostFolder.Length == 0 || !evDrag.Data.GetDataPresent(MAIN_DRAG_IMAGE_FORMAT) ||
                evDrag.Data.GetData(MAIN_DRAG_IMAGE_FORMAT) as string != objDrag.DGI_sMarker || Main_objDragInput != objDrag)
            {   /* No drag of ours, the drive list, a marker that is not      */
                /* this drag's, or the drag ended while the data was read:    */
                /* nothing extracted.  Main_OnHostDragEnter refuses all but   */
                /* the last already, so this is mostly a guard.               */
                return;
            }

            /* An ordinary extract of the drag's own paths and state -- not   */
            /* the pane's selection now.  It cannot run until the drag        */
            /* ends: the drag's hold lets only its read-out and its end       */
            /* through (Sys_Take), and it is posted as a follow-up, not as    */
            /* fresh user work, which the hold would refuse                   */
            /* (Main_PostCommandCopy).  The values are the record's own, so   */
            /* clearing Main_objDragInput when the drag returns does not      */
            /* touch the request.                                             */
            Main_ExtractPaths(objDrag.DGI_arrPaths, null, false,
                objDrag.DGI_objSource, 0, sHostFolder);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnImageItemDrag:                                              */
        /*                                                                    */
        /* A drag began in the image pane.  The data carries two things: a    */
        /* MARKER in a private format, which this program's own Windows pane  */
        /* recognises (it then extracts the selection as it was when the      */
        /* drag began, as any extract), and a FILE LIST for any other         */
        /* program, such as Explorer.  The file list names paths in a         */
        /* temporary folder of this drag's own, and the items are only        */
        /* extracted there if another program really takes the drop           */
        /* (DragData.cs explains how).                                        */
        /*                                                                    */
        /* A selected entry whose name Windows cannot hold is left out of the */
        /* file list; once the drag is over the status bar says how many      */
        /* were, and whether the read-out skipped or failed any item.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the image list.                                    */
        /*     evDrag    : the drag event.                                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : returns when the drag has ended.                        */
        /*--------------------------------------------------------------------*/
        private void Main_OnImageItemDrag(object objSender, ItemDragEventArgs evDrag)
        {
            STV_clsStateView     objSource;
            DGI_clsDragInput objDrag;
            EXO_stExtractOptions stExtractOptions;
            DRG_clsImageDragData objDragData;
            Dgm_clsDragMaterializer objMaterializer;
            SYS_clsSystem objSystem;
            List<string>      lstAllPaths;
            List<string>      lstImagePaths;
            List<string>      lstHostPaths;
            ListViewItem      lviRow;
            string[]          arrImagePaths;
            string[]          arrHostPaths;
            string            sFolder;
            string            sImageFolder;
            string            sStatus;
            long              lToken;
            int               iLeftOut;
            int               i;

            /* The worker's last published state, read before the drag       */
            /* starts: the selection is labelled with that open, and the     */
            /* worker's hold below is granted only for it (SYS_ReserveDrag   */
            /* tests its epoch).                                             */
            if (Main_objCommandClient == null)
            {   /* No command system yet: no state, so the test below drops   */
                /* the drag.                                                  */
                objSource = null;
            }
            else
            {   /* The state record itself (it is never changed). */
                objSource = Main_objCommandClient.CLI_ReadState();
            }

            /* The folder the selected rows are in, read in the same turn. */
            sImageFolder = Main_sImageDir;

            if (objSource == null || objSource.STV_objTarget == null)
            {   /* No state, or nothing open in it: nothing to drag. */
                return;
            }

            /* This drag's own folder under %TEMP% -- the prefix, this        */
            /* process's id and a random part, so no two drags share one      */
            /* and the start-up sweep (Main_SweepDragFolders) can tell a      */
            /* running copy's folders from a dead one's.  Only named here;    */
            /* it is created if and when the items are extracted.  Then the   */
            /* three lists the loop below fills, empty, and no names left     */
            /* out yet.                                                       */
            sFolder  = Path.Combine(Path.GetTempPath(),
                                    MAIN_DRAG_TEMP_PREFIX + System.Diagnostics.Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N"));
            lstImagePaths = new List<string>();
            lstHostPaths  = new List<string>();
            lstAllPaths   = new List<string>();
            iLeftOut = 0;

            /* The extract options read now, as the engine's 0 / 1: a drag   */
            /* can last, and the read-out, if there is one, uses what the    */
            /* Options menu said when the drag began.  First the metadata    */
            /* manifest ...                                                  */
            stExtractOptions = new EXO_stExtractOptions();
            if (Main_tsmOptMetaOut.Checked)
            {   /* Ticked: the manifest is written. */
                stExtractOptions.EXO_uiWriteManifest = 1U;
            }
            else
            {   /* Not ticked: no manifest. */
                stExtractOptions.EXO_uiWriteManifest = 0U;
            }

            /* ... then whether the files get the image's modify times. */
            if (Main_tsmOptSetTimes.Checked)
            {   /* Ticked: the image's times on the Windows files. */
                stExtractOptions.EXO_uiSetTimes = 1U;
            }
            else
            {   /* Not ticked: Windows' own times (the copy's). */
                stExtractOptions.EXO_uiSetTimes = 0U;
            }

            /* ... and whether a symbolic link is copied as what it names. */
            if (Main_tsmOptFollowLinks.Checked)
            {   /* Ticked: each link's target copied under the link's name. */
                stExtractOptions.EXO_uiFollowLinks = 1U;
            }
            else
            {   /* Not ticked: links skipped, as NTFS cannot hold them. */
                stExtractOptions.EXO_uiFollowLinks = 0U;
            }

            for (i = 0; i < Main_lstImage.SelectedItems.Count; i++)
            {   /* One selected row per iteration. */
                lviRow = Main_lstImage.SelectedItems[i];

                if (lviRow.Tag == null)
                {   /* The ".." row is not an item. */
                    continue;
                }

                /* Every selected entry goes on the full list, which a drop   */
                /* on the program's own Windows pane extracts -- there a      */
                /* name Windows cannot hold is the engine's to skip and       */
                /* report, as in any extract.                                 */
                lstAllPaths.Add(Main_JoinImagePath(sImageFolder, lviRow.Text));

                if (!Main_ValidHostName(lviRow.Text))
                {   /* Windows could not hold a file of this name. */
                    iLeftOut++;
                    continue;
                }

                /* The entry, and the path it will have in the drag's   */
                /* folder once extracted.                               */
                lstImagePaths.Add(Main_JoinImagePath(sImageFolder, lviRow.Text));
                lstHostPaths.Add(sFolder + "\\" + lviRow.Text);
            }

            if (lstAllPaths.Count == 0)
            {   /* Only the ".." row was selected: no drag. */
                return;
            }

            /* The file plan into arrays (the drag data and the read-out    */
            /* share them), then the worker's hold asked for.  The worker   */
            /* must be idle and must stay so: inside a drop on another      */
            /* program the UI thread waits for the read-out and could       */
            /* answer no question meanwhile (SYS_ReserveDrag).              */
            arrImagePaths = lstImagePaths.ToArray();
            arrHostPaths  = lstHostPaths.ToArray();
            objSystem = Main_objCommandClient.CLI_ReadSystem();
            lToken = objSystem.SYS_ReserveDrag(objSource.STV_lEpoch);

            if (lToken == 0)
            {   /* Not granted -- the worker is busy, work is waiting, a      */
                /* picker is open, or the open has changed: no drag, and      */
                /* why said.  Starting it anyway could leave the UI waiting   */
                /* inside the drop for a worker that is waiting for the UI.   */
                Main_SetStatus("Busy -- wait for the current operation to finish before dragging.");
                return;
            }

            try
            {   /* Held from here: the finally below releases it however the   */
                /* drag ends -- a throw included, which still goes on to the   */
                /* caller.  First the drag's record, made the window's drag    */
                /* under way before the drag loop starts: the Windows pane's   */
                /* DragEnter and DragDrop compare its marker.  It holds the    */
                /* full list; the file plan for other programs is the arrays   */
                /* above.  Then the read-out, captured for the drag data,      */
                /* and the data itself: the marker under the private format,   */
                /* and the file plan.                                          */
                objDrag = new DGI_clsDragInput(objSource, lToken, lstAllPaths.ToArray());
                Main_objDragInput = objDrag;
                objMaterializer = new Dgm_clsDragMaterializer(this, sFolder,
                    arrImagePaths, arrHostPaths, objDrag.DGI_objSource,
                    objDrag.DGI_lToken, stExtractOptions);
                objDragData = new DRG_clsImageDragData(MAIN_DRAG_IMAGE_FORMAT,
                    arrHostPaths, objMaterializer.DGM_Run, objDrag.DGI_sMarker);

                /* The drag: DoDragDrop runs Windows' drag loop and returns   */
                /* only when the drag ends.  A drop on the program's own      */
                /* Windows pane posts an extract that waits for the hold to   */
                /* end; a drop on another program makes it ask for the        */
                /* files, and the read-out runs inside this call (DGM_Run),   */
                /* with its own feedback window.                              */
                Main_lstImage.DoDragDrop(objDragData, DragDropEffects.Copy | DragDropEffects.Move);

                /* What the drag's end has to say, now the main window can show   */
                /* it: first a read-out that handed files over with some          */
                /* skipped or failed (Main_MaterializeDrag left its sentence on   */
                /* the read-out; "" when there was none, or none ran).            */
                sStatus = objMaterializer.DGM_sProblem;

                if (objMaterializer.DGM_bHandedOver && iLeftOut != 0)
                {   /* Some names were left out of the files another program       */
                    /* took: said too.  Not said when no program asked for         */
                    /* files, when the drop was on the program's own pane, where   */
                    /* those entries are extracted -- or skipped and reported      */
                    /* -- as in any extract, nor when the read-out was cancelled   */
                    /* or refused and handed nothing over: its own status line     */
                    /* (Main_MaterializeDrag) then stands.                         */
                    if (sStatus.Length != 0)
                    {   /* After the read-out's sentence, on the same line. */
                        sStatus = sStatus + "  ";
                    }

                    /* The count of names left out. */
                    sStatus = sStatus + iLeftOut + " selected item(s) have names Windows cannot hold and were not dragged out.";
                }

                if (sStatus.Length != 0)
                {   /* Something to say: on the status line.  Nothing to say   */
                    /* leaves the line as the read-out or the drop left it.    */
                    Main_SetStatus(sStatus);
                }
            }
            finally
            {   /* However the drag ended: the record cleared, and DragEnded   */
                /* posted with the hold's token, which releases the hold       */
                /* (CommandSequences.cs) -- only then can the extract a drop   */
                /* on the Windows pane posted, an Exit, or anything else       */
                /* that waited, run.  Posted on the command system directly:   */
                /* no completion is wanted, and it is a notification,          */
                /* admitted even while an Exit is pending.                     */
                Main_objDragInput = null;
                objSystem.SYS_Post(CFC_clsCommands.CFC_Create(KND_enmKind.DragEnded,
                    0, null, null, null, lToken, 0, null, "", ""));
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_MaterializeDrag:                                              */
        /*                                                                    */
        /* The drag's read-out (DGM_Run): another program has taken the drop  */
        /* and asked for the files, so the dragged entries are extracted into */
        /* the drag's temporary folder and the paths that now exist are       */
        /* handed back.  It runs on the UI thread, inside the drop, and the   */
        /* other program needs the names before it can go on -- so the        */
        /* extract is posted and WAITED for (CLI_ExtractDrag), with a         */
        /* feedback window on a thread of its own, since the main window can  */
        /* show nothing until this returns.                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sFolder       : the drag's temporary folder (made here); kept  */
        /*                     for the exit sweep.                            */
        /*     arrImagePaths : the entries to extract, as image paths.        */
        /*     arrHostPaths  : each one's path in that folder.                */
        /*     objSource     : the state when the drag began.                 */
        /*     lToken        : the worker's hold for the drag; it is released */
        /*                     when the drag returns, not here.               */
        /*     stOptions     : the extract options at the drag's start.       */
        /*     sProblem      : receives, when the extract ran but did not     */
        /*                     end clean (some items skipped or failed, or    */
        /*                     the engine stopped part-way), one sentence     */
        /*                     saying so, already logged; "" otherwise -- a   */
        /*                     clean extract, or one that handed nothing      */
        /*                     over, whose reason is in the status line.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string[] : the planned paths that exist after the extract --   */
        /*                all of them, or as far as a failed extract got;     */
        /*                empty when it was cancelled or refused or could not */
        /*                start.                                              */
        /*--------------------------------------------------------------------*/
        private string[] Main_MaterializeDrag(string sFolder, string[] arrImagePaths,
            string[] arrHostPaths, STV_clsStateView objSource, long lToken,
            EXO_stExtractOptions stOptions, out string sProblem)
        {
            SYS_clsSystem objSystem;
            RQT_clsRequest objRequest;
            RSL_clsResult objResult;
            ECR_clsCopyResult objReport;
            DFW_clsFeedbackWindow objFeedback;
            Dmc_clsDragCallbacks objCallbacks;
            List<string> lstExist;
            string sLogged;
            bool bStarted;
            int i;

            /* No problem to report until the extract has run and said so. */
            sProblem = "";

            if (Main_objCommandClient == null || arrImagePaths.Length == 0)
            {   /* No command system, or every selected name was one Windows   */
                /* cannot hold: nothing to extract and nothing handed over.    */
                /* The hold is still released when the drag returns            */
                /* (Main_OnImageItemDrag's finally).                           */
                return(new string[0]);
            }

            /* The command system itself, for the feedback window's Cancel:   */
            /* posted on it directly from the feedback window's thread,       */
            /* never through the command client, whose replies belong to      */
            /* this thread -- which is about to wait below.                   */
            objSystem = Main_objCommandClient.CLI_ReadSystem();
            try
            {   /* The folder made now, only now that files are wanted. */
                Directory.CreateDirectory(sFolder);
            }
            catch (Exception exCreate)
            {   /* It cannot be made: nothing to extract into.  Logged, and   */
                /* the other program is given no files.                       */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("drag", "cannot create " + sFolder + ": " + exCreate.Message);
                }

                /* No files for the other program. */
                return(new string[0]);
            }

            /* Kept for the exit sweep (Main_SweepDragFolders), not removed   */
            /* when the drop ends: the other program may still be copying     */
            /* out of it.  Then the feedback window, not yet started.         */
            Main_lstDragFolders.Add(sFolder);
            objFeedback = new DFW_clsFeedbackWindow();
            bStarted = false;
            try
            {   /* The request -- the drag's read-out, carrying the hold's     */
                /* token, the state and target the drag began with, the plan   */
                /* and the options (the import options are unused) -- and      */
                /* the two calls the feedback window and the progress make.    */
                /* The window is given its Cancel first (DFW_AttachCommand     */
                /* must come before it starts), then put on screen before      */
                /* anything can report to it.                                  */
                objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.DragExtract,
                    0, objSource, objSource.STV_objTarget, arrImagePaths, lToken,
                    0, new CCA_clsCopyArgs(arrHostPaths, stOptions,
                        new IMO_stImportOptions()), "", "");
                objCallbacks = new Dmc_clsDragCallbacks(objSystem, objRequest, objFeedback);
                objFeedback.DFW_AttachCommand(objCallbacks.DMC_RequestCancel);
                objFeedback.DFW_Start();
                bStarted = true;

                /* Posted and waited for (CLI_ExtractDrag): the post gives    */
                /* the request its serial, then the window's Cancel is        */
                /* turned on (DFW_ArmCommand); progress reaches the window    */
                /* through DMC_Report, and the result comes straight back     */
                /* here -- nothing goes through this thread's message loop,   */
                /* which is not running.                                      */
                objResult = Main_objCommandClient.CLI_ExtractDrag(objRequest,
                    objCallbacks.DMC_Report, objFeedback.DFW_ArmCommand);
            }
            catch (Exception exDrag)
            {   /* The set-up threw -- a request the factory refused, the     */
                /* feedback window, a channel already in use: logged, said,   */
                /* and nothing handed over.  Not tried again: the drag data   */
                /* keeps this empty answer for any later request.             */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("drag", exDrag.ToString());
                }

                /* Said on the status line, and no files handed over. */
                Main_SetStatus("Unable to extract the drag: " + exDrag.Message);
                return(new string[0]);
            }
            finally
            {   /* After the result, or after a set-up failure: the   */
                /* feedback window closed, if it was ever started.    */
                if (bStarted)
                {   /* Started: closed and its thread waited for. */
                    try
                    {   /* DFW_Stop posts the close to the window's thread   */
                        /* and joins it.                                     */
                        objFeedback.DFW_Stop();
                    }
                    catch (Exception exFeedback)
                    {   /* A failure closing it is only logged: the result   */
                        /* already in hand stands.                           */
                        if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                        {   /* Only while the debug log is kept: otherwise the line is never built. */
                            DBGLOG_clsDebugLog.DBGLOG_Write("drag feedback close", exFeedback.ToString());
                        }
                    }
                }
            }

            if (objResult.RSL_iStatus == ENG_clsNative.ENG_ERR_CANCELLED ||
                objResult.RSL_iStatus < 0)
            {   /* Cancelled, or not run at all -- refused by the worker's    */
                /* gate, or a command-system failure (the SYS_ statuses are   */
                /* negative): no files handed over, not even those already    */
                /* written, and the reason logged and said.                   */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("drag", objResult.RSL_sDetail + "; nothing handed over from " + sFolder);
                }

                /* The reason said: the one given, or a general one. */
                if (objResult.RSL_sDetail.Length == 0)
                {   /* No reason given (a plain Cancel): said in general. */
                    Main_SetStatus("Drag cancelled; nothing was handed over.");
                }
                else
                {   /* The worker's own reason. */
                    Main_SetStatus(objResult.RSL_sDetail);
                }

                /* Nothing handed over. */
                return(new string[0]);
            }

            if (objResult.RSL_iStatus != ENG_clsNative.ENG_OK)
            {   /* The extract ran and did not end clean: some items were      */
                /* skipped or failed (a partial copy), or the engine stopped   */
                /* part-way.  What exists is still handed over below -- the    */
                /* other program is owed what was read out -- but the user     */
                /* is told, or a missing file would only be found later.       */
                /* The sentence: the status's own words, then the copy's       */
                /* counts and its first problem line when the result holds     */
                /* the copy's report (the Copy step's payload), else the       */
                /* result's detail.                                            */
                objReport = objResult.RSL_objPayload as ECR_clsCopyResult;
                sProblem = "Dragged out with problems: " + ENG_clsNative.ENG_StatusText(objResult.RSL_iStatus);
                if (objReport != null)
                {   /* The counts, as the ordinary copy report gives them, and   */
                    /* the first problem line when there is one.                 */
                    sProblem = sProblem + "  " + objReport.ECR_iCopied + " item(s) copied, " +
                        objReport.ECR_iProblems + " skipped or failed.";
                    if (objReport.ECR_lstProblems != null && objReport.ECR_lstProblems.Count != 0)
                    {   /* The first problem, which names the item. */
                        sProblem = sProblem + "  " + objReport.ECR_lstProblems[0];
                    }
                }
                else if (objResult.RSL_sDetail.Length != 0)
                {   /* No report: the result's own detail stands in for it. */
                    sProblem = sProblem + "  " + objResult.RSL_sDetail;
                }

                /* Into the debug log too (an error, so logged): the sentence,   */
                /* the result's detail, and every problem line the report        */
                /* kept -- the status line can show only the first.              */
                sLogged = sProblem;
                if (objResult.RSL_sDetail.Length != 0)
                {   /* The engine's own detail line. */
                    sLogged = sLogged + "; " + objResult.RSL_sDetail;
                }

                if (objReport != null && objReport.ECR_lstProblems != null)
                {   /* The report's problem lines. */
                    for (i = 0; i < objReport.ECR_lstProblems.Count; i++)
                    {   /* One problem line per iteration. */
                        sLogged = sLogged + "; " + objReport.ECR_lstProblems[i];
                    }
                }

                /* Logged with the folder the files were read out to.  The      */
                /* status line is the drag's end's to set (DGM_sProblem): the   */
                /* main window shows nothing until the drop returns.            */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("drag", sLogged + " (read out to " + sFolder + ")");
                }
            }

            /* A success, or the extract that did not end clean: the planned   */
            /* paths that exist now are handed over -- what was extracted,     */
            /* all of it or as far as the extract got.                         */
            lstExist = new List<string>();
            for (i = 0; i < arrHostPaths.Length; i++)
            {   /* One planned path per iteration. */
                if (File.Exists(arrHostPaths[i]) || Directory.Exists(arrHostPaths[i]))
                {   /* There (a file or a folder): handed over. */
                    lstExist.Add(arrHostPaths[i]);
                }
            }

            /* The paths -- which the drag data keeps and hands out again to   */
            /* any later request for the files.                                */
            return(lstExist.ToArray());
        }

        /*--------------------------------------------------------------------*/
        /* Main_SweepDragFolders:                                             */
        /*                                                                    */
        /* Remove the program's folders under %TEMP% -- drags' and every      */
        /* Main_NewTempFolder's, which share MAIN_DRAG_TEMP_PREFIX: this      */
        /* run's own (at exit), or those an earlier run left because it       */
        /* crashed (at start-up).  A                                          */
        /* folder's name carries the process id of the run that made it; one  */
        /* whose process is still a running BifrostFM is another copy's and   */
        /* is left alone.                                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bOwnOnly : true to remove only the folders this run made;      */
        /*                false to remove every folder no running copy owns.  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : every removable folder is gone; a folder still in use   */
        /*            (a program copying out of it) is left for the next      */
        /*            start.                                                  */
        /*--------------------------------------------------------------------*/
        private void Main_SweepDragFolders(bool bOwnOnly)
        {
            string[] asFolders;
            string   sTail;
            int      iDash;
            int      iPid;
            int      i;

            if (bOwnOnly)
            {   /* Exit: the folders this run created. */
                asFolders = Main_lstDragFolders.ToArray();
            }
            else
            {   /* Start-up: whatever is lying in %TEMP%. */
                try
                {   /* Listing %TEMP% can be refused. */
                    asFolders = Directory.GetDirectories(Path.GetTempPath(), MAIN_DRAG_TEMP_PREFIX + "*");
                }
                catch (Exception)
                {   /* Unreadable: nothing is swept. */
                    return;
                }
            }

            for (i = 0; i < asFolders.Length; i++)
            {   /* One folder per iteration. */
                if (!bOwnOnly)
                {   /* Whose is it?  The name is prefix, process id, dash, random part. */
                    sTail = Path.GetFileName(asFolders[i]).Substring(MAIN_DRAG_TEMP_PREFIX.Length);
                    iDash = sTail.IndexOf('-');

                    if (iDash > 0 && int.TryParse(sTail.Substring(0, iDash), out iPid) && Main_IsRunningCopy(iPid))
                    {   /* Another running copy's folder. */
                        continue;
                    }
                }

                try
                {   /* Fails, harmlessly, while another program is reading from it. */
                    Directory.Delete(asFolders[i], true);
                }
                catch (Exception)
                {   /* In use or not removable; the next start tries again. */
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_IsRunningCopy:                                                */
        /*                                                                    */
        /* Whether a process id belongs to a running copy of this program.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iPid : the process id.                                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when a process of that id exists and has this      */
        /*            program's name.                                         */
        /*--------------------------------------------------------------------*/
        private static bool Main_IsRunningCopy(int iPid)
        {
            System.Diagnostics.Process procOther;

            try
            {   /* GetProcessById throws when no such process exists. */
                procOther = System.Diagnostics.Process.GetProcessById(iPid);

                /* The id may have been reused by an unrelated program. */
                return(procOther.ProcessName == System.Diagnostics.Process.GetCurrentProcess().ProcessName);
            }
            catch (Exception)
            {   /* Gone, or not ours to inspect. */
                return(false);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnHostItemDrag:                                               */
        /*                                                                    */
        /* A drag began in the Windows pane.  The data is the standard        */
        /* file-drop list of the selected paths, which the image pane accepts */
        /* and -- because it IS the standard format -- so does Explorer or    */
        /* any other program.                                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the host list.                                     */
        /*     evDrag    : the drag event.                                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : returns when the drag has ended.                        */
        /*--------------------------------------------------------------------*/
        private void Main_OnHostItemDrag(object objSender, ItemDragEventArgs evDrag)
        {
            string[] asPaths;
            SYS_clsSystem objSystem;
            long lToken;

            if (Main_objCommandClient == null)
            {   /* No command system yet: no hold can be asked for, and a     */
                /* drag is not started without one (as for the image pane).   */
                return;
            }

            /* The selected items' full paths (the ".." row left out). */
            asPaths = Main_SelectedHostPaths();

            if (asPaths.Length == 0 || Main_sHostDir.Length == 0)
            {   /* Nothing selected, or the drive list (drives are not dragged). */
                return;
            }

            /* The worker's hold, as for a drag out of the image pane, but   */
            /* for Windows files only, so no open image is needed (true).    */
            /* While it is held no engine work and no question from the      */
            /* worker starts, so nothing runs under the drag loop; the       */
            /* reason beyond that is not recorded.                           */
            objSystem = Main_objCommandClient.CLI_ReadSystem();
            lToken = objSystem.SYS_ReserveDrag(0, true);
            if (lToken == 0)
            {   /* Not granted -- the worker busy, work waiting, an Exit   */
                /* under way: no drag, and why said.                       */
                Main_SetStatus("Busy -- wait for the current operation to finish before dragging.");
                return;
            }

            /* The hold recorded for the drag's length: a drop on the image    */
            /* pane posts its import as a follow-up, not as fresh user work,   */
            /* which the hold would refuse (Main_PostCommandCopy reads this    */
            /* field).                                                         */
            Main_lHostDrag = lToken;
            try
            {   /* The drag: the paths in the shell's file-list format, so   */
                /* the image pane and any other program can take them; a     */
                /* copy only.  DoDragDrop returns when the drag ends.        */
                Main_lstHost.DoDragDrop(new DataObject(DataFormats.FileDrop, asPaths), DragDropEffects.Copy);
            }
            finally
            {   /* However the drag ended: the field cleared, and DragEnded   */
                /* posted with the token, which releases the hold -- so the   */
                /* import a drop posted can run.                              */
                Main_lHostDrag = 0;
                objSystem.SYS_Post(CFC_clsCommands.CFC_Create(KND_enmKind.DragEnded,
                    0, null, null, null, lToken, 0, null, "", ""));
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_ExtractPaths:                                                 */
        /*                                                                    */
        /* Extract a list of image entries into the folder the Windows pane   */
        /* is showing, each under its own name.  This is what a PASTE into    */
        /* the Windows pane runs, and a drop of the image pane's own drag on  */
        /* it (Main_OnHostDragDrop), so the paths come from the clipboard or  */
        /* the drag, not from the current selection.                          */
        /*                                                                    */
        /* When the entries were CUT, the extract is the first half of a      */
        /* move: once every one of them is in Windows they are deleted from   */
        /* the image.  If anything was skipped (a symbolic link, a name       */
        /* Windows cannot hold), failed or was cancelled, nothing is deleted. */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     asImagePaths : the image paths to extract.                     */
        /*     abIsDir      : for each, true when it is a directory (the      */
        /*                    delete that follows a cut needs to know).       */
        /*     bMoveSources : true when the entries were cut, not copied.     */
        /*     objSource    : the state the paste or drop was made against,   */
        /*                    read before the clipboard or the drop's data;   */
        /*                    optional, null to read it here.                 */
        /*     uiClipboard  : a clipboard cut's sequence number, so the       */
        /*                    clipboard is cleared afterwards only if it      */
        /*                    still holds that cut; optional, 0.              */
        /*     sHostFolder  : the Windows folder to extract into, read with   */
        /*                    objSource; optional, null for the folder on     */
        /*                    show now.                                       */
        /*     bClipboardInput : true when the paths come from the            */
        /*                    clipboard: the request then carries its         */
        /*                    clipboard lifetime, which the worker checks;    */
        /*                    optional, false.                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the extract -- and for a cut, the deletion from the     */
        /*            image -- is posted, or the status line says why not.    */
        /*--------------------------------------------------------------------*/
        private void Main_ExtractPaths(string[] asImagePaths, bool[] abIsDir, bool bMoveSources,
            STV_clsStateView objSource = null, uint uiClipboard = 0, string sHostFolder = null, bool bClipboardInput = false)
        {
            long lClipboardLifetime;
            bool[] arrDirectories;
            string[]    asHost;
            int         iSlash;
            int         i;

            /* The state and the folder: given by a paste or a drop, which   */
            /* read them before the other data (as Main_ImportPaths says     */
            /* why); read now for any other caller.                          */
            if (objSource == null && Main_objCommandClient != null)
            {   /* No state given: read once now. */
                objSource = Main_objCommandClient.CLI_ReadState();
            }

            if (sHostFolder == null)
            {   /* No folder given: the one on show now. */
                sHostFolder = Main_sHostDir;
            }

            if (sHostFolder.Length == 0)
            {   /* The Windows pane is showing the drive list, not a folder. */
                Main_SetStatus("Choose a Windows folder to extract into first.");
                return;
            }

            /* Each entry's destination in the Windows folder, by index. */
            asHost = new string[asImagePaths.Length];

            for (i = 0; i < asImagePaths.Length; i++)
            {   /* One image entry per iteration: its name is what follows the last slash. */

                /* Joined by Main_JoinHostPath, not Path.Combine: a name     */
                /* Windows cannot hold (a '|' in it, say) must reach the     */
                /* engine, which skips it, rather than throw here and stop   */
                /* the whole paste.                                          */
                iSlash    = asImagePaths[i].LastIndexOf('/');
                asHost[i] = Main_JoinHostPath(sHostFolder, asImagePaths[i].Substring(iSlash + 1));
            }

            /* A paste's paths carry the clipboard lifetime of the state    */
            /* they were read under: the worker refuses them if the image   */
            /* has closed since -- even closed and reopened, or only        */
            /* reopened for writing, which keeps the epoch -- since they    */
            /* may then name another lifetime's entries                     */
            /* (CommandSequences.cs).  -1 for a drop: its paths are the     */
            /* drag's own, and the epoch is test enough.                    */
            lClipboardLifetime = -1;
            if (bClipboardInput && objSource != null)
            {   /* From the clipboard: its lifetime. */
                lClipboardLifetime = objSource.STV_lClipboardLifetime;
            }

            /* Which entries are folders matters only to a cut, whose      */
            /* deletion from the image afterwards removes a folder         */
            /* differently from a file; the factory refuses the flags on   */
            /* anything else.                                              */
            arrDirectories = null;
            if (bMoveSources)
            {   /* A cut: the flags as the clipboard gave them (the request   */
                /* takes its own copy).                                       */
                arrDirectories = abIsDir;
            }

            /* Posted as one command: the extract, and for a cut the          */
            /* entries deleted from the image afterwards -- only when every   */
            /* one was copied, which is the worker's test.                    */
            Main_PostCommandCopy(KND_enmKind.Extract, asImagePaths, asHost, objSource,
                bMoveSources, arrDirectories, uiClipboard, false,
                lClipboardLifetime);
        }

        /*--------------------------------------------------------------------*/
        /* Main_NewEffectStream:                                              */
        /*                                                                    */
        /* The clipboard's "Preferred DropEffect" value: four bytes holding   */
        /* the drop effect a paste should perform.  It is the shell's own     */
        /* convention -- Explorer writes it and reads it -- so files cut here */
        /* are MOVED when pasted into Explorer, and files cut in Explorer are */
        /* recognised as cut when pasted here.                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bCut : true for a cut (move), false for a copy.                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     MemoryStream : the four bytes, ready to put on the clipboard.  */
        /*--------------------------------------------------------------------*/
        private static MemoryStream Main_NewEffectStream(bool bCut)
        {
            DragDropEffects enmPreferred;

            /* A copy unless ... */
            enmPreferred = DragDropEffects.Copy;

            if (bCut)
            {   /* ... a cut, which asks the paste to move. */
                enmPreferred = DragDropEffects.Move;
            }

            /* A little-endian 32-bit value, as the shell stores it. */
            return(new MemoryStream(BitConverter.GetBytes((int)enmPreferred)));
        }

        /*--------------------------------------------------------------------*/
        /* Main_SelectedCount:                                                */
        /*                                                                    */
        /* How many real items are selected in a pane's list -- the ".." row  */
        /* does not count, since no command acts on it.                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstPane : the pane's list.                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : the number of selected rows that are items.              */
        /*--------------------------------------------------------------------*/
        private static int Main_SelectedCount(ListView lstPane)
        {
            int iItems;
            int i;

            /* None counted yet. */
            iItems = 0;

            for (i = 0; i < lstPane.SelectedItems.Count; i++)
            {   /* One selected row per iteration. */
                if (lstPane.SelectedItems[i].Tag != null)
                {   /* A real item (the ".." row's Tag is null). */
                    iItems++;
                }
            }

            /* The count of selected items. */
            return(iItems);
        }

        /*--------------------------------------------------------------------*/
        /* Main_HostPaste:                                                    */
        /*                                                                    */
        /* Paste Windows files and folders into the folder the Windows pane   */
        /* is showing: a copy, or a move when they were cut.  The shell does  */
        /* the work, so the user gets Windows' own progress window and its    */
        /* own question when a destination already exists; cancelling either  */
        /* leaves that item where it was, stops the paste there and says so,  */
        /* and a cut keeps its clipboard unless every item moved.             */
        /*                                                                    */
        /* An item pasted into the folder it already lives in is left alone,  */
        /* and a folder is never pasted into itself or its own subfolder.     */
        /* It runs inside its pane-action input (Main_ExecutePaneAction,      */
        /* MainCommand.cs), so the worker starts nothing until it returns.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstHostPaths : the clipboard's Windows paths, as the paste     */
        /*                    read them.                                      */
        /*     bMove        : true when they were cut (moved, not copied).    */
        /*     sFolder      : the Windows folder to paste into, as read at    */
        /*                    the paste.                                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the items are pasted and the pane re-read, or a message */
        /*            names the one that stopped it.                          */
        /*--------------------------------------------------------------------*/
        private void Main_HostPaste(List<string> lstHostPaths, bool bMove, string sFolder)
        {
            string sSource;
            string sDest;
            int    iPasted;
            int    i;
            bool   bCancelled;

            if (sFolder.Length == 0)
            {   /* The drive list is not a folder to paste into. */
                Main_SetStatus("Go into a Windows folder to paste into first.");
                return;
            }

            /* None pasted yet, nothing cancelled. */
            iPasted = 0;
            bCancelled = false;

            for (i = 0; i < lstHostPaths.Count; i++)
            {   /* One clipboard item per iteration: the source without a   */
                /* trailing slash, so a folder's name can be taken from     */
                /* it, and the same name in this folder as its              */
                /* destination.                                             */
                sSource = lstHostPaths[i].TrimEnd('\\', '/');
                sDest   = Path.Combine(sFolder, Path.GetFileName(sSource));

                if (string.Equals(sSource, sDest, StringComparison.OrdinalIgnoreCase))
                {   /* It already lives in this folder. */
                    Main_SetStatus(Path.GetFileName(sSource) + " is already in this folder.");
                    continue;
                }

                if ((sDest + "\\").StartsWith(sSource + "\\", StringComparison.OrdinalIgnoreCase))
                {   /* A folder into itself or its own subfolder: it would never end. */
                    Main_SetStatus("A folder cannot be pasted into itself.");
                    continue;
                }

                try
                {   /* The shell's own copy or move, with all its dialogs       */
                    /* (progress, the exists question); it can be refused --    */
                    /* in use, no permission, no room -- which the last catch   */
                    /* takes.  A Cancel in its dialogs THROWS                   */
                    /* (UICancelOption.ThrowException, an                       */
                    /* OperationCanceledException by the framework's            */
                    /* documentation), so a cancelled item is never counted     */
                    /* as pasted.                                               */
                    if (Directory.Exists(sSource))
                    {   /* A folder, with everything in it. */
                        if (bMove)
                        {   /* Cut: move it. */
                            Microsoft.VisualBasic.FileIO.FileSystem.MoveDirectory(sSource, sDest,
                                Microsoft.VisualBasic.FileIO.UIOption.AllDialogs,
                                Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
                        }
                        else
                        {   /* Copied: copy it. */
                            Microsoft.VisualBasic.FileIO.FileSystem.CopyDirectory(sSource, sDest,
                                Microsoft.VisualBasic.FileIO.UIOption.AllDialogs,
                                Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
                        }
                    }
                    else if (bMove)
                    {   /* A file that was cut: move it. */
                        Microsoft.VisualBasic.FileIO.FileSystem.MoveFile(sSource, sDest,
                            Microsoft.VisualBasic.FileIO.UIOption.AllDialogs,
                            Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
                    }
                    else
                    {   /* A file that was copied: copy it. */
                        Microsoft.VisualBasic.FileIO.FileSystem.CopyFile(sSource, sDest,
                            Microsoft.VisualBasic.FileIO.UIOption.AllDialogs,
                            Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
                    }

                    /* Done without a Cancel: counted as pasted.  (A Skip      */
                    /* answered inside a folder's own copy or move still       */
                    /* returns normally, and that folder counts as pasted --   */
                    /* the shell reports no per-file outcome to tell it by.)   */
                    iPasted++;
                }
                catch (OperationCanceledException)
                {   /* Cancelled in the shell's dialog: this item and the     */
                    /* rest are not pasted, and the cancel is no error.  A    */
                    /* cut's clipboard is kept (below), so it can be pasted   */
                    /* again.                                                 */
                    bCancelled = true;
                    break;
                }
                catch (Exception exPaste)
                {   /* Stop at the first item Windows refuses. */
                    Main_ShowHostError("Pasting " + sSource, exPaste);
                    break;
                }
            }

            if (bMove && iPasted == lstHostPaths.Count)
            {   /* Every cut item has moved; the clipboard's paths no longer   */
                /* exist.  A cut that stopped part-way (a Cancel, a refusal,   */
                /* an item already here) keeps its clipboard: what did not     */
                /* move can be pasted again, and an item that did move is      */
                /* refused then as missing.                                    */
                Clipboard.Clear();
            }

            if (bCancelled)
            {   /* Stopped by a Cancel: said so, with what went before it. */
                Main_SetStatus("Paste cancelled; " + iPasted + " item(s) pasted into " + sFolder + " before it.");
            }
            else if (iPasted != 0)
            {   /* Say what happened (a skipped item's message stays otherwise). */
                Main_SetStatus(iPasted + " item(s) pasted into " + sFolder);
            }

            /* The folder re-read, to show what arrived. */
            Main_NavigateHost(sFolder);
        }

        /*--------------------------------------------------------------------*/
        /* Main_ImageCopy:                                                    */
        /*                                                                    */
        /* Paste image entries that were COPIED into the directory the image  */
        /* pane is showing: the engine copies each inside the image, a        */
        /* directory with everything under it, keeping modes, owners, times   */
        /* and symbolic links as they are.  It runs under the usual progress  */
        /* window, with the usual question when a destination file exists.    */
        /*                                                                    */
        /* An entry pasted into the directory it already lives in cannot keep */
        /* its name, so it is given one that is free there: "name - Copy",    */
        /* then "name - Copy (2)" and so on, as Explorer does.  The free name */
        /* is picked by the worker when the copy runs, from the names then in */
        /* the directory -- earlier queued changes included -- not from the   */
        /* pane's rows, which may be out of date by then.                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     asImagePaths : the copied entries' image paths.                */
        /*     objSource    : the state the paste was made against, read      */
        /*                    before the clipboard; optional, null to read it */
        /*                    here.                                           */
        /*     sImageFolder : the image folder to paste into, read with       */
        /*                    objSource; optional, null for the folder on     */
        /*                    show now.                                       */
        /*     bClipboardInput : true for a paste: the request carries the    */
        /*                    clipboard lifetime, as Main_ExtractPaths says;  */
        /*                    optional, false.                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the copy is posted, or the status line says why not.    */
        /*--------------------------------------------------------------------*/
        private void Main_ImageCopy(string[] asImagePaths, STV_clsStateView objSource = null,
            string sImageFolder = null, bool bClipboardInput = false)
        {
            long lClipboardLifetime;
            string[]     asTo;
            string       sName;
            int          iSlash;
            int          i;

            /* The state and the folder: given by a paste, which read them   */
            /* before the clipboard; read now for any other caller.          */
            if (objSource == null && Main_objCommandClient != null)
            {   /* No state given: read once now. */
                objSource = Main_objCommandClient.CLI_ReadState();
            }

            if (sImageFolder == null)
            {   /* No folder given: the one on show now. */
                sImageFolder = Main_sImageDir;
            }

            /* Each entry's destination in that folder, by index. */
            asTo = new string[asImagePaths.Length];

            for (i = 0; i < asImagePaths.Length; i++)
            {   /* One copied entry per iteration: its name is what follows the last slash. */
                iSlash = asImagePaths[i].LastIndexOf('/');
                sName  = asImagePaths[i].Substring(iSlash + 1);
                asTo[i] = Main_JoinImagePath(sImageFolder, sName);
            }

            /* A paste's paths carry their clipboard lifetime, as in   */
            /* Main_ExtractPaths; -1 for any other caller.             */
            lClipboardLifetime = -1;
            if (bClipboardInput && objSource != null)
            {   /* From the clipboard: its lifetime. */
                lClipboardLifetime = objSource.STV_lClipboardLifetime;
            }

            /* Posted as a copy within -- not a move -- with the worker     */
            /* picking a free name for any entry that cannot keep its own   */
            /* (the true: auto names).                                      */
            Main_PostCommandCopy(KND_enmKind.CopyWithin, asImagePaths, asTo, objSource,
                false, null, 0, true, lClipboardLifetime);
        }

        /*--------------------------------------------------------------------*/
        /* Main_ImageMove:                                                    */
        /*                                                                    */
        /* Paste image entries that were CUT into the directory the image     */
        /* pane is showing: each keeps its name and simply moves there.       */
        /* Inside one filesystem that is a rename, so nothing is copied and   */
        /* a whole tree moves at once.  An entry already in this directory is */
        /* left out; a directory is never moved into itself.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     asImagePaths : the cut entries' image paths.                   */
        /*     objSource    : the state the paste was made against, read      */
        /*                    before the clipboard; optional, null to read it */
        /*                    here.                                           */
        /*     uiClipboard  : the cut's sequence number, so the clipboard is  */
        /*                    cleared after the move only if it still holds   */
        /*                    that cut; optional, 0.                          */
        /*     sImageFolder : the image folder to move into, read with        */
        /*                    objSource; optional, null for the folder on     */
        /*                    show now.                                       */
        /*     bClipboardInput : true for a paste: the request carries the    */
        /*                    clipboard lifetime, as Main_ExtractPaths says;  */
        /*                    optional, false.                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the moves are posted, or the status line says why none  */
        /*            apply.                                                  */
        /*--------------------------------------------------------------------*/
        private void Main_ImageMove(string[] asImagePaths, STV_clsStateView objSource = null,
            uint uiClipboard = 0, string sImageFolder = null, bool bClipboardInput = false)
        {
            long lClipboardLifetime;
            List<string> lstFrom;
            List<string> lstTo;
            string[]     asFrom;
            string[]     asTo;
            string       sTo;
            int          iSlash;
            int          i;

            /* The state and the folder: given by a paste, which read them   */
            /* before the clipboard; read now for any other caller.          */
            if (objSource == null && Main_objCommandClient != null)
            {   /* No state given: read once now. */
                objSource = Main_objCommandClient.CLI_ReadState();
            }

            if (sImageFolder == null)
            {   /* No folder given: the one on show now. */
                sImageFolder = Main_sImageDir;
            }

            /* The pairs to move, from and to, by index. */
            lstFrom = new List<string>();
            lstTo   = new List<string>();

            for (i = 0; i < asImagePaths.Length; i++)
            {   /* One cut entry per iteration: it keeps the name after its last slash. */
                iSlash = asImagePaths[i].LastIndexOf('/');
                sTo    = Main_JoinImagePath(sImageFolder, asImagePaths[i].Substring(iSlash + 1));

                if (sTo == asImagePaths[i])
                {   /* Already in this directory: nothing to move. */
                    continue;
                }

                if ((sTo + "/").StartsWith(asImagePaths[i] + "/", StringComparison.Ordinal))
                {   /* A directory into itself or its own subdirectory. */
                    Main_SetStatus("A folder cannot be moved into itself.");
                    return;
                }

                /* A move to make. */
                lstFrom.Add(asImagePaths[i]);
                lstTo.Add(sTo);
            }

            if (lstFrom.Count == 0)
            {   /* Everything was already here. */
                Main_SetStatus("Those items are already in this folder.");
                return;
            }

            /* The pairs into the request's arrays.  A move inside one         */
            /* filesystem is a rename -- nothing copied, nothing deleted       */
            /* after -- and whether it may run (the image made writable, the   */
            /* cut not already pasted once) is the worker's to decide when     */
            /* it takes it.                                                    */
            asFrom = lstFrom.ToArray();
            asTo   = lstTo.ToArray();

            /* A paste's paths carry their clipboard lifetime, as in   */
            /* Main_ExtractPaths; -1 for any other caller.             */
            lClipboardLifetime = -1;
            if (bClipboardInput && objSource != null)
            {   /* From the clipboard: its lifetime. */
                lClipboardLifetime = objSource.STV_lClipboardLifetime;
            }

            /* Posted as a copy within WITH the move (true): each pair     */
            /* renamed, and the clipboard cleared afterwards if it still   */
            /* holds this cut.                                             */
            Main_PostCommandCopy(KND_enmKind.CopyWithin, asFrom, asTo, objSource,
                true, null, uiClipboard, false, lClipboardLifetime);
        }

        /*--------------------------------------------------------------------*/
        /* Main_CopyToClipboard:                                              */
        /*                                                                    */
        /* Edit > Cut / Copy, and their buttons and right-click twins: ask    */
        /* for a pane's selection to be put on the clipboard.  Nothing is     */
        /* put there here: the selection is captured and posted as a pane     */
        /* action (Main_RequestPaneAction, MainCommand.cs), admitted only     */
        /* when no other work runs, and the clipboard is written when it      */
        /* runs (Main_ExecutePaneAction).  A cut only MARKS the items as      */
        /* cut; nothing moves until they are pasted.                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bFromImage : true for the image pane's selection, false for    */
        /*                  the Windows pane's.                               */
        /*     bCut       : true for a cut, false for a copy.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the action is posted, or the status line says why not.  */
        /*--------------------------------------------------------------------*/
        private void Main_CopyToClipboard(bool bFromImage, bool bCut)
        {
            HPA_enmHostAction enmAction;
            List<string> lstPaths;
            ListViewItem lviRow;
            string sPath;
            string[] arrPaths;
            int i;

            if (!bFromImage)
            {   /* The Windows pane: its selected paths, the ".." row left   */
                /* out (Main_SelectedHostPaths), as a copy unless ...        */
                arrPaths = Main_SelectedHostPaths();
                enmAction = HPA_enmHostAction.CopySelection;
                if (bCut)
                {   /* ... a cut, which only marks them. */
                    enmAction = HPA_enmHostAction.CutSelection;
                }

                /* Posted with the folder they are in, which the action        */
                /* refuses for the drive list; the clipboard is written when   */
                /* it runs.                                                    */
                Main_RequestPaneAction(enmAction, arrPaths, Main_sHostDir);
                return;
            }

            /* The image pane: each selected entry's path, a folder's         */
            /* ending in "/" -- the mark a paste reads to know a folder       */
            /* (a cut's removal from the image deletes a folder differently   */
            /* from a file) and takes off again.                              */
            lstPaths = new List<string>();
            for (i = 0; i < Main_lstImage.SelectedItems.Count; i++)
            {   /* One selected row per iteration. */
                lviRow = Main_lstImage.SelectedItems[i];
                if (lviRow.Tag == null)
                {   /* The ".." row is not an item. */
                    continue;
                }

                /* Its path in the folder on show, and the folder mark. */
                sPath = Main_JoinImagePath(Main_sImageDir, lviRow.Text);
                if (((EEN_stEntry)lviRow.Tag).EEN_uiType == ENG_clsNative.ENG_TYPE_DIR)
                {   /* A directory (a link to one is not): marked. */
                    sPath = sPath + "/";
                }

                /* Onto the list. */
                lstPaths.Add(sPath);
            }

            /* Posted as the image copy or cut action.  The worker's gate       */
            /* refuses it unless the open it was taken from is still the        */
            /* open one, and refuses a cut while the image is read-only,        */
            /* since its paste will remove the sources (CommandSequences.cs).   */
            enmAction = HPA_enmHostAction.ImageCopySelection;
            if (bCut)
            {   /* A cut. */
                enmAction = HPA_enmHostAction.ImageCutSelection;
            }

            /* The Windows folder goes as the action's folder, which image   */
            /* items do not use; the clipboard is written, with the open's   */
            /* key beside the list, when the action runs.                    */
            Main_RequestPaneAction(enmAction, lstPaths.ToArray(), Main_sHostDir);
        }

        /*--------------------------------------------------------------------*/
        /* Main_ClipboardIsCut:                                               */
        /*                                                                    */
        /* Whether what is on the clipboard was CUT rather than copied, read  */
        /* from its "Preferred DropEffect" value.  A clipboard with no such   */
        /* value holds a plain copy.                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     datClipboard : the clipboard's data object.                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when a paste should move the items.                */
        /*--------------------------------------------------------------------*/
        private static bool Main_ClipboardIsCut(IDataObject datClipboard)
        {
            MemoryStream stmEffect;
            byte[]       abEffect;

            if (!datClipboard.GetDataPresent(MAIN_CLIP_EFFECT_FORMAT))
            {   /* No preference recorded: a copy. */
                return(false);
            }

            /* The value, which the shell keeps as a stream of bytes. */
            stmEffect = datClipboard.GetData(MAIN_CLIP_EFFECT_FORMAT) as MemoryStream;

            if (stmEffect == null || stmEffect.Length < 4)
            {   /* Not the four bytes expected: treat it as a copy, the harmless reading. */
                return(false);
            }

            /* Its first four bytes, read from the stream's start. */
            abEffect = new byte[4];
            stmEffect.Position = 0;
            stmEffect.Read(abEffect, 0, abEffect.Length);

            /* Move is the effect a cut records. */
            return((BitConverter.ToInt32(abEffect, 0) & (int)DragDropEffects.Move) != 0);
        }

        /*--------------------------------------------------------------------*/
        /* Main_PasteFromClipboard:                                           */
        /*                                                                    */
        /* Paste in a pane: put what the clipboard holds INTO that pane.      */
        /* The clipboard holds one of two things -- Windows files (from the   */
        /* Windows pane or from Explorer) or this program's own list of image */
        /* entries -- each either copied or cut, so there are eight cases:    */
        /*                                                                    */
        /*   Windows files  -> image pane   : import; if cut, the originals   */
        /*                                    then go to the Recycle Bin.     */
        /*   Windows files  -> Windows pane : the shell copies or moves them. */
        /*   image entries  -> Windows pane : extract; if cut, they are then  */
        /*                                    deleted from the image.         */
        /*   image entries  -> image pane   : cut moves them (a rename);      */
        /*                                    copy copies them inside the     */
        /*                                    image, metadata and all.        */
        /*                                                                    */
        /* Image entries on the clipboard belong to the open they were copied */
        /* from: a paste refuses them once that image has closed -- even if   */
        /* the same image is open again, or was only reopened for writing --  */
        /* since the paths would then name another lifetime's entries.  Two   */
        /* tests hold it: the key copied with them (CLP_SourceRange), checked */
        /* here, and the clipboard lifetime the worker checks.                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bIntoImage : true for the image pane, false for the Windows    */
        /*                  pane.                                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the paste is posted, or the status line says why not.   */
        /*--------------------------------------------------------------------*/
        private void Main_PasteFromClipboard(bool bIntoImage)
        {
            STV_clsStateView objSource;
            CPI_clsPasteInput objInput;
            string       sImageFolder;
            string       sHostFolder;
            string       sReason;
            string[]     asHost;
            string[]     asLines;
            string[]     asImagePaths;
            bool[]       abIsDir;
            bool         bCut;
            uint         uiClipboard;
            int          i;
            HPA_enmHostAction enmHostAction;

            if (Main_objCommandClipboard == null)
            {   /* The command system's clipboard reader is not made yet:   */
                /* refused with a reason -- nothing is pasted outside it.   */
                Main_SetStatus("The command clipboard endpoint is not initialized.");
                return;
            }

            /* The worker's state and BOTH panes' folders read FIRST: a        */
            /* clipboard read can let other window messages run before it      */
            /* returns (a call into the program that owns the data), and the   */
            /* paste must go where, and against what, it was made.             */
            objSource = null;
            if (Main_objCommandClient != null)
            {   /* The state record itself (it is never changed). */
                objSource = Main_objCommandClient.CLI_ReadState();
            }

            /* The two folders, in the same turn. */
            sImageFolder = Main_sImageDir;
            sHostFolder = Main_sHostDir;
            try
            {   /* The clipboard read into a record of plain values -- the   */
                /* Windows paths copied, or the image list and its key;      */
                /* whether it is a cut; the clipboard's sequence number --   */
                /* read again if the clipboard changed meanwhile, twice at   */
                /* most (CLP_ReadPaste, CommandClipboard.cs).                */
                objInput = Main_objCommandClipboard.CLP_ReadPaste(Clipboard.GetDataObject,
                    Main_ClipboardIsCut, MAIN_DRAG_IMAGE_FORMAT, MAIN_COMMAND_SOURCE_FORMAT, out sReason);
            }
            catch (System.Runtime.InteropServices.ExternalException exClipboard)
            {   /* The clipboard could not be read -- documented for one in   */
                /* use by another process: said, nothing pasted.              */
                Main_SetStatus("Unable to read the clipboard: " + exClipboard.Message);
                return;
            }

            if (objInput == null)
            {   /* Nothing usable on it, it kept changing, or a cut whose   */
                /* number could not be read: the reason said, nothing       */
                /* pasted.                                                  */
                Main_SetStatus(sReason);
                return;
            }

            /* A cut's sequence number goes with the request: the worker    */
            /* refuses a cut pasted a second time (its number already       */
            /* spent), and clears the clipboard after the move only if it   */
            /* still holds that number.  A copy carries 0.                  */
            bCut = objInput.CPI_bCut;
            uiClipboard = 0;
            if (bCut)
            {   /* A cut: its number (never 0 -- CLP_ReadPaste refuses that). */
                uiClipboard = objInput.CPI_uiSequence;
            }

            /* The Windows paths, if that is what the clipboard held (the   */
            /* reader's own copy of them).                                  */
            asHost = objInput.CPI_arrHostPaths;
            if (asHost != null)
            {   /* Windows files. */
                if (bIntoImage)
                {   /* Into the image: an import -- and for a cut, the     */
                    /* originals to the Recycle Bin afterwards -- as one   */
                    /* command (Main_ImportPaths).                         */
                    Main_ImportPaths(asHost, bCut, objSource, uiClipboard, sImageFolder);
                }
                else
                {   /* Into the Windows pane's folder: the shell copies or   */
                    /* moves them, as a pane action run when the worker      */
                    /* admits it (Main_HostPaste) -- a copy unless ...       */
                    enmHostAction = HPA_enmHostAction.PasteCopy;
                    if (bCut)
                    {   /* ... a cut: a move. */
                        enmHostAction = HPA_enmHostAction.PasteMove;
                    }

                    /* Posted with the folder read above, not whatever the   */
                    /* pane shows by the time it runs.                       */
                    Main_RequestPaneAction(enmHostAction, asHost, sHostFolder);
                }

                return;
            }

            /* Image items: the image's path, then one entry per line.      */
            /* They are used only when they belong to the open image NOW:   */
            /* the first line names it, and the key copied with them        */
            /* matches the open's own (CLP_SourceRange: this run, the       */
            /* image, its range and lifetime).  A list without a key, or    */
            /* with another's, is refused -- one path alone cannot show     */
            /* which partition, or which lifetime, the entries were in.     */
            asLines = objInput.CPI_sImageList.Split('\n');
            if (objSource == null || objSource.STV_objTarget == null || asLines.Length < 2 ||
                !string.Equals(asLines[0], objSource.STV_objTarget.TGT_sPath, StringComparison.OrdinalIgnoreCase) ||
                String.IsNullOrEmpty(objInput.CPI_sRange) || objInput.CPI_sRange != Main_objCommandClipboard.CLP_SourceRange(objSource))
            {   /* Not this open's items, or nothing open: refused, with the   */
                /* way out.                                                    */
                Main_SetStatus("Those image items are no longer valid for the open filesystem. Copy them again.");
                return;
            }

            /* Everything after the first line is an entry path; a trailing   */
            /* slash marks a directory and is not part of the path.           */
            asImagePaths = new string[asLines.Length - 1];
            abIsDir      = new bool[asLines.Length - 1];

            for (i = 0; i < asImagePaths.Length; i++)
            {   /* One entry line per iteration: the path, and whether it is   */
                /* marked as a directory.                                      */
                asImagePaths[i] = asLines[i + 1];
                abIsDir[i]      = asImagePaths[i].EndsWith("/");

                if (abIsDir[i])
                {   /* Take the directory mark off. */
                    asImagePaths[i] = asImagePaths[i].Substring(0, asImagePaths[i].Length - 1);
                }
            }

            if (!bIntoImage)
            {   /* Into Windows: extract them (and, if cut, delete them from the image after). */
                Main_ExtractPaths(asImagePaths, abIsDir, bCut, objSource, uiClipboard, sHostFolder, true);
                return;
            }

            if (!bCut)
            {   /* Image entries copied and pasted back into the image: a copy inside it. */
                Main_ImageCopy(asImagePaths, objSource, sImageFolder, true);
                return;
            }

            /* Cut, and pasted elsewhere in the same image: move them. */
            Main_ImageMove(asImagePaths, objSource, uiClipboard, sImageFolder, true);
        }

        /*--------------------------------------------------------------------*/
        /* Main_PaneKeyDown:                                                  */
        /*                                                                    */
        /* A key was pressed in one of the two lists: Backspace or Alt+Up     */
        /* goes up one level in that pane, Alt+Left goes back to the folder   */
        /* it showed before, Delete deletes its selection, Ctrl+X cuts and    */
        /* Ctrl+C copies that pane's selection, Ctrl+V pastes into that pane, */
        /* Ctrl+A selects everything in it but the ".." row.  These keys are  */
        /* handled HERE rather than as menu shortcuts on purpose: a menu      */
        /* shortcut is taken before the focused control sees the key, which   */
        /* would stop Ctrl+C, Ctrl+V and Delete working on the text in the    */
        /* path box.                                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstPane : the list that has the focus, named by its handler    */
        /*               (Main_OnImageKeyDown, Main_OnHostKeyDown).           */
        /*     evKey   : the key event.                                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the clipboard action ran; other keys pass through.      */
        /*--------------------------------------------------------------------*/
        private void Main_PaneKeyDown(ListView lstPane, KeyEventArgs evKey)
        {
            bool bImagePane;

            /* Which pane's list the key went to: every command below acts   */
            /* on THAT pane.                                                 */
            bImagePane = (lstPane == Main_lstImage);

            if (evKey.KeyCode == Keys.Back && !evKey.Control && !evKey.Alt && !evKey.Shift)
            {   /* Backspace: up one level in this pane. */
                evKey.Handled = true;
                Main_GoUp(bImagePane);
                return;
            }

            if (evKey.Alt && !evKey.Control && !evKey.Shift && evKey.KeyCode == Keys.Up)
            {   /* Alt+Up: up one level in this pane, as in Explorer. */
                evKey.Handled = true;
                Main_GoUp(bImagePane);
                return;
            }

            if (evKey.Alt && !evKey.Control && !evKey.Shift && evKey.KeyCode == Keys.Left)
            {   /* Alt+Left: back to the folder this pane showed before, as in Explorer. */
                evKey.Handled = true;
                Main_GoBack(bImagePane);
                return;
            }

            if (evKey.KeyCode == Keys.Delete && !evKey.Control && !evKey.Alt && !evKey.Shift)
            {   /* Delete: the same request the Delete handlers make            */
                /* (Main_RequestDelete), in this pane -- the focus being here   */
                /* has already made it the active one, which that function      */
                /* reads.  Called directly rather than through a handler, so    */
                /* a handler runs only for its own control's event.             */
                evKey.Handled = true;
                Main_RequestDelete();
                return;
            }

            if (!evKey.Control || evKey.Alt || evKey.Shift)
            {   /* Not a plain Ctrl+key. */
                return;
            }

            if (evKey.KeyCode == Keys.X)
            {   /* Cut this pane's selection. */
                evKey.Handled = true;
                Main_CopyToClipboard(bImagePane, true);
            }
            else if (evKey.KeyCode == Keys.C)
            {   /* Copy this pane's selection. */
                evKey.Handled = true;
                Main_CopyToClipboard(bImagePane, false);
            }
            else if (evKey.KeyCode == Keys.V)
            {   /* Paste into this pane. */
                evKey.Handled = true;
                Main_PasteFromClipboard(bImagePane);
            }
            else if (evKey.KeyCode == Keys.A)
            {   /* Select everything in this pane, as Explorer does. */
                evKey.Handled = true;
                Main_SelectAll(lstPane);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnImageKeyDown:                                               */
        /*                                                                    */
        /* A key was pressed in the image pane's list (Main_PaneKeyDown says  */
        /* which keys do what).                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the image pane's list (unused).                    */
        /*     evKey     : the key event.                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the key's command ran; other keys pass through.         */
        /*--------------------------------------------------------------------*/
        private void Main_OnImageKeyDown(object objSender, KeyEventArgs evKey)
        {
            /* The image pane's list, named. */
            Main_PaneKeyDown(Main_lstImage, evKey);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnHostKeyDown:                                                */
        /*                                                                    */
        /* The same for the Windows pane's list.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the Windows pane's list (unused).                  */
        /*     evKey     : the key event.                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the key's command ran; other keys pass through.         */
        /*--------------------------------------------------------------------*/
        private void Main_OnHostKeyDown(object objSender, KeyEventArgs evKey)
        {
            /* The Windows pane's list, named. */
            Main_PaneKeyDown(Main_lstHost, evKey);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnSelectAllClicked:                                           */
        /*                                                                    */
        /* Edit > Select All, on the pane last used: every real entry of it   */
        /* selected, but not the ".." row (Main_SelectAll, which Ctrl+A in a  */
        /* pane calls directly).                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item.                                     */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : every real entry of that pane is selected.              */
        /*--------------------------------------------------------------------*/
        private void Main_OnSelectAllClicked(object objSender, EventArgs evArgs)
        {
            if (Main_bHostPaneActive)
            {   /* The Windows pane was the last one used. */
                Main_SelectAll(Main_lstHost);
            }
            else
            {   /* The image pane was. */
                Main_SelectAll(Main_lstImage);
            }
        }

        /*---------------------------------------------------------------------*/
        /* Main_SelectAll:                                                     */
        /*                                                                     */
        /* Select every real entry of a pane -- the work behind Ctrl+A and     */
        /* Edit > Select All (the heading's select-all box is answered by      */
        /* the list control itself; Main_HeaderBoxClicked).  The ".." row is a */
        /* way up, not an entry, so it is left unselected (a copy or delete    */
        /* of "everything" must never take in the parent folder).  Redrawing   */
        /* is held off while the rows change, so a large folder does not       */
        /* repaint row by row; the selection events that follow are            */
        /* collapsed by their own posted handlers.                             */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     lstPane : the pane's list.                                      */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : every real entry is selected.                            */
        /*---------------------------------------------------------------------*/
        private void Main_SelectAll(ListView lstPane)
        {
            int i;

            /* No repainting until every row is set. */
            lstPane.BeginUpdate();

            for (i = 0; i < lstPane.Items.Count; i++)
            {   /* One row per iteration: every real entry selected, the way up not. */
                lstPane.Items[i].Selected = (lstPane.Items[i].Tag != null && lstPane.Items[i].Text != MAIN_PARENT_NAME);
            }

            /* Repainted once, now that every row is set. */
            lstPane.EndUpdate();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnImageHeaderBoxClicked:                                      */
        /*                                                                    */
        /* The image pane's list reports a click on its Name heading's        */
        /* select-all box (its PLV_pfnHeaderBoxClicked; Main_HeaderBoxClicked */
        /* says what follows).                                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstSource : the reporting list (unused: this is the image      */
        /*                 pane's callback, which names its list).            */
        /*     iColumn   : the heading clicked (only the first has a box).    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the image pane has the focus and ".." is not selected.  */
        /*--------------------------------------------------------------------*/
        private void Main_OnImageHeaderBoxClicked(ListView lstSource, int iColumn)
        {
            /* The image pane's list, named. */
            Main_HeaderBoxClicked(Main_lstImage);
        }

        /*---------------------------------------------------------------------*/
        /* Main_OnHostHeaderBoxClicked:                                        */
        /*                                                                     */
        /* The same for the Windows pane's list.                               */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     lstSource : the reporting list (unused: this is the Windows     */
        /*                 pane's callback, which names its list).             */
        /*     iColumn   : the heading clicked (only the first has a box).     */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : the Windows pane has the focus and ".." is not selected. */
        /*---------------------------------------------------------------------*/
        private void Main_OnHostHeaderBoxClicked(ListView lstSource, int iColumn)
        {
            /* The Windows pane's list, named. */
            Main_HeaderBoxClicked(Main_lstHost);
        }

        /*--------------------------------------------------------------------*/
        /* Main_HeaderBoxClicked:                                             */
        /*                                                                    */
        /* The select-all box in a pane's Name heading was clicked (it is     */
        /* there only while item check boxes are on; PLV_PaneListView posts   */
        /* the click).  THE LIST CONTROL ITSELF ANSWERS THE CLICK: it ticks   */
        /* every row when the heading's box is clear and unticks every row    */
        /* when it is ticked, and the ticks pull the selection along          */
        /* (Main_PaneItemChecked).  So this handler must NOT select all or    */
        /* none as well -- the two toggles would cancel, and the click would  */
        /* seem to do nothing.  It does only what the control does not, or    */
        /* does wrongly.  The heading's box is ticked or cleared by           */
        /* Main_ApplyItemChecks from the selection.                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstSource : the pane's list, named by its pane's callback      */
        /*                 (Main_OnImageHeaderBoxClicked,                     */
        /*                 Main_OnHostHeaderBoxClicked).                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the pane has the focus (Main_PaneEntered makes it the   */
        /*            active one) and ".." is not selected.                   */
        /*--------------------------------------------------------------------*/
        private void Main_HeaderBoxClicked(ListView lstSource)
        {
            int i;

            /* The pane takes the focus, so Edit's commands apply to it, as   */
            /* a click in its rows would make them.                           */
            lstSource.Focus();

            /* The ".." row taken back OUT of the selection: the control's   */
            /* select-all takes it in although its box stays filled (as      */
            /* found), and Ctrl+A leaves it out.  The control has done its   */
            /* part by the time this posted handler runs.                    */
            for (i = 0; i < lstSource.Items.Count; i++)
            {   /* One row per iteration: the way up (no Tag) never stays selected by a select-all. */
                if (lstSource.Items[i].Tag == null && lstSource.Items[i].Selected)
                {   /* Taken in by the control's select-all: out again. */
                    lstSource.Items[i].Selected = false;
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnItemChecksChanged:                                          */
        /*                                                                    */
        /* View > Item Check Boxes was ticked or unticked: remember it and    */
        /* show or hide the boxes in both panes.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item.                                     */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the setting is stored and the panes follow it.          */
        /*--------------------------------------------------------------------*/
        private void Main_OnItemChecksChanged(object objSender, EventArgs evArgs)
        {
            int iValue;

            /* The registry's 1 / 0 for the tick: 0 unless ... */
            iValue = 0;

            if (Main_tsmItemChecks.Checked)
            {   /* ... ticked: boxes on. */
                iValue = 1;
            }

            /* Remembered for the next run, then both panes made to match. */
            SET_Settings.SET_WriteInt(SET_Settings.SET_ITEM_CHECKS, iValue);
            Main_ApplyItemChecks(Main_lstImage);
            Main_ApplyItemChecks(Main_lstHost);
        }

        /*--------------------------------------------------------------------*/
        /* Main_ApplyItemChecks:                                              */
        /*                                                                    */
        /* Give a pane its check boxes, or take them away, and make every     */
        /* tick match the selection -- the tick and the selection are one and */
        /* the same, as in Explorer.  Called at start-up, when the option     */
        /* changes, after a change of view, and -- posted, through            */
        /* Main_PostItemChecks -- after a change of selection or a refill of  */
        /* the rows.                                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstPane : the pane's list.                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the pane's boxes follow the option and its selection.   */
        /*--------------------------------------------------------------------*/
        private void Main_ApplyItemChecks(ListView lstPane)
        {
            PLV_PaneListView plvPane;
            bool             bBoxes;
            int              iItems;
            int              iSelected;
            int              iImage;
            int              i;

            /* Boxes when the option is on -- but never in Tile view,      */
            /* where WinForms refuses check boxes (it throws, as found),   */
            /* so a pane in Tile shows none whatever the option says.      */
            bBoxes = (Main_tsmItemChecks.Checked && lstPane.View != View.Tile);

            /* The pane's own list type, for the filled box, the ".." row's    */
            /* box and the heading's box (null for a plain list view -- none   */
            /* today).                                                         */
            plvPane = lstPane as PLV_PaneListView;

            if (lstPane.CheckBoxes != bBoxes)
            {   /* The boxes come or go.  Each one widens a List / Small    */
                /* Icons cell (PLV_ArrangeGrid allows for it), and the      */
                /* framework may create the list's window afresh for the    */
                /* switch (not verified either way), which would drop the   */
                /* rows' places -- so the grid is laid out again, now and   */
                /* once the switch has settled.                             */
                lstPane.CheckBoxes = bBoxes;

                if (lstPane.IsHandleCreated)
                {   /* A window to lay out (at start-up the first layout comes later). */
                    Main_GridChanged(lstPane, false);
                    Main_SizeColumns(lstPane);

                    if ((lstPane.View == View.SmallIcon || lstPane.View == View.List) && lstPane is PLV_PaneListView)
                    {   /* A grid view: once more after the control settles. */
                        ((PLV_PaneListView)lstPane).PLV_ArrangeGridLater();
                    }
                }
            }

            if (!lstPane.CheckBoxes)
            {   /* No boxes: nothing to keep in step, and no select-all box   */
                /* in the heading either.                                     */
                if (plvPane != null)
                {   /* One of the panes: its heading box goes. */
                    plvPane.PLV_SetHeaderCheckBox(false, false);
                }

                return;
            }

            if (plvPane != null)
            {   /* The control's box pictures gain the solid-filled third   */
                /* for the ".." row (once per fresh list; a no-op after).   */
                plvPane.PLV_AddFilledBox();
            }

            /* Setting ticks raises the tick handler, which would set the    */
            /* selection back: the flag keeps the two handlers from          */
            /* answering each other until it is lowered below.  The counts   */
            /* for the heading's box start at none.                          */
            Main_bSyncingChecks = true;
            iItems              = 0;
            iSelected           = 0;

            for (i = 0; i < lstPane.Items.Count; i++)
            {   /* One row per iteration: ticked exactly when selected. */
                if (lstPane.Items[i].Tag == null && plvPane != null)
                {   /* The ".." row (no Tag): a way up, not an item, so it   */
                    /* shows the solid-filled box that cannot be ticked      */
                    /* (PLV_MarkUnavailable) -- set on every pass, since a   */
                    /* refill adds the row with an empty one.                */
                    plvPane.PLV_MarkUnavailable(i);
                    continue;
                }

                /* The tick written as the row's STATE IMAGE, not through    */
                /* Checked: a row added by a refill has no state image at    */
                /* all, and setting Checked to false on it changes nothing   */
                /* (as found), so an unselected new row would show no box    */
                /* until the next refresh.  Empty box 0, ticked box 1.       */
                iImage = 0;

                if (lstPane.Items[i].Selected)
                {   /* Selected: ticked. */
                    iImage = 1;
                }

                if (lstPane.Items[i].StateImageIndex != iImage)
                {   /* Not already that picture (or none at all): set it. */
                    lstPane.Items[i].StateImageIndex = iImage;
                }

                /* Counted for the heading's box: items, and how many are selected. */
                iItems++;

                if (lstPane.Items[i].Selected)
                {   /* This one is selected. */
                    iSelected++;
                }
            }

            /* Every tick set: the handlers answer each other again. */
            Main_bSyncingChecks = false;

            /* The select-all box in the Name heading (seen in Details), as   */
            /* in Explorer: ticked when every item is selected and there is   */
            /* at least one.                                                  */
            if (plvPane != null)
            {   /* One of the panes. */
                plvPane.PLV_SetHeaderCheckBox(true, iItems > 0 && iSelected == iItems);
            }
        }

        /*----------------------------------------------------------------------*/
        /* Main_PostItemChecks:                                                 */
        /*                                                                      */
        /* Bring a pane's ticks into line with its selection once the           */
        /* current burst of messages is over.  A selection change arrives       */
        /* once PER ROW -- Ctrl+A over a folder of N rows is N events -- and    */
        /* setting every tick on each of them would make selecting all cost N   */
        /* times N tick changes; posted, with one waiting per pane, it costs    */
        /* one pass after the last.  Also called after a refill of the rows, so */
        /* the new rows' ticks are set whether or not clearing the old ones     */
        /* raised a selection change.  Does nothing while the pane has no       */
        /* boxes, or while a tick handler is moving the selection itself (its   */
        /* tick is already right).                                              */
        /*                                                                      */
        /* Arguments:                                                           */
        /*     lstPane : the pane's list.                                       */
        /*                                                                      */
        /* Returns:                                                             */
        /*     void : a tick sync for the pane is waiting, or none is needed.   */
        /*----------------------------------------------------------------------*/
        private void Main_PostItemChecks(ListView lstPane)
        {
            int iSide;

            if (!lstPane.CheckBoxes || Main_bSyncingChecks || !this.IsHandleCreated)
            {   /* No boxes, our own change, or no window to post through. */
                return;
            }

            /* Which pane: one sync may wait for each. */
            iSide = MAIN_SIDE_IMAGE;

            if (lstPane == Main_lstHost)
            {   /* The Windows pane. */
                iSide = MAIN_SIDE_HOST;
            }

            if (Main_abChecksPending[iSide])
            {   /* One is already waiting; it will see this change too. */
                return;
            }

            /* One sync now waiting for this pane ... */
            Main_abChecksPending[iSide] = true;

            /* ... posted behind the rest of the burst's events, to the   */
            /* pane binding's named method (MPB_RunItemChecks).           */
            this.BeginInvoke(new MethodInvoker(new Mpb_clsPaneBinding(this, iSide).MPB_RunItemChecks));
        }

        /*---------------------------------------------------------------------*/
        /* Main_OnImageItemChecked:                                            */
        /*                                                                     */
        /* A row's tick changed in the image pane's list (Main_PaneItemChecked */
        /* says what follows).                                                 */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     objSender : the image pane's list (unused).                     */
        /*     evChecked : the row.                                            */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : the row's selection matches its tick.                    */
        /*---------------------------------------------------------------------*/
        private void Main_OnImageItemChecked(object objSender, ItemCheckedEventArgs evChecked)
        {
            /* The row's selection made to follow its tick. */
            Main_PaneItemChecked(evChecked);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnHostItemChecked:                                            */
        /*                                                                    */
        /* The same for the Windows pane's list.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the Windows pane's list (unused).                  */
        /*     evChecked : the row.                                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the row's selection matches its tick.                   */
        /*--------------------------------------------------------------------*/
        private void Main_OnHostItemChecked(object objSender, ItemCheckedEventArgs evChecked)
        {
            /* The row's selection made to follow its tick. */
            Main_PaneItemChecked(evChecked);
        }

        /*--------------------------------------------------------------------*/
        /* Main_PaneItemChecked:                                              */
        /*                                                                    */
        /* A row's tick changed by the user: its selection follows, so a      */
        /* click on a box adds the row to the selection (or takes it out)     */
        /* without disturbing the others, as in Explorer.  Ignored while      */
        /* Main_ApplyItemChecks is setting the ticks from the selection, and  */
        /* for the ".." row, whose filled box the framework counts as         */
        /* "checked" but which is never an item to select (the list itself    */
        /* refuses any click on that box -- PLV_PaneListView's WndProc).      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     evChecked : the row, from either list's ItemChecked event.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the row's selection matches its tick.                   */
        /*--------------------------------------------------------------------*/
        private void Main_PaneItemChecked(ItemCheckedEventArgs evChecked)
        {
            if (Main_bSyncingChecks)
            {   /* Our own change, from the selection: nothing to answer. */
                return;
            }

            if (evChecked.Item.Tag == null)
            {   /* The way up: its box is a marker, not a selection. */
                return;
            }

            /* The row's selection set to its tick, under the flag, so the   */
            /* selection change this raises does not post a tick sync back   */
            /* (its tick is already right).                                  */
            Main_bSyncingChecks = true;
            evChecked.Item.Selected = evChecked.Item.Checked;
            Main_bSyncingChecks = false;
        }


        /*--------------------------------------------------------------------*/
        /* Main_ValidImageName:                                               */
        /*                                                                    */
        /* Whether a typed name can be an entry name in the image: never an   */
        /* empty name, "." or "..", a slash or a NUL, and never longer than   */
        /* the filesystem holds -- 255 BYTES of UTF-8 on ext2/3/4 (bytes,     */
        /* not characters, which is why the length is measured after          */
        /* encoding), 255 UTF-16 units on FAT and exFAT, as Windows allows on */
        /* a FAT volume.  FAT's own characters are the caller's to test       */
        /* (SYS_FatNameProblem).                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sName : the name as typed.                                     */
        /*     bFat  : true when the open filesystem is FAT or exFAT, so the  */
        /*             length is FAT's rather than ext's.                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the image can hold an entry of that name.     */
        /*--------------------------------------------------------------------*/
        private static bool Main_ValidImageName(string sName, bool bFat)
        {
            if (sName.Length == 0 || sName == "." || sName == "..")
            {   /* Empty, or one of the two names every directory reserves. */
                return(false);
            }

            if (sName.IndexOf('/') >= 0 || sName.IndexOf('\0') >= 0)
            {   /* A path separator or a terminator cannot be part of a name. */
                return(false);
            }

            if (!bFat && System.Text.Encoding.UTF8.GetByteCount(sName) > 255)
            {   /* ext2/3/4: longer than its directory entry can record. */
                return(false);
            }

            if (bFat && sName.Length > 255)
            {   /* FAT or exFAT: longer than a long name holds (255 UTF-16   */
                /* units, the string's own length); ext's byte limit does    */
                /* not bind here.                                            */
                return(false);
            }

            /* A name the filesystem accepts, as far as these rules go. */
            return(true);
        }


        /*--------------------------------------------------------------------*/
        /* Main_OnImageListEntered:                                           */
        /*                                                                    */
        /* The focus moved into the image pane's list (Main_PaneEntered).     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the list (unused).                                 */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the image pane is the active one.                       */
        /*--------------------------------------------------------------------*/
        private void Main_OnImageListEntered(object objSender, EventArgs evArgs)
        {
            /* The image pane. */
            Main_PaneEntered(false);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnImagePathEntered:                                           */
        /*                                                                    */
        /* The focus moved into the image pane's path box (Main_PaneEntered). */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the path box (unused).                             */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the image pane is the active one.                       */
        /*--------------------------------------------------------------------*/
        private void Main_OnImagePathEntered(object objSender, EventArgs evArgs)
        {
            /* The image pane. */
            Main_PaneEntered(false);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnHostListEntered:                                            */
        /*                                                                    */
        /* The focus moved into the Windows pane's list (Main_PaneEntered).   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the list (unused).                                 */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the Windows pane is the active one.                     */
        /*--------------------------------------------------------------------*/
        private void Main_OnHostListEntered(object objSender, EventArgs evArgs)
        {
            /* The Windows pane. */
            Main_PaneEntered(true);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnHostPathEntered:                                            */
        /*                                                                    */
        /* The focus moved into the Windows pane's path box                   */
        /* (Main_PaneEntered).                                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the path box (unused).                             */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the Windows pane is the active one.                     */
        /*--------------------------------------------------------------------*/
        private void Main_OnHostPathEntered(object objSender, EventArgs evArgs)
        {
            /* The Windows pane. */
            Main_PaneEntered(true);
        }

        /*--------------------------------------------------------------------*/
        /* Main_PaneEntered:                                                  */
        /*                                                                    */
        /* The focus moved into one of the two panes (its list or its path    */
        /* box): remember which.  New Folder, Rename, Delete and the View     */
        /* menu's view items act on the pane the user was last in, and a menu */
        /* click does not change that.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bHostPane : true for the Windows pane, false for the image     */
        /*                 pane -- named by the control's own handler.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Main_bHostPaneActive says which pane is the active one. */
        /*--------------------------------------------------------------------*/
        private void Main_PaneEntered(bool bHostPane)
        {
            /* The pane the focus moved into. */
            Main_bHostPaneActive = bHostPane;

            /* Show which pane the menus and keys now act on; the menus'   */
            /* states follow it too.                                       */
            Main_MarkActivePane();
            Main_UpdateViewMenu();
            Main_UpdateToolbar();
        }

        /*--------------------------------------------------------------------*/
        /* Main_ValidHostName:                                                */
        /*                                                                    */
        /* Whether a typed name can be a file or folder name on Windows: not  */
        /* empty, none of the characters Windows forbids in a name, and no    */
        /* trailing dot or space (Windows silently drops those, so the item   */
        /* would not get the name that was typed).  A reserved device name    */
        /* ("con", "nul") is left for Windows itself to refuse.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sName : the name as typed.                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when Windows can hold an item of that name.        */
        /*--------------------------------------------------------------------*/
        private static bool Main_ValidHostName(string sName)
        {
            if (sName.Length == 0 || sName == "." || sName == "..")
            {   /* Empty, or one of the two names that mean a folder itself and its parent. */
                return(false);
            }

            if (sName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {   /* A separator, a colon, a wildcard, a quote or a control character. */
                return(false);
            }

            if (sName.EndsWith(".") || sName.EndsWith(" "))
            {   /* Windows would strip it and create a different name. */
                return(false);
            }

            /* A name Windows accepts. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* Main_ShowHostError:                                                */
        /*                                                                    */
        /* Tell the user a Windows-side edit failed, and log it.              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sAction : what was being done ("Creating " and the path).      */
        /*     exFail  : the exception Windows raised.                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : a message box was shown and dismissed.                  */
        /*--------------------------------------------------------------------*/
        private void Main_ShowHostError(string sAction, Exception exFail)
        {
            /* Into the debug log, then shown as a warning box with the   */
            /* framework's own reason.                                    */
            if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
            {   /* Only while the debug log is kept: otherwise the line is never built. */
                DBGLOG_clsDebugLog.DBGLOG_Write("host", sAction + " failed: " + exFail.Message);
            }

            /* Then the warning box, with the framework's own reason. */
            MSG_clsMessageForm.MSG_Show(this, Main_fntDialog, sAction + " failed.\n\n" + exFail.Message, "BifrostFM", MessageBoxButtons.OK, MessageBoxIcon.Warning,
                                     MessageBoxDefaultButton.Button1);
        }

        /*--------------------------------------------------------------------*/
        /* Main_HostNewFolder:                                                */
        /*                                                                    */
        /* New Folder, in the Windows pane: ask for a name and create that    */
        /* folder in the Windows folder the request named.  It runs inside    */
        /* its pane-action input (Main_ExecutePaneAction, MainCommand.cs) on  */
        /* the UI thread, so the worker starts nothing until it returns --    */
        /* the name question included.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sFolder : the folder the request named ("" for the drive       */
        /*               list).                                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the folder exists and the pane is re-read, or the       */
        /*            status bar or a message says why not.                   */
        /*--------------------------------------------------------------------*/
        private void Main_HostNewFolder(string sFolder)
        {
            PRM_clsPromptForm frmPrompt;
            string sName;
            string sNewDir;

            if (sFolder.Length == 0)
            {   /* The drive list: there is no folder to create one in. */
                Main_SetStatus("Go into a Windows folder first.");
                return;
            }

            using (frmPrompt = new PRM_clsPromptForm(Main_fntDialog, "New folder in Windows", "Name of the new folder:", "New folder"))
            {   /* Ask for the name, under the program's icon. */
                frmPrompt.Icon = this.Icon;
                frmPrompt.ShowDialog(this);

                if (!frmPrompt.PRM_bAccepted)
                {   /* Cancelled. */
                    return;
                }

                /* The name typed, without spaces at either end. */
                sName = frmPrompt.PRM_sText.Trim();
            }

            if (!Main_ValidHostName(sName))
            {   /* Not a name Windows can hold. */
                Main_SetStatus("That is not a valid Windows name.");
                return;
            }

            /* The new folder's full path. */
            sNewDir = Path.Combine(sFolder, sName);

            if (Directory.Exists(sNewDir) || File.Exists(sNewDir))
            {   /* CreateDirectory would quietly succeed on an existing folder; say so instead. */
                Main_SetStatus(sName + " already exists.");
                return;
            }

            try
            {   /* The folder made; Windows can refuse it (no permission,   */
                /* a read-only disc, a reserved name), which the catch      */
                /* reports.                                                 */
                Directory.CreateDirectory(sNewDir);
            }
            catch (Exception exCreate)
            {   /* Windows refused. */
                Main_ShowHostError("Creating " + sNewDir, exCreate);
            }

            /* The folder re-read either way. */
            Main_NavigateHost(sFolder);
        }

        /*--------------------------------------------------------------------*/
        /* Main_HostRename:                                                   */
        /*                                                                    */
        /* Rename, in the Windows pane: give the ONE selected file or folder  */
        /* a new name in the same folder -- a change of letter case only      */
        /* ("readme" to "README") included, which Windows otherwise treats    */
        /* as no change at all.                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sOldPath : the item the request named -- the selection when    */
        /*                Rename was asked for, not the selection now.        */
        /*     sFolder  : the folder it is in.                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item is renamed and the pane re-read, or the status */
        /*            bar or a message says why not.                          */
        /*--------------------------------------------------------------------*/
        private void Main_HostRename(string sOldPath, string sFolder)
        {
            PRM_clsPromptForm frmPrompt;
            string   sOldName;
            string   sNewName;
            string   sNewPath;
            string   sTempPath;
            bool     bCaseOnly;

            if (String.IsNullOrEmpty(sOldPath) || sFolder.Length == 0)
            {   /* Rename acts on exactly one real item, and a drive is not renamed here. */
                Main_SetStatus("Select one item in the Windows pane to rename.");
                return;
            }

            /* The one item, and its present name. */
            sOldName = Path.GetFileName(sOldPath);

            using (frmPrompt = new PRM_clsPromptForm(Main_fntDialog, "Rename in Windows", "New name for " + sOldName + ":", sOldName))
            {   /* Ask for the new name, starting from the old one, under the program's icon. */
                frmPrompt.Icon = this.Icon;
                frmPrompt.ShowDialog(this);

                if (!frmPrompt.PRM_bAccepted)
                {   /* Cancelled. */
                    return;
                }

                /* The name typed, without spaces at either end. */
                sNewName = frmPrompt.PRM_sText.Trim();
            }

            if (sNewName == sOldName)
            {   /* Nothing to change. */
                return;
            }

            if (!Main_ValidHostName(sNewName))
            {   /* Not a name Windows can hold. */
                Main_SetStatus("That is not a valid Windows name.");
                return;
            }

            /* The new path, and whether the change is one of letter case   */
            /* only: Windows treats those two names as the same item, so    */
            /* the "already exists" test below must be skipped for it.      */
            sNewPath  = Path.Combine(sFolder, sNewName);
            bCaseOnly = string.Equals(sOldName, sNewName, StringComparison.OrdinalIgnoreCase);

            if (!bCaseOnly && (Directory.Exists(sNewPath) || File.Exists(sNewPath)))
            {   /* Another item already has that name. */
                Main_SetStatus(sNewName + " already exists.");
                return;
            }

            try
            {   /* The rename; Windows can refuse it (in use, no            */
                /* permission, a reserved name), which the catch reports.   */
                if (Directory.Exists(sOldPath))
                {   /* A folder. */
                    if (bCaseOnly)
                    {   /* Case only: a folder cannot be moved onto a name     */
                        /* that differs only in case (refused, as found), so   */
                        /* it goes through a name nothing else can have.       */
                        /* The first move failing leaves it as it was (the     */
                        /* catch below reports).                               */
                        sTempPath = Path.Combine(sFolder, Guid.NewGuid().ToString("N"));
                        Directory.Move(sOldPath, sTempPath);
                        try
                        {   /* The second move, to the new spelling. */
                            Directory.Move(sTempPath, sNewPath);
                        }
                        catch (Exception exSecond)
                        {   /* Refused part-way (the folder came into use, a      */
                            /* permission changed): it is moved BACK to its old   */
                            /* name and the refusal reported, so it is never      */
                            /* left under the random name unexplained.            */
                            try
                            {   /* Back under its old name: the rename did not   */
                                /* happen, said as an ordinary refusal.          */
                                Directory.Move(sTempPath, sOldPath);
                                Main_ShowHostError("Renaming " + sOldPath, exSecond);
                            }
                            catch (Exception exBack)
                            {   /* It cannot go back either: the message names   */
                                /* where the folder IS now, so it can be found   */
                                /* and renamed by hand; nothing is deleted.      */
                                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                                {   /* Only while the debug log is kept: otherwise the line is never built. */
                                    DBGLOG_clsDebugLog.DBGLOG_Write("host rename", "second move: " + exSecond.ToString());
                                }

                                /* The error shown, naming where the folder is now. */
                                Main_ShowHostError("Renaming " + sOldPath + " stopped part-way; the folder is now " +
                                                   sTempPath + ", and moving it back", exBack);
                            }
                        }
                    }
                    else
                    {   /* An ordinary folder rename. */
                        Directory.Move(sOldPath, sNewPath);
                    }
                }
                else
                {   /* A file; a case-only change needs nothing special. */
                    File.Move(sOldPath, sNewPath);
                }
            }
            catch (Exception exRename)
            {   /* Windows refused. */
                Main_ShowHostError("Renaming " + sOldPath, exRename);
            }

            /* The folder re-read either way. */
            Main_NavigateHost(sFolder);
        }

        /*--------------------------------------------------------------------*/
        /* Main_FailureReportTail:                                            */
        /*                                                                    */
        /* The end of the e2fsck / resize2fs report a failed operation's      */
        /* result carries, for its failure box (MainCommand.cs): an Expand    */
        /* whose check or repair could not make the filesystem sound, or a    */
        /* check that failed, says what it found, not only the status.  Only  */
        /* the last lines are given, so a long report cannot grow the box     */
        /* past the screen; how many is the local constant below.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objPayload : the operation's result payload: a resize's        */
        /*                  record, a check's, or the repair-needed record    */
        /*                  holding a check's; anything else has no report.   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : "\n\n" and a heading and the lines, or "" when there  */
        /*              is no report.                                         */
        /*--------------------------------------------------------------------*/
        private string Main_FailureReportTail(object objPayload)
        {
            const int iTailLines = 15;   // the most report lines the failure box shows
            List<string> lstLines;
            RPN_clsRepairNeeded objRepair;
            System.Text.StringBuilder sbTail;
            int iFirst;
            int i;

            /* The report's lines, from whichever record carries them. */
            lstLines = null;
            objRepair = objPayload as RPN_clsRepairNeeded;
            if (objPayload is ERS_clsResizeResult)
            {   /* An Expand's resize record: its check's and resize's lines. */
                lstLines = ((ERS_clsResizeResult)objPayload).ERS_lstLines;
            }
            else if (objPayload is ECK_clsCheckResult)
            {   /* A check's own record. */
                lstLines = ((ECK_clsCheckResult)objPayload).ECK_lstLines;
            }
            else if (objRepair != null && objRepair.RPN_objReport is ECK_clsCheckResult)
            {   /* The repair-needed record: the check's report inside it. */
                lstLines = ((ECK_clsCheckResult)objRepair.RPN_objReport).ECK_lstLines;
            }

            if (lstLines == null || lstLines.Count == 0)
            {   /* No report to add. */
                return("");
            }

            /* The last iTailLines lines, under a heading saying they are the   */
            /* report's end when some were left out.                            */
            iFirst = Math.Max(0, lstLines.Count - iTailLines);
            sbTail = new System.Text.StringBuilder("\n\n");
            if (iFirst == 0)
            {   /* The whole report fits. */
                sbTail.Append("The report:");
            }
            else
            {   /* Lines left out at the start. */
                sbTail.Append("The end of the report:");
            }
            for (i = iFirst; i < lstLines.Count; i++)
            {   /* One report line per iteration, on its own line. */
                sbTail.Append("\n");
                sbTail.Append(lstLines[i]);
            }

            /* The tail, for the box to append. */
            return(sbTail.ToString());
        }

        /*--------------------------------------------------------------------*/
        /* Main_HostDelete:                                                   */
        /*                                                                    */
        /* Delete, in the Windows pane: send the selected files and folders   */
        /* to the RECYCLE BIN, after a confirmation.  Unlike a delete inside  */
        /* an image this can be undone, which is why it is the Recycle Bin    */
        /* and not a permanent delete.  (On a drive that has no Recycle Bin   */
        /* Windows deletes the item outright.)                                */
        /*                                                                    */
        /* The list stops at the first item Windows refuses, so the message   */
        /* names the one that actually stopped it; what went before it is     */
        /* already in the Recycle Bin.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstSelected : the items the request named -- the selection     */
        /*                   when Delete was asked for (the request's own     */
        /*                   list, read only).                                */
        /*     sFolder     : the folder they are in.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the items are gone and the pane re-read, or the status  */
        /*            bar or a message says why not.                          */
        /*--------------------------------------------------------------------*/
        private void Main_HostDelete(List<string> lstSelected, string sFolder)
        {
            int      i;

            if (lstSelected.Count == 0 || sFolder.Length == 0)
            {   /* Nothing selected, or the drive list (a drive is not deleted). */
                Main_SetStatus("Select one or more items in the Windows pane to delete.");
                return;
            }

            if (MSG_clsMessageForm.MSG_Show(this, Main_fntDialog, "Move " + lstSelected.Count + " item(s) from\n" + sFolder + "\nto the Recycle Bin?",
                                         "Delete from Windows?", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                                         MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {   /* The user declined. */
                return;
            }

            for (i = 0; i < lstSelected.Count; i++)
            {   /* One selected item per iteration. */
                try
                {   /* To the Recycle Bin, the shell asking nothing more     */
                    /* (OnlyErrorDialogs: this program asked already).  It   */
                    /* can refuse -- in use, no permission, or a Cancel in   */
                    /* its error dialog -- which the catch takes.            */
                    if (Directory.Exists(lstSelected[i]))
                    {   /* A folder, with everything in it. */
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(lstSelected[i],
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    }
                    else
                    {   /* A file. */
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(lstSelected[i],
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    }
                }
                catch (Exception exDelete)
                {   /* Stop at the first item that would not go. */
                    Main_ShowHostError("Deleting " + lstSelected[i], exDelete);
                    break;
                }
            }

            /* The folder re-read, so what went is gone from it. */
            Main_NavigateHost(sFolder);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnNewFolderClicked:                                           */
        /*                                                                    */
        /* Commands > New Folder: a new folder in the active pane.  (Each     */
        /* pane's New Folder button has its own handler.)                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the new folder is requested (Main_RequestNewFolder).    */
        /*--------------------------------------------------------------------*/
        private void Main_OnNewFolderClicked(object objSender, EventArgs evArgs)
        {
            /* The one function every New Folder handler calls. */
            Main_RequestNewFolder();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnCtxNewFolderClicked:                                        */
        /*                                                                    */
        /* The right-click menu's New Folder: the same request, from a        */
        /* handler of its own.                                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the new folder is requested (Main_RequestNewFolder).    */
        /*--------------------------------------------------------------------*/
        private void Main_OnCtxNewFolderClicked(object objSender, EventArgs evArgs)
        {
            /* The one function every New Folder handler calls. */
            Main_RequestNewFolder();
        }

        /*--------------------------------------------------------------------*/
        /* Main_RequestNewFolder:                                             */
        /*                                                                    */
        /* New Folder, in the active pane -- what every New Folder handler    */
        /* calls.  Nothing is asked or made here: in the Windows pane it is   */
        /* posted as a pane action (Main_HostNewFolder runs it when the       */
        /* worker admits it); in the image pane as an image edit, whose name  */
        /* question, write-access question and creation are the worker's.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the request is posted, or the status line says why not. */
        /*--------------------------------------------------------------------*/
        private void Main_RequestNewFolder()
        {
            if (Main_bHostPaneActive)
            {   /* The Windows pane: the pane action, for the folder it   */
                /* shows (refused for the drive list when it runs).       */
                Main_RequestPaneAction(HPA_enmHostAction.NewFolder, null, Main_sHostDir);
                return;
            }

            /* The image pane: an edit that asks a name, then makes that      */
            /* folder in the one path given -- the folder on show.  The       */
            /* worker asks the name only once it has taken the request, and   */
            /* asks the first-write question first if the image is            */
            /* read-only; the click itself changes nothing.                   */
            Main_PostImageEdit(new string[] { Main_sImageDir },
                new EDA_clsEditArgs(EDK_enmEditKind.NameDirectory, null, null,
                    false, 0, false, 0, 0, null, ""));
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnRenameClicked:                                              */
        /*                                                                    */
        /* Commands > Rename: rename the active pane's one selected item.     */
        /* (Each pane's Rename button has its own handler.)                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the rename is requested (Main_RequestRename).           */
        /*--------------------------------------------------------------------*/
        private void Main_OnRenameClicked(object objSender, EventArgs evArgs)
        {
            /* The one function every Rename handler calls. */
            Main_RequestRename();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnCtxRenameClicked:                                           */
        /*                                                                    */
        /* The right-click menu's Rename: the same rename, from a handler of  */
        /* its own.                                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the rename is requested (Main_RequestRename).           */
        /*--------------------------------------------------------------------*/
        private void Main_OnCtxRenameClicked(object objSender, EventArgs evArgs)
        {
            /* The one function every Rename handler calls. */
            Main_RequestRename();
        }

        /*--------------------------------------------------------------------*/
        /* Main_RequestRename:                                                */
        /*                                                                    */
        /* Rename, in the active pane -- what every Rename handler calls:     */
        /* the one selected item, posted as the Windows pane's rename action  */
        /* (Main_HostRename) or as an image edit that asks the new name and   */
        /* renames.  Nothing is asked or changed here.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the request is posted, or the status line says why not. */
        /*--------------------------------------------------------------------*/
        private void Main_RequestRename()
        {
            ListViewItem lviRow;
            string sFrom;

            if (Main_bHostPaneActive)
            {   /* The Windows pane: the pane action, with the selected   */
                /* paths -- it refuses anything but exactly one           */
                /* (Main_RequestPaneAction).                              */
                Main_RequestPaneAction(HPA_enmHostAction.Rename, Main_SelectedHostPaths(), Main_sHostDir);
                return;
            }

            if (Main_lstImage.SelectedItems.Count != 1 || Main_lstImage.SelectedItems[0].Tag == null)
            {   /* Not exactly one real entry (the ".." row is not one):   */
                /* refused, rather than guessing which was meant.          */
                Main_SetStatus("Select one item in the image pane to rename.");
                return;
            }

            /* The entry's path, read now: the request carries it, so the    */
            /* rename is of this entry whatever the pane selects while the   */
            /* name question is open.                                        */
            lviRow = Main_lstImage.SelectedItems[0];
            sFrom = Main_JoinImagePath(Main_sImageDir, lviRow.Text);
            Main_PostImageEdit(new string[] { sFrom },
                new EDA_clsEditArgs(EDK_enmEditKind.NameEntry, null, null,
                    false, 0, false, 0, 0, null, ""));
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnPropertiesClicked:                                          */
        /*                                                                    */
        /* Commands > Properties: the active pane's selected item's           */
        /* properties, or the filesystem's.  (Each pane's Properties button   */
        /* has its own handler.)                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the properties are requested (Main_RequestProperties).  */
        /*--------------------------------------------------------------------*/
        private void Main_OnPropertiesClicked(object objSender, EventArgs evArgs)
        {
            /* The one function every Properties handler calls. */
            Main_RequestProperties();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnCtxPropertiesClicked:                                       */
        /*                                                                    */
        /* The right-click menu's Properties: the same request, from a        */
        /* handler of its own.                                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the properties are requested (Main_RequestProperties).  */
        /*--------------------------------------------------------------------*/
        private void Main_OnCtxPropertiesClicked(object objSender, EventArgs evArgs)
        {
            /* The one function every Properties handler calls. */
            Main_RequestProperties();
        }

        /*--------------------------------------------------------------------*/
        /* Main_RequestProperties:                                            */
        /*                                                                    */
        /* Properties, in the active pane -- what every Properties handler    */
        /* calls: Windows' own Properties for the one selected Windows item   */
        /* (a pane action); the filesystem's window when nothing is selected  */
        /* in the image pane; otherwise the one selected entry's, posted so   */
        /* the worker can read what the dialog needs first.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the request is posted, or the status line says why not. */
        /*--------------------------------------------------------------------*/
        private void Main_RequestProperties()
        {
            ListViewItem lviRow;
            EEN_stEntry stEntry;
            string sImagePath;
            string[] arrHost;

            if (Main_bHostPaneActive)
            {   /* The Windows pane: Windows' own Properties, for exactly   */
                /* one item.                                                */
                arrHost = Main_SelectedHostPaths();
                if (arrHost.Length != 1)
                {   /* None or several: refused here, with the reason. */
                    Main_SetStatus("Select one item in the Windows pane to see its properties.");
                    return;
                }

                /* Posted as the pane action; the sheet is shown when it   */
                /* runs, for the item named now.                           */
                Main_RequestPaneAction(HPA_enmHostAction.Properties, arrHost, Main_sHostDir);

                return;
            }

            if (Main_FsPropertiesApply())
            {   /* Nothing selected in the image pane: the filesystem's own   */
                /* window instead of an entry's.                              */
                Main_ShowFsProperties();
                return;
            }

            if (Main_lstImage.SelectedItems.Count != 1 || Main_lstImage.SelectedItems[0].Tag == null)
            {   /* Not exactly one real entry (only the ".." row, or   */
                /* several): refused.                                  */
                Main_SetStatus("Select one item in the image pane to see its properties.");
                return;
            }

            /* The entry's path and its listing row, read now and posted:   */
            /* the worker checks the open is still the one they came from   */
            /* and reads a link's target before the dialog is shown         */
            /* (Main_PostPropertyInput, MainCommand.cs).                    */
            lviRow = Main_lstImage.SelectedItems[0];
            stEntry = (EEN_stEntry)lviRow.Tag;
            sImagePath = Main_JoinImagePath(Main_sImageDir, lviRow.Text);
            Main_PostPropertyInput(INK_enmInputKind.EntryProperties, sImagePath,
                new EPI_clsEntryProperties(stEntry));
        }

        /*--------------------------------------------------------------------*/
        /* Main_FsPropertiesApply:                                            */
        /*                                                                    */
        /* Whether Properties, in the Commands menu or the right-click menu,  */
        /* means the FILESYSTEM's properties now: the image pane is the       */
        /* active one, an image is open, and no row of the pane is selected   */
        /* -- not even the ".." row, which is a row even though it is no      */
        /* entry.  A right-click on the pane's empty space clears the         */
        /* selection first (Main_OnImageMouseDown), so that is how the        */
        /* background asks for it.                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when Properties shows the filesystem's window.     */
        /*--------------------------------------------------------------------*/
        private bool Main_FsPropertiesApply()
        {
            /* All three: the image side, something open, nothing picked. */
            return(!Main_bHostPaneActive && Main_bImageOpen && Main_lstImage.SelectedItems.Count == 0);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnFsPropertiesClicked:                                        */
        /*                                                                    */
        /* File > Filesystem Properties: the open filesystem's window,        */
        /* whatever is selected in either pane.                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item.                                     */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window is shown once the facts arrive.              */
        /*--------------------------------------------------------------------*/
        private void Main_OnFsPropertiesClicked(object objSender, EventArgs evArgs)
        {
            /* The window, whatever either pane has selected. */
            Main_ShowFsProperties();
        }

        /*--------------------------------------------------------------------*/
        /* Main_ShowFsProperties:                                             */
        /*                                                                    */
        /* The open filesystem's window (FsPropertiesForm.cs), asked for:     */
        /* the worker reads the filesystem's facts afresh -- its counts move  */
        /* with every change -- and the window is shown with them when they   */
        /* arrive.  The counts the open read are not used.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the request is posted, or the status line says why not. */
        /*--------------------------------------------------------------------*/
        private void Main_ShowFsProperties()
        {
            /* Posted as a property input with no entry (""): the worker's   */
            /* gate checks the open, and its read supplies the facts.        */
            Main_PostPropertyInput(INK_enmInputKind.FilesystemProperties, "", null);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnImageMouseDown:                                             */
        /*                                                                    */
        /* A mouse button went down on the image pane's list.  A RIGHT click  */
        /* on empty space -- below the last row, or beside the rows in the    */
        /* details view -- clears the selection, as Explorer does, so the     */
        /* right-click menu that follows is about the pane itself and its     */
        /* Properties item is the filesystem's.  A ListView left to itself    */
        /* keeps the old selection on such a click, and the menu would act    */
        /* on rows the user had not pointed at.  Any other click, and a right */
        /* click on a row, is the list's own business.  The Windows pane is   */
        /* left as it was.                                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the image pane's list.                             */
        /*     evMouse   : where, and which button.                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the selection is cleared, or nothing is done.           */
        /*--------------------------------------------------------------------*/
        private void Main_OnImageMouseDown(object objSender, MouseEventArgs evMouse)
        {
            if (evMouse.Button != MouseButtons.Right)
            {   /* Only a right click concerns this. */
                return;
            }

            if (Main_lstImage.HitTest(evMouse.Location).Item != null)
            {   /* On a row: the list selects it as usual. */
                return;
            }

            /* Empty space: nothing is pointed at, so nothing stays selected. */
            Main_lstImage.SelectedItems.Clear();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnDeleteClicked:                                              */
        /*                                                                    */
        /* Commands > Delete: delete the active pane's selection.  (Each      */
        /* pane's Delete button has its own handler, and the Delete key in a  */
        /* list calls Main_RequestDelete itself, Main_PaneKeyDown.)           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the delete is requested (Main_RequestDelete).           */
        /*--------------------------------------------------------------------*/
        private void Main_OnDeleteClicked(object objSender, EventArgs evArgs)
        {
            /* The one function every Delete handler calls. */
            Main_RequestDelete();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnCtxDeleteClicked:                                           */
        /*                                                                    */
        /* The right-click menu's Delete: the same delete, from a handler of  */
        /* its own.                                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the delete is requested (Main_RequestDelete).           */
        /*--------------------------------------------------------------------*/
        private void Main_OnCtxDeleteClicked(object objSender, EventArgs evArgs)
        {
            /* The one function every Delete handler calls. */
            Main_RequestDelete();
        }

        /*--------------------------------------------------------------------*/
        /* Main_RequestDelete:                                                */
        /*                                                                    */
        /* Delete, in the active pane: the selection posted -- the Windows    */
        /* pane's as its Recycle Bin action (Main_HostDelete), the image      */
        /* pane's as an image edit, each path with whether it is a folder.    */
        /* The questions (the confirmation, which in the image warns that the */
        /* delete is for good; the first-write question) and the delete are   */
        /* the worker's.  The Delete handlers and the Delete key in a list    */
        /* (Main_PaneKeyDown) call it.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the request is posted, or the status line says why not. */
        /*--------------------------------------------------------------------*/
        private void Main_RequestDelete()
        {
            List<string> lstPaths;
            List<bool> lstDirectories;
            ListViewItem lviRow;
            EEN_stEntry stEntry;
            int i;

            if (Main_bHostPaneActive)
            {   /* The Windows pane: the pane action, with the selected   */
                /* paths; it asks and recycles when it runs.              */
                Main_RequestPaneAction(HPA_enmHostAction.Delete, Main_SelectedHostPaths(), Main_sHostDir);
                return;
            }

            /* The image pane: each selected entry's path, and beside it      */
            /* whether it is a folder, read NOW from the row -- the request   */
            /* carries both, so a refresh of the rows before it runs cannot   */
            /* turn a folder's delete into a file's, or point it at           */
            /* another folder's entries.                                      */
            lstPaths = new List<string>();
            lstDirectories = new List<bool>();
            for (i = 0; i < Main_lstImage.SelectedItems.Count; i++)
            {   /* One selected row per iteration. */
                lviRow = Main_lstImage.SelectedItems[i];
                if (lviRow.Tag == null)
                {   /* The ".." row is not an item, and is never deleted. */
                    continue;
                }

                /* Its path in the folder on show, and whether it is a   */
                /* directory (a link to one is not).                     */
                stEntry = (EEN_stEntry)lviRow.Tag;
                lstPaths.Add(Main_JoinImagePath(Main_sImageDir, lviRow.Text));
                lstDirectories.Add(stEntry.EEN_uiType == ENG_clsNative.ENG_TYPE_DIR);
            }

            if (lstPaths.Count == 0)
            {   /* Nothing selected but the ".." row: refused -- an empty   */
                /* selection never means the folder itself.                 */
                Main_SetStatus("Select one or more items in the image pane to delete.");
                return;
            }

            /* Posted as the image edit, the flags beside the paths; the   */
            /* worker asks, deletes and lists the folder again, as one     */
            /* sequence.                                                   */
            Main_PostImageEdit(lstPaths.ToArray(),
                new EDA_clsEditArgs(EDK_enmEditKind.Delete, null, lstDirectories.ToArray(),
                    false, 0, false, 0, 0, null, ""));
        }

        /*--------------------------------------------------------------------*/
        /* Main_ShowNoImage:                                                  */
        /*                                                                    */
        /* Put the left pane, the window title and the status line back to    */
        /* the "no image open" state.  Called for every state the worker      */
        /* publishes with nothing to browse (Main_ObserveCommandState,        */
        /* MainCommand.cs) -- not only once, at the close -- so it must be    */
        /* safe to run again and again: the status line is touched only       */
        /* while it still shows the closed image's summary, and every other   */
        /* line (a check's verdict, a capacity test's result, a copy's        */
        /* report in the Windows pane) is left as it is.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the left pane is empty, the menu matches, and the       */
        /*            status line no longer describes a filesystem that has   */
        /*            closed.                                                 */
        /*--------------------------------------------------------------------*/
        private void Main_ShowNoImage()
        {
            if (Main_sVolumeText.Length != 0 &&
                Main_tslStatus.Text.StartsWith(Main_sVolumeText, StringComparison.Ordinal))
            {   /* The status line still shows the closed image's summary --   */
                /* alone (Main_ShowOpened) or with a folder's count after it   */
                /* (Main_ShowListResult), both of which begin with it -- so    */
                /* it is replaced by the nothing-open prompt.  Tested BEFORE   */
                /* the summary is cleared below, since the summary is what     */
                /* identifies the line; once cleared, a later call finds it    */
                /* empty and leaves the line alone.  A line the summary does   */
                /* not begin -- anything set since -- is not this function's   */
                /* to change.                                                  */
                Main_SetStatus(MAIN_NO_IMAGE_STATUS);
            }

            /* Nothing open: the session facts cleared (a device's           */
            /* description too), Close greyed and Partition/Format offered   */
            /* (as with an image open), the path box's prompt, the side      */
            /* label's MAIN_NO_IMAGE_SIDE in place of the closed             */
            /* filesystem's type, and the plain caption back.                */
            Main_bImageOpen            = false;
            Main_sImageFile            = "";
            Main_sDeviceText           = "";
            Main_sImageDir             = "/";
            Main_sVolumeText           = "";
            Main_tsmCloseImage.Enabled = false;
            Main_tsbClose.Enabled      = false;
            Main_tsmPartition.Enabled  = true;
            Main_txtImagePath.Text     = MAIN_NO_IMAGE_TEXT;
            Main_lblImageSide.Text     = MAIN_NO_IMAGE_SIDE;
            this.Text                  = "BifrostFM";

            /* The pane emptied, its Back history with it, and the buttons   */
            /* set for an empty pane.                                        */
            Main_lstImage.Items.Clear();
            Main_lstImageBack.Clear();
            Main_UpdateToolbar();
        }

        /*--------------------------------------------------------------------*/
        /* Main_NavigateImage:                                                */
        /*                                                                    */
        /* Ask for an image directory to be shown in the image pane: a        */
        /* listing posted to the worker.  Nothing in the pane changes here -- */
        /* not the rows, the folder, the path box's folder or the Back        */
        /* history; they change only when the listing arrives and is shown    */
        /* (Main_ShowListResult), so a navigation that is refused or          */
        /* overtaken by a newer one leaves the pane as it was.                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sImageDir      : the directory to show (an absolute image      */
        /*                      path).                                        */
        /*     bUserInitiated : true, the default, for a user's own move;     */
        /*                      false when the call comes from a completion   */
        /*                      of work already admitted, which must not be   */
        /*                      refused as busy.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the listing is posted, or the status line says why not. */
        /*--------------------------------------------------------------------*/
        private void Main_NavigateImage(string sImageDir, bool bUserInitiated = true)
        {
            RQT_clsRequest objRequest;
            uint uiFlags;

            if (Main_objCommandClient == null)
            {   /* No command system yet: refused with a reason -- the   */
                /* engine is reached only through it.                    */
                Main_SetStatus("The image pane is not ready to navigate.");
                return;
            }

            /* Whether this is a Back goes ON THE REQUEST (MAIN_NAV_BACK),     */
            /* from the flag Main_GoBack set: the renderer reads it from the   */
            /* listing's own request, so a later listing -- a re-list after    */
            /* a change, another navigation -- can never be taken for a        */
            /* Back by finding the flag still up.                              */
            uiFlags = 0;
            if (Main_bImageBackNav)
            {   /* Main_GoBack asked for this one. */
                uiFlags = MAIN_NAV_BACK;
            }

            /* The flag lowered once it is on the request.  Then the          */
            /* listing posted on the main pane's navigation stream (only      */
            /* the newest one waiting is kept), and remembered as the         */
            /* newest navigation: the renderer and the completion below act   */
            /* on that one only.                                              */
            Main_bImageBackNav = false;
            objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.ListFolder, 0,
                Main_objCommandClient.CLI_ReadState(), null,
                new string[] { sImageDir }, 0, uiFlags, null,
                sImageDir, "main-navigation");
            Main_objLatestNavigation = objRequest;
            Main_objCommandClient.CLI_Post(objRequest, Main_OnNavigationResult, bUserInitiated);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnNavigationResult:                                           */
        /*                                                                    */
        /* A navigation's completion.  Its listing, when it ran, has already  */
        /* been shown by then (the Folder view comes before the result).      */
        /* What is left is the case with no listing at all: a navigation the  */
        /* worker refused or that a newer one replaced, whose path box must   */
        /* be put back and whose reason said.                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the navigation's request.                         */
        /*     objResult  : its result.                                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the navigation is no longer the newest, and a failed    */
        /*            one's path box is put back.                             */
        /*--------------------------------------------------------------------*/
        private void Main_OnNavigationResult(RQT_clsRequest objRequest, RSL_clsResult objResult)
        {
            if (Main_objLatestNavigation != objRequest)
            {   /* An older navigation, or one dropped for a newer: nothing   */
                /* -- the newer one's path box and status are not touched.    */
                return;
            }

            /* The newest navigation is done.  A failure puts the path box    */
            /* back to the folder the rows are of (it may hold the path the   */
            /* user typed), drops a Back that was asked for, and says why;    */
            /* the rows and the folder were never changed for it.             */
            Main_objLatestNavigation = null;
            if (objResult.RSL_iStatus != SYS_clsSystem.SYS_OK)
            {   /* Refused, replaced or failed. */
                Main_txtImagePath.Text = Main_sImageDir;
                Main_bImageBackNav = false;
                Main_SetStatus(objResult.RSL_sDetail);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_ShowListResult:                                               */
        /*                                                                    */
        /* Show a completed directory listing in the image pane: the pane's   */
        /* directory, its path box, its Back history and its rows all become  */
        /* the listing's, and the status line counts the entries.  The ".."   */
        /* row comes first everywhere but the root, then the entries,         */
        /* directories first; each row keeps its EEN_stEntry in its Tag, so   */
        /* an action on the row has the entry's facts without asking the      */
        /* engine again, and carries the program's own icon for its type.     */
        /*                                                                    */
        /* Its one caller is Main_RenderCommandNavigation (MainCommand.cs),   */
        /* on the UI thread, once a Folder view -- a navigation's, or the     */
        /* re-list after a change -- has passed that renderer's checks, and   */
        /* Main_bImageBackNav says whether it is a Back.  A failed listing    */
        /* never reaches it -- the renderer reports it first, since the       */
        /* listing's status is its result's -- so the failure branch below    */
        /* is a guard.                                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objResult : the completed listing.                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the image pane shows the directory, or the failure is   */
        /*            reported and the pane is left as it was.                */
        /*--------------------------------------------------------------------*/
        private void Main_ShowListResult(ELR_clsListResult objResult)
        {
            ListViewItem lviRow;
            EEN_stEntry stEntry;
            string       sSize;
            int          i;

            if (objResult.ELR_iStatus != ENG_clsNative.ENG_OK)
            {   /* The directory could not be listed (a guard: the renderer   */
                /* reports a failed listing before calling this).  The pane   */
                /* stays where it was, so the path box is put back to match   */
                /* it (it may hold the path the user typed), and no Back      */
                /* entry is spent: nothing was shown for it.                  */
                Main_txtImagePath.Text = Main_sImageDir;
                Main_bImageBackNav     = false;
                Main_ShowEngineError("Listing " + objResult.ELR_sImageDir, objResult.ELR_iStatus, objResult.ELR_sDetail);
                return;
            }

            if (Main_bImageBackNav)
            {   /* This listing is the one Back asked for (the renderer set   */
                /* the flag from its request): the Back entry is spent NOW,   */
                /* when its folder is shown -- not at the click -- and the    */
                /* folder being left is not pushed, or Back would only        */
                /* bounce between two folders.                                */
                if (Main_lstImageBack.Count != 0 &&
                    Main_lstImageBack[Main_lstImageBack.Count - 1] == objResult.ELR_sImageDir)
                {   /* Still the newest entry, and the folder listed: taken   */
                    /* off.  Anything else leaves the history alone rather    */
                    /* than lose a different entry.                           */
                    Main_lstImageBack.RemoveAt(Main_lstImageBack.Count - 1);
                }
            }
            else if (objResult.ELR_sImageDir != Main_sImageDir)
            {   /* The pane is moving to a different directory, and not by   */
                /* Back: the one being left is where Back returns to.        */
                Main_PushBack(Main_lstImageBack, Main_sImageDir);
            }

            /* The listing is the pane's now: the Back flag spent, and the   */
            /* directory and its path box set to it.                         */
            Main_bImageBackNav     = false;
            Main_sImageDir         = objResult.ELR_sImageDir;
            Main_txtImagePath.Text = objResult.ELR_sImageDir;

            /* The entries put in order before they go in: directories    */
            /* first, then by name, ordinal (Main_CompareImageEntries).   */
            /* The pane's own sorter then orders the rows once, below.    */
            objResult.ELR_lstEntries.Sort(Main_CompareImageEntries);

            /* Fill the list with redrawing off, so a large directory does not   */
            /* flicker row by row.                                               */
            /* The list's sorter is taken off while the rows go in -- with one   */
            /* attached, every row added would sort the whole list again --      */
            /* and put back after, which sorts once.                             */
            Main_lstImage.ListViewItemSorter = null;
            Main_lstImage.BeginUpdate();
            Main_lstImage.Items.Clear();

            if (objResult.ELR_sImageDir != "/")
            {   /* Not the root: offer the way up.  Its Tag stays null, which   */
                /* is how the activation handler tells it from a real entry.    */
                lviRow            = new ListViewItem(MAIN_PARENT_NAME);
                lviRow.ImageIndex = EIC_EntryIcons.EIC_ICON_UP;
                Main_lstImage.Items.Add(lviRow);
            }

            for (i = 0; i < objResult.ELR_lstEntries.Count; i++)
            {   /* One directory entry per iteration. */
                stEntry = objResult.ELR_lstEntries[i];

                /* A size means something for a file or a link; a directory's   */
                /* size is its own block usage, which would only mislead.       */
                sSize = "";

                if (stEntry.EEN_uiType != ENG_clsNative.ENG_TYPE_DIR)
                {   /* Not a directory: show the size. */
                    sSize = Main_FormatSize(stEntry.EEN_ullSize);
                }

                /* The row: the name, then one cell per column in             */
                /* Main_asImageColumns' order -- size, type, mode, owner as   */
                /* uid:gid, modified -- the type's icon, and the entry's      */
                /* facts in the Tag for every later action on the row.        */
                lviRow = new ListViewItem(ENG_clsNative.ENG_EntryName(stEntry));
                lviRow.SubItems.Add(sSize);
                lviRow.SubItems.Add(Main_TypeText(stEntry.EEN_uiType));
                lviRow.SubItems.Add(Main_ModeText(stEntry.EEN_uiMode));
                lviRow.SubItems.Add(stEntry.EEN_uiUid.ToString() + ":" + stEntry.EEN_uiGid.ToString());
                lviRow.SubItems.Add(Main_FormatEpoch(stEntry.EEN_uiMtime));
                lviRow.ImageIndex = Main_ImageIconIndex(stEntry.EEN_uiType);
                lviRow.Tag        = stEntry;

                /* Into the list. */
                Main_lstImage.Items.Add(lviRow);
            }

            /* The sorter back on, which orders the rows once, and the   */
            /* list redrawn.                                             */
            Main_lstImage.ListViewItemSorter = Main_asrtSide[MAIN_SIDE_IMAGE];
            Main_lstImage.EndUpdate();

            /* The new rows may be wider or narrower than the last directory's:   */
            /* the kept measurement is dropped, so the columns -- or the grid     */
            /* of the List and Small Icons views -- are measured afresh.          */
            Main_aaiDetailWidths[MAIN_SIDE_IMAGE] = null;
            Main_GridChanged(Main_lstImage, true);
            Main_SizeColumns(Main_lstImage);

            /* New rows: their ticks are set from the selection (no-op without boxes). */
            Main_PostItemChecks(Main_lstImage);

            /* The status bar: the volume summary, then the count here. */
            Main_SetStatus(Main_sVolumeText + "   |   " + objResult.ELR_lstEntries.Count + " item(s) in " + objResult.ELR_sImageDir);

            /* A new directory: the selection is gone and Back and Up may have changed. */
            Main_UpdateToolbar();
        }

        /*--------------------------------------------------------------------*/
        /* Main_NavigateHost:                                                 */
        /*                                                                    */
        /* Show a Windows folder in the right pane, or -- for "" -- the list  */
        /* of drives, which is what sits above a drive's root.  A folder that */
        /* cannot be read (no permission, a drive with no disc) is reported   */
        /* and the pane is left on the folder it was showing.                 */
        /*                                                                    */
        /* Every row's icon and Type text are the shell's own for that item,  */
        /* so the pane shows what Explorer would -- but in TWO STEPS, as      */
        /* Explorer does it.  The list is filled at once from what a name     */
        /* alone tells (SHI_LookupQuick: the ordinary folder icon, the icon   */
        /* of a file's extension); the rows whose icon is their own -- every  */
        /* folder, every program, installer and shortcut -- are then asked    */
        /* about properly a few at a time (Main_ResolveHostIcons).  Asking    */
        /* for them all before showing anything would hold a folder of        */
        /* thousands of files, or of a few large installers, back for         */
        /* seconds.                                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sHostDir : the folder to show, or "" for the drives.           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the right pane shows the folder, or stays as it was.    */
        /*--------------------------------------------------------------------*/
        private void Main_NavigateHost(string sHostDir)
        {
            List<ListViewItem> lstRows;
            DirectoryInfo      dirShown;
            DirectoryInfo[]    adirSubs;
            FileInfo[]         afilFiles;
            DriveInfo[]        adrvDrives;
            ListViewItem       lviRow;
            string             sTypeName;
            int                iIcon;
            int                i;
            List<ListViewItem> lstPending;
            bool               bFinal;

            /* The rows are built aside, and the pane changed only once     */
            /* the read has worked; the rows still to have their own icon   */
            /* asked for are listed apart for the subsequent icon turns.    */
            lstRows    = new List<ListViewItem>();
            lstPending = new List<ListViewItem>();

            try
            {   /* Reading a folder can fail in several ordinary ways. */
                if (sHostDir.Length == 0)
                {   /* The level above the drives: list the drives themselves. */
                    adrvDrives = DriveInfo.GetDrives();

                    for (i = 0; i < adrvDrives.Length; i++)
                    {   /* One drive per iteration. */
                        SHI_ShellIcons.SHI_Lookup(adrvDrives[i].Name, true, out iIcon, out sTypeName);

                        if (sTypeName.Length == 0)
                        {   /* The shell named no type: fall back to the kind of drive. */
                            sTypeName = adrvDrives[i].DriveType.ToString();
                        }

                        /* The drive's row: its root as the name, the type, the    */
                        /* shell's icon, and the root path in the Tag (a string,   */
                        /* like a folder's).                                       */
                        lviRow = new ListViewItem(adrvDrives[i].Name);
                        lviRow.SubItems.Add("");
                        lviRow.SubItems.Add(sTypeName);
                        lviRow.SubItems.Add("");
                        lviRow.ImageIndex = iIcon;
                        lviRow.Tag        = adrvDrives[i].Name;
                        lstRows.Add(lviRow);
                    }
                }
                else
                {   /* A real folder: the way up, its subfolders, then its files. */
                    dirShown  = new DirectoryInfo(sHostDir);
                    adirSubs  = dirShown.GetDirectories();
                    afilFiles = dirShown.GetFiles();

                    /* The ".." row; its Tag stays null, as in the image pane.   */
                    /* It is no folder on disk; it takes the way-up picture      */
                    /* added to the pane's lists when the window was made.       */
                    lviRow            = new ListViewItem(MAIN_PARENT_NAME);
                    lviRow.ImageIndex = Main_iHostUpIcon;
                    lstRows.Add(lviRow);

                    for (i = 0; i < adirSubs.Length; i++)
                    {   /* One subfolder per iteration.  The ordinary     */
                        /* folder's icon stands in; the folder's own is   */
                        /* asked for afterwards.                          */
                        bFinal = SHI_ShellIcons.SHI_LookupQuick(adirSubs[i].FullName, true, out iIcon, out sTypeName);

                        if (sTypeName.Length == 0)
                        {   /* The shell named no type. */
                            sTypeName = "Folder";
                        }

                        /* Its row: no size, the type, the modify time, the icon   */
                        /* and its path in the Tag (a string: a folder).           */
                        lviRow = new ListViewItem(adirSubs[i].Name);
                        lviRow.SubItems.Add("");
                        lviRow.SubItems.Add(sTypeName);
                        lviRow.SubItems.Add(adirSubs[i].LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
                        lviRow.ImageIndex = iIcon;
                        lviRow.Tag        = adirSubs[i].FullName;
                        lstRows.Add(lviRow);

                        if (!bFinal)
                        {   /* Its own icon is still to be asked for. */
                            lstPending.Add(lviRow);
                        }
                    }

                    for (i = 0; i < afilFiles.Length; i++)
                    {   /* One file per iteration.  Its extension's icon   */
                        /* is final for most kinds, and stands in for a    */
                        /* program's own.                                  */
                        bFinal = SHI_ShellIcons.SHI_LookupQuick(afilFiles[i].FullName, false, out iIcon, out sTypeName);

                        if (sTypeName.Length == 0)
                        {   /* The shell named no type. */
                            sTypeName = "File";
                        }

                        /* Its row: size, type, modify time, the icon, and its   */
                        /* FileInfo in the Tag (how a file is told from a        */
                        /* folder).                                              */
                        lviRow = new ListViewItem(afilFiles[i].Name);
                        lviRow.SubItems.Add(Main_FormatSize((ulong)afilFiles[i].Length));
                        lviRow.SubItems.Add(sTypeName);
                        lviRow.SubItems.Add(afilFiles[i].LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
                        lviRow.ImageIndex = iIcon;
                        lviRow.Tag        = afilFiles[i];
                        lstRows.Add(lviRow);

                        if (!bFinal)
                        {   /* Its own icon is still to be asked for. */
                            lstPending.Add(lviRow);
                        }
                    }
                }
            }
            catch (Exception exRead)
            {   /* Not readable: say why and keep showing the current folder. */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("host", "cannot list " + sHostDir + ": " + exRead.Message);
                }

                /* The reason on the status line, marked as shown; the current   */
                /* folder stays.                                                 */
                Main_SetStatus("Cannot show " + sHostDir + ": " + exRead.Message);
                Main_bHostRefusalShown = true;
                Main_txtHostPath.Text  = Main_sHostDir;
                Main_bHostBackNav      = false;
                return;
            }

            if (Main_bHostRefusalShown)
            {   /* The status line still shows this pane's own earlier "Cannot    */
                /* show ..." refusal, which this successful read makes untrue:    */
                /* cleared.  Only that refusal -- this function also runs after   */
                /* every copy, whose report on the status line must stay.         */
                Main_SetStatus("");
                Main_bHostRefusalShown = false;
            }

            /* The read succeeded: only now does the pane change. */
            if (!string.Equals(sHostDir, Main_sHostDir, StringComparison.OrdinalIgnoreCase) && !Main_bHostBackNav)
            {   /* Moving to a different folder, and not by Back: the one   */
                /* being left is where Back returns to.                     */
                Main_PushBack(Main_lstHostBack, Main_sHostDir);
            }

            /* The folder is the pane's now: the Back flag spent, the   */
            /* folder and its path box set.                             */
            Main_bHostBackNav     = false;
            Main_sHostDir         = sHostDir;
            Main_txtHostPath.Text = sHostDir;

            /* The sorter comes off while the rows go in and goes back after,   */
            /* so the list is sorted once, not once per row.  (As measured,     */
            /* the time here is the list taking the rows in, not the sort, so   */
            /* sorting them beforehand would gain nothing.)                     */
            Main_lstHost.ListViewItemSorter = null;
            Main_lstHost.BeginUpdate();
            Main_lstHost.Items.Clear();
            Main_lstHost.Items.AddRange(lstRows.ToArray());
            Main_lstHost.ListViewItemSorter = Main_asrtSide[MAIN_SIDE_HOST];
            Main_lstHost.EndUpdate();

            /* The new rows may be wider or narrower than the last folder's:    */
            /* the kept measurement is dropped, so the columns -- or the grid   */
            /* of the List and Small Icons views -- are measured afresh.        */
            Main_aaiDetailWidths[MAIN_SIDE_HOST] = null;
            Main_GridChanged(Main_lstHost, true);
            Main_SizeColumns(Main_lstHost);

            /* New rows: their ticks are set from the selection (no-op without boxes). */
            Main_PostItemChecks(Main_lstHost);

            /* The rows that only have a stand-in icon so far are now asked   */
            /* about properly, a few per timer tick, top of the list first.   */
            /* A list left over from the folder shown before is dropped.      */
            Main_lstIconPending = lstPending;
            Main_iIconNext      = 0;

            if (lstPending.Count != 0)
            {   /* There is something to resolve. */
                Main_tmrIcons.Start();
            }
            else
            {   /* Every row's icon is already final. */
                Main_tmrIcons.Stop();
            }

            /* The folder remembered, for the next run to start in. */
            SET_Settings.SET_WriteString(SET_Settings.SET_HOST_FOLDER, sHostDir);

            /* A new folder: the selection is gone and Back and Up may have changed. */
            Main_UpdateToolbar();
        }

        /*--------------------------------------------------------------------*/
        /* Main_ResolveHostIcons:                                             */
        /*                                                                    */
        /* One turn of giving the Windows pane's rows their own icons: ask    */
        /* the shell about the next rows on the waiting list, by their real   */
        /* paths, until the turn's time is used up or the list is finished.   */
        /* A row whose own icon (or type name) differs from the stand-in it   */
        /* was shown with is updated in place.  When the list is finished the */
        /* timer that calls this is stopped.                                  */
        /*                                                                    */
        /* It runs on the UI thread, in short turns between which the window  */
        /* handles the user's input, so the pane is usable from the moment    */
        /* it is filled.  One question can still take long by itself (a large */
        /* installer on a slow disk); the turn ends after it.                 */
        /*                                                                    */
        /* A type name that changes is not re-sorted into place: a pane       */
        /* sorted by Type keeps the order its stand-in names gave it.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : some more rows, or all of them, show their own icons.   */
        /*--------------------------------------------------------------------*/
        private void Main_ResolveHostIcons()
        {
            System.Diagnostics.Stopwatch swTurn;
            ListViewItem                 lviRow;
            string                       sPath;
            string                       sTypeName;
            bool                         bIsFolder;
            int                          iIcon;

            /* The turn's clock: rows are asked about until it passes        */
            /* MAIN_ICON_SLICE_MS, the rest of the tick left for the user.   */
            swTurn = System.Diagnostics.Stopwatch.StartNew();

            while (Main_iIconNext < Main_lstIconPending.Count && swTurn.ElapsedMilliseconds < MAIN_ICON_SLICE_MS)
            {   /* One waiting row per iteration. */
                lviRow = Main_lstIconPending[Main_iIconNext];
                Main_iIconNext++;

                /* A folder row's Tag is its path; a file row's is a FileInfo. */
                if (lviRow.Tag is string)
                {   /* A folder. */
                    sPath     = (string)lviRow.Tag;
                    bIsFolder = true;
                }
                else
                {   /* A file. */
                    sPath     = ((FileInfo)lviRow.Tag).FullName;
                    bIsFolder = false;
                }

                /* The shell asked properly, by the real path: the item's own   */
                /* icon and type name (this is the call that can be slow).      */
                SHI_ShellIcons.SHI_Lookup(sPath, bIsFolder, out iIcon, out sTypeName);

                if (lviRow.ImageIndex != iIcon)
                {   /* Its own icon is not the stand-in: show it. */
                    lviRow.ImageIndex = iIcon;
                }

                if (sTypeName.Length != 0 && lviRow.SubItems[MAIN_HOST_COLUMN_TYPE].Text != sTypeName)
                {   /* Its own type name is not the stand-in's either. */
                    lviRow.SubItems[MAIN_HOST_COLUMN_TYPE].Text = sTypeName;
                }
            }

            if (Main_iIconNext >= Main_lstIconPending.Count)
            {   /* Finished: stop the timer and let the rows go. */
                Main_tmrIcons.Stop();
                Main_lstIconPending = new List<ListViewItem>();
                Main_iIconNext      = 0;
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnIconTick:                                                   */
        /*                                                                    */
        /* The icon timer ticked: take one turn at the waiting rows.          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the timer.                                         */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : one turn has run.                                       */
        /*--------------------------------------------------------------------*/
        private void Main_OnIconTick(object objSender, EventArgs evArgs)
        {
            /* One turn; the turn stops the timer when the list is done. */
            Main_ResolveHostIcons();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnShown:                                                      */
        /*                                                                    */
        /* The window is on screen: start what needs its handle -- the        */
        /* command system first, since every image and device command goes    */
        /* through it -- then show the Windows pane and open whatever the     */
        /* command line named.                                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the window (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window is running; or, the work folder not made,    */
        /*            closing; or, after a failure part-way, an Exit is       */
        /*            asked for.                                              */
        /*--------------------------------------------------------------------*/
        private void Main_OnShown(object objSender, EventArgs evArgs)
        {
            string sHostDir;

            try
            {   /* Any failure from here is caught below and closed through   */
                /* the worker's Exit: once the worker runs, the window may    */
                /* close no other way.  First the temporary folders a         */
                /* crashed earlier run left (drags' and work folders alike,   */
                /* Main_SweepDragFolders), then the command system and its    */
                /* worker.                                                    */
                Main_SweepDragFolders(false);
                if (!Main_StartCommandSystem())
                {   /* The work folder could not be made: no worker was     */
                    /* started (the user has been told), so the window is   */
                    /* simply closed, on a later turn.                      */
                    BeginInvoke(new MethodInvoker(Main_CloseAfterWorker));
                    return;
                }

                /* The stored Verify setting handed to the engine first, so    */
                /* it is in force before any image or device work this start   */
                /* posts -- the worker runs requests in order.                 */
                Main_ApplyWriteVerify();

                /* The divider and the columns, now the window has its size. */
                Main_ApplyLayout();

                /* The Windows folder shown last time, if it still exists; the   */
                /* list of drives otherwise.                                     */
                sHostDir = SET_Settings.SET_ReadString(SET_Settings.SET_HOST_FOLDER, "");

                if (sHostDir.Length != 0 && !Directory.Exists(sHostDir))
                {   /* The remembered folder has gone. */
                    sHostDir = "";
                }

                /* This first showing is not a move FROM anywhere, so it is   */
                /* flagged as Back is: nothing goes onto the Back history.    */
                Main_bHostBackNav = true;
                Main_NavigateHost(sHostDir);

                if (Main_sStartImage.Length != 0)
                {   /* An image was named on the command line: a filesystem or a disk dump, as Open Image takes it. */
                    Main_OpenImageFile(Main_sStartImage, false);
                }

                /* A device command from the command line (--open-device and   */
                /* the rest: how an elevated restart resumes) run on a LATER   */
                /* turn, once this handler has returned and the window is      */
                /* fully up; it only posts.                                    */
                BeginInvoke(new MethodInvoker(Main_RunStartCommand));
            }
            catch (Exception exStart)
            {   /* The start failed part-way: logged, said, and an Exit       */
                /* asked for, which closes whatever was started through the   */
                /* worker -- or, with no worker, closes the window            */
                /* (Main_RequestCommandExit).                                 */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("app", "command startup failed: " + exStart.Message);
                }

                /* Said on the status line, then the Exit asked for. */
                Main_SetStatus("Startup failed: " + exStart.Message);
                Main_RequestCommandExit();
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_RunStartCommand:                                              */
        /*                                                                    */
        /* The command line's device command -- --open-device, --read-device, */
        /* --write-device, --partition-device or --check-device, the way an   */
        /* elevated restart passes its action on -- run once the window is    */
        /* up: the command's request posted, as its menu item posts it, after */
        /* a restart as Administrator is asked for where the copy lacks it    */
        /* (Partition Device's request asks for its own, after its warning).  */
        /* Run on a posted turn from Main_OnShown.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the request is posted, a restart as Administrator asked */
        /*            for, or nothing (no command, or the window closing).    */
        /*--------------------------------------------------------------------*/
        private void Main_RunStartCommand()
        {
            string sCommand;
            string sWhat;

            if (IsDisposed || Main_bCommandStopped)
            {   /* The window closed, or the worker stopped, before this   */
                /* turn ran: nothing to post to.                           */
                return;
            }

            /* The command, as the program's start read it from the command   */
            /* line (APP_ReadStartCommand); then the action's words for a     */
            /* restart question.  Every device command posts ONE request of   */
            /* its own whose sequence asks what it needs (below).             */
            sCommand = APP_clsProgram.APP_ReadStartCommand();
            if (sCommand == "open-device")
            {   /* Open Device: its request asks the device itself. */
                sWhat = "Opening a device";
            }
            else if (sCommand == "read-device")
            {   /* Read Device: its request asks the device, its range and   */
                /* the file to write itself.                                 */
                sWhat = "Reading a device";
            }
            else if (sCommand == "write-device")
            {   /* Write Device: its request asks the image file and the   */
                /* device itself.                                          */
                sWhat = "Writing a device";
            }
            else if (sCommand == "partition-device")
            {   /* Partition Device: ONE OpenPartition request, posted here    */
                /* with no restart asked for first -- its sequence restarts    */
                /* as Administrator itself when it must, once the disk is      */
                /* chosen and warned about (Sys_OpenPartitionOperation).       */
                /* The disk is the one --disk names (the copy that restarted   */
                /* this one chose it and gave its warning, so neither is       */
                /* asked again), or "" to ask it.  Posted as the start-up's    */
                /* continuation (false), so it is not refused for the          */
                /* start-up's own requests still running.                      */
                if (Main_objCommandClient == null)
                {   /* No command system yet: refused with a reason. */
                    Main_SetStatus("The partition command endpoint is not initialized.");
                    return;
                }

                /* The request; a failure comes back to the status line. */
                Main_objCommandClient.CLI_Post(CFC_clsCommands.CFC_Create(KND_enmKind.OpenPartition, 0,
                    Main_objCommandClient.CLI_ReadState(), null, null, 0, 0,
                    new POA_clsPartitionOpenArgs("", false, true, APP_clsProgram.APP_ReadStartDisk()), "", ""),
                    Main_ReportCommandFailure, false);
                return;
            }
            else if (sCommand == "check-device")
            {   /* Check Filesystem on a device: its request asks the   */
                /* device itself.                                       */
                sWhat = "Checking a device";
            }
            else
            {   /* No device command on the command line: nothing to do. */
                return;
            }

            if (!Main_RequireElevated(sWhat, sCommand))
            {   /* Not elevated: a restart as Administrator has been asked   */
                /* for instead, which brings the same command back -- or     */
                /* it could not be: either way nothing more here.            */
                return;
            }

            if (sCommand == "open-device" || sCommand == "check-device")
            {   /* Open or Check a device: ONE OpenDevice request whose        */
                /* sequence asks the device (Main_PickDevice's request), as    */
                /* the start-up's continuation (false), so it is not refused   */
                /* for the start-up's own requests still running; a pending    */
                /* Exit still refuses it.                                      */
                Main_objCommandClient.CLI_Post(CFC_clsCommands.CFC_Create(KND_enmKind.OpenDevice, 0,
                    Main_objCommandClient.CLI_ReadState(), null, null, 0, 0,
                    new DOA_clsDeviceOpenArgs(sCommand == "check-device", true, ""), "/", ""),
                    Main_ReportCommandFailure, false);
                return;
            }

            if (sCommand == "read-device")
            {   /* Save a device to an image file: ONE ReadDevice request   */
                /* whose sequence asks the device and the file              */
                /* (Main_PostDeviceRead), as the start-up's continuation    */
                /* (false).                                                 */
                Main_PostDeviceRead(false);
                return;
            }

            /* Write a device, the one command left: ONE WriteDevice      */
            /* request whose sequence asks the file and the device        */
            /* (Main_PostDeviceWrite), as the start-up's continuation     */
            /* (false), so it is not refused for the start-up's own       */
            /* requests still running; a pending Exit still refuses it.   */
            Main_PostDeviceWrite(false);
        }

        /*--------------------------------------------------------------------*/
        /* Main_RequireElevated:                                              */
        /*                                                                    */
        /* Whether the program runs as Administrator, which reading or        */
        /* writing a real disk needs -- and, when it does not, the restart as */
        /* Administrator asked for instead: an Exit carrying the restart      */
        /* (ELA_clsElevationArgs), whose question, closing of everything and  */
        /* launch of the elevated copy all come after this returns.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sWhat    : the device action in words, for the restart's       */
        /*                question.                                           */
        /*     sCommand : the start-up command the elevated copy runs         */
        /*                (without its dashes), which brings it back to the   */
        /*                same action.                                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when already elevated (the caller goes on); false  */
        /*            when the restart was asked for, or the command system   */
        /*            is not ready.                                           */
        /*--------------------------------------------------------------------*/
        private bool Main_RequireElevated(string sWhat, string sCommand)
        {
            RQT_clsRequest objRequest;

            if (APP_clsProgram.APP_IsProcessElevated())
            {   /* Already Administrator: the caller goes on. */
                return(true);
            }

            if (Main_objCommandClient == null)
            {   /* No command system yet: refused with a reason -- the   */
                /* restart closes everything through the worker, and     */
                /* nothing else may start it.                            */
                Main_SetStatus("The elevated-restart command endpoint is not initialized.");
                return(false);
            }

            /* The Exit with the restart: the worker asks (a restart always   */
            /* asks), cancels what waits, closes the editors, the session     */
            /* and any window in order, and only then asks the window to      */
            /* launch the elevated copy (Main_RestartElevated); the Exit      */
            /* goes on to Stopped only if Windows started it.                 */
            objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.Exit, 0,
                Main_objCommandClient.CLI_ReadState(), null, null, 0, 0,
                new ELA_clsElevationArgs(sWhat, sCommand, ""), "", "");
            Main_objCommandClient.CLI_Post(objRequest, Main_ReportCommandFailure);

            /* The commands greyed at once: the pending Exit is read         */
            /* straight from the command system, before any state says so.   */
            Main_UpdateToolbar();

            /* Not elevated: the caller stops here -- the elevated copy   */
            /* will run the action, through sCommand.                     */
            return(false);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnReadDeviceClicked:                                          */
        /*                                                                    */
        /* File > Save Device to Image File: image a card or stick to a file  */
        /* (what Win32DiskImager's Read does).  Administrator rights come     */
        /* first (Main_RequireElevated); then ONE ReadDevice request, whose   */
        /* sequence lists the disks, asks the device, how much and the file,  */
        /* and copies (CommandRawSequence.cs, Sys_AskDeviceRead).             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item.                                     */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the request is posted, a restart asked for, or the      */
        /*            status line says why not.                               */
        /*--------------------------------------------------------------------*/
        private void Main_OnReadDeviceClicked(object objSender, EventArgs evArgs)
        {
            if (!Main_RequireElevated("Reading a device", "read-device"))
            {   /* Restarting, or declined. */
                return;
            }

            if (Main_objCommandClient == null)
            {   /* No command system yet: refused with a reason -- the   */
                /* device is asked only by the request's sequence.       */
                Main_SetStatus("The device-input command endpoint is not initialized.");
                return;
            }

            /* Said at once, since listing the disks can take a moment;      */
            /* then the request, carrying only the folder its save dialog    */
            /* starts in (Main_PostDeviceRead), posted as fresh user work.   */
            Main_SetStatus("Looking for disks ...");
            Main_PostDeviceRead(true);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnWriteDeviceClicked:                                         */
        /*                                                                    */
        /* File > Write Image File to Device: write a raw image to a card or  */
        /* stick (what Win32DiskImager's Write does).  Administrator rights   */
        /* come first; then ONE WriteDevice request, whose sequence asks the  */
        /* image file, inspects it, asks the device to write it to, and       */
        /* writes (CommandRawSequence.cs, Sys_AskDeviceWrite).                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item.                                     */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the request is posted, a restart asked for, or the      */
        /*            status line says why not.                               */
        /*--------------------------------------------------------------------*/
        private void Main_OnWriteDeviceClicked(object objSender, EventArgs evArgs)
        {
            if (!Main_RequireElevated("Writing a device", "write-device"))
            {   /* Restarting, or declined. */
                return;
            }

            /* The request, carrying only the folder its file dialog starts   */
            /* in (Main_PostDeviceWrite), posted as fresh user work.          */
            Main_PostDeviceWrite(true);
        }

        /*--------------------------------------------------------------------*/
        /* MAIN_ImageFileText:                                                */
        /*                                                                    */
        /* An image file, described for Write Image's warning: a plain one by */
        /* its size; a compressed one by its format and the size of the image */
        /* inside it -- what the device will actually receive -- or by the    */
        /* fact that the format does not record that size.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stInfo : what the engine read about the file.                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : e.g. "3.9 GiB", "a .xz file holding a 3.9 GiB image", */
        /*              "a .gz file; the image's size is not recorded".       */
        /*--------------------------------------------------------------------*/
        internal static string MAIN_ImageFileText(IFI_stImageInfo stInfo)
        {
            string sFormat;

            if (stInfo.IFI_uiKind == ENG_clsNative.ENG_IMAGE_RAW)
            {   /* Plain: the file is the image. */
                return(DVF_clsDeviceForm.DVF_SizeText(stInfo.IFI_ullImageBytes));
            }

            /* The format, by the name its files usually carry. */
            sFormat = "compressed";

            if (stInfo.IFI_uiKind == ENG_clsNative.ENG_IMAGE_XZ)
            {   /* xz. */
                sFormat = ".xz";
            }
            else if (stInfo.IFI_uiKind == ENG_clsNative.ENG_IMAGE_GZIP)
            {   /* gzip. */
                sFormat = ".gz";
            }
            else if (stInfo.IFI_uiKind == ENG_clsNative.ENG_IMAGE_ZSTD)
            {   /* zstd. */
                sFormat = ".zst";
            }
            else if (stInfo.IFI_uiKind == ENG_clsNative.ENG_IMAGE_ZIP)
            {   /* A zip archive, whose first file is the image. */
                sFormat = ".zip";
            }

            if (stInfo.IFI_ullImageBytes == ENG_clsNative.ENG_SIZE_UNKNOWN)
            {   /* The format does not say how large the image is. */
                return("a " + sFormat + " file; the image's size is not recorded");
            }

            /* The image inside, which is what the device receives. */
            return("a " + sFormat + " file holding a " + DVF_clsDeviceForm.DVF_SizeText(stInfo.IFI_ullImageBytes) + " image");
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnOpenDeviceClicked:                                          */
        /*                                                                    */
        /* File > Open Device.  Reading a real disk needs Administrator, so   */
        /* an unelevated program first offers to restart itself elevated --   */
        /* a restart rather than a helper process: on a machine with UAC set  */
        /* to prompt there is one prompt, and the restarted copy comes        */
        /* straight back here through --open-device.  Elevated, the worker    */
        /* lists the disks and the picker is shown (Main_PickDevice).         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item, or the window itself at start-up.   */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the picker is asked for, a restart asked for, or the    */
        /*            status line says why not.                               */
        /*--------------------------------------------------------------------*/
        private void Main_OnOpenDeviceClicked(object objSender, EventArgs evArgs)
        {
            /* The shared picker, for opening (false: not for a check). */
            Main_PickDevice(false);
        }

        /*--------------------------------------------------------------------*/
        /* Main_PickDevice:                                                   */
        /*                                                                    */
        /* The device picker behind Open Device and, with nothing open, Check */
        /* Filesystem: the restart as Administrator when it is needed (which  */
        /* resumes at --open-device or --check-device, so the purpose         */
        /* survives the restart), then the device input asked for.  The       */
        /* picker only collects the choice; its OK posts the whole Open (or   */
        /* Check) of that device as one command.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bCheck : true to check what is chosen rather than open it.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the input is posted, a restart asked for, or the        */
        /*            status line says why not.                               */
        /*--------------------------------------------------------------------*/
        private void Main_PickDevice(bool bCheck)
        {
            bool              bGo;
            RQT_clsRequest    objRequest;

            if (bCheck)
            {   /* A check of a device needs the same rights as opening one. */
                bGo = Main_RequireElevated("Checking a device", "check-device");
            }
            else
            {   /* Opening one: the same rights, and a restart resumes at   */
                /* --open-device instead.                                   */
                bGo = Main_RequireElevated("Opening a device", "open-device");
            }

            if (!bGo)
            {   /* Not elevated: either the restart is under way (this      */
                /* window is closing and the new copy resumes at the same   */
                /* command), or it was declined or could not be started     */
                /* and the status bar says so.  Nothing more here.          */
                return;
            }

            if (Main_objCommandClient == null || Main_objCommandInputs == null)
            {   /* No command system, or its inputs, yet: refused with a   */
                /* reason -- the picker is shown only as its input.        */
                Main_SetStatus("The device-input command endpoint is not initialized.");
                return;
            }

            /* Said at once, since listing the disks can take a moment;   */
            /* whether the picker may open is the worker's to decide.     */
            Main_SetStatus("Looking for disks ...");

            /* ONE OpenDevice request, its device still to be asked: the       */
            /* worker's sequence lists the disks, asks the picker as its own   */
            /* question (DeviceChoice), then reads the table and opens or      */
            /* checks (Sys_DeviceOpenSequence).  Its purpose is fixed now:     */
            /* Open's, or Check's, which the worker admits only with nothing   */
            /* open and whose picker says it is for a check.  Posted as        */
            /* fresh user work; a failure comes back to the status line.       */
            objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.OpenDevice, 0, Main_objCommandClient.CLI_ReadState(),
                null, null, 0, 0, new DOA_clsDeviceOpenArgs(bCheck, true, ""), "/", "");
            Main_objCommandClient.CLI_Post(objRequest, Main_ReportCommandFailure, true);
        }

        /*--------------------------------------------------------------------*/
        /* Main_RestartElevated:                                              */
        /*                                                                    */
        /* Start the copy of the program that runs as Administrator, the      */
        /* restart's last step: called when the worker's Exit, having closed  */
        /* everything, asks the window to launch it (MainCommand.cs, the      */
        /* restart question's answer) -- never straight from a click.         */
        /* Windows shows its own elevation prompt meanwhile.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sCommand : the start-up command the new copy runs, without its */
        /*                dashes.                                             */
        /*     sDisk    : the disk already chosen and warned about, which the */
        /*                new copy takes as --disk; "" for none.              */
        /*     sReason  : receives why it was not started; "" when it was.    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when Windows started the copy (this one may then   */
        /*            end); false keeps this copy running.                    */
        /*--------------------------------------------------------------------*/
        private bool Main_RestartElevated(string sCommand, string sDisk, out string sReason)
        {
            ProcessStartInfo psiElevated;
            Process procThis;
            Process procStarted;
            int iThisPid;
            bool bStarted;

            /* This copy's process id, for the new copy's --after: it waits   */
            /* for this one to end before it opens the debug log              */
            /* (APP_WaitForExit, Program.cs).  Nothing else of this copy's    */
            /* state is handed over -- the chosen disk, below, is the         */
            /* request's.                                                     */
            sReason = "";
            using (procThis = Process.GetCurrentProcess())
            {   /* The id kept; the process object released at the block's   */
                /* end.                                                      */
                iThisPid = procThis.Id;
            }

            /* The program itself, through the shell with the "runas" verb   */
            /* -- what asks Windows for elevation -- with the command and    */
            /* --after this copy's id.                                       */
            psiElevated = new ProcessStartInfo();
            psiElevated.FileName = Application.ExecutablePath;
            psiElevated.Arguments = "--" + sCommand + " --after " + iThisPid;
            psiElevated.UseShellExecute = true;
            psiElevated.Verb = "runas";
            if (sDisk.Length != 0)
            {   /* A disk chosen and warned about before the restart: the new   */
                /* copy takes it as --disk and asks neither again.  Quoted as   */
                /* one argument; a \\.\PhysicalDriveN path ends in a digit,     */
                /* so no backslash comes before the closing quote to escape     */
                /* it.                                                          */
                psiElevated.Arguments += " --disk \"" + sDisk + "\"";
            }

            if (APP_clsProgram.APP_ReadLogPath().Length != 0)
            {   /* This copy keeps a debug log: so does the new one. */
                psiElevated.Arguments += " --debug";
            }

            /* Whether it started is decided by the start alone -- a failure   */
            /* releasing the process object afterwards does not undo it, or    */
            /* the Exit would stop with both copies running.                   */
            procStarted = null;
            bStarted = false;
            try
            {   /* The start: Windows may refuse the elevation (a No at its   */
                /* prompt) or fail to make the process -- both throw.         */
                procStarted = Process.Start(psiElevated);
                if (procStarted == null)
                {   /* No process object back: not taken as started, so     */
                    /* this copy stays.  When the shell answers so is not   */
                    /* established.                                         */
                    sReason = "Windows did not return a started process.";
                }
                else
                {   /* Started: this copy may end. */
                    bStarted = true;
                }
            }
            catch (Exception exStart)
            {   /* Refused or failed: logged, the reason given back.  The   */
                /* Exit then stops and this copy goes on -- with what it    */
                /* had closed for the restart still closed.                 */
                sReason = exStart.Message;
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("app", "elevated restart refused: " + exStart.Message);
                }
            }
            finally
            {   /* The process object released, the new copy untouched. */
                if (procStarted != null)
                {   /* There is one. */
                    try
                    {   /* Released.  The new copy runs on regardless. */
                        procStarted.Dispose();
                    }
                    catch (Exception exRelease)
                    {   /* Only logged: the start stands. */
                        if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                        {   /* Only while the debug log is kept: otherwise the line is never built. */
                            DBGLOG_clsDebugLog.DBGLOG_Write("app", "elevated process handle cleanup failed: " + exRelease.Message);
                        }
                    }
                }
            }

            /* Whether Windows started it; the worker's Exit answers Yes or   */
            /* No from this, and only a Yes goes on to Stopped.               */
            return(bStarted);
        }

        /*--------------------------------------------------------------------*/
        /* Main_CheckTargetText:                                              */
        /*                                                                    */
        /* How the check window and its messages name what is being checked:  */
        /* a device by its model, size, bus and path, a file by its path,     */
        /* and the partition's offset when the filesystem is not at the       */
        /* start.                                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the description.                                      */
        /*--------------------------------------------------------------------*/
        private string Main_CheckTargetText()
        {
            string sText;

            if (Main_sCheckDeviceText.Length != 0)
            {   /* A real device: its captured description (model, size, bus   */
                /* and path), which says                                       */
                /* far more than \\.\PhysicalDriveN alone would.               */
                sText = Main_sCheckDeviceText;
            }
            else
            {   /* An image file: its path is the clearest name it has. */
                sText = Main_sCheckFile;
            }

            if (Main_ullCheckOffset != 0)
            {   /* A partition inside it: say where it starts, in the         */
                /* program's size wording, since one disk can hold several.   */
                sText = sText + " at offset " + Main_FormatSize(Main_ullCheckOffset);
            }

            /* The description, for the check window's heading, the status   */
            /* line and the repair question alike.                           */
            return(sText);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnCheckFsClicked:                                             */
        /*                                                                    */
        /* File > Check Filesystem, with an image or device open: its check   */
        /* posted as one command.  The worker closes the editors and the      */
        /* session, opens the check window and runs the report-only check;    */
        /* nothing about the session is changed here.                         */
        /*                                                                    */
        /* The check must also reach a filesystem that will not open at all   */
        /* -- one lwext4 refuses to mount is the very case e2fsck is for --   */
        /* so with NOTHING open the item is a sub-menu instead (Image File... */
        /* / Device..., Main_OnFileMenuOpening), whose entries pick the       */
        /* target with the Open pickers (Main_OnCheckImageChoice,             */
        /* Main_OnCheckDeviceChoice).                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item.                                     */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the check is posted, or the status line says why not;   */
        /*            nothing at all while the item is the sub-menu.          */
        /*--------------------------------------------------------------------*/
        private void Main_OnCheckFsClicked(object objSender, EventArgs evArgs)
        {
            TGT_clsTarget objTarget;

            if (Main_tsmCheckFs.HasDropDownItems)
            {   /* Nothing open, so the item is the sub-menu's parent: a       */
                /* click on it only opens Image File... / Device..., which     */
                /* the framework does by itself.  It still raises Click, and   */
                /* posting a check from it would only put "no target" on the   */
                /* status line, so nothing is done.                            */
                return;
            }

            /* The open's target, from the worker's last published state;     */
            /* null before the command system starts or with nothing open.    */
            /* The worker checks it is still the open one when it takes the   */
            /* request, then asks, closes, checks and -- when the window      */
            /* closes -- reopens it read-only.                                */
            objTarget = null;
            if (Main_objCommandClient != null)
            {   /* The state record's target (it is never changed). */
                objTarget = Main_objCommandClient.CLI_ReadState().STV_objTarget;
            }

            /* Posted (Main_PostCheckIntent, which says why when there is   */
            /* no target or no command system).                             */
            Main_PostCheckIntent(objTarget, false);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnCheckImageChoice:                                           */
        /*                                                                    */
        /* Check Filesystem > Image File..., with nothing open: the Open      */
        /* Image File picker -- the very same steps, the partition dialog for */
        /* a whole-disk dump included -- with its purpose set to checking, so */
        /* the file chosen is checked rather than opened, as one command.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item.                                     */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the file input is posted, or the status line says why   */
        /*            not.                                                    */
        /*--------------------------------------------------------------------*/
        private void Main_OnCheckImageChoice(object objSender, EventArgs evArgs)
        {
            /* The picker, for a check (true).  The risk question is the       */
            /* check's own, asked by the worker once the file is chosen; the   */
            /* click asks nothing.                                             */
            Main_PickImageFile(true);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnCheckDeviceChoice:                                          */
        /*                                                                    */
        /* Check Filesystem > Device..., with nothing open: the Open Device   */
        /* picker, with its purpose set to checking -- its own restart as     */
        /* Administrator included, which resumes at --check-device -- so the  */
        /* device chosen is checked, as one command.                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item.                                     */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the picker is asked for, a restart asked for, or the    */
        /*            status line says why not.                               */
        /*--------------------------------------------------------------------*/
        private void Main_OnCheckDeviceChoice(object objSender, EventArgs evArgs)
        {
            /* The device picker, for a check (true).  As for an image file,   */
            /* the risk question is the check's own, asked once the device     */
            /* is chosen.                                                      */
            Main_PickDevice(true);
        }

        /*--------------------------------------------------------------------*/
        /* Main_RunCheck:                                                     */
        /*                                                                    */
        /* Run the check again on what the check window was opened for -- in  */
        /* practice, its Repair: posted to the worker, which asks the repair  */
        /* question and runs it; the window shows the run as its views come.  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bRepair : true to fix what is found, false to report only.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the run is posted, or the status line says why not.     */
        /*--------------------------------------------------------------------*/
        private void Main_RunCheck(bool bRepair)
        {
            /* The window's own range (Main_objCommandCheckTarget), not    */
            /* anything the main window shows now; the request names the   */
            /* window too, and the worker refuses it if either no longer   */
            /* matches (Main_PostCheckIntent, MainCommand.cs).             */
            Main_PostCheckIntent(Main_objCommandCheckTarget, bRepair);
        }

        /*--------------------------------------------------------------------*/
        /* Main_ShowCheckResult:                                              */
        /*                                                                    */
        /* Show a finished check run's verdict: the check window takes the    */
        /* report, which also ends its running state -- it may be closed      */
        /* again, and Repair can be offered -- and the status line says the   */
        /* run is over and, when an open image was closed for the check,      */
        /* that closing the window reopens it.                                */
        /*                                                                    */
        /* Its one caller is Main_RenderCommandCheckView (MainCommand.cs),    */
        /* on the UI thread, when the CheckFinished view of the run the       */
        /* window is showing arrives, its result already shaped by            */
        /* Main_CheckWindowResult.  A run with no window to show it in is     */
        /* dropped (a guard: no known path reaches it).                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objResult : the run's finished report and verdict.             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window shows the verdict, or there is no window     */
        /*            to show it in.                                          */
        /*--------------------------------------------------------------------*/
        private void Main_ShowCheckResult(ECK_clsCheckResult objResult)
        {
            if (Main_frmCheck == null || Main_frmCheck.IsDisposed)
            {   /* No window to show it in.  A guard: the window refuses to   */
                /* close while its run is in progress (CheckForm.cs,          */
                /* Chk_OnFormClosing), so no known path reaches here.         */
                return;
            }

            /* The verdict into the window, which also leaves its running    */
            /* state, so it may be closed again and Repair can be offered.   */
            Main_frmCheck.CHK_Finish(objResult);

            if (Main_bCheckReopen)
            {   /* An open image was closed for the check: say how it comes back. */
                Main_SetStatus("The check of " + Main_CheckTargetText() + " has finished; close the check window to reopen the image.");
            }
            else
            {   /* Picked with nothing open: nothing comes back. */
                Main_SetStatus("The check of " + Main_CheckTargetText() + " has finished.");
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnCheckRepair:                                                */
        /*                                                                    */
        /* The check window's Repair button: the repair posted for the range  */
        /* that window was opened for (Main_RunCheck).  The worker's question */
        /* names that range before anything is written.                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmCheck : the window whose button it was.                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the repair is posted, or nothing for a window that is   */
        /*            not the current one.                                    */
        /*--------------------------------------------------------------------*/
        private void Main_OnCheckRepair(CHK_clsCheckForm frmCheck)
        {
            if (frmCheck != Main_frmCheck)
            {   /* Not the check window this window knows (an older one's     */
                /* late click): ignored.  The worker also checks the window   */
                /* when it takes a repair.                                    */
                return;
            }

            /* Posted; the question and the run are the worker's. */
            Main_RunCheck(true);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnCheckClosed:                                                */
        /*                                                                    */
        /* The check window has closed (its FormClosed): the window's end     */
        /* told to the worker, which leaves check mode and reopens a          */
        /* filesystem the check closed (Main_EndCommandCheckView,             */
        /* MainCommand.cs).  Nothing is reopened here.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the check window.                                  */
        /*     evClosed  : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : CheckClosed is posted, once for that window.            */
        /*--------------------------------------------------------------------*/
        private void Main_OnCheckClosed(object objSender, FormClosedEventArgs evClosed)
        {
            /* The same end as every other way a check window can go (its   */
            /* watch finding it gone, a window that could not be shown):    */
            /* the routing cleared, then CheckClosed posted, once.          */
            Main_EndCommandCheckView(objSender as Form);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnResize:                                                     */
        /*                                                                    */
        /* The window changed size: recompute the divider and the columns.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the form.                                          */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the layout fits the new size.                           */
        /*--------------------------------------------------------------------*/
        private void Main_OnResize(object objSender, EventArgs evArgs)
        {
            /* The divider and the columns refitted to the new size. */
            Main_ApplyLayout();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnOpenImageClicked:                                           */
        /*                                                                    */
        /* File > Open Image: the image-file picker, for opening.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item.                                     */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : an open request is posted, or a dialog was cancelled.   */
        /*--------------------------------------------------------------------*/
        private void Main_OnOpenImageClicked(object objSender, EventArgs evArgs)
        {
            /* The shared image-file picker, for opening (false: not for   */
            /* a check).                                                   */
            Main_PickImageFile(false);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnChoiceImageClicked:                                         */
        /*                                                                    */
        /* The image path icon's open menu (nothing open) > Open Image File:  */
        /* the same picker as File > Open > Image File, from a handler of its */
        /* own.                                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : an open request is posted, or a dialog was cancelled.   */
        /*--------------------------------------------------------------------*/
        private void Main_OnChoiceImageClicked(object objSender, EventArgs evArgs)
        {
            /* The image-file picker, for opening (false: not for a check). */
            Main_PickImageFile(false);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnChoiceDeviceClicked:                                        */
        /*                                                                    */
        /* The image path icon's open menu (nothing open) > Open Device: the  */
        /* same picker as File > Open > Device, its restart as Administrator  */
        /* included, from a handler of its own.                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the picker is asked for, a restart asked for, or the    */
        /*            status line says why not.                               */
        /*--------------------------------------------------------------------*/
        private void Main_OnChoiceDeviceClicked(object objSender, EventArgs evArgs)
        {
            /* The device picker, for opening (false: not for a check). */
            Main_PickDevice(false);
        }

        /*--------------------------------------------------------------------*/
        /* Main_PickImageFile:                                                */
        /*                                                                    */
        /* Ask for an image file -- a filesystem image or a dump of a whole   */
        /* disk, the user need not know which -- and hand it to               */
        /* Main_OpenImageFile, which tells the two apart and ends in an open  */
        /* or, for Check Filesystem with nothing open, a check.  The file     */
        /* dialog starts where every image-file dialog starts                 */
        /* (Main_ImageFolder), and the folder chosen becomes that place.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bCheck : true to check what is chosen rather than open it.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the file input is posted, or the status line says why   */
        /*            not; the dialog opens when the worker admits it.        */
        /*--------------------------------------------------------------------*/
        private void Main_PickImageFile(bool bCheck)
        {
            INK_enmInputKind enmPurpose;

            if (Main_objCommandClient == null || Main_objCommandInputs == null)
            {   /* No command system, or its inputs, yet: refused with a   */
                /* reason -- the file dialog opens only as an input.       */
                Main_SetStatus("The image filename input is not ready.");
                return;
            }

            /* The purpose, fixed now: one dialog for both, but Open's and   */
            /* Check's inputs are admitted by different rules (Check only    */
            /* with nothing open).                                           */
            enmPurpose = INK_enmInputKind.HostOpenImageFile;
            if (bCheck)
            {   /* For Check Filesystem. */
                enmPurpose = INK_enmInputKind.HostCheckImageFile;
            }

            /* Posted, the dialog starting in the image folder. */
            Main_PostMainInput(enmPurpose, Main_ImageFolder(""), null);
        }

        /*--------------------------------------------------------------------*/
        /* Main_ImageFolder:                                                  */
        /*                                                                    */
        /* The folder every image-file dialog starts in -- Open and Check     */
        /* (Main_PickImageFile), Partition/Format > Image File, New Image     */
        /* File, Save Device to Image File and Write Image File to Device --  */
        /* so that they all start from ONE last-used place rather than each   */
        /* from its own: the folder of the image file last chosen in any of   */
        /* them (Main_RememberImageFolder).                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sFallback : the folder to give when nothing usable is          */
        /*                 remembered ("" lets Windows choose).               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : an existing folder, or sFallback.                     */
        /*--------------------------------------------------------------------*/
        private string Main_ImageFolder(string sFallback)
        {
            string sFolder;
            string sLastImage;

            /* The shared last-used folder (SET_IMAGE_FOLDER). */
            sFolder = SET_Settings.SET_ReadString(SET_Settings.SET_IMAGE_FOLDER, "");

            if (sFolder.Length == 0)
            {   /* Never written yet: the folder of the image last opened   */
                /* (SET_LAST_IMAGE) stands in, so settings that hold only   */
                /* that still start the dialogs where that image was.       */
                sLastImage = SET_Settings.SET_ReadString(SET_Settings.SET_LAST_IMAGE, "");

                if (sLastImage.Length != 0)
                {   /* One was opened: its folder (null for a bare root). */
                    sFolder = Path.GetDirectoryName(sLastImage);
                }
            }

            if (sFolder == null || sFolder.Length == 0 || !Directory.Exists(sFolder))
            {   /* Nothing remembered, or the folder has gone (a removed   */
                /* card, a renamed folder): passed over for the caller's   */
                /* fallback rather than handed to a dialog.                */
                return(sFallback);
            }

            /* The remembered folder, which exists: start there. */
            return(sFolder);
        }

        /*--------------------------------------------------------------------*/
        /* Main_RememberImageFolder:                                          */
        /*                                                                    */
        /* Record the folder of an image file just chosen (or made) in one of */
        /* the image-file dialogs, as the place every one of them starts next */
        /* (Main_ImageFolder).  Written when the file is chosen, not when an  */
        /* open succeeds: the last place the user went is the "last used      */
        /* location", whether or not the file then opened.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sImageFile : the file's full path.                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the setting holds the file's folder (a path with no     */
        /*            folder part leaves it as it was).                       */
        /*--------------------------------------------------------------------*/
        private void Main_RememberImageFolder(string sImageFile)
        {
            string sFolder;

            /* The file's folder: null for a root path, "" for a bare name. */
            sFolder = Path.GetDirectoryName(sImageFile);

            if (sFolder != null && sFolder.Length != 0)
            {   /* A folder to remember, in the shared setting every image-   */
                /* file dialog reads (Main_ImageFolder).                      */
                SET_Settings.SET_WriteString(SET_Settings.SET_IMAGE_FOLDER, sFolder);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_OpenImageFile:                                                */
        /*                                                                    */
        /* Open an image FILE named by the user -- from File > Open Image or  */
        /* the command line -- working out for itself whether the file is a   */
        /* filesystem or a dump of a whole disk, so the user need not know    */
        /* which kind of file they have.  For a CHECK (bCheck) the same file  */
        /* is checked instead.  Either way it is ONE command, posted here:    */
        /* the worker looks at the file and its partition is chosen BEFORE    */
        /* anything open is closed (CommandSequences.cs,                      */
        /* Sys_ImageFileSequence).  Called by the file input's answer         */
        /* (MainCommand.cs) and, for an image named on the command line, by   */
        /* Main_OnShown.                                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sImageFile : the file's path.                                  */
        /*     bCheck     : true to check the filesystem rather than open it. */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the command is posted, or the status line says why not; */
        /*            the views that follow show its outcome.                 */
        /*--------------------------------------------------------------------*/
        private void Main_OpenImageFile(string sImageFile, bool bCheck)
        {
            RQT_clsRequest objRequest;
            TGT_clsTarget objTargetFile;
            string sFullPath;

            if (Main_objCommandClient == null)
            {   /* No command system yet: refused with a reason -- nothing   */
                /* is read here.                                             */
                Main_SetStatus("The image command system is not ready.");
                return;
            }

            /* The path made full NOW: a relative one from the command line   */
            /* means the folder the program started in, and a later Windows   */
            /* dialog may change the current folder before the worker reads   */
            /* the file.  Only the name is worked on here.                    */
            try
            {   /* A path the framework cannot make full throws. */
                sFullPath = Path.GetFullPath(sImageFile);
            }
            catch (Exception exPath)
            {   /* Not a usable path: said, nothing posted. */
                Main_SetStatus("Cannot select that image file: " + exPath.Message);
                return;
            }

            /* The target: the whole file -- offset and length 0, no device   */
            /* description, 512-byte sectors -- for the worker to inspect;    */
            /* it finds the partition (or asks), asks what needs asking and   */
            /* opens or checks.  Posted on the main window's image-file       */
            /* stream, the root to list after an open.                        */
            objTargetFile = new TGT_clsTarget(sFullPath, 0, 0, "", 0, 512);
            objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.OpenImageFile, 0,
                Main_objCommandClient.CLI_ReadState(), objTargetFile, null, 0, 0,
                new IFA_clsImageFileArgs(bCheck), "/", "main-image-file");
            /* Only a failure comes back here, to the status line; success   */
            /* is shown by the state, open and folder views that follow.     */
            Main_objCommandClient.CLI_Post(objRequest, Main_ReportCommandFailure);
        }
        /*--------------------------------------------------------------------*/
        /* Main_OnNewImageClicked:                                            */
        /*                                                                    */
        /* File > New Image File: the New Image request                       */
        /* (Main_RequestNewImage).                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the New Image request is posted.                        */
        /*--------------------------------------------------------------------*/
        private void Main_OnNewImageClicked(object objSender, EventArgs evArgs)
        {
            /* The same request the Windows pane's Create Image button makes. */
            Main_RequestNewImage();
        }

        /*--------------------------------------------------------------------*/
        /* Main_RequestNewImage:                                              */
        /*                                                                    */
        /* New Image File: ONE NewImage request posted, carrying only where   */
        /* its dialog starts -- the image folder, or the Windows pane's       */
        /* folder when none is remembered.  The worker's sequence asks the    */
        /* file and its size (NewImageForm.cs, as its own questions), leaves  */
        /* the open filesystem, makes the file and opens the partition window */
        /* over it, as one command; nothing is shown, made or closed here.    */
        /* File > New Image File's handler and the Windows pane's Create      */
        /* Image button call it.                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the request is posted, or the status line says why not. */
        /*--------------------------------------------------------------------*/
        private void Main_RequestNewImage()
        {
            RQT_clsRequest objRequest;

            if (Main_objCommandClient == null)
            {   /* No command system yet: refused with a reason. */
                Main_SetStatus("The New Image command endpoint is not initialized.");
                return;
            }

            /* The request, from the main window (origin 0) under the state    */
            /* now, naming no file and carrying the dialog's start folder as   */
            /* its payload (CFC_Create takes the state's target in place of    */
            /* the missing one; it is never read as the new file).  The        */
            /* worker replaces the target, and sets the replace choice, once   */
            /* the dialog is accepted (Sys_AskNewImageFile,                    */
            /* CommandSequences.cs).                                           */
            /* Posted as fresh user work, which the worker refuses while       */
            /* busy; the outcome, a cancel included, comes back to             */
            /* Main_OnNewImageResult (MainCommand.cs).                         */
            objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.NewImage, 0, Main_objCommandClient.CLI_ReadState(),
                null, null, 0, 0, new NIQ_clsNewImageQuestion(Main_ImageFolder(Main_sHostDir), false, "", 0, 0), "", "");
            Main_objCommandClient.CLI_Post(objRequest, Main_OnNewImageResult, true);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnPartitionFileClicked:                                       */
        /*                                                                    */
        /* File > Partition/Format > Image File: one request whose sequence   */
        /* asks the disk image (starting in the image folder), asks to close  */
        /* whatever is open, then opens the partition window over the file.   */
        /* Nothing is read or shown here.                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the request is posted, or the status line says why not. */
        /*--------------------------------------------------------------------*/
        private void Main_OnPartitionFileClicked(object objSender, EventArgs evArgs)
        {
            /* Posted as fresh user work (Main_PostPartitionFile, which says   */
            /* why when it cannot be); the file dialog is the request's own    */
            /* question, shown when the worker asks it.                        */
            Main_PostPartitionFile();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnPartitionDeviceClicked:                                     */
        /*                                                                    */
        /* File > Partition/Format > Device: one request whose sequence lists */
        /* the disks, asks which one, gives the risk warning naming it, and   */
        /* only then -- in a copy without Administrator rights -- restarts as */
        /* Administrator with that disk, or otherwise asks to close whatever  */
        /* is open and opens the partition window over it.  No elevation is   */
        /* asked for here: a No to the disk or the warning then changes       */
        /* nothing at all.                                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the request is posted, or the status line says why not. */
        /*--------------------------------------------------------------------*/
        private void Main_OnPartitionDeviceClicked(object objSender, EventArgs evArgs)
        {
            if (Main_objCommandClient == null)
            {   /* No command system yet: refused with a reason. */
                Main_SetStatus("The partition command endpoint is not initialized.");
                return;
            }

            /* Said at once, since listing the disks takes a moment; then      */
            /* ONE OpenPartition request whose sequence asks the disk as its   */
            /* own question, and everything after it, in order                 */
            /* (Sys_OpenPartitionOperation), posted as fresh user work; a      */
            /* failure comes back to the status line.                          */
            Main_SetStatus("Looking for disks ...");
            Main_objCommandClient.CLI_Post(CFC_clsCommands.CFC_Create(KND_enmKind.OpenPartition, 0,
                Main_objCommandClient.CLI_ReadState(), null, null, 0, 0,
                new POA_clsPartitionOpenArgs("", false, true, ""), "", ""), Main_ReportCommandFailure, true);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnCloseImageClicked:                                          */
        /*                                                                    */
        /* File > Close Image/Device: close the open image or device and      */
        /* empty the left pane.  The image pane's Close button posts the same */
        /* close (Main_OnCloseButtonClicked).                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item.                                     */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the close is posted; the pane empties when the worker's */
        /*            state says nothing is open.                             */
        /*--------------------------------------------------------------------*/
        private void Main_OnCloseImageClicked(object objSender, EventArgs evArgs)
        {
            /* Posted as one command: the worker asks about unsaved editors   */
            /* on the image, closes them and the session.  The rows and the   */
            /* title stay until its views say the image has closed -- so a    */
            /* Cancel leaves everything as it was.                            */
            Main_PostLifecycleCommand(KND_enmKind.Close);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnExitClicked:                                                */
        /*                                                                    */
        /* File > Exit: the program's way out (Main_RequestCommandExit).  The */
        /* worker asks when there is work to lose, closes everything in       */
        /* order, and only then does the window close; nothing is closed on   */
        /* this click.                                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the Exit is posted.                                     */
        /*--------------------------------------------------------------------*/
        private void Main_OnExitClicked(object objSender, EventArgs evArgs)
        {
            /* Posted; the window stays up to answer the worker's questions   */
            /* until it has stopped.                                          */
            Main_RequestCommandExit();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnAboutClicked:                                               */
        /*                                                                    */
        /* Help > About: the request posted to the window's message loop,     */
        /* which shows the About window once the click has returned           */
        /* (Main_ShowAbout).                                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the request posted.                                     */
        /*--------------------------------------------------------------------*/
        private void Main_OnAboutClicked(object objSender, EventArgs evArgs)
        {
            /* Posted, as the Places pull-down is: the window is made and     */
            /* shown from the loop, never on the stack of the menu's click.   */
            BeginInvoke(new MethodInvoker(Main_ShowAbout));
        }

        /*--------------------------------------------------------------------*/
        /* Main_ShowAbout:                                                    */
        /*                                                                    */
        /* Show the About window, modeless and with no owner.  If it is       */
        /* already open it is brought to the front instead of a second one    */
        /* being made; the reference is dropped when it closes.  Run from the */
        /* message loop, posted by Help > About's handler.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the About window is showing.                            */
        /*--------------------------------------------------------------------*/
        private void Main_ShowAbout()
        {
            if (Main_frmAbout != null)
            {   /* Already open: bring it forward. */
                Main_frmAbout.Activate();
                return;
            }

            /* A new one, under the program's icon, its reference dropped   */
            /* when it closes so the next Help > About makes a fresh one;   */
            /* shown modeless, with no owner, so it can stay open beside    */
            /* the main window.                                             */
            Main_frmAbout             = new ABT_clsAboutForm();
            Main_frmAbout.Icon        = this.Icon;
            Main_frmAbout.FormClosed += Main_OnAboutClosed;
            Main_frmAbout.Show();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnDebugLogClicked:                                            */
        /*                                                                    */
        /* Help > Debug Log: the request posted to the window's message loop, */
        /* which shows the log's window once the click has returned           */
        /* (Main_ShowDebugLog).                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the request posted.                                     */
        /*--------------------------------------------------------------------*/
        private void Main_OnDebugLogClicked(object objSender, EventArgs evArgs)
        {
            /* Posted, as About is: the status line and the window are set   */
            /* from the loop, never on the stack of the menu's click.        */
            BeginInvoke(new MethodInvoker(Main_ShowDebugLog));
        }

        /*--------------------------------------------------------------------*/
        /* Main_ShowDebugLog:                                                 */
        /*                                                                    */
        /* Show this run's debug log in its own window, modeless and with no  */
        /* owner, so it can stay open and keep following the log while the    */
        /* main window is used.  If it is already open it is brought to the   */
        /* front.                                                             */
        /*                                                                    */
        /* When no log is being kept the window still opens, says there is no */
        /* log file, and the status bar says how to get one.  Help > Debug    */
        /* Log is on the menu only in a DEBUG build, which always logs, so    */
        /* the one way to meet that case from the menu is a log file that     */
        /* could not be opened.  Run from the message loop, posted by Help >  */
        /* Debug Log's handler.                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the debug log window is showing.                        */
        /*--------------------------------------------------------------------*/
        private void Main_ShowDebugLog()
        {
            if (APP_clsProgram.APP_ReadLogPath().Length == 0)
            {   /* No log this run (App_StartLog left the path ""): say how   */
                /* to have one next time.  The window still opens below.      */
                Main_SetStatus("No debug log is being kept. Start BifrostFM with --debug to keep one.");
            }

            if (Main_frmDebugLog != null)
            {   /* Already open: bring it forward. */
                Main_frmDebugLog.Activate();
                return;
            }

            /* A new one on this run's log path -- "" shows "(no log file    */
            /* found)" (lib\DebugLogForm.cs) -- with the program's name in   */
            /* its caption and its icon, its reference dropped when it       */
            /* closes, and shown modeless with no owner so it can keep       */
            /* following the log while the main window is used.              */
            Main_frmDebugLog                     = new DBGLOGF_clsDebugLogForm(APP_clsProgram.APP_ReadLogPath());
            Main_frmDebugLog.DBGLOGF_FormCaption = "BifrostFM Debug Log";
            Main_frmDebugLog.Icon                = this.Icon;
            Main_frmDebugLog.FormClosed         += Main_OnDebugLogClosed;
            Main_frmDebugLog.Show();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnAboutClosed:                                                */
        /*                                                                    */
        /* The About window closed: its reference dropped, so the next Help > */
        /* About makes a fresh one rather than activating a closed window.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the About window (unused).                         */
        /*     evClosed  : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Main_frmAbout is null.                                  */
        /*--------------------------------------------------------------------*/
        private void Main_OnAboutClosed(object objSender, FormClosedEventArgs evClosed)
        {
            /* No About window open now. */
            Main_frmAbout = null;
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnDebugLogClosed:                                             */
        /*                                                                    */
        /* The debug log window closed: its reference dropped, so the next    */
        /* Help > Debug Log makes a fresh one.                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the debug log window (unused).                     */
        /*     evClosed  : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Main_frmDebugLog is null.                               */
        /*--------------------------------------------------------------------*/
        private void Main_OnDebugLogClosed(object objSender, FormClosedEventArgs evClosed)
        {
            /* No debug log window open now. */
            Main_frmDebugLog = null;
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnRefreshClicked:                                             */
        /*                                                                    */
        /* View > Refresh (F5): read both panes again.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item.                                     */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : both panes are re-read.                                 */
        /*--------------------------------------------------------------------*/
        private void Main_OnRefreshClicked(object objSender, EventArgs evArgs)
        {
            /* One pane action for both panes' folders as they are now: the   */
            /* Windows pane re-read, and the image pane when something is     */
            /* open.                                                          */
            Main_RequestPaneAction(HPA_enmHostAction.RefreshBoth, null, Main_sHostDir, Main_sImageDir);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnCtxRefreshClicked:                                          */
        /*                                                                    */
        /* The right-click menu's Refresh: both panes read again, as View >   */
        /* Refresh does, from a handler of its own.                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : both panes are re-read.                                 */
        /*--------------------------------------------------------------------*/
        private void Main_OnCtxRefreshClicked(object objSender, EventArgs evArgs)
        {
            /* One pane action for both panes' folders as they are now: the   */
            /* Windows pane re-read, and the image pane when something is     */
            /* open.                                                          */
            Main_RequestPaneAction(HPA_enmHostAction.RefreshBoth, null, Main_sHostDir, Main_sImageDir);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnRefreshButtonClicked:                                       */
        /*                                                                    */
        /* The shared toolbar's Refresh: both panes read again, as View >     */
        /* Refresh does, from a handler of its own.                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : both panes are re-read.                                 */
        /*--------------------------------------------------------------------*/
        private void Main_OnRefreshButtonClicked(object objSender, EventArgs evArgs)
        {
            /* One pane action for both panes' folders as they are now: the   */
            /* Windows pane re-read, and the image pane when something is     */
            /* open.                                                          */
            Main_RequestPaneAction(HPA_enmHostAction.RefreshBoth, null, Main_sHostDir, Main_sImageDir);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnToImageButtonClicked:                                       */
        /*                                                                    */
        /* The shared toolbar's copy to the left: the Windows pane's          */
        /* selection imported into the image, as Commands > Import into Image */
        /* does (Main_RequestImport), from a handler of its own.              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the import is posted, or the status line says why not;  */
        /*            its report comes when the copy ends.                    */
        /*--------------------------------------------------------------------*/
        private void Main_OnToImageButtonClicked(object objSender, EventArgs evArgs)
        {
            /* The Windows pane's selection into the image's folder. */
            Main_RequestImport();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnToHostButtonClicked:                                        */
        /*                                                                    */
        /* The shared toolbar's copy to the right: the image pane's selection */
        /* extracted to Windows, as Commands > Extract to Windows does        */
        /* (Main_RequestExtract), from a handler of its own.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the extract is posted, or the status line says why not; */
        /*            its report comes when the copy ends.                    */
        /*--------------------------------------------------------------------*/
        private void Main_OnToHostButtonClicked(object objSender, EventArgs evArgs)
        {
            /* The image pane's selection out to the Windows folder. */
            Main_RequestExtract();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnImageItemActivated:                                         */
        /*                                                                    */
        /* A row of the image pane's list was activated (a double-click, or   */
        /* Enter): open it (Main_ActivateImageRow).                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the image list (unused).                           */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the selected row is being opened.                       */
        /*--------------------------------------------------------------------*/
        private void Main_OnImageItemActivated(object objSender, EventArgs evArgs)
        {
            /* The image pane's one selected row, opened as its kind calls for. */
            Main_ActivateImageRow();
        }

        /*--------------------------------------------------------------------*/
        /* Main_ActivateImageRow:                                             */
        /*                                                                    */
        /* Open the image pane's selected row: go up for "..", go into a      */
        /* directory, open a regular file in the window its content calls     */
        /* for, and for anything else -- a symbolic link, a device, a pipe -- */
        /* say what it is in the status bar.  A link is not followed: the     */
        /* engine never follows one.  The row is read here, when it is        */
        /* activated; reading a file out of the image is the worker's, once   */
        /* the open is posted (Main_OpenImageEntry).                          */
        /* The list's activation handler and Open (Main_RequestOpen) call it. */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the pane navigates, a file is being opened, or the      */
        /*            status bar describes the row.                           */
        /*--------------------------------------------------------------------*/
        private void Main_ActivateImageRow()
        {
            ListViewItem lviRow;
            EEN_stEntry    stEntry;

            if (Main_lstImage.SelectedItems.Count != 1)
            {   /* Activation acts on one row only (none, or several      */
                /* selected by the time the posted open runs): nothing.   */
                return;
            }

            /* The one selected row. */
            lviRow = Main_lstImage.SelectedItems[0];

            if (lviRow.Tag == null)
            {   /* The ".." row: up one level. */
                Main_NavigateImage(Main_ImageParentDir(Main_sImageDir));
                return;
            }

            /* Every other row carries its entry's facts in Tag      */
            /* (Main_ShowListResult), so its type is known without   */
            /* asking the engine again.                              */
            stEntry = (EEN_stEntry)lviRow.Tag;

            if (stEntry.EEN_uiType == ENG_clsNative.ENG_TYPE_DIR)
            {   /* A directory: go into it. */
                Main_NavigateImage(Main_JoinImagePath(Main_sImageDir, lviRow.Text));
                return;
            }

            if (stEntry.EEN_uiType == ENG_clsNative.ENG_TYPE_FILE)
            {   /* A regular file: open it in the window its content calls   */
                /* for (MAIN_OPEN_AUTO: Main_OpenImageEntry decides).        */
                Main_OpenImageEntry(Main_JoinImagePath(Main_sImageDir, lviRow.Text), stEntry, MAIN_OPEN_AUTO);
                return;
            }

            /* A link or special node: nothing to open, so it is described   */
            /* in the status bar -- its type, size, inode and link count.    */
            /* A link is not followed: the engine never follows one.         */
            Main_SetStatus(lviRow.Text + "   " + Main_TypeText(stEntry.EEN_uiType) + ", " + Main_FormatSize(stEntry.EEN_ullSize) +
                           " bytes, inode " + stEntry.EEN_uiInode + ", " + stEntry.EEN_uiLinks + " link(s)");
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnHostItemActivated:                                          */
        /*                                                                    */
        /* A row of the Windows pane's list was activated (a double-click, or */
        /* Enter): open it (Main_ActivateHostRow).                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the host list (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the selected row is being opened.                       */
        /*--------------------------------------------------------------------*/
        private void Main_OnHostItemActivated(object objSender, EventArgs evArgs)
        {
            /* The Windows pane's one selected row, opened as Explorer would. */
            Main_ActivateHostRow();
        }

        /*--------------------------------------------------------------------*/
        /* Main_ActivateHostRow:                                              */
        /*                                                                    */
        /* Open the Windows pane's selected row: go up for "..", go into a    */
        /* folder or drive, and hand a file to Windows to open with whatever  */
        /* program Windows has for it -- exactly what a double-click in       */
        /* Explorer does, including running the file when it is a program.    */
        /* The row is read here; the file is handed to Windows by its pane    */
        /* action once the worker admits it, after this event has returned,   */
        /* so nothing is launched inside the list's activation.               */
        /* The list's activation handler and Open (Main_RequestOpen) call it. */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the pane navigates, or Windows was asked to open the    */
        /*            file.                                                   */
        /*--------------------------------------------------------------------*/
        private void Main_ActivateHostRow()
        {
            ListViewItem  lviRow;
            DirectoryInfo dirParent;

            if (Main_lstHost.SelectedItems.Count != 1)
            {   /* Activation acts on one row only (none, or several      */
                /* selected by the time the posted open runs): nothing.   */
                return;
            }

            /* The one selected row. */
            lviRow = Main_lstHost.SelectedItems[0];

            if (lviRow.Tag == null)
            {   /* The ".." row: the parent folder, or the drives above a root.   */
                /* The parent as the framework works it out: null for a           */
                /* drive's root, which has none.                                  */
                dirParent = Directory.GetParent(Main_sHostDir);

                if (dirParent == null)
                {   /* Already at a drive's root: go up to the list of drives. */
                    Main_RequestPaneAction(HPA_enmHostAction.Navigate, null, "");
                }
                else
                {   /* An ordinary parent folder. */
                    Main_RequestPaneAction(HPA_enmHostAction.Navigate, null, dirParent.FullName);
                }

                /* Posted either way; the pane moves when the action runs. */
                return;
            }

            if (lviRow.Tag is string)
            {   /* A folder or a drive: its Tag is the path to go to. */
                Main_RequestPaneAction(HPA_enmHostAction.Navigate, null, (string)lviRow.Tag);
                return;
            }

            /* A file: posted as the pane action that hands it to Windows,   */
            /* which opens it with its own program once the worker admits    */
            /* the action -- not here, inside the list's event.              */
            Main_RequestPaneAction(HPA_enmHostAction.OpenShell,
                new string[] { ((FileInfo)lviRow.Tag).FullName }, Main_sHostDir);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnOpenClicked:                                                */
        /*                                                                    */
        /* Commands > Open: the same as a double-click on the active pane's   */
        /* one selected row.                                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the open is requested (Main_RequestOpen).               */
        /*--------------------------------------------------------------------*/
        private void Main_OnOpenClicked(object objSender, EventArgs evArgs)
        {
            /* The one function both Open handlers call. */
            Main_RequestOpen();
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnCtxOpenClicked:                                             */
        /*                                                                    */
        /* The right-click menu's Open: the same open, from a handler of its  */
        /* own.                                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the open is requested (Main_RequestOpen).               */
        /*--------------------------------------------------------------------*/
        private void Main_OnCtxOpenClicked(object objSender, EventArgs evArgs)
        {
            /* The one function both Open handlers call. */
            Main_RequestOpen();
        }

        /*--------------------------------------------------------------------*/
        /* Main_RequestOpen:                                                  */
        /*                                                                    */
        /* Open the active pane's one selected row, as its pane's             */
        /* double-click does.                                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the row is opened, as its pane opens it.                */
        /*--------------------------------------------------------------------*/
        private void Main_RequestOpen()
        {
            if (Main_ActiveList().SelectedItems.Count != 1)
            {   /* Open acts on one row. */
                Main_SetStatus("Select one item to open.");
                return;
            }

            if (Main_bHostPaneActive)
            {   /* The Windows pane: what its list's activation does. */
                Main_ActivateHostRow();
            }
            else
            {   /* The image pane: likewise. */
                Main_ActivateImageRow();
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_NewOpenAsMenu:                                                */
        /*                                                                    */
        /* Create an Open As sub-menu: Text and Hex, then the structured      */
        /* editor's formats in their menu order (Main_aiEditorFormats) --     */
        /* each format's item greyed should its module ever be missing        */
        /* (VoorheesFormat.Vf_IsAvailable).  Every item's Tag is the          */
        /* MAIN_OPEN_* value that names its window, so ONE handler serves     */
        /* them all (Main_OnOpenAsClicked).  Called twice: a menu item has    */
        /* one parent, so the Commands menu and the right-click menu each     */
        /* get a sub-menu of their own.                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ToolStripMenuItem : the sub-menu, filled.                      */
        /*--------------------------------------------------------------------*/
        private ToolStripMenuItem Main_NewOpenAsMenu()
        {
            ToolStripMenuItem tsmOpenAs;
            ToolStripMenuItem tsmItem;
            int               i;

            /* The sub-menu, with the two editors every file can be shown   */
            /* in -- Text and Hex -- first, set off from the structured     */
            /* formats by a separator.                                      */
            tsmOpenAs = new ToolStripMenuItem("Open &As");
            tsmOpenAs.DropDownItems.Add(Main_NewOpenAsItem("&Text", MAIN_OPEN_TEXT));
            tsmOpenAs.DropDownItems.Add(Main_NewOpenAsItem("&Hex", MAIN_OPEN_HEX));
            tsmOpenAs.DropDownItems.Add(new ToolStripSeparator());

            for (i = 0; i < Main_aiEditorFormats.Length; i++)
            {   /* One structured-editor format per iteration: its item,    */
                /* named from the parallel Main_asEditorTexts and tagged    */
                /* MAIN_OPEN_EDITOR plus the format's id, greyed when its   */
                /* module is missing, then added in the table's order.      */
                tsmItem         = Main_NewOpenAsItem(Main_asEditorTexts[i], MAIN_OPEN_EDITOR + Main_aiEditorFormats[i]);
                tsmItem.Enabled = VoorheesFormat.Vf_IsAvailable(Main_aiEditorFormats[i]);
                tsmOpenAs.DropDownItems.Add(tsmItem);
            }

            /* The caller adds it to its menu. */
            return(tsmOpenAs);
        }

        /*--------------------------------------------------------------------*/
        /* Main_NewOpenAsItem:                                                */
        /*                                                                    */
        /* Create one Open As item: its text, the window it names in its Tag, */
        /* and the shared handler.                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sText : the menu text (an ampersand marks the access key).     */
        /*     iHow  : MAIN_OPEN_TEXT, MAIN_OPEN_HEX, or MAIN_OPEN_EDITOR     */
        /*             plus a VoorheesFormat.VF_ id.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ToolStripMenuItem : the item.                                  */
        /*--------------------------------------------------------------------*/
        private ToolStripMenuItem Main_NewOpenAsItem(string sText, int iHow)
        {
            ToolStripMenuItem tsmItem;

            /* The item, the window it names kept in its Tag, and the one   */
            /* handler every Open As item shares, which reads that Tag.     */
            tsmItem        = new ToolStripMenuItem(sText);
            tsmItem.Tag    = iHow;
            tsmItem.Click += Main_OnOpenAsClicked;

            /* The caller adds it to the sub-menu. */
            return(tsmItem);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnOpenAsClicked:                                              */
        /*                                                                    */
        /* One of Open As's items, on the Commands menu or the right-click    */
        /* menu: open the active pane's one selected file in the window the   */
        /* item names -- the text editor whatever the content looks like, the */
        /* hex editor, or the structured editor as the chosen format whatever */
        /* the file's name says (a plain Open reaches that editor too, for a  */
        /* file the program can identify: Main_ShowOpened).                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item; its Tag is the MAIN_OPEN_* value.   */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the file is being opened, or the status bar says why    */
        /*            not.                                                    */
        /*--------------------------------------------------------------------*/
        private void Main_OnOpenAsClicked(object objSender, EventArgs evArgs)
        {
            /* The active pane's selection opened in the window the item's   */
            /* Tag names (Main_NewOpenAsItem put the MAIN_OPEN_* value       */
            /* there).                                                       */
            Main_OpenSelectedEntry((int)((ToolStripMenuItem)objSender).Tag);
        }

        /*--------------------------------------------------------------------*/
        /* Main_NewNewMenu:                                                   */
        /*                                                                    */
        /* Create a New sub-menu: one item per kind of file Commands > New    */
        /* makes (Main_asNewTexts, in MAIN_NEW_* order), each item's Tag its  */
        /* MAIN_NEW_* value, so one handler serves them all                   */
        /* (Main_OnNewFileClicked).  Called twice, for the Commands menu and  */
        /* the right-click menu, as Main_NewOpenAsMenu is.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ToolStripMenuItem : the sub-menu, filled.                      */
        /*--------------------------------------------------------------------*/
        private ToolStripMenuItem Main_NewNewMenu()
        {
            ToolStripMenuItem tsmNew;
            ToolStripMenuItem tsmItem;
            int               i;

            /* The sub-menu, filled below. */
            tsmNew = new ToolStripMenuItem("Ne&w");

            for (i = 0; i < Main_asNewTexts.Length; i++)
            {   /* One kind of file per iteration: its item, tagged with     */
                /* its MAIN_NEW_* value -- the table's index, which is why   */
                /* Main_asNewTexts must stay in MAIN_NEW_* order -- and      */
                /* the shared handler.                                       */
                tsmItem        = new ToolStripMenuItem(Main_asNewTexts[i]);
                tsmItem.Tag    = i;
                tsmItem.Click += Main_OnNewFileClicked;
                tsmNew.DropDownItems.Add(tsmItem);
            }

            /* The caller adds it to its menu. */
            return(tsmNew);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnNewFileClicked:                                             */
        /*                                                                    */
        /* One of New's items, on the Commands menu or the right-click menu:  */
        /* make a file of that kind in the active pane (Main_NewFile).        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item; its Tag is the MAIN_NEW_* value.    */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : New File's input is posted, or the status line says     */
        /*            why not.                                                */
        /*--------------------------------------------------------------------*/
        private void Main_OnNewFileClicked(object objSender, EventArgs evArgs)
        {
            /* The kind the item's Tag holds (Main_NewNewMenu put the        */
            /* MAIN_NEW_* value there) handed to Main_NewFile, which only    */
            /* posts the input: the name is asked, and the file made, once   */
            /* the worker admits it.                                         */
            Main_NewFile((int)((ToolStripMenuItem)objSender).Tag);
        }

        /*--------------------------------------------------------------------*/
        /* Main_NewFile:                                                      */
        /*                                                                    */
        /* New File's first half: post the request for its input, carrying    */
        /* the pane, the folder and the offered name as they are at the       */
        /* click.  Nothing is asked or made here -- once the worker admits    */
        /* the input, Main_CollectNewFile asks the name (and a hex file's     */
        /* size) and makes the file.                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iKind : the MAIN_NEW_* kind (the item's index in               */
        /*             Main_asNewTexts).                                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the input is posted (a refusal is put in the status     */
        /*            line by Main_ReportCommandFailure), or the status line  */
        /*            says why not.                                           */
        /*--------------------------------------------------------------------*/
        private void Main_NewFile(int iKind)
        {
            RQT_clsRequest objRequest;
            INK_enmInputKind enmPurpose;
            string sFolder;

            if (Main_objCommandClient == null)
            {   /* No command system to post to: said in the status line,   */
                /* and nothing is posted -- New File has no route that      */
                /* goes round the worker.                                   */
                Main_SetStatus("The New File command endpoint is not initialized.");
                return;
            }

            if (iKind < 0 || iKind >= Main_asNewNames.Length)
            {   /* A kind outside the tables: refused before Main_asNewNames   */
                /* is indexed with it.  A guard -- the menu's items carry      */
                /* the table's own indices.                                    */
                Main_SetStatus("The requested new-file kind is unavailable.");
                return;
            }

            /* Where the file goes, taken now, at the click: the image pane     */
            /* and its folder unless the Windows pane is the active one.  The   */
            /* input's dialogs come later, and by then the user may have        */
            /* moved either pane, so nothing about the destination is read      */
            /* from the panes again after this.                                 */
            enmPurpose = INK_enmInputKind.ImageNewFile;
            sFolder = Main_sImageDir;
            if (Main_bHostPaneActive)
            {   /* The Windows pane is the active one: the Windows kind of    */
                /* input, in that pane's folder ("" when it shows the drive   */
                /* list, which Main_CollectNewFile refuses).                  */
                enmPurpose = INK_enmInputKind.HostNewFile;
                sFolder = Main_sHostDir;
            }

            /* The request: an input for the main window (origin 0), against   */
            /* the open in the last state view, the kind in its flags, and     */
            /* the folder and the offered name (Main_asNewNames) in its        */
            /* arguments, with a fresh window id for the input.  Posted as     */
            /* user work (true), so the worker refuses it while busy or with   */
            /* an Exit pending, and while the partition window is open; an     */
            /* image New File is refused too when that open is no longer the   */
            /* open one, none included (CommandSequences.cs).  A refusal is    */
            /* put in the status line (Main_ReportCommandFailure).             */
            objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.OpenInput, 0,
                Main_objCommandClient.CLI_ReadState(), null, null, 0, (uint)iKind,
                new INA_clsInputArgs(enmPurpose, Main_objCommandClient.CLI_NewWindowId(),
                    "", sFolder, Main_asNewNames[iKind]), sFolder, "");
            Main_objCommandClient.CLI_Post(objRequest, Main_ReportCommandFailure, true);
        }

        /*--------------------------------------------------------------------*/
        /* Main_CollectNewFile:                                               */
        /*                                                                    */
        /* New File's second half, run once the worker has admitted the       */
        /* input (Main_RenderLocalFileInput, MainCommand.cs, which holds the  */
        /* worker until this returns): ask the name, and a hex file's size,   */
        /* then make the file in the pane and folder captured at the click.   */
        /* A name already there is refused, never overwritten; a hex file is  */
        /* made full of zeros.  In the image the worker makes the entry and   */
        /* opens its editor (Main_PostCommandNewFile); in Windows the file    */
        /* is made and opened here.                                           */
        /*                                                                    */
        /* THE FILE EXISTS BEFORE ITS EDITOR OPENS -- made on disk, or in the */
        /* image -- so from there on the editor's Save is the ordinary one:   */
        /* it replaces the entry's content.                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objInput : the admitted input: its kind (ImageNewFile or       */
        /*                HostNewFile), the folder and the offered name, the  */
        /*                MAIN_NEW_* kind in the request's flags, and the     */
        /*                state view (the open) it was posted against.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the file made and its editor opening, or the status     */
        /*            line or a message box says why not; a cancelled         */
        /*            question makes nothing.                                 */
        /*--------------------------------------------------------------------*/
        private void Main_CollectNewFile(ICV_clsInputView objInput)
        {
            STV_clsStateView objSource;
            OFD_clsOpenedFile objFile;
            FileStream stmZeros;
            PRM_clsPromptForm frmPrompt;
            PRM_clsPromptForm frmSize;
            string          sName;
            string          sFolder;
            string          sSize;
            string          sProblem;
            long            lSize;
            bool            bTaken;
            bool            bHost;
            bool            bCreated;
            int iKind;

            if (objInput.ICV_objRequest.RQT_uiFlags >= (uint)Main_asNewNames.Length)
            {   /* A kind outside the tables (a request made wrongly inside   */
                /* the program -- Main_NewFile refuses one): thrown before    */
                /* any question is asked or anything made; the caller puts    */
                /* the message in the status line.                            */
                throw new InvalidOperationException("The accepted new-file kind is unavailable.");
            }

            /* What the input carries: the pane, the kind, the open and the   */
            /* folder, all as captured at the click.  Which pane is active,   */
            /* and what either shows, may have changed since and play no      */
            /* part.                                                          */
            bHost = objInput.ICV_objArgs.INA_enmKind == INK_enmInputKind.HostNewFile;
            iKind = (int)objInput.ICV_objRequest.RQT_uiFlags;
            objSource = objInput.ICV_objSource;
            sFolder = objInput.ICV_objArgs.INA_sInitialFolder;

            if (bHost)
            {   /* The Windows pane: a real folder, not the drive list. */
                if (sFolder.Length == 0)
                {   /* Nowhere to make a file. */
                    Main_SetStatus("Go into a Windows folder first.");
                    return;
                }
            }
            else
            {   /* The image pane: it needs the open it was asked in.         */
                /* Whether the session may be written is not tested here --   */
                /* the worker settles that when the entry is created.         */
                if (objSource == null || objSource.STV_objTarget == null)
                {   /* No open recorded: nothing to make the file in.  A      */
                    /* guard -- the worker refuses an image New File with     */
                    /* nothing open before this runs (CommandSequences.cs).   */
                    Main_SetStatus("Open an image first.");
                    return;
                }
            }

            /* The name, offered as "new" with the kind's extension. */
            using (frmPrompt = new PRM_clsPromptForm(Main_fntDialog, "New file", "Name of the new file:",
                objInput.ICV_objArgs.INA_sInitialName))
            {   /* Ask for the name, under the program's icon. */
                frmPrompt.Icon = this.Icon;
                frmPrompt.ShowDialog(this);

                if (!frmPrompt.PRM_bAccepted || IsDisposed ||
                    Main_objCommandInputs.IWR_InputCancelled(objInput.ICV_objArgs.INA_lWindow))
                {   /* Cancelled, or the main window gone while it was asked:   */
                    /* nothing is made and no size is asked.  The cancel test   */
                    /* is a guard -- the worker asks only an image browser to   */
                    /* close (CommandInputs.cs IWR_InputCancelled).             */
                    return;
                }

                /* The name typed, trimmed of spaces at either end. */
                sName = frmPrompt.PRM_sText.Trim();
            }

            if (bHost)
            {   /* A Windows name: Windows' rules, and nothing of that name already there. */
                if (!Main_ValidHostName(sName))
                {   /* Not a name Windows can hold. */
                    Main_SetStatus("That is not a valid Windows name.");
                    return;
                }

                bTaken = File.Exists(Path.Combine(sFolder, sName)) || Directory.Exists(Path.Combine(sFolder, sName));
            }
            else
            {   /* An image name: the filesystem's rules.  Whether the name   */
                /* is taken is left to the engine (below).                    */
                if (!Main_ValidImageName(sName, Main_OpenIsFat()))
                {   /* Not a name the image can hold -- its length judged   */
                    /* as the open filesystem judges it (Main_OpenIsFat:    */
                    /* the open last shown), so the sentence names both     */
                    /* limits.                                              */
                    Main_SetStatus("That is not a valid name: it must not be empty, contain '/', or be longer than the filesystem holds (255 bytes on ext, 255 characters on FAT).");
                    return;
                }

                /* On a FAT or exFAT open, FAT's own name rules too, held here    */
                /* so a bad name is refused with words of its own before the      */
                /* worker is asked: FAT forbids " * : < > ? | \ and control       */
                /* characters in a name, and a name of spaces and dots alone      */
                /* (SYS_FatNameProblem, CommandOperations.cs, the rule the        */
                /* worker's New Folder and Rename and the pickers apply).  The    */
                /* engine refuses all of these anyway, but only as a bare         */
                /* argument error.  The name box takes a NAME, never a path, so   */
                /* a ':' here is refused rather than read as a drive.  The open   */
                /* is the one last shown (Main_OpenIsFat): the worker refuses     */
                /* the create if it is no longer the open one.                    */
                if (Main_OpenIsFat())
                {   /* A FAT session: its sentence, when the name breaks a rule. */
                    sProblem = SYS_clsSystem.SYS_FatNameProblem(sName);
                    if (sProblem.Length != 0)
                    {   /* Not a name FAT can hold: said, nothing made. */
                        Main_SetStatus(sProblem);
                        return;
                    }
                }

                /* Not looked for here: the pane's rows may by now be another   */
                /* folder's, since the pane may have moved while the name was   */
                /* asked, so they cannot say what the captured folder holds.    */
                /* The create itself refuses a name that exists                 */
                /* (Main_PostCommandNewFile, MainCommand.cs).                   */
                bTaken = false;
            }

            if (bTaken)
            {   /* New makes a new file; it never replaces one. */
                Main_SetStatus(sName + " already exists.");
                return;
            }

            /* No bytes for any kind but hex: the others start empty. */
            lSize = 0;

            if (iKind == MAIN_NEW_HEX)
            {   /* A hex file needs bytes to show: its size is asked,     */
                /* offered as 1024 -- whole bytes, at least one, within   */
                /* MAIN_NEW_HEX_MAX.                                      */
                using (frmSize = new PRM_clsPromptForm(Main_fntDialog, "New hex file", "Size of " + sName + ", in bytes:", "1024"))
                {   /* Ask for the size.                          */
                    /* The program's icon, as every window has.   */
                    frmSize.Icon = this.Icon;
                    frmSize.ShowDialog(this);

                    if (!frmSize.PRM_bAccepted || IsDisposed ||
                        Main_objCommandInputs.IWR_InputCancelled(objInput.ICV_objArgs.INA_lWindow))
                    {   /* Cancelled, or the main window gone: nothing is made   */
                        /* (the cancel test a guard, as for the name).           */
                        return;
                    }

                    /* The size typed, trimmed; parsed below. */
                    sSize = frmSize.PRM_sText.Trim();
                }

                if (!long.TryParse(sSize, out lSize) || lSize <= 0 || lSize > MAIN_NEW_HEX_MAX)
                {   /* Not a usable size. */
                    Main_SetStatus("The size must be a whole number of bytes, from 1 to " + Main_FormatSize((ulong)MAIN_NEW_HEX_MAX) + ".");
                    return;
                }
            }

            if (!bHost)
            {   /* In the image: one request, in which the worker makes the   */
                /* entry in the captured folder and opens its editor          */
                /* (Main_PostCommandNewFile); nothing more is done here.      */
                Main_PostCommandNewFile(sFolder, sName, lSize, iKind, objSource);
                return;
            }

            /* In Windows: the file is made and opened here, with no engine   */
            /* work.  Its descriptor first -- a Windows file, so its          */
            /* "temporary" file is the file itself, made in the folder        */
            /* captured at the click (not the folder the pane shows now).     */
            objFile = new OFD_clsOpenedFile();
            objFile.OFD_sName = sName;
            objFile.OFD_bHostFile  = true;
            objFile.OFD_sImageFile = "";
            objFile.OFD_sImagePath = Path.Combine(sFolder, sName);
            objFile.OFD_sTempFile  = objFile.OFD_sImagePath;

            /* The bytes: none, or the hex file's zeros.  CreateNew: a file   */
            /* already there is never replaced, even one made since the       */
            /* name was checked.                                              */
            bCreated = false;
            try
            {   /* Windows can refuse either step: no permission, no room,    */
                /* a read-only disc, a name made since it was checked.  The   */
                /* create is noted the moment it succeeds, so a sizing that   */
                /* fails after it is known to have left a file behind.        */
                stmZeros = new FileStream(objFile.OFD_sTempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                bCreated = true;
                using (stmZeros)
                {   /* The length is set rather than written: Windows supplies   */
                    /* the zeros (as found), so even a large one is made at      */
                    /* once.  The stream is closed when the block ends, a        */
                    /* failed sizing included.                                   */
                    stmZeros.SetLength(lSize);
                }
            }
            catch (Exception exCreate)
            {   /* Windows refused: said with its reason (Main_ShowHostError)    */
                /* and no editor is opened.  When the create was refused,        */
                /* nothing was made.  When only the sizing failed (no room for   */
                /* the size asked), the empty file this call made is removed     */
                /* first, so the folder is left as it was and a second New of    */
                /* the same name is not refused as "already exists"; a removal   */
                /* that fails is logged, the create's reason still the one       */
                /* shown.                                                        */
                if (bCreated)
                {   /* Made, then not sized: removed. */
                    try
                    {   /* The file this call created, and only it (CreateNew). */
                        File.Delete(objFile.OFD_sTempFile);
                    }
                    catch (Exception exRemove)
                    {   /* Left behind: logged. */
                        if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                        {   /* Only while the debug log is kept: otherwise the line is never built. */
                            DBGLOG_clsDebugLog.DBGLOG_Write("new file cleanup", exRemove.ToString());
                        }
                    }
                }

                /* The reason, as Windows gave it. */
                Main_ShowHostError("Creating " + objFile.OFD_sTempFile, exCreate);
                return;
            }

            /* Made: the Windows pane re-listed so the file appears -- the    */
            /* folder the pane shows NOW, which may not be the one the file   */
            /* went in -- and the editor opened on the file itself.           */
            Main_NavigateHost(Main_sHostDir);
            Main_OpenNewFile(objFile, iKind);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OpenNewFile:                                                  */
        /*                                                                    */
        /* Open a file New has just made, in the editor its kind calls for:   */
        /* the text editor or the hex editor on the file's bytes, or the      */
        /* structured editor on an empty document of the format (the empty    */
        /* file is not valid JSON; the first save writes the document over    */
        /* it).                                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objFile : the file's descriptor (a Windows file, made by       */
        /*               Main_CollectNewFile: its "temporary" file is the     */
        /*               file itself).                                        */
        /*     iKind   : the MAIN_NEW_* kind it was made as.                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the editor is showing the file.                         */
        /*--------------------------------------------------------------------*/
        private void Main_OpenNewFile(OFD_clsOpenedFile objFile, int iKind)
        {
            ECR_clsCopyResult objReady;

            if (iKind == MAIN_NEW_JSON)
            {   /* An empty JSON object: the structured editor on a new      */
                /* document (true), not on the empty file, which is not      */
                /* valid JSON; its first save writes the document over it.   */
                Main_ShowEditor(objFile, VoorheesFormat.VF_JSON, true);
                return;
            }

            if (iKind == MAIN_NEW_INI)
            {   /* An empty INI file, the same way. */
                Main_ShowEditor(objFile, VoorheesFormat.VF_INI, true);
                return;
            }

            /* Text or hex: the file's own (empty or zero) bytes, through      */
            /* the ordinary route.  Nothing was read out of an image now, so   */
            /* there is no failure to report.                                  */
            objReady = new ECR_clsCopyResult();
            objReady.ECR_iStatus = ENG_clsNative.ENG_OK;
            objReady.ECR_sDetail = "";

            if (iKind == MAIN_NEW_HEX)
            {   /* The hex editor. */
                Main_ShowOpened(objFile, MAIN_OPEN_HEX, objReady, false);
            }
            else
            {   /* The text editor. */
                Main_ShowOpened(objFile, MAIN_OPEN_TEXT, objReady, false);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_NewTempFolder:                                                */
        /*                                                                    */
        /* Make a new private work folder under %TEMP%, and list it for       */
        /* removal when the program ends.  Each call makes another folder;    */
        /* its users name their own files inside it -- among them the         */
        /* command system's work folder for editor snapshots and baselines    */
        /* (Main_StartCommandSystem, MainCommand.cs) and each image New       */
        /* File's stage (Main_PostCommandNewFile).                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the folder's path, created; null when %TEMP% would    */
        /*              not take it (the user has been told).                 */
        /*--------------------------------------------------------------------*/
        private string Main_NewTempFolder()
        {
            string sFolder;

            /* A name no other folder has: the drag folders' prefix (this   */
            /* is a folder of that kind), this run's process id, and a      */
            /* fresh GUID.                                                  */
            sFolder = Path.Combine(Path.GetTempPath(),
                                   MAIN_DRAG_TEMP_PREFIX + System.Diagnostics.Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N"));

            try
            {   /* %TEMP% may not be writable. */
                Directory.CreateDirectory(sFolder);
            }
            catch (Exception exCreate)
            {   /* Nowhere to put the content: said with Windows' reason,   */
                /* and the caller gives up.                                 */
                Main_ShowHostError("Creating the temporary folder " + sFolder, exCreate);
                return(null);
            }

            /* Listed with the drag folders, which are removed only at the end   */
            /* of Main_OnFormClosing -- reached once the worker has stopped,     */
            /* or when it never started -- so no snapshot or baseline an         */
            /* editor still works on is taken away while it is in use.           */
            Main_lstDragFolders.Add(sFolder);

            /* The folder, made and empty; what goes in it is the caller's. */
            return(sFolder);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OpenSelectedEntry:                                            */
        /*                                                                    */
        /* Open the active pane's one selected file in the window iHow names, */
        /* by posting it to the command worker.  A Windows file goes as an    */
        /* input, its editor then made here (Main_RenderLocalFileInput,       */
        /* MainCommand.cs); an image file goes as an Open, which the worker   */
        /* reads out and opens (Main_OpenImageEntry).  The file and the       */
        /* choice are those of the click.                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iHow : MAIN_OPEN_TEXT, MAIN_OPEN_HEX, or MAIN_OPEN_EDITOR plus */
        /*            a format -- the command system's own values (VWH_*),    */
        /*            so handed on unchanged.                                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the request is posted, or the status line says why not. */
        /*--------------------------------------------------------------------*/
        private void Main_OpenSelectedEntry(int iHow)
        {
            RQT_clsRequest objRequest;
            ListViewItem lviRow;

            if (Main_bHostPaneActive)
            {   /* The Windows pane: one file, opened through an input. */
                if (Main_lstHost.SelectedItems.Count != 1 ||
                    !(Main_lstHost.SelectedItems[0].Tag is FileInfo))
                {   /* Not exactly one file selected -- none, several, or a    */
                    /* folder, a drive or the ".." row, whose Tags are not a   */
                    /* FileInfo: said, the selection left as it is.            */
                    Main_SetStatus("Select one file in the Windows pane to open.");
                    return;
                }

                if (Main_objCommandClient == null)
                {   /* No command system: said, and nothing opened -- an    */
                    /* editor is only ever made through the worker, which   */
                    /* registers it.                                        */
                    Main_SetStatus("The Windows viewer command endpoint is not initialized.");
                    return;
                }

                /* The input's request: the file's full path and the choice,    */
                /* taken now from the row clicked, with a fresh input window    */
                /* id.  Going through the worker means the editor is made       */
                /* only once the worker admits it -- never while an Exit or a   */
                /* restart is closing everything, nor while the partition       */
                /* window is open (CommandSequences.cs).  Posted as user        */
                /* work; a refusal reaches the status line.                     */
                lviRow = Main_lstHost.SelectedItems[0];
                objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.OpenInput, 0,
                    Main_objCommandClient.CLI_ReadState(), null,
                    new string[] { ((FileInfo)lviRow.Tag).FullName }, 0, (uint)iHow,
                    new INA_clsInputArgs(INK_enmInputKind.HostOpenEntry,
                        Main_objCommandClient.CLI_NewWindowId(), "", Main_sHostDir, lviRow.Text), "", "");
                Main_objCommandClient.CLI_Post(objRequest, Main_ReportCommandFailure, true);

                /* Posted: the image pane's selection plays no part. */
                return;
            }

            if (Main_lstImage.SelectedItems.Count != 1 ||
                Main_lstImage.SelectedItems[0].Tag == null)
            {   /* Not exactly one entry selected (the ".." row has no Tag):   */
                /* said, and nothing posted.                                   */
                Main_SetStatus("Select one file in the image pane to open.");
                return;
            }

            /* The image pane: the row's full path and its listed facts      */
            /* handed to Main_OpenImageEntry, which refuses anything but a   */
            /* regular file.  The worker then reads the entry out of the     */
            /* image and decides from what it reads, not from the row.       */
            lviRow = Main_lstImage.SelectedItems[0];
            Main_OpenImageEntry(Main_JoinImagePath(Main_sImageDir, lviRow.Text),
                (EEN_stEntry)lviRow.Tag, iHow);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OpenImageEntry:                                               */
        /*                                                                    */
        /* Post an Open of one image file to the worker, with the window      */
        /* asked for and the structured-editor option as it is now.  The      */
        /* worker does the rest -- the read-out, any question about a binary  */
        /* file, the editor's registration -- and the editor is shown when    */
        /* its view arrives (Main_ShowOpened, through MainCommand.cs).        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sImagePath : the entry's full path in the open filesystem.     */
        /*     stEntry    : the row's listed facts; only its type is used,    */
        /*                  to refuse anything but a regular file at once.    */
        /*     iHow       : MAIN_OPEN_AUTO (the content decides),             */
        /*                  MAIN_OPEN_TEXT, MAIN_OPEN_HEX, or                 */
        /*                  MAIN_OPEN_EDITOR plus a format.                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the Open is posted (a failure reaches the status line), */
        /*            or the status line says why not.                        */
        /*--------------------------------------------------------------------*/
        private void Main_OpenImageEntry(string sImagePath, EEN_stEntry stEntry, int iHow)
        {
            STV_clsStateView objSource;
            RQT_clsRequest objRequest;
            uint uiHow;

            if (stEntry.EEN_uiType != ENG_clsNative.ENG_TYPE_FILE)
            {   /* A directory, link or special node has no regular content to edit. */
                Main_SetStatus("Only a regular file can be opened; " + sImagePath +
                    " is a " + Main_TypeText(stEntry.EEN_uiType).ToLower() + ".");
                return;
            }

            if (Main_objCommandClient == null)
            {   /* No command system: said, and nothing opened -- the    */
                /* worker is the only thing that reads from the image.   */
                Main_SetStatus("The command viewer endpoint is not initialized.");
                return;
            }

            /* The open as the worker last reported it.  The request carries    */
            /* its epoch and range, and the worker refuses it if that open is   */
            /* no longer the open one when it is taken (CommandSequences.cs),   */
            /* so the file is never read from another filesystem.               */
            objSource = Main_objCommandClient.CLI_ReadState();
            if (objSource.STV_objTarget == null)
            {   /* No open in that state (none reported yet, or closed   */
                /* since): said, and nothing posted.                     */
                Main_SetStatus("No filesystem target was supplied for the viewer.");
                return;
            }

            /* The choice, with the Options menu's "open config files in the   */
            /* structured editor" tick added as it stands NOW, so a change     */
            /* made before the worker takes the request has no effect on       */
            /* it.  The worker uses the bit only for a plain Open whose        */
            /* content is text (CommandOperations.cs Sys_OpenEntrySequence).   */
            uiHow = (uint)iHow;
            if (Main_tsmOptEditorOpen.Checked)
            {   /* The option is on: its bit set. */
                uiHow |= VWH_clsViewerHow.VWH_AUTO_CONFIG;
            }

            /* The Open: one request against that open, the one path, the     */
            /* choice, and the image folder for the worker to list            */
            /* afterwards.  "Opening ..." is shown while it runs; posted as   */
            /* user work, its completion reports only a failure, a refusal    */
            /* or a declined question (Main_ReportCommandFailure) -- the      */
            /* editor itself arrives as a view of its own.                    */
            objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.OpenEntry, 0,
                objSource, objSource.STV_objTarget, new string[] { sImagePath },
                0, uiHow, null, Main_sImageDir, "");
            Main_SetStatus("Opening " + sImagePath + " ...");
            Main_objCommandClient.CLI_Post(objRequest, Main_ReportCommandFailure, true);
        }

        /*--------------------------------------------------------------------*/
        /* Main_ShowOpened:                                                   */
        /*                                                                    */
        /* Make the viewer a file is to be shown in, register it with the     */
        /* command system and show it (Main_TrackCommandViewer).  For an      */
        /* image entry the worker has already chosen the viewer and asked     */
        /* any question (bResolved); for a Windows file the choice is         */
        /* settled here, with the program's own questions -- the binary       */
        /* file's, and the text editor's size limit.  A viewer that cannot    */
        /* load its file is refused before it is registered, said in a        */
        /* message box, and released.                                         */
        /*                                                                    */
        /* Its callers: an image entry's EditorOpen view and a Windows file's */
        /* (MainCommand.cs, Main_ShowCommandImageViewer and                   */
        /* Main_ShowCommandHostViewer), and Main_OpenNewFile.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objFile   : the file's descriptor: where it is, and the file   */
        /*                 the viewer works on (an image entry's read-out, or */
        /*                 a Windows file itself).                            */
        /*     iHow      : the MAIN_OPEN_* choice; resolved, MAIN_OPEN_AUTO   */
        /*                 means a picture.                                   */
        /*     objResult : the read-out's result (every caller passes a       */
        /*                 success).                                          */
        /*     bResolved : true when the worker has already chosen the viewer */
        /*                 and asked its questions.                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true -- the viewer is registered and shown; false --    */
        /*            refused or declined before registration, with what it   */
        /*            held released (the caller tells the worker, for an      */
        /*            image entry).  A failure to register or show throws     */
        /*            (Main_TrackCommandViewer has cleaned up).               */
        /*--------------------------------------------------------------------*/
        private bool Main_ShowOpened(OFD_clsOpenedFile objFile, int iHow,
            ECR_clsCopyResult objResult, bool bResolved)
        {
            TXE_clsTextEditForm frmText;
            HEXF_clsHexEditForm frmHex;
            PIC_clsPictureForm frmPicture;
            BNY_clsBinaryForm frmBinary;
            string sWhy;
            string sError;
            string sMissing;
            long lSize;
            bool bTextAllowed;
            int iKind;
            int iChoice;
            int iFormat;

            /* The status line back to the volume's summary, replacing the    */
            /* "Opening ..." an image Open put there (Main_OpenImageEntry);   */
            /* then the file is checked before any viewer is made.            */
            Main_SetStatus(Main_sVolumeText);
            if (objResult.ECR_iStatus != ENG_clsNative.ENG_OK)
            {   /* The read-out failed: refused before any viewer is made,   */
                /* so there is nothing to release, with the status's own     */
                /* sentence.  A guard: every caller passes a success.        */
                MSG_clsMessageForm.MSG_Show(this, Main_fntDialog,
                    objFile.OFD_sImagePath + " could not be read out of the image.\n\n" +
                    ENG_clsNative.ENG_StatusText(objResult.ECR_iStatus) +
                    "\n\nThe debug log, if one is kept (log\\BifrostFM-debug.log beside the program), has the detail.",
                    "Open", MessageBoxButtons.OK, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button1);
                return(false);
            }

            if (!File.Exists(objFile.OFD_sTempFile))
            {   /* The file the viewer is to work on is not there: refused     */
                /* before any viewer is made, so nothing to release.  Its      */
                /* likeliest case is a Windows file deleted since the pane     */
                /* listed it -- a Windows file is opened in place, so that     */
                /* file IS the one tested -- and the words say which kind of   */
                /* file it was.  No status sentence: the status here is a      */
                /* success, and its "Done." would contradict the refusal.      */
                if (objFile.OFD_bHostFile)
                {   /* A Windows file: gone since it was listed. */
                    sMissing = objFile.OFD_sImagePath + " no longer exists.\n\n" +
                        "It may have been moved or deleted since the Windows pane listed it.";
                }
                else
                {   /* An image entry: its read-out copy is missing.  Why that   */
                    /* could happen after a successful read-out is not           */
                    /* established.                                              */
                    sMissing = objFile.OFD_sImagePath + " could not be read out of the image: " +
                        "the copy read out of it is not there.";
                }

                /* Said over the main window; nothing opened. */
                MSG_clsMessageForm.MSG_Show(this, Main_fntDialog, sMissing,
                    "Open", MessageBoxButtons.OK, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button1);
                return(false);
            }

            if (iHow >= MAIN_OPEN_EDITOR)
            {   /* The structured editor, the format chosen: no            */
                /* classification here -- the editor's own parser checks   */
                /* the file, and offers the text editor if it refuses      */
                /* (Main_ShowEditor).                                      */
                return(Main_ShowEditor(objFile, iHow - MAIN_OPEN_EDITOR, false));
            }

            /* The content's kind.  Resolved, it is not looked at again: the   */
            /* worker classified the read-out, and a resolved MAIN_OPEN_AUTO   */
            /* means a picture, so the kind starts as that.  A Windows file    */
            /* is classified now, below.                                       */
            iKind = OSC_clsScanner.OSC_KIND_PICTURE;
            if (!bResolved)
            {   /* A Windows file: its size and kind read now, and the   */
                /* choice settled with the program's own questions.      */
                try
                {   /* The file can have been deleted, or be held, since it   */
                    /* was checked a moment ago.                              */
                    lSize = new FileInfo(objFile.OFD_sTempFile).Length;
                    iKind = OSC_clsScanner.OSC_Classify(objFile.OFD_sTempFile, out sWhy);
                }
                catch (Exception exScan)
                {   /* It could not be read: said with Windows' reason, and   */
                    /* nothing opened (no viewer exists yet).  A Windows      */
                    /* file is read in place, so the message names the file   */
                    /* itself.                                                */
                    Main_ShowHostError("Reading " + objFile.OFD_sImagePath, exScan);
                    return(false);
                }

                /* Whether the text editor would take it -- the one size     */
                /* limit both questions below use (OSC_TEXT_MAX_BYTES, the   */
                /* worker's own for an image entry).                         */
                bTextAllowed = lSize <= OSC_clsScanner.OSC_TEXT_MAX_BYTES;
                if (iHow == MAIN_OPEN_AUTO && iKind == OSC_clsScanner.OSC_KIND_TEXT)
                {   /* A plain Open, and the content is text: the structured   */
                    /* editor if the Options tick is on and the name or        */
                    /* content says a format, else the text editor -- the      */
                    /* worker's rule for an image entry.                       */
                    if (Main_tsmOptEditorOpen.Checked)
                    {   /* The option is on: the format looked for, with the   */
                        /* worker's own test (OSC_IdentifyFormat).             */
                        iFormat = OSC_clsScanner.OSC_IdentifyFormat(objFile.OFD_sTempFile,
                            objFile.OFD_sName, lSize);
                        if (iFormat != VoorheesFormat.VF_DETECT)
                        {   /* A format: the structured editor, which still   */
                            /* parses the file itself.                        */
                            return(Main_ShowEditor(objFile, iFormat, false));
                        }
                    }

                    /* No format: the text editor (its size test below). */
                    iHow = MAIN_OPEN_TEXT;
                }

                if (iHow == MAIN_OPEN_AUTO && iKind != OSC_clsScanner.OSC_KIND_PICTURE)
                {   /* A plain Open, and the content is neither text nor a       */
                    /* picture: the binary question -- hex, text (offered only   */
                    /* when the size allows), or cancel -- with the              */
                    /* classifier's words for why it looks binary.               */
                    using (frmBinary = new BNY_clsBinaryForm(Main_fntDialog,
                        objFile.OFD_sName, sWhy, bTextAllowed))
                    {   /* Asked, and the answer read before the dialog is   */
                        /* disposed at the end of the block.                 */
                        frmBinary.Icon = this.Icon;
                        frmBinary.ShowDialog(this);
                        iChoice = frmBinary.BNY_ReadChoice();
                    }

                    if (iChoice == BNY_clsBinaryForm.BNY_CHOICE_CANCEL)
                    {   /* Cancelled: nothing opened (no viewer exists yet). */
                        return(false);
                    }

                    /* Hex, unless the answer was text. */
                    iHow = MAIN_OPEN_HEX;
                    if (iChoice == BNY_clsBinaryForm.BNY_CHOICE_TEXT)
                    {   /* Text -- the dialog offers it only within the size   */
                        /* limit it was given.                                 */
                        iHow = MAIN_OPEN_TEXT;
                    }
                }

                if (iHow == MAIN_OPEN_TEXT && !bTextAllowed)
                {   /* Text asked for (by Open As, or by a plain Open of      */
                    /* text) but larger than the text editor takes: the hex   */
                    /* editor offered instead, Yes the default.               */
                    if (MSG_clsMessageForm.MSG_Show(this, Main_fntDialog,
                        objFile.OFD_sImagePath + " is " + Main_FormatSize((ulong)lSize) +
                        " bytes, more than the text editor takes (" +
                        Main_FormatSize((ulong)OSC_clsScanner.OSC_TEXT_MAX_BYTES) +
                        ").\n\nOpen it in the hex editor instead?", "Open",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button1) != DialogResult.Yes)
                    {   /* Declined: nothing opened (no viewer exists yet). */
                        return(false);
                    }

                    /* The hex editor, as accepted. */
                    iHow = MAIN_OPEN_HEX;
                }
            }

            if (iHow == MAIN_OPEN_AUTO && iKind == OSC_clsScanner.OSC_KIND_PICTURE)
            {   /* A picture -- the worker's resolved answer, or a Windows   */
                /* file classified so above: the picture viewer, which       */
                /* decodes the file as it is made.                           */
                frmPicture = new PIC_clsPictureForm(objFile);
                sError = frmPicture.PIC_ReadLoadError();
                if (sError.Length != 0)
                {   /* It would not decode: said, then the viewer released   */
                    /* and disposed unregistered, and false returned.        */
                    try
                    {   /* The reason, with the hex editor named as the way     */
                        /* to see the file anyway -- by the menu command that   */
                        /* reaches it, Open As > Hex: a picture never meets     */
                        /* the binary-file question whose button is Edit Hex.   */
                        MSG_clsMessageForm.MSG_Show(this, Main_fntDialog,
                            objFile.OFD_sImagePath + " looks like a picture but could not be shown.\n\n" +
                            sError + "\n\nOpen As > Hex will still open it.", "Open",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning,
                            MessageBoxDefaultButton.Button1);
                    }
                    finally
                    {   /* Whatever the message box did, the viewer's picture     */
                        /* and stream are released by hand -- it was never        */
                        /* shown, so its FormClosed release will not run          */
                        /* (Main_ReleaseViewerResources, MainCommand.cs) -- and   */
                        /* only then is the form disposed.                        */
                        try
                        {   /* Released; a failure to release is thrown on,   */
                            /* never reported as a clean refusal.             */
                            Main_ReleaseViewerResources(frmPicture);
                        }
                        finally
                        {   /* Disposed even if the release threw. */
                            frmPicture.Dispose();
                        }
                    }

                    /* Refused and released: false, so an image entry's caller   */
                    /* tells the worker the viewer is gone.                      */
                    return(false);
                }

                /* Decoded: registered, then shown (Main_TrackCommandViewer),   */
                /* so no close can come from a window the command system does   */
                /* not know.                                                    */
                Main_TrackCommandViewer(frmPicture, objFile);
                return(true);
            }

            if (iHow == MAIN_OPEN_TEXT)
            {   /* The text editor, given the save route every editor uses   */
                /* (Main_SaveOpenedFile); its Save As is bound when it is    */
                /* registered.  It loads the file as it is made.             */
                frmText = new TXE_clsTextEditForm(Main_fntDialog, objFile, Main_SaveOpenedFile);
                sError = frmText.TXE_ReadLoadError();
                if (sError.Length != 0)
                {   /* It could not load the file: said, the editor disposed   */
                    /* unregistered, false returned.  Nothing is released by   */
                    /* hand: the text editor keeps no file open once loaded    */
                    /* (Main_ReleaseViewerResources has nothing for it).       */
                    try
                    {   /* The editor's own reason, over the main window. */
                        MSG_clsMessageForm.MSG_Show(this, Main_fntDialog,
                            objFile.OFD_sImagePath + " could not be opened.\n\n" + sError,
                            "Open", MessageBoxButtons.OK, MessageBoxIcon.Warning,
                            MessageBoxDefaultButton.Button1);
                    }
                    finally
                    {   /* Disposed whatever the message box did. */
                        frmText.Dispose();
                    }

                    /* Refused: false, so an image entry's caller tells the   */
                    /* worker the viewer is gone.                             */
                    return(false);
                }

                /* Loaded: registered, then shown, as the picture viewer is. */
                Main_TrackCommandViewer(frmText, objFile);
                return(true);
            }

            /* What is left is the hex editor -- asked for, chosen in the      */
            /* binary question, or taken instead of a text file too large.     */
            /* It keeps a read handle on the file for as long as it is open.   */
            frmHex = new HEXF_clsHexEditForm(Main_fntDialog, objFile, Main_SaveOpenedFile);
            sError = frmHex.HEXF_ReadLoadError();
            if (sError.Length != 0)
            {   /* It could not load the file: said, then its read handle   */
                /* released by hand and the editor disposed unregistered,   */
                /* as for the picture viewer.                               */
                try
                {   /* The editor's own reason, over the main window. */
                    MSG_clsMessageForm.MSG_Show(this, Main_fntDialog,
                        objFile.OFD_sImagePath + " could not be opened.\n\n" + sError,
                        "Open", MessageBoxButtons.OK, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button1);
                }
                finally
                {   /* Whatever the message box did: the read handle closed   */
                    /* (disposing the form would not close it), then the      */
                    /* form disposed.                                         */
                    try
                    {   /* Released; a failure to release is thrown on,   */
                        /* never reported as a clean refusal.             */
                        Main_ReleaseViewerResources(frmHex);
                    }
                    finally
                    {   /* Disposed even if the release threw. */
                        frmHex.Dispose();
                    }
                }

                /* Refused and released: false, so an image entry's caller   */
                /* tells the worker the viewer is gone.                      */
                return(false);
            }

            /* Loaded: registered, then shown.  From here its own close     */
            /* releases the handle, and the registration tells the worker   */
            /* once it has.                                                 */
            Main_TrackCommandViewer(frmHex, objFile);

            /* Shown: the caller has nothing more to do. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* Main_ShowEditor:                                                   */
        /*                                                                    */
        /* Open the structured editor on a file in one format, register it    */
        /* and show it; and when its load finds the file not valid in that    */
        /* format, offer the text editor instead (Main_OfferTextForEditor).   */
        /* Unlike the other viewers this one loads as it is SHOWN, not as it  */
        /* is made, so a refusal comes after registration: the window closes  */
        /* itself, and its registration tells the worker it has gone.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objFile      : the file's descriptor.                          */
        /*     iFormat      : the format, a VoorheesFormat.VF_ id.            */
        /*     bNewDocument : true for New File's placeholder: an empty       */
        /*                    document of the format, the file not read.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true -- registered, whether or not its load then        */
        /*            refused, so the caller must not tell the worker of a    */
        /*            close a second time.  A failure to register or show     */
        /*            throws (Main_TrackCommandViewer has cleaned up).        */
        /*--------------------------------------------------------------------*/
        private bool Main_ShowEditor(OFD_clsOpenedFile objFile, int iFormat, bool bNewDocument)
        {
            JSN_clsJsonEditForm frmEditor;
            string sRefused;

            /* The editor made, told whether it starts a new document (which   */
            /* it acts on when it loads), then registered and shown -- the     */
            /* registration first, so the close a refused load makes, or a     */
            /* Save, never comes from a window the command system does not     */
            /* know.                                                           */
            frmEditor = new JSN_clsJsonEditForm(Main_fntDialog, objFile,
                Main_SaveOpenedFile, iFormat);
            frmEditor.JSN_SetNewDocument(bNewDocument);
            Main_TrackCommandViewer(frmEditor, objFile);

            /* The load's verdict, read only now that the showing (and the      */
            /* load inside it) has returned: a reason when the file was not     */
            /* valid in the format, the window having closed itself.  Then      */
            /* the text editor is offered as a fresh request -- the closed      */
            /* window's file is not reused -- unless the main window has gone   */
            /* meanwhile.                                                       */
            sRefused = frmEditor.JSN_ReadRefused();
            if (sRefused != null && !this.IsDisposed)
            {   /* Refused by the format: the offer posted; the worker asks   */
                /* the question, then opens a Windows file in place or        */
                /* reads an image file out again, as text.                    */
                Main_OfferTextForEditor(objFile, iFormat, sRefused);
            }

            /* Registered either way: true, so the caller adds no close of    */
            /* its own -- a refused editor's own close has already told the   */
            /* worker.                                                        */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OfferTextForEditor:                                           */
        /*                                                                    */
        /* The structured editor refused a file as not valid in its format:   */
        /* post the offer to open it as plain text.  The worker asks the      */
        /* question and, on Yes, opens a Windows file in place or reads an    */
        /* image file out afresh (CommandOperations.cs,                       */
        /* Sys_ViewerFallbackSequence) -- the refused editor's read-out is    */
        /* never reused.  The file is the one the editor had, not anything a  */
        /* pane shows now.                                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objFile  : the refused editor's descriptor.                    */
        /*     iFormat  : the format that refused it.                         */
        /*     sRefused : the format's reason, with its line and column, for  */
        /*                the question.                                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the offer is posted (with no completion: a refusal --   */
        /*            the image closed meanwhile -- is not reported), or the  */
        /*            status line says why not.                               */
        /*--------------------------------------------------------------------*/
        private void Main_OfferTextForEditor(OFD_clsOpenedFile objFile, int iFormat, string sRefused)
        {
            TGT_clsTarget objTarget;
            STV_clsStateView objSource;
            RQT_clsRequest objRequest;

            if (Main_objCommandClient == null)
            {   /* No command system: said, and nothing offered -- the   */
                /* question and the new open are the worker's.           */
                Main_SetStatus("The command viewer endpoint is not initialized.");
                return;
            }

            /* The request's target and state, made from the editor's own       */
            /* descriptor.  A Windows file: its path, with no image, epoch or   */
            /* state.  An image file: the image, the range and the epoch the    */
            /* editor was opened from, in a state view made for the request,    */
            /* so the worker refuses it if that open is no longer the open      */
            /* one -- the text is never read from another filesystem.  The      */
            /* worker's test compares path, offset and length only              */
            /* (CommandContracts.cs TGT_SameRange), so the disk size 0 and      */
            /* sector size 512 given here play no part in it; they enter only   */
            /* the request's duplicate key.                                     */
            objSource = null;
            if (objFile.OFD_bHostFile)
            {   /* A Windows file: its path only. */
                objTarget = new TGT_clsTarget(objFile.OFD_sImagePath, 0, 0, "", 0, 512);
            }
            else
            {   /* An image file: the editor's open, as captured. */
                objTarget = new TGT_clsTarget(objFile.OFD_sImageFile,
                    objFile.OFD_ullOffset, objFile.OFD_ullLength,
                    objFile.OFD_sDeviceText, 0, 512);
                objSource = new STV_clsStateView(MOD_enmMode.Open, objTarget,
                    objFile.OFD_lEpoch, false, false, false);
            }

            /* The offer: a ViewerFallback for the main window, the one path   */
            /* (the entry, or the Windows file), the format and its reason.    */
            /* Posting asks nothing here; the question is the worker's.        */
            objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.ViewerFallback,
                0, objSource, objTarget, new string[] { objFile.OFD_sImagePath },
                0, 0, new VFA_clsViewerFallbackArgs(objFile.OFD_bHostFile,
                    iFormat, sRefused), "/", "");
            Main_objCommandClient.CLI_Post(objRequest, null);
        }

        /*--------------------------------------------------------------------*/
        /* Main_TrackViewer:                                                  */
        /*                                                                    */
        /* Show a viewer (an editor or the picture viewer) modeless and with  */
        /* no owner, and keep it on the main window's list (Main_lstViewers)  */
        /* until it closes.  The list is the main window's own -- its         */
        /* registration with the command system, and what the viewer holds,   */
        /* are the caller's and the viewer's (Main_TrackCommandViewer,        */
        /* MainCommand.cs, registers first and then calls this).              */
        /*                                                                    */
        /* A viewer can open while a MODAL dialog is still up -- a New file   */
        /* is made under the "Importing into the image" progress window and   */
        /* its editor shown before that window's modal loop has unwound.      */
        /* Windows activates a modal dialog's owner as the dialog goes, which */
        /* puts the main window in front of the new editor (as found: the     */
        /* editor in front, then the main window about 30 ms later).  So such */
        /* a viewer is recorded and brought back to the front once the dialog */
        /* has closed (Main_OnModalClosed, Main_OnMainActivated).             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmViewer : the viewer, made and already registered.           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : shown; a structured editor whose load refused may       */
        /*            already have closed itself.                             */
        /*--------------------------------------------------------------------*/
        private void Main_TrackViewer(Form frmViewer)
        {
            Form frmModal;

            /* The program's icon, and the list's removal hooked, BEFORE the   */
            /* Show: a structured editor loads inside the Show and can close   */
            /* itself there, and its FormClosed must find the hook in place.   */
            /* The handler takes the window from its sender, so one handler    */
            /* serves every viewer.                                            */
            frmViewer.Icon = this.Icon;
            frmViewer.FormClosed += Main_OnViewerClosed;
            Main_lstViewers.Add(frmViewer);

            /* The window active before the viewer appears: a modal dialog   */
            /* here is one whose closing will hand the activation away.      */
            frmModal = Form.ActiveForm;
            frmViewer.Show();

            if (frmModal != null && frmModal != this && frmModal != frmViewer &&
                frmModal.Modal && !frmModal.IsDisposed)
            {   /* Opened under a modal dialog: brought back in front once   */
                /* that dialog has closed and its owner been activated.      */
                Main_frmFrontViewer = frmViewer;
                frmModal.FormClosed += Main_OnModalClosed;
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnViewerClosed:                                               */
        /*                                                                    */
        /* A viewer's FormClosed: take it off the main window's list          */
        /* (Main_TrackViewer).  Only the list -- telling the worker the       */
        /* viewer has gone is its registration's own close handler, and       */
        /* releasing its file is the viewer's.                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the viewer that closed.                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the viewer is no longer listed.                         */
        /*--------------------------------------------------------------------*/
        private void Main_OnViewerClosed(object objSender, FormClosedEventArgs evArgs)
        {
            /* The window that raised the event removed -- by itself, not by   */
            /* position, so another viewer opened meanwhile is unaffected.     */
            Main_lstViewers.Remove((Form)objSender);
        }

        /*--------------------------------------------------------------------*/
        /* Main_SaveOpenedFile:                                               */
        /*                                                                    */
        /* The save route every editor is given (Main_ShowOpened,             */
        /* Main_ShowEditor): hand an editor's save to the editor coordinator  */
        /* (CommandEditorCoordinator.cs EUI_Save), which posts an image       */
        /* entry's save to the worker, or reports a Windows file's write as a */
        /* fact, and answers the editor.  Nothing is written here.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objFile : the save's descriptor (OFD_ForSave): where, which    */
        /*               revision, its order, and its stage when it has one.  */
        /*     pfnDone : the editor's answer, called exactly once -- false    */
        /*               when the save is refused or fails.                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : handed over (answered now or later by the               */
        /*            coordinator), or refused and answered here.             */
        /*--------------------------------------------------------------------*/
        private void Main_SaveOpenedFile(OFD_clsOpenedFile objFile, OFD_SaveDone pfnDone)
        {
            if (Main_objCommandEditors == null)
            {   /* No coordinator (the command system was never set up):    */
                /* the save is refused -- there is no other way to write.   */
                /* A guard: an editor is only made once the system runs.    */
                try
                {   /* A stage the save brought with it is deleted, as no   */
                    /* one else will.                                       */
                    if (objFile != null && objFile.OFD_objStage != null)
                    {   /* It has one: its own release deletes its file   */
                        /* (only its file -- never the editor's).         */
                        objFile.OFD_objStage.STG_Release();
                    }
                }
                catch (Exception exCleanup)
                {   /* The deletion failed: logged, and the editor is still   */
                    /* answered below.                                        */
                    if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                    {   /* Only while the debug log is kept: otherwise the line is never built. */
                        DBGLOG_clsDebugLog.DBGLOG_Write("editor snapshot cleanup", exCleanup.ToString());
                    }
                }

                /* The editor answered "not saved", whatever the clean-up      */
                /* did, so it does not wait for ever; then the reason shown.   */
                pfnDone(false);
                Main_SetStatus("The command editor endpoint is not initialized.");
                return;
            }

            /* Handed over: the coordinator answers the editor, now or when   */
            /* the worker's result arrives.                                   */
            Main_objCommandEditors.EUI_Save(objFile, pfnDone);
        }


        /*--------------------------------------------------------------------*/
        /* Main_OnImagePathKeyDown:                                           */
        /*                                                                    */
        /* Enter in the image path box goes to the directory typed there.     */
        /* The path must be absolute (start with '/'), as every path inside   */
        /* an image is; anything else is put back to the directory showing.   */
        /* A path that does not exist is reported by the listing itself,      */
        /* which also restores the box.  A Windows drive path typed there     */
        /* (a drive letter and colon, alone or followed by a path) is a       */
        /* request to go to that folder in the Windows pane: recorded here    */
        /* and posted, Main_RunDriveRequest doing the rest from the loop.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the path box.                                      */
        /*     evKey     : the key event.                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the pane navigates on Enter, or a drive request is      */
        /*            posted; other keys pass through.                        */
        /*--------------------------------------------------------------------*/
        private void Main_OnImagePathKeyDown(object objSender, KeyEventArgs evKey)
        {
            string sTyped;

            if (evKey.KeyCode != Keys.Enter)
            {   /* Ordinary typing. */
                return;
            }

            /* Swallow the key so the box does not also beep. */
            evKey.Handled          = true;
            evKey.SuppressKeyPress = true;

            /* The path typed, trimmed of spaces at either end. */
            sTyped = Main_txtImagePath.Text.Trim();

            if (sTyped.Length >= 2 && sTyped[1] == ':' &&
                ((sTyped[0] >= 'A' && sTyped[0] <= 'Z') || (sTyped[0] >= 'a' && sTyped[0] <= 'z')) &&
                (sTyped.Length == 2 || sTyped[2] == '\\' || sTyped[2] == '/'))
            {   /* A Windows drive path -- a letter, a colon, then nothing or   */
                /* a separator of either kind and a path -- typed into the      */
                /* image's box: no path inside an image looks like that (they   */
                /* all start with '/'), so it is taken as a request to go to    */
                /* that folder in the WINDOWS pane, the same test Image Save    */
                /* As's name box makes (Isv_TakeWindowsRequest,                 */
                /* ImageSaveForm.cs).  It is checked first, before the image    */
                /* tests below; with nothing open the box is read-only          */
                /* (Main_ApplyCommandAvailability), so it is typed only while   */
                /* an image is open.  A drive-relative "C:name" is not one:     */
                /* Windows would resolve it against that drive's own current    */
                /* folder, a place the user cannot see, so it falls through     */
                /* to the refusal below.  Only recorded here and posted: the    */
                /* handler does no more, and Main_RunDriveRequest acts on it    */
                /* from the loop.                                               */
                Main_sDriveRequest = sTyped;
                this.BeginInvoke(new MethodInvoker(Main_RunDriveRequest));
                return;
            }

            if (!Main_bImageOpen || sTyped.Length == 0 || sTyped[0] != '/')
            {   /* No image, or not an absolute image path: put the box back. */
                if (Main_bImageOpen)
                {   /* Show the directory the pane is really on. */
                    Main_txtImagePath.Text = Main_sImageDir;
                    Main_SetStatus("A path inside the image starts with '/'.");
                }

                return;
            }

            if (sTyped.Length > 1 && sTyped.EndsWith("/"))
            {   /* "/etc/" means "/etc", the form every other path in the   */
                /* pane has.  The slash must go: the engine refuses an      */
                /* image path that ends in one (an empty last component,    */
                /* BFX_PathValid), so the listing would fail on it.         */
                sTyped = sTyped.TrimEnd('/');

                if (sTyped.Length == 0)
                {   /* It was all slashes: the root. */
                    sTyped = "/";
                }
            }

            /* The pane goes there; a path that does not exist is said by   */
            /* the listing, which also puts the box back.                   */
            Main_NavigateImage(sTyped);
        }

        /*--------------------------------------------------------------------*/
        /* Main_RunDriveRequest:                                              */
        /*                                                                    */
        /* A Windows folder typed into the image path box, acted on from the  */
        /* message loop after Main_OnImagePathKeyDown recorded it: the image  */
        /* box shows its own pane's place again (the request was not the      */
        /* image pane's), the Windows pane is shown and made the active one,  */
        /* and that pane is sent to the folder by the same pane action its    */
        /* own path box posts.  A request already taken leaves nothing to do. */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the Windows pane's navigation is posted, or nothing     */
        /*            waited.                                                 */
        /*--------------------------------------------------------------------*/
        private void Main_RunDriveRequest()
        {
            string sFolder;

            /* The folder recorded, taken so it is acted on once: two Enters    */
            /* before the loop runs post twice, and the second finds nothing.   */
            sFolder = Main_sDriveRequest;
            Main_sDriveRequest = "";
            if (sFolder.Length == 0)
            {   /* Already taken by an earlier post: nothing to do. */
                return;
            }

            /* Windows' separator throughout, and the drive alone made its   */
            /* root, so a letter and colon alone is the top of that drive.   */
            sFolder = sFolder.Replace('/', '\\');
            if (sFolder.Length == 2)
            {   /* The drive alone. */
                sFolder = sFolder + "\\";
            }

            /* The image's box put back to what the image pane shows -- the   */
            /* folder, or the no-image prompt -- since the request is not     */
            /* the image pane's.                                              */
            if (Main_bImageOpen)
            {   /* The directory the pane is really on. */
                Main_txtImagePath.Text = Main_sImageDir;
            }
            else
            {   /* Nothing open: the prompt again. */
                Main_txtImagePath.Text = MAIN_NO_IMAGE_TEXT;
            }

            if (!Main_tsmShowHost.Checked)
            {   /* The Windows pane is hidden: shown, as View > Show Windows   */
                /* Pane would show it -- the tick's own handler                */
                /* (Main_OnShowHostChanged) remembers it and lays the window   */
                /* out -- so the folder asked for can be seen.                 */
                Main_tsmShowHost.Checked = true;
            }

            /* The Windows pane made the active one, as its own toolbar       */
            /* buttons make it, then sent to the folder by the same pane      */
            /* action its path box posts (Main_OnHostPathKeyDown); a folder   */
            /* that cannot be shown is reported there, and the pane stays     */
            /* where it was.                                                  */
            Main_ActivatePane(true);
            Main_RequestPaneAction(HPA_enmHostAction.Navigate, null, sFolder);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnImagePathIconClicked:                                       */
        /*                                                                    */
        /* The folder icon before the image pane's path box was clicked: the  */
        /* image pane's picker (Main_PickImageFolder).                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the icon (unused).                                 */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the Open menu is showing, or the picker's input is      */
        /*            posted.                                                 */
        /*--------------------------------------------------------------------*/
        private void Main_OnImagePathIconClicked(object objSender, EventArgs evArgs)
        {
            /* The open menu with nothing open, else the folder browser. */
            Main_PickImageFolder();
        }

        /*--------------------------------------------------------------------*/
        /* Main_PickImageFolder:                                              */
        /*                                                                    */
        /* The image pane's folder picker: with nothing open, the menu that   */
        /* opens an image file or a device; otherwise the input for the       */
        /* image's own folder browser, which the worker admits before the     */
        /* browser is shown (Main_RenderMainFolderInput, MainCommand.cs).     */
        /* The image path icon's handler and View > Choose Folder             */
        /* (Main_OnChooseFolderClicked) call it.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the Open menu is showing, or the picker's input is      */
        /*            posted (a refusal reaches the status line), or the      */
        /*            status line says why not.                               */
        /*--------------------------------------------------------------------*/
        private void Main_PickImageFolder()
        {
            if (!Main_bImageOpen)
            {   /* Nothing open: offer an image file or a device, the menu hanging from the icon's bottom left. */
                Main_cmsOpenChoice.Show(Main_picImagePath, new Point(0, Main_picImagePath.Height));
                return;
            }

            if (Main_objCommandClient == null)
            {   /* No command system: said, and nothing posted -- the      */
                /* browser lists the image, which only the worker reads.   */
                Main_SetStatus("The image folder browser is not ready.");
                return;
            }

            /* The input, starting in the folder the pane shows now.  No      */
            /* dialog is made and nothing is read here -- both wait for the   */
            /* worker to admit the input.                                     */
            Main_PostMainInput(INK_enmInputKind.ImageFolder, Main_sImageDir, null);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnHostPathIconClicked:                                        */
        /*                                                                    */
        /* The folder icon before the Windows pane's path box was clicked:    */
        /* Windows' own folder picker (Main_PickHostFolder).                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the icon (unused).                                 */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the picker is requested.                                */
        /*--------------------------------------------------------------------*/
        private void Main_OnHostPathIconClicked(object objSender, EventArgs evArgs)
        {
            /* The Windows pane's picker. */
            Main_PickHostFolder();
        }

        /*--------------------------------------------------------------------*/
        /* Main_PickHostFolder:                                               */
        /*                                                                    */
        /* Windows' own folder picker for the Windows pane, starting at the   */
        /* folder the pane shows, the pane then going to the folder chosen    */
        /* -- posted as a pane action, so the picker is shown only once the   */
        /* worker admits it (Main_ExecutePaneAction, MainCommand.cs).  The    */
        /* Windows path                                                       */
        /* icon's handler and View > Choose Folder                            */
        /* (Main_OnChooseFolderClicked) call it.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the picker is requested; the pane goes to the folder    */
        /*            chosen, or stays if the dialog is cancelled.            */
        /*--------------------------------------------------------------------*/
        private void Main_PickHostFolder()
        {
            /* The pane action, with the folder the pane shows now as the   */
            /* picker's start; no dialog is shown here.                     */
            Main_RequestPaneAction(HPA_enmHostAction.PickFolder, null, Main_sHostDir);
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnChooseFolderClicked:                                        */
        /*                                                                    */
        /* View > Choose Folder (Ctrl+G): the folder picker for the active    */
        /* pane -- exactly what clicking that pane's folder icon does,        */
        /* for someone working from the keyboard.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item.                                     */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the active pane's picker has run.                       */
        /*--------------------------------------------------------------------*/
        private void Main_OnChooseFolderClicked(object objSender, EventArgs evArgs)
        {
            if (Main_bHostPaneActive)
            {   /* The Windows pane: Windows' folder picker. */
                Main_PickHostFolder();
            }
            else
            {   /* The image pane: the image's own picker, or the open menu (image file or device) when nothing is open. */
                Main_PickImageFolder();
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnHostPathKeyDown:                                            */
        /*                                                                    */
        /* Enter in the Windows path box goes to the folder typed there.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the path box.                                      */
        /*     evKey     : the key event.                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the pane navigates on Enter; other keys pass through.   */
        /*--------------------------------------------------------------------*/
        private void Main_OnHostPathKeyDown(object objSender, KeyEventArgs evKey)
        {
            if (evKey.KeyCode != Keys.Enter)
            {   /* Ordinary typing. */
                return;
            }

            /* Swallow the key so the box does not also beep. */
            evKey.Handled          = true;
            evKey.SuppressKeyPress = true;

            /* The pane goes to the folder typed, trimmed of spaces at       */
            /* either end; a folder that cannot be shown is Main_Navigate-   */
            /* Host's to deal with, as for any other way of getting there.   */
            Main_RequestPaneAction(HPA_enmHostAction.Navigate, null, Main_txtHostPath.Text.Trim());
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnFormClosing:                                                */
        /*                                                                    */
        /* The main window closing, by any route (the title bar's X, Alt+F4,  */
        /* File > Exit, the close after the worker's Stopped).  While the     */
        /* command worker is running the close is refused and an Exit posted  */
        /* instead: the worker closes everything in order and publishes       */
        /* Stopped, and the window is closed again then.  The close that is   */
        /* let through saves the window's placement, unloads the engine and   */
        /* removes the run's temporary folders.                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the window (unused).                               */
        /*     evClosing : the close; Cancel is set while the worker runs.    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the close refused with an Exit posted, or the window's  */
        /*            last state saved and the engine unloaded.               */
        /*--------------------------------------------------------------------*/
        private void Main_OnFormClosing(object objSender, FormClosingEventArgs evClosing)
        {
            Rectangle rcNormal;

            if (Main_bCommandStarted && !Main_bCommandStopped)
            {   /* The worker has started and not yet published Stopped:     */
                /* the close is refused and an Exit posted                   */
                /* (Main_RequestCommandExit).  The window must stay -- the   */
                /* Exit may ask questions, and editors and inputs answer     */
                /* through it -- and the UI thread is never made to wait     */
                /* for the worker.  A second close while the Exit runs       */
                /* posts another, which the worker drops.                    */
                evClosing.Cancel = true;
                Main_RequestCommandExit();
                return;
            }

            /* No more icon turns: the window they would update is going. */
            Main_tmrIcons.Stop();

            /* Placement.  RestoreBounds is the un-maximized rectangle, which   */
            /* is the one worth remembering whatever the state is now.          */
            if (this.WindowState == FormWindowState.Normal)
            {   /* Not maximized or minimized: the live bounds are the answer. */
                rcNormal = this.Bounds;
            }
            else
            {   /* Maximized or minimized: use what it would restore to. */
                rcNormal = this.RestoreBounds;
            }

            /* The rectangle stored, for Main_RestorePlacement at the next   */
            /* start.                                                        */
            SET_Settings.SET_WriteInt(SET_Settings.SET_WINDOW_LEFT, rcNormal.Left);
            SET_Settings.SET_WriteInt(SET_Settings.SET_WINDOW_TOP, rcNormal.Top);
            SET_Settings.SET_WriteInt(SET_Settings.SET_WINDOW_WIDTH, rcNormal.Width);
            SET_Settings.SET_WriteInt(SET_Settings.SET_WINDOW_HEIGHT, rcNormal.Height);

            if (this.WindowState == FormWindowState.Maximized)
            {   /* Remember that it was maximized. */
                SET_Settings.SET_WriteInt(SET_Settings.SET_WINDOW_MAX, 1);
            }
            else
            {   /* Remember that it was not. */
                SET_Settings.SET_WriteInt(SET_Settings.SET_WINDOW_MAX, 0);
            }

            /* The panes' shares of the width: the share itself is stored, as   */
            /* the user last set it -- NOT worked back from where the divider   */
            /* sits, which would lose a little to rounding every run and so     */
            /* creep the divider leftwards over many runs.                      */
            SET_Settings.SET_WriteInt(SET_Settings.SET_SPLIT_PERMILLE, Main_iSplitPermille);

            /* Only here -- the worker stopped, or never started -- is the       */
            /* engine unloaded: nothing can call it any more.  Then the run's    */
            /* temporary folders are removed (Main_SweepDragFolders), the work   */
            /* folders among them: the Exit has already closed every editor      */
            /* and input (or, with no worker, none was ever opened), so no       */
            /* file in them is still open.                                       */
            ENG_clsNative.ENG_Unload();
            Main_SweepDragFolders(true);
        }
    }
}
