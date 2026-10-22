/*----------------------------------------------------------------------------*/
/* HexEditForm.cs                                                             */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The program's own hex editor, for ANY file inside an image -- or a Windows */
/* file opened directly -- a binary, or a text file whose exact bytes matter. */
/*                                                                            */
/*   HEX_clsHexView        the control that draws the bytes and takes the     */
/*                         typing: an offset column, sixteen bytes a row in   */
/*                         hex, and the same sixteen as characters.           */
/*   HEXF_clsHexEditForm   the window round it: a menu, a scroll bar, a       */
/*                         status bar, and the saves.                         */
/*   HSC_clsHexSaveCapture one save on its way: what it writes and where.     */
/*   HPI_clsHexPickIntent  one Save As waiting for its destination.           */
/*                                                                            */
/* IT OVERWRITES; IT DOES NOT INSERT OR DELETE.  A byte typed replaces the    */
/* byte under the caret, so the file keeps its length.  That is what makes it */
/* safe on a file of any size: the file is never held in memory.  The view    */
/* reads only the rows on screen from the file it shows -- an entry's         */
/* read-out copy, or the Windows file itself -- and keeps only the bytes that */
/* differ: those changed now, and those the last save holds.                  */
/*                                                                            */
/* A save takes a copy of the changed bytes (the patch) and the content's     */
/* revision, so typing after it cannot change what it writes.  An image       */
/* entry's save writes a snapshot -- the file read, with the patch applied -- */
/* for the worker to put in the image, and never rewrites the file read.  A   */
/* Windows file is written by this window: in place when it is the file being */
/* read, otherwise through a temporary copy put in the destination's place.   */
/* Once a save has succeeded, its destination is where Save writes next;      */
/* where the file was opened from is kept apart and does not change           */
/* (OpenScan.cs, ORG_clsOrigin).                                              */
/*----------------------------------------------------------------------------*/

/* The framework: the base types (Math, the time a save is stamped with,     */
/* the hex digit a key gives, the exceptions caught, event arguments), the   */
/* table of changed bytes, the font, sizes and colours, the Go To offset's   */
/* parsing, the file read, written and copied, and the window, the dump      */
/* control's base and their menus and controls.                              */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* HSC_clsHexSaveCapture -- one save on its way.                          */
    /*                                                                        */
    /* Everything a save needs, fixed when it begins (Hexf_SaveTo): its own   */
    /* descriptor, the patch it writes, the revision that is, its number in   */
    /* the window's order, and whether the window is to close once it has     */
    /* succeeded.  Its two methods are the save's callbacks -- the answer     */
    /* (HSC_OnSaved, the OFD_SaveDone handed over with it) and the close it   */
    /* may lead to (HSC_RunClose) -- so each reaches the window with THIS     */
    /* save's values, never whatever save the window started since.  It owns  */
    /* no file and no snapshot.                                               */
    /*------------------------------------------------------------------------*/
    internal sealed class HSC_clsHexSaveCapture
    {
        /* The save's values -- documentation table. */
        internal OFD_clsOpenedFile HSC_objSnapshot;          // the save's own descriptor (OFD_ForSave): where it writes, and the close question it answers, if any
        internal Dictionary<long, byte> HSC_dictPatch;       // the bytes it writes, offset to value, over the file the window reads; never changed after (the view keeps it as its saved table when the file read is not rewritten, HEX_MarkImageSaved)
        internal long HSC_lRevision;                         // the content revision it holds (HEX_Revision when it began)
        internal long HSC_lSaveOrder;                        // its number in the window's order of saves (from 1); an older one answered late must not undo a newer one
        internal bool HSC_bCloseWhenSaved;                   // true when the window's own close asked for it: once it succeeds, the window tries to close
        private readonly HEXF_clsHexEditForm Hsc_frmEditor;  // the window the save came from (not owned)

        /*--------------------------------------------------------------------*/
        /* HSC_clsHexSaveCapture (constructor):                               */
        /*                                                                    */
        /* Keep one save's values, as Hexf_SaveTo took them.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmEditor       : the window the save came from.               */
        /*     objSnapshot     : the save's own descriptor.                   */
        /*     dictPatch       : the save's copy of the changed bytes         */
        /*                       (HEX_CopyOverlay), kept as it is.            */
        /*     lRevision       : the revision that copy holds.                */
        /*     lSaveOrder      : the save's number in the window's order.     */
        /*     bCloseWhenSaved : true when the window's own close asked for   */
        /*                       it.                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the record.                                    */
        /*--------------------------------------------------------------------*/
        internal HSC_clsHexSaveCapture(HEXF_clsHexEditForm frmEditor,
            OFD_clsOpenedFile objSnapshot, Dictionary<long, byte> dictPatch,
            long lRevision, long lSaveOrder, bool bCloseWhenSaved)
        {
            /* The values kept as given.  The patch is the save's own copy,    */
            /* not the view's live table, so typing after this cannot change   */
            /* it.                                                             */
            Hsc_frmEditor = frmEditor;
            HSC_objSnapshot = objSnapshot;
            HSC_dictPatch = dictPatch;
            HSC_lRevision = lRevision;
            HSC_lSaveOrder = lSaveOrder;
            HSC_bCloseWhenSaved = bCloseWhenSaved;
        }

        /*--------------------------------------------------------------------*/
        /* HSC_OnSaved:                                                       */
        /*                                                                    */
        /* The save's answer (its OFD_SaveDone), called once when the save    */
        /* has ended: passed to the window with this save's values.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bSaved : true when the save succeeded.                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window has dealt with it (HEXF_SaveAcknowledged).   */
        /*--------------------------------------------------------------------*/
        internal void HSC_OnSaved(bool bSaved)
        {
            /* To the window, naming THIS save, not its newest one. */
            Hsc_frmEditor.HEXF_SaveAcknowledged(this, bSaved);
        }

        /*--------------------------------------------------------------------*/
        /* HSC_RunClose:                                                      */
        /*                                                                    */
        /* The close this save asked for, run on a later turn of the UI       */
        /* thread: Hexf_PostReadyCloses posts it once the window's own work   */
        /* and its closing handler have returned.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window has decided whether to close                 */
        /*            (HEXF_CompleteDeferredClose).                           */
        /*--------------------------------------------------------------------*/
        internal void HSC_RunClose()
        {
            /* To the window with this save's revision and order; the window   */
            /* reports the order back even when newer typing keeps it open.    */
            Hsc_frmEditor.HEXF_CompleteDeferredClose(this);
        }
    }

    /*-------------------------------------------------------------------------*/
    /* HPI_clsHexPickIntent -- one Save As waiting for its destination.        */
    /*                                                                         */
    /* Made when Save As asks the worker for a destination, and answered       */
    /* when the picker returns.  It keeps the window's binding at the time     */
    /* (a save that moves the window in between makes the answer void) and     */
    /* whether the window's own close asked for it.  Each Save As has its own, */
    /* so two waiting at once never share one slot.  It holds no content: the  */
    /* save is taken only when the destination arrives.                        */
    /*-------------------------------------------------------------------------*/
    internal sealed class HPI_clsHexPickIntent
    {
        /* The Save As's values -- documentation table. */
        internal OFD_clsOpenedFile HPI_objBinding;           // the window's descriptor when the Save As was asked; the answer is used only if it still is
        internal bool HPI_bCloseWhenSaved;                   // true when the user's own close asked for it (the close question's Save As): the save closes the window, and no choice asks the close again
        private readonly HEXF_clsHexEditForm Hpi_frmEditor;  // the window that asked (not owned)

        /*--------------------------------------------------------------------*/
        /* HPI_clsHexPickIntent (constructor):                                */
        /*                                                                    */
        /* Keep one Save As's values.                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmEditor       : the window that asked.                       */
        /*     objBinding      : its descriptor at the time.                  */
        /*     bCloseWhenSaved : true when its own close asked.               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the record; no content is taken yet.           */
        /*--------------------------------------------------------------------*/
        internal HPI_clsHexPickIntent(HEXF_clsHexEditForm frmEditor,
            OFD_clsOpenedFile objBinding, bool bCloseWhenSaved)
        {
            /* The values kept as given.  Asking for a destination changes   */
            /* nothing about where Save writes: only a save that succeeds    */
            /* does.                                                         */
            Hpi_frmEditor = frmEditor;
            HPI_objBinding = objBinding;
            HPI_bCloseWhenSaved = bCloseWhenSaved;
        }

        /*--------------------------------------------------------------------*/
        /* HPI_OnChosen:                                                      */
        /*                                                                    */
        /* The picker's answer (its TIN_TargetChosen): passed to the window   */
        /* with this Save As's values.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objTarget : the destination's descriptor; null when the picker */
        /*                 was cancelled or refused.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window has saved to it, or let it go -- asking the  */
        /*            close again when a close asked for the choice           */
        /*            (HEXF_ApplyTargetChoice).                               */
        /*--------------------------------------------------------------------*/
        internal void HPI_OnChosen(OFD_clsOpenedFile objTarget)
        {
            /* To the window, naming THIS Save As. */
            Hpi_frmEditor.HEXF_ApplyTargetChoice(this, objTarget);
        }
    }

    /*------------------------------------------------------------------------*/
    /* HEX_clsHexView -- the hex dump control.                                */
    /*                                                                        */
    /* A row is laid out in character cells of one fixed width:               */
    /*                                                                        */
    /*   OOOOOOOO  HH HH HH HH HH HH HH HH  HH HH HH HH HH HH HH HH  cccccccc */
    /*   offset    eight bytes, a gap, eight bytes                  the same  */
    /*                                                              sixteen   */
    /*                                                              as text   */
    /*                                                                        */
    /* The caret is on one BYTE and in one of the two panes (hex or text);    */
    /* Tab moves it between them.  In the hex pane two hex digits make a      */
    /* byte, high digit first; in the text pane one printable ASCII character */
    /* is a byte.  A byte that differs from the last saved content is drawn   */
    /* in red.                                                                */
    /*                                                                        */
    /* Derives from Control because the framework requires it: a control that */
    /* draws itself and takes its own keys and clicks is made only as a       */
    /* subclass of a WinForms control, whose hooks it overrides.  That is the */
    /* one kind of inheritance allowed here -- from a framework base class,   */
    /* never from a class of ours.                                            */
    /*------------------------------------------------------------------------*/
    internal sealed class HEX_clsHexView : Control
    {
        /* The row's width -- documentation table.  Every offset, row and   */
        /* screenful below is counted in it.  It is internal, but nothing   */
        /* outside this class reads it (the window works in rows, through   */
        /* HEX_TotalRows and the like).                                     */
        internal const int HEX_BYTES_PER_ROW = 16;        // bytes shown on one row

        /* The dump's look and behaviour -- documentation table.  Sizes in   */
        /* cells are multiplied by the measured cell width, so they keep     */
        /* their proportions at any text size; the margin is plain pixels.   */
        private const string HEX_FONT_NAME   = "Consolas";    // fixed-width, so every cell is the same width
        private const float  HEX_FONT_POINTS = 10.0f;         // the size the control starts at, points; the window replaces it at once with the remembered size (HEXF_FONT_DEFAULT on a first run)
        private const int    HEX_GAP_CELLS   = 2;             // blank cells between the offset column and the hex pane, and before the text pane
        private const int    HEX_HALF        = 8;             // an extra blank cell is left after this many bytes of a row
        private const int    HEX_MARGIN      = 4;             // blank pixels at the control's left and top edges
        private const int    HEX_WHEEL_ROWS  = 3;             // rows scrolled by one notch of the mouse wheel
        private const long   HEX_WIDE_FROM   = 0x100000000L;  // a file of this size or more needs more than eight offset digits
        private const int    HEX_MEASURE_LEN = 16;            // how many characters are measured to find one cell's width

        /* The one way the control talks to its window -- set by the window   */
        /* after the control is built, so changes made while the window is    */
        /* still setting it up are not reported.                              */
        private MethodInvoker Hex_pfnStateChanged;   // called whenever the caret, the scroll position or a byte changes; the window updates its bars from it

        /* The file, the edits, the view and the page read for it --         */
        /* documentation table.  Nothing else holds the file's bytes: only   */
        /* the rows on screen are read, and only the changed bytes kept.     */
        private FileStream             Hex_stmFile;        // the file shown -- an image entry's temporary copy, or a Windows file itself -- open for READING only (a save writes through a handle of its own)
        private long                   Hex_lLength;        // the file's length, bytes
        private Dictionary<long, byte> Hex_dictChanges;    // every byte that now differs from the file read, offset to value -- bytes an image save already holds included, since that save leaves the file read as it was
        private long                   Hex_lTopRow;        // the row at the top of the control (row n holds bytes 16n to 16n+15)
        private long                   Hex_lCaret;         // the byte the caret is on
        private bool                   Hex_bTextPane;      // true when the caret is in the text pane, false in the hex pane
        private bool                   Hex_bLowDigit;      // hex pane: true when the next digit typed is the byte's LOW digit
        private int                    Hex_iCellWidth;     // the width of one character cell, pixels
        private int                    Hex_iRowHeight;     // the height of one row, pixels
        private int                    Hex_iOffsetDigits;  // how many hex digits the offset column shows (8, or 12 for a file of 4 GiB or more)
        private byte[]                 Hex_abPage;         // the file's bytes for the rows now on screen
        private long                   Hex_lPageOffset;    // the file offset Hex_abPage starts at; -1 when it holds nothing
        private Dictionary<long, byte> Hex_dictSaved;      // the bytes the last successful save holds that differ from the file read (its patch, HEX_MarkImageSaved); empty when the file read holds them; shared with that save, never changed in place
        private int Hex_iUnsaved;                          // how many bytes now differ from the last saved content: the status bar's "changed" count
        private long Hex_lRevision;                        // the content's revision: stepped by every byte actually changed, never by a caret move or a scroll; a save records it
        private int                    Hex_iPageBytes;     // how many bytes of Hex_abPage are the file's

        /*--------------------------------------------------------------------*/
        /* HEX_clsHexView (constructor):                                      */
        /*                                                                    */
        /* Create the control over an open file.  It paints itself            */
        /* double-buffered, so a scroll does not flicker, and takes the       */
        /* focus when clicked, so typing goes to it.                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stmFile : the file to show, open for reading.  The control     */
        /*               uses it but does not close it; its window does.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the control, showing the start of the file.    */
        /*--------------------------------------------------------------------*/
        internal HEX_clsHexView(FileStream stmFile)
        {
            /* The file kept (not owned: the window closes it) with its       */
            /* length, which an overwriting editor never changes; no byte     */
            /* changed, none saved, none unsaved, revision 0; the top row     */
            /* and the caret at the start, in the hex pane on a byte's high   */
            /* digit; and no page read yet (-1), so the first paint reads     */
            /* one.                                                           */
            Hex_stmFile     = stmFile;
            Hex_lLength     = stmFile.Length;
            Hex_dictChanges = new Dictionary<long, byte>();
            Hex_dictSaved = new Dictionary<long, byte>();
            Hex_iUnsaved = 0;
            Hex_lRevision = 0;
            Hex_lTopRow     = 0;
            Hex_lCaret      = 0;
            Hex_bTextPane   = false;
            Hex_bLowDigit   = false;
            Hex_abPage      = new byte[0];
            Hex_lPageOffset = -1;
            Hex_iPageBytes  = 0;

            /* The offset column: eight hex digits reach 4 GiB, which is   */
            /* every file but the largest.                                 */
            Hex_iOffsetDigits = 8;

            if (Hex_lLength >= HEX_WIDE_FROM)
            {   /* Offsets past 4 GiB do not fit in eight digits. */
                Hex_iOffsetDigits = 12;
            }

            /* The fixed-width face at its default size (the window then    */
            /* applies the remembered one, HEX_SetFontPoints), the window   */
            /* colour behind it, and the styles: drawn by this control      */
            /* (UserPaint), double-buffered with every paint in WM_PAINT    */
            /* so a scroll does not flicker, redrawn whole on a resize,     */
            /* and able to take the focus -- with a tab stop -- so typing   */
            /* comes here.                                                  */
            this.Font      = new Font(HEX_FONT_NAME, HEX_FONT_POINTS);
            this.BackColor = SystemColors.Window;
            this.SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                          ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            this.TabStop = true;

            /* One cell's size, measured in the font actually in use. */
            Hex_iCellWidth = TextRenderer.MeasureText(new string('0', HEX_MEASURE_LEN), this.Font, Size.Empty,
                                                      TextFormatFlags.NoPadding).Width / HEX_MEASURE_LEN;
            Hex_iRowHeight = this.Font.Height;
        }

        /*--------------------------------------------------------------------*/
        /* HEX_SetFontPoints:                                                 */
        /*                                                                    */
        /* Show the dump at another text size.  Everything is laid out in     */
        /* character cells, so the cell's size is measured again in the new   */
        /* font; more or fewer rows then fit, so the caret is brought back    */
        /* into view and the window is told (its scroll bar counts rows).     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iPoints : the text size, points.                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the dump is drawn at that size.                         */
        /*--------------------------------------------------------------------*/
        internal void HEX_SetFontPoints(int iPoints)
        {
            /* The fixed-width face at the new size.  The font it replaces   */
            /* is not disposed here; it is left to the collector.            */
            this.Font = new Font(HEX_FONT_NAME, (float)iPoints);

            /* One cell measured again in that font: the width of sixteen    */
            /* digits over sixteen, with no padding, so the rounding of a    */
            /* single digit's width does not add up across a row; a row is   */
            /* the font's height.                                            */
            Hex_iCellWidth = TextRenderer.MeasureText(new string('0', HEX_MEASURE_LEN), this.Font, Size.Empty,
                                                      TextFormatFlags.NoPadding).Width / HEX_MEASURE_LEN;
            Hex_iRowHeight = this.Font.Height;

            /* With smaller text more rows fit: a view near the end of the     */
            /* file is brought back so the last screenful is full.  Then the   */
            /* caret's row is scrolled into view if it no longer fits; that    */
            /* also repaints and tells the window.                             */
            HEX_SetTopRow(Hex_lTopRow);
            HEX_GoTo(Hex_lCaret);
        }

        /*--------------------------------------------------------------------*/
        /* HEX_TotalRows:                                                     */
        /*                                                                    */
        /* How many rows the whole file takes.                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     long : the row count; 0 for an empty file.                     */
        /*--------------------------------------------------------------------*/
        internal long HEX_TotalRows()
        {
            /* Rounded up: a last, partly filled row counts. */
            return((Hex_lLength + HEX_BYTES_PER_ROW - 1) / HEX_BYTES_PER_ROW);
        }

        /*--------------------------------------------------------------------*/
        /* HEX_VisibleRows:                                                   */
        /*                                                                    */
        /* How many WHOLE rows fit in the control at its present height.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : the number of rows, at least 1.                          */
        /*--------------------------------------------------------------------*/
        internal int HEX_VisibleRows()
        {
            int iRows;

            /* The height below the top margin, in whole rows (a part-row   */
            /* at the bottom is not counted).                               */
            iRows = (this.ClientSize.Height - HEX_MARGIN) / Hex_iRowHeight;

            if (iRows < 1)
            {   /* A control squeezed to nothing still scrolls by one row. */
                iRows = 1;
            }

            /* The rows that fit. */
            return(iRows);
        }

        /*--------------------------------------------------------------------*/
        /* HEX_PreferredWidth:                                                */
        /*                                                                    */
        /* The width the control needs to show a whole row.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : the width, pixels.                                       */
        /*--------------------------------------------------------------------*/
        internal int HEX_PreferredWidth()
        {
            /* The text pane's last cell ends the row; a margin either side. */
            return(Hex_TextCell(HEX_BYTES_PER_ROW) * Hex_iCellWidth + (2 * HEX_MARGIN));
        }

        /*--------------------------------------------------------------------*/
        /* HEX_TopRow:                                                        */
        /*                                                                    */
        /* The row now at the top of the control.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     long : the row's number, from 0.                               */
        /*--------------------------------------------------------------------*/
        internal long HEX_TopRow()
        {
            /* As last set by a scroll or by following the caret. */
            return(Hex_lTopRow);
        }

        /*--------------------------------------------------------------------*/
        /* HEX_CaretOffset:                                                   */
        /*                                                                    */
        /* The offset of the byte the caret is on.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     long : the offset; 0 in an empty file, which has no bytes.     */
        /*--------------------------------------------------------------------*/
        internal long HEX_CaretOffset()
        {
            /* Always inside the file, or 0. */
            return(Hex_lCaret);
        }

        /*--------------------------------------------------------------------*/
        /* HEX_FileLength:                                                    */
        /*                                                                    */
        /* The file's length.                                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     long : the length, bytes; it never changes while the file is   */
        /*            open here.                                              */
        /*--------------------------------------------------------------------*/
        internal long HEX_FileLength()
        {
            /* Taken when the file was opened. */
            return(Hex_lLength);
        }

        /*--------------------------------------------------------------------*/
        /* HEX_ChangeCount:                                                   */
        /*                                                                    */
        /* How many bytes differ from the file as last saved.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : the number of changed bytes.                             */
        /*--------------------------------------------------------------------*/
        internal int HEX_ChangeCount()
        {
            /* Kept up to date by every byte changed and every save, so the   */
            /* status bar, which asks on every caret move, costs no count.    */
            return(Hex_iUnsaved);
        }

        /*--------------------------------------------------------------------*/
        /* HEX_SetTopRow:                                                     */
        /*                                                                    */
        /* Scroll so that a given row is at the top.  The row is brought      */
        /* inside the file first: the last screenful is as far down as the    */
        /* view goes.                                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lRow : the row wanted at the top.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the view is scrolled and repainted.                     */
        /*--------------------------------------------------------------------*/
        internal void HEX_SetTopRow(long lRow)
        {
            long lLast;

            /* The lowest top row there is: the one that puts the file's   */
            /* last row at the bottom.  Negative for a file shorter than   */
            /* the screen, which the second test below makes 0.            */
            lLast = HEX_TotalRows() - HEX_VisibleRows();

            if (lRow > lLast)
            {   /* Past the last screenful. */
                lRow = lLast;
            }

            if (lRow < 0)
            {   /* Above the start (also what a file shorter than the screen gives). */
                lRow = 0;
            }

            if (lRow == Hex_lTopRow)
            {   /* Already there: no repaint, and the window is not told. */
                return;
            }

            /* Scrolled: the new top kept, the control repainted from it,   */
            /* and the window told, so its scroll bar follows.              */
            Hex_lTopRow = lRow;
            this.Invalidate();
            Hex_NotifyState();
        }

        /*--------------------------------------------------------------------*/
        /* HEX_GoTo:                                                          */
        /*                                                                    */
        /* Put the caret on a given byte and bring it into view.  An offset   */
        /* outside the file is brought to its nearest end.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lOffset : the byte's offset.                                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the caret is there and showing.                         */
        /*--------------------------------------------------------------------*/
        internal void HEX_GoTo(long lOffset)
        {
            long lRow;

            if (lOffset >= Hex_lLength)
            {   /* Past the end: the last byte. */
                lOffset = Hex_lLength - 1;
            }

            if (lOffset < 0)
            {   /* Before the start (or an empty file). */
                lOffset = 0;
            }

            /* The caret on that byte, and back on its HIGH digit: a byte   */
            /* half typed elsewhere is not carried to the new one.          */
            Hex_lCaret    = lOffset;
            Hex_bLowDigit = false;

            /* Scroll just far enough that the caret's row is on screen. */
            lRow = Hex_lCaret / HEX_BYTES_PER_ROW;

            if (lRow < Hex_lTopRow)
            {   /* Above the top: it becomes the top row. */
                Hex_lTopRow = lRow;
            }
            else if (lRow >= Hex_lTopRow + HEX_VisibleRows())
            {   /* Below the bottom: it becomes the bottom row. */
                Hex_lTopRow = lRow - HEX_VisibleRows() + 1;
            }

            /* Repainted -- the caret moved even when the view did not --   */
            /* and the window told, so its bars follow.                     */
            this.Invalidate();
            Hex_NotifyState();
        }

        /*--------------------------------------------------------------------*/
        /* HEX_ByteAt:                                                        */
        /*                                                                    */
        /* The value a byte has NOW: the user's change when there is one,     */
        /* otherwise what the file holds.                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lOffset : the byte's offset, inside the file.                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte : its value.                                              */
        /*--------------------------------------------------------------------*/
        internal byte HEX_ByteAt(long lOffset)
        {
            byte bValue;

            if (Hex_dictChanges.TryGetValue(lOffset, out bValue))
            {   /* Changed: the changed value -- also when an image save     */
                /* already holds it, since the file read still has the old   */
                /* one.                                                      */
                return(bValue);
            }

            /* Unchanged: the file's own value, from the page on screen when   */
            /* it is there, else read on its own (Hex_FileByte).               */
            return(Hex_FileByte(lOffset));
        }

        /*--------------------------------------------------------------------*/
        /* HEX_WriteOverlay:                                                  */
        /*                                                                    */
        /* Write a save's changed bytes into a Windows file of the same       */
        /* length, each at its offset: the file being read, for a save in     */
        /* place, or the temporary copy a Save As builds (Hexf_WriteWindows). */
        /* Static: it reads nothing of a view.  A failure part-way leaves the */
        /* bytes before it written; the caller reports it, and the patch is   */
        /* unchanged.                                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sFile     : the file to write into; it exists and has the      */
        /*                 file read's length.                                */
        /*     dictPatch : the save's changed bytes, offset to value; not     */
        /*                 changed here.                                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the bytes are written; a failure throws.                */
        /*--------------------------------------------------------------------*/
        internal static void HEX_WriteOverlay(string sFile, Dictionary<long, byte> dictPatch)
        {
            FileStream stmWrite;
            Dictionary<long, byte>.Enumerator stIterator;
            KeyValuePair<long, byte> stPatch;

            /* A write handle of its own, sharing read and write so the      */
            /* window's read handle on the same file stays open beside it.   */
            /* FileMode.Open never truncates, so the length stays.           */
            stmWrite = new FileStream(sFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            using (stmWrite)
            {   /* The handle closed when the block ends, a failed write   */
                /* included.  The table walked directly with its own       */
                /* enumerator, so no copy of its keys is made.             */
                stIterator = dictPatch.GetEnumerator();
                try
                {   /* The walk, released in the finally whatever happens. */
                    while (stIterator.MoveNext())
                    {   /* One changed byte per iteration, put at its offset. */
                        stPatch = stIterator.Current;
                        stmWrite.Position = stPatch.Key;
                        stmWrite.WriteByte(stPatch.Value);
                    }
                }
                finally
                {   /* The enumerator released, a failed write included. */
                    stIterator.Dispose();
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* HEX_SetFile:                                                       */
        /*                                                                    */
        /* Read from another file from now on: after a Save As, the copy that */
        /* was saved to.  That file holds the same bytes this view has been   */
        /* showing -- the old file's, with the changes written in -- so its   */
        /* length is the same and the caret and scroll position stay where    */
        /* they are; only what was read ahead from the old file is dropped.   */
        /* The caller closes the old stream, and calls HEX_ForgetChanges,     */
        /* since the changes are IN the new file.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stmFile : the new file, open for reading.                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the view reads from stmFile.                            */
        /*--------------------------------------------------------------------*/
        internal void HEX_SetFile(FileStream stmFile)
        {
            /* The new file kept, and the page read from the old one marked   */
            /* empty, so the next paint reads the new file; then repainted.   */
            /* The length, the caret and the top row are left alone -- the    */
            /* two files are the same length -- so the window is not told.    */
            Hex_stmFile     = stmFile;
            Hex_lPageOffset = -1;
            this.Invalidate();
        }

        /*--------------------------------------------------------------------*/
        /* HEX_ForgetChanges:                                                 */
        /*                                                                    */
        /* A Windows save has put exactly the bytes shown into the file read: */
        /* nothing differs from it any more, so both tables are emptied.  A   */
        /* save that leaves the file read as it was (an image entry's) uses   */
        /* HEX_MarkImageSaved instead.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : no byte is marked as changed.                           */
        /*--------------------------------------------------------------------*/
        internal void HEX_ForgetChanges()
        {
            /* The changed bytes are in the file now, so the table is          */
            /* cleared.  The saved table is REPLACED by a new empty one, not   */
            /* cleared: it can be a save's own patch (HSC_dictPatch), which    */
            /* that save may still hold.  The count to 0, and the page         */
            /* dropped so the next paint reads the bytes now in the file;      */
            /* then repainted, and the window told.                            */
            Hex_dictChanges.Clear();
            Hex_dictSaved = new Dictionary<long, byte>();
            Hex_iUnsaved = 0;
            Hex_lPageOffset = -1;
            this.Invalidate();
            Hex_NotifyState();
        }

        /*--------------------------------------------------------------------*/
        /* HEX_FileRewritten:                                                 */
        /*                                                                    */
        /* The file read has just been written to in place (a Windows save    */
        /* onto it, Hexf_WriteWindows): the page read for the screen may hold */
        /* the bytes from before the write, so it is dropped and the next     */
        /* paint or byte asked for reads the file as it is now.  Without      */
        /* this, a byte typed while the save waits for its answer would be    */
        /* compared with a value the file no longer holds (Hex_SetByte), and  */
        /* a byte typed back to that old value would be dropped from the      */
        /* changed table although the file now differs from it.  The tables   */
        /* are left as they are: the save's answer brings them into line      */
        /* (HEX_ForgetChanges or HEX_RebaseOnFile).                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the next read of a file byte goes to the file.          */
        /*--------------------------------------------------------------------*/
        internal void HEX_FileRewritten()
        {
            /* The page marked empty, so Hex_LoadPage reads it again; then   */
            /* repainted, as bytes drawn from the old page may have moved.   */
            Hex_lPageOffset = -1;
            this.Invalidate();
        }

        /*--------------------------------------------------------------------*/
        /* HEX_RebaseOnFile:                                                  */
        /*                                                                    */
        /* A Windows save written IN PLACE has succeeded, but bytes were      */
        /* typed while it waited for its answer: the file read now holds the  */
        /* saved content, and the view still shows newer bytes.  The view's   */
        /* tables are brought into line with the file as it is now: nothing   */
        /* held as saved, and only the bytes that differ from the file marked */
        /* as changed.  Not used when nothing was typed since                 */
        /* (HEX_ForgetChanges), nor for a save that leaves the file read as   */
        /* it was (HEX_MarkImageSaved).                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the tables and the count describe the file as it is     */
        /*            now; the bytes shown are unchanged.                     */
        /*--------------------------------------------------------------------*/
        internal void HEX_RebaseOnFile()
        {
            Dictionary<long, byte>.Enumerator stIterator;
            KeyValuePair<long, byte> stEntry;
            List<long> lstSame;
            int i;

            /* The page dropped first, so every file byte read below is the     */
            /* file's new value, and the saved table REPLACED by an empty       */
            /* one, since the file read IS the saved content now -- replaced,   */
            /* not cleared: it can be an earlier save's own patch, which that   */
            /* save may still hold.                                             */
            Hex_lPageOffset = -1;
            Hex_dictSaved = new Dictionary<long, byte>();

            /* The entries the file now holds, collected first: a table   */
            /* cannot lose entries while it is being walked.              */
            lstSame = new List<long>();
            stIterator = Hex_dictChanges.GetEnumerator();
            try
            {   /* The walk, released in the finally whatever happens. */
                while (stIterator.MoveNext())
                {   /* One changed byte per iteration. */
                    stEntry = stIterator.Current;
                    if (Hex_FileByte(stEntry.Key) == stEntry.Value)
                    {   /* The file now holds this value: no longer a change. */
                        lstSame.Add(stEntry.Key);
                    }
                }
            }
            finally
            {   /* The walk's enumerator released. */
                stIterator.Dispose();
            }

            /* Those entries removed.  Every other entry is kept, since each   */
            /* differs from what the file now holds -- a byte typed to a new   */
            /* value, and one typed back to the value from before the save,    */
            /* alike.                                                          */
            for (i = 0; i < lstSame.Count; i++)
            {   /* One entry the file now holds per iteration. */
                Hex_dictChanges.Remove(lstSame[i]);
            }

            /* Unsaved is now "differs from the file read", which is every    */
            /* entry left; the revision is not touched (the bytes shown did   */
            /* not change).  Repainted for the red bytes, and the window      */
            /* told for the count.                                            */
            Hex_iUnsaved = Hex_dictChanges.Count;
            this.Invalidate();
            Hex_NotifyState();
        }

        /*--------------------------------------------------------------------*/
        /* HEX_Revision:                                                      */
        /*                                                                    */
        /* The content's revision, for a save to record and for its answer    */
        /* to be compared with: equal means no byte has changed since the     */
        /* save began.                                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     long : the revision; a save's success does not change it.      */
        /*--------------------------------------------------------------------*/
        internal long HEX_Revision()
        {
            /* Stepped only by a changed byte (Hex_SetByte). */
            return(Hex_lRevision);
        }

        /*--------------------------------------------------------------------*/
        /* HEX_CopyOverlay:                                                   */
        /*                                                                    */
        /* A copy of the changed-bytes table, for a save to keep as its       */
        /* patch: typing after the save then changes the view's table and not */
        /* the save's.  Only the changed bytes are copied, never the file.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Dictionary<long, byte> : the copy, which nothing changes       */
        /*                              afterwards.                           */
        /*--------------------------------------------------------------------*/
        internal Dictionary<long, byte> HEX_CopyOverlay()
        {
            /* A new table with the same entries. */
            return(new Dictionary<long, byte>(Hex_dictChanges));
        }

        /*--------------------------------------------------------------------*/
        /* HEX_MarkImageSaved:                                                */
        /*                                                                    */
        /* A save that leaves the file read as it was has succeeded -- an     */
        /* image entry's, or a Windows Save As whose new file is not being    */
        /* read: its patch becomes the saved content that unsaved bytes are   */
        /* counted against and drawn red by.  The bytes shown are not         */
        /* touched, nor is the revision.                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     dictSaved : the save's patch (HSC_dictPatch), kept as the      */
        /*                 saved table and never changed.                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the count and the red bytes follow the new saved        */
        /*            content.                                                */
        /*--------------------------------------------------------------------*/
        internal void HEX_MarkImageSaved(Dictionary<long, byte> dictSaved)
        {
            Dictionary<long, byte>.Enumerator stIterator;
            KeyValuePair<long, byte> stPatch;
            byte bSaved;

            /* The save's own table taken as the saved one -- both tables   */
            /* are over the same file read, and nothing changes a patch     */
            /* once taken, so no copy is needed -- and the unsaved bytes    */
            /* counted afresh, in two walks.                                */
            Hex_dictSaved = dictSaved;
            Hex_iUnsaved = 0;
            stIterator = Hex_dictChanges.GetEnumerator();
            try
            {   /* Walk 1, the changed bytes, released in the finally. */
                while (stIterator.MoveNext())
                {   /* One changed byte per iteration. */
                    stPatch = stIterator.Current;
                    if (!Hex_dictSaved.TryGetValue(stPatch.Key, out bSaved) || bSaved != stPatch.Value)
                    {   /* Unsaved: the save held the file's own byte there   */
                        /* (no entry), or a different value.                  */
                        Hex_iUnsaved++;
                    }
                }
            }
            finally
            {   /* The walk's enumerator released. */
                stIterator.Dispose();
            }

            /* Walk 2: a byte the save changed but which is back at the    */
            /* file's own value now (no entry in the changed table) also   */
            /* differs from the saved content, and is counted too.         */
            stIterator = Hex_dictSaved.GetEnumerator();
            try
            {   /* The saved bytes, released in the finally. */
                while (stIterator.MoveNext())
                {   /* One saved byte per iteration. */
                    stPatch = stIterator.Current;
                    if (!Hex_dictChanges.ContainsKey(stPatch.Key))
                    {   /* Typed back to the file's own value: unsaved. */
                        Hex_iUnsaved++;
                    }
                }
            }
            finally
            {   /* The walk's enumerator released. */
                stIterator.Dispose();
            }

            /* Repainted, as the red bytes may have changed, and the window   */
            /* told, as the count has.                                        */
            this.Invalidate();
            Hex_NotifyState();
        }

        /*--------------------------------------------------------------------*/
        /* Hex_SavedByte:                                                     */
        /*                                                                    */
        /* A byte as the last saved content holds it.                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lOffset : the byte's offset, inside the file.                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte : the saved table's value, or the file read's own.        */
        /*--------------------------------------------------------------------*/
        private byte Hex_SavedByte(long lOffset)
        {
            byte bSaved;

            if (Hex_dictSaved.TryGetValue(lOffset, out bSaved))
            {   /* A save changed it and left the file read as it was. */
                return(bSaved);
            }

            /* Not in the saved table: the save held the file's own byte. */
            return(Hex_FileByte(lOffset));
        }

        /*--------------------------------------------------------------------*/
        /* HEX_BindStateChange:                                               */
        /*                                                                    */
        /* Set the window's callback (Hexf_OnViewChanged).  The window sets   */
        /* it once it has built itself, so its own setting-up is not          */
        /* reported.                                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pfnChanged : called whenever the caret, the scroll position or */
        /*                  a byte changes.                                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : later changes are reported.                             */
        /*--------------------------------------------------------------------*/
        internal void HEX_BindStateChange(MethodInvoker pfnChanged)
        {
            /* Kept; Hex_NotifyState calls it. */
            Hex_pfnStateChanged = pfnChanged;
        }

        /*--------------------------------------------------------------------*/
        /* Hex_NotifyState:                                                   */
        /*                                                                    */
        /* Tell the window that something it shows has changed.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window's callback has run, if it set one.           */
        /*--------------------------------------------------------------------*/
        private void Hex_NotifyState()
        {
            if (Hex_pfnStateChanged != null)
            {   /* The window is listening. */
                Hex_pfnStateChanged();
            }
        }

        /*--------------------------------------------------------------------*/
        /* Hex_LoadPage:                                                      */
        /*                                                                    */
        /* Make sure the bytes for the rows on screen have been read from the */
        /* file.  They are read again only when the view has scrolled or the  */
        /* file has been written to.                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : Hex_abPage holds the screen's rows (or as many as the   */
        /*            file could give; a read failure leaves it short).       */
        /*--------------------------------------------------------------------*/
        private void Hex_LoadPage()
        {
            long lOffset;
            int  iWanted;
            int  iGot;

            /* The screen's rows: from the top row's first byte, the whole    */
            /* rows that fit plus one -- the part-row OnPaint also draws at   */
            /* the bottom (HEX_VisibleRows counts whole rows only).           */
            lOffset = Hex_lTopRow * HEX_BYTES_PER_ROW;
            iWanted = (HEX_VisibleRows() + 1) * HEX_BYTES_PER_ROW;

            if (lOffset == Hex_lPageOffset && Hex_abPage.Length == iWanted)
            {   /* The page already holds exactly these rows. */
                return;
            }

            if (Hex_abPage.Length != iWanted)
            {   /* The control's height (or the text size) changed: a      */
                /* buffer of the new size.  The same one is reused while   */
                /* the size holds, so scrolling allocates nothing.         */
                Hex_abPage = new byte[iWanted];
            }

            /* The page now stands for these rows, empty until read.  It is   */
            /* marked BEFORE the read, so a read that fails part-way is not   */
            /* tried again on every paint: the short page stands until the    */
            /* view scrolls, resizes or the file is written.                  */
            Hex_lPageOffset = lOffset;
            Hex_iPageBytes  = 0;

            try
            {   /* The file can fail like any file.  Only an I/O failure is   */
                /* caught below; anything else reaches the paint.             */
                Hex_stmFile.Position = lOffset;

                for (;;)
                {   /* One read per iteration: a read may return less than was asked for. */
                    iGot = Hex_stmFile.Read(Hex_abPage, Hex_iPageBytes, iWanted - Hex_iPageBytes);

                    if (iGot <= 0)
                    {   /* The end of the file: the last screen is short. */
                        break;
                    }

                    /* What arrived is added to the page's count. */
                    Hex_iPageBytes = Hex_iPageBytes + iGot;

                    if (Hex_iPageBytes == iWanted)
                    {   /* The page is full. */
                        break;
                    }
                }
            }
            catch (IOException)
            {   /* What was read is kept.  The bytes past it are not in the   */
                /* page, so Hex_FileByte reads each of them on its own, and   */
                /* one that cannot be read shows as 00.                       */
            }
        }

        /*--------------------------------------------------------------------*/
        /* Hex_FileByte:                                                      */
        /*                                                                    */
        /* A byte as the FILE holds it, ignoring the user's changes.  Bytes   */
        /* on screen come from the page already read; any other is read from  */
        /* the file.                                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lOffset : the byte's offset, inside the file.                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte : its value; 0 if the file could not be read.             */
        /*--------------------------------------------------------------------*/
        private byte Hex_FileByte(long lOffset)
        {
            int iValue;

            /* The screen's rows first (read only if they are not already),   */
            /* so a byte on screen -- every byte a paint asks for -- costs    */
            /* no read of its own.                                            */
            Hex_LoadPage();

            if (lOffset >= Hex_lPageOffset && lOffset < Hex_lPageOffset + Hex_iPageBytes)
            {   /* On screen. */
                return(Hex_abPage[(int)(lOffset - Hex_lPageOffset)]);
            }

            try
            {   /* Off screen, or past a page that was read short: one byte   */
                /* from the file.  Moving the shared position is harmless:    */
                /* Hex_LoadPage sets it again before every read of its own.   */
                Hex_stmFile.Position = lOffset;
                iValue               = Hex_stmFile.ReadByte();
            }
            catch (IOException)
            {   /* Unreadable: taken as "no byte", like the end of the file. */
                iValue = -1;
            }

            if (iValue < 0)
            {   /* Past the end, or a failed read: shown as 0, which cannot   */
                /* be told from a real 0 on screen.  Hex_SetByte compares     */
                /* against this value, so typing 00 over such a byte is not   */
                /* recorded as a change.                                      */
                return(0);
            }

            /* The byte read. */
            return((byte)iValue);
        }

        /*--------------------------------------------------------------------*/
        /* Hex_HexCell:                                                       */
        /*                                                                    */
        /* The character cell at which a row's nth byte starts in the HEX     */
        /* pane.  Each byte takes three cells (two digits and a blank), and   */
        /* one more blank cell separates the two halves of the row.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iByte : the byte's place in its row, 0 to 15 (16 gives the     */
        /*             cell just past the pane).                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : the cell, counted from the row's left end.               */
        /*--------------------------------------------------------------------*/
        private int Hex_HexCell(int iByte)
        {
            int iCell;

            /* Past the offset column and the gap after it, then three cells   */
            /* for each byte before this one.                                  */
            iCell = Hex_iOffsetDigits + HEX_GAP_CELLS + (iByte * 3);

            if (iByte >= HEX_HALF)
            {   /* In the second half: past the extra blank. */
                iCell++;
            }

            /* Where the byte's high digit is drawn. */
            return(iCell);
        }

        /*--------------------------------------------------------------------*/
        /* Hex_TextCell:                                                      */
        /*                                                                    */
        /* The character cell of a row's nth byte in the TEXT pane, which     */
        /* starts a gap after the hex pane ends.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iByte : the byte's place in its row, 0 to 15 (16 gives the     */
        /*             cell just past the pane).                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : the cell, counted from the row's left end.               */
        /*--------------------------------------------------------------------*/
        private int Hex_TextCell(int iByte)
        {
            /* The hex pane's end, less the blank its last byte carries, the gap, then one cell per byte. */
            return(Hex_HexCell(HEX_BYTES_PER_ROW) - 1 + HEX_GAP_CELLS + iByte);
        }

        /*--------------------------------------------------------------------*/
        /* Hex_SetByte:                                                       */
        /*                                                                    */
        /* Give one byte a new value, keeping the revision and the unsaved    */
        /* count right.  Both tables are over the same file read, so a byte   */
        /* typed back to the file's own value leaves the changed table -- and */
        /* may still be unsaved, when the last save had changed it.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lOffset : the byte's offset, inside the file.                  */
        /*     bValue  : its new value.                                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the byte, the revision and the count are updated.       */
        /*--------------------------------------------------------------------*/
        private void Hex_SetByte(long lOffset, byte bValue)
        {
            byte bPrevious;
            byte bSaved;

            /* The byte's value now and as last saved, read BEFORE either   */
            /* table changes.  The revision steps only when the value       */
            /* really changes: typing the same digit again is no edit.      */
            bPrevious = HEX_ByteAt(lOffset);
            bSaved = Hex_SavedByte(lOffset);
            if (bPrevious != bValue)
            {   /* A real change: a new revision, so a save already on its   */
                /* way is known to hold older content.                       */
                Hex_lRevision = checked(Hex_lRevision + 1);
            }

            /* The count is of bytes that differ from the SAVED content, so   */
            /* it moves only when this byte crosses that line -- not by       */
            /* whether it differs from the file read.                         */
            if (bPrevious == bSaved && bValue != bSaved)
            {   /* It matched the saved content and no longer does. */
                Hex_iUnsaved++;
            }
            else if (bPrevious != bSaved && bValue == bSaved)
            {   /* It differed, and is back at the saved value. */
                Hex_iUnsaved--;
            }

            if (Hex_FileByte(lOffset) == bValue)
            {   /* The file read's own value: no entry needed, and any one   */
                /* there is removed.                                         */
                Hex_dictChanges.Remove(lOffset);
            }
            else
            {   /* Another value: kept in the changed table.  A save on its   */
                /* way has its own copy, so it is not touched.                */
                Hex_dictChanges[lOffset] = bValue;
            }
        }

        /*--------------------------------------------------------------------*/
        /* The Framework's hooks.  Each override below only hands the event   */
        /* to the Hex_On... helper after it; the helper makes the base call   */
        /* itself, in the order its own comments give, so every base event    */
        /* runs exactly once -- a base call in an override as well would run  */
        /* it twice.                                                          */
        /*--------------------------------------------------------------------*/

        /*--------------------------------------------------------------------*/
        /* OnPaint:                                                           */
        /*                                                                    */
        /* The Framework's paint hook: hands the paint to Hex_OnPaint.        */
        /*                                                                    */
        /* An override because the framework requires it: a control draws     */
        /* itself only through this virtual method.                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     evPaint : the paint event; its Graphics is the surface.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the visible rows are drawn.                             */
        /*--------------------------------------------------------------------*/
        protected override void OnPaint(PaintEventArgs evPaint)
        {
            /* The drawing and the base call are in the helper. */
            Hex_OnPaint(evPaint);
        }

        /*--------------------------------------------------------------------*/
        /* IsInputKey:                                                        */
        /*                                                                    */
        /* The Framework's question: does this key belong to the control      */
        /* rather than to the form's focus navigation?  Hex_IsInputKey holds  */
        /* the one list of claimed keys (the arrows and Tab, which move the   */
        /* caret) and asks the base for the rest.                             */
        /*                                                                    */
        /* An override because the framework requires it: a control claims    */
        /* the arrows and Tab from focus navigation only through this virtual */
        /* method.                                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     enmKeyData : the key code with its modifier flags.             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the control takes the key.                    */
        /*--------------------------------------------------------------------*/
        protected override bool IsInputKey(Keys enmKeyData)
        {
            /* The policy, and the base fallback, are in the helper. */
            return(Hex_IsInputKey(enmKeyData));
        }

        /*--------------------------------------------------------------------*/
        /* OnKeyDown:                                                         */
        /*                                                                    */
        /* The Framework's key hook: hands the key to Hex_OnKeyDown, which    */
        /* moves the caret and marks the keys it used as handled.  The same   */
        /* event object is passed, so the marks reach the Framework.          */
        /*                                                                    */
        /* An override because the framework requires it: a control receives  */
        /* its own key events first only through this virtual method.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     evKey : the key event; the helper may mark it handled.         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the key has been dealt with.                            */
        /*--------------------------------------------------------------------*/
        protected override void OnKeyDown(KeyEventArgs evKey)
        {
            /* Caret movement and the base call are in the helper. */
            Hex_OnKeyDown(evKey);
        }

        /*--------------------------------------------------------------------*/
        /* OnKeyPress:                                                        */
        /*                                                                    */
        /* The Framework's character hook: hands the character to             */
        /* Hex_OnKeyPress, which decides whether it edits a byte (a hex digit */
        /* in the hex pane, printable ASCII in the text pane).                */
        /*                                                                    */
        /* An override because the framework requires it: a control receives  */
        /* its own typed characters first only through this virtual method.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     evKey : the character event; the helper may mark it handled.   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the character has been dealt with.                      */
        /*--------------------------------------------------------------------*/
        protected override void OnKeyPress(KeyPressEventArgs evKey)
        {
            /* The byte edit and the base call are in the helper. */
            Hex_OnKeyPress(evKey);
        }

        /*--------------------------------------------------------------------*/
        /* OnMouseDown:                                                       */
        /*                                                                    */
        /* The Framework's click hook: hands the click to Hex_OnMouseDown,    */
        /* which takes the focus and puts the caret on the byte clicked.      */
        /*                                                                    */
        /* An override because the framework requires it: a control receives  */
        /* its own mouse presses first only through this virtual method.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     evMouse : the click's position and button.                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the caret may have moved.                               */
        /*--------------------------------------------------------------------*/
        protected override void OnMouseDown(MouseEventArgs evMouse)
        {
            /* Focus, hit test and the base call are in the helper. */
            Hex_OnMouseDown(evMouse);
        }

        /*--------------------------------------------------------------------*/
        /* OnMouseWheel:                                                      */
        /*                                                                    */
        /* The Framework's wheel hook: hands the wheel to Hex_OnMouseWheel,   */
        /* which scrolls the rows.                                            */
        /*                                                                    */
        /* An override because the framework requires it: a control receives  */
        /* its own wheel turns first only through this virtual method.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     evMouse : the wheel's delta and position.                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the view may have scrolled.                             */
        /*--------------------------------------------------------------------*/
        protected override void OnMouseWheel(MouseEventArgs evMouse)
        {
            /* The base call, then the scrolling, are in the helper. */
            Hex_OnMouseWheel(evMouse);
        }

        /*--------------------------------------------------------------------*/
        /* OnResize:                                                          */
        /*                                                                    */
        /* The Framework's size hook, which also fires while the control is   */
        /* being built: hands it to Hex_OnResize, which skips the work until  */
        /* a file is attached and then refits the scroll range.               */
        /*                                                                    */
        /* An override because the framework requires it: a control learns of */
        /* its own size changes first only through this virtual method.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     evArgs : the size-change notification.                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the view fits its new size.                             */
        /*--------------------------------------------------------------------*/
        protected override void OnResize(EventArgs evArgs)
        {
            /* The no-file guard and the base call are in the helper. */
            Hex_OnResize(evArgs);
        }

        /*--------------------------------------------------------------------*/
        /* OnGotFocus:                                                        */
        /*                                                                    */
        /* The Framework's focus hook: hands it to Hex_OnGotFocus, which      */
        /* makes the base call and repaints (a repaint that, as its own       */
        /* comment says, changes nothing on screen today).                    */
        /*                                                                    */
        /* An override because the framework requires it: a control learns it */
        /* has the focus first only through this virtual method.              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     evArgs : the focus notification.                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the view repaints.                                      */
        /*--------------------------------------------------------------------*/
        protected override void OnGotFocus(EventArgs evArgs)
        {
            /* The base call and the repaint are in the helper. */
            Hex_OnGotFocus(evArgs);
        }

        /*--------------------------------------------------------------------*/
        /* Hex_OnPaint:                                                       */
        /*                                                                    */
        /* Draw the rows on screen.  Each byte is drawn as its own cell, in   */
        /* both panes, so that it can take its own colours: red for a byte    */
        /* the user has changed, the highlight colours for the caret's byte   */
        /* in the pane the caret is in, and a frame round the same byte in    */
        /* the other pane, so the eye can find it there.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     evPaint : the paint event; its Graphics is the surface.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the visible rows are drawn.                             */
        /*--------------------------------------------------------------------*/
        private void Hex_OnPaint(PaintEventArgs evPaint)
        {
            Graphics  gfx;
            Rectangle rcHex;
            Rectangle rcText;
            Color     clrText;
            long      lRowOffset;
            long      lOffset;
            string    sChar;
            byte      bValue;
            bool      bCaret;
            int       iRows;
            int       iRow;
            int       iByte;
            int       iTop;

            /* The base paint first (the Paint event for any listener). */
            base.OnPaint(evPaint);

            /* The surface, and the rows to draw: the whole rows that fit     */
            /* and the part-row below them, as far as the control shows it.   */
            gfx   = evPaint.Graphics;
            iRows = HEX_VisibleRows() + 1;

            /* The screen's bytes read once, up front, rather than byte by   */
            /* byte as the loop asks for them.                               */
            Hex_LoadPage();

            for (iRow = 0; iRow < iRows; iRow++)
            {   /* One row of the screen per iteration. */
                lRowOffset = (Hex_lTopRow + iRow) * HEX_BYTES_PER_ROW;

                if (lRowOffset >= Hex_lLength)
                {   /* Below the end of the file: nothing more to draw. */
                    break;
                }

                /* The row's top edge, in pixels. */
                iTop = HEX_MARGIN + (iRow * Hex_iRowHeight);

                /* The row's offset, in grey. */
                TextRenderer.DrawText(gfx, lRowOffset.ToString("X" + Hex_iOffsetDigits), this.Font, new Point(HEX_MARGIN, iTop),
                                      SystemColors.GrayText, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

                for (iByte = 0; iByte < HEX_BYTES_PER_ROW; iByte++)
                {   /* One byte of the row per iteration. */
                    lOffset = lRowOffset + iByte;

                    if (lOffset >= Hex_lLength)
                    {   /* The last row may be short. */
                        break;
                    }

                    /* The byte as it is NOW (the user's change, if any),   */
                    /* whether the caret is on it, and its text colour:     */
                    /* ordinary unless the test below makes it red.         */
                    bValue  = HEX_ByteAt(lOffset);
                    bCaret  = (lOffset == Hex_lCaret);
                    clrText = this.ForeColor;

                    if (bValue != Hex_SavedByte(lOffset))
                    {   /* Changed and not yet saved: red, in both panes --     */
                        /* except where the caret's highlight draws it below.   */
                        clrText = Color.Red;
                    }

                    /* Its two cells: two characters wide in the hex pane,   */
                    /* one in the text pane, a row high.                     */
                    rcHex  = new Rectangle(HEX_MARGIN + (Hex_HexCell(iByte) * Hex_iCellWidth), iTop, 2 * Hex_iCellWidth, Hex_iRowHeight);
                    rcText = new Rectangle(HEX_MARGIN + (Hex_TextCell(iByte) * Hex_iCellWidth), iTop, Hex_iCellWidth, Hex_iRowHeight);

                    /* A byte that is not printable ASCII shows as a dot. */
                    sChar = ".";

                    if (bValue >= 0x20 && bValue < 0x7F)
                    {   /* Printable. */
                        sChar = ((char)bValue).ToString();
                    }

                    if (bCaret)
                    {   /* The caret's byte: filled where the caret is,   */
                        /* framed in the other pane.  Drawn BEFORE the    */
                        /* text, so the text lands on the fill rather     */
                        /* than under it.                                 */
                        if (Hex_bTextPane)
                        {   /* The caret is in the text pane. */
                            gfx.FillRectangle(SystemBrushes.Highlight, rcText);
                            gfx.DrawRectangle(SystemPens.Highlight, rcHex.Left, rcHex.Top, rcHex.Width - 1, rcHex.Height - 1);
                        }
                        else
                        {   /* The caret is in the hex pane. */
                            gfx.FillRectangle(SystemBrushes.Highlight, rcHex);
                            gfx.DrawRectangle(SystemPens.Highlight, rcText.Left, rcText.Top, rcText.Width - 1, rcText.Height - 1);
                        }
                    }

                    if (bCaret && !Hex_bTextPane)
                    {   /* On the filled hex cell: in the highlight's own text   */
                        /* colour, so it reads on the fill (a changed byte is    */
                        /* not red here; it still is in the framed text cell).   */
                        TextRenderer.DrawText(gfx, bValue.ToString("X2"), this.Font, rcHex.Location, SystemColors.HighlightText,
                                              TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    }
                    else
                    {   /* An ordinary hex cell. */
                        TextRenderer.DrawText(gfx, bValue.ToString("X2"), this.Font, rcHex.Location, clrText,
                                              TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    }

                    if (bCaret && Hex_bTextPane)
                    {   /* On the filled text cell, in the highlight's text colour, as above. */
                        TextRenderer.DrawText(gfx, sChar, this.Font, rcText.Location, SystemColors.HighlightText,
                                              TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    }
                    else
                    {   /* An ordinary text cell. */
                        TextRenderer.DrawText(gfx, sChar, this.Font, rcText.Location, clrText,
                                              TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    }
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* Hex_IsInputKey:                                                    */
        /*                                                                    */
        /* Claim the keys a form would otherwise use for moving between       */
        /* controls -- the arrows and Tab -- so they reach OnKeyDown here.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     enmKeyData : the key, with its modifiers.                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true for a key this control handles itself.             */
        /*--------------------------------------------------------------------*/
        private bool Hex_IsInputKey(Keys enmKeyData)
        {
            switch (enmKeyData & Keys.KeyCode)
            {   /* One key this control wants per case. */

                /* The arrows and Tab, with or without modifiers: claimed,    */
                /* so the form does not use them to move the focus.  Page     */
                /* Up / Down, Home and End need no claim -- a form does not   */
                /* take them (the framework's behaviour, as found) -- and     */
                /* reach OnKeyDown as they are.                               */
                case Keys.Left:
                case Keys.Right:
                case Keys.Up:
                case Keys.Down:
                case Keys.Tab:
                    return(true);
            }

            /* Everything else: the usual rule. */
            return(base.IsInputKey(enmKeyData));
        }

        /*--------------------------------------------------------------------*/
        /* Hex_OnKeyDown:                                                     */
        /*                                                                    */
        /* The keys that move the caret: the arrows by a byte or a row, Page  */
        /* Up and Page Down by a screenful, Home and End to the ends of the   */
        /* row (with Ctrl, of the file), and Tab between the two panes.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     evKey : the key event.                                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the caret has moved; other keys pass through.           */
        /*--------------------------------------------------------------------*/
        private void Hex_OnKeyDown(KeyEventArgs evKey)
        {
            long lPage;
            long lRowStart;

            /* The base handling first: it raises the KeyDown event for any   */
            /* listener before this control acts on the key.                  */
            base.OnKeyDown(evKey);

            /* A screenful of bytes, for Page Up / Down, and the first byte   */
            /* of the caret's row, for Home and End.                          */
            lPage     = (long)HEX_VisibleRows() * HEX_BYTES_PER_ROW;
            lRowStart = Hex_lCaret - (Hex_lCaret % HEX_BYTES_PER_ROW);

            switch (evKey.KeyCode)
            {   /* One movement key per case.  Every move goes through        */
                /* HEX_GoTo, which keeps the caret inside the file, scrolls   */
                /* it into view, and starts a new byte on its high digit.     */

                /* Left: one byte back (staying on the first byte). */
                case Keys.Left:
                    HEX_GoTo(Hex_lCaret - 1);
                    break;

                /* Right: one byte on (staying on the last byte). */
                case Keys.Right:
                    HEX_GoTo(Hex_lCaret + 1);
                    break;

                /* Up: a row up, in the same column -- only when there is a   */
                /* row above, so the caret never jumps sideways to the        */
                /* file's start.                                              */
                case Keys.Up:
                    if (Hex_lCaret >= HEX_BYTES_PER_ROW)
                    {   /* There is a row above. */
                        HEX_GoTo(Hex_lCaret - HEX_BYTES_PER_ROW);
                    }

                    /* Moved or not, the key is used. */
                    break;

                /* Down: a row down, in the same column -- only when a byte    */
                /* is directly below, so the caret never jumps sideways onto   */
                /* a short last row's end.                                     */
                case Keys.Down:
                    if (Hex_lCaret + HEX_BYTES_PER_ROW < Hex_lLength)
                    {   /* There is a byte directly below. */
                        HEX_GoTo(Hex_lCaret + HEX_BYTES_PER_ROW);
                    }

                    /* Moved or not, the key is used. */
                    break;

                /* Page Up: a screenful back.  Unlike Up it does stop at the   */
                /* file's first byte when a whole screenful is not left.       */
                case Keys.PageUp:
                    HEX_GoTo(Hex_lCaret - lPage);
                    break;

                /* Page Down: a screenful on, stopping at the file's last   */
                /* byte when a whole screenful is not left.                 */
                case Keys.PageDown:
                    HEX_GoTo(Hex_lCaret + lPage);
                    break;

                /* Home: the row's start, or with Ctrl the file's. */
                case Keys.Home:
                    if (evKey.Control)
                    {   /* The start of the file. */
                        HEX_GoTo(0);
                    }
                    else
                    {   /* The start of the row. */
                        HEX_GoTo(lRowStart);
                    }

                    /* The key is used. */
                    break;

                /* End: the row's last byte, or with Ctrl the file's. */
                case Keys.End:
                    if (evKey.Control)
                    {   /* The end of the file. */
                        HEX_GoTo(Hex_lLength - 1);
                    }
                    else
                    {   /* The end of the row -- on the last, short row, the   */
                        /* file's last byte, as HEX_GoTo stops there.          */
                        HEX_GoTo(lRowStart + HEX_BYTES_PER_ROW - 1);
                    }

                    /* The key is used. */
                    break;

                /* Tab: the caret to the other pane, on the same byte.  A    */
                /* byte whose high digit was typed is left as it is (that    */
                /* half is already in the table), and the next hex digit     */
                /* starts a byte afresh.  Only repainted: the window shows   */
                /* nothing that depends on the pane, so it is not told.      */
                case Keys.Tab:
                    Hex_bTextPane = !Hex_bTextPane;
                    Hex_bLowDigit = false;
                    this.Invalidate();
                    break;

                /* Any other key is not this control's: not marked handled,   */
                /* so it goes on as usual (menu shortcuts and the like).      */
                default:
                    return;
            }

            /* A movement key: used here, so nothing else acts on it. */
            evKey.Handled = true;
        }

        /*--------------------------------------------------------------------*/
        /* Hex_OnKeyPress:                                                    */
        /*                                                                    */
        /* A character was typed: overwrite the caret's byte.  In the hex     */
        /* pane a hex digit sets one half of the byte -- the high half first, */
        /* then the low half, after which the caret moves on.  In the text    */
        /* pane a printable ASCII character becomes the byte and the caret    */
        /* moves on.  Anything else is ignored.                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     evKey : the key-press event; its KeyChar is the character.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the byte is changed, or the key was not one that types. */
        /*--------------------------------------------------------------------*/
        private void Hex_OnKeyPress(KeyPressEventArgs evKey)
        {
            byte bOld;
            int  iDigit;

            /* The base handling first: it raises the KeyPress event for any   */
            /* listener.                                                       */
            base.OnKeyPress(evKey);

            if (Hex_lLength == 0)
            {   /* An empty file has no byte to overwrite. */
                return;
            }

            if (Hex_bTextPane)
            {   /* The text pane: one printable character, one byte. */
                if (evKey.KeyChar < (char)0x20 || evKey.KeyChar > (char)0x7E)
                {   /* Not printable ASCII -- a control character, or one      */
                    /* with no single-byte ASCII form: ignored, and left for   */
                    /* anyone else (not marked handled).                       */
                    return;
                }

                /* The character's code is the byte, which the caret then    */
                /* leaves for the next (staying put on the file's last       */
                /* byte, where HEX_GoTo stops); HEX_GoTo also repaints and   */
                /* tells the window.                                         */
                Hex_SetByte(Hex_lCaret, (byte)evKey.KeyChar);
                evKey.Handled = true;
                HEX_GoTo(Hex_lCaret + 1);
                return;
            }

            /* The hex pane: one digit, half a byte. */
            if (!Uri.IsHexDigit(evKey.KeyChar))
            {   /* Not a hex digit (0-9, a-f, A-F -- the framework's test, as   */
                /* found): ignored, and not marked handled.                     */
                return;
            }

            /* The digit's value, 0 to 15. */
            iDigit = Uri.FromHex(evKey.KeyChar);

            /* The byte as it is now -- its other half is kept -- and the   */
            /* key taken here.                                              */
            bOld          = HEX_ByteAt(Hex_lCaret);
            evKey.Handled = true;

            if (!Hex_bLowDigit)
            {   /* The high digit: the low one is kept, and is typed next.   */
                /* The caret does not move, so HEX_GoTo is not called, and   */
                /* the repaint and the window's update are asked for here.   */
                Hex_SetByte(Hex_lCaret, (byte)((iDigit << 4) | (bOld & 0x0F)));
                Hex_bLowDigit = true;
                this.Invalidate();
                Hex_NotifyState();
                return;
            }

            /* The low digit: the byte is complete, and the caret moves on. */
            Hex_SetByte(Hex_lCaret, (byte)((bOld & 0xF0) | iDigit));
            HEX_GoTo(Hex_lCaret + 1);
        }

        /*--------------------------------------------------------------------*/
        /* Hex_OnMouseDown:                                                   */
        /*                                                                    */
        /* A click: take the focus, and put the caret on the byte clicked, in */
        /* the pane it was clicked in.  A click that is not on a byte only    */
        /* takes the focus.                                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     evMouse : the mouse event.                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the caret is on the clicked byte.                       */
        /*--------------------------------------------------------------------*/
        private void Hex_OnMouseDown(MouseEventArgs evMouse)
        {
            long lOffset;
            int  iCell;
            int  iRow;
            int  iByte;
            bool bTextPane;

            /* The base handling (the MouseDown event), then the focus --    */
            /* taken by any click, on a byte or not, so typing comes here.   */
            base.OnMouseDown(evMouse);
            this.Focus();

            if (evMouse.X < HEX_MARGIN || evMouse.Y < HEX_MARGIN)
            {   /* In the margin: not on any cell. */
                return;
            }

            /* The cell and the screen row under the pointer; no byte found   */
            /* yet, and the hex pane until the text pane is found.            */
            iCell     = (evMouse.X - HEX_MARGIN) / Hex_iCellWidth;
            iRow      = (evMouse.Y - HEX_MARGIN) / Hex_iRowHeight;
            iByte     = -1;
            bTextPane = false;

            if (iCell >= Hex_TextCell(0) && iCell < Hex_TextCell(HEX_BYTES_PER_ROW))
            {   /* In the text pane: one cell per byte. */
                iByte     = iCell - Hex_TextCell(0);
                bTextPane = true;
            }
            else
            {   /* Perhaps in the hex pane: find the byte whose three cells     */
                /* hold the click -- the rightmost byte starting at or left     */
                /* of it, so a click on a byte's trailing blank is that byte.   */
                /* That also catches clicks past the hex pane: in the gap       */
                /* before the text pane, or right of the text pane, the click   */
                /* lands on the row's last byte, in the hex pane.  Left of      */
                /* the first byte -- the offset column -- the loop runs out     */
                /* with iByte at -1.                                            */
                for (iByte = HEX_BYTES_PER_ROW - 1; iByte >= 0; iByte--)
                {   /* One byte of the row per iteration, from the right. */
                    if (iCell >= Hex_HexCell(iByte))
                    {   /* The click is at or right of this byte's first cell. */
                        break;
                    }
                }
            }

            /* The byte's offset: its row on screen from the top row, then   */
            /* its place in the row.                                         */
            lOffset = ((Hex_lTopRow + iRow) * HEX_BYTES_PER_ROW) + iByte;

            if (iByte < 0 || lOffset >= Hex_lLength)
            {   /* On the offset column, or below the end of the file. */
                return;
            }

            /* The caret into the pane clicked and onto the byte; HEX_GoTo   */
            /* also starts the byte on its high digit, repaints and tells    */
            /* the window.                                                   */
            Hex_bTextPane = bTextPane;
            HEX_GoTo(lOffset);
        }

        /*--------------------------------------------------------------------*/
        /* Hex_OnMouseWheel:                                                  */
        /*                                                                    */
        /* The wheel scrolls the view a few rows per notch; the caret stays   */
        /* on its byte, which may scroll out of sight.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     evMouse : the wheel event; a positive Delta is "away from the  */
        /*               user", which scrolls up.                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the view is scrolled.                                   */
        /*--------------------------------------------------------------------*/
        private void Hex_OnMouseWheel(MouseEventArgs evMouse)
        {
            /* The base handling (the MouseWheel event) first. */
            base.OnMouseWheel(evMouse);

            /* Only the Delta's SIGN is used: any wheel event scrolls          */
            /* HEX_WHEEL_ROWS, so a device that reports one notch as several   */
            /* smaller events would scroll several times as far (not tried     */
            /* on such a device).                                              */
            /* HEX_SetTopRow keeps the top inside the file.                    */
            if (evMouse.Delta > 0)
            {   /* Up. */
                HEX_SetTopRow(Hex_lTopRow - HEX_WHEEL_ROWS);
            }
            else if (evMouse.Delta < 0)
            {   /* Down. */
                HEX_SetTopRow(Hex_lTopRow + HEX_WHEEL_ROWS);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Hex_OnResize:                                                      */
        /*                                                                    */
        /* The control changed size: more or fewer rows fit, so the window's  */
        /* scroll bar has to be told, and a view scrolled past what is now    */
        /* the last screenful is brought back.                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     evArgs : unused.                                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the view fits its new size.                             */
        /*--------------------------------------------------------------------*/
        private void Hex_OnResize(EventArgs evArgs)
        {
            /* The base handling (the Resize event) first. */
            base.OnResize(evArgs);

            if (Hex_stmFile == null)
            {   /* Raised from inside the base constructor, before this      */
                /* object's own fields are set (the framework's behaviour,   */
                /* as found): nothing to fit yet.                            */
                return;
            }

            /* The top row brought back inside the file at the new height    */
            /* -- HEX_SetTopRow repaints and tells the window only when it   */
            /* moves -- and the window told in any case, since its scroll    */
            /* bar's screenful is a count of rows that fit and has changed   */
            /* even when the top has not.                                    */
            HEX_SetTopRow(Hex_lTopRow);
            Hex_NotifyState();
        }

        /*--------------------------------------------------------------------*/
        /* Hex_OnGotFocus:                                                    */
        /*                                                                    */
        /* Repaint when the focus arrives.                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     evArgs : unused.                                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the control is repainted.                               */
        /*--------------------------------------------------------------------*/
        private void Hex_OnGotFocus(EventArgs evArgs)
        {
            /* The base handling, then a repaint.  OnPaint draws the caret   */
            /* the same with or without the focus, and there is no           */
            /* OnLostFocus, so this repaint changes nothing on screen; why   */
            /* it is here is not recorded.                                   */
            base.OnGotFocus(evArgs);
            this.Invalidate();
        }
    }

    /*------------------------------------------------------------------------*/
    /* HEXF_clsHexEditForm -- the hex editor's window.                        */
    /*                                                                        */
    /* Derives from Form because the framework requires it: WinForms makes a  */
    /* window only as a Form subclass.  That is the one kind of inheritance   */
    /* allowed here -- from a framework base class, never from a class of     */
    /* ours.                                                                  */
    /*------------------------------------------------------------------------*/
    internal sealed class HEXF_clsHexEditForm : Form
    {
        /* The window's first size -- documentation table.  Its width is   */
        /* worked out from the dump's row and the status bar's texts.      */
        private const int HEXF_DEFAULT_HEIGHT = 640;    // first window height, pixels
        private const int HEXF_STATUS_PAD     = 48;     // added to the status bar's measured texts for its labels' margins, their dividers and the sizing grip, pixels

        /* The text size's default and the scroll bar's limit --   */
        /* documentation table.                                    */
        private const int HEXF_FONT_DEFAULT  = 10;         // the dump's text size on a first run, or when the stored size is not one the menu offers, points; the same as the text editor's TXE_FONT_DEFAULT, as the two share the stored size
        private const long HEXF_BAR_STEPS_MAX = 1L << 30;  // the most steps the scroll bar is given (16 GiB of 16-byte rows at one row a step); its range is an int, and this keeps Maximum + LargeChange well inside one

        /* The sizes View > Text Size offers, points (HEXF_FONT_DEFAULT is   */
        /* one of them).                                                     */
        private static readonly int[] Hexf_aiFontSizes = { 8, 9, 10, 11, 12, 14, 16, 18, 20, 24 }; // fixed offered point sizes, includes HEXF_FONT_DEFAULT

        /* The window's own commands, as its queue (Hexf_objQueue) carries   */
        /* them: each handler posts one and Hexf_RunItem runs it --          */
        /* documentation table.                                              */
        private enum Hexf_enmItem
        {
            GoToOffset = 1,     // Edit > Go to Offset: ask for an offset, then put the caret there (Hexf_AskGoTo)
            SaveAsImage = 2,    // File > Save As > Image...: an image destination asked for (Hexf_RequestSaveAs)
            SaveAsWindows = 3,  // File > Save As > Windows...: a Windows destination asked for (Hexf_RequestSaveAs)
            CloseAgain = 4,     // the window's own close asked again: a Save As chosen from its close question was cancelled, so the question shows once more (HEXF_ApplyTargetChoice)
            GoToAnswer = 5,     // the Go To prompt has closed: its answer applied and the window enabled again (Hexf_ApplyGoToAnswer); its argument the prompt
            AskClose = 6,       // a close found unsaved bytes: the close question shown (Hexf_ShowCloseQuestion); its argument the worker's close question's display, or null for the window's own close
            CloseAnswer = 7,    // the close question has closed: the window enabled again and its choice acted on (Hexf_ApplyCloseAnswer); its argument the question's form
            Save = 8,           // File > Save: the changed bytes saved where Save writes now, if a save may start (Hexf_RunSave)
            Close = 9,          // File > Close: the window closed, Hexf_OnFormClosing deciding first
            TextSize = 10       // a View > Text Size item chosen: the size remembered and shown (Hexf_ApplyTextSize); its argument the size in points, boxed
        }

        /* What the constructor tells its caller -- the main window, which     */
        /* shows a load error and disposes the window unshown (MainForm.cs).   */
        private string Hexf_sLoadError;    // "" when the file was opened; otherwise why it could not be, and the window is not to be shown

        /* Controls. */
        private MenuStrip            Hexf_mnuMain;      // the menu bar
        private ToolStripMenuItem    Hexf_tsmTextSize;  // View > Text Size: a sub-menu of sizes; each item's Tag is its size in points
        private HEX_clsHexView          Hexf_view;      // the dump
        private VScrollBar           Hexf_vsbRows;      // scrolls the dump by rows
        private StatusStrip          Hexf_stsStatus;    // the status bar
        private ToolStripStatusLabel Hexf_tslMessage;   // its message, taking the width the others leave
        private ToolStripStatusLabel Hexf_tslCaret;     // the caret's offset and the byte there
        private ToolStripStatusLabel Hexf_tslSize;      // the file's size and how many bytes are changed

        /* State. */
        private OFD_clsOpenedFile  Hexf_file;                          // which file this is: an image entry and its temporary Windows copy, or a Windows file itself (bHostFile)
        private OFD_SaveRequest Hexf_pfnSave;                          // how a save is handed over (the main window's Main_SaveOpenedFile); its answer comes through the save's own record
        private Font            Hexf_fntDialog;                        // the program's dialog font, given by the main window, for every dialog this window opens (not owned here)
        private FileStream      Hexf_stmFile;                          // the file read, for the window's whole life; replaced only when a Windows Save As's new file is opened for reading (HEXF_SaveAcknowledged), so it can differ from where Save writes
        private bool            Hexf_bDirty;                           // true once bytes have been changed since the last save; not lowered if they are all typed back (Hexf_RefreshBars)
        private bool            Hexf_bSyncing;                         // true while the scroll bar is being set from the view (so it does not scroll the view back)
        private int Hexf_iPendingSaves;                                // saves handed over and not yet answered; the window refuses to close while any are
        private int Hexf_iLocalWork;                                   // how deep the window is in its own save work or a save's answer (the writes, the snapshot's busy box, a message box); while not 0 no close runs
        private PRM_clsPromptForm Hexf_frmPrompt;                      // the Go To prompt while it is up -- modeless, over this window, which is disabled meanwhile (Hexf_AskGoTo); null otherwise.  Counted as the window's own work (HEXF_HasLocalWork)
        private int Hexf_iClosing;                                     // how deep the window is in its own FormClosing handler (which shows nothing; a close made while it runs would nest in it)
        private long Hexf_lSaveOrder;                                  // the last save's number; each save takes the next, from 1
        private long Hexf_lAcknowledgedOrder;                          // the number of the newest save applied (0 before any); an older answer must not undo it
        private EUI_PostFact Hexf_pfnPostFact;                         // the coordinator's callback for the window's reports (CommandEditorCoordinator.cs); null until registered
        private List<SJB_clsStageJob> Hexf_lstJobs;                    // the snapshot writers running now (one per image save being written), so an Exit can stop them (HEXF_AbortStaging)
        private List<HSC_clsHexSaveCapture> Hexf_lstCloseEvaluations;  // saves that succeeded and asked to close the window, waiting to be posted until its own work has returned
        private long            Hexf_lRowsPerStep;                     // rows one scroll-bar step stands for: 1 up to HEXF_BAR_STEPS_MAX rows, more for a larger file, so the bar always reaches the end
        private WQU_clsWindowQueue Hexf_objQueue;                      // the window's own command queue (WindowQueue.cs): the handlers of Save, Save As, Close, Go to Offset and Text Size post to it, and so do the Go To prompt's and close question's closings and a close whose Save As was cancelled; it runs each through Hexf_RunItem, one at a time
        private bool Hexf_bCloseAsked;                                 // true from the closing handler's post of the close question (Hexf_PresentClose) until the queue shows it (Hexf_ShowCloseQuestion).  Counted as the window's own work (HEXF_HasLocalWork)
        private RCD_clsRecoveryDialog Hexf_objCloseQuestion;           // the close question while it is up -- modeless, over this window, which is disabled meanwhile (Hexf_ShowCloseQuestion); null otherwise.  Counted as the window's own work (HEXF_HasLocalWork)
        private PRS_clsPresentation Hexf_objCloseShown;                // the worker's close question that close question was shown under, taken when it was shown; null for the window's own close
        private bool Hexf_bDiscardOnClose;                             // true only across the Close() a Discard answer makes (Hexf_ApplyCloseAnswer): that close lets the changed bytes go without asking again

        /*----------------------------------------------------------------------*/
        /* HEXF_clsHexEditForm (constructor):                                   */
        /*                                                                      */
        /* Build the editor over its file: opened for reading, and kept open    */
        /* until the window closes (Hexf_OnFormClosed) or is let go of          */
        /* unshown (HEXF_ReleaseBaseline, MainCommand.cs).  A file that         */
        /* cannot be opened leaves the window unbuilt and the reason in         */
        /* HEXF_ReadLoadError; the caller then disposes it unshown              */
        /* (MainForm.cs).                                                       */
        /*                                                                      */
        /* Arguments:                                                           */
        /*     fntDialog : the program's dialog font (the caller's; not freed   */
        /*                 here).                                               */
        /*     objFile   : the file's descriptor; its work file (OFD_sTempFile) */
        /*                 is the file read.                                    */
        /*     pfnSave   : how a save is handed over.                           */
        /*                                                                      */
        /* Returns:                                                             */
        /*     (constructor) : the window, unshown; HEXF_ReadLoadError says     */
        /*                     whether it may be shown.                         */
        /*----------------------------------------------------------------------*/
        internal HEXF_clsHexEditForm(Font fntDialog, OFD_clsOpenedFile objFile, OFD_SaveRequest pfnSave)
        {
            SVM_clsSaveMenu objSaveMenu;
            ToolStripMenuItem tsmFile;
            ToolStripMenuItem tsmEdit;
            ToolStripMenuItem tsmItem;
            ToolStripMenuItem tsmView;
            int               iStatusWidth;
            int               iWidth;
            int               i;

            /* The caller's font, file and save callback kept; not dirty; no   */
            /* save made, waiting or answered, no work running, no close       */
            /* waiting; the scroll bar not being set and a step one row.       */
            Hexf_sLoadError       = "";
            Hexf_fntDialog       = fntDialog;
            Hexf_file            = objFile;
            Hexf_pfnSave         = pfnSave;
            Hexf_bDirty          = false;
            Hexf_bSyncing        = false;
            Hexf_lRowsPerStep    = 1;
            Hexf_iPendingSaves = 0;
            Hexf_iLocalWork = 0;
            Hexf_iClosing = 0;
            Hexf_lSaveOrder = 0;
            Hexf_lAcknowledgedOrder = 0;
            Hexf_lstJobs = new List<SJB_clsStageJob>();
            Hexf_lstCloseEvaluations = new List<HSC_clsHexSaveCapture>();

            /* The window's own command queue, its items run by Hexf_RunItem. */
            Hexf_objQueue = new WQU_clsWindowQueue(this, Hexf_RunItem);

            /* No close question asked for or up, and no discard in hand. */
            Hexf_bCloseAsked = false;
            Hexf_objCloseQuestion = null;
            Hexf_objCloseShown = null;
            Hexf_bDiscardOnClose = false;

            try
            {   /* The file, open for reading for the window's whole life.      */
                /* FileShare.ReadWrite lets the window's own write handle       */
                /* (HEX_WriteOverlay) open beside it, and lets other programs   */
                /* go on using a Windows file.  It may fail: the temporary      */
                /* file removed, or the file locked or refused.                 */
                Hexf_stmFile = new FileStream(objFile.OFD_sTempFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (Exception exOpen)
            {   /* Nothing to show: the reason kept for the caller, and the   */
                /* window left unbuilt -- no controls, no handlers -- which   */
                /* is safe only because the caller disposes it unshown.       */
                Hexf_sLoadError = exOpen.Message;
                return;
            }

            /* File > Save (Ctrl+S): the changed bytes saved where the file   */
            /* came from.                                                     */
            tsmItem              = new ToolStripMenuItem("&Save");
            tsmItem.ShortcutKeys = Keys.Control | Keys.S;
            tsmItem.Click       += Hexf_OnSaveClicked;

            /* The File menu, Save first. */
            tsmFile = new ToolStripMenuItem("&File");
            tsmFile.DropDownItems.Add(tsmItem);

            /* File > Save As (Ctrl+Shift+S), made by the editors' shared      */
            /* menu (EditorSaveMenu.cs): its two choices -- in the image, or   */
            /* a Windows file -- each with its own handler, and the menu       */
            /* asks Hexf_ReadCurrentFile what to offer.  Choosing a            */
            /* destination does not move the window; a save to it that         */
            /* succeeds does.                                                  */
            tsmItem              = new ToolStripMenuItem("Save &As...");
            tsmItem.ShortcutKeys = Keys.Control | Keys.Shift | Keys.S;
            objSaveMenu = new SVM_clsSaveMenu(tsmItem, Hexf_ReadCurrentFile, Hexf_OnSaveAsImageClicked,
                Hexf_OnSaveAsWindowsClicked);
            tsmFile.DropDownItems.Add(tsmItem);

            /* A divider between the saves and Close. */
            tsmFile.DropDownItems.Add(new ToolStripSeparator());

            /* Close (Ctrl+W): the window's ordinary close, which asks   */
            /* about unsaved changes (Hexf_OnFormClosing).               */
            tsmItem              = new ToolStripMenuItem("&Close");
            tsmItem.ShortcutKeys = Keys.Control | Keys.W;
            tsmItem.Click       += Hexf_OnCloseClicked;
            tsmFile.DropDownItems.Add(tsmItem);

            /* Edit > Go to Offset (Ctrl+G). */
            tsmItem              = new ToolStripMenuItem("&Go to Offset...");
            tsmItem.ShortcutKeys = Keys.Control | Keys.G;
            tsmItem.Click       += Hexf_OnGoToClicked;

            /* The Edit menu, which holds only that item. */
            tsmEdit = new ToolStripMenuItem("&Edit");
            tsmEdit.DropDownItems.Add(tsmItem);

            /* View: the size of the dump's text, remembered (and shared with   */
            /* the text editor): one item per size, its Tag the size in         */
            /* points.                                                          */
            Hexf_tsmTextSize = new ToolStripMenuItem("&Text Size");

            for (i = 0; i < Hexf_aiFontSizes.Length; i++)
            {   /* One size per iteration. */
                tsmItem        = new ToolStripMenuItem(Hexf_aiFontSizes[i] + " point");
                tsmItem.Tag    = Hexf_aiFontSizes[i];
                tsmItem.Click += Hexf_OnTextSizeClicked;
                Hexf_tsmTextSize.DropDownItems.Add(tsmItem);
            }

            /* The View menu, holding the size sub-menu (its mark is set by   */
            /* Hexf_ApplyFontSize below).                                     */
            tsmView = new ToolStripMenuItem("&View");
            tsmView.DropDownItems.Add(Hexf_tsmTextSize);

            /* The menu bar: File, Edit, View. */
            Hexf_mnuMain = new MenuStrip();
            Hexf_mnuMain.Items.Add(tsmFile);
            Hexf_mnuMain.Items.Add(tsmEdit);
            Hexf_mnuMain.Items.Add(tsmView);

            /* The dump, over the file opened above, filling the window's   */
            /* middle.                                                      */
            Hexf_view      = new HEX_clsHexView(Hexf_stmFile);
            Hexf_view.Dock = DockStyle.Fill;

            /* Its scroll bar, a separate control at the right: the dump is   */
            /* a plain Control and draws none of its own.  The bar counts     */
            /* in steps that can stand for several rows on a very large       */
            /* file (Hexf_RefreshBars); a move of it scrolls the dump         */
            /* (Hexf_OnScroll).                                               */
            Hexf_vsbRows         = new VScrollBar();
            Hexf_vsbRows.Dock    = DockStyle.Right;
            Hexf_vsbRows.Scroll += Hexf_OnScroll;

            /* The remembered text size, set before the window's width is     */
            /* worked out from the dump's, and before the dump has a window   */
            /* to report changes to.                                          */
            Hexf_ApplyFontSize(SET_Settings.SET_ReadInt(SET_Settings.SET_EDITOR_FONT, HEXF_FONT_DEFAULT));
            Hexf_view.HEX_BindStateChange(Hexf_OnViewChanged);

            /* The status bar's message: a hint on how the editor types      */
            /* until a save or Go to Offset replaces it, springing to take   */
            /* the width the other two labels leave, read from the left.     */
            Hexf_tslMessage           = new ToolStripStatusLabel("Typing overwrites the byte under the caret. Tab switches between hex and text.");
            Hexf_tslMessage.Spring    = true;
            Hexf_tslMessage.TextAlign = ContentAlignment.MiddleLeft;

            /* The caret's and the size's labels, empty until                */
            /* Hexf_RefreshBars fills them at the end of this constructor.   */
            Hexf_tslCaret = new ToolStripStatusLabel("");
            Hexf_tslSize  = new ToolStripStatusLabel("");

            /* A divider at the left of each, between it and the label   */
            /* before.                                                   */
            Hexf_tslCaret.BorderSides = ToolStripStatusLabelBorderSides.Left;
            Hexf_tslSize.BorderSides  = ToolStripStatusLabelBorderSides.Left;

            /* The bar itself, the three labels in order. */
            Hexf_stsStatus = new StatusStrip();
            Hexf_stsStatus.Items.Add(Hexf_tslMessage);
            Hexf_stsStatus.Items.Add(Hexf_tslCaret);
            Hexf_stsStatus.Items.Add(Hexf_tslSize);

            /* Docking runs in reverse order of adding: the dump fills what   */
            /* the scroll bar, the status bar and the menu leave.             */
            this.Controls.Add(Hexf_view);
            this.Controls.Add(Hexf_vsbRows);
            this.Controls.Add(Hexf_stsStatus);
            this.Controls.Add(Hexf_mnuMain);
            this.MainMenuStrip = Hexf_mnuMain;

            /* Wide enough for a whole row and the scroll bar -- and never      */
            /* narrower than the status bar needs to show its hint beside the   */
            /* other two labels.  Those two are measured at the longest text    */
            /* this file can give them: the last offset, the widest byte, and   */
            /* every byte changed.                                              */
            iStatusWidth = TextRenderer.MeasureText(Hexf_tslMessage.Text, Hexf_stsStatus.Font).Width +
                           TextRenderer.MeasureText("Offset 0x" + Hexf_stmFile.Length.ToString("X") + " (" + Hexf_stmFile.Length.ToString("N0") +
                                                    ")   Byte 0xFF (255)", Hexf_stsStatus.Font).Width +
                           TextRenderer.MeasureText(Hexf_stmFile.Length.ToString("N0") + " bytes, " + Hexf_stmFile.Length + " changed",
                                                    Hexf_stsStatus.Font).Width +
                           HEXF_STATUS_PAD;

            /* The dump's whole row beside the scroll bar. */
            iWidth = Hexf_view.HEX_PreferredWidth() + Hexf_vsbRows.Width;

            if (iWidth < iStatusWidth)
            {   /* The dump is narrower than the status bar's three labels. */
                iWidth = iStatusWidth;
            }

            /* Centred on the screen, at that width and the first height.   */
            /* This replaces any widening Hexf_ApplyFontSize did above,     */
            /* which the width worked out here already covers.              */
            this.StartPosition = FormStartPosition.CenterScreen;
            this.ClientSize    = new Size(iWidth, HEXF_DEFAULT_HEIGHT);

            /* The close question and the file's release, and the bars kept   */
            /* in step on a resize -- hooked last, once every control they    */
            /* touch exists.                                                  */
            this.FormClosing += Hexf_OnFormClosing;
            this.FormClosed  += Hexf_OnFormClosed;
            this.Resize      += Hexf_OnResize;

            /* The caption, and the bars filled for the first time. */
            Hexf_UpdateTitle();
            Hexf_RefreshBars();
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_ApplyFontSize:                                                */
        /*                                                                    */
        /* Show the dump at a given text size, and move the mark in View >    */
        /* Text Size to it.  A size the menu does not offer (a damaged stored */
        /* value) is replaced by the default.  When the larger text makes a   */
        /* row wider than the window, the window is widened to show the whole */
        /* row; it is never made narrower.                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iPoints : the text size, points.                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the dump is drawn at that size and fits the window.     */
        /*--------------------------------------------------------------------*/
        private void Hexf_ApplyFontSize(int iPoints)
        {
            bool bOffered;
            int  iNeeded;
            int  i;

            /* Is the size one the menu offers?  Not until found. */
            bOffered = false;

            for (i = 0; i < Hexf_aiFontSizes.Length; i++)
            {   /* One offered size per iteration. */
                if (Hexf_aiFontSizes[i] == iPoints)
                {   /* The size asked for is one of them. */
                    bOffered = true;
                }
            }

            if (!bOffered)
            {   /* Not a size the menu has (a damaged or hand-edited stored   */
                /* value): the default.  The stored value is left as it is;   */
                /* the next choice from the menu replaces it.                 */
                iPoints = HEXF_FONT_DEFAULT;
            }

            /* The dump at that size: its cells are measured again, and the   */
            /* view and the window's bars follow (HEX_SetFontPoints).         */
            Hexf_view.HEX_SetFontPoints(iPoints);

            for (i = 0; i < Hexf_tsmTextSize.DropDownItems.Count; i++)
            {   /* One size item per iteration: marked when it is the size in use. */
                ((ToolStripMenuItem)Hexf_tsmTextSize.DropDownItems[i]).Checked = ((int)Hexf_tsmTextSize.DropDownItems[i].Tag == iPoints);
            }

            /* A whole row and the scroll bar must fit across the window. */
            iNeeded = Hexf_view.HEX_PreferredWidth() + Hexf_vsbRows.Width;

            if (this.WindowState == FormWindowState.Normal && this.ClientSize.Width < iNeeded)
            {   /* The row is wider than the window now: widened to fit.  A   */
                /* maximised or minimised window is left alone, and no        */
                /* window is ever made narrower -- the user's width stands.   */
                this.ClientSize = new Size(iNeeded, this.ClientSize.Height);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_OnTextSizeClicked:                                            */
        /*                                                                    */
        /* One of View > Text Size's items was chosen: the TextSize item      */
        /* posted with the size it stands for; the queue remembers and shows  */
        /* it (Hexf_ApplyTextSize).  The size items are made in a loop and    */
        /* share this handler, so the size is read from the sender's Tag.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item; its Tag is the size in points.      */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item posted (or dropped as one already in hand).    */
        /*--------------------------------------------------------------------*/
        private void Hexf_OnTextSizeClicked(object objSender, EventArgs evArgs)
        {
            int iPoints;

            /* The size the item stands for (its Tag, set when the menu was   */
            /* built), posted with the item.                                  */
            iPoints = (int)((ToolStripMenuItem)objSender).Tag;
            Hexf_objQueue.WQU_Post((int)Hexf_enmItem.TextSize, iPoints);
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_ApplyTextSize:                                                */
        /*                                                                    */
        /* A View > Text Size choice, run from the window's queue: remember   */
        /* the size (the text editor uses the same setting) and show the dump */
        /* at it.                                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iPoints : the size chosen, points.                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the dump is the chosen size.                            */
        /*--------------------------------------------------------------------*/
        private void Hexf_ApplyTextSize(int iPoints)
        {
            /* Stored first, for the next editor of either kind to open at    */
            /* (an editor already open keeps its size until its own menu is   */
            /* used), then shown here.                                        */
            SET_Settings.SET_WriteInt(SET_Settings.SET_EDITOR_FONT, iPoints);
            Hexf_ApplyFontSize(iPoints);
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_UpdateTitle:                                                  */
        /*                                                                    */
        /* Set the window's caption: an asterisk while bytes are changed and  */
        /* not saved, then the file -- an entry's path and the image file it  */
        /* is in, or a Windows file's path (OFD_Title).                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the caption is set.                                     */
        /*--------------------------------------------------------------------*/
        private void Hexf_UpdateTitle()
        {
            string sTitle;

            /* The file, then which editor this is. */
            sTitle = Hexf_file.OFD_Title() + " - BifrostFM Hex";

            if (Hexf_bDirty)
            {   /* Unsaved changes. */
                sTitle = "* " + sTitle;
            }

            /* Into the caption. */
            this.Text = sTitle;
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_OnViewChanged:                                                */
        /*                                                                    */
        /* The dump's state callback (HEX_BindStateChange): its caret,        */
        /* scroll position or bytes changed, so the bars and the caption are  */
        /* brought into line (Hexf_RefreshBars).                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the bars and the caption match the dump.                */
        /*--------------------------------------------------------------------*/
        private void Hexf_OnViewChanged()
        {
            /* What the dump now shows, into the bars. */
            Hexf_RefreshBars();
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_RefreshBars:                                                  */
        /*                                                                    */
        /* Bring the scroll bar, the status bar and the caption into line     */
        /* with the dump.  The scroll bar counts in steps that can stand for  */
        /* several rows, so that it reaches the end of a file of any size.    */
        /* The dump's state callback calls it, and so do the constructor (to  */
        /* fill the bars the first time) and a resize of the window.          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the bars and the caption match the dump.                */
        /*--------------------------------------------------------------------*/
        private void Hexf_RefreshBars()
        {
            long lRows;
            long lSteps;
            long lTop;
            long lCaret;
            int  iVisible;
            int  iPage;

            /* The dump's figures, in rows: the file's, the top one and the   */
            /* screenful.                                                     */
            lRows    = Hexf_view.HEX_TotalRows();
            lTop     = Hexf_view.HEX_TopRow();
            iVisible = Hexf_view.HEX_VisibleRows();

            /* How many rows a step is.  The bar's range is an int, so a     */
            /* file of more than HEXF_BAR_STEPS_MAX rows (past 16 GiB)       */
            /* gives each step several rows -- the fewest that bring the     */
            /* step count under the limit, rounded up so it always is --     */
            /* rather than clamping the bar short of the file's end.         */
            /* Hexf_OnScroll multiplies a step back into rows.  Below that   */
            /* size a step is a row.                                         */
            Hexf_lRowsPerStep = 1;

            if (lRows > HEXF_BAR_STEPS_MAX)
            {   /* A very large file: several rows a step. */
                Hexf_lRowsPerStep = (lRows + HEXF_BAR_STEPS_MAX - 1) / HEXF_BAR_STEPS_MAX;
            }

            /* The bar in steps: the rows, the top row and a screenful. */
            lSteps = (lRows + Hexf_lRowsPerStep - 1) / Hexf_lRowsPerStep;
            lTop   = lTop / Hexf_lRowsPerStep;
            iPage  = (int)Math.Max(iVisible / Hexf_lRowsPerStep, 1);

            if (lTop > lSteps - 1)
            {   /* Past the last step (cannot happen; kept as a guard). */
                lTop = lSteps - 1;
            }

            if (lTop < 0)
            {   /* An empty file. */
                lTop = 0;
            }

            /* A scroll bar's thumb stops LargeChange - 1 short of Maximum,     */
            /* so with Maximum at the last step the thumb's last position is    */
            /* the last screenful's top (HEX_SetTopRow clamps anything past).   */
            Hexf_bSyncing            = true;
            Hexf_vsbRows.Minimum     = 0;
            Hexf_vsbRows.Maximum     = (int)Math.Max(lSteps - 1, 0);
            Hexf_vsbRows.LargeChange = iPage;
            Hexf_vsbRows.SmallChange = 1;
            Hexf_vsbRows.Value       = (int)Math.Min(lTop, Math.Max(Hexf_vsbRows.Maximum - iPage + 1, 0));
            Hexf_vsbRows.Enabled     = (lRows > iVisible);
            Hexf_bSyncing            = false;

            /* The status bar: first the caret's offset. */
            lCaret = Hexf_view.HEX_CaretOffset();

            if (Hexf_view.HEX_FileLength() == 0)
            {   /* No byte for the caret to be on. */
                Hexf_tslCaret.Text = "(empty file)";
            }
            else
            {   /* The offset both ways, and the byte both ways. */
                Hexf_tslCaret.Text = "Offset 0x" + lCaret.ToString("X") + " (" + lCaret.ToString("N0") + ")   Byte 0x" +
                                     Hexf_view.HEX_ByteAt(lCaret).ToString("X2") + " (" + Hexf_view.HEX_ByteAt(lCaret) + ")";
            }

            /* The size, and how many bytes now differ from the file. */
            Hexf_tslSize.Text = Hexf_view.HEX_FileLength().ToString("N0") + " bytes, " + Hexf_view.HEX_ChangeCount() + " changed";

            /* The dirty mark is only ever RAISED here.  Typing every byte    */
            /* back to its old value brings the count to 0 but leaves the     */
            /* asterisk, the close question and Save working; such a save     */
            /* writes no byte and still sends the file to be saved.  Only a   */
            /* save that works lowers it (HEXF_SaveAcknowledged).             */
            if (Hexf_view.HEX_ChangeCount() != 0 && !Hexf_bDirty)
            {   /* The first change since the last save: the caption gets its asterisk. */
                Hexf_bDirty = true;
                Hexf_UpdateTitle();
            }
        }

        /*--------------------------------------------------------------------*/
        /* HEXF_ReadLoadError:                                                */
        /*                                                                    */
        /* Whether the constructor could open the file: the main window asks  */
        /* before showing the window, and shows the reason and disposes it    */
        /* unshown when it could not (MainForm.cs).                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : "" when the file is open; otherwise why it is not.    */
        /*--------------------------------------------------------------------*/
        internal string HEXF_ReadLoadError()
        {
            /* Set once, by the constructor. */
            return(Hexf_sLoadError);
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_ReadCurrentFile:                                              */
        /*                                                                    */
        /* The Save As menu's question (EditorSaveMenu.cs): the descriptor    */
        /* Save writes to now, which decides what the menu offers.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     OFD_clsOpenedFile : the window's descriptor -- the newest save */
        /*                         applied, never a Save As still waiting --  */
        /*                         or null once the window is disposed.       */
        /*--------------------------------------------------------------------*/
        private OFD_clsOpenedFile Hexf_ReadCurrentFile()
        {
            if (IsDisposed)
            {   /* Gone: nothing to offer, so no picker can be asked for a   */
                /* window that no longer exists.                             */
                return(null);
            }

            /* Where Save writes now. */
            return(Hexf_file);
        }

        /*--------------------------------------------------------------------*/
        /* HEXF_BindCommandFacts:                                             */
        /*                                                                    */
        /* Set the coordinator's callback for the window's reports, when it   */
        /* registers the window (CommandEditorCoordinator.cs).                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pfnPost : the coordinator's EUI_PostFact.                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : later reports reach it (Hexf_PostFact).                 */
        /*--------------------------------------------------------------------*/
        internal void HEXF_BindCommandFacts(EUI_PostFact pfnPost)
        {
            /* Kept.  The window reports when its own work returns; the   */
            /* coordinator never has to poll it.                          */
            Hexf_pfnPostFact = pfnPost;
        }

        /*--------------------------------------------------------------------*/
        /* HEXF_HasLocalWork:                                                 */
        /*                                                                    */
        /* Whether the window is in the middle of its own work -- a save's    */
        /* writing or answer, its closing handler, the Go To prompt or its    */
        /* own close question -- which the coordinator asks before it lets a  */
        /* close question go on (CommandEditorCoordinator.cs).                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true until that work has returned.                      */
        /*--------------------------------------------------------------------*/
        internal bool HEXF_HasLocalWork()
        {
            /* Counted, not read from a control: a message box or the busy     */
            /* box runs its own message loop, so other code can run while      */
            /* the work is still under way, and only its return ends it.       */
            /* The Go To prompt, while up, is the window's own work too, and   */
            /* so is the close question, asked for or up -- the coordinator    */
            /* waits for its answer rather than take the window for            */
            /* declining.                                                      */
            return(Hexf_iLocalWork != 0 || Hexf_iClosing != 0 || Hexf_frmPrompt != null ||
                Hexf_bCloseAsked || Hexf_objCloseQuestion != null);
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_PostFact:                                                     */
        /*                                                                    */
        /* Tell the coordinator that the window's own work has returned, or   */
        /* that the close a save asked for has been decided.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lCloseOrder : the save's number, for the close it asked for;   */
        /*                   EUI_LOCAL_RETURN for "the window's own work has  */
        /*                   returned".                                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : reported, when the window is registered.                */
        /*--------------------------------------------------------------------*/
        private void Hexf_PostFact(long lCloseOrder)
        {
            if (Hexf_pfnPostFact != null)
            {   /* Registered: the report, by the editor's id, so a close   */
                /* question waiting on this window can go on.               */
                Hexf_pfnPostFact(Hexf_file.OFD_lEditorId, lCloseOrder);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_TargetIsBaseline:                                             */
        /*                                                                    */
        /* Whether a Windows destination is the very file the window reads:   */
        /* then a save writes its changed bytes in place; otherwise it builds */
        /* a full copy (Hexf_WriteWindows).  Compared by full path, ignoring  */
        /* case; that chooses how to write, and is no proof that two          */
        /* spellings are one file.                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objTarget : the destination's descriptor.                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when it names the file read.                       */
        /*--------------------------------------------------------------------*/
        private bool Hexf_TargetIsBaseline(OFD_clsOpenedFile objTarget)
        {
            /* Against the file READ, not an earlier save's destination: a   */
            /* patch is over the file read, so a full copy must start from   */
            /* it -- starting from a file an earlier save wrote would keep   */
            /* bytes that have since been typed back.                        */
            return(string.Equals(Path.GetFullPath(objTarget.OFD_sTempFile),
                Path.GetFullPath(Hexf_stmFile.Name), StringComparison.OrdinalIgnoreCase));
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_Save:                                                         */
        /*                                                                    */
        /* File > Save: the changed bytes to where Save writes now.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true once the save is handed over; false when nothing   */
        /*            has changed or the save could not start.                */
        /*--------------------------------------------------------------------*/
        private bool Hexf_Save()
        {
            if (!Hexf_bDirty)
            {   /* Nothing changed since the last save: nothing is written. */
                return(false);
            }

            /* To the window's descriptor, with no close to follow. */
            return(Hexf_SaveTo(Hexf_file, false));
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_SaveTo:                                                       */
        /*                                                                    */
        /* Save the content as it is now to a given destination: a Windows    */
        /* file is written here, an image entry's snapshot made for the       */
        /* worker, and the save handed over.  A failure before the hand-over  */
        /* is shown here and leaves where Save writes, and the content, as    */
        /* they were.                                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objTarget       : where to save: the window's descriptor, or a */
        /*                       Save As destination already checked          */
        /*                       (EditorSaveMenu.cs); null does nothing.      */
        /*     bCloseWhenSaved : true when the window's own close asked for   */
        /*                       this save.                                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true once the save is handed over (a Windows file's may */
        /*            already be answered); false when it never was.          */
        /*--------------------------------------------------------------------*/
        private bool Hexf_SaveTo(OFD_clsOpenedFile objTarget, bool bCloseWhenSaved)
        {
            OFD_clsOpenedFile objSnapshot;
            HSC_clsHexSaveCapture objCapture;
            Dictionary<long, byte> dictPatch;
            SJB_clsStageJob objJob;
            STG_clsStage objUnsent;
            Exception exWrite;
            string sRoot;
            string sProblem;
            long lRevision;
            long lOrder;

            if (objTarget == null)
            {   /* No destination (a Save As cancelled): nothing is taken or   */
                /* written.                                                    */
                return(false);
            }

            if (!objTarget.OFD_bHostFile && objTarget.OFD_bDetached)
            {   /* The entry's image is not open: it cannot be saved there,   */
                /* and the status bar says what can be done instead.          */
                Hexf_tslMessage.Text = "The filesystem is unavailable. Use Save As to choose Windows.";
                return(false);
            }

            /* No save descriptor yet (the finally tests it), and the      */
            /* window's own work counted from here to the finally, so no   */
            /* close runs while it is under way.                           */
            objSnapshot = null;
            Hexf_iLocalWork = checked(Hexf_iLocalWork + 1);
            try
            {   /* Everything up to the hand-over, its message boxes           */
                /* included, inside the count.  First the save's values,       */
                /* taken at once and together: the revision, the next          */
                /* number, the save's own descriptor, and a copy of the        */
                /* changed bytes, so typing after this cannot change what is   */
                /* written.                                                    */
                lRevision = Hexf_view.HEX_Revision();
                lOrder = checked(++Hexf_lSaveOrder);
                objSnapshot = objTarget.OFD_ForSave(lRevision);
                objSnapshot.OFD_lSaveOrder = lOrder;
                dictPatch = Hexf_view.HEX_CopyOverlay();
                objCapture = new HSC_clsHexSaveCapture(this, objSnapshot,
                    dictPatch, lRevision, lOrder, bCloseWhenSaved);

                if (objSnapshot.OFD_bHostFile)
                {   /* A Windows file: written here and now, by this window,   */
                    /* so what is handed over is the fact that it was.         */
                    try
                    {   /* The write, finished before the hand-over below. */
                        Hexf_WriteWindows(objSnapshot, dictPatch);
                    }
                    catch (Exception exLocal)
                    {   /* Not written: said, with the reason, and         */
                        /* nothing handed over -- the window keeps every   */
                        /* changed byte, and Save still writes where it    */
                        /* did.                                            */
                        MSG_clsMessageForm.MSG_Show(this, Hexf_fntDialog,
                            "The file could not be written to\n" + objSnapshot.OFD_sTempFile +
                            "\n\n" + exLocal.Message, "Save " + objSnapshot.OFD_sName,
                            MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
                        return(false);
                    }
                }
                else
                {   /* An image entry: a snapshot of the whole content -- a      */
                    /* file of its own, never the file read, which other saves   */
                    /* and the view still use -- for the worker to put in the    */
                    /* image.                                                    */
                    sRoot = objSnapshot.OFD_sStageRoot;
                    if (string.IsNullOrEmpty(sRoot))
                    {   /* No stage folder set (an editor not registered): the   */
                        /* file read's own folder instead.                       */
                        sRoot = Path.GetDirectoryName(Hexf_stmFile.Name);
                    }

                    /* The snapshot's job: the file read, with this save's        */
                    /* patch laid over it -- nothing read from a control -- and   */
                    /* listed so an Exit can stop it (HEXF_AbortStaging).         */
                    objJob = new SJB_clsStageJob(sRoot, null, Hexf_stmFile.Name, dictPatch);
                    Hexf_lstJobs.Add(objJob);
                    try
                    {   /* Written off the UI thread under the busy box, which   */
                        /* shows only if the work takes longer than 300 ms       */
                        /* (BusyModalForm.cs) -- a large file takes a while.     */
                        exWrite = BMF_clsBusyModalForm.BMF_Run(this,
                            Hexf_fntDialog, "Saving file", objSnapshot.OFD_sName,
                            objJob.SJB_Run, "BifrostFM Hex", 300);
                    }
                    catch (Exception exStage)
                    {   /* The busy box failed to start or show: taken as the    */
                        /* failure.  BMF_Run throws only after any work it did   */
                        /* start has finished.                                   */
                        exWrite = exStage;
                    }
                    finally
                    {   /* Whatever happened, the job's result taken -- the      */
                        /* snapshot, or null, which also tells a still-running   */
                        /* job to delete what it makes -- and the job off the    */
                        /* list.                                                 */
                        objSnapshot.OFD_objStage = objJob.SJB_Take();
                        Hexf_lstJobs.Remove(objJob);
                    }

                    if (exWrite != null || objSnapshot.OFD_objStage == null)
                    {   /* No whole snapshot: nothing is handed over. */
                        if (objJob.SJB_WasAborted())
                        {   /* Stopped by an Exit the user agreed to:   */
                            /* no message, as that was the user's own   */
                            /* choice.                                  */
                            return(false);
                        }

                        /* A failure: the window keeps its bytes and the file   */
                        /* read is untouched.  The reason, the job's own when   */
                        /* it gave one.                                         */
                        sProblem = "The snapshot did not complete.";
                        if (exWrite != null)
                        {   /* The job's (or the busy box's) failure. */
                            sProblem = exWrite.Message;
                        }

                        /* Said, as why the save did not go ahead. */
                        MSG_clsMessageForm.MSG_Show(this, Hexf_fntDialog,
                            "The save snapshot could not be created:\n\n" + sProblem,
                            "Save " + objSnapshot.OFD_sName, MessageBoxButtons.OK,
                            MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
                        return(false);
                    }
                }

                /* The hand-over: counted as waiting BEFORE it, since a         */
                /* Windows file's save can be answered inside the call.  Who    */
                /* may write to the image is the worker's to decide, not this   */
                /* window's; each image save carries its own snapshot.          */
                Hexf_iPendingSaves = checked(Hexf_iPendingSaves + 1);
                Hexf_tslMessage.Text = "Saving ...";
                Hexf_pfnSave(objSnapshot, objCapture.HSC_OnSaved);

                /* Handed over; the answer comes through the save's record. */
                return(true);
            }
            finally
            {   /* Every way out of the save's own work. */
                if (objSnapshot != null && objSnapshot.OFD_objStage != null)
                {   /* A snapshot still here was never taken by the receiver   */
                    /* (it clears the field when it takes one): deleted now,   */
                    /* once.                                                   */
                    objUnsent = objSnapshot.OFD_objStage;
                    objSnapshot.OFD_objStage = null;
                    try
                    {   /* The snapshot's file removed. */
                        objUnsent.STG_Release();
                    }
                    catch (Exception exCleanup)
                    {   /* A file that will not go is logged, and does not   */
                        /* replace the save's own outcome.                   */
                        if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                        {   /* Only while the debug log is kept: otherwise the line is never built. */
                            DBGLOG_clsDebugLog.DBGLOG_Write("hex snapshot cleanup", exCleanup.ToString());
                        }
                    }
                }

                /* The window's own work has returned: counted down, and the   */
                /* coordinator told, so a close question waiting on it can     */
                /* go on.                                                      */
                Hexf_iLocalWork--;
                Hexf_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);

                /* Any close a save asked for, posted now if nothing else of   */
                /* the window's is still running.                              */
                Hexf_PostReadyCloses();
            }
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_WriteWindows:                                                 */
        /*                                                                    */
        /* Write a save to a Windows file: in place when it is the file read  */
        /* (only the changed bytes are written), otherwise as a complete new  */
        /* file put in the destination's place, so a failure part-way leaves  */
        /* an existing destination as it was rather than half written.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objTarget : the destination's descriptor (OFD_sTempFile is the */
        /*                 Windows file).                                     */
        /*     dictPatch : the save's changed bytes.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : written; a failure throws, after removing the copy.     */
        /*--------------------------------------------------------------------*/
        private void Hexf_WriteWindows(OFD_clsOpenedFile objTarget, Dictionary<long, byte> dictPatch)
        {
            string sStage;

            if (Hexf_TargetIsBaseline(objTarget))
            {   /* The file read itself: only the changed bytes written, in   */
                /* place.  A failure part-way leaves some written; the        */
                /* caller says the save failed.                               */
                try
                {   /* The write. */
                    HEX_clsHexView.HEX_WriteOverlay(objTarget.OFD_sTempFile, dictPatch);
                }
                finally
                {   /* Written, wholly or in part: the view's page of the      */
                    /* file read can hold bytes from before, so it is          */
                    /* dropped (HEX_FileRewritten) -- the answer may come      */
                    /* later, and typing in between must meet the file as it   */
                    /* is.                                                     */
                    Hexf_view.HEX_FileRewritten();
                }

                /* Written. */
                return;
            }

            /* Another file: the copy's name is the destination's with a      */
            /* random suffix, in the same folder, so the final move or        */
            /* replace stays on one volume.  The copy is of the file READ,    */
            /* the one the patch is over -- which can differ from where the   */
            /* window last saved, after a Save As whose new file could not    */
            /* be opened for reading.                                         */
            sStage = objTarget.OFD_sTempFile + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {   /* The copy made and patched in full before the   */
                /* destination's name is touched.                 */
                File.Copy(Hexf_stmFile.Name, sStage, false);
                HEX_clsHexView.HEX_WriteOverlay(sStage, dictPatch);
                if (File.Exists(objTarget.OFD_sTempFile))
                {   /* An existing file: replaced by the copy (no backup    */
                    /* kept; the last argument ignores errors merging the   */
                    /* old file's metadata, as File.Replace documents).     */
                    File.Replace(sStage, objTarget.OFD_sTempFile, null, true);
                }
                else
                {   /* No file there yet: the copy moved to the name. */
                    File.Move(sStage, objTarget.OFD_sTempFile);
                }
            }
            catch
            {   /* Any failure: the copy removed if it is still there, then    */
                /* the failure passed on.  What a failed File.Replace leaves   */
                /* is the framework's; no restore is attempted here.           */
                try
                {   /* The clean-up, which must not hide the failure. */
                    if (File.Exists(sStage))
                    {   /* The copy is still there: deleted. */
                        File.Delete(sStage);
                    }
                }
                catch (Exception exCleanup)
                {   /* A copy that will not go is logged; the save's own   */
                    /* failure is what is passed on.                       */
                    if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                    {   /* Only while the debug log is kept: otherwise the line is never built. */
                        DBGLOG_clsDebugLog.DBGLOG_Write("hex Windows candidate cleanup", exCleanup.ToString());
                    }
                }

                /* To Hexf_SaveTo, which says the save failed; where Save   */
                /* writes is not changed.                                   */
                throw;
            }
        }

        /*--------------------------------------------------------------------*/
        /* HEXF_SaveAcknowledged:                                             */
        /*                                                                    */
        /* One save's answer (HSC_OnSaved).  A save that succeeded becomes    */
        /* where Save writes next, and its bytes the saved content -- unless  */
        /* a newer save has already been applied, which an older answer must  */
        /* not undo.  Bytes typed since the save began are kept, and still    */
        /* count as unsaved.  Where the file was opened from is not touched   */
        /* (OFD_objOriginal).                                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objCapture : the save's values.                                */
        /*     bSaved     : true when it succeeded.                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the save no longer waits; a success is applied.         */
        /*--------------------------------------------------------------------*/
        internal void HEXF_SaveAcknowledged(HSC_clsHexSaveCapture objCapture, bool bSaved)
        {
            FileStream stmNew;
            OFD_clsOpenedFile objSnapshot;
            string sReadFailure;

            /* No longer waiting: counted down first, before anything below   */
            /* can show a message box and run other code meanwhile.           */
            Hexf_iPendingSaves--;
            if (IsDisposed)
            {   /* The window is gone: nothing left to update. */
                return;
            }

            if (objCapture.HSC_lSaveOrder < Hexf_lAcknowledgedOrder)
            {   /* An older save, answered after a newer one was applied:   */
                /* where Save writes and the saved content stay the newer   */
                /* one's.                                                   */
                if (bSaved && objCapture.HSC_bCloseWhenSaved)
                {   /* It asked to close the window: its close is still      */
                    /* decided (and reported, with its number), though the   */
                    /* newer content will normally keep the window open.     */
                    Hexf_lstCloseEvaluations.Add(objCapture);
                    Hexf_PostReadyCloses();
                }

                /* Nothing else of it is applied. */
                return;
            }

            if (!bSaved)
            {   /* Failed: where Save writes and every changed byte stay as    */
                /* they were.  How it failed is shown by whoever reported it   */
                /* (the coordinator's report, CommandEditorCoordinator.cs).    */
                Hexf_tslMessage.Text = "Not saved.";
                return;
            }

            /* A success.  Counted as the window's own work, since the   */
            /* message box at its end runs its own message loop.         */
            Hexf_iLocalWork = checked(Hexf_iLocalWork + 1);
            try
            {   /* The new state put in place BEFORE anything that can let   */
                /* other code run (Activate, the message box).               */
                objSnapshot = objCapture.HSC_objSnapshot;
                sReadFailure = "";
                if (objSnapshot.OFD_bHostFile && !Hexf_TargetIsBaseline(objSnapshot) &&
                    Hexf_view.HEX_Revision() == objCapture.HSC_lRevision)
                {   /* A Windows Save As to another file, and nothing typed      */
                    /* since: that file holds exactly what is shown, so it       */
                    /* becomes the file read.  With typing since, the old file   */
                    /* stays read, as the patch the view holds is over it.       */
                    stmNew = null;
                    try
                    {   /* Opened as the window opens its file.  Being written   */
                        /* is no promise it can be opened again.                 */
                        stmNew = new FileStream(objSnapshot.OFD_sTempFile,
                            FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    }
                    catch (Exception exOpen)
                    {   /* Not opened: the old file stays read, and the reason   */
                        /* is said at the end.                                   */
                        sReadFailure = exOpen.Message;
                    }

                    if (stmNew != null)
                    {   /* Opened: the view switched to it FIRST, then the old   */
                        /* handle closed -- the other order would leave the      */
                        /* view reading a closed handle.                         */
                        Hexf_view.HEX_SetFile(stmNew);
                        Hexf_stmFile.Close();
                        Hexf_stmFile = stmNew;
                    }
                }

                /* Where Save writes now: this save's descriptor, expecting    */
                /* its own path next time; its number the one to beat; dirty   */
                /* only if bytes were typed since it began.  Where the file    */
                /* was opened from is the shared OFD_objOriginal, untouched.   */
                objSnapshot.OFD_sExpectedEntry = objSnapshot.OFD_sImagePath;
                Hexf_file = objSnapshot;
                Hexf_lAcknowledgedOrder = objCapture.HSC_lSaveOrder;
                Hexf_bDirty = Hexf_view.HEX_Revision() != objCapture.HSC_lRevision;

                /* The view's tables brought into line with the save, by how   */
                /* it left the file read.                                      */
                if (!objSnapshot.OFD_bHostFile || !Hexf_TargetIsBaseline(objSnapshot))
                {   /* The file read still holds the old bytes (an image save,   */
                    /* or a Save As whose new file is not being read): the       */
                    /* save's patch becomes the saved content to count and       */
                    /* colour against.                                           */
                    Hexf_view.HEX_MarkImageSaved(objCapture.HSC_dictPatch);
                }
                else if (!Hexf_bDirty)
                {   /* The file read now holds exactly what is shown: nothing   */
                    /* differs any more.                                        */
                    Hexf_view.HEX_ForgetChanges();
                }
                else
                {   /* Written in place, and bytes typed while the answer was   */
                    /* awaited (a close question's Save As onto the file        */
                    /* itself waits for the worker): the file read holds the    */
                    /* saved content and the view newer bytes, so the tables    */
                    /* are rebuilt against the file (HEX_RebaseOnFile).         */
                    Hexf_view.HEX_RebaseOnFile();
                }

                /* The status bar's time and the caption (its asterisk). */
                Hexf_tslMessage.Text = "Saved at " + DateTime.Now.ToString("HH:mm:ss") + ".";
                Hexf_UpdateTitle();
                if (objCapture.HSC_bCloseWhenSaved)
                {   /* It asked to close the window: kept for its close to be   */
                    /* decided once the window's work returns -- always,        */
                    /* since the decision is reported even when newer typing    */
                    /* keeps the window open.                                   */
                    Hexf_lstCloseEvaluations.Add(objCapture);
                }

                /* The window brought forward, now that its state is right. */
                Activate();
                if (sReadFailure.Length != 0)
                {   /* Saved, but the new file could not be opened for        */
                    /* reading: said, so the user knows the window still      */
                    /* reads the old file while Save writes to the new one.   */
                    MSG_clsMessageForm.MSG_Show(this, Hexf_fntDialog,
                        "The file was saved, but it could not be reopened to go on editing it:\n" +
                        objSnapshot.OFD_sTempFile + "\n\n" + sReadFailure +
                        "\n\nThe current content remains available in this window. Future saves target the Windows file above.",
                        "Save " + objSnapshot.OFD_sName, MessageBoxButtons.OK,
                        MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
                }
            }
            finally
            {   /* The answer's work has returned: counted down, the   */
                /* coordinator told, and any close now ready posted.   */
                Hexf_iLocalWork--;
                Hexf_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
                Hexf_PostReadyCloses();
            }
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_PostReadyCloses:                                              */
        /*                                                                    */
        /* Post the closes that successful saves asked for -- but only once   */
        /* the window's own work and its closing handler have returned.  A    */
        /* Windows save is answered at once, inside the save's own call       */
        /* (Hexf_SaveTo), and a Close made there would run nested in it; a    */
        /* Close made while the closing handler runs would nest one close in  */
        /* another.  Posting lets the stack unwind first.                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : each waiting close posted once, or left waiting.        */
        /*--------------------------------------------------------------------*/
        private void Hexf_PostReadyCloses()
        {
            HSC_clsHexSaveCapture objCapture;

            if (Hexf_iLocalWork != 0 || Hexf_iClosing != 0)
            {   /* Still inside the window's own work or its closing handler:   */
                /* left waiting; their own finally calls this again.            */
                return;
            }

            while (Hexf_lstCloseEvaluations.Count != 0)
            {   /* One waiting close per iteration, taken off the list BEFORE   */
                /* it is posted so it is posted once; it reports its save's     */
                /* number even if newer typing keeps the window open.           */
                objCapture = Hexf_lstCloseEvaluations[0];
                Hexf_lstCloseEvaluations.RemoveAt(0);
                BeginInvoke(new Action(objCapture.HSC_RunClose));
            }
        }

        /*--------------------------------------------------------------------*/
        /* HEXF_CompleteDeferredClose:                                        */
        /*                                                                    */
        /* The close a successful save asked for, now that the stack it was   */
        /* asked from has unwound: the window closes only if nothing has      */
        /* changed since -- no typing, no other save waiting or applied, no   */
        /* work of its own running.  Either way the save's number is reported */
        /* to the coordinator, whose close question waits for it.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objCapture : the save's values.                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window closed, or kept; the report made.            */
        /*--------------------------------------------------------------------*/
        internal void HEXF_CompleteDeferredClose(HSC_clsHexSaveCapture objCapture)
        {
            try
            {   /* The decision, on a later turn of the UI thread, never   */
                /* inside the FormClosing that asked for it.               */
                if (!IsDisposed && !Hexf_bDirty && Hexf_iPendingSaves == 0 &&
                    Hexf_iLocalWork == 0 && Hexf_iClosing == 0 && Hexf_view.HEX_Revision() == objCapture.HSC_lRevision &&
                    Hexf_lAcknowledgedOrder == objCapture.HSC_lSaveOrder)
                {   /* Still exactly this save's content, nothing waiting:   */
                    /* closed.  FormClosing then finds it clean.             */
                    Close();
                }
            }
            finally
            {   /* Reported whether or not it closed. */
                Hexf_PostFact(objCapture.HSC_lSaveOrder);
            }
        }


        /*--------------------------------------------------------------------*/
        /* HEXF_ApplyTargetChoice:                                            */
        /*                                                                    */
        /* A Save As's destination has arrived (HPI_OnChosen): save to it --  */
        /* but only if the window still writes where it did when the Save As  */
        /* was asked.  A closed window or a window a save has moved since     */
        /* drops it, silently; the content is not touched.  A cancelled or    */
        /* refused picker drops it too -- and when that Save As was chosen    */
        /* from the window's own close question, the close is asked again,    */
        /* so the question shows once more and its own Cancel is what keeps   */
        /* the window.                                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objIntent : the Save As's values.                              */
        /*     objTarget : the destination; null when the picker was          */
        /*                 cancelled or refused.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the save started, the close asked again, or the answer  */
        /*            dropped.                                                */
        /*--------------------------------------------------------------------*/
        internal void HEXF_ApplyTargetChoice(HPI_clsHexPickIntent objIntent, OFD_clsOpenedFile objTarget)
        {
            if (objTarget == null && objIntent.HPI_bCloseWhenSaved && !IsDisposed &&
                Hexf_file == objIntent.HPI_objBinding)
            {   /* No destination for a Save As the user's own close asked    */
                /* for: the close posted to the window's queue, never made    */
                /* from here -- this answer can arrive inside a picker's      */
                /* call -- so the close question shows again once the stack   */
                /* has unwound.  (Under the worker's close question the       */
                /* worker asks again itself; that intent does not close on    */
                /* save, so it never reaches here.)                           */
                Hexf_objQueue.WQU_Post((int)Hexf_enmItem.CloseAgain, null);
                return;
            }

            if (objTarget == null || IsDisposed || Hexf_file != objIntent.HPI_objBinding)
            {   /* No destination, no window, or a window moved since: the   */
                /* answer belongs to a state that is gone.                   */
                return;
            }

            /* The content taken NOW, when the destination is known -- not   */
            /* when the Save As was asked.                                   */
            Hexf_SaveTo(objTarget, objIntent.HPI_bCloseWhenSaved);
        }

        /*--------------------------------------------------------------------*/
        /* HEXF_AbortStaging:                                                 */
        /*                                                                    */
        /* Stop the snapshots this window is writing, after the user agreed   */
        /* to an Exit (CommandEditorCoordinator.cs).  Snapshots already       */
        /* handed to the worker are the worker's, and are not touched here.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : each running job told to stop (SJB_Abort).              */
        /*--------------------------------------------------------------------*/
        internal void HEXF_AbortStaging()
        {
            int i;

            /* The list walked as it is: SJB_Abort shows nothing and runs   */
            /* no message loop, so no other code runs and the list cannot   */
            /* change under the walk.                                       */
            for (i = 0; i < Hexf_lstJobs.Count; i++)
            {   /* One running snapshot job per iteration. */
                Hexf_lstJobs[i].SJB_Abort();
            }
        }

        /*--------------------------------------------------------------------*/
        /* HEXF_SetSourceUnavailable:                                         */
        /*                                                                    */
        /* The coordinator's word that the entry's image has gone or come     */
        /* back (CommandEditorCoordinator.cs): marked on the window's         */
        /* descriptor, so Save refuses the image and the close offers the     */
        /* Windows Save As instead.  It decides nothing about what the worker */
        /* may do.                                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bUnavailable : true when the image is not open.                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : OFD_bDetached set; never for a Windows file.            */
        /*--------------------------------------------------------------------*/
        internal void HEXF_SetSourceUnavailable(bool bUnavailable)
        {
            /* On the window's own descriptor only; a save already on its   */
            /* way has its own copy.                                        */
            Hexf_file.OFD_bDetached = !Hexf_file.OFD_bHostFile && bUnavailable;
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_OnSaveClicked:                                                */
        /*                                                                    */
        /* File > Save (Ctrl+S): the Save item posted to the window's queue,  */
        /* which saves the changed bytes when it runs it (Hexf_RunSave).      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item posted (or dropped as one already in hand).    */
        /*--------------------------------------------------------------------*/
        private void Hexf_OnSaveClicked(object objSender, EventArgs evArgs)
        {
            /* Posted, as Go To and Save As are: the Windows write, the    */
            /* snapshot under the busy box, any message box and the        */
            /* hand-over all run from the queue, never inside the click.   */
            Hexf_objQueue.WQU_Post((int)Hexf_enmItem.Save, null);
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_RunSave:                                                      */
        /*                                                                    */
        /* The Save item, run from the window's queue: save the changed bytes */
        /* where Save writes now, if a save may start at all.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the save started, or nothing done.                      */
        /*--------------------------------------------------------------------*/
        private void Hexf_RunSave()
        {
            if (!Hexf_file.OFD_CanRequestUserFile())
            {   /* Not now (the worker is busy, exiting or holding a          */
                /* picker), judged as the item runs: nothing is done, so no   */
                /* Windows file is written behind an Exit.                    */
                return;
            }

            /* The save.  Whether the worker may write the image is its own   */
            /* to decide when the save reaches it.                            */
            Hexf_Save();
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_OnSaveAsImageClicked:                                         */
        /*                                                                    */
        /* File > Save As > Image...: an image destination asked for          */
        /* (Hexf_RequestSaveAs).                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item posted (or dropped as one already in hand);    */
        /*            nothing is saved until a destination arrives.           */
        /*--------------------------------------------------------------------*/
        private void Hexf_OnSaveAsImageClicked(object objSender, EventArgs evArgs)
        {
            /* Posted to the window's own queue, which asks when it runs   */
            /* the item (Hexf_RunItem); a second click while it waits or   */
            /* runs is dropped there.                                      */
            Hexf_objQueue.WQU_Post((int)Hexf_enmItem.SaveAsImage, null);
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_OnSaveAsWindowsClicked:                                       */
        /*                                                                    */
        /* File > Save As > Windows...: a Windows destination asked for       */
        /* (Hexf_RequestSaveAs).                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item posted (or dropped as one already in hand);    */
        /*            nothing is saved until a destination arrives.           */
        /*--------------------------------------------------------------------*/
        private void Hexf_OnSaveAsWindowsClicked(object objSender, EventArgs evArgs)
        {
            /* Posted to the window's own queue, as the Image choice is. */
            Hexf_objQueue.WQU_Post((int)Hexf_enmItem.SaveAsWindows, null);
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_RequestSaveAs:                                                */
        /*                                                                    */
        /* Save As: ask for a destination (EditorSaveMenu.cs); the save is    */
        /* made only when it arrives (HEXF_ApplyTargetChoice).  The window's  */
        /* queue runs it for the two Save As items, which say which side      */
        /* (Hexf_RunItem).                                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bWindows : true for a Windows file, false for a place in the   */
        /*                image.                                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the request posted, or nothing done.                    */
        /*--------------------------------------------------------------------*/
        private void Hexf_RequestSaveAs(bool bWindows)
        {
            HPI_clsHexPickIntent objIntent;

            if (!Hexf_file.OFD_CanRequestUserFile())
            {   /* Not now: as for Save, the click does nothing. */
                return;
            }

            /* The Save As's record -- the window's descriptor now, which      */
            /* must still be it when the answer comes, and no close to         */
            /* follow -- and the request, answered through its HPI_OnChosen.   */
            /* Nothing is taken or counted as waiting yet.                     */
            objIntent = new HPI_clsHexPickIntent(this, Hexf_file, false);
            SVM_clsSaveMenu.SVM_Request(this, Hexf_file, bWindows, objIntent.HPI_OnChosen);
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_OnCloseClicked:                                               */
        /*                                                                    */
        /* File > Close (Ctrl+W): the Close item posted; the window's queue   */
        /* closes the window when it runs it, Hexf_OnFormClosing asking about */
        /* unsaved changes.                                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item posted (or dropped as one already in hand).    */
        /*--------------------------------------------------------------------*/
        private void Hexf_OnCloseClicked(object objSender, EventArgs evArgs)
        {
            /* Posted: the close runs from the queue (Hexf_RunItem). */
            Hexf_objQueue.WQU_Post((int)Hexf_enmItem.Close, null);
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_OnGoToClicked:                                                */
        /*                                                                    */
        /* Edit > Go to Offset (Ctrl+G): the Go to Offset item posted to the  */
        /* window's queue, which asks for the offset and puts the caret       */
        /* there (Hexf_AskGoTo).                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the menu item (unused).                            */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item posted (or dropped as one already in hand).    */
        /*--------------------------------------------------------------------*/
        private void Hexf_OnGoToClicked(object objSender, EventArgs evArgs)
        {
            /* Posted to the window's own queue, which shows the prompt and   */
            /* moves the caret when it runs the item (Hexf_RunItem).          */
            Hexf_objQueue.WQU_Post((int)Hexf_enmItem.GoToOffset, null);
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_AskGoTo:                                                      */
        /*                                                                    */
        /* Go to Offset: ask for an offset.  The prompt is EDITOR-MODAL:      */
        /* shown modeless over this window, which is disabled while it is up, */
        /* so the main window and every other editor carry on; its answer     */
        /* comes back through the window's queue (Hexf_OnGoToPromptClosed,    */
        /* Hexf_ApplyGoToAnswer), which moves the caret.  The window's queue  */
        /* runs this for Edit > Go to Offset's item (Hexf_RunItem).           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the prompt shown, or nothing when one is already up.    */
        /*--------------------------------------------------------------------*/
        private void Hexf_AskGoTo()
        {
            if (Hexf_frmPrompt != null)
            {   /* A guard: the window is disabled while its prompt is up, so   */
                /* no second Go To can be asked for -- nothing more is shown.   */
                return;
            }

            /* The prompt, opening with the caret's offset in hex, under this   */
            /* window's (the program's) icon, kept as the window's own work     */
            /* until its answer is applied -- so a close the worker asks for    */
            /* waits for it (HEXF_HasLocalWork) and the window's own close      */
            /* keeps it open (Hexf_PresentClose).  Its closing only posts       */
            /* (Hexf_OnGoToPromptClosed).                                       */
            Hexf_frmPrompt = new PRM_clsPromptForm(Hexf_fntDialog, "Go to offset", "Offset (decimal, or hex starting with 0x):",
                "0x" + Hexf_view.HEX_CaretOffset().ToString("X"));
            Hexf_frmPrompt.Icon = this.Icon;
            Hexf_frmPrompt.FormClosed += Hexf_OnGoToPromptClosed;

            /* Placed over the middle of this window (a modeless window is       */
            /* not centred by the framework), this window disabled, then the     */
            /* prompt shown modeless and owned by it -- so it stays above this   */
            /* window, and it alone of the program is held up.                   */
            PRM_clsPromptForm.PRM_CentreOver(Hexf_frmPrompt, this);
            this.Enabled = false;
            Hexf_frmPrompt.Show(this);
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_OnGoToPromptClosed:                                           */
        /*                                                                    */
        /* The Go To prompt has closed (OK, Cancel, Escape or its close box): */
        /* its answer posted to the window's queue, which applies it          */
        /* (Hexf_ApplyGoToAnswer).  Nothing else is done here.                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the prompt.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the answer posted.                                      */
        /*--------------------------------------------------------------------*/
        private void Hexf_OnGoToPromptClosed(object objSender, FormClosedEventArgs evArgs)
        {
            /* Posted with the prompt it came from. */
            Hexf_objQueue.WQU_Post((int)Hexf_enmItem.GoToAnswer, objSender);
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_ApplyGoToAnswer:                                              */
        /*                                                                    */
        /* The Go To prompt's answer, run by the window's queue: the window   */
        /* enabled again and the prompt released, then -- when OK was         */
        /* pressed -- the caret moved to the offset typed (Hexf_GoToTyped).   */
        /* A close that waited for the prompt is then told it may go on.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmPrompt : the prompt that closed.                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the caret moved, the status bar told, or nothing for a  */
        /*            cancel.                                                 */
        /*--------------------------------------------------------------------*/
        private void Hexf_ApplyGoToAnswer(PRM_clsPromptForm frmPrompt)
        {
            if (frmPrompt != Hexf_frmPrompt)
            {   /* A guard: not the prompt this window has up -- nothing to   */
                /* apply.                                                     */
                return;
            }

            /* The prompt released and the window enabled again, before the    */
            /* caret moves, so the move is shown in a live window.  Its        */
            /* answer is still read below, after the dispose: PRM_bAccepted    */
            /* and PRM_sText are plain fields set at OK (PromptForm.cs), not   */
            /* read from its controls.                                         */
            Hexf_frmPrompt = null;
            frmPrompt.Dispose();
            this.Enabled = true;
            this.Activate();

            if (frmPrompt.PRM_bAccepted)
            {   /* OK: the text typed, without surrounding spaces, read and   */
                /* gone to.                                                   */
                Hexf_GoToTyped(frmPrompt.PRM_sText.Trim());
            }

            /* The window's own work has returned: a close question that   */
            /* waited for the prompt is looked at again.                   */
            Hexf_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_GoToTyped:                                                    */
        /*                                                                    */
        /* Put the caret at the offset the Go To prompt was given: decimal,   */
        /* or hexadecimal when it starts with "0x".                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sTyped : the text typed, trimmed.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the caret is at the offset, or the status bar says the  */
        /*            text typed is not one.                                  */
        /*--------------------------------------------------------------------*/
        private void Hexf_GoToTyped(string sTyped)
        {
            long   lOffset;
            bool   bParsed;

            if (sTyped.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {   /* Hexadecimal: the digits after "0x" or "0X".  Sixteen     */
                /* digits with the top bit set read as a negative number,   */
                /* which HEX_GoTo takes to the start of the file.           */
                bParsed = long.TryParse(sTyped.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out lOffset);
            }
            else
            {   /* Decimal: digits only -- NumberStyles.None takes no sign,   */
                /* spaces or thousands separators.                            */
                bParsed = long.TryParse(sTyped, NumberStyles.None, CultureInfo.InvariantCulture, out lOffset);
            }

            if (!bParsed)
            {   /* Not a number either way. */
                Hexf_tslMessage.Text = "\"" + sTyped + "\" is not an offset.";
                return;
            }

            /* An offset: any earlier message cleared, the caret put there   */
            /* (an offset past the end goes to the last byte, HEX_GoTo),     */
            /* and the focus back on the dump, so typing goes on there.      */
            Hexf_tslMessage.Text = "";
            Hexf_view.HEX_GoTo(lOffset);
            Hexf_view.Focus();
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_RunItem:                                                      */
        /*                                                                    */
        /* The window's own queue's runner (WindowQueue.cs): one item the     */
        /* handlers posted, run to its end -- the offset asked and the caret  */
        /* moved, the Save As destination asked for, or the close asked       */
        /* again.  The queue calls it once per item and never inside          */
        /* another, so a second click while one runs is never run inside it.  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iItem   : the item, a Hexf_enmItem value.                      */
        /*     objArgs : GoToAnswer's prompt, AskClose's question display (or */
        /*               null), CloseAnswer's question form, TextSize's size  */
        /*               in points (boxed); unused by every other item.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the item's work done, or nothing for an unknown item.   */
        /*--------------------------------------------------------------------*/
        private void Hexf_RunItem(int iItem, object objArgs)
        {
            switch ((Hexf_enmItem)iItem)
            {   /* One case per item: its own function. */
                case Hexf_enmItem.GoToOffset:
                    /* The prompt, then the caret moved. */
                    Hexf_AskGoTo();
                    break;
                case Hexf_enmItem.SaveAsImage:
                    /* An image destination asked for (false: not Windows). */
                    Hexf_RequestSaveAs(false);
                    break;
                case Hexf_enmItem.SaveAsWindows:
                    /* A Windows destination asked for. */
                    Hexf_RequestSaveAs(true);
                    break;
                case Hexf_enmItem.CloseAgain:
                    /* The close asked again: Close() raises FormClosing,         */
                    /* whose question (Hexf_PresentClose) shows once more while   */
                    /* the bytes are still unsaved -- or, saved or discarded      */
                    /* meanwhile, simply closes.  A window already gone is        */
                    /* left alone: there is nothing to ask.                       */
                    if (!IsDisposed)
                    {   /* Still open: closed, or asked about, as any close is. */
                        Close();
                    }
                    break;
                case Hexf_enmItem.GoToAnswer:
                    /* The Go To prompt's answer: the window enabled, then   */
                    /* the caret moved.                                      */
                    Hexf_ApplyGoToAnswer(objArgs as PRM_clsPromptForm);
                    break;
                case Hexf_enmItem.AskClose:
                    /* The close question, shown editor-modal. */
                    Hexf_ShowCloseQuestion(objArgs as PRS_clsPresentation);
                    break;
                case Hexf_enmItem.CloseAnswer:
                    /* The close question's answer: the window enabled, then   */
                    /* the choice acted on.                                    */
                    Hexf_ApplyCloseAnswer(objArgs as Form);
                    break;
                case Hexf_enmItem.Save:
                    /* File > Save, if a save may start now. */
                    Hexf_RunSave();
                    break;
                case Hexf_enmItem.Close:
                    /* File > Close: the ordinary close -- FormClosing decides    */
                    /* (unsaved bytes, a save waiting, work running) before the   */
                    /* file is let go.  A window already gone is left alone.      */
                    if (!IsDisposed)
                    {   /* Still open: closed, or asked about, as any close is. */
                        Close();
                    }
                    break;
                case Hexf_enmItem.TextSize:
                    /* The size the chosen item stood for, remembered and shown. */
                    Hexf_ApplyTextSize((int)objArgs);
                    break;
                default:
                    /* No such item (a guard: only the handlers above post):   */
                    /* nothing done.                                           */
                    break;
            }
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_OnScroll:                                                     */
        /*                                                                    */
        /* The scroll bar was moved by the user: scroll the dump to match.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the scroll bar.                                    */
        /*     evScroll  : the scroll event; its NewValue is the top STEP     */
        /*                 (Hexf_lRowsPerStep rows each; one below 16 GiB).   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the dump shows the bar's row at its top.                */
        /*--------------------------------------------------------------------*/
        private void Hexf_OnScroll(object objSender, ScrollEventArgs evScroll)
        {
            if (Hexf_bSyncing)
            {   /* The program is setting the bar from the dump: not the     */
                /* user, so the dump is not moved back.  Whether setting     */
                /* the bar in code raises Scroll at all is the framework's   */
                /* (not established here); the flag guards against it        */
                /* either way.                                               */
                return;
            }

            /* A step is Hexf_lRowsPerStep rows (one, below 16 GiB). */
            Hexf_view.HEX_SetTopRow((long)evScroll.NewValue * Hexf_lRowsPerStep);
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_OnResize:                                                     */
        /*                                                                    */
        /* The window changed size.  Docking has placed the controls and the  */
        /* dump reports its own change of height; this keeps the bars in step */
        /* when only the window's state changed (restored from minimized).    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the window.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the bars match the dump.                                */
        /*--------------------------------------------------------------------*/
        private void Hexf_OnResize(object objSender, EventArgs evArgs)
        {
            if (this.WindowState != FormWindowState.Minimized)
            {   /* There is something showing to keep in step. */
                Hexf_RefreshBars();
            }
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_OnFormClosing:                                                */
        /*                                                                    */
        /* The window is asked to close: decide, about unsaved bytes and work */
        /* still running (Hexf_PresentClose), counting the handler as the     */
        /* window's own work while it runs.  It shows nothing: unsaved bytes  */
        /* only post the close question to the window's queue, so a close a   */
        /* save asked for is never posted from inside this handler.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the window (unused).                               */
        /*     evClosing : the closing event; cancelled to keep the window.   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the close allowed or cancelled; any close a save asked  */
        /*            for is posted once this handler has returned.           */
        /*--------------------------------------------------------------------*/
        private void Hexf_OnFormClosing(object objSender, FormClosingEventArgs evClosing)
        {
            /* Inside the handler from here to the finally (a count of the   */
            /* UI's own nesting; nothing to do with what the worker runs).   */
            Hexf_iClosing = checked(Hexf_iClosing + 1);
            try
            {   /* The decision, released in the finally however it ends. */
                Hexf_PresentClose(evClosing);
            }
            finally
            {   /* Out of the handler: any close a save asked for may be   */
                /* posted now, as it can no longer nest in this one.       */
                Hexf_iClosing--;
                Hexf_PostReadyCloses();

                /* The coordinator told, so a close question that waited   */
                /* while this handler ran is looked at again (it finds     */
                /* the close question asked for, and waits on).            */
                Hexf_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_PresentClose:                                                 */
        /*                                                                    */
        /* Whether the window may close now -- a decision only, made inside   */
        /* the closing handler, which shows nothing.  It keeps itself open    */
        /* while a save waits or its own work runs (its close question among  */
        /* it); closes at once when nothing is unsaved, or when a Discard     */
        /* answer is closing it; and otherwise keeps the window open and      */
        /* posts the close question to the window's queue                     */
        /* (Hexf_ShowCloseQuestion), with the worker's close question's       */
        /* display when one is on the stack -- taken now, the one moment it   */
        /* can be.                                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     evClosing : the closing event; cancelled to keep the window.   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the close allowed (nothing unsaved, or an explicit      */
        /*            discard) or cancelled, the question posted when there   */
        /*            are unsaved bytes to ask about.                         */
        /*--------------------------------------------------------------------*/
        private void Hexf_PresentClose(FormClosingEventArgs evClosing)
        {
            PRS_clsPresentation objPresentation;

            if (Hexf_iPendingSaves != 0 || Hexf_iLocalWork != 0 || Hexf_frmPrompt != null ||
                Hexf_bCloseAsked || Hexf_objCloseQuestion != null)
            {   /* A save's answer is still to come, or the window's own     */
                /* work is running -- its Go To prompt or its close          */
                /* question (asked for or up) among it: kept open, as each   */
                /* still needs the window.                                   */
                evClosing.Cancel = true;
                return;
            }

            if (Hexf_bDiscardOnClose)
            {   /* The close a Discard answer makes (Hexf_ApplyCloseAnswer):   */
                /* closes, the changed bytes let go -- that answer was the     */
                /* question, so it is not asked again.                         */
                return;
            }

            if (!Hexf_bDirty)
            {   /* Nothing unsaved: closes, no question. */
                return;
            }

            /* The worker's close question for this editor, if one is        */
            /* showing (an Exit's): its display is on the stack only while   */
            /* this handler runs, so it is taken now and carried with the    */
            /* post -- the question it answers.                              */
            objPresentation = QWN_clsQuestionWindows.QWN_CurrentEditorQuestion(Hexf_file.OFD_lEditorId);
            if (objPresentation == null && !Hexf_file.OFD_CanRequestUserFile())
            {   /* The user's own close (no worker question) while the        */
                /* worker is busy: kept open, with no question -- a save      */
                /* could not start now anyway.  A close the worker asks for   */
                /* still gets its answers.                                    */
                evClosing.Cancel = true;
                return;
            }

            /* Kept open, and the question posted to the window's queue,      */
            /* which shows it once this handler has returned -- a handler     */
            /* opens nothing.  Marked as asked for meanwhile, so the window   */
            /* counts it as its own work (HEXF_HasLocalWork) and a second     */
            /* close posts no second question.                                */
            evClosing.Cancel = true;
            Hexf_bCloseAsked = true;
            Hexf_objQueue.WQU_Post((int)Hexf_enmItem.AskClose, objPresentation);
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_ShowCloseQuestion:                                            */
        /*                                                                    */
        /* The close question, run by the window's queue for a close that     */
        /* found unsaved bytes (Hexf_PresentClose): Save / Save As Image /    */
        /* Save As Windows / Discard / Cancel, or, when the entry's image is  */
        /* gone, the recovery question (Save As to Windows, Discard,          */
        /* Cancel).  It is EDITOR-MODAL: shown modeless over this window,     */
        /* which is disabled while it is up, so the main window and every     */
        /* other editor carry on; its choice comes back through the queue     */
        /* (Hexf_OnCloseQuestionClosed, Hexf_ApplyCloseAnswer).  Kept as the  */
        /* window's own work while it is up, so a close the worker asks for   */
        /* waits for its answer; under the worker's close question it is      */
        /* kept under that question too, so an Exit taking its place takes it */
        /* down unanswered.                                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objPresentation : the worker's close question's display, as    */
        /*                       the closing handler took it; null for the    */
        /*                       window's own close.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the question shown; or, when there is nothing left to   */
        /*            ask, the close made again or the coordinator told.      */
        /*--------------------------------------------------------------------*/
        private void Hexf_ShowCloseQuestion(PRS_clsPresentation objPresentation)
        {
            Form frmQuestion;
            bool bOrdinary;
            bool bImageOpen;

            /* No longer only asked for: shown below, or not at all. */
            Hexf_bCloseAsked = false;

            if (IsDisposed)
            {   /* A guard: the window is gone -- nothing to ask about. */
                return;
            }

            if (objPresentation != null &&
                !objPresentation.PRS_objQuestion.QST_IsCurrent(objPresentation))
            {   /* The worker's question was answered or replaced (an Exit)   */
                /* since the close was asked: nothing is shown for it.  The   */
                /* coordinator is told, and a question shown again asks the   */
                /* close again under its new display.                         */
                Hexf_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
                return;
            }

            if (!Hexf_bDirty)
            {   /* The bytes were saved since the close was asked: nothing   */
                /* to ask.  The close made again, which now finds nothing    */
                /* unsaved and closes -- its FormClosed answering the        */
                /* worker's question, if there is one.                       */
                Close();
                return;
            }

            /* Which question: the ordinary one, unless this is an image   */
            /* entry whose image is gone, which cannot be saved there --   */
            /* then the recovery question (EditorSaveMenu.cs).             */
            bOrdinary = Hexf_file.OFD_bHostFile || !Hexf_file.OFD_bDetached;

            /* Save As Image is offered only by the ordinary question, only   */
            /* on the user's own close, and only while the Save As menu       */
            /* would offer its Image item -- the target picker and an image   */
            /* open.  Under the worker's close question it is greyed: the     */
            /* worker runs only a Windows Save As inside a close              */
            /* (Sys_CloseEditors), and an image Save As posted then could     */
            /* run only after that close had ended.                           */
            bImageOpen = bOrdinary && objPresentation == null && Hexf_file.OFD_pfnPickTarget != null &&
                Hexf_file.OFD_pfnImageAvailable != null && Hexf_file.OFD_pfnImageAvailable();

            /* The question, kept with the display it answers, under this   */
            /* window's icon; its closing only posts                        */
            /* (Hexf_OnCloseQuestionClosed).                                */
            Hexf_objCloseQuestion = RCD_clsRecoveryDialog.RCD_Create(Hexf_fntDialog, Hexf_file, bOrdinary, bImageOpen);
            Hexf_objCloseShown = objPresentation;
            frmQuestion = Hexf_objCloseQuestion.RCD_ReadPopup();
            frmQuestion.Icon = this.Icon;
            frmQuestion.FormClosed += Hexf_OnCloseQuestionClosed;
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

        /*--------------------------------------------------------------------*/
        /* Hexf_OnCloseQuestionClosed:                                        */
        /*                                                                    */
        /* The close question has closed (a button, Escape, its close box, or */
        /* an Exit taking it down): posted to the window's queue, which acts  */
        /* on its choice (Hexf_ApplyCloseAnswer).  Nothing else is done here. */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the question's form.                               */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the answer posted.                                      */
        /*--------------------------------------------------------------------*/
        private void Hexf_OnCloseQuestionClosed(object objSender, FormClosedEventArgs evArgs)
        {
            /* Posted with the form it came from. */
            Hexf_objQueue.WQU_Post((int)Hexf_enmItem.CloseAnswer, objSender);
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_ApplyCloseAnswer:                                             */
        /*                                                                    */
        /* The close question's choice, run by the window's queue in a live   */
        /* window: Discard closes and drops the changes; Save saves and       */
        /* closes once that save succeeds; either Save As asks for its        */
        /* destination; Cancel keeps the window.  A question an Exit took     */
        /* down acts on nothing.  Under the worker's close question, the      */
        /* choice is that question's answer.                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmQuestion : the question's form that closed.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the choice acted on, or nothing for a stale one.        */
        /*--------------------------------------------------------------------*/
        private void Hexf_ApplyCloseAnswer(Form frmQuestion)
        {
            RCD_clsRecoveryDialog objQuestion;
            PRS_clsPresentation objPresentation;
            QSC_clsWindowScope objScope;
            HPI_clsHexPickIntent objIntent;
            RCA_enmRecoveryChoice enmChoice;
            bool bOwnClose;

            if (Hexf_objCloseQuestion == null || frmQuestion != Hexf_objCloseQuestion.RCD_ReadPopup())
            {   /* A guard: not the question this window has up -- nothing   */
                /* to act on.                                                */
                return;
            }

            /* The question released and the window enabled again, before   */
            /* anything is acted on, so a save's messages and a Save As's   */
            /* picker meet a live window: its fields cleared, its record    */
            /* under the worker's question dropped, its choice read, its    */
            /* form disposed.                                               */
            objQuestion = Hexf_objCloseQuestion;
            objPresentation = Hexf_objCloseShown;
            Hexf_objCloseQuestion = null;
            Hexf_objCloseShown = null;
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
                /* bytes stay, and the coordinator is told; an Exit declined    */
                /* shows the question again, which asks this close again.       */
                Hexf_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
                return;
            }

            if (enmChoice == RCA_enmRecoveryChoice.Discard)
            {   /* Discard, said in so many words: the close made again,         */
                /* marked so it closes without asking (Hexf_PresentClose) --     */
                /* its FormClosed answering the worker's question, if there is   */
                /* one.  The mark is taken off whatever the close did, so it     */
                /* can never let a later close go unasked.                       */
                Hexf_bDiscardOnClose = true;
                try
                {   /* The close, which lets the changes go. */
                    Close();
                }
                finally
                {   /* Unmarked (harmless on a window now closed). */
                    Hexf_bDiscardOnClose = false;
                }
                return;
            }

            if (enmChoice == RCA_enmRecoveryChoice.Cancel)
            {   /* Cancel (Escape and the close box included): the window and   */
                /* its bytes stay.                                              */
                if (objPresentation != null)
                {   /* Under the worker's question: it is answered Cancel, so   */
                    /* its close stops (Eui_OnEditorFact).                      */
                    Hexf_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_DECLINED);
                }
                else
                {   /* The window's own close: only the coordinator told that   */
                    /* this window's own work has returned.                     */
                    Hexf_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
                }
                return;
            }

            /* A save or a Save As.  Under the worker's question it is made    */
            /* inside a record of that display (QWN_Begin), the one moment     */
            /* its question is again on the stack, so the save is captured     */
            /* as that question's answer (OFD_ForSave) and a Windows Save As   */
            /* goes back inside it (Main_RequestEditorTarget,                  */
            /* MainCommand.cs).                                                */
            objScope = null;
            if (objPresentation != null)
            {   /* The worker's question: its record begun. */
                objScope = QWN_clsQuestionWindows.QWN_Begin(objPresentation);
            }
            try
            {   /* The choice, by kind. */
                if (enmChoice == RCA_enmRecoveryChoice.Save)
                {   /* The save, asking to close once it succeeds; the close   */
                    /* is posted after this call has returned.                 */
                    Hexf_SaveTo(Hexf_file, true);
                }
                else
                {   /* A Save As -- to the image or to Windows from the         */
                    /* ordinary question, to Windows from the recovery one --   */
                    /* its destination asked for through the Save As menu's     */
                    /* own request, as File > Save As asks it.  It closes the   */
                    /* window afterwards only when this is the user's own       */
                    /* close; a picker cancelled then posts the close again     */
                    /* (HEXF_ApplyTargetChoice), and under the worker's         */
                    /* question the worker asks again itself.                   */
                    bOwnClose = objPresentation == null;
                    objIntent = new HPI_clsHexPickIntent(this, Hexf_file, bOwnClose);
                    SVM_clsSaveMenu.SVM_Request(this, Hexf_file,
                        enmChoice != RCA_enmRecoveryChoice.SaveAsImage, objIntent.HPI_OnChosen);
                }
            }
            finally
            {   /* The display's record ended, and the coordinator told that   */
                /* this window's own work has returned.                        */
                if (objScope != null)
                {   /* Begun above: ended. */
                    QWN_clsQuestionWindows.QWN_End(objScope);
                }
                Hexf_PostFact(EUI_clsEditorCoordinator.EUI_LOCAL_RETURN);
            }
        }

        /*---------------------------------------------------------------------*/
        /* HEXF_ReleaseBaseline:                                               */
        /*                                                                     */
        /* Close the window's handle on the file read.  Called when the        */
        /* window has closed (Hexf_OnFormClosed), and by the main window for   */
        /* a window made but never shown (MainCommand.cs), before the read-out */
        /* copy may be deleted.  Safe to call again, and for a window whose    */
        /* file never opened.                                                  */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     None.                                                           */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : no handle held; a failure to close throws.               */
        /*---------------------------------------------------------------------*/
        internal void HEXF_ReleaseBaseline()
        {
            if (Hexf_stmFile != null)
            {   /* A handle to close.  The field is cleared only AFTER the     */
                /* close, so a close that throws is not taken for a release.   */
                /* It is the handle on the file read now, which may not be     */
                /* where Save writes or where the file was opened from.        */
                Hexf_stmFile.Close();
                Hexf_stmFile = null;
            }
        }

        /*--------------------------------------------------------------------*/
        /* Hexf_OnFormClosed:                                                 */
        /*                                                                    */
        /* The window has closed: let go of the file it held open, so an      */
        /* image entry's temporary copy can be removed with its folder, and a */
        /* Windows file is no longer held by this program.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the window.                                        */
        /*     evClosed  : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the file is closed.                                     */
        /*--------------------------------------------------------------------*/
        private void Hexf_OnFormClosed(object objSender, FormClosedEventArgs evClosed)
        {
            /* Hooked in the constructor, so it runs before the             */
            /* coordinator's own FormClosed handler reports the end -- by   */
            /* which time the worker may delete the read-out copy, which    */
            /* must not still be open here.                                 */
            HEXF_ReleaseBaseline();
        }
    }
}
