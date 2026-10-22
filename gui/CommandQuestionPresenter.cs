/*----------------------------------------------------------------------------*/
/* CommandQuestionPresenter.cs                                                */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The UI side of the worker's questions.  The worker decides that a          */
/* question is needed and what it is; this file shows it -- over the window   */
/* it belongs to, in the dialog its kind uses -- and answers through the      */
/* display it was given, which alone may answer and only while it is          */
/* current.  It starts nothing: the worker decides what follows the answer.   */
/* Also here: the record of each display's popups and of the editor-modal     */
/* close questions shown for it, so an Exit can take them down, and the       */
/* draft a name or offset question keeps across an Exit declined.             */
/*----------------------------------------------------------------------------*/

/* The framework: the base types and exceptions (System), the popup lists     */
/* and the display table (Collections.Generic), the dialog fonts (Drawing),   */
/* the file names in the offset question and the raw-write warning (IO),      */
/* and the forms and message boxes (Windows.Forms).                           */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* QPR_FindOwner:                                                         */
    /*                                                                        */
    /* The shape of the main window's owner lookup for the windows this file  */
    /* does not know -- the check, partition and progress windows             */
    /* (MainCommand.cs Main_FindCommandOwner).                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lWindow : the window id or the command's serial; 0 asks for the    */
    /*               owner of an application-wide question (an Exit).         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Form : that live window, borrowed, or null.                        */
    /*------------------------------------------------------------------------*/
    internal delegate Form QPR_FindOwner(long lWindow);

    /*------------------------------------------------------------------------*/
    /* QDU_clsDraftUpdates -- the handlers that keep a name or offset         */
    /* question's typed text as its draft, bound to the display that showed   */
    /* it: an older popup still unwinding keeps writing to its own display,   */
    /* never to a newer one -- documentation table.                           */
    /*------------------------------------------------------------------------*/
    internal class QDU_clsDraftUpdates
    {
        private readonly QST_clsQuestion Qdu_objQuestion;        // the question, borrowed; its draft outlives an Exit declined
        private readonly PRS_clsPresentation Qdu_objDisplay;     // the display that showed this popup; never replaced
        private readonly PRF_clsPartitionForm Qdu_dlgPartition;  // the offset question's popup; null for a name question
        private string Qdu_sRestoredText;                        // the offset draft to put back, exactly as typed (empty or incomplete included)
        private bool Qdu_bRestore;                               // a draft is to be put back, whatever its text

        /*--------------------------------------------------------------------*/
        /* QDU_clsDraftUpdates:                                               */
        /*                                                                    */
        /* The handlers for one popup, bound to its display.  The caller has  */
        /* already made this display the draft's owner (QST_AdoptDraft)       */
        /* before the popup's controls are built.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objQuestion   : the question.                                  */
        /*     objDisplay    : the display showing the popup.                 */
        /*     dlgPartition  : the offset popup, or null for a name question. */
        /*     sRestoredText : the offset draft to put back.                  */
        /*     bRestore      : a draft is to be put back.                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the handlers; no answer is made here.          */
        /*--------------------------------------------------------------------*/
        internal QDU_clsDraftUpdates(QST_clsQuestion objQuestion,
            PRS_clsPresentation objDisplay, PRF_clsPartitionForm dlgPartition,
            string sRestoredText, bool bRestore)
        {
            /* Kept as given -- the display above all: an older popup writes   */
            /* to its own display, so once a newer display has taken the       */
            /* draft over the question refuses its late text, while text       */
            /* typed before that, as the old popup unwinds, is not lost.       */
            Qdu_objQuestion = objQuestion;
            Qdu_objDisplay = objDisplay;
            Qdu_dlgPartition = dlgPartition;
            Qdu_sRestoredText = sRestoredText;
            Qdu_bRestore = bRestore;
        }

        /*--------------------------------------------------------------------*/
        /* QDU_OnNameChanged:                                                 */
        /*                                                                    */
        /* A name question's text changed (PromptForm.cs calls it, through    */
        /* PRM_BindDraft): kept as the draft.  Typing is not an answer --     */
        /* only the OK button answers.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sText : the text as it now is, empty included.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : kept, unless a newer display owns the draft.            */
        /*--------------------------------------------------------------------*/
        internal void QDU_OnNameChanged(string sText)
        {
            /* QST_StoreDraft takes the text while this display owns the      */
            /* draft -- even after an Exit has taken its right to answer --   */
            /* and refuses it once a newer display has adopted the draft.     */
            Qdu_objQuestion.QST_StoreDraft(Qdu_objDisplay, sText);
        }

        /*--------------------------------------------------------------------*/
        /* QDU_OnShownOffset:                                                 */
        /*                                                                    */
        /* The offset popup's Shown: the kept draft put back into its offset  */
        /* box, over whatever its preselected row put there.  An empty or     */
        /* invalid draft is put back as it is; the popup's OK still checks    */
        /* it.                                                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the popup, unused.                                 */
        /*     evShown   : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : put back, or nothing.                                   */
        /*--------------------------------------------------------------------*/
        internal void QDU_OnShownOffset(object objSender, EventArgs evShown)
        {
            if (Qdu_bRestore && Qdu_objQuestion.QST_IsCurrent(Qdu_objDisplay))
            {   /* A draft to put back, and this display is still current:     */
                /* its text put back exactly -- text only, which chooses no    */
                /* offset; the popup's own checks decide when OK is pressed.   */
                Qdu_dlgPartition.PRF_SetOffsetDraft(Qdu_sRestoredText);
            }
        }

        /*--------------------------------------------------------------------*/
        /* QDU_OnClosingOffset:                                               */
        /*                                                                    */
        /* The offset popup's FormClosing: its last text kept as the draft,   */
        /* before the modal call returns -- including when an Exit is taking  */
        /* the popup down, so a declined Exit shows the question again with   */
        /* what had been typed.  The close itself is not changed.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the popup, unused.                                 */
        /*     evClosing : the close; its Cancel is left as it is.            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : kept, unless a newer display owns the draft.            */
        /*--------------------------------------------------------------------*/
        internal void QDU_OnClosingOffset(object objSender, FormClosingEventArgs evClosing)
        {
            /* The popup's last text, handed to the question, which decides   */
            /* by draft ownership.  QST_IsCurrent is not asked: an Exit has   */
            /* already made this display not current, and its text would be   */
            /* lost.                                                          */
            Qdu_objQuestion.QST_StoreDraft(Qdu_objDisplay,
                Qdu_dlgPartition.PRF_ReadOffsetDraft());
        }
    }

    /*------------------------------------------------------------------------*/
    /* QSC_clsWindowScope -- one display's stretch of the UI call stack: the  */
    /* display, the one it is nested in, and the popups shown for it, so an   */
    /* Exit can close them (QWN_Supersede).  It holds no answer --            */
    /* documentation table.                                                   */
    /*------------------------------------------------------------------------*/
    internal sealed class QSC_clsWindowScope
    {
        internal PRS_clsPresentation QSC_objPresentation;  // the display, kept while its call is on the stack
        internal QSC_clsWindowScope QSC_objPrevious;       // the display it is nested in; null at the outermost
        internal List<Form> QSC_lstPopups;                 // the question popups shown for it (QWN_Attach) -- never an editor or a Windows dialog
        internal bool QSC_bEnded;                          // set by QWN_End; an ended record is skipped when the stack unwinds

        /*--------------------------------------------------------------------*/
        /* QSC_clsWindowScope:                                                */
        /*                                                                    */
        /* A display's record, made by QWN_Begin.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objPresentation : the display.                                 */
        /*     objPrevious     : the record it is nested in, or null.         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the record; QWN_End must end it once its call  */
        /*                     has returned.                                  */
        /*--------------------------------------------------------------------*/
        internal QSC_clsWindowScope(PRS_clsPresentation objPresentation, QSC_clsWindowScope objPrevious)
        {
            /* Both kept, even when the question is shown again under a new     */
            /* display: an older call keeps its own, so a late answer from it   */
            /* (a save, say) cannot answer the newer display.                   */
            QSC_objPresentation = objPresentation;
            QSC_objPrevious = objPrevious;

            /* No popups yet.  They are this display's alone; ending the      */
            /* record only forgets them -- whoever showed each disposes it.   */
            QSC_lstPopups = new List<Form>();
            QSC_bEnded = false;
        }
    }

    /*------------------------------------------------------------------------*/
    /* QWN_clsQuestionWindows -- the record of which question display is on   */
    /* the UI call stack now, of each question's newest display with its      */
    /* popups, and of the editor-modal close questions up for each question.  */
    /* UI thread only; the worker never reads it -- documentation table.      */
    /*------------------------------------------------------------------------*/
    internal static class QWN_clsQuestionWindows
    {
        [ThreadStatic]
        private static QSC_clsWindowScope Qwn_objCurrent; // the innermost display on this thread's stack (one per thread: [ThreadStatic]); null for none
        private static readonly Dictionary<QST_clsQuestion, QSC_clsWindowScope> Qwn_dictScopes =
            new Dictionary<QST_clsQuestion, QSC_clsWindowScope>(); // each question's newest live display record, removed when that call returns
        private static readonly Dictionary<QST_clsQuestion, List<Form>> Qwn_dictEditorModal =
            new Dictionary<QST_clsQuestion, List<Form>>(); // the editor-modal close questions up for each worker question (QWN_AttachEditorModal): they outlive the display's call, so its record cannot hold them; removed as each closes

        /*--------------------------------------------------------------------*/
        /* QWN_Begin:                                                         */
        /*                                                                    */
        /* A display's call begins: its record made, current, and the         */
        /* question's newest.  Popups shown from here on attach to it         */
        /* (QWN_Attach).                                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objPresentation : the display.                                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     QSC_clsWindowScope : the record; the caller ends it with       */
        /*                          QWN_End in a finally.                     */
        /*--------------------------------------------------------------------*/
        internal static QSC_clsWindowScope QWN_Begin(PRS_clsPresentation objPresentation)
        {
            QSC_clsWindowScope objScope;

            /* The record, nested in the current one, becomes the question's    */
            /* newest and the current.  A question shown again replaces the     */
            /* table's entry while its older call may still be returning --     */
            /* that older record stays on the stack, so its own end can clean   */
            /* it up without removing the newer one.                            */
            objScope = new QSC_clsWindowScope(objPresentation, Qwn_objCurrent);
            Qwn_dictScopes[objPresentation.PRS_objQuestion] = objScope;
            Qwn_objCurrent = objScope;

            /* The record, for the caller to end; no answer is made here. */
            return(objScope);
        }

        /*--------------------------------------------------------------------*/
        /* QWN_Attach:                                                        */
        /*                                                                    */
        /* A question popup about to be shown, attached to the current        */
        /* display so an Exit can close it.  Called by this file's renderers, */
        /* MessageForm.cs and the structured editor (JsonEditForm.cs) --      */
        /* never for the editor being closed itself.                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmPopup : the popup.                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : attached, or nothing outside a question's call.         */
        /*--------------------------------------------------------------------*/
        internal static void QWN_Attach(Form frmPopup)
        {
            if (Qwn_objCurrent != null)
            {   /* Inside a question's call: attached.  A popup shown outside   */
                /* one (an ordinary message) has nothing an Exit must close.    */
                Qwn_objCurrent.QSC_lstPopups.Add(frmPopup);
            }
        }

        /*--------------------------------------------------------------------*/
        /* QWN_CurrentEditorQuestion:                                         */
        /*                                                                    */
        /* The worker's close question showing for this editor, if any: the   */
        /* current display when it is the worker's EditorClose for this       */
        /* editor.  An editor's save captures it (OpenScan.cs OFD_ForSave),   */
        /* so the save goes to that question (EUI_Save), and each editor's    */
        /* closing handler takes it, so the close question the editor shows   */
        /* answers that question.                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lEditor : the editor's id.                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     PRS_clsPresentation : that display, or null for an ordinary    */
        /*                           save.                                    */
        /*--------------------------------------------------------------------*/
        internal static PRS_clsPresentation QWN_CurrentEditorQuestion(long lEditor)
        {
            if (Qwn_objCurrent != null &&
                Qwn_objCurrent.QSC_objPresentation.PRS_objQuestion.QST_enmKind == QNK_enmQuestionKind.EditorClose &&
                Qwn_objCurrent.QSC_objPresentation.PRS_objQuestion.QST_lWindow == lEditor)
            {   /* This editor's close question is on the stack: its display,   */
                /* captured now, before the save's own work can run a message   */
                /* loop in which an Exit replaces it.                           */
                return(Qwn_objCurrent.QSC_objPresentation);
            }

            /* No close question for this editor: an ordinary save, which   */
            /* answers no question.                                         */
            return(null);
        }

        /*--------------------------------------------------------------------*/
        /* QWN_AttachEditorModal:                                             */
        /*                                                                    */
        /* An editor's close question about to be shown EDITOR-MODAL          */
        /* (modeless over its editor) for the worker's close question: kept   */
        /* under that question, so an Exit taking its place can close it      */
        /* (QWN_Supersede).  Such a question outlives the call that showed    */
        /* it -- the coordinator's Close() returns while it is still up -- so */
        /* the display's own record (QWN_Attach) cannot hold it.  Called by   */
        /* each of the three editors; the editor detaches it when it has      */
        /* closed.                                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objPresentation : the worker's close question's display.       */
        /*     frmPopup        : the editor's question form.                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : kept.                                                   */
        /*--------------------------------------------------------------------*/
        internal static void QWN_AttachEditorModal(PRS_clsPresentation objPresentation, Form frmPopup)
        {
            List<Form> lstPopups;

            if (!Qwn_dictEditorModal.TryGetValue(objPresentation.PRS_objQuestion, out lstPopups))
            {   /* The question's first: its list made. */
                lstPopups = new List<Form>();
                Qwn_dictEditorModal.Add(objPresentation.PRS_objQuestion, lstPopups);
            }

            /* Kept until the editor detaches it. */
            lstPopups.Add(frmPopup);
        }

        /*--------------------------------------------------------------------*/
        /* QWN_DetachEditorModal:                                             */
        /*                                                                    */
        /* An editor-modal close question has closed: no longer kept, and a   */
        /* question left with none is forgotten.  Detaching one never kept    */
        /* does nothing.                                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objPresentation : the display it was attached under.           */
        /*     frmPopup        : the question form.                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : forgotten.                                              */
        /*--------------------------------------------------------------------*/
        internal static void QWN_DetachEditorModal(PRS_clsPresentation objPresentation, Form frmPopup)
        {
            List<Form> lstPopups;

            if (Qwn_dictEditorModal.TryGetValue(objPresentation.PRS_objQuestion, out lstPopups))
            {   /* Kept under the question: taken out, and the question's   */
                /* entry dropped once it holds none.                        */
                lstPopups.Remove(frmPopup);
                if (lstPopups.Count == 0)
                {   /* The last: the entry goes too. */
                    Qwn_dictEditorModal.Remove(objPresentation.PRS_objQuestion);
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* QWN_Supersede:                                                     */
        /*                                                                    */
        /* An Exit is being asked while this question waits: its newest       */
        /* display's popups are closed (QPR_Present), and so is every         */
        /* editor-modal close question shown for it (QWN_AttachEditorModal).  */
        /* Only those -- the question stays the worker's, suspended, and an   */
        /* Exit declined shows it again; an editor whose question was closed  */
        /* this way acts on no choice and is asked again then.                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objQuestion : the question interrupted, or null.               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : its popups asked to close, or nothing.                  */
        /*--------------------------------------------------------------------*/
        internal static void QWN_Supersede(QST_clsQuestion objQuestion)
        {
            QSC_clsWindowScope objScope;
            List<Form> lstEditorModal;
            Form[] afrmEditorModal;
            Form frmPopup;
            int i;

            if (objQuestion == null)
            {   /* No question interrupted: nothing to close. */
                return;
            }

            if (Qwn_dictScopes.TryGetValue(objQuestion, out objScope))
            {   /* One of its displays is on the stack: its popups, newest   */
                /* first; no editor is closed by this.                       */
                for (i = objScope.QSC_lstPopups.Count - 1; i >= 0; i--)
                {   /* One popup per iteration.  Its Close only takes it down --   */
                    /* whoever showed it still disposes it, and an answer from     */
                    /* it is refused, its display no longer being current.         */
                    frmPopup = objScope.QSC_lstPopups[i];
                    if (!frmPopup.IsDisposed)
                    {   /* Still standing: closed the ordinary way. */
                        frmPopup.Close();
                    }
                }
            }

            if (Qwn_dictEditorModal.TryGetValue(objQuestion, out lstEditorModal))
            {   /* Editor-modal close questions are up for it: each closed,     */
                /* from a copy of the list -- an editor detaches its question   */
                /* when its answer is applied, which is posted, but a copy      */
                /* keeps this loop safe whatever its FormClosed does.  The      */
                /* editor (its FormClosed, then its queue) finds its display    */
                /* no longer current and acts on no choice.                     */
                afrmEditorModal = lstEditorModal.ToArray();
                for (i = afrmEditorModal.Length - 1; i >= 0; i--)
                {   /* One question per iteration, newest first. */
                    frmPopup = afrmEditorModal[i];
                    if (!frmPopup.IsDisposed)
                    {   /* Still standing: closed the ordinary way. */
                        frmPopup.Close();
                    }
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* QWN_End:                                                           */
        /*                                                                    */
        /* A display's call has returned: its record ended, taken out of the  */
        /* table unless a newer display replaced it there, and the current    */
        /* record moved back to the nearest one still open.  An answered      */
        /* question's draft is cleared here too; an unanswered one keeps it   */
        /* for the display that shows it again.  Ending twice does nothing.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objScope : the record QWN_Begin returned; not null.            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : ended; no answer is made.                               */
        /*--------------------------------------------------------------------*/
        internal static void QWN_End(QSC_clsWindowScope objScope)
        {
            QSC_clsWindowScope objMapped;

            if (objScope.QSC_bEnded)
            {   /* Ended already (a finally and a clean-up both ending it):   */
                /* nothing more, so no other record is unwound.               */
                return;
            }

            /* Marked ended FIRST: a call nested inside this one may return   */
            /* after it, and must then skip this record as it unwinds.        */
            objScope.QSC_bEnded = true;
            if (Qwn_dictScopes.TryGetValue(objScope.QSC_objPresentation.PRS_objQuestion, out objMapped) &&
                objMapped == objScope)
            {   /* Still the question's entry: removed.  A display shown again   */
                /* since has a record of its own, left in place.                 */
                Qwn_dictScopes.Remove(objScope.QSC_objPresentation.PRS_objQuestion);
            }

            if (Qwn_objCurrent == objScope)
            {   /* It was the innermost: the current moves out to the nearest   */
                /* enclosing record that has not ended.                         */
                Qwn_objCurrent = objScope.QSC_objPrevious;
                while (Qwn_objCurrent != null && Qwn_objCurrent.QSC_bEnded)
                {   /* One enclosing record already ended per iteration:   */
                    /* skipped.                                            */
                    Qwn_objCurrent = Qwn_objCurrent.QSC_objPrevious;
                }
            }

            if (objScope.QSC_objPresentation.PRS_objQuestion.QST_ReadAnswer() != null)
            {   /* The question is answered (a cancel included, which needs no   */
                /* popup's reply): its draft is cleared.  A question suspended   */
                /* unanswered keeps its draft for the display shown next.        */
                objScope.QSC_objPresentation.PRS_objQuestion.QST_ClearDraft();
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* QPR_clsQuestionPresenter -- the presenter: one, made by MainCommand.cs */
    /* at start-up; UI thread only.  Its windows and fonts are borrowed, and  */
    /* none of them is permission for anything -- documentation table.        */
    /*------------------------------------------------------------------------*/
    internal sealed class QPR_clsQuestionPresenter
    {
        private const uint QPR_FAST_COMMIT = 0x0400;               // ext4's compat feature bit for fast_commit (EXT4_FEATURE_COMPAT_FAST_COMMIT, e2fsprogs ext2_fs.h), named in the first-write warning
        private readonly CLI_clsClient Qpr_objClient;              // the client: its copy of the worker's state (read for Close and Continue's target)
        private readonly EUI_clsEditorCoordinator Qpr_objEditors;  // the editors: owner lookups and the close / ready questions
        private readonly Form Qpr_frmMain;                         // the main window: the owner of an application-wide question
        private readonly Font Qpr_fntDialog;                       // the dialog font, borrowed
        private readonly Font Qpr_fntHeading;                      // the first-write warning's larger heading font, borrowed
        private readonly QPR_FindOwner Qpr_pfnOtherOwner;          // the main window's lookup for the check, partition and progress windows; null for none
        private bool Qpr_bFastCommit;                              // the open filesystem uses fast_commit (QPR_Opened); false until an open says so
        private bool Qpr_bFat;                                     // the open filesystem is FAT12 / 16 / 32 or exFAT, which has no journal (QPR_Opened); false until an open says so
        private readonly IWR_clsInputWindows Qpr_objInputs;        // the input registry: pickers as owners, and InputClose questions

        /*--------------------------------------------------------------------*/
        /* QPR_clsQuestionPresenter:                                          */
        /*                                                                    */
        /* The presenter, made once by the main window (MainCommand.cs) after */
        /* the editors, inputs and progress windows it looks owners up in.    */
        /* It starts no thread and calls no engine function: it only shows    */
        /* questions and answers them.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objClient     : the client, whose copy of the worker's state   */
        /*                     names what Close and Continue would close.     */
        /*     objEditors    : the editor coordinator.                        */
        /*     frmMain       : the main window.                               */
        /*     fntDialog     : the dialog font, borrowed.                     */
        /*     fntHeading    : the first-write warning's heading font,        */
        /*                     borrowed.                                      */
        /*     pfnOtherOwner : the main window's lookup for the check,        */
        /*                     partition and progress windows, or null.       */
        /*     objInputs     : the input registry, or null.  The parameter's  */
        /*                     default is never used: the one caller passes   */
        /*                     it, so the null tests below are guards.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the presenter.                                 */
        /*--------------------------------------------------------------------*/
        internal QPR_clsQuestionPresenter(CLI_clsClient objClient, EUI_clsEditorCoordinator objEditors,
            Form frmMain, Font fntDialog, Font fntHeading, QPR_FindOwner pfnOtherOwner, IWR_clsInputWindows objInputs = null)
        {
            /* Kept as given, every one borrowed: the windows and lookups stay   */
            /* on the UI thread, so a question the worker publishes carries      */
            /* only ids and values, and the worker never touches a control.      */
            Qpr_objClient = objClient;
            Qpr_objEditors = objEditors;
            Qpr_frmMain = frmMain;
            Qpr_fntDialog = fntDialog;
            Qpr_fntHeading = fntHeading;
            Qpr_pfnOtherOwner = pfnOtherOwner;
            Qpr_objInputs = objInputs;
        }

        /*--------------------------------------------------------------------*/
        /* QPR_Opened:                                                        */
        /*                                                                    */
        /* An open has completed: whether its filesystem uses fast_commit,    */
        /* and whether it is a FAT or exFAT one (no journal), are noted for   */
        /* the first-write warning's extra sentences.  Each open sets both    */
        /* afresh, so a filesystem opened later never inherits the last       */
        /* one's warning.                                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objOpened : the open's result, or null.                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the two flags set; nothing else.                        */
        /*--------------------------------------------------------------------*/
        internal void QPR_Opened(EOR_clsOpenResult objOpened)
        {
            /* The compat feature word's fast_commit bit; a null result reads   */
            /* as "no".  It only words a warning -- what may be written is      */
            /* decided by the engine's own feature and access checks.           */
            Qpr_bFastCommit = objOpened != null && (objOpened.EOR_stVolume.VLI_uiFeatureCompat & QPR_FAST_COMMIT) != 0;

            /* The engine's volume type, for the same warning; a null   */
            /* result reads as "not FAT".  Words only, as above.        */
            Qpr_bFat = objOpened != null &&
                (objOpened.EOR_stVolume.VLI_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_FAT12 ||
                objOpened.EOR_stVolume.VLI_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_FAT16 ||
                objOpened.EOR_stVolume.VLI_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_FAT32 ||
                objOpened.EOR_stVolume.VLI_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_EXFAT);
        }

        /*--------------------------------------------------------------------*/
        /* Qpr_FindOwner:                                                     */
        /*                                                                    */
        /* The window a question is shown over, looked up here on the UI      */
        /* thread from the id the question carries -- the worker never holds  */
        /* a window.  A question that names a window which has since gone     */
        /* finds none, and QPR_Present then answers it Cancel rather than     */
        /* showing it over some other window.                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objQuestion : the question.                                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Form : the live owner, borrowed, or null.                      */
        /*--------------------------------------------------------------------*/
        private Form Qpr_FindOwner(QST_clsQuestion objQuestion)
        {
            Form frmOwner;

            if (objQuestion.QST_enmKind == QNK_enmQuestionKind.ExitRunning && Qpr_objInputs != null)
            {   /* An Exit while a picker is open: the picker disables the       */
                /* windows under it, the main window included, so the question   */
                /* goes over the newest live picker (IWR_ExitOwner).  An Exit    */
                /* declined leaves that picker as it was, its typing kept.       */
                frmOwner = Qpr_objInputs.IWR_ExitOwner();
                if (frmOwner != null)
                {   /* A picker is open: it owns the Exit. */
                    return(frmOwner);
                }
            }

            if (objQuestion.QST_lWindow == 0)
            {   /* An application-wide question (window id 0): a copy's        */
                /* conflict, an option's warning, an Exit.  Tried in order:    */
                /* the progress window of the operation that asked, then the   */
                /* window an Exit would go over, then the main window -- so    */
                /* the answer is never 'no owner'.                             */
                frmOwner = null;
                if (Qpr_pfnOtherOwner != null && objQuestion.QST_objRequest != null)
                {   /* The asking operation's serial finds its own progress      */
                    /* window, if it has one (a copy's conflict goes over it).   */
                    frmOwner = Qpr_pfnOtherOwner(objQuestion.QST_objRequest.RQT_lSerial);
                }

                if (frmOwner == null && Qpr_pfnOtherOwner != null)
                {   /* Not found by serial: an option's warning or an Exit has   */
                    /* a serial of its own, not that of the operation whose      */
                    /* modal window now disables the main window.  Asked with    */
                    /* 0, the lookup answers the window the main window is       */
                    /* under -- a progress window, then the partition window     */
                    /* (MainCommand.cs Main_FindCommandOwner).  Being shown      */
                    /* over a window gives the question no say over that         */
                    /* window's work.                                            */
                    frmOwner = Qpr_pfnOtherOwner(0);
                }

                if (frmOwner == null)
                {   /* Nothing modal over the main window: it owns the   */
                    /* question.                                         */
                    frmOwner = Qpr_frmMain;
                }

                /* The owner, borrowed for this one display. */
                return(frmOwner);
            }

            /* A question naming a window, looked up in turn: an editor, a       */
            /* picker (a Save As's name or risk question goes over its picker,   */
            /* not the editor the picker disables), then the main window's       */
            /* check, partition or progress window.                              */
            frmOwner = Qpr_objEditors.EUI_FindOwner(objQuestion.QST_lWindow);
            if (frmOwner == null && Qpr_objInputs != null)
            {   /* Not an editor: a picker, while it is open. */
                frmOwner = Qpr_objInputs.IWR_FindInputOwner(objQuestion.QST_lWindow);
            }

            if (frmOwner == null && Qpr_pfnOtherOwner != null)
            {   /* Not a picker either: the check, partition or progress   */
                /* window that id names.                                   */
                frmOwner = Qpr_pfnOtherOwner(objQuestion.QST_lWindow);
            }

            /* The owner, or null when the named window has gone -- never a   */
            /* substitute, since a question shown over another window would   */
            /* seem to belong to it.                                          */
            return(frmOwner);
        }

        /*--------------------------------------------------------------------*/
        /* Qpr_TargetText:                                                    */
        /*                                                                    */
        /* The lines a warning ends with, naming exactly what would be        */
        /* written: the device's description (for a device), the path, and    */
        /* the byte range -- the target as the request captured it, never     */
        /* the pane's current one.  Display text only; nothing writes to it.  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objTarget : the request's target, or null.                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the lines, or "(no filesystem target)" for null.      */
        /*--------------------------------------------------------------------*/
        private static string Qpr_TargetText(TGT_clsTarget objTarget)
        {
            string sDevice;
            string sLength;

            if (objTarget == null)
            {   /* No target: said in the text, so a warning never reads as   */
                /* if it named one.                                           */
                return("(no filesystem target)");
            }

            /* A device's description (its model, size and bus) goes on a    */
            /* line of its own above the path; an image file has none, and   */
            /* gets no empty line.                                           */
            sDevice = "";
            if (objTarget.TGT_sDeviceText.Length != 0)
            {   /* A device: its description, then the line break. */
                sDevice = objTarget.TGT_sDeviceText + "\n";
            }

            /* The length: 0 means "from the offset to the end", not an   */
            /* empty range, and is worded so.                             */
            sLength = "to the end";
            if (objTarget.TGT_ullLength != 0)
            {   /* A given length: in bytes, exact. */
                sLength = objTarget.TGT_ullLength + " bytes";
            }

            /* The description, the path, then the offset and length in   */
            /* bytes.                                                     */
            return(sDevice + objTarget.TGT_sPath + "\nOffset: " + objTarget.TGT_ullOffset +
                " bytes; length: " + sLength);
        }

        /*--------------------------------------------------------------------*/
        /* Qpr_Warning:                                                       */
        /*                                                                    */
        /* The text of a Yes / No question, from what the request captured    */
        /* when it was made (and, for Close and Continue, the worker's state  */
        /* as the client last copied it) -- never from the panes as they are  */
        /* now.  An empty string means the question cannot be worded from     */
        /* what it carries (a kind this function does not know, or facts      */
        /* missing), and QPR_Present then answers Cancel without showing      */
        /* anything: a vague Yes / No must never stand in for a destructive   */
        /* question.                                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objQuestion : the question.                                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the whole question's text, or "".                     */
        /*--------------------------------------------------------------------*/
        private string Qpr_Warning(QST_clsQuestion objQuestion)
        {
            ELA_clsElevationArgs objElevation;
            RQT_clsRequest objRequest;
            TGT_clsTarget objTarget;
            VFA_clsViewerFallbackArgs objFallback;
            VCH_clsViewerChoice objChoice;
            PAA_clsPartitionArgs objPartition;
            EDA_clsEditArgs objEdit;
            int iDirectories;
            int i;
            string sContext;
            string sWhat;
            string sWarning;

            /* The request and its target, taken once for every case below.   */
            /* Close and Continue is the exception: it names the filesystem   */
            /* being CLOSED, read from the state in its own case, not the     */
            /* target the request would open next.                            */
            objRequest = objQuestion.QST_objRequest;
            objTarget = null;
            if (objRequest != null)
            {   /* A request: its target as captured. */
                objTarget = objRequest.RQT_objTarget;
            }

            /* A partition window's request also carries the window's own       */
            /* words: the partition's short name (PAA_sPlace) and the range     */
            /* with its size and what it holds (PAA_sTarget).  They head a      */
            /* warning, while the exact range in Qpr_TargetText's lines stays   */
            /* the fact.  Null for every other request.                         */
            objPartition = null;
            if (objRequest != null)
            {   /* A request: its partition arguments, if those are what it   */
                /* carries.                                                   */
                objPartition = objRequest.RQT_objPayload as PAA_clsPartitionArgs;
            }

            switch (objQuestion.QST_enmKind)
            {   /* One case per kind of Yes / No question. */
                case QNK_enmQuestionKind.DisableRiskWarnings:
                    /* Turning the risk warnings off, first of two questions:   */
                    /* what they guard against.                                 */
                    return("Turn off the risk warnings?\n\n" +
                        "You will no longer be asked before the first change to an image or a device, or warned " +
                        "before the partition and check windows open.\n" +
                        "Those are the places where one mistake can destroy everything on a card or an image, " +
                        "with no way back.\n\nTurn the warnings off?");
                case QNK_enmQuestionKind.ConfirmDisableRiskWarnings:
                    /* The second question, asked only after a Yes to the   */
                    /* first: the warnings go off only when both are Yes.   */
                    return("Just to be sure: with the warnings off, changes to images and devices " +
                        "go ahead without a word.\n\n" +
                        "They can be turned back on at any time from this menu.\n\n" +
                        "Are you sure you want them off?");
                case QNK_enmQuestionKind.DeleteEntries:
                    /* Deleting from the image.  The count comes from the    */
                    /* request's own lists -- the paths and, beside each,    */
                    /* whether it was a folder when selected -- never from   */
                    /* the pane's rows, which may now show another folder.   */
                    objEdit = null;
                    if (objRequest != null)
                    {   /* A request: its edit arguments, which hold the   */
                        /* folder flags paired with its paths.             */
                        objEdit = objRequest.RQT_objPayload as EDA_clsEditArgs;
                    }

                    if (objEdit == null || objEdit.EDA_enmKind != EDK_enmEditKind.Delete ||
                        objRequest.RQT_lstPaths.Count == 0 ||
                        objEdit.EDA_lstDirectories.Count != objRequest.RQT_lstPaths.Count)
                    {   /* Not a delete's arguments, nothing to delete, or the   */
                        /* two lists out of step: the question cannot be         */
                        /* worded truthfully, so "" -- answered Cancel, and      */
                        /* nothing is deleted.                                   */
                        return("");
                    }

                    /* How many of the items are folders, which the warning    */
                    /* must call out: a folder is deleted with everything in   */
                    /* it.                                                     */
                    iDirectories = 0;
                    for (i = 0; i < objEdit.EDA_lstDirectories.Count; i++)
                    {   /* One selected item per iteration. */
                        if (objEdit.EDA_lstDirectories[i])
                        {   /* A folder: counted. */
                            iDirectories++;
                        }
                    }

                    /* The question with the count; QPR_Present gives it No   */
                    /* as the default.                                        */
                    sWarning = "Delete " + objRequest.RQT_lstPaths.Count + " item(s) from the image?";
                    if (iDirectories != 0)
                    {   /* Folders among them: said, with what that means. */
                        sWarning += "\n\n" + iDirectories +
                            " of them are directories, which are deleted WITH everything inside them.";
                    }

                    /* And that the delete is for good -- the image has no   */
                    /* Recycle Bin.                                          */
                    return(sWarning + "\n\nThis cannot be undone.");
                case QNK_enmQuestionKind.CloseAndContinue:
                    /* Closing the open filesystem so another operation can    */
                    /* go on: it names the filesystem being closed, from the   */
                    /* state as the client last copied it, and says the        */
                    /* editors will be asked first.                            */
                    return("Close the open filesystem and continue?\n\n" +
                        Qpr_TargetText(Qpr_objClient.CLI_ReadState().STV_objTarget) +
                        "\n\nEach open editor will first be asked to save, discard or cancel.");
                case QNK_enmQuestionKind.ExitRunning:
                    /* An Exit asked while work is queued or running -- or      */
                    /* the restart as Administrator, which is the same Exit     */
                    /* carrying the device action that needs it.  No restores   */
                    /* the question it took down, or lets the work go on.       */
                    objElevation = null;
                    if (objRequest != null)
                    {   /* A request: the restart's arguments, if that is what   */
                        /* it carries (a plain Exit carries none).               */
                        objElevation = objRequest.RQT_objPayload as ELA_clsElevationArgs;
                    }

                    if (objElevation != null)
                    {   /* The restart: what needs the rights, and the order    */
                        /* it goes in -- the editors asked, the filesystem      */
                        /* closed, then Windows' elevation prompt -- so a       */
                        /* declined prompt leaves this window open with those   */
                        /* closed, as the text warns.                           */
                        return(objElevation.ELA_sWhat + " needs Administrator rights, which this program was not started with.\n\n" +
                            "Restart BifrostFM as Administrator and stop pending or in-process operations?\n\n" +
                            "Editors will first be asked to save, discard or cancel, and the filesystem will close before Windows asks for elevation.\n" +
                            "If elevation is declined, this window stays open with those resources closed.\n\nRestart now?");
                    }

                    /* A plain Exit: one answer for the queued work and the   */
                    /* running work alike; what has already been committed    */
                    /* stays.                                                 */
                    return("Exit BifrostFM and stop pending or in-process operations?\n\n" +
                        "No restores the interrupted question or continues the operation. " +
                        "Changes already committed remain in place.");
                case QNK_enmQuestionKind.Risk:
                    /* The risk warnings: before the partition window opens,   */
                    /* before the check window opens, and before the first     */
                    /* change to a filesystem.  Which one is told by the       */
                    /* request's kind.                                         */
                    if (objRequest != null && (objRequest.RQT_enmKind == KND_enmKind.OpenPartition || objRequest.RQT_enmKind == KND_enmKind.NewImage))
                    {   /* The partition window (a new image goes there too):   */
                        /* what it can do, then the target.                     */
                        return("Partitioning and formatting can destroy data on the selected file or device.\n\n" +
                            Qpr_TargetText(objTarget) + "\n\nContinue to the partition window?");
                    }

                    if (objRequest != null && objRequest.RQT_enmKind == KND_enmKind.Check)
                    {   /* The check window: its first check only reports, but   */
                        /* its Repair button changes the filesystem, which is    */
                        /* what this warns of.  The Repair sentence starts a     */
                        /* line of its own, as a following sentence that would   */
                        /* wrap does in every dialog.                            */
                        return("The check window can check and repair the selected filesystem.\nRepair changes its contents.\n\n" +
                            Qpr_TargetText(objTarget) + "\n\nContinue to the check window?");
                    }

                    /* Otherwise the first change to the open filesystem,       */
                    /* which reopens it for writing.  The fast_commit           */
                    /* sentence is added only when the open said the feature    */
                    /* is on (QPR_Opened), warning that Linux may not be able   */
                    /* to replay an interrupted write; why lwext4's writes      */
                    /* and fast_commit's replay disagree is not established     */
                    /* here.  A FAT or exFAT filesystem (QPR_Opened) gets a     */
                    /* sentence of its own: it has no journal at all, so an     */
                    /* interrupted write is not replayed by anything.  Each     */
                    /* selection is the allowed literal ternary inside a        */
                    /* concatenation.                                           */
                    return("Changing will WRITE to this filesystem:\n\n" + Qpr_TargetText(objTarget) +
                        "\n\nKeep a backup and make sure this is the intended target." +
                        (Qpr_bFastCommit ? "\n\nThis filesystem uses fast_commit; an interrupted write here may not be replayable by Linux." : "") +
                        (Qpr_bFat ? "\n\nThis is a FAT filesystem, which has no journal: a write interrupted here (a crash, the card pulled out) can leave lost space or a damaged folder for a check on Windows or Linux to repair." : "") +
                        "\n\nReopen it for writing and continue?");
                case QNK_enmQuestionKind.WriteTable:
                    /* Writing the partition table.  Named by the window's     */
                    /* own word for the disk, or "the selected disk" when it   */
                    /* gave none.                                              */
                    sWhat = "the selected disk";
                    if (objPartition != null && objPartition.PAA_sPlace.Length != 0)
                    {   /* The window named it when the request was made. */
                        sWhat = objPartition.PAA_sPlace;
                    }

                    /* What a table write does NOT do is the warning: the       */
                    /* partitions' contents stay where they are, so a deleted   */
                    /* or moved partition loses what it held, and one added     */
                    /* over old data still finds that data.                     */
                    sWarning = "WRITE this partition table to " + sWhat + "?\n\nOnly the table is written: nothing inside a partition is moved or " +
                        "erased by it, so a partition deleted or moved here loses what it held, and one added over old data finds that data still there.";
                    if (objTarget != null && objTarget.TGT_sDeviceText.Length != 0)
                    {   /* A device (it has a description): its volumes are     */
                        /* locked and dismounted for the write and Windows is   */
                        /* told to re-read the table, and the user is asked     */
                        /* once more whether it is the right device.            */
                        sWarning += "\n\nEvery volume on the device is locked and dismounted while it is written, and Windows is asked to " +
                            "re-read the table afterwards.\n\nAre you sure this is the right device?";
                    }

                    /* The exact target under the words. */
                    return(sWarning + "\n\n" + Qpr_TargetText(objTarget));
                case QNK_enmQuestionKind.Format:
                    /* Making a filesystem.  A format from the partition      */
                    /* window carries the window's description of the range   */
                    /* (PAA_sTarget, its size and what it holds), captured    */
                    /* before its options dialog ran.                         */
                    if (objTarget != null && objPartition != null && !String.IsNullOrEmpty(objPartition.PAA_sTarget))
                    {   /* Described: the question by that description, then   */
                        /* the exact range.                                    */
                        sWarning = "FORMAT " + objPartition.PAA_sTarget + "?\n\nEverything in that partition will be lost.";
                        if (objTarget.TGT_sDeviceText.Length != 0)
                        {   /* A device: the loss said in capitals, the volume   */
                            /* lock, and the right-device question -- this       */
                            /* text replaces the file's, not adds to it.         */
                            sWarning = "FORMAT " + objPartition.PAA_sTarget + "?\n\nEVERYTHING IN THAT PARTITION WILL BE LOST.\n" +
                                "Every volume on the device is locked and dismounted while it is written.\n\n" +
                                "Are you sure this is the right device?";
                        }

                        /* The exact range under the words -- the one that     */
                        /* will be written, which for a whole disk formatted   */
                        /* smaller than the disk is shorter than the disk.     */
                        return(sWarning + "\n\n" + Qpr_TargetText(objTarget));
                    }

                    /* Not described (no partition arguments, or no            */
                    /* description): the question by the range itself, which   */
                    /* is what is destroyed.                                   */
                    return("Make a filesystem in the selected range?\n\n" + Qpr_TargetText(objTarget) +
                        "\n\nExisting contents in that range will be destroyed.");
                case QNK_enmQuestionKind.GrowPartition:
                    /* Expand over free space: the sequence words it, with    */
                    /* the partition's number and the sizes it knows; "" (a   */
                    /* question without its text) is answered Cancel.         */
                    if (!(objQuestion.QST_objContext is string))
                    {   /* No text: nothing to ask. */
                        return("");
                    }

                    /* The sequence's own sentence. */
                    return((string)objQuestion.QST_objContext);
                case QNK_enmQuestionKind.DiscardEdits:
                    /* Refresh with unwritten edits: the read throws them   */
                    /* away, as Revert does, so it asks first (No the       */
                    /* default, set where the box is shown).                */
                    return("The table has changes that are not written.\nDiscard them and read the disk again?");
                case QNK_enmQuestionKind.ErasePartition:
                    /* Erasing a partition: a plain confirmation, not a        */
                    /* second risk warning -- the partition window's own       */
                    /* warning on opening is the serious one.  What is         */
                    /* erased, by its captured name, size and contents, then   */
                    /* the exact range.                                        */
                    if (objRequest == null || !(objRequest.RQT_objPayload is PRA_clsPartitionRawArgs))
                    {   /* No captured partition to name: "" -- answered   */
                        /* Cancel, and nothing is erased.                  */
                        return("");
                    }

                    /* The question by the partition's captured description --     */
                    /* its name, size and contents as the window saw them at the   */
                    /* click -- then what an erase does, and for a device the      */
                    /* lock it takes.                                              */
                    sWarning = "Erase " + ((PRA_clsPartitionRawArgs)objRequest.RQT_objPayload).PRA_sTarget + "?\n\n" +
                        "Every byte of it is overwritten with zeros, and what it holds is lost.";
                    if (objTarget != null && objTarget.TGT_sDeviceText.Length != 0)
                    {   /* A device: its volumes are locked while it is written. */
                        sWarning += "\nEvery volume on the device is locked and dismounted while it is written.";
                    }

                    /* The exact coordinates under the words. */
                    return(sWarning + "\n\n" + Qpr_TargetText(objTarget));
                case QNK_enmQuestionKind.TestCapacity:
                    /* The capacity test of a whole device. */
                    if (objTarget == null)
                    {   /* No device named: "" -- answered Cancel, and   */
                        /* nothing is written.                           */
                        return("");
                    }

                    /* The device by its description when it has one, else   */
                    /* by its path.                                          */
                    sWhat = objTarget.TGT_sPath;
                    if (objTarget.TGT_sDeviceText.Length != 0)
                    {   /* Described: the description as captured, for   */
                        /* display only.                                 */
                        sWhat = objTarget.TGT_sDeviceText;
                    }

                    if (!String.Equals(sWhat, objTarget.TGT_sPath, StringComparison.OrdinalIgnoreCase) &&
                        !sWhat.EndsWith(" (" + objTarget.TGT_sPath + ")", StringComparison.OrdinalIgnoreCase))
                    {   /* A description that does not already end in the       */
                        /* path gets it added in brackets: a model name alone   */
                        /* could hide which device is about to be written.      */
                        sWhat += " (" + objTarget.TGT_sPath + ")";
                    }

                    /* What the test does to the device -- blocks written and   */
                    /* read back across it, the old contents put back -- and    */
                    /* what an interruption leaves, before any block is         */
                    /* written.  The number of places in the text is the        */
                    /* engine's BFC_SAMPLES (bfe_capacity.c), written in as a   */
                    /* number: this text must change with it.                   */
                    return("Test how much of " + sWhat + " really holds data?\n\n" +
                        "A small test block is written to 64 places across the whole device and read back, and what was " +
                        "there is put back afterwards; it takes a few seconds and changes nothing on the device.\n\n" +
                        "Keep a backup of anything you cannot lose: if the program or the power stops during the test, a " +
                        "test block may be left where data was.\n\nRun the test?");
                case QNK_enmQuestionKind.Repair:
                    /* A change by e2fsck or resize2fs: Expand Filesystem, or   */
                    /* the check window's Repair.                               */
                    if (objRequest != null && objRequest.RQT_enmKind == KND_enmKind.ExpandFilesystem && objTarget != null)
                    {   /* Expand Filesystem: one Yes covers the grow AND the   */
                        /* check and repair that come first when the            */
                        /* filesystem needs them.  Growing the partition in     */
                        /* the table is not part of it -- that is the           */
                        /* window's own earlier step.                           */
                        sWhat = objTarget.TGT_sPath;
                        if (objPartition != null && objPartition.PAA_sPlace.Length != 0)
                        {   /* The window's name for the partition, captured   */
                            /* with the request.                               */
                            sWhat = objPartition.PAA_sPlace;
                        }

                        /* The size it grows to is the range's length, which   */
                        /* the window always gives -- the partition's or the   */
                        /* whole disk's size (CommandOperations.cs,            */
                        /* Sys_TableEditSequence) -- so it is never the 0 of   */
                        /* "to the end".                                       */
                        return("Grow the filesystem in " + sWhat + " to fill all " +
                            DVF_clsDeviceForm.DVF_SizeText(objTarget.TGT_ullLength) + "?\n\nIts files are kept.  " +
                            "If the filesystem has not been checked since it was last used, it is checked and repaired first.  " +
                            "Keep a backup of anything you cannot lose.\n\n" + Qpr_TargetText(objTarget));
                    }

                    /* The check window's Repair: e2fsck run answering yes to   */
                    /* every repair it proposes, so the warning says so.        */
                    return("Repair this filesystem?\n\n" + Qpr_TargetText(objTarget) +
                        "\n\ne2fsck will answer YES to its repair questions. Keep a backup before continuing.");
                case QNK_enmQuestionKind.WriteDevice:
                    /* An image written to a device: worded from the image    */
                    /* file's facts read in this run (its format and sizes)   */
                    /* and the device's captured description, by              */
                    /* QPR_RawWriteWarning; "" from it when either is         */
                    /* missing.                                               */
                    return(QPR_RawWriteWarning(objRequest, objQuestion.QST_objContext as RWF_clsRawWriteFacts));
                case QNK_enmQuestionKind.Overwrite:
                    /* An editor's Save As onto a file that exists in the     */
                    /* image.  The context is the destination's path, asked   */
                    /* only after the engine's create has reported that it    */
                    /* exists (CommandNativeSteps.cs).                        */
                    sContext = objQuestion.QST_objContext as string;
                    if (sContext == null)
                    {   /* None given: the question goes without it, rather   */
                        /* than reading anything from the panes.              */
                        sContext = "";
                    }

                    /* The question, the destination on its own line. */
                    return("Replace the existing image file entry\n" + sContext + "?");
                case QNK_enmQuestionKind.ViewerFallback:
                    /* The structured editor refused a file: offer the text   */
                    /* editor instead.  The format and the parser's reason    */
                    /* come from the request, never from the pane.            */
                    objFallback = null;
                    if (objRequest != null)
                    {   /* A request: its fallback arguments, if those are   */
                        /* what it carries.                                  */
                        objFallback = objRequest.RQT_objPayload as VFA_clsViewerFallbackArgs;
                    }

                    if (objFallback == null || objRequest.RQT_lstPaths.Count != 1)
                    {   /* No refusal recorded, or not exactly one file: ""   */
                        /* -- answered Cancel, and no editor opens.           */
                        return("");
                    }

                    /* The file, the format that refused it, and the           */
                    /* parser's words (with the line and column when it gave   */
                    /* them).  Only a Yes opens anything: an image file is     */
                    /* then read out afresh, a Windows file opened where it    */
                    /* stands (CommandOperations.cs                            */
                    /* Sys_ViewerFallbackSequence).                            */
                    return(objRequest.RQT_lstPaths[0] + " is not " + VoorheesFormat.Vf_Name(objFallback.VFA_iFormat) +
                        " the editor can open.\n\n" + objFallback.VFA_sReason + "\n\nOpen it in the text editor instead?");
                case QNK_enmQuestionKind.ViewerLargeText:
                    /* A file too large for the text or structured editor:      */
                    /* offer the hex editor.  The size is the read-out's own,   */
                    /* measured by the worker; the limit is                     */
                    /* OSC_TEXT_MAX_BYTES, the one the worker tested it         */
                    /* against for both editors (CommandOperations.cs).         */
                    objChoice = objQuestion.QST_objContext as VCH_clsViewerChoice;
                    if (objChoice == null || objRequest == null || objRequest.RQT_lstPaths.Count != 1)
                    {   /* No size, no request, or not exactly one file: ""   */
                        /* -- answered Cancel, and no editor opens.           */
                        return("");
                    }

                    /* The entry's path as captured, its size and the limit,   */
                    /* both with thousands separators ("N0").                  */
                    return(objRequest.RQT_lstPaths[0] + " is " +
                        objChoice.VCH_lBytes.ToString("N0") + " bytes, more than the text and structured editors take (" +
                        OSC_clsScanner.OSC_TEXT_MAX_BYTES.ToString("N0") +
                        ").\n\nOpen it in the hex editor instead?");
                case QNK_enmQuestionKind.ReplaceChecksum:
                    /* A device write's sum: a checksum file of the image's       */
                    /* name is already beside it (Nst_Raw,                        */
                    /* CommandNativeOperations.cs).  The context is that file's   */
                    /* path; without it the question cannot be worded, and the    */
                    /* Cancel it then gets keeps the file.                        */
                    sContext = objQuestion.QST_objContext as string;
                    if (String.IsNullOrEmpty(sContext))
                    {   /* No path: "" -- answered Cancel, nothing replaced. */
                        return("");
                    }

                    /* Why it is asked: beside a downloaded image that file is   */
                    /* usually the publisher's own sum, the one to check the     */
                    /* image against -- replaced by the sum of a damaged         */
                    /* download, a later check would pass.                       */
                    return("Replace the existing checksum file\n" + sContext + "\nwith the sum of the image just written?\n\n" +
                        "It may be the image publisher's own sum, kept to check the image against.\n" +
                        "No keeps it; the sum just computed is still shown.");
                default:
                    /* A kind this function does not word: "" -- answered   */
                    /* Cancel, so no unknown question can be given a Yes.   */
                    return("");
            }
        }

        /*---------------------------------------------------------------------*/
        /* Qpr_BinaryViewer:                                                   */
        /*                                                                     */
        /* The binary-file question: the file looks binary, so open it in      */
        /* the hex editor, in the text editor anyway (offered only when it is  */
        /* small enough), or not at all.  The dialog is given only words and   */
        /* whether to offer Text: the read-out file stays the worker's, so     */
        /* this question holds nothing an Exit would have to close.  No        */
        /* editor is opened here -- the worker does that after the answer.     */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     frmOwner        : the window it is shown over.                  */
        /*     objArgs         : the file's name, why it was called binary,    */
        /*                       and whether Text is offered; or null.         */
        /*     objPresentation : this display, which alone may answer.         */
        /*                                                                     */
        /* Returns:                                                            */
        /*     ANK_enmAnswerKind : OpenText, OpenHex, or Cancel (the dialog    */
        /*                         closed, no facts, or a superseded display). */
        /*---------------------------------------------------------------------*/
        private ANK_enmAnswerKind Qpr_BinaryViewer(Form frmOwner, VCH_clsViewerChoice objArgs,
            PRS_clsPresentation objPresentation)
        {
            BNY_clsBinaryForm dlgForm;

            if (objArgs == null)
            {   /* No facts: no editor may be chosen for it, so Cancel. */
                return(ANK_enmAnswerKind.Cancel);
            }

            /* The dialog, with the name, the reason and the Text offer --   */
            /* strings and a flag only.                                      */
            dlgForm = new BNY_clsBinaryForm(Qpr_fntDialog,
                objArgs.VCH_sName, objArgs.VCH_sReason, objArgs.VCH_bAllowText);
            using (dlgForm)
            {   /* The dialog is disposed when the block ends, after its     */
                /* answer is read.  It takes the owner's icon, and is        */
                /* attached to this display BEFORE it is shown, so an Exit   */
                /* closes it and its late answer, coming to a display no     */
                /* longer current, is refused.                               */
                dlgForm.Icon = frmOwner.Icon;
                QWN_clsQuestionWindows.QWN_Attach(dlgForm);
                if (!objPresentation.PRS_objQuestion.QST_IsCurrent(objPresentation))
                {   /* An Exit replaced this display while the dialog was       */
                    /* being made: it is never shown (the block disposes it),   */
                    /* and the Cancel returned is refused like any stale        */
                    /* answer.                                                  */
                    return(ANK_enmAnswerKind.Cancel);
                }

                /* Shown and waited for; then its button read, before the   */
                /* block disposes it.                                       */
                dlgForm.ShowDialog(frmOwner);
                switch (dlgForm.BNY_ReadChoice())
                {   /* One case per button. */
                    case BNY_clsBinaryForm.BNY_CHOICE_TEXT:
                        /* Text -- honoured only if it was offered: the    */
                        /* worker refuses an unoffered Text too, but the   */
                        /* answer here never claims one.                   */
                        if (objArgs.VCH_bAllowText)
                        {   /* Offered: the text editor. */
                            return(ANK_enmAnswerKind.OpenText);
                        }

                        /* Not offered: no editor. */
                        return(ANK_enmAnswerKind.Cancel);
                    case BNY_clsBinaryForm.BNY_CHOICE_HEX:
                        /* Hex: the hex editor, which has no size limit like   */
                        /* the text editor's (OSC_TEXT_MAX_BYTES).             */
                        return(ANK_enmAnswerKind.OpenHex);
                    default:
                        /* Cancel, the dialog closed, or anything else: no   */
                        /* editor.                                           */
                        return(ANK_enmAnswerKind.Cancel);
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* Qpr_Conflict:                                                      */
        /*                                                                    */
        /* A copy's conflict -- the destination already exists -- asked with  */
        /* the copy dialog's five answers.  The words are the two paths the   */
        /* engine's event named, copied out of it; no engine pointer reaches  */
        /* here.                                                              */
        /*                                                                    */
        /* Unlike the binary, name and offset dialogs, this one is not tested */
        /* for currency again before it is shown: nothing between             */
        /* QPR_Present's own test and the ShowDialog runs a message loop (no  */
        /* ShowDialog or DoEvents is reached), so that test still holds.      */
        /* Qpr_Clash is the same.                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmOwner : the window it is shown over (the copy's progress    */
        /*                window, as a rule -- Qpr_FindOwner).                */
        /*     objData  : the conflict's event, copied; or null.              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ANK_enmAnswerKind : Overwrite, OverwriteAll, Skip, SkipAll, or */
        /*                         Cancel (the dialog closed, or no event).   */
        /*--------------------------------------------------------------------*/
        private ANK_enmAnswerKind Qpr_Conflict(Form frmOwner, PGD_clsProgressData objData)
        {
            CFL_clsConflictForm dlgForm;

            if (objData == null)
            {   /* No event: nothing to name, and nothing may be   */
                /* overwritten, so Cancel.                         */
                return(ANK_enmAnswerKind.Cancel);
            }

            /* The dialog, with the Windows path above the image path.  It   */
            /* hides its icon (CopyForms.cs), so none is set here.           */
            dlgForm = new CFL_clsConflictForm(Qpr_fntDialog, objData.PGD_sHost + "\n" + objData.PGD_sImage);
            using (dlgForm)
            {   /* The dialog is disposed when the block ends.  Attached   */
                /* first, so an Exit closes it -- it alone: the progress   */
                /* window behind it stays.                                 */
                QWN_clsQuestionWindows.QWN_Attach(dlgForm);
                dlgForm.ShowDialog(frmOwner);
                switch (dlgForm.CFL_ReadChoice())
                {   /* One case per button. */
                    case CFL_clsConflictForm.CFL_CHOICE_OVERWRITE:
                        /* This destination only; the next conflict asks   */
                        /* again.                                          */
                        return(ANK_enmAnswerKind.Overwrite);
                    case CFL_clsConflictForm.CFL_CHOICE_OVERWRITE_ALL:
                        /* This one and every later conflict of this copy:   */
                        /* the worker keeps the answer for them.             */
                        return(ANK_enmAnswerKind.OverwriteAll);
                    case CFL_clsConflictForm.CFL_CHOICE_SKIP:
                        /* This destination kept, the copy going on. */
                        return(ANK_enmAnswerKind.Skip);
                    case CFL_clsConflictForm.CFL_CHOICE_SKIP_ALL:
                        /* This one and every later conflict of this copy   */
                        /* kept.                                            */
                        return(ANK_enmAnswerKind.SkipAll);
                    default:
                        /* Cancel, the dialog closed, or anything else: the   */
                        /* copy stops, nothing overwritten.                   */
                        return(ANK_enmAnswerKind.Cancel);
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* Qpr_Clash:                                                         */
        /*                                                                    */
        /* An extract with the metadata file meets an image file of the same  */
        /* name (_ext4meta.txt) at the tree's top: keep that file, or keep    */
        /* the metadata -- both cannot be written.  The words are the event's */
        /* two paths, copied out of it.  Not re-tested for currency before    */
        /* it is shown, for Qpr_Conflict's reason.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmOwner : the window it is shown over.                        */
        /*     objData  : the clash's event, copied; or null.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ANK_enmAnswerKind : KeepFile, KeepManifest, or Cancel (the     */
        /*                         dialog closed, or no event).               */
        /*--------------------------------------------------------------------*/
        private ANK_enmAnswerKind Qpr_Clash(Form frmOwner, PGD_clsProgressData objData)
        {
            CLH_clsClashForm dlgForm;

            if (objData == null)
            {   /* No event: nothing to name, so Cancel -- which writes   */
                /* neither.                                               */
                return(ANK_enmAnswerKind.Cancel);
            }

            /* The dialog, with the image file's path and the metadata's   */
            /* destination.  It hides its icon (CopyForms.cs).             */
            dlgForm = new CLH_clsClashForm(Qpr_fntDialog, objData.PGD_sImage, objData.PGD_sHost);
            using (dlgForm)
            {   /* The dialog is disposed when the block ends; attached   */
                /* first, so an Exit closes it.                           */
                QWN_clsQuestionWindows.QWN_Attach(dlgForm);
                dlgForm.ShowDialog(frmOwner);
                switch (dlgForm.CLH_ReadChoice())
                {   /* One case per button; the worker turns the answer     */
                    /* into the engine's reply (CommandNativeControl.cs).   */
                    case CLH_clsClashForm.CLH_KEEP_FILE:
                        /* The image file is extracted; no metadata file is   */
                        /* written.                                           */
                        return(ANK_enmAnswerKind.KeepFile);
                    case CLH_clsClashForm.CLH_KEEP_MANIFEST:
                        /* The metadata is written; that image file is not   */
                        /* extracted.                                        */
                        return(ANK_enmAnswerKind.KeepManifest);
                    default:
                        /* Cancel, the dialog closed, or anything else: the   */
                        /* copy is cancelled, neither written.                */
                        return(ANK_enmAnswerKind.Cancel);
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* Qpr_MakeFsOptions:                                                 */
        /*                                                                    */
        /* The Make Filesystem options dialog, shown for the format's own     */
        /* sequence (the MakeFsOptions question): the New Image dialog in its */
        /* fixed-target mode over the partition window -- the range's name    */
        /* read-only, its size fixed (or, for a whole disk with no table,     */
        /* allowed smaller), and the type, block size, label and zero-fill    */
        /* to choose.  It only shows and reads; the format, its warning and   */
        /* its progress are the sequence's, once this answers.                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmOwner    : the partition window.                            */
        /*     objQuestion : the question: the range is its request's target, */
        /*                   its words the context (FMI_clsFormatInput).      */
        /*     objPresentation : the display: the dialog is attached to it    */
        /*                   and shown only while it is current.              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ANS_clsAnswer : Yes carrying an MFC_clsMakeFsChoice            */
        /*                     (ANS_objValue) on OK; Cancel otherwise,        */
        /*                     including a question without its words.        */
        /*--------------------------------------------------------------------*/
        private ANS_clsAnswer Qpr_MakeFsOptions(Form frmOwner, QST_clsQuestion objQuestion, PRS_clsPresentation objPresentation)
        {
            NIM_clsNewImageForm frmOptions;
            MFO_stMakeFsOptions stOptions;
            FMI_clsFormatInput objFormat;
            ANS_clsAnswer objReply;
            byte[] abLabel;
            ulong ullLength;

            /* The range's words and length, as captured at the press --   */
            /* never the row selected now.                                 */
            objFormat = objQuestion.QST_objContext as FMI_clsFormatInput;
            if (objFormat == null || objQuestion.QST_objRequest == null || objQuestion.QST_objRequest.RQT_objTarget == null)
            {   /* No words or no range (a guard: the sequence always gives   */
                /* both): nothing to show, so Cancel.                         */
                return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
            }

            /* Nothing chosen yet: Cancel until OK is read. */
            ullLength = objQuestion.QST_objRequest.RQT_objTarget.TGT_ullLength;
            objReply = new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null);
            using (frmOptions = new NIM_clsNewImageForm(Qpr_fntDialog, ""))
            {   /* The dialog in its fixed-target mode; disposed when the   */
                /* block ends, after its values have been read.             */
                frmOptions.NIM_FixTarget(objFormat.FMI_sPlace, ullLength);
                if (objFormat.FMI_bWholeDisk)
                {   /* The whole disk, with no table: the size may be made   */
                    /* smaller than the disk.                                */
                    frmOptions.NIM_AllowSmaller();
                }

                /* The owner's icon, and shown modal over the partition window,   */
                /* which is what the user was working in.                         */
                frmOptions.Icon = frmOwner.Icon;

                /* Attached before it is shown, so an Exit closes it         */
                /* (QWN_Supersede); never shown once an Exit has replaced    */
                /* this display -- it answers Cancel then, which QST_Reply   */
                /* refuses, the display no longer being the question's.      */
                QWN_clsQuestionWindows.QWN_Attach(frmOptions);
                if (!objQuestion.QST_IsCurrent(objPresentation))
                {   /* Replaced while the dialog was being made. */
                    return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
                }

                frmOptions.ShowDialog(frmOwner);
                if (frmOptions.NIM_bAccepted)
                {   /* OK: every value read now, before the dialog is       */
                    /* disposed.  The length is the dialog's: the range's   */
                    /* own, or less when it was allowed.  MakeFsAt takes    */
                    /* its size from the range, so the options' size and    */
                    /* overwrite stay 0 -- unused there (bfe_mkfs.c).       */
                    stOptions = new MFO_stMakeFsOptions();
                    stOptions.MFO_uiFsType = frmOptions.NIM_uiFsType;
                    stOptions.MFO_uiBlockSize = frmOptions.NIM_uiBlockSize;
                    stOptions.MFO_ullSizeBytes = 0;
                    stOptions.MFO_uiOverwrite = 0;
                    stOptions.MFO_uiFlags = 0;
                    if (frmOptions.NIM_bZeroFill)
                    {   /* Zero-fill ticked: the engine zeros the whole   */
                        /* range before it formats (ENG_MAKEFS_ZERO).     */
                        stOptions.MFO_uiFlags = ENG_clsNative.ENG_MAKEFS_ZERO;
                    }

                    /* The label as UTF-8 into the engine's fixed field        */
                    /* (ENG_LABEL_FIELD), the rest left zero, so it is         */
                    /* NUL-terminated.  It fits: the dialog refuses an ext     */
                    /* label over ENG_LABEL_MAX_BYTES and a FAT or exFAT one   */
                    /* over 11 characters, 33 bytes at the most, and the       */
                    /* field holds that and its NUL (NewImageForm.cs).         */
                    stOptions.MFO_arrLabel = new byte[ENG_clsNative.ENG_LABEL_FIELD];
                    abLabel = System.Text.Encoding.UTF8.GetBytes(frmOptions.NIM_sLabel);
                    Array.Copy(abLabel, stOptions.MFO_arrLabel, abLabel.Length);

                    /* Yes, carrying the choice. */
                    objReply = new ANS_clsAnswer(ANK_enmAnswerKind.Yes, null);
                    objReply.ANS_objValue = new MFC_clsMakeFsChoice(stOptions, frmOptions.NIM_ullBytes);
                }
            }

            /* Yes with the choice, or Cancel. */
            return(objReply);
        }

        /*--------------------------------------------------------------------*/
        /* Qpr_NewImageFile:                                                  */
        /*                                                                    */
        /* New Image's dialog, shown for its own sequence (the NewImageFile   */
        /* question) over the main window: the file and its size only, with   */
        /* Match a Device beside the size.  Browse and Match do not run here: */
        /* each ends the dialog with what was typed (NIM_AskOutside), and the */
        /* sequence asks the file or the disk, then this question again with  */
        /* the fields put back.  It only shows and reads.                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmOwner    : the main window.                                 */
        /*     objQuestion : the question; its context is an                  */
        /*                   NIQ_clsNewImageQuestion.                         */
        /*     objPresentation : the display: the dialog is attached to it    */
        /*                   and shown only while it is current.              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ANS_clsAnswer : Yes carrying an NIC_clsNewImageChoice          */
        /*                     (ANS_objValue) on OK, Browse or Match; Cancel  */
        /*                     otherwise, including a question without its    */
        /*                     context.                                       */
        /*--------------------------------------------------------------------*/
        private ANS_clsAnswer Qpr_NewImageFile(Form frmOwner, QST_clsQuestion objQuestion, PRS_clsPresentation objPresentation)
        {
            NIM_clsNewImageForm frmInput;
            NIQ_clsNewImageQuestion objAsk;
            ANS_clsAnswer objReply;

            /* The start folder and any fields to put back (a guard: the   */
            /* sequence always gives them).                                */
            objAsk = objQuestion.QST_objContext as NIQ_clsNewImageQuestion;
            if (objAsk == null)
            {   /* No context: nothing to show, so Cancel. */
                return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
            }

            /* Nothing chosen yet: Cancel until the dialog says otherwise. */
            objReply = new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null);
            using (frmInput = new NIM_clsNewImageForm(Qpr_fntDialog, objAsk.NIQ_sStartFolder))
            {   /* Size-only mode, Browse and Match ending the dialog, the   */
                /* typed fields put back when asked again; disposed when     */
                /* the block ends, after its values have been read.          */
                frmInput.NIM_SizeOnly();
                frmInput.NIM_AskOutside();
                if (objAsk.NIQ_bTyped)
                {   /* Shown again after Browse or Match: the fields as   */
                    /* typed, the picker's answer already in them.        */
                    frmInput.NIM_SetTyped(objAsk.NIQ_sPath, objAsk.NIQ_decSize, objAsk.NIQ_iUnit);
                }

                /* The owner's icon, and shown modal over the main window. */
                frmInput.Icon = frmOwner.Icon;

                /* Attached before it is shown, so an Exit closes it         */
                /* (QWN_Supersede); never shown once an Exit has replaced    */
                /* this display -- it answers Cancel then, which QST_Reply   */
                /* refuses, the display no longer being the question's.      */
                QWN_clsQuestionWindows.QWN_Attach(frmInput);
                if (!objQuestion.QST_IsCurrent(objPresentation))
                {   /* Replaced while the dialog was being made. */
                    return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
                }

                frmInput.ShowDialog(frmOwner);
                if (frmInput.NIM_bAccepted)
                {   /* OK, every check passed (and the replace question      */
                    /* answered inside the dialog): the file, its size and   */
                    /* the replace choice.                                   */
                    objReply = new ANS_clsAnswer(ANK_enmAnswerKind.Yes, null);
                    objReply.ANS_objValue = new NIC_clsNewImageChoice(false, false, frmInput.NIM_sImagePath,
                        frmInput.NIM_ullBytes, frmInput.NIM_bOverwrite, 0, 0);
                }
                else if (frmInput.NIM_bBrowseAsked || frmInput.NIM_bMatchAsked)
                {   /* Browse or Match: which, and the fields as typed, for   */
                    /* the sequence to ask the picker.                        */
                    objReply = new ANS_clsAnswer(ANK_enmAnswerKind.Yes, null);
                    objReply.ANS_objValue = new NIC_clsNewImageChoice(frmInput.NIM_bBrowseAsked, frmInput.NIM_bMatchAsked,
                        frmInput.NIM_sTypedPath, 0, false, frmInput.NIM_decTypedSize, frmInput.NIM_iTypedUnit);
                }
            }

            /* Yes with the choice, or Cancel. */
            return(objReply);
        }

        /*--------------------------------------------------------------------*/
        /* Qpr_NewImageDisk:                                                  */
        /*                                                                    */
        /* New Image's Match a Device, shown for its sequence (the            */
        /* NewImageDisk question) over the main window: the disks the worker  */
        /* listed, in the disk picker's size mode, so one's exact length can  */
        /* become the image's size.  An empty list is said, as "no disks",    */
        /* not as a failure; a list the worker could not read comes as its    */
        /* reason instead, which is said in its own words.  A disk longer     */
        /* than New Image's size box takes is refused here, so the size       */
        /* stays as it was typed.  It only shows and reads.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmOwner    : the main window.                                 */
        /*     objQuestion : the question; its context is the disk list (a    */
        /*                   List<DEV_clsDisk>), or the reason none could be  */
        /*                   read (a string).                                 */
        /*     objPresentation : the display: the picker is attached to it    */
        /*                   and shown only while it is current.              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ANS_clsAnswer : Yes carrying the chosen DEV_clsDisk            */
        /*                     (ANS_objValue); Cancel otherwise -- a reason   */
        /*                     shown, an empty list, a disk refused, the      */
        /*                     picker cancelled, or no context.               */
        /*--------------------------------------------------------------------*/
        private ANS_clsAnswer Qpr_NewImageDisk(Form frmOwner, QST_clsQuestion objQuestion, PRS_clsPresentation objPresentation)
        {
            List<DEV_clsDisk> lstDisks;
            DVF_clsDeviceForm frmDisks;
            DEV_clsDisk objDisk;
            ANS_clsAnswer objReply;
            string sProblem;

            /* A reason in place of the list: the worker's read failed, was   */
            /* refused, or gave no list.  Said over the main window in its    */
            /* own words -- never as "No disks could be found.", which is     */
            /* only for a read that worked -- and Cancel, so the size is      */
            /* left as it was and Match can be tried again.                   */
            sProblem = objQuestion.QST_objContext as string;
            if (sProblem != null)
            {   /* The reason, then Cancel. */
                MSG_clsMessageForm.MSG_Show(frmOwner, Qpr_fntDialog, sProblem, "New image",
                    MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
                return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
            }

            /* The worker's list (a guard: it always gives a list or a reason). */
            lstDisks = objQuestion.QST_objContext as List<DEV_clsDisk>;
            if (lstDisks == null)
            {   /* No list: nothing to show, so Cancel. */
                return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
            }

            if (lstDisks.Count == 0)
            {   /* No disk at all to match: said, and Cancel, so the size   */
                /* is left as it was.                                       */
                MSG_clsMessageForm.MSG_Show(frmOwner, Qpr_fntDialog, "No disks could be found.", "New image",
                    MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
                return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
            }

            /* Nothing chosen yet; then the disk picker (PartitionForm.cs) in   */
            /* its size mode, which shows each disk's length.                   */
            objDisk = null;
            using (frmDisks = new DVF_clsDeviceForm(Qpr_fntDialog, lstDisks, "Match the size of a disk", DVF_clsDeviceForm.DVF_RANGE_SIZE, 0))
            {   /* Shown modal over the main window, under its icon; the   */
                /* answer taken out before the picker is disposed.         */
                frmDisks.Icon = frmOwner.Icon;

                /* Attached before it is shown, so an Exit closes it         */
                /* (QWN_Supersede); never shown once an Exit has replaced    */
                /* this display -- it answers Cancel then, which QST_Reply   */
                /* refuses, the display no longer being the question's.      */
                QWN_clsQuestionWindows.QWN_Attach(frmDisks);
                if (!objQuestion.QST_IsCurrent(objPresentation))
                {   /* Replaced while the picker was being made. */
                    return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
                }

                frmDisks.ShowDialog(frmOwner);
                if (frmDisks.DVF_bAccepted)
                {   /* One was chosen (null if the picker had none). */
                    objDisk = frmDisks.DVF_objDiskChosen;
                }
            }

            if (objDisk == null)
            {   /* Cancelled: the size is left as it was. */
                return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
            }

            if ((decimal)objDisk.DEV_stInfo.DVI_ullSize > NIM_clsNewImageForm.NIM_SIZE_MAX)
            {   /* Longer than New Image's size box takes (more than         */
                /* NIM_SIZE_MAX): refused in its own words, and Cancel, so   */
                /* the dialog comes back with the size as typed -- its box   */
                /* would otherwise hold the disk's length cut down to its    */
                /* ceiling, a size the user never asked for.                 */
                MSG_clsMessageForm.MSG_Show(frmOwner, Qpr_fntDialog, "That disk is larger than an image this program makes.",
                    "New image", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
                return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
            }

            /* Chosen and within the box: Yes, carrying the disk, whose   */
            /* length the sequence puts into the size.                    */
            objReply = new ANS_clsAnswer(ANK_enmAnswerKind.Yes, null);
            objReply.ANS_objValue = objDisk;
            return(objReply);
        }

        /*--------------------------------------------------------------------*/
        /* Qpr_NewImagePath:                                                  */
        /*                                                                    */
        /* New Image's Browse, shown for its sequence (the NewImagePath       */
        /* question) over the main window: the standard save dialog, set up   */
        /* for creating a file, starting where the typed path points.  Its    */
        /* own "replace it?" is off: the New Image dialog asks that once, on  */
        /* OK, whether the path was browsed or typed.  It only shows and      */
        /* reads.                                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmOwner    : the main window.                                 */
        /*     objQuestion : the question; its context is the path typed so   */
        /*                   far (a string, possibly empty or malformed).     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ANS_clsAnswer : Yes carrying the chosen full path (ANS_sText); */
        /*                     Cancel otherwise.                              */
        /*--------------------------------------------------------------------*/
        private ANS_clsAnswer Qpr_NewImagePath(Form frmOwner, QST_clsQuestion objQuestion)
        {
            SaveFileDialog dlgSave;
            string sTyped;
            string sFolder;
            ANS_clsAnswer objReply;

            /* The typed path ("" for none). */
            sTyped = objQuestion.QST_objContext as string;
            if (sTyped == null)
            {   /* None given: the dialog starts where it likes. */
                sTyped = "";
            }

            /* Nothing chosen yet: Cancel until a file is named. */
            objReply = new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null);
            using (dlgSave = new SaveFileDialog())
            {   /* For creating a file: .img offered first and added to a   */
                /* name typed without an extension, its own replace         */
                /* question off; disposed when the block ends.              */
                dlgSave.Title           = "Create an image file";
                dlgSave.Filter          = "Image files (*.img)|*.img|All files (*.*)|*.*";
                dlgSave.DefaultExt      = "img";
                dlgSave.OverwritePrompt = false;

                try
                {   /* The typed text may be anything; a malformed path throws here. */
                    sFolder = System.IO.Path.GetDirectoryName(sTyped.Trim());
                    if (sFolder != null && System.IO.Directory.Exists(sFolder))
                    {   /* Start where the typed path points. */
                        dlgSave.InitialDirectory = sFolder;
                        dlgSave.FileName         = System.IO.Path.GetFileName(sTyped.Trim());
                    }
                }
                catch (ArgumentException)
                {   /* Not a usable path: the dialog starts where it likes. */
                }

                if (dlgSave.ShowDialog(frmOwner) == DialogResult.OK)
                {   /* A file was named: Yes, carrying its full path. */
                    objReply = new ANS_clsAnswer(ANK_enmAnswerKind.Yes, null, dlgSave.FileName, null);
                }
            }

            /* Yes with the path, or Cancel. */
            return(objReply);
        }

        /*--------------------------------------------------------------------*/
        /* Qpr_DeviceChoice:                                                  */
        /*                                                                    */
        /* The device an operation works on, shown for its sequence (the      */
        /* DeviceChoice question) over the main window: the disks the worker  */
        /* listed just before asking, in the device picker (PartitionForm.cs) */
        /* with the caption and range mode the question names.  The choice is */
        /* copied out of its row before the picker is disposed.  It only      */
        /* shows and reads.                                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmOwner    : the main window.                                 */
        /*     objQuestion : the question; its context is a                   */
        /*                   DCQ_clsDeviceQuestion.                           */
        /*     objPresentation : the display: the picker is attached to it    */
        /*                   and shown only while it is current.              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ANS_clsAnswer : Yes carrying a DCH_clsDeviceChoice             */
        /*                     (ANS_objValue); Cancel otherwise, including a  */
        /*                     question without its context.                  */
        /*--------------------------------------------------------------------*/
        private ANS_clsAnswer Qpr_DeviceChoice(Form frmOwner, QST_clsQuestion objQuestion, PRS_clsPresentation objPresentation)
        {
            DCQ_clsDeviceQuestion objAsk;
            DVF_clsDeviceForm frmDevices;
            DEV_clsDisk objDisk;
            ulong ullRange;
            ANS_clsAnswer objReply;

            /* The disks and the picker's settings (a guard: the sequence   */
            /* always gives them, with at least one disk).                  */
            objAsk = objQuestion.QST_objContext as DCQ_clsDeviceQuestion;
            if (objAsk == null || objAsk.DCQ_lstDisks == null)
            {   /* No context: nothing to show, so Cancel. */
                return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
            }

            /* Nothing chosen yet; then the picker, modal over the main      */
            /* window under its icon, the row and range read out before it   */
            /* is disposed.                                                  */
            objDisk = null;
            ullRange = 0;
            using (frmDevices = new DVF_clsDeviceForm(Qpr_fntDialog, objAsk.DCQ_lstDisks, objAsk.DCQ_sTitle,
                objAsk.DCQ_iRange, objAsk.DCQ_ullImageBytes))
            {   /* Shown; disposed when the block ends. */
                frmDevices.Icon = frmOwner.Icon;

                /* Attached before it is shown, so an Exit closes it         */
                /* (QWN_Supersede); never shown once an Exit has replaced    */
                /* this display -- it answers Cancel then, which QST_Reply   */
                /* refuses, the display no longer being the question's.      */
                QWN_clsQuestionWindows.QWN_Attach(frmDevices);
                if (!objQuestion.QST_IsCurrent(objPresentation))
                {   /* Replaced while the picker was being made. */
                    return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
                }

                frmDevices.ShowDialog(frmOwner);
                if (frmDevices.DVF_bAccepted)
                {   /* Accepted: its row (null if none was chosen) and range. */
                    objDisk = frmDevices.DVF_objDiskChosen;
                    ullRange = frmDevices.DVF_ullRangeBytes;
                }
            }

            if (objDisk == null)
            {   /* Cancelled, or no disk chosen: Cancel. */
                return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
            }

            /* Chosen: Yes, carrying the choice copied out of its row (no row   */
            /* or list is kept), described as the question asked.               */
            objReply = new ANS_clsAnswer(ANK_enmAnswerKind.Yes, null);
            objReply.ANS_objValue = new DCH_clsDeviceChoice(objDisk, ullRange, objAsk.DCQ_bBrowseName);
            return(objReply);
        }

        /*--------------------------------------------------------------------*/
        /* Qpr_DeviceRead:                                                    */
        /*                                                                    */
        /* Read Device's whole choice, shown for its sequence (the DeviceRead */
        /* question) over the main window as two dialogs answered as one: the */
        /* device picker in its read mode -- which device, how much of it     */
        /* (all, a typed length, or through its backup GPT) and whether to    */
        /* compress -- then Windows' save dialog for the image file.  Nothing */
        /* is read or made here; every choice is copied out before its        */
        /* dialog is disposed, and the sequence checks it all again.          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmOwner    : the main window.                                 */
        /*     objQuestion : the question; its context is an                  */
        /*                   RDQ_clsDeviceReadQuestion with the disks.        */
        /*     objPresentation : the display: the picker is attached to it    */
        /*                   and shown only while it is current.              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ANS_clsAnswer : Yes carrying an RDI_clsDeviceReadIntent        */
        /*                     (ANS_objValue); Cancel when either dialog was  */
        /*                     cancelled or could not be shown, or the        */
        /*                     question came without its disks.               */
        /*--------------------------------------------------------------------*/
        private ANS_clsAnswer Qpr_DeviceRead(Form frmOwner, QST_clsQuestion objQuestion, PRS_clsPresentation objPresentation)
        {
            RDQ_clsDeviceReadQuestion objAsk;
            DVF_clsDeviceForm frmDevices;
            TGT_clsTarget objTarget;
            WFP_enmPurpose enmPurpose;
            ulong ullRange;
            bool bXz;
            bool bThroughGpt;
            string sName;
            string sOutput;
            ANS_clsAnswer objReply;

            /* The disks, their GPT ends and the starting folder (a guard:   */
            /* the sequence always gives them, with at least one disk).      */
            objAsk = objQuestion.QST_objContext as RDQ_clsDeviceReadQuestion;
            if (objAsk == null || objAsk.RDQ_lstDisks == null)
            {   /* No disks: nothing to show, so Cancel. */
                return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
            }

            /* Nothing chosen yet: the target stays null until the picker   */
            /* returns with a disk, which is how a Cancel there is told.    */
            objTarget = null;
            ullRange = 0;
            bXz = false;
            bThroughGpt = false;
            sName = "";
            using (frmDevices = new DVF_clsDeviceForm(Qpr_fntDialog, objAsk.RDQ_lstDisks,
                "Save a device to an image file", DVF_clsDeviceForm.DVF_RANGE_READ, 0))
            {   /* The picker in its read mode, modal over the main window      */
                /* under its icon; disposed when the block ends, after its      */
                /* answers have been copied out.  The GPT disks the sequence    */
                /* found go in before it is shown, so the first selected disk   */
                /* already has its GPT box right (shown only for a disk named   */
                /* there).                                                      */
                frmDevices.Icon = frmOwner.Icon;
                frmDevices.DVF_SetGptEnds(objAsk.RDQ_dictGptEnds);

                /* Attached before it is shown, so an Exit closes it         */
                /* (QWN_Supersede); never shown once an Exit has replaced    */
                /* this display -- it answers Cancel then, which QST_Reply   */
                /* refuses, the display no longer being the question's.      */
                QWN_clsQuestionWindows.QWN_Attach(frmDevices);
                if (!objQuestion.QST_IsCurrent(objPresentation))
                {   /* Replaced while the picker was being made. */
                    return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
                }

                frmDevices.ShowDialog(frmOwner);
                if (frmDevices.DVF_bAccepted && frmDevices.DVF_objDiskChosen != null)
                {   /* Accepted with a disk: the device as a target of its own   */
                    /* (MAIN_CommandRawTarget, no row kept), the range, the      */
                    /* compression box -- which, not the file name's suffix,     */
                    /* decides compression -- and the GPT box, which goes on     */
                    /* only as the user's choice: the read sequence reads the    */
                    /* header again for the length.  The drive's own name is     */
                    /* the start of the file name offered.                       */
                    objTarget = MAIN_clsMainForm.MAIN_CommandRawTarget(frmDevices.DVF_objDiskChosen);
                    ullRange = frmDevices.DVF_ullRangeBytes;
                    bXz = frmDevices.DVF_bCompressXz;
                    bThroughGpt = frmDevices.DVF_bThroughGpt;
                    sName = "PhysicalDrive" + frmDevices.DVF_objDiskChosen.DEV_iNumber;
                }
            }

            if (objTarget == null)
            {   /* Cancelled, or no disk chosen: no save dialog, Cancel. */
                return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
            }

            /* The name the save dialog offers -- PhysicalDriveN.img, or   */
            /* .img.xz compressed -- and the purpose that adds that        */
            /* extension when the user types none.                         */
            enmPurpose = WFP_enmPurpose.ReadRaw;
            if (bXz)
            {   /* Compressed: .img.xz offered, xz added. */
                sName += ".img.xz";
                enmPurpose = WFP_enmPurpose.ReadXz;
            }
            else
            {   /* Plain: .img offered, img added. */
                sName += ".img";
            }

            /* The save dialog for the image file, starting in the folder the   */
            /* command was given; null for a Cancel, or an owner gone.          */
            sOutput = WFD_clsWindowsDialogs.WFD_SelectFile(frmOwner, enmPurpose, "Save the device's image as",
                objAsk.RDQ_sStartFolder, sName);
            if (sOutput == null)
            {   /* Cancelled: nothing to read, and no file is made. */
                return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
            }

            /* Chosen: Yes, carrying every choice in one record (no dialog,   */
            /* row or list in it).                                            */
            objReply = new ANS_clsAnswer(ANK_enmAnswerKind.Yes, null);
            objReply.ANS_objValue = new RDI_clsDeviceReadIntent(objTarget, ullRange, bXz, sOutput, bThroughGpt);
            return(objReply);
        }

        /*--------------------------------------------------------------------*/
        /* Qpr_DeviceWriteFile:                                               */
        /*                                                                    */
        /* Write Device's image file, shown for its sequence (the             */
        /* DeviceWriteFile question) over the main window: Windows' open      */
        /* dialog for an image, plain or compressed.  It only names the file; */
        /* the sequence inspects it before any device is offered.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmOwner    : the main window.                                 */
        /*     objQuestion : the question; its context is the folder to start */
        /*                   in (a string; anything else starts nowhere in    */
        /*                   particular).                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ANS_clsAnswer : Yes carrying the chosen full path (ANS_sText); */
        /*                     Cancel for a cancel or an owner gone.          */
        /*--------------------------------------------------------------------*/
        private ANS_clsAnswer Qpr_DeviceWriteFile(Form frmOwner, QST_clsQuestion objQuestion)
        {
            string sFolder;
            string sImage;

            /* The starting folder, "" when none came. */
            sFolder = objQuestion.QST_objContext as string;
            if (sFolder == null)
            {   /* No folder: the dialog starts where it likes. */
                sFolder = "";
            }

            /* The open dialog with the write purpose, whose filter offers   */
            /* compressed images too; null for a Cancel or an owner gone.    */
            sImage = WFD_clsWindowsDialogs.WFD_SelectFile(frmOwner, WFP_enmPurpose.WriteImage,
                "Choose the image file to write to a device", sFolder, "");
            if (sImage == null)
            {   /* Cancelled: no file, Cancel. */
                return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
            }

            /* Chosen: Yes, carrying the path as the answer's text. */
            return(new ANS_clsAnswer(ANK_enmAnswerKind.Yes, null, sImage, null));
        }

        /*--------------------------------------------------------------------*/
        /* Qpr_PartitionImageFile:                                            */
        /*                                                                    */
        /* File > Partition/Format > Image File's disk image, shown for its   */
        /* sequence (the PartitionImageFile question) over the main window:   */
        /* Windows' open dialog with Open's filter.  It only names the file;  */
        /* the sequence reads its length, closes what is open after asking,   */
        /* and opens the partition window over it.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmOwner    : the main window.                                 */
        /*     objQuestion : the question; its context is the folder to start */
        /*                   in (a string; anything else starts nowhere in    */
        /*                   particular).                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ANS_clsAnswer : Yes carrying the chosen full path (ANS_sText); */
        /*                     Cancel for a cancel or an owner gone.          */
        /*--------------------------------------------------------------------*/
        private ANS_clsAnswer Qpr_PartitionImageFile(Form frmOwner, QST_clsQuestion objQuestion)
        {
            string sFolder;
            string sImage;

            /* The starting folder, "" when none came. */
            sFolder = objQuestion.QST_objContext as string;
            if (sFolder == null)
            {   /* No folder: the dialog starts where it likes. */
                sFolder = "";
            }

            /* The open dialog with Open's purpose -- the image filter the      */
            /* Open and Check pickers use, since a disk image is chosen the     */
            /* same way -- and the partition caption; null for a Cancel or an   */
            /* owner gone.                                                      */
            sImage = WFD_clsWindowsDialogs.WFD_SelectFile(frmOwner, WFP_enmPurpose.OpenImage,
                "Choose the disk image to partition", sFolder, "");
            if (sImage == null)
            {   /* Cancelled: no file, Cancel. */
                return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
            }

            /* Chosen: Yes, carrying the path as the answer's text. */
            return(new ANS_clsAnswer(ANK_enmAnswerKind.Yes, null, sImage, null));
        }

        /*--------------------------------------------------------------------*/
        /* Qpr_NewTableKind:                                                  */
        /*                                                                    */
        /* The New Table dialog, shown for the sequence of New Table or of    */
        /* New Image's first question (the NewTableKind question) over the    */
        /* partition window: MBR, GPT or no table, the present kind offered   */
        /* first.  It only shows and reads; the table is replaced by the      */
        /* sequence, on its copy, once this answers.                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmOwner    : the partition window.                            */
        /*     objQuestion : the question; its context is the table's present */
        /*                   PTB_KIND_*, a boxed int.                         */
        /*     objPresentation : the display: the dialog is attached to it    */
        /*                   and shown only while it is current.              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ANS_clsAnswer : Yes carrying the chosen PTB_KIND_* (a boxed    */
        /*                     int, ANS_objValue) on OK; Cancel otherwise,    */
        /*                     including a question without its kind.         */
        /*--------------------------------------------------------------------*/
        private ANS_clsAnswer Qpr_NewTableKind(Form frmOwner, QST_clsQuestion objQuestion, PRS_clsPresentation objPresentation)
        {
            NTB_clsNewTableForm frmKind;
            ANS_clsAnswer objReply;

            if (!(objQuestion.QST_objContext is int))
            {   /* No present kind (a guard: the sequence always gives it):   */
                /* nothing to offer, so Cancel.                               */
                return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
            }

            /* Nothing chosen yet: Cancel until OK is read. */
            objReply = new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null);
            using (frmKind = new NTB_clsNewTableForm(Qpr_fntDialog, (int)objQuestion.QST_objContext))
            {   /* The dialog, under the owner's icon, modal over the          */
                /* partition window; disposed when the block ends, after its   */
                /* answer has been read.                                       */
                frmKind.Icon = frmOwner.Icon;

                /* Attached before it is shown, so an Exit closes it         */
                /* (QWN_Supersede); never shown once an Exit has replaced    */
                /* this display -- it answers Cancel then, which QST_Reply   */
                /* refuses, the display no longer being the question's.      */
                QWN_clsQuestionWindows.QWN_Attach(frmKind);
                if (!objQuestion.QST_IsCurrent(objPresentation))
                {   /* Replaced while the dialog was being made. */
                    return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
                }

                frmKind.ShowDialog(frmOwner);
                if (frmKind.NTB_bAccepted)
                {   /* A kind chosen: Yes, carrying it. */
                    objReply = new ANS_clsAnswer(ANK_enmAnswerKind.Yes, null);
                    objReply.ANS_objValue = frmKind.NTB_iKind;
                }
            }

            /* Yes with the kind, or Cancel. */
            return(objReply);
        }

        /*---------------------------------------------------------------------*/
        /* Qpr_AddPartition:                                                   */
        /*                                                                     */
        /* The Add Partition dialog, shown for Add's own sequence (the         */
        /* AddPartition question) over the partition window: the start gap     */
        /* proposed, aligned, and the place, type and name taken -- the        */
        /* dialog validating them against the worker's table copy, which the   */
        /* worker does not touch while it waits.  The entry is added by the    */
        /* sequence, on its copy, once this answers.                           */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     frmOwner    : the partition window.                             */
        /*     objQuestion : the question; its context an ADQ_clsAddQuestion.  */
        /*     objPresentation : the display: the dialog is attached to it     */
        /*                   and shown only while it is current.               */
        /*                                                                     */
        /* Returns:                                                            */
        /*     ANS_clsAnswer : Yes carrying an ADC_clsAddChoice (ANS_objValue) */
        /*                     on OK; Cancel otherwise, including a question   */
        /*                     without its facts.                              */
        /*---------------------------------------------------------------------*/
        private ANS_clsAnswer Qpr_AddPartition(Form frmOwner, QST_clsQuestion objQuestion, PRS_clsPresentation objPresentation)
        {
            ADQ_clsAddQuestion objAdd;
            ADD_clsAddForm frmAdd;
            ANS_clsAnswer objReply;

            objAdd = objQuestion.QST_objContext as ADQ_clsAddQuestion;
            if (objAdd == null)
            {   /* No facts (a guard: the sequence always gives them):   */
                /* nothing to show, so Cancel.                           */
                return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
            }

            /* Nothing chosen yet: Cancel until OK is read. */
            objReply = new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null);
            using (frmAdd = new ADD_clsAddForm(Qpr_fntDialog, objAdd.ADQ_objTable, objAdd.ADQ_lstGaps, objAdd.ADQ_objStart))
            {   /* The dialog, under the owner's icon, modal over the          */
                /* partition window; disposed when the block ends, after its   */
                /* values have been read.                                      */
                frmAdd.Icon = frmOwner.Icon;

                /* Attached before it is shown, so an Exit closes it         */
                /* (QWN_Supersede); never shown once an Exit has replaced    */
                /* this display -- it answers Cancel then, which QST_Reply   */
                /* refuses, the display no longer being the question's.      */
                QWN_clsQuestionWindows.QWN_Attach(frmAdd);
                if (!objQuestion.QST_IsCurrent(objPresentation))
                {   /* Replaced while the dialog was being made. */
                    return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
                }

                frmAdd.ShowDialog(frmOwner);
                if (frmAdd.ADD_bAccepted)
                {   /* OK with a valid partition: Yes, carrying it. */
                    objReply = new ANS_clsAnswer(ANK_enmAnswerKind.Yes, null);
                    objReply.ANS_objValue = new ADC_clsAddChoice(frmAdd.ADD_ullFirstLba, frmAdd.ADD_ullLastLba,
                        frmAdd.ADD_objType, frmAdd.ADD_sName, frmAdd.ADD_bBoot);
                }
            }

            /* Yes with the partition, or Cancel. */
            return(objReply);
        }

        /*--------------------------------------------------------------------*/
        /* Qpr_PartitionFile:                                                 */
        /*                                                                    */
        /* The Windows file a partition Read saves to, or a partition Write   */
        /* writes from, shown for that copy's own sequence (the PartitionFile */
        /* question): the standard save dialog for a Read (default .img),     */
        /* the open dialog for a Write (raw and compressed images offered),   */
        /* over the partition window, starting in the image folder the press  */
        /* captured.  It only shows and reads; the copy is the sequence's.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmOwner    : the partition window.                            */
        /*     objQuestion : the question: its request's kind says Read or    */
        /*                   Write and its folder the starting folder; the    */
        /*                   context (FMI_clsFormatInput) names the range.    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ANS_clsAnswer : Yes carrying the path (ANS_sText) when a file  */
        /*                     was chosen; Cancel otherwise, including a      */
        /*                     question without its words.                    */
        /*--------------------------------------------------------------------*/
        private ANS_clsAnswer Qpr_PartitionFile(Form frmOwner, QST_clsQuestion objQuestion)
        {
            FMI_clsFormatInput objFormat;
            WFP_enmPurpose enmPurpose;
            string sTitle;
            string sFile;

            objFormat = objQuestion.QST_objContext as FMI_clsFormatInput;
            if (objFormat == null || objQuestion.QST_objRequest == null)
            {   /* No words or no request (a guard: the sequence always   */
                /* gives both): nothing to show, so Cancel.               */
                return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
            }

            /* The dialog and its caption by the copy's direction. */
            if (objQuestion.QST_objRequest.RQT_enmKind == KND_enmKind.ReadPartition)
            {   /* Read: the save dialog, default extension .img. */
                enmPurpose = WFP_enmPurpose.ReadRaw;
                sTitle = "Save " + objFormat.FMI_sPlace + " to a file";
            }
            else
            {   /* Write: the open dialog, raw and compressed images offered. */
                enmPurpose = WFP_enmPurpose.WriteImage;
                sTitle = "Choose the file to write into " + objFormat.FMI_sPlace;
            }

            /* The standard Windows dialog; Cancel gives null. */
            sFile = WFD_clsWindowsDialogs.WFD_SelectFile(frmOwner, enmPurpose, sTitle,
                objQuestion.QST_objRequest.RQT_sFolder, "");
            if (String.IsNullOrEmpty(sFile))
            {   /* Cancelled: nothing chosen. */
                return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
            }

            /* Chosen: Yes, carrying the path. */
            return(new ANS_clsAnswer(ANK_enmAnswerKind.Yes, null, sFile));
        }

        /*--------------------------------------------------------------------*/
        /* Qpr_FolderName:                                                    */
        /*                                                                    */
        /* A name question: a new folder's name in a Save As picker, or the   */
        /* main window's New Folder or Rename in the image.  The text typed   */
        /* is kept as the question's draft as it is typed, so an Exit         */
        /* declined shows the question again with it.  The name is only       */
        /* returned here; the worker checks it and makes the change.          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmOwner        : the window it is shown over (the main        */
        /*                       window or the picker).                       */
        /*     objQuestion     : the question, which holds the draft.         */
        /*     objPresentation : this display, which alone may answer.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ANS_clsAnswer : Yes with the text as typed, or Cancel (the     */
        /*                     dialog cancelled, the facts missing, or the    */
        /*                     display superseded).                           */
        /*--------------------------------------------------------------------*/
        private ANS_clsAnswer Qpr_FolderName(Form frmOwner, QST_clsQuestion objQuestion,
            PRS_clsPresentation objPresentation)
        {
            QDU_clsDraftUpdates objUpdates;
            PRM_clsPromptForm dlgName;
            RQT_clsRequest objNameRequest;
            EDA_clsEditArgs objEdit;
            string sFolder;
            string sDraft;
            string sInitial;
            string sCaption;
            string sPrompt;
            string sEntry;
            int iSlash;

            if (!objQuestion.QST_AdoptDraft(objPresentation))
            {   /* This display is no longer current (an Exit took the     */
                /* question down before its call arrived), so it may not   */
                /* take the draft: it shows nothing and answers Cancel,    */
                /* which the question refuses.                             */
                return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
            }

            /* The context says which question this is: a picker's New         */
            /* Folder gives the folder's path as a string, the main window's   */
            /* New Folder or Rename gives its captured request.  Neither       */
            /* reads anything from a pane.                                     */
            sFolder = objQuestion.QST_objContext as string;
            if (sFolder == null)
            {   /* Not a string: the main window's request (below), and no   */
                /* folder to name in the prompt.                             */
                sFolder = "";
            }

            /* The picker's words by default; the main window's request   */
            /* replaces them with its own below.                          */
            sInitial = "New folder";
            sCaption = "New Folder";
            sPrompt = "Name of the new folder in " + sFolder + ":";
            objNameRequest = objQuestion.QST_objContext as RQT_clsRequest;
            if (objNameRequest != null)
            {   /* The main window's request: one path, the folder to make   */
                /* in or the entry to rename.                                */
                objEdit = objNameRequest.RQT_objPayload as EDA_clsEditArgs;
                if (objEdit == null || objNameRequest.RQT_lstPaths.Count != 1)
                {   /* Not an edit, or not one path: nothing to name, and   */
                    /* nothing is taken from the pane instead -- Cancel.    */
                    return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
                }

                if (objEdit.EDA_enmKind == EDK_enmEditKind.NameDirectory)
                {   /* New Folder in the image: "new-folder" offered, a   */
                    /* Linux-style name.                                  */
                    sInitial = "new-folder";
                    sCaption = "New folder in the image";
                    sPrompt = "Name of the new folder:";
                }
                else if (objEdit.EDA_enmKind == EDK_enmEditKind.NameEntry)
                {   /* Rename in the image: the entry's own name offered --   */
                    /* the part after the last "/", the image's separator,    */
                    /* taken from the captured path, not from the pane.       */
                    sEntry = objNameRequest.RQT_lstPaths[0];
                    iSlash = sEntry.LastIndexOf('/');
                    sInitial = sEntry.Substring(iSlash + 1);
                    sCaption = "Rename in the image";
                    sPrompt = "New name for " + sInitial + ":";
                }
                else
                {   /* Any other edit: it asks for no name, so Cancel. */
                    return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
                }
            }

            if (!objQuestion.QST_ReadDraft(out sDraft))
            {   /* No draft yet -- the first showing: the offered name is    */
                /* the draft.  A draft that exists is used as it is, empty   */
                /* included, so a re-shown question keeps what was typed.    */
                sDraft = sInitial;
                objQuestion.QST_StoreDraft(objPresentation, sDraft);
            }

            /* The typing handler, bound to THIS display now, before the       */
            /* dialog's controls are made: if anything reentrant runs and a    */
            /* newer display appears, this dialog's typing still goes to its   */
            /* own display, which the question then refuses.                   */
            objUpdates = new QDU_clsDraftUpdates(objQuestion, objPresentation, null, null, false);

            /* The dialog, with the draft in its box. */
            dlgName = new PRM_clsPromptForm(Qpr_fntDialog, sCaption, sPrompt, sDraft);
            using (dlgName)
            {   /* The dialog is disposed when the block ends, after its      */
                /* text is read.  The owner's icon; the typing handler; and   */
                /* attached before it is shown, so an Exit closes it.         */
                dlgName.Icon = frmOwner.Icon;
                dlgName.PRM_BindDraft(objUpdates.QDU_OnNameChanged);
                QWN_clsQuestionWindows.QWN_Attach(dlgName);
                if (!objQuestion.QST_IsCurrent(objPresentation))
                {   /* An Exit replaced this display while the dialog was   */
                    /* being made: never shown; Cancel, which is refused.   */
                    return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
                }

                /* Shown and waited for.  Typing only updates the draft;   */
                /* only OK answers.                                        */
                dlgName.ShowDialog(frmOwner);
                if (!dlgName.PRM_bAccepted)
                {   /* Cancelled or closed (an Exit's close included): the   */
                    /* draft stays, nothing is made or renamed.              */
                    return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
                }

                /* OK: Yes with the text, read before the dialog goes.  The   */
                /* question still takes it only from a current display.       */
                return(new ANS_clsAnswer(ANK_enmAnswerKind.Yes, null, dlgName.PRM_sText));
            }
        }

        /*--------------------------------------------------------------------*/
        /* QPR_Present:                                                       */
        /*                                                                    */
        /* Shows one question display the worker published, and answers it    */
        /* through that display (MainCommand.cs calls it as the worker's      */
        /* views arrive).  The editors' close and ready questions and an      */
        /* input's close are handed on to the editor coordinator and the      */
        /* input registry, which answer them themselves; every other kind is  */
        /* shown here, over the window Qpr_FindOwner finds.  Whatever         */
        /* happens -- no owner, a kind with no words, a dialog that throws -- */
        /* the display is answered, Cancel when nothing better, so the worker */
        /* is never left waiting.  The one exception is a display already     */
        /* superseded, which is not answered at all: its answer would be      */
        /* refused.  Nothing that follows the answer starts here.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objPresentation : the display, or null.                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the display answered (or handed on, or superseded).     */
        /*--------------------------------------------------------------------*/
        internal void QPR_Present(PRS_clsPresentation objPresentation)
        {
            QST_clsQuestion objQuestion;
            ANK_enmAnswerKind enmAnswer;
            ANS_clsAnswer objReply;
            Form frmOwner;
            string sText;
            DialogResult enmResult;
            bool bAccepted;
            QSC_clsWindowScope objScope;
            string sCaption;
            MessageBoxIcon enmIcon;
            MessageBoxDefaultButton enmDefault;

            if (objPresentation == null || !objPresentation.PRS_objQuestion.QST_IsCurrent(objPresentation))
            {   /* No display, or one superseded before its call arrived    */
                /* (the call was queued; a Cancel or an Exit came first):   */
                /* nothing shown, and no answer, which would be refused.    */
                return;
            }

            /* The question.  An Exit takes down the dialogs of the question   */
            /* it interrupted (the worker keeps that question, suspended):     */
            /* closed here, before the Exit is shown, so the two never stand   */
            /* together and the old dialog's late answer is refused.           */
            objQuestion = objPresentation.PRS_objQuestion;
            if (objQuestion.QST_enmKind == QNK_enmQuestionKind.ExitRunning)
            {   /* An Exit: the suspended question's dialogs closed. */
                QWN_clsQuestionWindows.QWN_Supersede(objQuestion.QST_objSuspended);
            }

            if (objQuestion.QST_enmKind == QNK_enmQuestionKind.EditorClose)
            {   /* An editor's close question: the coordinator keeps it as   */
                /* that editor's close display and shows it now, or once     */
                /* the editor's own work and pending saves have finished     */
                /* (EUI_AskImageClose).                                      */
                Qpr_objEditors.EUI_AskImageClose(objPresentation);
                return;
            }

            if (objQuestion.QST_enmKind == QNK_enmQuestionKind.EditorsReady)
            {   /* Whether the editors already sent to the UI have finished      */
                /* being made (one can still be inside its own nested UI):       */
                /* the coordinator answers once they have (EUI_AskViewsReady).   */
                Qpr_objEditors.EUI_AskViewsReady(objPresentation);
                return;
            }

            if (objQuestion.QST_enmKind == QNK_enmQuestionKind.InputClose)
            {   /* An input's close: no dialog -- the input registry closes   */
                /* the input and answers when it has gone.                    */
                if (Qpr_objInputs == null)
                {   /* No registry (a guard: the one caller always gives      */
                    /* one): Cancel with the reason, since nothing here can   */
                    /* say the input has gone.                                */
                    objQuestion.QST_Reply(objPresentation, new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null,
                        "The input cancellation endpoint is unavailable."));
                }
                else
                {   /* The registry answers, once the input has gone or   */
                    /* cannot go (IWR_AskInputClose).                     */
                    Qpr_objInputs.IWR_AskInputClose(objPresentation);
                }
                return;
            }

            /* Every other kind is shown here.  The owner first; the answer   */
            /* starts as Cancel, which is what the display gets if there is   */
            /* no owner or the dialog cannot be shown -- a window that has    */
            /* gone must never leave the worker waiting.                      */
            frmOwner = Qpr_FindOwner(objQuestion);
            enmAnswer = ANK_enmAnswerKind.Cancel;
            objReply = null;
            try
            {   /* Anything that throws while a dialog is made or shown is   */
                /* caught below and answered Cancel.                         */
                if (frmOwner != null && !frmOwner.IsDisposed)
                {   /* An owner: this display's record opened (QWN_Begin), so    */
                    /* every dialog below attaches to it -- the message boxes    */
                    /* and every form the presenter shows -- and so can be       */
                    /* closed by an Exit; the finally below ends it.  The        */
                    /* Windows file dialogs are not forms and cannot be closed   */
                    /* this way: they finish normally first, and a queued Exit   */
                    /* then proceeds.                                            */
                    objScope = QWN_clsQuestionWindows.QWN_Begin(objPresentation);
                    try
                    {   /* The kind decides the dialog.  Each returns only an   */
                        /* answer; what follows it is the worker's.             */
                        if (objQuestion.QST_enmKind == QNK_enmQuestionKind.ViewerBinary)
                        {   /* A file that looks binary: which editor, if any. */
                            enmAnswer = Qpr_BinaryViewer(frmOwner, objQuestion.QST_objContext as VCH_clsViewerChoice, objPresentation);
                        }
                        else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.ImagePartition ||
                            objQuestion.QST_enmKind == QNK_enmQuestionKind.ImageOffset)
                        {   /* Where in a file or device the filesystem starts:   */
                            /* a partition from the table the worker read, or     */
                            /* an offset typed after a failed open at 0.  The     */
                            /* reply carries the offset.                          */
                            objReply = Qpr_ChooseImageOffset(frmOwner, objPresentation);
                            enmAnswer = objReply.ANS_enmKind;
                        }
                        else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.Conflict)
                        {   /* A copy's conflict: the five answers. */
                            enmAnswer = Qpr_Conflict(frmOwner, objQuestion.QST_objContext as PGD_clsProgressData);
                        }
                        else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.WritePartition)
                        {   /* A partition write: two or three answers, by the sizes. */
                            enmAnswer = Qpr_PartitionWrite(frmOwner, objPresentation);
                        }
                        else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.ManifestClash)
                        {   /* An extract's metadata file against an image   */
                            /* file of its name.                             */
                            enmAnswer = Qpr_Clash(frmOwner, objQuestion.QST_objContext as PGD_clsProgressData);
                        }
                        else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.FolderName)
                        {   /* A name, for a new folder or a rename.  The reply   */
                            /* carries the text.                                  */
                            objReply = Qpr_FolderName(frmOwner, objQuestion, objPresentation);
                            enmAnswer = objReply.ANS_enmKind;
                        }
                        else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.MakeFsOptions)
                        {   /* A format's options, asked by its own       */
                            /* sequence.  The reply carries the choice.   */
                            objReply = Qpr_MakeFsOptions(frmOwner, objQuestion, objPresentation);
                            enmAnswer = objReply.ANS_enmKind;
                        }
                        else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.NewImageFile)
                        {   /* New Image's file and size, asked by its own   */
                            /* sequence.  The reply carries the choice, or   */
                            /* Browse or Match with what was typed.          */
                            objReply = Qpr_NewImageFile(frmOwner, objQuestion, objPresentation);
                            enmAnswer = objReply.ANS_enmKind;
                        }
                        else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.NewImageDisk)
                        {   /* New Image's Match a Device: the disk, asked   */
                            /* by its sequence.  The reply carries it.       */
                            objReply = Qpr_NewImageDisk(frmOwner, objQuestion, objPresentation);
                            enmAnswer = objReply.ANS_enmKind;
                        }
                        else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.NewImagePath)
                        {   /* New Image's Browse: the file, asked by its   */
                            /* sequence.  The reply carries the path.       */
                            objReply = Qpr_NewImagePath(frmOwner, objQuestion);
                            enmAnswer = objReply.ANS_enmKind;
                        }
                        else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.DeviceChoice)
                        {   /* The device an operation works on, asked by its   */
                            /* own sequence.  The reply carries the choice.     */
                            objReply = Qpr_DeviceChoice(frmOwner, objQuestion, objPresentation);
                            enmAnswer = objReply.ANS_enmKind;
                        }
                        else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.DeviceRead)
                        {   /* Read Device's device, range, compression    */
                            /* and file, asked by its own sequence.  The   */
                            /* reply carries them.                         */
                            objReply = Qpr_DeviceRead(frmOwner, objQuestion, objPresentation);
                            enmAnswer = objReply.ANS_enmKind;
                        }
                        else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.DeviceWriteFile)
                        {   /* Write Device's image file, asked by its own   */
                            /* sequence.  The reply carries the path.        */
                            objReply = Qpr_DeviceWriteFile(frmOwner, objQuestion);
                            enmAnswer = objReply.ANS_enmKind;
                        }
                        else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.PartitionImageFile)
                        {   /* Partition/Format > Image File's disk image, asked   */
                            /* by its own sequence.  The reply carries the path.   */
                            objReply = Qpr_PartitionImageFile(frmOwner, objQuestion);
                            enmAnswer = objReply.ANS_enmKind;
                        }
                        else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.PartitionFile)
                        {   /* A partition Read's or Write's file, asked by its   */
                            /* own sequence.  The reply carries the path.         */
                            objReply = Qpr_PartitionFile(frmOwner, objQuestion);
                            enmAnswer = objReply.ANS_enmKind;
                        }
                        else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.NewTableKind)
                        {   /* A new table's kind, asked by New Table's (or New   */
                            /* Image's first) sequence.  The reply carries it.    */
                            objReply = Qpr_NewTableKind(frmOwner, objQuestion, objPresentation);
                            enmAnswer = objReply.ANS_enmKind;
                        }
                        else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.AddPartition)
                        {   /* A new partition, asked by Add Partition's   */
                            /* sequence.  The reply carries it.            */
                            objReply = Qpr_AddPartition(frmOwner, objQuestion, objPresentation);
                            enmAnswer = objReply.ANS_enmKind;
                        }
                        else
                        {   /* Every other kind is a Yes / No question, worded   */
                            /* by Qpr_Warning; "" means it cannot be worded,     */
                            /* and the Cancel already set stands.                */
                            sText = Qpr_Warning(objQuestion);
                            if (sText.Length != 0)
                            {   /* Worded: shown in the program's message box   */
                                /* over the owner.                              */
                                if (!objQuestion.QST_IsCurrent(objPresentation))
                                {   /* Superseded meanwhile: not shown, and   */
                                    /* not answered (the finally still ends   */
                                    /* the record).                           */
                                    return;
                                }

                                /* Each kind's caption, icon and default        */
                                /* button.  No is the default for anything      */
                                /* that changes data; Yes only for choosing     */
                                /* an editor (which changes nothing), growing   */
                                /* a partition in the table copy (nothing is    */
                                /* written), the restart as Administrator and   */
                                /* the capacity test -- why those last two      */
                                /* default to Yes is not recorded.              */
                                if (objQuestion.QST_enmKind == QNK_enmQuestionKind.Risk)
                                {   /* The risk warnings: their own box, with   */
                                    /* the red heading, No the default.         */
                                    enmResult = MSG_clsMessageForm.MSG_ShowWarned(frmOwner, Qpr_fntDialog, Qpr_fntHeading,
                                        "WARNING: YOU DO THIS AT YOUR OWN RISK", sText, "BifrostFM",
                                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                                }
                                else
                                {   /* Every other kind: the plain message   */
                                    /* box.  It starts as "BifrostFM", a     */
                                    /* warning, No the default; the kinds    */
                                    /* below change what differs for them.   */
                                    sCaption = "BifrostFM";
                                    enmIcon = MessageBoxIcon.Warning;
                                    enmDefault = MessageBoxDefaultButton.Button2;
                                    if (objQuestion.QST_enmKind == QNK_enmQuestionKind.WriteDevice)
                                    {   /* An image to a device: its own   */
                                        /* caption.                        */
                                        sCaption = "Write Device";
                                    }
                                    else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.ExitRunning &&
                                        objQuestion.QST_objRequest != null &&
                                        objQuestion.QST_objRequest.RQT_objPayload is ELA_clsElevationArgs)
                                    {   /* The restart as Administrator: the     */
                                        /* device action as the caption, a       */
                                        /* question, Yes the default.  A plain   */
                                        /* Exit keeps the warning and No.        */
                                        sCaption = ((ELA_clsElevationArgs)objQuestion.QST_objRequest.RQT_objPayload).ELA_sWhat;
                                        enmIcon = MessageBoxIcon.Question;
                                        enmDefault = MessageBoxDefaultButton.Button1;
                                    }
                                    else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.DeleteEntries)
                                    {   /* Deleting from the image: its own    */
                                        /* caption; the warning and No stay.   */
                                        sCaption = "Delete from the image?";
                                    }
                                    else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.DisableRiskWarnings ||
                                        objQuestion.QST_enmKind == QNK_enmQuestionKind.ConfirmDisableRiskWarnings)
                                    {   /* Either of the two questions that    */
                                        /* turn the risk warnings off: one     */
                                        /* caption; the warning and No stay.   */
                                        sCaption = "Risk Warnings";
                                    }
                                    else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.TestCapacity)
                                    {   /* The capacity test: a question, Yes   */
                                        /* (Run) the default.                   */
                                        sCaption = "Test Capacity";
                                        enmIcon = MessageBoxIcon.Question;
                                        enmDefault = MessageBoxDefaultButton.Button1;
                                    }
                                    else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.Format)
                                    {   /* Making a filesystem: its own        */
                                        /* caption; the warning and No stay.   */
                                        sCaption = "Make Filesystem";
                                    }
                                    else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.ErasePartition)
                                    {   /* A partition's erase: its own        */
                                        /* caption; the warning and No stay.   */
                                        sCaption = "Erase Partition";
                                    }
                                    else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.GrowPartition)
                                    {   /* Growing a partition in memory: a   */
                                        /* question, Yes the default --       */
                                        /* nothing is written by it.          */
                                        sCaption = "Expand Filesystem";
                                        enmIcon = MessageBoxIcon.Question;
                                        enmDefault = MessageBoxDefaultButton.Button1;
                                    }
                                    else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.DiscardEdits)
                                    {   /* Refresh over unwritten edits: a     */
                                        /* question, not a warning; No (keep   */
                                        /* them) stays the default.            */
                                        sCaption = "Refresh";
                                        enmIcon = MessageBoxIcon.Question;
                                    }
                                    else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.ReplaceChecksum)
                                    {   /* A checksum file that may be the     */
                                        /* publisher's: its own caption; the   */
                                        /* warning and No (keep it) stay.      */
                                        sCaption = "Checksum File";
                                    }
                                    else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.WriteTable)
                                    {   /* Writing the table -- named apart     */
                                        /* from making a filesystem, which it   */
                                        /* is not.                              */
                                        sCaption = "Write Table";
                                    }
                                    else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.Repair &&
                                        objQuestion.QST_objRequest != null &&
                                        objQuestion.QST_objRequest.RQT_enmKind == KND_enmKind.ExpandFilesystem)
                                    {   /* Expand Filesystem (one Yes for the    */
                                        /* grow and the check and repair         */
                                        /* first): its caption and a question    */
                                        /* icon, but No stays the default.       */
                                        /* The check window's Repair keeps the   */
                                        /* plain caption and the warning.        */
                                        sCaption = "Expand Filesystem";
                                        enmIcon = MessageBoxIcon.Question;
                                    }
                                    else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.ViewerFallback)
                                    {   /* The text editor instead: it changes   */
                                        /* nothing in either filesystem, so a    */
                                        /* question, Yes the default.            */
                                        sCaption = "Open As";
                                        enmIcon = MessageBoxIcon.Question;
                                        enmDefault = MessageBoxDefaultButton.Button1;
                                    }
                                    else if (objQuestion.QST_enmKind == QNK_enmQuestionKind.ViewerLargeText)
                                    {   /* The hex editor instead of a text   */
                                        /* file too large: likewise changes   */
                                        /* nothing, Yes the default.          */
                                        sCaption = "Open";
                                        enmIcon = MessageBoxIcon.Question;
                                        enmDefault = MessageBoxDefaultButton.Button1;
                                    }

                                    /* The program's message box    */
                                    /* (MessageForm.cs), which      */
                                    /* attaches itself to this      */
                                    /* display and is disposed by   */
                                    /* its own call.                */
                                    enmResult = MSG_clsMessageForm.MSG_Show(frmOwner, Qpr_fntDialog, sText, sCaption,
                                        MessageBoxButtons.YesNo, enmIcon, enmDefault);
                                }

                                /* Only the Yes button is a Yes.  No, the box   */
                                /* closed, or an Exit's close are all No.       */
                                enmAnswer = ANK_enmAnswerKind.No;
                                if (enmResult == DialogResult.Yes)
                                {   /* Yes pressed on this display. */
                                    enmAnswer = ANK_enmAnswerKind.Yes;
                                }
                            }
                        }
                    }
                    finally
                    {   /* The record ended however the dialog returned --    */
                        /* answered, closed by an Exit, or thrown -- so the   */
                        /* current record moves back out and a later Exit     */
                        /* finds no stale popups.                             */
                        QWN_clsQuestionWindows.QWN_End(objScope);
                    }
                }
            }
            catch (Exception exQuestion)
            {   /* A dialog threw: logged to the debug log, and answered    */
                /* Cancel -- never a Yes, and never left to escape on the   */
                /* UI thread.  A reply already built is dropped with it.    */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("command question", exQuestion.ToString());
                }

                /* Answered Cancel, and any reply already built dropped. */
                enmAnswer = ANK_enmAnswerKind.Cancel;
                objReply = null;
            }

            /* The reply.  The name and offset questions built their own      */
            /* (with the text or the offset); every other kind gets a plain   */
            /* one from the answer.                                           */
            if (objReply == null)
            {   /* No reply built: the plain answer, nothing more. */
                objReply = new ANS_clsAnswer(enmAnswer, null);
            }

            /* Given to the question through this display.  It is taken only   */
            /* if this display is still current -- a dialog an Exit closed     */
            /* returns here too, and its answer is then refused.               */
            bAccepted = objQuestion.QST_Reply(objPresentation, objReply);
            if (bAccepted && (objQuestion.QST_enmKind == QNK_enmQuestionKind.FolderName ||
                objQuestion.QST_enmKind == QNK_enmQuestionKind.ImagePartition || objQuestion.QST_enmKind == QNK_enmQuestionKind.ImageOffset))
            {   /* A name or offset question answered: its draft is done       */
                /* with.  Only an accepted answer clears it -- a refused one   */
                /* leaves the draft to the display that superseded this one.   */
                /* QWN_End, which ran before this reply, could not clear it:   */
                /* the question was not answered yet.                          */
                objQuestion.QST_ClearDraft();
            }

            if (bAccepted && objQuestion.QST_enmKind == QNK_enmQuestionKind.ExitRunning && enmAnswer == ANK_enmAnswerKind.Yes)
            {   /* An Exit taken: the hex and structured editors' snapshot   */
                /* jobs still running on this side are told to stop, so a    */
                /* late snapshot is deleted rather than handed to a worker   */
                /* that is stopping (EUI_AbortSnapshots).  The worker        */
                /* stops its own work itself.                                */
                try
                {   /* A failure here cannot take back the Exit, which is   */
                    /* already answered.                                    */
                    Qpr_objEditors.EUI_AbortSnapshots();
                }
                catch (Exception exAbort)
                {   /* Logged to the debug log only; each job still reports   */
                    /* its own return when it ends.                           */
                    if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                    {   /* Only while the debug log is kept: otherwise the line is never built. */
                        DBGLOG_clsDebugLog.DBGLOG_Write("snapshot abort", exAbort.ToString());
                    }
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* Qpr_ChooseImageOffset:                                             */
        /*                                                                    */
        /* The offset question: which partition of a file or device to open,  */
        /* from the table the worker read -- or, after an open at 0 failed,   */
        /* an offset to try.  Shown in the partition dialog                   */
        /* (PartitionForm.cs), given the worker's rows as they are; nothing   */
        /* is read again here.  The offset box's text is kept as the          */
        /* question's draft, so an Exit declined shows it again as typed.     */
        /* Only the offset is returned; the worker checks it and opens.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmOwner        : the window it is shown over.                 */
        /*     objPresentation : this display, which alone may answer; its    */
        /*                       question's context holds the worker's facts. */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ANS_clsAnswer : SelectOffset with the offset, or Cancel (the   */
        /*                     dialog cancelled, the facts missing, or the    */
        /*                     display superseded).                           */
        /*--------------------------------------------------------------------*/
        private ANS_clsAnswer Qpr_ChooseImageOffset(Form frmOwner, PRS_clsPresentation objPresentation)
        {
            QST_clsQuestion objQuestion;
            FCD_clsFileChoiceData objFacts;
            QDU_clsDraftUpdates objUpdates;
            PRF_clsPartitionForm dlgParts;
            string sFound;
            string sName;
            string sRestoredText;
            bool bRestore;

            /* The worker's facts: the file or device and the table it read.   */
            /* Nothing here reads them again or takes the pane's source.       */
            objQuestion = objPresentation.PRS_objQuestion;
            objFacts = objQuestion.QST_objContext as FCD_clsFileChoiceData;
            if (objFacts == null || !objQuestion.QST_AdoptDraft(objPresentation))
            {   /* No facts, or this display is no longer current: nothing   */
                /* shown, and Cancel -- no open.                             */
                return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
            }

            /* This display has the draft (adopted above, before any control    */
            /* exists).  The draft's text is read as it is -- empty or half     */
            /* typed included, not as an offset -- to be put back in the box.   */
            bRestore = objQuestion.QST_ReadDraft(out sRestoredText);
            sFound = objFacts.FCD_sFound;
            if (objQuestion.QST_enmKind == QNK_enmQuestionKind.ImageOffset)
            {   /* The typed-offset retry: the line under the list explains   */
                /* the failed open at 0 instead of the table found.           */
                sFound = objFacts.FCD_sFallback;
            }

            /* The dialog's title: a device's description, or the file's   */
            /* name without its folders.                                   */
            if (objFacts.FCD_bDevice)
            {   /* A device: its description as the worker copied it. */
                sName = objFacts.FCD_sDeviceName;
            }
            else
            {   /* A file: its name from the captured path. */
                sName = Path.GetFileName(objFacts.FCD_objFile.TGT_sPath);
            }

            /* The dialog, given the worker's rows themselves (not a copy).   */
            /* It hands back an offset only; the worker checks the range.     */
            dlgParts = new PRF_clsPartitionForm(Qpr_fntDialog, sName, objFacts.FCD_lstParts, sFound);
            using (dlgParts)
            {   /* The dialog is disposed when the block ends, after its        */
                /* offset is read.  First the draft's handlers, bound to this   */
                /* display, and the owner's icon; a draft is put back into      */
                /* the offset box now, over the row the dialog chose when it    */
                /* was made.                                                    */
                objUpdates = new QDU_clsDraftUpdates(objQuestion, objPresentation, dlgParts, sRestoredText, bRestore);
                dlgParts.Icon = frmOwner.Icon;
                if (bRestore && objQuestion.QST_IsCurrent(objPresentation))
                {   /* A draft, and still current: put back. */
                    dlgParts.PRF_SetOffsetDraft(sRestoredText);
                }

                /* Its Shown puts the draft back again, after the dialog's   */
                /* controls are made -- the dialog selects its row only in   */
                /* its constructor, and whether the list's selection fills   */
                /* the offset box again as it is shown is not established.   */
                /* Its FormClosing keeps the box's text as the draft, even   */
                /* when an Exit is the one closing it.  Both go to this      */
                /* display only, so an older dialog still unwinding cannot   */
                /* overwrite a newer one's draft.  Then it is attached, so   */
                /* an Exit closes it.                                        */
                dlgParts.Shown += objUpdates.QDU_OnShownOffset;
                dlgParts.FormClosing += objUpdates.QDU_OnClosingOffset;
                QWN_clsQuestionWindows.QWN_Attach(dlgParts);
                if (!objQuestion.QST_IsCurrent(objPresentation))
                {   /* An Exit replaced this display while the dialog was   */
                    /* being made: never shown; Cancel, which is refused.   */
                    return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
                }

                /* Shown and waited for.  Only OK, with an offset the dialog   */
                /* accepts, answers.                                           */
                dlgParts.ShowDialog(frmOwner);
                if (!dlgParts.PRF_bAccepted)
                {   /* Cancelled or closed: no offset, and no partition   */
                    /* guessed.                                           */
                    return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
                }

                /* OK: the offset in bytes, read before the dialog goes.   */
                /* The worker checks it against the facts it read before   */
                /* anything is opened.                                     */
                return(new ANS_clsAnswer(ANK_enmAnswerKind.SelectOffset, null, "",
                    new OFC_clsOffsetChoice(dlgParts.PRF_ullOffset)));
            }
        }

        /*--------------------------------------------------------------------*/
        /* QPR_RawWriteWarning:                                               */
        /*                                                                    */
        /* The text of the Write Device question: which image file, what it   */
        /* holds (its format and size), how much of it, onto which device --  */
        /* from the request and from the image facts the worker read in this  */
        /* run, never from a picker or the panes.  Internal, but only         */
        /* Qpr_Warning calls it.                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the request: the raw copy's arguments and the     */
        /*                  device target; or null.                           */
        /*     objFacts   : the image file's facts read in this run, or null. */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the whole question, or "" when either end cannot be   */
        /*              named from what was given (answered Cancel).          */
        /*--------------------------------------------------------------------*/
        internal static string QPR_RawWriteWarning(RQT_clsRequest objRequest, RWF_clsRawWriteFacts objFacts)
        {
            RWA_clsRawArgs objArgs;
            string sAbout;
            string sWhat;
            string sUnknown;
            string sDevice;
            string sOffsets;

            /* The raw copy's arguments, from the request only -- neither   */
            /* end is ever taken from the main window's fields.             */
            objArgs = null;
            if (objRequest != null)
            {   /* A request: its raw arguments, if that is what it carries. */
                objArgs = objRequest.RQT_objPayload as RWA_clsRawArgs;
            }

            if (objArgs == null || objFacts == null || objRequest.RQT_objTarget == null)
            {   /* No arguments, no facts, or no device: "".  Without the   */
                /* facts the file's format is unknown, and it is never      */
                /* assumed plain.                                           */
                return("");
            }

            /* The file by its name, with what it holds in the main window's    */
            /* own words (MAIN_ImageFileText -- the format and the sizes), so   */
            /* this text and the pane's never describe an image two ways.       */
            sAbout = MAIN_clsMainForm.MAIN_ImageFileText(objFacts.RWF_stInfo);
            sWhat = Path.GetFileName(objArgs.RWA_sFrom) + " (" + sAbout + ")";
            if (objArgs.RWA_ullFrom == 0 && objArgs.RWA_ullLength != 0)
            {   /* A length given from the image's start: only that much of   */
                /* it is written, which the question says in capitals.        */
                sWhat = "THE FIRST " + DVF_clsDeviceForm.DVF_SizeText(objArgs.RWA_ullLength) + " OF " + sWhat;
            }

            /* An image whose size the file does not record (a .gz, or a     */
            /* .zst without it), written whole, cannot be measured against   */
            /* the device first: the write learns the device is too small    */
            /* only when it fills it, so the question says so.               */
            sUnknown = "";
            if (objArgs.RWA_ullLength == 0 && objFacts.RWF_stInfo.IFI_ullImageBytes == ENG_clsNative.ENG_SIZE_UNKNOWN)
            {   /* Size unknown and the whole image: the warning. */
                sUnknown = "The image's size is not recorded in the file, so it cannot be checked against the device first: " +
                    "if it is larger, the write stops with an error when the device is full.\n\n";
            }

            /* Offsets other than 0 at either end -- which the program's own   */
            /* Write Device never makes, but a request may carry -- are said   */
            /* in bytes, rather than the write being called "the first" of     */
            /* anything.                                                       */
            sOffsets = "";
            if (objArgs.RWA_ullFrom != 0 || objArgs.RWA_ullTo != 0)
            {   /* An offset at either end: both said. */
                sOffsets = "Source offset: " + objArgs.RWA_ullFrom + " bytes; destination offset: " + objArgs.RWA_ullTo + " bytes.\n\n";
            }

            /* The device by its description when it has one, with its path   */
            /* in brackets unless the description already ends with it: a     */
            /* description may add to the path, never hide it.                */
            sDevice = objArgs.RWA_sTo;
            if (objRequest.RQT_objTarget.TGT_sDeviceText.Length != 0)
            {   /* Described: the description, as captured. */
                sDevice = objRequest.RQT_objTarget.TGT_sDeviceText;
            }

            if (sDevice != objArgs.RWA_sTo && !sDevice.EndsWith(" (" + objArgs.RWA_sTo + ")", System.StringComparison.OrdinalIgnoreCase))
            {   /* The description does not end with the path: the path   */
                /* added.                                                 */
                sDevice += " (" + objArgs.RWA_sTo + ")";
            }

            /* The question: what is written, onto what, any offsets, the     */
            /* unknown-size warning, then the loss, the volume lock and the   */
            /* read-back (every device write is read back and compared --     */
            /* CommandRawSequence.cs).  QPR_Present gives it No as the        */
            /* default.                                                       */
            return("WRITE " + sWhat + "\n\nTO THE DEVICE " + sDevice + "?\n\n" + sOffsets + sUnknown +
                "EVERYTHING ON THAT DEVICE WILL BE LOST. Every volume on it is locked and dismounted while it is written, " +
                "and the data is read back and compared afterwards.\n\nAre you sure this is the right device?");
        }

        /*---------------------------------------------------------------------*/
        /* Qpr_PartitionWrite:                                                 */
        /*                                                                     */
        /* The confirmation before a file is written into one partition:       */
        /* plain, not a second risk warning (the partition window's warning    */
        /* on opening is the serious one), naming both the file and the        */
        /* partition, with the safe answer as the default.  Its                */
        /* shape follows the two sizes the worker inspected:                   */
        /*   the same size -- Yes writes, No does not;                         */
        /*   the file LARGER -- Yes writes only the part that fits,            */
        /*     stopping exactly at the partition's end; No writes nothing;     */
        /*   the file SMALLER -- Yes writes it and zeros the rest of the       */
        /*     partition, No writes it and leaves the rest, Cancel writes      */
        /*     nothing (the default).                                          */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     frmOwner        : the partition window, which owns the box.     */
        /*     objPresentation : this display's capability, carrying the       */
        /*                       inspected facts (PWF_clsPartitionWriteFacts). */
        /*                                                                     */
        /* Returns:                                                            */
        /*     ANK_enmAnswerKind : Yes, No or Cancel as above; Cancel for a    */
        /*                         missing fact or a superseded display.       */
        /*---------------------------------------------------------------------*/
        private ANK_enmAnswerKind Qpr_PartitionWrite(Form frmOwner, PRS_clsPresentation objPresentation)
        {
            PWF_clsPartitionWriteFacts objFacts;
            MessageBoxButtons enmButtons;
            MessageBoxDefaultButton enmDefault;
            DialogResult enmResult;
            string sText;
            string sWhat;

            /* Only the worker's inspected facts can word this; without them   */
            /* nothing is written.                                             */
            objFacts = objPresentation.PRS_objQuestion.QST_objContext as PWF_clsPartitionWriteFacts;
            if (objFacts == null || objFacts.PWF_ullImageBytes == 0 || objFacts.PWF_ullPartBytes == 0)
            {   /* Missing or empty facts: no write.  A guard for the sizes:   */
                /* the worker refuses an empty image, and one whose size is    */
                /* unknown, before it asks (CommandRawSequence.cs), so the     */
                /* comparison below never meets ENG_SIZE_UNKNOWN.              */
                return(ANK_enmAnswerKind.Cancel);
            }

            /* The file and what is in it: a compressed file's image is      */
            /* decompressed as it is written, and its size is the image's.   */
            sWhat = objFacts.PWF_sFile;
            if (objFacts.PWF_uiKind != ENG_clsNative.ENG_IMAGE_RAW)
            {   /* Say so, since the file on disk is smaller than what is written. */
                sWhat += " (compressed; it is decompressed as it is written)";
            }

            /* The words and the buttons, by the sizes; the answers each   */
            /* gives are the header's three shapes.                        */
            if (objFacts.PWF_ullImageBytes > objFacts.PWF_ullPartBytes)
            {   /* LARGER than the partition: Yes / No, No the default --   */
                /* a Yes writes only what fits.                             */
                sText = "The image in " + sWhat + " holds " + DVF_clsDeviceForm.DVF_SizeText(objFacts.PWF_ullImageBytes) +
                    ", more than " + objFacts.PWF_sPlace + " (" + DVF_clsDeviceForm.DVF_SizeText(objFacts.PWF_ullPartBytes) + ").\n\n" +
                    "Write only the part that fits, stopping exactly at the end of the partition?\n" +
                    "What is in the partition now is replaced; the rest of the image is not written.\n\n" +
                    "No writes nothing.";
                enmButtons = MessageBoxButtons.YesNo;
                enmDefault = MessageBoxDefaultButton.Button2;
            }
            else if (objFacts.PWF_ullImageBytes < objFacts.PWF_ullPartBytes)
            {   /* SMALLER than the partition: Yes / No / Cancel, Cancel   */
                /* the default -- the only shape where No still writes.    */
                sText = "Write " + sWhat + " (" + DVF_clsDeviceForm.DVF_SizeText(objFacts.PWF_ullImageBytes) + ") into " +
                    objFacts.PWF_sPlace + " (" + DVF_clsDeviceForm.DVF_SizeText(objFacts.PWF_ullPartBytes) + ")?\n" +
                    "The file is smaller than the partition.\n\n" +
                    "Yes: write the file, then zeros over the rest of the partition.\n" +
                    "No: write the file and leave the rest of the partition as it is.\n" +
                    "Cancel: write nothing.";
                enmButtons = MessageBoxButtons.YesNoCancel;
                enmDefault = MessageBoxDefaultButton.Button3;
            }
            else
            {   /* The same size: Yes / No, No the default. */
                sText = "Write " + sWhat + " into " + objFacts.PWF_sPlace + " (" +
                    DVF_clsDeviceForm.DVF_SizeText(objFacts.PWF_ullPartBytes) + ")?\n" +
                    "What is in the partition now is replaced.";
                enmButtons = MessageBoxButtons.YesNo;
                enmDefault = MessageBoxDefaultButton.Button2;
            }

            if (objFacts.PWF_bDevice)
            {   /* A device: its volumes are locked while it is written,   */
                /* and the write is read back and compared, as every       */
                /* partition write is (CommandRawSequence.cs sets the      */
                /* read-back).                                             */
                sText += "\n\nEvery volume on the device is locked and dismounted while it is written, and the data is read back and compared.";
            }

            if (!objPresentation.PRS_objQuestion.QST_IsCurrent(objPresentation))
            {   /* A superseded display must not appear after the Exit question. */
                return(ANK_enmAnswerKind.Cancel);
            }

            /* The box, in the program's dialog font, over the partition      */
            /* window; it attaches itself to this display (MessageForm.cs),   */
            /* so an Exit closes it.                                          */
            enmResult = MSG_clsMessageForm.MSG_Show(frmOwner, Qpr_fntDialog, sText, "Write Partition",
                enmButtons, MessageBoxIcon.Warning, enmDefault);

            if (enmResult == DialogResult.Yes)
            {   /* Write (only what fits / then the zeros, as worded). */
                return(ANK_enmAnswerKind.Yes);
            }

            if (enmResult == DialogResult.No && enmButtons == MessageBoxButtons.YesNoCancel)
            {   /* Smaller file: write it, leave the rest. */
                return(ANK_enmAnswerKind.No);
            }

            /* No (same size or larger), Cancel, or the box closed: nothing written. */
            return(ANK_enmAnswerKind.Cancel);
        }
    }
}
