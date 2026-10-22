/*----------------------------------------------------------------------------*/
/* CommandNativeControl.cs                                                    */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The managed side of the engine's callbacks during a command's copy or      */
/* check, on the worker: each event's strings and numbers are copied out of   */
/* the engine's memory at once, then counted into the result, asked about     */
/* (a destination that exists, a manifest-named file) through the command's   */
/* question channel, or published as progress.  A managed exception in a      */
/* callback is never let through the engine: it is caught, kept as the        */
/* failure, and the engine is told to cancel (ENG_REPLY_CANCEL).              */
/*----------------------------------------------------------------------------*/

/* The framework: IntPtr, Environment and the exceptions (System), the       */
/* copy's note list (Collections.Generic), and the reading of the engine's   */
/* event structures (InteropServices).                                       */
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* PGD_clsProgressData -- one engine event's facts, copied, for a view    */
    /* (Progress) or a question (a conflict, a manifest clash); nothing in it */
    /* points into engine memory -- documentation table.                      */
    /*------------------------------------------------------------------------*/
    internal sealed class PGD_clsProgressData
    {
        internal bool PGD_bCheck;     // true: a check's (e2fsck's) event; false: a copy's, a raw operation's or a capacity test's
        internal uint PGD_uiKind;     // the event's kind (ENG_EVENT_* for a copy, ENG_CHECK_EVENT_* for a check)
        internal string PGD_sImage;   // the image path, a raw phase's label, or a check's report line
        internal string PGD_sHost;    // the Windows path; "" when the event has none
        internal ulong PGD_ullDone;   // bytes done (a copy) or the pass's count so far (a check)
        internal ulong PGD_ullTotal;  // the total in the same unit; 0 when unknown
        internal uint PGD_uiPass;     // a check's pass (1 to 5, 0 at a pass's end); 0 for a copy
        internal int PGD_iStatus;     // the event's status, for a conflict or clash question; 0 in a progress view
        internal int PGD_iCopied;     // items copied so far; 0 for a check
        internal int PGD_iProblems;   // items skipped or failed so far; 0 for a check

        /*---------------------------------------------------------------------*/
        /* PGD_clsProgressData:                                                */
        /*                                                                     */
        /* The copied facts of one event.                                      */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     bCheck    : true for a check's event.                           */
        /*     uiKind    : the event's kind.                                   */
        /*     sImage    : the image path, phase label or report line,         */
        /*                 already copied; null taken as "".                   */
        /*     sHost     : the Windows path, already copied; null taken as "". */
        /*     ullDone   : done so far.                                        */
        /*     ullTotal  : the total, 0 when unknown.                          */
        /*     uiPass    : a check's pass, 0 for a copy.                       */
        /*     iStatus   : the event's status (0 for progress).                */
        /*     iCopied   : items copied so far.                                */
        /*     iProblems : items skipped or failed so far.                     */
        /*                                                                     */
        /* Returns:                                                            */
        /*     (constructor) : the facts, safe to keep after the engine's      */
        /*                     event memory is gone.                           */
        /*---------------------------------------------------------------------*/
        internal PGD_clsProgressData(bool bCheck, uint uiKind, string sImage, string sHost,
            ulong ullDone, ulong ullTotal, uint uiPass, int iStatus, int iCopied, int iProblems)
        {
            /* The numbers as given.  The strings were copied from the engine   */
            /* by the caller, so they are kept as they are -- and no live       */
            /* result object is handed to the window, only these values.        */
            PGD_bCheck = bCheck;
            PGD_uiKind = uiKind;
            PGD_ullDone = ullDone;
            PGD_ullTotal = ullTotal;
            PGD_uiPass = uiPass;
            PGD_iStatus = iStatus;
            PGD_iCopied = iCopied;
            PGD_iProblems = iProblems;

            /* The two texts, "" unless given. */
            PGD_sImage = "";
            PGD_sHost = "";
            if (sImage != null)
            {   /* The image path, label or line, as copied. */
                PGD_sImage = sImage;
            }

            if (sHost != null)
            {   /* The Windows path, as copied. */
                PGD_sHost = sHost;
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* NCB_clsNativeControl -- the callback handling for one command's copy   */
    /* or check, made by the copy and check controls (EngineSession.cs) and   */
    /* the extraction (CommandNativeOperations.cs).  It runs on the worker,   */
    /* inside the engine's call, and holds the result being filled, the       */
    /* conflict answer that applies to the rest of the copy, the progress     */
    /* rate limit, and the first callback failure -- documentation table.     */
    /*------------------------------------------------------------------------*/
    internal sealed class NCB_clsNativeControl
    {
        /* How the next destination that exists is answered: asked, or     */
        /* settled by an earlier "All" answer, for the rest of this copy   */
        /* (all its engine calls) -- documentation table.                  */
        private enum Ncb_enmConflictPolicy
        {
            Ask = 0,           // no "All" answer yet: each conflict is asked about
            OverwriteAll = 1,  // "Overwrite All" was answered: every later conflict is overwritten
            SkipAll = 2        // "Skip All" was answered: every later conflict is skipped without a question
        }

        /* The bindings, fixed for the command's life, and the state that   */
        /* changes as events arrive.                                        */
        private readonly EXC_clsExecution Ncb_objExecution;  // the running command's services; not kept by the engine past the call
        private readonly ECR_clsCopyResult Ncb_objCopy;      // the copy's result, counted into here (worker only); null for a check
        private readonly ECK_clsCheckResult Ncb_objCheck;    // the check's result, its report lines added here; null for a copy
        private Ncb_enmConflictPolicy Ncb_enmPolicy;         // the conflict answer in force; Ask to begin with
        private int Ncb_iLastTick;                           // Environment.TickCount when progress was last published (ms)
        private string Ncb_sFailure;                         // the first callback failure's text; null when there has been none

        /*--------------------------------------------------------------------*/
        /* NCB_clsNativeControl:                                              */
        /*                                                                    */
        /* The handling for one command's copy or check.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objExecution : the running command's services.                 */
        /*     objCopy      : the copy's result, or null.                     */
        /*     objCheck     : the check's result, or null.                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the handling -- no lock, no window, no thread; */
        /*                     it only runs inside the engine's calls.        */
        /*--------------------------------------------------------------------*/
        internal NCB_clsNativeControl(EXC_clsExecution objExecution,
            ECR_clsCopyResult objCopy, ECK_clsCheckResult objCheck)
        {
            /* The bindings, for the whole command: a copy of several items   */
            /* makes several engine calls with this same handling, so one     */
            /* "All" answer covers them all.  The results are changed only    */
            /* here, on the worker.  Then: asking, no progress yet, no        */
            /* failure.                                                       */
            Ncb_objExecution = objExecution;
            Ncb_objCopy = objCopy;
            Ncb_objCheck = objCheck;
            Ncb_enmPolicy = Ncb_enmConflictPolicy.Ask;
            Ncb_iLastTick = 0;
            Ncb_sFailure = null;
        }

        /*--------------------------------------------------------------------*/
        /* NCB_ReadFailure:                                                   */
        /*                                                                    */
        /* Whether a callback failed: the CANCEL the engine was told to do    */
        /* was then the program's own failure, not the user's cancel.  Read   */
        /* by the callers once the engine's call has returned.                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sFailure : receives the failure's text; "" when there was      */
        /*                none.                                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when a callback failed; false otherwise (the       */
        /*            engine's own status then stands).                       */
        /*--------------------------------------------------------------------*/
        internal bool NCB_ReadFailure(out string sFailure)
        {
            /* The text, "" unless a failure was kept. */
            sFailure = "";
            if (Ncb_sFailure != null)
            {   /* A callback failed: its text, and true. */
                sFailure = Ncb_sFailure;
                return(true);
            }

            /* No callback failure: the engine's status is the outcome. */
            return(false);
        }

        /*--------------------------------------------------------------------*/
        /* NCB_RecordFailure:                                                 */
        /*                                                                    */
        /* Keep a callback's failure -- the first only; the cancels that      */
        /* follow it must not replace it.  Called from this class's catches   */
        /* and by the extraction's own callback (CommandNativeOperations.cs). */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sFailure : the failure's text; null is replaced by a sentence  */
        /*                saying there was none.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the first failure kept.                                 */
        /*--------------------------------------------------------------------*/
        internal void NCB_RecordFailure(string sFailure)
        {
            if (Ncb_sFailure != null)
            {   /* One is kept already: this later one (usually a result   */
                /* of the first) is dropped.                               */
                return;
            }

            /* The text given -- even an empty one -- or, for none, a       */
            /* sentence saying the callback failed without a description.   */
            Ncb_sFailure = "Native callback failed without a failure description.";
            if (sFailure != null)
            {   /* Given: kept as it is. */
                Ncb_sFailure = sFailure;
            }
        }

        /*--------------------------------------------------------------------*/
        /* NCB_Continue:                                                      */
        /*                                                                    */
        /* Whether the engine may go on: not after a callback has failed, and */
        /* not after a Cancel or a confirmed Exit (the command's checkpoint,  */
        /* where a pending Exit is asked about).                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true to go on; false to cancel.                         */
        /*--------------------------------------------------------------------*/
        internal bool NCB_Continue()
        {
            if (Ncb_sFailure != null)
            {   /* A callback has failed: the engine is not let continue on   */
                /* a later event.                                             */
                return(false);
            }

            try
            {   /* The command's checkpoint (SYS_Checkpoint) -- it may hold   */
                /* the engine here while the Exit question is answered.       */
                return(Ncb_objExecution.EXC_Checkpoint());
            }
            catch (Exception exCallback)
            {   /* The checkpoint threw: kept as the failure, and false --      */
                /* an exception must never unwind through the engine.  The      */
                /* whole exception goes to the debug log; only its message      */
                /* is kept, since it becomes the step's failure detail, which   */
                /* the user is shown (Nst_Copy's SYS_INTERNAL,                  */
                /* CommandNativeOperations.cs).                                 */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("native callback", exCallback.ToString());
                }

                /* Kept as the failure: the message alone, as the user is shown it. */
                NCB_RecordFailure(exCallback.Message);
                return(false);
            }
        }

        /*--------------------------------------------------------------------*/
        /* NCB_Progress:                                                      */
        /*                                                                    */
        /* Publish a copy event as progress, with the copy's counts: a PLAIN  */
        /* progress event at most once per ECO_PROGRESS_MS, every other kind  */
        /* (an item's start or end, a raw phase) at once.                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sItem    : the image path or the phase's label, copied.        */
        /*     uiKind   : the event's kind.                                   */
        /*     ullDone  : bytes done.                                         */
        /*     ullTotal : bytes in all, 0 when unknown.                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : published, or skipped as too soon.                      */
        /*--------------------------------------------------------------------*/
        internal void NCB_Progress(string sItem, uint uiKind, ulong ullDone, ulong ullTotal)
        {
            int iTick;
            int iCopied;
            int iProblems;

            /* The rate limit, for plain progress only: the engine sends one    */
            /* per buffer, which would flood the window's message queue.  The   */
            /* time difference is taken unchecked, so it stays right when       */
            /* TickCount wraps (every 49.7 days).  Other kinds -- an item's     */
            /* start or end, a new phase -- are never held back.                */
            iTick = Environment.TickCount;
            if (uiKind == ENG_clsNative.ENG_EVENT_PROGRESS &&
                unchecked(iTick - Ncb_iLastTick) < ECO_clsCopyControl.ECO_PROGRESS_MS)
            {   /* Too soon after the last: skipped. */
                return;
            }

            /* The counts as they are now -- read only once publishing is       */
            /* decided; the result itself keeps changing until the copy ends,   */
            /* so only the numbers are sent.  A raw operation has no result:    */
            /* zeros.                                                           */
            iCopied = 0;
            iProblems = 0;
            if (Ncb_objCopy != null)
            {   /* A copy: its counts so far. */
                iCopied = Ncb_objCopy.ECR_iCopied;
                iProblems = Ncb_objCopy.ECR_iProblems;
            }

            /* Published (the time noted for the rate limit), as copied   */
            /* values only.                                               */
            Ncb_iLastTick = iTick;
            Ncb_objExecution.EXC_Show(VWK_enmViewKind.Progress,
                new PGD_clsProgressData(false, uiKind, sItem, "",
                    ullDone, ullTotal, 0, 0, iCopied, iProblems));
        }

        /*--------------------------------------------------------------------*/
        /* Ncb_CountSkippedConflict:                                          */
        /*                                                                    */
        /* Count a destination conflict answered "skip" -- by the user, by an */
        /* earlier Skip All, or quietly for a drag-out -- as a problem of the */
        /* copy, with its line in the report.  The engine raises no event for */
        /* an item it skips on that answer (no DONE, no SKIPPED), so without  */
        /* this the copy would end with no problem counted although the item  */
        /* was never copied -- and a cut whose copy counts no problem has its */
        /* sources removed (CommandCopySequence.cs), the skipped one with     */
        /* them.  Counted here, the copy ends partial and every source of a   */
        /* cut is kept.                                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sImage : the event's image path: the item's destination in the */
        /*              image for an import or a copy within, its source for  */
        /*              an extract.                                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the problem counted and, while there is room, its line  */
        /*            kept; nothing when there is no copy result (a raw       */
        /*            operation, which raises no conflict).                   */
        /*--------------------------------------------------------------------*/
        private void Ncb_CountSkippedConflict(string sImage)
        {
            if (Ncb_objCopy == null)
            {   /* No copy result to count into: only a copy of items   */
                /* raises a conflict, so this is a guard, not a case.   */
                return;
            }

            /* One more problem, and its line while the list has room      */
            /* (ECO_PROBLEM_LINES, as for the engine's own SKIPPED and     */
            /* ERROR events), saying the destination was kept as it was.   */
            Ncb_objCopy.ECR_iProblems++;
            if (Ncb_objCopy.ECR_lstProblems.Count < ECO_clsCopyControl.ECO_PROBLEM_LINES)
            {   /* Room for the line: kept. */
                Ncb_objCopy.ECR_lstProblems.Add(sImage +
                    " -- skipped: the destination already exists and was kept as it was.");
            }
        }

        /*--------------------------------------------------------------------*/
        /* NCB_ProcessCopyEvent:                                              */
        /*                                                                    */
        /* Handle one event of a native copy, a format's zero-fill or a       */
        /* capacity test for the command worker, and give the engine its      */
        /* reply: the copy's counts and problem lines kept, a manifest clash  */
        /* or a destination conflict asked through the command's question     */
        /* channel (or settled by an earlier All answer, or quietly for a     */
        /* drag-out), and progress published.  It is not hooked to the        */
        /* engine itself: the callbacks that are -- the copy control's        */
        /* ECO_OnCopyEvent (EngineSession.cs) and the extraction's            */
        /* XEC_OnEvent (CommandNativeOperations.cs) -- call it.  A managed    */
        /* exception is caught, its cause kept for the adapter, and CANCEL    */
        /* returned, since none may unwind through the engine.                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pEvent    : the engine's BFE_CopyEvent, valid only during this */
        /*                 call.                                              */
        /*     pvContext : unused; the callers' bound delegates carry this    */
        /*                 owner.                                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : the ENG_REPLY_* value for the engine -- CONTINUE, an     */
        /*           answer's OVERWRITE, SKIP or KEEP_*, or CANCEL.           */
        /*--------------------------------------------------------------------*/
        internal int NCB_ProcessCopyEvent(IntPtr pEvent, IntPtr pvContext)
        {
            CEV_stCopyEvent stEvent;
            PGD_clsProgressData objData;
            ANS_clsAnswer objAnswer;
            RQT_clsRequest objRequest;
            string sImage;
            string sHost;
            int iCopied;
            int iProblems;

            try
            {   /* Everything -- the reads of engine memory, the questions,   */
                /* the publishing -- inside this try, so no exception can     */
                /* reach the engine.                                          */
                if (!NCB_Continue())
                {   /* Cancelled, Exit confirmed, or failed: no more of this   */
                    /* event is handled, and the engine is told to cancel.     */
                    return(ENG_clsNative.ENG_REPLY_CANCEL);
                }

                /* The event copied out of the engine's memory now: its          */
                /* strings point into memory valid only during this call, and    */
                /* a question below may wait a long time.  The image path (or    */
                /* phase label) is UTF-8; the Windows path, when there is one,   */
                /* UTF-16.                                                       */
                stEvent = (CEV_stCopyEvent)Marshal.PtrToStructure(pEvent, typeof(CEV_stCopyEvent));
                sImage = ENG_clsNative.ENG_PtrToUtf8(stEvent.CEV_pImagePath);
                sHost = "";
                if (stEvent.CEV_pHostPath != IntPtr.Zero)
                {   /* A Windows path: copied as UTF-16. */
                    sHost = Marshal.PtrToStringUni(stEvent.CEV_pHostPath);
                }

                /* The command's request (its kind and the window that asked),   */
                /* for the questions and the drag-out tests below.               */
                objRequest = Ncb_objExecution.EXC_ReadRequest();

#if DEBUG
                /* DEBUG BUILDS ONLY: the test pause, DBG_iCopyDelayMs, which      */
                /* --debug-copy-delay sets at start-up (DebugValues.cs; never      */
                /* the registry).  It is taken HERE, on the worker thread inside   */
                /* the engine's callback, so the window stays live -- Exit,        */
                /* Cancel and their questions can be tried while a copy of a few   */
                /* small files is still running.  Only an item's START, and only   */
                /* for the three copies the user asks for: a raw copy, a format    */
                /* or a capacity test reports its phases through the same          */
                /* callback and is never slowed, and a drag-out is excluded        */
                /* because its drop holds the window's own thread for the whole    */
                /* copy, so a pause there would freeze it.                         */
                if (DBG_clsDebugValues.DBG_iCopyDelayMs > 0 && stEvent.CEV_uiKind == ENG_clsNative.ENG_EVENT_START &&
                    (objRequest.RQT_enmKind == KND_enmKind.Extract ||
                     objRequest.RQT_enmKind == KND_enmKind.Import ||
                     objRequest.RQT_enmKind == KND_enmKind.CopyWithin))
                {   /* A copied item is about to start: wait, then look    */
                    /* again for a Cancel or an Exit that arrived during   */
                    /* the wait, so the item is not copied after the       */
                    /* user asked to stop.                                 */
                    System.Threading.Thread.Sleep(DBG_clsDebugValues.DBG_iCopyDelayMs);
                    if (!NCB_Continue())
                    {   /* Stopped during the pause: the engine copies nothing more. */
                        return(ENG_clsNative.ENG_REPLY_CANCEL);
                    }
                }
#endif

                /* The counts BEFORE this event, for a question's context.   */
                /* Zeros when there is no copy result (a raw operation).     */
                iCopied = 0;
                iProblems = 0;
                if (Ncb_objCopy != null)
                {   /* A copy: its counts so far. */
                    iCopied = Ncb_objCopy.ECR_iCopied;
                    iProblems = Ncb_objCopy.ECR_iProblems;
                }

                if (Ncb_objCopy != null && stEvent.CEV_uiKind == ENG_clsNative.ENG_EVENT_DONE)
                {   /* An item finished: one more copied. */
                    Ncb_objCopy.ECR_iCopied++;
                }

                if (Ncb_objCopy != null && (stEvent.CEV_uiKind == ENG_clsNative.ENG_EVENT_ERROR ||
                    stEvent.CEV_uiKind == ENG_clsNative.ENG_EVENT_SKIPPED))
                {   /* An item failed or was skipped: one more problem -- which   */
                    /* is also what stops a cut from removing its sources         */
                    /* (CommandCopySequence.cs) -- and its line kept, with the    */
                    /* status's sentence, up to ECO_PROBLEM_LINES of them (a      */
                    /* damaged or huge tree could give thousands).  A conflict    */
                    /* answered "skip" raises no event of its own and is          */
                    /* counted where it is answered (Ncb_CountSkippedConflict).   */
                    Ncb_objCopy.ECR_iProblems++;
                    if (Ncb_objCopy.ECR_lstProblems.Count < ECO_clsCopyControl.ECO_PROBLEM_LINES)
                    {   /* Room for the line: kept. */
                        Ncb_objCopy.ECR_lstProblems.Add(sImage + " -- " +
                            ENG_clsNative.ENG_StatusText(stEvent.CEV_iStatus));
                    }
                }

                if (Ncb_objCopy != null && stEvent.CEV_uiKind == ENG_clsNative.ENG_EVENT_NOT_MANIFEST)
                {   /* An imported file named like a manifest that is not one:   */
                    /* imported as an ordinary file -- a note, not a problem.    */
                    if (Ncb_objCopy.ECR_lstNotes == null)
                    {   /* The first note: the list made now (most copies have   */
                        /* none).                                                */
                        Ncb_objCopy.ECR_lstNotes = new List<string>();
                    }

                    /* The note, naming the Windows file: no metadata came from   */
                    /* it.                                                        */
                    Ncb_objCopy.ECR_lstNotes.Add(sHost +
                        " is not a metadata manifest; it was imported as an ordinary file.");
                }

                if (stEvent.CEV_uiKind == ENG_clsNative.ENG_EVENT_MANIFEST_CLASH)
                {   /* An image file named like the manifest meets the extract    */
                    /* with its manifest: keep the file, or write the manifest.   */
                    if (objRequest.RQT_enmKind == KND_enmKind.DragExtract)
                    {   /* A drag-out: no question can be shown (the UI thread   */
                        /* is held in OLE's GetData), so the real file is kept   */
                        /* -- no manifest is made over it.                       */
                        return(ENG_clsNative.ENG_REPLY_KEEP_FILE);
                    }

                    /* Asked, with the event's facts copied for the question. */
                    objData = new PGD_clsProgressData(false, stEvent.CEV_uiKind, sImage,
                        sHost, stEvent.CEV_ullBytesDone, stEvent.CEV_ullBytesTotal,
                        0, stEvent.CEV_iStatus, iCopied, iProblems);
                    objAnswer = Ncb_objExecution.EXC_Ask(QNK_enmQuestionKind.ManifestClash,
                        objRequest.RQT_lOrigin, objData);
                    if (objAnswer != null && objAnswer.ANS_enmKind == ANK_enmAnswerKind.KeepFile)
                    {   /* Keep the file: it is extracted, and no manifest is   */
                        /* written.                                             */
                        return(ENG_clsNative.ENG_REPLY_KEEP_FILE);
                    }

                    if (objAnswer != null && objAnswer.ANS_enmKind == ANK_enmAnswerKind.KeepManifest)
                    {   /* Keep the manifest: it is written, and that image   */
                        /* file is not extracted.                             */
                        return(ENG_clsNative.ENG_REPLY_KEEP_MANIFEST);
                    }

                    /* Any other answer (Cancel, an interrupted question): the   */
                    /* copy is cancelled -- neither output is written.           */
                    return(ENG_clsNative.ENG_REPLY_CANCEL);
                }

                if (stEvent.CEV_uiKind == ENG_clsNative.ENG_EVENT_CONFLICT)
                {   /* A destination that already exists: overwrite or skip,   */
                    /* by an "All" answer in force, quietly for a drag-out,    */
                    /* or asked.                                               */
                    if (Ncb_enmPolicy == Ncb_enmConflictPolicy.OverwriteAll)
                    {   /* "Overwrite All" was answered earlier: overwritten. */
                        return(ENG_clsNative.ENG_REPLY_OVERWRITE);
                    }

                    if (Ncb_enmPolicy == Ncb_enmConflictPolicy.SkipAll ||
                        objRequest.RQT_enmKind == KND_enmKind.DragExtract)
                    {   /* "Skip All" was answered, or a drag-out (which asks   */
                        /* nothing): the existing destination is left as it     */
                        /* is, and the item counted as skipped.                 */
                        Ncb_CountSkippedConflict(sImage);
                        return(ENG_clsNative.ENG_REPLY_SKIP);
                    }

                    /* Asked, with the copied facts.  The window only answers;   */
                    /* the reply to the engine is decided here.                  */
                    objData = new PGD_clsProgressData(false, stEvent.CEV_uiKind, sImage,
                        sHost, stEvent.CEV_ullBytesDone, stEvent.CEV_ullBytesTotal,
                        0, stEvent.CEV_iStatus, iCopied, iProblems);
                    objAnswer = Ncb_objExecution.EXC_Ask(QNK_enmQuestionKind.Conflict,
                        objRequest.RQT_lOrigin, objData);
                    if (objAnswer == null)
                    {   /* No answer: nothing overwritten -- cancelled. */
                        return(ENG_clsNative.ENG_REPLY_CANCEL);
                    }

                    switch (objAnswer.ANS_enmKind)
                    {   /* The answer turned into the engine's reply. */
                        case ANK_enmAnswerKind.OverwriteAll:
                            /* Overwrite, and every later conflict of this copy too. */
                            Ncb_enmPolicy = Ncb_enmConflictPolicy.OverwriteAll;
                            return(ENG_clsNative.ENG_REPLY_OVERWRITE);
                        case ANK_enmAnswerKind.Overwrite:
                            /* Overwrite this one; the next conflict is asked. */
                            return(ENG_clsNative.ENG_REPLY_OVERWRITE);
                        case ANK_enmAnswerKind.SkipAll:
                            /* Skip, and every later conflict without asking;   */
                            /* this item counted as skipped.                    */
                            Ncb_enmPolicy = Ncb_enmConflictPolicy.SkipAll;
                            Ncb_CountSkippedConflict(sImage);
                            return(ENG_clsNative.ENG_REPLY_SKIP);
                        case ANK_enmAnswerKind.Skip:
                            /* Skip this one, counted as skipped; the next   */
                            /* conflict is asked.                            */
                            Ncb_CountSkippedConflict(sImage);
                            return(ENG_clsNative.ENG_REPLY_SKIP);
                        default:
                            /* Cancel, or anything else: the copy is cancelled. */
                            return(ENG_clsNative.ENG_REPLY_CANCEL);
                    }
                }

                /* Any other event: published as progress (rate-limited for   */
                /* plain progress), with the counts after this event.         */
                NCB_Progress(sImage, stEvent.CEV_uiKind,
                    stEvent.CEV_ullBytesDone, stEvent.CEV_ullBytesTotal);

                /* The engine goes on. */
                return(ENG_clsNative.ENG_REPLY_CONTINUE);
            }
            catch (Exception exCallback)
            {   /* Something here threw: kept as the failure, and CANCEL --   */
                /* the caller reports the failure once the engine returns.    */
                /* The whole exception to the debug log, its message as the   */
                /* failure the user is shown.                                 */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("native callback", exCallback.ToString());
                }

                /* Kept as the failure: the message alone, as the user is shown it. */
                NCB_RecordFailure(exCallback.Message);
                return(ENG_clsNative.ENG_REPLY_CANCEL);
            }
        }

        /*--------------------------------------------------------------------*/
        /* NCB_ProcessCheckEvent:                                             */
        /*                                                                    */
        /* Handle one event of a native check (e2fsck) for the command        */
        /* worker: a report line is kept in the check's result and            */
        /* published, a pass's counts are published no more often than the    */
        /* progress rate allows, and a Cancel or an Exit taken at the         */
        /* command's checkpoint stops the check.  It is not hooked to the     */
        /* engine itself: the check control's ECC_OnCheckEvent                */
        /* (EngineSession.cs), the callback that is, calls it.  A managed     */
        /* exception is caught, its cause kept for the adapter, and CANCEL    */
        /* returned, since none may unwind through e2fsck.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pEvent    : the engine's 32-byte x64 BFE_CheckEvent, valid     */
        /*                 only during this call.                             */
        /*     pvContext : unused; the caller's bound delegate carries this   */
        /*                 owner.                                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : ENG_REPLY_CONTINUE, or ENG_REPLY_CANCEL to stop the      */
        /*           check.                                                   */
        /*--------------------------------------------------------------------*/
        internal int NCB_ProcessCheckEvent(IntPtr pEvent, IntPtr pvContext)
        {
            uint uiKind;
            int iTick;
            string sText;

            try
            {   /* Everything inside this try, so no exception can reach   */
                /* e2fsck.                                                 */
                if (!NCB_Continue())
                {   /* Cancelled, Exit confirmed, or failed: the check is   */
                    /* stopped, at a line or at a count alike.              */
                    return(ENG_clsNative.ENG_REPLY_CANCEL);
                }

                /* The event read field by field at its x64 offsets              */
                /* (bifrost_engine.h, BFE_CheckEvent: the two counts at 0 and    */
                /* 8, the text pointer at 16, the kind at 24, the pass at 28).   */
                /* A report line is copied now, while its pointer is valid,      */
                /* and kept in the result -- every line, however slowly the      */
                /* window draws them.                                            */
                uiKind = (uint)Marshal.ReadInt32(pEvent, 24);
                sText = "";
                if (uiKind == ENG_clsNative.ENG_CHECK_EVENT_TEXT)
                {   /* A report line: copied and kept. */
                    sText = ENG_clsNative.ENG_PtrToUtf8(Marshal.ReadIntPtr(pEvent, 16));
                    Ncb_objCheck.ECK_lstLines.Add(sText);
                }

                /* Lines are always published; counts at most once per         */
                /* ECO_PROGRESS_MS (the same unchecked, wrap-safe difference   */
                /* as the copy's).                                             */
                iTick = Environment.TickCount;
                if (uiKind != ENG_clsNative.ENG_CHECK_EVENT_TEXT &&
                    unchecked(iTick - Ncb_iLastTick) < ECO_clsCopyControl.ECO_PROGRESS_MS)
                {   /* A count too soon after the last: not published; the   */
                    /* check goes on.                                        */
                    return(ENG_clsNative.ENG_REPLY_CONTINUE);
                }

                /* Published: the line (or "") and the counts and pass, read   */
                /* now while the event is valid.                               */
                Ncb_iLastTick = iTick;
                Ncb_objExecution.EXC_Show(VWK_enmViewKind.Progress,
                    new PGD_clsProgressData(true, uiKind, sText, "",
                        (ulong)Marshal.ReadInt64(pEvent, 0),
                        (ulong)Marshal.ReadInt64(pEvent, 8),
                        (uint)Marshal.ReadInt32(pEvent, 28), 0, 0, 0));

                /* The check goes on; nothing kept points into the event. */
                return(ENG_clsNative.ENG_REPLY_CONTINUE);
            }
            catch (Exception exCallback)
            {   /* Something here threw: kept as the failure, and CANCEL;       */
                /* the caller reports it once the check returns.  The whole     */
                /* exception to the debug log, its message as the failure the   */
                /* user is shown (Nst_Check's SYS_INTERNAL).                    */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("native callback", exCallback.ToString());
                }

                /* Kept as the failure: the message alone, as the user is shown it. */
                NCB_RecordFailure(exCallback.Message);
                return(ENG_clsNative.ENG_REPLY_CANCEL);
            }
        }
    }
}
