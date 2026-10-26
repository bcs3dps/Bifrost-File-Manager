/*----------------------------------------------------------------------------*/
/* EngineSession.cs:                                                          */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The program's calls into the engine DLL, one function per operation, and   */
/* the records they fill.  Three parts:                                       */
/*                                                                            */
/*   - the result records (ESR_ ... ERS_): what one operation hands back to   */
/*     the command that asked for it -- the engine's status, the engine's     */
/*     detail line on a failure, and the operation's own facts;               */
/*   - the three callback controls, ECC_clsCheckControl for a check,          */
/*     ECO_clsCopyControl for a copy, a raw copy, a format's zero-fill and a  */
/*     capacity test, and ERC_clsRemoveControl for a delete: the objects      */
/*     whose methods the engine calls back while the operation runs;          */
/*   - ENGS_clsSession, the operations themselves.                            */
/*                                                                            */
/* Every operation is synchronous: it runs the engine call (or calls) to the  */
/* end and fills its result before it returns.  They are called only by the   */
/* command worker's native steps (CommandNativeSteps.cs,                      */
/* CommandNativeOperations.cs), so the worker is the only thread that ever    */
/* calls the engine, and the engine's one session is never entered twice.     */
/* Nothing here starts a thread, keeps a queue or decides what runs next:     */
/* the order of operations is the command system's.                           */
/*                                                                            */
/* The engine's callbacks run on the worker too, inside the engine call that  */
/* raised them.  They reach the user only through the command's own channel   */
/* (NCB_clsNativeControl, CommandNativeControl.cs): progress, a question, a   */
/* Cancel or a confirmed Exit.                                                */
/*----------------------------------------------------------------------------*/

/* The framework: the base types (IntPtr, GC, Guid, the exceptions caught),     */
/* the lists the results hold, the image file read for a raw copy's checksum,   */
/* the marshalling of the engine's structures and the pinning of a partition    */
/* write's buffers, SHA-256 and the checksum's hex text.  The debug log         */
/* (DBGLOG_clsDebugLog), which records each engine failure, is in this          */
/* program's own namespace.                                                     */
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* THE RESULT RECORDS                                                     */
    /*                                                                        */
    /* One class per kind of answer.  Each carries its own status and detail  */
    /* fields rather than deriving from a shared base: the program's classes  */
    /* derive from nothing of their own, and every reader names the record    */
    /* it expects.  An operation fills its record before it returns; the      */
    /* native step hands it on to the command as the step's payload           */
    /* (RSL_clsResult, CommandContracts.cs), and from then on it is read,     */
    /* not changed.                                                           */
    /*------------------------------------------------------------------------*/

    /* The plain result, for an operation that hands back only its outcome:     */
    /* the format, the write-verify setting, a partition write, the close and   */
    /* every edit -- documentation table.                                       */
    internal sealed class ESR_clsResult
    {
        public int ESR_iStatus;      // ENG_OK or an ENG_ERR_* code (EngineNative.cs)
        public string ESR_sDetail;   // the engine's detail line on a failure; "" on success
        public string ESR_sWarning;  // a save that worked but left its old copy behind says where (ENGS_DoReplaceFile); null otherwise
    }

    /* An open's result.  What was asked for is filled in first, whatever   */
    /* happens, so a failure message can name the path and the range --     */
    /* documentation table.                                                 */
    internal sealed class EOR_clsOpenResult
    {
        public int EOR_iStatus;                // the open's status, or the facts read's when the open worked and that failed
        public string EOR_sDetail;             // the engine's detail line on a failure, with what the page holds when it is a filesystem the program does not open; "" on success
        public string EOR_sImagePath;          // the image file or device path asked for
        public ulong EOR_ullOffset;            // where the filesystem was asked to start, bytes
        public ulong EOR_ullLength;            // its length, bytes; 0 for "to the end"
        public bool EOR_bReadOnly;             // true when a read-only session was asked for
        public VLI_stVolumeInfo EOR_stVolume;  // the mounted filesystem's facts; to be used only when the status is ENG_OK
    }

    /* The open filesystem's facts read again, for Filesystem Properties,   */
    /* which must show the free space as it is now, not as it was at the    */
    /* open -- documentation table.                                         */
    internal sealed class EVR_clsVolumeResult
    {
        public int EVR_iStatus;                // the read's status
        public string EVR_sDetail;             // the engine's detail line on a failure; "" on success
        public VLI_stVolumeInfo EVR_stVolume;  // the facts; to be used only when the status is ENG_OK
    }

    /* What an image file is and how large the image inside it is, asked   */
    /* before Write Image's device picker -- documentation table.          */
    internal sealed class EIR_clsImageResult
    {
        public int EIR_iStatus;             // the inspection's status
        public string EIR_sDetail;          // the engine's detail line on a failure; "" on success
        public IFI_stImageInfo EIR_stInfo;  // the format and the two sizes; to be used only when the status is ENG_OK
    }

    /* One image directory's listing.  The entries keep the directory's own   */
    /* order: the pane sorts them -- documentation table.                     */
    internal sealed class ELR_clsListResult
    {
        public int ELR_iStatus;                   // the listing's status
        public string ELR_sDetail;                // the engine's detail line on a failure; "" on success
        public string ELR_sImageDir;              // the directory listed, so a completion can name it without its request
        public List<EEN_stEntry> ELR_lstEntries;  // the entries, made before the call so it is never null: empty after a refusal, the entries that came before a failure part way

        /*--------------------------------------------------------------------*/
        /* ELR_OnEntry:                                                       */
        /*                                                                    */
        /* The listing's callback (BFE_ListCallback): the engine calls it     */
        /* once for each entry, on the worker, inside ENGS_DoList's engine    */
        /* call.  It copies the entry into the list and lets the listing go   */
        /* on.                                                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pEntry    : the engine's BFE_Entry, valid only during this     */
        /*                 call.                                              */
        /*     pvContext : not used -- the delegate is bound to this record,  */
        /*                 which is all the state there is.                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : 0, to go on to the next entry.                           */
        /*--------------------------------------------------------------------*/
        internal int ELR_OnEntry(IntPtr pEntry, IntPtr pvContext)
        {
            /* Copied out now, whole, as a managed structure.  The engine       */
            /* passes the SAME native structure for every entry, refilled, so   */
            /* keeping the pointer would leave every row reading as the last    */
            /* entry.  Nothing here calls the engine: the listing holds its     */
            /* busy flag while it calls back, so any engine call made from      */
            /* here would be refused as busy.                                   */
            ELR_lstEntries.Add((EEN_stEntry)Marshal.PtrToStructure(pEntry, typeof(EEN_stEntry)));

            /* Go on: this callback never stops a listing, so a directory is   */
            /* listed whole unless the engine itself fails part way.           */
            return(0);
        }
    }

    /* A symbolic link's target, for the Properties dialog -- documentation   */
    /* table.                                                                 */
    internal sealed class ETR_clsTextResult
    {
        public int ETR_iStatus;     // the read's status
        public string ETR_sDetail;  // the engine's detail line, or this program's on a damaged size; "" on success
        public string ETR_sText;    // the whole target; "" on any failure, never a target cut short
    }

    /* One physical disk, as the device pickers list it -- documentation   */
    /* table.                                                              */
    internal sealed class DEV_clsDisk
    {
        internal const string DEV_PHYSICAL_PREFIX = @"\\.\PhysicalDrive";  // a whole disk's path before its number; CommandArguments.cs, CommandSequences.cs and CommandDeviceOpening.cs test a target's path against it, and Program.cs the --disk option's value
        public string DEV_sPath;                                           // the disk's path: the prefix and its number
        public int DEV_iNumber;                                            // Windows' number for the disk, from 0
        public DVI_stDeviceInfo DEV_stInfo;                                // the engine's facts: length, sector size, bus, model, read-only and system flags
    }

    /* The physical disks, for the device pickers -- documentation table. */
    internal sealed class EDV_clsDeviceResult
    {
        public int EDV_iStatus;                 // always ENG_OK: a number with no disk behind it is left out, not a failure
        public string EDV_sDetail;              // always ""
        public List<DEV_clsDisk> EDV_lstDisks;  // the disks in number order, possibly none; never null once ENGS_DoListDevices has run
    }

    /* A device's or image file's partitions, for a picker's choices          */
    /* (CommandDeviceOpening.cs).  The native step that fills it              */
    /* (CommandNativeSteps.cs, Nst_ReadDeviceTable) reads the table through   */
    /* CommandLayoutReader.cs, which keeps a failed read as a failure --      */
    /* documentation table.                                                   */
    internal sealed class ETB_clsTableResult
    {
        public int ETB_iStatus;                      // the read's status: set to ENG_OK by the native step, which returns a failed read as a failure of its own instead
        public string ETB_sDetail;                   // "" (set by the native step)
        public List<PRT_clsPartition> ETB_lstParts;  // the partitions in table order, possibly none
        public string ETB_sFound;                    // the parser's one-line account of what it found, shown by the picker
    }

    /* A file's or device's layout, for the partition window: the table, what   */
    /* each partition holds, and what the disk holds as a whole when it has     */
    /* no table.  Filled by the native step (CommandNativeSteps.cs) through     */
    /* CommandLayoutReader.cs, which keeps a failed read as a failure --        */
    /* documentation table.                                                     */
    internal sealed class ELY_clsLayoutResult
    {
        public int ELY_iStatus;            // the read's status: set to ENG_OK by the native step, which returns a failed read as a failure of its own instead
        public string ELY_sDetail;         // "" (set by the native step, which passes it on as the step's detail)
        public PTB_Table ELY_objTable;     // the table, made before the read so it is never null: kind "none" when there is none
        public string ELY_sAccount;        // the parser's one-line account of what it found
        public string ELY_sWholeContent;   // what the disk's first page holds (PTB_ContentText); "" when nothing matched or the disk is shorter than a page
        public ulong ELY_ullWholeFsBytes;  // an ext filesystem's own length from that page's superblock; 0 when it is not ext
    }

    /* A copy's result: an extract, an import or a copy within the image, a     */
    /* raw copy, a format's zero-fill and a capacity test all count into one.   */
    /* The caller makes it with its problem list (Nst_CopyResult,               */
    /* CommandNativeOperations.cs) and hands the SAME object to the copy        */
    /* control, whose callback counts each item into it as the engine reports   */
    /* it; the operation itself adds only what no event reported --             */
    /* documentation table.                                                     */
    internal sealed class ECR_clsCopyResult
    {
        public int ECR_iStatus;               // a list copy's worst outcome (a failed commit, else out of memory -- which stopped the list -- else cancelled, else partial, else ENG_OK); a raw copy's engine status
        public string ECR_sDetail;            // the engine's detail line for a failed commit, a list stopped by memory running out, or a raw copy; "" otherwise
        public string ECR_sWarning;           // a raw copy whose checksum file could not be written says so (CommandNativeOperations.cs); null otherwise
        public int ECR_iCopied;               // items finished (DONE events)
        public int ECR_iProblems;             // items skipped or failed: SKIPPED and ERROR events, conflicts answered "skip" (which raise no event), and items that failed before any event
        public List<string> ECR_lstProblems;  // one line per problem, at most ECO_PROBLEM_LINES of them, for the report
        public List<string> ECR_lstNotes;     // notes that are not problems (an _ext4meta.txt that is no manifest); null until the first
        public string ECR_sHash;              // a raw copy's image file's SHA-256, 64 lower-case hex digits; "" when none was made
    }

    /* A check's result: the engine's status, which says whether e2fsck could   */
    /* run, and e2fsck's own verdict, which says what it found.  The two are    */
    /* separate because a check that ran returns ENG_OK whatever it found --    */
    /* only a repair whose writes the device did not keep returns a failure     */
    /* (ENG_ERR_VERIFY) -- documentation table.                                 */
    internal sealed class ECK_clsCheckResult
    {
        public int ECK_iStatus;            // the engine's status for the check call
        public string ECK_sDetail;         // the engine's detail line on a failure; "" on success
        public uint ECK_uiExitCode;        // e2fsck's exit bits (ENG_CHECK_*, EngineNative.cs), with ENG_CHECK_CANCELLED when the callback stopped it
        public bool ECK_bModified;         // true when a repair wrote to the filesystem
        public bool ECK_bErrorsLeft;       // true when errors remain after the check or the repair
        public uint ECK_uiInodesUsed;      // inodes in use, as e2fsck's closing line counts them
        public uint ECK_uiInodesTotal;     // the filesystem's inodes
        public ulong ECK_ullBlocksUsed;    // blocks in use
        public ulong ECK_ullBlocksTotal;   // the filesystem's blocks
        public List<string> ECK_lstLines;  // every line of e2fsck's report in the order it came, made by the caller and filled by the callback
    }

    /* A capacity test's verdict: what the device claims and what it really   */
    /* holds -- documentation table.                                          */
    internal sealed class ECP_clsCapacityResult
    {
        public int ECP_iStatus;        // the test's status, including the putting back of what it overwrote
        public string ECP_sDetail;     // the engine's detail line on a failure; "" on success
        public ulong ECP_ullReported;  // the length the device reports, bytes
        public ulong ECP_ullGood;      // how much from the start really holds data, bytes
        public ulong ECP_ullFirstBad;  // the first byte found not to hold data; the reported length when none was
        public uint ECP_uiSamples;     // the test blocks written and read back
        public uint ECP_uiBadSamples;  // those that did not read back as written
        public bool ECP_bWraps;        // true when writes to high addresses landed on lower storage
        public bool ECP_bCounterfeit;  // true when the device holds less than it claims
    }

    /* A resize's result: the block counts before and after, and the text of   */
    /* the resize and of any check it needed first, in one list --             */
    /* documentation table.                                                    */
    internal sealed class ERS_clsResizeResult
    {
        public int ERS_iStatus;            // the resize's status, or the check's when a needed check failed
        public string ERS_sDetail;         // the engine's detail line, or this program's, on a failure; "" on success
        public ulong ERS_ullBlocksBefore;  // the filesystem's blocks before; 0 when it did not open
        public ulong ERS_ullBlocksAfter;   // its blocks after; the same as before when there was nothing to grow
        public uint ERS_uiBlockSize;       // its block size, bytes
        public List<string> ERS_lstLines;  // the resize's text and any check's, in the order it came; made by the caller

        /*--------------------------------------------------------------------*/
        /* ERS_OnResizeEvent:                                                 */
        /*                                                                    */
        /* The resize's callback (BFE_CheckCallback, the check's shape): the  */
        /* engine calls it on the worker, inside ENGS_DoResizeFs's engine     */
        /* call, for each line of text and each step of progress.  It keeps   */
        /* the text and lets the progress go: a resize takes seconds and      */
        /* shows no bar.                                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pEvent    : the engine's BFE_CheckEvent, valid only during     */
        /*                 this call.                                         */
        /*     pvContext : not used -- the delegate is bound to this record.  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : ENG_REPLY_CONTINUE.  The resize ignores its callback's   */
        /*           reply (bfe_resize.c casts it to void), so a resize once  */
        /*           begun cannot be stopped from here.                       */
        /*--------------------------------------------------------------------*/
        internal int ERS_OnResizeEvent(IntPtr pEvent, IntPtr pvContext)
        {
            /* The event read at BFE_CheckEvent's offsets in bifrost_engine.h   */
            /* (mirrored by hand: a change there moves these reads) -- its      */
            /* kind at 24, its text pointer at 16.                              */
            if (Marshal.ReadInt32(pEvent, 24) == (int)ENG_clsNative.ENG_CHECK_EVENT_TEXT)
            {   /* A line of text: copied out now, since its pointer dies when   */
                /* this call returns.                                            */
                ERS_lstLines.Add(ENG_clsNative.ENG_PtrToUtf8(Marshal.ReadIntPtr(pEvent, 16)));
            }

            /* Go on -- the only reply there is, since the resize never reads   */
            /* it.                                                              */
            return(ENG_clsNative.ENG_REPLY_CONTINUE);
        }
    }

    /*------------------------------------------------------------------------*/
    /* ECC_clsCheckControl -- one check's callback.                           */
    /*                                                                        */
    /* The object whose ECC_OnCheckEvent the engine calls back, on the worker */
    /* and inside the check's engine call, for each line of e2fsck's report   */
    /* and each step of a pass.  Made by the check's native step (Nst_Check,  */
    /* CommandNativeOperations.cs) and by a resize that needs a check first   */
    /* (ENGS_DoResizeFs), each time with the running command, so every event  */
    /* goes to the command's own control (NCB_clsNativeControl,               */
    /* CommandNativeControl.cs): it keeps the line in the result, shows the   */
    /* line and the pass, and answers Cancel or a confirmed Exit.             */
    /*------------------------------------------------------------------------*/
    internal sealed class ECC_clsCheckControl
    {
        private readonly NCB_clsNativeControl Ecc_objNativeControl;  // the command's control every event goes to; never null

        /*--------------------------------------------------------------------*/
        /* ECC_clsCheckControl (constructor):                                 */
        /*                                                                    */
        /* The control for one check, bound to the running command.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objResult    : the check's result, its line list already made. */
        /*     objExecution : the running command.                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : a control with nothing cancelled.              */
        /*--------------------------------------------------------------------*/
        internal ECC_clsCheckControl(ECK_clsCheckResult objResult, EXC_clsExecution objExecution)
        {
            /* Every event goes to the command's control, which is given the   */
            /* caller's result by reference, so the lines land in the          */
            /* caller's own list rather than a copy.                           */
            Ecc_objNativeControl = new NCB_clsNativeControl(objExecution, null, objResult);
        }

        /*--------------------------------------------------------------------*/
        /* ECC_ReadFailure:                                                   */
        /*                                                                    */
        /* After the check: whether a callback failed.  A callback that       */
        /* throws cannot let the exception unwind through e2fsck, so the      */
        /* command's control keeps the failure and tells the engine to        */
        /* cancel; the check then returns as cancelled, and this is how the   */
        /* caller tells that from the user's Cancel.                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sFailure : receives the failure's text; "" when there was      */
        /*                none.                                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when a callback failed (report sFailure, not the   */
        /*            check's status); false when the status stands.          */
        /*--------------------------------------------------------------------*/
        internal bool ECC_ReadFailure(out string sFailure)
        {
            /* The command's control keeps the first callback failure; it   */
            /* fills sFailure ("" when there was none).                     */
            return(Ecc_objNativeControl.NCB_ReadFailure(out sFailure));
        }

        /*--------------------------------------------------------------------*/
        /* ECC_OnCheckEvent:                                                  */
        /*                                                                    */
        /* The check's callback (BFE_CheckCallback): called by the engine on  */
        /* the worker, inside ENGS_DoCheckFs's engine call, for each line of  */
        /* e2fsck's report and each step of a pass.                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pEvent    : the engine's BFE_CheckEvent, valid only during     */
        /*                 this call.                                         */
        /*     pvContext : not used -- the delegate is bound to this object.  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : ENG_REPLY_CONTINUE, or ENG_REPLY_CANCEL to stop the      */
        /*           check.                                                   */
        /*--------------------------------------------------------------------*/
        internal int ECC_OnCheckEvent(IntPtr pEvent, IntPtr pvContext)
        {
            /* The command's control keeps the line, shows it or the pass,    */
            /* and gives the reply (CANCEL after a Cancel, a confirmed Exit   */
            /* or a failure of its own).                                      */
            return(Ecc_objNativeControl.NCB_ProcessCheckEvent(pEvent, pvContext));
        }
    }

    /*------------------------------------------------------------------------*/
    /* ECO_clsCopyControl -- one copy's callback.                             */
    /*                                                                        */
    /* The object whose ECO_OnCopyEvent the engine calls back, on the worker  */
    /* and inside the engine call, for an extract, an import or a copy within */
    /* the image (ENGS_DoCopy), a raw copy (ENGS_DoCopyRaw), a format's       */
    /* zero-fill (ENGS_DoMakeFsAt) and a capacity test (ENGS_DoTestCapacity). */
    /* Its other members: ECO_ShouldCancel and ECO_PostProgress, which those  */
    /* operations ask between engine calls (at each item, at each checksum    */
    /* chunk), and ECO_ReadFailure, which their callers ask afterwards.       */
    /*                                                                        */
    /* Every caller (CommandNativeOperations.cs) makes it with the running    */
    /* command, so each event goes to the command's own control               */
    /* (NCB_clsNativeControl, CommandNativeControl.cs), which counts the item */
    /* into the copy's result, asks a conflict or a manifest clash through    */
    /* the command's questions, shows the progress, and answers Cancel or a   */
    /* confirmed Exit.  A CANCEL it gives is latched here too, so the checks  */
    /* between engine calls stop as well.                                     */
    /*------------------------------------------------------------------------*/
    internal sealed class ECO_clsCopyControl
    {
        /* The limits, which the command's control (CommandNativeControl.cs)   */
        /* and ENGS_DoCopy use as well -- documentation table.                 */
        internal const int ECO_PROBLEM_LINES   = 200;   // the most problem lines a copy's result keeps for its report; later problems are still counted
        internal const int ECO_PROGRESS_MS     = 100;   // the least time between two plain progress updates, milliseconds, so a fast copy does not flood the screen

        /* The control's state -- documentation table. */
        private readonly NCB_clsNativeControl Eco_objNativeControl;  // the command's control every event goes to; never null
        private bool Eco_bCancel;                                    // a stop, latched from the command's control's CANCEL; set and read on the worker only

        /*--------------------------------------------------------------------*/
        /* ECO_clsCopyControl (constructor):                                  */
        /*                                                                    */
        /* The control for one copy, bound to the running command.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objResult    : the copy's result, its problem list already     */
        /*                    made; the counts go into it.                    */
        /*     objExecution : the running command.                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : a control with nothing cancelled.              */
        /*--------------------------------------------------------------------*/
        internal ECO_clsCopyControl(ECR_clsCopyResult objResult, EXC_clsExecution objExecution)
        {
            /* Not cancelled; and every event goes to the command's control,   */
            /* which is given the caller's result by reference -- so the       */
            /* counts land in the caller's own record -- and asks the          */
            /* command's questions itself.                                     */
            Eco_bCancel = false;
            Eco_objNativeControl = new NCB_clsNativeControl(objExecution, objResult, null);
        }

        /*--------------------------------------------------------------------*/
        /* ECO_OnCopyEvent:                                                   */
        /*                                                                    */
        /* The copy's callback (BFE_CopyCallback): called by the engine on    */
        /* the worker, inside the operation's engine call, for each item's    */
        /* start and end, its progress, a skip or failure, a conflict, a      */
        /* manifest clash or a note.                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pEvent    : the engine's BFE_CopyEvent, valid only during this */
        /*                 call.                                              */
        /*     pvContext : not used -- the delegate is bound to this object.  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : an ENG_REPLY_* value -- go on, overwrite, skip, keep the */
        /*           file or the manifest, or cancel.                         */
        /*--------------------------------------------------------------------*/
        internal int ECO_OnCopyEvent(IntPtr pEvent, IntPtr pvContext)
        {
            int iReply;

            /* The command's control handles the event -- counts it into the   */
            /* result, asks a conflict or a clash through the command's        */
            /* questions, shows the progress -- and gives the reply.           */
            iReply = Eco_objNativeControl.NCB_ProcessCopyEvent(pEvent, pvContext);
            if (iReply == ENG_clsNative.ENG_REPLY_CANCEL)
            {   /* A stop: latched, so ECO_ShouldCancel also says stop --   */
                /* ENGS_DoCopy then starts no further item, and a raw       */
                /* copy's checksum pass ends.                               */
                Eco_bCancel = true;
            }

            /* The command's reply, whatever it is. */
            return(iReply);
        }

        /*--------------------------------------------------------------------*/
        /* ECO_PostProgress:                                                  */
        /*                                                                    */
        /* Show where an item or a phase has got.  The raw copy's checksum    */
        /* pass calls it for its own "hash" phase (ENGS_DoCopyRaw), so the    */
        /* window keeps moving while the image file is read for the sum.  The */
        /* command's control lets a plain PROGRESS update through no more     */
        /* often than ECO_PROGRESS_MS; every other kind goes through at once. */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sItem    : the item's image path, or a raw phase's label.      */
        /*     uiKind   : the event's kind (ENG_EVENT_*).                     */
        /*     ullDone  : bytes done of the item or phase.                    */
        /*     ullTotal : its total; 0 when there is none.                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : shown, or let go as too soon after the last.            */
        /*--------------------------------------------------------------------*/
        internal void ECO_PostProgress(string sItem, uint uiKind, ulong ullDone, ulong ullTotal)
        {
            /* The command's control shows it, with the copy's counts, and   */
            /* applies the rate itself.                                      */
            Eco_objNativeControl.NCB_Progress(sItem, uiKind, ullDone, ullTotal);
        }

        /*--------------------------------------------------------------------*/
        /* ECO_ShouldCancel:                                                  */
        /*                                                                    */
        /* Whether to stop, asked where no engine event is there to ask:      */
        /* between the items of a list copy (ENGS_DoCopy) and between the     */
        /* chunks of a raw copy's checksum (ENGS_DoCopyRaw).  For a command's */
        /* copy this is the command's checkpoint, so a pending Exit is asked  */
        /* about HERE, and the worker waits for the answer.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true to stop -- a Cancel, a confirmed Exit, a failed    */
        /*            callback, or a stop already latched by an event.        */
        /*--------------------------------------------------------------------*/
        internal bool ECO_ShouldCancel()
        {
            if (!Eco_objNativeControl.NCB_Continue())
            {   /* The command says stop: latched, so every later event and   */
                /* check gives the same answer.                               */
                Eco_bCancel = true;
            }

            /* The latched answer: set by this check or by an event's CANCEL. */
            return(Eco_bCancel);
        }

        /*--------------------------------------------------------------------*/
        /* ECO_ReadFailure:                                                   */
        /*                                                                    */
        /* After the operation: whether a callback failed.  A callback that   */
        /* throws cannot let the exception unwind through the engine, so the  */
        /* command's control keeps the failure and tells the engine to        */
        /* cancel; the operation then returns as cancelled, and this is how   */
        /* the caller tells that from the user's Cancel.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sFailure : receives the failure's text; "" when there was      */
        /*                none.                                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when a callback failed (report sFailure, not the   */
        /*            operation's status); false when the status stands.      */
        /*--------------------------------------------------------------------*/
        internal bool ECO_ReadFailure(out string sFailure)
        {
            /* The command's control keeps the first callback failure; it   */
            /* fills sFailure ("" when there was none).                     */
            return(Eco_objNativeControl.NCB_ReadFailure(out sFailure));
        }
    }

    /*------------------------------------------------------------------------*/
    /* ERC_clsRemoveControl -- one delete's callback and progress window.     */
    /*                                                                        */
    /* The object whose ERC_OnRemoveEvent the engine calls back, on the       */
    /* worker and inside BFE_RemoveTree, after each entry a folder's removal  */
    /* takes away; ENGS_DoDelete also counts each single file it removes      */
    /* through ERC_CountRemoved.  A delete is not timed in advance -- no      */
    /* counting walk runs first -- so the copy window is opened only once     */
    /* the count reaches SYS_DELETE_PROGRESS_ENTRIES: a small delete never    */
    /* flashes one, and a large one shows the count climbing from there,      */
    /* with Cancel.  The window's Cancel reaches the command, whose           */
    /* checkpoint this control asks at every event; a stop is the engine's    */
    /* CANCEL reply, and what was removed by then stays removed.              */
    /*                                                                        */
    /* Made by ENGS_DoDelete for one delete and dropped after it; ERC_Close   */
    /* closes the window it opened -- documentation table.                    */
    /*------------------------------------------------------------------------*/
    internal sealed class ERC_clsRemoveControl
    {
        private readonly EXC_clsExecution Erc_objExecution;    // the running command, for the window's views; null when the delete runs outside a command (no window, no checkpoint)
        private readonly NCB_clsNativeControl Erc_objControl;  // the command's checkpoint (Cancel, a confirmed Exit); null with no command
        private int Erc_iRemoved;                              // entries removed so far by this delete, every folder's and file's together
        private int Erc_iLastTick;                             // Environment.TickCount when the count was last shown (ms)
        private bool Erc_bShown;                               // the progress window has been opened, so ERC_Close closes it
        private string Erc_sFailure;                           // why the callback itself failed (an exception, or the checkpoint's own failure); null for none, a user's cancel included

        /*--------------------------------------------------------------------*/
        /* ERC_clsRemoveControl (constructor):                                */
        /*                                                                    */
        /* The state for one delete, bound to the command running it.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objExecution : the running command, or null when the delete is */
        /*                    not run by one (then no window and no stop).    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the state, nothing counted, no window open.    */
        /*--------------------------------------------------------------------*/
        internal ERC_clsRemoveControl(EXC_clsExecution objExecution)
        {
            /* The command's services, borrowed for this delete -- or none.   */
            /* The native control is made with no copy result: a delete has   */
            /* no report of items; only its checkpoint is used.               */
            Erc_objExecution = objExecution;
            Erc_objControl = null;
            if (objExecution != null)
            {   /* Run by a command: its checkpoint. */
                Erc_objControl = new NCB_clsNativeControl(objExecution, null, null);
            }

            /* Nothing removed, nothing shown, no failure yet. */
            Erc_iRemoved = 0;
            Erc_iLastTick = 0;
            Erc_bShown = false;
            Erc_sFailure = null;
        }

        /*--------------------------------------------------------------------*/
        /* ERC_CountRemoved:                                                  */
        /*                                                                    */
        /* One more entry gone: counted, the window opened when the count     */
        /* reaches SYS_DELETE_PROGRESS_ENTRIES, and the count shown in it --  */
        /* at most once per ECO_PROGRESS_MS, since a folder of small entries  */
        /* is removed far faster than the window can redraw.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sImagePath : the entry just removed, a managed copy.           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : counted, and shown when it is time to.                  */
        /*--------------------------------------------------------------------*/
        internal void ERC_CountRemoved(string sImagePath)
        {
            int iTick;

            /* Counted first, whatever else happens. */
            Erc_iRemoved++;
            if (Erc_objExecution == null)
            {   /* No command: nothing to show it in. */
                return;
            }

            if (!Erc_bShown && Erc_iRemoved >= SYS_clsSystem.SYS_DELETE_PROGRESS_ENTRIES)
            {   /* The delete has grown past the threshold: the copy window     */
                /* opened for this command, set to count removals.  EXC_Show    */
                /* does not wait for the window; the removal goes on at once.   */
                Erc_objExecution.EXC_Show(VWK_enmViewKind.ProgressOpen,
                    new PGV_clsProgressView(false, "Deleting from the image", "", 0, "", true));
                Erc_bShown = true;
                Erc_iLastTick = 0;
            }

            /* The rate limit: the time difference taken unchecked, so it     */
            /* stays right when TickCount wraps.  The first count after the   */
            /* window opens always goes (the tick was cleared above).         */
            iTick = Environment.TickCount;
            if (!Erc_bShown || (Erc_iLastTick != 0 &&
                unchecked(iTick - Erc_iLastTick) < ECO_clsCopyControl.ECO_PROGRESS_MS))
            {   /* No window yet, or too soon after the last count shown. */
                return;
            }

            /* The count shown, with the entry just removed; the window's   */
            /* bar has no total to fill (none is known), and the problems   */
            /* count stays 0 -- a delete stops at its first failure.        */
            Erc_iLastTick = iTick;
            if (Erc_iLastTick == 0)
            {   /* A tick of exactly 0 would read as "never shown": nudged. */
                Erc_iLastTick = 1;
            }

            Erc_objExecution.EXC_Show(VWK_enmViewKind.Progress,
                new PGD_clsProgressData(false, ENG_clsNative.ENG_EVENT_DONE, sImagePath, "",
                    0, 0, 0, 0, Erc_iRemoved, 0));
        }

        /*--------------------------------------------------------------------*/
        /* ERC_ShouldStop:                                                    */
        /*                                                                    */
        /* Whether the delete is to stop: asked at every engine event, and by */
        /* ENGS_DoDelete before each entry of its list.  It is the command's  */
        /* checkpoint, so a pending Exit is asked about here and the worker   */
        /* waits for the answer.                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true to stop -- a Cancel, a confirmed Exit, or the      */
        /*            checkpoint's own failure (kept for ERC_ReadFailure).    */
        /*--------------------------------------------------------------------*/
        internal bool ERC_ShouldStop()
        {
            string sFailure;

            if (Erc_objControl == null)
            {   /* No command: nothing can ask for a stop. */
                return(false);
            }

            if (Erc_objControl.NCB_Continue())
            {   /* Go on. */
                return(false);
            }

            /* Stop.  A stop because the checkpoint itself failed carries   */
            /* that failure; a user's cancel carries none.                  */
            if (Erc_objControl.NCB_ReadFailure(out sFailure) && Erc_sFailure == null)
            {   /* The checkpoint failed: its reason kept, the first only. */
                Erc_sFailure = sFailure;
            }

            /* The stop, either way. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* ERC_OnRemoveEvent:                                                 */
        /*                                                                    */
        /* The removal's callback (BFE_CopyCallback): called by the engine on */
        /* the worker, inside BFE_RemoveTree, with a DONE after each entry it */
        /* has removed.  Asks whether to go on, and counts the entry.  Any    */
        /* exception is caught here, since none may unwind through the        */
        /* engine.                                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pEvent    : the engine's BFE_CopyEvent, valid only during this */
        /*                 call.                                              */
        /*     pvContext : not used -- the delegate is bound to this object.  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : ENG_REPLY_CONTINUE to go on; ENG_REPLY_CANCEL to stop    */
        /*           before the next entry.                                   */
        /*--------------------------------------------------------------------*/
        internal int ERC_OnRemoveEvent(IntPtr pEvent, IntPtr pvContext)
        {
            CEV_stCopyEvent stEvent;

            try
            {   /* Everything below is managed code that could throw: the   */
                /* checkpoint, the copy of the event, the window's views.   */
                if (ERC_ShouldStop())
                {   /* Cancel, a confirmed Exit, or a failed checkpoint: the   */
                    /* engine stops before the next entry.                     */
                    return(ENG_clsNative.ENG_REPLY_CANCEL);
                }

                /* The event copied out of native memory -- good only during   */
                /* this call -- and a DONE counted with its path, copied now   */
                /* since the window shows it later.  BFE_RemoveTree raises     */
                /* nothing but DONE; anything else is passed over.             */
                stEvent = (CEV_stCopyEvent)Marshal.PtrToStructure(pEvent, typeof(CEV_stCopyEvent));
                if (stEvent.CEV_uiKind == ENG_clsNative.ENG_EVENT_DONE)
                {   /* One entry removed. */
                    ERC_CountRemoved(ENG_clsNative.ENG_PtrToUtf8(stEvent.CEV_pImagePath));
                }

                /* Go on to the next entry. */
                return(ENG_clsNative.ENG_REPLY_CONTINUE);
            }
            catch (Exception exCallback)
            {   /* The callback itself failed: the whole exception to the     */
                /* debug log, its message kept as the delete's failure (the   */
                /* first only), and the engine stopped through its own        */
                /* cancel, so it unwinds normally.                            */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("remove callback", exCallback.ToString());
                }

                /* The message kept as the delete's failure, unless an   */
                /* earlier one already was.                              */
                if (Erc_sFailure == null)
                {   /* The first failure: kept. */
                    Erc_sFailure = exCallback.Message;
                }

                /* Stop the removal. */
                return(ENG_clsNative.ENG_REPLY_CANCEL);
            }
        }

        /*--------------------------------------------------------------------*/
        /* ERC_ReadFailure:                                                   */
        /*                                                                    */
        /* After the delete: whether it was stopped by a failure of the       */
        /* callback's OWN -- not by the user's Cancel.  Both reach the engine */
        /* as the same CANCEL reply; only this tells them apart, so the       */
        /* delete reports a failure as a failure.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sFailure : receives the failure's text, or "" when there was   */
        /*                none.                                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the callback failed; false when the engine's  */
        /*            own status (a user's cancel included) stands.           */
        /*--------------------------------------------------------------------*/
        internal bool ERC_ReadFailure(out string sFailure)
        {
            /* "" unless a failure was kept. */
            sFailure = "";
            if (Erc_sFailure != null)
            {   /* The callback failed: its text, and true. */
                sFailure = Erc_sFailure;
                return(true);
            }

            /* No failure of the callback's own. */
            return(false);
        }

        /*--------------------------------------------------------------------*/
        /* ERC_Close:                                                         */
        /*                                                                    */
        /* The delete is over, however it ended: the progress window closed   */
        /* if this control opened one.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window closed, or nothing when none was opened.     */
        /*--------------------------------------------------------------------*/
        internal void ERC_Close()
        {
            if (Erc_bShown)
            {   /* Opened for this command: closed, addressed as its open    */
                /* was; and marked closed so a second call closes nothing.   */
                Erc_objExecution.EXC_Show(VWK_enmViewKind.ProgressClose, null);
                Erc_bShown = false;
            }
        }
    }


    /*------------------------------------------------------------------------*/
    /* ENGS_clsSession -- the engine operations.                              */
    /*                                                                        */
    /* One static function per operation the program asks of the engine, each */
    /* running its engine call (or calls) to the end on the calling thread    */
    /* and filling the record it is given.  The callers are the command       */
    /* worker's native steps alone (CommandNativeSteps.cs,                    */
    /* CommandNativeOperations.cs), which is what keeps every engine call on  */
    /* one thread: the engine has one session and refuses a second call while */
    /* one is running.  A static class: the state is the engine's, never      */
    /* kept here between calls.                                               */
    /*                                                                        */
    /* The rule every function keeps: a failure's detail line is fetched      */
    /* (ENGS_FillFailure) BEFORE any further engine call, since each call     */
    /* replaces the engine's last detail line, and a later clean-up call      */
    /* would otherwise put its own words in place of the real reason.         */
    /*------------------------------------------------------------------------*/
    internal static class ENGS_clsSession
    {
        /* The three kinds of copy ENGS_DoCopy runs, chosen by its caller      */
        /* (CommandNativeOperations.cs), then the engine structure sizes and   */
        /* the limits the operations below use -- documentation table.         */
        internal const int ENGS_COPY_EXTRACT = 0;            // image -> Windows (BFE_Extract)
        internal const int ENGS_COPY_IMPORT  = 1;            // Windows -> image (BFE_Import)
        internal const int ENGS_COPY_WITHIN  = 2;            // image -> another path in the same image (BFE_CopyWithin)
        internal const int ENGS_RESIZE_RESULT_BYTES = 24;    // sizeof(BFE_ResizeResult): before (8), after (8), block size (4), reserved (4)
        internal const int ENGS_CAPACITY_RESULT_BYTES = 40;  // sizeof(BFE_CapacityResult): reported, good, first bad (8 each), samples, bad, wraps, counterfeit (4 each)
        internal const int ENGS_HASH_CHUNK   = 4194304;      // bytes the SHA-256 pass reads at a time (4 MiB, the engine's raw-copy step), so its progress moves as the copy's did
        internal const int ENGS_MAX_DRIVES   = 64;           // physical drive numbers tried by ENGS_DoListDevices: \\.\PhysicalDrive0 to 63 (Windows numbers from 0; a disk numbered 64 or more is not listed)


        /* ENGS_DoReadLink's sanity bound on a link's recorded length --   */
        /* documentation table.                                            */
        private const ulong ENGS_LINK_MAX_BYTES = 65536;        // a symbolic link's target is shorter than one block, and ext4's largest block is 64 KiB: a recorded length this long or more is damage



        /*--------------------------------------------------------------------*/
        /* ENGS_FillFailure:                                                  */
        /*                                                                    */
        /* Record an engine call's status in a result's status and detail     */
        /* fields: the status as it is, and on a failure the engine's detail  */
        /* line, fetched now -- the next engine call replaces it -- and       */
        /* written to the log, which for a failure nobody is shown is the     */
        /* only trace.  The fields are passed by reference so one function    */
        /* serves every kind of result record without the records sharing a   */
        /* base class.                                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objResult     : the record the fields belong to.  Not used:    */
        /*                     the two fields below are what is written.      */
        /*     iResultStatus : the record's status field.                     */
        /*     sResultDetail : the record's detail field.                     */
        /*     iStatus       : the engine call's status, just returned.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the status stored; the detail "" on ENG_OK, the         */
        /*            engine's line otherwise.                                */
        /*--------------------------------------------------------------------*/
        internal static void ENGS_FillFailure(object objResult, ref int iResultStatus,
            ref string sResultDetail, int iStatus)
        {
            /* The status as the engine gave it, and no detail until a     */
            /* failure says otherwise -- so a success after an earlier     */
            /* failure in the same record leaves no stale reason behind.   */
            iResultStatus = iStatus;
            sResultDetail = "";

            if (iStatus != ENG_clsNative.ENG_OK)
            {   /* A failure: the engine's detail line for THIS call, taken    */
                /* before anything else can call the engine, and logged with   */
                /* the status and its sentence.                                */
                sResultDetail = ENG_clsNative.ENG_ErrorDetail();
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("engine", "status " + iStatus + " (" + ENG_clsNative.ENG_StatusText(iStatus) + ") " + sResultDetail);
                }
            }
        }


        /*--------------------------------------------------------------------*/
        /* ENGS_DoOpen:                                                       */
        /*                                                                    */
        /* Open an image file or a device as the engine's session and read    */
        /* its filesystem's facts.  If the open succeeds but the facts cannot */
        /* be read, the image is closed again, so the caller is never left    */
        /* holding a session it cannot show.  Called by the native steps      */
        /* (CommandNativeSteps.cs).                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sImagePath : the image file, or a device path.                 */
        /*     ullOffset  : where the filesystem starts in the file, in       */
        /*                  bytes; 0 when the file IS the filesystem.         */
        /*     ullLength  : the filesystem's length in bytes; 0 for "to the   */
        /*                  end of the file".                                 */
        /*     bReadOnly  : true to open read-only.                           */
        /*     objResult  : receives the outcome.                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : result is filled.                                       */
        /*--------------------------------------------------------------------*/
        internal static void ENGS_DoOpen(string sImagePath, ulong ullOffset, ulong ullLength, bool bReadOnly, EOR_clsOpenResult objResult)
        {
            byte[] abProbe;
            string sWhat;
            int    iReadOnly;
            int    iStatus;

            /* What was asked for, into the result whatever happens, so a   */
            /* failure message can name the path and the range too.         */
            objResult.EOR_sImagePath = sImagePath;
            objResult.EOR_ullOffset  = ullOffset;
            objResult.EOR_ullLength  = ullLength;
            objResult.EOR_bReadOnly  = bReadOnly;

            /* The read-only choice as the engine's int: 0 unless asked. */
            iReadOnly = 0;

            if (bReadOnly)
            {   /* The engine takes the flag as an int. */
                iReadOnly = 1;
            }

            /* The open itself; the status and, on a failure, the engine's   */
            /* detail line go into the result.                               */
            iStatus = ENG_clsNative.ENG_pfnOpen(sImagePath, ullOffset, ullLength, iReadOnly);
            ENGS_FillFailure(objResult, ref objResult.EOR_iStatus, ref objResult.EOR_sDetail, iStatus);

            if (iStatus == ENG_clsNative.ENG_ERR_MOUNT)
            {   /* No ext2/3/4, squashfs, FAT or exFAT filesystem there: the      */
                /* engine tries lwext4 first, then squashfs (by its magic), then  */
                /* FatFs, and this status means every one refused.  If the first  */
                /* page is one the partition window recognises but the engine     */
                /* does not open (NTFS, swap), the message says which, so the     */
                /* user is not left guessing.  An ext, squashfs, FAT or exFAT     */
                /* signature that still would not mount is a damaged filesystem   */
                /* of a kind the engine does open, and the engine's words are     */
                /* left as they are.                                              */
                abProbe = new byte[PTB_Tables.PTB_PROBE_BYTES];

                if (ENG_clsNative.ENG_pfnReadRaw(sImagePath, ullOffset, abProbe, (uint)abProbe.Length) == ENG_clsNative.ENG_OK)
                {   /* The first page read: what does it say it holds? */
                    sWhat = PTB_Tables.PTB_ContentText(abProbe);

                    if (sWhat.Length != 0 && !sWhat.StartsWith("ext") && !sWhat.StartsWith("FAT") && !sWhat.StartsWith("exFAT") &&
                        !sWhat.StartsWith("squashfs"))
                    {   /* A filesystem we know of, and not one the engine opens. */
                        objResult.EOR_sDetail = objResult.EOR_sDetail + "  It holds " + sWhat.Replace("  ", " ") +
                            ", not a filesystem this program opens (ext2, ext3, ext4, squashfs, FAT or exFAT).";
                    }
                }
            }

            if (iStatus != ENG_clsNative.ENG_OK)
            {   /* The image did not open: the result already says why, and   */
                /* there is no session to read facts from or to close.        */
                return;
            }

            /* Open: the filesystem's facts, which the main window needs to    */
            /* show the session at all; their failure replaces the open's OK   */
            /* status in the result, with their own detail line.               */
            iStatus = ENG_clsNative.ENG_pfnVolumeInfoGet(out objResult.EOR_stVolume);
            ENGS_FillFailure(objResult, ref objResult.EOR_iStatus, ref objResult.EOR_sDetail, iStatus);

            if (iStatus != ENG_clsNative.ENG_OK)
            {   /* Opened but its facts unreadable: closed again rather than    */
                /* left open with nothing to show.  The close's own status is   */
                /* not kept: the facts' failure, recorded above, is the one     */
                /* reported.                                                    */
                ENG_clsNative.ENG_pfnClose();
            }
        }

        /*--------------------------------------------------------------------*/
        /* ENGS_DoMakeFsAt:                                                   */
        /*                                                                    */
        /* Format a byte range of an existing file or device -- a partition,  */
        /* or the whole of it -- as an empty filesystem, in place.  The       */
        /* engine refuses while an image is open, so the command must close   */
        /* the session first; this function neither opens the new filesystem  */
        /* nor decides what follows.  Called by the format step (Nst_Format,  */
        /* CommandNativeOperations.cs).                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sPath      : the file or device.                               */
        /*     ullOffset  : where the range starts, bytes.                    */
        /*     ullLength  : its length, bytes, or 0 for "to the end".         */
        /*     stOptions  : the type, block size, label and MFO_uiFlags       */
        /*                  (ENG_MAKEFS_ZERO to zero the whole range first);  */
        /*                  the size and overwrite fields are not used.       */
        /*     objControl : the copy control, through which a zero-fill's     */
        /*                  progress is shown and its Cancel answered; the    */
        /*                  caller always passes one.  Without a zero-fill    */
        /*                  the engine raises no event and it is never        */
        /*                  called.  Null would mean no callback at all.      */
        /*     objResult  : receives the outcome.                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : result is filled.                                       */
        /*--------------------------------------------------------------------*/
        internal static void ENGS_DoMakeFsAt(string sPath, ulong ullOffset, ulong ullLength, MFO_stMakeFsOptions stOptions,
                                             ECO_clsCopyControl objControl, ESR_clsResult objResult)
        {
            ENG_CopyCallback pfnEvent;
            int              iStatus;

            /* The callback, as a copy's: none unless there is a control to   */
            /* hand the zero-fill's events to (the engine takes NULL as "no   */
            /* events"), kept alive for the whole call below.                 */
            pfnEvent = null;

            if (objControl != null)
            {   /* A control -- always, from the format step: its callback,     */
                /* through which a zero-fill shows progress and is cancelled.   */
                pfnEvent = new ENG_CopyCallback(objControl.ECO_OnCopyEvent);
            }

            /* The format.  GC.KeepAlive AFTER the call is what keeps the      */
            /* delegate alive through it: once marshalled, only native code    */
            /* refers to it, and the collector could otherwise free it while   */
            /* the engine is still calling it back -- a crash mid-zero-fill.   */
            iStatus = ENG_clsNative.ENG_pfnMakeFsAt(sPath, ullOffset, ullLength, ref stOptions, pfnEvent, IntPtr.Zero);
            GC.KeepAlive(pfnEvent);
            ENGS_FillFailure(objResult, ref objResult.ESR_iStatus, ref objResult.ESR_sDetail, iStatus);
        }

        /*--------------------------------------------------------------------*/
        /* ENGS_DoCheckFs:                                                    */
        /*                                                                    */
        /* Run the engine's e2fsck over an image file or device that is NOT   */
        /* open (the engine refuses otherwise), report-only or repairing, its */
        /* lines passed on as they arrive and its verdict put into the        */
        /* result.  Called by the check step (Nst_Check,                      */
        /* CommandNativeOperations.cs) and by ENGS_DoResizeFs when a resize   */
        /* needs a check first.                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sImagePath : the image file or device.                         */
        /*     ullOffset  : the filesystem's byte offset inside it.           */
        /*     ullLength  : its byte length, or 0 for "to the end".           */
        /*     bRepair    : true to fix what is found (ENG_CHECK_REPAIR).     */
        /*     objControl : the check's control, whose callback takes each    */
        /*                  line and step and answers Cancel.                 */
        /*     objResult  : receives the engine's status and the verdict;     */
        /*                  its line list, made by the caller, is filled      */
        /*                  through the control.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : result is filled.                                       */
        /*--------------------------------------------------------------------*/
        internal static void ENGS_DoCheckFs(string sImagePath, ulong ullOffset, ulong ullLength, bool bRepair,
                                            ECC_clsCheckControl objControl, ECK_clsCheckResult objResult)
        {
            ENG_CheckCallback pfnEvent;
            IntPtr            pResult;
            uint              uiFlags;
            int               iStatus;
            int               i;

            /* Report-only (e2fsck -fn) unless a repair was asked for. */
            uiFlags = 0;

            if (bRepair)
            {   /* e2fsck -fy. */
                uiFlags = ENG_clsNative.ENG_CHECK_REPAIR;
            }

            /* The result structure, zeroed, in unmanaged memory for the call:   */
            /* the engine's BFE_CheckResult, ENG_CHECK_RESULT_BYTES long.        */
            pResult = Marshal.AllocHGlobal(ENG_clsNative.ENG_CHECK_RESULT_BYTES);

            for (i = 0; i < ENG_clsNative.ENG_CHECK_RESULT_BYTES; i++)
            {   /* One byte per iteration. */
                Marshal.WriteByte(pResult, i, 0);
            }

            /* One delegate for the check, kept alive until the call returns:   */
            /* the control's own method, called on this thread inside the       */
            /* engine call; the lines it is given go into the result's line     */
            /* list, which the caller has already created.                      */
            pfnEvent = new ENG_CheckCallback(objControl.ECC_OnCheckEvent);
            iStatus  = ENG_clsNative.ENG_pfnCheckFs(sImagePath, ullOffset, ullLength, uiFlags, pfnEvent, IntPtr.Zero, pResult);
            GC.KeepAlive(pfnEvent);
            ENGS_FillFailure(objResult, ref objResult.ECK_iStatus, ref objResult.ECK_sDetail, iStatus);

            /* The verdict, field by field at BFE_CheckResult's offsets in       */
            /* bifrost_engine.h (mirrored by hand: a change there moves these    */
            /* reads), and the unmanaged block freed.  Read on a failure too:    */
            /* the block was zeroed, so a check that never ran reads as zeros.   */
            objResult.ECK_ullBlocksUsed  = (ulong)Marshal.ReadInt64(pResult, 0);
            objResult.ECK_ullBlocksTotal = (ulong)Marshal.ReadInt64(pResult, 8);
            objResult.ECK_uiInodesUsed   = (uint)Marshal.ReadInt32(pResult, 16);
            objResult.ECK_uiInodesTotal  = (uint)Marshal.ReadInt32(pResult, 20);
            objResult.ECK_uiExitCode     = (uint)Marshal.ReadInt32(pResult, 24);
            objResult.ECK_bModified      = (Marshal.ReadInt32(pResult, 28) != 0);
            objResult.ECK_bErrorsLeft    = (Marshal.ReadInt32(pResult, 32) != 0);
            Marshal.FreeHGlobal(pResult);

        }

        /*--------------------------------------------------------------------*/
        /* ENGS_DoResizeFs:                                                   */
        /*                                                                    */
        /* Grow the filesystem in a range of an image file or device that is  */
        /* NOT open until it fills the range (BFE_ResizeFs), checking and     */
        /* repairing it first when the engine asks for that and bCheckFirst   */
        /* is set.  The resize's own progress is not passed on -- its         */
        /* callback keeps only the text, and a resize of a card's filesystem  */
        /* takes seconds -- but a needed check's is, through the command.     */
        /* Called by the Expand step (Nst_Resize,                             */
        /* CommandNativeOperations.cs).                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sImagePath   : the image file or device.                       */
        /*     ullOffset    : the filesystem's byte offset inside it.         */
        /*     ullLength    : the range to fill, or 0 for "to the end".       */
        /*     bCheckFirst  : true to check and repair when the engine asks.  */
        /*     objResult    : receives the status, the block counts and size, */
        /*                    and the lines (its list made by the caller).    */
        /*     objExecution : the running command, for the Cancel / Exit      */
        /*                    check before the retry and for the check's      */
        /*                    control.                                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : result is filled.                                       */
        /*--------------------------------------------------------------------*/
        internal static void ENGS_DoResizeFs(string sImagePath, ulong ullOffset, ulong ullLength, bool bCheckFirst,
                                             ERS_clsResizeResult objResult, EXC_clsExecution objExecution)
        {
            ENG_CheckCallback pfnEvent;
            ECK_clsCheckResult objCheckResult;
            ECC_clsCheckControl objControl;
            string sCallbackFailure;
            IntPtr            pResult;
            int               iStatus;
            int               iTry;
            int               i;

            /* The result block in unmanaged memory (reused by both tries),   */
            /* the one callback delegate -- the result's own method, which    */
            /* collects the text lines into its line list -- and a status     */
            /* for the loop.                                                  */
            pResult  = Marshal.AllocHGlobal(ENGS_RESIZE_RESULT_BYTES);
            pfnEvent = new ENG_CheckCallback(objResult.ERS_OnResizeEvent);
            iStatus  = ENG_clsNative.ENG_OK;

            for (iTry = 0; iTry < 2; iTry++)
            {   /* One resize per iteration: the first, and once more after a check. */
                if (iTry != 0 && !objExecution.EXC_Checkpoint())
                {   /* The retry, after the check, but a Cancel or a confirmed   */
                    /* Exit came first (the checkpoint asks about a pending      */
                    /* Exit here): no second resize.  The repair has already     */
                    /* written whatever it fixed; the status says cancelled,     */
                    /* with this program's own line, since no engine call        */
                    /* failed.                                                   */
                    objResult.ERS_iStatus = ENG_clsNative.ENG_ERR_CANCELLED;
                    objResult.ERS_sDetail = "Expansion stopped before the resize retry.";
                    break;
                }

                /* The result block cleared before each try, so a second try   */
                /* refused before the engine fills it cannot show the first    */
                /* try's counts.                                               */
                for (i = 0; i < ENGS_RESIZE_RESULT_BYTES; i++)
                {   /* Clear one native result byte per iteration. */
                    Marshal.WriteByte(pResult, i, 0);
                }

                /* The resize; then the counts, read whatever the status --     */
                /* the engine fills them once the filesystem opened, and they   */
                /* read 0 (zeroed above) when it did not.                       */
                iStatus = ENG_clsNative.ENG_pfnResizeFs(sImagePath, ullOffset, ullLength, pfnEvent, IntPtr.Zero, pResult);
                ENGS_FillFailure(objResult, ref objResult.ERS_iStatus, ref objResult.ERS_sDetail, iStatus);
                objResult.ERS_ullBlocksBefore = (ulong)Marshal.ReadInt64(pResult, 0);
                objResult.ERS_ullBlocksAfter  = (ulong)Marshal.ReadInt64(pResult, 8);
                objResult.ERS_uiBlockSize     = (uint)Marshal.ReadInt32(pResult, 16);

                if (iStatus != ENG_clsNative.ENG_ERR_NEEDS_CHECK || !bCheckFirst || iTry != 0)
                {   /* Done: grown, nothing to do, refused for another reason,   */
                    /* a check not wanted, or the second try already made --     */
                    /* the result holds this try's outcome.                      */
                    break;
                }

                /* The engine wants a check: a repair-mode one, which records   */
                /* the check in the superblock so the retry is accepted.  Its   */
                /* result shares the resize's line list, so the user reads      */
                /* the check's report in the resize's own, in order, after a    */
                /* line saying why it ran.  Its control is bound to the         */
                /* command, so the check's progress is shown and Cancel and     */
                /* Exit are answered.                                           */
                objResult.ERS_lstLines.Add("The filesystem needs a check first (" + objResult.ERS_sDetail + "); checking and repairing it:");
                objCheckResult = new ECK_clsCheckResult();
                objCheckResult.ECK_lstLines = objResult.ERS_lstLines;
                objControl = new ECC_clsCheckControl(objCheckResult, objExecution);
                ENGS_DoCheckFs(sImagePath, ullOffset, ullLength, true, objControl, objCheckResult);

                if (objControl.ECC_ReadFailure(out sCallbackFailure))
                {   /* A callback failed, and the check was cancelled to stop   */
                    /* safely: the failure is the reason, reported as the       */
                    /* program's own (INTERNAL), not as a check that went       */
                    /* wrong.  No retry.                                        */
                    objResult.ERS_iStatus = ENG_clsNative.ENG_ERR_INTERNAL;
                    objResult.ERS_sDetail = sCallbackFailure;
                    break;
                }

                if (objCheckResult.ECK_iStatus == ENG_clsNative.ENG_ERR_VERIFY)
                {   /* The repair's writes were not kept by the device: that, in   */
                    /* its own words, is the reason -- not "needs a check".        */
                    objResult.ERS_iStatus = objCheckResult.ECK_iStatus;
                    objResult.ERS_sDetail = objCheckResult.ECK_sDetail;
                    break;
                }

                if (objCheckResult.ECK_iStatus == ENG_clsNative.ENG_ERR_CANCELLED)
                {   /* The user stopped the check -- a Cancel, or an Exit        */
                    /* confirmed at its checkpoint (the engine returns           */
                    /* CANCELLED, bfe_fsck.c): no resize, reported as the stop   */
                    /* it was, as the stop before the retry is, not as a check   */
                    /* that failed.  The repair may already have fixed part of   */
                    /* the filesystem; the check's lines so far are in the       */
                    /* result's list, which it shares.                           */
                    objResult.ERS_iStatus = ENG_clsNative.ENG_ERR_CANCELLED;
                    objResult.ERS_sDetail = "Expansion stopped during the check; the filesystem was not resized.";
                    break;
                }

                if (objCheckResult.ECK_iStatus != ENG_clsNative.ENG_OK || objCheckResult.ECK_bErrorsLeft)
                {   /* The check could not run, or left errors: no resize, and   */
                    /* the status stays "needs a check", with this program's     */
                    /* own line.                                                 */
                    objResult.ERS_iStatus = ENG_clsNative.ENG_ERR_NEEDS_CHECK;
                    objResult.ERS_sDetail = "the check did not leave the filesystem sound; it was not resized";
                    break;
                }
            }

            /* The delegate kept alive to here -- past every ENG_ResizeFs call   */
            /* that could call it back -- and then the result block freed.       */
            GC.KeepAlive(pfnEvent);
            Marshal.FreeHGlobal(pResult);

        }

        /*--------------------------------------------------------------------*/
        /* ENGS_DoSetWriteVerify:                                             */
        /*                                                                    */
        /* Tell the engine whether an ordinary session's writes to a device   */
        /* are read back (Options > Verify Writes to Devices).  Run on the    */
        /* worker like every engine call, so it never lands in the middle of  */
        /* one; the engine applies it to the open session at once.  Called by */
        /* the native steps (CommandNativeSteps.cs).                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bOn       : true to read back, false not to.                   */
        /*     objResult : receives the status.                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : result is filled.                                       */
        /*--------------------------------------------------------------------*/
        internal static void ENGS_DoSetWriteVerify(bool bOn, ESR_clsResult objResult)
        {
            uint uiEnabled;
            int iStatus;

            /* The setting as the engine takes it, an unsigned 0 or 1: off   */
            /* unless asked.                                                 */
            uiEnabled = 0;
            if (bOn)
            {   /* Read writes back. */
                uiEnabled = 1;
            }

            /* The call, and its status with the engine's detail on a        */
            /* failure -- only the engine's busy gate can refuse it, which   */
            /* cannot happen while the worker alone calls the engine.        */
            iStatus = ENG_clsNative.ENG_pfnSetWriteVerify(uiEnabled);
            ENGS_FillFailure(objResult, ref objResult.ESR_iStatus, ref objResult.ESR_sDetail, iStatus);
        }

        /*--------------------------------------------------------------------*/
        /* ENGS_DoTestCapacity:                                               */
        /*                                                                    */
        /* Run the engine's capacity test of a device that is NOT open (it    */
        /* refuses otherwise) and put its verdict into the result.  Its       */
        /* events go through the copy control -- labelled "test" and counting */
        /* steps -- and a Cancel stops it only after it has put back what it  */
        /* wrote.  Called by the TestCapacity step (Nst_Capacity,             */
        /* CommandNativeOperations.cs).                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sDevice    : the device's path.                                */
        /*     objControl : the copy control, bound to the command.           */
        /*     objResult  : receives the status and the verdict.              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : result is filled.                                       */
        /*--------------------------------------------------------------------*/
        internal static void ENGS_DoTestCapacity(string sDevice, ECO_clsCopyControl objControl, ECP_clsCapacityResult objResult)
        {
            ENG_CopyCallback pfnEvent;
            IntPtr           pResult;
            int              iStatus;
            int              i;

            /* The result structure, zeroed, in unmanaged memory for the call:   */
            /* the engine's 40-byte BFE_CapacityResult.                          */
            pResult = Marshal.AllocHGlobal(ENGS_CAPACITY_RESULT_BYTES);

            for (i = 0; i < ENGS_CAPACITY_RESULT_BYTES; i++)
            {   /* One byte per iteration. */
                Marshal.WriteByte(pResult, i, 0);
            }

            /* The control's callback, kept alive by the GC.KeepAlive after   */
            /* the call: once marshalled only native code refers to it, and   */
            /* a delegate collected while the engine still calls it crashes   */
            /* the program.  Then the status, with the detail on a failure.   */
            pfnEvent = new ENG_CopyCallback(objControl.ECO_OnCopyEvent);
            iStatus  = ENG_clsNative.ENG_pfnTestCapacity(sDevice, pfnEvent, IntPtr.Zero, pResult);
            GC.KeepAlive(pfnEvent);
            ENGS_FillFailure(objResult, ref objResult.ECP_iStatus, ref objResult.ECP_sDetail, iStatus);

            /* The verdict, field by field at BFE_CapacityResult's offsets in   */
            /* bifrost_engine.h (mirrored by hand: a change there moves these   */
            /* reads), and the unmanaged block freed.  Read on a failure too:   */
            /* the block was zeroed, so a test that never ran reads as zeros.   */
            objResult.ECP_ullReported  = (ulong)Marshal.ReadInt64(pResult, 0);
            objResult.ECP_ullGood      = (ulong)Marshal.ReadInt64(pResult, 8);
            objResult.ECP_ullFirstBad  = (ulong)Marshal.ReadInt64(pResult, 16);
            objResult.ECP_uiSamples    = (uint)Marshal.ReadInt32(pResult, 24);
            objResult.ECP_uiBadSamples = (uint)Marshal.ReadInt32(pResult, 28);
            objResult.ECP_bWraps       = (Marshal.ReadInt32(pResult, 32) != 0);
            objResult.ECP_bCounterfeit = (Marshal.ReadInt32(pResult, 36) != 0);
            Marshal.FreeHGlobal(pResult);

        }

        /*--------------------------------------------------------------------*/
        /* ENGS_DoCopyRaw:                                                    */
        /*                                                                    */
        /* Image a device (or a partition of one) to a file, or write an      */
        /* image file to a device, through the engine's raw copy, its events  */
        /* going through the copy control as any copy's do (the engine labels */
        /* them "copy" and, when verifying, "verify"); then, when the copy    */
        /* succeeded and a hash was asked for, the image FILE's SHA-256.      */
        /* Called by the raw copy step (Nst_Raw, CommandNativeOperations.cs), */
        /* which writes the sum's sidecar file itself.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sFrom         : the source path (a device, or the image file). */
        /*     ullFromOffset : where in it to start.                          */
        /*     sTo           : the destination path (the image file, or a     */
        /*                     device).                                       */
        /*     ullToOffset   : where in it to start.                          */
        /*     ullLength     : bytes to copy; 0 for the whole source.         */
        /*     uiFlags       : ENG_RAW_VERIFY, ENG_RAW_DECOMPRESS,            */
        /*                     ENG_RAW_XZ_OUT, as wanted, or 0.               */
        /*     sHashFile     : the image file to hash afterwards, or "" (the  */
        /*                     FILE is hashed, compressed or not -- what a    */
        /*                     download's published sum is a sum of).         */
        /*     objControl    : the copy control, bound to the command; asked  */
        /*                     between the checksum's chunks whether to stop. */
        /*     objResult     : receives the outcome and the hash.             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : result is filled.                                       */
        /*--------------------------------------------------------------------*/
        internal static void ENGS_DoCopyRaw(string sFrom, ulong ullFromOffset, string sTo, ulong ullToOffset,
                                            ulong ullLength, uint uiFlags, string sHashFile,
                                            ECO_clsCopyControl objControl, ECR_clsCopyResult objResult)
        {
            ENG_CopyCallback pfnEvent;
            byte[]           abHash;
            byte[]           abChunk;
            StringBuilder    sbHash;
            SHA256           objHash;
            FileStream       stmImage;
            ulong            ullHashed;
            ulong            ullFileBytes;
            int              cbRead;
            int              iStatus;
            int              i;

            /* No hash unless one is computed below. */
            objResult.ECR_sHash = "";

            /* The copy, with one delegate kept alive by the GC.KeepAlive   */
            /* after it (a collected delegate called back from the engine   */
            /* crashes the program), and its status with the detail.        */
            pfnEvent = new ENG_CopyCallback(objControl.ECO_OnCopyEvent);
            iStatus  = ENG_clsNative.ENG_pfnCopyRaw(sFrom, ullFromOffset, sTo, ullToOffset, ullLength, uiFlags, pfnEvent, IntPtr.Zero);
            GC.KeepAlive(pfnEvent);
            ENGS_FillFailure(objResult, ref objResult.ECR_iStatus, ref objResult.ECR_sDetail, iStatus);

            if (iStatus != ENG_clsNative.ENG_OK || sHashFile.Length == 0)
            {   /* Failed or cancelled -- a sum of a file the copy did not   */
                /* finish would mean nothing -- or no hash wanted: done.     */
                return;
            }

            try
            {   /* The checksum, which can fail on its own (the file unreadable   */
                /* now, out of memory): any exception is caught below, and a      */
                /* hash failure is never a copy failure.                          */
                /* The image FILE read once more and its SHA-256 computed         */
                /* here, with the framework's own implementation -- the           */
                /* engine does not hash -- so the user has a figure to check      */
                /* against a download or to keep with the image.  No sum          */
                /* until it is complete.                                          */
                abHash = null;

                /* The hash object and the file in nested using blocks, the    */
                /* hash made first: if the file then fails to open, the hash   */
                /* is still released on the way out.                           */
                objHash = SHA256.Create();
                using (objHash)
                {   /* The hash, released when the block ends. */
                    stmImage = new FileStream(sHashFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using (stmImage)
                    {   /* The file, read-only and shared for reading, closed   */
                        /* when this inner block ends -- before the hash.       */

                        /* Read in ENGS_HASH_CHUNK pieces rather than in one    */
                        /* call, so the window can show the pass moving and a   */
                        /* Cancel or Exit is looked at between pieces.  The     */
                        /* copy itself has already finished: a stop now only    */
                        /* gives up the sum.  The file's length, for the bar,   */
                        /* is taken before the phase is announced.              */
                        ullFileBytes = (ulong)stmImage.Length;
                        ullHashed    = 0;
                        abChunk      = new byte[ENGS_HASH_CHUNK];
                        objControl.ECO_PostProgress(CPY_clsProgressForm.CPY_LABEL_HASH, ENG_clsNative.ENG_EVENT_START, 0, ullFileBytes);

                        while (true)
                        {   /* One chunk of the file per iteration, into the sum. */
                            if (objControl.ECO_ShouldCancel())
                            {   /* Stopped (a Cancel, or an Exit confirmed at   */
                                /* this checkpoint): no sum, and the copy       */
                                /* stands, reported as the success it was.      */
                                break;
                            }

                            /* The next chunk; 0 only at the file's end. */
                            cbRead = stmImage.Read(abChunk, 0, abChunk.Length);

                            if (cbRead <= 0)
                            {   /* The end of the file: the sum is complete. */
                                objHash.TransformFinalBlock(abChunk, 0, 0);
                                abHash = objHash.Hash;
                                break;
                            }

                            /* Into the sum (no output copy wanted:   */
                            /* null), counted, and shown --           */
                            /* rate-limited by ECO_PostProgress.      */
                            objHash.TransformBlock(abChunk, 0, cbRead, null, 0);
                            ullHashed += (ulong)cbRead;
                            objControl.ECO_PostProgress(CPY_clsProgressForm.CPY_LABEL_HASH, ENG_clsNative.ENG_EVENT_PROGRESS, ullHashed, ullFileBytes);
                        }
                    }
                }

                if (abHash == null)
                {   /* The pass was stopped before the end: no sum is given,   */
                    /* and ECR_sHash stays "".                                 */
                    return;
                }

                /* The 32-byte sum as 64 lower-case hex digits, the form a   */
                /* published sum is written in.                              */
                sbHash = new StringBuilder();

                for (i = 0; i < abHash.Length; i++)
                {   /* One byte per iteration, as two lower-case hex digits. */
                    sbHash.Append(abHash[i].ToString("x2"));
                }

                /* The sum into the result, beside the copy's own status:   */
                /* the step shows it and writes it to the sidecar file.     */
                objResult.ECR_sHash = sbHash.ToString();
            }
            catch (Exception exHash)
            {   /* The checksum failed: said in the log only, ECR_sHash left   */
                /* "" -- the copy itself worked and its success stands.        */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("engine", "sha256 of " + sHashFile + " failed: " + exHash.Message);
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* ENGS_DoListDevices:                                                */
        /*                                                                    */
        /* Describe every physical disk Windows has, by asking the engine     */
        /* about \\.\PhysicalDrive0 up to ENGS_MAX_DRIVES - 1, for the device */
        /* pickers.  It needs no Administrator: the engine reads the facts    */
        /* through a zero-access open.  The outcome is always a success with  */
        /* a list, possibly empty.  Called by the native steps                */
        /* (CommandNativeSteps.cs).                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objResult : receives the list.                                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : result is filled.                                       */
        /*--------------------------------------------------------------------*/
        internal static void ENGS_DoListDevices(EDV_clsDeviceResult objResult)
        {
            DVI_stDeviceInfo stInfo;
            DEV_clsDisk       objDisk;
            string         sPath;
            int            i;

            /* An empty list, never null, so a picker can show "none". */
            objResult.EDV_lstDisks = new List<DEV_clsDisk>();

            for (i = 0; i < ENGS_MAX_DRIVES; i++)
            {   /* One drive number per iteration.  Every number is tried      */
                /* rather than stopping at the first miss: drive numbers can   */
                /* have gaps (a card reader that was unplugged).               */
                sPath = DEV_clsDisk.DEV_PHYSICAL_PREFIX + i;

                if (ENG_clsNative.ENG_pfnDeviceInfoGet(sPath, out stInfo) != ENG_clsNative.ENG_OK || stInfo.DVI_uiDevice == 0)
                {   /* No such drive (the engine could not open the number),     */
                    /* or the engine says it is not a device: left out, and      */
                    /* the next number tried.  The engine's detail line is not   */
                    /* fetched -- a missing number is no failure.                */
                    continue;
                }

                /* A disk: its path, number and facts, kept in number order. */
                objDisk       = new DEV_clsDisk();
                objDisk.DEV_sPath = sPath;
                objDisk.DEV_iNumber = i;
                objDisk.DEV_stInfo  = stInfo;
                objResult.EDV_lstDisks.Add(objDisk);
            }

            /* Always a success -- a number that is not a disk is simply not   */
            /* listed, so there is no failure to report.                       */
            objResult.EDV_iStatus = ENG_clsNative.ENG_OK;
            objResult.EDV_sDetail = "";
        }

        /*--------------------------------------------------------------------*/
        /* ENGS_DoWritePieces:                                                */
        /*                                                                    */
        /* Write a partition table -- every piece PTB_Build made, the MBR or  */
        /* GPT sectors and the erases -- to a file or device in ONE engine    */
        /* call (BFE_WriteRawBatch, bfe_rawbatch.c).  One call, because the   */
        /* engine then locks the disk's volumes once, writes every piece,     */
        /* flushes, and only then lets Windows look at the disk again:        */
        /* written piece by piece, Windows could remount a volume between two */
        /* pieces and read a table half old and half new.  The engine checks  */
        /* every piece's range before it writes the first, but a write that   */
        /* fails part way is not undone.  Called by the native steps          */
        /* (CommandNativeSteps.cs).                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sPath     : the image file or device.                          */
        /*     lstPieces : the pieces, in the order they are written; read    */
        /*                 only during this call.                             */
        /*     objResult : receives the outcome.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : result is filled.                                       */
        /*--------------------------------------------------------------------*/
        internal static void ENGS_DoWritePieces(string sPath, List<PTB_Piece> lstPieces,
            ESR_clsResult objResult)
        {
            RWP_stRawPiece[] arrPieces;
            GCHandle[] arrPins;
            int iStatus;
            int i;

            /* A failure until the engine says otherwise, with this program's   */
            /* own line: if anything below throws (pinning can run out of       */
            /* memory) the exception goes on to the command, and the result     */
            /* must not read as a success.  ENGS_FillFailure is not used --     */
            /* no engine call has been made, so the engine's detail line        */
            /* would be some earlier call's.                                    */
            objResult.ESR_iStatus = ENG_clsNative.ENG_ERR_INTERNAL;
            objResult.ESR_sDetail = "Partition publication did not reach the native write.";
            if (lstPieces == null || lstPieces.Count == 0 ||
                lstPieces.Count > ENG_clsNative.ENG_WRITE_BATCH_MAX_PIECES)
            {   /* No pieces, or more than the engine takes in one call          */
                /* (ENG_WRITE_BATCH_MAX_PIECES, its own limit mirrored):         */
                /* refused before anything is written, rather than written in    */
                /* two calls, which would bring back the remount between them.   */
                objResult.ESR_iStatus = ENG_clsNative.ENG_ERR_ARG;
                objResult.ESR_sDetail = "A partition write requires 1 to " +
                    ENG_clsNative.ENG_WRITE_BATCH_MAX_PIECES + " pieces.";
                return;
            }

            /* The engine's descriptors (RWP_stRawPiece, EngineNative.cs) and   */
            /* one pin handle per piece.  Each descriptor points straight at    */
            /* its piece's own bytes, pinned in place, so nothing is copied.    */
            arrPieces = new RWP_stRawPiece[lstPieces.Count];
            arrPins = new GCHandle[lstPieces.Count];
            try
            {   /* Every pin taken in here is released by the finally, on        */
                /* every way out -- a refusal, a throw or the engine's return.   */
                for (i = 0; i < lstPieces.Count; i++)
                {   /* One piece per iteration: checked, pinned, described. */
                    if (lstPieces[i] == null || lstPieces[i].abBytes == null ||
                        lstPieces[i].abBytes.Length == 0 ||
                        (uint)lstPieces[i].abBytes.Length > ENG_clsNative.ENG_WRITE_RAW_MAX)
                    {   /* A piece with no bytes, or more than the engine takes   */
                        /* in one piece (ENG_WRITE_RAW_MAX): the whole table is   */
                        /* refused, with nothing written -- the engine has not    */
                        /* been called.  The pins taken so far are released by    */
                        /* the finally.                                           */
                        objResult.ESR_iStatus = ENG_clsNative.ENG_ERR_ARG;
                        objResult.ESR_sDetail = "Partition piece " + i + " has invalid bytes or size.";
                        return;
                    }

                    /* The bytes pinned, so the collector cannot move them      */
                    /* while the engine reads them through the address, and     */
                    /* the descriptor filled.  The offsets, any overlap and a   */
                    /* device's sector alignment are the engine's to check,     */
                    /* before its first write.                                  */
                    arrPins[i] = GCHandle.Alloc(lstPieces[i].abBytes, GCHandleType.Pinned);
                    arrPieces[i].RWP_ullOffset = lstPieces[i].ullOffset;
                    arrPieces[i].RWP_pBytes = arrPins[i].AddrOfPinnedObject();
                    arrPieces[i].RWP_uiBytes = (uint)lstPieces[i].abBytes.Length;
                    arrPieces[i].RWP_uiReserved = 0;
                }

                /* The one call: lock, write every piece, flush, let Windows   */
                /* re-read the disk.  Its status replaces the placeholder,     */
                /* with the engine's detail on a failure.                      */
                iStatus = ENG_clsNative.ENG_pfnWriteRawBatch(sPath, arrPieces, (uint)arrPieces.Length);
                ENGS_FillFailure(objResult, ref objResult.ESR_iStatus,
                    ref objResult.ESR_sDetail, iStatus);
            }
            finally
            {   /* The engine has returned, or was never called: no address   */
                /* handed to it is in use, so every pin can go.               */
                for (i = 0; i < arrPins.Length; i++)
                {   /* One slot per iteration. */
                    if (arrPins[i].IsAllocated)
                    {   /* Pinned: released.  A refusal or a throw part way    */
                        /* leaves the later slots never pinned, and they are   */
                        /* passed over.                                        */
                        arrPins[i].Free();
                    }
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* ENGS_DoVolumeInfo:                                                 */
        /*                                                                    */
        /* Read the open filesystem's facts again -- its block and            */
        /* inode counts, the free counts as the superblock now records them,  */
        /* the feature bits, the label and the session's access mode.  For    */
        /* the Filesystem Properties window, which must not show the free     */
        /* space as it was when the image was opened.  On ext2/3/4 the free   */
        /* counts come from lwext4's in-memory superblock, which its          */
        /* allocator updates on every allocation and release, so they         */
        /* include writes not yet flushed (lwext4 ext4.c                      */
        /* ext4_mount_point_stats, ext4_balloc.c).  On FAT and exFAT the      */
        /* blocks are clusters and the free count is FatFs's f_getfree        */
        /* (bfe_fat.c), which on a large volume may scan the FAT.             */
        /* Reading changes nothing, so it is allowed on a read-only session.  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objResult : receives the outcome and the facts.                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : result is filled (ENG_ERR_NOT_OPEN with nothing open).  */
        /*--------------------------------------------------------------------*/
        internal static void ENGS_DoVolumeInfo(EVR_clsVolumeResult objResult)
        {
            /* The engine fills the facts straight into the result, and the   */
            /* status (with the engine's detail on a failure) is recorded     */
            /* beside them.  On a failure the facts are not to be used: the   */
            /* engine zeroes them only once past its gate, so on a busy or    */
            /* not-open refusal it writes nothing into them at all.           */
            ENGS_FillFailure(objResult, ref objResult.EVR_iStatus, ref objResult.EVR_sDetail, ENG_clsNative.ENG_pfnVolumeInfoGet(out objResult.EVR_stVolume));
        }

        /*--------------------------------------------------------------------*/
        /* ENGS_DoImageInfo:                                                  */
        /*                                                                    */
        /* What an image file is -- plain, or .xz / .gz / .zst / .zip by its  */
        /* first bytes -- and how large the image inside it is,               */
        /* read from the format's own directory, index or frame headers       */
        /* without decompressing anything.  Write Image asks this before its  */
        /* device picker, so the "how much" check and the warning speak of    */
        /* the image rather than the compressed file, and so a .xz cut short  */
        /* is refused before any device is chosen.  Needs no image open.      */
        /* The size is ENG_SIZE_UNKNOWN for a gzip file always, and for a     */
        /* .zst whose frames do not all record theirs -- neither can be told  */
        /* without decompressing; a device path is answered as plain, at its  */
        /* own length, without opening it for reading.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sPath     : the image file.                                    */
        /*     objResult : receives the outcome and the facts.                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : result is filled.                                       */
        /*--------------------------------------------------------------------*/
        internal static void ENGS_DoImageInfo(string sPath, EIR_clsImageResult objResult)
        {
            /* The engine fills the facts straight into the result, and the   */
            /* status (with the engine's detail on a failure) is recorded     */
            /* beside them; the facts are to be used only on ENG_OK, which    */
            /* is how a .xz cut short is refused before the picker opens.     */
            ENGS_FillFailure(objResult, ref objResult.EIR_iStatus, ref objResult.EIR_sDetail, ENG_clsNative.ENG_pfnImageInfoGet(sPath, out objResult.EIR_stInfo));
        }

        /*--------------------------------------------------------------------*/
        /* ENGS_DoClose:                                                      */
        /*                                                                    */
        /* Close the open image.  The session is closed whatever any step     */
        /* returned -- a failure is reported, never left half-open -- so the  */
        /* caller may treat the image as closed either way.  The failures     */
        /* worth showing are ENG_ERR_IO_IMAGE (a step of the final commit     */
        /* failed: run e2fsck) and ENG_ERR_VERIFY (a device write of the      */
        /* session did not stick).  Called by the native steps                */
        /* (CommandNativeSteps.cs), which hand the outcome to the command;    */
        /* anything that depends on a clean close must read it first.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objResult : receives the outcome.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : result is filled.                                       */
        /*--------------------------------------------------------------------*/
        internal static void ENGS_DoClose(ESR_clsResult objResult)
        {
            /* The close, and its status with the engine's detail (logged on   */
            /* a failure by ENGS_FillFailure, which is the only trace when     */
            /* nobody shows the result).  For a writable session the           */
            /* engine writes back its cache, stops the journal and unmounts,   */
            /* in that order; then a device's volume locks go, so Windows      */
            /* remounts the card only after the last write.                    */
            ENGS_FillFailure(objResult, ref objResult.ESR_iStatus, ref objResult.ESR_sDetail, ENG_clsNative.ENG_pfnClose());
        }

        /*--------------------------------------------------------------------*/
        /* ENGS_DoList:                                                       */
        /*                                                                    */
        /* List one image directory into a managed list.  The entries come in */
        /* the directory's own order (the pane sorts them), and an entry      */
        /* whose inode cannot be read still comes, as its name with type      */
        /* unknown and zeros.  The engine collects the directory's names      */
        /* before it calls back with any entry, so a failure there leaves the */
        /* list empty; one while it is calling back (out of memory) leaves    */
        /* the entries that came before it, with the failure status.  Called  */
        /* by the native steps (CommandNativeSteps.cs).                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sImageDir : the directory to list.                             */
        /*     objResult : receives the outcome and the entries.              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : result is filled.                                       */
        /*--------------------------------------------------------------------*/
        internal static void ENGS_DoList(string sImageDir, ELR_clsListResult objResult)
        {
            ENG_ListCallback pfnEntry;
            List<EEN_stEntry>  lstEntries;
            int              iStatus;

            /* The list goes into the result BEFORE the call, so the result   */
            /* never holds null: a refused list (not open, not a directory)   */
            /* leaves it empty, a list cut short leaves what came.  The       */
            /* directory's path rides with it, so whoever reads the result    */
            /* can name the directory in an error and make it the pane's      */
            /* current one without the request that asked for it.             */
            lstEntries        = new List<EEN_stEntry>();
            objResult.ELR_sImageDir  = sImageDir;
            objResult.ELR_lstEntries = lstEntries;

            /* The callback copies each entry into the list.  The engine        */
            /* calls it once per entry, passing the same native structure       */
            /* every time, refilled, so each one is copied out inside the       */
            /* call -- the pointer is valid only there.  It must not call the   */
            /* engine: it runs inside ENG_ListDir with the busy flag held, so   */
            /* any engine call from it would be refused as busy.                */
            pfnEntry = new ENG_ListCallback(objResult.ELR_OnEntry);

            /* The listing.  GC.KeepAlive AFTER it is what keeps the delegate   */
            /* alive while native code holds its address: a local alone does    */
            /* not, since the JIT may treat it as dead after its last use,      */
            /* and a collected delegate called back from C crashes the          */
            /* program.                                                         */
            iStatus = ENG_clsNative.ENG_pfnListDir(ENG_clsNative.ENG_Utf8(sImageDir), pfnEntry, IntPtr.Zero);
            GC.KeepAlive(pfnEntry);

            /* The status, with the engine's detail on a failure. */
            ENGS_FillFailure(objResult, ref objResult.ELR_iStatus, ref objResult.ELR_sDetail, iStatus);
        }

        /*--------------------------------------------------------------------*/
        /* Engs_CommitEdit:                                                   */
        /*                                                                    */
        /* The common tail of the edit operations: record the edit's status,  */
        /* and when the edit succeeded, flush the image so the change is in   */
        /* the image file before the user is told it was made.  A failed      */
        /* flush replaces the success, because a change that has not reached  */
        /* the file is not done.                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objResult : the result to fill.                                */
        /*     iStatus   : the edit call's status.                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : result is filled.                                       */
        /*--------------------------------------------------------------------*/
        private static void Engs_CommitEdit(ESR_clsResult objResult, int iStatus)
        {
            /* The edit's own status first, with its detail fetched now --   */
            /* before the flush below could make another call the engine's   */
            /* last.                                                         */
            ENGS_FillFailure(objResult, ref objResult.ESR_iStatus, ref objResult.ESR_sDetail, iStatus);

            if (iStatus != ENG_clsNative.ENG_OK)
            {   /* The edit itself failed; there is nothing to commit, and   */
                /* its own failure is the one to report.  Whatever it did    */
                /* change before failing is not committed here; it reaches   */
                /* the image at the next flush or the close at the latest.   */
                return;
            }

            /* The commit, whose status replaces the edit's success.  On a    */
            /* read-only session the engine's flush is a success with         */
            /* nothing to write.  A flush that loses the journal is           */
            /* reported here (JOURNAL, with the engine's detail), and the     */
            /* engine then refuses every later edit of the session the same   */
            /* way, so none is written without a journal.                     */
            ENGS_FillFailure(objResult, ref objResult.ESR_iStatus, ref objResult.ESR_sDetail, ENG_clsNative.ENG_pfnFlush());
        }

        /*--------------------------------------------------------------------*/
        /* ENGS_DoMakeDir:                                                    */
        /*                                                                    */
        /* Create one directory in the image and commit it.  One level only   */
        /* -- the parent must exist, as it always does for a folder made      */
        /* inside the one being shown -- and a name already taken is refused  */
        /* (ENG_ERR_EXISTS), never merged into.                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sImageDir : the directory to create.                           */
        /*     objResult : receives the outcome.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : result is filled.                                       */
        /*--------------------------------------------------------------------*/
        internal static void ENGS_DoMakeDir(string sImageDir, ESR_clsResult objResult)
        {
            /* The engine takes the path as UTF-8 -- the encoding ext4 names   */
            /* are stored in, and the one FatFs converts a FAT long name       */
            /* from; the create, then its commit (Engs_CommitEdit).            */
            Engs_CommitEdit(objResult, ENG_clsNative.ENG_pfnMakeDir(ENG_clsNative.ENG_Utf8(sImageDir)));
        }

        /*--------------------------------------------------------------------*/
        /* ENGS_DoRename:                                                     */
        /*                                                                    */
        /* Rename one entry in the image and commit it.  The engine refuses a */
        /* destination that exists (ENG_ERR_EXISTS), so a rename never        */
        /* replaces anything; a caller that means to replace deletes the old  */
        /* entry first.                                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sImageFrom : the existing path.                                */
        /*     sImageTo   : the new path.                                     */
        /*     objResult  : receives the outcome.                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : result is filled.                                       */
        /*--------------------------------------------------------------------*/
        internal static void ENGS_DoRename(string sImageFrom, string sImageTo, ESR_clsResult objResult)
        {
            /* Both paths as UTF-8; the rename, then its commit. */
            Engs_CommitEdit(objResult, ENG_clsNative.ENG_pfnRename(ENG_clsNative.ENG_Utf8(sImageFrom), ENG_clsNative.ENG_Utf8(sImageTo)));
        }

        /*--------------------------------------------------------------------*/
        /* ENGS_DoMove:                                                       */
        /*                                                                    */
        /* Move a list of entries to new paths inside the image and commit.   */
        /* Inside one filesystem a move IS a rename -- nothing                */
        /* is copied -- so this is the image side of cut and paste.  The list */
        /* stops at the first failure, so the reported error is the one that  */
        /* actually stopped it, and whatever was moved before it stays moved  */
        /* and is committed by the flush that follows (when that succeeds).   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     asImageFrom : the existing paths.                              */
        /*     asImageTo   : for each, the path it is to have; the same       */
        /*                   length (the caller pairs them).                  */
        /*     objResult   : receives the outcome.                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : result is filled.                                       */
        /*--------------------------------------------------------------------*/
        internal static void ENGS_DoMove(string[] asImageFrom, string[] asImageTo, ESR_clsResult objResult)
        {
            int iStatus;
            int iFlush;
            int i;

            /* Success until a rename says otherwise.  The two lists are the   */
            /* request's, taken when the move was asked for; nothing here      */
            /* looks at the pane's selection, which may have changed since.    */
            iStatus = ENG_clsNative.ENG_OK;

            for (i = 0; i < asImageFrom.Length; i++)
            {   /* One entry to move per iteration. */

                /* The move is the engine's rename (never replacing: an   */
                /* existing destination is refused, ENG_ERR_EXISTS).      */
                iStatus = ENG_clsNative.ENG_pfnRename(ENG_clsNative.ENG_Utf8(asImageFrom[i]), ENG_clsNative.ENG_Utf8(asImageTo[i]));

                if (iStatus != ENG_clsNative.ENG_OK)
                {   /* Stop at the first entry that would not move: the rest   */
                    /* are left where they were, and the moves before it       */
                    /* stand -- a rename is not undone.                        */
                    break;
                }
            }

            /* Keep the move's own failure text before the flush can replace   */
            /* it, then commit whatever was moved -- the flush runs on a       */
            /* failure too, since the moves before the failing one are real.   */
            ENGS_FillFailure(objResult, ref objResult.ESR_iStatus, ref objResult.ESR_sDetail, iStatus);
            iFlush = ENG_clsNative.ENG_pfnFlush();

            /* One status fits in the result.  When both fail, the move's is   */
            /* kept and the flush's is neither reported nor logged (nothing    */
            /* passes it to ENGS_FillFailure); the user is still told the      */
            /* move failed.  Why the move's was preferred is not recorded.     */
            if (iStatus == ENG_clsNative.ENG_OK && iFlush != ENG_clsNative.ENG_OK)
            {   /* Every move worked but the commit did not: the flush's     */
                /* failure replaces the success, since moves that have not   */
                /* reached the image file are not done.                      */
                ENGS_FillFailure(objResult, ref objResult.ESR_iStatus, ref objResult.ESR_sDetail, iFlush);
            }
        }

        /*--------------------------------------------------------------------*/
        /* ENGS_DoReadLink:                                                   */
        /*                                                                    */
        /* Read a symbolic link's target, whole, whatever its length, for the */
        /* Properties dialog, which shows the text -- "" on any failure.      */
        /* Called by the native steps (CommandNativeSteps.cs).                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sImagePath : the symbolic link.                                */
        /*     objResult  : receives the outcome and, in ETR_sText, the       */
        /*                  target.                                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : result is filled.                                       */
        /*--------------------------------------------------------------------*/
        internal static void ENGS_DoReadLink(string sImagePath, ETR_clsTextResult objResult)
        {
            EEN_stEntry stEntry;
            byte[]    abPath;
            byte[]    abTarget;
            int       iStatus;

            /* No text until the target is read, so every failure leaves "",   */
            /* and the path in the UTF-8 both engine calls take.               */
            objResult.ETR_sText = "";
            abPath       = ENG_clsNative.ENG_Utf8(sImagePath);

            /* The link's facts, for its size; a final link is described,   */
            /* not followed, so the size is the link's own -- which IS      */
            /* the target's length, so the buffer below is sized to hold    */
            /* a target of any length whole.                                */
            iStatus = ENG_clsNative.ENG_pfnStat(abPath, out stEntry);

            if (iStatus != ENG_clsNative.ENG_OK)
            {   /* The link has gone or cannot be read: report why; the   */
                /* text stays "".                                         */
                ENGS_FillFailure(objResult, ref objResult.ETR_iStatus, ref objResult.ETR_sDetail, iStatus);
                return;
            }

            if (stEntry.EEN_ullSize >= ENGS_LINK_MAX_BYTES)
            {   /* A target longer than any ext4 link can hold -- a link's   */
                /* text is always shorter than one block, and no block is    */
                /* over 64 KiB -- so the inode is damaged.  Refused before   */
                /* any allocation, rather than trusting its size.            */
                objResult.ETR_iStatus = ENG_clsNative.ENG_ERR_IO_IMAGE;
                objResult.ETR_sDetail = "the link records a target of " + stEntry.EEN_ullSize + " bytes, more than any ext4 link holds; it is damaged";
                return;
            }

            /* The target's length plus its terminator -- the engine needs   */
            /* room for the NUL it adds (lwext4 copies the target without    */
            /* one).  The size is below ENGS_LINK_MAX_BYTES, so the cast     */
            /* is exact.                                                     */
            abTarget = new byte[(int)stEntry.EEN_ullSize + 1];

            /* The read, and its status with the engine's detail.  The       */
            /* engine checks again that the path is a link and that the      */
            /* target and its NUL fit, so a link changed since the stat is   */
            /* refused rather than cut short.                                */
            iStatus  = ENG_clsNative.ENG_pfnReadLink(abPath, abTarget, (UIntPtr)(uint)abTarget.Length);
            ENGS_FillFailure(objResult, ref objResult.ETR_iStatus, ref objResult.ETR_sDetail, iStatus);

            if (iStatus == ENG_clsNative.ENG_OK)
            {   /* The target was read: as text, up to the engine's NUL. */
                objResult.ETR_sText = ENG_clsNative.ENG_FromUtf8(abTarget);
            }
        }

        /*--------------------------------------------------------------------*/
        /* ENGS_DoSetAttributes:                                              */
        /*                                                                    */
        /* Change an entry's permission bits and / or its owner and group,    */
        /* and commit.  Only what the caller says changed is                  */
        /* written, so an untouched value is never rewritten.  A FAT or       */
        /* exFAT session has no modes or owners: the engine refuses both      */
        /* calls (ENG_ERR_UNSUPPORTED), and the Properties dialog offers      */
        /* neither there, so no change is asked of it.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sImagePath : the entry.                                        */
        /*     bSetMode   : true to write uiMode.                             */
        /*     uiMode     : the new permission bits.                          */
        /*     bSetOwner  : true to write uiUid and uiGid.                    */
        /*     uiUid      : the new owner id.                                 */
        /*     uiGid      : the new group id.                                 */
        /*     objResult  : receives the outcome.                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : result is filled.                                       */
        /*--------------------------------------------------------------------*/
        internal static void ENGS_DoSetAttributes(string sImagePath, bool bSetMode, uint uiMode,
                                                  bool bSetOwner, uint uiUid, uint uiGid, ESR_clsResult objResult)
        {
            byte[] abPath;
            int    iStatus;

            /* The path once, for both calls; success until a call says     */
            /* otherwise, so with neither flag set this is a plain commit   */
            /* of nothing.                                                  */
            abPath  = ENG_clsNative.ENG_Utf8(sImagePath);
            iStatus = ENG_clsNative.ENG_OK;

            if (bSetMode)
            {   /* The permission bits changed.  The engine uses only the   */
                /* low 12 bits and lwext4 keeps the type bits, so this      */
                /* cannot turn the entry into another kind of thing.        */
                iStatus = ENG_clsNative.ENG_pfnSetMode(abPath, uiMode);
            }

            if (iStatus == ENG_clsNative.ENG_OK && bSetOwner)
            {   /* The owner or group changed, and the mode (if asked) was   */
                /* set.  Both ids are written together, as numbers -- the    */
                /* names live in the image's own files, which nothing here   */
                /* reads.  After a failed mode change this is not tried,     */
                /* so the one failure reported is the first.                 */
                iStatus = ENG_clsNative.ENG_pfnSetOwner(abPath, uiUid, uiGid);
            }

            /* The status and the commit.  A failed owner change after a    */
            /* successful mode change leaves the new mode in place; it is   */
            /* not undone (Engs_CommitEdit skips the flush, and the next    */
            /* flush or the close writes it).                               */
            Engs_CommitEdit(objResult, iStatus);
        }

        /*--------------------------------------------------------------------*/
        /* ENGS_DoDelete:                                                     */
        /*                                                                    */
        /* Delete a list of entries from the image and commit.  A             */
        /* directory is removed WITH its contents (the Delete command has     */
        /* confirmed that with the user; a cut pasted to Windows removes what */
        /* it has just copied out); anything else is removed as a single      */
        /* entry.                                                             */
        /* The list stops at the first failure, so the reported error is the  */
        /* one that actually stopped it, and whatever was removed before it   */
        /* stays removed and is committed by the flush that follows (when     */
        /* that succeeds).  A large delete shows the copy window counting     */
        /* the entries removed, with Cancel (ERC_clsRemoveControl): a stop    */
        /* comes between two entries, and is the same kind of early end.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     asImagePaths : the entries to delete.                          */
        /*     abIsDir      : for each, true when it is a directory; the same */
        /*                    length (the caller pairs them).                 */
        /*     objResult    : receives the outcome.                           */
        /*     objExecution : the running command, for the progress window    */
        /*                    and its Cancel / Exit checks; null for neither. */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : result is filled.                                       */
        /*--------------------------------------------------------------------*/
        internal static void ENGS_DoDelete(string[] asImagePaths, bool[] abIsDir, ESR_clsResult objResult,
            EXC_clsExecution objExecution)
        {
            ERC_clsRemoveControl objControl;
            ENG_CopyCallback pfnEvent;
            string sCallbackFailure;
            bool bStoppedBetween;
            int iStatus;
            int iFlush;
            int i;

            /* Success until a removal says otherwise.  Whether to delete at    */
            /* all is the command's decision: a cut reaches here only once      */
            /* its copy has said the sources may go (CommandCopySequence.cs);   */
            /* this function deletes what it is given.                          */
            iStatus = ENG_clsNative.ENG_OK;
            bStoppedBetween = false;

            /* The delete's control -- its count, its window, its stop -- and   */
            /* the one delegate every folder's removal is given, kept alive     */
            /* below for as long as the engine may call it.                     */
            objControl = new ERC_clsRemoveControl(objExecution);
            pfnEvent = new ENG_CopyCallback(objControl.ERC_OnRemoveEvent);
            try
            {   /* The list, inside the control's window life: the finally   */
                /* closes the window however the list ends.                  */
                for (i = 0; i < asImagePaths.Length; i++)
                {   /* One entry to delete per iteration. */

                    /* A Cancel or a confirmed Exit between two entries ends   */
                    /* the list here, as a stop inside a folder's removal      */
                    /* does: what went before stays gone.                      */
                    if (objControl.ERC_ShouldStop())
                    {   /* Stopped: the list ends, reported as cancelled   */
                        /* with a line of its own -- no engine call made   */
                        /* this stop, so the engine has no line for it.    */
                        iStatus = ENG_clsNative.ENG_ERR_CANCELLED;
                        bStoppedBetween = true;
                        break;
                    }

                    /* The flag comes from the listing, and each engine call   */
                    /* checks the kind again: ENG_Remove refuses a directory   */
                    /* (ENG_ERR_IS_DIR) and ENG_RemoveTree anything that is    */
                    /* not one (ENG_ERR_NOT_DIR), so an entry whose kind       */
                    /* changed since it was listed stops the list instead of   */
                    /* being removed the wrong way.                            */
                    if (abIsDir[i])
                    {   /* A directory: removed with everything under it, the   */
                        /* control counting each entry as it goes and           */
                        /* answering Cancel between two of them.                */
                        iStatus = ENG_clsNative.ENG_pfnRemoveTree(ENG_clsNative.ENG_Utf8(asImagePaths[i]),
                            pfnEvent, IntPtr.Zero);
                    }
                    else
                    {   /* A file, link or special node: removed, and counted    */
                        /* by hand once it is gone (the call raises no event).   */
                        iStatus = ENG_clsNative.ENG_pfnRemove(ENG_clsNative.ENG_Utf8(asImagePaths[i]));
                        if (iStatus == ENG_clsNative.ENG_OK)
                        {   /* Gone: one more for the count. */
                            objControl.ERC_CountRemoved(asImagePaths[i]);
                        }
                    }

                    if (iStatus != ENG_clsNative.ENG_OK)
                    {   /* Stop at the first entry that would not go (or a   */
                        /* stop inside a folder's removal): the rest stay,   */
                        /* and what went before is not brought back.         */
                        break;
                    }
                }
            }
            finally
            {   /* The delegate kept alive past the last engine call, and the   */
                /* window closed, however the list ended.                       */
                GC.KeepAlive(pfnEvent);
                objControl.ERC_Close();
            }

            /* Keep the delete's own failure text before the flush can         */
            /* replace it -- a callback's own failure, when one stopped the    */
            /* removal, reported as the program's (INTERNAL) rather than as    */
            /* the user's cancel -- then commit whatever was removed: the      */
            /* flush runs on a failure too, since the removals before it are   */
            /* real.                                                           */
            if (objControl.ERC_ReadFailure(out sCallbackFailure))
            {   /* The callback failed: its text is the reason. */
                objResult.ESR_iStatus = ENG_clsNative.ENG_ERR_INTERNAL;
                objResult.ESR_sDetail = sCallbackFailure;
            }
            else if (bStoppedBetween)
            {   /* Stopped between two entries: cancelled, in words saying   */
                /* that what went before the stop is gone.                   */
                objResult.ESR_iStatus = ENG_clsNative.ENG_ERR_CANCELLED;
                objResult.ESR_sDetail = "Deleting stopped; what was removed before the stop stays removed.";
            }
            else
            {   /* The engine's status (a cancel included) and its line. */
                ENGS_FillFailure(objResult, ref objResult.ESR_iStatus, ref objResult.ESR_sDetail, iStatus);
            }

            iFlush = ENG_clsNative.ENG_pfnFlush();

            /* One status fits in the result.  When both fail, the delete's    */
            /* is kept and the flush's is neither reported nor logged, as in   */
            /* ENGS_DoMove; why is not recorded.                               */
            if (iStatus == ENG_clsNative.ENG_OK && iFlush != ENG_clsNative.ENG_OK)
            {   /* Every delete worked but the commit did not: the flush's   */
                /* failure replaces the success, as in ENGS_DoMove.          */
                ENGS_FillFailure(objResult, ref objResult.ESR_iStatus, ref objResult.ESR_sDetail, iFlush);
            }
        }

        /*--------------------------------------------------------------------*/
        /* ENGS_DoReplaceFile:                                                */
        /*                                                                    */
        /* Replace the CONTENT of one regular file in the image with the      */
        /* content of a Windows file (an editor's staged copy), keeping the   */
        /* entry's name, permission bits, owner and group, and commit.  This  */
        /* is how an editor saves over a file: its one caller is              */
        /* ENGS_DoCreateFile, when the file is still there and may be         */
        /* replaced.  The old content stays in the image, under a temporary   */
        /* name, until the replacement is committed, so a full image or a     */
        /* failed write cannot lose it.  Two cases are NOT recovered: a       */
        /* temporary name that already exists (said where the names are       */
        /* made), and a failed rename back after a failed swap (STEP 3) --    */
        /* each leaves the outcome in the result for the caller to report.    */
        /* Removing the old copy after the commit is clean-up: its failure    */
        /* leaves the save reported as done, with a warning naming the .old   */
        /* left over (ESR_sWarning).                                          */
        /*                                                                    */
        /* The file that results is a NEW file under the old name: its modify */
        /* time is the Windows file's (the editor's save, so in practice      */
        /* now), it has a new inode number, and if the old file had other     */
        /* hard links, those names still lead to the old content.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sHostFile    : the Windows file holding the new content.       */
        /*     sImagePath   : the entry to replace, a regular file.           */
        /*     objResult    : receives the outcome.                           */
        /*     objExecution : the running command, for the import's progress  */
        /*                    and its Cancel / Exit checks; the callers pass  */
        /*                    it.  Null would mean neither.                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : result is filled.                                       */
        /*--------------------------------------------------------------------*/
        internal static void ENGS_DoReplaceFile(string sHostFile, string sImagePath, ESR_clsResult objResult,
            EXC_clsExecution objExecution = null)
        {
            IMO_stImportOptions stImport;
            ENG_CopyCallback  pfnEvent;
            EEN_stEntry         stEntry;
            byte[]            abPath;
            byte[]            abNew;
            byte[]            abOld;
            string            sStem;
            IMC_clsImportControl objImportControl;
            string            sCallbackFailure;
            int               iItemStatus;
            int               iStatus;

            /* STEP 1: the entry as it is now -- read again here rather than   */
            /* trusted from when the editor opened it, since its mode and      */
            /* owner are what the new file takes and the name may have been    */
            /* reused meanwhile.                                               */
            abPath  = ENG_clsNative.ENG_Utf8(sImagePath);
            iStatus = ENG_clsNative.ENG_pfnStat(abPath, out stEntry);

            if (iStatus != ENG_clsNative.ENG_OK)
            {   /* It has gone, or cannot be read: report why.  Nothing has   */
                /* been changed, so there is nothing to undo or commit.       */
                ENGS_FillFailure(objResult, ref objResult.ESR_iStatus, ref objResult.ESR_sDetail, iStatus);
                return;
            }

            if (stEntry.EEN_uiType != ENG_clsNative.ENG_TYPE_FILE)
            {   /* Something else has taken the name since the file was opened. */

                /* Refused here, not by the engine, so the status and the   */
                /* line are this function's own (no engine detail exists    */
                /* for it) and are logged here as ENGS_FillFailure would.   */
                /* Nothing has been changed.                                */
                objResult.ESR_iStatus = ENG_clsNative.ENG_ERR_TYPE;
                objResult.ESR_sDetail = sImagePath + " is no longer a regular file.";
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("engine", "replace refused: " + objResult.ESR_sDetail);
                }

                /* Refused: the result carries the status and its line. */
                return;
            }

            /* Two names in the entry's own directory that nothing else has:    */
            /* one for the new content while it is written, one for the old     */
            /* content while the two are swapped.  The SAME directory, so the   */
            /* swap is two renames inside one directory and copies nothing;     */
            /* a leading dot hides them from a plain ls if one is left over.    */
            /* "Nothing else has" rests on 32 random bits and is not checked:   */
            /* a name that did exist would be skipped by the import as a        */
            /* conflict -- which the engine counts as a success -- and the      */
            /* swap below would then put that file in the entry's place.        */
            sStem = sImagePath.Substring(0, sImagePath.LastIndexOf('/') + 1) + ".bfm-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            abNew = ENG_clsNative.ENG_Utf8(sStem + ".new");
            abOld = ENG_clsNative.ENG_Utf8(sStem + ".old");

            /* STEP 2: import the new content, with the entry's own mode and    */
            /* owner, under the temporary name and BEFORE the entry is          */
            /* touched: if the image is full, this is where it fails, and the   */
            /* entry is still whole.  The options: the entry's 12               */
            /* permission bits and ids (uiDirMode too, though one file makes    */
            /* no directory), the Windows file's modify time, and no            */
            /* manifest -- the editor has no metadata file to apply.            */
            stImport.IMO_uiFileMode   = stEntry.EEN_uiMode;
            stImport.IMO_uiDirMode    = stEntry.EEN_uiMode;
            stImport.IMO_uiUid        = stEntry.EEN_uiUid;
            stImport.IMO_uiGid        = stEntry.EEN_uiGid;
            stImport.IMO_uiCopyMtime   = 1;
            stImport.IMO_uiUseManifest = 0;

            /* The import's callback (IMC_clsImportControl,                    */
            /* CommandImportControl.cs), bound to the command: it shows the    */
            /* save's progress, answers a Cancel or an Exit at each event --   */
            /* all before the entry is touched -- and keeps the status of an   */
            /* item that failed, which matters because for a single file the   */
            /* import itself reports no more than "partial".                   */
            objImportControl = new IMC_clsImportControl(objExecution);
            pfnEvent = objImportControl.IMC_OnEvent;

            /* The import of the one file under the .new name.  GC.KeepAlive   */
            /* after it keeps the delegate alive while native code holds its   */
            /* address (a local alone does not).                               */
            iStatus = ENG_clsNative.ENG_pfnImport(sHostFile, abNew, ref stImport, pfnEvent, IntPtr.Zero);
            GC.KeepAlive(pfnEvent);

            /* The file's own failure, if an event reported one. */
            iItemStatus = objImportControl.IMC_ReadItemStatus();
            if (iStatus == ENG_clsNative.ENG_ERR_PARTIAL && iItemStatus != ENG_clsNative.ENG_OK)
            {   /* Report the file's own failure, not "partial".  The detail   */
                /* line still is the import's ("1 item(s) could not be         */
                /* copied"), the last the engine recorded; the event that      */
                /* carried the status is not kept.                             */
                iStatus = iItemStatus;
            }

            if (objImportControl.IMC_ReadFailure(out sCallbackFailure))
            {   /* A callback failed and told the engine to cancel: that       */
                /* failure is the reason, reported as the program's own        */
                /* (INTERNAL).  The entry is untouched; whatever part of the   */
                /* .new exists is removed and that committed, both results     */
                /* ignored, as after any failed import below.                  */
                objResult.ESR_iStatus = ENG_clsNative.ENG_ERR_INTERNAL;
                objResult.ESR_sDetail = sCallbackFailure;
                ENG_clsNative.ENG_pfnRemove(abNew);
                ENG_clsNative.ENG_pfnFlush();
                return;
            }

            if (iStatus != ENG_clsNative.ENG_OK)
            {   /* The new content is not in the image; the entry is untouched. */

                /* Report it, then remove whatever part of the new file exists   */
                /* (the import removes a half-written file itself; this also     */
                /* covers one it left) and commit that.  Both clean-up results   */
                /* are ignored: the failure worth reporting is the import's,     */
                /* and a .new left behind only costs its space.  The failure     */
                /* is recorded FIRST, before the clean-up calls can make         */
                /* another detail line the engine's last.                        */
                ENGS_FillFailure(objResult, ref objResult.ESR_iStatus, ref objResult.ESR_sDetail, iStatus);
                ENG_clsNative.ENG_pfnRemove(abNew);
                ENG_clsNative.ENG_pfnFlush();
                return;
            }

            if (objExecution != null && !objExecution.EXC_Checkpoint())
            {   /* The new content is in, but a Cancel or a confirmed Exit     */
                /* came (a pending Exit is asked about here).  This is the     */
                /* last point a stop is safe -- the entry is still whole --    */
                /* so the save is given up: the .new removed and that          */
                /* committed, results ignored, and the status cancelled with   */
                /* this program's own line.                                    */
                objResult.ESR_iStatus = ENG_clsNative.ENG_ERR_CANCELLED;
                objResult.ESR_sDetail = "Save cancelled before replacing the original file.";
                ENG_clsNative.ENG_pfnRemove(abNew);
                ENG_clsNative.ENG_pfnFlush();
                return;
            }

            /* STEP 3: the swap.  Old aside, new into its place, old removed.   */
            /* Renames only -- nothing is copied, so the step that needs the    */
            /* file's worth of space is already behind (a rename can still      */
            /* need one directory block for its new name).  There is NO         */
            /* Cancel or Exit check from here to the commit: a stop between     */
            /* the two renames would leave the entry's name empty, and one      */
            /* before the commit a replacement not yet in the image file.       */
            iStatus = ENG_clsNative.ENG_pfnRename(abPath, abOld);

            if (iStatus != ENG_clsNative.ENG_OK)
            {   /* The entry would not move: leave it, and drop the new content. */

                /* As after a failed import: report, remove the .new, commit;    */
                /* the entry is still under its own name with its old content.   */
                ENGS_FillFailure(objResult, ref objResult.ESR_iStatus, ref objResult.ESR_sDetail, iStatus);
                ENG_clsNative.ENG_pfnRemove(abNew);
                ENG_clsNative.ENG_pfnFlush();
                return;
            }

            /* The new file takes the entry's name; the old is aside as .old. */
            iStatus = ENG_clsNative.ENG_pfnRename(abNew, abPath);

            if (iStatus != ENG_clsNative.ENG_OK)
            {   /* The new file would not take the name: put the old one back. */

                /* Report, rename .old back to the name, remove the .new,       */
                /* commit.  The results are not checked: if the rename back     */
                /* fails too, the old content is left under the .old name and   */
                /* the entry's name is free -- nothing is lost, but nothing     */
                /* says where it went beyond the log.                           */
                ENGS_FillFailure(objResult, ref objResult.ESR_iStatus, ref objResult.ESR_sDetail, iStatus);
                ENG_clsNative.ENG_pfnRename(abOld, abPath);
                ENG_clsNative.ENG_pfnRemove(abNew);
                ENG_clsNative.ENG_pfnFlush();
                return;
            }

            /* STEP 4: commit the new content in its place WHILE the old is     */
            /* still aside as .old -- so if the commit fails, the old content   */
            /* is still in the image to recover, and is left there.             */
            iStatus = ENG_clsNative.ENG_pfnFlush();

            if (iStatus != ENG_clsNative.ENG_OK)
            {   /* The replacement is not committed: a failure, reported as    */
                /* such, and the .old copy is kept rather than removed after   */
                /* a commit that did not hold.                                 */
                ENGS_FillFailure(objResult, ref objResult.ESR_iStatus, ref objResult.ESR_sDetail, iStatus);
                return;
            }

            /* The save is in place and committed: from here it HAS worked. */
            ENGS_FillFailure(objResult, ref objResult.ESR_iStatus, ref objResult.ESR_sDetail, ENG_clsNative.ENG_OK);

            /* STEP 5: the old copy goes, and that is committed in turn -- a   */
            /* step apart, so its failure cannot be mistaken for the save's.   */
            iStatus = ENG_clsNative.ENG_pfnRemove(abOld);

            if (iStatus == ENG_clsNative.ENG_OK)
            {   /* Removed: commit the removal. */
                iStatus = ENG_clsNative.ENG_pfnFlush();
            }

            if (iStatus != ENG_clsNative.ENG_OK)
            {   /* Only the clean-up failed: the save stands, and the user is   */
                /* told, as a warning on a success, where the old copy is.      */
                objResult.ESR_sWarning = "The file was saved, but its previous version could not be removed afterwards.\n" +
                                  "It is still in the image as " + sStem + ".old\n\n" + ENG_clsNative.ENG_StatusText(iStatus);
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("engine", "save of " + sImagePath + ": the old copy " + sStem + ".old was not removed: " +
                                   ENG_clsNative.ENG_StatusText(iStatus));
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* ENGS_DoCreateFile:                                                 */
        /*                                                                    */
        /* Put a whole Windows file (a staged copy) into the image under a    */
        /* given name and commit it -- New File, and an editor's Save or Save */
        /* As (CommandNativeSteps.cs).  Unlike ENGS_DoReplaceFile, the name   */
        /* need not exist.  When a regular file is already there it is        */
        /* replaced only when bOverwrite says so -- an editor saving its own  */
        /* file, or a Save As the user has agreed to -- through               */
        /* ENGS_DoReplaceFile, keeping that file's own mode and owner; a new  */
        /* file takes them from sAttrsFrom's entry, or the defaults.  A       */
        /* directory, link or special node at the name is always refused.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sHostFile    : the Windows file holding the content.           */
        /*     sImagePath   : where it goes in the image.                     */
        /*     bOverwrite   : true when a regular file already at that path   */
        /*                    may be replaced.                                */
        /*     sAttrsFrom   : the image path of the entry this is a copy of,  */
        /*                    or "" / null for none.                          */
        /*     objResult    : receives the outcome; ENG_ERR_EXISTS means      */
        /*                    "ask, then call again with bOverwrite".         */
        /*     objExecution : the running command, for the import's progress  */
        /*                    and its Cancel / Exit checks; the callers pass  */
        /*                    it.  Null would mean neither.                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : result is filled.                                       */
        /*--------------------------------------------------------------------*/
        internal static void ENGS_DoCreateFile(string sHostFile, string sImagePath, bool bOverwrite,
            string sAttrsFrom, ESR_clsResult objResult, EXC_clsExecution objExecution = null)
        {
            IMO_stImportOptions stImport;
            ENG_CopyCallback  pfnEvent;
            EEN_stEntry         stEntry;
            byte[]            abPath;
            IMC_clsImportControl objImportControl;
            string            sCallbackFailure;
            int               iItemStatus;
            int               iStatus;

            /* STEP 1: what is at the path now.  A final link is described,   */
            /* not followed, so a link at the path reads as a link and is     */
            /* refused below rather than written through.                     */
            abPath  = ENG_clsNative.ENG_Utf8(sImagePath);
            iStatus = ENG_clsNative.ENG_pfnStat(abPath, out stEntry);

            if (iStatus == ENG_clsNative.ENG_OK)
            {   /* Something is at the path: what it is, and whether the   */
                /* caller allows replacing it, decide what happens.        */
                if (stEntry.EEN_uiType != ENG_clsNative.ENG_TYPE_FILE)
                {   /* A directory, a link or a special node: never replaced by a save. */

                    /* Refused here, so the status and its line are this        */
                    /* function's own, logged here; nothing has been changed.   */
                    objResult.ESR_iStatus = ENG_clsNative.ENG_ERR_TYPE;
                    objResult.ESR_sDetail = sImagePath + " exists and is not a regular file.";
                    if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                    {   /* Only while the debug log is kept: otherwise the line is never built. */
                        DBGLOG_clsDebugLog.DBGLOG_Write("engine", "save as refused: " + objResult.ESR_sDetail);
                    }

                    /* Refused: the result carries the status and its line. */
                    return;
                }

                if (!bOverwrite)
                {   /* A regular file the caller has not allowed to be        */
                    /* replaced (New File, or a Save As not yet agreed to):   */
                    /* nothing is changed.                                    */

                    /* ENG_ERR_EXISTS is the caller's cue to ask and call       */
                    /* again; not logged, as it is a question, not a failure.   */
                    objResult.ESR_iStatus = ENG_clsNative.ENG_ERR_EXISTS;
                    objResult.ESR_sDetail = sImagePath + " already exists.";
                    return;
                }

                /* Allowed: replace it the safe way, keeping its own mode and   */
                /* owner, so it cannot be lost to a full image --               */
                /* ENGS_DoReplaceFile stats it again and fills the result       */
                /* itself, so this function's answer is its answer.             */
                ENGS_DoReplaceFile(sHostFile, sImagePath, objResult, objExecution);
                return;
            }

            if (iStatus != ENG_clsNative.ENG_ERR_NOT_FOUND)
            {   /* The path could not even be looked at (not open, a damaged   */
                /* directory on the way): report why; nothing was changed.     */
                ENGS_FillFailure(objResult, ref objResult.ESR_iStatus, ref objResult.ESR_sDetail, iStatus);
                return;
            }

            /* STEP 2: the new file's mode and owner.  The defaults first --    */
            /* 0644 (0x1A4: C# has no octal literal), owner and group 0, the    */
            /* Windows file's modify time, no manifest; the directory mode,     */
            /* 0755, is unused, since one file makes no directory -- then the   */
            /* source entry's facts when there is one and it can be read.       */
            /* A source that cannot be read silently leaves the defaults.       */
            stImport.IMO_uiFileMode   = 0x1A4;
            stImport.IMO_uiDirMode    = 0x1ED;
            stImport.IMO_uiUid        = 0;
            stImport.IMO_uiGid        = 0;
            stImport.IMO_uiCopyMtime   = 1;
            stImport.IMO_uiUseManifest = 0;

            if (sAttrsFrom != null && sAttrsFrom.Length != 0)
            {   /* A copy of an image entry: it takes that entry's facts. */
                if (ENG_clsNative.ENG_pfnStat(ENG_clsNative.ENG_Utf8(sAttrsFrom), out stEntry) == ENG_clsNative.ENG_OK)
                {   /* The source is still there to be read. */
                    stImport.IMO_uiFileMode = stEntry.EEN_uiMode;
                    stImport.IMO_uiUid      = stEntry.EEN_uiUid;
                    stImport.IMO_uiGid      = stEntry.EEN_uiGid;
                }
            }

            /* STEP 3: the import's callback, bound to the command, as in    */
            /* ENGS_DoReplaceFile: progress, a Cancel or Exit answered at    */
            /* each event of a long save, and the file's own failure kept.   */
            objImportControl = new IMC_clsImportControl(objExecution);
            pfnEvent = objImportControl.IMC_OnEvent;

            /* The import of the one file under the chosen name; KeepAlive     */
            /* after it keeps the delegate alive while native code holds it.   */
            iStatus = ENG_clsNative.ENG_pfnImport(sHostFile, abPath, ref stImport, pfnEvent, IntPtr.Zero);
            GC.KeepAlive(pfnEvent);

            /* The file's own failure, if an event reported one. */
            iItemStatus = objImportControl.IMC_ReadItemStatus();
            if (iStatus == ENG_clsNative.ENG_ERR_PARTIAL && iItemStatus != ENG_clsNative.ENG_OK)
            {   /* Report the file's own failure, not "partial" (the detail   */
                /* line is still the import's own).                           */
                iStatus = iItemStatus;
            }

            if (objImportControl.IMC_ReadFailure(out sCallbackFailure))
            {   /* A callback failed and told the engine to cancel: that      */
                /* failure is the reason, reported as the program's own       */
                /* (INTERNAL).  Whatever part of the file exists is removed   */
                /* and that committed, results ignored -- the name was free   */
                /* at STEP 1, so nothing of the user's is removed.            */
                objResult.ESR_iStatus = ENG_clsNative.ENG_ERR_INTERNAL;
                objResult.ESR_sDetail = sCallbackFailure;
                ENG_clsNative.ENG_pfnRemove(abPath);
                ENG_clsNative.ENG_pfnFlush();
                return;
            }

            if (iStatus != ENG_clsNative.ENG_OK)
            {   /* The file is not in the image; whatever part of it exists is removed. */

                /* Reported first, so the clean-up calls cannot replace the   */
                /* detail line.  The import already removes a half-written    */
                /* file; this remove covers anything it left, and fails       */
                /* harmlessly (not found) when nothing was made -- a parent   */
                /* that does not exist, say, which fails the import with      */
                /* nothing created for it.  Clean-up results are ignored.     */
                /* The path was free at STEP 1, so nothing of the user's      */
                /* can be removed here.                                       */
                ENGS_FillFailure(objResult, ref objResult.ESR_iStatus, ref objResult.ESR_sDetail, iStatus);
                ENG_clsNative.ENG_pfnRemove(abPath);
                ENG_clsNative.ENG_pfnFlush();
                return;
            }

            /* In: commit it.  ENG_OK is passed because the import's status   */
            /* has already been judged above; Engs_CommitEdit then flushes    */
            /* and reports the flush.                                         */
            Engs_CommitEdit(objResult, ENG_clsNative.ENG_OK);
        }

        /*--------------------------------------------------------------------*/
        /* ENGS_DoCopy:                                                       */
        /*                                                                    */
        /* Copy a list of items in one direction, one engine call per item,   */
        /* all reporting through one copy control, so the whole list shares   */
        /* the user's "Overwrite All" / "Skip All" answer and one Cancel.     */
        /* Called by the copy step (Nst_Copy, CommandNativeOperations.cs).    */
        /*                                                                    */
        /* The result's status is the WORST outcome over the list: a failed   */
        /* commit, else out of memory, else cancelled, else partial if any    */
        /* item had a problem, else success.  An item that fails outright     */
        /* before copying anything (its source vanished) is recorded as a     */
        /* problem line and the list carries on.  Memory running out is the   */
        /* exception: the engine stops the item where it ran out, and the     */
        /* whole list stops with it -- the items after it are not started.    */
        /*                                                                    */
        /* After an import or a copy inside the image, the image is FLUSHED,  */
        /* so what the user was just told had been copied is in the image     */
        /* file even if the program is killed a moment later.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iKind        : ENGS_COPY_EXTRACT (image -> Windows),           */
        /*                    ENGS_COPY_IMPORT (Windows -> image) or          */
        /*                    ENGS_COPY_WITHIN (image -> the same image).     */
        /*     asImagePaths : each item's image path: the source of an        */
        /*                    extract or of a copy within, the destination of */
        /*                    an import.                                      */
        /*     asHostPaths  : each item's other end: its host path, or for a  */
        /*                    copy within its DESTINATION image path.         */
        /*     stExtract    : extract options (used for an extract).          */
        /*     stImport     : import options (used for an import).            */
        /*     objControl   : the copy control, bound to the command.         */
        /*     objResult    : receives the outcome; the same object the       */
        /*                    control was given.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : result is filled.                                       */
        /*--------------------------------------------------------------------*/
        internal static void ENGS_DoCopy(int iKind, string[] asImagePaths, string[] asHostPaths,
                                         EXO_stExtractOptions stExtract, IMO_stImportOptions stImport,
                                         ECO_clsCopyControl objControl, ECR_clsCopyResult objResult)
        {
            ENG_CopyCallback pfnEvent;
            int              iStatus;
            int              iFlush;
            int              iProblemsBefore;
            bool             bCancelled;
            bool             bOutOfMemory;
            int              i;

            /* Success until the list says otherwise.  The counts and the      */
            /* problem lines are NOT reset here: this result is the same       */
            /* object the copy control was given (Nst_Copy makes one result,   */
            /* with its problem list, and hands it to both), and the           */
            /* control's callback counts every item into it as the engine      */
            /* reports them; this function adds only the items that fail       */
            /* before any event.                                               */
            objResult.ECR_iStatus = ENG_clsNative.ENG_OK;
            objResult.ECR_sDetail = "";
            bCancelled     = false;
            bOutOfMemory   = false;

            /* One delegate for the whole list, kept alive until the end by   */
            /* the GC.KeepAlive after the loop.                               */
            pfnEvent = new ENG_CopyCallback(objControl.ECO_OnCopyEvent);

            for (i = 0; i < asImagePaths.Length; i++)
            {   /* One selected item per iteration. */

                /* Between items no engine event comes, so the control is    */
                /* asked directly: a Cancel, an Exit confirmed here, or a    */
                /* stop latched by the last item's events.  Within an item   */
                /* the same control answers through the callback.            */
                if (objControl.ECO_ShouldCancel())
                {   /* Stopped: no further item is started; what was copied   */
                    /* stays, and is committed below.                         */
                    bCancelled = true;
                    break;
                }

                /* The problem count before the item, so an item that ran   */
                /* out of memory can be told to have been counted by its    */
                /* own ERROR event or not (below).                          */
                iProblemsBefore = objResult.ECR_iProblems;

                /* The item, by direction.  Each call runs the whole item    */
                /* (a folder with everything under it), raising its events   */
                /* through the shared control.                               */
                if (iKind == ENGS_COPY_EXTRACT)
                {   /* Image -> Windows. */
                    iStatus = ENG_clsNative.ENG_pfnExtract(ENG_clsNative.ENG_Utf8(asImagePaths[i]), asHostPaths[i],
                                                     ref stExtract, pfnEvent, IntPtr.Zero);
                }
                else if (iKind == ENGS_COPY_WITHIN)
                {   /* Image -> the same image; the "host" path is the destination image path. */
                    iStatus = ENG_clsNative.ENG_pfnCopyWithin(ENG_clsNative.ENG_Utf8(asImagePaths[i]), ENG_clsNative.ENG_Utf8(asHostPaths[i]),
                                                        pfnEvent, IntPtr.Zero);
                }
                else
                {   /* Windows -> image. */
                    iStatus = ENG_clsNative.ENG_pfnImport(asHostPaths[i], ENG_clsNative.ENG_Utf8(asImagePaths[i]),
                                                    ref stImport, pfnEvent, IntPtr.Zero);
                }

                if (iStatus == ENG_clsNative.ENG_ERR_CANCELLED)
                {   /* Stopped during this item (a Cancel, an Exit, or a     */
                    /* callback failure the control turned into a cancel).   */

                    /* The rest of the list is not started; what this item   */
                    /* had copied stays (and is committed below).            */
                    bCancelled = true;
                    break;
                }

                if (iStatus == ENG_clsNative.ENG_ERR_NO_MEMORY)
                {   /* Memory ran out: part-way through the item, where the     */
                    /* engine stopped it, or before it began.  The whole copy   */
                    /* stops here: the rest of the list is not started, since   */
                    /* it would only meet the same shortage, and the report     */
                    /* is the failure, not a list of every item that missed.    */
                    /* What was copied before it stays (and is committed        */
                    /* below).                                                  */

                    if (objResult.ECR_iProblems == iProblemsBefore)
                    {   /* No event counted it: it ran out before the engine   */
                        /* raised one for the item.  Counted, logged and       */
                        /* listed here, as an outright failure is below; the   */
                        /* detail is read with no engine call in between.      */
                        objResult.ECR_iProblems++;
                        if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                        {   /* Only while the debug log is kept: otherwise the line is never built. */
                            DBGLOG_clsDebugLog.DBGLOG_Write("copy", "failed outright: " + asImagePaths[i] + "  --  " + ENG_clsNative.ENG_StatusText(iStatus) +
                                           "  " + ENG_clsNative.ENG_ErrorDetail());
                        }

                        if (objResult.ECR_lstProblems.Count < ECO_clsCopyControl.ECO_PROBLEM_LINES)
                        {   /* Room for another line in the report. */
                            objResult.ECR_lstProblems.Add(asImagePaths[i] + "  --  " + ENG_clsNative.ENG_StatusText(iStatus) +
                                                   "  " + ENG_clsNative.ENG_ErrorDetail());
                        }
                    }

                    /* The list's result: the status and the engine's detail   */
                    /* line (where it stopped), logged by ENGS_FillFailure     */
                    /* and kept to the end, where nothing replaces it but a    */
                    /* failed commit.                                          */
                    ENGS_FillFailure(objResult, ref objResult.ECR_iStatus, ref objResult.ECR_sDetail, iStatus);
                    bOutOfMemory = true;
                    break;
                }

                /* PARTIAL needs nothing here: the events already counted   */
                /* and described the items within it that failed.           */
                if (iStatus != ENG_clsNative.ENG_OK && iStatus != ENG_clsNative.ENG_ERR_PARTIAL)
                {   /* The item failed before any event was raised for it:   */
                    /* record it here, with the engine's own detail line.    */

                    /* Any other status comes only before a copy starts (the   */
                    /* engine's copy calls return OK, PARTIAL or CANCELLED     */
                    /* once running, or NO_MEMORY, handled above --            */
                    /* bfe_copy.c), so no event counted it.                    */
                    /* Counted, logged, and listed while the list has room;    */
                    /* the detail is read twice with no engine call between,   */
                    /* so both say the same.  The list carries on.             */
                    objResult.ECR_iProblems++;
                    if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                    {   /* Only while the debug log is kept: otherwise the line is never built. */
                        DBGLOG_clsDebugLog.DBGLOG_Write("copy", "failed outright: " + asImagePaths[i] + "  --  " + ENG_clsNative.ENG_StatusText(iStatus) +
                                       "  " + ENG_clsNative.ENG_ErrorDetail());
                    }

                    if (objResult.ECR_lstProblems.Count < ECO_clsCopyControl.ECO_PROBLEM_LINES)
                    {   /* Room for another line in the report. */
                        objResult.ECR_lstProblems.Add(asImagePaths[i] + "  --  " + ENG_clsNative.ENG_StatusText(iStatus) +
                                               "  " + ENG_clsNative.ENG_ErrorDetail());
                    }
                }
            }

            /* The delegate stays reachable up to here, past every engine   */
            /* call that holds its address.                                 */
            GC.KeepAlive(pfnEvent);

            if (iKind != ENGS_COPY_EXTRACT)
            {   /* An import or a copy within changed the image: commit it now. */

                /* Also after a cancel or a failure: what was copied before   */
                /* it is in the image and is committed, not left in cache.    */
                iFlush = ENG_clsNative.ENG_pfnFlush();

                if (iFlush != ENG_clsNative.ENG_OK)
                {   /* The commit failed: that outranks everything else. */

                    /* Its status and detail replace the result's; the      */
                    /* counts and problem lines stay, so the report still   */
                    /* says what was copied, but a cancel is no longer      */
                    /* stated as such.                                      */
                    ENGS_FillFailure(objResult, ref objResult.ECR_iStatus, ref objResult.ECR_sDetail, iFlush);
                    return;
                }
            }

            /* The list's worst outcome, set last: out of memory (already in   */
            /* the result, with its detail), else cancelled, else partial if   */
            /* any item had a problem (counted by the events or above), else   */
            /* the success set at the top.                                     */
            if (bOutOfMemory)
            {   /* Stopped by memory running out: the status and detail set   */
                /* when it happened stand.                                    */
            }
            else if (bCancelled)
            {   /* Stopped: cancelled.  Whether a callback failure caused it   */
                /* is the caller's to ask (ECO_ReadFailure, Nst_Copy).         */
                objResult.ECR_iStatus = ENG_clsNative.ENG_ERR_CANCELLED;
            }
            else if (objResult.ECR_iProblems != 0)
            {   /* Finished, with items skipped or failed: partial, and the   */
                /* problem lines say which.                                   */
                objResult.ECR_iStatus = ENG_clsNative.ENG_ERR_PARTIAL;
            }
        }
    }
}
