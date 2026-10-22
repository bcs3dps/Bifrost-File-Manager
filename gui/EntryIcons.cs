/*----------------------------------------------------------------------------*/
/* EntryIcons.cs                                                              */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The icons the IMAGE pane shows beside its entries.  An entry inside an     */
/* ext2/3/4, FAT or exFAT image is not a Windows file, so the shell has no    */
/* icon for it; the program supplies its own, one per KIND of entry: a        */
/* folder, a file, a symbolic link, a device node, and a pipe (which also     */
/* stands for a socket).                                                      */
/*                                                                            */
/* The icons are DRAWN here, not loaded: each is a few shapes described on a  */
/* 16-unit grid and scaled to whatever pixel size is asked for, so the same   */
/* code makes the small icons of the details view and the large ones of the   */
/* icon and tile views, at any display scaling, with no picture files.        */
/*                                                                            */
/* This file depends on nothing else in the program.  Its folder and page     */
/* drawings are also used by the toolbar's pictures (ToolIcons.cs), so that a */
/* folder looks the same wherever the program draws one.  Its users index the */
/* lists by the EIC_ICON_* constants -- MainForm.cs (the image pane, and the  */
/* Windows pane's ".." picture) and ImageSaveForm.cs (its folder tree and     */
/* list) -- so the constants' values, which are the pictures' positions in    */
/* every list, are the one thing they rely on.                                */
/*----------------------------------------------------------------------------*/

/* The framework: the base types (the list's handle), GDI+'s pictures,       */
/* colours and shapes, its smoothing setting, and the image list the panes   */
/* take.  The pixel format is named in full where used.                      */
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BifrostFM
{
    internal static class EIC_EntryIcons
    {
        /* The icons' positions in every list EIC_NewList makes -- documentation table. */
        internal const int EIC_ICON_FOLDER  = 0;    // a directory (the ".." row has its own, EIC_ICON_UP)
        internal const int EIC_ICON_FILE    = 1;    // a regular file, and anything of unknown type
        internal const int EIC_ICON_SYMLINK = 2;    // a symbolic link: the file page with an arrow badge
        internal const int EIC_ICON_DEVICE  = 3;    // a character or block device node: a chip
        internal const int EIC_ICON_PIPE    = 4;    // a fifo or a socket: a length of pipe
        internal const int EIC_ICON_UP      = 5;    // the ".." row that goes up a level: a folder with an arrow pointing up
        private  const int EIC_ICON_COUNT   = 6;    // how many icons a list holds; a new icon needs a constant above, a case in EIC_DrawIcon and this count raised

        private const float EIC_GRID        = 16f;  // the grid every shape below is described on, units per icon side

        /* Colours -- documentation table.  Fixed values, not the system's    */
        /* colours, so the icons look the same under any Windows theme; the   */
        /* values themselves are design choices with no rule behind them.     */
        private static readonly Color EIC_clrFolderBack  = Color.FromArgb(214, 160, 40);    // the folder's back and tab: darker gold
        private static readonly Color EIC_clrFolderFront = Color.FromArgb(246, 205, 90);    // the folder's front: lighter gold
        private static readonly Color EIC_clrFolderEdge  = Color.FromArgb(150, 105, 20);    // the folder's outline
        private static readonly Color EIC_clrPage        = Color.FromArgb(255, 255, 255);   // the file page
        private static readonly Color EIC_clrPageFold    = Color.FromArgb(215, 220, 228);   // the page's folded corner
        private static readonly Color EIC_clrPageEdge    = Color.FromArgb(110, 120, 135);   // the page's outline
        private static readonly Color EIC_clrPageLine    = Color.FromArgb(170, 180, 195);   // the lines of "text" on the page
        private static readonly Color EIC_clrLink        = Color.FromArgb(30, 100, 200);    // the symbolic link's arrow and badge outline
        private static readonly Color EIC_clrChip        = Color.FromArgb(60, 66, 76);      // the device chip's body
        private static readonly Color EIC_clrChipMark    = Color.FromArgb(150, 158, 170);   // the chip's pins and its inner square
        private static readonly Color EIC_clrPipe        = Color.FromArgb(95, 140, 180);    // the pipe's tube
        private static readonly Color EIC_clrPipeFlange  = Color.FromArgb(55, 90, 125);     // the pipe's two end flanges

        /*--------------------------------------------------------------------*/
        /* EIC_NewPicture:                                                    */
        /*                                                                    */
        /* Draw ONE of the icons, at any size, as a picture of its own.  The  */
        /* lists EIC_NewList makes are built from these; the Windows pane     */
        /* takes the way-up picture alone, for its ".." row, at whatever      */
        /* size its own icons are drawn (it shows the shell's icons for       */
        /* everything else, and the shell has no picture of "up a level").    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iIcon   : an EIC_ICON_* index.                                 */
        /*     sizIcon : the picture's size, pixels.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Bitmap : a new 32-bit picture with a transparent ground; the   */
        /*              caller owns it.                                       */
        /*--------------------------------------------------------------------*/
        internal static Bitmap EIC_NewPicture(int iIcon, Size sizIcon)
        {
            Bitmap bmpIcon;

            /* 32 bits with alpha, so the anti-aliased edges blend into   */
            /* whatever row or button colour the picture is shown on.     */
            bmpIcon = new Bitmap(sizIcon.Width, sizIcon.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            using (Graphics gfx = Graphics.FromImage(bmpIcon))
            {   /* Draw the icon onto the new picture.  The ground is cleared   */
                /* to transparent so only the shapes show, and anti-aliasing    */
                /* keeps their edges smooth at the small sizes; the surface     */
                /* is released when the block ends.                             */
                gfx.Clear(Color.Transparent);
                gfx.SmoothingMode = SmoothingMode.AntiAlias;

                /* Scale the 16-unit grid to the icon's pixel size, so every      */
                /* shape is written once, in grid units, and drawn at any size.   */
                gfx.ScaleTransform((float)sizIcon.Width / EIC_GRID, (float)sizIcon.Height / EIC_GRID);

                /* The icon itself; the using then releases the surface. */
                EIC_DrawIcon(gfx, iIcon);
            }

            /* The finished picture; the caller owns it and disposes it once   */
            /* copied (EIC_NewList does so at once).                           */
            return(bmpIcon);
        }

        /*--------------------------------------------------------------------*/
        /* EIC_NewList:                                                       */
        /*                                                                    */
        /* Make an image list holding every entry icon at one size, in the    */
        /* order of the EIC_ICON_* constants.  The caller owns the list (and  */
        /* gives it to a ListView as its small or large image list).  Any     */
        /* size is drawn equally well, which is what lets the image pane's    */
        /* icons follow View > Text Size.                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sizIcon : the icons' size, pixels.                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ImageList : the list, EIC_ICON_COUNT 32-bit images long.       */
        /*--------------------------------------------------------------------*/
        internal static ImageList EIC_NewList(Size sizIcon)
        {
            ImageList imlIcons;
            Bitmap    bmpIcon;
            IntPtr    hList;
            int       iIcon;

            /* The list: 32-bit, so the pictures keep their alpha edges, and   */
            /* sized to the icons -- both set before the handle is made        */
            /* below, so the Windows list is created at this size and depth.   */
            imlIcons            = new ImageList();
            imlIcons.ColorDepth = ColorDepth.Depth32Bit;
            imlIcons.ImageSize  = sizIcon;

            /* Make the list create its Windows image list NOW.  Until it has   */
            /* one, an ImageList only keeps a reference to each bitmap added    */
            /* and copies them later -- by which time the bitmaps below would   */
            /* have been disposed.  With the handle in place each Add copies    */
            /* the picture at once.  (Reading Handle is what creates it.)       */
            /* This is the framework's behaviour as found when the code was     */
            /* written; the framework's source is not in this tree.             */
            hList = imlIcons.Handle;

            if (hList == IntPtr.Zero)
            {   /* Windows would not make the list: hand back an empty one,      */
                /* and the rows simply show no icon.  A guard: whether the       */
                /* framework can return a zero handle rather than throw is not   */
                /* established.                                                  */
                return(imlIcons);
            }

            for (iIcon = 0; iIcon < EIC_ICON_COUNT; iIcon++)
            {   /* One icon per iteration, in index order. */

                /* Added in index order, so each picture's position in the   */
                /* list IS its EIC_ICON_* constant.                          */
                bmpIcon = EIC_NewPicture(iIcon, sizIcon);

                /* The list has copied the bitmap, so ours is released at once. */
                imlIcons.Images.Add(bmpIcon);
                bmpIcon.Dispose();
            }

            /* The finished list, for the caller to attach to a list view or   */
            /* tree (MainForm.cs, ImageSaveForm.cs); the caller owns it.       */
            return(imlIcons);
        }

        /*--------------------------------------------------------------------*/
        /* EIC_DrawIcon:                                                      */
        /*                                                                    */
        /* Draw one icon, chosen by its index, on a surface already scaled to */
        /* the 16-unit grid.  An index with no case of its own (EIC_ICON_FILE */
        /* among them) draws the plain page, so a bad index still gives a     */
        /* file's picture rather than nothing.                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx   : the surface, in grid units.                            */
        /*     iIcon : an EIC_ICON_* index.                                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the icon is drawn.                                      */
        /*--------------------------------------------------------------------*/
        private static void EIC_DrawIcon(Graphics gfx, int iIcon)
        {
            switch (iIcon)
            {   /* One icon per case. */
                case EIC_ICON_FOLDER:
                    /* A folder, plain. */
                    EIC_DrawFolder(gfx);
                    break;

                case EIC_ICON_SYMLINK:
                    /* A link is a page that points somewhere else. */
                    EIC_DrawPage(gfx);
                    EIC_DrawLinkBadge(gfx);
                    break;

                case EIC_ICON_UP:
                    /* The way up: a folder, with an arrow pointing up over it. */
                    EIC_DrawFolder(gfx);
                    EIC_DrawUpArrow(gfx);
                    break;

                case EIC_ICON_DEVICE:
                    /* A device node, character or block: a chip. */
                    EIC_DrawChip(gfx);
                    break;

                case EIC_ICON_PIPE:
                    /* A fifo or a socket: a length of pipe. */
                    EIC_DrawPipe(gfx);
                    break;

                default:
                    /* A file, and anything not otherwise known. */
                    EIC_DrawPage(gfx);
                    break;
            }
        }

        /*--------------------------------------------------------------------*/
        /* EIC_DrawFolder:                                                    */
        /*                                                                    */
        /* A closed folder: a darker back with its tab at the top left, and a */
        /* lighter front panel over the lower part.                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx : the surface, in grid units.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the folder is drawn.                                    */
        /*--------------------------------------------------------------------*/
        internal static void EIC_DrawFolder(Graphics gfx)
        {
            PointF[] aptBack;

            /* The back and its tab, as one outline, so a single fill and a   */
            /* single edge line cover both.                                   */
            aptBack = new PointF[]
            {
                new PointF(1f, 3f),      // top left of the tab
                new PointF(6f, 3f),      // top right of the tab
                new PointF(7.5f, 4.5f),  // the tab slopes down to the back's top edge
                new PointF(15f, 4.5f),   // top right of the back
                new PointF(15f, 13.5f),  // bottom right
                new PointF(1f, 13.5f)    // bottom left
            };

            using (SolidBrush brBack = new SolidBrush(EIC_clrFolderBack))
            using (SolidBrush brFront = new SolidBrush(EIC_clrFolderFront))
            using (Pen penEdge = new Pen(EIC_clrFolderEdge, 0.7f))
            {   /* Paint the folder: the darker back with its tab, then the      */
                /* lighter front panel across its lower part, which gives the    */
                /* closed-folder look.  The edges are drawn last so neither      */
                /* fill paints over them; the brushes and the pen are released   */
                /* when the block ends.                                          */
                gfx.FillPolygon(brBack, aptBack);
                gfx.FillRectangle(brFront, 1f, 6.5f, 14f, 7f);
                gfx.DrawPolygon(penEdge, aptBack);
                gfx.DrawLine(penEdge, 1f, 6.5f, 15f, 6.5f);
            }
        }

        /*--------------------------------------------------------------------*/
        /* EIC_DrawUpArrow:                                                   */
        /*                                                                    */
        /* A blue arrow pointing up, edged in white, over the right-hand side */
        /* of an already drawn folder: together, the usual picture of "up one */
        /* level".  It is the mark of the ".." row.                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx : the surface, in grid units.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the arrow is drawn.                                     */
        /*--------------------------------------------------------------------*/
        private static void EIC_DrawUpArrow(Graphics gfx)
        {
            PointF[] aptArrow;

            /* The arrow's outline, clockwise from its point: a head five   */
            /* units deep over a shaft about three wide, reaching down      */
            /* past the folder's bottom edge.                               */
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

            using (SolidBrush brArrow = new SolidBrush(EIC_clrLink))
            using (Pen penHalo = new Pen(EIC_clrPage, 1f))
            {   /* Paint the arrow in the link blue, then edge it with a white   */
                /* line so it stands clear of the gold folder beneath it at      */
                /* every size; the brush and the pen are released when the       */
                /* block ends.                                                   */
                gfx.FillPolygon(brArrow, aptArrow);
                gfx.DrawPolygon(penHalo, aptArrow);
            }
        }

        /*--------------------------------------------------------------------*/
        /* EIC_DrawPage:                                                      */
        /*                                                                    */
        /* A sheet of paper with its top right corner folded over and three   */
        /* lines of "text": the usual picture of a file.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx : the surface, in grid units.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the page is drawn.                                      */
        /*--------------------------------------------------------------------*/
        internal static void EIC_DrawPage(Graphics gfx)
        {
            PointF[] aptPage;
            PointF[] aptFold;
            float    fLine;

            /* The sheet, with the corner cut off where it folds. */
            aptPage = new PointF[]
            {
                new PointF(3f, 1f),      // top left
                new PointF(10f, 1f),     // where the fold begins on the top edge
                new PointF(13f, 4f),     // where the fold ends on the right edge
                new PointF(13f, 15f),    // bottom right
                new PointF(3f, 15f)      // bottom left
            };

            /* The folded-over corner. */
            aptFold = new PointF[]
            {
                new PointF(10f, 1f),     // on the top edge
                new PointF(10f, 4f),     // the corner's inner point
                new PointF(13f, 4f)      // on the right edge
            };

            using (SolidBrush brPage = new SolidBrush(EIC_clrPage))
            using (SolidBrush brFold = new SolidBrush(EIC_clrPageFold))
            using (Pen penEdge = new Pen(EIC_clrPageEdge, 0.7f))
            using (Pen penLine = new Pen(EIC_clrPageLine, 0.8f))
            {   /* Paint the page: the white sheet and its folded corner in a     */
                /* light grey, then both outlines on top so neither fill covers   */
                /* them, then the lines of "text" that make it read as a          */
                /* document; the brushes and pens are released when the block     */
                /* ends.                                                          */
                gfx.FillPolygon(brPage, aptPage);
                gfx.FillPolygon(brFold, aptFold);
                gfx.DrawPolygon(penEdge, aptPage);
                gfx.DrawPolygon(penEdge, aptFold);

                /* Three lines, at 7, 9.5 and 12 units down (2.5 is exact   */
                /* in binary, so the float steps land on 12 exactly).       */
                for (fLine = 7f; fLine <= 12f; fLine = fLine + 2.5f)
                {   /* One line of text per iteration. */
                    gfx.DrawLine(penLine, 5f, fLine, 11f, fLine);
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* EIC_DrawLinkBadge:                                                 */
        /*                                                                    */
        /* The mark that makes a page a symbolic link: a small white square   */
        /* at the bottom left holding an arrow that points up and to the      */
        /* right, as a shortcut's does.  Drawn over an already drawn page.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx : the surface, in grid units.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the badge is drawn.                                     */
        /*--------------------------------------------------------------------*/
        private static void EIC_DrawLinkBadge(Graphics gfx)
        {
            using (SolidBrush brBadge = new SolidBrush(EIC_clrPage))
            using (Pen penEdge = new Pen(EIC_clrLink, 0.7f))
            using (Pen penArrow = new Pen(EIC_clrLink, 1.3f))
            {   /* Draw the badge: a white square over the page's lower left,    */
                /* so the arrow reads clearly whatever the page shows beneath,   */
                /* outlined in the link blue; inside it, an arrow pointing up    */
                /* and to the right -- a shaft and two short head strokes.       */
                /* The brush and pens are released when the block ends.          */
                gfx.FillRectangle(brBadge, 1f, 8.5f, 7f, 7f);
                gfx.DrawRectangle(penEdge, 1f, 8.5f, 7f, 7f);
                gfx.DrawLine(penArrow, 2.5f, 14f, 6.5f, 10f);
                gfx.DrawLine(penArrow, 6.5f, 10f, 3.8f, 10f);
                gfx.DrawLine(penArrow, 6.5f, 10f, 6.5f, 12.7f);
            }
        }

        /*--------------------------------------------------------------------*/
        /* EIC_DrawChip:                                                      */
        /*                                                                    */
        /* An integrated circuit seen from above -- a dark square with pins   */
        /* on all four sides -- standing for a device node.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx : the surface, in grid units.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the chip is drawn.                                      */
        /*--------------------------------------------------------------------*/
        private static void EIC_DrawChip(Graphics gfx)
        {
            float fPin;

            using (SolidBrush brBody = new SolidBrush(EIC_clrChip))
            using (Pen penMark = new Pen(EIC_clrChipMark, 1f))
            {   /* Draw the chip: the pins first and the body over them, so only   */
                /* the pins' outer ends show, then a small square on the body.     */
                /* The brush and pen are released when the block ends.             */
                for (fPin = 5.5f; fPin <= 10.5f; fPin = fPin + 2.5f)
                {   /* One pin position per iteration: a pin on each of the four sides. */

                    /* Each line runs right across the chip; the body drawn   */
                    /* next hides the middle, leaving a pin at both ends --   */
                    /* so two lines make four pins, three positions twelve.   */
                    gfx.DrawLine(penMark, fPin, 1.5f, fPin, 14.5f);
                    gfx.DrawLine(penMark, 1.5f, fPin, 14.5f, fPin);
                }

                /* The body over the pins' inner ends, then the small   */
                /* square outlined in the pins' colour on top of it.    */
                gfx.FillRectangle(brBody, 3.5f, 3.5f, 9f, 9f);
                gfx.DrawRectangle(penMark, 6f, 6f, 4f, 4f);
            }
        }

        /*--------------------------------------------------------------------*/
        /* EIC_DrawPipe:                                                      */
        /*                                                                    */
        /* A short length of pipe with a flange at each end, standing for a   */
        /* fifo (a named pipe) and for a socket.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx : the surface, in grid units.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the pipe is drawn.                                      */
        /*--------------------------------------------------------------------*/
        private static void EIC_DrawPipe(Graphics gfx)
        {
            using (SolidBrush brTube = new SolidBrush(EIC_clrPipe))
            using (SolidBrush brFlange = new SolidBrush(EIC_clrPipeFlange))
            {   /* Paint the pipe: the tube across the middle, then a darker,    */
                /* taller flange over each end -- drawn after the tube so they   */
                /* cover its ends and read as the joints of a pipe.  The         */
                /* brushes are released when the block ends.                     */
                gfx.FillRectangle(brTube, 2f, 6f, 12f, 4f);
                gfx.FillRectangle(brFlange, 1f, 4.5f, 2.5f, 7f);
                gfx.FillRectangle(brFlange, 12.5f, 4.5f, 2.5f, 7f);
            }
        }
    }
}
