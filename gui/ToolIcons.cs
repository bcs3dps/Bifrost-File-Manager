/*----------------------------------------------------------------------------*/
/* ToolIcons.cs                                                               */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The pictures on the main window's toolbar buttons.  Like the image pane's  */
/* entry icons (EntryIcons.cs) they are DRAWN, not loaded: each is a few      */
/* shapes on a 16-unit grid, scaled to whatever pixel size the toolbar uses,  */
/* so they stay sharp at any display scaling and the program carries no       */
/* picture files for them.                                                    */
/*                                                                            */
/* The folder and the page are EntryIcons.cs's, so those two shapes look the  */
/* same on the toolbar as they do in the lists.  The up arrow repeats         */
/* EntryIcons.cs's ".." arrow point for point (that one is private there),    */
/* so the toolbar's Up button matches the ".." row; a change to one should    */
/* be made to both.  The one caller is MainForm.cs, which makes each button's */
/* picture at the toolbar's icon size and remakes it when the size changes    */
/* (small or large: the View menu's choice, or a window too narrow).          */
/*----------------------------------------------------------------------------*/

/* The framework: GDI+'s pictures, colours and shapes, and its smoothing,    */
/* saved-state and line-cap types.  Nothing of System itself is used here.   */
/* The pixel format is named in full where used.                             */
using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace BifrostFM
{
    internal static class TIC_ToolIcons
    {
        /* The pictures TIC_NewPicture can make -- documentation table.  The   */
        /* values are only names for TIC_NewPicture's switch, never list       */
        /* positions, so their order carries no meaning.                       */
        internal const int TIC_ICON_BACK       = 0;     // back to the folder shown before: an arrow pointing left
        internal const int TIC_ICON_UP         = 1;     // up one level: a folder with an arrow pointing up
        internal const int TIC_ICON_TO_IMAGE   = 2;     // copy to the left pane (import): a page and an arrow pointing left
        internal const int TIC_ICON_TO_HOST    = 3;     // copy to the right pane (extract): a page and an arrow pointing right
        internal const int TIC_ICON_CUT        = 4;     // cut: scissors
        internal const int TIC_ICON_COPY       = 5;     // copy: two pages
        internal const int TIC_ICON_PASTE      = 6;     // paste: a clipboard
        internal const int TIC_ICON_NEW_FOLDER = 7;     // new folder: a folder with a plus
        internal const int TIC_ICON_RENAME     = 8;     // rename: a text box with a caret
        internal const int TIC_ICON_DELETE     = 9;     // delete: a red cross
        internal const int TIC_ICON_PROPERTIES = 10;    // properties: a page with an "i"
        internal const int TIC_ICON_REFRESH    = 11;    // refresh: an arrow going round
        internal const int TIC_ICON_VIEWS      = 12;    // choose the view: four tiles
        internal const int TIC_ICON_OPEN       = 13;    // open an image: a drive with a folder
        internal const int TIC_ICON_NEW_IMAGE  = 14;    // new image: a drive with a plus
        internal const int TIC_ICON_WRITE      = 15;    // enable writing to the image: a pencil (drawn by the switch's default -- it has no case of its own)
        internal const int TIC_ICON_NEW_FILE   = 16;    // new file: a page with a plus
        internal const int TIC_ICON_CLOSE      = 17;    // close the image or device: a drive with a red cross

        /* The marks TIC_DrawDrive puts above the drive -- documentation   */
        /* table.  Only names for its branches, as the icons above are.    */
        private const int TIC_MARK_FOLDER      = 0;     // open one: a small folder
        private const int TIC_MARK_PLUS        = 1;     // make a new one: the green plus
        private const int TIC_MARK_CROSS       = 2;     // close the open one: a red cross

        /* The drawing's scale -- documentation table.  Every coordinate   */
        /* below is in grid units; TIC_NewPicture maps the grid onto the   */
        /* bitmap, so these two are the only sizes the shapes depend on.   */
        private const float TIC_GRID           = 16f;   // the grid every shape below is described on, units per picture side
        private const float TIC_SMALL          = 0.55f; // the scale a folder or page is drawn at when it is one part of a picture

        /* Colours -- documentation table.  Fixed values, so the pictures look   */
        /* the same under any theme; each colour stands for one kind of act      */
        /* (blue to go, green to add or copy, red to remove), and the blue is    */
        /* the same as EntryIcons.cs's link colour.                              */
        private static readonly Color TIC_clrGo     = Color.FromArgb(30, 100, 200);     // navigation arrows, the caret, the "i" disc, the scissors' handles: blue (EntryIcons.cs's EIC_clrLink)
        private static readonly Color TIC_clrAdd    = Color.FromArgb(40, 150, 70);      // copy arrows, plus signs, refresh, the drive's activity light: green
        private static readonly Color TIC_clrRemove = Color.FromArgb(205, 50, 40);      // the delete cross, the close mark's cross and the pencil's eraser: red
        private static readonly Color TIC_clrInk    = Color.FromArgb(70, 76, 88);       // outlines and dark strokes: blades, "letters", the drive's slot, the pencil's point
        private static readonly Color TIC_clrPaper  = Color.FromArgb(255, 255, 255);    // white fills, the white halos that part a mark from what is under it, the "i"
        private static readonly Color TIC_clrBoard  = Color.FromArgb(200, 160, 100);    // the clipboard's board
        private static readonly Color TIC_clrMetal  = Color.FromArgb(175, 182, 194);    // the drive's body, the clipboard's clip and the lines on its sheet
        private static readonly Color TIC_clrTile   = Color.FromArgb(95, 140, 180);     // the four view tiles
        private static readonly Color TIC_clrPencil = Color.FromArgb(240, 170, 40);     // the pencil's barrel

        /*--------------------------------------------------------------------*/
        /* TIC_NewPicture:                                                    */
        /*                                                                    */
        /* Make one toolbar picture at a given size.  A new bitmap is drawn   */
        /* on every call, at exactly the size asked for, rather than one      */
        /* drawing kept and stretched: MainForm.cs asks again whenever the    */
        /* toolbar switches between its small and large pictures (the View    */
        /* menu's choice, or a window too narrow for the large ones), so the  */
        /* shapes are always drawn for the pixels they will occupy.           */
        /* The caller owns the bitmap: a toolbar button keeps it as its       */
        /* Image, and MainForm.cs disposes of the old one when it gives a     */
        /* button a new picture.                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iIcon   : a TIC_ICON_* value; TIC_ICON_WRITE, and any value    */
        /*               not listed, draws the pencil.                        */
        /*     sizIcon : the picture's size, pixels; width and height scale   */
        /*               the grid separately, so a non-square size stretches. */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Bitmap : the picture, 32-bit with alpha, on a transparent      */
        /*              ground; the caller disposes of it.                    */
        /*--------------------------------------------------------------------*/
        internal static Bitmap TIC_NewPicture(int iIcon, Size sizIcon)
        {
            Bitmap bmpIcon;

            /* 32 bits with alpha, so the smoothed edges blend into the   */
            /* toolbar's own background.                                  */
            bmpIcon = new Bitmap(sizIcon.Width, sizIcon.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            using (Graphics gfx = Graphics.FromImage(bmpIcon))
            {   /* Draw the picture onto the new bitmap.  The ground is cleared   */
                /* to transparent so only the shapes show, anti-aliasing keeps    */
                /* their edges smooth, and the surface is scaled so every shape   */
                /* below is written once in 16-unit grid terms and comes out at   */
                /* the button's size.  The surface is released when the block     */
                /* ends.                                                          */
                gfx.Clear(Color.Transparent);
                gfx.SmoothingMode = SmoothingMode.AntiAlias;
                gfx.ScaleTransform((float)sizIcon.Width / TIC_GRID, (float)sizIcon.Height / TIC_GRID);

                /* One case per picture.  TIC_ICON_WRITE has no case: the      */
                /* default draws the pencil, so it serves that button -- and   */
                /* any value not listed draws a pencil too.                    */
                switch (iIcon)
                {   /* One picture per case. */
                    case TIC_ICON_BACK:       TIC_DrawBack(gfx);                 break;
                    case TIC_ICON_UP:         TIC_DrawUp(gfx);                   break;
                    case TIC_ICON_TO_IMAGE:   TIC_DrawTransfer(gfx, true);       break;
                    case TIC_ICON_TO_HOST:    TIC_DrawTransfer(gfx, false);      break;
                    case TIC_ICON_CUT:        TIC_DrawScissors(gfx);             break;
                    case TIC_ICON_COPY:       TIC_DrawTwoPages(gfx);             break;
                    case TIC_ICON_PASTE:      TIC_DrawClipboard(gfx);            break;
                    case TIC_ICON_NEW_FOLDER: TIC_DrawNewFolder(gfx);            break;
                    case TIC_ICON_RENAME:     TIC_DrawRename(gfx);               break;
                    case TIC_ICON_DELETE:     TIC_DrawCross(gfx);                break;
                    case TIC_ICON_PROPERTIES: TIC_DrawProperties(gfx);           break;
                    case TIC_ICON_REFRESH:    TIC_DrawRefresh(gfx);              break;
                    case TIC_ICON_VIEWS:      TIC_DrawTiles(gfx);                break;
                    case TIC_ICON_OPEN:       TIC_DrawDrive(gfx, TIC_MARK_FOLDER); break;
                    case TIC_ICON_NEW_IMAGE:  TIC_DrawDrive(gfx, TIC_MARK_PLUS);   break;
                    case TIC_ICON_CLOSE:      TIC_DrawDrive(gfx, TIC_MARK_CROSS);  break;
                    case TIC_ICON_NEW_FILE:   TIC_DrawNewFile(gfx);              break;
                    default:                  TIC_DrawPencil(gfx);               break;
                }
            }

            /* The finished picture; the caller gives it to a toolbar button,   */
            /* which keeps it as its Image.                                     */
            return(bmpIcon);
        }

        /*--------------------------------------------------------------------*/
        /* TIC_DrawSmall:                                                     */
        /*                                                                    */
        /* Draw EntryIcons.cs's folder or page at reduced size with its top   */
        /* left corner at a given grid position, as one part of a larger      */
        /* picture.  The surface's own scaling is put back afterwards.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx     : the surface, in grid units.                          */
        /*     bFolder : true for the folder, false for the page.             */
        /*     fLeft   : where the reduced shape's grid begins, x.            */
        /*     fTop    : where the reduced shape's grid begins, y.            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the small shape is drawn.                               */
        /*--------------------------------------------------------------------*/
        private static void TIC_DrawSmall(Graphics gfx, bool bFolder, float fLeft, float fTop)
        {
            GraphicsState gstSaved;

            /* Keep the surface's scaling, then move the origin to the given    */
            /* corner and shrink: the shared drawing works in its own 16-unit   */
            /* grid, so this places that whole grid, reduced, where the part    */
            /* belongs.                                                         */
            gstSaved = gfx.Save();
            gfx.TranslateTransform(fLeft, fTop);
            gfx.ScaleTransform(TIC_SMALL, TIC_SMALL);

            if (bFolder)
            {   /* The lists' folder. */
                EIC_EntryIcons.EIC_DrawFolder(gfx);
            }
            else
            {   /* The lists' file page. */
                EIC_EntryIcons.EIC_DrawPage(gfx);
            }

            /* Put the surface back as it was, so whatever the caller draws   */
            /* next is at full size again.                                    */
            gfx.Restore(gstSaved);
        }

        /*--------------------------------------------------------------------*/
        /* TIC_DrawPlus:                                                      */
        /*                                                                    */
        /* A green plus sign centred on a grid position: the mark for "a new  */
        /* one of these".                                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx     : the surface, in grid units.                          */
        /*     fCentreX : the plus's centre, x.                               */
        /*     fCentreY : the plus's centre, y.                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the plus is drawn.                                      */
        /*--------------------------------------------------------------------*/
        private static void TIC_DrawPlus(Graphics gfx, float fCentreX, float fCentreY)
        {
            using (Pen penHalo = new Pen(TIC_clrPaper, 3.4f))
            using (Pen penPlus = new Pen(TIC_clrAdd, 2f))
            {   /* Draw the plus: a wider white cross first, then the green one   */
                /* on top, so a thin white halo shows round it and the plus       */
                /* reads clearly over whatever is beneath -- a folder, a page,    */
                /* the drive.  The pens are released when the block ends.         */
                gfx.DrawLine(penHalo, fCentreX - 3f, fCentreY, fCentreX + 3f, fCentreY);
                gfx.DrawLine(penHalo, fCentreX, fCentreY - 3f, fCentreX, fCentreY + 3f);
                gfx.DrawLine(penPlus, fCentreX - 2.6f, fCentreY, fCentreX + 2.6f, fCentreY);
                gfx.DrawLine(penPlus, fCentreX, fCentreY - 2.6f, fCentreX, fCentreY + 2.6f);
            }
        }

        /*--------------------------------------------------------------------*/
        /* TIC_DrawBack:                                                      */
        /*                                                                    */
        /* A broad blue arrow pointing left: back to where the pane was.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx : the surface, in grid units.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the arrow is drawn.                                     */
        /*--------------------------------------------------------------------*/
        private static void TIC_DrawBack(Graphics gfx)
        {
            PointF[] aptArrow;

            /* The arrow's outline, from its point clockwise: a head and a   */
            /* broad shaft, filling most of the picture.                     */
            aptArrow = new PointF[]
            {
                new PointF(1.5f, 8f),     // the point
                new PointF(8f, 2f),       // top of the head
                new PointF(8f, 5.5f),     // where the head meets the shaft, top
                new PointF(14.5f, 5.5f),  // the shaft's far end, top
                new PointF(14.5f, 10.5f), // the shaft's far end, bottom
                new PointF(8f, 10.5f),    // where the head meets the shaft, bottom
                new PointF(8f, 14f)       // bottom of the head
            };

            using (SolidBrush brArrow = new SolidBrush(TIC_clrGo))
            {   /* Fill the arrow in the navigation blue -- one solid shape,   */
                /* with no edge line, since nothing lies beneath it; the       */
                /* brush is released when the block ends.                      */
                gfx.FillPolygon(brArrow, aptArrow);
            }
        }

        /*--------------------------------------------------------------------*/
        /* TIC_DrawUp:                                                        */
        /*                                                                    */
        /* The usual "up one level" picture: a folder with an arrow pointing  */
        /* up over its right-hand side.                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx : the surface, in grid units.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the folder and arrow are drawn.                         */
        /*--------------------------------------------------------------------*/
        private static void TIC_DrawUp(Graphics gfx)
        {
            PointF[] aptArrow;

            /* The same outline as EntryIcons.cs's ".." arrow, point for       */
            /* point (see the file header), so the button and the row match.   */
            aptArrow = new PointF[]
            {
                new PointF(11f, 4.5f),    // the point
                new PointF(15.5f, 9.5f),  // right of the head
                new PointF(12.6f, 9.5f),  // where the head meets the shaft, right
                new PointF(12.6f, 15f),   // the shaft's foot, right
                new PointF(9.4f, 15f),    // the shaft's foot, left
                new PointF(9.4f, 9.5f),   // where the head meets the shaft, left
                new PointF(6.5f, 9.5f)    // left of the head
            };

            /* The lists' folder at full size, as the ground of the picture. */
            EIC_EntryIcons.EIC_DrawFolder(gfx);

            using (SolidBrush brArrow = new SolidBrush(TIC_clrGo))
            using (Pen penHalo = new Pen(TIC_clrPaper, 1f))
            {   /* Paint the arrow over the folder, then edge it in white so   */
                /* it stands clear of the gold beneath; the brush and pen      */
                /* are released when the block ends.                           */
                gfx.FillPolygon(brArrow, aptArrow);
                gfx.DrawPolygon(penHalo, aptArrow);
            }
        }

        /*--------------------------------------------------------------------*/
        /* TIC_DrawTransfer:                                                  */
        /*                                                                    */
        /* A copy across to the other pane: a small page on the side the copy */
        /* comes FROM and a green arrow pointing to the side it goes TO.  The */
        /* image pane is on the left, so "to the image" points left.          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx      : the surface, in grid units.                         */
        /*     bToImage : true for the arrow pointing left (import), false    */
        /*                for the arrow pointing right (extract).             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the page and arrow are drawn.                           */
        /*--------------------------------------------------------------------*/
        private static void TIC_DrawTransfer(Graphics gfx, bool bToImage)
        {
            PointF[] aptArrow;

            if (bToImage)
            {   /* Import: the page on the right (the Windows side) and the   */
                /* arrow running from it to the left edge, so the picture     */
                /* matches the panes' places -- image left, Windows right.    */
                /* The page is drawn first, so where the arrow's white edge   */
                /* meets the page's border the arrow is on top.               */
                TIC_DrawSmall(gfx, false, 7.5f, 3.5f);

                /* The arrow's outline, clockwise from its point at the left   */
                /* edge: the head, then a shaft ending at x 9 -- at the        */
                /* page's left edge (the page spans about 9.15 to 14.65 at     */
                /* this scale), so arrow and page read as one movement.        */
                aptArrow = new PointF[]
                {
                    new PointF(0.5f, 8f),    // the point
                    new PointF(5f, 3.5f),    // top of the head
                    new PointF(5f, 6.2f),    // head meets shaft, top
                    new PointF(9f, 6.2f),    // the shaft's far end, top
                    new PointF(9f, 9.8f),    // the shaft's far end, bottom
                    new PointF(5f, 9.8f),    // head meets shaft, bottom
                    new PointF(5f, 12.5f)    // bottom of the head
                };
            }
            else
            {   /* Extract: the mirror image -- the page on the left (the   */
                /* image side), the arrow running to the right edge.        */
                TIC_DrawSmall(gfx, false, -0.5f, 3.5f);

                /* The same outline mirrored about the picture's centre line   */
                /* (x becomes 16 - x): point at the right edge, the shaft      */
                /* ending at x 7, just right of the page (about 1.15 to        */
                /* 6.65 here -- the page is not mirrored exactly).             */
                aptArrow = new PointF[]
                {
                    new PointF(15.5f, 8f),   // the point
                    new PointF(11f, 3.5f),   // top of the head
                    new PointF(11f, 6.2f),   // head meets shaft, top
                    new PointF(7f, 6.2f),    // the shaft's far end, top
                    new PointF(7f, 9.8f),    // the shaft's far end, bottom
                    new PointF(11f, 9.8f),   // head meets shaft, bottom
                    new PointF(11f, 12.5f)   // bottom of the head
                };
            }

            using (SolidBrush brArrow = new SolidBrush(TIC_clrAdd))
            using (Pen penHalo = new Pen(TIC_clrPaper, 0.8f))
            {   /* Paint the arrow in the copy green, then edge it in white so   */
                /* it stays distinct where it meets the page's edge; the         */
                /* brush and pen are released when the block ends.               */
                gfx.FillPolygon(brArrow, aptArrow);
                gfx.DrawPolygon(penHalo, aptArrow);
            }
        }

        /*--------------------------------------------------------------------*/
        /* TIC_DrawScissors:                                                  */
        /*                                                                    */
        /* A pair of scissors, blades up and crossed, handles at the bottom.  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx : the surface, in grid units.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the scissors are drawn.                                 */
        /*--------------------------------------------------------------------*/
        private static void TIC_DrawScissors(Graphics gfx)
        {
            using (Pen penBlade = new Pen(TIC_clrInk, 1.5f))
            using (Pen penHandle = new Pen(TIC_clrGo, 1.5f))
            {   /* Draw the scissors: two dark blades crossing in the middle,   */
                /* then a blue ring handle at the foot of each blade, the       */
                /* colour setting the handles apart from the blades at small    */
                /* sizes.  The pens are released when the block ends.           */
                gfx.DrawLine(penBlade, 4.5f, 1.5f, 10.5f, 10.5f);
                gfx.DrawLine(penBlade, 11.5f, 1.5f, 5.5f, 10.5f);
                gfx.DrawEllipse(penHandle, 2f, 10f, 4.2f, 4.2f);
                gfx.DrawEllipse(penHandle, 9.8f, 10f, 4.2f, 4.2f);
            }
        }

        /*--------------------------------------------------------------------*/
        /* TIC_DrawTwoPages:                                                  */
        /*                                                                    */
        /* Two pages, one overlapping the other: a copy.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx : the surface, in grid units.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the two pages are drawn.                                */
        /*--------------------------------------------------------------------*/
        private static void TIC_DrawTwoPages(Graphics gfx)
        {
            GraphicsState gstSaved;

            /* The page behind, up and to the left; then the page in front.    */
            /* Each is the lists' page at three quarters of full size --       */
            /* larger than TIC_DrawSmall's parts, so the two pages between     */
            /* them fill the picture.  The surface's own scaling is kept and   */
            /* put back around each, as in TIC_DrawSmall.                      */
            gstSaved = gfx.Save();
            gfx.TranslateTransform(-1.5f, 0f);
            gfx.ScaleTransform(0.75f, 0.75f);
            EIC_EntryIcons.EIC_DrawPage(gfx);
            gfx.Restore(gstSaved);

            /* The page in front, down and to the right, drawn second so it   */
            /* covers the other's corner: the copy lying on the original.     */
            gstSaved = gfx.Save();
            gfx.TranslateTransform(4f, 4f);
            gfx.ScaleTransform(0.75f, 0.75f);
            EIC_EntryIcons.EIC_DrawPage(gfx);
            gfx.Restore(gstSaved);
        }

        /*--------------------------------------------------------------------*/
        /* TIC_DrawClipboard:                                                 */
        /*                                                                    */
        /* A clipboard: a board, the metal clip at its top, and a sheet of    */
        /* paper on it.                                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx : the surface, in grid units.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the clipboard is drawn.                                 */
        /*--------------------------------------------------------------------*/
        private static void TIC_DrawClipboard(Graphics gfx)
        {
            float fLine;

            using (SolidBrush brBoard = new SolidBrush(TIC_clrBoard))
            using (SolidBrush brSheet = new SolidBrush(TIC_clrPaper))
            using (SolidBrush brClip = new SolidBrush(TIC_clrMetal))
            using (Pen penEdge = new Pen(TIC_clrInk, 0.7f))
            using (Pen penLine = new Pen(TIC_clrMetal, 0.8f))
            {   /* Draw the clipboard from the back forward: the outlined board,   */
                /* the white sheet on it, the sheet's lines of "text", and last    */
                /* the metal clip, which overlaps the board's top edge as a        */
                /* real clip does.  The brushes and pens are released when the     */
                /* block ends.                                                     */
                gfx.FillRectangle(brBoard, 2.5f, 2.5f, 11f, 13f);
                gfx.DrawRectangle(penEdge, 2.5f, 2.5f, 11f, 13f);
                gfx.FillRectangle(brSheet, 4.5f, 5.5f, 7f, 8.5f);

                /* Three lines, at 7.5, 9.5 and 11.5 units down, inside the       */
                /* sheet (5.5 to 14).  The bound, 12, sits between the last       */
                /* line and the next (13.5), so the loop stops after three; the   */
                /* steps are exact in binary, so no rounding moves it.            */
                for (fLine = 7.5f; fLine <= 12f; fLine = fLine + 2f)
                {   /* One line of text per iteration. */
                    gfx.DrawLine(penLine, 5.5f, fLine, 10.5f, fLine);
                }

                /* The clip, drawn last so it sits on top of the board's edge. */
                gfx.FillRectangle(brClip, 5.5f, 1f, 5f, 3f);
                gfx.DrawRectangle(penEdge, 5.5f, 1f, 5f, 3f);
            }
        }

        /*--------------------------------------------------------------------*/
        /* TIC_DrawNewFolder:                                                 */
        /*                                                                    */
        /* A folder with a green plus at its top right: make a new folder.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx : the surface, in grid units.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the folder and plus are drawn.                          */
        /*--------------------------------------------------------------------*/
        private static void TIC_DrawNewFolder(Graphics gfx)
        {
            /* The lists' folder at full size, then the plus over its top   */
            /* right corner, its white halo keeping it clear of the gold.   */
            EIC_EntryIcons.EIC_DrawFolder(gfx);
            TIC_DrawPlus(gfx, 12f, 4f);
        }

        /*--------------------------------------------------------------------*/
        /* TIC_DrawNewFile:                                                   */
        /*                                                                    */
        /* A file page with a green plus at its top right: make a new file,   */
        /* the page being the same one the lists show for a file and the plus */
        /* in the same place the new-folder picture puts it, so the two read  */
        /* as a pair.                                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx : the surface, in grid units.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the page and plus are drawn.                            */
        /*--------------------------------------------------------------------*/
        private static void TIC_DrawNewFile(Graphics gfx)
        {
            /* The lists' page at full size, then the plus at the same spot    */
            /* as on the new-folder picture (over the page's folded corner).   */
            EIC_EntryIcons.EIC_DrawPage(gfx);
            TIC_DrawPlus(gfx, 12f, 4f);
        }

        /*--------------------------------------------------------------------*/
        /* TIC_DrawRename:                                                    */
        /*                                                                    */
        /* A text box holding a couple of "letters" and a blue text caret:    */
        /* type a new name.                                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx : the surface, in grid units.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the box and caret are drawn.                            */
        /*--------------------------------------------------------------------*/
        private static void TIC_DrawRename(Graphics gfx)
        {
            using (SolidBrush brBox = new SolidBrush(TIC_clrPaper))
            using (Pen penEdge = new Pen(TIC_clrInk, 0.8f))
            using (Pen penLetter = new Pen(TIC_clrInk, 1.8f))
            using (Pen penCaret = new Pen(TIC_clrGo, 1.2f))
            {   /* Draw an edit box being typed in: the outlined white box, two   */
                /* short dark strokes standing for text, and the blue I-shaped    */
                /* caret just after them -- a stem reaching above and below the   */
                /* box with a short bar at each end.  The brush and pens are      */
                /* released when the block ends.                                  */
                gfx.FillRectangle(brBox, 1f, 4.5f, 14f, 7f);
                gfx.DrawRectangle(penEdge, 1f, 4.5f, 14f, 7f);
                gfx.DrawLine(penLetter, 3f, 8f, 5.5f, 8f);
                gfx.DrawLine(penLetter, 6.5f, 8f, 8.5f, 8f);
                gfx.DrawLine(penCaret, 11f, 2.5f, 11f, 13.5f);
                gfx.DrawLine(penCaret, 9.5f, 2.5f, 12.5f, 2.5f);
                gfx.DrawLine(penCaret, 9.5f, 13.5f, 12.5f, 13.5f);
            }
        }

        /*--------------------------------------------------------------------*/
        /* TIC_DrawCross:                                                     */
        /*                                                                    */
        /* A bold red diagonal cross: delete.                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx : the surface, in grid units.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the cross is drawn.                                     */
        /*--------------------------------------------------------------------*/
        private static void TIC_DrawCross(Graphics gfx)
        {
            using (Pen penCross = new Pen(TIC_clrRemove, 2.6f))
            {   /* Draw the cross: two thick red strokes corner to corner, with   */
                /* rounded ends set on the pen first, so the strokes do not       */
                /* look clipped.  The pen is released when the block ends.        */
                penCross.StartCap = LineCap.Round;
                penCross.EndCap   = LineCap.Round;
                gfx.DrawLine(penCross, 3.5f, 3.5f, 12.5f, 12.5f);
                gfx.DrawLine(penCross, 12.5f, 3.5f, 3.5f, 12.5f);
            }
        }

        /*--------------------------------------------------------------------*/
        /* TIC_DrawProperties:                                                */
        /*                                                                    */
        /* A page with a blue disc at its bottom right holding a white "i":   */
        /* information about an entry.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx : the surface, in grid units.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the page and disc are drawn.                            */
        /*--------------------------------------------------------------------*/
        private static void TIC_DrawProperties(Graphics gfx)
        {
            /* The lists' page at full size, as the ground of the picture. */
            EIC_EntryIcons.EIC_DrawPage(gfx);

            using (SolidBrush brDisc = new SolidBrush(TIC_clrGo))
            using (SolidBrush brDot = new SolidBrush(TIC_clrPaper))
            using (Pen penStem = new Pen(TIC_clrPaper, 1.3f))
            using (Pen penHalo = new Pen(TIC_clrPaper, 0.8f))
            {   /* Draw the information mark over the page's lower right: a blue   */
                /* disc edged in white so it stands off the page's lines, then     */
                /* the white "i" on it -- its dot, and its stem below.  The        */
                /* brushes and pens are released when the block ends.              */
                gfx.FillEllipse(brDisc, 8f, 8f, 7.5f, 7.5f);
                gfx.DrawEllipse(penHalo, 8f, 8f, 7.5f, 7.5f);
                gfx.FillEllipse(brDot, 11.1f, 9.3f, 1.3f, 1.3f);
                gfx.DrawLine(penStem, 11.75f, 11.3f, 11.75f, 14.2f);
            }
        }

        /*--------------------------------------------------------------------*/
        /* TIC_DrawRefresh:                                                   */
        /*                                                                    */
        /* Three quarters of a green circle ending in an arrow head: read the */
        /* folder again.                                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx : the surface, in grid units.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the turning arrow is drawn.                             */
        /*--------------------------------------------------------------------*/
        private static void TIC_DrawRefresh(Graphics gfx)
        {
            PointF[] aptHead;

            /* The arc runs clockwise from the right-hand side round to the   */
            /* top, where the head points right.                              */
            aptHead = new PointF[]
            {
                new PointF(8f, 0.5f),     // the head's upper corner
                new PointF(12.5f, 3f),    // its point
                new PointF(8f, 5.5f)      // its lower corner
            };

            using (Pen penArc = new Pen(TIC_clrAdd, 2f))
            using (SolidBrush brHead = new SolidBrush(TIC_clrAdd))
            {   /* Draw the turning arrow: a green arc three quarters of the way   */
                /* round a circle centred on the picture (angles are degrees       */
                /* clockwise from three o'clock -- GDI+'s convention, as found,    */
                /* not checkable in this tree -- so it starts at the right,        */
                /* passes the bottom and the left, and ends at the top), then      */
                /* the head at that end, its base centred on                       */
                /* the arc's top point (8, 3), pointing on in the arc's            */
                /* direction.  The pen and brush are released when the block       */
                /* ends.                                                           */
                gfx.DrawArc(penArc, 3f, 3f, 10f, 10f, 0f, 270f);
                gfx.FillPolygon(brHead, aptHead);
            }
        }

        /*--------------------------------------------------------------------*/
        /* TIC_DrawTiles:                                                     */
        /*                                                                    */
        /* Four small squares, two by two: choose how the list is shown.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx : the surface, in grid units.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the four tiles are drawn.                               */
        /*--------------------------------------------------------------------*/
        private static void TIC_DrawTiles(Graphics gfx)
        {
            float fLeft;
            float fTop;

            using (SolidBrush brTile = new SolidBrush(TIC_clrTile))
            {   /* Fill four tiles, two rows of two, 5 units square with a       */
                /* 2-unit gap, the steps landing on 2 and 9 exactly; the brush   */
                /* is released when the block ends.                              */
                for (fTop = 2f; fTop <= 9f; fTop = fTop + 7f)
                {   /* One row per iteration. */
                    for (fLeft = 2f; fLeft <= 9f; fLeft = fLeft + 7f)
                    {   /* One tile per iteration. */
                        gfx.FillRectangle(brTile, fLeft, fTop, 5f, 5f);
                    }
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* TIC_DrawDrive:                                                     */
        /*                                                                    */
        /* A disk drive seen from the front -- a grey box with an activity    */
        /* light -- standing for an image file or a device, with a mark above */
        /* it saying what is done with one: a small folder (open one), a      */
        /* green plus (make a new one) or a red cross (close the open one).   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx   : the surface, in grid units.                            */
        /*     iMark : a TIC_MARK_* value, the mark above the drive.          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the drive and its mark are drawn.                       */
        /*--------------------------------------------------------------------*/
        private static void TIC_DrawDrive(Graphics gfx, int iMark)
        {
            using (SolidBrush brBody = new SolidBrush(TIC_clrMetal))
            using (SolidBrush brLight = new SolidBrush(TIC_clrAdd))
            using (Pen penEdge = new Pen(TIC_clrInk, 0.8f))
            {   /* Draw the drive in the picture's lower half: the grey box and   */
                /* its outline, a dark slot line across its front, and a green    */
                /* activity light at the right, which make it read as a drive     */
                /* rather than a plain box.  The upper half is left for the       */
                /* mark below.  Released when the block ends.                     */
                gfx.FillRectangle(brBody, 1f, 8.5f, 14f, 6f);
                gfx.DrawRectangle(penEdge, 1f, 8.5f, 14f, 6f);
                gfx.DrawLine(penEdge, 3f, 11.5f, 9.5f, 11.5f);
                gfx.FillEllipse(brLight, 11.5f, 10.5f, 2f, 2f);
            }

            if (iMark == TIC_MARK_FOLDER)
            {   /* Open: a small folder above the drive -- the image comes out   */
                /* of a folder.                                                  */
                TIC_DrawSmall(gfx, true, 3.5f, 0f);
            }
            else if (iMark == TIC_MARK_PLUS)
            {   /* New: the plus above the drive, the same mark as the other   */
                /* "new" pictures.                                             */
                TIC_DrawPlus(gfx, 8f, 4.5f);
            }
            else
            {   /* Close: a red cross above the drive, in the delete cross's   */
                /* red, at the spot the plus takes, so the three drive         */
                /* pictures read as one family.                                */
                TIC_DrawSmallCross(gfx, 8f, 4.5f);
            }
        }

        /*--------------------------------------------------------------------*/
        /* TIC_DrawSmallCross:                                                */
        /*                                                                    */
        /* A small red diagonal cross centred on a grid position: the mark    */
        /* for "close this one", drawn as the plus is -- the same square, the */
        /* same pens and the same white halo -- so it sits on another shape   */
        /* as cleanly.                                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx      : the surface, in grid units.                         */
        /*     fCentreX : the cross's centre, x.                              */
        /*     fCentreY : the cross's centre, y.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the cross is drawn.                                     */
        /*--------------------------------------------------------------------*/
        private static void TIC_DrawSmallCross(Graphics gfx, float fCentreX, float fCentreY)
        {
            using (Pen penHalo = new Pen(TIC_clrPaper, 3.4f))
            using (Pen penCross = new Pen(TIC_clrRemove, 2f))
            {   /* Draw the cross: a wider white one first, then the red one on    */
                /* top, so a thin white halo shows round it and it reads clearly   */
                /* over what is beneath, as the plus does.  Each arm ends 2.6      */
                /* units across and 2.6 down from the centre (the halo 3), the     */
                /* plus's reach along its axes, so the cross fills the same        */
                /* square as the plus -- its diagonal arms the longer, about 3.7   */
                /* units.  Released when the block ends.                           */
                gfx.DrawLine(penHalo, fCentreX - 3f, fCentreY - 3f, fCentreX + 3f, fCentreY + 3f);
                gfx.DrawLine(penHalo, fCentreX + 3f, fCentreY - 3f, fCentreX - 3f, fCentreY + 3f);
                gfx.DrawLine(penCross, fCentreX - 2.6f, fCentreY - 2.6f, fCentreX + 2.6f, fCentreY + 2.6f);
                gfx.DrawLine(penCross, fCentreX + 2.6f, fCentreY - 2.6f, fCentreX - 2.6f, fCentreY + 2.6f);
            }
        }

        /*--------------------------------------------------------------------*/
        /* TIC_DrawPencil:                                                    */
        /*                                                                    */
        /* A pencil lying on the diagonal, point at the bottom left: writing  */
        /* to the image is (to be) enabled.                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx : the surface, in grid units.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the pencil is drawn.                                    */
        /*--------------------------------------------------------------------*/
        private static void TIC_DrawPencil(Graphics gfx)
        {
            PointF[] aptTip;

            /* The sharpened point, a triangle at the barrel's lower end. */
            aptTip = new PointF[]
            {
                new PointF(1.5f, 14.5f),  // the point itself
                new PointF(2.6f, 10.6f),  // where the barrel's upper edge begins
                new PointF(5.4f, 13.4f)   // where the barrel's lower edge begins
            };

            using (Pen penBarrel = new Pen(TIC_clrPencil, 4f))
            using (Pen penEraser = new Pen(TIC_clrRemove, 4f))
            using (SolidBrush brTip = new SolidBrush(TIC_clrInk))
            {   /* Draw the pencil on the diagonal: the yellow barrel as one      */
                /* thick stroke, the red eraser continuing it at the upper end,   */
                /* then the dark point filled at the lower end, over the          */
                /* barrel's square end.  The pens and brush are released when     */
                /* the block ends.                                                */
                gfx.DrawLine(penBarrel, 4f, 12f, 11.5f, 4.5f);
                gfx.DrawLine(penEraser, 11.5f, 4.5f, 13.2f, 2.8f);
                gfx.FillPolygon(brTip, aptTip);
            }
        }
    }
}
