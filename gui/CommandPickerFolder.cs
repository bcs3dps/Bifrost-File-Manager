/*----------------------------------------------------------------------------*/
/* CommandPickerFolder.cs                                                     */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The image Save As picker's New Folder, run as one command on the worker:   */
/* the name is asked for and checked, the filesystem is made writable if it   */
/* is not yet, the folder is made and committed, and then the folder it was   */
/* made in and the new folder itself are listed back for the picker           */
/* (ImageSaveForm.cs), which shows and enters the new folder from those       */
/* finished listings without asking for anything more.                        */
/*                                                                            */
/* Two records go with it: the request's payload (PFA_clsPickerFolderArgs),   */
/* which ties the command to the picker and to the destination its editor     */
/* had when the picker opened, and the result's payload                       */
/* (MFV_clsMadeFolderView), which says the folder was made even when the      */
/* listings after it did not finish.  The folder then exists, and the picker  */
/* must say so rather than leave the user to make it again -- a second try    */
/* would be refused, the name being taken.                                    */
/*----------------------------------------------------------------------------*/

/* The framework: the base types (the exception thrown for a missing        */
/* binding, the string tests, the exception caught after the commit), the   */
/* list of folder listings read back, and Encoding for a name's UTF-8       */
/* length.                                                                  */
using System;
using System.Collections.Generic;
using System.Text;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* PFA_clsPickerFolderArgs -- the payload of a picker's New Folder        */
    /* request (KND_enmKind.EditorMakeFolder).  The request itself carries    */
    /* the editor's id (its origin), the filesystem and lifetime it was       */
    /* posted against, and the folder to make the new one in; this adds what  */
    /* binds the command to the picker and to the destination the editor had  */
    /* when the picker opened -- documentation table.                         */
    /*------------------------------------------------------------------------*/
    internal sealed class PFA_clsPickerFolderArgs
    {
        internal long PFA_lPicker;          // the picker window's id: negative (the constructor refuses others), never its editor's; owns the name and first-change questions
        internal string PFA_sExpectedEntry; // the editor's destination (EDR_sEntry) when the request was made; the sequence refuses if it has changed
        internal long PFA_lExpectedOrder;   // the editor's save order (EDR_lSaveOrder) then; catches a new destination of the same name, which the entry alone would miss

        /*--------------------------------------------------------------------*/
        /* PFA_clsPickerFolderArgs:                                           */
        /*                                                                    */
        /* The payload, refusing a request that has no picker or no editor    */
        /* destination to bind to.                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lPicker        : the picker window's id; must be negative --   */
        /*                      it owns the name and first-change questions.  */
        /*     sExpectedEntry : the editor's destination (EDR_sEntry) as it   */
        /*                      stands now; must not be empty.                */
        /*     lExpectedOrder : the editor's save order (EDR_lSaveOrder) as   */
        /*                      it stands now.  Optional, 0 by default; the   */
        /*                      one caller (ImageSaveForm.cs) always passes   */
        /*                      it.                                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the payload; ArgumentException for a missing   */
        /*                     picker or destination, thrown before any       */
        /*                     request is posted.                             */
        /*--------------------------------------------------------------------*/
        internal PFA_clsPickerFolderArgs(long lPicker, string sExpectedEntry, long lExpectedOrder = 0)
        {
            if (lPicker >= 0 || String.IsNullOrEmpty(sExpectedEntry))
            {   /* Not a picker's id -- zero is the main window's, and the      */
                /* folder's questions would go there -- or no destination for   */
                /* the binding test to compare.  Refused by throwing, at the    */
                /* caller, before a request exists.                             */
                throw new ArgumentException("Directory creation needs a picker and editor binding.");
            }

            /* The three facts, kept as given.  Nothing else is held -- no      */
            /* control, no callback, no pane's current value -- so the worker   */
            /* acts on what was captured when New Folder was clicked; the       */
            /* editor, the filesystem and the parent are in the request.        */
            PFA_lPicker = lPicker;
            PFA_sExpectedEntry = sExpectedEntry;
            PFA_lExpectedOrder = lExpectedOrder;
        }
    }

    /*------------------------------------------------------------------------*/
    /* MFV_clsMadeFolderView -- the payload of a New Folder result once the   */
    /* folder has been made and committed.  Its presence says the folder      */
    /* exists, whatever the status: a status other than SYS_OK with this      */
    /* payload means the folder was made but its listings did not all         */
    /* finish -- documentation table.                                         */
    /*------------------------------------------------------------------------*/
    internal sealed class MFV_clsMadeFolderView
    {
        internal string MFV_sParent;              // the folder the new one was made in, as the request captured it (not where the picker is now)
        internal string MFV_sCreated;             // the new folder's full path in the image -- made and committed, whatever the listings did
        internal BRV_clsBranchView MFV_objBranch; // the listings read back, the parent's (folders only) then the new folder's, ending at the first that failed

        /*--------------------------------------------------------------------*/
        /* MFV_clsMadeFolderView:                                             */
        /*                                                                    */
        /* The record of a folder made, with whatever listings of it          */
        /* finished.                                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sParent   : the folder it was made in, as the request          */
        /*                 captured it (not wherever the picker is now).      */
        /*     sCreated  : the new folder's full path in the image.           */
        /*     objBranch : the listings read: the parent's (folders only),    */
        /*                 then the new folder's, ending at the first that    */
        /*                 failed, which is kept with its failure.            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the record.                                    */
        /*--------------------------------------------------------------------*/
        internal MFV_clsMadeFolderView(string sParent, string sCreated, BRV_clsBranchView objBranch)
        {
            /* Kept as given.  The record exists so the picker can say the    */
            /* folder was made even when its listings failed: told only       */
            /* "failed", the user would try again, and the second try would   */
            /* be refused because the name is now taken.                      */
            MFV_sParent = sParent;
            MFV_sCreated = sCreated;
            MFV_objBranch = objBranch;
        }
    }

    internal sealed partial class SYS_clsSystem
    {
        /*--------------------------------------------------------------------*/
        /* Sys_FolderNameProblem:                                             */
        /*                                                                    */
        /* Whether a typed name can be the new folder's: one name inside the  */
        /* folder shown, never a path, and short enough for the filesystem:   */
        /* 255 UTF-8 bytes on ext2/3/4, 255 UTF-16 units on FAT and exFAT.    */
        /* The main window's New Folder makes the same tests in its own       */
        /* sequence (CommandOperations.cs), with its own wording.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sName : the name as typed, already trimmed.                    */
        /*     bFat  : true when the image's filesystem is FAT or exFAT.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : "" when the name can be used; otherwise the sentence  */
        /*              to show.                                              */
        /*--------------------------------------------------------------------*/
        private static string Sys_FolderNameProblem(string sName, bool bFat)
        {
            if (String.IsNullOrEmpty(sName))
            {   /* Nothing typed, or only spaces (trimmed away): no name to   */
                /* make, and not taken to mean the folder shown.              */
                return("Type a folder name.");
            }

            if (sName == "." || sName == ".." || sName.IndexOf('/') >= 0 || sName.IndexOf((char)0) >= 0)
            {   /* "." and ".." name the folder itself and its parent, and a   */
                /* "/" would make a path reaching into or out of another       */
                /* folder: each would be something other than one new folder   */
                /* in the one shown.  A NUL would end the name early, as the   */
                /* engine takes the path as a NUL-terminated UTF-8 string.     */
                return("Choose a folder name without slash, NUL, dot or dot-dot path components.");
            }

            if (!bFat && Encoding.UTF8.GetByteCount(sName) > 255)
            {   /* Too long for ext: an ext directory entry holds a name of    */
                /* at most 255 BYTES, stored as UTF-8, so the encoded bytes    */
                /* are counted -- a count of characters would pass a name of   */
                /* multi-byte characters that does not fit.                    */
                return("The folder name exceeds 255 UTF-8 bytes.");
            }

            if (bFat && sName.Length > 255)
            {   /* Too long for FAT: a long name holds at most 255 UTF-16      */
                /* units -- the string's own length -- as Windows' names do.   */
                return("The folder name exceeds 255 characters.");
            }

            /* A name the filesystem can hold.  The names Windows reserves     */
            /* (CON, a trailing dot ...) are not refused: they are ordinary    */
            /* names in a Linux filesystem.  When the image's filesystem is    */
            /* FAT or exFAT the caller holds the name to FAT's rules as well   */
            /* (SYS_FatNameProblem, CommandOperations.cs).                     */
            return("");
        }

        /*--------------------------------------------------------------------*/
        /* Sys_PickerFolderSequence:                                          */
        /*                                                                    */
        /* The picker's New Folder (KND_enmKind.EditorMakeFolder): the name   */
        /* asked for, the folder made in the folder the picker shows, and its */
        /* listings read back for the picker -- all as steps of this one      */
        /* command, so no other request runs between the making and the       */
        /* reading.  Refused when the editor the picker serves has been       */
        /* closed or has taken another destination since the picker opened.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the request: the editor (its origin), the       */
        /*                    parent folder (its one path) and the payload    */
        /*                    (PFA_clsPickerFolderArgs).                      */
        /*     objExecution : the running command, for the questions, the     */
        /*                    steps and their interrupt checks.               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : SYS_OK with an MFV_clsMadeFolderView when the  */
        /*                     folder is made and both listings read; a       */
        /*                     refusal, a cancel or a failure with no payload */
        /*                     when it was not made; a listing's failure (or  */
        /*                     SYS_INTERNAL) WITH the record when it was made */
        /*                     and the reading did not finish.                */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_PickerFolderSequence(RQT_clsRequest objRequest,
            EXC_clsExecution objExecution)
        {
            PFA_clsPickerFolderArgs objArgs;
            EDR_clsEditor objEditor;
            ANS_clsAnswer objAnswer;
            RSL_clsResult objResult;
            RQT_clsRequest objMutation;
            RQT_clsRequest objListing;
            List<FVW_clsFolderView> lstFolders;
            string sParent;
            string sName;
            string sCreated;
            string sProblem;
            string sPath;
            string[] arrComponents;
            string[] arrReadPaths;
            bool bFat;
            int i;

            /* The binding.  The system's gate has already found this           */
            /* request's filesystem open, in the same lifetime (epoch) and      */
            /* range.  What it cannot check is the editor: it must still be     */
            /* registered (in either set, Sys_FindEditor) with the              */
            /* destination and save order the picker was opened with.  A        */
            /* picker whose editor has closed, or has saved elsewhere since,    */
            /* has nothing to make a folder for; the save order catches a new   */
            /* destination of the same name, which the entry alone would not.   */
            objArgs = objRequest.RQT_objPayload as PFA_clsPickerFolderArgs;
            objEditor = Sys_FindEditor(objRequest.RQT_lOrigin);
            if (objArgs == null || objEditor == null ||
                objEditor.EDR_sEntry != objArgs.PFA_sExpectedEntry ||
                objEditor.EDR_lSaveOrder != objArgs.PFA_lExpectedOrder ||
                objRequest.RQT_lstPaths.Count != 1)
            {   /* No payload, the editor gone, a destination or save order      */
                /* that has changed, or not exactly one parent: refused before   */
                /* any question is asked or anything written.                    */
                return(new RSL_clsResult(SYS_REFUSED, "The Save As editor binding has changed.", null));
            }

            /* The parent: the request's one path, which must be an absolute   */
            /* image path spelled plainly -- starting "/", no NUL, no "//",    */
            /* no trailing "/" but root's own.  Another spelling is refused    */
            /* rather than tidied: tidied, it could name another folder than   */
            /* the one the picker showed, and the new folder's path is built   */
            /* below from this text as it stands.                              */
            sParent = objRequest.RQT_lstPaths[0];
            if (sParent.Length == 0 || sParent[0] != '/' || sParent.IndexOf((char)0) >= 0 ||
                (sParent.Length > 1 && sParent.EndsWith("/", StringComparison.Ordinal)) ||
                sParent.IndexOf("//", StringComparison.Ordinal) >= 0)
            {   /* Not such a path: refused, nothing yet asked or written. */
                return(new RSL_clsResult(SYS_REFUSED, "The Save As parent directory is invalid.", null));
            }

            /* Then its components: a "." or ".." would make the path name     */
            /* another folder than it spells, so each is refused.  The empty   */
            /* first component -- before the leading "/" -- is expected and    */
            /* passes; the tests above leave no other empty one.               */
            arrComponents = sParent.Split('/');
            for (i = 0; i < arrComponents.Length; i++)
            {   /* One component of the parent per iteration. */
                if (arrComponents[i] == "." || arrComponents[i] == "..")
                {   /* A "." or "..": refused, nothing yet asked or written. */
                    return(new RSL_clsResult(SYS_REFUSED,
                        "The Save As parent contains a relative component.", null));
                }
            }

            /* The name, asked for with the PICKER as the question's owner:    */
            /* the picker is shown modally over its editor (ShowDialog,        */
            /* MainCommand.cs), so the editor cannot take a question.  The     */
            /* worker's question channel keeps the text typed so far, so a     */
            /* prompt that is interrupted (by the Exit question) is shown      */
            /* again with it; the prompt names the parent (Qpr_FolderName,     */
            /* CommandQuestionPresenter.cs).  Nothing has been made writable   */
            /* yet, so a Cancel leaves the filesystem as it was.               */
            objAnswer = objExecution.EXC_Ask(QNK_enmQuestionKind.FolderName,
                objArgs.PFA_lPicker, sParent);
            if (objAnswer == null || objAnswer.ANS_enmKind != ANK_enmAnswerKind.Yes)
            {   /* Cancelled, the window gone, or interrupted (which answers   */
                /* Cancel): nothing made writable or written.                  */
                return(new RSL_clsResult(SYS_CANCELLED, "Directory creation declined.", null));
            }

            /* The name, trimmed of the spaces round it as the program's        */
            /* other name boxes do (the main window's New Folder, the Save As   */
            /* name box), and checked here, where the folder is made -- the     */
            /* prompt checks nothing.  The check comes before the               */
            /* first-change question, so a bad name never makes the             */
            /* filesystem writable for nothing.                                 */
            sName = objAnswer.ANS_sText.Trim();

            /* Whether the image's filesystem is FAT or exFAT, for the length   */
            /* test and for FAT's own rules below (the worker's record of the   */
            /* session, Sys_OpenSession).                                       */
            bFat = Sys_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_FAT12 ||
                Sys_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_FAT16 ||
                Sys_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_FAT32 ||
                Sys_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_EXFAT;
            sProblem = Sys_FolderNameProblem(sName, bFat);
            if (sProblem.Length != 0)
            {   /* Not a usable name: refused with its reason (ENG_ERR_ARG, as   */
                /* the main window's New Folder answers), nothing written.       */
                return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG, sProblem, null));
            }

            if (bFat)
            {   /* The image's filesystem is FAT or exFAT: its own rules too   */
                /* -- the characters it forbids, a name of spaces and dots     */
                /* alone -- as the main window's New Folder holds them         */
                /* (SYS_FatNameProblem), checked before write access.          */
                sProblem = SYS_FatNameProblem(sName);
                if (sProblem.Length != 0)
                {   /* A name FAT cannot hold: refused, nothing written. */
                    return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG, sProblem, null));
                }
            }

            /* The new folder's path: the parent, one "/" and the name.  Root   */
            /* already ends in its "/", so under root it is "/" and the name.   */
            sCreated = "/" + sName;
            if (sParent != "/")
            {   /* Any other parent: its text exactly as captured, then "/" and the name. */
                sCreated = sParent + "/" + sName;
            }

            /* The make-folder request the steps take: built here and never      */
            /* posted -- it is a step of this command, not a queued request --   */
            /* with no staged content (a folder has none).  Its origin is the    */
            /* PICKER, not the editor, because the first-change question goes    */
            /* to the origin's window and the editor is disabled under the       */
            /* modal picker: asked there, it could not be answered.  The         */
            /* filesystem and range are the worker's, which the gate has just    */
            /* found equal to the request's.                                     */
            objMutation = new RQT_clsRequest(KND_enmKind.Edit, objArgs.PFA_lPicker,
                Sys_lEpoch, Sys_objTarget, new string[] { sCreated }, 0, 0,
                new EDA_clsEditArgs(EDK_enmEditKind.MakeDirectory, null, null,
                    false, 0, false, 0, 0, null, null), "", "", objRequest.RQT_sFolder);
            /* Write access (Sys_FirstChange, CommandSequences.cs): when the     */
            /* filesystem is still read-only, the risk question (if the          */
            /* warnings are on) and a reopen for writing in the same lifetime.   */
            objResult = Sys_FirstChange(objMutation, objExecution);
            if (objResult.RSL_iStatus != SYS_OK)
            {   /* Declined, or the reopen failed.  A failed writable reopen   */
                /* may have fallen back to a read-only one, which keeps the    */
                /* image open but gives no right to write: the failure is      */
                /* returned and nothing is made.                               */
                return(objResult);
            }

            /* Make the folder and commit it (ENGS_DoMakeDir: the create, then   */
            /* its commit), through Sys_Do, which first checks for a Cancel or   */
            /* an accepted Exit.  A name already taken is refused here           */
            /* (ENG_ERR_EXISTS).  This finishes before either listing below.     */
            objResult = Sys_Do(STP_enmStep.Edit, objMutation, objExecution);
            if (objResult.RSL_iStatus != SYS_OK)
            {   /* Not made, or not committed: the step's own failure, with no   */
                /* record -- the record is only for a folder made and            */
                /* committed.                                                    */
                return(objResult);
            }

            /* From here the folder exists, and every return says so (with an   */
            /* MFV record).  The picker needs two listings: the parent's, for   */
            /* its tree, then the new folder's, for the entries it shows on     */
            /* entering it.  Only listings actually read are kept; a failure    */
            /* ends the reading but undoes nothing -- the folder stays.         */
            lstFolders = new List<FVW_clsFolderView>();
            arrReadPaths = new string[] { sParent, sCreated };
            try
            {   /* An exception from here on is caught below and reported with   */
                /* the folder named as made.                                     */
                for (i = 0; i < arrReadPaths.Length; i++)
                {   /* One folder's listing per iteration.                       */
                    /* The listing request: built here and never posted -- a     */
                    /* step of this command, with no life in the queue -- with   */
                    /* the picker as its origin.                                 */
                    sPath = arrReadPaths[i];
                    objListing = new RQT_clsRequest(KND_enmKind.PickerList,
                        objArgs.PFA_lPicker, Sys_lEpoch, Sys_objTarget,
                        new string[] { sPath }, 0, 0, null, "", "", sPath);
                    /* Read through Sys_Do, so a Cancel or an accepted Exit     */
                    /* now refuses it, and that shows below as a listing that   */
                    /* did not finish (Sys_Refresh, by contrast, reads past     */
                    /* that check).                                             */
                    objResult = Sys_Do(STP_enmStep.List, objListing, objExecution);
                    if (sPath == sParent)
                    {   /* The parent's listing, cut to its folders             */
                        /* (Sys_DirectorySnapshot): the picker uses it only     */
                        /* for its tree, and the files would be carried for     */
                        /* nothing.  A failed read keeps its status and         */
                        /* reason, so the trimming never makes it look whole.   */
                        objResult = Sys_DirectorySnapshot(objResult);
                    }

                    /* Kept whether it succeeded or not, so the picker can show   */
                    /* the failed folder with its failure; a failure then ends    */
                    /* the reading.                                               */
                    lstFolders.Add(new FVW_clsFolderView(sPath, Sys_lEpoch, objResult, null));
                    if (objResult.RSL_iStatus != SYS_OK)
                    {   /* The listing failed: the listing's own status, but     */
                        /* with the record and the listings read so far, and a   */
                        /* message that names the new folder as made first.      */
                        return(new RSL_clsResult(objResult.RSL_iStatus,
                            "Created " + sCreated + ", but its picker readback did not finish: " +
                            objResult.RSL_sDetail, new MFV_clsMadeFolderView(sParent,
                                sCreated, new BRV_clsBranchView(lstFolders))));
                    }
                }

                /* Both listed: the main pane is re-listed too (Sys_Refresh),    */
                /* as after any change.  Its outcome is shown in the main pane   */
                /* and not looked at here -- the folder is made whether or not   */
                /* the re-list works.  It runs only on this path: after a        */
                /* failed listing or an exception the main pane is not           */
                /* re-listed.                                                    */
                Sys_Refresh(objRequest, objExecution);
            }
            catch (Exception exRead)
            {   /* An exception after the commit, from a listing or the main    */
                /* pane's re-list.  The folder is made, so it is reported as    */
                /* made, with the listings read before it and the exception's   */
                /* message (its text, not its stack trace).                     */
                return(new RSL_clsResult(SYS_INTERNAL, "Created " + sCreated +
                    ", but its view could not be refreshed: " + exRead.Message,
                    new MFV_clsMadeFolderView(sParent, sCreated,
                        new BRV_clsBranchView(lstFolders))));
            }

            /* Made, and both listings read: SYS_OK with the record, from   */
            /* which the picker shows the new folder and enters it with     */
            /* nothing more to ask for.                                     */
            return(new RSL_clsResult(SYS_OK, "", new MFV_clsMadeFolderView(sParent,
                sCreated, new BRV_clsBranchView(lstFolders))));
        }
    }
}
