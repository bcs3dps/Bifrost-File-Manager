/*----------------------------------------------------------------------------*/
/* CommandCheckViews.cs                                                       */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* How a filesystem check's window is told about each run: a Begin when the   */
/* run starts and a Finished with its result when it ends, sent by the        */
/* command worker apart from the request's own completion -- because that     */
/* completion goes to a callback that may consume the result, and the window  */
/* still has to be told the run is over.  One window serves the check and a   */
/* Repair after it; each run is a request of its own.  Also the way out when  */
/* the window cannot be told at all.                                          */
/*----------------------------------------------------------------------------*/

/* The framework: the exception a run may throw (System). */
using System;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* CRV_clsCheckRunView -- one run, as sent to the check window            */
    /* (MainCommand.cs paints it): which window, which run, whether a repair, */
    /* whether closing the window reopens the image, and the result once      */
    /* there is one.  The window and the run are told apart so a Cancel meant */
    /* for one run cannot stop a later one -- documentation table.            */
    /*------------------------------------------------------------------------*/
    internal sealed class CRV_clsCheckRunView
    {
        internal long CRV_lWindow;             // the window's identity: the first Check request's serial, the same through a Repair
        internal long CRV_lRun;                // this run's request serial; the window's Cancel names it
        internal bool CRV_bRepair;             // false for the report-only check, true for a repair the user agreed to
        internal bool CRV_bReopen;             // whether closing the window reopens the image read-only (shown only; the worker decides)
        internal RSL_clsResult CRV_objResult;  // null in the Begin; the run's result in the Finished, shared, not changed after

        /*------------------------------------------------------------------*/
        /* CRV_clsCheckRunView:                                             */
        /*                                                                  */
        /* One run's facts for a Begin (no result) or a Finished (with      */
        /* one).  The result is the run's own object, not a copy: nothing   */
        /* changes it after it is sent.                                     */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     lWindow    : the check window's identity.                    */
        /*     objRequest : the run's request; its serial names the run.    */
        /*     bRepair    : true for a repair, false for a check.           */
        /*     bReopen    : whether closing the window reopens the image.   */
        /*     objResult  : the result, or null for a Begin.                */
        /*                                                                  */
        /* Returns:                                                         */
        /*     (constructor) : the run's facts.                             */
        /*------------------------------------------------------------------*/
        internal CRV_clsCheckRunView(long lWindow, RQT_clsRequest objRequest,
            bool bRepair, bool bReopen, RSL_clsResult objResult)
        {
            /* Both identities: the window, and this run within it -- so a   */
            /* Cancel from an earlier run cannot stop a Repair shown later   */
            /* in the same window.                                           */
            CRV_lWindow = lWindow;
            CRV_lRun = objRequest.RQT_lSerial;

            /* What the window shows about this run.  The reopen flag is for   */
            /* display only: whether closing the window really reopens the     */
            /* image is decided by the worker's CheckClosed sequence.          */
            CRV_bRepair = bRepair;
            CRV_bReopen = bReopen;
            CRV_objResult = objResult;
        }
    }

    internal sealed partial class SYS_clsSystem
    {
        /* A check run's Begin and Finished, and the way out when the window   */
        /* cannot be told.                                                     */

        /*------------------------------------------------------------------*/
        /* Sys_CheckViewUnavailable:                                        */
        /*                                                                  */
        /* Leave check mode when the check window's Open or Begin could not */
        /* be sent to the window (the window going away, say).  It asks for */
        /* the window to close, runs the CheckClosed sequence -- which puts */
        /* back what was open before the check -- and reports the failure   */
        /* to show the window, even when the putting back succeeded.        */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objRequest   : the Check or Repair step's request.           */
        /*     objExecution : the running command.                          */
        /*                                                                  */
        /* Returns:                                                         */
        /*     RSL_clsResult : SYS_INTERNAL "could not be displayed", with  */
        /*                     the putting back's own failure added when it */
        /*                     failed too.                                  */
        /*------------------------------------------------------------------*/
        private RSL_clsResult Sys_CheckViewUnavailable(RQT_clsRequest objRequest,
            EXC_clsExecution objExecution)
        {
            RQT_clsRequest objOuter;
            RQT_clsRequest objClosed;
            RSL_clsResult objRestored;
            string sDetail;

            /* An Open sent earlier may still be on its way: ask for the        */
            /* window to close before check mode is left.  This only SENDS      */
            /* the request -- it does not wait for the window to have closed.   */
            SYS_Show(new VWM_clsView(VWK_enmViewKind.WindowClose, objRequest, Sys_lModeWindow));

            /* The CheckClosed sequence, run here on this worker: a request   */
            /* made for it (never queued, holding nothing of its own to       */
            /* release) that carries the OUTER request's serial, so its       */
            /* progress is shown against the command that is running.         */
            objOuter = objExecution.EXC_ReadRequest();
            objClosed = new RQT_clsRequest(KND_enmKind.CheckClosed, Sys_lModeWindow,
                objRequest.RQT_lEpoch, Sys_objTarget, null, 0, 0, null, "", "");
            objClosed.RQT_lSerial = objOuter.RQT_lSerial;
            objRestored = Sys_Sequence(objClosed, objExecution);

            /* The failure to show the window, and the putting back's own   */
            /* reason after it when that failed too.                        */
            sDetail = "The check window could not be displayed.";
            if (objRestored.RSL_iStatus != SYS_OK)
            {   /* The putting back failed as well: its reason is added. */
                sDetail = sDetail + " " + objRestored.RSL_sDetail;
            }

            /* A failure either way -- the check the user asked for was not   */
            /* shown -- carrying the putting back's result.                   */
            return(new RSL_clsResult(SYS_INTERNAL, sDetail, objRestored.RSL_objPayload));
        }

        /*------------------------------------------------------------------*/
        /* Sys_RunCheckView:                                                */
        /*                                                                  */
        /* One check or repair run with its window told: a Begin first (no  */
        /* run starts if it cannot be sent), the run, then a Finished with  */
        /* the result -- a failed result when the run threw or returned     */
        /* nothing, so the window always leaves its running state.          */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objRequest   : the step's request (its target is the one     */
        /*                    checked).                                     */
        /*     objExecution : the running command, also used for progress.  */
        /*     bRepair      : true only once the user has agreed to the     */
        /*                    repair.                                       */
        /*                                                                  */
        /* Returns:                                                         */
        /*     RSL_clsResult : the run's result (after the Finished has     */
        /*                     been sent), or Sys_CheckViewUnavailable's.   */
        /*------------------------------------------------------------------*/
        private RSL_clsResult Sys_RunCheckView(RQT_clsRequest objRequest,
            EXC_clsExecution objExecution, bool bRepair)
        {
            RQT_clsRequest objOuter;
            RSL_clsResult objResult;
            STP_enmStep enmStep;

            /* The Begin, named by the OUTER request -- the command that is   */
            /* running, which may be a whole image-open sequence -- so the    */
            /* window's progress and buttons address the right one.  If it    */
            /* cannot be sent, no run starts and check mode is left.          */
            objOuter = objExecution.EXC_ReadRequest();
            if (!SYS_Show(new VWM_clsView(VWK_enmViewKind.CheckBegin, objOuter,
                new CRV_clsCheckRunView(Sys_lModeWindow, objOuter,
                    bRepair, Sys_bReopenAfterCheck, null))))
            {   /* The window cannot be told: nothing is run. */
                return(Sys_CheckViewUnavailable(objRequest, objExecution));
            }

            /* The step: a check, or the repair the user agreed to.  Cancel   */
            /* and Exit are honoured at the step's own checkpoints.           */
            enmStep = STP_enmStep.Check;
            if (bRepair)
            {   /* Agreed to: the repair. */
                enmStep = STP_enmStep.Repair;
            }

            try
            {   /* The run itself.  No result at all is turned into a     */
                /* failure, so the window has something to finish with.   */
                objResult = Sys_Do(enmStep, objRequest, objExecution);
                if (objResult == null)
                {   /* Nothing came back: a failure. */
                    objResult = new RSL_clsResult(SYS_INTERNAL, "The check returned no result.", null);
                }
            }
            catch (Exception exCheck)
            {   /* The run threw: a failure, so the window still finishes.       */
                /* The whole exception, stack trace and all, goes to the         */
                /* debug log; the detail sent with the result is its message     */
                /* alone, because the window shows that detail to the user       */
                /* after "The check could not be run:" (Main_CheckWindowResult   */
                /* in MainCommand.cs carries it, CheckForm.cs words it).         */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("check run", exCheck.ToString());
                }

                /* The failed result, its detail the exception's message alone. */
                objResult = new RSL_clsResult(SYS_INTERNAL, exCheck.Message, null);
            }

            /* The Finished, with the result.  The request's own completion    */
            /* goes first to its callback (CommandClient.cs), which may use    */
            /* the result up; this separate message is what tells the window   */
            /* the run is over (it checks window and run before painting).     */
            /* Sending can fail while the window is being disposed; nothing    */
            /* else is done here either way.                                   */
            SYS_Show(new VWM_clsView(VWK_enmViewKind.CheckFinished, objOuter,
                new CRV_clsCheckRunView(Sys_lModeWindow, objOuter,
                    bRepair, Sys_bReopenAfterCheck, objResult)));

            /* The result to the scheduler, which reports and cleans up. */
            return(objResult);
        }
    }
}
