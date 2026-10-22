/*----------------------------------------------------------------------------*/
/* CommandNavigation.cs                                                       */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* Moving the image pane to another folder, and the record of a finished      */
/* folder listing that the command worker hands to the window.  The worker    */
/* keeps a "current folder" for the main pane -- the folder a later change    */
/* (a copy, a delete) re-lists -- and it moves only when the new folder has   */
/* actually been read: a failed read leaves it where it was.  The folder      */
/* pickers' listings travel in the same record but never move it.             */
/*----------------------------------------------------------------------------*/

/* The framework: the exception a failed listing may throw (System). */
using System;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* FVW_clsFolderView -- one finished folder listing, as delivered to the  */
    /* window: which folder was read, under which open of the image, and the  */
    /* result.  The epoch is what lets the window tell a listing of the image */
    /* now open from one of an image since closed -- documentation table.     */
    /*------------------------------------------------------------------------*/
    internal sealed class FVW_clsFolderView
    {
        internal string FVW_sFolder;                 // the image folder that was read (absolute), whether or not the read succeeded
        internal long FVW_lEpoch;                    // the worker's epoch when the read finished -- stepped by every NEW open, kept by a reopen that only changes read / write; the window ignores a listing from an earlier epoch
        internal RSL_clsResult FVW_objResult;        // the listing's result: the rows, or the failure and its reason
        internal EVR_clsVolumeResult FVW_objVolume;  // the filesystem's facts read with a re-list after a change (Sys_Refresh), so the status line's usage follows copies and edits; null when not read

        /*--------------------------------------------------------------------*/
        /* FVW_clsFolderView:                                                 */
        /*                                                                    */
        /* The record of one finished listing.  Made by the worker once the   */
        /* read is over, so every field is final; nothing changes it after.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sFolder   : the folder read.                                   */
        /*     lEpoch    : the worker's epoch when the read finished.         */
        /*     objResult : the listing's result, kept as it is.               */
        /*     objVolume : the filesystem's facts when they were read with    */
        /*                 it (a re-list after a change), else null.          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the record.                                    */
        /*--------------------------------------------------------------------*/
        internal FVW_clsFolderView(string sFolder, long lEpoch, RSL_clsResult objResult,
            EVR_clsVolumeResult objVolume)
        {
            /* The volume facts when the read came with them (a re-list after   */
            /* a change), else null: a finished record, kept as it is.  Every   */
            /* caller passes it explicitly, so no read is ever made to look     */
            /* like a plain navigation by a forgotten argument.                 */
            FVW_objVolume = objVolume;

            /* The folder, the epoch and the result, as the worker had them      */
            /* when the read finished.  The epoch is the one at the END of the   */
            /* read, not the one when the request was posted: an open that       */
            /* lists the new image's root is posted before that image exists,    */
            /* so only the worker can say which open the rows belong to.         */
            FVW_sFolder = sFolder;
            FVW_lEpoch = lEpoch;
            FVW_objResult = objResult;
        }
    }

    internal sealed partial class SYS_clsSystem
    {
        /*--------------------------------------------------------------------*/
        /* Sys_NavigateFolderSequence:                                        */
        /*                                                                    */
        /* The sequence for "show this folder": list it, move the main pane's */
        /* current folder there only if the listing succeeded and the         */
        /* request came from the main pane, and hand the window the finished  */
        /* listing either way -- the window needs a failed one too, to settle */
        /* the Back or path-box action that asked for it.                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the request: the one folder, and the origin     */
        /*                    that asked (0 is the main pane).                */
        /*     objExecution : the running command, for the listing step.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the listing's own result, a cancel included;   */
        /*                     SYS_REFUSED for a request without exactly one  */
        /*                     folder.                                        */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_NavigateFolderSequence(RQT_clsRequest objRequest,
            EXC_clsExecution objExecution)
        {
            RSL_clsResult objResult;
            string sFolder;

            if (objRequest.RQT_lstPaths.Count != 1)
            {   /* Not exactly one folder: refused without a listing, rather   */
                /* than reading the worker's current folder in its place.      */
                /* The request builder always supplies one, so this guards a   */
                /* malformed request only.                                     */
                return(new RSL_clsResult(SYS_REFUSED, "Navigation needs one captured directory.", null));
            }

            /* The listing.  The current folder is NOT moved yet: a listing     */
            /* that fails must leave the pane's folder -- and so the folder a   */
            /* later change re-lists -- where it was.                           */
            sFolder = objRequest.RQT_lstPaths[0];
            try
            {   /* The List step, on the folder the request carries. */
                objResult = Sys_Do(STP_enmStep.List, objRequest, objExecution);
            }
            catch (Exception exList)
            {   /* The listing threw: nothing was changed.  It becomes a         */
                /* failed result, so the window still gets its listing and       */
                /* can settle the action that asked for it.  The detail is the   */
                /* exception's message, which the window puts in the one-line    */
                /* status bar (MainCommand.cs); the whole exception, with its    */
                /* stack, goes to the debug log.                                 */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("folder listing", exList.ToString());
                }

                /* The failed result, its detail the message the status bar shows. */
                objResult = new RSL_clsResult(SYS_INTERNAL, exList.Message, null);
            }

            if (objResult.RSL_iStatus == SYS_OK && objRequest.RQT_lOrigin == 0)
            {   /* Read, and asked for by the main pane: the current folder   */
                /* moves.  A picker's listing never moves it.                 */
                Sys_sMainFolder = sFolder;
            }

            /* The finished listing to the window: the folder asked for (also   */
            /* on a failure, for the message), the epoch now, and the result.   */
            /* The window checks the epoch and folder before it draws; it       */
            /* takes no next step from this.                                    */
            SYS_Show(new VWM_clsView(VWK_enmViewKind.Folder, objRequest,
                new FVW_clsFolderView(sFolder, Sys_lEpoch, objResult, null)));

            /* The listing's own result goes back to the scheduler. */
            return(objResult);
        }
    }
}
