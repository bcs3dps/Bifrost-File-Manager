/*----------------------------------------------------------------------------*/
/* CommandOperations.cs                                                       */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The command sequences for the ordinary operations: the dispatcher          */
/* (Sys_OperationSequence) that sends each request kind to its sequence, the  */
/* opening of an image file in an editor (its viewer chosen, asking when      */
/* needed, before the editor is registered), the plain-text fallback for a    */
/* structured file that would not load, the partition window's buttons        */
/* (Sys_TableEditSequence: each one's question asked and its edit made on a   */
/* copy of the window's table, or the request turned into the disk operation  */
/* it names) and those disk operations with their layout re-read, the image   */
/* pane's re-listing after a change, and the records these hand the UI.  All  */
/* of it runs on the command worker; the UI is given finished values only.    */
/*----------------------------------------------------------------------------*/

/* The framework (System): String, ArgumentException and the exceptions   */
/* caught; and the list of free gaps Add Partition's sequence gathers     */
/* (System.Collections.Generic).                                          */
using System;
using System.Collections.Generic;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* VWH_clsViewerHow -- how an editor open asks for its viewer: the low    */
    /* word is the Open / Open As choice (the menus' own numbers), and one    */
    /* bit above it carries the "open config files in the structured editor"  */
    /* option as it stood when the request was made -- documentation table.   */
    /*------------------------------------------------------------------------*/
    internal static class VWH_clsViewerHow
    {
        internal const uint VWH_AUTO = 0;             // Open: the viewer chosen from the content; once resolved, 0 means a picture
        internal const uint VWH_TEXT = 1;             // the text editor, which takes files up to OSC_TEXT_MAX_BYTES
        internal const uint VWH_HEX = 2;              // the hex editor, at any size
        internal const uint VWH_EDITOR = 100;         // the structured editor: this plus the format's id (VoorheesFormat); held to the text editor's OSC_TEXT_MAX_BYTES too
        internal const uint VWH_HOW_MASK = 65535;     // the low word: the choice itself
        internal const uint VWH_AUTO_CONFIG = 65536;  // the option bit: an Open of text may go to the structured editor (Open only)
    }

    /*------------------------------------------------------------------------*/
    /* EVW_clsEditorView -- the EditorOpen view's value: the registered       */
    /* editor, its read-out, and the viewer the sequence chose.  The UI makes */
    /* that editor without asking anything again -- documentation table.      */
    /*------------------------------------------------------------------------*/
    internal sealed class EVW_clsEditorView
    {
        internal EDR_clsEditor EVW_objEditor;  // the registration, which owns the read-out file from before this view is sent
        internal RSL_clsResult EVW_objResult;  // the read-out step's result (its OED record: the file, its size, its classification)
        internal uint EVW_uiHow;               // the viewer chosen: VWH_TEXT, VWH_HEX, VWH_EDITOR + format, or 0 for a picture
        internal bool EVW_bNewDocument;        // true for a New File opened in the structured editor: start it as a new document over the empty file

        /*--------------------------------------------------------------------*/
        /* EVW_clsEditorView:                                                 */
        /*                                                                    */
        /* The view's value, its parts kept as given.                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objEditor    : the registered editor.                          */
        /*     objResult    : the read-out's result.                          */
        /*     uiHow        : the viewer chosen.                              */
        /*     bNewDocument : start a new structured document; optional,      */
        /*                    false.                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the value.                                     */
        /*--------------------------------------------------------------------*/
        internal EVW_clsEditorView(EDR_clsEditor objEditor, RSL_clsResult objResult,
            uint uiHow, bool bNewDocument = false)
        {
            /* Kept together, so the UI makes the editor from what the         */
            /* sequence decided -- never from the pane as it is by then, and   */
            /* without asking the viewer question a second time.               */
            EVW_objEditor = objEditor;
            EVW_objResult = objResult;
            EVW_uiHow = uiHow;
            EVW_bNewDocument = bNewDocument;
        }
    }

    /*------------------------------------------------------------------------*/
    /* VCH_clsViewerChoice -- what the binary and the too-large questions     */
    /* show: facts only, no file or registration -- documentation table.      */
    /*------------------------------------------------------------------------*/
    internal sealed class VCH_clsViewerChoice
    {
        internal bool VCH_bAllowText;  // the read-out is small enough for the text editor, so its button is offered
        internal long VCH_lBytes;      // the read-out's size, bytes
        internal string VCH_sName;     // the entry's name in the image (its last component)
        internal string VCH_sReason;   // the classifier's words for binary content ("It looks like ..."); "" when none

        /*--------------------------------------------------------------------*/
        /* VCH_clsViewerChoice:                                               */
        /*                                                                    */
        /* The question's facts.                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bAllowText : the text editor would take it.                    */
        /*     lBytes     : its size, bytes, not negative.                    */
        /*     sName      : its name, not empty.                              */
        /*     sReason    : the classifier's words, or null.                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the facts; an ArgumentException for a negative */
        /*                     size or no name.                               */
        /*--------------------------------------------------------------------*/
        internal VCH_clsViewerChoice(bool bAllowText, long lBytes, string sName, string sReason)
        {
            if (lBytes < 0 || String.IsNullOrEmpty(sName))
            {   /* No name or a negative size: refused -- neither is ever   */
                /* filled in from what the pane shows.                      */
                throw new ArgumentException("A viewer choice needs its captured filename and size.");
            }

            /* The facts as given, the reason "" when none. */
            VCH_bAllowText = bAllowText;
            VCH_lBytes = lBytes;
            VCH_sName = sName;
            VCH_sReason = "";
            if (sReason != null)
            {   /* Words given: kept (shown only, never an answer). */
                VCH_sReason = sReason;
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* VFA_clsViewerFallbackArgs -- the ViewerFallback request's arguments: a */
    /* structured editor would not load a file, and whether to open it as     */
    /* text is asked.  It holds only the refusal, not the failed editor or    */
    /* its read-out -- documentation table.                                   */
    /*------------------------------------------------------------------------*/
    internal sealed class VFA_clsViewerFallbackArgs
    {
        internal bool VFA_bHost;      // true for a Windows file; false for an image file (the request's epoch and target then hold it to that image)
        internal int VFA_iFormat;     // the format that refused it, named in the question's text (CommandQuestionPresenter.cs, "is not <format>")
        internal string VFA_sReason;  // the parser's words, with the line and column when it gave them

        /*--------------------------------------------------------------------*/
        /* VFA_clsViewerFallbackArgs:                                         */
        /*                                                                    */
        /* The refusal, as given.                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bHost   : a Windows file, not an image file.                   */
        /*     iFormat : the format that refused it.                          */
        /*     sReason : the parser's words, not empty.                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the arguments; an ArgumentException with no    */
        /*                     reason.  The file itself is the request's.     */
        /*--------------------------------------------------------------------*/
        internal VFA_clsViewerFallbackArgs(bool bHost, int iFormat, string sReason)
        {
            if (String.IsNullOrEmpty(sReason))
            {   /* No reason: refused -- the question would have nothing to   */
                /* tell the user why.                                         */
                throw new ArgumentException("A viewer fallback needs the parser's refusal reason.");
            }

            /* Kept as given; no editor, document or read-out goes with them. */
            VFA_bHost = bHost;
            VFA_iFormat = iFormat;
            VFA_sReason = sReason;
        }
    }

    /*------------------------------------------------------------------------*/
    /* HVW_clsHostViewerView -- the HostEditorOpen view's value: a Windows    */
    /* file to open in an editor, with no read-out (the file is its own) --   */
    /* documentation table.                                                   */
    /*------------------------------------------------------------------------*/
    internal sealed class HVW_clsHostViewerView
    {
        internal string HVW_sPath;  // the Windows file's full path; never a read-out to delete
        internal uint HVW_uiHow;    // the viewer chosen: VWH_TEXT or VWH_HEX

        /*--------------------------------------------------------------------*/
        /* HVW_clsHostViewerView:                                             */
        /*                                                                    */
        /* The view's value.                                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sPath  : the Windows file.                                     */
        /*     uiHow  : the viewer; optional, VWH_TEXT (the fallback's).      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the value; the UI gives the editor its own id  */
        /*                     and registers it.                              */
        /*--------------------------------------------------------------------*/
        internal HVW_clsHostViewerView(string sPath, uint uiHow = VWH_clsViewerHow.VWH_TEXT)
        {
            /* Kept as given.  The file is the user's own: closing the image   */
            /* neither saves it nor deletes it.                                */
            HVW_sPath = sPath;
            HVW_uiHow = uiHow;
        }
    }

    /*------------------------------------------------------------------------*/
    /* RPN_clsRepairNeeded -- an expansion's successful check, and whether it */
    /* found errors a repair must fix before the resize (Nst_Check,           */
    /* CommandNativeOperations.cs).  A check that could not run at all is a   */
    /* failed status instead, never this -- documentation table.              */
    /*------------------------------------------------------------------------*/
    internal sealed class RPN_clsRepairNeeded
    {
        internal bool RPN_bNeeded;      // true when the report-only check found errors: the Repair step runs before the resize
        internal object RPN_objReport;  // the check's own report (ECK_clsCheckResult), for the window to show

        /*--------------------------------------------------------------------*/
        /* RPN_clsRepairNeeded:                                               */
        /*                                                                    */
        /* The verdict and the report, kept as given.                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bNeeded   : the check found errors to repair.                  */
        /*     objReport : the check's report; not changed afterwards.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the record.                                    */
        /*--------------------------------------------------------------------*/
        internal RPN_clsRepairNeeded(bool bNeeded, object objReport)
        {
            /* Kept as given: "found errors" and "could not check" are told   */
            /* apart by the status, this record exists only for the former.   */
            RPN_bNeeded = bNeeded;
            RPN_objReport = objReport;
        }
    }

    /*------------------------------------------------------------------------*/
    /* RPO_clsPartitionOutcome -- a partition window change's outcome: the    */
    /* change's own result and the layout read again after it, and for an     */
    /* expansion its check and repair.  The change and the re-read are kept   */
    /* apart: a re-read that fails does not undo a change that was made --    */
    /* documentation table.                                                   */
    /*------------------------------------------------------------------------*/
    internal sealed class RPO_clsPartitionOutcome
    {
        internal RSL_clsResult RPO_objOperation;  // the change's result (a table write, format, resize, partition write or erase), as its step returned it
        internal RSL_clsResult RPO_objLayout;     // the layout read after it, or that read's failure
        internal RSL_clsResult RPO_objCheck;      // an expansion's report-only check; null otherwise
        internal RSL_clsResult RPO_objRepair;     // an expansion's repair, when one ran; null otherwise

        /*--------------------------------------------------------------------*/
        /* RPO_clsPartitionOutcome:                                           */
        /*                                                                    */
        /* The outcome, its parts kept as given.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objOperation : the change's result.                            */
        /*     objLayout    : the re-read's result.                           */
        /*     objCheck     : an expansion's check, or null.                  */
        /*     objRepair    : an expansion's repair, or null.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the outcome; no report is copied.              */
        /*--------------------------------------------------------------------*/
        internal RPO_clsPartitionOutcome(RSL_clsResult objOperation, RSL_clsResult objLayout,
            RSL_clsResult objCheck, RSL_clsResult objRepair)
        {
            /* The change and the re-read: a failed re-read must not hide the   */
            /* change, nor a new table hide a resize's report.                  */
            RPO_objOperation = objOperation;
            RPO_objLayout = objLayout;

            /* An expansion's check and repair, kept so the window can show   */
            /* them without running either again.                             */
            RPO_objCheck = objCheck;
            RPO_objRepair = objRepair;
        }
    }

    internal sealed partial class SYS_clsSystem
    {
        /*--------------------------------------------------------------------*/
        /* Sys_Refresh:                                                       */
        /*                                                                    */
        /* Re-list the main pane's folder after a change, with the            */
        /* filesystem's facts, and publish them as a Folder view.  Only       */
        /* information: the change's own result is the caller's, and this     */
        /* never changes it.                                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the request that made the change (the view is   */
        /*                    addressed to it).                               */
        /*     objExecution : the running command's services.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the listing's outcome, already published.      */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_Refresh(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            RQT_clsRequest objListing;
            RSL_clsResult objResult;
            RSL_clsResult objFacts;
            EVR_clsVolumeResult objVolume;

            /* The folder: the main pane's as of its last successful listing    */
            /* (Sys_sMainFolder) -- not the folder the change was asked from,   */
            /* which may be older, and never a picker's.                        */
            objListing = new RQT_clsRequest(KND_enmKind.ListFolder, 0, Sys_lEpoch,
                Sys_objTarget, new string[] { Sys_sMainFolder }, 0, 0, null, "", "", Sys_sMainFolder);
            try
            {   /* An exception while listing must not turn the change into a   */
                /* failure: it is caught and becomes the listing's result.      */
                /* The listing calls the step adapter directly, past            */
                /* Sys_Do's checkpoint: after a CANCELLED copy that             */
                /* checkpoint refuses every step (it exists to stop             */
                /* another WRITE), and a refused listing would leave the        */
                /* pane empty while the items already copied are in the         */
                /* image.  A listing only reads, so it runs regardless.         */
                objResult = Sys_objSteps.STB_Run(STP_enmStep.List, objListing, objExecution);
            }
            catch (Exception exRefresh)
            {   /* The listing failed: its failure is the pane's to show; no    */
                /* step follows from it and nothing is undone.  The whole       */
                /* exception goes to the debug log; the detail the pane shows   */
                /* is its message.                                              */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("re-listing after a change", exRefresh.ToString());
                }

                /* The failed result, its detail the message the pane shows. */
                objResult = new RSL_clsResult(SYS_INTERNAL, exRefresh.Message, null);
            }

            /* The filesystem's facts too: a change (a copy, an edit, a save)    */
            /* has moved its used and free counts, and the status line shows     */
            /* them.  Read here, by the worker, beside the listing -- never by   */
            /* the UI chaining a read off a completion -- and past the           */
            /* checkpoint for the same reason as the listing (a read only).      */
            /* A failure or exception just leaves the figure as it was.          */
            objVolume = null;
            try
            {   /* One facts read on the open session. */
                objFacts = Sys_objSteps.STB_Run(STP_enmStep.VolumeInfo, objListing, objExecution);
                if (objFacts.RSL_iStatus == SYS_OK)
                {   /* Good facts ride with the listing. */
                    objVolume = objFacts.RSL_objPayload as EVR_clsVolumeResult;
                }
            }
            catch (Exception exVolume)
            {   /* Not fatal to the refresh: the figure stays as it was. */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("command", "volume facts after a change: " + exVolume.Message);
                }
            }

            /* The Folder view: the folder, the epoch it belongs to, the   */
            /* listing and the facts, together.                            */
            SYS_Show(new VWM_clsView(VWK_enmViewKind.Folder, objRequest,
                new FVW_clsFolderView(Sys_sMainFolder, Sys_lEpoch, objResult, objVolume)));

            /* The listing's outcome (the caller keeps the change's own). */
            return(objResult);
        }

        /*---------------------------------------------------------------------*/
        /* Sys_OpenEntrySequence:                                              */
        /*                                                                     */
        /* Open an image file in an editor: read it out (or take a new         */
        /* file's own stage), choose the viewer -- asking when the content     */
        /* needs it -- then register the editor and send the UI its view.      */
        /* Until the editor is registered this sequence owns the read-out and  */
        /* deletes it on every way out.                                        */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     objRequest   : the entry (RQT_lstPaths[0]), the Open / Open As  */
        /*                    choice and the config option (RQT_uiFlags), or   */
        /*                    after a New File the create (its payload).       */
        /*     objExecution : the running command's services.                  */
        /*                                                                     */
        /* Returns:                                                            */
        /*     RSL_clsResult : the read-out's result, once the editor is       */
        /*                     registered and its view taken; the read-out's   */
        /*                     failure; SYS_CANCELLED for a declined question, */
        /*                     a Cancel or Exit, or a UI gone; SYS_REFUSED     */
        /*                     with no entry.                                  */
        /*---------------------------------------------------------------------*/
        private RSL_clsResult Sys_OpenEntrySequence(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            RSL_clsResult objResult;
            EDR_clsEditor objEditor;
            EDA_clsEditArgs objCreated;
            OED_clsOpenedEntryData objContent;
            VCH_clsViewerChoice objChoice;
            ANS_clsAnswer objAnswer;
            uint uiHow;
            int iFormat;
            int iSlash;
            string sName;
            bool bTextAllowed;
            bool bRegistered;

            if (objRequest.RQT_lstPaths.Count == 0)
            {   /* No entry: refused -- nothing read out, no editor. */
                return(new RSL_clsResult(SYS_REFUSED, "No image entry selected.", null));
            }

            /* The read-out (Nst_ExtractEntry): on its failure the step has   */
            /* already deleted what it made, and nothing is registered.       */
            objResult = Sys_Do(STP_enmStep.ExtractEntry, objRequest, objExecution);
            if (objResult.RSL_iStatus != SYS_OK)
            {   /* Failed or cancelled: its result, no editor. */
                return(objResult);
            }

            /* A success must carry its record, which owns the read-out. */
            objContent = objResult.RSL_objPayload as OED_clsOpenedEntryData;
            if (objContent == null)
            {   /* None: a failure -- an editor is never registered without   */
                /* the file it works on.                                      */
                return(new RSL_clsResult(SYS_INTERNAL, "The entry extraction returned no viewer content.", null));
            }

            /* Not registered yet: until it is, the finally below deletes the   */
            /* read-out on every way out -- a declined question, a Cancel, an   */
            /* exception.                                                       */
            bRegistered = false;
            try
            {   /* The viewer chosen, then the editor registered and shown.      */
                /* The choice: the request's low word, or after a New File the   */
                /* create's own (its editor and format, chosen in New File).     */
                objCreated = objRequest.RQT_objPayload as EDA_clsEditArgs;
                uiHow = objRequest.RQT_uiFlags & VWH_clsViewerHow.VWH_HOW_MASK;
                if (objCreated != null)
                {   /* A New File: the create's choice. */
                    uiHow = (uint)objCreated.EDA_iOpenHow;
                }

                /* The facts the choice rests on, from the read-out itself --   */
                /* its size on disk and its classification -- never from the    */
                /* pane: whether the text editor would take it, and its name.   */
                bTextAllowed = objContent.OED_lBytes <= OSC_clsScanner.OSC_TEXT_MAX_BYTES;
                iSlash = objContent.OED_sEntry.LastIndexOf('/');
                sName = objContent.OED_sEntry.Substring(iSlash + 1);
                if (uiHow == VWH_clsViewerHow.VWH_AUTO && objContent.OED_iKind == OSC_clsScanner.OSC_KIND_TEXT)
                {   /* Open, and the content is text: plain text, unless the    */
                    /* config option was on when the request was made and the   */
                    /* name or content says a structured format.  The editor    */
                    /* still checks the format itself when it loads.            */
                    iFormat = VoorheesFormat.VF_DETECT;
                    if ((objRequest.RQT_uiFlags & VWH_clsViewerHow.VWH_AUTO_CONFIG) != 0)
                    {   /* The option was on: the format, by name or content. */
                        iFormat = OSC_clsScanner.OSC_IdentifyFormat(objContent.OED_ReadPath(),
                            sName, objContent.OED_lBytes);
                    }

                    /* Text by default; a format found means the structured       */
                    /* editor, numbered as the menus number it.  Either way the   */
                    /* size test below holds it: a format found by NAME is        */
                    /* chosen whatever the file's size, so that test, which       */
                    /* covers both editors, is never skipped for it.              */
                    uiHow = VWH_clsViewerHow.VWH_TEXT;
                    if (iFormat != VoorheesFormat.VF_DETECT)
                    {   /* A format: the structured editor. */
                        uiHow = VWH_clsViewerHow.VWH_EDITOR + (uint)iFormat;
                    }
                }

                if (uiHow == VWH_clsViewerHow.VWH_AUTO && objContent.OED_iKind != OSC_clsScanner.OSC_KIND_PICTURE)
                {   /* Open, and the content is neither text nor a picture: the   */
                    /* binary question -- hex, text (offered only when it is      */
                    /* small enough), or cancel -- with the classifier's words.   */
                    objChoice = new VCH_clsViewerChoice(bTextAllowed, objContent.OED_lBytes,
                        sName, objContent.OED_sWhy);
                    objAnswer = objExecution.EXC_Ask(QNK_enmQuestionKind.ViewerBinary, 0, objChoice);
                    if (objAnswer == null || (objAnswer.ANS_enmKind != ANK_enmAnswerKind.OpenHex &&
                        (objAnswer.ANS_enmKind != ANK_enmAnswerKind.OpenText || !bTextAllowed)))
                    {   /* Not hex, and not an allowed text: nothing opens (no   */
                        /* editor exists yet, so there is nothing to close).     */
                        return(new RSL_clsResult(SYS_CANCELLED, "Viewer choice declined.", null));
                    }

                    /* Hex, unless the answer was text. */
                    uiHow = VWH_clsViewerHow.VWH_HEX;
                    if (objAnswer.ANS_enmKind == ANK_enmAnswerKind.OpenText)
                    {   /* Text (allowed, tested above). */
                        uiHow = VWH_clsViewerHow.VWH_TEXT;
                    }
                }

                if ((uiHow == VWH_clsViewerHow.VWH_TEXT || uiHow >= VWH_clsViewerHow.VWH_EDITOR) &&
                    !bTextAllowed)
                {   /* Text or the structured editor chosen (or fallen to) --   */
                    /* by Open, Open As or a New File -- but too large for      */
                    /* either: the question tells the user so and offers the    */
                    /* hex editor instead.  The structured editor reads and     */
                    /* parses the whole file into its tree, so it is held to    */
                    /* the same limit as the text editor rather than left to    */
                    /* run out of memory on a huge file named like a config.    */
                    /* Asked only here, so no other view makes the record.      */
                    objChoice = new VCH_clsViewerChoice(bTextAllowed, objContent.OED_lBytes,
                        sName, objContent.OED_sWhy);
                    objAnswer = objExecution.EXC_Ask(QNK_enmQuestionKind.ViewerLargeText, 0, objChoice);
                    if (objAnswer == null || objAnswer.ANS_enmKind != ANK_enmAnswerKind.Yes)
                    {   /* Declined: nothing opens; the finally deletes the   */
                        /* read-out.                                          */
                        return(new RSL_clsResult(SYS_CANCELLED, "Viewer choice declined.", null));
                    }

                    /* Yes: the hex editor. */
                    uiHow = VWH_clsViewerHow.VWH_HEX;
                }

                if (!objExecution.EXC_Checkpoint())
                {   /* A Cancel or an agreed Exit came during the questions:   */
                    /* nothing opens.                                          */
                    return(new RSL_clsResult(SYS_CANCELLED, "Viewer opening cancelled.", null));
                }

                /* The editor registered, owning the read-out from here, under   */
                /* the open session's epoch and target, its id the request's     */
                /* serial (an id that stays the same through a Save As to        */
                /* another filesystem).  Then the state sent, so the editor      */
                /* count shows.                                                  */
                objEditor = new EDR_clsEditor(objRequest.RQT_lSerial, Sys_lEpoch,
                    Sys_objTarget, objRequest.RQT_lstPaths[0], -1, objContent);
                Sys_lstEditors.Add(objEditor);
                bRegistered = true;
                Sys_PublishState(objRequest);
                if (!SYS_Show(new VWM_clsView(VWK_enmViewKind.EditorOpen, objRequest,
                    new EVW_clsEditorView(objEditor, objResult, uiHow,
                        objCreated != null && objCreated.EDA_bNewDocument))))
                {   /* The UI did not take the view (it is gone): the editor   */
                    /* unregistered and its read-out released.                 */
                    Sys_RemoveEditor(objEditor, objRequest);
                    return(new RSL_clsResult(SYS_CANCELLED, "The viewer owner has closed.", null));
                }

                /* Opened: the read-out is now released only when the editor   */
                /* closes (EditorClosed, CommandSequences.cs).                 */
                return(objResult);
            }
            finally
            {   /* Every way out: a read-out no editor took is deleted here. */
                if (!bRegistered)
                {   /* Not registered: the questions opened nothing on the   */
                    /* read-out, so it can go.                               */
                    try
                    {   /* The read-out only -- never the image's file, never a   */
                        /* Windows file.                                          */
                        objContent.OED_Release();
                    }
                    catch (Exception exCleanup)
                    {   /* A deletion that fails is a warning: it must not    */
                        /* replace the sequence's own outcome or exception.   */
                        SYS_Show(new VWM_clsView(VWK_enmViewKind.Warning, objRequest,
                            new RSL_clsResult(SYS_INTERNAL,
                                "Viewer temporary cleanup failed: " + exCleanup.Message, null)));
                    }
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* Sys_FindImageEditor:                                               */
        /*                                                                    */
        /* The editor, if any, whose Save writes to the image file an Open    */
        /* names: one registered on the filesystem open now (its lifetime and */
        /* range) at that path.  What decides is where the editor saves NOW:  */
        /* an editor Saved As elsewhere no longer holds the file it was       */
        /* opened from, and one Saved As to this file does.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the Open; its one path is the file.               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     EDR_clsEditor : that editor's record; null when none holds it, */
        /*                     and for a request without exactly one path or  */
        /*                     with no filesystem open (the opening sequence  */
        /*                     and its gate deal with those).                 */
        /*--------------------------------------------------------------------*/
        private EDR_clsEditor Sys_FindImageEditor(RQT_clsRequest objRequest)
        {
            EDR_clsEditor objEditor;
            int i;

            if (objRequest.RQT_lstPaths.Count != 1 || Sys_objTarget == null)
            {   /* Not one file, or nothing open: no editor can hold it here. */
                return(null);
            }

            for (i = 0; i < Sys_lstEditors.Count; i++)
            {   /* One registered image editor per iteration: it holds the     */
                /* file when it saves into the filesystem open now (the same   */
                /* lifetime -- kept through a writable reopen -- and range)    */
                /* at the same path, as the filesystem matches names.          */
                objEditor = Sys_lstEditors[i];
                if (objEditor.EDR_objTarget != null && objEditor.EDR_lEpoch == Sys_lEpoch &&
                    objEditor.EDR_objTarget.TGT_SameRange(Sys_objTarget) &&
                    Sys_SameImagePath(objEditor.EDR_sEntry, objRequest.RQT_lstPaths[0]))
                {   /* It saves to that file: the one to bring forward. */
                    return(objEditor);
                }
            }

            /* No editor saves to that file. */
            return(null);
        }

        /*--------------------------------------------------------------------*/
        /* Sys_FindHostEditor:                                                */
        /*                                                                    */
        /* The editor, if any, whose Save writes to a Windows file: opened on */
        /* it, or Saved As to it from anywhere.  Windows names are matched    */
        /* letter case aside, as Windows matches them; two spellings of one   */
        /* file (a short 8.3 name, a path through a link) are not recognised  */
        /* as one -- the paths compared are the full ones the panes and the   */
        /* Save As dialogs give.                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sPath : the Windows file's full path.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     EDR_clsEditor : that editor's record, or null for none.        */
        /*--------------------------------------------------------------------*/
        private EDR_clsEditor Sys_FindHostEditor(string sPath)
        {
            foreach (EDR_clsEditor objEditor in Sys_dictHostEditors.Values)
            {   /* One editor saving to a Windows file per iteration. */
                if (String.Equals(objEditor.EDR_sEntry, sPath, StringComparison.OrdinalIgnoreCase))
                {   /* It saves to that file: the one to bring forward. */
                    return(objEditor);
                }
            }

            /* No editor saves to that file. */
            return(null);
        }

        /*--------------------------------------------------------------------*/
        /* Sys_SameImagePath:                                                 */
        /*                                                                    */
        /* Whether two absolute image paths name the same entry in the        */
        /* filesystem open now.  On ext2, ext3 and ext4 a name is its bytes,  */
        /* so only the same text is the same entry.  On FAT and exFAT a name  */
        /* matches letter case aside and without its trailing spaces and      */
        /* dots, as FatFs matches it and the engine's own tests do            */
        /* (BFX_PathCanonical) -- so "/Notes.TXT." and "/notes.txt" are one   */
        /* file there.                                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sFirst  : one path.                                            */
        /*     sSecond : the other.                                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when they name the same entry.                     */
        /*--------------------------------------------------------------------*/
        private bool Sys_SameImagePath(string sFirst, string sSecond)
        {
            if (Sys_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_FAT12 ||
                Sys_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_FAT16 ||
                Sys_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_FAT32 ||
                Sys_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_EXFAT)
            {   /* FAT or exFAT: each path's components without their      */
                /* trailing spaces and dots, then compared letter case     */
                /* aside.  The framework's ordinal folding stands in for   */
                /* FatFs's own up-case table; whether the two agree on     */
                /* every non-ASCII letter is not established.              */
                return(String.Equals(Sys_FatPathKey(sFirst), Sys_FatPathKey(sSecond),
                    StringComparison.OrdinalIgnoreCase));
            }

            /* ext2, ext3 or ext4: the exact text. */
            return(String.Equals(sFirst, sSecond, StringComparison.Ordinal));
        }

        /*--------------------------------------------------------------------*/
        /* Sys_FatPathKey:                                                    */
        /*                                                                    */
        /* An image path as FAT stores it: every component without its        */
        /* trailing spaces and dots, which FAT drops from a name.  The case   */
        /* is left as it is; the caller compares letter case aside.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sPath : an absolute image path.                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the path with each component's ending dropped.        */
        /*--------------------------------------------------------------------*/
        private static string Sys_FatPathKey(string sPath)
        {
            string[] arrParts;
            int i;

            /* Split at every '/' (the leading one gives an empty first part,   */
            /* which stays empty), each part's ending dropped, then joined      */
            /* back the same way.  No part is "." or "..": an image path        */
            /* holding one is refused before it reaches an editor.              */
            arrParts = sPath.Split('/');
            for (i = 0; i < arrParts.Length; i++)
            {   /* One component per iteration. */
                arrParts[i] = arrParts[i].TrimEnd(' ', '.');
            }

            /* The components joined again. */
            return(String.Join("/", arrParts));
        }

        /*--------------------------------------------------------------------*/
        /* Sys_ShowOpenEditor:                                                */
        /*                                                                    */
        /* An Open of a file an editor already saves to: the UI is asked to   */
        /* bring that editor's window forward (an EditorShow view naming its  */
        /* id), and nothing is read out, asked or registered.  The editor's   */
        /* window was made when its own EditorOpen view was shown, which the  */
        /* UI handled before this view, since views reach it in order.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the Open (the view is addressed to it).           */
        /*     objEditor  : the editor that saves to the file.                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : SYS_OK once the view is sent; SYS_CANCELLED    */
        /*                     when the UI is gone.                           */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_ShowOpenEditor(RQT_clsRequest objRequest, EDR_clsEditor objEditor)
        {
            if (!SYS_Show(new VWM_clsView(VWK_enmViewKind.EditorShow, objRequest, objEditor.EDR_lId)))
            {   /* The UI did not take the view (it is gone): nothing shown. */
                return(new RSL_clsResult(SYS_CANCELLED, "The viewer owner has closed.", null));
            }

            /* Sent: the open is answered by the window already there. */
            return(new RSL_clsResult(SYS_OK, "", null));
        }

        /*--------------------------------------------------------------------*/
        /* Sys_ViewerFallbackSequence:                                        */
        /*                                                                    */
        /* A structured editor would not load a file: ask whether to open it  */
        /* as plain text, and if so open it -- a Windows file directly, an    */
        /* image file read out afresh (the failed editor's read-out is not    */
        /* reused).                                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the file (RQT_lstPaths[0]), its epoch and       */
        /*                    target for an image file, and the refusal (a    */
        /*                    VFA_clsViewerFallbackArgs payload).             */
        /*     objExecution : the running command's services.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the text open's outcome; SYS_CANCELLED for No, */
        /*                     a Cancel or Exit, or a UI gone; SYS_REFUSED    */
        /*                     for a malformed request.                       */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_ViewerFallbackSequence(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            VFA_clsViewerFallbackArgs objArgs;
            RQT_clsRequest objText;

            /* The refusal and the one file.  (For an image file the gate has    */
            /* already held the request to the open image's epoch and range; a   */
            /* Windows file needs neither.)                                      */
            objArgs = objRequest.RQT_objPayload as VFA_clsViewerFallbackArgs;
            if (objArgs == null || objRequest.RQT_lstPaths.Count != 1)
            {   /* No refusal or not one file: refused -- the file is never   */
                /* taken from the pane.                                       */
                return(new RSL_clsResult(SYS_REFUSED, "The refused viewer has no captured source.", null));
            }

            if (!Sys_Yes(QNK_enmQuestionKind.ViewerFallback, objRequest, 0) ||
                !objExecution.EXC_Checkpoint())
            {   /* No, or a Cancel or Exit while asking: nothing opens.  The   */
                /* failed editor's read-out is its own (it may be gone         */
                /* already); this request holds none.                          */
                return(new RSL_clsResult(SYS_CANCELLED, "Text viewer declined.", null));
            }

            if (objArgs.VFA_bHost)
            {   /* A Windows file: opened in place, as text -- no read-out. */
                if (!SYS_Show(new VWM_clsView(VWK_enmViewKind.HostEditorOpen, objRequest,
                    new HVW_clsHostViewerView(objRequest.RQT_lstPaths[0]))))
                {   /* The UI did not take the view: nothing opened. */
                    return(new RSL_clsResult(SYS_CANCELLED, "The viewer owner has closed.", null));
                }

                /* Sent: the UI makes and registers the editor itself; no   */
                /* engine step ran.                                         */
                return(new RSL_clsResult(SYS_OK, "", null));
            }

            /* An image file: a fresh Open, as text, in the same image -- an   */
            /* OpenEntry request of this command's own (its serial, never      */
            /* posted), read out and registered by the opening sequence.       */
            objText = new RQT_clsRequest(KND_enmKind.OpenEntry, objRequest.RQT_lOrigin,
                objRequest.RQT_lEpoch, objRequest.RQT_objTarget, null, 0,
                VWH_clsViewerHow.VWH_TEXT, null, "", "", objRequest.RQT_sFolder);
            objText.RQT_lstPaths = objRequest.RQT_lstPaths;
            objText.RQT_lSerial = objRequest.RQT_lSerial;

            /* The opening sequence's outcome. */
            return(Sys_OpenEntrySequence(objText, objExecution));
        }

        /*---------------------------------------------------------------------*/
        /* Sys_PartitionOperation:                                             */
        /*                                                                     */
        /* The partition window's disk operations other than a partition's     */
        /* raw copies: Read Layout, Write Table, Make Filesystem, Expand       */
        /* Filesystem and the capacity test.  Each but Read Layout asks first; */
        /* the long ones run under a progress window; a change -- even one     */
        /* that failed or was cancelled, since bytes may already be written    */
        /* -- is followed by the layout read again.                            */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     objRequest   : the operation, its range (RQT_objTarget) and     */
        /*                    its arguments (a PAA_clsPartitionArgs payload,   */
        /*                    or for a Make Filesystem whose options are       */
        /*                    still to be asked the range's words, an          */
        /*                    FMI_clsFormatInput, replaced in place once       */
        /*                    they are).                                       */
        /*     objExecution : the running command's services.                  */
        /*                                                                     */
        /* Returns:                                                            */
        /*     RSL_clsResult : a Read Layout's or capacity test's own result;  */
        /*                     a failed table write's own result; otherwise    */
        /*                     an RPO_clsPartitionOutcome -- under the         */
        /*                     change's status when it failed or was           */
        /*                     cancelled, else the re-read's.                  */
        /*---------------------------------------------------------------------*/
        private RSL_clsResult Sys_PartitionOperation(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            RSL_clsResult objResult;
            RSL_clsResult objLayout;
            RSL_clsResult objCheck;
            RSL_clsResult objRepair;
            PAA_clsPartitionArgs objArgs;
            FMI_clsFormatInput objFormat;
            MFC_clsMakeFsChoice objChoice;
            ANS_clsAnswer objAnswer;
            PGV_clsProgressView objProgress;
            STP_enmStep enmStep;
            QNK_enmQuestionKind enmQuestion;
            bool bAsk;
            bool bStopped;
            string sPlace;
            uint uiSector;

            /* The defaults, set from the request alone: Read Layout, which is   */
            /* the one operation that asks nothing; every other asks below.      */
            enmStep = STP_enmStep.ReadLayout;
            enmQuestion = QNK_enmQuestionKind.Format;
            bAsk = true;
            objProgress = null;
            objCheck = null;
            objRepair = null;
            objArgs = objRequest.RQT_objPayload as PAA_clsPartitionArgs;
            sPlace = objRequest.RQT_objTarget.TGT_sPath;
            if (!String.IsNullOrEmpty(objRequest.RQT_objTarget.TGT_sDeviceText))
            {   /* A device: its description names it in the titles. */
                sPlace = objRequest.RQT_objTarget.TGT_sDeviceText;
            }

            if (objArgs != null && !String.IsNullOrEmpty(objArgs.PAA_sPlace))
            {   /* A format or expansion of one partition: the partition's   */
                /* own short name.                                           */
                sPlace = objArgs.PAA_sPlace;
            }

            /* The sector size the progress counts in -- display only; 512   */
            /* when the target has none.                                     */
            uiSector = objRequest.RQT_objTarget.TGT_uiSectorBytes;
            if (uiSector == 0)
            {   /* None captured: 512.  The engine uses the device's own. */
                uiSector = 512;
            }

            /* The operation: its step, its question and its progress window. */
            switch (objRequest.RQT_enmKind)
            {   /* The request's kind. */
                case KND_enmKind.ReadLayout:
                    /* A plain re-read: no question, no progress window. */
                    bAsk = false;
                    break;
                case KND_enmKind.WriteTable:
                    /* The table, in one batch, after its own question; quick,   */
                    /* so no progress window.                                    */
                    enmQuestion = QNK_enmQuestionKind.WriteTable;
                    enmStep = STP_enmStep.WriteTable;
                    break;
                case KND_enmKind.MakeFilesystem:
                    objFormat = objRequest.RQT_objPayload as FMI_clsFormatInput;
                    if (objArgs == null && objFormat != null)
                    {   /* The press carried the range's words, not options:    */
                        /* the options are asked HERE, as a question of this    */
                        /* sequence (MakeFsOptions, the dialog shown over the   */
                        /* partition window), so the format is one operation    */
                        /* from the press to its end and nothing is posted in   */
                        /* between for anything to come between.                */
                        objAnswer = objExecution.EXC_Ask(QNK_enmQuestionKind.MakeFsOptions, Sys_lModeWindow, objFormat);
                        objChoice = null;
                        if (objAnswer != null && objAnswer.ANS_enmKind == ANK_enmAnswerKind.Yes)
                        {   /* OK: the choice the dialog made (a guard checks   */
                            /* its type below).                                 */
                            objChoice = objAnswer.ANS_objValue as MFC_clsMakeFsChoice;
                        }

                        if (objChoice == null)
                        {   /* Cancelled (or interrupted, or an answer without   */
                            /* its choice): nothing written.                     */
                            return(new RSL_clsResult(SYS_CANCELLED, "Make Filesystem cancelled.", null));
                        }

                        /* The request becomes the format it describes: its       */
                        /* arguments built from the choice, and its range the     */
                        /* length chosen (a whole disk may be made smaller).      */
                        /* The request is this worker's while it runs, and the    */
                        /* steps, the Format question and the window's result     */
                        /* handler (MainCommand.cs) all read the arguments from   */
                        /* it -- so it is changed in place, before any of them.   */
                        objArgs = new PAA_clsPartitionArgs(null, objChoice.MFC_stOptions, false,
                            objFormat.FMI_sPlace, objFormat.FMI_sTarget);
                        objRequest.RQT_objPayload = objArgs;
                        objRequest.RQT_objTarget = new TGT_clsTarget(objRequest.RQT_objTarget.TGT_sPath,
                            objRequest.RQT_objTarget.TGT_ullOffset, objChoice.MFC_ullLength,
                            objRequest.RQT_objTarget.TGT_sDeviceText, objRequest.RQT_objTarget.TGT_ullDiskBytes,
                            objRequest.RQT_objTarget.TGT_uiSectorBytes);
                        if (!String.IsNullOrEmpty(objFormat.FMI_sPlace))
                        {   /* The partition's short name for the titles, as   */
                            /* the arguments would have given it above.        */
                            sPlace = objFormat.FMI_sPlace;
                        }
                    }

                    if (objArgs == null)
                    {   /* No options and nothing to ask them for: refused. */
                        return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                            "No captured filesystem format settings were supplied.", null));
                    }

                    /* The format, after the Format question.  Its window         */
                    /* depends on the zero-fill: with it, the copy window         */
                    /* counting the zeros (with Cancel); without, the "working"   */
                    /* box (no Cancel -- the format alone cannot be stopped).     */
                    enmStep = STP_enmStep.MakeFilesystem;
                    if ((objArgs.PAA_stOptions.MFO_uiFlags & ENG_clsNative.ENG_MAKEFS_ZERO) != 0)
                    {   /* Zero-filled first: the copy window, in sectors. */
                        objProgress = new PGV_clsProgressView(false, "Erasing and formatting " + sPlace,
                            "", uiSector, "Writing zeros");
                    }
                    else
                    {   /* Format only: the "working" box. */
                        objProgress = new PGV_clsProgressView(true, "Making the filesystem",
                            "Formatting " + sPlace + " ...");
                    }

                    /* Either way the Format question is asked below. */
                    break;
                case KND_enmKind.ExpandFilesystem:
                    /* Write the table, check, repair if needed, resize -- all   */
                    /* under one "working" box, so the long check is not left    */
                    /* without one.  Its question is Repair's, which also        */
                    /* covers the repair the check may call for.                 */
                    enmQuestion = QNK_enmQuestionKind.Repair;
                    enmStep = STP_enmStep.Resize;
                    objProgress = new PGV_clsProgressView(true, "Expanding the filesystem",
                        "Expanding " + sPlace + " ...");
                    break;
                case KND_enmKind.TestCapacity:
                    /* The test writes too (and puts back), so it asks first;   */
                    /* then the copy window counting its steps, with Cancel.    */
                    enmQuestion = QNK_enmQuestionKind.TestCapacity;
                    enmStep = STP_enmStep.TestCapacity;
                    objProgress = new PGV_clsProgressView(false, "Testing the capacity of " + sPlace,
                        "", uiSector, "Testing");
                    break;
                default:
                    /* Any other kind: refused, nothing done. */
                    return(new RSL_clsResult(SYS_REFUSED, "Unsupported partition operation.", null));
            }

            if (bAsk && !Sys_Yes(enmQuestion, objRequest, Sys_lModeWindow))
            {   /* Asked through the partition window and not answered Yes:   */
                /* nothing written.                                           */
                return(new RSL_clsResult(SYS_CANCELLED, "Disk operation declined.", null));
            }

            if (objProgress != null && !SYS_Show(new VWM_clsView(VWK_enmViewKind.ProgressOpen,
                objRequest, objProgress)))
            {   /* The progress window could not be shown (the UI is gone):   */
                /* nothing written.                                           */
                return(new RSL_clsResult(SYS_CANCELLED, "The disk operation's progress owner has closed.", null));
            }

            /* A failed step ends the sequence by setting bStopped rather than   */
            /* returning, so a step that failed AFTER bytes were written (a      */
            /* cancelled zero-fill, Expand's table write followed by a failed    */
            /* check) still reaches the layout re-read below and the window      */
            /* stops showing what the disk no longer holds.                      */
            objResult = null;
            bStopped = false;
            try
            {   /* The steps, inside the progress window's life. */
                if (objRequest.RQT_enmKind == KND_enmKind.ExpandFilesystem)
                {   /* EXPAND, STEP 1: the table, if the arguments grow the   */
                    /* partition (else the WriteTable step succeeds with      */
                    /* nothing written).                                      */
                    objResult = Sys_Do(STP_enmStep.WriteTable, objRequest, objExecution);
                    if (objResult.RSL_iStatus != SYS_OK)
                    {   /* The table write failed: no check, no resize. */
                        bStopped = true;
                    }

                    if (!bStopped)
                    {   /* STEP 2: a report-only check, which tells "found        */
                        /* errors" (a success with RPN_bNeeded) from "could not   */
                        /* check" (a failure) -- only the first leads to a        */
                        /* repair.                                                */
                        objCheck = Sys_Do(STP_enmStep.Check, objRequest, objExecution);
                        if (objCheck.RSL_iStatus != SYS_OK)
                        {   /* Could not check, or cancelled: no repair, no   */
                            /* resize.                                        */
                            objResult = objCheck;
                            bStopped = true;
                        }
                    }

                    if (!bStopped && objCheck.RSL_objPayload is RPN_clsRepairNeeded &&
                        ((RPN_clsRepairNeeded)objCheck.RSL_objPayload).RPN_bNeeded)
                    {   /* STEP 3: the check found errors: the repair, which the   */
                        /* Repair question already covered.                        */
                        objRepair = Sys_Do(STP_enmStep.Repair, objRequest, objExecution);
                        if (objRepair.RSL_iStatus != SYS_OK)
                        {   /* The repair failed or left errors: no resize. */
                            objResult = objRepair;
                            bStopped = true;
                        }
                    }
                }

                if (!bStopped)
                {   /* The operation's own step (for Expand, STEP 4: the   */
                    /* resize), on the same range in the same run.         */
                    objResult = Sys_Do(enmStep, objRequest, objExecution);
                }
            }
            finally
            {   /* The progress window closed however the steps ended, an   */
                /* exception included.                                      */
                if (objProgress != null)
                {   /* Shown above: closed (Read Layout and a table write   */
                    /* opened none).                                        */
                    SYS_Show(new VWM_clsView(VWK_enmViewKind.ProgressClose, objRequest, null));
                }
            }

            if (objRequest.RQT_enmKind == KND_enmKind.TestCapacity ||
                objRequest.RQT_enmKind == KND_enmKind.ReadLayout)
            {   /* Read/capacity already have their complete result (the   */
                /* capacity test puts back what it wrote), so no re-read   */
                /* follows.                                                */
                return(objResult);
            }

            if (objResult.RSL_iStatus != SYS_OK &&
                objRequest.RQT_enmKind != KND_enmKind.MakeFilesystem &&
                !(objRequest.RQT_enmKind == KND_enmKind.ExpandFilesystem && objCheck != null))
            {   /* A failed table write (Write Table, or Expand's own before   */
                /* its check ran) is NOT re-read: the window keeps the         */
                /* user's unwritten edits so the write can be tried again.     */
                /* A failed or cancelled format, or an Expand                  */
                /* that failed after its table was written, goes on to the     */
                /* re-read below.                                              */
                return(objResult);
            }

            /* Read the whole owning disk once after a mutation --        */
            /* successful or not, since a failed or cancelled one may     */
            /* already have written.  Keep both outcomes: this read can   */
            /* fail after bytes were committed.                           */
            try
            {   /* The re-read, after the change has finished. */
                if (objResult.RSL_iStatus == SYS_OK)
                {   /* After a success the read goes through the run's checkpoint as usual. */
                    objLayout = Sys_Do(STP_enmStep.ReadLayout, Sys_RequestForTarget(objRequest, Sys_objTarget), objExecution);
                }
                else
                {   /* After a CANCEL the run's checkpoint refuses every    */
                    /* further step (it exists to stop another WRITE        */
                    /* starting), so through Sys_Do this read would come    */
                    /* back cancelled too and leave the window stale.  It   */
                    /* only reads the table and the contents, so it calls   */
                    /* the step adapter directly, past the checkpoint.      */
                    objLayout = Sys_objSteps.STB_Run(STP_enmStep.ReadLayout, Sys_RequestForTarget(objRequest, Sys_objTarget), objExecution);
                }
            }
            catch (Exception exLayout)
            {   /* An exception while re-reading: the re-read's failure, and   */
                /* the change's own result still kept below.  The whole        */
                /* exception to the debug log, its message as the detail.      */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("partition layout re-read", exLayout.ToString());
                }

                /* The re-read's failed result, its detail the exception's message. */
                objLayout = new RSL_clsResult(SYS_INTERNAL, exLayout.Message, null);
            }

            if (objResult.RSL_iStatus != SYS_OK)
            {   /* A failed or cancelled mutation stays the outcome, with     */
                /* its own status and detail; the fresh layout rides with     */
                /* it so the window repaints from what the disk now holds     */
                /* (the partition window's layout painter, MainCommand.cs).   */
                return(new RSL_clsResult(objResult.RSL_iStatus, objResult.RSL_sDetail,
                    new RPO_clsPartitionOutcome(objResult, objLayout, objCheck, objRepair)));
            }

            /* A success: the re-read's status outside (a re-read that failed   */
            /* is what the window must hear), the change's report inside.       */
            return(new RSL_clsResult(objLayout.RSL_iStatus, objLayout.RSL_sDetail,
                new RPO_clsPartitionOutcome(objResult, objLayout, objCheck, objRepair)));
        }

        /*---------------------------------------------------------------------*/
        /* Sys_TableEditSequence:                                              */
        /*                                                                     */
        /* A partition-window button's press, run whole on the worker: the     */
        /* edit to the window's table is made on the COPY the press carried,   */
        /* and the copy handed back as the result's payload for the window to  */
        /* show (MainCommand.cs, Pui_OnResult) -- the window's own table is    */
        /* never touched from here.  The window's buttons are greyed from the  */
        /* press until that result is shown, so the table the copy was made    */
        /* from is the one the window still shows when the edit comes back.    */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     objRequest   : the press: its TBE_clsTableEdit payload.         */
        /*     objExecution : the running command's services.                  */
        /*                                                                     */
        /* Returns:                                                            */
        /*     RSL_clsResult : SYS_OK with the edited record as the payload;   */
        /*                     SYS_REFUSED, with the reason, for an edit that  */
        /*                     cannot be made.                                 */
        /*---------------------------------------------------------------------*/
        private RSL_clsResult Sys_TableEditSequence(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            TBE_clsTableEdit objEdit;
            PTB_Entry objEntry;
            ANS_clsAnswer objAnswer;
            ADC_clsAddChoice objChoice;
            List<PTB_Range> lstGaps;
            List<PTB_Piece> lstPieces;
            PTB_Range objStart;
            string sProblem;
            string sTarget;
            string sPlace;
            string sContent;
            ulong ullDisk;
            ulong ullOffset;
            ulong ullLength;
            ulong ullNewLast;
            int iKind;
            int i;

            /* The press's record (the payload check admitted only this type). */
            objEdit = (TBE_clsTableEdit)objRequest.RQT_objPayload;

            /* The entry in the slot the press selected, in the copy, or null   */
            /* (nothing selected, or a table-less disk's whole-disk row).       */
            objEntry = null;
            for (i = 0; i < objEdit.TBE_objTable.lstEntries.Count; i++)
            {   /* One entry of the copy per iteration. */
                if (objEdit.TBE_objTable.lstEntries[i].iSlot == objEdit.TBE_iSlot)
                {   /* The selected slot's entry. */
                    objEntry = objEdit.TBE_objTable.lstEntries[i];
                }
            }

            switch (objEdit.TBE_enmAction)
            {   /* One case per button (New Image's first question with New Table). */
                case PAC_enmPartitionAction.Delete:
                    /* Delete Partition: the selected entry taken out of the   */
                    /* copy -- its slot becomes free.  Nothing is written,     */
                    /* and what the partition held is not touched by a later   */
                    /* write either: only the table's entry goes.              */
                    if (objEntry == null)
                    {   /* Nothing selected (the button is greyed then): no   */
                        /* edit.                                              */
                        return(new RSL_clsResult(SYS_REFUSED, "No partition is selected.", null));
                    }

                    objEdit.TBE_objTable.lstEntries.Remove(objEntry);
                    break;

                case PAC_enmPartitionAction.MakeFs:
                case PAC_enmPartitionAction.ReadPart:
                case PAC_enmPartitionAction.WritePart:
                case PAC_enmPartitionAction.ErasePart:
                    /* Make Filesystem, Save Partition to File, Write File    */
                    /* to Partition, Erase Partition: each works on the       */
                    /* bytes where the table ON THE DISK puts the range, so   */
                    /* a table with unwritten edits is refused (the buttons   */
                    /* are greyed then; a guard, with the reason).            */
                    if (objEdit.TBE_bModified)
                    {   /* The table on screen is not the one on the disk. */
                        return(new RSL_clsResult(SYS_REFUSED, "Write the partition table first: the command works where the table on the disk says the partition is.", null));
                    }

                    /* The range and its two names: the terse one for the     */
                    /* titles and the progress, the other with its size and   */
                    /* what it holds now (its double space -- the list's      */
                    /* column gap -- made single), for the warnings.          */
                    ullDisk = objRequest.RQT_objTarget.TGT_ullDiskBytes;
                    if (objEntry != null)
                    {   /* A selected partition: its whole range, from its   */
                        /* LBAs (the last inclusive).                        */
                        ullOffset = objEntry.ullFirstLba * objEdit.TBE_objTable.uiSector;
                        ullLength = (objEntry.ullLastLba - objEntry.ullFirstLba + 1) * objEdit.TBE_objTable.uiSector;
                        sPlace = objEdit.TBE_sName + ": p" + (objEntry.iSlot + 1) + " @ " + ullOffset.ToString("N0");
                        sContent = objEntry.sContent;
                    }
                    else if (objEdit.TBE_objTable.iKind == PTB_Tables.PTB_KIND_NONE)
                    {   /* No table: the whole disk -- its full length, not   */
                        /* an old filesystem's, which may be smaller.         */
                        ullOffset = 0;
                        ullLength = ullDisk;
                        sPlace = objEdit.TBE_sName;
                        sContent = objEdit.TBE_sWholeContent;
                    }
                    else
                    {   /* Nothing selected (the buttons are greyed then): no   */
                        /* range is guessed.                                    */
                        return(new RSL_clsResult(SYS_REFUSED, "No partition is selected.", null));
                    }

                    sTarget = sPlace + " (" + DVF_clsDeviceForm.DVF_SizeShortText(ullLength) + ")";
                    if (sContent.Length != 0)
                    {   /* Something recognised there: named. */
                        sTarget = sTarget + ", now holding " + sContent.Replace("  ", " ");
                    }

                    /* The request BECOMES the operation the button names --   */
                    /* its kind, its range and the words the operation's own   */
                    /* sequence takes -- and that sequence runs in this same   */
                    /* run: it asks its own question (the options, the file,   */
                    /* the warning), and the window's result handler reports   */
                    /* it as that operation.                                   */
                    objRequest.RQT_objTarget = new TGT_clsTarget(objRequest.RQT_objTarget.TGT_sPath, ullOffset, ullLength,
                        objRequest.RQT_objTarget.TGT_sDeviceText, ullDisk, objRequest.RQT_objTarget.TGT_uiSectorBytes);
                    if (objEdit.TBE_enmAction == PAC_enmPartitionAction.MakeFs)
                    {   /* The format: its options asked by its sequence.  The   */
                        /* whole-disk flag lets them make the filesystem         */
                        /* SMALLER than the range -- only for the entire disk,   */
                        /* from its start to its end.                            */
                        objRequest.RQT_enmKind = KND_enmKind.MakeFilesystem;
                        objRequest.RQT_objPayload = new FMI_clsFormatInput(ullOffset == 0 && ullLength == ullDisk,
                            sPlace, sTarget);
                        return(Sys_PartitionOperation(objRequest, objExecution));
                    }

                    if (objEdit.TBE_enmAction == PAC_enmPartitionAction.ErasePart)
                    {   /* The erase: no file; its sequence confirms first. */
                        objRequest.RQT_enmKind = KND_enmKind.ErasePartition;
                        objRequest.RQT_objPayload = new PRA_clsPartitionRawArgs("", sPlace, sTarget);
                        return(Sys_PartitionRawSequence(objRequest, objExecution));
                    }

                    /* A Read or a Write: its file asked by its sequence, in   */
                    /* the image folder the press carried (RQT_sFolder).       */
                    objRequest.RQT_enmKind = KND_enmKind.ReadPartition;
                    if (objEdit.TBE_enmAction == PAC_enmPartitionAction.WritePart)
                    {   /* Write File to Partition: an existing file, then its   */
                        /* confirmation.                                         */
                        objRequest.RQT_enmKind = KND_enmKind.WritePartition;
                    }

                    objRequest.RQT_objPayload = new FMI_clsFormatInput(false, sPlace, sTarget);
                    return(Sys_PartitionRawSequence(objRequest, objExecution));

                case PAC_enmPartitionAction.Expand:
                    /* Expand Filesystem -- grow a filesystem to fill its       */
                    /* card, in two steps the user can see: the partition       */
                    /* first (on the copy, then written by Write Table as       */
                    /* any edit is), then the filesystem inside it on a         */
                    /* second press.  The table on the disk must be this one.   */
                    if (objEdit.TBE_bModified)
                    {   /* Unwritten edits (the button is greyed then). */
                        return(new RSL_clsResult(SYS_REFUSED, "Write the partition table first: the filesystem is grown to fill the partition the table ON THE DISK describes.", null));
                    }

                    /* The place and what it holds: the selected partition,   */
                    /* or the whole disk of a table-less one.                 */
                    ullDisk = objRequest.RQT_objTarget.TGT_ullDiskBytes;
                    if (objEntry != null)
                    {   /* The selected partition. */
                        ullOffset = objEntry.ullFirstLba * objEdit.TBE_objTable.uiSector;
                        ullLength = (objEntry.ullLastLba - objEntry.ullFirstLba + 1) * objEdit.TBE_objTable.uiSector;
                        sPlace = objEdit.TBE_sName + ": p" + (objEntry.iSlot + 1) + " @ " + ullOffset.ToString("N0");
                        sContent = objEntry.sContent;
                    }
                    else if (objEdit.TBE_objTable.iKind == PTB_Tables.PTB_KIND_NONE)
                    {   /* The whole disk. */
                        ullOffset = 0;
                        ullLength = ullDisk;
                        sPlace = objEdit.TBE_sName;
                        sContent = objEdit.TBE_sWholeContent;
                    }
                    else
                    {   /* Nothing selected. */
                        return(new RSL_clsResult(SYS_REFUSED, "No partition is selected.", null));
                    }

                    if (!sContent.StartsWith("ext", StringComparison.OrdinalIgnoreCase))
                    {   /* Only ext2/3/4 is grown: the probe's words must   */
                        /* start with "ext" (the engine's resize knows      */
                        /* nothing else).                                   */
                        return(new RSL_clsResult(SYS_REFUSED, "Only an ext2, ext3 or ext4 filesystem can be expanded, and " +
                            sPlace + " does not hold one.", null));
                    }

                    if (objEntry != null)
                    {   /* STEP 1: free space directly after the partition?     */
                        /* Then its end moves to the end of that space IN THE   */
                        /* COPY, which the window shows modified, and the       */
                        /* user Writes Table and presses Expand again.  Free    */
                        /* space BEFORE it is never used: that would move the   */
                        /* filesystem's start, which a resize cannot do.  The   */
                        /* gaps are exact sectors (no alignment), so the        */
                        /* test is exact; 0 cannot be a gap's last sector, so   */
                        /* it means "none".                                     */
                        lstGaps = new List<PTB_Range>();
                        PTB_Tables.PTB_FreeGaps(objEdit.TBE_objTable, lstGaps);
                        ullNewLast = 0;
                        for (i = 0; i < lstGaps.Count; i++)
                        {   /* One free gap per iteration: only the one right   */
                            /* after this partition can extend its end.         */
                            if (lstGaps[i].ullFirstLba == objEntry.ullLastLba + 1)
                            {   /* Found. */
                                ullNewLast = lstGaps[i].ullLastLba;
                            }
                        }

                        if (ullNewLast != 0)
                        {   /* Grow the partition first, asked (Yes the      */
                            /* default); the filesystem on the next press.   */
                            objAnswer = objExecution.EXC_Ask(QNK_enmQuestionKind.GrowPartition, Sys_lModeWindow,
                                "Partition " + (objEntry.iSlot + 1) + " (" + DVF_clsDeviceForm.DVF_SizeShortText(ullLength) + ") has " +
                                DVF_clsDeviceForm.DVF_SizeShortText((ullNewLast - objEntry.ullLastLba) * objEdit.TBE_objTable.uiSector) +
                                " of free space after it.\n\nGrow the partition to take it?  Nothing is written yet: " +
                                "press Write Table to save the larger partition, then Expand Filesystem again to grow the filesystem into it.");
                            if (objAnswer == null || objAnswer.ANS_enmKind != ANK_enmAnswerKind.Yes)
                            {   /* Declined (or interrupted): nothing changed. */
                                return(new RSL_clsResult(SYS_CANCELLED, "Expand cancelled.", null));
                            }

                            /* The end moved in the copy only, the partition   */
                            /* kept selected; the window shows it modified.    */
                            objEntry.ullLastLba = ullNewLast;
                            objEdit.TBE_iSelect = objEntry.iSlot;
                            break;
                        }
                    }

                    /* STEP 2: no free space after it (or the whole disk):    */
                    /* the partition on the disk has its final size, so the   */
                    /* request BECOMES the filesystem's growth -- its         */
                    /* question, the check (and a repair if needed), the      */
                    /* resize and the re-read -- run in this same run, with   */
                    /* "do not grow the table" and no pieces.                 */
                    objRequest.RQT_enmKind = KND_enmKind.ExpandFilesystem;
                    objRequest.RQT_objPayload = new PAA_clsPartitionArgs(null, new MFO_stMakeFsOptions(), false, sPlace, "");
                    objRequest.RQT_objTarget = new TGT_clsTarget(objRequest.RQT_objTarget.TGT_sPath, ullOffset, ullLength,
                        objRequest.RQT_objTarget.TGT_sDeviceText, ullDisk, objRequest.RQT_objTarget.TGT_uiSectorBytes);
                    return(Sys_PartitionOperation(objRequest, objExecution));

                case PAC_enmPartitionAction.Refresh:
                    /* Refresh: read the disk again now.  With unwritten     */
                    /* edits the read throws them away, as Revert does, so   */
                    /* it is asked first through the partition window (No    */
                    /* the default); then the request BECOMES the layout     */
                    /* read, run in this same run, whose result the window   */
                    /* paints as any read's.                                 */
                    if (objEdit.TBE_bModified && !Sys_Yes(QNK_enmQuestionKind.DiscardEdits, objRequest, Sys_lModeWindow))
                    {   /* Kept: nothing is read. */
                        return(new RSL_clsResult(SYS_CANCELLED, "Refresh cancelled: the changes are kept.", null));
                    }

                    objRequest.RQT_enmKind = KND_enmKind.ReadLayout;
                    objRequest.RQT_objPayload = null;
                    return(Sys_PartitionOperation(objRequest, objExecution));

                case PAC_enmPartitionAction.WriteTable:
                    /* Write Table: the copy checked and built into the        */
                    /* pieces to write (PTB_Build) -- bytes in memory only.    */
                    /* A table that cannot be built is refused with the        */
                    /* reason; otherwise the request BECOMES the table write   */
                    /* (its confirmation, the old GPT's erase, the one batch   */
                    /* and the re-read), run in this same run.  The            */
                    /* arguments record copies each piece's bytes; the         */
                    /* options are unused by a table write.                    */
                    lstPieces = new List<PTB_Piece>();
                    if (!PTB_Tables.PTB_Build(objEdit.TBE_objTable, lstPieces, out sProblem))
                    {   /* Not a table that can be written: said, nothing   */
                        /* written.                                         */
                        return(new RSL_clsResult(SYS_REFUSED, "The table cannot be written as it is: " + sProblem, null));
                    }

                    objRequest.RQT_enmKind = KND_enmKind.WriteTable;
                    objRequest.RQT_objPayload = new PAA_clsPartitionArgs(lstPieces, new MFO_stMakeFsOptions(),
                        false, objEdit.TBE_sName, "");
                    return(Sys_PartitionOperation(objRequest, objExecution));

                case PAC_enmPartitionAction.Add:
                    /* Add Partition: the free gaps of the copy, the gap to   */
                    /* start in -- the one last clicked, else the largest     */
                    /* (a tie keeps the earlier) -- then the Add dialog       */
                    /* asked through the partition window.                    */
                    lstGaps = new List<PTB_Range>();
                    PTB_Tables.PTB_FreeGaps(objEdit.TBE_objTable, lstGaps);
                    if (lstGaps.Count == 0)
                    {   /* No room (the button is greyed then; a guard). */
                        return(new RSL_clsResult(SYS_REFUSED, "There is no free space to add a partition in.", null));
                    }

                    objStart = objEdit.TBE_objClicked;
                    if (objStart == null)
                    {   /* Nothing clicked: the largest gap. */
                        objStart = lstGaps[0];
                        for (i = 1; i < lstGaps.Count; i++)
                        {   /* One gap per iteration. */
                            if (lstGaps[i].ullLastLba - lstGaps[i].ullFirstLba > objStart.ullLastLba - objStart.ullFirstLba)
                            {   /* Larger. */
                                objStart = lstGaps[i];
                            }
                        }
                    }

                    objAnswer = objExecution.EXC_Ask(QNK_enmQuestionKind.AddPartition, Sys_lModeWindow,
                        new ADQ_clsAddQuestion(objEdit.TBE_objTable, lstGaps, objStart));
                    objChoice = null;
                    if (objAnswer != null && objAnswer.ANS_enmKind == ANK_enmAnswerKind.Yes)
                    {   /* OK: the partition the dialog accepted. */
                        objChoice = objAnswer.ANS_objValue as ADC_clsAddChoice;
                    }

                    if (objChoice == null)
                    {   /* Cancelled (or interrupted): no partition added. */
                        return(new RSL_clsResult(SYS_CANCELLED, "Add Partition cancelled.", null));
                    }

                    /* Validated by the dialog against this very copy: the    */
                    /* entry goes into the lowest free slot (PTB_AddEntry),   */
                    /* with the active flag the dialog gave, and the window   */
                    /* selects it once it shows the copy.                     */
                    objEntry = PTB_Tables.PTB_AddEntry(objEdit.TBE_objTable, objChoice.ADC_ullFirstLba,
                        objChoice.ADC_ullLastLba, objChoice.ADC_objType, objChoice.ADC_sName);
                    objEntry.bBoot = objChoice.ADC_bBoot;
                    objEdit.TBE_iSelect = objEntry.iSlot;
                    break;

                case PAC_enmPartitionAction.NewTable:
                case PAC_enmPartitionAction.AskKind:
                    /* New Table, or New Image's first question over its   */
                    /* fresh empty file: the kind asked, through the       */
                    /* partition window, the present kind offered first.   */
                    objAnswer = objExecution.EXC_Ask(QNK_enmQuestionKind.NewTableKind, Sys_lModeWindow,
                        objEdit.TBE_objTable.iKind);
                    if (objAnswer == null || objAnswer.ANS_enmKind != ANK_enmAnswerKind.Yes ||
                        !(objAnswer.ANS_objValue is int))
                    {   /* Cancelled (or interrupted): the table, and a New   */
                        /* Image's empty file, stay as they are.              */
                        return(new RSL_clsResult(SYS_CANCELLED, "New Table cancelled.", null));
                    }

                    iKind = (int)objAnswer.ANS_objValue;
                    if (objEdit.TBE_enmAction == PAC_enmPartitionAction.AskKind &&
                        iKind == PTB_Tables.PTB_KIND_NONE && objEdit.TBE_objTable.iKind == PTB_Tables.PTB_KIND_NONE)
                    {   /* New Image's "one filesystem over the whole file" on   */
                        /* a file with no table: the simple case goes straight   */
                        /* on to Make Filesystem over the whole disk, in this    */
                        /* same run.  The request BECOMES that format -- its     */
                        /* kind, its range (the whole disk) and its words, as    */
                        /* the window's Make Filesystem would post them -- so    */
                        /* the format's sequence asks its options and its        */
                        /* warning, and the window's result handler reports      */
                        /* it, as for any format.  The whole-disk words carry    */
                        /* what the disk's start holds now, its double space     */
                        /* (the list's column gap) made single for a sentence.   */
                        ullDisk = objRequest.RQT_objTarget.TGT_ullDiskBytes;
                        sTarget = objEdit.TBE_sName + " (" + DVF_clsDeviceForm.DVF_SizeShortText(ullDisk) + ")";
                        if (objEdit.TBE_sWholeContent.Length != 0)
                        {   /* Something recognised there: named. */
                            sTarget = sTarget + ", now holding " + objEdit.TBE_sWholeContent.Replace("  ", " ");
                        }

                        objRequest.RQT_enmKind = KND_enmKind.MakeFilesystem;
                        objRequest.RQT_objPayload = new FMI_clsFormatInput(true, objEdit.TBE_sName, sTarget);
                        objRequest.RQT_objTarget = new TGT_clsTarget(objRequest.RQT_objTarget.TGT_sPath, 0, ullDisk,
                            objRequest.RQT_objTarget.TGT_sDeviceText, ullDisk, objRequest.RQT_objTarget.TGT_uiSectorBytes);
                        return(Sys_PartitionOperation(objRequest, objExecution));
                    }

                    /* The copy replaced by a fresh empty table of that kind   */
                    /* (even the same kind: a fresh one), laid out for the     */
                    /* window's disk.  Written over a disk that holds a GPT,   */
                    /* a later table write also erases that GPT's headers.     */
                    if (iKind == PTB_Tables.PTB_KIND_GPT)
                    {   /* A fresh GPT. */
                        objEdit.TBE_objTable = PTB_Tables.PTB_NewGpt(objRequest.RQT_objTarget.TGT_ullDiskBytes,
                            objRequest.RQT_objTarget.TGT_uiSectorBytes);
                    }
                    else if (iKind == PTB_Tables.PTB_KIND_MBR)
                    {   /* A fresh MBR. */
                        objEdit.TBE_objTable = PTB_Tables.PTB_NewMbr(objRequest.RQT_objTarget.TGT_ullDiskBytes,
                            objRequest.RQT_objTarget.TGT_uiSectorBytes);
                    }
                    else
                    {   /* No table at all. */
                        objEdit.TBE_objTable = PTB_Tables.PTB_NewNone(objRequest.RQT_objTarget.TGT_ullDiskBytes,
                            objRequest.RQT_objTarget.TGT_uiSectorBytes);
                    }
                    break;

                default:
                    /* A guard: the record's constructor refuses an action   */
                    /* number the enum does not define, and every one it     */
                    /* defines has its case above.  Refused, nothing done.   */
                    return(new RSL_clsResult(SYS_REFUSED, "Unsupported partition table edit.", null));
            }

            /* The edited copy, for the window to show as its table, marked   */
            /* modified (nothing is written until Write Table).               */
            return(new RSL_clsResult(SYS_OK, "", objEdit));
        }

        /*---------------------------------------------------------------------*/
        /* Sys_OperationSequence:                                              */
        /*                                                                     */
        /* The ordinary operations: the kinds with a sequence of their own     */
        /* are sent to it; the rest -- an edit, a picker's listing, the drag's */
        /* read-out, a link, the filesystem's facts, the disks, a device's     */
        /* table, an image file's facts, the verify option -- run their one    */
        /* step here.  An edit first resolves any name it asks for and its     */
        /* Delete question, then gets write access, then runs, then re-lists   */
        /* the pane, and a New File then opens in its editor.                  */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     objRequest   : the request, its gate already passed.            */
        /*     objExecution : the running command's services.                  */
        /*                                                                     */
        /* Returns:                                                            */
        /*     RSL_clsResult : the operation's outcome (its counts, hash or    */
        /*                     warnings kept); SYS_REFUSED for a kind it does  */
        /*                     not know.                                       */
        /*---------------------------------------------------------------------*/
        private RSL_clsResult Sys_OperationSequence(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            RSL_clsResult objResult;
            RSL_clsResult objOpened;
            PGV_clsProgressView objProgress;
            STP_enmStep enmStep;
            EDA_clsEditArgs objEdit;
            EDR_clsEditor objOpenEditor;
            RQT_clsRequest objOpen;
            RQT_clsRequest objNamed;
            ANS_clsAnswer objAnswer;
            EDK_enmEditKind enmResolved;
            string sSubject;
            string sParent;
            string sOldName;
            string sName;
            string sProblem;
            string sDestination;
            string[] arrDestinations;
            string[] arrSubjects;
            int iSlash;
            bool bChange;
            bool bProgress;
            bool bFat;

            if (objRequest.RQT_enmKind == KND_enmKind.OpenEntry)
            {   /* Open in an editor (Open or Open As, from the pane). */

                /* An editor that already saves to that file -- opened on it,    */
                /* or Saved As to it -- is brought forward instead, whatever     */
                /* viewer this Open asked for: two editors on one file would     */
                /* each save over the other's changes.  Looked up here, before   */
                /* the read-out, so no question about the file's content is      */
                /* asked for an editor that will not be made.  Only a posted     */
                /* Open passes this way: a New File's open and the plain-text    */
                /* fallback call the opening sequence directly, for a file no    */
                /* other editor holds.                                           */
                objOpenEditor = Sys_FindImageEditor(objRequest);
                if (objOpenEditor != null)
                {   /* Already open: its window shown, nothing read out. */
                    return(Sys_ShowOpenEditor(objRequest, objOpenEditor));
                }

                /* Not open: the opening sequence. */
                return(Sys_OpenEntrySequence(objRequest, objExecution));
            }

            if (objRequest.RQT_enmKind == KND_enmKind.Extract || objRequest.RQT_enmKind == KND_enmKind.Import ||
                objRequest.RQT_enmKind == KND_enmKind.CopyWithin)
            {   /* A copy or cut, with a cut's removal of its sources, as one   */
                /* sequence (CommandCopySequence.cs).                           */
                return(Sys_CopySequence(objRequest, objExecution));
            }

            if (objRequest.RQT_enmKind == KND_enmKind.ListFolder)
            {   /* The main pane's move to a folder, which takes effect only   */
                /* once the folder has been read (CommandNavigation.cs).       */
                return(Sys_NavigateFolderSequence(objRequest, objExecution));
            }

            if (objRequest.RQT_enmKind == KND_enmKind.ReadDevice || objRequest.RQT_enmKind == KND_enmKind.WriteDevice)
            {   /* Read or Write Device: inspection, question and copy as one   */
                /* sequence (CommandRawSequence.cs).                            */
                return(Sys_RawSequence(objRequest, objExecution));
            }

            if (objRequest.RQT_enmKind == KND_enmKind.PickerBranch)
            {   /* A picker's start-up listings of the folders above its start   */
                /* (CommandPickerBranch.cs).                                     */
                return(Sys_PickerBranchSequence(objRequest, objExecution));
            }

            if (objRequest.RQT_enmKind == KND_enmKind.EditorMakeFolder)
            {   /* A Save As dialog's New Folder: name, create, list, for that   */
                /* dialog (CommandPickerFolder.cs).                              */
                return(Sys_PickerFolderSequence(objRequest, objExecution));
            }

            if (objRequest.RQT_enmKind == KND_enmKind.ReadLayout || objRequest.RQT_enmKind == KND_enmKind.WriteTable ||
                objRequest.RQT_enmKind == KND_enmKind.MakeFilesystem || objRequest.RQT_enmKind == KND_enmKind.ExpandFilesystem ||
                objRequest.RQT_enmKind == KND_enmKind.TestCapacity)
            {   /* The partition window's disk operations, with their   */
                /* questions and re-read.                               */
                return(Sys_PartitionOperation(objRequest, objExecution));
            }

            if (objRequest.RQT_enmKind == KND_enmKind.EditTable)
            {   /* A partition-window button's press: its question and its     */
                /* edit on the table's copy, or the disk operation it names.   */
                return(Sys_TableEditSequence(objRequest, objExecution));
            }

            if (objRequest.RQT_enmKind == KND_enmKind.ReadPartition ||
                objRequest.RQT_enmKind == KND_enmKind.WritePartition ||
                objRequest.RQT_enmKind == KND_enmKind.ErasePartition)
            {   /* One partition's raw read, write or erase: its question, its   */
                /* copy and (after a change) the layout re-read, in one run.     */
                return(Sys_PartitionRawSequence(objRequest, objExecution));
            }

            /* An edit that asks for a name (New Folder, Rename): the name    */
            /* asked FIRST, before write access is taken, and against the     */
            /* request as captured -- so the question's text and a restored   */
            /* draft never follow a selection or folder change made since.    */
            bChange = objRequest.RQT_enmKind == KND_enmKind.Edit;
            objEdit = objRequest.RQT_objPayload as EDA_clsEditArgs;
            if (bChange && objEdit != null &&
                (objEdit.EDA_enmKind == EDK_enmEditKind.NameDirectory ||
                objEdit.EDA_enmKind == EDK_enmEditKind.NameEntry))
            {   /* New Folder or Rename: the question, the name's checks and   */
                /* the change, all in this one sequence.                       */
                if (objRequest.RQT_lstPaths.Count != 1)
                {   /* Not one item: refused -- a new folder's parent (or the   */
                    /* item renamed) is never guessed from a selection.         */
                    return(new RSL_clsResult(SYS_REFUSED, "Naming needs one captured parent or entry.", null));
                }

                /* The one item, as an image path: absolute, no NUL, no           */
                /* trailing "/" (the root aside), and for a Rename not the root   */
                /* (the root is a folder's parent but cannot be renamed).         */
                sSubject = objRequest.RQT_lstPaths[0];
                if (String.IsNullOrEmpty(sSubject) || sSubject[0] != '/' ||
                    sSubject.IndexOf((char)0) >= 0 ||
                    (sSubject.Length > 1 && sSubject.EndsWith("/", StringComparison.Ordinal)) ||
                    (objEdit.EDA_enmKind == EDK_enmEditKind.NameEntry && sSubject == "/"))
                {   /* Malformed: refused before any question or write access. */
                    return(new RSL_clsResult(SYS_REFUSED, "The captured naming path is invalid.", null));
                }

                /* What the name is for: a New Folder's item IS the parent; a   */
                /* Rename's item is split at its last "/" (image paths, never   */
                /* Windows rules) into its parent and its old name.             */
                enmResolved = EDK_enmEditKind.MakeDirectory;
                sParent = sSubject;
                sOldName = "";
                if (objEdit.EDA_enmKind == EDK_enmEditKind.NameEntry)
                {   /* A Rename: parent and old name. */
                    iSlash = sSubject.LastIndexOf('/');
                    sOldName = sSubject.Substring(iSlash + 1);
                    sParent = sSubject.Substring(0, iSlash);
                    enmResolved = EDK_enmEditKind.Rename;
                    if (sParent.Length == 0)
                    {   /* An item at the root: its parent is "/". */
                        sParent = "/";
                    }
                }

                /* The name asked, through the asking window, with the request   */
                /* as the question's context (an Exit can take the question      */
                /* down and put it back with what had been typed).  Anything     */
                /* but OK ends here, before write access is asked for.           */
                objAnswer = objExecution.EXC_Ask(QNK_enmQuestionKind.FolderName,
                    objRequest.RQT_lOrigin, objRequest);
                if (objAnswer == null || objAnswer.ANS_enmKind != ANK_enmAnswerKind.Yes)
                {   /* Cancelled: nothing changed, access unchanged. */
                    return(new RSL_clsResult(SYS_CANCELLED, "Name input cancelled.", null));
                }

                /* Whether the open filesystem is FAT or exFAT, for the length    */
                /* test and FAT's own rules below -- the worker's own record of   */
                /* the session (Sys_OpenSession), never the pane's.               */
                bFat = Sys_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_FAT12 ||
                    Sys_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_FAT16 ||
                    Sys_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_FAT32 ||
                    Sys_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_EXFAT;

                /* The name, trimmed, and held to the rules every image name   */
                /* shares: not empty, not "." or "..", no "/" or NUL.          */
                sName = objAnswer.ANS_sText.Trim();
                if (sName.Length == 0 || sName == "." || sName == ".." ||
                    sName.IndexOf('/') >= 0 || sName.IndexOf((char)0) >= 0)
                {   /* Not a valid name: refused, before write access. */
                    return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                        "That is not a valid name: it must not be empty, \".\" or \"..\", or contain '/'.", null));
                }

                if (!bFat && System.Text.Encoding.UTF8.GetByteCount(sName) > 255)
                {   /* Too long for ext2/3/4, whose directory entry holds at      */
                    /* most 255 BYTES of a name, stored as UTF-8: the bytes are   */
                    /* counted, since a count of characters would let a longer    */
                    /* multi-byte name by.  Refused before write access.          */
                    return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                        "That is not a valid name: an ext filesystem holds at most 255 bytes of a name.", null));
                }

                if (bFat && sName.Length > 255)
                {   /* Too long for FAT or exFAT, whose long name holds at most   */
                    /* 255 UTF-16 units -- the string's own length -- as          */
                    /* Windows allows on a FAT volume; ext's byte limit does      */
                    /* not bind here, so a name of 255 characters outside ASCII   */
                    /* is taken.  Refused before write access.                    */
                    return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                        "That is not a valid name: a FAT filesystem holds at most 255 characters of a name.", null));
                }

                if (bFat)
                {   /* The open filesystem is FAT or exFAT, whose names refuse   */
                    /* more than ext4's: the characters FAT forbids, and a       */
                    /* name of spaces and dots alone (SYS_FatNameProblem).       */
                    /* Refused here with words of its own, before write access   */
                    /* -- the engine would refuse them too, but a ':' reaches    */
                    /* FatFs as a drive prefix and comes back as an I/O error,   */
                    /* which tells the user nothing.  The volume type is the     */
                    /* worker's own record of the session (Sys_OpenSession).     */
                    sProblem = SYS_FatNameProblem(sName);
                    if (sProblem.Length != 0)
                    {   /* A name FAT cannot hold: refused, nothing changed. */
                        return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG, sProblem, null));
                    }
                }

                if (enmResolved == EDK_enmEditKind.Rename && sName == sOldName)
                {   /* A Rename to the same name: nothing to do, success, no   */
                    /* write access asked for.                                 */
                    return(new RSL_clsResult(SYS_OK, "", null));
                }

                /* The new path: the parent, a "/" unless the parent is the     */
                /* root, then the name -- from the captured parent, never the   */
                /* pane's folder now.                                           */
                sDestination = "/" + sName;
                if (sParent != "/")
                {   /* Not the root: parent, "/", name. */
                    sDestination = sParent + "/" + sName;
                }

                /* The edit's items: a new folder's path alone; a Rename's old   */
                /* path, with the new one as its destination.  Neither has a     */
                /* stage.                                                        */
                arrSubjects = new string[] { sDestination };
                arrDestinations = null;
                if (enmResolved == EDK_enmEditKind.Rename)
                {   /* A Rename: the item as it was, and its new path. */
                    arrSubjects[0] = sSubject;
                    arrDestinations = new string[] { sDestination };
                }

                /* The resolved edit as a request of this command's own: never    */
                /* posted, with the command's serial (so its Cancel and           */
                /* progress still name this command); the command's own request   */
                /* is the one released at its end.  It replaces objRequest for    */
                /* the rest of this sequence.                                     */
                objEdit = new EDA_clsEditArgs(enmResolved, arrDestinations, null,
                    false, 0, false, 0, 0, null, "");
                objNamed = new RQT_clsRequest(KND_enmKind.Edit, objRequest.RQT_lOrigin,
                    objRequest.RQT_lEpoch, objRequest.RQT_objTarget, arrSubjects,
                    objRequest.RQT_lRevision, objRequest.RQT_uiFlags, objEdit,
                    "", "", objRequest.RQT_sFolder);
                objNamed.RQT_lSerial = objRequest.RQT_lSerial;
                objRequest = objNamed;
            }

            if (bChange && objEdit != null && objEdit.EDA_enmKind == EDK_enmEditKind.Delete)
            {   /* A Delete: asked first, before write access.  (A cut's    */
                /* removal of its sources runs its delete step inside the   */
                /* copy sequence, so it is not asked a second time.)        */
                objAnswer = objExecution.EXC_Ask(QNK_enmQuestionKind.DeleteEntries,
                    objRequest.RQT_lOrigin);
                if (objAnswer == null || objAnswer.ANS_enmKind != ANK_enmAnswerKind.Yes)
                {   /* Not Yes: nothing deleted, access unchanged. */
                    return(new RSL_clsResult(SYS_CANCELLED, "Deletion declined.", null));
                }
            }

            if (bChange)
            {   /* An edit: write access first -- the first change asks the   */
                /* risk question and reopens writable (Sys_FirstChange); no   */
                /* other request can run in between.                          */
                objResult = Sys_FirstChange(objRequest, objExecution);
                if (objResult.RSL_iStatus != SYS_OK)
                {   /* Refused or failed: that outcome, no edit. */
                    return(objResult);
                }
            }

            /* The one step for the kinds left.  A New File's import alone     */
            /* gets a progress window (a large zero-filled file takes time),   */
            /* whose Cancel names this running request.                        */
            bProgress = false;
            objProgress = null;
            objEdit = objRequest.RQT_objPayload as EDA_clsEditArgs;
            switch (objRequest.RQT_enmKind)
            {   /* The request's kind. */
                case KND_enmKind.PickerList:
                    /* A picker's listing (its own stream; the main pane's   */
                    /* folder is not moved).                                 */
                    enmStep = STP_enmStep.List;
                    break;
                case KND_enmKind.Edit:
                    /* An edit; a New File's import with the copy window. */
                    enmStep = STP_enmStep.Edit;
                    bProgress = objEdit != null && objEdit.EDA_enmKind == EDK_enmEditKind.CreateFile;
                    if (bProgress)
                    {   /* A New File: the import's window. */
                        objProgress = new PGV_clsProgressView(false, "Importing into the image", "");
                    }
                    break;
                case KND_enmKind.DragExtract:
                    /* The drag's read-out: the Copy step, whose control asks   */
                    /* no question for a drag-out (CommandNativeControl.cs).    */
                    enmStep = STP_enmStep.Copy;
                    break;
                case KND_enmKind.ReadLink:
                    /* A link's target text. */
                    enmStep = STP_enmStep.ReadLink;
                    break;
                case KND_enmKind.VolumeInfo:
                    /* The filesystem's facts. */
                    enmStep = STP_enmStep.VolumeInfo;
                    break;
                case KND_enmKind.ListDevices:
                    /* The disks; no session needed. */
                    enmStep = STP_enmStep.ListDevices;
                    break;
                case KND_enmKind.ReadDeviceTable:
                    /* A device's partition choices. */
                    enmStep = STP_enmStep.ReadDeviceTable;
                    break;
                case KND_enmKind.ImageInfo:
                    /* An image file's facts. */
                    enmStep = STP_enmStep.ImageInfo;
                    break;
                case KND_enmKind.SetVerify:
                    /* The verify option (the newest posted, Latest). */
                    enmStep = STP_enmStep.SetVerify;
                    break;
                default:
                    /* A kind with no sequence and no step here: refused,   */
                    /* nothing guessed.                                     */
                    return(new RSL_clsResult(SYS_REFUSED, "Unsupported command kind.", null));
            }

            if (bProgress)
            {   /* The import's window, shown (it decides nothing about what   */
                /* may run).                                                   */
                SYS_Show(new VWM_clsView(VWK_enmViewKind.ProgressOpen, objRequest, objProgress));
            }

            try
            {   /* The step, to its end, before any other request runs. */
                objResult = Sys_Do(enmStep, objRequest, objExecution);
            }
            finally
            {   /* The window closed however the step ended. */
                if (bProgress)
                {   /* Opened above: closed. */
                    SYS_Show(new VWM_clsView(VWK_enmViewKind.ProgressClose, objRequest, null));
                }
            }

            if (bChange && (objResult.RSL_iStatus == SYS_OK ||
                (objEdit != null && objEdit.EDA_enmKind == EDK_enmEditKind.Delete)))
            {   /* After a successful edit -- or ANY delete, since one of     */
                /* several items may have gone before a later one failed --   */
                /* the pane is listed again; the edit's own result is kept.   */
                /* (A declined question or refused access returned above.)    */
                Sys_Refresh(objRequest, objExecution);
            }

            if (objRequest.RQT_enmKind == KND_enmKind.Edit && objResult.RSL_iStatus == SYS_OK)
            {   /* A successful edit: a New File asked to open opens now,   */
                /* before any other queued request.                         */
                if (objEdit != null && objEdit.EDA_enmKind == EDK_enmEditKind.CreateFile &&
                    objEdit.EDA_iOpenHow >= 0)
                {   /* An OpenEntry of this command's own (its serial, never   */
                    /* posted) carrying the create, whose stage the read-out   */
                    /* step hands to the editor instead of reading the file    */
                    /* out again (Nst_OpenCreatedEntry).                       */
                    objOpen = new RQT_clsRequest(KND_enmKind.OpenEntry, objRequest.RQT_lOrigin,
                        Sys_lEpoch, Sys_objTarget, null, 0, (uint)objEdit.EDA_iOpenHow,
                        objEdit, "", "", objRequest.RQT_sFolder);
                    objOpen.RQT_lstPaths = objRequest.RQT_lstPaths;
                    objOpen.RQT_lSerial = objRequest.RQT_lSerial;
                    objOpened = Sys_OpenEntrySequence(objOpen, objExecution);
                    if (objOpened.RSL_iStatus != SYS_OK)
                    {   /* The editor did not open: its status, with words that   */
                        /* say the file WAS created -- the create stands.         */
                        return(new RSL_clsResult(objOpened.RSL_iStatus,
                            "The file was created, but its editor could not be opened: " + objOpened.RSL_sDetail,
                            objResult.RSL_objPayload));
                    }
                }
            }

            /* The step's own outcome -- its counts, hash or warnings -- not   */
            /* the re-listing's.                                               */
            return(objResult);
        }

        /*--------------------------------------------------------------------*/
        /* SYS_FatNameProblem:                                                */
        /*                                                                    */
        /* Whether a name -- one name, not a path -- can be an entry on a FAT */
        /* or exFAT filesystem, beyond the rules every image name is held to  */
        /* first (not empty, not "." or "..", no '/' or NUL) and FAT's own    */
        /* length, which each caller tests (255 UTF-16 units, as Windows      */
        /* allows on a FAT volume).  FAT forbids the characters               */
        /* " * : < > ? | and '\' in a name, and every control character, DEL  */
        /* included -- FatFs refuses them all (ff.c, create_name), and reads  */
        /* a '\' as a separator and                                           */
        /* the text before a ':' as a drive, so neither may reach it in a     */
        /* name.  A name of spaces and dots alone is refused too: FAT drops a */
        /* name's trailing spaces and dots, as Windows does, and nothing of   */
        /* such a name would be left.  Trailing spaces and dots on an         */
        /* ordinary name are NOT refused -- they are dropped, so "notes." is  */
        /* made as "notes", the engine's rule and Explorer's alike.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sName : the name as typed (trimmed by the caller); not null.   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : "" when FAT can hold it; otherwise one sentence       */
        /*              saying why not, for the user.                         */
        /*--------------------------------------------------------------------*/
        internal static string SYS_FatNameProblem(string sName)
        {
            bool bOnlySpacesAndDots;
            char c;
            int i;

            /* Every character in turn: one FAT forbids ends the test, and     */
            /* whether every one is a space or a dot is gathered on the way.   */
            bOnlySpacesAndDots = true;
            for (i = 0; i < sName.Length; i++)
            {   /* One character of the name per iteration. */
                c = sName[i];
                if (c < ' ' || c == (char)0x7F || "\"*:<>?|\\".IndexOf(c) >= 0)
                {   /* A control character, DEL, or one of the eight FAT   */
                    /* keeps for itself: the sentence lists them.          */
                    return("A FAT or exFAT name cannot contain \" * : < > ? | \\ or a control character.");
                }

                if (c != ' ' && c != '.')
                {   /* Something besides a space or a dot: the name keeps   */
                    /* something once its ending is dropped.                */
                    bOnlySpacesAndDots = false;
                }
            }

            if (bOnlySpacesAndDots)
            {   /* Spaces and dots alone ("..." -- "." and ".." are refused   */
                /* before this): nothing would be left of it on FAT.          */
                return("A FAT or exFAT name cannot be made of spaces and dots alone.");
            }

            /* A name FAT can hold (its trailing spaces and dots, if any,   */
            /* are dropped when it is made).                                */
            return("");
        }
    }
}
