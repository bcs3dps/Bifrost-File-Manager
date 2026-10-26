/*----------------------------------------------------------------------------*/
/* CommandImageOpening.cs                                                     */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* Open Image and Check Filesystem on an image FILE, as one command on the    */
/* worker: the file's partition table is read first, without touching what is */
/* open; when it has one, the user picks the partition (the partition         */
/* dialog); then the ordinary Open or Check sequence runs on the chosen range */
/* -- closing what is open only at that point.  A file with no table is       */
/* opened whole, and if that mount fails the user is offered one chance to    */
/* type the offset the filesystem starts at.  The dialog supplies only a      */
/* start: the length comes from the table the worker read, never from the     */
/* window.                                                                    */
/*----------------------------------------------------------------------------*/

/* The framework: ArgumentException and the base types, the list of the   */
/* table's partitions, and FileStream for the first page's probe.         */
using System;
using System.Collections.Generic;
using System.IO;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* IFA_clsImageFileArgs -- the payload of an image file's Open or Check   */
    /* request (KND_enmKind.OpenImageFile, posted by MainForm.cs): which of   */
    /* the two it is, fixed when the file was chosen -- documentation table.  */
    /*------------------------------------------------------------------------*/
    internal sealed class IFA_clsImageFileArgs
    {
        internal bool IFA_bCheck; // true: Check Filesystem (report only); false: Open, read-only until a first change

        /*--------------------------------------------------------------------*/
        /* IFA_clsImageFileArgs:                                              */
        /*                                                                    */
        /* The payload.                                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bCheck : true for Check, false for Open.                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the payload; whatever partition is picked      */
        /*                     later, it stays an Open or a Check.            */
        /*--------------------------------------------------------------------*/
        internal IFA_clsImageFileArgs(bool bCheck)
        {
            /* Kept as given: nothing of the open session or the window goes   */
            /* in.                                                             */
            IFA_bCheck = bCheck;
        }
    }

    /*------------------------------------------------------------------------*/
    /* OFC_clsOffsetChoice -- the answer the partition dialog gives: a byte   */
    /* offset and nothing more.  No length: the start a window chose cannot   */
    /* say where a filesystem ends; the worker takes that from the table it   */
    /* read (FCD_SelectedTarget) -- documentation table.                      */
    /*------------------------------------------------------------------------*/
    internal sealed class OFC_clsOffsetChoice
    {
        internal ulong OFC_ullOffset; // the byte offset picked (a row's start) or typed; the worker checks it is a multiple of 512

        /*--------------------------------------------------------------------*/
        /* OFC_clsOffsetChoice:                                               */
        /*                                                                    */
        /* The answer's offset.                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     ullOffset : the offset, as the dialog parsed it.               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the answer data.  Making one opens nothing:    */
        /*                     it counts only as the reply of the question    */
        /*                     still current.                                 */
        /*--------------------------------------------------------------------*/
        internal OFC_clsOffsetChoice(ulong ullOffset)
        {
            /* Kept as given; its alignment is checked by the worker. */
            OFC_ullOffset = ullOffset;
        }
    }

    /*------------------------------------------------------------------------*/
    /* FCD_clsFileChoiceData -- what the worker learned of a file or device   */
    /* before opening it: its table and the words the partition dialog shows. */
    /* Made by the inspection (here for a file, CommandDeviceOpening.cs for a */
    /* device), handed to the dialog as the question's context, and used by   */
    /* the worker to turn the dialog's offset into a range -- documentation   */
    /* table.                                                                 */
    /*------------------------------------------------------------------------*/
    internal sealed class FCD_clsFileChoiceData
    {
        internal TGT_clsTarget FCD_objFile;            // the file or device inspected, whole (offset and length 0), with a device's geometry
        internal List<PRT_clsPartition> FCD_lstParts;  // its partitions in table order (empty: no table); read, never changed, by the dialog and the worker
        internal string FCD_sFound;                    // the table reader's line, shown by the partition question
        internal string FCD_sFallback;                 // the line the offset question shows after a whole-file mount failed (no table)
        internal bool FCD_bDevice;                     // true: a device, whose name, size and sector size go into the chosen range; false: an image file
        internal string FCD_sDeviceName;               // the device's description for the dialog's title; "" for a file (its name is used)

        /*--------------------------------------------------------------------*/
        /* FCD_clsFileChoiceData:                                             */
        /*                                                                    */
        /* The record of one inspection.                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objFile     : the file or device inspected.                    */
        /*     lstParts    : its partitions, possibly none; kept as it is.    */
        /*     sFound      : the table line; null is taken as "".             */
        /*     sFallback   : the no-table line; null is taken as "".          */
        /*     bDevice     : true for a device; optional, false by default.   */
        /*     sDeviceName : the device's description; optional, "" by        */
        /*                   default, null taken as "".                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the record; ArgumentException without a target */
        /*                     or a list.                                     */
        /*--------------------------------------------------------------------*/
        internal FCD_clsFileChoiceData(TGT_clsTarget objFile, List<PRT_clsPartition> lstParts,
            string sFound, string sFallback, bool bDevice = false, string sDeviceName = "")
        {
            if (objFile == null || lstParts == null)
            {   /* No target or no list -- an empty list is "no table", but   */
                /* a missing one is a fault in the inspection: thrown.        */
                throw new ArgumentException("Inspection needs a target and a complete partition list.");
            }

            /* The target and the list, kept by reference, not copied: the      */
            /* inspection has finished with them, and the dialog and the        */
            /* worker only read the rows -- a choice changes the dialog's own   */
            /* result, never this table.                                        */
            FCD_objFile = objFile;
            FCD_lstParts = lstParts;
            FCD_bDevice = bDevice;

            /* The three texts, each "" when not given, so the dialog can use   */
            /* them without a test.  They are only display text.                */
            FCD_sFound = "";
            FCD_sFallback = "";
            FCD_sDeviceName = "";
            if (sFound != null)
            {   /* The table line as the reader wrote it. */
                FCD_sFound = sFound;
            }

            if (sFallback != null)
            {   /* The no-table line as the inspection wrote it. */
                FCD_sFallback = sFallback;
            }

            if (sDeviceName != null)
            {   /* The device's description, as it was when inspected. */
                FCD_sDeviceName = sDeviceName;
            }
        }

        /*--------------------------------------------------------------------*/
        /* FCD_SelectedTarget:                                                */
        /*                                                                    */
        /* The range to open for a start the dialog gave: the partition that  */
        /* starts there, or from there to the end of the file or device.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     ullOffset : the start, already checked to be a multiple of     */
        /*                 512 (0 for a whole file).                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     TGT_clsTarget : the range -- with the partition's length when  */
        /*                     a row starts there, else length 0, which the   */
        /*                     engine takes as "to the end" (bfe_blockdev.c). */
        /*--------------------------------------------------------------------*/
        internal TGT_clsTarget FCD_SelectedTarget(ulong ullOffset)
        {
            ulong ullLength;
            ulong ullDiskBytes;
            uint uiSectorBytes;
            string sDeviceText;
            int i;

            /* The length: a listed partition's, when the start is one; for     */
            /* a start typed by hand, 0 -- to the end -- since no table says    */
            /* where it stops.  The engine refuses a start or an end past the   */
            /* file or device, so a wrong offset cannot read beyond it.         */
            ullLength = 0;
            for (i = 0; i < FCD_lstParts.Count; i++)
            {   /* One partition's start compared per iteration. */
                if (FCD_lstParts[i].PRT_ullOffset == ullOffset)
                {   /* It starts here: its length.  The first row with this   */
                    /* start is taken; a later one at the same start (a       */
                    /* damaged table) is not looked at.                       */
                    ullLength = FCD_lstParts[i].PRT_ullLength;
                    break;
                }
            }

            /* A device's description, size and sector size go into the range   */
            /* with it (the description names the device in the title and the   */
            /* write question), whether a partition or a typed offset was       */
            /* chosen; a file gets none, and 512-byte sectors.                  */
            sDeviceText = "";
            ullDiskBytes = 0;
            uiSectorBytes = 512;
            if (FCD_bDevice)
            {   /* A device: its facts as inspected. */
                sDeviceText = FCD_objFile.TGT_sDeviceText;
                ullDiskBytes = FCD_objFile.TGT_ullDiskBytes;
                uiSectorBytes = FCD_objFile.TGT_uiSectorBytes;
            }

            /* A new target with every coordinate set -- nothing of the   */
            /* dialog's rows is handed on.                                */
            return(new TGT_clsTarget(FCD_objFile.TGT_sPath, ullOffset, ullLength,
                sDeviceText, ullDiskBytes, uiSectorBytes));
        }
    }

    internal sealed partial class SYS_clsSystem
    {
        /*--------------------------------------------------------------------*/
        /* Sys_ImageSelectionRequest:                                         */
        /*                                                                    */
        /* The Open or Check request for the chosen range, run as a step of   */
        /* the image file's command rather than queued.                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the image file's own request (already given its   */
        /*                  serial by the queue).                             */
        /*     objTarget  : the range to open or check.                       */
        /*     bCheck     : true for Check, false for Open.                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RQT_clsRequest : the request, never posted, carrying the       */
        /*                      outer request's serial.                       */
        /*--------------------------------------------------------------------*/
        private static RQT_clsRequest Sys_ImageSelectionRequest(RQT_clsRequest objRequest,
            TGT_clsTarget objTarget, bool bCheck)
        {
            RQT_clsRequest objSelected;
            KND_enmKind enmKind;

            /* Its kind: Open, or Check -- a Check stays a Check whatever   */
            /* range was chosen.                                            */
            enmKind = KND_enmKind.Open;
            if (bCheck)
            {   /* Check Filesystem: report only. */
                enmKind = KND_enmKind.Check;
            }

            /* The request: the outer one's origin, epoch, identity and        */
            /* stream, the chosen range, and "/" as its folder (a new open     */
            /* lists the root); no payload -- nothing of the inspection or     */
            /* the dialog goes in.  It takes the outer request's SERIAL, so    */
            /* the views it publishes (a check's window, progress) name the    */
            /* one operation the queue knows, and a Cancel of that operation   */
            /* still matches.                                                  */
            objSelected = new RQT_clsRequest(enmKind, objRequest.RQT_lOrigin,
                objRequest.RQT_lEpoch, objTarget, null, 0, 0, null,
                objRequest.RQT_sIdentity, objRequest.RQT_sStream, "/");
            objSelected.RQT_lSerial = objRequest.RQT_lSerial;

            /* Run by this worker inside the outer command; it holds nothing   */
            /* to release of its own.                                          */
            return(objSelected);
        }

        /*--------------------------------------------------------------------*/
        /* Sys_ImageFileSequence:                                             */
        /*                                                                    */
        /* Open Image or Check Filesystem on an image file: inspect it, ask   */
        /* for the partition when it has a table, then open or check the      */
        /* range -- all as one command, so no other request runs between the  */
        /* reading of the table and the opening.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the request: the whole file (its target) and    */
        /*                    Open or Check (the IFA payload).                */
        /*     objExecution : the running command, for the steps, the         */
        /*                    questions and their interrupt checks.           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the open or check's own outcome; or the first  */
        /*                     failure, refusal or cancel before it.          */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_ImageFileSequence(RQT_clsRequest objRequest,
            EXC_clsExecution objExecution)
        {
            IFA_clsImageFileArgs objArgs;
            RSL_clsResult objInspection;
            RSL_clsResult objLifecycle;
            FCD_clsFileChoiceData objFacts;
            TGT_clsTarget objTarget;
            ANS_clsAnswer objAnswer;
            OFC_clsOffsetChoice objChoice;

            /* Inspect the file first (Nst_InspectImageFile: its table, and a   */
            /* note for a file with none), while what is open stays open: a     */
            /* Cancel at the partition question then leaves the open image      */
            /* and its editors as they were.  Closing it, with its own          */
            /* questions, belongs to the Open / Check sequence that follows.    */
            /* The payload's type was checked at the gate.                      */
            objArgs = (IFA_clsImageFileArgs)objRequest.RQT_objPayload;
            objInspection = Sys_Do(STP_enmStep.InspectImageFile, objRequest, objExecution);
            if (objInspection.RSL_iStatus != SYS_OK)
            {   /* The inspection failed (or a Cancel stopped it): nothing   */
                /* asked, nothing closed.                                    */
                return(objInspection);
            }

            /* Its record, which must describe this very file.  The            */
            /* inspection builds it on the request's own target, so the test   */
            /* is a guard against a step that returned something else.         */
            objFacts = objInspection.RSL_objPayload as FCD_clsFileChoiceData;
            if (objFacts == null || !objRequest.RQT_objTarget.TGT_SameRange(objFacts.FCD_objFile))
            {   /* No record, or another file's: refused as internal before   */
                /* anything is closed.                                        */
                return(new RSL_clsResult(SYS_INTERNAL,
                    "The image inspection returned unrelated file facts.", null));
            }

            /* The range: the whole file, unless it has a table, when the       */
            /* user must choose.  With no table the whole file is opened, and   */
            /* only a failed mount of it brings the offset question below.      */
            objTarget = objFacts.FCD_SelectedTarget(0);
            if (objFacts.FCD_lstParts.Count != 0)
            {   /* A table: the partition question (the partition dialog,   */
                /* CommandQuestionPresenter.cs), which shows the rows as    */
                /* read and needs nothing more of the worker while it is    */
                /* up; answered by the window that asked.                   */
                objAnswer = SYS_Ask(QNK_enmQuestionKind.ImagePartition, objRequest,
                    objRequest.RQT_lOrigin, objFacts);
                objChoice = null;
                if (objAnswer != null)
                {   /* The answer's offset, when it carries one. */
                    objChoice = objAnswer.ANS_objOffset;
                }

                if (objAnswer == null || objAnswer.ANS_enmKind != ANK_enmAnswerKind.SelectOffset ||
                    objChoice == null || objChoice.OFC_ullOffset % 512 != 0)
                {   /* Cancelled, interrupted, or an offset that is not a     */
                    /* multiple of 512: stopped as cancelled, and the image   */
                    /* that is open stays open -- nothing has been closed.    */
                    return(new RSL_clsResult(SYS_CANCELLED, "Partition selection cancelled or invalid.", null));
                }

                /* The chosen start, made a range with the table's length   */
                /* (not any length the dialog might have shown).            */
                objTarget = objFacts.FCD_SelectedTarget(objChoice.OFC_ullOffset);
            }

            /* Open or check the range: the whole Open / Check sequence        */
            /* (Sys_Sequence), with its gate and its questions, run directly   */
            /* as a step of this command, so nothing queued can change what    */
            /* is open between the inspection and this.                        */
            objLifecycle = Sys_Sequence(Sys_ImageSelectionRequest(objRequest,
                objTarget, objArgs.IFA_bCheck), objExecution);
            if (objArgs.IFA_bCheck || objFacts.FCD_lstParts.Count != 0 ||
                objLifecycle.RSL_iStatus != ENG_clsNative.ENG_ERR_MOUNT ||
                !(objLifecycle.RSL_objPayload is EOR_clsOpenResult))
            {   /* Its outcome stands -- success, or any failure but a       */
                /* whole-file Open whose MOUNT failed.  Only the open step   */
                /* carries an EOR_clsOpenResult, so a failed close of the    */
                /* old image or a failed listing after the open is never     */
                /* taken for it.  A Check gets no retry (why is not          */
                /* recorded), nor does a partition the table named.          */
                return(objLifecycle);
            }

            /* A file with no table that would not mount whole: the one         */
            /* chance to type where the filesystem starts.  The old image was   */
            /* already closed by that attempt.  The same dialog, showing the    */
            /* inspection's no-table line; this command still owns the          */
            /* worker, so nothing else runs meanwhile, and an Exit question     */
            /* that interrupts it puts it back with what was typed.             */
            objAnswer = SYS_Ask(QNK_enmQuestionKind.ImageOffset, objRequest,
                objRequest.RQT_lOrigin, objFacts);
            objChoice = null;
            if (objAnswer != null)
            {   /* The answer's offset, when it carries one. */
                objChoice = objAnswer.ANS_objOffset;
            }

            if (objAnswer == null || objAnswer.ANS_enmKind != ANK_enmAnswerKind.SelectOffset ||
                objChoice == null || objChoice.OFC_ullOffset % 512 != 0)
            {   /* Cancelled, interrupted, or not a multiple of 512: stopped   */
                /* as cancelled, with no further try and no second message     */
                /* (the failed mount was the first).                           */
                return(new RSL_clsResult(SYS_CANCELLED, "Offset selection cancelled or invalid.", null));
            }

            /* The retry: an Open of the typed offset to the end of the file.   */
            /* Its outcome is final -- the file is not inspected again and no   */
            /* other offset is asked for.                                       */
            return(Sys_Sequence(Sys_ImageSelectionRequest(objRequest,
                objFacts.FCD_SelectedTarget(objChoice.OFC_ullOffset), false), objExecution));
        }
    }

    internal sealed partial class NST_clsNativeSteps
    {
        /*---------------------------------------------------------------------*/
        /* Nst_InspectImageFile:                                               */
        /*                                                                     */
        /* The InspectImageFile step: an image file's partition table, and     */
        /* for a file with none, a note saying what its first page holds --    */
        /* read with ordinary file calls, the open session untouched.          */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     objRequest : the request whose target is the whole Windows      */
        /*                  file (the gate refuses a device or a range).       */
        /*                                                                     */
        /* Returns:                                                            */
        /*     RSL_clsResult : SYS_OK with an FCD_clsFileChoiceData -- always: */
        /*                     a file that cannot be read gives an empty       */
        /*                     table and says so, and the open that follows    */
        /*                     reports the real failure.                       */
        /*---------------------------------------------------------------------*/
        private static RSL_clsResult Nst_InspectImageFile(RQT_clsRequest objRequest)
        {
            List<PRT_clsPartition> lstParts;
            string sFound;
            string sFallback;
            string sContent;
            byte[] abProbe;
            FileStream stmProbe;

            /* The table (PRF_ReadTableFile, PartitionForm.cs): it opens the    */
            /* file read-only and shared and closes it again, fills the list,   */
            /* and returns the line the dialog shows -- for a file it cannot    */
            /* read, an empty list and a line that says so.  The no-table       */
            /* line starts as the plain one, which names every filesystem the   */
            /* engine opens -- it is shown only after the whole-file mount      */
            /* failed, and that mount tries ext2/3/4, then squashfs, then FAT   */
            /* and exFAT.                                                       */
            lstParts = new List<PRT_clsPartition>();
            sFound = PRF_clsPartitionForm.PRF_ReadTableFile(objRequest.RQT_objTarget.TGT_sPath, lstParts);
            sFallback = "No filesystem this program opens (ext2, ext3, ext4, squashfs, FAT or exFAT) at the start of the file, and no partition table. Type the offset the filesystem starts at.";
            if (lstParts.Count == 0)
            {   /* No table.  The first page is read to say what the file   */
                /* holds when it is not ext -- words for the offset         */
                /* question, shown only if the whole-file mount fails; it   */
                /* decides nothing.                                         */
                try
                {   /* Any failure leaves the plain line. */
                    abProbe = new byte[PTB_Tables.PTB_PROBE_BYTES];
                    using (stmProbe = new FileStream(objRequest.RQT_objTarget.TGT_sPath,
                        FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {   /* Read-only and shared, and closed at this block's   */
                        /* end -- before the engine opens the file.           */
                        if (stmProbe.Read(abProbe, 0, abProbe.Length) == abProbe.Length)
                        {   /* A whole page read.  Anything less -- a shorter   */
                            /* file, or a read that returned short, which       */
                            /* Stream.Read is documented to be free to do --    */
                            /* keeps the plain line rather than test bytes      */
                            /* that were never read.  The page's words          */
                            /* (PTB_ContentText).                               */
                            sContent = PTB_Tables.PTB_ContentText(abProbe);
                            if (sContent.Length != 0 && !sContent.StartsWith("ext"))
                            {   /* Something known that is not ext: named, so     */
                                /* the user sees what is there.  The sentence     */
                                /* says only that it would not open, never that   */
                                /* it is not a kind the program reads: the        */
                                /* words may name squashfs, FAT or exFAT, which   */
                                /* the engine does read, and then the volume      */
                                /* itself is what the mount refused.  The double  */
                                /* spaces are closed up as PTB_Read does for a    */
                                /* whole disk (PartitionTable.cs).  An ext page   */
                                /* keeps the plain line.                          */
                                sFallback = "No partition table: the whole file holds " +
                                    sContent.Replace("  ", " ") +
                                    ", and it would not open as a filesystem this program reads (ext2, ext3, ext4, squashfs, FAT or exFAT). Type an offset if one starts further in.";
                            }
                        }
                    }
                }
                catch (Exception exProbe)
                {   /* The probe could not be made: logged, and the plain line   */
                    /* stays -- the mount that follows reports the real          */
                    /* failure.  The stream was closed by the using block.       */
                    if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                    {   /* Only while the debug log is kept: otherwise the line is never built. */
                        DBGLOG_clsDebugLog.DBGLOG_Write("image probe", exProbe.Message);
                    }
                }
            }

            /* The record: the request's own target (so the sequence's       */
            /* same-file test holds), the rows as read, and the two lines.   */
            return(new RSL_clsResult(SYS_clsSystem.SYS_OK, "",
                new FCD_clsFileChoiceData(objRequest.RQT_objTarget, lstParts, sFound, sFallback)));
        }
    }
}
