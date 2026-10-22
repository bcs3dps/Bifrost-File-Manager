/*----------------------------------------------------------------------------*/
/* CommandInputs.cs                                                           */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The command system's INPUTS: every window that asks the user for values    */
/* (a Save As target, an image folder, a Windows file, a device, a format's   */
/* options, a new file's name, Properties) -- the records that carry what     */
/* was asked and what was chosen, the worker's side (accepting an input,      */
/* holding other work while it is open, and closing inputs before an editor   */
/* or the image is closed), and the UI's side (each input window's record,    */
/* its end reported only once the window has really returned).  A chosen      */
/* value is copied out before its window goes, so no window, list or handle   */
/* ever reaches a request.                                                    */
/*----------------------------------------------------------------------------*/

/* The framework: ArgumentException, Enum and the other base types         */
/* (System); the lists and the GPT-end table (Collections.Generic); the    */
/* invalid file-name characters (IO); and the forms, the event arguments   */
/* and the Windows file dialogs (Windows.Forms).                           */
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* TIN_TargetChosen:                                                      */
    /*                                                                        */
    /* The shape of the call that hands an editor its chosen Save As target   */
    /* (MainCommand.cs holds one per pending target request).                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objTarget : the target's descriptor, or null when nothing was      */
    /*                 chosen.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the editor then saves to it, or does nothing for null.      */
    /*------------------------------------------------------------------------*/
    internal delegate void TIN_TargetChosen(OFD_clsOpenedFile objTarget);

    /*------------------------------------------------------------------------*/
    /* TIN_clsTargetIntent -- one editor's Save As target request, held by    */
    /* the main window while its picker is open (MainCommand.cs               */
    /* Main_dictTargets, under the picker's request).  UI side only: nothing  */
    /* in it goes to the worker -- documentation table.                       */
    /*------------------------------------------------------------------------*/
    internal sealed class TIN_clsTargetIntent
    {
        internal Form TIN_frmOwner;                 // the editor asking; it may close before a target is chosen
        internal OFD_clsOpenedFile TIN_objCurrent;  // the editor's descriptor when it asked: where it saved then, and its opening facts
        internal TIN_TargetChosen TIN_pfnChosen;    // the editor's call for the chosen target; the request's entry is taken out of the table before the picker runs

        /*--------------------------------------------------------------------*/
        /* TIN_clsTargetIntent:                                               */
        /*                                                                    */
        /* The request, kept as given.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmOwner   : the editor asking.                                */
        /*     objCurrent : its descriptor now; a later save of the editor    */
        /*                  does not change it.                               */
        /*     pfnChosen  : the call that hands the editor its target.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the request; it holds no content, form or      */
        /*                     file of its own.                               */
        /*--------------------------------------------------------------------*/
        internal TIN_clsTargetIntent(Form frmOwner, OFD_clsOpenedFile objCurrent, TIN_TargetChosen pfnChosen)
        {
            /* Kept as given.  Only where to save is chosen here; the editor   */
            /* makes its content's snapshot after a target is returned.        */
            TIN_frmOwner = frmOwner;
            TIN_objCurrent = objCurrent;
            TIN_pfnChosen = pfnChosen;
        }
    }

    /*------------------------------------------------------------------------*/
    /* DCH_clsDeviceChoice -- the device a device picker returned, copied     */
    /* out before the picker and its rows are disposed: no row, list or       */
    /* window handle is kept -- documentation table.                          */
    /*------------------------------------------------------------------------*/
    internal sealed class DCH_clsDeviceChoice
    {
        internal TGT_clsTarget DCH_objTarget;  // the whole device: its path, size, sector size and the description its command shows
        internal string DCH_sName;             // the device's model text as the picker spells it (DVF_ModelText: trimmed, runs of spaces made one), or PhysicalDriveN when it reports none
        internal ulong DCH_ullRange;           // the leading bytes chosen; 0 for all, and for a picker with no range question

        /*--------------------------------------------------------------------*/
        /* DCH_clsDeviceChoice:                                               */
        /*                                                                    */
        /* The chosen device, copied out of its row.                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objDisk     : the picker's chosen row; only read here.         */
        /*     ullRange    : the range chosen; 0 for none.                    */
        /*     bBrowseName : true for Open / Check / Partition Device (the    */
        /*                   description built here, an unnamed device        */
        /*                   named by its physical number); false for Write   */
        /*                   Device (the raw-write description,               */
        /*                   MAIN_CommandRawTarget).                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the choice; an ArgumentException for no row.   */
        /*--------------------------------------------------------------------*/
        internal DCH_clsDeviceChoice(DEV_clsDisk objDisk, ulong ullRange, bool bBrowseName)
        {
            string sName;

            if (objDisk == null)
            {   /* No row: refused -- a choice is never completed from what   */
                /* the main window has open.                                  */
                throw new ArgumentException("No device was selected.");
            }

            /* The model text as the device picker spells it                  */
            /* (PartitionForm.cs DVF_ModelText: the padding at either end     */
            /* taken off and every run of spaces made one), so the picker,    */
            /* the partition question's caption and the opened device's       */
            /* title name it one way -- or, when it reports none,             */
            /* PhysicalDriveN, the name the device commands give an unnamed   */
            /* disk.                                                          */
            sName = DVF_clsDeviceForm.DVF_ModelText(objDisk.DEV_stInfo);
            DCH_sName = sName;
            if (sName.Length == 0)
            {   /* No model text: named by its physical drive number. */
                DCH_sName = "PhysicalDrive" + objDisk.DEV_iNumber;
            }

            /* The range, and the whole device as the target with the    */
            /* description its command will show -- all copied values.   */
            DCH_ullRange = ullRange;
            if (bBrowseName)
            {   /* Open, Check or Partition: "name, size, bus (path)", the   */
                /* name being the model text or PhysicalDriveN.              */
                DCH_objTarget = new TGT_clsTarget(objDisk.DEV_sPath, 0, 0, DCH_sName + ", " +
                    DVF_clsDeviceForm.DVF_SizeText(objDisk.DEV_stInfo.DVI_ullSize) + ", " + DVF_clsDeviceForm.DVF_BusText(objDisk.DEV_stInfo.DVI_uiBus) +
                    " (" + objDisk.DEV_sPath + ")", objDisk.DEV_stInfo.DVI_ullSize, objDisk.DEV_stInfo.DVI_uiSectorBytes);
            }
            else
            {   /* Write Device: the raw-write target and its own wording   */
                /* (MainCommand.cs MAIN_CommandRawTarget).                  */
                DCH_objTarget = MAIN_clsMainForm.MAIN_CommandRawTarget(objDisk);
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* DCQ_clsDeviceQuestion -- the DeviceChoice question's context: the      */
    /* disks the worker listed and how the device picker is to show them,     */
    /* for an operation that asks its device as a question of its own         */
    /* sequence -- documentation table.                                       */
    /*------------------------------------------------------------------------*/
    internal sealed class DCQ_clsDeviceQuestion
    {
        internal List<DEV_clsDisk> DCQ_lstDisks;  // the disks, as the worker listed them just before asking; the picker only reads them
        internal string DCQ_sTitle;               // the picker's caption: what the device is chosen for
        internal int DCQ_iRange;                  // the picker's range mode (DVF_clsDeviceForm.DVF_RANGE_*)
        internal ulong DCQ_ullImageBytes;         // the image's length for a write's range, 0 otherwise
        internal bool DCQ_bBrowseName;            // how the choice describes the device (DCH_clsDeviceChoice's bBrowseName)

        /*--------------------------------------------------------------------*/
        /* DCQ_clsDeviceQuestion:                                             */
        /*                                                                    */
        /* The question's context, kept as given.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstDisks      : the disks listed.                              */
        /*     sTitle        : the picker's caption.                          */
        /*     iRange        : the picker's range mode.                       */
        /*     ullImageBytes : the image's length, or 0.                      */
        /*     bBrowseName   : the choice's description kind.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the record.                                    */
        /*--------------------------------------------------------------------*/
        internal DCQ_clsDeviceQuestion(List<DEV_clsDisk> lstDisks, string sTitle, int iRange, ulong ullImageBytes,
            bool bBrowseName)
        {
            /* Kept as given; the worker makes one per question. */
            DCQ_lstDisks = lstDisks;
            DCQ_sTitle = sTitle;
            DCQ_iRange = iRange;
            DCQ_ullImageBytes = ullImageBytes;
            DCQ_bBrowseName = bBrowseName;
        }
    }

    /*------------------------------------------------------------------------*/
    /* RDQ_clsDeviceReadQuestion -- Read Device still to be asked: posted as  */
    /* the ReadDevice request's arguments with only the folder its save       */
    /* dialog starts in, and made again by the request's sequence as the      */
    /* DeviceRead question's context, with the disks it has just listed and   */
    /* each GPT disk's end -- documentation table.                            */
    /*------------------------------------------------------------------------*/
    internal sealed class RDQ_clsDeviceReadQuestion
    {
        internal List<DEV_clsDisk> RDQ_lstDisks;             // the question's: the disks the worker listed just before asking (the picker only reads them); null in the posted request
        internal Dictionary<string, ulong> RDQ_dictGptEnds;  // the question's: each listed GPT disk's path to the bytes through its backup header's sector (the picker's GPT box); null in the posted request
        internal string RDQ_sStartFolder;                    // the folder the image file's save dialog starts in (the shared image folder when the command was given)

        /*--------------------------------------------------------------------*/
        /* RDQ_clsDeviceReadQuestion:                                         */
        /*                                                                    */
        /* The record, kept as given.  The menu posts it with no disks and no */
        /* GPT ends; the sequence makes its own with both before it asks.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstDisks     : the disks listed, or null.                      */
        /*     dictGptEnds  : the GPT disks' ends, or null.                   */
        /*     sStartFolder : the save dialog's starting folder; null is      */
        /*                    kept as "".                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the record.                                    */
        /*--------------------------------------------------------------------*/
        internal RDQ_clsDeviceReadQuestion(List<DEV_clsDisk> lstDisks, Dictionary<string, ulong> dictGptEnds,
            string sStartFolder)
        {
            /* Kept as given; the folder never null, as it is part of the   */
            /* request's identity and a dialog's starting point.            */
            RDQ_lstDisks = lstDisks;
            RDQ_dictGptEnds = dictGptEnds;
            RDQ_sStartFolder = "";
            if (sStartFolder != null)
            {   /* A folder given. */
                RDQ_sStartFolder = sStartFolder;
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* RDI_clsDeviceReadIntent -- what Read Device's question chose (the      */
    /* DeviceRead answer), copied out of its dialogs: no form, list, handle   */
    /* or open file in it -- documentation table.                             */
    /*------------------------------------------------------------------------*/
    internal sealed class RDI_clsDeviceReadIntent
    {
        internal TGT_clsTarget RDI_objTarget;  // the whole device: its description, size and sector size
        internal ulong RDI_ullRange;           // the leading bytes to read; 0 for the whole device
        internal bool RDI_bXz;                 // the compression box, whatever the output name's suffix
        internal string RDI_sOutput;           // the chosen Windows file, as the save dialog returned it (its extension added)
        internal bool RDI_bThroughGpt;         // the GPT box: read through the backup GPT header's sector (RDI_ullRange then 0)

        /*--------------------------------------------------------------------*/
        /* RDI_clsDeviceReadIntent:                                           */
        /*                                                                    */
        /* The choice, kept as given.  The worker checks it all again before  */
        /* it reads.                                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objTarget   : the device, copied before its picker was         */
        /*                   disposed.                                        */
        /*     ullRange    : the leading bytes; 0 for all.                    */
        /*     bXz         : compress the image.                              */
        /*     sOutput     : the chosen output file.                          */
        /*     bThroughGpt : the GPT box was ticked; the read sequence works  */
        /*                   out the length; optional, false.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the choice; an ArgumentException for no device */
        /*                     or no output file.                             */
        /*--------------------------------------------------------------------*/
        internal RDI_clsDeviceReadIntent(TGT_clsTarget objTarget, ulong ullRange, bool bXz, string sOutput,
            bool bThroughGpt = false)
        {
            if (objTarget == null || String.IsNullOrEmpty(sOutput))
            {   /* No device or no output file: refused, so a cancelled input   */
                /* can never pass for a complete one.                           */
                throw new ArgumentException("Device imaging needs a device and an output filename.");
            }

            /* Kept as given: copied values only, nothing of the windows. */
            RDI_objTarget = objTarget;
            RDI_ullRange = ullRange;
            RDI_bXz = bXz;
            RDI_sOutput = sOutput;
            RDI_bThroughGpt = bThroughGpt;
        }
    }

    /*------------------------------------------------------------------------*/
    /* WDQ_clsDeviceWriteQuestion -- Write Device still to be asked: the      */
    /* WriteDevice request's arguments as the menu posts it, with the folder  */
    /* its image file dialog starts in; its sequence records the image once   */
    /* chosen, for the completion -- documentation table.                     */
    /*------------------------------------------------------------------------*/
    internal sealed class WDQ_clsDeviceWriteQuestion
    {
        internal string WDQ_sStartFolder;  // the folder the image file's dialog starts in (the shared image folder when the command was given)
        internal string WDQ_sImage;        // "" until the sequence's file question is answered; then the chosen image, so the completion can remember its folder even when the device is not chosen

        /*--------------------------------------------------------------------*/
        /* WDQ_clsDeviceWriteQuestion:                                        */
        /*                                                                    */
        /* The record, as the menu posts it: no image chosen yet.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sStartFolder : the dialog's starting folder; null is kept as   */
        /*                    "".                                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the record.                                    */
        /*--------------------------------------------------------------------*/
        internal WDQ_clsDeviceWriteQuestion(string sStartFolder)
        {
            /* The folder never null, as it is part of the request's identity   */
            /* and a dialog's starting point; no image yet.                     */
            WDQ_sStartFolder = "";
            if (sStartFolder != null)
            {   /* A folder given. */
                WDQ_sStartFolder = sStartFolder;
            }

            WDQ_sImage = "";
        }
    }

    /*------------------------------------------------------------------------*/
    /* PIQ_clsPartitionImageQuestion -- File > Partition/Format > Image File  */
    /* still to be asked: the OpenPartition request's arguments as the menu   */
    /* posts it, with the folder its open dialog starts in.  Its sequence     */
    /* asks the file, records it, then makes the request the window over      */
    /* that file in place (its target, and a POA_clsPartitionOpenArgs for its */
    /* caption) -- documentation table.                                       */
    /*------------------------------------------------------------------------*/
    internal sealed class PIQ_clsPartitionImageQuestion
    {
        internal string PIQ_sStartFolder;  // the folder the disk image's open dialog starts in (the shared image folder when the command was given)
        internal string PIQ_sImage;        // "" until the sequence's file question is answered; then the chosen file, so the completion can remember its folder even when its length cannot be read

        /*--------------------------------------------------------------------*/
        /* PIQ_clsPartitionImageQuestion:                                     */
        /*                                                                    */
        /* The record, as the menu posts it: no file chosen yet.              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sStartFolder : the dialog's starting folder; null is kept as   */
        /*                    "".                                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the record.                                    */
        /*--------------------------------------------------------------------*/
        internal PIQ_clsPartitionImageQuestion(string sStartFolder)
        {
            /* The folder never null, as it is part of the request's identity   */
            /* and the dialog's starting point; no file yet.                    */
            PIQ_sStartFolder = "";
            if (sStartFolder != null)
            {   /* A folder given. */
                PIQ_sStartFolder = sStartFolder;
            }

            PIQ_sImage = "";
        }
    }

    /* The kinds of input, each with its own record in the worker while its   */
    /* window is open.  Only the two image browsers (ImageSaveTarget,         */
    /* ImageFolder: INA_IsImage) need the open filesystem while they run;     */
    /* every other input holds the worker's other work until it returns       */
    /* (Sys_HasValueInput) -- documentation table.                            */
    internal enum INK_enmInputKind
    {
        ImageSaveTarget,         // an editor's Save As to an entry in the open filesystem (an image browser)
        HostSaveTarget,          // an editor's Save As to a Windows file; no engine work
        ImageFolder,             // the image pane's folder picker, in the filesystem it captured (an image browser)
        HostOpenImageFile,       // Open Image's Windows file; refused while a check or partition window is open
        HostCheckImageFile,      // Check Filesystem's Windows image file; only with nothing open
        EntryProperties,         // an entry's Properties (mode, owner) from the row the click captured, a link's target read first
        FilesystemProperties,    // Filesystem Properties, from volume facts read for this request
        ImageNewFile,            // New File's name and size, for the folder of the filesystem it captured; write access (the risk question) is got before its window opens
        HostNewFile,             // New File's name and size for a Windows folder; the flags keep the document kind
        HostOpenEntry,           // opening a Windows file in an editor: its one path, with the viewer choice in the flags
        HostPaneAction           // a Windows-pane action (HPA_enmHostAction, in the flags), run while the input is held
    }

    /* The partition window's buttons with work of their own, and New         */
    /* Image's first question: the action an EditTable request carries        */
    /* (TBE_clsTableEdit).  Each press posts one such request and the         */
    /* worker runs it whole (CommandOperations.cs, Sys_TableEditSequence):    */
    /* it asks the button's question over the window and either makes the     */
    /* edit on the table copy the press carried, handed back for the window   */
    /* to show, or turns the request into the disk operation the button       */
    /* names and runs that in the same run -- documentation table.            */
    internal enum PAC_enmPartitionAction
    {
        AskKind = 1,     // New Image's first question over a fresh empty file: the table kind, then Make Filesystem over the whole file for "no table"
        NewTable = 2,    // New Table: the kind asked, then the copy replaced by a fresh empty table of that kind
        Add = 3,         // Add Partition: the dialog asked, then the entry added to the copy
        Delete = 4,      // Delete Partition: the selected entry taken out of the copy
        MakeFs = 5,      // Make Filesystem: the range found, then the request turned into the format, whose sequence asks for its options
        ReadPart = 6,    // Save Partition to File: the range found, then the request turned into the read, whose sequence asks for its file
        WritePart = 7,   // Write File to Partition: the range found, then the request turned into the write, whose sequence asks for its file
        ErasePart = 8,   // Erase Partition: the range found, then the request turned into the erase (its sequence confirms)
        Expand = 9,      // Expand Filesystem: the partition grown in the copy when free space follows it, else the request turned into the resize
        WriteTable = 10, // Write Table: the copy built into its pieces, then the request turned into the write (its sequence confirms)
        Refresh = 11     // Refresh: asked first when there are unwritten edits, then the request turned into the layout read
    }

    /* The Windows-pane actions, each run by the main window inside its own   */
    /* HostPaneAction input (MainCommand.cs) -- documentation table.          */
    internal enum HPA_enmHostAction
    {
        Navigate,            // show the captured folder; "" is the drive list
        NavigateBack,        // show the captured Back destination, taken off the Back history only when it still heads it
        PickFolder,          // the Windows folder chooser, starting at the captured folder
        OpenShell,           // open the one captured file in the program Windows has for it
        NewFolder,           // ask a name and make the folder in the captured folder (Main_HostNewFolder)
        Rename,              // rename the one captured path (Main_HostRename)
        Delete,              // send the captured paths to the Recycle Bin (Main_HostDelete)
        PasteCopy,           // copy the captured clipboard / drop paths into the captured folder (Main_HostPaste)
        PasteMove,           // move them there (Main_HostPaste)
        CopySelection,       // put the captured Windows selection on the clipboard, copied
        CutSelection,        // put it on the clipboard, cut
        Properties,          // Windows' Properties for the one captured path
        RefreshBoth,         // re-list the Windows pane, and the image pane when a filesystem is open
        ImageCopySelection,  // put the captured image selection on the clipboard with its source's identity
        ImageCutSelection    // the same, cut -- nothing is removed until a paste moves it
    }

    /*------------------------------------------------------------------------*/
    /* FMI_clsFormatInput -- the partition window's description of the range  */
    /* a format, or a partition Read or Write, is for: the request's payload  */
    /* while its options or its file are still to be asked, and then the      */
    /* context of that question (MakeFsOptions, CommandOperations.cs;         */
    /* PartitionFile, CommandRawSequence.cs).  The range itself is the        */
    /* request's target -- documentation table.                               */
    /*------------------------------------------------------------------------*/
    internal sealed class FMI_clsFormatInput
    {
        internal bool FMI_bWholeDisk;  // a format of the whole disk with no table: the options may make the filesystem smaller (NIM_AllowSmaller); false for Read / Write
        internal string FMI_sPlace;    // the range's short name, for the options' fixed target and the file dialogs' captions
        internal string FMI_sTarget;   // the range's full description (size, what it holds now), for the questions and messages

        /*--------------------------------------------------------------------*/
        /* FMI_clsFormatInput:                                                */
        /*                                                                    */
        /* The description, kept as given.                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bWholeDisk : the whole disk is chosen and has no table.        */
        /*     sPlace     : the short name.                                   */
        /*     sTarget    : the full description.                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the description; an ArgumentException for      */
        /*                     either text missing.                           */
        /*--------------------------------------------------------------------*/
        internal FMI_clsFormatInput(bool bWholeDisk, string sPlace, string sTarget)
        {
            if (String.IsNullOrEmpty(sPlace) || String.IsNullOrEmpty(sTarget))
            {   /* A text missing: refused -- the description is never taken   */
                /* later from what the window shows.                           */
                throw new ArgumentException("Format input needs its captured range description.");
            }

            /* Kept as given; the strings are the window's own, unchanged   */
            /* text, and nothing of the window is held.                     */
            FMI_bWholeDisk = bWholeDisk;
            FMI_sPlace = sPlace;
            FMI_sTarget = sTarget;
        }
    }

    /*------------------------------------------------------------------------*/
    /* TBE_clsTableEdit -- one partition-window button's press, as the        */
    /* EditTable request carries it to the worker: the action, a COPY of the  */
    /* table the window shows and what was selected, so the worker can ask    */
    /* the action's question and make its edit without touching the window.   */
    /* The worker edits the copy and hands this record back as the result's   */
    /* payload, with the slot to select, for the window to show -- the        */
    /* window's own table changes only then -- documentation table.           */
    /*------------------------------------------------------------------------*/
    internal sealed class TBE_clsTableEdit
    {
        internal PAC_enmPartitionAction TBE_enmAction;  // the button pressed
        internal PTB_Table TBE_objTable;                // a deep copy of the window's table at the press (PTB_CopyTable); the worker edits it, the window adopts it
        internal int TBE_iSlot;                         // the slot selected at the press (0 for a table-less disk's whole-disk row), or -1
        internal bool TBE_bModified;                    // whether the window's table had unwritten edits at the press
        internal string TBE_sName;                      // the window's short name for the disk, for questions and the ranges' names
        internal string TBE_sWholeContent;              // with no table: what the disk's start holds (the probe's words), "" when unknown
        internal int TBE_iSelect;                       // set by the worker: the slot the window selects once it shows the edited copy, or -1
        internal PTB_Range TBE_objClicked;              // the free gap last clicked in the window, a copy (Add starts there), or null; set after construction by the window

        /*--------------------------------------------------------------------*/
        /* TBE_clsTableEdit:                                                  */
        /*                                                                    */
        /* The press's record, checked and kept.  The table must be a copy    */
        /* made for this press: the worker edits it on its own thread.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     enmAction     : the button.                                    */
        /*     objTable      : the window's table, already copied.            */
        /*     iSlot         : the selected slot, or -1.                      */
        /*     bModified     : whether the table had unwritten edits.         */
        /*     sName         : the window's short name for the disk.          */
        /*     sWholeContent : what a table-less disk's start holds, or "".   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the record; an ArgumentException for an        */
        /*                     unknown action, no table or no name.           */
        /*--------------------------------------------------------------------*/
        internal TBE_clsTableEdit(PAC_enmPartitionAction enmAction, PTB_Table objTable, int iSlot,
            bool bModified, string sName, string sWholeContent)
        {
            if (!Enum.IsDefined(typeof(PAC_enmPartitionAction), enmAction) || objTable == null ||
                String.IsNullOrEmpty(sName))
            {   /* An action nothing names, no table, or no name: refused --   */
                /* nothing about the press is ever filled in later from what   */
                /* the window shows by then.                                   */
                throw new ArgumentException("A partition table edit needs its action, table copy and disk name.");
            }

            if (sWholeContent == null)
            {   /* No words for a table-less disk's start: "". */
                sWholeContent = "";
            }

            /* Kept as given; nothing selected for the window until the   */
            /* worker says.                                               */
            TBE_enmAction = enmAction;
            TBE_objTable = objTable;
            TBE_iSlot = iSlot;
            TBE_bModified = bModified;
            TBE_sName = sName;
            TBE_sWholeContent = sWholeContent;
            TBE_iSelect = -1;
            TBE_objClicked = null;
        }
    }

    /*------------------------------------------------------------------------*/
    /* ADQ_clsAddQuestion -- what the AddPartition question shows: the table  */
    /* copy being added to, its free gaps in disk order, and the gap to start */
    /* in.  All three are the worker's, untouched while it waits for the      */
    /* answer -- documentation table.                                         */
    /*------------------------------------------------------------------------*/
    internal sealed class ADQ_clsAddQuestion
    {
        internal PTB_Table ADQ_objTable;          // the table copy (the Add dialog validates against it, read only)
        internal List<PTB_Range> ADQ_lstGaps;     // its free gaps, in disk order; at least one
        internal PTB_Range ADQ_objStart;          // the gap the dialog proposes: the one last clicked, else the largest

        /*--------------------------------------------------------------------*/
        /* ADQ_clsAddQuestion:                                                */
        /*                                                                    */
        /* The question's facts, kept as given.                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objTable : the table copy.                                     */
        /*     lstGaps  : its free gaps.                                      */
        /*     objStart : the gap to start in.                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the record.                                    */
        /*--------------------------------------------------------------------*/
        internal ADQ_clsAddQuestion(PTB_Table objTable, List<PTB_Range> lstGaps, PTB_Range objStart)
        {
            /* Kept as given: the worker made all three for this question. */
            ADQ_objTable = objTable;
            ADQ_lstGaps = lstGaps;
            ADQ_objStart = objStart;
        }
    }

    /*------------------------------------------------------------------------*/
    /* ADC_clsAddChoice -- the AddPartition question's answer: the partition  */
    /* the Add dialog accepted (already validated by it against the table     */
    /* copy), carried in ANS_objValue -- documentation table.                 */
    /*------------------------------------------------------------------------*/
    internal sealed class ADC_clsAddChoice
    {
        internal ulong ADC_ullFirstLba;           // the first sector
        internal ulong ADC_ullLastLba;            // the last sector, inclusive
        internal PTB_TypeName ADC_objType;        // the type, named or made from what was typed
        internal string ADC_sName;                // the GPT name ("" otherwise)
        internal bool ADC_bBoot;                  // the MBR active flag

        /*--------------------------------------------------------------------*/
        /* ADC_clsAddChoice:                                                  */
        /*                                                                    */
        /* The dialog's accepted values, kept as given.                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     ullFirstLba : the first sector.                                */
        /*     ullLastLba  : the last sector, inclusive.                      */
        /*     objType     : the type.                                        */
        /*     sName       : the GPT name, or "".                             */
        /*     bBoot       : the MBR active flag.                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the record.                                    */
        /*--------------------------------------------------------------------*/
        internal ADC_clsAddChoice(ulong ullFirstLba, ulong ullLastLba, PTB_TypeName objType, string sName, bool bBoot)
        {
            /* Kept as given: read from the dialog before it was disposed. */
            ADC_ullFirstLba = ullFirstLba;
            ADC_ullLastLba = ullLastLba;
            ADC_objType = objType;
            ADC_sName = sName;
            ADC_bBoot = bBoot;
        }
    }

    /*------------------------------------------------------------------------*/
    /* NIQ_clsNewImageQuestion -- the NewImageFile question's context: where  */
    /* the dialog starts, and -- when it is asked again after Browse or       */
    /* Match -- the fields to put back, the picker's answer already in them   */
    /* -- documentation table.                                                */
    /*------------------------------------------------------------------------*/
    internal sealed class NIQ_clsNewImageQuestion
    {
        internal string NIQ_sStartFolder;         // the folder the dialog's path starts in (the shared image folder)
        internal bool NIQ_bTyped;                 // true to put the three fields below back (asked again); false for a first showing
        internal string NIQ_sPath;                // with NIQ_bTyped: the path box's text
        internal decimal NIQ_decSize;             // with NIQ_bTyped: the size box's number, in NIQ_iUnit
        internal int NIQ_iUnit;                   // with NIQ_bTyped: the unit box's index

        /*--------------------------------------------------------------------*/
        /* NIQ_clsNewImageQuestion:                                           */
        /*                                                                    */
        /* The question's context, kept as given.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sStartFolder : the dialog's start folder.                      */
        /*     bTyped       : true when the fields are to be put back.        */
        /*     sPath        : the path to put back ("" for none).             */
        /*     decSize      : the number to put back.                         */
        /*     iUnit        : the unit's index to put back.                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the record.                                    */
        /*--------------------------------------------------------------------*/
        internal NIQ_clsNewImageQuestion(string sStartFolder, bool bTyped, string sPath, decimal decSize, int iUnit)
        {
            /* Kept as given; the worker makes one per showing. */
            NIQ_sStartFolder = sStartFolder;
            NIQ_bTyped = bTyped;
            NIQ_sPath = sPath;
            NIQ_decSize = decSize;
            NIQ_iUnit = iUnit;
        }
    }

    /*------------------------------------------------------------------------*/
    /* NIC_clsNewImageChoice -- the NewImageFile question's answer, carried   */
    /* in ANS_objValue: the file and size accepted, or Browse or Match asked  */
    /* with what was typed, for the worker to ask the picker and show the     */
    /* dialog again -- documentation table.                                   */
    /*------------------------------------------------------------------------*/
    internal sealed class NIC_clsNewImageChoice
    {
        internal bool NIC_bBrowse;                // Browse was pressed: the file to be asked for
        internal bool NIC_bMatch;                 // Match a Device was pressed: the disk to be asked for
        internal string NIC_sPath;                // accepted: the image file's full path; asked: the path box's text
        internal ulong NIC_ullBytes;              // accepted: the size in bytes (0 when asked)
        internal bool NIC_bOverwrite;             // accepted: the file exists and the dialog's question was answered replace
        internal decimal NIC_decSize;             // asked: the size box's number
        internal int NIC_iUnit;                   // asked: the unit box's index

        /*--------------------------------------------------------------------*/
        /* NIC_clsNewImageChoice:                                             */
        /*                                                                    */
        /* The dialog's outcome, kept as given (read from the dialog before   */
        /* it was disposed).                                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bBrowse    : Browse was pressed.                               */
        /*     bMatch     : Match a Device was pressed.                       */
        /*     sPath      : the path accepted, or as typed.                   */
        /*     ullBytes   : the size accepted, or 0.                          */
        /*     bOverwrite : replace an existing file.                         */
        /*     decSize    : the size box's number as typed.                   */
        /*     iUnit      : the unit box's index as typed.                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the record.                                    */
        /*--------------------------------------------------------------------*/
        internal NIC_clsNewImageChoice(bool bBrowse, bool bMatch, string sPath, ulong ullBytes, bool bOverwrite,
            decimal decSize, int iUnit)
        {
            /* Kept as given. */
            NIC_bBrowse = bBrowse;
            NIC_bMatch = bMatch;
            NIC_sPath = sPath;
            NIC_ullBytes = ullBytes;
            NIC_bOverwrite = bOverwrite;
            NIC_decSize = decSize;
            NIC_iUnit = iUnit;
        }
    }

    /*------------------------------------------------------------------------*/
    /* EPI_clsEntryProperties -- the listing row an entry's Properties was    */
    /* asked for, as the click captured it; the entry's path and filesystem   */
    /* are the request's -- documentation table.                              */
    /*------------------------------------------------------------------------*/
    internal sealed class EPI_clsEntryProperties
    {
        internal EEN_stEntry EPI_stEntry; // the row's facts (size, inode, type, mode, owner, times) as copied; its name bytes the row's own array, not a copy

        /*--------------------------------------------------------------------*/
        /* EPI_clsEntryProperties:                                            */
        /*                                                                    */
        /* The row's facts, kept.                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stEntry : the row's entry value; no list item is held.         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the facts.                                     */
        /*--------------------------------------------------------------------*/
        internal EPI_clsEntryProperties(EEN_stEntry stEntry)
        {
            /* A structure, so the assignment copies its fields; its name is   */
            /* an array, so that alone is shared with the row -- nothing       */
            /* changes either afterwards.                                      */
            EPI_stEntry = stEntry;
        }
    }

    /*------------------------------------------------------------------------*/
    /* INA_clsInputArgs -- an OpenInput request's arguments: which input, its */
    /* window's id, and its starting values, copied when the request was      */
    /* made -- documentation table.                                           */
    /*------------------------------------------------------------------------*/
    internal sealed class INA_clsInputArgs
    {
        internal INK_enmInputKind INA_enmKind;                   // which input (a value, never code to run)
        internal long INA_lWindow;                               // the input window's id, made by the UI before posting (CLI_NewWindowId: negative), never its editor's
        internal string INA_sExpectedEntry;                      // a Save As: where the editor saves now; "" for the main window's inputs
        internal string INA_sInitialFolder;                      // the folder to start in; "" for the picker's own start
        internal string INA_sInitialName;                        // the name to offer; "" for none
        internal long INA_lExpectedOrder;                        // a Save As: the editor's acknowledged save order; the worker refuses the input if the editor has saved since
        internal EPI_clsEntryProperties INA_objEntryProperties;  // the row for EntryProperties (required there); null otherwise

        /*--------------------------------------------------------------------*/
        /* INA_clsInputArgs:                                                  */
        /*                                                                    */
        /* The arguments, checked and kept.  A missing text becomes "", so no */
        /* gap is ever filled later from what a window shows.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     enmKind            : the input.                                */
        /*     lWindow            : its window's id; must be negative.        */
        /*     sExpectedEntry     : a Save As's current destination           */
        /*                          (required for both Save As kinds).        */
        /*     sInitialFolder     : the starting folder, or null.             */
        /*     sInitialName       : the offered name, or null.                */
        /*     lExpectedOrder     : the acknowledged save order; optional, 0. */
        /*     objEntryProperties : the Properties row; optional, null.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the arguments; an ArgumentException for an     */
        /*                     unknown kind, an id not negative, or a part    */
        /*                     the kind requires missing.                     */
        /*--------------------------------------------------------------------*/
        internal INA_clsInputArgs(INK_enmInputKind enmKind, long lWindow, string sExpectedEntry,
            string sInitialFolder, string sInitialName, long lExpectedOrder = 0,
            EPI_clsEntryProperties objEntryProperties = null)
        {
            if (!Enum.IsDefined(typeof(INK_enmInputKind), enmKind) || lWindow >= 0 ||
                ((enmKind == INK_enmInputKind.ImageSaveTarget || enmKind == INK_enmInputKind.HostSaveTarget) &&
                String.IsNullOrEmpty(sExpectedEntry)))
            {   /* An unknown kind, an id that is not a UI window's (the        */
                /* worker's own ids are positive), or a Save As that does not   */
                /* say where its editor saves now: refused.                     */
                throw new ArgumentException("The input view needs a unique owner and complete binding.");
            }

            if (enmKind == INK_enmInputKind.EntryProperties && objEntryProperties == null)
            {   /* Properties with no row: refused -- a row selected later must   */
                /* never stand in for the one clicked.                            */
                throw new ArgumentException("Entry Properties input needs its captured listing facts.");
            }

            /* The texts left out become "": a Save As's destination was        */
            /* required above, so an empty one here is a main-window input's.   */
            if (sExpectedEntry == null)
            {   /* A main-window input: no editor destination. */
                sExpectedEntry = "";
            }

            if (sInitialFolder == null)
            {   /* No starting folder: the picker starts where it would. */
                sInitialFolder = "";
            }

            if (sInitialName == null)
            {   /* No name offered. */
                sInitialName = "";
            }

            /* Kept: values only -- no control, call or dialog. */
            INA_enmKind = enmKind;
            INA_lWindow = lWindow;
            INA_sExpectedEntry = sExpectedEntry;
            INA_sInitialFolder = sInitialFolder;
            INA_sInitialName = sInitialName;
            INA_lExpectedOrder = lExpectedOrder;
            INA_objEntryProperties = objEntryProperties;
        }

        /*--------------------------------------------------------------------*/
        /* INA_IsImage:                                                       */
        /*                                                                    */
        /* Whether the input is one of the two image browsers -- an editor's  */
        /* Save As to an entry, or the image pane's folder picker -- which    */
        /* post their own folder listings to the worker while they are open.  */
        /* The worker lets other work run beside these (Sys_HasValueInput),   */
        /* the state gate holds them to the open filesystem, and an image     */
        /* close ends them first (Sys_CloseInputSet).                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true for ImageSaveTarget and ImageFolder.               */
        /*--------------------------------------------------------------------*/
        internal bool INA_IsImage()
        {
            /* Only these two.  Properties reads the filesystem once, before   */
            /* its window opens (Sys_OpenInputSequence), and its change is a   */
            /* request of its own afterwards.                                  */
            return(INA_enmKind == INK_enmInputKind.ImageSaveTarget || INA_enmKind == INK_enmInputKind.ImageFolder);
        }
    }

    /*-------------------------------------------------------------------------*/
    /* ICV_clsInputView -- an accepted input, as the worker records it and     */
    /* hands it to the UI to show (an InputOpen view): the request, the        */
    /* arguments, and every fact the worker read for it first -- documentation */
    /* table.                                                                  */
    /*-------------------------------------------------------------------------*/
    internal sealed class ICV_clsInputView
    {
        internal RQT_clsRequest ICV_objRequest;              // the OpenInput request: its origin (the editor, or 0 for the main window), epoch and range
        internal INA_clsInputArgs ICV_objArgs;               // its arguments (kind, window id, starting values)
        internal EDR_clsEditor ICV_objEditor;                // a Save As: the worker's record of the editor when the input was accepted; null for the main window's inputs
        internal STV_clsStateView ICV_objSource;             // the worker's state when the input was accepted; a later state does not change it
        internal RSL_clsResult ICV_objPropertyFacts;         // Properties: the volume facts, or a link's target, read first; null when nothing was read

        /*--------------------------------------------------------------------*/
        /* ICV_clsInputView:                                                  */
        /*                                                                    */
        /* The record, kept as given (Sys_OpenInputSequence makes it).        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest       : the OpenInput request.                      */
        /*     objArgs          : its arguments.                              */
        /*     objEditor        : the editor's record, or null.               */
        /*     objSource        : the worker's state now.                     */
        /*     objPropertyFacts : the facts read; optional, null.             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the record; the caller registers it.           */
        /*--------------------------------------------------------------------*/
        internal ICV_clsInputView(RQT_clsRequest objRequest, INA_clsInputArgs objArgs, EDR_clsEditor objEditor, STV_clsStateView objSource,
            RSL_clsResult objPropertyFacts = null)
        {
            /* Kept by reference, not copied: nothing changes these once the   */
            /* worker has made them -- a window only reads them, and what it   */
            /* chooses is copied out into a request of its own.                */
            ICV_objRequest = objRequest;
            ICV_objArgs = objArgs;
            ICV_objEditor = objEditor;
            ICV_objSource = objSource;
            ICV_objPropertyFacts = objPropertyFacts;
        }
    }

    /* The worker's side of the inputs: part of the command system   */
    /* (CommandSystem.cs), run on the worker thread.                 */
    internal sealed partial class SYS_clsSystem
    {
        /*--------------------------------------------------------------------*/
        /* Sys_HasValueInput:                                                 */
        /*                                                                    */
        /* Whether an input other than the two image browsers is open.  While */
        /* one is, the worker runs only the facts that report a window's or a */
        /* drag's end and the editors' reports (Sys_Take), refuses fresh user */
        /* work (Sys_bUserInputHeld) and does not take an Exit                */
        /* (CommandInterrupts.cs): such an input is modal, and anything run   */
        /* beside it could change what it is choosing for.  Worker thread     */
        /* only: it reads the worker's own input list.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true while such an input is registered.                 */
        /*--------------------------------------------------------------------*/
        private bool Sys_HasValueInput()
        {
            int i;

            /* The image browsers do not count: they post their own folder   */
            /* listings, which must run while they are open.                 */
            for (i = 0; i < Sys_lstInputs.Count; i++)
            {   /* One registered input per iteration. */
                if (!Sys_lstInputs[i].ICV_objArgs.INA_IsImage())
                {   /* A value input: it holds the worker until its end is   */
                    /* reported (InputClosed), not when a close is asked.    */
                    return(true);
                }
            }

            /* No value input is open. */
            return(false);
        }

        /*--------------------------------------------------------------------*/
        /* Sys_OpenInputSequence:                                             */
        /*                                                                    */
        /* The OpenInput sequence: accept an input, get first what its window */
        /* needs (a Properties read; an image New File's write access -- the  */
        /* risk question asked before the name), record it, and hand it to    */
        /* the UI to show.  The command then ends at once, while the window   */
        /* is still open, so an image browser can post its folder listings;   */
        /* a value input holds other work by its record                       */
        /* (Sys_HasValueInput).  An Open of a Windows file that an editor     */
        /* already saves to opens no input: that editor is brought forward.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the OpenInput request.                          */
        /*     objExecution : the running command (its steps and checkpoint). */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : SYS_OK with the input's record -- or with no   */
        /*                     payload when an editor already open on the     */
        /*                     Windows file was brought forward instead;      */
        /*                     otherwise a refusal, a failure or a cancel,    */
        /*                     with nothing recorded and no window shown.     */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_OpenInputSequence(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            INA_clsInputArgs objArgs;
            EDR_clsEditor objEditor;
            ICV_clsInputView objInput;
            ICV_clsInputView objExisting;
            RSL_clsResult objResult;
            RSL_clsResult objPropertyFacts;
            int i;

            /* The arguments.  An image input has already passed the state   */
            /* gate's epoch and range checks (CommandSequences.cs); the      */
            /* others need no filesystem.                                    */
            objArgs = objRequest.RQT_objPayload as INA_clsInputArgs;
            objEditor = null;
            if (objArgs == null)
            {   /* No arguments: refused -- nothing is shown that the request   */
                /* does not describe.                                           */
                return(new RSL_clsResult(SYS_REFUSED, "No input-view metadata was supplied.", null));
            }

            if (objArgs.INA_enmKind == INK_enmInputKind.ImageSaveTarget || objArgs.INA_enmKind == INK_enmInputKind.HostSaveTarget)
            {   /* An editor's Save As: its editor must still be registered,   */
                /* saving where the request says, with the same acknowledged   */
                /* save order -- the main window's inputs have no editor.      */
                objEditor = Sys_FindEditor(objRequest.RQT_lOrigin);
                if (objEditor == null || objEditor.EDR_sEntry != objArgs.INA_sExpectedEntry ||
                    objEditor.EDR_lSaveOrder != objArgs.INA_lExpectedOrder)
                {   /* The editor closed or saved since the request was made: a   */
                    /* picker for where it used to save is refused.               */
                    return(new RSL_clsResult(SYS_REFUSED, "The Save As editor binding has changed.", null));
                }
            }

            if (objArgs.INA_enmKind == INK_enmInputKind.HostOpenEntry && objRequest.RQT_lstPaths.Count == 1)
            {   /* Opening a Windows file: when an editor already saves to   */
                /* that file -- opened on it, or Saved As to it -- that      */
                /* editor is brought forward instead (Sys_FindHostEditor,    */
                /* Sys_ShowOpenEditor, CommandOperations.cs), whatever       */
                /* viewer this Open asked for: two editors on one file       */
                /* would each save over the other's work.  Tested before     */
                /* the duplicate-input loop below and before any input is    */
                /* recorded, so no input is registered or shown for it and   */
                /* nothing waits on its end.                                 */
                objEditor = Sys_FindHostEditor(objRequest.RQT_lstPaths[0]);
                if (objEditor != null)
                {   /* Already open: its window brought forward, the Open   */
                    /* answered by it.                                      */
                    return(Sys_ShowOpenEditor(objRequest, objEditor));
                }
            }

            /* An input already open for the same window id, or of the same     */
            /* kind from the same origin, refuses this one -- before anything   */
            /* is read, so a second click repeats no read (Properties' facts,   */
            /* below).  A new id alone does not get round it.                   */
            for (i = 0; i < Sys_lstInputs.Count; i++)
            {   /* One registered input per iteration (the worker's own list:   */
                /* no window is looked at).                                     */
                objExisting = Sys_lstInputs[i];
                if (objExisting.ICV_objArgs.INA_lWindow == objArgs.INA_lWindow ||
                    (objExisting.ICV_objRequest.RQT_lOrigin == objRequest.RQT_lOrigin && objExisting.ICV_objArgs.INA_enmKind == objArgs.INA_enmKind))
                {   /* Already open: refused, with the open one in the result. */
                    return(new RSL_clsResult(SYS_REFUSED, "That input window is already open.", objExisting));
                }
            }

            /* New File in the image asks for write access BEFORE its name:     */
            /* on a filesystem still read-only, the risk question (when the     */
            /* warnings are on) and the writable reopen come first, so a No     */
            /* asks no name and makes nothing, and the name is typed into a     */
            /* filesystem that can take it.  The gate has held this request     */
            /* to the open filesystem, same epoch and range, from the main      */
            /* window, so the question shows over the main window and the       */
            /* reopen keeps the epoch, the editors and the pane's folder.  No   */
            /* input of this request is registered yet, so the close inside     */
            /* the reopen has none of its own to wait for.  The create that     */
            /* follows the name runs Sys_FirstChange again, which then finds    */
            /* the session writable and returns at once.  A Cancel at the       */
            /* name leaves the filesystem writable, as Enable Writing would.    */
            if (objArgs.INA_enmKind == INK_enmInputKind.ImageNewFile)
            {   /* An image New File: write access first. */
                objResult = Sys_FirstChange(objRequest, objExecution);
                if (objResult.RSL_iStatus != SYS_OK)
                {   /* Declined, or the reopen failed (a failed writable         */
                    /* reopen may have fallen back to read-only): that outcome   */
                    /* is the result, and no name is asked.                      */
                    return(objResult);
                }
            }

            /* Properties: what the window shows is read now, before it opens   */
            /* -- the volume's counts, or a link's target -- and a failed       */
            /* read opens no window.                                            */
            objPropertyFacts = null;
            if (objArgs.INA_enmKind == INK_enmInputKind.FilesystemProperties)
            {   /* Filesystem Properties: the counts read for this request, not   */
                /* those of an earlier open.                                      */
                objPropertyFacts = Sys_Do(STP_enmStep.VolumeInfo, objRequest, objExecution);
                if (objPropertyFacts.RSL_iStatus != SYS_OK)
                {   /* The read failed: its failure is the result. */
                    return(objPropertyFacts);
                }

                if (!(objPropertyFacts.RSL_objPayload is EVR_clsVolumeResult))
                {   /* Success with no facts: a failure, not a window of zeros. */
                    return(new RSL_clsResult(SYS_INTERNAL, "Filesystem Properties returned no usable volume facts.", null));
                }
            }
            else if (objArgs.INA_enmKind == INK_enmInputKind.EntryProperties &&
                objArgs.INA_objEntryProperties.EPI_stEntry.EEN_uiType == ENG_clsNative.ENG_TYPE_SYMLINK)
            {   /* A symbolic link's Properties: its target read, from the one   */
                /* path the gate required.                                       */
                objPropertyFacts = Sys_Do(STP_enmStep.ReadLink, objRequest, objExecution);
                if (objPropertyFacts.RSL_iStatus != SYS_OK)
                {   /* The read failed: its failure is the result. */
                    return(objPropertyFacts);
                }

                if (!(objPropertyFacts.RSL_objPayload is ETR_clsTextResult))
                {   /* Success with no text: a failure, not an empty target. */
                    return(new RSL_clsResult(SYS_INTERNAL, "Entry Properties returned no usable link target.", null));
                }
            }

            if (objPropertyFacts != null && !objExecution.EXC_Checkpoint())
            {   /* A read was made and could not be stopped part-way: a Cancel   */
                /* or an Exit asked meanwhile is honoured now, before the        */
                /* window opens.                                                 */
                return(new RSL_clsResult(SYS_CANCELLED, "Properties selection cancelled.", null));
            }

            /* The record made and registered BEFORE it is handed to the UI:    */
            /* an Exit may be posted the moment the window starts being made,   */
            /* and must find the input.  Its state view carries the clipboard   */
            /* lifetime (the close count), which the image pane's copy keys     */
            /* its clipboard data with -- a reopen for access keeps the epoch   */
            /* but not the clipboard's validity.                                */
            objInput = new ICV_clsInputView(objRequest, objArgs, objEditor, new STV_clsStateView(Sys_enmMode,
                Sys_objTarget, Sys_lEpoch, Sys_bWritable, false, false, Sys_lClipboardLifetime),
                objPropertyFacts);
            Sys_lstInputs.Add(objInput);
            if (!SYS_Show(new VWM_clsView(VWK_enmViewKind.InputOpen, objRequest, objInput)))
            {   /* The UI can no longer be reached (its window has gone): the   */
                /* record taken out again, as no window will report its end.    */
                Sys_lstInputs.Remove(objInput);
                return(new RSL_clsResult(SYS_CANCELLED, "The input owner has closed.", null));
            }

            /* Shown: the command ends now, the input staying open, so an     */
            /* image browser's folder listings can run as requests of their   */
            /* own.                                                           */
            return(new RSL_clsResult(SYS_OK, "", objInput));
        }

        /*--------------------------------------------------------------------*/
        /* Sys_RemoveInput:                                                   */
        /*                                                                    */
        /* An input's InputClosed report, on the worker: its record removed.  */
        /* A report for an input already removed (by Sys_CloseInputSet, or a  */
        /* second report) finds nothing and does nothing.                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lWindow : the input window's id.                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : removed, or nothing to remove.                          */
        /*--------------------------------------------------------------------*/
        private void Sys_RemoveInput(long lWindow)
        {
            int i;

            /* From the end, so a removal does not move a record not yet seen. */
            for (i = Sys_lstInputs.Count - 1; i >= 0; i--)
            {   /* One registered input per iteration. */
                if (Sys_lstInputs[i].ICV_objArgs.INA_lWindow == lWindow)
                {   /* This input: removed.  It holds no file, stage or handle,   */
                    /* so nothing else is released.                               */
                    Sys_lstInputs.RemoveAt(i);
                    return;
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* Sys_CloseInputSet:                                                 */
        /*                                                                    */
        /* Close the inputs a command must have closed before it goes on: an  */
        /* editor's (before its close question -- an input disables its       */
        /* editor), the image browsers (before the image is changed), or      */
        /* every one (an Exit).  Each is asked to close (InputClose) and the  */
        /* worker waits for the window to report that it has really           */
        /* returned; a window that will not close stops the command.          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest  : the command closing them.                        */
        /*     lEditor     : only this editor's inputs; 0 for any origin.     */
        /*     bImagesOnly : only the image browsers (INA_IsImage).           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : SYS_OK once every chosen input has closed;     */
        /*                     SYS_CANCELLED, with the window's reason, at    */
        /*                     the first that did not.                        */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_CloseInputSet(RQT_clsRequest objRequest, long lEditor, bool bImagesOnly)
        {
            ICV_clsInputView objInput;
            ANS_clsAnswer objAnswer;
            string sDetail;
            int i;

            /* Newest first: a modal input disables the windows under it, so    */
            /* a newer one must close before an older one, or the editor, can   */
            /* be closed or asked anything.                                     */
            for (i = Sys_lstInputs.Count - 1; i >= 0; i--)
            {   /* One registered input per iteration, newest first. */
                objInput = Sys_lstInputs[i];
                if ((lEditor != 0 && objInput.ICV_objRequest.RQT_lOrigin != lEditor) ||
                    (bImagesOnly && !objInput.ICV_objArgs.INA_IsImage()))
                {   /* Not one of those asked for -- another editor's, or a     */
                    /* Windows-file input when only the image browsers are to   */
                    /* close: left open.                                        */
                    continue;
                }

                /* Asked, and waited for: the answer is Closed only once the   */
                /* window has really returned (IWR_InputReturned), never       */
                /* because a close was asked; a close the window refuses is    */
                /* answered Cancel (IWR_AskInputClose).                        */
                objAnswer = SYS_Ask(QNK_enmQuestionKind.InputClose, objRequest,
                    objInput.ICV_objArgs.INA_lWindow, objInput);
                if (objAnswer == null || objAnswer.ANS_enmKind != ANK_enmAnswerKind.Closed)
                {   /* Not closed: the command stops here, so no editor is        */
                    /* asked about its content under a window that disables it.   */
                    sDetail = "An input window could not be closed.";
                    if (objAnswer != null && objAnswer.ANS_sText.Length != 0)
                    {   /* The window's own reason, when it gave one. */
                        sDetail = objAnswer.ANS_sText;
                    }

                    /* Cancelled, with the reason. */
                    return(new RSL_clsResult(SYS_CANCELLED, sDetail, null));
                }

                /* Closed: the record removed (its InputClosed, when it          */
                /* arrives, finds nothing).  This says only that the window is   */
                /* gone -- it consents to nothing about any editor's content.    */
                Sys_lstInputs.RemoveAt(i);
            }

            /* Every chosen input has closed. */
            return(new RSL_clsResult(SYS_OK, "", null));
        }
    }

    /* What a Windows file dialog is asked for, which sets its kind, filter   */
    /* and default extension (WFD_SelectFile) -- documentation table.         */
    internal enum WFP_enmPurpose
    {
        EditorSave,  // an editor's Save As to a Windows file: a save dialog, all files, no extension added
        OpenImage,   // Open Image / Check Filesystem / Partition: an existing image file (an open dialog)
        WriteImage,  // Write Device and the partition window's Write: an existing image, plain or compressed (an open dialog)
        ReadRaw,     // Read Device and the partition window's Read: a new raw image file, img added when no extension is typed
        ReadXz       // Read Device with compression: a new compressed image file, xz added when no extension is typed
    }

    /*------------------------------------------------------------------------*/
    /* WFD_clsWindowsDialogs -- the Windows file dialogs the inputs use.      */
    /*------------------------------------------------------------------------*/
    internal static class WFD_clsWindowsDialogs
    {
        /*--------------------------------------------------------------------*/
        /* WFD_SelectFile:                                                    */
        /*                                                                    */
        /* Ask for a Windows file with Windows' own open or save dialog, as   */
        /* the purpose sets.  Its filters only help the user choose: the      */
        /* worker inspects the file chosen and checks where it writes.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmOwner   : the window the dialog is shown over.              */
        /*     enmPurpose : what is asked for.                                */
        /*     sTitle     : the dialog's caption.                             */
        /*     sFolder    : the folder to start in, or "" / null.             */
        /*     sName      : the name to offer, or null; characters Windows    */
        /*                  does not allow in a name are made "_".            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the file chosen; null for a Cancel or an owner        */
        /*              already gone; an ArgumentException for an unknown     */
        /*              purpose.                                              */
        /*--------------------------------------------------------------------*/
        internal static string WFD_SelectFile(Form frmOwner, WFP_enmPurpose enmPurpose,
            string sTitle, string sFolder, string sName)
        {
            OpenFileDialog dlgOpen;
            SaveFileDialog dlgSave;
            string sFilter;
            string sExtension;
            string sSelected;
            char[] arrInvalid;
            bool bOpen;
            int i;

            if (frmOwner == null || frmOwner.IsDisposed)
            {   /* No owner, or one disposed: nothing to show the dialog over. */
                return(null);
            }

            /* The dialog's settings by purpose: a save dialog, all files and   */
            /* no extension added unless a case below says otherwise.           */
            bOpen = false;
            sFilter = "All files (*.*)|*.*";
            sExtension = "";
            switch (enmPurpose)
            {   /* One purpose's settings per case. */
                case WFP_enmPurpose.EditorSave:
                    /* An editor's file keeps the name and extension typed. */
                    break;
                case WFP_enmPurpose.OpenImage:
                    /* An existing image: the usual raw image extensions,   */
                    /* then all files.                                      */
                    bOpen = true;
                    sFilter = "Image files (*.img;*.bin;*.raw;*.dd;*.ext2;*.ext3;*.ext4)|*.img;*.bin;*.raw;*.dd;*.ext2;*.ext3;*.ext4|All files (*.*)|*.*";
                    break;
                case WFP_enmPurpose.WriteImage:
                    /* An image to write, plain or compressed.  The filter        */
                    /* only helps: what the file holds is found by the worker's   */
                    /* inspection, not from its extension.                        */
                    bOpen = true;
                    sFilter = "Image files, plain or compressed (*.img;*.xz;*.gz;*.zst;*.zip;*.bin;*.raw;*.dd)|*.img;*.xz;*.gz;*.zst;*.zip;*.bin;*.raw;*.dd|All files (*.*)|*.*";
                    break;
                case WFP_enmPurpose.ReadRaw:
                    /* A new raw image: img is added when only a name is typed. */
                    sFilter = "Image files (*.img)|*.img|All files (*.*)|*.*";
                    sExtension = "img";
                    break;
                case WFP_enmPurpose.ReadXz:
                    /* A new compressed image: offered as .img.xz or .xz, and xz   */
                    /* added when only a name is typed.                            */
                    sFilter = "Compressed image files (*.img.xz)|*.img.xz|xz files (*.xz)|*.xz|All files (*.*)|*.*";
                    sExtension = "xz";
                    break;
                default:
                    /* An unknown purpose: refused, never taken as some other   */
                    /* kind of dialog.                                          */
                    throw new ArgumentException("Unknown Windows filename purpose.", "enmPurpose");
            }

            /* The offered name: an image entry's name can hold characters    */
            /* Windows does not allow in a file name, so those are made "_"   */
            /* here (only the offer changes; no file is renamed).             */
            if (sName == null)
            {   /* No name given: none offered. */
                sName = "";
            }

            /* The characters Windows' own list says a name may not hold. */
            arrInvalid = Path.GetInvalidFileNameChars();
            for (i = 0; i < arrInvalid.Length; i++)
            {   /* One forbidden character replaced per iteration. */
                sName = sName.Replace(arrInvalid[i], '_');
            }

            /* The dialog, shown and read here; the caller ends its input       */
            /* only after this returns, never from the dialog's result alone.   */
            sSelected = null;
            if (bOpen)
            {   /* An open dialog: Windows checks that the chosen file exists   */
                /* (CheckFileExists, on by default -- documented).              */
                dlgOpen = new OpenFileDialog();
                using (dlgOpen)
                {   /* Disposed once the modal call has returned or thrown.   */
                    /* Its settings, from the captured values only.           */
                    dlgOpen.Title = sTitle;
                    dlgOpen.Filter = sFilter;
                    dlgOpen.FileName = sName;
                    if (!String.IsNullOrEmpty(sFolder))
                    {   /* A starting folder given: the dialog starts there. */
                        dlgOpen.InitialDirectory = sFolder;
                    }

                    /* Shown modal, with its own Cancel. */
                    if (dlgOpen.ShowDialog(frmOwner) == DialogResult.OK)
                    {   /* Chosen: the path copied before the dialog goes. */
                        sSelected = dlgOpen.FileName;
                    }
                }
            }
            else
            {   /* A save dialog: Windows asks before an existing file is     */
                /* chosen (OverwritePrompt), and adds the default extension   */
                /* when none is typed (AddExtension) -- both on by default,   */
                /* documented.                                                */
                dlgSave = new SaveFileDialog();
                using (dlgSave)
                {   /* Disposed once the modal call has returned or thrown.     */
                    /* The extension only names the file: whether the read is   */
                    /* compressed is the input's own choice.                    */
                    dlgSave.Title = sTitle;
                    dlgSave.Filter = sFilter;
                    dlgSave.FileName = sName;
                    dlgSave.DefaultExt = sExtension;
                    if (!String.IsNullOrEmpty(sFolder))
                    {   /* A starting folder given: the dialog starts there --   */
                        /* only a place to look, permission for nothing.         */
                        dlgSave.InitialDirectory = sFolder;
                    }

                    /* Shown modal; a Cancel gives no path, so nothing changes. */
                    if (dlgSave.ShowDialog(frmOwner) == DialogResult.OK)
                    {   /* Chosen: the path as the dialog returns it, its   */
                        /* extension added and any overwrite agreed.        */
                        sSelected = dlgSave.FileName;
                    }
                }
            }

            /* The chosen file, or null when nothing was chosen. */
            return(sSelected);
        }
    }

    /*------------------------------------------------------------------------*/
    /* IWN_clsInputWindow -- the UI's record of one open input: what it is,   */
    /* its picker window when it has one, and the state of a close the        */
    /* worker asked for.  UI thread only -- documentation table.              */
    /*------------------------------------------------------------------------*/
    internal sealed class IWN_clsInputWindow
    {
        internal ICV_clsInputView IWN_objInput;       // the worker's record of the input (its window id inside)
        internal Form IWN_frmInput;                   // an image browser's picker window; null for a value input (a modal dialog, nothing to close from outside)
        internal bool IWN_bCancelRequested;           // the worker asked it to close: a value it then returns must be ignored (IWR_InputCancelled)
        internal FormClosingEventArgs IWN_evClosing;  // the picker's last FormClosing arguments, read once every handler has run, for a refused close
        internal PRS_clsPresentation IWN_objClose;    // the worker's close question waiting on this input, answered when it really returns

        /*--------------------------------------------------------------------*/
        /* IWN_clsInputWindow:                                                */
        /*                                                                    */
        /* The record, kept as given.                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objInput : the input.                                          */
        /*     frmInput : its picker, or null.                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the record; it holds no window of its own.     */
        /*--------------------------------------------------------------------*/
        internal IWN_clsInputWindow(ICV_clsInputView objInput, Form frmInput)
        {
            /* Kept.  The input ends only when its renderer reports its       */
            /* return (IWR_InputReturned), never because a close was asked.   */
            IWN_objInput = objInput;
            IWN_frmInput = frmInput;
        }
    }

    /*------------------------------------------------------------------------*/
    /* IWR_clsInputWindows -- the UI's input registry: one record per open    */
    /* input, made when its renderer starts it and removed when it really     */
    /* returns.  The windows stay on this side; only the ids go to the        */
    /* worker -- documentation table.                                         */
    /*------------------------------------------------------------------------*/
    internal sealed class IWR_clsInputWindows
    {
        private readonly CLI_clsClient Iwr_objClient;              // the client: InputClosed reports are posted through it
        private readonly List<IWN_clsInputWindow> Iwr_lstWindows;  // the open inputs' records, oldest first
        private readonly LFT_clsLifetimes Iwr_objLifetimes;        // the UI-thread window watch: ends an input whose picker is gone after its renderer unwound

        /*------------------------------------------------------------------*/
        /* IWR_clsInputWindows:                                             */
        /*                                                                  */
        /* Bind actual input-return tracking to one UI endpoint and to the  */
        /* window watch.  The registry installs both of its client hooks    */
        /* here -- the cleanup after a renderer throws, and the settlement  */
        /* after a renderer returns -- so between them every input's end    */
        /* reaches the worker however its renderer leaves.                  */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objClient    : command client whose worker registered every  */
        /*                    input before publication.                     */
        /*     objLifetimes : the main window's window watch.               */
        /*                                                                  */
        /* Returns:                                                         */
        /*     IWR_clsInputWindows : empty; creates no window, timer or     */
        /*                           thread (the watch owns its timer).     */
        /*------------------------------------------------------------------*/
        internal IWR_clsInputWindows(CLI_clsClient objClient, LFT_clsLifetimes objLifetimes)
        {
            if (objClient == null)
            {   /* No client: refused -- no input's end could be reported. */
                throw new ArgumentNullException("objClient");
            }

            if (objLifetimes == null)
            {   /* An input whose renderer unwinds without ending it can only   */
                /* be ended later by the window watch, so one is required.      */
                throw new ArgumentNullException("objLifetimes");
            }

            /* The bindings and the empty list, made BEFORE the hooks below    */
            /* are given to the client, so a hook called at once finds them.   */
            Iwr_objClient = objClient;
            Iwr_lstWindows = new List<IWN_clsInputWindow>();
            Iwr_objLifetimes = objLifetimes;

            /* The clean-up after a renderer THROWS, given to the client once   */
            /* (CLI_RegisterInputFailure refuses a second registry).            */
            if (!objClient.CLI_RegisterInputFailure(new CLI_InputFailure(Iwr_FailInput)))
            {   /* Another registry already holds this client's clean-up. */
                throw new ArgumentException("Input tracking needs one unused command client.", "objClient");
            }

            /* The settlement after a NORMAL return, bound the same way. */
            if (!objClient.CLI_RegisterInputRendered(new CLI_InputRendered(Iwr_InputRendered)))
            {   /* Another registry already settles this client's inputs. */
                throw new ArgumentException("Input tracking needs one unused command client.", "objClient");
            }
        }

        /*--------------------------------------------------------------------*/
        /* Iwr_FindInputWindow:                                               */
        /*                                                                    */
        /* An open input's record, by its window id.                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lWindow : the input window's id (a number, not a window        */
        /*               handle).                                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     IWN_clsInputWindow : the record, or null when the input has    */
        /*                          returned or was never registered.         */
        /*--------------------------------------------------------------------*/
        private IWN_clsInputWindow Iwr_FindInputWindow(long lWindow)
        {
            int i;

            /* A walk of the list; UI thread only, so nothing changes it   */
            /* meanwhile.                                                  */
            for (i = 0; i < Iwr_lstWindows.Count; i++)
            {   /* One record per iteration. */
                if (Iwr_lstWindows[i].IWN_objInput.ICV_objArgs.INA_lWindow == lWindow)
                {   /* This input's record. */
                    return(Iwr_lstWindows[i]);
                }
            }

            /* No open input has that id. */
            return(null);
        }

        /*--------------------------------------------------------------------*/
        /* IWR_FindInputOwner:                                                */
        /*                                                                    */
        /* The window a worker question naming an input is shown over         */
        /* (CommandQuestionPresenter.cs): an image browser's picker, while it */
        /* is open -- a Save As's name question goes over its picker, not     */
        /* the editor the picker disables.                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lWindow : the id the question names.                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Form : the picker, or null (no such input, a value input, or a */
        /*            picker already disposed).                               */
        /*--------------------------------------------------------------------*/
        internal Form IWR_FindInputOwner(long lWindow)
        {
            IWN_clsInputWindow objWindow;

            /* The record.  A value input has no window of its own here, so   */
            /* a question naming one finds no owner in this registry.         */
            objWindow = Iwr_FindInputWindow(lWindow);
            if (objWindow == null || objWindow.IWN_frmInput == null || objWindow.IWN_frmInput.IsDisposed)
            {   /* No live picker for that id: null, for the presenter's next   */
                /* lookup.                                                      */
                return(null);
            }

            /* The picker; its renderer still owns it. */
            return(objWindow.IWN_frmInput);
        }

        /*--------------------------------------------------------------------*/
        /* IWR_ExitOwner:                                                     */
        /*                                                                    */
        /* The window an Exit question is shown over when an input is open    */
        /* (CommandQuestionPresenter.cs): the newest live picker, since it    */
        /* disables the windows under it, the main window included.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Form : that picker, or null (none open: the main window owns   */
        /*            the question).                                          */
        /*--------------------------------------------------------------------*/
        internal Form IWR_ExitOwner()
        {
            Form frmInput;
            int i;

            /* Only the pickers this registry knows; no window of Windows'   */
            /* own is looked for.                                            */
            for (i = Iwr_lstWindows.Count - 1; i >= 0; i--)
            {   /* One record per iteration, newest first. */
                frmInput = Iwr_lstWindows[i].IWN_frmInput;
                if (frmInput != null && !frmInput.IsDisposed)
                {   /* A live picker: the newest, so it is on top. */
                    return(frmInput);
                }
            }

            /* No picker: a value input has none, and the worker takes no   */
            /* Exit while one is open (CommandInterrupts.cs).               */
            return(null);
        }

        /*--------------------------------------------------------------------*/
        /* IWR_RegisterInput:                                                 */
        /*                                                                    */
        /* Record an input as its renderer starts it, before its window is    */
        /* shown.  The renderer must end it with IWR_InputReturned once the   */
        /* window has returned and been disposed (in a finally).              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objInput : the worker's record of the input.                   */
        /*     frmInput : an image browser's picker (required for one); null  */
        /*                for a value input (required for one).               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : recorded; an ArgumentException for no input, an id      */
        /*            already open, or a picker given to the wrong kind.      */
        /*--------------------------------------------------------------------*/
        internal void IWR_RegisterInput(ICV_clsInputView objInput, Form frmInput)
        {
            if (objInput == null || Iwr_FindInputWindow(objInput.ICV_objArgs.INA_lWindow) != null)
            {   /* No input, or one with that id already open: refused -- one   */
                /* input, one record.                                           */
                throw new ArgumentException("Input tracking needs a unique accepted identity.");
            }

            if (objInput.ICV_objArgs.INA_IsImage() && (frmInput == null || frmInput.IsDisposed))
            {   /* An image browser with no live picker: refused -- the worker   */
                /* closes it through that window (IWR_AskInputClose).            */
                throw new ArgumentException("An image browser input needs its actual managed picker.");
            }

            if (!objInput.ICV_objArgs.INA_IsImage() && frmInput != null)
            {   /* A value input given a window: refused -- it is a modal    */
                /* dialog that ends on its own, never closed from outside.   */
                throw new ArgumentException("Value-only input uses a record without a hidden window.");
            }

            /* Recorded.  A picker's FormClosed is not taken as its end: the   */
            /* renderer reports that, after its ShowDialog and clean-up have   */
            /* returned.                                                       */
            Iwr_lstWindows.Add(new IWN_clsInputWindow(objInput, frmInput));
            if (frmInput != null)
            {   /* A picker: its FormClosing hooked so a close asked from        */
                /* outside can see whether it was refused -- the arguments are   */
                /* kept, and their Cancel read after every handler (the          */
                /* picker's own among them) has run (Iwr_OnRecordClosing).       */
                frmInput.FormClosing += Iwr_OnRecordClosing;

                /* Watched, UNARMED: the renderer is on the stack and reports   */
                /* the end itself; the watch is armed only if it unwinds        */
                /* without doing so (Iwr_FailInput, Iwr_InputRendered).         */
                Iwr_objLifetimes.LFT_Watch(frmInput, Iwr_OnInputLost, false);
            }
        }

        /*------------------------------------------------------------------*/
        /* Iwr_OnInputLost:                                                 */
        /*                                                                  */
        /* The window watch found an input's picker gone after its renderer */
        /* had unwound without ending the input: end it now, exactly as the */
        /* renderer's own return would have -- the record removed, the      */
        /* worker told, any waiting close request answered.                 */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     frmLost : the gone picker.                                   */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the input has ended (nothing, if it already had).     */
        /*------------------------------------------------------------------*/
        private void Iwr_OnInputLost(Form frmLost)
        {
            int i;

            /* The input whose picker it was, found by that window. */
            for (i = 0; i < Iwr_lstWindows.Count; i++)
            {   /* One record per iteration, matched by its picker. */
                if (Iwr_lstWindows[i].IWN_frmInput == frmLost)
                {   /* This input: end it as its renderer would have. */
                    IWR_InputReturned(Iwr_lstWindows[i].IWN_objInput.ICV_objArgs.INA_lWindow);
                    return;
                }
            }
        }

        /*------------------------------------------------------------------*/
        /* Iwr_InputRendered:                                               */
        /*                                                                  */
        /* An input's renderer has returned normally (the client calls this */
        /* after every InputOpen view), and every input is modal inside its */
        /* renderer -- so the input has ended.  This makes sure the worker  */
        /* knows it, whether or not the renderer said so, so a renderer     */
        /* that forgets can never hold every request (Exit included).       */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objInput : the input whose renderer returned.                */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the worker has been told, or will be when the picker  */
        /*            goes.                                                 */
        /*------------------------------------------------------------------*/
        private void Iwr_InputRendered(ICV_clsInputView objInput)
        {
            IWN_clsInputWindow objWindow;
            long lWindow;

            /* The input's window id, and its record if the renderer has not   */
            /* ended it -- normally the renderer has, and there is none.       */
            lWindow = objInput.ICV_objArgs.INA_lWindow;
            objWindow = Iwr_FindInputWindow(lWindow);

            if (objWindow == null)
            {   /* Ended (or never registered): tell the worker again.  A        */
                /* second InputClosed is harmless -- the worker ignores one      */
                /* for an input it no longer holds -- and the same post covers   */
                /* an input no renderer claimed at all.                          */
                Iwr_objClient.CLI_Post(CFC_clsCommands.CFC_Create(KND_enmKind.InputClosed,
                    lWindow, null, null, null, 0, 0, null, "", ""), null);
                return;
            }

            /* The renderer returned without ending its input: a renderer bug,   */
            /* logged so it is seen, and repaired here.                          */
            if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
            {   /* Only while the debug log is kept: otherwise the line is never built. */
                DBGLOG_clsDebugLog.DBGLOG_Write("input", "a renderer returned without ending input " + lWindow);
            }

            if (objWindow.IWN_frmInput == null || objWindow.IWN_frmInput.IsDisposed)
            {   /* No picker left: the input has ended. */
                IWR_InputReturned(lWindow);
                return;
            }

            /* A picker still open after its renderer: it ends when that window does. */
            Iwr_objLifetimes.LFT_Arm(objWindow.IWN_frmInput);
        }

        /*--------------------------------------------------------------------*/
        /* Iwr_OnRecordClosing:                                               */
        /*                                                                    */
        /* A picker's FormClosing: its arguments kept in the input's record.  */
        /* WinForms passes the same arguments object to every FormClosing     */
        /* handler, so a caller that reads it after Close() returns sees the  */
        /* final Cancel -- a refusal set by a handler after this one too.     */
        /* It decides and reports nothing.                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the picker.                                        */
        /*     evClosing : its FormClosing arguments.                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : kept, or nothing for a picker not registered.           */
        /*--------------------------------------------------------------------*/
        private void Iwr_OnRecordClosing(object objSender, FormClosingEventArgs evClosing)
        {
            int i;

            /* The record, found by its picker. */
            for (i = 0; i < Iwr_lstWindows.Count; i++)
            {   /* One record per iteration. */
                if (Iwr_lstWindows[i].IWN_frmInput == objSender)
                {   /* This picker's: the arguments kept.  A close that goes   */
                    /* through still ends the input only when it returns.      */
                    Iwr_lstWindows[i].IWN_evClosing = evClosing;
                    return;
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* IWR_InputCancelled:                                                */
        /*                                                                    */
        /* Whether the worker asked this input to close: its renderer then    */
        /* ignores what the window returns (MainCommand.cs, MainForm.cs) --   */
        /* the command that wanted it has moved on.  Only an image browser    */
        /* can be asked (IWR_AskInputClose), so a value input always answers  */
        /* false.                                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lWindow : the input window's id, still registered.             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when a close was asked and not refused.            */
        /*--------------------------------------------------------------------*/
        internal bool IWR_InputCancelled(long lWindow)
        {
            IWN_clsInputWindow objWindow;

            /* The record (a flag of the UI's, permission for nothing). */
            objWindow = Iwr_FindInputWindow(lWindow);

            /* Asked to close; false when there is no record. */
            return(objWindow != null && objWindow.IWN_bCancelRequested);
        }

        /*--------------------------------------------------------------------*/
        /* IWR_InputReturned:                                                 */
        /*                                                                    */
        /* An input's end, reported by its renderer once its window has       */
        /* returned and been disposed (or by the clean-ups below): the record */
        /* removed, the worker told (InputClosed), and a close the worker is  */
        /* waiting on answered Closed.  A second report does nothing.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lWindow : the input window's id.                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : ended, or nothing for an input not registered.          */
        /*--------------------------------------------------------------------*/
        internal void IWR_InputReturned(long lWindow)
        {
            IWN_clsInputWindow objWindow;
            PRS_clsPresentation objClose;

            /* The record.  It is removed before anything is posted, so a    */
            /* second report -- a finally and a clean-up both reporting --   */
            /* finds nothing.                                                */
            objWindow = Iwr_FindInputWindow(lWindow);
            if (objWindow == null)
            {   /* Never registered, or ended already: nothing to do. */
                return;
            }

            /* Removed.  No window is closed or disposed here: the caller has   */
            /* already done that.                                               */
            Iwr_lstWindows.Remove(objWindow);
            if (objWindow.IWN_frmInput != null)
            {   /* The renderer has unwound, so the FormClosing hook is taken   */
                /* off -- and the window watch settled, since the end is        */
                /* reported here.                                               */
                objWindow.IWN_frmInput.FormClosing -= Iwr_OnRecordClosing;
                Iwr_objLifetimes.LFT_Settle(objWindow.IWN_frmInput);
            }

            /* The worker told, by id only; an InputClosed is admitted even   */
            /* during an Exit (a notification).                               */
            Iwr_objClient.CLI_Post(CFC_clsCommands.CFC_Create(KND_enmKind.InputClosed,
                lWindow, null, null, null, 0, 0, null, "", ""), null);

            /* A close the worker is waiting on (Sys_CloseInputSet) answered   */
            /* now -- the window really has returned.                          */
            objClose = objWindow.IWN_objClose;
            if (objClose != null)
            {   /* Waiting: answered Closed (QST_Reply refuses a display that   */
                /* has been replaced since).                                    */
                objClose.PRS_objQuestion.QST_Reply(objClose, new ANS_clsAnswer(ANK_enmAnswerKind.Closed, null));
            }
        }

        /*--------------------------------------------------------------------*/
        /* Iwr_FailInput:                                                     */
        /*                                                                    */
        /* The clean-up after an input's renderer THREW (the client calls     */
        /* it, CLI_FailInput, once the renderer's stack has unwound): a       */
        /* picker still standing is closed and disposed and the input ended.  */
        /* A picker that refuses to close is left open for the user, with     */
        /* the window watch armed to end the input when it goes.  The         */
        /* caller posts InputClosed itself when this answers true.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objInput : the input whose renderer threw.                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when no window of it remains -- including when the */
        /*            clean-up threw after the record was removed, so the     */
        /*            caller's own InputClosed reaches the worker; false when */
        /*            a picker is still open (its end reported later, by the  */
        /*            watch).                                                 */
        /*--------------------------------------------------------------------*/
        private bool Iwr_FailInput(ICV_clsInputView objInput)
        {
            IWN_clsInputWindow objWindow;

            /* The record.  A Windows file dialog the renderer made is its   */
            /* own using block's, already disposed by the unwinding.         */
            objWindow = Iwr_FindInputWindow(objInput.ICV_objArgs.INA_lWindow);
            if (objWindow == null)
            {   /* No record: the throw came before registration, or the   */
                /* renderer's finally ended it -- nothing left.            */
                return(true);
            }

            try
            {   /* A picker still standing is closed -- through its own close,   */
                /* so it can still refuse.                                       */
                if (objWindow.IWN_frmInput != null && !objWindow.IWN_frmInput.IsDisposed)
                {   /* Closed here; no modal call of it is running any more. */
                    objWindow.IWN_evClosing = null;
                    objWindow.IWN_frmInput.Close();
                    if (objWindow.IWN_evClosing != null && objWindow.IWN_evClosing.Cancel)
                    {   /* It refused: left open, as the user's.  Its renderer   */
                        /* has unwound, so nothing would report the end when     */
                        /* the user closes it later: the window watch is armed   */
                        /* to do so.                                             */
                        Iwr_objLifetimes.LFT_Arm(objWindow.IWN_frmInput);
                        return(false);
                    }

                    /* Closed: disposed too -- a close only hides a form shown   */
                    /* with ShowDialog (documented), and this one may never      */
                    /* have been shown at all.                                   */
                    objWindow.IWN_frmInput.Dispose();
                }

                /* The input ended here, as its renderer would have. */
                IWR_InputReturned(objInput.ICV_objArgs.INA_lWindow);

                /* Nothing remains. */
                return(true);
            }
            catch (Exception exCleanup)
            {   /* The clean-up itself failed: logged first. */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("input cleanup", exCleanup.ToString());
                }

                if (Iwr_FindInputWindow(objInput.ICV_objArgs.INA_lWindow) == null)
                {   /* The record is already gone: the throw came from        */
                    /* inside IWR_InputReturned after its removal (its        */
                    /* post, the watch's settling, or a close's answer), so   */
                    /* whether the worker was told is not known, and          */
                    /* neither the watch nor a later report would tell it     */
                    /* -- both find no record.  True, so the caller posts     */
                    /* InputClosed by its own route; a second InputClosed     */
                    /* for the same input finds nothing to remove and does    */
                    /* nothing.                                               */
                    return(true);
                }

                if (objWindow.IWN_frmInput != null)
                {   /* Still registered, with a picker: the renderer has      */
                    /* unwound, so the window watch ends the input when the   */
                    /* picker is gone (now, if it already is), and its end    */
                    /* reports it.                                            */
                    Iwr_objLifetimes.LFT_Arm(objWindow.IWN_frmInput);
                }

                /* Still registered: ownership kept for now -- the watch       */
                /* reports the end once the picker is gone.  (With no picker   */
                /* the record cannot be left here: IWR_InputReturned removes   */
                /* it before anything in it can throw.)                        */
                return(false);
            }
        }

        /*--------------------------------------------------------------------*/
        /* IWR_AskInputClose:                                                 */
        /*                                                                    */
        /* The worker's InputClose question (Sys_CloseInputSet), delivered    */
        /* (CommandQuestionPresenter.cs): an image browser's picker is closed */
        /* now, and the question answered Closed when its renderer reports    */
        /* the return (IWR_InputReturned) -- or Cancel at once if the picker  */
        /* refuses or the close fails.  A value input is not closed: it is    */
        /* left to finish, and its return answers.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objPresentation : the question's display.                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : answered, or left for the input's return to answer.     */
        /*--------------------------------------------------------------------*/
        internal void IWR_AskInputClose(PRS_clsPresentation objPresentation)
        {
            IWN_clsInputWindow objWindow;

            if (objPresentation == null || !objPresentation.PRS_objQuestion.QST_IsCurrent(objPresentation))
            {   /* No display, or no longer the current one: ignored. */
                return;
            }

            /* The input the question names. */
            objWindow = Iwr_FindInputWindow(objPresentation.PRS_objQuestion.QST_lWindow);
            if (objWindow == null)
            {   /* Not open (returned already, or never started): answered   */
                /* Closed -- there is nothing to close.                      */
                objPresentation.PRS_objQuestion.QST_Reply(objPresentation, new ANS_clsAnswer(ANK_enmAnswerKind.Closed, null));
                return;
            }

            /* The question kept BEFORE the close: the close can return the   */
            /* picker's modal call, whose finally reports the end and must    */
            /* find the question to answer.                                   */
            objWindow.IWN_objClose = objPresentation;
            if (objWindow.IWN_frmInput == null)
            {   /* A value input: nothing is closed.  The dialog finishes as   */
                /* the user leaves it, and its return answers the question.    */
                return;
            }

            try
            {   /* The picker closed through its own Close, marked first as   */
                /* asked, so the renderer ignores what it returns.            */
                objWindow.IWN_bCancelRequested = true;
                objWindow.IWN_evClosing = null;
                objWindow.IWN_frmInput.Close();
                if (objWindow.IWN_evClosing != null && objWindow.IWN_evClosing.Cancel)
                {   /* It refused: the marks taken back and the question        */
                    /* answered Cancel -- the worker's command stops; nothing   */
                    /* polls the window.                                        */
                    objWindow.IWN_bCancelRequested = false;
                    objWindow.IWN_objClose = null;
                    objPresentation.PRS_objQuestion.QST_Reply(objPresentation,
                        new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null, "The input window declined to close."));
                }
            }
            catch (Exception exClose)
            {   /* The close threw: the marks taken back and the question      */
                /* answered Cancel with the reason, so the command stops and   */
                /* the editor keeps its content.                               */
                objWindow.IWN_bCancelRequested = false;
                objWindow.IWN_objClose = null;
                objPresentation.PRS_objQuestion.QST_Reply(objPresentation,
                    new ANS_clsAnswer(ANK_enmAnswerKind.Cancel, null, "Input closure failed: " + exClose.Message));
            }
        }
    }
}
