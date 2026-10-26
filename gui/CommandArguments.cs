/*----------------------------------------------------------------------------*/
/* CommandArguments.cs                                                        */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The arguments requests carry, and the one factory that makes a request     */
/* (CFC_Create).  Each argument record copies what it is given when it is     */
/* made, so a later change in a window or a list cannot change what a queued  */
/* request does.  The factory checks that each kind carries the right         */
/* argument record and a complete shape, then builds the duplicate key and    */
/* the stream key the queue compares.  Nothing here grants permission to do   */
/* anything or decides what follows: that is the worker's.                    */
/*----------------------------------------------------------------------------*/

/* The framework: ArgumentException, Convert, Enum and BitConverter          */
/* (System), the private copies of lists (Collections.Generic), the          */
/* invariant number text of the keys (Globalization), and the SHA-256 of a   */
/* table's pieces (Security.Cryptography).                                   */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;

namespace BifrostFM
{
    /* The edit kinds an Edit request carries; each is one change, and        */
    /* whether it may run is the worker's to decide -- documentation table.   */
    internal enum EDK_enmEditKind
    {
        MakeDirectory = 0,  // create one folder at the one path
        Rename = 1,         // rename the one item to its one destination
        Move = 2,           // move each item to its destination, pair by pair
        Delete = 3,         // delete each item, its folder-or-not flag beside it
        Attributes = 4,     // set the one item's mode and / or owner, each only when its flag says so
        CreateFile = 5,     // create one image file from its stage (New File)
        NameDirectory = 6,  // ask a name, then create that folder in the one path (New Folder)
        NameEntry = 7       // ask a new name for the one item, then rename it (Rename)
    }

    /*------------------------------------------------------------------------*/
    /* EDA_clsEditArgs -- an Edit request's arguments, copied when made, so a */
    /* later change in a list or the Properties dialog cannot change them --  */
    /* documentation table.                                                   */
    /*------------------------------------------------------------------------*/
    internal sealed class EDA_clsEditArgs
    {
        internal EDK_enmEditKind EDA_enmKind;       // the kind of change
        internal List<string> EDA_lstDestinations;  // a Rename's or Move's destinations, a private copy; empty otherwise
        internal List<bool> EDA_lstDirectories;     // a Delete's folder-or-not flags, one per item, a private copy; empty otherwise
        internal bool EDA_bMode;                    // Attributes: set the mode
        internal uint EDA_uiMode;                   // Attributes: the mode's permission bits
        internal bool EDA_bOwner;                   // Attributes: set the owner and group
        internal uint EDA_uiUid;                    // Attributes: the owner's number
        internal uint EDA_uiGid;                    // Attributes: the group's number
        internal STG_clsStage EDA_objStage;         // CreateFile: the content's stage, the request's from posting (NST_Release deletes it); null otherwise
        internal string EDA_sAttributesFrom;        // CreateFile: the entry whose attributes the new file takes, or null / ""
        internal int EDA_iOpenHow;                  // CreateFile: the viewer to open it in (VWH_*); -1 for none
        internal bool EDA_bNewDocument;             // CreateFile: open it in the structured editor as a new, empty document

        /*--------------------------------------------------------------------*/
        /* EDA_clsEditArgs:                                                   */
        /*                                                                    */
        /* An edit's arguments, the lists copied.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     enmKind         : the kind of change.                          */
        /*     arrDestinations : the destinations, or null for none.          */
        /*     arrDirectories  : the folder-or-not flags, or null for none.   */
        /*     bMode           : set the mode.                                */
        /*     uiMode          : the mode's bits.                             */
        /*     bOwner          : set the owner and group.                     */
        /*     uiUid           : the owner's number.                          */
        /*     uiGid           : the group's number.                          */
        /*     objStage        : CreateFile's stage, else null.               */
        /*     sAttributesFrom : the entry to take attributes from, or null.  */
        /*     iOpenHow        : the viewer for a new file; optional, -1.     */
        /*     bNewDocument    : a new structured document; optional, false.  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the arguments; their shape is checked by       */
        /*                     CFC_Create (Cfc_Validate).                     */
        /*--------------------------------------------------------------------*/
        internal EDA_clsEditArgs(EDK_enmEditKind enmKind, string[] arrDestinations, bool[] arrDirectories,
            bool bMode, uint uiMode, bool bOwner, uint uiUid, uint uiGid,
            STG_clsStage objStage, string sAttributesFrom, int iOpenHow = -1, bool bNewDocument = false)
        {
            /* The values as given.  Holding the stage here does not make it   */
            /* the request's: only posting the request does (SYS_Post), and    */
            /* until then its maker still owns it.                             */
            EDA_enmKind = enmKind;
            EDA_bMode = bMode;
            EDA_uiMode = uiMode;
            EDA_bOwner = bOwner;
            EDA_uiUid = uiUid;
            EDA_uiGid = uiGid;
            EDA_objStage = objStage;
            EDA_sAttributesFrom = sAttributesFrom;
            EDA_iOpenHow = iOpenHow;
            EDA_bNewDocument = bNewDocument;

            /* The destinations copied into a list of their own, so a later     */
            /* change to the caller's array cannot change where a queued move   */
            /* goes.                                                            */
            if (arrDestinations == null)
            {   /* None: an empty list. */
                EDA_lstDestinations = new List<string>();
            }
            else
            {   /* Given: copied. */
                EDA_lstDestinations = new List<string>(arrDestinations);
            }

            /* The flags copied likewise: each stays paired with its item as   */
            /* the request was made, whatever the pane shows later.            */
            if (arrDirectories == null)
            {   /* None: an empty list (the kinds that need no flags). */
                EDA_lstDirectories = new List<bool>();
            }
            else
            {   /* Given: copied. */
                EDA_lstDirectories = new List<bool>(arrDirectories);
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* ELA_clsElevationArgs -- the arguments of an Exit that restarts the     */
    /* program as Administrator: what the restart is for, the command the     */
    /* new copy runs, and the disk it runs it on when one was chosen before   */
    /* the restart.  The launch itself is the UI's, after the Exit has closed */
    /* everything -- documentation table.                                     */
    /*------------------------------------------------------------------------*/
    internal sealed class ELA_clsElevationArgs
    {
        internal string ELA_sWhat;     // the device action, in words, for the restart question
        internal string ELA_sCommand;  // the start-up command the elevated copy runs, without its leading dashes
        internal string ELA_sDisk;     // the disk already chosen, and its risk warning answered, before the restart (\\.\PhysicalDriveN), passed on as --disk; "" when the elevated copy is to ask its own device

        /*--------------------------------------------------------------------*/
        /* ELA_clsElevationArgs:                                              */
        /*                                                                    */
        /* The restart's arguments, kept as given.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sWhat    : the action's words, not empty.                      */
        /*     sCommand : the start-up command, not empty.                    */
        /*     sDisk    : the disk chosen before the restart, or "" (null is  */
        /*                kept as "") when there is none.                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the arguments; an ArgumentException for the    */
        /*                     words or the command missing.  Nothing is      */
        /*                     started or closed.                             */
        /*--------------------------------------------------------------------*/
        internal ELA_clsElevationArgs(string sWhat, string sCommand, string sDisk)
        {
            if (String.IsNullOrEmpty(sWhat) || String.IsNullOrEmpty(sCommand))
            {   /* Either missing: refused -- the action a queued restart   */
                /* resumes is never taken from a later click.               */
                throw new ArgumentException("Elevated restart needs its captured action and startup command.");
            }

            if (sDisk == null)
            {   /* No disk given: kept empty, never null, so the launch   */
                /* tests one value.                                       */
                sDisk = "";
            }

            /* Kept as given.  The Exit asks, closes everything, and only   */
            /* then lets the UI start the elevated copy (StartElevated).    */
            ELA_sWhat = sWhat;
            ELA_sCommand = sCommand;
            ELA_sDisk = sDisk;
        }
    }

    /*------------------------------------------------------------------------*/
    /* CCA_clsCopyArgs -- a copy's or cut's arguments: each item's other end, */
    /* the extract and import options, and for a cut how its sources are      */
    /* removed and which clipboard contents it spends -- documentation table. */
    /*------------------------------------------------------------------------*/
    internal sealed class CCA_clsCopyArgs
    {
        internal List<string> CCA_lstOtherPaths;      // each item's other end (a Windows path, or for a copy within its image destination), a private copy
        internal EXO_stExtractOptions CCA_stExtract;  // the extract's options (numbers only)
        internal IMO_stImportOptions CCA_stImport;    // the import's options (numbers only)
        internal bool CCA_bMoveSources;               // a cut: the sources removed after the copy (within the image, a rename instead)
        internal List<bool> CCA_lstDirectories;       // an extract cut's folder-or-not flags, one per item, a private copy; empty otherwise
        internal uint CCA_uiClipboard;                // the cut's clipboard sequence number; 0 means no clipboard to clear
        internal bool CCA_bAutoNames;                 // a copy within pasted beside itself: free " - Copy" names chosen when it runs
        internal long CCA_lClipboardLifetime;         // the close count the cut was captured under (a cut out of the image); -1 otherwise

        /*--------------------------------------------------------------------*/
        /* CCA_clsCopyArgs:                                                   */
        /*                                                                    */
        /* A copy's arguments, the lists copied.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrOtherPaths      : each item's other end.                    */
        /*     stExtract          : the extract options.                      */
        /*     stImport           : the import options.                       */
        /*     bMoveSources       : a cut; optional, false.                   */
        /*     arrDirectories     : an extract cut's flags; optional, null.   */
        /*     uiClipboard        : the cut's sequence number; optional, 0.   */
        /*     bAutoNames         : free names beside the items; optional,    */
        /*                          false.                                    */
        /*     lClipboardLifetime : the close count; optional, -1.            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the arguments; their shape is checked by       */
        /*                     CFC_Create.                                    */
        /*--------------------------------------------------------------------*/
        internal CCA_clsCopyArgs(string[] arrOtherPaths, EXO_stExtractOptions stExtract,
            IMO_stImportOptions stImport, bool bMoveSources = false, bool[] arrDirectories = null,
            uint uiClipboard = 0, bool bAutoNames = false, long lClipboardLifetime = -1)
        {
            /* The options and the cut's values, as given.  The option       */
            /* structures hold numbers only, so the assignment copies them   */
            /* whole.                                                        */
            CCA_stExtract = stExtract;
            CCA_stImport = stImport;
            CCA_bMoveSources = bMoveSources;
            CCA_uiClipboard = uiClipboard;
            CCA_bAutoNames = bAutoNames;
            CCA_lClipboardLifetime = lClipboardLifetime;

            /* The other ends copied into a list of their own. */
            if (arrOtherPaths == null)
            {   /* None: an empty list, which CFC_Create then refuses. */
                CCA_lstOtherPaths = new List<string>();
            }
            else
            {   /* Given: copied. */
                CCA_lstOtherPaths = new List<string>(arrOtherPaths);
            }

            /* The flags copied likewise. */
            if (arrDirectories == null)
            {   /* None: an empty list (every copy but an extract cut). */
                CCA_lstDirectories = new List<bool>();
            }
            else
            {   /* Given: copied. */
                CCA_lstDirectories = new List<bool>(arrDirectories);
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* SVA_clsSaveArgs -- an editor's save into the image: its stage and      */
    /* where it goes -- documentation table.                                  */
    /*------------------------------------------------------------------------*/
    internal sealed class SVA_clsSaveArgs
    {
        internal STG_clsStage SVA_objStage;  // the content's stage, the request's from posting (NST_Release deletes it)
        internal string SVA_sExpectedEntry;  // where the editor's Save wrote until now (its registration's entry)
        internal string SVA_sDestination;    // where this save writes; the same entry for a plain Save
        internal bool SVA_bOverwrite;        // replacing was agreed in the Save As dialog -- part of the duplicate key only; the save step asks again on an existing file (Nst_Save)
        internal long SVA_lSaveOrder;        // the save's order: one older than the editor's last save is refused (CommandSequences.cs)
        internal bool SVA_bSameFilesystem;   // the destination is in the filesystem the editor's Save writes to now

        /*--------------------------------------------------------------------*/
        /* SVA_clsSaveArgs:                                                   */
        /*                                                                    */
        /* A save's arguments, kept as given.                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objStage         : the content's stage.                        */
        /*     sExpectedEntry   : where the editor's Save wrote until now.    */
        /*     sDestination     : where this save writes.                     */
        /*     bOverwrite       : replacing was agreed.                       */
        /*     lSaveOrder       : the save's order; optional, 0.              */
        /*     bSameFilesystem  : the same filesystem; optional, true.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the arguments; an ArgumentException for no     */
        /*                     stage or a path missing (the stage stays its   */
        /*                     maker's).                                      */
        /*--------------------------------------------------------------------*/
        internal SVA_clsSaveArgs(STG_clsStage objStage, string sExpectedEntry, string sDestination,
            bool bOverwrite, long lSaveOrder = 0, bool bSameFilesystem = true)
        {
            if (objStage == null || String.IsNullOrEmpty(sExpectedEntry) || String.IsNullOrEmpty(sDestination))
            {   /* Refused: neither the content nor the destination is ever   */
                /* rebuilt later from the editor or the dialog.               */
                throw new ArgumentException("A Save needs a snapshot and complete entry paths.");
            }

            /* Kept as given.  The content's revision and the epoch travel in   */
            /* the request itself.                                              */
            SVA_objStage = objStage;
            SVA_sExpectedEntry = sExpectedEntry;
            SVA_sDestination = sDestination;
            SVA_bOverwrite = bOverwrite;
            SVA_lSaveOrder = lSaveOrder;
            SVA_bSameFilesystem = bSameFilesystem;
        }
    }

    /*------------------------------------------------------------------------*/
    /* POA_clsPartitionOpenArgs -- OpenPartition's arguments: the window's    */
    /* short caption, whether to offer the table-kind choice, and how the     */
    /* disk is still to be found.  The disk's full description and size are   */
    /* the request's target -- documentation table.                           */
    /*------------------------------------------------------------------------*/
    internal sealed class POA_clsPartitionOpenArgs
    {
        internal string POA_sName;      // the window's short caption (the file's or model's name); "" only while the device is still to be found
        internal bool POA_bAskKind;     // offer the table-kind choice at once (a new image from New Image); permission for nothing
        internal bool POA_bAskDevice;   // true: no disk set yet -- the sequence lists the disks and asks it (DeviceChoice), or takes POA_sDisk from the list, then sets the target and the caption
        internal string POA_sDisk;      // with POA_bAskDevice: the disk chosen, and its risk warning answered, by the copy that restarted this one as Administrator (--disk) -- found in the list with no question and no warning; "" to ask

        /*--------------------------------------------------------------------*/
        /* POA_clsPartitionOpenArgs:                                          */
        /*                                                                    */
        /* The window's arguments, kept as given.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sName      : the caption; not empty unless bAskDevice.         */
        /*     bAskKind   : offer the table-kind choice.                      */
        /*     bAskDevice : the disk is still to be found.                    */
        /*     sDisk      : with bAskDevice, the disk a restart chose; "" (or */
        /*                  null, kept as "") to ask.                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the arguments; an ArgumentException for no     */
        /*                     caption on a disk already chosen, or a         */
        /*                     restart's disk on one not still to be found.   */
        /*--------------------------------------------------------------------*/
        internal POA_clsPartitionOpenArgs(string sName, bool bAskKind, bool bAskDevice, string sDisk)
        {
            if (String.IsNullOrEmpty(sName) && !bAskDevice)
            {   /* No caption for a disk already chosen: refused -- it is   */
                /* never taken from a window later.                         */
                throw new ArgumentException("A partition window needs its disk caption.");
            }

            if (sDisk == null)
            {   /* No restart's disk: kept empty, never null. */
                sDisk = "";
            }

            if (sDisk.Length != 0 && !bAskDevice)
            {   /* A restart's disk on a request whose disk is already set:   */
                /* refused -- the two would name the disk twice.              */
                throw new ArgumentException("A partition window's restart disk needs a disk still to be found.");
            }

            /* Whether the disk is still to be found, and the restart's    */
            /* choice when there was one; the caption is then set by the   */
            /* sequence once the disk is known.                            */
            POA_bAskDevice = bAskDevice;
            POA_sDisk = sDisk;
            if (sName == null)
            {   /* None given (asking): kept empty, never null. */
                sName = "";
            }

            /* Kept as given: display facts, which neither change the target   */
            /* nor stand for the worker's risk question.                       */
            POA_sName = sName;
            POA_bAskKind = bAskKind;
        }
    }

    /*------------------------------------------------------------------------*/
    /* MFC_clsMakeFsChoice -- what the Make Filesystem options dialog chose,  */
    /* carried back to the format's sequence in the MakeFsOptions question's  */
    /* answer (ANS_objValue) -- documentation table.                          */
    /*------------------------------------------------------------------------*/
    internal sealed class MFC_clsMakeFsChoice
    {
        internal MFO_stMakeFsOptions MFC_stOptions;  // the type, block size, label and zero-fill flag, as the engine takes them (size and overwrite 0: the range is the size)
        internal ulong MFC_ullLength;                // the range's length to format, bytes: the range's own, or less for a whole disk made smaller

        /*--------------------------------------------------------------------*/
        /* MFC_clsMakeFsChoice:                                               */
        /*                                                                    */
        /* The choice, kept as given.  The sequence builds the format's       */
        /* arguments from it (PAA_clsPartitionArgs copies the label), so      */
        /* nothing here is checked again.                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stOptions : the options read from the dialog.                  */
        /*     ullLength : the length to format.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the choice.                                    */
        /*--------------------------------------------------------------------*/
        internal MFC_clsMakeFsChoice(MFO_stMakeFsOptions stOptions, ulong ullLength)
        {
            /* Both kept as they are. */
            MFC_stOptions = stOptions;
            MFC_ullLength = ullLength;
        }
    }

    /*------------------------------------------------------------------------*/
    /* PAA_clsPartitionArgs -- the arguments of a table write, a format or an */
    /* expansion: the table's pieces and the format's options, copied when    */
    /* made, a key over the pieces, and the partition's words for the         */
    /* questions and windows -- documentation table.                          */
    /*------------------------------------------------------------------------*/
    internal sealed class PAA_clsPartitionArgs
    {
        internal List<PTB_Piece> PAA_lstPieces;      // the table's pieces in writing order, each with its own copy of its bytes; the steps use them as they are
        internal MFO_stMakeFsOptions PAA_stOptions;  // the format's options, with a private copy of the label bytes
        internal bool PAA_bGrowTable;                // an expansion that grows the partition: the pieces are written before the resize
        internal string PAA_sContentKey;             // SHA-256 (Base64) over the pieces in order, for the duplicate key
        internal string PAA_sPlace;                  // the partition's short name, for the progress window and the reports; "" when none
        internal string PAA_sTarget;                 // the range, its size and what it holds, for the question; "" when none

        /*--------------------------------------------------------------------*/
        /* PAA_clsPartitionArgs:                                              */
        /*                                                                    */
        /* The arguments, the pieces and the label copied, the key made.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstPieces  : the table's pieces, in order, or null for none.   */
        /*     stOptions  : the format's options.                             */
        /*     bGrowTable : the expansion grows the partition.                */
        /*     sPlace     : the partition's name; optional, "".               */
        /*     sTarget    : the range's description; optional, "".            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the arguments; an ArgumentException for too    */
        /*                     many pieces or a piece with no bytes or too    */
        /*                     many.                                          */
        /*--------------------------------------------------------------------*/
        internal PAA_clsPartitionArgs(List<PTB_Piece> lstPieces, MFO_stMakeFsOptions stOptions,
            bool bGrowTable, string sPlace = "", string sTarget = "")
        {
            PTB_Piece objPiece;
            MFO_stMakeFsOptions stPrivate;
            int i;

            if (lstPieces != null && lstPieces.Count > ENG_clsNative.ENG_WRITE_BATCH_MAX_PIECES)
            {   /* More pieces than the engine's batch takes: refused before   */
                /* anything is copied.                                         */
                throw new ArgumentException("Too many partition metadata pieces.");
            }

            /* The pieces, each copied with its own bytes, in the order given    */
            /* (the order they are written in; never sorted).  None is valid:    */
            /* a format, or an expansion that does not grow the table.  The      */
            /* sector alignment and the disk's bounds are the engine's checks.   */
            PAA_lstPieces = new List<PTB_Piece>();
            if (lstPieces != null)
            {   /* Pieces given: each checked and copied. */
                for (i = 0; i < lstPieces.Count; i++)
                {   /* One piece per iteration.                                  */
                    /* A piece with no bytes, or more than one raw write takes   */
                    /* (ENG_WRITE_RAW_MAX), is refused before it is kept.        */
                    objPiece = lstPieces[i];
                    if (objPiece == null || objPiece.abBytes == null ||
                        objPiece.abBytes.Length == 0 ||
                        objPiece.abBytes.Length > ENG_clsNative.ENG_WRITE_RAW_MAX)
                    {   /* Refused. */
                        throw new ArgumentException("A partition piece has no valid bounded bytes.");
                    }

                    /* Copied: a later edit of the layout cannot reach these bytes. */
                    PAA_lstPieces.Add(new PTB_Piece { ullOffset = objPiece.ullOffset,
                        abBytes = (byte[])objPiece.abBytes.Clone() });
                }
            }

            /* The options: assigning the structure copies its numbers but      */
            /* shares its label array, so the label is copied too -- or, when   */
            /* none was given, a zeroed field of the engine's width             */
            /* (ENG_LABEL_FIELD).                                               */
            stPrivate = stOptions;
            if (stOptions.MFO_arrLabel == null)
            {   /* No label: a zeroed field. */
                stPrivate.MFO_arrLabel = new byte[ENG_clsNative.ENG_LABEL_FIELD];
            }
            else
            {   /* A label: its own copy. */
                stPrivate.MFO_arrLabel = (byte[])stOptions.MFO_arrLabel.Clone();
            }

            /* The options kept, and the key made over the copied pieces.  The   */
            /* steps pass these buffers to the engine as they are, with no       */
            /* further copy.                                                     */
            PAA_stOptions = stPrivate;
            PAA_bGrowTable = bGrowTable;
            PAA_sContentKey = Paa_MakeContentKey();

            /* The words, "" when none, kept as given. */
            PAA_sPlace = "";
            PAA_sTarget = "";
            if (sPlace != null)
            {   /* A name given. */
                PAA_sPlace = sPlace;
            }

            if (sTarget != null)
            {   /* A description given. */
                PAA_sTarget = sTarget;
            }
        }

        /*--------------------------------------------------------------------*/
        /* Paa_MakeContentKey:                                                */
        /*                                                                    */
        /* A fixed-size key for the pieces, for the duplicate key: two tables */
        /* give the same key only when they are the same pieces in the same   */
        /* order.  A key only -- no permission and no file format.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the SHA-256, as Base64.                               */
        /*--------------------------------------------------------------------*/
        private string Paa_MakeContentKey()
        {
            SHA256 objHash;
            byte[] arrNumber;
            PTB_Piece objPiece;
            int i;

            /* A hash rather than the bytes themselves, so the key stays the     */
            /* same short size however large the table is; fed piece by piece,   */
            /* so no text the size of the table is ever made.                    */
            objHash = SHA256.Create();
            using (objHash)
            {   /* The hash released at the block's end, a failure included.     */
                /* The piece count first, then each piece's offset, length and   */
                /* bytes: fixed-width numbers between the pieces, so no two      */
                /* different tables can feed the same bytes.                     */
                arrNumber = BitConverter.GetBytes(PAA_lstPieces.Count);
                objHash.TransformBlock(arrNumber, 0, arrNumber.Length, arrNumber, 0);
                for (i = 0; i < PAA_lstPieces.Count; i++)
                {   /* One piece per iteration: its offset, its length, its   */
                    /* bytes -- the same bytes at another offset, or in       */
                    /* another order, give another key.                       */
                    objPiece = PAA_lstPieces[i];
                    arrNumber = BitConverter.GetBytes(objPiece.ullOffset);
                    objHash.TransformBlock(arrNumber, 0, arrNumber.Length, arrNumber, 0);
                    arrNumber = BitConverter.GetBytes(objPiece.abBytes.Length);
                    objHash.TransformBlock(arrNumber, 0, arrNumber.Length, arrNumber, 0);
                    objHash.TransformBlock(objPiece.abBytes, 0, objPiece.abBytes.Length,
                        objPiece.abBytes, 0);
                }

                /* The hash finished, before its value is read. */
                objHash.TransformFinalBlock(new byte[0], 0, 0);

                /* The value, as Base64 text. */
                return(Convert.ToBase64String(objHash.Hash));
            }
        }

    }

    /*------------------------------------------------------------------------*/
    /* PRA_clsPartitionRawArgs -- one partition's Read, Write or Erase, as    */
    /* the partition window captured it.  The partition's byte range is the   */
    /* request's own target (the window's disk, at the partition's offset and */
    /* length); this carries the rest: the Windows file and the two           */
    /* descriptions the questions and the progress window use, frozen at the  */
    /* click so a later selection cannot change what the user agreed to.      */
    /*------------------------------------------------------------------------*/
    internal sealed class PRA_clsPartitionRawArgs
    {
        /* The captured file and wording -- documentation table. */
        internal string PRA_sFile;   // the Windows file: Read's new output, Write's input; "" for an Erase
        internal string PRA_sPlace;  // terse partition name ("card.img: p2 @ 4,194,304"), for questions and progress
        internal string PRA_sTarget; // the place with its size and what it holds, for the Erase question

        /*--------------------------------------------------------------------*/
        /* PRA_clsPartitionRawArgs:                                           */
        /*                                                                    */
        /* Freeze one partition command's file and wording.  The place and    */
        /* target texts are required -- they are what the confirmation names  */
        /* -- while the file is checked per command by Cfc_Validate (an Erase */
        /* has none).                                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sFile   : the Windows file, or "" for an Erase.                */
        /*     sPlace  : the partition's terse name.                          */
        /*     sTarget : the name with size and contents.                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the captured values; a missing description     */
        /*                     throws before the request exists.              */
        /*--------------------------------------------------------------------*/
        internal PRA_clsPartitionRawArgs(string sFile, string sPlace, string sTarget)
        {
            if (String.IsNullOrEmpty(sPlace) || String.IsNullOrEmpty(sTarget))
            {   /* A later selection cannot supply the partition the user meant. */
                throw new ArgumentException("A partition command needs its captured partition description.");
            }

            /* A missing file is kept as "" (an Erase); the strings are retained   */
            /* as they are, no UI or table model being borrowed.                   */
            PRA_sFile   = "";
            PRA_sPlace  = sPlace;
            PRA_sTarget = sTarget;
            if (sFile != null)
            {   /* Read's output or Write's input. */
                PRA_sFile = sFile;
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* RWA_clsRawArgs -- a raw copy's arguments: its two ends with their      */
    /* offsets, the length, the hash wish, the window's words and the GPT     */
    /* box, captured when the picker returned -- documentation table.         */
    /*------------------------------------------------------------------------*/
    internal sealed class RWA_clsRawArgs
    {
        internal string RWA_sFrom;      // the source's path (a device or a file); "" for an erase
        internal ulong RWA_ullFrom;     // where in the source the copy starts, bytes
        internal string RWA_sTo;        // the destination's path (a file or a device)
        internal ulong RWA_ullTo;       // where in the destination it starts, bytes
        internal ulong RWA_ullLength;   // the bytes to copy; 0 for the rest of the source
        internal bool RWA_bHash;        // compute the image file's SHA-256 after a successful copy (and write its .sha256)
        internal string RWA_sTitle;     // the progress window's title; "" for the direction's own
        internal string RWA_sCopyText;  // the copy phase's words; "" for the direction's own
        internal bool RWA_bThroughGpt;  // a device read through the backup GPT header's sector (offsets 0, length 0): the sequence reads the GPT end from the device

        /*--------------------------------------------------------------------*/
        /* RWA_clsRawArgs:                                                    */
        /*                                                                    */
        /* A raw copy's arguments, kept as given.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sFrom       : the source's path.                               */
        /*     ullFrom     : its offset, bytes.                               */
        /*     sTo         : the destination's path.                          */
        /*     ullTo       : its offset, bytes.                               */
        /*     ullLength   : the bytes to copy; 0 for the rest.               */
        /*     bHash       : compute the SHA-256.                             */
        /*     sTitle      : the window's title; optional, "".                */
        /*     sCopyText   : the phase's words; optional, "".                 */
        /*     bThroughGpt : a device read through the backup GPT (the GPT    */
        /*                   box); optional, false.                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the arguments; the ends' identities and sizes  */
        /*                     are the engine's to check when it runs.        */
        /*--------------------------------------------------------------------*/
        internal RWA_clsRawArgs(string sFrom, ulong ullFrom, string sTo, ulong ullTo,
            ulong ullLength, bool bHash, string sTitle = "", string sCopyText = "",
            bool bThroughGpt = false)
        {
            /* The ends and the range as given -- a path here is only a path;   */
            /* which object it names is the raw sequence's and the engine's     */
            /* question when the copy runs.                                     */
            RWA_sFrom = sFrom;
            RWA_ullFrom = ullFrom;
            RWA_sTo = sTo;
            RWA_ullTo = ullTo;
            RWA_ullLength = ullLength;
            RWA_bHash = bHash;
            RWA_bThroughGpt = bThroughGpt;

            /* The words, "" when none, kept as given (never read from the   */
            /* picker later).                                                */
            RWA_sTitle = "";
            RWA_sCopyText = "";
            if (sTitle != null)
            {   /* A title given. */
                RWA_sTitle = sTitle;
            }

            if (sCopyText != null)
            {   /* Phase words given. */
                RWA_sCopyText = sCopyText;
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* CFC_clsCommands -- the factory every request is made through: it       */
    /* checks the kind's arguments and shape, and builds the keys the queue   */
    /* compares.  It runs nothing.                                            */
    /*------------------------------------------------------------------------*/
    internal static class CFC_clsCommands
    {
        /*--------------------------------------------------------------------*/
        /* CFC_Create:                                                        */
        /*                                                                    */
        /* Make a request: its shape checked (Cfc_Validate), its epoch and    */
        /* target taken from the state view the poster had, and its duplicate */
        /* key and stream key built.                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     enmKind    : the kind (its class follows from it, SYS_Policy). */
        /*     lOrigin    : the asking window's id; 0 for the main window.    */
        /*     objView    : the last state view the poster had, or null at    */
        /*                  start-up.                                         */
        /*     objTarget  : the target, or null for the view's.               */
        /*     arrPaths   : the items' paths, or null.                        */
        /*     lRevision  : an editor's revision, a Cancel's serial or a      */
        /*                  drag's token.                                     */
        /*     uiFlags    : the kind's options.                               */
        /*     objPayload : the kind's argument record, or null.              */
        /*     sFolder    : the image folder to list afterwards.              */
        /*     sStream    : the stream within the origin and target.          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RQT_clsRequest : the request, not yet posted; an               */
        /*                      ArgumentException for a malformed one         */
        /*                      (nothing it carries is handed over).          */
        /*--------------------------------------------------------------------*/
        internal static RQT_clsRequest CFC_Create(KND_enmKind enmKind, long lOrigin, STV_clsStateView objView,
            TGT_clsTarget objTarget, string[] arrPaths, long lRevision, uint uiFlags,
            object objPayload, string sFolder, string sStream)
        {
            List<string> lstIdentity;
            long lEpoch;
            string sTarget;
            string sDiskBytes;
            string sSectorBytes;
            string sPathCount;

            /* The epoch the poster saw, and the view's target when none was   */
            /* given -- the state as captured, never the panes now.            */
            lEpoch = 0;
            if (objView != null)
            {   /* A view: its epoch. */
                lEpoch = objView.STV_lEpoch;
                if (objTarget == null)
                {   /* No target given: the view's. */
                    objTarget = objView.STV_objTarget;
                }
            }

            /* The shape checked before anything else; a failure throws here,   */
            /* before the request exists.                                       */
            Cfc_Validate(enmKind, objTarget, arrPaths, objPayload);

            /* The target's part of the keys: its path (upper-cased, as Windows   */
            /* compares names), offset and length, then the disk's size and       */
            /* sector size, which tell partition and device requests apart.       */
            /* Whether two spellings name one object is the engine's test.        */
            sTarget = "";
            sDiskBytes = "";
            sSectorBytes = "";
            if (objTarget != null)
            {   /* A target: its key parts. */
                sTarget = RQT_clsRequest.RQT_MakeKey(objTarget.TGT_sPath.ToUpperInvariant(),
                    objTarget.TGT_ullOffset.ToString(CultureInfo.InvariantCulture),
                    objTarget.TGT_ullLength.ToString(CultureInfo.InvariantCulture));
                sDiskBytes = objTarget.TGT_ullDiskBytes.ToString(CultureInfo.InvariantCulture);
                sSectorBytes = objTarget.TGT_uiSectorBytes.ToString(CultureInfo.InvariantCulture);
            }

            /* The paths' count, so the paths and the arguments after them in   */
            /* the key cannot be mistaken for each other.                       */
            sPathCount = "0";
            if (arrPaths != null)
            {   /* Paths given: their count. */
                sPathCount = arrPaths.Length.ToString(CultureInfo.InvariantCulture);
            }

            /* The duplicate key: every value that makes two requests of one   */
            /* stream different.  (The queue compares kind, origin and epoch   */
            /* itself, Sys_SameStream.)                                        */
            lstIdentity = new List<string>();
            lstIdentity.Add(sTarget);
            lstIdentity.Add(lRevision.ToString(CultureInfo.InvariantCulture));
            lstIdentity.Add(uiFlags.ToString(CultureInfo.InvariantCulture));
            lstIdentity.Add(sFolder);
            lstIdentity.Add(sDiskBytes);
            lstIdentity.Add(sSectorBytes);
            lstIdentity.Add(sPathCount);
            if (arrPaths != null)
            {   /* The paths, in order. */
                lstIdentity.AddRange(arrPaths);
            }

            /* The arguments' values (a save's stage path is NOT among them:   */
            /* its revision and order already tell two saves apart).           */
            Cfc_AppendPayloadIdentity(lstIdentity, objPayload);

            /* The request, with both keys.  Its class is the kind's alone. */
            return(new RQT_clsRequest(enmKind, lOrigin, lEpoch, objTarget, arrPaths, lRevision,
                uiFlags, objPayload, RQT_clsRequest.RQT_MakeKey(lstIdentity.ToArray()),
                RQT_clsRequest.RQT_MakeKey(sTarget, sStream), sFolder));
        }

        /*--------------------------------------------------------------------*/
        /* Cfc_AppendPayloadIdentity:                                         */
        /*                                                                    */
        /* Add an argument record's values to a duplicate key -- every value  */
        /* that changes what the request does.                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstIdentity : the key's values so far.                         */
        /*     objPayload  : the argument record, or null.                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the values added; an ArgumentException for a record of  */
        /*            no known kind.                                          */
        /*--------------------------------------------------------------------*/
        private static void Cfc_AppendPayloadIdentity(List<string> lstIdentity, object objPayload)
        {
            ELA_clsElevationArgs objElevation;
            SVA_clsSaveArgs objSave;
            EDA_clsEditArgs objEdit;
            CCA_clsCopyArgs objCopy;
            RWA_clsRawArgs objRaw;
            PAA_clsPartitionArgs objPartition;
            POA_clsPartitionOpenArgs objPartitionOpen;
            PFA_clsPickerFolderArgs objFolder;
            INA_clsInputArgs objInput;
            EPI_clsEntryProperties objProperties;
            HBA_clsHostBindingArgs objHost;
            VFA_clsViewerFallbackArgs objFallback;
            IFA_clsImageFileArgs objImageFile;
            DOA_clsDeviceOpenArgs objDeviceOpen;
            MFO_stMakeFsOptions stOptions;
            string sStagePath;
            int i;

            if (objPayload == null)
            {   /* No arguments: the request's own values are the key. */
                return;
            }

            /* Which record: the known set of plain records, each tested by   */
            /* type; none holds code to run.                                  */
            objSave = objPayload as SVA_clsSaveArgs;
            objElevation = objPayload as ELA_clsElevationArgs;
            objEdit = objPayload as EDA_clsEditArgs;
            objCopy = objPayload as CCA_clsCopyArgs;
            objRaw = objPayload as RWA_clsRawArgs;
            objPartition = objPayload as PAA_clsPartitionArgs;
            objPartitionOpen = objPayload as POA_clsPartitionOpenArgs;
            objFolder = objPayload as PFA_clsPickerFolderArgs;
            objInput = objPayload as INA_clsInputArgs;
            objHost = objPayload as HBA_clsHostBindingArgs;
            objFallback = objPayload as VFA_clsViewerFallbackArgs;
            objImageFile = objPayload as IFA_clsImageFileArgs;
            objDeviceOpen = objPayload as DOA_clsDeviceOpenArgs;
            if (objPayload is PRA_clsPartitionRawArgs)
            {   /* A partition's Read / Write / Erase: the file and the   */
                /* partition (whose range is already the target's part    */
                /* of the identity) tell two such requests apart.         */
                lstIdentity.Add(((PRA_clsPartitionRawArgs)objPayload).PRA_sFile);
                lstIdentity.Add(((PRA_clsPartitionRawArgs)objPayload).PRA_sPlace);
            }
            else if (objSave != null)
            {   /* A save: everything but its stage path -- each save has a    */
                /* stage of its own, so the path would make two posts of the   */
                /* same save never equal.                                      */
                lstIdentity.Add(objSave.SVA_sExpectedEntry);
                lstIdentity.Add(objSave.SVA_sDestination);
                lstIdentity.Add(objSave.SVA_bOverwrite.ToString());
                lstIdentity.Add(objSave.SVA_lSaveOrder.ToString(CultureInfo.InvariantCulture));
                lstIdentity.Add(objSave.SVA_bSameFilesystem.ToString());
            }
            else if (objElevation != null)
            {   /* A restart: what it is for, what it will run, and on which   */
                /* disk when one was chosen before it.                         */
                lstIdentity.Add(objElevation.ELA_sWhat);
                lstIdentity.Add(objElevation.ELA_sCommand);
                lstIdentity.Add(objElevation.ELA_sDisk);
            }
            else if (objEdit != null)
            {   /* An edit: its kind and every list and option, not only the   */
                /* paths.  Each list is preceded by its count, so the          */
                /* destinations and the flags cannot be read as each other.    */
                lstIdentity.Add(objEdit.EDA_enmKind.ToString());
                lstIdentity.Add(objEdit.EDA_lstDestinations.Count.ToString(CultureInfo.InvariantCulture));
                lstIdentity.AddRange(objEdit.EDA_lstDestinations);
                lstIdentity.Add(objEdit.EDA_lstDirectories.Count.ToString(CultureInfo.InvariantCulture));
                for (i = 0; i < objEdit.EDA_lstDirectories.Count; i++)
                {   /* One folder-or-not flag per iteration. */
                    lstIdentity.Add(objEdit.EDA_lstDirectories[i].ToString());
                }

                /* A new file's stage path IS in the key (unlike a save's): two   */
                /* New Files of the same name may hold different content, each    */
                /* in a stage of its own.  Only the path is read.                 */
                sStagePath = null;
                if (objEdit.EDA_objStage != null)
                {   /* A stage: its path. */
                    sStagePath = objEdit.EDA_objStage.STG_ReadPath();
                }

                /* The options, the attribute source, the stage and the viewer. */
                lstIdentity.Add(objEdit.EDA_bMode.ToString());
                lstIdentity.Add(objEdit.EDA_uiMode.ToString(CultureInfo.InvariantCulture));
                lstIdentity.Add(objEdit.EDA_bOwner.ToString());
                lstIdentity.Add(objEdit.EDA_uiUid.ToString(CultureInfo.InvariantCulture));
                lstIdentity.Add(objEdit.EDA_uiGid.ToString(CultureInfo.InvariantCulture));
                lstIdentity.Add(objEdit.EDA_sAttributesFrom);
                lstIdentity.Add(sStagePath);
                lstIdentity.Add(objEdit.EDA_iOpenHow.ToString(CultureInfo.InvariantCulture));
                lstIdentity.Add(objEdit.EDA_bNewDocument.ToString());
            }
            else if (objCopy != null)
            {   /* A copy: its other ends, then every option of both option   */
                /* structures (numbers, copied when the record was made).     */
                lstIdentity.Add(objCopy.CCA_lstOtherPaths.Count.ToString(CultureInfo.InvariantCulture));
                lstIdentity.AddRange(objCopy.CCA_lstOtherPaths);
                lstIdentity.Add(objCopy.CCA_stExtract.EXO_uiWriteManifest.ToString(CultureInfo.InvariantCulture));
                lstIdentity.Add(objCopy.CCA_stExtract.EXO_uiSetTimes.ToString(CultureInfo.InvariantCulture));
                lstIdentity.Add(objCopy.CCA_stExtract.EXO_uiFollowLinks.ToString(CultureInfo.InvariantCulture));
                lstIdentity.Add(objCopy.CCA_stImport.IMO_uiFileMode.ToString(CultureInfo.InvariantCulture));
                lstIdentity.Add(objCopy.CCA_stImport.IMO_uiDirMode.ToString(CultureInfo.InvariantCulture));
                lstIdentity.Add(objCopy.CCA_stImport.IMO_uiUid.ToString(CultureInfo.InvariantCulture));
                lstIdentity.Add(objCopy.CCA_stImport.IMO_uiGid.ToString(CultureInfo.InvariantCulture));
                lstIdentity.Add(objCopy.CCA_stImport.IMO_uiCopyMtime.ToString(CultureInfo.InvariantCulture));
                lstIdentity.Add(objCopy.CCA_stImport.IMO_uiUseManifest.ToString(CultureInfo.InvariantCulture));

                /* The cut's values: a cut, its clipboard sequence and close     */
                /* count, so a late cut is never taken for a plain copy or one   */
                /* of another clipboard's.                                       */
                lstIdentity.Add(objCopy.CCA_bMoveSources.ToString());
                lstIdentity.Add(objCopy.CCA_uiClipboard.ToString(CultureInfo.InvariantCulture));
                lstIdentity.Add(objCopy.CCA_bAutoNames.ToString());
                lstIdentity.Add(objCopy.CCA_lClipboardLifetime.ToString(CultureInfo.InvariantCulture));
                lstIdentity.Add(objCopy.CCA_lstDirectories.Count.ToString(CultureInfo.InvariantCulture));
                for (i = 0; i < objCopy.CCA_lstDirectories.Count; i++)
                {   /* One extract cut's folder-or-not flag per iteration. */
                    lstIdentity.Add(objCopy.CCA_lstDirectories[i].ToString());
                }
            }
            else if (objRaw != null)
            {   /* A raw copy: both ends, the range, the hash, the words and   */
                /* the GPT box.                                                */
                lstIdentity.Add(objRaw.RWA_sFrom);
                lstIdentity.Add(objRaw.RWA_ullFrom.ToString(CultureInfo.InvariantCulture));
                lstIdentity.Add(objRaw.RWA_sTo);
                lstIdentity.Add(objRaw.RWA_ullTo.ToString(CultureInfo.InvariantCulture));
                lstIdentity.Add(objRaw.RWA_ullLength.ToString(CultureInfo.InvariantCulture));
                lstIdentity.Add(objRaw.RWA_bHash.ToString());
                lstIdentity.Add(objRaw.RWA_sTitle);
                lstIdentity.Add(objRaw.RWA_sCopyText);
                lstIdentity.Add(objRaw.RWA_bThroughGpt.ToString());
            }
            else if (objPartition != null)
            {   /* A table write, format or expansion: the pieces' key (the    */
                /* same ranges with other bytes are another table), then the   */
                /* options, read from the record's own copy.                   */
                lstIdentity.Add(objPartition.PAA_sContentKey);
                stOptions = objPartition.PAA_stOptions;

                /* The grow flag and every format option, the label as Base64. */
                lstIdentity.Add(objPartition.PAA_bGrowTable.ToString());
                lstIdentity.Add(stOptions.MFO_uiFsType.ToString(CultureInfo.InvariantCulture));
                lstIdentity.Add(stOptions.MFO_uiBlockSize.ToString(CultureInfo.InvariantCulture));
                lstIdentity.Add(stOptions.MFO_uiFlags.ToString(CultureInfo.InvariantCulture));
                lstIdentity.Add(stOptions.MFO_ullSizeBytes.ToString(CultureInfo.InvariantCulture));
                lstIdentity.Add(stOptions.MFO_uiOverwrite.ToString(CultureInfo.InvariantCulture));
                lstIdentity.Add(Convert.ToBase64String(stOptions.MFO_arrLabel));

                /* The words: a question about another description is another   */
                /* question.                                                    */
                lstIdentity.Add(objPartition.PAA_sPlace);
                lstIdentity.Add(objPartition.PAA_sTarget);
            }
            else if (objPayload is FMI_clsFormatInput)
            {   /* A format whose options, or a partition Read or Write whose    */
                /* file, is still to be asked (the same payload serves all       */
                /* three): the range's words and the whole-disk flag (the        */
                /* range itself is already the target's part of the identity).   */
                lstIdentity.Add(((FMI_clsFormatInput)objPayload).FMI_bWholeDisk.ToString());
                lstIdentity.Add(((FMI_clsFormatInput)objPayload).FMI_sPlace);
                lstIdentity.Add(((FMI_clsFormatInput)objPayload).FMI_sTarget);
            }
            else if (objPayload is TBE_clsTableEdit)
            {   /* A partition-window button: which button, and the slot it    */
                /* acted on -- a second press of the same button on the same   */
                /* slot while the first waits is the same request.             */
                lstIdentity.Add(((TBE_clsTableEdit)objPayload).TBE_enmAction.ToString());
                lstIdentity.Add(((TBE_clsTableEdit)objPayload).TBE_iSlot.ToString(CultureInfo.InvariantCulture));
            }
            else if (objPayload is NIQ_clsNewImageQuestion)
            {   /* New Image File, its dialog still to be asked: only where    */
                /* the dialog starts -- a second press while the first waits   */
                /* is the same request.  Nothing typed is ever in it here:     */
                /* the fields put back are records the sequence makes for      */
                /* its own questions, never posted.                            */
                lstIdentity.Add(((NIQ_clsNewImageQuestion)objPayload).NIQ_sStartFolder);
            }
            else if (objPayload is RDQ_clsDeviceReadQuestion)
            {   /* Read Device, its device and file still to be asked: only   */
                /* where the save dialog starts -- a second press while the   */
                /* first waits is the same request.  The disks and GPT ends   */
                /* are in the sequence's own record for its question, never   */
                /* in a posted one.                                           */
                lstIdentity.Add(((RDQ_clsDeviceReadQuestion)objPayload).RDQ_sStartFolder);
            }
            else if (objPayload is WDQ_clsDeviceWriteQuestion)
            {   /* Write Device, its image and device still to be asked:   */
                /* only where the file dialog starts -- the image the      */
                /* sequence records later is not part of what was asked.   */
                lstIdentity.Add(((WDQ_clsDeviceWriteQuestion)objPayload).WDQ_sStartFolder);
            }
            else if (objPayload is PIQ_clsPartitionImageQuestion)
            {   /* Partition/Format > Image File, its file still to be asked:   */
                /* only where the open dialog starts -- a second press while    */
                /* the first waits is the same request, and the file the        */
                /* sequence records later is not part of what was asked.        */
                lstIdentity.Add(((PIQ_clsPartitionImageQuestion)objPayload).PIQ_sStartFolder);
            }
            else if (objPartitionOpen != null)
            {   /* The partition window: its caption, the kind choice,      */
                /* whether its disk is still to be found, and a restart's   */
                /* chosen disk.                                             */
                lstIdentity.Add(objPartitionOpen.POA_sName);
                lstIdentity.Add(objPartitionOpen.POA_bAskKind.ToString());
                lstIdentity.Add(objPartitionOpen.POA_bAskDevice.ToString());
                lstIdentity.Add(objPartitionOpen.POA_sDisk);
            }
            else if (objFolder != null)
            {   /* A Save As dialog's New Folder: its picker and its editor's   */
                /* expected entry and order.                                    */
                lstIdentity.Add(objFolder.PFA_lPicker.ToString(CultureInfo.InvariantCulture));
                lstIdentity.Add(objFolder.PFA_sExpectedEntry);
                lstIdentity.Add(objFolder.PFA_lExpectedOrder.ToString(CultureInfo.InvariantCulture));
            }
            else if (objImageFile != null)
            {   /* Open Image File: Open or Check (a dialog's id made on the   */
                /* way is not part of what is asked).                          */
                lstIdentity.Add(objImageFile.IFA_bCheck.ToString());
            }
            else if (objDeviceOpen != null)
            {   /* Open Device: Open or Check, whether the device is still   */
                /* to be asked, and the model's words.                       */
                lstIdentity.Add(objDeviceOpen.DOA_bCheck.ToString());
                lstIdentity.Add(objDeviceOpen.DOA_bAsk.ToString());
                lstIdentity.Add(objDeviceOpen.DOA_sName);
            }
            else if (objFallback != null)
            {   /* A text fallback: the file's kind, the format and the   */
                /* parser's words.                                        */
                lstIdentity.Add(objFallback.VFA_bHost.ToString());
                lstIdentity.Add(objFallback.VFA_iFormat.ToString(CultureInfo.InvariantCulture));
                lstIdentity.Add(objFallback.VFA_sReason);
            }
            else if (objHost != null)
            {   /* An editor on a Windows file: its path and save order (two   */
                /* writes at one revision are told apart by the order).        */
                lstIdentity.Add(objHost.HBA_sPath);
                lstIdentity.Add(objHost.HBA_lSaveOrder.ToString(CultureInfo.InvariantCulture));
            }
            else if (objInput != null)
            {   /* A picker: what it is for and what it starts with -- not the   */
                /* id made for it, or two clicks would never be equal.           */
                lstIdentity.Add(objInput.INA_enmKind.ToString());
                lstIdentity.Add(objInput.INA_sExpectedEntry);
                lstIdentity.Add(objInput.INA_sInitialFolder);
                lstIdentity.Add(objInput.INA_sInitialName);
                lstIdentity.Add(objInput.INA_lExpectedOrder.ToString(CultureInfo.InvariantCulture));

                /* Properties' facts as shown -- size, inode, type, mode, owner,   */
                /* links, times: the dialog compares the answer with them, so      */
                /* other facts make another request.  The name is the path's.      */
                objProperties = objInput.INA_objEntryProperties;
                if (objProperties != null)
                {   /* Properties: the entry's facts. */
                    lstIdentity.Add(objProperties.EPI_stEntry.EEN_ullSize.ToString(CultureInfo.InvariantCulture));
                    lstIdentity.Add(objProperties.EPI_stEntry.EEN_uiInode.ToString(CultureInfo.InvariantCulture));
                    lstIdentity.Add(objProperties.EPI_stEntry.EEN_uiType.ToString(CultureInfo.InvariantCulture));
                    lstIdentity.Add(objProperties.EPI_stEntry.EEN_uiMode.ToString(CultureInfo.InvariantCulture));
                    lstIdentity.Add(objProperties.EPI_stEntry.EEN_uiUid.ToString(CultureInfo.InvariantCulture));
                    lstIdentity.Add(objProperties.EPI_stEntry.EEN_uiGid.ToString(CultureInfo.InvariantCulture));
                    lstIdentity.Add(objProperties.EPI_stEntry.EEN_uiLinks.ToString(CultureInfo.InvariantCulture));
                    lstIdentity.Add(objProperties.EPI_stEntry.EEN_uiAtime.ToString(CultureInfo.InvariantCulture));
                    lstIdentity.Add(objProperties.EPI_stEntry.EEN_uiMtime.ToString(CultureInfo.InvariantCulture));
                    lstIdentity.Add(objProperties.EPI_stEntry.EEN_uiCtime.ToString(CultureInfo.InvariantCulture));
                }
            }
            else
            {   /* A record of no known kind: refused, so nothing unknown can   */
                /* travel in a request.                                         */
                throw new ArgumentException("Unsupported command payload type.");
            }
        }

        /*--------------------------------------------------------------------*/
        /* Cfc_Validate:                                                      */
        /*                                                                    */
        /* Check a request's shape before it is made: the kind's argument     */
        /* record, a target where one is needed, the paths' count and that    */
        /* none is empty, and each record's own rules.  Whether it may RUN    */
        /* is the worker's gate's question, not this one's.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     enmKind    : the kind.                                         */
        /*     objTarget  : the target, or null.                              */
        /*     arrPaths   : the paths, or null.                               */
        /*     objPayload : the argument record, or null.                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the shape is good; else an ArgumentException, before    */
        /*            anything is handed over.                                */
        /*--------------------------------------------------------------------*/
        private static void Cfc_Validate(KND_enmKind enmKind, TGT_clsTarget objTarget,
            string[] arrPaths, object objPayload)
        {
            bool bValid;
            int iPaths;
            EDA_clsEditArgs objEdit;
            CCA_clsCopyArgs objCopy;
            int i;
            INA_clsInputArgs objInput;

            if (!Enum.IsDefined(typeof(KND_enmKind), enmKind))
            {   /* A number that is no kind: refused. */
                throw new ArgumentException("Unsupported typed command kind.");
            }

            /* The record the kind may carry: by default none; the kinds below   */
            /* each take their own (any other record is refused, so a save's     */
            /* stage cannot ride on, say, a read).                               */
            bValid = objPayload == null;
            switch (enmKind)
            {   /* The kind. */
                case KND_enmKind.Exit:
                    /* None, or a restart's arguments. */
                    bValid = objPayload == null || objPayload is ELA_clsElevationArgs;
                    break;
                case KND_enmKind.Edit:
                    /* An edit's (checked further below). */
                    bValid = objPayload is EDA_clsEditArgs;
                    break;
                case KND_enmKind.OpenImageFile:
                    /* Open Image File's (Open or Check). */
                    bValid = objPayload is IFA_clsImageFileArgs;
                    break;
                case KND_enmKind.OpenDevice:
                    /* Open Device's (Open or Check, the model's words). */
                    bValid = objPayload is DOA_clsDeviceOpenArgs;
                    break;
                case KND_enmKind.OpenPartition:
                    /* None (the caption then comes from the target), the       */
                    /* window's own caption, or -- with no target -- the open   */
                    /* dialog's start when the sequence is to ask the disk      */
                    /* image itself.                                            */
                    bValid = objPayload == null || objPayload is POA_clsPartitionOpenArgs ||
                        objPayload is PIQ_clsPartitionImageQuestion;
                    break;
                case KND_enmKind.Extract:
                case KND_enmKind.Import:
                case KND_enmKind.CopyWithin:
                case KND_enmKind.DragExtract:
                    /* A copy's (checked further below). */
                    bValid = objPayload is CCA_clsCopyArgs;
                    break;
                case KND_enmKind.SaveEditor:
                    /* A save's. */
                    bValid = objPayload is SVA_clsSaveArgs;
                    break;
                case KND_enmKind.EditorMakeFolder:
                    /* A Save As dialog's New Folder's. */
                    bValid = objPayload is PFA_clsPickerFolderArgs;
                    break;
                case KND_enmKind.OpenInput:
                    /* A picker's. */
                    bValid = objPayload is INA_clsInputArgs;
                    break;
                case KND_enmKind.ViewerFallback:
                    /* A text fallback's. */
                    bValid = objPayload is VFA_clsViewerFallbackArgs;
                    break;
                case KND_enmKind.RegisterHostEditor:
                case KND_enmKind.HostEditorSaved:
                    /* An editor on a Windows file: facts already true (one   */
                    /* opened, one written), not writes to come.              */
                    bValid = objPayload is HBA_clsHostBindingArgs;
                    break;
                case KND_enmKind.ReadDevice:
                    /* A raw copy's, or -- with no target -- the save           */
                    /* dialog's start when the sequence is to ask the device,   */
                    /* the range and the file itself.                           */
                    bValid = objPayload is RWA_clsRawArgs || objPayload is RDQ_clsDeviceReadQuestion;
                    break;
                case KND_enmKind.WriteDevice:
                    /* A raw copy's, or -- with no target -- the file         */
                    /* dialog's start when the sequence is to ask the image   */
                    /* and the device itself.                                 */
                    bValid = objPayload is RWA_clsRawArgs || objPayload is WDQ_clsDeviceWriteQuestion;
                    break;
                case KND_enmKind.WriteTable:
                case KND_enmKind.ExpandFilesystem:
                    /* The table's and the expansion's. */
                    bValid = objPayload is PAA_clsPartitionArgs;
                    break;
                case KND_enmKind.MakeFilesystem:
                    /* A format's: its arguments, or the range's words when   */
                    /* the sequence is to ask for the options itself.         */
                    bValid = objPayload is PAA_clsPartitionArgs || objPayload is FMI_clsFormatInput;
                    break;
                case KND_enmKind.ReadPartition:
                case KND_enmKind.WritePartition:
                    /* A partition read or write names its Windows file, or    */
                    /* carries the range's words when its sequence is to ask   */
                    /* for the file itself.                                    */
                    bValid = (objPayload is PRA_clsPartitionRawArgs &&
                        ((PRA_clsPartitionRawArgs)objPayload).PRA_sFile.Length != 0) ||
                        objPayload is FMI_clsFormatInput;
                    break;
                case KND_enmKind.ErasePartition:
                    /* An erase has the partition only. */
                    bValid = objPayload is PRA_clsPartitionRawArgs;
                    break;
                case KND_enmKind.EditTable:
                    /* A partition-window button's press, with its table copy. */
                    bValid = objPayload is TBE_clsTableEdit;
                    break;
                case KND_enmKind.NewImage:
                    /* None (the file and size already chosen, in the          */
                    /* target), or the question's start when the sequence is   */
                    /* to ask for them itself.                                 */
                    bValid = objPayload == null || objPayload is NIQ_clsNewImageQuestion;
                    break;
            }

            if (!bValid)
            {   /* The wrong record (or one where none belongs): refused. */
                throw new ArgumentException("The command kind and immutable payload do not match.");
            }

            if (objTarget == null && enmKind != KND_enmKind.Exit && enmKind != KND_enmKind.Cancel &&
                enmKind != KND_enmKind.Close && enmKind != KND_enmKind.SetVerify &&
                enmKind != KND_enmKind.MakeWritable && enmKind != KND_enmKind.MakeReadOnly &&
                enmKind != KND_enmKind.SetRiskWarnings && enmKind != KND_enmKind.ListDevices &&
                enmKind != KND_enmKind.DragEnded && enmKind != KND_enmKind.InputClosed &&
                enmKind != KND_enmKind.EditorClosed && enmKind != KND_enmKind.EditorReady &&
                enmKind != KND_enmKind.RegisterHostEditor && enmKind != KND_enmKind.HostEditorSaved &&
                enmKind != KND_enmKind.OpenInput && enmKind != KND_enmKind.ListFolder &&
                !(enmKind == KND_enmKind.NewImage && objPayload is NIQ_clsNewImageQuestion) &&
                !(enmKind == KND_enmKind.OpenDevice && objPayload is DOA_clsDeviceOpenArgs &&
                ((DOA_clsDeviceOpenArgs)objPayload).DOA_bAsk) &&
                !(enmKind == KND_enmKind.OpenPartition && objPayload is POA_clsPartitionOpenArgs &&
                ((POA_clsPartitionOpenArgs)objPayload).POA_bAskDevice) &&
                !(enmKind == KND_enmKind.OpenPartition && objPayload is PIQ_clsPartitionImageQuestion) &&
                !(enmKind == KND_enmKind.ReadDevice && objPayload is RDQ_clsDeviceReadQuestion) &&
                !(enmKind == KND_enmKind.WriteDevice && objPayload is WDQ_clsDeviceWriteQuestion))
            {   /* No target where one is needed: refused.  The kinds above     */
                /* need none -- they act on no file, or (an access change, a    */
                /* listing) may be posted with nothing open and are refused     */
                /* in the ordinary way by their state gate -- and a NewImage    */
                /* needs none only while its sequence is to ask the file        */
                /* itself (its question's start as the payload), an             */
                /* OpenDevice only while its sequence is to ask the device      */
                /* (its DOA_bAsk), an OpenPartition only while its sequence     */
                /* is to ask the disk (its POA_bAskDevice) or the disk image    */
                /* (its open dialog's start as the payload), and a ReadDevice   */
                /* or a WriteDevice only while its sequence is to ask the       */
                /* device and the file (its file dialog's start as the          */
                /* payload).                                                    */
                throw new ArgumentException("This command needs a complete target.");
            }

            /* Open Image File reads a whole Windows file: no range, no device   */
            /* description, no \\.\ name -- those go by Open Device or a range   */
            /* command.                                                          */
            if (enmKind == KND_enmKind.OpenImageFile && (objTarget.TGT_ullOffset != 0 ||
                objTarget.TGT_ullLength != 0 || objTarget.TGT_sDeviceText.Length != 0 ||
                objTarget.TGT_sPath.StartsWith(@"\\.\", StringComparison.Ordinal)))
            {   /* Not a whole file: refused. */
                throw new ArgumentException("Image inspection needs a whole Windows image file.");
            }

            /* Open Device's target is tested only once the device is chosen.    */
            /* A request whose sequence is still to ask it (DOA_bAsk) has no     */
            /* device yet: its target is null with nothing open, or the view's   */
            /* open image, and neither is the device -- so it is passed over     */
            /* here, and the sequence sets the chosen disk as the target and     */
            /* makes this same test on it then (Sys_DeviceOpenSequence), as      */
            /* the worker's gate also leaves it to.                              */
            if (enmKind == KND_enmKind.OpenDevice &&
                !(objPayload is DOA_clsDeviceOpenArgs && ((DOA_clsDeviceOpenArgs)objPayload).DOA_bAsk) &&
                (objTarget.TGT_ullOffset != 0 ||
                objTarget.TGT_ullLength != 0 || objTarget.TGT_ullDiskBytes == 0 ||
                !objTarget.TGT_sPath.StartsWith(DEV_clsDisk.DEV_PHYSICAL_PREFIX, StringComparison.OrdinalIgnoreCase)))
            {   /* Open Device takes a whole physical disk (\\.\PhysicalDrive   */
                /* N) with its size known: anything else is refused.            */
                throw new ArgumentException("Device inspection needs a whole selected physical device.");
            }

            /* The paths: counted, and none empty (an empty path would read as   */
            /* the root, or widen a delete).  Nothing here needs a session.      */
            iPaths = 0;
            if (arrPaths != null)
            {   /* Paths given: each checked. */
                iPaths = arrPaths.Length;
                for (i = 0; i < arrPaths.Length; i++)
                {   /* One path per iteration. */
                    if (String.IsNullOrEmpty(arrPaths[i]))
                    {   /* Empty: refused. */
                        throw new ArgumentException("A selected path is empty.");
                    }
                }
            }

            if ((enmKind == KND_enmKind.OpenEntry || enmKind == KND_enmKind.ReadLink ||
                enmKind == KND_enmKind.ListFolder || enmKind == KND_enmKind.PickerList ||
                enmKind == KND_enmKind.EditorMakeFolder || enmKind == KND_enmKind.ViewerFallback) && iPaths != 1)
            {   /* These act on exactly one path: refused otherwise. */
                throw new ArgumentException("This command needs exactly one path.");
            }

            if (enmKind == KND_enmKind.PickerBranch && iPaths == 0)
            {   /* A picker's start-up listings need at least the root. */
                throw new ArgumentException("A picker branch needs at least its root path.");
            }

            /* A picker's own rules: Properties of an entry needs that one   */
            /* entry and its facts; Filesystem Properties names no entry.    */
            /* Whether the filesystem may be read is still the gate's.       */
            objInput = objPayload as INA_clsInputArgs;
            if (objInput != null)
            {   /* A picker's arguments (matched to OpenInput above). */
                if (objInput.INA_enmKind == INK_enmInputKind.EntryProperties &&
                    (iPaths != 1 || objInput.INA_objEntryProperties == null))
                {   /* Entry Properties without its one entry or its facts:   */
                    /* refused.                                               */
                    throw new ArgumentException("Entry Properties needs one captured entry path and listing value.");
                }

                if (objInput.INA_enmKind == INK_enmInputKind.FilesystemProperties && iPaths != 0)
                {   /* Filesystem Properties with paths: refused. */
                    throw new ArgumentException("Filesystem Properties takes no entry paths.");
                }
            }

            /* A copy's rules, checked whole now, so a mismatch is never found   */
            /* after part of the copy has been done.                             */
            objCopy = objPayload as CCA_clsCopyArgs;
            if (objCopy != null)
            {   /* A copy's arguments. */
                if (iPaths == 0 || objCopy.CCA_lstOtherPaths.Count != iPaths)
                {   /* No items, or not one other end per item: refused. */
                    throw new ArgumentException("Copy selection lengths differ or are empty.");
                }

                if ((objCopy.CCA_bMoveSources && enmKind == KND_enmKind.DragExtract) ||
                    objCopy.CCA_lClipboardLifetime < -1 ||
                    (objCopy.CCA_lClipboardLifetime >= 0 && enmKind != KND_enmKind.Extract &&
                    enmKind != KND_enmKind.CopyWithin) ||
                    (objCopy.CCA_bAutoNames && (enmKind != KND_enmKind.CopyWithin || objCopy.CCA_bMoveSources)) ||
                    (objCopy.CCA_uiClipboard != 0 && !objCopy.CCA_bMoveSources) ||
                    (enmKind == KND_enmKind.Extract && objCopy.CCA_bMoveSources &&
                    objCopy.CCA_lstDirectories.Count != iPaths) ||
                    (!(enmKind == KND_enmKind.Extract && objCopy.CCA_bMoveSources) &&
                    objCopy.CCA_lstDirectories.Count != 0))
                {   /* A combination that cannot be: a drag-out that cuts; a      */
                    /* close count where no image clipboard is involved (only     */
                    /* an extract or a copy within comes from the image) or       */
                    /* below -1; free names other than for a plain copy within;   */
                    /* a clipboard to clear without a cut; an extract cut         */
                    /* without one flag per item, or flags on anything else.      */
                    throw new ArgumentException("The copy/cut policy and captured source metadata do not match.");
                }

                /* Every other end given (a full count with an empty one is   */
                /* still no destination).                                     */
                for (i = 0; i < objCopy.CCA_lstOtherPaths.Count; i++)
                {   /* One other end per iteration. */
                    if (String.IsNullOrEmpty(objCopy.CCA_lstOtherPaths[i]))
                    {   /* Empty: refused. */
                        throw new ArgumentException("A copy destination is empty.");
                    }
                }
            }

            /* An edit's rules, each kind its own combination of paths, lists   */
            /* and stage -- checked whole before any list is indexed or any     */
            /* stage handed over.                                               */
            objEdit = objPayload as EDA_clsEditArgs;
            if (objEdit != null)
            {   /* An edit's arguments. */
                if (!Enum.IsDefined(typeof(EDK_enmEditKind), objEdit.EDA_enmKind) || iPaths == 0 ||
                    (objEdit.EDA_enmKind != EDK_enmEditKind.Move &&
                    objEdit.EDA_enmKind != EDK_enmEditKind.Delete && iPaths != 1) ||
                    (objEdit.EDA_enmKind == EDK_enmEditKind.Move && objEdit.EDA_lstDestinations.Count != iPaths) ||
                    (objEdit.EDA_enmKind == EDK_enmEditKind.Rename && objEdit.EDA_lstDestinations.Count != 1) ||
                    ((objEdit.EDA_enmKind == EDK_enmEditKind.NameDirectory ||
                    objEdit.EDA_enmKind == EDK_enmEditKind.NameEntry) &&
                    (objEdit.EDA_lstDestinations.Count != 0 || objEdit.EDA_lstDirectories.Count != 0)) ||
                    (objEdit.EDA_enmKind == EDK_enmEditKind.Delete && objEdit.EDA_lstDirectories.Count != iPaths) ||
                    ((objEdit.EDA_enmKind == EDK_enmEditKind.CreateFile) != (objEdit.EDA_objStage != null)) ||
                    (objEdit.EDA_enmKind != EDK_enmEditKind.CreateFile &&
                    (objEdit.EDA_iOpenHow != -1 || objEdit.EDA_bNewDocument)) ||
                    (objEdit.EDA_bNewDocument && objEdit.EDA_iOpenHow < 100))
                {   /* Refused: an unknown kind; no paths; more than one path    */
                    /* except for a Move or Delete; a Move without one           */
                    /* destination per item, a Rename without exactly one;       */
                    /* a naming edit with lists; a Delete without one flag per   */
                    /* item; a stage on anything but CreateFile, or none on      */
                    /* it; a viewer on anything but CreateFile; a new document   */
                    /* not in the structured editor (VWH_EDITOR, 100, plus a     */
                    /* format).                                                  */
                    throw new ArgumentException("Incomplete mutation selection or content.");
                }

                /* Every destination given, extra ones included. */
                for (i = 0; i < objEdit.EDA_lstDestinations.Count; i++)
                {   /* One destination per iteration. */
                    if (String.IsNullOrEmpty(objEdit.EDA_lstDestinations[i]))
                    {   /* Empty: refused. */
                        throw new ArgumentException("A mutation destination is empty.");
                    }
                }
            }
        }
    }
}
