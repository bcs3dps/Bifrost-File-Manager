/*----------------------------------------------------------------------------*/
/* CommandNativeSteps.cs                                                      */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The command worker's native steps: NST_Run, which the worker calls for     */
/* every step of every sequence (bound in MainCommand.cs through              */
/* STB_clsStepTable), and NST_Release, which deletes the stage files a        */
/* finished request carried.  Each step is one synchronous call on the        */
/* worker's own thread -- this class starts no thread -- and each hands the   */
/* captured request to its EngineSession.cs helper (ENGS_Do...) or to a       */
/* function of its own, and returns that helper's own result record inside    */
/* an RSL_clsResult, unconverted.  The steps for copies, the raw copy, the    */
/* format, the check, the resize, the capacity test and the read-out for an   */
/* editor are in CommandNativeOperations.cs; the recycling of a cut's         */
/* sources in CommandRecycleSources.cs; the image file's inspection in        */
/* CommandImageOpening.cs.                                                    */
/*----------------------------------------------------------------------------*/

/* The framework: ArgumentException, String and Int64 / UInt64 limits         */
/* (System), the picker's partition list (Collections.Generic), and the new   */
/* image file's stream and the staging folder's full path (IO).  No window    */
/* is touched.                                                                */
using System;
using System.Collections.Generic;
using System.IO;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* GPE_clsGptEnd -- what a disk's primary GPT header says about where its */
    /* backup copy lies, as Nst_ReadGptEnd found it: the facts behind Read    */
    /* Device's choice to stop just after the backup table.  Plain values;    */
    /* no handle or buffer is kept.                                           */
    /*------------------------------------------------------------------------*/
    internal sealed class GPE_clsGptEnd
    {
        /* The finding -- documentation table. */
        internal bool  GPE_bGpt;             // true when sector 0 holds the protective entry and sector 1 a whole, primary GPT header
        internal ulong GPE_ullAlternateLba;  // the sector the primary header names for the backup header; 0 when not a GPT
        internal ulong GPE_ullEndBytes;      // bytes from the disk's start through that sector, (alternate + 1) x the sector size; 0 when not a GPT
    }

    internal sealed partial class NST_clsNativeSteps
    {
        /* The staging folder, fixed for the steps' life -- documentation table. */
        private readonly string Nst_sStageRoot; // full path of the program's own staging folder, where read-outs are made (CommandStage.cs); never a place a user's Save As writes to

        /*--------------------------------------------------------------------*/
        /* NST_clsNativeSteps:                                                */
        /*                                                                    */
        /* The native steps, bound to the staging folder.  Nothing is opened  */
        /* or started: the steps run on whichever thread calls NST_Run, which */
        /* is the command worker's.                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sStageRoot : the program's staging folder.                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the steps; an ArgumentException for no folder. */
        /*--------------------------------------------------------------------*/
        internal NST_clsNativeSteps(string sStageRoot)
        {
            if (String.IsNullOrEmpty(sStageRoot))
            {   /* No folder: refused -- a read-out's stage file needs one. */
                throw new ArgumentException("A private staging directory is required.");
            }

            /* The folder as a full path, once.  It is not made here: the    */
            /* stage procedure makes it when a stage file is first created   */
            /* (Stg_CreateFile, CommandStage.cs).                            */
            Nst_sStageRoot = Path.GetFullPath(sStageRoot);
        }

        /*--------------------------------------------------------------------*/
        /* Nst_Status:                                                        */
        /*                                                                    */
        /* The result of an engine call just made: its status, and on a       */
        /* failure the engine's detail -- read now, before another engine     */
        /* call can replace it.                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iStatus    : the call's status.                                */
        /*     objPayload : the step's finished data, or null.                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the status, the detail ("" for success), and   */
        /*                     the data.                                      */
        /*--------------------------------------------------------------------*/
        private static RSL_clsResult Nst_Status(int iStatus, object objPayload)
        {
            string sDetail;

            /* No words for a success.  For a failure, the engine's last-error   */
            /* text, which belongs to the call that just returned only until     */
            /* the next engine call -- so it is read before anything else is.    */
            sDetail = "";
            if (iStatus != ENG_clsNative.ENG_OK)
            {   /* A failure: its words, now. */
                sDetail = ENG_clsNative.ENG_ErrorDetail();
            }

            /* The result, the data kept as it is. */
            return(new RSL_clsResult(iStatus, sDetail, objPayload));
        }

        /*--------------------------------------------------------------------*/
        /* NST_Run:                                                           */
        /*                                                                    */
        /* Run one step of a sequence: the native or local work the step      */
        /* names, with the request's captured arguments, and return its       */
        /* result.  A request missing what its step needs is refused here,    */
        /* never completed from what a window shows.                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     enmStep      : the step the sequence chose.                    */
        /*     objRequest   : the request whose arguments it uses.            */
        /*     objExecution : the running command's services.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the step's result, its helper's own record as  */
        /*                     its data; ENG_ERR_ARG for a malformed request  */
        /*                     or an unknown step.                            */
        /*--------------------------------------------------------------------*/
        internal RSL_clsResult NST_Run(STP_enmStep enmStep, RQT_clsRequest objRequest,
            EXC_clsExecution objExecution)
        {
            TGT_clsTarget objTarget;
            ESR_clsResult objSimple;
            EOR_clsOpenResult objOpened;
            ELR_clsListResult objListing;
            EVR_clsVolumeResult objVolume;
            ETR_clsTextResult objText;
            EDV_clsDeviceResult objDevices;
            EIR_clsImageResult objImage;
            PAA_clsPartitionArgs objBatch;
            string sFolder;

            /* The target, which every step but three must carry -- even one   */
            /* that works on the open session (its request names the target    */
            /* it was made for).  Closing the session, listing the disks and   */
            /* setting the verify option need none.  A missing one is never    */
            /* taken from the panes.                                           */
            objTarget = objRequest.RQT_objTarget;
            if (objTarget == null && enmStep != STP_enmStep.Close &&
                enmStep != STP_enmStep.ListDevices && enmStep != STP_enmStep.SetVerify)
            {   /* No target where one is needed: refused. */
                return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG, "No target supplied.", null));
            }

            /* One case per step.  Each returns its helper's own result record   */
            /* inside the RSL_clsResult, with the record's own status and        */
            /* detail: the records share no base class, so none is converted.    */
            switch (enmStep)
            {   /* The step the sequence chose. */
                case STP_enmStep.InspectImageFile:
                    /* An image file's table and content read, the open session   */
                    /* untouched (CommandImageOpening.cs).                        */
                    return(Nst_InspectImageFile(objRequest));
                case STP_enmStep.Open:
                    /* The open: read-only unless the request is the writable   */
                    /* reopen -- the kind decides, not the flags.  The helper   */
                    /* closes the session again if its facts cannot be read.    */
                    objOpened = new EOR_clsOpenResult();
                    ENGS_clsSession.ENGS_DoOpen(objTarget.TGT_sPath, objTarget.TGT_ullOffset,
                        objTarget.TGT_ullLength, objRequest.RQT_enmKind != KND_enmKind.MakeWritable,
                        objOpened);

                    /* The open's record, success or failure alike. */
                    return(new RSL_clsResult(objOpened.EOR_iStatus, objOpened.EOR_sDetail, objOpened));
                case STP_enmStep.Close:
                    /* The close.  The session is closed whatever its final     */
                    /* commit returned (ENGS_DoClose); a failure is reported,   */
                    /* never left half-open.                                    */
                    objSimple = new ESR_clsResult();
                    ENGS_clsSession.ENGS_DoClose(objSimple);

                    /* Its outcome, which the sequence reads before anything   */
                    /* that needs the close to have succeeded.                 */
                    return(new RSL_clsResult(objSimple.ESR_iStatus, objSimple.ESR_sDetail, objSimple));
                case STP_enmStep.List:
                    /* The folder: the request's one named path when it has     */
                    /* one, else its folder (a re-list after a change carries   */
                    /* only that).                                              */
                    sFolder = objRequest.RQT_sFolder;
                    if (objRequest.RQT_lstPaths.Count != 0)
                    {   /* A path named: that one. */
                        sFolder = objRequest.RQT_lstPaths[0];
                    }

                    /* The folder's entries, in their on-disk order. */
                    objListing = new ELR_clsListResult();
                    ENGS_clsSession.ENGS_DoList(sFolder, objListing);

                    /* The listing's record: the entries got, and the status   */
                    /* -- a failure part-way keeps the entries already read.   */
                    return(new RSL_clsResult(objListing.ELR_iStatus, objListing.ELR_sDetail, objListing));
                case STP_enmStep.VolumeInfo:
                    /* The open filesystem's facts, from the engine. */
                    objVolume = new EVR_clsVolumeResult();
                    ENGS_clsSession.ENGS_DoVolumeInfo(objVolume);

                    /* The facts' record. */
                    return(new RSL_clsResult(objVolume.EVR_iStatus, objVolume.EVR_sDetail, objVolume));
                case STP_enmStep.ReadLink:
                    if (objRequest.RQT_lstPaths.Count != 1)
                    {   /* Not exactly one item: refused -- Properties reads   */
                        /* one link.                                           */
                        return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG, "Select one symbolic link.", null));
                    }

                    /* The link's target text. */
                    objText = new ETR_clsTextResult();
                    ENGS_clsSession.ENGS_DoReadLink(objRequest.RQT_lstPaths[0], objText);

                    /* The text's record. */
                    return(new RSL_clsResult(objText.ETR_iStatus, objText.ETR_sDetail, objText));
                case STP_enmStep.ListDevices:
                    /* The disks the program can open; no session needed. */
                    objDevices = new EDV_clsDeviceResult();
                    ENGS_clsSession.ENGS_DoListDevices(objDevices);

                    /* The disks' record, handed on as it is. */
                    return(new RSL_clsResult(objDevices.EDV_iStatus, objDevices.EDV_sDetail, objDevices));
                case STP_enmStep.ReadDeviceTable:
                    /* A picker's partition choices for one device or file. */
                    return(Nst_ReadDeviceTable(objRequest));
                case STP_enmStep.ImageInfo:
                    /* The image file's format and sizes, read from the file   */
                    /* itself, never from its name.                            */
                    objImage = new EIR_clsImageResult();
                    ENGS_clsSession.ENGS_DoImageInfo(objTarget.TGT_sPath, objImage);

                    /* The facts' record: the size may be ENG_SIZE_UNKNOWN. */
                    return(new RSL_clsResult(objImage.EIR_iStatus, objImage.EIR_sDetail, objImage));
                case STP_enmStep.ReadLayout:
                    /* The partition window's whole layout. */
                    return(Nst_ReadLayout(objRequest));
                case STP_enmStep.WriteTable:
                    /* The table's pieces, built and captured before the      */
                    /* request was posted -- never rebuilt from the window.   */
                    objBatch = objRequest.RQT_objPayload as PAA_clsPartitionArgs;
                    if (objBatch == null)
                    {   /* No pieces: refused, nothing written. */
                        return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                            "No immutable table proposal supplied.", null));
                    }

                    if (objRequest.RQT_enmKind == KND_enmKind.ExpandFilesystem && !objBatch.PAA_bGrowTable)
                    {   /* An expansion that only grows the filesystem into room   */
                        /* the table already gives: no table write -- success,     */
                        /* so the expansion's check and resize follow.             */
                        return(new RSL_clsResult(SYS_clsSystem.SYS_OK, "", null));
                    }

                    /* The table written, with any old GPT's headers it does    */
                    /* not rewrite erased in the same batch (Nst_WriteTable).   */
                    return(Nst_WriteTable(objRequest, objBatch));
                case STP_enmStep.Edit:
                    /* One change to the filesystem, of the request's kind. */
                    return(Nst_Edit(objRequest, objExecution));
                case STP_enmStep.Save:
                    /* An editor's save into the image. */
                    return(Nst_Save(objRequest, objExecution));
                case STP_enmStep.Copy:
                    /* An extract, import or copy within (CommandNativeOperations.cs). */
                    return(Nst_Copy(objRequest, objExecution));
                case STP_enmStep.Recycle:
                    /* A cut's Windows sources to the Recycle Bin   */
                    /* (CommandRecycleSources.cs).                  */
                    return(Nst_RecycleCopySources(objRequest, objExecution));
                case STP_enmStep.ExtractEntry:
                    /* An image file read out for an editor   */
                    /* (CommandNativeOperations.cs).          */
                    return(Nst_ExtractEntry(objRequest, objExecution));
                case STP_enmStep.Check:
                case STP_enmStep.Repair:
                    /* e2fsck: repairing for the Repair step, report-only for   */
                    /* the Check step -- the step decides, nothing else.        */
                    return(Nst_Check(objRequest, objExecution, enmStep == STP_enmStep.Repair));
                case STP_enmStep.MakeFilesystem:
                    /* A format, with its zero-fill when asked. */
                    return(Nst_Format(objRequest, objExecution));
                case STP_enmStep.Resize:
                    /* A grow, with its one repair-first retry. */
                    return(Nst_Resize(objRequest, objExecution));
                case STP_enmStep.TestCapacity:
                    /* The capacity test, which puts back what it wrote. */
                    return(Nst_Capacity(objRequest, objExecution));
                case STP_enmStep.CopyRaw:
                    /* A raw copy, with its read-back, hash and sum's file. */
                    return(Nst_Raw(objRequest, objExecution));
                case STP_enmStep.CreateImage:
                    /* New Image's file, made once the old session has closed   */
                    /* (the sequence runs this only after that).                */
                    return(Nst_CreateImage(objRequest));
                case STP_enmStep.SetVerify:
                    /* The Verify Writes option, as captured: any flag set is   */
                    /* "on".                                                    */
                    objSimple = new ESR_clsResult();
                    ENGS_clsSession.ENGS_DoSetWriteVerify(objRequest.RQT_uiFlags != 0, objSimple);

                    /* The engine's status; nothing else follows from it. */
                    return(new RSL_clsResult(objSimple.ESR_iStatus, objSimple.ESR_sDetail, objSimple));
                case STP_enmStep.ReadGptEnd:
                    /* Two sectors read, nothing written: where the backup GPT ends. */
                    return(Nst_ReadGptEnd(objRequest));
                default:
                    /* A step this switch does not know: refused, never   */
                    /* taken for another.                                 */
                    return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                        "Unknown synchronous command step.", null));
            }
        }

        /*--------------------------------------------------------------------*/
        /* NST_Release:                                                       */
        /*                                                                    */
        /* Delete the stage files a request carried, once it is finished --   */
        /* done, refused at posting, or dropped (STB_Release).  Only the      */
        /* stages a request's own arguments hold are touched: never a         */
        /* read-out an editor owns, and never a user's file.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the finished request.                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : its stages released; a failed deletion throws           */
        /*            (STG_Release).                                          */
        /*--------------------------------------------------------------------*/
        internal void NST_Release(RQT_clsRequest objRequest)
        {
            SVA_clsSaveArgs objSave;
            EDA_clsEditArgs objEdit;

            /* Which stages: the ones the request's arguments own, found by    */
            /* their type -- never by a path, which could name any file.  A    */
            /* release twice deletes once (STG_Release), and a new file's      */
            /* stage that was handed to its editor (Nst_OpenCreatedEntry) is   */
            /* already released and deletes nothing here.                      */
            objSave = objRequest.RQT_objPayload as SVA_clsSaveArgs;
            objEdit = objRequest.RQT_objPayload as EDA_clsEditArgs;
            if (objSave != null)
            {   /* A save's stage: released, whatever the save's outcome. */
                objSave.SVA_objStage.STG_Release();
            }

            if (objEdit != null && objEdit.EDA_objStage != null)
            {   /* A new file's content stage: released.  The file it made   */
                /* in the image is not touched.                              */
                objEdit.EDA_objStage.STG_Release();
            }
        }

        /*--------------------------------------------------------------------*/
        /* Nst_ReadLayout:                                                    */
        /*                                                                    */
        /* The ReadLayout step: the partition window's disk read whole -- its */
        /* table (PTB_Read), each partition's content (PTB_Probe), and what   */
        /* the disk's start holds when it has no table.  A read failure is    */
        /* reported as one, not passed off as an empty disk.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the disk as its target: path, size, sector size.  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the layout (ELY_clsLayoutResult); the first    */
        /*                     read's failure; ENG_ERR_ARG for a sector size  */
        /*                     or disk size the readers cannot use.           */
        /*--------------------------------------------------------------------*/
        private static RSL_clsResult Nst_ReadLayout(RQT_clsRequest objRequest)
        {
            TGT_clsTarget objTarget;
            LDR_clsLayoutReader objReader;
            byte[] abFirst;
            ELY_clsLayoutResult objLayout;
            RSL_clsResult objFailure;

            /* The sector size must be one the table readers handle -- a power   */
            /* of two from 512 to 4096 -- and the disk must hold at least one    */
            /* sector, or there is no first sector to read a table from.         */
            objTarget = objRequest.RQT_objTarget;
            if (objTarget.TGT_uiSectorBytes < 512 || objTarget.TGT_uiSectorBytes > 4096 ||
                (objTarget.TGT_uiSectorBytes & (objTarget.TGT_uiSectorBytes - 1)) != 0 ||
                objTarget.TGT_ullDiskBytes < objTarget.TGT_uiSectorBytes)
            {   /* Refused before any read. */
                return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                    "Invalid target capacity or sector size.", null));
            }

            /* The table and each partition's content, through a reader that     */
            /* keeps the first real read failure (CommandLayoutReader.cs): the   */
            /* parsers take a failed read as "not there" and fall back (a        */
            /* damaged GPT to the MBR, say), so without it an unreadable disk    */
            /* would come back as an emptier layout.  The parsing itself is      */
            /* unchanged.                                                        */
            objReader = new LDR_clsLayoutReader(objTarget.TGT_sPath);
            objLayout = new ELY_clsLayoutResult();
            objLayout.ELY_objTable = new PTB_Table();
            objLayout.ELY_sAccount = PTB_Tables.PTB_Read(objReader.LDR_Read,
                objTarget.TGT_ullDiskBytes, objTarget.TGT_uiSectorBytes, objLayout.ELY_objTable);
            PTB_Tables.PTB_Probe(objReader.LDR_Read, objLayout.ELY_objTable);

            /* What the disk's start holds, for a disk with no table (a whole   */
            /* filesystem, say) -- only from a whole probe page, so a disk      */
            /* shorter than one is left unrecognised ("").                      */
            objLayout.ELY_sWholeContent = "";
            abFirst = new byte[PTB_Tables.PTB_PROBE_BYTES];
            if (objTarget.TGT_ullDiskBytes >= (ulong)abFirst.Length && objReader.LDR_Read(0, abFirst))
            {   /* The page read: its content's name, and an ext filesystem's   */
                /* own length (0 when it is not ext).                           */
                objLayout.ELY_sWholeContent = PTB_Tables.PTB_ContentText(abFirst);
                objLayout.ELY_ullWholeFsBytes = PTB_Tables.PTB_ExtBytes(abFirst);
            }

            if (objReader.LDR_ReadFailure(out objFailure))
            {   /* A read failed somewhere above: that failure, not the    */
                /* layout -- an unreadable table must never look like an   */
                /* empty disk ready to be partitioned.                     */
                return(objFailure);
            }

            /* Every read succeeded: the record says so (ENG_OK, no detail),   */
            /* and the layout is returned with that status and detail.         */
            objLayout.ELY_iStatus = ENG_clsNative.ENG_OK;
            objLayout.ELY_sDetail = "";
            return(new RSL_clsResult(objLayout.ELY_iStatus, objLayout.ELY_sDetail, objLayout));
        }

        /*--------------------------------------------------------------------*/
        /* Nst_ReadDeviceTable:                                               */
        /*                                                                    */
        /* The ReadDeviceTable step: a picker's partition choices for one     */
        /* device or image file -- its sector size from the engine, then its  */
        /* table (PRF_ReadTable, which follows an extended MBR's logical      */
        /* partitions too).  A read failure is reported as one.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the device or file as its target.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the choices (ETB_clsTableResult); the engine's */
        /*                     failure to identify the target; the first      */
        /*                     read's failure; ENG_ERR_ARG for a sector size  */
        /*                     the reader cannot use.                         */
        /*--------------------------------------------------------------------*/
        private static RSL_clsResult Nst_ReadDeviceTable(RQT_clsRequest objRequest)
        {
            DVI_stDeviceInfo stInfo;
            int iStatus;
            LDR_clsLayoutReader objReader;
            ETB_clsTableResult objTable;
            RSL_clsResult objFailure;

            /* The target's facts from the engine, its sector size above all:   */
            /* every LBA in the table counts in it.  If they cannot be read,    */
            /* nothing is read with a guessed 512.                              */
            iStatus = ENG_clsNative.ENG_pfnDeviceInfoGet(objRequest.RQT_objTarget.TGT_sPath, out stInfo);
            if (iStatus != ENG_clsNative.ENG_OK)
            {   /* Not identified: the failure, with the engine's detail. */
                return(Nst_Status(iStatus, null));
            }

            if (stInfo.DVI_uiSectorBytes < 512 || stInfo.DVI_uiSectorBytes > 4096 ||
                (stInfo.DVI_uiSectorBytes & (stInfo.DVI_uiSectorBytes - 1)) != 0)
            {   /* A sector size the reader cannot use (not a power of two   */
                /* from 512 to 4096): refused before any read.               */
                return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                    "Unsupported device sector size.", null));
            }

            /* The table, through the reader that keeps the first read failure   */
            /* (as Nst_ReadLayout's).  The list is made first and filled by      */
            /* the reader; the status is ENG_OK and the detail empty, since a    */
            /* failed read is returned as a failure of its own below.            */
            objReader = new LDR_clsLayoutReader(objRequest.RQT_objTarget.TGT_sPath);
            objTable = new ETB_clsTableResult();
            objTable.ETB_lstParts = new List<PRT_clsPartition>();
            objTable.ETB_iStatus = ENG_clsNative.ENG_OK;
            objTable.ETB_sDetail = "";
            objTable.ETB_sFound = PRF_clsPartitionForm.PRF_ReadTable(objReader.LDR_Read,
                stInfo.DVI_uiSectorBytes, objTable.ETB_lstParts);
            if (objReader.LDR_ReadFailure(out objFailure))
            {   /* A read failed: that failure, not a table-less disk. */
                return(objFailure);
            }

            /* The choices, with the reader's one-line account, once every   */
            /* read succeeded.                                               */
            return(new RSL_clsResult(objTable.ETB_iStatus, objTable.ETB_sDetail, objTable));
        }

        /*--------------------------------------------------------------------*/
        /* Nst_ReadGptEnd:                                                    */
        /*                                                                    */
        /* Where a disk's backup GPT header lies, as its primary header       */
        /* records it -- so Read Device can stop just after the backup table  */
        /* instead of at the device's end.  Only a GPT that can be trusted    */
        /* counts: the protective MBR entry in sector 0, and in sector 1 a    */
        /* header whose signature, size, CRC-32 and own sector are right.  A  */
        /* disk that is not such a GPT is a SUCCESS that says so; only a      */
        /* failed read is a failure.  The end is not held against the         */
        /* device's length here -- what an end past it means is the caller's. */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the disk as its target: path and sector size.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : success carrying a GPE_clsGptEnd, or the first */
        /*                     read's failure.                                */
        /*--------------------------------------------------------------------*/
        private static RSL_clsResult Nst_ReadGptEnd(RQT_clsRequest objRequest)
        {
            TGT_clsTarget objTarget;
            LDR_clsLayoutReader objReader;
            RSL_clsResult objFailure;
            GPE_clsGptEnd objEnd;
            byte[] abMbr;
            byte[] abHeader;
            ulong ullAlternate;
            uint uiSector;
            uint uiHeaderBytes;
            uint uiStoredCrc;
            bool bProtective;
            int iEntry;

            /* The disk's sector, which every LBA in the header counts: only    */
            /* the sizes the table readers handle (a power of two from 512 to   */
            /* 4096), or the two sectors below could not be read as sectors.    */
            objTarget = objRequest.RQT_objTarget;
            uiSector = objTarget.TGT_uiSectorBytes;
            if (uiSector < PTB_Tables.PTB_MIN_SECTOR || uiSector > PTB_Tables.PTB_MAX_SECTOR ||
                (uiSector & (uiSector - 1)) != 0)
            {   /* No sector size to count in: refused before any read. */
                return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG, "Unsupported device sector size.", null));
            }

            /* Not a GPT until the two sectors say so. */
            objEnd = new GPE_clsGptEnd();
            objEnd.GPE_bGpt = false;
            objEnd.GPE_ullAlternateLba = 0;
            objEnd.GPE_ullEndBytes = 0;

            /* Sectors 0 and 1, through a reader that keeps the first real     */
            /* read failure, so an unreadable disk is reported as one rather   */
            /* than taken for a disk with no GPT.                              */
            objReader = new LDR_clsLayoutReader(objTarget.TGT_sPath);
            abMbr = new byte[uiSector];
            abHeader = new byte[uiSector];
            if (!objReader.LDR_Read(0, abMbr) || !objReader.LDR_Read(uiSector, abHeader))
            {   /* A sector could not be read.  LDR_Read answers false for two   */
                /* reasons, told apart by whether it kept a failure.             */
                if (objReader.LDR_ReadFailure(out objFailure))
                {   /* A real read failure: the failure the reader kept is the   */
                    /* answer, so an unreadable disk is reported as one.         */
                    return(objFailure);
                }

                /* No failure kept: the range lies past the target's end -- a     */
                /* target shorter than two sectors, which an image file can be.   */
                /* No header can be there, so the answer is "no GPT", as for a    */
                /* disk without the protective MBR below -- never a null that     */
                /* a caller's RSL_iStatus would throw on.                         */
                return(new RSL_clsResult(SYS_clsSystem.SYS_OK, "", objEnd));
            }

            /* STEP 1: the protective MBR -- the 0x55 0xAA signature and an   */
            /* entry of type 0xEE.  Without it a header in sector 1 is not    */
            /* taken as the disk's table, as the table readers decide too.    */
            bProtective = false;
            if (abMbr[PTB_Tables.PTB_MBR_BOOT_SIG] == 0x55 && abMbr[PTB_Tables.PTB_MBR_BOOT_SIG + 1] == 0xAA)
            {   /* A boot signature: look for the protective entry. */
                for (iEntry = 0; iEntry < PTB_Tables.PTB_MBR_ENTRIES; iEntry++)
                {   /* One primary entry per iteration: its type byte. */
                    if (abMbr[PTB_Tables.PTB_MBR_TABLE + (iEntry * PTB_Tables.PTB_MBR_ENTRY) + 4] == PTB_Tables.PTB_TYPE_PROTECTIVE)
                    {   /* The entry that says the disk has a GPT. */
                        bProtective = true;
                    }
                }
            }

            /* STEP 2: the header -- its signature, and a size from the 92    */
            /* bytes the header defines up to a whole sector (the CRC below   */
            /* is taken over exactly that many bytes).                        */
            uiHeaderBytes = PTB_Tables.PTB_ReadU32(abHeader, PTB_Tables.PTB_GPT_HEADER_SIZE_AT);
            if (!bProtective ||
                System.Text.Encoding.ASCII.GetString(abHeader, 0, PTB_Tables.PTB_GPT_SIGNATURE_BYTES) != "EFI PART" ||
                uiHeaderBytes < (uint)PTB_Tables.PTB_GPT_HEADER_BYTES || uiHeaderBytes > uiSector)
            {   /* Not a GPT disk: said so, which is no failure. */
                return(new RSL_clsResult(SYS_clsSystem.SYS_OK, "", objEnd));
            }

            /* The header's own CRC-32, computed with its CRC field zero (the   */
            /* UEFI GPT header's rule), and the sector the header says it is    */
            /* in, which must be 1 for the primary: a damaged header, or a      */
            /* backup copy found where the primary should be, gives no end      */
            /* that can be trusted.                                             */
            uiStoredCrc = PTB_Tables.PTB_ReadU32(abHeader, PTB_Tables.PTB_GPT_HEADER_CRC_AT);
            PTB_Tables.PTB_WriteU32(abHeader, PTB_Tables.PTB_GPT_HEADER_CRC_AT, 0);
            if (PTB_Tables.PTB_Crc32(abHeader, 0, (int)uiHeaderBytes) != uiStoredCrc ||
                PTB_Tables.PTB_ReadU64(abHeader, PTB_Tables.PTB_GPT_CURRENT_AT) != 1)
            {   /* Not a header to take an end from: treated as no GPT. */
                return(new RSL_clsResult(SYS_clsSystem.SYS_OK, "", objEnd));
            }

            /* STEP 3: the backup header's sector, as recorded.  It lies past   */
            /* the primary on any real table, and the end in bytes,             */
            /* (alternate + 1) x the sector, must be countable without          */
            /* wrapping 64 bits.                                                */
            ullAlternate = PTB_Tables.PTB_ReadU64(abHeader, PTB_Tables.PTB_GPT_ALTERNATE_AT);
            if (ullAlternate <= 1 || ullAlternate >= ulong.MaxValue / uiSector)
            {   /* No usable record of where the backup is: treated as no GPT. */
                return(new RSL_clsResult(SYS_clsSystem.SYS_OK, "", objEnd));
            }

            /* A GPT, and the end of its backup header's sector. */
            objEnd.GPE_bGpt = true;
            objEnd.GPE_ullAlternateLba = ullAlternate;
            objEnd.GPE_ullEndBytes = (ullAlternate + 1) * uiSector;

            /* The finding, for the picker or the read sequence to use. */
            return(new RSL_clsResult(SYS_clsSystem.SYS_OK, "", objEnd));
        }

        /*--------------------------------------------------------------------*/
        /* Nst_WriteTable:                                                    */
        /*                                                                    */
        /* The WriteTable step's write: a table's captured pieces as one      */
        /* engine batch, after asking the disk itself whether it holds a GPT  */
        /* -- and if it does, the old GPT's two headers that the new pieces   */
        /* do not rewrite are erased in that same batch, in front of the new  */
        /* table.  Without the erase an MBR written over a GPT, or a GPT      */
        /* written with its backup in another place, leaves the old headers   */
        /* on the disk with good checksums, and a GPT reader or a repair tool */
        /* finds the old partitions there.  The disk is read here, at the     */
        /* moment of the write, rather than taken from the window: its last   */
        /* read can be stale, and the window only posts what it built.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the disk as its target (path, length, sector).    */
        /*     objBatch   : the table's captured pieces, used as they are.    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the batch write's outcome; or the GPT header   */
        /*                     read's failure, with nothing written.          */
        /*--------------------------------------------------------------------*/
        private static RSL_clsResult Nst_WriteTable(RQT_clsRequest objRequest, PAA_clsPartitionArgs objBatch)
        {
            TGT_clsTarget objTarget;
            RSL_clsResult objGpt;
            GPE_clsGptEnd objEnd;
            ESR_clsResult objSimple;
            List<PTB_Piece> lstPieces;

            /* What the disk's primary header says now.  A sector that cannot   */
            /* be read fails the write before anything is written: a disk       */
            /* whose first sectors do not read is not one to write a table to   */
            /* blind.  No GPT (or one whose header cannot be trusted) is a      */
            /* success that says so, and nothing is erased.                     */
            objTarget = objRequest.RQT_objTarget;
            objGpt = Nst_ReadGptEnd(objRequest);
            if (objGpt.RSL_iStatus != SYS_clsSystem.SYS_OK)
            {   /* The header could not be read: that failure, nothing written. */
                return(objGpt);
            }

            /* The pieces to write: the captured ones, in their order, in a      */
            /* list of this step's own so the request's record stays as it was   */
            /* posted (the pieces' bytes are shared, never changed).             */
            lstPieces = new List<PTB_Piece>(objBatch.PAA_lstPieces);

            /* The header's finding, the read's payload: whether the disk   */
            /* holds a GPT, and where its backup header is.                 */
            objEnd = objGpt.RSL_objPayload as GPE_clsGptEnd;
            if (objEnd != null && objEnd.GPE_bGpt)
            {   /* A GPT on the disk: its headers the new pieces leave alone   */
                /* are erased first, in the same batch.                        */
                PTB_Tables.PTB_AddOldGptErase(objTarget.TGT_ullDiskBytes, objTarget.TGT_uiSectorBytes,
                    objEnd.GPE_ullAlternateLba, lstPieces);
            }

            /* Every piece in one engine call (ENGS_DoWritePieces, the engine's   */
            /* batch write): the engine checks them all before it writes any;     */
            /* an I/O failure part-way has no rollback.  The erases come first,   */
            /* so a stop part-way leaves no valid old GPT behind a new table.     */
            objSimple = new ESR_clsResult();
            ENGS_clsSession.ENGS_DoWritePieces(objTarget.TGT_sPath, lstPieces, objSimple);

            /* The batch's outcome and detail. */
            return(new RSL_clsResult(objSimple.ESR_iStatus, objSimple.ESR_sDetail, objSimple));
        }

        /*--------------------------------------------------------------------*/
        /* Nst_CreateImage:                                                   */
        /*                                                                    */
        /* The CreateImage step: New Image's file made on Windows at its      */
        /* size, flushed and closed -- empty (the length set, nothing         */
        /* written), for the partition window to lay out.                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the file as its target (path, and its size as the */
        /*                  disk's size); flag bit 0 set when New Image's own */
        /*                  file dialog agreed to replace an existing file.   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : SYS_OK once the file is made, flushed and      */
        /*                     closed; ENG_ERR_ARG for a bad size or a device */
        /*                     name.  A file that cannot be made (one that    */
        /*                     exists without the consent, no room, no        */
        /*                     access) throws to the worker.                  */
        /*--------------------------------------------------------------------*/
        private static RSL_clsResult Nst_CreateImage(RQT_clsRequest objRequest)
        {
            TGT_clsTarget objTarget;
            FileStream stmOutput;
            FileMode enmMode;

            /* The size must be given and fit a FileStream's signed length,    */
            /* and the path must not be a \\.\ device name (New Image makes    */
            /* files only) -- tested before anything is opened, so a refusal   */
            /* leaves an existing file whole.                                  */
            objTarget = objRequest.RQT_objTarget;
            if (objTarget.TGT_ullDiskBytes == 0 || objTarget.TGT_ullDiskBytes > Int64.MaxValue ||
                objTarget.TGT_sPath.StartsWith(@"\\.\", StringComparison.Ordinal))
            {   /* Refused, nothing made. */
                return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                    "Invalid new-image path or size.", null));
            }

            /* How the file is made: CreateNew, which fails on an existing       */
            /* file -- an existing file is replaced only when the request says   */
            /* the user agreed (bit 0), never because it happens to be there.    */
            enmMode = FileMode.CreateNew;
            if ((objRequest.RQT_uiFlags & 1) != 0)
            {   /* Replacing agreed to: Create, which empties an existing   */
                /* file.                                                    */
                enmMode = FileMode.Create;
            }

            /* The file opened for this alone (no sharing), set to its size     */
            /* and flushed to the disk itself, then closed at the block's end   */
            /* -- before the partition window, the next step, opens it.         */
            using (stmOutput = new FileStream(objTarget.TGT_sPath, enmMode,
                FileAccess.ReadWrite, FileShare.None))
            {   /* The new file: its length, then the flush. */
                stmOutput.SetLength((long)objTarget.TGT_ullDiskBytes);
                stmOutput.Flush(true);
            }

            /* Made: the sequence may go on to the partition window. */
            return(new RSL_clsResult(SYS_clsSystem.SYS_OK, "", null));
        }

        /*--------------------------------------------------------------------*/
        /* Nst_Edit:                                                          */
        /*                                                                    */
        /* The Edit step: one change to the open filesystem -- a new folder,  */
        /* a rename, a move, a delete, attributes, or a new file -- through   */
        /* its EngineSession.cs helper, which commits it.                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the items (RQT_lstPaths) and the change's       */
        /*                    arguments (an EDA_clsEditArgs payload).         */
        /*     objExecution : the running command's services (a new file's    */
        /*                    import reports progress and takes Cancel).      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the helper's outcome, its commit's failure     */
        /*                     included; ENG_ERR_ARG for a malformed request  */
        /*                     or an unknown kind.                            */
        /*--------------------------------------------------------------------*/
        private static RSL_clsResult Nst_Edit(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            EDA_clsEditArgs objArgs;
            ESR_clsResult objResult;

            /* The change's arguments, captured when the request was made, and   */
            /* at least one item.  Nothing missing is filled from a window.      */
            objArgs = objRequest.RQT_objPayload as EDA_clsEditArgs;
            if (objArgs == null || objRequest.RQT_lstPaths.Count == 0)
            {   /* Malformed: refused, nothing changed. */
                return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                    "No valid edit selection supplied.", null));
            }

            /* One helper per kind, each committing its own change and putting   */
            /* its outcome in this one record.  Where a helper takes arrays,     */
            /* the lists are converted once at its call.                         */
            objResult = new ESR_clsResult();
            switch (objArgs.EDA_enmKind)
            {   /* The kind of change. */
                case EDK_enmEditKind.MakeDirectory:
                    /* A new folder, at the one path. */
                    ENGS_clsSession.ENGS_DoMakeDir(objRequest.RQT_lstPaths[0], objResult);
                    break;
                case EDK_enmEditKind.Rename:
                    if (objRequest.RQT_lstPaths.Count != 1 || objArgs.EDA_lstDestinations.Count != 1)
                    {   /* Not one item and one new name: refused. */
                        return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                            "Rename needs one source and destination.", null));
                    }

                    /* The rename; the helper refuses a new name already taken. */
                    ENGS_clsSession.ENGS_DoRename(objRequest.RQT_lstPaths[0],
                        objArgs.EDA_lstDestinations[0], objResult);
                    break;
                case EDK_enmEditKind.Move:
                    if (objRequest.RQT_lstPaths.Count != objArgs.EDA_lstDestinations.Count)
                    {   /* An item without its destination (or one too many):   */
                        /* refused before any item moves.                       */
                        return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                            "Move selection lengths differ.", null));
                    }

                    /* The moves, item by item, as arrays for the helper. */
                    ENGS_clsSession.ENGS_DoMove(objRequest.RQT_lstPaths.ToArray(),
                        objArgs.EDA_lstDestinations.ToArray(), objResult);
                    break;
                case EDK_enmEditKind.Delete:
                    if (objRequest.RQT_lstPaths.Count != objArgs.EDA_lstDirectories.Count)
                    {   /* An item without its folder-or-not flag: refused   */
                        /* before anything is deleted.                       */
                        return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                            "Delete selection lengths differ.", null));
                    }

                    /* The deletes, each item with its flag, as arrays; the      */
                    /* command given too, so a large delete shows its progress   */
                    /* window and takes Cancel through it.                       */
                    ENGS_clsSession.ENGS_DoDelete(objRequest.RQT_lstPaths.ToArray(),
                        objArgs.EDA_lstDirectories.ToArray(), objResult, objExecution);
                    break;
                case EDK_enmEditKind.Attributes:
                    /* The one item's mode and owner, each only when its flag   */
                    /* says it is to change.                                    */
                    ENGS_clsSession.ENGS_DoSetAttributes(objRequest.RQT_lstPaths[0],
                        objArgs.EDA_bMode, objArgs.EDA_uiMode, objArgs.EDA_bOwner,
                        objArgs.EDA_uiUid, objArgs.EDA_uiGid, objResult);
                    break;
                case EDK_enmEditKind.CreateFile:
                    if (objArgs.EDA_objStage == null)
                    {   /* No content stage: refused -- a new file's content   */
                        /* is never read from an editor's live text.           */
                        return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                            "No new-file content stage supplied.", null));
                    }

                    /* The new file from its stage, refusing a name already     */
                    /* taken (no overwrite), its attributes from the entry it   */
                    /* copies when one is named; the import reports progress    */
                    /* and takes Cancel through the command.                    */
                    ENGS_clsSession.ENGS_DoCreateFile(objArgs.EDA_objStage.STG_ReadPath(),
                        objRequest.RQT_lstPaths[0], false, objArgs.EDA_sAttributesFrom,
                        objResult, objExecution);
                    break;
                default:
                    /* An unknown kind: refused, nothing guessed. */
                    return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG, "Unknown edit kind.", null));
            }

            /* The change's outcome, with its record. */
            return(new RSL_clsResult(objResult.ESR_iStatus, objResult.ESR_sDetail, objResult));
        }

        /*--------------------------------------------------------------------*/
        /* Nst_Save:                                                          */
        /*                                                                    */
        /* The Save step: an editor's staged content written into the image   */
        /* at its destination and committed.  A Save to the file the editor   */
        /* came from replaces it; a Save As onto an existing file asks first. */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the save's arguments (an SVA_clsSaveArgs        */
        /*                    payload: the stage, the destination, the entry  */
        /*                    the editor came from, the same-filesystem test) */
        /*                    and the asking window.                          */
        /*     objExecution : the running command's services.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the helper's outcome (a successful save can    */
        /*                     carry a warning); ENG_ERR_CANCELLED when the   */
        /*                     replace was declined, nothing changed;         */
        /*                     ENG_ERR_ARG for no arguments.                  */
        /*--------------------------------------------------------------------*/
        private static RSL_clsResult Nst_Save(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            SVA_clsSaveArgs objArgs;
            ESR_clsResult objResult;
            ANS_clsAnswer objAnswer;
            bool bSameEntry;
            string sAttributesFrom;

            /* The save's arguments.  The sequence has checked the editor's     */
            /* id, epoch, range and revision already; the helper below still    */
            /* looks at the destination afresh rather than trusting a Save As   */
            /* listing or the pane.                                             */
            objArgs = objRequest.RQT_objPayload as SVA_clsSaveArgs;
            if (objArgs == null)
            {   /* No arguments: refused -- an editor's file is never taken   */
                /* in their place.                                            */
                return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                    "No immutable Save content supplied.", null));
            }

            /* A plain Save (the destination is the entry it came from, in the   */
            /* same filesystem) may replace that entry without asking.  The      */
            /* attributes come from that entry only within the same              */
            /* filesystem; a Save As to another one takes the new-file           */
            /* defaults.                                                         */
            bSameEntry = objArgs.SVA_bSameFilesystem &&
                objArgs.SVA_sExpectedEntry == objArgs.SVA_sDestination;
            sAttributesFrom = null;
            if (objArgs.SVA_bSameFilesystem)
            {   /* Same filesystem: its original entry's attributes. */
                sAttributesFrom = objArgs.SVA_sExpectedEntry;
            }

            /* The write and commit; the helper refuses a directory, a link or   */
            /* a special file, and an existing file unless replacing is          */
            /* allowed (ENG_ERR_EXISTS: ask, then call again).                   */
            objResult = new ESR_clsResult();
            ENGS_clsSession.ENGS_DoCreateFile(objArgs.SVA_objStage.STG_ReadPath(),
                objArgs.SVA_sDestination, bSameEntry, sAttributesFrom, objResult, objExecution);
            if (objResult.ESR_iStatus == ENG_clsNative.ENG_ERR_EXISTS)
            {   /* A Save As onto an existing file: the user is asked, through   */
                /* the editor's window.                                          */
                objAnswer = objExecution.EXC_Ask(QNK_enmQuestionKind.Overwrite,
                    objRequest.RQT_lOrigin, objArgs.SVA_sDestination);
                if (objAnswer == null || objAnswer.ANS_enmKind != ANK_enmAnswerKind.Yes ||
                    !objExecution.EXC_Checkpoint())
                {   /* Not Yes -- or a Cancel or a confirmed Exit came while   */
                    /* asking: nothing replaced.                               */
                    return(new RSL_clsResult(ENG_clsNative.ENG_ERR_CANCELLED,
                        "Save As overwrite declined.", null));
                }

                /* Agreed: the same stage and attributes again, replacing now.   */
                /* The worker held the session while the question waited, and    */
                /* the helper looks at the destination again.                    */
                ENGS_clsSession.ENGS_DoCreateFile(objArgs.SVA_objStage.STG_ReadPath(),
                    objArgs.SVA_sDestination, true, sAttributesFrom, objResult, objExecution);
            }

            /* The save's outcome: a failed commit is a failure; a success may   */
            /* carry a clean-up warning.                                         */
            return(new RSL_clsResult(objResult.ESR_iStatus, objResult.ESR_sDetail, objResult));
        }
    }
}
