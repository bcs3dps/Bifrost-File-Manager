/*----------------------------------------------------------------------------*/
/* EditorSaveMenu.cs                                                          */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* What the three editors (text, hex and structured) share for Save As: the   */
/* File menu's Save As item with its two choices -- into the open image's     */
/* filesystem (Image...) or to a Windows file (Windows...) -- the check a     */
/* typed Windows destination must pass before the editor writes to it, and    */
/* the close question: the ordinary one for changed content (save, save as to */
/* the image or to Windows, discard, or keep editing), or the recovery one    */
/* when the image it saves into is no longer open (save to Windows, discard,  */
/* or keep editing).  Choosing a file and writing it stay separate steps:     */
/* none of this writes anything.                                              */
/*----------------------------------------------------------------------------*/

/* The framework: EventHandler, String, Math and the exceptions (System),   */
/* the question's font and sizes (Drawing), the path and folder tests       */
/* (IO), and the menu items and the question's controls (Windows.Forms).    */
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* SVM_CurrentFile:                                                       */
    /*                                                                        */
    /* The editor's call that says where it saves now, read each time the     */
    /* Save As choices open, so their enabling follows the binding the worker */
    /* has acknowledged rather than one captured when the menu was made.      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     OFD_clsOpenedFile : the editor's current file description (the     */
    /*                         object itself, not a copy); null once the      */
    /*                         viewer is gone.                                */
    /*------------------------------------------------------------------------*/
    internal delegate OFD_clsOpenedFile SVM_CurrentFile();

    /*------------------------------------------------------------------------*/
    /* RCA_enmRecoveryChoice -- the answer to the recovery question: what the */
    /* user chose for an editor whose image is no longer open --              */
    /* documentation table.                                                   */
    /*------------------------------------------------------------------------*/
    internal enum RCA_enmRecoveryChoice
    {
        Cancel = 0,       // keep editing, nothing lost (the default, and what closing the question gives)
        Discard = 1,      // close the editor, dropping its unsaved changes
        SaveAs = 2,       // choose a Windows file to save to -- a request only; nothing is written or closed yet
        Save = 3,         // the ordinary close question only: save to the editor's own file, closing once it succeeds
        SaveAsImage = 4   // the ordinary close question only: choose a place in the open image to save to -- a request only, like SaveAs
    }

    /*------------------------------------------------------------------------*/
    /* SVM_clsSaveMenu -- an editor's Save As item made into its two choices, */
    /* and the shared functions behind them -- documentation table.           */
    /*------------------------------------------------------------------------*/
    internal sealed class SVM_clsSaveMenu
    {
        private readonly ToolStripMenuItem Svm_tsmParent;   // the editor's Save As item (the editor's own)
        private readonly ToolStripMenuItem Svm_tsmImage;    // its "Image..." choice: save into the open image's filesystem (ext2/3/4, FAT or exFAT)
        private readonly ToolStripMenuItem Svm_tsmWindows;  // its "Windows..." choice: save to a Windows file
        private readonly SVM_CurrentFile Svm_pfnCurrent;    // the editor's call for where it saves now

        /*--------------------------------------------------------------------*/
        /* SVM_clsSaveMenu:                                                   */
        /*                                                                    */
        /* Give an editor's Save As item its two choices (Image... and        */
        /* Windows...), each wired to the editor's own handler.  Whether a    */
        /* choice is enabled is only a hint; the worker decides what may run. */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     tsmParent         : the editor's Save As item.                 */
        /*     pfnCurrent        : the editor's call for where it saves now.  */
        /*     pfnImageClicked   : the editor's handler for "Image...".       */
        /*     pfnWindowsClicked : the editor's handler for "Windows...".     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the menu wired up; nothing chosen or written.  */
        /*--------------------------------------------------------------------*/
        internal SVM_clsSaveMenu(ToolStripMenuItem tsmParent,
            SVM_CurrentFile pfnCurrent, EventHandler pfnImageClicked, EventHandler pfnWindowsClicked)
        {
            /* The item and the call kept; the item's text and shortcut set   */
            /* (Ctrl+Shift+S).  The editor owns the item and its children;    */
            /* this object owns neither the editor nor anything the worker    */
            /* holds.                                                         */
            Svm_tsmParent = tsmParent;
            Svm_pfnCurrent = pfnCurrent;
            Svm_tsmParent.Text = "Save &As";
            Svm_tsmParent.ShortcutKeys = Keys.Control | Keys.Shift | Keys.S;

            /* Each child has the editor's handler for its own filesystem, so   */
            /* which choice was made is known from the handler that runs --     */
            /* nothing is read back from the clicked item.  Neither is          */
            /* evidence that a filesystem can accept a Save.                    */
            Svm_tsmImage = new ToolStripMenuItem("&Image...");
            Svm_tsmWindows = new ToolStripMenuItem("&Windows...");
            Svm_tsmImage.Click += pfnImageClicked;
            Svm_tsmWindows.Click += pfnWindowsClicked;
            Svm_tsmParent.DropDownItems.Add(Svm_tsmImage);
            Svm_tsmParent.DropDownItems.Add(Svm_tsmWindows);

            /* A click on the item itself (or its shortcut) opens the two     */
            /* choices rather than picking one; and each opening sets which   */
            /* choices are enabled.                                           */
            Svm_tsmParent.Click += Svm_OnParentClick;
            Svm_tsmParent.DropDownOpening += Svm_OnOpening;
        }

        /* Menu presentation and captured selection requests. */

        /*--------------------------------------------------------------------*/
        /* Svm_OnParentClick:                                                 */
        /*                                                                    */
        /* The Save As item's click (or its shortcut): open its two choices.  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the item (unused).                                 */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the choices shown; neither is chosen.                   */
        /*--------------------------------------------------------------------*/
        private void Svm_OnParentClick(object objSender, EventArgs evArgs)
        {
            /* The drop-down opened, so the shortcut never silently picks one   */
            /* of the two destinations.                                         */
            Svm_tsmParent.ShowDropDown();
        }

        /*--------------------------------------------------------------------*/
        /* Svm_OnOpening:                                                     */
        /*                                                                    */
        /* The choices opening: enable each from where the editor saves now.  */
        /* "Image..." only while an image is open to save into; "Windows..."  */
        /* whenever the editor can choose a target.  A hint only -- it grants */
        /* no right to save.                                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the item (unused).                                 */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the two choices enabled or not.                         */
        /*--------------------------------------------------------------------*/
        private void Svm_OnOpening(object objSender, EventArgs evArgs)
        {
            OFD_clsOpenedFile objCurrent;

            /* Where the editor saves now, read once -- the binding the        */
            /* worker has acknowledged, not a Save As still in progress.  An   */
            /* editor opened from Windows can outlive the image, and a Save    */
            /* As can move either kind.  "Image..." needs the target picker    */
            /* and an image available (the description's own call says);       */
            /* "Windows..." needs only the picker.                             */
            objCurrent = Svm_pfnCurrent();
            Svm_tsmImage.Enabled = objCurrent != null &&
                objCurrent.OFD_pfnPickTarget != null &&
                objCurrent.OFD_pfnImageAvailable != null &&
                objCurrent.OFD_pfnImageAvailable();
            Svm_tsmWindows.Enabled = objCurrent != null &&
                objCurrent.OFD_pfnPickTarget != null;
        }

        /*--------------------------------------------------------------------*/
        /* SVM_Request:                                                       */
        /*                                                                    */
        /* Ask for a Save As target of the chosen kind -- called by each      */
        /* editor's Save As handlers (TextEditForm.cs, HexEditForm.cs,        */
        /* json\JsonEditForm.cs) with the kind their handler stands for.  It  */
        /* only passes the request on to the editor's target picker; nothing  */
        /* is chosen or written here.                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmOwner   : the editor.                                       */
        /*     objCurrent : where it saves now, as at the click.              */
        /*     bWindows   : true for "Windows...", false for "Image...".      */
        /*     pfnChosen  : the editor's call that receives the target --     */
        /*                  null when there is none.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the request passed on, or answered null at once.        */
        /*--------------------------------------------------------------------*/
        internal static void SVM_Request(Form frmOwner, OFD_clsOpenedFile objCurrent,
            bool bWindows, TIN_TargetChosen pfnChosen)
        {
            if (objCurrent == null || objCurrent.OFD_pfnPickTarget == null)
            {   /* No description, or no picker (the viewer is gone or was   */
                /* never bound): answered "no target" -- nothing was         */
                /* staged or written, and the editor keeps its content.      */
                pfnChosen(null);
                return;
            }

            /* Passed to the picker, which captures the editor and the names   */
            /* to offer.  When a close question is waiting for this choice,    */
            /* the picker answers it directly rather than queue a request      */
            /* behind the worker that is waiting on that very question.        */
            objCurrent.OFD_pfnPickTarget(frmOwner, objCurrent, bWindows, pfnChosen);
        }

        /* Protected Windows destination shape and application file ownership. */

        /*--------------------------------------------------------------------*/
        /* SVM_ValidatePath:                                                  */
        /*                                                                    */
        /* Whether a typed Windows path may be an editor's Save As target: a  */
        /* plain file in an existing folder, not a device, and not one of the */
        /* files the editor's content must never be written over -- the       */
        /* image, the editor's work file, its original, the image open now,   */
        /* or anything in the staging folder.  Called by MainCommand.cs for   */
        /* the Windows file dialog's choice.  A path that passes is not proof */
        /* the save will work: the editor's own save still has to succeed.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSource         : the editor's file description (what it     */
        /*                         came from and works in).                   */
        /*     sTyped            : the path as chosen.                        */
        /*     sPath             : receives the path made full; use it only   */
        /*                         when true is returned.                     */
        /*     sProblem          : receives the reason when false is          */
        /*                         returned; "" otherwise.                    */
        /*     bAllowHostCurrent : true lets an editor of a Windows file save */
        /*                         over that same file; optional, false by    */
        /*                         default.                                   */
        /*     sOpenImage        : the image open now, which may not be the   */
        /*                         target; optional, null by default.         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the path may be used.                         */
        /*--------------------------------------------------------------------*/
        internal static bool SVM_ValidatePath(OFD_clsOpenedFile objSource, string sTyped,
            out string sPath, out string sProblem, bool bAllowHostCurrent = false,
            string sOpenImage = null)
        {
            string sRoot;
            string sParent;
            string sName;
            string sStem;
            string[] arrComponents;
            int i;

            /* Nothing is written to test the path: whether the folder can be   */
            /* written, has room, and keeps the file is the editor's save's     */
            /* to find out.                                                     */
            sPath = "";
            sProblem = "";
            try
            {   /* The path calls below throw for some malformed names (a   */
                /* ":" stream suffix, for one); the catch turns that into   */
                /* the reason.                                              */
                if (String.IsNullOrWhiteSpace(sTyped) || !Path.IsPathRooted(sTyped) ||
                    sTyped.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
                {   /* Empty, relative (an image path is), or a raw device   */
                    /* path (\\.\...): not a file to save to.                */
                    sProblem = "Choose a full Windows file path on an available filesystem.";
                    return(false);
                }

                /* Its root must be a whole drive (the letter, a colon and a       */
                /* backslash) or a share (two backslashes) -- a drive letter and   */
                /* colon with a name straight after counts as rooted to the path   */
                /* calls but is relative to that drive's current folder.           */
                sRoot = Path.GetPathRoot(sTyped);
                if (String.IsNullOrEmpty(sRoot) || sRoot.Length < 3 ||
                    (!sTyped.StartsWith(@"\\", StringComparison.Ordinal) &&
                    (sRoot[1] != ':' ||
                    (sRoot[2] != Path.DirectorySeparatorChar && sRoot[2] != Path.AltDirectorySeparatorChar))))
                {   /* No full drive or share: refused. */
                    sProblem = "The destination must include its full drive or network-share path.";
                    return(false);
                }

                /* Made full (so the comparisons below see one spelling), then   */
                /* its folder and name: a name with no forbidden character,      */
                /* not a folder, in a folder that exists.                        */
                sPath = Path.GetFullPath(sTyped);
                sParent = Path.GetDirectoryName(sPath);
                sName = Path.GetFileName(sPath);
                if (String.IsNullOrEmpty(sName) || sName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    Directory.Exists(sPath) ||
                    String.IsNullOrEmpty(sParent) || !Directory.Exists(sParent))
                {   /* No name, a bad character, a folder, or a missing   */
                    /* folder: refused.                                   */
                    sProblem = "Choose an ordinary file in an existing Windows folder.";
                    return(false);
                }

                /* No part may end in a dot or a space: Windows drops those, so   */
                /* such a name is another spelling of a different one, and        */
                /* could slip past the comparisons with the protected files.      */
                arrComponents = sPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                for (i = 0; i < arrComponents.Length; i++)
                {   /* One part of the path per iteration. */
                    if (arrComponents[i].EndsWith(".", StringComparison.Ordinal) ||
                        arrComponents[i].EndsWith(" ", StringComparison.Ordinal))
                    {   /* A trailing dot or space: refused. */
                        sProblem = "Choose a Windows path without trailing dots or spaces in its names.";
                        return(false);
                    }
                }

                /* A device name -- CON, PRN, AUX, NUL, and COM or LPT followed   */
                /* by a digit 0 to 9 or a superscript one, two or three, as       */
                /* Windows documents its reserved names -- also with an           */
                /* extension, so only the part before the first dot is tested:    */
                /* it names a device, not a file, and saving "to NUL" would       */
                /* succeed and keep nothing.  The superscripts are built from     */
                /* their code points (U+00B9, U+00B2, U+00B3), so the source      */
                /* stays ASCII; ToUpperInvariant leaves them as they are.         */
                sStem = sName.Split('.')[0].ToUpperInvariant();
                if (sStem == "CON" || sStem == "PRN" || sStem == "AUX" || sStem == "NUL" ||
                    (sStem.Length == 4 && (sStem.StartsWith("COM", StringComparison.Ordinal) ||
                    sStem.StartsWith("LPT", StringComparison.Ordinal)) &&
                    ((sStem[3] >= '0' && sStem[3] <= '9') || sStem[3] == (char)0x00B9 ||
                    sStem[3] == (char)0x00B2 || sStem[3] == (char)0x00B3)))
                {   /* A device name: refused. */
                    sProblem = "Choose an ordinary Windows filename, not a reserved device name.";
                    return(false);
                }

                if ((!String.IsNullOrEmpty(objSource.OFD_sImageFile) && String.Equals(sPath,
                    Path.GetFullPath(objSource.OFD_sImageFile), StringComparison.OrdinalIgnoreCase)) ||
                    ((!objSource.OFD_bHostFile || !bAllowHostCurrent) &&
                    String.Equals(sPath, Path.GetFullPath(objSource.OFD_sTempFile), StringComparison.OrdinalIgnoreCase)) ||
                    (!String.IsNullOrEmpty(sOpenImage) && String.Equals(sPath,
                    Path.GetFullPath(sOpenImage), StringComparison.OrdinalIgnoreCase)) ||
                    (objSource.OFD_objOriginal != null && !objSource.OFD_objOriginal.ORG_bHost &&
                    (String.Equals(sPath, Path.GetFullPath(objSource.OFD_objOriginal.ORG_sBaseline), StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(sPath, Path.GetFullPath(objSource.OFD_objOriginal.ORG_objTarget.TGT_sPath), StringComparison.OrdinalIgnoreCase))))
                {   /* One of the files that must not be written over (compared   */
                    /* ignoring case, as Windows paths are): the editor's image   */
                    /* file; its work file -- unless this is an editor of that    */
                    /* Windows file saving over it, when the caller allows it;    */
                    /* the image open now; or, for content first opened from an   */
                    /* image, its original's baseline copy and that image.        */
                    /* Writing the content there would destroy what it could be   */
                    /* recovered from: refused.                                   */
                    sProblem = "Choose a separate Windows file, not the original image or editor work file.";
                    return(false);
                }

                if (!String.IsNullOrEmpty(objSource.OFD_sStageRoot) && sPath.StartsWith(
                    Path.GetFullPath(objSource.OFD_sStageRoot).TrimEnd(Path.DirectorySeparatorChar) +
                    Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {   /* Inside the program's staging folder, whose files are      */
                    /* the program's own temporary ones: refused.  (That the     */
                    /* folder is cleared at shutdown is not established here.)   */
                    sProblem = "Choose a permanent folder outside the application's temporary files.";
                    return(false);
                }
            }
            catch (Exception exPath)
            {   /* A path call threw on the name: refused with its message;   */
                /* the editor keeps its content.                              */
                sProblem = exPath.Message;
                sPath = "";
                return(false);
            }

            /* Usable.  Permission, room and a write that holds are the   */
            /* editor's save's to find out.                               */
            return(true);
        }

    }

    /*------------------------------------------------------------------------*/
    /* RCD_clsRecoveryDialog -- an editor's close question: the ordinary one  */
    /* for changed content (Save, Save As Image, Save As Windows, Discard,    */
    /* Cancel), or the recovery one when the image it saves into is no longer */
    /* open (Save As to Windows, Discard, Cancel).  It records the choice     */
    /* only -- no path is typed here -- documentation table.                  */
    /*------------------------------------------------------------------------*/
    internal sealed class RCD_clsRecoveryDialog
    {
        /* The layout's sizes, and the question's own controls and answer. */
        private const int RCD_PAD = 12;                   // padding round the text and buttons, pixels
        private const int RCD_TEXT_WIDTH = 640;           // the widest the text may run before wrapping, pixels (its height is measured)
        private readonly Form Rcd_frmPopup;               // the question's form, shown modeless by the editor, which disposes it once it has closed
        private readonly Label Rcd_lblExplanation;        // the question's text
        private readonly TableLayoutPanel Rcd_tblLayout;  // two rows, sized to their contents: the text, then the buttons
        private RCA_enmRecoveryChoice Rcd_enmAnswer;      // the choice; Cancel until a button sets it

        /*--------------------------------------------------------------------*/
        /* RCD_clsRecoveryDialog:                                             */
        /*                                                                    */
        /* The question, built but not shown (RCD_Create hands it to the      */
        /* editor, which shows it editor-modal): the recovery question for    */
        /* an entry whose image is gone, or the ordinary close question for   */
        /* changed content -- Save, Save As to the image or to Windows,       */
        /* Discard, Cancel.                                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     fntDialog  : the program's dialog font (not disposed here).    */
        /*     sName      : the editor's file name, for the caption.          */
        /*     sTitle     : the editor's file as its title names it, for the  */
        /*                  ordinary question's text; unused otherwise.       */
        /*     bOrdinary  : true for the ordinary close question, false for   */
        /*                  the recovery question.                            */
        /*     bImageOpen : the ordinary question only: an image filesystem   */
        /*                  is open, so Save As to the image is offered (the  */
        /*                  button greyed otherwise, as the menu's Image item */
        /*                  is).                                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the question, its answer Cancel.               */
        /*--------------------------------------------------------------------*/
        private RCD_clsRecoveryDialog(Font fntDialog, string sName, string sTitle, bool bOrdinary, bool bImageOpen)
        {
            FlowLayoutPanel pnlButtons;
            Button btnCancel;
            Button btnDiscard;
            Button btnSaveAs;
            Button btnSave;
            Button btnSaveAsImage;

            /* The form: Cancel to begin with, the dialog font, "Recover" (or    */
            /* "Close" for the ordinary question) and the name as its caption,   */
            /* sized to its contents and not resizable.  It is shown modeless,   */
            /* which the framework does not centre, so the editor places it      */
            /* over itself (PRM_CentreOver).  It holds nothing but its           */
            /* controls and the answer -- no file, stage or engine access.       */
            Rcd_enmAnswer = RCA_enmRecoveryChoice.Cancel;
            Rcd_frmPopup = new Form();
            Rcd_frmPopup.Font = fntDialog;
            Rcd_frmPopup.Text = "Recover " + sName;
            if (bOrdinary)
            {   /* The ordinary close: captioned as a close. */
                Rcd_frmPopup.Text = "Close " + sName;
            }
            Rcd_frmPopup.AutoScaleMode = AutoScaleMode.Font;
            Rcd_frmPopup.AutoSize = true;
            Rcd_frmPopup.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Rcd_frmPopup.FormBorderStyle = FormBorderStyle.FixedDialog;
            Rcd_frmPopup.MaximizeBox = false;
            Rcd_frmPopup.MinimizeBox = false;
            Rcd_frmPopup.ShowInTaskbar = false;

            /* The text, wrapping at RCD_TEXT_WIDTH: for the recovery           */
            /* question, the image is gone and the three ways on (the           */
            /* ordinary question's own text is set below).  Whether a Windows   */
            /* save will work is found out only by the save.                    */
            Rcd_lblExplanation = new Label();
            Rcd_lblExplanation.AutoSize = true;
            Rcd_lblExplanation.MaximumSize = new Size(RCD_TEXT_WIDTH, 0);
            Rcd_lblExplanation.Text = "The filesystem this editor is targeting is unavailable.\n\n" +
                "Save As to a Windows file before continuing, discard the changes, or cancel to keep editing.";
            if (bOrdinary)
            {   /* The ordinary close: the question itself, naming the file. */
                Rcd_lblExplanation.Text = "Save the changes to " + sTitle + "?";
            }

            /* The buttons, each recording its choice.  Discard is the only    */
            /* one that loses the changes; a Save As only asks for a target,   */
            /* later.  The recovery question's Save As is to Windows (the      */
            /* image is gone), so it keeps its plain words.                    */
            btnCancel = new Button();
            btnCancel.Text = "Cancel";
            btnCancel.AutoSize = true;
            btnCancel.Click += Rcd_OnCancel;
            btnDiscard = new Button();
            btnDiscard.Text = "Discard";
            btnDiscard.AutoSize = true;
            btnDiscard.Click += Rcd_OnDiscard;
            btnSaveAs = new Button();
            btnSaveAs.Text = "Save As...";
            btnSaveAs.AutoSize = true;
            btnSaveAs.Click += Rcd_OnSaveAs;

            /* The button row, right to left (so Cancel ends at the right),   */
            /* sized to the buttons.                                          */
            pnlButtons = new FlowLayoutPanel();
            pnlButtons.AutoSize = true;
            pnlButtons.FlowDirection = FlowDirection.RightToLeft;
            pnlButtons.Dock = DockStyle.Fill;
            pnlButtons.WrapContents = false;
            pnlButtons.Controls.Add(btnCancel);
            pnlButtons.Controls.Add(btnDiscard);
            pnlButtons.Controls.Add(btnSaveAs);
            Rcd_frmPopup.AcceptButton = btnCancel;
            Rcd_frmPopup.CancelButton = btnCancel;
            if (bOrdinary)
            {   /* The ordinary close: Save As names its side (Windows), and    */
                /* Save As Image and Save are added on its left, Save the       */
                /* leftmost; Enter is Save -- the answer the question offers    */
                /* first -- and Escape stays Cancel.  Save As Image is greyed   */
                /* with no image filesystem open, as the menu's Image item.     */
                btnSaveAs.Text = "Save As Windows...";
                btnSaveAsImage = new Button();
                btnSaveAsImage.Text = "Save As Image...";
                btnSaveAsImage.AutoSize = true;
                btnSaveAsImage.Enabled = bImageOpen;
                btnSaveAsImage.Click += Rcd_OnSaveAsImage;
                btnSave = new Button();
                btnSave.Text = "Save";
                btnSave.AutoSize = true;
                btnSave.Click += Rcd_OnSave;
                pnlButtons.Controls.Add(btnSaveAsImage);
                pnlButtons.Controls.Add(btnSave);
                Rcd_frmPopup.AcceptButton = btnSave;
            }
            else
            {   /* The recovery question: Enter and Escape both mean Cancel,   */
                /* so the keys can never lose or move the content.             */
            }

            /* The two rows -- text, then buttons -- padded by RCD_PAD and   */
            /* put on the form, all parented before the form is shown.       */
            Rcd_tblLayout = new TableLayoutPanel();
            Rcd_tblLayout.AutoSize = true;
            Rcd_tblLayout.ColumnCount = 1;
            Rcd_tblLayout.RowCount = 2;
            Rcd_tblLayout.Padding = new Padding(RCD_PAD);
            Rcd_tblLayout.Dock = DockStyle.Fill;
            Rcd_tblLayout.Controls.Add(Rcd_lblExplanation, 0, 0);
            Rcd_tblLayout.Controls.Add(pnlButtons, 0, 1);
            Rcd_frmPopup.Controls.Add(Rcd_tblLayout);

            /* Laid out now everything is in place, and only then the resize    */
            /* hooked, so the text's wrap follows the form's width from here.   */
            Rcd_frmPopup.PerformLayout();
            Rcd_frmPopup.Resize += Rcd_OnResize;
        }

        /* Recovery choice presentation and manual outcomes. */

        /*--------------------------------------------------------------------*/
        /* Rcd_OnResize:                                                      */
        /*                                                                    */
        /* The form resized: wrap the text to the width inside the padding    */
        /* (never wider than RCD_TEXT_WIDTH).                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the form (unused).                                 */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the wrap width changed when it differs; else nothing.   */
        /*--------------------------------------------------------------------*/
        private void Rcd_OnResize(object objSender, EventArgs evArgs)
        {
            int iWidth;

            /* The width between the padding, at most RCD_TEXT_WIDTH and at   */
            /* least 1 (a zero would stop the wrap).                          */
            iWidth = Math.Max(1, Math.Min(RCD_TEXT_WIDTH,
                Rcd_frmPopup.ClientSize.Width - Rcd_tblLayout.Padding.Horizontal));
            if (Rcd_lblExplanation.MaximumSize.Width != iWidth)
            {   /* A new width: set, and the rows re-measure the text's   */
                /* height.  The same width is left alone, so setting it   */
                /* cannot set off another resize.                         */
                Rcd_lblExplanation.MaximumSize = new Size(iWidth, 0);
            }
        }

        /*---------------------------------------------------------------------*/
        /* RCD_Create:                                                         */
        /*                                                                     */
        /* The close question for an editor closing with changes, built and    */
        /* handed back NOT shown: the editor shows it EDITOR-MODAL -- modeless */
        /* over itself, the editor disabled while it is up -- so the main      */
        /* window and the other editors carry on, and the editor reads the     */
        /* choice once the question has closed (RCD_ReadAnswer).  The          */
        /* recovery question for an entry whose image is gone (save to         */
        /* Windows, discard, or keep editing), or the ordinary question        */
        /* (save, save as to the image or to Windows, discard, or keep         */
        /* editing).  Called by each of the three editors' close (text, hex    */
        /* and structured).                                                    */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     fntDialog  : the dialog font (not disposed here).               */
        /*     objSource  : the editor's file description, for the caption     */
        /*                  and the ordinary question's text.                  */
        /*     bOrdinary  : true for the ordinary question, false for the      */
        /*                  recovery question.                                 */
        /*     bImageOpen : the ordinary question only: Save As to the image   */
        /*                  is offered (an image filesystem is open).          */
        /*                                                                     */
        /* Returns:                                                            */
        /*     RCD_clsRecoveryDialog : the question, its answer Cancel until   */
        /*                             a button records another; the caller    */
        /*                             shows its form (RCD_ReadPopup) and      */
        /*                             disposes it once it has closed.         */
        /*---------------------------------------------------------------------*/
        internal static RCD_clsRecoveryDialog RCD_Create(Font fntDialog,
            OFD_clsOpenedFile objSource, bool bOrdinary, bool bImageOpen)
        {
            /* Built with the file's name for the caption and its title for   */
            /* the ordinary question's text; nothing is shown here.           */
            return(new RCD_clsRecoveryDialog(fntDialog, objSource.OFD_sName,
                objSource.OFD_Title(), bOrdinary, bImageOpen));
        }

        /*--------------------------------------------------------------------*/
        /* RCD_ReadPopup:                                                     */
        /*                                                                    */
        /* The question's form, for the editor that shows it (RCD_Create):    */
        /* the editor places it, hooks its FormClosed, shows it and later     */
        /* disposes it.                                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Form : the question's form, never null.                        */
        /*--------------------------------------------------------------------*/
        internal Form RCD_ReadPopup()
        {
            /* The one form this question owns. */
            return(Rcd_frmPopup);
        }

        /*--------------------------------------------------------------------*/
        /* RCD_ReadAnswer:                                                    */
        /*                                                                    */
        /* The choice the question recorded: the last button pressed, or      */
        /* Cancel when it closed with none (Escape, its close box, or an Exit */
        /* taking it down).  Read by the editor once the form has closed.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RCA_enmRecoveryChoice : the choice.                            */
        /*--------------------------------------------------------------------*/
        internal RCA_enmRecoveryChoice RCD_ReadAnswer()
        {
            /* As the buttons left it. */
            return(Rcd_enmAnswer);
        }

        /*--------------------------------------------------------------------*/
        /* Rcd_OnCancel:                                                      */
        /*                                                                    */
        /* Cancel: keep editing, nothing lost.                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Cancel recorded and the question closed.                */
        /*--------------------------------------------------------------------*/
        private void Rcd_OnCancel(object objSender, EventArgs evArgs)
        {
            /* The same as closing the question without a choice. */
            Rcd_enmAnswer = RCA_enmRecoveryChoice.Cancel;
            Rcd_frmPopup.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Rcd_OnDiscard:                                                     */
        /*                                                                    */
        /* Discard: close the editor and drop its unsaved changes.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Discard recorded and the question closed; the editor    */
        /*            closes after.                                           */
        /*--------------------------------------------------------------------*/
        private void Rcd_OnDiscard(object objSender, EventArgs evArgs)
        {
            /* The only choice that lets the editor drop its changes. */
            Rcd_enmAnswer = RCA_enmRecoveryChoice.Discard;
            Rcd_frmPopup.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Rcd_OnSaveAs:                                                      */
        /*                                                                    */
        /* Save As: ask for a Windows file to save to, once the question has  */
        /* closed.                                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Save As recorded and the question closed; no file       */
        /*            dialog, stage or write here.                            */
        /*--------------------------------------------------------------------*/
        private void Rcd_OnSaveAs(object objSender, EventArgs evArgs)
        {
            /* The target is asked for, and the save made, by the editor   */
            /* after this returns.                                         */
            Rcd_enmAnswer = RCA_enmRecoveryChoice.SaveAs;
            Rcd_frmPopup.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Rcd_OnSave:                                                        */
        /*                                                                    */
        /* The ordinary question's Save: save to the editor's own file, the   */
        /* editor closing once that save succeeds.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Save recorded and the question closed; the save is the  */
        /*            editor's, after.                                        */
        /*--------------------------------------------------------------------*/
        private void Rcd_OnSave(object objSender, EventArgs evArgs)
        {
            /* Recorded only; the editor saves after this returns. */
            Rcd_enmAnswer = RCA_enmRecoveryChoice.Save;
            Rcd_frmPopup.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Rcd_OnSaveAsImage:                                                 */
        /*                                                                    */
        /* The ordinary question's Save As Image: ask for a place in the open */
        /* image to save to, once the question has closed.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Save As Image recorded and the question closed; no      */
        /*            picker, stage or write here.                            */
        /*--------------------------------------------------------------------*/
        private void Rcd_OnSaveAsImage(object objSender, EventArgs evArgs)
        {
            /* The target is asked for, and the save made, by the editor   */
            /* after this returns, as for the Windows Save As.             */
            Rcd_enmAnswer = RCA_enmRecoveryChoice.SaveAsImage;
            Rcd_frmPopup.Close();
        }
    }
}
