/*----------------------------------------------------------------------------*/
/* DiskLayoutForm.cs                                                          */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The partition window behind File > Partition/Format > Image File and       */
/* > Device: the disk drawn as a bar with its partitions in                   */
/* proportion and coloured by what they hold, the same partitions in a list   */
/* beneath, and the commands -- a new table of either kind or none at all     */
/* (one filesystem over the whole card), a partition added into free space    */
/* or deleted, a filesystem made in one, the table written, and a partition   */
/* opened in the main window.  Edits are made to a copy of the table and      */
/* nothing reaches the disk until Write Table, which warns first; until then  */
/* the window says "(modified)" and Revert throws the edits away.             */
/*                                                                            */
/* The window shows the table (PartitionTable.cs) and runs no command of its  */
/* own: it touches no disk, asks no question and makes no edit.  Each button  */
/* with work -- a new table, add, delete, format, a partition's Read /        */
/* Write / Erase, expand, write, refresh -- posts ONE request through its     */
/* host (MainCommand.cs, the partition view) carrying a copy of the table     */
/* and the selection frozen at the press (DSK_PostEdit); Revert, Test, Open   */
/* and Close post theirs through callbacks of their own.  The command worker  */
/* asks every question over this window, makes the edit on the copy or runs   */
/* the disk's work, and what comes back is shown (DSK_ShowEdited,             */
/* DSK_ShowRead and the like) and never starts anything; while work is        */
/* pending the host greys the buttons.                                        */
/*                                                                            */
/* Shown modally, with the layout the host has already read painted before    */
/* it appears.  Open and Close are asked of the worker too: it asks about     */
/* unwritten edits, then closes the window itself, and the host reports the   */
/* window's real return apart from the request.  The two small dialogs the    */
/* worker's questions show over it (CommandQuestionPresenter.cs) -- the       */
/* table kind, and a new partition's place, type and name -- live at the end  */
/* of this file.  All three show their text in the program's dialog font,     */
/* which their callers pass in; none chooses a font or frees the one it is    */
/* given.                                                                     */
/*----------------------------------------------------------------------------*/

/* The framework: the base types (Math, event arguments), the lists of the    */
/* free gaps, the named types and a trial copy of the entries, GDI+ for the   */
/* disk picture's painting and the fonts and sizes, and the window, its       */
/* picture panel and its two dialogs.                                         */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* How the window hands its commands to its host -- the partition view    */
    /* (MainCommand.cs, Pui_Post...), which posts them to the worker.  Each   */
    /* carries values, never work to run, and the window learns the outcome   */
    /* only from what the host shows it afterwards.                           */
    /*------------------------------------------------------------------------*/
    /*------------------------------------------------------------------------*/
    /* DSK_ReadTable:                                                         */
    /*                                                                        */
    /* Read (and Revert): ask for a fresh read of the disk's layout           */
    /* (Pui_PostRead).  The window's rows change only when that read          */
    /* succeeds and comes back through DSK_ShowRead.                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the read is posted.                                         */
    /*------------------------------------------------------------------------*/
    internal delegate void DSK_ReadTable();

    /*------------------------------------------------------------------------*/
    /* DSK_TestCapacity:                                                      */
    /*                                                                        */
    /* Test Capacity: test how much of the device really holds data           */
    /* (Pui_PostTest), always the whole device the window was opened for.     */
    /* The worker asks first, and the test can be cancelled.                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the test is posted.                                         */
    /*------------------------------------------------------------------------*/
    internal delegate void DSK_TestCapacity();

    /*------------------------------------------------------------------------*/
    /* DSK_OpenRange:                                                         */
    /*                                                                        */
    /* Open: the chosen partition (or the whole disk) to be opened in the     */
    /* main window (Pui_PostOpen).  The worker closes this window first, as   */
    /* Close does -- asking about unwritten edits -- and only then opens the  */
    /* range.                                                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     ullOffset : the range's start, bytes.                              */
    /*     ullLength : its length, bytes; 0 for "to the end of the file".     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the request is posted.                                      */
    /*------------------------------------------------------------------------*/
    internal delegate void DSK_OpenRange(ulong ullOffset, ulong ullLength);

    /*------------------------------------------------------------------------*/
    /* DSK_PostEdit -- the host's poster for a button run whole by the        */
    /* worker (Pui_PostEdit): the press's record -- its action, a copy of the */
    /* table and the selection -- goes to the worker as one EditTable         */
    /* request, which asks the button's question and makes its edit on the    */
    /* copy; the edited copy comes back to be shown (DSK_ShowEdited).         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objEdit : the press's record, made for this press.                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the request is posted.                                      */
    /*------------------------------------------------------------------------*/
    internal delegate void DSK_PostEdit(TBE_clsTableEdit objEdit);

    /*------------------------------------------------------------------------*/
    /* DPN_PanelClick -- what the picture reports when clicked: the slot of   */
    /* the partition under the pointer (-1 for none), and the free gap under  */
    /* it when it was free space (null otherwise).                            */
    /*------------------------------------------------------------------------*/
    internal delegate void DPN_PanelClick(int iSlot, PTB_Range objGap);

    /*------------------------------------------------------------------------*/
    /* DPN_clsDiskPanel -- the picture: the whole disk as one bar, the usable */
    /* area outlined inside the reserved sectors at either end (in the same   */
    /* grey -- a cosmetic defect intentionally deferred), each partition a    */
    /* block whose width is its share of the disk (never less than a few      */
    /* pixels, so a quarter-megabyte boot environment on a 32 GB card can     */
    /* still be seen and clicked), coloured by its contents, numbered, and    */
    /* named when there is room; free space white; the selected partition     */
    /* outlined.  Double-buffered and repainted on resize, so dragging the    */
    /* window edge redraws it without flicker.                                */
    /*                                                                        */
    /* Derives from Panel because the framework requires it: a control that   */
    /* draws itself and takes its own clicks is made only as a subclass of a  */
    /* WinForms control, whose OnPaint and OnMouseDown it overrides.  That is */
    /* the one kind of inheritance allowed here -- from a framework base      */
    /* class, never from a class of ours.                                     */
    /*------------------------------------------------------------------------*/
    internal sealed class DPN_clsDiskPanel : Panel
    {
        /* The bar's geometry -- documentation table. */
        private const int DPN_BAR_PAD     = 6;      // pixels between the panel's edge and the bar
        private const int DPN_MIN_WIDTH   = 4;      // the narrowest a partition is drawn, pixels, so a tiny one can be seen and clicked
        private const int DPN_SELECT_PEN  = 3;      // the selection outline's width, pixels
        private const int DPN_TEXT_PAD    = 3;      // pixels between a block's edge and its text

        /* The colours a block takes from its contents -- documentation table. */
        private static readonly Color Dpn_clrExt      = Color.FromArgb(150, 205, 150);   // an ext2 / ext3 / ext4 filesystem
        private static readonly Color Dpn_clrFat      = Color.FromArgb(160, 190, 230);   // FAT, exFAT, NTFS
        private static readonly Color Dpn_clrSwap     = Color.FromArgb(240, 200, 140);   // Linux swap
        private static readonly Color Dpn_clrSquash   = Color.FromArgb(200, 170, 220);   // squashfs
        private static readonly Color Dpn_clrUnknown  = Color.FromArgb(210, 210, 210);   // nothing recognised
        private static readonly Color Dpn_clrFree     = Color.White;                     // free space inside the usable area
        private static readonly Color Dpn_clrReserved = Color.FromArgb(200, 200, 200);   // the sectors a table keeps for itself
        private static readonly Color Dpn_clrEdge     = Color.FromArgb(120, 120, 120);   // every block's outline

        /* What the picture shows, and who is told of a click --          */
        /* documentation table.  The window sets them through the three   */
        /* functions below and then asks for the repaint itself.          */
        private PTB_Table     Dpn_objTable;       // the table drawn; null until the first read
        private string        Dpn_sWholeContent;  // with no table: what the disk's first page holds, for the one block's label
        private int           Dpn_iSelectedSlot;  // the slot outlined, or -1
        private DPN_PanelClick Dpn_pfnClick;      // told what was clicked

        /*--------------------------------------------------------------------*/
        /* DPN_clsDiskPanel (constructor):                                    */
        /*                                                                    */
        /* The panel with its own painting switched on and double-buffered.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the panel, empty until a table is given.       */
        /*--------------------------------------------------------------------*/
        internal DPN_clsDiskPanel()
        {
            /* Nothing to draw yet (the paint shows "reading ..."), nothing   */
            /* selected, and nobody told of clicks until the window says.     */
            Dpn_objTable         = null;
            Dpn_sWholeContent = "";
            Dpn_iSelectedSlot = -1;
            Dpn_pfnClick      = null;

            /* Painted here, in one go, through a back buffer; redrawn whole on a resize. */
            this.SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                          ControlStyles.ResizeRedraw, true);
            this.BackColor = SystemColors.Window;
        }

        /*--------------------------------------------------------------------*/
        /* DPN_SetView:                                                       */
        /*                                                                    */
        /* What to draw: the table, the whole disk's contents when there is   */
        /* no table, and the slot to outline.  The table is the window's own, */
        /* not a copy, so the picture always shows the entries the list does. */
        /* Nothing is repainted here; the window asks for that.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objTable      : the table, or null before the first read.      */
        /*     sWholeContent : with no table, what the disk's start holds.    */
        /*     iSelectedSlot : the slot to outline, or -1.                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the next paint shows them.                              */
        /*--------------------------------------------------------------------*/
        internal void DPN_SetView(PTB_Table objTable, string sWholeContent, int iSelectedSlot)
        {
            /* The three kept together, as one view. */
            Dpn_objTable = objTable;
            Dpn_sWholeContent = sWholeContent;
            Dpn_iSelectedSlot = iSelectedSlot;
        }

        /*--------------------------------------------------------------------*/
        /* DPN_SetSelection:                                                  */
        /*                                                                    */
        /* Move the outline to another slot, the table left as it is.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iSelectedSlot : the slot (0 for the one block of a disk with   */
        /*                     no table), or -1 for none.                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the next paint outlines it.                             */
        /*--------------------------------------------------------------------*/
        internal void DPN_SetSelection(int iSelectedSlot)
        {
            /* Only what is outlined changes: no entry, no disk byte. */
            Dpn_iSelectedSlot = iSelectedSlot;
        }

        /*--------------------------------------------------------------------*/
        /* DPN_BindClick:                                                     */
        /*                                                                    */
        /* Set who is told what a click hit -- the window's one handler.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pfnClick : the handler, or null for none.                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : later clicks are reported to it; setting it reports     */
        /*            nothing.                                                */
        /*--------------------------------------------------------------------*/
        internal void DPN_BindClick(DPN_PanelClick pfnClick)
        {
            /* Kept.  A click only says what it hit; the panel starts   */
            /* nothing on a disk.                                       */
            Dpn_pfnClick = pfnClick;
        }

        /*----------------------------------------------------------------------*/
        /* DPN_ColorFor:                                                        */
        /*                                                                      */
        /* The block colour for a contents text from the probe.                 */
        /*                                                                      */
        /* Arguments:                                                           */
        /*     sContent : the probe's words ("ext4  \"rootfs\"", "FAT32", ...). */
        /*                                                                      */
        /* Returns:                                                             */
        /*     Color : the colour.                                              */
        /*----------------------------------------------------------------------*/
        internal static Color DPN_ColorFor(string sContent)
        {
            if (sContent.StartsWith("ext"))
            {   /* ext2 / ext3 / ext4. */
                return(Dpn_clrExt);
            }

            if (sContent.StartsWith("FAT") || sContent == "exFAT" || sContent == "NTFS")
            {   /* Windows' own. */
                return(Dpn_clrFat);
            }

            if (sContent == "Linux swap")
            {   /* Swap. */
                return(Dpn_clrSwap);
            }

            if (sContent == "squashfs")
            {   /* A read-only image. */
                return(Dpn_clrSquash);
            }

            /* Nothing recognised, or empty. */
            return(Dpn_clrUnknown);
        }

        /*--------------------------------------------------------------------*/
        /* Dpn_BarRect:                                                       */
        /*                                                                    */
        /* Where the bar is drawn: the panel less its padding.                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Rectangle : the bar.                                           */
        /*--------------------------------------------------------------------*/
        private Rectangle Dpn_BarRect()
        {
            /* Inset on every side. */
            return(new Rectangle(DPN_BAR_PAD, DPN_BAR_PAD, this.ClientSize.Width - (2 * DPN_BAR_PAD), this.ClientSize.Height - (2 * DPN_BAR_PAD)));
        }

        /*--------------------------------------------------------------------*/
        /* Dpn_XOf:                                                           */
        /*                                                                    */
        /* The pixel column a sector falls at, by its share of the disk.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     ullLba : the sector.                                           */
        /*     rcBar  : the bar.                                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : the x coordinate.                                        */
        /*--------------------------------------------------------------------*/
        private int Dpn_XOf(ulong ullLba, Rectangle rcBar)
        {
            double dblShare;

            /* The sector's byte position as a fraction of the disk, in      */
            /* floating point, since the share is a fraction.  A disk of 0   */
            /* bytes would divide by zero; nothing here guards it, and       */
            /* whether the window can be opened on one is not established.   */
            dblShare = (double)ullLba * (double)Dpn_objTable.uiSector / (double)Dpn_objTable.ullDiskBytes;

            /* Along the bar. */
            return(rcBar.Left + (int)(dblShare * rcBar.Width));
        }

        /*--------------------------------------------------------------------*/
        /* Dpn_BlockRect:                                                     */
        /*                                                                    */
        /* The block for a run of sectors: from its first sector's column to  */
        /* the column after its last, never narrower than DPN_MIN_WIDTH, and  */
        /* held inside the bar.                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     ullFirst : the first sector.                                   */
        /*     ullLast  : the last sector, inclusive.                         */
        /*     rcBar    : the bar.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Rectangle : the block.                                         */
        /*--------------------------------------------------------------------*/
        private Rectangle Dpn_BlockRect(ulong ullFirst, ulong ullLast, Rectangle rcBar)
        {
            int iLeft;
            int iRight;

            /* From the first sector's column to the column after the last   */
            /* (the last is inclusive, so +1 makes the right edge exact).    */
            iLeft  = Dpn_XOf(ullFirst, rcBar);
            iRight = Dpn_XOf(ullLast + 1, rcBar);

            if (iRight - iLeft < DPN_MIN_WIDTH)
            {   /* Too thin to see: widen it. */
                iRight = iLeft + DPN_MIN_WIDTH;
            }

            if (iRight > rcBar.Right)
            {   /* Widened past the bar's end: held inside it, moving the    */
                /* left edge back instead, so a tiny partition at the very   */
                /* end is still DPN_MIN_WIDTH wide.                          */
                iRight = rcBar.Right;
                iLeft  = Math.Max(rcBar.Left, iRight - DPN_MIN_WIDTH);
            }

            /* The block, the bar's full height. */
            return(new Rectangle(iLeft, rcBar.Top, iRight - iLeft, rcBar.Height));
        }

        /*--------------------------------------------------------------------*/
        /* OnPaint:                                                           */
        /*                                                                    */
        /* The Framework's paint hook for the panel: hands the paint to       */
        /* Dpn_OnPaint below, which carries both the drawing and the base     */
        /* Paint event's call, so listeners are told exactly once -- a base   */
        /* call here as well would notify them twice.                         */
        /*                                                                    */
        /* An override because the framework requires it: a control draws     */
        /* itself only through this virtual method.                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     evPaint : the paint event, with the graphics to draw on.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the panel is drawn.                                     */
        /*--------------------------------------------------------------------*/
        protected override void OnPaint(PaintEventArgs evPaint)
        {
            /* All the drawing, and the base call, are in the helper. */
            Dpn_OnPaint(evPaint);
        }

        /*--------------------------------------------------------------------*/
        /* OnMouseDown:                                                       */
        /*                                                                    */
        /* The Framework's mouse hook for the panel: hands the click to       */
        /* Dpn_OnMouseDown below, which raises the base MouseDown event first */
        /* and then hit-tests the bar.  The selection it reports is only a    */
        /* picture of the table, never a disk operation.                      */
        /*                                                                    */
        /* An override because the framework requires it: a control receives  */
        /* its own mouse presses first only through this virtual method.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     evMouse : the click.                                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window is told, through Dpn_pfnClick.               */
        /*--------------------------------------------------------------------*/
        protected override void OnMouseDown(MouseEventArgs evMouse)
        {
            /* The base event and the hit test, in that order, are in the helper. */
            Dpn_OnMouseDown(evMouse);
        }

        /*--------------------------------------------------------------------*/
        /* Dpn_OnPaint:                                                       */
        /*                                                                    */
        /* Draw the bar: with no table, one block for the whole disk with     */
        /* what its first page holds; with one, the reserved sectors at each  */
        /* end and usable area grey, the free gaps white, every partition     */
        /* its colour with its number and (room permitting) its name or type, */
        /* and the selected one outlined last so the outline is on top.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     evPaint : the paint event, with the graphics to draw on.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the panel is drawn.                                     */
        /*--------------------------------------------------------------------*/
        private void Dpn_OnPaint(PaintEventArgs evPaint)
        {
            Graphics        gfx;
            Rectangle       rcBar;
            Rectangle       rcBlock;
            Rectangle       rcSelected;
            PTB_Range       objRange;
            List<PTB_Range> lstGaps;
            PTB_Entry       objEntry;
            string          sLabel;
            bool            bSelected;
            int             i;

            /* The base paint (the Paint event), then the surface and the   */
            /* bar's rectangle.                                             */
            base.OnPaint(evPaint);
            gfx     = evPaint.Graphics;
            rcBar = Dpn_BarRect();

            if (rcBar.Width < DPN_MIN_WIDTH || rcBar.Height < DPN_MIN_WIDTH)
            {   /* Nothing to draw into. */
                return;
            }

            if (Dpn_objTable == null)
            {   /* Not read yet. */
                Dpn_DrawBlock(gfx, rcBar, Dpn_clrUnknown, "reading ...");
                return;
            }

            if (Dpn_objTable.iKind == PTB_Tables.PTB_KIND_NONE)
            {   /* No table: the whole disk is one thing. */
                if (Dpn_sWholeContent.Length == 0)
                {   /* Nothing recognised at the start. */
                    sLabel = "no partition table";
                }
                else
                {   /* A filesystem over the whole disk. */
                    sLabel = "whole disk: " + Dpn_sWholeContent;
                }

                /* The whole bar one block, coloured by what it holds. */
                Dpn_DrawBlock(gfx, rcBar, DPN_ColorFor(Dpn_sWholeContent), sLabel);

                if (Dpn_iSelectedSlot == 0)
                {   /* The whole disk counts as the one selectable thing. */
                    Dpn_DrawOutline(gfx, rcBar);
                }

                /* Drawn: nothing else is on the bar with no table. */
                return;
            }

            /* The reserved sectors at both ends: the whole bar painted       */
            /* first, then the usable area (PTB_UsableRange) drawn over it    */
            /* as a block of its own -- with its own outline, so the edges    */
            /* where the reserved sectors end show.                           */
            /* The usable area is filled with Dpn_clrReserved, the            */
            /* reserved sectors' own grey, not a lighter colour of its own;   */
            /* only its outline sets it apart.  THE DEFECT EXISTS AND IS      */
            /* INTENTIONALLY DEFERRED: it is cosmetic -- the partitions and   */
            /* free space painted over the area show it all the same.         */
            Dpn_DrawBlock(gfx, rcBar, Dpn_clrReserved, "");
            objRange = new PTB_Range();
            PTB_Tables.PTB_UsableRange(Dpn_objTable, objRange);
            Dpn_DrawBlock(gfx, Dpn_BlockRect(objRange.ullFirstLba, objRange.ullLastLba, rcBar), Dpn_clrReserved, "");

            /* The free gaps, white, over the usable area -- so any of it   */
            /* that is neither a gap nor a partition (any space             */
            /* PTB_FreeGaps leaves out) stays grey.                         */
            lstGaps = new List<PTB_Range>();
            PTB_Tables.PTB_FreeGaps(Dpn_objTable, lstGaps);

            for (i = 0; i < lstGaps.Count; i++)
            {   /* One gap per iteration. */
                Dpn_DrawBlock(gfx, Dpn_BlockRect(lstGaps[i].ullFirstLba, lstGaps[i].ullLastLba, rcBar), Dpn_clrFree, "free");
            }

            /* The partitions, the selected one remembered for its outline. */
            bSelected  = false;
            rcSelected = Rectangle.Empty;

            for (i = 0; i < Dpn_objTable.lstEntries.Count; i++)
            {   /* One partition per iteration, in the table's list order     */
                /* (a later one is drawn over an earlier where they touch):   */
                /* its block, and its label starting with its number, the     */
                /* slot counted from 1.                                       */
                objEntry   = Dpn_objTable.lstEntries[i];
                rcBlock = Dpn_BlockRect(objEntry.ullFirstLba, objEntry.ullLastLba, rcBar);
                sLabel  = (objEntry.iSlot + 1).ToString();

                if (objEntry.sName.Length != 0)
                {   /* A named GPT partition. */
                    sLabel = sLabel + "  " + objEntry.sName;
                }
                else if (objEntry.sContent.Length != 0)
                {   /* No name: what it holds. */
                    sLabel = sLabel + "  " + objEntry.sContent;
                }

                /* Filled by what it holds, whatever its table type says. */
                Dpn_DrawBlock(gfx, rcBlock, DPN_ColorFor(objEntry.sContent), sLabel);

                if (objEntry.iSlot == Dpn_iSelectedSlot)
                {   /* Outlined after everything else. */
                    bSelected  = true;
                    rcSelected = rcBlock;
                }
            }

            if (bSelected)
            {   /* On top: drawn last, so no neighbouring block's fill or   */
                /* edge paints over the outline.                            */
                Dpn_DrawOutline(gfx, rcSelected);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Dpn_DrawBlock:                                                     */
        /*                                                                    */
        /* One block: filled, outlined, and its label drawn inside when the   */
        /* label fits at least its first characters; a block too narrow for   */
        /* any text gets none.                                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx     : the graphics.                                        */
        /*     rcBlock : the block.                                           */
        /*     clrFill : its fill.                                            */
        /*     sLabel  : its text, or "".                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the block is drawn.                                     */
        /*--------------------------------------------------------------------*/
        private void Dpn_DrawBlock(Graphics gfx, Rectangle rcBlock, Color clrFill, string sLabel)
        {
            Rectangle rcText;
            SolidBrush brFill;
            Pen penEdge;

            using (brFill = new SolidBrush(clrFill))
            {   /* The fill. */
                gfx.FillRectangle(brFill, rcBlock);
            }

            using (penEdge = new Pen(Dpn_clrEdge))
            {   /* The outline, inside the block. */
                gfx.DrawRectangle(penEdge, rcBlock.Left, rcBlock.Top, rcBlock.Width - 1, rcBlock.Height - 1);
            }

            /* The text's room: the block less DPN_TEXT_PAD each side, the   */
            /* full height (the text is centred in it).                      */
            rcText = new Rectangle(rcBlock.Left + DPN_TEXT_PAD, rcBlock.Top, rcBlock.Width - (2 * DPN_TEXT_PAD), rcBlock.Height);

            if (sLabel.Length != 0 && rcText.Width >= TextRenderer.MeasureText("8", this.Font).Width)
            {   /* Room for at least one character: the label, cut with an ellipsis. */
                TextRenderer.DrawText(gfx, sLabel, this.Font, rcText, SystemColors.WindowText,
                                      TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Dpn_DrawOutline:                                                   */
        /*                                                                    */
        /* The selection outline round a block, in the highlight colour.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx     : the graphics.                                        */
        /*     rcBlock : the block.                                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the outline is drawn.                                   */
        /*--------------------------------------------------------------------*/
        private void Dpn_DrawOutline(Graphics gfx, Rectangle rcBlock)
        {
            Pen penEdge;

            /* A thick pen in the highlight colour, made for this one   */
            /* outline and released when the block ends.                */
            using (penEdge = new Pen(SystemColors.Highlight, DPN_SELECT_PEN))
            {   /* Drawn inside the block's edge. */
                gfx.DrawRectangle(penEdge, rcBlock.Left + 1, rcBlock.Top + 1, rcBlock.Width - DPN_SELECT_PEN, rcBlock.Height - DPN_SELECT_PEN);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Dpn_OnMouseDown:                                                   */
        /*                                                                    */
        /* A click on the bar: find the partition under the pointer -- by its */
        /* drawn block, so a widened tiny one can be hit -- or else the free  */
        /* gap under it, and tell the window.  With no table the whole disk   */
        /* is the one thing to click.                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     evMouse : the click.                                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window is told, through Dpn_pfnClick.               */
        /*--------------------------------------------------------------------*/
        private void Dpn_OnMouseDown(MouseEventArgs evMouse)
        {
            Rectangle       rcBar;
            List<PTB_Range> lstGaps;
            ulong           ullLba;
            int             i;

            /* The base handling (the MouseDown event) first. */
            base.OnMouseDown(evMouse);

            if (Dpn_objTable == null || Dpn_pfnClick == null)
            {   /* Nothing to click yet. */
                return;
            }

            /* The bar, as the paint placed it. */
            rcBar = Dpn_BarRect();

            if (!rcBar.Contains(evMouse.Location))
            {   /* Off the bar, in the padding round it: no report. */
                return;
            }

            if (Dpn_objTable.iKind == PTB_Tables.PTB_KIND_NONE)
            {   /* The whole disk. */
                Dpn_pfnClick(0, null);
                return;
            }

            for (i = Dpn_objTable.lstEntries.Count - 1; i >= 0; i--)
            {   /* One partition per iteration, the later-drawn first so an overlap favours it. */
                if (Dpn_BlockRect(Dpn_objTable.lstEntries[i].ullFirstLba, Dpn_objTable.lstEntries[i].ullLastLba, rcBar).Contains(evMouse.Location))
                {   /* Hit. */
                    Dpn_pfnClick(Dpn_objTable.lstEntries[i].iSlot, null);
                    return;
                }
            }

            /* Free space, perhaps: the sector under the pointer -- the       */
            /* pointer's share of the bar times the disk's bytes, in          */
            /* sectors, Dpn_XOf the other way round -- then the gap holding   */
            /* it, from the same list the paint drew.                         */
            ullLba  = (ulong)((double)(evMouse.X - rcBar.Left) / (double)rcBar.Width * (double)Dpn_objTable.ullDiskBytes) / Dpn_objTable.uiSector;
            lstGaps = new List<PTB_Range>();
            PTB_Tables.PTB_FreeGaps(Dpn_objTable, lstGaps);

            for (i = 0; i < lstGaps.Count; i++)
            {   /* One gap per iteration. */
                if (ullLba >= lstGaps[i].ullFirstLba && ullLba <= lstGaps[i].ullLastLba)
                {   /* In this gap. */
                    Dpn_pfnClick(-1, lstGaps[i]);
                    return;
                }
            }

            /* The reserved area: nothing selected. */
            Dpn_pfnClick(-1, null);
        }
    }

    /*------------------------------------------------------------------------*/
    /* DSK_clsLayoutForm -- the window.                                       */
    /*                                                                        */
    /* Derives from Form because the framework requires it: WinForms makes a  */
    /* window only as a Form subclass.  That is the one kind of inheritance   */
    /* allowed here -- from a framework base class, never from a class of     */
    /* ours.                                                                  */
    /*------------------------------------------------------------------------*/
    internal sealed class DSK_clsLayoutForm : Form
    {
        /* The window's sizes -- documentation table. */
        private const int DSK_WIDTH          = 860;    // client width, pixels
        private const int DSK_HEIGHT         = 480;    // the preferred first client height, pixels; the window starts taller when the command column, measured at the dialog font, needs more
        private const int DSK_PICTURE_HEIGHT = 56;     // the bar's panel height, pixels
        private const int DSK_PAD            = 12;     // padding round the content, pixels
        private const int DSK_CELL_PAD       = 16;     // added to a column's measured text width, pixels
        private const int DSK_BUTTON_WIDTH   = 150;    // the command buttons' width, pixels (the longest caption with room)

        /* The list's columns, which the row fill indexes. */
        private static readonly string[] Dsk_asColumns = { "#", "Name", "Type", "Contents", "Start (bytes)", "Size (bytes)" };   // the list's headings

        /* The disk and what the host does for the window -- documentation   */
        /* table; all set once, by the constructor.                          */
        private string         Dsk_sName;               // what the window calls the disk
        private ulong          Dsk_ullDiskBytes;        // the disk's length
        private uint           Dsk_uiSector;            // its bytes per sector
        private bool           Dsk_bDevice;             // true for a real device: only then is Test Capacity shown, and enabled (Dsk_UpdateButtons)
        private DSK_ReadTable  Dsk_pfnRead;             // Read / Revert: a fresh read posted; the rows change when it comes back (DSK_ShowRead)
        private DSK_TestCapacity Dsk_pfnTest;           // Test Capacity: posted for the whole device
        private MethodInvoker Dsk_pfnClose;             // Close (and the X): the worker asked to end the window (Pui_PostClose)
        private DSK_OpenRange Dsk_pfnOpen;              // Open: the selected range posted, to open once the window has closed
        private DSK_PostEdit Dsk_pfnEdit;               // every other button with work: its record (action, table copy, selection) posted as one EditTable request, run whole by the worker (Pui_PostEdit)
        private MethodInvoker Dsk_pfnLocalReturned;     // called when one of the window's own message boxes returns: the host looks again, on a later turn, at a question it held back (Pui_PostReady)
        private Button         Dsk_btnExpand;           // Expand Filesystem...
        private Button         Dsk_btnTest;             // Test Capacity...: a device only (hidden for a file)
        private Button         Dsk_btnReadPart;         // Save Partition to File...: the selected partition (or whole disk) read into a new file
        private Button         Dsk_btnWritePart;        // Write File to Partition...: an image file into the selected partition
        private Button         Dsk_btnErasePart;        // Erase Partition...: zeros over the selected partition

        /* The table and the window's state -- documentation table. */
        private PTB_Table      Dsk_objTable;         // the table as edited here, the window's own; null until the host's first read is shown (DSK_ShowRead)
        private string         Dsk_sAccount;         // the read's account line
        private string         Dsk_sWholeContent;    // with no table: what the first page holds
        private ulong          Dsk_ullWholeFsBytes;  // with no table: the ext filesystem's own length from its superblock; 0 when not ext
        private bool           Dsk_bModified;        // edits not yet written
        private bool           Dsk_bCommandPending;  // true while the host has work posted for the window, or its answer is still being shown (DSK_ApplyCommandPending): the buttons are greyed; it decides nothing the worker may do
        private bool           Dsk_bCommandClose;    // true only inside the worker's own Close (DSK_CloseFromCommand), so that close is let through instead of being posted as a request
        private bool           Dsk_bCommandClosed;   // set by Dsk_OnClosing during the worker's own Close: true when that close went ahead (the event not cancelled); DSK_CloseFromCommand clears it first and returns it
        private int            Dsk_iLocalInput;      // how many of the window's own message boxes are open now (one, normally); UI thread only
        private bool           Dsk_bAskKind;         // true to ask the New Table question when the window is first shown (once; Dsk_OnShown clears it)
        private PTB_Range      Dsk_objClickedRange;  // the free gap last clicked in the picture, for Add's default; null for none

        /* The window's controls -- documentation table. */
        private Label           Dsk_lblHead;        // the disk: name, size, sector size; wrapped to its width (Dsk_FitLine)
        private Label           Dsk_lblState;       // the account line, "(modified)", or what is in flight; wrapped to its width (Dsk_FitLine)
        private DPN_clsDiskPanel   Dsk_pnlPicture;  // the bar
        private ListView        Dsk_lstParts;       // the partitions
        private FlowLayoutPanel Dsk_pnlCommands;    // the command buttons, down the right
        private Button          Dsk_btnNewTable;    // New Table...
        private Button          Dsk_btnAdd;         // Add Partition...
        private Button          Dsk_btnDelete;      // Delete Partition
        private Button          Dsk_btnMakeFs;      // Make Filesystem...
        private Button          Dsk_btnWrite;       // Write Table
        private Button          Dsk_btnRevert;      // Revert
        private Button          Dsk_btnRefresh;     // Refresh: read the disk again at any time (asks first when there are unwritten edits)
        private FlowLayoutPanel Dsk_pnlBottom;      // Open and Close, right-aligned
        private Button          Dsk_btnOpen;        // opens the selected partition in the main window
        private Button          Dsk_btnClose;       // closes

        /*--------------------------------------------------------------------*/
        /* DSK_clsLayoutForm (constructor):                                   */
        /*                                                                    */
        /* Build the window, with the host's callbacks for its commands.  It  */
        /* holds no table yet: the host shows the layout it has read          */
        /* (DSK_ShowRead) before showing the window.                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     fntDialog    : the program's dialog font (the caller's; not    */
        /*                    freed here).                                    */
        /*     sName        : what to call the disk (a file name, a model).   */
        /*     ullDiskBytes : its length, bytes.                              */
        /*     uiSector     : its bytes per sector.                           */
        /*     bDevice      : true for a real device.                         */
        /*     bAskKind     : true to ask the New Table question when the     */
        /*                    window is first shown, over the layout already  */
        /*                    read -- New Image's fresh empty file: "no       */
        /*                    table" then goes straight on to Make Filesystem */
        /*                    over the whole file.                            */
        /*     pfnRead      : posts a fresh read of the layout (Revert).      */
        /*     pfnTest      : posts the capacity test of the whole device.    */
        /*     pfnClose     : posts the request to close the window.          */
        /*     pfnOpen      : posts the selected range, to be opened once the */
        /*                    window has closed.                              */
        /*     pfnEdit      : posts every other button with work, run whole   */
        /*                    by the worker with a copy of the table          */
        /*                    (DSK_ShowEdited shows the edited copy that      */
        /*                    comes back).                                    */
        /*     pfnLocalReturned : called when one of the window's own message */
        /*                    boxes returns; it must only POST a later look   */
        /*                    (the caller is still handling the answer),      */
        /*                    never close anything there and then.            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the window, not yet shown.                     */
        /*--------------------------------------------------------------------*/
        internal DSK_clsLayoutForm(Font fntDialog, string sName, ulong ullDiskBytes, uint uiSector, bool bDevice, bool bAskKind,
                                DSK_ReadTable pfnRead, DSK_TestCapacity pfnTest,
                                MethodInvoker pfnClose, DSK_OpenRange pfnOpen,
                                DSK_PostEdit pfnEdit, MethodInvoker pfnLocalReturned)
        {
            int i;
            int iWidest;
            int iHeight;

            /* The disk's facts and the host's callbacks kept, before any    */
            /* control exists; no table until the host shows its read; not   */
            /* modified, no gap clicked, no dialog of its own open, not      */
            /* inside the worker's Close, nothing pending.                   */
            Dsk_sName         = sName;
            Dsk_ullDiskBytes  = ullDiskBytes;
            Dsk_uiSector      = uiSector;
            Dsk_bDevice       = bDevice;
            Dsk_bAskKind      = bAskKind;
            Dsk_pfnRead       = pfnRead;
            Dsk_pfnTest       = pfnTest;
            Dsk_pfnClose      = pfnClose;
            Dsk_pfnOpen       = pfnOpen;
            Dsk_pfnEdit       = pfnEdit;
            Dsk_pfnLocalReturned = pfnLocalReturned;
            Dsk_iLocalInput   = 0;
            Dsk_bCommandClose = false;
            Dsk_bCommandClosed = false;
            Dsk_objTable         = null;
            Dsk_sAccount      = "";
            Dsk_sWholeContent = "";
            Dsk_bModified     = false;
            Dsk_bCommandPending         = false;
            Dsk_objClickedRange  = null;

            /* The caller's dialog font, set before any control is made: the    */
            /* list, the labels and the picture's text inherit it once added,   */
            /* and the command buttons are measured in it.                      */
            this.Font = fntDialog;

            /* A sizable window, unlike the other dialogs -- the bar and the   */
            /* list use the room -- maximisable but not minimisable (it is     */
            /* modal), with no taskbar button, centred on its owner, and no    */
            /* smaller than three quarters of its first size.                  */
            this.Text            = "Partitions of " + sName;
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MaximizeBox     = true;
            this.MinimizeBox     = false;
            this.ShowInTaskbar   = false;
            this.StartPosition   = FormStartPosition.CenterParent;
            this.Padding         = new Padding(DSK_PAD);
            this.MinimumSize     = new Size(DSK_WIDTH * 3 / 4, DSK_HEIGHT * 3 / 4);

            /* Close, sized to its text: it asks the worker to end the       */
            /* window, which asks about unwritten edits before closing it.   */
            Dsk_btnClose          = new Button();
            Dsk_btnClose.Text     = "Close";
            Dsk_btnClose.AutoSize = true;
            Dsk_btnClose.Click   += Dsk_OnCloseClicked;

            /* Open, likewise: the selected partition opened in the main   */
            /* window.                                                     */
            Dsk_btnOpen          = new Button();
            Dsk_btnOpen.Text     = "&Open";
            Dsk_btnOpen.AutoSize = true;
            Dsk_btnOpen.Click   += Dsk_OnOpenClicked;

            /* The bottom row, a pad above it, flowing right to left --   */
            /* Close, added first, at the right edge, Open to its left.   */
            Dsk_pnlBottom               = new FlowLayoutPanel();
            Dsk_pnlBottom.Dock          = DockStyle.Bottom;
            Dsk_pnlBottom.FlowDirection = FlowDirection.RightToLeft;
            Dsk_pnlBottom.AutoSize      = true;
            Dsk_pnlBottom.Padding       = new Padding(0, DSK_PAD, 0, 0);
            Dsk_pnlBottom.Controls.Add(Dsk_btnClose);
            Dsk_pnlBottom.Controls.Add(Dsk_btnOpen);

            /* The commands, down the right, in the order they are shown;   */
            /* each sized to its caption by Dsk_MakeCommand.  Which are     */
            /* usable at any moment is Dsk_UpdateButtons' business.         */
            Dsk_btnNewTable = Dsk_MakeCommand("&New Table...", Dsk_OnNewTableClicked);
            Dsk_btnAdd      = Dsk_MakeCommand("&Add Partition...", Dsk_OnAddClicked);
            Dsk_btnDelete   = Dsk_MakeCommand("&Delete Partition", Dsk_OnDeleteClicked);
            Dsk_btnMakeFs   = Dsk_MakeCommand("&Make Filesystem...", Dsk_OnMakeFsClicked);
            Dsk_btnExpand   = Dsk_MakeCommand("E&xpand Filesystem...", Dsk_OnExpandClicked);
            Dsk_btnReadPart = Dsk_MakeCommand("Save &Partition to File...", Dsk_OnReadPartClicked);
            Dsk_btnWritePart = Dsk_MakeCommand("Writ&e File to Partition...", Dsk_OnWritePartClicked);
            Dsk_btnErasePart = Dsk_MakeCommand("Era&se Partition...", Dsk_OnErasePartClicked);
            Dsk_btnWrite    = Dsk_MakeCommand("&Write Table", Dsk_OnWriteClicked);
            Dsk_btnRevert   = Dsk_MakeCommand("&Revert", Dsk_OnRevertClicked);
            Dsk_btnRefresh  = Dsk_MakeCommand("Re&fresh", Dsk_OnRefreshClicked);
            Dsk_btnTest     = Dsk_MakeCommand("&Test Capacity...", Dsk_OnTestClicked);

            /* Test Capacity is for a real device only: a file holds what it   */
            /* holds, so for one the button is not shown at all rather than    */
            /* left grey for ever.                                             */
            Dsk_btnTest.Visible = bDevice;

            /* All one width, so the column reads as a block whatever the   */
            /* font: the widest of the twelve (the hidden Test button       */
            /* included, so the width does not depend on the disk), then    */
            /* every button given it.                                       */
            iWidest = Math.Max(Math.Max(Math.Max(Dsk_btnNewTable.Width, Dsk_btnAdd.Width), Math.Max(Dsk_btnDelete.Width, Dsk_btnMakeFs.Width)),
                               Math.Max(Math.Max(Dsk_btnWrite.Width, Dsk_btnRevert.Width), Math.Max(Dsk_btnExpand.Width, Dsk_btnRefresh.Width)));
            iWidest = Math.Max(iWidest, Dsk_btnTest.Width);
            iWidest = Math.Max(iWidest, Math.Max(Dsk_btnReadPart.Width, Math.Max(Dsk_btnWritePart.Width, Dsk_btnErasePart.Width)));
            Dsk_btnReadPart.Width  = iWidest;
            Dsk_btnWritePart.Width = iWidest;
            Dsk_btnErasePart.Width = iWidest;
            Dsk_btnNewTable.Width = iWidest;
            Dsk_btnAdd.Width      = iWidest;
            Dsk_btnDelete.Width   = iWidest;
            Dsk_btnMakeFs.Width   = iWidest;
            Dsk_btnExpand.Width   = iWidest;
            Dsk_btnWrite.Width    = iWidest;
            Dsk_btnRevert.Width   = iWidest;
            Dsk_btnRefresh.Width  = iWidest;
            Dsk_btnTest.Width     = iWidest;

            /* The column itself, docked at the right, top to bottom, never   */
            /* wrapped, a pad to its left between it and the list.            */
            Dsk_pnlCommands               = new FlowLayoutPanel();
            Dsk_pnlCommands.Dock          = DockStyle.Right;
            Dsk_pnlCommands.FlowDirection = FlowDirection.TopDown;
            Dsk_pnlCommands.WrapContents  = false;
            Dsk_pnlCommands.AutoSize      = true;
            Dsk_pnlCommands.Padding       = new Padding(DSK_PAD, 0, 0, 0);
            Dsk_pnlCommands.Controls.Add(Dsk_btnNewTable);
            Dsk_pnlCommands.Controls.Add(Dsk_btnAdd);
            Dsk_pnlCommands.Controls.Add(Dsk_btnDelete);
            Dsk_pnlCommands.Controls.Add(Dsk_btnMakeFs);
            Dsk_pnlCommands.Controls.Add(Dsk_btnExpand);
            Dsk_pnlCommands.Controls.Add(Dsk_btnReadPart);
            Dsk_pnlCommands.Controls.Add(Dsk_btnWritePart);
            Dsk_pnlCommands.Controls.Add(Dsk_btnErasePart);
            Dsk_pnlCommands.Controls.Add(Dsk_btnWrite);
            Dsk_pnlCommands.Controls.Add(Dsk_btnRevert);
            Dsk_pnlCommands.Controls.Add(Dsk_btnRefresh);
            Dsk_pnlCommands.Controls.Add(Dsk_btnTest);

            /* The list of partitions, filling what is left: details view,   */
            /* whole rows, one at a time, the selection kept visible.        */
            Dsk_lstParts               = new ListView();
            Dsk_lstParts.Dock          = DockStyle.Fill;
            Dsk_lstParts.View          = View.Details;
            Dsk_lstParts.FullRowSelect = true;
            Dsk_lstParts.MultiSelect   = false;
            Dsk_lstParts.HideSelection = false;

            for (i = 0; i < Dsk_asColumns.Length; i++)
            {   /* One column heading per iteration. */
                Dsk_lstParts.Columns.Add(Dsk_asColumns[i]);
            }

            /* Start and Size right-aligned so their digits line up; a   */
            /* selection updates the picture and the buttons; a row      */
            /* double-clicked (or Enter on it) is Open: the list's own   */
            /* handler calls what the button's does (Dsk_PostOpen).      */
            Dsk_lstParts.Columns[4].TextAlign = HorizontalAlignment.Right;
            Dsk_lstParts.Columns[5].TextAlign = HorizontalAlignment.Right;
            Dsk_lstParts.SelectedIndexChanged += Dsk_OnRowSelected;
            Dsk_lstParts.ItemActivate         += Dsk_OnRowActivated;

            /* The picture, above the list. */
            Dsk_pnlPicture              = new DPN_clsDiskPanel();
            Dsk_pnlPicture.Dock         = DockStyle.Top;
            Dsk_pnlPicture.Height       = DSK_PICTURE_HEIGHT;
            Dsk_pnlPicture.DPN_BindClick(Dsk_OnPictureClicked);

            /* The two lines above the picture: the state line (the read's     */
            /* account, "(modified)", or what is in flight) ...  Its text      */
            /* is shown as written: no "&" in it is taken for an access key,   */
            /* so a disk name holding one keeps it.  Its height follows its    */
            /* width and its text (Dsk_FitLine), so a long account -- the      */
            /* reason a table cannot be written back runs to two or three      */
            /* lines -- wraps inside the space left of the command column      */
            /* instead of being cut off there.                                 */
            Dsk_lblState              = new Label();
            Dsk_lblState.Dock         = DockStyle.Top;
            Dsk_lblState.AutoSize     = false;
            Dsk_lblState.UseMnemonic  = false;
            Dsk_lblState.TextAlign    = ContentAlignment.MiddleLeft;
            Dsk_lblState.Text         = "Reading the partition table ...";
            Dsk_lblState.SizeChanged += Dsk_OnStateSized;

            /* ... and the head line above it: the disk's name, its size in   */
            /* the program's one written form, and its sector size -- shown   */
            /* as written and wrapped the same way, since a device's model    */
            /* or a file's path can be long.                                  */
            Dsk_lblHead              = new Label();
            Dsk_lblHead.Dock         = DockStyle.Top;
            Dsk_lblHead.AutoSize     = false;
            Dsk_lblHead.UseMnemonic  = false;
            Dsk_lblHead.TextAlign    = ContentAlignment.MiddleLeft;
            Dsk_lblHead.Text         = sName + ": " + DVF_clsDeviceForm.DVF_SizeText(ullDiskBytes) + ", " + uiSector + "-byte sectors";
            Dsk_lblHead.SizeChanged += Dsk_OnHeadSized;

            /* Docking runs in reverse order of adding (as found): the     */
            /* bottom row and the command column take their edges first,   */
            /* then the head line, the state line and the picture stack    */
            /* down from the top, and the list, added first, fills the     */
            /* rest.                                                       */
            this.Controls.Add(Dsk_lstParts);
            this.Controls.Add(Dsk_pnlPicture);
            this.Controls.Add(Dsk_lblState);
            this.Controls.Add(Dsk_lblHead);
            this.Controls.Add(Dsk_pnlCommands);
            this.Controls.Add(Dsk_pnlBottom);

            /* The two lines' heights are measured only now, once they have     */
            /* their parent and so its larger font: measured before, they       */
            /* would be the small default's height and clip the text.  The      */
            /* width they are measured at is not final until the window has     */
            /* its size below; each line's SizeChanged measures it again then   */
            /* (Dsk_OnStateSized, Dsk_OnHeadSized).                             */
            Dsk_FitLine(Dsk_lblState);
            Dsk_FitLine(Dsk_lblHead);

            /* The first height: DSK_HEIGHT, unless the command column --       */
            /* docked down the right side above the bottom row -- needs more.   */
            /* Its height is MEASURED from the buttons at the dialog font,      */
            /* never assumed, so a larger font or another button grows the      */
            /* window instead of hiding the column's last buttons.  The         */
            /* window's own padding -- above the column and below the bottom    */
            /* row -- is part of the sum, as the two small dialogs below add    */
            /* theirs: left out, the bottom row covers the column's last        */
            /* button.                                                          */
            iHeight = Math.Max(DSK_HEIGHT, this.Padding.Vertical + Dsk_pnlCommands.PreferredSize.Height + Dsk_pnlBottom.PreferredSize.Height);

            /* Escape is Close (there is no AcceptButton: Enter on a row is   */
            /* the list's Open).                                              */
            this.CancelButton = Dsk_btnClose;

            /* Close given back NO dialog result.  Made the CancelButton, a     */
            /* button with none is given Cancel by the framework (as found,     */
            /* .NET Framework 4.x), and in a modal window a button with a       */
            /* result also closes the window when clicked.  That close would    */
            /* reach Dsk_OnClosing and post the close request a second time,    */
            /* beside the one Dsk_OnCloseClicked posts; the second arrives      */
            /* while the first is still running and is refused as busy, and     */
            /* the refusal ("An operation is in progress") is left on the       */
            /* main window's status line with nothing running.  With no         */
            /* result a click -- and Escape, which clicks the CancelButton --   */
            /* only posts, once, and the worker closes the window.              */
            Dsk_btnClose.DialogResult = DialogResult.None;

            /* The first size; then the handlers: a resize refits the          */
            /* columns, Shown asks the New Table question when asked to, and   */
            /* a close from the user becomes a request to the worker.          */
            this.ClientSize   = new Size(DSK_WIDTH, iHeight);
            this.Resize      += Dsk_OnResize;
            this.Shown       += Dsk_OnShown;
            this.FormClosing += Dsk_OnClosing;

            /* The buttons as they must be before any table: nothing to   */
            /* act on yet.                                                */
            Dsk_UpdateButtons();
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_MakeCommand:                                                   */
        /*                                                                    */
        /* One command button of the right-hand column, sized to its caption  */
        /* in the window's font and never narrower than the design width; the */
        /* constructor evens them out so the column reads as a block.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sText    : the caption, with its access key.                   */
        /*     pfnClick : the handler.                                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Button : the button, not yet added to a panel.                 */
        /*--------------------------------------------------------------------*/
        private Button Dsk_MakeCommand(string sText, EventHandler pfnClick)
        {
            Button btn;

            /* The button with its caption (the & marks its access key). */
            btn        = new Button();
            btn.Text   = sText;

            /* Measured in the WINDOW's font: the button has no parent yet, so   */
            /* its own Font is still the small default.  At least the design     */
            /* width, wider when the caption needs it at the larger size; the    */
            /* constructor then evens the column out to the widest.              */
            btn.Width  = Math.Max(DSK_BUTTON_WIDTH, TextRenderer.MeasureText(sText, this.Font).Width + (2 * DSK_CELL_PAD));
            btn.Height = TextRenderer.MeasureText(sText, this.Font).Height + DSK_CELL_PAD;
            btn.Margin = new Padding(0, 0, 0, DSK_PAD / 2);
            btn.Click += pfnClick;

            /* Ready to add. */
            return(btn);
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_OnShown:                                                       */
        /*                                                                    */
        /* The window has appeared: when the host asked for it (a fresh empty */
        /* image file from New Image), the New Table question is posted,      */
        /* once, for the worker to ask over the layout already read           */
        /* (Dsk_PostEdit).  Nothing is asked here.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the window (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the question posted, or nothing.                        */
        /*--------------------------------------------------------------------*/
        private void Dsk_OnShown(object objSender, EventArgs evArgs)
        {
            if (Dsk_bAskKind)
            {   /* Asked for: cleared first, so it is posted once.  No read   */
                /* is needed -- the host's is already shown.                  */
                Dsk_bAskKind = false;
                Dsk_PostEdit(PAC_enmPartitionAction.AskKind);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_Reload:                                                        */
        /*                                                                    */
        /* Ask for the layout to be read again (Read, Revert, Refresh).  The  */
        /* rows and any edits stay as they are until the read succeeds and    */
        /* comes back through DSK_ShowRead.                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the read is posted.                                     */
        /*--------------------------------------------------------------------*/
        private void Dsk_Reload()
        {
            /* The host posts it; the window reads nothing itself. */
            Dsk_pfnRead();
        }

        /*--------------------------------------------------------------------*/
        /* DSK_ShowRead:                                                      */
        /*                                                                    */
        /* A read of the layout has succeeded: its table becomes the one the  */
        /* window shows and edits, replacing any unwritten edits.  The host   */
        /* calls it only for a successful read (MainCommand.cs).              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objTable        : the table read, handed over to the window    */
        /*                       (the worker is done with it).                */
        /*     sAccount        : the parser's one-line account of it.         */
        /*     sWholeContent   : with no table, what the disk's start holds.  */
        /*     ullWholeFsBytes : with no table, an ext filesystem's own       */
        /*                       length, bytes; 0 when it is not ext.         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window shows the table, unmodified.                 */
        /*--------------------------------------------------------------------*/
        internal void DSK_ShowRead(PTB_Table objTable, string sAccount, string sWholeContent, ulong ullWholeFsBytes)
        {
            /* The table taken as it is, not copied; the facts with it; not   */
            /* modified, and the gap last clicked forgotten (it was the old   */
            /* table's); then everything shown again.                         */
            Dsk_objTable = objTable;
            Dsk_sAccount = sAccount;
            Dsk_sWholeContent = sWholeContent;
            Dsk_ullWholeFsBytes = ullWholeFsBytes;
            Dsk_bModified = false;
            Dsk_objClickedRange = null;
            Dsk_Refresh();
        }

        /*--------------------------------------------------------------------*/
        /* DSK_ApplyCommandPending:                                           */
        /*                                                                    */
        /* The host's word that work it posted for the window is under way    */
        /* (or that its answer is still being shown), or that it is over:     */
        /* the buttons are greyed or not, and the state line says so.  It     */
        /* only greys; whether work may run is the worker's decision.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bPending : true while the work, or its answer, is unsettled.   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the buttons and the state line follow it.               */
        /*--------------------------------------------------------------------*/
        internal void DSK_ApplyCommandPending(bool bPending)
        {
            /* Kept greyed until the answer has been SHOWN, not only until   */
            /* the work ends: otherwise an edit made in between could be     */
            /* replaced by the older result arriving after it.               */
            Dsk_bCommandPending = bPending;
            if (bPending)
            {   /* Under way. */
                Dsk_lblState.Text = "Working ...";
            }
            else if (Dsk_bModified)
            {   /* Over, with edits still unwritten: said so. */
                Dsk_lblState.Text = Dsk_KindText() + " (modified -- nothing is written until Write Table)";
            }
            else
            {   /* Over, nothing unwritten: the read's own account. */
                Dsk_lblState.Text = Dsk_sAccount;
            }

            /* The state line's height follows its new text: "Working ..."   */
            /* is one line, an account may be several.                       */
            Dsk_FitLine(Dsk_lblState);

            /* The buttons, from the new state; the rows are not rebuilt. */
            Dsk_UpdateButtons();
        }

        /*--------------------------------------------------------------------*/
        /* DSK_IsReadyForLifecycle:                                           */
        /*                                                                    */
        /* Whether none of the window's own message boxes is open (the        */
        /* discard question, a failed write's report): the host asks before   */
        /* it shows a question about the window or                            */
        /* closes it, so neither happens on top of one.                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when none is open.                                 */
        /*--------------------------------------------------------------------*/
        internal bool DSK_IsReadyForLifecycle()
        {
            /* The host waits on its own for an answer still being shown. */
            return(Dsk_iLocalInput == 0);
        }

        /*---------------------------------------------------------------------*/
        /* DSK_ConfirmClose:                                                   */
        /*                                                                     */
        /* The discard question, for the host to ask before the worker closes  */
        /* the window (Pui_PresentPending, MainCommand.cs).  It closes         */
        /* nothing itself.                                                     */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     None.                                                           */
        /*                                                                     */
        /* Returns:                                                            */
        /*     bool : true when nothing is unwritten or the user said discard; */
        /*            false to keep the window and its edits.                  */
        /*---------------------------------------------------------------------*/
        internal bool DSK_ConfirmClose()
        {
            if (!Dsk_bModified)
            {   /* Nothing unwritten: nothing to lose, no question. */
                return(true);
            }

            /* Asked, No the default.  Only Yes lets the edits go; asking   */
            /* changes nothing in the table.                                */
            return(Dsk_ShowLocalMessage("The partition table was changed and not written. Discard the changes?",
                "Partitions", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes);
        }

        /*--------------------------------------------------------------------*/
        /* DSK_CloseFromCommand:                                              */
        /*                                                                    */
        /* The worker's own Close (PUI_Close, MainCommand.cs), sent once the  */
        /* discard question has been answered: a close for real, not turned   */
        /* into a request, and the caller learns whether it went ahead from   */
        /* the answer -- the window's one FormClosing handler (Dsk_OnClosing) */
        /* records it, so the host hooks no handler of its own on the event.  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the close went ahead; false when the window   */
        /*            never began to close or the close was cancelled.        */
        /*--------------------------------------------------------------------*/
        internal bool DSK_CloseFromCommand()
        {
            /* Marked as the worker's, so Dsk_OnClosing lets it through       */
            /* rather than turning it into a request; the mark lets nothing   */
            /* else happen.  No close seen yet: a Close that raises no        */
            /* FormClosing at all reads as not closed.                        */
            Dsk_bCommandClose  = true;
            Dsk_bCommandClosed = false;
            try
            {   /* The close.  The discard question was answered before the   */
                /* worker sent this.                                          */
                this.Close();
            }
            finally
            {   /* Cleared whatever happened, so a later X from the user is   */
                /* again a request -- even if this close was refused.  A      */
                /* throw goes on to the caller, which records it.             */
                Dsk_bCommandClose = false;
            }

            /* Whether the close went ahead, as Dsk_OnClosing saw it. */
            return(Dsk_bCommandClosed);
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_ShowLocalMessage:                                              */
        /*                                                                    */
        /* One of the window's own message boxes (MessageForm.cs), counted    */
        /* while it is open so the host shows no question about the window    */
        /* over it (DSK_IsReadyForLifecycle), and the host told when it       */
        /* returns.                                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sText      : the message.                                      */
        /*     sCaption   : its caption.                                      */
        /*     enmButtons : its buttons.                                      */
        /*     enmIcon    : its icon.                                         */
        /*     enmDefault : its default button.                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     DialogResult : the button pressed.                             */
        /*--------------------------------------------------------------------*/
        private DialogResult Dsk_ShowLocalMessage(string sText, string sCaption,
            MessageBoxButtons enmButtons, MessageBoxIcon enmIcon, MessageBoxDefaultButton enmDefault)
        {
            /* Counted from here to the finally (UI thread only). */
            Dsk_iLocalInput++;
            try
            {   /* The program's own message box over this window, in its   */
                /* font; the answer handed back as it is.                   */
                return(MSG_clsMessageForm.MSG_Show(this, this.Font, sText, sCaption,
                    enmButtons, enmIcon, enmDefault));
            }
            finally
            {   /* Counted down, and the host told.  The host only POSTS a   */
                /* later look, so nothing closes while the caller is still   */
                /* handling the answer.                                      */
                Dsk_iLocalInput--;
                Dsk_pfnLocalReturned();
            }
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_Refresh:                                                       */
        /*                                                                    */
        /* Show the table as it now is: the state line, the picture, the      */
        /* list (keeping the selected slot where it survives), the buttons.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : every part of the window agrees with the table.         */
        /*--------------------------------------------------------------------*/
        private void Dsk_Refresh()
        {
            int iSelected;

            /* The slot selected now, to select again after the refill. */
            iSelected = Dsk_SelectedSlot();

            if (Dsk_bModified)
            {   /* Edits not yet on the disk. */
                Dsk_lblState.Text = Dsk_KindText() + " (modified -- nothing is written until Write Table)";
            }
            else
            {   /* As read. */
                Dsk_lblState.Text = Dsk_sAccount;
            }

            /* The state line's height follows its new text. */
            Dsk_FitLine(Dsk_lblState);

            /* The list refilled; then the picture given the same table and   */
            /* the selection AS THE REFILL LEFT IT (a slot that no longer     */
            /* exists is not selected), and repainted; then the buttons.      */
            Dsk_FillList(iSelected);
            Dsk_pnlPicture.DPN_SetView(Dsk_objTable, Dsk_sWholeContent, Dsk_SelectedSlot());
            Dsk_pnlPicture.Invalidate();
            Dsk_UpdateButtons();
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_KindText:                                                      */
        /*                                                                    */
        /* The table's kind in words, for the state line.                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : "GUID partition table", "MBR partition table", or     */
        /*              "No partition table".                                 */
        /*--------------------------------------------------------------------*/
        private string Dsk_KindText()
        {
            if (Dsk_objTable.iKind == PTB_Tables.PTB_KIND_GPT)
            {   /* GPT. */
                return("GUID partition table: " + Dsk_objTable.lstEntries.Count + " partition(s)");
            }

            if (Dsk_objTable.iKind == PTB_Tables.PTB_KIND_MBR)
            {   /* MBR. */
                return("MBR partition table: " + Dsk_objTable.lstEntries.Count + " primary partition(s)");
            }

            /* None. */
            return("No partition table: the whole disk is one filesystem");
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_FillList:                                                      */
        /*                                                                    */
        /* The list's rows from the table, one per partition in slot order,   */
        /* reselecting the slot given when it is still there.  Each row's Tag */
        /* is its entry.  With NO table the list is not left empty: it gets   */
        /* one row for the whole disk -- its name, what its first page holds, */
        /* start 0 and its length -- so the list says what the picture's one  */
        /* block is, and a selected whole disk still has its facts shown.     */
        /* That row's Tag is null, since there is no entry behind it, and it  */
        /* stands for slot 0, the picture's whole-disk slot.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iSelectSlot : the slot to select again (0 for the whole-disk   */
        /*                   row), or -1.                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the list shows the table.                               */
        /*--------------------------------------------------------------------*/
        private void Dsk_FillList(int iSelectSlot)
        {
            ListViewItem lviRow;
            PTB_Entry    objEntry;
            string       sName;
            string       sType;
            int          i;

            /* The selection handler unhooked for the refill -- the clear    */
            /* and the reselect would otherwise each run it, mid-refill --   */
            /* and the rows cleared.                                         */
            Dsk_lstParts.SelectedIndexChanged -= Dsk_OnRowSelected;
            Dsk_lstParts.Items.Clear();

            if (Dsk_objTable != null)
            {   /* Rows to show. */
                for (i = 0; i < Dsk_objTable.lstEntries.Count; i++)
                {   /* One partition per iteration, in the list's order. */
                    objEntry = Dsk_objTable.lstEntries[i];

                    if (Dsk_objTable.iKind == PTB_Tables.PTB_KIND_GPT)
                    {   /* The GPT's name and type words. */
                        sName = objEntry.sName;
                        sType = PTB_Tables.PTB_GptTypeText(objEntry.guidType);
                    }
                    else
                    {   /* The MBR's active flag stands where a name would. */
                        sType = PTB_Tables.PTB_MbrTypeText(objEntry.bType);
                        sName = "";

                        if (objEntry.bBoot)
                        {   /* Marked active. */
                            sName = "(active)";
                        }
                    }

                    /* The row: the slot counted from 1, the entry in the   */
                    /* Tag, then name, type, contents, and start and size   */
                    /* in bytes from the LBAs (the last LBA inclusive).     */
                    lviRow     = new ListViewItem((objEntry.iSlot + 1).ToString());
                    lviRow.Tag = objEntry;
                    lviRow.SubItems.Add(sName);
                    lviRow.SubItems.Add(sType);
                    lviRow.SubItems.Add(objEntry.sContent);
                    lviRow.SubItems.Add((objEntry.ullFirstLba * Dsk_objTable.uiSector).ToString("N0"));
                    lviRow.SubItems.Add(((objEntry.ullLastLba - objEntry.ullFirstLba + 1) * Dsk_objTable.uiSector).ToString("N0"));
                    Dsk_lstParts.Items.Add(lviRow);

                    if (objEntry.iSlot == iSelectSlot)
                    {   /* The slot selected before the refill. */
                        lviRow.Selected = true;
                    }
                }

                if (Dsk_objTable.iKind == PTB_Tables.PTB_KIND_NONE)
                {   /* No table: the one row for the whole disk, with a null     */
                    /* Tag, "-" for its number, the disk's name, "Whole disk",   */
                    /* what its first page holds, and start 0.                   */
                    lviRow     = new ListViewItem("-");
                    lviRow.Tag = null;
                    lviRow.SubItems.Add(Dsk_sName);
                    lviRow.SubItems.Add("Whole disk");
                    lviRow.SubItems.Add(Dsk_sWholeContent);
                    lviRow.SubItems.Add("0");

                    if (Dsk_ullWholeFsBytes != 0)
                    {   /* An ext filesystem: ITS size, which may be less than   */
                        /* the disk -- the filesystem's own length, not the      */
                        /* device's.                                             */
                        lviRow.SubItems.Add(Dsk_ullWholeFsBytes.ToString("N0"));
                    }
                    else
                    {   /* Anything else: the disk's length, the most it can be. */
                        lviRow.SubItems.Add(Dsk_ullDiskBytes.ToString("N0"));
                    }

                    /* Listed. */
                    Dsk_lstParts.Items.Add(lviRow);

                    if (iSelectSlot == 0)
                    {   /* The whole disk was selected before the refill. */
                        lviRow.Selected = true;
                    }
                }
            }

            /* The handler hooked again for the user's own selections, and   */
            /* the columns refitted to the rows now listed.                  */
            Dsk_lstParts.SelectedIndexChanged += Dsk_OnRowSelected;
            Dsk_FitColumns();
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_FitColumns:                                                    */
        /*                                                                    */
        /* Size every column to its content, the Name column taking what is   */
        /* left so the list is filled edge to edge.                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the columns fit the list's width.                       */
        /*--------------------------------------------------------------------*/
        private void Dsk_FitColumns()
        {
            int iColumn;
            int iRow;
            int iWidest;
            int iOthers;

            /* The width every column but Name takes, added up. */
            iOthers = 0;

            for (iColumn = 0; iColumn < Dsk_lstParts.Columns.Count; iColumn++)
            {   /* One column per iteration: the widest of its heading and its cells. */
                iWidest = TextRenderer.MeasureText(Dsk_lstParts.Columns[iColumn].Text, Dsk_lstParts.Font).Width;

                for (iRow = 0; iRow < Dsk_lstParts.Items.Count; iRow++)
                {   /* One row per iteration. */
                    iWidest = Math.Max(iWidest, TextRenderer.MeasureText(Dsk_lstParts.Items[iRow].SubItems[iColumn].Text, Dsk_lstParts.Font).Width);
                }

                /* The widest text plus the cell's margins. */
                Dsk_lstParts.Columns[iColumn].Width = iWidest + DSK_CELL_PAD;

                if (iColumn != 1)
                {   /* Everything but the Name column keeps its measured width. */
                    iOthers = iOthers + Dsk_lstParts.Columns[iColumn].Width;
                }
            }

            /* The Name column takes the rest -- less a vertical scroll       */
            /* bar's width, kept whether or not one shows -- but never less   */
            /* than it measured.                                              */
            Dsk_lstParts.Columns[1].Width = Math.Max(Dsk_lstParts.Columns[1].Width,
                                                     Dsk_lstParts.ClientSize.Width - iOthers - SystemInformation.VerticalScrollBarWidth);
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_SelectedEntry:                                                 */
        /*                                                                    */
        /* The partition selected in the list, or null.  With no table the    */
        /* list's one row is the whole disk, which has no entry: null too.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     PTB_Entry : the entry, or null.                                */
        /*--------------------------------------------------------------------*/
        private PTB_Entry Dsk_SelectedEntry()
        {
            if (Dsk_lstParts.SelectedItems.Count == 0)
            {   /* Nothing picked. */
                return(null);
            }

            /* The row's entry (null for the whole-disk row). */
            return((PTB_Entry)Dsk_lstParts.SelectedItems[0].Tag);
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_SelectedSlot:                                                  */
        /*                                                                    */
        /* The selected partition's slot, or -1 for nothing selected.  The    */
        /* whole-disk row of a table-less disk is slot 0, the slot the        */
        /* picture draws the whole-disk block as selected under.              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : the slot (0 for the whole-disk row), or -1.              */
        /*--------------------------------------------------------------------*/
        private int Dsk_SelectedSlot()
        {
            PTB_Entry objEntry;

            if (Dsk_lstParts.SelectedItems.Count == 0)
            {   /* Nothing picked. */
                return(-1);
            }

            /* The row's entry, if it has one. */
            objEntry = Dsk_SelectedEntry();

            if (objEntry == null)
            {   /* A row with no entry: the whole-disk row of a table-less disk. */
                return(0);
            }

            /* Its slot. */
            return(objEntry.iSlot);
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_TargetHoldsExt:                                                */
        /*                                                                    */
        /* Whether the place Expand would act on holds an ext2, ext3 or ext4  */
        /* filesystem, by the probe's words: the selected partition's         */
        /* contents, or with no table what the whole disk's start holds.      */
        /* It is the test the worker's Expand (Sys_TableEditSequence) makes   */
        /* before it does anything,                                           */
        /* so the button is offered exactly when that test would pass; the    */
        /* two must change together.                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the target's words start with "ext"; false    */
        /*            for any other contents, with nothing selected on a      */
        /*            table, or before the first read.                        */
        /*--------------------------------------------------------------------*/
        private bool Dsk_TargetHoldsExt()
        {
            PTB_Entry objEntry;
            string    sContent;

            if (Dsk_objTable == null)
            {   /* Not read yet: no target at all. */
                return(false);
            }

            /* The partition picked in the list, if any (null for nothing   */
            /* picked, and for the whole-disk row of a table-less disk).    */
            objEntry = Dsk_SelectedEntry();

            if (objEntry != null)
            {   /* A partition: what the probe found in it. */
                sContent = objEntry.sContent;
            }
            else if (Dsk_objTable.iKind == PTB_Tables.PTB_KIND_NONE)
            {   /* No table: what the disk's own start holds. */
                sContent = Dsk_sWholeContent;
            }
            else
            {   /* A table and nothing picked: nothing to expand. */
                return(false);
            }

            /* The probe names ext2, ext3 and ext4 with words starting    */
            /* "ext", and nothing else does; letter case is ignored, as   */
            /* the worker's Expand ignores it.                            */
            return(sContent.StartsWith("ext", StringComparison.OrdinalIgnoreCase));
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_UpdateButtons:                                                 */
        /*                                                                    */
        /* Enable each button by what is selected, what is edited, and        */
        /* whether the host has work pending (then all are greyed).  This     */
        /* only spares the user a click that would do nothing: the worker     */
        /* still decides what may run.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the buttons are enabled or not.                         */
        /*--------------------------------------------------------------------*/
        private void Dsk_UpdateButtons()
        {
            List<PTB_Range> lstGaps;
            bool            bHaveTable;
            bool            bTarget;

            /* A table to act on: read, and no work pending.  Then whether    */
            /* there is a target -- a selected partition, or the whole disk   */
            /* when there is no table.                                        */
            bHaveTable = (!Dsk_bCommandPending && Dsk_objTable != null);
            bTarget    = false;

            if (bHaveTable)
            {   /* Something to format or open: a partition, or the whole disk. */
                bTarget = (Dsk_SelectedEntry() != null || Dsk_objTable.iKind == PTB_Tables.PTB_KIND_NONE);
            }

            /* Each command's rule.  No table command before the first read    */
            /* is shown.  New Table needs only a table; Delete a selected      */
            /* partition.  Make Filesystem, Expand, Open and the partition's   */
            /* Read / Write / Erase need a target and the table ON THE DISK    */
            /* to be this one (not modified): each works where the disk's      */
            /* table says the partition is, so an unwritten edit would point   */
            /* it at the wrong place.  Expand needs an ext filesystem in the   */
            /* target too, the only kind it grows, so a FAT or exFAT           */
            /* partition does not offer a command that would only refuse it    */
            /* (the worker's own Expand test, which stays, says why when the   */
            /* button is used anyway).  Write and Revert need edits.           */
            /* Refresh and Close need only that nothing is pending, so a       */
            /* read that failed can be tried again; so does Test Capacity (a   */
            /* device only), which touches no table.  Add starts off and is    */
            /* decided below.                                                  */
            Dsk_btnNewTable.Enabled = bHaveTable;
            Dsk_btnDelete.Enabled   = bHaveTable && Dsk_SelectedEntry() != null;
            Dsk_btnMakeFs.Enabled   = bHaveTable && bTarget && !Dsk_bModified;
            Dsk_btnExpand.Enabled   = bHaveTable && bTarget && !Dsk_bModified && Dsk_TargetHoldsExt();
            Dsk_btnReadPart.Enabled  = bHaveTable && bTarget && !Dsk_bModified;
            Dsk_btnWritePart.Enabled = bHaveTable && bTarget && !Dsk_bModified;
            Dsk_btnErasePart.Enabled = bHaveTable && bTarget && !Dsk_bModified;
            Dsk_btnWrite.Enabled    = bHaveTable && Dsk_bModified;
            Dsk_btnRevert.Enabled   = bHaveTable && Dsk_bModified;
            Dsk_btnRefresh.Enabled  = !Dsk_bCommandPending;
            Dsk_btnTest.Enabled     = !Dsk_bCommandPending && Dsk_bDevice;
            Dsk_btnOpen.Enabled     = bHaveTable && bTarget && !Dsk_bModified;
            Dsk_btnClose.Enabled    = !Dsk_bCommandPending;
            Dsk_btnAdd.Enabled      = false;

            if (bHaveTable && PTB_Tables.PTB_FreeSlot(Dsk_objTable) >= 0)
            {   /* A slot is free: is there room?  Add only with at least   */
                /* one free gap to put the partition in.                    */
                lstGaps = new List<PTB_Range>();
                PTB_Tables.PTB_FreeGaps(Dsk_objTable, lstGaps);
                Dsk_btnAdd.Enabled = (lstGaps.Count != 0);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_OnRowSelected:                                                 */
        /*                                                                    */
        /* The list's selection changed: outline it in the picture and        */
        /* reconsider the buttons.                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the list.                                          */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the picture and the buttons follow the selection.       */
        /*--------------------------------------------------------------------*/
        private void Dsk_OnRowSelected(object objSender, EventArgs evArgs)
        {
            /* The picture outlines the selected slot (or none), repaints,   */
            /* and the buttons follow the new selection.                     */
            Dsk_pnlPicture.DPN_SetSelection(Dsk_SelectedSlot());
            Dsk_pnlPicture.Invalidate();
            Dsk_UpdateButtons();
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_OnPictureClicked:                                              */
        /*                                                                    */
        /* The picture's click callback (DPN_BindClick): what was clicked is  */
        /* selected (Dsk_SelectSlot).                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iSlot  : the slot clicked, or -1.                              */
        /*     objGap : the free gap clicked, or null.                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the list and the picture agree.                         */
        /*--------------------------------------------------------------------*/
        private void Dsk_OnPictureClicked(int iSlot, PTB_Range objGap)
        {
            /* The click, as a selection. */
            Dsk_SelectSlot(iSlot, objGap);
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_SelectSlot:                                                    */
        /*                                                                    */
        /* Select a partition's row, or clear the selection and remember a    */
        /* free gap so Add starts there.  With no table the one block selects */
        /* itself and the list's whole-disk row with it.  The picture's click */
        /* callback calls it, and so do Add Partition and an edited table     */
        /* coming back from the worker (DSK_ShowEdited), to select what they  */
        /* made.                                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iSlot  : the slot to select, or -1.                            */
        /*     objGap : the free gap to remember, or null.                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the list and the picture agree.                         */
        /*--------------------------------------------------------------------*/
        private void Dsk_SelectSlot(int iSlot, PTB_Range objGap)
        {
            int i;

            /* The free gap under the click, kept for Add's starting place   */
            /* (null when the click was not on free space).                  */
            Dsk_objClickedRange = objGap;

            if (Dsk_objTable != null && Dsk_objTable.iKind == PTB_Tables.PTB_KIND_NONE)
            {   /* The whole disk is the one thing: its row selected in the list, and shown selected in the picture. */
                if (Dsk_lstParts.Items.Count != 0)
                {   /* The whole-disk row is there (it always is once the table is read). */
                    Dsk_lstParts.Items[0].Selected = true;
                }

                /* Shown selected, repainted, and the buttons updated. */
                Dsk_pnlPicture.DPN_SetSelection(0);
                Dsk_pnlPicture.Invalidate();
                Dsk_UpdateButtons();
                return;
            }

            for (i = 0; i < Dsk_lstParts.Items.Count; i++)
            {   /* One row per iteration: select the clicked slot, clear the rest. */
                Dsk_lstParts.Items[i].Selected = (((PTB_Entry)Dsk_lstParts.Items[i].Tag).iSlot == iSlot);
            }

            /* The picture told the same slot (each row's own selection    */
            /* change has also run Dsk_OnRowSelected; this makes the end   */
            /* state certain, -1 included), repainted, and the buttons.    */
            Dsk_pnlPicture.DPN_SetSelection(iSlot);
            Dsk_pnlPicture.Invalidate();
            Dsk_UpdateButtons();
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_OnNewTableClicked:                                             */
        /*                                                                    */
        /* The New Table button: posted to the worker, which asks the kind    */
        /* and hands back a fresh empty table of it (DSK_ShowEdited).         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : posted; nothing asked or changed here.                  */
        /*--------------------------------------------------------------------*/
        private void Dsk_OnNewTableClicked(object objSender, EventArgs evArgs)
        {
            /* The press, with the table as it is shown now. */
            Dsk_PostEdit(PAC_enmPartitionAction.NewTable);
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_OnAddClicked:                                                  */
        /*                                                                    */
        /* The Add Partition button: posted to the worker, which asks the     */
        /* Add dialog and hands back the table with the new partition         */
        /* (DSK_ShowEdited, which selects it).                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : posted; no dialog is opened here.                       */
        /*--------------------------------------------------------------------*/
        private void Dsk_OnAddClicked(object objSender, EventArgs evArgs)
        {
            /* The action, with the selection as it is at the press. */
            Dsk_PostEdit(PAC_enmPartitionAction.Add);
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_OnDeleteClicked:                                               */
        /*                                                                    */
        /* The Delete Partition button: posted to the worker, which takes the */
        /* selected entry out of a copy of the table and hands the copy back  */
        /* (DSK_ShowEdited).                                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : posted; the table is not changed here.                  */
        /*--------------------------------------------------------------------*/
        private void Dsk_OnDeleteClicked(object objSender, EventArgs evArgs)
        {
            /* The press, with the partition selected now. */
            Dsk_PostEdit(PAC_enmPartitionAction.Delete);
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_PostEdit:                                                      */
        /*                                                                    */
        /* A press run whole by the worker, captured and posted: the button,  */
        /* a copy of the table as it is shown now, the slot selected, whether */
        /* the table has unwritten edits, and the disk's name and whole-disk  */
        /* words.  Nothing is asked, checked or changed here; the window's    */
        /* own table changes only when the edited copy comes back.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     enmAction : the button.                                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the press posted, or nothing for a window with no table */
        /*            shown yet.                                              */
        /*--------------------------------------------------------------------*/
        private void Dsk_PostEdit(PAC_enmPartitionAction enmAction)
        {
            TBE_clsTableEdit objEdit;

            if (Dsk_objTable == null)
            {   /* No layout shown yet (the buttons are greyed then; a   */
                /* guard): nothing to copy.                              */
                return;
            }

            /* The record, with a COPY of the table -- the worker edits it   */
            /* on its own thread.                                            */
            objEdit = new TBE_clsTableEdit(enmAction, PTB_Tables.PTB_CopyTable(Dsk_objTable),
                Dsk_SelectedSlot(), Dsk_bModified, Dsk_sName, Dsk_sWholeContent);

            if (Dsk_objClickedRange != null)
            {   /* A free gap was clicked: a copy of it rides along, so Add   */
                /* proposes that gap.                                         */
                objEdit.TBE_objClicked = new PTB_Range();
                objEdit.TBE_objClicked.ullFirstLba = Dsk_objClickedRange.ullFirstLba;
                objEdit.TBE_objClicked.ullLastLba = Dsk_objClickedRange.ullLastLba;
            }

            /* Handed to the host to post. */
            Dsk_pfnEdit(objEdit);
        }

        /*--------------------------------------------------------------------*/
        /* DSK_ShowEdited:                                                    */
        /*                                                                    */
        /* The host's word that a press the worker ran (Dsk_PostEdit) has     */
        /* edited the table: the edited copy becomes the window's table,      */
        /* marked modified -- nothing is written until Write Table -- and the */
        /* slot the worker names is selected, as if clicked.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objEdit : the press's record as the worker returned it.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window shows the edited, modified table.            */
        /*--------------------------------------------------------------------*/
        internal void DSK_ShowEdited(TBE_clsTableEdit objEdit)
        {
            /* The copy taken as the table; modified; the gap last clicked   */
            /* belonged to the old table's shape, so it is forgotten; then   */
            /* everything shown again.                                       */
            Dsk_objTable = objEdit.TBE_objTable;
            Dsk_bModified = true;
            Dsk_objClickedRange = null;
            Dsk_Refresh();

            if (objEdit.TBE_iSelect >= 0)
            {   /* A slot to select (a partition just added): selected, as   */
                /* if clicked.                                               */
                Dsk_SelectSlot(objEdit.TBE_iSelect, null);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_OnMakeFsClicked:                                               */
        /*                                                                    */
        /* The Make Filesystem button: posted to the worker, which finds the  */
        /* range and runs the format's own sequence (its options, its         */
        /* warning, the format) in the same run.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : posted; nothing refused or asked here.                  */
        /*--------------------------------------------------------------------*/
        private void Dsk_OnMakeFsClicked(object objSender, EventArgs evArgs)
        {
            /* The action, with the partition selected at the press. */
            Dsk_PostEdit(PAC_enmPartitionAction.MakeFs);
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_OnReadPartClicked:                                             */
        /*                                                                    */
        /* The Save Partition to File button: posted to the worker, which     */
        /* finds the range and runs the read's own sequence (its file, the    */
        /* copy) in the same run.                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : posted; no window is opened here.                       */
        /*--------------------------------------------------------------------*/
        private void Dsk_OnReadPartClicked(object objSender, EventArgs evArgs)
        {
            /* The action, with the partition selected at the press. */
            Dsk_PostEdit(PAC_enmPartitionAction.ReadPart);
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_OnWritePartClicked:                                            */
        /*                                                                    */
        /* The Write File to Partition button: posted to the worker, which    */
        /* finds the range and runs the write's own sequence (its file, its   */
        /* confirmation, the copy) in the same run.                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : posted; no window is opened here.                       */
        /*--------------------------------------------------------------------*/
        private void Dsk_OnWritePartClicked(object objSender, EventArgs evArgs)
        {
            /* The action, with the partition selected at the press. */
            Dsk_PostEdit(PAC_enmPartitionAction.WritePart);
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_OnErasePartClicked:                                            */
        /*                                                                    */
        /* The Erase Partition button: posted to the worker, which finds the  */
        /* range and runs the erase's own sequence (its confirmation, the     */
        /* zeros) in the same run.                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : posted; no window is opened here.                       */
        /*--------------------------------------------------------------------*/
        private void Dsk_OnErasePartClicked(object objSender, EventArgs evArgs)
        {
            /* The action, with the partition selected at the press. */
            Dsk_PostEdit(PAC_enmPartitionAction.ErasePart);
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_OnExpandClicked:                                               */
        /*                                                                    */
        /* The Expand Filesystem button: posted to the worker, which grows    */
        /* the partition in the table's copy when free space follows it       */
        /* (asked first), and otherwise runs the filesystem's growth in the   */
        /* same run.                                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : posted; nothing asked or changed here.                  */
        /*--------------------------------------------------------------------*/
        private void Dsk_OnExpandClicked(object objSender, EventArgs evArgs)
        {
            /* The action, with the partition selected at the press. */
            Dsk_PostEdit(PAC_enmPartitionAction.Expand);
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_OnWriteClicked:                                                */
        /*                                                                    */
        /* The Write Table button: posted to the worker, which builds the     */
        /* table's copy into its pieces (refusing one that cannot be built,   */
        /* with the reason) and runs the write's own sequence in the same     */
        /* run.                                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : posted; the table is not built here.                    */
        /*--------------------------------------------------------------------*/
        private void Dsk_OnWriteClicked(object objSender, EventArgs evArgs)
        {
            /* The press; the whole table is written, whatever is selected. */
            Dsk_PostEdit(PAC_enmPartitionAction.WriteTable);
        }

        /*--------------------------------------------------------------------*/
        /* DSK_ShowWriteFailure:                                              */
        /*                                                                    */
        /* The host's word that a Write Table failed (MainCommand.cs): the    */
        /* edits are kept on screen, and the user told that the disk may now  */
        /* hold part of the new table, with the two ways on.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sError : why it failed.                                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the edits stay, for Write Table again or Revert.        */
        /*--------------------------------------------------------------------*/
        internal void DSK_ShowWriteFailure(string sError)
        {
            /* A failed batch may have written some pieces, and none is      */
            /* undone.  The edits are kept and shown again, and no read is   */
            /* asked for -- that is the user's choice (Revert).              */
            Dsk_Refresh();
            Dsk_ShowLocalMessage("The partition table was not written: " + sError + "\n\nThe disk may now hold part of the new table. " +
                "Write Table again, or Revert to read what is there.", "Write Table",
                MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1);
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_OnRevertClicked:                                               */
        /*                                                                    */
        /* Revert: throw the edits away and read the disk again.              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : a re-read is under way.                                 */
        /*--------------------------------------------------------------------*/
        private void Dsk_OnRevertClicked(object objSender, EventArgs evArgs)
        {
            /* No question: Revert exists to throw the edits away, and the   */
            /* read replaces the table in memory with the disk's.            */
            Dsk_Reload();
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_OnRefreshClicked:                                              */
        /*                                                                    */
        /* The Refresh button: posted to the worker, which asks first when    */
        /* the table has unwritten edits and then reads the disk again, in    */
        /* the same run.                                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : posted; nothing asked or read here.                     */
        /*--------------------------------------------------------------------*/
        private void Dsk_OnRefreshClicked(object objSender, EventArgs evArgs)
        {
            /* The press; a read is of the whole disk, whatever is selected.   */
            /* The worker asks first when the table has unwritten edits.       */
            Dsk_PostEdit(PAC_enmPartitionAction.Refresh);
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_OnTestClicked:                                                 */
        /*                                                                    */
        /* Test Capacity: posted for the device the window was opened for.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the test posted; the worker asks, runs it and reports.  */
        /*--------------------------------------------------------------------*/
        private void Dsk_OnTestClicked(object objSender, EventArgs evArgs)
        {
            /* The host knows the device; no warning or progress window is   */
            /* opened here.                                                  */
            Dsk_pfnTest();
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_OnOpenClicked:                                                 */
        /*                                                                    */
        /* The Open button: open the selected partition (or the whole disk    */
        /* of a table-less one) in the main window.                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the selected range is posted to the host.               */
        /*--------------------------------------------------------------------*/
        private void Dsk_OnOpenClicked(object objSender, EventArgs evArgs)
        {
            /* The same posting a double-clicked row makes. */
            Dsk_PostOpen();
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_OnRowActivated:                                                */
        /*                                                                    */
        /* A row of the list was double-clicked (or Enter pressed on it):     */
        /* that is Open.  Unlike the button, the list can be activated while  */
        /* Open is greyed -- unwritten edits, or work pending -- which        */
        /* Dsk_PostOpen refuses.                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the list.                                          */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the selected range is posted, or nothing (Open is off). */
        /*--------------------------------------------------------------------*/
        private void Dsk_OnRowActivated(object objSender, EventArgs evArgs)
        {
            /* The same posting the Open button makes. */
            Dsk_PostOpen();
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_PostOpen:                                                      */
        /*                                                                    */
        /* Open, from the button or a row: capture the selected filesystem    */
        /* range and post it before any window closure.  The request owns the */
        /* order from there -- the partition window's close, then the main    */
        /* window's read-only open of the range.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the range is posted to the host, or nothing while Open  */
        /*            is greyed.                                              */
        /*--------------------------------------------------------------------*/
        private void Dsk_PostOpen()
        {
            PTB_Entry objEntry;
            ulong ullOffset;
            ulong ullLength;

            if (!Dsk_btnOpen.Enabled)
            {   /* Open is greyed (a row activated anyway): nothing.  The   */
                /* worker would check the request again in any case.        */
                return;
            }

            /* The range taken now, so a later selection cannot change a     */
            /* request already posted while the worker asks about closing.   */
            objEntry = Dsk_SelectedEntry();
            if (objEntry != null)
            {   /* A partition: its exact bytes, from its LBAs. */
                ullOffset = objEntry.ullFirstLba * Dsk_objTable.uiSector;
                ullLength = (objEntry.ullLastLba - objEntry.ullFirstLba + 1) * Dsk_objTable.uiSector;
            }
            else
            {   /* The whole disk of a table-less one: from 0, length 0 for   */
                /* "to the end".                                              */
                ullOffset = 0;
                ullLength = 0;
            }

            /* Posted; the window opens nothing itself. */
            Dsk_pfnOpen(ullOffset, ullLength);
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_OnCloseClicked:                                                */
        /*                                                                    */
        /* The Close button: the worker asked to end the window.  It asks     */
        /* about unwritten edits and then closes the window itself.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button (unused).                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the request posted; the window is still open.           */
        /*--------------------------------------------------------------------*/
        private void Dsk_OnCloseClicked(object objSender, EventArgs evArgs)
        {
            /* Posted, as the X is (Dsk_OnClosing). */
            Dsk_pfnClose();
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_OnClosing:                                                     */
        /*                                                                    */
        /* The window is asked to close.  The worker's own Close              */
        /* (DSK_CloseFromCommand) passes; any other -- the X, Alt+F4,         */
        /* Escape's Close -- is refused and posted as a request instead, so   */
        /* the window ends only through the worker, after its discard         */
        /* question.                                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the window (unused).                               */
        /*     evClosing : the closing event; cancelled for a request.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the worker's close allowed, or the request posted.      */
        /*--------------------------------------------------------------------*/
        private void Dsk_OnClosing(object objSender, FormClosingEventArgs evClosing)
        {
            if (Dsk_bCommandClose)
            {   /* The worker's own Close: let through, and whether it goes   */
                /* ahead recorded for DSK_CloseFromCommand's answer -- this   */
                /* is the event's one handler, so its Cancel is final here.   */
                Dsk_bCommandClosed = !evClosing.Cancel;
                return;
            }

            /* Kept open, and the close posted as a request.  Pending work   */
            /* does not stop the request being posted; the worker decides.   */
            evClosing.Cancel = true;
            Dsk_pfnClose();
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_OnResize:                                                      */
        /*                                                                    */
        /* The window's size changed: refit the list's columns (the picture   */
        /* redraws itself).                                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the window.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the layout fits the new size.                           */
        /*--------------------------------------------------------------------*/
        private void Dsk_OnResize(object objSender, EventArgs evArgs)
        {
            /* The Name column takes the new width; the picture repaints   */
            /* itself (ResizeRedraw).                                      */
            Dsk_FitColumns();
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_FitLine:                                                       */
        /*                                                                    */
        /* Make one of the two lines above the picture (the head line or the  */
        /* state line) as tall as its text needs at its present width, so a   */
        /* long text wraps onto further lines inside the space left of the    */
        /* command column rather than running into it and being cut off.      */
        /* The lines are docked at the top, so a change of height moves the   */
        /* picture and the list down (or up) with it.                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lbl : the line -- Dsk_lblHead or Dsk_lblState.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the line's height fits its text.                        */
        /*--------------------------------------------------------------------*/
        private void Dsk_FitLine(Label lbl)
        {
            int iWidth;
            int iHeight;

            /* The width the text is laid out in: the line less its padding. */
            iWidth = lbl.ClientSize.Width - lbl.Padding.Horizontal;

            if (iWidth < 1)
            {   /* No width yet (before the window's first layout): one line's   */
                /* height, measured unwrapped, until the line is given a width   */
                /* and its SizeChanged measures it again.                        */
                iHeight = lbl.PreferredHeight + (DSK_PAD / 2);
            }
            else
            {   /* The text broken at word boundaries to that width, measured   */
                /* in the line's own font, with the access-key "&" read as      */
                /* text as the line shows it (UseMnemonic is off) -- the same   */
                /* rules the line draws by -- and half a pad of room.           */
                iHeight = TextRenderer.MeasureText(lbl.Text, lbl.Font, new Size(iWidth, int.MaxValue),
                                                   TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + (DSK_PAD / 2);
            }

            if (lbl.Height != iHeight)
            {   /* A different height: set it.  This raises the line's          */
                /* SizeChanged again, which measures the same width and text,   */
                /* finds the height right and sets nothing, so the two never    */
                /* loop.                                                        */
                lbl.Height = iHeight;
            }
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_OnStateSized:                                                  */
        /*                                                                    */
        /* The state line's size changed -- the window was resized, or the    */
        /* line was docked into place: its height is measured again for its   */
        /* new width.                                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the state line.                                    */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the line's height fits its text.                        */
        /*--------------------------------------------------------------------*/
        private void Dsk_OnStateSized(object objSender, EventArgs evArgs)
        {
            /* Layout work only, in the window's own message: nothing posted. */
            Dsk_FitLine(Dsk_lblState);
        }

        /*--------------------------------------------------------------------*/
        /* Dsk_OnHeadSized:                                                   */
        /*                                                                    */
        /* The head line's size changed: its height is measured again for its */
        /* new width, as the state line's is.                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the head line.                                     */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the line's height fits its text.                        */
        /*--------------------------------------------------------------------*/
        private void Dsk_OnHeadSized(object objSender, EventArgs evArgs)
        {
            /* Layout work only, in the window's own message: nothing posted. */
            Dsk_FitLine(Dsk_lblHead);
        }
    }

    /*------------------------------------------------------------------------*/
    /* NTB_clsNewTableForm -- the question New Table asks: which kind.  The   */
    /* current kind is preselected.                                           */
    /*                                                                        */
    /* Derives from Form because the framework requires it: WinForms makes a  */
    /* window only as a Form subclass.  That is the one kind of inheritance   */
    /* allowed here -- from a framework base class, never from a class of     */
    /* ours.                                                                  */
    /*------------------------------------------------------------------------*/
    internal sealed class NTB_clsNewTableForm : Form
    {
        /* The dialog's one size -- documentation table. */
        private const int NTB_PAD = 12;    // padding round the content, pixels

        /* The outcome, valid when NTB_bAccepted. */
        internal bool NTB_bAccepted;    // true when OK was pressed
        internal int  NTB_iKind;        // the PTB_KIND_* chosen

        /* The dialog's controls -- documentation table. */
        private RadioButton     Ntb_radNone;      // no table
        private RadioButton     Ntb_radMbr;       // MBR
        private RadioButton     Ntb_radGpt;       // GPT
        private FlowLayoutPanel Ntb_pnlChoices;   // the three, top down
        private FlowLayoutPanel Ntb_pnlButtons;   // OK and Cancel, right-aligned
        private Button          Ntb_btnOk;        // accepts
        private Button          Ntb_btnCancel;    // abandons

        /*--------------------------------------------------------------------*/
        /* NTB_clsNewTableForm (constructor):                                 */
        /*                                                                    */
        /* Build the dialog with the current kind preselected.                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     fntDialog : the program's dialog font (the caller's; not       */
        /*                 freed here).                                       */
        /*     iKindNow  : the table's present kind.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the dialog, not yet shown.                     */
        /*--------------------------------------------------------------------*/
        internal NTB_clsNewTableForm(Font fntDialog, int iKindNow)
        {
            /* Not accepted yet; the kind starts as the table's own. */
            NTB_bAccepted = false;
            NTB_iKind     = iKindNow;

            /* The caller's dialog font, set before any control is made:   */
            /* each control takes it once parented, and the layout is      */
            /* measured in it.                                             */
            this.Font = fntDialog;

            /* A fixed dialog, centred on its owner, with no maximise,      */
            /* minimise or taskbar button, and NTB_PAD round its content.   */
            this.Text            = "New partition table";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox     = false;
            this.MinimizeBox     = false;
            this.ShowInTaskbar   = false;
            this.StartPosition   = FormStartPosition.CenterParent;
            this.Padding         = new Padding(NTB_PAD);

            /* The three kinds, each worded with what it is for, the   */
            /* table's present kind ticked.                            */
            Ntb_radNone          = Ntb_MakeChoice("&No partition table -- the whole disk is one filesystem (a USB stick or card used as one volume)");
            Ntb_radMbr           = Ntb_MakeChoice("&MBR -- up to four partitions; what most cards, sticks and Raspberry Pi images use");
            Ntb_radGpt           = Ntb_MakeChoice("&GPT -- a GUID partition table: named partitions of any type, more than four (what an eMMC's firmware layout uses)");
            Ntb_radNone.Checked  = (iKindNow == PTB_Tables.PTB_KIND_NONE);
            Ntb_radMbr.Checked   = (iKindNow == PTB_Tables.PTB_KIND_MBR);
            Ntb_radGpt.Checked   = (iKindNow == PTB_Tables.PTB_KIND_GPT);

            /* The three, top down at the top, never wrapped, sized to the   */
            /* longest; sharing this panel makes them one radio group.       */
            Ntb_pnlChoices               = new FlowLayoutPanel();
            Ntb_pnlChoices.Dock          = DockStyle.Top;
            Ntb_pnlChoices.FlowDirection = FlowDirection.TopDown;
            Ntb_pnlChoices.WrapContents  = false;
            Ntb_pnlChoices.AutoSize      = true;
            Ntb_pnlChoices.Controls.Add(Ntb_radNone);
            Ntb_pnlChoices.Controls.Add(Ntb_radMbr);
            Ntb_pnlChoices.Controls.Add(Ntb_radGpt);

            /* Cancel, sized to its text; it closes with nothing accepted. */
            Ntb_btnCancel          = new Button();
            Ntb_btnCancel.Text     = "Cancel";
            Ntb_btnCancel.AutoSize = true;
            Ntb_btnCancel.Click   += Ntb_OnCancelClicked;

            /* OK, likewise; it records the kind ticked. */
            Ntb_btnOk          = new Button();
            Ntb_btnOk.Text     = "OK";
            Ntb_btnOk.AutoSize = true;
            Ntb_btnOk.Click   += Ntb_OnOkClicked;

            /* The button row along the bottom, a pad above it, flowing   */
            /* right to left -- Cancel, added first, at the right edge.   */
            Ntb_pnlButtons               = new FlowLayoutPanel();
            Ntb_pnlButtons.Dock          = DockStyle.Bottom;
            Ntb_pnlButtons.FlowDirection = FlowDirection.RightToLeft;
            Ntb_pnlButtons.AutoSize      = true;
            Ntb_pnlButtons.Padding       = new Padding(0, NTB_PAD, 0, 0);
            Ntb_pnlButtons.Controls.Add(Ntb_btnCancel);
            Ntb_pnlButtons.Controls.Add(Ntb_btnOk);

            /* Into the dialog (the buttons, added last, dock first at the   */
            /* bottom); Enter is OK and Escape Cancel; then sized to the     */
            /* choices, again on any resize.                                 */
            this.Controls.Add(Ntb_pnlChoices);
            this.Controls.Add(Ntb_pnlButtons);
            this.AcceptButton = Ntb_btnOk;
            this.CancelButton = Ntb_btnCancel;
            this.Resize      += Ntb_OnResize;
            Ntb_FitLayout();
        }

        /*--------------------------------------------------------------------*/
        /* Ntb_MakeChoice:                                                    */
        /*                                                                    */
        /* One radio button, sized to its text.                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sText : the caption.                                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RadioButton : the button, not yet added.                       */
        /*--------------------------------------------------------------------*/
        private RadioButton Ntb_MakeChoice(string sText)
        {
            RadioButton rad;

            /* The caption (with its access key), sized to it, and half a   */
            /* pad below to space the choices apart.                        */
            rad          = new RadioButton();
            rad.Text     = sText;
            rad.AutoSize = true;
            rad.Margin   = new Padding(0, 0, 0, NTB_PAD / 2);

            /* Ready to add. */
            return(rad);
        }

        /*--------------------------------------------------------------------*/
        /* Ntb_FitLayout:                                                     */
        /*                                                                    */
        /* The dialog as wide as its widest choice and as tall as its rows.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the client size fits.                                   */
        /*--------------------------------------------------------------------*/
        private void Ntb_FitLayout()
        {
            /* The choices' panel and the button row, stacked, with the   */
            /* dialog's padding either side and above and below.          */
            this.ClientSize = new Size(Ntb_pnlChoices.PreferredSize.Width + (2 * NTB_PAD),
                                       Ntb_pnlChoices.PreferredSize.Height + Ntb_pnlButtons.PreferredSize.Height + (2 * NTB_PAD));
        }

        /*--------------------------------------------------------------------*/
        /* Ntb_OnOkClicked:                                                   */
        /*                                                                    */
        /* OK: record the choice and close accepted.                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the dialog closes.                                      */
        /*--------------------------------------------------------------------*/
        private void Ntb_OnOkClicked(object objSender, EventArgs evArgs)
        {
            if (Ntb_radGpt.Checked)
            {   /* GPT. */
                NTB_iKind = PTB_Tables.PTB_KIND_GPT;
            }
            else if (Ntb_radMbr.Checked)
            {   /* MBR. */
                NTB_iKind = PTB_Tables.PTB_KIND_MBR;
            }
            else
            {   /* None. */
                NTB_iKind = PTB_Tables.PTB_KIND_NONE;
            }

            /* Accepted, and closed; the caller reads NTB_iKind. */
            NTB_bAccepted = true;
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Ntb_OnCancelClicked:                                               */
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
        private void Ntb_OnCancelClicked(object objSender, EventArgs evArgs)
        {
            /* Closed; NTB_bAccepted is still false from the constructor. */
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Ntb_OnResize:                                                      */
        /*                                                                    */
        /* The dialog's size changed -- not by the user (a fixed border), but */
        /* by its own sizing: the constructor's Ntb_FitLayout runs after this */
        /* is hooked.  Refit, detached while doing so, because setting        */
        /* ClientSize raises Resize again.                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the dialog.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the layout fits.                                        */
        /*--------------------------------------------------------------------*/
        private void Ntb_OnResize(object objSender, EventArgs evArgs)
        {
            /* Unhooked, refitted, hooked again. */
            this.Resize -= Ntb_OnResize;
            Ntb_FitLayout();
            this.Resize += Ntb_OnResize;
        }
    }

    /*-------------------------------------------------------------------------*/
    /* ADD_clsAddForm -- the question Add Partition asks: where, how big, what */
    /* type, and (on a GPT) what name.  A free gap is chosen from a list and   */
    /* fills in an aligned start and the size to the gap's end; both can be    */
    /* typed over, in bytes or with a K, M, G or T suffix.  The type is one    */
    /* of the named ones or, typed, a GUID (GPT) or a hexadecimal byte (MBR).  */
    /* OK checks the new partition on a trial copy of the entries, by the      */
    /* table's own check (PTB_Validate); the worker adds it to its copy of the */
    /* table only once the dialog has been accepted (Sys_TableEditSequence,    */
    /* CommandOperations.cs), and the window shows that copy.                  */
    /*                                                                         */
    /* Derives from Form because the framework requires it: WinForms makes a   */
    /* window only as a Form subclass.  That is the one kind of inheritance    */
    /* allowed here -- from a framework base class, never from a class of      */
    /* ours.                                                                   */
    /*-------------------------------------------------------------------------*/
    internal sealed class ADD_clsAddForm : Form
    {
        /* The dialog's sizes and the size suffixes -- documentation table. */
        private const int   ADD_PAD        = 12;                // padding round the content, pixels
        private const int   ADD_LABEL_PAD  = 8;                 // pixels between a caption and its field
        private const int   ADD_FIELD_CHARS = 28;               // the width of a text field, in "0"s
        private const ulong ADD_KIB        = 1024UL;            // the K suffix
        private const ulong ADD_MIB        = ADD_KIB * 1024UL;  // M
        private const ulong ADD_GIB        = ADD_MIB * 1024UL;  // G
        private const ulong ADD_TIB        = ADD_GIB * 1024UL;  // T

        /* The outcome, valid when ADD_bAccepted. */
        internal bool         ADD_bAccepted;    // true when OK was pressed with a valid partition
        internal ulong        ADD_ullFirstLba;  // the first sector
        internal ulong        ADD_ullLastLba;   // the last sector, inclusive
        internal PTB_TypeName ADD_objType;      // the type, named or made from what was typed
        internal string       ADD_sName;        // the GPT name ("" otherwise)
        internal bool         ADD_bBoot;        // the MBR active flag

        /* What the dialog was given -- documentation table. */
        private PTB_Table       Add_objTable;     // the table being added to (read only here)
        private List<PTB_Range> Add_lstGaps;      // the free gaps, in disk order
        private List<PTB_TypeName> Add_lstTypes;  // the named types this kind has

        /* The dialog's controls -- documentation table. */
        private TableLayoutPanel Add_pnlFields;   // captions and fields in two columns
        private ComboBox         Add_cmbGap;      // the free gaps
        private TextBox          Add_txtStart;    // the start, bytes
        private TextBox          Add_txtSize;     // the size, bytes
        private ComboBox         Add_cmbType;     // the type: named, or typed
        private TextBox          Add_txtName;     // the GPT name
        private CheckBox         Add_chkBoot;     // the MBR active flag
        private Label            Add_lblHint;     // what the fields take
        private FlowLayoutPanel  Add_pnlButtons;  // OK and Cancel, right-aligned
        private Button           Add_btnOk;       // asks for the partition to be checked and accepted (Add_AcceptPartition, from the dialog's queue)
        private Button           Add_btnCancel;   // abandons

        /* The work the dialog's handlers post to its own queue, which   */
        /* Add_RunItem runs one at a time -- documentation table.        */
        private enum Add_enmItem
        {
            Accept = 1     // OK or Enter: the fields checked against a trial table and, when the partition stands, taken (Add_AcceptPartition)
        }

        /* The dialog's own queue: its handlers post, never show a box. */
        private WQU_clsWindowQueue Add_objQueue;   // made in the constructor before any control is hooked to post to it

        /*--------------------------------------------------------------------*/
        /* ADD_clsAddForm (constructor):                                      */
        /*                                                                    */
        /* Build the dialog over the table, its gaps, and the gap to start    */
        /* from.                                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     fntDialog  : the program's dialog font (the caller's; not      */
        /*                  freed here).                                      */
        /*     objTable   : the table.                                        */
        /*     lstGaps    : its free gaps, in disk order (not empty).         */
        /*     objStartRange : the gap to preselect.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the dialog, not yet shown.                     */
        /*--------------------------------------------------------------------*/
        internal ADD_clsAddForm(Font fntDialog, PTB_Table objTable, List<PTB_Range> lstGaps, PTB_Range objStartRange)
        {
            int iRow;
            int i;

            /* Nothing accepted yet; the table and its gaps kept; the type   */
            /* list built next.                                              */
            ADD_bAccepted   = false;
            ADD_ullFirstLba = 0;
            ADD_ullLastLba  = 0;
            ADD_objType        = null;
            ADD_sName       = "";
            ADD_bBoot       = false;
            Add_objTable       = objTable;
            Add_lstGaps     = lstGaps;
            Add_lstTypes    = new List<PTB_TypeName>();

            for (i = 0; i < PTB_Tables.PTB_aTypes.Length; i++)
            {   /* One named type per iteration: the ones this kind has. */
                if (objTable.iKind == PTB_Tables.PTB_KIND_GPT && PTB_Tables.PTB_aTypes[i].guidGpt != Guid.Empty)
                {   /* A GPT type. */
                    Add_lstTypes.Add(PTB_Tables.PTB_aTypes[i]);
                }
                else if (objTable.iKind == PTB_Tables.PTB_KIND_MBR && PTB_Tables.PTB_aTypes[i].bMbr != 0)
                {   /* An MBR type. */
                    Add_lstTypes.Add(PTB_Tables.PTB_aTypes[i]);
                }
            }

            /* The caller's dialog font, set before any control is made.  The   */
            /* fields' widths and the hint's height below are measured in it    */
            /* (this.Font), not in each control's own Font, which stays the     */
            /* inherited default until the control is added to the dialog.      */
            this.Font = fntDialog;

            /* A fixed dialog, centred on its owner, with no maximise,      */
            /* minimise or taskbar button, and ADD_PAD round its content.   */
            this.Text            = "Add a partition";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox     = false;
            this.MinimizeBox     = false;
            this.ShowInTaskbar   = false;
            this.StartPosition   = FormStartPosition.CenterParent;
            this.Padding         = new Padding(ADD_PAD);

            /* The fields, captions in the left column. */
            Add_pnlFields              = new TableLayoutPanel();
            Add_pnlFields.Dock         = DockStyle.Top;
            Add_pnlFields.AutoSize     = true;
            Add_pnlFields.ColumnCount  = 2;
            Add_pnlFields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Add_pnlFields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            /* The free gaps, a fixed list (no typing), twice a text field's   */
            /* width so a gap's line fits.                                     */
            Add_cmbGap               = new ComboBox();
            Add_cmbGap.DropDownStyle = ComboBoxStyle.DropDownList;
            Add_cmbGap.Width         = TextRenderer.MeasureText(new string('0', ADD_FIELD_CHARS * 2), this.Font).Width;

            for (i = 0; i < lstGaps.Count; i++)
            {   /* One gap per iteration: its sectors and its short size.   */
                /* Its index matches Add_lstGaps', which Add_FillFromGap    */
                /* relies on.                                               */
                Add_cmbGap.Items.Add("sectors " + lstGaps[i].ullFirstLba.ToString("N0") + " to " + lstGaps[i].ullLastLba.ToString("N0") + "  (" +
                                     DVF_clsDeviceForm.DVF_SizeShortText((lstGaps[i].ullLastLba - lstGaps[i].ullFirstLba + 1) * objTable.uiSector) + ")");

                if (lstGaps[i] == objStartRange)
                {   /* The one to start from. */
                    Add_cmbGap.SelectedIndex = i;
                }
            }

            if (Add_cmbGap.SelectedIndex < 0)
            {   /* The start gap was not in the list (the test above is by   */
                /* object, so a gap from an older list never matches): the   */
                /* first.                                                    */
                Add_cmbGap.SelectedIndex = 0;
            }

            /* The start and size boxes, a text field's width each. */
            Add_txtStart       = new TextBox();
            Add_txtStart.Width = TextRenderer.MeasureText(new string('0', ADD_FIELD_CHARS), this.Font).Width;
            Add_txtSize        = new TextBox();
            Add_txtSize.Width  = Add_txtStart.Width;

            /* The type: a list that can also be typed into (DropDown, not   */
            /* DropDownList), so a GUID or a hex byte can be entered.        */
            Add_cmbType               = new ComboBox();
            Add_cmbType.DropDownStyle = ComboBoxStyle.DropDown;
            Add_cmbType.Width         = Add_cmbGap.Width;

            for (i = 0; i < Add_lstTypes.Count; i++)
            {   /* One named type per iteration. */
                Add_cmbType.Items.Add(Add_lstTypes[i].sName);
            }

            /* The first named type -- the table's first of this kind, its    */
            /* default (PartitionTable.cs).  The list is never empty: Add     */
            /* is only enabled for an MBR or a GPT (PTB_FreeSlot answers -1   */
            /* for no table), and both have named types.                      */
            Add_cmbType.SelectedIndex = 0;

            /* The GPT name box, and the MBR active flag; only one of the   */
            /* two is shown, by the table's kind below.                     */
            Add_txtName       = new TextBox();
            Add_txtName.Width = Add_cmbGap.Width;
            Add_chkBoot       = new CheckBox();
            Add_chkBoot.Text  = "Mark &active (bootable)";
            Add_chkBoot.AutoSize = true;

            /* The rows, top down. */
            iRow = 0;
            Add_AddRow(iRow++, "Free &space:", Add_cmbGap);
            Add_AddRow(iRow++, "S&tart (bytes):", Add_txtStart);
            Add_AddRow(iRow++, "Si&ze (bytes):", Add_txtSize);
            Add_AddRow(iRow++, "&Type:", Add_cmbType);

            if (objTable.iKind == PTB_Tables.PTB_KIND_GPT)
            {   /* A GPT partition has a name. */
                Add_AddRow(iRow++, "&Name:", Add_txtName);
            }
            else
            {   /* An MBR partition has an active flag. */
                Add_AddRow(iRow++, "", Add_chkBoot);
            }

            /* The hint under the fields, as wide as the field column and    */
            /* its widest caption, as tall as its wrapped text.  Its "4      */
            /* MiB" is PartitionTable.cs's PTB_ALIGN_BYTES, written out --   */
            /* the two change together.                                      */
            Add_lblHint          = new Label();
            Add_lblHint.Dock     = DockStyle.Top;
            Add_lblHint.AutoSize = false;
            Add_lblHint.Padding  = new Padding(0, ADD_PAD / 2, 0, 0);
            Add_lblHint.Text     = "Start and size take a byte count, or a number followed by K, M, G or T (1024-based). A new partition starts on a " +
                                   "4 MiB boundary (what cards and Pi images use) unless the start is typed over. The type may also be typed: a GUID for a GPT, a hexadecimal byte " +
                                   "(83) for an MBR.";
            Add_lblHint.Width    = Add_cmbGap.Width + TextRenderer.MeasureText("Free space:", this.Font).Width + ADD_LABEL_PAD;
            Add_lblHint.Height   = TextRenderer.MeasureText(Add_lblHint.Text, this.Font, new Size(Add_lblHint.Width, 0),
                                                            TextFormatFlags.WordBreak).Height + ADD_PAD;

            /* Cancel, sized to its text; it closes with nothing accepted. */
            Add_btnCancel          = new Button();
            Add_btnCancel.Text     = "Cancel";
            Add_btnCancel.AutoSize = true;
            Add_btnCancel.Click   += Add_OnCancelClicked;

            /* The dialog's own queue, made before OK, which posts to it, is   */
            /* hooked: OK asks for the partition to be checked and taken,      */
            /* and Add_RunItem does it, so a refusal is never shown from       */
            /* inside the button's handler.                                    */
            Add_objQueue = new WQU_clsWindowQueue(this, Add_RunItem);

            /* OK, likewise; it asks for the fields to be checked and the   */
            /* partition taken.                                             */
            Add_btnOk          = new Button();
            Add_btnOk.Text     = "OK";
            Add_btnOk.AutoSize = true;
            Add_btnOk.Click   += Add_OnOkClicked;

            /* The button row along the bottom, a pad above it, flowing   */
            /* right to left -- Cancel, added first, at the right edge.   */
            Add_pnlButtons               = new FlowLayoutPanel();
            Add_pnlButtons.Dock          = DockStyle.Bottom;
            Add_pnlButtons.FlowDirection = FlowDirection.RightToLeft;
            Add_pnlButtons.AutoSize      = true;
            Add_pnlButtons.Padding       = new Padding(0, ADD_PAD, 0, 0);
            Add_pnlButtons.Controls.Add(Add_btnCancel);
            Add_pnlButtons.Controls.Add(Add_btnOk);

            /* Docking runs in reverse order of adding (as found): the       */
            /* buttons take the bottom first, then the fields and the hint   */
            /* stack down from the top.                                      */
            this.Controls.Add(Add_lblHint);
            this.Controls.Add(Add_pnlFields);
            this.Controls.Add(Add_pnlButtons);

            /* Enter is OK and Escape Cancel; refitted on a resize, and   */
            /* sized once now.                                            */
            this.AcceptButton = Add_btnOk;
            this.CancelButton = Add_btnCancel;
            this.Resize      += Add_OnResize;
            Add_FitLayout();

            /* The start and size from the preselected gap, then follow the gap. */
            Add_FillFromGap();
            Add_cmbGap.SelectedIndexChanged += Add_OnGapChanged;
        }

        /*--------------------------------------------------------------------*/
        /* Add_AddRow:                                                        */
        /*                                                                    */
        /* One row of the field table: a caption and its field.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iRow     : the row.                                            */
        /*     sCaption : the caption, with its access key ("" for none).     */
        /*     ctl      : the field.                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the row is in the table.                                */
        /*--------------------------------------------------------------------*/
        private void Add_AddRow(int iRow, string sCaption, Control ctl)
        {
            Label lbl;

            /* The caption: sized to its text, at the left, vertically       */
            /* centred on its field, the label pad between them and half a   */
            /* pad below; the field likewise at the left with half a pad     */
            /* below, so every row is spaced the same.                       */
            lbl           = new Label();
            lbl.Text      = sCaption;
            lbl.AutoSize  = true;
            lbl.Anchor    = AnchorStyles.Left;
            lbl.TextAlign = ContentAlignment.MiddleLeft;
            lbl.Margin    = new Padding(0, 0, ADD_LABEL_PAD, ADD_PAD / 2);
            ctl.Margin    = new Padding(0, 0, 0, ADD_PAD / 2);
            ctl.Anchor    = AnchorStyles.Left;

            /* The caption in column 0, the field in column 1, on the row. */
            Add_pnlFields.Controls.Add(lbl, 0, iRow);
            Add_pnlFields.Controls.Add(ctl, 1, iRow);
        }

        /*--------------------------------------------------------------------*/
        /* Add_FitLayout:                                                     */
        /*                                                                    */
        /* The dialog as wide as its fields and as tall as its rows.          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the client size fits.                                   */
        /*--------------------------------------------------------------------*/
        private void Add_FitLayout()
        {
            /* As wide as the wider of the field table and the hint, as   */
            /* tall as the three parts stacked, with the padding.         */
            this.ClientSize = new Size(Math.Max(Add_pnlFields.PreferredSize.Width, Add_lblHint.Width) + (2 * ADD_PAD),
                                       Add_pnlFields.PreferredSize.Height + Add_lblHint.Height + Add_pnlButtons.PreferredSize.Height + (2 * ADD_PAD));
        }

        /*--------------------------------------------------------------------*/
        /* Add_OnGapChanged:                                                  */
        /*                                                                    */
        /* Another gap chosen: fill the start and size from it.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the combo box.                                     */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the start and size boxes are filled.                    */
        /*--------------------------------------------------------------------*/
        private void Add_OnGapChanged(object objSender, EventArgs evArgs)
        {
            /* Whatever was typed into the two boxes is replaced. */
            Add_FillFromGap();
        }

        /*--------------------------------------------------------------------*/
        /* Add_FillFromGap:                                                   */
        /*                                                                    */
        /* The chosen gap's start and size into their boxes: the start is its */
        /* first sector rounded up to the alignment (or the gap's start when  */
        /* the gap is too small for that), the size runs to the gap's end.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the start and size boxes are filled.                    */
        /*--------------------------------------------------------------------*/
        private void Add_FillFromGap()
        {
            PTB_Range objGap;
            ulong     ullStart;

            if (Add_cmbGap.SelectedIndex < 0)
            {   /* Nothing chosen. */
                return;
            }

            /* The gap (the box's rows are in Add_lstGaps' order) and its   */
            /* first sector rounded up to the alignment boundary.           */
            objGap      = Add_lstGaps[Add_cmbGap.SelectedIndex];
            ullStart = PTB_Tables.PTB_AlignUp(objGap.ullFirstLba, Add_objTable.uiSector);

            if (ullStart > objGap.ullLastLba)
            {   /* The gap ends before the next boundary: take it as it is. */
                ullStart = objGap.ullFirstLba;
            }

            /* Into the boxes as plain byte counts (what ADD_ParseSize   */
            /* reads back): the start, and the size to the gap's end.    */
            Add_txtStart.Text = (ullStart * Add_objTable.uiSector).ToString();
            Add_txtSize.Text  = ((objGap.ullLastLba - ullStart + 1) * Add_objTable.uiSector).ToString();
        }

        /*--------------------------------------------------------------------*/
        /* ADD_ParseSize:                                                     */
        /*                                                                    */
        /* A byte count out of typed text: digits with commas and spaces      */
        /* removed, optionally followed by K, M, G or T meaning               */
        /* 1024-based multiples.  Refuses anything else, and a value that     */
        /* would overflow.                                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sText    : the text.                                           */
        /*     ullBytes : receives the count.                                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the text was a size.                          */
        /*--------------------------------------------------------------------*/
        internal static bool ADD_ParseSize(string sText, out ulong ullBytes)
        {
            string sDigits;
            ulong  ullUnit;
            ulong  ullValue;
            char   cLast;

            /* No count yet; the text without surrounding spaces and with    */
            /* commas and inner spaces taken out (only those separators --   */
            /* a "." grouping is not handled).                               */
            ullBytes = 0;
            sDigits  = sText.Trim().Replace(",", "").Replace(" ", "");

            if (sDigits.Length == 0)
            {   /* Nothing typed. */
                return(false);
            }

            /* The last character, in either case, decides the unit; bytes   */
            /* (1) unless it is a suffix.                                    */
            cLast   = char.ToUpperInvariant(sDigits[sDigits.Length - 1]);
            ullUnit = 1;

            if (cLast == 'K')
            {   /* KiB. */
                ullUnit = ADD_KIB;
            }
            else if (cLast == 'M')
            {   /* MiB. */
                ullUnit = ADD_MIB;
            }
            else if (cLast == 'G')
            {   /* GiB. */
                ullUnit = ADD_GIB;
            }
            else if (cLast == 'T')
            {   /* TiB. */
                ullUnit = ADD_TIB;
            }

            if (ullUnit != 1)
            {   /* Take the suffix off. */
                sDigits = sDigits.Substring(0, sDigits.Length - 1);
            }

            if (!ulong.TryParse(sDigits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ullValue))
            {   /* Not a number. */
                return(false);
            }

            if (ullValue > ulong.MaxValue / ullUnit)
            {   /* Would overflow. */
                return(false);
            }

            /* The count, safe from overflow by the test above. */
            ullBytes = ullValue * ullUnit;

            /* A size. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* Add_ParseType:                                                     */
        /*                                                                    */
        /* The type from the combo box: a named one when the text is one of   */
        /* the names, else a GUID (GPT) or a hexadecimal byte with or without */
        /* 0x (MBR) typed in.                                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objType : receives the type.                                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the text was a type.                          */
        /*--------------------------------------------------------------------*/
        private bool Add_ParseType(out PTB_TypeName objType)
        {
            string sText;
            Guid   guidTyped;
            byte   bTyped;
            int    i;

            /* No type yet; the box's text, typed or chosen. */
            objType  = null;
            sText = Add_cmbType.Text.Trim();

            for (i = 0; i < Add_lstTypes.Count; i++)
            {   /* One named type per iteration. */
                if (string.Compare(Add_lstTypes[i].sName, sText, StringComparison.OrdinalIgnoreCase) == 0)
                {   /* Named. */
                    objType = Add_lstTypes[i];
                    return(true);
                }
            }

            /* Not a name: a type of its own, named by what was typed,   */
            /* with neither value set until the text is parsed below.    */
            objType         = new PTB_TypeName();
            objType.sName   = sText;
            objType.bMbr    = 0;
            objType.guidGpt = Guid.Empty;

            if (Add_objTable.iKind == PTB_Tables.PTB_KIND_GPT)
            {   /* A GUID. */
                if (!Guid.TryParse(sText, out guidTyped) || guidTyped == Guid.Empty)
                {   /* Not one -- or the all-zero GUID, which marks an unused   */
                    /* entry and so is no type.                                 */
                    return(false);
                }

                /* A typed GUID. */
                objType.guidGpt = guidTyped;
                return(true);
            }

            if (sText.StartsWith("0x") || sText.StartsWith("0X"))
            {   /* With the prefix. */
                sText = sText.Substring(2);
            }

            if (!byte.TryParse(sText, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out bTyped) ||
                bTyped == 0)
            {   /* Not a byte, or the empty-slot value. */
                return(false);
            }

            /* The byte as the type. */
            objType.bMbr = bTyped;

            /* A typed byte. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* Add_OnOkClicked:                                                   */
        /*                                                                    */
        /* OK, or Enter, the dialog's accept key: asks for the partition to   */
        /* be checked and taken.  The handler only posts: the checks can      */
        /* show a refusal box, which is never shown from a handler.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : an Accept item posted to the dialog's queue.            */
        /*--------------------------------------------------------------------*/
        private void Add_OnOkClicked(object objSender, EventArgs evArgs)
        {
            /* Posted to the dialog's own queue; a second press while one   */
            /* waits is dropped there.                                      */
            Add_objQueue.WQU_Post((int)Add_enmItem.Accept, null);
        }

        /*--------------------------------------------------------------------*/
        /* Add_RunItem:                                                       */
        /*                                                                    */
        /* The dialog's queue runner: one posted item at a time, after the    */
        /* handler that posted it has returned, so a refusal box is shown     */
        /* from here and never from inside a handler.                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iItem   : the Add_enmItem posted.                              */
        /*     objArgs : the item's argument (none for Accept).               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item's work done.                                   */
        /*--------------------------------------------------------------------*/
        private void Add_RunItem(int iItem, object objArgs)
        {
            switch ((Add_enmItem)iItem)
            {   /* Which work was asked for. */
                case Add_enmItem.Accept:
                    /* The partition checked and, when it stands, taken. */
                    Add_AcceptPartition();
                    break;
                default:
                    /* No such item (a guard: only OK's handler posts):   */
                    /* nothing done.                                      */
                    break;
            }
        }

        /*--------------------------------------------------------------------*/
        /* Add_AcceptPartition:                                               */
        /*                                                                    */
        /* OK's work, run from the dialog's queue: parse the start, the size  */
        /* and the type, then add the entry to a COPY of the table and run    */
        /* the model's validation over it, so an overlap, a range outside the */
        /* usable area or a long name is refused here with the model's own    */
        /* words.  Accepted, the outcome is set and the dialog closes.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the dialog closes accepted, or shows what to correct.   */
        /*--------------------------------------------------------------------*/
        private void Add_AcceptPartition()
        {
            PTB_Table    objTrial;
            PTB_TypeName objType;
            PTB_Entry    objEntry;
            string       sProblem;
            ulong        ullStart;
            ulong        ullSize;

            if (!ADD_ParseSize(Add_txtStart.Text, out ullStart) || (ullStart % Add_objTable.uiSector) != 0)
            {   /* Not a size, or not on a sector. */
                Add_Complain("The start must be a byte count that is a multiple of the sector size (" + Add_objTable.uiSector + ").", Add_txtStart);
                return;
            }

            if (!ADD_ParseSize(Add_txtSize.Text, out ullSize) || ullSize == 0 || (ullSize % Add_objTable.uiSector) != 0)
            {   /* Not a size, zero, or not whole sectors. */
                Add_Complain("The size must be a byte count that is a multiple of the sector size (" + Add_objTable.uiSector + ").", Add_txtSize);
                return;
            }

            if (!Add_ParseType(out objType))
            {   /* Not a type. */
                Add_Complain("The type must be one of the named ones, a GUID (on a GPT) or a hexadecimal byte (on an MBR).", Add_cmbType);
                return;
            }

            if (Add_objTable.iKind == PTB_Tables.PTB_KIND_GPT && Add_txtName.Text.Length > PTB_Tables.PTB_GPT_NAME_CHARS)
            {   /* Too long for the field. */
                Add_Complain("The name can be at most " + PTB_Tables.PTB_GPT_NAME_CHARS + " characters.", Add_txtName);
                return;
            }

            /* The trial: a new table with the real one's facts and a NEW   */
            /* list of its entries, so adding the proposed entry cannot     */
            /* touch the real table before the check passes.  The entries   */
            /* themselves and the boot bytes are shared, not copied: the    */
            /* check only reads them.                                       */
            objTrial                 = new PTB_Table();
            objTrial.iKind           = Add_objTable.iKind;
            objTrial.uiSector        = Add_objTable.uiSector;
            objTrial.ullDiskBytes    = Add_objTable.ullDiskBytes;
            objTrial.abBoot          = Add_objTable.abBoot;
            objTrial.uiDiskSignature = Add_objTable.uiDiskSignature;
            objTrial.guidDisk        = Add_objTable.guidDisk;
            objTrial.ullFirstUsable  = Add_objTable.ullFirstUsable;
            objTrial.ullLastUsable   = Add_objTable.ullLastUsable;
            objTrial.ullEntriesLba   = Add_objTable.ullEntriesLba;
            objTrial.nSlots          = Add_objTable.nSlots;
            objTrial.cbEntry         = Add_objTable.cbEntry;
            objTrial.lstEntries      = new List<PTB_Entry>(Add_objTable.lstEntries);

            /* The new entry into the trial: its first and last sector from   */
            /* the bytes.  start + size is not tested for wrapping: a sum     */
            /* that wraps lands below the start (or underflows to a huge      */
            /* last sector), and PTB_Validate below refuses both.             */
            objEntry = PTB_Tables.PTB_AddEntry(objTrial, ullStart / Add_objTable.uiSector, ((ullStart + ullSize) / Add_objTable.uiSector) - 1, objType, Add_txtName.Text);

            if (objEntry == null)
            {   /* No slot left. */
                Add_Complain("The table has no free slot for another partition.", Add_cmbGap);
                return;
            }

            if (!PTB_Tables.PTB_Validate(objTrial, out sProblem))
            {   /* The model refuses it, and says why. */
                Add_Complain(sProblem, Add_txtStart);
                return;
            }

            /* Valid: the outcome set for the caller and the dialog          */
            /* closed.  The worker then adds the partition, with these       */
            /* same values, to its own copy of the table                     */
            /* (Sys_TableEditSequence, CommandOperations.cs); the window's   */
            /* table is left alone until that copy comes back.               */
            ADD_ullFirstLba = objEntry.ullFirstLba;
            ADD_ullLastLba  = objEntry.ullLastLba;
            ADD_objType        = objType;
            ADD_sName       = Add_txtName.Text;
            ADD_bBoot       = Add_chkBoot.Checked;
            ADD_bAccepted   = true;
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Add_Complain:                                                      */
        /*                                                                    */
        /* Say what is wrong and put the focus on the field to correct.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sWhat : the problem.                                           */
        /*     ctl   : the field.                                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the message is shown; the dialog stays open.            */
        /*--------------------------------------------------------------------*/
        private void Add_Complain(string sWhat, Control ctl)
        {
            /* Said in the dialog's font, then the field to correct focused. */
            MSG_clsMessageForm.MSG_Show(this, this.Font, sWhat, "Add a partition", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
            ctl.Focus();
        }

        /*--------------------------------------------------------------------*/
        /* Add_OnCancelClicked:                                               */
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
        private void Add_OnCancelClicked(object objSender, EventArgs evArgs)
        {
            /* Closed; ADD_bAccepted is still false from the constructor. */
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Add_OnResize:                                                      */
        /*                                                                    */
        /* The dialog's size changed -- not by the user (a fixed border), but */
        /* by its own sizing: the constructor's Add_FitLayout runs after this */
        /* is hooked.  Refit, detached while it does so.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the dialog.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the layout fits.                                        */
        /*--------------------------------------------------------------------*/
        private void Add_OnResize(object objSender, EventArgs evArgs)
        {
            /* Unhooked, refitted, hooked again: the refit's ClientSize   */
            /* raises Resize, which must not come back here.              */
            this.Resize -= Add_OnResize;
            Add_FitLayout();
            this.Resize += Add_OnResize;
        }
    }
}
