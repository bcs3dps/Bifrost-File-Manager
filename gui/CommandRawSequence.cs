/*----------------------------------------------------------------------------*/
/* CommandRawSequence.cs                                                      */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The raw copies as command sequences on the worker: Read Device (a whole    */
/* device, or through its backup GPT, to an image file) and Write Device (an  */
/* image file, plain or compressed, to a device), and the partition window's  */
/* Read, Write and Erase of one partition.  Each checks the captured request, */
/* inspects what it must just before it acts (the image file's format and     */
/* size, the device's GPT end) rather than trusting the picker's facts, asks  */
/* before anything destructive, and runs the copy under its progress window.  */
/* The copy itself -- its read-back, the hash and the sum's file -- is the    */
/* CopyRaw step's (Nst_Raw, CommandNativeOperations.cs); the engine holds     */
/* both ends to their identities, the system disk and the sizes.              */
/*----------------------------------------------------------------------------*/

/* The framework (System): String and its comparisons, UInt64's limit and   */
/* the exceptions caught; the GPT disks' ends handed to Read Device's       */
/* question (System.Collections.Generic); the chosen file's name in the     */
/* read's title (System.IO).                                                */
using System;
using System.Collections.Generic;
using System.IO;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* RWF_clsRawWriteFacts -- what a device write's question shows: the      */
    /* image file's facts, inspected just before asking -- documentation      */
    /* table.                                                                 */
    /*------------------------------------------------------------------------*/
    internal sealed class RWF_clsRawWriteFacts
    {
        internal IFI_stImageInfo RWF_stInfo; // the image file's format and sizes, read in this run, copied by value

        /*--------------------------------------------------------------------*/
        /* RWF_clsRawWriteFacts:                                              */
        /*                                                                    */
        /* The facts for the question.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stInfo : the image file's information, as the ImageInfo step   */
        /*              returned it.                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the facts; no file or engine memory is held    */
        /*                     while the question waits.                      */
        /*--------------------------------------------------------------------*/
        internal RWF_clsRawWriteFacts(IFI_stImageInfo stInfo)
        {
            /* The structure copied by value: its members are all numbers, so   */
            /* the copy holds nothing of the engine's.                          */
            RWF_stInfo = stInfo;
        }
    }

    /*------------------------------------------------------------------------*/
    /* PWF_clsPartitionWriteFacts -- what a partition write's question shows, */
    /* inspected by the worker just before it asks: the file and the size of  */
    /* the image inside it, and the partition and its size.  Whether the      */
    /* question offers to write only what fits (a larger file) or to zero the */
    /* rest (a smaller one) follows from the two sizes.                       */
    /*------------------------------------------------------------------------*/
    internal sealed class PWF_clsPartitionWriteFacts
    {
        /* The question's facts -- documentation table. */
        internal string PWF_sFile;          // the Windows image file to be written
        internal ulong  PWF_ullImageBytes;  // the image's own size, bytes (decompressed size for a compressed file)
        internal uint   PWF_uiKind;         // ENG_IMAGE_*: raw, or the compressed format, as its first bytes say
        internal string PWF_sPlace;         // the partition's terse name
        internal ulong  PWF_ullPartBytes;   // the partition's size, bytes
        internal bool   PWF_bDevice;        // true when the disk is a real device (its volumes are locked while written)

        /*--------------------------------------------------------------------*/
        /* PWF_clsPartitionWriteFacts:                                        */
        /*                                                                    */
        /* Keep the inspected facts for the question; plain values, no file   */
        /* or native handle is held while the question waits.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sFile         : the image file.                                */
        /*     ullImageBytes : the image's size, known (the sequence refuses  */
        /*                     an unknown one before asking).                 */
        /*     uiKind        : its ENG_IMAGE_* format.                        */
        /*     sPlace        : the partition's name.                          */
        /*     ullPartBytes  : the partition's size.                          */
        /*     bDevice       : the disk is a device.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the facts.                                     */
        /*--------------------------------------------------------------------*/
        internal PWF_clsPartitionWriteFacts(string sFile, ulong ullImageBytes, uint uiKind,
            string sPlace, ulong ullPartBytes, bool bDevice)
        {
            /* Copied as given. */
            PWF_sFile         = sFile;
            PWF_ullImageBytes = ullImageBytes;
            PWF_uiKind        = uiKind;
            PWF_sPlace        = sPlace;
            PWF_ullPartBytes  = ullPartBytes;
            PWF_bDevice       = bDevice;
        }
    }

    internal sealed partial class SYS_clsSystem
    {
        /*--------------------------------------------------------------------*/
        /* Sys_RawEnvelope:                                                   */
        /*                                                                    */
        /* A request for one step of a raw sequence -- an inspection, the GPT */
        /* end, the copy itself -- made from the running command: never       */
        /* posted, and carrying the command's own serial, so the step's       */
        /* progress and Cancel belong to the command.  Also used for Read     */
        /* Device's GPT reads before its question (Sys_AskDeviceRead).        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the running command.                              */
        /*     enmKind    : the kind the step's request carries.              */
        /*     objTarget  : the file or device the step reads or copies.      */
        /*     objArgs    : the copy's arguments, or null for an inspection.  */
        /*     uiFlags    : ENG_RAW_* for a copy; 0 for an inspection.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RQT_clsRequest : the step's request.                           */
        /*--------------------------------------------------------------------*/
        private static RQT_clsRequest Sys_RawEnvelope(RQT_clsRequest objRequest,
            KND_enmKind enmKind, TGT_clsTarget objTarget, RWA_clsRawArgs objArgs, uint uiFlags)
        {
            RQT_clsRequest objStep;

            /* The step's request, with the command's origin, epoch and         */
            /* folder, and the command's serial in place of one of its own --   */
            /* it is never posted, so it has no queue life and no stage to      */
            /* release; the command's own request keeps all of that.            */
            objStep = new RQT_clsRequest(enmKind, objRequest.RQT_lOrigin,
                objRequest.RQT_lEpoch, objTarget, null, 0, uiFlags, objArgs,
                "", "", objRequest.RQT_sFolder);
            objStep.RQT_lSerial = objRequest.RQT_lSerial;

            /* The step's request, for Sys_Do. */
            return(objStep);
        }

        /*---------------------------------------------------------------------*/
        /* Sys_AskDeviceRead:                                                  */
        /*                                                                     */
        /* Read Device as the menu asks it: the device, how much of it, the    */
        /* compression and the image file, asked as ONE question of the read's */
        /* own sequence (DeviceRead, over the main window) before anything     */
        /* is copied -- so the choice and the copy are one operation and       */
        /* nothing is posted in between.  On a choice the request becomes the  */
        /* read it describes, in place: the device as its target, the .xz      */
        /* flag, and the copy's arguments with the window's title.             */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     objRequest   : the ReadDevice command, its arguments an         */
        /*                    RDQ_clsDeviceReadQuestion (the save dialog's     */
        /*                    start); changed in place on a choice.            */
        /*     objExecution : the running command's services.                  */
        /*                                                                     */
        /* Returns:                                                            */
        /*     RSL_clsResult : SYS_OK with the request now a read; or the      */
        /*                     list's failure as it came, SYS_INTERNAL for a   */
        /*                     list with no facts, SYS_REFUSED for no disks    */
        /*                     or the open filesystem's own file chosen, and   */
        /*                     SYS_CANCELLED for a stop or a Cancel.           */
        /*---------------------------------------------------------------------*/
        private RSL_clsResult Sys_AskDeviceRead(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            RSL_clsResult objResult;
            RSL_clsResult objGpt;
            EDV_clsDeviceResult objListed;
            GPE_clsGptEnd objEnd;
            Dictionary<string, ulong> dictGptEnds;
            RDQ_clsDeviceReadQuestion objPosted;
            ANS_clsAnswer objAnswer;
            RDI_clsDeviceReadIntent objIntent;
            string sTitle;
            int i;

            /* What the menu posted: only where the save dialog starts (the   */
            /* caller has tested the record's type).                          */
            objPosted = (RDQ_clsDeviceReadQuestion)objRequest.RQT_objPayload;

            /* STEP 1: the devices listed now, on the worker, for the picker. */
            objResult = Sys_Do(STP_enmStep.ListDevices, objRequest, objExecution);
            if (objResult.RSL_iStatus != SYS_OK)
            {   /* The list could not be read (or the command was stopped):   */
                /* its outcome is the result.                                 */
                return(objResult);
            }

            /* The list itself, which a success must carry -- a success   */
            /* without one is an internal fault -- and an empty list is   */
            /* refused, as there is nothing to ask about.                 */
            objListed = objResult.RSL_objPayload as EDV_clsDeviceResult;
            if (objListed == null || objListed.EDV_lstDisks == null)
            {   /* Success with no list: a failure, not an empty choice. */
                return(new RSL_clsResult(SYS_INTERNAL, "Device enumeration returned no usable facts.", null));
            }

            if (objListed.EDV_lstDisks.Count == 0)
            {   /* No device at all: refused. */
                return(new RSL_clsResult(SYS_REFUSED, "No disks could be found.", null));
            }

            /* STEP 2: the picker's GPT box is shown only for a disk that        */
            /* holds a GPT, so every listed disk's two table sectors are read    */
            /* now, before the question, and each GPT disk's end is handed to    */
            /* it.  A disk that cannot be read (no card in the reader, say) is   */
            /* left out -- it simply gets no box -- rather than failing the      */
            /* whole choice.  The read sequence reads the chosen disk's header   */
            /* again before it copies, so a disk changed meanwhile is caught.    */
            dictGptEnds = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            for (i = 0; i < objListed.EDV_lstDisks.Count; i++)
            {   /* One listed disk per iteration, read as a whole-disk target. */
                objGpt = Sys_Do(STP_enmStep.ReadGptEnd, Sys_RawEnvelope(objRequest, KND_enmKind.ReadDeviceTable,
                    new TGT_clsTarget(objListed.EDV_lstDisks[i].DEV_sPath, 0, 0, "",
                        objListed.EDV_lstDisks[i].DEV_stInfo.DVI_ullSize, objListed.EDV_lstDisks[i].DEV_stInfo.DVI_uiSectorBytes),
                    null, 0), objExecution);
                objEnd = objGpt.RSL_objPayload as GPE_clsGptEnd;
                if (objGpt.RSL_iStatus == SYS_OK && objEnd != null && objEnd.GPE_bGpt)
                {   /* A GPT disk: its end goes to the picker. */
                    dictGptEnds[objListed.EDV_lstDisks[i].DEV_sPath] = objEnd.GPE_ullEndBytes;
                }
            }

            /* The device reads have no progress events, so the checkpoint is   */
            /* here: a Cancel or an Exit asked meanwhile is honoured before     */
            /* the question is shown.                                           */
            if (!objExecution.EXC_Checkpoint())
            {   /* Stopped: no question. */
                return(new RSL_clsResult(SYS_CANCELLED, "Device selection cancelled.", null));
            }

            /* STEP 3: the question, over the main window: the device picker     */
            /* in its read mode, then the save dialog                            */
            /* (CommandQuestionPresenter.cs, Qpr_DeviceRead), answered as one.   */
            objAnswer = objExecution.EXC_Ask(QNK_enmQuestionKind.DeviceRead, 0,
                new RDQ_clsDeviceReadQuestion(objListed.EDV_lstDisks, dictGptEnds, objPosted.RDQ_sStartFolder));
            objIntent = null;
            if (objAnswer != null && objAnswer.ANS_enmKind == ANK_enmAnswerKind.Yes)
            {   /* Chosen (a guard tests the record's type). */
                objIntent = objAnswer.ANS_objValue as RDI_clsDeviceReadIntent;
            }

            if (objIntent == null)
            {   /* Cancelled at either dialog, or the window gone: nothing   */
                /* read, no file made.                                       */
                return(new RSL_clsResult(SYS_CANCELLED, "No device was chosen.", null));
            }

            /* STEP 4: the file chosen must not be the open filesystem's own   */
            /* -- the gate's test for a raw copy, which this request passed    */
            /* before it had a file.  By spelling only, as there; another      */
            /* spelling of the same file is refused by the engine's identity   */
            /* test when the copy runs.                                        */
            if (Sys_enmMode == MOD_enmMode.Open && Sys_objTarget != null &&
                String.Equals(Sys_objTarget.TGT_sPath, objIntent.RDI_sOutput, StringComparison.OrdinalIgnoreCase))
            {   /* The open filesystem's file: refused, nothing copied. */
                return(new RSL_clsResult(SYS_REFUSED, "That destination is open in this window. Close it before raw imaging to it.", null));
            }

            /* STEP 5: the progress window's title says how much is read:       */
            /* through the backup GPT (whose length the read works out from     */
            /* the disk, so none is named here), a typed length, or the whole   */
            /* device.                                                          */
            if (objIntent.RDI_bThroughGpt)
            {   /* The GPT box: from the start through the backup partition table. */
                sTitle = "Reading " + objIntent.RDI_objTarget.TGT_sPath +
                    " through its backup partition table to " + Path.GetFileName(objIntent.RDI_sOutput);
            }
            else if (objIntent.RDI_ullRange == 0)
            {   /* The whole device, to its last sector. */
                sTitle = "Reading " + objIntent.RDI_objTarget.TGT_sPath + " to " +
                    Path.GetFileName(objIntent.RDI_sOutput);
            }
            else
            {   /* A typed length: the title names how much, so the window   */
                /* says what is being copied while it runs.                  */
                sTitle = "Reading the first " + DVF_clsDeviceForm.DVF_SizeText(objIntent.RDI_ullRange) + " of " +
                    objIntent.RDI_objTarget.TGT_sPath + " to " + Path.GetFileName(objIntent.RDI_sOutput);
            }

            /* STEP 6: the request becomes the read, in place: the whole         */
            /* device as its target; the .xz output flag when the box was        */
            /* ticked, whatever the file name's suffix says; and the copy from   */
            /* the device's start to the file's, the range (0 for the whole      */
            /* device, or with the GPT box), hashed, with the title, the copy    */
            /* phase's words and the GPT box.  The read sequence then checks     */
            /* it all as it checks any read.                                     */
            objRequest.RQT_objTarget = objIntent.RDI_objTarget;
            objRequest.RQT_uiFlags = 0;
            if (objIntent.RDI_bXz)
            {   /* Compressed output. */
                objRequest.RQT_uiFlags = ENG_clsNative.ENG_RAW_XZ_OUT;
            }

            objRequest.RQT_objPayload = new RWA_clsRawArgs(objIntent.RDI_objTarget.TGT_sPath, 0, objIntent.RDI_sOutput, 0,
                objIntent.RDI_ullRange, true, sTitle, "Reading the device", objIntent.RDI_bThroughGpt);

            /* Asked and set: the read goes on. */
            return(new RSL_clsResult(SYS_OK, "", null));
        }

        /*--------------------------------------------------------------------*/
        /* Sys_AskDeviceWrite:                                                */
        /*                                                                    */
        /* Write Device as the menu asks it: the image file, then -- once     */
        /* the file has been inspected, so the picker knows how much it can   */
        /* write -- the device and the range, each asked as a question of the */
        /* write's own sequence over the main window, before anything is      */
        /* written.  On a choice the request becomes the write it describes,  */
        /* in place: the device as its target and the copy's arguments with   */
        /* the window's title.  The write then inspects the file again and    */
        /* asks before it writes, as any write does.                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the WriteDevice command, its arguments a        */
        /*                    WDQ_clsDeviceWriteQuestion (the file dialog's   */
        /*                    start; the image recorded in it once chosen);   */
        /*                    changed in place on a choice.                   */
        /*     objExecution : the running command's services.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : SYS_OK with the request now a write; or the    */
        /*                     inspection's or the list's failure as it came, */
        /*                     SYS_INTERNAL for one with no facts,            */
        /*                     SYS_REFUSED for no disks, and SYS_CANCELLED    */
        /*                     for a stop or a Cancel at either question.     */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_AskDeviceWrite(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            RSL_clsResult objResult;
            EIR_clsImageResult objImage;
            EDV_clsDeviceResult objListed;
            WDQ_clsDeviceWriteQuestion objPosted;
            ANS_clsAnswer objAnswer;
            DCH_clsDeviceChoice objPicked;
            string sImage;

            /* What the menu posted: where the file dialog starts (the caller   */
            /* has tested the record's type).                                   */
            objPosted = (WDQ_clsDeviceWriteQuestion)objRequest.RQT_objPayload;

            /* STEP 1: the image file, over the main window. */
            objAnswer = objExecution.EXC_Ask(QNK_enmQuestionKind.DeviceWriteFile, 0, objPosted.WDQ_sStartFolder);
            sImage = null;
            if (objAnswer != null && objAnswer.ANS_enmKind == ANK_enmAnswerKind.Yes)
            {   /* A file named. */
                sImage = objAnswer.ANS_sText;
            }

            if (String.IsNullOrEmpty(sImage))
            {   /* Cancelled, or the window gone: nothing inspected or written. */
                return(new RSL_clsResult(SYS_CANCELLED, "No image file was chosen.", null));
            }

            /* Recorded on the posted record, so the completion remembers   */
            /* the file's folder whatever follows.                          */
            objPosted.WDQ_sImage = sImage;

            /* STEP 2: the file inspected now, before any device is offered,   */
            /* as one whole Windows file: a file that cannot be read gets no   */
            /* picker.  Its length only bounds the picker's range: the write   */
            /* inspects the file again before it asks and writes.              */
            objResult = Sys_Do(STP_enmStep.ImageInfo,
                Sys_RawEnvelope(objRequest, KND_enmKind.ImageInfo, new TGT_clsTarget(sImage, 0, 0, "", 0, 512), null, 0),
                objExecution);
            if (objResult.RSL_iStatus != SYS_OK)
            {   /* Unreadable or damaged: its failure is the result. */
                return(objResult);
            }

            /* Its facts, which a success must carry. */
            objImage = objResult.RSL_objPayload as EIR_clsImageResult;
            if (objImage == null)
            {   /* Success with no facts: a failure, not a length of 0. */
                return(new RSL_clsResult(SYS_INTERNAL, "Image inspection returned no usable facts.", null));
            }

            /* STEP 3: the devices listed now, on the worker, for the picker. */
            objResult = Sys_Do(STP_enmStep.ListDevices, objRequest, objExecution);
            if (objResult.RSL_iStatus != SYS_OK)
            {   /* The list could not be read (or the command was stopped):   */
                /* its outcome is the result.                                 */
                return(objResult);
            }

            /* The list itself, which a success must carry -- a success   */
            /* without one is an internal fault -- and an empty list is   */
            /* refused, as there is nothing to ask about.                 */
            objListed = objResult.RSL_objPayload as EDV_clsDeviceResult;
            if (objListed == null || objListed.EDV_lstDisks == null)
            {   /* Success with no list: a failure, not an empty choice. */
                return(new RSL_clsResult(SYS_INTERNAL, "Device enumeration returned no usable facts.", null));
            }

            if (objListed.EDV_lstDisks.Count == 0)
            {   /* No device at all: refused. */
                return(new RSL_clsResult(SYS_REFUSED, "No disks could be found.", null));
            }

            /* The inspection and the list have no progress events, so the   */
            /* checkpoint is here: a Cancel or an Exit asked meanwhile is    */
            /* honoured before the picker is shown.                          */
            if (!objExecution.EXC_Checkpoint())
            {   /* Stopped: no picker. */
                return(new RSL_clsResult(SYS_CANCELLED, "Device selection cancelled.", null));
            }

            /* STEP 4: the device and the range, over the main window: the   */
            /* picker's write mode, given the image's length as the most a   */
            /* range may ask to write (ENG_SIZE_UNKNOWN when its format      */
            /* records none, leaving only the device's own limit), the       */
            /* choice described as a raw write (false).                      */
            objAnswer = objExecution.EXC_Ask(QNK_enmQuestionKind.DeviceChoice, 0,
                new DCQ_clsDeviceQuestion(objListed.EDV_lstDisks, "Write the image to a device", DVF_clsDeviceForm.DVF_RANGE_WRITE,
                    objImage.EIR_stInfo.IFI_ullImageBytes, false));
            objPicked = null;
            if (objAnswer != null && objAnswer.ANS_enmKind == ANK_enmAnswerKind.Yes)
            {   /* Chosen (a guard tests the record's type). */
                objPicked = objAnswer.ANS_objValue as DCH_clsDeviceChoice;
            }

            if (objPicked == null)
            {   /* Cancelled: nothing written. */
                return(new RSL_clsResult(SYS_CANCELLED, "No device was chosen.", null));
            }

            /* STEP 5: the request becomes the write, in place: the device as   */
            /* its target, the image from its start to the device's, the        */
            /* range (0 for the whole image), hashed, with its title and        */
            /* phase words -- and NO flags: the write sets them from the file   */
            /* as it is when it runs.                                           */
            objRequest.RQT_objTarget = objPicked.DCH_objTarget;
            objRequest.RQT_uiFlags = 0;
            objRequest.RQT_objPayload = new RWA_clsRawArgs(sImage, 0, objPicked.DCH_objTarget.TGT_sPath, 0,
                objPicked.DCH_ullRange, true, "Writing " + Path.GetFileName(sImage) + " to " + objPicked.DCH_objTarget.TGT_sPath,
                "Writing to the device", false);

            /* Asked and set: the write goes on. */
            return(new RSL_clsResult(SYS_OK, "", null));
        }

        /*--------------------------------------------------------------------*/
        /* Sys_RawSequence:                                                   */
        /*                                                                    */
        /* Read Device and Write Device: for one the menu posted, its device  */
        /* and file asked first (Sys_AskDeviceRead / Sys_AskDeviceWrite); the */
        /* captured pair checked, then for a write the image file inspected   */
        /* and the user asked, for a GPT read the device's GPT end read, and  */
        /* then the copy under its progress window.                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the command: the whole device as its target,    */
        /*                    the two ends, offsets, length, hash wish and    */
        /*                    captions as its payload (RWA_clsRawArgs), and   */
        /*                    its flags -- or, for one still to be asked, no  */
        /*                    target and an RDQ_clsDeviceReadQuestion (a      */
        /*                    read) or WDQ_clsDeviceWriteQuestion (a write),  */
        /*                    made into the copy in place once asked.         */
        /*     objExecution : the running command's services.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the copy's outcome (Nst_Raw: the hash and the  */
        /*                     sum's file with it); or the first refusal --   */
        /*                     the read's question's outcome when it was not  */
        /*                     answered with a choice, ENG_ERR_ARG for a      */
        /*                     malformed request, SYS_CANCELLED for a         */
        /*                     declined write, an inspection's failure as it  */
        /*                     came.                                          */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_RawSequence(RQT_clsRequest objRequest,
            EXC_clsExecution objExecution)
        {
            RWA_clsRawArgs objArgs;
            RQT_clsRequest objRaw;
            RSL_clsResult objResult;
            ANS_clsAnswer objAnswer;
            EIR_clsImageResult objImage;
            GPE_clsGptEnd objEnd;
            TGT_clsTarget objTarget;
            uint uiFlags;
            uint uiSectorBytes;
            bool bWrite;
            string sDevicePath;
            string sImageFile;
            string sTitle;
            string sCopyText;

            if (objRequest.RQT_enmKind == KND_enmKind.ReadDevice && objRequest.RQT_objPayload is RDQ_clsDeviceReadQuestion)
            {   /* Read Device as the menu asks it: the device, the range and   */
                /* the file asked first, and the request made into the read     */
                /* they describe (Sys_AskDeviceRead); what follows checks it    */
                /* as it checks any read.                                       */
                objResult = Sys_AskDeviceRead(objRequest, objExecution);
                if (objResult.RSL_iStatus != SYS_OK)
                {   /* Cancelled, refused or failed before any copy: that is   */
                    /* the outcome.                                            */
                    return(objResult);
                }
            }
            else if (objRequest.RQT_enmKind == KND_enmKind.WriteDevice && objRequest.RQT_objPayload is WDQ_clsDeviceWriteQuestion)
            {   /* Write Device as the menu asks it: the image file and the   */
                /* device asked first, and the request made into the write    */
                /* they describe (Sys_AskDeviceWrite).                        */
                objResult = Sys_AskDeviceWrite(objRequest, objExecution);
                if (objResult.RSL_iStatus != SYS_OK)
                {   /* Cancelled, refused or failed before any write: that is   */
                    /* the outcome.                                             */
                    return(objResult);
                }
            }

            /* The command's arguments and target, and its direction.  Both     */
            /* ends must be named: a missing one is never filled from the       */
            /* picker.  Only the request is checked here; the engine refuses    */
            /* the same object at both ends, the system disk, and a copy that   */
            /* does not fit.                                                    */
            objArgs = objRequest.RQT_objPayload as RWA_clsRawArgs;
            objTarget = objRequest.RQT_objTarget;
            bWrite = objRequest.RQT_enmKind == KND_enmKind.WriteDevice;
            if (objArgs == null || objTarget == null || String.IsNullOrEmpty(objArgs.RWA_sFrom) ||
                String.IsNullOrEmpty(objArgs.RWA_sTo))
            {   /* An end missing: refused, nothing inspected, asked or opened. */
                return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                    "Raw imaging needs its complete bound device and file pair.", null));
            }

            /* Which end is the device: a read copies from it into the image   */
            /* file, a write the other way.                                    */
            sDevicePath = objArgs.RWA_sFrom;
            sImageFile = objArgs.RWA_sTo;
            if (bWrite)
            {   /* A write: the roles swapped; the pair itself is unchanged. */
                sDevicePath = objArgs.RWA_sTo;
                sImageFile = objArgs.RWA_sFrom;
            }

            /* The pair must fit the command, each test refused before any    */
            /* engine work with words naming its own cause.  First, the two   */
            /* ends must differ.                                              */
            if (String.Equals(objArgs.RWA_sFrom, objArgs.RWA_sTo, StringComparison.OrdinalIgnoreCase))
            {   /* One path at both ends. */
                return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                    "The image file and the device are the same: " + objArgs.RWA_sFrom, null));
            }

            if (!String.Equals(objTarget.TGT_sPath, sDevicePath, StringComparison.OrdinalIgnoreCase) ||
                objTarget.TGT_ullOffset != 0 || objTarget.TGT_ullLength != 0)
            {   /* The device end is not the command's target, or the target    */
                /* is not the whole device (offset and length 0): the request   */
                /* was built wrong.                                             */
                return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                    "Raw imaging must name the whole device it was asked for (" + sDevicePath + ").", null));
            }

            if (objArgs.RWA_ullLength != 0 &&
                (objArgs.RWA_ullFrom > UInt64.MaxValue - objArgs.RWA_ullLength ||
                objArgs.RWA_ullTo > UInt64.MaxValue - objArgs.RWA_ullLength))
            {   /* An end plus the given length passes 2^64 (tested by   */
                /* subtraction, so the test itself cannot wrap).         */
                return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                    "The range to copy runs past the end of what can be addressed.", null));
            }

            /* The image end must be a Windows file: a raw device name there     */
            /* would turn a read into a device-to-device copy nobody was asked   */
            /* about.  The usual device spellings are refused here, before any   */
            /* engine work: \\.\..., \\?\GLOBALROOT\..., \\?\PhysicalDrive...    */
            /* and \\?\Volume{...}.  A spelling not on this list that still      */
            /* opens a device (\\?\D:, say) is refused by the copy step, which   */
            /* asks the engine what the path IS (Nst_Raw,                        */
            /* CommandNativeOperations.cs); this list only refuses the common    */
            /* ones sooner.                                                      */
            if (sImageFile.StartsWith(@"\\.\", StringComparison.Ordinal) ||
                sImageFile.StartsWith(@"\\?\GLOBALROOT\", StringComparison.OrdinalIgnoreCase) ||
                sImageFile.StartsWith(@"\\?\PhysicalDrive", StringComparison.OrdinalIgnoreCase) ||
                (sImageFile.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) &&
                    sImageFile.EndsWith("}")))
            {   /* A device name: refused before any Windows file is touched. */
                return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                    "The image endpoint must be a Windows file, not a raw device name.", null));
            }

            /* The flags each direction may carry: a write only the read-back    */
            /* and the decompression (both set again below from the file), a     */
            /* read only the .xz output.  The GPT box belongs only to a device   */
            /* READ from offset 0 with no length typed: its length is the GPT    */
            /* end read below, so a write, an offset, a length or an unknown     */
            /* disk size makes the request malformed.                            */
            uiFlags = objRequest.RQT_uiFlags;
            if ((bWrite && (uiFlags & ~(ENG_clsNative.ENG_RAW_VERIFY | ENG_clsNative.ENG_RAW_DECOMPRESS)) != 0) ||
                (!bWrite && (uiFlags & ~ENG_clsNative.ENG_RAW_XZ_OUT) != 0) ||
                (objArgs.RWA_bThroughGpt &&
                    (bWrite || objArgs.RWA_ullFrom != 0 || objArgs.RWA_ullTo != 0 ||
                    objArgs.RWA_ullLength != 0 || objTarget.TGT_ullDiskBytes == 0)))
            {   /* Refused: nothing inspected or opened. */
                return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                    "Raw flags or whole-device extent policy are invalid.", null));
            }

            /* The copy's request: the command's own, unless a write's flags   */
            /* or a GPT read's length must be set below.                       */
            objRaw = objRequest;
            if (bWrite)
            {   /* A WRITE.  STEP 1: the image file inspected now -- after any   */
                /* earlier queued work, and before the user is asked -- so the   */
                /* question and the flags rest on what the file holds now.       */
                objResult = Sys_Do(STP_enmStep.ImageInfo,
                    Sys_RawEnvelope(objRequest, KND_enmKind.ImageInfo,
                        new TGT_clsTarget(objArgs.RWA_sFrom, 0, 0, "", 0, 512), null, 0),
                    objExecution);
                if (objResult.RSL_iStatus != SYS_OK)
                {   /* Unreadable, or a damaged compressed file: stopped before   */
                    /* the question and before the device is opened.              */
                    return(objResult);
                }

                /* The inspection's report: the file's format and sizes. */
                objImage = objResult.RSL_objPayload as EIR_clsImageResult;
                if (objImage == null)
                {   /* A success with no report: nothing to ask or copy from. */
                    return(new RSL_clsResult(SYS_INTERNAL, "The image-file metadata report is missing.", null));
                }

                /* STEP 2: the flags, from the inspection -- never from the       */
                /* file's name or the picker's facts.  The read-back always:      */
                /* every device write is verified, apart from the Verify Writes   */
                /* option (an open session's writes).  Decompression when the     */
                /* file's first bytes say it is compressed.                       */
                uiFlags = ENG_clsNative.ENG_RAW_VERIFY;
                if (objImage.EIR_stInfo.IFI_uiKind != ENG_clsNative.ENG_IMAGE_RAW)
                {   /* Compressed: decoded as it is copied and as it is checked. */
                    uiFlags |= ENG_clsNative.ENG_RAW_DECOMPRESS;
                }

                /* STEP 3: the question, with the inspected facts, asked       */
                /* through the window that asked for the write -- before the   */
                /* device is opened for writing.                               */
                objAnswer = objExecution.EXC_Ask(QNK_enmQuestionKind.WriteDevice,
                    objRequest.RQT_lOrigin, new RWF_clsRawWriteFacts(objImage.EIR_stInfo));
                if (objAnswer == null || objAnswer.ANS_enmKind != ANK_enmAnswerKind.Yes)
                {   /* Anything but Yes -- No, Cancel, or the window gone --   */
                    /* writes nothing.                                         */
                    return(new RSL_clsResult(SYS_CANCELLED, "Device write declined.", null));
                }

                /* The copy's request: the command's ends and range, with these   */
                /* flags for the copy and its read-back.                          */
                objRaw = Sys_RawEnvelope(objRequest, objRequest.RQT_enmKind,
                    objTarget, objArgs, uiFlags);
            }
            else if (objArgs.RWA_bThroughGpt)
            {   /* The GPT box: read only from the start through the sector the    */
                /* primary GPT header names for its backup header, so the backup   */
                /* table comes with the image -- for a GPT written to a LARGER     */
                /* card, whose backup sits before the card's end.  The header is   */
                /* read here, on the worker, just before the copy, rather than     */
                /* taken from the picker's list: earlier queued work, or another   */
                /* card put in the reader, may have changed the disk since.        */
                /* The step reads only the target (Nst_ReadGptEnd); why its        */
                /* request carries ReadDeviceTable is not recorded.                */
                objResult = Sys_Do(STP_enmStep.ReadGptEnd,
                    Sys_RawEnvelope(objRequest, KND_enmKind.ReadDeviceTable, objTarget, null, 0),
                    objExecution);
                if (objResult.RSL_iStatus != SYS_OK)
                {   /* The disk could not be read: stopped before the image file is opened. */
                    return(objResult);
                }

                /* The step's finding: whether there is a GPT, and its end. */
                objEnd = objResult.RSL_objPayload as GPE_clsGptEnd;
                if (objEnd == null)
                {   /* A success with no finding cannot size the read. */
                    return(new RSL_clsResult(SYS_INTERNAL, "The GPT report is missing.", null));
                }

                if (!objEnd.GPE_bGpt)
                {   /* The picker saw a GPT on this disk and it is not there   */
                    /* now: the read is refused rather than taking the whole   */
                    /* device, which is not what the box asked for.            */
                    return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                        objTarget.TGT_sPath + " no longer holds a GUID partition table, so it cannot be read " +
                        "through its backup table.\nRead the whole device instead.", null));
                }

                if (objEnd.GPE_ullEndBytes > objTarget.TGT_ullDiskBytes)
                {   /* The table was made for a LARGER disk: its backup lies     */
                    /* past this device's end and cannot be read, so the image   */
                    /* would not hold it -- refused, as the box promises.        */
                    return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                        "The GPT on " + objTarget.TGT_sPath + " puts its backup table at sector " +
                        objEnd.GPE_ullAlternateLba.ToString() + ", past the end of the device, so it cannot be read " +
                        "through its backup table.\nRead the whole device instead.", null));
                }

                /* The copy's length is exactly the GPT end; every other value   */
                /* -- the ends, the hash and the captions -- is the request's.   */
                objRaw = Sys_RawEnvelope(objRequest, objRequest.RQT_enmKind, objTarget,
                    new RWA_clsRawArgs(objArgs.RWA_sFrom, 0, objArgs.RWA_sTo, 0,
                        objEnd.GPE_ullEndBytes, objArgs.RWA_bHash, objArgs.RWA_sTitle,
                        objArgs.RWA_sCopyText), uiFlags);
            }

            /* The progress window's title and its copy phase's words: the   */
            /* command's when given, else the direction's own.               */
            sTitle = objArgs.RWA_sTitle;
            sCopyText = objArgs.RWA_sCopyText;
            if (sTitle.Length == 0)
            {   /* No title given: the read's, or for a write its own. */
                sTitle = "Reading device to image";
                if (bWrite)
                {   /* A write. */
                    sTitle = "Writing image to device";
                }
            }

            if (sCopyText.Length == 0)
            {   /* No phase words given: the read's, or for a write its own    */
                /* (the read-back phase is labelled by the engine's events).   */
                sCopyText = "Reading the device";
                if (bWrite)
                {   /* A write. */
                    sCopyText = "Writing to the device";
                }
            }

            /* The sector size the window counts in -- display only; 512 when   */
            /* the target has none.  The engine works in the device's own.      */
            uiSectorBytes = objTarget.TGT_uiSectorBytes;
            if (uiSectorBytes == 0)
            {   /* None captured: 512. */
                uiSectorBytes = 512;
            }

            /* The progress window, opened before the copy; showing it does   */
            /* not wait for the window, and the copy does not wait for it     */
            /* either.                                                        */
            SYS_Show(new VWM_clsView(VWK_enmViewKind.ProgressOpen, objRequest,
                new PGV_clsProgressView(false, sTitle, "", uiSectorBytes, sCopyText)));
            try
            {   /* The copy (Nst_Raw): its read-back, the hash and the sum's   */
                /* file are the step's; its outcome is the command's.          */
                return(Sys_Do(STP_enmStep.CopyRaw, objRaw, objExecution));
            }
            finally
            {   /* The window closed however the copy ended, an exception   */
                /* included.                                                */
                SYS_Show(new VWM_clsView(VWK_enmViewKind.ProgressClose, objRequest, null));
            }
        }

        /*--------------------------------------------------------------------*/
        /* Sys_PartitionCopy:                                                 */
        /*                                                                    */
        /* One raw copy of a partition command, under its own progress        */
        /* window: the window opened with this phase's wording, the CopyRaw   */
        /* step run on an internal envelope of the same run (so a Cancel or   */
        /* an Exit reaches it), and the window closed whatever happens.  A    */
        /* write that then zeros the rest of its partition runs this twice,   */
        /* each phase in words of its own.                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the partition command (read, write or erase).   */
        /*     objExecution : the run's interruption and progress services.   */
        /*     enmKind      : the kind the envelope carries (the copy step    */
        /*                    adds no flag of its own for these kinds).       */
        /*     objRaw       : the copy's ends, offsets and length.            */
        /*     uiFlags      : ENG_RAW_* for this copy.                        */
        /*     sTitle       : the progress window's title.                    */
        /*     sCopyText    : the words for the copy phase.                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the copy's outcome (its status and detail).    */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_PartitionCopy(RQT_clsRequest objRequest, EXC_clsExecution objExecution,
            KND_enmKind enmKind, RWA_clsRawArgs objRaw, uint uiFlags, string sTitle, string sCopyText)
        {
            uint uiSector;

            /* The sector the counts are shown in; 512 when none was captured. */
            uiSector = objRequest.RQT_objTarget.TGT_uiSectorBytes;
            if (uiSector == 0)
            {   /* Display geometry only; the engine holds the device to its own. */
                uiSector = 512;
            }

            /* The window, opened before the copy starts; showing it does not   */
            /* wait for the window.                                             */
            SYS_Show(new VWM_clsView(VWK_enmViewKind.ProgressOpen, objRequest,
                new PGV_clsProgressView(false, sTitle, "", uiSector, sCopyText)));
            try
            {   /* The copy itself, on the same run's checkpoint. */
                return(Sys_Do(STP_enmStep.CopyRaw,
                    Sys_RawEnvelope(objRequest, enmKind, objRequest.RQT_objTarget, objRaw, uiFlags),
                    objExecution));
            }
            finally
            {   /* A failure or a managed exception still ends this window. */
                SYS_Show(new VWM_clsView(VWK_enmViewKind.ProgressClose, objRequest, null));
            }
        }

        /*---------------------------------------------------------------------*/
        /* Sys_PartitionRawSequence:                                           */
        /*                                                                     */
        /* One partition's Read, Write or Erase, from the partition window --  */
        /* raw copies bounded to the partition's own range, the way Read and   */
        /* Write Device copy a whole disk.  What each one does:                */
        /*                                                                     */
        /* READ copies the partition into a new file and asks nothing (the     */
        /* save dialog has already asked before replacing a file).  The file   */
        /* is a plain image of the partition's bytes, tied to nothing, so it   */
        /* can later be written into a partition of ANOTHER image or device.   */
        /*                                                                     */
        /* WRITE puts an image file -- plain or compressed -- into the         */
        /* partition, in place and read back, after a plain confirmation       */
        /* naming both.  A file larger than the partition is written only up   */
        /* to the partition's end, and a smaller one is followed by zeros,     */
        /* each only when the answer says so; a file whose image size cannot   */
        /* be told before reading it all is refused.  The steps are below.     */
        /*                                                                     */
        /* ERASE asks a plain confirmation and writes zeros over the range,    */
        /* in place, without a read-back (as a format's zero-fill).            */
        /*                                                                     */
        /* After a Write or an Erase -- finished, failed or cancelled, since   */
        /* bytes may already be written -- the layout is read again so the     */
        /* window shows what the disk now holds.                               */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     objRequest   : the captured command: the partition's range as   */
        /*                    its target, the file and wording as its payload. */
        /*     objExecution : the run's question, interruption and progress    */
        /*                    services.                                        */
        /*                                                                     */
        /* Returns:                                                            */
        /*     RSL_clsResult : a Read's copy outcome; for a Write or Erase     */
        /*                     the outcome paired with the fresh layout        */
        /*                     (RPO_clsPartitionOutcome), as the window's      */
        /*                     other changes return it.                        */
        /*---------------------------------------------------------------------*/
        private RSL_clsResult Sys_PartitionRawSequence(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            PRA_clsPartitionRawArgs objArgs;
            FMI_clsFormatInput objAsk;
            TGT_clsTarget objTarget;
            RSL_clsResult objResult;
            RSL_clsResult objZero;
            RSL_clsResult objLayout;
            EIR_clsImageResult objImage;
            ANS_clsAnswer objAnswer;
            ulong ullImage;
            ulong ullCopy;
            ulong ullZeroAt;
            ulong ullPartEnd;
            uint uiFlags;
            uint uiSector;
            bool bDevice;
            bool bZeroRest;
            string sFile;
            string sPlace;

            /* A Read or Write whose press carried the range's words, not a    */
            /* file: the file is asked HERE, as a question of this sequence    */
            /* (PartitionFile, the Windows file dialog over the partition      */
            /* window), so the copy is one operation from the press to its     */
            /* end and nothing is posted in between for anything to come       */
            /* between.  The request then becomes the copy it describes: its   */
            /* arguments, with the file, replace the words in place -- the     */
            /* request is this worker's while it runs, and the steps and the   */
            /* window's result handler (MainCommand.cs) read them from it.     */
            objAsk = objRequest.RQT_objPayload as FMI_clsFormatInput;
            if (objAsk != null && (objRequest.RQT_enmKind == KND_enmKind.ReadPartition ||
                objRequest.RQT_enmKind == KND_enmKind.WritePartition))
            {   /* The file still to choose: asked through the partition window. */
                objAnswer = objExecution.EXC_Ask(QNK_enmQuestionKind.PartitionFile, Sys_lModeWindow, objAsk);
                if (objAnswer == null || objAnswer.ANS_enmKind != ANK_enmAnswerKind.Yes || objAnswer.ANS_sText.Length == 0)
                {   /* Cancelled, interrupted, or no file named: nothing read   */
                    /* or written.                                              */
                    return(new RSL_clsResult(SYS_CANCELLED, "No file was chosen.", null));
                }

                /* The chosen file and the captured words, as the copy's arguments. */
                objRequest.RQT_objPayload = new PRA_clsPartitionRawArgs(objAnswer.ANS_sText, objAsk.FMI_sPlace, objAsk.FMI_sTarget);
            }

            /* The captured partition and wording; a malformed command does   */
            /* nothing (the gate has already held the range to the disk).     */
            objArgs = objRequest.RQT_objPayload as PRA_clsPartitionRawArgs;
            objTarget = objRequest.RQT_objTarget;
            if (objArgs == null || objTarget == null || objTarget.TGT_ullLength == 0)
            {   /* No range or no wording: nothing to read, write or erase. */
                return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                    "A partition command needs its captured partition.", null));
            }

            /* The chosen file (empty for an Erase), the partition's terse      */
            /* name for every message and window title below, and whether       */
            /* the disk is a real device -- which decides where the zeros       */
            /* after a smaller file begin (STEP 5).  A device's target always   */
            /* carries its description (CommandInputs.cs), a file's none.       */
            sFile   = objArgs.PRA_sFile;
            sPlace  = objArgs.PRA_sPlace;
            bDevice = objTarget.TGT_sDeviceText.Length != 0;

            /* The file end of a Read or Write must be a Windows FILE: a raw   */
            /* device name here would turn a partition command into an         */
            /* unasked device-to-device copy.  (The engine still holds both    */
            /* ends to their identities and the disk to its size.)  The same   */
            /* spellings are refused as in Sys_RawSequence, and any other      */
            /* path that opens as a device by the copy step (Nst_Raw).         */
            if (objRequest.RQT_enmKind != KND_enmKind.ErasePartition &&
                (sFile.Length == 0 || sFile.StartsWith(@"\\.\", StringComparison.Ordinal) ||
                sFile.StartsWith(@"\\?\GLOBALROOT\", StringComparison.OrdinalIgnoreCase) ||
                sFile.StartsWith(@"\\?\PhysicalDrive", StringComparison.OrdinalIgnoreCase) ||
                (sFile.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) && sFile.EndsWith("}"))))
            {   /* Refused before anything is opened. */
                return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                    "The file must be a Windows file, not a raw device name.", null));
            }

            if (objRequest.RQT_enmKind == KND_enmKind.ReadPartition)
            {   /* READ: the partition's bytes into a new file, asking nothing.   */
                /* Nothing on the disk changes, so no layout re-read follows.     */
                return(Sys_PartitionCopy(objRequest, objExecution, KND_enmKind.ReadPartition,
                    new RWA_clsRawArgs(objTarget.TGT_sPath, objTarget.TGT_ullOffset, sFile, 0,
                        objTarget.TGT_ullLength, false),
                    0, "Reading " + sPlace + " to a file", "Reading " + sPlace));
            }

            /* A Write or an Erase.  No zeros after the file unless a Write's   */
            /* question asks for them (set only in STEP 3).                     */
            bZeroRest = false;
            if (objRequest.RQT_enmKind == KND_enmKind.WritePartition)
            {   /* WRITE, STEP 1: what the file holds -- read fresh now, after   */
                /* any earlier queued work, never from the file's name.          */
                objResult = Sys_Do(STP_enmStep.ImageInfo,
                    Sys_RawEnvelope(objRequest, KND_enmKind.ImageInfo,
                        new TGT_clsTarget(sFile, 0, 0, "", 0, 512), null, 0),
                    objExecution);
                if (objResult.RSL_iStatus != SYS_OK)
                {   /* Unreadable, or a damaged compressed file: nothing written. */
                    return(objResult);
                }

                /* The step's report: the file's format and the image's size,   */
                /* the two facts every later step depends on.                   */
                objImage = objResult.RSL_objPayload as EIR_clsImageResult;
                if (objImage == null)
                {   /* A success with no report cannot size the write. */
                    return(new RSL_clsResult(SYS_INTERNAL, "The image file's report is missing.", null));
                }

                /* STEP 2: the size, which the question and the length need. */
                ullImage = objImage.EIR_stInfo.IFI_ullImageBytes;
                if (ullImage == ENG_clsNative.ENG_SIZE_UNKNOWN)
                {   /* A .gz (or a .zst that does not record its size): its     */
                    /* length is known only once it is read to its end, so it   */
                    /* cannot be measured against the partition up front.       */
                    return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                        "The size of the image in " + sFile + " cannot be told without reading all of it, " +
                        "so it cannot be fitted to the partition.  Decompress it first and write the plain image.", null));
                }

                if (ullImage == 0)
                {   /* Nothing to write. */
                    return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG, sFile + " is empty.", null));
                }

                /* STEP 3: the question, with the inspected facts. */
                objAnswer = objExecution.EXC_Ask(QNK_enmQuestionKind.WritePartition, objRequest.RQT_lOrigin,
                    new PWF_clsPartitionWriteFacts(sFile, ullImage, objImage.EIR_stInfo.IFI_uiKind,
                        sPlace, objTarget.TGT_ullLength, bDevice));
                if (objAnswer == null)
                {   /* The window went away: nothing is written. */
                    return(new RSL_clsResult(SYS_CANCELLED, "Partition write declined.", null));
                }

                if (ullImage < objTarget.TGT_ullLength)
                {   /* SMALLER: Yes zeros the rest after the file, No leaves it,   */
                    /* anything else writes nothing.                               */
                    if (objAnswer.ANS_enmKind == ANK_enmAnswerKind.Yes)
                    {   /* The file, then zeros to the partition's end. */
                        bZeroRest = true;
                    }
                    else if (objAnswer.ANS_enmKind != ANK_enmAnswerKind.No)
                    {   /* Cancel. */
                        return(new RSL_clsResult(SYS_CANCELLED, "Partition write declined.", null));
                    }
                }
                else if (objAnswer.ANS_enmKind != ANK_enmAnswerKind.Yes)
                {   /* The same size or LARGER: only Yes writes. */
                    return(new RSL_clsResult(SYS_CANCELLED, "Partition write declined.", null));
                }

                /* STEP 4: the write -- in place, read back, decompressed when     */
                /* the file is compressed -- of the image or, when it is larger,   */
                /* exactly the part that fits, stopping at the partition's end.    */
                ullCopy = ullImage;
                if (ullCopy > objTarget.TGT_ullLength)
                {   /* Larger: only what fits. */
                    ullCopy = objTarget.TGT_ullLength;
                }

                /* The copy's flags: IN_PLACE, so an image file is written     */
                /* where it stands and never cut or extended; VERIFY, so the   */
                /* partition is read back and compared, as every partition     */
                /* write is.                                                   */
                uiFlags = ENG_clsNative.ENG_RAW_IN_PLACE | ENG_clsNative.ENG_RAW_VERIFY;
                if (objImage.EIR_stInfo.IFI_uiKind != ENG_clsNative.ENG_IMAGE_RAW)
                {   /* A compressed file is decoded as it is copied and checked. */
                    uiFlags |= ENG_clsNative.ENG_RAW_DECOMPRESS;
                }

                /* The write itself, under its own progress window: the file   */
                /* from its first byte into the partition from its first.      */
                objResult = Sys_PartitionCopy(objRequest, objExecution, KND_enmKind.WritePartition,
                    new RWA_clsRawArgs(sFile, 0, objTarget.TGT_sPath, objTarget.TGT_ullOffset, ullCopy, false),
                    uiFlags, "Writing a file into " + sPlace, "Writing " + sPlace);

                if (objResult.RSL_iStatus == SYS_OK && bZeroRest)
                {   /* STEP 5, only when asked: zeros from the file's end to the   */
                    /* partition's.  On a DEVICE the copy padded its last sector   */
                    /* with zeros already, so the zeros start at the next whole    */
                    /* sector; in an image FILE nothing was padded, so they        */
                    /* start right after the file's last byte.                     */
                    uiSector = objTarget.TGT_uiSectorBytes;
                    if (uiSector == 0)
                    {   /* The usual sector when none was captured. */
                        uiSector = 512;
                    }

                    /* Where the zeros start: right after the file's last byte,   */
                    /* moved on to the next whole sector on a device.             */
                    ullZeroAt = objTarget.TGT_ullOffset + ullCopy;
                    if (bDevice && (ullCopy % uiSector) != 0)
                    {   /* Past the padded sector (the copy is inside the   */
                        /* partition, so this cannot pass its end by more   */
                        /* than the padding).                               */
                        ullZeroAt += uiSector - (ullCopy % uiSector);
                    }

                    /* Where they stop: the partition's end, never past it.  A   */
                    /* file that ended within its last sector leaves nothing.    */
                    ullPartEnd = objTarget.TGT_ullOffset + objTarget.TGT_ullLength;
                    if (ullZeroAt < ullPartEnd)
                    {   /* Something is left to zero. */
                        objZero = Sys_PartitionCopy(objRequest, objExecution, KND_enmKind.ErasePartition,
                            new RWA_clsRawArgs("", 0, objTarget.TGT_sPath, ullZeroAt, ullPartEnd - ullZeroAt, false),
                            ENG_clsNative.ENG_RAW_ZERO_SOURCE | ENG_clsNative.ENG_RAW_IN_PLACE,
                            "Zeroing the rest of " + sPlace, "Writing zeros after the file in " + sPlace);
                        if (objZero.RSL_iStatus != SYS_OK)
                        {   /* The file is in; the zeros failed or were cancelled. */
                            objResult = objZero;
                        }
                    }
                }
            }
            else
            {   /* ERASE: the plain confirmation, No the default. */
                if (!Sys_Yes(QNK_enmQuestionKind.ErasePartition, objRequest, Sys_lModeWindow))
                {   /* Declined: nothing is written. */
                    return(new RSL_clsResult(SYS_CANCELLED, "Partition erase declined.", null));
                }

                /* Zeros over the whole range, in place. */
                objResult = Sys_PartitionCopy(objRequest, objExecution, KND_enmKind.ErasePartition,
                    new RWA_clsRawArgs("", 0, objTarget.TGT_sPath, objTarget.TGT_ullOffset, objTarget.TGT_ullLength, false),
                    ENG_clsNative.ENG_RAW_ZERO_SOURCE | ENG_clsNative.ENG_RAW_IN_PLACE,
                    "Erasing " + sPlace, "Writing zeros over " + sPlace);
            }

            /* The disk changed (or may have, if the copy stopped part-way):   */
            /* read its layout again so the window shows what it now holds.    */
            /* After a success the read goes through the run's checkpoint;     */
            /* after a failure or cancel it is a read only and runs past it,   */
            /* as Sys_PartitionOperation's does.                               */
            try
            {   /* The whole owning disk, once. */
                if (objResult.RSL_iStatus == SYS_OK)
                {   /* The usual path. */
                    objLayout = Sys_Do(STP_enmStep.ReadLayout, Sys_RequestForTarget(objRequest, Sys_objTarget), objExecution);
                }
                else
                {   /* The checkpoint would refuse it after a cancel. */
                    objLayout = Sys_objSteps.STB_Run(STP_enmStep.ReadLayout, Sys_RequestForTarget(objRequest, Sys_objTarget), objExecution);
                }
            }
            catch (Exception exLayout)
            {   /* A managed read failure keeps the command's own report.  The   */
                /* whole exception, with its stack, goes to the debug log; the   */
                /* layout's detail, which the partition window may show, is      */
                /* only its message.                                             */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("partition layout re-read", exLayout.ToString());
                }

                /* The layout's failed result, its detail the exception's message. */
                objLayout = new RSL_clsResult(SYS_INTERNAL, exLayout.Message, null);
            }

            if (objResult.RSL_iStatus != SYS_OK)
            {   /* The failed or cancelled command stays the outcome; the fresh   */
                /* layout rides with it for the window's painter.                 */
                return(new RSL_clsResult(objResult.RSL_iStatus, objResult.RSL_sDetail,
                    new RPO_clsPartitionOutcome(objResult, objLayout, null, null)));
            }

            /* Success: the read's status outside, both reports inside. */
            return(new RSL_clsResult(objLayout.RSL_iStatus, objLayout.RSL_sDetail,
                new RPO_clsPartitionOutcome(objResult, objLayout, null, null)));
        }
    }
}
