/*----------------------------------------------------------------------------*/
/* MessageForm.cs                                                             */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The program's own message box, used everywhere MessageBox.Show would be:   */
/* an icon, a text, and one row of buttons, in a modal dialog.  It exists     */
/* for one reason: the text of a dialog box is to be larger than the system   */
/* message box shows it (one or two points at least), and MessageBox.Show     */
/* takes no font.  So this is a near copy of the Windows box -- the same      */
/* button sets, the same icons and sounds, the same default and cancel        */
/* buttons, Ctrl+C copying the text -- in the font the CALLER passes: the     */
/* program's dialog font, which the program makes once at start-up            */
/* (Program.cs) and hands to every window that shows a dialog, at or before   */
/* its creation.  This file decides no font size of its own.                  */
/*                                                                            */
/* MSG_Show is the entry point, taking the same arguments MessageBox.Show     */
/* does (owner, text, caption, buttons, icon, default button) plus the font,  */
/* and returning the DialogResult VALUE of the button pressed -- the value    */
/* only: the form's own DialogResult property is not used (the reason given   */
/* with it is that the property behaves differently between modal and         */
/* modeless forms -- not established here), and the answer is recorded by     */
/* the button handlers in a field of this class.  MSG_ShowWarned              */
/* is the same box opening with a WARNING HEADING: one line above the         */
/* message, in a second, larger font the caller also passes, in red -- for a  */
/* question that must not be answered without reading it (the first write     */
/* to an image).  The Windows box has nothing like it; it is the one thing    */
/* here that is not a copy.                                                   */
/*                                                                            */
/* Nothing is given a position by hand: the text is measured and wrapped at   */
/* a width, the buttons are sized from their captions, and the window is      */
/* sized to hold it all; the layout runs again on every resize.               */
/*                                                                            */
/* A box shown while a worker's question is up attaches itself to that        */
/* question's windows (CommandQuestionPresenter.cs, QWN_Attach), so an Exit   */
/* can close the box alone, leaving the editor or window behind it open.      */
/*----------------------------------------------------------------------------*/

/* The framework: the base types (Math, event arguments, the exceptions   */
/* caught), fonts, sizes, colours and the system icons, and the box and   */
/* its controls.  The system sounds are named in full where played.       */
using System;
using System.Drawing;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* MSG_clsMessageForm -- the program's message box (the file header       */
    /* describes it), with its one Framework override, OnKeyDown.             */
    /*                                                                        */
    /* Derives from Form because the framework requires it: WinForms makes a  */
    /* window only as a Form subclass.  That is the one kind of inheritance   */
    /* allowed here -- from a framework base class, never from a class of     */
    /* ours.                                                                  */
    /*------------------------------------------------------------------------*/
    internal sealed class MSG_clsMessageForm : Form
    {
        /* The box's design sizes, at 96 dpi -- documentation table. */
        private  const int   MSG_PAD              = 12;      // padding round the content, and between the icon and the text, pixels at 96 dpi
        private  const int   MSG_GAP               = 8;      // between the buttons, and between a warning heading and the message, pixels at 96 dpi
        private  const int   MSG_TEXT_MAX_WIDTH    = 520;    // the text wraps at this width, pixels at 96 dpi (about what the Windows box allows)
        private  const int   MSG_TEXT_SLACK        = 2;      // added to the measured text size so the last glyph is never clipped, pixels
        private  const int   MSG_ICON_SIDE         = 32;     // the icon's side, pixels at 96 dpi (the size the system icons are drawn at)
        private  const int   MSG_BUTTON_MIN_WIDTH  = 88;     // a button is at least this wide, pixels at 96 dpi
        private  const int   MSG_BUTTON_PAD        = 24;     // added to a caption's measured width for the button's margins, pixels at 96 dpi
        private  const int   MSG_BUTTON_HEIGHT_PAD = 10;     // added to the font's height for the button's height, pixels at 96 dpi
        private  const float MSG_DESIGN_DPI        = 96f;    // the dpi the sizes above are designed for
        private  const int   MSG_DEFAULT_STEP      = 0x100;  // MessageBoxDefaultButton.Button2 is 0x100, Button3 is 0x200: the index is the value over this

        /* The warning heading's colour, the same for every box --   */
        /* documentation table.                                      */
        private static readonly Color MSG_HEADING_COLOR = Color.FromArgb(0xC0, 0x00, 0x00);  // the warning heading's colour: a deep red, dark enough to read on the dialog's light grey (pure red is harsh there); a colour, not a font, so it is this box's to decide

        /* The box's controls, its answer and its sizes on this screen --   */
        /* documentation table.                                             */
        private DialogResult    Msg_enmAnswer;         // the value of the button pressed; Cancel until one is (the close box counts as Cancel)
        private PictureBox      Msg_picIcon;           // the system icon, or null for MessageBoxIcon.None
        private Label           Msg_lblHeading;        // the warning heading above the message, in the heading font and red; null when the box has none
        private Label           Msg_lblText;           // the message, wrapped
        private Button[]        Msg_abtnRow;           // the buttons, left to right; each one's Tag is its DialogResult value
        private int             Msg_iPad;              // MSG_PAD on this screen, pixels
        private int             Msg_iGap;              // MSG_GAP on this screen, pixels
        private int             Msg_iIconSide;         // MSG_ICON_SIDE on this screen, pixels
        private int             Msg_iTextMaxWidth;     // MSG_TEXT_MAX_WIDTH on this screen, pixels
        private int             Msg_iButtonMin;        // MSG_BUTTON_MIN_WIDTH on this screen, pixels
        private int             Msg_iButtonPad;        // MSG_BUTTON_PAD on this screen, pixels
        private int             Msg_iButtonHeightPad;  // MSG_BUTTON_HEIGHT_PAD on this screen, pixels
        private bool            Msg_bLaying;           // true while Msg_Layout runs: setting the client size raises Resize, which would call it again

        /*-----------------------------------------------------------------------*/
        /* MSG_Show:                                                             */
        /*                                                                       */
        /* Show a message and wait for the answer -- the replacement for         */
        /* MessageBox.Show, with its arguments.  The sound Windows plays for     */
        /* the icon is played, the box is shown modally over the owner (or       */
        /* centred on the screen when there is none), and the value of the       */
        /* button pressed comes back.                                            */
        /*                                                                       */
        /* Arguments:                                                            */
        /*     frmOwner         : the window the box is modal to and centred on; */
        /*                     null for none.                                    */
        /*     fntDialog     : the program's dialog font, which the box's        */
        /*                     text and buttons are shown and measured in;       */
        /*                     the caller owns it (the box does not free it).    */
        /*     sText         : the message; line breaks are kept, long lines     */
        /*                     wrap.                                             */
        /*     sCaption      : the title.                                        */
        /*     enmButtons       : which row of buttons.                          */
        /*     enmIcon          : which system icon, or None.                    */
        /*     enmDefaultButton : which button Enter presses (Button1 to 3).     */
        /*                                                                       */
        /* Returns:                                                              */
        /*     DialogResult : the value of the button pressed; Cancel when       */
        /*                    the box was closed without one.                    */
        /*-----------------------------------------------------------------------*/
        internal static DialogResult MSG_Show(Form frmOwner, Font fntDialog, string sText, string sCaption,
                                              MessageBoxButtons enmButtons, MessageBoxIcon enmIcon,
                                              MessageBoxDefaultButton enmDefaultButton)
        {
            /* The same box with no heading: no heading font, no heading text. */
            return(MSG_ShowWarned(frmOwner, fntDialog, null, "", sText, sCaption, enmButtons, enmIcon, enmDefaultButton));
        }

        /*-----------------------------------------------------------------------*/
        /* MSG_ShowWarned:                                                       */
        /*                                                                       */
        /* MSG_Show with a WARNING HEADING: the box opens with sHeading on a     */
        /* line of its own above the message, in fntHeading and red, so the      */
        /* risk is the first thing read and cannot be mistaken for one more      */
        /* sentence of the message.  Everything else -- the sound, the           */
        /* buttons, the default and cancel buttons, the placement, the           */
        /* answer -- is MSG_Show's.  An empty sHeading (or a null font) shows    */
        /* no heading at all, which is how MSG_Show calls it.                    */
        /*                                                                       */
        /* Arguments:                                                            */
        /*     frmOwner         : the window the box is modal to and centred on; */
        /*                     null for none.                                    */
        /*     fntDialog     : the program's dialog font, for the message and    */
        /*                     the buttons; the caller owns it.                  */
        /*     fntHeading    : the program's warning-heading font, for the       */
        /*                     heading only; the caller owns it.  May be null    */
        /*                     when sHeading is empty.                           */
        /*     sHeading      : the heading, one short line (it wraps if it       */
        /*                     must); "" for none.                               */
        /*     sText         : the message; line breaks are kept, long lines     */
        /*                     wrap.                                             */
        /*     sCaption      : the title.                                        */
        /*     enmButtons       : which row of buttons.                          */
        /*     enmIcon          : which system icon, or None.                    */
        /*     enmDefaultButton : which button Enter presses (Button1 to 3).     */
        /*                                                                       */
        /* Returns:                                                              */
        /*     DialogResult : the value of the button pressed; Cancel when       */
        /*                    the box was closed without one.                    */
        /*-----------------------------------------------------------------------*/
        internal static DialogResult MSG_ShowWarned(Form frmOwner, Font fntDialog, Font fntHeading, string sHeading,
                                                    string sText, string sCaption, MessageBoxButtons enmButtons,
                                                    MessageBoxIcon enmIcon, MessageBoxDefaultButton enmDefaultButton)
        {
            DialogResult enmAnswer;
            MSG_clsMessageForm frmBox;

            using (frmBox = new MSG_clsMessageForm(fntDialog, fntHeading, sHeading, sText, sCaption, enmButtons, enmIcon,
                                                                enmDefaultButton, frmOwner != null))
            {   /* The box lives for the one question, and is disposed when   */
                /* it has been answered.                                      */

                /* Attached to the worker's question windows                  */
                /* (CommandQuestionPresenter.cs), so an Exit can close this   */
                /* box alone -- not the editor or window that asked.          */
                QWN_clsQuestionWindows.QWN_Attach(frmBox);

                /* The icon's sound, as the Windows box sounds as it opens. */
                Msg_PlaySound(enmIcon);

                if (frmOwner != null)
                {   /* Modal to the owner and centred on it. */
                    frmBox.ShowDialog(frmOwner);
                }
                else
                {   /* No owner: centred on the screen. */
                    frmBox.ShowDialog();
                }

                /* The answer the button handler recorded -- read before the   */
                /* using block disposes the box.                               */
                enmAnswer = frmBox.Msg_enmAnswer;
            }

            /* What was pressed. */
            return(enmAnswer);
        }

        /*--------------------------------------------------------------------*/
        /* MSG_clsMessageForm (constructor):                                  */
        /*                                                                    */
        /* Build the box for one question, laid out and ready to show, with   */
        /* the keys and the close box behaving as the Windows box's do.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     fntDialog     : the program's dialog font (not owned here).    */
        /*     fntHeading    : the warning-heading font (not owned here), or  */
        /*                     null when there is no heading.                 */
        /*     sHeading      : the warning heading, or "" for none.           */
        /*     sText         : the message.                                   */
        /*     sCaption      : the title.                                     */
        /*     enmButtons       : which row of buttons.                       */
        /*     enmIcon          : which system icon, or None.                 */
        /*     enmDefaultButton : which button Enter presses.                 */
        /*     bHasOwner     : true to centre on the owner, false on the      */
        /*                     screen.                                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the box, not yet shown.                        */
        /*--------------------------------------------------------------------*/
        private MSG_clsMessageForm(Font fntDialog, Font fntHeading, string sHeading, string sText, string sCaption,
                                MessageBoxButtons enmButtons, MessageBoxIcon enmIcon, MessageBoxDefaultButton enmDefaultButton,
                                bool bHasOwner)
        {
            Graphics gfx;
            Icon     icoSystem;
            float    fScale;
            int      iDefault;
            int      i;

            /* Cancel until a button says otherwise, so a box closed with   */
            /* no button pressed answers Cancel; and not laying out yet.    */
            Msg_enmAnswer  = DialogResult.Cancel;
            Msg_bLaying = false;

            /* Shaped like Windows' own message box: a fixed frame, no         */
            /* maximise or minimise, off the taskbar, and NO caption icon --   */
            /* Windows' message boxes have none, and without this one the      */
            /* framework's generic icon would show.                            */
            this.Text            = sCaption;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox     = false;
            this.MinimizeBox     = false;
            this.ShowInTaskbar   = false;
            this.ShowIcon        = false;

            /* Keys reach OnKeyDown before the focused button (as found),     */
            /* so Ctrl+C copies the message whichever button has the focus.   */
            this.KeyPreview      = true;

            if (bHasOwner)
            {   /* Over the window that asked. */
                this.StartPosition = FormStartPosition.CenterParent;
            }
            else
            {   /* Nothing to centre on but the screen. */
                this.StartPosition = FormStartPosition.CenterScreen;
            }

            /* The caller's dialog font for everything in the box, set before   */
            /* anything is measured; the icon's bitmap is released with it.     */
            this.Font      = fntDialog;
            this.Disposed += Msg_OnDisposed;

            /* The design sizes, scaled to this screen's dpi so they keep   */
            /* their size against the text whatever the display scaling.    */
            gfx = this.CreateGraphics();

            using (gfx)
            {   /* Read the dpi, then release the surface. */
                fScale = gfx.DpiX / MSG_DESIGN_DPI;
            }

            /* Every design size times that scale, rounded to whole pixels   */
            /* once here, so the layout works in this screen's pixels.       */
            Msg_iPad             = (int)Math.Round(MSG_PAD * fScale);
            Msg_iGap             = (int)Math.Round(MSG_GAP * fScale);
            Msg_iIconSide        = (int)Math.Round(MSG_ICON_SIDE * fScale);
            Msg_iTextMaxWidth    = (int)Math.Round(MSG_TEXT_MAX_WIDTH * fScale);
            Msg_iButtonMin       = (int)Math.Round(MSG_BUTTON_MIN_WIDTH * fScale);
            Msg_iButtonPad       = (int)Math.Round(MSG_BUTTON_PAD * fScale);
            Msg_iButtonHeightPad = (int)Math.Round(MSG_BUTTON_HEIGHT_PAD * fScale);

            /* The icon, when there is one: the system's own, as a picture. */
            icoSystem   = Msg_SystemIcon(enmIcon);
            Msg_picIcon = null;

            if (icoSystem != null)
            {   /* This box has an icon. */
                Msg_picIcon          = new PictureBox();
                Msg_picIcon.Size     = new Size(Msg_iIconSide, Msg_iIconSide);
                Msg_picIcon.SizeMode = PictureBoxSizeMode.Zoom;
                Msg_picIcon.Image    = icoSystem.ToBitmap();
                this.Controls.Add(Msg_picIcon);
            }

            /* The warning heading, when there is one: its own label, in the    */
            /* heading font and red, measured and wrapped by Msg_Layout like    */
            /* the text.  Both a heading and its font are needed -- a heading   */
            /* without its font would show in the dialog font and be just one   */
            /* more line of the message, which is what it exists not to be.     */
            Msg_lblHeading = null;

            if (sHeading != null && sHeading.Length != 0 && fntHeading != null)
            {   /* This box opens with a warning heading. */
                Msg_lblHeading             = new Label();
                Msg_lblHeading.AutoSize    = false;
                Msg_lblHeading.UseMnemonic = false;
                Msg_lblHeading.TextAlign   = ContentAlignment.TopLeft;
                Msg_lblHeading.Font        = fntHeading;
                Msg_lblHeading.ForeColor   = MSG_HEADING_COLOR;
                Msg_lblHeading.Text        = sHeading;
                this.Controls.Add(Msg_lblHeading);
            }

            /* The text.  Not auto-sized: it is measured and wrapped by    */
            /* Msg_Layout.  Mnemonics are off, so an ampersand in a file   */
            /* name is shown and not eaten.                                */
            Msg_lblText             = new Label();
            Msg_lblText.AutoSize    = false;
            Msg_lblText.UseMnemonic = false;
            Msg_lblText.TextAlign   = ContentAlignment.TopLeft;
            Msg_lblText.Text        = sText;
            this.Controls.Add(Msg_lblText);

            /* The buttons for the set, left to right; each carries its   */
            /* answer in its Tag and all share one handler.               */
            Msg_abtnRow = Msg_NewButtons(enmButtons);

            for (i = 0; i < Msg_abtnRow.Length; i++)
            {   /* One button per iteration. */
                Msg_abtnRow[i].Click += Msg_OnButtonClicked;
                this.Controls.Add(Msg_abtnRow[i]);
            }

            /* The default button's index in the row: the enumeration's       */
            /* values are 0, 0x100 and 0x200, so the value over the step is   */
            /* 0, 1 or 2.                                                     */
            iDefault = (int)enmDefaultButton / MSG_DEFAULT_STEP;

            if (iDefault < 0 || iDefault >= Msg_abtnRow.Length)
            {   /* A default the set has no button for (Button3 on a        */
                /* two-button box): the first button instead.  What the     */
                /* Windows box does in that case is not established here.   */
                iDefault = 0;
            }

            /* That button is the one Enter presses, and it starts with the   */
            /* focus, so a key press lands on the default as in the Windows   */
            /* box.                                                           */
            this.AcceptButton  = Msg_abtnRow[iDefault];
            this.ActiveControl = Msg_abtnRow[iDefault];

            /* The cancel button: Escape presses it.  Cancel where there is   */
            /* one; No in a Yes / No box behaves as Windows has it -- there   */
            /* is NO escape from that box and no close box either; OK alone   */
            /* is its own escape.                                             */
            this.CancelButton = Msg_CancelButton(enmButtons);

            if (this.CancelButton == null)
            {   /* No way out but a button (Yes / No, Abort / Retry /         */
                /* Ignore): no close box, as the Windows box does.  Whether   */
                /* Alt+F4 still closes the box then is the framework's (not   */
                /* established here); if it does, the answer is Cancel, the   */
                /* recorded default.  An Exit that closes the box from code   */
                /* gives Cancel too; the presenter then checks that its       */
                /* question is still the one showing before it uses the       */
                /* answer.                                                    */
                this.ControlBox = false;
            }

            /* Place everything, sizing the window to its content, and place   */
            /* it again whenever the size changes.                             */
            Msg_Layout();
            this.Resize += Msg_OnResize;
        }

        /*--------------------------------------------------------------------*/
        /* Msg_SystemIcon:                                                    */
        /*                                                                    */
        /* The system icon for a MessageBoxIcon value.  The enumeration has   */
        /* several names for each picture (Error, Hand and Stop are one       */
        /* value; so are Warning and Exclamation, and Information and         */
        /* Asterisk), so the comparison is by value.                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     enmIcon : the MessageBoxIcon value.                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Icon : the shared system icon (not to be disposed), or null    */
        /*            for None or an unknown value.                           */
        /*--------------------------------------------------------------------*/
        private static Icon Msg_SystemIcon(MessageBoxIcon enmIcon)
        {
            if (enmIcon == MessageBoxIcon.Error)
            {   /* Error, Hand, Stop: the red cross. */
                return(SystemIcons.Error);
            }

            if (enmIcon == MessageBoxIcon.Question)
            {   /* The question mark. */
                return(SystemIcons.Question);
            }

            if (enmIcon == MessageBoxIcon.Warning)
            {   /* Warning, Exclamation: the yellow triangle. */
                return(SystemIcons.Warning);
            }

            if (enmIcon == MessageBoxIcon.Information)
            {   /* Information, Asterisk: the blue "i". */
                return(SystemIcons.Information);
            }

            /* None, or a value not named above: no picture, and Msg_Layout   */
            /* then starts the text at the pad.                               */
            return(null);
        }

        /*--------------------------------------------------------------------*/
        /* Msg_PlaySound:                                                     */
        /*                                                                    */
        /* The sound Windows plays when a message box with this icon opens,   */
        /* so the program's box sounds like the system's.  None plays none.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     enmIcon : the MessageBoxIcon value.                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the sound is queued (or none).                          */
        /*--------------------------------------------------------------------*/
        private static void Msg_PlaySound(MessageBoxIcon enmIcon)
        {
            if (enmIcon == MessageBoxIcon.Error)
            {   /* The critical-stop sound. */
                System.Media.SystemSounds.Hand.Play();
                return;
            }

            if (enmIcon == MessageBoxIcon.Question)
            {   /* The question sound. */
                System.Media.SystemSounds.Question.Play();
                return;
            }

            if (enmIcon == MessageBoxIcon.Warning)
            {   /* The exclamation sound. */
                System.Media.SystemSounds.Exclamation.Play();
                return;
            }

            if (enmIcon == MessageBoxIcon.Information)
            {   /* The asterisk sound. */
                System.Media.SystemSounds.Asterisk.Play();
            }
        }

        /*--------------------------------------------------------------------*/
        /* Msg_NewButtons:                                                    */
        /*                                                                    */
        /* The buttons for a button set, left to right as Windows orders      */
        /* them, each with its caption (Yes, No, Retry, Abort and Ignore      */
        /* carry the access keys the Windows box gives them; OK and Cancel    */
        /* have none, as there) and its DialogResult value in its Tag.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     enmButtons : the set.                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Button[] : the buttons, made and not yet placed; one OK button */
        /*                for a set this box does not know.                   */
        /*--------------------------------------------------------------------*/
        private static Button[] Msg_NewButtons(MessageBoxButtons enmButtons)
        {
            switch (enmButtons)
            {   /* One set per case. */
                case MessageBoxButtons.OKCancel:
                    /* OK and Cancel, in the Windows box's order. */
                    return(new Button[] { Msg_NewButton("OK", DialogResult.OK), Msg_NewButton("Cancel", DialogResult.Cancel) });

                case MessageBoxButtons.YesNo:
                    /* Yes and No; no Cancel. */
                    return(new Button[] { Msg_NewButton("&Yes", DialogResult.Yes), Msg_NewButton("&No", DialogResult.No) });

                case MessageBoxButtons.YesNoCancel:
                    /* Yes, No and Cancel: No (refuse) kept apart from Cancel   */
                    /* (back out of the question).                              */
                    return(new Button[] { Msg_NewButton("&Yes", DialogResult.Yes), Msg_NewButton("&No", DialogResult.No),
                                          Msg_NewButton("Cancel", DialogResult.Cancel) });

                case MessageBoxButtons.RetryCancel:
                    /* Retry and Cancel, in the Windows box's order. */
                    return(new Button[] { Msg_NewButton("&Retry", DialogResult.Retry), Msg_NewButton("Cancel", DialogResult.Cancel) });

                case MessageBoxButtons.AbortRetryIgnore:
                    /* Abort, Retry and Ignore. */
                    return(new Button[] { Msg_NewButton("&Abort", DialogResult.Abort), Msg_NewButton("&Retry", DialogResult.Retry),
                                          Msg_NewButton("&Ignore", DialogResult.Ignore) });

                default:
                    /* OK alone -- also for any set not listed above. */
                    return(new Button[] { Msg_NewButton("OK", DialogResult.OK) });
            }
        }

        /*--------------------------------------------------------------------*/
        /* Msg_NewButton:                                                     */
        /*                                                                    */
        /* One button of the row: its caption and the answer it stands for.   */
        /* It is sized by Msg_Layout, so it is not auto-sized here.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sCaption : the caption, with its access key marked.            */
        /*     enmAnswer   : the DialogResult value pressing it gives.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Button : the button, not yet parented or placed.               */
        /*--------------------------------------------------------------------*/
        private static Button Msg_NewButton(string sCaption, DialogResult enmAnswer)
        {
            Button btnNew;

            /* The caption, and the answer in the Tag for the one shared     */
            /* click handler; not auto-sized, since Msg_Layout gives every   */
            /* button of the row the same size.                              */
            btnNew      = new Button();
            btnNew.Text = sCaption;
            btnNew.Tag  = enmAnswer;

            /* Made; the row's layout sizes it. */
            return(btnNew);
        }

        /*--------------------------------------------------------------------*/
        /* Msg_CancelButton:                                                  */
        /*                                                                    */
        /* The button Escape presses, as the Windows box has it: the Cancel   */
        /* button of a set that has one, OK when OK is the only button, and   */
        /* none for Yes / No and Abort / Retry / Ignore.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     enmButtons : the set.                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Button : the button in Msg_abtnRow, or null for no escape.     */
        /*--------------------------------------------------------------------*/
        private Button Msg_CancelButton(MessageBoxButtons enmButtons)
        {
            int i;

            if (enmButtons == MessageBoxButtons.OK)
            {   /* OK alone: Escape presses it. */
                return(Msg_abtnRow[0]);
            }

            for (i = 0; i < Msg_abtnRow.Length; i++)
            {   /* One button per iteration: the one that answers Cancel. */
                if ((DialogResult)Msg_abtnRow[i].Tag == DialogResult.Cancel)
                {   /* Found. */
                    return(Msg_abtnRow[i]);
                }
            }

            /* A set with no Cancel. */
            return(null);
        }

        /*--------------------------------------------------------------------*/
        /* Msg_Layout:                                                        */
        /*                                                                    */
        /* Place everything from measured sizes and size the window to hold   */
        /* it: the icon at the left, the heading and the text to its right,   */
        /* the buttons in a row at the bottom right.  Run once when the box   */
        /* is built and again on any resize; the steps are in the body.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the controls are placed and the client size set.        */
        /*--------------------------------------------------------------------*/
        private void Msg_Layout()
        {
            Size sizText;
            Size sizHeading;
            int  iHeadingBlock;
            int  iBlockWidth;
            int  iBlockHeight;
            int  iBlockTop;
            int  iTextLeft;
            int  iIconHeight;
            int  iTopHeight;
            int  iButtonWidth;
            int  iButtonHeight;
            int  iRowWidth;
            int  iWidth;
            int  x;
            int  y;
            int  i;

            if (Msg_abtnRow == null || Msg_bLaying)
            {   /* A resize raised by this very function's own sizing (the      */
                /* flag), which must not start a second layout inside the       */
                /* first.  The button-row test is a guard only: the row is      */
                /* made before the constructor's one call here, and Resize is   */
                /* hooked only after it, so no call comes while it is null.     */
                return;
            }

            /* Laying out from here: the client size set at the end raises   */
            /* Resize, which comes straight back here and must return.       */
            Msg_bLaying = true;

            /* The text, wrapped at the width limit (about what the Windows   */
            /* box allows), with MSG_TEXT_SLACK added so the last glyph is    */
            /* never clipped.                                                 */
            sizText = TextRenderer.MeasureText(Msg_lblText.Text, Msg_lblText.Font,
                                               new Size(Msg_iTextMaxWidth, int.MaxValue), TextFormatFlags.WordBreak);
            sizText = new Size(sizText.Width + MSG_TEXT_SLACK, sizText.Height + MSG_TEXT_SLACK);

            /* The warning heading, when there is one, measured the same way   */
            /* in its own font; it and the gap under it are the block's top.   */
            sizHeading    = Size.Empty;
            iHeadingBlock = 0;

            if (Msg_lblHeading != null)
            {   /* A heading: wrapped at the same width, with the same slack. */
                sizHeading    = TextRenderer.MeasureText(Msg_lblHeading.Text, Msg_lblHeading.Font,
                                                         new Size(Msg_iTextMaxWidth, int.MaxValue), TextFormatFlags.WordBreak);
                sizHeading    = new Size(sizHeading.Width + MSG_TEXT_SLACK, sizHeading.Height + MSG_TEXT_SLACK);
                iHeadingBlock = sizHeading.Height + Msg_iGap;
            }

            /* The heading-and-text block: as wide as the wider of the two,   */
            /* and as tall as the heading, its gap and the text together.     */
            iBlockWidth  = Math.Max(sizText.Width, sizHeading.Width);
            iBlockHeight = iHeadingBlock + sizText.Height;

            /* The icon and the block side by side, the shorter centred on   */
            /* the taller.  With no icon the block starts at the pad and     */
            /* has no partner to centre on.                                  */
            iTextLeft   = Msg_iPad;
            iIconHeight = 0;

            if (Msg_picIcon != null)
            {   /* The icon takes the left, with a pad on each side of it,   */
                /* and the block moves right past it.                        */
                iTextLeft   = Msg_iPad + Msg_iIconSide + Msg_iPad;
                iIconHeight = Msg_iIconSide;
            }

            /* The top part's height: the taller of icon and block. */
            iTopHeight = Math.Max(iBlockHeight, iIconHeight);

            if (Msg_picIcon != null)
            {   /* Centred on the taller of the two. */
                Msg_picIcon.Location = new Point(Msg_iPad, Msg_iPad + ((iTopHeight - iIconHeight) / 2));
            }

            /* The heading at the block's top, the text under it. */
            iBlockTop = Msg_iPad + ((iTopHeight - iBlockHeight) / 2);

            if (Msg_lblHeading != null)
            {   /* The heading first. */
                Msg_lblHeading.SetBounds(iTextLeft, iBlockTop, sizHeading.Width, sizHeading.Height);
            }

            /* The text under the heading and its gap (iHeadingBlock is 0   */
            /* with no heading), at exactly its measured size.              */
            Msg_lblText.SetBounds(iTextLeft, iBlockTop + iHeadingBlock, sizText.Width, sizText.Height);

            /* One size for every button, so the row reads evenly: the     */
            /* widest caption plus its margins, never under the minimum,   */
            /* and the font's height plus a pad.                           */
            iButtonWidth  = Msg_iButtonMin;
            iButtonHeight = Msg_abtnRow[0].Font.Height + Msg_iButtonHeightPad;

            for (i = 0; i < Msg_abtnRow.Length; i++)
            {   /* One button per iteration: its caption's width with the   */
                /* margins, kept when it is the widest so far.              */
                iButtonWidth = Math.Max(iButtonWidth, TextRenderer.MeasureText(Msg_abtnRow[i].Text, Msg_abtnRow[i].Font).Width + Msg_iButtonPad);
            }

            /* The row: every button at that width, a gap between each. */
            iRowWidth = (Msg_abtnRow.Length * iButtonWidth) + ((Msg_abtnRow.Length - 1) * Msg_iGap);

            /* The window's width: the top row or the button row, whichever is wider. */
            iWidth = Math.Max(iTextLeft + iBlockWidth, Msg_iPad + iRowWidth) + Msg_iPad;

            /* The buttons, right-aligned under the text. */
            y = Msg_iPad + iTopHeight + Msg_iPad;
            x = iWidth - Msg_iPad - iRowWidth;

            for (i = 0; i < Msg_abtnRow.Length; i++)
            {   /* One button per iteration, left to right. */
                Msg_abtnRow[i].SetBounds(x, y, iButtonWidth, iButtonHeight);
                x = x + iButtonWidth + Msg_iGap;
            }

            /* The window holds it all, down to the buttons and a pad.  (This   */
            /* raises Resize, which the flag turns away.)                       */
            this.ClientSize = new Size(iWidth, y + iButtonHeight + Msg_iPad);

            /* Laid out: a later resize may run the layout again. */
            Msg_bLaying = false;
        }

        /*--------------------------------------------------------------------*/
        /* Msg_OnButtonClicked:                                               */
        /*                                                                    */
        /* A button was pressed: its Tag is the answer; keep it and close.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the answer is recorded and the box closes.              */
        /*--------------------------------------------------------------------*/
        private void Msg_OnButtonClicked(object objSender, EventArgs evArgs)
        {
            /* The pressed button's answer, from the Tag Msg_NewButton gave   */
            /* it, recorded before the close so MSG_ShowWarned reads it       */
            /* when ShowDialog returns; Enter and Escape press a button,      */
            /* so they come here too.                                         */
            Msg_enmAnswer = (DialogResult)((Button)objSender).Tag;
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Msg_OnResize:                                                      */
        /*                                                                    */
        /* The window's size changed: place everything again from the         */
        /* measured sizes.  The border style forbids the user a resize; what  */
        /* else may raise one (a display scaling change) is not established   */
        /* here.                                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the window.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the layout fits the new size.                           */
        /*--------------------------------------------------------------------*/
        private void Msg_OnResize(object objSender, EventArgs evArgs)
        {
            /* The whole layout again; Msg_Layout turns away the resize its   */
            /* own sizing raises.                                             */
            Msg_Layout();
        }

        /*--------------------------------------------------------------------*/
        /* Msg_OnDisposed:                                                    */
        /*                                                                    */
        /* The box is gone: release the icon's bitmap.  The font is the       */
        /* caller's and is left alone.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the window.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the bitmap is released.                                 */
        /*--------------------------------------------------------------------*/
        private void Msg_OnDisposed(object objSender, EventArgs evArgs)
        {
            if (Msg_picIcon != null && Msg_picIcon.Image != null)
            {   /* The bitmap made from the system icon. */
                Msg_picIcon.Image.Dispose();
                Msg_picIcon.Image = null;
            }
        }

        /*--------------------------------------------------------------------*/
        /* OnKeyDown:                                                         */
        /*                                                                    */
        /* The Framework's key hook for this box: hands every key to          */
        /* Msg_OnKeyDown below, which carries the Ctrl+C behaviour and the    */
        /* base event's call, so the base event runs exactly once.            */
        /*                                                                    */
        /* An override because the framework requires it: a Form receives     */
        /* its own key events first only through this virtual method.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     evKey : the key pressed; Msg_OnKeyDown may mark it handled.    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the key has been dealt with.                            */
        /*--------------------------------------------------------------------*/
        protected override void OnKeyDown(KeyEventArgs evKey)
        {
            /* All the work, and the base call, are in the helper. */
            Msg_OnKeyDown(evKey);
        }

        /*--------------------------------------------------------------------*/
        /* Msg_OnKeyDown:                                                     */
        /*                                                                    */
        /* Ctrl+C puts the box's text on the clipboard, as the Windows box    */
        /* does (in its own layout, not this one), so an error can be pasted  */
        /* into a report.                                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     evKey : the key pressed.                                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the clipboard may hold the text.                        */
        /*--------------------------------------------------------------------*/
        private void Msg_OnKeyDown(KeyEventArgs evKey)
        {
            string sCopy;

            if (evKey.Control && evKey.KeyCode == Keys.C)
            {   /* Ctrl+C -- seen here whichever button has the focus, since   */
                /* KeyPreview is set.  The copy: the title, a blank line,      */
                /* the heading if any, the message.                            */
                sCopy = this.Text + "\r\n\r\n";

                if (Msg_lblHeading != null)
                {   /* The warning heading, on its own line. */
                    sCopy = sCopy + Msg_lblHeading.Text + "\r\n";
                }

                /* The message last, as shown (its own line breaks kept). */
                sCopy = sCopy + Msg_lblText.Text;

                try
                {   /* The clipboard can be held by another program; then nothing is copied. */
                    Clipboard.SetText(sCopy);
                }
                catch (Exception)
                {   /* Not copied, silently (why no message is given is not   */
                    /* recorded); the box stays as it is.                     */
                }

                /* Handled either way, so the focused button does not see   */
                /* the C as well (the framework's rule, as found).          */
                evKey.Handled = true;
            }

            /* The base handling as usual, which raises KeyDown for anyone   */
            /* else listening.                                               */
            base.OnKeyDown(evKey);
        }
    }
}
