/*----------------------------------------------------------------------------*/
/* CommandRecycleSources.cs                                                   */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The last step of a CUT from Windows into the image: once the import has    */
/* copied every item, the Windows originals are sent to the Recycle Bin,      */
/* one at a time, through the framework's Visual Basic file calls, which      */
/* show Windows' own dialog when an item cannot be removed.  It               */
/* stops at the first item that cannot be removed or that the user cancels;   */
/* what was already recycled stays recycled, and the copies in the image      */
/* stay whatever happens here.                                                */
/*----------------------------------------------------------------------------*/

/* The framework: the exceptions caught (System), and the folder-or-file   */
/* test that picks the right recycle call (System.IO).                     */
using System;
using System.IO;

namespace BifrostFM
{
    internal sealed partial class NST_clsNativeSteps
    {
        /*--------------------------------------------------------------------*/
        /* Nst_RecycleCopySources:                                            */
        /*                                                                    */
        /* The Recycle step (CommandNativeSteps.cs): send the Windows         */
        /* sources of a cut-and-import to the Recycle Bin, after the copy     */
        /* sequence (CommandCopySequence.cs) has judged the import complete.  */
        /* It runs on the command worker's thread, which is STA               */
        /* (CommandSystem.cs) -- the apartment the shell's dialogs need.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the import request; its payload carries the     */
        /*                    Windows paths captured when the cut was made.   */
        /*     objExecution : the running command, asked before each item     */
        /*                    whether the user has interrupted it.            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : SYS_OK with the count recycled; SYS_CANCELLED  */
        /*                     or SYS_INTERNAL with the count recycled before */
        /*                     the stop and the reason; ENG_ERR_ARG for a     */
        /*                     request that is not a complete cut.  Nothing   */
        /*                     already recycled is put back.                  */
        /*--------------------------------------------------------------------*/
        private static RSL_clsResult Nst_RecycleCopySources(RQT_clsRequest objRequest,
            EXC_clsExecution objExecution)
        {
            CCA_clsCopyArgs objArgs;
            int iRemoved;
            string sPath;
            int i;

            /* The cut's captured arguments, and the request's shape checked   */
            /* before anything is removed: an import, marked as a move, with   */
            /* one captured Windows path for every image path.  Only paths     */
            /* captured with the cut may be recycled -- never the pane's       */
            /* selection as it is now, which may have changed since.           */
            objArgs = objRequest.RQT_objPayload as CCA_clsCopyArgs;
            if (objRequest.RQT_enmKind != KND_enmKind.Import || objArgs == null ||
                !objArgs.CCA_bMoveSources || objRequest.RQT_lstPaths.Count == 0 ||
                objArgs.CCA_lstOtherPaths.Count != objRequest.RQT_lstPaths.Count)
            {   /* Not a complete cut: refused before any Windows call, so   */
                /* nothing is removed.                                       */
                return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                    "No complete Windows cut-source selection was supplied.", null));
            }

            /* One item at a time, the count of those recycled kept for the     */
            /* result.  A recycle call cannot be interrupted part-way, so the   */
            /* user's interrupt is honoured BETWEEN items.  "The import is      */
            /* complete" means no item was skipped or failed, a conflict        */
            /* answered "skip" counted as skipped (CommandNativeControl.cs),    */
            /* so every path here was imported.                                 */
            iRemoved = 0;
            for (i = 0; i < objArgs.CCA_lstOtherPaths.Count; i++)
            {   /* One captured Windows source per iteration. */
                if (!objExecution.EXC_Checkpoint())
                {   /* Interrupted: no further item is recycled.  Those       */
                    /* already recycled stay so, and the copies stay in the   */
                    /* image.                                                 */
                    return(new RSL_clsResult(SYS_clsSystem.SYS_CANCELLED,
                        "Removing the moved Windows sources was cancelled.", iRemoved));
                }

                /* The path as it was captured with the cut. */
                sPath = objArgs.CCA_lstOtherPaths[i];
                try
                {   /* The three-argument calls: Windows' own error dialogs   */
                    /* only (no progress window), to the Recycle Bin.  With   */
                    /* no cancel option given they throw on a cancel          */
                    /* (documented), which is what stops the loop at a        */
                    /* cancelled item rather than going on to the next.       */
                    if (Directory.Exists(sPath))
                    {   /* A folder: recycled with everything inside it. */
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(sPath,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    }
                    else
                    {   /* Anything else is taken as a file: a missing one, or    */
                        /* one in use or not permitted, throws, and that is the   */
                        /* failure reported below.                                */
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(sPath,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    }

                    /* The call returned, so the item has gone to the bin. */
                    iRemoved++;
                }
                catch (OperationCanceledException exCancel)
                {   /* The user cancelled the item in Windows' dialog: the       */
                    /* cut is stopped here, reported as cancelled, not failed.   */
                    return(new RSL_clsResult(SYS_clsSystem.SYS_CANCELLED,
                        "Removing the moved item " + sPath + " was cancelled: " + exCancel.Message,
                        iRemoved));
                }
                catch (Exception exRemove)
                {   /* The item could not be recycled: the cut stops at it,     */
                    /* with Windows' reason and the path, and the items after   */
                    /* it are left where they are.                              */
                    return(new RSL_clsResult(SYS_clsSystem.SYS_INTERNAL,
                        "Removing the moved item " + sPath + " failed: " + exRemove.Message,
                        iRemoved));
                }
            }

            /* Every source recycled.  The copy sequence reports the whole cut   */
            /* and spends its clipboard.                                         */
            return(new RSL_clsResult(SYS_clsSystem.SYS_OK, "", iRemoved));
        }
    }
}
