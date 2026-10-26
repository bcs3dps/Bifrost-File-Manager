/*----------------------------------------------------------------------------*/
/* MakeIcon.cs                                                                */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* Development tool: DRAWS BifrostFM's icon and writes it out.  The artwork   */
/* is code, not a picture file, so it can be changed and regenerated from     */
/* the source tree alone.                                                     */
/*                                                                            */
/*     MakeIcon.exe <output-folder>                                           */
/*                                                                            */
/* writes, into the folder:                                                   */
/*                                                                            */
/*     BifrostFM.ico    the program icon at every size in ICO_aiSizes: those  */
/*                      below ICO_LARGE_FROM in the SMALL design, the rest in */
/*                      the LARGE design                                      */
/*     BifrostFM.png    the large design alone at 256 pixels, for the About   */
/*                      window                                                */
/*                                                                            */
/* Both names are the ones gui\engine.rc compiles into the exe (the icon as   */
/* ICON 1 and again as RCDATA 102, the picture as RCDATA 101), from           */
/* gui\art\, where build_icon.bat has this tool write them; rename one here   */
/* and engine.rc must follow.                                                 */
/*                                                                            */
/* THE TWO DESIGNS.  Small: a sword, point down, over a rainbow -- the        */
/* Bifrost bridge and the sword that guards it, kept to shapes that survive   */
/* at 16 pixels.  Large: a penguin dressed as Heimdall -- horned helmet and   */
/* cape -- holding the sword point down, the rainbow behind.                  */
/*                                                                            */
/* Every shape is placed in a 256-unit square and scaled to the size being    */
/* drawn.  Each size is drawn at four times its size and reduced, which       */
/* gives cleaner edges at small sizes than drawing small directly.            */
/*                                                                            */
/* Exit codes: 0 written; 2 usage; 3 any failure after that (writing the      */
/* folder or files, or drawing; the message says which).                      */
/*----------------------------------------------------------------------------*/

/* The framework parts used: the list of encoded images                     */
/* (Collections.Generic), GDI+ drawing, paths and pixel formats (Drawing,   */
/* Drawing2D, Imaging), and the files and in-memory streams (IO).           */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace BifrostFM.Tools
{
    internal static class ICO_Program
    {
        private const int   ICO_DESIGN      = 256;    // the side of the square every shape is placed in
        private const int   ICO_OVERSAMPLE  = 4;      // each size is drawn this many times larger, then reduced
        private const int   ICO_LARGE_FROM  = 32;     // sizes from this up use the large design (32: the taskbar's and Alt+Tab's size at 100 % scaling); below it, the sword alone
        private const int   ICO_PNG_FROM    = 256;    // sizes from this up are stored in the icon as PNG; smaller ones as bitmaps
        private const int   ICO_EXIT_OK     = 0;      // exit code: both files written
        private const int   ICO_EXIT_USAGE  = 2;      // exit code: no output folder given
        private const int   ICO_EXIT_WRITE  = 3;      // exit code: anything failed after the usage check -- writing, or the drawing or encoding before it; the message says which

        private static readonly int[] ICO_aiSizes = { 16, 20, 24, 32, 40, 48, 64, 256 };    // the sizes the icon holds

        /* The rainbow's bands, outermost first. */
        private static readonly Color[] ICO_aclrRainbow =
        {
            Color.FromArgb(230, 40, 40),      // red
            Color.FromArgb(245, 140, 30),     // orange
            Color.FromArgb(250, 220, 40),     // yellow
            Color.FromArgb(60, 180, 75),      // green
            Color.FromArgb(40, 130, 230),     // blue
            Color.FromArgb(120, 70, 200)      // violet
        };

        /*--------------------------------------------------------------------*/
        /* ICO_DrawBackground:                                                */
        /*                                                                    */
        /* The night sky both designs sit on: a rounded square, deep blue at  */
        /* the top fading to indigo.                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx : the surface, already scaled to the 256-unit square.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the background is drawn.                                */
        /*--------------------------------------------------------------------*/
        private static void ICO_DrawBackground(Graphics gfx)
        {
            const float fRadius = 44f;    // corner radius of the rounded square, design units

            using (GraphicsPath pthSquare = new GraphicsPath())
            using (LinearGradientBrush brSky = new LinearGradientBrush(new Point(0, 0), new Point(0, ICO_DESIGN),
                                                                       Color.FromArgb(18, 28, 78), Color.FromArgb(58, 34, 110)))
            {   /* The outline and the sky's brush, released once the square   */
                /* is filled and the clip taken from it.  The brush runs top   */
                /* to bottom over the whole 256 units, deep blue to indigo.    */
                /* The rounded square sits 4 units in from every edge, so      */
                /* the icon has a small transparent margin: one quarter-       */
                /* circle per corner, clockwise from the top left, the         */
                /* straight sides added between them by the path itself,       */
                /* then closed.                                                */
                pthSquare.AddArc(4, 4, fRadius * 2, fRadius * 2, 180, 90);
                pthSquare.AddArc(ICO_DESIGN - 4 - fRadius * 2, 4, fRadius * 2, fRadius * 2, 270, 90);
                pthSquare.AddArc(ICO_DESIGN - 4 - fRadius * 2, ICO_DESIGN - 4 - fRadius * 2, fRadius * 2, fRadius * 2, 0, 90);
                pthSquare.AddArc(4, ICO_DESIGN - 4 - fRadius * 2, fRadius * 2, fRadius * 2, 90, 90);
                pthSquare.CloseFigure();

                /* The sky, filling the square. */
                gfx.FillPath(brSky, pthSquare);

                /* Everything drawn after this stays inside the rounded        */
                /* square: both designs call this first, and their rainbows    */
                /* run past the square's lower corners, which this cuts off.   */
                /* The clip outlives the path released below (GDI+ keeps       */
                /* its own region -- framework behaviour, as found).           */
                gfx.SetClip(pthSquare);
            }
        }

        /*--------------------------------------------------------------------*/
        /* ICO_DrawRainbow:                                                   */
        /*                                                                    */
        /* The bridge: concentric arcs rising from the bottom edge.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx     : the surface, scaled to the 256-unit square.          */
        /*     fCentreY: the height of the arcs' common centre.               */
        /*     fOuter  : the outer radius of the outermost band.              */
        /*     fBand   : the thickness of each band.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the rainbow is drawn.                                   */
        /*--------------------------------------------------------------------*/
        private static void ICO_DrawRainbow(Graphics gfx, float fCentreY, float fOuter, float fBand)
        {
            float fRadius;                               // the radius of the band being drawn, at its middle
            int   i;

            /* The six bands, each one band's thickness inside the last. */
            for (i = 0; i < ICO_aclrRainbow.Length; i++)
            {   /* One band per iteration, outermost first.                  */
                /* The band's middle line: a pen is stroked centred on       */
                /* its path, so half a band in from the band's outer edge.   */
                fRadius = fOuter - (i * fBand) - (fBand / 2f);

                /* The pen is 0.6 units wider than the band, so each band   */
                /* overlaps its neighbours a little (the 0.6 itself is      */
                /* not explained anywhere).                                 */
                using (Pen penBand = new Pen(ICO_aclrRainbow[i], fBand + 0.6f))
                {   /* A half-circle, drawn as the top half of a full one:   */
                    /* GDI+ measures angles clockwise from the right, so     */
                    /* 180 to 360 degrees runs left, over the top, right.    */
                    gfx.DrawArc(penBand, (ICO_DESIGN / 2f) - fRadius, fCentreY - fRadius, fRadius * 2f, fRadius * 2f, 180f, 180f);
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* ICO_DrawSword:                                                     */
        /*                                                                    */
        /* The sword, point down: pommel, grip, crossguard, then a blade      */
        /* that tapers to its point, with a lighter line down its middle.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx    : the surface, scaled to the 256-unit square.           */
        /*     fX     : the sword's centre line.                              */
        /*     fTop   : the top of the pommel.                                */
        /*     fTip   : the blade's point.                                    */
        /*     fWidth : the blade's width at the guard.                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the sword is drawn.                                     */
        /*--------------------------------------------------------------------*/
        private static void ICO_DrawSword(Graphics gfx, float fX, float fTop, float fTip, float fWidth)
        {
            float    fGrip;                              // length of the grip
            float    fGuardY;                            // the crossguard's centre line, where the blade starts
            float    fGuardHalf;                         // half the crossguard's span
            PointF[] aptBlade;                           // the blade's outline

            /* Every proportion follows the blade's width, so one sword   */
            /* drawing serves both designs at any size: the grip is 1.9   */
            /* widths long; the crossguard sits below the pommel's 0.9    */
            /* widths and the grip; it spans 2.1 widths each side.  The   */
            /* figures themselves are not explained anywhere.             */
            fGrip      = fWidth * 1.9f;
            fGuardY    = fTop + fWidth * 0.9f + fGrip;
            fGuardHalf = fWidth * 2.1f;

            /* The blade: straight sides from the guard, then tapering to   */
            /* the point over its last 1.6 widths (about a third of the     */
            /* blade in the small design, a quarter in the large).          */
            aptBlade = new PointF[]
            {
                new PointF(fX - fWidth / 2f, fGuardY),
                new PointF(fX + fWidth / 2f, fGuardY),
                new PointF(fX + fWidth / 2f, fTip - fWidth * 1.6f),
                new PointF(fX, fTip),
                new PointF(fX - fWidth / 2f, fTip - fWidth * 1.6f)
            };

            using (SolidBrush brSteel = new SolidBrush(Color.FromArgb(225, 230, 240)))
            using (Pen penEdge = new Pen(Color.FromArgb(70, 80, 105), fWidth * 0.12f))
            using (Pen penFuller = new Pen(Color.FromArgb(160, 170, 190), fWidth * 0.16f))
            {   /* Steel, its dark outline, and the lighter groove (the     */
                /* fuller) down the middle from just below the guard to     */
                /* short of the taper.  The blade goes first so the hilt,   */
                /* drawn next, covers its top edge.                         */
                gfx.FillPolygon(brSteel, aptBlade);
                gfx.DrawPolygon(penEdge, aptBlade);
                gfx.DrawLine(penFuller, fX, fGuardY + fWidth * 0.5f, fX, fTip - fWidth * 2.2f);
            }

            using (SolidBrush brGold = new SolidBrush(Color.FromArgb(240, 190, 60)))
            using (SolidBrush brGrip = new SolidBrush(Color.FromArgb(110, 60, 30)))
            using (Pen penGoldEdge = new Pen(Color.FromArgb(140, 95, 20), fWidth * 0.12f))
            {   /* The hilt: grip, crossguard over it, pommel on top, each   */
                /* later part covering a joint of the one before.            */
                /* The brown grip, 0.6 widths wide, running from under the   */
                /* pommel to just under the guard, so neither end shows.     */
                gfx.FillRectangle(brGrip, fX - fWidth * 0.3f, fTop + fWidth * 0.8f, fWidth * 0.6f, fGrip + fWidth * 0.2f);

                /* The gold crossguard, 0.7 widths tall, centred on the   */
                /* line where the blade begins, with a darker edge.       */
                gfx.FillRectangle(brGold, fX - fGuardHalf, fGuardY - fWidth * 0.35f, fGuardHalf * 2f, fWidth * 0.7f);
                gfx.DrawRectangle(penGoldEdge, fX - fGuardHalf, fGuardY - fWidth * 0.35f, fGuardHalf * 2f, fWidth * 0.7f);

                /* The round gold pommel at the top, 1.1 widths across. */
                gfx.FillEllipse(brGold, fX - fWidth * 0.55f, fTop, fWidth * 1.1f, fWidth * 1.1f);
                gfx.DrawEllipse(penGoldEdge, fX - fWidth * 0.55f, fTop, fWidth * 1.1f, fWidth * 1.1f);
            }
        }

        /*--------------------------------------------------------------------*/
        /* ICO_DrawSmall:                                                     */
        /*                                                                    */
        /* The small design: the rainbow as a broad arch, the sword standing  */
        /* in front of it.  Bands and blade are kept thick so they are still  */
        /* there at 16 pixels.                                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx : the surface, scaled to the 256-unit square.              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the design is drawn.                                    */
        /*--------------------------------------------------------------------*/
        private static void ICO_DrawSmall(Graphics gfx)
        {
            /* Back to front: the sky (which also sets the clip), the       */
            /* arch centred low at 236 with an outer radius of 122 --       */
            /* nearly the icon's full width -- in 15-unit bands, then the   */
            /* sword down the middle from 14 to 238, 30 units wide.         */
            ICO_DrawBackground(gfx);
            ICO_DrawRainbow(gfx, 236f, 122f, 15f);
            ICO_DrawSword(gfx, 128f, 14f, 238f, 30f);
        }

        /*--------------------------------------------------------------------*/
        /* ICO_DrawLarge:                                                     */
        /*                                                                    */
        /* The large design: Heimdall as a penguin.  Drawn back to front --   */
        /* rainbow, cape, body and belly, feet, head and face, helmet and     */
        /* horns, then the sword held point down in front, with the flippers  */
        /* laid over its guard.                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     gfx : the surface, scaled to the 256-unit square.              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the design is drawn.                                    */
        /*--------------------------------------------------------------------*/
        private static void ICO_DrawLarge(Graphics gfx)
        {
            PointF[] aptCape;                            // the cape's outline
            PointF[] aptBeak;                            // the beak's upper half
            PointF[] aptFootLeft;                        // the left foot's outline
            PointF[] aptFootRight;                       // the right foot's outline (the left mirrored about 128)
            PointF[] aptHornLeft;                        // the left horn's curve points
            PointF[] aptHornRight;                       // the right horn's (mirrored)

            /* The sky (which also sets the clip), then the rainbow behind   */
            /* the penguin: lower and thinner than the small design's --     */
            /* centre 250, 11-unit bands.                                    */
            ICO_DrawBackground(gfx);
            ICO_DrawRainbow(gfx, 250f, 126f, 11f);

            /* The cape: a red trapezium behind the body, wider at the hem. */
            aptCape = new PointF[] { new PointF(92, 96), new PointF(164, 96), new PointF(206, 236), new PointF(50, 236) };

            using (SolidBrush brCape = new SolidBrush(Color.FromArgb(170, 30, 45)))
            using (Pen penCape = new Pen(Color.FromArgb(240, 190, 60), 4f))
            {   /* Red cloth with a gold edge. */
                gfx.FillPolygon(brCape, aptCape);
                gfx.DrawPolygon(penCape, aptCape);
            }

            using (SolidBrush brBlack = new SolidBrush(Color.FromArgb(24, 26, 34)))
            using (SolidBrush brWhite = new SolidBrush(Color.FromArgb(246, 246, 250)))
            using (SolidBrush brOrange = new SolidBrush(Color.FromArgb(245, 150, 30)))
            using (SolidBrush brGold = new SolidBrush(Color.FromArgb(240, 190, 60)))
            using (SolidBrush brHorn = new SolidBrush(Color.FromArgb(236, 226, 200)))
            using (Pen penGoldEdge = new Pen(Color.FromArgb(140, 95, 20), 2.5f))
            {   /* The penguin's own colours and the helmet's, shared by the   */
                /* parts below and released together at the end.               */
                /* brOrange is made and released, but nothing draws            */
                /* with it.  THE DEFECT EXISTS AND IS INTENTIONALLY            */
                /* DEFERRED: dead code in a development tool, no effect on     */
                /* the pictures it makes.                                      */

                /* The black body, then the white belly over its front,    */
                /* narrower and starting lower, so black shows round it.   */
                gfx.FillEllipse(brBlack, 74, 92, 108, 142);
                gfx.FillEllipse(brWhite, 92, 116, 72, 112);

                /* Feet: webbed, splayed outward at the hem, three toes each.    */
                /* Each is a fan from a narrow heel under the body to a          */
                /* scalloped front edge; the notches between the toes are what   */
                /* make it read as a web rather than a slipper.                  */
                aptFootLeft = new PointF[]
                {
                    new PointF(112, 218), new PointF(96, 220), new PointF(66, 236), new PointF(70, 246),
                    new PointF(82, 240), new PointF(90, 250), new PointF(100, 241), new PointF(110, 249),
                    new PointF(120, 236)
                };
                aptFootRight = new PointF[]
                {
                    new PointF(144, 218), new PointF(160, 220), new PointF(190, 236), new PointF(186, 246),
                    new PointF(174, 240), new PointF(166, 250), new PointF(156, 241), new PointF(146, 249),
                    new PointF(136, 236)
                };

                using (SolidBrush brFoot = new SolidBrush(Color.FromArgb(246, 180, 30)))
                using (Pen penFoot = new Pen(Color.FromArgb(170, 105, 10), 2.5f))
                {   /* Yellow-orange, with a darker edge so the toes show      */
                    /* against the cape.  The edge's corners are rounded, so   */
                    /* the sharp toe tips and notches are not drawn as long    */
                    /* pointed joins; each foot is filled, then outlined.      */
                    penFoot.LineJoin = LineJoin.Round;
                    gfx.FillPolygon(brFoot, aptFootLeft);
                    gfx.DrawPolygon(penFoot, aptFootLeft);
                    gfx.FillPolygon(brFoot, aptFootRight);
                    gfx.DrawPolygon(penFoot, aptFootRight);
                }

                /* The head: all black, as a penguin's is -- no white face       */
                /* patch.  The face is carried by the eyes and the beak alone.   */
                gfx.FillEllipse(brBlack, 86, 44, 84, 80);

                /* The eyes: two tall white ovals set close together, each   */
                /* with a black pupil toward the middle and a small glint.   */
                gfx.FillEllipse(brWhite, 103, 76, 21, 27);
                gfx.FillEllipse(brWhite, 132, 76, 21, 27);
                gfx.FillEllipse(brBlack, 112, 86, 10, 13);
                gfx.FillEllipse(brBlack, 134, 86, 10, 13);
                gfx.FillEllipse(brWhite, 116, 88, 4, 4);
                gfx.FillEllipse(brWhite, 138, 88, 4, 4);

                /* The beak: broad and flat, a darker lower half showing      */
                /* beneath a lighter upper half that comes to a soft point.   */
                aptBeak = new PointF[]
                {
                    new PointF(106, 102), new PointF(118, 96), new PointF(128, 94), new PointF(138, 96),
                    new PointF(150, 102), new PointF(138, 108), new PointF(128, 110), new PointF(118, 108)
                };

                using (SolidBrush brBeakLow = new SolidBrush(Color.FromArgb(214, 130, 14)))
                using (SolidBrush brBeakTop = new SolidBrush(Color.FromArgb(250, 196, 40)))
                {   /* Lower half first, so the upper overlaps it. */
                    gfx.FillEllipse(brBeakLow, 112, 103, 32, 13);
                    gfx.FillClosedCurve(brBeakTop, aptBeak);
                }

                /* The helmet, over the head drawn above: a gold cap (the    */
                /* top half of an ellipse, outlined along its curve only),   */
                /* an outlined band across its rim, and a plain nose guard   */
                /* down between the eyes.                                    */
                gfx.FillPie(brGold, 84, 34, 88, 76, 180, 180);
                gfx.DrawArc(penGoldEdge, 84, 34, 88, 76, 180, 180);
                gfx.FillRectangle(brGold, 84, 68, 88, 9);
                gfx.DrawRectangle(penGoldEdge, 84, 68, 88, 9);
                gfx.FillRectangle(brGold, 124, 72, 8, 16);

                /* The horns, curving up and out from the sides of the cap:   */
                /* each a closed smooth curve through five points, the        */
                /* right the left mirrored, filled without an outline.        */
                aptHornLeft  = new PointF[] { new PointF(88, 62), new PointF(70, 50), new PointF(60, 22), new PointF(78, 40), new PointF(98, 48) };
                aptHornRight = new PointF[] { new PointF(168, 62), new PointF(186, 50), new PointF(196, 22), new PointF(178, 40), new PointF(158, 48) };
                gfx.FillClosedCurve(brHorn, aptHornLeft);
                gfx.FillClosedCurve(brHorn, aptHornRight);

                /* The sword, in front of the belly, its point between the feet. */
                ICO_DrawSword(gfx, 128f, 112f, 236f, 14f);

                /* The flippers, brought round to rest on the guard: drawn   */
                /* after the sword, so they cover the guard's two ends as    */
                /* if holding it.                                            */
                gfx.FillEllipse(brBlack, 70, 126, 50, 26);
                gfx.FillEllipse(brBlack, 136, 126, 50, 26);
            }
        }

        /*--------------------------------------------------------------------*/
        /* ICO_Render:                                                        */
        /*                                                                    */
        /* Draw one size of the icon: the design for that size, drawn large   */
        /* and reduced.                                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iSize : the side of the finished image, pixels.                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Bitmap : the image, 32 bits with transparency; the caller      */
        /*              disposes it.                                          */
        /*--------------------------------------------------------------------*/
        private static Bitmap ICO_Render(int iSize)
        {
            Bitmap bmpBig;                               // the drawing at ICO_OVERSAMPLE times the size
            Bitmap bmpOut;                               // the finished image
            int    iBig;                                 // the side of bmpBig, pixels

            /* The working picture, four times the finished size, with an   */
            /* alpha channel so the margin round the square stays clear.    */
            iBig   = iSize * ICO_OVERSAMPLE;
            bmpBig = new Bitmap(iBig, iBig, PixelFormat.Format32bppArgb);

            using (Graphics gfx = Graphics.FromImage(bmpBig))
            {   /* Draw in design units: one unit is iBig / 256 pixels.   */
                /* Smoothed edges, a transparent start, then the design   */
                /* this size uses.                                        */
                gfx.SmoothingMode = SmoothingMode.AntiAlias;
                gfx.Clear(Color.Transparent);
                gfx.ScaleTransform(iBig / (float)ICO_DESIGN, iBig / (float)ICO_DESIGN);

                if (iSize >= ICO_LARGE_FROM)
                {   /* ICO_LARGE_FROM and up: room for the penguin. */
                    ICO_DrawLarge(gfx);
                }
                else
                {   /* Below ICO_LARGE_FROM: sword and rainbow only, the   */
                    /* shapes that survive at 16 pixels.                   */
                    ICO_DrawSmall(gfx);
                }
            }

            /* The finished image at its real size, also with alpha. */
            bmpOut = new Bitmap(iSize, iSize, PixelFormat.Format32bppArgb);

            using (Graphics gfx = Graphics.FromImage(bmpOut))
            {   /* Reduce the big drawing into it with the highest-quality   */
                /* interpolation GDI+ offers, from a transparent start.      */
                gfx.InterpolationMode = InterpolationMode.HighQualityBicubic;
                gfx.PixelOffsetMode   = PixelOffsetMode.HighQuality;
                gfx.Clear(Color.Transparent);
                gfx.DrawImage(bmpBig, new Rectangle(0, 0, iSize, iSize), 0, 0, iBig, iBig, GraphicsUnit.Pixel);
            }

            /* The big drawing is no longer needed. */
            bmpBig.Dispose();

            /* The finished image at its real size; the caller disposes it. */
            return(bmpOut);
        }

        /*--------------------------------------------------------------------*/
        /* ICO_EncodeBitmapEntry:                                             */
        /*                                                                    */
        /* One image in the form an icon file stores a small size in: a       */
        /* BITMAPINFOHEADER whose height is DOUBLED (the format's way of      */
        /* saying an AND mask follows), the 32-bit pixels bottom row first,   */
        /* then the one-bit mask, each mask row padded to four bytes.  The    */
        /* mask is all zeros: the pixels' own alpha carries the transparency. */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bmpImage : the image.                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte[] : the entry's bytes.                                    */
        /*--------------------------------------------------------------------*/
        private static byte[] ICO_EncodeBitmapEntry(Bitmap bmpImage)
        {
            int   iSize;                                 // the image's side, pixels (the images are square)
            int   cbMaskRow;                             // bytes in one row of the AND mask
            Color clrPixel;                              // the pixel being written
            int   x;
            int   y;

            /* The side, and the mask's row length: one bit per pixel,     */
            /* rounded up to whole 32-bit words, as the format requires.   */
            iSize     = bmpImage.Width;
            cbMaskRow = ((iSize + 31) / 32) * 4;

            using (MemoryStream stmEntry = new MemoryStream())
            using (BinaryWriter wrtEntry = new BinaryWriter(stmEntry))
            {   /* Built in memory and handed back whole; BinaryWriter     */
                /* writes little-endian, as the format wants.  First the   */
                /* 40-byte BITMAPINFOHEADER, field by field.               */
                /* biSize: the header's own length.                        */
                wrtEntry.Write(40);
                /* biWidth. */
                wrtEntry.Write(iSize);
                /* biHeight: doubled, for the image plus its mask. */
                wrtEntry.Write(iSize * 2);
                /* biPlanes: always 1. */
                wrtEntry.Write((short)1);
                /* biBitCount: 32, blue-green-red-alpha. */
                wrtEntry.Write((short)32);
                /* biCompression: none. */
                wrtEntry.Write(0);
                /* biSizeImage: the pixels' bytes (the mask not counted). */
                wrtEntry.Write(iSize * iSize * 4);
                /* biXPelsPerMeter, biYPelsPerMeter: not used. */
                wrtEntry.Write(0);
                wrtEntry.Write(0);
                /* biClrUsed, biClrImportant: no palette. */
                wrtEntry.Write(0);
                wrtEntry.Write(0);

                /* The pixels, bottom row first as a bitmap stores them,     */
                /* read one at a time (the largest bitmap entry is only 64   */
                /* pixels square).                                           */
                for (y = iSize - 1; y >= 0; y--)
                {   /* One row per iteration, bottom row first. */
                    for (x = 0; x < iSize; x++)
                    {   /* One pixel per iteration: blue, green, red, alpha. */
                        clrPixel = bmpImage.GetPixel(x, y);
                        wrtEntry.Write(clrPixel.B);
                        wrtEntry.Write(clrPixel.G);
                        wrtEntry.Write(clrPixel.R);
                        wrtEntry.Write(clrPixel.A);
                    }
                }

                /* The AND mask: all zero bits, nothing masked -- the    */
                /* alpha channel does the work.  Flushed so the stream   */
                /* holds every byte before it is copied out.             */
                wrtEntry.Write(new byte[cbMaskRow * iSize]);
                wrtEntry.Flush();

                /* The entry, complete; both writers are released as the   */
                /* block is left.                                          */
                return(stmEntry.ToArray());
            }
        }

        /*--------------------------------------------------------------------*/
        /* Main:                                                              */
        /*                                                                    */
        /* Render every size, then write the icon file and the 256-pixel      */
        /* picture on its own as a PNG.  Exits 3 on any failure after the     */
        /* usage check; files already written are then left as they are.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     args : the output folder.                                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : an ICO_EXIT_* code.                                      */
        /*--------------------------------------------------------------------*/
        private static int Main(string[] args)
        {
            List<byte[]> lstEntries;                     // each size's encoded image, in ICO_aiSizes order
            Bitmap       bmpImage;                       // the size being encoded
            int          iOffset;                        // where the next image's data starts in the icon file
            int          i;

            if (args.Length != 1)
            {   /* The output folder is the one argument. */
                Console.Error.WriteLine("usage: MakeIcon.exe <output-folder>");
                return(ICO_EXIT_USAGE);
            }

            /* Every image is encoded before the icon file is opened, since   */
            /* its directory needs each one's length.                         */
            lstEntries = new List<byte[]>();

            try
            {   /* Everything that can fail -- the folder, the drawing, the   */
                /* encoding, the writes -- is inside this one try, and any    */
                /* failure is reported the same way (exit 3).  First the      */
                /* output folder, made if it is missing.                      */
                Directory.CreateDirectory(args[0]);

                for (i = 0; i < ICO_aiSizes.Length; i++)
                {   /* One size per iteration: drawn, encoded, released. */
                    bmpImage = ICO_Render(ICO_aiSizes[i]);

                    if (ICO_aiSizes[i] >= ICO_PNG_FROM)
                    {   /* The big one: stored as PNG (an icon file allows PNG   */
                        /* entries; why PNG from 256 up is not recorded), and    */
                        /* saved on its own for the About window.                */
                        using (MemoryStream stmPng = new MemoryStream())
                        {   /* Encoded once, used twice: the same bytes go into   */
                            /* the icon and into BifrostFM.png.                   */
                            bmpImage.Save(stmPng, ImageFormat.Png);
                            lstEntries.Add(stmPng.ToArray());
                            File.WriteAllBytes(Path.Combine(args[0], "BifrostFM.png"), stmPng.ToArray());
                        }
                    }
                    else
                    {   /* A small one: stored as a bitmap with its mask. */
                        lstEntries.Add(ICO_EncodeBitmapEntry(bmpImage));
                    }

                    /* Encoded: the drawing is no longer needed. */
                    bmpImage.Dispose();
                }

                using (FileStream stmIcon = new FileStream(Path.Combine(args[0], "BifrostFM.ico"), FileMode.Create, FileAccess.Write))
                using (BinaryWriter wrtIcon = new BinaryWriter(stmIcon))
                {   /* The icon file, created or emptied: header, directory,   */
                    /* images.  The six-byte header, field by field:           */
                    /* reserved, always 0.                                     */
                    wrtIcon.Write((short)0);
                    /* type 1: an icon (2 would be a cursor). */
                    wrtIcon.Write((short)1);
                    /* the number of images. */
                    wrtIcon.Write((short)ICO_aiSizes.Length);

                    /* The first image's data starts after the header and   */
                    /* one 16-byte directory record per image.              */
                    iOffset = 6 + (16 * ICO_aiSizes.Length);

                    for (i = 0; i < ICO_aiSizes.Length; i++)
                    {   /* One directory record per iteration, in the same   */
                        /* order as the images follow.                       */
                        /* Width and height, one byte each: 256 does not     */
                        /* fit, and the format writes it as 0 -- which the   */
                        /* & 0xFF does.                                      */
                        wrtIcon.Write((byte)(ICO_aiSizes[i] & 0xFF));
                        wrtIcon.Write((byte)(ICO_aiSizes[i] & 0xFF));
                        /* no palette. */
                        wrtIcon.Write((byte)0);
                        /* reserved. */
                        wrtIcon.Write((byte)0);
                        /* colour planes. */
                        wrtIcon.Write((short)1);
                        /* bits per pixel. */
                        wrtIcon.Write((short)32);
                        /* the size of the image's data. */
                        wrtIcon.Write(lstEntries[i].Length);
                        /* where it starts in the file. */
                        wrtIcon.Write(iOffset);
                        /* The next image follows this one. */
                        iOffset = iOffset + lstEntries[i].Length;
                    }

                    /* The images' data, back to back in directory order. */
                    for (i = 0; i < lstEntries.Count; i++)
                    {   /* One image's data per iteration. */
                        wrtIcon.Write(lstEntries[i]);
                    }
                }
            }
            catch (Exception exWrite)
            {   /* Anything failed: the folder, a file, the drawing or the   */
                /* encoding.  Its message goes to the error stream.  What    */
                /* was written first is left: a new BifrostFM.png, and a     */
                /* BifrostFM.ico emptied or part-written if the failure      */
                /* came while it was open.  Exit 3.                          */
                Console.Error.WriteLine("MakeIcon: " + exWrite.Message);
                return(ICO_EXIT_WRITE);
            }

            /* Done: one line saying where the two files went (the only   */
            /* output on success; build_icon.bat shows it).               */
            Console.WriteLine("MakeIcon: wrote BifrostFM.ico and BifrostFM.png to " + args[0]);

            /* Both files are written. */
            return(ICO_EXIT_OK);
        }
    }
}
