/*----------------------------------------------------------------------------*/
/* CommandImportControl.cs                                                    */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The engine callback for an editor's save into the image -- one file        */
/* imported under a temporary name before it replaces the entry               */
/* (EngineSession.cs).  It does three things the plain import could not:      */
/* lets the running command pause or stop the copy (Cancel, or Exit while it  */
/* runs) before the entry is touched; keeps the one file's OWN failure        */
/* status, where the import as a whole says only "partial"; and keeps a       */
/* failure inside the callback itself apart from a user's cancel, so the      */
/* save reports the right one.                                                */
/*----------------------------------------------------------------------------*/

/* The framework: the exceptions caught and IntPtr (System), and the copy of   */
/* the engine's event structure out of native memory (InteropServices).        */
using System;
using System.Runtime.InteropServices;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* IMC_clsImportControl -- the callback's state for ONE save: made before */
    /* the import, read after it, and dropped.  Each save makes its own, so   */
    /* nothing carries from one save to the next -- documentation table.      */
    /*------------------------------------------------------------------------*/
    internal sealed class IMC_clsImportControl
    {
        private readonly NCB_clsNativeControl Imc_objControl;  // the running command's checkpoint and progress, for this save only; null when the save runs outside a command
        private int Imc_iItemStatus;                           // the first SKIPPED or ERROR status the import reported for the file; ENG_OK until one comes
        private string Imc_sFailure;                           // why the callback itself failed (its exception, or the checkpoint's own failure); null for none, a user's cancel included

        /*--------------------------------------------------------------------*/
        /* IMC_clsImportControl:                                              */
        /*                                                                    */
        /* The state for one save, bound to the command running it, if any.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objExecution : the running command, or null when the save is   */
        /*                    not run by one (then no checkpoint, no          */
        /*                    progress).                                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the state, with nothing reported yet.          */
        /*--------------------------------------------------------------------*/
        internal IMC_clsImportControl(EXC_clsExecution objExecution)
        {
            /* The command's checkpoint and progress, borrowed for this save   */
            /* -- or none.  The control is made without a copy report or a     */
            /* window: a save has neither, it only needs the stop test and     */
            /* the progress line.                                              */
            Imc_objControl = null;
            if (objExecution != null)
            {   /* Run by a command: its services, for the length of this save. */
                Imc_objControl = new NCB_clsNativeControl(objExecution, null, null);
            }

            /* Nothing reported yet: the file's status is OK until an event   */
            /* says otherwise, and no failure of the callback's own.          */
            Imc_iItemStatus = ENG_clsNative.ENG_OK;
            Imc_sFailure = null;
        }

        /*--------------------------------------------------------------------*/
        /* IMC_ReadItemStatus:                                                */
        /*                                                                    */
        /* After the import: the file's own failure status, if an event       */
        /* reported one.  The import of one file that fails returns only      */
        /* "partial"; this is the reason behind it (a name refused, the image */
        /* full ...), which the save reports instead.                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : ENG_OK when no SKIPPED or ERROR event came; otherwise    */
        /*           the first one's status.                                  */
        /*--------------------------------------------------------------------*/
        internal int IMC_ReadItemStatus()
        {
            /* The first status kept, or ENG_OK. */
            return(Imc_iItemStatus);
        }

        /*--------------------------------------------------------------------*/
        /* IMC_ReadFailure:                                                   */
        /*                                                                    */
        /* After the import: whether the callback stopped it because of a     */
        /* failure of its OWN -- not because the user cancelled.  Both reach  */
        /* the engine as the same CANCEL reply, and only this tells them      */
        /* apart, so the save reports a failure as a failure.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sFailure : receives the failure's text, or "" when there was   */
        /*                none.                                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the callback failed; false when the import's  */
        /*            own status (a user's cancel included) stands.           */
        /*--------------------------------------------------------------------*/
        internal bool IMC_ReadFailure(out string sFailure)
        {
            /* Read by the save after the import has returned, so no          */
            /* callback is running any more.  "" unless a failure was kept.   */
            sFailure = "";
            if (Imc_sFailure != null)
            {   /* The callback failed: its text, and true. */
                sFailure = Imc_sFailure;
                return(true);
            }

            /* No failure of the callback's own: the import's status stands. */
            return(false);
        }

        /*--------------------------------------------------------------------*/
        /* IMC_OnEvent:                                                       */
        /*                                                                    */
        /* The engine's copy callback (BFE_CopyCallback) for the save's       */
        /* import, called on the thread that made the import call, inside     */
        /* it.  Asks the command whether to go on, keeps the file's first     */
        /* failure status, and passes progress on; any exception in it is     */
        /* caught here, since one must never unwind through the engine.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pEvent    : the engine's event (BFE_CopyEvent), valid only     */
        /*                 while this call runs.                              */
        /*     pvContext : not used -- the delegate is bound to this object,  */
        /*                 which carries the state.                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : ENG_REPLY_CONTINUE to go on (on a CONFLICT that means    */
        /*           skip); ENG_REPLY_CANCEL to stop the import.              */
        /*--------------------------------------------------------------------*/
        internal int IMC_OnEvent(IntPtr pEvent, IntPtr pvContext)
        {
            CEV_stCopyEvent stEvent;
            string sFailure;

            try
            {   /* Everything below is managed code that could throw: the   */
                /* checkpoint, the copy of the event, the progress line.    */
                if (Imc_objControl != null && !Imc_objControl.NCB_Continue())
                {   /* The command says stop: Cancel, or an Exit the user     */
                    /* confirmed (an Exit waits here for the answer first).   */
                    /* A stop because the checkpoint itself failed carries    */
                    /* that failure, kept for IMC_ReadFailure; a user's       */
                    /* cancel carries none.                                   */
                    if (Imc_objControl.NCB_ReadFailure(out sFailure))
                    {   /* The checkpoint failed: its reason is kept. */
                        Imc_sFailure = sFailure;
                    }

                    /* Stop the import: no more bytes are copied, and the   */
                    /* entry being saved over is not touched.               */
                    return(ENG_clsNative.ENG_REPLY_CANCEL);
                }

                /* The event copied out of native memory -- the pointer is    */
                /* good only during this call -- and the file's FIRST         */
                /* SKIPPED or ERROR status kept, the specific reason behind   */
                /* the import's "partial".                                    */
                stEvent = (CEV_stCopyEvent)Marshal.PtrToStructure(pEvent, typeof(CEV_stCopyEvent));
                if (Imc_iItemStatus == ENG_clsNative.ENG_OK &&
                    (stEvent.CEV_uiKind == ENG_clsNative.ENG_EVENT_ERROR ||
                    stEvent.CEV_uiKind == ENG_clsNative.ENG_EVENT_SKIPPED))
                {   /* The first failure status; later ones do not replace it. */
                    Imc_iItemStatus = stEvent.CEV_iStatus;
                }

                if (Imc_objControl != null && stEvent.CEV_uiKind == ENG_clsNative.ENG_EVENT_PROGRESS)
                {   /* Progress, run by a command: the path is copied into a    */
                    /* managed string NOW, since the window shows it later      */
                    /* (progress is rate-limited) and the native text is gone   */
                    /* once this call returns.                                  */
                    Imc_objControl.NCB_Progress(
                        ENG_clsNative.ENG_PtrToUtf8(stEvent.CEV_pImagePath),
                        stEvent.CEV_uiKind, stEvent.CEV_ullBytesDone, stEvent.CEV_ullBytesTotal);
                }

                /* Go on.  A CONFLICT is answered "skip" by this reply; the   */
                /* save imports under a fresh random name, so it does not     */
                /* expect one (EngineSession.cs says what happens if it       */
                /* meets one).                                                */
                return(ENG_clsNative.ENG_REPLY_CONTINUE);
            }
            catch (Exception exCallback)
            {   /* The callback itself failed: the reason kept, and the         */
                /* import stopped through the engine's own cancel, so it        */
                /* unwinds normally and removes its partial file.  The whole    */
                /* exception, stack trace and all, goes to the debug log, for   */
                /* whoever traces the fault; the text kept is its message       */
                /* alone, because the save shows that text to the user as its   */
                /* failure's detail (ESR_sDetail, EngineSession.cs), where a    */
                /* stack trace would only bury the reason.                      */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("import callback", exCallback.ToString());
                }

                /* The reason kept for the save's failure: the message alone. */
                Imc_sFailure = exCallback.Message;
                return(ENG_clsNative.ENG_REPLY_CANCEL);
            }
        }
    }
}
