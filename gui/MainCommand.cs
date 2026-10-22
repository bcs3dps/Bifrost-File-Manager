/*----------------------------------------------------------------------------*/
/* MainCommand.cs                                                             */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The main window's side of the command system: it turns the user's          */
/* clicks into requests posted to the worker (each carrying what it was       */
/* asked about, captured at the click), and shows what the worker sends       */
/* back -- results, listings, progress, questions, editors, the check and     */
/* partition windows.  It never orders filesystem work itself: the worker     */
/* owns what runs and when.                                                   */
/*----------------------------------------------------------------------------*/

/* The framework: the base types and exceptions (System), the lists and      */
/* tables of views and windows (Collections.Generic), the stage file a new   */
/* file's placeholder is written to (IO), and the windows (Windows.Forms).   */
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* DGI_clsDragInput -- a drag out of the image pane: what was selected,   */
    /* from which open, and the worker's hold that keeps other work from      */
    /* running while Windows' drag loop owns the UI -- documentation table.   */
    /*------------------------------------------------------------------------*/
    internal class DGI_clsDragInput
    {
        internal STV_clsStateView DGI_objSource;  // the state when the drag began: the open, its range and epoch
        internal long DGI_lToken;                 // the worker's hold for this drag; released (DragEnded) once Windows' drag loop returns
        internal string DGI_sMarker;              // a random string on the drag's data, by which a drop on the image pane itself knows its own drag
        internal string[] DGI_arrPaths;           // the selected entries' paths, a fresh array from the selection, never changed during the drag

        /*--------------------------------------------------------------------*/
        /* DGI_clsDragInput:                                                  */
        /*                                                                    */
        /* A drag's record, made once the worker has granted its hold.  Data  */
        /* only: nothing is read out of the image here (that happens only if  */
        /* the drop is outside the program).                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSource : the state when the drag began.                     */
        /*     lToken    : the hold the worker granted (above 0).             */
        /*     arrPaths  : the selected paths -- names Windows could not hold */
        /*                 included.                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the record; it throws for a missing part.      */
        /*--------------------------------------------------------------------*/
        internal DGI_clsDragInput(STV_clsStateView objSource, long lToken, string[] arrPaths)
        {
            if (objSource == null || objSource.STV_objTarget == null ||
                lToken <= 0 || arrPaths == null || arrPaths.Length == 0)
            {   /* No source, no hold or no selection: a drag cannot be    */
                /* made without all three -- thrown, as a program error.   */
                throw new ArgumentException("A reserved drag needs its complete source and selection.");
            }

            /* Kept as given.  The marker is made now: a drop on the image    */
            /* pane reads it to know the drag is its own, without reading     */
            /* anything out of the image.  The array is the selection's own   */
            /* fresh copy, so no further copy is made.                        */
            DGI_objSource = objSource;
            DGI_lToken = lToken;
            DGI_sMarker = Guid.NewGuid().ToString("N");
            DGI_arrPaths = arrPaths;
        }
    }

    /*------------------------------------------------------------------------*/
    /* NPW_clsPlaceholderWork -- New File's placeholder: the length the new   */
    /* file is made with, written into its stage file before the worker       */
    /* creates the entry -- documentation table.                              */
    /*------------------------------------------------------------------------*/
    internal class NPW_clsPlaceholderWork
    {
        private long Npw_lBytes; // the new file's length, bytes, as New File accepted it; 0 for a text or config file

        /*--------------------------------------------------------------------*/
        /* NPW_clsPlaceholderWork:                                            */
        /*                                                                    */
        /* The placeholder's writer, holding the length only: no stream is    */
        /* opened and no entry made here.                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lBytes : the length, already checked by New File.              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the writer.                                    */
        /*--------------------------------------------------------------------*/
        internal NPW_clsPlaceholderWork(long lBytes)
        {
            /* Kept as given.  The stage's own code opens, closes and   */
            /* deletes its stream; the worker makes the entry later.    */
            Npw_lBytes = lBytes;
        }

        /*---------------------------------------------------------------------*/
        /* NPW_Write:                                                          */
        /*                                                                     */
        /* The stage writer's callback: the stage file given the placeholder's */
        /* length (zeros).  Closing the stream, and deleting the stage if      */
        /* this throws, are the stage code's.                                  */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     stmOutput : the stage file's stream, borrowed.                  */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : the length set; a failure throws to the stage code.      */
        /*---------------------------------------------------------------------*/
        internal void NPW_Write(FileStream stmOutput)
        {
            /* The length set (the file reads as zeros); nothing more --   */
            /* the entry is the worker's to make.                          */
            stmOutput.SetLength(Npw_lBytes);
        }
    }

    /*------------------------------------------------------------------------*/
    /* CST_clsCheckStop -- the check window's Cancel for one run: it posts a  */
    /* Cancel for the run it was made for, so a late click cannot cancel the  */
    /* Repair that came after -- documentation table.                         */
    /*------------------------------------------------------------------------*/
    internal class CST_clsCheckStop
    {
        private CLI_clsClient Cst_objClient; // the client it posts through, borrowed
        private long Cst_lWindow;            // the check window's id
        private long Cst_lRun;               // the serial of the run it cancels, fixed when made -- never a later Repair's

        /*--------------------------------------------------------------------*/
        /* CST_clsCheckStop:                                                  */
        /*                                                                    */
        /* The Cancel for one shown check run.                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objClient : the client, borrowed.                              */
        /*     lWindow   : the check window's id.                             */
        /*     lRun      : the run's serial.                                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the Cancel.                                    */
        /*--------------------------------------------------------------------*/
        internal CST_clsCheckStop(CLI_clsClient objClient, long lWindow, long lRun)
        {
            /* Kept as given: the run is fixed now, and nothing of the    */
            /* window or its button goes into the request posted later.   */
            Cst_objClient = objClient;
            Cst_lWindow = lWindow;
            Cst_lRun = lRun;
        }

        /*--------------------------------------------------------------------*/
        /* CST_RequestCancel:                                                 */
        /*                                                                    */
        /* Post a Cancel for this run.  Whether that run is still the one     */
        /* running is the worker's to decide; nothing is stopped here.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the Cancel posted.                                      */
        /*--------------------------------------------------------------------*/
        internal void CST_RequestCancel()
        {
            /* The Cancel, carrying this run's serial (as its revision): a   */
            /* later Repair has another serial, so this Cancel cannot stop   */
            /* it.                                                           */
            Cst_objClient.CLI_Post(CFC_clsCommands.CFC_Create(KND_enmKind.Cancel,
                Cst_lWindow, null, null, null, Cst_lRun, 0, null, "", ""), null);
        }
    }

    internal partial class MAIN_clsMainForm
    {
        /*--------------------------------------------------------------------*/
        /* Pui_clsPartitionView -- one partition window's life, from the      */
        /* worker's PartitionOpen to the window's end: the window, the disk   */
        /* it was opened for, and the handlers that post its buttons'         */
        /* requests.  It only shows and posts; what may run is the worker's   */
        /* -- documentation table.                                            */
        /*--------------------------------------------------------------------*/
        private class Pui_clsPartitionView
        {
            private MAIN_clsMainForm Pui_frmMain;         // the main window, borrowed: its client, fonts and shared display code
            private long Pui_lWindow;                     // the window's id: the PartitionOpen request's serial (above 0), fixed for its life
            private TGT_clsTarget Pui_objTarget;          // the whole disk or file it was opened for, with the device's description
            private string Pui_sName;                     // the window's short caption
            private bool Pui_bAskKind;                    // ask which table kind to make when it opens (New Image's empty file); the risk warning is not skipped by it
            private DSK_clsLayoutForm Pui_frmLayout;      // the window, kept until it has been shown, closed and disposed
            private bool Pui_bReturned;                   // true once its modal call has returned and it is disposed (or none was ever made)
            private string Pui_sCloseError;               // why a close the worker asked for did not happen (vetoed, or disposal failed); "" otherwise
            private int Pui_iReplies;                     // requests posted whose result has not yet come back; for the window's display only
            private int Pui_iPresenting;                  // how deep inside showing a result it is (its reports can be modal); 0 when not
            private bool Pui_bRunning;                    // whether the worker last said it was busy; display only, never permission to post
            private PRS_clsPresentation Pui_objQuestion;  // a question kept for showing once the window can (a newer display replaces it); null for none

            /*----------------------------------------------------------------*/
            /* Pui_clsPartitionView:                                          */
            /*                                                                */
            /* The record for one partition window the worker has opened; the */
            /* window itself is made later (PUI_Show).                        */
            /*                                                                */
            /* Arguments:                                                     */
            /*     frmMain   : the main window, borrowed.                     */
            /*     lWindow   : the window's id (the PartitionOpen serial).    */
            /*     objTarget : the whole disk or file, kept by reference.     */
            /*     sName     : the short caption.                             */
            /*     bAskKind  : ask the table kind when it opens.              */
            /*                                                                */
            /* Returns:                                                       */
            /*     (constructor) : the record; no window, no engine work.     */
            /*----------------------------------------------------------------*/
            internal Pui_clsPartitionView(MAIN_clsMainForm frmMain, long lWindow,
                TGT_clsTarget objTarget, string sName, bool bAskKind)
            {
                /* Kept as given: every handler of this window uses these,    */
                /* even if the main window later shows another disk.  No      */
                /* table is copied here; the window gets the layout itself.   */
                Pui_frmMain = frmMain;
                Pui_lWindow = lWindow;
                Pui_objTarget = objTarget;
                Pui_sName = sName;
                Pui_bAskKind = bAskKind;
                Pui_sCloseError = "";
            }

            /*----------------------------------------------------------------*/
            /* PUI_FindOwner:                                                 */
            /*                                                                */
            /* The window a question naming this partition window is shown    */
            /* over (the question presenter's lookup, through the main        */
            /* window's Main_FindCommandOwner): this window, if the id is its */
            /* own and it is still live.                                      */
            /*                                                                */
            /* Arguments:                                                     */
            /*     lWindow : the id the question names.                       */
            /*                                                                */
            /* Returns:                                                       */
            /*     Form : this window, or null (another id, or it has gone).  */
            /*----------------------------------------------------------------*/
            internal Form PUI_FindOwner(long lWindow)
            {
                if (lWindow == Pui_lWindow && Pui_frmLayout != null &&
                    !Pui_bReturned && !Pui_frmLayout.IsDisposed)
                {   /* Its own id and still live: this window.  An older   */
                    /* window's id never finds a newer window.             */
                    return(Pui_frmLayout);
                }

                /* Not this window's: null, and the presenter treats the   */
                /* owner as gone.                                          */
                return(null);
            }

            /*----------------------------------------------------------------*/
            /* PUI_ExitOwner:                                                 */
            /*                                                                */
            /* The window an Exit (or an option's warning) is shown over      */
            /* while this window is modal over the main one.  Being shown     */
            /* over it gives the question no say over the window's work.      */
            /*                                                                */
            /* Arguments:                                                     */
            /*     None.                                                      */
            /*                                                                */
            /* Returns:                                                       */
            /*     Form : this window while it is live, or null.              */
            /*----------------------------------------------------------------*/
            internal Form PUI_ExitOwner()
            {
                /* The same test as PUI_FindOwner, by this window's own id. */
                return(PUI_FindOwner(Pui_lWindow));
            }

            /*----------------------------------------------------------------*/
            /* PUI_Show:                                                      */
            /*                                                                */
            /* The partition window made and shown, modal, with the layout    */
            /* the worker already read -- and, once it returns, disposed and  */
            /* its end posted.  Only a disposal that really completed counts  */
            /* as the window's end; a failure to show it is put in the status */
            /* line and starts nothing.                                       */
            /*                                                                */
            /* Arguments:                                                     */
            /*     objLayout : the worker's layout read, borrowed.            */
            /*                                                                */
            /* Returns:                                                       */
            /*     void : after the window's display and clean-up.            */
            /*----------------------------------------------------------------*/
            internal void PUI_Show(ELY_clsLayoutResult objLayout)
            {
                bool bDisposed;

                /* Not disposed yet: only a disposal that returns normally   */
                /* (below) lets the window's end be reported.                */
                bDisposed = false;
                try
                {   /* The window, given the disk's facts and what it posts   */
                    /* through: Read, Test, Close and Open their own; every   */
                    /* other button with work one record each through         */
                    /* Pui_PostEdit; and Pui_PostReady for the return of      */
                    /* its own message boxes.                                 */
                    Pui_frmLayout = new DSK_clsLayoutForm(Pui_frmMain.Main_fntDialog,
                        Pui_sName, Pui_objTarget.TGT_ullDiskBytes, Pui_objTarget.TGT_uiSectorBytes,
                        Pui_objTarget.TGT_sDeviceText.Length != 0, Pui_bAskKind,
                        Pui_PostRead, Pui_PostTest, Pui_PostClose, Pui_PostOpen,
                        Pui_PostEdit, Pui_PostReady);
                    Pui_frmLayout.Icon = Pui_frmMain.Icon;

                    /* Watched, UNARMED: this call reports the end below; the   */
                    /* watch is armed only if that report cannot be made.       */
                    Pui_frmMain.Main_objLifetimes.LFT_Watch(Pui_frmLayout, Pui_OnLayoutLost, false);

                    /* The layout already read painted in (no second read of   */
                    /* the disk), the window's buttons set from the worker's   */
                    /* state, then shown modal over the main window and        */
                    /* waited for; its Shown only posts the table-kind         */
                    /* question when asked for (Pui_PostEdit).                 */
                    Pui_frmLayout.DSK_ShowRead(objLayout.ELY_objTable, objLayout.ELY_sAccount,
                        objLayout.ELY_sWholeContent, objLayout.ELY_ullWholeFsBytes);
                    PUI_ApplyState(Pui_frmMain.Main_objCommandClient.CLI_ReadState());
                    Pui_frmLayout.ShowDialog(Pui_frmMain);
                }
                catch (Exception exDisplay)
                {   /* Making or showing it threw: said in the status line   */
                    /* (the message only); the clean-up in the finally is    */
                    /* still owed.                                           */
                    Pui_frmMain.Main_SetStatus("The partition window could not be displayed: " + exDisplay.Message);
                }
                finally
                {   /* Shown and returned, or thrown: the window disposed,    */
                    /* and only a disposal that completes counts as its end   */
                    /* -- not a close posted, nor a disposal merely tried.    */
                    try
                    {   /* The disposal.  The fonts and the main window are   */
                        /* their owners' and are not disposed with it.        */
                        if (Pui_frmLayout != null)
                        {   /* A window was made: disposed, now that its modal   */
                            /* call has returned.                                */
                            Pui_frmLayout.Dispose();
                        }

                        /* Disposed (or none was ever made): the end can be   */
                        /* reported.                                          */
                        bDisposed = true;
                    }
                    catch (Exception exDispose)
                    {   /* The disposal threw: the window's end is not certain,   */
                        /* so it is not reported; the reason is kept and shown.   */
                        Pui_sCloseError = "The partition window could not finish closing: " + exDispose.Message;
                        Pui_frmMain.Main_SetStatus(Pui_sCloseError);
                    }

                    /* Whether it ended, recorded: this record stays (a         */
                    /* ModeClosed question may still arrive for it after this   */
                    /* returns), but an ended record finds no owner and posts   */
                    /* nothing new.                                             */
                    Pui_bReturned = bDisposed;
                    if (bDisposed)
                    {   /* Ended: PartitionClosed posted -- the worker's notice   */
                        /* that the mode may end, not the answer to a close it    */
                        /* asked for.                                             */
                        Pui_frmMain.Main_objCommandClient.CLI_Post(CFC_clsCommands.CFC_Create(
                            KND_enmKind.PartitionClosed, Pui_lWindow, null, Pui_objTarget,
                            null, 0, 0, null, "", ""), null);

                        if (Pui_frmLayout != null)
                        {   /* A window was made: its end reported here, so      */
                            /* its lifetime watch is settled with nothing left   */
                            /* to do.                                            */
                            Pui_frmMain.Main_objLifetimes.LFT_Settle(Pui_frmLayout);
                        }
                    }
                    else if (Pui_frmLayout != null)
                    {   /* The disposal failed, so the end could not be     */
                        /* certified and the mode is kept.  The watch is    */
                        /* armed: when the window is in fact gone it ends   */
                        /* the mode then.                                   */
                        Pui_frmMain.Main_objLifetimes.LFT_Arm(Pui_frmLayout);
                    }

                    /* A question kept while the window was up (a close's      */
                    /* consent or its ModeClosed receipt, or a display held    */
                    /* back behind the window) is looked at again on a later   */
                    /* UI turn.  The modal call has returned, so a receipt     */
                    /* can now be answered: Closed after a clean disposal,     */
                    /* Cancel with the reason after a failed one.              */
                    Pui_PostReady();
                }
            }

            /*------------------------------------------------------------------*/
            /* Pui_OnLayoutLost:                                                */
            /*                                                                  */
            /* The window watch's end handler (CommandLifetimes.cs): this       */
            /* partition window has been found gone after PUI_Show could not    */
            /* certify its end -- its disposal failed, which is the only case   */
            /* its watch is armed.  The end is certified now instead, so the    */
            /* worker's partition mode is not held for good by a window that    */
            /* no longer exists.                                                */
            /*                                                                  */
            /* Arguments:                                                       */
            /*     frmLost : the window the watch found gone.                   */
            /*                                                                  */
            /* Returns:                                                         */
            /*     void : the partition mode's end posted, or nothing done for  */
            /*            another window or an end already reported.            */
            /*------------------------------------------------------------------*/
            private void Pui_OnLayoutLost(Form frmLost)
            {
                if (frmLost != Pui_frmLayout || Pui_bReturned)
                {   /* Not this view's window, or its end already reported:   */
                    /* the watch's contract is that a handler may be called   */
                    /* for a window its owner has finished with, and then     */
                    /* does nothing.  (A second PartitionClosed would be      */
                    /* harmless -- the worker ends the mode only for the      */
                    /* window that owns it, CommandSequences.cs -- but it     */
                    /* would be a notice for nothing.)                        */
                    return;
                }

                /* The window counts as returned after all, and the close     */
                /* error PUI_Show recorded no longer stands -- set BEFORE     */
                /* the post below, so a ModeClosed receipt answered after     */
                /* it reads Closed and not a failure.  PartitionClosed is     */
                /* the same notice PUI_Show posts for a clean end: it tells   */
                /* the worker the mode may end.                               */
                Pui_bReturned = true;
                Pui_sCloseError = "";
                Pui_frmMain.Main_objCommandClient.CLI_Post(CFC_clsCommands.CFC_Create(
                    KND_enmKind.PartitionClosed, Pui_lWindow, null, Pui_objTarget,
                    null, 0, 0, null, "", ""), null);

                /* A ModeClosed receipt waiting on this window can now be   */
                /* answered (Closed), on a later UI turn.                   */
                Pui_PostReady();
            }

            /*-----------------------------------------------------------------*/
            /* PUI_ApplyState:                                                 */
            /*                                                                 */
            /* The partition window's buttons and state line brought up to the */
            /* worker's latest published state: shown as working while the     */
            /* worker is busy or a request of this window's has not had its    */
            /* result shown yet.  A display courtesy only -- what may run is   */
            /* decided by the worker's own gate, never by these counts.        */
            /*                                                                 */
            /* Arguments:                                                      */
            /*     objState : the worker's state, as last published.           */
            /*                                                                 */
            /* Returns:                                                        */
            /*     void : the window repainted, or nothing done once it has    */
            /*            gone.                                                */
            /*-----------------------------------------------------------------*/
            internal void PUI_ApplyState(STV_clsStateView objState)
            {
                /* The worker's busy flag kept for the window's display.  It   */
                /* is not enough alone: the worker can publish "idle" before   */
                /* the result of this window's last request has been shown     */
                /* here, so the replies still owed keep the window "working"   */
                /* too -- otherwise an edit made in that gap could be thrown   */
                /* away by the older result's repaint (DSK_ShowRead).          */
                Pui_bRunning = objState.STV_bRunning;
                if (Pui_frmLayout != null && !Pui_bReturned && !Pui_frmLayout.IsDisposed)
                {   /* This view's window is still live: it is told whether    */
                    /* anything is pending.  A window that has gone is left    */
                    /* alone -- a late state must not touch a disposed form.   */
                    Pui_frmLayout.DSK_ApplyCommandPending(Pui_bRunning ||
                        Pui_iReplies != 0);
                }
            }

            /*------------------------------------------------------------------*/
            /* Pui_Post:                                                        */
            /*                                                                  */
            /* Every press this window makes goes through here: user work,      */
            /* posted at once (Pui_PostRequest), so a click while the worker    */
            /* is busy or an Exit is pending is refused, not queued.            */
            /*                                                                  */
            /* Arguments:                                                       */
            /*     objRequest : the request, made from what the window captured */
            /*                  at the click; it becomes the worker's.          */
            /*                                                                  */
            /* Returns:                                                         */
            /*     void : posted (its result comes later to Pui_OnResult), or   */
            /*            the failure shown.                                    */
            /*------------------------------------------------------------------*/
            private void Pui_Post(RQT_clsRequest objRequest)
            {
                /* A press: posted now, as user work. */
                Pui_PostRequest(objRequest, true);
            }

            /*------------------------------------------------------------------*/
            /* Pui_PostRequest:                                                 */
            /*                                                                  */
            /* A request posted, with Pui_OnResult as its completion, and       */
            /* counted as a reply owed until that completion has shown its      */
            /* result -- the count that keeps the window "working" meanwhile    */
            /* (PUI_ApplyState).  A post that throws is reported in the status  */
            /* line rather than thrown out of the window's button handler.      */
            /*                                                                  */
            /* Arguments:                                                       */
            /*     objRequest     : the request; it becomes the worker's.       */
            /*     bUserInitiated : true for a press (refused while the worker  */
            /*                      is busy); false for New Image's first       */
            /*                      question, which the window's Shown posts to */
            /*                      continue the New Image that opened it.      */
            /*                                                                  */
            /* Returns:                                                         */
            /*     void : posted (its result, a refusal included, comes later   */
            /*            to Pui_OnResult), or the failure shown.               */
            /*------------------------------------------------------------------*/
            private void Pui_PostRequest(RQT_clsRequest objRequest, bool bUserInitiated)
            {
                /* One more reply owed, until Pui_OnResult takes it back       */
                /* down.  Even a refusal comes back that way: the worker       */
                /* publishes it while CLI_Post runs, and the client delivers   */
                /* it on a later UI turn (BeginInvoke).  The count is for      */
                /* the window's display only, never a gate on what may run.    */
                Pui_iReplies++;

                /* The window shown as working now, before anything the post   */
                /* publishes, so no click can land on an enabled button in     */
                /* between.                                                    */
                PUI_ApplyState(Pui_frmMain.Main_objCommandClient.CLI_ReadState());
                try
                {   /* A press is USER work, which SYS_Post refuses while the   */
                    /* worker is busy or an Exit is pending, rather than        */
                    /* queueing it behind other work; New Image's first         */
                    /* question continues that New Image and queues behind      */
                    /* it (an Exit still refuses it).  The                      */
                    /* client registers the completion before it hands the      */
                    /* request over (CommandClient.cs), so even an at-once      */
                    /* refusal finds it.  CLI_Post's true / false is not        */
                    /* needed: a refusal reaches Pui_OnResult like any other    */
                    /* result.                                                  */
                    Pui_frmMain.Main_objCommandClient.CLI_Post(objRequest, Pui_OnResult, bUserInitiated);
                }
                catch (Exception exPost)
                {   /* The post threw (a request posted twice, or a serial    */
                    /* that would overflow): nothing reached the worker, so   */
                    /* no completion will come.  The registration is taken    */
                    /* back and the reply is no longer owed.                  */
                    Pui_frmMain.Main_objCommandClient.CLI_AbandonReply(objRequest);
                    Pui_iReplies--;

                    /* Reported, not thrown: this is reached from the window's   */
                    /* button handlers, and the program installs no handler      */
                    /* for an exception escaping a WinForms event.  Then the     */
                    /* window is repainted for the lowered count, and any        */
                    /* question it was holding back looked at again.             */
                    Pui_frmMain.Main_SetStatus(exPost.Message);
                    PUI_ApplyState(Pui_frmMain.Main_objCommandClient.CLI_ReadState());
                    Pui_PostReady();
                }
            }

            /*------------------------------------------------------------------*/
            /* Pui_PostRead:                                                    */
            /*                                                                  */
            /* Where the window's Read (and Revert) lands: a fresh read of the  */
            /* disk's layout is asked for.  The window's rows are replaced only */
            /* when that read succeeds (Pui_OnResult); until then the edits     */
            /* stay as they are.                                                */
            /*                                                                  */
            /* Arguments:                                                       */
            /*     None.                                                        */
            /*                                                                  */
            /* Returns:                                                         */
            /*     void : the read is posted.                                   */
            /*------------------------------------------------------------------*/
            private void Pui_PostRead()
            {
                /* The disk the window was opened for, kept in this view --   */
                /* never whatever the main window shows by now -- so a read   */
                /* asked for here is always of this window's disk.            */
                Pui_Post(CFC_clsCommands.CFC_Create(KND_enmKind.ReadLayout, Pui_lWindow,
                    Pui_frmMain.Main_objCommandClient.CLI_ReadState(), Pui_objTarget,
                    null, 0, 0, null, "", ""));
            }

            /*------------------------------------------------------------------*/
            /* Pui_Range:                                                       */
            /*                                                                  */
            /* A target for one range of this window's disk -- a partition, or  */
            /* the whole disk -- for the requests that act on a range.          */
            /*                                                                  */
            /* Arguments:                                                       */
            /*     ullOffset : the range's start, bytes.                        */
            /*     ullLength : its length, bytes (0 means "to the end", as for  */
            /*                 every target).                                   */
            /*                                                                  */
            /* Returns:                                                         */
            /*     TGT_clsTarget : a new target; whether the range lies on the  */
            /*                     disk is checked by the worker and the        */
            /*                     engine, not here.                            */
            /*------------------------------------------------------------------*/
            private TGT_clsTarget Pui_Range(ulong ullOffset, ulong ullLength)
            {
                /* Only the range differs from the window's own target: the    */
                /* path, the device's description and the disk's size and      */
                /* sector size are carried over unchanged, so the worker can   */
                /* tell the request is for the disk its mode owns, and no      */
                /* other description is ever taken from what is on screen.     */
                return(new TGT_clsTarget(Pui_objTarget.TGT_sPath, ullOffset, ullLength,
                    Pui_objTarget.TGT_sDeviceText, Pui_objTarget.TGT_ullDiskBytes,
                    Pui_objTarget.TGT_uiSectorBytes));
            }

            /*------------------------------------------------------------------*/
            /* Pui_PostTest:                                                    */
            /*                                                                  */
            /* Where the window's Test Capacity lands: the disk's real capacity */
            /* is tested.  The worker asks first and shows the progress.        */
            /*                                                                  */
            /* Arguments:                                                       */
            /*     None.                                                        */
            /*                                                                  */
            /* Returns:                                                         */
            /*     void : the test is posted.                                   */
            /*------------------------------------------------------------------*/
            private void Pui_PostTest()
            {
                /* Always the whole disk the window was opened for: a capacity   */
                /* test is of the device, never of one partition.                */
                Pui_Post(CFC_clsCommands.CFC_Create(KND_enmKind.TestCapacity, Pui_lWindow,
                    Pui_frmMain.Main_objCommandClient.CLI_ReadState(), Pui_objTarget,
                    null, 0, 0, null, "", ""));
            }

            /*------------------------------------------------------------------*/
            /* Pui_PostClose:                                                   */
            /*                                                                  */
            /* Where the window's Close button and its title-bar X both land:   */
            /* the worker is asked to end this partition window.  Posting it    */
            /* closes nothing and ends no mode -- the worker asks the discard   */
            /* question if the table has unwritten edits (Pui_PresentPending),  */
            /* then sends the Close (PUI_Close), and the mode ends only once    */
            /* the window has really gone (PUI_Show).                           */
            /*                                                                  */
            /* Arguments:                                                       */
            /*     None.                                                        */
            /*                                                                  */
            /* Returns:                                                         */
            /*     void : the close request is posted.                          */
            /*------------------------------------------------------------------*/
            private void Pui_PostClose()
            {
                /* One request for both ways of closing, for this window's   */
                /* own disk and id -- so a close can never end a later       */
                /* window's mode.                                            */
                Pui_Post(CFC_clsCommands.CFC_Create(KND_enmKind.ClosePartition, Pui_lWindow,
                    Pui_frmMain.Main_objCommandClient.CLI_ReadState(), Pui_objTarget,
                    null, 0, 0, null, "", ""));
            }

            /*------------------------------------------------------------------*/
            /* Pui_PostOpen:                                                    */
            /*                                                                  */
            /* Where the window's Open lands: the chosen partition (or the      */
            /* whole disk) is to be opened in the main window.  The worker      */
            /* closes this window first, as Close does, and only then opens     */
            /* the range (CommandSequences.cs, Sys_PartitionEndSequence).       */
            /*                                                                  */
            /* Arguments:                                                       */
            /*     ullOffset : the range's start, bytes.                        */
            /*     ullLength : its length, bytes; 0 for "to the end of the      */
            /*                 file".                                           */
            /*                                                                  */
            /* Returns:                                                         */
            /*     void : the open request is posted.                           */
            /*------------------------------------------------------------------*/
            private void Pui_PostOpen(ulong ullOffset, ulong ullLength)
            {
                /* The range goes into the request now: the window may be     */
                /* closed (and its selection gone) before the request runs,   */
                /* and nothing is read back from the window afterwards.       */
                Pui_Post(CFC_clsCommands.CFC_Create(KND_enmKind.OpenPartitionEntry, Pui_lWindow,
                    Pui_frmMain.Main_objCommandClient.CLI_ReadState(), Pui_Range(ullOffset, ullLength),
                    null, 0, 0, null, "", ""));
            }

            /*-----------------------------------------------------------------*/
            /* Pui_PostEdit:                                                   */
            /*                                                                 */
            /* Where a button run whole by the worker lands: its record -- the */
            /* button, a copy of the table and the selection -- posted as ONE  */
            /* EditTable request for this window's disk.  The worker asks the  */
            /* button's question and makes its edit on the copy                */
            /* (CommandOperations.cs, Sys_TableEditSequence); the copy comes   */
            /* back to Pui_OnResult to be shown.  Nothing is asked here.       */
            /*                                                                 */
            /* Arguments:                                                      */
            /*     objEdit : the press's record, made for this press; it       */
            /*               becomes the worker's.                             */
            /*                                                                 */
            /* Returns:                                                        */
            /*     void : the request is posted.                               */
            /*-----------------------------------------------------------------*/
            private void Pui_PostEdit(TBE_clsTableEdit objEdit)
            {
                RQT_clsRequest objRequest;

                /* For the whole disk the window was opened for, with the    */
                /* shared image folder every image dialog starts in, for a   */
                /* partition Read's or Write's file question.                */
                objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.EditTable, Pui_lWindow,
                    Pui_frmMain.Main_objCommandClient.CLI_ReadState(), Pui_objTarget,
                    null, 0, 0, objEdit, Pui_frmMain.Main_ImageFolder(""), "");

                if (objEdit.TBE_enmAction == PAC_enmPartitionAction.AskKind)
                {   /* New Image's first question is posted by the window's       */
                    /* Shown, not by a press: it continues the New Image that     */
                    /* opened the window, which may still be finishing on the     */
                    /* worker, so it queues behind it rather than being refused   */
                    /* as a press made while the worker is busy.                  */
                    Pui_PostRequest(objRequest, false);
                    return;
                }

                /* A press, as user work: a second press while the worker is   */
                /* busy is refused, and the same press waiting already is      */
                /* dropped as the same request.                                */
                Pui_Post(objRequest);
            }

            /*------------------------------------------------------------------*/
            /* Pui_OnResult:                                                    */
            /*                                                                  */
            /* The completion of every request this window posted               */
            /* (Pui_PostRequest -- the presses and New Image's first question): */
            /* a layout that was read successfully is painted into the window,  */
            /* a failed table write is shown there, the result is reported, and */
            /* the reply is no longer owed.  It shows what the worker already   */
            /* did and never posts more work.                                   */
            /*                                                                  */
            /* Arguments:                                                       */
            /*     objRequest : the request as it was posted.                   */
            /*     objResult  : its result -- a success, a failure, or a        */
            /*                  refusal or drop by the worker, all alike.       */
            /*                                                                  */
            /* Returns:                                                         */
            /*     void : the result shown and its reply settled.               */
            /*------------------------------------------------------------------*/
            private void Pui_OnResult(RQT_clsRequest objRequest, RSL_clsResult objResult)
            {
                RPO_clsPartitionOutcome objOutcome;
                RSL_clsResult objRead;
                ELY_clsLayoutResult objLayout;

                /* Showing the result can take a modal message box (the         */
                /* report below), inside which another result or question can   */
                /* arrive.  Counted, so a close's or an Exit's question waits   */
                /* until this report has returned (PUI_Question,                */
                /* Pui_PresentPending) rather than appearing on top of it.      */
                Pui_iPresenting++;
                try
                {   /* The layout to paint, if the result carries one.  A      */
                    /* change to the disk (write, format, expand, partition    */
                    /* write or erase) comes back as an outcome holding the    */
                    /* change's result and the layout read again after it,     */
                    /* kept apart (CommandOperations.cs): a failed re-read     */
                    /* does not make the change itself a failure.  Any other   */
                    /* result is its own read.                                 */
                    objOutcome = objResult.RSL_objPayload as RPO_clsPartitionOutcome;
                    objRead = objResult;
                    if (objOutcome != null)
                    {   /* A change: the re-read is what may hold a layout. */
                        objRead = objOutcome.RPO_objLayout;
                    }

                    /* Only a SUCCESSFUL read's layout is taken: painting      */
                    /* replaces the window's table and throws away its         */
                    /* unwritten edits (DSK_ShowRead), so a failed read must   */
                    /* leave them.  A success with no layout (a capacity       */
                    /* test, an input) leaves the rows alone too.              */
                    objLayout = null;
                    if (objRead != null && objRead.RSL_iStatus == SYS_clsSystem.SYS_OK)
                    {   /* A success: its layout, if it is a layout read. */
                        objLayout = objRead.RSL_objPayload as ELY_clsLayoutResult;
                    }

                    if (!Pui_bReturned && Pui_frmLayout != null && !Pui_frmLayout.IsDisposed)
                    {   /* The window is still live.  A result arriving after it   */
                        /* has gone (a close overtook it) touches nothing.         */
                        if (objLayout != null)
                        {   /* A fresh layout: painted, through the same call   */
                            /* the window's first display used.                 */
                            Pui_frmLayout.DSK_ShowRead(objLayout.ELY_objTable, objLayout.ELY_sAccount,
                                objLayout.ELY_sWholeContent, objLayout.ELY_ullWholeFsBytes);

                            if (objRequest.RQT_enmKind == KND_enmKind.ReadLayout)
                            {   /* A Refresh (or Revert): the main status      */
                                /* line says the table was read, replacing     */
                                /* an earlier line (a cancelled Refresh's, a   */
                                /* refusal's) that a success would otherwise   */
                                /* leave standing.  A write's, an expand's     */
                                /* or a format's re-read keeps the line its    */
                                /* own result sets.                            */
                                Pui_frmMain.Main_SetStatus("Partition table read.");
                            }
                        }
                        else if (objResult.RSL_iStatus == SYS_clsSystem.SYS_OK &&
                            objResult.RSL_objPayload is TBE_clsTableEdit)
                        {   /* A button the worker ran on a copy of the table    */
                            /* (EditTable): the edited copy shown as the         */
                            /* window's table, unwritten.  The buttons were      */
                            /* greyed from the press to here, so the table the   */
                            /* copy was made from is still the one shown.        */
                            Pui_frmLayout.DSK_ShowEdited((TBE_clsTableEdit)objResult.RSL_objPayload);
                        }
                        else if (objRequest.RQT_enmKind == KND_enmKind.WriteTable &&
                            objResult.RSL_iStatus != SYS_clsSystem.SYS_OK && objResult.RSL_objPayload is ESR_clsResult)
                        {   /* A table write that the engine really attempted   */
                            /* and that failed (its result carries the          */
                            /* engine's own record; a refusal or a No to the    */
                            /* question does not).  The worker does not read    */
                            /* the layout back after it, so the edits stay      */
                            /* for another try, and the window says the disk    */
                            /* may hold part of the new table -- the batch      */
                            /* has no rollback.                                 */
                            Pui_frmLayout.DSK_ShowWriteFailure(objResult.RSL_sDetail);
                        }
                    }

                    /* The result reported (its warnings and verdicts, in     */
                    /* the status line or a message box over the window) --   */
                    /* for every result, whether the window is live or not.   */
                    Pui_frmMain.Main_ReportPartitionResult(objRequest, objResult,
                        Pui_frmLayout, Pui_sName);
                }
                finally
                {   /* Settled exactly once per request, whatever happened    */
                    /* above -- a refusal and a drop included, and a report   */
                    /* that threw: the report is no longer showing, and the   */
                    /* reply is no longer owed.                               */
                    Pui_iPresenting--;
                    Pui_iReplies--;

                    /* The window repainted for the lowered count, then any    */
                    /* question held back during the report looked at again.   */
                    PUI_ApplyState(Pui_frmMain.Main_objCommandClient.CLI_ReadState());
                    Pui_PostReady();
                }
            }

            /*------------------------------------------------------------------*/
            /* PUI_Close:                                                       */
            /*                                                                  */
            /* The worker's Close for a partition window, after its discard     */
            /* question (if any) was answered Yes: the window is closed with no */
            /* second question.  Closing it does not end the mode -- that waits */
            /* until the window's modal call has returned and it is disposed    */
            /* (PUI_Show) -- and a close that is refused or throws is recorded, */
            /* so the worker's ModeClosed receipt is answered with the reason   */
            /* (Pui_PresentPending) instead of waiting for good.                */
            /*                                                                  */
            /* Arguments:                                                       */
            /*     lWindow : the window the worker's Close names.               */
            /*                                                                  */
            /* Returns:                                                         */
            /*     bool : true when it named this window (closed, or the        */
            /*            failure recorded); false for another window's id.     */
            /*------------------------------------------------------------------*/
            internal bool PUI_Close(long lWindow)
            {
                if (lWindow != Pui_lWindow)
                {   /* Another window's Close (a later partition window's):   */
                    /* not this one's to act on.                              */
                    return(false);
                }

                /* A fresh attempt: no failure recorded yet.  A window that   */
                /* has already returned is not closed again -- nothing is     */
                /* done to it, and its receipt is simply answered Closed      */
                /* below.                                                     */
                Pui_sCloseError = "";
                if (!Pui_bReturned && Pui_frmLayout != null)
                {   /* The window is still up: closed on the worker's word,   */
                    /* which its own FormClosing lets through (no second      */
                    /* discard question; DSK_CloseFromCommand).               */
                    try
                    {   /* The window answers whether its close went ahead --   */
                        /* its one FormClosing handler records it -- and no     */
                        /* event at all (the window never began to close)       */
                        /* counts as a refusal too.                             */
                        if (!Pui_frmLayout.DSK_CloseFromCommand())
                        {   /* Not closing: the reason recorded, so the           */
                            /* receipt is answered Cancel and the mode is kept.   */
                            Pui_sCloseError = "The partition window declined to close.";
                        }
                    }
                    catch (Exception exClose)
                    {   /* The Close threw: recorded the same way -- a Closed   */
                        /* receipt is never given for a window not known to     */
                        /* have gone.                                           */
                        Pui_sCloseError = exClose.Message;
                    }
                }

                /* The receipt looked at on a later UI turn.  If the close      */
                /* went ahead it still waits there for PUI_Show's disposal to   */
                /* finish; a failure is answered at once.                       */
                Pui_PostReady();
                return(true);
            }

            /*------------------------------------------------------------------*/
            /* PUI_Question:                                                    */
            /*                                                                  */
            /* A question from the worker, offered to this view before it is    */
            /* shown.  This view takes it when it is its own (the close's       */
            /* discard question, or the ModeClosed receipt), or when the window */
            /* is busy with a dialog or report of its own that the question     */
            /* must not appear on top of; it is then kept and shown, or         */
            /* answered, on a later UI turn (Pui_PresentPending).  Anything     */
            /* else is left to the ordinary presenter.                          */
            /*                                                                  */
            /* Arguments:                                                       */
            /*     objDisplay : the question's display, as published.           */
            /*                                                                  */
            /* Returns:                                                         */
            /*     bool : true when this view took it (or it is stale and is    */
            /*            dropped); false for the caller to present.            */
            /*------------------------------------------------------------------*/
            internal bool PUI_Question(PRS_clsPresentation objDisplay)
            {
                QST_clsQuestion objQuestion;
                bool bMode;
                bool bLocal;

                /* Whether this display may still answer is the question's   */
                /* own test (QST_IsCurrent), not the kind it was saved as.   */
                objQuestion = objDisplay.PRS_objQuestion;
                if (!objQuestion.QST_IsCurrent(objDisplay))
                {   /* A display already replaced, answered or taken down      */
                    /* (it arrived late): taken and dropped, so nothing is     */
                    /* shown for it -- and in particular a late Exit display   */
                    /* closes no popup a newer display has put back.           */
                    return(true);
                }

                if (objQuestion.QST_enmKind == QNK_enmQuestionKind.ExitRunning)
                {   /* The Exit question while a command's question waits:        */
                    /* that question's popups are closed first (the question      */
                    /* itself stays suspended, shown again if Exit is             */
                    /* declined).  A message box of the window's own -- its       */
                    /* discard question, a failed write's report -- is not        */
                    /* closed; the Exit waits for it below.  (The New Table and   */
                    /* Add Partition dialogs are the presenter's questions, and   */
                    /* attached, so the Supersede closes them too.)               */
                    QWN_clsQuestionWindows.QWN_Supersede(objQuestion.QST_objSuspended);
                }

                /* This window's own close questions; and whether the window    */
                /* has a dialog of its own open now (DSK_IsReadyForLifecycle:   */
                /* its own message boxes are what is counted,                   */
                /* DiskLayoutForm.cs).  Being "ready" locally                   */
                /* gives no right to answer -- only the question's own          */
                /* currency does that.                                          */
                bMode = objQuestion.QST_lWindow == Pui_lWindow &&
                    (objQuestion.QST_enmKind == QNK_enmQuestionKind.ModeClose ||
                    objQuestion.QST_enmKind == QNK_enmQuestionKind.ModeClosed);
                bLocal = !Pui_bReturned && Pui_frmLayout != null &&
                    !Pui_frmLayout.DSK_IsReadyForLifecycle();
                if (!bMode && !bLocal && Pui_iPresenting == 0)
                {   /* Not this window's question, and nothing of the window's   */
                    /* in the way: left to the ordinary presenter at once.       */
                    /* Work merely running is no reason to hold it -- an Exit    */
                    /* question must be able to appear while the worker runs.    */
                    return(false);
                }

                /* Kept, replacing any question kept before: the worker asks   */
                /* one question at a time, so a newer display supersedes the   */
                /* older, whose popups can no longer answer.  Looked at on a   */
                /* later UI turn, once whatever is in the way has returned.    */
                Pui_objQuestion = objDisplay;
                Pui_PostReady();
                return(true);
            }

            /*------------------------------------------------------------------*/
            /* Pui_PostReady:                                                   */
            /*                                                                  */
            /* Ask for the kept question (if any) to be looked at again, on a   */
            /* later turn of the UI loop.  Every place where something that     */
            /* held it back has finished calls this: a result shown, a dialog   */
            /* returned, the window closed or disposed.  Posting rather than    */
            /* calling means the question is never shown from inside the code   */
            /* that is still unwinding a dialog or a disposal.                  */
            /*                                                                  */
            /* Arguments:                                                       */
            /*     None.                                                        */
            /*                                                                  */
            /* Returns:                                                         */
            /*     void : posted, or -- when the main window can no longer be   */
            /*            posted to -- the kept question answered Cancel.       */
            /*------------------------------------------------------------------*/
            private void Pui_PostReady()
            {
                try
                {   /* Posted to the main window's thread (BeginInvoke): it     */
                    /* returns at once, waiting on nothing.  Several posts      */
                    /* are harmless -- Pui_PresentPending finds nothing to do   */
                    /* once the question is gone.                               */
                    Pui_frmMain.BeginInvoke(new MethodInvoker(Pui_PresentPending));
                }
                catch (InvalidOperationException)
                {   /* The main window cannot be posted to (its handle is     */
                    /* gone): the question could never be shown, so it is     */
                    /* answered here instead of leaving the worker waiting.   */
                    if (Pui_objQuestion != null)
                    {   /* Answered Cancel with the reason, and let go.  If     */
                        /* the display is no longer current, QST_Reply simply   */
                        /* refuses the answer.                                  */
                        Pui_objQuestion.PRS_objQuestion.QST_Reply(Pui_objQuestion,
                            new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null,
                            "The partition dispatcher is unavailable."));
                        Pui_objQuestion = null;
                    }
                }
            }

            /*------------------------------------------------------------------*/
            /* Pui_PresentPending:                                              */
            /*                                                                  */
            /* The kept question looked at again (posted by Pui_PostReady): if  */
            /* nothing of the window's is still in the way it is shown -- the   */
            /* close's discard question here, any other through the ordinary    */
            /* presenter -- or, for the ModeClosed receipt, answered once the   */
            /* window's end is known.  If something is still in the way it      */
            /* stays kept; whatever is in the way posts again when it returns,  */
            /* so nothing here polls.                                           */
            /*                                                                  */
            /* Arguments:                                                       */
            /*     None.                                                        */
            /*                                                                  */
            /* Returns:                                                         */
            /*     void : the question shown or answered, or still kept.        */
            /*------------------------------------------------------------------*/
            private void Pui_PresentPending()
            {
                PRS_clsPresentation objDisplay;
                QST_clsQuestion objQuestion;
                QSC_clsWindowScope objScope;
                ANK_enmAnswerKind enmAnswer;

                if (Pui_objQuestion == null)
                {   /* Nothing kept (an earlier turn already dealt with it,   */
                    /* or several posts came in): nothing to do.              */
                    return;
                }

                /* The kept display, still current?  A post can be queued   */
                /* behind an Exit that has since replaced or taken the      */
                /* question down.                                           */
                objDisplay = Pui_objQuestion;
                objQuestion = objDisplay.PRS_objQuestion;
                if (!objQuestion.QST_IsCurrent(objDisplay))
                {   /* No longer current: let go unanswered -- a newer     */
                    /* display (if any) is the only one that may answer.   */
                    Pui_objQuestion = null;
                    return;
                }

                if (objQuestion.QST_enmKind == QNK_enmQuestionKind.ModeClosed)
                {   /* The receipt the worker waits on after its Close: no     */
                    /* popup, only an answer once the window's end is known.   */
                    if (!Pui_bReturned && Pui_sCloseError.Length == 0)
                    {   /* Not known yet: the close went ahead but PUI_Show's   */
                        /* modal call has not returned and disposed it.  It     */
                        /* posts again when it has.                             */
                        return;
                    }

                    /* Known: let go first, then answered.  Closed only when   */
                    /* the window has really returned and been disposed;       */
                    /* otherwise (refused, threw, or its disposal failed)      */
                    /* Cancel, with the reason.                                */
                    Pui_objQuestion = null;
                    enmAnswer = ANK_enmAnswerKind.Cancel;
                    if (Pui_bReturned)
                    {   /* Returned and disposed (PUI_Show, or the watch's   */
                        /* Pui_OnLayoutLost): the window has gone.           */
                        enmAnswer = ANK_enmAnswerKind.Closed;
                    }

                    /* A Cancel keeps the worker's partition mode and stops     */
                    /* the command that asked for the close, with this          */
                    /* reason (CommandSequences.cs); Closed ends the mode and   */
                    /* lets that command go on.                                 */
                    objQuestion.QST_Reply(objDisplay, new ANS_clsAnswer(enmAnswer, null, Pui_sCloseError));
                    return;
                }

                if (Pui_iPresenting != 0 || (!Pui_bReturned && Pui_frmLayout != null &&
                    !Pui_frmLayout.DSK_IsReadyForLifecycle()))
                {   /* A result's report, or a dialog of the window's own, is   */
                    /* still open: kept.  Its return posts again                */
                    /* (Pui_OnResult's finally; the window's local-dialog       */
                    /* notice, which is Pui_PostReady).                         */
                    return;
                }

                /* Let go BEFORE anything is shown: the popup below runs its   */
                /* own modal loop, inside which a newer display (an Exit, or   */
                /* a question put back) can be kept by PUI_Question, and it    */
                /* must not be overwritten by this one afterwards.  Requests   */
                /* this window posted and that are still queued are NOT        */
                /* waited for: an urgent Exit may have gone ahead of them,     */
                /* and they cannot run while this question is being asked.     */
                Pui_objQuestion = null;
                if (objQuestion.QST_enmKind != QNK_enmQuestionKind.ModeClose)
                {   /* Not the close's question (an Exit, an editor's, a copy   */
                    /* conflict ...): shown by the ordinary presenter, now      */
                    /* that the window is out of the way.                       */
                    Pui_frmMain.Main_objCommandQuestions.QPR_Present(objDisplay);
                    return;
                }

                /* The close's discard question.  The answer only gives or     */
                /* withholds consent -- nothing is closed and nothing thrown   */
                /* away here; the worker sends the Close (PUI_Close) only      */
                /* once QST_Reply has accepted a Yes.  A window that has       */
                /* already gone has no edits to lose: Yes without asking.      */
                enmAnswer = ANK_enmAnswerKind.Yes;
                if (!Pui_bReturned)
                {   /* The window is up and may hold unwritten edits: its      */
                    /* own question asks (only when the table was changed).    */
                    /* The scope ties that popup to this display, so an Exit   */
                    /* can take it down (QWN_Supersede).                       */
                    objScope = QWN_clsQuestionWindows.QWN_Begin(objDisplay);
                    try
                    {   /* Asked over the partition window. */
                        if (!Pui_frmLayout.DSK_ConfirmClose())
                        {   /* No, or the popup closed: the window and its   */
                            /* edits are kept.                               */
                            enmAnswer = ANK_enmAnswerKind.No;
                        }
                    }
                    catch (Exception exQuestion)
                    {   /* Showing the question threw.  This runs from a       */
                        /* BeginInvoke, outside the client's dispatch that     */
                        /* would contain a failure, so it is caught here and   */
                        /* answered Cancel -- an exception would escape the    */
                        /* UI loop and leave the worker waiting.               */
                        enmAnswer = ANK_enmAnswerKind.Cancel;
                        Pui_frmMain.Main_SetStatus(exQuestion.Message);
                    }
                    finally
                    {   /* The popup has returned: its scope ended (only    */
                        /* this one's -- a newer display's is untouched).   */
                        QWN_clsQuestionWindows.QWN_End(objScope);
                    }
                }

                /* The answer.  If an Exit took this popup down meanwhile,      */
                /* the display is no longer current and QST_Reply refuses it,   */
                /* so a superseded popup can never authorise the Close.         */
                objQuestion.QST_Reply(objDisplay, new ANS_clsAnswer(enmAnswer, null));
            }
        }

        /* The command system's parts on the UI side, made together at      */
        /* start-up (Main_StartCommandSystem, Main_BindCommandEditors)      */
        /* before the worker starts, so every view it publishes finds its   */
        /* renderer -- documentation table.                                 */
        private CLI_clsClient Main_objCommandClient;                               // the client: the one way to post to the worker and read its state; made once at start-up, null before
        private bool Main_bCommandStarted;                                         // true once SYS_Start has returned; until then the window may close without shutting a worker down
        private bool Main_bCommandStopped;                                         // true once the worker has published Stopped (its work ended, its resources closed); the window may then be torn down
        private EUI_clsEditorCoordinator Main_objCommandEditors;                   // the editor coordinator: the editor windows open and the save replies they wait on (CommandEditorCoordinator.cs)
        private IWR_clsInputWindows Main_objCommandInputs;                         // the input registry: the input windows (value dialogs, the image browser) open until each has returned (CommandInputs.cs)
        private LFT_clsLifetimes Main_objLifetimes;                                // UI-thread watch over every window the worker waits on (inputs, partition, check, editors); made with the client
        private Dictionary<RQT_clsRequest, TIN_clsTargetIntent> Main_dictTargets;  // an editor's Save As target request, by its picker's request, while that picker is open; UI side only (CommandInputs.cs)
        private string Main_sWorkRoot;                                             // the run's private work folder (Main_NewTempFolder): editor snapshots and baselines; removed only after the worker has stopped
        private PGW_clsProgressWindows Main_objCommandProgress;                    // the progress windows, one per running request (CommandProgressWindows.cs)
        private QPR_clsQuestionPresenter Main_objCommandQuestions;                 // the question presenter: shows the worker's questions and gives back their answers (CommandQuestionPresenter.cs)
        private QPR_FindOwner Main_pfnOtherOwner;                                  // a further owner lookup for questions, consulted last by Main_FindCommandOwner; null from the start-up call
        private Pui_clsPartitionView Main_objPartitionView;                        // the latest partition window's view, kept after its window has gone so a late ModeClosed receipt still finds it

        /* The image pane's listing: which navigation's result may still    */
        /* repaint it, and which open the rows and the state shown belong   */
        /* to -- documentation table.                                       */
        private const uint MAIN_NAV_BACK = 1;             // a ListFolder request's option bit: this listing is a Back (so it adds nothing to the Back history); set in Main_NavigateImage
        private RQT_clsRequest Main_objLatestNavigation;  // the newest navigation posted; an older one's result does not touch the path box (Main_OnNavigationResult); null once settled
        private long Main_lFolderEpoch;                   // the epoch (open) of the folder now listed in the image pane; 0 when none
        private long Main_lStateEpoch;                    // the epoch of the open last shown in the main window from a published state
        private uint Main_uiOpenVolumeType;               // the ENG_VOLTYPE_* of the open whose facts were last shown (Main_RenderCommandLifecycle); 0 before any; read only while the state says Open (Main_OpenIsFat)

        /* Keyboard focus and window order around a running command, owned by UI. */
        private bool Main_bBusyFocusImage; // the image pane's list held the focus when a command greyed it; cleared when it is given back
        private bool Main_bBusyFocusHost;  // the Windows pane's list held the focus when a command greyed it; cleared when it is given back
        private Form Main_frmFrontViewer;  // a viewer shown while a modal dialog was up, to come to the front once that dialog has gone; null for none

        /* The Check Filesystem window, by the ids the worker gave it -- not   */
        /* by what the pane shows now -- documentation table.                  */
        private long Main_lCommandCheckWindow;             // the check window's id (the serial of the Check request that opened it), fixed for its life, a Repair's too; 0 when there is none
        private long Main_lCommandCheckRun;                // the serial of the run (check or repair) it now shows; 0 once that run has finished
        private TGT_clsTarget Main_objCommandCheckTarget;  // the range the check window was opened for
        private long Main_lEndedCheckWindow;               // the id of the last check window that ended, kept so a ModeClosed receipt arriving later can still be answered
        private PRS_clsPresentation Main_objCheckReceipt;  // the ModeClosed receipt waiting on the check window's end; null when none waits
        private string Main_sCheckCloseError;              // why the worker's Close of the check window did not happen; "" when none, or after a new attempt

        /* The clipboard and the drags, all on the UI side -- documentation   */
        /* table.                                                             */
        private const string MAIN_COMMAND_SOURCE_FORMAT = "BifrostFM.SourceRange.v2";  // the clipboard format of the key copied with image items (CLP_SourceRange: this run, the image, its range and open), checked before a paste
        private CLP_clsClipboard Main_objCommandClipboard;                             // the clipboard's reader and writer for image items (CommandClipboard.cs)
        private DGI_clsDragInput Main_objDragInput;                                    // the drag out of the image pane now under way; null once Windows' drag loop has returned
        private long Main_lHostDrag;                                                   // the worker's hold for a drag out of the Windows pane (SYS_ReserveDrag), held only while DoDragDrop runs; 0 otherwise

        /* The command system's start, the dispatch of what the worker   */
        /* publishes, and the shutdown.                                  */

        /*------------------------------------------------------------------*/
        /* Main_StartCommandSystem:                                         */
        /*                                                                  */
        /* The command system made and its worker started, once the main    */
        /* window is shown (Main_OnShown): the private work folder, the     */
        /* worker's step table, the client, and every UI-side part, all     */
        /* in place before the worker can publish a view.  From here on the */
        /* window can only be closed through the worker's Exit.             */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     None.                                                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     bool : true once the worker is running; false when the work  */
        /*            folder could not be made (the user has been told, and */
        /*            no worker exists).  An exception from the start is    */
        /*            the caller's (Main_OnShown asks for an Exit).         */
        /*------------------------------------------------------------------*/
        private bool Main_StartCommandSystem()
        {
            NST_clsNativeSteps objSteps;
            CLI_clsClient objClient;
            string sRoot;

            /* The run's private work folder first: the editors' snapshots    */
            /* and baselines go into it, and it is removed only after the     */
            /* worker has really stopped -- not when an Exit is agreed, nor   */
            /* when a close of the window is asked for.                       */
            sRoot = Main_NewTempFolder();
            if (sRoot == null)
            {   /* No folder: Main_NewTempFolder has already told the user,   */
                /* and nothing has been started that would need stopping.     */
                return(false);
            }

            /* The engine steps the worker runs.  They are plain calls the    */
            /* worker makes on its own thread, not another thread; the step   */
            /* table holds the two delegates for the system's whole life.     */
            /* Then the client (and inside it the command system, not yet     */
            /* started), with this window to post views to, the main          */
            /* renderer, and the risk-warning option's value now; then the    */
            /* editors', inputs', progress and question parts.  No other      */
            /* owner lookup is given (null).                                  */
            objSteps = new NST_clsNativeSteps(sRoot);
            objClient = new CLI_clsClient(this,
                new STB_clsStepTable(objSteps.NST_Run, objSteps.NST_Release),
                Main_RenderCommandView, Main_RiskWarningsOn());
            Main_BindCommandEditors(objClient, sRoot, null);

            /* The worker started.  The window is shown, so its handle       */
            /* exists for the views to be posted to, and every renderer is   */
            /* in place before the first one is published.                   */
            objClient.CLI_ReadSystem().SYS_Start();
            Main_bCommandStarted = true;

            /* Running: from now on a close of the window goes through the     */
            /* worker's Exit and waits for its Stopped (Main_OnFormClosing).   */
            return(true);
        }

        /*------------------------------------------------------------------*/
        /* Main_RequestCommandExit:                                         */
        /*                                                                  */
        /* The program's way out: an Exit is posted to the worker, which    */
        /* asks when there is work to lose, closes everything in order (the */
        /* queued work, the inputs, every editor, the session, the check or */
        /* partition window -- CommandSequences.cs, Sys_ExitSequence), and  */
        /* once all has closed publishes Stopped; only then does the window */
        /* close (Main_RenderCommandView).  Nothing is closed here.  With   */
        /* no worker running the window is simply closed, on a later UI     */
        /* turn.                                                            */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     None.                                                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the Exit posted, or the window's close posted.        */
        /*------------------------------------------------------------------*/
        private void Main_RequestCommandExit()
        {
            RQT_clsRequest objRequest;

            if (!Main_bCommandStarted || Main_bCommandStopped)
            {   /* No worker running (never started, or already stopped):   */
                /* nothing to ask and nothing to shut down, so the window   */
                /* is closed -- on a later UI turn, where FormClosing       */
                /* finds the same facts and lets the close through.         */
                BeginInvoke(new MethodInvoker(Main_CloseAfterWorker));
                return;
            }

            /* The Exit, for the main window (origin 0), with its failures     */
            /* reported in the status line.  Clicking twice is safe without    */
            /* any flag here: the worker drops a second Exit while the first   */
            /* is pending (SYS_Post).                                          */
            objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.Exit, 0,
                Main_objCommandClient.CLI_ReadState(), null, null, 0, 0, null, "", "");
            Main_objCommandClient.CLI_Post(objRequest, Main_ReportCommandFailure);

            /* The menus and toolbars greyed at once: the pending Exit is      */
            /* read straight from the command system (SYS_CanAcceptUserWork,   */
            /* through Main_ApplyCommandAvailability), before any published    */
            /* state could say so.                                             */
            Main_UpdateToolbar();
        }

        /*------------------------------------------------------------------*/
        /* Main_CloseAfterWorker:                                           */
        /*                                                                  */
        /* The main window's own close, run as a posted UI turn once there  */
        /* is no worker to wait for (it stopped, or never started).  Its    */
        /* FormClosing then lets the close through and does the final       */
        /* clean-up.                                                        */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     None.                                                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the close begun, or nothing for a window already      */
        /*            disposed.                                             */
        /*------------------------------------------------------------------*/
        private void Main_CloseAfterWorker()
        {
            if (IsDisposed)
            {   /* Already gone (a second posted close, or one that came   */
                /* after the window ended): nothing left to close.         */
                return;
            }

            /* Closed from its own UI turn, never from inside another     */
            /* view's handler that is still running.  The worker is not   */
            /* waited for here -- it has already stopped, or never ran.   */
            Close();
        }

        /*------------------------------------------------------------------*/
        /* Main_RenderCommandView:                                          */
        /*                                                                  */
        /* The main renderer: every view the worker publishes that the      */
        /* client does not hand to a request's own completion arrives here, */
        /* on the UI thread (CommandClient.cs, Cli_Dispatch).  It is        */
        /* offered to the three specialised renderers in turn; what none of */
        /* them takes -- the worker's Stopped, a result with no completion, */
        /* a warning -- is dealt with here.  It only shows; no view starts  */
        /* engine work.                                                     */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objView : the view, as published.                            */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the view shown, or ignored when nothing is owed it.   */
        /*------------------------------------------------------------------*/
        private void Main_RenderCommandView(VWM_clsView objView)
        {
            RSL_clsResult objResult;

            /* First the editor-side renderer (editors, inputs, questions,    */
            /* progress, the check and partition windows -- and the state,    */
            /* which it shows and then passes on); each renderer returns      */
            /* true when it took the view, and the first that does ends it.   */
            if (Main_RenderCommandEditorView(objView))
            {   /* Taken by the editor-side renderer. */
                return;
            }

            if (Main_RenderCommandLifecycle(objView))
            {   /* Taken by the open / close renderer (the session's   */
                /* facts after an open, a reopen or a close).          */
                return;
            }

            if (Main_RenderCommandNavigation(objView))
            {   /* Taken by the navigation renderer (a folder's listing   */
                /* for the image pane).                                   */
                return;
            }

            /* What is left.  A result reaches here only when its request    */
            /* registered no completion -- the client gives the others to    */
            /* their completions first -- so it has nobody else to report    */
            /* it.  The value is a result for those kinds; null otherwise.   */
            objResult = objView.VWM_objValue as RSL_clsResult;
            switch (objView.VWM_enmKind)
            {   /* One kind of left-over view per case. */
                case VWK_enmViewKind.Stopped:
                    /* The worker has ended: its work done, the waiting         */
                    /* requests refused, its resources closed (CommandSystem.   */
                    /* cs).  Recorded, so the window's FormClosing lets the     */
                    /* close through, and the close posted for a UI turn of     */
                    /* its own.                                                 */
                    Main_bCommandStopped = true;
                    BeginInvoke(new MethodInvoker(Main_CloseAfterWorker));
                    break;
                case VWK_enmViewKind.Result:
                case VWK_enmViewKind.Refused:
                case VWK_enmViewKind.Dropped:
                case VWK_enmViewKind.EditorSaved:
                    if (objResult != null)
                    {   /* A request's end with no completion of its own:     */
                        /* reported the way the main window reports a         */
                        /* command's failure, so even this one is not lost.   */
                        Main_ReportCommandFailure(objView.VWM_objRequest, objResult);
                    }
                    break;
                case VWK_enmViewKind.Warning:
                    if (objResult != null)
                    {   /* A warning (trouble in a clean-up, say): shown in    */
                        /* the status line.  It reports, and undoes nothing.   */
                        Main_SetStatus(objResult.RSL_sDetail);
                    }
                    break;
                default:
                    /* Any other view no renderer took (a late one, for a       */
                    /* window that has gone): nothing is owed it, and nothing   */
                    /* is started for it.                                       */
                    break;
            }
        }

        /* The command system's UI-side parts and the routes between them. */

        /*--------------------------------------------------------------------*/
        /* Main_BindCommandEditors:                                           */
        /*                                                                    */
        /* Every UI-side part of the command system made and wired to the     */
        /* client, once, before the worker starts: the clipboard helper,      */
        /* the window watch, the input registry, the editor coordinator       */
        /* with its routes, the progress windows and the question             */
        /* presenter.  The worker can publish a view the moment it starts,    */
        /* and each must already find what shows it.                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objClient     : the client, made but not started.              */
        /*     sWorkRoot     : the run's private work folder.                 */
        /*     pfnOtherOwner : a further owner lookup for questions, or       */
        /*                     null (the start-up call passes null).          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : all bound; nothing started.  InvalidOperationException  */
        /*            for no client, or a second binding.                     */
        /*--------------------------------------------------------------------*/
        private void Main_BindCommandEditors(CLI_clsClient objClient, string sWorkRoot,
            QPR_FindOwner pfnOtherOwner)
        {
            if (objClient == null || Main_objCommandClient != null)
            {   /* No client, or one already bound: refused.  There is one    */
                /* client for the program's life, bound once at start-up --   */
                /* a second would leave parts wired to the first.             */
                throw new InvalidOperationException("Editor commands need one command client installed once.");
            }

            /* The client kept, and the clipboard helper made -- neither   */
            /* depends on any other part.                                  */
            Main_objCommandClient = objClient;
            Main_objCommandClipboard = new CLP_clsClipboard();

            /* The window watch first, on this UI thread (its timer ticks      */
            /* here): the input registry and the editors watch through it.     */
            /* The input registry installs its own two client hooks as it is   */
            /* made (the clean-up after an input's renderer throws, and the    */
            /* settlement after one returns -- CommandInputs.cs).  Then the    */
            /* Save As targets' table, the work folder, the extra owner        */
            /* lookup, and the editor coordinator, with its three routes set   */
            /* at once -- before any editor exists, since each editor copies   */
            /* two of them when it is registered (CommandEditorCoordinator.    */
            /* cs, EUI_SetInputRoutes).                                        */
            Main_objLifetimes = new LFT_clsLifetimes();
            Main_objCommandInputs = new IWR_clsInputWindows(objClient, Main_objLifetimes);
            Main_dictTargets = new Dictionary<RQT_clsRequest, TIN_clsTargetIntent>();
            Main_sWorkRoot = sWorkRoot;
            Main_pfnOtherOwner = pfnOtherOwner;
            Main_objCommandEditors = new EUI_clsEditorCoordinator(objClient, sWorkRoot,
                Main_RefreshCommandHost, Main_ReportCommandSave, Main_objLifetimes);
            Main_objCommandEditors.EUI_SetInputRoutes(Main_RequestEditorTarget,
                Main_CanTargetImage, Main_CloseWindowsInput);

            /* The progress windows, then the question presenter.  The        */
            /* presenter finds each question's owner window through           */
            /* Main_FindCommandOwner, which asks the progress windows: a      */
            /* question asked while a modal progress window is up (a copy's   */
            /* conflict, an Exit) is shown over THAT window, not over the     */
            /* main one, which it has disabled.                               */
            Main_objCommandProgress = new PGW_clsProgressWindows(objClient, this,
                Main_fntDialog, Main_ReportCommandProgress);
            Main_objCommandQuestions = new QPR_clsQuestionPresenter(objClient,
                Main_objCommandEditors, this, Main_fntDialog, Main_fntHeading,
                Main_FindCommandOwner, Main_objCommandInputs);
        }

        /*------------------------------------------------------------------*/
        /* Main_RefreshCommandHost:                                         */
        /*                                                                  */
        /* The editor coordinator's route for re-listing the Windows pane   */
        /* after an editor has saved a Windows file, so the pane shows the  */
        /* file as it now is.  Only the Windows side is re-listed; nothing  */
        /* is read from the image.                                          */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     None.                                                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the Windows pane listed again.                        */
        /*------------------------------------------------------------------*/
        private void Main_RefreshCommandHost()
        {
            /* The folder the Windows pane shows now, listed again by the   */
            /* pane's own navigation (MainForm.cs).                         */
            Main_NavigateHost(Main_sHostDir);
        }

        /*------------------------------------------------------------------*/
        /* Main_ReportCommandProgress:                                      */
        /*                                                                  */
        /* The progress windows' route for a failure of a progress window   */
        /* itself (one that could not be shown, say -- CommandProgressWin-  */
        /* dows.cs).  Shown in the status line; the command's own result    */
        /* is reported separately, when it comes.                           */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objResult : the failure.                                     */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : its text in the status line.                          */
        /*------------------------------------------------------------------*/
        private void Main_ReportCommandProgress(RSL_clsResult objResult)
        {
            /* Only shown: nothing is retried and nothing else posted. */
            Main_SetStatus(objResult.RSL_sDetail);
        }

        /*------------------------------------------------------------------*/
        /* Main_FindCommandOwner:                                           */
        /*                                                                  */
        /* The question presenter's lookup: the window a question naming    */
        /* the given id is to be shown over -- the check window, a progress */
        /* window, or the partition window, whichever is live and is the    */
        /* one named; for the Exit question (id 0) the window that is modal */
        /* over the main one.  The worker carries only ids; the windows are */
        /* found here, on the UI thread.                                    */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     lWindow : the id the question names -- a window's id or a    */
        /*               run's serial; 0 for the Exit question.             */
        /*                                                                  */
        /* Returns:                                                         */
        /*     Form : the window, or null when none is found (the presenter */
        /*            then decides what a question with no owner does).     */
        /*------------------------------------------------------------------*/
        private Form Main_FindCommandOwner(long lWindow)
        {
            Form frmProgress;

            if (lWindow == 0)
            {   /* The Exit question.  Its request has a serial of its own,   */
                /* so it cannot name the window that is up -- the window      */
                /* modal over the main one is found instead.                  */
                frmProgress = Main_objCommandProgress.PGW_ExitOwner();
                if (frmProgress != null)
                {   /* A progress window is up: the question goes over it,   */
                    /* since the main window under it is disabled and a      */
                    /* question owned by it would open behind.               */
                    return(frmProgress);
                }

                if (Main_objPartitionView != null)
                {   /* No progress window: the partition window, if one is   */
                    /* up, is modal over the main one in the same way.       */
                    frmProgress = Main_objPartitionView.PUI_ExitOwner();
                    if (frmProgress != null)
                    {   /* It is: the Exit is asked over it.  Being shown   */
                        /* over it gives the question no say over the       */
                        /* window's table.                                  */
                        return(frmProgress);
                    }
                }
            }

            if (Main_frmCheck != null && !Main_frmCheck.IsDisposed &&
                (lWindow == Main_lCommandCheckWindow || lWindow == Main_lCommandCheckRun))
            {   /* The check window, named by its own id or by the serial   */
                /* of the run it now shows: either finds it.                */
                return(Main_frmCheck);
            }

            /* A progress window for the command the id names: preferred   */
            /* over the main window, which is disabled under it.           */
            frmProgress = Main_objCommandProgress.PGW_FindProgressOwner(lWindow);
            if (frmProgress != null)
            {   /* That command's progress window is up: the question goes   */
                /* over it.                                                  */
                return(frmProgress);
            }

            if (Main_objPartitionView != null)
            {   /* The partition window, for its own questions (the close's   */
                /* discard question, its operations' questions).              */
                frmProgress = Main_objPartitionView.PUI_FindOwner(lWindow);
                if (frmProgress != null)
                {   /* This id is its own and it is live.  An older           */
                    /* window's id never finds a newer one (PUI_FindOwner).   */
                    return(frmProgress);
                }
            }

            if (Main_pfnOtherOwner == null)
            {   /* No further lookup was given (the start-up passes none):   */
                /* null, rather than guessing at some other window.          */
                return(null);
            }

            /* The further lookup's own answer, whatever it is. */
            return(Main_pfnOtherOwner(lWindow));
        }

        /*------------------------------------------------------------------*/
        /* Main_ReportCommandSave:                                          */
        /*                                                                  */
        /* The editor coordinator's route for reporting a save's result,    */
        /* once the editor has been told: the result's text in the status   */
        /* line -- or, for a save that succeeded but left a warning (the    */
        /* old copy that could not be removed, say), the warning, since     */
        /* that is the only thing then worth saying.  The save stays a      */
        /* success.                                                         */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     frmOwner  : the editor that saved (unused: the status line   */
        /*                 is the main window's).                           */
        /*     objResult : the save's result.                               */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the text shown; nothing retried.                      */
        /*------------------------------------------------------------------*/
        private void Main_ReportCommandSave(Form frmOwner, RSL_clsResult objResult)
        {
            ESR_clsResult objNative;
            string sDetail;

            /* The engine's own record, if the result carries one: only it   */
            /* holds a warning (ESR_sWarning).  By default the result's      */
            /* text is what is shown.                                        */
            objNative = objResult.RSL_objPayload as ESR_clsResult;
            sDetail = objResult.RSL_sDetail;
            if (objResult.RSL_iStatus == SYS_clsSystem.SYS_OK && objNative != null &&
                !String.IsNullOrEmpty(objNative.ESR_sWarning))
            {   /* Saved, with a warning: the warning is shown instead. */
                sDetail = objNative.ESR_sWarning;
            }

            /* Shown in the status line, and nothing more: the save is   */
            /* not repeated, and where it went is not changed.           */
            Main_SetStatus(sDetail);
        }

        /*------------------------------------------------------------------*/
        /* Main_RenderCommandEditorView:                                    */
        /*                                                                  */
        /* The first of the main renderer's three: the worker's state, the  */
        /* windows it waits on (an editor's Save As target, the partition   */
        /* and check windows, the progress windows), and the views that     */
        /* belong to the editors, inputs and questions.  It only shows what */
        /* the view carries; the worker decides what follows.               */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objView : the view.                                          */
        /*                                                                  */
        /* Returns:                                                         */
        /*     bool : true when it took the view; false to leave it to the  */
        /*            next renderer (an Opened view is noted here and still */
        /*            left to the next).                                    */
        /*------------------------------------------------------------------*/
        private bool Main_RenderCommandEditorView(VWM_clsView objView)
        {
            RSL_clsResult objOpenResult;
            ICV_clsInputView objInput;
            EOR_clsOpenResult objOpened;
            PRS_clsPresentation objDisplay;

            if (objView.VWM_enmKind == VWK_enmViewKind.State)
            {   /* The worker's new state (the client has already recorded   */
                /* it, Cli_Dispatch): given to every part that shows it.     */
                if (Main_objCommandEditors == null)
                {   /* Not bound yet: a guard -- the worker is started only   */
                    /* after every part exists (Main_StartCommandSystem).     */
                    return(false);
                }

                /* The editors' "image there or gone" marks, the main       */
                /* window's menus and panes, and the partition window's     */
                /* buttons, all from this one state -- so no part shows a   */
                /* mix of two.  The partition window keeps "working" past   */
                /* an idle state while its results are still owed           */
                /* (PUI_ApplyState).                                        */
                Main_objCommandEditors.EUI_ApplyState((STV_clsStateView)objView.VWM_objValue);
                Main_ObserveCommandState((STV_clsStateView)objView.VWM_objValue);
                if (Main_objPartitionView != null)
                {   /* A partition window's view exists: its buttons too. */
                    Main_objPartitionView.PUI_ApplyState((STV_clsStateView)objView.VWM_objValue);
                }

                /* The state is taken here; no other renderer needs it. */
                return(true);
            }

            if (Main_RenderTargetResult(objView))
            {   /* The result of an editor's Save As target choice, for   */
                /* the editor that asked.                                 */
                return(true);
            }

            if (Main_RenderPartitionView(objView))
            {   /* The partition window's own views (its opening, its   */
                /* Close).                                              */
                return(true);
            }

            if (Main_RenderCommandCheckView(objView))
            {   /* The check window's own views (its opening, its runs'   */
                /* lines and ends, its Close).                            */
                return(true);
            }

            if (Main_objCommandProgress != null &&
                Main_objCommandProgress.PGW_RenderProgress(objView))
            {   /* A progress window's opening, counts or close. */
                return(true);
            }

            if (Main_objCommandEditors == null)
            {   /* Not bound yet (a guard, as above): no editor, input or   */
                /* question can be shown without its parts.                 */
                return(false);
            }

            switch (objView.VWM_enmKind)
            {   /* One kind of view per case.  None starts engine work. */
                case VWK_enmViewKind.ClipboardSpent:
                    /* A cut has been carried out: the clipboard emptied --   */
                    /* but only if it still holds that cut (its sequence      */
                    /* number), so a copy made since is left alone.           */
                    Main_objCommandClipboard.CLP_Consume((uint)objView.VWM_objValue);

                    /* Taken, whether the clipboard was emptied or not. */
                    return(true);
                case VWK_enmViewKind.Question:
                    /* A question from the worker, which waits for its answer   */
                    /* and decides what follows it.  Offered to the special     */
                    /* askers first, in this order; the ordinary presenter      */
                    /* takes whatever they leave.                               */
                    objDisplay = (PRS_clsPresentation)objView.VWM_objValue;
                    if (Main_RenderElevationQuestion(objDisplay))
                    {   /* The restart as Administrator's own question. */
                        return(true);
                    }

                    if (Main_objPartitionView != null && Main_objPartitionView.PUI_Question(objDisplay))
                    {   /* The partition window's own question, or one held   */
                        /* back until a dialog or report of that window has   */
                        /* returned.                                          */
                        return(true);
                    }

                    if (Main_RenderModeQuestion(objDisplay))
                    {   /* The check window's close question or receipt. */
                        return(true);
                    }

                    /* Any other question: the ordinary presenter. */
                    Main_objCommandQuestions.QPR_Present(objDisplay);

                    /* Taken: the presenter shows it and gives the answer. */
                    return(true);
                case VWK_enmViewKind.InputOpen:
                    /* An input the worker has admitted.  None is the     */
                    /* partition window's: its buttons are run whole by   */
                    /* the worker, every dialog asked as a question.      */
                    objInput = (ICV_clsInputView)objView.VWM_objValue;

                    /* An editor's Save As target input, else one of the   */
                    /* main window's own; false if neither knows it (the   */
                    /* client still settles the input, Cli_Dispatch).      */
                    return(Main_RenderEditorTarget(objInput) || Main_RenderMainInput(objInput));
                case VWK_enmViewKind.EditorOpen:
                    /* An image entry the worker has read out and registered,   */
                    /* with the viewer it chose: the viewer made and shown.     */
                    Main_ShowCommandImageViewer((EVW_clsEditorView)objView.VWM_objValue);

                    /* Taken: the registration has its one window. */
                    return(true);
                case VWK_enmViewKind.HostEditorOpen:
                    /* A Windows file to open in one of the program's own   */
                    /* viewers, by its own path.                            */
                    Main_ShowCommandHostViewer((HVW_clsHostViewerView)objView.VWM_objValue);

                    /* Taken. */
                    return(true);
                case VWK_enmViewKind.EditorShow:
                    /* An Open of a file an editor already saves to: that   */
                    /* editor brought forward, and no second one made.      */
                    Main_ShowOpenEditor(objView);

                    /* Taken. */
                    return(true);
                case VWK_enmViewKind.Opened:
                    /* An open has finished: the question presenter notes       */
                    /* whether its filesystem uses fast_commit and whether it   */
                    /* is FAT or exFAT (for the first-write warning's           */
                    /* sentences), from the open's own result -- never from     */
                    /* what the pane shows.  No result (or not an open's)       */
                    /* passes null, which clears both notes.                    */
                    objOpenResult = objView.VWM_objValue as RSL_clsResult;
                    objOpened = null;
                    if (objOpenResult != null)
                    {   /* A result: the open's facts, if it carries them. */
                        objOpened = objOpenResult.RSL_objPayload as EOR_clsOpenResult;
                    }

                    /* Noted. */
                    Main_objCommandQuestions.QPR_Opened(objOpened);

                    /* NOT taken: the open / close renderer shows the same   */
                    /* view's title and volume facts next.                   */
                    return(false);
                default:
                    /* Anything else (a folder's listing, an open's facts):   */
                    /* the next renderers'.                                   */
                    return(false);
            }
        }

        /*------------------------------------------------------------------*/
        /* Main_ShowCommandImageViewer:                                     */
        /*                                                                  */
        /* An EditorOpen: the image entry the worker has read out and       */
        /* registered as an editor, opened in the viewer the worker chose   */
        /* -- the text, hex or structured editor, or the picture viewer.    */
        /* Nothing is asked again here, and nothing is taken from what the  */
        /* pane shows by now: every fact comes from the view.               */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objOpened : the registered editor, its read-out, the viewer  */
        /*                 chosen, and whether it is a New File's new       */
        /*                 structured document.                             */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the viewer shown and registered; or refused as it was */
        /*            made, its resources released and the worker told      */
        /*            EditorClosed.  A failure throws (to Cli_Dispatch).    */
        /*------------------------------------------------------------------*/
        private void Main_ShowCommandImageViewer(EVW_clsEditorView objOpened)
        {
            OED_clsOpenedEntryData objContent;
            EDR_clsEditor objEditor;
            OFD_clsOpenedFile objFile;
            ECR_clsCopyResult objReady;
            int iSlash;
            int iHow;
            bool bPresented;

            /* The making of the window counted (EUI_Begin/End...): until     */
            /* it has been registered or refused, the worker's wait for       */
            /* editors still being made is not answered -- a close of the     */
            /* image deciding meanwhile which editors exist would miss this   */
            /* one, whose making can run a message loop of its own.           */
            Main_objCommandEditors.EUI_BeginViewerConstruction();
            try
            {   /* The read-out and the editor's registration, from the view. */
                objContent = objOpened.EVW_objResult.RSL_objPayload as OED_clsOpenedEntryData;
                objEditor = objOpened.EVW_objEditor;
                if (objContent == null || objEditor == null || objEditor.EDR_objTarget == null)
                {   /* A part missing: a program error, thrown.  Nothing is    */
                    /* filled in from the pane.  The worker is not left        */
                    /* waiting: an editor id the coordinator never             */
                    /* registered is answered Closed when it is to be closed   */
                    /* (CommandEditorCoordinator.cs, EUI_AskImageClose).       */
                    throw new InvalidOperationException("The command viewer has no registered extracted content.");
                }

                /* The editor's descriptor, every field from the view: the    */
                /* image file and the entry's path, the entry's name (after   */
                /* its last "/" -- an image path), the read-out file it       */
                /* works on, the worker's id and epoch, the range and the     */
                /* device's description, and where it saves now; then the     */
                /* viewer chosen, as the openers' number.  The read-out       */
                /* stays the worker's to remove, whatever Save As later       */
                /* points the editor at.                                      */
                iSlash = objContent.OED_sEntry.LastIndexOf('/');
                objFile = new OFD_clsOpenedFile();
                objFile.OFD_bHostFile = false;
                objFile.OFD_sImageFile = objEditor.EDR_objTarget.TGT_sPath;
                objFile.OFD_sImagePath = objContent.OED_sEntry;
                objFile.OFD_sName = objContent.OED_sEntry.Substring(iSlash + 1);
                objFile.OFD_sTempFile = objContent.OED_ReadPath();
                objFile.OFD_lEditorId = objEditor.EDR_lId;
                objFile.OFD_lEpoch = objEditor.EDR_lEpoch;
                objFile.OFD_ullOffset = objEditor.EDR_objTarget.TGT_ullOffset;
                objFile.OFD_ullLength = objEditor.EDR_objTarget.TGT_ullLength;
                objFile.OFD_sDeviceText = objEditor.EDR_objTarget.TGT_sDeviceText;
                objFile.OFD_sExpectedEntry = objEditor.EDR_sEntry;
                iHow = (int)objOpened.EVW_uiHow;

                if (objOpened.EVW_bNewDocument)
                {   /* A New File for the structured editor: the empty       */
                    /* placeholder already exists in the image, and the      */
                    /* editor starts a new document of its format over it.   */
                    if (objOpened.EVW_uiHow < VWH_clsViewerHow.VWH_EDITOR)
                    {   /* Marked new but not for the structured editor (only   */
                        /* it starts a new document): a program error.          */
                        throw new InvalidOperationException("The new structured viewer has no creation metadata.");
                    }

                    /* The structured editor, its format the choice above      */
                    /* VWH_EDITOR.  False means the editor refused as it was   */
                    /* made and has released what it held, unregistered.       */
                    bPresented = Main_ShowEditor(objFile,
                        iHow - (int)VWH_clsViewerHow.VWH_EDITOR, true);
                }
                else
                {   /* An existing entry: the worker has already chosen the   */
                    /* viewer and settled the size question, so the opener    */
                    /* is told the read-out succeeded and the choice is       */
                    /* resolved (asks nothing).                               */
                    objReady = new ECR_clsCopyResult();
                    objReady.ECR_iStatus = ENG_clsNative.ENG_OK;
                    objReady.ECR_sDetail = "";
                    bPresented = Main_ShowOpened(objFile, iHow, objReady, true);
                }

                if (!bPresented)
                {   /* Refused as it was made, its resources released by the    */
                    /* refusal itself: the worker told EditorClosed, so it      */
                    /* removes the read-out and forgets the editor.  An         */
                    /* exception is NOT treated the same way -- it propagates   */
                    /* -- since a window disposed on the way out is no proof    */
                    /* that its file handles were released.                     */
                    Main_objCommandClient.CLI_Post(new RQT_clsRequest(KND_enmKind.EditorClosed,
                        objEditor.EDR_lId, objEditor.EDR_lEpoch, objEditor.EDR_objTarget,
                        null, 0, 0, null, "", ""), null);
                }
            }
            finally
            {   /* The making has returned, shown, refused or thrown: no   */
                /* longer counted.                                         */
                Main_objCommandEditors.EUI_EndViewerConstruction();
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_ShowCommandHostViewer:                                        */
        /*                                                                    */
        /* A HostEditorOpen: a Windows file opened in one of the program's    */
        /* own viewers, in place -- its own path is the file worked on, so    */
        /* there is no read-out to remove and no image id.  The viewer        */
        /* number comes with the view; the opener still applies its own       */
        /* questions for a Windows file (its size limits, a binary file).     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objOpened : the Windows file's path and the viewer asked for.  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the opener has returned (shown, or refused).            */
        /*--------------------------------------------------------------------*/
        private void Main_ShowCommandHostViewer(HVW_clsHostViewerView objOpened)
        {
            OFD_clsOpenedFile objFile;
            ECR_clsCopyResult objReady;

            /* The making counted, as for an image entry's viewer   */
            /* (Main_ShowCommandImageViewer).                       */
            Main_objCommandEditors.EUI_BeginViewerConstruction();
            try
            {   /* The descriptor of a Windows file: no image file, its     */
                /* path as both where it is and the file worked on, its     */
                /* name from the path, and where it saves now (itself).     */
                /* The coordinator gives it its id when it is registered.   */
                /* Then a read-out result that says "succeeded" -- there    */
                /* was nothing to read out.                                 */
                objFile = new OFD_clsOpenedFile();
                objFile.OFD_bHostFile = true;
                objFile.OFD_sImageFile = "";
                objFile.OFD_sImagePath = objOpened.HVW_sPath;
                objFile.OFD_sName = Path.GetFileName(objOpened.HVW_sPath);
                objFile.OFD_sTempFile = objOpened.HVW_sPath;
                objFile.OFD_sExpectedEntry = objOpened.HVW_sPath;
                objReady = new ECR_clsCopyResult();
                objReady.ECR_iStatus = ENG_clsNative.ENG_OK;
                objReady.ECR_sDetail = "";
                /* Opened with the choice NOT marked resolved (false): the   */
                /* opener's own Windows-file questions still apply.  Its     */
                /* answer is not needed -- a Windows file has no worker      */
                /* registration to give back if it is refused.               */
                Main_ShowOpened(objFile, (int)objOpened.HVW_uiHow, objReady, false);
            }
            finally
            {   /* The making has returned: no longer counted. */
                Main_objCommandEditors.EUI_EndViewerConstruction();
            }
        }

        /*------------------------------------------------------------------*/
        /* Main_ShowOpenEditor:                                             */
        /*                                                                  */
        /* An EditorShow: the file an Open named is already in an editor    */
        /* (the worker found the editor that saves to it), so that editor's */
        /* window is brought to the front instead of a second editor being  */
        /* made, and the status line says why nothing new opened.           */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objView : the view; its value is the editor's id (a long),   */
        /*               its request the Open, whose one path is the file.  */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the editor in front; or, when its window has gone     */
        /*            meanwhile (its making failed, or it closed), the      */
        /*            status line says to open the file again.              */
        /*------------------------------------------------------------------*/
        private void Main_ShowOpenEditor(VWM_clsView objView)
        {
            Form frmEditor;
            string sPath;

            /* The window, by the editor's id, from the coordinator's own      */
            /* registrations (a registered, undisposed window, or null); and   */
            /* the file as the Open named it.  The editor's own EditorOpen     */
            /* view was handled before this one, as views arrive in order.     */
            frmEditor = Main_objCommandEditors.EUI_FindOwner((long)objView.VWM_objValue);
            sPath = objView.VWM_objRequest.RQT_lstPaths[0];

            if (frmEditor == null)
            {   /* No live window under that id: it closed, or its making      */
                /* failed, after the worker looked.  Nothing is opened here;   */
                /* the worker has forgotten the editor (or will, on its        */
                /* EditorClosed), so the next Open makes one.                  */
                Main_SetStatus("The editor holding " + sPath + " has closed; open the file again.");
                return;
            }

            if (frmEditor.WindowState == FormWindowState.Minimized)
            {   /* Minimised to the taskbar: restored first, since bringing a   */
                /* minimised window forward leaves it minimised.                */
                frmEditor.WindowState = FormWindowState.Normal;
            }

            /* The editor made the active window, and the reason said. */
            frmEditor.Activate();
            Main_SetStatus(sPath + " is already open in an editor.");
        }

        /*------------------------------------------------------------------*/
        /* Main_ReleaseViewerResources:                                     */
        /*                                                                  */
        /* A viewer's file and memory released by hand, for a viewer whose  */
        /* showing failed: the hex editor's read handle, the picture        */
        /* viewer's picture and stream.  Disposing a form does not run its  */
        /* FormClosed handler (as found), which is where they are released  */
        /* normally, so a failed viewer would otherwise keep them -- and    */
        /* its read-out could not be removed.                               */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     frmViewer : the viewer.                                      */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : released (or nothing to release for the other         */
        /*            viewers); a failure to release throws.                */
        /*------------------------------------------------------------------*/
        private void Main_ReleaseViewerResources(Form frmViewer)
        {
            HEXF_clsHexEditForm frmHex;
            PIC_clsPictureForm frmPicture;

            /* Which viewer it is.  Only these two hold a file or memory      */
            /* past their making; each has a release of its own that may be   */
            /* called more than once (HexEditForm.cs, PictureViewForm.cs),    */
            /* the same one its close uses, so nothing of theirs is touched   */
            /* from here.                                                     */
            frmHex = frmViewer as HEXF_clsHexEditForm;
            frmPicture = frmViewer as PIC_clsPictureForm;
            if (frmHex != null)
            {   /* The hex editor: its read handle on the file closed,   */
                /* even if it was never shown.                           */
                frmHex.HEXF_ReleaseBaseline();
            }

            if (frmPicture != null)
            {   /* The picture viewer: its picture taken off the window,   */
                /* then the picture and its stream disposed.               */
                frmPicture.PIC_ReleaseContent();
            }
        }

        /*------------------------------------------------------------------*/
        /* Main_TrackCommandViewer:                                         */
        /*                                                                  */
        /* A viewer the openers have made, registered with the editor       */
        /* coordinator and then shown (Main_TrackViewer) -- in that order,  */
        /* so no save or close can come from a window the command system    */
        /* does not know.  If either step fails the viewer is closed,       */
        /* released and disposed here, and the failure goes on up.          */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     frmViewer : the viewer, made, not yet shown.                 */
        /*     objFile   : its descriptor (and, for an image entry, the     */
        /*                 worker's id).                                    */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : registered and shown; a failure throws after the      */
        /*            clean-up.                                             */
        /*------------------------------------------------------------------*/
        private void Main_TrackCommandViewer(Form frmViewer, OFD_clsOpenedFile objFile)
        {
            IntPtr hWindow;

            try
            {   /* The registration, then the showing; anything thrown in   */
                /* either reaches the clean-up below.                       */
                if (Main_objCommandEditors == null)
                {   /* Not bound: a guard (the parts exist before any view   */
                    /* can open a viewer) -- an unregistered viewer is       */
                    /* never shown.                                          */
                    throw new InvalidOperationException("Command editor presentation is not initialized.");
                }

                /* The window's handle made now (reading Handle creates it),   */
                /* before registration: the coordinator needs a window that    */
                /* exists.  The viewer keeps no owner window.                  */
                hWindow = frmViewer.Handle;
                if (hWindow == IntPtr.Zero)
                {   /* No handle: refused.  Whether Handle can ever give    */
                    /* zero rather than throw is not established; this is   */
                    /* a guard.                                             */
                    throw new InvalidOperationException("The editor has no window handle.");
                }

                /* Registered (its end hooked, the worker told it exists),   */
                /* then shown modeless, with no owner, and listed by the     */
                /* main window's own tracker.                                */
                Main_objCommandEditors.EUI_Register(frmViewer, objFile);
                Main_TrackViewer(frmViewer);
            }
            catch
            {   /* The registration or the showing failed: the viewer is   */
                /* closed the ordinary way first, so any handler already   */
                /* hooked (the registration's end, the viewer's own        */
                /* FormClosed release) runs as for a normal close.         */
                try
                {   /* Closed, unless it is already gone (a structured   */
                    /* editor that refused its load can close itself     */
                    /* while being shown).                               */
                    if (!frmViewer.IsDisposed)
                    {   /* Still there: closed. */
                        frmViewer.Close();
                    }
                }
                finally
                {   /* Then released by hand, for a failure that came    */
                    /* before FormClosed could run -- this tells the     */
                    /* worker nothing; an EditorClosed comes only from   */
                    /* the registration's own end.                       */
                    try
                    {   /* The viewer's file and memory released. */
                        Main_ReleaseViewerResources(frmViewer);
                    }
                    finally
                    {   /* And the form disposed, even if that release threw. */
                        frmViewer.Dispose();
                    }
                }

                /* The clean-up done: the original failure thrown on, so the   */
                /* caller does not take a disposed viewer for a closed one.    */
                throw;
            }
        }

        /* The image pane's listing. */

        /*------------------------------------------------------------------*/
        /* Main_RenderCommandNavigation:                                    */
        /*                                                                  */
        /* The third of the main renderer's three: a Folder view -- the     */
        /* listing a navigation asked for, or the re-list the worker makes  */
        /* after a change -- shown in the image pane, but only when it is   */
        /* still about the filesystem the main window has open and, for a   */
        /* navigation, still the newest one asked for.  It only paints; no  */
        /* listing is asked for again here.                                 */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objView : the view.                                          */
        /*                                                                  */
        /* Returns:                                                         */
        /*     bool : true for a Folder view of the main window's (shown,   */
        /*            or dropped as stale); false for any other view.       */
        /*------------------------------------------------------------------*/
        private bool Main_RenderCommandNavigation(VWM_clsView objView)
        {
            FVW_clsFolderView objFolder;
            STV_clsStateView objSource;
            ELR_clsListResult objRows;

            if (objView.VWM_enmKind != VWK_enmViewKind.Folder)
            {   /* Not a folder's listing: not this renderer's. */
                return(false);
            }

            /* Whose listing.  A re-list after a change is the main pane's    */
            /* whichever window asked for the change (an editor's save, a     */
            /* picker's new folder), so it is shown; only a navigation        */
            /* asked for by another window (a non-zero origin) is not --      */
            /* the image browser's own listings come as views of their own.   */
            if (objView.VWM_objRequest != null && objView.VWM_objRequest.RQT_enmKind == KND_enmKind.ListFolder &&
                objView.VWM_objRequest.RQT_lOrigin != 0)
            {   /* Another window's navigation: never painted over the   */
                /* main pane's path and rows.                            */
                return(false);
            }

            /* The listing, and the worker's state as last published -- the   */
            /* open the main window shows now -- to hold it against.          */
            objFolder = (FVW_clsFolderView)objView.VWM_objValue;
            objSource = Main_objCommandClient.CLI_ReadState();

            if (objSource.STV_enmMode != MOD_enmMode.Open || objSource.STV_objTarget == null ||
                objFolder.FVW_lEpoch != objSource.STV_lEpoch || objView.VWM_objRequest == null ||
                !objSource.STV_objTarget.TGT_SameRange(objView.VWM_objRequest.RQT_objTarget))
            {   /* Nothing open now, another open (epoch), or another range:   */
                /* a late listing, taken and dropped.  The epoch and range     */
                /* are what tell two partitions of one image file apart, as    */
                /* the path box's file name cannot.                            */
                return(true);
            }

            if (objView.VWM_objRequest.RQT_enmKind == KND_enmKind.ListFolder &&
                objView.VWM_objRequest != Main_objLatestNavigation)
            {   /* A navigation that is no longer the newest: dropped, so     */
                /* it cannot spend the newer one's Back flag or replace the   */
                /* folder just typed.                                         */
                return(true);
            }

            if (objFolder.FVW_objResult.RSL_iStatus != SYS_clsSystem.SYS_OK)
            {   /* The listing failed: the rows and the folder they belong   */
                /* to stay as they were, the path box goes back to that      */
                /* folder, no Back is spent, and the reason goes to the      */
                /* status line.  A failed re-list after a change is only     */
                /* this -- the change itself was reported by its own         */
                /* result.                                                   */
                Main_txtImagePath.Text = Main_sImageDir;
                Main_bImageBackNav = false;
                Main_SetStatus(objFolder.FVW_objResult.RSL_sDetail);
                return(true);
            }

            if (objFolder.FVW_objVolume != null && Main_bImageOpen)
            {   /* A re-list after a change carries the filesystem's fresh   */
                /* facts: the status line's usage is recomputed from them    */
                /* before the row painter writes the line, so it follows     */
                /* copies and edits.                                         */
                Main_sVolumeText = Main_ComposeVolumeText(objFolder.FVW_objVolume.EVR_stVolume,
                    Main_ullImageOffset, Main_bReadOnly);
            }

            /* The rows: the listing's own list, used as it is, not copied.   */
            /* It must name the same folder as the view.                      */
            objRows = objFolder.FVW_objResult.RSL_objPayload as ELR_clsListResult;

            if (objRows == null || objRows.ELR_sImageDir != objFolder.FVW_sFolder)
            {   /* No rows, or rows of another folder: a program error,       */
                /* thrown -- showing them would put one folder's rows under   */
                /* another's path.  The client logs it (Cli_Dispatch); the    */
                /* pane stays as it was.                                      */
                throw new InvalidOperationException("The completed directory view has no matching rows.");
            }

            if (Main_lFolderEpoch != objFolder.FVW_lEpoch)
            {   /* The first listing of a new open: the Back history names   */
                /* another filesystem's folders and is emptied, and the      */
                /* pane's folder is set to this one FIRST -- otherwise the   */
                /* painter would put the previous image's folder onto the    */
                /* Back history as if the user had moved from it.            */
                Main_lstImageBack.Clear();
                Main_sImageDir = objFolder.FVW_sFolder;
                Main_lFolderEpoch = objFolder.FVW_lEpoch;
            }

            /* Whether this listing is a Back, from this request's own flag   */
            /* (never a flag left over from an earlier click: a re-list       */
            /* after a change is never a Back); then the painter makes the    */
            /* folder, the path box, the history and the rows the             */
            /* listing's, in this one UI turn (MainForm.cs).                  */
            Main_bImageBackNav = objView.VWM_objRequest.RQT_enmKind == KND_enmKind.ListFolder &&
                (objView.VWM_objRequest.RQT_uiFlags & MAIN_NAV_BACK) != 0;
            Main_ShowListResult(objRows);
            /* Shown; nothing is posted. */
            return(true);
        }

        /* The worker's state shown in the main window. */

        /*------------------------------------------------------------------*/
        /* Main_ObserveCommandState:                                        */
        /*                                                                  */
        /* A published state shown in the main window: whether an image is  */
        /* open and writable, which filesystem it is, the image pane        */
        /* emptied when the open has changed or ended, and every menu and   */
        /* toolbar item set from it.  It only paints; what may run is still */
        /* the worker's decision.                                           */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objState : the state, already recorded by the client.        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the window shows the state.  An open state with no    */
        /*            target throws (a program error).                      */
        /*------------------------------------------------------------------*/
        private void Main_ObserveCommandState(STV_clsStateView objState)
        {
            /* The two flags the main window's older code reads (the rows'   */
            /* and menus' own painters), set from this state.  They are a    */
            /* copy for showing, never permission for a request.  Set        */
            /* FIRST: emptying the rows below can raise selection events     */
            /* (as found), which repaint the toolbar from these flags.       */
            Main_bImageOpen = objState.STV_enmMode == MOD_enmMode.Open;
            Main_bReadOnly = !objState.STV_bWritable;

            if (Main_bImageOpen)
            {   /* A filesystem is open: which one is taken from the     */
                /* state's own target -- never from a dialog that last   */
                /* showed a path.                                        */
                if (objState.STV_objTarget == null)
                {   /* Open with no target: a program error, thrown -- the   */
                    /* rows could not be named.                              */
                    throw new InvalidOperationException("The open state has no filesystem target.");
                }

                /* The open's file, device description and range, set      */
                /* before the open's facts are painted into the title --   */
                /* so a file opened after a device does not keep the       */
                /* device's name.  The editors keep their own records.     */
                Main_sImageFile = objState.STV_objTarget.TGT_sPath;
                Main_sDeviceText = objState.STV_objTarget.TGT_sDeviceText;
                Main_ullImageOffset = objState.STV_objTarget.TGT_ullOffset;
                Main_ullImageLength = objState.STV_objTarget.TGT_ullLength;

                if (Main_lStateEpoch != objState.STV_lEpoch)
                {   /* A new open (the epoch moves only for a new open, not   */
                    /* for a reopen of the same one for writing): the old     */
                    /* filesystem's rows, history, folder and usage go now,   */
                    /* even if the new one's first listing then fails; the    */
                    /* newest navigation is forgotten.  A reopen keeps all    */
                    /* of these and waits for its re-list.                    */
                    Main_lStateEpoch = objState.STV_lEpoch;
                    Main_lstImage.Items.Clear();
                    Main_lstImageBack.Clear();
                    Main_sImageDir = "/";
                    Main_txtImagePath.Text = "/";
                    Main_sVolumeText = "";
                    Main_objLatestNavigation = null;
                }
            }
            else if (objState.STV_enmMode != MOD_enmMode.None || !objState.STV_bRunning)
            {   /* No filesystem to browse: a close or a failed open has      */
                /* finished, or the check or partition window owns the        */
                /* disk.  The pane shows "no image".  But NOT while the       */
                /* worker is still running with nothing open -- a reopen of   */
                /* the same filesystem for writing passes through that, and   */
                /* its folder and Back history must survive it; the rows      */
                /* stay, greyed, until the next state.                        */
                Main_ShowNoImage();
                Main_objLatestNavigation = null;
                Main_lFolderEpoch = 0;
            }

            /* The toolbars and menus: first from the panes' own selection    */
            /* rules, then narrowed by this state (Main_ApplyCommandAvail-    */
            /* ability).  The menu and toolbar painters narrow the same way   */
            /* after their own runs, so a late selection event cannot         */
            /* enable image work while a command runs.                        */
            Main_UpdateToolbar();
            Main_ApplyCommandAvailability();
        }

        /*------------------------------------------------------------------*/
        /* Main_ApplyCommandAvailability:                                   */
        /*                                                                  */
        /* The main window's commands narrowed by the worker's state: what  */
        /* the panes' own rules enabled is greyed while the worker runs, an */
        /* Exit is pending, or the check or partition window owns the disk; */
        /* image commands need an open image.  It only ever greys what the  */
        /* selection rules allowed, and it is advice -- the worker's gate   */
        /* checks every request again when it is taken.                     */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     None.                                                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : every command item's Enabled set, and the Enable      */
        /*            Writing item's text.                                  */
        /*------------------------------------------------------------------*/
        private void Main_ApplyCommandAvailability()
        {
            STV_clsStateView objState;
            bool bIdle;
            bool bImage;
            bool bLifecycle;
            int i;

            if (Main_objCommandClient == null)
            {   /* No client yet: the window is still being built and its    */
                /* painters run before the command system exists.  Nothing   */
                /* is narrowed -- and no absent client is ever taken for     */
                /* leave to work.                                            */
                return;
            }

            /* The state read once, for every item this turn, so all of       */
            /* them follow the same state.  "Idle" also asks the command      */
            /* system itself (under its lock) whether fresh user work would   */
            /* be admitted -- that sees a pending Exit at once, before any    */
            /* state says so.  An image command needs idle and an open        */
            /* image; Open, Check, New Image and the device commands need     */
            /* idle and no check or partition window (mode None or Open).     */
            objState = Main_objCommandClient.CLI_ReadState();
            bIdle = !objState.STV_bRunning && !objState.STV_bExiting &&
                Main_objCommandClient.CLI_ReadSystem().SYS_CanAcceptUserWork();
            bImage = bIdle && objState.STV_enmMode == MOD_enmMode.Open;
            bLifecycle = bIdle && (objState.STV_enmMode == MOD_enmMode.None || objState.STV_enmMode == MOD_enmMode.Open);

            /* The File menu's open and whole-disk commands.  The check and   */
            /* partition windows own their disk for as long as they are up,   */
            /* even while the worker is idle, so these follow the mode, not   */
            /* the image flag.  Partition/Format too, whatever image or       */
            /* image editor is open: its sequence asks to close the image,    */
            /* each editor asked to save, discard or cancel, once the disk    */
            /* has been chosen.  Exit until an Exit has been agreed.          */
            Main_tsmOpenMenu.Enabled = bLifecycle;
            Main_tsmOpenImage.Enabled = bLifecycle;
            Main_tsmOpenDevice.Enabled = bLifecycle;
            Main_tsdOpen.Enabled = bLifecycle;
            Main_tsmNewImage.Enabled = bLifecycle;
            Main_tsbNewImage.Enabled = bLifecycle;
            Main_tsmReadDevice.Enabled = bLifecycle;
            Main_tsmWriteDevice.Enabled = bLifecycle;
            Main_tsmPartition.Enabled = bLifecycle;

            /* Check Filesystem as the open commands, but greyed while a FAT   */
            /* or exFAT filesystem is open: with an image open the item        */
            /* checks THAT image, and the check is e2fsprogs' e2fsck, which    */
            /* reads only ext2/3/4 -- the program has no FAT check.  With      */
            /* nothing open it stays offered (its pickers choose the place).   */
            Main_tsmCheckFs.Enabled = bLifecycle &&
                !(objState.STV_enmMode == MOD_enmMode.Open && Main_OpenIsFat());
            Main_tsmCloseImage.Enabled = bImage;
            Main_tsbClose.Enabled = bImage;
            Main_tsmEnableWrite.Enabled = bImage;
            Main_tsbWrite.Enabled = bImage && !objState.STV_bWritable;
            Main_tsmFsProperties.Enabled = bImage;
            Main_tsmExit.Enabled = !objState.STV_bExiting;

            /* The Enable / Disable Writing item says what the state IS,      */
            /* never what a click has only asked for; its handler posts the   */
            /* mode it wants, so a second click while the first waits         */
            /* cannot flip it back.                                           */
            if (objState.STV_enmMode == MOD_enmMode.Open && objState.STV_bWritable)
            {   /* Open for writing: the item offers to stop writing. */
                Main_tsmEnableWrite.Text = "Disable &Writing to Image";
            }
            else
            {   /* Read-only or nothing open: the item offers to enable   */
                /* writing (whether it can be clicked was set above).     */
                Main_tsmEnableWrite.Text = "Enable &Writing to Image...";
            }

            /* A pane's list about to be greyed while it holds the keyboard      */
            /* focus is remembered first.  Greying the focused list makes        */
            /* WinForms move the focus on to the next control -- the pane's      */
            /* path box -- and nothing would give it back when the command       */
            /* ends, so the user's next Delete, Ctrl+A or arrow key would land   */
            /* in the path box instead of the list (Main_RestoreBusyFocus).      */
            if (!bImage && Main_lstImage.Focused)
            {   /* The image pane's list had the focus as its command began. */
                Main_bBusyFocusImage = true;
            }

            if (!bIdle && Main_lstHost.Focused)
            {   /* The Windows pane's list had the focus as its command began. */
                Main_bBusyFocusHost = true;
            }

            /* The image pane's path box, path icon and list follow the       */
            /* same state as the toolbars (the icon's menu opens images, so   */
            /* it follows the open commands).  Greyed or not, they grant      */
            /* nothing: the worker's gate still decides.                      */
            Main_txtImagePath.ReadOnly = !bImage;
            Main_picImagePath.Enabled = bLifecycle;
            Main_lstImage.Enabled = bImage;

            /* The Windows pane and the commands shared by both panes need     */
            /* only the worker idle, whatever the mode.  (While the check or   */
            /* partition window is up the whole main window is disabled        */
            /* under it, both being modal.)  The idle test above already       */
            /* sees a pending Exit.                                            */
            Main_lstHost.Enabled = bIdle;
            Main_txtHostPath.ReadOnly = !bIdle;
            Main_picHostPath.Enabled = bIdle;
            Main_tsdPlaces.Enabled = bIdle;
            Main_tsbRefresh.Enabled = bIdle;
            Main_tsmCommands.Enabled = bIdle;
            Main_tsmEdit.Enabled = bIdle;
            Main_tsmOptVerify.Enabled = bIdle;
            Main_tsmOptWarnings.Enabled = bIdle;
            Main_tsmChooseFolder.Enabled = bIdle;

            if (!bIdle)
            {   /* The worker is busy (or an Exit is pending): both panes'   */
                /* toolbars greyed whole.  Their selection rules are not     */
                /* touched -- the next idle pass applies them again.         */
                for (i = 0; i < Main_atspSide.Length; i++)
                {   /* One pane's toolbar per iteration. */
                    Main_atsbBack[i].Enabled = false;
                    Main_atsbUp[i].Enabled = false;
                    Main_atsbCut[i].Enabled = false;
                    Main_atsbCopy[i].Enabled = false;
                    Main_atsbPaste[i].Enabled = false;
                    Main_atsbNewFolder[i].Enabled = false;
                    Main_atsdNew[i].Enabled = false;
                    Main_atsbRename[i].Enabled = false;
                    Main_atsbDelete[i].Enabled = false;
                    Main_atsbProperties[i].Enabled = false;
                }
            }

            /* Refresh (the menu's and the right-click menu's).  No          */
            /* selection rule sets it anywhere else, so it is set fully      */
            /* here -- both ways -- or a busy spell with the image pane      */
            /* active would leave it grey for the Windows pane afterwards.   */
            Main_tsmRefresh.Enabled = bIdle && (Main_bHostPaneActive || bImage);
            Main_tsmCtxRefresh.Enabled = bIdle && (Main_bHostPaneActive || bImage);

            if (!bImage)
            {   /* No image work possible (nothing open, or busy): the image   */
                /* pane's toolbar and the transfer commands greyed.  Only      */
                /* greyed -- the next toolbar pass after an idle open state    */
                /* enables them again by their own rules.                      */
                Main_atsbBack[MAIN_SIDE_IMAGE].Enabled = false;
                Main_atsbUp[MAIN_SIDE_IMAGE].Enabled = false;
                Main_atsbCut[MAIN_SIDE_IMAGE].Enabled = false;
                Main_atsbCopy[MAIN_SIDE_IMAGE].Enabled = false;
                Main_atsbPaste[MAIN_SIDE_IMAGE].Enabled = false;
                Main_atsbNewFolder[MAIN_SIDE_IMAGE].Enabled = false;
                Main_atsdNew[MAIN_SIDE_IMAGE].Enabled = false;
                Main_atsbRename[MAIN_SIDE_IMAGE].Enabled = false;
                Main_atsbDelete[MAIN_SIDE_IMAGE].Enabled = false;
                Main_atsbProperties[MAIN_SIDE_IMAGE].Enabled = false;
                Main_tsbToImage.Enabled = false;
                Main_tsbToHost.Enabled = false;
                Main_tsmImport.Enabled = false;
                Main_tsmExtract.Enabled = false;
                Main_tsmCtxTransfer.Enabled = false;

                if (!Main_bHostPaneActive || !bIdle)
                {   /* And the menus' item commands too, when they would act   */
                    /* on the image pane (it is the active one) or when the    */
                    /* worker is busy -- with the Windows pane active and      */
                    /* the worker idle they act on Windows files and keep      */
                    /* their own rules.                                        */
                    Main_tsmOpen.Enabled = false;
                    Main_tsmOpenAs.Enabled = false;
                    Main_tsmNew.Enabled = false;
                    Main_tsmNewFolder.Enabled = false;
                    Main_tsmRename.Enabled = false;
                    Main_tsmDelete.Enabled = false;
                    Main_tsmProperties.Enabled = false;
                    Main_tsmBack.Enabled = false;
                    Main_tsmUp.Enabled = false;

                    /* The Edit menu's and the right-click menu's items      */
                    /* likewise.  Their painters apply their own selection   */
                    /* and clipboard rules first, and this pass after them   */
                    /* only greys.                                           */
                    Main_tsmCut.Enabled = false;
                    Main_tsmCopy.Enabled = false;
                    Main_tsmPaste.Enabled = false;
                    Main_tsmCtxCut.Enabled = false;
                    Main_tsmCtxCopy.Enabled = false;
                    Main_tsmCtxPaste.Enabled = false;
                    Main_tsmCtxOpen.Enabled = false;
                    Main_tsmCtxOpenAs.Enabled = false;
                    Main_tsmCtxNew.Enabled = false;
                    Main_tsmCtxNewFolder.Enabled = false;
                    Main_tsmCtxRename.Enabled = false;
                    Main_tsmCtxDelete.Enabled = false;
                    Main_tsmCtxProperties.Enabled = false;
                }
            }

            /* A list greyed with the focus in it gets the focus back once it   */
            /* is usable again (no change while the work is still running).     */
            Main_RestoreBusyFocus();
        }

        /*------------------------------------------------------------------*/
        /* Main_OpenIsFat:                                                  */
        /*                                                                  */
        /* Whether the open whose facts were last shown is a FAT12, FAT16,  */
        /* FAT32 or exFAT filesystem -- what Check Filesystem's greying,    */
        /* the Properties dialog's FAT form and the New File name check     */
        /* (MainForm.cs) go by.  The value is the                           */
        /* engine's own (BFE_VolumeInfo's volume type, kept by              */
        /* Main_RenderCommandLifecycle); callers ask only while the worker  */
        /* says a filesystem is open, since it is not cleared at a close.   */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     None.                                                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     bool : true for a FAT or exFAT open; false for ext2/3/4, or  */
        /*            before any open's facts have been shown.              */
        /*------------------------------------------------------------------*/
        private bool Main_OpenIsFat()
        {
            /* Every FAT kind the engine names; anything else -- ext2/3/4's   */
            /* one value, or 0 before any open -- is not FAT.                 */
            return(Main_uiOpenVolumeType == ENG_clsNative.ENG_VOLTYPE_FAT12 ||
                Main_uiOpenVolumeType == ENG_clsNative.ENG_VOLTYPE_FAT16 ||
                Main_uiOpenVolumeType == ENG_clsNative.ENG_VOLTYPE_FAT32 ||
                Main_uiOpenVolumeType == ENG_clsNative.ENG_VOLTYPE_EXFAT);
        }

        /*--------------------------------------------------------------------*/
        /* Main_RestoreBusyFocus:                                             */
        /*                                                                    */
        /* Give the keyboard focus back to the pane's list that held it when  */
        /* a command greyed it (Main_ApplyCommandAvailability records which). */
        /* Greying the focused list makes WinForms park the focus on the next */
        /* control, the pane's path box, so without this every command left   */
        /* the user typing into the path box.                                 */
        /*                                                                    */
        /* It acts only when it is safe to: the list is usable again, the     */
        /* main window is the active one (focusing a control of an inactive   */
        /* window would activate it and pull it in front of an editor the     */
        /* command has just opened), and the focus is still where WinForms    */
        /* parked it -- a path box, or nowhere in the window -- so a control  */
        /* the user chose in the meantime is never taken from them.  Until    */
        /* then the record waits: this runs again on every availability pass  */
        /* and whenever the main window is activated (Main_OnMainActivated).  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the list has the focus again and the record is cleared, */
        /*            or nothing changed and the record still waits.          */
        /*--------------------------------------------------------------------*/
        private void Main_RestoreBusyFocus()
        {
            ListView lstPane;
            bool     bParked;

            if (!Main_bBusyFocusImage && !Main_bBusyFocusHost)
            {   /* No list lost the focus to a command: nothing to give back. */
                return;
            }

            if (Form.ActiveForm != this)
            {   /* Another window is active (an editor the command opened, a    */
                /* dialog).  Focusing a list now would activate the main        */
                /* window over it, so the record waits for Main's activation.   */
                return;
            }

            /* The list to give the focus back to: the image pane's when both   */
            /* were recorded, since the image pane is where commands act.       */
            if (Main_bBusyFocusImage)
            {   /* The image pane's list held the focus. */
                lstPane = Main_lstImage;
            }
            else
            {   /* The Windows pane's list held the focus. */
                lstPane = Main_lstHost;
            }

            if (!lstPane.Enabled || !lstPane.Visible)
            {   /* Still greyed (the work runs on), or its pane is hidden: wait. */
                return;
            }

            /* Where WinForms parked the focus when the list was greyed: a     */
            /* path box, or no control of this window at all.  Anywhere else   */
            /* is a control the user picked since, and is left alone.          */
            bParked = Main_txtImagePath.Focused || Main_txtHostPath.Focused || !this.ContainsFocus;

            if (bParked)
            {   /* The focus is still parked: the list takes it back. */
                lstPane.Focus();
            }

            /* Given back, or overtaken by the user's own choice: either way   */
            /* the record is spent, for both panes.                            */
            Main_bBusyFocusImage = false;
            Main_bBusyFocusHost = false;
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnMainActivated:                                              */
        /*                                                                    */
        /* The main window has become the active window -- the user clicked   */
        /* it, or a modal dialog over it closed and Windows handed the        */
        /* activation back to its owner.  The handler only posts the two      */
        /* follow-ups; they run once this activation, and WinForms' own       */
        /* restoring of the window's focused control, have finished.          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the main window.                                   */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the focus return and the editor's return to the front   */
        /*            are posted.                                             */
        /*--------------------------------------------------------------------*/
        private void Main_OnMainActivated(object objSender, EventArgs evArgs)
        {
            /* A list greyed by a command may now take its focus back. */
            BeginInvoke(new MethodInvoker(Main_RestoreBusyFocus));

            /* An editor opened under a modal dialog goes back in front. */
            BeginInvoke(new MethodInvoker(Main_BringViewerFront));
        }

        /*--------------------------------------------------------------------*/
        /* Main_OnModalClosed:                                                */
        /*                                                                    */
        /* A modal dialog that was up when a viewer opened has closed         */
        /* (Main_TrackViewer subscribed to it).  Windows activates a modal    */
        /* dialog's OWNER as the dialog goes, which puts that owner in front  */
        /* of the viewer opened meanwhile; the viewer is brought back once    */
        /* the dialog's teardown has finished.  This catches an owner other   */
        /* than the main window too, whose activation Main_OnMainActivated    */
        /* would not see.                                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the dialog that closed.                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the viewer's return to the front is posted.             */
        /*--------------------------------------------------------------------*/
        private void Main_OnModalClosed(object objSender, FormClosedEventArgs evArgs)
        {
            /* One closing per subscription: the dialog is going away. */
            ((Form)objSender).FormClosed -= Main_OnModalClosed;

            /* Posted, so it runs after ShowDialog has activated the owner. */
            BeginInvoke(new MethodInvoker(Main_BringViewerFront));
        }

        /*--------------------------------------------------------------------*/
        /* Main_BringViewerFront:                                             */
        /*                                                                    */
        /* Bring to the front the viewer that opened while a modal dialog was */
        /* up (Main_frmFrontViewer), once that dialog has gone.  The record   */
        /* is spent by the first call, so the user's own later choice of      */
        /* window is never overridden.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the viewer is active again, or there was none waiting   */
        /*            (or it has closed since).                               */
        /*--------------------------------------------------------------------*/
        private void Main_BringViewerFront()
        {
            Form frmViewer;

            /* Taken and cleared together, so a second posted call does nothing. */
            frmViewer = Main_frmFrontViewer;
            Main_frmFrontViewer = null;

            if (frmViewer != null && !frmViewer.IsDisposed && frmViewer.Visible)
            {   /* The viewer is still open: it becomes the active window again. */
                frmViewer.Activate();
            }
        }

        /*------------------------------------------------------------------*/
        /* Main_RenderCommandLifecycle:                                     */
        /*                                                                  */
        /* The second of the main renderer's three: an Opened view -- an    */
        /* open, or a reopen for writing or reading, has succeeded -- and   */
        /* its facts painted: the title, the access mode, the volume        */
        /* summary, and which kind of filesystem it is kept for the window  */
        /* (Main_OpenIsFat).  The listing comes separately, as a Folder     */
        /* view the same sequence publishes next; nothing is asked for      */
        /* here.                                                            */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objView : the view.                                          */
        /*                                                                  */
        /* Returns:                                                         */
        /*     bool : true for an Opened view (painted); false for any      */
        /*            other.  Facts that do not match the open throw.       */
        /*------------------------------------------------------------------*/
        private bool Main_RenderCommandLifecycle(VWM_clsView objView)
        {
            RSL_clsResult objResult;
            EOR_clsOpenResult objOpened;
            STV_clsStateView objState;

            if (objView.VWM_enmKind != VWK_enmViewKind.Opened)
            {   /* Not an open's facts: not this renderer's. */
                return(false);
            }

            /* The state as last published, and the open's result.  The       */
            /* worker publishes the state just before the Opened view         */
            /* (CommandSequences.cs), so the state already names this open.   */
            objState = Main_objCommandClient.CLI_ReadState();
            objResult = objView.VWM_objValue as RSL_clsResult;

            if (objResult == null)
            {   /* No result in the view: no facts -- the test below then   */
                /* refuses, rather than painting an earlier open's.         */
                objOpened = null;
            }
            else
            {   /* The open's own facts, if the result carries them. */
                objOpened = objResult.RSL_objPayload as EOR_clsOpenResult;
            }

            /* The facts must be a success's, and of exactly the open the   */
            /* state shows (file, offset, length) before they replace the   */
            /* title and the volume line.                                   */
            if (objOpened == null || objResult.RSL_iStatus != SYS_clsSystem.SYS_OK ||
                objState.STV_enmMode != MOD_enmMode.Open || objState.STV_objTarget == null ||
                !String.Equals(objOpened.EOR_sImagePath, objState.STV_objTarget.TGT_sPath, StringComparison.OrdinalIgnoreCase) ||
                objOpened.EOR_ullOffset != objState.STV_objTarget.TGT_ullOffset || objOpened.EOR_ullLength != objState.STV_objTarget.TGT_ullLength)
            {   /* Missing or someone else's facts: a program error, thrown   */
                /* (the client logs it).  A guard: the worker publishes an    */
                /* Opened view only for a successful open, after the state    */
                /* that names it.                                             */
                throw new InvalidOperationException("The completed open facts do not match the current filesystem.");
            }

            /* The open's kind of filesystem kept first, so the narrowing       */
            /* below and every later one know a FAT or exFAT session (Check     */
            /* greyed, Properties showing FAT's attributes).  A reopen of the   */
            /* same filesystem for writing or reading sets the same value.      */
            Main_uiOpenVolumeType = objOpened.EOR_stVolume.VLI_uiVolumeType;

            /* The facts painted (Main_ApplyOpenResult, MainForm.cs) -- the   */
            /* device's description was already set from the state -- and     */
            /* then the state's narrowing applied again, since that painter   */
            /* sets some toolbar items by its own rules.                      */
            Main_ApplyOpenResult(objOpened);
            Main_ApplyCommandAvailability();
            /* Painted; the listing that follows is the sequence's. */
            return(true);
        }

        /*------------------------------------------------------------------*/
        /* Main_PostLifecycleCommand:                                       */
        /*                                                                  */
        /* Close Image, or Enable / Disable Writing, posted to the worker,  */
        /* which asks what needs asking (unsaved editors, the first-write   */
        /* warning), closes or reopens the session, and puts things back if */
        /* a close fails.  Nothing is changed here.                         */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     enmKind : Close, MakeWritable or MakeReadOnly -- as the      */
        /*               menu item showed it (Main_RequestWriteToggle).     */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : posted (a failure comes back to the status line), or  */
        /*            the reason shown when there is no client.             */
        /*------------------------------------------------------------------*/
        private void Main_PostLifecycleCommand(KND_enmKind enmKind)
        {
            RQT_clsRequest objRequest;

            if (Main_objCommandClient == null)
            {   /* No command system yet: refused with a reason -- there   */
                /* is no other way to the engine.                          */
                Main_SetStatus("The image command system is not ready.");
                return;
            }

            /* The request, for what the state shows now; whether it may   */
            /* run (busy, an Exit pending) is not tested here -- it is     */
            /* posted as fresh user work, so the worker refuses it then.   */
            objRequest = CFC_clsCommands.CFC_Create(enmKind, 0,
                Main_objCommandClient.CLI_ReadState(), null, null, 0, 0, null, "", "main-lifecycle");
            Main_objCommandClient.CLI_Post(objRequest, Main_ReportCommandFailure, true);
        }

        /*------------------------------------------------------------------*/
        /* Main_ReportCommandFailure:                                       */
        /*                                                                  */
        /* The main window's usual completion: a request that failed, was   */
        /* refused or was declined has its reason put in the status line.   */
        /* A success says nothing here -- its views have already shown it.  */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objRequest : the request (unused).                           */
        /*     objResult  : its result.                                     */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the reason shown, or nothing for a success.           */
        /*------------------------------------------------------------------*/
        private void Main_ReportCommandFailure(RQT_clsRequest objRequest, RSL_clsResult objResult)
        {
            if (objResult.RSL_iStatus != SYS_clsSystem.SYS_OK)
            {   /* Not a success: its reason in the status line -- not a   */
                /* message box, which could hide a question the worker     */
                /* shows next.                                             */
                Main_SetStatus(objResult.RSL_sDetail);
            }
        }

        /*------------------------------------------------------------------*/
        /* Main_OnRiskWarningsResult:                                       */
        /*                                                                  */
        /* The completion of Options > Risk Warning Enable: once the worker */
        /* has accepted the change (turning them off asks twice; turning    */
        /* them on asks nothing), the setting is remembered, the tick set   */
        /* and the new state said.  A declined or refused change changes    */
        /* nothing here.                                                    */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objRequest : the request: its options are 1 for "on", 0 for  */
        /*                  "off".                                          */
        /*     objResult  : its result.                                     */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the setting stored and shown, or the reason shown.    */
        /*------------------------------------------------------------------*/
        private void Main_OnRiskWarningsResult(RQT_clsRequest objRequest, RSL_clsResult objResult)
        {
            int iValue;

            if (objResult.RSL_iStatus != SYS_clsSystem.SYS_OK)
            {   /* Declined (a No to either question) or refused: nothing   */
                /* remembered, the tick left as it was, the reason shown.   */
                Main_ReportCommandFailure(objRequest, objResult);
                return;
            }

            /* The value from the REQUEST -- what this click asked for --    */
            /* not from the tick now, which a later click may already have   */
            /* changed.  Results come back in the order the requests ran.    */
            iValue = 0;
            if (objRequest.RQT_uiFlags != 0)
            {   /* The click asked for the warnings on. */
                iValue = 1;
            }

            /* Remembered in the registry -- a failure there is ignored     */
            /* (SET_WriteInt), since the worker already has the new value   */
            /* -- and the tick set to it either way, so the tick and the    */
            /* next click stay right.                                       */
            SET_Settings.SET_WriteInt(SET_Settings.SET_OPT_WARNINGS, iValue);
            Main_tsmOptWarnings.Checked = iValue != 0;

            if (iValue != 0)
            {   /* On again: said so. */
                Main_SetStatus("The risk warnings are on again.");
            }
            else
            {   /* Off: said so, with the way to turn them back on. */
                Main_SetStatus("The risk warnings are off. Options > Risk Warning Enable brings them back.");
            }
        }

        /* The partition window, and the questions the check and partition   */
        /* windows and the restart own.                                      */

        /*------------------------------------------------------------------*/
        /* Main_PostPartitionFile:                                          */
        /*                                                                  */
        /* File > Partition/Format > Image File posted to the worker as ONE */
        /* OpenPartition request with no target, carrying only the folder   */
        /* its open dialog starts in -- the shared image folder as it is    */
        /* now.  Its sequence asks the disk image, asks to close what is    */
        /* open (each image editor asked to save, discard or cancel), asks  */
        /* the risk warning, reads the layout and only then publishes       */
        /* PartitionOpen for the window to be shown                         */
        /* (Main_RenderPartitionView); the outcome comes back to            */
        /* Main_OnPartitionFileResult.  Posted as user work: a busy worker  */
        /* or a pending Exit refuses it.                                    */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     None.                                                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : posted, or the reason it could not be made in the     */
        /*            status line.                                          */
        /*------------------------------------------------------------------*/
        private void Main_PostPartitionFile()
        {
            RQT_clsRequest objRequest;

            if (Main_objCommandClient == null)
            {   /* No command system yet: refused with a reason -- there   */
                /* is no other way to the engine.                          */
                Main_SetStatus("The partition command endpoint is not initialized.");
                return;
            }

            try
            {   /* The request, under the state as last published; the     */
                /* factory checks the kind against its arguments' record   */
                /* and throws for a mismatch.                              */
                objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.OpenPartition, 0,
                    Main_objCommandClient.CLI_ReadState(), null, null, 0, 0,
                    new PIQ_clsPartitionImageQuestion(Main_ImageFolder("")), "", "");
            }
            catch (ArgumentException exInput)
            {   /* Refused by the factory: said, and nothing posted. */
                Main_SetStatus(exInput.Message);
                return;
            }

            /* Posted as user work, as its neighbours' menu posts are:   */
            /* refused while the worker is busy or an Exit is pending.   */
            Main_objCommandClient.CLI_Post(objRequest, Main_OnPartitionFileResult, true);
        }

        /*------------------------------------------------------------------*/
        /* Main_OnPartitionFileResult:                                      */
        /*                                                                  */
        /* The completion of Partition/Format > Image File's request        */
        /* (Main_PostPartitionFile).  The chosen file's folder, once one    */
        /* was chosen, becomes the shared image folder every image dialog   */
        /* starts in, whatever followed -- the close declined, the risk     */
        /* warning answered No, the layout unreadable; a failure's reason   */
        /* is said.  The window itself was shown by its view, not here.     */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objRequest : the request as the worker left it: its target   */
        /*                  the chosen file once its length was read, or    */
        /*                  still its question's record, which holds the    */
        /*                  file once one was chosen.                       */
        /*     objResult  : the outcome.                                    */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the folder remembered, and the reason shown for a     */
        /*            request that did not end in the window.               */
        /*------------------------------------------------------------------*/
        private void Main_OnPartitionFileResult(RQT_clsRequest objRequest, RSL_clsResult objResult)
        {
            PIQ_clsPartitionImageQuestion objAsked;

            /* The question's record, which the request still carries when   */
            /* it ended before the window was made of it; null otherwise.    */
            objAsked = objRequest.RQT_objPayload as PIQ_clsPartitionImageQuestion;
            if (objRequest.RQT_objTarget != null)
            {   /* The sequence made the request the window over the chosen   */
                /* file: that file's folder remembered.                       */
                Main_RememberImageFolder(objRequest.RQT_objTarget.TGT_sPath);
            }
            else if (objAsked != null && objAsked.PIQ_sImage.Length != 0)
            {   /* A file chosen, but its length could not be read (the   */
                /* request still carries its question's record): its      */
                /* folder remembered all the same.                        */
                Main_RememberImageFolder(objAsked.PIQ_sImage);
            }

            /* A failure, refusal or Cancel has its reason in the status   */
            /* line; a success says nothing more (its window has shown).   */
            Main_ReportCommandFailure(objRequest, objResult);
        }

        /*------------------------------------------------------------------*/
        /* Main_RenderPartitionView:                                        */
        /*                                                                  */
        /* The partition window's two views: PartitionOpen -- a new view    */
        /* made and its window shown, modal, with the layout the worker     */
        /* read -- and the worker's WindowClose for it.                     */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objView : the view.                                          */
        /*                                                                  */
        /* Returns:                                                         */
        /*     bool : true when it took the view (a PartitionOpen returns   */
        /*            only once the window has closed); false otherwise.    */
        /*------------------------------------------------------------------*/
        private bool Main_RenderPartitionView(VWM_clsView objView)
        {
            RSL_clsResult objResult;
            ELY_clsLayoutResult objLayout;
            POA_clsPartitionOpenArgs objArgs;
            TGT_clsTarget objTarget;
            string sName;
            bool bAskKind;

            if (objView.VWM_enmKind == VWK_enmViewKind.WindowClose &&
                objView.VWM_objValue is long && Main_objPartitionView != null)
            {   /* A Close naming a window id: the partition view takes it   */
                /* when the id is its own (PUI_Close answers false for       */
                /* another -- the check window's, say -- which then goes     */
                /* on to the next renderer).                                 */
                return(Main_objPartitionView.PUI_Close((long)objView.VWM_objValue));
            }

            if (objView.VWM_enmKind != VWK_enmViewKind.PartitionOpen)
            {   /* Not a partition window's view. */
                return(false);
            }

            /* The worker has read the layout and taken the partition mode   */
            /* before publishing this.  The caption: the file's name, or a   */
            /* device's own description when the request names one; the      */
            /* table-kind question asked for a New Image's file.  Both are   */
            /* overridden by the arguments the request carries -- those      */
            /* Partition/Format's sequence put in once the disk or disk      */
            /* image was chosen (Sys_AskPartitionTarget); a request posted   */
            /* without them (New Image's) keeps these defaults.              */
            objResult = (RSL_clsResult)objView.VWM_objValue;
            objLayout = (ELY_clsLayoutResult)objResult.RSL_objPayload;
            objTarget = objView.VWM_objRequest.RQT_objTarget;
            objArgs = objView.VWM_objRequest.RQT_objPayload as POA_clsPartitionOpenArgs;
            sName = Path.GetFileName(objTarget.TGT_sPath);
            bAskKind = objView.VWM_objRequest.RQT_enmKind == KND_enmKind.NewImage;
            if (objTarget.TGT_sDeviceText.Length != 0)
            {   /* A device: named by its description, not its path. */
                sName = objTarget.TGT_sDeviceText;
            }

            if (objArgs != null)
            {   /* The poster's caption and table-kind flag, as given. */
                sName = objArgs.POA_sName;
                bAskKind = objArgs.POA_bAskKind;
            }

            /* The new view, kept as THE partition view before its window   */
            /* is shown, so the views that arrive during its modal loop     */
            /* (its questions, its Close) find it.  Each view's handlers    */
            /* use its own fields, never this one, so a later view cannot   */
            /* take over an older window's buttons.  Its id is the          */
            /* PartitionOpen request's serial.                              */
            Main_objPartitionView = new Pui_clsPartitionView(this,
                objView.VWM_objRequest.RQT_lSerial, objTarget, sName, bAskKind);

            /* The status line says what is open now, replacing whatever an   */
            /* earlier step left there ("Looking for disks ...", or an        */
            /* earlier refusal) -- the request that got here succeeded, and   */
            /* a success reports no line of its own.                          */
            Main_SetStatus("Partition window: " + sName + ".");
            Main_objPartitionView.PUI_Show(objLayout);

            /* Taken: the window has been shown, closed and disposed (or it   */
            /* could not be shown), with the layout read once.                */
            return(true);
        }

        /*------------------------------------------------------------------*/
        /* Main_RenderElevationQuestion:                                    */
        /*                                                                  */
        /* The worker's StartElevated question -- a step, not a popup: an   */
        /* Exit that restarts the program as Administrator has closed       */
        /* everything, and now the UI starts the elevated copy and answers  */
        /* whether Windows started it.  Yes lets the Exit finish (Stopped); */
        /* No keeps this copy running with what was closed still closed     */
        /* (CommandSequences.cs, Sys_ExitSequence).                         */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objDisplay : the question's display.                         */
        /*                                                                  */
        /* Returns:                                                         */
        /*     bool : true for a StartElevated question (answered, or       */
        /*            dropped as stale); false for any other question.      */
        /*------------------------------------------------------------------*/
        private bool Main_RenderElevationQuestion(PRS_clsPresentation objDisplay)
        {
            QST_clsQuestion objQuestion;
            ELA_clsElevationArgs objArgs;
            ANK_enmAnswerKind enmAnswer;
            bool bStarted;
            string sReason;

            /* Only this question starts the elevated copy -- no click or   */
            /* ordinary answer can.                                         */
            objQuestion = objDisplay.PRS_objQuestion;
            if (objQuestion.QST_enmKind != QNK_enmQuestionKind.StartElevated)
            {   /* Another question: the next asker's. */
                return(false);
            }

            if (!objQuestion.QST_IsCurrent(objDisplay))
            {   /* A display no longer current (late): taken and dropped --   */
                /* it must never start a second copy.                         */
                return(true);
            }

            /* What to run: the start-up command, and any disk chosen        */
            /* before the restart, that the Exit request itself carries --   */
            /* never the device menu's latest choice.                        */
            objArgs = null;
            if (objQuestion.QST_objRequest != null &&
                objQuestion.QST_objRequest.RQT_enmKind == KND_enmKind.Exit)
            {   /* An Exit's question: the restart's arguments from it. */
                objArgs = objQuestion.QST_objRequest.RQT_objPayload as ELA_clsElevationArgs;
            }

            if (objArgs == null || IsDisposed)
            {   /* No arguments, or the main window has gone: nothing is   */
                /* started, and No is answered with the reason, so the     */
                /* Exit stops and this copy goes on.                       */
                objQuestion.QST_Reply(objDisplay, new ANS_clsAnswer(ANK_enmAnswerKind.No,
                    null, "The elevated restart owner is unavailable."));
                return(true);
            }

            /* The copy started (Main_RestartElevated, MainForm.cs) -- the     */
            /* worker waits meanwhile, while Windows shows its own elevation   */
            /* prompt; nothing else can run, the program being in its Exit.    */
            /* Not started until it returns true.                              */
            bStarted = false;
            sReason = "";
            try
            {   /* Started, or not, with Windows' reason; it returns once   */
                /* the prompt has been answered.                            */
                bStarted = Main_RestartElevated(objArgs.ELA_sCommand, objArgs.ELA_sDisk, out sReason);
            }
            catch (Exception exStart)
            {   /* It threw (before Windows was asked, say): not started,   */
                /* the message as the reason, and logged.                   */
                sReason = exStart.Message;
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("app", "elevated restart action failed: " + exStart.Message);
                }
            }

            /* The answer: Yes only when Windows started the copy, so the   */
            /* Exit goes on to Stopped; No otherwise -- the copy keeps      */
            /* running, what was closed stays closed, and the worker lets   */
            /* work in again.                                               */
            enmAnswer = ANK_enmAnswerKind.No;
            if (bStarted)
            {   /* Started: this copy may end. */
                enmAnswer = ANK_enmAnswerKind.Yes;
            }

            /* Answered with the reason.  QST_Reply still refuses it if the   */
            /* display stopped being current meanwhile.                       */
            objQuestion.QST_Reply(objDisplay, new ANS_clsAnswer(enmAnswer, null, sReason));

            /* Taken; nothing more is started or posted. */
            return(true);
        }

        /*------------------------------------------------------------------*/
        /* Main_RenderModeQuestion:                                         */
        /*                                                                  */
        /* The check window's two close questions, neither a popup: its     */
        /* consent (ModeClose) -- always Yes, since the window holds only a */
        /* report, nothing unwritten -- and the receipt the worker waits on */
        /* after its Close (ModeClosed), answered once the window has       */
        /* really gone or the close has failed (Main_TryCheckReceipt).  The */
        /* partition window's own are taken before this (PUI_Question).     */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objDisplay : the question's display.                         */
        /*                                                                  */
        /* Returns:                                                         */
        /*     bool : true for a close question (answered or kept); false   */
        /*            for any other question.                               */
        /*------------------------------------------------------------------*/
        private bool Main_RenderModeQuestion(PRS_clsPresentation objDisplay)
        {
            QST_clsQuestion objQuestion;

            /* The question, and whether it is a close one. */
            objQuestion = objDisplay.PRS_objQuestion;
            if (objQuestion.QST_enmKind != QNK_enmQuestionKind.ModeClose &&
                objQuestion.QST_enmKind != QNK_enmQuestionKind.ModeClosed)
            {   /* Not a close question: the ordinary presenter's. */
                return(false);
            }

            if (objQuestion.QST_lWindow != Main_lCommandCheckWindow &&
                objQuestion.QST_lWindow != Main_lEndedCheckWindow)
            {   /* For no window this side knows (neither the check window   */
                /* now, nor the last one that ended): answered Cancel with   */
                /* the reason -- consent cannot be given, nor a close        */
                /* receipted, for a window that is not here.                 */
                objQuestion.QST_Reply(objDisplay, new ANS_clsAnswer(ANK_enmAnswerKind.Cancel,
                    null, "The operation window's close endpoint is unavailable."));
                return(true);
            }

            if (objQuestion.QST_enmKind == QNK_enmQuestionKind.ModeClose)
            {   /* The consent: Yes at once.  A check window holds a      */
                /* report, not edits, so there is nothing to ask about.   */
                objQuestion.QST_Reply(objDisplay, new ANS_clsAnswer(ANK_enmAnswerKind.Yes, null));
                return(true);
            }

            /* The receipt: kept, and answered now if the window has         */
            /* already gone or its close has already failed; otherwise the   */
            /* window's end, or the close's failure, answers it later.       */
            Main_objCheckReceipt = objDisplay;
            Main_TryCheckReceipt();

            /* Taken; no popup is shown for a receipt. */
            return(true);
        }

        /*------------------------------------------------------------------*/
        /* Main_TryCheckReceipt:                                            */
        /*                                                                  */
        /* The kept ModeClosed receipt answered, if it can be yet: Closed   */
        /* once the check window it names has ended, Cancel (with the       */
        /* reason) once the worker's Close of it has failed.  Called when   */
        /* the receipt arrives and again when either of those happens --    */
        /* never on a timer.                                                */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     None.                                                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : answered, or still kept.                              */
        /*------------------------------------------------------------------*/
        private void Main_TryCheckReceipt()
        {
            PRS_clsPresentation objDisplay;
            ANK_enmAnswerKind enmAnswer;

            if (Main_objCheckReceipt == null)
            {   /* No receipt waiting. */
                return;
            }

            /* The kept display, still current?  An Exit may have replaced     */
            /* the question since; the newer display is answered on its own.   */
            objDisplay = Main_objCheckReceipt;
            if (!objDisplay.PRS_objQuestion.QST_IsCurrent(objDisplay))
            {   /* No longer current: let go unanswered.  The window itself   */
                /* is unaffected.                                             */
                Main_objCheckReceipt = null;
                return;
            }

            if (objDisplay.PRS_objQuestion.QST_lWindow != Main_lEndedCheckWindow &&
                String.IsNullOrEmpty(Main_sCheckCloseError))
            {   /* The window has not ended, and no close has failed: wait. */
                return;
            }

            /* Answerable: let go first, then answered -- Closed when the   */
            /* window named is the one that ended, Cancel otherwise.        */
            Main_objCheckReceipt = null;
            enmAnswer = ANK_enmAnswerKind.Cancel;
            if (objDisplay.PRS_objQuestion.QST_lWindow == Main_lEndedCheckWindow)
            {   /* That window has ended (its FormClosed recorded it). */
                enmAnswer = ANK_enmAnswerKind.Closed;
            }

            /* A Cancel keeps the worker's check mode and stops the command   */
            /* that asked for the close; Closed ends the mode and lets that   */
            /* command go on (CommandSequences.cs).                           */
            objDisplay.PRS_objQuestion.QST_Reply(objDisplay,
                new ANS_clsAnswer(enmAnswer, null, Main_sCheckCloseError));
        }

        /*--------------------------------------------------------------------*/
        /* Main_ReportPartitionResult:                                        */
        /*                                                                    */
        /* A partition window request's result reported to the user           */
        /* (called by Pui_OnResult): a failure's reason, a cancel's           */
        /* consequence, a success's verdict -- in the status line or a        */
        /* message box over the window -- and a warning when the change       */
        /* worked but the layout could not be read again.  It only reports.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the request as posted.                            */
        /*     objResult  : its result.                                       */
        /*     frmLayout  : the partition window (it may have gone).          */
        /*     sName      : the window's caption (the disk's short name).     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : reported.                                               */
        /*--------------------------------------------------------------------*/
        private void Main_ReportPartitionResult(RQT_clsRequest objRequest, RSL_clsResult objResult,
            Form frmLayout, string sName)
        {
            RPO_clsPartitionOutcome objOutcome;
            RSL_clsResult objOperation;
            PAA_clsPartitionArgs objArgs;
            ERS_clsResizeResult objResize;
            ECK_clsCheckResult objCheck;
            ECP_clsCapacityResult objCapacity;
            Form frmOwner;
            string sPlace;
            string sLines;
            bool bPartRaw;
            int i;

            /* The window the message boxes go over: the partition window   */
            /* while it is live (it is modal over the main one), the main   */
            /* window once it has gone -- never whatever partition window   */
            /* may be up by then.                                           */
            frmOwner = this;
            if (frmLayout != null && !frmLayout.IsDisposed)
            {   /* Still live: over it. */
                frmOwner = frmLayout;
            }

            /* The change's own result: for a change, the outcome holds it   */
            /* apart from the re-read after it (reported at the end), so a   */
            /* failed re-read is not taken for a failed change.              */
            objOutcome = objResult.RSL_objPayload as RPO_clsPartitionOutcome;
            objOperation = objResult;
            if (objOutcome != null)
            {   /* A change: its own result. */
                objOperation = objOutcome.RPO_objOperation;
            }

            /* The range's name for the messages, from the request (as the     */
            /* click captured it): a format's or expansion's partition name,   */
            /* or a partition Read's / Write's / Erase's -- else the disk's    */
            /* caption.                                                        */
            objArgs = objRequest.RQT_objPayload as PAA_clsPartitionArgs;
            sPlace = sName;
            if (objArgs != null && objArgs.PAA_sPlace.Length != 0)
            {   /* A format or expansion with its partition's name. */
                sPlace = objArgs.PAA_sPlace;
            }
            else if (objRequest.RQT_objPayload is PRA_clsPartitionRawArgs)
            {   /* A partition's Read / Write / Erase names its partition the same way. */
                sPlace = ((PRA_clsPartitionRawArgs)objRequest.RQT_objPayload).PRA_sPlace;
            }

            /* Whether this is one of the partition's raw commands, which word   */
            /* their own outcomes below.                                         */
            bPartRaw = objRequest.RQT_enmKind == KND_enmKind.ReadPartition ||
                objRequest.RQT_enmKind == KND_enmKind.WritePartition ||
                objRequest.RQT_enmKind == KND_enmKind.ErasePartition;

            if (bPartRaw && objRequest.RQT_objPayload is PRA_clsPartitionRawArgs &&
                ((PRA_clsPartitionRawArgs)objRequest.RQT_objPayload).PRA_sFile.Length != 0)
            {   /* A Read or Write whose sequence was given a file: that file's   */
                /* folder becomes the shared image folder, as in every image      */
                /* dialog -- whether the copy then succeeded or not, since the    */
                /* file was chosen either way.                                    */
                Main_RememberImageFolder(((PRA_clsPartitionRawArgs)objRequest.RQT_objPayload).PRA_sFile);
            }

            if (objOperation.RSL_iStatus != SYS_clsSystem.SYS_OK)
            {   /* Not done -- refused, declined, cancelled or failed.  The   */
                /* reason goes to the status line first; the cases below      */
                /* replace it or add a message box.  Nothing is retried.      */
                Main_SetStatus(objOperation.RSL_sDetail);
                if (objOperation.RSL_iStatus == ENG_clsNative.ENG_ERR_CANCELLED &&
                    objRequest.RQT_enmKind == KND_enmKind.TestCapacity)
                {   /* A capacity test cancelled: the engine puts back what   */
                    /* it wrote before it returns, so the device is as it     */
                    /* was -- said in the status line only.                   */
                    Main_SetStatus("Capacity test cancelled; nothing on the device was changed.");
                    return;
                }

                if (objOperation.RSL_iStatus == ENG_clsNative.ENG_ERR_CANCELLED &&
                    objRequest.RQT_enmKind == KND_enmKind.MakeFilesystem && objArgs != null &&
                    (objArgs.PAA_stOptions.MFO_uiFlags & ENG_clsNative.ENG_MAKEFS_ZERO) != 0)
                {   /* A format with zero-fill cancelled: the zeros come    */
                    /* first, so the start of the range is already erased   */
                    /* and nothing is formatted -- said plainly.            */
                    Main_SetStatus("Cancelled: the start of " + sPlace + " was erased and nothing was formatted.");
                    return;
                }

                if (bPartRaw && objOperation.RSL_iStatus == ENG_clsNative.ENG_ERR_CANCELLED &&
                    objRequest.RQT_objPayload is FMI_clsFormatInput)
                {   /* Cancelled at the file dialog: the request still carries    */
                    /* the range's words, which its sequence replaces only once   */
                    /* a file is chosen (CommandRawSequence.cs) -- so nothing     */
                    /* was read or written, and no file was made.                 */
                    Main_SetStatus("Cancelled: no file was chosen.");
                    return;
                }

                if (bPartRaw && objOperation.RSL_iStatus == ENG_clsNative.ENG_ERR_CANCELLED)
                {   /* Stopped part-way.  A Read's unfinished file is removed by   */
                    /* the engine; a Write or Erase has changed the partition up   */
                    /* to where it stopped, which the status says.                 */
                    if (objRequest.RQT_enmKind == KND_enmKind.ReadPartition)
                    {   /* Nothing kept. */
                        Main_SetStatus("Cancelled: " + sPlace + " was not saved; the unfinished file was removed.");
                    }
                    else
                    {   /* Part written. */
                        Main_SetStatus("Cancelled: the start of " + sPlace + " has already been overwritten.");
                    }

                    return;
                }

                if (bPartRaw && objOperation.RSL_iStatus != SYS_clsSystem.SYS_CANCELLED)
                {   /* A partition command that failed (not one the user   */
                    /* declined): the reason in a box over the window,     */
                    /* whatever it carries.                                */
                    MSG_clsMessageForm.MSG_Show(frmOwner, Main_fntDialog,
                        "The operation on " + sPlace + " failed.\n\n" +
                        ENG_clsNative.ENG_StatusText(objOperation.RSL_iStatus) + "\n" + objOperation.RSL_sDetail,
                        "BifrostFM", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1);
                    return;
                }

                /* A failure the engine ran into: the status, the detail, and   */
                /* -- for an Expand or a check that ran e2fsck -- the end of    */
                /* its report (Main_FailureReportTail), so a check that could   */
                /* not make the filesystem sound says what it found.            */
                if (objRequest.RQT_enmKind != KND_enmKind.WriteTable &&
                    objOperation.RSL_iStatus != SYS_clsSystem.SYS_CANCELLED &&
                    objOperation.RSL_iStatus != ENG_clsNative.ENG_ERR_CANCELLED &&
                    (objOperation.RSL_objPayload is ESR_clsResult ||
                    objOperation.RSL_objPayload is ERS_clsResizeResult ||
                    objOperation.RSL_objPayload is ECP_clsCapacityResult ||
                    objOperation.RSL_objPayload is ECK_clsCheckResult ||
                    objOperation.RSL_objPayload is RPN_clsRepairNeeded))
                {   /* The engine ran and failed (its result carries one of     */
                    /* the engine's records -- a refusal does not), and it is   */
                    /* not a cancel or a No: a message box with the status's    */
                    /* sentence and the detail.  Not for a table write: the     */
                    /* window shows that failure itself (DSK_ShowWriteFailure   */
                    /* from Pui_OnResult).                                      */
                    MSG_clsMessageForm.MSG_Show(frmOwner, Main_fntDialog,
                        "The operation on " + sPlace + " failed.\n\n" +
                        ENG_clsNative.ENG_StatusText(objOperation.RSL_iStatus) + "\n" + objOperation.RSL_sDetail +
                        Main_FailureReportTail(objOperation.RSL_objPayload),
                        "BifrostFM", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1);
                }

                /* Reported: no verdict follows a failure -- a cancelled or   */
                /* failed test or resize proves nothing.                      */
                return;
            }

            /* A capacity test's verdict: passed, or the large red warning. */
            objCapacity = objOperation.RSL_objPayload as ECP_clsCapacityResult;
            if (objCapacity != null)
            {   /* A capacity test: its counts are final (the test has put   */
                /* back what it wrote).                                      */
                Main_ReportPartitionCapacity(frmOwner, sName, objCapacity);
            }

            /* An expansion's verdict: the sizes before and after, with the    */
            /* reports of the check (and repair) that came first and of the    */
            /* resize, in the order they ran -- shown from the results, with   */
            /* nothing run again.                                              */
            objResize = objOperation.RSL_objPayload as ERS_clsResizeResult;
            if (objResize != null)
            {   /* An expansion: its report lines gathered, then its box. */
                sLines = "";
                if (objOutcome != null && objOutcome.RPO_objCheck != null)
                {   /* The report-only check ran first: its lines. */
                    objCheck = Main_CheckWindowResult(objOutcome.RPO_objCheck);
                    if (objCheck.ECK_lstLines != null)
                    {   /* It has lines (a success may have none). */
                        for (i = 0; i < objCheck.ECK_lstLines.Count; i++)
                        {   /* One check line per iteration, in order. */
                            sLines += "\n" + objCheck.ECK_lstLines[i];
                        }
                    }
                }

                if (objOutcome != null && objOutcome.RPO_objRepair != null)
                {   /* A repair ran (the check found errors): its lines next. */
                    objCheck = Main_CheckWindowResult(objOutcome.RPO_objRepair);
                    if (objCheck.ECK_lstLines != null)
                    {   /* It has lines (none does not make the repair   */
                        /* fail).                                        */
                        for (i = 0; i < objCheck.ECK_lstLines.Count; i++)
                        {   /* One repair line per iteration, in order. */
                            sLines += "\n" + objCheck.ECK_lstLines[i];
                        }
                    }
                }

                if (objResize.ERS_lstLines != null)
                {   /* The resize's own lines last. */
                    for (i = 0; i < objResize.ERS_lstLines.Count; i++)
                    {   /* One resize line per iteration. */
                        sLines += "\n" + objResize.ERS_lstLines[i];
                    }
                }

                if (objResize.ERS_ullBlocksAfter == objResize.ERS_ullBlocksBefore)
                {   /* No change: the filesystem already filled its room.  A   */
                    /* success -- said so, without the report lines.           */
                    MSG_clsMessageForm.MSG_Show(frmOwner, Main_fntDialog,
                        "The filesystem in " + sPlace + " already fills its space; nothing was changed.",
                        "Expand Filesystem", MessageBoxButtons.OK, MessageBoxIcon.Information,
                        MessageBoxDefaultButton.Button1);
                }
                else
                {   /* Grown: the sizes before and after (blocks times the   */
                    /* block size), the report lines under them, and a       */
                    /* status line.                                          */
                    MSG_clsMessageForm.MSG_Show(frmOwner, Main_fntDialog,
                        "The filesystem in " + sPlace + " was grown from " +
                        DVF_clsDeviceForm.DVF_SizeShortText(objResize.ERS_ullBlocksBefore * objResize.ERS_uiBlockSize) + " to " +
                        DVF_clsDeviceForm.DVF_SizeShortText(objResize.ERS_ullBlocksAfter * objResize.ERS_uiBlockSize) + "." + sLines,
                        "Expand Filesystem", MessageBoxButtons.OK, MessageBoxIcon.Information,
                        MessageBoxDefaultButton.Button1);
                    Main_SetStatus("Expanded " + sPlace + ".");
                }
            }

            if (objRequest.RQT_enmKind == KND_enmKind.MakeFilesystem)
            {   /* A format that finished says so, replacing whatever the line   */
                /* held -- a "Cancelled: ..." from an earlier attempt among      */
                /* them, which would otherwise outlive the success.              */
                Main_SetStatus("Made the filesystem in " + sPlace + ".");
            }
            else if (objRequest.RQT_enmKind == KND_enmKind.WriteTable)
            {   /* Likewise a table that was written. */
                Main_SetStatus("Wrote the partition table of " + sName + ".");
            }

            if (bPartRaw)
            {   /* A partition command that finished: said on the status line. */
                if (objRequest.RQT_enmKind == KND_enmKind.ReadPartition)
                {   /* The partition is in the file. */
                    Main_SetStatus("Saved " + sPlace + " to " + ((PRA_clsPartitionRawArgs)objRequest.RQT_objPayload).PRA_sFile + ".");
                }
                else if (objRequest.RQT_enmKind == KND_enmKind.WritePartition)
                {   /* The file is in the partition (and read back). */
                    Main_SetStatus("Wrote " + ((PRA_clsPartitionRawArgs)objRequest.RQT_objPayload).PRA_sFile + " into " + sPlace + ".");
                }
                else
                {   /* The partition holds zeros. */
                    Main_SetStatus("Erased " + sPlace + ".");
                }
            }

            if (objOutcome != null && objOutcome.RPO_objLayout != null &&
                objOutcome.RPO_objLayout.RSL_iStatus != SYS_clsSystem.SYS_OK)
            {   /* The change worked but the layout could not be read again:   */
                /* a warning over the window (its rows are now old), and the   */
                /* reason in the status line.  The change stands -- nothing    */
                /* is undone because the display could not follow it.          */
                MSG_clsMessageForm.MSG_Show(frmOwner, Main_fntDialog,
                    "The disk operation completed, but the partition display could not be refreshed.\n\n" +
                    objOutcome.RPO_objLayout.RSL_sDetail + "\n\nUse Refresh to read the disk again.",
                    "Partitions", MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
                Main_SetStatus(objOutcome.RPO_objLayout.RSL_sDetail);
            }
        }

        /*------------------------------------------------------------------*/
        /* Main_ReportPartitionCapacity:                                    */
        /*                                                                  */
        /* A finished capacity test's verdict: a plain "holds what it       */
        /* claims", or the large red warning with the size the device       */
        /* really holds, how it fails past that, and the sample counts.     */
        /* Either way the test has already put back what it overwrote.      */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     frmOwner : the window the box goes over.                     */
        /*     sName    : the device's short name.                          */
        /*     objTest  : the test's result.                                */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the verdict shown, and summed up in the status line.  */
        /*------------------------------------------------------------------*/
        private void Main_ReportPartitionCapacity(Form frmOwner, string sName, ECP_clsCapacityResult objTest)
        {
            string sVerdict;

            if (!objTest.ECP_bCounterfeit)
            {   /* Every test block read back as written: it holds what it   */
                /* claims -- said in a plain box and the status line.        */
                MSG_clsMessageForm.MSG_Show(frmOwner, Main_fntDialog, sName + " holds what it claims:\n" +
                    DVF_clsDeviceForm.DVF_SizeText(objTest.ECP_ullReported) + "\n\nAll " + objTest.ECP_uiSamples +
                    " test blocks, spread across the whole device, read back exactly as written, and what was there before has been put back.",
                    "Test Capacity", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
                Main_SetStatus("Capacity test passed: " + sName + " holds " + DVF_clsDeviceForm.DVF_SizeShortText(objTest.ECP_ullReported) + ".");
                return;
            }

            /* It does not: the size it claims beside the size it really     */
            /* holds.  The wording says "counterfeit (or failing)" because   */
            /* a fake controller and a genuine card with a bad stretch       */
            /* read the same to this test.                                   */
            sVerdict = sName + " claims to hold:\n    " + DVF_clsDeviceForm.DVF_SizeText(objTest.ECP_ullReported) +
                "\nIt really holds only:\n    " + DVF_clsDeviceForm.DVF_SizeText(objTest.ECP_ullGood) + "\n\n";
            if (objTest.ECP_bWraps)
            {   /* Its addresses wrap: a write past the real end lands on   */
                /* data stored nearer the start and overwrites it.          */
                sVerdict += "Its addresses repeat: anything written past " + DVF_clsDeviceForm.DVF_SizeShortText(objTest.ECP_ullGood) +
                    " lands on data already stored nearer the start, and overwrites it.\n";
            }
            else
            {   /* No wrap: writes past the real end are accepted and then   */
                /* lost.                                                     */
                sVerdict += "Anything written past " + DVF_clsDeviceForm.DVF_SizeShortText(objTest.ECP_ullGood) +
                    " is accepted and reported as written, and then lost: reading it back gives something else.\n";
            }

            /* The sample counts, that the test put things back, and the        */
            /* advice -- under the large red heading -- then the status line.   */
            sVerdict += "\n" + objTest.ECP_uiBadSamples + " of the " + objTest.ECP_uiSamples +
                " test blocks did not read back as written. What was there before the test has been put back.\n\n" +
                "This is a counterfeit (or failing) card. Do not trust it with data: a filesystem made on it must be no larger " +
                "than its real size, and it is best not used at all.";
            MSG_clsMessageForm.MSG_ShowWarned(frmOwner, Main_fntDialog, Main_fntHeading,
                "WARNING: COUNTERFEIT OR FAILING CARD", sVerdict, "Test Capacity",
                MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1);
            Main_SetStatus("Capacity test FAILED: " + sName + " holds " + DVF_clsDeviceForm.DVF_SizeShortText(objTest.ECP_ullGood) +
                ", not the " + DVF_clsDeviceForm.DVF_SizeShortText(objTest.ECP_ullReported) + " it claims.");
        }

        /* The check window. */

        /*------------------------------------------------------------------*/
        /* Main_PostCheckIntent:                                            */
        /*                                                                  */
        /* Check Filesystem, or the check window's Repair, posted to the    */
        /* worker for the given range.  A Repair names the check window     */
        /* that is open as its origin, so a late Repair can never act on a  */
        /* range another window was opened for -- the worker compares the   */
        /* two when it takes the request.                                   */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objTarget : the range to check (null: nothing to check).     */
        /*     bRepair   : true for the check window's Repair.              */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : posted (a refusal or failure comes back to the status */
        /*            line), or the reason shown.                           */
        /*------------------------------------------------------------------*/
        private void Main_PostCheckIntent(TGT_clsTarget objTarget, bool bRepair)
        {
            RQT_clsRequest objRequest;
            KND_enmKind enmKind;
            long lOrigin;

            if (Main_objCommandClient == null)
            {   /* No command system yet: refused with a reason -- there   */
                /* is no other way to the engine.                          */
                Main_SetStatus("The command check endpoint is not initialized.");
                return;
            }

            if (objTarget == null)
            {   /* No range: there is nothing to check -- said, and nothing   */
                /* posted.  (Whether a check may run is the worker's          */
                /* question, not this test's.)                                */
                Main_SetStatus("No filesystem target was supplied for the check.");
                return;
            }

            /* A Check comes from the main window (origin 0); a Repair from   */
            /* the check window, by its id.                                   */
            enmKind = KND_enmKind.Check;
            lOrigin = 0;
            if (bRepair)
            {   /* The Repair: the check window's own id as the origin. */
                enmKind = KND_enmKind.Repair;
                lOrigin = Main_lCommandCheckWindow;
            }

            /* Posted as fresh user work, with the usual failure report as     */
            /* its completion.  The report itself reaches the check window     */
            /* through the run's own views (CheckBegin / CheckFinished), and   */
            /* the completion never starts anything.                           */
            objRequest = CFC_clsCommands.CFC_Create(enmKind, lOrigin,
                Main_objCommandClient.CLI_ReadState(), objTarget, null, 0, 0, null, "", "");
            Main_objCommandClient.CLI_Post(objRequest, Main_ReportCommandFailure, true);
        }

        /*------------------------------------------------------------------*/
        /* Main_PostCheckCancel:                                            */
        /*                                                                  */
        /* A Cancel posted for one check run, by its serial -- as when the  */
        /* check window could not be shown.  The worker refuses a           */
        /* Cancel for a run that is no longer running, so it can never stop */
        /* a later one.                                                     */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     lRun : the run's request serial (not the window's id).       */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the Cancel posted.                                    */
        /*------------------------------------------------------------------*/
        private void Main_PostCheckCancel(long lRun)
        {
            /* The Cancel, from the check window's id, naming the run.  It   */
            /* is the only way to stop the run: no flag is shared with the   */
            /* worker.                                                       */
            Main_objCommandClient.CLI_Post(CFC_clsCommands.CFC_Create(KND_enmKind.Cancel,
                Main_lCommandCheckWindow, null, null, null, lRun, 0, null, "", ""), null);
        }

        /*------------------------------------------------------------------*/
        /* Main_CheckWindowResult:                                          */
        /*                                                                  */
        /* A check run's result in the shape the check window shows: the    */
        /* engine's report, unwrapped from an expansion's record when it is */
        /* in one; with the result's own status when the two differ (a      */
        /* failure in the progress callback is cancelled in the engine but  */
        /* is a failure); and a failure report when there is no report at   */
        /* all -- never a clean-looking one with zero counts.  Neither of   */
        /* the results it reads is changed.                                 */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objResult : the run's result.                                */
        /*                                                                  */
        /* Returns:                                                         */
        /*     ECK_clsCheckResult : the report to show (the engine's own    */
        /*                          object when nothing needed changing).   */
        /*------------------------------------------------------------------*/
        private static ECK_clsCheckResult Main_CheckWindowResult(RSL_clsResult objResult)
        {
            RPN_clsRepairNeeded objNeeded;
            ECK_clsCheckResult objNativeResult;
            int iStatus;
            string sDetail;

            /* The engine's report: straight in the result for a check or a   */
            /* repair, or inside the "repair needed" record an expansion's    */
            /* report-only check returns.                                     */
            objNeeded = objResult.RSL_objPayload as RPN_clsRepairNeeded;
            if (objNeeded == null)
            {   /* A check or a repair: the report is the payload. */
                objNativeResult = objResult.RSL_objPayload as ECK_clsCheckResult;
            }
            else
            {   /* An expansion's check: the report inside its record. */
                objNativeResult = objNeeded.RPN_objReport as ECK_clsCheckResult;
            }

            if (objNativeResult == null)
            {   /* No report (an exception came first, say): a failure       */
                /* report, so the window never shows an absent report as a   */
                /* clean filesystem with zero counts.                        */

                /* Its status: the result's, when that is a failure; a   */
                /* success with no report is made an internal failure.   */
                iStatus = objResult.RSL_iStatus;
                if (iStatus == SYS_clsSystem.SYS_OK)
                {   /* "OK" with no report is not true: internal failure. */
                    iStatus = SYS_clsSystem.SYS_INTERNAL;
                }

                /* Its reason: the result's, or a plain "no report" when it   */
                /* gives none.                                                */
                if (objResult.RSL_sDetail.Length == 0)
                {   /* No reason given: the missing report is the reason. */
                    sDetail = "The check returned no report.";
                }
                else
                {   /* The result's own reason. */
                    sDetail = objResult.RSL_sDetail;
                }

                /* Only the status and the reason -- no counts or findings   */
                /* are made up.                                              */
                return(new ECK_clsCheckResult { ECK_iStatus = iStatus, ECK_sDetail = sDetail });
            }

            if (objNativeResult.ECK_iStatus == objResult.RSL_iStatus)
            {   /* The report and the result agree: shown as it is. */
                return(objNativeResult);
            }

            /* They differ: a progress callback failed and the engine was       */
            /* cancelled only to stop safely.  A copy with the result's real    */
            /* status and reason, keeping the report's facts and counts (a      */
            /* repair may have changed things) and its exit bits without the    */
            /* "cancelled" bit -- a copy, since the report is shared with the   */
            /* request's own completion.                                        */
            return(new ECK_clsCheckResult { ECK_iStatus = objResult.RSL_iStatus, ECK_sDetail = objResult.RSL_sDetail,
                ECK_uiExitCode = objNativeResult.ECK_uiExitCode & ~ENG_clsNative.ENG_CHECK_CANCELLED,
                ECK_bModified = objNativeResult.ECK_bModified, ECK_bErrorsLeft = objNativeResult.ECK_bErrorsLeft,
                ECK_uiInodesUsed = objNativeResult.ECK_uiInodesUsed, ECK_uiInodesTotal = objNativeResult.ECK_uiInodesTotal,
                ECK_ullBlocksUsed = objNativeResult.ECK_ullBlocksUsed, ECK_ullBlocksTotal = objNativeResult.ECK_ullBlocksTotal,
                ECK_lstLines = objNativeResult.ECK_lstLines });
        }

        /*------------------------------------------------------------------*/
        /* Main_RenderCommandCheckView:                                     */
        /*                                                                  */
        /* The check window's views: CheckOpen -- the window made and shown */
        /* for the range the worker has taken, MODAL over the main window,  */
        /* so nothing else in the program can be used until it closes,      */
        /* between its runs too; this call returns only once it has closed, */
        /* the views below arriving through its modal loop -- each run's    */
        /* CheckBegin and CheckFinished, the e2fsck lines and progress of   */
        /* the run it shows, and the worker's WindowClose for it.  Each is  */
        /* matched to the window's id and the run's serial, so a late view  */
        /* never reaches a newer window or run.                             */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objView : the view.                                          */
        /*                                                                  */
        /* Returns:                                                         */
        /*     bool : true when it took the view (shown, or dropped as      */
        /*            stale); false for anything else.                      */
        /*------------------------------------------------------------------*/
        private bool Main_RenderCommandCheckView(VWM_clsView objView)
        {
            TGT_clsTarget objTarget;
            CRV_clsCheckRunView objRun;
            PGD_clsProgressData objProgress;
            long lRun;
            CST_clsCheckStop objStop;
            CHK_clsCheckForm frmShown;

            if (objView.VWM_enmKind == VWK_enmViewKind.CheckOpen)
            {   /* The worker has taken the check mode for this range and   */
                /* started its first run.  The window's facts recorded --   */
                /* its id (the Check request's serial), no close failure,   */
                /* the range, and the file / offset / length / device the   */
                /* main window's own check code describes it by.            */
                objTarget = (TGT_clsTarget)objView.VWM_objValue;
                Main_lCommandCheckWindow = objView.VWM_objRequest.RQT_lSerial;
                Main_sCheckCloseError = "";
                Main_objCommandCheckTarget = objTarget;
                Main_sCheckFile = objTarget.TGT_sPath;
                Main_ullCheckOffset = objTarget.TGT_ullOffset;
                Main_ullCheckLength = objTarget.TGT_ullLength;
                Main_sCheckDeviceText = objTarget.TGT_sDeviceText;
                try
                {   /* The window made, its FormClosed hooked, before it is   */
                    /* shown -- so its X, its Repair and any message loop     */
                    /* inside Show already find everything in place.          */
                    Main_frmCheck = new CHK_clsCheckForm(Main_fntDialog, Main_CheckTargetText(), Main_OnCheckRepair);
                    Main_frmCheck.FormClosed += Main_OnCheckClosed;

                    /* Watched, ARMED: FormClosed ends the check mode    */
                    /* normally; a check window disposed without it is   */
                    /* ended by the watch.                               */
                    Main_objLifetimes.LFT_Watch(Main_frmCheck, Main_OnCheckLost, true);

                    /* The first run is already under way, so it is marked      */
                    /* before the window is shown: its Cancel is given that     */
                    /* run's serial (CST_clsCheckStop, so a late click cannot   */
                    /* stop a later Repair), and the window starts in its       */
                    /* running state -- an X at once then asks for a Cancel     */
                    /* and the window stays until the run's Finished.  The      */
                    /* run's own CheckBegin, when it comes, sees the serial     */
                    /* already marked and adds no second heading.               */
                    lRun = objView.VWM_objRequest.RQT_lSerial;
                    Main_lCommandCheckRun = lRun;
                    objStop = new CST_clsCheckStop(Main_objCommandClient, Main_lCommandCheckWindow, lRun);
                    Main_frmCheck.CHK_Attach(objStop.CST_RequestCancel);
                    Main_frmCheck.CHK_Begin(false);

                    /* Shown MODAL over the main window (its owner): the       */
                    /* whole program waits on it, between runs too, until it   */
                    /* closes.  Its runs' views and the worker's questions     */
                    /* reach it through this modal loop.  Kept in a local,     */
                    /* because its FormClosed (Main_EndCommandCheckView)       */
                    /* clears Main_frmCheck before ShowDialog returns.         */
                    frmShown = Main_frmCheck;
                    frmShown.ShowDialog(this);

                    /* Closed: a modal window is only hidden by its close,    */
                    /* so it is disposed here.  Its end was reported by its   */
                    /* FormClosed, which also settled its watch, so this      */
                    /* disposal is not taken for a window lost unreported.    */
                    frmShown.Dispose();
                }
                catch (Exception exCheckView)
                {   /* The window could not be made or shown: the run is        */
                    /* asked to stop, the window (if any) disposed -- Dispose   */
                    /* cannot be refused by its closing handler the way a       */
                    /* Close can -- and its end reported by hand, since its     */
                    /* FormClosed may not have been hooked or may not run.      */
                    /* Nothing else is started.                                 */
                    Main_PostCheckCancel(objView.VWM_objRequest.RQT_lSerial);
                    if (Main_frmCheck != null)
                    {   /* A window was made: disposed. */
                        Main_frmCheck.Dispose();
                    }

                    /* The end (CheckClosed posted), then the reason shown. */
                    Main_EndCommandCheckView(Main_frmCheck);
                    Main_SetStatus("The check window could not be displayed: " + exCheckView.Message);
                }
                return(true);
            }

            if (objView.VWM_enmKind == VWK_enmViewKind.CheckBegin || objView.VWM_enmKind == VWK_enmViewKind.CheckFinished)
            {   /* A run's start or end.  These come as views of their own,   */
                /* not through the request's completion, so the window gets   */
                /* them whatever that completion does.                        */
                objRun = (CRV_clsCheckRunView)objView.VWM_objValue;
                if (objRun.CRV_lWindow != Main_lCommandCheckWindow || Main_frmCheck == null || Main_frmCheck.IsDisposed)
                {   /* Not the window that is open (closed, or another): taken   */
                    /* and dropped -- never shown in another window.             */
                    return(true);
                }

                /* Whether closing the window will reopen the image: kept   */
                /* only for the status line's wording; the worker decides   */
                /* the reopen itself when the window closes.                */
                Main_bCheckReopen = objRun.CRV_bReopen;
                if (objView.VWM_enmKind == VWK_enmViewKind.CheckBegin)
                {   /* A run starting.  A Repair (after its question) has a   */
                    /* serial of its own, so its Cancel needs a new stop.     */
                    if (Main_lCommandCheckRun != objRun.CRV_lRun)
                    {   /* A new run (the first was marked before the show):   */
                        /* its serial, its Cancel, and its heading, once.      */
                        lRun = objRun.CRV_lRun;
                        Main_lCommandCheckRun = lRun;
                        objStop = new CST_clsCheckStop(Main_objCommandClient, Main_lCommandCheckWindow, lRun);
                        Main_frmCheck.CHK_Attach(objStop.CST_RequestCancel);
                        Main_frmCheck.CHK_Begin(objRun.CRV_bRepair);
                    }
                }
                else if (Main_lCommandCheckRun == objRun.CRV_lRun)
                {   /* The end of the run the window shows (a late Finished   */
                    /* of another run is ignored): the window leaves its      */
                    /* running state and shows the verdict, shaped by         */
                    /* Main_CheckWindowResult.                                */
                    Main_lCommandCheckRun = 0;
                    Main_ShowCheckResult(Main_CheckWindowResult(objRun.CRV_objResult));
                }
                return(true);
            }

            if (objView.VWM_enmKind == VWK_enmViewKind.Progress)
            {   /* A progress view.  A check's carry the serial of the run   */
                /* that sent them.                                           */
                objProgress = objView.VWM_objValue as PGD_clsProgressData;
                if (objProgress == null || !objProgress.PGD_bCheck)
                {   /* Not a check's (a copy's, a format's, a capacity   */
                    /* test's): the progress windows' renderer's.        */
                    return(false);
                }

                if (Main_frmCheck != null && !Main_frmCheck.IsDisposed && objView.VWM_objRequest != null &&
                    objView.VWM_objRequest.RQT_lSerial == Main_lCommandCheckRun)
                {   /* For the run the window shows: a report line appended,   */
                    /* or the bar moved.  Lines and counts that arrive after   */
                    /* the run's Finished, or from another run, are dropped.   */
                    if (objProgress.PGD_uiKind == ENG_clsNative.ENG_CHECK_EVENT_TEXT)
                    {   /* A line of e2fsck's report (its text was copied   */
                        /* before the engine's callback returned).          */
                        Main_frmCheck.CHK_AppendLines(new string[] { objProgress.PGD_sImage });
                    }
                    else
                    {   /* Progress: the pass and the counts, which the window   */
                        /* checks for range itself.                              */
                        Main_frmCheck.CHK_ShowProgress(objProgress.PGD_uiPass, objProgress.PGD_ullDone, objProgress.PGD_ullTotal);
                    }
                }
                /* Taken while a check window is open (shown or dropped);   */
                /* with none open, left to the other renderers.             */
                return(Main_lCommandCheckWindow != 0);
            }

            if (objView.VWM_enmKind == VWK_enmViewKind.WindowClose && objView.VWM_objValue is long &&
                (long)objView.VWM_objValue == Main_lCommandCheckWindow && Main_frmCheck != null)
            {   /* The worker's Close of this check window (an Exit, or a   */
                /* run's view that could not be delivered), sent once the   */
                /* engine work has stopped.                                 */
                if (Main_lCommandCheckRun != 0)
                {   /* A run is still marked running -- its CheckFinished   */
                    /* never came (a run whose start failed sends none):    */
                    /* the window's running state ended as cancelled, or    */
                    /* its closing handler would refuse the close.          */
                    Main_lCommandCheckRun = 0;
                    Main_frmCheck.CHK_Finish(new ECK_clsCheckResult { ECK_iStatus = ENG_clsNative.ENG_ERR_CANCELLED });
                }

                /* The close.  Only its FormClosed (Main_EndCommandCheckView,   */
                /* which zeroes the id) counts as the end: a Close that         */
                /* returns with the window still there is a refusal.            */
                Main_sCheckCloseError = "";
                try
                {   /* Closed; the id still set afterwards means it was not. */
                    Main_frmCheck.Close();
                    if (Main_lCommandCheckWindow != 0)
                    {   /* Still open after Close: refused, the reason kept. */
                        Main_sCheckCloseError = "The check window declined to close.";
                    }
                }
                catch (Exception exClose)
                {   /* The Close threw: the reason kept.  The mode stays and   */
                    /* the program stays open, so it can be tried again.       */
                    Main_sCheckCloseError = exClose.Message;
                }

                /* The receipt answered now if it is already waiting (a        */
                /* failure); after a real close Main_EndCommandCheckView has   */
                /* posted the same look for a later UI turn.                   */
                Main_TryCheckReceipt();
                return(true);
            }

            /* Not a check window's view (the partition window's Close, a   */
            /* progress view of another kind ...).                          */
            return(false);
        }

        /*------------------------------------------------------------------*/
        /* Main_OnCheckLost:                                                */
        /*                                                                  */
        /* The window watch found the check window gone without its         */
        /* FormClosed having run: end its view as FormClosed would have,    */
        /* which posts CheckClosed so the worker leaves check mode (and     */
        /* reopens a filesystem the check closed).  A normal close settles  */
        /* the watch in Main_EndCommandCheckView, so this is reached only   */
        /* for a window disposed unclosed; and that function ignores a      */
        /* window that is no longer the current one, so one already ended   */
        /* makes this do nothing.                                           */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     frmLost : the gone check window.                             */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : CheckClosed posted, or nothing when already ended.    */
        /*------------------------------------------------------------------*/
        private void Main_OnCheckLost(Form frmLost)
        {
            /* The same end as a real close. */
            Main_EndCommandCheckView(frmLost);
        }

        /*------------------------------------------------------------------*/
        /* Main_EndCommandCheckView:                                        */
        /*                                                                  */
        /* The check window's end: its FormClosed (Main_OnCheckClosed), the */
        /* window watch finding it gone (Main_OnCheckLost), or a window     */
        /* that could not be shown.  The routing is cleared and CheckClosed */
        /* posted -- once -- and the worker then leaves check mode and      */
        /* reopens a filesystem the check closed; nothing is reopened here. */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     frmCheck : the check window that ended, or null when it was  */
        /*                never made.                                       */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : CheckClosed posted, or nothing for a window already   */
        /*            ended or not the current one.                         */
        /*------------------------------------------------------------------*/
        private void Main_EndCommandCheckView(Form frmCheck)
        {
            long lWindow;
            TGT_clsTarget objTarget;

            if (frmCheck != Main_frmCheck || Main_lCommandCheckWindow == 0)
            {   /* Not the current window, or its end already reported (a   */
                /* second notice): nothing -- an old window's end must      */
                /* never end a newer one's mode.                            */
                return;
            }

            /* The end is reported here, so the window watch has nothing     */
            /* left to report: it stops watching this window, and the        */
            /* disposal that follows a close is not mistaken for a window    */
            /* lost unreported (a null form, from the construction-failure   */
            /* path, was never watched).                                     */
            Main_objLifetimes.LFT_Settle(frmCheck);

            /* The routing cleared BEFORE the post: the id kept as the ended   */
            /* window's (for its ModeClosed receipt) and the current one's     */
            /* zeroed, so a late FormClosed from the disposed window can       */
            /* never be taken for a newer window's end.  Then CheckClosed,     */
            /* with the window's id and range.                                 */
            lWindow = Main_lCommandCheckWindow;
            Main_lEndedCheckWindow = lWindow;
            objTarget = Main_objCommandCheckTarget;
            Main_lCommandCheckWindow = 0;
            Main_lCommandCheckRun = 0;
            Main_objCommandCheckTarget = null;
            Main_frmCheck = null;
            Main_objCommandClient.CLI_Post(CFC_clsCommands.CFC_Create(KND_enmKind.CheckClosed,
                lWindow, null, objTarget, null, 0, 0, null, "", ""), null);

            /* The receipt looked at on a later UI turn, not now: this can   */
            /* be running inside the window's Close call (its FormClosed),   */
            /* and the answer must wait until that call has returned.        */
            BeginInvoke(new MethodInvoker(Main_TryCheckReceipt));
        }

        /* Copies, and their reports. */

        /*------------------------------------------------------------------*/
        /* Main_PostCommandCopy:                                            */
        /*                                                                  */
        /* A copy or cut posted to the worker -- Extract, Import, or a copy */
        /* within the image -- from the paths and the open the click, drop  */
        /* or paste captured, with the Options menu's copy settings read    */
        /* NOW, so a later change of them cannot alter a queued copy.  The  */
        /* worker runs the copy, its questions and progress, and a cut's    */
        /* removal of its sources; nothing is copied here.                  */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     enmKind            : Extract, Import or CopyWithin.          */
        /*     arrImagePaths      : the image side's paths: an Extract's or */
        /*                          a copy-within's sources, an Import's    */
        /*                          destination.                            */
        /*     arrOtherPaths      : the other side's: the Windows paths, or */
        /*                          a copy-within's destinations.           */
        /*     objSource          : the state (the open and its epoch) the  */
        /*                          paths belong to.                        */
        /*     bMoveSources       : a cut: remove the sources after a whole */
        /*                          copy (a rename within the image);       */
        /*                          optional, false.                        */
        /*     arrDirectories     : for an Extract's cut, which sources are */
        /*                          folders; optional, null.                */
        /*     uiClipboard        : a clipboard cut's sequence number (so   */
        /*                          the clipboard is cleared only if it     */
        /*                          still holds it); optional, 0.           */
        /*     bAutoNames         : a copy beside itself, the worker        */
        /*                          picking the new names; optional, false. */
        /*     lClipboardLifetime : the open the clipboard's image items    */
        /*                          were copied from; optional, -1 (none).  */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : posted (its report comes to Main_ReportCommandCopy),  */
        /*            or the reason shown.                                  */
        /*------------------------------------------------------------------*/
        private void Main_PostCommandCopy(KND_enmKind enmKind, string[] arrImagePaths, string[] arrOtherPaths,
            STV_clsStateView objSource, bool bMoveSources = false, bool[] arrDirectories = null,
            uint uiClipboard = 0, bool bAutoNames = false, long lClipboardLifetime = -1)
        {
            EXO_stExtractOptions objExtractOptions;
            IMO_stImportOptions objImportOptions;
            RQT_clsRequest objCopyRequest;

            if (Main_objCommandClient == null)
            {   /* No command system yet: refused with a reason -- there   */
                /* is no other way to the engine.                          */
                Main_SetStatus("The command copy endpoint is not initialized.");
                return;
            }

            if (objSource == null || objSource.STV_objTarget == null || arrImagePaths == null || arrOtherPaths == null || arrImagePaths.Length == 0)
            {   /* Something the request needs is missing (no open, no       */
                /* paths): said, nothing posted.  Whether the copy may run   */
                /* is the worker's question, not this test's.                */
                Main_SetStatus("Select items and a filesystem target before copying.");
                return;
            }

            /* The Extract options, from the Options menu's ticks now: the   */
            /* metadata manifest written beside the files, as the engine's   */
            /* explicit 0 / 1.                                               */
            objExtractOptions = new EXO_stExtractOptions();
            if (Main_tsmOptMetaOut.Checked)
            {   /* Ticked: the manifest is written. */
                objExtractOptions.EXO_uiWriteManifest = 1U;
            }
            else
            {   /* Not ticked: no manifest. */
                objExtractOptions.EXO_uiWriteManifest = 0U;
            }

            /* And whether extracted files get the image's modify times. */
            if (Main_tsmOptSetTimes.Checked)
            {   /* Ticked: the image's times on the Windows files. */
                objExtractOptions.EXO_uiSetTimes = 1U;
            }
            else
            {   /* Not ticked: Windows' own times (the copy's). */
                objExtractOptions.EXO_uiSetTimes = 0U;
            }

            /* The Import options: the defaults for what the Windows files   */
            /* cannot say -- files 0644 (0x1A4), folders 0755 (0x1ED),       */
            /* owner root (0 / 0), the Windows modify time kept -- and the   */
            /* manifest's restore from the Options tick.                     */
            objImportOptions = new IMO_stImportOptions { IMO_uiFileMode = 0x1A4, IMO_uiDirMode = 0x1ED,
                IMO_uiUid = 0, IMO_uiGid = 0, IMO_uiCopyMtime = 1 };
            if (Main_tsmOptMetaIn.Checked)
            {   /* Ticked: a manifest found with the files restores their   */
                /* owners, modes and times.                                 */
                objImportOptions.IMO_uiUseManifest = 1U;
            }
            else
            {   /* Not ticked: the defaults above for every item. */
                objImportOptions.IMO_uiUseManifest = 0U;
            }

            /* The request made, its shape checked by the factory before   */
            /* anything is handed over.                                    */
            try
            {   /* Made; a malformed one throws here. */
                objCopyRequest = CFC_clsCommands.CFC_Create(enmKind, 0, objSource, objSource.STV_objTarget,
                    arrImagePaths, 0, 0, new CCA_clsCopyArgs(arrOtherPaths, objExtractOptions, objImportOptions,
                        bMoveSources, arrDirectories, uiClipboard, bAutoNames, lClipboardLifetime), "", "");
            }
            catch (ArgumentException exInput)
            {   /* A malformed selection or drop: its reason shown, and   */
                /* nothing started or asked.                              */
                Main_SetStatus(exInput.Message);
                return;
            }

            /* Posted.  As fresh user work -- except while a drag is under    */
            /* way: a drop onto the program's own pane is part of the drag,   */
            /* and the drag's hold on the worker would make a fresh user      */
            /* request be refused as busy (Sys_UserWorkBlocked).  The         */
            /* completion only reports.                                       */
            Main_objCommandClient.CLI_Post(objCopyRequest, Main_ReportCommandCopy,
                Main_objDragInput == null && Main_lHostDrag == 0);
        }

        /*------------------------------------------------------------------*/
        /* Main_ReportCommandCopy:                                          */
        /*                                                                  */
        /* A copy's completion: a clean copy said in the status line (with  */
        /* a box for any manifest notes); anything else -- a failure, a     */
        /* partial copy, a cancel, a cut whose sources could not be removed */
        /* -- in a box with the counts and the problem lines.  Then the     */
        /* Windows pane is listed again (the image pane was re-listed by    */
        /* the worker).  It only reports.                                   */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objRequest : the request as posted (its kind gives the       */
        /*                  direction).                                     */
        /*     objResult  : its result -- for a cut, an outcome holding the */
        /*                  copy's result apart from the sources' removal.  */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : reported.                                             */
        /*------------------------------------------------------------------*/
        private void Main_ReportCommandCopy(RQT_clsRequest objRequest, RSL_clsResult objResult)
        {
            KND_enmKind enmKind;
            CPO_clsCopyOutcome objCut;
            ECR_clsCopyResult objReport;
            MessageBoxIcon enmIcon;
            string sTitle;
            string sReport;
            string sStatus;
            int iLine;

            /* The direction, from the request: the report's caption never   */
            /* follows a later click.                                        */
            enmKind = objRequest.RQT_enmKind;

            /* The copy's report.  For a cut it is inside the outcome, kept    */
            /* apart from the removal of the sources: a removal that failed    */
            /* after every copy arrived is reported as that, with the copy's   */
            /* own counts -- never as a failed copy.                           */
            objCut = objResult.RSL_objPayload as CPO_clsCopyOutcome;
            if (objCut == null)
            {   /* A plain copy: the report is the payload. */
                objReport = objResult.RSL_objPayload as ECR_clsCopyResult;
            }
            else
            {   /* A cut: the copy's report from inside the outcome. */
                objReport = objCut.CPO_objCopy.RSL_objPayload as ECR_clsCopyResult;
            }

            if (objReport == null)
            {   /* No copy report: refused, declined, or a cut within the   */
                /* image (a rename) -- there are no counts to show.         */
                if (objResult.RSL_iStatus != SYS_clsSystem.SYS_OK)
                {   /* Not a success: its reason in the status line -- the   */
                    /* result's own detail, or the status's sentence when    */
                    /* it gives none -- and no made-up "0 copied" report.    */
                    if (objResult.RSL_sDetail.Length == 0)
                    {   /* No detail: the status's sentence. */
                        sStatus = ENG_clsNative.ENG_StatusText(objResult.RSL_iStatus);
                    }
                    else
                    {   /* The result's own reason, which is more exact. */
                        sStatus = objResult.RSL_sDetail;
                    }

                    /* Shown; there is no report to show. */
                    Main_SetStatus(sStatus);
                }
                return;
            }

            if (enmKind == KND_enmKind.Extract)
            {   /* Out of the image, to Windows. */
                sTitle = "Extracting to Windows";
            }
            else if (enmKind == KND_enmKind.CopyWithin)
            {   /* From one place in the image to another. */
                sTitle = "Copying within the image";
            }
            else
            {   /* Into the image, from Windows. */
                sTitle = "Importing into the image";
            }

            /* The counts are the copy's, whatever became of a cut's   */
            /* sources afterwards.                                     */
            if (objResult.RSL_iStatus == SYS_clsSystem.SYS_OK)
            {   /* A clean copy: the count in the status line, and the   */
                /* manifest's notes (if any) in a box.                   */
                Main_SetStatus(objReport.ECR_iCopied + " item(s) copied.");
                if (objReport.ECR_lstNotes != null && objReport.ECR_lstNotes.Count != 0)
                {   /* Notes: shown, one paragraph each. */
                    MSG_clsMessageForm.MSG_Show(this, Main_fntDialog, string.Join("\n\n", objReport.ECR_lstNotes.ToArray()), sTitle,
                        MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
                }
            }
            else
            {   /* Not clean (failed, partial, cancelled, or a cut whose   */
                /* sources were not removed): a box with the reason, the   */
                /* counts, the problem lines and the notes.                */
                if (objResult.RSL_iStatus < 0)
                {   /* The command system's own statuses (negative) carry   */
                    /* their reason in the detail.                          */
                    sStatus = objResult.RSL_sDetail;
                }
                else
                {   /* An engine status: its sentence first; the detail   */
                    /* gets its own paragraph below.                      */
                    sStatus = ENG_clsNative.ENG_StatusText(objResult.RSL_iStatus);
                }

                /* The reason and the copy's counts, then the engine detail    */
                /* (a command-system detail was already used as the reason).   */
                sReport = sStatus +
                    "\n\n" + objReport.ECR_iCopied + " item(s) copied, " + objReport.ECR_iProblems + " skipped or failed.";
                if (objResult.RSL_iStatus >= 0 && objResult.RSL_sDetail.Length != 0)
                {   /* An engine status with a detail: added. */
                    sReport += "\n\n" + objResult.RSL_sDetail;
                }

                /* The problem lines, at most MAIN_REPORT_LINES, then how many   */
                /* more there were.                                              */
                if (objReport.ECR_lstProblems != null)
                {   /* A list (the engine's report normally has one). */
                    for (iLine = 0; iLine < objReport.ECR_lstProblems.Count && iLine < MAIN_REPORT_LINES; iLine++)
                    {   /* One problem line per iteration. */
                        sReport += "\n" + objReport.ECR_lstProblems[iLine];
                    }

                    if (objReport.ECR_lstProblems.Count > MAIN_REPORT_LINES)
                    {   /* More than shown: how many more (from the problem   */
                        /* count, not the list's length).                     */
                        sReport += "\n... and " + (objReport.ECR_iProblems - MAIN_REPORT_LINES) + " more";
                    }
                }

                if (objReport.ECR_lstNotes != null && objReport.ECR_lstNotes.Count != 0)
                {   /* Notes: their own paragraphs after the problems. */
                    sReport += "\n\n" + string.Join("\n\n", objReport.ECR_lstNotes.ToArray());
                }

                /* The box's icon: an engine failure that stopped the copy     */
                /* (memory running out, a commit that failed) is an error;     */
                /* a partial or cancelled copy, or a command-system outcome,   */
                /* is reported as information, as the copy itself ran.         */
                if (objResult.RSL_iStatus > 0 &&
                    objResult.RSL_iStatus != ENG_clsNative.ENG_ERR_PARTIAL &&
                    objResult.RSL_iStatus != ENG_clsNative.ENG_ERR_CANCELLED)
                {   /* An engine failure. */
                    enmIcon = MessageBoxIcon.Error;
                }
                else
                {   /* Partial, cancelled, or the command system's own. */
                    enmIcon = MessageBoxIcon.Information;
                }

                /* The box (it reports; it asks nothing), then the counts in   */
                /* the status line.                                            */
                MSG_clsMessageForm.MSG_Show(this, Main_fntDialog, sReport, sTitle,
                    MessageBoxButtons.OK, enmIcon, MessageBoxDefaultButton.Button1);
                Main_SetStatus(objReport.ECR_iCopied + " item(s) copied, " + objReport.ECR_iProblems + " skipped or failed.");
            }

            if (IsDisposed || Main_bCommandStopped)
            {   /* The program is shutting down: a copy that an accepted Exit    */
                /* cancelled reports here, and the report's message box above    */
                /* runs its own message loop, in which the worker's Stopped      */
                /* view can close and dispose this window.  The Windows list     */
                /* has lost its columns by then, so re-listing it would throw    */
                /* (Main_SizeColumns' column 0); there is nothing left to show   */
                /* it in anyway.                                                 */
                return;
            }

            /* The Windows pane listed again (a copy may have changed it).   */
            /* The image pane needs nothing: the worker re-listed it after   */
            /* the copy, a partly done removal of the sources included.      */
            Main_NavigateHost(Main_sHostDir);
        }

        /* New File in the image. */

        /*--------------------------------------------------------------------*/
        /* Main_PostCommandNewFile:                                           */
        /*                                                                    */
        /* New File's request: the placeholder file (empty, or zeros of the   */
        /* asked length for a hex file) written to a stage, and posted with   */
        /* the editor it is to open in.  The worker creates the entry in      */
        /* the image (asking first if writing needs it) and opens the         */
        /* editor itself -- nothing is created or opened here.                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sFolder   : the image folder it goes in (as it was when New    */
        /*                 File was chosen).                                  */
        /*     sName     : its name, already checked (the engine still        */
        /*                 refuses one that exists).                          */
        /*     lBytes    : its length, bytes -- 0 for a text or config file.  */
        /*     iKind     : which kind of new file (MAIN_NEW_*).               */
        /*     objSource : the state (the open and its epoch) it was asked    */
        /*                 in.                                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : posted (reported by Main_ReportCreatedEntry), or the    */
        /*            reason shown.                                           */
        /*--------------------------------------------------------------------*/
        private void Main_PostCommandNewFile(string sFolder, string sName, long lBytes,
            int iKind, STV_clsStateView objSource)
        {
            STG_clsStage objStage;
            NPW_clsPlaceholderWork objWriter;
            RQT_clsRequest objCreateRequest;
            string sRoot;
            int iHow;
            bool bStructured;

            if (Main_objCommandClient == null || objSource == null || objSource.STV_objTarget == null)
            {   /* No command system, or no open to create it in: said, and   */
                /* nothing posted -- there is no other way to the engine.     */
                Main_SetStatus("The command new-file endpoint has no captured filesystem.");
                return;
            }

            /* A private work folder for the stage (Main_NewTempFolder, a     */
            /* new one each time, listed for removal at the program's end     */
            /* once the worker has stopped -- the editor works on that file   */
            /* as its baseline until then).                                   */
            sRoot = Main_NewTempFolder();
            if (sRoot == null)
            {   /* No folder: Main_NewTempFolder has already told the user. */
                return;
            }

            /* The editor it opens in, chosen now from the kind -- not from   */
            /* the empty file's content, which says nothing.  The JSON and    */
            /* INI kinds open the structured editor on a new document.        */
            bStructured = iKind == MAIN_NEW_JSON || iKind == MAIN_NEW_INI;
            if (iKind == MAIN_NEW_HEX)
            {   /* A hex file (zeros): the hex editor. */
                iHow = MAIN_OPEN_HEX;
            }
            else if (iKind == MAIN_NEW_JSON)
            {   /* A JSON file: the structured editor, JSON format. */
                iHow = MAIN_OPEN_EDITOR + VoorheesFormat.VF_JSON;
            }
            else if (iKind == MAIN_NEW_INI)
            {   /* An INI file: the structured editor, INI format. */
                iHow = MAIN_OPEN_EDITOR + VoorheesFormat.VF_INI;
            }
            else
            {   /* Otherwise a text file: the text editor. */
                iHow = MAIN_OPEN_TEXT;
            }

            /* The stage written, then the request made around it.  No        */
            /* stage exists until the factory returns one whole (it deletes   */
            /* a half-written file itself), so only a failure AFTER that --   */
            /* in making the request -- leaves one to release here.           */
            objStage = null;
            try
            {   /* The placeholder's writer, the stage, then the request: an   */
                /* Edit of kind CreateFile, at the folder and name joined      */
                /* as an image path, with the stage, the editor choice and     */
                /* whether it is a new structured document; the folder is      */
                /* listed again afterwards.                                    */
                objWriter = new NPW_clsPlaceholderWork(lBytes);
                objStage = STG_clsStage.STG_WriteSnapshot(sRoot, objWriter.NPW_Write);
                objCreateRequest = CFC_clsCommands.CFC_Create(KND_enmKind.Edit, 0, objSource, objSource.STV_objTarget,
                    new string[] { Main_JoinImagePath(sFolder, sName) }, 0, 0,
                    new EDA_clsEditArgs(EDK_enmEditKind.CreateFile, null, null, false, 0, false, 0, 0,
                        objStage, "", iHow, bStructured), sFolder, "");
            }
            catch (Exception exStage)
            {   /* The stage or the request failed: nothing was handed over. */
                if (objStage != null)
                {   /* A whole stage was made before the request failed: it   */
                    /* is released here.                                      */
                    try
                    {   /* Released; a failure here is logged only, so the   */
                        /* message below stays the first failure's.          */
                        objStage.STG_Release();
                    }
                    catch (Exception exCleanup)
                    {   /* The release failed: logged, nothing retried. */
                        if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                        {   /* Only while the debug log is kept: otherwise the line is never built. */
                            DBGLOG_clsDebugLog.DBGLOG_Write("new-file stage cleanup", exCleanup.ToString());
                        }
                    }
                }

                /* Nothing was created in the image and no editor opened:   */
                /* the reason shown.                                        */
                Main_SetStatus("Preparing " + sName + ": " + exStage.Message);
                return;
            }

            /* Posted.  The request owns the stage from here, a refusal       */
            /* included (the worker then releases it); on success the same    */
            /* file becomes the new editor's baseline.  The completion only   */
            /* reports.                                                       */
            Main_objCommandClient.CLI_Post(objCreateRequest, Main_ReportCreatedEntry);
        }

        /*------------------------------------------------------------------*/
        /* Main_ReportCreatedEntry:                                         */
        /*                                                                  */
        /* New File's completion: "Created <name>." in the status line, or  */
        /* the failure's reason.  The editor was already opened by the      */
        /* worker's sequence; nothing is opened or created here.            */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objRequest : the request (its one path is the new entry).    */
        /*     objResult  : its result.                                     */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the line shown.                                       */
        /*------------------------------------------------------------------*/
        private void Main_ReportCreatedEntry(RQT_clsRequest objRequest, RSL_clsResult objResult)
        {
            string sMessage;
            string sEntry;
            int iSlash;

            /* A failure's own reason by default; a success names the new    */
            /* entry by its name (after its last "/" -- an image path, not   */
            /* a Windows one).                                               */
            sMessage = objResult.RSL_sDetail;
            if (objResult.RSL_iStatus == SYS_clsSystem.SYS_OK)
            {   /* Created: its name. */
                sEntry = objRequest.RQT_lstPaths[0];
                iSlash = sEntry.LastIndexOf('/');
                sMessage = "Created " + sEntry.Substring(iSlash + 1) + ".";
            }

            /* Shown; nothing follows from it. */
            Main_SetStatus(sMessage);
        }

        /* An editor's Save As target. */

        /*------------------------------------------------------------------*/
        /* Main_CanTargetImage:                                             */
        /*                                                                  */
        /* The editors' advice for their Save As menus: whether "into the   */
        /* image" can be offered -- an image open and no Exit agreed.  It   */
        /* is advice only: a save is checked again by the worker against    */
        /* the open it was made for.                                        */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     None.                                                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     bool : true when an image target can be offered.             */
        /*------------------------------------------------------------------*/
        private bool Main_CanTargetImage()
        {
            STV_clsStateView objState;

            /* The state read once, for both tests. */
            objState = Main_objCommandClient.CLI_ReadState();

            /* Open and not exiting.  A queued save is not allowed by this:   */
            /* the worker tests it again when it runs.                        */
            return(objState.STV_enmMode == MOD_enmMode.Open && !objState.STV_bExiting);
        }

        /*------------------------------------------------------------------*/
        /* Main_RequestEditorTarget:                                        */
        /*                                                                  */
        /* An editor's Save As: the target's picker asked for -- a Windows  */
        /* file, or a place in the open image -- as an OpenInput the worker */
        /* admits, and the editor's request kept (Main_dictTargets) for the */
        /* picker to answer.  When the editor's own close question is       */
        /* waiting on a Windows Save As, the request is handed over inside  */
        /* that question's answer instead of posted, since nothing posted   */
        /* could run while the close waits.  No picker opens here.          */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     frmOwner   : the editor asking.                              */
        /*     objCurrent : its descriptor now (where it saves, its id).    */
        /*     bWindows   : true for a Windows file; false for the image.   */
        /*     pfnChosen  : the editor's call for the chosen target -- null */
        /*                  when there is none (refused, cancelled).        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : asked for, or the editor told "none" at once.         */
        /*------------------------------------------------------------------*/
        private void Main_RequestEditorTarget(Form frmOwner, OFD_clsOpenedFile objCurrent,
            bool bWindows, TIN_TargetChosen pfnChosen)
        {
            RQT_clsRequest objRequest;
            STV_clsStateView objState;
            PRS_clsPresentation objClose;
            TIN_clsTargetIntent objIntent;
            INK_enmInputKind enmPurpose;
            string sFolder;
            int iSlash;

            if (frmOwner == null || frmOwner.IsDisposed || objCurrent == null || pfnChosen == null)
            {   /* No live editor (or nothing to answer it with): no   */
                /* picker can be shown for it.                         */
                if (pfnChosen != null)
                {   /* It can still be answered: "none". */
                    pfnChosen(null);
                }

                /* Nothing asked for. */
                return;
            }

            /* The picker's starting folder.  A Windows picker starts where     */
            /* the editor's Windows file is (only when it is one); an image     */
            /* picker starts at the root -- or at the entry's own folder when   */
            /* the editor's entry is in this very open (same range and          */
            /* epoch), never at a path from another filesystem.                 */
            objState = Main_objCommandClient.CLI_ReadState();
            sFolder = "";
            enmPurpose = INK_enmInputKind.HostSaveTarget;
            if (bWindows)
            {   /* A Windows target: its picker. */
                if (objCurrent.OFD_bHostFile)
                {   /* The editor holds a Windows file: start in its folder. */
                    sFolder = Path.GetDirectoryName(objCurrent.OFD_sImagePath);
                }
            }
            else
            {   /* An image target: the image browser, from the root unless   */
                /* the test below finds the entry's own folder usable.        */
                enmPurpose = INK_enmInputKind.ImageSaveTarget;
                sFolder = "/";
                if (!objCurrent.OFD_bHostFile && objState.STV_objTarget != null &&
                    objCurrent.OFD_lEpoch == objState.STV_lEpoch && objState.STV_objTarget.TGT_SameRange(
                    new TGT_clsTarget(objCurrent.OFD_sImageFile, objCurrent.OFD_ullOffset,
                    objCurrent.OFD_ullLength, objCurrent.OFD_sDeviceText, 0, 512)))
                {   /* The entry is in the open filesystem now: its folder   */
                    /* (the part before its last "/").                       */
                    iSlash = objCurrent.OFD_sImagePath.LastIndexOf('/');
                    if (iSlash > 0)
                    {   /* Below the root: that folder.  A file at the root   */
                        /* keeps "/".                                         */
                        sFolder = objCurrent.OFD_sImagePath.Substring(0, iSlash);
                    }
                }
            }

            /* The OpenInput request, from the editor (its id as the origin):   */
            /* the picker's kind and new window id, where the editor saves      */
            /* now, the folder, its name, and its save order (the worker        */
            /* refuses the input if the editor has saved since).  The           */
            /* editor's request is kept under that request, for the picker's    */
            /* renderer to answer (or for a refusal to settle).                 */
            objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.OpenInput, objCurrent.OFD_lEditorId,
                objState, objState.STV_objTarget, null, 0, 0, new INA_clsInputArgs(enmPurpose,
                Main_objCommandClient.CLI_NewWindowId(), objCurrent.OFD_sImagePath, sFolder,
                objCurrent.OFD_sName, objCurrent.OFD_lSaveOrder), "", "");
            objIntent = new TIN_clsTargetIntent(frmOwner,
                objCurrent.OFD_ForSave(objCurrent.OFD_lRevision), pfnChosen);
            Main_dictTargets.Add(objRequest, objIntent);

            /* Is the worker waiting on this editor's close question?  A       */
            /* Windows Save As chosen from that question cannot be posted --   */
            /* it would wait behind the very close that waits for it -- so     */
            /* the request goes back inside the question's answer, and the     */
            /* close's own Windows phase shows the picker.                     */
            objClose = QWN_clsQuestionWindows.QWN_CurrentEditorQuestion(objCurrent.OFD_lEditorId);
            if (bWindows && objClose != null)
            {   /* A close is waiting: answered SelectHostTarget, carrying   */
                /* the request.                                              */
                if (!objClose.PRS_objQuestion.QST_Reply(objClose,
                    new ANS_clsAnswer(ANK_enmAnswerKind.SelectHostTarget, objRequest)))
                {   /* Refused (the display was replaced meanwhile, by an   */
                    /* Exit say): nothing was handed over, so the kept      */
                    /* request is removed and the editor told "none" --     */
                    /* once.                                                */
                    Main_dictTargets.Remove(objRequest);
                    objIntent.TIN_pfnChosen(null);
                }

                /* Handed over (or settled): not posted. */
                return;
            }

            /* An ordinary Save As from the editor's menu: posted, with no   */
            /* completion -- its result comes to Main_RenderTargetResult.    */
            Main_objCommandClient.CLI_Post(objRequest, null);
        }

        /*------------------------------------------------------------------*/
        /* Main_CloseWindowsInput:                                          */
        /*                                                                  */
        /* The Windows phase of an editor's close: the close question was   */
        /* answered "Save As to Windows" (Main_RequestEditorTarget), and    */
        /* the worker, still waiting on that close, now asks for the        */
        /* picker.  The file is chosen, the editor writes to it, and the    */
        /* editor coordinator answers the question when that save is done.  */
        /* A picker cancelled with no file chosen answers it Retry, so the  */
        /* worker asks the editor's close question again; anything else (a  */
        /* failure, no editor) answers it Cancel with the reason -- each    */
        /* once the picker and the write have returned.                     */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objPresentation : the close question's display, its context  */
        /*                       the admitted input.                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the question answered (by the save, Retry or Cancel), */
        /*            or nothing for a display no longer current.           */
        /*------------------------------------------------------------------*/
        private void Main_CloseWindowsInput(PRS_clsPresentation objPresentation)
        {
            QST_clsQuestion objQuestion;
            ICV_clsInputView objInput;
            TIN_clsTargetIntent objIntent;
            OFD_clsOpenedFile objTarget;
            string sFailure;
            bool bNoFileChosen;

            if (objPresentation == null ||
                !objPresentation.PRS_objQuestion.QST_IsCurrent(objPresentation))
            {   /* No display, or no longer current (late): nothing -- a   */
                /* newer display of the question owns the kept request.    */
                return;
            }

            /* The question and its admitted input; the reason a Cancel will   */
            /* give unless something better is learned; and, until the         */
            /* picker says otherwise, no cancelled pick.  The worker waits,    */
            /* unable to be interrupted, until this answers.                   */
            objQuestion = objPresentation.PRS_objQuestion;
            objInput = objQuestion.QST_objContext as ICV_clsInputView;
            sFailure = "Windows Save As did not complete.";
            bNoFileChosen = false;
            try
            {   /* The picker and the editor's write; the finally answers   */
                /* Cancel if the save did not answer.                       */
                if (objInput == null ||
                    objInput.ICV_objArgs.INA_enmKind != INK_enmInputKind.HostSaveTarget ||
                    objInput.ICV_objRequest.RQT_lOrigin != objQuestion.QST_lWindow ||
                    !Main_dictTargets.TryGetValue(objInput.ICV_objRequest, out objIntent))
                {   /* Not a Windows Save As of this editor's, or its kept     */
                    /* request is gone: the reason set, and the finally        */
                    /* answers Cancel -- nothing is rebuilt from the screen.   */
                    sFailure = "The Windows Save As input is no longer available.";
                    return;
                }

                /* The kept request removed BEFORE the picker (a modal loop)   */
                /* or the editor's call, so a late second delivery cannot      */
                /* run it again.  Then the picker, owned by the editor, if     */
                /* the editor is still open.                                   */
                Main_dictTargets.Remove(objInput.ICV_objRequest);
                objTarget = null;
                if (!objIntent.TIN_frmOwner.IsDisposed)
                {   /* The editor is open: the Windows file picked over it,   */
                    /* from the input's folder and name, the input            */
                    /* registered and ended by the picker itself (true).      */
                    objTarget = Main_PickEditorWindowsTarget(objInput, objIntent,
                        objInput.ICV_objArgs.INA_sInitialFolder, objInput.ICV_objArgs.INA_sInitialName, true);
                }

                if (objTarget != null)
                {   /* A file chosen: its save answers THIS display (the   */
                    /* close's Windows phase), not the one the close was   */
                    /* first asked with.                                   */
                    objTarget.OFD_objQuestion = objPresentation;
                }
                else if (!objIntent.TIN_frmOwner.IsDisposed)
                {   /* The picker was shown and cancelled, no file chosen:    */
                    /* the editor's close question is to be asked again, so   */
                    /* the finally answers Retry rather than Cancel.          */
                    bNoFileChosen = true;
                }

                /* The editor told its target (null: none) and writes to it    */
                /* now, on this call.  A successful save is reported once by   */
                /* the coordinator (HostEditorSaved), which answers the        */
                /* question; nothing is posted here for it.                    */
                objIntent.TIN_pfnChosen(objTarget);
            }
            catch (Exception exInput)
            {   /* The picker or the write threw: its message is the   */
                /* reason, logged whole and shown.                     */
                sFailure = exInput.Message;
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("close-owned Windows Save As", exInput.ToString());
                }

                /* The reason shown on the status line. */
                Main_SetStatus(sFailure);
            }
            finally
            {   /* Everything has returned.  A question still unanswered is   */
                /* answered here, so the close never waits for nothing.  (A   */
                /* successful save has already answered it through the        */
                /* coordinator.)                                              */
                if (objQuestion.QST_IsCurrent(objPresentation) && bNoFileChosen)
                {   /* The picker cancelled: Retry with no completed save,       */
                    /* which the worker takes as "ask this editor's close        */
                    /* question again" (Sys_CloseHostTarget) -- the question's   */
                    /* own Cancel is what ends the close.                        */
                    objQuestion.QST_Reply(objPresentation,
                        new ANS_clsAnswer(ANK_enmAnswerKind.Retry, null, ""));
                }
                else if (objQuestion.QST_IsCurrent(objPresentation))
                {   /* No Save As of this editor's, no editor, or a failure:   */
                    /* Cancel with the reason, so the close stops.             */
                    objQuestion.QST_Reply(objPresentation,
                        new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null, sFailure));
                }
            }
        }

        /*------------------------------------------------------------------*/
        /* Main_RenderTargetResult:                                         */
        /*                                                                  */
        /* The result of an editor's Save As OpenInput (posted with no      */
        /* completion, Main_RequestEditorTarget): when the worker refused   */
        /* or failed it, the editor is told "none" and the kept request     */
        /* removed; a success is only taken -- the picker's renderer has    */
        /* already answered the editor.                                     */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objView : the view.                                          */
        /*                                                                  */
        /* Returns:                                                         */
        /*     bool : true for the result of an editor's Save As input;     */
        /*            false for anything else.                              */
        /*------------------------------------------------------------------*/
        private bool Main_RenderTargetResult(VWM_clsView objView)
        {
            TIN_clsTargetIntent objIntent;
            RSL_clsResult objResult;
            INA_clsInputArgs objArgs;

            if (Main_dictTargets == null || objView.VWM_objRequest == null ||
                (objView.VWM_enmKind != VWK_enmViewKind.Result && objView.VWM_enmKind != VWK_enmViewKind.Refused &&
                objView.VWM_enmKind != VWK_enmViewKind.Dropped) || objView.VWM_objRequest.RQT_enmKind != KND_enmKind.OpenInput)
            {   /* Not an OpenInput's end (or no table yet): not this   */
                /* renderer's.                                          */
                return(false);
            }

            /* Only the editors' Save As inputs.  The main window's own   */
            /* inputs (file names, devices) have their own completion.    */
            objArgs = objView.VWM_objRequest.RQT_objPayload as INA_clsInputArgs;
            if (objArgs == null || (objArgs.INA_enmKind != INK_enmInputKind.ImageSaveTarget &&
                objArgs.INA_enmKind != INK_enmInputKind.HostSaveTarget))
            {   /* Another kind of input: not this renderer's. */
                return(false);
            }

            /* The result.  An admitted input's renderer removed the kept    */
            /* request before its picker opened, so on success there is      */
            /* nothing left here -- and a success is never itself a chosen   */
            /* target.                                                       */
            objResult = (RSL_clsResult)objView.VWM_objValue;
            if (objResult.RSL_iStatus != SYS_clsSystem.SYS_OK &&
                Main_dictTargets.TryGetValue(objView.VWM_objRequest, out objIntent))
            {   /* Refused or failed, no picker shown: the kept request   */
                /* removed BEFORE the editor is called (its answer may    */
                /* run UI of its own), the editor told "none", and the    */
                /* reason logged and shown.                               */
                Main_dictTargets.Remove(objView.VWM_objRequest);
                objIntent.TIN_pfnChosen(null);
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("Save As input", objResult.RSL_sDetail);
                }

                /* The reason shown on the status line. */
                Main_SetStatus(objResult.RSL_sDetail);
            }

            /* Taken, a success too -- no other renderer has anything to do   */
            /* with it.                                                       */
            return(true);
        }

        /*------------------------------------------------------------------*/
        /* Main_RenderEditorTarget:                                         */
        /*                                                                  */
        /* An admitted Save As input of an editor's (its request is in      */
        /* Main_dictTargets): the picker shown -- the Windows save dialog   */
        /* or the image browser -- and the editor given the target chosen,  */
        /* or "none".  The editor writes its content only after this, so a  */
        /* failed or cancelled choice changes nothing it holds.             */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objInput : the admitted input.                               */
        /*                                                                  */
        /* Returns:                                                         */
        /*     bool : true when the input was an editor's (answered, chosen */
        /*            or not); false otherwise, for the main window's own   */
        /*            input renderer.                                       */
        /*------------------------------------------------------------------*/
        private bool Main_RenderEditorTarget(ICV_clsInputView objInput)
        {
            TIN_clsTargetIntent objIntent;
            OFD_clsOpenedFile objTarget;

            if (!Main_dictTargets.TryGetValue(objInput.ICV_objRequest, out objIntent))
            {   /* No editor's request kept for it: the main window's own   */
                /* input.                                                   */
                return(false);
            }

            /* The kept request removed BEFORE any picker is made or shown   */
            /* (a modal loop), so neither a failure nor a late second        */
            /* delivery can answer the editor twice -- and so the input's    */
            /* result finds nothing left (Main_RenderTargetResult).  No      */
            /* target yet.                                                   */
            Main_dictTargets.Remove(objInput.ICV_objRequest);
            objTarget = null;
            try
            {   /* The picker, by the input's kind.  A failure leaves no     */
                /* target: nothing is staged and where the editor saves is   */
                /* unchanged.                                                */
                if (objIntent.TIN_frmOwner.IsDisposed)
                {   /* The editor has closed meanwhile: no picker, and the   */
                    /* input ended unused.                                   */
                    Main_EndUnusedInput(objInput);
                }
                else if (objInput.ICV_objArgs.INA_enmKind == INK_enmInputKind.HostSaveTarget)
                {   /* A Windows target: the standard save dialog, from the   */
                    /* input's folder and name, the input registered and      */
                    /* ended by the picker itself (true).                     */
                    objTarget = Main_PickEditorWindowsTarget(objInput, objIntent,
                        objInput.ICV_objArgs.INA_sInitialFolder, objInput.ICV_objArgs.INA_sInitialName, true);
                }
                else
                {   /* An image target: the program's image browser, over   */
                    /* the open the input was admitted for.                 */
                    objTarget = Main_PickEditorImageTarget(objInput, objIntent);
                }
            }
            catch (Exception exInput)
            {   /* The picker threw (after its own clean-up): the input     */
                /* ended (a second end of one already ended is harmless),   */
                /* the failure logged whole and shown.                      */
                Main_EndUnusedInput(objInput);
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("Save As input", exInput.ToString());
                }

                /* The failure shown on the status line. */
                Main_SetStatus(exInput.Message);
            }

            /* Only now is the editor told -- and only now does it take its    */
            /* content's snapshot and save.  It checks for itself that it is   */
            /* still open and still bound as it was.                           */
            objIntent.TIN_pfnChosen(objTarget);

            /* Taken: the input is spent, chosen, cancelled or not shown. */
            return(true);
        }

        /*------------------------------------------------------------------*/
        /* Main_EndUnusedInput:                                             */
        /*                                                                  */
        /* An admitted input ended without its window being shown (the      */
        /* asking window has gone) or after its picker failed: the worker   */
        /* is told InputClosed for its id, so it stops holding work for it. */
        /* A second end of an input already ended is harmless.              */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objInput : the input.                                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : InputClosed posted.                                   */
        /*------------------------------------------------------------------*/
        private void Main_EndUnusedInput(ICV_clsInputView objInput)
        {
            /* InputClosed for the input's id.  Any picker window has   */
            /* already been disposed by its own using block.            */
            Main_objCommandClient.CLI_Post(CFC_clsCommands.CFC_Create(KND_enmKind.InputClosed,
                objInput.ICV_objArgs.INA_lWindow, null, null, null, 0, 0, null, "", ""), null);
        }

        /*------------------------------------------------------------------*/
        /* Main_PickEditorImageTarget:                                      */
        /*                                                                  */
        /* The image browser (ImageSaveForm.cs) shown for an editor's Save  */
        /* As into the image, over the open the input was admitted for, and */
        /* a target descriptor made from the place and name chosen.  The    */
        /* browser's folder listings go through the worker, bound to this   */
        /* input.  A Windows drive path typed in its name box changes the   */
        /* save to Windows: the standard save dialog is shown there         */
        /* instead (Main_PickEditorWindowsTarget), and its file is the      */
        /* target.  Nothing is written here.                                */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objInput  : the admitted input (the open, the editor, the    */
        /*                 window id, the starting folder and name).        */
        /*     objIntent : the editor's request (its window, its            */
        /*                 descriptor).                                     */
        /*                                                                  */
        /* Returns:                                                         */
        /*     OFD_clsOpenedFile : the target chosen, or null (cancelled,   */
        /*                         or the worker asked the browser to       */
        /*                         close).  A failure throws.               */
        /*------------------------------------------------------------------*/
        private OFD_clsOpenedFile Main_PickEditorImageTarget(ICV_clsInputView objInput, TIN_clsTargetIntent objIntent)
        {
            ISV_clsImageSaveForm dlgInput;
            OFD_clsOpenedFile objTarget;
            bool bRegistered;

            /* Not registered yet: if the browser cannot even be made, the    */
            /* caller ends the unused input (Main_RenderEditorTarget); once   */
            /* registered, the finally below ends it.                         */
            bRegistered = false;
            try
            {   /* The browser itself, bound to the client, the open, the      */
                /* editor and this input's id (its listings are requests       */
                /* the worker ties to this input), starting where the input    */
                /* says, with the dialogs' icon list -- and told whether the   */
                /* open is FAT or exFAT (Main_OpenIsFat: the open this input   */
                /* was admitted for, the worker running nothing else           */
                /* meanwhile), so its name box holds FAT's rules too.          */
                dlgInput = new ISV_clsImageSaveForm(Main_fntDialog, Main_objCommandClient,
                    objInput.ICV_objSource, objInput.ICV_objEditor, objInput.ICV_objArgs.INA_lWindow,
                    objInput.ICV_objArgs.INA_sInitialFolder, objInput.ICV_objArgs.INA_sInitialName, Main_imlDialogIcons,
                    Main_OpenIsFat());
                using (dlgInput)
                {   /* Disposed when this block ends, after the modal call has   */
                    /* returned.  Registered (as a picker the worker may ask     */
                    /* to close) before it is shown, then shown over the         */
                    /* editor; then: asked to close by the worker gives no       */
                    /* target.                                                   */
                    Main_objCommandInputs.IWR_RegisterInput(objInput, dlgInput);
                    bRegistered = true;
                    dlgInput.ShowDialog(objIntent.TIN_frmOwner);
                    if (Main_objCommandInputs.IWR_InputCancelled(objInput.ICV_objArgs.INA_lWindow))
                    {   /* No target: the worker's close request wins even   */
                        /* over a Save pressed just before it.               */
                        return(null);
                    }

                    if (dlgInput.ISV_bWindowsAsked)
                    {   /* A Windows drive path was typed in the name box: a   */
                        /* request to change drive, so the save goes to        */
                        /* Windows -- the standard save dialog, starting       */
                        /* there, under this same input (registered with the   */
                        /* browser above and ended by the finally below, so    */
                        /* false: not registered again).  Its target is a      */
                        /* Windows file's, like any Windows Save As.           */
                        objTarget = Main_PickEditorWindowsTarget(objInput, objIntent,
                            dlgInput.ISV_sWindowsFolder, dlgInput.ISV_sWindowsName, false);
                        if (Main_objCommandInputs.IWR_InputCancelled(objInput.ICV_objArgs.INA_lWindow))
                        {   /* The worker asked the input to close while the   */
                            /* Windows dialog was up: no target, as above.     */
                            return(null);
                        }

                        /* The Windows file chosen, or null for a Cancel. */
                        return(objTarget);
                    }

                    if (!dlgInput.ISV_bAccepted)
                    {   /* Cancelled, or closed with no name: no target. */
                        return(null);
                    }

                    /* The target: an image entry at the chosen path and name,   */
                    /* in the input's open (file, epoch, range, device words),   */
                    /* with a work file of its own in the run's work folder      */
                    /* for the editor's snapshot (never the editor's original    */
                    /* read-out, which stays the worker's), and the editor's     */
                    /* command context copied over (OFD_CopyCommandContextTo).   */
                    objTarget = new OFD_clsOpenedFile();
                    objTarget.OFD_bHostFile = false;
                    objTarget.OFD_sImageFile = objInput.ICV_objSource.STV_objTarget.TGT_sPath;
                    objTarget.OFD_sImagePath = dlgInput.ISV_sPath;
                    objTarget.OFD_sName = dlgInput.ISV_sName;
                    objTarget.OFD_sTempFile = Path.Combine(Main_sWorkRoot, Guid.NewGuid().ToString("N") + ".editor-work");
                    objIntent.TIN_objCurrent.OFD_CopyCommandContextTo(objTarget);
                    objTarget.OFD_lEpoch = objInput.ICV_objSource.STV_lEpoch;
                    objTarget.OFD_ullOffset = objInput.ICV_objSource.STV_objTarget.TGT_ullOffset;
                    objTarget.OFD_ullLength = objInput.ICV_objSource.STV_objTarget.TGT_ullLength;
                    objTarget.OFD_sDeviceText = objInput.ICV_objSource.STV_objTarget.TGT_sDeviceText;
                    objTarget.OFD_bDetached = false;

                    /* The target's description only; the save itself is the   */
                    /* editor's, after this returns.                           */
                    return(objTarget);
                }
            }
            finally
            {   /* The browser has been disposed by its using block by now. */
                if (bRegistered)
                {   /* It was registered: its end reported (InputClosed to   */
                    /* the worker).  One never registered has nothing to     */
                    /* end here -- the caller ends that input.               */
                    Main_objCommandInputs.IWR_InputReturned(objInput.ICV_objArgs.INA_lWindow);
                }
            }
        }
        /*------------------------------------------------------------------*/
        /* Main_PickEditorWindowsTarget:                                    */
        /*                                                                  */
        /* The standard Windows save dialog shown for an editor's Save As   */
        /* to Windows, and the file chosen checked against the files the    */
        /* program itself must not overwrite (the open image, its private   */
        /* work files -- SVM_ValidatePath) before a target descriptor is    */
        /* made from it.  Nothing is written here.  It serves two inputs:   */
        /* a Windows Save As of its own (which it registers and ends), and  */
        /* an Image Save As whose name box was given a Windows drive path   */
        /* (Main_PickEditorImageTarget, whose input is registered with its  */
        /* image browser and ended by that function).                       */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objInput  : the admitted input (its id, the state it was     */
        /*                 admitted in).                                    */
        /*     objIntent : the editor's request (its window, its            */
        /*                 descriptor).                                     */
        /*     sFolder   : the folder the dialog starts in.                 */
        /*     sName     : the file name it offers.                         */
        /*     bRegister : true to register the input here (a value input)  */
        /*                 and end it when the dialog has returned; false   */
        /*                 when the caller has registered it already.       */
        /*                                                                  */
        /* Returns:                                                         */
        /*     OFD_clsOpenedFile : the Windows target, or null when the     */
        /*                         dialog was cancelled.  A refused file    */
        /*                         throws (IOException, with the reason).   */
        /*------------------------------------------------------------------*/
        private OFD_clsOpenedFile Main_PickEditorWindowsTarget(ICV_clsInputView objInput, TIN_clsTargetIntent objIntent,
            string sFolder, string sName, bool bRegister)
        {
            OFD_clsOpenedFile objTarget;
            string sSelected;
            string sNormalized;
            string sProblem;
            string sMountedImage;

            if (bRegister)
            {   /* A Windows Save As of its own: registered as a value input   */
                /* (no picker window the worker could ask to close), so the    */
                /* worker holds other work until it returns; ended in the      */
                /* finally.                                                    */
                Main_objCommandInputs.IWR_RegisterInput(objInput, null);
            }

            try
            {   /* The save dialog over the editor, starting where the         */
                /* caller says: it asks before an existing file is replaced,   */
                /* and has its own Cancel.                                     */
                sSelected = WFD_clsWindowsDialogs.WFD_SelectFile(objIntent.TIN_frmOwner,
                    WFP_enmPurpose.EditorSave, "Save " + objIntent.TIN_objCurrent.OFD_sName + " As",
                    sFolder, sName);
                if (sSelected == null)
                {   /* Cancelled: no target, and where the editor saves is   */
                    /* unchanged.                                            */
                    return(null);
                }

                /* The dialog checks only that the name is a valid Windows     */
                /* path; the program's own files are checked here.  The open   */
                /* image, when one was open as the input was admitted, is      */
                /* among them.                                                 */
                sMountedImage = null;
                if (objInput.ICV_objSource.STV_enmMode == MOD_enmMode.Open)
                {   /* An image open: its file is protected too. */
                    sMountedImage = objInput.ICV_objSource.STV_objTarget.TGT_sPath;
                }

                /* The check (EditorSaveMenu.cs), which also normalises the   */
                /* path: a file the program must not overwrite is refused.    */
                if (!SVM_clsSaveMenu.SVM_ValidatePath(objIntent.TIN_objCurrent, sSelected,
                    out sNormalized, out sProblem, true, sMountedImage))
                {   /* Refused: thrown with the reason (the caller shows it),   */
                    /* before anything is written.                              */
                    throw new IOException(sProblem);
                }

                /* The target: a Windows file at the normalised path, worked   */
                /* on in place (its path is also its work file), no image,     */
                /* no range, with the editor's command context copied over.    */
                /* The editor's binding moves to it only when the save         */
                /* succeeds.                                                   */
                objTarget = new OFD_clsOpenedFile();
                objTarget.OFD_bHostFile = true;
                objTarget.OFD_sImageFile = "";
                objTarget.OFD_sImagePath = sNormalized;
                objTarget.OFD_sName = Path.GetFileName(sNormalized);
                objTarget.OFD_sTempFile = sNormalized;
                objIntent.TIN_objCurrent.OFD_CopyCommandContextTo(objTarget);
                objTarget.OFD_lEpoch = 0;
                objTarget.OFD_ullOffset = 0;
                objTarget.OFD_ullLength = 0;
                objTarget.OFD_sDeviceText = "";
                objTarget.OFD_bDetached = false;

                /* The target's description; it holds no work file of the   */
                /* program's to be removed later.                           */
                return(objTarget);
            }
            finally
            {   /* The dialog has returned and been disposed (WFD_SelectFile   */
                /* does both), however this ended.                             */
                if (bRegister)
                {   /* Registered here: the input ended (InputClosed to the    */
                    /* worker).  A caller that registered it ends it itself.   */
                    Main_objCommandInputs.IWR_InputReturned(objInput.ICV_objArgs.INA_lWindow);
                }
            }
        }

        /* The main window's own inputs, image edits and Properties. */

        /*------------------------------------------------------------------*/
        /* Main_PostMainInput:                                              */
        /*                                                                  */
        /* One of the main window's inputs asked for -- a file name, a      */
        /* device choice, an image folder -- as an OpenInput the worker     */
        /* admits (after any listing or inspection it needs) before the     */
        /* dialog is shown (Main_RenderMainInput).  No dialog opens here.   */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     enmPurpose     : which input.                                */
        /*     sFolder        : the folder to start in; "" for a device     */
        /*                      choice.                                     */
        /*     objTarget      : the whole image a Write Device writes;      */
        /*                      null otherwise.                             */
        /*     bUserInitiated : false for the device choice that continues  */
        /*                      a file choice already admitted; optional,   */
        /*                      true.                                       */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : posted (a refusal comes back to the status line).     */
        /*------------------------------------------------------------------*/
        private void Main_PostMainInput(INK_enmInputKind enmPurpose, string sFolder, TGT_clsTarget objTarget,
            bool bUserInitiated = true)
        {
            RQT_clsRequest objRequest;

            /* The OpenInput, from the main window (origin 0), with a new   */
            /* window id of the UI's own for the dialog -- an id, nothing   */
            /* more: the worker's gate decides whether it may open.         */
            objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.OpenInput, 0, Main_objCommandClient.CLI_ReadState(),
                objTarget, null, 0, 0, new INA_clsInputArgs(enmPurpose, Main_objCommandClient.CLI_NewWindowId(),
                "", sFolder, ""), "", "");
            Main_objCommandClient.CLI_Post(objRequest, Main_ReportCommandFailure, bUserInitiated);
        }

        /*--------------------------------------------------------------------*/
        /* Main_PostImageEdit:                                                */
        /*                                                                    */
        /* An edit in the image -- New Folder, Rename, Delete -- posted for   */
        /* the selection the click captured.  The worker asks what needs      */
        /* asking (the name, the delete's confirmation, write access), does   */
        /* the edit and lists the folder again, as one sequence; nothing is   */
        /* decided here.                                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrPaths : the item named (New Folder's parent, the entry      */
        /*                renamed) or the items deleted.                      */
        /*     objEdit  : the edit's kind and data (values only).             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : posted (a refusal or failure comes back to the status   */
        /*            line), or the reason shown.                             */
        /*--------------------------------------------------------------------*/
        private void Main_PostImageEdit(string[] arrPaths, EDA_clsEditArgs objEdit)
        {
            RQT_clsRequest objRequest;

            if (Main_objCommandClient == null)
            {   /* No command system yet: refused with a reason -- there   */
                /* is no other way to the engine.                          */
                Main_SetStatus("The image edit command endpoint is not initialized.");
                return;
            }

            /* The request, with the state now (so an edit asked for before   */
            /* a close or reopen is refused as stale by the gate) and the     */
            /* pane's folder to list afterwards; posted as fresh user work.   */
            /* Whether the worker is busy is not tested here.                 */
            objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.Edit, 0,
                Main_objCommandClient.CLI_ReadState(), null, arrPaths, 0, 0,
                objEdit, Main_sImageDir, "");
            Main_objCommandClient.CLI_Post(objRequest, Main_ReportCommandFailure, true);
        }

        /*--------------------------------------------------------------------*/
        /* Main_PostPropertyInput:                                            */
        /*                                                                    */
        /* Properties (of an entry) or Filesystem Properties, asked for as    */
        /* an OpenInput: the worker admits it and reads what the dialog       */
        /* needs (a link's target, the filesystem's facts) before the         */
        /* dialog is shown (Main_RenderPropertyInput).                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     enmPurpose : EntryProperties or FilesystemProperties.          */
        /*     sEntry     : the entry's image path; "" for the filesystem's.  */
        /*     objEntry   : the entry's listing row; null for the             */
        /*                  filesystem's.                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : posted (a refusal comes back to the status line), or    */
        /*            the reason shown.                                       */
        /*--------------------------------------------------------------------*/
        private void Main_PostPropertyInput(INK_enmInputKind enmPurpose, string sEntry,
            EPI_clsEntryProperties objEntry)
        {
            string[] arrPaths;
            RQT_clsRequest objRequest;

            if (Main_objCommandClient == null)
            {   /* No command system yet: refused with a reason -- there   */
                /* is no other way to the engine.                          */
                Main_SetStatus("The Properties command endpoint is not initialized.");
                return;
            }

            /* The path: the entry's (already an absolute image path) for an   */
            /* entry's Properties; none for the filesystem's.                  */
            arrPaths = null;
            if (enmPurpose == INK_enmInputKind.EntryProperties)
            {   /* An entry: its one path. */
                arrPaths = new string[] { sEntry };
            }

            /* The OpenInput, with the state now, the path, a new UI window   */
            /* id, the row, and the pane's folder; posted as fresh user       */
            /* work.  The gate checks the open the request carries, not       */
            /* what the pane shows by then.                                   */
            objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.OpenInput, 0,
                Main_objCommandClient.CLI_ReadState(), null, arrPaths, 0, 0,
                new INA_clsInputArgs(enmPurpose, Main_objCommandClient.CLI_NewWindowId(),
                "", "", "", 0, objEntry), Main_sImageDir, "");
            Main_objCommandClient.CLI_Post(objRequest, Main_ReportCommandFailure, true);
        }

        /*------------------------------------------------------------------*/
        /* Main_RenderPropertyInput:                                        */
        /*                                                                  */
        /* An admitted Properties input shown: Filesystem Properties (the   */
        /* facts the worker read, read-only) or an entry's Properties (its  */
        /* row and, for a link, its target), and -- only when the entry's   */
        /* dialog ends with OK and something changed -- an attributes edit  */
        /* posted for the same entry and open.  The worker then asks what   */
        /* it must and makes the change.                                    */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objInput : the admitted input: its state, path, row and the  */
        /*                facts read for it.                                */
        /*                                                                  */
        /* Returns:                                                         */
        /*     bool : true for a Properties input (shown, or ended unused); */
        /*            false for any other.                                  */
        /*------------------------------------------------------------------*/
        private bool Main_RenderPropertyInput(ICV_clsInputView objInput)
        {
            INK_enmInputKind enmPurpose;
            PROP_clsPropertiesForm frmEntry;
            FSP_clsFsPropertiesForm frmFilesystem;
            EVR_clsVolumeResult objVolume;
            ETR_clsTextResult objLink;
            TGT_clsTarget objTarget;
            EEN_stEntry stEntry;
            RQT_clsRequest objEdit;
            string sEntry;
            string sLink;
            uint uiMode;
            uint uiUid;
            uint uiGid;
            bool bAccepted;
            bool bSetMode;
            bool bSetOwner;

            /* Which input: only the two Properties are this renderer's. */
            enmPurpose = objInput.ICV_objArgs.INA_enmKind;
            if (enmPurpose != INK_enmInputKind.EntryProperties && enmPurpose != INK_enmInputKind.FilesystemProperties)
            {   /* Another input: not this renderer's. */
                return(false);
            }

            if (IsDisposed)
            {   /* The main window has gone: nothing to show over, so the   */
                /* input is ended unused.                                   */
                Main_EndUnusedInput(objInput);
                return(true);
            }

            /* Nothing accepted yet: only the entry dialog's OK sets this.    */
            /* The other values are placeholders for the Cancel and failure   */
            /* paths, and never lead to an edit.  The input is registered     */
            /* (a value input) before a dialog is shown, and ended in the     */
            /* finally.                                                       */
            bAccepted = false;
            stEntry = new EEN_stEntry();
            sEntry = "";
            sLink = "";
            uiMode = 0;
            uiUid = 0;
            uiGid = 0;
            Main_objCommandInputs.IWR_RegisterInput(objInput, null);
            try
            {   /* The dialog, modal; it needs no more engine calls -- the    */
                /* facts were read before admission -- and the worker holds   */
                /* other work, an Exit included, until it returns.            */
                if (enmPurpose == INK_enmInputKind.FilesystemProperties)
                {   /* Filesystem Properties: the open the input was       */
                    /* admitted for (never what the main window shows by   */
                    /* then) and the worker's facts, in the read-only      */
                    /* dialog, disposed when the block ends.               */
                    objTarget = objInput.ICV_objSource.STV_objTarget;
                    objVolume = (EVR_clsVolumeResult)objInput.ICV_objPropertyFacts.RSL_objPayload;
                    using (frmFilesystem = new FSP_clsFsPropertiesForm(Main_fntDialog,
                        objTarget.TGT_sPath, objTarget.TGT_sDeviceText, objTarget.TGT_ullOffset,
                        objTarget.TGT_ullLength, objVolume.EVR_stVolume))
                    {   /* The program's icon; shown over the main window.   */
                        /* It only shows (and copies), so nothing is read    */
                        /* back.                                             */
                        frmFilesystem.Icon = this.Icon;
                        frmFilesystem.ShowDialog(this);
                    }
                }
                else
                {   /* An entry's Properties: its row and path as the click   */
                    /* captured them, and a link's target when the worker     */
                    /* read one.                                              */
                    stEntry = objInput.ICV_objArgs.INA_objEntryProperties.EPI_stEntry;
                    sEntry = objInput.ICV_objRequest.RQT_lstPaths[0];
                    if (objInput.ICV_objPropertyFacts != null)
                    {   /* A symbolic link: the target text read for it. */
                        objLink = (ETR_clsTextResult)objInput.ICV_objPropertyFacts.RSL_objPayload;
                        sLink = objLink.ETR_sText;
                    }

                    /* The dialog, with the type and times worded as the       */
                    /* pane's list words them; it checks the mode and owner    */
                    /* boxes itself.  On a FAT or exFAT session it shows the   */
                    /* entry's FAT attributes and offers nothing to change     */
                    /* (FAT has no mode or owner; the engine refuses both),    */
                    /* so its OK never comes and no edit follows.  The input   */
                    /* was admitted for the open the main window shows: the    */
                    /* worker runs nothing else while an input is open.        */
                    /* Disposed when the block ends.                           */
                    using (frmEntry = new PROP_clsPropertiesForm(Main_fntDialog, sEntry, stEntry,
                        Main_TypeText(stEntry.EEN_uiType), sLink, Main_FormatEpoch(stEntry.EEN_uiAtime),
                        Main_FormatEpoch(stEntry.EEN_uiMtime), Main_FormatEpoch(stEntry.EEN_uiCtime),
                        Main_OpenIsFat()))
                    {   /* Shown over the main window; its values read here,   */
                        /* before the dialog is disposed.                      */
                        frmEntry.Icon = this.Icon;
                        frmEntry.ShowDialog(this);
                        if (frmEntry.PROP_bAccepted &&
                            !Main_objCommandInputs.IWR_InputCancelled(objInput.ICV_objArgs.INA_lWindow))
                        {   /* OK (the cancel test is a guard: a value input is   */
                            /* never asked to close): the values taken.           */
                            bAccepted = true;
                            uiMode = frmEntry.PROP_uiMode;
                            uiUid = frmEntry.PROP_uiUid;
                            uiGid = frmEntry.PROP_uiGid;
                        }
                    }
                }
            }
            catch (Exception exProperties)
            {   /* A dialog, a value read or a disposal threw: no edit,   */
                /* even after an OK, and the reason shown.                */
                bAccepted = false;
                Main_SetStatus(exProperties.Message);
            }
            finally
            {   /* The dialog has returned and been disposed: the input   */
                /* ended (InputClosed to the worker).                     */
                Main_objCommandInputs.IWR_InputReturned(objInput.ICV_objArgs.INA_lWindow);
            }

            if (bAccepted && !IsDisposed)
            {   /* OK, and the main window still here: what changed.  Owner   */
                /* and group go together as one change (the edit has one      */
                /* flag for both), even when only one of them differs.        */
                bSetMode = uiMode != stEntry.EEN_uiMode;
                bSetOwner = uiUid != stEntry.EEN_uiUid || uiGid != stEntry.EEN_uiGid;
                if (bSetMode || bSetOwner)
                {   /* Something changed: an attributes edit posted for the   */
                    /* same entry and the open the input was admitted in,     */
                    /* with the pane's folder to list afterwards.  The        */
                    /* worker asks the first-change question if it must,      */
                    /* reopens for writing, sets them and lists the folder.   */
                    /* Posted as a continuation of the admitted input, not    */
                    /* as fresh user work.                                    */
                    objEdit = CFC_clsCommands.CFC_Create(KND_enmKind.Edit, 0,
                        objInput.ICV_objSource, null, new string[] { sEntry }, 0, 0,
                        new EDA_clsEditArgs(EDK_enmEditKind.Attributes, null, null,
                        bSetMode, uiMode, bSetOwner, uiUid, uiGid, null, ""),
                        objInput.ICV_objRequest.RQT_sFolder, "");
                    Main_objCommandClient.CLI_Post(objEdit, Main_ReportCommandFailure);
                }
            }

            /* Taken: the input is spent, whether shown read-only, OK'd   */
            /* with no change, cancelled or failed.                       */
            return(true);
        }

        /*------------------------------------------------------------------*/
        /* Main_OnNewImageResult:                                           */
        /*                                                                  */
        /* The completion of a New Image request (Main_RequestNewImage,     */
        /* MainForm.cs) -- the whole sequence: its dialog asked, the open   */
        /* filesystem left, the file made, its partition window opened.     */
        /* The chosen file's folder is remembered for the next image-file   */
        /* dialog; success refreshes the Windows pane so the new file       */
        /* shows; a cancel or refusal is said in the status line; a real    */
        /* failure gets a message box too.                                  */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objRequest : the request as the worker left it: once the     */
        /*                  dialog was accepted, its payload is null and    */
        /*                  its target names the new file.                  */
        /*     objResult  : the sequence's outcome.                         */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the pane refreshed, or the reason shown.              */
        /*------------------------------------------------------------------*/
        private void Main_OnNewImageResult(RQT_clsRequest objRequest, RSL_clsResult objResult)
        {
            string sImage;
            string sMessage;

            /* The file chosen, when the dialog was accepted: the worker      */
            /* cleared the payload and set the target to it then              */
            /* (Sys_AskNewImageFile, CommandSequences.cs).  Before that the   */
            /* target is whatever the request was posted with -- the open     */
            /* filesystem's, or none -- so it is never named as the file.     */
            sImage = "";
            if (objRequest.RQT_objPayload == null && objRequest.RQT_objTarget != null)
            {   /* Accepted: its path, and its folder remembered for every     */
                /* image-file dialog's next start, whether or not the create   */
                /* then succeeded (Main_RememberImageFolder).                  */
                sImage = objRequest.RQT_objTarget.TGT_sPath;
                Main_RememberImageFolder(sImage);
            }

            if (objResult.RSL_iStatus == SYS_clsSystem.SYS_OK)
            {   /* Made, and its partition window open (the worker made,     */
                /* sized, flushed and closed the file before opening the     */
                /* window over it): the Windows pane's folder listed again   */
                /* so the new file appears if it was made there.  Nothing    */
                /* else to say -- the window itself is the answer.           */
                Main_NavigateHost(Main_sHostDir);
                return;
            }

            /* Any other outcome: its reason in the status line, verbatim.   */
            /* The worker's text already tells the two failures apart --     */
            /* the file not made, or made with only its partition window     */
            /* failing ("The image file was created, but ...",               */
            /* CommandSequences.cs) -- so nothing here claims either.        */
            Main_SetStatus(objResult.RSL_sDetail);
            if (objResult.RSL_iStatus != SYS_clsSystem.SYS_CANCELLED &&
                objResult.RSL_iStatus != SYS_clsSystem.SYS_REFUSED &&
                objResult.RSL_iStatus != SYS_clsSystem.SYS_DROPPED)
            {   /* A real failure -- not a cancel (the user declining to        */
                /* close the open image is one: "The current image remains      */
                /* open."), a refusal by the state gate (a partition or         */
                /* check window open) or a duplicate dropped.  The create       */
                /* step may have run, and the file may exist (made, with only   */
                /* its partition window failing), so the Windows pane's         */
                /* folder is listed again as after a success -- harmless        */
                /* when it was not made.                                        */
                Main_NavigateHost(Main_sHostDir);

                /* Then a message box, as an error: the reason, under the   */
                /* file's name when one was chosen.                         */
                sMessage = objResult.RSL_sDetail;
                if (sImage.Length != 0)
                {   /* A file was chosen: named first. */
                    sMessage = sImage + "\n\n" + objResult.RSL_sDetail;
                }

                MSG_clsMessageForm.MSG_Show(this, Main_fntDialog, sMessage,
                    "New image", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1);
            }
        }

        /*------------------------------------------------------------------*/
        /* Main_OnPlaceClicked:                                             */
        /*                                                                  */
        /* A Places menu item's click: the Windows pane is made the active  */
        /* one and asked to show that place.  The click only posts the      */
        /* request; the pane changes when the worker admits it and          */
        /* Main_ExecutePaneAction runs it.                                  */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objSender : the Places item; its Tag holds the folder's full */
        /*                 path ("" for This PC, the drive list).           */
        /*     evArgs    : unused.                                          */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the navigation posted (or refused, said in the        */
        /*            status line).                                         */
        /*------------------------------------------------------------------*/
        private void Main_OnPlaceClicked(object objSender, EventArgs evArgs)
        {
            /* The Windows pane made active first -- only the focus and the   */
            /* menus' marks change, at once, as for any click in it -- then   */
            /* the navigation posted with the place's path from the item's    */
            /* Tag, captured now so a later change cannot retarget it.        */
            Main_ActivatePane(true);
            Main_RequestPaneAction(HPA_enmHostAction.Navigate, null,
                (string)((ToolStripMenuItem)objSender).Tag);
        }

        /*------------------------------------------------------------------*/
        /* Main_RequestPaneAction:                                          */
        /*                                                                  */
        /* The one route by which a Windows-pane command (and Copy / Cut of */
        /* image items to the clipboard) asks to run.  It checks the        */
        /* selection the action needs, then posts the action as a value     */
        /* INPUT, with everything it will act on captured now; the worker   */
        /* admits it as fresh user work -- refused while other work runs    */
        /* or waits -- and the input is then done in this window            */
        /* (Main_ExecutePaneAction).  Running it as an input is what makes  */
        /* it wait its turn: no engine work and no Exit runs until the      */
        /* local work (a dialog, a shell call, a file move) has returned.   */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     enmAction    : the action (CommandInputs.cs                  */
        /*                    HPA_enmHostAction), carried as the request's  */
        /*                    flags.                                        */
        /*     arrPaths     : the selected items' paths, or null for an     */
        /*                    action on the folder alone.                   */
        /*     sFolder      : the Windows folder the action is in, or the   */
        /*                    folder to go to ("" is the drive list).       */
        /*     sImageFolder : optional, "" by default: the image folder to  */
        /*                    list again -- used only by RefreshBoth.       */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the request posted, or the reason it was not in the   */
        /*            status line.                                          */
        /*------------------------------------------------------------------*/
        private void Main_RequestPaneAction(HPA_enmHostAction enmAction, string[] arrPaths,
            string sFolder, string sImageFolder = "")
        {
            RQT_clsRequest objRequest;

            if (Main_objCommandClient == null)
            {   /* The command system is not made yet (the window still being   */
                /* built): nothing can be admitted, so nothing is done -- the   */
                /* action is not run here either, outside the system.           */
                Main_SetStatus("The file command endpoint is not initialized.");
                return;
            }

            /* The selection each action needs, checked before anything is   */
            /* posted, so a request never reaches the worker without its     */
            /* subject -- and never falls back to "the current folder" or    */
            /* the focused row instead.                                      */
            if ((enmAction == HPA_enmHostAction.CopySelection || enmAction == HPA_enmHostAction.CutSelection) &&
                String.IsNullOrEmpty(sFolder))
            {   /* Copy or Cut in the drive list: a drive is not a file the   */
                /* clipboard can carry, so refused.                           */
                Main_SetStatus("Go into a Windows folder and select one or more items first.");
                return;
            }

            if ((enmAction == HPA_enmHostAction.Rename || enmAction == HPA_enmHostAction.Properties ||
                enmAction == HPA_enmHostAction.OpenShell) && (arrPaths == null || arrPaths.Length != 1))
            {   /* Rename, Properties and Open act on exactly ONE item:     */
                /* none or several selected, refused rather than guessing   */
                /* which one was meant.                                     */
                Main_SetStatus("Select one item in the Windows pane first.");
                return;
            }

            if ((enmAction == HPA_enmHostAction.Delete || enmAction == HPA_enmHostAction.CopySelection ||
                enmAction == HPA_enmHostAction.CutSelection || enmAction == HPA_enmHostAction.ImageCopySelection ||
                enmAction == HPA_enmHostAction.ImageCutSelection) && (arrPaths == null || arrPaths.Length == 0))
            {   /* Delete, and Copy / Cut of either pane's items, with        */
                /* nothing selected: refused -- an empty list must never be   */
                /* taken to mean the folder itself.                           */
                Main_SetStatus("Select one or more items first.");
                return;
            }

            /* The request: an OpenInput of the pane-action kind, under the    */
            /* state as last published (its epoch and target are what an       */
            /* image Copy / Cut is checked against, Sys_Denied), with the      */
            /* paths, the action as its flags, a new id of its own and the     */
            /* folder, and the image folder to list afterwards.  Posted as     */
            /* user-initiated, so a busy worker or a pending Exit refuses it   */
            /* (Main_ReportCommandFailure says why) instead of queueing it.    */
            objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.OpenInput, 0,
                Main_objCommandClient.CLI_ReadState(), null, arrPaths, 0, (uint)enmAction,
                new INA_clsInputArgs(INK_enmInputKind.HostPaneAction,
                    Main_objCommandClient.CLI_NewWindowId(), "", sFolder, ""),
                sImageFolder, "");
            Main_objCommandClient.CLI_Post(objRequest, Main_ReportCommandFailure, true);

            /* The buttons repainted at once: the request now waits in the     */
            /* queue, which SYS_CanAcceptUserWork reads directly, so the       */
            /* commands grey before the worker publishes any new state and a   */
            /* second click is not offered meanwhile (Main_UpdateToolbar       */
            /* ends in Main_UpdateCommandsMenu, whose                          */
            /* Main_ApplyCommandAvailability applies that test).               */
            Main_UpdateToolbar();
        }

        /*------------------------------------------------------------------*/
        /* Main_ExecutePaneAction:                                          */
        /*                                                                  */
        /* Run an admitted pane action (Main_RequestPaneAction) here, on    */
        /* the UI thread, over exactly what was captured at the click: the  */
        /* navigations, the folder chooser, Open, New Folder, Rename,       */
        /* Delete, Paste, Properties and Copy / Cut to the clipboard.  The  */
        /* Windows-side work itself is MainForm.cs's own (Main_Host...);    */
        /* this only chooses it and hands it the captured values.  It runs  */
        /* inside the input's life (Main_RenderLocalFileInput), so the      */
        /* worker starts nothing else until it has returned.                */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objInput : the admitted input: the request (action, paths,   */
        /*                image folder), the folder, and the state it was   */
        /*                admitted in.                                      */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the action done, or its failure shown by the helper   */
        /*            it called; an exception is the caller's to report.    */
        /*------------------------------------------------------------------*/
        private void Main_ExecutePaneAction(ICV_clsInputView objInput)
        {
            RQT_clsRequest objRequest;
            HPA_enmHostAction enmAction;
            string sFolder;
            string sPath;
            DataObject datClip;
            System.Text.StringBuilder sbPaths;
            FolderBrowserDialog dlgFolder;
            bool bCut;
            int i;

            /* The captured values: the request, its action (the flags) and   */
            /* the folder, all as they were at the click -- the pane's        */
            /* selection or path box now plays no part, so nothing that       */
            /* changed while the request waited can retarget it.              */
            objRequest = objInput.ICV_objRequest;
            enmAction = (HPA_enmHostAction)objRequest.RQT_uiFlags;
            sFolder = objInput.ICV_objArgs.INA_sInitialFolder;
            switch (enmAction)
            {   /* One action per case; every HPA_enmHostAction has one. */
                case HPA_enmHostAction.NavigateBack:
                    /* Back: the captured destination is taken off the Back   */
                    /* history only if it still heads it, so a history that   */
                    /* changed between the click and now never loses some     */
                    /* other entry in its place.  What could change it is     */
                    /* not established: a second Back is refused while the    */
                    /* first waits (user work is never queued).               */
                    if (Main_lstHostBack.Count != 0 &&
                        Main_lstHostBack[Main_lstHostBack.Count - 1] == sFolder)
                    {   /* Still the newest Back entry: taken off. */
                        Main_lstHostBack.RemoveAt(Main_lstHostBack.Count - 1);
                    }

                    /* Then gone to, marked as Back's (Main_bHostBackNav), so   */
                    /* the folder left is not pushed onto the history the       */
                    /* listing would otherwise add it to (Main_NavigateHost     */
                    /* clears the mark once it has been read).                  */
                    Main_bHostBackNav = true;
                    Main_NavigateHost(sFolder);
                    break;
                case HPA_enmHostAction.Navigate:
                    /* Go to the folder, the folder left going onto the Back   */
                    /* history as for any navigation.                          */
                    Main_NavigateHost(sFolder);
                    break;
                case HPA_enmHostAction.RefreshBoth:
                    /* Refresh: the Windows folder listed again here (local   */
                    /* work), then the image folder too when the input was    */
                    /* admitted with a filesystem open.                       */
                    Main_NavigateHost(sFolder);
                    if (objInput.ICV_objSource.STV_enmMode == MOD_enmMode.Open)
                    {   /* A filesystem open: its listing posted as this        */
                        /* action's continuation (false: not fresh user work,   */
                        /* so the input's own hold does not refuse it).  With   */
                        /* nothing open there is nothing to list, and a         */
                        /* refresh never opens anything.                        */
                        Main_NavigateImage(objRequest.RQT_sFolder, false);
                    }
                    break;
                case HPA_enmHostAction.PickFolder:
                    /* The Windows folder chooser, modal here: the input     */
                    /* lasts until it has returned and been disposed (the    */
                    /* using block's end), so nothing else runs meanwhile.   */
                    using (dlgFolder = new FolderBrowserDialog())
                    {   /* Its prompt and a New Folder button, starting in the   */
                        /* pane's folder; the dialog is disposed as the block    */
                        /* ends.                                                 */
                        dlgFolder.Description = "Folder to show in the Windows pane:";
                        dlgFolder.ShowNewFolderButton = true;
                        if (sFolder.Length != 0)
                        {   /* A folder to start in; the drive list ("") has   */
                            /* none, and the chooser keeps its own default.    */
                            dlgFolder.SelectedPath = sFolder;
                        }

                        if (dlgFolder.ShowDialog(this) == DialogResult.OK)
                        {   /* A folder chosen: the pane goes there.  A Cancel   */
                            /* leaves the pane as it is.                         */
                            Main_NavigateHost(dlgFolder.SelectedPath);
                        }
                    }
                    break;
                case HPA_enmHostAction.OpenShell:
                    /* Open: the one captured file handed to Windows, which    */
                    /* starts the program associated with that kind of file    */
                    /* (Process.Start of a path, as documented); a file with   */
                    /* none fails to start and is reported below.              */
                    sPath = objRequest.RQT_lstPaths[0];
                    try
                    {   /* The shell's open of the file. */
                        System.Diagnostics.Process.Start(sPath);
                    }
                    catch (Exception exOpen)
                    {   /* No program for it, or the start failed: a      */
                        /* message naming the file with Windows' reason   */
                        /* (Main_ShowHostError also logs it).             */
                        Main_ShowHostError("Opening " + sPath, exOpen);
                    }
                    break;
                case HPA_enmHostAction.NewFolder:
                    /* New Folder: the name asked and the folder made in the   */
                    /* captured folder (Main_HostNewFolder).                   */
                    Main_HostNewFolder(sFolder);
                    break;
                case HPA_enmHostAction.Rename:
                    /* Rename of the one captured item, in its folder          */
                    /* (Main_HostRename, which also handles a change of case   */
                    /* alone).                                                 */
                    Main_HostRename(objRequest.RQT_lstPaths[0], sFolder);
                    break;
                case HPA_enmHostAction.Delete:
                    /* Delete: the captured items to the Recycle Bin, one by   */
                    /* one, stopping at the first Windows refuses              */
                    /* (Main_HostDelete).                                      */
                    Main_HostDelete(objRequest.RQT_lstPaths, sFolder);
                    break;
                case HPA_enmHostAction.PasteCopy:
                case HPA_enmHostAction.PasteMove:
                    /* Paste: the captured clipboard items copied, or moved   */
                    /* when they were cut, into the captured folder by the    */
                    /* shell, with Windows' own progress and questions        */
                    /* (Main_HostPaste).                                      */
                    Main_HostPaste(objRequest.RQT_lstPaths,
                        enmAction == HPA_enmHostAction.PasteMove, sFolder);
                    break;
                case HPA_enmHostAction.Properties:
                    /* Properties: Windows' own Properties sheet for the one   */
                    /* captured item (ShellIcons.cs SHI_ShowProperties).       */
                    sPath = objRequest.RQT_lstPaths[0];
                    if (!SHI_ShellIcons.SHI_ShowProperties(this.Handle, sPath))
                    {   /* The shell could not show it: said in the status   */
                        /* line, naming the item.                            */
                        Main_SetStatus("Windows could not show the properties of " + sPath);
                    }
                    break;
                case HPA_enmHostAction.CopySelection:
                case HPA_enmHostAction.CutSelection:
                case HPA_enmHostAction.ImageCopySelection:
                case HPA_enmHostAction.ImageCutSelection:
                    /* Copy or Cut to the clipboard, of Windows items or of    */
                    /* image items.  Nothing is moved or removed here: a cut   */
                    /* is only MARKED as one, and the items move when a        */
                    /* paste reads that mark.                                  */
                    bCut = enmAction == HPA_enmHostAction.CutSelection ||
                        enmAction == HPA_enmHostAction.ImageCutSelection;
                    datClip = new DataObject();
                    if (enmAction == HPA_enmHostAction.ImageCopySelection ||
                        enmAction == HPA_enmHostAction.ImageCutSelection)
                    {   /* Image items: the program's own format, which no    */
                        /* other program reads.  Its text is the image's      */
                        /* path, then one entry path per line, a folder's     */
                        /* ending in "/" as the pane captured it -- the       */
                        /* shape the image paste splits it back into, which   */
                        /* also refuses it unless the first line names the    */
                        /* open image (MainForm.cs).                          */
                        sbPaths = new System.Text.StringBuilder();
                        sbPaths.Append(objInput.ICV_objSource.STV_objTarget.TGT_sPath);
                        for (i = 0; i < objRequest.RQT_lstPaths.Count; i++)
                        {   /* One captured image path per iteration, on a   */
                            /* line of its own.                              */
                            sbPaths.Append('\n');
                            sbPaths.Append(objRequest.RQT_lstPaths[i]);
                        }

                        /* The text, and beside it the key of the open these     */
                        /* items belong to -- taken from the state the input     */
                        /* was admitted in, which the gate has just matched to   */
                        /* the open filesystem (Sys_Denied).  A paste refuses    */
                        /* items whose key no longer matches: the image since    */
                        /* closed or reopened, or another run of the program     */
                        /* (CommandClipboard.cs CLP_SourceRange).                */
                        datClip.SetData(MAIN_DRAG_IMAGE_FORMAT, sbPaths.ToString());
                        datClip.SetData(MAIN_COMMAND_SOURCE_FORMAT,
                            Main_objCommandClipboard.CLP_SourceRange(objInput.ICV_objSource));
                    }
                    else
                    {   /* Windows items: the shell's file-list format, so    */
                        /* Explorer can paste them too.  It takes an array,   */
                        /* not the request's list.                            */
                        datClip.SetData(DataFormats.FileDrop, objRequest.RQT_lstPaths.ToArray());
                    }

                    /* The copy-or-move mark, in the shell's own format, so    */
                    /* a cut is a move when pasted here or in Explorer; then   */
                    /* the whole object onto the clipboard, kept there after   */
                    /* this program ends (true), and the count said.           */
                    datClip.SetData(MAIN_CLIP_EFFECT_FORMAT, Main_NewEffectStream(bCut));
                    Clipboard.SetDataObject(datClip, true);
                    Main_SetStatus(objRequest.RQT_lstPaths.Count + " item(s) " +
                        (bCut ? "cut" : "copied") + " to the clipboard.");
                    break;
                default:
                    /* A guard: the gate refuses any action number that       */
                    /* HPA_enmHostAction does not name (Sys_Denied), and      */
                    /* every name has its case above.  Thrown rather than     */
                    /* guessed at, so no other operation runs in its place;   */
                    /* the caller shows the message.                          */
                    throw new InvalidOperationException("Unknown admitted pane action.");
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main_RenderLocalFileInput:                                         */
        /*                                                                    */
        /* The admitted inputs whose work is done here in the window, on      */
        /* Windows files or in its own dialogs: a pane action                 */
        /* (Main_ExecutePaneAction), opening a Windows file in an editor,     */
        /* and New File in either pane (Main_CollectNewFile).  The input      */
        /* is registered for the whole of that work and ended only when it    */
        /* has returned, so the worker runs nothing else -- an Exit           */
        /* included -- in between.                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objInput : the admitted input: its kind (HostPaneAction,       */
        /*                HostOpenEntry, ImageNewFile or HostNewFile) and     */
        /*                what was captured for it.                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true -- the input is spent, whether the work was        */
        /*            done, declined or failed.                               */
        /*--------------------------------------------------------------------*/
        private bool Main_RenderLocalFileInput(ICV_clsInputView objInput)
        {
            if (IsDisposed)
            {   /* The main window has gone: no dialog to show and no pane   */
                /* to act in, so the input is ended unused.                  */
                Main_EndUnusedInput(objInput);
                return(true);
            }

            /* The input registered (null: a value input, with no picker of    */
            /* its own) BEFORE any of its work starts, so it is held for all   */
            /* of it -- the questions it asks and the files it touches --      */
            /* and released only in the finally below.                         */
            Main_objCommandInputs.IWR_RegisterInput(objInput, null);
            try
            {   /* The work, chosen by the input's kind.  Each piece is the   */
                /* window's own Windows-side code; what runs as an input is   */
                /* only when it may start and how long it holds the worker.   */
                if (objInput.ICV_objArgs.INA_enmKind == INK_enmInputKind.HostPaneAction)
                {   /* A Windows-pane command, or Copy / Cut of image items. */
                    Main_ExecutePaneAction(objInput);
                }
                else if (objInput.ICV_objArgs.INA_enmKind == INK_enmInputKind.HostOpenEntry)
                {   /* A Windows file opened in an editor: the one captured   */
                    /* path and the viewer chosen (the request's flags,       */
                    /* VWH_TEXT or VWH_HEX), handed to the same opener the    */
                    /* worker's views use, which makes and registers the      */
                    /* editor (Main_ShowCommandHostViewer).                   */
                    Main_ShowCommandHostViewer(new HVW_clsHostViewerView(
                        objInput.ICV_objRequest.RQT_lstPaths[0], objInput.ICV_objRequest.RQT_uiFlags));
                }
                else
                {   /* New File, in the image or in Windows: its name (and   */
                    /* a hex file's size) asked, then made with what was     */
                    /* captured for it (Main_CollectNewFile).                */
                    Main_CollectNewFile(objInput);
                }
            }
            catch (Exception exInput)
            {   /* Any of them threw (a clipboard held open by another      */
                /* program, an unexpected file error): its message in the   */
                /* status line, and nothing is posted after it.             */
                Main_SetStatus(exInput.Message);
            }
            finally
            {   /* Every dialog has returned and every file call ended, by   */
                /* success or by throw: only now is the input ended          */
                /* (InputClosed to the worker), releasing it.                */
                Main_objCommandInputs.IWR_InputReturned(objInput.ICV_objArgs.INA_lWindow);
            }

            /* Taken: the input is spent, whether the work was done, the   */
            /* user declined it or it failed.                              */
            return(true);
        }

        /*------------------------------------------------------------------*/
        /* Main_RenderMainInput:                                            */
        /*                                                                  */
        /* The main window's admitted inputs, routed to the code that shows */
        /* each: the image-folder picker, the local-file inputs,            */
        /* the Properties dialogs, and -- here -- the image-file choices of */
        /* Open and Check.  For those it shows the picker, then             */
        /* acts on what was chosen only AFTER the picker has returned and   */
        /* its input has ended, by posting the next request.  Nothing is    */
        /* read or written here.                                            */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objInput : the admitted input.                               */
        /*                                                                  */
        /* Returns:                                                         */
        /*     bool : true when the input was the main window's and is now  */
        /*            spent (chosen, cancelled or failed); false when it is */
        /*            not one this function shows.                          */
        /*------------------------------------------------------------------*/
        private bool Main_RenderMainInput(ICV_clsInputView objInput)
        {
            INK_enmInputKind enmPurpose;
            string sFile;
            bool bCheck;

            if (objInput.ICV_objRequest.RQT_lOrigin != 0)
            {   /* Asked for by another window (its origin is that window's   */
                /* id), such as an editor's Save As target: not the main      */
                /* window's, so left to the renderer that owns it.            */
                return(false);
            }

            /* The input's kind decides which code shows it.  Each input is   */
            /* published once and reaches here once, so the kind alone        */
            /* routes it -- nothing needs to remember which inputs are        */
            /* expected.                                                      */
            enmPurpose = objInput.ICV_objArgs.INA_enmKind;
            if (enmPurpose == INK_enmInputKind.ImageFolder)
            {   /* The image-folder picker (Main_RenderMainFolderInput). */
                return(Main_RenderMainFolderInput(objInput));
            }

            if (enmPurpose == INK_enmInputKind.ImageNewFile || enmPurpose == INK_enmInputKind.HostNewFile ||
                enmPurpose == INK_enmInputKind.HostOpenEntry || enmPurpose == INK_enmInputKind.HostPaneAction)
            {   /* Work done here on Windows files or in the window's own    */
                /* dialogs: New File, opening a Windows file in an editor,   */
                /* a pane action (Main_RenderLocalFileInput).                */
                return(Main_RenderLocalFileInput(objInput));
            }

            if (Main_RenderPropertyInput(objInput))
            {   /* A Properties dialog (an entry's or the filesystem's),   */
                /* shown with the facts the worker read for it.            */
                return(true);
            }

            if (enmPurpose != INK_enmInputKind.HostOpenImageFile && enmPurpose != INK_enmInputKind.HostCheckImageFile)
            {   /* None of the image-file choices below either: not one   */
                /* this function shows.                                   */
                return(false);
            }

            /* Nothing chosen yet: the file stays null until its picker has      */
            /* returned with a choice, so a cancel or a failure leaves nothing   */
            /* for the code after the try to act on.  Check is told apart        */
            /* once, here.                                                       */
            sFile = null;
            bCheck = enmPurpose == INK_enmInputKind.HostCheckImageFile;
            try
            {   /* The picker shown; a failure in it is caught below, the   */
                /* input ended and the reason shown, with nothing posted.   */
                if (IsDisposed)
                {   /* The main window has gone: nothing shown, and the      */
                    /* worker told the input is closed so it holds nothing   */
                    /* back for it.                                          */
                    Main_objCommandClient.CLI_Post(CFC_clsCommands.CFC_Create(KND_enmKind.InputClosed,
                        objInput.ICV_objArgs.INA_lWindow, null, null, null, 0, 0, null, "", ""), null);
                }
                else
                {   /* The picker for the input's kind.  Each one registers   */
                    /* the input while it is shown and ends it as it          */
                    /* returns.                                               */
                    switch (enmPurpose)
                    {   /* One kind of picker per case. */
                        case INK_enmInputKind.HostOpenImageFile:
                        case INK_enmInputKind.HostCheckImageFile:
                            /* An image file to open or check: Windows' file   */
                            /* dialog, with the caption and filter for the     */
                            /* purpose (Main_PickMainFilename).  It reads      */
                            /* nothing.                                        */
                            sFile = Main_PickMainFilename(objInput);
                            break;
                    }
                }
            }
            catch (Exception exInput)
            {   /* The picker, or the guard above, threw.  The input is     */
                /* ended only once the registry confirms none of its        */
                /* windows is left (CLI_FailInput): telling the worker it   */
                /* closed while a dialog still stood would let other work   */
                /* start under it.                                          */
                if (Main_objCommandClient.CLI_FailInput(objInput))
                {   /* None left: InputClosed to the worker. */
                    Main_objCommandClient.CLI_Post(CFC_clsCommands.CFC_Create(KND_enmKind.InputClosed,
                        objInput.ICV_objArgs.INA_lWindow, null, null, null, 0, 0, null, "", ""), null);
                }

                /* The reason in the status line; nothing chosen is acted on. */
                Main_SetStatus(exInput.Message);

                /* Taken: the input is spent, though it failed. */
                return(true);
            }

            if (IsDisposed)
            {   /* The main window went while the picker was up: whatever   */
                /* was chosen is dropped, as there is no window left to     */
                /* run it in.                                               */
                return(true);
            }

            /* The picker has returned and its input has ended (its own      */
            /* finally); only now is what was chosen acted on, by posting    */
            /* the next request or input -- the worker does every read and   */
            /* write that follows.                                           */
            if (sFile != null)
            {   /* An image file was chosen to open or check: its folder      */
                /* remembered for the next image-file dialog, then the open   */
                /* (or the check) posted for it, which finds out for itself   */
                /* whether the file is a filesystem or a whole disk           */
                /* (Main_OpenImageFile).                                      */
                Main_RememberImageFolder(sFile);
                Main_OpenImageFile(sFile, bCheck);
            }

            /* Taken: the input is spent, whether something was chosen and   */
            /* posted or the picker was cancelled.                           */
            return(true);
        }

        /*------------------------------------------------------------------*/
        /* Main_RenderMainFolderInput:                                      */
        /*                                                                  */
        /* The image-folder picker (ImageFolderForm.cs) over the open       */
        /* filesystem; a folder chosen with OK becomes the image pane's     */
        /* next listing.  The picker reads its tree through the command     */
        /* client, under the state the input was admitted in, so it can     */
        /* only ever browse that open.                                      */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objInput : the admitted input: the state it was admitted in  */
        /*                (the open to browse), its id and the folder to    */
        /*                start in.                                         */
        /*                                                                  */
        /* Returns:                                                         */
        /*     bool : true -- the input is spent, chosen, cancelled or      */
        /*            failed.                                               */
        /*------------------------------------------------------------------*/
        private bool Main_RenderMainFolderInput(ICV_clsInputView objInput)
        {
            IFP_clsImageFolderForm dlgInput;
            RQT_clsRequest objRequest;
            string sChosen;
            bool bRegistered;

            if (IsDisposed)
            {   /* The main window has gone: no picker made, and the input   */
                /* ended unused.                                             */
                Main_EndUnusedInput(objInput);
                return(true);
            }

            /* Nothing chosen yet (only an OK sets it), and the input not     */
            /* registered yet -- the flag is what tells the finally whether   */
            /* the picker was registered (so its end is reported) or failed   */
            /* before that (so the input is ended unused).                    */
            sChosen = null;
            bRegistered = false;
            try
            {   /* The picker built over the admitted state, with the       */
                /* input's id (its own listings carry it as their origin,   */
                /* ImageFolderForm.cs), the starting folder and the         */
                /* dialogs' icon list.                                      */
                dlgInput = new IFP_clsImageFolderForm(Main_fntDialog, Main_objCommandClient,
                    objInput.ICV_objSource, objInput.ICV_objArgs.INA_lWindow,
                    objInput.ICV_objArgs.INA_sInitialFolder, Main_imlDialogIcons,
                    EIC_EntryIcons.EIC_ICON_FOLDER);
                using (dlgInput)
                {   /* Disposed when the block ends -- before the finally      */
                    /* reports the input's end.  Registered WITH the picker,   */
                    /* so the registry can close it when the worker asks       */
                    /* (its InputClose question, IWR_AskInputClose), then      */
                    /* shown modal.                                            */
                    dlgInput.Icon = this.Icon;
                    Main_objCommandInputs.IWR_RegisterInput(objInput, dlgInput);
                    bRegistered = true;
                    dlgInput.ShowDialog(this);
                    if (!Main_objCommandInputs.IWR_InputCancelled(objInput.ICV_objArgs.INA_lWindow) &&
                        dlgInput.IFP_bAccepted)
                    {   /* OK, and the picker was not closed by the worker:   */
                        /* the folder taken.  A picker the worker closed is   */
                        /* never acted on, even if OK had been pressed.       */
                        sChosen = dlgInput.IFP_sPath;
                    }
                }
            }
            catch (Exception exInput)
            {   /* Building or showing the picker threw: nothing chosen,   */
                /* and the reason shown.                                   */
                sChosen = null;
                Main_SetStatus(exInput.Message);
            }
            finally
            {   /* The picker has returned and been disposed (or was never   */
                /* made): the input ended, the right way for each case.      */
                if (bRegistered)
                {   /* Registered: its end reported to the registry. */
                    Main_objCommandInputs.IWR_InputReturned(objInput.ICV_objArgs.INA_lWindow);
                }
                else
                {   /* Failed before it was registered: ended unused. */
                    Main_EndUnusedInput(objInput);
                }
            }

            if (sChosen != null && !IsDisposed)
            {   /* A folder chosen, and the main window still here: the        */
                /* image pane's listing of it posted now, after the picker     */
                /* has gone.  Under the state the picker browsed, not the      */
                /* one now -- if the open has since closed or changed, the     */
                /* worker refuses the listing rather than reading the folder   */
                /* in another filesystem.  Not a Back (the folder left goes    */
                /* onto the history), in the pane's own navigation stream,     */
                /* and recorded as the latest navigation, which is the only    */
                /* one Main_OnNavigationResult (MainForm.cs) settles.          */
                Main_bImageBackNav = false;
                objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.ListFolder, 0,
                    objInput.ICV_objSource, null, new string[] { sChosen }, 0, 0,
                    null, sChosen, "main-navigation");
                Main_objLatestNavigation = objRequest;
                Main_objCommandClient.CLI_Post(objRequest, Main_OnNavigationResult);
            }

            /* Taken: the input is spent, whether a folder was chosen, the   */
            /* picker cancelled or a failure shown.                          */
            return(true);
        }

        /*------------------------------------------------------------------*/
        /* Main_PickMainFilename:                                           */
        /*                                                                  */
        /* The image file for Open Image or Check Filesystem: Windows' own  */
        /* open dialog, with the caption and filter for the purpose.  It    */
        /* only returns the name; the worker inspects the file later.       */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objInput : the admitted input: its kind (the purpose), its   */
        /*                id and the folder to start in.                    */
        /*                                                                  */
        /* Returns:                                                         */
        /*     string : the file chosen, or null for a Cancel; the input    */
        /*              has ended by the time the caller has it.            */
        /*------------------------------------------------------------------*/
        private string Main_PickMainFilename(ICV_clsInputView objInput)
        {
            WFP_enmPurpose enmPurpose;
            string sTitle;

            /* The dialog's purpose (its filter) and caption: Open's unless   */
            /* the input is Check's.                                          */
            enmPurpose = WFP_enmPurpose.OpenImage;
            sTitle = "Open an ext2 / ext3 / ext4, FAT or exFAT image file, or a disk image that holds partitions";
            if (objInput.ICV_objArgs.INA_enmKind == INK_enmInputKind.HostCheckImageFile)
            {   /* Check Filesystem: Open's filter, its own caption. */
                sTitle = "Check the filesystem in an image file, or in a disk image that holds partitions";
            }

            /* The input registered (null: a value input) for as long as the   */
            /* dialog stands, so the worker runs nothing else meanwhile.       */
            Main_objCommandInputs.IWR_RegisterInput(objInput, null);
            try
            {   /* The dialog, modal over the main window, starting in the   */
                /* input's folder, with no name offered; its answer is       */
                /* returned once the finally below has ended the input.      */
                return(WFD_clsWindowsDialogs.WFD_SelectFile(this, enmPurpose, sTitle,
                    objInput.ICV_objArgs.INA_sInitialFolder, ""));
            }
            finally
            {   /* The dialog has returned and been disposed (inside           */
                /* WFD_SelectFile), or it threw: the input ended either way.   */
                Main_objCommandInputs.IWR_InputReturned(objInput.ICV_objArgs.INA_lWindow);
            }
        }

        /*------------------------------------------------------------------*/
        /* Main_PostDeviceRead:                                             */
        /*                                                                  */
        /* Save Device to Image File posted to the worker as ONE ReadDevice */
        /* request with no target, carrying only the folder its save dialog */
        /* starts in -- the shared image folder as it is now.  Its sequence */
        /* lists the disks, asks the device, how much of it and the file,   */
        /* and copies (CommandRawSequence.cs, Sys_AskDeviceRead); the       */
        /* outcome comes back to Main_OnDeviceReadResult.                   */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     bUserInitiated : true for the menu (fresh user work, refused */
        /*                      while the worker is busy); false for the    */
        /*                      start-up's command after a restart as       */
        /*                      Administrator (the start-up's               */
        /*                      continuation, not refused for its own       */
        /*                      requests still running).                    */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : posted, or the reason it could not be made in the     */
        /*            status line.                                          */
        /*------------------------------------------------------------------*/
        private void Main_PostDeviceRead(bool bUserInitiated)
        {
            RQT_clsRequest objRequest;

            if (Main_objCommandClient == null)
            {   /* A guard: the command system is not made, so nothing can   */
                /* be posted.                                                */
                Main_SetStatus("The device-input command endpoint is not initialized.");
                return;
            }

            try
            {   /* The request, under the state as last published; the     */
                /* factory checks the kind against its arguments' record   */
                /* and throws for a mismatch.                              */
                objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.ReadDevice, 0, Main_objCommandClient.CLI_ReadState(),
                    null, null, 0, 0, new RDQ_clsDeviceReadQuestion(null, null, Main_ImageFolder("")), "", "");
            }
            catch (ArgumentException exInput)
            {   /* Refused by the factory: said, and nothing posted. */
                Main_SetStatus(exInput.Message);
                return;
            }

            /* Posted as the caller says: fresh user work from the menu, a   */
            /* continuation from the start-up.                               */
            Main_objCommandClient.CLI_Post(objRequest, Main_OnDeviceReadResult, bUserInitiated);
        }

        /* Raw imaging intent and reports */

        /*------------------------------------------------------------------*/
        /* MAIN_CommandRawTarget:                                           */
        /*                                                                  */
        /* A device row made into the whole-device target of a raw copy     */
        /* (Read Device, Write Device): its path, size and sector size, and */
        /* a description -- the one Write Device's question names the       */
        /* device by (CommandQuestionPresenter.cs).  Copied values only, so */
        /* the row and its list need not outlive the picker; the worker     */
        /* checks the device itself when it runs.  Called by the            */
        /* presenter's Read Device question (Qpr_DeviceRead) and by         */
        /* DCH_clsDeviceChoice (CommandInputs.cs) for Write Device's        */
        /* choice.                                                          */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objDisk : the chosen device row; only read here.             */
        /*                                                                  */
        /* Returns:                                                         */
        /*     TGT_clsTarget : the whole device (offset 0, length 0 for the */
        /*                     rest).                                       */
        /*------------------------------------------------------------------*/
        internal static TGT_clsTarget MAIN_CommandRawTarget(DEV_clsDisk objDisk)
        {
            string sDescription;
            string sModel;

            /* The device's name: the model text as the device picker spells   */
            /* it (PartitionForm.cs DVF_ModelText: the padding at either end   */
            /* taken off and every run of spaces made one), so the picker      */
            /* and the write question name the device one way -- or, for a     */
            /* device that reports none, "PhysicalDriveN", as the browse       */
            /* choice names it (CommandInputs.cs), so the description never    */
            /* begins with a bare comma.                                       */
            sModel = DVF_clsDeviceForm.DVF_ModelText(objDisk.DEV_stInfo);
            if (sModel.Length == 0)
            {   /* No model text: the drive's own name. */
                sModel = "PhysicalDrive" + objDisk.DEV_iNumber;
            }

            /* "name, size, bus (path)": the name above, the size and bus in   */
            /* the picker's own words (PartitionForm.cs DVF_SizeText /         */
            /* DVF_BusText), and the path.                                     */
            sDescription = sModel + ", " +
                DVF_clsDeviceForm.DVF_SizeText(objDisk.DEV_stInfo.DVI_ullSize) + ", " +
                DVF_clsDeviceForm.DVF_BusText(objDisk.DEV_stInfo.DVI_uiBus) + " (" + objDisk.DEV_sPath + ")";

            /* The target: the whole device, the description, its size and   */
            /* its sector size.                                              */
            return(new TGT_clsTarget(objDisk.DEV_sPath, 0, 0, sDescription, objDisk.DEV_stInfo.DVI_ullSize, objDisk.DEV_stInfo.DVI_uiSectorBytes));
        }

        /*------------------------------------------------------------------*/
        /* Main_OnDeviceReadResult:                                         */
        /*                                                                  */
        /* The completion of Save Device to Image File's request            */
        /* (Main_PostDeviceRead).  A request that ended before it became a  */
        /* read -- the question cancelled, no disks, the list unreadable,   */
        /* refused -- still carries its question's record, and only its     */
        /* reason is said; one the worker made into a read has its chosen   */
        /* file's folder remembered for the next image dialog, and is       */
        /* reported as any raw copy is (Main_ReportCommandRaw, which takes  */
        /* the read's arguments as given).                                  */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objRequest : the request as the worker left it: its          */
        /*                  arguments the read's (RWA_clsRawArgs) once the  */
        /*                  device and the file were chosen.                */
        /*     objResult  : the outcome.                                    */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the outcome shown in the status line, or in a message */
        /*            box for an engine failure or a sum.                   */
        /*------------------------------------------------------------------*/
        private void Main_OnDeviceReadResult(RQT_clsRequest objRequest, RSL_clsResult objResult)
        {
            RWA_clsRawArgs objArgs;

            /* The read's arguments, which exist only once the device and   */
            /* the file were chosen.                                        */
            objArgs = objRequest.RQT_objPayload as RWA_clsRawArgs;
            if (objArgs == null)
            {   /* Never became a read: nothing was copied, and its reason   */
                /* ("No device was chosen.", "No disks could be found.", a   */
                /* refusal) replaces the "Looking for disks ..." the menu    */
                /* left on the status line.  A success here cannot happen    */
                /* (the sequence makes the read before it copies), so it     */
                /* shows nothing.                                            */
                if (objResult.RSL_iStatus != SYS_clsSystem.SYS_OK)
                {   /* Not a success: its reason said. */
                    Main_SetStatus(objResult.RSL_sDetail);
                }

                return;
            }

            /* The chosen file's folder becomes the shared image folder every   */
            /* image dialog starts in -- whatever came of the copy, as the      */
            /* file was chosen.                                                 */
            Main_RememberImageFolder(objArgs.RWA_sTo);

            /* The read's outcome, said as any raw copy's is. */
            Main_ReportCommandRaw(objRequest, objResult);
        }

        /*------------------------------------------------------------------*/
        /* Main_PostDeviceWrite:                                            */
        /*                                                                  */
        /* Write Image File to Device posted to the worker as ONE           */
        /* WriteDevice request with no target, carrying only the folder its */
        /* file dialog starts in -- the shared image folder as it is now.   */
        /* Its sequence asks the image file, inspects it, lists the disks,  */
        /* asks the device and the range, asks before writing, and writes   */
        /* (CommandRawSequence.cs, Sys_AskDeviceWrite); the outcome comes   */
        /* back to Main_OnDeviceWriteResult.                                */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     bUserInitiated : true for the menu (fresh user work, refused */
        /*                      while the worker is busy); false for the    */
        /*                      start-up's command after a restart as       */
        /*                      Administrator (the start-up's               */
        /*                      continuation).                              */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : posted, or the reason it could not be made in the     */
        /*            status line.                                          */
        /*------------------------------------------------------------------*/
        private void Main_PostDeviceWrite(bool bUserInitiated)
        {
            RQT_clsRequest objRequest;

            if (Main_objCommandClient == null)
            {   /* A guard: the command system is not made, so nothing can   */
                /* be posted.                                                */
                Main_SetStatus("The write-device command endpoint is not initialized.");
                return;
            }

            try
            {   /* The request, under the state as last published; the     */
                /* factory checks the kind against its arguments' record   */
                /* and throws for a mismatch.                              */
                objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.WriteDevice, 0, Main_objCommandClient.CLI_ReadState(),
                    null, null, 0, 0, new WDQ_clsDeviceWriteQuestion(Main_ImageFolder("")), "", "");
            }
            catch (ArgumentException exInput)
            {   /* Refused by the factory: said, and nothing posted. */
                Main_SetStatus(exInput.Message);
                return;
            }

            /* Posted as the caller says: fresh user work from the menu, a   */
            /* continuation from the start-up.                               */
            Main_objCommandClient.CLI_Post(objRequest, Main_OnDeviceWriteResult, bUserInitiated);
        }

        /*------------------------------------------------------------------*/
        /* Main_OnDeviceWriteResult:                                        */
        /*                                                                  */
        /* The completion of Write Image File to Device's request           */
        /* (Main_PostDeviceWrite).  The chosen image's folder, once one was */
        /* chosen, becomes the shared image folder whatever followed.  A    */
        /* request that ended before it became a write -- a question        */
        /* cancelled, the file unreadable, no disks -- still carries its    */
        /* question's record, and only its reason is said; one the worker   */
        /* made into a write is reported as any raw copy is                 */
        /* (Main_ReportCommandRaw, which takes the write's arguments as     */
        /* given).                                                          */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objRequest : the request as the worker left it: its          */
        /*                  arguments the write's (RWA_clsRawArgs) once the */
        /*                  file and the device were chosen.                */
        /*     objResult  : the outcome.                                    */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the outcome shown in the status line, or in a message */
        /*            box for an engine failure or a sum.                   */
        /*------------------------------------------------------------------*/
        private void Main_OnDeviceWriteResult(RQT_clsRequest objRequest, RSL_clsResult objResult)
        {
            RWA_clsRawArgs objArgs;
            WDQ_clsDeviceWriteQuestion objAsked;

            /* The write's arguments, which exist only once the file and the   */
            /* device were chosen.                                             */
            objArgs = objRequest.RQT_objPayload as RWA_clsRawArgs;
            if (objArgs == null)
            {   /* Never became a write: nothing was written.  The image's    */
                /* folder is remembered when the file question was answered   */
                /* (the sequence recorded it); the reason is said.  A         */
                /* success here cannot happen, so it shows nothing.           */
                objAsked = objRequest.RQT_objPayload as WDQ_clsDeviceWriteQuestion;
                if (objAsked != null && objAsked.WDQ_sImage.Length != 0)
                {   /* An image was chosen before the request ended. */
                    Main_RememberImageFolder(objAsked.WDQ_sImage);
                }

                if (objResult.RSL_iStatus != SYS_clsSystem.SYS_OK)
                {   /* Not a success: its reason said. */
                    Main_SetStatus(objResult.RSL_sDetail);
                }

                return;
            }

            /* The image's folder becomes the shared image folder every image   */
            /* dialog starts in, then the write's outcome is said.              */
            Main_RememberImageFolder(objArgs.RWA_sFrom);
            Main_ReportCommandRaw(objRequest, objResult);
        }

        /*------------------------------------------------------------------*/
        /* Main_ReportCommandRaw:                                           */
        /*                                                                  */
        /* The outcome of a Read Device or Write Device request, once its   */
        /* sequence has made it a copy (Main_OnDeviceReadResult,            */
        /* Main_OnDeviceWriteResult): the outcome said, and after a success */
        /* the image file's SHA-256 shown with where its .sha256 file was   */
        /* put.                                                             */
        /* Everything it reports was done by the worker; it writes and      */
        /* posts nothing.                                                   */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objRequest : the request (its direction and its arguments).  */
        /*     objResult  : the outcome; after a copy, the copy's result    */
        /*                  (ECR_clsCopyResult) as its payload.             */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the outcome shown in the status line, and in a        */
        /*            message box for an engine failure or a sum.           */
        /*------------------------------------------------------------------*/
        private void Main_ReportCommandRaw(RQT_clsRequest objRequest, RSL_clsResult objResult)
        {
            KND_enmKind enmKind;
            RWA_clsRawArgs objArgs;
            ECR_clsCopyResult objReport;
            string sTitle;
            string sHashFile;
            string sSidecar;

            /* The direction and the copy's arguments, from the request   */
            /* itself -- nothing the window shows now plays a part.       */
            enmKind = objRequest.RQT_enmKind;
            objArgs = (RWA_clsRawArgs)objRequest.RQT_objPayload;

            if (objArgs.RWA_sTitle.Length != 0)
            {   /* The title the progress window had (the device, how much   */
                /* and the file), used for the report too.                   */
                sTitle = objArgs.RWA_sTitle;
            }
            else if (enmKind == KND_enmKind.WriteDevice)
            {   /* No title given (the program's own posts always give   */
                /* one): a write's plain words.                          */
                sTitle = "Writing image to device";
            }
            else
            {   /* No title given: a read's plain words. */
                sTitle = "Reading device to image";
            }

            /* The request's outcome decides the report -- including the   */
            /* failures that come before any copy (refused, the image      */
            /* unreadable), which carry no copy result.                    */
            if (objResult.RSL_iStatus < 0)
            {   /* The system's own statuses (refused, dropped, a caught      */
                /* exception -- all negative, CommandSystem.cs): the reason   */
                /* in the status line, no box.                                */
                Main_SetStatus(objResult.RSL_sDetail);
                return;
            }

            if (objResult.RSL_iStatus == ENG_clsNative.ENG_ERR_CANCELLED)
            {   /* Cancelled (by the user, or by declining the write        */
                /* question; SYS_CANCELLED has the same value): said, and   */
                /* nothing more.                                            */
                Main_SetStatus("Cancelled.");
                return;
            }

            if (objResult.RSL_iStatus != ENG_clsNative.ENG_OK)
            {   /* An engine failure -- in the inspection, the table read      */
                /* or the copy itself: a message box with the status's         */
                /* sentence and the detail of THIS outcome -- and the status   */
                /* line says it failed, so no earlier line ("Looking for       */
                /* disks ...") stands after the box is closed.                 */
                Main_SetStatus(sTitle + ": failed.");
                Main_ShowEngineError(sTitle, objResult.RSL_iStatus, objResult.RSL_sDetail);
                return;
            }

            /* Done: said in the status line, then the sum, if there is one.   */
            /* A cancel during the hash, or a hash that failed (logged),       */
            /* leaves the copy's success with no sum -- nothing to show, and   */
            /* it is never computed again from here.                           */
            Main_SetStatus(sTitle + ": done.");
            objReport = objResult.RSL_objPayload as ECR_clsCopyResult;
            if (objReport == null || String.IsNullOrEmpty(objReport.ECR_sHash))
            {   /* No sum: the status line is the whole report. */
                return;
            }

            /* The file the sum is of: the image FILE, as published sums     */
            /* are -- compressed or not -- the same file the worker hashed   */
            /* (CommandNativeOperations.cs picks it the same way).           */
            if (enmKind == KND_enmKind.ReadDevice)
            {   /* A read: the file it wrote. */
                sHashFile = objArgs.RWA_sTo;
            }
            else
            {   /* A write: the image it wrote from. */
                sHashFile = objArgs.RWA_sFrom;
            }

            if (String.IsNullOrEmpty(objReport.ECR_sWarning))
            {   /* The .sha256 file was written: named beside the image. */
                sSidecar = "Also saved as " + sHashFile + ".sha256";
            }
            else
            {   /* It was not written -- it could not be, or an existing one   */
                /* was kept at the user's word (Nst_Raw,                       */
                /* CommandNativeOperations.cs): the worker's own sentence,     */
                /* which says which and why.  The copy and its sum stand.      */
                sSidecar = objReport.ECR_sWarning;
            }

            /* The sum shown in an information box, with the file it is of   */
            /* and what became of its .sha256 file; everything in it was     */
            /* written by the worker before this ran.                        */
            MSG_clsMessageForm.MSG_Show(this, Main_fntDialog,
                sTitle + ": done.\n\nSHA-256 of " + Path.GetFileName(sHashFile) + ":\n" + objReport.ECR_sHash +
                "\n\n" + sSidecar, "BifrostFM", MessageBoxButtons.OK,
                MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
        }
    }
}
