/*----------------------------------------------------------------------------*/
/* PaneListView.cs                                                            */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The list control both panes use: an ordinary list view with these things   */
/* added -- it paints itself DOUBLE-BUFFERED, it says when the USER has       */
/* resized one of its columns, it lays its List and Small Icons views out on  */
/* an even grid sized to the widest name (PLV_ArrangeGrid says why), and, for */
/* item check boxes, it can hide one row's box and show a select-all box in   */
/* the first column's heading, reporting a click on it.                       */
/*                                                                            */
/* WHY DOUBLE-BUFFERED.  A plain list view paints straight to the screen: it  */
/* wipes the area and then draws the rows over it, one after another, and the */
/* eye sees the wiped state in between.  While a column divider is dragged    */
/* (or the window resized) that happens on every mouse move, and the whole    */
/* list flickers.  Double-buffered, the list draws each frame off screen and  */
/* puts the finished picture up in one step, so there is nothing in between   */
/* to see.  The switch for it, DoubleBuffered, is a PROTECTED property of a   */
/* control, so it can only be set from inside a class derived from it.        */
/*                                                                            */
/* WHY ITS OWN "USER RESIZED A COLUMN" REPORT.  A list view's own             */
/* ColumnWidthChanged event is raised for every change of a column's width,   */
/* the program's as well as the user's -- and some of the program's arrive    */
/* late, when the list's window is created -- so it cannot be used to tell    */
/* which widths the user chose.  The column headings are a control of their   */
/* own inside the list, and that control tells the list, in a WM_NOTIFY       */
/* message, when the user FINISHES DRAGGING a divider and when the user       */
/* DOUBLE-CLICKS one (which fits the column to its content).  Only the user   */
/* can cause those two, so they are what is reported here.                    */
/*                                                                            */
/* This file depends on nothing else in the program.                          */
/*----------------------------------------------------------------------------*/

/* The framework: the base types (window handles, Math, event arguments),   */
/* the table of cut names, GDI+'s bitmap, icon and brush for the filled     */
/* box, P/Invoke and the reading of the control's notification data, and    */
/* the list view this control is.                                           */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* PLV_ColumnHandler:                                                     */
    /*                                                                        */
    /* What PLV_PaneListView calls when the user has resized a column.        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstSource : the list whose column it was.                          */
    /*     iColumn   : the column's index.                                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the receiver has taken note of the column's new width.      */
    /*------------------------------------------------------------------------*/
    internal delegate void PLV_ColumnHandler(ListView lstSource, int iColumn);

    /*------------------------------------------------------------------------*/
    /* PLV_PaneListView -- the panes' list control (the file header           */
    /* describes it).                                                         */
    /*                                                                        */
    /* Derives from ListView because the framework requires it: the           */
    /* double-buffered painting, the window procedure and the handle-created  */
    /* hook this control needs reach only a ListView subclass.  That is the   */
    /* one kind of inheritance allowed here -- from a framework base class,   */
    /* never from a class of ours.                                            */
    /*------------------------------------------------------------------------*/
    internal sealed class PLV_PaneListView : ListView
    {
        /*--------------------------------------------------------------------*/
        /* Plv_FnIndex -- the shape of the posted calls that carry one index  */
        /* (a row or a column): Plv_UntickRow, Plv_ReportHeaderBoxClick and   */
        /* Plv_ReportUserResized, each posted with BeginInvoke and its index  */
        /* passed as the posted call's argument.                              */
        /*--------------------------------------------------------------------*/
        private delegate void Plv_FnIndex(int iIndex);

        /* The message and the notification codes watched for -- documentation   */
        /* table.  The heading control sends each code in an ANSI and a          */
        /* Unicode form, depending on how its window was created; both are       */
        /* accepted.                                                             */
        private const int PLV_WM_NOTIFY            = 0x004E;   // a child control telling its parent that something happened
        private const int PLV_HDN_ENDTRACK_A       = -307;     // the user let go of a column divider after dragging it (ANSI form)
        private const int PLV_HDN_ENDTRACK_W       = -327;     // the same (Unicode form)
        private const int PLV_HDN_DIVIDERDBLCLK_A  = -305;     // the user double-clicked a column divider (ANSI form)
        private const int PLV_HDN_DIVIDERDBLCLK_W  = -325;     // the same (Unicode form)
        private const int PLV_HDN_ITEMSTATEICONCLICK = -316;   // the user clicked a heading's check box (one form only; Vista and later)

        /* The heading's select-all check box -- documentation table.  The   */
        /* heading control draws a box in a heading whose format has         */
        /* HDF_CHECKBOX, once the control itself has the HDS_CHECKBOXES      */
        /* style.  A click on it is answered by the LIST control, which      */
        /* ticks or unticks every row itself (as found, when tried); the     */
        /* heading's own tick is set by the owner from the selection.        */
        private const int  PLV_LVM_GETHEADER       = 0x101F;   // LVM_GETHEADER: the list's heading control's window, or zero
        private const int  PLV_HDM_GETITEMW        = 0x120B;   // HDM_GETITEMW: read one heading's members (wParam its index, lParam an HDITEM)
        private const int  PLV_HDM_SETITEMW        = 0x120C;   // HDM_SETITEMW: write them
        private const uint PLV_HDI_FORMAT          = 0x0004;   // HDITEM mask: the fmt member is the one read or written
        private const int  PLV_HDF_CHECKBOX        = 0x0040;   // HDITEM fmt: the heading draws a check box
        private const int  PLV_HDF_CHECKED         = 0x0080;   // HDITEM fmt: that box is ticked
        private const int  PLV_HDS_CHECKBOXES      = 0x0400;   // heading control style: headings may carry check boxes
        private const int  PLV_GWL_STYLE           = -16;      // GetWindowLong / SetWindowLong: the window's style bits

        /* The list-view message the List view's column width is set by, and   */
        /* the grid's spacing -- documentation table.                          */
        private const int PLV_LVM_SETCOLUMNWIDTH   = 0x101E;   // LVM_SETCOLUMNWIDTH: wParam the column (0, the only one, in the List view), lParam the width in pixels
        private const int PLV_LVM_ARRANGE          = 0x1016;   // LVM_ARRANGE: re-flow the icon views' rows onto the control's own slots (wParam how); intercepted in Small Icons view, where the program lays the rows out
        private const int PLV_LVM_GETSTRINGWIDTHW  = 0x1057;   // LVM_GETSTRINGWIDTHW: how wide the control draws a string (lParam, Unicode) in its own font, pixels
        private const int PLV_LVM_GETITEMSPACING   = 0x1033;   // LVM_GETITEMSPACING: wParam 1 for the small views; the answer's low word the horizontal, high word the vertical spacing, pixels
        private const int PLV_LVM_SETITEMPOSITION32 = 0x1031;  // LVM_SETITEMPOSITION32: place one row (wParam its index) at a point (lParam a POINT), view coordinates
        private const int PLV_LVM_GETIMAGELIST     = 0x1002;   // LVM_GETIMAGELIST: the handle of one of the control's image lists (wParam which), or zero when it has none
        private const int PLV_LVSIL_STATE          = 2;        // LVM_GETIMAGELIST's wParam for the STATE image list -- the check boxes, when CheckBoxes is on

        /* The item check boxes' pictures -- documentation table.  The         */
        /* control's own state image list holds its two boxes, and a third,    */
        /* solid-filled box is appended to it for a row that can never be      */
        /* ticked (the ".." row; PLV_AddFilledBox).  A row's state image is    */
        /* the 1-based index in bits 12-15 of its state; 0 means no picture.   */
        private const int  PLV_STATE_UNCHECKED     = 0;        // StateImageIndex of an empty box (state image 1)
        private const int  PLV_STATE_CHECKED       = 1;        // StateImageIndex of a ticked box (state image 2)
        internal const int PLV_STATE_UNAVAILABLE   = 2;        // StateImageIndex of the solid-filled box: a row that cannot be ticked (state image 3)
        private const int  PLV_WM_REFLECT_NOTIFY   = 0x204E;   // WM_NOTIFY as the framework REFLECTS it back to the list: the list's own notifications arrive here
        private const int  PLV_WM_LBUTTONDBLCLK    = 0x0203;   // a left-button double-click in the list (lParam the point, client coordinates)
        private const int  PLV_LVN_ITEMCHANGING    = -100;     // a row's state is about to change; answering 1 refuses the change
        private const uint PLV_LVIF_STATE          = 0x0008;   // NMLISTVIEW.uChanged: the change is to the row's state bits
        private const uint PLV_LVIS_STATEIMAGEMASK = 0xF000;   // the state bits holding the state image index (bits 12-15)

        /*--------------------------------------------------------------------*/
        /* PLV_Point -- POINT, a row's place for LVM_SETITEMPOSITION32.       */
        /*--------------------------------------------------------------------*/
        [StructLayout(LayoutKind.Sequential)]
        private struct PLV_Point
        {
            public int iX;    // pixels from the view's left
            public int iY;    // pixels from the view's top
        }
        /*--------------------------------------------------------------------*/
        /* PLV_HdItem -- HDITEMW, as HDM_GETITEMW / HDM_SETITEMW read and     */
        /* write it.  Only fmt is used here (mask HDI_FORMAT); the rest are   */
        /* zero and are there so the structure is the size the control        */
        /* expects.                                                           */
        /*--------------------------------------------------------------------*/
        [StructLayout(LayoutKind.Sequential)]
        private struct PLV_HdItem
        {
            public uint   uiMask;         // which members are read or written: HDI_FORMAT
            public int    cxy;            // unused here
            public IntPtr pszText;        // unused here
            public IntPtr hbm;            // unused here
            public int    cchTextMax;     // unused here
            public int    iFmt;           // the heading's format bits (alignment, sort arrow, HDF_CHECKBOX, HDF_CHECKED ...)
            public IntPtr lParam;         // unused here
            public int    iImage;         // unused here
            public int    iOrder;         // unused here
            public uint   uiType;         // unused here
            public IntPtr pvFilter;       // unused here
            public uint   uiState;        // unused here
        }

        /* The grid's sizes and the name cap, and the messages the cap's   */
        /* cut names and the List view's scroll are made with --           */
        /* documentation table.                                            */
        private const int PLV_GRID_ICON_GAP        = 4;               // between a cell's icon and its name, pixels (the list's own gap, with a little room)
        private const int PLV_GRID_CELL_PAD        = 16;              // added to the widest name for the label's own margins and the gap to the next cell, pixels
        internal const int PLV_GRID_NAME_MAX_CHARS = 50;              // THE NAME CAP's default: the List and Small Icons views' cells hold a name of at most this many average ('n') characters, measured in the list's own font; longer names are cut with "..." and shown whole on hover.  A single tunable setting: the owner may set PLV_iNameMaxChars from its registry value
        private const int PLV_LVM_GETEXTENDEDLISTVIEWSTYLE = 0x1037;  // LVM_GETEXTENDEDLISTVIEWSTYLE: the control's extended style bits
        private const int PLV_LVM_SETEXTENDEDLISTVIEWSTYLE = 0x1036;  // LVM_SETEXTENDEDLISTVIEWSTYLE: change them (wParam the bits to change, lParam their values)
        private const int PLV_LVS_EX_LABELTIP      = 0x4000;          // extended style: a name the control has cut short is shown whole in a tooltip on hover
        private const int PLV_LVM_SCROLL           = 0x1014;          // LVM_SCROLL: scroll the view (wParam across -- in the List view, in COLUMNS -- lParam down)
        private const int PLV_LVM_SETITEMTEXTW     = 0x1074;          // LVM_SETITEMTEXTW: set one row's text IN THE CONTROL (wParam the row, lParam an LVITEM; pszText -1 makes it a callback)
        private const int PLV_LVN_GETDISPINFOW     = -177;            // the control asking for a callback row's text (reflected back to the list)
        private const int PLV_LVIF_TEXT            = 0x0001;          // LVITEM mask: the text is asked for

        /*--------------------------------------------------------------------*/
        /* PLV_LvItem -- LVITEMW, as LVM_SETITEMTEXTW reads it: the sub-item  */
        /* and the text pointer are used; the rest are zero and are there so  */
        /* the structure is the size the control expects.                     */
        /*--------------------------------------------------------------------*/
        [StructLayout(LayoutKind.Sequential)]
        private struct PLV_LvItem
        {
            public uint   uiMask;         // not read by LVM_SETITEMTEXTW
            public int    iItem;          // the row (the message takes it from wParam)
            public int    iSubItem;       // 0, the name
            public uint   uiState;        // unused here
            public uint   uiStateMask;    // unused here
            public IntPtr pszText;        // the text, or -1 (LPSTR_TEXTCALLBACK) to be asked for it
            public int    cchTextMax;     // unused here
            public int    iImage;         // unused here
            public IntPtr lParam;         // unused here
            public int    iIndent;        // unused here
            public int    iGroupId;       // unused here
            public uint   cColumns;       // unused here
            public IntPtr puColumns;      // unused here
            public IntPtr piColFmt;       // unused here
            public int    iGroup;         // unused here
        }

        /* The scroll bar GetScrollPos is asked about -- documentation table. */
        private const int PLV_SB_HORZ              = 0;        // GetScrollPos: the horizontal scroll bar (in the List view its position is the first column shown)

        /* What the owner sets: who hears of a column resize and of a click   */
        /* on the heading's box, and the name cap -- documentation table.     */
        internal PLV_ColumnHandler PLV_pfnUserResized;       // called after the user resizes a column; null for "nobody is listening"
        internal PLV_ColumnHandler PLV_pfnHeaderBoxClicked;  // called after the user clicks a heading's check box (the column's index); null for "nobody is listening"
        internal int               PLV_iNameMaxChars;        // the name cap in average characters (PLV_GRID_NAME_MAX_CHARS unless the owner sets it, from its settings); a change applies at the next PLV_GridChanged(true)

        /* The grid of the List and Small Icons views -- see PLV_ArrangeGrid. */
        private int  PLV_iGridWidest;                               // the widest row name, pixels, in the font it was measured in; -1 when the names or the font have changed and it must be measured again
        private int  PLV_iGridCell;                                 // the cell width last applied, pixels; 0 before the first
        private int  PLV_iGridColumns;                              // Small Icons: the number of cells across last laid out; 0 before the first
        private bool PLV_bGridMoved;                                // Small Icons: true when the rows' ORDER or set has changed, so every row must be placed again
        private int  PLV_iGridNameCap;                              // the name cap in pixels (PLV_iNameMaxChars 'n's in the list's font), measured with the names; 0 before the first
        private Dictionary<ListViewItem, string> PLV_dictCutNames;  // the rows whose name is past the cap, each with its cut name (ending "..."): the control asks for their text (PLV_AnswerCutName)

        /* The ".." row's filled box -- see PLV_MarkUnavailable. */
        private bool PLV_bMarking;        // true while PLV_MarkUnavailable gives a row the filled box, so the change-refusal in WndProc lets it through
        private bool PLV_bKeepTicks;      // true while the framework handles a double-click that is not on a box: every tick change is refused (it would toggle the tick, and so the selection)

        /*--------------------------------------------------------------------*/
        /* PLV_PaneListView (constructor):                                    */
        /*                                                                    */
        /* Create the list with double-buffered painting switched on, nobody  */
        /* listening for column resizes or heading-box clicks, and no grid    */
        /* measured yet.  All else                                            */
        /* about it is set by whoever creates it, as for any list view.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the list, not yet parented.                    */
        /*--------------------------------------------------------------------*/
        internal PLV_PaneListView()
        {
            /* Nobody listening yet; the grid not measured (-1) and nothing    */
            /* applied, so the first arrange does everything; no row being     */
            /* marked; the name cap at its default until the owner sets it;    */
            /* no cut names.  PLV_bKeepTicks is left at the false a new        */
            /* object has.  Then the double-buffered painting the file         */
            /* header explains -- settable only from inside a derived class.   */
            PLV_pfnUserResized      = null;
            PLV_pfnHeaderBoxClicked = null;
            PLV_iGridWidest     = -1;
            PLV_iGridCell       = 0;
            PLV_iGridColumns    = 0;
            PLV_bGridMoved      = true;
            PLV_bMarking        = false;
            PLV_iGridNameCap    = 0;
            PLV_iNameMaxChars   = PLV_GRID_NAME_MAX_CHARS;
            PLV_dictCutNames    = new Dictionary<ListViewItem, string>();
            this.DoubleBuffered = true;

            /* Rows' tooltips are shown on hover: only a cut name has one,   */
            /* so a name the cap shortened can still be read whole.          */
            this.ShowItemToolTips = true;
        }

        /*--------------------------------------------------------------------*/
        /* PLV_GridChanged:                                                   */
        /*                                                                    */
        /* Tell the list that what its grid was laid out from has changed,    */
        /* so the next PLV_ArrangeGrid does the work again instead of finding */
        /* nothing to do.  New rows, or a new font, change the names' widths  */
        /* (bNamesChanged); a sort only changes their order.  Either way the  */
        /* rows must be placed again.                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bNamesChanged : true when the rows or the font changed (the    */
        /*                     widest name is measured again), false when     */
        /*                     only the order did.                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the next PLV_ArrangeGrid lays the grid out afresh.      */
        /*--------------------------------------------------------------------*/
        internal void PLV_GridChanged(bool bNamesChanged)
        {
            if (bNamesChanged)
            {   /* The widths may be different: measure them again. */
                PLV_iGridWidest = -1;
            }

            /* Whatever changed, the next arrange applies everything again:    */
            /* the rows are placed, and the List view's column width is sent   */
            /* even if it is the same number (a change of view resets the      */
            /* control's own width, which this list cannot see).               */
            PLV_iGridCell  = 0;
            PLV_bGridMoved = true;
        }

        /*--------------------------------------------------------------------*/
        /* PLV_ArrangeGridLater:                                              */
        /*                                                                    */
        /* Lay the grid out again once the current message is done -- for a   */
        /* change of view, where the control finishes switching after the     */
        /* call that asked for it, so a layout made at once can be left with  */
        /* the old view's figures.  The caller lays it out at once as well;   */
        /* this second pass costs one placement of the rows.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : a fresh layout is posted to the message loop.           */
        /*--------------------------------------------------------------------*/
        internal void PLV_ArrangeGridLater()
        {
            if (!this.IsHandleCreated)
            {   /* No window: the first layout will happen when there is one   */
                /* (and BeginInvoke needs a window to post to).                */
                return;
            }

            /* Posted to this list's message loop, so it runs once the   */
            /* current message -- the view change -- is finished.        */
            this.BeginInvoke(new MethodInvoker(Plv_RunArrangeAgain));
        }

        /*--------------------------------------------------------------------*/
        /* Plv_RunArrangeAgain:                                               */
        /*                                                                    */
        /* The layout PLV_ArrangeGridLater posts: once the view change is     */
        /* finished, every row placed again.                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the grid is laid out afresh.                            */
        /*--------------------------------------------------------------------*/
        private void Plv_RunArrangeAgain()
        {
            /* Marked so the arrange does everything, then laid out. */
            PLV_GridChanged(false);
            PLV_ArrangeGrid();
        }

        /*---------------------------------------------------------------------*/
        /* PLV_ArrangeGrid:                                                    */
        /*                                                                     */
        /* Lay out the List and Small Icons views on an even grid, as Windows  */
        /* Explorer does, so every name fits and the rows line up: every cell  */
        /* the width of the widest name (up to the name cap, PLV_iNameMaxChars */
        /* average characters) plus its icon and a little room, never wider    */
        /* than the list.  A name past the cap is cut with "..." and shown     */
        /* whole on hover.  The other views are left alone: Details has its    */
        /* own columns, Large Icons and Tiles arrange themselves.  How each    */
        /* of the two views is laid out is said in the body.                   */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     None.                                                           */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : the view's rows line up on the grid.                     */
        /*---------------------------------------------------------------------*/
        internal void PLV_ArrangeGrid()
        {
            PLV_Point ptRow;
            int iIcon;
            int iScrolled;
            int iCell;
            int iColumns;
            int iRowHeight;
            int iName;
            int i;

            if ((this.View != View.List && this.View != View.SmallIcon) || !this.IsHandleCreated)
            {   /* Not a grid view, or no window yet to lay out. */
                return;
            }

            if (PLV_iGridWidest < 0)
            {   /* The names or the font changed: find the widest name again,    */
                /* and the cap with it.  THE CAP keeps one very long name from   */
                /* making every cell unwieldy, in both grid views: no name       */
                /* column wider than PLV_iNameMaxChars average characters,       */
                /* measured by the control in its own font so it follows the     */
                /* text size (a setting below 1 is taken as 1, so a bad          */
                /* registry value cannot remove the names).  The cut names are   */
                /* found afresh below.                                           */
                PLV_iGridWidest  = 0;
                PLV_iGridNameCap = (int)PLV_SendMessageText(this.Handle, PLV_LVM_GETSTRINGWIDTHW, IntPtr.Zero,
                                                            new string('n', Math.Max(1, PLV_iNameMaxChars)));
                PLV_dictCutNames.Clear();

                for (i = 0; i < this.Items.Count; i++)
                {   /* One row per iteration, measured BY THE CONTROL: its    */
                    /* own answer for how wide it draws the text              */
                    /* (LVM_GETSTRINGWIDTH).  TextRenderer measured this      */
                    /* list's font about a tenth wider than the control       */
                    /* drew it (223 against about 198 pixels for one name),   */
                    /* which cost a column; the control's figure is by        */
                    /* definition the width it shows.                         */
                    iName = (int)PLV_SendMessageText(this.Handle, PLV_LVM_GETSTRINGWIDTHW, IntPtr.Zero, this.Items[i].Text);

                    if (iName > PLV_iGridWidest)
                    {   /* The widest so far. */
                        PLV_iGridWidest = iName;
                    }

                    /* A name past the cap is cut in both grid views, so its    */
                    /* whole text is the row's tooltip, shown on hover; the     */
                    /* rest have none.  The control is made to ASK for          */
                    /* such a row's text, so Small Icons can be given the cut   */
                    /* name (PLV_SetRowCallback says why); a row that is no     */
                    /* longer cut -- its tooltip says it was -- gets its own    */
                    /* text back in the control.                                */
                    if (PLV_iGridNameCap > 0 && iName > PLV_iGridNameCap)
                    {   /* Cut: the whole name on hover, the cut one asked for. */
                        this.Items[i].ToolTipText = this.Items[i].Text;
                        PLV_dictCutNames[this.Items[i]] = PLV_CutName(this.Items[i].Text);
                        PLV_SetRowCallback(i, true);
                    }
                    else if (this.Items[i].ToolTipText.Length != 0)
                    {   /* Not cut (any more): no tooltip, its own text again. */
                        this.Items[i].ToolTipText = "";
                        PLV_SetRowCallback(i, false);
                    }
                }
            }

            /* The cell: the icon, the gap, the widest name and the margins --   */
            /* no wider than the list, and at least one pixel.                   */
            iIcon = 0;

            if (this.SmallImageList != null)
            {   /* Both grid views draw the small icons. */
                iIcon = this.SmallImageList.ImageSize.Width;
            }

            /* The name's share of the cell: the widest name, up to the cap.    */
            /* A longer name is cut with "..." -- in the List view by the       */
            /* control itself, in Small Icons by the cut name WndProc hands     */
            /* the control when it asks for the row's text                      */
            /* (PLV_AnswerCutName), since there it would draw the whole name,   */
            /* into its neighbour.                                              */
            iName = PLV_iGridWidest;

            if (PLV_iGridNameCap > 0 && iName > PLV_iGridNameCap)
            {   /* Longer than the cap: the column stops there. */
                iName = PLV_iGridNameCap;
            }

            if (this.View == View.List)
            {   /* The List view's in-place label tip as well, for a cut name. */
                PLV_EnsureLabelTip();
            }

            /* The cell: the icon, the gap after it, the name's share and   */
            /* the margins with the gap to the next cell.                   */
            iCell = iIcon + PLV_GRID_ICON_GAP + iName + PLV_GRID_CELL_PAD;

            if (this.CheckBoxes)
            {   /* Item check boxes: the box sits before the icon, so the cell   */
                /* is wider by the box and a gap.  Left out, the widest name     */
                /* would be cut short by the box's width.                        */
                iCell += PLV_CheckBoxWidth() + PLV_GRID_ICON_GAP;
            }

            if (iCell > this.ClientSize.Width)
            {   /* Wider than the list: one cell across, the whole width. */
                iCell = this.ClientSize.Width;
            }

            if (iCell < 1)
            {   /* A pane collapsed to nothing. */
                iCell = 1;
            }

            if (this.View == View.List)
            {   /* THE LIST VIEW already lays its rows out in even columns,    */
                /* top to bottom; it only needs the column's width, which      */
                /* it otherwise guesses too narrow, cutting names short.  It   */
                /* is told the cell width (LVM_SETCOLUMNWIDTH, column 0)       */
                /* only when that changes.                                     */
                if (iCell != PLV_iGridCell)
                {   /* A new width: the control re-flows its columns itself --    */
                    /* but NOT while it is scrolled sideways.  It keeps the old   */
                    /* scroll offset in PIXELS while working the scroll bar out   */
                    /* afresh in COLUMNS, so the first column would be left off   */
                    /* the pane's left edge with the bar at its start and no      */
                    /* way back (as found when measured: scrolled one column at   */
                    /* 250 px and widened in steps -- the column follows the      */
                    /* pane's width while the pane is narrower than a cell --     */
                    /* the first row was left at -246 px with the bar's           */
                    /* position 0).  So the list is scrolled                      */
                    /* back to its first column for the change and then on to     */
                    /* the column it showed before (the control clamps that to    */
                    /* what still needs scrolling).  LVM_SCROLL counts in         */
                    /* columns in this view, as the bar's position does.          */
                    iScrolled = PLV_GetScrollPos(this.Handle, PLV_SB_HORZ);

                    if (iScrolled > 0)
                    {   /* Scrolled: back to the first column first. */
                        PLV_SendMessage(this.Handle, PLV_LVM_SCROLL, (IntPtr)(-iScrolled), IntPtr.Zero);
                    }

                    /* The new width to the control, and remembered so the   */
                    /* same width is not sent again.                         */
                    PLV_SendMessage(this.Handle, PLV_LVM_SETCOLUMNWIDTH, IntPtr.Zero, (IntPtr)iCell);
                    PLV_iGridCell = iCell;

                    if (iScrolled > 0)
                    {   /* And on to the same column again, at the new width. */
                        PLV_SendMessage(this.Handle, PLV_LVM_SCROLL, (IntPtr)iScrolled, IntPtr.Zero);
                    }
                }

                /* Nothing more to do: the List view places its own rows. */
                return;
            }

            /* THE SMALL ICONS VIEW has no such column: the list control       */
            /* places each row in as many of its own fixed-width slots as      */
            /* the name needs, so one short name (the ".." row) knocks every   */
            /* row after it out of line.  So the rows are PLACED here, one     */
            /* cell each, with the list's own arranging switched off (the      */
            /* caller sets AutoArrange false for this view).  First, how       */
            /* many cells fit across.                                          */
            iColumns = this.ClientSize.Width / iCell;

            if (iColumns < 1)
            {   /* Narrower than one cell: one column. */
                iColumns = 1;
            }

            if (!PLV_bGridMoved && iCell == PLV_iGridCell && iColumns == PLV_iGridColumns)
            {   /* Nothing the layout depends on has changed -- the rows,      */
                /* their order, the font, the cell width, how many fit         */
                /* across: the rows stay where they are.  Placing them costs   */
                /* a message each, so a resize that changes none of these      */
                /* does nothing.                                               */
                return;
            }

            /* What this layout is made from, remembered for that test next   */
            /* time.                                                          */
            PLV_iGridCell    = iCell;
            PLV_iGridColumns = iColumns;
            PLV_bGridMoved   = false;

            if (this.Items.Count == 0)
            {   /* An empty folder: nothing to place. */
                return;
            }

            /* A row is as tall as the control spaces small-icon rows (the     */
            /* icon or a line of the font, whichever is taller, and its        */
            /* margins): its own answer, LVM_GETITEMSPACING for the small      */
            /* views, the height in the high word.  NOT a row's bounds: just   */
            /* after a change of view those can still be the old view's (a     */
            /* Large Icons row is twice the height), which spaced the rows     */
            /* wrongly when the view was switched to Small Icons.              */
            iRowHeight = (int)(((long)PLV_SendMessage(this.Handle, PLV_LVM_GETITEMSPACING, (IntPtr)1, IntPtr.Zero) >> 16) & 0xFFFF);

            if (iRowHeight <= 0)
            {   /* No answer: the first row's own height stands in. */
                iRowHeight = this.Items[0].Bounds.Height;
            }

            /* The list's drawing held while every row moves, so it is not   */
            /* redrawn once per row (as found).                              */
            this.BeginUpdate();

            for (i = 0; i < this.Items.Count; i++)
            {   /* One row per iteration, in the list's order: across, then    */
                /* down -- told to the CONTROL directly.  Not through the      */
                /* item's Position property: its setter compares the new       */
                /* value with the position IT last set, not with where the     */
                /* control has the row now, and skips the call when they       */
                /* match.  After a change of view the control has moved the    */
                /* rows but the property still remembers the grid's places,    */
                /* so every row was skipped and the top row stayed where the   */
                /* control put it (measured: three layouts, not one position   */
                /* message reached the control).                               */
                ptRow.iX = (i % iColumns) * iCell;
                ptRow.iY = (i / iColumns) * iRowHeight;
                PLV_SendMessagePoint(this.Handle, PLV_LVM_SETITEMPOSITION32, (IntPtr)i, ref ptRow);
            }

            /* Every row placed: the list draws again, once. */
            this.EndUpdate();
        }

        /*--------------------------------------------------------------------*/
        /* PLV_EnsureLabelTip:                                                */
        /*                                                                    */
        /* Make sure the control has its LVS_EX_LABELTIP style, so a name it  */
        /* has cut short with "..." (the List view's capped column) is shown  */
        /* whole in a tooltip while the pointer rests on it.  Checked each    */
        /* time the grid is laid out rather than set once, because the        */
        /* framework rewrites the extended styles when some of its own        */
        /* properties change and might not keep a bit it does not manage      */
        /* (not verified either way); asking costs one message.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the style is set (nothing is done with no window).      */
        /*--------------------------------------------------------------------*/
        private void PLV_EnsureLabelTip()
        {
            int iStyle;

            if (!this.IsHandleCreated)
            {   /* No control yet: the next layout, once there is one, sets it. */
                return;
            }

            /* The control's extended style bits as they are now. */
            iStyle = (int)PLV_SendMessage(this.Handle, PLV_LVM_GETEXTENDEDLISTVIEWSTYLE, IntPtr.Zero, IntPtr.Zero);

            if ((iStyle & PLV_LVS_EX_LABELTIP) == 0)
            {   /* Not set: only that bit changes. */
                PLV_SendMessage(this.Handle, PLV_LVM_SETEXTENDEDLISTVIEWSTYLE, (IntPtr)PLV_LVS_EX_LABELTIP, (IntPtr)PLV_LVS_EX_LABELTIP);
            }
        }

        /*--------------------------------------------------------------------*/
        /* PLV_CheckBoxWidth:                                                 */
        /*                                                                    */
        /* How wide the control draws an item check box, in pixels: the       */
        /* width of the pictures in its STATE image list, which the control   */
        /* makes itself when check boxes are switched on, sized for the       */
        /* display's scaling.  Asked of the control rather than assumed, as   */
        /* the names' widths are, because the box grows with the display      */
        /* scale and a fixed figure would be right at only one of them.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : the box's width; the system's small icon width when the  */
        /*           control has no state image list yet or will not say      */
        /*           (the box is drawn at that size by default).              */
        /*--------------------------------------------------------------------*/
        private int PLV_CheckBoxWidth()
        {
            IntPtr hList;
            int    iWidth;
            int    iHeight;

            /* The control's own state image list, if it has made one. */
            hList = PLV_SendMessage(this.Handle, PLV_LVM_GETIMAGELIST, (IntPtr)PLV_LVSIL_STATE, IntPtr.Zero);

            if (hList != IntPtr.Zero && PLV_ImageListGetIconSize(hList, out iWidth, out iHeight) && iWidth > 0)
            {   /* It has one: its pictures are the boxes. */
                return(iWidth);
            }

            /* No list or no answer: the size a box is drawn at by default. */
            return(SystemInformation.SmallIconSize.Width);
        }

        /*----------------------------------------------------------------------*/
        /* PLV_AddFilledBox:                                                    */
        /*                                                                      */
        /* Make sure the control's own check-box pictures include a third,      */
        /* SOLID-FILLED box for a row that can never be ticked -- the ".."      */
        /* row's, so its box says it is not clickable.  Hiding its box          */
        /* (state image none) did not hold in the                               */
        /* real window -- the framework tracks each row's picture itself and    */
        /* seemingly writes its own back when it updates the row -- so the      */
        /* row is given this third picture through the framework, which then    */
        /* keeps it (PLV_MarkUnavailable).                                      */
        /*                                                                      */
        /* WHY THE CONTROL'S OWN LIST AND NOT ONE OF OURS: given a state list   */
        /* of three pictures, switching the boxes on EMPTIED it (measured       */
        /* off screen: 3 pictures before, 0 after, nothing drawn).  So the      */
        /* control makes its two boxes as usual -- they look exactly as the     */
        /* system's -- and the third is made from ITS empty box -- taken out as */
        /* an icon, which keeps the alpha -- with the inside filled in the      */
        /* system's grey-text colour, and appended.  A fresh list (boxes        */
        /* switched on again, the window remade) starts with two again, so      */
        /* this is called after every switch and does nothing once three are    */
        /* there (one message and one count to find out).                       */
        /*                                                                      */
        /* Arguments:                                                           */
        /*     None.                                                            */
        /*                                                                      */
        /* Returns:                                                             */
        /*     void : the state list has the filled box as its third picture    */
        /*            (nothing is done without a window, boxes or a list).      */
        /*----------------------------------------------------------------------*/
        internal void PLV_AddFilledBox()
        {
            IntPtr hList;
            IntPtr hIconIn;
            IntPtr hIconOut;
            Bitmap bmp;

            if (!this.IsHandleCreated || !this.CheckBoxes)
            {   /* No boxes showing: nothing to add to. */
                return;
            }

            /* The control's own state image list -- the boxes -- if it has   */
            /* made one.                                                      */
            hList = PLV_SendMessage(this.Handle, PLV_LVM_GETIMAGELIST, (IntPtr)PLV_LVSIL_STATE, IntPtr.Zero);

            if (hList == IntPtr.Zero || PLV_ImageListGetImageCount(hList) != PLV_STATE_UNAVAILABLE)
            {   /* No list, or not exactly the control's two: the third is   */
                /* there already (or the list is not one we understand).     */
                return;
            }

            /* The control's empty box, as an icon (which carries the alpha). */
            hIconIn = PLV_ImageListGetIcon(hList, PLV_STATE_UNCHECKED, 0);

            if (hIconIn == IntPtr.Zero)
            {   /* It would not give it: the row keeps an empty box instead. */
                return;
            }

            using (Icon icoBox = Icon.FromHandle(hIconIn))
            {   /* Into a bitmap with the alpha kept, filled, back to an icon. */
                bmp = icoBox.ToBitmap();
            }

            /* The control's icon is a copy the image list made for us, so   */
            /* it is destroyed here (Icon.FromHandle does not own it -- as   */
            /* found); the                                                   */
            /* bitmap's inside filled; and back to an icon for the list --   */
            /* the bitmap is not needed after.                               */
            PLV_DestroyIcon(hIconIn);
            PLV_FillBoxInside(bmp);
            hIconOut = bmp.GetHicon();
            bmp.Dispose();

            /* Appended (-1): it becomes picture 3, StateImageIndex 2. */
            PLV_ImageListReplaceIcon(hList, -1, hIconOut);
            PLV_DestroyIcon(hIconOut);
        }

        /*--------------------------------------------------------------------*/
        /* PLV_FillBoxInside:                                                 */
        /*                                                                    */
        /* Fill the inside of a check-box picture solid, in the system's      */
        /* grey-text colour, so it reads as a box that is there but not to be */
        /* used.  The box is found from the picture itself -- the smallest    */
        /* rectangle holding every visible pixel, since the control's picture */
        /* may have empty margins -- and the fill leaves two pixels of it all */
        /* round, for the box's own border.                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bmp : the picture, 32-bit with alpha; changed in place.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the inside is filled (left alone if the box is too      */
        /*            small to have one).                                     */
        /*--------------------------------------------------------------------*/
        private static void PLV_FillBoxInside(Bitmap bmp)
        {
            int iLeft;
            int iTop;
            int iRight;
            int iBottom;
            int x;
            int y;

            /* The visible box's bounds start inside out -- left and top at   */
            /* the far edges, right and bottom before the first pixel -- so   */
            /* the first visible pixel sets all four.                         */
            iLeft   = bmp.Width;
            iTop    = bmp.Height;
            iRight  = -1;
            iBottom = -1;

            for (y = 0; y < bmp.Height; y++)
            {   /* One line per iteration: widen the box to its visible pixels. */
                for (x = 0; x < bmp.Width; x++)
                {   /* One pixel: part of the box when it shows at all. */
                    if (bmp.GetPixel(x, y).A != 0)
                    {   /* Visible: the box reaches here. */
                        iLeft   = Math.Min(iLeft, x);
                        iTop    = Math.Min(iTop, y);
                        iRight  = Math.Max(iRight, x);
                        iBottom = Math.Max(iBottom, y);
                    }
                }
            }

            if (iRight - iLeft < 5 || iBottom - iTop < 5)
            {   /* Nothing visible, or too small to have an inside. */
                return;
            }

            using (Graphics g = Graphics.FromImage(bmp))
            using (SolidBrush brFill = new SolidBrush(SystemColors.GrayText))
            {   /* The system's colour for something not available, inside the border. */
                g.FillRectangle(brFill, iLeft + 2, iTop + 2, iRight - iLeft - 3, iBottom - iTop - 3);
            }
        }

        /*--------------------------------------------------------------------*/
        /* PLV_MarkUnavailable:                                               */
        /*                                                                    */
        /* Give one row the solid-filled box -- the ".." row, which is a way  */
        /* up and never an item to tick.  Set through the row's own           */
        /* StateImageIndex, so the framework REMEMBERS it and writes it, not  */
        /* the empty box, whenever it updates the row afterwards (selecting   */
        /* it, sorting, ...).  WndProc refuses any state-image change to or   */
        /* from this picture except this one, which PLV_bMarking lets by.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iIndex : the row.                                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the row shows the filled box (nothing is done while the */
        /*            list has no window, no boxes, or no third picture).     */
        /*--------------------------------------------------------------------*/
        internal void PLV_MarkUnavailable(int iIndex)
        {
            IntPtr hList;

            if (!this.IsHandleCreated || !this.CheckBoxes)
            {   /* Not showing boxes: nothing to mark. */
                return;
            }

            /* The filled box must exist, or the row would show no box at all. */
            hList = PLV_SendMessage(this.Handle, PLV_LVM_GETIMAGELIST, (IntPtr)PLV_LVSIL_STATE, IntPtr.Zero);

            if (hList == IntPtr.Zero || PLV_ImageListGetImageCount(hList) <= PLV_STATE_UNAVAILABLE)
            {   /* No third picture (PLV_AddFilledBox could not make it): the row keeps its empty box. */
                return;
            }

            if (this.Items[iIndex].StateImageIndex == PLV_STATE_UNAVAILABLE)
            {   /* Already marked: no change, no redraw. */
                return;
            }

            /* Set through the row's own StateImageIndex, so the framework      */
            /* remembers it, with PLV_bMarking up for the one change: WndProc   */
            /* refuses every other change to or from this picture.              */
            PLV_bMarking = true;
            this.Items[iIndex].StateImageIndex = PLV_STATE_UNAVAILABLE;
            PLV_bMarking = false;
        }

        /*--------------------------------------------------------------------*/
        /* PLV_SetHeaderCheckBox:                                             */
        /*                                                                    */
        /* Show, tick or take away the select-all check box in the FIRST      */
        /* column's heading -- Explorer's box above the names in Details      */
        /* view while item check boxes are on.  The heading control draws it  */
        /* once it has the HDS_CHECKBOXES style and the heading's format has  */
        /* HDF_CHECKBOX; HDF_CHECKED ticks it.  Only those two bits of the    */
        /* format are changed, so the heading's alignment is kept.  The       */
        /* style is added once and left: without HDF_CHECKBOX on a heading it */
        /* draws nothing.                                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bShow    : true to show the box, false to take it away.        */
        /*     bChecked : with bShow, true to tick it.                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the heading shows the box as asked (nothing is done     */
        /*            with no window, no heading or no column).               */
        /*--------------------------------------------------------------------*/
        internal void PLV_SetHeaderCheckBox(bool bShow, bool bChecked)
        {
            IntPtr     hHeader;
            PLV_HdItem hdItem;
            int        iStyle;
            int        iFmt;

            if (!this.IsHandleCreated || this.Columns.Count == 0)
            {   /* Nothing to put a box on yet. */
                return;
            }

            /* The list's heading control: one exists in every view, shown   */
            /* only in Details.                                              */
            hHeader = PLV_SendMessage(this.Handle, PLV_LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);

            if (hHeader == IntPtr.Zero)
            {   /* No heading control. */
                return;
            }

            if (bShow)
            {   /* The control must allow boxes before a heading can show one. */
                iStyle = PLV_GetWindowLong(hHeader, PLV_GWL_STYLE);

                if ((iStyle & PLV_HDS_CHECKBOXES) == 0)
                {   /* Not yet: add the style. */
                    PLV_SetWindowLong(hHeader, PLV_GWL_STYLE, iStyle | PLV_HDS_CHECKBOXES);
                }
            }

            /* The first heading's format, read so that only the box's two   */
            /* bits change.                                                  */
            hdItem        = new PLV_HdItem();
            hdItem.uiMask = PLV_HDI_FORMAT;

            if (PLV_SendMessageHeader(hHeader, PLV_HDM_GETITEMW, IntPtr.Zero, ref hdItem) == IntPtr.Zero)
            {   /* The heading would not say: leave it alone. */
                return;
            }

            /* The format with the box's two bits cleared: a heading taking    */
            /* its box away keeps it so, and one showing it sets them below.   */
            iFmt = hdItem.iFmt & ~(PLV_HDF_CHECKBOX | PLV_HDF_CHECKED);

            if (bShow)
            {   /* The box, ticked or not. */
                iFmt |= PLV_HDF_CHECKBOX;

                if (bChecked)
                {   /* Every item is selected. */
                    iFmt |= PLV_HDF_CHECKED;
                }
            }

            if (iFmt == hdItem.iFmt)
            {   /* Already as asked: no redraw. */
                return;
            }

            /* The new format written back -- the mask still names only fmt,   */
            /* so nothing else of the heading changes.  Its answer is not      */
            /* checked: a refused write leaves the heading as it was.          */
            hdItem.iFmt = iFmt;
            PLV_SendMessageHeader(hHeader, PLV_HDM_SETITEMW, IntPtr.Zero, ref hdItem);
        }

        /*--------------------------------------------------------------------*/
        /* PLV_SendMessageHeader:                                             */
        /*                                                                    */
        /* user32's SendMessageW a fifth time, for a heading message whose    */
        /* second parameter is an HDITEM (passed by address) -- HDM_GETITEMW  */
        /* and HDM_SETITEMW.                                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hWnd     : the heading control's window.                       */
        /*     iMessage : the message.                                        */
        /*     pWParam  : its first parameter (the heading's index).          */
        /*     hdLParam : its second parameter, the item; filled by a GET.    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     IntPtr : the window's answer (nonzero on success).             */
        /*--------------------------------------------------------------------*/
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
        private static extern IntPtr PLV_SendMessageHeader(IntPtr hWnd, int iMessage, IntPtr pWParam, ref PLV_HdItem hdLParam);

        /*--------------------------------------------------------------------*/
        /* PLV_GetWindowLong:                                                 */
        /*                                                                    */
        /* user32's GetWindowLongW: one of a window's 32-bit values -- here   */
        /* its style bits, which fit 32 bits on every Windows, 64-bit too.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hWnd   : the window.                                           */
        /*     iIndex : which value (GWL_STYLE).                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : the value.                                               */
        /*--------------------------------------------------------------------*/
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int PLV_GetWindowLong(IntPtr hWnd, int iIndex);

        /*--------------------------------------------------------------------*/
        /* PLV_SetWindowLong:                                                 */
        /*                                                                    */
        /* user32's SetWindowLongW: change one of a window's 32-bit values -- */
        /* here the heading control's style, to add HDS_CHECKBOXES.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hWnd     : the window.                                         */
        /*     iIndex   : which value (GWL_STYLE).                            */
        /*     iNewLong : the new value.                                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : the value it replaced.                                   */
        /*--------------------------------------------------------------------*/
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int PLV_SetWindowLong(IntPtr hWnd, int iIndex, int iNewLong);

        /*--------------------------------------------------------------------*/
        /* PLV_CutName:                                                       */
        /*                                                                    */
        /* A name past the cap, cut to fit it: the longest beginning of the   */
        /* name that, with a "..." (U+2026, built from its code point) after  */
        /* it, the control draws no wider than the cap -- found by halving,   */
        /* each trial measured by the control in its own font.                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sName : the whole name.                                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the cut name, ending in "..." (just the "..." when    */
        /*              not even one character fits).                         */
        /*--------------------------------------------------------------------*/
        private string PLV_CutName(string sName)
        {
            string sEllipsis;
            int    iLow;
            int    iHigh;
            int    iMid;
            int    iWidth;

            /* The one-character ellipsis; the search between none of the     */
            /* name (which always "fits": the bare ellipsis) and all of it.   */
            sEllipsis = new string((char)0x2026, 1);
            iLow      = 0;
            iHigh     = sName.Length;

            while (iLow < iHigh)
            {   /* One halving per iteration: iLow characters are known to fit. */
                iMid   = (iLow + iHigh + 1) / 2;
                iWidth = (int)PLV_SendMessageText(this.Handle, PLV_LVM_GETSTRINGWIDTHW, IntPtr.Zero, sName.Substring(0, iMid) + sEllipsis);

                if (iWidth <= PLV_iGridNameCap)
                {   /* This many fit: try more. */
                    iLow = iMid;
                }
                else
                {   /* Too wide: fewer. */
                    iHigh = iMid - 1;
                }
            }

            /* The most that fits, then the "...". */
            return(sName.Substring(0, iLow) + sEllipsis);
        }

        /*--------------------------------------------------------------------*/
        /* PLV_SetRowCallback:                                                */
        /*                                                                    */
        /* Make one row's text, IN THE CONTROL, either a callback (the        */
        /* control then asks for it each time it needs it -- LVN_GETDISPINFO, */
        /* answered by PLV_AnswerCutName) or the row's own text again.  The   */
        /* framework's copy of the text -- what the program reads as the      */
        /* name -- is not touched either way; only what the control holds.    */
        /* WHY: Small Icons draws a name whole, whatever the cell (measured   */
        /* off screen: a 666-pixel label), and the label's whole width is     */
        /* also what a click hits -- so clipping the drawing alone left a     */
        /* click on the next row's icon taking the long row (measured).  A    */
        /* label the control itself believes is short fixes both.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iRow      : the row.                                           */
        /*     bCallback : true for the callback, false for the row's text.   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the control holds the callback or the text.             */
        /*--------------------------------------------------------------------*/
        private void PLV_SetRowCallback(int iRow, bool bCallback)
        {
            PLV_LvItem lvItem;
            IntPtr     pText;

            /* An item naming the first column -- the name -- as the one   */
            /* whose text is set; every other field stays zero.            */
            lvItem          = new PLV_LvItem();
            lvItem.iSubItem = 0;

            if (bCallback)
            {   /* LPSTR_TEXTCALLBACK: ask me. */
                lvItem.pszText = (IntPtr)(-1);
                PLV_SendMessageItem(this.Handle, PLV_LVM_SETITEMTEXTW, (IntPtr)iRow, ref lvItem);
                return;
            }

            /* The row's own text back, copied to memory the call can read. */
            pText = Marshal.StringToHGlobalUni(this.Items[iRow].Text);
            lvItem.pszText = pText;
            PLV_SendMessageItem(this.Handle, PLV_LVM_SETITEMTEXTW, (IntPtr)iRow, ref lvItem);
            Marshal.FreeHGlobal(pText);
        }

        /*--------------------------------------------------------------------*/
        /* PLV_AnswerCutName:                                                 */
        /*                                                                    */
        /* The control asks for a row's text (LVN_GETDISPINFO): answered for  */
        /* a row PLV_SetRowCallback made a callback -- the cut name in Small  */
        /* Icons, the whole name in every other view (the List view cuts it   */
        /* itself, Details by its column, Large Icons and Tiles by their own  */
        /* width).  Any other request is left to the framework.  The          */
        /* request's item: after the three-field header, LVITEMW's mask,      */
        /* row, sub-item, state and state mask (five 4-byte fields), then the */
        /* text buffer and its size in characters, pointer-aligned.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pData : the NMLVDISPINFOW.                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when answered here, false to pass it on.           */
        /*--------------------------------------------------------------------*/
        private bool PLV_AnswerCutName(IntPtr pData)
        {
            int     iBase;
            int     iRow;
            int     iTextAt;
            IntPtr  pBuffer;
            int     iChars;
            string  sText;
            char[]  acText;

            /* Where the item starts: after the NMHDR's three fields, which   */
            /* with the item's pointer alignment take three pointer widths    */
            /* (12 bytes on 32-bit, 24 on 64-bit).                            */
            iBase = 3 * IntPtr.Size;

            if ((Marshal.ReadInt32(pData, iBase) & PLV_LVIF_TEXT) == 0 || Marshal.ReadInt32(pData, iBase + 8) != 0)
            {   /* Not asking for the name's text. */
                return(false);
            }

            /* The row asked about: the item's second field. */
            iRow = Marshal.ReadInt32(pData, iBase + 4);

            if (iRow < 0 || iRow >= this.Items.Count || !PLV_dictCutNames.ContainsKey(this.Items[iRow]))
            {   /* Not one of our callback rows. */
                return(false);
            }

            /* The cut name in Small Icons, the whole name elsewhere. */
            sText = this.Items[iRow].Text;

            if (this.View == View.SmallIcon)
            {   /* Cut. */
                sText = PLV_dictCutNames[this.Items[iRow]];
            }

            /* Copied into the control's buffer, cut to fit it, then the NUL. */
            iTextAt = ((iBase + 20 + IntPtr.Size - 1) / IntPtr.Size) * IntPtr.Size;
            pBuffer = Marshal.ReadIntPtr(pData, iTextAt);
            iChars  = Marshal.ReadInt32(pData, iTextAt + IntPtr.Size);

            if (pBuffer == IntPtr.Zero || iChars <= 0)
            {   /* No buffer to fill: nothing to say. */
                return(true);
            }

            if (sText.Length > iChars - 1)
            {   /* Longer than the buffer: what fits. */
                sText = sText.Substring(0, iChars - 1);
            }

            /* The characters into the control's buffer, then the 2-byte   */
            /* NUL after them (UTF-16) -- room for which the cut above     */
            /* kept.                                                       */
            acText = sText.ToCharArray();
            Marshal.Copy(acText, 0, pBuffer, acText.Length);
            Marshal.WriteInt16(pBuffer, acText.Length * 2, 0);

            /* Answered. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* OnHandleCreated:                                                   */
        /*                                                                    */
        /* The control's window was made -- first, or again when the          */
        /* framework re-creates it, which puts every row's own text back in   */
        /* the control and so loses the callbacks.  The names are measured    */
        /* again at the next layout, which makes the callbacks afresh.        */
        /*                                                                    */
        /* An override because the framework requires it: a control hears     */
        /* that its window was made only through this virtual method.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     evArgs : passed on.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the next PLV_ArrangeGrid measures the names again.      */
        /*--------------------------------------------------------------------*/
        protected override void OnHandleCreated(EventArgs evArgs)
        {
            /* The framework's own handling first (it fills the new window    */
            /* with the rows), then the grid marked for measuring again, so   */
            /* the next layout makes the callbacks afresh.                    */
            base.OnHandleCreated(evArgs);
            PLV_GridChanged(true);
        }

        /*--------------------------------------------------------------------*/
        /* PLV_SendMessageItem:                                               */
        /*                                                                    */
        /* user32's SendMessageW for a message whose second parameter is an   */
        /* LVITEM (passed by address) -- LVM_SETITEMTEXTW.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hWnd     : the window.                                         */
        /*     iMessage : the message.                                        */
        /*     pWParam  : its first parameter (the row's index).              */
        /*     lvLParam : its second parameter, the item.                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     IntPtr : the window's answer (nonzero on success).             */
        /*--------------------------------------------------------------------*/
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
        private static extern IntPtr PLV_SendMessageItem(IntPtr hWnd, int iMessage, IntPtr pWParam, ref PLV_LvItem lvLParam);

        /*--------------------------------------------------------------------*/
        /* PLV_GetScrollPos:                                                  */
        /*                                                                    */
        /* user32's GetScrollPos: where one of a window's scroll bars is --   */
        /* here the list's horizontal bar, whose position in the List view is */
        /* the first column shown (0 when not scrolled, or with no bar).      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hWnd : the window.                                             */
        /*     iBar : which bar (SB_HORZ).                                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : the position.                                            */
        /*--------------------------------------------------------------------*/
        [DllImport("user32.dll", EntryPoint = "GetScrollPos")]
        private static extern int PLV_GetScrollPos(IntPtr hWnd, int iBar);

        /*--------------------------------------------------------------------*/
        /* PLV_ImageListGetImageCount:                                        */
        /*                                                                    */
        /* comctl32's ImageList_GetImageCount: how many pictures a native     */
        /* image list holds -- here the control's state list, two boxes or    */
        /* three with the filled one.                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hList : the image list's handle.                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : the number of pictures.                                  */
        /*--------------------------------------------------------------------*/
        [DllImport("comctl32.dll", EntryPoint = "ImageList_GetImageCount")]
        private static extern int PLV_ImageListGetImageCount(IntPtr hList);

        /*--------------------------------------------------------------------*/
        /* PLV_ImageListGetIcon:                                              */
        /*                                                                    */
        /* comctl32's ImageList_GetIcon: one picture of a native image list   */
        /* made into a new icon, alpha and all.  The caller destroys it.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hList  : the image list's handle.                              */
        /*     iImage : the picture's index, from 0.                          */
        /*     uFlags : how it is drawn into the icon (0, ILD_NORMAL).        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     IntPtr : the new icon, or zero on failure.                     */
        /*--------------------------------------------------------------------*/
        [DllImport("comctl32.dll", EntryPoint = "ImageList_GetIcon")]
        private static extern IntPtr PLV_ImageListGetIcon(IntPtr hList, int iImage, uint uFlags);

        /*--------------------------------------------------------------------*/
        /* PLV_ImageListReplaceIcon:                                          */
        /*                                                                    */
        /* comctl32's ImageList_ReplaceIcon: put an icon into a native image  */
        /* list -- with index -1, APPENDED as a new picture.  The list makes  */
        /* its own copy, so the caller still destroys the icon.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hList  : the image list's handle.                              */
        /*     iImage : the picture to replace, or -1 to append.              */
        /*     hIcon  : the icon.                                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : the picture's index, or -1 on failure.                   */
        /*--------------------------------------------------------------------*/
        [DllImport("comctl32.dll", EntryPoint = "ImageList_ReplaceIcon")]
        private static extern int PLV_ImageListReplaceIcon(IntPtr hList, int iImage, IntPtr hIcon);

        /*--------------------------------------------------------------------*/
        /* PLV_DestroyIcon:                                                   */
        /*                                                                    */
        /* user32's DestroyIcon: free an icon this file made (the box taken   */
        /* out of the list, and the filled one once the list has its copy).   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hIcon : the icon.                                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when it was freed.                                 */
        /*--------------------------------------------------------------------*/
        [DllImport("user32.dll", EntryPoint = "DestroyIcon")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PLV_DestroyIcon(IntPtr hIcon);

        /*--------------------------------------------------------------------*/
        /* PLV_ImageListGetIconSize:                                          */
        /*                                                                    */
        /* comctl32's ImageList_GetIconSize: the width and height of the      */
        /* pictures in a native image list, given its handle.  Used for the   */
        /* list's state image list, which the framework does not expose when  */
        /* the control made it itself.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hList   : the image list's handle.                             */
        /*     iWidth  : receives the pictures' width, pixels.                */
        /*     iHeight : receives their height, pixels.                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the sizes were read.                          */
        /*--------------------------------------------------------------------*/
        [DllImport("comctl32.dll", EntryPoint = "ImageList_GetIconSize")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PLV_ImageListGetIconSize(IntPtr hList, out int iWidth, out int iHeight);

        /*--------------------------------------------------------------------*/
        /* PLV_SendMessage:                                                   */
        /*                                                                    */
        /* user32's SendMessageW: send the list's own window a message and    */
        /* wait for its answer.  Used for the messages whose parameters are   */
        /* plain numbers and which the framework does not expose: the List    */
        /* view's column width and its scroll, the small views' spacing, the  */
        /* state image list's handle, the extended styles and the heading     */
        /* control's handle.                                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hWnd     : the window.                                         */
        /*     iMessage : the message.                                        */
        /*     pWParam  : its first parameter.                                */
        /*     pLParam  : its second parameter.                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     IntPtr : the window's answer (LVM_SETCOLUMNWIDTH: nonzero on   */
        /*              success; LVM_GETITEMSPACING: the two spacings;        */
        /*              LVM_GETIMAGELIST: the list's handle, or zero).        */
        /*--------------------------------------------------------------------*/
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
        private static extern IntPtr PLV_SendMessage(IntPtr hWnd, int iMessage, IntPtr pWParam, IntPtr pLParam);

        /*--------------------------------------------------------------------*/
        /* PLV_SendMessageText:                                               */
        /*                                                                    */
        /* user32's SendMessageW again, imported under a second name for a    */
        /* message whose second parameter is a string (marshalled as a        */
        /* Unicode pointer for the call's duration) -- LVM_GETSTRINGWIDTHW.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hWnd     : the window.                                         */
        /*     iMessage : the message.                                        */
        /*     pWParam  : its first parameter.                                */
        /*     sLParam  : its second parameter, the text.                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     IntPtr : the window's answer (for LVM_GETSTRINGWIDTHW, the     */
        /*              width in pixels).                                     */
        /*--------------------------------------------------------------------*/
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
        private static extern IntPtr PLV_SendMessageText(IntPtr hWnd, int iMessage, IntPtr pWParam, string sLParam);

        /*--------------------------------------------------------------------*/
        /* PLV_SendMessagePoint:                                              */
        /*                                                                    */
        /* user32's SendMessageW a third time, for a message whose second     */
        /* parameter is a POINT (passed by address) -- LVM_SETITEMPOSITION32. */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hWnd     : the window.                                         */
        /*     iMessage : the message.                                        */
        /*     pWParam  : its first parameter (the row's index).              */
        /*     ptLParam : its second parameter, the point.                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     IntPtr : the window's answer (nonzero on success).             */
        /*--------------------------------------------------------------------*/
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
        private static extern IntPtr PLV_SendMessagePoint(IntPtr hWnd, int iMessage, IntPtr pWParam, ref PLV_Point ptLParam);

        /*--------------------------------------------------------------------*/
        /* WndProc:                                                           */
        /*                                                                    */
        /* The list's window procedure.  Every message the list view does not */
        /* need changed is handled by it as usual.  What is added: a          */
        /* double-click off the boxes keeps the ticks as they are; a cut      */
        /* row's name is handed over when the control asks for it; a tick     */
        /* change the program's box pictures do not allow is refused; in      */
        /* Small Icons the framework's own "arrange" is replaced by the       */
        /* program's grid; and the heading control's column-resize and        */
        /* check-box notifications are reported to whoever is listening.      */
        /*                                                                    */
        /* An override because the framework requires it: a control sees its  */
        /* window's raw messages only through this virtual method.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     msgIn : the message; changed by the base class as usual.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the message is handled.                                 */
        /*--------------------------------------------------------------------*/
        protected override void WndProc(ref Message msgIn)
        {
            int  iCode;
            int  iColumn;
            int  iBoxColumn;
            int  iRow;
            uint uiChanged;
            int  iNewImage;
            int  iOldImage;
            bool bToggle;

            /* No notification read yet, and no column to report: -1 means   */
            /* "none" to the reports after the base handling.                */
            iCode      = 0;
            iColumn    = -1;
            iBoxColumn = -1;

            if (msgIn.Msg == PLV_WM_LBUTTONDBLCLK && this.CheckBoxes &&
                (this.HitTest((short)((long)msgIn.LParam & 0xFFFF), (short)(((long)msgIn.LParam >> 16) & 0xFFFF)).Location &
                 ListViewHitTestLocations.StateImage) == 0)
            {   /* A double-click NOT on a box, with boxes on.  The framework   */
                /* toggles the row's tick on any double-click, and the tick     */
                /* moves the selection, so the row double-clicked to open it    */
                /* would be deselected and nothing opened.  A TOGGLE -- an      */
                /* empty                                                        */
                /* box ticked or a ticked one emptied -- is refused while the   */
                /* framework handles it (WndProc's LVN_ITEMCHANGING refusal);   */
                /* the open goes ahead as usual.  Only the toggle: a folder     */
                /* opened by the double-click refills the list INSIDE this      */
                /* message, and its new rows must still get their first box     */
                /* (refusing every change would leave them with none: the       */
                /* folder opens but its rows show no boxes, every new row       */
                /* left at state image 0).                                      */
                PLV_bKeepTicks = true;

                try
                {   /* The framework's own handling, ticks held. */
                    base.WndProc(ref msgIn);
                }
                finally
                {   /* Ticks free again, whatever happened. */
                    PLV_bKeepTicks = false;
                }

                return;
            }

            if (msgIn.Msg == PLV_WM_REFLECT_NOTIFY && msgIn.LParam != IntPtr.Zero &&
                Marshal.ReadInt32(msgIn.LParam, 2 * IntPtr.Size) == PLV_LVN_GETDISPINFOW && PLV_AnswerCutName(msgIn.LParam))
            {   /* The control asked for a cut row's text, and has it. */
                msgIn.Result = IntPtr.Zero;
                return;
            }

            if (msgIn.Msg == PLV_WM_REFLECT_NOTIFY && msgIn.LParam != IntPtr.Zero && this.CheckBoxes && !PLV_bMarking &&
                Marshal.ReadInt32(msgIn.LParam, 2 * IntPtr.Size) == PLV_LVN_ITEMCHANGING)
            {   /* One of the list's own rows is about to change, with our box   */
                /* pictures in use.  NMLISTVIEW after its header: the row, the   */
                /* sub-item, the new state, the old state, what changed.         */
                iRow      = Marshal.ReadInt32(msgIn.LParam, 3 * IntPtr.Size);
                iNewImage = (int)(((uint)Marshal.ReadInt32(msgIn.LParam, 3 * IntPtr.Size + 8) & PLV_LVIS_STATEIMAGEMASK) >> 12);
                iOldImage = (int)(((uint)Marshal.ReadInt32(msgIn.LParam, 3 * IntPtr.Size + 12) & PLV_LVIS_STATEIMAGEMASK) >> 12);
                uiChanged = (uint)Marshal.ReadInt32(msgIn.LParam, 3 * IntPtr.Size + 16);

                /* A tick TOGGLE: from an empty or ticked box to the other.  A   */
                /* row with no box yet (state image 0, a row just added)         */
                /* getting its first one is not a toggle.                        */
                bToggle = (iOldImage == PLV_STATE_UNCHECKED + 1 || iOldImage == PLV_STATE_CHECKED + 1) &&
                          (iNewImage == PLV_STATE_UNCHECKED + 1 || iNewImage == PLV_STATE_CHECKED + 1);

                if (iRow >= 0 && (uiChanged & PLV_LVIF_STATE) != 0 && iNewImage != iOldImage &&
                    ((PLV_bKeepTicks && bToggle) ||
                     iNewImage == PLV_STATE_UNAVAILABLE + 1 || (iOldImage == PLV_STATE_UNAVAILABLE + 1 && iNewImage != 0)))
                {   /* The framework's toggle during a double-click, a box       */
                    /* click or the space bar moving a row INTO the filled box   */
                    /* (the control may step a clicked box through every         */
                    /* picture in the list, ticked to filled), or the filled     */
                    /* box being ticked: refused, and the framework never sees   */
                    /* it.  Taking the boxes away (to no picture) is let         */
                    /* through.                                                  */
                    msgIn.Result = (IntPtr)1;

                    if (!PLV_bKeepTicks && iOldImage == PLV_STATE_CHECKED + 1 && this.IsHandleCreated)
                    {   /* It was a ticked box being clicked: what the user     */
                        /* meant is to untick it, done through the framework    */
                        /* once this notification is over, so the tick events   */
                        /* run.  NOT during a double-click's hold: there        */
                        /* nothing is meant to change, and an untick left       */
                        /* waiting would run in the middle of whatever the      */
                        /* double-click opens.                                  */
                        this.BeginInvoke(new Plv_FnIndex(Plv_UntickRow), new object[] { iRow });
                    }

                    return;
                }
            }

            if (msgIn.Msg == PLV_LVM_ARRANGE && this.View == View.SmallIcon)
            {   /* An "arrange the icons" request in Small Icons view.  The     */
                /* FRAMEWORK sends one of its own (aligned to the top) after    */
                /* rows are added and after EndUpdate, and the control would    */
                /* answer it by re-flowing every row onto its own fixed slots   */
                /* -- undoing PLV_ArrangeGrid's even grid (the rows are         */
                /* placed, then this message moves them back).  In this view    */
                /* the program owns the layout, so the request is not passed    */
                /* on: the program's grid is laid out again instead, once the   */
                /* current message is done, and the request is reported done.   */
                PLV_bGridMoved = true;
                msgIn.Result   = (IntPtr)1;

                if (this.IsHandleCreated)
                {   /* Posted: never laid out from inside another of the control's messages. */
                    this.BeginInvoke(new MethodInvoker(PLV_ArrangeGrid));
                }

                return;
            }

            if (msgIn.Msg == PLV_WM_NOTIFY && msgIn.LParam != IntPtr.Zero)
            {   /* A notification from one of the list's child windows (the   */
                /* heading control among them): read its code.                */
                /* Its data begins with three pointer-sized fields -- the     */
                /* sender's window, its id, and the code, an int padded to    */
                /* pointer size -- so the code is at 2 * IntPtr.Size.         */
                iCode = Marshal.ReadInt32(msgIn.LParam, 2 * IntPtr.Size);

                if (iCode == PLV_HDN_ENDTRACK_A || iCode == PLV_HDN_ENDTRACK_W ||
                    iCode == PLV_HDN_DIVIDERDBLCLK_A || iCode == PLV_HDN_DIVIDERDBLCLK_W)
                {   /* One of the two watched for -- the user let go of a         */
                    /* divider after dragging it, or double-clicked one.  A       */
                    /* heading notification's next field, after the three, is     */
                    /* the column it is about.  (Read now: the data is the        */
                    /* sender's, and is only promised to last while the message   */
                    /* is handled.)                                               */
                    iColumn = Marshal.ReadInt32(msgIn.LParam, 3 * IntPtr.Size);
                }

                if (iCode == PLV_HDN_ITEMSTATEICONCLICK)
                {   /* A heading's check box was clicked: the same field names   */
                    /* the column, read now for the same reason.                 */
                    iBoxColumn = Marshal.ReadInt32(msgIn.LParam, 3 * IntPtr.Size);
                }
            }

            /* Every message the list view handles as usual -- the watched   */
            /* notifications too; the reports below only add to it.          */
            base.WndProc(ref msgIn);

            if (iBoxColumn >= 0 && PLV_pfnHeaderBoxClicked != null && this.IsHandleCreated)
            {   /* Someone is listening for heading-box clicks: told after the   */
                /* list has finished with the notification, as for a resize.     */
                this.BeginInvoke(new Plv_FnIndex(Plv_ReportHeaderBoxClick), new object[] { iBoxColumn });
            }

            if (iColumn < 0 || iColumn >= this.Columns.Count)
            {   /* Not a column resize by the user (or not a column of this list). */
                return;
            }

            if (PLV_pfnUserResized == null || !this.IsHandleCreated)
            {   /* Nobody to tell (or no window to post to). */
                return;
            }

            /* The user resized this column: POSTED to the message loop       */
            /* rather than reported here, so it runs after the list has       */
            /* finished with the notification -- when the column's width is   */
            /* certainly the final one -- and the receiver is free to         */
            /* change column widths itself without doing so from inside the   */
            /* heading control's own message.                                 */
            this.BeginInvoke(new Plv_FnIndex(Plv_ReportUserResized), new object[] { iColumn });
        }

        /*--------------------------------------------------------------------*/
        /* Plv_UntickRow:                                                     */
        /*                                                                    */
        /* WndProc's posted untick: a ticked box the user clicked, whose      */
        /* control-made change was refused, unticked through the framework    */
        /* once that notification is over, so the tick events run.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iRow : the row whose box was clicked.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the row is unticked, or nothing when it has gone or is  */
        /*            no longer ticked.                                       */
        /*--------------------------------------------------------------------*/
        private void Plv_UntickRow(int iRow)
        {
            if (iRow < this.Items.Count && this.Items[iRow].StateImageIndex == PLV_STATE_CHECKED)
            {   /* Still that row, still ticked. */
                this.Items[iRow].Checked = false;
            }
        }

        /*--------------------------------------------------------------------*/
        /* Plv_ReportHeaderBoxClick:                                          */
        /*                                                                    */
        /* WndProc's posted report of a click on a heading's check box, run   */
        /* once the list has finished with the notification.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iColumn : the heading clicked.                                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the listener has been told.                             */
        /*--------------------------------------------------------------------*/
        private void Plv_ReportHeaderBoxClick(int iColumn)
        {
            /* The listener WndProc found set when it posted this. */
            PLV_pfnHeaderBoxClicked(this, iColumn);
        }

        /*--------------------------------------------------------------------*/
        /* Plv_ReportUserResized:                                             */
        /*                                                                    */
        /* WndProc's posted report of a column the user resized, run once     */
        /* the list has finished with the notification, when the width is     */
        /* the final one.                                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iColumn : the column.                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the listener has been told.                             */
        /*--------------------------------------------------------------------*/
        private void Plv_ReportUserResized(int iColumn)
        {
            /* The listener WndProc found set when it posted this. */
            PLV_pfnUserResized(this, iColumn);
        }
    }
}
