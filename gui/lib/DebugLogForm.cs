/*----------------------------------------------------------------------------*/
/* DebugLogForm.cs -- a window that shows the tail of one log file            */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* A modeless window holding the whole text of a log file.  Twice a second    */
/* it looks at the file's size and, when that has changed, reads the file     */
/* again and scrolls to the end -- so it follows a log another part of the    */
/* program (or another program) is still writing.                             */
/*                                                                            */
/* It knows nothing about any particular program: the caller hands it the     */
/* log's path.  With no path, or a path that does not exist, it shows         */
/* "(no log file found)".                                                     */
/*                                                                            */
/*     DBGLOGF_clsDebugLogForm frmLog = new DBGLOGF_clsDebugLogForm(sPath);   */
/*     frmLog.Show();       (modeless, and with no owner)                     */
/*                                                                            */
/* Select All and Copy make it one click to take the log to a bug report.     */
/* Every button, the poll timer and a close from the title bar only post to   */
/* the window's own queue (WindowQueue.cs); the queue does the work, one      */
/* item at a time.                                                            */
/*----------------------------------------------------------------------------*/

/* The framework only: drawing types, files and the WinForms controls. */
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* DBGLOGF_clsDebugLogForm -- the log viewer window (the file header      */
    /* describes it).                                                         */
    /*                                                                        */
    /* Derives from Form because the framework requires it: WinForms makes a  */
    /* window only as a Form subclass.  That is the one kind of inheritance   */
    /* allowed here -- from a framework base class, never from a class of     */
    /* ours.                                                                  */
    /*------------------------------------------------------------------------*/
    public class DBGLOGF_clsDebugLogForm : Form
    {
        public static string DBGLOGF_sDefaultFormCaption = "Debug Log";    // the caption every instance starts with; DBGLOGF_FormCaption overrides it per instance; nothing in the program changes it

        private const int DBGLOGF_PAD          = 10;     // pixels between the buttons and round them
        private const int DBGLOGF_ROW_H        = 28;     // button height in pixels
        private const int DBGLOGF_BTN_W        = 90;     // button width in pixels (a chosen size; the three captions are short)
        private const int DBGLOGF_POLL_MS      = 500;    // how often the file's size is checked, in milliseconds
        private const int DBGLOGF_START_W      = 800;    // the window's first width
        private const int DBGLOGF_START_H      = 500;    // the window's first height
        private const int DBGLOGF_MIN_W        = 400;    // the narrowest the window may be dragged
        private const int DBGLOGF_MIN_H        = 250;    // the shortest the window may be dragged
        private const long DBGLOGF_SIZE_UNREAD = -1;     // Dbglogf_lLastFileSize before any read: no real size equals it, so the first poll always loads

        private TextBox            Dbglogf_txtLog;          // the log text: read-only, monospace, fills the window above the buttons
        private Button             Dbglogf_btnSelectAll;    // selects the whole log
        private Button             Dbglogf_btnCopy;         // copies the selection, or the whole log when nothing is selected
        private Button             Dbglogf_btnClose;        // closes the window
        private Panel              Dbglogf_pnlButtons;      // the strip along the bottom that holds the three buttons
        private Timer              Dbglogf_tmrPoll;         // fires every DBGLOGF_POLL_MS on the UI thread; stopped and disposed by the Close item (Dbglogf_CloseWindow)
        private long               Dbglogf_lLastFileSize;   // the file's size at the last read; a different size means there is more to show
        private string             Dbglogf_sLogPath;        // the file being shown; null or empty for none
        private string             Dbglogf_sFormCaption;    // this instance's caption override; null to use DBGLOGF_sDefaultFormCaption
        private WQU_clsWindowQueue Dbglogf_objQueue;        // the window's own command queue (WindowQueue.cs): the handlers post to it, and it runs each item through Dbglogf_RunItem
        private bool               Dbglogf_bCloseReady;     // true once the Close item has stopped the timer and asked for the close: the closing handler then lets it through

        /* The window's own commands, as its queue (Dbglogf_objQueue)   */
        /* carries them -- documentation table.                         */
        private enum Dbglogf_enmItem
        {
            SelectAll = 1,      // Select All: the whole log selected (Dbglogf_SelectAll)
            Copy      = 2,      // Copy: the selection, or the whole log, onto the clipboard (Dbglogf_CopyLog)
            Close     = 3,      // Close, or the title bar's close: the timer stopped, then the window closed (Dbglogf_CloseWindow)
            Poll      = 4       // the poll timer's tick: the log re-read when its size moved (Dbglogf_PollFile)
        }

        /*--------------------------------------------------------------------*/
        /* DBGLOGF_LogPath:                                                   */
        /*                                                                    */
        /* The log file this window shows.  KNOWN UNUSED AT PRESENT: nothing  */
        /* in the program calls this -- its one user (MainForm.cs) gives the  */
        /* path to the constructor -- and it is kept on purpose, as part of   */
        /* the window's interface.                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     value : SET only -- the new file; it is read at once, not at   */
        /*             the next poll.                                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the current path, or null / empty for none.           */
        /*--------------------------------------------------------------------*/
        public string DBGLOGF_LogPath
        {
            get { return(Dbglogf_sLogPath); }
            set
            {   /* The new path, its size forgotten so nothing of the old   */
                /* file's is compared against, and the new file read now.   */
                Dbglogf_sLogPath      = value;
                Dbglogf_lLastFileSize = DBGLOGF_SIZE_UNREAD;
                Dbglogf_LoadLog();
            }
        }

        /*--------------------------------------------------------------------*/
        /* DBGLOGF_FormCaption:                                               */
        /*                                                                    */
        /* This window's caption, when it should differ from the default.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     value : SET only -- the caption; null or empty goes back to    */
        /*             DBGLOGF_sDefaultFormCaption.  The title bar changes at */
        /*             once.                                                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the override, or null when the default is in use.     */
        /*--------------------------------------------------------------------*/
        public string DBGLOGF_FormCaption
        {
            get { return(Dbglogf_sFormCaption); }
            set
            {   /* Kept, then shown in the title bar at once. */
                Dbglogf_sFormCaption = value;

                if (string.IsNullOrEmpty(value))
                {   /* No override: the shared default. */
                    Text = DBGLOGF_sDefaultFormCaption;
                }
                else
                {   /* This instance's own caption. */
                    Text = value;
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* DBGLOGF_clsDebugLogForm (constructor):                             */
        /*                                                                    */
        /* Build the window -- a button strip docked along the bottom and a   */
        /* text box filling the rest -- read the log once, and start the      */
        /* poll timer.                                                        */
        /*                                                                    */
        /* The window is meant to be shown modeless and without an owner, so  */
        /* it has its own taskbar button and opens centred on the screen.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sLogPath : the log file to show, or null / empty for none yet  */
        /*                (DBGLOGF_LogPath sets one later).                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the window, built and not yet shown.           */
        /*--------------------------------------------------------------------*/
        public DBGLOGF_clsDebugLogForm(string sLogPath)
        {
            /* The file to show, no caption override yet, and no close   */
            /* asked for.                                                */
            Dbglogf_sLogPath     = sLogPath;
            Dbglogf_sFormCaption = null;
            Dbglogf_bCloseReady  = false;

            /* The window itself: the default caption, its first size,     */
            /* centred on the screen, and a taskbar button of its own --   */
            /* it has no owner to sit under.                               */
            Text          = DBGLOGF_sDefaultFormCaption;
            Size          = new Size(DBGLOGF_START_W, DBGLOGF_START_H);
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;

            /* The window's own queue, made before any handler can post to    */
            /* it.  A post needs the window's handle, which the queue posts   */
            /* its drain to; the program shows the window as soon as it is    */
            /* made (MainForm.cs, Main_ShowDebugLog), so no click or tick     */
            /* comes before the handle exists.                                */
            Dbglogf_objQueue = new WQU_clsWindowQueue(this, Dbglogf_RunItem);

            /* The button strip, docked to the bottom edge. */
            Dbglogf_pnlButtons        = new Panel();
            Dbglogf_pnlButtons.Height = DBGLOGF_ROW_H + DBGLOGF_PAD * 2;
            Dbglogf_pnlButtons.Dock   = DockStyle.Bottom;

            /* Three buttons in a row at fixed pixel sizes, each placed   */
            /* from the one before it.  First Select All, at the left.    */
            Dbglogf_btnSelectAll          = new Button();
            Dbglogf_btnSelectAll.Text     = "Select All";
            Dbglogf_btnSelectAll.Size     = new Size(DBGLOGF_BTN_W, DBGLOGF_ROW_H);
            Dbglogf_btnSelectAll.Location = new Point(DBGLOGF_PAD, DBGLOGF_PAD);
            Dbglogf_btnSelectAll.Click   += Dbglogf_OnSelectAllClicked;
            Dbglogf_pnlButtons.Controls.Add(Dbglogf_btnSelectAll);

            /* Copy, one gap to the right of it. */
            Dbglogf_btnCopy          = new Button();
            Dbglogf_btnCopy.Text     = "Copy";
            Dbglogf_btnCopy.Size     = new Size(DBGLOGF_BTN_W, DBGLOGF_ROW_H);
            Dbglogf_btnCopy.Location = new Point(Dbglogf_btnSelectAll.Right + DBGLOGF_PAD, DBGLOGF_PAD);
            Dbglogf_btnCopy.Click   += Dbglogf_OnCopyClicked;
            Dbglogf_pnlButtons.Controls.Add(Dbglogf_btnCopy);

            /* Close, last; its click posts the same Close item the title   */
            /* bar's close does, so the timer is stopped the one way.       */
            Dbglogf_btnClose          = new Button();
            Dbglogf_btnClose.Text     = "Close";
            Dbglogf_btnClose.Size     = new Size(DBGLOGF_BTN_W, DBGLOGF_ROW_H);
            Dbglogf_btnClose.Location = new Point(Dbglogf_btnCopy.Right + DBGLOGF_PAD, DBGLOGF_PAD);
            Dbglogf_btnClose.Click   += Dbglogf_OnCloseClicked;
            Dbglogf_pnlButtons.Controls.Add(Dbglogf_btnClose);

            /* The text box, filling whatever the strip leaves: read-only,   */
            /* both scroll bars and no wrapping (a log's lines are kept as   */
            /* written), white rather than read-only grey so it reads as a   */
            /* page, in a fixed-pitch face so columns in the log line up.    */
            Dbglogf_txtLog            = new TextBox();
            Dbglogf_txtLog.Multiline  = true;
            Dbglogf_txtLog.ReadOnly   = true;
            Dbglogf_txtLog.ScrollBars = ScrollBars.Both;
            Dbglogf_txtLog.WordWrap   = false;
            Dbglogf_txtLog.BackColor  = Color.White;
            Dbglogf_txtLog.Font       = new Font("Consolas", 9.0f);
            Dbglogf_txtLog.Dock       = DockStyle.Fill;

            /* A TextBox stops at 32,767 characters unless told otherwise,   */
            /* and a real log passes that quickly; text beyond the limit     */
            /* is silently dropped and the viewer looks frozen.  Zero        */
            /* means no limit (the framework's behaviour, as found).         */
            Dbglogf_txtLog.MaxLength = 0;

            /* The fill control is added BEFORE the docked strip: docking   */
            /* is laid out from the last control added to the first.        */
            Controls.Add(Dbglogf_txtLog);
            Controls.Add(Dbglogf_pnlButtons);

            /* Small enough to tuck aside, large enough that the three   */
            /* buttons and some log always show.                         */
            MinimumSize = new Size(DBGLOGF_MIN_W, DBGLOGF_MIN_H);

            /* Show what is there now. */
            Dbglogf_lLastFileSize = DBGLOGF_SIZE_UNREAD;
            Dbglogf_LoadLog();

            /* Then follow the file: a size check is cheap, and the file     */
            /* is re-read only when the size has moved.  Each tick only      */
            /* posts a Poll item.  The first tick comes after the window     */
            /* is shown, as the constructor's caller shows it at once.  A    */
            /* tick before then would leave its Poll waiting, and the next   */
            /* tick -- dropped as a duplicate of it -- would still ask the   */
            /* queue for a drain, which runs it once the handle exists       */
            /* (WindowQueue.cs).                                             */
            Dbglogf_tmrPoll          = new Timer();
            Dbglogf_tmrPoll.Interval = DBGLOGF_POLL_MS;
            Dbglogf_tmrPoll.Tick    += Dbglogf_OnPollTick;
            Dbglogf_tmrPoll.Start();

            /* A close from the title bar is turned into the Close item   */
            /* (Dbglogf_OnFormClosing).                                   */
            FormClosing += Dbglogf_OnFormClosing;
        }

        /*--------------------------------------------------------------------*/
        /* Dbglogf_OnSelectAllClicked:                                        */
        /*                                                                    */
        /* Select All: posted to the window's own queue, which runs it        */
        /* (Dbglogf_SelectAll).  Nothing else is done here.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : the click (unused).                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item posted (a second click while one waits is      */
        /*            dropped by the queue).                                  */
        /*--------------------------------------------------------------------*/
        private void Dbglogf_OnSelectAllClicked(object objSender, EventArgs evArgs)
        {
            /* Posted, with nothing to carry. */
            Dbglogf_objQueue.WQU_Post((int)Dbglogf_enmItem.SelectAll, null);
        }

        /*--------------------------------------------------------------------*/
        /* Dbglogf_OnCopyClicked:                                             */
        /*                                                                    */
        /* Copy: posted to the window's own queue, which runs it              */
        /* (Dbglogf_CopyLog).  Nothing else is done here.                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : the click (unused).                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item posted (a second click while one waits is      */
        /*            dropped by the queue).                                  */
        /*--------------------------------------------------------------------*/
        private void Dbglogf_OnCopyClicked(object objSender, EventArgs evArgs)
        {
            /* Posted, with nothing to carry. */
            Dbglogf_objQueue.WQU_Post((int)Dbglogf_enmItem.Copy, null);
        }

        /*--------------------------------------------------------------------*/
        /* Dbglogf_OnCloseClicked:                                            */
        /*                                                                    */
        /* The Close button: the Close item posted to the window's own queue, */
        /* which stops the timer and closes the window (Dbglogf_CloseWindow)  */
        /* -- the same item the title bar's close becomes.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : the click (unused).                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item posted.                                        */
        /*--------------------------------------------------------------------*/
        private void Dbglogf_OnCloseClicked(object objSender, EventArgs evArgs)
        {
            /* Posted, with nothing to carry. */
            Dbglogf_objQueue.WQU_Post((int)Dbglogf_enmItem.Close, null);
        }

        /*--------------------------------------------------------------------*/
        /* Dbglogf_OnPollTick:                                                */
        /*                                                                    */
        /* The timer's tick: the Poll item posted to the window's own queue,  */
        /* which looks at the file (Dbglogf_PollFile).  A tick while a Poll   */
        /* still waits is dropped by the queue, so a slow read never piles    */
        /* up ticks behind it.                                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the timer (unused).                                */
        /*     evArgs    : the tick (unused).                                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item posted, or dropped as a duplicate.             */
        /*--------------------------------------------------------------------*/
        private void Dbglogf_OnPollTick(object objSender, EventArgs evArgs)
        {
            /* Posted, with nothing to carry. */
            Dbglogf_objQueue.WQU_Post((int)Dbglogf_enmItem.Poll, null);
        }

        /*--------------------------------------------------------------------*/
        /* Dbglogf_OnFormClosing:                                             */
        /*                                                                    */
        /* The window is asked to close: a decision only.  A close the Close  */
        /* item asked for (the timer already stopped) goes through.  A close  */
        /* from the user -- the title bar, Alt+F4 -- is held, and the Close   */
        /* item posted to do it, so the timer is stopped in one place.  A     */
        /* close for any other reason (Windows shutting down, the task        */
        /* manager) is never held: the process is ending, and the timer with  */
        /* it.                                                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : this window (unused).                              */
        /*     evClosing : the closing event; cancelled to hold the window.   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the close let through, or held with the Close item      */
        /*            posted.                                                 */
        /*--------------------------------------------------------------------*/
        private void Dbglogf_OnFormClosing(object objSender, FormClosingEventArgs evClosing)
        {
            if (Dbglogf_bCloseReady || evClosing.CloseReason != CloseReason.UserClosing)
            {   /* The Close item's own close, or one that cannot wait:   */
                /* let it through.                                        */
                return;
            }

            /* The user's close: held, and the Close item posted to stop   */
            /* the timer and close the window again from the queue.        */
            evClosing.Cancel = true;
            Dbglogf_objQueue.WQU_Post((int)Dbglogf_enmItem.Close, null);
        }

        /*--------------------------------------------------------------------*/
        /* Dbglogf_RunItem:                                                   */
        /*                                                                    */
        /* The window's own queue's runner (WindowQueue.cs): one item the     */
        /* handlers posted, run to its end.  The queue calls it once per item */
        /* and never inside another.                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iItem   : the item, a Dbglogf_enmItem value.                   */
        /*     objArgs : unused.                                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item's work done, or nothing for an unknown item.   */
        /*--------------------------------------------------------------------*/
        private void Dbglogf_RunItem(int iItem, object objArgs)
        {
            switch ((Dbglogf_enmItem)iItem)
            {   /* One case per item: its own function. */
                case Dbglogf_enmItem.SelectAll:
                    /* The whole log selected. */
                    Dbglogf_SelectAll();
                    break;
                case Dbglogf_enmItem.Copy:
                    /* The log onto the clipboard. */
                    Dbglogf_CopyLog();
                    break;
                case Dbglogf_enmItem.Close:
                    /* The timer stopped and the window closed. */
                    Dbglogf_CloseWindow();
                    break;
                case Dbglogf_enmItem.Poll:
                    /* The file looked at, and re-read if it moved. */
                    Dbglogf_PollFile();
                    break;
                default:
                    /* No such item (a guard: only the handlers above post):   */
                    /* nothing done.                                           */
                    break;
            }
        }

        /*--------------------------------------------------------------------*/
        /* Dbglogf_SelectAll:                                                 */
        /*                                                                    */
        /* Select the whole log, ready for Copy or for the keyboard's own     */
        /* copy.  Run by the window's queue (Dbglogf_RunItem).                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the text box holds the focus with everything selected.  */
        /*--------------------------------------------------------------------*/
        private void Dbglogf_SelectAll()
        {
            /* The focus first: a TextBox without the focus does not draw its   */
            /* selection, so without it the click would appear to do nothing.   */
            /* Then everything selected.                                        */
            Dbglogf_txtLog.Focus();
            Dbglogf_txtLog.SelectAll();
        }

        /*--------------------------------------------------------------------*/
        /* Dbglogf_CopyLog:                                                   */
        /*                                                                    */
        /* Put log text on the clipboard: the selection when there is one,    */
        /* otherwise the whole log -- so one click captures everything for a  */
        /* bug report without selecting first.  Run by the window's queue     */
        /* (Dbglogf_RunItem).                                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the clipboard holds the text, or is left alone when     */
        /*            there is no text or the clipboard stays locked.         */
        /*--------------------------------------------------------------------*/
        private void Dbglogf_CopyLog()
        {
            string sText;

            if (Dbglogf_txtLog.SelectionLength > 0)
            {   /* The user picked a part: copy just that. */
                sText = Dbglogf_txtLog.SelectedText;
            }
            else
            {   /* Nothing selected: copy it all. */
                sText = Dbglogf_txtLog.Text;
            }

            if (string.IsNullOrEmpty(sText))
            {   /* An empty log; Clipboard.SetText refuses an empty string. */
                return;
            }

            try
            {   /* The clipboard is shared and another program may hold it. */
                Clipboard.SetText(sText);
            }
            catch
            {   /* Locked: one more try usually succeeds. */
                try
                {   /* The second and last attempt. */
                    Clipboard.SetText(sText);
                }
                catch
                {   /* Still locked: give up quietly; the user can click again. */
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* Dbglogf_CloseWindow:                                               */
        /*                                                                    */
        /* Stop and dispose the poll timer, so it cannot tick against a       */
        /* window that is going away, then close the window -- the one way    */
        /* the Close button and the title bar's close both end.  Run by the   */
        /* window's queue (Dbglogf_RunItem).                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the timer is gone and the window is closing.            */
        /*--------------------------------------------------------------------*/
        private void Dbglogf_CloseWindow()
        {
            if (Dbglogf_tmrPoll != null)
            {   /* Still running: this is the first close. */
                Dbglogf_tmrPoll.Stop();
                Dbglogf_tmrPoll.Dispose();
                Dbglogf_tmrPoll = null;
            }

            /* The flag FIRST: Close raises FormClosing, which lets the   */
            /* close through only once it is set.                         */
            Dbglogf_bCloseReady = true;
            Close();
        }

        /*--------------------------------------------------------------------*/
        /* Dbglogf_PollFile:                                                  */
        /*                                                                    */
        /* Reload the log when the file's size differs from the size at the   */
        /* last read.  Run by the window's queue (Dbglogf_RunItem) for each   */
        /* tick of the poll timer.                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the text box is refreshed, or nothing happens.          */
        /*--------------------------------------------------------------------*/
        private void Dbglogf_PollFile()
        {
            FileInfo fiLog;

            if (string.IsNullOrEmpty(Dbglogf_sLogPath))
            {   /* No file to follow. */
                return;
            }

            try
            {   /* The file may be mid-write or briefly locked. */
                fiLog = new FileInfo(Dbglogf_sLogPath);

                if (!fiLog.Exists)
                {   /* Not created yet (or rotated away): keep what is shown. */
                    return;
                }

                if (fiLog.Length != Dbglogf_lLastFileSize)
                {   /* It grew, or was replaced by a new log. */
                    Dbglogf_LoadLog();
                }
            }
            catch
            {   /* Busy: the next tick tries again. */
            }
        }

        /*--------------------------------------------------------------------*/
        /* Dbglogf_LoadLog:                                                   */
        /*                                                                    */
        /* Read the whole log file into the text box and scroll to its end,   */
        /* remembering the file's size for the next poll.                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the text box shows the file, "(no log file found)", or  */
        /*            the reason the file could not be read.                  */
        /*--------------------------------------------------------------------*/
        private void Dbglogf_LoadLog()
        {
            if (string.IsNullOrEmpty(Dbglogf_sLogPath) || !File.Exists(Dbglogf_sLogPath))
            {   /* Nothing to show.  The size is left as it was, so a file   */
                /* that appears later differs from it and is loaded by the   */
                /* next poll.                                                */
                if (Dbglogf_txtLog != null)
                {   /* The window is built: say so in it.  A guard -- the     */
                    /* first call is made after the text box exists, and      */
                    /* DBGLOGF_LogPath can only be set once construction is   */
                    /* done.                                                  */
                    Dbglogf_txtLog.Text = "(no log file found)";
                }

                /* Nothing read. */
                return;
            }

            try
            {   /* Reading can fail if the file vanishes between the test and the open. */

                /* Share for read AND write: the program writing the log    */
                /* holds it open, and must not be blocked by this reader.   */
                using (FileStream fsLog = new FileStream(Dbglogf_sLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {   /* The stream is closed at this block's one exit.        */
                    /* The size is taken BEFORE the read: anything the       */
                    /* writer adds meanwhile makes the next poll's size      */
                    /* differ, so it is picked up then rather than missed.   */
                    Dbglogf_lLastFileSize = fsLog.Length;

                    using (StreamReader srLog = new StreamReader(fsLog))
                    {   /* The whole file, decoded as UTF-8. */
                        Dbglogf_txtLog.Text = srLog.ReadToEnd();
                    }
                }

                /* Put the caret at the end and bring it into view. */
                Dbglogf_txtLog.SelectionStart  = Dbglogf_txtLog.TextLength;
                Dbglogf_txtLog.SelectionLength = 0;
                Dbglogf_txtLog.ScrollToCaret();
            }
            catch (Exception exRead)
            {   /* Show the reason in place of the log.  The size may    */
                /* already have been taken, in which case the next try   */
                /* waits for the file to change size.                    */
                Dbglogf_txtLog.Text = "Error reading log: " + exRead.Message;
            }
        }
    }
}
