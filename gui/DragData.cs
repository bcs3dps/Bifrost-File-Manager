/*----------------------------------------------------------------------------*/
/* DragData.cs                                                                */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* What a drag out of the image pane carries, and the small window shown      */
/* while its files are extracted for an outside drop.                         */
/*                                                                            */
/* The data object (DRG_clsImageDragData) offers two things.  A private       */
/* marker -- the drag's own random identity -- by which the program's own     */
/* Windows pane knows the drag and copies the frozen selection through the    */
/* worker (MainForm.cs).  And a FileDrop: real Windows paths, which an        */
/* outside target such as Explorer needs, so the image items are extracted    */
/* into a temporary folder -- but only when that target asks for FileDrop     */
/* with the mouse released, never while the drag merely hovers.               */
/*                                                                            */
/* The feedback window (DFW_clsFeedbackWindow) runs on a thread of its own,   */
/* because the program's UI thread is held inside the other program's drop    */
/* call for as long as the extract takes.  Its progress comes from the        */
/* worker through the drag's own channel (CommandClient.cs, CLI_ExtractDrag)  */
/* and its Cancel goes straight to the worker -- neither through the main     */
/* window.                                                                    */
/*----------------------------------------------------------------------------*/

/* The framework: the base types (Array, Math, Environment's tick count, the   */
/* exceptions thrown and caught, event arguments), and the window, its         */
/* controls, the mouse-button state and the drag's DataObject.                 */
using System;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* DFW_CancelRequest:                                                     */
    /*                                                                        */
    /* How the feedback window's Cancel asks the extract to stop: one call    */
    /* that posts a Cancel naming the extract's own serial straight to the    */
    /* worker (MainForm.cs, DMC_RequestCancel, which uses SYS_Post) -- never  */
    /* through the main window, whose thread is held in the drop.  Called on  */
    /* the feedback window's thread.                                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the Cancel is posted.                                       */
    /*------------------------------------------------------------------------*/
    internal delegate void DFW_CancelRequest();

    /*------------------------------------------------------------------------*/
    /* DFP_clsFeedbackProgress -- one progress update on its way to the       */
    /* feedback window's thread: the window and the two values to show, held  */
    /* so a named method (DFP_Show) can be posted with them -- documentation  */
    /* table.                                                                 */
    /*------------------------------------------------------------------------*/
    internal class DFP_clsFeedbackProgress
    {
        private readonly DFW_clsFeedbackWindow Dfp_objWindow;  // the window to update; borrowed
        private readonly string Dfp_sItem;                     // the item's image path, as reported
        private readonly int Dfp_iPercent;                     // the current file's progress, 0 up (not yet capped at 100 -- DFW_ShowProgress does that)

        /*--------------------------------------------------------------------*/
        /* DFP_clsFeedbackProgress:                                           */
        /*                                                                    */
        /* One update, made on the thread that reports the progress           */
        /* (DFW_Report's) and painted later on the window's.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objWindow : the feedback window.                               */
        /*     sItem     : the item's image path.                             */
        /*     iPercent  : the current file's progress, in percent.           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the update.                                    */
        /*--------------------------------------------------------------------*/
        internal DFP_clsFeedbackProgress(DFW_clsFeedbackWindow objWindow,
            string sItem, int iPercent)
        {
            /* The three values, kept as given: nothing else touches them,   */
            /* and DFP_Show reads them once.                                 */
            Dfp_objWindow = objWindow;
            Dfp_sItem = sItem;
            Dfp_iPercent = iPercent;
        }

        /*--------------------------------------------------------------------*/
        /* DFP_Show:                                                          */
        /*                                                                    */
        /* The update painted -- the method posted to the window's thread     */
        /* (DFW_Report's BeginInvoke), so it runs where the controls may be   */
        /* touched.                                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window shows the item and its progress.             */
        /*--------------------------------------------------------------------*/
        internal void DFP_Show()
        {
            /* The window paints it; its controls stay its own. */
            Dfp_objWindow.DFW_ShowProgress(Dfp_sItem, Dfp_iPercent);
        }
    }

    /*------------------------------------------------------------------------*/
    /* DRG_Materialize:                                                       */
    /*                                                                        */
    /* How the drag data has its files extracted when an outside target asks  */
    /* for FileDrop: one call that runs the extract and waits for it          */
    /* (MainForm.cs, Dgm_clsDragMaterializer.DGM_Run, which calls             */
    /* Main_MaterializeDrag).  Called on the UI thread, inside the drop.      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string[] : the Windows paths of the items extracted (never null);  */
    /*                empty when the extract was cancelled, refused or could  */
    /*                not start.                                              */
    /*------------------------------------------------------------------------*/
    internal delegate string[] DRG_Materialize();

    /*------------------------------------------------------------------------*/
    /* DRG_clsImageDragData -- the data object an image-pane drag carries: a  */
    /* private marker the program's own Windows pane recognises, and a        */
    /* FileDrop that is extracted only when an outside target asks for it.    */
    /*                                                                        */
    /* Derives from DataObject because the framework requires it: DoDragDrop  */
    /* and the OLE drop target reach the data only through DataObject's       */
    /* virtual methods, so producing the files on demand means overriding     */
    /* them.  That is the one kind of inheritance allowed here -- from a      */
    /* framework base class, never from a class of ours.                      */
    /*------------------------------------------------------------------------*/
    internal sealed class DRG_clsImageDragData : DataObject
    {
        /* The drag's file plan and what became of it -- documentation    */
        /* table.  Touched only on the UI thread (the drop's calls come   */
        /* there).                                                        */
        private readonly string[] Drg_arrPlanned;             // the Windows paths the items WILL have once extracted (the temporary folder and each name), offered while the drag hovers; the caller's array, kept as given
        private readonly DRG_Materialize Drg_pfnMaterialize;  // runs the extract when an outside target asks for FileDrop with the mouse released
        private string[] Drg_arrReal;                         // the paths the extract produced; null until it has run, then kept -- empty when it was cancelled or failed -- and handed out again on every later request

        /*------------------------------------------------------------------*/
        /* DRG_clsImageDragData:                                            */
        /*                                                                  */
        /* The data for one drag: the marker stored, the file plan kept,    */
        /* and nothing extracted yet -- that waits for an outside target's  */
        /* request (Drg_ReadFileDrop).                                      */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     sMarkerFormat  : the private format the program's own        */
        /*                      Windows pane looks for.                     */
        /*     arrPlanned     : the Windows paths the items will have.      */
        /*     pfnMaterialize : runs the extract; returns the paths made,   */
        /*                      never null.                                 */
        /*     sMarkerValue   : the drag's identity, stored under the       */
        /*                      marker; optional (null gives "selection").  */
        /*                                                                  */
        /* Returns:                                                         */
        /*     (constructor) : the drag data.                               */
        /*------------------------------------------------------------------*/
        internal DRG_clsImageDragData(string sMarkerFormat, string[] arrPlanned, DRG_Materialize pfnMaterialize,
            string sMarkerValue = null)
        {
            /* The plan and the extract kept as given -- the caller builds   */
            /* the plan's array for this drag alone (MainForm.cs), so no     */
            /* copy is needed -- and nothing extracted yet.                  */
            Drg_arrPlanned = arrPlanned;
            Drg_pfnMaterialize = pfnMaterialize;
            Drg_arrReal = null;

            if (sMarkerValue == null)
            {   /* No identity given: a fixed value stands in.  No caller     */
                /* does this -- MainForm.cs passes the drag's own random      */
                /* marker (DGI_sMarker) -- and a drop on the Windows pane     */
                /* matches the value, so such a drag would not be taken for   */
                /* the program's own.                                         */
                sMarkerValue = "selection";
            }

            /* The marker stored as ordinary data under its format: the      */
            /* Windows pane's drop reads it to know the drag as its own      */
            /* (MainForm.cs compares it with DGI_sMarker), and reading it    */
            /* never asks for FileDrop, so nothing is extracted for a drop   */
            /* inside the program.                                           */
            this.SetData(sMarkerFormat, sMarkerValue);
        }

        /*---------------------------------------------------------------------*/
        /* GetDataPresent:                                                     */
        /*                                                                     */
        /* The Framework's query, made by a drop target while it decides       */
        /* whether to accept: is this format on offer?  FileDrop always is --  */
        /* it is rendered on demand, so answering must neither extract files   */
        /* nor read the mouse.  Every other format, the private marker among   */
        /* them, is answered by the DataObject's own storage, keeping its      */
        /* conversion rules.                                                   */
        /*                                                                     */
        /* An override because the framework requires it: a drop target's      */
        /* question reaches the data object only through this virtual method.  */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     sFormat      : the format the drop target asks about.           */
        /*     bAutoConvert : whether the Framework may convert to satisfy it. */
        /*                                                                     */
        /* Returns:                                                            */
        /*     bool : true for FileDrop, else the storage's answer.            */
        /*---------------------------------------------------------------------*/
        public override bool GetDataPresent(string sFormat, bool bAutoConvert)
        {
            if (sFormat == DataFormats.FileDrop)
            {   /* Offered, but not made until asked for (GetData). */
                return(true);
            }

            /* The stored formats, the marker among them. */
            return(base.GetDataPresent(sFormat, bAutoConvert));
        }

        /*--------------------------------------------------------------------*/
        /* GetFormats:                                                        */
        /*                                                                    */
        /* The Framework's list of the formats on offer: the stored ones plus */
        /* FileDrop, which is not stored -- it is made on demand -- so it is  */
        /* appended to a new array without touching the storage, and without  */
        /* extracting anything.                                               */
        /*                                                                    */
        /* An override because the framework requires it: the list of         */
        /* formats is asked of the data object only through this virtual      */
        /* method.                                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bAutoConvert : include the formats the storage can convert to. */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string[] : a new array of format names; the receiver owns it.  */
        /*--------------------------------------------------------------------*/
        public override string[] GetFormats(bool bAutoConvert)
        {
            string[] arrStored;
            string[] arrAll;

            /* The stored formats, and room for one more. */
            arrStored = base.GetFormats(bAutoConvert);
            arrAll = new string[arrStored.Length + 1];

            /* The stored names, then FileDrop at the end. */
            Array.Copy(arrStored, arrAll, arrStored.Length);
            arrAll[arrStored.Length] = DataFormats.FileDrop;

            /* Format names only: no temporary file exists yet. */
            return(arrAll);
        }

        /*--------------------------------------------------------------------*/
        /* GetData:                                                           */
        /*                                                                    */
        /* The Framework's request for a format's data.  A stored format --   */
        /* the marker an own-pane drop reads -- comes from the storage and    */
        /* must never cause the temporary extraction.  FileDrop is the one an */
        /* outside target (Explorer) asks for, and it needs the files before  */
        /* this call returns: Drg_ReadFileDrop has them extracted by the      */
        /* worker, under the reservation taken for the drag before it began,  */
        /* so the engine work is still the worker's, not this callback's.     */
        /*                                                                    */
        /* An override because the framework requires it: a drop target       */
        /* takes the data from the data object only through this virtual      */
        /* method.                                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sFormat      : the format the drop target wants.               */
        /*     bAutoConvert : allow the storage to convert a stored format.   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     object : the stored data, the file list (planned, then real),  */
        /*              or null when the format is not held.                  */
        /*--------------------------------------------------------------------*/
        public override object GetData(string sFormat, bool bAutoConvert)
        {
            if (sFormat != DataFormats.FileDrop)
            {   /* A stored format: no extraction. */
                return(base.GetData(sFormat, bAutoConvert));
            }

            /* FileDrop: the planned or extracted paths. */
            return(Drg_ReadFileDrop());
        }

        /*------------------------------------------------------------------*/
        /* Drg_ReadFileDrop:                                                */
        /*                                                                  */
        /* The FileDrop paths: the planned ones while the drag still        */
        /* hovers, and once the mouse is released the real ones, extracted  */
        /* on that first request and handed out again on every later one.   */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     None.                                                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     string[] : the planned paths, or the extracted ones (empty   */
        /*                when the extract was cancelled or failed).        */
        /*------------------------------------------------------------------*/
        private string[] Drg_ReadFileDrop()
        {
            if (Drg_arrReal != null)
            {   /* Already extracted: a target may ask more than once, and    */
                /* it is given the same paths -- even an empty list after a   */
                /* cancel -- rather than a second extract.                    */
                return(Drg_arrReal);
            }

            if (Control.MouseButtons != MouseButtons.None)
            {   /* A button still down: the drag is hovering, and a target     */
                /* may look at the names to decide whether to accept.  It is   */
                /* given the planned paths, which do not exist yet, so         */
                /* merely hovering over Explorer makes no temporary files.     */
                return(Drg_arrPlanned);
            }

            /* Released over an outside target: the files must exist before     */
            /* this returns.  The extract runs now and is waited for            */
            /* (Main_MaterializeDrag, through the worker's direct channel,      */
            /* since the UI thread is held in the drop); its result is kept,    */
            /* an empty one after a cancel too.  If it throws, Drg_arrReal is   */
            /* not set and the exception goes on to the drop's caller -- what   */
            /* the target then does is not established.                         */
            Drg_arrReal = Drg_pfnMaterialize();

            /* The extracted paths, which later requests get too. */
            return(Drg_arrReal);
        }
    }

    /*------------------------------------------------------------------------*/
    /* DFW_clsFeedbackWindow -- the "extracting for the drop" window.         */
    /*                                                                        */
    /* WHY IT HAS A THREAD OF ITS OWN.  While another program is taking a     */
    /* drop, this program's UI thread is held inside that program's call and  */
    /* cannot run a dialog: one opened there never starts (it was tried).  So */
    /* this window is created, shown and pumped by a SECOND thread that does  */
    /* nothing else.  The extract's progress is posted to that thread from    */
    /* the thread the extract's events arrive on; its Cancel posts a Cancel   */
    /* naming the extract's serial straight to the worker.  Its controls are  */
    /* touched only on its own thread, and DFW_Stop -- called once the        */
    /* extract has ended -- closes it there and waits for the thread.         */
    /*------------------------------------------------------------------------*/
    internal sealed class DFW_clsFeedbackWindow
    {
        /* The window's geometry and its update rate -- documentation table. */
        private const int DFW_WIDTH     = 480;    // client width, pixels
        private const int DFW_PAD       = 12;     // padding round the content, pixels
        private const int DFW_BAR       = 18;     // the progress bar's height, pixels
        private const int DFW_REPORT_MS = 100;    // least time between two progress updates shown, milliseconds

        /* The window, its thread and its state -- documentation table. */
        private bool Dfw_bCancel;       // true once Cancel has been pressed: the label then keeps "Cancelling ..." and Cancel stays off; set false by DFW_Start, then read and written only on the window's thread

        private System.Threading.Thread           Dfw_thrWindow;  // the thread that owns the window
        private System.Threading.ManualResetEvent Dfw_evtReady;   // set once the window exists and can be posted to
        private Form                              Dfw_frmWindow;  // the window; touched only by its own thread, or through BeginInvoke
        private Label                             Dfw_lblItem;    // the item being extracted
        private ProgressBar                       Dfw_prgBar;     // the current file's progress
        private Button                            Dfw_btnCancel;  // asks for the extract to stop
        private int                               Dfw_iLastTick;  // Environment.TickCount of last update; set by DFW_Start, then touched only by DFW_Report's thread
        private DFW_CancelRequest Dfw_pfnCommandCancel;           // posts the Cancel (DFW_AttachCommand); set before the window's thread starts, and not changed after

        /*------------------------------------------------------------------*/
        /* DFW_AttachCommand:                                               */
        /*                                                                  */
        /* Give the window the call its Cancel makes.  It must come before  */
        /* DFW_Start: the window's thread reads the field, and it is set    */
        /* before that thread exists so the thread sees it without a lock.  */
        /* Cancel stays off until DFW_ArmCommand, once the extract has its  */
        /* serial.                                                          */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     pfnCancel : posts the Cancel; safe to call from the window's */
        /*                 thread (it must not touch the main window).      */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : kept; throws ArgumentException when it is null or the */
        /*            window has already started.                           */
        /*------------------------------------------------------------------*/
        internal void DFW_AttachCommand(DFW_CancelRequest pfnCancel)
        {
            if (pfnCancel == null || Dfw_thrWindow != null)
            {   /* No call, or too late (the thread exists): refused, loud,   */
                /* since the caller's order is wrong.  MainForm.cs's catch    */
                /* ends the drag's extract with nothing handed over.          */
                throw new ArgumentException("Command Cancel must be attached before feedback starts.");
            }

            /* Kept for Dfw_OnCancelClicked, which only calls it -- the   */
            /* window does no engine work of its own.                     */
            Dfw_pfnCommandCancel = pfnCancel;
        }

        /*------------------------------------------------------------------*/
        /* DFW_ArmCommand:                                                  */
        /*                                                                  */
        /* Turn Cancel on, now that the extract has been posted and has its */
        /* serial -- a Cancel posted before that could name no run.  Called */
        /* by the command client right after the post (CommandClient.cs,    */
        /* CLI_ExtractDrag's posted hook), on the UI thread.                */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     None.                                                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the enable is posted to the window's thread; when the */
        /*            window is already gone, BeginInvoke throws and the    */
        /*            client asks the extract to stop (DWT_FailFeedback).   */
        /*------------------------------------------------------------------*/
        internal void DFW_ArmCommand()
        {
            /* Posted, not done here: only the window's thread may touch   */
            /* the button.  The extract may already have finished by the   */
            /* time it runs; the enable then only lights a button on a     */
            /* window about to close.                                      */
            Dfw_frmWindow.BeginInvoke(new MethodInvoker(Dfw_EnableCancel));
        }

        /*------------------------------------------------------------------*/
        /* Dfw_EnableCancel:                                                */
        /*                                                                  */
        /* The enable itself, on the window's thread.                       */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     None.                                                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : Cancel is on, unless it has already been pressed.     */
        /*------------------------------------------------------------------*/
        private void Dfw_EnableCancel()
        {
            if (!Dfw_bCancel)
            {   /* Not pressed: on.  It cannot have been pressed yet -- it   */
                /* is off from the start until now -- so the test is a       */
                /* guard that keeps "Cancelling ..." if it ever was.         */
                Dfw_btnCancel.Enabled = true;
            }
        }

        /*--------------------------------------------------------------------*/
        /* DFW_Start:                                                         */
        /*                                                                    */
        /* Start the window's thread and wait until the window exists, so the */
        /* first report cannot arrive before there is something to post to.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window is on screen and DFW_Report may be called.   */
        /*--------------------------------------------------------------------*/
        internal void DFW_Start()
        {
            /* Not cancelled; no update posted yet (so the first is shown --   */
            /* it is a forced item start in any case); the readiness event     */
            /* unset until the window's Shown handler sets it.                 */
            Dfw_bCancel   = false;
            Dfw_iLastTick = 0;
            Dfw_evtReady  = new System.Threading.ManualResetEvent(false);

            /* The window's thread.  STA, as the program's own UI thread is   */
            /* (Program.cs's Main is [STAThread]); a background thread, so    */
            /* it can never keep the program alive if something goes wrong    */
            /* before DFW_Stop; named for the debugger.                       */
            Dfw_thrWindow              = new System.Threading.Thread(Dfw_ThreadMain);
            Dfw_thrWindow.Name         = "BifrostFM drag feedback";
            Dfw_thrWindow.IsBackground = true;
            Dfw_thrWindow.SetApartmentState(System.Threading.ApartmentState.STA);
            Dfw_thrWindow.Start();

            /* Wait for Shown.  No timeout: the wait assumes the window is    */
            /* always made.  What happens if making it throws on its thread   */
            /* is not handled here and not established.                       */
            Dfw_evtReady.WaitOne();
        }

        /*--------------------------------------------------------------------*/
        /* Dfw_ThreadMain:                                                    */
        /*                                                                    */
        /* The window's thread: build the window, say it is ready the moment  */
        /* it has been shown, and run its message loop until it closes.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : returns when the window has closed.                     */
        /*--------------------------------------------------------------------*/
        private void Dfw_ThreadMain()
        {
            /* The window: a fixed dialog frame with no close box (it closes   */
            /* when the extract ends, not when the user says), not in the      */
            /* taskbar, and on top -- the other program's window is the        */
            /* active one, and this must not open hidden behind it.            */
            Dfw_frmWindow                 = new Form();
            Dfw_frmWindow.Text            = "Extracting for the drop";
            Dfw_frmWindow.FormBorderStyle = FormBorderStyle.FixedDialog;
            Dfw_frmWindow.ControlBox      = false;
            Dfw_frmWindow.ShowInTaskbar   = false;
            Dfw_frmWindow.TopMost         = true;
            Dfw_frmWindow.StartPosition   = FormStartPosition.CenterScreen;
            Dfw_frmWindow.Padding         = new Padding(DFW_PAD);

            /* The three controls: Cancel along the bottom, the bar and the    */
            /* item's name docked to the top.  First the button, sized to      */
            /* its text, its click posting the Cancel (Dfw_OnCancelClicked).   */
            Dfw_btnCancel          = new Button();
            Dfw_btnCancel.Text     = "Cancel";
            Dfw_btnCancel.AutoSize = true;
            Dfw_btnCancel.Dock     = DockStyle.Bottom;
            Dfw_btnCancel.Click   += Dfw_OnCancelClicked;

            /* Off until DFW_ArmCommand: the window is up before the        */
            /* extract is posted, and a Cancel pressed before the extract   */
            /* has its serial could name no run.                            */
            Dfw_btnCancel.Enabled = false;

            /* The bar: one file's progress, 0 to 100 (DFW_Report's scale). */
            Dfw_prgBar        = new ProgressBar();
            Dfw_prgBar.Dock   = DockStyle.Top;
            Dfw_prgBar.Height = DFW_BAR;

            /* The item's name: fixed in size (not AutoSize) so a long path   */
            /* is cut with "..." instead of widening the window, and one      */
            /* line tall plus the gap to the bar.                             */
            Dfw_lblItem              = new Label();
            Dfw_lblItem.Dock         = DockStyle.Top;
            Dfw_lblItem.AutoSize     = false;
            Dfw_lblItem.AutoEllipsis = true;
            Dfw_lblItem.Text         = "Starting ...";
            Dfw_lblItem.Height       = Dfw_lblItem.PreferredHeight + DFW_PAD;

            /* Docking runs in reverse order of adding (as found): Cancel,   */
            /* added last, takes the bottom first; the name, added second,   */
            /* the top; the bar, added first, the top below the name.        */
            Dfw_frmWindow.Controls.Add(Dfw_prgBar);
            Dfw_frmWindow.Controls.Add(Dfw_lblItem);
            Dfw_frmWindow.Controls.Add(Dfw_btnCancel);

            /* The size the content needs: the three heights, plus three       */
            /* pads -- the form's padding at top and bottom, and the gap       */
            /* left between the top-docked bar and the bottom-docked button.   */
            Dfw_frmWindow.ClientSize = new System.Drawing.Size(DFW_WIDTH, Dfw_lblItem.Height + Dfw_prgBar.Height +
                                                               Dfw_btnCancel.PreferredSize.Height + (3 * DFW_PAD));

            /* Ready as soon as the window is really on screen: the first   */
            /* report then lands on a window the user can already see.      */
            Dfw_frmWindow.Shown += Dfw_OnShown;

            /* This thread's message loop, shown with the window; it returns   */
            /* when the window closes (DFW_Stop), which ends the thread.       */
            Application.Run(Dfw_frmWindow);
        }

        /*------------------------------------------------------------------*/
        /* Dfw_OnShown:                                                     */
        /*                                                                  */
        /* The window is on screen: let DFW_Start's wait go, so the         */
        /* extract -- and with it the first progress update -- starts only  */
        /* once there is a window to show it on.                            */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objSender : the window; unused.                              */
        /*     evArgs    : unused.                                          */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : DFW_Start returns.                                    */
        /*------------------------------------------------------------------*/
        private void Dfw_OnShown(object objSender, EventArgs evArgs)
        {
            /* The event DFW_Start waits on. */
            Dfw_evtReady.Set();
        }

        /*------------------------------------------------------------------*/
        /* Dfw_CloseWindow:                                                 */
        /*                                                                  */
        /* The close DFW_Stop posts, run on the window's own thread -- the  */
        /* one thread that may touch the window.                            */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     None.                                                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the window closes, which ends this thread's           */
        /*            Application.Run, and so the thread DFW_Stop joins.    */
        /*------------------------------------------------------------------*/
        private void Dfw_CloseWindow()
        {
            /* Closed; DFW_Stop is waiting for the thread to end. */
            Dfw_frmWindow.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Dfw_OnCancelClicked:                                               */
        /*                                                                    */
        /* Cancel: ask the extract to stop, and say so until it has.  The     */
        /* window stays up -- DFW_Stop closes it once the extract has ended,  */
        /* stopped or not.                                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the Cancel is posted and the window says "Cancelling".  */
        /*--------------------------------------------------------------------*/
        private void Dfw_OnCancelClicked(object objSender, EventArgs evArgs)
        {
            /* "Cancelling ..." shown, and kept: the flag stops later        */
            /* progress updates replacing it (DFW_ShowProgress), and the     */
            /* button goes off so it cannot be pressed twice.  The flag is   */
            /* only the window's; the stop itself is the worker's.           */
            Dfw_bCancel           = true;
            Dfw_btnCancel.Enabled = false;
            Dfw_lblItem.Text      = "Cancelling ...";

            /* The Cancel, posted on this window's thread straight to the   */
            /* worker (DMC_RequestCancel's SYS_Post), never through the     */
            /* main window, whose thread is held in the drop.               */
            if (Dfw_pfnCommandCancel != null)
            {   /* Attached (always, in the program -- DFW_AttachCommand      */
                /* comes first).  The Cancel names the extract's serial, so   */
                /* one that arrives after the extract has ended is refused    */
                /* by the worker and stops nothing else.                      */
                Dfw_pfnCommandCancel();
            }
        }

        /*--------------------------------------------------------------------*/
        /* DFW_Report:                                                        */
        /*                                                                    */
        /* Show the current item and its progress -- called on the thread     */
        /* the extract's events arrive on (MainForm.cs, DMC_Report), never    */
        /* the window's.  Updates are posted without waiting, and no more     */
        /* often than every DFW_REPORT_MS unless bForce says the item just    */
        /* changed.                                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sItem    : the item's image path.                              */
        /*     ullDone  : bytes of the current file copied so far.            */
        /*     ullTotal : the current file's size (0 for a directory).        */
        /*     bForce   : true to show this update whatever the interval.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the update is posted to the window's thread.            */
        /*--------------------------------------------------------------------*/
        internal void DFW_Report(string sItem, ulong ullDone, ulong ullTotal, bool bForce)
        {
            int iTick;
            int iPercent;
            DFP_clsFeedbackProgress objFrame;

            /* The rate limit, so a fast extract does not flood the window's   */
            /* queue with posts.  The subtraction is done unchecked: the       */
            /* tick count is a 32-bit count of milliseconds that wraps, and    */
            /* the difference of two wrapped values is still the interval.     */
            iTick = Environment.TickCount;

            if (!bForce && unchecked(iTick - Dfw_iLastTick) < DFW_REPORT_MS)
            {   /* Too soon after the last update: dropped, not queued -- a   */
                /* later update says more than this one would have.           */
                return;
            }

            /* This update goes out; its time is the next one's yardstick.   */
            /* The bar shows 0 for a directory or an empty file (no size).   */
            Dfw_iLastTick = iTick;
            iPercent      = 0;

            if (ullTotal != 0)
            {   /* A size to measure against: the bytes done as a whole        */
                /* percentage, rounded down.  The multiply comes first, in     */
                /* 64 bits, so a file under about 184 PB cannot overflow it.   */
                iPercent = (int)((ullDone * 100UL) / ullTotal);
            }

            /* Posted to the window's thread, which alone may touch its      */
            /* controls: the values go in an update object whose named       */
            /* method is posted (DFP_Show).  The percent can pass 100 when   */
            /* a file grows while it is read; the painting caps it.          */
            try
            {   /* The window may be closing. */
                objFrame = new DFP_clsFeedbackProgress(this, sItem, iPercent);
                Dfw_frmWindow.BeginInvoke(new MethodInvoker(objFrame.DFP_Show));
            }
            catch (InvalidOperationException)
            {   /* No window to tell: the update is dropped, which costs only   */
                /* a frame of feedback.                                         */
            }
        }

        /*--------------------------------------------------------------------*/
        /* DFW_ShowProgress:                                                  */
        /*                                                                    */
        /* One update painted, on the window's own thread (DFP_Show runs it   */
        /* there).                                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sItem    : the item's image path.                              */
        /*     iPercent : the current file's progress, in percent, 0 up.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the label and the bar updated -- the label not, once    */
        /*            Cancel has been pressed.                                */
        /*--------------------------------------------------------------------*/
        internal void DFW_ShowProgress(string sItem, int iPercent)
        {
            if (!Dfw_bCancel)
            {   /* Not cancelling: the item's path shown.  After a Cancel      */
                /* the label keeps "Cancelling ..." while the extract stops.   */
                Dfw_lblItem.Text = sItem;
            }

            /* The bar, capped at its maximum of 100: a file that grows     */
            /* while it is read gives more, and a ProgressBar refuses a     */
            /* value past its maximum by throwing (as found).  The counts   */
            /* are unsigned, so the percent is not negative -- short of a   */
            /* file growing some twenty million times while it is read,     */
            /* which would wrap the int cast.                               */
            Dfw_prgBar.Value = Math.Min(100, iPercent);
        }

        /*--------------------------------------------------------------------*/
        /* DFW_Stop:                                                          */
        /*                                                                    */
        /* The extract has ended: close the window and wait for its thread.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window is gone and its thread has ended.            */
        /*--------------------------------------------------------------------*/
        internal void DFW_Stop()
        {
            try
            {   /* Close it on its own thread -- only that thread may touch    */
                /* the window, so the Close is posted there; closing it ends   */
                /* that thread's Application.Run, and with it the thread.      */
                Dfw_frmWindow.BeginInvoke(new MethodInvoker(Dfw_CloseWindow));
            }
            catch (InvalidOperationException)
            {   /* Already gone: its thread has ended or is ending, so the   */
                /* join below still returns.                                 */
            }

            /* Wait for the thread to finish its loop and end, so nothing of   */
            /* the window outlives the drop; then free the readiness event,    */
            /* which nothing waits on any more.                                */
            Dfw_thrWindow.Join();
            Dfw_evtReady.Close();
        }
    }
}
