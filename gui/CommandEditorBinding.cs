/*----------------------------------------------------------------------------*/
/* CommandEditorBinding.cs                                                    */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The command worker's record of which editors are open on WINDOWS files,    */
/* and what a finished Save As to a Windows file changes in it.  The worker   */
/* keeps two sets of editors: those whose save goes into the image (closing   */
/* the image must deal with them) and those whose save goes to a Windows      */
/* file (closing the image does not concern them).  An editor moves between   */
/* them when a Save As changes where it saves; what it was opened from -- its */
/* original content kept for comparison -- stays with it until it closes.     */
/*----------------------------------------------------------------------------*/

/* The framework: the exceptions thrown and caught, and String's null-or-   */
/* empty test (System); the list the waiting facts are gathered into        */
/* (System.Collections.Generic).                                            */
using System;
using System.Collections.Generic;

namespace BifrostFM
{
    /*-------------------------------------------------------------------------*/
    /* HBA_clsHostBindingArgs -- a report from an editor's window that has     */
    /* ALREADY happened: an editor opened on a Windows file (order 0), or a    */
    /* save to a Windows file that has finished.  It gives the worker facts to */
    /* record, never permission to write anything -- documentation table.      */
    /*-------------------------------------------------------------------------*/
    internal sealed class HBA_clsHostBindingArgs
    {
        internal string HBA_sPath;     // the full Windows path the editor now saves to; never an image path
        internal long HBA_lSaveOrder;  // the finished save's order number, so an older save's report cannot undo a newer one; 0 for the opening registration

        /*--------------------------------------------------------------------*/
        /* HBA_clsHostBindingArgs:                                            */
        /*                                                                    */
        /* One report, checked as it is made.                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sPath      : the Windows path, captured when the save or open  */
        /*                  happened.                                         */
        /*     lSaveOrder : the save's order number, or 0 for the opening.    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the report.  Throws ArgumentException for no   */
        /*                     path or a negative order.                      */
        /*--------------------------------------------------------------------*/
        internal HBA_clsHostBindingArgs(string sPath, long lSaveOrder)
        {
            if (String.IsNullOrEmpty(sPath) || lSaveOrder < 0)
            {   /* No path, or an impossible order: refused at once, so a   */
                /* report can never be completed later from whatever the    */
                /* window shows by then.                                    */
                throw new ArgumentException("A Windows binding needs its path and save order.");
            }

            /* The two facts as given.  The window sends a save's report only   */
            /* once the file is written; the opening's report writes nothing.   */
            HBA_sPath = sPath;
            HBA_lSaveOrder = lSaveOrder;
        }
    }

    internal sealed partial class SYS_clsSystem
    {
        /*--------------------------------------------------------------------*/
        /* Sys_HostBindingSequence:                                           */
        /*                                                                    */
        /* Apply one report: record an editor newly opened on a Windows file, */
        /* or move an editor to the Windows set after its Save As to a        */
        /* Windows file finished.  Bookkeeping only -- nothing is written,    */
        /* and no engine call is made.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the report's request: the editor's id (origin)    */
        /*                  and HBA_clsHostBindingArgs as payload.            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : SYS_OK when recorded; SYS_REFUSED for a        */
        /*                     malformed, duplicate or stale report.          */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_HostBindingSequence(RQT_clsRequest objRequest)
        {
            HBA_clsHostBindingArgs objArgs;
            EDR_clsEditor objEditor;
            EDR_clsEditor objRebound;

            /* The report and the editor it names, if the worker knows it.    */
            /* An editor opened on a Windows file has a NEGATIVE id; the id   */
            /* stays the same when a later Save As changes where it saves.    */
            objArgs = objRequest.RQT_objPayload as HBA_clsHostBindingArgs;
            objEditor = Sys_FindEditor(objRequest.RQT_lOrigin);
            if (objArgs == null || objRequest.RQT_lOrigin == 0)
            {   /* No report, or id 0 (the main window): refused -- the main   */
                /* window is never recorded as an editor.                      */
                return(new RSL_clsResult(SYS_REFUSED, "Missing Windows editor binding.", null));
            }

            if (objRequest.RQT_enmKind == KND_enmKind.RegisterHostEditor)
            {   /* An editor just opened on a Windows file. */
                if (objRequest.RQT_lOrigin >= 0 || objEditor != null || objArgs.HBA_lSaveOrder != 0)
                {   /* Not a Windows editor's id, already recorded, or not an   */
                    /* opening: refused, so nothing already recorded is         */
                    /* replaced.                                                */
                    return(new RSL_clsResult(SYS_REFUSED,
                        "The Windows viewer id is already registered or invalid.", null));
                }

                /* Recorded in the Windows set.  No image need be open for    */
                /* that.  "Presented" says a real window exists, so a later   */
                /* close of it is matched to this record.                     */
                Sys_dictHostEditors.Add(objRequest.RQT_lOrigin,
                    new EDR_clsEditor(objRequest.RQT_lOrigin, 0, null,
                        objArgs.HBA_sPath, bPresented: true));

                /* Recorded; nothing written, nothing of the image held. */
                return(new RSL_clsResult(SYS_OK, "", null));
            }

            if (objEditor == null || objArgs.HBA_lSaveOrder < objEditor.EDR_lSaveOrder)
            {   /* A save's report for an editor that has since closed, or      */
                /* one older than a save already recorded: refused, so a late   */
                /* report cannot bring a closed editor back or undo a newer     */
                /* save.                                                        */
                return(new RSL_clsResult(SYS_REFUSED,
                    "The viewer closed or a newer save completed.", null));
            }

            /* The editor now saves to a Windows file: a new record with the    */
            /* same id, the new path and this save's order, which keeps what    */
            /* the editor was opened from (its epoch, target and original       */
            /* content) -- then it replaces the old record, moving the editor   */
            /* out of the image set.                                            */
            objRebound = new EDR_clsEditor(objEditor.EDR_lId, 0, null, objArgs.HBA_sPath,
                objRequest.RQT_lRevision, objEditor.EDR_objResource, objEditor,
                objArgs.HBA_lSaveOrder);
            Sys_ReplaceEditor(objEditor, objRebound);

            /* Recorded; only where the editor saves has changed. */
            return(new RSL_clsResult(SYS_OK, "", null));
        }

        /*--------------------------------------------------------------------*/
        /* Sys_ReplaceEditor:                                                 */
        /*                                                                    */
        /* Put an editor's new record in place of its old one, in the set its */
        /* new save place belongs to: the Windows set for a Windows file, the */
        /* image set for an image entry.  The editor's original content is    */
        /* not released or remade: the new record carries it.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objOriginal : the editor's current record.                     */
        /*     objRebound  : its replacement (same id, same original).        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the editor is in exactly one set, under its new record. */
        /*--------------------------------------------------------------------*/
        private void Sys_ReplaceEditor(EDR_clsEditor objOriginal, EDR_clsEditor objRebound)
        {
            /* Out of both sets first -- it is in one of them -- then into   */
            /* the one its new record belongs to, so it is never in two.     */
            Sys_dictHostEditors.Remove(objOriginal.EDR_lId);
            Sys_lstEditors.Remove(objOriginal);
            if (objRebound.EDR_objTarget == null)
            {   /* No image target: it saves to Windows, and an image close   */
                /* leaves it alone.                                           */
                Sys_dictHostEditors[objOriginal.EDR_lId] = objRebound;
            }
            else
            {   /* An image target: it joins the set an image close must deal   */
                /* with, wherever it was first opened from.                     */
                Sys_lstEditors.Add(objRebound);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Sys_FlushEditorBindings:                                           */
        /*                                                                    */
        /* Apply, ahead of their turn, the editor reports waiting in the      */
        /* queue -- openings and finished Windows saves -- before the worker  */
        /* decides which editors an image close must deal with.  A Windows    */
        /* save can finish while the worker waits on a question, and leaving  */
        /* its report queued would count that editor in the wrong set.  Only  */
        /* these reports jump the queue: each is bookkeeping, with no write,  */
        /* no engine call and no question.                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the running command, for the window's state       */
        /*                  update afterwards.                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the reports applied, each answered and released; the    */
        /*            rest of the queue as it was.                            */
        /*--------------------------------------------------------------------*/
        private void Sys_FlushEditorBindings(RQT_clsRequest objRequest)
        {
            List<RQT_clsRequest> lstCompleted;
            RQT_clsRequest objWaiting;
            RQT_clsRequest objNotification;
            RSL_clsResult objResult;
            int i;

            /* The reports taken out of the waiting queue, in the order they   */
            /* were posted, under the queue's lock; everything else stays      */
            /* where it was.                                                   */
            lstCompleted = new List<RQT_clsRequest>();
            lock (Sys_objQueue)
            {   /* The queue held while it is walked and changed. */
                for (i = 0; i < Sys_lstWaiting.Count;)
                {   /* One waiting request per iteration; the index moves on   */
                    /* only when nothing was taken out (a removal brings the   */
                    /* next request to the same index).                        */
                    objWaiting = Sys_lstWaiting[i];
                    if (objWaiting.RQT_enmKind == KND_enmKind.RegisterHostEditor ||
                        objWaiting.RQT_enmKind == KND_enmKind.HostEditorSaved)
                    {   /* An editor report: taken out to be applied now. */
                        lstCompleted.Add(objWaiting);
                        Sys_lstWaiting.RemoveAt(i);
                    }
                    else
                    {   /* Any other request: left in its place for its turn. */
                        i++;
                    }
                }
            }

            /* The reports applied outside the lock, one at a time; one that   */
            /* fails does not stop the others.                                 */
            for (i = 0; i < lstCompleted.Count; i++)
            {   /* One report per iteration: applied, answered, released. */
                objNotification = lstCompleted[i];
                try
                {   /* Applied; a refusal comes back as a result. */
                    objResult = Sys_HostBindingSequence(objNotification);
                }
                catch (Exception exBinding)
                {   /* It threw: a failure, and nothing else follows from it.     */
                    /* The whole exception, stack trace and all, goes to the      */
                    /* debug log; the result's detail is its message alone,       */
                    /* because the editors' coordinator shows a failed report's   */
                    /* detail to the user (CommandEditorCoordinator.cs), where    */
                    /* a stack trace would only bury the reason.                  */
                    if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                    {   /* Only while the debug log is kept: otherwise the line is never built. */
                        DBGLOG_clsDebugLog.DBGLOG_Write("editor binding", exBinding.ToString());
                    }

                    /* The failed result, its detail the exception's message alone. */
                    objResult = new RSL_clsResult(SYS_INTERNAL, exBinding.Message, null);
                }

                /* Every report gets its answer and its release, as it would   */
                /* have in its turn.                                           */
                SYS_Show(new VWM_clsView(VWK_enmViewKind.Result, objNotification, objResult));
                Sys_Release(objNotification);
            }

            if (lstCompleted.Count != 0)
            {   /* Something changed: the window's menus are told the state,   */
                /* so they agree with the sets the close will now use.         */
                Sys_PublishState(objRequest);
            }
        }
    }
}
