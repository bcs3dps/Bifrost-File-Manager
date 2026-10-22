/*----------------------------------------------------------------------------*/
/* CommandInterrupts.cs                                                       */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* How a command running on the worker is interrupted, and how it asks the    */
/* user something.  A running command calls SYS_Checkpoint between steps and  */
/* from the engine's callbacks; there a Cancel stops it, and an Exit pauses   */
/* it to ask whether the running work may be stopped -- consent first,        */
/* cancellation only after a Yes.  A command's own question (SYS_Ask) is      */
/* shown through the window that asked and waited for on the worker, and the  */
/* same wait watches for Cancel and Exit: an Exit hides the question, asks    */
/* its own, and on a No puts the first question back exactly as it was, with  */
/* what had been typed into it.  The windows only answer; the command decides */
/* what follows.                                                              */
/*----------------------------------------------------------------------------*/

/* The framework: the exception thrown when a second question is asked       */
/* while one is waiting.  The question's own wait handles are in its class   */
/* (CommandQuestions.cs).                                                    */
using System;

namespace BifrostFM
{
    internal sealed partial class SYS_clsSystem
    {
        /*--------------------------------------------------------------------*/
        /* SYS_Checkpoint:                                                    */
        /*                                                                    */
        /* Whether the running command may go on.  Called between its steps   */
        /* (Sys_Do) and from inside the engine's progress callbacks, it is    */
        /* where a Cancel is noticed, and where a pending Exit is asked about */
        /* before anything is stopped.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the request of the command asking -- it must be   */
        /*                  the one running.                                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true to go on; false to stop -- a Cancel, a confirmed   */
        /*            Exit, or a caller that is not the running command.      */
        /*--------------------------------------------------------------------*/
        internal bool SYS_Checkpoint(RQT_clsRequest objRequest)
        {
            bool bExit;

            lock (Sys_objQueue)
            {   /* The running request and the Exit facts are read together    */
                /* under the queue lock, which every post also takes, so the   */
                /* answer reflects one moment.                                 */
                if (Sys_objRunning != objRequest)
                {   /* Not the running command (a callback kept by work that    */
                    /* has ended, for one): told to stop, so it cannot act on   */
                    /* behalf of the command running now.                       */
                    return(false);
                }

                /* Ask about Exit now only if the user can answer.  Not while   */
                /* a drag holds the UI thread inside OLE's GetData              */
                /* (Sys_lDrag), and not while a modal value input -- a file     */
                /* dialog over a disabled window, the device picker, New        */
                /* Image, Properties -- is open (Sys_HasValueInput): Exit       */
                /* waits for those to return.  New Image's Match a Device       */
                /* reads devices on this worker while its dialog is open, so    */
                /* an Exit arriving at that read's checkpoint waits here too.   */
                bExit = Sys_bExitPending && Sys_lDrag == 0 && !Sys_HasValueInput();
            }

            if (bExit)
            {   /* Exit is pending: ask now, outside the lock, before the   */
                /* command is told to stop -- consent comes first.  A Yes   */
                /* sets the cancel and terminal flags tested below.         */
                Sys_ConfirmExit(null);
            }

            lock (Sys_objQueue)
            {   /* Read the stop facts again: the Exit answer, or a Cancel      */
                /* posted meanwhile, may have changed them.  A confirmed Exit   */
                /* stops every command but the Exit sequence itself, which      */
                /* must be able to finish the close the user agreed to.         */
                return(!Sys_bCancelled &&
                    (!Sys_bTerminal || objRequest.RQT_enmKind == KND_enmKind.Exit));
            }
        }

        /*--------------------------------------------------------------------*/
        /* Sys_PresentQuestion:                                               */
        /*                                                                    */
        /* Put a question on screen -- a new one, or one put back after a     */
        /* declined Exit -- or cancel it when nobody can show it.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objQuestion : the question.                                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the question is shown, or answered Cancel, so its wait  */
        /*            always ends.                                            */
        /*--------------------------------------------------------------------*/
        private void Sys_PresentQuestion(QST_clsQuestion objQuestion)
        {
            PRS_clsPresentation objPresentation;

            /* A fresh display for it (QST_Present).  Each showing gets a new   */
            /* display reference, and only the current one may answer, so a     */
            /* popup taken down by Exit cannot answer the one shown after it.   */
            /* A question already answered, or already showing, gets none and   */
            /* is not shown again.  Then sent to the asking window; when the    */
            /* send fails (the window is gone, or its host throws) no answer    */
            /* could ever come, so the question is answered Cancel instead of   */
            /* being waited for for ever.                                       */
            objPresentation = objQuestion.QST_Present();
            if (objPresentation != null && !SYS_Show(new VWM_clsView(
                VWK_enmViewKind.Question, objQuestion.QST_objRequest, objPresentation)))
            {   /* Nobody can show it: answered Cancel, which wakes the wait. */
                objQuestion.QST_Cancel();
            }
        }

        /*--------------------------------------------------------------------*/
        /* Sys_WaitQuestion:                                                  */
        /*                                                                    */
        /* Wait on the worker for a shown question's answer, dealing on the   */
        /* way with an Exit or a Cancel -- and nothing else: no queued        */
        /* request runs while a question waits.                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objQuestion    : the question, already shown.                  */
        /*     bInterruptible : true for a command's question; false for the  */
        /*                      Exit question itself, which neither a Cancel  */
        /*                      nor another Exit may take down.               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ANS_clsAnswer : the accepted answer; or Cancel when the        */
        /*                     command was stopped while it waited.           */
        /*--------------------------------------------------------------------*/
        private ANS_clsAnswer Sys_WaitQuestion(QST_clsQuestion objQuestion, bool bInterruptible)
        {
            ANS_clsAnswer objAnswer;
            VWK_enmViewKind enmTerminal;
            bool bExit;
            bool bCancel;

            /* The question's wait wakes on either of two events: its own       */
            /* answered event, or the worker's wake event, which every post     */
            /* sets -- so a posted Exit or Cancel wakes it.  The wait is made   */
            /* without the queue lock held, because the window needs that       */
            /* lock to store an answer and a post needs it to queue an Exit.    */
            for (;;)
            {   /* One wake per iteration: an answer, an interrupt, or a post   */
                /* that concerns neither.                                       */
                /* Sleep until one of the two events is set.                    */
                objQuestion.QST_WaitForSignal();
                /* Exit and Cancel, read under the queue lock.  An Exit is     */
                /* asked about at once unless a drag holds the UI thread.      */
                /* (SYS_Checkpoint's other condition -- no modal value input   */
                /* open -- is not tested here; whether a question can be       */
                /* waiting while one is open is not established.)  A Cancel    */
                /* is a Cancel request for this command, or an Exit already    */
                /* confirmed, which stops everything but the Exit sequence.    */
                lock (Sys_objQueue)
                {   /* Read the command-owned flags; nothing is changed here. */
                    bExit = bInterruptible && Sys_bExitPending && Sys_lDrag == 0;
                    bCancel = bInterruptible && (Sys_bCancelled ||
                        (Sys_bTerminal && Sys_objRunning.RQT_enmKind != KND_enmKind.Exit));
                }

                if (bExit)
                {   /* Exit is pending: its question replaces this one          */
                    /* (Sys_ConfirmExit).  On a No this question is put back,   */
                    /* the same question with what was typed into it.           */
                    Sys_ConfirmExit(objQuestion);
                }

                /* Read the flags again: a Yes to Exit has just set them.  A   */
                /* Cancel already seen stays seen.                             */
                lock (Sys_objQueue)
                {   /* Keep the earlier Cancel; add one the Exit answer made. */
                    bCancel = bCancel || (bInterruptible && (Sys_bCancelled ||
                        (Sys_bTerminal && Sys_objRunning.RQT_enmKind != KND_enmKind.Exit)));
                }

                if (bCancel)
                {   /* The command is to stop.  Stopping wins even over an     */
                    /* answer that arrived first: the question is answered     */
                    /* Cancel unless it already has an answer, and its popup   */
                    /* is told to close.                                       */
                    objQuestion.QST_Cancel();
                    SYS_Show(new VWM_clsView(VWK_enmViewKind.QuestionClose,
                        objQuestion.QST_objRequest, objQuestion));

                    /* The answer as it stands.  Reading it takes the queue     */
                    /* lock, and a reply stores its answer and signals before   */
                    /* giving that lock up, so an accepted answer is complete   */
                    /* here.  The answered event itself is waited for and       */
                    /* released by QST_EndWait, in the caller's finally.        */
                    objAnswer = objQuestion.QST_ReadAnswer();
                    if (objAnswer != null && objAnswer.ANS_objRequest != null)
                    {   /* The answer was accepted first and carries a request   */
                        /* of its own (an editor's Save, with its staged         */
                        /* content, or a chosen save target), which became       */
                        /* this command's to settle when it was accepted.  It    */
                        /* is refused here, not run.                             */
                        enmTerminal = VWK_enmViewKind.Refused;
                        if (objAnswer.ANS_objRequest.RQT_enmKind == KND_enmKind.SaveEditor)
                        {   /* A Save is answered as a save (EditorSaved), the   */
                            /* reply its editor waits for to leave "saving".     */
                            enmTerminal = VWK_enmViewKind.EditorSaved;
                        }

                        /* Its result first -- cancelled -- then its staged   */
                        /* content released (Sys_Release).  A target choice   */
                        /* has no content to release, but still gets the      */
                        /* refusal rather than a false "saved".               */
                        SYS_Show(new VWM_clsView(enmTerminal, objAnswer.ANS_objRequest,
                            new RSL_clsResult(SYS_CANCELLED,
                                "Cancellation stopped the interrupted editor action.", null)));
                        Sys_Release(objAnswer.ANS_objRequest);
                    }

                    /* Cancel, to the command: any accepted answer has been   */
                    /* settled above, and this command may not act on it.     */
                    return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
                }

                /* No interrupt: has it been answered?  The read takes the   */
                /* same lock the reply stores under, so an answer is seen    */
                /* whole or not at all; a wake with no answer (a post that   */
                /* concerned neither) goes round and waits again.            */
                objAnswer = objQuestion.QST_ReadAnswer();
                if (objAnswer != null)
                {   /* Answered: the answer goes to the waiting command. */
                    return(objAnswer);
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* SYS_Ask:                                                           */
        /*                                                                    */
        /* Ask the user one question for the running command and wait, on the */
        /* worker, for the answer -- the UI thread never waits on the worker. */
        /* One question at a time: a command waits on one question or none.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     enmKind    : which question (it selects the wording and the    */
        /*                  dialog).                                          */
        /*     objRequest : the running command's request.                    */
        /*     lWindow    : the window to ask through; zero for the main      */
        /*                  window.                                           */
        /*     objContext : what the question shows (a path, a conflict's     */
        /*                  facts ...); optional, null by default.            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ANS_clsAnswer : the answer; Cancel when the command was        */
        /*                     stopped while asking, the question could not   */
        /*                     be shown, or the command is a drag's extract.  */
        /*--------------------------------------------------------------------*/
        internal ANS_clsAnswer SYS_Ask(QNK_enmQuestionKind enmKind, RQT_clsRequest objRequest,
            long lWindow, object objContext = null)
        {
            QST_clsQuestion objQuestion;

            if (objRequest.RQT_enmKind == KND_enmKind.DragExtract)
            {   /* A drag's extract runs while the UI thread is held inside    */
                /* OLE's GetData, so no window could answer: Cancel at once,   */
                /* and the quiet extract stops rather than hang.               */
                return(new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
            }

            if (Sys_objQuestion != null)
            {   /* A question already waits.  The command runs on this one   */
                /* thread and waits on one question at a time, so a second   */
                /* ask is a fault in the calling code: thrown, not queued.   */
                throw new InvalidOperationException("Only one operation question may be pending.");
            }

            /* The question, holding the queue lock (which its showing and     */
            /* its reply also take) and the worker's wake event (so a post     */
            /* wakes its wait); it makes only its own answered event.  It is   */
            /* recorded as the one waiting.                                    */
            objQuestion = new QST_clsQuestion(enmKind, objRequest, lWindow,
                null, Sys_objQueue, Sys_evtWake, objContext);
            Sys_objQuestion = objQuestion;
            try
            {   /* Shown, then waited for; a failure to show is answered   */
                /* Cancel, so the wait always ends.                        */
                Sys_PresentQuestion(objQuestion);

                /* The answer goes back to the command, which decides what   */
                /* follows from it.                                          */
                return(Sys_WaitQuestion(objQuestion, true));
            }
            finally
            {   /* Whatever happened, even an exception: QST_EndWait answers   */
                /* a still-open question Cancel, waits for its event's         */
                /* signal and only then releases the event -- outside the      */
                /* queue lock -- leaving an accepted answer readable; and      */
                /* the question stops being the one waiting.                   */
                objQuestion.QST_EndWait();
                Sys_objQuestion = null;
            }
        }

        /*--------------------------------------------------------------------*/
        /* Sys_ConfirmExit:                                                   */
        /*                                                                    */
        /* Ask the user whether a pending Exit may stop the running command,  */
        /* in place of any question that command is waiting on.  A Yes makes  */
        /* the Exit terminal (the command is cancelled, and the worker runs   */
        /* the Exit when the command has unwound); a No drops the Exit and    */
        /* the command goes on, with its question put back.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSuspended : the command's question to take down while Exit  */
        /*                    is asked, or null at a checkpoint (no question  */
        /*                    waiting).                                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true for a confirmed Exit; false when it was declined   */
        /*            or there was no queued Exit to ask about.               */
        /*--------------------------------------------------------------------*/
        private bool Sys_ConfirmExit(QST_clsQuestion objSuspended)
        {
            RQT_clsRequest objExit;
            QST_clsQuestion objQuestion;
            ANS_clsAnswer objAnswer;
            bool bSuspended;
            int i;

            /* The queued Exit request, taken out of the waiting list (and     */
            /* only it -- nothing else queued runs).  While it is out of the   */
            /* list, Sys_bExitPending stays set until the answer, and that     */
            /* flag keeps ordinary posts out of the queue meanwhile            */
            /* (Sys_HasUrgent, CommandSystem.cs).                              */
            objExit = null;
            lock (Sys_objQueue)
            {   /* Find and remove the Exit under the lock posts take. */
                for (i = 0; i < Sys_lstWaiting.Count; i++)
                {   /* One queued request per iteration. */
                    if (Sys_lstWaiting[i].RQT_enmKind == KND_enmKind.Exit)
                    {   /* The Exit: this call now owns it -- to run it after   */
                        /* a Yes, or to drop it after a No.                     */
                        objExit = Sys_lstWaiting[i];
                        Sys_lstWaiting.RemoveAt(i);
                        break;
                    }
                }
            }

            if (objExit == null)
            {   /* No Exit queued: it has already been taken -- the running   */
                /* command is the Exit sequence itself.  Nothing to ask.      */
                return(false);
            }

            /* Take the command's question down (QST_Suspend): its popup can   */
            /* no longer answer, but the question and its typed text are       */
            /* kept, and nothing is cancelled.  When it has already been       */
            /* answered the answer stands, and it is not shown again after a   */
            /* No.                                                             */
            bSuspended = false;
            if (objSuspended != null)
            {   /* A command question is waiting: taken down, if unanswered. */
                bSuspended = objSuspended.QST_Suspend();
            }

            /* The Exit question, with the same queue lock and wake event; it   */
            /* carries the taken-down question, so the window can replace       */
            /* exactly that popup and keep its last typed text for when it is   */
            /* put back.  Waited for as not interruptible: neither a Cancel     */
            /* of the command nor another Exit may take it down.                */
            objQuestion = new QST_clsQuestion(QNK_enmQuestionKind.ExitRunning,
                objExit, 0, objSuspended, Sys_objQueue, Sys_evtWake);
            try
            {   /* Shown through the main window (owner 0), then waited for. */
                Sys_PresentQuestion(objQuestion);
                objAnswer = Sys_WaitQuestion(objQuestion, false);
            }
            finally
            {   /* The Exit question's event, released outside the lock. */
                objQuestion.QST_EndWait();
            }

            lock (Sys_objQueue)
            {   /* The answer applied under the lock posts take. */
                if (objAnswer != null && objAnswer.ANS_enmKind == ANK_enmAnswerKind.Yes)
                {   /* Yes: only now does Exit become terminal.  The running   */
                    /* command is cancelled, and the Exit is kept for the      */
                    /* worker, which runs it once that command has unwound     */
                    /* (Sys_ThreadMain takes Sys_objExit first).               */
                    Sys_bTerminal = true;
                    Sys_bCancelled = true;
                    Sys_objExit = objExit;
                }

                /* This Exit has been answered, either way: its pending flags   */
                /* are cleared, so posts are admitted again after a No.         */
                Sys_bExitPending = false;
                Sys_bExitHadRunning = false;
            }

            if (objAnswer == null || objAnswer.ANS_enmKind != ANK_enmAnswerKind.Yes)
            {   /* No (or no answer -- an Exit question that could not be    */
                /* shown is answered Cancel): the command goes on and its    */
                /* question keeps any accepted answer.  The Exit request's   */
                /* resources are released and it is reported as dropped.     */
                Sys_Release(objExit);
                SYS_Show(new VWM_clsView(VWK_enmViewKind.Dropped, objExit,
                    new RSL_clsResult(SYS_CANCELLED,
                        "Exit was declined; the operation continues.", null)));
                if (bSuspended)
                {   /* The question that was taken down is shown again: a       */
                    /* new display, so the old popup's late reply is refused,   */
                    /* with the text that had been typed into it.               */
                    Sys_PresentQuestion(objSuspended);
                }

                /* Declined: the checkpoint or the question wait carries on. */
                return(false);
            }

            if (objSuspended != null)
            {   /* Yes: the command's unanswered question is answered Cancel    */
                /* (an accepted answer is left alone -- its wait settles it).   */
                objSuspended.QST_Cancel();
            }

            /* Every queued request but the notifications is cancelled and    */
            /* answered now (Sys_CancelPending), so a queued editor save is   */
            /* settled before the Exit sequence asks that editor to close.    */
            Sys_CancelPending();

            /* Confirmed: the worker runs the Exit once the running command   */
            /* has returned.                                                  */
            return(true);
        }
    }
}
