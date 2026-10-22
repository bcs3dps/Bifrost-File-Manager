/*----------------------------------------------------------------------------*/
/* BusyModalForm.cs                                                           */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* A small "working" window -- a heading, a line of explanation and a         */
/* progress bar, with no Cancel -- shown only when work outlasts a delay, so  */
/* quick work shows nothing at all.  Two ways to use it:                      */
/*                                                                            */
/*   - BMF_Run: the work is handed over and run on a BackgroundWorker, the    */
/*     window shown MODAL over its owner meanwhile (a marquee bar), and the   */
/*     call returns only once the work has finished -- whatever happened to   */
/*     the window.  The structured editor's loads and saves and the hex       */
/*     editor's save use it (json\JsonEditForm.cs, HexEditForm.cs).           */
/*   - BMF_BeginUiProgress ... BMF_EndUiProgress: for work that must stay on  */
/*     the UI thread (it touches controls), the window is shown MODELESS once */
/*     the delay passes and its bar is moved by the caller's reports between  */
/*     steps (the structured editor's tree, json\VoorheesTreeView.cs).        */
/*                                                                            */
/* Neither way calls the engine: this is for the program's own work on its    */
/* own data.                                                                  */
/*----------------------------------------------------------------------------*/

/* The framework: the base types (Action, the exceptions caught, Math), the   */
/* BackgroundWorker, the Stopwatch for the delay, the fonts and sizes,        */
/* P/Invoke for PeekMessage, the ManualResetEvent that says the work is       */
/* done, and the window and its controls.                                     */
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* BMF_clsBusyModalForm -- the delayed busy box (the file header says     */
    /* what it shows).  A plain Form: its base class is declared here with    */
    /* the rest of its UI.                                                    */
    /*                                                                        */
    /* Derives from Form because the framework requires it: WinForms makes a  */
    /* window only as a Form subclass.  That is the one kind of inheritance   */
    /* allowed here -- from a framework base class, never from a class of     */
    /* ours.                                                                  */
    /*------------------------------------------------------------------------*/
    public class BMF_clsBusyModalForm : Form
    {
        /* The caption, the layout's sizes and the PeekMessage flag --   */
        /* documentation table.  Text sizes are measured, not fixed.     */
        public static string BMF_sDefaultFormCaption = "Working";  // the window's caption when the caller gives none; nothing in the program changes it
        private const int BMF_PAD = 16;                            // padding round the content and between the rows, pixels
        private const float BMF_TITLE_POINTS_UP = 1f;              // the heading's size above the dialog font, points (shown bold)
        private const int BMF_MINBARWIDTH = 260;                   // the bar's least width, pixels; wider when a text line is
        private const int BMF_BARMAX = 1000;                       // the measured bar's range: tenths of a percent
        private const uint BMF_PM_NOREMOVE = 0x0000;               // PeekMessage's PM_NOREMOVE: look at the queue, take nothing out

        /* The window's controls and the work's state -- documentation table. */
        private Label Bmf_lblTitle;              // the heading; its bold font is made here and freed with the window (Bmf_OnDisposed)
        private Label Bmf_lblBody;               // the explanation, in the dialog font
        private ProgressBar Bmf_prgBar;          // a marquee for BMF_Run; a measured bar after BMF_BeginUiProgress
        private BackgroundWorker Bmf_bwWorker;   // BMF_Run only: runs the work off the UI thread
        private Action Bmf_pfnWork;              // BMF_Run's work, the caller's; null in the UI-progress use
        private Exception Bmf_exError;           // what the work threw, or null; set BEFORE Bmf_evtDone is
        private ManualResetEvent Bmf_evtDone;    // set by the worker as its very last act, so a wait on it means the work and its result are both done; closed by BMF_Run or BMF_EndUiProgress
        private bool Bmf_bWorkStarted;           // true once RunWorkerAsync has returned: there is work to wait for
        private bool Bmf_bShown;                 // true once the window is on screen; stays false when quick work never shows it
        private Form Bmf_frmOwner;               // UI-progress: the window to centre over (not made the owner); may be null
        private Stopwatch Bmf_swElapsed;         // UI-progress: time since BMF_BeginUiProgress, for the delay
        private int Bmf_iShowDelayMs;            // UI-progress: how long the work may run before the window appears, milliseconds
        private int Bmf_iBarValue;               // UI-progress: the bar's value last painted; -1 at the start so the first report paints

        /* Win32's MSG, for PeekMessage to fill -- documentation table.  The   */
        /* fields are filled and never read: only the call matters.            */
        [StructLayout(LayoutKind.Sequential)]
        private struct Bmf_stMessage
        {
            public IntPtr BMM_hWindow; // destination window handle
            public uint BMM_uiMessage; // message number
            public IntPtr BMM_pWord;   // WPARAM, pointer-sized
            public IntPtr BMM_pLong;   // LPARAM, pointer-sized
            public uint BMM_uiTime;    // posted time, milliseconds
            public int BMM_iX;         // cursor screen X, pixels
            public int BMM_iY;         // cursor screen Y, pixels
        }

        /*------------------------------------------------------------------*/
        /* Bmf_PeekMessage:                                                 */
        /*                                                                  */
        /* user32: look at this thread's message queue without taking       */
        /* anything out.  Windows dispatches messages SENT to the thread's  */
        /* windows during the call (documented), so a peek is not free of   */
        /* re-entry; posted messages, input included, stay queued.          */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     stMessage : receives the message looked at; not used.        */
        /*     hWindow   : zero: every window of this thread.               */
        /*     uiMinimum : 0, with uiMaximum 0: every message number.       */
        /*     uiMaximum : 0, with uiMinimum 0.                             */
        /*     uiRemove  : BMF_PM_NOREMOVE.                                 */
        /*                                                                  */
        /* Returns:                                                         */
        /*     bool : whether a message was there; the caller ignores it.   */
        /*------------------------------------------------------------------*/
        [DllImport("user32.dll", EntryPoint = "PeekMessage")]
        private static extern bool Bmf_PeekMessage(out Bmf_stMessage stMessage,
            IntPtr hWindow, uint uiMinimum, uint uiMaximum, uint uiRemove);

        /* Making the window, and BMF_Run. */

        /*------------------------------------------------------------------*/
        /* BMF_clsBusyModalForm:                                            */
        /*                                                                  */
        /* Make the window, not shown and with no work started -- for       */
        /* BMF_Run (with its work) or BMF_BeginUiProgress (without).  The   */
        /* dialog font is the caller's and is never freed here; the         */
        /* heading's bold font and the done-event are the window's own.     */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     fntDialog : the dialog font.                                 */
        /*     sTitle    : the heading.                                     */
        /*     sBody     : the explanation, or null for none.               */
        /*     pfnWork   : BMF_Run's work, or null.                         */
        /*     sCaption  : the caption, or null / "" for                    */
        /*                 BMF_sDefaultFormCaption.                         */
        /*                                                                  */
        /* Returns:                                                         */
        /*     (constructor) : the window.                                  */
        /*------------------------------------------------------------------*/
        private BMF_clsBusyModalForm(Font fntDialog, string sTitle, string sBody,
            Action pfnWork, string sCaption)
        {
            /* The work kept, nothing thrown, nothing started or shown, and   */
            /* the done-event unset.  The UI-progress use has no work but     */
            /* makes the event too, so both uses close it the same way.       */
            Bmf_pfnWork = pfnWork;
            Bmf_exError = null;
            Bmf_bWorkStarted = false;
            Bmf_bShown = false;
            Bmf_evtDone = new ManualResetEvent(false);

            /* The caption: the caller's, or the default when it gives none. */
            if (string.IsNullOrEmpty(sCaption))
            {   /* None given: the default ("Working"). */
                Text = BMF_sDefaultFormCaption;
            }
            else
            {   /* The caller's own. */
                Text = sCaption;
            }

            /* The controls, made and laid out; nothing shown yet. */
            Bmf_BuildUi(fntDialog, sTitle, sBody);
        }

        /*------------------------------------------------------------------*/
        /* BMF_Run:                                                         */
        /*                                                                  */
        /* Run the work off the UI thread, show the window modal over its   */
        /* owner if the work outlasts the delay, and return once the work   */
        /* has FINISHED -- never sooner, whatever happens to the window --  */
        /* so the caller may free or reuse what the work was using the      */
        /* moment it returns.  What the work threw is handed back, not      */
        /* thrown; a failure to show the window is thrown, but only after   */
        /* the work has finished.                                           */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     frmOwner     : the window it is modal over.                  */
        /*     fntDialog    : the dialog font.                              */
        /*     sTitle       : the heading.                                  */
        /*     sBody        : the explanation, or null.                     */
        /*     pfnWork      : the work: it must touch no control (it runs   */
        /*                    off the UI thread); null does nothing.        */
        /*     sCaption     : the caption, or null / "" for the default.    */
        /*     iShowDelayMs : how long the work may run before the window   */
        /*                    appears, milliseconds; below 0 counts as 0.   */
        /*                                                                  */
        /* Returns:                                                         */
        /*     Exception : what the work threw, or null when it finished    */
        /*                 normally.                                        */
        /*------------------------------------------------------------------*/
        public static Exception BMF_Run(Form frmOwner, Font fntDialog,
            string sTitle, string sBody, Action pfnWork, string sCaption, int iShowDelayMs)
        {
            BMF_clsBusyModalForm frmBusy;
            Exception exResult;

            if (pfnWork == null)
            {   /* No work: nothing to run or wait for, and no window. */
                return(null);
            }

            /* The window, with the work; disposed at the using block's end,   */
            /* which comes only after the wait in its finally.                 */
            frmBusy = new BMF_clsBusyModalForm(fntDialog, sTitle, sBody, pfnWork, sCaption);
            using (frmBusy)
            {   /* One window for this one run. */
                try
                {   /* The work started on a BackgroundWorker.  The worker     */
                    /* sets the done-event itself (Bmf_OnWorkerDoWork), so     */
                    /* knowing the work has finished never depends on the UI   */
                    /* loop running the completion; the completion only        */
                    /* closes the window.  Started is noted only once          */
                    /* RunWorkerAsync has returned, so a start that throws     */
                    /* leaves nothing to wait for.                             */
                    frmBusy.Bmf_bwWorker = new BackgroundWorker();
                    frmBusy.Bmf_bwWorker.DoWork += frmBusy.Bmf_OnWorkerDoWork;
                    frmBusy.Bmf_bwWorker.RunWorkerCompleted += frmBusy.Bmf_OnWorkerCompleted;
                    frmBusy.Bmf_bwWorker.RunWorkerAsync();
                    frmBusy.Bmf_bWorkStarted = true;

                    /* The delay: work done within it shows no window at all   */
                    /* (no flash).  Otherwise the window is shown modal; its   */
                    /* loop runs until the completion (or Bmf_OnShown, when    */
                    /* the work beat the window) closes it.                    */
                    if (!frmBusy.Bmf_evtDone.WaitOne(Math.Max(0, iShowDelayMs)))
                    {   /* Still running after the delay: the window, modal. */
                        frmBusy.ShowDialog(frmOwner);
                    }
                }
                finally
                {   /* Whatever happened above -- the window closed, or         */
                    /* failed to show -- the work may still be running, so it   */
                    /* is waited for before the event is closed and the         */
                    /* window disposed.  This is what lets a caller release     */
                    /* what the work uses straight after the return (the        */
                    /* structured editor's save does: JsonEditForm.cs).         */
                    if (frmBusy.Bmf_bWorkStarted)
                    {   /* Started: wait for the end, however long. */
                        frmBusy.Bmf_evtDone.WaitOne();
                    }

                    /* The event closed: the worker has set it and is done    */
                    /* with it, and a completion still queued does not read   */
                    /* it (Bmf_OnWorkerCompleted tests the window instead).   */
                    frmBusy.Bmf_evtDone.Close();
                }

                /* What the work threw -- read only after the wait, so the   */
                /* worker has written it (it is set before the event).       */
                exResult = frmBusy.Bmf_exError;
            }

            /* The work's own failure, or null. */
            return(exResult);
        }

        /* The UI-progress use: work that stays on the UI thread. */

        /*--------------------------------------------------------------------*/
        /* BMF_BeginUiProgress:                                               */
        /*                                                                    */
        /* Start the window for work the caller does itself, on the UI        */
        /* thread: nothing is shown yet -- BMF_ReportUiProgress shows it      */
        /* once the delay passes (BMF_ShowNow at once).  The caller must      */
        /* call BMF_EndUiProgress, in a finally, whether or not the window    */
        /* ever appeared.  frmOwner only places the window; it is not made    */
        /* its owner.                                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmOwner     : the window to centre over, or null for the      */
        /*                    screen.                                         */
        /*     fntDialog    : the dialog font.                                */
        /*     sTitle       : the heading.                                    */
        /*     sBody        : the explanation, or null.                       */
        /*     sCaption     : the caption, or null / "" for the default.      */
        /*     iShowDelayMs : how long before the window may appear,          */
        /*                    milliseconds; below 0 counts as 0.              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     BMF_clsBusyModalForm : the window, not shown; the caller's     */
        /*                            until BMF_EndUiProgress.                */
        /*--------------------------------------------------------------------*/
        public static BMF_clsBusyModalForm BMF_BeginUiProgress(Form frmOwner,
            Font fntDialog, string sTitle, string sBody, string sCaption, int iShowDelayMs)
        {
            BMF_clsBusyModalForm frmProgress;

            /* The window, with no work.  The clock starts now: the window   */
            /* appears once it passes the delay.  The last value -1 makes    */
            /* the first report paint whatever it says.                      */
            frmProgress = new BMF_clsBusyModalForm(fntDialog, sTitle, sBody, null, sCaption);
            frmProgress.Bmf_frmOwner = frmOwner;
            frmProgress.Bmf_iShowDelayMs = Math.Max(0, iShowDelayMs);
            frmProgress.Bmf_iBarValue = -1;
            frmProgress.Bmf_swElapsed = Stopwatch.StartNew();

            /* A measured bar instead of the marquee: the caller's work     */
            /* holds the UI thread, so a marquee would not animate -- the   */
            /* bar moves only when a report sets it.  0 to BMF_BARMAX.      */
            frmProgress.Bmf_prgBar.Style = ProgressBarStyle.Continuous;
            frmProgress.Bmf_prgBar.Minimum = 0;
            frmProgress.Bmf_prgBar.Maximum = BMF_BARMAX;

            /* The window, not shown; BMF_EndUiProgress is still owed even   */
            /* if the work ends before it ever appears.                      */
            return(frmProgress);
        }

        /*------------------------------------------------------------------*/
        /* BMF_ReportUiProgress:                                            */
        /*                                                                  */
        /* Report progress between steps of the caller's work: once the     */
        /* delay has passed, the window appears (if it has not) and the bar */
        /* moves.  Inside a long call the caller cannot break into -- a     */
        /* control's own -- BMF_UpdateUiProgress moves the bar of a window  */
        /* already shown without ever showing one.                          */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     lDone  : the units of work done.                             */
        /*     lTotal : the units in all; 0 or less gives an empty bar.     */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the window shown and its bar moved, or nothing yet.   */
        /*------------------------------------------------------------------*/
        public void BMF_ReportUiProgress(long lDone, long lTotal)
        {
            if (!Bmf_bShown && Bmf_swElapsed.ElapsedMilliseconds < Bmf_iShowDelayMs)
            {   /* Not shown, and the delay not over: nothing yet, so quick   */
                /* work never flashes the window.                             */
                return;
            }

            /* Shown now if it was not (BMF_ShowNow does nothing once it is),   */
            /* then the bar moved.                                              */
            BMF_ShowNow();
            BMF_UpdateUiProgress(lDone, lTotal);
        }

        /*------------------------------------------------------------------*/
        /* BMF_ShowNow:                                                     */
        /*                                                                  */
        /* Show the UI-progress window at once, whatever the delay -- for a */
        /* caller about to make a long call it cannot break into to show it */
        /* later (VoorheesTreeView.cs, before a control fills itself).  A   */
        /* window already shown is left as it is.                           */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     None.                                                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the window is on screen and painted.                  */
        /*------------------------------------------------------------------*/
        public void BMF_ShowNow()
        {
            Rectangle rcOwner;

            if (Bmf_bShown)
            {   /* Already on screen: nothing to do. */
                return;
            }

            /* Placed by hand: a modeless window with no owner has nothing   */
            /* for CenterParent to centre on, so the rectangle is the        */
            /* given window's, or the screen's working area.                 */
            StartPosition = FormStartPosition.Manual;
            if (Bmf_frmOwner != null)
            {   /* A window given: centred over it, without it becoming the   */
                /* owner.                                                     */
                rcOwner = Bmf_frmOwner.Bounds;
            }
            else
            {   /* None: centred on the primary screen's working area. */
                rcOwner = Screen.PrimaryScreen.WorkingArea;
            }

            /* The window's top-left, so its centre is the rectangle's. */
            Location = new Point(rcOwner.Left + (rcOwner.Width - Width) / 2,
                rcOwner.Top + (rcOwner.Height - Height) / 2);

            /* Shown, noted, and painted at once with Refresh: the caller's   */
            /* work holds the UI thread, so no message loop runs to paint     */
            /* it later.                                                      */
            Show();
            Bmf_bShown = true;
            Refresh();
        }

        /*------------------------------------------------------------------*/
        /* BMF_UpdateUiProgress:                                            */
        /*                                                                  */
        /* Move the bar of a window already shown -- it never shows one.    */
        /* Safe inside a control's own long call: it takes no message out   */
        /* of the queue, input included.  Windows may still run messages    */
        /* SENT to the thread's windows during its peek, so a handler can   */
        /* run inside it: it is not a guard against re-entry.               */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     lDone  : the units of work done.                             */
        /*     lTotal : the units in all; 0 or less gives an empty bar.     */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the bar shows the new value, when it changed.         */
        /*------------------------------------------------------------------*/
        public void BMF_UpdateUiProgress(long lDone, long lTotal)
        {
            Bmf_stMessage stMessage;
            int iValue;

            if (!Bmf_bShown)
            {   /* Not shown: no bar to move. */
                return;
            }

            /* A peek at the queue, taking nothing out.  Its purpose is not   */
            /* recorded; the effect documented is that Windows treats a       */
            /* thread that looks at its queue as responding, so the window    */
            /* is not marked "Not Responding" while the work runs (as         */
            /* found -- not established that this is why it is here).         */
            Bmf_PeekMessage(out stMessage, IntPtr.Zero, 0, 0, BMF_PM_NOREMOVE);

            /* The bar's value: the done fraction of BMF_BARMAX, in whole    */
            /* steps, held to 0 .. BMF_BARMAX (a done past the total, or a   */
            /* negative one, does not overflow the bar).                     */
            if (lTotal > 0)
            {   /* A total: the fraction, clamped. */
                iValue = (int)Math.Min((long)BMF_BARMAX,
                    Math.Max(0L, lDone * BMF_BARMAX / lTotal));
            }
            else
            {   /* No total to measure against: an empty bar. */
                iValue = 0;
            }

            if (iValue != Bmf_iBarValue)
            {   /* A new value: set and painted now -- nothing else will     */
                /* paint it while the work holds the thread.  Set one step   */
                /* past the value and then back: a themed bar animates       */
                /* toward a value it is raised to, but jumps to one it is    */
                /* lowered to, so it shows the true value at once (as        */
                /* found).                                                   */
                Bmf_iBarValue = iValue;
                if (iValue < BMF_BARMAX)
                {   /* Room above the value for the step past it. */
                    Bmf_prgBar.Value = iValue + 1;
                    Bmf_prgBar.Value = iValue;
                }
                else
                {   /* The maximum: nothing above it, so set as it is. */
                    Bmf_prgBar.Value = iValue;
                }

                /* The bar repainted now, without running the message loop. */
                Bmf_prgBar.Update();
            }
        }

        /*------------------------------------------------------------------*/
        /* BMF_EndUiProgress:                                               */
        /*                                                                  */
        /* End the UI-progress window: closed if it was shown, then freed   */
        /* (its done-event, its controls, its heading font).  The caller    */
        /* calls it in a finally; a second call does nothing.               */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     None.                                                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the window is gone, shown or not.                     */
        /*------------------------------------------------------------------*/
        public void BMF_EndUiProgress()
        {
            if (IsDisposed)
            {   /* Already ended: nothing left to free. */
                return;
            }

            if (Bmf_bShown)
            {   /* Shown: closed first.  (No work is running, so the   */
                /* closing guard lets it close.)                       */
                Close();
            }

            /* Freed: the event (no worker uses it in this use), then the      */
            /* window, which frees its controls and, through Bmf_OnDisposed,   */
            /* the heading's font.                                             */
            Bmf_evtDone.Close();
            Dispose();
        }

        /* The window's layout and events. */

        /*------------------------------------------------------------------*/
        /* Bmf_BuildUi:                                                     */
        /*                                                                  */
        /* Make the window's three rows -- heading, explanation, bar -- in  */
        /* the dialog font, with no Cancel and no close box: the work       */
        /* cannot be stopped from here.  Laid out to its text before it is  */
        /* shown; the same for both uses.                                   */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     fntDialog : the dialog font.                                 */
        /*     sTitle    : the heading.                                     */
        /*     sBody     : the explanation, or null.                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the controls are made and placed.                     */
        /*------------------------------------------------------------------*/
        private void Bmf_BuildUi(Font fntDialog, string sTitle, string sBody)
        {
            /* A fixed dialog frame with no control box (so no close        */
            /* button and no system menu), not in the taskbar, no icon,     */
            /* centred on its owner when shown modal (the UI-progress use   */
            /* places itself, BMF_ShowNow), in the dialog font.             */
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ControlBox = false;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = fntDialog;

            /* The heading, in a bold font one step up made here -- the one    */
            /* font the window owns, freed when it is disposed                 */
            /* (Bmf_OnDisposed); the dialog font itself is never freed here.   */
            Bmf_lblTitle = new Label();
            Bmf_lblTitle.Text = sTitle;
            Bmf_lblTitle.AutoSize = true;
            Bmf_lblTitle.Font = new Font(fntDialog.FontFamily,
                fntDialog.SizeInPoints + BMF_TITLE_POINTS_UP, FontStyle.Bold);
            Controls.Add(Bmf_lblTitle);
            Disposed += Bmf_OnDisposed;

            /* The explanation, in the window's dialog font; empty when the   */
            /* caller gives none.                                             */
            Bmf_lblBody = new Label();
            Bmf_lblBody.Text = "";
            if (sBody != null)
            {   /* Given: shown under the heading. */
                Bmf_lblBody.Text = sBody;
            }

            /* Sized to its text, and added before it is measured           */
            /* (Bmf_Layout), so it measures in the font it takes from the   */
            /* window (as found).                                           */
            Bmf_lblBody.AutoSize = true;
            Controls.Add(Bmf_lblBody);

            /* The bar: a marquee, moving on its own, for BMF_Run, where the   */
            /* work's progress is not known (MarqueeAnimationSpeed 30 ms a     */
            /* step); BMF_BeginUiProgress turns it into a measured bar.        */
            Bmf_prgBar = new ProgressBar();
            Bmf_prgBar.Style = ProgressBarStyle.Marquee;
            Bmf_prgBar.MarqueeAnimationSpeed = 30;
            Controls.Add(Bmf_prgBar);

            /* Laid out first, then the three events hooked: Resize lays it   */
            /* out again, Shown notes it is on screen (and closes it when     */
            /* the work has already finished), FormClosing refuses to close   */
            /* while the work runs.                                           */
            Bmf_Layout();
            Resize += Bmf_OnResize;
            Shown += Bmf_OnShown;
            FormClosing += Bmf_OnClosing;
        }

        /*------------------------------------------------------------------*/
        /* Bmf_Layout:                                                      */
        /*                                                                  */
        /* Place the three rows one under another and size the window to    */
        /* them: as wide as the wider text line (at least BMF_MINBARWIDTH), */
        /* with BMF_PAD round and between them.                             */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     None.                                                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the controls placed and the window sized to them.     */
        /*------------------------------------------------------------------*/
        private void Bmf_Layout()
        {
            Size szTitle;
            Size szBody;
            Size szClient;
            int iContentWidth;
            int iBarHeight;

            if (Bmf_prgBar == null)
            {   /* The bar is made last: without it the controls are not all   */
                /* there.  A guard -- Resize is hooked only after the first    */
                /* layout, once every control exists -- that no call takes.    */
                return;
            }

            /* The two text lines' own sizes (PreferredSize: what AutoSize    */
            /* will give them), the content width -- the wider line, but at   */
            /* least the bar's minimum -- and the bar a little taller than    */
            /* a line of text.                                                */
            szTitle = Bmf_lblTitle.PreferredSize;
            szBody = Bmf_lblBody.PreferredSize;
            iContentWidth = Math.Max(BMF_MINBARWIDTH, Math.Max(szTitle.Width, szBody.Width));
            iBarHeight = Font.Height + BMF_PAD / 4;

            /* Top to bottom: the heading at the padding, the explanation    */
            /* half a pad below it, the bar a full pad below that, as wide   */
            /* as the content.                                               */
            Bmf_lblTitle.Location = new Point(BMF_PAD, BMF_PAD);
            Bmf_lblBody.Location = new Point(BMF_PAD, Bmf_lblTitle.Top + szTitle.Height + BMF_PAD / 2);
            Bmf_prgBar.SetBounds(BMF_PAD, Bmf_lblBody.Top + szBody.Height + BMF_PAD,
                iContentWidth, iBarHeight);

            /* The window's inside sized to the content and its padding --   */
            /* set only when it differs, since setting it raises Resize,     */
            /* which lays out again: a same-size set would be wasted work,   */
            /* and the test is what ends the round.                          */
            szClient = new Size(iContentWidth + BMF_PAD * 2, Bmf_prgBar.Bottom + BMF_PAD);
            if (ClientSize != szClient)
            {   /* A different size: set, which raises Resize once more. */
                ClientSize = szClient;
            }
        }

        /*------------------------------------------------------------------*/
        /* Bmf_OnResize:                                                    */
        /*                                                                  */
        /* The window's size changed: lay it out again.  The user cannot    */
        /* resize it (a fixed frame), and the first layout's own size set   */
        /* comes before this is hooked, so what raises it later -- Windows  */
        /* or the framework -- is not established.                          */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objSender : the window; unused.                              */
        /*     evArgs    : unused.                                          */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : laid out again.                                       */
        /*------------------------------------------------------------------*/
        private void Bmf_OnResize(object objSender, EventArgs evArgs)
        {
            /* The layout; it sets the size again only if it differs. */
            Bmf_Layout();
        }

        /*------------------------------------------------------------------*/
        /* Bmf_OnDisposed:                                                  */
        /*                                                                  */
        /* The window has been disposed: free the heading's bold font, the  */
        /* one font made here (Bmf_BuildUi).  The dialog font is the        */
        /* caller's and is left alone.                                      */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objSender : the window; unused.                              */
        /*     evArgs    : unused.                                          */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the heading's font is freed.                          */
        /*------------------------------------------------------------------*/
        private void Bmf_OnDisposed(object objSender, EventArgs evArgs)
        {
            /* Disposing a control does not dispose a font set on it (as   */
            /* found), so the window frees the one it made.                */
            Bmf_lblTitle.Font.Dispose();
        }

        /*------------------------------------------------------------------*/
        /* Bmf_OnShown:                                                     */
        /*                                                                  */
        /* The window is on screen: noted, so the work's completion knows   */
        /* there is a window to close -- and when BMF_Run's work finished   */
        /* while the window was still being shown (its completion then      */
        /* found no window shown, and closed nothing), it is closed here.   */
        /* A UI-progress window has no work, and stays until                */
        /* BMF_EndUiProgress.                                               */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objSender : the window; unused.                              */
        /*     evArgs    : unused.                                          */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : noted, and the window closed if the work is done.     */
        /*------------------------------------------------------------------*/
        private void Bmf_OnShown(object objSender, EventArgs evArgs)
        {
            /* Shown: from here the completion closes the window.  Then the   */
            /* done-event, tested without waiting: BMF_Run's work already     */
            /* finished means nothing else will close it.                     */
            Bmf_bShown = true;
            if (Bmf_pfnWork != null && Bmf_evtDone.WaitOne(0))
            {   /* BMF_Run's work is done already: closed now. */
                Close();
            }
        }

        /*------------------------------------------------------------------*/
        /* Bmf_OnClosing:                                                   */
        /*                                                                  */
        /* The window is asked to close: refused while BMF_Run's work is    */
        /* still running, so nothing -- Alt+F4 included -- can end the      */
        /* modal loop early.  BMF_Run would wait for the work anyway; this  */
        /* keeps the window up while it does.                               */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objSender : the window; unused.                              */
        /*     evClosing : its Cancel refuses the close.                    */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the close refused while the work runs, else allowed.  */
        /*------------------------------------------------------------------*/
        private void Bmf_OnClosing(object objSender, FormClosingEventArgs evClosing)
        {
            if (Bmf_bWorkStarted && !Bmf_evtDone.WaitOne(0))
            {   /* Work started and not done: the window stays. */
                evClosing.Cancel = true;
            }
        }

        /*------------------------------------------------------------------*/
        /* Bmf_OnWorkerDoWork:                                              */
        /*                                                                  */
        /* The work itself, on the BackgroundWorker's thread: run it, keep  */
        /* what it threw, and set the done-event last.  It touches no       */
        /* control.                                                         */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objSender : the BackgroundWorker; unused.                    */
        /*     evWork    : unused (the result is kept in the window).       */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the work has run and the event is set.                */
        /*------------------------------------------------------------------*/
        private void Bmf_OnWorkerDoWork(object objSender, DoWorkEventArgs evWork)
        {
            try
            {   /* The caller's work. */
                Bmf_pfnWork();
            }
            catch (Exception exWork)
            {   /* It threw: kept for BMF_Run to hand back, not rethrown --   */
                /* a throw here would only reach RunWorkerCompleted's         */
                /* Error, which nothing reads.                                */
                Bmf_exError = exWork;
            }
            finally
            {   /* The event set LAST, after the exception is stored: a   */
                /* waiter that sees it set may read the result and free   */
                /* what the work used.                                    */
                Bmf_evtDone.Set();
            }
        }

        /*------------------------------------------------------------------*/
        /* Bmf_OnWorkerCompleted:                                           */
        /*                                                                  */
        /* The BackgroundWorker's completion, on the UI thread: close the   */
        /* window if it is on screen.  For quick work BMF_Run may have      */
        /* returned and disposed the window already, which the test         */
        /* covers; the done-event, which BMF_Run may have closed, is not    */
        /* touched.                                                         */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objSender   : the BackgroundWorker; unused.                  */
        /*     evCompleted : unused (Bmf_OnWorkerDoWork kept any error).    */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the window closed, if it was shown and is still here. */
        /*------------------------------------------------------------------*/
        private void Bmf_OnWorkerCompleted(object objSender, RunWorkerCompletedEventArgs evCompleted)
        {
            if (Bmf_bShown && !IsDisposed)
            {   /* Shown and not yet disposed: closed, which ends            */
                /* ShowDialog in BMF_Run (the closing guard allows it: the   */
                /* event is set).                                            */
                Close();
            }
        }
    }
}
