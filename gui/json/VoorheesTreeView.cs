/*----------------------------------------------------------------------------*/
/* VoorheesTreeView.cs                                                        */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/*----------------------------------------------------------------------------*/
/* The editor's tree: a picture of a VoorheesDocument that is built LAZILY    */
/* and kept up to date IN PLACE.                                              */
/*                                                                            */
/*     o One tree node per item, at the same position: tree child index ==    */
/*       item position at every level, and the top level is the document's    */
/*       items (top-level comments and the top value).  So a tree node's      */
/*       position path (its indexes, up to the top) IS the item's path in     */
/*       the document.                                                        */
/*     o A container's children are created only when it is first expanded.   */
/*       Until then it holds one placeholder child (Tag null) so it shows an  */
/*       expand box.  Opening the 40,000-item array of a 9 MB file therefore  */
/*       costs one level, not the whole document.                             */
/*     o Each change the editor makes to the document (VoorheesChange) is     */
/*       applied to the tree by VTV_ApplyChange: one node relabelled,         */
/*       inserted or removed, plus the siblings whose labels depend on it.    */
/*       Nothing is ever rebuilt wholesale.                                   */
/*                                                                            */
/* Labels: a member "key: value", {key} for an object, [key] for an array     */
/* (array elements use their index as the key, the top value "root"); a       */
/* comment entry "comment: words".  A key that appears more than once in its  */
/* object is drawn in VTV_COLORDUPLICATE with "(duplicate)" after it.  The    */
/* comment on an item's line follows its label, and a marker follows an item  */
/* with comments only the Raw box can edit; both, and comment entries, are    */
/* drawn in the muted (grey) colour.  With the Show Raw Text option on, every */
/* label is instead the first line of the item's text exactly as in the file. */
/*                                                                            */
/* Drawing: the control draws every label itself, natively, in the node's     */
/* own colour (TreeNode.ForeColor: red for a duplicate, grey for a comment    */
/* entry).  Only a label with a grey TAIL (the line comment or the marker     */
/* after a value) needs more: after the control has drawn such an item, the   */
/* tail is painted over in grey (custom draw, item post-paint, handled in     */
/* WndProc).  WinForms' own owner-drawn mode is not used: measured, it makes  */
/* inserting nodes three to four times slower (40,000 children: about 4 s     */
/* against 1 s), because every label then goes through managed drawing.       */
/*                                                                            */
/* Progress: filling a big container and removing a big branch each take a    */
/* long time inside one call into the control.  The control counts the items  */
/* it inserts (TVM_INSERTITEM) and deletes (TVN_DELETEITEM) in WndProc and    */
/* moves a progress bar from there; the window is put up BEFORE such a call   */
/* when the measured rates say it will be slow, never from inside it.         */
/*                                                                            */
/* Classes:                                                                   */
/*     VTT_clsTreeTag  (VTT_) : stored in every real tree node's Tag.         */
/*     VTV_clsTreeView (Vtv_) : the control.                                  */
/*----------------------------------------------------------------------------*/

/* The framework: the base types (IntPtr, Math), the                            */
/* lists of paths and keys and the key counts, the colours and rectangles,      */
/* the native calls and structures for GDI and the control's messages, the      */
/* StringBuilder VTV_OneLine builds with, and the tree and its window           */
/* classes; and BifrostFM's namespace for the busy box (BMF_clsBusyModalForm,   */
/* gui\lib\BusyModalForm.cs) that shows a long fill's or removal's progress.    */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using BifrostFM;

/*----------------------------------------------------------------------------*/
/* VTT_clsTreeTag                                                             */
/*                                                                            */
/* What the tree needs to remember about one node beyond its text.  The       */
/* node's place in the document is NOT stored: it is the node's position      */
/* path, which stays right as siblings come and go.                           */
/*----------------------------------------------------------------------------*/
internal class VTT_clsTreeTag
{
    /* Whether the node's children exist, and where the runs of its label   */
    /* lie -- documentation table.                                          */
    public bool VTT_bPopulated;                          // its children have been created (always true for a non-container)
    public int VTT_iMutedFrom;                           // where the grey tail of the label starts; -1 for none (a label grey throughout uses TreeNode.ForeColor)
    public int VTT_iValueFrom;                           // where the editable part of the label starts ("key: " ends); 0 when the whole label is the text
    public int VTT_iKeyFrom;                             // where the key (or index, or "root") starts in the label; -1 when there is none
    public int VTT_iKeyLength;                           // how long it is
    public int VTT_iValueLength;                         // how long the shown value is, from VTT_iValueFrom; 0 when the label shows no value (containers, comments, an INI key alone, the raw view)
    public int VTT_iValueRole;                           // the shown value's colour role (VoorheesFormat.VF_ROLE_); -1 when there is no value

    /*------------------------------------------------------------------------*/
    /* VTT_Create:                                                            */
    /*                                                                        */
    /* A new tag for a real node (a placeholder has none), with nothing of    */
    /* its label marked yet: the labelling code that follows sets the grey    */
    /* tail, the key and the value's run once it has built the text.          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bPopulated : the node's children exist (or it can have none).      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VTT_clsTreeTag : the tag, with no grey tail, key or value run.     */
    /*------------------------------------------------------------------------*/
    public static VTT_clsTreeTag VTT_Create(bool bPopulated)
    {
        VTT_clsTreeTag objTag;                             // the new tag

        /* Whether the children exist; every run marked absent (-1 or 0),   */
        /* the whole label taken as the editable text.                      */
        objTag = new VTT_clsTreeTag();
        objTag.VTT_bPopulated = bPopulated;
        objTag.VTT_iMutedFrom = -1;
        objTag.VTT_iValueFrom = 0;
        objTag.VTT_iKeyFrom = -1;
        objTag.VTT_iKeyLength = 0;
        objTag.VTT_iValueLength = 0;
        objTag.VTT_iValueRole = -1;

        /* Ready; the label code sets the tail, the key and the value's run. */
        return(objTag);
    }
}

/*----------------------------------------------------------------------------*/
/* VTV_clsTreeView                                                            */
/*                                                                            */
/* The control.  The editor gives it the document (VTV_SetDocument), tells    */
/* it about every change (VTV_ApplyChange) and selects by path (VTV_Select).  */
/* While it rearranges nodes itself VTV_IsBusy is true, during which the      */
/* editor ignores structural selection events; when the outermost such        */
/* rearrangement ends it calls the editor's return receiver                   */
/* (VTV_BindReturn), which only posts work for later; and it asks the editor  */
/* for the document's name its progress windows show (VTV_BindDocName).       */
/* Its four Framework entry points (OnBeforeExpand, OnBeforeLabelEdit,        */
/* OnAfterLabelEdit, WndProc) are here, each forwarding to the Vtv_On...      */
/* helper beside it.                                                          */
/*                                                                            */
/* Derives from TreeView because the framework requires it: the expand and    */
/* label-edit hooks and the window procedure this control needs reach only    */
/* a TreeView subclass.  That is the one kind of inheritance allowed here --  */
/* from a framework base class, never from a class of ours.                   */
/*----------------------------------------------------------------------------*/
internal class VTV_clsTreeView : TreeView
{
    /*------------------------------------------------------------------------*/
    /* Vrc_stRect / Vsz_stSize                                                */
    /*                                                                        */
    /* RECT and SIZE, for the GDI calls below; laid out as in C.              */
    /*------------------------------------------------------------------------*/
    [StructLayout(LayoutKind.Sequential)]
    struct Vrc_stRect
    {
        public int VRC_iLeft;                            // left edge
        public int VRC_iTop;                             // top edge
        public int VRC_iRight;                           // right edge (exclusive)
        public int VRC_iBottom;                          // bottom edge (exclusive)
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Vsz_stSize
    {
        public int VSZ_iWidth;   // width
        public int VSZ_iHeight;  // height
    }

    /*------------------------------------------------------------------------*/
    /* GDI values (wingdi.h / winuser.h) for drawing labels exactly as the    */
    /* control does (Vtv_DrawLabelRuns).                                      */
    /*------------------------------------------------------------------------*/
    const uint VTV_ETO_OPAQUE = 0x0002;                  // ExtTextOut: fill the rectangle with the background colour
    const int VTV_WM_GETFONT = 0x0031;                   // ask a control for the GDI font it draws with
    const int VTV_TRANSPARENT = 1;                       // SetBkMode: text leaves the background as it is

    /*------------------------------------------------------------------------*/
    /* Vnh_stHeader                                                           */
    /*                                                                        */
    /* NMHDR, the header every notification starts with.  It is its own       */
    /* structure (not flattened into Vcd_stCustomDraw) because C pads it to a */
    /* multiple of the pointer size: on 64-bit Windows it is 24 bytes, so     */
    /* what follows it starts at 24, not straight after the 4-byte code.      */
    /* (Flattened, the draw stage would be read from that padding: always 0.) */
    /*------------------------------------------------------------------------*/
    [StructLayout(LayoutKind.Sequential)]
    struct Vnh_stHeader
    {
        public IntPtr VNH_hWindowFrom;  // the control sending it: the tree
        public IntPtr VNH_pIdFrom;      // its control ID
        public int VNH_iCode;           // the notification: NM_CUSTOMDRAW here
    }

    /*------------------------------------------------------------------------*/
    /* Vcd_stCustomDraw                                                       */
    /*                                                                        */
    /* The start of NMTVCUSTOMDRAW (an NMCUSTOMDRAW, which begins with an     */
    /* NMHDR), as far as this control reads it.  Laid out sequentially, the   */
    /* header nested, so the marshaller gives it the same padding as the C    */
    /* structure on both 32- and 64-bit Windows.                              */
    /*------------------------------------------------------------------------*/
    [StructLayout(LayoutKind.Sequential)]
    struct Vcd_stCustomDraw
    {
        public Vnh_stHeader VCD_stHeader;  // NMHDR: who sent it, and the code
        public int VCD_iDrawStage;         // dwDrawStage: a CDDS_ value
        public IntPtr VCD_hDeviceContext;  // hdc: the device context being painted
        public int VCD_iLeft;              // rc.left: the item's row, client coordinates
        public int VCD_iTop;               // rc.top
        public int VCD_iRight;             // rc.right
        public int VCD_iBottom;            // rc.bottom
        public IntPtr VCD_hItem;           // dwItemSpec: the item (HTREEITEM)
        public int VCD_iItemState;         // uItemState: CDIS_ bits
        public IntPtr VCD_pItemParam;      // lItemlParam: the item's application value
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_FnExpandPath                                                       */
    /*                                                                        */
    /* The shape of VTV_ExpandPath, so an expand can be posted to the         */
    /* message queue bound to that named method, with the path passed as the  */
    /* posted call's argument (Vtv_OnBeforeExpand).                           */
    /*------------------------------------------------------------------------*/
    delegate void Vtv_FnExpandPath(List<int> lstPath);

    /*------------------------------------------------------------------------*/
    /* VTV_FnDocName:                                                         */
    /*                                                                        */
    /* The shape of the editor's call that names the document for the tree's  */
    /* progress windows (VTV_BindDocName) -- the name the user knows the file */
    /* by, which need not be the name of the path the document was read from. */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the name to show.                                         */
    /*------------------------------------------------------------------------*/
    public delegate string VTV_FnDocName();

    /*------------------------------------------------------------------------*/
    /* TreeView messages and notifications (commctrl.h).  TV_FIRST is 0x1100  */
    /* and TVN_FIRST is -400.                                                 */
    /*------------------------------------------------------------------------*/

    const int VTV_TVM_INSERTITEMA = 0x1100;              // TV_FIRST + 0: insert an item, ANSI form
    const int VTV_TVM_INSERTITEMW = 0x1132;              // TV_FIRST + 50: insert an item, Unicode form (WinForms')
    const int VTV_TVM_GETITEMA = 0x110C;                 // TV_FIRST + 12: read an item's state, ANSI form
    const int VTV_TVM_GETITEMW = 0x113E;                 // TV_FIRST + 62: read an item's state, Unicode form (WinForms')
    const int VTV_TVM_GETEDITCONTROL = 0x110F;           // TV_FIRST + 15: the edit box of the in-place edit
    const int VTV_WM_SETTEXT = 0x000C;                   // set a window's (the edit box's) text
    const int VTV_EM_SETSEL = 0x00B1;                    // edit box: select characters wParam..lParam (0..-1: all)
    const int VTV_WM_REFLECT_NOTIFY = 0x204E;            // WM_REFLECT (0x2000) + WM_NOTIFY: a notification WinForms bounces back to the control
    const int VTV_TVN_DELETEITEMA = -409;                // TVN_FIRST - 9: an item was deleted, ANSI form
    const int VTV_TVN_DELETEITEMW = -458;                // TVN_FIRST - 58: an item was deleted, Unicode form (seen in practice)

    /*------------------------------------------------------------------------*/
    /* Custom draw (commctrl.h): the control asks, through a reflected        */
    /* NM_CUSTOMDRAW notification, before and after it paints each item.      */
    /* WinForms answers the pre-paint stages (it applies TreeNode colours);   */
    /* this control adds a post-paint request and draws the grey tails then.  */
    /*------------------------------------------------------------------------*/

    const int VTV_NM_CUSTOMDRAW = -12;                   // NM_FIRST - 12: the custom draw notification
    const int VTV_CDDS_ITEMPREPAINT = 0x00010001;        // CDDS_ITEM | CDDS_PREPAINT: an item is about to be painted
    const int VTV_CDDS_ITEMPOSTPAINT = 0x00010002;       // CDDS_ITEM | CDDS_POSTPAINT: an item has just been painted
    const int VTV_CDRF_NOTIFYPOSTPAINT = 0x00000010;     // answer to a pre-paint: send the post-paint stage too
    const int VTV_CDIS_SELECTED = 0x0001;                // item state: the item is selected

    /*------------------------------------------------------------------------*/
    /* Label limits and markers.  Non-ASCII marks are built from their code   */
    /* points, which keeps this file pure ASCII.                              */
    /*------------------------------------------------------------------------*/

    public const int VTV_LEAFMAX = 60;                                   // longest value (or raw line) shown in a label, characters
    const string VTV_DUPLICATEMARK = "  (duplicate)";                    // after a key that appears more than once in its object
    const string VTV_ODDMARK = "  /*...*/";                              // after an item with comments only the Raw box can edit
    const string VTV_COMMENTPREFIX = "comment: ";                        // start of a comment entry's label
    static readonly string VTV_RETURNMARK = ((char)0x21B5).ToString();   // U+21B5 return arrow: a line break in a label
    static readonly Color VTV_COLORDUPLICATE = Color.Firebrick;          // repeated keys
    static readonly Color VTV_COLORMUTED = SystemColors.GrayText;        // comments and markers
    public static readonly Color VTV_COLORKEY = Color.DarkBlue;          // keys, indexes and "root", with View > Color Keys on

    /*------------------------------------------------------------------------*/
    /* Name colour roles: which kind of name a                                */
    /* label's name part is.  Value roles are VoorheesFormat.VF_ROLE_.        */
    /*------------------------------------------------------------------------*/

    public const int VTV_NAME_TOP = 0;                   // a top-level container's name: [section], [include ...], REG keys, JSON's root
    public const int VTV_NAME_NESTED = 1;                // a container inside another (JSON objects / arrays below the top, sections in an auto-saved block)
    public const int VTV_NAME_ENTRY = 2;                 // a plain value's key or index
    public const int VTV_NAMEROLECOUNT = 3;              // how many name roles there are

    /*------------------------------------------------------------------------*/
    /* Progress: nodes counted between reports, and the measured costs used   */
    /* to decide beforehand whether a populate or removal will be slow        */
    /* enough to put the progress window up (9 MB, 440,000-value document on  */
    /* the development machine).                                              */
    /*------------------------------------------------------------------------*/

    const long VTV_PROGRESSSTEP = 2000;                  // nodes per progress report
    const long VTV_FILLUSPERNODE = 16;                   // cost of labelling and inserting one node, microseconds
    const long VTV_CLEARUSPERNODE = 64;                  // cost of removing one node from the control, microseconds
    const int VTV_SHOWDELAYMS = 300;                     // work shorter than this never shows the progress window

    private VoorheesDocument Vtv_doc;       // the document shown
    Font Vtv_fntDialog;                     // the host program's dialog font, for the progress window this control opens; given at creation, not owned here
    private bool Vtv_bShowRaw;              // labels are the items' raw text
    private bool Vtv_bColorKeys;            // names and values are drawn in their roles' colours (View > Color Keys)
    private Color[] Vtv_arrNameColors;      // with Color Keys on: each VTV_NAME_ role's colour
    private Color[] Vtv_arrValueColors;     // with Color Keys on: each VF_ROLE_ value role's colour
    MethodInvoker Vtv_pfnReturned;          // the editor's call for "the tree's own rearrangement has ended" (VTV_BindReturn), or null; it must only post, never change the document inline
    VTV_FnDocName Vtv_pfnDocName;           // the editor's call for the document's name its progress windows show (VTV_BindDocName), or null: then the file name of the path it was read from
    int Vtv_iBusyDepth;                     // above 0 while the tree rearranges itself (nests): see VTV_IsBusy
    string Vtv_sEditValue;                  // the full text for a prepared in-place edit, else null (VTV_PrepareValueEdit)
    int Vtv_iEditShift;                     // how far right its edit box goes: the key part's width, pixels
    VTS_clsEditShifter Vtv_objEditShifter;  // watches that edit box while it is open, else null
    TreeNode Vtv_nodEdit;                   // the node whose key part is drawn beside the open box, else null

    BMF_clsBusyModalForm Vtv_frmProgress;  // progress window while counting, else null
    long Vtv_lDone;                        // progress units done so far
    long Vtv_lTotal;                       // progress units in all
    bool Vtv_bCountInserts;                // count inserted items
    bool Vtv_bCountDeletes;                // count deleted items (and answer their notifications)

    /*------------------------------------------------------------------------*/
    /* VTV_BindReturn:                                                        */
    /*                                                                        */
    /* Give the tree the one call it makes when a rearrangement of its own    */
    /* has ended (Vtv_ReportReturn) -- the editor's (JsonEditForm.cs          */
    /* Jsn_OnTreeReturned), set when the editor is registered with the        */
    /* command system.  One call, not an event: a single receiver, named.     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     pfnReturned : the receiver, or null for none.  It must only post   */
    /*                   work for later: it runs while the control's own      */
    /*                   events may still be unwinding.                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : kept; nothing is called now.                                */
    /*------------------------------------------------------------------------*/
    public void VTV_BindReturn(MethodInvoker pfnReturned)
    {
        /* Kept for Vtv_ReportReturn, on the UI thread, as all the tree's   */
        /* work is.                                                         */
        Vtv_pfnReturned = pfnReturned;
    }

    /*------------------------------------------------------------------------*/
    /* VTV_BindDocName:                                                       */
    /*                                                                        */
    /* Give the tree the call that names the document in its progress         */
    /* windows (Vtv_DocName) -- the editor's, set when the editor makes the   */
    /* tree.  Asked each time a window opens, so a name the editor changes    */
    /* (a Save As) is shown from then on with nothing kept here to go stale.  */
    /* Without one the tree shows the file name of the path the document was  */
    /* read from, which for a file read through a temporary copy is that      */
    /* copy's name, not the file's.                                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     pfnDocName : the call, or null for none.                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : kept; nothing is called now.                                */
    /*------------------------------------------------------------------------*/
    public void VTV_BindDocName(VTV_FnDocName pfnDocName)
    {
        /* Kept for Vtv_DocName, on the UI thread, as all the tree's work is. */
        Vtv_pfnDocName = pfnDocName;
    }

    /*------------------------------------------------------------------------*/
    /* VTV_ReadNameColors:                                                    */
    /*                                                                        */
    /* The tree's name-role colours (one per VTV_NAME_ role), for the         */
    /* editor's Colors dialog and its settings (JsonEditForm.cs).             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Color[] : the tree's OWN array, not a copy: the editor writes the  */
    /*               chosen and saved colours straight into it (Array.Copy,   */
    /*               an indexed set) and the drawing reads it, so a copy here */
    /*               would make those writes change nothing on screen.        */
    /*------------------------------------------------------------------------*/
    public Color[] VTV_ReadNameColors()
    {
        /* The array the drawing uses (Vtv_NameColor). */
        return(Vtv_arrNameColors);
    }

    /*------------------------------------------------------------------------*/
    /* VTV_ReadValueColors:                                                   */
    /*                                                                        */
    /* The tree's value-role colours, one per VoorheesFormat.VF_ROLE_, in     */
    /* that order -- for the same callers, on the same terms, as              */
    /* VTV_ReadNameColors.                                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Color[] : the tree's own array, not a copy.                        */
    /*------------------------------------------------------------------------*/
    public Color[] VTV_ReadValueColors()
    {
        /* The array the drawing uses (Vtv_PostPaintItem). */
        return(Vtv_arrValueColors);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_ReportReturn:                                                      */
    /*                                                                        */
    /* Tell the editor that the tree's own rearrangement has ended -- once,   */
    /* at the outermost end (the rearrangements nest), and only when a        */
    /* receiver is bound.  Each rearrangement's finally calls it after the    */
    /* busy depth comes down.                                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the receiver called, or nothing done.                       */
    /*------------------------------------------------------------------------*/
    void Vtv_ReportReturn()
    {
        if (Vtv_iBusyDepth == 0 && Vtv_pfnReturned != null)
        {   /* No rearrangement left open, and a receiver: called.  The       */
            /* control's own events from the rearrangement (or a selection    */
            /* the program made) may still be unwinding, which is why the     */
            /* receiver only posts -- the editor's return fact, and any       */
            /* acknowledgement or close it held back -- and changes nothing   */
            /* inline.                                                        */
            Vtv_pfnReturned();
        }
    }
    /*------------------------------------------------------------------------*/
    /* VTV_Create:                                                            */
    /*                                                                        */
    /* The control, set up to draw its own label text.                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     fntDialog : the host program's dialog font, which the progress     */
    /*                 window a long fill or clear opens is shown in (the     */
    /*                 caller's; not freed here).  The tree's own text is the */
    /*                 editor's size setting, not this.                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VTV_clsTreeView : the control, with no document yet.               */
    /*------------------------------------------------------------------------*/
    public static VTV_clsTreeView VTV_Create(Font fntDialog)
    {
        VTV_clsTreeView treView;                           // the new control

        /* Normal (native) drawing -- the file header gives the measured   */
        /* reason -- the progress window's font, no document, both view    */
        /* options off, not busy, and the default colours, which the       */
        /* editor's saved colours replace when it loads them.              */
        treView = new VTV_clsTreeView();
        treView.Vtv_fntDialog = fntDialog;
        treView.DrawMode = TreeViewDrawMode.Normal;
        treView.Vtv_doc = null;
        treView.Vtv_bShowRaw = false;
        treView.Vtv_bColorKeys = false;
        treView.Vtv_iBusyDepth = 0;
        treView.Vtv_arrNameColors = VTV_DefaultNameColors();
        treView.Vtv_arrValueColors = VTV_DefaultValueColors();

        /* Ready for a document. */
        return(treView);
    }

    /*------------------------------------------------------------------------*/
    /* VTV_DefaultNameColors:                                                 */
    /*                                                                        */
    /* The name roles' default colours: dark blue (VTV_COLORKEY) for all      */
    /* three, the colour View > Color Keys draws keys in until the user       */
    /* picks others.                                                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Color[] : one colour per VTV_NAME_ role (a new array).             */
    /*------------------------------------------------------------------------*/
    public static Color[] VTV_DefaultNameColors()
    {
        Color[] arrColors;                               // the defaults
        int i;

        /* A new array each call, so a caller may change its copy. */
        arrColors = new Color[VTV_NAMEROLECOUNT];
        for (i = 0; i < arrColors.Length; i++)
        {   /* One name role per iteration. */
            arrColors[i] = VTV_COLORKEY;
        }

        /* Every name dark blue. */
        return(arrColors);
    }

    /*------------------------------------------------------------------------*/
    /* VTV_DefaultValueColors:                                                */
    /*                                                                        */
    /* The value roles' default colours: the system's text colour (black by   */
    /* default) for every type, so values look as they would uncoloured.      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Color[] : one colour per VoorheesFormat.VF_ROLE_ (a new array).    */
    /*------------------------------------------------------------------------*/
    public static Color[] VTV_DefaultValueColors()
    {
        Color[] arrColors;                               // the defaults
        int i;

        /* A new array each call, so a caller may change its copy. */
        arrColors = new Color[VoorheesFormat.VF_ROLECOUNT];
        for (i = 0; i < arrColors.Length; i++)
        {   /* One value role per iteration. */
            arrColors[i] = SystemColors.WindowText;
        }

        /* Every value in the system's text colour. */
        return(arrColors);
    }

    /*------------------------------------------------------------------------*/
    /* VTV_IsBusy:                                                            */
    /*                                                                        */
    /* Whether the tree is rearranging its own nodes right now, so a          */
    /* selection event comes from that and not from the user: the editor      */
    /* ignores selection events while this is true.                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true inside any of the tree's own rearrangements.           */
    /*------------------------------------------------------------------------*/
    public bool VTV_IsBusy()
    {
        /* Rearrangements nest, hence a depth rather than a flag. */
        return(Vtv_iBusyDepth > 0);
    }

    /*------------------------------------------------------------------------*/
    /* VTV_SetDocument:                                                       */
    /*                                                                        */
    /* Shows a new document in place of the old: its top level only (every    */
    /* container left unopened), with the entry the format names first        */
    /* selected.                                                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objDocument : the document.                                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the tree shows the document's top level.                    */
    /*------------------------------------------------------------------------*/
    public void VTV_SetDocument(VoorheesDocument objDocument)
    {
        List<int> lstFirst;                              // the entry to select first
        TreeNode[] arrNodes;                             // the new top-level nodes
        Dictionary<string, int> dictKeys;                // the document's own key counts (none in JSON)
        bool bExpand;                                    // open that entry
        int i;

        /* Busy while the nodes are rebuilt: the selection the control     */
        /* moves by itself as nodes go is not the user's, and the editor   */
        /* ignores it.                                                     */
        Vtv_iBusyDepth++;
        try
        {   /* The rebuild; the finally lowers the busy depth whatever      */
            /* happens, or the editor would ignore selections for good.     */
            /* The old nodes removed -- counted, with the progress window   */
            /* up first if there are enough to be slow -- and the new       */
            /* document taken.                                              */
            Vtv_RemoveNodesCounted(Nodes, -1);
            Vtv_doc = objDocument;

            /* The top level: one node per document item, made before any   */
            /* is added, with the top level's own key counts (for the       */
            /* duplicate marks of formats whose top level has keys).        */
            dictKeys = objDocument.Vd_KeyCounts(objDocument.Vd_root);
            arrNodes = new TreeNode[objDocument.Vd_root.Vn_Count()];
            for (i = 0; i < arrNodes.Length; i++)
            {   /* One top-level item per iteration, unopened. */
                arrNodes[i] = Vtv_MakeNode(objDocument.Vd_root, new List<int>(), i, dictKeys, -1);
            }

            /* Added in one call, drawing held off meanwhile. */
            BeginUpdate();
            Nodes.AddRange(arrNodes);
            EndUpdate();
        }
        finally
        {   /* The rearrangement over, however it ended: the busy depth   */
            /* down, then the editor told (Vtv_ReportReturn), whose       */
            /* receiver only posts its work for later.                    */
            Vtv_iBusyDepth--;
            Vtv_ReportReturn();
        }

        /* The format's first entry (Vd_InitialPath), opened one level if   */
        /* it says so, and selected -- outside the busy block, so the       */
        /* editor takes the selection and loads its panel.                  */
        lstFirst = objDocument.Vd_InitialPath(out bExpand);
        if (lstFirst == null)
        {   /* An empty document: nothing to select. */
            return;
        }

        if (bExpand)
        {   /* The format asks for it opened (JSON's top value): one level. */
            VTV_ExpandPath(lstFirst);
        }
        /* Selected and scrolled into view. */
        VTV_Select(lstFirst);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_RemoveNodesCounted:                                                */
    /*                                                                        */
    /* Removes one node (or every node of a collection) from the control,     */
    /* with a progress window when the removal will be slow.  It is one long  */
    /* call into the control, which cannot report as it goes, so the window   */
    /* is put up BEFORE it, and WndProc moves the bar as items are deleted.   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     colNodes  : the collection the node is in.                         */
    /*     iPos  : the node's position; -1 to clear the whole collection.     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the node (or every node) is gone.                           */
    /*------------------------------------------------------------------------*/
    void Vtv_RemoveNodesCounted(TreeNodeCollection colNodes, int iPos)
    {
        long lNodes;                       // tree nodes going
        BMF_clsBusyModalForm frmProgress;  // progress window, shown only if this is slow
        int i;

        /* How many tree nodes are going (only populated ones exist). */
        lNodes = 0;
        if (iPos < 0)
        {   /* The whole collection. */
            for (i = 0; i < colNodes.Count; i++)
            {   /* One node per iteration: itself and every node under it. */
                lNodes += 1 + colNodes[i].GetNodeCount(true);
            }
        }
        else
        {   /* One node and its branch. */
            lNodes = 1 + colNodes[iPos].GetNodeCount(true);
        }

        if (lNodes * VTV_CLEARUSPERNODE / 1000 < VTV_SHOWDELAYMS)
        {   /* Quick by the measured cost per node (microseconds, against   */
            /* the show delay in milliseconds): no progress needed.         */
            Vtv_RemoveNodesNow(colNodes, iPos);
            return;
        }

        /* Slow: the progress window, in the program's dialog font,   */
        /* named with the document's name as the editor gives it      */
        /* (Vtv_DocName).                                             */
        frmProgress = BMF_clsBusyModalForm.BMF_BeginUiProgress(FindForm(), Vtv_fntDialog, "Updating the tree",
            Vtv_DocName(), "BifrostFM Editor", VTV_SHOWDELAYMS);
        try
        {   /* The counted removal; the finally takes the counting and      */
            /* the window down whatever happens.                            */
            /* The window shown NOW, not after the delay: the removal is    */
            /* one call, inside which it must not appear (BusyModalForm).   */
            /* Then WndProc's counting armed: two units per node, as the    */
            /* control reads each once and deletes each once.  Drawing is   */
            /* held off for the removal itself.  (A throw would skip        */
            /* EndUpdate: the finally does not undo BeginUpdate.)           */
            frmProgress.BMF_ShowNow();
            Vtv_frmProgress = frmProgress;
            Vtv_lDone = 0;
            Vtv_lTotal = lNodes * 2;
            Vtv_bCountDeletes = true;
            BeginUpdate();
            Vtv_RemoveNodesNow(colNodes, iPos);
            EndUpdate();
        }
        finally
        {   /* Always: the counting off, and the window closed and   */
            /* released.                                             */
            Vtv_bCountDeletes = false;
            Vtv_frmProgress = null;
            frmProgress.BMF_EndUiProgress();
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_RemoveNodesNow:                                                    */
    /*                                                                        */
    /* Removes one node, or clears a collection: the bare removal, which      */
    /* Vtv_RemoveNodesCounted wraps with its counting when it will be slow.   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     colNodes : the collection.                                         */
    /*     iPos : the node's position; -1 to clear the collection.            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the node or nodes are gone.                                 */
    /*------------------------------------------------------------------------*/
    static void Vtv_RemoveNodesNow(TreeNodeCollection colNodes, int iPos)
    {
        if (iPos < 0)
        {   /* Everything. */
            colNodes.Clear();
        }
        else
        {   /* The one node. */
            colNodes.RemoveAt(iPos);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_DocName:                                                           */
    /*                                                                        */
    /* The name the tree's progress windows show: the editor's, through the   */
    /* call it bound (VTV_BindDocName) -- the name the user knows the file    */
    /* by, an image entry's own name rather than its read-out copy's.  With   */
    /* no call bound, the file name of the path the document was read from.   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the name, or "Untitled".                                  */
    /*------------------------------------------------------------------------*/
    string Vtv_DocName()
    {
        if (Vtv_pfnDocName != null)
        {   /* The editor names the document: asked now, so its current   */
            /* name is shown.                                             */
            return(Vtv_pfnDocName());
        }

        if (Vtv_doc == null || Vtv_doc.Vd_sPath == null)
        {   /* No document, or no path recorded for it: no file name to   */
            /* show.                                                      */
            return("Untitled");
        }

        /* The file's name without its folder. */
        return(System.IO.Path.GetFileName(Vtv_doc.Vd_sPath));
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_MakeNode:                                                          */
    /*                                                                        */
    /* A new, unpopulated tree node for one item: labelled and coloured, with */
    /* a placeholder child if it is a container with items.                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objContainer     : the item's container.                           */
    /*     lstContainer  : the container's path.                              */
    /*     iPos          : the item's position.                               */
    /*     dictKeyCounts : the container's key counts (Vn_KeyCounts).         */
    /*     iElementIndex : an array element's index if the caller is counting */
    /*                     them as it goes; -1 to have it counted here.       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     TreeNode : the node, not in any tree yet.                          */
    /*------------------------------------------------------------------------*/
    TreeNode Vtv_MakeNode(VoorheesNode objContainer, List<int> lstContainer,
        int iPos, Dictionary<string, int> dictKeyCounts, int iElementIndex)
    {
        TreeNode nodTree;       // the node
        VoorheesItem objItem;   // its item
        VTT_clsTreeTag objTag;  // its tag
        bool bHasChildren;      // it is a container with items

        /* The item, and whether it is a container with items -- the only   */
        /* kind of node that has children to make later.                    */
        objItem = objContainer.Vn_lstItems[iPos];
        bHasChildren = objItem.Vi_IsValue() && objItem.Vi_node.Vn_IsContainer()
            && objItem.Vi_node.Vn_Count() > 0;

        /* The node with its tag -- populated already when there is   */
        /* nothing to populate -- and its label and colours.          */
        objTag = VTT_clsTreeTag.VTT_Create(!bHasChildren);
        nodTree = new TreeNode();
        nodTree.Tag = objTag;
        Vtv_SetLabel(nodTree, objContainer, lstContainer, iPos, dictKeyCounts,
            iElementIndex);

        if (bHasChildren)
        {   /* Not populated yet: a placeholder child (a node with no Tag)   */
            /* gives it an expand box; opening it replaces the placeholder   */
            /* with the real children.                                       */
            nodTree.Nodes.Add(new TreeNode());
        }

        /* Labelled, ready to insert. */
        return(nodTree);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_SetLabel:                                                          */
    /*                                                                        */
    /* Works out an item's label and colours (see the file header) and puts   */
    /* them on its tree node, changing the node's text and colour only if     */
    /* they differ (each costs a message to the control).                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     nodTree      : the node.                                           */
    /*     objContainer     : the item's container.                           */
    /*     lstContainer  : the container's path.                              */
    /*     iPos          : the item's position.                               */
    /*     dictKeyCounts : the container's key counts.                        */
    /*     iElementIndex : an array element's index if the caller is counting */
    /*                     them as it goes (a loop over every item must, or   */
    /*                     labelling n elements costs n squared); -1 to have  */
    /*                     it counted here, for a single label.               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the node's text and colour are set, and its tag says where  */
    /*            the grey tail, the key and the value (with its colour       */
    /*            role) are in the label.                                     */
    /*------------------------------------------------------------------------*/
    void Vtv_SetLabel(TreeNode nodTree, VoorheesNode objContainer,
        List<int> lstContainer, int iPos, Dictionary<string, int> dictKeyCounts,
        int iElementIndex)
    {
        VoorheesItem objItem;   // the item
        VTT_clsTreeTag objTag;  // the node's tag
        string sLabel;          // the label being built
        string sName;           // key, index or "root"
        string sOpen;           // a container label's opening bracket
        string sClose;          // its closing bracket
        string sSeparator;      // between a plain value's name and value
        string sValue;          // a plain value as its label shows it, or null for none
        string sLineComment;    // the comment on its line
        string sRaw;            // raw text, for the raw option
        List<int> lstPath;      // the item's path, for the raw option
        bool bTruncated;        // the raw text was cut
        int iCount;             // copies of the member's key
        int iMutedFrom;         // where the grey tail starts
        int iValueFrom;         // where the editable value starts
        int iKeyFrom;           // where the key (name) starts, -1 for none
        int iKeyLength;         // its length
        int iValueLength;       // how long the shown value is, 0 for none
        int iValueRole;         // its colour role (VF_ROLE_), -1 for none
        int iBreak;             // first line break in the raw text
        Color clrMain;          // colour of the label (all of it but the tail)

        /* The item and the node's tag, and every run of the label marked   */
        /* absent until the label below sets it; the tree's own colour.     */
        objItem = objContainer.Vn_lstItems[iPos];
        objTag = (VTT_clsTreeTag)nodTree.Tag;
        iMutedFrom = -1;
        iValueFrom = 0;
        iKeyFrom = -1;
        iKeyLength = 0;
        iValueLength = 0;
        iValueRole = -1;
        clrMain = Color.Empty;

        if (objItem.Vi_iKind == VoorheesItem.VI_MEMBER
            && dictKeyCounts.TryGetValue(objItem.Vi_sKey, out iCount) && iCount > 1)
        {   /* A key that appears more than once in its object: the whole   */
            /* label in the duplicate colour, in either view.               */
            clrMain = VTV_COLORDUPLICATE;
        }

        if (Vtv_bShowRaw)
        {   /* Raw option: the first line of the item's text, as in the file.   */
            /* The item's text from the file, asked for only up to four         */
            /* label-widths: the label shows just its first line, cut to        */
            /* VTV_LEAFMAX, so whether more was cut off (bTruncated) is         */
            /* not needed.  Then everything from the first line break           */
            /* dropped, and the line made fit for a label.                      */
            lstPath = new List<int>(lstContainer);
            lstPath.Add(iPos);
            sRaw = VoorheesFormat.Vf_RenderItemLimited(Vtv_doc, lstPath,
                VTV_LEAFMAX * 4, out bTruncated);
            iBreak = sRaw.IndexOfAny(new char[] { '\r', '\n' });
            if (iBreak >= 0)
            {   /* Several lines: the first one. */
                sRaw = sRaw.Substring(0, iBreak);
            }
            sLabel = VTV_OneLine(sRaw);

            if (objItem.Vi_iKind == VoorheesItem.VI_COMMENT)
            {   /* A comment entry: all grey, drawn so by the control itself. */
                clrMain = VTV_COLORMUTED;
            }
        }
        else if (objItem.Vi_iKind == VoorheesItem.VI_COMMENT)
        {   /* A comment entry: its words, all grey, drawn so by the control itself. */
            sLabel = VTV_COMMENTPREFIX + VTV_OneLine(
                VoorheesFormat.Vf_CommentText(Vtv_doc.Vd_iFormat, objItem.Vi_sComment));
            iValueFrom = VTV_COMMENTPREFIX.Length;
            clrMain = VTV_COLORMUTED;
        }
        else
        {   /* A value: name, then value or brackets, as its format labels them.   */
            /* The name: key, index or root (JSON), counted by the format if       */
            /* the caller is not counting element indexes.                         */
            sName = VTV_OneLine(VoorheesFormat.Vf_ItemName(Vtv_doc, objContainer,
                iPos, iElementIndex));

            /* The name is the key part (View > Color Keys colours it):   */
            /* inside the brackets of a container, first for a value.     */
            iKeyLength = sName.Length;
            if (VoorheesFormat.Vf_LabelBrackets(Vtv_doc.Vd_iFormat,
                objItem.Vi_node.Vn_iKind, out sOpen, out sClose))
            {   /* A container: its brackets round the name (JSON {name}, [name]). */
                sLabel = sOpen + sName + sClose;
                iKeyFrom = sOpen.Length;
            }
            else
            {   /* Plain value: name, separator, value (the value is what an in-place edit changes). */
                sValue = VoorheesFormat.Vf_LabelValue(Vtv_doc, objItem.Vi_node);
                if (sValue == null)
                {   /* No value to show (an INI key on its own): the name alone. */
                    sLabel = sName;
                    iValueFrom = sName.Length;
                }
                else
                {   /* Name, separator, value.  The value is a run of its own   */
                    /* for View > Color Keys, coloured by the role its format   */
                    /* gives its kind (text,                                    */
                    /* number, boolean, null, binary).                          */
                    sSeparator = VoorheesFormat.Vf_LabelSeparator(Vtv_doc.Vd_iFormat);
                    sValue = VTV_OneLine(sValue);
                    sLabel = sName + sSeparator + sValue;
                    iValueFrom = sName.Length + sSeparator.Length;
                    iValueLength = sValue.Length;
                    iValueRole = VoorheesFormat.Vf_ValueRole(objItem.Vi_node.Vn_iKind);
                }
                /* A plain value's name starts the label. */
                iKeyFrom = 0;
            }

            if (clrMain == VTV_COLORDUPLICATE)
            {   /* A repeated key: said in words too, not by colour alone. */
                sLabel += VTV_DUPLICATEMARK;
            }

            /* Comments after it, in grey. */
            sLineComment = objItem.Vi_LineComment();
            if (sLineComment != null)
            {   /* The comment on its line, as its format shows it (JSON "// words"). */
                iMutedFrom = sLabel.Length;
                sLabel += "  " + VTV_OneLine(VoorheesFormat.Vf_LabelLineComment(
                    Vtv_doc.Vd_iFormat, sLineComment));
            }

            if (objItem.Vi_bOddComments)
            {   /* Comments only Raw can edit: a marker. */
                if (iMutedFrom < 0)
                {   /* The grey part starts here. */
                    iMutedFrom = sLabel.Length;
                }
                sLabel += VTV_ODDMARK;
            }
        }

        /* Where the runs are, kept on the tag for what reads the label   */
        /* later: the grey tail's post-paint, the in-place edit (the      */
        /* key part it keeps in view) and View > Color Keys.  Then the    */
        /* colour, set only if it differs: each set is a message to the   */
        /* control.                                                       */
        objTag.VTT_iMutedFrom = iMutedFrom;
        objTag.VTT_iValueFrom = iValueFrom;
        objTag.VTT_iKeyFrom = iKeyFrom;
        objTag.VTT_iKeyLength = iKeyLength;
        objTag.VTT_iValueLength = iValueLength;
        objTag.VTT_iValueRole = iValueRole;
        if (nodTree.ForeColor != clrMain)
        {   /* A different colour: set it (Color.Empty is the tree's own). */
            nodTree.ForeColor = clrMain;
        }

        if (nodTree.Text != sLabel)
        {   /* Changed: set it (this redraws the node), and only then, for   */
            /* the same reason.                                              */
            nodTree.Text = sLabel;
        }
    }

    /*------------------------------------------------------------------------*/
    /* VTV_OneLine:                                                           */
    /*                                                                        */
    /* Text fit for a one-line label: each line break shown as a return       */
    /* arrow (CRLF as one), the indentation the next line starts with         */
    /* collapsed to a single space after the arrow (a multi-line comment's    */
    /* second line is usually indented to line up in the file, which only     */
    /* spreads the label out), tabs as spaces, cut to VTV_LEAFMAX characters  */
    /* with "..." if longer.                                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText : the text.                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the label text.                                           */
    /*------------------------------------------------------------------------*/
    public static string VTV_OneLine(string sText)
    {
        string sLine;          // the text on one line
        StringBuilder sbText;  // the text with indentation collapsed
        bool bAfterBreak;      // the last character was a line break
        bool bSkipped;         // indentation was skipped after it
        int i;

        /* Every line break as the return arrow: CRLF first, so it is one   */
        /* arrow and not two, then a lone LF or CR.                         */
        sLine = sText.Replace("\r\n", VTV_RETURNMARK).Replace("\n", VTV_RETURNMARK)
            .Replace("\r", VTV_RETURNMARK);

        /* Indentation after each line break: one space. */
        sbText = new StringBuilder(sLine.Length);
        bAfterBreak = false;
        bSkipped = false;
        for (i = 0; i < sLine.Length; i++)
        {   /* One character per iteration. */
            if (bAfterBreak && (sLine[i] == ' ' || sLine[i] == '\t'))
            {   /* Indentation of the next line: skipped. */
                bSkipped = true;
                continue;
            }

            if (bSkipped)
            {   /* The indentation ended: one space in its place. */
                sbText.Append(' ');
            }
            /* The character kept, noting whether it is an arrow (so the   */
            /* indentation after it is skipped).                           */
            bSkipped = false;
            bAfterBreak = (sLine[i] == VTV_RETURNMARK[0]);
            sbText.Append(sLine[i]);
        }

        if (bSkipped)
        {   /* Indentation at the very end: one space for it too. */
            sbText.Append(' ');
        }
        /* Any tab left inside a line as a space. */
        sLine = sbText.ToString().Replace('\t', ' ');

        if (sLine.Length > VTV_LEAFMAX)
        {   /* Too long for a label: cut so that with "..." it is exactly   */
            /* VTV_LEAFMAX characters -- which is why JsonEditForm.cs's     */
            /* in-place edit takes the value from the document, never       */
            /* from the label.                                              */
            sLine = sLine.Substring(0, VTV_LEAFMAX - 3) + "...";
        }

        /* Fit for a label. */
        return(sLine);
    }

    /*------------------------------------------------------------------------*/
    /* VTV_PathOf:                                                            */
    /*                                                                        */
    /* A tree node's position path: its index at each level, top first,       */
    /* which is the item's path in the document.                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     nodTree : the node.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<int> : the path.                                              */
    /*------------------------------------------------------------------------*/
    public static List<int> VTV_PathOf(TreeNode nodTree)
    {
        List<int> lstPath;                               // positions, gathered bottom first

        /* Climbed from the node to the top, each level's index noted on   */
        /* the way, then turned round.                                     */
        lstPath = new List<int>();
        while (nodTree != null)
        {   /* One level per iteration: the node's index, then its parent. */
            lstPath.Add(nodTree.Index);
            nodTree = nodTree.Parent;
        }
        lstPath.Reverse();

        /* Top first: the item's path in the document. */
        return(lstPath);
    }

    /*------------------------------------------------------------------------*/
    /* VTV_NodeAt:                                                            */
    /*                                                                        */
    /* The tree node at a path, optionally creating unpopulated levels on the */
    /* way down.  Without that, a path through a level not yet populated      */
    /* finds nothing.                                                         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstPath      : the path.                                           */
    /*     bMaterialize : populate levels on the way.                         */
    /*     bNearest     : return the deepest node found when the path goes    */
    /*                    further than the tree does (or than the levels      */
    /*                    already populated, without bMaterialize).           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     TreeNode : the node, the deepest one found (bNearest), or null.    */
    /*------------------------------------------------------------------------*/
    public TreeNode VTV_NodeAt(List<int> lstPath, bool bMaterialize,
        bool bNearest)
    {
        TreeNodeCollection colNodes;  // the level being looked in
        TreeNode nodTree;             // the node reached
        int i;

        /* Down from the top level, no node reached yet. */
        colNodes = Nodes;
        nodTree = null;
        for (i = 0; i < lstPath.Count; i++)
        {   /* One level per iteration: the path's index at that level. */
            if (lstPath[i] < 0 || lstPath[i] >= colNodes.Count
                || colNodes[lstPath[i]].Tag == null)
            {   /* No node there: past the level's end, or only a    */
                /* placeholder (no Tag), which stands for no item.   */
                if (bNearest)
                {   /* The deepest found (null if not even the first). */
                    return(nodTree);
                }
                return(null);
            }

            /* The node at this level. */
            nodTree = colNodes[lstPath[i]];
            if (i + 1 < lstPath.Count)
            {   /* Going deeper: the node's children must exist. */
                if (!((VTT_clsTreeTag)nodTree.Tag).VTT_bPopulated)
                {   /* Not populated. */
                    if (!bMaterialize)
                    {   /* Not asked to create it: stop. */
                        if (bNearest)
                        {   /* This is as deep as it goes. */
                            return(nodTree);
                        }
                        return(null);
                    }
                    /* Asked to: its children made (the same node comes   */
                    /* back, back in its place).                          */
                    nodTree = Vtv_Populate(nodTree);
                }
                /* The next level is this node's children. */
                colNodes = nodTree.Nodes;
            }
        }

        /* The node at the end of the path. */
        return(nodTree);
    }

    /*------------------------------------------------------------------------*/
    /* VTV_Select:                                                            */
    /*                                                                        */
    /* Selects the item at a path (or its nearest existing ancestor), making  */
    /* the levels down to it, and scrolls it into view.  When the selection   */
    /* moves, selection events fire and the editor loads its panel from       */
    /* them.  Whether re-selecting the node already selected raises any is    */
    /* not established -- that is Windows' tree control's own behaviour,      */
    /* undocumented and not in this tree to read -- so after an edit the      */
    /* editor reloads its panel itself (JsonEditForm.cs, Jsn_DocFinish)       */
    /* rather than relying on this either way.                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstPath : the path.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : a node is selected, if the tree has any.                    */
    /*------------------------------------------------------------------------*/
    public void VTV_Select(List<int> lstPath)
    {
        TreeNode nodTree;                               // the node to select

        /* The node at the path, making unopened levels on the way, or the   */
        /* deepest one that exists.                                          */
        nodTree = VTV_NodeAt(lstPath, true, true);
        if (nodTree == null && Nodes.Count > 0)
        {   /* Nothing on the path at all: the first top-level node. */
            nodTree = Nodes[0];
        }

        if (nodTree != null)
        {   /* Select it and scroll it into view. */
            SelectedNode = nodTree;
            nodTree.EnsureVisible();
        }
    }

    /*------------------------------------------------------------------------*/
    /* VTV_ExpandPath:                                                        */
    /*                                                                        */
    /* Opens the container at a path, populating it first.                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstPath : the container's path.                                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the node is populated and expanded, if it exists.           */
    /*------------------------------------------------------------------------*/
    public void VTV_ExpandPath(List<int> lstPath)
    {
        TreeNode nodTree;                               // the container's node

        /* The node itself, the levels above it made on the way (the node   */
        /* is the end of the path, so its own children are not).            */
        nodTree = VTV_NodeAt(lstPath, true, false);
        if (nodTree == null)
        {   /* No node at that path: nothing to open. */
            return;
        }

        if (!((VTT_clsTreeTag)nodTree.Tag).VTT_bPopulated)
        {   /* Create its children first, so the expand shows them. */
            nodTree = Vtv_Populate(nodTree);
        }
        /* Opened; for a populated node OnBeforeExpand lets it through. */
        nodTree.Expand();
    }

    /*------------------------------------------------------------------------*/
    /* OnBeforeExpand:                                                        */
    /*                                                                        */
    /* The Framework's expand hook: hands it to Vtv_OnBeforeExpand below,     */
    /* which alone decides whether the base expand runs now or the node is    */
    /* held and populated after the event -- so no base call is made here.    */
    /*                                                                        */
    /* An override because the framework requires it: a TreeView hears of an  */
    /* expand before it happens only through this virtual method.             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     evExpand : the node; its Cancel holds the expand.                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the node opens now, or is queued to.                        */
    /*------------------------------------------------------------------------*/
    protected override void OnBeforeExpand(TreeViewCancelEventArgs evExpand)
    {
        /* The expand-or-hold decision and the base call are in the helper. */
        Vtv_OnBeforeExpand(evExpand);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_OnBeforeExpand:                                                    */
    /*                                                                        */
    /* The user is opening a node.  A populated one opens as usual.  An       */
    /* unpopulated one is held (the event is cancelled) and populated and     */
    /* opened once this event is over, through the message queue: the         */
    /* populate removes and re-inserts the node, which must not happen        */
    /* inside the control's own expand.                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     evArgs : the node; evArgs.Cancel holds the expand.                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the node opens now, or is queued to.                        */
    /*------------------------------------------------------------------------*/
    void Vtv_OnBeforeExpand(TreeViewCancelEventArgs evArgs)
    {
        List<int> lstPath;                               // the node's path

        if (evArgs.Node.Tag != null && !((VTT_clsTreeTag)evArgs.Node.Tag).VTT_bPopulated)
        {   /* Not populated: the children must be made first, and that   */
            /* cannot happen inside the control's own expand.             */

            /* This expand held, so the control opens nothing now. */
            evArgs.Cancel = true;

            /* The populate and open posted for after the event, to the     */
            /* named VTV_ExpandPath with the node's PATH as its argument.   */
            /* The populate takes the node out and puts it back (the same   */
            /* node object -- Vtv_Populate); why the node is found again    */
            /* by its path rather than passed itself is not recorded.       */
            lstPath = VTV_PathOf(evArgs.Node);
            BeginInvoke(new Vtv_FnExpandPath(VTV_ExpandPath), new object[] { lstPath });
            return;
        }

        /* Populated already: the ordinary expand, through the base class   */
        /* (which raises the BeforeExpand event).                           */
        base.OnBeforeExpand(evArgs);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_Populate:                                                          */
    /*                                                                        */
    /* Creates a container node's children -- fast even for tens of           */
    /* thousands, by filling the node while it is out of the control -- with  */
    /* a progress window when the fill will be slow.  Its place, the          */
    /* selection and the scroll position are as they were afterwards.         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     nodTree : the unpopulated container node.                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     TreeNode : the same node, now populated and back in the tree.      */
    /*------------------------------------------------------------------------*/
    TreeNode Vtv_Populate(TreeNode nodTree)
    {
        List<int> lstPath;                 // the container's path
        VoorheesNode objContainer;         // the container
        Dictionary<string, int> dictKeys;  // its key counts
        TreeNode[] arrChildren;            // the new children
        TreeNodeCollection colNodes;       // the collection the node is in
        TreeNode nodTop;                   // the first visible node, to restore the scroll
        BMF_clsBusyModalForm frmProgress;  // progress window, shown only if this is slow
        bool bSelected;                    // the node was selected
        int iIndex;                        // its position in colNodes
        int iElement;                      // array index of the next value
        int i;

        /* The container the node stands for (by its path in the document),   */
        /* its key counts for duplicate marks, and room for its children.     */
        lstPath = VTV_PathOf(nodTree);
        objContainer = Vtv_doc.Vd_NodeAt(lstPath);
        dictKeys = Vtv_doc.Vd_KeyCounts(objContainer);
        arrChildren = new TreeNode[objContainer.Vn_Count()];

        /* No progress window unless the measured cost per node says the   */
        /* fill will outlast the show delay.                               */
        frmProgress = null;
        if (arrChildren.Length * VTV_FILLUSPERNODE / 1000 >= VTV_SHOWDELAYMS)
        {   /* Slow: progress, with the window up before the control's long   */
            /* call -- inside it the window must not appear.                  */
            frmProgress = BMF_clsBusyModalForm.BMF_BeginUiProgress(FindForm(), Vtv_fntDialog, "Opening",
                Vtv_DocName(), "BifrostFM Editor", VTV_SHOWDELAYMS);
            frmProgress.BMF_ShowNow();
        }

        /* Busy while the node is out and back: the selection the control   */
        /* moves meanwhile is not the user's.                               */
        Vtv_iBusyDepth++;
        try
        {   /* The fill; the finally lowers the busy depth and closes the   */
            /* window whatever happens.                                     */
            /* Label every child: the first half of the progress.  Array    */
            /* indexes are counted on the way, one per value.               */
            iElement = 0;
            for (i = 0; i < arrChildren.Length; i++)
            {   /* One child per iteration, its array index passed in (so   */
                /* the format need not count them for each: n squared).     */
                arrChildren[i] = Vtv_MakeNode(objContainer, lstPath, i, dictKeys,
                    iElement);
                if (objContainer.Vn_lstItems[i].Vi_IsValue())
                {   /* A value: the next one has the next index. */
                    iElement++;
                }

                if (frmProgress != null && i % VTV_PROGRESSSTEP == 0)
                {   /* Report now and then: the labelling is the first half   */
                    /* of twice the child count.                              */
                    frmProgress.BMF_ReportUiProgress(i, (long)arrChildren.Length * 2);
                }
            }

            /* Take the node out, fill it, put it back.  Adding children    */
            /* one at a time to a node in the control is slow -- it walks   */
            /* to the end of the sibling list for each (measured: 40,000    */
            /* children about 20 s) -- but it takes a whole DETACHED        */
            /* branch fast.  First, with drawing held off, what must be     */
            /* put back afterwards: the scroll position (the top visible    */
            /* node), whether this node was selected, and its collection    */
            /* and place.                                                   */
            BeginUpdate();
            nodTop = TopNode;
            bSelected = (SelectedNode == nodTree);
            if (nodTree.Parent != null)
            {   /* A child: its parent's collection. */
                colNodes = nodTree.Parent.Nodes;
            }
            else
            {   /* Top level. */
                colNodes = Nodes;
            }
            /* Out of the control; the placeholder replaced by the real   */
            /* children while it is detached; marked populated.           */
            iIndex = nodTree.Index;
            colNodes.RemoveAt(iIndex);
            nodTree.Nodes.Clear();
            nodTree.Nodes.AddRange(arrChildren);
            ((VTT_clsTreeTag)nodTree.Tag).VTT_bPopulated = true;

            /* The control inserts the branch: the second half, counted. */
            if (frmProgress != null)
            {   /* Count the inserts in WndProc, carrying on from where the   */
                /* labelling left the bar (half way).                         */
                Vtv_frmProgress = frmProgress;
                Vtv_lDone = arrChildren.Length;
                Vtv_lTotal = (long)arrChildren.Length * 2;
                Vtv_bCountInserts = true;
            }
            try
            {   /* The branch back at its place, in one call. */
                colNodes.Insert(iIndex, nodTree);
            }
            finally
            {   /* Always: the counting off, so no later insert moves a   */
                /* bar that is gone.                                      */
                Vtv_bCountInserts = false;
                Vtv_frmProgress = null;
            }

            if (bSelected)
            {   /* It was selected (taking it out may have moved the   */
                /* selection): select it again.  Still busy, so the    */
                /* editor ignores it -- its panel already shows this   */
                /* entry.                                              */
                SelectedNode = nodTree;
            }

            if (nodTop != null && nodTop.TreeView == this)
            {   /* Keep the view where it was: the same top node, if it is   */
                /* still in this tree -- a guard; even this node itself is   */
                /* back in it by now.                                        */
                TopNode = nodTop;
            }
            /* Drawing on again.  (A throw before here skips it: the   */
            /* finally does not undo BeginUpdate.)                     */
            EndUpdate();
        }
        finally
        {   /* However the fill ended: the progress window first, then the   */
            /* busy depth and the editor told -- in that order, so the       */
            /* window is gone before the editor's receiver runs.             */
            try
            {   /* The window closed while the fill still counts as this    */
                /* rearrangement: should closing it raise window messages   */
                /* of its own (not established), selection events from      */
                /* them are ignored.                                        */
                if (frmProgress != null)
                {   /* This fill put one up: taken down and released. */
                    frmProgress.BMF_EndUiProgress();
                }
            }
            finally
            {   /* Even if closing the window throws: the busy depth down,    */
                /* or the editor would ignore selections for good, then the   */
                /* editor told (its receiver only posts).                     */
                Vtv_iBusyDepth--;
                Vtv_ReportReturn();
            }
        }

        /* The node, populated, in its place (the same object, so the   */
        /* caller's reference stays good).                              */
        return(nodTree);
    }

    /*------------------------------------------------------------------------*/
    /* VTV_ApplyChange:                                                       */
    /*                                                                        */
    /* Brings the tree up to date with one change the document has ALREADY    */
    /* made (or, with bReverse, already undone), touching only the parts of   */
    /* the tree that exist -- never rebuilding anything.                      */
    /*                                                                        */
    /* It reads items from the document as the WHOLE step left it, so it is   */
    /* correct only while no step holds two inserts or removes in one         */
    /* container: today every action records at most one (VoorheesDocument.cs */
    /* Vd_ActInsert and Vd_ActRemove are the only places that record them,    */
    /* each once per action).  A step with two -- undone, say -- could read   */
    /* the wrong item for the earlier one.                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objChange       : the change.                                      */
    /*     bReverse : it was undone rather than made.                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the tree matches the document.                              */
    /*------------------------------------------------------------------------*/
    public void VTV_ApplyChange(VoorheesChange objChange, bool bReverse)
    {
        TreeNode nodParent;           // the container's node, or null for the top level
        TreeNodeCollection colNodes;  // the container's child nodes
        VoorheesNode objContainer;    // the container, as it is now
        VoorheesItem objItemBefore;   // the item before the change
        VoorheesItem objItemAfter;    // the item after it
        bool bInsert;                 // the change inserted an item
        bool bRemove;                 // the change removed an item
        List<string> lstKeys;         // keys whose duplicate marks may change

        /* What the change did, in the direction it was applied. */
        bInsert = false;
        bRemove = false;
        objItemBefore = null;
        objItemAfter = null;
        if (objChange.Vch_iKind == VoorheesChange.VCH_SETITEM)
        {   /* One item replaced by another. */
            if (bReverse)
            {   /* Undone: new back to old. */
                objItemBefore = objChange.Vch_itemNew;
                objItemAfter = objChange.Vch_itemOld;
            }
            else
            {   /* Made: old to new. */
                objItemBefore = objChange.Vch_itemOld;
                objItemAfter = objChange.Vch_itemNew;
            }
        }
        else if ((objChange.Vch_iKind == VoorheesChange.VCH_INSERT) != bReverse)
        {   /* An insert made, or a remove undone: an item went in -- read   */
            /* from the document at the position (see the header for when    */
            /* that is the right item).                                      */
            bInsert = true;
            objItemAfter = Vtv_doc.Vd_NodeAt(objChange.Vch_lstPath).Vn_lstItems[objChange.Vch_iPos];
        }
        else
        {   /* A remove made, or an insert undone: an item came out. */
            bRemove = true;
            if (bReverse)
            {   /* An undone insert: the item that had been inserted. */
                objItemBefore = objChange.Vch_itemNew;
            }
            else
            {   /* A remove: the item removed. */
                objItemBefore = objChange.Vch_itemOld;
            }
        }

        /* The container the change was made in, as the document has it. */
        objContainer = Vtv_doc.Vd_NodeAt(objChange.Vch_lstPath);

        /* A step's changes are replayed against the document as the WHOLE     */
        /* step left it.  A replacement whose item is not (or no longer) at    */
        /* its position there is changed again by a later change of the        */
        /* same step, in the order replayed -- an entry added and named in     */
        /* one step (an include's file, a registry key's hive and path),       */
        /* undone: the name comes off first, then the entry goes.  That        */
        /* later change brings the node in line; labelling it now would read   */
        /* an item that is not there.                                          */
        if (objChange.Vch_iKind == VoorheesChange.VCH_SETITEM
            && (objChange.Vch_iPos >= objContainer.Vn_Count()
            || !ReferenceEquals(objContainer.Vn_lstItems[objChange.Vch_iPos], objItemAfter)))
        {   /* Superseded within the step: nothing to do now. */
            return;
        }

        /* Find the container's node; not in the tree means nothing to do. */
        if (objChange.Vch_lstPath.Count == 0)
        {   /* The document: the top level. */
            nodParent = null;
            colNodes = Nodes;
        }
        else
        {   /* A container: its node, only if the tree has it. */
            nodParent = VTV_NodeAt(objChange.Vch_lstPath, false, false);
            if (nodParent == null)
            {   /* Inside a part never populated: nothing to change -- it   */
                /* is built fresh from the document when it is opened.      */
                return;
            }

            if (!((VTT_clsTreeTag)nodParent.Tag).VTT_bPopulated)
            {   /* Unpopulated: only the placeholder needs to follow   */
                /* (whether the container still has items to open).    */
                Vtv_SyncPlaceholder(nodParent, objContainer);
                return;
            }
            /* Populated: its children are what changes. */
            colNodes = nodParent.Nodes;
        }

        /* Busy while the nodes change: the selection the control moves   */
        /* meanwhile is not the user's.                                   */
        Vtv_iBusyDepth++;
        try
        {   /* The change; the finally lowers the busy depth whatever   */
            /* happens.  Drawing held off throughout.                   */
            BeginUpdate();
            if (bInsert)
            {   /* A new node at the position, labelled with the   */
                /* container's key counts as they now are.         */
                colNodes.Insert(objChange.Vch_iPos, Vtv_MakeNode(objContainer, objChange.Vch_lstPath,
                    objChange.Vch_iPos, Vtv_doc.Vd_KeyCounts(objContainer), -1));
            }
            else if (bRemove)
            {   /* The node goes (counted if its branch is big).  Drawing   */
                /* is let go round it -- why is not recorded; the counted   */
                /* removal holds drawing off itself when it is slow.        */
                EndUpdate();
                Vtv_RemoveNodesCounted(colNodes, objChange.Vch_iPos);
                BeginUpdate();
            }
            else
            {   /* Replaced in place: relabelled, or a fresh node when a   */
                /* container came or went (Vtv_ReplaceItemNode).           */
                Vtv_ReplaceItemNode(colNodes, objContainer, objChange.Vch_lstPath, objChange.Vch_iPos,
                    objItemBefore, objItemAfter);
            }

            /* Siblings whose labels depend on this one: the other copies   */
            /* of its key before and after (their duplicate marks), and,    */
            /* below, every later array index when an item came or went.    */
            lstKeys = new List<string>();
            if (objItemBefore != null && objItemBefore.Vi_iKind == VoorheesItem.VI_MEMBER)
            {   /* Its old key's other copies. */
                lstKeys.Add(objItemBefore.Vi_sKey);
            }

            if (objItemAfter != null && objItemAfter.Vi_iKind == VoorheesItem.VI_MEMBER)
            {   /* Its new key's other copies. */
                lstKeys.Add(objItemAfter.Vi_sKey);
            }

            if (bInsert || bRemove || (objItemBefore != null && objItemAfter != null
                && (objItemBefore.Vi_IsValue() != objItemAfter.Vi_IsValue()
                || objItemBefore.Vi_sKey != objItemAfter.Vi_sKey)))
            {   /* Indexes or keys may have moved -- an item came or went,   */
                /* became or stopped being a value (array indexes count      */
                /* values only), or changed its key: relabel the siblings    */
                /* concerned.                                                */
                Vtv_RelabelSiblings(colNodes, objContainer, objChange.Vch_lstPath, objChange.Vch_iPos,
                    lstKeys, bInsert || bRemove);
            }

            if (nodParent != null)
            {   /* An empty container shows no expand box. */
                Vtv_SyncPlaceholder(nodParent, objContainer);
            }
            /* Drawing on again.  (A throw before here skips it: the   */
            /* finally does not undo BeginUpdate.)                     */
            EndUpdate();
        }
        finally
        {   /* However the change ended: the busy depth down, then the      */
            /* editor told (Vtv_ReportReturn), whose receiver only posts.   */
            Vtv_iBusyDepth--;
            Vtv_ReportReturn();
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_ReplaceItemNode:                                                   */
    /*                                                                        */
    /* SETITEM's work on the tree.  When the value node is the same object    */
    /* (a rename, a comment edit) or neither value is a container, the node   */
    /* is only relabelled, keeping its children and open state.  Otherwise    */
    /* (a container replaced, or a value turned into or out of a container)   */
    /* the node is replaced by a new unpopulated one, opened again if the old */
    /* one was open, and selected again if it (or something inside it) was    */
    /* selected.                                                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     colNodes         : the container's child nodes.                    */
    /*     objContainer    : the container.                                   */
    /*     lstContainer : its path.                                           */
    /*     iPos         : the item's position.                                */
    /*     objItemBefore   : the item before.                                 */
    /*     objItemAfter    : the item after.                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the node shows the new item.                                */
    /*------------------------------------------------------------------------*/
    void Vtv_ReplaceItemNode(TreeNodeCollection colNodes, VoorheesNode objContainer,
        List<int> lstContainer, int iPos, VoorheesItem objItemBefore,
        VoorheesItem objItemAfter)
    {
        TreeNode nodOld;       // the node now
        TreeNode nodNew;       // its replacement
        TreeNode nodSelected;  // the selected node
        bool bExpanded;        // the old node was open
        bool bWasSelected;     // it or something in it was selected
        bool bSameValue;       // the value node did not change
        bool bContainers;      // a container is involved

        /* The node there now, and what kind of replacement this is: the   */
        /* same value object (a rename, a comment edit), and whether a     */
        /* container is on either side -- only then can children be        */
        /* affected.                                                       */
        nodOld = colNodes[iPos];
        bSameValue = objItemBefore.Vi_IsValue() && objItemAfter.Vi_IsValue()
            && ReferenceEquals(objItemBefore.Vi_node, objItemAfter.Vi_node);
        bContainers = (objItemBefore.Vi_IsValue() && objItemBefore.Vi_node.Vn_IsContainer())
            || (objItemAfter.Vi_IsValue() && objItemAfter.Vi_node.Vn_IsContainer());

        if (bSameValue || !bContainers)
        {   /* Only the label changes: relabelled in place, its children   */
            /* and its open state kept.                                    */
            Vtv_SetLabel(nodOld, objContainer, lstContainer, iPos,
                Vtv_doc.Vd_KeyCounts(objContainer), -1);
            return;
        }

        /* A container came or went: a fresh node in its place.  First    */
        /* what to carry over: whether it was open, and whether the       */
        /* selection was on it or anywhere inside it (found by climbing   */
        /* from the selected node).                                       */
        bExpanded = nodOld.IsExpanded;
        nodSelected = SelectedNode;
        bWasSelected = false;
        while (nodSelected != null)
        {   /* One level up per iteration. */
            if (nodSelected == nodOld)
            {   /* The selection is this node or inside it. */
                bWasSelected = true;
                break;
            }
            /* Not this one: its parent. */
            nodSelected = nodSelected.Parent;
        }

        /* The new node, unopened, made before the old goes; the old one   */
        /* removed (counted if its branch is big) and the new put in its   */
        /* place.                                                          */
        nodNew = Vtv_MakeNode(objContainer, lstContainer, iPos,
            Vtv_doc.Vd_KeyCounts(objContainer), -1);
        Vtv_RemoveNodesCounted(colNodes, iPos);
        colNodes.Insert(iPos, nodNew);

        if (bExpanded && nodNew.Nodes.Count > 0)
        {   /* It was open and the new value has items: populated and   */
            /* opened, one level, as the old one was.                   */
            nodNew = Vtv_Populate(nodNew);
            nodNew.Expand();
        }

        if (bWasSelected)
        {   /* The selection was on it or inside it: on the new node       */
            /* now (whatever was selected inside has gone with the old).   */
            SelectedNode = nodNew;
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_RelabelSiblings:                                                   */
    /*                                                                        */
    /* Relabels the nodes of a container whose labels may have changed: with  */
    /* bIndexes, every unkeyed value (array element) from a position on       */
    /* (their indexes moved) -- and every member whose key is in a list,      */
    /* compared as the format compares keys (duplicate marks).  Only labels   */
    /* that differ are set.                                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     colNodes         : the container's child nodes.                    */
    /*     objContainer    : the container.                                   */
    /*     lstContainer : its path.                                           */
    /*     iFrom        : first position whose index may have moved.          */
    /*     lstKeys      : keys whose members to relabel.                      */
    /*     bIndexes     : indexes may have moved.                             */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the labels are current.                                     */
    /*------------------------------------------------------------------------*/
    void Vtv_RelabelSiblings(TreeNodeCollection colNodes, VoorheesNode objContainer,
        List<int> lstContainer, int iFrom, List<string> lstKeys, bool bIndexes)
    {
        Dictionary<string, int> dictKeys;  // the container's key counts
        StringComparer objComparer;        // how its keys compare
        VoorheesItem objItem;              // an item
        int iElement;                      // array index of the value at i
        int i;

        /* The container's key counts and key comparison as they now are,   */
        /* and the array index counted from the start, since an index       */
        /* counts the values before it.                                     */
        dictKeys = Vtv_doc.Vd_KeyCounts(objContainer);
        objComparer = Vtv_doc.Vd_KeyComparer(objContainer);
        iElement = 0;
        for (i = 0; i < colNodes.Count && i < objContainer.Vn_Count(); i++)
        {   /* One item per iteration, within both the nodes and the   */
            /* items (they match; the double bound is a guard).        */
            objItem = objContainer.Vn_lstItems[i];
            if (bIndexes && i >= iFrom
                && objItem.Vi_iKind == VoorheesItem.VI_ELEMENT)
            {   /* An unkeyed value (a JSON array element) whose index may have moved. */
                Vtv_SetLabel(colNodes[i], objContainer, lstContainer, i, dictKeys,
                    iElement);
            }
            else if (objItem.Vi_iKind == VoorheesItem.VI_MEMBER
                && Vtv_KeyListed(lstKeys, objItem.Vi_sKey, objComparer))
            {   /* A member sharing a key involved: its duplicate mark may change. */
                Vtv_SetLabel(colNodes[i], objContainer, lstContainer, i, dictKeys, -1);
            }

            if (objItem.Vi_IsValue())
            {   /* A value: the next one has the next index (comment   */
                /* entries take none).                                 */
                iElement++;
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_KeyListed:                                                         */
    /*                                                                        */
    /* Whether a key is in a list of keys, compared as the container's format */
    /* compares keys (so a format that ignores case finds "Name" for "name"). */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstKeys  : the keys.                                               */
    /*     sKey     : the key to look for.                                    */
    /*     objComparer : how keys compare (Vd_KeyComparer).                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when one of them is the same key.                      */
    /*------------------------------------------------------------------------*/
    static bool Vtv_KeyListed(List<string> lstKeys, string sKey,
        StringComparer objComparer)
    {
        int i;

        for (i = 0; i < lstKeys.Count; i++)
        {   /* One listed key per iteration (the list holds two at most). */
            if (objComparer.Equals(lstKeys[i], sKey))
            {   /* The same key. */
                return(true);
            }
        }

        /* Not listed. */
        return(false);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_SyncPlaceholder:                                                   */
    /*                                                                        */
    /* Keeps an unpopulated container's placeholder in step with whether the  */
    /* container has items (an expand box only when there is something to     */
    /* open), and marks a populated empty container as such.                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     nodTree  : the container's node.                                   */
    /*     objContainer : the container.                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the placeholder is added or removed as needed.              */
    /*------------------------------------------------------------------------*/
    static void Vtv_SyncPlaceholder(TreeNode nodTree, VoorheesNode objContainer)
    {
        VTT_clsTreeTag objTag;                             // the node's tag

        /* The node's tag says whether its children are real yet. */
        objTag = (VTT_clsTreeTag)nodTree.Tag;
        if (objTag.VTT_bPopulated)
        {   /* Populated: its children are real (an empty one has none,   */
            /* so no expand box either); nothing to sync.                 */
            return;
        }

        if (objContainer.Vn_Count() > 0 && nodTree.Nodes.Count == 0)
        {   /* Has items now: give it its expand box. */
            nodTree.Nodes.Add(new TreeNode());
        }
        else if (objContainer.Vn_Count() == 0 && nodTree.Nodes.Count > 0)
        {   /* Empty now: nothing to open, and nothing left to populate. */
            nodTree.Nodes.Clear();
            objTag.VTT_bPopulated = true;
        }
    }

    /*------------------------------------------------------------------------*/
    /* VTV_SetShowRaw:                                                        */
    /*                                                                        */
    /* Switches the raw-text labels on or off and relabels every node that    */
    /* exists.                                                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bShowRaw : raw labels wanted.                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : every label is redone.                                      */
    /*------------------------------------------------------------------------*/
    public void VTV_SetShowRaw(bool bShowRaw)
    {
        /* The option, which every later label follows. */
        Vtv_bShowRaw = bShowRaw;
        if (Vtv_doc == null)
        {   /* No document yet: its first labels will follow the option. */
            return;
        }

        /* Every node that exists relabelled, from the top level down,   */
        /* with drawing held off until all are done.                     */
        BeginUpdate();
        Vtv_RelabelAll(Nodes, Vtv_doc.Vd_root, new List<int>());
        EndUpdate();
    }

    /*------------------------------------------------------------------------*/
    /* VTV_SetColorKeys:                                                      */
    /*                                                                        */
    /* Switches coloured keys on or off (View > Color Keys).  Labels do not   */
    /* change -- only how they are drawn -- so the tree is just repainted.    */
    /* The colours are the role tables' (Vtv_arrNameColors,                   */
    /* Vtv_arrValueColors); a change to those is shown the same way, by a     */
    /* repaint.                                                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bColorKeys : names and values wanted in their roles' colours.      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the tree repaints.                                          */
    /*------------------------------------------------------------------------*/
    public void VTV_SetColorKeys(bool bColorKeys)
    {
        /* The option, then the whole tree repainted in (or out of) the   */
        /* role colours.                                                  */
        Vtv_bColorKeys = bColorKeys;
        Invalidate();
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_RelabelAll:                                                        */
    /*                                                                        */
    /* Relabels a level and every populated level below it.                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     colNodes         : the level's nodes.                              */
    /*     objContainer    : the container they show.                         */
    /*     lstContainer : its path.                                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the labels are current.                                     */
    /*------------------------------------------------------------------------*/
    void Vtv_RelabelAll(TreeNodeCollection colNodes, VoorheesNode objContainer,
        List<int> lstContainer)
    {
        Dictionary<string, int> dictKeys;                // the container's key counts
        List<int> lstChild;                              // a child's path
        int iElement;                                    // array index of the value at i
        int i;

        /* The level's key counts, and the array index counted as the   */
        /* loop goes (not by the format for each: n squared).           */
        dictKeys = Vtv_doc.Vd_KeyCounts(objContainer);
        iElement = 0;
        for (i = 0; i < colNodes.Count && i < objContainer.Vn_Count(); i++)
        {   /* One node per iteration: itself, then its branch if it has   */
            /* real children.                                              */
            Vtv_SetLabel(colNodes[i], objContainer, lstContainer, i, dictKeys, iElement);
            if (objContainer.Vn_lstItems[i].Vi_IsValue())
            {   /* A value: the next one has the next index. */
                iElement++;
            }

            if (((VTT_clsTreeTag)colNodes[i].Tag).VTT_bPopulated && colNodes[i].Nodes.Count > 0)
            {   /* Its children exist: relabel them too, one level down. */
                lstChild = new List<int>(lstContainer);
                lstChild.Add(i);
                Vtv_RelabelAll(colNodes[i].Nodes, objContainer.Vn_lstItems[i].Vi_node,
                    lstChild);
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* VTV_RefreshLabel:                                                      */
    /*                                                                        */
    /* Relabels one node from the document (after an in-place edit box put    */
    /* other text on it, say).                                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     nodTree : the node.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : its label is current.                                       */
    /*------------------------------------------------------------------------*/
    public void VTV_RefreshLabel(TreeNode nodTree)
    {
        List<int> lstPath;          // the node's path
        List<int> lstContainer;     // its container's path
        VoorheesNode objContainer;  // its container

        if (nodTree == null || nodTree.Tag == null || nodTree.TreeView != this)
        {   /* No node, a placeholder, or a node no longer in this tree   */
            /* (a caller may pass one it looked up before a change):      */
            /* nothing to relabel.                                        */
            return;
        }

        /* Where the node is: its path, its container's path, and the   */
        /* container in the document.                                   */
        lstPath = VTV_PathOf(nodTree);
        lstContainer = lstPath.GetRange(0, lstPath.Count - 1);
        objContainer = Vtv_doc.Vd_NodeAt(lstContainer);
        if (objContainer == null || lstPath[lstPath.Count - 1] >= objContainer.Vn_Count())
        {   /* The document has no item there -- the tree out of step,   */
            /* which should not happen: a guard.                         */
            return;
        }

        /* Relabelled from the document, its index counted here. */
        Vtv_SetLabel(nodTree, objContainer, lstContainer, lstPath[lstPath.Count - 1],
            Vtv_doc.Vd_KeyCounts(objContainer), -1);
    }

    /*------------------------------------------------------------------------*/
    /* VTV_PrepareValueEdit:                                                  */
    /*                                                                        */
    /* Gets a node ready for an in-place edit of its value (call it just      */
    /* before TreeNode.BeginEdit), so the box edits the FULL value with the   */
    /* key still in view.  The control's own edit box would cover the whole   */
    /* label and start with the label's text -- hiding the key, and holding   */
    /* a value cut to VTV_LEAFMAX.  VTV_EndValueEdit and VTV_RefreshLabel put */
    /* everything back.                                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     nodTree : the node.                                                */
    /*     sValue   : the full text to edit.                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the node shows its key part; the edit is prepared.          */
    /*------------------------------------------------------------------------*/
    public void VTV_PrepareValueEdit(TreeNode nodTree, string sValue)
    {
        VTT_clsTreeTag objTag;  // the node's tag
        string sPrefix;         // the label's key part

        /* The label's key part ("key: ", "3: ", "comment: "): the text   */
        /* before where the editable value starts, as the label set it.   */
        objTag = (VTT_clsTreeTag)nodTree.Tag;
        sPrefix = "";
        if (objTag.VTT_iValueFrom > 0 && objTag.VTT_iValueFrom <= nodTree.Text.Length)
        {   /* The label has a key part: keep it in view. */
            sPrefix = nodTree.Text.Substring(0, objTag.VTT_iValueFrom);
        }

        /* Any earlier edit forgotten; the full value kept for the box,   */
        /* which OnBeforeLabelEdit fills with it; no shift yet; and no    */
        /* grey tail, since the label is about to lose it.                */
        VTV_EndValueEdit();
        Vtv_sEditValue = sValue;
        Vtv_iEditShift = 0;
        objTag.VTT_iMutedFrom = -1;

        /* The value leaves the label for the box: no value run to colour. */
        objTag.VTT_iValueLength = 0;
        objTag.VTT_iValueRole = -1;
        if (sPrefix.Length > 0)
        {   /* The key part stays as the whole label, so the key stays   */
            /* readable; the box is to go after it, moved right by the   */
            /* key part's measured width (VTS_clsEditShifter).           */
            Vtv_iEditShift = Vtv_TextWidth(sPrefix);
            nodTree.Text = sPrefix;
        }
        else
        {   /* All value (the raw-text view): the box takes the label's   */
            /* place, as normal, not moved.                               */
            nodTree.Text = sValue;
        }
    }

    /*------------------------------------------------------------------------*/
    /* VTV_EndValueEdit:                                                      */
    /*                                                                        */
    /* Forgets a prepared or finished in-place edit: the value and the box's  */
    /* shift are dropped and the box is no longer watched.  The node's label  */
    /* is put back separately (VTV_RefreshLabel).                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : no in-place edit is prepared.                               */
    /*------------------------------------------------------------------------*/
    public void VTV_EndValueEdit()
    {
        /* No value waiting for a box, no shift, and no node whose key   */
        /* part the post-paint draws beside a box.                       */
        Vtv_sEditValue = null;
        Vtv_iEditShift = 0;
        Vtv_nodEdit = null;
        if (Vtv_objEditShifter != null)
        {   /* Stop watching the edit box: its subclassing released, so   */
            /* the box no longer comes through this program first.        */
            Vtv_objEditShifter.ReleaseHandle();
            Vtv_objEditShifter = null;
        }
    }

    /*------------------------------------------------------------------------*/
    /* OnBeforeLabelEdit:                                                     */
    /*                                                                        */
    /* The Framework's edit-starting hook: hands it to Vtv_OnBeforeLabelEdit  */
    /* below, which raises the event to the editor first (so it can refuse)   */
    /* and only then fills and watches the edit box -- that order is why the  */
    /* base call is the helper's, not this override's.                        */
    /*                                                                        */
    /* An override because the framework requires it: a TreeView hears that   */
    /* a label edit is starting first only through this virtual method.       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     evEdit : the node; its CancelEdit stops the edit.                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the edit box is filled and watched, or the edit cancelled.  */
    /*------------------------------------------------------------------------*/
    protected override void OnBeforeLabelEdit(NodeLabelEditEventArgs evEdit)
    {
        /* The editor's say, then the edit box, are in the helper. */
        Vtv_OnBeforeLabelEdit(evEdit);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_OnBeforeLabelEdit:                                                 */
    /*                                                                        */
    /* An in-place edit is starting.  The event goes to the editor first      */
    /* (which refuses edits it did not start).  If it goes ahead and was      */
    /* prepared by VTV_PrepareValueEdit, the control's edit box -- which      */
    /* exists by now but is not yet placed or shown -- gets the full value,   */
    /* all of it selected, and is watched so that wherever the control puts   */
    /* it (now, and again as it widens with typing) it sits after the key     */
    /* part of the label.                                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     evArgs : the node; evArgs.CancelEdit stops the edit.               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the edit box is filled and watched, or the edit cancelled.  */
    /*------------------------------------------------------------------------*/
    void Vtv_OnBeforeLabelEdit(NodeLabelEditEventArgs evArgs)
    {
        IntPtr hEdit;                                    // the control's edit box

        /* The base class first: it raises the BeforeLabelEdit event, so       */
        /* the editor (JsonEditForm.cs Jsn_OnTreeBeforeLabelEdit) can refuse   */
        /* an edit it did not start before anything is set up here.            */
        base.OnBeforeLabelEdit(evArgs);
        if (evArgs.CancelEdit || Vtv_sEditValue == null)
        {   /* Refused, or not one of ours: nothing to set up. */
            return;
        }

        /* The control's own edit box, asked of the control. */
        hEdit = Vtv_SendMessage(Handle, VTV_TVM_GETEDITCONTROL, IntPtr.Zero, IntPtr.Zero);
        if (hEdit == IntPtr.Zero)
        {   /* No box (should not happen): the edit goes on as the control does it. */
            return;
        }

        /* The full value, all selected, as the control would select its text. */
        Vtv_SendMessageText(hEdit, VTV_WM_SETTEXT, IntPtr.Zero, Vtv_sEditValue);
        Vtv_SendMessage(hEdit, VTV_EM_SETSEL, IntPtr.Zero, new IntPtr(-1));

        if (Vtv_iEditShift > 0)
        {   /* A key part to keep in view: move the box after it, every time it is placed. */
            Vtv_objEditShifter = VTS_clsEditShifter.VTS_Create(hEdit, Vtv_iEditShift);

            /* The control leaves an edited item's label blank: the key   */
            /* part is drawn at its post-paint (Vtv_DrawEditKey).         */
            Vtv_nodEdit = evArgs.Node;
            Invalidate(evArgs.Node.Bounds);
        }
    }

    /*------------------------------------------------------------------------*/
    /* OnAfterLabelEdit:                                                      */
    /*                                                                        */
    /* The Framework's edit-ended hook: hands it to Vtv_OnAfterLabelEdit      */
    /* below, which tells the editor while the edit box and its text still    */
    /* exist and then forgets the edit's preparation.  The editor is told     */
    /* once, by the helper.                                                   */
    /*                                                                        */
    /* An override because the framework requires it: a TreeView hears that   */
    /* a label edit has ended first only through this virtual method.         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     evEdit : the node and the text typed (null if cancelled).          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the edit is over.                                           */
    /*------------------------------------------------------------------------*/
    protected override void OnAfterLabelEdit(NodeLabelEditEventArgs evEdit)
    {
        /* The editor's notice and the clean-up are in the helper. */
        Vtv_OnAfterLabelEdit(evEdit);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_OnAfterLabelEdit:                                                  */
    /*                                                                        */
    /* An in-place edit has ended: the editor handles the event (it takes     */
    /* the text and posts the commit, which relabels the node once this       */
    /* event is over), then the edit's preparation is forgotten.              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     evArgs : the node and the text typed (null if cancelled).          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the edit is over.                                           */
    /*------------------------------------------------------------------------*/
    void Vtv_OnAfterLabelEdit(NodeLabelEditEventArgs evArgs)
    {
        /* The editor's handler first (the base class raises the event),   */
        /* while the box and its text still exist; then the prepared       */
        /* value, the shift and the box's watcher dropped.                 */
        base.OnAfterLabelEdit(evArgs);
        VTV_EndValueEdit();
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_SendMessage:                                                       */
    /*                                                                        */
    /* user32.dll SendMessageW with a pointer-sized lParam: sends a message   */
    /* and waits for it to be handled.  SendMessageW is imported a second     */
    /* time, for text, as Vtv_SendMessageText, since no name may be           */
    /* overloaded.                                                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     hWnd   : the window.                                               */
    /*     iMsg   : the message.                                              */
    /*     pWindowParam : its first parameter.                                */
    /*     pLongParam : its second parameter, a pointer-sized value.          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     IntPtr : the message's result.                                     */
    /*------------------------------------------------------------------------*/
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    static extern IntPtr Vtv_SendMessage(IntPtr hWnd, int iMsg, IntPtr pWindowParam,
        IntPtr pLongParam);

    /*------------------------------------------------------------------------*/
    /* Vtv_SendMessageText:                                                   */
    /*                                                                        */
    /* user32.dll SendMessageW again, with a text lParam passed as UTF-16     */
    /* (CharSet.Unicode, as the W form expects): for WM_SETTEXT on the edit   */
    /* box.                                                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     hWnd   : the window.                                               */
    /*     iMsg   : the message.                                              */
    /*     pWindowParam : its first parameter.                                */
    /*     sTextParam : its second parameter, the text.                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     IntPtr : the message's result.                                     */
    /*------------------------------------------------------------------------*/
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    static extern IntPtr Vtv_SendMessageText(IntPtr hWnd, int iMsg, IntPtr pWindowParam,
        string sTextParam);

    /*------------------------------------------------------------------------*/
    /* Vtv_PostPaintItem:                                                     */
    /*                                                                        */
    /* Called at an item's post-paint stage, after the control has drawn it,  */
    /* to finish what the control cannot draw itself: the key part beside an  */
    /* item being edited in place, or an item's coloured runs -- its key and  */
    /* value in their roles' colours (View > Color Keys) and its grey tail.   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     hDeviceContext : the device context the control is painting with.  */
    /*     hItem          : the item's handle (HTREEITEM).                    */
    /*     rcRow          : the row the item occupies.                        */
    /*     iItemState     : the item's CDIS_ state bits.                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the key, value and tail are coloured, or the edited item's  */
    /*            key part drawn.                                             */
    /*------------------------------------------------------------------------*/
    void Vtv_PostPaintItem(IntPtr hDeviceContext, IntPtr hItem, Rectangle rcRow, int iItemState)
    {
        TreeNode nodTree;       // the item's node
        VTT_clsTreeTag objTag;  // its tag
        Color clrBack;          // the label's background
        Color clrMain;          // the label's own colour (what the control drew it in)
        Color clrValue;         // the value run's colour, by its role
        bool bKey;              // the key is to be coloured
        bool bValue;            // the value is to be coloured
        bool bTail;             // there is a grey tail
        int iKeyFrom;           // the key run's start, or -1
        int iValueFrom;         // the value run's start, or -1
        int iTailFrom;          // the tail's start, or -1

        if (rcRow.Height <= 0
            || ((iItemState & VTV_CDIS_SELECTED) != 0 && Vtv_nodEdit == null))
        {   /* Nothing to draw, or the selected item with no edit open (selection colours throughout). */
            return;
        }

        /* The node: the item on the painted row -- a hit test at the      */
        /* row's middle, one pixel in -- if it is the one the control      */
        /* named (its handle checked), since the notification gives only   */
        /* the handle.                                                     */
        nodTree = GetNodeAt(Math.Max(0, rcRow.Left) + 1, rcRow.Top + rcRow.Height / 2);
        if (nodTree == null || nodTree.Handle != hItem)
        {   /* Not found (should not happen): leave the label as drawn. */
            return;
        }

        /* The background the label sits on: the node's own, else the   */
        /* tree's.                                                      */
        clrBack = nodTree.BackColor;
        if (clrBack.IsEmpty)
        {   /* No background of its own: the tree's. */
            clrBack = BackColor;
        }

        if (nodTree == Vtv_nodEdit)
        {   /* Being edited in place: the control leaves an edited        */
            /* item's label blank (just the selection colour), so its     */
            /* key part is drawn here beside the box (Vtv_DrawEditKey).   */
            Vtv_DrawEditKey(hDeviceContext, nodTree, clrBack);
            return;
        }

        if ((iItemState & VTV_CDIS_SELECTED) != 0)
        {   /* Selected: the control's selection colours throughout,   */
            /* which read the same for the whole label, as anywhere    */
            /* else in Windows.                                        */
            return;
        }

        /* The node's tag, which says where the runs are. */
        objTag = nodTree.Tag as VTT_clsTreeTag;
        if (objTag == null)
        {   /* A placeholder (no Tag): not a real node. */
            return;
        }

        /* What needs repainting: the key in its colour (option on, and   */
        /* the label in the tree's own colour, so not a repeated key's    */
        /* red), and the grey tail.                                       */
        clrMain = Vtv_NodeTextColor(nodTree);
        bKey = Vtv_bColorKeys && nodTree.ForeColor.IsEmpty && objTag.VTT_iKeyFrom >= 0
            && objTag.VTT_iKeyLength > 0
            && objTag.VTT_iKeyFrom + objTag.VTT_iKeyLength <= nodTree.Text.Length;
        bTail = objTag.VTT_iMutedFrom > 0 && objTag.VTT_iMutedFrom < nodTree.Text.Length;

        /* The value in its type's colour: the same conditions as the      */
        /* key, a value shown in the label, and a colour that differs      */
        /* from what the control already drew (with the defaults, black    */
        /* on black, there is nothing to repaint).  Colours compare by     */
        /* their RGB values: SystemColors.WindowText and Color.Black are   */
        /* different Color objects for the same black.                     */
        clrValue = clrMain;
        bValue = false;
        if (Vtv_bColorKeys && nodTree.ForeColor.IsEmpty
            && objTag.VTT_iValueRole >= 0 && objTag.VTT_iValueRole < Vtv_arrValueColors.Length
            && objTag.VTT_iValueLength > 0
            && objTag.VTT_iValueFrom + objTag.VTT_iValueLength <= nodTree.Text.Length)
        {   /* A value shown, coloured keys on: its role's colour. */
            clrValue = Vtv_arrValueColors[objTag.VTT_iValueRole];
            bValue = clrValue.ToArgb() != clrMain.ToArgb();
        }

        if (!bKey && !bValue && !bTail)
        {   /* None: the control's drawing is complete. */
            return;
        }

        /* The label again, in runs of colour, at Windows' own character   */
        /* positions (Vtv_DrawLabelRuns); -1 for a run not repainted.      */
        iKeyFrom = -1;
        if (bKey)
        {   /* The key, in the key colour. */
            iKeyFrom = objTag.VTT_iKeyFrom;
        }

        iValueFrom = -1;
        if (bValue)
        {   /* The value, in its role's colour. */
            iValueFrom = objTag.VTT_iValueFrom;
        }

        iTailFrom = -1;
        if (bTail)
        {   /* The tail, grey. */
            iTailFrom = objTag.VTT_iMutedFrom;
        }

        Vtv_DrawLabelRuns(hDeviceContext, nodTree, clrBack, clrMain, iKeyFrom,
            objTag.VTT_iKeyLength, iValueFrom, objTag.VTT_iValueLength, clrValue,
            iTailFrom);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_NodeTextColor:                                                     */
    /*                                                                        */
    /* A node's text colour: its own (red for a repeated key, grey for a      */
    /* comment entry), or the tree's.                                         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     nodTree : the node.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Color : the colour its label is drawn in.                          */
    /*------------------------------------------------------------------------*/
    Color Vtv_NodeTextColor(TreeNode nodTree)
    {
        if (nodTree.ForeColor.IsEmpty)
        {   /* No colour of its own: the tree's. */
            return(ForeColor);
        }

        /* Its own. */
        return(nodTree.ForeColor);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_DrawLabelRuns:                                                     */
    /*                                                                        */
    /* Draws a label afresh in runs of colour -- main colour, the key in its  */
    /* name role's colour (Vtv_NameColor), the value in its type's colour,    */
    /* the tail grey -- over what the control drew, every character exactly   */
    /* where the control put it, because both the positions and the drawing   */
    /* are Windows' own (GDI, not WinForms' TextRenderer).  The device        */
    /* context is left as the control had it.                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     hDeviceContext : the device context the control is painting with.  */
    /*     nodTree        : the node.                                         */
    /*     clrBack        : the background.                                   */
    /*     clrMain        : the colour of everything not in a run below.      */
    /*     iKeyFrom       : first character of the key run, or -1 for none.   */
    /*     iKeyLength     : the key run's length.                             */
    /*     iValueFrom     : first character of the value run, or -1 for none. */
    /*     iValueLength   : the value run's length.                           */
    /*     clrValue       : the value run's colour (its type's role colour).  */
    /*     iTailFrom      : first character of the grey tail, or -1 for none. */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the label is drawn.                                         */
    /*------------------------------------------------------------------------*/
    void Vtv_DrawLabelRuns(IntPtr hDeviceContext, TreeNode nodTree, Color clrBack,
        Color clrMain, int iKeyFrom, int iKeyLength, int iValueFrom,
        int iValueLength, Color clrValue, int iTailFrom)
    {
        string sText;       // the label
        Rectangle rcLabel;  // its rectangle
        Vrc_stRect stFill;  // the same, for ExtTextOut
        int[] arrRunning;   // running width after each character
        Vsz_stSize stSize;  // the whole text's size
        IntPtr hFont;       // the font, as a GDI font
        IntPtr hOldFont;    // the font it replaced
        int iOldTextColor;  // the text colour it replaced
        int iOldBackColor;  // the background colour it replaced
        int iOldBackMode;   // the background mode it replaced
        int iMargin;        // the text's margin in the label
        int y;              // the text's top
        int iRunFrom;       // first character of the run being drawn
        Color clrRun;       // its colour
        Color clrChar;      // one character's colour
        Color clrKey;       // the name's colour, by its role
        int i;

        /* The label's text and its rectangle, as the control has them. */
        sText = nodTree.Text;
        rcLabel = nodTree.Bounds;
        if (sText.Length == 0 || rcLabel.Width <= 0)
        {   /* Nothing to draw. */
            return;
        }

        /* The font the control itself draws with (not one made from the   */
        /* Font object: that can come out a different size).               */
        hFont = Vtv_ControlFont();
        hOldFont = Vtv_SelectObject(hDeviceContext, hFont);
        try
        {   /* The drawing; the finally puts the control's font back       */
            /* whatever happens.                                           */
            /* Windows' own layout of the text: GetTextExtentExPoint       */
            /* gives the running width after every character -- the        */
            /* measurement the control lays its text out by.  WinForms'    */
            /* TextRenderer measures some fonts and sizes a few pixels     */
            /* differently, enough to land off the control's characters    */
            /* and clip the brackets and colon next to a key (seen at 10   */
            /* point).  The text starts half of the rectangle's spare      */
            /* width in (the control pads both sides equally) and is       */
            /* centred vertically.                                         */
            arrRunning = new int[sText.Length];
            Vtv_GetTextExtentExPoint(hDeviceContext, sText, sText.Length, 0, IntPtr.Zero,
                arrRunning, out stSize);
            iMargin = Math.Max(0, (rcLabel.Width - stSize.VSZ_iWidth) / 2);
            y = rcLabel.Top + (rcLabel.Height - stSize.VSZ_iHeight) / 2;

            /* Clear the whole label to the background (ExtTextOut with   */
            /* ETO_OPAQUE and no text fills the rectangle), the old       */
            /* background colour kept to put back.                        */
            stFill.VRC_iLeft = rcLabel.Left;
            stFill.VRC_iTop = rcLabel.Top;
            stFill.VRC_iRight = rcLabel.Right;
            stFill.VRC_iBottom = rcLabel.Bottom;
            iOldBackColor = Vtv_SetBkColor(hDeviceContext, ColorTranslator.ToWin32(clrBack));
            Vtv_ExtTextOutFill(hDeviceContext, 0, 0, VTV_ETO_OPAQUE, ref stFill, "", 0, IntPtr.Zero);

            /* Each run of one colour, at its first character's place,   */
            /* with a transparent background so runs never clip each     */
            /* other; the old mode and colour kept to put back.  The     */
            /* first run's colour is the first character's.              */
            iOldBackMode = Vtv_SetBkMode(hDeviceContext, VTV_TRANSPARENT);
            iOldTextColor = Vtv_SetTextColor(hDeviceContext, ColorTranslator.ToWin32(clrMain));
            iRunFrom = 0;
            clrKey = Vtv_NameColor(nodTree);
            clrRun = Vtv_RunColor(0, clrMain, clrKey, iKeyFrom, iKeyLength,
                clrValue, iValueFrom, iValueLength, iTailFrom);
            for (i = 1; i <= sText.Length; i++)
            {   /* One character per iteration, and one step past the end   */
                /* (i == length) to draw the last run.                      */
                clrChar = clrRun;
                if (i < sText.Length)
                {   /* A character: its colour. */
                    clrChar = Vtv_RunColor(i, clrMain, clrKey, iKeyFrom,
                        iKeyLength, clrValue, iValueFrom, iValueLength, iTailFrom);
                }

                if (i == sText.Length || clrChar != clrRun)
                {   /* The run ends here: draw it. */
                    Vtv_SetTextColor(hDeviceContext, ColorTranslator.ToWin32(clrRun));
                    Vtv_ExtTextOut(hDeviceContext, rcLabel.Left + iMargin + Vtv_EdgeAt(arrRunning, iRunFrom),
                        y, 0, IntPtr.Zero, sText.Substring(iRunFrom, i - iRunFrom),
                        i - iRunFrom, IntPtr.Zero);
                    iRunFrom = i;
                    clrRun = clrChar;
                }
            }

            /* The device context as the control left it. */
            Vtv_SetTextColor(hDeviceContext, iOldTextColor);
            Vtv_SetBkMode(hDeviceContext, iOldBackMode);
            Vtv_SetBkColor(hDeviceContext, iOldBackColor);
        }
        finally
        {   /* Always: the control's font is the control's -- put back,   */
            /* never freed.                                               */
            Vtv_SelectObject(hDeviceContext, hOldFont);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_ControlFont:                                                       */
    /*                                                                        */
    /* The GDI font the control draws its labels with, asked of the control   */
    /* itself (WM_GETFONT).  Measuring with exactly this font is what makes   */
    /* the character positions match the control's; a font made from the      */
    /* WinForms Font object (Font.ToHfont) measured 7 pixels narrower on      */
    /* "name: " at 8.25 point.  The handle belongs to the control and must    */
    /* not be freed.                                                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     IntPtr : the font handle.                                          */
    /*------------------------------------------------------------------------*/
    IntPtr Vtv_ControlFont()
    {
        /* The control's answer. */
        return(Vtv_SendMessage(Handle, VTV_WM_GETFONT, IntPtr.Zero, IntPtr.Zero));
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_NameColor:                                                         */
    /*                                                                        */
    /* The colour of a node's name: a top-level                               */
    /* container's, a nested container's, or a plain entry's, from            */
    /* Vtv_arrNameColors.                                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     nodTree : the node.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Color : its name's colour.                                         */
    /*------------------------------------------------------------------------*/
    Color Vtv_NameColor(TreeNode nodTree)
    {
        VoorheesNode objNode;                               // the node's value

        /* The value the node stands for, found by its path (null with   */
        /* no document).                                                 */
        objNode = null;
        if (Vtv_doc != null)
        {   /* A document: the node's value. */
            objNode = Vtv_doc.Vd_NodeAt(VTV_PathOf(nodTree));
        }

        if (objNode != null && objNode.Vn_IsContainer() && nodTree.Parent == null)
        {   /* A container on the tree's top level (an INI section, a      */
            /* registry key, JSON's "root"): the Top colour.  JSON's own   */
            /* top-level keys -- the root's children -- are one level      */
            /* down, so they take the nested or entry colour below, as     */
            /* the Colors dialog's "Top level" row says.                   */
            return(Vtv_arrNameColors[VTV_NAME_TOP]);
        }

        if (objNode != null && objNode.Vn_IsContainer())
        {   /* A container inside another. */
            return(Vtv_arrNameColors[VTV_NAME_NESTED]);
        }

        /* A plain entry. */
        return(Vtv_arrNameColors[VTV_NAME_ENTRY]);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_RunColor:                                                          */
    /*                                                                        */
    /* The colour one character of a label is drawn in (Vtv_DrawLabelRuns):   */
    /* grey from the tail on, the name's colour inside the key (its role's:   */
    /* Vtv_NameColor), the value's colour inside the value (its type's role), */
    /* else the main colour.  The runs never overlap (the label is name,      */
    /* separator, value, then the tail), so the order of the tests only       */
    /* matters for a run that was cut short; the tail is tested first, as it  */
    /* always ends the label.                                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     i            : the character.                                      */
    /*     clrMain    : the main colour.                                      */
    /*     clrKey     : the name's colour.                                    */
    /*     iKeyFrom     : first character of the key, or -1 for none.         */
    /*     iKeyLength   : the key's length.                                   */
    /*     clrValue   : the value's colour.                                   */
    /*     iValueFrom   : first character of the value, or -1 for none.       */
    /*     iValueLength : the value's length.                                 */
    /*     iTailFrom    : first character of the tail, or -1 for none.        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Color : its colour.                                                */
    /*------------------------------------------------------------------------*/
    static Color Vtv_RunColor(int i, Color clrMain, Color clrKey, int iKeyFrom,
        int iKeyLength, Color clrValue, int iValueFrom, int iValueLength,
        int iTailFrom)
    {
        if (iTailFrom >= 0 && i >= iTailFrom)
        {   /* In the tail. */
            return(VTV_COLORMUTED);
        }

        if (iKeyFrom >= 0 && i >= iKeyFrom && i < iKeyFrom + iKeyLength)
        {   /* In the key: its role's colour. */
            return(clrKey);
        }

        if (iValueFrom >= 0 && i >= iValueFrom && i < iValueFrom + iValueLength)
        {   /* In the value: its type's colour. */
            return(clrValue);
        }

        /* Anywhere else. */
        return(clrMain);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_EdgeAt:                                                            */
    /*                                                                        */
    /* Where a character starts, from GetTextExtentExPoint's running widths:  */
    /* character i starts where the text before it ends.                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     arrRunning : running width after each character.                   */
    /*     i          : the character.                                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : its left edge, pixels from the text's start.                 */
    /*------------------------------------------------------------------------*/
    static int Vtv_EdgeAt(int[] arrRunning, int i)
    {
        if (i <= 0)
        {   /* The first character starts the text. */
            return(0);
        }

        /* The width of everything before it. */
        return(arrRunning[i - 1]);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_TextWidth:                                                         */
    /*                                                                        */
    /* A text's width as the control lays its labels out (GDI, the control's  */
    /* own font -- Vtv_ControlFont -- on one of its device contexts), for use */
    /* outside painting.                                                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText : the text.                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the width, pixels.                                           */
    /*------------------------------------------------------------------------*/
    int Vtv_TextWidth(string sText)
    {
        IntPtr hDeviceContext;  // a device context of the control's
        IntPtr hOldFont;        // the font it replaced
        int[] arrRunning;       // running widths (needed by the call)
        Vsz_stSize stSize;      // the text's size
        Graphics gfx;           // measured control surface, released by the using block

        /* Zero until measured. */
        stSize.VSZ_iWidth = 0;
        stSize.VSZ_iHeight = 0;
        using (gfx = CreateGraphics())
        {   /* A device context of the control's, released when the block   */
            /* ends: its GDI handle borrowed, the control's own font put    */
            /* in, the text measured, the old font put back and the         */
            /* handle returned -- in that order.  The running widths are    */
            /* not used but the call needs room for them (one at least).    */
            hDeviceContext = gfx.GetHdc();
            hOldFont = Vtv_SelectObject(hDeviceContext, Vtv_ControlFont());
            arrRunning = new int[Math.Max(1, sText.Length)];
            Vtv_GetTextExtentExPoint(hDeviceContext, sText, sText.Length, 0, IntPtr.Zero,
                arrRunning, out stSize);
            Vtv_SelectObject(hDeviceContext, hOldFont);
            gfx.ReleaseHdc(hDeviceContext);
        }

        /* Its width. */
        return(stSize.VSZ_iWidth);
    }

    /*-----------------------------------------------------------------------------*/
    /* Vtv_DrawEditKey:                                                            */
    /*                                                                             */
    /* Draws the key part of the item being edited in place, where the             */
    /* control drew only the selection colour, so the row reads "key: [value       */
    /* being typed]" (the label holds only the key part while the box is           */
    /* open: VTV_PrepareValueEdit).                                                */
    /*                                                                             */
    /* Arguments:                                                                  */
    /*     hDeviceContext       : the device context the control is painting with. */
    /*     nodTree  : the node being edited.                                       */
    /*     clrBack : its background.                                               */
    /*                                                                             */
    /* Returns:                                                                    */
    /*     void : the key part is drawn.                                           */
    /*-----------------------------------------------------------------------------*/
    void Vtv_DrawEditKey(IntPtr hDeviceContext, TreeNode nodTree, Color clrBack)
    {
        VTT_clsTreeTag objTag;  // the node's tag
        int iKeyFrom;           // the key run, or -1
        int iKeyLength;         // its length

        /* No key run to colour unless View > Color Keys is on and the   */
        /* label is in the tree's own colour (a repeated key stays red   */
        /* throughout).                                                  */
        objTag = nodTree.Tag as VTT_clsTreeTag;
        iKeyFrom = -1;
        iKeyLength = 0;
        if (Vtv_bColorKeys && nodTree.ForeColor.IsEmpty && objTag != null
            && objTag.VTT_iKeyFrom >= 0 && objTag.VTT_iKeyLength > 0
            && objTag.VTT_iKeyFrom + objTag.VTT_iKeyLength <= nodTree.Text.Length)
        {   /* Coloured keys: the key itself in the key colour. */
            iKeyFrom = objTag.VTT_iKeyFrom;
            iKeyLength = objTag.VTT_iKeyLength;
        }

        /* The key part on the plain background -- not the selection        */
        /* colour the control left -- at the control's own text margin,     */
        /* in the node's own colour, as any coloured label (no value run:   */
        /* the value is in the box, not the label).                         */
        Vtv_DrawLabelRuns(hDeviceContext, nodTree, clrBack, Vtv_NodeTextColor(nodTree),
            iKeyFrom, iKeyLength, -1, 0, Vtv_NodeTextColor(nodTree), -1);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_SelectObject:                                                      */
    /*                                                                        */
    /* gdi32.dll SelectObject: puts a font into a device context.             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     hDeviceContext  : the device context.                              */
    /*     hObject : the font.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     IntPtr : the font it replaced, to put back.                        */
    /*------------------------------------------------------------------------*/
    [DllImport("gdi32.dll", EntryPoint = "SelectObject")]
    static extern IntPtr Vtv_SelectObject(IntPtr hDeviceContext, IntPtr hObject);

    /*------------------------------------------------------------------------*/
    /* Vtv_GetTextExtentExPoint:                                              */
    /*                                                                        */
    /* gdi32.dll GetTextExtentExPointW: a text's size in the device context's */
    /* font, and the running width after each character.                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     hDeviceContext        : the device context.                        */
    /*     sText      : the text.                                             */
    /*     iChars  : its length.                                              */
    /*     iMaxExtent : a width limit (unused here: 0, with pFit null).       */
    /*     pFit     : null.                                                   */
    /*     aiRunning      : receives the running widths (one per character).  */
    /*     stSize       : receives the whole text's size.                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true on success (not checked here).                         */
    /*------------------------------------------------------------------------*/
    [DllImport("gdi32.dll", EntryPoint = "GetTextExtentExPointW", CharSet = CharSet.Unicode)]
    static extern bool Vtv_GetTextExtentExPoint(IntPtr hDeviceContext, string sText, int iChars,
        int iMaxExtent, IntPtr pFit, [Out] int[] aiRunning, out Vsz_stSize stSize);

    /*------------------------------------------------------------------------*/
    /* Vtv_SetTextColor:                                                      */
    /*                                                                        */
    /* gdi32.dll SetTextColor: the colour text is drawn in.                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     hDeviceContext     : the device context.                           */
    /*     iColorRef : the colour, as a COLORREF (ColorTranslator.ToWin32).   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the previous colour, to put back.                            */
    /*------------------------------------------------------------------------*/
    [DllImport("gdi32.dll", EntryPoint = "SetTextColor")]
    static extern int Vtv_SetTextColor(IntPtr hDeviceContext, int iColorRef);

    /*------------------------------------------------------------------------*/
    /* Vtv_SetBkColor:                                                        */
    /*                                                                        */
    /* gdi32.dll SetBkColor: the background colour an opaque fill uses.       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     hDeviceContext     : the device context.                           */
    /*     iColorRef : the colour, as a COLORREF.                             */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the previous colour, to put back.                            */
    /*------------------------------------------------------------------------*/
    [DllImport("gdi32.dll", EntryPoint = "SetBkColor")]
    static extern int Vtv_SetBkColor(IntPtr hDeviceContext, int iColorRef);

    /*------------------------------------------------------------------------*/
    /* Vtv_SetBkMode:                                                         */
    /*                                                                        */
    /* gdi32.dll SetBkMode: whether text fills its background                 */
    /* (VTV_TRANSPARENT: it does not).                                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     hDeviceContext   : the device context.                             */
    /*     iMode : the mode.                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the previous mode, to put back.                              */
    /*------------------------------------------------------------------------*/
    [DllImport("gdi32.dll", EntryPoint = "SetBkMode")]
    static extern int Vtv_SetBkMode(IntPtr hDeviceContext, int iMode);

    /*------------------------------------------------------------------------*/
    /* Vtv_ExtTextOutFill:                                                    */
    /*                                                                        */
    /* gdi32.dll ExtTextOutW with a rectangle: with VTV_ETO_OPAQUE and no     */
    /* text, fills the rectangle with the background colour.  ExtTextOutW is  */
    /* imported again without one as Vtv_ExtTextOut, since no name may be     */
    /* overloaded.                                                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     hDeviceContext       : the device context.                         */
    /*     x         : where text would start, across (unused for a fill).    */
    /*     y         : where text would start, down (unused for a fill).      */
    /*     uiOptions : ETO_ flags.                                            */
    /*     stRectangle      : the rectangle.                                  */
    /*     sText     : the text ("" for a fill).                              */
    /*     iChars   : its length.                                             */
    /*     pSpacing      : character spacing (null: the font's own).          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true on success (not checked here).                         */
    /*------------------------------------------------------------------------*/
    [DllImport("gdi32.dll", EntryPoint = "ExtTextOutW", CharSet = CharSet.Unicode)]
    static extern bool Vtv_ExtTextOutFill(IntPtr hDeviceContext, int x, int y, uint uiOptions,
        ref Vrc_stRect stRectangle, string sText, int iChars, IntPtr pSpacing);

    /*------------------------------------------------------------------------*/
    /* Vtv_ExtTextOut:                                                        */
    /*                                                                        */
    /* gdi32.dll ExtTextOutW with no rectangle: draws text at a point, in     */
    /* the device context's font, colour and background mode -- as the        */
    /* control draws its labels.                                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     hDeviceContext       : the device context.                         */
    /*     x         : where the text starts, across.                         */
    /*     y         : where the text starts, down (its top).                 */
    /*     uiOptions : ETO_ flags (0).                                        */
    /*     pRectangle      : null.                                            */
    /*     sText     : the text.                                              */
    /*     iChars   : its length.                                             */
    /*     pSpacing      : character spacing (null: the font's own).          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true on success (not checked here).                         */
    /*------------------------------------------------------------------------*/
    [DllImport("gdi32.dll", EntryPoint = "ExtTextOutW", CharSet = CharSet.Unicode)]
    static extern bool Vtv_ExtTextOut(IntPtr hDeviceContext, int x, int y, uint uiOptions,
        IntPtr pRectangle, string sText, int iChars, IntPtr pSpacing);

    /*------------------------------------------------------------------------*/
    /* WndProc:                                                               */
    /*                                                                        */
    /* The control's window procedure: hands the SAME message, by reference,  */
    /* to Vtv_OnWindowMessage below, which takes the custom-draw and          */
    /* insert / delete messages it needs and passes everything else to the    */
    /* base procedure itself.  A copy, or a base call here, would lose its    */
    /* Result or handle a message twice.                                      */
    /*                                                                        */
    /* An override because the framework requires it: the custom-draw and     */
    /* item messages reach a control's own code only through its window       */
    /* procedure, which a WinForms control exposes as this virtual method.    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     stMessage : the message, by reference.                             */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the message is handled.                                     */
    /*------------------------------------------------------------------------*/
    protected override void WndProc(ref Message stMessage)
    {
        /* The message's handling, base included, is in the helper. */
        Vtv_OnWindowMessage(ref stMessage);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_OnWindowMessage:                                                   */
    /*                                                                        */
    /* The control's window procedure, for the two things WinForms cannot do  */
    /* here: the post-paint stage of each item's custom draw, at which        */
    /* Vtv_PostPaintItem colours the label's runs (or draws an edited item's  */
    /* key part), and counting the control's own inserts and deletes for a    */
    /* progress bar.  Every other message goes on to the normal handling.     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     stMessage : the message.                                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the message is handled.                                     */
    /*------------------------------------------------------------------------*/
    void Vtv_OnWindowMessage(ref Message stMessage)
    {
        int iCode;                // a notification's code, from its NMHDR
        Vcd_stCustomDraw stDraw;  // a custom draw notification's contents

        if (stMessage.Msg == VTV_WM_REFLECT_NOTIFY)
        {   /* A notification: custom draw is handled here.                  */
            /* NMHDR is { HWND hwndFrom; UINT_PTR idFrom; UINT code }: the   */
            /* code follows two pointer-sized fields.                        */
            iCode = Marshal.ReadInt32(stMessage.LParam, IntPtr.Size * 2);
            if (iCode == VTV_NM_CUSTOMDRAW)
            {   /* Custom draw: see which stage. */
                stDraw = (Vcd_stCustomDraw)Marshal.PtrToStructure(stMessage.LParam,
                    typeof(Vcd_stCustomDraw));
                if (stDraw.VCD_iDrawStage == VTV_CDDS_ITEMPOSTPAINT)
                {   /* An item has been painted: its coloured runs or grey      */
                    /* tail drawn over it (or an edited item's key part),       */
                    /* and answered here -- WinForms asked for no post-paint,   */
                    /* so it has nothing to do at this stage (as found).        */
                    Vtv_PostPaintItem(stDraw.VCD_hDeviceContext, stDraw.VCD_hItem,
                        Rectangle.FromLTRB(stDraw.VCD_iLeft, stDraw.VCD_iTop,
                        stDraw.VCD_iRight, stDraw.VCD_iBottom), stDraw.VCD_iItemState);
                    stMessage.Result = IntPtr.Zero;
                    return;
                }

                /* Every other stage answered by WinForms as usual (it   */
                /* applies the nodes' colours at the pre-paints).        */
                base.WndProc(ref stMessage);
                if (stDraw.VCD_iDrawStage == VTV_CDDS_ITEMPREPAINT)
                {   /* An item is about to be painted: its answer also asks   */
                    /* for the post-paint stage, to hear when it has been.    */
                    stMessage.Result = (IntPtr)(stMessage.Result.ToInt64() | VTV_CDRF_NOTIFYPOSTPAINT);
                }
                return;
            }
        }

        if (Vtv_bCountDeletes && stMessage.Msg == VTV_WM_REFLECT_NOTIFY)
        {   /* A notification while removing: see whether it is a deletion.   */
            /* NMHDR is { HWND hwndFrom; UINT_PTR idFrom; UINT code }: the    */
            /* code follows two pointer-sized fields.                         */
            iCode = Marshal.ReadInt32(stMessage.LParam, IntPtr.Size * 2);
            if (iCode == VTV_TVN_DELETEITEMW || iCode == VTV_TVN_DELETEITEMA)
            {   /* One item gone: count it, and answer here without passing   */
                /* it on -- nothing else needs it (as found).                 */
                Vtv_CountUnit();
                stMessage.Result = IntPtr.Zero;
                return;
            }
        }

        if (Vtv_bCountDeletes
            && (stMessage.Msg == VTV_TVM_GETITEMW || stMessage.Msg == VTV_TVM_GETITEMA))
        {   /* WinForms reads each node once while it forgets it: count it. */
            Vtv_CountUnit();
        }

        if (Vtv_bCountInserts
            && (stMessage.Msg == VTV_TVM_INSERTITEMW || stMessage.Msg == VTV_TVM_INSERTITEMA))
        {   /* An item going in: count it. */
            Vtv_CountUnit();
        }

        /* Everything not answered above, counted or not: the normal   */
        /* handling.                                                   */
        base.WndProc(ref stMessage);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_CountUnit:                                                         */
    /*                                                                        */
    /* Adds one unit of progress and, every VTV_PROGRESSSTEP units, moves the */
    /* bar.  Only moves it: this runs inside the control's own call, where    */
    /* making the window APPEAR would send the tree paint and size messages   */
    /* half way through, so the window is put up before the call or not at    */
    /* all.                                                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the count advances and the bar may move.                    */
    /*------------------------------------------------------------------------*/
    void Vtv_CountUnit()
    {
        Vtv_lDone++;
        if (Vtv_frmProgress != null && Vtv_lDone % VTV_PROGRESSSTEP == 0)
        {   /* Time for a report. */
            Vtv_frmProgress.BMF_UpdateUiProgress(Vtv_lDone, Vtv_lTotal);
        }
    }
}

/*----------------------------------------------------------------------------*/
/* VTS_clsEditShifter                                                         */
/*                                                                            */
/* Watches the tree's in-place edit box and keeps it to the right of the      */
/* label's key part.  The tree control places the box over the label's text   */
/* when the edit starts, and again each time the text changes (the box        */
/* widens with what is typed), always from the label's left edge.  Each time, */
/* the move is caught before it happens (WM_WINDOWPOSCHANGING) and the box's  */
/* new left edge is moved right by the key part's width.  A move to where the */
/* box already is after shifting is not shifted twice.                        */
/*                                                                            */
/* Derives from NativeWindow because the framework requires it: catching      */
/* another window's messages (the tree's own edit box, which is no WinForms   */
/* control) is done only by subclassing it through a NativeWindow.  That is   */
/* the one kind of inheritance allowed here -- from a framework base class,   */
/* never from a class of ours.                                                */
/*----------------------------------------------------------------------------*/
internal class VTS_clsEditShifter : NativeWindow
{
    /*------------------------------------------------------------------------*/
    /* Vwp_stWindowPos                                                        */
    /*                                                                        */
    /* WINDOWPOS, as passed with WM_WINDOWPOSCHANGING; laid out sequentially  */
    /* as in C.                                                               */
    /*------------------------------------------------------------------------*/
    [StructLayout(LayoutKind.Sequential)]
    struct Vwp_stWindowPos
    {
        public IntPtr VWP_hWindow;       // the window being moved
        public IntPtr VWP_hInsertAfter;  // its new place in the z-order
        public int VWP_iX;               // new left edge, parent client coordinates
        public int VWP_iY;               // new top edge
        public int VWP_iWidth;           // new width
        public int VWP_iHeight;          // new height
        public uint VWP_uiFlags;         // SWP_ flags
    }

    /* The window message caught and the flag tested (winuser.h) --   */
    /* documentation table.                                           */
    const int VTS_WM_WINDOWPOSCHANGING = 0x0046;        // a window is about to be moved or sized; lParam is a WINDOWPOS
    const uint VTS_SWP_NOMOVE = 0x0002;                 // WINDOWPOS flag: the position is not changing

    /* The watcher's state -- documentation table. */
    int Vts_iShift;                                     // how far right to move the box, pixels
    int Vts_iLastX;                                     // the left edge it was last moved to (int.MinValue: none yet)

    /*------------------------------------------------------------------------*/
    /* VTS_Create:                                                            */
    /*                                                                        */
    /* Starts watching an edit box.                                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     hEdit  : the tree's edit box.                                      */
    /*     iShift : how far right it goes, pixels.                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VTS_clsEditShifter : the watcher, attached to the box until        */
    /*                               ReleaseHandle or the box is destroyed.   */
    /*------------------------------------------------------------------------*/
    public static VTS_clsEditShifter VTS_Create(IntPtr hEdit, int iShift)
    {
        VTS_clsEditShifter objShifter;                 // the new watcher

        /* The shift, no move made yet, and the box's messages routed   */
        /* through this watcher (AssignHandle subclasses the box).      */
        objShifter = new VTS_clsEditShifter();
        objShifter.Vts_iShift = iShift;
        objShifter.Vts_iLastX = int.MinValue;
        objShifter.AssignHandle(hEdit);

        /* Watching. */
        return(objShifter);
    }

    /*------------------------------------------------------------------------*/
    /* WndProc:                                                               */
    /*                                                                        */
    /* The edit box's window procedure, as this NativeWindow sees it: hands   */
    /* the SAME message, by reference, to Vts_OnWindowMessage below, which    */
    /* shifts a move's left edge in place and then passes the message on to   */
    /* the edit box itself -- so nothing is passed on twice.                  */
    /*                                                                        */
    /* An override because the framework requires it: a NativeWindow sees the */
    /* window's messages only through this virtual method.                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     stMessage : the message, by reference.                             */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the message is handled by the edit box.                     */
    /*------------------------------------------------------------------------*/
    protected override void WndProc(ref Message stMessage)
    {
        /* The shift and the pass-on are in the helper. */
        Vts_OnWindowMessage(ref stMessage);
    }

    /*------------------------------------------------------------------------*/
    /* Vts_OnWindowMessage:                                                   */
    /*                                                                        */
    /* The edit box's messages pass through here first.  A move the tree is   */
    /* about to make has its left edge shifted right; every message then      */
    /* goes on to the edit box as normal.                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     stMessage : the message.                                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the message is handled by the edit box.                     */
    /*------------------------------------------------------------------------*/
    void Vts_OnWindowMessage(ref Message stMessage)
    {
        Vwp_stWindowPos stPosition;                                // the move about to happen

        if (stMessage.Msg == VTS_WM_WINDOWPOSCHANGING && stMessage.LParam != IntPtr.Zero)
        {   /* A move or resize: see whether the position changes. */
            stPosition = (Vwp_stWindowPos)Marshal.PtrToStructure(stMessage.LParam, typeof(Vwp_stWindowPos));
            if ((stPosition.VWP_uiFlags & VTS_SWP_NOMOVE) == 0 && stPosition.VWP_iX != Vts_iLastX)
            {   /* Placed from the label's left edge: after the key part instead. */
                stPosition.VWP_iX += Vts_iShift;
                Vts_iLastX = stPosition.VWP_iX;
                Marshal.StructureToPtr(stPosition, stMessage.LParam, false);
            }
        }

        /* On to the edit box, the move (if any) already changed. */
        base.WndProc(ref stMessage);
    }
}
