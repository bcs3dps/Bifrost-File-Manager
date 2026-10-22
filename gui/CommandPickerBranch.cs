/*----------------------------------------------------------------------------*/
/* CommandPickerBranch.cs                                                     */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The listings an image folder picker (the image Save As dialog, the         */
/* folder choosers) needs to open its tree at a starting folder: the root     */
/* and every folder down to the start, each listed in turn by the command     */
/* worker as one sequence, and handed to the dialog together.  A folder that  */
/* cannot be listed ends the branch there, with the folders before it kept,   */
/* so the dialog can show them and say which folder failed.                   */
/*----------------------------------------------------------------------------*/

/* The framework: the lists the branch and each listing's rows are kept in   */
/* (System.Collections.Generic).                                             */
using System.Collections.Generic;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* BRV_clsBranchView -- one finished branch, root first, as delivered to  */
    /* the dialog; the last listing is the failed one when a read failed.     */
    /* The two flags are the request's choice of what each listing keeps --   */
    /* documentation table.                                                   */
    /*------------------------------------------------------------------------*/
    internal sealed class BRV_clsBranchView
    {
        internal const uint BRV_ALL_DIRECTORIES = 1;      // RQT_uiFlags: every listing keeps folders only (a folder chooser)
        internal const uint BRV_PARENT_DIRECTORIES = 2;   // RQT_uiFlags: the parents keep folders only, the last folder keeps everything (the Save dialog, which shows the files where it saves)
        internal List<FVW_clsFolderView> BRV_lstFolders;  // the listings, root first; finished before the view is made, and not changed after

        /*--------------------------------------------------------------------*/
        /* BRV_clsBranchView:                                                 */
        /*                                                                    */
        /* The branch, wrapping the finished list as it is.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstFolders : the listings, root first, the failed one last     */
        /*                  when a read failed.                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the view.                                      */
        /*--------------------------------------------------------------------*/
        internal BRV_clsBranchView(List<FVW_clsFolderView> lstFolders)
        {
            /* The list itself, not a copy: the worker has finished adding to   */
            /* it before the view is made, and the dialog only reads it, so a   */
            /* copy or a read-only wrapper would protect nothing.               */
            BRV_lstFolders = lstFolders;
        }
    }

    internal sealed partial class SYS_clsSystem
    {
        /*--------------------------------------------------------------------*/
        /* Sys_DirectorySnapshot:                                             */
        /*                                                                    */
        /* A listing cut down to its folders, for a branch folder that only   */
        /* needs its tree nodes: the files of every parent would be kept for  */
        /* nothing while the rest of the branch is read.  The listing's own   */
        /* status and detail are kept, so a read that failed or was partial   */
        /* is never made to look whole by the trimming.                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objResult : the List step's result.                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the result with its rows cut to folders; the   */
        /*                     failure itself when no rows came; SYS_INTERNAL */
        /*                     for a "success" with no rows.                  */
        /*--------------------------------------------------------------------*/
        private static RSL_clsResult Sys_DirectorySnapshot(RSL_clsResult objResult)
        {
            ELR_clsListResult objListing;
            ELR_clsListResult objTree;
            int i;

            /* The listing's rows, if it has any. */
            objListing = objResult.RSL_objPayload as ELR_clsListResult;
            if (objListing == null || objListing.ELR_lstEntries == null)
            {   /* No rows to trim. */
                if (objResult.RSL_iStatus == SYS_OK)
                {   /* "Success" with no rows is a fault in the step, not an   */
                    /* empty folder: refused as internal.                      */
                    return(new RSL_clsResult(SYS_INTERNAL,
                        "The listing step returned no directory data.", null));
                }

                /* A failed read: its own result, reason and all. */
                return(objResult);
            }

            /* A new listing with the old one's facts -- status, detail,      */
            /* folder -- and only its folder rows.  The rows are structures   */
            /* copied by value; the name buffers they point to are shared,    */
            /* and only read (decoded by the dialog).                         */
            objTree = new ELR_clsListResult();
            objTree.ELR_iStatus = objListing.ELR_iStatus;
            objTree.ELR_sDetail = objListing.ELR_sDetail;
            objTree.ELR_sImageDir = objListing.ELR_sImageDir;
            objTree.ELR_lstEntries = new List<EEN_stEntry>();
            for (i = 0; i < objListing.ELR_lstEntries.Count; i++)
            {   /* One row per iteration. */
                if (objListing.ELR_lstEntries[i].EEN_uiType == ENG_clsNative.ENG_TYPE_DIR)
                {   /* A folder: kept.  A link to a folder is left out: the     */
                    /* engine types an entry by its own mode word, so a link    */
                    /* is ENG_TYPE_SYMLINK whatever it points at.  Whether a    */
                    /* linked folder should show in the tree is not recorded.   */
                    objTree.ELR_lstEntries.Add(objListing.ELR_lstEntries[i]);
                }
            }

            /* The trimmed rows under the step's own status and detail. */
            return(new RSL_clsResult(objResult.RSL_iStatus, objResult.RSL_sDetail, objTree));
        }

        /*--------------------------------------------------------------------*/
        /* Sys_PickerBranchSequence:                                          */
        /*                                                                    */
        /* The sequence for a picker's branch: list each folder of the        */
        /* captured path in turn, root first, as steps of this one command,   */
        /* so no other request runs between them.  The source and epoch were  */
        /* checked when the request was taken (the system's gate).            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the request: the picker that asked (origin),    */
        /*                    the folders root first, and the flags saying    */
        /*                    what each listing keeps.                        */
        /*     objExecution : the running command, for the List steps and     */
        /*                    their interrupt checks.                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : SYS_OK with the whole branch; otherwise the    */
        /*                     failed listing's status and detail, with the   */
        /*                     branch up to and including that folder.        */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_PickerBranchSequence(RQT_clsRequest objRequest,
            EXC_clsExecution objExecution)
        {
            List<FVW_clsFolderView> lstFolders;
            RQT_clsRequest objListing;
            RSL_clsResult objResult;
            bool bDirectories;
            int i;

            /* The listings, root first.  The folders are the ones captured    */
            /* when the request was posted, never the picker's tree as it is   */
            /* now.                                                            */
            lstFolders = new List<FVW_clsFolderView>();
            for (i = 0; i < objRequest.RQT_lstPaths.Count; i++)
            {   /* One folder of the branch per iteration, listed before its     */
                /* child is.  Its listing is a request of its own for the List   */
                /* step -- one folder, this epoch and target -- run here, not    */
                /* posted, so it never enters the queue.                         */
                objListing = new RQT_clsRequest(KND_enmKind.PickerList,
                    objRequest.RQT_lOrigin, Sys_lEpoch, Sys_objTarget,
                    new string[] { objRequest.RQT_lstPaths[i] }, 0, 0, null, "", "",
                    objRequest.RQT_lstPaths[i]);
                objResult = Sys_Do(STP_enmStep.List, objListing, objExecution);

                /* Folders only, unless this is the Save dialog's LAST folder,   */
                /* where the files are shown (the user saves among them).        */
                bDirectories = (objRequest.RQT_uiFlags & BRV_clsBranchView.BRV_ALL_DIRECTORIES) != 0 ||
                    ((objRequest.RQT_uiFlags & BRV_clsBranchView.BRV_PARENT_DIRECTORIES) != 0 &&
                    i + 1 < objRequest.RQT_lstPaths.Count);
                if (bDirectories)
                {   /* Cut to its folders, its status kept as it was. */
                    objResult = Sys_DirectorySnapshot(objResult);
                }

                /* The listing joins the branch -- a failed one too, so the   */
                /* dialog can name the folder that failed.                    */
                lstFolders.Add(new FVW_clsFolderView(objRequest.RQT_lstPaths[i],
                    Sys_lEpoch, objResult, null));
                if (objResult.RSL_iStatus != SYS_OK)
                {   /* The read failed: the folders below it are not listed    */
                    /* (their parent could not be read).  The branch so far,   */
                    /* under the failure's status and detail.                  */
                    return(new RSL_clsResult(objResult.RSL_iStatus, objResult.RSL_sDetail,
                        new BRV_clsBranchView(lstFolders)));
                }
            }

            /* Every folder listed: the whole branch. */
            return(new RSL_clsResult(SYS_OK, "", new BRV_clsBranchView(lstFolders)));
        }
    }
}
