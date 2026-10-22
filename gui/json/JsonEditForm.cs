/*----------------------------------------------------------------------------*/
/* JsonEditForm.cs                                                            */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* BifrostFM's structured-file editor: one window per file, showing a JSON,   */
/* INI or Klipper-family file (Klipper, Moonraker, KlipperScreen) as a tree,  */
/* with an edit panel, undo and redo, and its own Find / Replace and Colors   */
/* dialogs.  The main window gives it one file -- a Windows file, or an image */
/* entry read out to a private copy -- and the format to read it as           */
/* (Main_ShowEditor, MainForm.cs).  Parsing, writing, undo and the tree are   */
/* the other files of this folder; this file is the window around them.       */
/*                                                                            */
/* Saving.  A Save encodes the whole file at the click and keeps the bytes    */
/* with the document's revision (JSC_clsSaveCapture).  A Windows file is      */
/* written here, through a temporary file that replaces it; an image entry's  */
/* bytes go into a staged file of their own, which the command worker puts    */
/* into the image.  Where Save writes next, and the end of the undo history,  */
/* change only when the save is acknowledged; an answer that arrives while    */
/* the document is being changed is held until that work returns, so it       */
/* cannot clear the history under a step still being built.  Save As asks     */
/* the command system for an image or Windows target; a save into the image   */
/* never writes the Windows file the document was opened from.                */
/*                                                                            */
/* The command system.  The window reports when its own work has returned     */
/* (EUI_LOCAL_RETURN, CommandEditorCoordinator.cs), so a close the worker     */
/* asks for waits for it, and the worker's close question is answered from    */
/* here; a close a save asked for is decided on a later turn and reported     */
/* with that save's number, even when newer edits keep the window open.       */
/*                                                                            */
/* The other files of this folder it works through:                           */
/*     VoorheesDocument.cs : document changes, undo/redo, search and actions. */
/*     VoorheesFormat.cs   : format dispatch.                                 */
/*     VoorheesText.cs     : encoding, byte-order marks and line endings.     */
/*     VoorheesJson.cs     : JSON reader/writer and comments.                 */
/*     VoorheesLines.cs    : line engine for INI/configuration/registry text. */
/*     VoorheesIni.cs      : INI format.                                      */
/*     VoorheesCfg.cs      : Klipper, Moonraker and KlipperScreen formats.    */
/*     VoorheesReg.cs      : registry format, absent from the editor menus.   */
/*     VoorheesTreeView.cs : lazy tree construction and in-place updates.     */
/* And ..\lib\BusyModalForm.cs : the busy window shown over long work.        */
/*                                                                            */
/* The window's placement, splitter, font size, view options and colours are  */
/* kept under HKCU\Software\BifrostFM, in values whose names begin "Json"     */
/* (the program's other settings never do, AppSettings.cs).  How faithfully a */
/* file is written back is each format's own: this window promises no more    */
/* than its module does.                                                      */
/*----------------------------------------------------------------------------*/

/* The framework: the base types (event arguments, the exceptions caught,       */
/* Guid for the temporary file's name, Math, Action), the lists, drawing        */
/* (fonts, colours, sizes, the dialogs' icons), invariant number text for the   */
/* settings, the file read and the staged write (File, FileStream, Path), and   */
/* the window and its controls.  BifrostFM's own types (the file record, the    */
/* command system's questions, stages and Save As menu, the debug log and the   */
/* busy window -- gui\lib\DebugLog.cs, BusyModalForm.cs); and the registry,     */
/* for the settings.                                                            */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using BifrostFM;
using Microsoft.Win32;

/*----------------------------------------------------------------------------*/
/* VCO_PickColor:                                                             */
/*                                                                            */
/* The shape of the Colors dialog's colour picker: given the dialog, a        */
/* colour role and its present colour, it asks for a new one.  The Colors     */
/* dialog (VoorheesColorsForm, at the end of this file) is given one by its   */
/* maker and calls it for the role the user chooses to change.                */
/*                                                                            */
/* Arguments:                                                                 */
/*     frmOwner  : the Colors dialog, which owns the picker while it is up.   */
/*     sRole     : the role's key (a name in the palette).                    */
/*     clrNow    : the role's present colour, shown as the picker's start.    */
/*     clrPicked : receives the colour chosen; the caller ignores it when     */
/*                 false is returned.                                         */
/*                                                                            */
/* Returns:                                                                   */
/*     bool : true when a colour was chosen, false when the picker was        */
/*            cancelled.                                                      */
/*----------------------------------------------------------------------------*/
delegate bool VCO_PickColor(Form frmOwner, string sRole, Color clrNow,
    out Color clrPicked);

/*----------------------------------------------------------------------------*/
/* JFD_clsFileData: a file as read, before it is the document                 */
/*----------------------------------------------------------------------------*/

/*--------------------------------------------------------------------------*/
/* JFD_clsFileData:                                                         */
/*                                                                          */
/* What JSN_ReadDocument learned about a file: the document (with its       */
/* format and style) and what the user should be told about it, or why it   */
/* could not be read.  It holds no controls, so it is filled off the UI     */
/* thread (JLD_clsLoadWork, behind the busy window) and read afterwards on  */
/* the UI thread, once the work has finished (Jsn_FileLoadFromPath).        */
/*--------------------------------------------------------------------------*/
internal class JFD_clsFileData
{
    public VoorheesDocument JFD_objDocument;  // the document; null when the file could not be read or is not valid in the format
    public int JFD_iExtraCopies;              // repeated-key copies in the document (Vd_ExtraCopies), for the format's opening warnings
    public bool JFD_bHasComments;             // the document holds a comment, counted with the parse off the UI thread and handed to the document's set-up (Jsn_DocSetDocument); false when it was not parsed
    public bool JFD_bReadFailed;              // the file could not be read at all (JFD_sError says why)
    public string JFD_sError;                 // the system's reason for a failed read, or the format's for a refused parse (line and column); null when neither
}

/*----------------------------------------------------------------------------*/
/* JSC_clsSaveCapture -- one Save, as it was when it was asked for.           */
/*                                                                            */
/* A Save is written after the click returns -- on a worker for a Windows     */
/* file, through the command system for an image entry -- and its answer      */
/* comes later still.  Meanwhile the user can go on editing.  So everything   */
/* the Save needs, and everything its answer must be matched against, is      */
/* taken at the click and kept here: the file record it writes to, the bytes  */
/* already encoded, the document and its revision number at that moment, the  */
/* Save's own order number, and whether a close is waiting on it.  The        */
/* editor's later state never leaks into the write or the answer.  The        */
/* write of the file records its own outcome here as well, so it is known     */
/* even when the busy window it ran behind fails (JSC_WriteOutcome).          */
/*----------------------------------------------------------------------------*/
internal class JSC_clsSaveCapture
{
    internal OFD_clsOpenedFile JSC_objSnapshot;          // the file record this Save writes to, a copy of the editor's at the click
    internal VoorheesDocument JSC_doc;                   // the document whose content was encoded; the answer clears its undo history only if it is still the editor's document
    internal byte[] JSC_abFile;                          // the whole file, encoded at the click; never changed afterwards
    internal long JSC_lRevision;                         // the document's revision number those bytes are
    internal long JSC_lSaveOrder;                        // this Save's order number among the editor's Saves
    internal bool JSC_bSaved;                            // the answer, kept while the editor is too busy to apply it (see the editor's acknowledgement code)
    internal bool JSC_bCloseWhenSaved;                   // true when this Save was made for a close, which is decided again once it has worked
    internal bool JSC_bWritten;                          // the last JSC_WriteWindows finished its write; set on the worker, read only once BMF_Run has waited for it
    internal Exception JSC_exWrite;                      // what the last JSC_WriteWindows' write threw; null when it has not run or did not throw
    private readonly JSN_clsJsonEditForm Jsc_frmEditor;  // the editor that asked; the answer goes back to it

    /*------------------------------------------------------------------------*/
    /* JSC_clsSaveCapture (constructor):                                      */
    /*                                                                        */
    /* Keep one Save's facts, as taken at the click.                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     frmEditor       : the editor asking.                               */
    /*     objSnapshot     : the file record to write to.                     */
    /*     docSaved        : the document that was encoded.                   */
    /*     abFile          : the encoded bytes; kept, not copied.             */
    /*     lRevision       : the document's revision number at the click.     */
    /*     lSaveOrder      : this Save's order number.                        */
    /*     bCloseWhenSaved : true when a close is waiting on this Save.       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     (constructor) : the capture, with no answer yet and no write       */
    /*                     made.  It holds no stage file of its own.          */
    /*------------------------------------------------------------------------*/

    internal JSC_clsSaveCapture(JSN_clsJsonEditForm frmEditor, OFD_clsOpenedFile objSnapshot,
        VoorheesDocument docSaved, byte[] abFile, long lRevision, long lSaveOrder, bool bCloseWhenSaved)
    {
        /* The facts kept by reference: the bytes are the encoder's own     */
        /* array, which nothing writes to again, and the document is the    */
        /* one object the answer compares against.  No answer yet, and no   */
        /* write made, so no outcome of one.                                */
        Jsc_frmEditor = frmEditor;
        JSC_objSnapshot = objSnapshot;
        JSC_doc = docSaved;
        JSC_abFile = abFile;
        JSC_lRevision = lRevision;
        JSC_lSaveOrder = lSaveOrder;
        JSC_bCloseWhenSaved = bCloseWhenSaved;
        JSC_bSaved = false;
        JSC_bWritten = false;
        JSC_exWrite = null;
    }

    /*------------------------------------------------------------------------*/
    /* JSC_WriteWindows:                                                      */
    /*                                                                        */
    /* Write the captured bytes to the record's file: the Windows file itself */
    /* when the editor opened one, or an image entry's private copy (the      */
    /* file the editor reads back).  A named method so the editor's local     */
    /* work can run it as a callback; what the writer does on a failure is    */
    /* its own (JSN_WriteBytes).  The outcome is also kept here               */
    /* (JSC_bWritten, JSC_exWrite), because the busy window this runs behind  */
    /* can fail after the write has finished and BMF_Run then throws that     */
    /* failure instead of handing back the write's (JSC_WriteOutcome).        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the file is written and JSC_bWritten set, or                */
    /*            JSN_WriteBytes' exception is kept in JSC_exWrite and goes   */
    /*            on to whoever ran this.                                     */
    /*------------------------------------------------------------------------*/

    internal void JSC_WriteWindows()
    {
        /* No outcome yet for this run: a capture's write runs once, but   */
        /* the fields are cleared so they can only ever describe the run   */
        /* that is about to happen.                                        */
        JSC_bWritten = false;
        JSC_exWrite = null;
        try
        {   /* The bytes and the path as captured -- never the editor's       */
            /* present document, which may have changed since the click --    */
            /* and, once the whole file is in place, the write marked done.   */
            JSN_clsJsonEditForm.JSN_WriteBytes(JSC_abFile, JSC_objSnapshot.OFD_sTempFile);
            JSC_bWritten = true;
        }
        catch (Exception exWrite)
        {   /* Not written: the write's own failure kept for                  */
            /* JSC_WriteOutcome, then thrown on unchanged, so BMF_Run hands   */
            /* it back, as it does whenever its window did not fail.          */
            JSC_exWrite = exWrite;
            throw;
        }
    }

    /*------------------------------------------------------------------------*/
    /* JSC_WriteOutcome:                                                      */
    /*                                                                        */
    /* What a write run behind the busy window came to, when BMF_Run threw    */
    /* because the window itself failed (it throws that only once the work    */
    /* has finished, or before the work was started at all).  A write that    */
    /* finished is a success: the window's failure is a display fault only,   */
    /* written to the debug log, and the file is reported as written.  A      */
    /* write that threw is reported by its own exception; a write that never  */
    /* ran is reported by the window's.  Read on the UI thread after BMF_Run  */
    /* has returned or thrown -- by then it has waited for the work, so the   */
    /* two fields hold the work's final values.                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     exWindow : what BMF_Run threw.                                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Exception : null when the file was written; otherwise the failure  */
    /*                 to report -- the write's own, or the window's when     */
    /*                 the write never ran.                                   */
    /*------------------------------------------------------------------------*/

    internal Exception JSC_WriteOutcome(Exception exWindow)
    {
        if (JSC_bWritten)
        {   /* Written: the file holds the saved bytes.  The window's         */
            /* failure is logged as the error it is, and is not reported as   */
            /* the save's -- the caller goes on as for a clean write.         */
            if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
            {   /* Only while the debug log is kept: otherwise the line is never built. */
                DBGLOG_clsDebugLog.DBGLOG_Write("structured save busy window", exWindow.ToString());
            }

            /* No failure for the save: the file was written. */
            return(null);
        }

        if (JSC_exWrite != null)
        {   /* The write ran and threw: its own failure is the one to report. */
            return(JSC_exWrite);
        }

        /* The write never ran (BMF_Run failed before starting it): the   */
        /* window's failure is the reason nothing was written.            */
        return(exWindow);
    }

    /*------------------------------------------------------------------------*/
    /* JSC_OnSaved:                                                           */
    /*                                                                        */
    /* The Save's answer: hands it to the editor together with this capture,  */
    /* so the editor settles exactly this Save -- its document, revision and  */
    /* order -- and not whichever Save happens to be its latest.              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bSaved : true when the file was written (and, for an image entry,  */
    /*              the command system took it in); false otherwise.          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the editor has the answer (JSN_SaveAcknowledged).           */
    /*------------------------------------------------------------------------*/

    internal void JSC_OnSaved(bool bSaved)
    {
        /* This capture travels with the answer: two Saves in flight can   */
        /* never be mistaken for each other.                               */
        Jsc_frmEditor.JSN_SaveAcknowledged(this, bSaved);
    }

    /*------------------------------------------------------------------------*/
    /* JSC_RunClose:                                                          */
    /*                                                                        */
    /* A Save made for a close has worked: hands the capture to the editor,   */
    /* posted to a later turn of the UI thread, which decides whether to      */
    /* close now (JSN_CompleteDeferredClose).                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the editor has decided.                                     */
    /*------------------------------------------------------------------------*/

    internal void JSC_RunClose()
    {
        /* The capture goes with it, so the decision is made against this    */
        /* Save's revision and order even when newer edits have been made.   */
        Jsc_frmEditor.JSN_CompleteDeferredClose(this);
    }
}

/*----------------------------------------------------------------------------*/
/* JPI_clsPickIntent -- one Save As target question, as it was asked.         */
/*                                                                            */
/* Save As asks the command system for a destination (an image entry or a     */
/* Windows file) and the answer comes later.  This keeps what the question    */
/* was asked against: the editor's file record at the time, and whether a     */
/* close is waiting on the Save.  Each question has its own, so two asked one */
/* after the other never share a slot.  Choosing a target saves nothing by    */
/* itself: the editor decides what to do with it (JSN_ApplyTargetChoice).     */
/*----------------------------------------------------------------------------*/
internal class JPI_clsPickIntent
{
    internal OFD_clsOpenedFile JPI_objBinding;           // the editor's file record when the question was asked, to be matched when the answer comes
    internal bool JPI_bCloseWhenSaved;                   // true when the editor's own close asked the question, so the Save it leads to is one a close waits on, and no target asks the close again
    private readonly JSN_clsJsonEditForm Jpi_frmEditor;  // the editor that asked; the answer goes back to it

    /*------------------------------------------------------------------------*/
    /* JPI_clsPickIntent (constructor):                                       */
    /*                                                                        */
    /* Keep one target question's facts.                                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     frmEditor       : the editor asking.                               */
    /*     objBinding      : its file record at the time.                     */
    /*     bCloseWhenSaved : true when the editor's close asked.              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     (constructor) : the question's facts; no content, no stage file.   */
    /*------------------------------------------------------------------------*/

    internal JPI_clsPickIntent(JSN_clsJsonEditForm frmEditor, OFD_clsOpenedFile objBinding, bool bCloseWhenSaved)
    {
        /* The record kept by reference: it is compared, as the same   */
        /* object, against the editor's when the answer comes.         */
        Jpi_frmEditor = frmEditor;
        JPI_objBinding = objBinding;
        JPI_bCloseWhenSaved = bCloseWhenSaved;
    }

    /*------------------------------------------------------------------------*/
    /* JPI_OnChosen:                                                          */
    /*                                                                        */
    /* The target question's answer: hands it, with this question, to the     */
    /* editor (JSN_ApplyTargetChoice), which checks it still applies and then */
    /* asks about the extension and saves.                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objTarget : the destination chosen; null when the question was     */
    /*                 cancelled or refused.                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the editor has the answer; a null saves nothing, and asks   */
    /*            the close again when the editor's own close asked it.       */
    /*------------------------------------------------------------------------*/

    internal void JPI_OnChosen(OFD_clsOpenedFile objTarget)
    {
        /* The question goes with its answer, so the editor matches this   */
        /* answer to the record it was asked against.                      */
        Jpi_frmEditor.JSN_ApplyTargetChoice(this, objTarget);
    }
}

/*----------------------------------------------------------------------------*/
/* JLD_clsLoadWork -- one file read, run as the editor's local work.          */
/*                                                                            */
/* The path and format go in, the read's facts (JFD_clsFileData) come out.    */
/* It touches no control, so it can run behind the editor's progress window   */
/* (BusyModalForm.cs), and its JLD_Run is a named method rather than a        */
/* lambda.                                                                    */
/*----------------------------------------------------------------------------*/
internal class JLD_clsLoadWork
{
    internal string JLD_sPath;               // the file to read: a Windows file, or an image entry's private copy
    internal int JLD_iFormat;                // the VoorheesFormat.VF_ format to read it as, or VF_DETECT
    internal JFD_clsFileData JLD_objResult;  // the read's facts; null until JLD_Run has run

    /*------------------------------------------------------------------------*/
    /* JLD_clsLoadWork (constructor):                                         */
    /*                                                                        */
    /* Keep one read's path and format.                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sPath   : the file to read.                                        */
    /*     iFormat : the format to read it as, or VF_DETECT.                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     (constructor) : the work, not yet run.                             */
    /*------------------------------------------------------------------------*/

    internal JLD_clsLoadWork(string sPath, int iFormat)
    {
        /* The path and format; no result until the read has run. */
        JLD_sPath = sPath;
        JLD_iFormat = iFormat;
        JLD_objResult = null;
    }

    /*------------------------------------------------------------------------*/
    /* JLD_Run:                                                               */
    /*                                                                        */
    /* The read itself, through JSN_ReadDocument, which reads the file and    */
    /* parses it in its format.                                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : JLD_objResult holds the facts; an exception goes on to      */
    /*            whoever ran this.                                           */
    /*------------------------------------------------------------------------*/

    internal void JLD_Run()
    {
        /* Read and parse, the facts kept for whoever ran this to read once   */
        /* it has returned.                                                   */
        JLD_objResult = JSN_clsJsonEditForm.JSN_ReadDocument(JLD_sPath, JLD_iFormat);
    }
}
/*----------------------------------------------------------------------------*/
/* JSR_clsSearchWork -- one Find Next or Replace All, run as local work.      */
/*                                                                            */
/* What the search needs goes in and what it found comes out, so the search   */
/* itself (JSR_Find or JSR_ReplaceAll, named methods rather than lambdas) can */
/* run behind the editor's progress window on a large document.  The caller   */
/* waits for the work to finish before it reads the result fields.            */
/*----------------------------------------------------------------------------*/
internal class JSR_clsSearchWork
{
    internal VoorheesDocument JSR_doc;            // the document searched; the editor's own, used only while the work runs
    internal List<int> JSR_lstStart;              // Find: the selected entry's position path to search on from; null starts at the first entry
    internal VoorheesEditStep JSR_objStep;        // Replace All: the one undo step every replacement goes into; null for Find
    internal string JSR_sSearch;                  // the text to find, as typed
    internal string JSR_sReplace;                 // Replace All: the replacement text; "" for Find
    internal bool JSR_bMatchCase;                 // Replace All: true to match case
    internal bool JSR_bKeys;                      // true to look in member keys
    internal bool JSR_bValues;                    // true to look in plain values and comments
    internal StringComparison JSR_enmComparison;  // Find: the comparison, ordinal with or without case
    internal List<int> JSR_lstFound;              // Find's result: the match's position path, null when there is none
    internal int JSR_iReplaced;                   // Replace All's result: how many were replaced
    internal int JSR_iSkipped;                    // Replace All's result: how many matches were refused (the format would not take the new text)

    /*-------------------------------------------------------------------------*/
    /* JSR_clsSearchWork (constructor):                                        */
    /*                                                                         */
    /* Keep one search's inputs, with empty results.                           */
    /*                                                                         */
    /* Arguments:                                                              */
    /*     doc           : the document to search.                             */
    /*     lstStart      : Find's start position, or null for the first entry. */
    /*     objStep       : Replace All's undo step, or null for Find.          */
    /*     sSearch       : the text to find.                                   */
    /*     sReplace      : the replacement, "" for Find.                       */
    /*     bMatchCase    : Replace All's case rule.                            */
    /*     bKeys         : true to look in keys.                               */
    /*     bValues       : true to look in values and comments.                */
    /*     enmComparison : Find's comparison.                                  */
    /*                                                                         */
    /* Returns:                                                                */
    /*     (constructor) : the work, not yet run, results empty.               */
    /*-------------------------------------------------------------------------*/

    internal JSR_clsSearchWork(VoorheesDocument doc, List<int> lstStart,
        VoorheesEditStep objStep, string sSearch, string sReplace, bool bMatchCase,
        bool bKeys, bool bValues, StringComparison enmComparison)
    {
        /* Kept by reference, not copied: the caller waits while the work    */
        /* runs, so the document and the path cannot change underneath it.   */
        /* No match, nothing replaced or refused yet.                        */
        JSR_doc = doc;
        JSR_lstStart = lstStart;
        JSR_objStep = objStep;
        JSR_sSearch = sSearch;
        JSR_sReplace = sReplace;
        JSR_bMatchCase = bMatchCase;
        JSR_bKeys = bKeys;
        JSR_bValues = bValues;
        JSR_enmComparison = enmComparison;
        JSR_lstFound = null;
        JSR_iReplaced = 0;
        JSR_iSkipped = 0;
    }

    /*------------------------------------------------------------------------*/
    /* JSR_Find:                                                              */
    /*                                                                        */
    /* Find Next: the next match after the start position, through the        */
    /* document's own search (Vd_FindNext, VoorheesDocument.cs).              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : JSR_lstFound holds the match's path, or null; an exception  */
    /*            goes on to whoever ran this.                                */
    /*------------------------------------------------------------------------*/

    internal void JSR_Find()
    {
        /* The document is searched, not the tree, so entries the tree has   */
        /* not opened yet are found too; no control is touched.              */
        JSR_lstFound = JSR_doc.Vd_FindNext(JSR_lstStart, JSR_sSearch,
            JSR_enmComparison, JSR_bKeys, JSR_bValues);
    }

    /*------------------------------------------------------------------------*/
    /* JSR_ReplaceAll:                                                        */
    /*                                                                        */
    /* Replace All, through the document's own (Vd_ReplaceAll), every         */
    /* replacement recorded in the one undo step given -- so one Undo takes   */
    /* them all back.  Finishing the step and bringing the tree up to date    */
    /* are the caller's, on the UI thread, once this has returned.            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the two counts are kept; an exception goes on to whoever    */
    /*            ran this, the replacements made before it already in the    */
    /*            document.                                                   */
    /*------------------------------------------------------------------------*/

    internal void JSR_ReplaceAll()
    {
        /* The document changed in place and the counts kept; no control is   */
        /* touched and the engine is not called.                              */
        JSR_iReplaced = JSR_doc.Vd_ReplaceAll(JSR_objStep, JSR_sSearch,
            JSR_sReplace, JSR_bMatchCase, JSR_bKeys, JSR_bValues, out JSR_iSkipped);
    }
}

/*-----------------------------------------------------------------------------*/
/* JTE_clsTreeIntent -- a tree selection or an in-place edit, to finish later. */
/*                                                                             */
/* The tree's selection and label-edit events are not finished inside the      */
/* event: the work they lead to (asking about unsaved panel changes, loading   */
/* the panel, applying an edit to the document) is posted to a later turn of   */
/* the UI thread, after the tree's own handling has returned.  This keeps      */
/* what the event knew -- the entry's position path, which still finds the     */
/* entry if its tree node is replaced meanwhile, and an edit's text -- for     */
/* the posted call (JTE_Select or JTE_Commit) to hand back to the editor.      */
/*-----------------------------------------------------------------------------*/
internal class JTE_clsTreeIntent
{
    internal List<int> JTE_lstPath;                      // the entry's position path in the document, as the event found it; not copied
    internal bool JTE_bCloseAfterCommit;                 // true when the editor's own close waits for this in-place edit to be applied
    internal string JTE_sText;                           // an in-place edit's text, copied; null for a cancelled edit and for a selection
    private readonly JSN_clsJsonEditForm Jte_frmEditor;  // the editor the posted call goes back to

    /*------------------------------------------------------------------------*/
    /* JTE_clsTreeIntent (constructor):                                       */
    /*                                                                        */
    /* Keep one tree event's facts for its posted finish.                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     frmEditor         : the editor.                                    */
    /*     lstPath           : the entry's position path.                     */
    /*     sText             : the edit's text; null for a selection or a     */
    /*                         cancelled edit.                                */
    /*     bCloseAfterCommit : true when the editor's close waits on this     */
    /*                         edit.                                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     (constructor) : the facts; nothing has been done yet.              */
    /*------------------------------------------------------------------------*/

    internal JTE_clsTreeIntent(JSN_clsJsonEditForm frmEditor, List<int> lstPath, string sText, bool bCloseAfterCommit)
    {
        /* Kept as given: the path is the event's, not whatever node is   */
        /* selected when the posted call runs.                            */
        Jte_frmEditor = frmEditor;
        JTE_lstPath = lstPath;
        JTE_sText = sText;
        JTE_bCloseAfterCommit = bCloseAfterCommit;
    }

    /*------------------------------------------------------------------------*/
    /* JTE_Select:                                                            */
    /*                                                                        */
    /* The posted finish of a selection: the editor decides about the panel   */
    /* and selects the entry (JSN_CompleteTreeSelection).                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the selection is finished.                                  */
    /*------------------------------------------------------------------------*/

    internal void JTE_Select()
    {
        /* These facts go back with the call, so the editor finishes this   */
        /* selection rather than a later one.                               */
        Jte_frmEditor.JSN_CompleteTreeSelection(this);
    }

    /*------------------------------------------------------------------------*/
    /* JTE_Commit:                                                            */
    /*                                                                        */
    /* The posted finish of an in-place edit: the editor applies the text to  */
    /* the document, or drops a cancelled edit (JSN_CompleteTreeCommit).      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the edit is finished.                                       */
    /*------------------------------------------------------------------------*/

    internal void JTE_Commit()
    {
        /* This edit's own text goes back, never a later edit's. */
        Jte_frmEditor.JSN_CompleteTreeCommit(this);
    }
}
/*----------------------------------------------------------------------------*/
/* JCA_clsContextChoice -- what one right-click menu entry acts on.           */
/*                                                                            */
/* Held in the entry's Tag, so one named handler serves the entries the menu  */
/* builds at run time: each carries its own position path and value kind,     */
/* and the handler reads them from the entry clicked.                         */
/*----------------------------------------------------------------------------*/
internal class JCA_clsContextChoice
{
    internal List<int> JCA_lstPath;  // the entry's position path in the document; the menu's own, not copied
    internal int JCA_iKind;          // the value kind the menu entry makes (a VoorheesDocument kind), or JSN_KIND_COMMENT

    /*------------------------------------------------------------------------*/
    /* JCA_clsContextChoice (constructor):                                    */
    /*                                                                        */
    /* Keep one menu entry's path and kind.                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstPath : the entry's position path.                               */
    /*     iKind   : the kind this menu entry makes.                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     (constructor) : the Tag data; nothing is done by making it.        */
    /*------------------------------------------------------------------------*/

    internal JCA_clsContextChoice(List<int> lstPath, int iKind)
    {
        /* Kept by reference: the path belongs to this one opening of the   */
        /* menu, which is rebuilt each time, so nothing changes it.         */
        JCA_lstPath = lstPath;
        JCA_iKind = iKind;
    }
}

/*----------------------------------------------------------------------------*/
/* JCR_clsContextRequest -- what one request for the right-click menu is for. */
/*                                                                            */
/* The menu's opening is kept closed and posted to the window's queue with    */
/* this record (Jsn_OnContextOpening), whose run settles the panel, selects   */
/* the entry, builds the menu and shows it there (Jsn_ShowContextMenu).       */
/*----------------------------------------------------------------------------*/
internal class JCR_clsContextRequest
{
    internal List<int> JCR_lstPath;  // the entry's position path, taken at the gesture; null for empty space or a placeholder
    internal Point JCR_ptAt;         // where the menu shows, in the tree's coordinates

    /*------------------------------------------------------------------------*/
    /* JCR_clsContextRequest (constructor):                                   */
    /*                                                                        */
    /* Keep one request's entry and place.                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstPath : the entry's position path, or null for empty space.      */
    /*     ptAt    : where the menu shows, in the tree's coordinates.         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     (constructor) : the request; nothing is done by making it.         */
    /*------------------------------------------------------------------------*/

    internal JCR_clsContextRequest(List<int> lstPath, Point ptAt)
    {
        /* Kept by reference: the path was made for this one gesture. */
        JCR_lstPath = lstPath;
        JCR_ptAt = ptAt;
    }
}

/*----------------------------------------------------------------------------*/
/* JDP_clsPromptData -- one small input dialog's controls and its answer.     */
/*                                                                            */
/* The editor's text prompts (a name, a value) and choice-plus-text prompts   */
/* are plain Forms built in code (the DLG_ functions).  Their handlers --     */
/* OK, Cancel, Shown, Resize -- are this record's named methods, so each has  */
/* the controls it needs without a lambda, and OK records the answer here     */
/* for the caller to read once the dialog has closed.  The caller shows the   */
/* dialog modally and disposes of it; nothing here outlives that.             */
/*----------------------------------------------------------------------------*/
internal class JDP_clsPromptData
{
    internal Form JDP_frmPopup;       // the dialog; shown, and disposed of, by the caller
    internal Label JDP_lblChoice;     // the choice's caption; null in a text-only prompt
    internal ComboBox JDP_cmbChoice;  // the fixed choices; null in a text-only prompt
    internal Label JDP_lblPrompt;     // the line saying what to type
    internal TextBox JDP_txtText;     // the box the answer is typed in
    internal Button JDP_btnOk;        // OK, which accepts
    internal Button JDP_btnCancel;    // Cancel
    internal bool JDP_bAccepted;      // true once OK was pressed; every other way of closing leaves it false

    /*------------------------------------------------------------------------*/
    /* JDP_clsPromptData (constructor):                                       */
    /*                                                                        */
    /* Keep one prompt's controls, not yet answered.                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     frmPopup  : the dialog.                                            */
    /*     lblChoice : the choice's caption, or null for a text-only prompt.  */
    /*     cmbChoice : the choice list, or null for a text-only prompt.       */
    /*     lblPrompt : the prompt line.                                       */
    /*     txtText   : the text box.                                          */
    /*     btnOk     : OK.                                                    */
    /*     btnCancel : Cancel.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     (constructor) : the record, with no answer accepted.               */
    /*------------------------------------------------------------------------*/

    internal JDP_clsPromptData(Form frmPopup, Label lblChoice, ComboBox cmbChoice,
        Label lblPrompt, TextBox txtText, Button btnOk, Button btnCancel)
    {
        /* The dialog's own controls, kept by reference for the handlers;   */
        /* not accepted until OK is pressed.                                */
        JDP_frmPopup = frmPopup;
        JDP_lblChoice = lblChoice;
        JDP_cmbChoice = cmbChoice;
        JDP_lblPrompt = lblPrompt;
        JDP_txtText = txtText;
        JDP_btnOk = btnOk;
        JDP_btnCancel = btnCancel;
        JDP_bAccepted = false;
    }

    /*------------------------------------------------------------------------*/
    /* JDP_OnAccept:                                                          */
    /*                                                                        */
    /* OK's Click handler: records that the answer was accepted, then closes  */
    /* the dialog.  The answer is known by this flag, not by the dialog's     */
    /* DialogResult, so every other way the dialog closes reads as not        */
    /* accepted without a case of its own.                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : OK; not used.                                          */
    /*     evArgs    : not used.                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : accepted, and the dialog closing; the caller reads the      */
    /*            choice and text once ShowDialog returns.                    */
    /*------------------------------------------------------------------------*/

    internal void JDP_OnAccept(object objSender, EventArgs evArgs)
    {
        /* Accepted first, then closed: the caller's ShowDialog returns only   */
        /* after the close, by when the flag is set.                           */
        JDP_bAccepted = true;
        JDP_frmPopup.Close();
    }

    /*------------------------------------------------------------------------*/
    /* JDP_OnCancel:                                                          */
    /*                                                                        */
    /* Cancel's Click handler: closes the dialog with nothing accepted.       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : Cancel; not used.                                      */
    /*     evArgs    : not used.                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the dialog closing; JDP_bAccepted stays false.              */
    /*------------------------------------------------------------------------*/

    internal void JDP_OnCancel(object objSender, EventArgs evArgs)
    {
        /* Closed only: the flag is left false, so the caller changes   */
        /* nothing.                                                     */
        JDP_frmPopup.Close();
    }

    /*------------------------------------------------------------------------*/
    /* JDP_OnShown:                                                           */
    /*                                                                        */
    /* The dialog's Shown handler: puts the caret in the text box with its    */
    /* text selected, so typing replaces it.                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the dialog; not used.                                  */
    /*     evArgs    : not used.                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the text box has the focus, its text selected.              */
    /*------------------------------------------------------------------------*/

    internal void JDP_OnShown(object objSender, EventArgs evArgs)
    {
        /* Done once the dialog is on screen rather than while it is built:   */
        /* why focus is not taken earlier is not recorded.                    */
        JDP_txtText.Focus();
        JDP_txtText.SelectAll();
    }

    /*------------------------------------------------------------------------*/
    /* JDP_OnResize:                                                          */
    /*                                                                        */
    /* The dialog's Resize handler: lays the controls out again for its new   */
    /* size, through the same layout the dialog was built with.               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the dialog; not used.                                  */
    /*     evArgs    : not used.                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the controls are placed; the dialog's size is left alone.   */
    /*------------------------------------------------------------------------*/

    internal void JDP_OnResize(object objSender, EventArgs evArgs)
    {
        if (JDP_cmbChoice != null)
        {   /* A choice-plus-text prompt: laid out by its own function, with   */
            /* false for the last argument (JSN_DlgLayoutChoiceBox says what   */
            /* that selects).                                                  */
            JSN_clsJsonEditForm.JSN_DlgLayoutChoiceBox(JDP_frmPopup, JDP_lblChoice,
                JDP_cmbChoice, JDP_lblPrompt, JDP_txtText, JDP_btnOk, JDP_btnCancel, false);
        }
        else
        {   /* A text-only prompt: the input box's layout. */
            JSN_clsJsonEditForm.JSN_DlgLayoutInputBox(JDP_frmPopup,
                JDP_lblPrompt, JDP_txtText, JDP_btnOk, JDP_btnCancel);
        }
    }
}

/*----------------------------------------------------------------------------*/
/* JSN_clsJsonEditForm: the editor window                                     */
/*----------------------------------------------------------------------------*/

/*----------------------------------------------------------------------------*/
/* JSN_clsJsonEditForm                                                        */
/*                                                                            */
/* The editor window and everything it does.  Its functions are grouped by    */
/* what they do, the group's word following the Jsn_ / JSN_ prefix            */
/* (Jsn_TreeBeginInlineEdit, JSN_DlgLayoutChoiceBox):                         */
/*     INIT_   : builds the menus, tree, edit panel and status strip.         */
/*     EDIT_   : the edit panel, its Save and Discard, undo and redo.         */
/*     VIEW_   : the font size, the two view options and the Colors dialog.   */
/*     CTX_    : the tree's right-click menu.                                 */
/*     FILE_   : load (and reopen as another format) and save.                */
/*     DOC_    : finishing an edit: the tree, the title, the comment          */
/*               warning.                                                     */
/*     PROG_   : the progress dialog for long work.                           */
/*     STATE_  : title bar and status strip.                                  */
/*     TREE_   : the tree's selection and in-place editing.                   */
/*     SEARCH_ : find and replace.                                            */
/*     DLG_    : the small text input dialogs.                                */
/*     CFG_    : settings kept in the registry.                               */
/*     JSN_    : what the main window gives this window and reads back.       */
/*                                                                            */
/* At each place the window would show a message, ask for text or a colour,   */
/* or read its settings, it first calls a partial method (the Jsn_...Hook...  */
/* declarations below), which can supply the answer instead.  The program     */
/* itself supplies none, so every such call compiles to nothing and the       */
/* window's own dialogs and registry reads are used.  The class is partial    */
/* for that reason alone: a hook would be written in another part of the      */
/* class, which the program does not have.                                    */
/*                                                                            */
/* Derives from Form because the framework requires it: WinForms makes a      */
/* window only as a Form subclass.  That is the one kind of inheritance       */
/* allowed here -- from a framework base class, never from a class of ours.   */
/*----------------------------------------------------------------------------*/
internal partial class JSN_clsJsonEditForm : Form
{
    /*------------------------------------------------------------------------*/
    /* What the main window gives this window, and what it reads back.        */
    /*------------------------------------------------------------------------*/

    internal const string JSN_CAPTION = "BifrostFM Editor";  // caption of every message box and progress window this editor shows

    OFD_clsOpenedFile Jsn_file;     // the file record a Save writes to now: the opened file's, replaced only when a Save As has been acknowledged
    OFD_SaveRequest Jsn_pfnSave;    // the main window's save request (Main_SaveOpenedFile, MainForm.cs), given at construction
    Font Jsn_fntDialog;             // the program's dialog font, given by the main window, for every dialog, message box and progress window this editor opens (not owned here)
    int Jsn_iFormat;                // the VoorheesFormat.VF_ format the file is first read as, or VF_DETECT to go by its name and contents
    bool Jsn_bLoadFailed;           // the file could not be loaded: the window closes itself without ever showing a document
    private bool Jsn_bNewDocument;  // the main window's New command: the window opens on a new, empty document of Jsn_iFormat instead of reading Jsn_file.OFD_sTempFile (which does not exist yet); the first Save creates the file. Set by whoever shows the window, before it is shown
    private string Jsn_sRefused;    // why the file is not valid in the format it was read as (with line and column), for the main window to show; null when it loaded or failed another way

    /* The Saves in flight and the work running now: counters and lists the    */
    /* window keeps so a Save's answer is applied to the right document at a   */
    /* safe moment, and so the command system can be told when the window's    */
    /* own work has returned -- documentation table.                           */
    private long Jsn_lRevision;                                // the document's revision number, stepped on every change before the change's own progress runs
    private long Jsn_lSaveOrder;                               // the latest Save's order number, stepped at the click before anything is written
    private long Jsn_lAcknowledgedOrder;                       // the order number of the newest Save whose success has been applied here
    private int Jsn_iPendingSaves;                             // Saves asked for and not yet answered
    private int Jsn_iLocalWork;                                // how deep the window's own local work is nested (encoding, file writes, document changes, diagnostics)
    private int Jsn_iModelWork;                                // how deep the document and tree changes in progress are nested; a Save's answer may not clear the undo history while it is not 0
    private int Jsn_iAcknowledgementWork;                      // how deep the applying of Save answers is nested; keeps two from writing the reload copy at once
    private int Jsn_iClosing;                                  // how deep the window's FormClosing handling is nested
    private int Jsn_iPostedTree;                               // tree selections and in-place edits posted (JTE_clsTreeIntent) and not yet finished
    private bool Jsn_bSaveBaselineStale;                       // the undo history goes back past the file as last saved: undoing to "unmodified" would not match the file
    private bool Jsn_bWorkCopyFailed;                          // a Save of an image entry worked, but the editor's private copy of the file could not be brought up to date
    private bool Jsn_bAcknowledgementsPosted;                  // a call to apply the held Save answers is posted and has not run yet; only one at a time
    private bool Jsn_bCloseAfterInline;                        // the editor's own close is waiting for a posted in-place edit to be applied
    private EUI_PostFact Jsn_pfnPostFact;                      // the editor coordinator's report call (CommandEditorCoordinator.cs), given when the window registers with it
    private List<SJB_clsStageJob> Jsn_lstJobs;                 // stage files being written for image-entry Saves (CommandStage.cs); the window's own work, not the engine's
    private List<JSC_clsSaveCapture> Jsn_lstAcknowledgements;  // Save answers that came while the window was too busy to apply them, held in order
    private List<JSC_clsSaveCapture> Jsn_lstCloseEvaluations;  // Saves made for a close that worked, whose close decision is posted and has not yet run
    private WQU_clsWindowQueue Jsn_objQueue;                   // the window's own command queue (WindowQueue.cs): the handlers of Save, Save As, Close, Undo, Redo, Find, Colors, Reopen As, the Font Size items, Show Raw Text, Color Keys, the panel's Discard, the right-click menu's opening and items, and F2 or a double click's in-place edit post to it, and so do a close that needs its questions, their answers, and a close whose Save As was cancelled; it runs each through Jsn_RunItem, one at a time
    private bool Jsn_bCloseAsked;                              // true from the closing handler's post of the close's questions (Jsn_PresentClose) until the queue has the panel's answer (Jsn_ShowCloseQuestion).  Counted as the window's own work (JSN_HasLocalWork)
    private RCD_clsRecoveryDialog Jsn_objCloseQuestion;        // the close question while it is up -- modeless, over this window, which is disabled meanwhile (Jsn_ShowCloseQuestion); null otherwise.  Counted as the window's own work (JSN_HasLocalWork)
    private PRS_clsPresentation Jsn_objCloseShown;             // the worker's close question that close question was shown under, taken when it was shown; null for the window's own close
    private bool Jsn_bDiscardOnClose;                          // true only across the Close() a Discard answer makes (Jsn_ActOnCloseChoice): that close lets the changes go without asking again
    private VCO_clsColorsForm Jsn_frmColors;                   // the Colors dialog while it is up -- modeless, over this window, which is disabled meanwhile (Jsn_ViewShowColors); null otherwise.  Counted as the window's own work (JSN_HasLocalWork)

    /* The window's own commands, as its queue (Jsn_objQueue) carries them:   */
    /* each handler posts one, with the record its menu item holds where it   */
    /* has one, and Jsn_RunItem runs it -- documentation table.               */
    private enum Jsn_enmItem
    {
        Find = 1,           // Search > Find: the Find window, Find-only layout (Jsn_SearchShowFind)
        FindReplace = 2,    // Search > Find + Replace: the same window with its Replace controls
        Colors = 3,         // View > Colors: the Colors dialog (Jsn_ViewShowColors)
        SaveAsImage = 4,    // File > Save As > Image...: an image destination asked for (Jsn_RequestSaveAs)
        SaveAsWindows = 5,  // File > Save As > Windows...: a Windows destination asked for (Jsn_RequestSaveAs)
        ReopenAs = 6,       // a File > Reopen As format: the file read again (Jsn_FileReopenAs); its argument the format, a boxed int
        AddChild = 7,       // right-click Add Child: its argument the item's JCA_clsContextChoice (Jsn_ContextAddChild)
        Rename = 8,         // right-click Rename Key: its argument the entry's path, a List<int> (Jsn_ContextRenameKey)
        Copy = 9,           // right-click Copy: its argument the entry's path (Jsn_ContextCopy)
        Paste = 10,         // right-click Paste: its argument the container's path (Jsn_ContextPaste)
        Delete = 11,        // right-click Delete: its argument the entry's path (Jsn_ContextDelete)
        CloseAgain = 12,    // the window's own close asked again: a Save As chosen from its close question was cancelled, so the question shows once more (JSN_ApplyTargetChoice)
        ColorsAnswer = 13,  // the Colors dialog has closed: its outcome applied and the window enabled again (Jsn_ViewColorsAnswer); its argument the dialog
        AskClose = 14,      // a close found the document open: the panel's question, then the close question (Jsn_ShowCloseQuestion); its argument the worker's close question's display, or null for the window's own close
        CloseAnswer = 15,   // the close question has closed: the window enabled again and its choice acted on (Jsn_ApplyCloseAnswer); its argument the question's form
        ContextMenu = 16,   // the tree's right-click menu asked for: the panel's question, the selection, the menu built and shown (Jsn_ShowContextMenu); its argument a JCR_clsContextRequest
        InlineEdit = 17,    // F2 or a left double click on an entry: the entry found again and edited in place (Jsn_RunInlineEdit); its argument the entry's path, a List<int>
        Save = 18,          // File > Save: a save where Save writes now, if one may start (Jsn_RunSave)
        Close = 19,         // File > Close: the window's ordinary close, which asks about unsaved changes through this queue
        Undo = 20,          // Edit > Undo: one step back (Jsn_EditUndo)
        Redo = 21,          // Edit > Redo: one step forward (Jsn_EditRedo)
        FontLarger = 22,    // View > Font Size > Larger: the next preset up (Jsn_ViewStepFontSize)
        FontSmaller = 23,   // View > Font Size > Smaller: the next preset down (Jsn_ViewStepFontSize)
        FontDefault = 24,   // View > Font Size > Default: the framework's control font size (Jsn_ViewApplyFontSize)
        FontPreset = 25,    // a View > Font Size preset: its size applied (Jsn_ViewApplyFontSize); its argument the size, a boxed float
        ShowRaw = 26,       // View > Show Raw Text in Tree: the option switched (Jsn_ViewToggleShowRaw)
        ColorKeys = 27,     // View > Color Keys: the option switched (Jsn_ViewToggleColorKeys)
        Discard = 28        // the panel's Discard button: the panel reloaded from the selected entry (Jsn_EditDiscard)
    }

    /*------------------------------------------------------------------------*/
    /* Controls                                                               */
    /*------------------------------------------------------------------------*/

    MenuStrip Jsn_mnuMain;                         // the menu bar
    ToolStripMenuItem Jsn_tsmFileMenu;             // File menu
    ToolStripMenuItem Jsn_tsmReopenAs;             // File > Reopen As, greyed while a save is on its way
    ToolStripMenuItem Jsn_tsmEditMenu;             // Edit menu
    ToolStripMenuItem Jsn_tsmSearchMenu;           // Search menu
    ToolStripMenuItem Jsn_tsmViewMenu;             // View menu
    ToolStripMenuItem Jsn_tsmShowRaw;              // View > Show Raw Text in Tree, ticked while on
    ToolStripMenuItem Jsn_tsmColorKeys;            // View > Color Keys, ticked while on
    List<ToolStripMenuItem> Jsn_lstFontSizeItems;  // View > Font Size preset items, checked = current
    CheckBox Jsn_chkBoolValue;                     // value editor shown instead of Jsn_txtValue for Boolean
    Button Jsn_btnDiscard;                         // edit panel Discard: reload the panel from the document
    bool Jsn_bPanelDirty;                          // the panel holds typed changes not yet saved to the document
    bool Jsn_bRawDirty;                            // those changes are in the Raw box (the other fields are locked)
    bool Jsn_bSuppressPanelDirty;                  // set while the panel is loaded from code, so it is not marked dirty
    SplitContainer Jsn_splMain;                    // tree on the left, edit panel on the right
    VTV_clsTreeView Jsn_tree;                      // the document as a tree
    Panel Jsn_pnlEdit;                             // right-hand panel holding the editors below
    Label Jsn_lblKey;                              // "Key:" caption
    TextBox Jsn_txtKey;                            // member key; read-only for array items, comments and the top value
    Label Jsn_lblValue;                            // "Value:" caption
    TextBox Jsn_txtValue;                          // value of a string, number or null; a comment entry's words
    Label Jsn_lblType;                             // "Type:" caption
    ComboBox Jsn_cmbType;                          // the value kinds of Jsn_lstTypeKinds, then Comment
    List<int> Jsn_lstTypeKinds;                    // the kind behind each Type box entry but the last (Comment)
    Label Jsn_lblComment;                          // "Comment:" caption
    TextBox Jsn_txtComment;                        // the comment on the selected entry's line
    Label Jsn_lblRaw;                              // "Raw:" caption
    TextBox Jsn_txtRaw;                            // the selected entry's text exactly as in the file
    Font Jsn_fntRaw;                               // fixed-pitch font of the Raw box
    Button Jsn_btnApply;                           // edit panel Save: write the panel into the document
    StatusStrip Jsn_stsStatus;                     // status bar
    ToolStripStatusLabel Jsn_tslFilePath;          // full path of the open file
    ToolStripStatusLabel Jsn_tslFormat;            // the document's format ("JSON", "INI" ...)
    ToolStripStatusLabel Jsn_tslComments;          // "Contains comments" while the document has any
    ToolStripStatusLabel Jsn_tslModified;          // "Modified" while there are unsaved changes
    ContextMenuStrip Jsn_mnuTreeContext;           // right-click menu, rebuilt by the window queue before each showing (Jsn_ShowContextMenu)
    bool Jsn_bContextFromMouse;                    // the next opening comes from a right click, recorded in the two fields below (set by Jsn_OnTreeMouseDown; read once by Jsn_OnContextOpening; lowered by the tree's next key)
    List<int> Jsn_lstContextPath;                  // the right-clicked entry's path, or null for empty space or a placeholder
    Point Jsn_ptContext;                           // where the right click was, in the tree's coordinates
    bool Jsn_bContextShowing;                      // the window queue is showing the menu itself: its Opening is let through (set and lowered round the Show in Jsn_ShowContextMenu)

    /*------------------------------------------------------------------------*/
    /* Document and editor state                                              */
    /*------------------------------------------------------------------------*/

    VoorheesDocument Jsn_doc;            // the document being edited
    bool Jsn_bHasComments;               // the document holds a comment now
    bool Jsn_bEditWarned;                // the format's edit warning (JSON: now JSONC) was shown for this document
    string Jsn_sCommentShown;            // the Comment box's text as loaded, to see whether it was edited
    string Jsn_sRawSource;               // the Raw box's text as rendered (before CRLF for the box)
    string Jsn_sRawShown;                // the Raw box's text as loaded, to see whether it was edited
    bool Jsn_bRawTruncated;              // the Raw box shows only the start of a huge entry (read-only)
    bool Jsn_bLabelEditing;              // an in-tree value edit box is open
    bool Jsn_bInlineEditRequested;       // the next label edit was started by Jsn_TreeBeginInlineEdit
    VoorheesItem Jsn_clipboardItem;      // entry copied with Copy (a private deep copy), for Paste
    int Jsn_iClipboardFormat;            // the VoorheesFormat.VF_ format Jsn_clipboardItem was copied from (Paste converts from it)
    FRF_clsFindReplaceForm Jsn_frmFind;  // the Find / Replace window while it is open
    double Jsn_dblSplitRatio;            // splitter position as a fraction of the width
    float Jsn_fFontSize;                 // tree and panel font size, points
    Font Jsn_fntView;                    // the font currently given to the tree and panel

    /*------------------------------------------------------------------------*/
    /* View font sizes, points.  Larger / Smaller step through the            */
    /* presets; the default is the standard WinForms control font size,       */
    /* which is not itself a preset.                                          */
    /*------------------------------------------------------------------------*/

    static readonly float[] Jsn_afFontSizes = new float[]
    {
        8f, 9f, 10f, 11f, 12f, 14f, 16f, 18f, 20f, 24f   // View > Font Size preset menu items
    };
    const float JSN_FONT_MIN = 6f;                       // smallest size accepted from the registry or code
    const float JSN_FONT_MAX = 36f;                      // largest size accepted from the registry or code

    /*------------------------------------------------------------------------*/
    /* The Type box's entries are the value kinds the format offers for the   */
    /* selected entry (VoorheesFormat.Vf_PanelKinds, kept in                  */
    /* Jsn_lstTypeKinds), then Comment, shown (and fixed) for a comment       */
    /* entry; JSN_KIND_COMMENT stands for that last entry wherever a kind is  */
    /* expected.  And the Raw box's size limit: an entry whose text is        */
    /* longer is shown cut, read-only.                                        */
    /*------------------------------------------------------------------------*/

    const int JSN_KIND_COMMENT = -1;                     // "kind" of the Type box's Comment entry, and of Add Child > Comment
    const int JSN_RAW_MAX = 64 * 1024;                   // longest entry text editable in the Raw box, characters

    /* The progress window's step and delay, the prompts' spacing, and the     */
    /* registry key and value names the settings are kept under, with the      */
    /* colour values' names in the order of the roles they hold --             */
    /* documentation table.                                                    */
    /*                                                                         */
    /* The step is also the size below which Jsn_DocApplyStepToTree sets up    */
    /* no progress window at all: a smaller step would report only at its      */
    /* first change, before the delay could pass.  The delay keeps ordinary    */
    /* work -- opening or saving a usual file -- from flashing a window.       */
    /* The prompts' other sizes are measured from their fonts and captions.    */
    /* The key is the program's own (HKCU, so no administrator rights are      */
    /* needed), and every value name begins "Json", so none can meet one of    */
    /* the program's other settings.  The two colour arrays are indexed by     */
    /* the tree's colour slots -- VoorheesTreeView.cs's VTV_NAME_ (0 to 2)     */
    /* and VoorheesFormat.cs's VF_ROLE_ (0 to 4) -- so a slot added or moved   */
    /* there is added or moved here too.                                       */
    const int JSN_PROGRESS_STEP = 500;                            // tree changes per BMF_ReportUiProgress call
    const int JSN_SHOW_DELAY_MS = 300;                            // delay before BusyModalForm appears
    const int JSN_CLOSE_WAIT_NODES = 20000;                       // a closing tree of at least this many nodes shows a wait box while the Windows tree control removes them (measured at about 60 microseconds a node: 400 000 take about 24 s)
    const int JSN_DLG_PAD = 12;                                   // space round the content
    const int JSN_DLG_GAP = 6;                                    // space between the rows and between the buttons
    const int JSN_DLG_MIN_CHARS = 40;                             // input box is at least this many average characters wide
    static readonly string Jsn_sRegPath = "Software\\BifrostFM";  // under HKEY_CURRENT_USER
    const string JSN_VAL_X = "JsonWindowLeft";                    // window left edge, pixels (DWORD)
    const string JSN_VAL_Y = "JsonWindowTop";                     // window top edge, pixels (DWORD)
    const string JSN_VAL_W = "JsonWindowWidth";                   // window width, pixels (DWORD)
    const string JSN_VAL_H = "JsonWindowHeight";                  // window height, pixels (DWORD)
    const string JSN_VAL_MAX = "JsonWindowMaximized";             // 1 when the window was maximized (DWORD)
    const string JSN_VAL_SHOW_RAW = "JsonShowRaw";                // 1 when View > Show Raw Text in Tree is on (DWORD)
    const string JSN_VAL_COLOR_KEYS = "JsonColorKeys";            // 1 when View > Color Keys is on (DWORD)
    const string JSN_VAL_SPLIT = "JsonSplitRatio";                // the tree's share of the width, 0..1, invariant-culture text (string)
    const string JSN_VAL_FONT = "JsonFontPoints";                 // tree and panel font size, points, invariant-culture text (string)
    static readonly string[] Jsn_asNameColors = new string[]
    {
        "JsonColorTopName",                              // VTV_NAME_TOP
        "JsonColorNestedName",                           // VTV_NAME_NESTED
        "JsonColorEntryName"                             // VTV_NAME_ENTRY
    };
    static readonly string[] Jsn_asValueColors = new string[]
    {
        "JsonColorText",                                 // VF_ROLE_TEXT
        "JsonColorNumber",                               // VF_ROLE_NUMBER
        "JsonColorBoolean",                              // VF_ROLE_BOOLEAN
        "JsonColorNull",                                 // VF_ROLE_NULL
        "JsonColorBinary"                                // VF_ROLE_BINARY
    };

    /*------------------------------------------------------------------------*/
    /* Jsn_ViewHookColors:                                                    */
    /*                                                                        */
    /* Partial hook before the Colors dialog is shown: may set the dialog's   */
    /* choices and its answer instead of the user.  Not implemented in the    */
    /* program, so the call compiles to nothing and the dialog is shown.      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     frmColors : the Colors dialog, built and not yet shown.            */
    /*     bAnswered : set true by a hook that has answered.                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : bAnswered false means show the dialog as usual.             */
    /*------------------------------------------------------------------------*/
    partial void Jsn_ViewHookColors(VCO_clsColorsForm frmColors, ref bool bAnswered);

    /*------------------------------------------------------------------------*/
    /* Jsn_DlgHookColor:                                                      */
    /*                                                                        */
    /* Partial hook before the colour picker is shown for one role: may       */
    /* answer instead.  Not implemented in the program (compiles to nothing). */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sRole     : the role's key.                                        */
    /*     clrNow    : its present colour.                                    */
    /*     bAnswered : set true by a hook that has answered.                  */
    /*     bPicked   : the hook's answer: true for a colour chosen, false     */
    /*                 for a cancel.                                          */
    /*     clrAnswer : the colour, when bPicked is true.                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : bAnswered false means show the picker as usual.             */
    /*------------------------------------------------------------------------*/
    partial void Jsn_DlgHookColor(string sRole, Color clrNow, ref bool bAnswered,
        ref bool bPicked, ref Color clrAnswer);

    /*------------------------------------------------------------------------*/
    /* Jsn_DlgHookMessage:                                                    */
    /*                                                                        */
    /* Partial hook before a message box is shown: may answer instead.  Not   */
    /* implemented in the program (compiles to nothing).                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText      : the message's whole text.                             */
    /*     enmButtons : the buttons it offers.                                */
    /*     bAnswered  : set true by a hook that has answered.                 */
    /*     enmAnswer  : the hook's answer, as the button pressed.             */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : bAnswered false means show the message as usual.            */
    /*------------------------------------------------------------------------*/
    partial void Jsn_DlgHookMessage(string sText, MessageBoxButtons enmButtons,
        ref bool bAnswered, ref DialogResult enmAnswer);

    /*------------------------------------------------------------------------*/
    /* Jsn_DlgHookClose:                                                      */
    /*                                                                        */
    /* Partial hook before the close question is shown                        */
    /* (Jsn_ShowCloseQuestion): may answer instead, its choice then acted on  */
    /* as a shown question's would be.  Not implemented in the program        */
    /* (compiles to nothing).                                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sTitle     : the file as the question names it (OFD_Title).        */
    /*     bOrdinary  : true for the ordinary close question, false for the   */
    /*                  recovery question (the entry's image is gone).        */
    /*     bImageOpen : the ordinary question only: Save As to the image is   */
    /*                  offered.                                              */
    /*     bAnswered  : set true by a hook that has answered.                 */
    /*     enmAnswer  : the hook's answer, as the choice made.                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : bAnswered false means show the question as usual.           */
    /*------------------------------------------------------------------------*/
    partial void Jsn_DlgHookClose(string sTitle, bool bOrdinary, bool bImageOpen,
        ref bool bAnswered, ref RCA_enmRecoveryChoice enmAnswer);

    /*------------------------------------------------------------------------*/
    /* Jsn_DlgHookInput:                                                      */
    /*                                                                        */
    /* Partial hook before a text prompt is shown: may answer instead.  Not   */
    /* implemented in the program (compiles to nothing).                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sTitle    : the prompt's caption, which says which question it is. */
    /*     bAnswered : set true by a hook that has answered.                  */
    /*     sAnswer   : the hook's text; null for a cancel.                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : bAnswered false means show the prompt as usual.             */
    /*------------------------------------------------------------------------*/
    partial void Jsn_DlgHookInput(string sTitle, ref bool bAnswered, ref string sAnswer);

    /*------------------------------------------------------------------------*/
    /* Jsn_DlgHookChoice:                                                     */
    /*                                                                        */
    /* Partial hook before a choice-plus-text prompt (a new entry's name in   */
    /* a format that offers several kinds of name) is shown: may answer       */
    /* instead.  Not implemented in the program (compiles to nothing).        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objQuestion : the question: its choices and defaults.              */
    /*     bAnswered   : set true by a hook that has answered.                */
    /*     iChoice     : the hook's choice, an index into the choices.        */
    /*     sAnswer     : the hook's text; null for a cancel.                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : bAnswered false means show the prompt as usual.             */
    /*------------------------------------------------------------------------*/
    partial void Jsn_DlgHookChoice(VoorheesNameQuestion objQuestion, ref bool bAnswered,
        ref int iChoice, ref string sAnswer);

    /*------------------------------------------------------------------------*/
    /* Jsn_CfgHookSkip:                                                       */
    /*                                                                        */
    /* Partial hook before the window reads or writes its registry settings:  */
    /* may say to leave them alone.  Not implemented in the program           */
    /* (compiles to nothing), so the settings are always read and written.    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bSkip : set true by a hook to skip the read or the write.          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : bSkip false means the registry is used as usual.            */
    /*------------------------------------------------------------------------*/
    partial void Jsn_CfgHookSkip(ref bool bSkip);

    /*------------------------------------------------------------------------*/
    /* JSN_clsJsonEditForm (constructor):                                     */
    /*                                                                        */
    /* Build the editor for one file and format, every control made, the      */
    /* document not yet read: the file is read when the window loads.  Made   */
    /* by the main window (Main_ShowEditor, MainForm.cs), which says before   */
    /* showing it whether it is a new document (JSN_SetNewDocument).          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     fntDialog : the program's dialog font, for every dialog this       */
    /*                 window opens; not owned here.                          */
    /*     objFile   : the file record: the Windows file, or an image entry   */
    /*                 and its private copy (OFD_sTempFile, the file read).   */
    /*     pfnSave   : the main window's save request (Main_SaveOpenedFile).  */
    /*     iFormat   : the format to read it as (VoorheesFormat.VF_), or      */
    /*                 VF_DETECT.                                             */
    /*                                                                        */
    /* Returns:                                                               */
    /*     (constructor) : the window, not yet shown, nothing in flight.      */
    /*------------------------------------------------------------------------*/
    public JSN_clsJsonEditForm(Font fntDialog, OFD_clsOpenedFile objFile, OFD_SaveRequest pfnSave,
        int iFormat)
    {
        VoorheesDocument docEmpty;  // the empty placeholder document the window starts with

        /* What the main window handed over, and the save state.  The      */
        /* dialog font is set FIRST: the control builders below make the   */
        /* tree control, which is given it at its creation, for the        */
        /* progress windows it opens.                                      */
        Jsn_fntDialog = fntDialog;
        Jsn_file = objFile;
        Jsn_pfnSave = pfnSave;
        Jsn_iFormat = iFormat;
        Jsn_bLoadFailed = false;
        Jsn_bNewDocument = false;
        Jsn_sRefused = null;

        /* No revision, no Save, nothing in flight, no close question asked     */
        /* or up, and the three lists made -- all before the first control      */
        /* builder runs, since building and the first document set-up already   */
        /* count their work in these fields, before the window has registered   */
        /* with the command system.                                             */
        Jsn_lRevision = 0;
        Jsn_lSaveOrder = 0;
        Jsn_lAcknowledgedOrder = 0;
        Jsn_iPendingSaves = 0;
        Jsn_iLocalWork = 0;
        Jsn_iModelWork = 0;
        Jsn_iAcknowledgementWork = 0;
        Jsn_iClosing = 0;
        Jsn_iPostedTree = 0;
        Jsn_bSaveBaselineStale = false;
        Jsn_bWorkCopyFailed = false;
        Jsn_bAcknowledgementsPosted = false;
        Jsn_bCloseAfterInline = false;
        Jsn_bCloseAsked = false;
        Jsn_objCloseQuestion = null;
        Jsn_objCloseShown = null;
        Jsn_bDiscardOnClose = false;
        Jsn_lstJobs = new List<SJB_clsStageJob>();
        Jsn_lstAcknowledgements = new List<JSC_clsSaveCapture>();
        Jsn_lstCloseEvaluations = new List<JSC_clsSaveCapture>();

        /* The window's own command queue, its items run by Jsn_RunItem. */
        Jsn_objQueue = new WQU_clsWindowQueue(this, Jsn_RunItem);

        /* State that does not depend on the document. */
        Jsn_dblSplitRatio = 0.75;
        Jsn_fFontSize = Control.DefaultFont.SizeInPoints;
        Jsn_lstFontSizeItems = new List<ToolStripMenuItem>();

        /* Window basics.  A saved size and position replace these in      */
        /* OnLoad (Jsn_CfgLoad); the title names the file and its format   */
        /* as soon as the document is shown (Jsn_StateUpdateTitle).        */
        Text = objFile.OFD_Title();
        Size = new Size(960, 640);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        /* Create every control. */
        Jsn_InitMenuStrip();
        Jsn_InitSplitContainer();
        Jsn_InitEditPanel();
        Jsn_InitStatusStrip();
        Jsn_InitContextMenu();

        /* Docking order: WinForms docks controls in reverse order of    */
        /* addition, so the Fill control must be added FIRST to end up   */
        /* filling only the space the menu (top) and status strip        */
        /* (bottom) leave. The control builders add them in creation     */
        /* order, so clear and re-add them in the right one.             */
        Controls.Clear();
        Controls.Add(Jsn_splMain);
        Controls.Add(Jsn_stsStatus);
        Controls.Add(Jsn_mnuMain);
        MainMenuStrip = Jsn_mnuMain;

        /* Start with an empty JSON document; OnLoad replaces it with the   */
        /* file's.  Its comment state is asked of the document itself,      */
        /* an empty object, so the walk costs nothing.                      */
        docEmpty = VoorheesDocument.Vd_CreateNew(VoorheesFormat.VF_JSON);
        Jsn_DocSetDocument(docEmpty, docEmpty.Vd_HasComments());
    }

    /*------------------------------------------------------------------------*/
    /* INIT: building the window's controls                                   */
    /*------------------------------------------------------------------------*/

    /*--------------------------------------------------------------------------*/
    /* Jsn_InitMenuStrip:                                                       */
    /*                                                                          */
    /* Builds the menu bar: File, Edit, Search and View, with their             */
    /* keyboard shortcuts.  Shortcuts are processed by the menu before the      */
    /* focused control sees the key, which is why Jsn_EditUndo and Jsn_EditRedo */
    /* check for a focused text box themselves.                                 */
    /*                                                                          */
    /* Arguments:                                                               */
    /*     None.                                                                */
    /*                                                                          */
    /* Returns:                                                                 */
    /*     void : Jsn_mnuMain and the menu fields are created and filled,       */
    /*            and the menu bar is added to the form.                        */
    /*--------------------------------------------------------------------------*/
    void Jsn_InitMenuStrip()
    {
        SVM_clsSaveMenu objSaveMenu;
        int i;
        float fPresetSize;
        ToolStripMenuItem tsmFont;     // View > Font Size submenu
        ToolStripMenuItem tsmLarger;   // Font Size > Larger
        ToolStripMenuItem tsmSmaller;  // Font Size > Smaller
        ToolStripMenuItem tsmReset;    // Font Size > Default
        ToolStripMenuItem tsmSize;     // one preset size item
        ToolStripMenuItem tsmItem;     // a File menu item with a shortcut

        /* The bar, filled menu by menu below and added to the form last. */
        Jsn_mnuMain = new MenuStrip();

        /* File menu.  The window edits one file: Reopen As reads it       */
        /* again as another format (when its name led to the wrong one,    */
        /* or it has no telling name); Save puts it back where it came     */
        /* from; Save As has a sub-menu, built by SVM_clsSaveMenu          */
        /* (EditorSaveMenu.cs), with one item for a destination in the     */
        /* image and one for a Windows file, each with its own handler;    */
        /* Close is the window's (its closing handler, Jsn_PresentClose,   */
        /* has unsaved changes asked about).                               */
        /* Each item gets its shortcut directly, so adding items never     */
        /* shifts one.                                                     */
        Jsn_tsmFileMenu = new ToolStripMenuItem("File");
        Jsn_tsmReopenAs = Jsn_FileFormatSubmenu("Reopen As");
        Jsn_tsmFileMenu.DropDownItems.Add(Jsn_tsmReopenAs);
        Jsn_tsmFileMenu.DropDownItems.Add(new ToolStripSeparator());
        tsmItem = new ToolStripMenuItem("Save", null, Jsn_OnSaveClick);
        tsmItem.ShortcutKeys = Keys.Control | Keys.S;
        Jsn_tsmFileMenu.DropDownItems.Add(tsmItem);
        tsmItem = new ToolStripMenuItem("Save As");
        objSaveMenu = new SVM_clsSaveMenu(tsmItem, Jsn_ReadCurrentFile, Jsn_OnSaveAsImageClicked,
            Jsn_OnSaveAsWindowsClicked);
        Jsn_tsmFileMenu.DropDownItems.Add(tsmItem);
        Jsn_tsmFileMenu.DropDownItems.Add(new ToolStripSeparator());
        tsmItem = new ToolStripMenuItem("Close", null, Jsn_OnCloseClick);
        tsmItem.ShortcutKeys = Keys.Control | Keys.W;
        Jsn_tsmFileMenu.DropDownItems.Add(tsmItem);

        /* Reopen As reads the file from disk: not while a save of it is on its way. */
        Jsn_tsmFileMenu.DropDownOpening += Jsn_OnFileMenuOpening;

        /* Edit menu: undo and redo of document changes. */
        Jsn_tsmEditMenu = new ToolStripMenuItem("Edit");
        Jsn_tsmEditMenu.DropDownItems.Add("Undo", null, Jsn_OnUndoClick);
        Jsn_tsmEditMenu.DropDownItems.Add("Redo", null, Jsn_OnRedoClick);

        /* Their keys, Ctrl+Z and Ctrl+Y, set on the two items just   */
        /* added (indices 0 and 1: nothing else is in the menu).      */
        ((ToolStripMenuItem)Jsn_tsmEditMenu.DropDownItems[0]).ShortcutKeys =
            Keys.Control | Keys.Z;
        ((ToolStripMenuItem)Jsn_tsmEditMenu.DropDownItems[1]).ShortcutKeys =
            Keys.Control | Keys.Y;

        /* Search menu: both items open the same window, in Find-only   */
        /* or Find + Replace layout.                                    */
        Jsn_tsmSearchMenu = new ToolStripMenuItem("Search");
        Jsn_tsmSearchMenu.DropDownItems.Add("Find...", null,
            Jsn_OnFindClick);
        Jsn_tsmSearchMenu.DropDownItems.Add("Find + Replace...", null,
            Jsn_OnFindReplaceClick);

        /* Their keys, Ctrl+F and Ctrl+H, the same way. */
        ((ToolStripMenuItem)Jsn_tsmSearchMenu.DropDownItems[0]).ShortcutKeys =
            Keys.Control | Keys.F;
        ((ToolStripMenuItem)Jsn_tsmSearchMenu.DropDownItems[1]).ShortcutKeys =
            Keys.Control | Keys.H;

        /* View menu: font size of the tree and the edit panel.  Keys   */
        /* .Oemplus is the "=" / "+" key, so Ctrl++ is shown as the     */
        /* caption rather than the enum's own name.                     */
        Jsn_tsmViewMenu = new ToolStripMenuItem("View");
        tsmFont = new ToolStripMenuItem("Font Size");

        /* Larger: next preset up. */
        tsmLarger = new ToolStripMenuItem("Larger",
            null, Jsn_OnFontLargerClick);
        tsmLarger.ShortcutKeys = Keys.Control | Keys.Oemplus;
        tsmLarger.ShortcutKeyDisplayString = "Ctrl++";
        tsmFont.DropDownItems.Add(tsmLarger);

        /* Smaller: next preset down. */
        tsmSmaller = new ToolStripMenuItem("Smaller",
            null, Jsn_OnFontSmallerClick);
        tsmSmaller.ShortcutKeys = Keys.Control | Keys.OemMinus;
        tsmSmaller.ShortcutKeyDisplayString = "Ctrl+-";
        tsmFont.DropDownItems.Add(tsmSmaller);

        /* Default: back to the standard WinForms control font size. */
        tsmReset = new ToolStripMenuItem("Default", null, Jsn_OnFontDefaultClick);
        tsmReset.ShortcutKeys = Keys.Control | Keys.D0;
        tsmFont.DropDownItems.Add(tsmReset);

        /* A divider between the three steps and the preset list. */
        tsmFont.DropDownItems.Add(new ToolStripSeparator());

        /* One item per preset size.  Each keeps its size in Tag, and        */
        /* Jsn_ViewApplyFontSize checks the item matching the current size   */
        /* through Jsn_lstFontSizeItems.                                     */
        for (i = 0; i < Jsn_afFontSizes.Length; i++)
        {   /* One preset per iteration: its caption in invariant digits   */
            /* ("10 pt", never "10,5"), its size in Tag for the shared     */
            /* click handler, and the item kept for the tick.  The size    */
            /* is taken into a local first, so each item's Tag holds its   */
            /* own value.                                                  */
            fPresetSize = Jsn_afFontSizes[i];
            tsmSize = new ToolStripMenuItem(
                fPresetSize.ToString(CultureInfo.InvariantCulture) + " pt");
            tsmSize.Tag = fPresetSize;
            tsmSize.Click += Jsn_OnFontPresetClick;
            Jsn_lstFontSizeItems.Add(tsmSize);
            tsmFont.DropDownItems.Add(tsmSize);
        }

        /* The Font Size sub-menu into View, first. */
        Jsn_tsmViewMenu.DropDownItems.Add(tsmFont);

        /* Show Raw Text in Tree: a tick item, saved with the settings. */
        Jsn_tsmShowRaw = new ToolStripMenuItem("Show Raw Text in Tree",
            null, Jsn_OnShowRawClick);
        Jsn_tsmViewMenu.DropDownItems.Add(Jsn_tsmShowRaw);

        /* Color Keys: names and values in their roles' colours; a tick   */
        /* item, saved too.  Colors... chooses those colours.             */
        Jsn_tsmColorKeys = new ToolStripMenuItem("Color Keys",
            null, Jsn_OnColorKeysClick);
        Jsn_tsmViewMenu.DropDownItems.Add(Jsn_tsmColorKeys);
        Jsn_tsmViewMenu.DropDownItems.Add("Colors...", null,
            Jsn_OnColorsClick);

        /* Menu bar order, left to right. */
        Jsn_mnuMain.Items.Add(Jsn_tsmFileMenu);
        Jsn_mnuMain.Items.Add(Jsn_tsmEditMenu);
        Jsn_mnuMain.Items.Add(Jsn_tsmSearchMenu);
        Jsn_mnuMain.Items.Add(Jsn_tsmViewMenu);
        Controls.Add(Jsn_mnuMain);
        MainMenuStrip = Jsn_mnuMain;
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_InitSplitContainer:                                                */
    /*                                                                        */
    /* Builds the splitter with the tree on its left and hooks up every       */
    /* tree event the editor uses.  The splitter position is kept as a        */
    /* RATIO (Jsn_dblSplitRatio) rather than a pixel count: dragging the bar  */
    /* records the new ratio, and resizing the window re-applies it, so       */
    /* the two panes keep their proportions.                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : Jsn_splMain and Jsn_tree are created and wired up.          */
    /*------------------------------------------------------------------------*/
    void Jsn_InitSplitContainer()
    {
        /* The splitter, filling the window, with neither pane fixed:   */
        /* both grow with the window, in the ratio kept below.          */
        Jsn_splMain = new SplitContainer();
        Jsn_splMain.Dock = DockStyle.Fill;
        Jsn_splMain.FixedPanel = FixedPanel.None;

        /* The user dragged the bar: remember where, as a fraction. */
        Jsn_splMain.SplitterMoved += Jsn_OnSplitterMoved;

        /* The tree, filling the left pane, its selection kept shown when    */
        /* the focus leaves it.  It is given the dialog font for the         */
        /* progress windows a long fill or clear of it opens; its own text   */
        /* takes the editor's font size setting (Jsn_ViewApplyFontSize).     */
        Jsn_tree = VTV_clsTreeView.VTV_Create(Jsn_fntDialog);
        Jsn_tree.Dock = DockStyle.Fill;
        Jsn_tree.HideSelection = false;

        /* The tree's progress windows name the document as this window's   */
        /* own do -- by the file's own name, not an image entry's           */
        /* temporary copy's -- through the one call it is given             */
        /* (VTV_BindDocName), which asks this window each time.  Then       */
        /* LabelEdit on, so values can be edited in place; but              */
        /* Jsn_OnTreeBeforeLabelEdit lets through only the edits that       */
        /* Jsn_TreeBeginInlineEdit starts.                                  */
        Jsn_tree.VTV_BindDocName(Jsn_OnTreeDocNameAsked);
        Jsn_tree.LabelEdit = true;

        /* Selection, in-place editing, keyboard and right-click   */
        /* handlers; each handler's header says what it does.      */
        Jsn_tree.BeforeSelect += Jsn_OnTreeBeforeSelect;
        Jsn_tree.AfterSelect += Jsn_OnTreeAfterSelect;
        Jsn_tree.BeforeLabelEdit += Jsn_OnTreeBeforeLabelEdit;
        Jsn_tree.AfterLabelEdit += Jsn_OnTreeAfterLabelEdit;
        Jsn_tree.NodeMouseDoubleClick += Jsn_OnTreeNodeMouseDoubleClick;
        Jsn_tree.KeyDown += Jsn_OnTreeKeyDown;
        Jsn_tree.MouseDown += Jsn_OnTreeMouseDown;

        /* The tree into the left pane, and the splitter onto the form   */
        /* (the constructor re-adds it in docking order).                */
        Jsn_splMain.Panel1.Controls.Add(Jsn_tree);
        Controls.Add(Jsn_splMain);

        /* The window was resized: put the bar back at the same ratio. */
        Resize += Jsn_OnResize;
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_InitEditPanel:                                                     */
    /*                                                                        */
    /* Builds the right-hand edit panel: Key, Type and Value, the Boolean     */
    /* checkbox that stands in for the Value box, the Comment box (the        */
    /* comment on the entry's line), the Raw box (the entry's text exactly    */
    /* as in the file), and the Save and Discard buttons.  No positions       */
    /* are set here: Jsn_EditLayoutPanel places everything from the current   */
    /* font and panel size.                                                   */
    /*                                                                        */
    /* Every editor marks the panel dirty when the USER changes it.           */
    /* Jsn_bSuppressPanelDirty is raised while Jsn_EditLoadPanel fills the    */
    /* panel from code, so loading an entry never looks like an edit.         */
    /* Typing in the Raw box locks the other fields, and typing in them locks */
    /* the Raw box, until Save or Discard (Jsn_EditMarkDirty).                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : Jsn_pnlEdit and its controls are created and wired up.      */
    /*------------------------------------------------------------------------*/
    void Jsn_InitEditPanel()
    {
        /* The panel, filling the splitter's right pane. */
        Jsn_pnlEdit = new Panel();
        Jsn_pnlEdit.Dock = DockStyle.Fill;

        /* Key row. */
        Jsn_lblKey = new Label();
        Jsn_lblKey.Text = "Key:";
        Jsn_lblKey.AutoSize = true;
        Jsn_pnlEdit.Controls.Add(Jsn_lblKey);

        /* The key's box: typing in it marks the fields dirty (not the   */
        /* Raw box -- false), through a handler of its own.              */
        Jsn_txtKey = new TextBox();
        Jsn_txtKey.TextChanged += Jsn_OnKeyChanged;
        Jsn_pnlEdit.Controls.Add(Jsn_txtKey);

        /* Type row.  Changing the type also swaps the value editor    */
        /* (text box or checkbox) through Jsn_EditUpdateValueEditor.   */
        Jsn_lblType = new Label();
        Jsn_lblType.Text = "Type:";
        Jsn_lblType.AutoSize = true;
        Jsn_pnlEdit.Controls.Add(Jsn_lblType);

        /* A list to choose from (no typing): the format's value         */
        /* kinds for the selected entry, filled by Jsn_EditFillTypeBox   */
        /* when an entry is loaded, then Comment for comment             */
        /* entries.  It starts with Comment alone.                       */
        Jsn_cmbType = new ComboBox();
        Jsn_cmbType.DropDownStyle = ComboBoxStyle.DropDownList;
        Jsn_lstTypeKinds = new List<int>();
        Jsn_cmbType.Items.Add("Comment");
        Jsn_cmbType.SelectedIndexChanged += Jsn_OnPanelTypeChanged;
        Jsn_pnlEdit.Controls.Add(Jsn_cmbType);

        /* Value row: a multi-line box for strings, numbers and null. */
        Jsn_lblValue = new Label();
        Jsn_lblValue.Text = "Value:";
        Jsn_lblValue.AutoSize = true;
        Jsn_pnlEdit.Controls.Add(Jsn_lblValue);

        /* The value's box: several lines, a vertical scroll bar, and   */
        /* typing marks the fields dirty.                               */
        Jsn_txtValue = new TextBox();
        Jsn_txtValue.Multiline = true;
        Jsn_txtValue.ScrollBars = ScrollBars.Vertical;
        Jsn_txtValue.TextChanged += Jsn_OnValueChanged;
        Jsn_pnlEdit.Controls.Add(Jsn_txtValue);

        /* Boolean editor.  It sits where the Value box is and is shown   */
        /* only while the type is Boolean.  Its caption follows its       */
        /* state, so it reads "true" or "false".                          */
        Jsn_chkBoolValue = new CheckBox();
        Jsn_chkBoolValue.Text = "true";
        Jsn_chkBoolValue.AutoSize = true;
        Jsn_chkBoolValue.Visible = false;
        Jsn_chkBoolValue.CheckedChanged += Jsn_OnPanelBooleanChanged;
        Jsn_pnlEdit.Controls.Add(Jsn_chkBoolValue);

        /* Comment row: the comment on the entry's line; empty = none. */
        Jsn_lblComment = new Label();
        Jsn_lblComment.Text = "Comment:";
        Jsn_lblComment.AutoSize = true;
        Jsn_pnlEdit.Controls.Add(Jsn_lblComment);

        /* The comment's box: typing marks the fields dirty. */
        Jsn_txtComment = new TextBox();
        Jsn_txtComment.TextChanged += Jsn_OnCommentChanged;
        Jsn_pnlEdit.Controls.Add(Jsn_txtComment);

        /* Raw row: the entry's text exactly as in the file, editable;   */
        /* no wrapping, so lines read as they are in the file.           */
        Jsn_lblRaw = new Label();
        Jsn_lblRaw.Text = "Raw:";
        Jsn_lblRaw.AutoSize = true;
        Jsn_pnlEdit.Controls.Add(Jsn_lblRaw);

        /* The Raw box: several lines, unwrapped, both scroll bars, and   */
        /* typing marks the RAW text dirty (true), which locks the        */
        /* other fields.                                                  */
        Jsn_txtRaw = new TextBox();
        Jsn_txtRaw.Multiline = true;
        Jsn_txtRaw.WordWrap = false;
        Jsn_txtRaw.ScrollBars = ScrollBars.Both;
        Jsn_txtRaw.TextChanged += Jsn_OnPanelRawChanged;
        Jsn_pnlEdit.Controls.Add(Jsn_txtRaw);

        /* Save: write the panel into the document. */
        Jsn_btnApply = new Button();
        Jsn_btnApply.Text = "Save";
        Jsn_btnApply.Click += Jsn_OnApplyClick;
        Jsn_pnlEdit.Controls.Add(Jsn_btnApply);

        /* Discard: throw away typed changes and reload the panel. */
        Jsn_btnDiscard = new Button();
        Jsn_btnDiscard.Text = "Discard";
        Jsn_btnDiscard.Click += Jsn_OnDiscardClick;
        Jsn_pnlEdit.Controls.Add(Jsn_btnDiscard);

        /* The panel into the splitter's right pane. */
        Jsn_splMain.Panel2.Controls.Add(Jsn_pnlEdit);

        /* Lay the panel out again whenever its size changes. */
        Jsn_pnlEdit.Resize += Jsn_OnPanelResize;
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_EditLayoutPanel:                                                   */
    /*                                                                        */
    /* Places every edit panel control from the current font and the          */
    /* panel's size; nothing in the panel has a fixed position.               */
    /*     o The caption column is as wide as the widest caption.             */
    /*     o Rows are spaced by the controls' real heights.                   */
    /*     o The Type box is just wide enough for its longest entry.          */
    /*     o Save and Discard sit bottom right, sized to the wider            */
    /*       caption.                                                         */
    /*     o The height between the Type row and the buttons, less the        */
    /*       Comment row, is shared by the Value box (two fifths) and the     */
    /*       Raw box (the rest).                                              */
    /* Called on every panel resize and after every font size change.         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the panel's controls are moved and resized.                 */
    /*------------------------------------------------------------------------*/
    void Jsn_EditLayoutPanel()
    {
        int iMargin;        // space between the panel edge and the controls
        int iGap;           // space between rows and between caption and field
        int iRowHeight;     // height of a single-line text box at this font
        int iLabelWidth;    // width of the caption column
        int iFieldLeft;     // x of the editors, right of the captions
        int iFieldWidth;    // width of the Key, Value, Comment and Raw boxes
        int iComboWidth;    // width of the Type box
        int iButtonWidth;   // width of each button
        int iButtonHeight;  // height of each button
        int iButtonY;       // y of the button row
        int iFreeHeight;    // height shared by the Value and Raw boxes
        int iValueHeight;   // height of the Value box
        int iRawHeight;     // height of the Raw box
        int i;
        int y;                                           // top of the row being placed

        if (Jsn_pnlEdit == null || Jsn_btnDiscard == null)
        {   /* Not every control exists yet: nothing to place.  A guard --   */
            /* the panel's Resize handler is attached only after the last    */
            /* control is made (Jsn_InitEditPanel).                          */
            return;
        }

        /* The fixed spacings, pixels: round the panel's edge, and between   */
        /* rows and between a caption and its field.                         */
        iMargin = 8;
        iGap = 6;

        /* A single-line text box's height follows its font; the   */
        /* captions and the checkbox are centred against it.       */
        iRowHeight = Jsn_txtKey.Height;

        /* Caption column and the editors to its right.  The editors   */
        /* never shrink below 60 pixels, however narrow the panel.     */
        iLabelWidth = Math.Max(Math.Max(Jsn_lblKey.PreferredWidth, Jsn_lblType.PreferredWidth),
            Math.Max(Math.Max(Jsn_lblValue.PreferredWidth, Jsn_lblComment.PreferredWidth),
            Jsn_lblRaw.PreferredWidth));
        iFieldLeft = iMargin + iLabelWidth + iGap;
        iFieldWidth = Jsn_pnlEdit.ClientSize.Width - iFieldLeft - iMargin - 4;
        if (iFieldWidth < 60)
        {   /* Panel narrower than the captions allow: keep the editors usable. */
            iFieldWidth = 60;
        }

        /* Key row. */
        y = iMargin + 4;
        Jsn_txtKey.SetBounds(iFieldLeft, y, iFieldWidth, iRowHeight);
        Jsn_lblKey.Location = new Point(iMargin,
            y + (iRowHeight - Jsn_lblKey.Height) / 2);
        y += iRowHeight + iGap;

        /* Type row.  The box is measured against its longest entry   */
        /* plus room for the drop-down arrow, never wider than the    */
        /* other editors.                                             */
        iComboWidth = 0;
        for (i = 0; i < Jsn_cmbType.Items.Count; i++)
        {   /* One entry per iteration: the widest so far kept. */
            iComboWidth = Math.Max(iComboWidth, TextRenderer.MeasureText(
                Jsn_cmbType.Items[i].ToString(), Jsn_cmbType.Font).Width);
        }
        iComboWidth += SystemInformation.VerticalScrollBarWidth + 12;
        if (iComboWidth > iFieldWidth)
        {   /* Narrow panel: the combo shrinks with the other editors. */
            iComboWidth = iFieldWidth;
        }

        /* The box at that width with its caption centred beside it, and      */
        /* the next row below the box's own height, which follows the font.   */
        Jsn_cmbType.SetBounds(iFieldLeft, y, iComboWidth, Jsn_cmbType.Height);
        Jsn_lblType.Location = new Point(iMargin,
            y + (Jsn_cmbType.Height - Jsn_lblType.Height) / 2);
        y += Jsn_cmbType.Height + iGap;

        /* Save and Discard: both as wide as the wider caption plus      */
        /* padding, never smaller than a standard 85 x 28 button, side   */
        /* by side at the bottom right with Save outermost.              */
        iButtonWidth = Math.Max(
            TextRenderer.MeasureText(Jsn_btnApply.Text, Jsn_btnApply.Font).Width,
            TextRenderer.MeasureText(Jsn_btnDiscard.Text, Jsn_btnDiscard.Font).Width)
            + 24;
        if (iButtonWidth < 85)
        {   /* Small font: keep the standard button width. */
            iButtonWidth = 85;
        }

        iButtonHeight = Jsn_btnApply.Font.Height + 12;
        if (iButtonHeight < 28)
        {   /* Small font: keep the standard button height. */
            iButtonHeight = 28;
        }

        iButtonY = Jsn_pnlEdit.ClientSize.Height - iButtonHeight - iMargin;
        Jsn_btnApply.SetBounds(Jsn_pnlEdit.ClientSize.Width - iButtonWidth - iMargin - 4,
            iButtonY, iButtonWidth, iButtonHeight);
        Jsn_btnDiscard.SetBounds(Jsn_btnApply.Left - iButtonWidth - iMargin,
            iButtonY, iButtonWidth, iButtonHeight);

        /* The height left for the Value and Raw boxes, after the Comment   */
        /* row and the gaps, never less than 60 for either.                 */
        iFreeHeight = iButtonY - 12 - y - (iRowHeight + iGap) - iGap;
        iValueHeight = iFreeHeight * 2 / 5;
        if (iValueHeight < 60)
        {   /* Short panel: keep a usable minimum height. */
            iValueHeight = 60;
        }

        /* The Raw box takes the rest, with the same minimum of its own: in   */
        /* a short panel the two together may then run past the buttons,      */
        /* and both stay usable rather than one shrinking to nothing.         */
        iRawHeight = iFreeHeight - iValueHeight;
        if (iRawHeight < 60)
        {   /* Short panel: keep a usable minimum height. */
            iRawHeight = 60;
        }

        /* Value row; the checkbox that stands in for the box is centred   */
        /* on its first line.                                              */
        Jsn_txtValue.SetBounds(iFieldLeft, y, iFieldWidth, iValueHeight);
        Jsn_lblValue.Location = new Point(iMargin,
            y + (iRowHeight - Jsn_lblValue.Height) / 2);
        Jsn_chkBoolValue.Location = new Point(iFieldLeft,
            y + (iRowHeight - Jsn_chkBoolValue.Height) / 2);
        y += iValueHeight + iGap;

        /* Comment row. */
        Jsn_txtComment.SetBounds(iFieldLeft, y, iFieldWidth, iRowHeight);
        Jsn_lblComment.Location = new Point(iMargin,
            y + (iRowHeight - Jsn_lblComment.Height) / 2);
        y += iRowHeight + iGap;

        /* Raw row. */
        Jsn_txtRaw.SetBounds(iFieldLeft, y, iFieldWidth, iRawHeight);
        Jsn_lblRaw.Location = new Point(iMargin,
            y + (iRowHeight - Jsn_lblRaw.Height) / 2);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_EditMarkDirty:                                                     */
    /*                                                                        */
    /* Called when an editor's text or state changes.  When the USER made     */
    /* the change (not Jsn_EditLoadPanel), the panel now differs from the     */
    /* document; and since a Raw edit and the other fields cannot both be     */
    /* saved, the first one edited locks the other until Save or Discard.     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bRaw : the change was in the Raw box.                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the dirty flags are set and the other side locked.          */
    /*------------------------------------------------------------------------*/
    void Jsn_EditMarkDirty(bool bRaw)
    {
        if (Jsn_bSuppressPanelDirty)
        {   /* Loaded by code: not an edit. */
            return;
        }

        if (bRaw && !Jsn_bPanelDirty)
        {   /* The first edit is in the Raw box: lock the other fields. */
            Jsn_bRawDirty = true;
            Jsn_txtKey.ReadOnly = true;
            Jsn_cmbType.Enabled = false;
            Jsn_txtValue.ReadOnly = true;
            Jsn_chkBoolValue.Enabled = false;
            Jsn_txtComment.ReadOnly = true;
        }
        else if (!bRaw && !Jsn_bPanelDirty)
        {   /* The first edit is in the other fields: lock the Raw box. */
            Jsn_txtRaw.ReadOnly = true;
        }

        /* Either way the panel now differs from the document (a later   */
        /* edit locks nothing more: the first one decided the side).     */
        Jsn_bPanelDirty = true;
    }

    /*------------------------------------------------------------------------*/
    /* VIEW: font size and the raw-text option                                */
    /*------------------------------------------------------------------------*/

    /*------------------------------------------------------------------------*/
    /* Jsn_ViewApplyFontSize:                                                 */
    /*                                                                        */
    /* Sets the font size used by the tree and the edit panel, clamped to     */
    /* JSN_FONT_MIN..JSN_FONT_MAX, and moves the View menu's tick.            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     fSize : requested size, points.                                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : Jsn_fFontSize, Jsn_fntView, the tree, the panel and the     */
    /*            menu check marks are updated.                               */
    /*------------------------------------------------------------------------*/
    void Jsn_ViewApplyFontSize(float fSize)
    {
        int i;
        ToolStripMenuItem tsmItem;
        Font fntOld;                                    // font being replaced, disposed afterwards
        Font fntOldRaw;                                 // Raw box font being replaced

        if (fSize < JSN_FONT_MIN)
        {   /* Too small to read: use the smallest allowed. */
            fSize = JSN_FONT_MIN;
        }

        if (fSize > JSN_FONT_MAX)
        {   /* Too large to be useful: use the largest allowed. */
            fSize = JSN_FONT_MAX;
        }

        /* The size in force from now (Jsn_CfgSave stores it). */
        Jsn_fFontSize = fSize;

        /* Swap in the new fonts, then dispose the old ones: the tree and    */
        /* panel no longer refer to them.  The panel's controls inherit      */
        /* their font from the panel, so one assignment covers them all --   */
        /* except the Raw box, which gets a fixed-pitch font of the same     */
        /* size so the file's own layout lines up.                           */
        fntOld = Jsn_fntView;
        fntOldRaw = Jsn_fntRaw;
        Jsn_fntView = new Font(Control.DefaultFont.FontFamily, fSize);
        Jsn_fntRaw = new Font(FontFamily.GenericMonospace, fSize);
        Jsn_tree.Font = Jsn_fntView;
        Jsn_pnlEdit.Font = Jsn_fntView;
        Jsn_txtRaw.Font = Jsn_fntRaw;
        if (fntOld != null)
        {   /* There was a previous font (not on the very first call): release it. */
            fntOld.Dispose();
        }

        if (fntOldRaw != null)
        {   /* And the previous Raw box font. */
            fntOldRaw.Dispose();
        }

        /* Text sizes changed, so every position in the panel changed. */
        Jsn_EditLayoutPanel();

        /* Tick the preset matching the new size; none is ticked for a   */
        /* size that is not a preset, such as the 8.25 pt default.       */
        for (i = 0; i < Jsn_lstFontSizeItems.Count; i++)
        {   /* One preset item per iteration: ticked when its size is     */
            /* this one, within 0.01 pt of float noise; its size is the   */
            /* one its Tag was given when the menu was built.             */
            tsmItem = Jsn_lstFontSizeItems[i];
            tsmItem.Checked = (Math.Abs((float)tsmItem.Tag - Jsn_fFontSize) < 0.01f);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_ViewStepFontSize:                                                  */
    /*                                                                        */
    /* Moves to the next larger or next smaller preset size.  It works        */
    /* from any current size, including the 8.25 pt default that is not       */
    /* itself a preset.  At either end of the list nothing changes.           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iDirection : above zero for larger, zero or below for smaller.     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the font size may change through Jsn_ViewApplyFontSize.     */
    /*------------------------------------------------------------------------*/
    void Jsn_ViewStepFontSize(int iDirection)
    {
        int i;

        if (iDirection > 0)
        {   /* Larger: the first preset above the current size (0.01 pt tolerance for float noise). */
            for (i = 0; i < Jsn_afFontSizes.Length; i++)
            {   /* One preset per iteration, smallest first. */
                if (Jsn_afFontSizes[i] > Jsn_fFontSize + 0.01f)
                {   /* Found it. */
                    Jsn_ViewApplyFontSize(Jsn_afFontSizes[i]);
                    return;
                }
            }
        }
        else
        {   /* Smaller: the last preset below the current size. */
            for (i = Jsn_afFontSizes.Length - 1; i >= 0; i--)
            {   /* One preset per iteration, largest first. */
                if (Jsn_afFontSizes[i] < Jsn_fFontSize - 0.01f)
                {   /* Found it. */
                    Jsn_ViewApplyFontSize(Jsn_afFontSizes[i]);
                    return;
                }
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_ViewToggleShowRaw:                                                 */
    /*                                                                        */
    /* View > Show Raw Text in Tree: switches every tree label between        */
    /* the friendly form ("key: value") and the entry's first line of         */
    /* text exactly as in the file.  The setting is saved with the window     */
    /* settings (Jsn_CfgSave).                                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the menu tick and every label change.                       */
    /*------------------------------------------------------------------------*/
    void Jsn_ViewToggleShowRaw()
    {
        /* The tick flipped, and the tree told to relabel every node. */
        Jsn_tsmShowRaw.Checked = !Jsn_tsmShowRaw.Checked;
        Jsn_tree.VTV_SetShowRaw(Jsn_tsmShowRaw.Checked);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_ViewToggleColorKeys:                                               */
    /*                                                                        */
    /* View > Color Keys: switches the tree between the normal text colour    */
    /* throughout and the colour scheme -- names (keys,                       */
    /* indexes, "root", section and key paths) by role, values by type, as    */
    /* chosen in View > Colors... (Jsn_ViewShowColors).  Repeated keys stay   */
    /* red.  The setting is saved with the window settings (Jsn_CfgSave).     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the menu tick changes and the tree repaints.                */
    /*------------------------------------------------------------------------*/
    void Jsn_ViewToggleColorKeys()
    {
        /* The tick flipped, and the tree told to repaint in or out of colour. */
        Jsn_tsmColorKeys.Checked = !Jsn_tsmColorKeys.Checked;
        Jsn_tree.VTV_SetColorKeys(Jsn_tsmColorKeys.Checked);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_ViewShowColors:                                                    */
    /*                                                                        */
    /* View > Colors...: the Colors dialog (VCO_clsColorsForm) with the       */
    /* tree's colours now; on OK its colours replace the tree's, the tree     */
    /* repaints, and they are stored in the registry (Jsn_CfgSaveColors).     */
    /* Cancel changes nothing.  The dialog is EDITOR-MODAL -- the main window */
    /* and every other editor carry on while it is up -- and its outcome is   */
    /* applied by the window's queue once it closes (Jsn_ViewColorsAnswer).   */
    /* An optional hook can supply its decision through Jsn_ViewHookColors,   */
    /* which is then applied at once.                                         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the colours are changed and saved, or nothing changes.      */
    /*------------------------------------------------------------------------*/
    void Jsn_ViewShowColors()
    {
        VCO_clsColorsForm frmColors;  // the dialog
        bool bAnswered;               // optional hook supplied the dialog decision

        if (Jsn_frmColors != null)
        {   /* A guard: the window is disabled while its Colors dialog is   */
            /* up, so its menu cannot post a second, and a second posted    */
            /* before the first ran is dropped by the queue as a            */
            /* duplicate (WQU_Post, WindowQueue.cs) -- nothing more.        */
            return;
        }

        /* The dialog's text in the program's dialog font, which this        */
        /* editor was given: never the editor window's own font, which is    */
        /* WinForms' smaller inherited default and would put this dialog's   */
        /* text below every other dialog's.  The dialog derives its title    */
        /* and heading fonts from it and frees only those.                   */
        frmColors = VCO_clsColorsForm.VCO_Create(Jsn_fntDialog, Jsn_tree.VTV_ReadNameColors(),
            Jsn_tree.VTV_ReadValueColors(), Jsn_ViewPickColor);

        /* The partial hook first (none in the program, so the call            */
        /* compiles to nothing and bAnswered stays false).  A test's hook      */
        /* answers at once, so its outcome is applied here, the dialog never   */
        /* shown.                                                              */
        bAnswered = false;
        Jsn_ViewHookColors(frmColors, ref bAnswered);
        if (bAnswered)
        {   /* Answered by the hook: applied now, the dialog released. */
            Jsn_ViewApplyColors(frmColors);
            frmColors.Dispose();
            return;
        }

        /* The user's choice, EDITOR-MODAL: the dialog under this window's   */
        /* (the program's) icon, kept as the window's own work until its     */
        /* answer is applied (JSN_HasLocalWork, the close's keep-open        */
        /* test), its closing only posting (Jsn_OnColorsClosed); this        */
        /* window disabled, the dialog shown modeless and owned by it, so    */
        /* the main window and every other editor carry on.  It is placed    */
        /* over the middle of this window first (PRM_CentreOver,             */
        /* PromptForm.cs): the framework centres only a ShowDialog.          */
        Jsn_frmColors = frmColors;
        frmColors.Icon = this.Icon;
        frmColors.FormClosed += Jsn_OnColorsClosed;
        PRM_clsPromptForm.PRM_CentreOver(frmColors, this);
        this.Enabled = false;
        frmColors.Show(this);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnColorsClosed:                                                    */
    /*                                                                        */
    /* The Colors dialog has closed (OK, Cancel or its close box): its        */
    /* outcome posted to the window's queue, which applies it                 */
    /* (Jsn_ViewColorsAnswer).  Nothing else is done here.                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the dialog.                                            */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the outcome posted.                                         */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnColorsClosed(object objSender, FormClosedEventArgs evArgs)
    {
        /* Posted with the dialog it came from. */
        Jsn_objQueue.WQU_Post((int)Jsn_enmItem.ColorsAnswer, objSender);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_ViewColorsAnswer:                                                  */
    /*                                                                        */
    /* The Colors dialog's outcome, run by the window's queue: the window     */
    /* enabled again, the outcome applied (Jsn_ViewApplyColors) and the       */
    /* dialog released; a close that waited for it is then told it may go     */
    /* on.                                                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     frmColors : the dialog that closed.                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the colours applied, or nothing for a cancel.               */
    /*------------------------------------------------------------------------*/
    private void Jsn_ViewColorsAnswer(VCO_clsColorsForm frmColors)
    {
        if (frmColors != Jsn_frmColors)
        {   /* A guard: not the dialog this window has up -- nothing to   */
            /* apply.                                                     */
            return;
        }

        /* The window enabled again, then the outcome, then the dialog   */
        /* released (its readers are read before it goes).               */
        Jsn_frmColors = null;
        this.Enabled = true;
        this.Activate();
        Jsn_ViewApplyColors(frmColors);
        frmColors.Dispose();

        /* The window's own work has returned: a close question that   */
        /* waited for the dialog is looked at again.                   */
        Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_ViewApplyColors:                                                   */
    /*                                                                        */
    /* A closed Colors dialog's outcome applied: on OK the tree's colours     */
    /* replaced by the chosen ones, shown at once and kept for next time;     */
    /* on Cancel nothing.                                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     frmColors : the dialog, closed but not yet disposed.               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the colours applied, or nothing.                            */
    /*------------------------------------------------------------------------*/
    private void Jsn_ViewApplyColors(VCO_clsColorsForm frmColors)
    {
        bool bSaved;                  // OK was pressed
        Color[] arrNames;             // the chosen name colours
        Color[] arrValues;            // the chosen value colours

        /* The outcome, read from the dialog. */
        bSaved = frmColors.VCO_ReadSaved();
        arrNames = frmColors.VCO_ReadNameColors();
        arrValues = frmColors.VCO_ReadValueColors();

        if (!bSaved)
        {   /* Cancelled: nothing changes. */
            return;
        }

        /* The tree's tables take the chosen colours (the tables      */
        /* themselves stay: the tree holds them), shown at once and   */
        /* kept for next time.                                        */
        Array.Copy(arrNames, Jsn_tree.VTV_ReadNameColors(), Jsn_tree.VTV_ReadNameColors().Length);
        Array.Copy(arrValues, Jsn_tree.VTV_ReadValueColors(), Jsn_tree.VTV_ReadValueColors().Length);
        Jsn_tree.Invalidate();
        Jsn_CfgSaveColors();
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_ViewPickColor:                                                     */
    /*                                                                        */
    /* The Colors dialog's colour picker (a VCO_PickColor): the standard      */
    /* Windows colour dialog (ColorDialog, fully open so any colour can be    */
    /* mixed), starting at the role's colour now, owned by the Colors dialog; */
    /* or an optional answer (Jsn_DlgHookColor). The common dialog reports    */
    /* OK only through its DialogResult, which is therefore what is read      */
    /* here -- the editor's own dialogs report through a field of their own   */
    /* instead (the Colors dialog's Vco_bSaved).                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     frmOwner  : the Colors dialog, which owns the picker.              */
    /*     sRole     : the role being picked for (Vco_asRoleKeys).            */
    /*     clrNow    : the role's colour before this choice.                  */
    /*     clrPicked : the colour chosen; ignored when false is returned.     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when a colour was picked, false for Cancel.            */
    /*------------------------------------------------------------------------*/
    bool Jsn_ViewPickColor(Form frmOwner, string sRole, Color clrNow,
        out Color clrPicked)
    {
        ColorDialog dlgColor;  // the Windows colour dialog
        bool bAnswered;        // optional hook supplied a color decision
        bool bPicked;          // a colour was picked
        Color clrAnswer;       // the colour picked

        /* The partial hook first, from "no answer, not picked, the colour   */
        /* as it is" (none in the program, so the call compiles to           */
        /* nothing and the Windows picker below is shown).                   */
        bAnswered = false;
        bPicked = false;
        clrAnswer = clrNow;
        Jsn_DlgHookColor(sRole, clrNow, ref bAnswered, ref bPicked, ref clrAnswer);
        if (bAnswered)
        {   /* A hook answered: its answer, with no picker shown. */
            clrPicked = clrAnswer;
            return(bPicked);
        }

        /* The colour stays unless one is picked; the Windows dialog,   */
        /* disposed when the block ends.                                */
        clrPicked = clrNow;
        dlgColor = new ColorDialog();
        using (dlgColor)
        {   /* Starting at the colour now, fully open, any colour allowed. */
            dlgColor.Color = clrNow;
            dlgColor.FullOpen = true;
            dlgColor.AnyColor = true;
            if (dlgColor.ShowDialog(frmOwner) == DialogResult.OK)
            {   /* Picked: opaque, as the dialog gives it. */
                clrPicked = Color.FromArgb(255, dlgColor.Color);
                return(true);
            }
        }

        /* Cancelled: the colour stays. */
        return(false);
    }

    /*---------------------------------------------------------------------------*/
    /* Jsn_InitStatusStrip:                                                      */
    /*                                                                           */
    /* Builds the status bar: the open file's full path, stretched to            */
    /* fill, then the document's format, a "Contains comments" marker and a      */
    /* "Modified" marker on the right.  Jsn_StateUpdateTitle keeps them current. */
    /*                                                                           */
    /* Arguments:                                                                */
    /*     None.                                                                 */
    /*                                                                           */
    /* Returns:                                                                  */
    /*     void : Jsn_stsStatus and its labels are created and added.            */
    /*---------------------------------------------------------------------------*/
    void Jsn_InitStatusStrip()
    {
        /* The strip, its four labels made below and added in order. */
        Jsn_stsStatus = new StatusStrip();

        /* File path: Spring makes it take all the width the markers   */
        /* do not, left-aligned.                                       */
        Jsn_tslFilePath = new ToolStripStatusLabel();
        Jsn_tslFilePath.Spring = true;
        Jsn_tslFilePath.TextAlign = ContentAlignment.MiddleLeft;

        /* The document's format ("JSON", "INI" ...). */
        Jsn_tslFormat = new ToolStripStatusLabel();
        Jsn_tslFormat.Text = "";

        /* "Contains comments", empty while the document has none. */
        Jsn_tslComments = new ToolStripStatusLabel();
        Jsn_tslComments.Text = "";

        /* "Modified" marker, empty while the document is saved. */
        Jsn_tslModified = new ToolStripStatusLabel();
        Jsn_tslModified.Text = "";
        Jsn_tslModified.Width = 80;

        /* Left to right, and the strip onto the form (re-added in   */
        /* docking order by the constructor).                        */
        Jsn_stsStatus.Items.Add(Jsn_tslFilePath);
        Jsn_stsStatus.Items.Add(Jsn_tslFormat);
        Jsn_stsStatus.Items.Add(Jsn_tslComments);
        Jsn_stsStatus.Items.Add(Jsn_tslModified);
        Controls.Add(Jsn_stsStatus);
    }

    /*-------------------------------------------------------------------------*/
    /* Jsn_InitContextMenu:                                                    */
    /*                                                                         */
    /* Attaches an empty right-click menu to the tree.  Its items depend       */
    /* on the entry clicked, so the window queue builds them each time before  */
    /* it shows the menu (Jsn_ShowContextMenu, Jsn_ContextBuild).              */
    /*                                                                         */
    /* Arguments:                                                              */
    /*     None.                                                               */
    /*                                                                         */
    /* Returns:                                                                */
    /*     void : Jsn_mnuTreeContext is created and attached to the tree.      */
    /*-------------------------------------------------------------------------*/
    void Jsn_InitContextMenu()
    {
        /* Empty now; the window queue fills and shows it for each right   */
        /* click or menu key, the tree's own opening being turned into a   */
        /* request (Jsn_OnContextOpening).                                 */
        Jsn_mnuTreeContext = new ContextMenuStrip();
        Jsn_mnuTreeContext.Opening += Jsn_OnContextOpening;
        Jsn_tree.ContextMenuStrip = Jsn_mnuTreeContext;
    }

    /*------------------------------------------------------------------------*/
    /* CTX: the tree's right-click menu                                       */
    /*------------------------------------------------------------------------*/

    /*------------------------------------------------------------------------*/
    /* Jsn_OnContextOpening:                                                  */
    /*                                                                        */
    /* The right-click menu is about to open.  It opens only when the window  */
    /* queue's own menu item shows it (Jsn_ShowContextMenu, which raises      */
    /* this event inside its Show): that opening is let through.  Any other   */
    /* opening -- the tree's own, for a right click or the menu key -- is     */
    /* kept closed, and a request for the menu is posted to the window's      */
    /* queue instead, carrying what the gesture was for, so the panel's       */
    /* question, the selection and the menu's items are all done by the       */
    /* queue's run, never here.                                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the context menu; not used.                            */
    /*     evArgs    : its Cancel set to keep the menu closed.                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the opening let through, or cancelled and a request posted. */
    /*------------------------------------------------------------------------*/
    void Jsn_OnContextOpening(object objSender, System.ComponentModel.CancelEventArgs evArgs)
    {
        JCR_clsContextRequest objRequest;  // what the gesture was for
        List<int> lstPath;                 // the entry it is for, or null

        if (Jsn_bContextShowing)
        {   /* The queue's own showing: the menu is built; let it open.  The   */
            /* mark is lowered by the queue's run once its Show returns.       */
            return;
        }

        /* Any other opening stays closed. */
        evArgs.Cancel = true;

        if (Jsn_bContextFromMouse)
        {   /* A right click (Jsn_OnTreeMouseDown noted it): the entry it     */
            /* was on, or none for empty space, and where it was.  The mark   */
            /* is read once and lowered.                                      */
            Jsn_bContextFromMouse = false;
            objRequest = new JCR_clsContextRequest(Jsn_lstContextPath, Jsn_ptContext);
        }
        else
        {   /* The menu key or Shift+F10: for the selected entry, shown at     */
            /* the left of its row, just below it (or at the tree's top-left   */
            /* with none selected).                                            */
            lstPath = null;
            if (Jsn_tree.SelectedNode != null && Jsn_tree.SelectedNode.Tag != null)
            {   /* An entry is selected: its path, taken now. */
                lstPath = VTV_clsTreeView.VTV_PathOf(Jsn_tree.SelectedNode);
                objRequest = new JCR_clsContextRequest(lstPath,
                    new Point(Jsn_tree.SelectedNode.Bounds.Left, Jsn_tree.SelectedNode.Bounds.Bottom));
            }
            else
            {   /* Nothing selected: the top-level additions, at the tree's   */
                /* top-left.                                                  */
                objRequest = new JCR_clsContextRequest(null, new Point(0, 0));
            }
        }

        /* The menu requested from the window's queue. */
        Jsn_objQueue.WQU_Post((int)Jsn_enmItem.ContextMenu, objRequest);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_ShowContextMenu:                                                   */
    /*                                                                        */
    /* The window queue's right-click menu: settle the edit panel (its        */
    /* question, when it has unsaved changes), select the entry the gesture   */
    /* was for, build the menu for it and show it where the gesture was.  A   */
    /* Cancel at the panel's question ends the gesture there: no selection    */
    /* change and no menu, so the question is never asked twice and nothing   */
    /* is offered for the entry still selected.                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objArgs : the JCR_clsContextRequest the opening posted.            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the menu shown, or nothing when the gesture ended.          */
    /*------------------------------------------------------------------------*/
    void Jsn_ShowContextMenu(object objArgs)
    {
        JCR_clsContextRequest objRequest;  // what the gesture was for
        bool bEmptySpace;                  // the menu is for empty space

        objRequest = (JCR_clsContextRequest)objArgs;

        /* The panel's typing settled first, since selecting reloads it   */
        /* and the menu's actions read the document.                      */
        if (!Jsn_EditConfirmPendingPanel())
        {   /* Cancelled, or the panel's values would not save: the        */
            /* gesture ends -- the selection stays, the typing kept, and   */
            /* no menu.                                                    */
            return;
        }

        /* Empty space, or an entry: one found again by its path, since    */
        /* saving the panel may have changed the tree (or, if the path     */
        /* no longer names one, its nearest existing ancestor --           */
        /* VTV_Select), and selected (a selection from code, which         */
        /* Jsn_OnTreeBeforeSelect lets through) so the menu is for it --   */
        /* the control itself does not move the selection on a right       */
        /* click (as found).                                               */
        bEmptySpace = (objRequest.JCR_lstPath == null);
        if (!bEmptySpace)
        {   /* An entry: selected. */
            Jsn_tree.VTV_Select(objRequest.JCR_lstPath);
        }

        /* The menu built for what it is for. */
        Jsn_ContextBuild(bEmptySpace);

        if (Jsn_mnuTreeContext.Items.Count == 0)
        {   /* Nothing to offer here: no menu. */
            return;
        }

        /* Shown where the gesture was; the mark lets its own Opening    */
        /* through (Jsn_OnContextOpening) and is lowered once Show has   */
        /* raised it, so no later opening is let through by mistake.     */
        Jsn_bContextShowing = true;
        Jsn_mnuTreeContext.Show(Jsn_tree, objRequest.JCR_ptAt);
        Jsn_bContextShowing = false;
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_ContextBuild:                                                      */
    /*                                                                        */
    /* Builds the right-click menu for the selected entry, or for empty       */
    /* space, before Jsn_ShowContextMenu shows it.                            */
    /* The actions are handed the entry's POSITION PATH, which names the      */
    /* same entry until the document changes.                                 */
    /*     o Objects and arrays get Add Child (one item per kind the          */
    /*       format offers there -- Vf_ChildKinds -- and Comment).            */
    /*     o Object members get Rename Key.  (An entry that names another     */
    /*       file -- a Klipper-family include -- gets no "open it" item here: */
    /*       the file it names lives beside the document inside the image,    */
    /*       which this window cannot reach; the main window's panes are      */
    /*       where it is opened.)                                             */
    /*     o Every entry gets Copy; objects and arrays also get Paste once    */
    /*       something has been copied.                                       */
    /*     o Top-level entries get the top-level additions                    */
    /*       (Jsn_ContextAddTopLevel: Add Top-Level Comment for JSON, an Add  */
    /*       Top-Level submenu where the format has top-level kinds, such as  */
    /*       INI's sections), and so does a right click on empty space or an  */
    /*       empty document, where they are all the menu offers.              */
    /*     o Everything the format lets go gets Delete (Vf_RemoveProblem:     */
    /*       JSON keeps the top value).                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bEmptySpace : true when the gesture was on empty space (or an      */
    /*                   unopened container's placeholder): only the          */
    /*                   top-level additions are offered.                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : Jsn_mnuTreeContext is refilled -- left empty when nothing   */
    /*            can be offered, which the caller takes as no menu.          */
    /*------------------------------------------------------------------------*/
    void Jsn_ContextBuild(bool bEmptySpace)
    {
        TreeNode nodTree;          // node the menu is for
        VoorheesItem objItem;      // its entry
        ToolStripMenuItem tsmAdd;  // Add Child submenu
        List<int> lstPath;         // position path handed to the actions
        List<int> lstKinds;        // value kinds Add Child offers here
        bool bContainer;           // the entry is a container (object, array)
        JCA_clsContextChoice objChoice;
        ToolStripMenuItem tsmChoice;
        int i;

        /* Last time's items gone. */
        Jsn_mnuTreeContext.Items.Clear();

        /* The entry the menu is for: the one Jsn_ShowContextMenu has just   */
        /* selected.                                                         */
        nodTree = Jsn_tree.SelectedNode;
        if (bEmptySpace || nodTree == null || nodTree.Tag == null)
        {   /* Empty space, or nothing selected (an empty document): only the top-level additions. */
            Jsn_ContextAddTopLevel();
            return;
        }

        /* The selected node's position path, and the entry the document   */
        /* has there now.                                                  */
        lstPath = VTV_clsTreeView.VTV_PathOf(nodTree);
        objItem = Jsn_doc.Vd_ItemAt(lstPath);
        if (objItem == null)
        {   /* Tree out of step with the document: offer nothing rather than act on the wrong entry. */
            return;
        }

        /* A value that holds others (an object or array, a section). */
        bContainer = objItem.Vi_IsValue() && objItem.Vi_node.Vn_IsContainer();

        if (bContainer)
        {   /* A container: it can take a new child of each kind its format offers there, or a comment. */
            tsmAdd = new ToolStripMenuItem("Add Child");
            lstKinds = VoorheesFormat.Vf_ChildKinds(Jsn_doc, objItem.Vi_node);
            for (i = 0; i < lstKinds.Count; i++)
            {   /* One kind per iteration, through Jsn_ContextAddKindItem,   */
                /* which gives each entry its own kind in its Tag.           */
                Jsn_ContextAddKindItem(tsmAdd, lstPath, lstKinds[i]);
            }

            /* A comment entry, set apart. */
            if (lstKinds.Count > 0)
            {   /* Kinds above it: a separator between them and Comment. */
                tsmAdd.DropDownItems.Add(new ToolStripSeparator());
            }

            /* The Comment entry, its container's path and the comment kind   */
            /* in its Tag, which the shared Add Child handler reads from      */
            /* the entry clicked.                                             */
            objChoice = new JCA_clsContextChoice(lstPath, JSN_KIND_COMMENT);
            tsmChoice = new ToolStripMenuItem("Comment...", null, Jsn_OnContextAddChild);
            tsmChoice.Tag = objChoice;
            tsmAdd.DropDownItems.Add(tsmChoice);
            Jsn_mnuTreeContext.Items.Add(tsmAdd);
        }

        if (objItem.Vi_iKind == VoorheesItem.VI_MEMBER)
        {   /* Object member: offer Rename Key. */
            tsmChoice = new ToolStripMenuItem("Rename Key...", null, Jsn_OnContextRename);
            tsmChoice.Tag = lstPath;
            Jsn_mnuTreeContext.Items.Add(tsmChoice);
        }

        /* Anything can be copied. */
        tsmChoice = new ToolStripMenuItem("Copy", null, Jsn_OnContextCopy);
        tsmChoice.Tag = lstPath;
        Jsn_mnuTreeContext.Items.Add(tsmChoice);

        if (Jsn_clipboardItem != null && bContainer)
        {   /* Something was copied and this entry can hold it: offer Paste. */
            tsmChoice = new ToolStripMenuItem("Paste", null, Jsn_OnContextPaste);
            tsmChoice.Tag = lstPath;
            Jsn_mnuTreeContext.Items.Add(tsmChoice);
        }

        if (lstPath.Count == 1)
        {   /* A top-level entry: things can be added at the top level too. */
            Jsn_ContextAddTopLevel();
        }

        if (VoorheesFormat.Vf_RemoveProblem(Jsn_doc,
            Jsn_doc.Vd_NodeAt(lstPath.GetRange(0, lstPath.Count - 1)), objItem) == null)
        {   /* The format lets it go (JSON keeps the top value, the document's one value): offer Delete, set apart. */
            Jsn_mnuTreeContext.Items.Add(new ToolStripSeparator());
            tsmChoice = new ToolStripMenuItem("Delete", null, Jsn_OnContextDelete);
            tsmChoice.Tag = lstPath;
            Jsn_mnuTreeContext.Items.Add(tsmChoice);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_ContextAddTopLevel:                                                */
    /*                                                                        */
    /* Adds the top-level additions to the right-click menu: where the        */
    /* format offers kinds at the top level (Vf_ChildKinds of the document:   */
    /* INI's entries and sections), an "Add Top-Level" submenu with them and  */
    /* Comment...; where it offers none (JSON, whose document holds its one   */
    /* value and comments), "Add Top-Level Comment...".                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : Jsn_mnuTreeContext gains one item.                          */
    /*------------------------------------------------------------------------*/
    void Jsn_ContextAddTopLevel()
    {
        ToolStripMenuItem tsmAdd;  // Add Top-Level submenu
        List<int> lstKinds;        // kinds the top level takes
        List<int> lstDocument;     // the document's path (empty)
        ToolStripMenuItem tsmChoice;
        JCA_clsContextChoice objChoice;
        int i;

        /* The document's own path (empty), and the kinds its top level   */
        /* takes.                                                         */
        lstDocument = new List<int>();
        lstKinds = VoorheesFormat.Vf_ChildKinds(Jsn_doc, Jsn_doc.Vd_root);
        if (lstKinds.Count == 0)
        {   /* Comments only (JSON). */
            objChoice = new JCA_clsContextChoice(lstDocument, JSN_KIND_COMMENT);
            tsmChoice = new ToolStripMenuItem("Add Top-Level Comment...", null, Jsn_OnContextAddChild);
            tsmChoice.Tag = objChoice;
            Jsn_mnuTreeContext.Items.Add(tsmChoice);
            return;
        }

        /* Each kind, then Comment, set apart. */
        tsmAdd = new ToolStripMenuItem("Add Top-Level");
        for (i = 0; i < lstKinds.Count; i++)
        {   /* One kind per iteration, each entry given its own kind. */
            Jsn_ContextAddKindItem(tsmAdd, lstDocument, lstKinds[i]);
        }

        /* Then a separator and Comment, for the same (empty) document path,   */
        /* and the sub-menu into the menu.                                     */
        tsmAdd.DropDownItems.Add(new ToolStripSeparator());
        objChoice = new JCA_clsContextChoice(lstDocument, JSN_KIND_COMMENT);
        tsmChoice = new ToolStripMenuItem("Comment...", null, Jsn_OnContextAddChild);
        tsmChoice.Tag = objChoice;
        tsmAdd.DropDownItems.Add(tsmChoice);
        Jsn_mnuTreeContext.Items.Add(tsmAdd);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_ContextAddKindItem:                                                */
    /*                                                                        */
    /* Add one "add a value of this kind" entry to an Add Child or Add        */
    /* Top-Level sub-menu: captioned with the format's name for the kind,     */
    /* its container's path and the kind in its Tag (a JCA_clsContextChoice), */
    /* so the one shared handler (Jsn_OnContextAddChild) knows from the entry */
    /* clicked what to add and where.                                         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     tsmParent : the sub-menu.                                          */
    /*     lstPath   : the container's position path; empty for the document. */
    /*     iKind     : the value kind to add, one of the format's.            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the entry is added to the sub-menu.                         */
    /*------------------------------------------------------------------------*/
    private void Jsn_ContextAddKindItem(ToolStripMenuItem tsmParent, List<int> lstPath, int iKind)
    {
        ToolStripMenuItem tsmChoice;
        JCA_clsContextChoice objChoice;

        /* The entry: the format's own name for the kind as its caption, the    */
        /* shared handler, and the path and kind in its Tag.  The path is the   */
        /* menu's, shared by all its entries and changed by none.               */
        objChoice = new JCA_clsContextChoice(lstPath, iKind);
        tsmChoice = new ToolStripMenuItem(VoorheesFormat.Vf_KindName(Jsn_doc.Vd_iFormat,
            iKind), null, Jsn_OnContextAddChild);
        tsmChoice.Tag = objChoice;
        tsmParent.DropDownItems.Add(tsmChoice);
    }

    /*-------------------------------------------------------------------------*/
    /* Jsn_ContextAddChild:                                                    */
    /*                                                                         */
    /* Adds an empty value of the chosen kind (or a comment entry, whose       */
    /* words are asked for first) at the end of a container, or a comment at   */
    /* the end of the document, and selects it.  A new object member gets      */
    /* the first free name out of newKey, newKey1 ... (Vd_ActAddChild), or,    */
    /* where its format asks for the name first (Vf_NameQuestion: an           */
    /* include's file, a registry key's hive and path), the name given         */
    /* (Jsn_DlgAskName), in the same undo step.  The                           */
    /* cursor is then put where the new entry needs editing: the Key box for   */
    /* an object member, the Value box otherwise.                              */
    /*                                                                         */
    /* Arguments:                                                              */
    /*     lstContainerPath : container position path; empty for the document. */
    /*     iKind         : a value kind of the document's format, or           */
    /*                     JSN_KIND_COMMENT (-1, which is also what            */
    /*                     Vd_ActAddChild takes for a comment entry).          */
    /*                                                                         */
    /* Returns:                                                                */
    /*     void : the document gains an entry (one undo step) and it is        */
    /*            selected.                                                    */
    /*-------------------------------------------------------------------------*/
    private void Jsn_ContextAddChild(List<int> lstContainerPath, int iKind)
    {
        VoorheesEditStep objStep;          // the action's undo step
        List<int> lstNewPath;              // where the new entry went
        string sWords;                     // a comment's words
        string sName;                      // a name asked for first (an include's file, a registry key's path), or null
        VoorheesNameQuestion objQuestion;  // the format's question for that name, or null
        string sError;                     // why the action was refused

        /* The action counted as local work and as a document change for its    */
        /* whole length -- its questions, the change and the tree's update --   */
        /* so a Save answer arriving meanwhile is held rather than applied:     */
        /* applying it could clear the undo history under this step.            */
        /* checked: a count that wrapped would hide work still running.         */
        Jsn_iLocalWork = checked(Jsn_iLocalWork + 1);
        Jsn_iModelWork = checked(Jsn_iModelWork + 1);
        try
        {   /* The action itself; the finally below ends the counting on every   */
            /* way out, a cancelled question included.  Nothing asked yet: no    */
            /* words, no name, no question.                                      */
            sWords = null;
            sName = null;
            objQuestion = null;
            if (iKind == JSN_KIND_COMMENT)
            {   /* A comment: ask for its words. */
                sWords = Jsn_DlgInputBox("Add Comment", "Comment:", "");
                if (sWords == null)
                {   /* Cancelled. */
                    return;
                }
            }
            else
            {   /* An entry: the format may want its name first. */
                objQuestion = VoorheesFormat.Vf_NameQuestion(Jsn_doc, iKind);
            }

            if (objQuestion != null)
            {   /* Asked first (a Klipper-family include's file; a registry key's hive and path). */
                sName = Jsn_DlgAskName(objQuestion);
                if (sName == null)
                {   /* Cancelled. */
                    return;
                }
            }

            /* One undo step for the whole addition: the entry (a new member   */
            /* gets the first free name out of newKey, newKey1 ...), then      */
            /* the name asked for, and the new entry focused afterwards.       */
            objStep = Jsn_doc.Vd_BeginStep(null);
            sError = Jsn_doc.Vd_ActAddChild(objStep, lstContainerPath, iKind, sWords,
                out lstNewPath);
            if (sError == null && sName != null)
            {   /* Added with a placeholder: give it the name asked for, in the same step (refused: all of it is taken back). */
                sError = Jsn_doc.Vd_ActRename(objStep, lstNewPath, sName);
            }

            /* Where the new entry is, kept as the step's focus            */
            /* (Ves_lstFocus), then the step finished: kept whole, or --   */
            /* refused -- taken back whole with the reason shown.          */
            objStep.Ves_lstFocus = lstNewPath;
            if (!Jsn_DocFinish(objStep, sError))
            {   /* Refused (the user has been told why). */
                return;
            }

            /* Focus the right editor only after the context menu has         */
            /* finished closing: done straight away, the closing menu hands   */
            /* focus back to the tree.                                        */
            BeginInvoke(new MethodInvoker(Jsn_FocusAddedEntry));
        }
        finally
        {   /* The action has returned, questions and all, however it ended:   */
            /* the counts come down, any Save answers and close decisions      */
            /* held while it ran are posted for a later turn, and the editor   */
            /* coordinator is told this window's own work has returned         */
            /* (EUI_LOCAL_RETURN, CommandEditorCoordinator.cs).                */
            Jsn_iModelWork--;
            Jsn_iLocalWork--;
            Jsn_PostReadyAcknowledgements();
            Jsn_PostReadyCloses();
            Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
        }
    }
    /*------------------------------------------------------------------------*/
    /* Jsn_ContextRenameKey:                                                  */
    /*                                                                        */
    /* Asks for a new key for an object member and renames it in place;       */
    /* the member keeps its position, value and layout.  An empty key, or     */
    /* one already used in the object, is refused (Vd_ActRename), so no       */
    /* new duplicate key is ever created.  The question is the plain box      */
    /* holding the key, unless the format has one of its own                  */
    /* (Vf_RenameQuestion: a registry key's hive drop-down and path).         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstPath : current position path of the member to rename.           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the key may change (one undo step).                         */
    /*------------------------------------------------------------------------*/
    private void Jsn_ContextRenameKey(List<int> lstPath)
    {
        VoorheesItem objItem;              // the member
        VoorheesEditStep objStep;          // the action's undo step
        VoorheesNameQuestion objQuestion;  // the format's own question, or null
        string sNewKey;                    // key typed by the user

        /* Counted as local work and as a document change for the whole   */
        /* action, as in Jsn_ContextAddChild: a Save answer arriving      */
        /* meanwhile is held, not applied under this undo step.           */
        Jsn_iLocalWork = checked(Jsn_iLocalWork + 1);
        Jsn_iModelWork = checked(Jsn_iModelWork + 1);
        try
        {   /* The action itself; the finally below ends the counting on     */
            /* every way out, a cancelled question included.                 */
            /* The entry now at that path -- the document may have changed   */
            /* since the menu was built.                                     */
            objItem = Jsn_doc.Vd_ItemAt(lstPath);
            if (objItem == null || objItem.Vi_iKind != VoorheesItem.VI_MEMBER)
            {   /* Gone, or not a member: nothing to rename. */
                return;
            }

            /* The format's own question for the name, if it has one. */
            objQuestion = VoorheesFormat.Vf_RenameQuestion(Jsn_doc, objItem);
            if (objQuestion != null)
            {   /* The format's question (a registry key: hive and path). */
                sNewKey = Jsn_DlgAskName(objQuestion);
            }
            else
            {   /* The plain box, holding the key as it is. */
                sNewKey = Jsn_DlgInputBox("Rename Key", "New key name:", objItem.Vi_sKey);
            }

            if (sNewKey == null || sNewKey == objItem.Vi_sKey)
            {   /* Cancelled, or left unchanged: nothing to do. */
                return;
            }

            /* The rename as one undo step; Jsn_DocFinish applies it to the   */
            /* tree and reloads the panel with the new key, or says why it    */
            /* was refused (an empty or a taken key).                         */
            objStep = Jsn_doc.Vd_BeginStep(lstPath);
            Jsn_DocFinish(objStep, Jsn_doc.Vd_ActRename(objStep, lstPath, sNewKey));
        }
        finally
        {   /* The action has returned, questions and all, however it ended:   */
            /* the counts come down, any Save answers and close decisions      */
            /* held while it ran are posted for a later turn, and the editor   */
            /* coordinator is told this window's own work has returned         */
            /* (EUI_LOCAL_RETURN, CommandEditorCoordinator.cs).                */
            Jsn_iModelWork--;
            Jsn_iLocalWork--;
            Jsn_PostReadyAcknowledgements();
            Jsn_PostReadyCloses();
            Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
        }
    }
    /*------------------------------------------------------------------------*/
    /* Jsn_ContextCopy:                                                       */
    /*                                                                        */
    /* Puts a deep copy of an entry on the editor's own clipboard, for        */
    /* Paste (not the Windows clipboard).  A copy, so later edits to the      */
    /* original do not show up in what gets pasted.                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstPath : the entry's position path now.                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : Jsn_clipboardItem is replaced.                              */
    /*------------------------------------------------------------------------*/
    private void Jsn_ContextCopy(List<int> lstPath)
    {
        VoorheesItem objItem;                               // the entry

        /* The entry now at that path. */
        objItem = Jsn_doc.Vd_ItemAt(lstPath);
        if (objItem == null)
        {   /* Gone: nothing to copy. */
            return;
        }

        /* A deep copy, and the format it came from, so a paste into   */
        /* another format's document can convert it (Vd_ActPaste).     */
        Jsn_clipboardItem = objItem.Vi_CloneDeep();
        Jsn_iClipboardFormat = Jsn_doc.Vd_iFormat;
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_ContextPaste:                                                      */
    /*                                                                        */
    /* Adds a fresh copy of the clipboard entry at the end of an object       */
    /* or array, laid out in this document's style, and selects it            */
    /* (Vd_ActPaste: a member keeps its key if free, else the next free       */
    /* variant; members and elements convert as needed).                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstContainerPath : current container position path for the paste.  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the document gains an entry (one undo step).                */
    /*------------------------------------------------------------------------*/
    private void Jsn_ContextPaste(List<int> lstContainerPath)
    {
        VoorheesEditStep objStep;  // the action's undo step
        List<int> lstNewPath;      // where the copy went
        string sError;             // why the action was refused

        /* Counted as local work and as a document change for the whole   */
        /* action, as in Jsn_ContextAddChild: a Save answer arriving      */
        /* meanwhile is held, not applied under this undo step.           */
        Jsn_iLocalWork = checked(Jsn_iLocalWork + 1);
        Jsn_iModelWork = checked(Jsn_iModelWork + 1);
        try
        {   /* The action itself; the finally below ends the counting on   */
            /* every way out, the early return included.                   */
            if (Jsn_clipboardItem == null)
            {   /* Nothing has been copied. */
                return;
            }

            /* The paste as one undo step, the new entry focused; Jsn_DocFinish   */
            /* applies it or says why it was refused.                             */
            objStep = Jsn_doc.Vd_BeginStep(null);
            sError = Jsn_doc.Vd_ActPaste(objStep, lstContainerPath, Jsn_clipboardItem,
                Jsn_iClipboardFormat, out lstNewPath);
            objStep.Ves_lstFocus = lstNewPath;
            Jsn_DocFinish(objStep, sError);
        }
        finally
        {   /* The action has returned, however it ended: the counts come     */
            /* down, any Save answers and close decisions held while it ran   */
            /* are posted for a later turn, and the editor coordinator is     */
            /* told this window's own work has returned (EUI_LOCAL_RETURN,    */
            /* CommandEditorCoordinator.cs).                                  */
            Jsn_iModelWork--;
            Jsn_iLocalWork--;
            Jsn_PostReadyAcknowledgements();
            Jsn_PostReadyCloses();
            Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
        }
    }
    /*------------------------------------------------------------------------*/
    /* Jsn_ContextDelete:                                                     */
    /*                                                                        */
    /* Removes an entry by POSITION, so deleting one copy of a duplicated     */
    /* key leaves the other copies alone.  Afterwards the selection lands     */
    /* on whatever now has the same position (the next entry) or else on      */
    /* the nearest ancestor.                                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstPath : current position path of the entry to delete.            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the entry is removed (one undo step).                       */
    /*------------------------------------------------------------------------*/
    private void Jsn_ContextDelete(List<int> lstPath)
    {
        VoorheesEditStep objStep;                           // the action's undo step

        /* Counted as local work and as a document change for the whole   */
        /* action, as in Jsn_ContextAddChild: a Save answer arriving      */
        /* meanwhile is held, not applied under this undo step.           */
        Jsn_iLocalWork = checked(Jsn_iLocalWork + 1);
        Jsn_iModelWork = checked(Jsn_iModelWork + 1);
        try
        {   /* The action itself; the finally below ends the counting even       */
            /* if it throws.                                                     */
            /* The removal by position as one undo step; Jsn_DocFinish applies   */
            /* it, moves the selection and reloads the panel from it, or says    */
            /* why it was refused.                                               */
            objStep = Jsn_doc.Vd_BeginStep(lstPath);
            Jsn_DocFinish(objStep, Jsn_doc.Vd_ActRemove(objStep, lstPath));
        }
        finally
        {   /* The action has returned, however it ended: the counts come     */
            /* down, any Save answers and close decisions held while it ran   */
            /* are posted for a later turn, and the editor coordinator is     */
            /* told this window's own work has returned (EUI_LOCAL_RETURN,    */
            /* CommandEditorCoordinator.cs).                                  */
            Jsn_iModelWork--;
            Jsn_iLocalWork--;
            Jsn_PostReadyAcknowledgements();
            Jsn_PostReadyCloses();
            Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
        }
    }
    /*------------------------------------------------------------------------*/
    /* FILE: load (and reopen as another format), save                        */
    /*------------------------------------------------------------------------*/

    /*------------------------------------------------------------------------*/
    /* Jsn_FileFormatSubmenu:                                                 */
    /*                                                                        */
    /* Builds File > Reopen As: one item per format the main window offers    */
    /* (JSON, INI and the Klipper family; registry files are not one of       */
    /* them here), each greyed until its module exists                        */
    /* (VoorheesFormat.Vf_IsAvailable).                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText : the submenu's caption.                                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     ToolStripMenuItem : the submenu, filled.                           */
    /*------------------------------------------------------------------------*/
    ToolStripMenuItem Jsn_FileFormatSubmenu(string sText)
    {
        ToolStripMenuItem tsmSubmenu;                       // the submenu
        int iFormat;

        /* The submenu, then the formats from JSON to KlipperScreen in   */
        /* their id order -- the range leaves the registry format out.   */
        tsmSubmenu = new ToolStripMenuItem(sText);
        for (iFormat = VoorheesFormat.VF_JSON; iFormat <= VoorheesFormat.VF_KLIPPERSCREEN;
            iFormat++)
        {   /* One format per iteration, each with a handler of its own. */
            tsmSubmenu.DropDownItems.Add(Jsn_FileFormatItem(iFormat));
        }

        /* The submenu, one item per format, for the caller to add. */
        return(tsmSubmenu);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_FileFormatItem:                                                    */
    /*                                                                        */
    /* One format's item in Reopen As: the format's name as its caption, its  */
    /* id in its Tag, so the one shared handler (Jsn_OnReopenFormatClick)     */
    /* knows from the item clicked which format to read the file as, and      */
    /* greyed when that format's module is not there.                         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the VF_ format.                                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     ToolStripMenuItem : the item.                                      */
    /*------------------------------------------------------------------------*/
    ToolStripMenuItem Jsn_FileFormatItem(int iFormat)
    {
        ToolStripMenuItem tsmItem;                          // the item

        /* The item: caption, the shared handler, the id in the Tag, and   */
        /* enabled only when the format's module exists                    */
        /* (VoorheesFormat.Vf_IsAvailable).                                */
        tsmItem = new ToolStripMenuItem(VoorheesFormat.Vf_Name(iFormat), null, Jsn_OnReopenFormatClick);
        tsmItem.Tag = iFormat;
        tsmItem.Enabled = VoorheesFormat.Vf_IsAvailable(iFormat);

        /* The finished item, for the sub-menu. */
        return(tsmItem);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_FileReopenAs:                                                      */
    /*                                                                        */
    /* File > Reopen As: read the file again, as it was last saved, in        */
    /* another format -- for a file whose name led to the wrong one, or that  */
    /* has no telling name.  The file read is the one the editor writes       */
    /* (Jsn_file.OFD_sTempFile): a Windows file itself, or an image entry's   */
    /* private copy, which an acknowledged image save brings up to date.      */
    /* Unsaved changes are lost only when the user agrees; a file that will   */
    /* not read, or is not valid in the format, leaves the document as it     */
    /* was.                                                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the VoorheesFormat.VF_ format to read it as (the menu    */
    /*               item's Tag, Jsn_OnReopenFormatClick).                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the document is replaced by the file read in the new        */
    /*            format, or is unchanged (refused, declined or failed, the   */
    /*            user told why where there is something to tell).            */
    /*------------------------------------------------------------------------*/
    private void Jsn_FileReopenAs(int iFormat)
    {
        string sRefused;

        if (Jsn_iPendingSaves != 0 || JSN_HasLocalWork() || Jsn_bWorkCopyFailed)
        {   /* Refused, silently: the file on disk is not a settled copy of   */
            /* the last save.  A Save still unanswered may yet rewrite it     */
            /* (an image save's answer rewrites the private copy,             */
            /* Jsn_ApplyAcknowledgement), the window's own work is still      */
            /* running, or an image save worked but its private copy could    */
            /* not be brought up to date, so the copy holds older content.    */
            /* The File menu greys Reopen As on the same test when it opens   */
            /* (Jsn_OnFileMenuOpening); this is for a state that changed      */
            /* while the menu was open.                                       */
            return;
        }

        /* Counted as local work and as a document change for the whole     */
        /* reopen -- its questions, the read and the new tree -- as in      */
        /* Jsn_ContextAddChild: a Save answer arriving meanwhile is held,   */
        /* not applied while the document is being replaced.                */
        Jsn_iLocalWork = checked(Jsn_iLocalWork + 1);
        Jsn_iModelWork = checked(Jsn_iModelWork + 1);
        try
        {   /* The reopen itself; the finally below ends the counting on     */
            /* every way out.  Typing still in the panel is settled first,   */
            /* since a Yes there saves it into the document, which the       */
            /* question below then counts as an unsaved change.              */
            if (!Jsn_EditConfirmPendingPanel())
            {   /* Cancel at the panel's question, or its values would not   */
                /* save: no reopen, the typing kept.                         */
                return;
            }

            if (Jsn_doc.Vd_bModified)
            {   /* Unsaved changes: reading the file again throws them away,   */
                /* so the user is asked first, naming the file and the         */
                /* format it is to be read as.                                 */
                if (Jsn_DlgMessage("Reopening reads " + Jsn_file.OFD_sName + " again as it "
                    + "was last saved, so the unsaved changes here will be lost."
                    + "\r\n\r\nReopen it as " + VoorheesFormat.Vf_Name(iFormat) + "?",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {   /* No (or Cancel, Jsn_DlgMessage's answer when a close   */
                    /* question the command system asked has been replaced   */
                    /* meanwhile): the changes are kept.                     */
                    return;
                }
            }

            if (Jsn_FileLoadFromPath(Jsn_file.OFD_sTempFile, iFormat))
            {   /* Read and valid: the document, the tree and the panel are   */
                /* now the file's, in the new format.                         */
                return;
            }

            /* Not loaded.  A file that could not be read has already been   */
            /* reported by the loader; a file not valid in the format is     */
            /* left to the caller, in Jsn_sRefused.  Taken and cleared, so   */
            /* the field holds only the outcome of a load still to be read   */
            /* (the main window reads it once, after the first load,         */
            /* Main_ShowEditor in MainForm.cs).                              */
            sRefused = Jsn_sRefused;
            Jsn_sRefused = null;
            if (sRefused != null)
            {   /* Not valid in that format: the reason (with its line and    */
                /* column), and that nothing changed -- the document is set   */
                /* only once a load has worked.                               */
                Jsn_DlgMessage(Jsn_file.OFD_sName + " is not " + VoorheesFormat.Vf_Name(iFormat)
                    + " the editor can open:\r\n\r\n" + sRefused
                    + "\r\n\r\nThe document is unchanged.",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {   /* The reopen has returned, questions and all, however it         */
            /* ended: the counts come down, any Save answers and close        */
            /* decisions held while it ran are posted for a later turn, and   */
            /* the editor coordinator is told this window's own work has      */
            /* returned (EUI_LOCAL_RETURN, CommandEditorCoordinator.cs).      */
            Jsn_iModelWork--;
            Jsn_iLocalWork--;
            Jsn_PostReadyAcknowledgements();
            Jsn_PostReadyCloses();
            Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_FileLoadFromPath:                                                  */
    /*                                                                        */
    /* Read a file in a format and, when it is valid, make it the window's    */
    /* document (tree, panel and title with it) and show the format's         */
    /* opening warnings.  The two callers are the first load (Jsn_OnLoad)     */
    /* and Reopen As (Jsn_FileReopenAs).  A file that cannot be read is       */
    /* reported here; a file not valid in the format is NOT -- its reason is  */
    /* left in Jsn_sRefused for the caller, because the first load's caller   */
    /* (the main window) offers to open it as plain text instead, and Reopen  */
    /* As words its own message.                                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sPath   : the file: a Windows file, or an image entry's private    */
    /*               copy (OFD_sTempFile).                                    */
    /*     iFormat : the VoorheesFormat.VF_ format to read it as, or          */
    /*               VF_DETECT to go by its name and contents.                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when the file is now the document; false when it       */
    /*            could not be read (reported here) or is not valid in the    */
    /*            format (reason in Jsn_sRefused), the document unchanged.    */
    /*            A failure to show the progress window is thrown on.         */
    /*------------------------------------------------------------------------*/
    private bool Jsn_FileLoadFromPath(string sPath, int iFormat)
    {
        JLD_clsLoadWork objWork;
        JFD_clsFileData objData;
        Exception exWork;
        List<string> lstWarnings;
        int i;

        /* Counted as local work and as a document change for the whole       */
        /* load -- the read, the new tree and the warnings -- as in           */
        /* Jsn_ContextAddChild, so a Save answer arriving meanwhile is held   */
        /* until the document in place is the one it will be compared with.   */
        Jsn_iLocalWork = checked(Jsn_iLocalWork + 1);
        Jsn_iModelWork = checked(Jsn_iModelWork + 1);
        try
        {   /* The load itself; the finally below ends the counting on      */
            /* every way out, a throw included.  The read and the parse     */
            /* run off the UI thread behind the busy window                 */
            /* (BusyModalForm.cs), which appears only if they outlast       */
            /* JSN_SHOW_DELAY_MS: the work is given only the path and the   */
            /* format (JLD_clsLoadWork), never a control, and BMF_Run       */
            /* returns only once it has finished, handing back what it      */
            /* threw rather than throwing it.                               */
            objWork = new JLD_clsLoadWork(sPath, iFormat);
            exWork = BMF_clsBusyModalForm.BMF_Run(this, Jsn_fntDialog,
                "Opening file", Jsn_file.OFD_sName, objWork.JLD_Run,
                JSN_CAPTION, JSN_SHOW_DELAY_MS);
            objData = objWork.JLD_objResult;
            if (exWork != null || objData == null)
            {   /* The work threw: JSN_ReadDocument catches a file that       */
                /* cannot be read, so this is a failure inside the format's   */
                /* reader, shown by its message (Jsn_ProgDescribe).  No       */
                /* result without an exception cannot happen -- JLD_Run       */
                /* always sets one when it returns -- so that half is a       */
                /* guard.  No reason in Jsn_sRefused: there is no format      */
                /* refusal to offer plain text for.                           */
                Jsn_DlgMessage("Failed to read " + Jsn_file.OFD_Title() +
                    ":\r\n" + Jsn_ProgDescribe(exWork),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return(false);
            }

            if (objData.JFD_bReadFailed)
            {   /* The file could not be read at all (missing, locked, no    */
                /* access): reported here with the system's own reason,      */
                /* naming the file as the user knows it (OFD_Title) rather   */
                /* than its private copy.                                    */
                Jsn_DlgMessage("Failed to read " + Jsn_file.OFD_Title() +
                    ":\r\n" + objData.JFD_sError,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return(false);
            }

            if (objData.JFD_sError != null)
            {   /* Read, but not valid in the format: the reason (with its     */
                /* line and column) kept for the caller and nothing shown      */
                /* here, since each caller words it itself -- the first        */
                /* load's offers the file as plain text, Reopen As gives its   */
                /* own message.                                                */
                Jsn_sRefused = objData.JFD_sError;
                return(false);
            }

            /* Valid: it becomes the window's document -- a new revision,    */
            /* the tree rebuilt, the panel and title redrawn                 */
            /* (Jsn_DocSetDocument) -- before any warning is shown, so the   */
            /* warnings appear over the document they are about.  Its        */
            /* comment state is the one the reader counted off the UI        */
            /* thread: nothing has changed the document since.               */
            Jsn_DocSetDocument(objData.JFD_objDocument, objData.JFD_bHasComments);
            /* The format's warnings on opening this document (for JSON,     */
            /* repeated keys, counted on the worker, JFD_iExtraCopies;       */
            /* what each format warns about is its own, Vf_OpenWarnings in   */
            /* VoorheesFormat.cs).                                           */
            lstWarnings = VoorheesFormat.Vf_OpenWarnings(objData.JFD_objDocument,
                Jsn_file.OFD_sName, objData.JFD_iExtraCopies);
            for (i = 0; i < lstWarnings.Count; i++)
            {   /* One warning per iteration, each in a box of its own, as   */
                /* information: the document is open and usable.             */
                Jsn_DlgMessage(lstWarnings[i], MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            /* The file is the document now. */
            return(true);
        }
        finally
        {   /* The load has returned, warnings and all, however it ended:   */
            /* the counts come down, any Save answers and close decisions   */
            /* held while it ran are posted for a later turn, and the       */
            /* editor coordinator is told this window's own work has        */
            /* returned (EUI_LOCAL_RETURN, CommandEditorCoordinator.cs).    */
            Jsn_iModelWork--;
            Jsn_iLocalWork--;
            Jsn_PostReadyAcknowledgements();
            Jsn_PostReadyCloses();
            Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
        }
    }

    /*------------------------------------------------------------------------*/
    /* JSN_ReadDocument:                                                      */
    /*                                                                        */
    /* Read and parse a file WITHOUT touching the editor, so it can run off   */
    /* the UI thread behind the busy window (JLD_clsLoadWork, for             */
    /* Jsn_FileLoadFromPath).  The format is the one given, or else the one   */
    /* the file's name or contents show; each format's own reader keeps its   */
    /* encoding and style (VoorheesFormat.cs).  A file that cannot be read    */
    /* and one that is not valid in the format are told apart, because the    */
    /* caller treats them differently.                                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sPath   : the file: a Windows file, or an image entry's private    */
    /*               copy.                                                    */
    /*     iFormat : the VoorheesFormat.VF_ format to read it as, or          */
    /*               VF_DETECT to work it out.                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     JFD_clsFileData : never null.  JFD_bReadFailed with JFD_sError     */
    /*                       when the file could not be read; JFD_sError      */
    /*                       alone when it is not valid in the format;        */
    /*                       otherwise the document, its repeated-key count   */
    /*                       and whether it holds a comment.  A failure       */
    /*                       inside a format's reader is thrown on, not       */
    /*                       caught here.                                     */
    /*------------------------------------------------------------------------*/
    public static JFD_clsFileData JSN_ReadDocument(string sPath, int iFormat)
    {
        JFD_clsFileData objData;  // what is learned about the file
        byte[] abBytes;           // the file's raw contents
        string sError;            // why parsing failed

        /* The answer, filled as the file is learned about. */
        objData = new JFD_clsFileData();

        try
        {   /* The whole file at once: every format's reader works on   */
            /* all of its bytes.                                        */
            abBytes = File.ReadAllBytes(sPath);
        }
        catch (Exception ex)
        {   /* Missing, locked or unreadable file: marked as a read        */
            /* failure, with the system's reason, and nothing more to      */
            /* learn.  Any exception, since every way a file can fail to   */
            /* read means the same here.                                   */
            objData.JFD_bReadFailed = true;
            objData.JFD_sError = ex.Message;
            return(objData);
        }

        if (iFormat == VoorheesFormat.VF_DETECT)
        {   /* No format given: the name, else the contents, decide. */
            iFormat = VoorheesFormat.Vf_Detect(sPath, abBytes);
        }

        /* The parse, by the format's own reader; null with the reason   */
        /* (line and column) when the bytes are not valid in it.         */
        objData.JFD_objDocument = VoorheesFormat.Vf_Load(iFormat, abBytes, sPath, out sError);
        if (objData.JFD_objDocument == null)
        {   /* Not valid in that format: the reason kept for the caller,   */
            /* which shows it in its own words.                            */
            objData.JFD_sError = sError;
            return(objData);
        }

        /* The repeated-key copies and whether the document holds a       */
        /* comment, both counted here, off the UI thread, because each    */
        /* walks the whole document.  The format's opening warnings use   */
        /* the first; the second is handed to the document's set-up       */
        /* (Jsn_FileLoadFromPath to Jsn_DocSetDocument), so the window    */
        /* does not walk a large document again on the UI thread.         */
        objData.JFD_iExtraCopies = objData.JFD_objDocument.Vd_ExtraCopies();
        objData.JFD_bHasComments = objData.JFD_objDocument.Vd_HasComments();

        /* A parsed document and what was found in it. */
        return(objData);
    }

    /*------------------------------------------------------------------------*/
    /* JSN_ReadRefused:                                                       */
    /*                                                                        */
    /* Why the first load found the file not valid in its format, for the     */
    /* main window (Main_ShowEditor, MainForm.cs), which reads it once the    */
    /* window has been shown -- and, having refused its load, has closed      */
    /* itself -- and then offers to open the file as plain text instead.      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the format's reason (with its line and column), or null   */
    /*              when the load worked or failed some other way (a file     */
    /*              that could not be read, already reported).                */
    /*------------------------------------------------------------------------*/
    internal string JSN_ReadRefused()
    {
        /* Set by Jsn_FileLoadFromPath. */
        return(Jsn_sRefused);
    }

    /*------------------------------------------------------------------------*/
    /* JSN_SetNewDocument:                                                    */
    /*                                                                        */
    /* Say whether the window is for the main window's New command: an empty  */
    /* document of the format, with no file read, which the first Save        */
    /* writes (Jsn_OnLoad).  Called by the main window (Main_ShowEditor)      */
    /* after the window is made and before it is shown, since the choice is   */
    /* acted on when it loads.                                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bNewDocument : true for a new, empty document; false to read the   */
    /*                    file.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : kept for Jsn_OnLoad; nothing is read or made now.           */
    /*------------------------------------------------------------------------*/
    internal void JSN_SetNewDocument(bool bNewDocument)
    {
        /* Kept for Jsn_OnLoad. */
        Jsn_bNewDocument = bNewDocument;
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_ReadCurrentFile:                                                   */
    /*                                                                        */
    /* The editor's destination record as it stands -- the one the last       */
    /* acknowledged save made current, never a target still being chosen --   */
    /* for the Save As menu to offer its choices from (SVM_clsSaveMenu,       */
    /* EditorSaveMenu.cs, given it in Jsn_InitMenuStrip).                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     OFD_clsOpenedFile : the record, or null once the window has been   */
    /*                         disposed.                                      */
    /*------------------------------------------------------------------------*/
    private OFD_clsOpenedFile Jsn_ReadCurrentFile()
    {
        if (IsDisposed)
        {   /* The window is gone: no menu to offer anything. */
            return(null);
        }

        /* The record itself (not a copy): Jsn_file changes only when a        */
        /* save to another place is acknowledged (Jsn_ApplyAcknowledgement).   */
        return(Jsn_file);
    }

    /*------------------------------------------------------------------------*/
    /* JSN_BindCommandFacts:                                                  */
    /*                                                                        */
    /* Give the window the call it reports its returns through, when the      */
    /* editors' coordinator registers it (EUI_Register,                       */
    /* CommandEditorCoordinator.cs) -- and give the tree this window's        */
    /* receiver for the end of its own rearrangements, which can run without  */
    /* any of this window's code on the stack (a lazy expansion the user      */
    /* opens) and so must report through it too.                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     pfnPost : the coordinator's call (EUI_PostEditorFact); it only     */
    /*               posts, and does no engine work.                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : kept; Jsn_PostFact uses it from now on, and the tree calls  */
    /*            Jsn_OnTreeReturned.                                         */
    /*------------------------------------------------------------------------*/
    internal void JSN_BindCommandFacts(EUI_PostFact pfnPost)
    {
        /* Kept for Jsn_PostFact, and the tree's one receiver set   */
        /* (VTV_BindReturn: a single named call, not an event).     */
        Jsn_pfnPostFact = pfnPost;
        Jsn_tree.VTV_BindReturn(Jsn_OnTreeReturned);
    }

    /*------------------------------------------------------------------------*/
    /* JSN_HasLocalWork:                                                      */
    /*                                                                        */
    /* Whether the window is still inside work of its own, which can have a   */
    /* question or a busy window open: the coordinator then holds back a      */
    /* close the command system asks for until a return fact says it has      */
    /* ended (Eui_LocalWork, CommandEditorCoordinator.cs), and Reopen As is   */
    /* refused (Jsn_FileReopenAs, Jsn_OnFileMenuOpening).  A Save still       */
    /* unanswered is not counted here (Jsn_iPendingSaves): its answer is the  */
    /* command system's to give.                                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true while any of the counts below is not 0, Save answers   */
    /*            are held, the tree is rearranging itself, or the Colors     */
    /*            dialog or the close question is asked for or up.            */
    /*------------------------------------------------------------------------*/
    internal bool JSN_HasLocalWork()
    {
        /* The window's own work (local work, its closing handler, posted   */
        /* tree selections and edits), Save answers held until that work    */
        /* returns, and the tree's own rearrangements -- which can run      */
        /* with none of this window's code on the stack, and report their   */
        /* end through Jsn_OnTreeReturned.  The tree test has a null        */
        /* guard: this can be asked before the tree is made only in         */
        /* principle (the coordinator registers a built window).            */
        /* The Colors dialog while up, and the close question from the      */
        /* closing handler's post until its answer is taken, are the        */
        /* window's own work too -- so a close the worker asks for waits    */
        /* for that answer rather than being answered Cancel at once.       */
        return(Jsn_iLocalWork != 0 || Jsn_iClosing != 0 ||
            Jsn_iPostedTree != 0 || Jsn_lstAcknowledgements.Count != 0 ||
            (Jsn_tree != null && Jsn_tree.VTV_IsBusy()) || Jsn_frmColors != null ||
            Jsn_bCloseAsked || Jsn_objCloseQuestion != null);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_PostFact:                                                          */
    /*                                                                        */
    /* Report through the coordinator's call that the window's own work (or   */
    /* a deferred close) has returned, naming the window by its editor id.    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lCloseOrder : the deferred close's save order, or                  */
    /*                   EUI_LOCAL_RETURN for the window's own work.          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : reported, when a call was given.                            */
    /*------------------------------------------------------------------------*/
    private void Jsn_PostFact(long lCloseOrder)
    {
        if (Jsn_pfnPostFact != null)
        {   /* Registered: reported, by the editor id, which stays the same   */
            /* through every save and Save As (OFD_lEditorId, OpenScan.cs).   */
            /* Unregistered -- the window's own set-up before it is shown,    */
            /* which already counts work -- nothing waits on it.              */
            Jsn_pfnPostFact(Jsn_file.OFD_lEditorId, lCloseOrder);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnTreeReturned:                                                    */
    /*                                                                        */
    /* The tree's receiver for the end of a rearrangement of its own          */
    /* (Vtv_ReportReturn, VoorheesTreeView.cs): reports the window's return   */
    /* and posts any Save answers and close decisions held back while the     */
    /* tree was busy.  It runs while the control's own events may still be    */
    /* unwinding, which is why it only posts and changes nothing itself.      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the three posted for later turns of the UI thread.          */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnTreeReturned()
    {
        /* Each of the three posts (BeginInvoke, here or in the          */
        /* coordinator) rather than acting at once, so the document,     */
        /* undo history and window change only after the control's own   */
        /* handling has returned.                                        */
        Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
        Jsn_PostReadyAcknowledgements();
        Jsn_PostReadyCloses();
    }

    /*------------------------------------------------------------------------*/
    /* JSN_AbortStaging:                                                      */
    /*                                                                        */
    /* After an Exit was agreed (EUI_AbortSnapshots,                          */
    /* CommandEditorCoordinator.cs): every image-save snapshot this window    */
    /* is still writing (Jsn_lstJobs) told to stop, so a late snapshot is     */
    /* deleted rather than handed to a worker that is stopping.  The          */
    /* window's other work is not stopped: it ends at its own return.         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : each job told (SJB_Abort, CommandStage.cs); a job still     */
    /*            writing stops at its next piece and deletes its file.       */
    /*------------------------------------------------------------------------*/
    internal void JSN_AbortStaging()
    {
        int i;

        /* A walk of the list itself: SJB_Abort sets a flag and takes the    */
        /* result, shows nothing and runs no UI, so the list cannot change   */
        /* under the loop.                                                   */
        for (i = 0; i < Jsn_lstJobs.Count; i++)
        {   /* One snapshot job per iteration, told to stop. */
            Jsn_lstJobs[i].SJB_Abort();
        }
    }

    /*------------------------------------------------------------------------*/
    /* JSN_SetSourceUnavailable:                                              */
    /*                                                                        */
    /* The coordinator's word that the image an entry was opened from is      */
    /* closed or open again (Eui_SetUnavailable): the editor's record is      */
    /* marked, so an ordinary Save to the image is refused while it is gone   */
    /* (Jsn_FileSaveTo) and a close offers Save As to Windows instead         */
    /* (Jsn_ShowCloseQuestion).  A save already on its way keeps its own      */
    /* record, which this does not touch.                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bUnavailable : true when the image is gone.                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : marked; a Windows file's mark always stays false.           */
    /*------------------------------------------------------------------------*/
    internal void JSN_SetSourceUnavailable(bool bUnavailable)
    {
        /* Only an image entry can lose its image. */
        Jsn_file.OFD_bDetached = !Jsn_file.OFD_bHostFile && bUnavailable;
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_FileSave:                                                          */
    /*                                                                        */
    /* File > Save: save to the editor's own destination -- the one the last  */
    /* acknowledged save made current.  When that is an image entry whose     */
    /* image is no longer open it is refused (Jsn_FileSaveTo says so), and    */
    /* Save As can then choose a Windows file.                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when a save was handed over; false when it was         */
    /*            refused or failed before that.                              */
    /*------------------------------------------------------------------------*/
    private bool Jsn_FileSave()
    {
        /* The editor's own record, as it stands: it changes only when a    */
        /* save to another place is acknowledged, never here.  No test of   */
        /* the document's modified mark: a Save of an unchanged document    */
        /* writes it all the same (and settles the panel's typing first).   */
        return(Jsn_FileSaveTo(Jsn_file, false));
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_FileSaveTo:                                                        */
    /*                                                                        */
    /* Capture one save of the document as it is now and send it on.  The     */
    /* document is encoded once, to the bytes of the whole file, and those    */
    /* bytes go two ways: to a Windows file written here, through a           */
    /* temporary file that replaces it (JSN_WriteBytes, behind the busy       */
    /* window), or into a new staged file of this save's own for the command  */
    /* worker to put in the image (SJB_clsStageJob, CommandStage.cs).  The    */
    /* editor's destination record and its undo history change only once      */
    /* the save is acknowledged (JSN_SaveAcknowledged); whether an image      */
    /* save may run is the coordinator's and the worker's to decide.          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objTarget       : where to save: the editor's own record, or a     */
    /*                       Save As target just chosen; null (a cancelled    */
    /*                       choice) does nothing.                            */
    /*     bCloseWhenSaved : true when the editor's own close asked for this  */
    /*                       save: the close is decided again once it is      */
    /*                       acknowledged.                                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when the save was handed over (a Windows one may be    */
    /*            acknowledged already, inside the call); false when it was   */
    /*            refused or failed before that, the document unsaved.        */
    /*------------------------------------------------------------------------*/
    private bool Jsn_FileSaveTo(OFD_clsOpenedFile objTarget, bool bCloseWhenSaved)
    {
        PRS_clsPresentation objPresentation;
        OFD_clsOpenedFile objSnapshot;
        JSC_clsSaveCapture objCapture;
        SJB_clsStageJob objJob;
        STG_clsStage objUnsent;
        VoorheesDocument docSaved;
        byte[] abFile;
        Exception exWrite;
        string sProblem;
        long lRevision;
        long lOrder;

        if (objTarget == null)
        {   /* No target (a Save As choice cancelled or refused): nothing   */
            /* captured, nothing to save.                                   */
            return(false);
        }

        if (!objTarget.OFD_bHostFile && objTarget.OFD_bDetached)
        {   /* An image entry whose image is no longer open: nowhere to      */
            /* write it.  Said on the status bar, in the Modified marker's   */
            /* place (the next title update restores the marker), with       */
            /* the way out.                                                  */
            Jsn_tslModified.Text = "The filesystem is unavailable. Use Save As to choose Windows.";
            return(false);
        }

        /* No captured destination yet (set in the try below), and the    */
        /* window's own work begins: counted until the finally, so a      */
        /* question or busy window this save opens holds back any close   */
        /* question the coordinator has waiting (JSN_HasLocalWork).       */
        /* checked: an overflow throws rather than wraps.                 */
        objSnapshot = null;
        Jsn_iLocalWork = checked(Jsn_iLocalWork + 1);
        try
        {   /* The save itself; the finally below ends the counting and        */
            /* deletes a stage not handed over, on every way out.              */
            /*                                                                 */
            /* The close question this save may answer is taken first, then    */
            /* the panel's typing is settled -- before anything is captured,   */
            /* since a Yes there changes the document.  That question can      */
            /* run a message loop in which an Exit replaces the close          */
            /* question, which is why the question is read beforehand.         */
            objPresentation = QWN_clsQuestionWindows.QWN_CurrentEditorQuestion(Jsn_file.OFD_lEditorId);
            if (!Jsn_EditConfirmPendingPanel())
            {   /* Cancel at the panel's question, or its values would not   */
                /* save: no save, the typing kept.                           */
                return(false);
            }

            if (objPresentation != null &&
                !objPresentation.PRS_objQuestion.QST_IsCurrent(objPresentation))
            {   /* The close question this save was for has been taken down   */
                /* or replaced meanwhile (an Exit, say): it no longer asks    */
                /* for this save, so none is made.                            */
                return(false);
            }

            /* The capture, before anything that could run a message loop:    */
            /* the document's revision, this save's order number, the         */
            /* document itself, and the destination copied for this save      */
            /* (OFD_ForSave: a later Save As cannot change it, and it keeps   */
            /* the target's own file path -- an image target's is a work      */
            /* file of its own, made when it was chosen (MainCommand.cs),     */
            /* never the Windows file the document was opened from).          */
            lRevision = Jsn_lRevision;
            lOrder = checked(++Jsn_lSaveOrder);
            docSaved = Jsn_doc;
            objSnapshot = objTarget.OFD_ForSave(lRevision);
            objSnapshot.OFD_lSaveOrder = lOrder;
            try
            {   /* The whole file's bytes, by the format's own writer            */
                /* (encoding, line endings and style kept, VoorheesFormat.cs),   */
                /* made here on the UI thread, at once.                          */
                abFile = VoorheesFormat.Vf_SaveBytes(docSaved);
            }
            catch (Exception exSerialize)
            {   /* The writer failed: nothing written, nothing handed over,   */
                /* and the reason shown; the document stays unsaved.          */
                Jsn_DlgMessage("Failed to create the save snapshot:\r\n" + exSerialize.Message,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return(false);
            }

            /* The capture: everything this save is, in one record       */
            /* (JSC_clsSaveCapture), the bytes by reference -- the one   */
            /* buffer both the write and the answer use, which nothing   */
            /* changes after this.                                       */
            objCapture = new JSC_clsSaveCapture(this, objSnapshot, docSaved,
                abFile, lRevision, lOrder, bCloseWhenSaved);
            if (objSnapshot.OFD_bHostFile)
            {   /* A Windows file (the one opened, or a Save As target --      */
                /* OFD_sTempFile is that file): written here, whole, through   */
                /* a temporary file that replaces it (JSN_WriteBytes), off     */
                /* the UI thread behind the busy window -- the work is given   */
                /* only the captured bytes and path, never the document.       */
                /* What is handed over below is then only the fact.            */
                try
                {   /* BMF_Run returns once the write has finished, with   */
                    /* what it threw (or null).                            */
                    exWrite = BMF_clsBusyModalForm.BMF_Run(this, Jsn_fntDialog, "Saving file",
                        objSnapshot.OFD_sName, objCapture.JSC_WriteWindows,
                        JSN_CAPTION, JSN_SHOW_DELAY_MS);
                }
                catch (Exception exLocal)
                {   /* The busy window failed (BMF_Run throws that only once   */
                    /* the write has finished, or before it started).  The     */
                    /* save's outcome is the write's own, as the capture       */
                    /* recorded it (JSC_WriteOutcome): a file written is a     */
                    /* success -- handed over below like any other, the        */
                    /* window's failure only logged -- so the user is never    */
                    /* told a replaced file was not written.                   */
                    exWrite = objCapture.JSC_WriteOutcome(exLocal);
                }

                if (exWrite != null)
                {   /* Not written: said, with the file and the reason, and   */
                    /* nothing handed over -- the document stays as it is     */
                    /* and unsaved, its history kept, and Save still writes   */
                    /* where it did.                                          */
                    Jsn_DlgMessage("Failed to write " + objSnapshot.OFD_sTempFile +
                        ":\r\n" + exWrite.Message, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return(false);
                }
            }
            else
            {   /* An image entry: the bytes into a new staged file of this   */
                /* save's own, in the stage folder the registration set       */
                /* (OFD_sStageRoot), for the worker to put in the image --    */
                /* never the private copy the editor reads back, which an     */
                /* earlier save's answer may be writing.  The job is listed   */
                /* while it runs (Jsn_lstJobs), so an agreed Exit can stop    */
                /* it (JSN_AbortStaging).                                     */
                objJob = new SJB_clsStageJob(objSnapshot.OFD_sStageRoot, abFile, null, null);
                Jsn_lstJobs.Add(objJob);
                try
                {   /* The stage written off the UI thread behind the busy   */
                    /* window, from the captured bytes only.                 */
                    exWrite = BMF_clsBusyModalForm.BMF_Run(this, Jsn_fntDialog, "Saving file",
                        objSnapshot.OFD_sName, objJob.SJB_Run,
                        JSN_CAPTION, JSN_SHOW_DELAY_MS);
                }
                catch (Exception exStage)
                {   /* The busy window could not be shown: thrown only once   */
                    /* the job has finished, and treated as a failure.  The   */
                    /* stage, if the job made one, is taken below and         */
                    /* deleted by the finally.                                */
                    exWrite = exStage;
                }
                finally
                {   /* Whatever happened, the job has finished (BMF_Run        */
                    /* waits for it): its stage taken -- or, if it has none,   */
                    /* given up (SJB_Take), so nothing it makes later is       */
                    /* left behind -- and the job taken off the list.          */
                    objSnapshot.OFD_objStage = objJob.SJB_Take();
                    Jsn_lstJobs.Remove(objJob);
                }

                if (exWrite != null || objSnapshot.OFD_objStage == null)
                {   /* No complete stage, or a failure: nothing is handed   */
                    /* over (a stage taken all the same is deleted by the   */
                    /* finally below).                                      */
                    if (objJob.SJB_WasAborted())
                    {   /* Stopped by an agreed Exit (JSN_AbortStaging): the   */
                        /* user asked for it, so no failure is reported.       */
                        return(false);
                    }

                    /* The reason: the job's (or the window's) own when      */
                    /* there is one, else that the stage did not complete.   */
                    sProblem = "The snapshot did not complete.";
                    if (exWrite != null)
                    {   /* The job threw, or the busy window failed: its message. */
                        sProblem = exWrite.Message;
                    }

                    /* Said; the document stays as it is and unsaved, its   */
                    /* history kept, for another Save or a Save As.         */
                    Jsn_DlgMessage("Failed to create the save snapshot:\r\n" + sProblem,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return(false);
                }
            }

            /* The hand-over: counted as waiting BEFORE it, since a save     */
            /* can be answered inside the call -- an ordinary Windows save   */
            /* always is, and so is a refusal (the coordinator's EUI_Save,   */
            /* CommandEditorCoordinator.cs).  An image save's answer comes   */
            /* with the worker's result.  The status bar says it is on its   */
            /* way until the answer redraws it.                              */
            Jsn_iPendingSaves = checked(Jsn_iPendingSaves + 1);
            Jsn_tslModified.Text = "Saving ...";
            Jsn_pfnSave(objSnapshot, objCapture.JSC_OnSaved);

            /* Handed over; the answer comes through the capture's   */
            /* JSC_OnSaved, and may already have come.               */
            return(true);
        }
        finally
        {   /* Every way out of the save's own work, the refusals and         */
            /* failures above included.                                       */
            /*                                                                */
            /* A stage still named here was not taken: the coordinator        */
            /* clears the field whenever it takes one, refusing or not        */
            /* (EUI_Save).  The main window, when it has no coordinator,      */
            /* deletes the stage without clearing it (Main_SaveOpenedFile,    */
            /* MainForm.cs), and the release below then does nothing, as      */
            /* a second STG_Release does.  Otherwise it is reached when the   */
            /* stage was made but the save failed after all (the busy         */
            /* window's failure), or when something between the stage and     */
            /* the hand-over threw; the stage's file is deleted here.         */
            if (objSnapshot != null && objSnapshot.OFD_objStage != null)
            {   /* Not taken: no longer named, then deleted. */
                objUnsent = objSnapshot.OFD_objStage;
                objSnapshot.OFD_objStage = null;
                try
                {   /* The stage's file removed (STG_Release). */
                    objUnsent.STG_Release();
                }
                catch (Exception exCleanup)
                {   /* A file that will not go is logged, and does not   */
                    /* replace the save's own outcome.                   */
                    if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                    {   /* Only while the debug log is kept: otherwise the line is never built. */
                        DBGLOG_clsDebugLog.DBGLOG_Write("structured snapshot cleanup", exCleanup.ToString());
                    }
                }
            }

            /* The save's own work has returned, however it ended: the       */
            /* count comes down, any Save answers and close decisions held   */
            /* while it ran are posted for a later turn -- a Windows save    */
            /* answered inside the hand-over left its close waiting here     */
            /* -- and the editor coordinator is told this window's own       */
            /* work has returned (EUI_LOCAL_RETURN).                         */
            Jsn_iLocalWork--;
            Jsn_PostReadyAcknowledgements();
            Jsn_PostReadyCloses();
            Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
        }
    }

    /*------------------------------------------------------------------------*/
    /* JSN_SaveAcknowledged:                                                  */
    /*                                                                        */
    /* One save's answer (JSC_OnSaved), on the UI thread: applied now         */
    /* (Jsn_ApplyAcknowledgement), or held when applying it now could act on  */
    /* a document still being changed or overlap another answer's work.  A    */
    /* Windows save is answered inside its own Jsn_FileSaveTo call and so is  */
    /* normally applied at once.                                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objCapture : the save, as captured.                                */
    /*     bSaved     : true when it succeeded (written, and for an image     */
    /*                  entry taken into the image).                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : applied, or held in Jsn_lstAcknowledgements, in order, for  */
    /*            Jsn_PostReadyAcknowledgements to post once that work ends.  */
    /*------------------------------------------------------------------------*/
    internal void JSN_SaveAcknowledged(JSC_clsSaveCapture objCapture, bool bSaved)
    {
        if (Jsn_iModelWork != 0 || Jsn_iAcknowledgementWork != 0 ||
            (Jsn_tree != null && Jsn_tree.VTV_IsBusy()))
        {   /* A document change, another answer's application or the        */
            /* tree's own rearrangement is under way (an answer can arrive   */
            /* inside any of them, whenever a busy window or question runs   */
            /* a message loop).  Applied now, a success could clear the      */
            /* undo history under a step still being built, or start a       */
            /* second write of the private copy: so the answer is kept       */
            /* with its capture and held, in arrival order.  The Save        */
            /* stays counted as waiting until it is applied.                 */
            objCapture.JSC_bSaved = bSaved;
            Jsn_lstAcknowledgements.Add(objCapture);
            return;
        }

        /* Nothing it could disturb is running: applied now. */
        Jsn_ApplyAcknowledgement(objCapture, bSaved);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_PostReadyAcknowledgements:                                         */
    /*                                                                        */
    /* Post the held Save answers (Jsn_OnAcknowledgementsReady) once nothing  */
    /* that made them wait is still running -- called from the finally of     */
    /* every piece of work that can hold them, so the answers move on at the  */
    /* first chance with no timer or polling.  One post at a time is enough:  */
    /* the posted call applies every answer held.                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : one call posted, or nothing (no answer held, one already    */
    /*            posted, or still blocked -- the blocking work's own end     */
    /*            calls this again).                                          */
    /*------------------------------------------------------------------------*/
    private void Jsn_PostReadyAcknowledgements()
    {
        if (IsDisposed || Jsn_bAcknowledgementsPosted ||
            Jsn_lstAcknowledgements.Count == 0 || Jsn_iModelWork != 0 ||
            Jsn_iAcknowledgementWork != 0 || Jsn_tree.VTV_IsBusy())
        {   /* Gone, already posted, nothing held, or a document change,   */
            /* an answer's application or the tree's own work still        */
            /* running -- the same tests that made the answers wait.       */
            return;
        }

        /* Posted rather than applied here: this runs in the finally of     */
        /* the work that just ended, and inside the tree's own events for   */
        /* Jsn_OnTreeReturned, so the undo history, the private copy and    */
        /* any close decision change only after that stack has unwound.     */
        /* Marked as posted first, so a second call adds no second post.    */
        Jsn_bAcknowledgementsPosted = true;
        BeginInvoke(new MethodInvoker(Jsn_OnAcknowledgementsReady));
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnAcknowledgementsReady:                                           */
    /*                                                                        */
    /* The posted call: the held Save answers applied, oldest first, for as   */
    /* long as nothing that makes them wait has started again; then the       */
    /* coordinator told, since a close it held back for them may now go on.   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the answers applied, or the rest left held for the work     */
    /*            that started meanwhile to post again.                       */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnAcknowledgementsReady()
    {
        JSC_clsSaveCapture objCapture;

        /* The post has arrived: the flag lowered, so a later end of work   */
        /* can post again.                                                  */
        Jsn_bAcknowledgementsPosted = false;
        while (Jsn_lstAcknowledgements.Count != 0)
        {   /* One held answer per iteration, oldest first. */
            if (Jsn_iModelWork != 0 || Jsn_iAcknowledgementWork != 0 || Jsn_tree.VTV_IsBusy())
            {   /* Work that makes answers wait has started since the post   */
                /* (the previous answer's busy window can let one in): the   */
                /* rest stay held, and that work's own end posts again.      */
                return;
            }

            /* Taken off the list BEFORE it is applied: applying a success   */
            /* can show a busy window or a warning, in whose message loop    */
            /* another answer may arrive and be added to the list.           */
            objCapture = Jsn_lstAcknowledgements[0];
            Jsn_lstAcknowledgements.RemoveAt(0);
            Jsn_ApplyAcknowledgement(objCapture, objCapture.JSC_bSaved);
        }

        /* None held now: the coordinator told the window's work has      */
        /* returned (held answers count as work, JSN_HasLocalWork), and   */
        /* any close the answers asked for posted.                        */
        Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
        Jsn_PostReadyCloses();
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_ApplyAcknowledgement:                                              */
    /*                                                                        */
    /* Apply one save's answer.  A success becomes where Save writes next,    */
    /* and ends the undo history -- but only when the document is still the   */
    /* one that was saved, at the revision saved; edits made since keep their */
    /* history and the document stays modified.  An image entry's private     */
    /* copy (the file Reopen As reads) is rewritten with the saved bytes.  An */
    /* answer older than a success already applied changes nothing, so it     */
    /* cannot undo a newer save.  Where the file was opened from is not       */
    /* touched (OFD_objOriginal).                                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objCapture : the save, as captured.                                */
    /*     bSaved     : true when it succeeded.                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the save no longer waits; a success is applied.             */
    /*------------------------------------------------------------------------*/
    private void Jsn_ApplyAcknowledgement(JSC_clsSaveCapture objCapture, bool bSaved)
    {
        OFD_clsOpenedFile objSnapshot;
        Exception exRefreshWork;

        /* No longer waiting: every save handed over is answered exactly   */
        /* once (EUI_Save), so the count comes down first, before any      */
        /* message box below can run a message loop.                       */
        Jsn_iPendingSaves--;
        if (IsDisposed)
        {   /* The window is gone (disposed before the answer came):   */
            /* nothing left to update.                                 */
            return;
        }

        if (objCapture.JSC_lSaveOrder < Jsn_lAcknowledgedOrder)
        {   /* An older save, answered after a newer one was applied:         */
            /* where Save writes and the undo history stay the newer one's.   */
            if (bSaved && objCapture.JSC_bCloseWhenSaved)
            {   /* It asked to close the window: its close is still       */
                /* decided and reported with its number, which a close    */
                /* question may be waiting for -- though the newer save   */
                /* decides whether the window stays open                  */
                /* (JSN_CompleteDeferredClose).                           */
                Jsn_lstCloseEvaluations.Add(objCapture);
                Jsn_PostReadyCloses();
            }

            /* Nothing else of it is applied. */
            return;
        }

        if (!bSaved)
        {   /* Failed: where Save writes, the document and its history      */
            /* stay as they were, still unsaved.  How it failed is shown    */
            /* by whoever reported it (the coordinator's report,            */
            /* CommandEditorCoordinator.cs); the title and status bar are   */
            /* only redrawn, which puts the modified marker back in place   */
            /* of "Saving ...".                                             */
            Jsn_StateUpdateTitle();
            return;
        }

        /* A success.  Counted as an answer being applied, so a second       */
        /* answer arriving in a message loop below (the busy window, the     */
        /* warning) is held rather than started alongside -- two writes of   */
        /* the one private copy at once -- and as local work, so a close     */
        /* the coordinator asks for waits too.                               */
        Jsn_iAcknowledgementWork = checked(Jsn_iAcknowledgementWork + 1);
        Jsn_iLocalWork = checked(Jsn_iLocalWork + 1);
        try
        {   /* The answer applied; the finally below ends the counting on    */
            /* every way out.                                                */
            /*                                                               */
            /* Save writes to this save's own record from now on,            */
            /* expecting its own path next time (a Save As's record still    */
            /* names the entry saved to before), and its number is the one   */
            /* an older answer must beat.  Done first, before the private    */
            /* copy's refresh below can show a warning.                      */
            objSnapshot = objCapture.JSC_objSnapshot;
            objSnapshot.OFD_sExpectedEntry = objSnapshot.OFD_sImagePath;
            Jsn_file = objSnapshot;
            Jsn_lAcknowledgedOrder = objCapture.JSC_lSaveOrder;
            if (Object.ReferenceEquals(Jsn_doc, objCapture.JSC_doc) &&
                Jsn_lRevision == objCapture.JSC_lRevision)
            {   /* Still the document saved, at the revision saved: it now   */
                /* matches its file, and the undo history ends here          */
                /* (Vd_MarkSaved).  The document test is a guard -- only a   */
                /* load replaces the document, and Reopen As is refused      */
                /* while a save waits.                                       */
                Jsn_doc.Vd_MarkSaved(objSnapshot.OFD_sTempFile);
                Jsn_bSaveBaselineStale = false;
            }
            else
            {   /* Edited since the save was captured: the newer history    */
                /* is kept, but its start is no longer the file as saved,   */
                /* so undoing every step must still leave the document      */
                /* modified -- marked so here (VD_MarkBaseUnsaved) and on   */
                /* every later revision (Jsn_DocAdvanceRevision).           */
                Jsn_bSaveBaselineStale = true;
                Jsn_doc.VD_MarkBaseUnsaved();
            }

            if (objCapture.JSC_bCloseWhenSaved)
            {   /* It asked to close the window: kept for its close to be     */
                /* decided on a later turn -- always, since the decision is   */
                /* reported even when newer edits keep the window open --     */
                /* and posted once the window's own work has returned (the    */
                /* finally below).                                            */
                Jsn_lstCloseEvaluations.Add(objCapture);
            }

            if (!objSnapshot.OFD_bHostFile)
            {   /* An image entry: its private copy (OFD_sTempFile, the file   */
                /* Reopen As reads) still holds what was there before the      */
                /* save, so it is rewritten with the saved bytes -- the        */
                /* captured buffer, not the document now, which may have       */
                /* changed.                                                    */
                try
                {   /* The write off the UI thread behind the busy window   */
                    /* (JSC_WriteWindows: JSN_WriteBytes, through a         */
                    /* temporary file); what it threw handed back.          */
                    exRefreshWork = BMF_clsBusyModalForm.BMF_Run(this, Jsn_fntDialog,
                        "Saving file", objSnapshot.OFD_sName,
                        objCapture.JSC_WriteWindows, JSN_CAPTION, JSN_SHOW_DELAY_MS);
                }
                catch (Exception exWindow)
                {   /* The busy window failed (thrown only once the write has   */
                    /* finished, or before it started): the copy's outcome      */
                    /* is the write's own, as the capture recorded it           */
                    /* (JSC_WriteOutcome) -- a copy written is current, the     */
                    /* window's failure only logged.                            */
                    exRefreshWork = objCapture.JSC_WriteOutcome(exWindow);
                }

                if (exRefreshWork == null)
                {   /* The private copy holds what was saved. */
                    Jsn_bWorkCopyFailed = false;
                }
                else
                {   /* The copy could not be rewritten.  The image itself WAS   */
                    /* saved, so the save stays applied; but the copy may       */
                    /* hold older content, so Reopen As is refused until a      */
                    /* later save rewrites it, and the user is told.            */
                    Jsn_bWorkCopyFailed = true;
                    Jsn_DlgMessage("The image was saved, but its private working copy could not be refreshed:\r\n" +
                        exRefreshWork.Message, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            else
            {   /* A Windows file: the file itself was written, and it IS   */
                /* the file Reopen As reads, so it is current (and an old   */
                /* failure of an image copy no longer matters).             */
                Jsn_bWorkCopyFailed = false;
            }

            /* The title (the destination, the modified mark) and status      */
            /* bar redrawn; typing still in the panel is not part of the      */
            /* document and stays pending.  The window brought forward: the   */
            /* save's report or question may have been in front of it.        */
            Jsn_StateUpdateTitle();
            Activate();
        }
        finally
        {   /* The answer has been applied, warning and all: the counts       */
            /* come down, any further answers and the close decisions held    */
            /* are posted for a later turn, and the editor coordinator is     */
            /* told this window's own work has returned (EUI_LOCAL_RETURN).   */
            Jsn_iLocalWork--;
            Jsn_iAcknowledgementWork--;
            Jsn_PostReadyAcknowledgements();
            Jsn_PostReadyCloses();
            Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_PostReadyCloses:                                                   */
    /*                                                                        */
    /* Post the closes that successful saves asked for -- but only once       */
    /* nothing of the window's own is still running.  A Windows save is       */
    /* answered inside the save's own call -- the close question's Save       */
    /* (Jsn_ActOnCloseChoice, run by the window's queue) among them -- so a   */
    /* Close made from the answer would run with that save, and the queue     */
    /* item that made it, still on the stack; the post lets that stack        */
    /* unwind first.                                                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : each waiting close posted once, or left waiting.            */
    /*------------------------------------------------------------------------*/
    private void Jsn_PostReadyCloses()
    {
        JSC_clsSaveCapture objCapture;

        if (Jsn_iLocalWork != 0 || Jsn_iClosing != 0 || Jsn_iPostedTree != 0 ||
            Jsn_lstAcknowledgements.Count != 0 || Jsn_tree.VTV_IsBusy())
        {   /* Still inside the window's own work or its closing handler, a   */
            /* posted tree selection or edit not yet finished, Save answers   */
            /* held, or the tree rearranging itself: left waiting -- each     */
            /* of those calls this again when it ends.                        */
            return;
        }

        while (Jsn_lstCloseEvaluations.Count != 0)
        {   /* One waiting close per iteration, taken off the list BEFORE   */
            /* it is posted so it is posted once; it reports its save's     */
            /* number even if newer edits keep the window open              */
            /* (JSC_RunClose, JSN_CompleteDeferredClose).                   */
            objCapture = Jsn_lstCloseEvaluations[0];
            Jsn_lstCloseEvaluations.RemoveAt(0);
            BeginInvoke(new Action(objCapture.JSC_RunClose));
        }
    }

    /*------------------------------------------------------------------------*/
    /* JSN_CompleteDeferredClose:                                             */
    /*                                                                        */
    /* The close a successful save asked for, now that the stack it was       */
    /* asked from has unwound (JSC_RunClose, posted): the window closes only  */
    /* if nothing has changed since -- no edit, nothing typed in the panel,   */
    /* no other save waiting or applied since, no work of its own running.    */
    /* Either way the save's number is reported, since the command system's   */
    /* close question waits for exactly that report.                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objCapture : the successful save that asked for the close.         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : closed (or left open for its reason), and reported.         */
    /*------------------------------------------------------------------------*/
    internal void JSN_CompleteDeferredClose(JSC_clsSaveCapture objCapture)
    {
        try
        {   /* The decision; the finally reports it whatever happens,   */
            /* the close's own FormClosing included.                    */
            if (!IsDisposed && !Jsn_doc.Vd_bModified && !Jsn_bPanelDirty &&
                Jsn_iPendingSaves == 0 && Jsn_iLocalWork == 0 &&
                Jsn_iClosing == 0 && Jsn_iPostedTree == 0 &&
                !Jsn_tree.VTV_IsBusy() && Jsn_lRevision == objCapture.JSC_lRevision &&
                Jsn_lAcknowledgedOrder == objCapture.JSC_lSaveOrder)
            {   /* Still there, unmodified with an empty panel, nothing    */
                /* waiting or running, the document at the revision this   */
                /* save wrote, and this save the newest applied: closed.   */
                /* An ordinary Close, so FormClosing runs and, finding     */
                /* nothing to ask about, lets it go (Jsn_PresentClose).    */
                Close();
            }
        }
        finally
        {   /* Reported with the save's number: the window closed, or kept   */
            /* open for something newer -- in which case the coordinator     */
            /* has the worker look at the close again (Eui_OnEditorFact).    */
            Jsn_PostFact(objCapture.JSC_lSaveOrder);
        }
    }

    /*------------------------------------------------------------------------*/
    /* JSN_ApplyTargetChoice:                                                 */
    /*                                                                        */
    /* A Save As target chosen (JPI_OnChosen): the save to it is made now --  */
    /* the document captured as it is at this moment -- provided the editor   */
    /* is still the one the question was asked for, and the user agrees when  */
    /* the name's extension says another format.  Choosing a target changes   */
    /* nothing by itself: Save goes on writing where it did until a save to   */
    /* the new target is acknowledged.  No target -- the question cancelled   */
    /* or refused -- saves nothing; when the editor's own close asked the     */
    /* question, the close is asked again, so its question shows once more    */
    /* and its own Cancel is what keeps the window.                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objIntent : the question, as asked: the editor's record then, and  */
    /*                 whether the editor's own close asked it.               */
    /*     objTarget : the target chosen; null when the question was          */
    /*                 cancelled or refused.                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : a save to the target started (Jsn_FileSaveTo), the close    */
    /*            asked again, or nothing.                                    */
    /*------------------------------------------------------------------------*/
    internal void JSN_ApplyTargetChoice(JPI_clsPickIntent objIntent, OFD_clsOpenedFile objTarget)
    {
        if (objTarget == null && objIntent.JPI_bCloseWhenSaved && !IsDisposed &&
            Jsn_file == objIntent.JPI_objBinding)
        {   /* No destination for a Save As the user's own close asked for:   */
            /* the close posted to the window's queue, never made from here   */
            /* -- this answer can arrive inside a picker's call -- so the     */
            /* close question shows again once the stack has unwound.         */
            /* (Under the worker's close question the worker asks again       */
            /* itself; that question does not close on save, so it never      */
            /* reaches here.)                                                 */
            Jsn_objQueue.WQU_Post((int)Jsn_enmItem.CloseAgain, null);
            return;
        }

        if (objTarget == null || IsDisposed || Jsn_file != objIntent.JPI_objBinding)
        {   /* Cancelled or refused, the window gone, or the editor's   */
            /* record changed since the question was asked (a save      */
            /* elsewhere acknowledged meanwhile; compared as the same   */
            /* object, not by value): the answer no longer applies.     */
            return;
        }

        if (!Jsn_FileConfirmOtherExtension(objTarget.OFD_sImagePath))
        {   /* The name says another format and the user declined to write   */
            /* this one under it: no save, nothing changed.                  */
            return;
        }

        if (IsDisposed || Jsn_file != objIntent.JPI_objBinding)
        {   /* Tested again: the extension question ran a message loop, in   */
            /* which the window may have gone or another save's answer may   */
            /* have changed its record.                                      */
            return;
        }

        /* The save, captured only now that the target is chosen and     */
        /* agreed: the document as it is at this moment, not as it was   */
        /* when Save As was clicked.                                     */
        Jsn_FileSaveTo(objTarget, objIntent.JPI_bCloseWhenSaved);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnSaveAsImageClicked:                                              */
    /*                                                                        */
    /* File > Save As > Image...: an image destination asked for              */
    /* (Jsn_RequestSaveAs).                                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the menu item (unused).                                */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item posted (or dropped as one already in hand);        */
    /*            nothing is saved until a target is chosen.                  */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnSaveAsImageClicked(object objSender, EventArgs evArgs)
    {
        /* Posted to the window's own queue, which asks when it runs the   */
        /* item (Jsn_RunItem); a second click while it waits or runs is    */
        /* dropped there.                                                  */
        Jsn_objQueue.WQU_Post((int)Jsn_enmItem.SaveAsImage, null);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnSaveAsWindowsClicked:                                            */
    /*                                                                        */
    /* File > Save As > Windows...: a Windows destination asked for           */
    /* (Jsn_RequestSaveAs).                                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the menu item (unused).                                */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item posted (or dropped as one already in hand);        */
    /*            nothing is saved until a target is chosen.                  */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnSaveAsWindowsClicked(object objSender, EventArgs evArgs)
    {
        /* Posted to the window's own queue, as the Image choice is. */
        Jsn_objQueue.WQU_Post((int)Jsn_enmItem.SaveAsWindows, null);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_RequestSaveAs:                                                     */
    /*                                                                        */
    /* Ask for a Save As target of the kind chosen, through the Save As menu  */
    /* (SVM_Request, EditorSaveMenu.cs), which passes the question to the     */
    /* editor's target picker.  Nothing is captured or written yet: the       */
    /* document is captured once a target comes back (JSN_ApplyTargetChoice). */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bWindows : true for a Windows file, false for an image entry.      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the question asked (its answer comes to JPI_OnChosen), or   */
    /*            refused, silently.                                          */
    /*------------------------------------------------------------------------*/
    private void Jsn_RequestSaveAs(bool bWindows)
    {
        JPI_clsPickIntent objIntent;

        if (!Jsn_file.OFD_CanRequestUserFile())
        {   /* The command system says no save may start now (the worker     */
            /* busy, exiting, or holding a picker -- the panes' own test):   */
            /* refused, as Save is (Jsn_OnSaveClick).                        */
            return;
        }

        /* A question record of its own for this request, holding the        */
        /* editor's record as it is now (not a slot the next request would   */
        /* overwrite); not asked by the editor's close (false).  Its         */
        /* JPI_OnChosen receives the target.                                 */
        objIntent = new JPI_clsPickIntent(this, Jsn_file, false);
        SVM_clsSaveMenu.SVM_Request(this, Jsn_file, bWindows, objIntent.JPI_OnChosen);
    }



    /*------------------------------------------------------------------------*/
    /* Jsn_PresentClose:                                                      */
    /*                                                                        */
    /* Whether the window may close now -- a decision only, made inside the   */
    /* closing handler, which shows nothing: the window is kept open while    */
    /* anything of its own still needs it, let close when nothing is unsaved  */
    /* (or a Discard answer is closing it), and otherwise kept open with the  */
    /* close question posted to the window's queue (Jsn_ShowCloseQuestion).   */
    /* A close that goes ahead remembers the window's settings and closes the */
    /* Find window.                                                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     evClosing : the closing event; cancelled to keep the window.       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the close allowed (nothing unsaved, an explicit discard, or */
    /*            a load that failed) or cancelled, the question posted when  */
    /*            there is typing or a change to ask about.                   */
    /*------------------------------------------------------------------------*/
    private void Jsn_PresentClose(FormClosingEventArgs evClosing)
    {
        PRS_clsPresentation objPresentation;
        TreeNode nodEditing;

        if (Jsn_iPendingSaves != 0 || Jsn_iLocalWork != 0 ||
            Jsn_iPostedTree != 0 || Jsn_lstAcknowledgements.Count != 0 ||
            Jsn_tree.VTV_IsBusy() || Jsn_frmColors != null ||
            Jsn_bCloseAsked || Jsn_objCloseQuestion != null)
        {   /* A save's answer still to come, the window's own work (its    */
            /* Colors dialog up, or its close question asked for or up,     */
            /* among it), a posted tree selection or edit, held Save        */
            /* answers, or the tree's own rearrangement: each still needs   */
            /* the window, so it is kept open with no question -- and a     */
            /* second close while the question is waiting posts no second   */
            /* one.  A close the worker asks for is held back by the        */
            /* coordinator until they return.                               */
            evClosing.Cancel = true;
            return;
        }

        if (Jsn_bLabelEditing)
        {   /* An in-place edit box is still open: its text is not in the   */
            /* document yet.  Kept open, and the edit finished first.       */
            evClosing.Cancel = true;
            /* After the edit, the close is tried again only when it is     */
            /* the user's own (no worker question showing): the posted      */
            /* commit then posts a Close (JSN_CompleteTreeCommit), which    */
            /* comes back here and asks about the changes as usual.         */
            /* Under the worker's question, the commit's return fact lets   */
            /* the coordinator have the worker ask again instead.           */
            Jsn_bCloseAfterInline =
                QWN_clsQuestionWindows.QWN_CurrentEditorQuestion(Jsn_file.OFD_lEditorId) == null;
            nodEditing = Jsn_tree.SelectedNode;
            if (nodEditing != null)
            {   /* The edit box belongs to the selected node: ended, KEEPING   */
                /* the text (false: not a cancel), so the control raises       */
                /* AfterLabelEdit, which posts the commit                      */
                /* (Jsn_OnTreeAfterLabelEdit).                                 */
                nodEditing.EndEdit(false);
            }

            /* Nothing more now: the posted commit is counted as the        */
            /* window's work (Jsn_iPostedTree) until it has been applied.   */
            return;
        }

        if (!Jsn_bLoadFailed && !Jsn_bDiscardOnClose && (Jsn_bPanelDirty || Jsn_doc.Vd_bModified))
        {   /* A document is open (a load that failed has none to ask        */
            /* about), this is not the close a Discard answer makes          */
            /* (Jsn_ActOnCloseChoice -- that answer was the question), and   */
            /* the panel holds typing or the document holds changes: there   */
            /* is something to ask about.                                    */
            /*                                                               */
            /* The worker's close question for this editor, if one is        */
            /* showing (an image close's or an Exit's): its display is on    */
            /* the stack only while this handler runs, so it is taken now    */
            /* and carried with the post -- the question it answers.         */
            objPresentation = QWN_clsQuestionWindows.QWN_CurrentEditorQuestion(Jsn_file.OFD_lEditorId);

            /* Kept open, and the questions -- the panel's, then the close   */
            /* question -- posted to the window's queue, which asks them     */
            /* once this handler has returned: a handler opens nothing.      */
            /* Marked as asked for meanwhile, so the window counts it as     */
            /* its own work (JSN_HasLocalWork) and a second close posts no   */
            /* second question.                                              */
            evClosing.Cancel = true;
            Jsn_bCloseAsked = true;
            Jsn_objQueue.WQU_Post((int)Jsn_enmItem.AskClose, objPresentation);
            return;
        }

        if (!Jsn_bLoadFailed)
        {   /* The close goes ahead with a document open (nothing unsaved,    */
            /* or Discard answered): the window's placement, splitter,        */
            /* font size and view options kept for next time (Jsn_CfgSave).   */
            /* A load that failed has no placement worth keeping.             */
            Jsn_CfgSave();
        }

        if (Jsn_frmFind != null && !Jsn_frmFind.IsDisposed)
        {   /* The Find window is open: closed with the editor.  It has no   */
            /* owner (Jsn_SearchShowFind), so nothing else would close it,   */
            /* and it would go on calling an editor that is gone.            */
            Jsn_frmFind.Close();
        }

        /* The base class last, which raises the FormClosing event for any   */
        /* handler hooked to it -- only on the paths that let the close go   */
        /* on; a close cancelled above returns before it.                    */
        base.OnFormClosing(evClosing);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_ShowCloseQuestion:                                                 */
    /*                                                                        */
    /* The close's questions, run by the window's queue for a close that      */
    /* found typing or changes (Jsn_PresentClose): first the panel's own      */
    /* question about typing not yet in the document, then -- while the       */
    /* document is still unsaved -- the close question: Save / Save As Image  */
    /* / Save As Windows / Discard / Cancel, or, when the entry's image is    */
    /* gone, the recovery question (Save As to Windows, Discard, Cancel).     */
    /* The close question is EDITOR-MODAL: shown modeless over this window,   */
    /* which is disabled while it is up, so the main window and every other   */
    /* editor carry on; its choice comes back through the queue               */
    /* (Jsn_OnCloseQuestionClosed, Jsn_ApplyCloseAnswer).  A test's hook may  */
    /* answer it instead (Jsn_DlgHookClose), its choice then acted on at once */
    /* by the same code.  Kept as the window's own work until it is           */
    /* answered, so a close the worker asks for waits for that answer; under  */
    /* the worker's close question it is kept under that question too, so an  */
    /* Exit taking its place takes it down unanswered.                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objPresentation : the worker's close question's display, as the    */
    /*                       closing handler took it; null for the window's   */
    /*                       own close.                                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the question shown or answered by the hook; or, when there  */
    /*            is nothing left to ask, the close made again or the         */
    /*            coordinator told.                                           */
    /*------------------------------------------------------------------------*/
    private void Jsn_ShowCloseQuestion(PRS_clsPresentation objPresentation)
    {
        Form frmQuestion;
        RCA_enmRecoveryChoice enmChoice;
        bool bPanelSettled;
        bool bOrdinary;
        bool bImageOpen;
        bool bAnswered;

        if (IsDisposed)
        {   /* A guard: the window is gone -- nothing to ask about, and   */
            /* no longer asked for.                                       */
            Jsn_bCloseAsked = false;
            return;
        }

        if (objPresentation != null &&
            !objPresentation.PRS_objQuestion.QST_IsCurrent(objPresentation))
        {   /* The worker's question was answered or replaced (an Exit)   */
            /* since the close was asked: nothing is asked for it.  No    */
            /* longer asked for, and the coordinator told; a question     */
            /* shown again asks the close again under its new display.    */
            Jsn_bCloseAsked = false;
            Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
            return;
        }

        /* The panel's typing settled first, as before any reload: Yes      */
        /* saves it into the document, No drops it, Cancel keeps it.  Its   */
        /* question is the editor's ordinary message box.  The close is     */
        /* still marked as asked for while it is up, so a worker's close    */
        /* waits and another close posts nothing; the mark comes off as     */
        /* soon as it has answered -- the close made below must not find    */
        /* it, or it would only keep the window open.                       */
        bPanelSettled = Jsn_EditConfirmPendingPanel();
        Jsn_bCloseAsked = false;
        if (!bPanelSettled)
        {   /* Cancel at the panel's question, or its values would not   */
            /* save: the window and the typing stay.                     */
            if (objPresentation != null)
            {   /* Under the worker's question: it is answered Cancel, so   */
                /* its close stops (Eui_OnEditorFact).                      */
                Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_DECLINED);
            }
            else
            {   /* The window's own close: only the coordinator told   */
                /* that this window's own work has returned.           */
                Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
            }
            return;
        }

        if (objPresentation != null &&
            !objPresentation.PRS_objQuestion.QST_IsCurrent(objPresentation))
        {   /* The worker's question was replaced while the panel's was     */
            /* up (an Exit's, say): an answer to a question that is gone,   */
            /* so nothing more is asked and nothing dropped; the            */
            /* coordinator is told, and the worker asks again.              */
            Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
            return;
        }

        if (!Jsn_doc.Vd_bModified)
        {   /* Nothing unsaved now (the panel's typing dropped, or saved   */
            /* since): no close question.  The close made again, which     */
            /* finds nothing to ask about and closes -- its FormClosed     */
            /* answering the worker's question, if there is one.           */
            Close();
            return;
        }

        if (objPresentation == null && !Jsn_file.OFD_CanRequestUserFile())
        {   /* The user's own close (no worker question) while a save       */
            /* could not start (OFD_CanRequestUserFile): kept open, with    */
            /* no question, as the text editor does; the coordinator told   */
            /* that this window's own work has returned.  A close the       */
            /* worker asks for still gets its question -- the worker        */
            /* asked for it.                                                */
            Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
            return;
        }

        /* Which question: the ordinary one, unless this is an image        */
        /* entry whose image is gone, which cannot be saved there -- then   */
        /* the recovery question (EditorSaveMenu.cs).                       */
        bOrdinary = Jsn_file.OFD_bHostFile || !Jsn_file.OFD_bDetached;

        /* Save As Image is offered only by the ordinary question, only on   */
        /* the user's own close, and only while the Save As menu would       */
        /* offer its Image item -- the target picker and an image open.      */
        /* Under the worker's close question it is greyed: the worker runs   */
        /* only a Windows Save As inside a close (Sys_CloseEditors), and     */
        /* an image Save As posted then could run only after that close      */
        /* had ended.                                                        */
        bImageOpen = bOrdinary && objPresentation == null && Jsn_file.OFD_pfnPickTarget != null &&
            Jsn_file.OFD_pfnImageAvailable != null && Jsn_file.OFD_pfnImageAvailable();

        /* A test's hook may answer the question instead (none in the   */
        /* program): its choice acted on at once, by the code a shown   */
        /* question's answer runs through.                              */
        bAnswered = false;
        enmChoice = RCA_enmRecoveryChoice.Cancel;
        Jsn_DlgHookClose(Jsn_file.OFD_Title(), bOrdinary, bImageOpen, ref bAnswered, ref enmChoice);
        if (bAnswered)
        {   /* Answered by the hook: no question shown. */
            Jsn_ActOnCloseChoice(enmChoice, objPresentation);
            return;
        }

        /* The question, kept with the display it answers, under this           */
        /* window's icon; its closing only posts (Jsn_OnCloseQuestionClosed).   */
        Jsn_objCloseQuestion = RCD_clsRecoveryDialog.RCD_Create(Jsn_fntDialog, Jsn_file, bOrdinary, bImageOpen);
        Jsn_objCloseShown = objPresentation;
        frmQuestion = Jsn_objCloseQuestion.RCD_ReadPopup();
        frmQuestion.Icon = this.Icon;
        frmQuestion.FormClosed += Jsn_OnCloseQuestionClosed;
        if (objPresentation != null)
        {   /* Under the worker's question: kept under it, so an Exit   */
            /* taking its place closes this one (QWN_Supersede).        */
            QWN_clsQuestionWindows.QWN_AttachEditorModal(objPresentation, frmQuestion);
        }

        /* Placed over the middle of this window (a modeless window is     */
        /* not centred by the framework), this window disabled, then the   */
        /* question shown modeless and owned by it -- so it stays above    */
        /* this window, and it alone of the program is held up.            */
        PRM_clsPromptForm.PRM_CentreOver(frmQuestion, this);
        this.Enabled = false;
        frmQuestion.Show(this);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnCloseQuestionClosed:                                             */
    /*                                                                        */
    /* The close question has closed (a button, Escape, its close box, or an  */
    /* Exit taking it down): posted to the window's queue, which acts on its  */
    /* choice (Jsn_ApplyCloseAnswer).  Nothing else is done here.             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the question's form.                                   */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the answer posted.                                          */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnCloseQuestionClosed(object objSender, FormClosedEventArgs evArgs)
    {
        /* Posted with the form it came from. */
        Jsn_objQueue.WQU_Post((int)Jsn_enmItem.CloseAnswer, objSender);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_ApplyCloseAnswer:                                                  */
    /*                                                                        */
    /* The close question's answer, run by the window's queue: the window     */
    /* enabled again and the question released, then its choice acted on      */
    /* (Jsn_ActOnCloseChoice).  A question an Exit took down, its display no  */
    /* longer current, acts on nothing: the worker asks again under its new   */
    /* display.                                                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     frmQuestion : the question's form that closed.                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the choice acted on, or nothing for a stale one.            */
    /*------------------------------------------------------------------------*/
    private void Jsn_ApplyCloseAnswer(Form frmQuestion)
    {
        RCD_clsRecoveryDialog objQuestion;
        PRS_clsPresentation objPresentation;
        RCA_enmRecoveryChoice enmChoice;

        if (Jsn_objCloseQuestion == null || frmQuestion != Jsn_objCloseQuestion.RCD_ReadPopup())
        {   /* A guard: not the question this window has up -- nothing   */
            /* to act on.                                                */
            return;
        }

        /* The question released and the window enabled again, before   */
        /* anything is acted on, so a save's messages and a Save As's   */
        /* picker meet a live window: its fields cleared, its record    */
        /* under the worker's question dropped, its choice read, its    */
        /* form disposed.                                               */
        objQuestion = Jsn_objCloseQuestion;
        objPresentation = Jsn_objCloseShown;
        Jsn_objCloseQuestion = null;
        Jsn_objCloseShown = null;
        if (objPresentation != null)
        {   /* It was kept under the worker's question: no longer. */
            QWN_clsQuestionWindows.QWN_DetachEditorModal(objPresentation, frmQuestion);
        }
        enmChoice = objQuestion.RCD_ReadAnswer();
        frmQuestion.Dispose();
        this.Enabled = true;
        this.Activate();

        if (objPresentation != null &&
            !objPresentation.PRS_objQuestion.QST_IsCurrent(objPresentation))
        {   /* The worker's question was answered or replaced while this    */
            /* was up -- an Exit took it down: no choice is acted on, the   */
            /* document stays, and the coordinator is told; an Exit         */
            /* declined shows the question again, which asks this close     */
            /* again.                                                       */
            Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
            return;
        }

        /* The choice acted on. */
        Jsn_ActOnCloseChoice(enmChoice, objPresentation);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_ActOnCloseChoice:                                                  */
    /*                                                                        */
    /* What the close question's choice does, whether the question answered   */
    /* or a test's hook did (Jsn_ShowCloseQuestion): Discard closes, the      */
    /* changes dropped; Save saves and closes once that save succeeds; either */
    /* Save As asks for its destination (closing afterwards only on the       */
    /* user's own close -- under the worker's question that question goes on  */
    /* once the save is applied); Cancel keeps the window, and under the      */
    /* worker's question answers it Cancel (EUI_LOCAL_DECLINED).  A save or   */
    /* Save As under the worker's question is made inside that display's      */
    /* record (QWN_Begin), so it answers that question: a save's capture      */
    /* takes the display then on the stack (OFD_ForSave), and so does a Save  */
    /* As's target request (Main_RequestEditorTarget, MainCommand.cs).        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     enmChoice       : the choice made.                                 */
    /*     objPresentation : the worker's close question's display the        */
    /*                       question answered; null for the window's own     */
    /*                       close.                                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the choice acted on; the coordinator told how it ended.     */
    /*------------------------------------------------------------------------*/
    private void Jsn_ActOnCloseChoice(RCA_enmRecoveryChoice enmChoice, PRS_clsPresentation objPresentation)
    {
        QSC_clsWindowScope objScope;
        JPI_clsPickIntent objIntent;
        bool bOwnClose;

        if (enmChoice == RCA_enmRecoveryChoice.Discard)
        {   /* Discard, said in so many words: the close made again,         */
            /* marked so it closes without asking (Jsn_PresentClose) --      */
            /* its FormClosed answering the worker's question, if there is   */
            /* one.  The mark is taken off whatever the close did, so it     */
            /* can never let a later close go unasked.                       */
            Jsn_bDiscardOnClose = true;
            try
            {   /* The close, which lets the changes go. */
                Close();
            }
            finally
            {   /* Unmarked (harmless on a window now closed). */
                Jsn_bDiscardOnClose = false;
            }
            return;
        }

        if (enmChoice == RCA_enmRecoveryChoice.Cancel)
        {   /* Cancel (Escape and the close box included): the window and   */
            /* its document stay.                                           */
            if (objPresentation != null)
            {   /* Under the worker's question: it is answered Cancel, so   */
                /* its close stops (Eui_OnEditorFact).                      */
                Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_DECLINED);
            }
            else
            {   /* The window's own close: only the coordinator told that   */
                /* this window's own work has returned.                     */
                Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
            }
            return;
        }

        /* A save or a Save As.  Under the worker's question it is made   */
        /* inside a record of that display (QWN_Begin), the one moment    */
        /* its question is again on the stack, so the save is captured    */
        /* as that question's answer and a Windows Save As goes back      */
        /* inside it.                                                     */
        objScope = null;
        if (objPresentation != null)
        {   /* The worker's question: its record begun. */
            objScope = QWN_clsQuestionWindows.QWN_Begin(objPresentation);
        }
        try
        {   /* The choice, by kind. */
            if (enmChoice == RCA_enmRecoveryChoice.Save)
            {   /* The save, asking to close once it succeeds; the close   */
                /* is posted once the window's own work has returned.  A   */
                /* save refused here has said why, and the window simply   */
                /* stays.                                                  */
                Jsn_FileSaveTo(Jsn_file, true);
            }
            else
            {   /* A Save As -- to the image or to Windows from the         */
                /* ordinary question, to Windows from the recovery one --   */
                /* its destination asked for through the Save As menu's     */
                /* own request, as File > Save As asks it.  It closes the   */
                /* window afterwards only when this is the user's own       */
                /* close; a picker cancelled then posts the close again     */
                /* (JSN_ApplyTargetChoice), and under the worker's          */
                /* question the worker asks again itself.                   */
                bOwnClose = objPresentation == null;
                objIntent = new JPI_clsPickIntent(this, Jsn_file, bOwnClose);
                SVM_clsSaveMenu.SVM_Request(this, Jsn_file,
                    enmChoice != RCA_enmRecoveryChoice.SaveAsImage, objIntent.JPI_OnChosen);
            }
        }
        finally
        {   /* The display's record ended, and the coordinator told that   */
            /* this window's own work has returned.                        */
            if (objScope != null)
            {   /* Begun above: ended. */
                QWN_clsQuestionWindows.QWN_End(objScope);
            }
            Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
        }
    }

    /*------------------------------------------------------------------------*/
    /* JSN_CompleteTreeSelection:                                             */
    /*                                                                        */
    /* The posted finish of a selection the user made while the panel held    */
    /* typing (JTE_Select, posted by Jsn_OnTreeBeforeSelect, which held the   */
    /* move): the panel's question asked, and, unless it is cancelled, the    */
    /* entry selected by its position path -- which still finds it if         */
    /* saving the panel replaced its tree node.                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objIntent : the selection, as the event found it (its path).       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the entry selected, or the selection left where it was;     */
    /*            the posted call no longer counted either way.               */
    /*------------------------------------------------------------------------*/
    internal void JSN_CompleteTreeSelection(JTE_clsTreeIntent objIntent)
    {
        try
        {   /* The selection finished; the finally below ends the count on   */
            /* every way out.                                                */
            if (!IsDisposed && Jsn_EditConfirmPendingPanel())
            {   /* The window is still there and the panel settled (saved   */
                /* or dropped): the entry selected by path, from code, so   */
                /* Jsn_OnTreeBeforeSelect lets it through this time; its    */
                /* selection loads the panel.                               */
                Jsn_tree.VTV_Select(objIntent.JTE_lstPath);
            }
        }
        finally
        {   /* The posted call has returned, however it ended -- a Cancel   */
            /* or a throw included: no longer counted (Jsn_iPostedTree),    */
            /* any Save answers and close decisions held are posted, and    */
            /* the editor coordinator is told the window's work returned.   */
            Jsn_iPostedTree--;
            Jsn_PostReadyAcknowledgements();
            Jsn_PostReadyCloses();
            Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
        }
    }

    /*-------------------------------------------------------------------------*/
    /* JSN_CompleteTreeCommit:                                                 */
    /*                                                                         */
    /* The posted finish of an in-place edit (JTE_Commit, posted by            */
    /* Jsn_OnTreeAfterLabelEdit once the edit box closed): its text applied    */
    /* to the document, or a cancelled edit dropped (Jsn_TreeCommitInlineEdit) */
    /* -- and, when the user's own close was waiting for the edit              */
    /* (Jsn_PresentClose), the close tried again afterwards.                   */
    /*                                                                         */
    /* Arguments:                                                              */
    /*     objIntent : the edit: its entry's path, its text (null for a        */
    /*                 cancel), and whether a close waits on it.               */
    /*                                                                         */
    /* Returns:                                                                */
    /*     void : the edit applied or dropped; a waiting close posted.         */
    /*-------------------------------------------------------------------------*/
    internal void JSN_CompleteTreeCommit(JTE_clsTreeIntent objIntent)
    {
        try
        {   /* The edit applied; the finally below ends the count on every   */
            /* way out.                                                      */
            if (!IsDisposed)
            {   /* The window is still there: the edit committed (or a   */
                /* cancel's label redrawn) by the commit's own rules.    */
                Jsn_TreeCommitInlineEdit(objIntent.JTE_lstPath, objIntent.JTE_sText);
            }
        }
        finally
        {   /* The posted call has returned, however it ended: no longer     */
            /* counted, any Save answers and close decisions held are        */
            /* posted, and the editor coordinator is told the window's       */
            /* work returned -- which, under a worker's close question, is   */
            /* what lets the worker ask again.                               */
            Jsn_iPostedTree--;
            Jsn_PostReadyAcknowledgements();
            Jsn_PostReadyCloses();
            Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
        }

        if (objIntent.JTE_bCloseAfterCommit && !IsDisposed)
        {   /* The user's own close waited for this edit: tried again,     */
            /* posted so it runs after this call has unwound rather than   */
            /* nesting a close inside the posted call.                     */
            BeginInvoke(new MethodInvoker(Close));
        }
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_FileConfirmOtherExtension:                                         */
    /*                                                                        */
    /* Save As keeps the document's format -- the editor does not convert     */
    /* between formats -- so a name whose extension belongs to ANOTHER        */
    /* format would give a file that does not match its name.  The user is    */
    /* asked first; a name that says nothing (or says this format) needs no   */
    /* question.                                                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sPath : the file chosen.                                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true to go ahead and write it.                              */
    /*------------------------------------------------------------------------*/
    bool Jsn_FileConfirmOtherExtension(string sPath)
    {
        int iNamed;                                      // the format the name says

        /* The format the chosen name's extension says, if any. */
        iNamed = VoorheesFormat.Vf_DetectFromPath(sPath);
        if (iNamed == VoorheesFormat.VF_DETECT || iNamed == Jsn_doc.Vd_iFormat)
        {   /* The name agrees, or says nothing. */
            return(true);
        }

        /* A name of another format: ask. */
        return(Jsn_DlgMessage("This document will be written as "
            + VoorheesFormat.Vf_Name(Jsn_doc.Vd_iFormat) + ", though the name "
            + Path.GetFileName(sPath) + " is a "
            + VoorheesFormat.Vf_Name(iNamed) + " name (the editor does not "
            + "convert between formats).\r\n\r\nSave anyway?",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes);
    }





    /*------------------------------------------------------------------------*/
    /* EDIT: undo, redo, and the edit panel's Save and Discard                */
    /*------------------------------------------------------------------------*/

    /*------------------------------------------------------------------------*/
    /* JSN_WriteBytes:                                                        */
    /*                                                                        */
    /* Write a Windows file whole without ever writing it in place: the bytes */
    /* go into a temporary file beside it, which then replaces it, so a       */
    /* failure part-way leaves the old file as it was.  Used for a save to a  */
    /* Windows file and for an image entry's private copy after its save      */
    /* (JSC_WriteWindows), off the UI thread: it touches no document and no   */
    /* control.  A failure throws, after the temporary file is removed.  What */
    /* File.Replace leaves when IT fails is Windows' (documented: the file    */
    /* may be left under either name), so a Windows save that fails keeps     */
    /* the document unsaved, and a private copy that fails refuses Reopen As  */
    /* (Jsn_FileSaveTo, Jsn_ApplyAcknowledgement).                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     abFile : the file's whole new contents (only read).                */
    /*     sPath  : the file to write.                                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the file holds the new contents; a failure throws.          */
    /*------------------------------------------------------------------------*/
    internal static void JSN_WriteBytes(byte[] abFile, string sPath)
    {
        string sTemporary;
        FileStream stmTemporary;

        /* The temporary file's name: the target's own, with 8 random hex     */
        /* digits and ".bifrostfm.tmp" -- beside it, so on the same volume,   */
        /* where a replace is a rename.  CreateNew below refuses a name       */
        /* that is already taken.                                             */
        sTemporary = sPath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bifrostfm.tmp";
        try
        {   /* The write and the replace, one catch for both.  A NEW file   */
            /* (it never replaces anything), written and flushed to the     */
            /* disk itself before it takes the name.                        */
            stmTemporary = new FileStream(sTemporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using (stmTemporary)
            {   /* Write, then flush through to the disk; closed when the   */
                /* block ends, a failure included.                          */
                stmTemporary.Write(abFile, 0, abFile.Length);
                stmTemporary.Flush(true);
            }

            if (File.Exists(sPath))
            {   /* The file exists: replaced by the temporary one, with no   */
                /* backup copy (null).  The true lets the replace go on      */
                /* when the old file's attributes or ACLs cannot be          */
                /* carried over (documented), so they may be lost.           */
                File.Replace(sTemporary, sPath, null, true);
            }
            else
            {   /* A new file: the temporary one simply takes the name. */
                File.Move(sTemporary, sPath);
            }
        }
        catch
        {   /* The write or the replace failed: the temporary file removed,   */
            /* then the failure thrown on as it was.  The delete is by        */
            /* name: it does not know whether CreateNew made the file --      */
            /* were one of that random name there already, CreateNew          */
            /* refused it and this deletes it (not guarded against; the       */
            /* chance is about one in four billion).                          */
            try
            {   /* The removal, which must not hide the real failure. */
                if (File.Exists(sTemporary))
                {   /* There: deleted. */
                    File.Delete(sTemporary);
                }
            }
            catch (Exception exCleanup)
            {   /* Not removable: left, logged, and the real failure   */
                /* thrown below.                                       */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("structured candidate cleanup", exCleanup.ToString());
                }
            }

            /* The failure, unchanged, to the caller (BMF_Run hands it   */
            /* back to whoever ran the write), which reports it.         */
            throw;
        }
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_EditFocusedTextBox:                                                */
    /*                                                                        */
    /* The panel text box that has the keyboard, if any: Ctrl+Z and           */
    /* Ctrl+Y belong to it then, not to the document.                         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     TextBox : the focused Key, Value, Comment or Raw box, or null.     */
    /*------------------------------------------------------------------------*/
    TextBox Jsn_EditFocusedTextBox()
    {
        if (Jsn_txtValue.Focused)
        {   /* The Value box. */
            return(Jsn_txtValue);
        }

        if (Jsn_txtKey.Focused)
        {   /* The Key box. */
            return(Jsn_txtKey);
        }

        if (Jsn_txtComment.Focused)
        {   /* The Comment box. */
            return(Jsn_txtComment);
        }

        if (Jsn_txtRaw.Focused)
        {   /* The Raw box. */
            return(Jsn_txtRaw);
        }

        /* None: the keyboard is elsewhere. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_EditUndo:                                                          */
    /*                                                                        */
    /* Edit > Undo (Ctrl+Z), checked in this order:                           */
    /*     o Typing in a panel text box: undo the typing.  The menu           */
    /*       shortcut would otherwise take Ctrl+Z away from the box.          */
    /*     o An in-tree edit box is open: do nothing; it is finishing.        */
    /*     o The panel holds unsaved typed changes: those are the newest      */
    /*       change, so they are what gets undone (discarded).                */
    /*     o Otherwise the document's newest step is undone (Vd_Undo) and     */
    /*       the tree follows its changes, last first.                        */
    /* The history goes back only to the last acknowledged save of the        */
    /* document as it was then (Vd_MarkSaved).  When a save wrote an older    */
    /* revision and the newer history was kept, undoing every step still      */
    /* leaves the document modified: its start no longer matches the file     */
    /* (Jsn_ApplyAcknowledgement, VD_MarkBaseUnsaved).                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the document, panel or text box steps back once.            */
    /*------------------------------------------------------------------------*/
    void Jsn_EditUndo()
    {
        TextBox txtFocused;        // a focused panel text box
        VoorheesEditStep objStep;  // the step undone

        /* Counted as local work and as a document change for the whole     */
        /* action, as in Jsn_ContextAddChild: a Save answer arriving        */
        /* meanwhile (the format's edit warning is a message box, which     */
        /* can let one in) is held, not applied while the step is undone.   */
        Jsn_iLocalWork = checked(Jsn_iLocalWork + 1);
        Jsn_iModelWork = checked(Jsn_iModelWork + 1);
        try
        {   /* The action itself; the finally below ends the counting on       */
            /* every way out, the early returns included.                      */
            /* The menu takes Ctrl+Z before the focused box sees it, so the    */
            /* box's own undo is called from here when one has the keyboard.   */
            txtFocused = Jsn_EditFocusedTextBox();
            if (txtFocused != null)
            {   /* Typing in a box: that box's own undo. */
                txtFocused.Undo();
                return;
            }

            if (Jsn_bLabelEditing)
            {   /* In-tree edit in progress: leave it to finish or cancel. */
                return;
            }

            if (Jsn_bPanelDirty)
            {   /* Unsaved panel changes are the newest change: discard them. */
                Jsn_EditDiscard();
                return;
            }

            /* The document's newest step taken back, if there is one. */
            objStep = Jsn_doc.Vd_Undo();
            if (objStep == null)
            {   /* Nothing to undo. */
                return;
            }

            /* The document has changed: a new revision                         */
            /* (Jsn_DocAdvanceRevision), so an earlier save's answer, applied   */
            /* later, does not take this document for the one it saved.  Its    */
            /* place before the tree's update is not forced -- an answer        */
            /* arriving during that update is held anyway, since this action    */
            /* counts as a document change.                                     */
            Jsn_DocAdvanceRevision();

            /* The tree follows the step's changes backwards (true), the       */
            /* title and comment state are brought up to date, and the         */
            /* entry the step touched is selected and loaded into the panel.   */
            Jsn_DocApplyStepToTree(objStep, true);
            Jsn_DocAfterChange(objStep);
            Jsn_tree.VTV_Select(objStep.Ves_FocusAfterUndo());
            Jsn_EditLoadSelected();
        }
        finally
        {   /* The action has returned, progress window and all, however       */
            /* it ended: the counts come down, any Save answers and close      */
            /* decisions held while it ran are posted for a later turn,        */
            /* and the editor coordinator is told this window's own work       */
            /* has returned (EUI_LOCAL_RETURN, CommandEditorCoordinator.cs).   */
            Jsn_iModelWork--;
            Jsn_iLocalWork--;
            Jsn_PostReadyAcknowledgements();
            Jsn_PostReadyCloses();
            Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_EditRedo:                                                          */
    /*                                                                        */
    /* Edit > Redo (Ctrl+Y): makes the most recently undone step again, and   */
    /* the tree follows.  Does nothing while a text box or an in-tree edit    */
    /* has the keyboard.  Unsaved panel changes are settled first; saving     */
    /* them is a new change, which ends the redo history, so there may        */
    /* then be nothing left to redo.                                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the document steps forward once, if it can.                 */
    /*------------------------------------------------------------------------*/
    void Jsn_EditRedo()
    {
        VoorheesEditStep objStep;                           // the step redone

        /* Counted as local work and as a document change for the whole      */
        /* action, as in Jsn_EditUndo: a Save answer arriving meanwhile is   */
        /* held, not applied while the step is being redone.                 */
        Jsn_iLocalWork = checked(Jsn_iLocalWork + 1);
        Jsn_iModelWork = checked(Jsn_iModelWork + 1);
        try
        {   /* The action itself; the finally below ends the counting on   */
            /* every way out, the early returns included.                  */
            if (Jsn_EditFocusedTextBox() != null || Jsn_bLabelEditing)
            {   /* Keyboard belongs to a text box or an in-tree edit. */
                return;
            }

            if (!Jsn_doc.Vd_CanRedo())
            {   /* Nothing to redo. */
                return;
            }

            if (!Jsn_EditConfirmPendingPanel())
            {   /* User cancelled at the unsaved-panel question. */
                return;
            }

            /* The most recently undone step made again, if one is left. */
            objStep = Jsn_doc.Vd_Redo();
            if (objStep == null)
            {   /* Saving the panel was a new change and cleared the redo history. */
                return;
            }

            /* The document has changed: a new revision, as in Jsn_EditUndo. */
            Jsn_DocAdvanceRevision();

            /* The tree follows the step's changes forwards (false), the   */
            /* title and comment state follow, and the step's entry is     */
            /* selected and loaded.                                        */
            Jsn_DocApplyStepToTree(objStep, false);
            Jsn_DocAfterChange(objStep);
            Jsn_tree.VTV_Select(objStep.Ves_FocusAfterDo());
            Jsn_EditLoadSelected();
        }
        finally
        {   /* The action has returned, however it ended: the counts come   */
            /* down, held Save answers and close decisions are posted for   */
            /* a later turn, and the editor coordinator is told this        */
            /* window's own work has returned (EUI_LOCAL_RETURN).           */
            Jsn_iModelWork--;
            Jsn_iLocalWork--;
            Jsn_PostReadyAcknowledgements();
            Jsn_PostReadyCloses();
            Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_EditDiscard:                                                       */
    /*                                                                        */
    /* Discard button: throws away whatever was typed into the panel and      */
    /* reloads it from the selected entry.  Also used wherever pending        */
    /* panel changes are to be dropped.                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the panel shows the document again and is not dirty.        */
    /*------------------------------------------------------------------------*/
    void Jsn_EditDiscard()
    {
        /* Reloading refreshes every field and clears the dirty flags. */
        Jsn_EditLoadSelected();
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_EditConfirmPendingPanel:                                           */
    /*                                                                        */
    /* If the panel holds typed changes not yet saved to the document,        */
    /* asks whether to save them: Yes saves, No discards, Cancel stops.       */
    /* Called before anything that would reload the panel and so lose         */
    /* them: selecting another entry, tree edits, undo and redo, find and     */
    /* replace, file operations, and closing the window.                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true to go ahead (nothing pending, saved or                 */
    /*            discarded); false if the user cancelled or the values       */
    /*            could not be saved (bad number, used key, ...).             */
    /*------------------------------------------------------------------------*/
    bool Jsn_EditConfirmPendingPanel()
    {
        DialogResult enmAnswer;                             // the user's answer

        if (!Jsn_bPanelDirty)
        {   /* Nothing pending. */
            return(true);
        }

        /* Changes are pending: Yes / No / Cancel, as a warning. */
        enmAnswer = Jsn_DlgMessage(
            "The edit panel has changes that haven't been saved to "
            + "the document.\r\n\r\nSave them now?",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Warning);

        if (enmAnswer == DialogResult.Yes)
        {   /* Save them; go ahead only if they were valid and saved. */
            return(Jsn_EditApply());
        }

        if (enmAnswer == DialogResult.No)
        {   /* Drop them and go ahead. */
            Jsn_EditDiscard();
            return(true);
        }

        /* Cancel: stay put with the edits still in the panel. */
        return(false);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnApplyClick:                                                      */
    /*                                                                        */
    /* Save button handler.  The result does not matter here: when the        */
    /* save is refused, Jsn_EditApply has already told the user why.          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the Save button; unused.                               */
    /*     evArgs : unused event arguments.                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : see Jsn_EditApply.                                          */
    /*------------------------------------------------------------------------*/
    void Jsn_OnApplyClick(object objSender, EventArgs evArgs)
    {
        /* The panel written into the document; its answer is not needed. */
        Jsn_EditApply();
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_EditApply:                                                         */
    /*                                                                        */
    /* Writes the edit panel into the selected entry, as ONE undo step that   */
    /* is all or nothing: if any part is refused, the parts already made      */
    /* are taken back (Vd_AbandonStep) and the user is told why.              */
    /*     o A Raw edit: the entry is replaced by the Raw text, parsed        */
    /*       strictly and kept exactly as typed (Vd_ActRaw).                  */
    /*     o A comment entry: its words (Vd_ActSetCommentText).               */
    /*     o A value: the new value from the Type and Value boxes             */
    /*       (Jsn_EditBuildValue; turning a container with items into another */
    /*       type asks first), then a changed key (Vd_ActRename: never an     */
    /*       empty or used key), then a changed comment                       */
    /*       (Vd_ActSetLineComment).                                          */
    /* Changes are made by POSITION, so with a duplicated key only the        */
    /* selected copy changes.  Saving a panel that matches the document       */
    /* records nothing and does not mark the file modified.                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true if the panel now matches the document; false if a      */
    /*            check failed or the user declined (told why already).       */
    /*------------------------------------------------------------------------*/
    bool Jsn_EditApply()
    {
        List<int> lstPath;         // the entry's path
        VoorheesItem objItem;      // the entry
        VoorheesEditStep objStep;  // the Save's undo step
        VoorheesNode nodNew;       // value built from the panel
        string sError;             // why a part was refused
        bool bDeclined;            // the user declined a type change

        /* Counted as local work and as a document change for the whole       */
        /* Save -- its question, the changes and the tree's update -- as in   */
        /* Jsn_ContextAddChild: a Save answer arriving meanwhile is held,     */
        /* so it cannot clear the undo history under this step.               */
        Jsn_iLocalWork = checked(Jsn_iLocalWork + 1);
        Jsn_iModelWork = checked(Jsn_iModelWork + 1);
        try
        {   /* The panel's Save itself; the finally below ends the counting   */
            /* on every way out.                                              */
            /* The selected entry, by position, as the document has it now.   */
            lstPath = Jsn_TreeSelectedPath();
            objItem = null;
            if (lstPath != null)
            {   /* Something is selected: its entry. */
                objItem = Jsn_doc.Vd_ItemAt(lstPath);
            }

            if (objItem == null)
            {   /* Nothing to save into: drop the edits. */
                Jsn_bPanelDirty = false;
                return(true);
            }

            /* One undo step for the whole Save, nothing refused yet. */
            objStep = Jsn_doc.Vd_BeginStep(lstPath);
            sError = null;

            if (Jsn_bRawDirty)
            {   /* The Raw box: the entry exactly as typed. */
                sError = Jsn_doc.Vd_ActRaw(objStep, lstPath, Jsn_EditTextFromBox(Jsn_txtRaw.Text,
                    Jsn_sRawSource));
            }
            else if (objItem.Vi_iKind == VoorheesItem.VI_COMMENT)
            {   /* A comment entry: its words. */
                sError = Jsn_doc.Vd_ActSetCommentText(objStep, lstPath,
                    Jsn_EditTextFromBox(Jsn_txtValue.Text, objItem.Vi_sComment));
            }
            else
            {   /* A value: value, then key, then comment. */
                nodNew = Jsn_EditBuildValue(objItem, out sError, out bDeclined);
                if (bDeclined)
                {   /* The user said no to losing a container's items. */
                    Jsn_doc.Vd_AbandonStep(objStep);
                    return(false);
                }

                if (sError == null)
                {   /* A valid value: set it. */
                    sError = Jsn_doc.Vd_ActSetValue(objStep, lstPath, nodNew);
                }

                if (sError == null && objItem.Vi_iKind == VoorheesItem.VI_MEMBER
                    && Jsn_txtKey.Text != objItem.Vi_sKey)
                {   /* The key was edited: rename (taken exactly as typed: spaces are legal in keys). */
                    sError = Jsn_doc.Vd_ActRename(objStep, lstPath, Jsn_txtKey.Text);
                }

                if (sError == null && Jsn_txtComment.Text != Jsn_sCommentShown)
                {   /* The comment was edited. */
                    sError = Jsn_doc.Vd_ActSetLineComment(objStep, lstPath,
                        Jsn_EditTextFromBox(Jsn_txtComment.Text, ""));
                }
            }

            if (!Jsn_DocFinish(objStep, sError))
            {   /* Refused (the user has been told why); nothing has changed. */
                return(false);
            }

            /* The panel's contents are in the document now: show it from there. */
            Jsn_EditLoadSelected();

            /* Saved. */
            return(true);
        }
        finally
        {   /* The panel's Save has returned, questions and all, however     */
            /* it ended: the counts come down, held Save answers and close   */
            /* decisions are posted for a later turn, and the editor         */
            /* coordinator is told this window's own work has returned.      */
            Jsn_iModelWork--;
            Jsn_iLocalWork--;
            Jsn_PostReadyAcknowledgements();
            Jsn_PostReadyCloses();
            Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
        }
    }
    /*------------------------------------------------------------------------*/
    /* Jsn_EditBuildValue:                                                    */
    /*                                                                        */
    /* The value the panel describes, for Jsn_EditApply: the kind chosen in   */
    /* the Type box with the Value box's text or the checkbox, read by the    */
    /* format's rules (VoorheesFormat.Vf_ValueFromPanel: for JSON a number    */
    /* must read as one, a container already of the chosen kind is kept       */
    /* with its items, a string keeps its line endings).  Two things are      */
    /* the editor's own:                                                      */
    /*     o Comment : refused: a value cannot become a comment;              */
    /*     o turning a container WITH items into anything else deletes        */
    /*       them, so the user is asked first.                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objItem : selected value entry being edited.                       */
    /*     sError    : set to why the panel's value is refused, else null.    */
    /*     bDeclined : set to true when the user declined to lose items.      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the value, or null when refused or declined.        */
    /*------------------------------------------------------------------------*/
    VoorheesNode Jsn_EditBuildValue(VoorheesItem objItem, out string sError,
        out bool bDeclined)
    {
        VoorheesNode nodOld;  // the entry's value now
        VoorheesNode nodNew;  // the value built
        int iKind;            // the Type box's choice

        /* Nothing refused or declined yet; the value the entry has now,   */
        /* and the kind the Type box asks for.                             */
        sError = null;
        bDeclined = false;
        nodOld = objItem.Vi_node;
        iKind = Jsn_EditSelectedKind();

        if (iKind == JSN_KIND_COMMENT)
        {   /* A value cannot be turned into a comment. */
            sError = "A value can't become a comment.  To add a comment, "
                + "use Add Child > Comment on its object or array.";
            return(null);
        }

        /* The format reads the panel. */
        nodNew = VoorheesFormat.Vf_ValueFromPanel(Jsn_doc, iKind, Jsn_txtValue.Text,
            Jsn_chkBoolValue.Checked, nodOld, out sError);

        if (nodNew != null && nodOld.Vn_IsContainer()
            && !ReferenceEquals(nodNew, nodOld) && nodOld.Vn_Count() > 0)
        {   /* A container with items is becoming another type, which deletes them: ask first. */
            if (Jsn_DlgMessage("Changing this "
                + VoorheesFormat.Vf_KindName(Jsn_doc.Vd_iFormat, nodOld.Vn_iKind).ToLower() + " to "
                + Jsn_cmbType.Text.ToLower() + " will delete its "
                + nodOld.Vn_Count().ToString() + " item(s).\r\n\r\n"
                + "Continue?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
            {   /* Declined: change nothing. */
                bDeclined = true;
                return(null);
            }
        }

        /* The value, or null with the reason. */
        return(nodNew);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_EditTextForBox:                                                    */
    /*                                                                        */
    /* Prepares text for a multi-line box.  The Windows text box only         */
    /* breaks lines at CRLF, so text holding bare LFs would show on one       */
    /* line; every line break becomes CRLF for display.                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText : text in the document's line endings.                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the same text with every line break as CRLF.              */
    /*------------------------------------------------------------------------*/
    string Jsn_EditTextForBox(string sText)
    {
        /* CRLF to LF first, so existing CRLFs are not doubled. */
        return(sText.Replace("\r\n", "\n").Replace("\n", "\r\n"));
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_EditTextFromBox:                                                   */
    /*                                                                        */
    /* Text from a box (which uses CRLF) back in the line endings of the      */
    /* text it came from: CRLF if that used CRLF, LF if it used LF, and       */
    /* otherwise the document's own line ending.  So an edited entry          */
    /* keeps the file's line endings.                                         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText : edited box text.                                           */
    /*     sSample : originally displayed text; empty if none.                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the text with fitting line breaks.                        */
    /*------------------------------------------------------------------------*/
    string Jsn_EditTextFromBox(string sText, string sSample)
    {
        string sNewline;                                 // the line ending to use

        if (sSample.IndexOf("\r\n", StringComparison.Ordinal) >= 0)
        {   /* The original used CRLF. */
            sNewline = "\r\n";
        }
        else if (sSample.IndexOf('\n') >= 0)
        {   /* The original used LF. */
            sNewline = "\n";
        }
        else
        {   /* No line breaks to go by: the document's own. */
            sNewline = Jsn_doc.Vd_sNewline;
        }

        /* Every box line break as the chosen ending. */
        return(sText.Replace("\r\n", "\n").Replace("\n", sNewline));
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_EditUpdateValueEditor:                                             */
    /*                                                                        */
    /* Shows the right value editor for the kind chosen in the Type box:      */
    /* the checkbox for a boolean kind (Vf_IsBooleanKind), the Value box      */
    /* for everything else.  When the user switches TO a boolean the          */
    /* checkbox starts out matching the Value box ("true" ticks it).  The     */
    /* Value box is greyed out for kinds with no value to type                */
    /* (Vf_KindHasText: JSON null, object and array).                         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : txtValue and chkBoolValue are shown, hidden, enabled or     */
    /*            disabled.                                                   */
    /*------------------------------------------------------------------------*/
    void Jsn_EditUpdateValueEditor()
    {
        int iKind;        // kind chosen in the Type box
        string sCurrent;  // Value box text, for the checkbox

        /* The kind the Type box now names. */
        iKind = Jsn_EditSelectedKind();

        if (iKind != JSN_KIND_COMMENT
            && VoorheesFormat.Vf_IsBooleanKind(Jsn_doc.Vd_iFormat, iKind))
        {   /* Boolean: show the checkbox in place of the Value box. */
            Jsn_txtValue.Visible = false;
            Jsn_chkBoolValue.Visible = true;

            /* Start the checkbox from the Value box's text. */
            sCurrent = Jsn_txtValue.Text.Trim().ToLower();
            Jsn_chkBoolValue.Checked = (sCurrent == "true");
        }
        else
        {   /* Any other type: show the Value box. */
            Jsn_chkBoolValue.Visible = false;
            Jsn_txtValue.Visible = true;
        }

        if (iKind == JSN_KIND_COMMENT)
        {   /* A comment entry: its words are typed in the Value box. */
            Jsn_txtValue.Enabled = true;
        }
        else
        {   /* A value: typed only if its kind has text (not JSON null, object, array). */
            Jsn_txtValue.Enabled = VoorheesFormat.Vf_KindHasText(Jsn_doc.Vd_iFormat,
                iKind);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_EditFillTypeBox:                                                   */
    /*                                                                        */
    /* Gives the Type box the value kinds for an entry, each named by its     */
    /* format (Vf_KindName), followed by Comment.  Left alone when the        */
    /* kinds are the ones it already holds, so moving between entries of      */
    /* one format does not rebuild it; when they change, the panel is laid    */
    /* out again, since the box is sized to its longest entry.                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstKinds : the kinds, in order (from Vf_PanelKinds).               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the Type box and retained kind list agree; nothing is       */
    /*            selected when they changed.                                 */
    /*------------------------------------------------------------------------*/
    void Jsn_EditFillTypeBox(List<int> lstKinds)
    {
        bool bSame;                                      // the box already holds these kinds
        int i;

        /* The same kinds in the same order: nothing to do. */
        bSame = (lstKinds.Count == Jsn_lstTypeKinds.Count);
        for (i = 0; bSame && i < lstKinds.Count; i++)
        {   /* One kind per iteration, until the first that differs. */
            bSame = (lstKinds[i] == Jsn_lstTypeKinds[i]);
        }

        if (bSame)
        {   /* Already filled with them. */
            return;
        }

        /* Rebuild: each kind by its format's name, then Comment. */
        Jsn_lstTypeKinds = new List<int>(lstKinds);
        Jsn_cmbType.Items.Clear();
        for (i = 0; i < lstKinds.Count; i++)
        {   /* One kind per iteration, in Jsn_lstTypeKinds' order. */
            Jsn_cmbType.Items.Add(VoorheesFormat.Vf_KindName(Jsn_doc.Vd_iFormat,
                lstKinds[i]));
        }
        Jsn_cmbType.Items.Add("Comment");

        /* The box's width follows its entries. */
        Jsn_EditLayoutPanel();
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_EditSelectedKind:                                                  */
    /*                                                                        */
    /* The kind chosen in the Type box.                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the kind behind the chosen entry, or JSN_KIND_COMMENT for    */
    /*           the Comment entry (and when nothing is chosen).              */
    /*------------------------------------------------------------------------*/
    int Jsn_EditSelectedKind()
    {
        int iIndex;                                      // the chosen entry

        /* The chosen entry's index: the box's entries are the kinds in   */
        /* Jsn_lstTypeKinds' order, then Comment last.                    */
        iIndex = Jsn_cmbType.SelectedIndex;
        if (iIndex >= 0 && iIndex < Jsn_lstTypeKinds.Count)
        {   /* A value kind's entry. */
            return(Jsn_lstTypeKinds[iIndex]);
        }

        /* The last entry, Comment (or none). */
        return(JSN_KIND_COMMENT);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_EditSelectKind:                                                    */
    /*                                                                        */
    /* Chooses a kind's entry in the Type box.                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind : a kind the box holds, or JSN_KIND_COMMENT.                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the Type selection changes and its handler runs; a kind     */
    /*            the box does not hold selects nothing.                      */
    /*------------------------------------------------------------------------*/
    void Jsn_EditSelectKind(int iKind)
    {
        if (iKind == JSN_KIND_COMMENT)
        {   /* Comment: always the last entry. */
            Jsn_cmbType.SelectedIndex = Jsn_cmbType.Items.Count - 1;
            return;
        }

        /* A value kind: its entry, or -1 (nothing) if it is not offered. */
        Jsn_cmbType.SelectedIndex = Jsn_lstTypeKinds.IndexOf(iKind);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_EditLoadSelected:                                                  */
    /*                                                                        */
    /* Loads the selected entry into the panel (Jsn_EditLoadPanel), or clears */
    /* the dirty flags when nothing is selected.                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the panel shows the selected entry.                         */
    /*------------------------------------------------------------------------*/
    void Jsn_EditLoadSelected()
    {
        if (Jsn_tree.SelectedNode == null || Jsn_tree.SelectedNode.Tag == null)
        {   /* Nothing selected (an empty document): an empty, locked panel; forget any edits. */
            Jsn_EditClearPanel();
            Jsn_bPanelDirty = false;
            Jsn_bRawDirty = false;
            return;
        }

        /* An entry is selected: the panel loaded from it (which clears   */
        /* the dirty flags too).                                          */
        Jsn_EditLoadPanel(Jsn_tree.SelectedNode);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_EditClearPanel:                                                    */
    /*                                                                        */
    /* Empties the edit panel when no entry is selected (an empty document,   */
    /* such as a new INI file), so it never shows an entry of a document      */
    /* that is gone: every box empty and read-only, the Type box without a    */
    /* choice and disabled.  Jsn_EditLoadPanel unlocks everything again for   */
    /* the next entry selected.                                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the panel is empty and not dirty.                           */
    /*------------------------------------------------------------------------*/
    void Jsn_EditClearPanel()
    {
        /* Filling the panel from code: not an edit. */
        Jsn_bSuppressPanelDirty = true;

        /* Every box emptied and locked, the Type box without a choice      */
        /* and disabled, the Value box (not the checkbox) shown, and the    */
        /* remembered "as loaded" texts cleared.  The dirty flags are the   */
        /* caller's to clear (Jsn_EditLoadSelected).                        */
        Jsn_txtKey.Text = "";
        Jsn_txtKey.Enabled = false;
        Jsn_cmbType.SelectedIndex = -1;
        Jsn_cmbType.Enabled = false;
        Jsn_txtValue.Text = "";
        Jsn_txtValue.ReadOnly = true;
        Jsn_chkBoolValue.Visible = false;
        Jsn_txtValue.Visible = true;
        Jsn_txtComment.Text = "";
        Jsn_txtComment.ReadOnly = true;
        Jsn_txtRaw.Text = "";
        Jsn_txtRaw.ReadOnly = true;
        Jsn_sCommentShown = "";
        Jsn_sRawSource = "";
        Jsn_sRawShown = "";
        Jsn_bRawTruncated = false;

        /* Done: nothing to edit until an entry is selected. */
        Jsn_bSuppressPanelDirty = false;
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_EditLoadPanel:                                                     */
    /*                                                                        */
    /* Loads an entry into the edit panel, with suppressPanelDirty raised     */
    /* so loading does not count as editing, and every field unlocked:        */
    /*     o Key     : an object member's key (editable); an array            */
    /*                 element's index, or nothing, otherwise (read-only);    */
    /*     o Type    : the value's kind; Comment (fixed) for a comment        */
    /*                 entry;                                                 */
    /*     o Value   : a plain value's text (line breaks as CRLF), a          */
    /*                 comment entry's words (read-only when it holds         */
    /*                 several comments), nothing for a container;            */
    /*     o Comment : the comment on the entry's line, without markers       */
    /*                 (read-only when it holds several); hidden for a        */
    /*                 comment entry;                                         */
    /*     o Raw     : the entry's text exactly as in the file -- the start   */
    /*                 of it, read-only, when it is longer than JSN_RAW_MAX.  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     nodTree : selected entry's tree node.                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the panel shows the entry and is not dirty.                 */
    /*------------------------------------------------------------------------*/
    void Jsn_EditLoadPanel(TreeNode nodTree)
    {
        List<int> lstPath;          // the entry's path
        VoorheesItem objItem;       // the entry
        VoorheesNode nodContainer;  // its container
        string sLineComment;        // the comment on its line
        string sRaw;                // its raw text
        bool bTruncated;            // the raw text was cut
        bool bComment;              // it is a comment entry
        bool bLineComment;          // its format gives it a line comment (Comment box)

        /* The node's entry, as the document has it now. */
        lstPath = VTV_clsTreeView.VTV_PathOf(nodTree);
        objItem = Jsn_doc.Vd_ItemAt(lstPath);
        if (objItem == null)
        {   /* Tree out of step with the document: show nothing new. */
            return;
        }

        /* Its container (the path less its last step) and whether it is   */
        /* a comment entry -- the two facts every field below turns on.    */
        nodContainer = Jsn_doc.Vd_NodeAt(lstPath.GetRange(0, lstPath.Count - 1));
        bComment = (objItem.Vi_iKind == VoorheesItem.VI_COMMENT);

        /* Filling the panel from code: not an edit. */
        Jsn_bSuppressPanelDirty = true;

        /* Every field unlocked; Raw and the others lock each other   */
        /* again on the first edit (Jsn_EditMarkDirty).               */
        Jsn_txtKey.ReadOnly = false;
        Jsn_cmbType.Enabled = !bComment;
        Jsn_txtValue.ReadOnly = false;
        Jsn_chkBoolValue.Enabled = true;
        Jsn_txtComment.ReadOnly = false;

        /* Key: a member's key; an element's index; nothing otherwise. */
        if (objItem.Vi_iKind == VoorheesItem.VI_MEMBER)
        {   /* An object member: its key, editable. */
            Jsn_txtKey.Text = objItem.Vi_sKey;
            Jsn_txtKey.Enabled = true;
        }
        else if (!bComment && objItem.Vi_iKind == VoorheesItem.VI_ELEMENT
            && nodContainer.Vn_iKind != VoorheesNode.VN_DOCUMENT)
        {   /* An array element: its index, for information. */
            Jsn_txtKey.Text = nodContainer.Vn_ElementIndex(lstPath[lstPath.Count - 1])
                .ToString(CultureInfo.InvariantCulture);
            Jsn_txtKey.Enabled = false;
        }
        else
        {   /* The top value or a comment: no key. */
            Jsn_txtKey.Text = "";
            Jsn_txtKey.Enabled = false;
        }

        if (bComment)
        {   /* A comment entry: its words in the Value box. */
            Jsn_EditSelectKind(JSN_KIND_COMMENT);
            Jsn_txtValue.Text = Jsn_EditTextForBox(VoorheesFormat.Vf_CommentText(Jsn_doc.Vd_iFormat,
                objItem.Vi_sComment));
            Jsn_txtValue.ReadOnly = !VoorheesFormat.Vf_IsSingleComment(Jsn_doc.Vd_iFormat, objItem.Vi_sComment);
            Jsn_lblComment.Visible = false;
            Jsn_txtComment.Visible = false;
            Jsn_txtComment.Text = "";
        }
        else
        {   /* A value: the kinds its format offers there, its own chosen. */
            Jsn_EditFillTypeBox(VoorheesFormat.Vf_PanelKinds(Jsn_doc, lstPath));
            Jsn_EditSelectKind(objItem.Vi_node.Vn_iKind);
            if (objItem.Vi_node.Vn_IsContainer())
            {   /* A container: no typed value. */
                Jsn_txtValue.Text = "";
            }
            else
            {   /* Plain value: show it. */
                Jsn_txtValue.Text = Jsn_EditTextForBox(objItem.Vi_node.Vn_DisplayValue());
            }

            if (VoorheesFormat.Vf_IsBooleanKind(Jsn_doc.Vd_iFormat,
                objItem.Vi_node.Vn_iKind))
            {   /* Boolean: the checkbox takes the value. */
                Jsn_chkBoolValue.Checked = (objItem.Vi_node.Vn_sText == "true");
            }

            /* The comment on its line, where its format has one. */
            sLineComment = objItem.Vi_LineComment();
            bLineComment = VoorheesFormat.Vf_HasLineComment(Jsn_doc, nodContainer,
                objItem);
            Jsn_lblComment.Visible = bLineComment;
            Jsn_txtComment.Visible = bLineComment;
            Jsn_txtComment.Text = "";
            if (bLineComment)
            {   /* Shown: its words, on one line; read-only when it holds several comments. */
                Jsn_txtComment.Text = VoorheesFormat.Vf_CommentText(
                    Jsn_doc.Vd_iFormat, sLineComment)
                    .Replace("\r\n", " ").Replace("\n", " ");
                Jsn_txtComment.ReadOnly = (sLineComment != null
                    && !VoorheesFormat.Vf_IsSingleComment(Jsn_doc.Vd_iFormat,
                    sLineComment));
            }
        }

        /* The Comment box as loaded, so Jsn_EditApply sees whether it was edited. */
        Jsn_sCommentShown = Jsn_txtComment.Text;

        /* Raw: the entry as in the file, cut and read-only when huge. */
        sRaw = VoorheesFormat.Vf_RenderItemLimited(Jsn_doc, lstPath, JSN_RAW_MAX,
            out bTruncated);
        Jsn_sRawSource = sRaw;
        Jsn_bRawTruncated = bTruncated;
        if (bTruncated)
        {   /* Too long to edit here: its start, and why it stops.  The   */
            /* limit is a count of CHARACTERS, so it is said as one       */
            /* rather than as a size in bytes, which it is not.           */
            Jsn_txtRaw.Text = Jsn_EditTextForBox(sRaw) + "\r\n\r\n... (more than "
                + JSN_RAW_MAX.ToString("N0", CultureInfo.InvariantCulture)
                + " characters: too large to edit as raw text; edit its entries instead)";
            Jsn_txtRaw.ReadOnly = true;
        }
        else
        {   /* All of it, editable. */
            Jsn_txtRaw.Text = Jsn_EditTextForBox(sRaw);
            Jsn_txtRaw.ReadOnly = false;
        }

        /* The Raw box as loaded, so an edit of it can be told apart. */
        Jsn_sRawShown = Jsn_txtRaw.Text;

        /* Show the editor that suits the type. */
        Jsn_EditUpdateValueEditor();

        /* Done: the panel now matches the document. */
        Jsn_bSuppressPanelDirty = false;
        Jsn_bPanelDirty = false;
        Jsn_bRawDirty = false;
    }

    /*------------------------------------------------------------------------*/
    /* DOC: finishing edits, and replacing the document                       */
    /*------------------------------------------------------------------------*/

    /*------------------------------------------------------------------------*/
    /* Jsn_DocSetDocument:                                                    */
    /*                                                                        */
    /* Make a document the window's: the constructor's empty placeholder, a   */
    /* file just read (Jsn_FileLoadFromPath), or the main window's New        */
    /* (Jsn_OnLoad).  Everything that described the old document is reset,    */
    /* the tree is rebuilt from the new one, and the panel and title redrawn. */
    /* Whether the document holds a comment is the caller's to say: a file    */
    /* just read had it counted by the reader, off the UI thread, and a new   */
    /* document is asked directly -- so a large file is not walked again      */
    /* here.                                                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     docNew       : the document; the window keeps and edits it.        */
    /*     bHasComments : whether docNew holds a comment (Vd_HasComments, as  */
    /*                    counted by whoever made or read it).                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the document, tree, panel and title are the new one's.      */
    /*------------------------------------------------------------------------*/
    private void Jsn_DocSetDocument(VoorheesDocument docNew, bool bHasComments)
    {
        /* Counted as local work and as a document change for the whole   */
        /* set-up, as every document change is, so no Save answer is      */
        /* applied until the new document is in place.  (The tree's       */
        /* progress window runs no message loop of its own -- it only     */
        /* peeks at the queue, BusyModalForm.cs -- so this is the rule    */
        /* every change follows rather than a need found here.)           */
        Jsn_iLocalWork = checked(Jsn_iLocalWork + 1);
        Jsn_iModelWork = checked(Jsn_iModelWork + 1);
        try
        {   /* The set-up itself; the finally below ends the counting on       */
            /* every way out.                                                  */
            /*                                                                 */
            /* The document, and a new revision, so no answer to a save        */
            /* made before can take it for the document it saved.  What        */
            /* described the old one is reset: no stale save history, no       */
            /* failed private copy (a load reads the file afresh), nothing     */
            /* typed in the panel, the comment state the caller gave for the   */
            /* new document, and its format's one-time edit warning not yet    */
            /* given.                                                          */
            Jsn_doc = docNew;
            Jsn_lRevision = checked(Jsn_lRevision + 1);
            Jsn_bSaveBaselineStale = false;
            Jsn_bWorkCopyFailed = false;
            Jsn_bPanelDirty = false;
            Jsn_bRawDirty = false;
            Jsn_bHasComments = bHasComments;
            Jsn_bEditWarned = false;

            /* The tree rebuilt from the document (its top level; deeper    */
            /* levels are made as they are opened), the panel loaded from   */
            /* its selection, and the title and status bar redrawn.         */
            Jsn_tree.VTV_SetDocument(Jsn_doc);
            Jsn_EditLoadSelected();
            Jsn_StateUpdateTitle();
        }
        finally
        {   /* The set-up has returned, however it ended: the counts come   */
            /* down, held Save answers and close decisions are posted for   */
            /* a later turn, and the editor coordinator is told this        */
            /* window's own work has returned (EUI_LOCAL_RETURN).           */
            Jsn_iModelWork--;
            Jsn_iLocalWork--;
            Jsn_PostReadyAcknowledgements();
            Jsn_PostReadyCloses();
            Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_DocFinish:                                                         */
    /*                                                                        */
    /* Finishes every edit the window makes: a refused action is taken back   */
    /* and the user told why; one that changed anything is recorded for       */
    /* undo and shown -- in the tree, the title and status bar, and the       */
    /* selection.                                                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objStep : completed action's undo step and requested focus.        */
    /*     sError : why the action was refused, or null.                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : false when refused; true otherwise (changed or not).        */
    /*------------------------------------------------------------------------*/
    bool Jsn_DocFinish(VoorheesEditStep objStep, string sError)
    {

        /* Counted as local work and as a document change while the step     */
        /* is ended and shown: the refusal's message and the format's edit   */
        /* warning are message boxes, whose message loop can let a Save      */
        /* answer in, and it is held.  Every caller counts its own action    */
        /* too; this keeps the finish counted whoever calls it.              */
        Jsn_iLocalWork = checked(Jsn_iLocalWork + 1);
        Jsn_iModelWork = checked(Jsn_iModelWork + 1);
        try
        {   /* The finish itself; the finally below ends the counting on   */
            /* every way out.                                              */
            if (sError != null)
            {   /* Refused (the action set sError): none of it may stay.          */
                /* Whatever the step already made is reversed in the document,    */
                /* last change first -- for the edit panel's Save of several      */
                /* parts, the parts made before the one refused -- so the         */
                /* action is all or nothing, and nothing is recorded for undo.    */
                /* The tree needs no taking back: it is brought up to date only   */
                /* below, once a step has ended, which this path never reaches.   */
                Jsn_doc.Vd_AbandonStep(objStep);
                /* Then why, as a warning; false tells the caller the action   */
                /* did not happen.                                             */
                Jsn_DlgMessage(sError, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return(false);
            }

            if (!Jsn_doc.Vd_EndStep(objStep))
            {   /* Nothing changed (the edit put back what was there):        */
                /* Vd_EndStep made no undo step and left the modified state   */
                /* as it was, so there is nothing to show.  Still true: the   */
                /* action was not refused.                                    */
                return(true);
            }

            /* A real change, now the newest undo step.  The tree is brought     */
            /* to match the document before selecting its new entry: the         */
            /* selection below can only find an entry this very step created     */
            /* once the tree holds it (VTV_Select would otherwise fall back to   */
            /* the nearest ancestor).  false: the step was made, not undone.     */
            Jsn_DocAdvanceRevision();
            Jsn_DocApplyStepToTree(objStep, false);
            /* The comment state, the format's one-time warning, the title. */
            Jsn_DocAfterChange(objStep);
            /* The step's focus selected and scrolled into view (the entry   */
            /* the action named, else the first thing it changed).           */
            Jsn_tree.VTV_Select(objStep.Ves_FocusAfterDo());

            /* Then the edit panel loaded from that selection HERE, for       */
            /* every action -- not left to the tree's selection event,        */
            /* which the control is not relied on to raise when the node      */
            /* is already selected (a renamed member keeps its node).  Left   */
            /* to it, the panel could go on showing the member's old key,     */
            /* which a later Save of the panel would write back, or an        */
            /* entry just deleted.  A panel holding typed changes not yet     */
            /* applied is left as it is, so they are never thrown away        */
            /* unasked.                                                       */
            if (!Jsn_bPanelDirty)
            {   /* Nothing typed: show the selection as the document now has it. */
                Jsn_EditLoadSelected();
            }

            /* Changed and shown: the action went through. */
            return(true);
        }
        finally
        {   /* The finish has returned, warning and all, however it ended:   */
            /* the counts come down, held Save answers and close decisions   */
            /* are posted (they still wait while the caller's own count      */
            /* stands), and the editor coordinator is told.                  */
            Jsn_iModelWork--;
            Jsn_iLocalWork--;
            Jsn_PostReadyAcknowledgements();
            Jsn_PostReadyCloses();
            Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_DocApplyStepToTree:                                                */
    /*                                                                        */
    /* Brings the tree up to date with a step the document has just made,     */
    /* undone or redone, change by change, so only the entries the step       */
    /* touched are redrawn rather than the whole tree rebuilt.  A step of     */
    /* many changes can take long enough to need a progress window.           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objStep : recorded model changes to apply to the tree.             */
    /*     bReverse : it was undone.                                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the tree matches the document.                              */
    /*------------------------------------------------------------------------*/
    void Jsn_DocApplyStepToTree(VoorheesEditStep objStep, bool bReverse)
    {
        BMF_clsBusyModalForm frmProgress;  // progress window, shown only if this is slow
        int iCount;                        // changes in the step
        int i;

        /* Counted as local work and as a document change while the tree      */
        /* follows the step, as every document change is.  (No message loop   */
        /* runs meanwhile -- the progress window only peeks at the queue,     */
        /* BusyModalForm.cs -- and the callers count their actions too.)      */
        Jsn_iLocalWork = checked(Jsn_iLocalWork + 1);
        Jsn_iModelWork = checked(Jsn_iModelWork + 1);
        try
        {   /* The tree's update; the finally below ends the counting on    */
            /* every way out.                                               */
            /* How many changes the step holds decides whether a progress   */
            /* window is worth setting up.                                  */
            iCount = objStep.Ves_lstChanges.Count;
            if (iCount < JSN_PROGRESS_STEP)
            {   /* Fewer than JSN_PROGRESS_STEP changes -- every ordinary edit,   */
                /* and a small Replace All.  No window is set up: the loop        */
                /* below reports only every JSN_PROGRESS_STEP changes, so a       */
                /* step this small would report once, at its first change,        */
                /* before any delay could pass, and the window could never        */
                /* appear.                                                        */
                for (i = 0; i < iCount; i++)
                {   /* One change per iteration, in the order the step needs. */
                    if (bReverse)
                    {   /* Undone: from the last change back.  Each change was    */
                        /* made on the document as the change before it left      */
                        /* it, and its positions (an insert's or a removal's      */
                        /* index) refer to that state, so the tree is unwound     */
                        /* in the reverse order -- forwards, entries would land   */
                        /* at the wrong positions.  true: take the change back.   */
                        Jsn_tree.VTV_ApplyChange(objStep.Ves_lstChanges[iCount - 1 - i], true);
                    }
                    else
                    {   /* Made or redone: first change first, the order the   */
                        /* document made them in.  false: apply the change.    */
                        Jsn_tree.VTV_ApplyChange(objStep.Ves_lstChanges[i], false);
                    }
                }
                return;
            }

            /* Many changes -- a large Replace All, or its undo or redo.  The     */
            /* progress window is set up hidden, titled with the file's own       */
            /* name; it appears only if the work outlasts JSN_SHOW_DELAY_MS,      */
            /* which BMF_ReportUiProgress decides.  The work changes the tree's   */
            /* controls, which only the UI thread may do, so it cannot go to      */
            /* Jsn_ProgRun's worker thread; this is BusyModalForm's UI-thread     */
            /* mode instead.                                                      */
            frmProgress = BMF_clsBusyModalForm.BMF_BeginUiProgress(this, Jsn_fntDialog, "Updating the tree",
                Jsn_DocName(), JSN_CAPTION, JSN_SHOW_DELAY_MS);
            try
            {   /* The changes, reported now and then; the finally below   */
                /* takes the window down whatever happens.                 */
                for (i = 0; i < iCount; i++)
                {   /* One change per iteration, in the order the step needs. */
                    if (bReverse)
                    {   /* Undone: from the last change back, as above. */
                        Jsn_tree.VTV_ApplyChange(objStep.Ves_lstChanges[iCount - 1 - i], true);
                    }
                    else
                    {   /* Made or redone: first change first, as above. */
                        Jsn_tree.VTV_ApplyChange(objStep.Ves_lstChanges[i], false);
                    }

                    if (i % JSN_PROGRESS_STEP == 0)
                    {   /* Every JSN_PROGRESS_STEP-th change, the first        */
                        /* included: BMF_ReportUiProgress shows the window     */
                        /* once the delay has passed and moves its bar.        */
                        /* With the work on the UI thread no message loop      */
                        /* runs meanwhile, and the report is also what keeps   */
                        /* Windows from taking the program for hung.           */
                        frmProgress.BMF_ReportUiProgress(i, iCount);
                    }
                }
            }
            finally
            {   /* Always: the window closed if it appeared and released     */
                /* either way, so an exception cannot leave it behind        */
                /* (BusyModalForm requires its end in a finally for that).   */
                frmProgress.BMF_EndUiProgress();
            }
        }
        finally
        {   /* The update has returned, however it ended: the counts come     */
            /* down, held Save answers and close decisions are posted (they   */
            /* still wait while the caller's own count stands), and the       */
            /* editor coordinator is told.                                    */
            Jsn_iModelWork--;
            Jsn_iLocalWork--;
            Jsn_PostReadyAcknowledgements();
            Jsn_PostReadyCloses();
            Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_DocAdvanceRevision:                                                */
    /*                                                                        */
    /* The document has changed (a step made, undone or redone, or a Replace  */
    /* All about to change it): its revision number is stepped, so a save     */
    /* captured before (JSC_lRevision) no longer matches and its answer does  */
    /* not mark the document saved (Jsn_ApplyAcknowledgement).  Only content  */
    /* changes step it -- a selection or a repaint does not.                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the revision stepped; the document kept modified while the  */
    /*            saved baseline is stale.                                    */
    /*------------------------------------------------------------------------*/
    private void Jsn_DocAdvanceRevision()
    {
        /* The new revision (checked: an overflow throws rather than wraps   */
        /* round to a number a captured save might hold).                    */
        Jsn_lRevision = checked(Jsn_lRevision + 1);
        if (Jsn_bSaveBaselineStale)
        {   /* A save wrote an older revision and the newer history was       */
            /* kept: the history's start is not the file, so the document     */
            /* stays modified whatever the step did -- the window's side      */
            /* of the rule the document's own base mark keeps at the bottom   */
            /* of the history (VD_MarkBaseUnsaved).  Cleared only by a        */
            /* matching save or a new document.                               */
            Jsn_doc.Vd_bModified = true;
        }
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_DocAfterChange:                                                    */
    /*                                                                        */
    /* What every change, undo and redo has in common once the tree has       */
    /* followed it: the record of whether the document holds comments kept    */
    /* current, the format's warning about the change shown if it has one     */
    /* (at most once per document), and the title and status bar.             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objStep : the step just made, undone or redone.                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : Jsn_bHasComments is current and the display updated.        */
    /*------------------------------------------------------------------------*/
    void Jsn_DocAfterChange(VoorheesEditStep objStep)
    {
        bool bHadComments;                               // the document had comments before
        string sWarning;                                 // the format's warning, if any

        /* Whether the document held comments before this change and        */
        /* after it: the old answer kept, the new one read from the         */
        /* document.  JSON's warning turns on the difference, and the       */
        /* status strip's "Contains comments" (JSON only) on the new one.   */
        bHadComments = Jsn_bHasComments;
        Jsn_bHasComments = Jsn_doc.Vd_HasComments();
        if (!Jsn_bEditWarned)
        {   /* Not warned yet for this document: ask the format.  JSON    */
            /* warns at the first comment in a document that had none     */
            /* (it is then JSONC, which many programs refuse; an undo     */
            /* takes the comment away); the Klipper family at the first   */
            /* change inside the auto-saved block, which Klipper and      */
            /* KlipperScreen rewrite themselves; other formats have       */
            /* nothing to say.                                            */
            sWarning = VoorheesFormat.Vf_EditWarning(Jsn_doc, objStep, bHadComments,
                Jsn_bHasComments);
            if (sWarning != null)
            {   /* It does: marked as given, then shown.  Once per            */
                /* document, so a user who has read it is not stopped at      */
                /* every later edit; Jsn_DocSetDocument clears the mark for   */
                /* the next document.                                         */
                Jsn_bEditWarned = true;
                Jsn_DlgMessage(sWarning, MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }

        /* The title and status bar redrawn: the modified mark (kept on       */
        /* through Jsn_DocAdvanceRevision when the saved baseline is stale,   */
        /* even with nothing left to undo) and the comment state.             */
        Jsn_StateUpdateTitle();
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_DocName:                                                           */
    /*                                                                        */
    /* The file's own name, for progress windows -- the name it has where     */
    /* it came from, not the name of an image entry's temporary copy.         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the name.                                                 */
    /*------------------------------------------------------------------------*/
    string Jsn_DocName()
    {
        /* The name the main window gave with the file. */
        return(Jsn_file.OFD_sName);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnTreeDocNameAsked:                                                */
    /*                                                                        */
    /* The tree's call for the name its progress windows show (bound with     */
    /* VTV_BindDocName when the tree is made): the same name this window's    */
    /* own progress windows show, so an image entry's tree work is named by   */
    /* the entry, not by its temporary copy.  A callback of its own, so the   */
    /* tree's asking is told apart from this window's direct calls.           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the file's own name (Jsn_DocName).                        */
    /*------------------------------------------------------------------------*/
    string Jsn_OnTreeDocNameAsked()
    {
        /* The window's own answer, unchanged. */
        return(Jsn_DocName());
    }

    /*------------------------------------------------------------------------*/
    /* PROG: progress shown during long work                                  */
    /*------------------------------------------------------------------------*/

    /*------------------------------------------------------------------------*/
    /* Jsn_ProgRun:                                                           */
    /*                                                                        */
    /* Run a long piece of document work (a Find's search, a Replace All) off */
    /* the UI thread behind the busy window, and return once it has finished  */
    /* -- the work touches the document only, never a control, and the        */
    /* window waits for it, so nothing else changes the document meanwhile.   */
    /* What the work throws is thrown on, wrapped with the step's title.      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sTitle           : the busy window's heading ("Searching",         */
    /*                        "Replacing"), and the failure's prefix.         */
    /*     pfnWork          : the work, a named method of a JSR_ record; it   */
    /*                        must touch no control and call no engine.       */
    /*     bChangesDocument : true when the work changes the document         */
    /*                        (Replace All): a new revision first.            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the work has finished; an InvalidOperationException         */
    /*            carrying its failure when it threw.                         */
    /*------------------------------------------------------------------------*/
    private void Jsn_ProgRun(string sTitle, Action pfnWork, bool bChangesDocument)
    {
        Exception exWork;

        if (bChangesDocument)
        {   /* The work will change the document: the revision stepped       */
            /* before it starts, so a save captured before cannot be taken   */
            /* for this document by its answer (Jsn_DocAdvanceRevision).     */
            Jsn_DocAdvanceRevision();
        }

        /* Counted as local work and as a document change while the work    */
        /* runs: the busy window is modal, and in its message loop a Save   */
        /* answer can arrive -- it is held until the work has finished.     */
        Jsn_iLocalWork = checked(Jsn_iLocalWork + 1);
        Jsn_iModelWork = checked(Jsn_iModelWork + 1);
        try
        {   /* The work; the finally below ends the counting on every way   */
            /* out.  BMF_Run returns only once the work has finished,       */
            /* handing back what it threw (a failure to show the window     */
            /* is thrown, after the work has finished).                     */
            exWork = BMF_clsBusyModalForm.BMF_Run(this, Jsn_fntDialog,
                sTitle, Jsn_DocName(), pfnWork, JSN_CAPTION, JSN_SHOW_DELAY_MS);
            if (exWork != null)
            {   /* The work threw: thrown on to the caller, named by the     */
                /* step ("Replacing: ..."), the original kept as the inner   */
                /* exception.  Nothing here catches it (the Find window's    */
                /* callers do not either: Frf_FindNext, Frf_ReplaceAll).     */
                throw new InvalidOperationException(sTitle + ": " + exWork.Message, exWork);
            }
        }
        finally
        {   /* The work has returned, however it ended: the counts come       */
            /* down, held Save answers and close decisions are posted for a   */
            /* later turn, and the editor coordinator is told.                */
            Jsn_iModelWork--;
            Jsn_iLocalWork--;
            Jsn_PostReadyAcknowledgements();
            Jsn_PostReadyCloses();
            Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_ProgDescribe:                                                      */
    /*                                                                        */
    /* The message for an exception that came back from work run behind       */
    /* the progress dialog.  The worker's own expected failures (an           */
    /* unreadable file, say) are reported by the work itself; this covers     */
    /* the rest, including the case where no exception was returned but       */
    /* the work produced nothing.                                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     ex : what the work threw, or null when it returned no result       */
    /*          without throwing.                                             */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the text to show the user.                                */
    /*------------------------------------------------------------------------*/
    static string Jsn_ProgDescribe(Exception ex)
    {
        if (ex == null)
        {   /* The work ended without a result or an exception. */
            return("The operation did not complete.");
        }

        /* The exception's own explanation. */
        return(ex.Message);
    }

    /*------------------------------------------------------------------------*/
    /* STATE: title bar and status strip                                      */
    /*------------------------------------------------------------------------*/

    /*------------------------------------------------------------------------*/
    /* Jsn_StateUpdateTitle:                                                  */
    /*                                                                        */
    /* Shows the file in the title bar (where it came from, then its format   */
    /* -- "name.cfg in image.img - Klipper config" -- with " *" while         */
    /* modified) and, in the status strip, the same description, the          */
    /* format, whether it contains comments, and its modified state.  The     */
    /* description is the file's own (OFD_Title): an image entry's path and   */
    /* its image, or a Windows file's full path -- never the temporary        */
    /* copy's path, which would mean nothing to the user.                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : Text and the status labels are updated.                     */
    /*------------------------------------------------------------------------*/
    void Jsn_StateUpdateTitle()
    {
        string sFileName;  // description shown in the title bar
        string sModified;  // " *" while modified

        /* The file's own description (OFD_Title), in the status strip   */
        /* and, below, the title.                                        */
        sFileName = Jsn_file.OFD_Title();
        Jsn_tslFilePath.Text = sFileName;

        /* The format, by name. */
        Jsn_tslFormat.Text = VoorheesFormat.Vf_Name(Jsn_doc.Vd_iFormat);

        if (Jsn_bHasComments
            && VoorheesFormat.Vf_CommentsMakeNonStandard(Jsn_doc.Vd_iFormat))
        {   /* Comments in a format whose standard has none (JSON): say so. */
            Jsn_tslComments.Text = "Contains comments";
        }
        else
        {   /* Standard for its format. */
            Jsn_tslComments.Text = "";
        }

        if (Jsn_doc.Vd_bModified)
        {   /* Unsaved changes. */
            sModified = " *";
            Jsn_tslModified.Text = "Modified";
        }
        else
        {   /* Matches the file on disk. */
            sModified = "";
            Jsn_tslModified.Text = "";
        }

        /* The title: where the file came from, " *" while modified,   */
        /* then its format.                                            */
        Text = sFileName + sModified + " - " + VoorheesFormat.Vf_Name(Jsn_doc.Vd_iFormat);
    }

    /*------------------------------------------------------------------------*/
    /* TREE: selection and in-place editing in the tree                       */
    /*------------------------------------------------------------------------*/

    /*------------------------------------------------------------------------*/
    /* Jsn_TreeSelectedPath:                                                  */
    /*                                                                        */
    /* The position path of the selected entry: its index at each level,      */
    /* top first -- the way the document (Vd_ItemAt) and the tree             */
    /* (VTV_NodeAt) both name an entry, and which, unlike the TreeNode        */
    /* itself, still names it after the tree has replaced its node.           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<int> : the path, or null when nothing is selected.            */
    /*------------------------------------------------------------------------*/
    List<int> Jsn_TreeSelectedPath()
    {
        if (Jsn_tree.SelectedNode == null || Jsn_tree.SelectedNode.Tag == null)
        {   /* Nothing selected, or only an unopened container's            */
            /* placeholder (the one node with no Tag), which is no entry.   */
            return(null);
        }

        /* Its indexes, top first: the tree's levels are the document's. */
        return(VTV_clsTreeView.VTV_PathOf(Jsn_tree.SelectedNode));
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnTreeBeforeSelect:                                                */
    /*                                                                        */
    /* The tree is about to move its selection.  While the panel holds typing */
    /* the user moves on from, the move is held: it is cancelled here and its */
    /* finish -- the panel's question, then the selection -- posted to a      */
    /* later turn (JSN_CompleteTreeSelection), so the question is not asked,  */
    /* and the nodes not changed by saving the panel, from inside the tree's  */
    /* own selection event.                                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the tree (unused).                                     */
    /*     evSelect  : the node about to be selected and how; Cancel set to   */
    /*                 hold the move.                                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the move let through, or held and its finish posted.        */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnTreeBeforeSelect(object objSender, TreeViewCancelEventArgs evSelect)
    {
        List<int> lstTarget;
        JTE_clsTreeIntent objIntent;

        if (Jsn_tree.VTV_IsBusy() || !Jsn_bPanelDirty)
        {   /* The tree moving its own selection while it rearranges its     */
            /* nodes, or nothing typed in the panel: let through, there is   */
            /* nothing to ask about.                                         */
            return;
        }

        if (evSelect.Action == TreeViewAction.Unknown)
        {   /* A selection made from code (VTV_Select): its caller has      */
            /* already settled the panel (JSN_CompleteTreeSelection, the    */
            /* right-click menu's Jsn_ShowContextMenu, Jsn_DocFinish), so   */
            /* let through.                                                 */
            return;
        }

        /* The user's move with typing in the panel.  The target's position   */
        /* path taken now -- it still finds the entry if saving the panel     */
        /* replaces its node -- the move cancelled, and its finish posted,    */
        /* counted as the window's work until it has run (Jsn_iPostedTree).   */
        lstTarget = VTV_clsTreeView.VTV_PathOf(evSelect.Node);
        evSelect.Cancel = true;
        objIntent = new JTE_clsTreeIntent(this, lstTarget, null, false);
        Jsn_iPostedTree = checked(Jsn_iPostedTree + 1);
        try
        {   /* Posted, to run after the tree's event has returned. */
            BeginInvoke(new MethodInvoker(objIntent.JTE_Select));
        }
        catch
        {   /* The post failed: no finish will run, so it is no longer   */
            /* counted, and the failure thrown on (the move stays        */
            /* cancelled).                                               */
            Jsn_iPostedTree--;
            throw;
        }
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnTreeAfterSelect:                                                 */
    /*                                                                        */
    /* Loads the newly selected entry into the edit panel, so the panel       */
    /* always shows what the tree has selected.                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the tree.                                              */
    /*     evArgs : the node just selected.                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the panel shows the entry.                                  */
    /*------------------------------------------------------------------------*/
    void Jsn_OnTreeAfterSelect(object objSender, TreeViewEventArgs evArgs)
    {
        if (Jsn_tree.VTV_IsBusy() || evArgs.Node == null || evArgs.Node.Tag == null)
        {   /* The tree moving its own selection while it rearranges its       */
            /* nodes -- the selection after a change is made once the          */
            /* tree has finished (Jsn_DocFinish's VTV_Select) -- or no node,   */
            /* or a placeholder that is no entry: the panel is left as it      */
            /* is.                                                             */
            return;
        }

        /* The entry the user (or code) chose, into the panel. */
        Jsn_EditLoadPanel(evArgs.Node);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnTreeNodeMouseDoubleClick:                                        */
    /*                                                                        */
    /* A left double click on a plain value or a comment asks for it to be    */
    /* edited in place.  (A double click on an object or array just opens or  */
    /* closes it, as usual.)  The handler only records the entry and posts:   */
    /* starting the edit can ask the panel's unsaved-changes question, which  */
    /* is never asked from a handler.                                         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the tree.                                              */
    /*     evArgs : the node clicked and the mouse button.                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : an InlineEdit item posted to the window's queue, or         */
    /*            nothing for another button or no entry.                     */
    /*------------------------------------------------------------------------*/
    void Jsn_OnTreeNodeMouseDoubleClick(object objSender,
        TreeNodeMouseClickEventArgs evArgs)
    {
        if (evArgs.Button == MouseButtons.Left && evArgs.Node != null && evArgs.Node.Tag != null)
        {   /* Left double click on an entry: its path posted, so the    */
            /* queue finds it again even if the tree is rebuilt first.   */
            /* On a container the queue's edit does nothing, and the     */
            /* control's own opening or closing of it stands.            */
            Jsn_objQueue.WQU_Post((int)Jsn_enmItem.InlineEdit, VTV_clsTreeView.VTV_PathOf(evArgs.Node));
        }
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnTreeKeyDown:                                                     */
    /*                                                                        */
    /* F2 asks for the selected value or comment to be edited in place, the   */
    /* key Explorer uses for renaming in place; the handler only records the  */
    /* entry and posts, as the double click does.  Any key also drops a right */
    /* click's record whose menu never opened, so a menu asked for from the   */
    /* keyboard is for the selected entry.                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the tree.                                              */
    /*     evArgs : the key pressed.                                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : for a plain F2, an InlineEdit item posted to the window's   */
    /*            queue (when an entry is selected) and the key handled.      */
    /*------------------------------------------------------------------------*/
    void Jsn_OnTreeKeyDown(object objSender, KeyEventArgs evArgs)
    {
        TreeNode nodSelected;  // the entry F2 is for

        /* A key is a new gesture: a right click's record, if its menu       */
        /* never asked to open, goes now, before the menu key or Shift+F10   */
        /* can open one (the key comes first).                               */
        Jsn_bContextFromMouse = false;

        if (evArgs.KeyCode == Keys.F2 && evArgs.Modifiers == Keys.None)
        {   /* Plain F2: an in-place edit asked for. */

            /* The selected entry's path posted, so the queue finds it      */
            /* again even if the tree is rebuilt first; with nothing        */
            /* selected, or an unopened container's placeholder, there is   */
            /* nothing to edit and nothing is posted.                       */
            nodSelected = Jsn_tree.SelectedNode;
            if (nodSelected != null && nodSelected.Tag != null)
            {   /* An entry is selected: its edit posted. */
                Jsn_objQueue.WQU_Post((int)Jsn_enmItem.InlineEdit, VTV_clsTreeView.VTV_PathOf(nodSelected));
            }

            /* Handled, so the tree's own F2 handling -- a label edit of   */
            /* its own, as found -- does not run beside the queue's.       */
            evArgs.Handled = true;
        }
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_RunInlineEdit:                                                     */
    /*                                                                        */
    /* The window queue's in-place edit, posted by F2 or a left double click: */
    /* the entry found again by the path the gesture recorded -- the tree may */
    /* have been rebuilt since -- and handed to Jsn_TreeBeginInlineEdit,      */
    /* which may ask the panel's unsaved-changes question first.              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objArgs : the entry's path, a List<int>.                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the edit started, or nothing when the entry is gone.        */
    /*------------------------------------------------------------------------*/
    void Jsn_RunInlineEdit(object objArgs)
    {
        TreeNode nodTree;  // the entry, found again

        /* The node at the recorded path: no levels made on the way and no   */
        /* nearest ancestor -- only the entry itself will do.                */
        nodTree = Jsn_tree.VTV_NodeAt((List<int>)objArgs, false, false);
        if (nodTree == null)
        {   /* Not at that path any more: nothing to edit. */
            return;
        }

        /* The edit itself, with its own checks (the selection, the kind   */
        /* of entry, the panel's question).                                */
        Jsn_TreeBeginInlineEdit(nodTree);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_TreeBeginInlineEdit:                                               */
    /*                                                                        */
    /* Starts editing a plain value, or a single comment's words, inside the  */
    /* tree -- the double click and F2, run from the window's queue by        */
    /* Jsn_RunInlineEdit -- with the key left in view.  What the one-line     */
    /* edit box cannot hold is sent to the Value box instead.                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     nodTree : the node to edit.                                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : an edit box opens over the node, the Value box gets         */
    /*            the focus, or nothing happens (a container).                */
    /*------------------------------------------------------------------------*/
    void Jsn_TreeBeginInlineEdit(TreeNode nodTree)
    {
        VoorheesItem objItem;  // the entry
        List<int> lstPath;     // its path
        string sValue;         // full text to edit

        if (nodTree == null || nodTree.Tag == null)
        {   /* No node, or an unopened container's placeholder:   */
            /* nothing to edit (a guard: the gestures post only   */
            /* an entry's path).                                  */
            return;
        }

        if (nodTree != Jsn_tree.SelectedNode)
        {   /* A double click on a node the selection never reached: the        */
            /* panel held typing, so Jsn_OnTreeBeforeSelect held the move and   */
            /* posted its own question about it.  Starting an edit here would   */
            /* edit an entry the panel does not show.                           */
            return;
        }

        /* The entry's path, kept so the node can be found again if   */
        /* settling the panel replaces it.                            */
        lstPath = VTV_clsTreeView.VTV_PathOf(nodTree);
        if (Jsn_bPanelDirty)
        {   /* Typed changes for this entry in the panel: settle them, then find the node again. */
            if (!Jsn_EditConfirmPendingPanel())
            {   /* Cancelled, or the panel's values would not save: no in-   */
                /* place edit, and the panel keeps the typing.               */
                return;
            }

            /* Saving may have replaced the node (VTV_ApplyChange replaces   */
            /* it when a container value is saved over the entry), so it     */
            /* is looked up again by path: no levels made on the way,        */
            /* since they were there a moment ago, and no nearest ancestor   */
            /* -- only the entry itself will do.                             */
            nodTree = Jsn_tree.VTV_NodeAt(lstPath, false, false);
            if (nodTree == null)
            {   /* Not at that path any more: nothing to edit. */
                return;
            }
        }

        /* The entry itself, from the document: the label's text is cut   */
        /* short and dressed for display, so the value is never taken     */
        /* from it.                                                       */
        objItem = Jsn_doc.Vd_ItemAt(lstPath);
        if (objItem == null)
        {   /* The document has nothing at the tree's path -- the tree out   */
            /* of step with it, which should not happen: a guard.            */
            return;
        }

        if (objItem.Vi_iKind == VoorheesItem.VI_COMMENT)
        {   /* A comment entry: its words are what is edited. */
            if (!VoorheesFormat.Vf_IsSingleComment(Jsn_doc.Vd_iFormat, objItem.Vi_sComment))
            {   /* Several comments stored together: only the Raw box   */
                /* edits those, so no in-place edit.                    */
                return;
            }

            /* Its words without the comment markers, as the tree and the   */
            /* Comment box show them.                                       */
            sValue = VoorheesFormat.Vf_CommentText(Jsn_doc.Vd_iFormat, objItem.Vi_sComment);
        }
        else if (objItem.Vi_node.Vn_IsContainer())
        {   /* Object or array: no single value to edit (on a double   */
            /* click the control opens or closes it instead).          */
            return;
        }
        else
        {   /* A plain value: the string itself, the number as written,   */
            /* or true / false / null.                                    */
            sValue = objItem.Vi_node.Vn_DisplayValue();
        }

        if (sValue.IndexOf('\n') >= 0 || sValue.IndexOf('\r') >= 0)
        {   /* Text with a line break: the in-tree edit box is a single     */
            /* line and would lose it, so the Value box, which keeps line   */
            /* breaks, is given the focus with the text selected instead.   */
            Jsn_txtValue.Focus();
            Jsn_txtValue.SelectAll();
            return;
        }

        /* Edit the FULL text, after the key.  The label's value is cut to   */
        /* VTV_LEAFMAX characters, and committing that would write its       */
        /* "..." into the data; VTV_PrepareValueEdit keeps the full text     */
        /* for the box and shortens the label to its key part, so the box    */
        /* opens to the right of a key that stays in view.                   */
        /* Jsn_bInlineEditRequested is what lets Jsn_OnTreeBeforeLabelEdit   */
        /* allow this edit; BeginEdit raises that event before it returns,   */
        /* so the flag is up only for the edit started here.                 */
        Jsn_tree.VTV_PrepareValueEdit(nodTree, sValue);
        Jsn_bInlineEditRequested = true;
        nodTree.BeginEdit();
        Jsn_bInlineEditRequested = false;

        if (!Jsn_bLabelEditing)
        {   /* The edit box did not open: Jsn_bLabelEditing is raised by         */
            /* Jsn_OnTreeBeforeLabelEdit, so the control never raised it (when   */
            /* it would not is not established).  The prepared edit is           */
            /* forgotten and the label, cut to its key part above, is            */
            /* redrawn from the document.                                        */
            Jsn_tree.VTV_EndValueEdit();
            Jsn_tree.VTV_RefreshLabel(nodTree);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnTreeBeforeLabelEdit:                                             */
    /*                                                                        */
    /* Lets through only the in-place edits Jsn_TreeBeginInlineEdit starts.   */
    /* With LabelEdit on, the control also opens an edit box on ANY node      */
    /* from a slow second click (as found); committed, such an edit could     */
    /* replace a whole object or array with a string.                         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the tree.                                              */
    /*     evArgs : the edit about to start; evArgs.CancelEdit stops it.      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the edit opens or is cancelled.                             */
    /*------------------------------------------------------------------------*/
    void Jsn_OnTreeBeforeLabelEdit(object objSender, NodeLabelEditEventArgs evArgs)
    {
        if (!Jsn_bInlineEditRequested)
        {   /* Started by the control (a slow second click), not by this   */
            /* editor: refused, so no box opens.                           */
            evArgs.CancelEdit = true;
            return;
        }

        /* Ours: an edit box is now open.  Jsn_EditUndo and Jsn_EditRedo stand   */
        /* aside while this is set, leaving the box to finish or cancel,         */
        /* and Jsn_TreeBeginInlineEdit reads it to see that the box opened.      */
        Jsn_bLabelEditing = true;
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnTreeAfterLabelEdit:                                              */
    /*                                                                        */
    /* An in-place edit box has closed: its text and its entry's path are     */
    /* taken now, the control's own change of the label refused, and the      */
    /* commit posted to a later turn (JSN_CompleteTreeCommit) -- it changes   */
    /* the document and relabels or replaces nodes, which is not done from    */
    /* inside the tree's own event on the node being edited.                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the tree (unused).                                     */
    /*     evLabel   : the node and the text typed (Label null for a          */
    /*                 cancelled edit); CancelEdit set to keep the label.     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the commit posted, with whether a close waits on it.        */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnTreeAfterLabelEdit(object objSender, NodeLabelEditEventArgs evLabel)
    {
        List<int> lstPath;
        string sText;
        JTE_clsTreeIntent objIntent;
        bool bOwnClose;

        /* The box is closed (Undo and Redo may act again); the entry's   */
        /* path and the text taken from the event; the control's own      */
        /* change of the label refused -- the label is redrawn from the   */
        /* document after the commit (VTV_RefreshLabel), never left as    */
        /* typed; and the waiting close, if the user's own close is       */
        /* waiting for this edit (Jsn_PresentClose), taken and lowered.   */
        Jsn_bLabelEditing = false;
        lstPath = VTV_clsTreeView.VTV_PathOf(evLabel.Node);
        sText = evLabel.Label;
        evLabel.CancelEdit = true;
        bOwnClose = Jsn_bCloseAfterInline;
        Jsn_bCloseAfterInline = false;
        objIntent = new JTE_clsTreeIntent(this, lstPath, sText, bOwnClose);
        /* Counted as the window's work until the posted commit has run   */
        /* (Jsn_iPostedTree), so no close goes ahead in between           */
        /* (Jsn_PresentClose, Jsn_PostReadyCloses).                       */
        Jsn_iPostedTree = checked(Jsn_iPostedTree + 1);
        try
        {   /* Posted, to run after the tree's event has returned. */
            BeginInvoke(new MethodInvoker(objIntent.JTE_Commit));
        }
        catch
        {   /* The post failed: no commit will run, so it is no longer   */
            /* counted, and the failure thrown on (the edit is lost).    */
            Jsn_iPostedTree--;
            throw;
        }
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_TreeCommitInlineEdit:                                              */
    /*                                                                        */
    /* Applies an in-place edit as one undo step: a comment entry gets its    */
    /* new words, a value new text of the SAME type -- changing a value's     */
    /* type is the panel's Type box's job, never a side effect of typing.     */
    /* Whatever the outcome -- applied, refused or cancelled -- the label is  */
    /* redrawn from the document and the panel reloaded; an exception from    */
    /* the document or the tree is not caught, and goes on to the caller.     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstPath : path of the edited entry.                                */
    /*     sText : the typed text, or null if the edit was cancelled.         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the entry changes (one undo step), or the label is          */
    /*            simply redrawn.                                             */
    /*------------------------------------------------------------------------*/
    void Jsn_TreeCommitInlineEdit(List<int> lstPath, string sText)
    {
        VoorheesItem objItem;      // the entry
        VoorheesNode nodNew;       // a value's new value
        VoorheesEditStep objStep;  // the edit's undo step
        string sError;             // why the edit was refused
        string sReason;            // the format's own reason for refusing the text, or ""

        /* The entry as the document holds it now, by the path taken when   */
        /* the box closed; null if nothing is there any more.               */
        objItem = Jsn_doc.Vd_ItemAt(lstPath);
        if (sText != null && objItem != null)
        {   /* Something was typed (null: the edit was cancelled) and the   */
            /* entry is still there.                                        */
            /* One undo step for the edit, its focus the entry, so it is    */
            /* selected again afterwards; no refusal yet.                   */
            objStep = Jsn_doc.Vd_BeginStep(lstPath);
            sError = null;
            if (objItem.Vi_iKind == VoorheesItem.VI_COMMENT)
            {   /* A comment entry: its new words, the comment's own style   */
                /* kept by the document (Vd_ActSetCommentText).              */
                sError = Jsn_doc.Vd_ActSetCommentText(objStep, lstPath, sText);
            }
            else if (!objItem.Vi_node.Vn_IsContainer())
            {   /* A plain value: a new value of the same type, from the     */
                /* text, by the format's own rules (Vf_ConvertText) -- for   */
                /* JSON a string stays a string even when the text looks     */
                /* like a number ("007"), a number or boolean must still     */
                /* read as one, and only a null, having no type to keep,     */
                /* becomes whatever the text looks like.                     */
                nodNew = VoorheesFormat.Vf_ConvertText(Jsn_doc, objItem.Vi_node, sText, out sReason);
                if (nodNew == null && sReason.Length != 0)
                {   /* Refused for a reason the format gives -- text the file   */
                    /* could not hold as typed (spaces at an end, several       */
                    /* lines, a character a REGEDIT4 file's code page lacks),   */
                    /* or, for REG, the rule of the value's own type that the   */
                    /* text breaks (Vrg_MakeValue's reason, VoorheesReg.cs)     */
                    /* -- so that reason is shown, in place of the pointer to   */
                    /* the Type box.                                            */
                    sError = "\"" + sText + "\" cannot be this "
                        + VoorheesFormat.Vf_KindName(Jsn_doc.Vd_iFormat, objItem.Vi_node.Vn_iKind).ToLower()
                        + " value as typed.\r\n\r\n" + sReason;
                }
                else if (nodNew == null)
                {   /* Not valid for its type: refused, naming the type and   */
                    /* pointing to the Type box, the one place a type is      */
                    /* changed.                                               */
                    sError = "\"" + sText + "\" is not a valid "
                        + VoorheesFormat.Vf_KindName(Jsn_doc.Vd_iFormat, objItem.Vi_node.Vn_iKind).ToLower()
                        + " value.\r\n\r\nTo change the value's type, use the "
                        + "Type box in the edit panel.";
                }
                else
                {   /* Valid: set over the old value; the document may still   */
                    /* refuse it, and its reason is the refusal.               */
                    sError = Jsn_doc.Vd_ActSetValue(objStep, lstPath, nodNew);
                }
            }
            /* (A container, which no in-place edit is started on, leaves   */
            /* the step empty: Jsn_DocFinish then finds nothing changed.)   */
            /* Finished like every edit: refused with the reason, or        */
            /* recorded, shown and selected.                                */
            Jsn_DocFinish(objStep, sError);
        }

        /* Then, applied, refused or cancelled, the label redrawn from the   */
        /* document (a node no longer at the path comes back null, which     */
        /* VTV_RefreshLabel ignores), and the panel is reloaded from the     */
        /* selected entry, so it shows the committed value whether or not    */
        /* Jsn_DocFinish's re-selection raised a selection event (for the    */
        /* node already selected, whether one is raised is not               */
        /* established).                                                     */
        Jsn_tree.VTV_RefreshLabel(Jsn_tree.VTV_NodeAt(lstPath, false, false));
        Jsn_EditLoadSelected();
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnTreeMouseDown:                                                   */
    /*                                                                        */
    /* A mouse button went down on the tree.  A right click is recorded --    */
    /* the entry under the mouse by its path (none for empty space or an      */
    /* unopened container's placeholder) and where it was -- for the menu's   */
    /* opening that follows it (Jsn_OnContextOpening), which posts it to the  */
    /* window's queue.  Nothing is asked or selected here: the panel's        */
    /* question and the selection belong to the queue's run                   */
    /* (Jsn_ShowContextMenu).  Any other button selects through the control.  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the tree.                                              */
    /*     evArgs : the mouse button and position.                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : a right click is recorded.                                  */
    /*------------------------------------------------------------------------*/
    void Jsn_OnTreeMouseDown(object objSender, MouseEventArgs evArgs)
    {
        TreeNode nodTree;                                   // node under the mouse

        /* Any click is a new gesture: an earlier right click's record, if   */
        /* its menu never asked to open, goes now.                           */
        Jsn_bContextFromMouse = false;

        if (evArgs.Button != MouseButtons.Right)
        {   /* A left click selects through the control itself; only a   */
            /* right click is recorded.                                  */
            return;
        }

        /* The node under the mouse, or null over empty space; its path is   */
        /* taken now, as the tree may be rebuilt before the queue runs.      */
        nodTree = Jsn_tree.GetNodeAt(evArgs.X, evArgs.Y);
        Jsn_lstContextPath = null;
        if (nodTree != null && nodTree.Tag != null)
        {   /* An entry: its path. */
            Jsn_lstContextPath = VTV_clsTreeView.VTV_PathOf(nodTree);
        }

        /* Where it was, and the mark that the next opening is this one. */
        Jsn_ptContext = new Point(evArgs.X, evArgs.Y);
        Jsn_bContextFromMouse = true;
    }

    /*------------------------------------------------------------------------*/
    /* SEARCH: find and replace                                               */
    /*------------------------------------------------------------------------*/

    /*------------------------------------------------------------------------*/
    /* Jsn_SearchShowFind:                                                    */
    /*                                                                        */
    /* Opens the Find / Replace window, or brings the open one forward in     */
    /* the requested layout.  It is modeless and has no owner, so it stays    */
    /* usable alongside the editor and is not hidden or closed with it (the   */
    /* editor's close closes it explicitly, Jsn_PresentClose).                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bShowReplace : true for Find + Replace, false for Find only.       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the window is shown.                                        */
    /*------------------------------------------------------------------------*/
    void Jsn_SearchShowFind(bool bShowReplace)
    {
        if (Jsn_frmFind != null && !Jsn_frmFind.IsDisposed)
        {   /* Already open (a closed one is disposed, which IsDisposed    */
            /* tells): switched to the layout asked for -- Find alone or   */
            /* with Replace -- and brought forward, so there is never a    */
            /* second one.                                                 */
            Jsn_frmFind.FRF_SetMode(bShowReplace);
            Jsn_frmFind.BringToFront();
            return;
        }

        /* Not open: created in the dialog font, under this window's (the   */
        /* program's) icon, and shown modeless -- Show, not ShowDialog --   */
        /* so editing can go on while it is open.                           */
        Jsn_frmFind = new FRF_clsFindReplaceForm(this, Jsn_fntDialog, bShowReplace);
        Jsn_frmFind.Icon = this.Icon;
        Jsn_frmFind.Show();
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_SearchComparison:                                                  */
    /*                                                                        */
    /* The string comparison for a match-case setting.                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bMatchCase : true for an exact-case match.                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     StringComparison : Ordinal or OrdinalIgnoreCase.                   */
    /*------------------------------------------------------------------------*/
    static StringComparison Jsn_SearchComparison(bool bMatchCase)
    {
        if (bMatchCase)
        {   /* Exact case: character for character, with no culture's    */
            /* rules (why ordinal rather than the culture's comparison   */
            /* is not recorded).                                         */
            return(StringComparison.Ordinal);
        }

        /* Any case: the same ordinal comparison with case ignored. */
        return(StringComparison.OrdinalIgnoreCase);
    }

    /*------------------------------------------------------------------------*/
    /* JSN_SearchFindNext:                                                    */
    /*                                                                        */
    /* Find Next: selects the next entry after the current one whose key,     */
    /* value or comment contains the search text, wrapping round to the top,  */
    /* including entries in parts of the tree never opened.                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sSearchText : text to look for.                                    */
    /*     bMatchCase : true for an exact-case match.                         */
    /*     bSearchKeys : look in object member keys.                          */
    /*     bSearchValues : look in plain values and comments.                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : 1 when a match was selected, 0 when there is none, and       */
    /*           a negative value when the user cancelled at the              */
    /*           unsaved-panel question.                                      */
    /*------------------------------------------------------------------------*/
    public int JSN_SearchFindNext(string sSearchText, bool bMatchCase,
        bool bSearchKeys, bool bSearchValues)
    {
        JSR_clsSearchWork objWork;
        VoorheesDocument docSearch;      // the document, captured for the worker
        List<int> lstStart;              // where the search starts
        List<int> lstFound;              // the match
        StringComparison enmComparison;  // exact or case-insensitive comparison

        /* Counted as local work and as a document change for the whole     */
        /* Find, so a Save answer arriving in the busy window's message     */
        /* loop is held.  The Find changes nothing, and an applied answer   */
        /* would change only the history and the saved marks, not what is   */
        /* searched; why it counts as a document change too is not          */
        /* recorded (every action that runs the busy window over the        */
        /* document does).                                                  */
        Jsn_iLocalWork = checked(Jsn_iLocalWork + 1);
        Jsn_iModelWork = checked(Jsn_iModelWork + 1);
        try
        {   /* The Find itself; the finally below ends the counting on   */
            /* every way out, the early returns included.                */
            if (string.IsNullOrEmpty(sSearchText))
            {   /* Nothing to look for: answered as no match. */
                return(0);
            }

            if (!Jsn_EditConfirmPendingPanel())
            {   /* Moving to a match reloads the panel, and the user chose   */
                /* Cancel (or its values would not save): no search, and     */
                /* the negative answer tells this apart from "no match".     */
                return(-1);
            }

            /* What the worker needs, taken here first: the document itself   */
            /* (the work must not touch a control, and the tree is one),      */
            /* the selected entry the search starts after (null when none:    */
            /* from the first entry), the comparison, and no match yet.       */
            docSearch = Jsn_doc;
            lstStart = Jsn_TreeSelectedPath();
            enmComparison = Jsn_SearchComparison(bMatchCase);
            lstFound = null;
            /* The DOCUMENT is searched, not the tree, so entries inside     */
            /* containers never opened are found too; on a worker behind     */
            /* the progress dialog, which appears only if a large document   */
            /* takes longer than JSN_SHOW_DELAY_MS.                          */
            objWork = new JSR_clsSearchWork(docSearch, lstStart, null, sSearchText, "",
                bMatchCase, bSearchKeys, bSearchValues, enmComparison);
            Jsn_ProgRun("Searching", objWork.JSR_Find, false);
            lstFound = objWork.JSR_lstFound;

            if (lstFound == null)
            {   /* Searched everything, round to the start again: no match. */
                return(0);
            }

            /* Match: selected, with the levels down to it made and opened    */
            /* and the entry scrolled into view (VTV_Select); the selection   */
            /* loads it into the panel, which was settled above.              */
            Jsn_tree.VTV_Select(lstFound);
            /* A match is selected. */
            return(1);
        }
        finally
        {   /* The Find has returned, however it ended: the counts come       */
            /* down, held Save answers and close decisions are posted for a   */
            /* later turn, and the editor coordinator is told.                */
            Jsn_iModelWork--;
            Jsn_iLocalWork--;
            Jsn_PostReadyAcknowledgements();
            Jsn_PostReadyCloses();
            Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
        }
    }

    /*------------------------------------------------------------------------*/
    /* JSN_SearchReplaceCurrent:                                              */
    /*                                                                        */
    /* The Replace button: replaces the search text in the selected entry     */
    /* alone -- its key, value and comments, as asked -- as one undo step,    */
    /* telling the user if part of the match could not be replaced.           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sSearchText : text to replace.                                     */
    /*     sReplaceText : text to put in its place.                           */
    /*     bMatchCase : true for an exact-case match.                         */
    /*     bSearchKeys : replace in the key.                                  */
    /*     bSearchValues : replace in the value and comments.                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : 1 when anything changed (one undo step), 0 when nothing was  */
    /*           replaced, and a negative value when the user cancelled at    */
    /*           the unsaved-panel question -- as JSN_SearchFindNext answers, */
    /*           so the caller can stop the whole command on a cancel.        */
    /*------------------------------------------------------------------------*/
    public int JSN_SearchReplaceCurrent(string sSearchText, string sReplaceText,
        bool bMatchCase, bool bSearchKeys, bool bSearchValues)
    {
        List<int> lstPath;         // the selected entry
        VoorheesEditStep objStep;  // the replace's undo step
        int iCount;                // replacements made
        int iSkipped;              // matches refused

        /* Counted as local work and as a document change for the whole      */
        /* Replace, as in Jsn_ContextAddChild: a Save answer arriving in a   */
        /* message loop meanwhile (the panel's question, the refusal note)   */
        /* is held, not applied under this undo step.                        */
        Jsn_iLocalWork = checked(Jsn_iLocalWork + 1);
        Jsn_iModelWork = checked(Jsn_iModelWork + 1);
        try
        {   /* The Replace itself; the finally below ends the counting on   */
            /* every way out, the early returns included.                   */
            if (string.IsNullOrEmpty(sSearchText))
            {   /* Nothing to look for: nothing replaced. */
                return(0);
            }

            if (!Jsn_EditConfirmPendingPanel())
            {   /* The replace reloads the panel, and the user chose Cancel    */
                /* (or its values would not save): nothing replaced, and the   */
                /* negative answer tells this apart from "nothing matched",    */
                /* so the Replace command stops here instead of asking the     */
                /* same question again from its Find Next.                     */
                return(-1);
            }

            /* The entry to replace in: the one selected. */
            lstPath = Jsn_TreeSelectedPath();
            if (lstPath == null)
            {   /* Nothing selected (or only a placeholder): nothing to   */
                /* replace in.                                            */
                return(0);
            }

            /* One undo step focused on the entry, and the replacement made     */
            /* in that entry alone (Vd_ReplaceAt: at most one change, every     */
            /* match it must refuse skipped and counted).  No refusal is        */
            /* passed to Jsn_DocFinish -- Replace refuses match by match, not   */
            /* outright -- and the panel is reloaded so it shows the new text   */
            /* whether or not the re-selection raised a selection event.        */
            iSkipped = 0;
            objStep = Jsn_doc.Vd_BeginStep(lstPath);
            iCount = Jsn_doc.Vd_ReplaceAt(objStep, lstPath, sSearchText, sReplaceText, bMatchCase,
                Jsn_SearchComparison(bMatchCase), bSearchKeys, bSearchValues, ref iSkipped);
            Jsn_DocFinish(objStep, null);
            Jsn_EditLoadSelected();

            if (iSkipped > 0)
            {   /* Something matched but was refused.  The document counts      */
                /* refusals without saying which rule refused each, and the     */
                /* rules are the format's (Vd_ReplaceAt: Vf_ConvertText for a   */
                /* value -- a REG type's own rule, text the file cannot hold    */
                /* as typed; for a key Vd_RenameProblem, which asks             */
                /* Vf_KeyProblem and refuses a key already in use; a            */
                /* comment's room), so the message says so in general terms     */
                /* and gives the common cases only as examples.                 */
                Jsn_DlgMessage("Part of this match wasn't replaced: the "
                    + "file's format would not accept the result -- for "
                    + "example a value of the wrong kind, text the file "
                    + "can't hold as typed, a key it refuses or one "
                    + "already in use, or a comment that can't hold the "
                    + "text.",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            if (iCount > 0)
            {   /* Something was replaced, and so an undo step made. */
                return(1);
            }

            /* The entry did not match, or every match in it was refused:   */
            /* nothing replaced.                                            */
            return(0);
        }
        finally
        {   /* The Replace has returned, however it ended: the counts come    */
            /* down, held Save answers and close decisions are posted for a   */
            /* later turn, and the editor coordinator is told.                */
            Jsn_iModelWork--;
            Jsn_iLocalWork--;
            Jsn_PostReadyAcknowledgements();
            Jsn_PostReadyCloses();
            Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
        }
    }

    /*------------------------------------------------------------------------*/
    /* JSN_SearchReplaceAll:                                                  */
    /*                                                                        */
    /* Replace All: replaces throughout the document, opened in the tree or   */
    /* not, as ONE undo step; matches that cannot be replaced are skipped     */
    /* and counted for the Find window to report.                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sSearchText : text to replace.                                     */
    /*     sReplaceText : text to put in its place.                           */
    /*     bMatchCase : true for an exact-case match.                         */
    /*     bSearchKeys : replace in object member keys.                       */
    /*     bSearchValues : replace in plain values and comments.              */
    /*     iSkipped : set to the number of matches refused.                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : replacements made; a negative value when the user            */
    /*           cancelled at the unsaved-panel question.                     */
    /*------------------------------------------------------------------------*/
    public int JSN_SearchReplaceAll(string sSearchText, string sReplaceText,
        bool bMatchCase, bool bSearchKeys, bool bSearchValues, out int iSkipped)
    {
        JSR_clsSearchWork objWork;
        VoorheesDocument docReplace;  // the document, captured for the worker
        VoorheesEditStep objStep;     // Replace All's undo step
        int iCount;                   // replacements made
        int iSkippedHere;             // matches refused, from the worker

        /* Counted as local work and as a document change for the whole     */
        /* Replace All, as in Jsn_ContextAddChild: a Save answer arriving   */
        /* in the busy window's message loop is held, not applied while     */
        /* the worker changes the document.                                 */
        Jsn_iLocalWork = checked(Jsn_iLocalWork + 1);
        Jsn_iModelWork = checked(Jsn_iModelWork + 1);
        try
        {   /* The Replace All itself; the finally below ends the counting   */
            /* on every way out.                                             */
            /* The out count set first, so every return below hands back     */
            /* a value (the compiler requires it on every path).             */
            iSkipped = 0;
            if (string.IsNullOrEmpty(sSearchText))
            {   /* Nothing to look for: nothing replaced. */
                return(0);
            }

            if (!Jsn_EditConfirmPendingPanel())
            {   /* The replace reloads the panel, and the user chose Cancel    */
                /* (or its values would not save): nothing replaced, and the   */
                /* negative answer tells this apart from "none replaced".      */
                return(-1);
            }

            /* What the worker needs, set up here first: the document itself   */
            /* (the work must not touch a control), and the ONE undo step      */
            /* every replacement goes into, begun here with the selection as   */
            /* its focus so the user is left where they were afterwards        */
            /* (nothing selected: the first entry changed); nothing counted    */
            /* yet.                                                            */
            docReplace = Jsn_doc;
            objStep = Jsn_doc.Vd_BeginStep(Jsn_TreeSelectedPath());
            iCount = 0;
            iSkippedHere = 0;
            /* The whole document walked on a worker behind the progress        */
            /* dialog.  No catch here: Jsn_ProgRun throws on to the caller if   */
            /* the work throws, and the step is then neither ended nor          */
            /* abandoned -- the replacements made before the failure stay       */
            /* in the document with no undo step (JSR_ReplaceAll).              */
            objWork = new JSR_clsSearchWork(docReplace, null, objStep, sSearchText,
                sReplaceText, bMatchCase, bSearchKeys, bSearchValues, Jsn_SearchComparison(bMatchCase));
            Jsn_ProgRun("Replacing", objWork.JSR_ReplaceAll, true);
            iCount = objWork.JSR_iReplaced;
            iSkippedHere = objWork.JSR_iSkipped;

            /* The worker has finished: its refused count handed out, and    */
            /* the one undo step ended and shown (Jsn_DocFinish: recorded,   */
            /* the tree brought up to date change by change, the selection   */
            /* kept).  The panel is reloaded too, so it shows the new text   */
            /* whether or not the re-selection raised a selection event.     */
            iSkipped = iSkippedHere;
            Jsn_DocFinish(objStep, null);
            Jsn_EditLoadSelected();

            /* The number of replacements (0: nothing matched, or every   */
            /* match was refused).                                        */
            return(iCount);
        }
        finally
        {   /* The Replace All has returned, however it ended -- a throw   */
            /* from the worker included: the counts come down, held Save   */
            /* answers and close decisions are posted for a later turn,    */
            /* and the editor coordinator is told.                         */
            Jsn_iModelWork--;
            Jsn_iLocalWork--;
            Jsn_PostReadyAcknowledgements();
            Jsn_PostReadyCloses();
            Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
        }
    }

    /*------------------------------------------------------------------------*/
    /* DLG: message boxes and a small text input dialog                       */
    /*------------------------------------------------------------------------*/

    /*------------------------------------------------------------------------*/
    /* Jsn_DlgAskName:                                                        */
    /*                                                                        */
    /* Asks a format's question for a new entry's name (Vf_NameQuestion),     */
    /* in whichever dialog the question needs, and turns the answer into      */
    /* the name the way the format says.                                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objQuestion : the question.                                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the name, or null when cancelled.                         */
    /*------------------------------------------------------------------------*/
    string Jsn_DlgAskName(VoorheesNameQuestion objQuestion)
    {
        string sText;                                    // the text given
        int iChoice;                                     // the choice made, or -1

        /* No choice yet: -1 is what a text-only answer passes on, which   */
        /* Vf_NameFromAnswer takes as "the text as typed".                 */
        iChoice = -1;
        if (objQuestion.Vnq_arrChoices == null)
        {   /* Text alone: the one-line input box, starting from the   */
            /* question's suggested text.                              */
            sText = Jsn_DlgInputBox(objQuestion.Vnq_sTitle, objQuestion.Vnq_sPrompt,
                objQuestion.Vnq_sText);
        }
        else if (!Jsn_DlgChoiceInputBox(objQuestion, out iChoice, out sText))
        {   /* A choice and text (a registry key's hive and path), and     */
            /* the dialog was cancelled.  (On OK the choice and the text   */
            /* have come back through the out arguments.)                  */
            sText = null;
        }

        if (sText == null)
        {   /* Cancelled, in either dialog: no name. */
            return(null);
        }

        /* The name the answer gives: the text as typed, except that a   */
        /* registry key joins the hive chosen to the path typed.         */
        return(VoorheesFormat.Vf_NameFromAnswer(Jsn_doc.Vd_iFormat, objQuestion, iChoice, sText));
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_DlgChoiceInputBox:                                                 */
    /*                                                                        */
    /* Ask a format's choice-plus-text question for a name (a registry key's  */
    /* hive and path, Jsn_DlgAskName) in a small modal dialog, or take the    */
    /* answer from the partial hook (Jsn_DlgHookChoice; none in the           */
    /* program).  An answer is refused when the worker's close question       */
    /* shown with it has been replaced meanwhile (an Exit's), so a name typed */
    /* for a question that is gone is never used.                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objQuestion : the question: its choices, default and text.         */
    /*     iChoice     : receives the choice made (the default when not       */
    /*                   accepted).                                           */
    /*     sText       : receives the text typed; null when not accepted.     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when OK was pressed and the question still stands;     */
    /*            false for Cancel, the close box, or a replaced question.    */
    /*------------------------------------------------------------------------*/
    private bool Jsn_DlgChoiceInputBox(VoorheesNameQuestion objQuestion, out int iChoice, out string sText)
    {
        Form frmPopup;
        ComboBox cmbChoice;
        TextBox txtText;
        Button btnOk;
        Button btnCancel;
        JDP_clsPromptData objPrompt;
        PRS_clsPresentation objPresentation;
        bool bAnswered;
        int iAnswer;
        string sAnswer;

        /* The worker's close question for this editor, if one is showing,   */
        /* taken before anything can run a message loop; the out answers     */
        /* start as "not accepted" (the default choice, no text); then the   */
        /* partial hook, from no answer (none in the program, so the call    */
        /* compiles to nothing and the dialog below is shown).               */
        objPresentation = QWN_clsQuestionWindows.QWN_CurrentEditorQuestion(Jsn_file.OFD_lEditorId);
        iChoice = objQuestion.Vnq_iChoice;
        sText = null;
        bAnswered = false;
        iAnswer = objQuestion.Vnq_iChoice;
        sAnswer = null;
        Jsn_DlgHookChoice(objQuestion, ref bAnswered, ref iAnswer, ref sAnswer);
        if (bAnswered)
        {   /* A hook answered: its answer, with no dialog shown. */
            if (objPresentation != null &&
                !objPresentation.PRS_objQuestion.QST_IsCurrent(objPresentation))
            {   /* The worker's question was replaced meanwhile: refused,   */
                /* as for the dialog below.                                 */
                return(false);
            }

            /* The hook's choice and text; accepted when it gave text. */
            iChoice = iAnswer;
            sText = sAnswer;
            return(sAnswer != null);
        }

        /* The dialog, built (JSN_DlgBuildChoiceBox) with its prompt   */
        /* record in its Tag, whose named methods are its handlers.    */
        frmPopup = JSN_DlgBuildChoiceBox(Jsn_fntDialog, objQuestion,
            out cmbChoice, out txtText, out btnOk, out btnCancel);
        objPrompt = (JDP_clsPromptData)frmPopup.Tag;
        using (frmPopup)
        {   /* The dialog lives for the one question and is disposed when   */
            /* the block ends, answered or not.                             */
            /* OK records the answer, Cancel only closes; Enter presses     */
            /* OK and Escape Cancel; the text box gets the focus once it    */
            /* is shown; the program's icon.                                */
            btnOk.Click += objPrompt.JDP_OnAccept;
            btnCancel.Click += objPrompt.JDP_OnCancel;
            frmPopup.AcceptButton = btnOk;
            frmPopup.CancelButton = btnCancel;
            frmPopup.Shown += objPrompt.JDP_OnShown;
            frmPopup.Icon = Icon;
            if (objPresentation != null &&
                !objPresentation.PRS_objQuestion.QST_IsCurrent(objPresentation))
            {   /* The worker's question has been replaced already: no   */
                /* dialog for a question that is gone.                   */
                return(false);
            }

            /* Listed with the worker's question being answered, if any   */
            /* (QWN_Attach), so an Exit that replaces it closes this      */
            /* dialog too; then shown, modal over the editor.             */
            QWN_clsQuestionWindows.QWN_Attach(frmPopup);
            frmPopup.ShowDialog(this);
            if (objPresentation != null &&
                !objPresentation.PRS_objQuestion.QST_IsCurrent(objPresentation))
            {   /* The worker's question was replaced while the dialog was   */
                /* up: whatever was typed is for a question that is gone.    */
                return(false);
            }

            if (objPrompt.JDP_bAccepted)
            {   /* OK: the choice and the text read from the controls,   */
                /* before the dialog is disposed.                        */
                iChoice = cmbChoice.SelectedIndex;
                sText = txtText.Text;
                return(true);
            }
        }

        /* Cancel, the close box, or Escape: nothing accepted. */
        return(false);
    }

    /*------------------------------------------------------------------------*/
    /* JSN_DlgBuildChoiceBox:                                                 */
    /*                                                                        */
    /* Builds the choice dialog for Jsn_DlgChoiceInputBox, not shown and with */
    /* OK and Cancel not yet wired, and hands back the controls the caller    */
    /* needs.  Public and apart from the dialog's use, so it could be built   */
    /* without being shown; nothing outside this file calls it.               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     fntDialog : the program's dialog font (the caller's; the dialog    */
    /*                 does not free it).                                     */
    /*     objQuestion : the question.                                        */
    /*     cmbChoice : receives the drop-down list.                           */
    /*     txtText : receives the text box.                                   */
    /*     btnOk     : receives OK.                                           */
    /*     btnCancel : receives Cancel.                                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Form : the dialog (the caller disposes of it).                     */
    /*------------------------------------------------------------------------*/
    public static Form JSN_DlgBuildChoiceBox(Font fntDialog, VoorheesNameQuestion objQuestion,
        out ComboBox cmbChoice, out TextBox txtText, out Button btnOk, out Button btnCancel)
    {
        JDP_clsPromptData objPrompt;
        Form frmPopup;          // the dialog
        Label lblChoice;        // the choice's label
        Label lblPrompt;        // the text's label
        ComboBox cmbLocal;      // the choice list, kept by the prompt record too, handed out as cmbChoice
        TextBox txtLocal;       // the text box, handed out as txtText
        Button btnOkLocal;      // OK, handed out; the caller hooks its Click (JDP_OnAccept)
        Button btnCancelLocal;  // Cancel, handed out; the caller hooks its Click (JDP_OnCancel)

        /* A small fixed dialog, centred on the editor, no taskbar button. */
        frmPopup = new Form();
        frmPopup.Text = objQuestion.Vnq_sTitle;
        frmPopup.StartPosition = FormStartPosition.CenterParent;
        frmPopup.FormBorderStyle = FormBorderStyle.FixedDialog;
        frmPopup.MinimizeBox = false;
        frmPopup.MaximizeBox = false;
        frmPopup.ShowInTaskbar = false;

        /* The caller's dialog font, before any control is made (the list,   */
        /* the box and the buttons are measured from it) -- a bare Form      */
        /* would otherwise start in WinForms' smaller inherited default.     */
        frmPopup.Font = fntDialog;

        /* The choice: its label and the list, the default chosen. */
        lblChoice = new Label();
        lblChoice.Text = objQuestion.Vnq_sChoiceLabel;
        lblChoice.AutoSize = true;
        frmPopup.Controls.Add(lblChoice);

        /* The list: a drop-down LIST, so only one of the choices can be   */
        /* given -- nothing else can be typed there -- with the            */
        /* question's default chosen.                                      */
        cmbLocal = new ComboBox();
        cmbLocal.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbLocal.Items.AddRange(objQuestion.Vnq_arrChoices);
        if (objQuestion.Vnq_iChoice >= 0 && objQuestion.Vnq_iChoice < cmbLocal.Items.Count)
        {   /* The question's default, when it names a choice there is. */
            cmbLocal.SelectedIndex = objQuestion.Vnq_iChoice;
        }
        else
        {   /* No usable default: the first choice, so one is always   */
            /* chosen.                                                 */
            cmbLocal.SelectedIndex = 0;
        }

        /* The list onto the dialog, under its label. */
        frmPopup.Controls.Add(cmbLocal);

        /* The text: its label and the box with the starting text. */
        lblPrompt = new Label();
        lblPrompt.Text = objQuestion.Vnq_sPrompt;
        lblPrompt.AutoSize = true;
        frmPopup.Controls.Add(lblPrompt);

        /* The text box, holding the question's suggested text. */
        txtLocal = new TextBox();
        txtLocal.Text = objQuestion.Vnq_sText;
        frmPopup.Controls.Add(txtLocal);

        /* OK and Cancel, bottom right. */
        btnOkLocal = new Button();
        btnOkLocal.Text = "OK";
        frmPopup.Controls.Add(btnOkLocal);

        /* Cancel, beside OK. */
        btnCancelLocal = new Button();
        btnCancelLocal.Text = "Cancel";
        frmPopup.Controls.Add(btnCancelLocal);

        /* Sized to the measured content and placed now (true); a later   */
        /* resize only places the controls again in whatever size the     */
        /* dialog then has (false).                                       */
        JSN_DlgLayoutChoiceBox(frmPopup, lblChoice, cmbLocal, lblPrompt, txtLocal, btnOkLocal,
            btnCancelLocal, true);
        /* The prompt record (JDP_clsPromptData), holding the controls for   */
        /* its named handlers, in the dialog's Tag for the caller to take;   */
        /* its Resize handler places the controls again.                     */
        objPrompt = new JDP_clsPromptData(frmPopup, lblChoice, cmbLocal, lblPrompt,
            txtLocal, btnOkLocal, btnCancelLocal);
        frmPopup.Tag = objPrompt;
        frmPopup.Resize += objPrompt.JDP_OnResize;

        /* The controls handed out: the caller wires OK and Cancel and   */
        /* reads the answer from these same controls, which the prompt   */
        /* record holds too.                                             */
        cmbChoice = cmbLocal;
        txtText = txtLocal;
        btnOk = btnOkLocal;
        btnCancel = btnCancelLocal;

        /* Built, not shown. */
        return(frmPopup);
    }

    /*------------------------------------------------------------------------*/
    /* JSN_DlgLayoutChoiceBox:                                                */
    /*                                                                        */
    /* Places the choice dialog's controls from measured sizes, so nothing    */
    /* is cut off at any text size; asked to, it first sizes the dialog to    */
    /* its content.                                                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     frmPopup : the dialog.                                             */
    /*     lblChoice  : the choice's label.                                   */
    /*     cmbChoice  : the list.                                             */
    /*     lblPrompt  : the text's label.                                     */
    /*     txtText : the text box.                                            */
    /*     btnOk      : OK button.                                            */
    /*     btnCancel  : Cancel button.                                        */
    /*     bSizeIt    : size the dialog to the content first.                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the controls are placed (and the dialog sized).             */
    /*------------------------------------------------------------------------*/
    internal static void JSN_DlgLayoutChoiceBox(Form frmPopup, Label lblChoice, ComboBox cmbChoice,
        Label lblPrompt, TextBox txtText, Button btnOk, Button btnCancel, bool bSizeIt)
    {
        Size szButton;                                   // size of each button
        int iListWidth;                                  // the list's width
        int iWidth;                                      // content width
        int y;                                           // the next row's top
        int i;

        /* One size for both buttons, from their captions (JSN_DlgButtonSize). */
        szButton = JSN_DlgButtonSize(new Button[] { btnOk, btnCancel });

        /* The list as wide as its longest entry, measured in its own      */
        /* font, plus room for the drop-down button -- taken as a scroll   */
        /* bar's width -- and a JSN_DLG_GAP margin each side.              */
        iListWidth = 0;
        for (i = 0; i < cmbChoice.Items.Count; i++)
        {   /* One entry per iteration; the widest so far kept. */
            iListWidth = Math.Max(iListWidth, TextRenderer.MeasureText(
                cmbChoice.Items[i].ToString(), cmbChoice.Font).Width);
        }
        iListWidth += SystemInformation.VerticalScrollBarWidth + JSN_DLG_GAP * 2;

        if (bSizeIt)
        {   /* The dialog sized to its content.  Its width is the widest    */
            /* of: the list, the two labels, the starting text with two     */
            /* characters' height to spare for typing, JSN_DLG_MIN_CHARS    */
            /* characters (so a short default does not make a cramped       */
            /* box), and the two buttons side by side.  Its height is the   */
            /* rows stacked: JSN_DLG_GAP under each label, JSN_DLG_PAD      */
            /* between the two rows, above the buttons and round the        */
            /* whole.                                                       */
            iWidth = Math.Max(iListWidth, lblChoice.PreferredSize.Width);
            iWidth = Math.Max(iWidth, lblPrompt.PreferredSize.Width);
            iWidth = Math.Max(iWidth, TextRenderer.MeasureText(txtText.Text,
                txtText.Font).Width + txtText.Font.Height * 2);
            iWidth = Math.Max(iWidth, TextRenderer.MeasureText(
                new string('x', JSN_DLG_MIN_CHARS), txtText.Font).Width);
            iWidth = Math.Max(iWidth, szButton.Width * 2 + JSN_DLG_GAP);
            frmPopup.ClientSize = new Size(iWidth + JSN_DLG_PAD * 2,
                JSN_DLG_PAD + lblChoice.PreferredSize.Height + JSN_DLG_GAP + cmbChoice.Height
                + JSN_DLG_PAD + lblPrompt.PreferredSize.Height + JSN_DLG_GAP + txtText.Height
                + JSN_DLG_PAD + szButton.Height + JSN_DLG_PAD);
        }

        /* The choice, then the text, top down, in the same steps as the   */
        /* height above: the list at its measured width under its label,   */
        /* the text box across the whole width under the prompt.           */
        y = JSN_DLG_PAD;
        lblChoice.Location = new Point(JSN_DLG_PAD, y);
        y += lblChoice.PreferredSize.Height + JSN_DLG_GAP;
        cmbChoice.SetBounds(JSN_DLG_PAD, y, iListWidth, cmbChoice.Height);
        y += cmbChoice.Height + JSN_DLG_PAD;
        lblPrompt.Location = new Point(JSN_DLG_PAD, y);
        y += lblPrompt.PreferredSize.Height + JSN_DLG_GAP;
        txtText.SetBounds(JSN_DLG_PAD, y, frmPopup.ClientSize.Width - JSN_DLG_PAD * 2, txtText.Height);

        /* Buttons along the bottom, right-aligned from the dialog's      */
        /* actual size (not the content's): Cancel outermost, OK to its   */
        /* left, JSN_DLG_GAP apart.                                       */
        y = frmPopup.ClientSize.Height - JSN_DLG_PAD - szButton.Height;
        btnCancel.SetBounds(frmPopup.ClientSize.Width - JSN_DLG_PAD - szButton.Width, y,
            szButton.Width, szButton.Height);
        btnOk.SetBounds(btnCancel.Left - JSN_DLG_GAP - szButton.Width, y,
            szButton.Width, szButton.Height);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_DlgMessage:                                                        */
    /*                                                                        */
    /* Every message and question this editor shows: the program's own        */
    /* message box (MSG_clsMessageForm, MessageForm.cs) in the dialog font,   */
    /* under the editor's caption, the first button the default -- or the     */
    /* partial hook's answer (Jsn_DlgHookMessage; none in the program).       */
    /* When the worker's close question for this editor is showing and is     */
    /* replaced meanwhile (an Exit's), the answer is Cancel, so a Save or     */
    /* Discard meant for a question that is gone is never acted on.           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText      : the message's whole text.                             */
    /*     enmButtons : the buttons it offers.                                */
    /*     enmIcon    : its icon (and so its kind: warning, error ...).       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     DialogResult : the button pressed (or the hook's answer); Cancel   */
    /*                    when the worker's question was replaced.            */
    /*------------------------------------------------------------------------*/
    private DialogResult Jsn_DlgMessage(string sText, MessageBoxButtons enmButtons, MessageBoxIcon enmIcon)
    {
        PRS_clsPresentation objPresentation;
        bool bAnswered;
        DialogResult enmAnswer;

        /* The worker's close question for this editor, if one is showing,   */
        /* taken before the box runs its message loop; then the partial      */
        /* hook, from no answer (none in the program, so the call compiles   */
        /* to nothing and the box below is shown).                           */
        objPresentation = QWN_clsQuestionWindows.QWN_CurrentEditorQuestion(Jsn_file.OFD_lEditorId);
        bAnswered = false;
        enmAnswer = DialogResult.None;
        Jsn_DlgHookMessage(sText, enmButtons, ref bAnswered, ref enmAnswer);
        if (!bAnswered)
        {   /* No hook answer: the box itself. */
            if (objPresentation != null &&
                !objPresentation.PRS_objQuestion.QST_IsCurrent(objPresentation))
            {   /* The worker's question has been replaced already: no box   */
                /* for it, and Cancel.                                       */
                return(DialogResult.Cancel);
            }

            /* Shown modal over the editor (MessageForm.cs lists its box   */
            /* with the worker's question itself, so an Exit closes it).   */
            enmAnswer = MSG_clsMessageForm.MSG_Show(this, Jsn_fntDialog, sText,
                JSN_CAPTION, enmButtons, enmIcon, MessageBoxDefaultButton.Button1);
        }

        if (objPresentation != null &&
            !objPresentation.PRS_objQuestion.QST_IsCurrent(objPresentation))
        {   /* The worker's question was replaced while the box was up (or     */
            /* before the hook answered): the answer is for a question that    */
            /* is gone, so Cancel, which every caller takes as "do nothing".   */
            return(DialogResult.Cancel);
        }

        /* The answer, for the caller's decision. */
        return(enmAnswer);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_DlgInputBox:                                                       */
    /*                                                                        */
    /* Asks for one line of text in a small modal dialog with OK and          */
    /* Cancel, in the dialog font and sized to what it holds.                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sTitle : dialog caption.                                           */
    /*     sPrompt : text explaining the requested input.                     */
    /*     sDefaultValue : starting editable text.                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the text entered, or null if cancelled -- or when the     */
    /*              worker's close question it was asked under has been       */
    /*              replaced meanwhile (an Exit), its text then for a         */
    /*              question that is gone.                                    */
    /*------------------------------------------------------------------------*/
    string Jsn_DlgInputBox(string sTitle, string sPrompt, string sDefaultValue)
    {
        JDP_clsPromptData objPrompt;
        PRS_clsPresentation objPresentation;
        Form frmPopup;     // the dialog
        Label lblPrompt;   // the prompt
        TextBox txtText;   // the input box
        Button btnOk;      // OK button
        Button btnCancel;  // Cancel button
        bool bAnswered;    // optional hook supplied a text decision
        string sAnswer;    // its answer

        /* The worker's close question for this editor, if one is showing,   */
        /* taken before anything can run a message loop; then the partial    */
        /* hook, from no answer (none in the program, so the call compiles   */
        /* to nothing and the dialog below is shown).                        */
        objPresentation = QWN_clsQuestionWindows.QWN_CurrentEditorQuestion(Jsn_file.OFD_lEditorId);
        bAnswered = false;
        sAnswer = null;
        Jsn_DlgHookInput(sTitle, ref bAnswered, ref sAnswer);
        if (bAnswered)
        {   /* A hook answered: its text, with no dialog shown. */
            if (objPresentation != null &&
                !objPresentation.PRS_objQuestion.QST_IsCurrent(objPresentation))
            {   /* The worker's question was replaced meanwhile: no text,   */
                /* as for the dialog below.                                 */
                return(null);
            }

            /* The hook's text (null for its cancel). */
            return(sAnswer);
        }

        /* The dialog, released when the block ends, OK or not. */
        frmPopup = new Form();
        using (frmPopup)
        {   /* Built, shown and read inside the block.                  */
            /* A small fixed-size dialog, centred on the editor, with   */
            /* no taskbar button of its own.                            */
            frmPopup.Text = sTitle;
            frmPopup.StartPosition = FormStartPosition.CenterParent;
            frmPopup.FormBorderStyle = FormBorderStyle.FixedDialog;
            frmPopup.MinimizeBox = false;
            frmPopup.MaximizeBox = false;
            frmPopup.ShowInTaskbar = false;

            /* The program's dialog font, which this editor was given,     */
            /* before any control is made (the box and the buttons are     */
            /* measured from it) -- a bare Form would otherwise start in   */
            /* WinForms' smaller inherited default.  Not freed here.       */
            frmPopup.Font = Jsn_fntDialog;

            /* The prompt, and below it the box holding the default. */
            lblPrompt = new Label();
            lblPrompt.Text = sPrompt;
            lblPrompt.AutoSize = true;
            frmPopup.Controls.Add(lblPrompt);

            /* The box, holding the default text to edit or type over. */
            txtText = new TextBox();
            txtText.Text = sDefaultValue;
            frmPopup.Controls.Add(txtText);

            /* OK and Cancel, side by side under the box on the right. */
            btnOk = new Button();
            btnOk.Text = "OK";
            frmPopup.Controls.Add(btnOk);

            /* Cancel, beside OK. */
            btnCancel = new Button();
            btnCancel.Text = "Cancel";
            frmPopup.Controls.Add(btnCancel);

            /* Size the window to its measured content, then place the   */
            /* controls; the same placing runs again on any resize.      */
            frmPopup.ClientSize = Jsn_DlgInputBoxSize(lblPrompt, txtText, btnOk, btnCancel);
            JSN_DlgLayoutInputBox(frmPopup, lblPrompt, txtText, btnOk, btnCancel);
            objPrompt = new JDP_clsPromptData(frmPopup, null, null, lblPrompt, txtText, btnOk, btnCancel);
            frmPopup.Resize += objPrompt.JDP_OnResize;

            /* OK records the answer before closing; Cancel just closes.      */
            /* OK is tracked with this flag rather than DialogResult -- the   */
            /* reason recorded is that DialogResult behaves differently for   */
            /* modal and modeless windows, though this one is always modal.   */
            /* Enter presses OK and Escape Cancel.                            */
            btnOk.Click += objPrompt.JDP_OnAccept;
            btnCancel.Click += objPrompt.JDP_OnCancel;
            frmPopup.AcceptButton = btnOk;
            frmPopup.CancelButton = btnCancel;

            /* Shown under this window's (the program's) icon, modal over   */
            /* the editor until OK, Cancel or the close box.                */
            frmPopup.Icon = this.Icon;
            if (objPresentation != null &&
                !objPresentation.PRS_objQuestion.QST_IsCurrent(objPresentation))
            {   /* The worker's question has been replaced already: no   */
                /* dialog for a question that is gone.                   */
                return(null);
            }

            /* Listed with the worker's question being answered, if any   */
            /* (QWN_Attach), so an Exit that replaces it closes this      */
            /* dialog too; then shown, modal over the editor.             */
            QWN_clsQuestionWindows.QWN_Attach(frmPopup);
            frmPopup.ShowDialog(this);
            if (objPresentation != null &&
                !objPresentation.PRS_objQuestion.QST_IsCurrent(objPresentation))
            {   /* The worker's question was replaced while the dialog was   */
                /* up: whatever was typed is for a question that is gone.    */
                return(null);
            }

            if (objPrompt.JDP_bAccepted)
            {   /* OK: hand back what was typed (read before the dialog is disposed). */
                return(txtText.Text);
            }
        }

        /* Cancelled or closed. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* JSN_DlgButtonSize:                                                     */
    /*                                                                        */
    /* One size for a row of buttons, so the row is even and every caption    */
    /* fits at any text size.  Public: gui\AboutForm.cs sizes its Close       */
    /* button with it too.                                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     arrButtons : the buttons in the row; their text and font are       */
    /*                  measured.                                             */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Size : the size every button in the row should have.               */
    /*------------------------------------------------------------------------*/
    public static Size JSN_DlgButtonSize(Button[] arrButtons)
    {
        int iWidth;                                      // widest caption plus padding
        int iHeight;                                     // tallest font plus padding
        int i;

        /* The standard 75 x 23 dialog button is the smallest allowed, so   */
        /* at the usual text size the buttons look like any other           */
        /* dialog's.                                                        */
        iWidth = 75;
        iHeight = 23;
        for (i = 0; i < arrButtons.Length; i++)
        {   /* One button per iteration: its caption's width plus 24      */
            /* pixels and its font's height plus 10 for the border and    */
            /* margins (how those two were chosen is not recorded), the   */
            /* largest so far kept.                                       */
            iWidth = Math.Max(iWidth, TextRenderer.MeasureText(
                arrButtons[i].Text, arrButtons[i].Font).Width + 24);
            iHeight = Math.Max(iHeight, arrButtons[i].Font.Height + 10);
        }

        /* Big enough for every caption in the row; the caller gives every   */
        /* button in it this size.                                           */
        return(new Size(iWidth, iHeight));
    }

    /*------------------------------------------------------------------------*/
    /* JSN_DlgSmallIconBitmap:                                                */
    /*                                                                        */
    /* A system icon (SystemIcons.Warning, .Information ...) as a picture     */
    /* at the system's small icon size, for a note in a dialog.  Nothing      */
    /* outside this file calls it.                                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     icoSource : the system icon.                                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Bitmap : the picture (the caller disposes of it).                  */
    /*------------------------------------------------------------------------*/
    public static Bitmap JSN_DlgSmallIconBitmap(Icon icoSource)
    {
        Icon icoSmall;      // the icon at the small size
        Bitmap bmpPicture;  // its picture

        /* A copy of the icon at the small size, turned into the     */
        /* picture, and the copy freed.  The icon passed in is the   */
        /* system's shared one and is never freed here.              */
        icoSmall = new Icon(icoSource, SystemInformation.SmallIconSize);
        bmpPicture = icoSmall.ToBitmap();
        icoSmall.Dispose();

        /* The picture, which the caller now owns. */
        return(bmpPicture);
    }

    /*------------------------------------------------------------------------*/
    /* JSN_DlgNewEtchedLine:                                                  */
    /*                                                                        */
    /* An etched horizontal line for a dialog -- the classic Windows          */
    /* separator -- added to its parent for the dialog's layout to place.     */
    /* Public: gui\AboutForm.cs uses it for its footer rule.                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     ctlParent : the control it goes in.                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Label : the line, added.                                           */
    /*------------------------------------------------------------------------*/
    public static Label JSN_DlgNewEtchedLine(Control ctlParent)
    {
        Label lblLine;                                        // the new line

        /* An empty label with a sunken 3-D border, two pixels high, so   */
        /* only its border's two edges show: that pair is the etched      */
        /* line.  Not auto-sized, so the height stays as set; added to    */
        /* the parent with no width or place yet.                         */
        lblLine = new Label();
        lblLine.AutoSize = false;
        lblLine.BorderStyle = BorderStyle.Fixed3D;
        lblLine.Height = 2;
        ctlParent.Controls.Add(lblLine);

        /* The line, for the dialog's layout to size and place. */
        return(lblLine);
    }

    /*-------------------------------------------------------------------------*/
    /* Jsn_DlgInputBoxSize:                                                    */
    /*                                                                         */
    /* The client size the input dialog needs: as wide as the widest of        */
    /* the prompt, the default text (with room to spare) and JSN_DLG_MIN_CHARS */
    /* characters, and tall enough for the prompt, the box and the             */
    /* buttons, with JSN_DLG_PAD round them.                                   */
    /*                                                                         */
    /* Arguments:                                                              */
    /*     lblPrompt : the prompt.                                             */
    /*     txtText : the input box, holding the default text.                  */
    /*     btnOk     : OK button.                                              */
    /*     btnCancel : Cancel button.                                          */
    /*                                                                         */
    /* Returns:                                                                */
    /*     Size : the client size to give the dialog.                          */
    /*-------------------------------------------------------------------------*/
    static Size Jsn_DlgInputBoxSize(Label lblPrompt, TextBox txtText, Button btnOk,
        Button btnCancel)
    {
        Size szButton;                                   // size of each button
        int iWidth;                                      // content width
        int iHeight;                                     // content height

        /* One size for both buttons (JSN_DlgButtonSize), as the layout uses. */
        szButton = JSN_DlgButtonSize(new Button[] { btnOk, btnCancel });

        /* Widest of: the prompt, the default text with two characters'     */
        /* height to spare for typing, JSN_DLG_MIN_CHARS characters (so a   */
        /* short default does not make a cramped box), and the two          */
        /* buttons side by side.                                            */
        iWidth = lblPrompt.PreferredSize.Width;
        iWidth = Math.Max(iWidth, TextRenderer.MeasureText(txtText.Text,
            txtText.Font).Width + txtText.Font.Height * 2);
        iWidth = Math.Max(iWidth, TextRenderer.MeasureText(
            new string('x', JSN_DLG_MIN_CHARS), txtText.Font).Width);
        iWidth = Math.Max(iWidth, szButton.Width * 2 + JSN_DLG_GAP);

        /* Prompt, box and button rows: JSN_DLG_GAP under the prompt,   */
        /* JSN_DLG_PAD between the box and the buttons -- the steps     */
        /* JSN_DlgLayoutInputBox places them by.                        */
        iHeight = lblPrompt.PreferredSize.Height + JSN_DLG_GAP + txtText.Height
            + JSN_DLG_PAD + szButton.Height;

        /* Content plus padding all round. */
        return(new Size(iWidth + JSN_DLG_PAD * 2, iHeight + JSN_DLG_PAD * 2));
    }

    /*------------------------------------------------------------------------*/
    /* JSN_DlgLayoutInputBox:                                                 */
    /*                                                                        */
    /* Places the input dialog's controls from the dialog's current client    */
    /* size: the prompt at the top left, the box below it across the full     */
    /* width, OK and Cancel at the bottom right with Cancel outermost.        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     frmPopup : the dialog.                                             */
    /*     lblPrompt : the prompt.                                            */
    /*     txtText : the input box.                                           */
    /*     btnOk     : OK button.                                             */
    /*     btnCancel : Cancel button.                                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the controls are moved and sized.                           */
    /*------------------------------------------------------------------------*/
    internal static void JSN_DlgLayoutInputBox(Form frmPopup, Label lblPrompt, TextBox txtText,
        Button btnOk, Button btnCancel)
    {
        Size szButton;                                   // size of each button
        int iButtonTop;                                  // y of the button row

        /* One size for both buttons, the same Jsn_DlgInputBoxSize allowed for. */
        szButton = JSN_DlgButtonSize(new Button[] { btnOk, btnCancel });

        /* Prompt, then the box under it, full width. */
        lblPrompt.Location = new Point(JSN_DLG_PAD, JSN_DLG_PAD);
        txtText.SetBounds(JSN_DLG_PAD, JSN_DLG_PAD + lblPrompt.PreferredSize.Height + JSN_DLG_GAP,
            frmPopup.ClientSize.Width - JSN_DLG_PAD * 2, txtText.Height);

        /* Buttons along the bottom, right-aligned from the dialog's           */
        /* actual size: Cancel outermost, OK to its left, JSN_DLG_GAP apart.   */
        iButtonTop = frmPopup.ClientSize.Height - JSN_DLG_PAD - szButton.Height;
        btnCancel.SetBounds(frmPopup.ClientSize.Width - JSN_DLG_PAD - szButton.Width,
            iButtonTop, szButton.Width, szButton.Height);
        btnOk.SetBounds(btnCancel.Left - JSN_DLG_GAP - szButton.Width,
            iButtonTop, szButton.Width, szButton.Height);
    }

    /*------------------------------------------------------------------------*/
    /* OnLoad:                                                                */
    /*                                                                        */
    /* The Framework's load hook: hands it to Jsn_OnLoad below, which raises  */
    /* the base Load event in its own order and, when the document will not   */
    /* load, closes the window before it appears -- so this adds no base call */
    /* of its own.                                                            */
    /*                                                                        */
    /* An override because the framework requires it: a Form learns it is     */
    /* about to be shown first only through this virtual method.              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     evArgs : the load notification.                                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the window is set up for display, or is closing.            */
    /*------------------------------------------------------------------------*/
    protected override void OnLoad(EventArgs evArgs)
    {
        /* Settings, the document and the base call are in the helper. */
        Jsn_OnLoad(evArgs);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnLoad:                                                            */
    /*                                                                        */
    /* The window is about to appear for the first time: the saved settings   */
    /* are applied, then the document is put up -- the file the window was    */
    /* made for, or a new one for the main window's New command.  A file      */
    /* that will not load closes the window before it ever appears.           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     evArgs : passed on to the base class.                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the window is set up for display, or is closing.            */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnLoad(EventArgs evArgs)
    {
        int iDistance;                                        // splitter position for the saved ratio, pixels
        VoorheesDocument docNew;                              // the main window's New: the format's new document, made for it

        /* The base class first (it raises the Load event), then the      */
        /* saved settings: Jsn_CfgLoad sets the window's placement, the   */
        /* splitter ratio, the font size and the View menu's ticks, and   */
        /* the font and the two label options are applied to the tree     */
        /* and panel here -- before the document is read below, so the    */
        /* tree is first built with them.                                 */
        base.OnLoad(evArgs);
        Jsn_CfgLoad();
        Jsn_ViewApplyFontSize(Jsn_fFontSize);
        Jsn_tree.VTV_SetShowRaw(Jsn_tsmShowRaw.Checked);
        Jsn_tree.VTV_SetColorKeys(Jsn_tsmColorKeys.Checked);

        if (Jsn_splMain.Width > 0)
        {   /* The splitter has a real width (when it could still be 0   */
            /* here is not recorded): put the bar at the saved ratio.    */
            iDistance = (int)(Jsn_splMain.Width * Jsn_dblSplitRatio);
            if (iDistance >= Jsn_splMain.Panel1MinSize
                && iDistance <= Jsn_splMain.Width - Jsn_splMain.Panel2MinSize)
            {   /* Within the panes' minimum sizes: SplitterDistance would throw otherwise. */
                Jsn_splMain.SplitterDistance = iDistance;
            }
        }

        if (Jsn_bNewDocument)
        {   /* The main window's New (Jsn_bNewDocument): nothing is read.     */
            /* An empty document of the format, in its default style, with    */
            /* nothing on disk behind it; its first Save writes the file.     */
            /* Marked modified from the start, so closing the window asks     */
            /* and the title carries the mark -- the title is redrawn,        */
            /* since Jsn_DocSetDocument drew it before the mark was set.      */
            /* Its comment state is asked of the new document itself: a       */
            /* format's new document (Vf_NewDocument) is {} for JSON, empty   */
            /* for INI and the Klipper family, and only REG's header line,    */
            /* so the walk costs nothing.                                     */
            docNew = VoorheesDocument.Vd_CreateNew(Jsn_iFormat);
            Jsn_DocSetDocument(docNew, docNew.Vd_HasComments());
            Jsn_doc.VD_MarkBaseUnsaved();
            Jsn_StateUpdateTitle();
            return;
        }

        /* The file the window was made for, read as the format it was   */
        /* asked to open it as.                                          */
        if (!Jsn_FileLoadFromPath(Jsn_file.OFD_sTempFile, Jsn_iFormat))
        {   /* Unreadable, or not valid in the format: there is nothing to   */
            /* edit, so the window closes itself from here, before it has    */
            /* appeared.  Left open on the empty document it was built       */
            /* with, it would invite a Save that replaces the file with      */
            /* nothing.  Jsn_bLoadFailed tells the closing handler           */
            /* (Jsn_PresentClose) there is no document to ask about and no   */
            /* placement worth remembering.                                  */
            Jsn_bLoadFailed = true;
            Close();
        }
    }

    /*------------------------------------------------------------------------*/
    /* CFG: settings kept in the registry                                     */
    /*------------------------------------------------------------------------*/

    /*-------------------------------------------------------------------------*/
    /* Jsn_CfgSave:                                                            */
    /*                                                                         */
    /* Saves the window's placement, the splitter ratio, the font size and     */
    /* the two label options, for Jsn_CfgLoad to restore next time.  A failure */
    /* is ignored: losing the window position is not worth an error            */
    /* message.                                                                */
    /*                                                                         */
    /* Arguments:                                                              */
    /*     None.                                                               */
    /*                                                                         */
    /* Returns:                                                                */
    /*     void : HKCU\Software\BifrostFM is updated.                          */
    /*-------------------------------------------------------------------------*/
    void Jsn_CfgSave()
    {
        Rectangle rctBounds;  // normal (restored) window bounds
        RegistryKey regKey;   // HKCU\Software\BifrostFM
        int iMaximized;       // 1 when maximized, else 0
        int iShowRaw;         // 1 when raw labels are on, else 0
        int iColorKeys;       // 1 when coloured keys are on, else 0
        bool bSkip;           // set by the partial hook to leave the registry alone

        /* The partial hook first, from "write" (none in the program, so   */
        /* the call compiles to nothing and the settings are written).     */
        bSkip = false;
        Jsn_CfgHookSkip(ref bSkip);
        if (bSkip)
        {   /* A hook said to skip: nothing written, every stored value   */
            /* left as it is.                                             */
            return;
        }

        try
        {   /* Every registry step; a failure anywhere is absorbed by   */
            /* the catch below.                                         */
            if (WindowState == FormWindowState.Normal)
            {   /* Normal window: its current bounds are the ones to keep. */
                rctBounds = new Rectangle(Location, Size);
            }
            else
            {   /* Maximized or minimized: the NORMAL bounds it restores   */
                /* to are kept, so it reopens at a sensible size --        */
                /* being maximized is saved separately below, and a        */
                /* minimized window's own bounds are of no use.            */
                rctBounds = RestoreBounds;
            }

            if (WindowState == FormWindowState.Maximized)
            {   /* Reopen maximized. */
                iMaximized = 1;
            }
            else
            {   /* Reopen at the saved bounds. */
                iMaximized = 0;
            }

            if (Jsn_tsmShowRaw.Checked)
            {   /* Raw labels on. */
                iShowRaw = 1;
            }
            else
            {   /* Friendly labels. */
                iShowRaw = 0;
            }

            if (Jsn_tsmColorKeys.Checked)
            {   /* Coloured keys on. */
                iColorKeys = 1;
            }
            else
            {   /* Keys in the normal colour. */
                iColorKeys = 0;
            }

            /* Window placement and the view options, as DWORDs, into the   */
            /* program's key (CreateSubKey opens it, making it on the       */
            /* first save).  No using block: a throw before Close below     */
            /* leaves the key for the garbage collector to release.         */
            regKey = Registry.CurrentUser.CreateSubKey(Jsn_sRegPath);
            regKey.SetValue(JSN_VAL_X, rctBounds.X);
            regKey.SetValue(JSN_VAL_Y, rctBounds.Y);
            regKey.SetValue(JSN_VAL_W, rctBounds.Width);
            regKey.SetValue(JSN_VAL_H, rctBounds.Height);
            regKey.SetValue(JSN_VAL_MAX, iMaximized);
            regKey.SetValue(JSN_VAL_SHOW_RAW, iShowRaw);
            regKey.SetValue(JSN_VAL_COLOR_KEYS, iColorKeys);

            /* Splitter ratio and font size, as text (registry values      */
            /* have no floating-point type), written invariant -- "." as   */
            /* the decimal point -- so they read back the same under any   */
            /* locale; four and two decimals.  Then the key closed.        */
            regKey.SetValue(JSN_VAL_SPLIT, Jsn_dblSplitRatio.ToString("F4",
                CultureInfo.InvariantCulture));
            regKey.SetValue(JSN_VAL_FONT, Jsn_fFontSize.ToString("F2",
                CultureInfo.InvariantCulture));
            regKey.Close();
        }
        catch
        {   /* The registry would not take it (any failure, whichever     */
            /* step): the settings are simply not kept this time, which   */
            /* is not worth bothering the user about.                     */
        }
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_CfgLoad:                                                           */
    /*                                                                        */
    /* Reads the settings Jsn_CfgSave wrote.  Each value is used only if it   */
    /* is present and sensible:                                               */
    /*     o size and position: all four present, at least 400 x 300, and     */
    /*       still on one of today's monitors (a monitor unplugged since      */
    /*       would otherwise leave the window out of sight);                  */
    /*     o maximized flag;                                                  */
    /*     o splitter ratio: between 0.1 and 0.95;                            */
    /*     o font size: within JSN_FONT_MIN..JSN_FONT_MAX;                    */
    /*     o raw-text view option: 1 for on;                                  */
    /*     o coloured-keys option: 1 for on (off by default).                 */
    /* Anything missing or out of range keeps its default.  A failure is      */
    /* ignored: whatever was taken before it stands, the rest keep their      */
    /* defaults.                                                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : window placement, splitRatio, fontSize and the View menu    */
    /*            ticks may change.                                           */
    /*------------------------------------------------------------------------*/
    void Jsn_CfgLoad()
    {
        RegistryKey regKey;   // HKCU\Software\BifrostFM
        object objX;          // saved left
        object objY;          // saved top
        object objW;          // saved width
        object objH;          // saved height
        object objMax;        // saved maximized flag
        object objRatio;      // saved splitter ratio
        object objFont;       // saved font size
        object objShowRaw;    // saved raw-text view option
        object objColorKeys;  // saved coloured-keys option
        float fSize;          // font size read back
        double dblRatio;      // splitter ratio read back
        Rectangle rctSaved;   // saved bounds
        bool bSkip;           // set by the partial hook to leave the registry alone

        /* The partial hook first, from "read" (none in the program, so   */
        /* the call compiles to nothing and the settings are read).       */
        bSkip = false;
        Jsn_CfgHookSkip(ref bSkip);
        if (bSkip)
        {   /* A hook said to skip: nothing read, every setting keeps the   */
            /* default the constructor gave it.                             */
            return;
        }

        try
        {   /* Every registry step; a failure anywhere is absorbed by the   */
            /* catch below.                                                 */
            /* The program's key, opened for reading only.                  */
            regKey = Registry.CurrentUser.OpenSubKey(Jsn_sRegPath);
            if (regKey == null)
            {   /* No key yet (no settings ever saved): keep the defaults. */
                return;
            }

            /* Every value read first, null where absent, and the colours   */
            /* taken from the same key; then the key closed, before any     */
            /* value is used.  (A throw in between leaves the key for the   */
            /* garbage collector to release.)                               */
            objX = regKey.GetValue(JSN_VAL_X);
            objY = regKey.GetValue(JSN_VAL_Y);
            objW = regKey.GetValue(JSN_VAL_W);
            objH = regKey.GetValue(JSN_VAL_H);
            objMax = regKey.GetValue(JSN_VAL_MAX);
            objRatio = regKey.GetValue(JSN_VAL_SPLIT);
            objFont = regKey.GetValue(JSN_VAL_FONT);
            objShowRaw = regKey.GetValue(JSN_VAL_SHOW_RAW);
            objColorKeys = regKey.GetValue(JSN_VAL_COLOR_KEYS);
            Jsn_CfgLoadColors(regKey);
            regKey.Close();

            if (objFont != null)
            {   /* Font size saved: use it if it reads and is in range. */
                if (float.TryParse(objFont.ToString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out fSize))
                {   /* Read: now check the range. */
                    if (fSize >= JSN_FONT_MIN && fSize <= JSN_FONT_MAX)
                    {   /* Sensible size. */
                        Jsn_fFontSize = fSize;
                    }
                }
            }

            if (objX != null && objY != null && objW != null && objH != null)
            {   /* Placement saved, all four parts: used only if it is a      */
                /* real size (400 x 300 at least) and still on one of         */
                /* today's monitors -- one unplugged since would otherwise    */
                /* leave the window out of sight.  The values are cast        */
                /* unchecked: Jsn_CfgSave writes them as DWORDs, and one of   */
                /* another type (only a hand edit could make it) throws       */
                /* here, so the catch skips every setting after this one.     */
                rctSaved = new Rectangle((int)objX, (int)objY, (int)objW, (int)objH);
                if (rctSaved.Width >= 400 && rctSaved.Height >= 300
                    && Jsn_CfgIsOnScreen(rctSaved))
                {   /* Usable: placed by hand there, not by Windows. */
                    StartPosition = FormStartPosition.Manual;
                    Location = rctSaved.Location;
                    Size = rctSaved.Size;
                }
            }

            if (objMax != null && (int)objMax == 1)
            {   /* It was maximized: open maximized, over the normal bounds   */
                /* just set, which it restores to.  (An unchecked cast, as    */
                /* above.)                                                    */
                WindowState = FormWindowState.Maximized;
            }

            if (objRatio != null)
            {   /* Splitter ratio saved: use it if it reads and leaves both panes visible. */
                if (double.TryParse(objRatio.ToString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out dblRatio))
                {   /* Read: now check the range. */
                    if (dblRatio > 0.1 && dblRatio < 0.95)
                    {   /* Sensible ratio. */
                        Jsn_dblSplitRatio = dblRatio;
                    }
                }
            }

            if (objShowRaw != null && objShowRaw is int)
            {   /* Raw-text view option saved as a DWORD (the type tested,   */
                /* unlike the placement's): 1 is on.  OnLoad applies the     */
                /* tick to the tree.                                         */
                Jsn_tsmShowRaw.Checked = ((int)objShowRaw == 1);
            }

            if (objColorKeys != null && objColorKeys is int)
            {   /* Coloured-keys option saved: 1 is on. */
                Jsn_tsmColorKeys.Checked = ((int)objColorKeys == 1);
            }
        }
        catch
        {   /* Unreadable or malformed settings: what was taken before      */
            /* the failure stands, everything after it keeps its default.   */
        }
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_CfgIsOnScreen:                                                     */
    /*                                                                        */
    /* Whether a saved window rectangle is still usefully visible: at least   */
    /* 100 x 50 pixels of it within some monitor's working area.  Meant as    */
    /* enough to grab the window by, but the test does not look at WHERE the  */
    /* overlap is, so a window whose title bar is off screen can pass.        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     rctBounds : the window rectangle.                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true if it is usefully visible on some monitor.             */
    /*------------------------------------------------------------------------*/
    bool Jsn_CfgIsOnScreen(Rectangle rctBounds)
    {
        Screen[] arrScreens;
        int i;
        Rectangle rctVisible;                               // part of bounds on one monitor

        /* The monitors, read once into a local for the loop. */
        arrScreens = Screen.AllScreens;
        for (i = 0; i < arrScreens.Length; i++)
        {   /* One monitor per iteration: the part of the rectangle on   */
            /* its working area (the desktop less the taskbar).          */
            rctVisible = Rectangle.Intersect(arrScreens[i].WorkingArea, rctBounds);
            if (rctVisible.Width >= 100 && rctVisible.Height >= 50)
            {   /* Enough of it on this monitor. */
                return(true);
            }
        }

        /* Off every monitor, or only a sliver showing. */
        return(false);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_CfgReadColor:                                                      */
    /*                                                                        */
    /* One colour setting: a DWORD 0x00RRGGBB under HKCU\Software\BifrostFM;  */
    /* a missing value, or one of another type, gives the default.            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     regKey : the open settings key.                                    */
    /*     sName    : the value's name (JsonColorTopName, JsonColorText ...). */
    /*     clrDefault : the role's default colour.                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Color : the colour.                                                */
    /*------------------------------------------------------------------------*/
    static Color Jsn_CfgReadColor(RegistryKey regKey, string sName, Color clrDefault)
    {
        object objValue;                                   // the stored value, or null

        /* The stored value, or null when there is none. */
        objValue = regKey.GetValue(sName);
        if (!(objValue is int))
        {   /* Not set (or not a DWORD): the default. */
            return(clrDefault);
        }

        /* Opaque, from its red, green and blue bytes: the DWORD read as   */
        /* ARGB has a zero alpha byte, so the alpha is set to 255.         */
        return(Color.FromArgb(255, Color.FromArgb((int)objValue)));
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_CfgLoadColors:                                                     */
    /*                                                                        */
    /* Reads every role's colour into the tree's colour arrays, each missing  */
    /* one taking its default (Jsn_CfgReadColor).                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     regKey : the open settings key.                                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : every slot of the tree's two colour arrays is set.          */
    /*------------------------------------------------------------------------*/
    void Jsn_CfgLoadColors(RegistryKey regKey)
    {
        Color[] arrNames;                                // the name roles' defaults
        Color[] arrValues;                               // the value roles' defaults
        int i;

        /* The tree's default colours, for any role with no saved value;    */
        /* then each role's colour into the tree's arrays, indexed as the   */
        /* name arrays above are (VTV_NAME_ / VF_ROLE_ order).              */
        arrNames = VTV_clsTreeView.VTV_DefaultNameColors();
        arrValues = VTV_clsTreeView.VTV_DefaultValueColors();
        for (i = 0; i < Jsn_asNameColors.Length; i++)
        {   /* One name role per iteration: saved, or its default. */
            Jsn_tree.VTV_ReadNameColors()[i] = Jsn_CfgReadColor(regKey, Jsn_asNameColors[i], arrNames[i]);
        }
        for (i = 0; i < Jsn_asValueColors.Length; i++)
        {   /* One value role per iteration: saved, or its default. */
            Jsn_tree.VTV_ReadValueColors()[i] = Jsn_CfgReadColor(regKey, Jsn_asValueColors[i], arrValues[i]);
        }
    }

    /*-------------------------------------------------------------------------*/
    /* Jsn_CfgWriteColor:                                                      */
    /*                                                                         */
    /* Stores one colour setting as a DWORD 0x00RRGGBB (Jsn_CfgReadColor reads */
    /* it back).                                                               */
    /*                                                                         */
    /* Arguments:                                                              */
    /*     regKey : the settings key, open for writing.                        */
    /*     sName : the value's name.                                           */
    /*     clrColor : the colour.                                              */
    /*                                                                         */
    /* Returns:                                                                */
    /*     void : the value is written.                                        */
    /*-------------------------------------------------------------------------*/
    static void Jsn_CfgWriteColor(RegistryKey regKey, string sName, Color clrColor)
    {
        /* Red, green and blue, the alpha byte masked off; written as a   */
        /* DWORD explicitly, the type Jsn_CfgReadColor tests for.         */
        regKey.SetValue(sName, clrColor.ToArgb() & 0x00FFFFFF, RegistryValueKind.DWord);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_CfgStoreColor:                                                     */
    /*                                                                        */
    /* Keeps one colour setting: a colour that is its role's default is       */
    /* stored as NO value (a missing value means                              */
    /* the default -- so Reset leaves nothing behind, and a default that      */
    /* follows the system's text colour keeps following it); any other is     */
    /* written (Jsn_CfgWriteColor).  Colours compare by their RGB values.     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     regKey : the settings key, open for writing.                       */
    /*     sName    : the value's name.                                       */
    /*     clrColor : the colour.                                             */
    /*     clrDefault : the role's default.                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the value is written or removed.                            */
    /*------------------------------------------------------------------------*/
    static void Jsn_CfgStoreColor(RegistryKey regKey, string sName, Color clrColor,
        Color clrDefault)
    {
        if (clrColor.ToArgb() == clrDefault.ToArgb())
        {   /* The default: no value (removing a missing one is not an error). */
            regKey.DeleteValue(sName, false);
            return;
        }

        /* A colour of the user's own. */
        Jsn_CfgWriteColor(regKey, sName, clrColor);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_CfgSaveColors:                                                     */
    /*                                                                        */
    /* Stores every role's colour from the tree's tables (Jsn_CfgStoreColor,  */
    /* names Jsn_asNameColors / Jsn_asValueColors), for Jsn_CfgLoadColors     */
    /* next time;                                                             */
    /* called once the Colors dialog closes with OK, whether or not a colour  */
    /* changed (the View menu's colours handler).  Failure is ignored,        */
    /* as Jsn_CfgSave ignores it: the colours still apply for this session.   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : HKCU\Software\BifrostFM holds the colours.                  */
    /*------------------------------------------------------------------------*/
    void Jsn_CfgSaveColors()
    {
        RegistryKey regKey;  // HKCU\Software\BifrostFM
        Color[] arrNames;    // the name roles' defaults
        Color[] arrValues;   // the value roles' defaults
        bool bSkip;          // set by the partial hook to leave the registry alone
        int i;

        /* The partial hook first, from "write" (none in the program, so   */
        /* the call compiles to nothing and the colours are written).      */
        bSkip = false;
        Jsn_CfgHookSkip(ref bSkip);
        if (bSkip)
        {   /* A hook said to skip: no colour written or removed. */
            return;
        }

        try
        {   /* Every registry step; a failure anywhere is absorbed by the    */
            /* catch below.                                                  */
            /* The defaults each colour is compared with (a default is       */
            /* stored as no value), and the program's key, opened or made.   */
            /* No using block: a throw before Close leaves the key for the   */
            /* garbage collector to release.                                 */
            arrNames = VTV_clsTreeView.VTV_DefaultNameColors();
            arrValues = VTV_clsTreeView.VTV_DefaultValueColors();
            regKey = Registry.CurrentUser.CreateSubKey(Jsn_sRegPath);
            for (i = 0; i < Jsn_asNameColors.Length; i++)
            {   /* One name role per iteration: written, or removed when   */
                /* it is the default.                                      */
                Jsn_CfgStoreColor(regKey, Jsn_asNameColors[i], Jsn_tree.VTV_ReadNameColors()[i], arrNames[i]);
            }
            for (i = 0; i < Jsn_asValueColors.Length; i++)
            {   /* One value role per iteration, likewise. */
                Jsn_CfgStoreColor(regKey, Jsn_asValueColors[i], Jsn_tree.VTV_ReadValueColors()[i], arrValues[i]);
            }
            /* Done: the key closed. */
            regKey.Close();
        }
        catch
        {   /* The registry would not take it: the colours apply for this   */
            /* session only, which is not worth bothering the user about.   */
        }
    }

    /*------------------------------------------------------------------------*/
    /* OnFormClosing:                                                         */
    /*                                                                        */
    /* The Framework's closing hook: hands the same event object to           */
    /* Jsn_OnFormClosing below, whose Cancel the Framework then reads.  The   */
    /* helper wraps the editor's close decision (Jsn_PresentClose, which      */
    /* makes the base call on the paths that let the close go ahead) with its */
    /* bookkeeping of the closing call's actual return -- a base call here as */
    /* well would run outside that bookkeeping.                               */
    /*                                                                        */
    /* An override because the framework requires it: a Form is asked about   */
    /* its own close first only through this virtual method.                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     evClosing : the closing event; the helper may set Cancel.          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the close decision is recorded in evClosing.                */
    /*------------------------------------------------------------------------*/
    protected override void OnFormClosing(FormClosingEventArgs evClosing)
    {
        /* The close decision and the base call are in the helper. */
        Jsn_OnFormClosing(evClosing);
    }

    /*------------------------------------------------------------------------*/
    /* OnFormClosed (override):                                               */
    /*                                                                        */
    /* An override because the framework requires it: a Form learns that its  */
    /* own close has gone through, before the FormClosed event is raised,     */
    /* only through this virtual method.  The close is certain here, so the   */
    /* tree is emptied now.  The Windows tree control spends about 60         */
    /* microseconds removing each node however it is done -- one by one, all  */
    /* at once, collapsed first, or by destroying the window, as measured --  */
    /* so a tree of hundreds of thousands of nodes holds the program for tens */
    /* of seconds.  For one that large (JSN_CLOSE_WAIT_NODES) the editor is   */
    /* hidden and a wait box is shown first, so the time reads as work rather */
    /* than as a program that has stopped.                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     evClosed : the closed event, passed on to the base class.          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the tree empty, then the FormClosed event raised.           */
    /*------------------------------------------------------------------------*/
    protected override void OnFormClosed(FormClosedEventArgs evClosed)
    {
        int iNodes;        // nodes the tree holds
        Form frmWait;      // the wait box, or null
        Label lblWait;     // its one line

        /* The tree's selection handlers unhooked first: removing the         */
        /* selected node moves the selection, and the panel must not be       */
        /* reloaded (or a held move asked about) for a window that is gone.   */
        Jsn_tree.BeforeSelect -= Jsn_OnTreeBeforeSelect;
        Jsn_tree.AfterSelect -= Jsn_OnTreeAfterSelect;

        /* Every node, counted by the framework from its own node           */
        /* collections (as found), to decide whether the removal is long.   */
        iNodes = Jsn_tree.GetNodeCount(true);
        frmWait = null;

        if (iNodes >= JSN_CLOSE_WAIT_NODES)
        {   /* A large tree: the removal takes seconds, so a wait box. */

            /* The box built at the size its line needs, in the program's   */
            /* dialog font, centred over the editor while it is still       */
            /* shown, with no buttons and no close box: nothing can be      */
            /* done while the control works, and it closes itself after.    */
            frmWait = new Form();
            frmWait.Text = JSN_CAPTION;
            frmWait.Font = Jsn_fntDialog;
            frmWait.FormBorderStyle = FormBorderStyle.FixedDialog;
            frmWait.ControlBox = false;
            frmWait.ShowInTaskbar = false;
            lblWait = new Label();
            lblWait.AutoSize = true;
            lblWait.UseMnemonic = false;
            lblWait.Text = "Closing the editor: removing " + iNodes.ToString("N0") + " tree entries. Please wait.";
            lblWait.Location = new Point(JSN_DLG_PAD, JSN_DLG_PAD);
            frmWait.Controls.Add(lblWait);
            frmWait.ClientSize = new Size(lblWait.PreferredWidth + 2 * JSN_DLG_PAD,
                lblWait.PreferredHeight + 2 * JSN_DLG_PAD);
            PRM_clsPromptForm.PRM_CentreOver(frmWait, this);

            /* The editor hidden and the box shown and painted NOW: the     */
            /* removal below holds the UI thread, so nothing is painted     */
            /* while it runs, and a box shown only by a later paint would   */
            /* never appear.                                                */
            Hide();
            frmWait.Show();
            frmWait.Refresh();
        }

        /* Every node removed with the drawing held, so the control does    */
        /* not repaint or rescroll per node.  No EndUpdate: the window is   */
        /* about to be destroyed, and nothing is drawn again.               */
        Jsn_tree.BeginUpdate();
        Jsn_tree.Nodes.Clear();

        if (frmWait != null)
        {   /* The removal is over: the wait box goes. */
            frmWait.Close();
            frmWait.Dispose();
        }

        /* The base class last, which raises the FormClosed event -- the   */
        /* coordinator's hook among its handlers.                          */
        base.OnFormClosed(evClosed);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnFormClosing:                                                     */
    /*                                                                        */
    /* The window's closing: the decision is Jsn_PresentClose's, made with    */
    /* the handler counted as running (Jsn_iClosing).  It shows nothing:      */
    /* typing or changes only post the close's questions to the window's      */
    /* queue, so a close a save asks for is never posted from inside this     */
    /* handler, and one close never nests in another.                         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     evClosing : the closing event; cancelled to keep the window.       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the close allowed or cancelled; any close a save asked for  */
    /*            is posted once this handler has returned.                   */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnFormClosing(FormClosingEventArgs evClosing)
    {
        /* Inside the handler from here to the finally (a count of the   */
        /* UI's own nesting; nothing to do with what the worker runs).   */
        Jsn_iClosing = checked(Jsn_iClosing + 1);
        try
        {   /* The decision, released in the finally however it ends. */
            Jsn_PresentClose(evClosing);
        }
        finally
        {   /* Out of the handler: held Save answers and any close a save   */
            /* asked for may be posted now, as it can no longer nest in     */
            /* this one, and the coordinator told, so a close question      */
            /* that waited while this handler ran is looked at again (it    */
            /* finds the close's questions asked for, and waits on).        */
            Jsn_iClosing--;
            Jsn_PostReadyAcknowledgements();
            Jsn_PostReadyCloses();
            Jsn_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
        }
    }
    /*------------------------------------------------------------------------*/
    /* Jsn_OnSaveClick:                                                       */
    /*                                                                        */
    /* File > Save (Ctrl+S): the Save item posted to the window's queue,      */
    /* which saves where Save writes now when it runs it (Jsn_RunSave).       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the menu item (unused).                                */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item posted (or dropped as one already in hand).        */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnSaveClick(object objSender, EventArgs evArgs)
    {
        /* Posted, as Find and Save As are: the panel's question, the   */
        /* encoding, the write or the stage, any message box and the    */
        /* hand-over all run from the queue, never inside the click.    */
        Jsn_objQueue.WQU_Post((int)Jsn_enmItem.Save, null);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_RunSave:                                                           */
    /*                                                                        */
    /* The Save item, run from the window's queue: save where Save writes     */
    /* now (Jsn_FileSave), if a save may start at all.                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the save started, or nothing done; the document is kept     */
    /*            either way.                                                 */
    /*------------------------------------------------------------------------*/
    private void Jsn_RunSave()
    {
        if (!Jsn_file.OFD_CanRequestUserFile())
        {   /* Not now -- the worker is busy, exiting or holding a picker   */
            /* (OFD_CanRequestUserFile), judged as the item runs: nothing   */
            /* is done, as a pane's own command would not start either.     */
            return;
        }

        /* The save; whether one started is not needed here -- a refusal   */
        /* has said why itself.                                            */
        Jsn_FileSave();
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnCloseClick:                                                      */
    /*                                                                        */
    /* File > Close (Ctrl+W): the Close item posted; the window's queue runs  */
    /* the ordinary close, which asks about unsaved changes through the same  */
    /* queue (Jsn_PresentClose, Jsn_ShowCloseQuestion).                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the menu item (unused).                                */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item posted (or dropped as one already in hand).        */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnCloseClick(object objSender, EventArgs evArgs)
    {
        /* Posted: the close runs from the queue (Jsn_RunItem). */
        Jsn_objQueue.WQU_Post((int)Jsn_enmItem.Close, null);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnUndoClick:                                                       */
    /*                                                                        */
    /* Edit > Undo (Ctrl+Z): the Undo item posted; the queue runs             */
    /* Jsn_EditUndo, which decides whether the panel, a text box or the       */
    /* document steps back.                                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the menu item (unused).                                */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item posted (or dropped as one already in hand).        */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnUndoClick(object objSender, EventArgs evArgs)
    {
        /* Posted: the undo runs from the queue (Jsn_RunItem). */
        Jsn_objQueue.WQU_Post((int)Jsn_enmItem.Undo, null);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnRedoClick:                                                       */
    /*                                                                        */
    /* Edit > Redo (Ctrl+Y): the Redo item posted; the queue runs             */
    /* Jsn_EditRedo.                                                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the menu item (unused).                                */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item posted (or dropped as one already in hand).        */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnRedoClick(object objSender, EventArgs evArgs)
    {
        /* Posted: the redo runs from the queue (Jsn_RunItem). */
        Jsn_objQueue.WQU_Post((int)Jsn_enmItem.Redo, null);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnFindClick:                                                       */
    /*                                                                        */
    /* Search > Find... (Ctrl+F): the Find window, Find-only layout.          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the menu item (unused).                                */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item posted (or dropped as one already in hand); the    */
    /*            queue shows the window (Jsn_SearchShowFind).                */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnFindClick(object objSender, EventArgs evArgs)
    {
        /* Posted to the window's own queue (Jsn_RunItem runs it). */
        Jsn_objQueue.WQU_Post((int)Jsn_enmItem.Find, null);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnFindReplaceClick:                                                */
    /*                                                                        */
    /* Search > Find + Replace... (Ctrl+H): the same window with its Replace  */
    /* controls.                                                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the menu item (unused).                                */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item posted (or dropped as one already in hand); the    */
    /*            queue shows the window (Jsn_SearchShowFind).                */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnFindReplaceClick(object objSender, EventArgs evArgs)
    {
        /* Posted to the window's own queue (Jsn_RunItem runs it). */
        Jsn_objQueue.WQU_Post((int)Jsn_enmItem.FindReplace, null);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnFontLargerClick:                                                 */
    /*                                                                        */
    /* View > Font Size > Larger (Ctrl++): the FontLarger item posted; the    */
    /* queue steps to the next preset up (Jsn_ViewStepFontSize).              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the menu item (unused).                                */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item posted (or dropped as one already in hand).        */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnFontLargerClick(object objSender, EventArgs evArgs)
    {
        /* Posted: the step runs from the queue (Jsn_RunItem). */
        Jsn_objQueue.WQU_Post((int)Jsn_enmItem.FontLarger, null);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnFontSmallerClick:                                                */
    /*                                                                        */
    /* View > Font Size > Smaller (Ctrl+-): the FontSmaller item posted; the  */
    /* queue steps to the next preset down (Jsn_ViewStepFontSize).            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the menu item (unused).                                */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item posted (or dropped as one already in hand).        */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnFontSmallerClick(object objSender, EventArgs evArgs)
    {
        /* Posted: the step runs from the queue (Jsn_RunItem). */
        Jsn_objQueue.WQU_Post((int)Jsn_enmItem.FontSmaller, null);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnFontDefaultClick:                                                */
    /*                                                                        */
    /* View > Font Size > Default (Ctrl+0): the FontDefault item posted; the  */
    /* queue goes back to the standard WinForms control font size, which is   */
    /* not itself a preset.                                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the menu item (unused).                                */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item posted (or dropped as one already in hand).        */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnFontDefaultClick(object objSender, EventArgs evArgs)
    {
        /* Posted: the size is set from the queue (Jsn_RunItem). */
        Jsn_objQueue.WQU_Post((int)Jsn_enmItem.FontDefault, null);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnShowRawClick:                                                    */
    /*                                                                        */
    /* View > Show Raw Text in Tree: the ShowRaw item posted; the queue turns */
    /* the tick over and redraws every label (Jsn_ViewToggleShowRaw).         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the menu item (unused).                                */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item posted (or dropped as one already in hand).        */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnShowRawClick(object objSender, EventArgs evArgs)
    {
        /* Posted: the switch runs from the queue (Jsn_RunItem). */
        Jsn_objQueue.WQU_Post((int)Jsn_enmItem.ShowRaw, null);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnColorKeysClick:                                                  */
    /*                                                                        */
    /* View > Color Keys: the ColorKeys item posted; the queue turns the tick */
    /* over and repaints the tree (Jsn_ViewToggleColorKeys).                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the menu item (unused).                                */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item posted (or dropped as one already in hand).        */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnColorKeysClick(object objSender, EventArgs evArgs)
    {
        /* Posted: the switch runs from the queue (Jsn_RunItem). */
        Jsn_objQueue.WQU_Post((int)Jsn_enmItem.ColorKeys, null);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnColorsClick:                                                     */
    /*                                                                        */
    /* View > Colors...: the Colors dialog (Jsn_ViewShowColors).              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the menu item (unused).                                */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item posted (or dropped as one already in hand); the    */
    /*            queue shows the dialog (Jsn_ViewShowColors).                */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnColorsClick(object objSender, EventArgs evArgs)
    {
        /* Posted to the window's own queue (Jsn_RunItem runs it). */
        Jsn_objQueue.WQU_Post((int)Jsn_enmItem.Colors, null);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnKeyChanged:                                                      */
    /*                                                                        */
    /* The Key box's text changed: the panel now differs from the document,   */
    /* on the fields' side, which locks the Raw box until Save or Discard.    */
    /* A change the program makes while loading the panel is not an edit,     */
    /* and Jsn_EditMarkDirty passes it over.                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the Key box.                                           */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the panel's dirty flags are set (a user edit).              */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnKeyChanged(object objSender, EventArgs evArgs)
    {
        /* A fields-side change (false: not the Raw box). */
        Jsn_EditMarkDirty(false);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnValueChanged:                                                    */
    /*                                                                        */
    /* The Value box's text changed: the panel now differs from the document, */
    /* on the fields' side, as for the Key box.                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the Value box.                                         */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the panel's dirty flags are set (a user edit).              */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnValueChanged(object objSender, EventArgs evArgs)
    {
        /* A fields-side change (false: not the Raw box). */
        Jsn_EditMarkDirty(false);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnCommentChanged:                                                  */
    /*                                                                        */
    /* The Comment box's text changed: the panel now differs from the         */
    /* document, on the fields' side, as for the Key box.                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the Comment box.                                       */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the panel's dirty flags are set (a user edit).              */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnCommentChanged(object objSender, EventArgs evArgs)
    {
        /* A fields-side change (false: not the Raw box). */
        Jsn_EditMarkDirty(false);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnPanelRawChanged:                                                 */
    /*                                                                        */
    /* The Raw box's text changed: the panel now differs from the document,   */
    /* on the Raw side, which locks the other fields until Save or Discard.   */
    /* A change the program makes while loading the panel is not an edit,     */
    /* and Jsn_EditMarkDirty passes it over.                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the Raw box (unused).                                  */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the panel's dirty flags are set (a user edit).              */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnPanelRawChanged(object objSender, EventArgs evArgs)
    {
        /* A Raw-side change (true). */
        Jsn_EditMarkDirty(true);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnDiscardClick:                                                    */
    /*                                                                        */
    /* The panel's Discard button: the Discard item posted; the queue throws  */
    /* away what was typed and reloads the panel from the selected entry      */
    /* (Jsn_EditDiscard).                                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the button (unused).                                   */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item posted (or dropped as one already in hand).        */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnDiscardClick(object objSender, EventArgs evArgs)
    {
        /* Posted: the discard runs from the queue (Jsn_RunItem). */
        Jsn_objQueue.WQU_Post((int)Jsn_enmItem.Discard, null);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnPanelResize:                                                     */
    /*                                                                        */
    /* The edit panel's size changed: its controls placed again               */
    /* (Jsn_EditLayoutPanel).                                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the panel (unused).                                    */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the controls are moved and sized.                           */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnPanelResize(object objSender, EventArgs evArgs)
    {
        /* The layout, from the panel's new size. */
        Jsn_EditLayoutPanel();
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnFontPresetClick:                                                 */
    /*                                                                        */
    /* One of View > Font Size's preset items: the FontPreset item posted     */
    /* with its size, which the item keeps in its Tag (Jsn_InitMenuStrip);    */
    /* the queue applies it.  One handler serves every preset, which the      */
    /* menu builds in a loop, so the size is read from the sender.            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the preset item; its Tag holds its size (a float).     */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item posted (or dropped as one already in hand).        */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnFontPresetClick(object objSender, EventArgs evArgs)
    {
        /* The size of the item clicked, posted with the item. */
        Jsn_objQueue.WQU_Post((int)Jsn_enmItem.FontPreset, (float)((ToolStripMenuItem)objSender).Tag);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnFileMenuOpening:                                                 */
    /*                                                                        */
    /* The File menu is opening: Reopen As greyed whenever it would be        */
    /* refused (Jsn_FileReopenAs's test), so the menu shows it.               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the File menu (unused).                                */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : Reopen As enabled or greyed.                                */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnFileMenuOpening(object objSender, EventArgs evArgs)
    {
        /* Enabled only when the file on disk is a settled copy of the last   */
        /* save: no Save unanswered, none of the window's own work running,   */
        /* and no image save whose private copy could not be rewritten.       */
        /* The same test as Jsn_FileReopenAs's refusal, which also covers a   */
        /* change while the menu is open.                                     */
        Jsn_tsmReopenAs.Enabled = Jsn_iPendingSaves == 0 && !JSN_HasLocalWork() && !Jsn_bWorkCopyFailed;
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnSplitterMoved:                                                   */
    /*                                                                        */
    /* The user dragged the splitter: its place kept as a fraction of the     */
    /* width (Jsn_dblSplitRatio), which a resize puts back (Jsn_OnResize) and */
    /* the settings keep (Jsn_CfgSave).                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the splitter (unused).                                 */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the ratio updated.                                          */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnSplitterMoved(object objSender, SplitterEventArgs evArgs)
    {
        if (Jsn_splMain.Width > 0)
        {   /* A real width (a zero one would divide by zero): the bar's   */
            /* place as a fraction of it.                                  */
            Jsn_dblSplitRatio = (double)Jsn_splMain.SplitterDistance / Jsn_splMain.Width;
        }
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnResize:                                                          */
    /*                                                                        */
    /* The window was resized: the splitter put back at the kept ratio, so    */
    /* the two panes keep their proportions.                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the window (unused).                                   */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the splitter moved, or left where it is.                    */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnResize(object objSender, EventArgs evArgs)
    {
        int iDistance;

        if (Jsn_splMain.Width > 0 && WindowState != FormWindowState.Minimized)
        {   /* A real width and not minimized (a minimized window's       */
            /* sizes are not the panes'): the bar's place at the ratio.   */
            iDistance = (int)(Jsn_splMain.Width * Jsn_dblSplitRatio);
            if (iDistance >= Jsn_splMain.Panel1MinSize &&
                iDistance <= Jsn_splMain.Width - Jsn_splMain.Panel2MinSize)
            {   /* Within the panes' minimum sizes: SplitterDistance   */
                /* would throw otherwise.                              */
                Jsn_splMain.SplitterDistance = iDistance;
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnPanelTypeChanged:                                                */
    /*                                                                        */
    /* The Type box's choice changed: the right value editor shown for the    */
    /* new kind, and the panel marked as differing from the document (unless  */
    /* the program made the change while loading the panel).                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the Type box (unused).                                 */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the value editor swapped; the dirty flags set for an edit.  */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnPanelTypeChanged(object objSender, EventArgs evArgs)
    {
        /* The value editor for the kind (the checkbox for a boolean, the   */
        /* box otherwise), then the change marked -- a fields-side one      */
        /* (false); Jsn_EditMarkDirty ignores a load's own change.          */
        Jsn_EditUpdateValueEditor();
        Jsn_EditMarkDirty(false);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnPanelBooleanChanged:                                             */
    /*                                                                        */
    /* The boolean checkbox was ticked or cleared: its caption follows, so it */
    /* reads "true" or "false", and the panel is marked as differing from     */
    /* the document (unless the program made the change while loading).       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the checkbox (unused).                                 */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the caption set; the dirty flags set for an edit.           */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnPanelBooleanChanged(object objSender, EventArgs evArgs)
    {
        if (Jsn_chkBoolValue.Checked)
        {   /* Ticked: true. */
            Jsn_chkBoolValue.Text = "true";
        }
        else
        {   /* Cleared: false. */
            Jsn_chkBoolValue.Text = "false";
        }

        /* A fields-side change (false); Jsn_EditMarkDirty ignores one the   */
        /* program makes while loading the panel.                            */
        Jsn_EditMarkDirty(false);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnContextAddChild:                                                 */
    /*                                                                        */
    /* One of the right-click menu's "add" entries (a kind under Add Child or */
    /* Add Top-Level, or a Comment entry): the entry's Tag holds what it adds */
    /* and where (JCA_clsContextChoice), so this one handler serves every     */
    /* such entry the menu builds at run time.                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the menu entry clicked; its Tag is a JCA_ choice.      */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item posted with the entry's choice (or dropped as one  */
    /*            already in hand); the queue adds it (Jsn_ContextAddChild).  */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnContextAddChild(object objSender, EventArgs evArgs)
    {
        /* The clicked entry's own choice, from its Tag, posted to the   */
        /* window's own queue (Jsn_RunItem runs it).                     */
        Jsn_objQueue.WQU_Post((int)Jsn_enmItem.AddChild, ((ToolStripMenuItem)objSender).Tag);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnContextRename:                                                   */
    /*                                                                        */
    /* The right-click menu's Rename Key...: the member's position path is in */
    /* the entry's Tag (Jsn_ContextBuild).                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the menu entry; its Tag is the member's path.          */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item posted with the path; the queue renames the key    */
    /*            (Jsn_ContextRenameKey).                                     */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnContextRename(object objSender, EventArgs evArgs)
    {
        /* The path as the menu built it -- not the selection now, and not   */
        /* copied: nothing changes it -- posted to the window's own queue.   */
        Jsn_objQueue.WQU_Post((int)Jsn_enmItem.Rename, ((ToolStripMenuItem)objSender).Tag);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnContextCopy:                                                     */
    /*                                                                        */
    /* The right-click menu's Copy: the entry's position path is in the menu  */
    /* entry's Tag.                                                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the menu entry; its Tag is the entry's path.           */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item posted with the path; the queue copies the entry   */
    /*            to the editor's clipboard (Jsn_ContextCopy).                */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnContextCopy(object objSender, EventArgs evArgs)
    {
        /* The path as the menu built it, posted to the window's own queue. */
        Jsn_objQueue.WQU_Post((int)Jsn_enmItem.Copy, ((ToolStripMenuItem)objSender).Tag);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnContextPaste:                                                    */
    /*                                                                        */
    /* The right-click menu's Paste: the container's position path is in the  */
    /* menu entry's Tag.                                                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the menu entry; its Tag is the container's path.       */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item posted with the path; the queue pastes the copy    */
    /*            (Jsn_ContextPaste).                                         */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnContextPaste(object objSender, EventArgs evArgs)
    {
        /* The path as the menu built it, posted to the window's own queue. */
        Jsn_objQueue.WQU_Post((int)Jsn_enmItem.Paste, ((ToolStripMenuItem)objSender).Tag);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnContextDelete:                                                   */
    /*                                                                        */
    /* The right-click menu's Delete: the entry's position path is in the     */
    /* menu entry's Tag.                                                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the menu entry; its Tag is the entry's path.           */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item posted with the path; the queue removes the entry  */
    /*            (Jsn_ContextDelete).                                        */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnContextDelete(object objSender, EventArgs evArgs)
    {
        /* The path as the menu built it, posted to the window's own queue. */
        Jsn_objQueue.WQU_Post((int)Jsn_enmItem.Delete, ((ToolStripMenuItem)objSender).Tag);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_OnReopenFormatClick:                                               */
    /*                                                                        */
    /* One of File > Reopen As's format items: the format is in the item's    */
    /* Tag (Jsn_FileFormatItem), so this one handler serves them all.         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the item; its Tag is the VoorheesFormat.VF_ format.    */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item posted with the format; the queue reads the file   */
    /*            again in it (Jsn_FileReopenAs).                             */
    /*------------------------------------------------------------------------*/
    private void Jsn_OnReopenFormatClick(object objSender, EventArgs evArgs)
    {
        /* The format of the item clicked (a boxed int, which the queue     */
        /* compares by value, so a second click on the same format while    */
        /* the first waits is dropped), posted to the window's own queue.   */
        Jsn_objQueue.WQU_Post((int)Jsn_enmItem.ReopenAs, ((ToolStripMenuItem)objSender).Tag);
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_RunItem:                                                           */
    /*                                                                        */
    /* The window's own queue's runner (WindowQueue.cs): one item the         */
    /* handlers posted, run to its end -- the Find window or the Colors       */
    /* dialog shown, a Save As destination asked for, the file read again in  */
    /* another format, a right-click change made, a dialog's answer applied,  */
    /* the close's questions asked or answered, or the close asked again.     */
    /* The queue calls it once per item and never inside another, so a        */
    /* second click while one runs is never run inside it.                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iItem   : the item, a Jsn_enmItem value.                           */
    /*     objArgs : the record the menu item held: Reopen As's format (a     */
    /*               boxed int), Add Child's JCA_clsContextChoice, the other  */
    /*               right-click items' path (a List<int>); the Colors        */
    /*               dialog or the close question's form that closed; the     */
    /*               worker's close question's display for AskClose; a        */
    /*               preset's size (a boxed float) for FontPreset; null       */
    /*               otherwise.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item's work done, or nothing for an unknown item.       */
    /*------------------------------------------------------------------------*/
    private void Jsn_RunItem(int iItem, object objArgs)
    {
        JCA_clsContextChoice objChoice;

        switch ((Jsn_enmItem)iItem)
        {   /* One case per item: its own function. */
            case Jsn_enmItem.Find:
                /* Find only (false: no Replace controls). */
                Jsn_SearchShowFind(false);
                break;
            case Jsn_enmItem.FindReplace:
                /* Find + Replace (true). */
                Jsn_SearchShowFind(true);
                break;
            case Jsn_enmItem.Colors:
                /* The Colors dialog, editor-modal. */
                Jsn_ViewShowColors();
                break;
            case Jsn_enmItem.SaveAsImage:
                /* An image destination asked for (false: not Windows). */
                Jsn_RequestSaveAs(false);
                break;
            case Jsn_enmItem.SaveAsWindows:
                /* A Windows destination asked for. */
                Jsn_RequestSaveAs(true);
                break;
            case Jsn_enmItem.ReopenAs:
                /* The file read again in the format the item held. */
                Jsn_FileReopenAs((int)objArgs);
                break;
            case Jsn_enmItem.AddChild:
                /* What to add and where, as the menu entry held it. */
                objChoice = (JCA_clsContextChoice)objArgs;
                Jsn_ContextAddChild(objChoice.JCA_lstPath, objChoice.JCA_iKind);
                break;
            case Jsn_enmItem.Rename:
                /* The member's key renamed. */
                Jsn_ContextRenameKey((List<int>)objArgs);
                break;
            case Jsn_enmItem.Copy:
                /* The entry copied to the editor's clipboard. */
                Jsn_ContextCopy((List<int>)objArgs);
                break;
            case Jsn_enmItem.Paste:
                /* The copy pasted into the container. */
                Jsn_ContextPaste((List<int>)objArgs);
                break;
            case Jsn_enmItem.Delete:
                /* The entry removed. */
                Jsn_ContextDelete((List<int>)objArgs);
                break;
            case Jsn_enmItem.CloseAgain:
                /* The close asked again: Close() raises FormClosing, whose     */
                /* handler posts the close's questions once more while the      */
                /* document is still unsaved -- or, saved or discarded          */
                /* meanwhile, simply lets it close.  A window already gone is   */
                /* left alone: there is nothing to ask.                         */
                if (!IsDisposed)
                {   /* Still open: closed, or asked about, as any close is. */
                    Close();
                }
                break;
            case Jsn_enmItem.ColorsAnswer:
                /* The Colors dialog's outcome: the window enabled, then   */
                /* the colours applied.                                    */
                Jsn_ViewColorsAnswer(objArgs as VCO_clsColorsForm);
                break;
            case Jsn_enmItem.AskClose:
                /* The panel's question, then the close question, shown   */
                /* editor-modal.                                          */
                Jsn_ShowCloseQuestion(objArgs as PRS_clsPresentation);
                break;
            case Jsn_enmItem.CloseAnswer:
                /* The close question's answer: the window enabled, then   */
                /* the choice acted on.                                    */
                Jsn_ApplyCloseAnswer(objArgs as Form);
                break;
            case Jsn_enmItem.ContextMenu:
                /* The tree's right-click menu: settled, selected, built and   */
                /* shown.                                                      */
                Jsn_ShowContextMenu(objArgs);
                break;
            case Jsn_enmItem.InlineEdit:
                /* F2 or a double click: the entry found again by its path   */
                /* and edited in place.                                      */
                Jsn_RunInlineEdit(objArgs);
                break;
            case Jsn_enmItem.Save:
                /* File > Save, if a save may start now. */
                Jsn_RunSave();
                break;
            case Jsn_enmItem.Close:
                /* File > Close: the close, as the title bar's close box would   */
                /* ask for it.  A window already gone is left alone.             */
                if (!IsDisposed)
                {   /* Still open: closed, or asked about, as any close is. */
                    Close();
                }
                break;
            case Jsn_enmItem.Undo:
                /* One step back: the panel, a text box or the document. */
                Jsn_EditUndo();
                break;
            case Jsn_enmItem.Redo:
                /* One step forward. */
                Jsn_EditRedo();
                break;
            case Jsn_enmItem.FontLarger:
                /* Up one preset (1: larger). */
                Jsn_ViewStepFontSize(1);
                break;
            case Jsn_enmItem.FontSmaller:
                /* Down one preset (-1: smaller). */
                Jsn_ViewStepFontSize(-1);
                break;
            case Jsn_enmItem.FontDefault:
                /* The framework's default control font's size. */
                Jsn_ViewApplyFontSize(Control.DefaultFont.SizeInPoints);
                break;
            case Jsn_enmItem.FontPreset:
                /* The preset's size, as its item held it. */
                Jsn_ViewApplyFontSize((float)objArgs);
                break;
            case Jsn_enmItem.ShowRaw:
                /* Show Raw Text in Tree switched. */
                Jsn_ViewToggleShowRaw();
                break;
            case Jsn_enmItem.ColorKeys:
                /* Color Keys switched. */
                Jsn_ViewToggleColorKeys();
                break;
            case Jsn_enmItem.Discard:
                /* What was typed thrown away; the panel reloaded. */
                Jsn_EditDiscard();
                break;
            default:
                /* No such item (a guard: only the handlers above post):   */
                /* nothing done.                                           */
                break;
        }
    }

    /*------------------------------------------------------------------------*/
    /* Jsn_FocusAddedEntry:                                                   */
    /*                                                                        */
    /* After Add Child, the keyboard put where the new entry needs editing:   */
    /* the Key box for a new member (its placeholder name, selected, to type  */
    /* over), the Value box otherwise.  Posted by Jsn_ContextAddChild, so it  */
    /* runs after the right-click menu has finished closing -- done at once,  */
    /* the closing menu would hand the focus back to the tree.                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : a box has the focus, its text selected, or nothing.         */
    /*------------------------------------------------------------------------*/
    private void Jsn_FocusAddedEntry()
    {
        if (IsDisposed)
        {   /* The window closed before the post ran: nothing to focus. */
            return;
        }

        if (Jsn_txtKey.Enabled)
        {   /* A member (only a member's Key box is enabled): its key,   */
            /* selected, so typing replaces the placeholder name.        */
            Jsn_txtKey.Focus();
            Jsn_txtKey.SelectAll();
        }
        else if (Jsn_txtValue.Visible && Jsn_txtValue.Enabled)
        {   /* An element or a comment with a value to type (not a     */
            /* boolean, which shows the checkbox, nor a kind with no   */
            /* text): its value, selected.                             */
            Jsn_txtValue.Focus();
            Jsn_txtValue.SelectAll();
        }
    }


}

/*----------------------------------------------------------------------------*/
/* FRF_clsFindReplaceForm: the Find / Find + Replace window                   */
/*----------------------------------------------------------------------------*/

/*----------------------------------------------------------------------------*/
/* FRF_clsFindReplaceForm                                                     */
/*                                                                            */
/* A small modeless tool window.  It holds no search logic of its own:        */
/* every button calls the editor window's search functions                    */
/* (JSN_SearchFindNext, JSN_SearchReplaceCurrent, JSN_SearchReplaceAll),      */
/* which work on the document and the tree.  The same window serves Find      */
/* (replace controls hidden) and Find + Replace; FRF_SetMode switches         */
/* between them.                                                              */
/*                                                                            */
/* Derives from Form because the framework requires it: WinForms makes a      */
/* window only as a Form subclass.  That is the one kind of inheritance       */
/* allowed here -- from a framework base class, never from a class of ours.   */
/*----------------------------------------------------------------------------*/
internal class FRF_clsFindReplaceForm : Form
{
    /*----------------------------------------------------------------------*/
    /* Spacing, pixels, and the narrowest text box, in characters.          */
    /* Everything else is measured from the fonts and captions.             */
    /*----------------------------------------------------------------------*/

    const int FRF_PAD = 12;                              // space round the content
    const int FRF_GAP = 6;                               // space between rows and between buttons
    const int FRF_MINCHARS = 36;                         // text boxes are at least this many average characters wide

    /* The window's own commands, as its queue (Frf_objQueue) carries them:   */
    /* each button's handler posts one and Frf_RunItem runs it --             */
    /* documentation table.                                                   */
    private enum Frf_enmItem
    {
        FindNext = 1,       // Find Next (and Enter): the next match selected (Frf_FindNext)
        Replace = 2,        // Replace: the current match replaced, then the next found (Frf_Replace)
        ReplaceAll = 3      // Replace All: every match replaced as one undo step (Frf_ReplaceAll)
    }

    JSN_clsJsonEditForm Frf_frmEditor;  // editor whose document is searched
    WQU_clsWindowQueue Frf_objQueue;    // the window's own command queue (WindowQueue.cs): the three search buttons post to it, and it runs each through Frf_RunItem, one at a time
    Label Frf_lblFind;                  // "Find:" caption
    TextBox Frf_txtFind;                // text to look for
    TextBox Frf_txtReplace;             // replacement text
    Label Frf_lblReplace;               // "Replace:" caption, hidden in Find mode
    CheckBox Frf_chkMatchCase;          // exact-case matching
    CheckBox Frf_chkSearchKeys;         // look in object member keys
    CheckBox Frf_chkSearchValues;       // look in plain values and comments
    Button Frf_btnFindNext;             // select the next match
    Button Frf_btnReplace;              // replace in the current match, then find the next
    Button Frf_btnReplaceAll;           // replace everywhere as one undo step
    Button Frf_btnClose;                // close the window

    /*------------------------------------------------------------------------*/
    /* FRF_clsFindReplaceForm:                                                */
    /*                                                                        */
    /* Builds the window, in the layout asked for: the Find and Replace       */
    /* boxes, the three search options and the four buttons, sized and        */
    /* placed from measured content rather than fixed positions.              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     frmEditor : the editor to search.                                  */
    /*     fntDialog   : the program's dialog font, which the editor was      */
    /*                   given (the caller's; not freed here).                */
    /*     bShowReplace : true to open in Find + Replace layout.              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     FRF_clsFindReplaceForm : the window, not yet shown.                */
    /*------------------------------------------------------------------------*/
    public FRF_clsFindReplaceForm(JSN_clsJsonEditForm frmEditor, Font fntDialog, bool bShowReplace)
    {
        /* The editor whose SEARCH_ functions every button calls.  It is   */
        /* kept here, not made the window's Owner: an owned window would   */
        /* be hidden and closed with the editor, and this one is closed    */
        /* by the editor's OnFormClosing instead.                          */
        Frf_frmEditor = frmEditor;

        /* The window's own queue: the search buttons only post to it, and   */
        /* it runs each search after the click has returned, one at a        */
        /* time, so no search, busy window or message box runs inside a      */
        /* button's click.                                                   */
        Frf_objQueue = new WQU_clsWindowQueue(this, Frf_RunItem);

        /* The caller's dialog font, set before any control is made (the   */
        /* text boxes' widths are measured from it) -- WinForms'           */
        /* inherited default, which the window would otherwise have, is    */
        /* smaller.                                                        */
        Font = fntDialog;

        /* A small fixed tool window (a thin title bar, no minimize or     */
        /* maximize), centred on the screen -- it has no Owner to centre   */
        /* on.  The title is set again by FRF_SetMode below.  KeyPreview   */
        /* lets OnKeyDown see Escape whichever control has the focus.      */
        Text = "Find";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        MinimizeBox = false;
        MaximizeBox = false;
        KeyPreview = true;

        /* Find row. */
        Frf_lblFind = new Label();
        Frf_lblFind.Text = "Find:";
        Frf_lblFind.AutoSize = true;
        Controls.Add(Frf_lblFind);

        /* The text to look for (its width set by Frf_Layout). */
        Frf_txtFind = new TextBox();
        Controls.Add(Frf_txtFind);

        /* Replace row (hidden in Find mode). */
        Frf_lblReplace = new Label();
        Frf_lblReplace.Text = "Replace:";
        Frf_lblReplace.AutoSize = true;
        Controls.Add(Frf_lblReplace);

        /* The replacement text. */
        Frf_txtReplace = new TextBox();
        Controls.Add(Frf_txtReplace);

        /* Options: Match case on its own row, off by default. */
        Frf_chkMatchCase = new CheckBox();
        Frf_chkMatchCase.Text = "Match case";
        Frf_chkMatchCase.AutoSize = true;
        Controls.Add(Frf_chkMatchCase);

        /* Then what to search, side by side, both on by default. */
        Frf_chkSearchKeys = new CheckBox();
        Frf_chkSearchKeys.Text = "Search keys";
        Frf_chkSearchKeys.AutoSize = true;
        Frf_chkSearchKeys.Checked = true;
        Controls.Add(Frf_chkSearchKeys);

        /* Search values: plain values and comments alike. */
        Frf_chkSearchValues = new CheckBox();
        Frf_chkSearchValues.Text = "Search values";
        Frf_chkSearchValues.AutoSize = true;
        Frf_chkSearchValues.Checked = true;
        Controls.Add(Frf_chkSearchValues);

        /* Buttons, one row, left to right.  Find Next first: it is   */
        /* the Enter key's button.                                    */
        Frf_btnFindNext = new Button();
        Frf_btnFindNext.Text = "Find Next";
        Frf_btnFindNext.Click += Frf_OnFindNextClick;
        Controls.Add(Frf_btnFindNext);

        /* Replace and Replace All, hidden in Find-only mode. */
        Frf_btnReplace = new Button();
        Frf_btnReplace.Text = "Replace";
        Frf_btnReplace.Click += Frf_OnReplaceClick;
        Controls.Add(Frf_btnReplace);

        /* Replace All: everywhere at once, as one undo step. */
        Frf_btnReplaceAll = new Button();
        Frf_btnReplaceAll.Text = "Replace All";
        Frf_btnReplaceAll.Click += Frf_OnReplaceAllClick;
        Controls.Add(Frf_btnReplaceAll);

        /* Close, last: it is the Escape key's button. */
        Frf_btnClose = new Button();
        Frf_btnClose.Text = "Close";
        Frf_btnClose.Click += Frf_OnCloseClick;
        Controls.Add(Frf_btnClose);

        /* Enter presses Find Next, Escape Close. */
        AcceptButton = Frf_btnFindNext;
        CancelButton = Frf_btnClose;

        /* Size to the content, place everything, and place it again     */
        /* whenever the size changes.  Resize is hooked only now, with   */
        /* every control made, so no resize reaches Frf_Layout before.   */
        ClientSize = Frf_PreferredClientSize();
        Frf_Layout();
        Resize += Frf_OnResize;

        /* The layout asked for: the title, and the Replace controls   */
        /* shown or hidden.                                            */
        FRF_SetMode(bShowReplace);
    }

    /*------------------------------------------------------------------------*/
    /* Frf_ButtonSize:                                                        */
    /*                                                                        */
    /* The one size all four buttons share, from the longest caption          */
    /* (JSN_clsJsonEditForm.JSN_DlgButtonSize).                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Size : the size of each button.                                    */
    /*------------------------------------------------------------------------*/
    Size Frf_ButtonSize()
    {
        /* Measured over all four captions. */
        return(JSN_clsJsonEditForm.JSN_DlgButtonSize(new Button[]
        {
            Frf_btnFindNext, Frf_btnReplace, Frf_btnReplaceAll, Frf_btnClose
        }));
    }

    /*------------------------------------------------------------------------*/
    /* Frf_PreferredClientSize:                                               */
    /*                                                                        */
    /* The client size the window needs, from measured sizes.  The Replace    */
    /* row is counted in Find mode too, so switching modes never resizes the  */
    /* window.                                                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Size : the client size to give the window.                         */
    /*------------------------------------------------------------------------*/
    Size Frf_PreferredClientSize()
    {
        Size szButton;                                   // size of each button
        int iCaptionWidth;                               // width of the caption column
        int iFieldWidth;                                 // narrowest text box
        int iOptionsWidth;                               // the two search options side by side
        int iWidth;                                      // content width
        int iHeight;                                     // content height

        /* The measured pieces: the buttons' shared size, the caption       */
        /* column (the wider of "Find:" and "Replace:"), the narrowest      */
        /* text box (FRF_MINCHARS characters), and the two search options   */
        /* side by side with the FRF_PAD * 2 gap Frf_Layout puts between.   */
        szButton = Frf_ButtonSize();
        iCaptionWidth = Math.Max(Frf_lblFind.PreferredSize.Width,
            Frf_lblReplace.PreferredSize.Width);
        iFieldWidth = TextRenderer.MeasureText(new string('x', FRF_MINCHARS),
            Frf_txtFind.Font).Width;
        iOptionsWidth = Frf_chkSearchKeys.PreferredSize.Width + FRF_PAD * 2
            + Frf_chkSearchValues.PreferredSize.Width;

        /* The widest row decides the width: the four buttons with their   */
        /* gaps, or the caption column and the wider of a text box and     */
        /* the options.                                                    */
        iWidth = Math.Max(szButton.Width * 4 + FRF_GAP * 3,
            iCaptionWidth + FRF_GAP + Math.Max(iFieldWidth, iOptionsWidth));

        /* Two text rows, the two option rows, and the buttons: FRF_GAP   */
        /* between the rows, FRF_PAD above the buttons -- the steps       */
        /* Frf_Layout places them by.                                     */
        iHeight = Frf_txtFind.Height + FRF_GAP
            + Frf_txtReplace.Height + FRF_GAP
            + Frf_chkMatchCase.PreferredSize.Height + FRF_GAP
            + Frf_chkSearchKeys.PreferredSize.Height + FRF_PAD
            + szButton.Height;

        /* Content plus padding all round. */
        return(new Size(iWidth + FRF_PAD * 2, iHeight + FRF_PAD * 2));
    }

    /*----------------------------------------------------------------------*/
    /* Frf_Layout:                                                          */
    /*                                                                      */
    /* Places every control from the measured sizes and the window's        */
    /* current width; run once the window is built and on every resize.     */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     None.                                                            */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : the controls are moved and sized.                         */
    /*----------------------------------------------------------------------*/
    void Frf_Layout()
    {
        Size szButton;                                   // size of each button
        int iFieldLeft;                                  // x of the text boxes and options
        int iFieldWidth;                                 // width of the text boxes
        int y;                                           // top of the row being placed

        if (Frf_btnClose == null)
        {   /* No controls yet -- a guard: the constructor hooks Resize     */
            /* only after every control is made and calls this itself       */
            /* last, so no resize reaches here before the controls exist.   */
            return;
        }

        /* The buttons' shared size; the text boxes and options start     */
        /* right of the wider caption, and the boxes stretch to the       */
        /* right edge -- never narrower than 1 pixel, should the window   */
        /* be squeezed.                                                   */
        szButton = Frf_ButtonSize();
        iFieldLeft = FRF_PAD + Math.Max(Frf_lblFind.PreferredSize.Width,
            Frf_lblReplace.PreferredSize.Width) + FRF_GAP;
        iFieldWidth = Math.Max(1, ClientSize.Width - iFieldLeft - FRF_PAD);

        /* Find row: the box, and its caption centred on it. */
        y = FRF_PAD;
        Frf_txtFind.SetBounds(iFieldLeft, y, iFieldWidth, Frf_txtFind.Height);
        Frf_lblFind.Location = new Point(FRF_PAD,
            y + (Frf_txtFind.Height - Frf_lblFind.PreferredSize.Height) / 2);
        y += Frf_txtFind.Height + FRF_GAP;

        /* Replace row, the same way; placed in Find mode too, hidden. */
        Frf_txtReplace.SetBounds(iFieldLeft, y, iFieldWidth, Frf_txtReplace.Height);
        Frf_lblReplace.Location = new Point(FRF_PAD,
            y + (Frf_txtReplace.Height - Frf_lblReplace.PreferredSize.Height) / 2);
        y += Frf_txtReplace.Height + FRF_GAP;

        /* Match case. */
        Frf_chkMatchCase.Location = new Point(iFieldLeft, y);
        y += Frf_chkMatchCase.PreferredSize.Height + FRF_GAP;

        /* Search keys, then Search values beside it. */
        Frf_chkSearchKeys.Location = new Point(iFieldLeft, y);
        Frf_chkSearchValues.Location = new Point(
            iFieldLeft + Frf_chkSearchKeys.PreferredSize.Width + FRF_PAD * 2, y);
        y += Frf_chkSearchKeys.PreferredSize.Height + FRF_PAD;

        /* Buttons, left to right, one size, FRF_GAP apart.  In Find mode   */
        /* the two Replace buttons keep their places, hidden.               */
        Frf_btnFindNext.SetBounds(FRF_PAD, y, szButton.Width, szButton.Height);
        Frf_btnReplace.SetBounds(Frf_btnFindNext.Right + FRF_GAP, y,
            szButton.Width, szButton.Height);
        Frf_btnReplaceAll.SetBounds(Frf_btnReplace.Right + FRF_GAP, y,
            szButton.Width, szButton.Height);
        Frf_btnClose.SetBounds(Frf_btnReplaceAll.Right + FRF_GAP, y,
            szButton.Width, szButton.Height);
    }

    /*------------------------------------------------------------------------*/
    /* FRF_SetMode:                                                           */
    /*                                                                        */
    /* Switches between Find (replace controls hidden) and Find + Replace,    */
    /* without moving or resizing anything: the hidden controls keep their    */
    /* places.  Public: the editor calls it to reuse the open window.         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bShowReplace : true for Find + Replace.                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the title and the replace controls are updated.             */
    /*------------------------------------------------------------------------*/
    public void FRF_SetMode(bool bShowReplace)
    {
        if (bShowReplace)
        {   /* Find + Replace. */
            Text = "Find + Replace";
        }
        else
        {   /* Find only. */
            Text = "Find";
        }

        /* The Replace row and the two Replace buttons shown or hidden. */
        Frf_lblReplace.Visible = bShowReplace;
        Frf_txtReplace.Visible = bShowReplace;
        Frf_btnReplace.Visible = bShowReplace;
        Frf_btnReplaceAll.Visible = bShowReplace;
    }

    /*------------------------------------------------------------------------*/
    /* Frf_FindNext:                                                          */
    /*                                                                        */
    /* Find Next: asks the editor for the next match, and says so when        */
    /* there is none.  Nothing is said when the user cancelled at the         */
    /* editor's unsaved-panel question.                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the editor's selection may move.                            */
    /*------------------------------------------------------------------------*/
    void Frf_FindNext()
    {
        int iResult;                                      // 1 found, 0 none, negative = cancelled

        /* The editor searches its document with this window's settings      */
        /* and selects any match itself.  (Not caught: if the search's       */
        /* work throws, Jsn_ProgRun's exception goes on to the framework.)   */
        iResult = Frf_frmEditor.JSN_SearchFindNext(Frf_txtFind.Text,
            Frf_chkMatchCase.Checked,
            Frf_chkSearchKeys.Checked,
            Frf_chkSearchValues.Checked);

        if (iResult == 0)
        {   /* Searched everything and found nothing: said in the program's   */
            /* own message box, over this window, in its font.  A match       */
            /* (1) needs no word -- the selection moved -- and a cancel       */
            /* (negative) was the user's own choice.                          */
            MSG_clsMessageForm.MSG_Show(this, this.Font, "No match found.", JSN_clsJsonEditForm.JSN_CAPTION,
                MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Frf_Replace:                                                           */
    /*                                                                        */
    /* Replace: replaces in the selected entry (if it matches), then moves    */
    /* on to the next match.  On an entry that does not match, the first      */
    /* click just finds the next match and the second replaces it.  A Cancel  */
    /* at the editor's unsaved-panel question ends the whole command there.   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the document may change and the selection moves.            */
    /*------------------------------------------------------------------------*/
    void Frf_Replace()
    {
        int iReplaced;                                   // the replace's answer: 1 replaced, 0 not, negative cancelled

        /* The replacement in the selected entry, if it matches. */
        iReplaced = Frf_frmEditor.JSN_SearchReplaceCurrent(Frf_txtFind.Text, Frf_txtReplace.Text,
            Frf_chkMatchCase.Checked, Frf_chkSearchKeys.Checked,
            Frf_chkSearchValues.Checked);

        if (iReplaced < 0)
        {   /* The user cancelled at the unsaved-panel question: the       */
            /* Replace is over, so no Find Next -- it would ask the same   */
            /* question again at once and, answered, move the selection    */
            /* the user chose to keep.                                     */
            return;
        }

        /* On to the next match, whether or not this entry was replaced:   */
        /* a non-matching entry's first click is how the next match is     */
        /* found.                                                          */
        Frf_FindNext();
    }

    /*------------------------------------------------------------------------*/
    /* Frf_ReplaceAll:                                                        */
    /*                                                                        */
    /* Replace All: replaces everywhere and reports how many were             */
    /* replaced, and how many matches were skipped and why.                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the document may change (one undo step).                    */
    /*------------------------------------------------------------------------*/
    void Frf_ReplaceAll()
    {
        int iCount;                                       // replacements made
        int iSkipped;                                     // matches refused
        string sMessage;                                  // report shown afterwards

        /* The editor replaces throughout its document as one undo step.      */
        /* (Not caught: if the work throws, Jsn_ProgRun's exception goes on   */
        /* to the framework, the document part-changed with no undo step      */
        /* -- JSN_SearchReplaceAll's comment has it.)                         */
        iCount = Frf_frmEditor.JSN_SearchReplaceAll(Frf_txtFind.Text,
            Frf_txtReplace.Text, Frf_chkMatchCase.Checked,
            Frf_chkSearchKeys.Checked, Frf_chkSearchValues.Checked, out iSkipped);
        if (iCount < 0)
        {   /* Cancelled at the editor's unsaved-panel question: no report. */
            return;
        }

        /* The report: how many were made (0 included, so the user knows   */
        /* the search ran), then why any were skipped.                     */
        sMessage = iCount.ToString() + " replacement(s) made.";
        if (iSkipped > 0)
        {   /* Some matches were refused.  The document counts them         */
            /* without saying which rule refused each, and the rules are    */
            /* the format's, so the reason is given in general terms with   */
            /* the common cases as examples, as in                          */
            /* JSN_SearchReplaceCurrent's note.                             */
            sMessage += "\r\n\r\n" + iSkipped.ToString() + " match(es) "
                + "skipped: the file's format would not accept the "
                + "result -- for example a value of the wrong kind, "
                + "text the file can't hold as typed, a key it refuses "
                + "or one already used in its object, or a comment that "
                + "can't hold the text.";
        }

        /* Shown in the program's own message box, over this window. */
        MSG_clsMessageForm.MSG_Show(this, this.Font, sMessage, JSN_clsJsonEditForm.JSN_CAPTION, MessageBoxButtons.OK,
            MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
    }

    /*------------------------------------------------------------------------*/
    /* OnKeyDown:                                                             */
    /*                                                                        */
    /* The Framework's key hook for the Find window: hands every key to       */
    /* Frf_OnKeyDown below, which closes on Escape and then makes the base    */
    /* call itself, so the base event runs once.                              */
    /*                                                                        */
    /* An override because the framework requires it: a Form receives its     */
    /* own key events first only through this virtual method.                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     evKey : the key pressed; the helper may mark it handled.           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the window may close.                                       */
    /*------------------------------------------------------------------------*/
    protected override void OnKeyDown(KeyEventArgs evKey)
    {
        /* Escape's close and the base call are in the helper. */
        Frf_OnKeyDown(evKey);
    }

    /*------------------------------------------------------------------------*/
    /* Frf_OnKeyDown:                                                         */
    /*                                                                        */
    /* Escape closes the window from any control, including when focus        */
    /* is in a text box (KeyPreview routes keys here first).  CancelButton    */
    /* is the Close button as well; why both is not recorded.                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     evArgs : the key pressed.                                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the window may close.                                       */
    /*------------------------------------------------------------------------*/
    private void Frf_OnKeyDown(KeyEventArgs evArgs)
    {
        if (evArgs.KeyCode == Keys.Escape)
        {   /* Escape: the window closed, and the key marked handled so   */
            /* the focused control does not act on it too.                */
            Close();
            evArgs.Handled = true;
        }

        /* The base class, which raises the KeyDown event for any other   */
        /* handler (Escape included, already marked handled).             */
        base.OnKeyDown(evArgs);
    }
    /*------------------------------------------------------------------------*/
    /* Frf_OnFindNextClick:                                                   */
    /*                                                                        */
    /* The Find Next button (and Enter, the window's AcceptButton): the       */
    /* FindNext item posted to the window's queue, which runs the search      */
    /* through the editor (Frf_FindNext).                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the button (unused).                                   */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item posted (or dropped as one already in hand).        */
    /*------------------------------------------------------------------------*/
    private void Frf_OnFindNextClick(object objSender, EventArgs evArgs)
    {
        /* Posted: the search runs from the queue (Frf_RunItem). */
        Frf_objQueue.WQU_Post((int)Frf_enmItem.FindNext, null);
    }

    /*------------------------------------------------------------------------*/
    /* Frf_OnReplaceClick:                                                    */
    /*                                                                        */
    /* The Replace button: the Replace item posted; the queue replaces in the */
    /* selected entry, then finds the next match (Frf_Replace).               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the button (unused).                                   */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item posted (or dropped as one already in hand).        */
    /*------------------------------------------------------------------------*/
    private void Frf_OnReplaceClick(object objSender, EventArgs evArgs)
    {
        /* Posted: the replace runs from the queue (Frf_RunItem). */
        Frf_objQueue.WQU_Post((int)Frf_enmItem.Replace, null);
    }

    /*------------------------------------------------------------------------*/
    /* Frf_OnReplaceAllClick:                                                 */
    /*                                                                        */
    /* The Replace All button: the ReplaceAll item posted; the queue replaces */
    /* everywhere and reports how many (Frf_ReplaceAll).                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the button (unused).                                   */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item posted (or dropped as one already in hand).        */
    /*------------------------------------------------------------------------*/
    private void Frf_OnReplaceAllClick(object objSender, EventArgs evArgs)
    {
        /* Posted: the replace runs from the queue (Frf_RunItem). */
        Frf_objQueue.WQU_Post((int)Frf_enmItem.ReplaceAll, null);
    }

    /*------------------------------------------------------------------------*/
    /* Frf_RunItem:                                                           */
    /*                                                                        */
    /* The window's own queue's runner (WindowQueue.cs): one item a button    */
    /* posted, run to its end -- the search or the replacement made through   */
    /* the editor, under its busy window, with any message box it shows.  The */
    /* queue calls it once per item and never inside another.                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iItem   : the item, a Frf_enmItem value.                           */
    /*     objArgs : unused by every item.                                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item's work done, or nothing for an unknown item.       */
    /*------------------------------------------------------------------------*/
    private void Frf_RunItem(int iItem, object objArgs)
    {
        switch ((Frf_enmItem)iItem)
        {   /* One case per item: its own function. */
            case Frf_enmItem.FindNext:
                /* The search, through the editor. */
                Frf_FindNext();
                break;
            case Frf_enmItem.Replace:
                /* The replace, then the next match. */
                Frf_Replace();
                break;
            case Frf_enmItem.ReplaceAll:
                /* Everywhere at once. */
                Frf_ReplaceAll();
                break;
            default:
                /* No such item (a guard: only the three handlers above   */
                /* post): nothing done.                                   */
                break;
        }
    }

    /*------------------------------------------------------------------------*/
    /* Frf_OnCloseClick:                                                      */
    /*                                                                        */
    /* The Close button (and Escape, the window's CancelButton): the window   */
    /* closes; the editor is left as it is.                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the button (unused).                                   */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the window is closing.                                      */
    /*------------------------------------------------------------------------*/
    private void Frf_OnCloseClick(object objSender, EventArgs evArgs)
    {
        /* This window only. */
        Close();
    }

    /*------------------------------------------------------------------------*/
    /* Frf_OnResize:                                                          */
    /*                                                                        */
    /* The window's size changed: its controls placed again (Frf_Layout).     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the window (unused).                                   */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the controls are moved and sized.                           */
    /*------------------------------------------------------------------------*/
    private void Frf_OnResize(object objSender, EventArgs evArgs)
    {
        /* The layout, from the new size. */
        Frf_Layout();
    }


}

/*----------------------------------------------------------------------------*/
/* VCO_clsColorsForm: the Colors dialog (View > Colors...)                    */
/*----------------------------------------------------------------------------*/

/*----------------------------------------------------------------------------*/
/* VCO_clsColorsForm                                                          */
/*                                                                            */
/* The Colors dialog: a white header band with a bold title and a line of     */
/* explanation; a bold "Names" heading over the three name roles and a bold   */
/* "Values" heading over the five value types, each row a swatch button       */
/* showing the role's colour (a click opens the colour picker), what the role */
/* colours, and a sample ("[section]", "42" ...) drawn in that colour on the  */
/* tree's background, so a choice shows before OK; a note beside the          */
/* standard information icon; then Reset (every role back to its default) at  */
/* the left and OK, Cancel at the right.  It works on its own copies of the   */
/* colours; the editor takes them (VCO_ReadNameColors, VCO_ReadValueColors)   */
/* only when VCO_ReadSaved says OK was pressed -- the dialog's answer is that */
/* flag, not its DialogResult.  Every action is a public method the buttons   */
/* call (VCO_PickRow, VCO_Reset, VCO_Accept, VCO_Cancel), so the dialog can   */
/* be worked without being shown; nothing outside this file does so.          */
/*                                                                            */
/* Derives from Form because the framework requires it: WinForms makes a      */
/* window only as a Form subclass.  That is the one kind of inheritance       */
/* allowed here -- from a framework base class, never from a class of ours.   */
/*----------------------------------------------------------------------------*/
internal class VCO_clsColorsForm : Form
{
    /*------------------------------------------------------------------------*/
    /* The rows, in the tree's role order: VoorheesTreeView.cs's VTV_NAME_    */
    /* (the first VTV_NAMEROLECOUNT rows), then VoorheesFormat.cs's VF_ROLE_  */
    /* -- a role added or moved there must be added or moved in all three     */
    /* tables (VCO_Create refuses to build otherwise).  Each row's key names  */
    /* the role to the colour picker (VCO_PickColor's sRole) and to           */
    /* VCO_RowOf; its text is what the dialog says.                           */
    /*------------------------------------------------------------------------*/

    private static readonly string[] Vco_asRoleKeys = new string[]
    {
        "top",                                           // VTV_NAME_TOP
        "nested",                                        // VTV_NAME_NESTED
        "entry",                                         // VTV_NAME_ENTRY
        "text",                                          // VF_ROLE_TEXT
        "number",                                        // VF_ROLE_NUMBER
        "boolean",                                       // VF_ROLE_BOOLEAN
        "null",                                          // VF_ROLE_NULL
        "binary"                                         // VF_ROLE_BINARY
    };

    static readonly string[] Vco_asRowTexts = new string[]
    {
        "Top level: [section], [registry key], JSON's root",                         // VTV_NAME_TOP
        "Nested: objects and arrays inside others, sections in an auto-saved block", // VTV_NAME_NESTED
        "Entries: the keys and indexes of plain values",                             // VTV_NAME_ENTRY
        "Text: strings, INI and Klipper values, registry strings",                   // VF_ROLE_TEXT
        "Numbers: JSON numbers, registry DWORD and QWORD",                           // VF_ROLE_NUMBER
        "Booleans: true and false",                                                  // VF_ROLE_BOOLEAN
        "No value: null, a registry value to delete",                                // VF_ROLE_NULL
        "Binary: registry hex values"                                                // VF_ROLE_BINARY
    };

    static readonly string[] Vco_asSamples = new string[]
    {
        "[section]",                                     // VTV_NAME_TOP: as a section or key path is labelled
        "{object}",                                      // VTV_NAME_NESTED: as a nested JSON object is labelled
        "key",                                           // VTV_NAME_ENTRY
        "text",                                          // VF_ROLE_TEXT
        "42",                                            // VF_ROLE_NUMBER
        "true",                                          // VF_ROLE_BOOLEAN
        "null",                                          // VF_ROLE_NULL
        "01,02,ff"                                       // VF_ROLE_BINARY: as the tree shows registry bytes
    };

    private Color[] Vco_arrNameColors;   // the name roles' colours as chosen so far (VTV_NAME_ order)
    private Color[] Vco_arrValueColors;  // the value roles' colours as chosen so far (VF_ROLE_ order)
    private bool Vco_bSaved;             // OK was pressed
    VCO_PickColor Vco_pfnPick;           // asks for a colour
    Panel Vco_pnlHeader;                 // the white header band
    Label Vco_lblTitle;                  // its bold title
    Label Vco_lblIntro;                  // its line of explanation
    Label Vco_lblTopLine;                // the etched line under the header
    Label Vco_lblNames;                  // "Names" heading, bold
    Label Vco_lblValues;                 // "Values" heading, bold
    PictureBox Vco_picInfo;              // the note's information icon
    Label Vco_lblNote;                   // when the colours show
    Label Vco_lblBottomLine;             // the etched line above the buttons
    List<Button> Vco_lstSwatches;        // one swatch per row, its colour as its background
    List<Label> Vco_lstLabels;           // one label per row: what the role colours
    List<Label> Vco_lstSamples;          // one sample per row, drawn in the role's colour
    private Font Vco_fntTitle;           // the title's bold, larger font, made here; released when the dialog is disposed (Vco_OnDisposed)
    private Font Vco_fntHeading;         // the group headings' bold font, made here; released likewise
    private Bitmap Vco_bmpInfo;          // the note's small information picture, made here; released likewise
    Button Vco_btnReset;                 // every role to its default
    Button Vco_btnOk;                    // keep the colours
    Button Vco_btnCancel;                // keep nothing

    /*------------------------------------------------------------------------*/
    /* VCO_Create:                                                            */
    /*                                                                        */
    /* Builds the dialog, not shown, on copies of the colours given.  The     */
    /* row tables must match the role counts (a programming error             */
    /* otherwise, thrown at once).                                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     fntDialog : the editor's font (the dialog uses it).                */
    /*     arrNames  : the name roles' colours now (copied).                  */
    /*     arrValues : the value roles' colours now (copied).                 */
    /*     pfnPick : the colour picker.                                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VCO_clsColorsForm : the dialog, laid out.                          */
    /*------------------------------------------------------------------------*/
    public static VCO_clsColorsForm VCO_Create(Font fntDialog, Color[] arrNames,
        Color[] arrValues, VCO_PickColor pfnPick)
    {
        VCO_clsColorsForm frmColors;  // the new dialog
        Button btnSwatch;             // one row's swatch
        Label lblRow;                 // one row's label
        Label lblSample;              // one row's sample
        Font fntTitle;                // the title's font
        Font fntHeading;              // the group headings' font
        Bitmap bmpInfo;               // the information icon, small
        int i;

        if (Vco_asRoleKeys.Length != VTV_clsTreeView.VTV_NAMEROLECOUNT + VoorheesFormat.VF_ROLECOUNT
            || Vco_asRowTexts.Length != Vco_asRoleKeys.Length
            || Vco_asSamples.Length != Vco_asRoleKeys.Length)
        {   /* The row tables are out of step with the roles: a           */
            /* programming error, thrown at once rather than shown as a   */
            /* dialog whose rows colour the wrong things.                 */
            throw new InvalidOperationException("Colors dialog rows do not match the colour roles");
        }

        /* The window: a small fixed dialog centred on the editor, in the   */
        /* editor's font, holding COPIES of the colours -- so Cancel        */
        /* leaves the editor's own untouched -- with OK not yet pressed     */
        /* and the picker to ask.                                           */
        frmColors = new VCO_clsColorsForm();
        frmColors.Text = "Colors";
        frmColors.FormBorderStyle = FormBorderStyle.FixedDialog;
        frmColors.MaximizeBox = false;
        frmColors.MinimizeBox = false;
        frmColors.ShowInTaskbar = false;
        frmColors.StartPosition = FormStartPosition.CenterParent;
        frmColors.Font = fntDialog;
        frmColors.Vco_arrNameColors = (Color[])arrNames.Clone();
        frmColors.Vco_arrValueColors = (Color[])arrValues.Clone();
        frmColors.Vco_bSaved = false;
        frmColors.Vco_pfnPick = pfnPick;

        /* The fonts: the title bold and a third larger, the headings bold;   */
        /* both go when the dialog does.                                      */
        fntTitle = new Font(fntDialog.FontFamily, fntDialog.SizeInPoints * 4f / 3f, FontStyle.Bold);
        fntHeading = new Font(fntDialog, FontStyle.Bold);
        frmColors.Vco_fntTitle = fntTitle;
        frmColors.Vco_fntHeading = fntHeading;
        frmColors.Disposed += frmColors.Vco_OnDisposed;

        /* The header band: white, the title and a line of explanation   */
        /* (children of the band), an etched line under it.              */
        frmColors.Vco_pnlHeader = new Panel();
        frmColors.Vco_pnlHeader.BackColor = SystemColors.Window;
        frmColors.Controls.Add(frmColors.Vco_pnlHeader);
        frmColors.Vco_lblTitle = Vco_NewLabel(frmColors.Vco_pnlHeader, "Tree colors");
        frmColors.Vco_lblTitle.Font = fntTitle;
        frmColors.Vco_lblIntro = Vco_NewLabel(frmColors.Vco_pnlHeader,
            "Names by kind, values by type. Click a swatch to choose its color.");
        frmColors.Vco_lblTopLine = JSN_clsJsonEditForm.JSN_DlgNewEtchedLine(frmColors);

        /* The two group headings. */
        frmColors.Vco_lblNames = Vco_NewLabel(frmColors, "Names");
        frmColors.Vco_lblNames.Font = fntHeading;
        frmColors.Vco_lblValues = Vco_NewLabel(frmColors, "Values");
        frmColors.Vco_lblValues.Font = fntHeading;

        /* One row per role: the swatch (its row number in Tag, for the     */
        /* click), what it colours, and a sample in its colour on the       */
        /* tree's own background, framed like a little piece of the tree.   */
        frmColors.Vco_lstSwatches = new List<Button>();
        frmColors.Vco_lstLabels = new List<Label>();
        frmColors.Vco_lstSamples = new List<Label>();
        for (i = 0; i < Vco_asRoleKeys.Length; i++)
        {   /* One role's row per iteration.                              */
            /* The swatch: an empty flat button whose background IS the   */
            /* colour (flat and without the visual style, so the colour   */
            /* shows -- as found), its row in Tag for the shared click    */
            /* handler, and the row's text as its name for screen         */
            /* readers, since it has no caption.                          */
            btnSwatch = new Button();
            btnSwatch.Tag = i;
            btnSwatch.Text = "";
            btnSwatch.FlatStyle = FlatStyle.Flat;
            btnSwatch.UseVisualStyleBackColor = false;
            btnSwatch.BackColor = frmColors.VCO_RowColor(i);
            btnSwatch.AccessibleName = Vco_asRowTexts[i];
            btnSwatch.Click += frmColors.Vco_OnSwatchClick;
            frmColors.Controls.Add(btnSwatch);
            frmColors.Vco_lstSwatches.Add(btnSwatch);

            /* What the role colours. */
            lblRow = Vco_NewLabel(frmColors, Vco_asRowTexts[i]);
            frmColors.Vco_lstLabels.Add(lblRow);

            /* The sample, in the role's colour on the tree's background   */
            /* (the window colour), framed.                                */
            lblSample = Vco_NewLabel(frmColors, Vco_asSamples[i]);
            lblSample.BackColor = SystemColors.Window;
            lblSample.ForeColor = frmColors.VCO_RowColor(i);
            lblSample.BorderStyle = BorderStyle.FixedSingle;
            lblSample.TextAlign = ContentAlignment.MiddleLeft;
            frmColors.Vco_lstSamples.Add(lblSample);
        }

        /* The note beside the standard information icon (the picture goes   */
        /* when the dialog does).                                            */
        bmpInfo = JSN_clsJsonEditForm.JSN_DlgSmallIconBitmap(SystemIcons.Information);
        frmColors.Vco_bmpInfo = bmpInfo;
        frmColors.Vco_picInfo = new PictureBox();
        frmColors.Vco_picInfo.Image = bmpInfo;
        frmColors.Vco_picInfo.SizeMode = PictureBoxSizeMode.Zoom;
        frmColors.Vco_picInfo.Size = SystemInformation.SmallIconSize;
        frmColors.Controls.Add(frmColors.Vco_picInfo);
        frmColors.Vco_lblNote = Vco_NewLabel(frmColors, "These colors show while View > Color Keys "
            + "is on. Repeated keys stay red and comments stay grey.");
        frmColors.Vco_lblBottomLine = JSN_clsJsonEditForm.JSN_DlgNewEtchedLine(frmColors);

        /* Reset at the left, OK and Cancel at the right. */
        frmColors.Vco_btnReset = Vco_NewButton(frmColors, "Reset");
        frmColors.Vco_btnReset.Click += frmColors.Vco_OnResetClick;
        frmColors.Vco_btnOk = Vco_NewButton(frmColors, "OK");
        frmColors.Vco_btnOk.Click += frmColors.Vco_OnAcceptClick;
        frmColors.Vco_btnCancel = Vco_NewButton(frmColors, "Cancel");
        frmColors.Vco_btnCancel.Click += frmColors.Vco_OnCancelClick;
        frmColors.AcceptButton = frmColors.Vco_btnOk;
        frmColors.CancelButton = frmColors.Vco_btnCancel;

        /* Placed from measured sizes, now and on every resize.  Resize is   */
        /* hooked first, so the first layout's own ClientSize change runs    */
        /* it once more with the same sizes -- harmless, and it then stops   */
        /* (an unchanged size raises no further Resize).                     */
        frmColors.Resize += frmColors.Vco_OnResize;
        frmColors.VCO_Layout();

        /* Built and laid out, not shown: the caller shows it. */
        return(frmColors);
    }

    /*-------------------------------------------------------------------------*/
    /* Vco_NewLabel:                                                           */
    /*                                                                         */
    /* A label of the dialog's, sized and placed by VCO_Layout from its        */
    /* measured size rather than by AutoSize (whose size, as found, is not     */
    /* final until Windows lays the dialog out).                               */
    /*                                                                         */
    /* Arguments:                                                              */
    /*     ctlParent : the control it goes in (the dialog or the header band). */
    /*     sText  : the text.                                                  */
    /*                                                                         */
    /* Returns:                                                                */
    /*     Label : the label, added to its parent.                             */
    /*-------------------------------------------------------------------------*/
    static Label Vco_NewLabel(Control ctlParent, string sText)
    {
        Label lblNew;                                       // the new label

        /* Not auto-sized, holding its text, added to its parent with no   */
        /* size or place yet.                                              */
        lblNew = new Label();
        lblNew.AutoSize = false;
        lblNew.Text = sText;
        ctlParent.Controls.Add(lblNew);

        /* The label, for VCO_Layout to size and place. */
        return(lblNew);
    }

    /*-------------------------------------------------------------------------*/
    /* Vco_NewButton:                                                          */
    /*                                                                         */
    /* A text button of the dialog's (Reset, OK, Cancel), sized by VCO_Layout. */
    /*                                                                         */
    /* Arguments:                                                              */
    /*     frmColors : the dialog.                                             */
    /*     sText : the caption.                                                */
    /*                                                                         */
    /* Returns:                                                                */
    /*     Button : the button, added to the dialog.                           */
    /*-------------------------------------------------------------------------*/
    static Button Vco_NewButton(VCO_clsColorsForm frmColors, string sText)
    {
        Button btnNew;                                      // the new button

        /* Not auto-sized, holding its caption, added to the dialog with   */
        /* no size or place yet (VCO_Layout gives all three one width).    */
        btnNew = new Button();
        btnNew.AutoSize = false;
        btnNew.Text = sText;
        frmColors.Controls.Add(btnNew);

        /* The button, for VCO_Layout to size and place. */
        return(btnNew);
    }

    /*------------------------------------------------------------------------*/
    /* VCO_RowOf:                                                             */
    /*                                                                        */
    /* The row of a role named by its key (Vco_asRoleKeys).  Nothing outside  */
    /* this file calls it.                                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sRole : the key, e.g. "number".                                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the row, or -1 for an unknown name.                          */
    /*------------------------------------------------------------------------*/
    public static int VCO_RowOf(string sRole)
    {
        /* Its place in the table (-1 when absent). */
        return(Array.IndexOf(Vco_asRoleKeys, sRole));
    }

    /*------------------------------------------------------------------------*/
    /* VCO_RowColor:                                                          */
    /*                                                                        */
    /* A row's colour as chosen so far: a name role's from Vco_arrNameColors, */
    /* a value role's from Vco_arrValueColors.                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iRow : the row.                                                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Color : its colour.                                                */
    /*------------------------------------------------------------------------*/
    public Color VCO_RowColor(int iRow)
    {
        if (iRow < VTV_clsTreeView.VTV_NAMEROLECOUNT)
        {   /* A name role. */
            return(Vco_arrNameColors[iRow]);
        }

        /* A value role, after the name roles. */
        return(Vco_arrValueColors[iRow - VTV_clsTreeView.VTV_NAMEROLECOUNT]);
    }

    /*------------------------------------------------------------------------*/
    /* Vco_SetRowColor:                                                       */
    /*                                                                        */
    /* Changes a row's colour: in its table, and on its swatch.               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iRow  : the row.                                                   */
    /*     clrColor : the new colour.                                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the colour is chosen and shown.                             */
    /*------------------------------------------------------------------------*/
    void Vco_SetRowColor(int iRow, Color clrColor)
    {
        if (iRow < VTV_clsTreeView.VTV_NAMEROLECOUNT)
        {   /* A name role. */
            Vco_arrNameColors[iRow] = clrColor;
        }
        else
        {   /* A value role, after the name roles. */
            Vco_arrValueColors[iRow - VTV_clsTreeView.VTV_NAMEROLECOUNT] = clrColor;
        }

        /* The swatch and the sample show it. */
        Vco_lstSwatches[iRow].BackColor = clrColor;
        Vco_lstSamples[iRow].ForeColor = clrColor;
    }

    /*------------------------------------------------------------------------*/
    /* VCO_SwatchColor:                                                       */
    /*                                                                        */
    /* The colour a row's swatch shows -- what the user sees -- so the dialog */
    /* can be checked without being shown.  Nothing outside this file calls   */
    /* it.                                                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iRow : the row.                                                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Color : the swatch's background.                                   */
    /*------------------------------------------------------------------------*/
    public Color VCO_SwatchColor(int iRow)
    {
        /* Its background. */
        return(Vco_lstSwatches[iRow].BackColor);
    }

    /*------------------------------------------------------------------------*/
    /* VCO_SampleColor:                                                       */
    /*                                                                        */
    /* The colour a row's sample is drawn in, which must follow the swatch    */
    /* (Vco_SetRowColor sets both); for checking the dialog without showing   */
    /* it.  Nothing outside this file calls it.                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iRow : the row.                                                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Color : the sample's text colour.                                  */
    /*------------------------------------------------------------------------*/
    public Color VCO_SampleColor(int iRow)
    {
        /* Its text colour. */
        return(Vco_lstSamples[iRow].ForeColor);
    }

    /*------------------------------------------------------------------------*/
    /* Vco_OnSwatchClick:                                                     */
    /*                                                                        */
    /* A swatch was clicked: pick that row's colour.  The row comes from the  */
    /* button's Tag (one handler for every swatch).                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the swatch.                                            */
    /*     evArgs : unused.                                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : see VCO_PickRow.                                            */
    /*------------------------------------------------------------------------*/
    void Vco_OnSwatchClick(object objSender, EventArgs evArgs)
    {
        /* The clicked swatch's row, from its Tag. */
        VCO_PickRow((int)((Button)objSender).Tag);
    }

    /*------------------------------------------------------------------------*/
    /* VCO_PickRow:                                                           */
    /*                                                                        */
    /* Asks the picker for a row's colour, starting at its colour now; a      */
    /* colour picked becomes the row's, Cancel leaves it.                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iRow : the row.                                                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the row's colour may change.                                */
    /*------------------------------------------------------------------------*/
    public void VCO_PickRow(int iRow)
    {
        Color clrPicked;                               // the colour the picker gave

        if (Vco_pfnPick(this, Vco_asRoleKeys[iRow], VCO_RowColor(iRow), out clrPicked))
        {   /* Picked: the row takes it. */
            Vco_SetRowColor(iRow, clrPicked);
        }
    }

    /*------------------------------------------------------------------------*/
    /* VCO_Reset:                                                             */
    /*                                                                        */
    /* Reset: every row back to its role's default, as the tree gives them    */
    /* (VTV_DefaultNameColors / VTV_DefaultValueColors, VoorheesTreeView.cs). */
    /* Nothing is kept until OK.                                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : every row shows its default.                                */
    /*------------------------------------------------------------------------*/
    public void VCO_Reset()
    {
        Color[] arrNames;                                // the name roles' defaults
        Color[] arrValues;                               // the value roles' defaults
        int i;

        /* The defaults, then every row set to its own -- swatch and   */
        /* sample with it (Vco_SetRowColor).                           */
        arrNames = VTV_clsTreeView.VTV_DefaultNameColors();
        arrValues = VTV_clsTreeView.VTV_DefaultValueColors();
        for (i = 0; i < Vco_asRoleKeys.Length; i++)
        {   /* One row per iteration: names first, then values. */
            if (i < VTV_clsTreeView.VTV_NAMEROLECOUNT)
            {   /* A name role. */
                Vco_SetRowColor(i, arrNames[i]);
            }
            else
            {   /* A value role. */
                Vco_SetRowColor(i, arrValues[i - VTV_clsTreeView.VTV_NAMEROLECOUNT]);
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* VCO_Accept:                                                            */
    /*                                                                        */
    /* OK: the colours chosen are to be kept (Vco_bSaved) and the dialog      */
    /* closes.                                                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the dialog is closing.                                      */
    /*------------------------------------------------------------------------*/
    public void VCO_Accept()
    {
        /* Accepted first, then closed: the caller's ShowDialog returns       */
        /* only after the close, by when the flag is set, and it then reads   */
        /* the flag and the colours (VCO_ReadSaved and the two readers).      */
        Vco_bSaved = true;
        Close();
    }

    /*------------------------------------------------------------------------*/
    /* VCO_Cancel:                                                            */
    /*                                                                        */
    /* Cancel: nothing is kept (Vco_bSaved stays false) and the dialog        */
    /* closes.                                                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the dialog is closing.                                      */
    /*------------------------------------------------------------------------*/
    public void VCO_Cancel()
    {
        /* Closed with the flag still false: the editor keeps nothing. */
        Close();
    }

    /*------------------------------------------------------------------------*/
    /* VCO_Layout:                                                            */
    /*                                                                        */
    /* Places everything from measured sizes, the font's height giving the    */
    /* margins and spacing, so the dialog scales with the text size, and      */
    /* sizes the dialog to hold it.  Run when the dialog is built and on      */
    /* every resize.                                                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the controls are placed and the client size set.            */
    /*------------------------------------------------------------------------*/
    public void VCO_Layout()
    {
        int iPad;                                        // margin and spacing, from the font
        int iSwatchW;                                    // a swatch's width
        int iRowH;                                       // a row's (a swatch's, every button's) height
        int iButtonW;                                    // the text buttons' width
        int iDescW;                                      // the description column's width
        int iSampleW;                                    // the sample column's width
        int iContent;                                    // the content's width
        int iNoteX;                                      // where the note's text starts
        int y;                                           // the next row's top
        Label lblHeading;                                // a group heading, or null
        Button btnSwatch;                                // one row's swatch
        Label lblRow;                                    // one row's description
        Label lblSample;                                 // one row's sample
        int i;

        /* The measures, all from the font: the margin and spacing is one   */
        /* font height; a swatch three heights wide and as tall as a        */
        /* button; the three text buttons one width -- the widest           */
        /* caption, never under five font heights (the usual Windows        */
        /* button width at the usual font).                                 */
        iPad = Font.Height;
        iSwatchW = iPad * 3;
        iRowH = Vco_btnOk.PreferredSize.Height;
        iButtonW = Math.Max(iPad * 5, Math.Max(Vco_btnReset.PreferredSize.Width,
            Math.Max(Vco_btnOk.PreferredSize.Width, Vco_btnCancel.PreferredSize.Width)));

        /* The columns, each as wide as its widest entry; the samples get   */
        /* a font height more as padding.                                   */
        iDescW = 0;
        iSampleW = 0;
        for (i = 0; i < Vco_lstSwatches.Count; i++)
        {   /* One row per iteration; the widest so far kept. */
            iDescW = Math.Max(iDescW, Vco_lstLabels[i].PreferredSize.Width);
            iSampleW = Math.Max(iSampleW, Vco_lstSamples[i].PreferredSize.Width);
        }
        iSampleW += iPad;

        /* The content width: the widest of the header's two lines, a   */
        /* row (indent, swatch, description, sample) and the three      */
        /* buttons, never under a floor of 26 font heights.             */
        iContent = iPad * 26;
        iContent = Math.Max(iContent, Vco_lblTitle.PreferredSize.Width);
        iContent = Math.Max(iContent, Vco_lblIntro.PreferredSize.Width);
        iContent = Math.Max(iContent, iPad + iSwatchW + iPad / 2 + iDescW + iPad + iSampleW);
        iContent = Math.Max(iContent, iButtonW * 3 + iPad * 2);

        /* The header band, across the whole dialog, its lines inside it. */
        Vco_lblTitle.Size = Vco_lblTitle.PreferredSize;
        Vco_lblTitle.Location = new Point(iPad, iPad * 3 / 4);
        Vco_lblIntro.Size = Vco_lblIntro.PreferredSize;
        Vco_lblIntro.Location = new Point(iPad, Vco_lblTitle.Bottom + iPad / 4);
        Vco_pnlHeader.SetBounds(0, 0, iContent + iPad * 2, Vco_lblIntro.Bottom + iPad * 3 / 4);
        Vco_lblTopLine.SetBounds(0, Vco_pnlHeader.Bottom, Vco_pnlHeader.Width, 2);
        y = Vco_lblTopLine.Bottom + iPad * 3 / 4;

        /* The rows, top down: names, then values. */
        for (i = 0; i < Vco_lstSwatches.Count; i++)
        {   /* One row per iteration, its group's heading above it when   */
            /* it is the group's first.                                   */
            if (i == 0)
            {   /* The name group starts. */
                lblHeading = Vco_lblNames;
            }
            else if (i == VTV_clsTreeView.VTV_NAMEROLECOUNT)
            {   /* The value group starts, a little apart from the names. */
                y += iPad / 2;
                lblHeading = Vco_lblValues;
            }
            else
            {   /* Inside a group: no heading. */
                lblHeading = null;
            }

            if (lblHeading != null)
            {   /* The heading, at the margin. */
                lblHeading.Size = lblHeading.PreferredSize;
                lblHeading.Location = new Point(iPad, y);
                y = lblHeading.Bottom + iPad / 4;
            }

            /* The swatch, indented; its description centred beside it; the   */
            /* sample in its own column, as tall as the swatch.               */
            btnSwatch = Vco_lstSwatches[i];
            btnSwatch.SetBounds(iPad * 2, y, iSwatchW, iRowH);
            lblRow = Vco_lstLabels[i];
            lblRow.Size = lblRow.PreferredSize;
            lblRow.Location = new Point(btnSwatch.Right + iPad / 2,
                y + (iRowH - lblRow.Height) / 2);
            lblSample = Vco_lstSamples[i];
            lblSample.SetBounds(btnSwatch.Right + iPad / 2 + iDescW + iPad, y, iSampleW, iRowH);
            y = btnSwatch.Bottom + iPad / 4;
        }

        /* The note: the icon at the margin, the text wrapped beside it. */
        y += iPad / 2;
        iNoteX = iPad + Vco_picInfo.Width + iPad / 2;
        Vco_picInfo.Location = new Point(iPad, y);
        Vco_lblNote.Size = Vco_lblNote.GetPreferredSize(new Size(iPad + iContent - iNoteX, 0));
        Vco_lblNote.Location = new Point(iNoteX, y);
        y = Math.Max(Vco_picInfo.Bottom, Vco_lblNote.Bottom) + iPad;

        /* The footer: the line across; Reset at the left; OK and Cancel at   */
        /* the right, Cancel last.                                            */
        Vco_lblBottomLine.SetBounds(iPad, y, iContent, 2);
        y = Vco_lblBottomLine.Bottom + iPad * 3 / 4;
        Vco_btnReset.SetBounds(iPad, y, iButtonW, iRowH);
        Vco_btnCancel.SetBounds(iPad + iContent - iButtonW, y, iButtonW, iRowH);
        Vco_btnOk.SetBounds(Vco_btnCancel.Left - iPad / 2 - iButtonW, y, iButtonW, iRowH);

        /* The dialog holds it all.  Setting the size raises Resize, which   */
        /* runs this again with the same sizes; that run's size is           */
        /* unchanged, so it raises no more.                                  */
        ClientSize = new Size(iContent + iPad * 2, Vco_btnOk.Bottom + iPad * 3 / 4);
    }
    /*------------------------------------------------------------------------*/
    /* Vco_OnResetClick:                                                      */
    /*                                                                        */
    /* The Reset button: every row back to its default (VCO_Reset); nothing   */
    /* is kept until OK.                                                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the button (unused).                                   */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : every row shows its default.                                */
    /*------------------------------------------------------------------------*/
    private void Vco_OnResetClick(object objSender, EventArgs evArgs)
    {
        /* The reset. */
        VCO_Reset();
    }

    /*------------------------------------------------------------------------*/
    /* Vco_OnAcceptClick:                                                     */
    /*                                                                        */
    /* The OK button (and Enter, the AcceptButton): the colours chosen are    */
    /* to be kept (VCO_Accept).                                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the button (unused).                                   */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : accepted, and the dialog closing.                           */
    /*------------------------------------------------------------------------*/
    private void Vco_OnAcceptClick(object objSender, EventArgs evArgs)
    {
        /* Accepted, then closed. */
        VCO_Accept();
    }

    /*------------------------------------------------------------------------*/
    /* Vco_OnCancelClick:                                                     */
    /*                                                                        */
    /* The Cancel button (and Escape, the CancelButton): nothing kept         */
    /* (VCO_Cancel).                                                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the button (unused).                                   */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the dialog closing, not accepted.                           */
    /*------------------------------------------------------------------------*/
    private void Vco_OnCancelClick(object objSender, EventArgs evArgs)
    {
        /* Closed, not accepted. */
        VCO_Cancel();
    }

    /*------------------------------------------------------------------------*/
    /* Vco_OnResize:                                                          */
    /*                                                                        */
    /* The dialog's size changed: everything placed again (VCO_Layout).       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the dialog (unused).                                   */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the controls placed and the client size set.                */
    /*------------------------------------------------------------------------*/
    private void Vco_OnResize(object objSender, EventArgs evArgs)
    {
        /* The layout (its own ClientSize change raises this once more,   */
        /* with the same sizes, and stops there -- VCO_Create).           */
        VCO_Layout();
    }

    /*------------------------------------------------------------------------*/
    /* Vco_OnDisposed:                                                        */
    /*                                                                        */
    /* The dialog is disposed: the drawing resources it made for itself --    */
    /* the title and heading fonts and the information picture -- released.   */
    /* The dialog font it was given is the caller's and is not released.      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objSender : the dialog (unused).                                   */
    /*     evArgs    : unused.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : its own fonts and picture freed.                            */
    /*------------------------------------------------------------------------*/
    private void Vco_OnDisposed(object objSender, EventArgs evArgs)
    {
        /* Each released if it was made: the null tests guard a dialog       */
        /* disposed before VCO_Create made them all (the handler is hooked   */
        /* only after the two fonts, so the picture can be missing).         */
        if (Vco_fntTitle != null)
        {   /* The title font. */
            Vco_fntTitle.Dispose();
        }

        if (Vco_fntHeading != null)
        {   /* The group headings' font. */
            Vco_fntHeading.Dispose();
        }

        if (Vco_bmpInfo != null)
        {   /* The note's information picture. */
            Vco_bmpInfo.Dispose();
        }
    }

    /*------------------------------------------------------------------------*/
    /* VCO_ReadSaved:                                                         */
    /*                                                                        */
    /* Whether the dialog closed with OK, for its caller (Jsn_ViewShowColors) */
    /* to read once ShowDialog has returned: only then are the colours        */
    /* copied back.                                                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true after OK; false for Cancel, Escape or the close box.   */
    /*------------------------------------------------------------------------*/
    internal bool VCO_ReadSaved()
    {
        /* Set by VCO_Accept. */
        return(Vco_bSaved);
    }

    /*------------------------------------------------------------------------*/
    /* VCO_ReadNameColors:                                                    */
    /*                                                                        */
    /* The name roles' colours as chosen in the dialog, for the caller to     */
    /* copy into the tree after OK.                                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Color[] : the dialog's own array (its copy of the tree's), not a   */
    /*               copy of it; read before the dialog is disposed.          */
    /*------------------------------------------------------------------------*/
    internal Color[] VCO_ReadNameColors()
    {
        /* The array the rows were chosen into. */
        return(Vco_arrNameColors);
    }

    /*------------------------------------------------------------------------*/
    /* VCO_ReadValueColors:                                                   */
    /*                                                                        */
    /* The value roles' colours as chosen, on the same terms as               */
    /* VCO_ReadNameColors.                                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Color[] : the dialog's own array, not a copy.                      */
    /*------------------------------------------------------------------------*/
    internal Color[] VCO_ReadValueColors()
    {
        /* The array the rows were chosen into. */
        return(Vco_arrValueColors);
    }


}

