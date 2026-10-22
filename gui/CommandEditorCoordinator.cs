/*----------------------------------------------------------------------------*/
/* CommandEditorCoordinator.cs                                                */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The main window's side of the editors (text, hex, structured): which       */
/* editor windows exist and the command-system id each has, the saves they    */
/* hand over, and the worker's questions that must be answered inside an      */
/* editor (close it, with its Save / Save As / Discard / Cancel prompt).      */
/* UI thread only.  It decides nothing about the filesystem: an image entry's */
/* save becomes a SaveEditor request the worker runs, a Windows file's save   */
/* (already written by the editor itself) becomes the report of a fact, and   */
/* a close question is answered only from what really happened to the         */
/* window -- its FormClosed, a save's result, the editor's own work           */
/* returning -- never from a guess.                                           */
/*----------------------------------------------------------------------------*/

/* The framework: the exceptions thrown and caught, and the Action that     */
/* posts a fact back to a window's message loop (System); the id- and       */
/* request-keyed tables (Collections.Generic); and the editor windows and   */
/* their FormClosed (Windows.Forms).  The error log (DBGLOG_clsDebugLog,    */
/* gui\lib\DebugLog.cs) is in this program's own namespace.                 */
using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace BifrostFM
{
    /* The coordinator's fixed calls into the main window, given once (the   */
    /* constructor, EUI_SetInputRoutes) and never put into a request the     */
    /* worker receives -- documentation table.                               */
    internal delegate void EUI_RefreshHost();                                           // re-list the Windows pane after a Windows file was written (MainCommand.cs Main_RefreshCommandHost)
    internal delegate void EUI_Report(Form frmOwner, RSL_clsResult objResult);          // show a save's failure or warning, AFTER the editor has its answer (Main_ReportCommandSave: the status line; the owner is unused there)
    internal delegate void EUI_CloseWindowsInput(PRS_clsPresentation objPresentation);  // a close question's Windows Save As: the picker, then the editor's own write, both returned before this does (Main_CloseWindowsInput)
    internal delegate void EUI_PostFact(long lEditor, long lCloseOrder);                // an editor's report: its own work returned (EUI_LOCAL_RETURN), or the close it scheduled after save N returned (N)

    /*------------------------------------------------------------------------*/
    /* EDP_clsEditorPort -- one registered editor window as the coordinator   */
    /* holds it: the window, where it saves now, and the state of any close   */
    /* the worker is asking it to make.  UI thread only -- documentation      */
    /* table.                                                                 */
    /*------------------------------------------------------------------------*/
    internal sealed class EDP_clsEditorPort
    {
        internal Form EDP_frmViewer;                // the window; kept until it ends (its FormClosed, or the window watch's report of it disposed)
        internal OFD_clsOpenedFile EDP_objFile;     // where its Save writes now: the newest acknowledged save's descriptor (Eui_ApplySavedBinding), never a Save As still pending
        internal int EDP_iPending;                  // saves handed over and not yet answered; a close question waits while it is not 0
        internal PRS_clsPresentation EDP_objClose;  // the worker's close question shown for this window now (its newest display), or null
        internal long EDP_lAcknowledgedOrder;       // the save order of EDP_objFile: an older save answered late cannot move the destination back
        internal bool EDP_bCloseDispatch;           // true while Eui_TryClose's Close() or Windows Save As call has not returned; a nested delivery waits for it
        internal bool EDP_bAwaitCloseReturn;        // a Windows save made from the close prompt scheduled the editor's own close; the question waits for its return fact
        internal long EDP_lCloseReturnOrder;        // that save's order: only the return fact carrying it ends the wait
    }

    /*------------------------------------------------------------------------*/
    /* SVP_clsSavePending -- one save waiting for its answer, held in         */
    /* Eui_dictSaves under its request -- documentation table.                */
    /*------------------------------------------------------------------------*/
    internal sealed class SVP_clsSavePending
    {
        internal EDP_clsEditorPort SVP_objPort;  // the editor it came from, kept through the answer even if that window has since ended
        internal OFD_clsOpenedFile SVP_objFile;  // the save's own descriptor (destination, revision, order), captured when it was made; its stage went with the request
        internal OFD_SaveDone SVP_pfnDone;       // the editor's answer for exactly this save, called once
    }

    /*------------------------------------------------------------------------*/
    /* EUI_clsEditorCoordinator -- the coordinator: one, made by              */
    /* MainCommand.cs at start-up, used on the UI thread only.                */
    /*------------------------------------------------------------------------*/
    internal sealed class EUI_clsEditorCoordinator
    {
        /* The value of a return fact that is not a close's -- documentation   */
        /* table.                                                              */
        internal const long EUI_LOCAL_RETURN = -1;   // "the editor's own work returned": never a save order (the editors count theirs up from 1), so it never ends a close's wait
        internal const long EUI_LOCAL_DECLINED = -2; // "the editor's own close question was answered Cancel" under the worker's close question: that question is answered Cancel (Eui_OnEditorFact); never a save order either

        /* The bindings, fixed at construction, and the UI-side state --   */
        /* documentation table.                                            */
        private readonly CLI_clsClient Eui_objClient;                                   // the client to the worker; borrowed
        private readonly LFT_clsLifetimes Eui_objLifetimes;                             // the main window's window watch; borrowed: it reports an editor disposed without its FormClosed
        private readonly string Eui_sStageRoot;                                         // the private staging folder every save's snapshot is made in -- never a file the user named
        private readonly EUI_RefreshHost Eui_pfnRefreshHost;                            // re-lists the Windows pane after a Windows save; null for none
        private readonly EUI_Report Eui_pfnReport;                                      // shows a save's failure or warning; null for none
        private readonly Dictionary<long, EDP_clsEditorPort> Eui_dictPorts;             // the registered editors by id: negative for a Windows file's, positive (the worker's) for an image entry's
        private readonly Dictionary<RQT_clsRequest, SVP_clsSavePending> Eui_dictSaves;  // the saves waiting for their answer, by request (compared by reference)
        private OFD_TargetRequest Eui_pfnTargetPicker;                                  // the Save As target request, copied into every editor registered after EUI_SetInputRoutes
        private OFD_ImageAvailable Eui_pfnImageAvailable;                               // advice for the Save As menus: whether an image target can be offered; copied likewise
        private EUI_CloseWindowsInput Eui_pfnCloseWindows;                              // a close question's Windows Save As; read when a close needs it
        private int Eui_iConstructing;                                                  // editor windows being made now (EUI_BeginViewerConstruction to its End)
        private PRS_clsPresentation Eui_objViewsReady;                                  // the worker's "every editor made?" question, kept until the count is 0; null when none waits

        /* Registration, and the end of an editor's window. */

        /*--------------------------------------------------------------------*/
        /* EUI_clsEditorCoordinator:                                          */
        /*                                                                    */
        /* The coordinator for one command client, with nothing registered.   */
        /* It holds the editors' windows and their pending saves, never the   */
        /* engine's state.                                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objClient      : the client to the worker; borrowed.           */
        /*     sStageRoot     : the private staging folder.                   */
        /*     pfnRefreshHost : the Windows pane's re-list, or null.          */
        /*     pfnReport      : the save report, or null.                     */
        /*     objLifetimes   : the main window's window watch; required --   */
        /*                      every editor is watched through it.           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the coordinator; an ArgumentNullException      */
        /*                     when there is no window watch.                 */
        /*--------------------------------------------------------------------*/
        internal EUI_clsEditorCoordinator(CLI_clsClient objClient, string sStageRoot,
            EUI_RefreshHost pfnRefreshHost, EUI_Report pfnReport, LFT_clsLifetimes objLifetimes)
        {
            if (objLifetimes == null)
            {   /* No window watch: refused.  Without it an editor disposed     */
                /* without its FormClosed would stay registered for good, and   */
                /* every image close that asked it would be answered Cancel     */
                /* (Eui_TryClose: disposed with no closure acknowledgement).    */
                throw new ArgumentNullException("objLifetimes");
            }

            /* The bindings, kept for the coordinator's life; both tables   */
            /* start empty.                                                 */
            Eui_objLifetimes = objLifetimes;
            Eui_objClient = objClient;
            Eui_sStageRoot = sStageRoot;
            Eui_pfnRefreshHost = pfnRefreshHost;
            Eui_pfnReport = pfnReport;
            Eui_dictPorts = new Dictionary<long, EDP_clsEditorPort>();
            Eui_dictSaves = new Dictionary<RQT_clsRequest, SVP_clsSavePending>();
        }

        /*--------------------------------------------------------------------*/
        /* EUI_SetInputRoutes:                                                */
        /*                                                                    */
        /* The main window's three routes for the editors: the Save As target */
        /* request, the advice for its menus, and a close question's Windows  */
        /* Save As.  Called once by MainCommand.cs, right after the           */
        /* constructor and before any editor exists: the first two are copied */
        /* into each editor's descriptor at its registration, so an editor    */
        /* registered earlier would keep none; the third is read when a close */
        /* needs it.                                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pfnPicker       : the Save As target request                   */
        /*                       (Main_RequestEditorTarget).                  */
        /*     pfnAvailable    : whether an image target can be offered       */
        /*                       (Main_CanTargetImage) -- advice only.        */
        /*     pfnCloseWindows : a close's Windows Save As                    */
        /*                       (Main_CloseWindowsInput).                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the routes kept.                                        */
        /*--------------------------------------------------------------------*/
        internal void EUI_SetInputRoutes(OFD_TargetRequest pfnPicker,
            OFD_ImageAvailable pfnAvailable, EUI_CloseWindowsInput pfnCloseWindows)
        {
            /* Kept as given. */
            Eui_pfnTargetPicker = pfnPicker;
            Eui_pfnImageAvailable = pfnAvailable;
            Eui_pfnCloseWindows = pfnCloseWindows;
        }

        /*--------------------------------------------------------------------*/
        /* EUI_Register:                                                      */
        /*                                                                    */
        /* Take a newly made editor window into the command system: a Windows */
        /* file's editor given its id, the window recorded, its end hooked,   */
        /* and the worker told it exists.  Called by MainCommand.cs           */
        /* (Main_TrackCommandViewer) once the window has its handle and       */
        /* before it is shown, so no save or close can come from a window     */
        /* that is not registered.                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmViewer : the editor window, not disposed.                   */
        /*     objFile   : its descriptor: paths and range set, and for an    */
        /*                 image entry the worker's id.                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : registered; an ArgumentException for no window, no      */
        /*            descriptor, a disposed window, an id of the wrong sign  */
        /*            or an id already registered -- nothing is then          */
        /*            recorded here.                                          */
        /*--------------------------------------------------------------------*/
        internal void EUI_Register(Form frmViewer, OFD_clsOpenedFile objFile)
        {
            EDP_clsEditorPort objPort;
            TXE_clsTextEditForm frmText;
            HEXF_clsHexEditForm frmHex;
            JSN_clsJsonEditForm frmJson;

            if (frmViewer == null || objFile == null || frmViewer.IsDisposed)
            {   /* No window, no descriptor, or a disposed window: refused.  A    */
                /* disposed window raises no FormClosed, so its end would never   */
                /* be reported.                                                   */
                throw new ArgumentException("A live viewer and descriptor are required.");
            }

            if (objFile.OFD_bHostFile && objFile.OFD_lEditorId == 0)
            {   /* A Windows file's editor arrives without an id: it is given     */
                /* the next negative one (CLI_NewWindowId).  An image entry's     */
                /* id is the worker's own, positive, made when it extracted the   */
                /* entry for the editor.                                          */
                objFile.OFD_lEditorId = Eui_objClient.CLI_NewWindowId();
            }

            if ((!objFile.OFD_bHostFile && objFile.OFD_lEditorId <= 0) ||
                (objFile.OFD_bHostFile && objFile.OFD_lEditorId >= 0))
            {   /* An id of the wrong sign: refused.  The worker tells the two   */
                /* kinds apart by the sign alone (Sys_HostBindingSequence        */
                /* refuses a Windows editor's registration with a non-negative   */
                /* id), so this editor could never be matched to its record.     */
                throw new ArgumentException("The viewer has no valid command ownership id.");
            }

            /* The descriptor completed for the command system: the staging    */
            /* folder its saves use; its opening facts sealed once             */
            /* (OFD_CaptureOrigin -- a later Save As changes where it saves,   */
            /* never where it was opened from, which the worker's record and   */
            /* its baseline belong to); and the routes its menus call.         */
            objFile.OFD_sStageRoot = Eui_sStageRoot;
            objFile.OFD_CaptureOrigin();
            objFile.OFD_pfnPickTarget = Eui_pfnTargetPicker;
            objFile.OFD_pfnImageAvailable = Eui_pfnImageAvailable;
            objFile.OFD_pfnUserFileAllowed = Eui_CanRequestUserFile;
            /* The window recorded under its id, saving where it was opened   */
            /* (Dictionary.Add throws for an id already registered).          */
            objPort = new EDP_clsEditorPort();
            objPort.EDP_frmViewer = frmViewer;
            objPort.EDP_objFile = objFile;
            Eui_dictPorts.Add(objFile.OFD_lEditorId, objPort);

            /* Each editor kind given the coordinator's fact receiver, which   */
            /* it calls once its own work (a write, a model change, its own    */
            /* close prompt) has really returned, or once the close it         */
            /* scheduled after a save has.  A close question waits for these   */
            /* facts rather than polling the window.                           */
            frmText = frmViewer as TXE_clsTextEditForm;
            frmHex = frmViewer as HEXF_clsHexEditForm;
            frmJson = frmViewer as JSN_clsJsonEditForm;
            if (frmText != null)
            {   /* A text editor: given the receiver. */
                frmText.TXE_BindCommandFacts(EUI_PostEditorFact);
            }

            if (frmHex != null)
            {   /* A hex editor: given the receiver. */
                frmHex.HEXF_BindCommandFacts(EUI_PostEditorFact);
            }

            if (frmJson != null)
            {   /* A structured editor: given the receiver, which its tree's    */
                /* own lazy work also reports through (JSN_BindCommandFacts).   */
                frmJson.JSN_BindCommandFacts(EUI_PostEditorFact);
            }

            /* FormClosed hooked before the window is shown.  An editor that    */
            /* holds a file open (the hex editor's read handle) lets go of it   */
            /* in a FormClosed handler hooked in its own constructor, so        */
            /* before this one -- WinForms calls an event's handlers in the     */
            /* order they were hooked -- and by the time Eui_OnViewerClosed     */
            /* reports the end, the worker may delete the extracted baseline.   */
            frmViewer.FormClosed += Eui_OnViewerClosed;

            /* Watched, ARMED: FormClosed reports the normal end and settles   */
            /* the watch; a viewer disposed without it (FormClosed never       */
            /* raised) is reported by the watch through Eui_OnViewerLost.      */
            Eui_objLifetimes.LFT_Watch(frmViewer, Eui_OnViewerLost, true);
            if (objFile.OFD_objOriginal.ORG_bHost)
            {   /* Opened on a Windows file: the worker records it in its        */
                /* Windows set (RegisterHostEditor, save order 0), before this   */
                /* editor can choose an image target in a Save As.  Nothing is   */
                /* written; the worker refuses a duplicate.                      */
                Eui_objClient.CLI_Post(CFC_clsCommands.CFC_Create(KND_enmKind.RegisterHostEditor,
                    objFile.OFD_lEditorId, null, null, null, 0, 0,
                    new HBA_clsHostBindingArgs(objFile.OFD_sImagePath, 0), "", ""), null);
            }
            else
            {   /* Opened on an image entry: the worker already holds its       */
                /* record from the extraction, and is told its window now       */
                /* exists (EditorReady) -- which tells a later close question   */
                /* a real window from one still being made.                     */
                Eui_objClient.CLI_Post(CFC_clsCommands.CFC_Create(KND_enmKind.EditorReady,
                    objFile.OFD_lEditorId, null, null, null, 0, 0, null, "", ""), null);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Eui_CanRequestUserFile:                                            */
        /*                                                                    */
        /* The test every registered editor's Save and Save As make before    */
        /* they start (OFD_CanRequestUserFile): may fresh user work begin     */
        /* now?  It is the panes' own test (SYS_CanAcceptUserWork), so an     */
        /* editor writes no Windows file while the worker is busy, exiting,   */
        /* or holding a picker.                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when nothing blocks user work (read under the      */
        /*            worker's queue lock).                                   */
        /*--------------------------------------------------------------------*/
        private bool Eui_CanRequestUserFile()
        {
            /* Asked BEFORE the editor writes: a Windows file is written by   */
            /* the editor itself, so this is the only test before the write   */
            /* -- a refusal after it could only report a file already         */
            /* changed.                                                       */
            return(Eui_objClient.CLI_ReadSystem().SYS_CanAcceptUserWork());
        }

        /*--------------------------------------------------------------------*/
        /* Eui_FindPort:                                                      */
        /*                                                                    */
        /* The registration that holds a window, found by the window itself   */
        /* -- a FormClosed or the window watch names a window, not an id.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmViewer : the window.                                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     EDP_clsEditorPort : its registration, or null when it has none */
        /*                         (never registered, or ended already).      */
        /*--------------------------------------------------------------------*/
        private EDP_clsEditorPort Eui_FindPort(Form frmViewer)
        {
            Dictionary<long, EDP_clsEditorPort>.ValueCollection.Enumerator enmPorts;

            /* A walk of the table itself, not of a copy: nothing in it   */
            /* changes the table or shows a window.                       */
            enmPorts = Eui_dictPorts.Values.GetEnumerator();
            try
            {   /* The finally disposes the enumerator, as a foreach would. */
                while (enmPorts.MoveNext())
                {   /* One registration per iteration. */
                    if (enmPorts.Current.EDP_frmViewer == frmViewer)
                    {   /* This registration holds the window: returned (the   */
                        /* finally still runs).                                */
                        return(enmPorts.Current);
                    }
                }
            }
            finally
            {   /* The walk ended before the caller changes the table or   */
                /* shows anything.                                         */
                enmPorts.Dispose();
            }

            /* No registration holds the window. */
            return(null);
        }

        /*--------------------------------------------------------------------*/
        /* Eui_OnViewerClosed:                                                */
        /*                                                                    */
        /* A registered viewer's FormClosed: its end is reported              */
        /* (Eui_ViewerEnded).                                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the closed viewer.                                 */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : EditorClosed posted and a close question answered       */
        /*            Closed, or nothing for a viewer already ended.          */
        /*--------------------------------------------------------------------*/
        private void Eui_OnViewerClosed(object objSender, FormClosedEventArgs evArgs)
        {
            /* The viewer this FormClosed belongs to (every hook of it is on   */
            /* a registered viewer, Eui_Register).                             */
            Eui_ViewerEnded(objSender as Form);
        }

        /*--------------------------------------------------------------------*/
        /* Eui_ViewerEnded:                                                   */
        /*                                                                    */
        /* A registered editor's end, from either report of it -- its         */
        /* FormClosed, or the window watch's report that it was disposed      */
        /* without one (Eui_OnViewerLost): the window is forgotten, the       */
        /* worker told (EditorClosed: it removes the editor's record and its  */
        /* extracted baseline), and a close question it was being asked is    */
        /* answered Closed.  A second report of the same end does nothing.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmViewer : the ended window.                                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : ended; nothing for a window not (or no longer)          */
        /*            registered.                                             */
        /*--------------------------------------------------------------------*/
        private void Eui_ViewerEnded(Form frmViewer)
        {
            EDP_clsEditorPort objPort;
            OFD_clsOpenedFile objFile;
            PRS_clsPresentation objClose;

            /* The registration, looked up before anything changes. */
            objPort = Eui_FindPort(frmViewer);
            if (objPort == null)
            {   /* Not registered, or ended already: nothing to do. */
                return;
            }

            /* The end is reported here, so the window watch has nothing left    */
            /* to report: it stops watching this viewer, and the disposal that   */
            /* follows a close is not mistaken for a viewer lost unreported.     */
            Eui_objLifetimes.LFT_Settle(frmViewer);

            /* The descriptor and the close question read before the            */
            /* registration goes; then the worker told.  The EditorClosed       */
            /* names the editor by its id -- all the worker matches it by --    */
            /* with the epoch and range it was OPENED from (where it saves      */
            /* now can be another filesystem; its baseline is the opening's).   */
            /* No reply is needed: a duplicate is harmless there.               */
            objFile = objPort.EDP_objFile;
            objClose = objPort.EDP_objClose;
            Eui_dictPorts.Remove(objFile.OFD_lEditorId);
            Eui_objClient.CLI_Post(new RQT_clsRequest(KND_enmKind.EditorClosed,
                objFile.OFD_lEditorId, objFile.OFD_objOriginal.ORG_lEpoch,
                objFile.OFD_objOriginal.ORG_objTarget, null, 0, 0, null, "", ""), null);

            if (objClose != null)
            {   /* The worker was asking this window to close: answered       */
                /* Closed, since it really has.  That is what the worker      */
                /* waits for -- Close()'s returning would prove nothing, as   */
                /* the editor's prompt can keep the window open.              */
                Eui_Reply(objClose, ANK_enmAnswerKind.Closed, "");
            }
        }

        /*--------------------------------------------------------------------*/
        /* Eui_OnViewerLost:                                                  */
        /*                                                                    */
        /* The window watch found a registered viewer gone without its        */
        /* FormClosed having run: report its end exactly as FormClosed        */
        /* would have.  A normal close settles the watch in                   */
        /* Eui_ViewerEnded, so this is reached only for a viewer disposed     */
        /* unclosed; and Eui_ViewerEnded looks the viewer up first, so one    */
        /* already ended makes this do nothing.                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmLost : the gone viewer.                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : EditorClosed posted, or nothing when already ended.     */
        /*--------------------------------------------------------------------*/
        private void Eui_OnViewerLost(Form frmLost)
        {
            /* The same end as a real close. */
            Eui_ViewerEnded(frmLost);
        }

        /*--------------------------------------------------------------------*/
        /* EUI_FindOwner:                                                     */
        /*                                                                    */
        /* The window a worker question is shown over when it names an editor */
        /* (CommandQuestionPresenter.cs, resolving a question's owner): the   */
        /* editor's window when it is registered and not disposed.  Otherwise */
        /* null, and the presenter tries the pickers and the other windows.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lWindow : the id the question names.                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Form : the editor's window, or null.                           */
        /*--------------------------------------------------------------------*/
        internal Form EUI_FindOwner(long lWindow)
        {
            EDP_clsEditorPort objPort;

            if (Eui_dictPorts.TryGetValue(lWindow, out objPort) &&
                !objPort.EDP_frmViewer.IsDisposed)
            {   /* Registered, and its window is alive: the question is shown   */
                /* over it.                                                     */
                return(objPort.EDP_frmViewer);
            }

            /* No live editor has that id: null, for the presenter's next   */
            /* lookup.                                                      */
            return(null);
        }

        /* Where an editor saves, and whether its image is there. */

        /*--------------------------------------------------------------------*/
        /* Eui_FileTarget:                                                    */
        /*                                                                    */
        /* An image-entry descriptor's range as a target: the image or device */
        /* file, the filesystem's start and length in it, and its             */
        /* description -- from the descriptor alone, never from what a pane   */
        /* shows now.  Every caller passes only a descriptor that saves to an */
        /* image: a Windows file's has no image file, which TGT_clsTarget     */
        /* refuses.                                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objFile : the descriptor.                                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     TGT_clsTarget : a new target; an ArgumentException             */
        /*                     (TGT_clsTarget) for an empty image path.       */
        /*--------------------------------------------------------------------*/
        private static TGT_clsTarget Eui_FileTarget(OFD_clsOpenedFile objFile)
        {
            /* The disk's size and sector size are not known to an editor and   */
            /* are given as 0 and 512.  Neither takes part in comparing         */
            /* targets -- TGT_SameRange compares the path, the start and the    */
            /* length, and so does the save's own gate (CommandSequences.cs)    */
            /* -- and every editor save carries the same two.                   */
            return(new TGT_clsTarget(objFile.OFD_sImageFile, objFile.OFD_ullOffset,
                objFile.OFD_ullLength, objFile.OFD_sDeviceText, 0, 512));
        }

        /*--------------------------------------------------------------------*/
        /* EUI_ApplyState:                                                    */
        /*                                                                    */
        /* A state the worker published (MainCommand.cs, before the main      */
        /* window's own menus take it): each image-entry editor's image       */
        /* marked as there or gone.  Gone means that very filesystem is not   */
        /* open -- the same range and the same epoch -- so an image closed    */
        /* and opened again counts as gone too, its Save refused until a Save */
        /* As.  A Windows file's editor is never marked.  This is advice for  */
        /* the editors' menus: the worker checks a save's range and epoch     */
        /* again itself.                                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objState : the published state, already installed in the       */
        /*                client.                                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : every registered editor marked, or nothing for a state  */
        /*            skipped (below).                                        */
        /*--------------------------------------------------------------------*/
        internal void EUI_ApplyState(STV_clsStateView objState)
        {
            Dictionary<long, EDP_clsEditorPort>.ValueCollection.Enumerator enmPorts;
            EDP_clsEditorPort objPort;
            bool bUnavailable;

            if (objState.STV_enmMode == MOD_enmMode.None && objState.STV_bRunning)
            {   /* No filesystem open while a request runs -- which is how a   */
                /* reopen for access (read-only to writable, the same range    */
                /* and epoch) looks part-way: skipped.  Another state always   */
                /* follows, since the worker publishes one when the request    */
                /* ends (CommandSystem.cs), and that one marks the editors.    */
                return;
            }

            /* Every registration marked.  A walk of the table itself: the      */
            /* marks set flags only, show nothing and change no registration,   */
            /* so the table cannot change under it.                             */
            enmPorts = Eui_dictPorts.Values.GetEnumerator();
            try
            {   /* The finally disposes the enumerator, as a foreach would. */
                while (enmPorts.MoveNext())
                {   /* One registration per iteration. */
                    objPort = enmPorts.Current;
                    /* Gone when the editor saves to an image entry and the   */
                    /* worker has no filesystem open, another epoch (closed   */
                    /* and opened since), no target, or another range.        */
                    bUnavailable = !objPort.EDP_objFile.OFD_bHostFile &&
                        (objState.STV_enmMode != MOD_enmMode.Open ||
                        objState.STV_lEpoch != objPort.EDP_objFile.OFD_lEpoch ||
                        objState.STV_objTarget == null ||
                        !objState.STV_objTarget.TGT_SameRange(Eui_FileTarget(objPort.EDP_objFile)));

                    /* Marked on the coordinator's descriptor and the editor's   */
                    /* own (Eui_SetUnavailable).                                 */
                    Eui_SetUnavailable(objPort, bUnavailable);
                }
            }
            finally
            {   /* The walk ended. */
                enmPorts.Dispose();
            }
        }

        /*--------------------------------------------------------------------*/
        /* Eui_SetUnavailable:                                                */
        /*                                                                    */
        /* Mark one editor's image as there or gone: on the coordinator's     */
        /* descriptor and, through the editor's own setter, on the descriptor */
        /* its menus read -- two objects once any save has been acknowledged  */
        /* (Eui_ApplySavedBinding keeps the save's own).  The setters only    */
        /* set a flag, so this is safe inside EUI_ApplyState's walk.          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objPort      : the registration.                               */
        /*     bUnavailable : true when its image is gone.                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : marked (never for a Windows file, whose mark stays      */
        /*            false); an editor marked gone refuses an ordinary Save  */
        /*            and offers Save As.                                     */
        /*--------------------------------------------------------------------*/
        private static void Eui_SetUnavailable(EDP_clsEditorPort objPort, bool bUnavailable)
        {
            TXE_clsTextEditForm frmText;
            HEXF_clsHexEditForm frmHex;
            JSN_clsJsonEditForm frmJson;

            /* The coordinator's descriptor first; a save already captured   */
            /* keeps its own descriptor, which this does not touch.  Then    */
            /* which editor kind the window is: one test below matches, or   */
            /* none for any other window.                                    */
            objPort.EDP_objFile.OFD_bDetached =
                !objPort.EDP_objFile.OFD_bHostFile && bUnavailable;
            frmText = objPort.EDP_frmViewer as TXE_clsTextEditForm;
            frmHex = objPort.EDP_frmViewer as HEXF_clsHexEditForm;
            frmJson = objPort.EDP_frmViewer as JSN_clsJsonEditForm;
            if (frmText != null)
            {   /* A text editor: its own descriptor marked, by the same rule. */
                frmText.TXE_SetSourceUnavailable(bUnavailable);
            }

            if (frmHex != null)
            {   /* A hex editor: likewise. */
                frmHex.HEXF_SetSourceUnavailable(bUnavailable);
            }

            if (frmJson != null)
            {   /* A structured editor: likewise. */
                frmJson.JSN_SetSourceUnavailable(bUnavailable);
            }
        }

        /*---------------------------------------------------------------------*/
        /* EUI_Save:                                                           */
        /*                                                                     */
        /* An editor's save, handed over by the main window (MainForm.cs       */
        /* Main_SaveOpenedFile).  An image entry's save becomes a SaveEditor   */
        /* request carrying a snapshot of the content, and the editor's        */
        /* answer waits for the worker's result.  A Windows file has already   */
        /* been written by the editor itself: what is sent is the fact         */
        /* (HostEditorSaved), and the editor is answered at once -- unless     */
        /* the write was the close question's Windows Save As, whose answer    */
        /* waits for the worker's acknowledgement.  A save made in answer to   */
        /* a close question goes to that question, never into the queue        */
        /* behind it.                                                          */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     objFile : the save's own descriptor (OFD_ForSave): where, which */
        /*               revision, its order, its stage (null when the         */
        /*               editor's work file is to be copied here), and the     */
        /*               close question it answers (null for none).            */
        /*     pfnDone : the editor's answer for this save; required, called   */
        /*               exactly once, here or from the result.                */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : answered now (a refusal, a failure, a Windows save) or   */
        /*            later (Eui_SaveResult).                                  */
        /*---------------------------------------------------------------------*/
        internal void EUI_Save(OFD_clsOpenedFile objFile, OFD_SaveDone pfnDone)
        {
            EDP_clsEditorPort objPort;
            STG_clsStage objStage;
            RQT_clsRequest objRequest;
            TGT_clsTarget objTarget;
            STV_clsStateView objState;
            SVP_clsSavePending objPending;
            PRS_clsPresentation objClose;
            ICV_clsInputView objInput;
            string sExpected;
            bool bSameFilesystem;
            bool bAccepted;

            if (objFile == null || !Eui_dictPorts.TryGetValue(objFile.OFD_lEditorId, out objPort))
            {   /* No descriptor, or an editor not registered (ended, or   */
                /* never): refused -- the worker could check such a save   */
                /* against no record.                                      */
                if (objFile != null)
                {   /* A descriptor: its stage, if it carries one, never left   */
                    /* this side, so it is deleted here and no longer named.    */
                    Eui_ReleaseLocal(objFile.OFD_objStage);
                    objFile.OFD_objStage = null;
                }

                /* The editor told "not saved"; its content stays unsaved in   */
                /* the window.                                                 */
                pfnDone(false);
                return;
            }

            /* The close question this save answers, if any: the one the        */
            /* editor captured when the save began (OFD_ForSave reads           */
            /* QWN_CurrentEditorQuestion), not whatever shows now.  Its         */
            /* context is an input view in the worker's Windows Save As phase   */
            /* (CommandSequences.cs Sys_CloseHostTarget); otherwise it is the   */
            /* ordinary close, Save / Discard / Cancel.                         */
            objClose = objFile.OFD_objQuestion;
            objInput = null;
            if (objClose != null)
            {   /* A close question: its context read (null unless it is the   */
                /* Windows Save As phase).                                     */
                objInput = objClose.PRS_objQuestion.QST_objContext as ICV_clsInputView;
            }

            if (objFile.OFD_bHostFile)
            {   /* A Windows file, already written by the editor.  What goes to   */
                /* the worker is the fact: this editor, its revision, the path    */
                /* and the save order (HBA_clsHostBindingArgs), which moves it    */
                /* into the worker's Windows set.  Nothing is written for it      */
                /* and no engine call made (Sys_HostBindingSequence).             */
                objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.HostEditorSaved,
                    objFile.OFD_lEditorId, null, null, null, objFile.OFD_lRevision, 0,
                    new HBA_clsHostBindingArgs(objFile.OFD_sImagePath, objFile.OFD_lSaveOrder), "", "");
                if (objInput != null)
                {   /* The close question's Windows Save As phase: this write   */
                    /* IS its answer.  The fact goes to the question (Retry,    */
                    /* carrying it), and the editor waits for the worker's      */
                    /* acknowledgement, which Sys_CloseHostTarget gives         */
                    /* whatever it makes of the fact.                           */
                    /*                                                          */
                    /* Recorded as pending, and its reply registered, BEFORE    */
                    /* the answer is given: the worker may publish the          */
                    /* acknowledgement the moment it accepts.  The pending      */
                    /* count makes a close wait for it (Eui_TryClose).  Then    */
                    /* the answer.                                              */
                    objPending = new SVP_clsSavePending();
                    objPending.SVP_objPort = objPort;
                    objPending.SVP_objFile = objFile;
                    objPending.SVP_pfnDone = pfnDone;
                    Eui_dictSaves.Add(objRequest, objPending);
                    objPort.EDP_iPending++;
                    Eui_objClient.CLI_RegisterReply(objRequest, Eui_OnSaveResult);
                    bAccepted = objClose.PRS_objQuestion.QST_Reply(objClose,
                        new ANS_clsAnswer(ANK_enmAnswerKind.Retry, objRequest));
                    if (!bAccepted)
                    {   /* Refused: the question was taken down or answered       */
                        /* meanwhile (an Exit replacing it, say).  The file was   */
                        /* written all the same, so the fact must still reach     */
                        /* the worker: the reply is moved to an ordinary post     */
                        /* of the same request, whose result answers this save.   */
                        /* The close it was meant for is not resumed by it.       */
                        Eui_objClient.CLI_AbandonReply(objRequest);
                        Eui_objClient.CLI_Post(objRequest, Eui_OnSaveResult);
                    }

                    /* Handed over (to the question, or posted): the editor's   */
                    /* answer comes with the result, not here.                  */
                    return;
                }

                /* An ordinary Windows save: the new binding applied at once,    */
                /* since the write is done.  The fact may still be queued when   */
                /* an image close starts, and that close applies queued facts    */
                /* before it decides which editors are its own                   */
                /* (Sys_FlushEditorBindings), so it finds this editor in the     */
                /* Windows set.                                                  */
                Eui_ApplySavedBinding(objPort, objFile);
                /* Whether this save was made from the ordinary close question   */
                /* shown now: then the editor closes itself after it (a          */
                /* deferred close), and the question waits for that close's      */
                /* return fact, matched by this save's order.  Any other save    */
                /* leaves no wait.                                               */
                objPort.EDP_bAwaitCloseReturn = objClose != null &&
                    objPort.EDP_objClose == objClose &&
                    objClose.PRS_objQuestion.QST_IsCurrent(objClose);
                if (objPort.EDP_bAwaitCloseReturn)
                {   /* That save's order: only its return fact ends the wait   */
                    /* (Eui_OnEditorFact).                                     */
                    objPort.EDP_lCloseReturnOrder = objFile.OFD_lSaveOrder;
                }
                /* The fact posted with no reply: the editor is answered here,   */
                /* and the write has happened whatever the worker makes of it.   */
                Eui_objClient.CLI_Post(objRequest, null);
                try
                {   /* The editor answered "saved" for exactly this revision,   */
                    /* then the Windows pane re-listed, since a file in it      */
                    /* changed.                                                 */
                    pfnDone(true);
                    if (Eui_pfnRefreshHost != null)
                    {   /* A re-list route given: run. */
                        Eui_pfnRefreshHost();
                    }
                }
                finally
                {   /* Whatever happened, this save's local work has returned:   */
                    /* POSTED, so a close waiting on it is looked at only        */
                    /* after this whole call stack -- the editor's save          */
                    /* handler included -- has unwound.                          */
                    EUI_PostEditorFact(objFile.OFD_lEditorId, EUI_LOCAL_RETURN);
                }

                /* Done: the editor wrote the file, and nothing waits for the   */
                /* worker.                                                      */
                return;
            }

            /* An image entry's save: the content as a stage the worker will    */
            /* own.  An editor that made its own snapshot hands it over in      */
            /* the descriptor; otherwise its work file is copied now, before    */
            /* control goes back to an editor that may change it.  Either way   */
            /* the descriptor no longer names the stage.                        */
            objStage = objFile.OFD_objStage;
            objFile.OFD_objStage = null;
            try
            {   /* A failure from here to the request is caught below, while   */
                /* the stage is still this side's to delete.                   */
                if (objStage == null)
                {   /* No snapshot handed over: the work file copied into the   */
                    /* staging folder (STG_CopySnapshot deletes its partial     */
                    /* file and rethrows on failure).                           */
                    objStage = STG_clsStage.STG_CopySnapshot(Eui_sStageRoot, objFile.OFD_sTempFile);
                }

                /* The request's facts, all from the save's own descriptor:      */
                /* its range; a state view of the filesystem it expects (open,   */
                /* this range, this epoch -- the worker checks it against what   */
                /* is open; the view's other values are false and 0); and the    */
                /* entry it expects to replace: the current path, or for a       */
                /* Save As the one saved to before the new target was chosen.    */
                objTarget = Eui_FileTarget(objFile);
                objState = new STV_clsStateView(MOD_enmMode.Open, objTarget,
                    objFile.OFD_lEpoch, false, false, false);
                sExpected = objFile.OFD_sImagePath;
                if (objFile.OFD_sExpectedEntry != null)
                {   /* A Save As: the entry saved to before the target was   */
                    /* chosen.                                               */
                    sExpected = objFile.OFD_sExpectedEntry;
                }

                /* Whether the save writes to the filesystem the editor saved    */
                /* to until now (both image entries, the same epoch, the same    */
                /* range) -- the worker checks the relationship itself before    */
                /* using it.  A Save As from a Windows file is never the same.   */
                bSameFilesystem = !objPort.EDP_objFile.OFD_bHostFile &&
                    objPort.EDP_objFile.OFD_lEpoch == objFile.OFD_lEpoch &&
                    Eui_FileTarget(objPort.EDP_objFile).TGT_SameRange(objTarget);
                /* The request: SaveEditor from this editor, its revision, the   */
                /* stage, the expected and new paths, overwrite false (it        */
                /* enters only the duplicate key -- the save asks itself         */
                /* before replacing an existing entry, Nst_Save) and the save    */
                /* order.                                                        */
                objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.SaveEditor,
                    objFile.OFD_lEditorId, objState, objTarget, null, objFile.OFD_lRevision, 0,
                    new SVA_clsSaveArgs(objStage, sExpected, objFile.OFD_sImagePath,
                        false, objFile.OFD_lSaveOrder, bSameFilesystem), "", "");
            }
            catch (Exception exStage)
            {   /* The snapshot or the request could not be made: nothing was     */
                /* sent.  The stage deleted (it never left this side), the        */
                /* editor told "not saved" -- its content stays in the window     */
                /* -- and the failure logged; a close question this save          */
                /* answered is answered Cancel with the reason, so the worker's   */
                /* close stops rather than waiting.  The report comes last.       */
                Eui_ReleaseLocal(objStage);
                pfnDone(false);
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("editor snapshot", exStage.ToString());
                }

                /* A close question this save answered: answered Cancel, with   */
                /* the reason; then the report, where a route was given.        */
                Eui_Reply(objClose, ANK_enmAnswerKind.Cancel, exStage.Message);
                if (Eui_pfnReport != null)
                {   /* A report route given: the reason shown (the exception's   */
                    /* message).                                                 */
                    Eui_pfnReport(objPort.EDP_frmViewer,
                        new RSL_clsResult(SYS_clsSystem.SYS_INTERNAL, exStage.Message, null));
                }

                /* Nothing was posted, and nothing else follows from this   */
                /* save.                                                    */
                return;
            }

            /* Recorded as pending under its request: the reply finds it     */
            /* there (Eui_SaveResult), and the pending count makes a close   */
            /* wait for it.                                                  */
            objPending = new SVP_clsSavePending();
            objPending.SVP_objPort = objPort;
            objPending.SVP_objFile = objFile;
            objPending.SVP_pfnDone = pfnDone;
            Eui_dictSaves.Add(objRequest, objPending);
            objPort.EDP_iPending++;
            if (objClose != null)
            {   /* It answers a close question: it goes to that question         */
                /* (Save, carrying the request), since the worker is waiting     */
                /* on it -- posted to the queue, the save would wait behind      */
                /* that very close.  The reply is registered first: the worker   */
                /* may publish the result the moment it accepts.                 */
                Eui_objClient.CLI_RegisterReply(objRequest, Eui_OnSaveResult);
                if (!objClose.PRS_objQuestion.QST_Reply(objClose,
                    new ANS_clsAnswer(ANK_enmAnswerKind.Save, objRequest)))
                {   /* Refused: the question was taken down or answered          */
                    /* meanwhile.  The request went to nobody, so its stage is   */
                    /* deleted here and the save answered Cancelled at once --   */
                    /* not posted, as a save its close no longer asks for.       */
                    Eui_objClient.CLI_AbandonReply(objRequest);
                    Eui_ReleaseLocal(objStage);
                    Eui_SaveResult(objRequest, new RSL_clsResult(SYS_clsSystem.SYS_CANCELLED,
                        "The close question was superseded.", null));
                }

                /* Accepted, the request and its stage are the worker's: it      */
                /* runs the save inside the close and releases the stage after   */
                /* (Sys_CloseEditors).                                           */
                return;
            }

            /* An ordinary save, posted with its reply.  The stage is the       */
            /* worker's from here whatever happens -- run, refused at           */
            /* posting, or dropped as a duplicate, it is released when the      */
            /* request is finished (NST_Release) -- and the reply comes every   */
            /* time (a refusal is answered while SYS_Post runs).                */
            Eui_objClient.CLI_Post(objRequest, Eui_OnSaveResult);
        }

        /*--------------------------------------------------------------------*/
        /* Eui_ApplySavedBinding:                                             */
        /*                                                                    */
        /* A save that succeeded becomes where the editor saves next --       */
        /* unless a newer one has already been applied: saves can be answered */
        /* out of the order they were made, and an older one's late success   */
        /* must not move a newer Save As destination back.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objPort : the editor's registration.                           */
        /*     objFile : the saved descriptor.                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : applied, or nothing for an older save.                  */
        /*--------------------------------------------------------------------*/
        private static void Eui_ApplySavedBinding(EDP_clsEditorPort objPort, OFD_clsOpenedFile objFile)
        {
            if (objFile.OFD_lSaveOrder >= objPort.EDP_lAcknowledgedOrder)
            {   /* The newest save applied so far (or one of the same order):   */
                /* its descriptor becomes the binding, its path the entry the   */
                /* next save expects to replace, and its order the one an       */
                /* older save must now beat.                                    */
                objFile.OFD_sExpectedEntry = objFile.OFD_sImagePath;
                objPort.EDP_objFile = objFile;
                objPort.EDP_lAcknowledgedOrder = objFile.OFD_lSaveOrder;
            }
        }

        /*--------------------------------------------------------------------*/
        /* Eui_OnSaveResult:                                                  */
        /*                                                                    */
        /* The reply callback an editor Save request is posted or             */
        /* registered with: the worker's outcome for it is acknowledged       */
        /* (Eui_SaveResult).                                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the request the reply belongs to.                 */
        /*     objResult  : the worker's outcome for it.                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the save answered once, its binding moved on success.   */
        /*--------------------------------------------------------------------*/
        private void Eui_OnSaveResult(RQT_clsRequest objRequest, RSL_clsResult objResult)
        {
            /* The worker's reply for this request. */
            Eui_SaveResult(objRequest, objResult);
        }

        /*--------------------------------------------------------------------*/
        /* Eui_SaveResult:                                                    */
        /*                                                                    */
        /* A save's outcome -- the worker's reply, or EUI_Save's own Cancel   */
        /* when a close question refused the save: the pending record         */
        /* removed, the binding moved on success, the editor answered, the    */
        /* Windows pane re-listed after a Windows save, and only then any     */
        /* failure or warning shown.  A second reply for the same request     */
        /* does nothing.                                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the save's request.                               */
        /*     objResult  : its outcome.                                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the save answered, or nothing when it already was.      */
        /*--------------------------------------------------------------------*/
        private void Eui_SaveResult(RQT_clsRequest objRequest, RSL_clsResult objResult)
        {
            SVP_clsSavePending objPending;
            EDP_clsEditorPort objPort;
            ESR_clsResult objNative;

            if (!Eui_dictSaves.TryGetValue(objRequest, out objPending))
            {   /* Not pending: a second or late reply, whose save has been   */
                /* answered.                                                  */
                return;
            }

            /* The record removed and the count lowered FIRST: the editor's    */
            /* answer and the report below can run a message loop (a message   */
            /* box), in which another reply could arrive -- it must not find   */
            /* this one still pending.                                         */
            Eui_dictSaves.Remove(objRequest);
            objPort = objPending.SVP_objPort;
            objPort.EDP_iPending--;
            if (objResult.RSL_iStatus == SYS_clsSystem.SYS_OK)
            {   /* Saved: the save's descriptor becomes the binding   */
                /* (Eui_ApplySavedBinding keeps a newer one).         */
                Eui_ApplySavedBinding(objPort, objPending.SVP_objFile);
            }

            try
            {   /* The editor answered first, so it settles exactly this   */
                /* revision before any message is shown.                   */
                objPending.SVP_pfnDone(objResult.RSL_iStatus == SYS_clsSystem.SYS_OK);

                /* A close's Windows Save As, acknowledged: the Windows pane   */
                /* re-listed, as EUI_Save does for an ordinary Windows save.   */
                if (objResult.RSL_iStatus == SYS_clsSystem.SYS_OK &&
                    objRequest.RQT_enmKind == KND_enmKind.HostEditorSaved && Eui_pfnRefreshHost != null)
                {   /* A Windows save acknowledged, with a re-list route: run. */
                    Eui_pfnRefreshHost();
                }

                /* A native save's result may carry a clean-up warning: the    */
                /* save succeeded, but something it left behind could not be   */
                /* removed (EngineSession.cs ESR_sWarning).                    */
                objNative = objResult.RSL_objPayload as ESR_clsResult;
                if (objResult.RSL_iStatus != SYS_clsSystem.SYS_OK &&
                    objResult.RSL_iStatus != SYS_clsSystem.SYS_CANCELLED &&
                    objResult.RSL_iStatus != SYS_clsSystem.SYS_DROPPED)
                {   /* A refusal or failure: logged and shown (the content stays   */
                    /* unsaved in the window).  A cancel is neither -- the user    */
                    /* or the worker chose it -- and a dropped duplicate's work    */
                    /* is carried by the earlier save.                             */
                    if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                    {   /* Only while the debug log is kept: otherwise the line is never built. */
                        DBGLOG_clsDebugLog.DBGLOG_Write("editor save", objResult.RSL_sDetail);
                    }

                    /* The reason shown, where a report route was given. */
                    if (Eui_pfnReport != null)
                    {   /* A report route given: the reason shown. */
                        Eui_pfnReport(objPort.EDP_frmViewer, objResult);
                    }
                }
                else if (objResult.RSL_iStatus == SYS_clsSystem.SYS_OK && objNative != null &&
                    !string.IsNullOrEmpty(objNative.ESR_sWarning) && Eui_pfnReport != null)
                {   /* Saved with a warning, and a report route: shown (the   */
                    /* report puts the warning in place of the detail).       */
                    Eui_pfnReport(objPort.EDP_frmViewer, objResult);
                }
            }
            finally
            {   /* Whatever the answer did, even if it threw: a close waiting on   */
                /* this pending count is looked at again, posted so it runs        */
                /* after this stack unwinds.  An editor that has ended is          */
                /* skipped there.                                                  */
                EUI_PostEditorFact(objPending.SVP_objFile.OFD_lEditorId, EUI_LOCAL_RETURN);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Eui_ReleaseLocal:                                                  */
        /*                                                                    */
        /* Delete a stage that never reached the worker, for a refusal or a   */
        /* failure.  A failed deletion is logged, not thrown: the caller is   */
        /* answering the save, and that answer matters more than the leftover */
        /* file.                                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objStage : the stage, or null for none.                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : deleted, or the failure logged.                         */
        /*--------------------------------------------------------------------*/
        private static void Eui_ReleaseLocal(STG_clsStage objStage)
        {
            if (objStage == null)
            {   /* No stage: nothing to delete. */
                return;
            }

            try
            {   /* Deleted (STG_Release deletes only the token's own file, and   */
                /* only once).                                                   */
                objStage.STG_Release();
            }
            catch (Exception exCleanup)
            {   /* It could not be deleted: logged, and the file stays in the   */
                /* staging folder.                                              */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("snapshot cleanup", exCleanup.ToString());
                }
            }
        }

        /* The editors' return facts, and the worker's close questions. */

        /*--------------------------------------------------------------------*/
        /* EUI_PostEditorFact:                                                */
        /*                                                                    */
        /* An editor's report that its own work has returned                  */
        /* (EUI_LOCAL_RETURN), or that the close it scheduled after a save    */
        /* has (that save's order), turned into a call of Eui_OnEditorFact    */
        /* POSTED to the window's message loop -- so it runs only after the   */
        /* whole call stack that reported it has unwound, with no timer or    */
        /* polling.  The editors call it through the receiver EUI_Register    */
        /* gave them; this file calls it after each save is settled.          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lEditor     : the editor's id.                                 */
        /*     lCloseOrder : the save order of the close that returned, or    */
        /*                   EUI_LOCAL_RETURN.                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : posted; nothing for an editor that has ended; a close   */
        /*            question answered Cancel when the window can no longer  */
        /*            take a post.                                            */
        /*--------------------------------------------------------------------*/
        internal void EUI_PostEditorFact(long lEditor, long lCloseOrder)
        {
            EDP_clsEditorPort objPort;

            if (!Eui_dictPorts.TryGetValue(lEditor, out objPort) || objPort.EDP_frmViewer.IsDisposed)
            {   /* Not registered, or its window disposed: nothing is left to   */
                /* resume.                                                      */
                return;
            }

            try
            {   /* Posted (BeginInvoke) to the window's own thread. */
                objPort.EDP_frmViewer.BeginInvoke(new Action<long, long>(Eui_OnEditorFact),
                    new object[] { lEditor, lCloseOrder });
            }
            catch (InvalidOperationException exHost)
            {   /* The window cannot take the post (BeginInvoke throws this     */
                /* without a window handle, as documented): a close question    */
                /* waiting on the fact would wait for ever, so it is answered   */
                /* Cancel with the reason, and the failure logged.              */
                Eui_Reply(objPort.EDP_objClose, ANK_enmAnswerKind.Cancel, exHost.Message);
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("editor completion", exHost.ToString());
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* Eui_OnEditorFact:                                                  */
        /*                                                                    */
        /* A posted return fact arrives: a close the worker is asking this    */
        /* editor to make is looked at again (Eui_TryClose).  When it is the  */
        /* return of the close a Windows save scheduled and the window is     */
        /* still there, the editor kept itself open (its own reason), so the  */
        /* worker is asked to look again (Retry) rather than told it closed.  */
        /* When it says the editor's own close question was answered Cancel   */
        /* (EUI_LOCAL_DECLINED), the editor declined to close: the worker's   */
        /* close question is answered Cancel, and its close stops.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lEditor     : the editor's id.                                 */
        /*     lCloseOrder : the save order of the close that returned, or    */
        /*                   EUI_LOCAL_RETURN, or EUI_LOCAL_DECLINED.         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the close question looked at; no engine work runs.      */
        /*--------------------------------------------------------------------*/
        private void Eui_OnEditorFact(long lEditor, long lCloseOrder)
        {
            EDP_clsEditorPort objPort;

            if (!Eui_dictPorts.TryGetValue(lEditor, out objPort))
            {   /* Ended already (its FormClosed ran): nothing to resume. */
                return;
            }

            /* The return of the close the matching save scheduled ends its   */
            /* wait.                                                          */
            if (objPort.EDP_bAwaitCloseReturn &&
                objPort.EDP_lCloseReturnOrder == lCloseOrder)
            {   /* That close has returned. */
                objPort.EDP_bAwaitCloseReturn = false;
                if (!objPort.EDP_frmViewer.IsDisposed)
                {   /* The window is still there: answered Retry, and the        */
                    /* worker checks again which editors and what content        */
                    /* remain (Sys_CloseEditors).  A window that did close was   */
                    /* answered Closed by its FormClosed.                        */
                    Eui_Reply(objPort.EDP_objClose, ANK_enmAnswerKind.Retry, "");
                }
            }

            if (lCloseOrder == EUI_LOCAL_DECLINED)
            {   /* The editor's own close question (shown editor-modal under   */
                /* the worker's) was answered Cancel: the worker's question    */
                /* answered Cancel too, so its close stops with the window     */
                /* and its content kept.  The editor posts this only while     */
                /* that display was current when the choice was applied, and   */
                /* a question re-shown since has a new display only after an   */
                /* Exit the user answers, which runs after this posted fact    */
                /* -- so the display here is that same one.  A display no      */
                /* longer current refuses the answer (Eui_Reply).              */
                Eui_Reply(objPort.EDP_objClose, ANK_enmAnswerKind.Cancel, "");
            }

            /* Then the close question, if any, looked at again. */
            Eui_TryClose(objPort);
        }

        /*--------------------------------------------------------------------*/
        /* Eui_LocalWork:                                                     */
        /*                                                                    */
        /* Whether an editor's own work is still on its call stack -- a       */
        /* write, a model change, its own close prompt, the structured        */
        /* editor's tree work or answers not yet applied (each editor's       */
        /* _HasLocalWork).  A close question is not shown while it is: that   */
        /* work can show a message of its own, and ends with a return fact    */
        /* (EUI_PostEditorFact) that looks at the close again.                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmViewer : the editor's window.                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true while its own work has not returned; false for any */
        /*            other window.                                           */
        /*--------------------------------------------------------------------*/
        private static bool Eui_LocalWork(Form frmViewer)
        {
            TXE_clsTextEditForm frmText;
            HEXF_clsHexEditForm frmHex;
            JSN_clsJsonEditForm frmJson;

            /* Which editor kind the window is (none of the three for any   */
            /* other window, which has no work of its own).                 */
            frmText = frmViewer as TXE_clsTextEditForm;
            frmHex = frmViewer as HEXF_clsHexEditForm;
            frmJson = frmViewer as JSN_clsJsonEditForm;

            /* Busy when its own test says so. */
            return((frmText != null && frmText.TXE_HasLocalWork()) ||
                (frmHex != null && frmHex.HEXF_HasLocalWork()) ||
                (frmJson != null && frmJson.JSN_HasLocalWork()));
        }

        /*--------------------------------------------------------------------*/
        /* EUI_AskImageClose:                                                 */
        /*                                                                    */
        /* The worker's EditorClose question, delivered                       */
        /* (CommandQuestionPresenter.cs): kept as the editor's current close  */
        /* display and looked at (Eui_TryClose), which shows it now or waits  */
        /* for the editor's work and pending saves.  Its context is a bool    */
        /* (the image is gone) for an ordinary close, or the input view of    */
        /* the Windows Save As phase.                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objPresentation : the question's display.                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : shown, kept waiting, or answered.                       */
        /*--------------------------------------------------------------------*/
        internal void EUI_AskImageClose(PRS_clsPresentation objPresentation)
        {
            EDP_clsEditorPort objPort;

            if (objPresentation == null ||
                !objPresentation.PRS_objQuestion.QST_IsCurrent(objPresentation))
            {   /* No display, or no longer the question's current one   */
                /* (answered, or shown again since): ignored.            */
                return;
            }

            if (!Eui_dictPorts.TryGetValue(objPresentation.PRS_objQuestion.QST_lWindow, out objPort))
            {   /* No editor with that id -- its construction failed before      */
                /* registration, so no window holds anything: answered Closed,   */
                /* there is nothing to close.                                    */
                Eui_Reply(objPresentation, ANK_enmAnswerKind.Closed, "");
                return;
            }

            /* Kept BEFORE anything is shown, so a nested message loop (a     */
            /* picker, a prompt) delivering a newer display of the question   */
            /* replaces this one rather than being overwritten by it.         */
            objPort.EDP_objClose = objPresentation;
            Eui_TryClose(objPort);
        }

        /*--------------------------------------------------------------------*/
        /* Eui_TryClose:                                                      */
        /*                                                                    */
        /* Show the worker's close question in its editor, once nothing of    */
        /* the editor's is in flight: its saves answered, its own work        */
        /* returned, a close it scheduled returned.  Until then it returns,   */
        /* and a return fact calls it again.  The question's answer comes     */
        /* from what the close then leads to, often after this call has       */
        /* returned; the body says each route at the line that waits for it.  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objPort : the editor's registration.                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : answered, left to an answer under way, or waiting for a */
        /*            return fact.                                            */
        /*--------------------------------------------------------------------*/
        private void Eui_TryClose(EDP_clsEditorPort objPort)
        {
            PRS_clsPresentation objPresentation;
            QST_clsQuestion objQuestion;
            ICV_clsInputView objInput;
            QSC_clsWindowScope objScope;
            bool bUnavailable;

            /* The display read once, onto this stack: a nested message loop   */
            /* below can replace the registration's (an Exit replacing the     */
            /* question), and this call goes on with its own.                  */
            objPresentation = objPort.EDP_objClose;
            if (objPresentation == null)
            {   /* No close question for this editor. */
                return;
            }

            /* The question behind the display. */
            objQuestion = objPresentation.PRS_objQuestion;
            if (!objQuestion.QST_IsCurrent(objPresentation))
            {   /* Answered or replaced since it was kept: forgotten, with no   */
                /* close started for it.                                        */
                if (objPort.EDP_objClose == objPresentation)
                {   /* Only while it is still the registration's display: a   */
                    /* newer one kept meanwhile stays.                        */
                    objPort.EDP_objClose = null;
                }

                /* Nothing to show for an expired display. */
                return;
            }

            if (objPort.EDP_bCloseDispatch)
            {   /* A Close() or Windows Save As of this editor is still on the   */
                /* stack below, and this call is nested inside it: that call     */
                /* posts a return fact as it unwinds (the finally below).        */
                return;
            }

            if (objPort.EDP_frmViewer.IsDisposed)
            {   /* Disposed, but its FormClosed has not ended the registration:   */
                /* disposal alone does not show that the editor let go of its     */
                /* files (its FormClosed handlers do that), so the baseline may   */
                /* not be deleted -- answered Cancel.                             */
                Eui_Reply(objPresentation, ANK_enmAnswerKind.Cancel,
                    "The editor was disposed without a closure acknowledgement.");
                return;
            }

            /* Which kind of close: the Windows Save As phase carries an   */
            /* input view; the ordinary close carries a bool.              */
            objInput = objQuestion.QST_objContext as ICV_clsInputView;
            if (objPort.EDP_iPending != 0)
            {   /* Saves of this editor are still unanswered. */
                if (objInput == null)
                {   /* An ordinary close: answered Retry.  The worker cancels     */
                    /* this editor's queued saves before it asks again            */
                    /* (Sys_CancelEditorSaves), so their answers arrive and the   */
                    /* count falls.                                               */
                    Eui_Reply(objPresentation, ANK_enmAnswerKind.Retry, "");
                }

                /* Nothing shown now.  In the Windows Save As phase the    */
                /* display is kept: the pending save is that phase's own   */
                /* write, whose acknowledgement resumes it.                */
                return;
            }

            if (objPort.EDP_bAwaitCloseReturn || Eui_LocalWork(objPort.EDP_frmViewer))
            {   /* The editor's own work, or the close a save scheduled, has   */
                /* not returned: its return fact calls this again.             */
                return;
            }

            if (objInput == null && objQuestion.QST_objRequest != null &&
                objQuestion.QST_objRequest.RQT_enmKind != KND_enmKind.Exit &&
                objPort.EDP_objFile.OFD_bHostFile)
            {   /* An image close (not an Exit) is asking an editor that now     */
                /* saves to a Windows file: its Save As to Windows finished      */
                /* after the question was asked.  Answered Retry, and the        */
                /* window stays open: the worker applies that fact and selects   */
                /* again, finding the editor in the Windows set.                 */
                Eui_Reply(objPresentation, ANK_enmAnswerKind.Retry, "");
                return;
            }

            /* The ordinary close's context: true when the editor's image is   */
            /* gone (CommandSequences.cs decides).  Marked on the editor, so   */
            /* a Save from its prompt is refused while the image is gone --    */
            /* only a Save As to Windows can keep the content then.            */
            bUnavailable = objQuestion.QST_objContext is bool &&
                (bool)objQuestion.QST_objContext;
            if (objInput == null)
            {   /* An ordinary close: marked (the Windows Save As phase is not   */
                /* about the image).                                             */
                Eui_SetUnavailable(objPort, bUnavailable);
            }

            /* The close shown, marked as on the stack so a nested delivery   */
            /* waits for it (above).                                          */
            objPort.EDP_bCloseDispatch = true;
            try
            {   /* A failure showing it is caught below and answers Cancel, so   */
                /* the worker is never left waiting.  Every message box shown    */
                /* inside is tracked against this display, so an Exit can take   */
                /* them down (QWN_Supersede).                                    */
                objScope = QWN_clsQuestionWindows.QWN_Begin(objPresentation);
                try
                {   /* The close itself, by its kind. */
                    if (objInput != null)
                    {   /* The Windows Save As phase: the main window's picker,   */
                        /* then the editor's own write, both returned before      */
                        /* this call does (Main_CloseWindowsInput).               */
                        if (Eui_pfnCloseWindows == null)
                        {   /* No route given: answered Cancel -- with no picker   */
                            /* there is no target and no write to claim.           */
                            Eui_Reply(objPresentation, ANK_enmAnswerKind.Cancel,
                                "The Windows Save As presentation endpoint is unavailable.");
                        }
                        else
                        {   /* Shown; a completed write answers the question   */
                            /* itself (EUI_Save, Retry carrying the fact).     */
                            Eui_pfnCloseWindows(objPresentation);
                        }
                    }
                    else
                    {   /* The ordinary close: the editor's own Close(), whose   */
                        /* prompt (Save / Save As / Discard / Cancel), shown     */
                        /* editor-modal, answers later through EUI_Save, the     */
                        /* window's FormClosed or the declined fact.  The wait   */
                        /* for a scheduled close is cleared first; a Windows     */
                        /* save from this prompt sets it again (EUI_Save).       */
                        objPort.EDP_bAwaitCloseReturn = false;
                        objPort.EDP_frmViewer.Close();
                    }
                }
                finally
                {   /* The call has returned: this display's message-box   */
                    /* tracking ended.                                     */
                    QWN_clsQuestionWindows.QWN_End(objScope);
                }

                if (objPort.EDP_objClose != objPresentation ||
                    !objQuestion.QST_IsCurrent(objPresentation))
                {   /* Answered meanwhile -- a save, a FormClosed or an Exit --   */
                    /* or replaced: nothing more for this display.                */
                    return;
                }

                if (objPort.EDP_frmViewer.IsDisposed)
                {   /* Disposed, but nothing answered for it: disposal alone   */
                    /* does not show that its files were let go -- answered    */
                    /* Cancel.                                                 */
                    Eui_Reply(objPresentation, ANK_enmAnswerKind.Cancel,
                        "The editor was disposed without a closure acknowledgement.");
                    return;
                }

                if (objInput != null)
                {   /* The Windows Save As phase returned with nothing        */
                    /* answering it -- a guard: its picker's own call         */
                    /* answers every outcome before it returns (Retry for a   */
                    /* cancelled pick, Cancel for a failure,                  */
                    /* Main_CloseWindowsInput).  Answered Cancel, and the     */
                    /* close stops.                                           */
                    Eui_Reply(objPresentation, ANK_enmAnswerKind.Cancel,
                        "Windows Save As did not complete.");
                    return;
                }

                if (objPort.EDP_bAwaitCloseReturn || objPort.EDP_iPending != 0)
                {   /* A save is still unanswered, or the close a Windows save   */
                    /* scheduled has not returned: their answers resume this     */
                    /* display.                                                  */
                    return;
                }

                if (Eui_LocalWork(objPort.EDP_frmViewer))
                {   /* The editor's own work is up -- its close question,       */
                    /* shown editor-modal, is still waiting for the user (the   */
                    /* Close() above returned with it open).  Nothing is        */
                    /* answered yet: the question's choice, applied by the      */
                    /* editor's own queue, answers this display -- a save, a    */
                    /* close (FormClosed), or Cancel (EUI_LOCAL_DECLINED) --    */
                    /* and its return fact looks at the close again.            */
                    return;
                }

                /* The editor is still open, nothing of its own is up and    */
                /* nothing answered: it declined to close (it kept itself    */
                /* open without a question, for a reason of its own).  Its   */
                /* content stays with the window, and the worker's close     */
                /* stops.                                                    */
                Eui_Reply(objPresentation, ANK_enmAnswerKind.Cancel, "");
            }
            catch (Exception exClose)
            {   /* Showing it failed: answered Cancel with the reason, and   */
                /* logged, so the worker is not left waiting.                */
                Eui_Reply(objPresentation, ANK_enmAnswerKind.Cancel, exClose.Message);
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("editor close", exClose.ToString());
                }
            }
            finally
            {   /* No longer on the stack.  A newer display kept while this       */
                /* call ran (a nested delivery waited, above) is looked at now,   */
                /* through a posted return fact.                                  */
                objPort.EDP_bCloseDispatch = false;
                if (objPort.EDP_objClose != objPresentation &&
                    objPort.EDP_objClose != null)
                {   /* A newer display is waiting. */
                    EUI_PostEditorFact(objPort.EDP_objFile.OFD_lEditorId, EUI_LOCAL_RETURN);
                }
            }
        }

        /* The worker's wait for editors still being made, and an Exit's   */
        /* cancel of the editors' snapshot jobs.                           */

        /*--------------------------------------------------------------------*/
        /* EUI_BeginViewerConstruction:                                       */
        /*                                                                    */
        /* An editor window is being made (MainCommand.cs, around the making  */
        /* and registering of each command viewer): counted, so the worker's  */
        /* wait for editors still being made (EUI_AskViewsReady) is not       */
        /* answered meanwhile.  A construction can run a message loop of its  */
        /* own before the window is registered, and an image close deciding   */
        /* which editors exist then would miss it.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : counted; the caller ends it with                        */
        /*            EUI_EndViewerConstruction in a finally.                 */
        /*--------------------------------------------------------------------*/
        internal void EUI_BeginViewerConstruction()
        {
            /* Counted (checked: an overflow throws rather than wrapping). */
            Eui_iConstructing = checked(Eui_iConstructing + 1);
        }

        /*--------------------------------------------------------------------*/
        /* EUI_EndViewerConstruction:                                         */
        /*                                                                    */
        /* An editor window's making has returned (registered or refused):    */
        /* uncounted, and the worker's wait answered once none is left.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : uncounted; an InvalidOperationException for an end with */
        /*            no begin.                                               */
        /*--------------------------------------------------------------------*/
        internal void EUI_EndViewerConstruction()
        {
            if (Eui_iConstructing <= 0)
            {   /* An end with no begin: refused -- counting it would let a   */
                /* real construction's end answer the wait too early.         */
                throw new InvalidOperationException("Viewer construction acknowledgement is unbalanced.");
            }

            /* Uncounted, then the wait looked at. */
            Eui_iConstructing--;
            Eui_TryViewsReady();
        }

        /*--------------------------------------------------------------------*/
        /* EUI_AskViewsReady:                                                 */
        /*                                                                    */
        /* The worker's EditorsReady question (asked before a close decides   */
        /* which editors to close, CommandSequences.cs Sys_CloseEditors):     */
        /* kept, and answered Closed once no editor window is being made.  It */
        /* is never shown, and decides nothing about content.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objPresentation : the question's display.                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : answered now, or kept until the count reaches 0.        */
        /*--------------------------------------------------------------------*/
        internal void EUI_AskViewsReady(PRS_clsPresentation objPresentation)
        {
            if (objPresentation == null ||
                !objPresentation.PRS_objQuestion.QST_IsCurrent(objPresentation))
            {   /* No display, or no longer the current one: ignored. */
                return;
            }

            /* Kept (a newer one replaces an older), then looked at. */
            Eui_objViewsReady = objPresentation;
            Eui_TryViewsReady();
        }

        /*--------------------------------------------------------------------*/
        /* Eui_TryViewsReady:                                                 */
        /*                                                                    */
        /* Answer the kept EditorsReady question Closed, once: when one is    */
        /* kept and no editor window is being made.                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : answered, or nothing yet.                               */
        /*--------------------------------------------------------------------*/
        private void Eui_TryViewsReady()
        {
            PRS_clsPresentation objPresentation;

            if (Eui_iConstructing != 0 || Eui_objViewsReady == null)
            {   /* An editor still being made (its end calls this again), or   */
                /* nothing kept.                                               */
                return;
            }

            /* Taken off BEFORE the answer, so a message loop inside it   */
            /* cannot answer it twice.                                    */
            objPresentation = Eui_objViewsReady;
            Eui_objViewsReady = null;
            Eui_Reply(objPresentation, ANK_enmAnswerKind.Closed, "");
        }

        /*--------------------------------------------------------------------*/
        /* Eui_Reply:                                                         */
        /*                                                                    */
        /* Answer a question through its display: Closed, Cancel or Retry,    */
        /* with a reason when there is one.  The question decides whether the */
        /* display may still answer -- QST_Reply refuses a late or replaced   */
        /* one -- and nothing is started here.  An answer carrying a save is  */
        /* made in EUI_Save.                                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objPresentation : the display, or null for none.               */
        /*     enmKind         : the answer.                                  */
        /*     sDetail         : the reason, or "" for none.                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : answered, or refused by the question.                   */
        /*--------------------------------------------------------------------*/
        private static void Eui_Reply(PRS_clsPresentation objPresentation,
            ANK_enmAnswerKind enmKind, string sDetail)
        {
            if (objPresentation != null)
            {   /* A display given: answered.  A refusal is ignored -- a late   */
                /* answer is simply not the one that counts.                    */
                objPresentation.PRS_objQuestion.QST_Reply(objPresentation,
                    new ANS_clsAnswer(enmKind, null, sDetail));
            }
        }

        /*---------------------------------------------------------------------*/
        /* EUI_AbortSnapshots:                                                 */
        /*                                                                     */
        /* After an Exit was agreed (CommandQuestionPresenter.cs, its accepted */
        /* Yes): every editor's snapshot jobs still running on this side --    */
        /* the hex and structured editors' (SJB_Abort) -- told to stop, so a   */
        /* late snapshot is deleted rather than handed to a worker that is     */
        /* stopping.  A save already handed over is the worker's and is not    */
        /* touched; the text editor makes no jobs (its snapshot is copied in   */
        /* EUI_Save).                                                          */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     None.                                                           */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : the jobs told; each still reports its own return.        */
        /*---------------------------------------------------------------------*/
        internal void EUI_AbortSnapshots()
        {
            Dictionary<long, EDP_clsEditorPort>.ValueCollection.Enumerator enmPorts;
            HEXF_clsHexEditForm frmHex;
            JSN_clsJsonEditForm frmJson;

            /* Every registration's editor told.  A walk of the table itself:   */
            /* the aborts set flags only and show nothing, so the table         */
            /* cannot change under it.                                          */
            enmPorts = Eui_dictPorts.Values.GetEnumerator();
            try
            {   /* The finally disposes the enumerator, as a foreach would. */
                while (enmPorts.MoveNext())
                {   /* One registration per iteration; only these two editor   */
                    /* kinds run snapshot jobs.                                */
                    frmHex = enmPorts.Current.EDP_frmViewer as HEXF_clsHexEditForm;
                    frmJson = enmPorts.Current.EDP_frmViewer as JSN_clsJsonEditForm;
                    if (frmHex != null)
                    {   /* A hex editor: its jobs told to stop. */
                        frmHex.HEXF_AbortStaging();
                    }

                    if (frmJson != null)
                    {   /* A structured editor: its file-stage jobs told to   */
                        /* stop; its model work ends at its own return.       */
                        frmJson.JSN_AbortStaging();
                    }
                }
            }
            finally
            {   /* The walk ended. */
                enmPorts.Dispose();
            }
        }
    }
}
