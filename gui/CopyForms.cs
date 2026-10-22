/*----------------------------------------------------------------------------*/
/* CopyForms.cs                                                               */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The three windows a copy can show: the progress dialog that stays up while */
/* the engine works (CPY_clsProgressForm, which also shows a large delete's   */
/* count of entries removed, CPY_SetRemoval), the question asked when a       */
/* destination already exists (CFL_clsConflictForm), and the question asked   */
/* when a name clashes with the metadata manifest's (CLH_clsClashForm).       */
/*                                                                            */
/* All three are MODAL and none uses DialogResult.  None touches the engine:  */
/* the command worker runs the copy and asks its questions.  The progress     */
/* dialog is made, given the run's Cancel and finished by the progress        */
/* windows (CommandProgressWindows.cs, CPY_Finish); the two questions are     */
/* shown by the question presenter (CommandQuestionPresenter.cs), which       */
/* reads the button pressed through CFL_ReadChoice / CLH_ReadChoice once the  */
/* dialog has returned.  EngineSession.cs keeps an older path that would show */
/* the questions over a progress dialog it was given, but every caller now    */
/* gives it none (CommandNativeOperations.cs passes null).                    */
/*                                                                            */
/* Every control is placed by docking or by a flow panel, so nothing carries  */
/* a coordinate.  Each shows its text in the program's dialog font, which the */
/* caller passes in (none decides a font of its own or frees the one it is    */
/* given), and each is sized to its content at that font.                     */
/*----------------------------------------------------------------------------*/

/* The framework: the base types (event arguments, Math), the font, sizes   */
/* and text alignment, and the forms and controls of the three dialogs.     */
using System;
using System.Drawing;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* CPY_CancelRequest:                                                     */
    /*                                                                        */
    /* The shape of the progress dialog's Cancel: a call back to the progress */
    /* windows (CommandProgressWindows.cs), which post a Cancel for the one   */
    /* command the dialog was made for -- by its serial, fixed when the       */
    /* dialog was given it (CPY_AttachCommand).                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the Cancel posted; the dialog closes only when the copy's   */
    /*            end comes (CPY_Finish).                                     */
    /*------------------------------------------------------------------------*/
    internal delegate void CPY_CancelRequest();

    /*------------------------------------------------------------------------*/
    /* CPY_clsProgressForm -- shown while a copy of files, or a raw operation */
    /* on a device or a range (CPY_SetRaw), runs.                             */
    /*                                                                        */
    /* Derives from Form because the framework requires it: WinForms makes a  */
    /* window only as a Form subclass.  That is the one kind of inheritance   */
    /* allowed here -- from a framework base class, never from a class of     */
    /* ours.                                                                  */
    /*------------------------------------------------------------------------*/
    internal sealed class CPY_clsProgressForm : Form
    {
        /* The dialog's geometry and the bar's scale -- documentation table. */
        private const int CPY_PROGRESS_WIDTH  = 560;      // client width, pixels (a path needs room)
        private const int CPY_PROGRESS_HEIGHT = 170;      // the least client height, pixels; more when the content needs it at the dialog's font
        private const int CPY_PAD             = 12;       // padding round the dialog's content, pixels
        private const int CPY_BAR_STEPS       = 1000;     // the progress bar's range; a file's fraction is scaled onto it
        private const ulong CPY_MEBIBYTE      = 1048576;  // bytes in the binary MiB used for file-hash progress

        /* The phase labels -- documentation table.  A raw operation's events   */
        /* carry one of these in place of an item's path.  Each must equal,     */
        /* letter for letter, the string its sender uses -- bfe_image.c         */
        /* (copy, verify), bfe_mkfs.c (zero), bfe_capacity.c (test),            */
        /* EngineSession.cs (hash) -- or the phase is shown as the raw label    */
        /* and counted in sectors.                                              */
        internal const string CPY_LABEL_COPY   = "copy";     // a raw copy's events while copying (the engine's label)
        internal const string CPY_LABEL_VERIFY = "verify";   // ... while reading the destination back to compare (the engine's label)
        internal const string CPY_LABEL_ZERO   = "zero";     // BFE_MakeFsAt's events while writing zeros over the range (the engine's label)
        internal const string CPY_LABEL_HASH   = "hash";     // the session's own events while it computes the image file's SHA-256
        internal const string CPY_LABEL_TEST   = "test";     // BFE_TestCapacity's events: its done and total count STEPS, not bytes (the engine's label)

        /* The controls and the operation's state -- documentation table. */
        private Label           Cpy_lblItem;        // the item being copied (its path), or a raw operation's phase in words
        private ProgressBar     Cpy_prgFile;        // progress through the current file, or through the raw phase
        private Label           Cpy_lblCounts;      // "N item(s) copied, M problem(s)", or a raw phase's "n of m sectors (p%)"
        private FlowLayoutPanel Cpy_pnlButtons;     // holds the Cancel button at the right
        private Button          Cpy_btnCancel;      // asks the copy to stop
        private CPY_CancelRequest Cpy_pfnCancel;    // the Cancel for the command this dialog was made for (CPY_AttachCommand); null until attached
        private bool            Cpy_bFinished;      // true once the progress windows have said the copy is over (CPY_Finish); only then may the dialog close
        private uint            Cpy_uiSectorBytes;  // a RAW operation's sector size (CPY_SetRaw); 0 for a copy of files, which counts items
        private string          Cpy_sCopyText;      // a raw operation's words for its "copy" phase ("Writing to the device")
        private ulong           Cpy_ullPhaseTotal;  // the current raw phase's total as its START gave it; 0 when the image's size is not known
        private bool            Cpy_bRemoval;       // a DELETE's window (CPY_SetRemoval): it counts entries removed, its bar moving with no total

        /*--------------------------------------------------------------------*/
        /* CPY_clsProgressForm (constructor):                                 */
        /*                                                                    */
        /* Build the dialog for one copy.                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     fntDialog : the program's dialog font (the caller's; not       */
        /*                 freed here).                                       */
        /*     sTitle    : the caption ("Extracting to Windows").             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the dialog, not yet shown, and with no Cancel  */
        /*                     attached yet (CPY_AttachCommand).              */
        /*--------------------------------------------------------------------*/
        internal CPY_clsProgressForm(Font fntDialog, string sTitle)
        {
            /* A copy of files until CPY_SetRaw says otherwise (no sector         */
            /* size, no phase words or total), not attached to a copy yet         */
            /* (CPY_AttachCommand), and not finished -- so the dialog cannot be   */
            /* closed until CPY_Finish says the copy is over.                     */
            Cpy_pfnCancel     = null;
            Cpy_bFinished     = false;
            Cpy_uiSectorBytes = 0;
            Cpy_sCopyText     = "";
            Cpy_ullPhaseTotal = 0;
            Cpy_bRemoval      = false;

            /* The caller's dialog font, on the form itself: the controls   */
            /* below are given no font of their own, so each takes the      */
            /* form's once it is added (as found) -- which is why their     */
            /* heights are measured only after they are parented.           */
            this.Font = fntDialog;

            /* A fixed dialog with the caller's caption, centred on the main    */
            /* window and not in the taskbar (it belongs to the main window),   */
            /* at its design size for now -- the height is fitted to the        */
            /* content at the end -- with a pad round everything.               */
            this.Text            = sTitle;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox     = false;
            this.MinimizeBox     = false;
            this.ShowInTaskbar   = false;
            this.StartPosition   = FormStartPosition.CenterParent;
            this.ClientSize      = new Size(CPY_PROGRESS_WIDTH, CPY_PROGRESS_HEIGHT);
            this.Padding         = new Padding(CPY_PAD);

            /* Cancel, sized to its caption at the dialog's font; a press   */
            /* asks the copy to stop (Cpy_OnCancelClicked).  It sits        */
            /* right-aligned in the flow panel at the bottom, below.        */
            Cpy_btnCancel          = new Button();
            Cpy_btnCancel.Text     = "Cancel";
            Cpy_btnCancel.AutoSize = true;
            Cpy_btnCancel.Click   += Cpy_OnCancelClicked;

            /* Its panel: docked along the bottom, laid out right to left so   */
            /* the one button sits at the right-hand end, and as tall as the   */
            /* button needs at the dialog's font.                              */
            Cpy_pnlButtons               = new FlowLayoutPanel();
            Cpy_pnlButtons.Dock          = DockStyle.Bottom;
            Cpy_pnlButtons.FlowDirection = FlowDirection.RightToLeft;
            Cpy_pnlButtons.AutoSize      = true;
            Cpy_pnlButtons.Controls.Add(Cpy_btnCancel);

            /* The counts line, under the bar: not auto-sized (its height is   */
            /* set below, once it has the dialog's font), and "Starting..."    */
            /* until the first event arrives.                                  */
            Cpy_lblCounts          = new Label();
            Cpy_lblCounts.Dock     = DockStyle.Top;
            Cpy_lblCounts.AutoSize = false;
            Cpy_lblCounts.TextAlign = ContentAlignment.MiddleLeft;
            Cpy_lblCounts.Text     = "Starting...";

            /* The per-file (or per-phase) progress bar, from 0 to             */
            /* CPY_BAR_STEPS: CPY_ShowProgress scales each fraction onto it.   */
            Cpy_prgFile         = new ProgressBar();
            Cpy_prgFile.Dock    = DockStyle.Top;
            Cpy_prgFile.Minimum = 0;
            Cpy_prgFile.Maximum = CPY_BAR_STEPS;

            /* The current item, two lines tall (set below), so a long path   */
            /* can wrap onto a second line; what still does not fit is cut    */
            /* with an ellipsis rather than spilling out of the label.  How   */
            /* the framework breaks a path with no space in it is not         */
            /* established here.                                              */
            Cpy_lblItem              = new Label();
            Cpy_lblItem.Dock         = DockStyle.Top;
            Cpy_lblItem.AutoSize     = false;
            Cpy_lblItem.AutoEllipsis = true;
            Cpy_lblItem.Text         = "";

            /* Docking runs in reverse order of adding: the control added       */
            /* LAST is docked first (the framework's rule, as found).  So the   */
            /* button panel, added last, takes                                  */
            /* the bottom edge, and the three Top-docked controls stack down    */
            /* from the top in the reverse of their adding: the item line,      */
            /* the bar, then the counts.                                        */
            this.Controls.Add(Cpy_lblCounts);
            this.Controls.Add(Cpy_prgFile);
            this.Controls.Add(Cpy_lblItem);
            this.Controls.Add(Cpy_pnlButtons);

            /* The two lines' heights are measured only now, once they have     */
            /* their parent and so the dialog's font (before, they would be     */
            /* measured in the default one); then the dialog is made tall       */
            /* enough for all four rows and both pads, never shorter than its   */
            /* design height.                                                   */
            Cpy_lblCounts.Height = Cpy_lblCounts.PreferredHeight + CPY_PAD;
            Cpy_lblItem.Height   = (Cpy_lblItem.PreferredHeight * 2) + CPY_PAD;
            this.ClientSize      = new Size(CPY_PROGRESS_WIDTH,
                                            Math.Max(CPY_PROGRESS_HEIGHT, Cpy_lblItem.Height + Cpy_prgFile.Height + Cpy_lblCounts.Height +
                                                                          Cpy_pnlButtons.PreferredSize.Height + (2 * CPY_PAD)));

            /* Hooked last, so the sizing above does not run them: a resize   */
            /* re-fits the item line's height, and a close is refused until   */
            /* the copy has finished.                                         */
            this.Resize      += Cpy_OnProgressResize;
            this.FormClosing += Cpy_OnProgressClosing;
        }

        /*------------------------------------------------------------------*/
        /* CPY_AttachCommand:                                               */
        /*                                                                  */
        /* Give the dialog's Cancel (and its X) the command it is to stop:  */
        /* the progress windows' Cancel for that command, by its serial.    */
        /* The dialog never holds the engine's control or decides what may  */
        /* run; it only asks.                                               */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     pfnCancel : posts a Cancel for that command.                 */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : Cancel can now ask for the stop; the dialog still     */
        /*            closes only through CPY_Finish.                       */
        /*------------------------------------------------------------------*/
        internal void CPY_AttachCommand(CPY_CancelRequest pfnCancel)
        {
            /* Kept.  The progress windows attach it before the dialog is   */
            /* shown, so no click can find none; it names its command by    */
            /* serial, so it can never stop a later one.                    */
            Cpy_pfnCancel = pfnCancel;
        }

        /*--------------------------------------------------------------------*/
        /* CPY_SetRaw:                                                        */
        /*                                                                    */
        /* Make this the window of a RAW operation -- a device read or        */
        /* written whole, a range zero-filled, a capacity test -- rather than */
        /* a copy of files.  Its events are then not items but PHASES, so the */
        /* item line names the phase in words and the counts line says how    */
        /* far it has got ("n of m sectors (p%)").  Without it the line would */
        /* say "0 item(s) copied" for the whole of a long write, and the bar  */
        /* alone could not say which pass it was showing.  The progress       */
        /* windows call it, from the command's progress view, before the      */
        /* dialog is shown (CommandProgressWindows.cs).                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uiSectorBytes : the device's bytes per sector, which the       */
        /*                     counts are given in; 0 is taken as 512.        */
        /*     sCopyText     : the words for the "copy" phase -- "Writing to  */
        /*                     the device", "Reading the device".             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : later progress is shown as phases and sectors.          */
        /*--------------------------------------------------------------------*/
        internal void CPY_SetRaw(uint uiSectorBytes, string sCopyText)
        {
            /* The sector size: a non-zero value is also what marks this       */
            /* dialog as a raw operation's (CPY_ShowProgress tests it), so a   */
            /* zero cannot be kept.                                            */
            Cpy_uiSectorBytes = uiSectorBytes;

            if (Cpy_uiSectorBytes == 0)
            {   /* No figure given: the sector every disk had for decades --   */
                /* and the raw mode set all the same, as the caller asked.     */
                Cpy_uiSectorBytes = 512;
            }

            /* The words the "copy" phase is shown with, which only the   */
            /* caller knows (which way the data is going).                */
            Cpy_sCopyText = sCopyText;
        }

        /*--------------------------------------------------------------------*/
        /* CPY_SetRemoval:                                                    */
        /*                                                                    */
        /* Make this the window of a DELETE: its events are entries removed,  */
        /* so the counts line says how many have gone ("N item(s) removed")   */
        /* rather than copied, and since a delete runs with no count of what  */
        /* it will remove (no walk is made first), the bar moves on its own   */
        /* instead of filling against a total.  The progress windows call it, */
        /* from the command's progress view, before the dialog is shown       */
        /* (CommandProgressWindows.cs).                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : later progress is shown as a count of removals.         */
        /*--------------------------------------------------------------------*/
        internal void CPY_SetRemoval()
        {
            /* The mode CPY_ShowProgress tests, and the bar's moving style   */
            /* (a marquee, which shows activity, not a share).               */
            Cpy_bRemoval      = true;
            Cpy_prgFile.Style = ProgressBarStyle.Marquee;
        }

        /*--------------------------------------------------------------------*/
        /* Cpy_PhaseText:                                                     */
        /*                                                                    */
        /* A raw operation's phase label in words, for the item line.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sLabel : the event's label (CPY_LABEL_*).                      */
        /*     uiKind : the event (ENG_EVENT_*).                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the words; an unknown label is shown as it came.      */
        /*--------------------------------------------------------------------*/
        private string Cpy_PhaseText(string sLabel, uint uiKind)
        {
            if (sLabel == CPY_LABEL_COPY)
            {   /* The copy itself, in the caller's words. */
                return(Cpy_sCopyText);
            }

            if (sLabel == CPY_LABEL_VERIFY)
            {   /* The read-back after a copy, which compares the device's   */
                /* bytes with the image's.                                   */
                return("Reading back to compare with the image");
            }

            if (sLabel == CPY_LABEL_ZERO && uiKind == ENG_clsNative.ENG_EVENT_DONE)
            {   /* The zeros are written, and the format that follows them    */
                /* runs under this same window but sends no events of its     */
                /* own (bfe_mkfs.c) -- so the zeros' DONE is the last event   */
                /* until the format ends, and it names what comes next        */
                /* rather than leave a full bar saying "Writing zeros".       */
                /* This test comes before the plain "zero" one below.         */
                return("Formatting");
            }

            if (sLabel == CPY_LABEL_ZERO)
            {   /* The erase before a format. */
                return("Writing zeros");
            }

            if (sLabel == CPY_LABEL_HASH)
            {   /* The checksum over the image file. */
                return("Computing the image file's SHA-256");
            }

            if (sLabel == CPY_LABEL_TEST)
            {   /* The capacity test: writing, reading back and putting back. */
                return("Testing how much of the device really holds data");
            }

            /* A label this window does not know: as it came. */
            return(sLabel);
        }

        /*--------------------------------------------------------------------*/
        /* Cpy_RawCounts:                                                     */
        /*                                                                    */
        /* The counts line of a raw phase: how far it has got, in the unit    */
        /* that phase counts in, and its share when the total is known.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sLabel   : the event's label (CPY_LABEL_*).                    */
        /*     ullDone  : bytes done in this phase (steps for the capacity    */
        /*                test; the compressed file's bytes when the image's  */
        /*                size is not known).                                 */
        /*     ullTotal : the phase's total in the same unit, or 0.           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : "123,456 of 1,988,608 sectors (6.2%)" and the like.   */
        /*--------------------------------------------------------------------*/
        private string Cpy_RawCounts(string sLabel, ulong ullDone, ulong ullTotal)
        {
            string sPercent;
            ulong  ullUnit;
            string sUnit;

            /* No share to show unless the event carries a total. */
            sPercent = "";

            if (ullTotal != 0)
            {   /* A known total: the share done, to a tenth of a percent   */
                /* (worked in floating point, so a large count cannot       */
                /* overflow the product).                                   */
                sPercent = " (" + (((double)ullDone * 100.0) / (double)ullTotal).ToString("F1") + "%)";
            }

            if (sLabel == CPY_LABEL_HASH)
            {   /* The SHA-256 pass reads the image FILE -- compressed,        */
                /* perhaps -- which has no sectors: its bytes, in mebibytes.   */
                ullUnit = CPY_MEBIBYTE;
                sUnit   = " MiB";
            }
            else if (sLabel == CPY_LABEL_TEST)
            {   /* The capacity test's done and total are STEPS, not bytes   */
                /* (bfe_capacity.c), so they are shown one for one.          */
                ullUnit = 1;
                sUnit   = " steps";
            }
            else if (Cpy_ullPhaseTotal == 0 && ullTotal != 0)
            {   /* The phase's START gave no total -- the image's size is not   */
                /* known (a .gz, or a .zst made from a pipe) -- yet this        */
                /* event has one: the engine is then counting the COMPRESSED    */
                /* file's bytes against its size (bfe_image.c), so no sector    */
                /* count can be given, only the share of the file read, said    */
                /* as such.                                                     */
                return(sPercent.Trim() + " of the compressed file read");
            }
            else
            {   /* A copy, a read-back or the zeros: the device's sectors.     */
                /* The total is rounded up, so a last partial sector counts    */
                /* as one; the done is rounded down, so it never runs ahead.   */
                ullUnit = Cpy_uiSectorBytes;
                sUnit   = " sectors";
            }

            if (ullTotal == 0)
            {   /* No total at all (a copy of an image whose size is not   */
                /* known, at its START): what is done, alone.              */
                return((ullDone / ullUnit).ToString("N0") + sUnit);
            }

            /* Done of total, and the share. */
            return((ullDone / ullUnit).ToString("N0") + " of " + ((ullTotal + ullUnit - 1) / ullUnit).ToString("N0") + sUnit + sPercent);
        }

        /*--------------------------------------------------------------------*/
        /* CPY_ShowProgress:                                                  */
        /*                                                                    */
        /* Show a progress view's figures, on the UI thread.  The progress    */
        /* windows call it only for the command this dialog was made for      */
        /* (CommandProgressWindows.cs), so another command's progress never   */
        /* reaches it.                                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sItem     : the path of the item being copied, a raw phase's   */
        /*                 label (CPY_LABEL_*), or a delete's entry just      */
        /*                 removed.                                           */
        /*     uiKind    : the event (ENG_EVENT_*): a raw phase's START       */
        /*                 carries its total, which says whether the image's  */
        /*                 size is known.                                     */
        /*     ullDone   : bytes of the current file (or phase) done.         */
        /*     ullTotal  : the current file's size (0 for a directory), or    */
        /*                 the phase's total.                                 */
        /*     iCopied   : items completed so far -- for a delete, the        */
        /*                 entries removed (ERC_clsRemoveControl,             */
        /*                 EngineSession.cs, sends that count here).          */
        /*     iProblems : items skipped or failed so far.                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the labels and the bar show the new state.              */
        /*--------------------------------------------------------------------*/
        internal void CPY_ShowProgress(string sItem, uint uiKind, ulong ullDone, ulong ullTotal, int iCopied, int iProblems)
        {
            int iValue;

            if (Cpy_uiSectorBytes != 0)
            {   /* A raw operation -- CPY_SetRaw left a sector size, which a   */
                /* copy of files never has: the item is a phase label, not a   */
                /* path, so it is shown in words, and the counts line says     */
                /* how far the phase has got in its own unit.                  */
                if (uiKind == ENG_clsNative.ENG_EVENT_START)
                {   /* A new phase: its START's total is kept, since only it   */
                    /* says whether the image's size is known -- the later     */
                    /* events of an unknown size carry the compressed file's   */
                    /* total instead (Cpy_RawCounts).                          */
                    Cpy_ullPhaseTotal = ullTotal;
                }

                /* The phase in words, and its count in its own unit. */
                Cpy_lblItem.Text   = Cpy_PhaseText(sItem, uiKind);
                Cpy_lblCounts.Text = Cpy_RawCounts(sItem, ullDone, ullTotal);
            }
            else if (Cpy_bRemoval)
            {   /* A delete: the entry just removed, and how many have gone.   */
                /* Its bar is a marquee and takes no value, so nothing more    */
                /* is shown.                                                   */
                Cpy_lblItem.Text   = sItem;
                Cpy_lblCounts.Text = iCopied + " item(s) removed";
                return;
            }
            else
            {   /* A copy of files: the item's path, and the running tally   */
                /* of what is done and what was not.                         */
                Cpy_lblItem.Text   = sItem;
                Cpy_lblCounts.Text = iCopied + " item(s) copied, " + iProblems + " skipped or failed";
            }

            /* Scale the file's fraction onto the bar.  An empty file or a   */
            /* directory has no fraction; show the bar empty for it.         */
            iValue = 0;

            if (ullTotal != 0)
            {   /* A file (or phase) with a size: done / total, in bar      */
                /* steps -- multiplied before the divide, so the fraction   */
                /* keeps its precision; the 64-bit product could overflow   */
                /* only past about 18 000 TB done.                          */
                iValue = (int)((ullDone * (ulong)CPY_BAR_STEPS) / ullTotal);
            }

            if (iValue > CPY_BAR_STEPS)
            {   /* Done past the total -- a file that grew while it was   */
                /* being read: held at full, since the bar throws on a    */
                /* value past its Maximum (as found).                     */
                iValue = CPY_BAR_STEPS;
            }

            /* The bar moved to the fraction. */
            Cpy_prgFile.Value = iValue;
        }

        /*--------------------------------------------------------------------*/
        /* CPY_Finish:                                                        */
        /*                                                                    */
        /* The copy is over: allow the dialog to close, and close it.  Called */
        /* on the UI thread by the progress windows when the command's        */
        /* progress closes (CommandProgressWindows.cs, its ProgressClose      */
        /* view); nothing else closes the dialog.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the dialog is closed and ShowDialog returns.            */
        /*--------------------------------------------------------------------*/
        internal void CPY_Finish()
        {
            /* The flag FIRST: Close raises FormClosing, and                 */
            /* Cpy_OnProgressClosing refuses every close until the flag is   */
            /* set -- the other order would turn this close into a Cancel    */
            /* and leave the dialog up for good.  The close ends the         */
            /* ShowDialog the progress windows are waiting in.               */
            Cpy_bFinished = true;
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Cpy_OnCancelClicked:                                               */
        /*                                                                    */
        /* The Cancel button: ask the copy to stop (Cpy_RequestCancel).       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the Cancel posted, and the button shows it.             */
        /*--------------------------------------------------------------------*/
        private void Cpy_OnCancelClicked(object objSender, EventArgs evArgs)
        {
            /* The user's stop. */
            Cpy_RequestCancel();
        }

        /*--------------------------------------------------------------------*/
        /* Cpy_RequestCancel:                                                 */
        /*                                                                    */
        /* Ask the copy to stop.  The dialog stays up until the copy really   */
        /* has stopped -- its completion closes it -- so here the request is  */
        /* only made and shown.  The Cancel button's handler calls it, and so */
        /* does a close the user asks for while the copy runs                 */
        /* (Cpy_OnProgressClosing).                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the Cancel posted, and the button shows it.             */
        /*--------------------------------------------------------------------*/
        private void Cpy_RequestCancel()
        {
            if (Cpy_pfnCancel != null)
            {   /* A Cancel is attached: posted, for this dialog's command   */
                /* only.  The worker raises the engine's cancel flag         */
                /* itself; the dialog never touches it.                      */
                Cpy_pfnCancel();
            }

            /* The button greyed and relabelled: a second press could do    */
            /* nothing more, and the caption tells the user the stop is     */
            /* under way, since the engine may take a moment to reach its   */
            /* next event.                                                  */
            Cpy_btnCancel.Enabled = false;
            Cpy_btnCancel.Text    = "Cancelling...";
        }

        /*--------------------------------------------------------------------*/
        /* Cpy_OnProgressClosing:                                             */
        /*                                                                    */
        /* Something tried to close the dialog while the copy is still        */
        /* running.  The close is always refused -- the dialog must outlive   */
        /* the copy -- and what it means depends on who asked:                */
        /*                                                                    */
        /* - the user, at this dialog (the X, or Alt+F4): taken as Cancel;    */
        /* - the MAIN WINDOW being closed (CloseReason.FormOwnerClosing --    */
        /*   WinForms asks every owned window first, and the main window is   */
        /*   this dialog's owner: the progress windows show it with           */
        /*   ShowDialog over the main window, CommandProgressWindows.cs):     */
        /*   NOT a Cancel.  That close is Exit -- the main window's           */
        /*   FormClosing refuses it and posts an Exit request while the       */
        /*   worker runs (MainForm.cs, Main_OnFormClosing), and the worker    */
        /*   pauses the command at its next checkpoint to ask whether it may  */
        /*   be stopped (CommandInterrupts.cs, SYS_Checkpoint).  Taking it as */
        /*   Cancel here would stop the copy before that question was         */
        /*   answered, so a No ("continue") could not continue anything.  How */
        /*   the main window comes to be closed while this modal dialog       */
        /*   disables it is not established here.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the dialog.                                        */
        /*     evClosing : the closing event; cancelled while the copy runs.  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the close is refused (and, from the user, the copy      */
        /*            asked to stop), or allowed once finished.               */
        /*--------------------------------------------------------------------*/
        private void Cpy_OnProgressClosing(object objSender, FormClosingEventArgs evClosing)
        {
            if (!Cpy_bFinished)
            {   /* Still copying: the close is refused -- closing the window   */
                /* under a running copy would leave the copy going with        */
                /* nothing to show it or stop it.  CPY_Finish's close, once    */
                /* the copy is over, passes.                                   */
                evClosing.Cancel = true;

                if (evClosing.CloseReason == CloseReason.FormOwnerClosing)
                {   /* The main window is closing: that is Exit, posted and    */
                    /* asked about by the main window and the worker, so the   */
                    /* copy is left running until the user answers it.         */
                    return;
                }

                /* The user closed this dialog: the same stop as its Cancel button. */
                Cpy_RequestCancel();
            }
        }

        /*--------------------------------------------------------------------*/
        /* Cpy_OnProgressResize:                                              */
        /*                                                                    */
        /* Keep the item label's height at two text lines for the current     */
        /* font.  The dialog has a fixed border today; this is what keeps the */
        /* layout right if that, or the font, ever changes.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the dialog.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the label's height is recomputed.                       */
        /*--------------------------------------------------------------------*/
        private void Cpy_OnProgressResize(object objSender, EventArgs evArgs)
        {
            /* Two lines of the label's font, plus a pad -- the same sum the   */
            /* constructor uses.  Only the height: docking keeps the width.    */
            Cpy_lblItem.Height = (Cpy_lblItem.PreferredHeight * 2) + CPY_PAD;
        }
    }

    /*------------------------------------------------------------------------*/
    /* CFL_clsConflictForm -- asks what to do about a destination file that   */
    /* already exists.                                                        */
    /*                                                                        */
    /* Derives from Form because the framework requires it: WinForms makes a  */
    /* window only as a Form subclass.  That is the one kind of inheritance   */
    /* allowed here -- from a framework base class, never from a class of     */
    /* ours.                                                                  */
    /*------------------------------------------------------------------------*/
    internal sealed class CFL_clsConflictForm : Form
    {
        /* The answers the dialog can give -- documentation table.  The         */
        /* question presenter turns each into the answer kind the worker acts   */
        /* on (CommandQuestionPresenter.cs, Qpr_Conflict).                      */
        internal const int CFL_CHOICE_OVERWRITE     = 1;    // replace this one
        internal const int CFL_CHOICE_OVERWRITE_ALL = 2;    // replace this one and every later one
        internal const int CFL_CHOICE_SKIP          = 3;    // keep this one
        internal const int CFL_CHOICE_SKIP_ALL      = 4;    // keep this one and every later one
        internal const int CFL_CHOICE_CANCEL        = 5;    // stop the whole copy

        /* The dialog's geometry -- documentation table. */
        private const int CFL_CONFLICT_WIDTH = 560;    // the least client width, pixels; wider when the button row needs it at the dialog's font
        private const int CFL_CONFLICT_PAD   = 12;     // padding round the content, pixels

        /* The answer given -- documentation table.  Read from outside only   */
        /* through CFL_ReadChoice, once the dialog has returned.              */
        private int Cfl_iChoice;                      // one of CFL_CHOICE_*; CANCEL until a button says otherwise

        /* The dialog's two parts, laid out by docking. */
        private Label           Cfl_lblQuestion;       // the question and the path
        private FlowLayoutPanel Cfl_pnlChoices;        // the five buttons, right-aligned

        /*--------------------------------------------------------------------*/
        /* CFL_clsConflictForm (constructor):                                 */
        /*                                                                    */
        /* Build the question for one existing destination.  Closing the      */
        /* dialog without pressing a button counts as Cancel.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     fntDialog    : the program's dialog font (the caller's; not    */
        /*                    freed here).                                    */
        /*     sDestination : the destination that already exists.            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the dialog, not yet shown.                     */
        /*--------------------------------------------------------------------*/
        internal CFL_clsConflictForm(Font fntDialog, string sDestination)
        {
            /* Cancel until a button says otherwise, so closing the dialog   */
            /* by its X (or Alt+F4) stops the copy: stopping is the answer   */
            /* that changes nothing more.  Escape does not close it -- no    */
            /* button is made the dialog's cancel button.                    */
            Cfl_iChoice = CFL_CHOICE_CANCEL;

            /* The caller's dialog font, on the form itself, for the controls   */
            /* below to take once added (as found); the question is measured    */
            /* in it only after it is parented (Cfl_FitHeight).                 */
            this.Font = fntDialog;

            /* A question in the shape of a message box, so like one it has    */
            /* no caption icon (the framework's generic one would show); a     */
            /* fixed dialog centred on the progress dialog it is shown over,   */
            /* with a pad round everything.  Its size is set at the end.       */
            this.Text            = "The destination already exists";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox     = false;
            this.MinimizeBox     = false;
            this.ShowInTaskbar   = false;
            this.ShowIcon        = false;
            this.StartPosition   = FormStartPosition.CenterParent;
            this.Padding         = new Padding(CFL_CONFLICT_PAD);

            /* The buttons' row, docked along the bottom.  It flows right to     */
            /* left, so the first button added sits at the right-hand end, and   */
            /* it does not wrap: the five stay on one line, and the dialog is    */
            /* widened to hold them (below).                                     */
            Cfl_pnlChoices               = new FlowLayoutPanel();
            Cfl_pnlChoices.Dock          = DockStyle.Bottom;
            Cfl_pnlChoices.FlowDirection = FlowDirection.RightToLeft;
            Cfl_pnlChoices.AutoSize      = true;
            Cfl_pnlChoices.WrapContents  = false;

            /* The five choices, added from the right: Cancel at the          */
            /* right-hand end, then the Skips, then the Overwrites, so the    */
            /* row reads left to right Overwrite, Overwrite All, Skip, Skip   */
            /* All, Cancel.  None is made the default button, so Enter        */
            /* presses whichever has the focus (which starts focused is the   */
            /* framework's choice, not established here); why no default is   */
            /* set is not recorded.  Each button has a click handler of its   */
            /* own, which records its choice.                                 */
            Cfl_AddChoice("Cancel", Cfl_OnCancelClicked);
            Cfl_AddChoice("Skip All", Cfl_OnSkipAllClicked);
            Cfl_AddChoice("Skip", Cfl_OnSkipClicked);
            Cfl_AddChoice("Overwrite All", Cfl_OnOverwriteAllClicked);
            Cfl_AddChoice("Overwrite", Cfl_OnOverwriteClicked);

            /* The question, filling the space above the buttons and wrapped   */
            /* to its width: the path on a line of its own between the two     */
            /* sentences, so even a long one is read whole.                    */
            Cfl_lblQuestion          = new Label();
            Cfl_lblQuestion.Dock     = DockStyle.Fill;
            Cfl_lblQuestion.AutoSize = false;
            Cfl_lblQuestion.Text     = "This file already exists at the destination:\n\n" + sDestination +
                                       "\n\nReplace it with the one being copied?";

            /* The question first and the row last: the control added last     */
            /* is docked first (as found), so the row takes the bottom edge    */
            /* and the Fill-docked question gets what is left.  In the other   */
            /* order the question would fill the whole dialog first.           */
            this.Controls.Add(Cfl_lblQuestion);
            this.Controls.Add(Cfl_pnlChoices);

            /* The width is the design width, or the button row's if that is     */
            /* wider at this font (the row does not wrap, so a narrower dialog   */
            /* would cut a button off).  The height set here is only a start;    */
            /* Cfl_FitHeight then fits it to the question as wrapped at that     */
            /* width, plus the button row.  The resize handler is hooked         */
            /* first, so a later width change refits it too; the fit below       */
            /* raises one resize of its own, whose handler simply fits again     */
            /* to the same height (and turns away the resize THAT raises).       */
            this.ClientSize = new Size(Math.Max(CFL_CONFLICT_WIDTH, Cfl_pnlChoices.PreferredSize.Width + (2 * CFL_CONFLICT_PAD)),
                                       CFL_CONFLICT_WIDTH / 2);
            this.Resize    += Cfl_OnConflictResize;
            Cfl_FitHeight();
        }

        /*--------------------------------------------------------------------*/
        /* CFL_ReadChoice:                                                    */
        /*                                                                    */
        /* Which button was pressed, for the question presenter to read once  */
        /* ShowDialog has returned (CommandQuestionPresenter.cs,              */
        /* Qpr_Conflict).  Reading it changes nothing: what the answer means  */
        /* for the copy is decided by the presenter and the worker, not here. */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : one of CFL_CHOICE_*; CFL_CHOICE_CANCEL when the dialog   */
        /*           was closed without a button (its X, Alt+F4, or an Exit   */
        /*           closing it), since the constructor starts it there.      */
        /*--------------------------------------------------------------------*/
        internal int CFL_ReadChoice()
        {
            /* The choice the last button pressed recorded, or the   */
            /* constructor's Cancel when none was.                   */
            return(Cfl_iChoice);
        }

        /*--------------------------------------------------------------------*/
        /* Cfl_AddChoice:                                                     */
        /*                                                                    */
        /* Add one button to the row, with the click handler of its own that  */
        /* records the choice it stands for.                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sText      : the button's caption.                             */
        /*     pfnClicked : that button's own handler (one per choice).       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the button is in the row.                               */
        /*--------------------------------------------------------------------*/
        private void Cfl_AddChoice(string sText, EventHandler pfnClicked)
        {
            Button btnChoice;

            /* A button sized to its caption at the dialog's font, hooked to   */
            /* the handler given for it.                                       */
            btnChoice          = new Button();
            btnChoice.Text     = sText;
            btnChoice.AutoSize = true;
            btnChoice.Click   += pfnClicked;

            /* Into the row, which places it left of the ones added before. */
            Cfl_pnlChoices.Controls.Add(btnChoice);
        }

        /*--------------------------------------------------------------------*/
        /* Cfl_FitHeight:                                                     */
        /*                                                                    */
        /* Size the dialog's height to its content at its present width, so   */
        /* the whole question shows above the buttons, however long the path. */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the client height fits the text.                        */
        /*--------------------------------------------------------------------*/
        private void Cfl_FitHeight()
        {
            Size sizText;
            int  iTextWidth;

            /* The width the question's label really has: the client width   */
            /* less the dialog's pad at each side.                           */
            iTextWidth = this.ClientSize.Width - (2 * CFL_CONFLICT_PAD);

            if (iTextWidth <= 0)
            {   /* No room to measure against (a dialog squeezed to nothing):   */
                /* the height is left as it is.                                 */
                return;
            }

            /* The question's height wrapped at that width, in the label's   */
            /* own font (the dialog's, inherited now it is parented).  The   */
            /* flags ask for word wrapping as an edit box does it; whether   */
            /* they match the label's own drawing exactly is not             */
            /* established here.                                             */
            sizText = TextRenderer.MeasureText(Cfl_lblQuestion.Text, Cfl_lblQuestion.Font,
                                               new Size(iTextWidth, int.MaxValue),
                                               TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);

            /* The width kept; the height the text, the button row and three   */
            /* pads -- one above and one below the content (the dialog's       */
            /* Padding), and one more that the question's Fill label takes     */
            /* as the gap between its text and the buttons.                    */
            this.ClientSize = new Size(this.ClientSize.Width,
                                       sizText.Height + Cfl_pnlChoices.PreferredSize.Height + (3 * CFL_CONFLICT_PAD));
        }

        /*--------------------------------------------------------------------*/
        /* Cfl_OnCancelClicked:                                               */
        /*                                                                    */
        /* Cancel: the whole copy stops (Cfl_Choose).                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Cfl_iChoice is set and the dialog closes.               */
        /*--------------------------------------------------------------------*/
        private void Cfl_OnCancelClicked(object objSender, EventArgs evArgs)
        {
            /* Stop the whole copy. */
            Cfl_Choose(CFL_CHOICE_CANCEL);
        }

        /*--------------------------------------------------------------------*/
        /* Cfl_OnSkipAllClicked:                                              */
        /*                                                                    */
        /* Skip All: this file and every later one that exists is kept        */
        /* (Cfl_Choose).                                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Cfl_iChoice is set and the dialog closes.               */
        /*--------------------------------------------------------------------*/
        private void Cfl_OnSkipAllClicked(object objSender, EventArgs evArgs)
        {
            /* Keep this one and every later one. */
            Cfl_Choose(CFL_CHOICE_SKIP_ALL);
        }

        /*--------------------------------------------------------------------*/
        /* Cfl_OnSkipClicked:                                                 */
        /*                                                                    */
        /* Skip: this file is kept (Cfl_Choose).                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Cfl_iChoice is set and the dialog closes.               */
        /*--------------------------------------------------------------------*/
        private void Cfl_OnSkipClicked(object objSender, EventArgs evArgs)
        {
            /* Keep this one. */
            Cfl_Choose(CFL_CHOICE_SKIP);
        }

        /*--------------------------------------------------------------------*/
        /* Cfl_OnOverwriteAllClicked:                                         */
        /*                                                                    */
        /* Overwrite All: this file and every later one that exists is        */
        /* replaced (Cfl_Choose).                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Cfl_iChoice is set and the dialog closes.               */
        /*--------------------------------------------------------------------*/
        private void Cfl_OnOverwriteAllClicked(object objSender, EventArgs evArgs)
        {
            /* Replace this one and every later one. */
            Cfl_Choose(CFL_CHOICE_OVERWRITE_ALL);
        }

        /*--------------------------------------------------------------------*/
        /* Cfl_OnOverwriteClicked:                                            */
        /*                                                                    */
        /* Overwrite: this file is replaced (Cfl_Choose).                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Cfl_iChoice is set and the dialog closes.               */
        /*--------------------------------------------------------------------*/
        private void Cfl_OnOverwriteClicked(object objSender, EventArgs evArgs)
        {
            /* Replace this one. */
            Cfl_Choose(CFL_CHOICE_OVERWRITE);
        }

        /*---------------------------------------------------------------------*/
        /* Cfl_Choose:                                                         */
        /*                                                                     */
        /* A button was pressed: record what it stands for and close.  Each    */
        /* of the five buttons' handlers calls it with its own choice.         */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     iChoice : the CFL_CHOICE_* value the pressed button stands for. */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : Cfl_iChoice is set and the dialog closes.                */
        /*---------------------------------------------------------------------*/
        private void Cfl_Choose(int iChoice)
        {
            /* The choice, recorded before the close so the waiting presenter   */
            /* finds it when ShowDialog returns; then the dialog closes.        */
            Cfl_iChoice = iChoice;
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Cfl_OnConflictResize:                                              */
        /*                                                                    */
        /* The dialog's width changed: the question wraps differently, so     */
        /* refit the height.  Guarded against the resize it causes itself.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the dialog.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the height fits the text at the new width.              */
        /*--------------------------------------------------------------------*/
        private void Cfl_OnConflictResize(object objSender, EventArgs evArgs)
        {
            /* Detach while refitting, since setting ClientSize raises Resize. */
            this.Resize -= Cfl_OnConflictResize;
            Cfl_FitHeight();
            this.Resize += Cfl_OnConflictResize;
        }
    }

    /*---------------------------------------------------------------------------*/
    /* CLH_clsClashForm -- asks which to keep when a folder being extracted with */
    /* the metadata manifest holds an entry of the manifest's own name           */
    /* (_ext4meta.txt): the image's entry, or the manifest.  Most likely the     */
    /* folder was extracted once before, manifest and all, and copied back       */
    /* into an image, so either answer is plausible and neither is assumed.      */
    /* The engine asks once, before anything is made.  Laid out as               */
    /* CFL_clsConflictForm is, and kept apart from it: the two questions share   */
    /* a shape, not a meaning.                                                   */
    /*                                                                           */
    /* Derives from Form because the framework requires it: WinForms makes a     */
    /* window only as a Form subclass.  That is the one kind of inheritance      */
    /* allowed here -- from a framework base class, never from a class of        */
    /* ours.                                                                     */
    /*---------------------------------------------------------------------------*/
    internal sealed class CLH_clsClashForm : Form
    {
        /* The answers the dialog can give -- documentation table, numbered    */
        /* apart from the conflict question's.  The question presenter turns   */
        /* each into the answer kind the worker acts on                        */
        /* (CommandQuestionPresenter.cs, Qpr_Clash).                           */
        internal const int CLH_KEEP_FILE     = 11;   // extract the image's entry; write no manifest
        internal const int CLH_KEEP_MANIFEST = 12;   // write the manifest; leave the image's entry out
        internal const int CLH_CANCEL        = 13;   // stop before anything is made

        /* The dialog's geometry -- documentation table. */
        private const int CLH_WIDTH = 560;       // the least client width, pixels; wider when the button row needs it at the dialog's font
        private const int CLH_PAD   = 12;        // padding round the content, pixels

        /* The answer given -- documentation table.  Read from outside only   */
        /* through CLH_ReadChoice, once the dialog has returned.              */
        private int Clh_iChoice;                 // one of CLH_*; CANCEL until a button says otherwise

        /* The dialog's two parts, laid out by docking. */
        private Label           Clh_lblClashQuestion;  // the question, the entry and where the manifest goes
        private FlowLayoutPanel Clh_pnlClashChoices;   // the three buttons, right-aligned

        /*--------------------------------------------------------------------*/
        /* CLH_clsClashForm (constructor):                                    */
        /*                                                                    */
        /* Build the question for one entry of the manifest's name.  Closing  */
        /* the dialog without pressing a button counts as Cancel, which       */
        /* leaves nothing made.                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     fntDialog   : the program's dialog font (the caller's; not     */
        /*                   freed here).                                     */
        /*     sImageEntry : the image entry with the manifest's name.        */
        /*     sManifest   : the host path the manifest would be written to.  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the dialog, not yet shown.                     */
        /*--------------------------------------------------------------------*/
        internal CLH_clsClashForm(Font fntDialog, string sImageEntry, string sManifest)
        {
            /* Cancel until a button says otherwise, so closing the dialog by   */
            /* its X (or Alt+F4) stops the extract before it starts: the        */
            /* answer that changes nothing.  No button is the cancel button,    */
            /* so Escape does not close it, as in the conflict question.        */
            Clh_iChoice = CLH_CANCEL;

            /* The caller's dialog font, for the controls below to take once   */
            /* added; the question is measured in it once it is parented.      */
            this.Font = fntDialog;

            /* A question in the shape of a message box: no caption icon, a    */
            /* fixed dialog centred on the progress dialog it is shown over,   */
            /* a pad round everything.  Its size is set at the end.            */
            this.Text            = "The manifest's name is taken";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox     = false;
            this.MinimizeBox     = false;
            this.ShowInTaskbar   = false;
            this.ShowIcon        = false;
            this.StartPosition   = FormStartPosition.CenterParent;
            this.Padding         = new Padding(CLH_PAD);

            /* The buttons' row along the bottom, flowing right to left so the   */
            /* first added sits at the right-hand end, and never wrapping: the   */
            /* dialog is widened to hold it (below).                             */
            Clh_pnlClashChoices               = new FlowLayoutPanel();
            Clh_pnlClashChoices.Dock          = DockStyle.Bottom;
            Clh_pnlClashChoices.FlowDirection = FlowDirection.RightToLeft;
            Clh_pnlClashChoices.AutoSize      = true;
            Clh_pnlClashChoices.WrapContents  = false;

            /* The three choices, added from the right, so the row reads left   */
            /* to right Keep the Folder's File, Keep the Manifest, Cancel.      */
            /* None is the default button, as in the conflict question.  Each   */
            /* has a click handler of its own, which records its choice.        */
            Clh_AddClashChoice("Cancel", Clh_OnClashCancelClicked);
            Clh_AddClashChoice("Keep the Manifest", Clh_OnKeepManifestClicked);
            Clh_AddClashChoice("Keep the Folder's File", Clh_OnKeepFileClicked);

            /* The question, filling the space above the buttons and wrapped   */
            /* to its width: each path on a line of its own, and each answer   */
            /* explained on its own line, so the choice is made knowing what   */
            /* each one does.                                                  */
            Clh_lblClashQuestion          = new Label();
            Clh_lblClashQuestion.Dock     = DockStyle.Fill;
            Clh_lblClashQuestion.AutoSize = false;
            Clh_lblClashQuestion.Text     = "The folder being extracted holds an entry with the metadata manifest's own name:\n\n" +
                                            sImageEntry + "\n\n" +
                                            "Keep the Folder's File extracts it, and no manifest is written.\n" +
                                            "Keep the Manifest writes the manifest, and leaves that entry out.\n\n" +
                                            "The manifest would be written to:\n" + sManifest;

            /* The question first and the row last: the control added last is   */
            /* docked first, so the row takes the bottom edge and the           */
            /* Fill-docked question gets what is left.                          */
            this.Controls.Add(Clh_lblClashQuestion);
            this.Controls.Add(Clh_pnlClashChoices);

            /* The width is the design width, or the button row's if wider at   */
            /* this font; the height is a start that Clh_FitClashHeight then    */
            /* fits to the wrapped question plus the row.  The resize handler   */
            /* is hooked first, so a later width change refits it too.          */
            this.ClientSize = new Size(Math.Max(CLH_WIDTH, Clh_pnlClashChoices.PreferredSize.Width + (2 * CLH_PAD)),
                                       CLH_WIDTH / 2);
            this.Resize    += Clh_OnClashResize;
            Clh_FitClashHeight();
        }

        /*--------------------------------------------------------------------*/
        /* CLH_ReadChoice:                                                    */
        /*                                                                    */
        /* Which button was pressed, for the question presenter to read once  */
        /* ShowDialog has returned (CommandQuestionPresenter.cs, Qpr_Clash),  */
        /* which hands it to the worker as the answer the engine is given     */
        /* (CommandNativeControl.cs).  Reading it changes nothing.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : CLH_KEEP_FILE or CLH_KEEP_MANIFEST; CLH_CANCEL when the  */
        /*           dialog was closed without a button, since the            */
        /*           constructor starts it there.                             */
        /*--------------------------------------------------------------------*/
        internal int CLH_ReadChoice()
        {
            /* The choice the last button pressed recorded, or the   */
            /* constructor's Cancel when none was.                   */
            return(Clh_iChoice);
        }

        /*--------------------------------------------------------------------*/
        /* Clh_AddClashChoice:                                                */
        /*                                                                    */
        /* Add one button to the row, with the click handler of its own that  */
        /* records the choice it stands for.                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sText      : the button's caption.                             */
        /*     pfnClicked : that button's own handler (one per choice).       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the button is in the row.                               */
        /*--------------------------------------------------------------------*/
        private void Clh_AddClashChoice(string sText, EventHandler pfnClicked)
        {
            Button btnChoice;

            /* A button sized to its caption at the dialog's font, hooked to   */
            /* the handler given for it.                                       */
            btnChoice          = new Button();
            btnChoice.Text     = sText;
            btnChoice.AutoSize = true;
            btnChoice.Click   += pfnClicked;

            /* Into the row, which places it left of the ones added before. */
            Clh_pnlClashChoices.Controls.Add(btnChoice);
        }

        /*--------------------------------------------------------------------*/
        /* Clh_FitClashHeight:                                                */
        /*                                                                    */
        /* Size the dialog's height to its content at its present width, so   */
        /* the whole question shows above the buttons, however long the       */
        /* paths.                                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the client height fits the text.                        */
        /*--------------------------------------------------------------------*/
        private void Clh_FitClashHeight()
        {
            Size sizText;
            int  iTextWidth;

            /* The question label's real width: the client width less the   */
            /* pad at each side.                                            */
            iTextWidth = this.ClientSize.Width - (2 * CLH_PAD);

            if (iTextWidth <= 0)
            {   /* No room to measure against: the height is left as it is. */
                return;
            }

            /* The question's height wrapped at that width, in the label's    */
            /* own font, with the word-wrapping flags the conflict question   */
            /* measures with.                                                 */
            sizText = TextRenderer.MeasureText(Clh_lblClashQuestion.Text, Clh_lblClashQuestion.Font,
                                               new Size(iTextWidth, int.MaxValue),
                                               TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);

            /* The width kept; the height the text, the button row and three   */
            /* pads -- above and below the content, and the gap between the    */
            /* question and the buttons.                                       */
            this.ClientSize = new Size(this.ClientSize.Width,
                                       sizText.Height + Clh_pnlClashChoices.PreferredSize.Height + (3 * CLH_PAD));
        }

        /*--------------------------------------------------------------------*/
        /* Clh_OnClashCancelClicked:                                          */
        /*                                                                    */
        /* Cancel: the extract stops before anything is made                  */
        /* (Clh_ChooseClash).                                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Clh_iChoice is set and the dialog closes.               */
        /*--------------------------------------------------------------------*/
        private void Clh_OnClashCancelClicked(object objSender, EventArgs evArgs)
        {
            /* Stop before anything is made. */
            Clh_ChooseClash(CLH_CANCEL);
        }

        /*--------------------------------------------------------------------*/
        /* Clh_OnKeepManifestClicked:                                         */
        /*                                                                    */
        /* Keep the Manifest: the manifest is written and the image's entry   */
        /* of its name left out (Clh_ChooseClash).                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Clh_iChoice is set and the dialog closes.               */
        /*--------------------------------------------------------------------*/
        private void Clh_OnKeepManifestClicked(object objSender, EventArgs evArgs)
        {
            /* The manifest kept, the entry left out. */
            Clh_ChooseClash(CLH_KEEP_MANIFEST);
        }

        /*--------------------------------------------------------------------*/
        /* Clh_OnKeepFileClicked:                                             */
        /*                                                                    */
        /* Keep the Folder's File: the image's entry is extracted and no      */
        /* manifest written (Clh_ChooseClash).                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Clh_iChoice is set and the dialog closes.               */
        /*--------------------------------------------------------------------*/
        private void Clh_OnKeepFileClicked(object objSender, EventArgs evArgs)
        {
            /* The entry kept, no manifest. */
            Clh_ChooseClash(CLH_KEEP_FILE);
        }

        /*--------------------------------------------------------------------*/
        /* Clh_ChooseClash:                                                   */
        /*                                                                    */
        /* A button was pressed: record what it stands for and close.  The    */
        /* waiting copy reads the choice once ShowDialog returns.  Each of    */
        /* the three buttons' handlers calls it with its own choice.          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iChoice : the CLH_* value the pressed button stands for.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Clh_iChoice is set and the dialog closes.               */
        /*--------------------------------------------------------------------*/
        private void Clh_ChooseClash(int iChoice)
        {
            /* The choice, recorded before the close. */
            Clh_iChoice = iChoice;
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Clh_OnClashResize:                                                 */
        /*                                                                    */
        /* The dialog's width changed: the question wraps differently, so     */
        /* refit the height.  Guarded against the resize it causes itself.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the dialog.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the height fits the text at the new width.              */
        /*--------------------------------------------------------------------*/
        private void Clh_OnClashResize(object objSender, EventArgs evArgs)
        {
            /* Detach while refitting, since setting ClientSize raises Resize. */
            this.Resize -= Clh_OnClashResize;
            Clh_FitClashHeight();
            this.Resize += Clh_OnClashResize;
        }
    }
}
