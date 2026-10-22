/*----------------------------------------------------------------------------*/
/* ImageFolderForm.cs                                                         */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The image folder picker: a modal dialog that shows the open image's        */
/* folders as a tree and hands back the one chosen, for the main window's     */
/* "choose a folder in the image" input (MainCommand.cs,                      */
/* Main_RenderMainFolderInput).  The tree is filled lazily: a folder is       */
/* listed only when it is first opened, and shows folders only.               */
/*                                                                            */
/* The dialog reads nothing itself.  Every listing is a request posted to the */
/* command worker, bound to the open image as it was when the input was       */
/* accepted, and its answer is run on the main window's loop (the command     */
/* client posts it there).  When the dialog is shown, ONE request lists the   */
/* root and each folder down to the starting folder (CommandPickerBranch.cs), */
/* so the tree opens there; after that, each folder the user opens is a       */
/* request of its own.  A dialog that has closed ignores every answer still   */
/* to come.                                                                   */
/*----------------------------------------------------------------------------*/

/* The framework: the base types (the exception the constructor throws,         */
/* the path split, ordinal comparison), the table of pending folder reads and   */
/* the list of names, the dialog font and sizes, and the dialog and its         */
/* controls.                                                                    */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* IFN_clsNodeRead -- one folder listing the user asked for and is still  */
    /* waiting on: the request posted and the tree node it is for, kept       */
    /* together so the answer goes to that node and no other -- documentation */
    /* table.                                                                 */
    /*------------------------------------------------------------------------*/
    internal class IFN_clsNodeRead
    {
        internal RQT_clsRequest IFN_objRequest;     // the request posted; the answer is matched to it by the object itself, so an older request for the same folder cannot pass for it
        internal TreeNode IFN_nodDirectory;         // the node the listing fills; one no longer in the tree when the answer comes is left alone
    }

    /*------------------------------------------------------------------------*/
    /* IFP_clsImageFolderForm -- the window that browses an image's folders   */
    /* to choose one.  A plain Form: its base class is declared here with the */
    /* rest of its UI.                                                        */
    /*                                                                        */
    /* Derives from Form because the framework requires it: WinForms makes a  */
    /* window only as a Form subclass.  That is the one kind of inheritance   */
    /* allowed here -- from a framework base class, never from a class of     */
    /* ours.                                                                  */
    /*------------------------------------------------------------------------*/
    internal sealed class IFP_clsImageFolderForm : Form
    {
        /* The dialog's geometry and the placeholder's text --         */
        /* documentation table.  The dialog is resizable down to its   */
        /* minimum, so a deep tree can be given more room.             */
        private const int    IFP_WIDTH       = 420;              // first client width, pixels
        private const int    IFP_HEIGHT      = 480;              // first client height, pixels
        private const int    IFP_MIN_WIDTH   = 280;              // smallest window width, pixels
        private const int    IFP_MIN_HEIGHT  = 240;              // smallest window height, pixels
        private const int    IFP_PAD         = 12;               // padding round the content, pixels
        private const string IFP_PLACEHOLDER = "(reading ...)";  // the text of a directory's stand-in child until it has been listed (never seen: a node is expanded only once it is listed)

        /* The outcome, the two fields the caller reads once ShowDialog   */
        /* has returned -- documentation table.                           */
        internal bool   IFP_bAccepted;         // true when OK was pressed; false for Cancel or a closed dialog
        internal string IFP_sPath;             // the chosen image directory, valid when IFP_bAccepted; "" otherwise

        /* The dialog's controls -- documentation table. */
        private Label           Ifp_lblPrompt;     // what is being asked for; also where a failed listing is reported
        private TreeView        Ifp_treFolders;    // the image's directories
        private FlowLayoutPanel Ifp_pnlButtons;    // OK and Cancel, right-aligned
        private Button          Ifp_btnOk;         // accepts the selected directory
        private Button          Ifp_btnCancel;     // abandons the choice

        /* Where the listings come from and what is still awaited --    */
        /* documentation table.  All of it lives only as long as this   */
        /* dialog does.                                                 */
        private readonly CLI_clsClient Ifp_objClient;                         // the main window's command client, which every listing is posted through; borrowed, never disposed here
        private readonly STV_clsStateView Ifp_objSource;                      // the open image as it stood when the input was accepted (its target and epoch); every listing is bound to it, so a listing of an image opened since is refused by the worker
        private readonly long Ifp_lWindow;                                    // the input's id (negative, from CLI_NewWindowId): the origin every listing carries, and the id the main window registered the dialog under
        private string[] Ifp_arrStart;                                        // the starting folder's names, root first ("/home/pi" gives "home", "pi"); set once, in the constructor
        private readonly Dictionary<string, IFN_clsNodeRead> Ifp_dictLatest;  // the user's folder listings still awaited, one per folder path (case-sensitive): the newest request for that folder and its node
        private RQT_clsRequest Ifp_objBranch;                                 // the opening request (the root down to the start) while it is awaited; null once answered, or closed
        private bool Ifp_bClosed;                                             // true once the dialog has closed: answers still to come touch nothing
        private bool Ifp_bStartupSuperseded;                                  // true once the user has opened an unlisted folder: the opening request's answer is then ignored, so it does not move the selection the user has taken over

        /*--------------------------------------------------------------------*/
        /* IFP_clsImageFolderForm:                                            */
        /*                                                                    */
        /* Build the dialog for one accepted folder input: the tree with      */
        /* only its root, the prompt and the two buttons.  Nothing is read    */
        /* yet; the first listing is posted once the dialog is shown.  The    */
        /* image the listings are bound to is fixed here, so nothing the      */
        /* panes do meanwhile can point them at another one.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     fntDialog   : the dialog font, the caller's (not disposed      */
        /*                   here).                                           */
        /*     objClient   : the command client every listing is posted       */
        /*                   through.                                         */
        /*     objSource   : the open image as the input was accepted over.   */
        /*     lWindow     : the input's id, negative; the origin of every    */
        /*                   listing.                                         */
        /*     sStartDir   : the image folder the tree opens at ("/" for      */
        /*                   the root).                                       */
        /*     imlIcons    : the icon list for the nodes -- the dialogs'      */
        /*                   list, not a pane's, which may be replaced while  */
        /*                   the dialog is open.                              */
        /*     iFolderIcon : the folder icon's index in it.                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the dialog, not yet shown; throws              */
        /*                     ArgumentException when the client, the image   */
        /*                     or its target is missing, or the id is not     */
        /*                     negative.                                      */
        /*--------------------------------------------------------------------*/
        internal IFP_clsImageFolderForm(Font fntDialog, CLI_clsClient objClient,
            STV_clsStateView objSource, long lWindow, string sStartDir,
            ImageList imlIcons, int iFolderIcon)
        {
            TreeNode nodRoot;

            if (objClient == null || objSource == null ||
                objSource.STV_objTarget == null || lWindow >= 0)
            {   /* No client to post through, no image to list, or an id that   */
                /* is not an input's: the dialog could list nothing, and        */
                /* nothing here could put that right, so it refuses to be       */
                /* made.  The caller's catch shows the message                  */
                /* (Main_RenderMainFolderInput).                                */
                throw new ArgumentException("The folder picker needs its accepted command source and input identity.");
            }

            /* The client, the image and the id, kept before any control    */
            /* exists.  The worker checks each listing against this image   */
            /* when it takes it, and refuses one for an image no longer     */
            /* open.  The table of awaited listings compares paths          */
            /* ordinally: ext folder names are case-sensitive.              */
            Ifp_objClient = objClient;
            Ifp_objSource = objSource;
            Ifp_lWindow = lWindow;
            Ifp_dictLatest = new Dictionary<string, IFN_clsNodeRead>(StringComparer.Ordinal);

            /* Nothing chosen (only OK sets the outcome), nothing closed,   */
            /* the user not yet in charge of the tree, and no opening       */
            /* request awaited -- Shown posts it.                           */
            IFP_bAccepted = false;
            IFP_sPath = "";
            Ifp_bClosed = false;
            Ifp_bStartupSuperseded = false;
            Ifp_objBranch = null;

            /* The starting folder's names, root first, split once on "/"   */
            /* with the empty pieces dropped (so "/" gives none, and a      */
            /* doubled or trailing "/" changes nothing).  The opening       */
            /* request is built from them and its answer is matched         */
            /* against them, name by name.                                  */
            Ifp_arrStart = sStartDir.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            /* The caller's dialog font.  Controls take it on when they are   */
            /* added below (the ambient font, as found) -- which is why the   */
            /* prompt's height is measured only after that.                   */
            this.Font = fntDialog;

            /* A resizable dialog (no border style is set) with a minimum,   */
            /* centred on its owner, not in the taskbar, without minimise    */
            /* or maximise; the padding is the space round the content.      */
            this.Text          = "Choose a folder in the image";
            this.MinimizeBox   = false;
            this.MaximizeBox   = false;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Padding       = new Padding(IFP_PAD);
            this.MinimumSize   = new Size(IFP_MIN_WIDTH, IFP_MIN_HEIGHT);

            /* The buttons, each sizing itself to its text and recording      */
            /* its outcome in its own click handler.  The row fills right     */
            /* to left, so Cancel, made and added first, ends up rightmost.   */
            Ifp_btnCancel          = new Button();
            Ifp_btnCancel.Text     = "Cancel";
            Ifp_btnCancel.AutoSize = true;
            Ifp_btnCancel.Click   += Ifp_OnCancelClicked;

            /* OK, to Cancel's left. */
            Ifp_btnOk          = new Button();
            Ifp_btnOk.Text     = "OK";
            Ifp_btnOk.AutoSize = true;
            Ifp_btnOk.Click   += Ifp_OnOkClicked;

            /* The row that holds them: docked along the bottom, filling     */
            /* from the right, as tall as the buttons, with a pad above to   */
            /* part it from the tree.                                        */
            Ifp_pnlButtons               = new FlowLayoutPanel();
            Ifp_pnlButtons.Dock          = DockStyle.Bottom;
            Ifp_pnlButtons.FlowDirection = FlowDirection.RightToLeft;
            Ifp_pnlButtons.AutoSize      = true;
            Ifp_pnlButtons.Padding       = new Padding(0, IFP_PAD, 0, 0);
            Ifp_pnlButtons.Controls.Add(Ifp_btnCancel);
            Ifp_pnlButtons.Controls.Add(Ifp_btnOk);

            /* The tree, filling the middle.  Every node shows the one        */
            /* folder icon (selected or not -- the program's own folder,      */
            /* from the dialogs' list the caller passes); the                 */
            /* selection stays visible when the tree loses the focus to a     */
            /* button; the path separator is the image's "/" (though this     */
            /* file never reads FullPath -- each node's Tag carries its       */
            /* path); and expanding is intercepted so an unlisted directory   */
            /* is listed first.                                               */
            Ifp_treFolders                    = new TreeView();
            Ifp_treFolders.Dock               = DockStyle.Fill;
            Ifp_treFolders.HideSelection      = false;
            Ifp_treFolders.ImageList          = imlIcons;
            Ifp_treFolders.ImageIndex         = iFolderIcon;
            Ifp_treFolders.SelectedImageIndex = iFolderIcon;
            Ifp_treFolders.PathSeparator      = "/";
            Ifp_treFolders.BeforeExpand      += Ifp_OnBeforeExpand;

            /* The root, its Tag the image path "/", with its placeholder    */
            /* child so it shows an expand mark; selected, so OK means the   */
            /* root until the opening below selects deeper.                  */
            nodRoot     = new TreeNode("/");
            nodRoot.Tag = "/";
            nodRoot.Nodes.Add(new TreeNode(IFP_PLACEHOLDER));
            Ifp_treFolders.Nodes.Add(nodRoot);
            Ifp_treFolders.SelectedNode = nodRoot;

            /* The prompt line, along the top; its height is set below,   */
            /* once it has the dialog font.                               */
            Ifp_lblPrompt           = new Label();
            Ifp_lblPrompt.Dock      = DockStyle.Top;
            Ifp_lblPrompt.AutoSize  = false;
            Ifp_lblPrompt.Text      = "Folder to show in the image pane:";
            Ifp_lblPrompt.TextAlign = ContentAlignment.MiddleLeft;

            /* Docking runs in reverse order of adding (the framework's       */
            /* layout order, as found): the buttons take the bottom, the      */
            /* prompt the top, and the tree fills what they leave.  Added     */
            /* first, the tree is also first in the tab order, so the arrow   */
            /* keys work in it from the start.                                */
            this.Controls.Add(Ifp_treFolders);
            this.Controls.Add(Ifp_lblPrompt);
            this.Controls.Add(Ifp_pnlButtons);

            /* The prompt's height is measured only now, once it has its   */
            /* parent and so the larger font; before, it would be the      */
            /* default's and clip the line.                                */
            Ifp_lblPrompt.Height = Ifp_lblPrompt.PreferredHeight + IFP_PAD;

            /* Enter presses OK and Escape Cancel, through their click   */
            /* handlers; the first size, which the user may change.      */
            this.AcceptButton = Ifp_btnOk;
            this.CancelButton = Ifp_btnCancel;
            this.ClientSize   = new Size(IFP_WIDTH, IFP_HEIGHT);

            /* The three events the dialog works from: shown (the first   */
            /* listing, which needs the window to exist), resized (keep   */
            /* the selection in view), closed (drop late listings).       */
            this.Shown      += Ifp_OnShown;
            this.Resize     += Ifp_OnResize;
            this.FormClosed += Ifp_OnFormClosed;
        }

        /*------------------------------------------------------------------*/
        /* Ifp_OnShown:                                                     */
        /*                                                                  */
        /* The dialog is on screen: post the opening request.  Done here,   */
        /* not in the constructor, so that its answer -- run on the main    */
        /* window's loop, and painting this dialog's tree -- finds the      */
        /* dialog and its tree already on screen.  (That an earlier answer  */
        /* would go wrong is not established; this is the order chosen.)    */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objSender : the dialog; unused.                              */
        /*     evArgs    : unused.                                          */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the opening request is posted.                        */
        /*------------------------------------------------------------------*/
        private void Ifp_OnShown(object objSender, EventArgs evArgs)
        {
            /* The root and every folder down to the start, as one request. */
            Ifp_LoadBranch();
        }

        /*------------------------------------------------------------------*/
        /* Ifp_LoadBranch:                                                  */
        /*                                                                  */
        /* Post the opening request: the root and each folder down to the   */
        /* starting folder's parent, listed by the worker in turn as ONE    */
        /* command (CommandPickerBranch.cs), so the tree can be opened down */
        /* to the start in one answer, with no request posted from inside   */
        /* another's answer.  The starting folder itself is not listed: it  */
        /* is selected in its parent's listing, not opened.                 */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     None.                                                        */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the request is posted; Ifp_OnBranchListed takes its   */
        /*            answer.                                               */
        /*------------------------------------------------------------------*/
        private void Ifp_LoadBranch()
        {
            List<string> lstPaths;
            string sPath;
            int i;

            /* The folders to list, root first: the root always, then each   */
            /* parent on the way to the start -- every name but the last,    */
            /* since the start is only selected in its parent's listing.     */
            /* A start of "/" (no names) gives the root alone.               */
            lstPaths = new List<string>();
            lstPaths.Add("/");
            sPath = "";
            for (i = 0; i + 1 < Ifp_arrStart.Length; i++)
            {   /* One parent per iteration: the path so far, one name deeper. */
                sPath += "/" + Ifp_arrStart[i];
                lstPaths.Add(sPath);
            }

            /* A PickerBranch request: this input's id as its origin, the    */
            /* accepted image, the folders, and BRV_ALL_DIRECTORIES so       */
            /* each listing keeps folders only.  Its last text names its     */
            /* stream ("initial-branch"): with the kind, origin and epoch    */
            /* it is the key the worker's duplicate and replacement tests    */
            /* compare (CommandSystem.cs, Sys_SameStream), so only another   */
            /* opening request of this dialog could meet it there.  Kept     */
            /* in Ifp_objBranch, which is how its answer is recognised.      */
            /* Posted with no user-initiated flag, so a busy worker does     */
            /* not turn it away (CLI_Post); a refusal still comes back       */
            /* through the answer.                                           */
            Ifp_objBranch = CFC_clsCommands.CFC_Create(KND_enmKind.PickerBranch,
                Ifp_lWindow, Ifp_objSource, Ifp_objSource.STV_objTarget,
                lstPaths.ToArray(), 0, BRV_clsBranchView.BRV_ALL_DIRECTORIES,
                null, "", "initial-branch");
            Ifp_objClient.CLI_Post(Ifp_objBranch, Ifp_OnBranchListed);
        }

        /*------------------------------------------------------------------*/
        /* Ifp_OnBranchListed:                                              */
        /*                                                                  */
        /* The opening request's answer, on the main window's loop: fill    */
        /* the tree from the root down, folder by folder, and select the    */
        /* starting folder -- or the deepest folder on the way to it that   */
        /* exists and could be listed.  It posts nothing: every listing it  */
        /* draws is already in the answer.                                  */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objRequest : the request answered.                           */
        /*     objResult  : its result: a BRV_clsBranchView of the folders  */
        /*                  listed, root first (the last one the failed     */
        /*                  one when a read failed or was cancelled), or    */
        /*                  no branch at all when the request never ran (a  */
        /*                  refusal, a drop, a cancel before it started).   */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the tree filled as far as the answer goes; a failure  */
        /*            said in the prompt line.                              */
        /*------------------------------------------------------------------*/
        private void Ifp_OnBranchListed(RQT_clsRequest objRequest, RSL_clsResult objResult)
        {
            BRV_clsBranchView objBranch;
            TreeNode nodCurrent;
            TreeNode nodNext;
            TreeNode nodChild;
            int i;
            int j;

            if (Ifp_bClosed || IsDisposed || Ifp_objBranch != objRequest || Ifp_bStartupSuperseded)
            {   /* The dialog has closed, this is not the opening request now   */
                /* awaited, or the user has taken over the tree by opening a    */
                /* folder: the answer would only move the selection under the   */
                /* user, so it is dropped unread.                               */
                return;
            }

            /* Answered: no longer awaited, cleared BEFORE the tree is        */
            /* filled, so that nothing run while it is filled could take      */
            /* this answer a second time.  (What could re-enter here is not   */
            /* established; the clear costs nothing.)  Then the branch out    */
            /* of the answer.                                                 */
            Ifp_objBranch = null;
            objBranch = objResult.RSL_objPayload as BRV_clsBranchView;
            if (objBranch == null)
            {   /* No branch: the request never ran. */
                if (objResult.RSL_iStatus != SYS_clsSystem.SYS_CANCELLED &&
                    objResult.RSL_iStatus != SYS_clsSystem.SYS_DROPPED)
                {   /* Refused (or failed some other way): the reason goes in   */
                    /* the prompt line.  A cancel or a drop is said nowhere:    */
                    /* the user did not ask for this request, and a drop        */
                    /* means another copy of it carries the work.               */
                    Ifp_lblPrompt.Text = objResult.RSL_sDetail;
                }

                /* The root keeps its placeholder, so opening it later lists   */
                /* it as any folder is.                                        */
                return;
            }

            /* The walk down the start's path, from the root node. */
            nodCurrent = Ifp_treFolders.Nodes[0];
            for (i = 0; i < objBranch.BRV_lstFolders.Count; i++)
            {   /* One listed folder per iteration: fill its node, then find   */
                /* and select the next name of the path among its children.    */
                if (!String.Equals((string)nodCurrent.Tag,
                    objBranch.BRV_lstFolders[i].FVW_sFolder, StringComparison.Ordinal))
                {   /* The listing is not of the node reached: the answer     */
                    /* does not follow the path the request was built from.   */
                    /* A guard -- the branch lists exactly the paths it was   */
                    /* given, in order (CommandPickerBranch.cs) -- that       */
                    /* refuses to put one folder's children under another.    */
                    Ifp_lblPrompt.Text = "The initial directory path could not be matched.";
                    return;
                }

                /* The node filled from its listing.  A listing that failed   */
                /* (the last one, when any did) leaves the node as it was,    */
                /* says why in the prompt, and ends the walk.  The second     */
                /* test holds only for a start of "/" (no names): the root    */
                /* is filled and nothing below it is to be selected.          */
                /* Otherwise the branch has one listing per name, and the     */
                /* loop ends on its own once the start, the last name, has    */
                /* been selected in its parent's listing.                     */
                if (!Ifp_PaintNode(nodCurrent, objBranch.BRV_lstFolders[i].FVW_objResult) ||
                    i >= Ifp_arrStart.Length)
                {   /* Failed, or nothing deeper to select: the walk ends. */
                    return;
                }

                /* The next name among this folder's children, matched       */
                /* exactly: ext names are case-sensitive, so "Home" is not   */
                /* "home".  Only this node's own children are looked at.     */
                nodNext = null;
                for (j = 0; j < nodCurrent.Nodes.Count; j++)
                {   /* One child per iteration. */
                    nodChild = nodCurrent.Nodes[j];
                    if (nodChild.Text == Ifp_arrStart[i])
                    {   /* Found: no need to look further. */
                        nodNext = nodChild;
                        break;
                    }
                }

                if (nodNext == null)
                {   /* No such folder (removed or renamed since the start was   */
                    /* given): the deepest folder found stays selected, and     */
                    /* nothing is said.                                         */
                    return;
                }

                /* Select it and scroll it into view.  Its parent is already   */
                /* open (filled above), so nothing is read; the next pass      */
                /* fills it in turn when the answer listed it.                 */
                Ifp_treFolders.SelectedNode = nodNext;
                nodNext.EnsureVisible();
                nodCurrent = nodNext;
            }
        }

        /*------------------------------------------------------------------*/
        /* Ifp_NeedsLoading:                                                */
        /*                                                                  */
        /* Whether a folder's node has not been listed yet: it still holds  */
        /* only its placeholder.  A folder listed and found empty has no    */
        /* children at all, so it is not listed again.                      */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     nodDir : a folder's node.                                    */
        /*                                                                  */
        /* Returns:                                                         */
        /*     bool : true while the placeholder is its one child.          */
        /*------------------------------------------------------------------*/
        private static bool Ifp_NeedsLoading(TreeNode nodDir)
        {
            /* The placeholder is told by its Tag, not its text: every real   */
            /* child carries its path in Tag, so a folder that happens to     */
            /* be named "(reading ...)" is not taken for one.                 */
            return(nodDir.Nodes.Count == 1 && nodDir.Nodes[0].Tag == null);
        }

        /*--------------------------------------------------------------------*/
        /* Ifp_OnBeforeExpand:                                                */
        /*                                                                    */
        /* A node is about to open.  An unlisted one is held shut and its     */
        /* listing requested -- it opens when the listing has filled it --    */
        /* and the user, having opened a folder, now has charge of the tree   */
        /* (the opening request's answer is ignored from here).  A node       */
        /* already listed opens as usual, which is also what happens when a   */
        /* listing's answer opens the node it filled.                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the tree; unused.                                  */
        /*     evExpand  : the node, and the Cancel flag that holds it shut.  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the node opens, or is held shut with its listing        */
        /*            posted.                                                 */
        /*--------------------------------------------------------------------*/
        private void Ifp_OnBeforeExpand(object objSender, TreeViewCancelEventArgs evExpand)
        {
            if (Ifp_NeedsLoading(evExpand.Node))
            {   /* Not listed yet: kept shut so the placeholder is never    */
                /* shown, the opening request set aside, and the listing    */
                /* posted.  Opening it again while the listing is awaited   */
                /* posts it again, and only the newer answer is used.       */
                evExpand.Cancel = true;
                Ifp_bStartupSuperseded = true;
                Ifp_LoadNode(evExpand.Node);
            }
        }

        /*------------------------------------------------------------------*/
        /* Ifp_LoadNode:                                                    */
        /*                                                                  */
        /* Post the listing of one folder the user opened, and note it as   */
        /* the one awaited for that folder, with its node.  It only posts:  */
        /* whether the listing may run (busy, the image changed) is the     */
        /* worker's to decide, and its answer says.                         */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     nodDir : the folder's node; its Tag is the folder's path.    */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the listing is posted; Ifp_OnNodeListed takes its     */
        /*            answer.                                               */
        /*------------------------------------------------------------------*/
        private void Ifp_LoadNode(TreeNode nodDir)
        {
            string sDirectory;
            RQT_clsRequest objRequest;
            IFN_clsNodeRead objRead;

            /* A PickerList request for the folder's path: this input's id    */
            /* as its origin, the accepted image, a stream of its own per     */
            /* folder ("node:<path>").  The node itself is not given to the   */
            /* worker, only its path; the request and the node are kept       */
            /* here together, replacing any earlier one for this folder.      */
            /* The answer, a refusal included, comes through the completion   */
            /* on this same loop, so it cannot arrive before the note is      */
            /* made.                                                          */
            sDirectory = (string)nodDir.Tag;
            objRequest = CFC_clsCommands.CFC_Create(KND_enmKind.PickerList,
                Ifp_lWindow, Ifp_objSource, Ifp_objSource.STV_objTarget,
                new string[] { sDirectory }, 0, 0, null, "", "node:" + sDirectory);
            objRead = new IFN_clsNodeRead();
            objRead.IFN_objRequest = objRequest;
            objRead.IFN_nodDirectory = nodDir;
            Ifp_dictLatest[sDirectory] = objRead;
            Ifp_objClient.CLI_Post(objRequest, Ifp_OnNodeListed);
        }

        /*------------------------------------------------------------------*/
        /* Ifp_OnNodeListed:                                                */
        /*                                                                  */
        /* A folder listing's answer, on the main window's loop: fill its   */
        /* node -- but only when it answers the request now awaited for     */
        /* that folder, and the node is still in the tree.                  */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objRequest : the request answered.                           */
        /*     objResult  : its result: the listing, or why there is none   */
        /*                  (a failure, a refusal, a cancel, a drop).       */
        /*                                                                  */
        /* Returns:                                                         */
        /*     void : the node filled and opened, a failure said in the     */
        /*            prompt line, or the answer dropped.                   */
        /*------------------------------------------------------------------*/
        private void Ifp_OnNodeListed(RQT_clsRequest objRequest, RSL_clsResult objResult)
        {
            IFN_clsNodeRead objRead;
            string sDirectory;

            if (Ifp_bClosed || IsDisposed)
            {   /* The dialog has closed (the table of awaited listings was   */
                /* emptied then): nothing to fill.                            */
                return;
            }

            /* The folder the request was for -- its one path, as            */
            /* Ifp_LoadNode built it -- and the request awaited for it,      */
            /* compared as the same OBJECT: a newer request for the folder   */
            /* replaced the note, so an older answer arriving late finds     */
            /* another request there and is dropped.                         */
            sDirectory = objRequest.RQT_lstPaths[0];
            if (!Ifp_dictLatest.TryGetValue(sDirectory, out objRead) ||
                objRead.IFN_objRequest != objRequest)
            {   /* Not the request awaited: dropped. */
                return;
            }

            /* Answered, so no longer awaited, whatever the answer is.  Then   */
            /* the cases that leave the node as it was, placeholder and all,   */
            /* so opening it again lists it again.                             */
            Ifp_dictLatest.Remove(sDirectory);
            if (objRead.IFN_nodDirectory.TreeView != Ifp_treFolders ||
                objResult.RSL_iStatus == SYS_clsSystem.SYS_CANCELLED ||
                objResult.RSL_iStatus == SYS_clsSystem.SYS_DROPPED)
            {   /* The node is no longer in the tree, or the listing was   */
                /* cancelled or dropped (another copy of it carries the    */
                /* work): nothing is filled and nothing said.              */
                return;
            }

            /* The listing, done by the worker, put into the node -- or its   */
            /* failure said in the prompt line.                               */
            Ifp_PaintNode(objRead.IFN_nodDirectory, objResult);
        }

        /*------------------------------------------------------------------*/
        /* Ifp_PaintNode:                                                   */
        /*                                                                  */
        /* Fill a folder's node from its listing: one child per sub-folder, */
        /* each with its own placeholder, and the node opened.  A listing   */
        /* that failed -- or that is not of this folder -- leaves the node  */
        /* as it was and says why in the prompt line, so an unreadable      */
        /* folder never looks like an empty one.  Used by both answers      */
        /* (the opening request's and a folder's own).                      */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     nodDir    : the folder's node, in the tree; its Tag is the   */
        /*                 folder's path.                                   */
        /*     objResult : the listing's result -- its status and detail,   */
        /*                 and its rows (an ELR_clsListResult), which are   */
        /*                 read, not kept.                                  */
        /*                                                                  */
        /* Returns:                                                         */
        /*     bool : true when the node was filled; false when the listing */
        /*            failed.                                               */
        /*------------------------------------------------------------------*/
        private bool Ifp_PaintNode(TreeNode nodDir, RSL_clsResult objResult)
        {
            ELR_clsListResult objRows;
            List<string> lstNames;
            TreeNode nodChild;
            string sDetail;
            int iStatus;
            int i;

            /* The answer's status and detail, and its rows.  A success is    */
            /* believed only with rows that are of THIS folder (the path      */
            /* the listing names, compared exactly): a success without        */
            /* them would otherwise fill the node with nothing, and show an   */
            /* unread folder as empty.                                        */
            iStatus = objResult.RSL_iStatus;
            sDetail = objResult.RSL_sDetail;
            objRows = objResult.RSL_objPayload as ELR_clsListResult;
            if (objRows == null || objRows.ELR_lstEntries == null ||
                !String.Equals(objRows.ELR_sImageDir, (string)nodDir.Tag, StringComparison.Ordinal))
            {   /* No rows, or another folder's: not a usable listing. */
                if (iStatus == SYS_clsSystem.SYS_OK)
                {   /* It said success: taken as an internal failure instead.   */
                    /* Whether a success can come without its rows is not       */
                    /* established; the test makes sure it is never shown as    */
                    /* an empty folder if one does.                             */
                    iStatus = SYS_clsSystem.SYS_INTERNAL;
                }

                if (String.IsNullOrEmpty(sDetail))
                {   /* No reason given: one that says what was wrong. */
                    sDetail = "No matching directory data was returned.";
                }
            }

            if (iStatus != ENG_clsNative.ENG_OK)
            {   /* Failed (SYS_OK and ENG_OK are both 0, CommandSystem.cs):   */
                /* the node keeps its placeholder, so it can be opened and    */
                /* listed again, and is not filled.                           */
                if (String.IsNullOrEmpty(sDetail))
                {   /* No detail: the status's own sentence (ENG_StatusText).   */
                    /* For the worker's own codes (refused, internal) that is   */
                    /* only a general one.                                      */
                    sDetail = ENG_clsNative.ENG_StatusText(iStatus);
                }

                /* The failure, with the folder named, in the prompt line --   */
                /* where it stays until another one replaces it.               */
                Ifp_lblPrompt.Text = "Cannot read " + (string)nodDir.Tag + ": " + sDetail;
                return(false);
            }

            /* A listing: the placeholder (or any children) cleared, then    */
            /* the sub-folders' names collected first, so they can be        */
            /* sorted before any node is made.  A link to a folder is not    */
            /* ENG_TYPE_DIR, so it is not shown (as found).  ENG_EntryName   */
            /* decodes each name from UTF-8, a name that is not valid        */
            /* UTF-8 with replacement characters.                            */
            nodDir.Nodes.Clear();
            lstNames = new List<string>();
            for (i = 0; i < objRows.ELR_lstEntries.Count; i++)
            {   /* One row per iteration: kept when it is a folder. */
                if (objRows.ELR_lstEntries[i].EEN_uiType == ENG_clsNative.ENG_TYPE_DIR)
                {   /* A folder: its name kept. */
                    lstNames.Add(ENG_clsNative.ENG_EntryName(objRows.ELR_lstEntries[i]));
                }
            }

            /* Ordinal order, case-sensitive like the names themselves --   */
            /* the order the image pane lists folders in (PaneSorter.cs,    */
            /* Psr_CompareNames).                                           */
            lstNames.Sort(string.CompareOrdinal);

            /* The nodes added between BeginUpdate and EndUpdate, so the   */
            /* tree is redrawn once, not once per node (as found).         */
            Ifp_treFolders.BeginUpdate();
            for (i = 0; i < lstNames.Count; i++)
            {   /* One sub-folder per iteration: its node, carrying its path. */
                nodChild = new TreeNode(lstNames[i]);
                if ((string)nodDir.Tag == "/")
                {   /* Under the root: "/" and the name, so no "//". */
                    nodChild.Tag = "/" + lstNames[i];
                }
                else
                {   /* Deeper: the parent's path, "/", and the name. */
                    nodChild.Tag = (string)nodDir.Tag + "/" + lstNames[i];
                }

                /* Each child starts unlisted, with a placeholder so it shows   */
                /* an expand mark; it is listed when it is opened (or, during   */
                /* the opening request's walk, from the same answer).           */
                nodChild.Nodes.Add(new TreeNode(IFP_PLACEHOLDER));
                nodDir.Nodes.Add(nodChild);
            }

            /* Redrawn once, and the node opened.  Its children are real    */
            /* now, so Ifp_OnBeforeExpand lets it open and posts nothing.   */
            Ifp_treFolders.EndUpdate();
            nodDir.Expand();

            /* Filled. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* Ifp_OnOkClicked:                                                   */
        /*                                                                    */
        /* OK: keep the selected directory's path and close.  With nothing    */
        /* selected (it cannot normally happen) OK does nothing.              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the outcome fields are set and the dialog closes.       */
        /*--------------------------------------------------------------------*/
        private void Ifp_OnOkClicked(object objSender, EventArgs evArgs)
        {
            if (Ifp_treFolders.SelectedNode == null || Ifp_treFolders.SelectedNode.Tag == null)
            {   /* Nothing chosen, or the placeholder: not a directory.  The   */
                /* dialog stays open, so the user can pick one.                */
                return;
            }

            /* The chosen directory's image path, then the flag, both set   */
            /* before the close: the caller reads them as soon as           */
            /* ShowDialog returns (Close hides a modal dialog, as found).   */
            IFP_sPath     = (string)Ifp_treFolders.SelectedNode.Tag;
            IFP_bAccepted = true;
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Ifp_OnCancelClicked:                                               */
        /*                                                                    */
        /* Cancel: close with the outcome left at "not accepted".             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the dialog closes.                                      */
        /*--------------------------------------------------------------------*/
        private void Ifp_OnCancelClicked(object objSender, EventArgs evArgs)
        {
            /* Closed with the outcome left as the constructor set it --      */
            /* what Escape and the title bar's close leave too.  A listing    */
            /* still running is dropped when it answers (Ifp_OnFormClosed).   */
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Ifp_OnResize:                                                      */
        /*                                                                    */
        /* The dialog's size changed.  Docking has already refitted the three */
        /* controls; what is left is to keep the selected directory in view,  */
        /* since a smaller tree may have scrolled it out.                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the dialog.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the selected node is visible.                           */
        /*--------------------------------------------------------------------*/
        private void Ifp_OnResize(object objSender, EventArgs evArgs)
        {
            if (Ifp_treFolders.SelectedNode != null)
            {   /* There is a selection to keep in view. */
                Ifp_treFolders.SelectedNode.EnsureVisible();
            }
        }

        /*--------------------------------------------------------------------*/
        /* Ifp_OnFormClosed:                                                  */
        /*                                                                    */
        /* The dialog has closed: note it, so a listing still running when it */
        /* closed does not touch the tree when its answer arrives.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the dialog.                                        */
        /*     evClosed  : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : later answers are dropped.                              */
        /*--------------------------------------------------------------------*/
        private void Ifp_OnFormClosed(object objSender, FormClosedEventArgs evClosed)
        {
            /* Closed: every answer still to come is ignored (both answer     */
            /* handlers test the flag first), and nothing is awaited any      */
            /* more.  The listings themselves are not cancelled -- they       */
            /* are the worker's, which finishes them and sends answers that   */
            /* now touch nothing.                                             */
            Ifp_bClosed = true;
            Ifp_dictLatest.Clear();
            Ifp_objBranch = null;
        }
    }
}
