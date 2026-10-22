/*----------------------------------------------------------------------------*/
/* OpenScan.cs                                                                */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* What an editor knows about the file it holds, and how the program decides  */
/* how to open a file.  Three parts:                                          */
/*                                                                            */
/* - OFD_clsOpenedFile, the descriptor every editor (text, hex, structured,   */
/*   picture) is given: where the file lives now -- an entry in an image or a */
/*   Windows file -- and the command facts its saves carry (the editor's id,  */
/*   the range and its epoch, the save order).  ORG_clsOrigin keeps where it  */
/*   was OPENED from, which a Save As to another filesystem does not change.  */
/*   The descriptor holds no engine state and opens nothing; the worker does  */
/*   the work (CommandEditorCoordinator.cs posts it).                         */
/* - OSC_clsScanner, the scans that choose the window: text, picture or       */
/*   binary from the file's first bytes, and which structured format (JSON or */
/*   a printer config) a file is, from its name and, where the name cannot    */
/*   say, its content.  They keep no state and look at nothing but the bytes  */
/*   and the name they are given.                                             */
/* - BNY_clsBinaryForm, the question asked before a file that does not look   */
/*   like text is opened.                                                     */
/*----------------------------------------------------------------------------*/

/* The framework: the base types (Math, Array, string comparison, the           */
/* exceptions caught), the dialog's font and sizes, the file reads and paths,   */
/* and the dialog's form, controls and text measuring.  The debug log           */
/* (gui\lib\DebugLog.cs), which records a file that cannot be read, is in       */
/* this program's own namespace.                                                */
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* OFD_SaveDone:                                                          */
    /*                                                                        */
    /* An editor's answer for one save: called once, on the UI thread, when   */
    /* the save it was given with has ended (CommandEditorCoordinator.cs      */
    /* keeps it per save, SVP_pfnDone).  What the editor does while it waits  */
    /* is the editor's own.                                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bSaved : true when the content is saved; false when the save was   */
    /*              refused or failed.                                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the editor knows how its save ended.                        */
    /*------------------------------------------------------------------------*/
    internal delegate void OFD_SaveDone(bool bSaved);
    /*------------------------------------------------------------------------*/
    /* OFD_SaveRequest:                                                       */
    /*                                                                        */
    /* How an editor hands over a save: the main window's Main_SaveOpenedFile */
    /* (MainForm.cs), which passes it to the editor coordinator (EUI_Save).   */
    /* The editors call it and never reach the engine themselves.             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objFile : the save's own descriptor, captured by OFD_ForSave.      */
    /*     pfnDone : the editor's answer for this save, called once.          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the save is handed over; its end comes through pfnDone.     */
    /*------------------------------------------------------------------------*/
    internal delegate void OFD_SaveRequest(OFD_clsOpenedFile objFile, OFD_SaveDone pfnDone);
    /*-------------------------------------------------------------------------*/
    /* OFD_TargetRequest:                                                      */
    /*                                                                         */
    /* How Save As asks for a destination: a request to the worker for the     */
    /* image picker or the Windows file dialog, held in the descriptor         */
    /* (OFD_pfnPickTarget) and called by the Save As menu (EditorSaveMenu.cs). */
    /* It writes nothing; the chosen destination comes back through pfnChosen  */
    /* and the editor then saves to it.                                        */
    /*                                                                         */
    /* Arguments:                                                              */
    /*     frmOwner   : the editor the picker is shown over.                   */
    /*     objCurrent : the editor's descriptor.                               */
    /*     bWindows   : true for a Windows file, false for a place in the      */
    /*                  image.                                                 */
    /*     pfnChosen  : given the destination's descriptor once one is         */
    /*                  chosen.                                                */
    /*                                                                         */
    /* Returns:                                                                */
    /*     void : the request is posted.                                       */
    /*-------------------------------------------------------------------------*/
    internal delegate void OFD_TargetRequest(Form frmOwner, OFD_clsOpenedFile objCurrent,
        bool bWindows, TIN_TargetChosen pfnChosen);
    /*------------------------------------------------------------------------*/
    /* OFD_ImageAvailable:                                                    */
    /*                                                                        */
    /* Whether an image is open, for the Save As menu to offer "in the image" */
    /* (EditorSaveMenu.cs).  It only sets the menu; the worker still checks   */
    /* the image when the save is made.                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when an image is open.                                 */
    /*------------------------------------------------------------------------*/
    internal delegate bool OFD_ImageAvailable();

    /*------------------------------------------------------------------------*/
    /* OFD_UserFileAllowed:                                                   */
    /*                                                                        */
    /* The test an editor's Save and Save As make before they start: may      */
    /* fresh user work begin now?  It is the editor coordinator's             */
    /* Eui_CanRequestUserFile (CommandEditorCoordinator.cs), the panes' own   */
    /* test.  A save made in answer to a close question does not ask it.      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when the save may start.                               */
    /*------------------------------------------------------------------------*/
    internal delegate bool OFD_UserFileAllowed();

    /*------------------------------------------------------------------------*/
    /* ORG_clsOrigin -- where an editor's file was OPENED from.               */
    /*                                                                        */
    /* Taken once, when the editor is registered (OFD_CaptureOrigin), and     */
    /* never changed after: a Save As can move the descriptor to another      */
    /* filesystem, but the editor's baseline -- the read-out copy, or the     */
    /* Windows file itself -- still belongs to where it was opened.  Every    */
    /* descriptor made from the editor's shares this one record.  Its users:  */
    /* the editor coordinator registers a Windows-file editor and reports the */
    /* editor's end by it (CommandEditorCoordinator.cs), and Save As refuses  */
    /* the baseline and the original image as destinations                    */
    /* (EditorSaveMenu.cs).  It holds paths only, and owns no file.           */
    /*------------------------------------------------------------------------*/
    internal sealed class ORG_clsOrigin
    {
        /* Where it was opened -- documentation table. */
        internal bool ORG_bHost;               // true when opened on a Windows file; decides the Windows-set registration
        internal TGT_clsTarget ORG_objTarget;  // the image (or device) range it was opened from; null for a Windows file
        internal long ORG_lEpoch;              // that image's epoch at the open; 0 for a Windows file
        internal string ORG_sBaseline;         // the editor's baseline: the read-out copy of an image entry, or the Windows file itself

        /*--------------------------------------------------------------------*/
        /* ORG_clsOrigin (constructor):                                       */
        /*                                                                    */
        /* Take the opening facts from the descriptor as it stands at         */
        /* registration, before any Save As can change it.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objFile : the editor's descriptor, its command facts already   */
        /*               set.                                                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the record, never changed afterwards.          */
        /*--------------------------------------------------------------------*/
        internal ORG_clsOrigin(OFD_clsOpenedFile objFile)
        {
            /* Which side it was opened on, and its baseline's path.  Only   */
            /* the path is kept: the file stays the editor's (a Windows      */
            /* file) or the worker's (an image entry's read-out), so this    */
            /* record is no second owner of it.  No range or epoch until     */
            /* the test below says it came from an image.                    */
            ORG_bHost = objFile.OFD_bHostFile;
            ORG_sBaseline = objFile.OFD_sTempFile;
            ORG_objTarget = null;
            ORG_lEpoch = 0;
            if (!objFile.OFD_bHostFile)
            {   /* Opened from an image: its range and epoch, which the      */
                /* editor's end is reported by even after a Save As has      */
                /* moved it elsewhere.  The descriptor knows neither the     */
                /* disk's size nor its sector size, so 0 and 512 stand in;   */
                /* whether the worker reads them for that report is not      */
                /* established here.                                         */
                ORG_objTarget = new TGT_clsTarget(objFile.OFD_sImageFile,
                    objFile.OFD_ullOffset, objFile.OFD_ullLength, objFile.OFD_sDeviceText, 0, 512);
                ORG_lEpoch = objFile.OFD_lEpoch;
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* OFD_clsOpenedFile -- what an editor knows about the file it holds.     */
    /*                                                                        */
    /* An editor is given one when it opens and keeps it as its LIVE          */
    /* descriptor: where the file is now, and the command facts every save    */
    /* of it carries.  A save does not hand over the live one but a copy      */
    /* (OFD_ForSave), so a later Save As cannot change a save already on its  */
    /* way.  A Save As builds a fresh descriptor for the destination and      */
    /* copies the command facts into it (OFD_CopyCommandContextTo); it        */
    /* becomes the editor's binding only once a save to it has succeeded      */
    /* (CommandEditorCoordinator.cs, Eui_ApplySavedBinding).  It holds no     */
    /* engine state.                                                          */
    /*------------------------------------------------------------------------*/
    internal sealed class OFD_clsOpenedFile
    {
        /* Where the file is now -- documentation table. */
        public bool OFD_bHostFile;     // true when it is a Windows file; a Save As to the other side sets it in the destination's descriptor
        public string OFD_sImageFile;  // the image file or device holding the entry; "" for a Windows file
        public string OFD_sImagePath;  // the entry's path in the image, or the Windows file's full path
        public string OFD_sName;       // the name shown in captions and questions (the last component)
        public string OFD_sTempFile;   // the Windows file the editor reads and writes: an entry's read-out copy, or the Windows file itself

        /* The command facts a save carries -- documentation table.  None is   */
        /* engine state; the worker matches and checks each one.               */
        internal long OFD_lEditorId;                          // the editor's id, the same for every save and Save As (the worker's for an entry, MainCommand.cs; the client's for a Windows file)
        internal long OFD_lEpoch;                             // the open image's epoch for the entry's range; 0 for a Windows file; a reopen of the same range keeps it
        internal ulong OFD_ullOffset;                         // the filesystem's start in the image or device, bytes
        internal ulong OFD_ullLength;                         // the filesystem's length, bytes; 0 for "to the end"
        internal string OFD_sDeviceText;                      // a device's description for display; "" for a file
        internal string OFD_sStageRoot;                       // the private folder save snapshots are written in; Save As refuses any path in it (EditorSaveMenu.cs)
        internal long OFD_lRevision;                          // the content revision this save holds (OFD_ForSave)
        internal long OFD_lSaveOrder;                         // the save's place in the editor's order: an older save answered late cannot move the destination back
        internal string OFD_sExpectedEntry;                   // the entry this save must still find bound to the editor; null means "the current path"
        internal STG_clsStage OFD_objStage;                   // the save's snapshot of the content, handed to the worker with it; null before it is written
        internal PRS_clsPresentation OFD_objQuestion;         // the close question this save answers (OFD_ForSave); null for an ordinary save
        internal bool OFD_bDetached;                          // true while the entry's image is not open: an ordinary Save to the image is refused
        internal ORG_clsOrigin OFD_objOriginal;               // where it was opened from, shared by every descriptor made from it; null until registration
        internal OFD_TargetRequest OFD_pfnPickTarget;         // Save As's request for a destination; set at registration
        internal OFD_ImageAvailable OFD_pfnImageAvailable;    // whether an image is open, for the Save As menu only; set at registration
        internal OFD_UserFileAllowed OFD_pfnUserFileAllowed;  // may a Save or Save As start now; null for an editor not registered

        /* What the descriptor is asked: may a save start, the copies a save   */
        /* and a Save As take, and the caption.                                */

        /*--------------------------------------------------------------------*/
        /* OFD_CanRequestUserFile:                                            */
        /*                                                                    */
        /* May an ordinary Save or Save As start now?  The editors ask it     */
        /* first, and refuse the click when the answer is no.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : the registered test's answer (false while the worker is */
        /*            busy, exiting or holding a picker); true for an editor  */
        /*            with no test set.                                       */
        /*--------------------------------------------------------------------*/
        internal bool OFD_CanRequestUserFile()
        {
            if (OFD_pfnUserFileAllowed == null)
            {   /* No test set: an editor not registered with the command   */
                /* system has nothing to ask, so its save may start.  The   */
                /* coordinator sets the test when it registers an editor    */
                /* (CommandEditorCoordinator.cs).                           */
                return(true);
            }

            /* The panes' own test, so an editor starts no save that a pane   */
            /* could not start either.  A save made in answer to a close      */
            /* question does not come here: the editor skips the test when    */
            /* such a question is showing.                                    */
            return(OFD_pfnUserFileAllowed());
        }

        /*--------------------------------------------------------------------*/
        /* OFD_CopyCommandContextTo:                                          */
        /*                                                                    */
        /* Carry this editor's command facts into a descriptor made for       */
        /* another destination -- a Save As target the picker answered with   */
        /* (MainCommand.cs), or a save's own copy (OFD_ForSave) -- so the     */
        /* save there is still this editor's.  The caller has set the         */
        /* destination's place (the side, image, path, name, work file), and  */
        /* sets the range and epoch again afterwards when the target is in    */
        /* another filesystem.  The snapshot and the close question are not   */
        /* copied: each save takes its own.                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objDestination : the new descriptor; not the editor's binding  */
        /*                      yet.                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the destination carries the editor's id, origin, range, */
        /*            revision, save order and the entry it must still find.  */
        /*--------------------------------------------------------------------*/
        internal void OFD_CopyCommandContextTo(OFD_clsOpenedFile objDestination)
        {
            /* The opening record taken first, if it has not been, so it       */
            /* describes where the editor was opened and not the destination   */
            /* about to be written; then shared, with the three tests set at   */
            /* registration.  Sharing the record owns no file.                 */
            OFD_CaptureOrigin();
            objDestination.OFD_objOriginal = OFD_objOriginal;
            objDestination.OFD_pfnPickTarget = OFD_pfnPickTarget;
            objDestination.OFD_pfnImageAvailable = OFD_pfnImageAvailable;
            objDestination.OFD_pfnUserFileAllowed = OFD_pfnUserFileAllowed;

            /* The command facts as THIS descriptor holds them -- never read   */
            /* again from the main window, whose open image or selection may   */
            /* have changed while the picker was up.                           */
            objDestination.OFD_lEditorId = OFD_lEditorId;
            objDestination.OFD_lEpoch = OFD_lEpoch;
            objDestination.OFD_ullOffset = OFD_ullOffset;
            objDestination.OFD_ullLength = OFD_ullLength;
            objDestination.OFD_sDeviceText = OFD_sDeviceText;
            objDestination.OFD_sStageRoot = OFD_sStageRoot;
            objDestination.OFD_lRevision = OFD_lRevision;
            objDestination.OFD_lSaveOrder = OFD_lSaveOrder;

            /* The save there must still find the entry the editor is bound   */
            /* to now: choosing a destination does not move the editor, and   */
            /* the save's request names that entry for the worker to check    */
            /* (CommandEditorCoordinator.cs).  The image-closed state goes    */
            /* with it.                                                       */
            objDestination.OFD_sExpectedEntry = OFD_sImagePath;
            objDestination.OFD_bDetached = OFD_bDetached;
        }

        /*--------------------------------------------------------------------*/
        /* OFD_CaptureOrigin:                                                 */
        /*                                                                    */
        /* Take the opening record (ORG_clsOrigin) the first time it is       */
        /* called, and keep it; every later call leaves it alone.  The        */
        /* coordinator calls it when it registers the editor, before any Save */
        /* As can move the descriptor (CommandEditorCoordinator.cs).          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : OFD_objOriginal is set.                                 */
        /*--------------------------------------------------------------------*/
        internal void OFD_CaptureOrigin()
        {
            if (OFD_objOriginal == null)
            {   /* Not taken yet: taken now, from the descriptor as it stands,   */
                /* and shared by every descriptor copied from this one.          */
                OFD_objOriginal = new ORG_clsOrigin(this);
            }
        }

        /*--------------------------------------------------------------------*/
        /* OFD_ForSave:                                                       */
        /*                                                                    */
        /* The descriptor a save is handed over with: a copy of this one,     */
        /* fixed at the moment of the save, so a later Save As or rebinding   */
        /* of the editor cannot change a save already on its way.  The editor */
        /* writes the content's snapshot into it afterwards (OFD_objStage).   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lRevision : the content revision the save holds.               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     OFD_clsOpenedFile : the save's own descriptor, with the close  */
        /*                         question it answers, if any.               */
        /*--------------------------------------------------------------------*/
        internal OFD_clsOpenedFile OFD_ForSave(long lRevision)
        {
            OFD_clsOpenedFile objSnapshot;

            /* A new descriptor with this one's place, and the command facts   */
            /* copied in.  The strings and the opening record are shared,      */
            /* not duplicated: nothing here copies content or a file.          */
            objSnapshot = new OFD_clsOpenedFile();
            objSnapshot.OFD_bHostFile = OFD_bHostFile;
            objSnapshot.OFD_sImageFile = OFD_sImageFile;
            objSnapshot.OFD_sImagePath = OFD_sImagePath;
            objSnapshot.OFD_sName = OFD_sName;
            objSnapshot.OFD_sTempFile = OFD_sTempFile;
            OFD_CopyCommandContextTo(objSnapshot);
            objSnapshot.OFD_lRevision = lRevision;

            /* The entry the save must still find.  An ordinary save expects   */
            /* the current path; a Save As target already carries the entry    */
            /* the editor was bound to when its picker was asked               */
            /* (OFD_CopyCommandContextTo), and that one is kept.               */
            objSnapshot.OFD_sExpectedEntry = OFD_sImagePath;
            if (OFD_sExpectedEntry != null)
            {   /* Already set by the copy: the binding this save must match. */
                objSnapshot.OFD_sExpectedEntry = OFD_sExpectedEntry;
            }

            /* The close question this save answers, if one is showing for   */
            /* this editor (QWN_CurrentEditorQuestion), taken NOW, before    */
            /* the snapshot is written, so that a question shown later is    */
            /* never taken for the one this save answers.                    */
            objSnapshot.OFD_objQuestion =
                QWN_clsQuestionWindows.QWN_CurrentEditorQuestion(OFD_lEditorId);

            /* The save's descriptor, for the editor to fill and hand over   */
            /* once.                                                         */
            return(objSnapshot);
        }

        /*--------------------------------------------------------------------*/
        /* OFD_Title:                                                         */
        /*                                                                    */
        /* What an editor's caption names: where the file is now, not where   */
        /* it was opened.                                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the Windows file's full path, or "entry in image";    */
        /*              the window adds its own suffix.                       */
        /*--------------------------------------------------------------------*/
        internal string OFD_Title()
        {
            if (OFD_bHostFile)
            {   /* A Windows file: its full path says it all. */
                return(OFD_sImagePath);
            }

            /* An image entry: its path in the image, and the image file's    */
            /* (or device's) name, so two images' same-named entries can be   */
            /* told apart.                                                    */
            return(OFD_sImagePath + " in " + Path.GetFileName(OFD_sImageFile));
        }
    }

    /*------------------------------------------------------------------------*/
    /* OSC_clsScanner -- which window a file opens in.                        */
    /*                                                                        */
    /* Two questions, each answered from what the caller hands over and       */
    /* nothing else: the file's bytes (a local file -- an entry's read-out    */
    /* copy, or a Windows file) and its name and size.  OSC_Classify says     */
    /* text, picture or binary (CommandNativeOperations.cs, MainForm.cs);     */
    /* OSC_IdentifyFormat says which structured editor, if any, takes it      */
    /* (CommandOperations.cs, MainForm.cs).  Both are heuristics that choose  */
    /* a window; neither proves the file is valid for it -- the window can    */
    /* still refuse what it is given.  No state is kept between calls; the    */
    /* tables are fixed.                                                      */
    /*------------------------------------------------------------------------*/
    internal static class OSC_clsScanner
    {
        /* The three answers -- documentation table. */
        internal const int OSC_KIND_TEXT    = 0;    // plain text: opened in the text editor
        internal const int OSC_KIND_BINARY  = 1;    // not text: the user is asked before it is opened
        internal const int OSC_KIND_PICTURE = 2;    // a PNG or JPEG picture: opened in the picture viewer

        /* Limits -- documentation table. */
        private const long OSC_DETECT_MAX_BYTES = 4L * 1024 * 1024;     // the most read to tell a printer config's kind, and the largest file tested as JSON, bytes
        internal const int  OSC_SAMPLE_BYTES      = 64 * 1024;          // how much of the start of a file is examined
        internal const int  OSC_CONTROL_PER_MILLE = 10;                 // more control bytes than this many per thousand sample bytes means binary (1 %)
        internal const long OSC_TEXT_MAX_BYTES    = 16L * 1024 * 1024;  // the largest file the text editor takes; above it only the hex editor is offered
        internal const long OSC_PICTURE_MAX_BYTES = 64L * 1024 * 1024;  // the largest picture the viewer loads; above it the file is treated as binary

        /* What OSC_ConfigKind finds a .cfg or .conf to be -- documentation table. */
        internal const int OSC_CONFIG_NONE      = 0;    // none of the three: plain text, for the text editor
        internal const int OSC_CONFIG_KLIPPER   = 1;    // Klipper's printer.cfg or one of its includes
        internal const int OSC_CONFIG_MOONRAKER = 2;    // Moonraker's moonraker.conf
        internal const int OSC_CONFIG_SCREEN    = 3;    // KlipperScreen's KlipperScreen.conf

        /* The section words each program's files hold, which OSC_ConfigKind    */
        /* counts -- documentation tables.  All lower case, as the section      */
        /* names are lower-cased before they are looked up.  "printer" is in    */
        /* none of them: Klipper and KlipperScreen both use it, told apart by   */
        /* whether a name follows, so OSC_ConfigKind decides it itself.         */
        private static readonly string[] Osc_arrKlipperSections = {                   // Klipper's (printer.cfg and its includes)
            "mcu", "extruder", "heater_bed", "stepper_x", "stepper_y", "stepper_z",   // the machine itself
            "stepper_a", "stepper_b", "stepper_c", "bed_mesh", "probe", "bltouch",    // delta steppers, levelling
            "safe_z_home", "gcode_macro", "virtual_sdcard", "pause_resume",           // homing, macros, printing from a file
            "input_shaper", "tmc2209", "tmc2208", "tmc2130", "tmc5160",               // resonance and stepper drivers
            "heater_fan", "controller_fan", "fan", "idle_timeout", "display_status",  // fans and housekeeping
            "firmware_retraction", "exclude_object", "respond", "save_variables" };   // printing features
        private static readonly string[] Osc_arrMoonrakerSections = {                 // Moonraker's (moonraker.conf)
            "server", "authorization", "file_manager", "machine", "update_manager",   // the server's own core
            "octoprint_compat", "history", "data_store", "announcements",             // optional components
            "job_queue", "database", "webcam", "zeroconf", "secrets" };               // more of them
        private static readonly string[] Osc_arrScreenSections = {                    // KlipperScreen's (KlipperScreen.conf)
            "main", "preheat", "menu" };                                              // KlipperScreen's settings, presets and menus

        /* The signatures looked for at offset 0, and what each is called in   */
        /* the question.  The two tables are read together, index for index,   */
        /* and the first match wins.  The first two are the pictures; every    */
        /* other one means "binary".  So a new signature goes BELOW the two    */
        /* pictures, with its name at the same index in the names table: one   */
        /* added above them would be taken for a picture (OSC_PICTURE_SIGNA-   */
        /* TURES counts from the top), and one added to only one table would   */
        /* give every later signature the next one's name.  The xz, gzip and   */
        /* zstd bytes match the engine's own readers (bfe_compress.c).         */
        private const int OSC_PICTURE_SIGNATURES = 2;    // how many entries, from the top of the tables, are pictures

        /* The signatures, byte for byte, each compared at offset 0 of the   */
        /* file -- documentation table, one format per entry.                */
        private static readonly byte[][] Osc_aabSignatures = // index for index with Osc_arrSignatureNames
        {
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },                                                  // PNG
            new byte[] { 0xFF, 0xD8, 0xFF },                                                                                // JPEG
            new byte[] { 0x7F, 0x45, 0x4C, 0x46 },                                                                          // ELF: 0x7F "ELF"
            new byte[] { 0x4D, 0x5A },                                                                                      // "MZ"
            new byte[] { 0x1F, 0x8B },                                                                                      // gzip
            new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 },                                                              // xz
            new byte[] { 0x42, 0x5A, 0x68 },                                                                                // "BZh"
            new byte[] { 0x28, 0xB5, 0x2F, 0xFD },                                                                          // zstd
            new byte[] { 0x50, 0x4B, 0x03, 0x04 },                                                                          // "PK" 3 4
            new byte[] { 0x21, 0x3C, 0x61, 0x72, 0x63, 0x68, 0x3E, 0x0A },                                                  // "!<arch>" and a line feed
            new byte[] { 0x30, 0x37, 0x30, 0x37, 0x30, 0x31 },                                                              // "070701"
            new byte[] { 0x30, 0x37, 0x30, 0x37, 0x30, 0x32 },                                                              // "070702"
            new byte[] { 0x68, 0x73, 0x71, 0x73 },                                                                          // "hsqs"
            new byte[] { 0x27, 0x05, 0x19, 0x56 },                                                                          // U-Boot legacy image magic
            new byte[] { 0xD0, 0x0D, 0xFE, 0xED },                                                                          // flattened device tree magic
            new byte[] { 0x53, 0x51, 0x4C, 0x69, 0x74, 0x65, 0x20, 0x66, 0x6F, 0x72, 0x6D, 0x61, 0x74, 0x20, 0x33, 0x00 }   // "SQLite format 3" and a NUL
        };

        /* What each signature above is called, at the same index: the words   */
        /* that finish the question's "It looks like ..." -- documentation     */
        /* table.                                                              */
        private static readonly string[] Osc_arrSignatureNames = // index for index with Osc_aabSignatures
        {
            "a PNG picture",                           // shown only when the picture is too large for the viewer
            "a JPEG picture",                          // likewise
            "an ELF executable or library",            // Linux programs, shared libraries, kernel modules
            "a Windows or DOS executable",             // also EFI programs
            "gzip-compressed data",                    // .gz, .tgz, a compressed kernel or initramfs
            "xz-compressed data",                      // .xz
            "bzip2-compressed data",                   // .bz2
            "zstd-compressed data",                    // .zst
            "a ZIP archive",                           // also .jar, .apk and office documents
            "an ar archive",                           // .a static libraries, .deb packages
            "a cpio archive",                          // an initramfs, the new ASCII format
            "a cpio archive",                          // the same with checksums
            "a squashfs filesystem",                   // a compressed read-only filesystem image
            "a U-Boot image",                          // a kernel or ramdisk wrapped for U-Boot
            "a device tree blob",                      // .dtb
            "an SQLite database"                       // .db, .sqlite
        };

        /*--------------------------------------------------------------------*/
        /* OSC_Classify:                                                      */
        /*                                                                    */
        /* Which window a local file first opens in: the text editor, the     */
        /* picture viewer, or the question that comes before a binary file.   */
        /* Only the file's start and its length are looked at, so a picture   */
        /* named here may still be one the viewer cannot decode -- the viewer */
        /* then refuses it.                                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sFile : the file to examine (the temporary copy of an image    */
        /*             entry).                                                */
        /*     sWhy  : receives, for a binary file, the words that finish the */
        /*             sentence "It looks like ..."; "" otherwise.            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : OSC_KIND_TEXT, OSC_KIND_BINARY or OSC_KIND_PICTURE.  A   */
        /*           file that cannot be read throws, as File.OpenRead does.  */
        /*--------------------------------------------------------------------*/
        internal static int OSC_Classify(string sFile, out string sWhy)
        {
            FileStream stmFile;
            byte[] abSample;
            long   lLength;
            int    iRead;
            int    iGot;
            int    iKind;

            /* The sample buffer is the full 64 KiB whatever the file's size;   */
            /* iRead counts how much of it the file fills, and only that much   */
            /* is scanned, so a short file is judged on what it has.            */
            abSample = new byte[OSC_SAMPLE_BYTES];
            iRead    = 0;

            using (stmFile = File.OpenRead(sFile))
            {   /* Read the file's length and as much of its start as the    */
                /* sample holds -- the length decides later whether a        */
                /* picture is too large to show.  The file is closed again   */
                /* when this block ends, a failure included.                 */
                lLength = stmFile.Length;

                for (;;)
                {   /* One read per iteration: a read may return less than was asked for. */
                    iGot = stmFile.Read(abSample, iRead, abSample.Length - iRead);

                    if (iGot <= 0)
                    {   /* The end of the file. */
                        break;
                    }

                    /* Count what arrived; the next read fills on after it. */
                    iRead = iRead + iGot;

                    if (iRead == abSample.Length)
                    {   /* The sample is full. */
                        break;
                    }
                }
            }

            /* The scan, on the bytes actually read (a short file's sample   */
            /* is short).                                                    */
            iKind = OSC_ClassifyBytes(abSample, iRead, out sWhy);

            if (iKind == OSC_KIND_PICTURE && lLength > OSC_PICTURE_MAX_BYTES)
            {   /* A picture, but more than the viewer will load.  Reported as   */
                /* binary so the open asks first and offers the hex editor;      */
                /* sWhy already names the format, so the question reads "It      */
                /* looks like a PNG picture too large to show."  The text        */
                /* editor's own limit (OSC_TEXT_MAX_BYTES) is not applied        */
                /* here: the open applies it to the read-out's size, whatever    */
                /* kind comes back (CommandOperations.cs).                       */
                sWhy = sWhy + " too large to show";
                return(OSC_KIND_BINARY);
            }

            if (iKind == OSC_KIND_PICTURE)
            {   /* A picture the viewer will load: it opens without a      */
                /* question, and this function gives a reason only for     */
                /* binary, so the format's name OSC_ClassifyBytes put in   */
                /* sWhy is cleared.                                        */
                sWhy = "";
            }

            /* The kind the content gave. */
            return(iKind);
        }

        /*--------------------------------------------------------------------*/
        /* OSC_ClassifyBytes:                                                 */
        /*                                                                    */
        /* The verdict on a file's first bytes: a known signature (a picture, */
        /* or a named binary format), a NUL, too many control bytes, or       */
        /* text.  OSC_Classify is its one caller, with at most                */
        /* OSC_SAMPLE_BYTES -- the control-byte test's products are sized for */
        /* that (STEP 3).                                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     abSample : the start of the file.                              */
        /*     iBytes   : how many of abSample's bytes are the file's, from 0 */
        /*                to abSample.Length.                                 */
        /*     sWhy     : receives the words that finish "It looks like ...": */
        /*                for a binary file why it is one, for a picture what */
        /*                it is; "" for text.                                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : OSC_KIND_TEXT, OSC_KIND_BINARY or OSC_KIND_PICTURE.      */
        /*--------------------------------------------------------------------*/
        internal static int OSC_ClassifyBytes(byte[] abSample, int iBytes, out string sWhy)
        {
            bool bMatch;
            int  iControl;
            int  iSignature;
            int  i;

            /* No reason until one is found (text keeps it empty). */
            sWhy = "";

            /* STEP 1: a known signature at offset 0. */
            for (iSignature = 0; iSignature < Osc_aabSignatures.Length; iSignature++)
            {   /* One signature per iteration. */
                if (iBytes < Osc_aabSignatures[iSignature].Length)
                {   /* The file is shorter than this signature, so it cannot     */
                    /* match.  Comparing anyway would read the buffer past the   */
                    /* file's end -- zeros, in OSC_Classify's buffer -- and a    */
                    /* signature ending in NUL (xz, SQLite) would then match a   */
                    /* file that stops just short of it.                         */
                    continue;
                }

                /* A match until a byte differs. */
                bMatch = true;

                for (i = 0; i < Osc_aabSignatures[iSignature].Length; i++)
                {   /* One signature byte per iteration. */
                    if (abSample[i] != Osc_aabSignatures[iSignature][i])
                    {   /* Not this one. */
                        bMatch = false;
                        break;
                    }
                }

                if (!bMatch)
                {   /* Try the next signature. */
                    continue;
                }

                /* Matched: its name from the parallel table -- for a picture   */
                /* too, which OSC_Classify needs if the picture is too large.   */
                sWhy = Osc_arrSignatureNames[iSignature];

                if (iSignature < OSC_PICTURE_SIGNATURES)
                {   /* One of the two picture formats. */
                    return(OSC_KIND_PICTURE);
                }

                /* A named binary format. */
                return(OSC_KIND_BINARY);
            }

            /* STEPS 2 and 3: one pass over the sample, looking for a NUL and   */
            /* counting the other control bytes.                                */
            iControl = 0;

            for (i = 0; i < iBytes; i++)
            {   /* One sample byte per iteration. */
                if (abSample[i] == 0x00)
                {   /* A NUL: binary.  Text in UTF-16 holds them too and is   */
                    /* called binary here, which is right for this program:   */
                    /* the text editor reads UTF-8 or Latin-1 only            */
                    /* (TextEditForm.cs), never UTF-16.  The question still   */
                    /* offers Open as Text when the file is small enough.     */
                    sWhy = "data that is not text (it holds NUL bytes)";
                    return(OSC_KIND_BINARY);
                }

                if (abSample[i] >= 0x20 && abSample[i] != 0x7F)
                {   /* A printable character, or a byte of 0x80 and above --   */
                    /* NOT counted against the file: such bytes are UTF-8 or   */
                    /* an older 8-bit encoding, and the editor reads both.     */
                    continue;
                }

                if (abSample[i] == 0x09 || abSample[i] == 0x0A || abSample[i] == 0x0D || abSample[i] == 0x0C || abSample[i] == 0x1B)
                {   /* Tab, line feed, carriage return, form feed, escape: all   */
                    /* found in text.  Escape is allowed so that a log holding   */
                    /* terminal colour codes still opens as text.                */
                    continue;
                }

                /* Any other control byte (DEL among them) counts against it. */
                iControl++;
            }

            /* STEP 3: more control bytes than OSC_CONTROL_PER_MILLE per     */
            /* thousand, tested in whole numbers (no division, so no         */
            /* rounding).  At OSC_SAMPLE_BYTES the larger product is about   */
            /* 65 million, far inside an int.                                */
            if (iControl * 1000 > iBytes * OSC_CONTROL_PER_MILLE)
            {   /* Too many: binary, said as such. */
                sWhy = "data that is not text (it holds many control characters)";
                return(OSC_KIND_BINARY);
            }

            /* STEP 4: nothing says otherwise, so it is text -- an empty   */
            /* file included, whose sample holds nothing to object to.     */
            return(OSC_KIND_TEXT);
        }

        /*--------------------------------------------------------------------*/
        /* OSC_ConfigKind:                                                    */
        /*                                                                    */
        /* Which printer program a .cfg or .conf belongs to, from its content */
        /* -- the extension alone cannot say.  A program's own save marker    */
        /* decides at once; otherwise the program whose section words are     */
        /* strictly the most wins, and a tie or no known section means plain  */
        /* text.  Only the shape is looked at: the structured editor's own    */
        /* reader can still refuse the file.  Called only from                */
        /* Osc_ConfigFormatFromText.                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     abText : the file's bytes (its start is enough).               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : OSC_CONFIG_KLIPPER, _MOONRAKER, _SCREEN, or _NONE.       */
        /*--------------------------------------------------------------------*/
        internal static int OSC_ConfigKind(byte[] abText)
        {
            string[] arrLines;
            string   sLine;
            string   sSection;
            string   sWord;
            int      iKlipper;
            int      iMoonraker;
            int      iScreen;
            int      iClose;
            int      iSpace;
            int      i;

            /* The text as UTF-8 -- what all three programs read and write --   */
            /* a byte order mark (U+FEFF) dropped from its start, split into    */
            /* lines.  Bytes that are not valid UTF-8 (an 8-bit file, or a      */
            /* character cut in two where the head OSC_IdentifyFormat reads     */
            /* stops) become the replacement character rather than an error     */
            /* -- harmless, since only the ASCII markers and section words      */
            /* below are compared.                                              */
            arrLines = System.Text.Encoding.UTF8.GetString(abText).TrimStart((char)0xFEFF).Split('\n');

            /* No section counted for any of the three yet. */
            iKlipper   = 0;
            iMoonraker = 0;
            iScreen    = 0;

            for (i = 0; i < arrLines.Length; i++)
            {   /* One line per iteration. */

                /* Trimmed, which also drops the carriage return a Windows   */
                /* line ending leaves after the split on line feeds.         */
                sLine = arrLines[i].Trim();

                /* The save markers decide on the spot: each program writes   */
                /* its own when it saves its block into the file ("#*#"       */
                /* Klipper's SAVE_CONFIG, "#~#" KlipperScreen's settings),    */
                /* so the first such line settles it, whatever the section    */
                /* counts so far.                                             */
                if (sLine.StartsWith("#*#", StringComparison.Ordinal))
                {   /* Klipper's SAVE_CONFIG block. */
                    return(OSC_CONFIG_KLIPPER);
                }

                if (sLine.StartsWith("#~#", StringComparison.Ordinal))
                {   /* KlipperScreen's saved settings. */
                    return(OSC_CONFIG_SCREEN);
                }

                if (!sLine.StartsWith("[", StringComparison.Ordinal))
                {   /* Not a section header. */
                    continue;
                }

                /* Where the header closes; the name lies between. */
                iClose = sLine.IndexOf(']');

                if (iClose < 2)
                {   /* "[]" or no closing bracket: not a section. */
                    continue;
                }

                /* The section's name, lower-cased so the tables match it    */
                /* without regard to case, and its first word -- only that   */
                /* counts ("[gcode_macro PARK]" is gcode_macro).             */
                sSection = sLine.Substring(1, iClose - 1).Trim().ToLowerInvariant();
                iSpace   = sSection.IndexOf(' ');
                sWord    = sSection;

                if (iSpace > 0)
                {   /* "[gcode_macro PARK]": the first word names the kind. */
                    sWord = sSection.Substring(0, iSpace);
                }

                if (sWord == "printer")
                {   /* Shared by two programs: Klipper's has no name after it. */
                    if (iSpace > 0)
                    {   /* "[printer Voron]": one of KlipperScreen's printers. */
                        iScreen++;
                    }
                    else
                    {   /* "[printer]": Klipper's kinematics section. */
                        iKlipper++;
                    }

                    /* Counted: on to the next line.  The three tables below      */
                    /* need not be asked -- "printer" is in none of them, which   */
                    /* is why it is decided here.                                 */
                    continue;
                }

                if (Array.IndexOf(Osc_arrKlipperSections, sWord) >= 0)
                {   /* A Klipper section. */
                    iKlipper++;
                }

                if (Array.IndexOf(Osc_arrMoonrakerSections, sWord) >= 0)
                {   /* A Moonraker section. */
                    iMoonraker++;
                }

                if (Array.IndexOf(Osc_arrScreenSections, sWord) >= 0)
                {   /* A KlipperScreen section. */
                    iScreen++;
                }
            }

            /* The verdict: one program strictly ahead of both others wins. */
            if (iKlipper > iMoonraker && iKlipper > iScreen)
            {   /* Mostly Klipper's sections. */
                return(OSC_CONFIG_KLIPPER);
            }

            if (iMoonraker > iKlipper && iMoonraker > iScreen)
            {   /* Mostly Moonraker's. */
                return(OSC_CONFIG_MOONRAKER);
            }

            if (iScreen > iKlipper && iScreen > iMoonraker)
            {   /* Mostly KlipperScreen's. */
                return(OSC_CONFIG_SCREEN);
            }

            /* None known, or a tie: plain text. */
            return(OSC_CONFIG_NONE);
        }

        /* Telling the structured formats apart, for the open. */

        /*--------------------------------------------------------------------*/
        /* OSC_IdentifyFormat:                                                */
        /*                                                                    */
        /* Which structured editor, if any, a text file opens in.  The name   */
        /* decides first (VoorheesFormat.cs); a printer config's extension is */
        /* shared by three programs and by plain text, so its content         */
        /* decides which (OSC_ConfigKind); a file the name says nothing about */
        /* opens structured only if it reads as JSON -- the format module's   */
        /* content test falls back to INI, which takes any text, so that      */
        /* answer is ignored.  A registry file is never opened structured     */
        /* this way; the reason is not recorded.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sPath : the local file: an entry's read-out copy, or the       */
        /*             Windows file.                                          */
        /*     sName : the entry's or file's own name, which the name test    */
        /*             reads.                                                 */
        /*     lSize : its size, bytes.                                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : a VoorheesFormat.VF_* id, or VF_DETECT for plain text.   */
        /*--------------------------------------------------------------------*/
        internal static int OSC_IdentifyFormat(string sPath, string sName, long lSize)
        {
            byte[] abFile;
            int iFormat;

            /* The name first, by the format module's own rules, given as the    */
            /* bare name: an image entry's name may hold '\' and characters      */
            /* no Windows path may (", <, >, |), and the name test takes every   */
            /* character as part of the name, so such a name is judged by its    */
            /* extension like any other.  A registry answer is dropped back to   */
            /* "not known".                                                      */
            iFormat = VoorheesFormat.VF_DetectFromName(sName);
            if (iFormat == VoorheesFormat.VF_REG)
            {   /* A .reg name: not opened structured from here (why is not   */
                /* recorded), so it goes on as a name that says nothing.      */
                iFormat = VoorheesFormat.VF_DETECT;
            }

            if (iFormat == VoorheesFormat.VF_KLIPPER ||
                iFormat == VoorheesFormat.VF_MOONRAKER ||
                iFormat == VoorheesFormat.VF_KLIPPERSCREEN)
            {   /* A printer config's extension: which program's, if any, is    */
                /* read from the file's start (at most OSC_DETECT_MAX_BYTES).   */
                /* A file that cannot be read is answered as plain text, the    */
                /* failure logged.                                              */
                abFile = Osc_ReadHead(sPath, OSC_DETECT_MAX_BYTES);
                if (abFile == null)
                {   /* Not read (logged by Osc_ReadHead): plain text. */
                    return(VoorheesFormat.VF_DETECT);
                }

                /* The content's verdict, as the editor's format id. */
                return(Osc_ConfigFormatFromText(abFile));
            }

            if (iFormat != VoorheesFormat.VF_DETECT)
            {   /* Any other format the name names (a .json, an .ini): that   */
                /* editor.                                                    */
                return(iFormat);
            }

            if (lSize > OSC_DETECT_MAX_BYTES)
            {   /* Too large to read whole for the JSON test: plain text. */
                return(VoorheesFormat.VF_DETECT);
            }

            try
            {   /* The whole file, for the content test (JSON is parsed   */
                /* whole, so a head would not do).                        */
                abFile = File.ReadAllBytes(sPath);
            }
            catch (Exception exRead)
            {   /* Not read: logged with the reason, and answered as plain   */
                /* text -- a failed read is no evidence of a format.         */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("open", "cannot read " + sPath + " to identify it: " + exRead.Message);
                }

                /* The plain-text answer: a failed read is no evidence of a format. */
                return(VoorheesFormat.VF_DETECT);
            }

            if (VoorheesFormat.Vf_DetectFromText(abFile) == VoorheesFormat.VF_JSON)
            {   /* It reads as JSON: the structured editor.  Any other answer   */
                /* is the INI fallback, which is not taken.                     */
                return(VoorheesFormat.VF_JSON);
            }

            /* Nothing structured: plain text. */
            return(VoorheesFormat.VF_DETECT);
        }

        /*--------------------------------------------------------------------*/
        /* Osc_ReadHead:                                                      */
        /*                                                                    */
        /* The first lLimit bytes of a file, or all of a shorter one, for the */
        /* printer-config test.  A failure is logged and answered with null.  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sPath  : the local file.                                       */
        /*     lLimit : the most to read, bytes; small enough for one array   */
        /*              (the caller passes OSC_DETECT_MAX_BYTES).             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte[] : the bytes read (fewer than asked if the file ended    */
        /*              first), or null when the file could not be read.      */
        /*--------------------------------------------------------------------*/
        private static byte[] Osc_ReadHead(string sPath, long lLimit)
        {
            FileStream stmHead;
            byte[] abHead;
            int iWanted;
            int iRead;
            int iGot;

            try
            {   /* Every failure below -- the open, a read -- is caught, so   */
                /* the caller gets null rather than an exception.             */
                using (stmHead = new FileStream(sPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {   /* Opened for reading, letting others read and write it   */
                    /* meanwhile; closed when the block ends, a failure       */
                    /* included.  The buffer is the smaller of the file and   */
                    /* the limit, so a large file costs only the limit.       */
                    iWanted = (int)Math.Min(stmHead.Length, lLimit);
                    abHead = new byte[iWanted];
                    iRead = 0;
                    while (iRead < iWanted)
                    {   /* One read per iteration: a read may return less   */
                        /* than was asked for.                              */
                        iGot = stmHead.Read(abHead, iRead, iWanted - iRead);
                        if (iGot <= 0)
                        {   /* The file ended early -- it shrank after its   */
                            /* length was read.                              */
                            break;
                        }

                        /* Count what arrived; the next read fills on after it. */
                        iRead += iGot;
                    }
                }
            }
            catch (Exception exRead)
            {   /* Not read: the reason logged, and null -- the caller then   */
                /* treats the file as plain text.                             */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("open", "cannot read " + sPath + " to identify it: " + exRead.Message);
                }

                /* Nothing read: null, which the caller takes as plain text. */
                return(null);
            }

            if (iRead < abHead.Length)
            {   /* The file ended early: the unfilled tail is cut off, so the   */
                /* scan sees only the file's own bytes.                         */
                Array.Resize(ref abHead, iRead);
            }

            /* The bytes read, the caller's to keep. */
            return(abHead);
        }

        /*--------------------------------------------------------------------*/
        /* Osc_ConfigFormatFromText:                                          */
        /*                                                                    */
        /* OSC_ConfigKind's verdict as the structured editor's format id.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     abText : the file's start.                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : VF_KLIPPER, VF_MOONRAKER or VF_KLIPPERSCREEN; VF_DETECT  */
        /*           (plain text) for a tie or no known section.              */
        /*--------------------------------------------------------------------*/
        private static int Osc_ConfigFormatFromText(byte[] abText)
        {
            int iKind;

            /* The content test, then one id per answer. */
            iKind = OSC_ConfigKind(abText);
            if (iKind == OSC_CONFIG_KLIPPER)
            {   /* Klipper's: the Klipper editor. */
                return(VoorheesFormat.VF_KLIPPER);
            }

            if (iKind == OSC_CONFIG_MOONRAKER)
            {   /* Moonraker's. */
                return(VoorheesFormat.VF_MOONRAKER);
            }

            if (iKind == OSC_CONFIG_SCREEN)
            {   /* KlipperScreen's. */
                return(VoorheesFormat.VF_KLIPPERSCREEN);
            }

            /* None of the three: plain text. */
            return(VoorheesFormat.VF_DETECT);
        }
    }

    /*--------------------------------------------------------------------------*/
    /* BNY_clsBinaryForm -- the question asked before a file that does not look */
    /* like text is opened: open it as text anyway, open it in the hex          */
    /* editor, or leave it.  The question presenter shows it                    */
    /* (CommandQuestionPresenter.cs), and so does the main window's             */
    /* Main_ShowOpened (MainForm.cs); each reads the answer through             */
    /* BNY_ReadChoice once the dialog has closed.                               */
    /* Its text is in the program's dialog font, which                          */
    /* the caller passes in; the dialog neither chooses nor frees it.           */
    /*                                                                          */
    /* Derives from Form because the framework requires it: WinForms makes a    */
    /* window only as a Form subclass.  That is the one kind of inheritance     */
    /* allowed here -- from a framework base class, never from a class of       */
    /* ours.                                                                    */
    /*--------------------------------------------------------------------------*/
    internal sealed class BNY_clsBinaryForm : Form
    {
        /* The three outcomes -- documentation table. */
        internal const int BNY_CHOICE_CANCEL = 0;    // leave the file alone (also what closing the dialog means)
        internal const int BNY_CHOICE_TEXT   = 1;    // open it in the text editor anyway
        internal const int BNY_CHOICE_HEX    = 2;    // open it in the hex editor

        /* The dialog's geometry -- documentation table.  The width is fixed   */
        /* and the height follows from it (Bny_FitHeight).                     */
        private const int BNY_WIDTH = 520;           // client width, pixels
        private const int BNY_PAD   = 12;            // padding round the content, pixels

        /* The answer, the one thing the caller reads -- after ShowDialog   */
        /* returns, before the dialog is disposed.                          */
        private int Bny_iChoice;                    // recorded BNY_CHOICE_*; Cancel until a choice is made

        /* The dialog's controls -- documentation table. */
        private Label           Bny_lblQuestion;     // the question, with the file's name and what it looks like
        private FlowLayoutPanel Bny_pnlButtons;      // the three buttons, right-aligned
        private Button          Bny_btnText;         // Open as Text
        private Button          Bny_btnHex;          // Edit Hex
        private Button          Bny_btnCancel;       // Cancel

        /*--------------------------------------------------------------------*/
        /* BNY_clsBinaryForm (constructor):                                   */
        /*                                                                    */
        /* Build the dialog.  Escape presses Cancel; Enter presses Edit Hex,  */
        /* the choice that shows a binary file as it is, byte for byte.  When */
        /* the file is too large for the text editor, Open as Text is greyed  */
        /* out and the question says why.                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     fntDialog    : the program's dialog font (the caller's; not    */
        /*                    freed here).                                    */
        /*     sName        : the file's name.                                */
        /*     sWhy         : what it looks like, finishing "It looks like";  */
        /*                    "" when there is nothing to add.                */
        /*     bTextAllowed : false when the file is too large for the text   */
        /*                    editor.                                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the dialog, not yet shown.                     */
        /*--------------------------------------------------------------------*/
        internal BNY_clsBinaryForm(Font fntDialog, string sName, string sWhy, bool bTextAllowed)
        {
            string sQuestion;

            /* Cancel until a button says otherwise, so closing the dialog     */
            /* any other way (Escape, the title bar's close) leaves the file   */
            /* alone.                                                          */
            Bny_iChoice = BNY_CHOICE_CANCEL;

            /* The caller's dialog font, set before any control is made; the   */
            /* question's height below is measured in it.                      */
            this.Font = fntDialog;

            /* A fixed dialog named for the file, centred on its owner, not   */
            /* in the taskbar, with no maximise or minimise: it is a          */
            /* question, not a window to keep.                                */
            this.Text            = "Open " + sName;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox     = false;
            this.MinimizeBox     = false;
            this.ShowInTaskbar   = false;
            this.StartPosition   = FormStartPosition.CenterParent;
            this.Padding         = new Padding(BNY_PAD);

            /* The question: a fixed sentence first, then what the scan found   */
            /* and, when it applies, why Open as Text is greyed out.            */
            sQuestion = "This file appears to be binary. Are you sure you want to open it?";

            if (sWhy.Length != 0)
            {   /* Say what it looks like. */
                sQuestion = sQuestion + "\n\nIt looks like " + sWhy + ".";
            }

            if (!bTextAllowed)
            {   /* Explain the greyed-out button. */
                sQuestion = sQuestion + "\n\nIt is too large to open as text; the hex editor can still open it.";
            }

            /* The buttons, right-aligned; added right-to-left.  Each sizes   */
            /* itself to its text (the font can be large) and records its     */
            /* answer when clicked.  Cancel first, so it ends up rightmost.   */
            Bny_btnCancel          = new Button();
            Bny_btnCancel.Text     = "Cancel";
            Bny_btnCancel.AutoSize = true;
            Bny_btnCancel.Click   += Bny_OnCancelClicked;

            /* Edit Hex, next to Cancel: the dialog's default (set below),   */
            /* since the hex editor shows any file, byte for byte.           */
            Bny_btnHex          = new Button();
            Bny_btnHex.Text     = "Edit &Hex";
            Bny_btnHex.AutoSize = true;
            Bny_btnHex.Click   += Bny_OnHexClicked;

            /* Open as Text, leftmost, greyed out when the file is too large   */
            /* for the text editor.                                            */
            Bny_btnText          = new Button();
            Bny_btnText.Text     = "Open as &Text";
            Bny_btnText.AutoSize = true;
            Bny_btnText.Enabled  = bTextAllowed;
            Bny_btnText.Click   += Bny_OnTextClicked;

            /* The row that holds them: docked along the bottom, filling from   */
            /* the right, as tall as the buttons, with a pad above to part it   */
            /* from the question.                                               */
            Bny_pnlButtons               = new FlowLayoutPanel();
            Bny_pnlButtons.Dock          = DockStyle.Bottom;
            Bny_pnlButtons.FlowDirection = FlowDirection.RightToLeft;
            Bny_pnlButtons.AutoSize      = true;
            Bny_pnlButtons.Padding       = new Padding(0, BNY_PAD, 0, 0);
            Bny_pnlButtons.Controls.Add(Bny_btnCancel);
            Bny_pnlButtons.Controls.Add(Bny_btnHex);
            Bny_pnlButtons.Controls.Add(Bny_btnText);

            /* The question fills what the buttons leave; its height is      */
            /* measured for the width it will have, so no line is cut off.   */
            Bny_lblQuestion          = new Label();
            Bny_lblQuestion.Dock     = DockStyle.Fill;
            Bny_lblQuestion.AutoSize = false;
            Bny_lblQuestion.Text     = sQuestion;

            /* Docking runs in reverse order of adding: the button row takes   */
            /* the bottom first, then the question fills the rest (the         */
            /* framework's layout order, as found).                            */
            this.Controls.Add(Bny_lblQuestion);
            this.Controls.Add(Bny_pnlButtons);

            /* Enter presses Edit Hex, the default; Escape presses Cancel,   */
            /* the answer that opens nothing.                                */
            this.AcceptButton = Bny_btnHex;
            this.CancelButton = Bny_btnCancel;

            /* The focus starts on Edit Hex as well: a button that has the      */
            /* focus is the one Enter presses, whatever the accept button is,   */
            /* and the first button added (Cancel) would otherwise have it --   */
            /* both the framework's behaviour, as found -- so without this      */
            /* line Enter would press Cancel.                                   */
            this.ActiveControl = Bny_btnHex;

            /* The width fixed at BNY_WIDTH first, because the question's       */
            /* height depends on where it wraps; then the height fitted to      */
            /* it; then the handler that fits it again should the width ever    */
            /* change.  Attached last, so the two sizings here do not run it.   */
            this.ClientSize = new Size(BNY_WIDTH, this.ClientSize.Height);
            Bny_FitHeight();
            this.Resize += Bny_OnResize;
        }

        /*--------------------------------------------------------------------*/
        /* BNY_ReadChoice:                                                    */
        /*                                                                    */
        /* Which button was pressed, read by the caller once ShowDialog has   */
        /* returned and before the dialog is disposed.  Reading it changes    */
        /* nothing: what follows is the caller's to decide.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : BNY_CHOICE_TEXT or BNY_CHOICE_HEX; BNY_CHOICE_CANCEL for */
        /*           Cancel and every other way the dialog was closed.        */
        /*--------------------------------------------------------------------*/
        internal int BNY_ReadChoice()
        {
            /* The choice the last button pressed recorded, or the   */
            /* constructor's Cancel when none was.                   */
            return(Bny_iChoice);
        }

        /*--------------------------------------------------------------------*/
        /* Bny_FitHeight:                                                     */
        /*                                                                    */
        /* Make the dialog as tall as its content needs at its present        */
        /* width.  The docking does the placing; the height is the one fact   */
        /* it cannot supply, since a wrapped label does not grow its window.  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the client height fits the question and the buttons.    */
        /*--------------------------------------------------------------------*/
        private void Bny_FitHeight()
        {
            int iHeight;

            /* The question's wrapped height at the width between the         */
            /* paddings, the button row's height, and three pads: the         */
            /* dialog's own above and below, and one meant for the gap        */
            /* between the two.  The row also carries a top pad of its own;   */
            /* whether its PreferredSize counts it is the framework's and     */
            /* not established here, and if it does, the question gets one    */
            /* pad of spare room under its last line -- space only, nothing   */
            /* is cut off.                                                    */
            iHeight = TextRenderer.MeasureText(Bny_lblQuestion.Text, this.Font, new Size(this.ClientSize.Width - (2 * BNY_PAD), 0),
                                               TextFormatFlags.WordBreak).Height +
                      Bny_pnlButtons.PreferredSize.Height + (3 * BNY_PAD);

            if (iHeight != this.ClientSize.Height)
            {   /* Only a real change is applied: the Resize this raises calls   */
                /* back here, finds the height already right, and stops --       */
                /* without the test the two would call each other for ever.      */
                this.ClientSize = new Size(this.ClientSize.Width, iHeight);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Bny_OnResize:                                                      */
        /*                                                                    */
        /* The dialog was resized: fit its height to the question again, by   */
        /* the same sum the constructor used.  The border is fixed, so the    */
        /* user cannot resize it; what else raises a resize here is not       */
        /* established.                                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the dialog.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the height fits again.                                  */
        /*--------------------------------------------------------------------*/
        private void Bny_OnResize(object objSender, EventArgs evArgs)
        {
            /* The fitting is Bny_FitHeight's, shared with the constructor so   */
            /* the first sizing and every later one are the same calculation;   */
            /* this handler only connects it to the event.  Its own test of     */
            /* the height is what stops the Resize it raises from looping.      */
            Bny_FitHeight();
        }

        /*--------------------------------------------------------------------*/
        /* Bny_OnTextClicked:                                                 */
        /*                                                                    */
        /* Open as Text: record the choice and close.                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Bny_iChoice is set and the dialog closes.               */
        /*--------------------------------------------------------------------*/
        private void Bny_OnTextClicked(object objSender, EventArgs evArgs)
        {
            /* The answer is set BEFORE the close: the caller reads it as soon   */
            /* as ShowDialog returns, which the close causes.  A dialog shown    */
            /* with ShowDialog is hidden by Close, not disposed (the             */
            /* framework's behaviour, as found; the caller's using block         */
            /* disposes of it), so the field is still readable there.            */
            Bny_iChoice = BNY_CHOICE_TEXT;
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Bny_OnHexClicked:                                                  */
        /*                                                                    */
        /* Edit Hex: record the choice and close.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Bny_iChoice is set and the dialog closes.               */
        /*--------------------------------------------------------------------*/
        private void Bny_OnHexClicked(object objSender, EventArgs evArgs)
        {
            /* The answer set BEFORE the close, for the reason Open as Text   */
            /* gives (Bny_OnTextClicked).                                     */
            Bny_iChoice = BNY_CHOICE_HEX;
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Bny_OnCancelClicked:                                               */
        /*                                                                    */
        /* Cancel: close with the choice left at "cancel".                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the dialog closes.                                      */
        /*--------------------------------------------------------------------*/
        private void Bny_OnCancelClicked(object objSender, EventArgs evArgs)
        {
            /* Closed with the choice left at the "cancel" the constructor   */
            /* set, which is also what Escape and the title bar's close      */
            /* leave -- so all three ways out mean the same thing.           */
            this.Close();
        }
    }
}
