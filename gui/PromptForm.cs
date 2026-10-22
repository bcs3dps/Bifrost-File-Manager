/*----------------------------------------------------------------------------*/
/* PromptForm.cs                                                              */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* A small dialog that asks for one line of text: a new Windows folder's or a */
/* new file's name, a Windows rename, a new hex file's size (MainForm.cs);    */
/* the name the worker asks for -- a new folder or a rename in the image --   */
/* which the question presenter shows and keeps what is typed as a draft      */
/* (CommandQuestionPresenter.cs, PRM_BindDraft); the text editor's Find; and  */
/* the hex editor's Go To offset.  Those callers show it modal (ShowDialog),  */
/* because the answer is needed before the action it belongs to can proceed   */
/* and it is over in seconds -- all but the two editors, which show it        */
/* modeless over the editor, the editor disabled meanwhile, so only that      */
/* editor waits (placed by PRM_CentreOver, which their Colors sibling in the  */
/* structured editor uses too).                                               */
/*                                                                            */
/* The outcome is carried in two fields the caller reads once the dialog has  */
/* closed -- after ShowDialog returns, or once a modeless one's FormClosed    */
/* has been told: PRM_bAccepted (OK was pressed) and PRM_sText (what was      */
/* typed).  The form's DialogResult is not used.                              */
/*                                                                            */
/* Its text is in the program's dialog font, which the caller passes in, the  */
/* same font the message box is given, since it is a dialog box of the same   */
/* kind.  The dialog decides no font of its own.                              */
/*                                                                            */
/* The layout is docking and one flow panel; the Resize handler keeps the     */
/* dialog's height equal to what its content needs at the current font, the   */
/* prompt line's own height measured each time in the font it is drawn in.    */
/*----------------------------------------------------------------------------*/

/* The framework: the base types (event arguments, Math), the font, sizes,   */
/* points, rectangles and text alignment, and the dialog, its controls and   */
/* the screens it is placed on.                                              */
using System;
using System.Drawing;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* PRM_DraftChanged:                                                      */
    /*                                                                        */
    /* Told the answer box's text every time it changes, before OK is         */
    /* pressed: the question presenter keeps it as the question's draft       */
    /* (QDU_OnNameChanged), so a question that an Exit hides for a moment     */
    /* comes back with what had been typed.  Typing is never an answer.       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText : the box's text as it now is, unfinished or empty.          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the draft is kept; nothing is answered.                     */
    /*------------------------------------------------------------------------*/
    internal delegate void PRM_DraftChanged(string sText);

    /*------------------------------------------------------------------------*/
    /* PRM_clsPromptForm -- the one-line text question (the file header says  */
    /* where it is used).  A plain Form: its base class is declared here with */
    /* the rest of its UI.                                                    */
    /*                                                                        */
    /* Derives from Form because the framework requires it: WinForms makes a  */
    /* window only as a Form subclass.  That is the one kind of inheritance   */
    /* allowed here -- from a framework base class, never from a class of     */
    /* ours.                                                                  */
    /*------------------------------------------------------------------------*/
    internal sealed class PRM_clsPromptForm : Form
    {
        /* The dialog's geometry -- documentation table.  The width is fixed   */
        /* and the height follows from the content (Prm_FitHeight).            */
        private const int PRM_WIDTH = 460;     // client width, pixels
        private const int PRM_PAD   = 12;      // padding round the content, pixels

        /* The outcome, the two fields the caller reads once the dialog   */
        /* has closed (after ShowDialog returns, or at a modeless one's   */
        /* FormClosed) -- documentation table.                            */
        internal bool   PRM_bAccepted;         // true when OK was pressed; false for Cancel or a closed dialog
        internal string PRM_sText;             // the text typed, valid when PRM_bAccepted; "" otherwise

        /* Who keeps the draft -- documentation table. */
        private PRM_DraftChanged Prm_pfnDraftChanged; // told the text as it changes (PRM_BindDraft); null for the callers that keep no draft

        /* The dialog's controls -- documentation table. */
        private Label           Prm_lblPrompt;     // what is being asked for
        private TextBox         Prm_txtAnswer;     // where the answer is typed
        private FlowLayoutPanel Prm_pnlButtons;    // OK and Cancel, right-aligned
        private Button          Prm_btnOk;         // accepts the text
        private Button          Prm_btnCancel;     // abandons the action

        /*--------------------------------------------------------------------*/
        /* PRM_clsPromptForm (constructor):                                   */
        /*                                                                    */
        /* Build the dialog.  Enter presses OK and Escape presses Cancel, as  */
        /* any Windows prompt behaves; both buttons still go through their    */
        /* own click handlers, which is where the outcome is recorded.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     fntDialog : the program's dialog font (the caller's; not       */
        /*                 freed here).                                       */
        /*     sTitle    : the caption.                                       */
        /*     sPrompt   : the line above the text box.                       */
        /*     sInitial  : the text the box starts with (selected, so typing  */
        /*                 replaces it).                                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the dialog, not yet shown.                     */
        /*--------------------------------------------------------------------*/
        internal PRM_clsPromptForm(Font fntDialog, string sTitle, string sPrompt, string sInitial)
        {
            /* Not accepted, and no text, until OK says otherwise: closing   */
            /* the dialog any other way (Escape, Cancel, the title bar's     */
            /* close) leaves both as they are here, so the caller cannot     */
            /* mistake a closed dialog for an answer.                        */
            PRM_bAccepted = false;
            PRM_sText     = "";

            /* A fixed dialog with the caller's caption, centred on its     */
            /* owner, not in the taskbar, with no maximise or minimise: a   */
            /* question to answer, not a window to keep.  The padding is    */
            /* the space round the content, which Prm_FitHeight counts.     */
            this.Text            = sTitle;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox     = false;
            this.MinimizeBox     = false;
            this.ShowInTaskbar   = false;
            this.StartPosition   = FormStartPosition.CenterParent;
            this.Padding         = new Padding(PRM_PAD);

            /* The caller's dialog font.  Each control takes it on when it is   */
            /* added to the dialog below, not when it is made (the              */
            /* framework's ambient font, as found) -- so nothing is measured    */
            /* here before then: the one size this dialog measures itself,      */
            /* the prompt line's height, is taken by Prm_FitHeight once every   */
            /* control has been added.                                          */
            this.Font = fntDialog;

            /* The buttons, each sizing itself to its text (the font may be   */
            /* large) and recording its outcome in its own click handler.     */
            /* The row fills right to left, so Cancel, made and added         */
            /* first, ends up rightmost.                                      */
            Prm_btnCancel          = new Button();
            Prm_btnCancel.Text     = "Cancel";
            Prm_btnCancel.AutoSize = true;
            Prm_btnCancel.Click   += Prm_OnCancelClicked;

            /* OK, to Cancel's left. */
            Prm_btnOk          = new Button();
            Prm_btnOk.Text     = "OK";
            Prm_btnOk.AutoSize = true;
            Prm_btnOk.Click   += Prm_OnOkClicked;

            /* The row that holds them: docked along the bottom, filling     */
            /* from the right, as tall as the buttons, with a pad above to   */
            /* part it from the text box.                                    */
            Prm_pnlButtons               = new FlowLayoutPanel();
            Prm_pnlButtons.Dock          = DockStyle.Bottom;
            Prm_pnlButtons.FlowDirection = FlowDirection.RightToLeft;
            Prm_pnlButtons.AutoSize      = true;
            Prm_pnlButtons.Padding       = new Padding(0, PRM_PAD, 0, 0);
            Prm_pnlButtons.Controls.Add(Prm_btnCancel);
            Prm_pnlButtons.Controls.Add(Prm_btnOk);

            /* The text box, under the prompt, holding the caller's starting   */
            /* text all selected -- typing replaces it at once, while an       */
            /* arrow key keeps it for editing.  An empty answer is not         */
            /* refused here: what an empty name means is the caller's to       */
            /* decide.                                                         */
            Prm_txtAnswer      = new TextBox();
            Prm_txtAnswer.Dock = DockStyle.Top;
            Prm_txtAnswer.Text = sInitial;
            Prm_txtAnswer.SelectAll();

            /* Every change of the text reported as the draft (when a       */
            /* draft keeper is set), so a worker's question an Exit hides   */
            /* comes back as it was; it is never an answer.  Hooked AFTER   */
            /* the starting text is set, so that text is not reported.      */
            Prm_txtAnswer.TextChanged += Prm_OnDraftChanged;

            /* The prompt line, docked at the top, its text centred in its    */
            /* height.  AutoSize is off, so its height is whatever is set,    */
            /* and none is set here: until the label is added to the dialog   */
            /* it has the default font, not the dialog's, and a height read   */
            /* now would be the default font's -- too short at a larger       */
            /* dialog font, the prompt clipped.  Prm_FitHeight sets it once   */
            /* the label has its parent, and so the dialog's font.            */
            Prm_lblPrompt           = new Label();
            Prm_lblPrompt.Dock      = DockStyle.Top;
            Prm_lblPrompt.AutoSize  = false;
            Prm_lblPrompt.Text      = sPrompt;
            Prm_lblPrompt.TextAlign = ContentAlignment.MiddleLeft;

            /* Docking runs in reverse order of adding (the framework's     */
            /* layout order, as found): the button row takes the bottom     */
            /* first, then the prompt the top, then the text box the top    */
            /* below it.  Added first, the text box is                      */
            /* also first in the tab order, and so has the focus when the   */
            /* dialog opens (likewise as found).                            */
            this.Controls.Add(Prm_txtAnswer);
            this.Controls.Add(Prm_lblPrompt);
            this.Controls.Add(Prm_pnlButtons);

            /* Enter presses OK and Escape presses Cancel, each through its   */
            /* click handler, so the outcome is recorded the same way.        */
            this.AcceptButton = Prm_btnOk;
            this.CancelButton = Prm_btnCancel;

            /* The width fixed at PRM_WIDTH (a third of it as a starting       */
            /* height, replaced at once); the resize handler attached BEFORE   */
            /* the fit, so the fit's own resize already runs through it; and   */
            /* the prompt line's height and the dialog's fitted to the         */
            /* content -- now, with every control added and in the dialog's    */
            /* font.                                                           */
            this.ClientSize = new Size(PRM_WIDTH, PRM_WIDTH / 3);
            this.Resize    += Prm_OnResize;
            Prm_FitHeight();
        }

        /*--------------------------------------------------------------------*/
        /* PRM_BindDraft:                                                     */
        /*                                                                    */
        /* Set who keeps the draft -- the question presenter, for a name the  */
        /* worker asks for (CommandQuestionPresenter.cs).  The other callers  */
        /* set none, and their dialog then reports nothing as it is typed.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pfnChanged : told the text as it changes; null for none.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : later changes are reported; setting it reports nothing. */
        /*--------------------------------------------------------------------*/
        internal void PRM_BindDraft(PRM_DraftChanged pfnChanged)
        {
            /* Kept, and the starting text NOT reported now: the presenter   */
            /* passed its own draft in as that text, so reporting it back    */
            /* would only repeat it.                                         */
            Prm_pfnDraftChanged = pfnChanged;
        }

        /*--------------------------------------------------------------------*/
        /* PRM_CentreOver:                                                    */
        /*                                                                    */
        /* Place a window that is about to be shown MODELESS over the middle  */
        /* of the window it belongs to, kept inside that screen's working     */
        /* area -- where CenterParent puts a dialog shown with ShowDialog.    */
        /* CenterParent applies only to ShowDialog: a window shown with       */
        /* Show(owner) opens wherever Windows places a new window (the        */
        /* screen's top-left, as found).  Used for the editors' own windows   */
        /* over them: this dialog for the text editor's Find and the hex      */
        /* editor's Go To, the structured editor's Colors dialog, each        */
        /* editor's close question, and the structured editor's wait box on   */
        /* closing a large tree.  The window's size must be final when this   */
        /* is called -- each caller sets it while the window is built.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmPopup : the window to place, not yet shown.                 */
        /*     frmOwner : the window it is centred over.                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : frmPopup is set to be placed by hand, at that spot.     */
        /*--------------------------------------------------------------------*/
        internal static void PRM_CentreOver(Form frmPopup, Form frmOwner)
        {
            Rectangle rcArea;
            int iLeft;
            int iTop;

            /* The spot that puts the window's centre on the owner's centre. */
            iLeft = frmOwner.Left + (frmOwner.Width - frmPopup.Width) / 2;
            iTop  = frmOwner.Top + (frmOwner.Height - frmPopup.Height) / 2;

            /* Kept inside the working area of the screen the owner is on    */
            /* (the taskbar left out), as CenterParent keeps a dialog: the   */
            /* right and bottom edges first, then the left and top, so a     */
            /* window larger than the area is held at its top-left corner    */
            /* and its title bar stays where it can be reached.              */
            rcArea = Screen.FromControl(frmOwner).WorkingArea;
            iLeft  = Math.Min(iLeft, rcArea.Right - frmPopup.Width);
            iTop   = Math.Min(iTop, rcArea.Bottom - frmPopup.Height);
            iLeft  = Math.Max(iLeft, rcArea.Left);
            iTop   = Math.Max(iTop, rcArea.Top);

            /* Placed by hand: the framework uses Location for a new window   */
            /* only when its StartPosition is Manual.                         */
            frmPopup.StartPosition = FormStartPosition.Manual;
            frmPopup.Location      = new Point(iLeft, iTop);
        }

        /*--------------------------------------------------------------------*/
        /* Prm_OnDraftChanged:                                                */
        /*                                                                    */
        /* The answer box's text changed: tell the draft keeper, if there is  */
        /* one.  Nothing is accepted by it.                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the box (unused).                                  */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the draft keeper has the new text.                      */
        /*--------------------------------------------------------------------*/
        private void Prm_OnDraftChanged(object objSender, EventArgs evArgs)
        {
            if (Prm_pfnDraftChanged != null)
            {   /* A keeper is set: the text as it now is, empty included. */
                Prm_pfnDraftChanged(Prm_txtAnswer.Text);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Prm_FitHeight:                                                     */
        /*                                                                    */
        /* Make the dialog as tall as its three rows need, the prompt line    */
        /* first sized to its text in the font it is drawn in.  The docking   */
        /* places the rows; the heights are the one fact it does not supply.  */
        /* Called only once the controls are in the dialog, so the prompt     */
        /* line has the dialog's font by then.                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the prompt line's height and the client height fit the  */
        /*            content.                                                */
        /*--------------------------------------------------------------------*/
        private void Prm_FitHeight()
        {
            /* The prompt line: one line of its text in its own font -- the   */
            /* dialog's, inherited now that it has a parent -- plus a pad,    */
            /* which leaves space between it and the box below.  Measured     */
            /* here at every fit rather than once, so the line follows the    */
            /* font it is drawn in, whatever that is when the dialog fits.    */
            Prm_lblPrompt.Height = Prm_lblPrompt.PreferredHeight + PRM_PAD;

            /* The width kept; the height the sum of the three rows -- the   */
            /* prompt, the text box, and the button row at its               */
            /* PreferredSize, taken to include the row's own top pad (the    */
            /* framework's behaviour, not established here; OpenScan.cs's    */
            /* dialog counts that pad separately, so one of the two is a     */
            /* pad off, which only changes the spare space) -- plus the      */
            /* dialog's own pads above and below.  Setting it raises         */
            /* Resize, which Prm_OnResize guards against re-entering.        */
            this.ClientSize = new Size(this.ClientSize.Width,
                                       Prm_lblPrompt.Height + Prm_txtAnswer.Height +
                                       Prm_pnlButtons.PreferredSize.Height + (2 * PRM_PAD));
        }

        /*--------------------------------------------------------------------*/
        /* Prm_OnOkClicked:                                                   */
        /*                                                                    */
        /* OK: keep what was typed and close.                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the outcome fields are set and the dialog closes.       */
        /*--------------------------------------------------------------------*/
        private void Prm_OnOkClicked(object objSender, EventArgs evArgs)
        {
            /* The text as typed (not trimmed: the caller decides what a    */
            /* leading or trailing space means), then the flag, both set    */
            /* BEFORE the close, since the caller reads them as soon as     */
            /* ShowDialog returns, or once a modeless dialog's FormClosed   */
            /* is told.  A dialog shown with ShowDialog is hidden by        */
            /* Close, not disposed (as found); a modeless one is disposed   */
            /* by it, and its plain fields stay readable all the same.      */
            PRM_sText     = Prm_txtAnswer.Text;
            PRM_bAccepted = true;
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Prm_OnCancelClicked:                                               */
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
        private void Prm_OnCancelClicked(object objSender, EventArgs evArgs)
        {
            /* Closed with the outcome left at the "not accepted" the     */
            /* constructor set -- what Escape and the title bar's close   */
            /* leave too, so every way out but OK means the same thing.   */
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Prm_OnResize:                                                      */
        /*                                                                    */
        /* The dialog's size changed: refit its height.                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the dialog.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the height fits the content.                            */
        /*--------------------------------------------------------------------*/
        private void Prm_OnResize(object objSender, EventArgs evArgs)
        {
            /* Detached, refitted, attached again: the refit sets the       */
            /* height, which raises Resize, and with the handler attached   */
            /* that would call straight back in.  (OpenScan.cs's dialog     */
            /* stops the same loop by comparing the height instead.)        */
            this.Resize -= Prm_OnResize;
            Prm_FitHeight();
            this.Resize += Prm_OnResize;
        }
    }
}
