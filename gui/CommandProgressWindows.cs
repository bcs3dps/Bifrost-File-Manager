/*----------------------------------------------------------------------------*/
/* CommandProgressWindows.cs                                                  */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The progress windows a command shows while the worker runs it: the copy    */
/* progress dialog (CopyForms.cs) or the "working" box (NewImageForm.cs),     */
/* opened, updated and closed by the views the worker publishes for that      */
/* command (ProgressOpen, Progress, ProgressClose), each keyed by the         */
/* command's serial.  The UI owns the window's modal life; the window only    */
/* shows and posts -- its Cancel posts a Cancel request -- and no work is     */
/* ever started from its Shown event.  Also the lookups that let a question   */
/* be asked over the progress window rather than behind it.                   */
/*----------------------------------------------------------------------------*/

/* The framework: Exception (System), the registry of open windows by       */
/* serial (Collections.Generic), the dialog font (Drawing), and the forms   */
/* (Windows.Forms).                                                         */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace BifrostFM
{
    /* The two UI calls the registry is given -- documentation table. */
    internal delegate void PGW_Report(RSL_clsResult objResult);    // shows a progress window's own failure, without a modal box; null: not shown
    internal delegate void PGW_Show(Form frmView, Form frmOwner);  // shows a window and returns when it has closed (the default: ShowDialog)

    /*------------------------------------------------------------------------*/
    /* PGV_clsProgressView -- what a ProgressOpen view carries: which window, */
    /* its caption, for a raw operation the units and words of its counts,    */
    /* and for a delete that the counts are of entries removed.  Display      */
    /* facts only -- documentation table.                                     */
    /*------------------------------------------------------------------------*/
    internal sealed class PGV_clsProgressView
    {
        internal const ulong PGV_ENTRY_BUSY_BYTES = 8UL * 1024 * 1024;  // a file in the image this size or larger shows the "working" box while it is read out for an editor (CommandNativeOperations.cs)
        internal bool PGV_bBusy;                                        // true: the "working" box (a moving bar, no Cancel); false: the copy progress dialog
        internal string PGV_sTitle;                                     // the window's caption; "" when none was given
        internal string PGV_sWhat;                                      // the "working" box's line saying what is being done; unused by the copy dialog
        internal uint PGV_uiSector;                                     // a raw operation's bytes per sector, its counts' unit; 0: a file copy, counted in items
        internal string PGV_sCopyText;                                  // a raw operation's words for its copy phase ("Writing to the device" ...); unused otherwise
        internal bool PGV_bRemoval;                                     // true: the copy dialog counts entries REMOVED by a delete, with no total (ERC_clsRemoveControl, EngineSession.cs); false otherwise

        /*--------------------------------------------------------------------*/
        /* PGV_clsProgressView:                                               */
        /*                                                                    */
        /* The view's facts, each null text taken as "".                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bBusy     : true for the "working" box.                        */
        /*     sTitle    : the caption, or null.                              */
        /*     sWhat     : the "working" box's line, or null.                 */
        /*     uiSector  : bytes per sector for a raw operation; optional, 0  */
        /*                 (a file copy) by default.                          */
        /*     sCopyText : a raw operation's copy-phase words; optional, ""   */
        /*                 by default, null taken as "".                      */
        /*     bRemoval  : true for a delete's window, which counts entries   */
        /*                 removed; optional, false by default.               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the facts.  They decide only what is shown;    */
        /*                     what may run is the worker's gate's business.  */
        /*--------------------------------------------------------------------*/
        internal PGV_clsProgressView(bool bBusy, string sTitle, string sWhat,
            uint uiSector = 0, string sCopyText = "", bool bRemoval = false)
        {
            /* Which window, the raw unit, and whether it counts removals. */
            PGV_bBusy = bBusy;
            PGV_uiSector = uiSector;
            PGV_bRemoval = bRemoval;

            /* The texts, each "" unless given -- nothing is filled in from   */
            /* the window or the filesystem when the view is drawn.           */
            PGV_sTitle = "";
            PGV_sWhat = "";
            PGV_sCopyText = "";
            if (sTitle != null)
            {   /* The caption given. */
                PGV_sTitle = sTitle;
            }

            if (sWhat != null)
            {   /* The "working" line given. */
                PGV_sWhat = sWhat;
            }

            if (sCopyText != null)
            {   /* The copy phase's words given (they differ for reading   */
                /* and for writing a device).                              */
                PGV_sCopyText = sCopyText;
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* PWN_clsProgressWindow -- one progress window's record, from the moment */
    /* its ProgressOpen is handled until its form is disposed.  It exists     */
    /* before the form does, because building and showing the form runs       */
    /* nested messages, and the ProgressClose may be handled among them --    */
    /* the record remembers it -- documentation table.                        */
    /*------------------------------------------------------------------------*/
    internal sealed class PWN_clsProgressWindow
    {
        internal RQT_clsRequest PWN_objRequest;        // the command's request; its serial is the window's key and the target of its Cancel
        internal Form PWN_frmView;                     // the form; null until it has been built
        internal bool PWN_bClosed;                     // true once ProgressClose (or the teardown) has been seen -- even before the form exists
        private readonly CLI_clsClient Pwn_objClient;  // where the Cancel is posted; given, never disposed here

        /*--------------------------------------------------------------------*/
        /* PWN_clsProgressWindow:                                             */
        /*                                                                    */
        /* The record, made before the form.                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objClient  : the command client a Cancel is posted through.    */
        /*     objRequest : the command's request.                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the record, with no form yet.                  */
        /*--------------------------------------------------------------------*/
        internal PWN_clsProgressWindow(CLI_clsClient objClient, RQT_clsRequest objRequest)
        {
            /* This command's own request -- not "whatever is running now"    */
            /* -- so a Cancel or a Close always concerns this window's run.   */
            PWN_objRequest = objRequest;
            Pwn_objClient = objClient;
        }

        /*--------------------------------------------------------------------*/
        /* PWN_OnCancel:                                                      */
        /*                                                                    */
        /* The copy form's Cancel callback (its Cancel button and close box,  */
        /* through CPY_AttachCommand): a stop posted for this display's       */
        /* operation (PWN_PostCancel).                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Cancel posted; the display waits for the completed      */
        /*            close.                                                  */
        /*--------------------------------------------------------------------*/
        internal void PWN_OnCancel()
        {
            /* The user's stop, for this display's operation. */
            PWN_PostCancel();
        }

        /*--------------------------------------------------------------------*/
        /* PWN_PostCancel:                                                    */
        /*                                                                    */
        /* Post a Cancel for this window's command.  Called by the copy       */
        /* form's Cancel (PWN_OnCancel) and by the registry when the window   */
        /* cannot be built or shown.                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the Cancel posted; the window stays until the command's */
        /*            ProgressClose.                                          */
        /*--------------------------------------------------------------------*/
        internal void PWN_PostCancel()
        {
            /* A Cancel request naming the command's serial (its revision).   */
            /* The worker accepts it only while that serial is the one        */
            /* running, so a late Cancel cannot stop a later command; the     */
            /* window never touches the stop flag itself.                     */
            Pwn_objClient.CLI_Post(CFC_clsCommands.CFC_Create(KND_enmKind.Cancel,
                0, null, null, null, PWN_objRequest.RQT_lSerial, 0, null, "", ""), null);
        }
    }

    /*------------------------------------------------------------------------*/
    /* PGW_clsProgressWindows -- the main window's registry of progress       */
    /* windows (made once, by MainCommand.cs), and the renderer of the views  */
    /* that drive them.  Used on the UI thread only -- documentation table.   */
    /*------------------------------------------------------------------------*/
    internal sealed class PGW_clsProgressWindows
    {
        /* The fixed parts, given at construction; and the windows, by serial. */
        private readonly CLI_clsClient Pgw_objClient;                              // the command client, used only to post Cancels
        private readonly Form Pgw_frmOwner;                                        // the main window, each progress window's modal owner; given
        private readonly Font Pgw_fntDialog;                                       // the program's dialog font; given, not disposed here
        private readonly PGW_Report Pgw_pfnReport;                                 // shows a window's own failure; null: not shown
        private readonly PGW_Show Pgw_pfnShow;                                     // how a window is shown; Pgw_ShowModal unless one was given
        private readonly Dictionary<long, PWN_clsProgressWindow> Pgw_dictWindows;  // the windows open, by command serial; each entry from its ProgressOpen to its form's disposal

        /*--------------------------------------------------------------------*/
        /* PGW_clsProgressWindows:                                            */
        /*                                                                    */
        /* The registry, empty; it opens no window and runs no command.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objClient : the command client, for Cancels.                   */
        /*     frmOwner  : the main window.                                   */
        /*     fntDialog : the dialog font.                                   */
        /*     pfnReport : shows a window's failure, or null.                 */
        /*     pfnShow   : how to show a window; optional, null by default,   */
        /*                 which takes ShowDialog (the one caller passes      */
        /*                 none).                                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the registry.                                  */
        /*--------------------------------------------------------------------*/
        internal PGW_clsProgressWindows(CLI_clsClient objClient, Form frmOwner,
            Font fntDialog, PGW_Report pfnReport, PGW_Show pfnShow = null)
        {
            /* The given parts, kept for the registry's life, and the empty   */
            /* registry.  Nothing of this goes to the worker.                 */
            Pgw_objClient = objClient;
            Pgw_frmOwner = frmOwner;
            Pgw_fntDialog = fntDialog;
            Pgw_pfnReport = pfnReport;
            Pgw_dictWindows = new Dictionary<long, PWN_clsProgressWindow>();

            /* How windows are shown: the one given, else a modal ShowDialog.   */
            /* Either way the call must return only once the window closes.     */
            Pgw_pfnShow = pfnShow;
            if (Pgw_pfnShow == null)
            {   /* None given: the ordinary modal show. */
                Pgw_pfnShow = Pgw_ShowModal;
            }
        }

        /* Progress presentation and addressed dispatch. */

        /*--------------------------------------------------------------------*/
        /* Pgw_ShowModal:                                                     */
        /*                                                                    */
        /* The default way a progress window is shown: modally, over the main */
        /* window.                                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmView  : the progress window, built.                         */
        /*     frmOwner : the main window.                                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : once the window has closed.                             */
        /*--------------------------------------------------------------------*/
        private static void Pgw_ShowModal(Form frmView, Form frmOwner)
        {
            /* ShowDialog runs its own message loop, in which the command's    */
            /* later views (progress, the close, questions) are handled; the   */
            /* window starts nothing when shown.                               */
            frmView.ShowDialog(frmOwner);
        }

        /*--------------------------------------------------------------------*/
        /* PGW_FindProgressOwner:                                             */
        /*                                                                    */
        /* The window to ask a command's question over, when that command     */
        /* has a progress window open: the progress window itself -- its      */
        /* owner, the main window, is disabled under it, and a question owned */
        /* by the main window would open behind it.  Used by MainCommand.cs's */
        /* owner lookup.                                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lSerial : the command serial the question names.               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Form : the progress window; null when there is none, or it is  */
        /*            closing or gone.                                        */
        /*--------------------------------------------------------------------*/
        internal Form PGW_FindProgressOwner(long lSerial)
        {
            PWN_clsProgressWindow objWindow;

            if (Pgw_dictWindows.TryGetValue(lSerial, out objWindow) &&
                !objWindow.PWN_bClosed && objWindow.PWN_frmView != null &&
                !objWindow.PWN_frmView.IsDisposed)
            {   /* That command's window, built, not closing, not disposed:   */
                /* the question goes over it.                                 */
                return(objWindow.PWN_frmView);
            }

            /* No usable window: the caller looks elsewhere. */
            return(null);
        }

        /*--------------------------------------------------------------------*/
        /* PGW_ExitOwner:                                                     */
        /*                                                                    */
        /* The window to ask the Exit question over: the newest live progress */
        /* window.  The Exit request has a serial of its own, so it cannot    */
        /* name the running command's window -- this finds it instead.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Form : the newest live progress window, or null when there is  */
        /*            none.                                                   */
        /*--------------------------------------------------------------------*/
        internal Form PGW_ExitOwner()
        {
            Dictionary<long, PWN_clsProgressWindow>.ValueCollection.Enumerator enmWindows;
            PWN_clsProgressWindow objWindow;
            Form frmOwner;
            long lNewest;

            /* The live window with the highest serial.  An older window may   */
            /* still be unwinding after its close, so only live, unclosed      */
            /* ones count; and the registry's order is not the order windows   */
            /* opened, so the serials themselves are compared.                 */
            frmOwner = null;
            lNewest = 0;
            enmWindows = Pgw_dictWindows.Values.GetEnumerator();
            while (enmWindows.MoveNext())
            {   /* One registered window per iteration. */
                objWindow = enmWindows.Current;
                if (!objWindow.PWN_bClosed && objWindow.PWN_frmView != null &&
                    !objWindow.PWN_frmView.IsDisposed && objWindow.PWN_objRequest.RQT_lSerial > lNewest)
                {   /* Live and newer than any so far: the candidate.  It    */
                    /* is only the question's parent; nothing else follows   */
                    /* from it.                                              */
                    frmOwner = objWindow.PWN_frmView;
                    lNewest = objWindow.PWN_objRequest.RQT_lSerial;
                }
            }

            /* The window found, or null -- the caller then tries the   */
            /* partition window and the main window (MainCommand.cs).   */
            return(frmOwner);
        }

        /*--------------------------------------------------------------------*/
        /* Pgw_OpenProgress:                                                  */
        /*                                                                    */
        /* Open a command's progress window and keep it until it closes: it   */
        /* is registered, built, shown modally (the call returns when it      */
        /* closes), then disposed and unregistered.  A window that cannot be  */
        /* built or shown has its command cancelled and its failure shown on  */
        /* its own, apart from the command's result.                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the command's request.                            */
        /*     objData    : the window's facts (the ProgressOpen's value).    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window has closed and is out of the registry.       */
        /*--------------------------------------------------------------------*/
        private void Pgw_OpenProgress(RQT_clsRequest objRequest, PGV_clsProgressView objData)
        {
            PWN_clsProgressWindow objWindow;
            CPY_clsProgressForm frmCopy;

            /* Registered BEFORE the form is built, so a ProgressClose          */
            /* handled while it is built or shown finds the record.  Keyed by   */
            /* serial: an older window still unwinding after a newer one        */
            /* opened removes only its own entry.                               */
            objWindow = new PWN_clsProgressWindow(Pgw_objClient, objRequest);
            Pgw_dictWindows.Add(objRequest.RQT_lSerial, objWindow);
            try
            {   /* Build the window the facts ask for, then show it; any   */
                /* exception goes to the catch below.                      */
                if (objData.PGV_bBusy)
                {   /* The "working" box: a moving bar and no Cancel. */
                    objWindow.PWN_frmView = new NBF_clsBusyForm(Pgw_fntDialog,
                        objData.PGV_sTitle, objData.PGV_sWhat);
                }
                else
                {   /* The copy progress dialog, its Cancel (and close box)   */
                    /* posting a Cancel for this command; for a raw           */
                    /* operation, set to show phases and sectors, and for a   */
                    /* delete, entries removed -- both set before the show,   */
                    /* so the first progress event is drawn in their terms.   */
                    frmCopy = new CPY_clsProgressForm(Pgw_fntDialog, objData.PGV_sTitle);
                    objWindow.PWN_frmView = frmCopy;
                    frmCopy.CPY_AttachCommand(objWindow.PWN_OnCancel);
                    if (objData.PGV_uiSector != 0)
                    {   /* A raw operation: counts in this sector size, the   */
                        /* copy phase in these words.                         */
                        frmCopy.CPY_SetRaw(objData.PGV_uiSector, objData.PGV_sCopyText);
                    }

                    if (objData.PGV_bRemoval)
                    {   /* A delete: its count is of entries removed, with no   */
                        /* total to fill the bar against.                       */
                        frmCopy.CPY_SetRemoval();
                    }
                }

                /* The program's icon, then the show -- unless the close has   */
                /* already been handled (during the building).  A window       */
                /* shown after its close would wait for a ProgressClose that   */
                /* will never come, and stay up for good.                      */
                objWindow.PWN_frmView.Icon = Pgw_frmOwner.Icon;
                if (!objWindow.PWN_bClosed)
                {   /* Still running: shown, and this call returns when the   */
                    /* ProgressClose has closed it.                           */
                    Pgw_pfnShow(objWindow.PWN_frmView, Pgw_frmOwner);
                }
            }
            catch (Exception exProgress)
            {   /* The window could not be built or shown, while the         */
                /* command may still be running: it is cancelled (a Cancel   */
                /* for this serial only -- it starts nothing), and the       */
                /* window's failure is shown, apart from the command's own   */
                /* result, which comes later.                                */
                objWindow.PWN_PostCancel();
                if (Pgw_pfnReport != null)
                {   /* A report call was given: the failure, as internal. */
                    Pgw_pfnReport(new RSL_clsResult(SYS_clsSystem.SYS_INTERNAL,
                        exProgress.Message, null));
                }
            }
            finally
            {   /* However it ended: marked closed FIRST, so a question       */
                /* handled during the disposal does not take this window as   */
                /* its owner; then the form disposed and the entry removed.   */
                objWindow.PWN_bClosed = true;
                try
                {   /* The form, disposed even after a failed build, show   */
                    /* or report.                                           */
                    if (objWindow.PWN_frmView != null)
                    {   /* A form was built: disposed.  (A build that threw   */
                        /* may have left none.)                               */
                        objWindow.PWN_frmView.Dispose();
                    }
                }
                finally
                {   /* The entry removed even when the disposal throws. */
                    Pgw_dictWindows.Remove(objRequest.RQT_lSerial);
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* PGW_RenderProgress:                                                */
        /*                                                                    */
        /* Handle a view if it belongs to a progress window: ProgressOpen     */
        /* opens one, Progress updates a copy dialog's counts, ProgressClose  */
        /* closes it.  Every other view -- results, questions, a check's      */
        /* report lines -- is left to the main window's other renderers.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objView : the view, on the UI thread.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when it was handled here; false when it is not a   */
        /*            progress view, or names no window registered here.      */
        /*--------------------------------------------------------------------*/
        internal bool PGW_RenderProgress(VWM_clsView objView)
        {
            PWN_clsProgressWindow objWindow;
            PGV_clsProgressView objData;
            PGD_clsProgressData objProgress;
            CPY_clsProgressForm frmCopy;
            NBF_clsBusyForm frmBusy;

            if (objView.VWM_objRequest == null)
            {   /* No request (a state publication): no window to address. */
                return(false);
            }

            if (objView.VWM_enmKind == VWK_enmViewKind.ProgressOpen)
            {   /* Open a window -- only from the facts the view carries,   */
                /* never from what the main window shows.                   */
                objData = objView.VWM_objValue as PGV_clsProgressView;
                if (objData == null)
                {   /* No facts: not this renderer's view (another may take   */
                    /* it).                                                   */
                    return(false);
                }

                if (!Pgw_dictWindows.ContainsKey(objView.VWM_objRequest.RQT_lSerial))
                {   /* No window for this command yet: opened.  This call   */
                    /* returns only when the window has closed.             */
                    Pgw_OpenProgress(objView.VWM_objRequest, objData);
                }

                /* Handled -- a second Open for a command that already has   */
                /* a window too, which opens nothing more.                   */
                return(true);
            }

            if (!Pgw_dictWindows.TryGetValue(objView.VWM_objRequest.RQT_lSerial, out objWindow))
            {   /* No window for this serial: a late view, or another          */
                /* renderer's -- it must not touch a later command's window.   */
                return(false);
            }

            if (objView.VWM_enmKind == VWK_enmViewKind.ProgressClose)
            {   /* The command is done: the window is marked closed (even      */
                /* while it is still being built, so it is then never shown)   */
                /* and, when it exists, finished -- CPY_Finish / NBF_Finish    */
                /* set the flag that lets it close and close it, which ends    */
                /* its ShowDialog; its disposal is Pgw_OpenProgress's.         */
                objWindow.PWN_bClosed = true;
                frmCopy = objWindow.PWN_frmView as CPY_clsProgressForm;
                frmBusy = objWindow.PWN_frmView as NBF_clsBusyForm;
                if (frmCopy != null && !frmCopy.IsDisposed)
                {   /* The copy dialog: finished and closed. */
                    frmCopy.CPY_Finish();
                }
                else if (frmBusy != null && !frmBusy.IsDisposed)
                {   /* The "working" box: finished and closed the same way. */
                    frmBusy.NBF_Finish();
                }

                /* Handled, whether or not a form existed yet. */
                return(true);
            }

            if (objView.VWM_enmKind == VWK_enmViewKind.Progress)
            {   /* A progress event, its facts copied by the worker while      */
                /* the engine's pointers were still valid.  A check's report   */
                /* and progress (PGD_bCheck) belong to the check window's      */
                /* renderer instead.                                           */
                objProgress = objView.VWM_objValue as PGD_clsProgressData;
                if (objProgress == null || objProgress.PGD_bCheck)
                {   /* Not a copy's or raw operation's event: left to the   */
                    /* others.                                              */
                    return(false);
                }

                /* Shown only by a live copy dialog: the "working" box has no   */
                /* counts, and a closed window ignores a late event.            */
                frmCopy = objWindow.PWN_frmView as CPY_clsProgressForm;
                if (!objWindow.PWN_bClosed && frmCopy != null && !frmCopy.IsDisposed)
                {   /* The counts, phase and bar updated from the event. */
                    frmCopy.CPY_ShowProgress(objProgress.PGD_sImage, objProgress.PGD_uiKind,
                        objProgress.PGD_ullDone, objProgress.PGD_ullTotal,
                        objProgress.PGD_iCopied, objProgress.PGD_iProblems);
                }

                /* Handled, even when ignored (after the close). */
                return(true);
            }

            /* Another kind of view, for this command: not a progress view. */
            return(false);
        }
    }
}
