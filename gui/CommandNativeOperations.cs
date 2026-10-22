/*----------------------------------------------------------------------------*/
/* CommandNativeOperations.cs                                                 */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The command worker's native steps for the copies, the raw copy, the        */
/* format, the check, the resize and the capacity test, and the read-out of   */
/* an image file for an editor.  Each runs on the worker, synchronously,      */
/* inside one command's sequence: it hands the captured request to the        */
/* EngineSession.cs helper that makes the engine call -- with the command's   */
/* own callback control in place of a progress window -- and turns what the   */
/* helper reports into the step's result.  A managed failure inside a         */
/* callback makes the engine cancel, and is then reported as that failure,    */
/* not as a cancel.  The read-out also owns the file it makes: a stage file   */
/* (CommandStage.cs), deleted here on every failure and handed, on success,   */
/* to the opening sequence and then to the editor.                            */
/*----------------------------------------------------------------------------*/

/* The framework: IntPtr, the exceptions, the string comparison and the     */
/* collector's KeepAlive (System), the problem and report line lists        */
/* (Collections.Generic), the files and the read-out's size (IO), and the   */
/* reads of the engine's event memory (Runtime.InteropServices).            */
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* OED_clsOpenedEntryData -- an image file read out for an editor: its    */
    /* path in the image, its size, the classifier's verdict, and the stage   */
    /* file holding its bytes, whose token this record owns.  It passes from  */
    /* the read-out (Nst_ExtractEntry) to the opening sequence                */
    /* (Sys_OpenEntrySequence, CommandOperations.cs), which releases it when  */
    /* no editor takes it, and then to the editor's registration, released    */
    /* once the editor has closed (CommandSequences.cs) -- documentation      */
    /* table.                                                                 */
    /*------------------------------------------------------------------------*/
    internal sealed class OED_clsOpenedEntryData
    {
        internal string OED_sEntry;         // the entry's path in the image, as the request named it: the editor's image path, and its name is the last component (MainCommand.cs)
        internal long OED_lBytes;           // the read-out file's size in bytes, measured on disk after the read; the viewer choice's text limit tests it (OSC_TEXT_MAX_BYTES)
        internal int OED_iKind;             // OSC_KIND_TEXT, OSC_KIND_BINARY or OSC_KIND_PICTURE from OSC_Classify: which editor opens unless the user chose one
        internal string OED_sWhy;           // for binary content, the classifier's words finishing "It looks like ..."; "" otherwise
        private STG_clsStage Oed_objStage;  // the token owning the read-out file; only OED_Release deletes it

        /*--------------------------------------------------------------------*/
        /* OED_clsOpenedEntryData:                                            */
        /*                                                                    */
        /* The record of one read-out, taking ownership of its stage file.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objStage : the read-out file's token; this record is its       */
        /*                owner from here on.                                 */
        /*     sEntry   : the entry's path in the image.                      */
        /*     lBytes   : the read-out file's size, bytes.                    */
        /*     iKind    : OSC_Classify's verdict on its content.              */
        /*     sWhy     : OSC_Classify's words for binary content, or "".     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the record; nothing is opened or read.         */
        /*--------------------------------------------------------------------*/
        internal OED_clsOpenedEntryData(STG_clsStage objStage, string sEntry,
            long lBytes, int iKind, string sWhy)
        {
            /* The token and the facts, kept as given.  The read-out file       */
            /* becomes the editor's working copy, which it changes in place;    */
            /* each save takes a stage of its own from it (STG_CopySnapshot,    */
            /* CommandStage.cs), so a save waiting in the command queue never   */
            /* reads bytes the editor is still changing.                        */
            Oed_objStage = objStage;
            OED_sEntry = sEntry;
            OED_lBytes = lBytes;
            OED_iKind = iKind;
            OED_sWhy = sWhy;
        }

        /*--------------------------------------------------------------------*/
        /* OED_ReadPath:                                                      */
        /*                                                                    */
        /* The read-out file's path, for whoever reads its content: the       */
        /* format identification of the opening sequence, and the editor,     */
        /* which works on this file (MainCommand.cs).  It is never a place a  */
        /* user's Save As writes to.                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the path; the file is not opened.                     */
        /*--------------------------------------------------------------------*/
        internal string OED_ReadPath()
        {
            /* Only the path is lent: the right to delete the file stays with   */
            /* the token, so this record is kept until the editor is done.      */
            return(Oed_objStage.STG_ReadPath());
        }

        /*--------------------------------------------------------------------*/
        /* OED_Release:                                                       */
        /*                                                                    */
        /* Delete the read-out file: when no editor took it                   */
        /* (Sys_OpenEntrySequence), or once its editor has closed             */
        /* (CommandSequences.cs).  Only the first release deletes.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the file deleted; a failed deletion throws, the token   */
        /*            used all the same (STG_Release).                        */
        /*--------------------------------------------------------------------*/
        internal void OED_Release()
        {
            /* The token's one release.  Only the file this read-out made is   */
            /* deleted: the entry in the image, and any Windows file a Save    */
            /* As wrote, are other files with lives of their own.              */
            Oed_objStage.STG_Release();
        }
    }

    /*------------------------------------------------------------------------*/
    /* XEC_clsExtractCallback -- the engine callback for one read-out of an   */
    /* image file into its stage file.  The read-out writes into a file this  */
    /* program has already made (the stage's empty placeholder), so the       */
    /* engine meets an existing destination and asks about it; this callback  */
    /* answers OVERWRITE for that one file, and hands every other event to    */
    /* the command's native control (NCB_ProcessCopyEvent).  The delegate     */
    /* made from XEC_OnEvent keeps this object alive for the engine's call    */
    /* -- documentation table.                                                */
    /*------------------------------------------------------------------------*/
    internal sealed class XEC_clsExtractCallback
    {
        private const int XEC_KIND_OFFSET = 32;                // byte offset of BFE_CopyEvent's uiKind on x64: two 8-byte counts and two pointers come first (bifrost_engine.h)
        private const int XEC_HOST_OFFSET = 24;                // byte offset of BFE_CopyEvent's pwszHostPath on x64: the destination's UTF-16 path, valid only during the callback
        private readonly string Xec_sPath;                     // the stage file's path: the one existing destination this read-out may replace
        private readonly EXC_clsExecution Xec_objExecution;    // the running command's services: its checkpoint (a Cancel, a pending Exit)
        private readonly NCB_clsNativeControl Xec_objControl;  // this read-out's native control: every other event, and the first callback failure

        /*--------------------------------------------------------------------*/
        /* XEC_clsExtractCallback:                                            */
        /*                                                                    */
        /* The callback for one read-out into the stage file at sPath.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sPath        : the stage file the engine writes into.          */
        /*     objExecution : the running command's services.                 */
        /*     objControl   : the read-out's native control.                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the callback; nothing is opened, made or       */
        /*                     waited on.                                     */
        /*--------------------------------------------------------------------*/
        internal XEC_clsExtractCallback(string sPath, EXC_clsExecution objExecution,
            NCB_clsNativeControl objControl)
        {
            /* Kept as given, for the one engine call: the read-out is          */
            /* synchronous, and the delegate made from XEC_OnEvent holds this   */
            /* object for as long as the engine holds the delegate.             */
            Xec_sPath = sPath;
            Xec_objExecution = objExecution;
            Xec_objControl = objControl;
        }

        /*--------------------------------------------------------------------*/
        /* XEC_OnEvent:                                                       */
        /*                                                                    */
        /* Answer one event of the read-out.  The engine asking about the     */
        /* stage file is answered OVERWRITE while the command may go on; any  */
        /* other existing destination, or a stopped command, CANCEL; every    */
        /* other event is the native control's.  Hooked to BFE_Extract as its */
        /* callback (Nst_ExtractEntry), so it runs on the worker inside the   */
        /* engine's call -- and no exception may unwind into the engine.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pEvent    : the engine's BFE_CopyEvent, valid only during this */
        /*                 call.                                              */
        /*     pvContext : unused (the delegate is bound to this object);     */
        /*                 passed on to the native control unchanged.         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : ENG_REPLY_OVERWRITE, ENG_REPLY_CANCEL, or the native     */
        /*           control's reply.                                         */
        /*--------------------------------------------------------------------*/
        internal int XEC_OnEvent(IntPtr pEvent, IntPtr pvContext)
        {
            uint uiKind;
            IntPtr pHost;
            string sHost;

            try
            {   /* Everything inside the try -- the reads of engine memory,      */
                /* the checkpoint and the native control's work -- so that no    */
                /* exception can reach the engine's C frames.                    */
                /* The event's kind only, read at its offset.  The rest of the   */
                /* event is copied by the native control when it handles one,    */
                /* so it is not copied twice.                                    */
                uiKind = (uint)Marshal.ReadInt32(pEvent, XEC_KIND_OFFSET);
                if (uiKind == ENG_clsNative.ENG_EVENT_CONFLICT)
                {   /* The destination exists.  For a read-out that is the       */
                    /* stage file this program made, empty, before the call      */
                    /* (Nst_WritePlaceholder) -- which the engine keeps unless   */
                    /* the reply is OVERWRITE (bfe_copy.c).                      */
                    /* The destination's path, copied as UTF-16 now: the         */
                    /* engine's memory is valid only during this call, and the   */
                    /* checkpoint below can wait (on an Exit question).          */
                    pHost = Marshal.ReadIntPtr(pEvent, XEC_HOST_OFFSET);
                    sHost = "";
                    if (pHost != IntPtr.Zero)
                    {   /* A path given: copied. */
                        sHost = Marshal.PtrToStringUni(pHost);
                    }

                    /* Only the stage file is replaced -- the engine hands back   */
                    /* the path it was given, compared without letter case as     */
                    /* Windows names are -- and only while the command may go     */
                    /* on: EXC_Checkpoint is false after a Cancel or a            */
                    /* confirmed Exit, and an Exit question waits inside it.      */
                    if (String.Equals(sHost, Xec_sPath, StringComparison.OrdinalIgnoreCase) &&
                        Xec_objExecution.EXC_Checkpoint())
                    {   /* The stage file, and the command still running:   */
                        /* replace it.                                      */
                        return(ENG_clsNative.ENG_REPLY_OVERWRITE);
                    }

                    /* Another existing file is not this read-out's to replace   */
                    /* -- a guard: a one-file read-out meets only its own        */
                    /* destination -- and a stopped command writes nothing.      */
                    /* The engine is told to cancel, keeping the file, and       */
                    /* returns ENG_ERR_CANCELLED; no failure is kept, so the     */
                    /* result reads as a cancel.                                 */
                    return(ENG_clsNative.ENG_REPLY_CANCEL);
                }

                /* Every other event -- the item's start, progress, done, an      */
                /* error or a skip -- handled as any copy's events are            */
                /* (CommandNativeControl.cs): counted into the result, progress   */
                /* published, a Cancel or an Exit answered.  Its reply is the     */
                /* engine's.                                                      */
                return(Xec_objControl.NCB_ProcessCopyEvent(pEvent, pvContext));
            }
            catch (Exception exCallback)
            {   /* A managed exception: kept as the failure (the first only),    */
                /* and the engine told to cancel.  Nst_ExtractEntry then         */
                /* reports this failure, not the cancel it caused.  The whole    */
                /* exception goes to the debug log; its message is the failure   */
                /* the user is shown.                                            */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("read-out callback", exCallback.ToString());
                }

                /* Kept as the failure (the first only): the message alone. */
                Xec_objControl.NCB_RecordFailure(exCallback.Message);
                return(ENG_clsNative.ENG_REPLY_CANCEL);
            }
        }
    }

    internal sealed partial class NST_clsNativeSteps
    {
        /*--------------------------------------------------------------------*/
        /* Nst_CopyResult:                                                    */
        /*                                                                    */
        /* A new copy result for one native step, ready for the engine's      */
        /* first event.                                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ECR_clsCopyResult : no counts, an empty problem list, empty    */
        /*                         texts and no notes list.                   */
        /*--------------------------------------------------------------------*/
        private static ECR_clsCopyResult Nst_CopyResult()
        {
            ECR_clsCopyResult objResult;

            /* The problem list made now: an error or a skip can be the very    */
            /* first event, and the native control adds to the list without     */
            /* testing for one.  The notes list is left null -- the control     */
            /* makes it with the first note.  The detail and the hash start     */
            /* empty, so a reader can test their length without a null test     */
            /* (ENGS_DoCopyRaw also empties the hash itself before its copy).   */
            /* The result is filled on the worker only, until the step          */
            /* returns it.                                                      */
            objResult = new ECR_clsCopyResult();
            objResult.ECR_lstProblems = new List<string>();
            objResult.ECR_sDetail = "";
            objResult.ECR_sHash = "";

            /* The empty result, for the helper and the callbacks to fill. */
            return(objResult);
        }

        /*--------------------------------------------------------------------*/
        /* Nst_Copy:                                                          */
        /*                                                                    */
        /* The Copy step: copy the captured items -- out of the image (an     */
        /* extract, or a drag out to Explorer), into it (an import), or       */
        /* within it -- through ENGS_DoCopy, with the command's questions,    */
        /* progress and Cancel in place of a progress window.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the items (RQT_lstPaths) and their other ends   */
        /*                    and options (a CCA_clsCopyArgs payload).        */
        /*     objExecution : the running command's services.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the copy's worst outcome with its result       */
        /*                     (counts, problem lines, notes); ENG_ERR_ARG    */
        /*                     for an incomplete request; SYS_INTERNAL for a  */
        /*                     callback failure.                              */
        /*--------------------------------------------------------------------*/
        private static RSL_clsResult Nst_Copy(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            CCA_clsCopyArgs objArgs;
            ECR_clsCopyResult objResult;
            ECO_clsCopyControl objControl;
            int iKind;
            string sFailure;

            /* The copy's arguments, and the pairs they make: every item needs   */
            /* its other end, one for one, and there must be at least one.       */
            objArgs = objRequest.RQT_objPayload as CCA_clsCopyArgs;
            if (objArgs == null || objRequest.RQT_lstPaths.Count == 0 ||
                objRequest.RQT_lstPaths.Count != objArgs.CCA_lstOtherPaths.Count)
            {   /* Incomplete: refused before anything is copied.  Nothing is   */
                /* taken from the panes to fill a gap -- the request is the     */
                /* whole of what was asked.                                     */
                return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                    "Copy selection lengths differ or are empty.", null));
            }

            /* The direction, from the request's kind: out of the image unless   */
            /* it is an import or a copy within.  An extract and a drag out to   */
            /* Explorer (DragExtract, CommandOperations.cs) both go out.         */
            iKind = ENGS_clsSession.ENGS_COPY_EXTRACT;
            if (objRequest.RQT_enmKind == KND_enmKind.Import)
            {   /* Windows items into the image. */
                iKind = ENGS_clsSession.ENGS_COPY_IMPORT;
            }
            else if (objRequest.RQT_enmKind == KND_enmKind.CopyWithin)
            {   /* Image items to another place in the same image. */
                iKind = ENGS_clsSession.ENGS_COPY_WITHIN;
            }

            /* The copy.  The control has no window: it is bound to the         */
            /* command, so its events go to the native control -- conflicts     */
            /* asked through the command's question channel, progress           */
            /* published, the command's Cancel and Exit answered.  The helper   */
            /* makes one engine call per item, all sharing one "All" answer,    */
            /* and takes arrays, so the lists are converted once here; the      */
            /* option structures are passed by value.                           */
            objResult = Nst_CopyResult();
            objControl = new ECO_clsCopyControl(objResult, objExecution);
            ENGS_clsSession.ENGS_DoCopy(iKind, objRequest.RQT_lstPaths.ToArray(),
                objArgs.CCA_lstOtherPaths.ToArray(), objArgs.CCA_stExtract,
                objArgs.CCA_stImport, objControl, objResult);
            if (objControl.ECO_ReadFailure(out sFailure))
            {   /* A callback failed and made the engine cancel: the failure   */
                /* is reported, not a cancel, with the counts so far.          */
                return(new RSL_clsResult(SYS_clsSystem.SYS_INTERNAL, sFailure, objResult));
            }

            /* The helper's outcome -- the worst over the items: a failed     */
            /* commit, else out of memory (the list stopped there), else      */
            /* cancelled, else partial, else success -- with its result for   */
            /* the report.                                                    */
            /* After an import or a copy within, the helper has flushed the   */
            /* image even after a cancel, so what was copied is committed.    */
            return(new RSL_clsResult(objResult.ECR_iStatus, objResult.ECR_sDetail, objResult));
        }

        /*--------------------------------------------------------------------*/
        /* Nst_Raw:                                                           */
        /*                                                                    */
        /* The CopyRaw step: a device read to a file, an image written to a   */
        /* device, or one partition's read, write or erase, through           */
        /* ENGS_DoCopyRaw; then, when a hash was asked for and the copy       */
        /* succeeded, the image file's SHA-256 written beside it as           */
        /* "<name>.sha256" -- for a device write, over an existing one only   */
        /* when the user agrees.                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the flags (RQT_uiFlags) and the copy's ends,    */
        /*                    offsets, length and hash wish (a                */
        /*                    RWA_clsRawArgs payload).                        */
        /*     objExecution : the running command's services.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the copy's outcome with its result (the hash,  */
        /*                     and a warning when the sum's file could not be */
        /*                     written); ENG_ERR_ARG for an incomplete        */
        /*                     request, or a file end the engine finds to be  */
        /*                     a disk or a volume; SYS_INTERNAL for a         */
        /*                     callback failure.                              */
        /*--------------------------------------------------------------------*/
        private static RSL_clsResult Nst_Raw(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            RWA_clsRawArgs objArgs;
            ECR_clsCopyResult objResult;
            ECO_clsCopyControl objControl;
            DVI_stDeviceInfo stInfo;
            ANS_clsAnswer objAnswer;
            string sHashFile;
            string sSumFile;
            string sFileEnd;
            string sFailure;
            uint uiFlags;
            bool bWriteSum;

            /* The copy's arguments: a destination always, and a source unless   */
            /* the copy is an erase (ENG_RAW_ZERO_SOURCE), which writes zeros    */
            /* from no source.  Only completeness is tested here: whether the    */
            /* two ends are the same object, the system disk, or too small is    */
            /* the engine's to refuse.                                           */
            objArgs = objRequest.RQT_objPayload as RWA_clsRawArgs;
            if (objArgs == null || String.IsNullOrEmpty(objArgs.RWA_sTo) ||
                (String.IsNullOrEmpty(objArgs.RWA_sFrom) &&
                    (objRequest.RQT_uiFlags & ENG_clsNative.ENG_RAW_ZERO_SOURCE) == 0))
            {   /* Incomplete: refused before either end is opened, and no   */
                /* end is taken from the picker or the panes to fill it.     */
                return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                    "No complete raw-copy target pair.", null));
            }

            /* The end that must be a Windows FILE: the destination of a read    */
            /* (a whole device's or a partition's), the source of a write.  An   */
            /* erase, and the zeros after a partition write, have no file end    */
            /* ("").                                                             */
            sFileEnd = "";
            if (objRequest.RQT_enmKind == KND_enmKind.ReadDevice ||
                objRequest.RQT_enmKind == KND_enmKind.ReadPartition)
            {   /* A read: the file the bytes go into. */
                sFileEnd = objArgs.RWA_sTo;
            }
            else if (objRequest.RQT_enmKind == KND_enmKind.WriteDevice ||
                objRequest.RQT_enmKind == KND_enmKind.WritePartition)
            {   /* A write: the file the bytes come from. */
                sFileEnd = objArgs.RWA_sFrom;
            }

            /* The file end must BE a file, judged by what the engine finds      */
            /* when it opens the path, not by how the path is spelled.  The      */
            /* sequences refuse the usual device spellings already               */
            /* (CommandRawSequence.cs), but Windows reaches a device by others   */
            /* too (\\?\D:, say), and the engine holds the two ends only to      */
            /* being different objects and the destination to not being the      */
            /* system disk: a read whose "image file" was a volume would copy    */
            /* the card raw over it.  A path that cannot be opened at all is     */
            /* no device -- a read's new file does not exist yet -- and the      */
            /* copy itself then reports whatever is wrong with it.  The facts    */
            /* need no Administrator and no session (bifrost_engine.h,           */
            /* BFE_DeviceInfoGet).                                               */
            if (sFileEnd.Length != 0 &&
                ENG_clsNative.ENG_pfnDeviceInfoGet(sFileEnd, out stInfo) == ENG_clsNative.ENG_OK &&
                stInfo.DVI_uiDevice != 0)
            {   /* The engine opened it as a disk or a volume: refused before   */
                /* either end is opened for the copy, nothing written.          */
                return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                    sFileEnd + " is a disk or a volume, not a Windows file; choose a file.", null));
            }

            /* The flags as captured, and for an image written to a device the   */
            /* read-back forced on whatever they say: a device write is always   */
            /* verified, apart from the Verify Writes option, which is for an    */
            /* open session's writes.  (CommandRawSequence.cs sets it for that   */
            /* write and for a partition write already; this keeps the device    */
            /* write's verify from resting on the request alone.)                */
            uiFlags = objRequest.RQT_uiFlags;
            if (objRequest.RQT_enmKind == KND_enmKind.WriteDevice)
            {   /* An image to a device: read back and compared. */
                uiFlags |= ENG_clsNative.ENG_RAW_VERIFY;
            }

            /* Which file is hashed, when one is: the image FILE, compressed   */
            /* or not, since that is what a published sum is a sum of -- the   */
            /* source of a device write, the destination of a device read.     */
            /* "" means no hash.  Only those two copies ask for one: every     */
            /* partition read, write and erase passes no hash                  */
            /* (CommandRawSequence.cs), and the test below relies on that --   */
            /* for a partition read it would name the device, its source, as   */
            /* the file to hash.                                               */
            sHashFile = "";
            if (objArgs.RWA_bHash)
            {   /* A hash wanted: the source, unless the copy made the file. */
                sHashFile = objArgs.RWA_sFrom;
                if (objRequest.RQT_enmKind == KND_enmKind.ReadDevice)
                {   /* A device read: the file it wrote. */
                    sHashFile = objArgs.RWA_sTo;
                }
            }

            /* The copy, and then the hash, through the helper, with a control   */
            /* bound to the command as Nst_Copy's is.  The helper computes the   */
            /* sum only after a successful copy; a Cancel during the hash ends   */
            /* the hash alone and leaves the copy's success standing, with no    */
            /* sum; a hash that fails is logged, with no sum either.             */
            objResult = Nst_CopyResult();
            objControl = new ECO_clsCopyControl(objResult, objExecution);
            ENGS_clsSession.ENGS_DoCopyRaw(objArgs.RWA_sFrom, objArgs.RWA_ullFrom,
                objArgs.RWA_sTo, objArgs.RWA_ullTo, objArgs.RWA_ullLength,
                uiFlags, sHashFile, objControl, objResult);
            if (objResult.ECR_iStatus == ENG_clsNative.ENG_OK && objResult.ECR_sHash.Length != 0)
            {   /* A successful copy with its sum: the sum saved beside the      */
                /* image file as "<name>.sha256", in the usual "<sum> *<name>"   */
                /* form, so any tool can check the file against it later.        */
                /* It is written here, in the step, so it belongs to this        */
                /* copy's own outcome.                                           */
                sSumFile = sHashFile + ".sha256";
                bWriteSum = true;
                if (objRequest.RQT_enmKind == KND_enmKind.WriteDevice && File.Exists(sSumFile))
                {   /* A device WRITE, and a checksum file of that name already   */
                    /* beside its source image -- after a download, very often    */
                    /* the publisher's own sum, the one the image is checked      */
                    /* against.  Replaced only when the user says so (over the    */
                    /* copy's progress window); any other answer, or none,        */
                    /* keeps it.  A device READ is not asked: its sum is of the   */
                    /* file it has just written, which replaced whatever that     */
                    /* checksum file described.                                   */
                    objAnswer = objExecution.EXC_Ask(QNK_enmQuestionKind.ReplaceChecksum,
                        objRequest.RQT_lOrigin, sSumFile);
                    if (objAnswer == null || objAnswer.ANS_enmKind != ANK_enmAnswerKind.Yes)
                    {   /* Kept: no file written, and the result says why, so   */
                        /* the report shows the sum without claiming it was     */
                        /* saved.                                               */
                        bWriteSum = false;
                        objResult.ECR_sWarning = "The existing checksum file " + sSumFile +
                            " was kept; the sum above was not saved.";
                    }
                }

                if (bWriteSum)
                {   /* No checksum file there, a device read, or a Yes. */
                    try
                    {   /* The sum's file, replacing any of that name (asked   */
                        /* above for a device write).                          */
                        File.WriteAllText(sSumFile, objResult.ECR_sHash +
                            " *" + Path.GetFileName(sHashFile) + "\n");
                    }
                    catch (Exception exSidecar)
                    {   /* Not written: the copy and its sum stand, and the   */
                        /* reason goes with the result as a warning, which    */
                        /* the report shows in place of the file's name.      */
                        objResult.ECR_sWarning = "The checksum file could not be written: " + exSidecar.Message;
                    }
                }
            }

            if (objControl.ECO_ReadFailure(out sFailure))
            {   /* A callback failed and made the engine cancel: the failure   */
                /* is reported, not a cancel.  (A cancelled copy is not a      */
                /* success, so no sum's file was written above.)               */
                return(new RSL_clsResult(SYS_clsSystem.SYS_INTERNAL, sFailure, objResult));
            }

            /* The copy's outcome, with its result: the sum when one was       */
            /* computed, and the warning when its file could not be written.   */
            return(new RSL_clsResult(objResult.ECR_iStatus, objResult.ECR_sDetail, objResult));
        }

        /*--------------------------------------------------------------------*/
        /* Nst_Format:                                                        */
        /*                                                                    */
        /* The MakeFilesystem step: make an empty filesystem in the target's  */
        /* range through ENGS_DoMakeFsAt, zeroing the range first when the    */
        /* options ask, with the zero-fill's progress and Cancel going        */
        /* through the command.                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the target range (RQT_objTarget) and the        */
        /*                    format's options (a PAA_clsPartitionArgs        */
        /*                    payload).                                       */
        /*     objExecution : the running command's services.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the format's outcome with its ESR_clsResult;   */
        /*                     ENG_ERR_ARG with no options; SYS_INTERNAL for  */
        /*                     a callback failure.                            */
        /*--------------------------------------------------------------------*/
        private static RSL_clsResult Nst_Format(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            PAA_clsPartitionArgs objArgs;
            ESR_clsResult objResult;
            ECO_clsCopyControl objControl;
            string sFailure;

            /* The options, captured -- type, block size, label, zero-fill --   */
            /* when the request was made, so nothing is read from a dialog      */
            /* that may since have changed.                                     */
            objArgs = objRequest.RQT_objPayload as PAA_clsPartitionArgs;
            if (objArgs == null)
            {   /* None: refused, nothing written.  A format's settings are   */
                /* never taken from a window.                                 */
                return(new RSL_clsResult(ENG_clsNative.ENG_ERR_ARG,
                    "No immutable format settings supplied.", null));
            }

            /* The format.  The control is bound to the command, so a zero-      */
            /* fill's progress and Cancel go through it; without the zero-fill   */
            /* the engine sends no event and the control is never called.  Its   */
            /* copy result only receives the zero-fill's counts, which nothing   */
            /* reads.  The target exists: NST_Run refuses a request without.     */
            objResult = new ESR_clsResult();
            objControl = new ECO_clsCopyControl(Nst_CopyResult(), objExecution);
            ENGS_clsSession.ENGS_DoMakeFsAt(objRequest.RQT_objTarget.TGT_sPath,
                objRequest.RQT_objTarget.TGT_ullOffset, objRequest.RQT_objTarget.TGT_ullLength,
                objArgs.PAA_stOptions, objControl, objResult);
            if (objControl.ECO_ReadFailure(out sFailure))
            {   /* A callback failed and made the engine cancel: the failure   */
                /* is reported, not a cancel.                                  */
                return(new RSL_clsResult(SYS_clsSystem.SYS_INTERNAL, sFailure, objResult));
            }

            /* The format's outcome, with its result (status and detail). */
            return(new RSL_clsResult(objResult.ESR_iStatus, objResult.ESR_sDetail, objResult));
        }

        /*--------------------------------------------------------------------*/
        /* Nst_Check:                                                         */
        /*                                                                    */
        /* The Check and Repair steps: run e2fsck over the target's range     */
        /* through ENGS_DoCheckFs, its lines and progress going through the   */
        /* command.  For Expand Filesystem the verdict is also turned into    */
        /* what the expansion may do next: a repair first, a resize, or stop. */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the target range (RQT_objTarget); its kind      */
        /*                    tells a plain check from an expansion's.        */
        /*     objExecution : the running command's services.                 */
        /*     bRepair      : true to repair what is found; false to report   */
        /*                    only.                                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the check's outcome with its report; for an    */
        /*                     expansion, success carries an                  */
        /*                     RPN_clsRepairNeeded around the report, and     */
        /*                     ENG_ERR_NEEDS_CHECK says the expansion must    */
        /*                     stop; SYS_INTERNAL for a callback failure.     */
        /*--------------------------------------------------------------------*/
        private static RSL_clsResult Nst_Check(RQT_clsRequest objRequest,
            EXC_clsExecution objExecution, bool bRepair)
        {
            ECK_clsCheckResult objResult;
            ECC_clsCheckControl objControl;
            RSL_clsResult objOutcome;
            string sFailure;

            /* The check.  Its line list is made first: the control adds each   */
            /* of e2fsck's lines to it as they come.  The control is bound to   */
            /* the command, so progress and Cancel go through it.  Then the     */
            /* outcome as the helper reports it -- unless a callback failed     */
            /* and made the engine cancel, when the failure is reported         */
            /* instead, with the lines so far.                                  */
            objResult = new ECK_clsCheckResult();
            objResult.ECK_lstLines = new List<string>();
            objControl = new ECC_clsCheckControl(objResult, objExecution);
            ENGS_clsSession.ENGS_DoCheckFs(objRequest.RQT_objTarget.TGT_sPath,
                objRequest.RQT_objTarget.TGT_ullOffset, objRequest.RQT_objTarget.TGT_ullLength,
                bRepair, objControl, objResult);
            objOutcome = new RSL_clsResult(objResult.ECK_iStatus, objResult.ECK_sDetail, objResult);
            if (objControl.ECC_ReadFailure(out sFailure))
            {   /* A callback failed: that failure, with the report kept. */
                objOutcome = new RSL_clsResult(SYS_clsSystem.SYS_INTERNAL, sFailure, objResult);
            }

            if (objRequest.RQT_enmKind != KND_enmKind.ExpandFilesystem ||
                objOutcome.RSL_iStatus != ENG_clsNative.ENG_OK)
            {   /* A plain check, whose verdict is in its report; or a check   */
                /* that did not run, which ends an expansion as it is          */
                /* (CommandOperations.cs).  Either way the outcome as it is.   */
                return(objOutcome);
            }

            if ((objResult.ECK_uiExitCode &
                (ENG_clsNative.ENG_CHECK_ERROR | ENG_clsNative.ENG_CHECK_CANCELLED)) != 0 ||
                (bRepair && objResult.ECK_bErrorsLeft))
            {   /* An expansion's check that ran but is no basis for a resize:   */
                /* e2fsck itself failed or was stopped (its exit bits, though    */
                /* the call succeeded), or the repair left errors.  The          */
                /* expansion stops here, with the report.                        */
                return(new RSL_clsResult(ENG_clsNative.ENG_ERR_NEEDS_CHECK,
                    "The check did not leave the filesystem suitable for expansion.", objResult));
            }

            /* A sound check: success, with whether a repair must come first.   */
            /* A report-only check can succeed and still have found errors,     */
            /* and then the expansion runs the Repair step before the resize    */
            /* (CommandOperations.cs); after a repair this reads false.         */
            return(new RSL_clsResult(ENG_clsNative.ENG_OK, "",
                new RPN_clsRepairNeeded(objResult.ECK_bErrorsLeft, objResult)));
        }

        /*--------------------------------------------------------------------*/
        /* Nst_Resize:                                                        */
        /*                                                                    */
        /* The Resize step: grow the filesystem in the target's range to fill */
        /* it, through ENGS_DoResizeFs, which checks and repairs it once      */
        /* first when the engine asks for that.                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the target range (RQT_objTarget).               */
        /*     objExecution : the running command's services, looked at       */
        /*                    before the helper's retry and inside its        */
        /*                    repair.                                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the resize's outcome with its result (the      */
        /*                     block counts, and every line of the resize and */
        /*                     of any repair).                                */
        /*--------------------------------------------------------------------*/
        private static RSL_clsResult Nst_Resize(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            ERS_clsResizeResult objResult;

            /* The resize, with its line list made first (the helper adds the   */
            /* resize's and any repair's lines to it).  Check-first is on: an   */
            /* expansion's report-only check writes nothing, so it records no   */
            /* check in the superblock, and the engine refuses a filesystem     */
            /* last checked before it was last mounted (BFE_ERR_NEEDS_CHECK,    */
            /* bfe_resize.c) -- the helper then repairs it once and tries       */
            /* again, looking at the command's Cancel and Exit before the       */
            /* retry and inside the repair.                                     */
            objResult = new ERS_clsResizeResult();
            objResult.ERS_lstLines = new List<string>();
            ENGS_clsSession.ENGS_DoResizeFs(objRequest.RQT_objTarget.TGT_sPath,
                objRequest.RQT_objTarget.TGT_ullOffset, objRequest.RQT_objTarget.TGT_ullLength,
                true, objResult, objExecution);

            /* The resize's outcome, with its result.  A resize once begun is   */
            /* not stopped -- its callback only collects text -- so a Cancel    */
            /* or Exit asked during it is seen at the next checkpoint.          */
            return(new RSL_clsResult(objResult.ERS_iStatus, objResult.ERS_sDetail, objResult));
        }

        /*--------------------------------------------------------------------*/
        /* Nst_Capacity:                                                      */
        /*                                                                    */
        /* The TestCapacity step: the engine's test of how much of a device   */
        /* really holds data, through ENGS_DoTestCapacity, its steps and      */
        /* Cancel going through the command.                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the whole device (RQT_objTarget).               */
        /*     objExecution : the running command's services.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the test's outcome with its result (what was   */
        /*                     found to hold, and the counterfeit verdict);   */
        /*                     SYS_INTERNAL for a callback failure.           */
        /*--------------------------------------------------------------------*/
        private static RSL_clsResult Nst_Capacity(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            ECP_clsCapacityResult objResult;
            ECO_clsCopyControl objControl;
            string sFailure;

            /* The test.  The control is bound to the command, as Nst_Copy's      */
            /* is: the engine reports its steps through it ("test" events), and   */
            /* a Cancel stops the test once the engine has put back what it       */
            /* wrote.  Its copy result only receives the step counts, which       */
            /* nothing reads.  The refusals -- a system disk, an open session     */
            /* -- are the engine's.                                               */
            objResult = new ECP_clsCapacityResult();
            objControl = new ECO_clsCopyControl(Nst_CopyResult(), objExecution);
            ENGS_clsSession.ENGS_DoTestCapacity(objRequest.RQT_objTarget.TGT_sPath, objControl, objResult);
            if (objControl.ECO_ReadFailure(out sFailure))
            {   /* A callback failed and made the engine cancel: the failure   */
                /* is reported, not a cancel.                                  */
                return(new RSL_clsResult(SYS_clsSystem.SYS_INTERNAL, sFailure, objResult));
            }

            /* The test's outcome, with what it measured. */
            return(new RSL_clsResult(objResult.ECP_iStatus, objResult.ECP_sDetail, objResult));
        }

        /*--------------------------------------------------------------------*/
        /* Nst_OpenCreatedEntry:                                              */
        /*                                                                    */
        /* The read-out for a file this command has just created (New File,   */
        /* to be opened at once): its content is already in a stage file --   */
        /* the one the create imported -- so that file is handed to the       */
        /* editor instead of being read out of the image again, which for a   */
        /* large zero-filled new file would be a second long copy.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the open, naming the new file's image path.       */
        /*     objCreated : the create's arguments, holding the stage; its    */
        /*                  content is committed to the image.                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : ENG_OK with an OED record owning the stage     */
        /*                     file.  A failure to classify it throws, with   */
        /*                     the file deleted (or, when it will not go,     */
        /*                     logged and left).                              */
        /*--------------------------------------------------------------------*/
        private static RSL_clsResult Nst_OpenCreatedEntry(RQT_clsRequest objRequest, EDA_clsEditArgs objCreated)
        {
            STG_clsStage objStage;
            OED_clsOpenedEntryData objData;
            bool bTransferred;
            int iKind;
            string sWhy;
            string sPath;

            /* The create's stage handed on to a new token: the create's own   */
            /* release at the request's end (NST_Release) then deletes         */
            /* nothing, and this token is the file's only owner.  A token      */
            /* already used throws here, with nothing owned to clean up.       */
            /* Nothing is handed to the record yet.                            */
            objStage = objCreated.EDA_objStage.STG_Transfer();
            bTransferred = false;
            try
            {   /* Until the record takes the token, the finally below owns     */
                /* the file and deletes it on any failure.                      */
                /* The file's content classified (its start is read), and the   */
                /* record made with the image path and the size on disk; the    */
                /* record owns the token from here.                             */
                sPath = objStage.STG_ReadPath();
                iKind = OSC_clsScanner.OSC_Classify(sPath, out sWhy);
                objData = new OED_clsOpenedEntryData(objStage, objRequest.RQT_lstPaths[0],
                    new FileInfo(sPath).Length, iKind, sWhy);
                bTransferred = true;

                /* The record, now the opening sequence's to own. */
                return(new RSL_clsResult(ENG_clsNative.ENG_OK, "", objData));
            }
            finally
            {   /* Every exit: the file deleted only if the record never took   */
                /* it.                                                          */
                if (!bTransferred)
                {   /* Classifying or measuring failed: no editor opens, and   */
                    /* the read-out file goes.  The image's new file stays.    */
                    try
                    {   /* The deletion. */
                        objStage.STG_Release();
                    }
                    catch (Exception exRelease)
                    {   /* It would not go: logged and left, so the failure     */
                        /* that brought the program here is the one reported,   */
                        /* not the deletion's.                                  */
                        if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                        {   /* Only while the debug log is kept: otherwise the line is never built. */
                            DBGLOG_clsDebugLog.DBGLOG_Write("read-out stage cleanup", exRelease.ToString());
                        }
                    }
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* Nst_WritePlaceholder:                                              */
        /*                                                                    */
        /* The writer of a read-out's stage file (a STG_WriteContent): the    */
        /* file is made empty, for BFE_Extract to write into.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stmOutput : the new stage file's stream, lent by               */
        /*                 STG_WriteSnapshot, which flushes and closes it.    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the file's length is 0.                                 */
        /*--------------------------------------------------------------------*/
        private static void Nst_WritePlaceholder(FileStream stmOutput)
        {
            /* Empty.  The file was just created new, so it is empty already;   */
            /* setting the length says so rather than leaving it assumed.       */
            stmOutput.SetLength(0);
        }

        /*--------------------------------------------------------------------*/
        /* Nst_ExtractEntry:                                                  */
        /*                                                                    */
        /* The ExtractEntry step: read one image file out into a stage file   */
        /* of its own, for an editor, and classify its content.  Refused      */
        /* unless the path names a regular file.  A file this command has     */
        /* just created is not read out again (Nst_OpenCreatedEntry).  On     */
        /* success the stage file passes to the result's OED record; on every */
        /* failure it is deleted here (one that will not go is logged and     */
        /* left, never reported in place of the read-out's own result).       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the open, naming the entry (RQT_lstPaths[0])    */
        /*                    and, after a New File, carrying its create.     */
        /*     objExecution : the running command's services.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : ENG_OK with the OED record; the stat's or the  */
        /*                     extract's failure, its detail read at once;    */
        /*                     ENG_ERR_TYPE for anything but a regular file;  */
        /*                     SYS_INTERNAL for a callback failure.  A stage  */
        /*                     that cannot be made, or content that cannot be */
        /*                     classified, throws.                            */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Nst_ExtractEntry(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            EEN_stEntry stEntry;
            EXO_stExtractOptions stOptions;
            ENG_CopyCallback pfnEvent;
            ECR_clsCopyResult objResult;
            NCB_clsNativeControl objControl;
            XEC_clsExtractCallback objCallback;
            EDA_clsEditArgs objCreated;
            STG_clsStage objStage;
            OED_clsOpenedEntryData objData;
            int iStatus;
            int iKind;
            string sWhy;
            string sPath;
            string sFailure;
            bool bTransferred;
            bool bBusy;

            /* A New File's open: the request carries the create              */
            /* (Sys_OperationSequence builds it after a successful create,    */
            /* CommandOperations.cs), and the create's stage already holds    */
            /* the content.  An ordinary open carries no create and goes on   */
            /* to the read-out below.                                         */
            objCreated = objRequest.RQT_objPayload as EDA_clsEditArgs;
            if (objCreated != null && objCreated.EDA_enmKind == EDK_enmEditKind.CreateFile &&
                objCreated.EDA_objStage != null)
            {   /* The new file's stage handed on; nothing is read out. */
                return(Nst_OpenCreatedEntry(objRequest, objCreated));
            }

            /* The entry as it is now, before any file is made: BFE_Stat does   */
            /* not follow a symbolic link, so a link reads as a link.           */
            iStatus = ENG_clsNative.ENG_pfnStat(
                ENG_clsNative.ENG_Utf8(objRequest.RQT_lstPaths[0]), out stEntry);
            if (iStatus != ENG_clsNative.ENG_OK)
            {   /* Gone, or unreadable: the failure, with the engine's detail   */
                /* read now, before another engine call can replace it.         */
                return(Nst_Status(iStatus, null));
            }

            if (stEntry.EEN_uiType != ENG_clsNative.ENG_TYPE_FILE)
            {   /* A directory, a link or a special file: no editor opens   */
                /* one, and nothing has been made.                          */
                return(new RSL_clsResult(ENG_clsNative.ENG_ERR_TYPE,
                    "Only a regular file can open in an editor.", null));
            }

            /* The "working" box (a moving bar, no Cancel) for a large file,   */
            /* from the size just read -- not a pane's row, which may be out   */
            /* of date.  At PGV_ENTRY_BUSY_BYTES or more the read-out takes    */
            /* long enough to need it; a smaller file is read out without.     */
            bBusy = stEntry.EEN_ullSize >= PGV_clsProgressView.PGV_ENTRY_BUSY_BYTES;
            if (bBusy)
            {   /* Shown, addressed to this command.  EXC_Show does not wait   */
                /* for the window: the read-out goes on at once.               */
                objExecution.EXC_Show(VWK_enmViewKind.ProgressOpen,
                    new PGV_clsProgressView(true, "Opening",
                        "Reading " + objRequest.RQT_lstPaths[0] + " out of the image ..."));
            }

            /* No stage yet, and nothing handed on: the finally below tests   */
            /* both, so a failure before the stage exists still closes the    */
            /* box and deletes nothing.                                       */
            objStage = null;
            bTransferred = false;
            try
            {   /* Until the record takes the stage, this function owns the       */
                /* stage file, and the finally deletes it on any failure.         */
                /* The stage file, made empty -- by the stage procedure, which    */
                /* makes every stage file the same way: a new GUID name made      */
                /* with CreateNew, flushed, closed, and owned by its token from   */
                /* the moment it exists.  Its own failure deletes its partial     */
                /* file and throws, leaving objStage null.                        */
                objStage = STG_clsStage.STG_WriteSnapshot(Nst_sStageRoot,
                    new STG_WriteContent(Nst_WritePlaceholder));
                sPath = objStage.STG_ReadPath();

                /* The read-out's callback: the native control, handling the    */
                /* events as a copy's are, and the frame that answers the one   */
                /* expected conflict -- the stage file -- and hands it the      */
                /* rest.  No manifest and no times (the options zeroed): an     */
                /* editor needs only the bytes.                                 */
                stOptions = new EXO_stExtractOptions();
                objResult = Nst_CopyResult();
                objControl = new NCB_clsNativeControl(objExecution, objResult, null);
                objCallback = new XEC_clsExtractCallback(sPath, objExecution, objControl);
                pfnEvent = new ENG_CopyCallback(objCallback.XEC_OnEvent);
                try
                {   /* The read-out, one file into the stage file.  The engine   */
                    /* holds the delegate for the whole call.                    */
                    iStatus = ENG_clsNative.ENG_pfnExtract(
                        ENG_clsNative.ENG_Utf8(objRequest.RQT_lstPaths[0]),
                        sPath, ref stOptions, pfnEvent, IntPtr.Zero);
                }
                finally
                {   /* The delegate kept alive past the call, however it       */
                    /* ended: once marshalled, only the engine refers to it,   */
                    /* and the collector could otherwise free it while the     */
                    /* engine still calls it back.                             */
                    GC.KeepAlive(pfnEvent);
                }

                if (objControl.NCB_ReadFailure(out sFailure))
                {   /* A callback failed and made the engine cancel: that   */
                    /* failure is reported, not a cancel.                   */
                    return(new RSL_clsResult(SYS_clsSystem.SYS_INTERNAL, sFailure, objResult));
                }

                if (iStatus != ENG_clsNative.ENG_OK)
                {   /* Failed or cancelled: no editor opens.  The engine's   */
                    /* detail is read now (Nst_Status), and the finally      */
                    /* deletes the stage file.                               */
                    return(Nst_Status(iStatus, objResult));
                }

                /* The content classified (its start is read), and the record   */
                /* made with the image path and the size on disk.  The record   */
                /* owns the stage from here, so the finally leaves it.          */
                iKind = OSC_clsScanner.OSC_Classify(sPath, out sWhy);
                objData = new OED_clsOpenedEntryData(objStage, objRequest.RQT_lstPaths[0],
                    new FileInfo(sPath).Length, iKind, sWhy);
                bTransferred = true;

                /* The record, now the opening sequence's to own. */
                return(new RSL_clsResult(ENG_clsNative.ENG_OK, "", objData));
            }
            finally
            {   /* Every exit: the stage file deleted unless the record took   */
                /* it, and the box closed.                                     */
                try
                {   /* The deletion first, in a try of its own, so the box's   */
                    /* close below runs whatever it does.                      */
                    if (!bTransferred && objStage != null)
                    {   /* A stage made and not handed on: deleted.  A file   */
                        /* the engine removed already (a failed copy) is no   */
                        /* error to delete.                                   */
                        objStage.STG_Release();
                    }
                }
                catch (Exception exRelease)
                {   /* The stage file would not go (another program holding it   */
                    /* open, say): logged, and left behind.  The failure is      */
                    /* NOT passed on -- thrown from a finally it would replace   */
                    /* the read-out's own result (a cancel, a failure) with an   */
                    /* internal error.  The opening sequence keeps its own       */
                    /* outcome over its release's failure the same way           */
                    /* (Sys_OpenEntrySequence, CommandOperations.cs), though     */
                    /* it shows that failure as a warning; a leftover stage      */
                    /* file here is only logged.                                 */
                    if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                    {   /* Only while the debug log is kept: otherwise the line is never built. */
                        DBGLOG_clsDebugLog.DBGLOG_Write("read-out stage cleanup", exRelease.ToString());
                    }
                }
                finally
                {   /* The box closed whatever happened above, addressed to   */
                    /* this command as its open was.                          */
                    if (bBusy)
                    {   /* It was shown: closed. */
                        objExecution.EXC_Show(VWK_enmViewKind.ProgressClose, null);
                    }
                }
            }
        }
    }
}
