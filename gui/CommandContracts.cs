/*----------------------------------------------------------------------------*/
/* CommandContracts.cs                                                        */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The shapes the command system's two sides exchange: what may be asked for  */
/* (the request kinds, with their scheduling classes and the worker's modes), */
/* the steps a sequence is built from, the views the worker sends the UI,     */
/* the questions it asks and the answers it can be given; the request         */
/* envelope the UI posts, the result a step returns, the worker's record of   */
/* an open editor, the state view the UI mirrors, and the step table that     */
/* binds the worker to its native steps.  These carry values only: a request  */
/* is frozen when posted and never carries code to run, and what may run,     */
/* when, and in what order is decided by the one worker (CommandSystem.cs),   */
/* never by the UI.                                                           */
/*----------------------------------------------------------------------------*/

/* The framework: ArgumentException and the string comparison (System), the   */
/* request's path list (Collections.Generic), the invariant number text of    */
/* a request's key (Globalization) and the key's builder (Text).  No UI       */
/* namespace: nothing here touches a window.                                  */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BifrostFM
{
    /* What a request asks for: each kind is one whole sequence on the          */
    /* worker.  The numbers are fixed by hand and are never array indexes; a    */
    /* missing number (15, 48) is one no kind uses (the reason not recorded).   */
    /* A kind's scheduling class comes from SYS_Policy (CommandSystem.cs),      */
    /* never from the poster -- documentation table.                            */
    internal enum KND_enmKind
    {
        Cancel = 0,               // stop the one running request whose serial the Cancel names (Urgent); refused once that request has ended
        Exit = 1,                 // ask first, then cancel what runs and shut down if the user agrees (Urgent)
        Open = 2,                 // open the chosen target read-only
        OpenImageFile = 3,        // inspect a Windows image file, choose its range, then Open or Check it -- one sequence
        OpenDevice = 4,           // inspect a chosen device, choose its range, then Open or Check it -- one sequence
        Close = 5,                // close (or save) the image's editors, then close the session
        MakeWritable = 6,         // reopen the same filesystem writable; its epoch is kept, so open editors stay valid
        MakeReadOnly = 7,         // reopen the same filesystem read-only; its epoch is kept
        ListFolder = 8,           // list an image folder for the main pane (Latest: a newer one from the same origin replaces it while it waits)
        Edit = 9,                 // one change to the filesystem (new folder, rename, delete, new file ...), then a fresh listing
        Extract = 10,             // copy image items to Windows
        Import = 11,              // copy Windows items into the image
        CopyWithin = 12,          // copy or move items inside the open image
        OpenEntry = 13,           // read an image file out, choose its viewer (asking when needed) and register the editor
        ViewerFallback = 14,      // ask before reopening a structured document that failed to load as plain text
        SaveEditor = 16,          // commit an editor's staged content to the image or a Windows file
        EditorMakeFolder = 17,    // a Save As dialog's New Folder: ask the name, create it, list it -- in that editor's image
        OpenInput = 18,           // register a picker (a folder or file choice) before its window is shown
        InputClosed = 19,         // a picker's window has ended (Notification)
        ReadLink = 20,            // read a symbolic link's target text, for Properties
        VolumeInfo = 21,          // read the open filesystem's facts, for Filesystem Properties
        Check = 22,               // enter the exclusive Check mode with its window
        Repair = 23,              // repair the target the check window holds
        CheckClosed = 24,         // the check window has ended (Notification)
        OpenPartition = 25,       // enter the exclusive Partition mode, from no open session
        ReadLayout = 26,          // read the partition window's target's layout
        WriteTable = 27,          // write a partition table, every piece in one engine batch
        MakeFilesystem = 28,      // format a range, with no session open
        ExpandFilesystem = 29,    // write the table, check, repair if needed, then resize -- in that order
        TestCapacity = 30,        // the destructive capacity test, which puts back what it wrote, on the partition window's device
        PartitionClosed = 31,     // the partition window has ended (Notification)
        ListDevices = 32,         // list the disks for a picker (Latest)
        ReadDeviceTable = 33,     // read a device's partition choices for a picker (Latest)
        ImageInfo = 34,           // inspect an image file a raw copy would read
        ReadDevice = 35,          // copy a device to a file, with the optional SHA-256
        WriteDevice = 36,         // copy an image file to a device, always read back
        NewImage = 37,            // create a new image file, only after the open session has closed
        SetVerify = 38,           // set the Verify Writes to Devices option (Latest: the newest value wins)
        SetRiskWarnings = 39,     // set the risk warnings option (Latest: the newest value wins)
        PickerList = 40,          // list a folder for an image folder picker (Latest, per picker)
        PickerBranch = 41,        // a picker's start-up listings of the folders above its starting folder, as one sequence (Latest)
        DragExtract = 42,         // a drag out to Explorer read out while the UI waits (IdleOnly: only under the drag's reservation, with the worker idle)
        DragEnded = 43,           // the drag is over: its reservation released (Notification)
        EditorClosed = 44,        // an editor's window has ended: its registration removed (Notification)
        EditorReady = 45,         // an editor's window has been made, before it is shown; its read-out file stays owned until it closes (Notification)
        RegisterHostEditor = 46,  // record an editor opened on a Windows file, tied to no image (Notification)
        HostEditorSaved = 47,     // an editor's write of a Windows file has finished, with where it now points (Notification)
        ClosePartition = 49,      // ask the partition window to close, and wait for it to have closed
        OpenPartitionEntry = 50,  // close the partition window, then open its chosen range read-only
        ReadPartition = 51,       // copy one partition's range into a new Windows file (no question)
        WritePartition = 52,      // write a Windows image file into one partition's range, in place, after its question
        ErasePartition = 53,      // write zeros over one partition's range, in place, after its question
        EditTable = 54            // a partition-window button: its question asked and its edit made on a copy of the window's table, or the request turned into the disk operation it names (TBE_clsTableEdit)
    }

    /* How a request is admitted and queued (SYS_Post, CommandSystem.cs) --   */
    /* documentation table.                                                   */
    internal enum PRY_enmPriority
    {
        Urgent = 0,       // Cancel and Exit: put at the head of the queue, ahead of ordinary work; one of the same kind already waiting from the same origin (and epoch and stream) drops the new one
        Ordered = 1,      // the default: first in, first out; one with the same kind, origin, epoch, stream and identity already waiting drops the new one
        Latest = 2,       // a stream of values: a new one replaces the one waiting with the same kind, origin, epoch and stream key
        IdleOnly = 3,     // the drag read-out: admitted only under the drag's reservation, with nothing running
        Notification = 4  // "a window has ended" and similar facts: admitted even while an Exit is pending, so an ownership is never left held
    }

    /* The worker's exclusive mode: at most one of an open session, the   */
    /* partition window or the check window holds a target at a time --   */
    /* documentation table.                                               */
    internal enum MOD_enmMode
    {
        None = 0,       // no session open and no exclusive window
        Open = 1,       // a filesystem session is open
        Partition = 2,  // the partition window holds its target
        Check = 3       // the check window holds its target
    }

    /* The steps a sequence is made of, each one synchronous call on the     */
    /* worker (NST_Run, CommandNativeSteps.cs); a step has no queue of its   */
    /* own -- documentation table.                                           */
    internal enum STP_enmStep
    {
        Open = 0,              // open a chosen filesystem read-only or writable
        InspectImageFile = 1,  // read an image file's table and content facts, read-only, before its range is chosen
        Close = 2,             // flush and close the open session
        List = 3,              // read one folder's entries
        Edit = 4,              // one change to the filesystem, of the kind the request carries
        Copy = 5,              // an extract, an import, or a copy within the image
        Recycle = 6,           // send a cut's copied Windows sources to the Recycle Bin
        ExtractEntry = 7,      // read an image file out into a stage file of its own, for an editor
        Save = 8,              // replace or create an image file from a stage file
        ReadLink = 9,          // read a symbolic link's target text
        VolumeInfo = 10,       // read the open filesystem's facts
        Check = 11,            // report-only e2fsck
        Repair = 12,           // repairing e2fsck
        ReadLayout = 13,       // read and probe the target's whole partition table
        WriteTable = 14,       // write a checked table's pieces under one engine lock
        MakeFilesystem = 15,   // format an existing range
        Resize = 16,           // grow a filesystem to fill its range
        TestCapacity = 17,     // the capacity test: write, read back and put back samples
        ListDevices = 18,      // list the disks the program can open
        ReadDeviceTable = 19,  // read a device's partition choices for a picker
        ImageInfo = 20,        // an image file's size and format, compressed or not
        CopyRaw = 21,          // a raw copy, with the read-back and hash the request asks for
        CreateImage = 22,      // create a new image file on Windows
        SetVerify = 23,        // set the engine's device write read-back
        ReadGptEnd = 24        // read a device's primary GPT header: where its backup header lies, so a read can stop just after it
    }

    /* What the worker sends the UI: each view carries finished facts for a   */
    /* window to show, and gives the window no power to change anything --    */
    /* documentation table.                                                   */
    internal enum VWK_enmViewKind
    {
        State = 0,            // the whole state view, replacing the last (STV_clsStateView)
        Result = 1,           // a request's final result
        Question = 2,         // show one question and take its answer
        QuestionClose = 3,    // take down a cancelled question's window, unanswered
        ProgressOpen = 4,     // show a progress window or the "working" box (PGV_clsProgressView)
        ProgressClose = 5,    // end that window, with its report
        Progress = 6,         // one progress report, copied out of an engine event
        CheckOpen = 7,        // show the exclusive check window
        CheckBegin = 8,       // a check run has started: the window's id and the run's Cancel serial
        CheckFinished = 9,    // that run has finished; sent apart from the request's own result
        PartitionOpen = 10,   // show the exclusive partition window
        WindowClose = 11,     // close an exclusive window
        EditorOpen = 12,      // show a registered editor
        HostEditorOpen = 13,  // show an editor on a Windows file, once its viewer was chosen; the UI gives it its id
        EditorSaved = 14,     // an editor's save is done, for the saved revision only
        EditorClose = 15,     // close the named editor, after its save or discard
        InputOpen = 16,       // show a registered picker
        Refused = 17,         // a request refused at admission or by its state gate
        Dropped = 18,         // a request dropped as a duplicate or replaced by a newer one
        Stopped = 19,         // the worker thread has ended: the program may close and unload the engine
        Folder = 20,          // a finished folder listing, with the epoch it belongs to
        Opened = 21,          // a successful open's facts, for display only
        ClipboardSpent = 22,  // a cut has reached its copy: the UI clears the clipboard, if it is still the one captured
        Warning = 23,         // a clean-up or display problem; never a request's own result
        EditorShow = 24       // an Open of a file an editor already saves to: bring that editor's window forward (the value its id, a long) instead of opening a second
    }

    /* The questions the worker can ask, each shown by its own window code    */
    /* (CommandQuestionPresenter.cs, MainCommand.cs).  Number 19 is unused    */
    /* (the reason not recorded).  From ReplaceChecksum on, the entries are   */
    /* not listed in number order (the reason not recorded either): each      */
    /* value is the one written beside it -- documentation table.             */
    internal enum QNK_enmQuestionKind
    {
        CloseAndContinue = 0,             // close what is open (or holds the target) and carry on with the request?
        Risk = 1,                         // the first-write warning (and the partition window's): its own heading, No the default
        EditorClose = 2,                  // Save / Discard / Cancel, asked in the editor itself
        WriteTable = 3,                   // write this partition table?
        Format = 4,                       // make a filesystem here, destroying what is there?
        Repair = 5,                       // repair this filesystem?
        WriteDevice = 6,                  // write this image to this device, destroying what is there?
        ExitRunning = 7,                  // with work running or queued: stop it and exit?
        Conflict = 8,                     // a destination exists: overwrite or skip (once, or for all)
        ManifestClash = 9,                // an image item has the manifest's name: keep the data or the manifest
        Overwrite = 10,                   // a Save As destination, looked at again, exists: replace it?
        EditorsReady = 11,                // not shown: wait until every editor the UI was making has been made, before closing editors
        FolderName = 12,                  // a name: for New Folder or Rename in the main window, or a Save As dialog's New Folder
        InputClose = 13,                  // a picker is open on the editor about to be asked about: end it first
        ViewerFallback = 14,              // a structured file would not load: open it as text instead?
        ViewerBinary = 15,                // binary content: open as text, as hex, or cancel (with the text size limit)
        ViewerLargeText = 16,             // text past the editor's limit: open as hex instead?
        ImagePartition = 17,              // choose a partition (or the whole) from the table read, before the open session changes
        ImageOffset = 18,                 // no table and the whole file would not mount: one try at a typed offset
        TestCapacity = 20,                // run the capacity test (it writes samples and puts them back)?
        ModeClose = 21,                   // an exclusive window's own close question, before a Close goes ahead
        ModeClosed = 22,                  // not shown: wait for the exclusive window to report that it has really closed (or refused)
        DeleteEntries = 23,               // delete these image items for good?
        DisableRiskWarnings = 24,         // turn the risk warnings off? -- the first of two
        ConfirmDisableRiskWarnings = 25,  // the second; a No to either leaves them on
        StartElevated = 26,               // not shown: the UI's report that it has started the copy run as Administrator, after the restart was agreed and everything closed
        WritePartition = 27,              // write this file into the partition: Yes / No, or, for a smaller file, Yes (zero the rest) / No / Cancel
        ErasePartition = 28,              // write zeros over this partition: Yes / No, No the default
        ReplaceChecksum = 29,             // a device write's sum: replace the <image>.sha256 already beside the image? Yes / No, No (keep it) the default
        PartitionFile = 31,               // the Windows file a partition is saved to (Read) or written from (Write), asked through the partition window; the answer's text is the path
        PartitionImageFile = 42,          // File > Partition/Format > Image File's disk image, asked through the main window with Windows' open dialog before anything open is closed; the context is the folder to start in (a string), the answer carries the chosen full path (ANS_sText)
        MakeFsOptions = 30,               // the options of a filesystem about to be made (type, block size, label, zero-fill; a whole disk's size), asked through the partition window; the answer carries them (ANS_objValue, an MFC_clsMakeFsChoice)
        DeviceWriteFile = 41,             // Write Image File to Device's image file, asked through the main window with Windows' open dialog (images plain or compressed); the context is the folder to start in (a string), the answer carries the chosen full path (ANS_sText); the device is then asked as a DeviceChoice
        DeviceRead = 40,                  // Save Device to Image File's whole choice, asked through the main window: the device picker in its read mode (the device, how much -- all, a typed length or through its backup GPT -- and the .xz box), then Windows' save dialog for the image file; the context is an RDQ_clsDeviceReadQuestion (the disks listed, each GPT disk's end, the folder to start in), the answer carries an RDI_clsDeviceReadIntent (ANS_objValue)
        DeviceChoice = 39,                // a device an operation works on (Open Device, Check a device ...), asked through the main window with the device picker; the context is a DCQ_clsDeviceQuestion (the disks listed, the picker's caption and range mode), the answer carries a DCH_clsDeviceChoice (ANS_objValue)
        NewImagePath = 38,                // New Image's Browse: the file to create, asked through the main window with the standard save dialog; the context is the path typed so far (a string), the answer carries the chosen full path (ANS_sText)
        NewImageDisk = 37,                // New Image's Match a Device: the disk whose length the size takes, asked through the main window; the context is the disk list (a List<DEV_clsDisk>), or the reason none could be read (a string, shown and answered Cancel); the answer carries the chosen DEV_clsDisk (ANS_objValue), a disk longer than New Image's size box takes refused and answered Cancel
        NewImageFile = 36,                // New Image's file and size (its dialog), asked through the main window; the context is an NIQ_clsNewImageQuestion, the answer carries an NIC_clsNewImageChoice (ANS_objValue) -- accepted, or Browse / Match asked with what was typed
        GrowPartition = 35,               // Expand Filesystem on a partition with free space after it: grow the partition (in the table copy) to take it?  Yes / No, Yes the default; the context is the question's text (a string)
        DiscardEdits = 34,                // Refresh over a partition table with unwritten edits: discard them and read the disk again?  Yes / No, No the default
        AddPartition = 33,                // a new partition's place, type and name (the Add dialog), asked through the partition window; the context is an ADQ_clsAddQuestion, the answer carries an ADC_clsAddChoice (ANS_objValue)
        NewTableKind = 32                 // the kind of a new partition table (MBR, GPT or none), asked through the partition window; the context is the present kind (a boxed int), the answer carries the chosen PTB_KIND_* (ANS_objValue, a boxed int)
    }

    /* The answers a question can be given; an answer counts only when the   */
    /* question that is current accepts it -- documentation table.           */
    internal enum ANK_enmAnswerKind
    {
        Cancel = 0,            // no: also what a question whose window disappears is answered
        Yes = 1,               // agreed
        No = 2,                // refused
        Closed = 3,            // the editor (or window) has closed, unchanged; for the "not shown" questions, the awaited fact
        Discard = 4,           // close the changed editor without saving
        Save = 5,              // save first: the answer carries the editor's stage
        Overwrite = 6,         // a conflict: replace this destination
        OverwriteAll = 7,      // a conflict: replace this one and every later one
        Skip = 8,              // a conflict: keep this destination
        SkipAll = 9,           // a conflict: keep this one and every later one
        KeepFile = 10,         // a manifest clash: extract the image item under that name, no manifest
        KeepManifest = 11,     // a manifest clash: write the manifest, leave the item out
        Retry = 12,            // the UI is still settling answers: look again, with no engine work in between
        OpenText = 13,         // a viewer choice: text (no permission to change anything)
        OpenHex = 14,          // a viewer choice: hex (no permission to change anything)
        SelectOffset = 15,     // the typed byte offset; the length comes from the facts already read
        SelectHostTarget = 16  // an editor's close: choose a Windows file to save to (no write or close permission)
    }

    /*------------------------------------------------------------------------*/
    /* TGT_clsTarget -- a target: a Windows file or device, and the byte      */
    /* range in it holding a filesystem (or being partitioned), with the      */
    /* disk's size and sector width.  The worker compares targets by these    */
    /* values; that the engine is not handed the same object twice by         */
    /* another name is the engine's own test -- documentation table.          */
    /*------------------------------------------------------------------------*/
    internal sealed class TGT_clsTarget
    {
        internal string TGT_sPath;        // the file's or device's path, as captured
        internal ulong TGT_ullOffset;     // where the range starts, bytes
        internal ulong TGT_ullLength;     // the range's length, bytes; 0 for "to the end"
        internal string TGT_sDeviceText;  // a device's description for display; "" for a file; never compared
        internal ulong TGT_ullDiskBytes;  // the whole file's or device's size, bytes (the partition window's model)
        internal uint TGT_uiSectorBytes;  // its logical sector size, bytes

        /*--------------------------------------------------------------------*/
        /* TGT_clsTarget:                                                     */
        /*                                                                    */
        /* A target, its values kept as given.                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sPath         : the file's or device's path; required.         */
        /*     ullOffset     : the range's start, bytes.                      */
        /*     ullLength     : its length, bytes; 0 for "to the end".         */
        /*     sDeviceText   : a description for display, or null.            */
        /*     ullDiskBytes  : the whole file's or device's size, bytes.      */
        /*     uiSectorBytes : its logical sector size, bytes.                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the target; an ArgumentException for an empty  */
        /*                     path.                                          */
        /*--------------------------------------------------------------------*/
        internal TGT_clsTarget(string sPath, ulong ullOffset, ulong ullLength,
            string sDeviceText, ulong ullDiskBytes, uint uiSectorBytes)
        {
            if (String.IsNullOrEmpty(sPath))
            {   /* No path: refused.  A target is never completed later from   */
                /* whatever a pane shows.                                      */
                throw new ArgumentException("A target path is required.", "sPath");
            }

            /* The range and the disk's facts, exactly as given.  None of     */
            /* them, the sector size included, is permission to write: that   */
            /* is the request's and the engine's business.                    */
            TGT_sPath = sPath;
            TGT_ullOffset = ullOffset;
            TGT_ullLength = ullLength;
            TGT_ullDiskBytes = ullDiskBytes;
            TGT_uiSectorBytes = uiSectorBytes;

            /* The description, "" when none was given, so it can be joined   */
            /* into text without a null test; it plays no part in comparing   */
            /* targets or in choosing the device.                             */
            TGT_sDeviceText = "";
            if (sDeviceText != null)
            {   /* Given: kept. */
                TGT_sDeviceText = sDeviceText;
            }
        }

        /*--------------------------------------------------------------------*/
        /* TGT_SameRange:                                                     */
        /*                                                                    */
        /* Whether another target names the same range: the same path         */
        /* (letter case aside), the same start and the same length.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objOther : the target to compare with, or null.                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when all three match; false for null or any        */
        /*            difference.                                             */
        /*--------------------------------------------------------------------*/
        internal bool TGT_SameRange(TGT_clsTarget objOther)
        {
            /* Compared as text: two spellings of one file (a short name, a      */
            /* "..") compare different, and one spelling of two objects cannot   */
            /* happen.  This is the worker's bookkeeping only; a raw write's     */
            /* refusal of the same object by another name is the engine's        */
            /* handle test (BFV_SameObject).                                     */
            return(objOther != null && String.Equals(TGT_sPath, objOther.TGT_sPath,
                StringComparison.OrdinalIgnoreCase) && TGT_ullOffset == objOther.TGT_ullOffset &&
                TGT_ullLength == objOther.TGT_ullLength);
        }
    }

    /*------------------------------------------------------------------------*/
    /* RQT_clsRequest -- a request, as the UI posts it: what is asked for and */
    /* everything it needs, captured when it was made.  Posting hands it to   */
    /* the worker; the poster changes nothing in it afterwards, and the       */
    /* worker never fills a gap from what a window shows now --               */
    /* documentation table.                                                   */
    /*------------------------------------------------------------------------*/
    internal sealed class RQT_clsRequest
    {
        internal KND_enmKind RQT_enmKind;      // what is asked for: decides its scheduling class, its state gate and its sequence
        internal long RQT_lOrigin;             // the id of the window that asked; 0 for the main window
        internal long RQT_lEpoch;              // the filesystem lifetime the poster saw; a request for an older one is refused by its gate
        internal TGT_clsTarget RQT_objTarget;  // the target or range; null for a request that needs none
        internal List<string> RQT_lstPaths;    // the chosen items' paths, copied when the request was made
        internal long RQT_lRevision;           // a save's content revision, or the kind's own serial or token (a Cancel: the serial it stops)
        internal uint RQT_uiFlags;             // the kind's own options, captured
        internal object RQT_objPayload;        // the kind's own arguments (CommandArguments.cs); data only, never code to run
        internal string RQT_sIdentity;         // the key that makes an equal waiting request a duplicate; "" when unused
        internal string RQT_sStream;           // the stream a Latest request replaces within (origin and source); "" when unused
        internal string RQT_sFolder;           // the image folder to list again afterwards, captured
        internal long RQT_lSerial;             // 0 until posted; then the serial SYS_Post gives it, once, under the queue's lock

        /*--------------------------------------------------------------------*/
        /* RQT_clsRequest:                                                    */
        /*                                                                    */
        /* A request, ready to post.  The kind's arguments are already        */
        /* captured in the payload, and kept as they are; the paths are       */
        /* copied here.                                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     enmKind    : what is asked for; its scheduling class follows   */
        /*                  from it, never from the poster.                   */
        /*     lOrigin    : the asking window's id.                           */
        /*     lEpoch     : the filesystem lifetime the poster saw.           */
        /*     objTarget  : the target or range, or null.                     */
        /*     arrPaths   : the chosen items' paths, copied; null for none.   */
        /*     lRevision  : a content revision, or the kind's own token.      */
        /*     uiFlags    : the kind's options.                               */
        /*     objPayload : the kind's arguments; the poster stops changing   */
        /*                  them.                                             */
        /*     sIdentity  : the duplicate key, or null for none.              */
        /*     sStream    : the replacement stream, or null for none.         */
        /*     sFolder    : the image folder to list afterwards; optional,    */
        /*                  "/" by default, and null also reads as "/".       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the request, not yet posted.  Posting it hands */
        /*                     over any stage it carries, even when it is     */
        /*                     refused (NST_Release then deletes it).         */
        /*--------------------------------------------------------------------*/
        internal RQT_clsRequest(KND_enmKind enmKind, long lOrigin, long lEpoch, TGT_clsTarget objTarget,
            string[] arrPaths, long lRevision, uint uiFlags, object objPayload,
            string sIdentity, string sStream, string sFolder = "/")
        {
            /* The values as given.  The payload was captured when its          */
            /* arguments were made, so it is kept as it is, not copied again.   */
            RQT_enmKind = enmKind;
            RQT_lOrigin = lOrigin;
            RQT_lEpoch = lEpoch;
            RQT_objTarget = objTarget;
            RQT_lRevision = lRevision;
            RQT_uiFlags = uiFlags;
            RQT_objPayload = objPayload;

            /* The paths copied into a list of the request's own, so a later   */
            /* change to the caller's array cannot change what queued work     */
            /* uses.  Strings never change, so copying the list is enough.     */
            if (arrPaths == null)
            {   /* None: an empty list -- the request names no items, and no   */
                /* item is ever read from a control in their place.            */
                RQT_lstPaths = new List<string>();
            }
            else
            {   /* Given: copied. */
                RQT_lstPaths = new List<string>(arrPaths);
            }

            /* The three texts: "" / "" / "/" unless given, so each can be      */
            /* compared and used without a null test.  A given value is kept    */
            /* exactly -- no case or culture change, so keys compare as made.   */
            RQT_sIdentity = "";
            RQT_sStream = "";
            RQT_sFolder = "/";
            if (sIdentity != null)
            {   /* A duplicate key given: kept. */
                RQT_sIdentity = sIdentity;
            }

            if (sStream != null)
            {   /* A stream given: kept. */
                RQT_sStream = sStream;
            }

            if (sFolder != null)
            {   /* A folder given: kept. */
                RQT_sFolder = sFolder;
            }
        }

        /*--------------------------------------------------------------------*/
        /* RQT_MakeKey:                                                       */
        /*                                                                    */
        /* One text key from several values -- a request's duplicate key or   */
        /* its stream (CommandArguments.cs, CommandClipboard.cs).  Two lists  */
        /* of values give the same key only when they are the same values in  */
        /* the same order.                                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrValues : the values, in order; a null value is kept apart   */
        /*                 from "".                                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the key.                                              */
        /*--------------------------------------------------------------------*/
        internal static string RQT_MakeKey(params string[] arrValues)
        {
            StringBuilder objKey;
            string sValue;
            int i;

            /* Each value written as its length, a colon, then the value       */
            /* itself, so no character inside a value -- a colon included --   */
            /* can be read as a boundary.  Callers turn numbers into text      */
            /* invariantly before they pass them.                              */
            objKey = new StringBuilder();
            for (i = 0; i < arrValues.Length; i++)
            {   /* One value per iteration.                                      */
                /* A null value written as length -1, which no real value has,   */
                /* so a missing value and an empty one give different keys --    */
                /* else two different requests could be dropped as equal.        */
                sValue = arrValues[i];
                if (sValue == null)
                {   /* Missing: the -1 mark. */
                    objKey.Append("-1:");
                }
                else
                {   /* Present: its length, the colon, then every character. */
                    objKey.Append(sValue.Length.ToString(CultureInfo.InvariantCulture));
                    objKey.Append(':');
                    objKey.Append(sValue);
                }
            }

            /* The key. */
            return(objKey.ToString());
        }
    }

    /*------------------------------------------------------------------------*/
    /* RSL_clsResult -- what a step or a sequence returns: its status, its    */
    /* detail, and its finished data.  Made once; nothing changes it after    */
    /* -- documentation table.                                                */
    /*------------------------------------------------------------------------*/
    internal sealed class RSL_clsResult
    {
        internal int RSL_iStatus;        // 0 for success; otherwise the engine's status as returned, or the system's own (SYS_*, CommandSystem.cs)
        internal string RSL_sDetail;     // what went wrong, in words; "" for an ordinary success
        internal object RSL_objPayload;  // the finished data (a report, a record that owns a file, a warning), or null

        /*--------------------------------------------------------------------*/
        /* RSL_clsResult:                                                     */
        /*                                                                    */
        /* A result, its values kept as given.                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iStatus    : the status, 0 for success.                        */
        /*     sDetail    : the words, or null for none.                      */
        /*     objPayload : the finished data, or null.                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the result.                                    */
        /*--------------------------------------------------------------------*/
        internal RSL_clsResult(int iStatus, string sDetail, object objPayload)
        {
            /* The status and the data, together: a status alone would lose   */
            /* the engine's own code and a successful save's warning.         */
            RSL_iStatus = iStatus;
            RSL_objPayload = objPayload;

            /* The detail, "" when none was given.  A producer reads the   */
            /* engine's detail before its next engine call, so the words   */
            /* given are those of this outcome.                            */
            RSL_sDetail = "";
            if (sDetail != null)
            {   /* Given: kept. */
                RSL_sDetail = sDetail;
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* EDR_clsEditor -- the worker's record of one open editor: where its     */
    /* Save now writes (an image file, or a Windows file), the last revision  */
    /* saved, and the read-out file it was opened from, which this record     */
    /* owns until the editor closes.  A Save As to somewhere else replaces    */
    /* the record but keeps the editor's id and where it was opened from --   */
    /* documentation table.                                                   */
    /*------------------------------------------------------------------------*/
    internal sealed class EDR_clsEditor
    {
        internal long EDR_lId;                            // the editor's id, kept across a Save As to another filesystem
        internal long EDR_lEpoch;                         // the filesystem lifetime its Save writes into; 0 for a Windows file
        internal TGT_clsTarget EDR_objTarget;             // the target its Save writes into; null for a Windows file
        internal string EDR_sEntry;                       // where its Save writes: the image path, or the full Windows path
        internal long EDR_lSavedRevision;                 // the newest revision saved; -1 until a save
        internal OED_clsOpenedEntryData EDR_objResource;  // the read-out file it opened (CommandNativeOperations.cs), released once the editor has closed; null when none
        internal long EDR_lSourceEpoch;                   // the filesystem lifetime it was opened from, kept through a Save As; 0 when opened on a Windows file
        internal TGT_clsTarget EDR_objSourceTarget;       // the target it was opened from, kept through a Save As; null when opened on a Windows file
        internal long EDR_lSaveOrder;                     // the newest successful save's order; 0 until one
        internal bool EDR_bPresented;                     // true once the UI has reported the editor's window made; never set back

        /*--------------------------------------------------------------------*/
        /* EDR_clsEditor:                                                     */
        /*                                                                    */
        /* An editor's record: a new one, or a replacement for objPrevious    */
        /* after a Save As.                                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lId            : the editor's id.                              */
        /*     lEpoch         : the lifetime its Save writes into, 0 for a    */
        /*                      Windows file.                                 */
        /*     objTarget      : the target its Save writes into, null for a   */
        /*                      Windows file.                                 */
        /*     sEntry         : where its Save writes.                        */
        /*     lSavedRevision : the newest revision saved; optional, -1.      */
        /*     objResource    : the read-out file it owns; optional, null.    */
        /*     objPrevious    : the record this replaces; optional, null for  */
        /*                      a new editor.                                 */
        /*     lSaveOrder     : the newest successful save's order; optional, */
        /*                      0.                                            */
        /*     bPresented     : the window is made; optional, false.          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the record.  Whether the editor holds unsaved  */
        /*                     changes is not kept here -- the editor knows.  */
        /*--------------------------------------------------------------------*/
        internal EDR_clsEditor(long lId, long lEpoch, TGT_clsTarget objTarget, string sEntry,
            long lSavedRevision = -1, OED_clsOpenedEntryData objResource = null,
            EDR_clsEditor objPrevious = null, long lSaveOrder = 0, bool bPresented = false)
        {
            /* Where Save writes, and the saved revision, as given: a       */
            /* successful Save As moves both at once.  Neither comes from   */
            /* the pane, nor from the id.                                   */
            EDR_lId = lId;
            EDR_lEpoch = lEpoch;
            EDR_objTarget = objTarget;
            EDR_sEntry = sEntry;
            EDR_lSavedRevision = lSavedRevision;
            EDR_objResource = objResource;
            EDR_lSaveOrder = lSaveOrder;

            /* Where the editor was opened from: set once, by the first        */
            /* record, and carried through every replacement -- it goes with   */
            /* the read-out file, not with where Save now writes, and it is    */
            /* what decides which session's close the editor belongs to        */
            /* (CommandSequences.cs).                                          */
            if (objPrevious == null)
            {   /* A new editor: opened from where its Save writes. */
                EDR_lSourceEpoch = lEpoch;
                EDR_objSourceTarget = objTarget;
            }
            else
            {   /* A replacement: the first record's origin, kept. */
                EDR_lSourceEpoch = objPrevious.EDR_lSourceEpoch;
                EDR_objSourceTarget = objPrevious.EDR_objSourceTarget;
            }

            /* Made-and-reported is never undone by a replacement: a record   */
            /* for a window that exists must not read as one with no window   */
            /* (whose read-out file the worker could then delete).            */
            EDR_bPresented = bPresented;
            if (objPrevious != null && objPrevious.EDR_bPresented)
            {   /* The window already reported: still so. */
                EDR_bPresented = true;
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* STV_clsStateView -- the worker's state as the UI sees it, sent whole   */
    /* every time it changes (VWK_enmViewKind.State) and kept by the main     */
    /* window to grey menus and show what is open.  It is information only:   */
    /* the worker decides each request against its own state when it runs     */
    /* it -- documentation table.                                             */
    /*------------------------------------------------------------------------*/
    internal sealed class STV_clsStateView
    {
        internal MOD_enmMode STV_enmMode;      // the worker's mode when sent
        internal TGT_clsTarget STV_objTarget;  // the open session's or exclusive window's target; null in None
        internal long STV_lEpoch;              // the filesystem lifetime; kept across a same-range reopen for writing or reading
        internal bool STV_bWritable;           // true only once a writable open has succeeded
        internal bool STV_bRunning;            // true while a request runs: for greying and progress, never for admission
        internal bool STV_bExiting;            // true once an Exit has been agreed to
        internal long STV_lClipboardLifetime;  // stepped by every session close, even one that keeps the epoch: a cut captured under an older value is stale (CommandSequences.cs)

        /*--------------------------------------------------------------------*/
        /* STV_clsStateView:                                                  */
        /*                                                                    */
        /* A state view, its values kept as given.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     enmMode            : the worker's mode.                        */
        /*     objTarget          : the target, or null in None.              */
        /*     lEpoch             : the filesystem lifetime.                  */
        /*     bWritable          : a writable open has succeeded.            */
        /*     bRunning           : a request is running.                     */
        /*     bExiting           : an Exit has been agreed to.               */
        /*     lClipboardLifetime : the close count; optional, 0.             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the view -- values only, none of the worker's  */
        /*                     lists or any control.                          */
        /*--------------------------------------------------------------------*/
        internal STV_clsStateView(MOD_enmMode enmMode, TGT_clsTarget objTarget, long lEpoch,
            bool bWritable, bool bRunning, bool bExiting,
            long lClipboardLifetime = 0)
        {
            /* All the values in one object, sent whole, so the menus never   */
            /* see half an update.                                            */
            STV_enmMode = enmMode;
            STV_objTarget = objTarget;
            STV_lEpoch = lEpoch;
            STV_bWritable = bWritable;
            STV_bRunning = bRunning;
            STV_bExiting = bExiting;
            STV_lClipboardLifetime = lClipboardLifetime;
        }
    }

    /*------------------------------------------------------------------------*/
    /* VWM_clsView -- one view on its way to the UI: its kind, the request it */
    /* belongs to (so a window can tell a stale view from a current one), and */
    /* its value.  The code that shows it is the UI's own -- documentation    */
    /* table.                                                                 */
    /*------------------------------------------------------------------------*/
    internal sealed class VWM_clsView
    {
        internal VWK_enmViewKind VWM_enmKind;    // what kind of view
        internal RQT_clsRequest VWM_objRequest;  // the request it belongs to: its origin and serial
        internal object VWM_objValue;            // the finished data, or the question with its answer channel

        /*--------------------------------------------------------------------*/
        /* VWM_clsView:                                                       */
        /*                                                                    */
        /* A view, its values kept as given.                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     enmKind    : what kind of view.                                */
        /*     objRequest : the request it belongs to.                        */
        /*     objValue   : its data; the producer stops changing it.         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the view.  How it is delivered -- queued to    */
        /*                     the window's thread, or straight to a drag's   */
        /*                     waiting thread -- is the sender's business.    */
        /*--------------------------------------------------------------------*/
        internal VWM_clsView(VWK_enmViewKind enmKind, RQT_clsRequest objRequest, object objValue)
        {
            /* Kept as given; nothing here touches a control. */
            VWM_enmKind = enmKind;
            VWM_objRequest = objRequest;
            VWM_objValue = objValue;
        }
    }

    /* The worker's two calls into the native steps, as named function          */
    /* pointers bound once (STB_clsStepTable): run a step, and release what a   */
    /* finished request owned.  Neither is ever carried in a request.           */

    /*------------------------------------------------------------------------*/
    /* STB_RunStep:                                                           */
    /*                                                                        */
    /* The shape of the step runner (NST_Run, CommandNativeSteps.cs): run one */
    /* step synchronously and return its result.                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     enmStep      : the step the sequence chose.                        */
    /*     objRequest   : the request whose arguments it uses.                */
    /*     objExecution : the running command's services.                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     RSL_clsResult : the step's result; the step is over when it        */
    /*                     returns.                                           */
    /*------------------------------------------------------------------------*/
    internal delegate RSL_clsResult STB_RunStep(STP_enmStep enmStep,
        RQT_clsRequest objRequest, EXC_clsExecution objExecution);

    /*------------------------------------------------------------------------*/
    /* STB_ReleaseRequest:                                                    */
    /*                                                                        */
    /* The shape of the request release (NST_Release, CommandNativeSteps.cs): */
    /* delete the stage files a finished request carried.                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objRequest : the request, finished or refused at posting.          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : its stages released; no engine call is made.                */
    /*------------------------------------------------------------------------*/
    internal delegate void STB_ReleaseRequest(RQT_clsRequest objRequest);

    /*------------------------------------------------------------------------*/
    /* STB_clsStepTable -- the two native-step calls, bound once for the      */
    /* worker's life and reached only through STB_Run and STB_Release --      */
    /* documentation table.                                                   */
    /*------------------------------------------------------------------------*/
    internal sealed class STB_clsStepTable
    {
        private STB_RunStep Stb_pfnRun;             // the step runner; called only by STB_Run
        private STB_ReleaseRequest Stb_pfnRelease;  // the request release; called only by STB_Release

        /*--------------------------------------------------------------------*/
        /* STB_clsStepTable:                                                  */
        /*                                                                    */
        /* The table, bound to the two calls.                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pfnRun     : the step runner.                                  */
        /*     pfnRelease : the request release.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the table; an ArgumentNullException for a      */
        /*                     missing call.                                  */
        /*--------------------------------------------------------------------*/
        internal STB_clsStepTable(STB_RunStep pfnRun, STB_ReleaseRequest pfnRelease)
        {
            if (pfnRun == null)
            {   /* No runner: refused -- the worker could run no step. */
                throw new ArgumentNullException("pfnRun");
            }

            if (pfnRelease == null)
            {   /* No release: refused -- a refused or dropped request's   */
                /* stage would never be deleted.                           */
                throw new ArgumentNullException("pfnRelease");
            }

            /* The two calls kept.  Which step runs when is still the   */
            /* sequences' to decide; the table only makes the calls.    */
            Stb_pfnRun = pfnRun;
            Stb_pfnRelease = pfnRelease;
        }

        /*--------------------------------------------------------------------*/
        /* STB_Run:                                                           */
        /*                                                                    */
        /* Run one step through the bound runner, on the calling thread.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     enmStep      : the step the sequence chose.                    */
        /*     objRequest   : the request whose arguments it uses.            */
        /*     objExecution : the running command's services.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the step's result, complete before the next    */
        /*                     step can run.                                  */
        /*--------------------------------------------------------------------*/
        internal RSL_clsResult STB_Run(STP_enmStep enmStep,
            RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            /* A plain call on the worker: no queue and no other thread. */
            return(Stb_pfnRun(enmStep, objRequest, objExecution));
        }

        /*--------------------------------------------------------------------*/
        /* STB_Release:                                                       */
        /*                                                                    */
        /* Release what a finished request carried, through the bound         */
        /* release.                                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the request, finished, refused or dropped.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : its stages released.                                    */
        /*--------------------------------------------------------------------*/
        internal void STB_Release(RQT_clsRequest objRequest)
        {
            /* Apart from the steps, so a request refused at posting can have   */
            /* its stage deleted without any filesystem work being started.     */
            Stb_pfnRelease(objRequest);
        }
    }
}
