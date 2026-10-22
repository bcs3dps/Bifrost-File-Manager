/*----------------------------------------------------------------------------*/
/* CommandDeviceOpening.cs                                                    */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* Opening a filesystem on a real device (Open Device), or checking one       */
/* (Check Filesystem on a device), as one command sequence on the worker:     */
/* ask which device, when the menu has not chosen one; read the device's      */
/* partition table, mark the rows that do not fit the device as invalid       */
/* (refusing the device only when none is valid), ask which partition when    */
/* there is a table -- every row listed, an invalid one                       */
/* refused if chosen -- then hand the chosen range to the ordinary            */
/* image-open sequence, which asks its own questions (write consent, unsaved  */
/* editors), closes what is open, and opens or checks.  Nothing open is       */
/* touched until the table has been read and a partition chosen.  A device    */
/* whose table has no valid row is still the partition window's to repair:    */
/* that window reads the layout through a path of its own.                    */
/*----------------------------------------------------------------------------*/

/* The framework: the list of the table's rows handed to the question   */
/* (Collections.Generic).                                               */
using System.Collections.Generic;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* DOA_clsDeviceOpenArgs -- what the device open was asked for, captured  */
    /* when the request was made: open or check, whether the device is still  */
    /* to be asked, and the device's name for the partition question.  The    */
    /* device's path and size travel in the request's target, not here --     */
    /* documentation table.                                                   */
    /*------------------------------------------------------------------------*/
    internal sealed class DOA_clsDeviceOpenArgs
    {
        internal bool DOA_bCheck;   // true: a report-only check of the device; false: open it (read-only browsing)
        internal bool DOA_bAsk;     // true: no device chosen yet -- the sequence lists the disks and asks it (DeviceChoice), then sets the target and the name
        internal string DOA_sName;  // the device's model text for the question's caption; "" when it has none (the path is shown instead)

        /*--------------------------------------------------------------------*/
        /* DOA_clsDeviceOpenArgs:                                             */
        /*                                                                    */
        /* The captured purpose and caption of one device open.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bCheck : true for a check, false for an open.                  */
        /*     bAsk   : true when the device is still to be asked.            */
        /*     sName  : the device's model text, or null for none.            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the arguments.                                 */
        /*--------------------------------------------------------------------*/
        internal DOA_clsDeviceOpenArgs(bool bCheck, bool bAsk, string sName)
        {
            /* The purpose, whether to ask, and the caption -- "" for none,   */
            /* so the sequence shows the device's own path rather than        */
            /* anything else (the image open now, say).                       */
            DOA_bCheck = bCheck;
            DOA_bAsk = bAsk;
            DOA_sName = "";
            if (sName != null)
            {   /* A model text was captured: kept as it is. */
                DOA_sName = sName;
            }
        }
    }

    internal sealed partial class SYS_clsSystem
    {
        /*--------------------------------------------------------------------*/
        /* Sys_DeviceOpenSequence:                                            */
        /*                                                                    */
        /* The whole device open or check: the table read and checked, the    */
        /* partition asked for, then the image-open sequence run on the       */
        /* chosen range as part of this same command (no second request is    */
        /* posted, so nothing can come between the steps).                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the request: the whole device as its target     */
        /*                    (path, size, sector), and DOA_clsDeviceOpenArgs */
        /*                    as its payload.                                 */
        /*     objExecution : the running command, for its steps and its      */
        /*                    questions.                                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : when the device is asked, the disk list's      */
        /*                     failure, SYS_INTERNAL for a list with no       */
        /*                     facts, SYS_REFUSED for no disk or a choice     */
        /*                     that is not a whole physical disk, and         */
        /*                     SYS_CANCELLED for no choice; then the table    */
        /*                     read's failure, SYS_INTERNAL for a             */
        /*                     read with no rows, SYS_REFUSED for a table     */
        /*                     none of whose rows fits the device or for an   */
        /*                     invalid row chosen, SYS_CANCELLED when the     */
        /*                     question is declined, or the image-open        */
        /*                     sequence's result.                             */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_DeviceOpenSequence(RQT_clsRequest objRequest,
            EXC_clsExecution objExecution)
        {
            DOA_clsDeviceOpenArgs objArgs;
            RSL_clsResult objResult;
            ETB_clsTableResult objTable;
            FCD_clsFileChoiceData objFacts;
            TGT_clsTarget objSelected;
            ANS_clsAnswer objAnswer;
            OFC_clsOffsetChoice objChoice;
            PRT_clsPartition objPart;
            List<PRT_clsPartition> lstRows;
            ulong ullDiskBytes;
            string sName;
            int iValid;
            int i;
            EDV_clsDeviceResult objListed;
            DCH_clsDeviceChoice objPicked;
            string sCaption;

            /* STEP 0: the device, when it is still to be asked -- as a        */
            /* question of this sequence, so the choice and the open are one   */
            /* operation and nothing is posted in between.                     */
            objArgs = (DOA_clsDeviceOpenArgs)objRequest.RQT_objPayload;
            if (objArgs.DOA_bAsk)
            {   /* Open Device or Check a device as the menu asks it: the   */
                /* disks listed now, on this worker, then the picker.       */
                objResult = Sys_Do(STP_enmStep.ListDevices, objRequest, objExecution);
                if (objResult.RSL_iStatus != SYS_OK)
                {   /* The list could not be read (or the command was   */
                    /* stopped): its outcome is the result.             */
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

                /* The picker over the main window, captioned for the          */
                /* purpose, with no range; the choice describes the device     */
                /* as a browse does (an unnamed one by its physical number).   */
                sCaption = "Open a device";
                if (objArgs.DOA_bCheck)
                {   /* Check Filesystem's caption. */
                    sCaption = "Check the filesystem on a device";
                }

                objAnswer = objExecution.EXC_Ask(QNK_enmQuestionKind.DeviceChoice, 0,
                    new DCQ_clsDeviceQuestion(objListed.EDV_lstDisks, sCaption, DVF_clsDeviceForm.DVF_RANGE_NONE, 0, true));
                objPicked = null;
                if (objAnswer != null && objAnswer.ANS_enmKind == ANK_enmAnswerKind.Yes)
                {   /* Chosen (a guard tests the record's type). */
                    objPicked = objAnswer.ANS_objValue as DCH_clsDeviceChoice;
                }

                if (objPicked == null)
                {   /* Cancelled: nothing read, opened or closed. */
                    return(new RSL_clsResult(SYS_CANCELLED, "No device was chosen.", null));
                }

                /* The request becomes the open it describes, in place: the   */
                /* whole device as its target and its name as the caption,    */
                /* no longer asking.  Every step below reads them from it.    */
                objArgs = new DOA_clsDeviceOpenArgs(objArgs.DOA_bCheck, false, objPicked.DCH_sName);
                objRequest.RQT_objPayload = objArgs;
                objRequest.RQT_objTarget = objPicked.DCH_objTarget;
                if (objRequest.RQT_objTarget.TGT_ullOffset != 0 || objRequest.RQT_objTarget.TGT_ullLength != 0 ||
                    objRequest.RQT_objTarget.TGT_ullDiskBytes == 0 ||
                    !objRequest.RQT_objTarget.TGT_sPath.StartsWith(DEV_clsDisk.DEV_PHYSICAL_PREFIX, System.StringComparison.OrdinalIgnoreCase))
                {   /* The gate's whole-physical-disk test, made here for a   */
                    /* device it could not see: a range, no size, or not a    */
                    /* physical-drive path is refused (a guard: the picker    */
                    /* lists whole disks only).                               */
                    return(new RSL_clsResult(SYS_REFUSED, "No complete physical-device selection was supplied.", null));
                }
            }

            /* STEP 1: the device's partition table, read before anything     */
            /* open is touched.  A read that fails is the result: it is not   */
            /* taken for "no table", which would go on to mount the whole     */
            /* device as if it held one filesystem.                           */
            objResult = Sys_Do(STP_enmStep.ReadDeviceTable, objRequest, objExecution);
            if (objResult.RSL_iStatus != SYS_OK)
            {   /* The table could not be read: reported, and the open   */
                /* session and editors are left as they are.             */
                return(objResult);
            }

            /* The rows the read produced.  A successful read with no row   */
            /* list at all is a fault in the step, not an empty table.      */
            objTable = objResult.RSL_objPayload as ETB_clsTableResult;
            if (objTable == null || objTable.ETB_lstParts == null)
            {   /* No rows to work from: refused as an internal fault. */
                return(new RSL_clsResult(SYS_INTERNAL, "Device inspection returned no partition facts.", null));
            }

            /* STEP 2: every row of the table is listed, and each that does     */
            /* not lie inside the device as it was measured is MARKED INVALID   */
            /* with its reason (PRT_sInvalid) -- tested before the question,    */
            /* so the dialog shows it greyed and refuses it if chosen.  A row   */
            /* that does not fit (a table written for a card a few sectors      */
            /* larger, or a damaged last entry) is no reason to refuse the      */
            /* whole device: the partitions that do fit, often the very one     */
            /* wanted, stay open to the user, and the bad one stays in view     */
            /* so the table reads as it is.  The engine checks the chosen       */
            /* range again when it opens, since a card can be swapped between   */
            /* the two.                                                         */
            ullDiskBytes = objRequest.RQT_objTarget.TGT_ullDiskBytes;
            lstRows = new List<PRT_clsPartition>();
            iValid = 0;
            for (i = 0; i < objTable.ETB_lstParts.Count; i++)
            {   /* One table row per iteration. */
                objPart = objTable.ETB_lstParts[i];
                if (objPart == null)
                {   /* No row at all: a guard -- the table reader adds only   */
                    /* rows it made (PRF_ReadTable) -- and nothing to show,   */
                    /* so not listed.                                         */
                    continue;
                }

                /* Valid until a test below says otherwise.  Not inside the   */
                /* device is tested as the room left after the offset, so a   */
                /* huge length cannot wrap the sum; each reason is a phrase   */
                /* the dialog puts after "it".                                */
                objPart.PRT_sInvalid = null;
                if (objPart.PRT_ullLength == 0)
                {   /* An entry with no length: nothing there to open. */
                    objPart.PRT_sInvalid = "has no length";
                }
                else if (objPart.PRT_ullOffset >= ullDiskBytes)
                {   /* It starts at or past the device's end. */
                    objPart.PRT_sInvalid = "starts past the end of the device (" + ullDiskBytes.ToString("N0") + " bytes)";
                }
                else if (objPart.PRT_ullLength > ullDiskBytes - objPart.PRT_ullOffset)
                {   /* It starts inside the device and runs past its end. */
                    objPart.PRT_sInvalid = "runs past the end of the device (" + ullDiskBytes.ToString("N0") + " bytes)";
                }
                else
                {   /* Inside the device: one that can be opened. */
                    iValid++;
                }

                /* Listed either way, in table order. */
                lstRows.Add(objPart);
            }

            if (objTable.ETB_lstParts.Count != 0 && iValid == 0)
            {   /* A table, and not one of its rows fits: the device is          */
                /* refused, before any question and with nothing closed.         */
                /* Going on would offer nothing that could be opened, and a      */
                /* typed offset would open the WHOLE device past a table that    */
                /* says it holds partitions.  The partition window is where      */
                /* such a table is repaired -- erased, or a new one made -- so   */
                /* the refusal names it, on a line of its own.                   */
                return(new RSL_clsResult(SYS_REFUSED,
                    "No partition in the device's table is a non-empty range inside the device as measured (" +
                    ullDiskBytes.ToString("N0") + " bytes).\n" +
                    "To repair the table, use File > Partition/Format > Device.", null));
            }

            /* The question's caption: the device's model text, or its own   */
            /* path when it has none.                                        */
            sName = objArgs.DOA_sName;
            if (sName.Length == 0)
            {   /* No model text: the path names the device unambiguously. */
                sName = objRequest.RQT_objTarget.TGT_sPath;
            }

            /* STEP 3: the partition.  The choice data is the one an image     */
            /* file's partition question uses, built over every row, the       */
            /* invalid ones marked, with the reader's own account line; it     */
            /* starts at the whole device (offset 0).  The answer is only a    */
            /* start offset: the length comes from the matching row, or runs   */
            /* to the device's end for an offset typed in.                     */
            objFacts = new FCD_clsFileChoiceData(objRequest.RQT_objTarget,
                lstRows, objTable.ETB_sFound, "", true, sName);
            objSelected = objFacts.FCD_SelectedTarget(0);
            if (objFacts.FCD_lstParts.Count != 0)
            {   /* A table with partitions: the user is asked which. */
                objAnswer = SYS_Ask(QNK_enmQuestionKind.ImagePartition, objRequest,
                    objRequest.RQT_lOrigin, objFacts);
                objChoice = null;
                if (objAnswer != null)
                {   /* Answered: the offset it carries, if any. */
                    objChoice = objAnswer.ANS_objOffset;
                }

                if (objAnswer == null || objAnswer.ANS_enmKind != ANK_enmAnswerKind.SelectOffset ||
                    objChoice == null || objChoice.OFC_ullOffset % 512 != 0 ||
                    objChoice.OFC_ullOffset >= objRequest.RQT_objTarget.TGT_ullDiskBytes)
                {   /* Declined, or an offset that is not a 512-byte multiple    */
                    /* inside the device: cancelled, with the open session and   */
                    /* editors untouched.                                        */
                    return(new RSL_clsResult(SYS_CANCELLED,
                        "Device partition selection cancelled or invalid.", null));
                }

                /* The listed row the answer names, if any, refused when it is   */
                /* marked invalid; a typed offset that names none goes on.       */
                for (i = 0; i < lstRows.Count; i++)
                {   /* One listed row per iteration, the first that starts at   */
                    /* the answer's offset being the one FCD_SelectedTarget     */
                    /* would take.  The dialog refuses an invalid row itself;   */
                    /* the worker checks the answer again, as it checks every   */
                    /* answer before using it.                                  */
                    if (lstRows[i].PRT_ullOffset == objChoice.OFC_ullOffset)
                    {   /* The row chosen. */
                        if (lstRows[i].PRT_sInvalid != null)
                        {   /* Marked invalid: refused with its reason, nothing   */
                            /* closed or opened.                                  */
                            return(new RSL_clsResult(SYS_REFUSED, "Partition " + lstRows[i].PRT_sNumber +
                                " cannot be opened: it " + lstRows[i].PRT_sInvalid + ".", null));
                        }

                        /* A valid row: its range below. */
                        break;
                    }
                }

                /* The chosen range: the row's own length for a listed start,   */
                /* the rest of the device for a typed one.                      */
                objSelected = objFacts.FCD_SelectedTarget(objChoice.OFC_ullOffset);
            }

            /* STEP 4: the image-open sequence on the chosen range, run here   */
            /* as part of this command -- write consent, the editors' save     */
            /* question, the close of what is open, then the open or check.    */
            /* A device with no table goes in as the whole device, with no     */
            /* offset retry as an image file gets.                             */
            return(Sys_Sequence(Sys_ImageSelectionRequest(objRequest,
                objSelected, objArgs.DOA_bCheck), objExecution));
        }
    }
}
