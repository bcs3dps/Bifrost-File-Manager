/*----------------------------------------------------------------------------*/
/* CommandQuestions.cs                                                        */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The question a running command asks the user, as the worker and the        */
/* windows share it: the question itself (QST_clsQuestion), which outlives    */
/* any one popup showing it; the display reference each showing gets          */
/* (PRS_clsPresentation), which alone may answer while it is current; and     */
/* the answer (ANS_clsAnswer).  When an Exit takes a question down and the    */
/* user then says No, the same question is shown again under a new display,   */
/* with the text that had been typed into it.  Also the services a running    */
/* command's steps use (EXC_clsExecution): ask, check for an interrupt,       */
/* publish a view.  The waits and interrupts themselves are                   */
/* CommandInterrupts.cs's.                                                    */
/*----------------------------------------------------------------------------*/

/* The framework: ArgumentException (System), and the events a question's   */
/* wait uses (Threading).  The queue lock and the wake event are the        */
/* command system's, lent to each question.                                 */
using System;
using System.Threading;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* ANS_clsAnswer -- one answer, from the window that showed the question  */
    /* to the command waiting on it -- documentation table.                   */
    /*------------------------------------------------------------------------*/
    internal sealed class ANS_clsAnswer
    {
        internal ANK_enmAnswerKind ANS_enmKind;      // the answer: Yes, No, Cancel, a choice ...
        internal RQT_clsRequest ANS_objRequest;      // a request the answer carries -- an editor's staged Save, or a chosen save target -- or null
        internal string ANS_sText;                   // the text typed (a name ...); "" when there is none
        internal OFC_clsOffsetChoice ANS_objOffset;  // the offset chosen in the partition dialog; null for other questions
        internal object ANS_objValue;                // the value the question's own dialog chose, of the type its question kind names (MakeFsOptions an MFC_clsMakeFsChoice, NewImageFile an NIC_clsNewImageChoice, NewImageDisk a DEV_clsDisk, DeviceChoice a DCH_clsDeviceChoice, DeviceRead an RDI_clsDeviceReadIntent, NewTableKind an int, AddPartition an ADC_clsAddChoice); null for other questions, set by the presenter after construction

        /*--------------------------------------------------------------------*/
        /* ANS_clsAnswer:                                                     */
        /*                                                                    */
        /* An answer, with whatever it carries.                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     enmKind    : the answer.                                       */
        /*     objRequest : a request it carries, or null.                    */
        /*     sText      : the text typed; optional, "" by default, null     */
        /*                  taken as "".                                      */
        /*     objOffset  : the offset chosen; optional, null by default.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the answer.  Making it hands nothing over: a   */
        /*                     carried request (and its staged file) becomes  */
        /*                     the command's only when the reply is ACCEPTED  */
        /*                     (QST_Reply); a refused reply leaves it with    */
        /*                     whoever made it.                               */
        /*--------------------------------------------------------------------*/
        internal ANS_clsAnswer(ANK_enmAnswerKind enmKind, RQT_clsRequest objRequest,
            string sText = "", OFC_clsOffsetChoice objOffset = null)
        {
            /* The answer and its objects, kept by reference.  The command   */
            /* checks what it received before acting on it.                  */
            ANS_enmKind = enmKind;
            ANS_objRequest = objRequest;
            ANS_objOffset = objOffset;

            /* No dialog value yet: the presenter of a question that asks for   */
            /* one sets it once the dialog has returned.                        */
            ANS_objValue = null;

            /* The text, "" unless given -- an empty answer stays empty, and   */
            /* is never filled in from a control.  It is only input: it        */
            /* consents to nothing.                                            */
            ANS_sText = "";
            if (sText != null)
            {   /* The text as it was typed. */
                ANS_sText = sText;
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* QST_clsQuestion -- one question a command asks, from its showing to    */
    /* its answer.  The public fields say what is asked and of which window;  */
    /* the private ones -- its current display, its answer, its event and     */
    /* the typed draft -- change only under the command system's queue lock   */
    /* (the draft on the UI thread alone) -- documentation table.             */
    /*------------------------------------------------------------------------*/
    internal sealed class QST_clsQuestion
    {
        internal QNK_enmQuestionKind QST_enmKind;   // which question: its wording and its dialog
        internal RQT_clsRequest QST_objRequest;     // the asking command's request -- never what the panes show now
        internal long QST_lWindow;                  // the window to ask through; 0 is the main window
        internal QST_clsQuestion QST_objSuspended;  // for the Exit question: the command's question it took down; null otherwise
        internal object QST_objContext;             // what the question shows (a path, a conflict's facts ...); copies, no engine pointers

        /* The answer's machinery, under the command system's queue lock. */
        private object Qst_objQueue;                 // the command system's queue lock, lent -- the same lock posts take
        private ManualResetEvent Qst_evtAnswered;    // set once answered (or cancelled); this question's own, closed only by QST_EndWait
        private WaitHandle[] Qst_arrEvents;          // what the worker waits on: the answered event, then the system's wake event (lent)
        private PRS_clsPresentation Qst_objDisplay;  // the display that may answer now; null when hidden or answered
        private ANS_clsAnswer Qst_objAnswer;         // the first answer accepted; null until then; never replaced
        private bool Qst_bWaitEnded;                 // set once QST_EndWait has released the event (worker only)

        /* The typed text, kept for when the question is shown again --   */
        /* the UI thread's alone, apart from who may answer.              */
        private string Qst_sDraftText;                    // the text typed so far; null before any
        private bool Qst_bHasDraft;                       // true once a draft is kept -- so a deliberately empty draft differs from none
        private PRS_clsPresentation Qst_objDraftDisplay;  // the display whose updates to the draft are taken

        /*--------------------------------------------------------------------*/
        /* QST_clsQuestion:                                                   */
        /*                                                                    */
        /* A question, not yet shown or answered.  Its maker (SYS_Ask, or the */
        /* Exit question in CommandInterrupts.cs) must call QST_EndWait in a  */
        /* finally.                                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     enmKind      : which question.                                 */
        /*     objRequest   : the asking command's request.                   */
        /*     lWindow      : the window to ask through, 0 for the main one.  */
        /*     objSuspended : for the Exit question, the question it takes    */
        /*                    down; else null.                                */
        /*     objQueue     : the command system's queue lock.                */
        /*     evtWake      : the command system's wake event.                */
        /*     objContext   : what the question shows; optional, null by      */
        /*                    default.                                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the question; ArgumentException without the    */
        /*                     lock or the wake event.                        */
        /*--------------------------------------------------------------------*/
        internal QST_clsQuestion(QNK_enmQuestionKind enmKind, RQT_clsRequest objRequest,
            long lWindow, QST_clsQuestion objSuspended, object objQueue,
            AutoResetEvent evtWake, object objContext = null)
        {
            if (objQueue == null || evtWake == null)
            {   /* Without the system's own lock and wake event its answer   */
                /* and its interrupts could not be synchronised: refused.    */
                throw new ArgumentException("The question needs the command queue lock and wake event.");
            }

            /* What is asked, of whom, for which command -- fixed for the   */
            /* question's life, even while an Exit has it taken down.       */
            QST_enmKind = enmKind;
            QST_objRequest = objRequest;
            QST_lWindow = lWindow;
            QST_objSuspended = objSuspended;
            QST_objContext = objContext;
            Qst_objQueue = objQueue;

            /* The wait's two events: this question's answered event, then      */
            /* the system's wake event (so a posted Cancel or Exit wakes the    */
            /* wait).  No display yet -- nothing can answer until the worker    */
            /* shows it.  The array is made before the event, so if making it   */
            /* fails no handle has been taken that would need closing.          */
            Qst_arrEvents = new WaitHandle[2];
            Qst_evtAnswered = new ManualResetEvent(false);
            Qst_arrEvents[0] = Qst_evtAnswered;
            Qst_arrEvents[1] = evtWake;
        }

        /*--------------------------------------------------------------------*/
        /* QST_Present:                                                       */
        /*                                                                    */
        /* A new display for the question, to send to the window that shows   */
        /* it -- or none, when it is already showing or answered.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     PRS_clsPresentation : the display that may answer; null when   */
        /*                           one is current or it is answered.        */
        /*--------------------------------------------------------------------*/
        internal PRS_clsPresentation QST_Present()
        {
            lock (Qst_objQueue)
            {   /* Under the lock a reply also takes, so the test and the   */
                /* new display are one step against any reply.              */
                if (Qst_objAnswer != null || Qst_objDisplay != null)
                {   /* Answered, or already showing: nothing new. */
                    return(null);
                }

                /* A NEW display each time it is shown: a popup taken down by   */
                /* an Exit keeps the old one, which no longer matches, so its   */
                /* late reply cannot answer.                                    */
                Qst_objDisplay = new PRS_clsPresentation(this);

                /* The display, for the window. */
                return(Qst_objDisplay);
            }
        }

        /*--------------------------------------------------------------------*/
        /* QST_Suspend:                                                       */
        /*                                                                    */
        /* Take the question down (for the Exit question) without cancelling  */
        /* it: its popup can no longer answer, but the question and its       */
        /* typed text are kept for showing again.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when taken down; false when it had already been    */
        /*            answered (the answer stands).                           */
        /*--------------------------------------------------------------------*/
        internal bool QST_Suspend()
        {
            lock (Qst_objQueue)
            {   /* Under the reply's lock: either the reply won, or the   */
                /* question is taken down -- never half of each.          */
                if (Qst_objAnswer != null)
                {   /* Already answered: the answer, and anything it   */
                    /* carries, stand.                                 */
                    return(false);
                }

                /* No display may answer now.  The draft's owner is left as     */
                /* it is until a new display adopts the draft, so the closing   */
                /* popup can still store the user's last typed text.            */
                Qst_objDisplay = null;

                /* Down; the worker asks Exit and may show it again. */
                return(true);
            }
        }

        /*--------------------------------------------------------------------*/
        /* QST_Reply:                                                         */
        /*                                                                    */
        /* A window's answer, accepted only from the display that is current  */
        /* -- and only the first.                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objDisplay : the display the popup was given.                  */
        /*     objAnswer  : the answer.                                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when accepted (the answer, and anything it         */
        /*            carries, are the command's now); false when refused     */
        /*            (they stay with the window).                            */
        /*--------------------------------------------------------------------*/
        internal bool QST_Reply(PRS_clsPresentation objDisplay, ANS_clsAnswer objAnswer)
        {
            lock (Qst_objQueue)
            {   /* Under the queue lock, which cancelling and reading take   */
                /* too.                                                      */
                if (objDisplay == null || objAnswer == null ||
                    Qst_objAnswer != null || Qst_objDisplay != objDisplay)
                {   /* No display or answer, already answered, or not the    */
                    /* current display (taken down or shown again since):    */
                    /* refused.  This test is also what keeps a late reply   */
                    /* off the event once QST_EndWait has closed it -- the   */
                    /* question is answered by then.                         */
                    return(false);
                }

                /* The answer stored FIRST, the display retired, then the   */
                /* event set -- all before the lock is let go, so whoever   */
                /* reads under the lock next finds the answer whole.        */
                Qst_objAnswer = objAnswer;
                Qst_objDisplay = null;
                Qst_evtAnswered.Set();

                /* Accepted. */
                return(true);
            }
        }

        /*--------------------------------------------------------------------*/
        /* QST_Cancel:                                                        */
        /*                                                                    */
        /* Answer the question Cancel, unless it is already answered.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the question is answered (Cancel, or the earlier        */
        /*            answer) and its event set.                              */
        /*--------------------------------------------------------------------*/
        internal void QST_Cancel()
        {
            lock (Qst_objQueue)
            {   /* Under the reply's lock: a reply and a cancel cannot both   */
                /* win.                                                       */
                if (Qst_objAnswer != null)
                {   /* Answered already: that answer stands, and what it   */
                    /* carries is the command's to settle.                 */
                    return;
                }

                /* Cancel, carrying nothing; no display can answer after it.   */
                /* Then the event set, under the lock, as for a reply.         */
                Qst_objAnswer = new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null);
                Qst_objDisplay = null;
                Qst_evtAnswered.Set();
            }
        }

        /*--------------------------------------------------------------------*/
        /* QST_ReadAnswer:                                                    */
        /*                                                                    */
        /* The answer as it stands.                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ANS_clsAnswer : the answer; null while unanswered.  Readable   */
        /*                     even after QST_EndWait.                        */
        /*--------------------------------------------------------------------*/
        internal ANS_clsAnswer QST_ReadAnswer()
        {
            lock (Qst_objQueue)
            {   /* Read under the lock a reply stores under, so an answer    */
                /* is seen whole.  No event is touched, so a window's late   */
                /* clean-up may read it after the event has been closed.     */
                return(Qst_objAnswer);
            }
        }

        /*--------------------------------------------------------------------*/
        /* QST_IsCurrent:                                                     */
        /*                                                                    */
        /* Whether a popup's display may still answer.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objDisplay : the popup's display, or null.                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true while it is the current display and the question   */
        /*            is unanswered; false once taken down, replaced,         */
        /*            answered or cancelled.                                  */
        /*--------------------------------------------------------------------*/
        internal bool QST_IsCurrent(PRS_clsPresentation objDisplay)
        {
            lock (Qst_objQueue)
            {   /* References compared under the lock; no event is touched   */
                /* (it may already be closed).                               */
                return(objDisplay != null && Qst_objAnswer == null && Qst_objDisplay == objDisplay);
            }
        }

        /*--------------------------------------------------------------------*/
        /* QST_WaitForSignal:                                                 */
        /*                                                                    */
        /* The worker's wait: until the question is answered or the system's  */
        /* wake event is set (any post, a Cancel or an Exit among them).      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : woken; the caller then looks at the interrupts and the  */
        /*            answer.                                                 */
        /*--------------------------------------------------------------------*/
        internal void QST_WaitForSignal()
        {
            /* On the worker only, and never with the queue lock held: the     */
            /* window needs the lock to store an answer, and a post needs it   */
            /* to queue the Cancel or Exit that would end this wait.           */
            WaitHandle.WaitAny(Qst_arrEvents);
        }

        /*--------------------------------------------------------------------*/
        /* QST_EndWait:                                                       */
        /*                                                                    */
        /* The question's end, on the worker: answered Cancel if still open,  */
        /* then its event closed, once.  The answer stays readable.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the event released (or already released).               */
        /*--------------------------------------------------------------------*/
        internal void QST_EndWait()
        {
            if (Qst_bWaitEnded)
            {   /* Ended already: the event is closed and is not touched   */
                /* again.                                                  */
                return;
            }

            /* Answered first -- Cancel if nothing else has -- which also      */
            /* sets the event; the wait then returns at once.  An accepted     */
            /* answer is left as it is, and what it carries stays the          */
            /* command's to settle.  Because the question is now answered, a   */
            /* popup's late QST_Reply is refused before it could touch the     */
            /* closed event.                                                   */
            QST_Cancel();
            Qst_evtAnswered.WaitOne();

            /* This question's own event closed -- not the wake event, which   */
            /* is the system's.  No lock is held here.                         */
            Qst_evtAnswered.Close();
            Qst_bWaitEnded = true;
        }

        /*--------------------------------------------------------------------*/
        /* QST_AdoptDraft:                                                    */
        /*                                                                    */
        /* A new popup takes over the typed draft: from now on only its       */
        /* updates are kept.                                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objDisplay : the new popup's display.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when taken over; false when the display is no      */
        /*            longer current (the popup should give up).              */
        /*--------------------------------------------------------------------*/
        internal bool QST_AdoptDraft(PRS_clsPresentation objDisplay)
        {
            if (!QST_IsCurrent(objDisplay))
            {   /* Not current any more -- an Exit question may have taken   */
                /* the question down while the popup was being built.        */
                return(false);
            }

            /* This display owns the draft now (UI thread only), so a closing   */
            /* older popup cannot overwrite the text the new one starts from.   */
            Qst_objDraftDisplay = objDisplay;

            /* Taken over; the text kept so far (and whether there is any)   */
            /* is unchanged.                                                 */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* QST_ReadDraft:                                                     */
        /*                                                                    */
        /* The draft typed so far.                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sText : receives the text; null when there is none.            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when a draft is kept -- even an empty one.         */
        /*--------------------------------------------------------------------*/
        internal bool QST_ReadDraft(out string sText)
        {
            /* UI thread only, so no lock.  The text through the out   */
            /* parameter; the fields stay private.                     */
            sText = Qst_sDraftText;

            /* Whether one is kept -- an empty draft is not "none". */
            return(Qst_bHasDraft);
        }

        /*--------------------------------------------------------------------*/
        /* QST_StoreDraft:                                                    */
        /*                                                                    */
        /* Keep a popup's typed text -- if that popup still owns the draft.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objDisplay : the popup's display.                              */
        /*     sText      : the text, empty or unfinished as it is.           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : kept; or ignored, from a popup that no longer owns it.  */
        /*--------------------------------------------------------------------*/
        internal void QST_StoreDraft(PRS_clsPresentation objDisplay, string sText)
        {
            if (objDisplay == null || Qst_objDraftDisplay != objDisplay)
            {   /* A newer popup owns the draft, or it was cleared: ignored. */
                return;
            }

            /* Kept even from a popup closing while taken down: it may no    */
            /* longer answer, but its last text is what the question shows   */
            /* again after the Exit question is answered No.                 */
            Qst_sDraftText = sText;
            Qst_bHasDraft = true;
        }

        /*--------------------------------------------------------------------*/
        /* QST_ClearDraft:                                                    */
        /*                                                                    */
        /* Drop the draft once the question has its answer.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the draft and its owner gone; it answers nothing, and a */
        /*            second call does nothing more.                          */
        /*--------------------------------------------------------------------*/
        internal void QST_ClearDraft()
        {
            /* The owner cleared with the text, so a late popup handler        */
            /* cannot store into a question that has ended.  UI thread only.   */
            Qst_objDraftDisplay = null;
            Qst_sDraftText = null;
            Qst_bHasDraft = false;
        }
    }

    /*------------------------------------------------------------------------*/
    /* PRS_clsPresentation -- one showing of a question: the reference a      */
    /* popup is given, which may answer only while it is the question's       */
    /* current display -- documentation table.                                */
    /*------------------------------------------------------------------------*/
    internal sealed class PRS_clsPresentation
    {
        internal QST_clsQuestion PRS_objQuestion; // the question this showing is of

        /*--------------------------------------------------------------------*/
        /* PRS_clsPresentation:                                               */
        /*                                                                    */
        /* A display of a question (made by QST_Present).                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objQuestion : the question.                                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : a new reference, unlike every other -- its     */
        /*                     identity is the whole point.                   */
        /*--------------------------------------------------------------------*/
        internal PRS_clsPresentation(QST_clsQuestion objQuestion)
        {
            /* The question, not its event: a popup cleaning up late compares   */
            /* displays through the question, never touching an event it        */
            /* does not own and that may already be closed.                     */
            PRS_objQuestion = objQuestion;
        }
    }

    /*------------------------------------------------------------------------*/
    /* EXC_clsExecution -- what a running command's steps are given: its      */
    /* request, and the system's services (checkpoint, ask, publish) bound to */
    /* it.  Made by the worker for each command (Sys_ThreadMain) and used     */
    /* only while it runs -- documentation table.                             */
    /*------------------------------------------------------------------------*/
    internal sealed class EXC_clsExecution
    {
        private SYS_clsSystem Exc_objSystem;    // the command system (the only one)
        private RQT_clsRequest Exc_objRequest;  // the running command's request

        /*--------------------------------------------------------------------*/
        /* EXC_clsExecution:                                                  */
        /*                                                                    */
        /* The services for one running command.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSystem  : the command system.                               */
        /*     objRequest : the command's request.                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the services; no step may keep them after the  */
        /*                     command ends.                                  */
        /*--------------------------------------------------------------------*/
        internal EXC_clsExecution(SYS_clsSystem objSystem, RQT_clsRequest objRequest)
        {
            /* This command's request, held explicitly: a callback that runs   */
            /* late checks against it (SYS_Checkpoint) and so cannot act for   */
            /* a later command.                                                */
            Exc_objSystem = objSystem;
            Exc_objRequest = objRequest;
        }

        /*--------------------------------------------------------------------*/
        /* EXC_ReadRequest:                                                   */
        /*                                                                    */
        /* The running command's request, for a step to read.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RQT_clsRequest : the request itself (not a copy; nothing is    */
        /*                      handed over).                                 */
        /*--------------------------------------------------------------------*/
        internal RQT_clsRequest EXC_ReadRequest()
        {
            /* The request, without exposing the field. */
            return(Exc_objRequest);
        }

        /*--------------------------------------------------------------------*/
        /* EXC_Checkpoint:                                                    */
        /*                                                                    */
        /* The step's interrupt check (SYS_Checkpoint, CommandInterrupts.cs): */
        /* a Cancel, or a pending Exit asked about there.                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true to go on; false to stop.                           */
        /*--------------------------------------------------------------------*/
        internal bool EXC_Checkpoint()
        {
            /* Called by the steps and by the engine's callbacks alike, on   */
            /* the worker; an Exit question waits here, never on the UI      */
            /* thread.                                                       */
            return(Exc_objSystem.SYS_Checkpoint(Exc_objRequest));
        }

        /*--------------------------------------------------------------------*/
        /* EXC_Ask:                                                           */
        /*                                                                    */
        /* Ask the user a question for the running command (SYS_Ask).         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     enmKind    : which question.                                   */
        /*     lWindow    : the window to ask through, 0 for the main one.    */
        /*     objContext : what it shows; optional, null by default.         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ANS_clsAnswer : the answer, or Cancel when interrupted.        */
        /*--------------------------------------------------------------------*/
        internal ANS_clsAnswer EXC_Ask(QNK_enmQuestionKind enmKind, long lWindow,
            object objContext = null)
        {
            /* The window gives only the answer; what follows from it is the   */
            /* command's to decide once this returns.                          */
            return(Exc_objSystem.SYS_Ask(enmKind, Exc_objRequest, lWindow, objContext));
        }

        /*--------------------------------------------------------------------*/
        /* EXC_Show:                                                          */
        /*                                                                    */
        /* Publish a view for the running command (SYS_Show).                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     enmKind  : the view's kind.                                    */
        /*     objValue : its data, already copied (no engine pointers).      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the view handed to the system's publication call, which */
        /*            never waits on the UI thread.                           */
        /*--------------------------------------------------------------------*/
        internal void EXC_Show(VWK_enmViewKind enmKind, object objValue)
        {
            /* Addressed to this command's request; how it reaches the   */
            /* window is the publication call's business.                */
            Exc_objSystem.SYS_Show(new VWM_clsView(enmKind, Exc_objRequest, objValue));
        }
    }
}
