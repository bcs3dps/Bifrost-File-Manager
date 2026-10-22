/*----------------------------------------------------------------------------*/
/* CommandCopySequence.cs                                                     */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The worker's sequence for every copy -- Extract (image to Windows), Import */
/* (Windows to image) and a copy within the image -- and for a CUT of any of  */
/* them, run as one command so nothing else happens between the copying and   */
/* the removal of the sources.  A cut within the image is a rename; a cut     */
/* across filesystems is a whole copy first, and only a copy that reported no */
/* problem has its sources removed afterwards: from the image (Extract) or to */
/* the Recycle Bin (Import, CommandRecycleSources.cs).  A copy pasted beside  */
/* itself is given free names (" - Copy", " - Copy (2)" ...) on the worker,   */
/* from the folder as it is then.                                             */
/*----------------------------------------------------------------------------*/

/* The framework: Exception (System), the name sets and path lists      */
/* (Collections.Generic), and Encoding for names' UTF-8 bytes (Text).   */
using System;
using System.Collections.Generic;
using System.Text;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* CPO_clsCopyOutcome -- the payload of a cut across filesystems' result: */
    /* the copy's own result and the removal of the sources, kept apart,      */
    /* because the second can fail after the first has completed -- and the   */
    /* copies are not undone when it does -- documentation table.             */
    /*------------------------------------------------------------------------*/
    internal sealed class CPO_clsCopyOutcome
    {
        internal RSL_clsResult CPO_objCopy;     // the copy's result, with its counts, problems and notes
        internal RSL_clsResult CPO_objSources;  // the sources' removal -- or the declined / failed write access that prevented it; it never undoes the copy

        /*--------------------------------------------------------------------*/
        /* CPO_clsCopyOutcome:                                                */
        /*                                                                    */
        /* The two results of a cut across filesystems.                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objCopy    : the copy's result.                                */
        /*     objSources : the removal's result.                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the pair.  Nothing in it is atomic: some       */
        /*                     sources may be gone and others not.            */
        /*--------------------------------------------------------------------*/
        internal CPO_clsCopyOutcome(RSL_clsResult objCopy, RSL_clsResult objSources)
        {
            /* Both kept as given, so the window can say which half failed   */
            /* -- the removal can fail after every copy has arrived.         */
            CPO_objCopy = objCopy;
            CPO_objSources = objSources;
        }
    }

    internal sealed partial class SYS_clsSystem
    {
        /*--------------------------------------------------------------------*/
        /* Sys_CopyEnvelope:                                                  */
        /*                                                                    */
        /* A request for one step inside a copy command (a listing, the copy, */
        /* a rename, a delete), built here and never queued.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the copy command's request.                       */
        /*     enmKind    : the step's kind.                                  */
        /*     lstPaths   : the step's paths, used as they are (not copied).  */
        /*     objPayload : the step's arguments.                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RQT_clsRequest : the request, carrying the command's serial;   */
        /*                      it has nothing of its own to release.         */
        /*--------------------------------------------------------------------*/
        private RQT_clsRequest Sys_CopyEnvelope(RQT_clsRequest objRequest,
            KND_enmKind enmKind, List<string> lstPaths, object objPayload)
        {
            RQT_clsRequest objStep;

            /* The worker's filesystem and range as they are NOW, not the      */
            /* request's: a reopen for writing in this command (the first-     */
            /* change question) keeps the range and the epoch but is the       */
            /* session the step must use.  The command's origin, flags and     */
            /* folder, and its SERIAL, so the step's views (progress, a        */
            /* conflict question) belong to the one command.  The paths are    */
            /* set directly rather than through the constructor, which would   */
            /* copy them: they are already the command's own or this           */
            /* worker's, and nothing else changes them.                        */
            objStep = new RQT_clsRequest(enmKind, objRequest.RQT_lOrigin,
                Sys_lEpoch, Sys_objTarget, null, 0, objRequest.RQT_uiFlags,
                objPayload, "", "", objRequest.RQT_sFolder);
            objStep.RQT_lstPaths = lstPaths;
            objStep.RQT_lSerial = objRequest.RQT_lSerial;

            /* The step; the queued command's own resources are released   */
            /* with the command.                                           */
            return(objStep);
        }

        /*--------------------------------------------------------------------*/
        /* Sys_CopyFreeNames:                                                 */
        /*                                                                    */
        /* For a copy within the image pasted into the folder its items are   */
        /* in, the destinations to use: each item copied onto itself is given */
        /* a free name -- " - Copy", then " - Copy (2)" and on -- free in the */
        /* folder as the worker reads it now and among the other items of     */
        /* this copy.  Read before any write access is asked for.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the copy's request; its paths are the sources.  */
        /*     objExecution : the running command, for the listings.          */
        /*     objArgs      : the copy's arguments; its destinations are not  */
        /*                    changed.                                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : SYS_OK with a new list of destinations; or the */
        /*                     refusal or failure that stops the copy before  */
        /*                     anything is asked or written.                  */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_CopyFreeNames(RQT_clsRequest objRequest,
            EXC_clsExecution objExecution, CCA_clsCopyArgs objArgs)
        {
            Dictionary<string, HashSet<string>> dictTaken;
            HashSet<string> setListed;
            HashSet<string> setNames;
            StringComparer cmpNames;
            bool bFat;
            List<string> lstTo;
            List<string> lstReadPaths;
            string sParent;
            string sName;
            string sFree;
            int iSlash;
            int iTry;
            int i;
            int j;
            RSL_clsResult objListResult;
            ELR_clsListResult objListing;
            EEN_stEntry stEntry;

            /* How a folder's names are compared: exactly on ext2/3/4, whose     */
            /* names are case-sensitive, so "a - Copy" and "A - Copy" are two    */
            /* free names there; letter case aside on FAT and exFAT, whose       */
            /* names match that way -- an exact test there would offer           */
            /* "a - Copy" beside an existing "A - COPY", and the copy would      */
            /* then meet that entry as an "already exists" question instead      */
            /* of getting a name that is free.  The folding is the framework's   */
            /* ordinal one, as Image Save As's FAT name test uses                */
            /* (ImageSaveForm.cs); the volume type is the worker's own record    */
            /* of the session (Sys_OpenSession).  bFat records the same for      */
            /* the name-length test below.                                       */
            cmpNames = StringComparer.Ordinal;
            bFat = false;
            if (Sys_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_FAT12 ||
                Sys_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_FAT16 ||
                Sys_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_FAT32 ||
                Sys_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_EXFAT)
            {   /* A FAT or exFAT session: names compared letter case aside,   */
                /* and held to FAT's length rather than ext4's (below).        */
                cmpNames = StringComparer.OrdinalIgnoreCase;
                bFat = true;
            }

            /* The names taken, per folder: first every destination of this      */
            /* copy, so no generated name can take one another item is           */
            /* going to; each folder's own entries are added when it is read     */
            /* below.  The folders themselves are keyed exactly: every path      */
            /* here comes from the one paste, spelled alike.  The destinations   */
            /* are copied into a list of their own, as some will be changed;     */
            /* the arguments keep theirs.                                        */
            dictTaken = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            setListed = new HashSet<string>(StringComparer.Ordinal);
            lstTo = new List<string>(objArgs.CCA_lstOtherPaths);
            for (i = 0; i < lstTo.Count; i++)
            {   /* One destination per iteration: its folder and name, and    */
                /* the name recorded as taken in that folder.  Split at the   */
                /* last "/" only -- a backslash is an ordinary character in   */
                /* an ext name, so no Windows path call is used.              */
                iSlash = lstTo[i].LastIndexOf('/');
                if (iSlash < 0 || iSlash == lstTo[i].Length - 1)
                {   /* No "/", or nothing after the last: no name to copy   */
                    /* to.  Refused rather than taken as a whole folder.    */
                    return(new RSL_clsResult(SYS_REFUSED,
                        "A copy destination has no complete entry name.", null));
                }

                /* The folder: "/" for a root entry, else the text before the   */
                /* last "/", spelled exactly as given.                          */
                sParent = "/";
                if (iSlash != 0)
                {   /* Not under root: the path up to the last "/". */
                    sParent = lstTo[i].Substring(0, iSlash);
                }

                if (!dictTaken.TryGetValue(sParent, out setNames))
                {   /* The first destination in this folder: its own set,   */
                    /* comparing as the filesystem does (cmpNames).         */
                    setNames = new HashSet<string>(cmpNames);
                    dictTaken.Add(sParent, setNames);
                }

                /* The name, taken.  Taking it only keeps it from being         */
                /* generated; whether an existing entry may be overwritten is   */
                /* still the copy's conflict question.                          */
                setNames.Add(lstTo[i].Substring(iSlash + 1));
            }

            /* Then each item whose destination IS its source -- pasted into   */
            /* its own folder -- is given a free name; the others keep their   */
            /* destination.                                                    */
            for (i = 0; i < lstTo.Count; i++)
            {   /* One source / destination pair per iteration. */
                if (objRequest.RQT_lstPaths[i] != lstTo[i])
                {   /* Going somewhere else: kept as it is; an existing name   */
                    /* there is the copy's conflict question, as usual.        */
                    continue;
                }

                /* Its folder ("/" for root) and its name.  Every destination   */
                /* has a "/" and a name -- the loop above refused any other.    */
                iSlash = lstTo[i].LastIndexOf('/');
                sParent = "/";
                if (iSlash != 0)
                {   /* Not under root: the path up to the last "/". */
                    sParent = lstTo[i].Substring(0, iSlash);
                }

                /* The name, and the folder's set of names taken (made by the   */
                /* loop above for every destination folder).                    */
                sName = lstTo[i].Substring(iSlash + 1);
                setNames = dictTaken[sParent];
                if (!setListed.Contains(sParent))
                {   /* The folder not read yet: read now, once, and its      */
                    /* entries added to the names taken.  The listing is a   */
                    /* step of this command -- it neither moves the main     */
                    /* pane nor is shown.                                    */
                    lstReadPaths = new List<string>();
                    lstReadPaths.Add(sParent);
                    objListResult = Sys_Do(STP_enmStep.List,
                        Sys_CopyEnvelope(objRequest, KND_enmKind.ListFolder,
                            lstReadPaths, null), objExecution);
                    if (objListResult.RSL_iStatus != SYS_OK)
                    {   /* The folder could not be read: no name can be     */
                        /* known to be free, so the copy stops with the     */
                        /* listing's failure, before anything is asked or   */
                        /* written.                                         */
                        return(objListResult);
                    }

                    /* Its rows -- a success with none is a fault in the step,   */
                    /* not an empty folder.                                      */
                    objListing = objListResult.RSL_objPayload as ELR_clsListResult;
                    if (objListing == null || objListing.ELR_lstEntries == null)
                    {   /* No rows: refused as internal, nothing written. */
                        return(new RSL_clsResult(SYS_INTERNAL, "The copy-name listing is missing.", null));
                    }

                    /* Every entry's name added to the names taken. */
                    for (j = 0; j < objListing.ELR_lstEntries.Count; j++)
                    {   /* One entry per iteration: its name, from its   */
                        /* counted UTF-8 bytes.                          */
                        stEntry = objListing.ELR_lstEntries[j];
                        if (stEntry.EEN_abName == null ||
                            stEntry.EEN_uiNameBytes > stEntry.EEN_abName.Length)
                        {   /* A name buffer missing, or shorter than its count:   */
                            /* refused as internal -- skipping it could make a     */
                            /* name in use look free.                              */
                            return(new RSL_clsResult(SYS_INTERNAL,
                                "The copy-name listing has an invalid entry name.", null));
                        }

                        /* The name is the first EEN_uiNameBytes bytes; the rest   */
                        /* of the fixed buffer is padding.  Kept as spelled; the   */
                        /* set compares it as the filesystem does.                 */
                        setNames.Add(Encoding.UTF8.GetString(stEntry.EEN_abName, 0,
                            (int)stEntry.EEN_uiNameBytes));
                    }

                    /* Read: another item in the same folder uses this set. */
                    setListed.Add(sParent);
                }

                /* The first free name: the whole name (extension and all)      */
                /* with " - Copy" after it, then " - Copy (2)", (3) ... until   */
                /* one is not taken.                                            */
                sFree = sName + " - Copy";
                iTry = 2;
                while (setNames.Contains(sFree))
                {   /* Taken: the next number. */
                    sFree = sName + " - Copy (" + iTry + ")";
                    iTry++;
                }

                if (sFree.Length == 0 || sFree == "." || sFree == ".." ||
                    sFree.IndexOf('/') >= 0 || sFree.IndexOf('\0') >= 0 ||
                    (!bFat && Encoding.UTF8.GetByteCount(sFree) > 255) ||
                    (bFat && sFree.Length > 255))
                {   /* Not a name an entry can hold -- in practice, too long   */
                    /* once " - Copy" is added: over 255 UTF-8 bytes on        */
                    /* ext2/3/4 (ext4's limit), over 255 UTF-16 units on FAT   */
                    /* and exFAT (FAT's limit, and Windows' own):              */
                    /* the copy is refused, nothing written, with the advice   */
                    /* to copy into another folder.                            */
                    return(new RSL_clsResult(SYS_REFUSED,
                        "The name of " + sName +
                        " is too long to add \" - Copy\" to; copy it into another folder.", null));
                }

                /* The name taken at once, so the next item cannot be given   */
                /* it too; then the destination rebuilt in the same folder.   */
                setNames.Add(sFree);
                if (sParent == "/")
                {   /* Under root: "/" and the name. */
                    lstTo[i] = "/" + sFree;
                }
                else
                {   /* Elsewhere: the folder, "/" and the name. */
                    lstTo[i] = sParent + "/" + sFree;
                }
            }

            /* The destinations, as a new list; the request's arguments are   */
            /* unchanged.                                                     */
            return(new RSL_clsResult(SYS_OK, "", lstTo));
        }

        /*--------------------------------------------------------------------*/
        /* Sys_SpendCopyClipboard:                                            */
        /*                                                                    */
        /* Mark a cut from the clipboard as used, then tell the main window   */
        /* to clear the clipboard if it still holds that cut.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the copy's request.                               */
        /*     objArgs    : its arguments; CCA_uiClipboard is the cut's       */
        /*                  sequence number, 0 when it did not come from the  */
        /*                  clipboard.                                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the cut is recorded as spent; the clear is posted.      */
        /*--------------------------------------------------------------------*/
        private void Sys_SpendCopyClipboard(RQT_clsRequest objRequest, CCA_clsCopyArgs objArgs)
        {
            if (objArgs.CCA_uiClipboard != 0)
            {   /* A clipboard cut: its number recorded as spent, here on    */
                /* the worker, so the gate refuses it if pasted again        */
                /* (CommandSequences.cs) -- even when the clipboard cannot   */
                /* be cleared (another program holds it, or has replaced     */
                /* it).                                                      */
                Sys_setConsumedCuts.Add(objArgs.CCA_uiClipboard);
            }

            /* The clear, posted to the main window (CLP_Consume,               */
            /* CommandClipboard.cs), which clears only if the clipboard still   */
            /* holds this number -- a 0 clears nothing.                         */
            SYS_Show(new VWM_clsView(VWK_enmViewKind.ClipboardSpent, objRequest, objArgs.CCA_uiClipboard));
        }

        /*--------------------------------------------------------------------*/
        /* Sys_CopySourceResult:                                              */
        /*                                                                    */
        /* The result of a cut across filesystems whose copy completed: the   */
        /* sources' removal decides the status, and both results are kept.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objCopy    : the copy's result (a success).                    */
        /*     objSources : the removal's result; null is taken as an         */
        /*                  internal failure.                                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the removal's status, a message saying which   */
        /*                     half failed, and a CPO_clsCopyOutcome.         */
        /*--------------------------------------------------------------------*/
        private static RSL_clsResult Sys_CopySourceResult(RSL_clsResult objCopy, RSL_clsResult objSources)
        {
            string sDetail;

            if (objSources == null)
            {   /* No removal result: taken as an internal failure, so the   */
                /* copy's result is still reported.                          */
                objSources = new RSL_clsResult(SYS_INTERNAL, "Source cleanup returned no result.", null);
            }

            /* The message: the copy's, unless the removal failed -- then one    */
            /* that says the items WERE copied but the moved sources were not    */
            /* all removed, with the removal's reason.  Nothing is undone: the   */
            /* copies stay, and sources already removed stay removed.            */
            sDetail = objCopy.RSL_sDetail;
            if (objSources.RSL_iStatus != SYS_OK)
            {   /* The removal failed (or was declined): said so. */
                sDetail = "The items were copied, but removing the moved source items did not complete: " +
                    objSources.RSL_sDetail;
            }

            /* The removal's status, with both results for the window. */
            return(new RSL_clsResult(objSources.RSL_iStatus, sDetail,
                new CPO_clsCopyOutcome(objCopy, objSources)));
        }

        /*--------------------------------------------------------------------*/
        /* Sys_CopySequence:                                                  */
        /*                                                                    */
        /* The sequence for Extract, Import and Copy Within, a copy or a cut: */
        /* free names if asked for, write access if the image changes, then   */
        /* the rename (a cut within the image) or the copy with its progress  */
        /* window, then for a cut across filesystems the removal of the       */
        /* sources -- only after a copy that reported no problem.  The pane   */
        /* is re-listed afterwards whenever the image may have changed.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the request: the sources (its paths) and the    */
        /*                    destinations and options (CCA_clsCopyArgs),     */
        /*                    checked by the request factory.                 */
        /*     objExecution : the running command, for the steps, the         */
        /*                    progress and the questions.                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the copy's or the rename's result; for a cut   */
        /*                     across filesystems, the removal's (with both,  */
        /*                     Sys_CopySourceResult); or the first failure,   */
        /*                     after which nothing further is done.           */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_CopySequence(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            CCA_clsCopyArgs objArgs;
            CCA_clsCopyArgs objResolved;
            RQT_clsRequest objCopy;
            RSL_clsResult objCopyResult;
            RSL_clsResult objSourceResult;
            RSL_clsResult objNameResult;
            ECR_clsCopyResult objReport;
            EDA_clsEditArgs objEdit;
            bool bChange;
            bool bRefresh;
            bool bMove;
            string sTitle;

            /* The arguments (checked by the request factory; a malformed      */
            /* step request still meets the copy step's own refusal); the      */
            /* copy request, the command's own unless free names replace its   */
            /* destinations; whether this is a cut; whether the image          */
            /* changes (Import and Copy Within do, Extract only reads it);     */
            /* and no re-list due yet.                                         */
            objArgs = objRequest.RQT_objPayload as CCA_clsCopyArgs;
            objCopy = objRequest;
            bMove = objArgs != null && objArgs.CCA_bMoveSources;
            bChange = objRequest.RQT_enmKind == KND_enmKind.Import ||
                objRequest.RQT_enmKind == KND_enmKind.CopyWithin;
            bRefresh = false;
            try
            {   /* The whole sequence, as one command: nothing queued can   */
                /* change what is open between the copy and the removal.    */
                /* The finally below re-lists the pane.                     */
                if (objArgs != null && objArgs.CCA_bAutoNames)
                {   /* A copy within the image into its own folder: the free    */
                    /* names first, before write access is asked for.  (The     */
                    /* factory allows free names only for a plain copy within   */
                    /* the image, never a cut.)                                 */
                    objNameResult = Sys_CopyFreeNames(objRequest, objExecution, objArgs);
                    if (objNameResult.RSL_iStatus != SYS_OK)
                    {   /* No names: nothing asked or written. */
                        return(objNameResult);
                    }

                    /* A copy request with the new destinations: new arguments   */
                    /* carrying the two option structures (the copy step reads   */
                    /* only those and the destinations -- Nst_Copy), the new     */
                    /* list taken as it is, and the command's sources.           */
                    objResolved = new CCA_clsCopyArgs(null, objArgs.CCA_stExtract, objArgs.CCA_stImport);
                    objResolved.CCA_lstOtherPaths = (List<string>)objNameResult.RSL_objPayload;
                    objCopy = Sys_CopyEnvelope(objRequest, objRequest.RQT_enmKind,
                        objRequest.RQT_lstPaths, objResolved);
                }

                if (bMove && objRequest.RQT_enmKind == KND_enmKind.CopyWithin)
                {   /* A cut within the image: spent now, BEFORE the write     */
                    /* question -- so declining that question loses the cut    */
                    /* although nothing has moved.  Why the order is this is   */
                    /* not recorded.                                           */
                    Sys_SpendCopyClipboard(objRequest, objArgs);
                }

                if (bChange)
                {   /* Import or Copy Within: write access first (the first-   */
                    /* change question and reopen, if still read-only).        */
                    objSourceResult = Sys_FirstChange(objRequest, objExecution);
                    if (objSourceResult.RSL_iStatus != SYS_OK)
                    {   /* Declined or failed: nothing copied or renamed. */
                        return(objSourceResult);
                    }
                }

                if (bMove && objRequest.RQT_enmKind == KND_enmKind.CopyWithin)
                {   /* A cut within the image: a Move edit -- renames, never   */
                    /* a copy and a delete -- to the command's destinations    */
                    /* (used as they are: they are the command's own).  A      */
                    /* re-list is due whatever the rename does.                */
                    objEdit = new EDA_clsEditArgs(EDK_enmEditKind.Move, null, null,
                        false, 0, false, 0, 0, null, "");
                    objEdit.EDA_lstDestinations = objArgs.CCA_lstOtherPaths;
                    bRefresh = true;

                    /* The rename's result is the command's; the finally re-   */
                    /* lists the pane.                                         */
                    return(Sys_Do(STP_enmStep.Edit, Sys_CopyEnvelope(objRequest,
                        KND_enmKind.Edit, objRequest.RQT_lstPaths, objEdit), objExecution));
                }

                /* A copy (or the copy half of a cut across filesystems): its   */
                /* progress window's caption, by direction.                     */
                sTitle = "Importing into the image";
                if (objRequest.RQT_enmKind == KND_enmKind.Extract)
                {   /* Image to Windows. */
                    sTitle = "Extracting to Windows";
                }
                else if (objRequest.RQT_enmKind == KND_enmKind.CopyWithin)
                {   /* Image to image. */
                    sTitle = "Copying within the image";
                }

                /* The progress window, opened for the COMMAND's request (the   */
                /* outer one, EXC_ReadRequest) even when the copy runs with     */
                /* resolved destinations -- the copy request carries the same   */
                /* serial, so its progress reaches this window.                 */
                SYS_Show(new VWM_clsView(VWK_enmViewKind.ProgressOpen,
                    objExecution.EXC_ReadRequest(), new PGV_clsProgressView(false, sTitle, "")));
                try
                {   /* The copy.  A re-list is due from here when the image   */
                    /* changes, since a copy can write part of its items      */
                    /* before it fails.                                       */
                    bRefresh = bChange;
                    objCopyResult = Sys_Do(STP_enmStep.Copy, objCopy, objExecution);
                }
                finally
                {   /* The progress window closed however the copy ended --   */
                    /* closing it starts nothing.                             */
                    SYS_Show(new VWM_clsView(VWK_enmViewKind.ProgressClose,
                        objExecution.EXC_ReadRequest(), null));
                }

                if (objCopyResult.RSL_iStatus != SYS_OK || !bMove)
                {   /* A plain copy: its result is the command's.  Or a cut     */
                    /* whose copy failed, was cancelled, or ended partial       */
                    /* (an item skipped or failed, a conflict answered "skip"   */
                    /* among them): every source is kept, and the copy's        */
                    /* result returned.                                         */
                    return(objCopyResult);
                }

                /* A cut whose copy succeeded: its sources go only if the        */
                /* copy's report is there and counts no problem.  A problem      */
                /* is an item skipped or failed -- a conflict answered "skip"    */
                /* included (CommandNativeControl.cs counts it, the engine       */
                /* raising no event for it) -- so a source not copied is never   */
                /* removed.  Such a copy normally ends PARTIAL and is returned   */
                /* above; this test is the second line of that rule.             */
                objReport = objCopyResult.RSL_objPayload as ECR_clsCopyResult;
                if (objReport == null || objReport.ECR_iProblems != 0)
                {   /* No report, or problems reported: every source kept,   */
                    /* the result PARTIAL, with the copy's report so its     */
                    /* counts are still shown.                               */
                    return(new RSL_clsResult(ENG_clsNative.ENG_ERR_PARTIAL,
                        "The copy did not provide a complete successful report; the source items were kept.",
                        objCopyResult.RSL_objPayload));
                }

                if (objRequest.RQT_enmKind == KND_enmKind.Extract)
                {   /* A cut from the image to Windows: the cut spent now,     */
                    /* the copy having succeeded; then write access, and the   */
                    /* sources deleted from the image.                         */
                    Sys_SpendCopyClipboard(objRequest, objArgs);
                    try
                    {   /* An exception here is the removal's failure, and   */
                        /* the copy's result is still reported (below).      */
                        objSourceResult = Sys_FirstChange(objRequest, objExecution);
                        if (objSourceResult.RSL_iStatus != SYS_OK)
                        {   /* Declined or failed: the copies are made, the   */
                            /* image's originals all kept.                    */
                            return(Sys_CopySourceResult(objCopyResult, objSourceResult));
                        }

                        /* The delete: the command's sources, each with the        */
                        /* folder-or-file flag captured with it (used as it is).   */
                        /* A re-list is due from here.                             */
                        objEdit = new EDA_clsEditArgs(EDK_enmEditKind.Delete, null, null,
                            false, 0, false, 0, 0, null, "");
                        objEdit.EDA_lstDirectories = objArgs.CCA_lstDirectories;
                        bRefresh = true;
                        objSourceResult = Sys_Do(STP_enmStep.Edit,
                            Sys_CopyEnvelope(objRequest, KND_enmKind.Edit,
                                objRequest.RQT_lstPaths, objEdit), objExecution);
                    }
                    catch (Exception exRemove)
                    {   /* Thrown during the removal: the copies stand.  The    */
                        /* whole exception goes to the debug log, its message   */
                        /* (not the trace) to the result.                       */
                        if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                        {   /* Only while the debug log is kept: otherwise the line is never built. */
                            DBGLOG_clsDebugLog.DBGLOG_Write("copy source cleanup", exRemove.ToString());
                        }

                        /* The removal's failure, its detail the exception's message. */
                        objSourceResult = new RSL_clsResult(SYS_INTERNAL, exRemove.Message, null);
                    }
                }
                else
                {   /* A cut from Windows into the image: every item has been   */
                    /* imported, so the Windows originals go to the Recycle     */
                    /* Bin (CommandRecycleSources.cs).                          */
                    try
                    {   /* The recycle step returns its own failures; an   */
                        /* exception is caught below.                      */
                        objSourceResult = Sys_Do(STP_enmStep.Recycle, objRequest, objExecution);
                    }
                    catch (Exception exRemove)
                    {   /* Thrown during the recycling: the removal's      */
                        /* failure, not the import's.  Logged whole; its   */
                        /* message to the result.                          */
                        if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                        {   /* Only while the debug log is kept: otherwise the line is never built. */
                            DBGLOG_clsDebugLog.DBGLOG_Write("copy source cleanup", exRemove.ToString());
                        }

                        /* The recycling's failure, its detail the exception's message. */
                        objSourceResult = new RSL_clsResult(SYS_INTERNAL, exRemove.Message, null);
                    }
                    finally
                    {   /* The cut spent after the recycling, however it   */
                        /* went -- the import itself is complete.          */
                        Sys_SpendCopyClipboard(objRequest, objArgs);
                    }
                }

                /* The removal's status, with the copy's counts kept too. */
                return(Sys_CopySourceResult(objCopyResult, objSourceResult));
            }
            finally
            {   /* After any path that may have changed the image -- even a   */
                /* failure part-way -- the pane is re-listed (Sys_Refresh);   */
                /* its outcome is for the pane and does not change the        */
                /* command's result.                                          */
                if (bRefresh && Sys_enmMode == MOD_enmMode.Open)
                {   /* Only when an image is open: a failed reopen for   */
                    /* writing can leave none to read.                   */
                    Sys_Refresh(objRequest, objExecution);
                }
            }
        }
    }
}
