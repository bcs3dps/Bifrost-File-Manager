/*----------------------------------------------------------------------------*/
/* CommandClient.cs                                                           */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The UI's end of the command system: posting requests, and getting the      */
/* worker's views back onto the UI thread -- each one-shot completion to the  */
/* callback registered with its request, everything else to the main          */
/* window's renderer.  The worker never calls window code itself: views are   */
/* posted (BeginInvoke).  The one exception is a drag-out's extract, during   */
/* which the UI thread is held inside OLE's GetData: its progress and its     */
/* result go straight to a waiting channel (DWT_clsDragWait) instead.         */
/* Requests stay plain data; no callback or control goes into one.            */
/*----------------------------------------------------------------------------*/

/* The framework: the exceptions (System), the completion table               */
/* (Collections.Generic), the drag channel's event and atomics (Threading),   */
/* and Control / BeginInvoke (Windows.Forms).                                 */
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* CLI_Render:                                                            */
    /*                                                                        */
    /* The shape of the main window's view renderer, called on the UI thread  */
    /* for every view that is not a registered completion.                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objView : the view -- finished data, or a question's display.      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : shown; the renderer never decides a command's next step.    */
    /*------------------------------------------------------------------------*/
    internal delegate void CLI_Render(VWM_clsView objView);

    /*------------------------------------------------------------------------*/
    /* CLI_Reply:                                                             */
    /*                                                                        */
    /* The shape of a one-shot completion registered with a request.          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objRequest : the request it was registered with.                   */
    /*     objResult  : its result -- including a refusal or a dropped        */
    /*                  duplicate.                                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the window has taken the result; nothing runs after it      */
    /*            here.                                                       */
    /*------------------------------------------------------------------------*/
    internal delegate void CLI_Reply(RQT_clsRequest objRequest, RSL_clsResult objResult);

    /*------------------------------------------------------------------------*/
    /* CLI_InputFailure:                                                      */
    /*                                                                        */
    /* The shape of the input registry's clean-up for an input whose renderer */
    /* threw.                                                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objInput : the input.                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when no window of the input remains; false when its    */
    /*            close was refused (the input still stands).                 */
    /*------------------------------------------------------------------------*/
    internal delegate bool CLI_InputFailure(ICV_clsInputView objInput);

    /*------------------------------------------------------------------------*/
    /* CLI_InputRendered:                                                     */
    /*                                                                        */
    /* Settle an input after its renderer RETURNED normally.  Every           */
    /* input is shown modally inside its renderer, so by then the input       */
    /* has ended -- the registry makes sure the worker has been told,         */
    /* whether or not the renderer did it (one that forgot, or that           */
    /* claimed no input at all, would otherwise hold every request,           */
    /* Exit included, for good).                                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objInput : the input whose renderer has returned.                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the worker has been, or is now, told the input ended.       */
    /*------------------------------------------------------------------------*/
    internal delegate void CLI_InputRendered(ICV_clsInputView objInput);

    /*------------------------------------------------------------------------*/
    /* DWT_Progress:                                                          */
    /*                                                                        */
    /* The shape of a drag-out's progress sink: the extract's progress, sent  */
    /* to the drag's own feedback window (which runs its own message loop),   */
    /* since the main window is held in GetData.                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objProgress : the event's copied facts.                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : posted to the feedback window, without waiting on the main  */
    /*            UI thread.                                                  */
    /*------------------------------------------------------------------------*/
    internal delegate void DWT_Progress(PGD_clsProgressData objProgress);

    /*------------------------------------------------------------------------*/
    /* DWT_Posted:                                                            */
    /*                                                                        */
    /* The shape of the drag's call made once its extract has been posted     */
    /* (and has its serial) -- the feedback window then enables its Cancel.   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : set up; no work starts.                                     */
    /*------------------------------------------------------------------------*/
    internal delegate void DWT_Posted();

    /*------------------------------------------------------------------------*/
    /* DWT_clsDragWait -- the direct channel for one drag-out's extract: the  */
    /* UI thread, held inside OLE's GetData, waits on its event, and the      */
    /* worker's views for that one request -- progress to the feedback        */
    /* window, the result to the waiter -- reach it without the blocked main  */
    /* dispatcher -- documentation table.                                     */
    /*------------------------------------------------------------------------*/
    internal sealed class DWT_clsDragWait
    {
        /* Kept apart from the request itself, which stays plain data. */
        private readonly SYS_clsSystem Dwt_objSystem;    // the command system, used only to post a Cancel for this extract
        private readonly RQT_clsRequest Dwt_objRequest;  // the extract's own request -- matched by reference, not by serial
        private readonly DWT_Progress Dwt_pfnProgress;   // the feedback window's progress sink; null when there is none
        private readonly ManualResetEvent Dwt_evtDone;   // set once the result is stored; the GetData caller waits on it
        private RSL_clsResult Dwt_objResult;             // the result; null until stored, read only after the event is set
        private string Dwt_sFailure;                     // the first feedback failure's reason; null until then (set atomically: the UI and the worker may both report one)
        private int Dwt_iTerminal;                       // 1 once a result has been taken (set atomically), so a late second one never touches the event

        /*--------------------------------------------------------------------*/
        /* DWT_clsDragWait:                                                   */
        /*                                                                    */
        /* The channel for one extract, made before the request is posted.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSystem   : the command system.                              */
        /*     objRequest  : the extract's request (KND_enmKind.DragExtract), */
        /*                   not yet posted.                                  */
        /*     pfnProgress : the progress sink, or null; it must never wait   */
        /*                   on the main UI thread.                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the channel, no result yet; its maker keeps it */
        /*                     until the result arrives.                      */
        /*--------------------------------------------------------------------*/
        internal DWT_clsDragWait(SYS_clsSystem objSystem, RQT_clsRequest objRequest, DWT_Progress pfnProgress)
        {
            /* The parts, held by the client only -- nothing goes into the   */
            /* request -- and the event, not set.                            */
            Dwt_objSystem = objSystem;
            Dwt_objRequest = objRequest;
            Dwt_pfnProgress = pfnProgress;
            Dwt_evtDone = new ManualResetEvent(false);
        }

        /*--------------------------------------------------------------------*/
        /* DWT_FailFeedback:                                                  */
        /*                                                                    */
        /* The drag's feedback failed (its set-up, or a progress post): keep  */
        /* the reason and ask the extract to stop.  Only the first failure    */
        /* counts.                                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     exFailure : the failure.                                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the reason kept and a Cancel posted; the wait still     */
        /*            ends only when the extract has really stopped.          */
        /*--------------------------------------------------------------------*/
        internal void DWT_FailFeedback(Exception exFailure)
        {
            if (Interlocked.CompareExchange(ref Dwt_sFailure,
                "Drag feedback failed: " + exFailure.Message, null) != null)
            {   /* A failure is already kept (and its Cancel posted): this   */
                /* one adds nothing.                                         */
                return;
            }

            /* Logged whole, and a Cancel for this extract's serial posted.     */
            /* This can run on the UI thread (set-up) or the worker (a          */
            /* progress post); SYS_Post is safe from either, and the client's   */
            /* completion table, which is the UI thread's alone, is not used.   */
            if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
            {   /* Only while the debug log is kept: otherwise the line is never built. */
                DBGLOG_clsDebugLog.DBGLOG_Write("drag feedback", exFailure.ToString());
            }

            /* The Cancel for this extract's serial, posted to the worker. */
            Dwt_objSystem.SYS_Post(CFC_clsCommands.CFC_Create(KND_enmKind.Cancel, 0,
                null, null, null, Dwt_objRequest.RQT_lSerial, 0, null, "", ""));
        }

        /*--------------------------------------------------------------------*/
        /* DWT_Forward:                                                       */
        /*                                                                    */
        /* Take a view if it is this extract's progress or result; leave any  */
        /* other to the ordinary route.  Called by CLI_Publish, on whatever   */
        /* thread published (usually the worker).                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objView : the view.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when taken here; false otherwise.                  */
        /*--------------------------------------------------------------------*/
        internal bool DWT_Forward(VWM_clsView objView)
        {
            if (objView.VWM_objRequest != Dwt_objRequest)
            {   /* Another request's view: not this channel's. */
                return(false);
            }

            if (objView.VWM_enmKind == VWK_enmViewKind.Progress)
            {   /* This extract's progress: to the feedback window. */
                if (Dwt_pfnProgress != null)
                {   /* A sink exists: forwarded -- and a failure must not   */
                    /* leave the GetData caller waiting for ever.           */
                    try
                    {   /* The copied facts to the feedback window. */
                        Dwt_pfnProgress((PGD_clsProgressData)objView.VWM_objValue);
                    }
                    catch (Exception exProgress)
                    {   /* Failed: the extract is asked to stop; the wait   */
                        /* still ends with its real result.                 */
                        DWT_FailFeedback(exProgress);
                    }
                }

                /* Taken (even with no sink): the main window cannot show it   */
                /* while it is held in GetData.                                */
                return(true);
            }

            if (objView.VWM_enmKind == VWK_enmViewKind.Result || objView.VWM_enmKind == VWK_enmViewKind.Refused ||
                objView.VWM_enmKind == VWK_enmViewKind.Dropped)
            {   /* The extract's end -- its result, or a refusal at posting   */
                /* (published while SYS_Post runs): either releases the       */
                /* waiter.                                                    */
                if (Interlocked.CompareExchange(ref Dwt_iTerminal, 1, 0) == 0)
                {   /* The first end only: the result stored, THEN the    */
                    /* event set, so the waiter reads it whole.  A late   */
                    /* second end never touches the (by then perhaps      */
                    /* disposed) event.                                   */
                    Dwt_objResult = objView.VWM_objValue as RSL_clsResult;
                    if (Dwt_objResult == null)
                    {   /* No result in the view: a failure, never an empty   */
                        /* success.                                           */
                        Dwt_objResult = new RSL_clsResult(SYS_clsSystem.SYS_INTERNAL, "Drag extraction returned no result.", null);
                    }

                    /* The waiter released. */
                    Dwt_evtDone.Set();
                }

                /* Taken: never left for the blocked main dispatcher. */
                return(true);
            }

            /* Any other view of this request (state, a question): the   */
            /* ordinary route.                                           */
            return(false);
        }

        /*--------------------------------------------------------------------*/
        /* DWT_Wait:                                                          */
        /*                                                                    */
        /* The GetData caller's wait for the extract's result -- the one wait */
        /* the main UI thread ever makes on the worker, allowed because the   */
        /* extract can ask no question and its views come here directly.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the result; SYS_INTERNAL with the feedback     */
        /*                     failure's reason when the feedback failed,     */
        /*                     even if the extract finished.                  */
        /*--------------------------------------------------------------------*/
        internal RSL_clsResult DWT_Wait()
        {
            /* Until the result is stored and the event set. */
            Dwt_evtDone.WaitOne();
            if (Dwt_sFailure != null)
            {   /* The feedback failed: reported as a failure -- with what   */
                /* the extract produced kept as its payload.                 */
                return(new RSL_clsResult(SYS_clsSystem.SYS_INTERNAL, Dwt_sFailure, Dwt_objResult.RSL_objPayload));
            }

            /* The result, stored before the event was set. */
            return(Dwt_objResult);
        }

        /*--------------------------------------------------------------------*/
        /* DWT_Release:                                                       */
        /*                                                                    */
        /* Release the channel's event, once the client has removed the       */
        /* channel.                                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the event disposed.                                     */
        /*--------------------------------------------------------------------*/
        public void DWT_Release()
        {
            /* Called after the result arrived (or the post threw before the    */
            /* request was taken); the client has already removed the channel   */
            /* from CLI_Publish's path.                                         */
            Dwt_evtDone.Dispose();
        }
    }

    /*------------------------------------------------------------------------*/
    /* CLI_clsClient -- the UI's command client (one, made by the main        */
    /* window's start-up): it owns the command system, registers each         */
    /* request's completion, and brings the worker's views onto the UI        */
    /* thread -- documentation table.                                         */
    /*------------------------------------------------------------------------*/
    internal sealed class CLI_clsClient
    {
        /* The UI side's parts; what may run stays the worker's to decide. */
        private readonly Control Cli_ctlOwner;                                   // the control views are posted to (BeginInvoke); the worker never calls it directly
        private readonly CLI_Render Cli_pfnRender;                               // the main window's renderer
        private readonly Dictionary<RQT_clsRequest, CLI_Reply> Cli_dictReplies;  // each request's one-shot completion, by request; UI thread only
        private readonly SYS_clsSystem Cli_objSystem;                            // the command system (one worker), made here and started once the owner's handle exists
        private STV_clsStateView Cli_objState;                                   // the last state the worker published, as delivered to the UI
        private long Cli_lAuxiliary;                                             // the last window id handed out: 0, then -1, -2 ... (pickers, Windows-file editors)
        private volatile DWT_clsDragWait Cli_objDragWait;                        // the drag-out's channel while its extract runs; null otherwise (read from the worker, hence volatile)
        private CLI_InputFailure Cli_pfnInputFailure;                            // the input registry's clean-up for a renderer that threw; null until bound
        private CLI_InputRendered Cli_pfnInputRendered;                          // the input registry's settlement for a renderer that returned; null until bound

        /*--------------------------------------------------------------------*/
        /* CLI_clsClient:                                                     */
        /*                                                                    */
        /* The client, and the command system it owns (not yet started).      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     ctlOwner      : the control to post views to; its window must  */
        /*                     exist before the worker starts.                */
        /*     objSteps      : the step table the worker runs.                */
        /*     pfnRender     : the main window's renderer.                    */
        /*     bRiskWarnings : the first-change warning option's starting     */
        /*                     value.                                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the client, its state "nothing open"; the      */
        /*                     ArgumentNullException without an owner or a    */
        /*                     renderer.                                      */
        /*--------------------------------------------------------------------*/
        internal CLI_clsClient(Control ctlOwner, STB_clsStepTable objSteps, CLI_Render pfnRender, bool bRiskWarnings)
        {
            if (ctlOwner == null)
            {   /* Nothing to post views to: no question could be shown,   */
                /* no save answered.  Refused.                             */
                throw new ArgumentNullException("ctlOwner");
            }

            if (pfnRender == null)
            {   /* No renderer: a question could never be answered.   */
                /* Refused.                                           */
                throw new ArgumentNullException("pfnRender");
            }

            /* The owner, the renderer and the empty completion table, all   */
            /* the UI thread's.  The worker is given one publication call    */
            /* (CLI_Publish) and nothing else of the UI.                     */
            Cli_ctlOwner = ctlOwner;
            Cli_pfnRender = pfnRender;
            Cli_dictReplies = new Dictionary<RQT_clsRequest, CLI_Reply>();

            /* The starting state (nothing open) and the command system, not   */
            /* started -- the start-up installs every renderer and registry    */
            /* first.                                                          */
            Cli_objState = new STV_clsStateView(MOD_enmMode.None, null, 0, false, false, false);
            Cli_objSystem = new SYS_clsSystem(objSteps, CLI_Publish, bRiskWarnings);
        }

        /*--------------------------------------------------------------------*/
        /* CLI_ReadState:                                                     */
        /*                                                                    */
        /* The last state the worker published.  A hint for the windows (what */
        /* to enable, what to capture into a request); the worker's own gate  */
        /* decides what runs.                                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     STV_clsStateView : the state record itself (not a copy; it is  */
        /*                        never changed).                             */
        /*--------------------------------------------------------------------*/
        internal STV_clsStateView CLI_ReadState()
        {
            /* One reference: a whole, consistent record. */
            return(Cli_objState);
        }

        /*--------------------------------------------------------------------*/
        /* CLI_ReadSystem:                                                    */
        /*                                                                    */
        /* The command system, for the start-up (to start it) and the drag    */
        /* feedback (to post its Cancel).                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     SYS_clsSystem : the command system (its calls are thread-safe; */
        /*                     its queue is private).                         */
        /*--------------------------------------------------------------------*/
        internal SYS_clsSystem CLI_ReadSystem()
        {
            /* The drag feedback posts its Cancel through this, without the   */
            /* client's completion table, while the main thread is blocked.   */
            return(Cli_objSystem);
        }

        /*--------------------------------------------------------------------*/
        /* CLI_RegisterInputFailure:                                          */
        /*                                                                    */
        /* Bind the input registry's clean-up for a renderer that threw --    */
        /* once, before the worker starts.  Registering calls nothing.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pfnFailure : the clean-up.                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when bound; false when null or already bound.      */
        /*--------------------------------------------------------------------*/
        internal bool CLI_RegisterInputFailure(CLI_InputFailure pfnFailure)
        {
            if (pfnFailure == null || Cli_pfnInputFailure != null)
            {   /* Missing, or bound already: a second registry would lose   */
                /* the inputs the first one holds.                           */
                return(false);
            }

            /* Bound, before any input can be shown. */
            Cli_pfnInputFailure = pfnFailure;

            /* The registry now cleans up after a renderer that throws. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* CLI_RegisterInputRendered:                                         */
        /*                                                                    */
        /* Bind the one "renderer returned" settlement, beside the failure    */
        /* cleanup above and by the same registry, before the worker          */
        /* starts.  Registration calls nothing.                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pfnRendered : the registry's settlement; required, bound       */
        /*                   once.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when installed; false when missing or already      */
        /*            bound (a second registry would split input ownership).  */
        /*--------------------------------------------------------------------*/
        internal bool CLI_RegisterInputRendered(CLI_InputRendered pfnRendered)
        {
            if (pfnRendered == null || Cli_pfnInputRendered != null)
            {   /* Missing, or another registry already settles inputs. */
                return(false);
            }

            /* Installed before any input can be published. */
            Cli_pfnInputRendered = pfnRendered;

            /* The registry now settles every input whose renderer returns. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* CLI_FailInput:                                                     */
        /*                                                                    */
        /* After an input's renderer threw: ask the registry whether any of   */
        /* its windows remain.                                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objInput : the input.                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when none remains (the input may be ended); false  */
        /*            when one does, or when no registry is bound.            */
        /*--------------------------------------------------------------------*/
        internal bool CLI_FailInput(ICV_clsInputView objInput)
        {
            if (Cli_pfnInputFailure == null)
            {   /* No registry: nothing can say the input is gone, so it is   */
                /* left standing.                                             */
                return(false);
            }

            /* The registry, which owns the input's windows, answers. */
            return(Cli_pfnInputFailure(objInput));
        }

        /*--------------------------------------------------------------------*/
        /* CLI_NewWindowId:                                                   */
        /*                                                                    */
        /* A new id for a window of the UI's own (a picker, a Windows-file    */
        /* editor) -- a number, not tied to any window handle.                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     long : -1, -2, ... -- negative, so never one of the worker's   */
        /*            positive serials (which name image viewers and modes).  */
        /*--------------------------------------------------------------------*/
        internal long CLI_NewWindowId()
        {
            /* The next negative number (checked: it cannot wrap back to a   */
            /* positive one).                                                */
            return(checked(--Cli_lAuxiliary));
        }

        /*--------------------------------------------------------------------*/
        /* CLI_Post:                                                          */
        /*                                                                    */
        /* Post a request, registering its completion first.  Returns at      */
        /* once -- it waits for no step, question or result.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest     : the request (it, and any staged file in it,   */
        /*                      become the worker's).                         */
        /*     pfnDone        : its completion, or null for none.             */
        /*     bUserInitiated : true for fresh user work (a pane or menu      */
        /*                      command), which the worker may refuse while   */
        /*                      busy or exiting; optional, false by default   */
        /*                      (continuations of work already accepted).     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when accepted into the queue; the result comes     */
        /*            later, through the completion either way.               */
        /*--------------------------------------------------------------------*/
        internal bool CLI_Post(RQT_clsRequest objRequest, CLI_Reply pfnDone, bool bUserInitiated = false)
        {
            if (pfnDone != null)
            {   /* Registered BEFORE the post: a refused request is   */
                /* answered at once, while SYS_Post runs.             */
                Cli_dictReplies.Add(objRequest, pfnDone);
            }

            /* Posted; the caller goes on. */
            return(Cli_objSystem.SYS_Post(objRequest, bUserInitiated));
        }

        /*---------------------------------------------------------------------*/
        /* CLI_ExtractDrag:                                                    */
        /*                                                                     */
        /* A drag-out's extract, posted and WAITED for on the UI thread (OLE's */
        /* GetData must return the files).  Its progress and result come       */
        /* through a channel of their own, since the main dispatcher cannot    */
        /* run meanwhile.                                                      */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     objRequest  : a new DragExtract request, holding its drag       */
        /*                   reservation token.                                */
        /*     pfnProgress : the feedback window's progress sink, or null --   */
        /*                   never a call into the main window.                */
        /*     pfnPosted   : the feedback's set-up once posted (enabling its   */
        /*                   Cancel), or null.                                 */
        /*                                                                     */
        /* Returns:                                                            */
        /*     RSL_clsResult : the extract's result, a refusal included --     */
        /*                     being accepted alone gives no files.            */
        /*---------------------------------------------------------------------*/
        internal RSL_clsResult CLI_ExtractDrag(RQT_clsRequest objRequest, DWT_Progress pfnProgress, DWT_Posted pfnPosted)
        {
            DWT_clsDragWait objWait;

            if (objRequest == null || objRequest.RQT_enmKind != KND_enmKind.DragExtract ||
                objRequest.RQT_lSerial != 0 || Cli_objDragWait != null)
            {   /* Not a new drag extract, or a channel already in use:     */
                /* refused (a fault in the caller).  Whether the extract    */
                /* may run at all is still the worker's gate's to decide.   */
                throw new ArgumentException("A fresh reserved extraction needs its own direct result channel.");
            }

            /* The channel installed BEFORE the post (volatile, so the worker   */
            /* sees it): a refusal is published while SYS_Post runs, and must   */
            /* already find it.                                                 */
            objWait = new DWT_clsDragWait(Cli_objSystem, objRequest, pfnProgress);
            Cli_objDragWait = objWait;
            try
            {   /* Posted, set up, then waited for; the finally removes the   */
                /* channel.                                                   */
                Cli_objSystem.SYS_Post(objRequest);
                if (pfnPosted != null)
                {   /* The feedback's set-up, now the request has its   */
                    /* serial (so its Cancel names the right run).      */
                    try
                    {   /* It must start nothing and not run the main loop. */
                        pfnPosted();
                    }
                    catch (Exception exPosted)
                    {   /* Set-up failed: the extract, already accepted, is   */
                        /* asked to stop, and is still waited for below.      */
                        objWait.DWT_FailFeedback(exPosted);
                    }
                }

                /* The wait, until the worker's result reaches the channel   */
                /* (CLI_Publish -> DWT_Forward).                             */
                return(objWait.DWT_Wait());
            }
            finally
            {   /* The channel removed and its event released.  Normally the   */
                /* result has arrived by now; if the post itself threw (a      */
                /* serial that would overflow), the request was never taken.   */
                Cli_objDragWait = null;
                objWait.DWT_Release();
            }
        }

        /*--------------------------------------------------------------------*/
        /* CLI_RegisterReply:                                                 */
        /*                                                                    */
        /* Register a completion for a request that is handed over INSIDE a   */
        /* question's answer (an editor's staged Save, a chosen Windows       */
        /* target), not posted.                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : that request.                                     */
        /*     pfnDone    : its completion.                                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : registered.                                             */
        /*--------------------------------------------------------------------*/
        internal void CLI_RegisterReply(RQT_clsRequest objRequest, CLI_Reply pfnDone)
        {
            /* Registered BEFORE the answer is given: the waiting worker may   */
            /* publish the result the moment it accepts the answer.            */
            Cli_dictReplies.Add(objRequest, pfnDone);
        }

        /*--------------------------------------------------------------------*/
        /* CLI_AbandonReply:                                                  */
        /*                                                                    */
        /* Remove such a completion when the answer carrying its request was  */
        /* refused (no longer the question's current display).                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the request.                                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : removed; the caller releases the request's staged file  */
        /*            itself, or posts the request properly.                  */
        /*--------------------------------------------------------------------*/
        internal void CLI_AbandonReply(RQT_clsRequest objRequest)
        {
            /* The refused answer handed nothing to the worker. */
            Cli_dictReplies.Remove(objRequest);
        }

        /*--------------------------------------------------------------------*/
        /* CLI_Publish:                                                       */
        /*                                                                    */
        /* The worker's one publication call: a view is posted to the UI      */
        /* thread -- or, when it is the drag extract's progress or result,    */
        /* given straight to its channel.  It never waits on any thread.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objView : the view.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when posted or taken by the channel; false when    */
        /*            the UI can no longer be reached.                        */
        /*--------------------------------------------------------------------*/
        public bool CLI_Publish(VWM_clsView objView)
        {
            DWT_clsDragWait objDrag;

            /* While a drag extract runs, the UI thread is held in GetData and   */
            /* runs nothing posted to it: that request's progress and result     */
            /* go to its channel instead.  Everything else takes the ordinary    */
            /* route.                                                            */
            objDrag = Cli_objDragWait;
            if (objDrag != null && objDrag.DWT_Forward(objView))
            {   /* Taken by the channel; no UI code ran here. */
                return(true);
            }

            try
            {   /* Posted to the owner's thread (BeginInvoke, never Invoke)   */
                /* -- no window code runs on the worker's stack.  It throws   */
                /* once the owner's window is gone (being disposed).          */
                Cli_ctlOwner.BeginInvoke(new CLI_Render(Cli_Dispatch), new object[] { objView });

                /* Posted: the UI thread shows it. */
                return(true);
            }
            catch (InvalidOperationException)
            {   /* No window to post to (ObjectDisposedException is one of   */
                /* these too): false -- the worker then cancels a question   */
                /* that could never be answered.                             */
                return(false);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Cli_Dispatch:                                                      */
        /*                                                                    */
        /* A view, on the UI thread: the state recorded; a question's popup   */
        /* closed; a result handed to its request's completion; anything else */
        /* to the renderer -- and an input settled once its renderer returns. */
        /* A failure while showing it is contained here.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objView : the view.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : handled; a failure answers its question Cancel and ends */
        /*            its input if no window of it remains.                   */
        /*--------------------------------------------------------------------*/
        private void Cli_Dispatch(VWM_clsView objView)
        {
            CLI_Reply pfnDone;
            PRS_clsPresentation objPresentation;
            ICV_clsInputView objInput;

            if (objView.VWM_enmKind == VWK_enmViewKind.State)
            {   /* A new state: recorded first, so the renderer and anything   */
                /* it starts see it.  (It goes on to the renderer as well.)    */
                Cli_objState = (STV_clsStateView)objView.VWM_objValue;
            }

            try
            {   /* Everything below, so a failure in a completion or a    */
                /* renderer never reaches WinForms as an unhandled one.   */
                if (objView.VWM_enmKind == VWK_enmViewKind.QuestionClose)
                {   /* The worker has already ended the question: its popup   */
                    /* is closed (a late reply is refused).                   */
                    QWN_clsQuestionWindows.QWN_Supersede((QST_clsQuestion)objView.VWM_objValue);
                    return;
                }

                if (objView.VWM_objRequest != null &&
                    (objView.VWM_enmKind == VWK_enmViewKind.Result || objView.VWM_enmKind == VWK_enmViewKind.Refused ||
                    objView.VWM_enmKind == VWK_enmViewKind.Dropped || objView.VWM_enmKind == VWK_enmViewKind.EditorSaved) &&
                    Cli_dictReplies.TryGetValue(objView.VWM_objRequest, out pfnDone))
                {   /* A request's end, with a completion registered: removed   */
                    /* FIRST -- the completion may open a modal window, whose   */
                    /* loop could deliver views meanwhile -- then called.       */
                    Cli_dictReplies.Remove(objView.VWM_objRequest);
                    pfnDone(objView.VWM_objRequest, (RSL_clsResult)objView.VWM_objValue);
                    return;
                }

                /* Anything else to the renderer, which only shows it. */
                Cli_pfnRender(objView);

                if (objView.VWM_enmKind == VWK_enmViewKind.InputOpen && Cli_pfnInputRendered != null)
                {   /* An input's renderer has returned, so its modal    */
                    /* input has ended: the registry makes sure the      */
                    /* worker knows it, even when the renderer did not   */
                    /* say so (or no renderer claimed it).  A failure    */
                    /* in that settlement reaches the catch below,       */
                    /* whose input cleanup is the same release.          */
                    Cli_pfnInputRendered((ICV_clsInputView)objView.VWM_objValue);
                }
            }
            catch (Exception exView)
            {   /* Showing it failed (building or drawing a window, say).   */
                /* What follows keeps the worker from waiting for ever.     */
                objPresentation = objView.VWM_objValue as PRS_clsPresentation;
                if (objPresentation != null)
                {   /* A question that could not be shown: answered Cancel. */
                    objPresentation.PRS_objQuestion.QST_Reply(objPresentation, new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null));
                }

                /* An editor is not closed by a failure here -- only its own    */
                /* window's end says so.  An input whose renderer failed is     */
                /* ended, once its registry confirms no window of it remains.   */
                if (objView.VWM_enmKind == VWK_enmViewKind.InputOpen)
                {   /* The input's renderer failed: asked of the registry. */
                    objInput = (ICV_clsInputView)objView.VWM_objValue;
                    if (CLI_FailInput(objInput))
                    {   /* None left: the worker told the input closed, so   */
                        /* it holds nothing back for it.                     */
                        Cli_objSystem.SYS_Post(CFC_clsCommands.CFC_Create(KND_enmKind.InputClosed,
                            objInput.ICV_objArgs.INA_lWindow, null, null, null, 0, 0, null, "", ""));
                    }
                }

                /* Logged whole (the debug log only); no command step follows. */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("command view", exView.ToString());
                }
            }
        }
    }
}
