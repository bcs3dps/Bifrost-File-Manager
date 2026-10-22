/*----------------------------------------------------------------------------*/
/* CheckForm.cs                                                               */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The Check Filesystem window: e2fsck's report, line by line as it arrives,  */
/* the verdict when it has finished, and the offer of a repair.               */
/*                                                                            */
/* THE SHAPE.  The window is shown MODAL over the main window, its owner      */
/* (MainCommand.cs, Main_RenderCommandCheckView): the whole program waits on  */
/* it, between runs too, until it closes -- the image itself is CLOSED for    */
/* the check, because the engine checks a filesystem it does not have open,   */
/* and its runs' views and the worker's questions reach it through that       */
/* modal loop.  The window owns nothing but its text.  The command worker     */
/* runs the check and publishes its lines, its progress and its outcome as    */
/* views; the main window's renderer hands them to this window on the UI      */
/* thread (CHK_AppendLines, CHK_ShowProgress, CHK_Finish).  Repair and        */
/* Cancel only call back into the main window,                                */
/* which posts the request; the engine work and its cancel flag are never     */
/* this window's.                                                             */
/*                                                                            */
/* Closing the window while a check runs is refused and turned into a         */
/* Cancel, exactly as the copy progress dialog does: the engine has to be     */
/* allowed to unwind before the window its lines are posted to goes away.     */
/*                                                                            */
/* Its text is in the program's dialog font, which the main window passes in  */
/* (the window decides no size of its own and does not free that font); the   */
/* report is in a fixed-pitch face at the same size, made here from it and    */
/* released when the window is disposed -- by the main window once its modal  */
/* show returns, a modal window being only hidden by its close.               */
/*----------------------------------------------------------------------------*/

/* The framework: the base types (event arguments, the string comparison,   */
/* the exceptions caught), fonts, sizes and colours, the batch of report    */
/* lines built up, and the window and its controls.                         */
using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* CHK_CancelRequest:                                                     */
    /*                                                                        */
    /* The shape of the Cancel button's call back into the main window: a     */
    /* Cancel posted for the one run it was made for (MainCommand.cs,         */
    /* CST_clsCheckStop.CST_RequestCancel, which fixes that run's serial      */
    /* when it is made), so a late click can never stop a later Repair.       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the Cancel posted; the run ends, and the window hears of it */
    /*            through CHK_Finish.                                         */
    /*------------------------------------------------------------------------*/
    internal delegate void CHK_CancelRequest();

    /*------------------------------------------------------------------------*/
    /* CHK_RepairRequest:                                                     */
    /*                                                                        */
    /* The shape of the Repair button's call back into the main window        */
    /* (MainForm.cs, Main_OnCheckRepair): the repair is posted for the check  */
    /* window's own id and range, and the worker asks before it repairs.      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     frmCheck : this window.                                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the request posted; the run, if admitted, comes back as     */
    /*            views (CHK_Begin and the rest).                             */
    /*------------------------------------------------------------------------*/
    internal delegate void CHK_RepairRequest(CHK_clsCheckForm frmCheck);

    /*------------------------------------------------------------------------*/
    /* CHK_clsCheckForm -- the Check Filesystem window.                       */
    /*                                                                        */
    /* Derives from Form because the framework requires it: WinForms makes a  */
    /* window only as a Form subclass.  That is the one kind of inheritance   */
    /* allowed here -- from a framework base class, never from a class of     */
    /* ours.                                                                  */
    /*------------------------------------------------------------------------*/
    internal sealed class CHK_clsCheckForm : Form
    {
        /* The window's geometry and the bar's scale -- documentation table. */
        private const int CHK_START_W     = 760;    // the window's first width, pixels (room for e2fsck's longest usual lines)
        private const int CHK_START_H     = 520;    // the window's first height, pixels
        private const int CHK_MIN_W       = 480;    // the narrowest the window may be dragged, pixels -- raised to the button row's width when the dialog font needs more (the constructor)
        private const int CHK_MIN_H       = 300;    // the shortest the window may be dragged, pixels
        private const int CHK_PAD         = 10;     // padding round the window's content and between its parts, pixels
        private const int CHK_LABEL_LINES = 2;      // text lines the target and verdict labels have room for (a device's description, or a verdict, wraps)
        private const int CHK_BAR_STEPS   = 1000;   // the progress bar's maximum: the whole check
        private const int CHK_PASSES      = 5;      // e2fsck's passes, each an equal share of the bar

        /* e2fsck's names for its passes, as its own "Pass N: Checking ..."   */
        /* headings give them (e2fsck problem.c), for the verdict line.       */
        private static readonly string[] Chk_arrPassNames = { "", "inodes, blocks and sizes", "directory structure", "directory connectivity",
                                                             "reference counts", "group summary information" };   // e2fsck's own names for passes 1 to 5 (index 0 unused)

        /* The controls and the run's state -- documentation table. */
        private Label             Chk_lblTarget;    // what is being checked, and how (report only, or repairing)
        private TextBox           Chk_txtLines;     // e2fsck's report: read-only, monospace, no limit on its length
        private Label             Chk_lblVerdict;   // the one-line verdict once a run has finished; "Running ... pass N of 5" meanwhile
        private ProgressBar       Chk_prgCheck;     // how far the run has got: five equal shares, one per e2fsck pass
        private FlowLayoutPanel   Chk_pnlButtons;   // Copy, Repair, Cancel and Close, right-aligned along the bottom
        private Button            Chk_btnCopy;      // puts the whole report on the clipboard
        private Button            Chk_btnRepair;    // runs the check again, fixing what it finds; enabled only after a run that found errors
        private Button            Chk_btnCancel;    // asks the running check to stop; enabled only while one runs
        private Button            Chk_btnClose;     // closes the window; disabled while a check runs
        private CHK_CancelRequest Chk_pfnCancel;    // the Cancel for the run now shown (CHK_Attach, replaced for every run); null before the first
        private CHK_RepairRequest Chk_pfnRepair;    // the main window's Repair handler
        private string            Chk_sTarget;      // the file or device being checked, as the main window describes it
        private bool              Chk_bRunning;     // true from CHK_Begin to CHK_Finish: a run is shown as under way (what may run is the worker's decision, not this flag's)
        private bool              Chk_bRepairRun;   // true while, and after, the run in progress is a repair; the verdict's wording depends on it
        private bool              Chk_bExtJournal;  // true once this run's report has mentioned an external journal; the finish adds a note saying what that means
        private Font              Chk_fntReport;    // the report's monospace font at the dialog font's size; owned here, disposed with the window

        /*--------------------------------------------------------------------*/
        /* CHK_clsCheckForm (constructor):                                    */
        /*                                                                    */
        /* Build the window, empty and idle: the target along the top, the    */
        /* report filling the middle, the progress, verdict and buttons along */
        /* the bottom.  Nothing runs yet: the main window's renderer attaches */
        /* the run's Cancel and calls CHK_Begin as it shows the window        */
        /* (MainCommand.cs, Main_RenderCommandCheckView).                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     fntDialog : the program's dialog font (the main window's; not  */
        /*                 freed here).                                       */
        /*     sTarget   : the file or device being checked, as described     */
        /*                 by the main window (shown in the title and the     */
        /*                 top label).                                        */
        /*     pfnRepair : the main window's handler for the Repair button.   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the window, built and not yet shown.           */
        /*--------------------------------------------------------------------*/
        internal CHK_clsCheckForm(Font fntDialog, string sTarget, CHK_RepairRequest pfnRepair)
        {
            int iRowWidth;
            int iScreenWidth;

            /* No run yet: no Cancel endpoint, not running, not a repair. The   */
            /* main window's Repair handler and its description of the          */
            /* target are kept for the life of the window -- a repair runs      */
            /* on the same target, and CHK_Begin names it again in the top      */
            /* label for each run.                                              */
            Chk_pfnCancel  = null;
            Chk_pfnRepair  = pfnRepair;
            Chk_sTarget    = sTarget;
            Chk_bRunning   = false;
            Chk_bRepairRun = false;

            /* The caller's dialog font, on the window for the controls below   */
            /* to take once added (as found -- the labels are measured only     */
            /* after they are parented, Chk_FitLabels), and the report's        */
            /* fixed-pitch face at its size (e2fsck lines up its                */
            /* columns with spaces); the report's font is released with the     */
            /* window, the dialog font is not ours.  Consolas is named by       */
            /* name; on a system without it the framework substitutes another   */
            /* face (as found), which may not be fixed-pitch.                   */
            this.Font      = fntDialog;
            Chk_fntReport  = new Font("Consolas", fntDialog.Size);
            this.Disposed += Chk_OnDisposed;

            /* The window itself: titled with the target, at its start size     */
            /* and never smaller than its minimum, centred on the SCREEN and    */
            /* with its own taskbar button -- although it is shown modal over   */
            /* the main window (MainCommand.cs, Main_RenderCommandCheckView).   */
            /* Why it is placed by the screen rather than by its owner, and     */
            /* keeps a taskbar button of its own, is not recorded.              */
            this.Text          = "Check Filesystem - " + sTarget;
            this.Size          = new Size(CHK_START_W, CHK_START_H);
            this.MinimumSize   = new Size(CHK_MIN_W, CHK_MIN_H);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.ShowInTaskbar = true;
            this.Padding       = new Padding(CHK_PAD);

            /* The buttons, right-aligned.  They are added right to left, so     */
            /* the first added sits at the right-hand end: Close, Cancel,        */
            /* Repair, Copy.  Repair and Cancel start greyed: nothing has run.   */
            /* Close first: always usable while no run is under way.             */
            Chk_btnClose          = new Button();
            Chk_btnClose.Text     = "Close";
            Chk_btnClose.AutoSize = true;
            Chk_btnClose.Click   += Chk_OnCloseClicked;

            /* Cancel: greyed until CHK_Begin starts a run. */
            Chk_btnCancel          = new Button();
            Chk_btnCancel.Text     = "Cancel";
            Chk_btnCancel.AutoSize = true;
            Chk_btnCancel.Enabled  = false;
            Chk_btnCancel.Click   += Chk_OnCancelClicked;

            /* Repair: greyed until a finished run has found errors (its   */
            /* "..." because the worker asks before it repairs).           */
            Chk_btnRepair          = new Button();
            Chk_btnRepair.Text     = "Repair...";
            Chk_btnRepair.AutoSize = true;
            Chk_btnRepair.Enabled  = false;
            Chk_btnRepair.Click   += Chk_OnRepairClicked;

            /* Copy: always usable -- an empty report is simply not copied. */
            Chk_btnCopy          = new Button();
            Chk_btnCopy.Text     = "Copy";
            Chk_btnCopy.AutoSize = true;
            Chk_btnCopy.Click   += Chk_OnCopyClicked;

            /* Their row along the bottom, flowing right to left and kept on   */
            /* one line; the window's minimum width is fitted to it below,     */
            /* once the buttons have the dialog's font.                        */
            Chk_pnlButtons               = new FlowLayoutPanel();
            Chk_pnlButtons.Dock          = DockStyle.Bottom;
            Chk_pnlButtons.FlowDirection = FlowDirection.RightToLeft;
            Chk_pnlButtons.AutoSize      = true;
            Chk_pnlButtons.WrapContents  = false;
            Chk_pnlButtons.Controls.Add(Chk_btnClose);
            Chk_pnlButtons.Controls.Add(Chk_btnCancel);
            Chk_pnlButtons.Controls.Add(Chk_btnRepair);
            Chk_pnlButtons.Controls.Add(Chk_btnCopy);

            /* The verdict, above the buttons: empty until a run starts, and   */
            /* not auto-sized -- Chk_FitLabels gives it room for two lines.    */
            Chk_lblVerdict           = new Label();
            Chk_lblVerdict.Dock      = DockStyle.Bottom;
            Chk_lblVerdict.AutoSize  = false;
            Chk_lblVerdict.TextAlign = ContentAlignment.MiddleLeft;
            Chk_lblVerdict.Text      = "";

            /* The target, along the top, as the main window described it;   */
            /* CHK_Begin adds the run's mode to it.  Two lines' room too.    */
            Chk_lblTarget           = new Label();
            Chk_lblTarget.Dock      = DockStyle.Top;
            Chk_lblTarget.AutoSize  = false;
            Chk_lblTarget.TextAlign = ContentAlignment.MiddleLeft;
            Chk_lblTarget.Text      = sTarget;

            /* The report.  A TextBox stops at 32,767 characters unless told    */
            /* otherwise, and a long report passes that; zero means no limit    */
            /* (the framework's rules, as found).  Read-only, unwrapped and     */
            /* scrollable both ways, so e2fsck's lines stay as it wrote them.   */
            /* The typeface is monospace, as e2fsck's own output is read, at    */
            /* the window's own text size.                                      */
            Chk_txtLines            = new TextBox();
            Chk_txtLines.Multiline  = true;
            Chk_txtLines.ReadOnly   = true;
            Chk_txtLines.ScrollBars = ScrollBars.Both;
            Chk_txtLines.WordWrap   = false;
            Chk_txtLines.BackColor  = Color.White;
            Chk_txtLines.Font       = Chk_fntReport;
            Chk_txtLines.MaxLength  = 0;
            Chk_txtLines.Dock       = DockStyle.Fill;

            /* The progress bar, the copy window's kind: the whole check as    */
            /* five equal passes, filled as e2fsck reports each one's count.   */
            /* Docked between the report and the verdict line.                 */
            Chk_prgCheck         = new ProgressBar();
            Chk_prgCheck.Dock    = DockStyle.Bottom;
            Chk_prgCheck.Style   = ProgressBarStyle.Continuous;
            Chk_prgCheck.Minimum = 0;
            Chk_prgCheck.Maximum = CHK_BAR_STEPS;

            /* Docking is laid out from the LAST control added to the first   */
            /* (the framework's rule, as found), each taking the outermost    */
            /* edge still free: the target the top, the button row the        */
            /* bottom, the verdict above the buttons, the bar above the       */
            /* verdict -- and the report, added first, fills what is left.    */
            this.Controls.Add(Chk_txtLines);
            this.Controls.Add(Chk_prgCheck);
            this.Controls.Add(Chk_lblVerdict);
            this.Controls.Add(Chk_pnlButtons);
            this.Controls.Add(Chk_lblTarget);

            /* The two labels' heights, measured now that they are parented   */
            /* and so in the dialog's font.                                   */
            Chk_FitLabels();

            /* The narrowest the window may be dragged: CHK_MIN_W, or as wide   */
            /* as the button row needs at the dialog's font -- the row's own    */
            /* preferred width (its buttons and their margins), the window's    */
            /* padding, and the frame round the client area -- since the row    */
            /* runs off the window's left end when it is wider, cutting off     */
            /* Copy.  The dialog font follows the text size, so a fixed         */
            /* minimum cannot cover it.  The frame is the window's size less    */
            /* its client size, both known now that the window has its size.    */
            iRowWidth = Chk_pnlButtons.PreferredSize.Width + this.Padding.Horizontal +
                        (this.Width - this.ClientSize.Width);

            /* Bounded by the screen: a row wider than the working area of      */
            /* the screen holding the window's present bounds cannot be kept    */
            /* on one line at any width, so it is let wrap onto a second line   */
            /* and the minimum held to the screen's width.  (The window is      */
            /* centred on its screen only when shown, so the screen is the      */
            /* one its unshown bounds fall on.)                                 */
            iScreenWidth = Screen.GetWorkingArea(this).Width;
            if (iRowWidth > iScreenWidth)
            {   /* Wider than the screen: wrapped, the minimum the screen's. */
                Chk_pnlButtons.WrapContents = true;
                iRowWidth = iScreenWidth;
            }

            if (iRowWidth > CHK_MIN_W)
            {   /* The row needs more than the fixed floor: the minimum set    */
                /* to the row's width.  The window grows to it if it was       */
                /* narrower (the framework enlarges a window to its minimum,   */
                /* as found).                                                  */
                this.MinimumSize = new Size(iRowWidth, CHK_MIN_H);
            }

            /* Hooked last, so the building above does not run them: a         */
            /* resize re-fits the labels, and a close is refused while a run   */
            /* is under way.                                                   */
            this.Resize      += Chk_OnResize;
            this.FormClosing += Chk_OnFormClosing;
        }

        /*--------------------------------------------------------------------*/
        /* CHK_Attach:                                                        */
        /*                                                                    */
        /* Give the Cancel button the run it is to stop: the main window's    */
        /* Cancel for that run, which carries the run's serial, so a stop     */
        /* clicked late cannot cancel the Repair that came after.  A repair   */
        /* run gets a new one, attached the same way.                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pfnCancel : posts a Cancel for that run.                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Cancel now reaches that check.                          */
        /*--------------------------------------------------------------------*/
        internal void CHK_Attach(CHK_CancelRequest pfnCancel)
        {
            /* Kept, replacing the last run's -- before CHK_Begin enables the   */
            /* button, so no click can find the old run's Cancel.  The          */
            /* window never works out for itself which run is current.          */
            Chk_pfnCancel = pfnCancel;
        }

        /*--------------------------------------------------------------------*/
        /* CHK_Begin:                                                         */
        /*                                                                    */
        /* A run is starting: the window is put in its running state, and the */
        /* run is marked in the report so that a repair's lines can be told   */
        /* from the report-only run's above them.  The main window's renderer */
        /* calls it for the first run as it shows the window, and for a       */
        /* Repair when its run starts -- after the repair's question has been */
        /* answered Yes (MainCommand.cs, Main_RenderCommandCheckView).        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bRepair : true for a repair run, false for report only.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window shows a check in progress.                   */
        /*--------------------------------------------------------------------*/
        internal void CHK_Begin(bool bRepair)
        {
            string sMode;

            /* A new run: running from now (so a close is refused), its kind    */
            /* kept for the verdict's wording, and its report has not named     */
            /* an external journal yet -- the last run's sighting is cleared,   */
            /* so the note is given only for a run that saw it.  The mode is    */
            /* named as e2fsck's own options, so the words say exactly what     */
            /* runs: -f to check even a clean-marked filesystem, -n to          */
            /* answer no and change nothing.                                    */
            Chk_bRunning    = true;
            Chk_bRepairRun  = bRepair;
            Chk_bExtJournal = false;
            sMode           = "report only (e2fsck -fn)";

            if (bRepair)
            {   /* Fixing what it finds: -y answers yes to every question,   */
                /* in capitals so a repair is never mistaken for a look.     */
                sMode = "REPAIRING (e2fsck -fy)";
            }

            /* The top label names the target and the mode; the verdict   */
            /* line says it is running (CHK_ShowProgress then names the   */
            /* pass); the bar starts again from empty.                    */
            Chk_lblTarget.Text  = Chk_sTarget + "   --   " + sMode;
            Chk_lblVerdict.Text = "Running ...";
            Chk_prgCheck.Value  = 0;

            /* The run's heading in the report, below any earlier run's. */
            CHK_AppendLines(new string[] { "---- " + sMode + " ----" });

            /* The buttons for a running check: Cancel usable (and its     */
            /* caption back from a previous "Cancelling..."), Repair and   */
            /* Close not -- the window must stay while the engine runs.    */
            Chk_btnRepair.Enabled = false;
            Chk_btnClose.Enabled  = false;
            Chk_btnCancel.Enabled = true;
            Chk_btnCancel.Text    = "Cancel";
        }

        /*--------------------------------------------------------------------*/
        /* CHK_ShowProgress:                                                  */
        /*                                                                    */
        /* Show where e2fsck says the run has got, on the bar and in the      */
        /* verdict line.  Called on the UI thread with a progress view's      */
        /* figures, only for the run the window shows: the renderer matches   */
        /* the view's request serial to that run before calling.              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uiPass   : 1 to 5; 0 at the end of a pass.                     */
        /*     ullCur   : how far the pass has got.                           */
        /*     ullTotal : out of how much (0 when unknown).                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the bar and the verdict line show the progress.         */
        /*--------------------------------------------------------------------*/
        internal void CHK_ShowProgress(uint uiPass, ulong ullCur, ulong ullTotal)
        {
            ulong ullValue;

            if (!Chk_bRunning || uiPass < 1 || uiPass > CHK_PASSES || ullTotal == 0)
            {   /* Not a pass under way with a count to show: a snapshot     */
                /* that arrives after CHK_Finish (it was posted before the   */
                /* run ended), the engine's end-of-pass marker (pass 0,      */
                /* bfe_fsck.c), a pass number outside the five, or a count   */
                /* with no total -- which would divide by zero below.  The   */
                /* bar and the line are left as they are.                    */
                return;
            }

            if (ullCur > ullTotal)
            {   /* A count past its total: the pass is done -- held at its   */
                /* total, so this pass cannot spill into the next one's      */
                /* share of the bar.                                         */
                ullCur = ullTotal;
            }

            /* Pass N's share is the bar's N-th fifth: the passes before     */
            /* this one count whole (1000 steps each), this one's fraction   */
            /* is scaled to steps and added, and the sum is divided by the   */
            /* five passes.                                                  */
            ullValue = (((ulong)(uiPass - 1) * CHK_BAR_STEPS) + ((ullCur * CHK_BAR_STEPS) / ullTotal)) / CHK_PASSES;

            if (ullValue > CHK_BAR_STEPS)
            {   /* A guard only: with the count held to its total above, the   */
                /* sum cannot pass the end -- but the bar throws on a value    */
                /* past its Maximum (as found), so it is held there anyway.    */
                ullValue = CHK_BAR_STEPS;
            }

            /* The bar moved, and the line names the pass in e2fsck's words. */
            Chk_prgCheck.Value  = (int)ullValue;
            Chk_lblVerdict.Text = "Running ... pass " + uiPass + " of " + CHK_PASSES + ": " + Chk_arrPassNames[uiPass];
        }

        /*--------------------------------------------------------------------*/
        /* CHK_AppendLines:                                                   */
        /*                                                                    */
        /* Add lines to the end of the report and scroll to them.  Called on  */
        /* the UI thread: by the renderer with one line per text event of     */
        /* the run shown, and by this window itself with its headings and     */
        /* verdicts -- any number of lines, kept in their order.              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrLines : the lines, in order, without line endings.          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the report ends with the lines.                         */
        /*--------------------------------------------------------------------*/
        internal void CHK_AppendLines(string[] arrLines)
        {
            StringBuilder sbText;
            int           i;

            /* The batch is built up first and added to the report in one   */
            /* call, not one call per line (what a call per line would      */
            /* cost the TextBox is not measured here).                      */
            sbText = new StringBuilder();

            for (i = 0; i < arrLines.Length; i++)
            {   /* One line per iteration, with the text box's own line ending. */
                sbText.Append(arrLines[i]);
                sbText.Append("\r\n");

                /* e2fsck names an external journal only when the filesystem   */
                /* has one -- every message of its that says the words         */
                /* (problem.c: "Can't find external journal", "External        */
                /* journal has bad superblock", ...) is about one -- so the    */
                /* words are the signal for the note CHK_Finish adds.  It is   */
                /* read from the report's text alone; nothing more is asked    */
                /* of the engine.                                              */
                if (arrLines[i].IndexOf("external journal", StringComparison.OrdinalIgnoreCase) >= 0)
                {   /* Remember it for the end of the run. */
                    Chk_bExtJournal = true;
                }
            }

            /* AppendText (not Text +=) adds at the end and keeps the end   */
            /* in view, as found -- so the newest line shows as it comes.   */
            Chk_txtLines.AppendText(sbText.ToString());
        }

        /*--------------------------------------------------------------------*/
        /* CHK_Finish:                                                        */
        /*                                                                    */
        /* The run is over: show its verdict, and put the window back in its  */
        /* idle state, offering Repair only when the run's findings give it   */
        /* something to act on.  The renderer calls it when the run it shows  */
        /* sends its CheckFinished view (through Main_ShowCheckResult), or    */
        /* with a cancelled result when the worker closes the window while a  */
        /* run is still marked as under way.                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objResult : the check's outcome.                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window shows the verdict.                           */
        /*--------------------------------------------------------------------*/
        internal void CHK_Finish(ECK_clsCheckResult objResult)
        {
            string sVerdict;
            bool   bOfferRepair;

            /* Not running from here, FIRST: the window may be closed again   */
            /* (Chk_OnFormClosing), and a late progress snapshot is ignored   */
            /* (CHK_ShowProgress).  Then the verdict in words.                */
            Chk_bRunning = false;
            sVerdict     = Chk_VerdictText(objResult, Chk_bRepairRun);

            /* The verdict at the end of the report, set off by blank   */
            /* lines so it stands out from e2fsck's own, and in the     */
            /* verdict line, where it replaces "Running ... pass N".    */
            CHK_AppendLines(new string[] { "", sVerdict, "" });
            Chk_lblVerdict.Text = sVerdict;

            if (Chk_bExtJournal)
            {   /* An external journal: the report's own words do not say       */
                /* what to do about it, so the note does -- once, at the end.   */
                /* "Opens it read-only" is the engine's rule: it refuses a      */
                /* writable open of such a filesystem (bifrost_engine.c,        */
                /* BFE_Open).  The verdict line points the reader at it.        */
                CHK_AppendLines(new string[] {
                    "NOTE: this filesystem keeps its journal on a SEPARATE DEVICE (an external journal).",
                    "That device cannot be found from Windows, so e2fsck cannot check or replay the journal",
                    "here, and BifrostFM opens this filesystem read-only. To check or repair it, use e2fsck",
                    "on Linux with the journal device attached (e2fsck -f -j <journal device> <filesystem>).",
                    "" });
                Chk_lblVerdict.Text = sVerdict + "  (External journal: see the note at the end of the report.)";
            }

            if (objResult.ECK_iStatus == ENG_clsNative.ENG_OK && (objResult.ECK_uiExitCode & ENG_clsNative.ENG_CHECK_CANCELLED) == 0)
            {   /* The engine ran the check and it was not cancelled: the bar   */
                /* is filled.  That includes a run e2fsck gave up on (exit      */
                /* bit 8), whose verdict says so; a cancelled run, or one the   */
                /* engine could not run, stays where it stopped.                */
                Chk_prgCheck.Value = CHK_BAR_STEPS;
            }

            /* Repair is offered only when the run went the whole way        */
            /* (the engine's status OK, and e2fsck neither gave up nor was   */
            /* cancelled) and left errors behind (FSCK_UNCORRECTED): a       */
            /* report-only run that found some, or a repair that could not   */
            /* fix everything, which a second pass sometimes can.  A run     */
            /* that gave up or was stopped has no finding to act on.         */
            bOfferRepair = (objResult.ECK_iStatus == ENG_clsNative.ENG_OK &&
                            (objResult.ECK_uiExitCode & ENG_clsNative.ENG_CHECK_UNCORRECTED) != 0 &&
                            (objResult.ECK_uiExitCode & (ENG_clsNative.ENG_CHECK_ERROR | ENG_clsNative.ENG_CHECK_CANCELLED)) == 0);

            /* The buttons for a finished check: Cancel no longer, Close   */
            /* again, Repair as just decided.                              */
            Chk_btnCancel.Enabled = false;
            Chk_btnClose.Enabled  = true;
            Chk_btnRepair.Enabled = bOfferRepair;
        }

        /*--------------------------------------------------------------------*/
        /* Chk_VerdictText:                                                   */
        /*                                                                    */
        /* The one-line verdict for a finished check, from the engine's       */
        /* status and e2fsck's exit bits: whether it ran at all, whether it   */
        /* was stopped, and -- when it ran to the end -- whether errors were  */
        /* found, corrected, or left, with the inode and block counts that    */
        /* e2fsck's closing line gives.  Each verdict says what to do next.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objResult     : the check's outcome.                           */
        /*     bRepairRun : true when the run was a repair.                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the verdict.                                          */
        /*--------------------------------------------------------------------*/
        private static string Chk_VerdictText(ECK_clsCheckResult objResult, bool bRepairRun)
        {
            string sCounts;

            /* The order of the tests matters: a cancel first (it may come   */
            /* as the engine's status or as e2fsck's exit bit), then a run   */
            /* the engine could not make, then one e2fsck gave up on; only   */
            /* a run that went the whole way reaches the findings below.     */
            if (objResult.ECK_iStatus == ENG_clsNative.ENG_ERR_CANCELLED || (objResult.ECK_uiExitCode & ENG_clsNative.ENG_CHECK_CANCELLED) != 0)
            {   /* Stopped by Cancel.  A repair stopped part-way may already have written. */
                if (objResult.ECK_bModified)
                {   /* It had changed the filesystem before it stopped: said    */
                    /* in capitals, since a half-done repair may have left      */
                    /* it inconsistent, and another check is the way to know.   */
                    return("Cancelled. The filesystem WAS CHANGED before the stop; run the check again.");
                }

                /* Stopped with nothing written -- always so for a report-   */
                /* only run, which opens the filesystem read-only.           */
                return("Cancelled. Nothing was changed.");
            }

            if (objResult.ECK_iStatus != ENG_clsNative.ENG_OK)
            {   /* The check could not be run (or failed in the engine):      */
                /* the status in a sentence and the engine's own detail       */
                /* after it -- never the findings, which would be empty and   */
                /* read as a clean check.                                     */
                return("The check could not be run: " + ENG_clsNative.ENG_StatusText(objResult.ECK_iStatus) + "  " + objResult.ECK_sDetail);
            }

            if ((objResult.ECK_uiExitCode & ENG_clsNative.ENG_CHECK_ERROR) != 0)
            {   /* e2fsck gave up on the filesystem (FSCK_ERROR): no counts   */
                /* to give, and its last lines in the report say why.         */
                return("e2fsck could not check the filesystem; its last lines above say why.");
            }

            /* The counts, as e2fsck's closing line prints them (inodes and   */
            /* blocks in use of the totals, from the engine's result), for    */
            /* each verdict below to end with.                                */
            sCounts = "  (" + objResult.ECK_uiInodesUsed.ToString("N0") + " of " + objResult.ECK_uiInodesTotal.ToString("N0") + " inodes and " +
                      objResult.ECK_ullBlocksUsed.ToString("N0") + " of " + objResult.ECK_ullBlocksTotal.ToString("N0") + " blocks in use)";

            if ((objResult.ECK_uiExitCode & ENG_clsNative.ENG_CHECK_UNCORRECTED) != 0)
            {   /* Errors are still there. */
                if (bRepairRun)
                {   /* Even after a repair: another pass sometimes clears what the first exposed. */
                    return("Errors remain after the repair. Run Repair again; if they persist, the lines above say what e2fsck could not fix." + sCounts);
                }

                /* Report only: nothing was touched. */
                return("ERRORS WERE FOUND. The filesystem was NOT changed. Repair... fixes them (e2fsck -fy, answering yes to every question)." + sCounts);
            }

            if ((objResult.ECK_uiExitCode & ENG_clsNative.ENG_CHECK_CORRECTED) != 0)
            {   /* A repair fixed everything it found. */
                return("Errors were found and corrected; the filesystem was changed and is clean now." + sCounts);
            }

            /* Clean: none of the bits tested above is set.  The engine      */
            /* never sets e2fsck's reboot, usage or library bits (2, 16,     */
            /* 128 -- bifrost_engine.h's BFE_CHECK_* box), so nothing else   */
            /* can be hiding behind this verdict.                            */
            return("No errors found." + sCounts);
        }

        /*--------------------------------------------------------------------*/
        /* Chk_FitLabels:                                                     */
        /*                                                                    */
        /* Give the target and verdict labels the height of CHK_LABEL_LINES   */
        /* lines of their font, plus the padding between them and the         */
        /* report, so each can wrap once at any width and text size.          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : both labels' heights are set.                           */
        /*--------------------------------------------------------------------*/
        private void Chk_FitLabels()
        {
            /* Two lines of each label's own font, plus one pad that parts   */
            /* it from the report.  Only the heights: docking sets widths.   */
            Chk_lblTarget.Height  = (Chk_lblTarget.PreferredHeight * CHK_LABEL_LINES) + CHK_PAD;
            Chk_lblVerdict.Height = (Chk_lblVerdict.PreferredHeight * CHK_LABEL_LINES) + CHK_PAD;
        }

        /*--------------------------------------------------------------------*/
        /* Chk_OnResize:                                                      */
        /*                                                                    */
        /* The window changed size: the docked controls follow it by          */
        /* themselves; the two labels' heights are recomputed, which is what  */
        /* keeps the layout right if the font ever changes.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the window.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the layout fits the new size.                           */
        /*--------------------------------------------------------------------*/
        private void Chk_OnResize(object objSender, EventArgs evArgs)
        {
            /* The labels' heights again; everything else is docked. */
            Chk_FitLabels();
        }

        /*--------------------------------------------------------------------*/
        /* Chk_OnCopyClicked:                                                 */
        /*                                                                    */
        /* Copy: put the whole report on the clipboard, so it can be pasted   */
        /* into a message or a file.  A clipboard that stays busy is given    */
        /* up on quietly; the user can click again.                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the clipboard holds the report, or is left alone when   */
        /*            the report is empty or the clipboard stays locked.      */
        /*--------------------------------------------------------------------*/
        private void Chk_OnCopyClicked(object objSender, EventArgs evArgs)
        {
            string sText;

            /* The whole report, every run's lines and verdicts. */
            sText = Chk_txtLines.Text;

            if (sText.Length == 0)
            {   /* Nothing to copy; Clipboard.SetText refuses an empty   */
                /* string (it throws -- as found).                       */
                return;
            }

            try
            {   /* The first try. */
                Clipboard.SetText(sText);
            }
            catch (Exception)
            {   /* The clipboard is shared, and another program may hold it   */
                /* for a moment (the framework then throws -- as found):      */
                /* one more try.  Why one, and not a wait, is not recorded.   */
                try
                {   /* The second and last try. */
                    Clipboard.SetText(sText);
                }
                catch (Exception)
                {   /* Still locked: give up quietly; the user can click again. */
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* Chk_OnRepairClicked:                                               */
        /*                                                                    */
        /* Repair: handed to the main window, which posts the repair; the     */
        /* worker asks the user, and runs it only on a Yes.  Nothing is       */
        /* decided here, and the buttons change only when the run begins.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the main window has the request.                        */
        /*--------------------------------------------------------------------*/
        private void Chk_OnRepairClicked(object objSender, EventArgs evArgs)
        {
            /* The main window's handler, given this window: it acts only    */
            /* if this is still the check window it knows (Main_OnCheck-     */
            /* Repair), and posts the repair with that window's own id and   */
            /* range -- nothing is read from this window's fields.           */
            Chk_pfnRepair(this);
        }

        /*--------------------------------------------------------------------*/
        /* Chk_OnCancelClicked:                                               */
        /*                                                                    */
        /* The Cancel button: ask the running check to stop                   */
        /* (Chk_RequestCancel).                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the Cancel posted, and the button shows it.             */
        /*--------------------------------------------------------------------*/
        private void Chk_OnCancelClicked(object objSender, EventArgs evArgs)
        {
            /* The user's stop. */
            Chk_RequestCancel();
        }

        /*--------------------------------------------------------------------*/
        /* Chk_RequestCancel:                                                 */
        /*                                                                    */
        /* Ask the running check to stop.  The window stays up until it has   */
        /* -- CHK_Finish ends the run -- so here the request is only made and */
        /* shown.  The Cancel button's handler calls it, and so does a close  */
        /* the user asks for while a check runs (Chk_OnFormClosing).          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the Cancel posted, and the button shows it.             */
        /*--------------------------------------------------------------------*/
        private void Chk_RequestCancel()
        {
            if (Chk_pfnCancel != null)
            {   /* A run's Cancel is attached: posted, carrying that run's    */
                /* serial.  The worker raises the cancel flag itself, and     */
                /* refuses a Cancel for a run that is no longer running, so   */
                /* it can never stop a later one.                             */
                Chk_pfnCancel();
            }

            /* The button greyed and relabelled: a second press could do   */
            /* nothing more, and the caption says the stop is under way,   */
            /* since e2fsck may take a moment to reach its next event.     */
            Chk_btnCancel.Enabled = false;
            Chk_btnCancel.Text    = "Cancelling...";
        }

        /*--------------------------------------------------------------------*/
        /* Chk_OnCloseClicked:                                                */
        /*                                                                    */
        /* Close: close the window (Chk_OnFormClosing has the say while a     */
        /* check runs).  The main window's FormClosed handler posts           */
        /* CheckClosed, and the worker then reopens a filesystem the check    */
        /* closed (MainCommand.cs, Main_EndCommandCheckView).                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window is closing.                                  */
        /*--------------------------------------------------------------------*/
        private void Chk_OnCloseClicked(object objSender, EventArgs evArgs)
        {
            /* Closed.  The button is greyed while a run is under way, so    */
            /* this close normally goes through; the window is modeless,     */
            /* so the close also disposes it (as found), freeing the font.   */
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Chk_OnFormClosing:                                                 */
        /*                                                                    */
        /* The window is asked to close (the Close button, the X, Alt+F4, or  */
        /* the main window shutting down).  While a check is running that is  */
        /* refused: the engine is inside the check and posting lines here,    */
        /* so the window must outlive it.  Once the check has stopped the     */
        /* next close goes through.                                           */
        /*                                                                    */
        /* A refused close is taken as Cancel -- except one raised because    */
        /* its owner is closing (CloseReason.FormOwnerClosing), which leaves  */
        /* the check running.  The window is shown modal over the main        */
        /* window, its owner (Main_RenderCommandCheckView); an Exit reaches   */
        /* it as the worker's Close once the check has stopped, so the        */
        /* owner's own close must not answer the worker's Exit question       */
        /* (stop the check?) with a Cancel before that question is asked.     */
        /* Whether the owner's close can arrive at all while this modal       */
        /* window is up is not established (the main window is disabled       */
        /* under it).                                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the window.                                        */
        /*     evClosing : the closing event; cancelled while a check runs.   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the close is refused (from the user, with the check     */
        /*            asked to stop), or allowed.                             */
        /*--------------------------------------------------------------------*/
        private void Chk_OnFormClosing(object objSender, FormClosingEventArgs evClosing)
        {
            if (Chk_bRunning)
            {   /* Still checking: the window stays open meanwhile. */
                evClosing.Cancel = true;

                if (evClosing.CloseReason == CloseReason.FormOwnerClosing)
                {   /* The owner (the main window) closing: left running, as   */
                    /* the header says, for the worker's Exit to stop it.      */
                    return;
                }

                /* The user closed the window: ask the check to stop. */
                Chk_RequestCancel();
            }
        }

        /*--------------------------------------------------------------------*/
        /* Chk_OnDisposed:                                                    */
        /*                                                                    */
        /* The window is gone: release the report's font, the one font made   */
        /* for it (the dialog font is the main window's and stays).           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the window.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the font is released.                                   */
        /*--------------------------------------------------------------------*/
        private void Chk_OnDisposed(object objSender, EventArgs evArgs)
        {
            if (Chk_fntReport != null)
            {   /* The report's face; released once only. */
                Chk_fntReport.Dispose();
                Chk_fntReport = null;
            }
        }
    }
}
