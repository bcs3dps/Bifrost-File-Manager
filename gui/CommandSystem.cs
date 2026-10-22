/*----------------------------------------------------------------------------*/
/* CommandSystem.cs                                                           */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The command system's core: its one worker thread, which sleeps until a     */
/* request arrives and then runs that request's whole sequence before taking  */
/* the next; the admission of posted requests (SYS_Post) by their scheduling  */
/* class; the drag reservation; and the publication of views to the UI.  The  */
/* worker alone changes the filesystem state (the open session, the modes,    */
/* the editors); the UI only posts.  The queue lock guards admission and the  */
/* cancel / exit signals, and is never held across engine or UI work -- no    */
/* operation keeps a busy flag of its own.  The sequences, gates, questions   */
/* and steps are in the other parts of SYS_clsSystem (CommandSequences.cs,    */
/* CommandInterrupts.cs, CommandOperations.cs ...).                           */
/*----------------------------------------------------------------------------*/

/* The framework: the exceptions and the thread start (System), the queue,   */
/* the editor and picker registries and the spent-cut set                    */
/* (Collections.Generic), and the worker thread with its wake event          */
/* (Threading).                                                              */
using System;
using System.Collections.Generic;
using System.Threading;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* SYS_PublishView:                                                       */
    /*                                                                        */
    /* The shape of the worker's one publication call (CLI_Publish,           */
    /* CommandClient.cs): hand a view to the UI without waiting.              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objView : the view, its data finished (a question included).       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when it was posted; false when the UI can no longer    */
    /*            be reached.                                                 */
    /*------------------------------------------------------------------------*/
    internal delegate bool SYS_PublishView(VWM_clsView objView);

    internal sealed partial class SYS_clsSystem
    {
        /* The system's own statuses, apart from the engine's -- documentation   */
        /* table.                                                                */
        internal const int SYS_OK = 0;          // success; equal to ENG_OK
        internal const int SYS_CANCELLED = 18;  // cancelled; equal to ENG_ERR_CANCELLED (EngineNative.cs), so a cancel reads the same from either side
        internal const int SYS_REFUSED = -1;    // refused by admission or a state gate; never an engine status, never passed to the engine
        internal const int SYS_INTERNAL = -2;   // a managed exception was caught; never a success
        internal const int SYS_DROPPED = -3;    // a duplicate of a waiting request: the earlier one still carries the work

        /* When a delete shows its progress window -- documentation table.   */
        /* The one place the threshold is set, so it is tuned here alone.    */
        internal const int SYS_DELETE_PROGRESS_ENTRIES = 200;  // entries a delete removes before its progress window is shown; a smaller delete never shows one, and no counting walk runs first

        /* The bindings, fixed for the system's life (their contents change,   */
        /* the references do not) -- documentation table.                      */
        private readonly object Sys_objQueue;                                  // the one scheduler lock; never held across engine or UI work
        private readonly List<RQT_clsRequest> Sys_lstWaiting;                  // the waiting requests, in queue order (an Urgent one inserted at the head)
        private readonly AutoResetEvent Sys_evtWake;                           // set by every accepted post: wakes the idle worker, and a question waiting for an urgent request
        private readonly STB_clsStepTable Sys_objSteps;                        // the native steps' runner and release (CommandNativeSteps.cs)
        private readonly SYS_PublishView Sys_pfnPublish;                       // the publication call (CLI_Publish); never carried in a request
        private readonly List<EDR_clsEditor> Sys_lstEditors;                   // the editors registered on image files; worker thread only
        private readonly Dictionary<long, EDR_clsEditor> Sys_dictHostEditors;  // the editors on Windows files, by editor id; worker thread only
        private readonly List<ICV_clsInputView> Sys_lstInputs;                 // the pickers open; worker thread only, no controls held
        private readonly HashSet<uint> Sys_setConsumedCuts;                    // the clipboard sequence numbers of cuts already carried out (CommandCopySequence.cs); worker thread only

        /* The running request and the signals, read and set under the queue   */
        /* lock -- documentation table.                                        */
        private Thread Sys_thrWorker;               // the worker, made once by SYS_Start; ended by an Exit, never joined by the UI
        private RQT_clsRequest Sys_objRunning;      // the request running now, the only one a Cancel can name; null when idle
        private RQT_clsRequest Sys_objExit;         // an agreed Exit, to run once the interrupted step has unwound (CommandInterrupts.cs)
        private QST_clsQuestion Sys_objQuestion;    // the question being asked now; worker thread only
        private STV_clsStateView Sys_objPublished;  // the last state view sent: the drag reservation's test reads it
        private long Sys_lNextSerial;               // the last serial given, to a request or a drag reservation; only goes up
        private long Sys_lDrag;                     // the drag reservation's token; 0 when there is none
        private bool Sys_bCancelled;                // a Cancel of the running request has arrived
        private bool Sys_bExitPending;              // an Exit has arrived and is to be asked at the next checkpoint or question wait
        private bool Sys_bExitHadRunning;           // a request was running when the Exit arrived: it still asks, even if that request could not be cancelled
        private bool Sys_bTerminal;                 // an Exit was agreed: ordinary posts are refused
        private bool Sys_bStopped;                  // the worker has ended: nothing more runs
        private bool Sys_bUserInputHeld;            // a picker that asks for a value is open (the worker's copy of Sys_HasValueInput, set before it drops Sys_objRunning)

        /* The filesystem and window state, the worker's alone --   */
        /* documentation table.                                     */
        private MOD_enmMode Sys_enmMode;      // the exclusive mode now
        private TGT_clsTarget Sys_objTarget;  // the open session's or exclusive window's target; null in None
        private long Sys_lEpoch;              // the filesystem lifetime: stepped by a new open, kept by a same-range reopen for writing or reading
        private long Sys_lClipboardLifetime;  // stepped by every session close, even one that keeps the epoch: a cut captured under an older value is stale
        private long Sys_lModeWindow;         // the id of the check or partition window that holds the mode
        private bool Sys_bWritable;           // the session was opened writable, successfully
        private bool Sys_bRiskWarnings;       // the risk warnings option, the worker's copy (SetRiskWarnings changes it)
        private bool Sys_bReopenAfterCheck;   // the check window's close is to reopen the session it closed (CommandSequences.cs)
        private uint Sys_uiVolumeType;        // the open session's ENG_VOLTYPE_*, from the facts its open read (Sys_OpenSession); 0 while nothing is open; the gate refuses Check Filesystem on FAT and exFAT by it, and the name tests read it for FAT's rules and length (New Folder and Rename, CommandOperations.cs; the picker's new folder, CommandPickerFolder.cs; a copy's free names, CommandCopySequence.cs)
        private string Sys_sMainFolder;       // the main pane's folder after its last successful listing; a picker's listing never changes it

        /*--------------------------------------------------------------------*/
        /* SYS_clsSystem:                                                     */
        /*                                                                    */
        /* The command system, not yet started: no session, no mode, no       */
        /* editors, an empty queue (CommandClient.cs makes it).               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSteps      : the native steps' runner and release.          */
        /*     pfnPublish    : the publication call.                          */
        /*     bRiskWarnings : the risk warnings option, as saved.            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the system; an ArgumentNullException for a     */
        /*                     missing binding.                               */
        /*--------------------------------------------------------------------*/
        internal SYS_clsSystem(STB_clsStepTable objSteps, SYS_PublishView pfnPublish, bool bRiskWarnings)
        {
            if (objSteps == null)
            {   /* No steps: refused -- the worker could run nothing. */
                throw new ArgumentNullException("objSteps");
            }

            if (pfnPublish == null)
            {   /* No publication call: refused -- no question could be   */
                /* asked and no result shown.                             */
                throw new ArgumentNullException("pfnPublish");
            }

            /* The lock, the queue, the wake event and the registries, made   */
            /* before any thread can see them.                                */
            Sys_objQueue = new object();
            Sys_lstWaiting = new List<RQT_clsRequest>();
            Sys_evtWake = new AutoResetEvent(false);
            Sys_objSteps = objSteps;
            Sys_pfnPublish = pfnPublish;
            Sys_lstEditors = new List<EDR_clsEditor>();
            Sys_dictHostEditors = new Dictionary<long, EDR_clsEditor>();
            Sys_lstInputs = new List<ICV_clsInputView>();

            /* The spent cuts are the worker's record, not the UI's: the UI   */
            /* clears the clipboard later and asynchronously, so a Paste      */
            /* queued before that clear must still be stopped from removing   */
            /* the same cut's sources a second time.                          */
            Sys_setConsumedCuts = new HashSet<uint>();

            /* The option as saved, the main pane at the root, and a first       */
            /* state view of nothing open (for the drag test before the worker   */
            /* has published one).                                               */
            Sys_bRiskWarnings = bRiskWarnings;
            Sys_sMainFolder = "/";
            Sys_objPublished = new STV_clsStateView(MOD_enmMode.None, null, 0, false, false, false);
        }

        /*--------------------------------------------------------------------*/
        /* SYS_Start:                                                         */
        /*                                                                    */
        /* Start the worker thread, once the main window exists (MainCommand  */
        /* .cs) -- the first state view it sends needs a window to go to.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the worker running; it ends only through an Exit.       */
        /*            InvalidOperationException if started before.            */
        /*--------------------------------------------------------------------*/
        internal void SYS_Start()
        {
            lock (Sys_objQueue)
            {   /* Under the lock, so two starts cannot both make a worker --   */
                /* there must only ever be one thread calling the engine.       */
                if (Sys_thrWorker != null || Sys_bStopped)
                {   /* Started already, or ended: a system runs once. */
                    throw new InvalidOperationException("Command runner already started or stopped.");
                }

                /* The worker: named for the debugger and the log, and a         */
                /* background thread, so a UI that dies without an Exit does     */
                /* not leave the process alive; a normal close still waits for   */
                /* its Stopped view.                                             */
                Sys_thrWorker = new Thread(Sys_ThreadMain);
                Sys_thrWorker.Name = "BifrostFM commands";
                Sys_thrWorker.IsBackground = true;
                /* A single-threaded apartment, as the UI thread is (Program.cs   */
                /* Main is [STAThread]): the Recycle Bin calls, run on this       */
                /* worker (CommandRecycleSources.cs), are the shell's, which      */
                /* were made from the UI thread before; this keeps them in the    */
                /* same kind of apartment rather than adding another thread.      */
                Sys_thrWorker.SetApartmentState(ApartmentState.STA);
                Sys_thrWorker.Start();
            }
        }

        /*--------------------------------------------------------------------*/
        /* SYS_Policy:                                                        */
        /*                                                                    */
        /* A request kind's scheduling class -- decided here, from the kind   */
        /* alone, so no poster can choose a higher one.                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     enmKind : the request's kind.                                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     PRY_enmPriority : the class (CommandContracts.cs says what     */
        /*                       each means).                                 */
        /*--------------------------------------------------------------------*/
        internal static PRY_enmPriority SYS_Policy(KND_enmKind enmKind)
        {
            switch (enmKind)
            {   /* One class per group of kinds. */
                case KND_enmKind.Cancel:
                case KND_enmKind.Exit:
                    /* Interrupts: ahead of everything waiting, so a checkpoint   */
                    /* sees them while the running step is still going.           */
                    return(PRY_enmPriority.Urgent);
                case KND_enmKind.ListFolder:
                case KND_enmKind.ListDevices:
                case KND_enmKind.ReadDeviceTable:
                case KND_enmKind.PickerList:
                case KND_enmKind.PickerBranch:
                case KND_enmKind.SetVerify:
                case KND_enmKind.SetRiskWarnings:
                    /* Streams of values where only the newest still matters. */
                    return(PRY_enmPriority.Latest);
                case KND_enmKind.DragExtract:
                    /* The drag's read-out: the UI waits for it inside the   */
                    /* drag, so it runs only under the drag's reservation,   */
                    /* with the worker idle.                                 */
                    return(PRY_enmPriority.IdleOnly);
                case KND_enmKind.EditorClosed:
                case KND_enmKind.EditorReady:
                case KND_enmKind.RegisterHostEditor:
                case KND_enmKind.HostEditorSaved:
                case KND_enmKind.InputClosed:
                case KND_enmKind.PartitionClosed:
                case KND_enmKind.CheckClosed:
                case KND_enmKind.DragEnded:
                    /* "A window has ended" and similar facts: admitted even   */
                    /* during an Exit, so no ownership is ever left held.      */
                    return(PRY_enmPriority.Notification);
                default:
                    /* Everything else: in posting order, its state gate   */
                    /* deciding when it runs.                              */
                    return(PRY_enmPriority.Ordered);
            }
        }

        /*--------------------------------------------------------------------*/
        /* SYS_Post:                                                          */
        /*                                                                    */
        /* Hand a request to the worker, without waiting: admitted into the   */
        /* queue by its class, or refused or dropped.  Only the queue is      */
        /* looked at here; whether the request may run is its state gate's    */
        /* question, asked by the worker when it takes it.  The request is    */
        /* the worker's from this call on, whatever the outcome: a refused    */
        /* one has its stage deleted and its refusal published here.          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest     : a request never posted before.                */
        /*     bUserInitiated : true for a fresh user action (a menu, a pane, */
        /*                      a button); false, the default, for a          */
        /*                      sequence's own follow-up or a fact.           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when queued; false when refused or dropped (a view */
        /*            says why).  InvalidOperationException for a request     */
        /*            posted twice.                                           */
        /*--------------------------------------------------------------------*/
        internal bool SYS_Post(RQT_clsRequest objRequest, bool bUserInitiated = false)
        {
            PRY_enmPriority enmPriority;
            RQT_clsRequest objReplaced;
            RQT_clsRequest objWaiting;
            VWK_enmViewKind enmReport;
            string sReason;
            int iReportStatus;
            int i;
            bool bDropped;
            bool bAccepted;

            if (objRequest == null)
            {   /* No request: refused, nothing to release. */
                throw new ArgumentNullException("objRequest");
            }

            /* The class, and nothing decided yet. */
            enmPriority = SYS_Policy(objRequest.RQT_enmKind);
            objReplaced = null;
            sReason = "";
            bDropped = false;
            bAccepted = false;
            lock (Sys_objQueue)
            {   /* The whole admission decision under one hold of the lock. */
                if (objRequest.RQT_lSerial != 0)
                {   /* Posted before: refused -- a second post would release   */
                    /* its stage twice.                                        */
                    throw new InvalidOperationException("A request may be posted only once.");
                }

                /* Its serial, the next one ("checked", so a wrap throws      */
                /* rather than letting a Cancel name an old request).  Then   */
                /* the tests, in order; the first that fails decides.         */
                objRequest.RQT_lSerial = checked(++Sys_lNextSerial);
                if (Sys_bStopped)
                {   /* The worker has ended: refused (a notification then has   */
                    /* nothing left to tell).                                   */
                    sReason = "The command system has stopped.";
                }
                else if (bUserInitiated && objRequest.RQT_enmKind != KND_enmKind.Cancel &&
                    objRequest.RQT_enmKind != KND_enmKind.Exit && (Sys_bExitPending || Sys_bTerminal))
                {   /* A fresh user action while an Exit (or a restart) is        */
                    /* pending: refused, whatever its class -- a user action      */
                    /* posted as a notification kind gets no way past.  The       */
                    /* editors' and windows' own answers are not user-initiated   */
                    /* and go on through the tests below.                         */
                    sReason = "Exit or restart is pending.";
                }
                else if (enmPriority != PRY_enmPriority.Notification &&
                    (Sys_bTerminal || (enmPriority != PRY_enmPriority.Urgent && Sys_HasUrgent())))
                {   /* Anything but a notification after an agreed Exit, and      */
                    /* anything but an interrupt while one is pending: refused,   */
                    /* so nothing new queues up behind the decision.              */
                    sReason = "Exit or cancellation is pending.";
                }
                else if (objRequest.RQT_enmKind == KND_enmKind.Exit && Sys_bExitPending)
                {   /* A second Exit: dropped -- the first may already be   */
                    /* asking, out of the queue.                            */
                    bDropped = true;
                    sReason = "Exit is already pending.";
                }
                else if (objRequest.RQT_enmKind == KND_enmKind.Cancel &&
                    (Sys_objRunning == null || objRequest.RQT_lRevision != Sys_objRunning.RQT_lSerial))
                {   /* A Cancel of a request that is no longer running (it ends   */
                    /* between the click and here): refused, so a late Cancel     */
                    /* can never stop the request that came after it.             */
                    sReason = "That operation is no longer running.";
                }
                else if (bUserInitiated && enmPriority != PRY_enmPriority.Notification &&
                    enmPriority != PRY_enmPriority.Urgent && Sys_UserWorkBlocked())
                {   /* A fresh user action while the worker is busy: refused --   */
                    /* user work is never queued behind other work.  A            */
                    /* sequence's own follow-ups and facts are not tested here,   */
                    /* or a request could never finish what it started.           */
                    sReason = "An operation is in progress. Wait for it to finish.";
                }
                else if (enmPriority == PRY_enmPriority.IdleOnly &&
                    (Sys_lDrag == 0 || objRequest.RQT_lRevision != Sys_lDrag ||
                    Sys_objRunning != null || Sys_HasDragExtract()))
                {   /* The drag's read-out without its reservation -- none, not   */
                    /* this one, the worker busy, or one already queued:          */
                    /* refused.                                                   */
                    sReason = "The drag reservation is no longer available.";
                }
                else
                {   /* Admissible: look for a waiting request it duplicates or   */
                    /* replaces.                                                 */
                    for (i = 0; i < Sys_lstWaiting.Count; i++)
                    {   /* One waiting request per iteration (the running one    */
                        /* is not in the list).  Only the same stream -- kind,   */
                        /* origin, epoch and stream key -- can match.            */
                        objWaiting = Sys_lstWaiting[i];
                        if (!Sys_SameStream(objWaiting, objRequest))
                        {   /* Another stream: no bearing on this one. */
                            continue;
                        }

                        if (enmPriority == PRY_enmPriority.Latest)
                        {   /* A newer value of the same stream: the waiting one   */
                            /* is replaced.                                        */
                            objReplaced = objWaiting;
                            break;
                        }

                        if (enmPriority == PRY_enmPriority.Urgent ||
                            (enmPriority == PRY_enmPriority.Ordered && objWaiting.RQT_sIdentity == objRequest.RQT_sIdentity))
                        {   /* An interrupt already waiting, or an equal   */
                            /* request (same identity): the new one is     */
                            /* dropped.  Same-kind requests that differ    */
                            /* still queue.                                */
                            bDropped = true;
                            sReason = "An identical request is already waiting.";
                            break;
                        }
                    }

                    if (!bDropped)
                    {   /* Queued: the queue changed and the interrupt flags set   */
                        /* in the same hold of the lock, so a checkpoint never     */
                        /* sees one without the other.                             */
                        if (objReplaced != null)
                        {   /* The replaced one out of the queue (its stage is   */
                            /* released below, outside the lock).                */
                            Sys_lstWaiting.Remove(objReplaced);
                        }

                        if (enmPriority == PRY_enmPriority.Urgent)
                        {   /* An interrupt at the head. */
                            Sys_lstWaiting.Insert(0, objRequest);
                        }
                        else
                        {   /* Everything else at the end, in posting order. */
                            Sys_lstWaiting.Add(objRequest);
                        }

                        if (objRequest.RQT_enmKind == KND_enmKind.Cancel)
                        {   /* A Cancel: the running request (the one it named,   */
                            /* tested above) sees it at its next checkpoint.      */
                            Sys_bCancelled = true;
                        }

                        if (objRequest.RQT_enmKind == KND_enmKind.Exit)
                        {   /* An Exit: the running request pauses at its next    */
                            /* checkpoint and the user is asked before anything   */
                            /* is cancelled (CommandInterrupts.cs).               */
                            Sys_bExitPending = true;
                            Sys_bExitHadRunning = Sys_objRunning != null;
                        }

                        /* The request is the queue's now. */
                        bAccepted = true;
                    }
                }
            }

            if (objReplaced != null)
            {   /* The replaced request's stage released and its replacement    */
                /* reported, outside the lock, so neither holds up admission.   */
                Sys_Release(objReplaced);
                SYS_Show(new VWM_clsView(VWK_enmViewKind.Dropped, objReplaced,
                    new RSL_clsResult(SYS_CANCELLED, "A newer request replaced this one.", null)));
            }

            if (!bAccepted)
            {   /* Refused or dropped: the request is still the worker's to   */
                /* end, so its stage is released here, at once.               */
                Sys_Release(objRequest);

                /* Which view: Dropped for a duplicate (the earlier request   */
                /* still does the work), Refused otherwise.                   */
                enmReport = VWK_enmViewKind.Refused;
                iReportStatus = SYS_REFUSED;
                if (bDropped)
                {   /* A duplicate. */
                    enmReport = VWK_enmViewKind.Dropped;
                    iReportStatus = SYS_DROPPED;
                }

                /* The request's one outcome, after its stage is gone. */
                SYS_Show(new VWM_clsView(enmReport, objRequest,
                    new RSL_clsResult(iReportStatus, sReason, null)));
                return(false);
            }

            /* The worker woken: from its idle wait, or from a question        */
            /* waiting for an interrupt.  An AutoResetEvent keeps the signal   */
            /* until it is waited on, so a wake is never lost.                 */
            Sys_evtWake.Set();

            /* Queued: the worker will run it and publish its result. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* SYS_CanAcceptUserWork:                                             */
        /*                                                                    */
        /* Whether a fresh user action would be admitted now -- for the UI    */
        /* to grey what it offers (MainCommand.cs, CommandEditorCoordinator   */
        /* .cs).  Only advice: SYS_Post tests again under the lock.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when nothing blocks user work.                     */
        /*--------------------------------------------------------------------*/
        internal bool SYS_CanAcceptUserWork()
        {
            lock (Sys_objQueue)
            {   /* Read under the lock, so a pending Exit shows at once,   */
                /* before the worker publishes it.                         */
                return(!Sys_UserWorkBlocked());
            }
        }

        /*--------------------------------------------------------------------*/
        /* Sys_UserWorkBlocked:                                               */
        /*                                                                    */
        /* Whether fresh user work must be refused.  The caller holds the     */
        /* queue lock.                                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true while the worker has ended or is exiting, an       */
        /*            interrupt is pending, a request runs or waits, a drag   */
        /*            is reserved, or a picker that asks for a value is open. */
        /*--------------------------------------------------------------------*/
        private bool Sys_UserWorkBlocked()
        {
            /* Any one of them blocks.  The picker test reads the worker's       */
            /* copy (Sys_bUserInputHeld), which the worker sets BEFORE it        */
            /* drops its running request (Sys_ThreadMain) -- so there is no      */
            /* moment between two requests at which an open picker looks idle.   */
            return(Sys_bStopped || Sys_bTerminal || Sys_HasUrgent() || Sys_objRunning != null ||
                Sys_lstWaiting.Count != 0 || Sys_lDrag != 0 || Sys_bUserInputHeld);
        }

        /*--------------------------------------------------------------------*/
        /* Sys_HasUrgent:                                                     */
        /*                                                                    */
        /* Whether an interrupt is pending: an Exit not yet settled, or a     */
        /* Cancel or Exit still in the queue.  The caller holds the queue     */
        /* lock.                                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true while one is pending.                              */
        /*--------------------------------------------------------------------*/
        private bool Sys_HasUrgent()
        {
            int i;

            /* An Exit counts as pending even while its question has taken it   */
            /* out of the queue, so nothing slips in behind its window.         */
            if (Sys_bExitPending)
            {   /* An Exit not yet settled. */
                return(true);
            }

            /* Else any interrupt still waiting in the queue. */
            for (i = 0; i < Sys_lstWaiting.Count; i++)
            {   /* One waiting request per iteration. */
                if (SYS_Policy(Sys_lstWaiting[i].RQT_enmKind) == PRY_enmPriority.Urgent)
                {   /* An interrupt waiting (a Cancel stays until the step it   */
                    /* stops has unwound).                                      */
                    return(true);
                }
            }

            /* None pending. */
            return(false);
        }

        /*--------------------------------------------------------------------*/
        /* Sys_HasDragExtract:                                                */
        /*                                                                    */
        /* Whether the drag's read-out is already queued -- the drag's        */
        /* GetData can be called more than once, but only one read-out may    */
        /* run.  The caller holds the queue lock.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when one is waiting.                               */
        /*--------------------------------------------------------------------*/
        private bool Sys_HasDragExtract()
        {
            int i;

            /* The waiting requests, searched for one. */
            for (i = 0; i < Sys_lstWaiting.Count; i++)
            {   /* One waiting request per iteration. */
                if (Sys_lstWaiting[i].RQT_enmKind == KND_enmKind.DragExtract)
                {   /* One is queued already. */
                    return(true);
                }
            }

            /* None queued. */
            return(false);
        }

        /*--------------------------------------------------------------------*/
        /* Sys_SameStream:                                                    */
        /*                                                                    */
        /* Whether two requests belong to one stream, for SYS_Post's          */
        /* duplicate and replacement tests: the same kind, origin, epoch and  */
        /* stream key.                                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objLeft  : a waiting request.                                  */
        /*     objRight : the request being posted.                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when all four match.                               */
        /*--------------------------------------------------------------------*/
        private static bool Sys_SameStream(RQT_clsRequest objLeft, RQT_clsRequest objRight)
        {
            /* The origin keeps a picker's listing apart from the main pane's,   */
            /* the epoch keeps two filesystem lifetimes apart, and a stream      */
            /* that depends on a target has the whole range in its key (made     */
            /* by its factory, CommandArguments.cs).                             */
            return(objLeft.RQT_enmKind == objRight.RQT_enmKind && objLeft.RQT_lOrigin == objRight.RQT_lOrigin &&
                objLeft.RQT_lEpoch == objRight.RQT_lEpoch && objLeft.RQT_sStream == objRight.RQT_sStream);
        }

        /*--------------------------------------------------------------------*/
        /* SYS_ReserveDrag:                                                   */
        /*                                                                    */
        /* Reserve the idle worker for a drag out of a pane (MainForm.cs),    */
        /* before the drag starts: inside the drag the UI thread blocks       */
        /* waiting for the read-out, so the worker must be free to do it and  */
        /* must not ask the UI anything meanwhile.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lEpoch : the filesystem lifetime of the last state view the UI */
        /*              received.                                             */
        /*     bHost  : true for a drag of Windows files only, which needs no */
        /*              open image; optional, false.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     long : the reservation's token; 0 when the worker is not free  */
        /*            (the drag is then not started).                         */
        /*--------------------------------------------------------------------*/
        internal long SYS_ReserveDrag(long lEpoch, bool bHost = false)
        {
            lock (Sys_objQueue)
            {   /* Under the lock the worker takes and finishes requests   */
                /* with, so it cannot start one between the test and the   */
                /* reservation.                                            */
                if (Sys_bTerminal || Sys_bStopped || Sys_objRunning != null || Sys_lstWaiting.Count != 0 ||
                    Sys_lDrag != 0 || Sys_bUserInputHeld || (!bHost &&
                    (Sys_objPublished.STV_enmMode != MOD_enmMode.Open || Sys_objPublished.STV_lEpoch != lEpoch)))
                {   /* Not free -- exiting, ended, busy, work waiting, a drag   */
                    /* already reserved, a picker open -- or, for an image      */
                    /* drag, no session or not the one the UI saw: no           */
                    /* reservation, since the blocked UI could not answer a     */
                    /* question.                                                */
                    return(0);
                }

                /* Reserved: requests may still be posted, but none but the    */
                /* drag's runs until the reservation ends (Sys_Take).  The     */
                /* token comes from the serial counter, so it is never 0 and   */
                /* never a past token.                                         */
                Sys_lDrag = checked(++Sys_lNextSerial);

                /* The token: only a read-out or drag end carrying it counts. */
                return(Sys_lDrag);
            }
        }

        /*--------------------------------------------------------------------*/
        /* SYS_Show:                                                          */
        /*                                                                    */
        /* Publish one view, without waiting, through the publication call.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objView : the view.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when it was posted; false when it could not be --  */
        /*            the UI gone, or the call threw.                         */
        /*--------------------------------------------------------------------*/
        internal bool SYS_Show(VWM_clsView objView)
        {
            try
            {   /* The call, and whether it was taken.  Publishing runs no   */
                /* next step; that is the sequence's.                        */
                return(Sys_pfnPublish(objView));
            }
            catch (Exception)
            {   /* Any exception is taken as the UI gone: false, so a          */
                /* question's caller cancels it before its wait is ended --    */
                /* and nothing from the UI side unwinds through the worker's   */
                /* engine work.                                                */
                return(false);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Sys_Release:                                                       */
        /*                                                                    */
        /* Release a finished request's stages (STB_Release), never letting a */
        /* failure stop the worker.                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the request.                                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : released, or a Warning view says why not; nothing is    */
        /*            tried again.                                            */
        /*--------------------------------------------------------------------*/
        private void Sys_Release(RQT_clsRequest objRequest)
        {
            try
            {   /* The release: no engine call, and not under the lock. */
                Sys_objSteps.STB_Release(objRequest);
            }
            catch (Exception exRelease)
            {   /* A stage file that cannot be deleted: a warning, and the     */
                /* worker goes on -- the queue must not stall on a temporary   */
                /* file.                                                       */
                SYS_Show(new VWM_clsView(VWK_enmViewKind.Warning, objRequest,
                    new RSL_clsResult(SYS_INTERNAL, "Temporary cleanup failed: " + exRelease.Message, null)));
            }
        }

        /*--------------------------------------------------------------------*/
        /* Sys_PublishState:                                                  */
        /*                                                                    */
        /* Send the UI the worker's whole state after it changes: a new state */
        /* view, kept also for the drag test.                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the request that changed it, or null for the      */
        /*                  first view.                                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the view published.                                     */
        /*--------------------------------------------------------------------*/
        private void Sys_PublishState(RQT_clsRequest objRequest)
        {
            STV_clsStateView objView;

            lock (Sys_objQueue)
            {   /* Made and kept under the lock, so SYS_ReserveDrag reads   */
                /* the very view the UI is sent, never half an update.      */
                objView = new STV_clsStateView(Sys_enmMode, Sys_objTarget, Sys_lEpoch,
                    Sys_bWritable, Sys_objRunning != null, Sys_bTerminal, Sys_lClipboardLifetime);
                Sys_objPublished = objView;
            }

            /* Sent outside the lock. */
            SYS_Show(new VWM_clsView(VWK_enmViewKind.State, objRequest, objView));
        }

        /*---------------------------------------------------------------------*/
        /* Sys_Take:                                                           */
        /*                                                                     */
        /* The next request that may run now, out of the queue.  While a drag  */
        /* is reserved only the drag's own requests may; while a picker that   */
        /* asks for a value is open, only the facts that report a window's or  */
        /* a drag's end and the editors' reports -- nothing that does work.    */
        /* Called by the worker alone, with the queue lock held.               */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     None.                                                           */
        /*                                                                     */
        /* Returns:                                                            */
        /*     RQT_clsRequest : the request, removed from the queue; null      */
        /*                      when none may run yet.                         */
        /*---------------------------------------------------------------------*/
        private RQT_clsRequest Sys_Take()
        {
            RQT_clsRequest objRequest;
            bool bValueInput;
            int i;

            /* Whether a value-asking picker is open (CommandInputs.cs): such   */
            /* a picker holds other work and even an Exit until it returns;     */
            /* an image folder picker does not, so its listings go on.          */
            bValueInput = Sys_HasValueInput();

            /* The first request, in queue order, that may run; one that may   */
            /* not stays where it is (it is held, not cancelled).              */
            for (i = 0; i < Sys_lstWaiting.Count; i++)
            {   /* One waiting request per iteration. */
                objRequest = Sys_lstWaiting[i];
                if (Sys_lDrag != 0 && objRequest.RQT_enmKind != KND_enmKind.DragExtract &&
                    objRequest.RQT_enmKind != KND_enmKind.DragEnded)
                {   /* A drag is reserved: only its read-out and its end run.    */
                    /* Even an Exit waits -- asking the UI while it is blocked   */
                    /* inside the drag would deadlock both threads.              */
                    continue;
                }

                if (bValueInput && objRequest.RQT_enmKind != KND_enmKind.InputClosed &&
                    objRequest.RQT_enmKind != KND_enmKind.EditorClosed && objRequest.RQT_enmKind != KND_enmKind.EditorReady &&
                    objRequest.RQT_enmKind != KND_enmKind.RegisterHostEditor && objRequest.RQT_enmKind != KND_enmKind.HostEditorSaved &&
                    objRequest.RQT_enmKind != KND_enmKind.DragEnded)
                {   /* A value-asking picker is open: only these facts run      */
                    /* until it returns.  CheckClosed is NOT among them -- it   */
                    /* can reopen a filesystem -- and an Exit waits too,        */
                    /* rather than force the picker's window closed or ask      */
                    /* above its disabled owner.                                */
                    continue;
                }

                /* This one: out of the queue; the caller makes it the running   */
                /* request in the same hold of the lock.                         */
                Sys_lstWaiting.RemoveAt(i);

                /* The request to run. */
                return(objRequest);
            }

            /* None may run yet: the worker sleeps until a post (an              */
            /* InputClosed or DragEnded among them) wakes it -- nothing polls.   */
            return(null);
        }

        /*--------------------------------------------------------------------*/
        /* Sys_ThreadMain:                                                    */
        /*                                                                    */
        /* The worker: take one request, run its whole sequence, publish its  */
        /* result, release its stages, and only then take the next -- until   */
        /* an agreed Exit succeeds.  Then whatever is left in the queue is    */
        /* refused and Stopped is sent.                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : once the worker has stopped (Stopped published; the     */
        /*            program may then unload the engine).                    */
        /*--------------------------------------------------------------------*/
        private void Sys_ThreadMain()
        {
            RQT_clsRequest objRequest;
            RSL_clsResult objResult;
            VWK_enmViewKind enmResultView;
            bool bStop;

            /* The first state view, before any request is handled. */
            Sys_PublishState(null);
            for (;;)
            {   /* One whole request, or one idle wait, per iteration. */
                lock (Sys_objQueue)
                {   /* The next request taken and made the running one in a    */
                    /* single hold of the lock, so a Cancel posted meanwhile   */
                    /* always finds the right request.                         */
                    if (Sys_objExit != null)
                    {   /* An agreed Exit first, now that the step it        */
                        /* interrupted has unwound (CommandInterrupts.cs).   */
                        objRequest = Sys_objExit;
                    }
                    else
                    {   /* Otherwise the first that may run. */
                        objRequest = Sys_Take();
                    }

                    /* The running request (or none), its Cancel not yet set. */
                    Sys_objExit = null;
                    Sys_objRunning = objRequest;
                    Sys_bCancelled = false;
                }

                if (objRequest == null)
                {   /* Nothing may run: sleep until a post.  A post that came   */
                    /* after Sys_Take looked has set the event already, so      */
                    /* the wait returns at once.                                */
                    Sys_evtWake.WaitOne();
                    continue;
                }

                /* The running state sent (for greying only -- the request's   */
                /* own gate, run by the sequence, decides).                    */
                Sys_PublishState(objRequest);
                try
                {   /* The request's whole sequence.  An exception ends it as    */
                    /* a failure, never as a success, and never ends the loop.   */
                    objResult = Sys_Sequence(objRequest, new EXC_clsExecution(this, objRequest));
                    if (objResult == null)
                    {   /* A sequence with no result: a failure (every sequence   */
                        /* must return one).                                      */
                        objResult = new RSL_clsResult(SYS_INTERNAL, "A command returned no result.", null);
                    }
                }
                catch (Exception exCommand)
                {   /* An exception out of the sequence: a failure, so nothing   */
                    /* that needs the sequence's success runs after it.  The     */
                    /* whole exception, with its stack, goes to the debug log;   */
                    /* the result's detail, which the request's window shows,    */
                    /* is its message.                                           */
                    if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                    {   /* Only while the debug log is kept: otherwise the line is never built. */
                        DBGLOG_clsDebugLog.DBGLOG_Write("command", exCommand.ToString());
                    }

                    /* The sequence's failed result, its detail the exception's message. */
                    objResult = new RSL_clsResult(SYS_INTERNAL, exCommand.Message, null);
                }

                if (objRequest.RQT_enmKind == KND_enmKind.Exit && objResult.RSL_iStatus != SYS_OK)
                {   /* An Exit that was refused or failed: the program goes on,    */
                    /* so the agreed-exit flag is cleared BEFORE the failure is    */
                    /* published -- the UI may post again the moment it sees it.   */
                    lock (Sys_objQueue)
                    {   /* Under the lock SYS_Post reads it with. */
                        Sys_bTerminal = false;
                    }
                }

                /* The result's view: a save's goes to its editor as   */
                /* EditorSaved, every other as Result.                 */
                enmResultView = VWK_enmViewKind.Result;
                if (objRequest.RQT_enmKind == KND_enmKind.SaveEditor)
                {   /* A save: its editor acknowledges it. */
                    enmResultView = VWK_enmViewKind.EditorSaved;
                }

                /* The result published, then the request's stages released --   */
                /* in that order, so a result can be shown before its stage is   */
                /* gone; no result may need the stage afterwards.                */
                SYS_Show(new VWM_clsView(enmResultView, objRequest, objResult));
                Sys_Release(objRequest);
                lock (Sys_objQueue)
                {   /* The request ends only now, after its whole sequence and   */
                    /* its result.  The picker copy is set first, so another     */
                    /* thread never sees "nothing running" with a value-asking   */
                    /* picker still open; an image folder picker's listings      */
                    /* are follow-ups and are not held.                          */
                    Sys_bUserInputHeld = Sys_HasValueInput();
                    Sys_objRunning = null;
                    bStop = Sys_bTerminal && objRequest.RQT_enmKind == KND_enmKind.Exit && objResult.RSL_iStatus == SYS_OK;
                }

                /* The idle state sent.  Views arrive late, but each carries its   */
                /* serial and epoch, so none can stand in for the next request's   */
                /* gate.                                                           */
                Sys_PublishState(objRequest);
                if (bStop)
                {   /* The agreed Exit succeeded: the worker stops. */
                    break;
                }
            }

            /* Every request still waiting refused and its stages released,     */
            /* then Stopped -- after which the program may unload the engine;   */
            /* the UI never joins this thread.                                  */
            Sys_Drain();
            SYS_Show(new VWM_clsView(VWK_enmViewKind.Stopped, objRequest, objResult));
        }

        /*--------------------------------------------------------------------*/
        /* Sys_Drain:                                                         */
        /*                                                                    */
        /* End the queue: no more admissions, and every request still waiting */
        /* refused with its stages released.                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the queue empty and closed.                             */
        /*--------------------------------------------------------------------*/
        private void Sys_Drain()
        {
            RQT_clsRequest[] arrPending;
            RQT_clsRequest objRequest;
            int i;

            lock (Sys_objQueue)
            {   /* Stopped set and the queue emptied in one hold: a post    */
                /* after this is refused at once (SYS_Post's first test).   */
                Sys_bStopped = true;
                arrPending = Sys_lstWaiting.ToArray();
                Sys_lstWaiting.Clear();
            }

            /* The requests taken out, ended outside the lock. */
            for (i = 0; i < arrPending.Length; i++)
            {   /* One request per iteration: its stages, then its refusal. */
                objRequest = arrPending[i];
                Sys_Release(objRequest);
                SYS_Show(new VWM_clsView(VWK_enmViewKind.Refused, objRequest,
                    new RSL_clsResult(SYS_CANCELLED, "Exit stopped this pending request.", null)));
            }
        }

        /*--------------------------------------------------------------------*/
        /* Sys_CancelPending:                                                 */
        /*                                                                    */
        /* Cancel every waiting request but the notifications, once the user  */
        /* has agreed to stop pending work (CommandInterrupts.cs, and the     */
        /* Exit sequence, CommandSequences.cs) -- before the editors are      */
        /* asked to close, so no queued save is left waiting on an editor the */
        /* Exit is about to close.                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : each one's stages released and its cancel published.    */
        /*--------------------------------------------------------------------*/
        private void Sys_CancelPending()
        {
            List<RQT_clsRequest> lstCancelled;
            RQT_clsRequest objRequest;
            int i;

            /* The requests taken out under the lock, ended outside it.  The   */
            /* notifications stay: they must still be delivered.               */
            lstCancelled = new List<RQT_clsRequest>();
            lock (Sys_objQueue)
            {   /* Every waiting request but the notifications. */
                for (i = Sys_lstWaiting.Count - 1; i >= 0; i--)
                {   /* One waiting request per iteration, from the end, so       */
                    /* removing one does not move those still to be looked at.   */
                    if (SYS_Policy(Sys_lstWaiting[i].RQT_enmKind) != PRY_enmPriority.Notification)
                    {   /* Not a notification: the user's agreement covers it. */
                        lstCancelled.Add(Sys_lstWaiting[i]);
                        Sys_lstWaiting.RemoveAt(i);
                    }
                }
            }

            /* Back into posting order (the walk ran from the end), so the UI's   */
            /* counts of outstanding requests settle in order before the next     */
            /* question (an editor's close) is shown.                             */
            lstCancelled.Reverse();

            /* Each request ended: stages, then its one outcome. */
            for (i = 0; i < lstCancelled.Count; i++)
            {   /* One request per iteration. */
                objRequest = lstCancelled[i];
                Sys_Release(objRequest);
                SYS_Show(new VWM_clsView(VWK_enmViewKind.Refused, objRequest,
                    new RSL_clsResult(SYS_CANCELLED, "Exit stopped this pending operation.", null)));
            }
        }

        /*--------------------------------------------------------------------*/
        /* Sys_CancelEditorSaves:                                             */
        /*                                                                    */
        /* Cancel the saves queued from one editor, before that editor is     */
        /* asked Save / Discard / Cancel by a close (CommandSequences.cs):    */
        /* they could not run once the session is closed, and the editor      */
        /* would otherwise wait on them.  Its current content is what the     */
        /* question then offers to save.                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lEditor : the editor's id.                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : each save's stage released and its cancel published,    */
        /*            before the question.                                    */
        /*--------------------------------------------------------------------*/
        private void Sys_CancelEditorSaves(long lEditor)
        {
            List<RQT_clsRequest> lstCancelled;
            RQT_clsRequest objRequest;
            int i;

            /* That editor's queued saves taken out under the lock, every   */
            /* other request left in its order.                             */
            lstCancelled = new List<RQT_clsRequest>();
            lock (Sys_objQueue)
            {   /* Only SaveEditor requests from this editor. */
                for (i = Sys_lstWaiting.Count - 1; i >= 0; i--)
                {   /* One waiting request per iteration, from the end. */
                    if (Sys_lstWaiting[i].RQT_enmKind == KND_enmKind.SaveEditor && Sys_lstWaiting[i].RQT_lOrigin == lEditor)
                    {   /* One of its saves: cancelled. */
                        lstCancelled.Add(Sys_lstWaiting[i]);
                        Sys_lstWaiting.RemoveAt(i);
                    }
                }
            }

            /* Back into posting order, so the editor's counts settle in order   */
            /* before its question is shown.                                     */
            lstCancelled.Reverse();

            /* Each save ended outside the lock: stage, then outcome. */
            for (i = 0; i < lstCancelled.Count; i++)
            {   /* One save per iteration. */
                objRequest = lstCancelled[i];
                Sys_Release(objRequest);
                SYS_Show(new VWM_clsView(VWK_enmViewKind.Refused, objRequest,
                    new RSL_clsResult(SYS_CANCELLED, "The close decision superseded this queued save; current editor content is retained.", null)));
            }
        }
    }
}
