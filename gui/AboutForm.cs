/*-----------------------------------------------------------------------------*/
/* AboutForm.cs                                                                */
/*                                                                             */
/* SPDX-License-Identifier: GPL-2.0-or-later                                   */
/* Copyright (c) 2026 B. C. Services                                           */
/*                                                                             */
/* The About window: what the program is, its version, what it does, its       */
/* licence, and the credits the bundled libraries' licences ask for.           */
/*                                                                             */
/* It is a white body band over a grey footer: the name and tagline across     */
/* the top, the program's picture beside a column of text below them, and      */
/* Close at the bottom right.  Nothing is given a position by hand: Abt_Layout */
/* sizes and places every control from its measured text and sizes the         */
/* window to hold it all, so the window fits whatever the screen and fonts.    */
/*                                                                             */
/* It is MODELESS and has no owner: it can stay open beside the main window    */
/* without blocking it.  The main form keeps a reference so a second click on  */
/* Help > About brings the open window forward instead of making another.      */
/*                                                                             */
/* ITS FONTS ARE DELIBERATELY ITS OWN.  Every dialog is handed the program's   */
/* one dialog font and never goes below the message box font's size; this      */
/* window is the one exception, by design.  It is given no font and sets its   */
/* own fixed sizes in WinForms' default face (ABT_POINTS_*), the licence       */
/* lines below that floor -- so a change to the dialog font or its step does   */
/* not reach it.  Do not bring it into line with the dialog font rule.         */
/*-----------------------------------------------------------------------------*/

/* The framework: the base types (Math, event arguments), the list of the   */
/* fonts this window makes, GDI+'s fonts, colours, sizes and picture, and   */
/* the window and its controls.  The picture's memory stream is named in    */
/* full where used.                                                         */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* ABT_clsAboutForm -- the About window (the file header describes it).   */
    /* A Form whose one Framework override, OnKeyDown, is here with its UI.   */
    /*                                                                        */
    /* Derives from Form because the framework requires it: WinForms makes a  */
    /* window only as a Form subclass.  That is the one kind of inheritance   */
    /* allowed here -- from a framework base class, never from a class of     */
    /* ours.                                                                  */
    /*------------------------------------------------------------------------*/
    internal sealed class ABT_clsAboutForm : Form
    {
        /* The window's geometry -- documentation table.  The two sizes in   */
        /* pixels are at 96 dpi and scaled to the screen in the              */
        /* constructor; the gaps are not scaled.                             */
        private const int   ABT_PAD          = 20;    // space round the content, and between the picture and the text column, pixels
        private const int   ABT_LINEGAP      = 4;     // space between the lines of the text column, pixels
        private const int   ABT_PICTURE      = 288;   // the picture's side at 96 dpi, pixels; scaled to the screen -- larger than the 256-pixel artwork, so it is drawn up-scaled
        private const float ABT_DESIGN_DPI   = 96f;   // the dpi the two sizes above are designed for
        private const int   ABT_ACCENT_WIDTH = 48;    // the accent rule's length at 96 dpi, pixels: a short rule under the tagline, not a full-width line
        private const int   ABT_ACCENT_THICK = 2;     // the accent rule's and the etched line's thickness, pixels

        /* The text sizes -- documentation table.  Fixed sizes in the          */
        /* window's own face (WinForms' default), not the program's dialog     */
        /* font: this window is exempt from the dialog font and its floor by   */
        /* design (the file header), so these are its sizes and nothing        */
        /* else's.                                                             */
        private const float ABT_POINTS_NAME    = 24f; // the name's size across the top, points
        private const float ABT_POINTS_LINE    = 10f; // the long name's, the version's and the tagline's size, points
        private const float ABT_POINTS_BODY    = 9f;  // the description's and the features' size, points
        private const float ABT_POINTS_LICENCE = 8f;  // the licence lines' size, points (below the message box floor, which this window is exempt from)

        /* The two colours and the bullet -- documentation table. */
        private static readonly Color  ABT_COLOR_MUTED  = Color.FromArgb(100, 100, 100);     // the version line and the licence lines
        private static readonly Color  ABT_COLOR_ACCENT = Color.FromArgb(64, 48, 192);       // the tagline and the accent rule: a violet-blue, the bridge's colour
        private static readonly string ABT_BULLET       = ((char)0x2022).ToString() + "  ";  // the bullet (U+2022, built from its code point so the source stays ASCII) and a gap, before each feature

        /* The features, one bullet each, in the order they are listed. */
        private static readonly string[] Abt_asFeatures =
        {
            "Browse ext2, ext3, ext4, FAT12, FAT16, FAT32 and exFAT images,",      // the image pane, File > Open > Image File / Device: lwext4 for ext, FatFs for FAT and exFAT
            "    and real cards and sticks",                                       // the line above continues: the same filesystems on a device
            "Copy in and out, and drag and drop between the panes",                // Extract, Import, the clipboard and drags
            "Text, hex and picture viewers, and a structured editor",              // Open and Open As
            "    for JSON, INI and Klipper, Moonraker and KlipperScreen configs",  // the structured editor's formats (indented: the line above continues)
            "New images, partitions in disk images, e2fsck check and repair",      // File > New Image, File > Partition/Format, File > Check Filesystem (a partition of a disk image is opened through File > Open > Image File itself)
            "Device imaging, both ways"                                            // File > Save Device to Image File, Write Image File to Device
        };

        /* The window's controls and its layout state -- documentation table. */
        private Panel      Abt_pnlBody;          // the white body band: the picture and the text column
        private PictureBox Abt_picArt;           // the program's picture, large, at the left of the band; empty when the exe carries none
        private Label      Abt_lblName;          // "BifrostFM", large and bold, centred across the top of the band
        private Label      Abt_lblTagline;       // the tagline, italic, in the accent colour, centred under the name
        private Label      Abt_lblAccent;        // the short accent rule, centred under the tagline
        private Label      Abt_lblLongName;      // "Bifrost File Manager", bold: the head of the text column
        private Label      Abt_lblVersion;       // "Version x.y.z", grey
        private Label      Abt_lblDesc;          // what the program is, in a few lines
        private Label      Abt_lblFeaturesHead;  // "Features", bold
        private Label      Abt_lblFeatures;      // the features, bulleted
        private Label      Abt_lblLicence;       // the licence and the credits, small and grey
        private Label      Abt_lblFooter;        // the etched line between the band and the footer
        private Button     Abt_btnClose;         // closes the window
        private int        Abt_iPicture;         // the picture's side on this screen, pixels
        private int        Abt_iAccentWidth;     // the accent rule's length on this screen, pixels
        private bool       Abt_bLaying;          // true while Abt_Layout runs: setting the client size raises Resize, which would call it again
        private List<Font> Abt_lstFonts;         // the fonts made here, released with the window

        /*--------------------------------------------------------------------*/
        /* ABT_clsAboutForm (constructor):                                    */
        /*                                                                    */
        /* Build the window and everything in it, and lay it out once.  The   */
        /* version line and the engine interface version are assembled from   */
        /* the program's own constants, so they cannot fall out of step with  */
        /* what is actually running.                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the window, not yet shown.                     */
        /*--------------------------------------------------------------------*/
        internal ABT_clsAboutForm()
        {
            Graphics gfx;
            byte[]   abPicture;

            /* A fixed window (sized by Abt_Layout, not by the user) centred   */
            /* on the SCREEN -- it has no owner to centre on -- with no        */
            /* maximise or minimise.  KeyPreview lets OnKeyDown see Escape     */
            /* whichever control has the focus.  Not laying out yet.           */
            this.Text            = "About BifrostFM";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox     = false;
            this.MinimizeBox     = false;
            this.StartPosition   = FormStartPosition.CenterScreen;
            this.KeyPreview      = true;
            Abt_bLaying          = false;

            /* The fonts made here go when the window does. */
            Abt_lstFonts   = new List<Font>();
            this.Disposed += Abt_OnDisposed;

            /* The picture's and the rule's sizes for this screen: the design   */
            /* sizes at 96 dpi, scaled, so they keep their size against the     */
            /* text whatever the display scaling.                               */
            gfx = this.CreateGraphics();

            using (gfx)
            {   /* Read the dpi, then release the surface. */
                Abt_iPicture     = (int)Math.Round(ABT_PICTURE * gfx.DpiX / ABT_DESIGN_DPI);
                Abt_iAccentWidth = (int)Math.Round(ABT_ACCENT_WIDTH * gfx.DpiX / ABT_DESIGN_DPI);
            }

            /* The white band; the picture and the text column go in it. */
            Abt_pnlBody           = new Panel();
            Abt_pnlBody.BackColor = SystemColors.Window;
            this.Controls.Add(Abt_pnlBody);

            /* Left: the picture -- the PNG compiled into the exe                */
            /* (ENG_PICTURE_ID) -- in a box of the dpi-scaled side worked out    */
            /* above, zoomed to it so it keeps its proportions.  The stream      */
            /* is left open on purpose: an Image made from a stream needs that   */
            /* stream for as long as it lives (the framework's rule, as          */
            /* found).  Neither is disposed with the window; they go when they   */
            /* are collected.                                                    */
            Abt_picArt           = new PictureBox();
            Abt_picArt.Size      = new Size(Abt_iPicture, Abt_iPicture);
            Abt_picArt.SizeMode  = PictureBoxSizeMode.Zoom;
            Abt_picArt.BackColor = Color.Transparent;

            /* The PNG's bytes, or null when this build carries none. */
            abPicture = ENG_clsNative.ENG_ReadResource(ENG_clsNative.ENG_PICTURE_ID);

            if (abPicture != null)
            {   /* This build carries the picture: decoded from the bytes. */
                Abt_picArt.Image = Image.FromStream(new System.IO.MemoryStream(abPicture));
            }

            /* Into the band whether or not it has a picture: the layout   */
            /* keeps its space either way.                                 */
            Abt_pnlBody.Controls.Add(Abt_picArt);

            /* Across the top: the name, large and bold; the tagline, italic   */
            /* in the accent colour, right under it; and a short rule of the   */
            /* same colour under that.  All three are centred on the window    */
            /* by Abt_Layout, so the text is centred inside each label too.    */
            Abt_lblName               = Abt_NewLabel("BifrostFM", Abt_Font(ABT_POINTS_NAME, FontStyle.Bold));
            Abt_lblName.TextAlign     = ContentAlignment.MiddleCenter;
            Abt_lblTagline            = Abt_NewLabel("\"The bridge between two worlds.\"", Abt_Font(ABT_POINTS_LINE, FontStyle.Italic));
            Abt_lblTagline.ForeColor  = ABT_COLOR_ACCENT;
            Abt_lblTagline.TextAlign  = ContentAlignment.MiddleCenter;
            Abt_lblAccent              = Abt_NewLabel("", Abt_Font(ABT_POINTS_BODY, FontStyle.Regular));
            Abt_lblAccent.BackColor    = ABT_COLOR_ACCENT;

            /* Right of the picture: the long name, bold, heading the column;   */
            /* the version, grey.                                               */
            Abt_lblLongName          = Abt_NewLabel("Bifrost File Manager", Abt_Font(ABT_POINTS_LINE, FontStyle.Bold));
            Abt_lblVersion           = Abt_NewLabel("Version " + APP_clsProgram.APP_VERSION, Abt_Font(ABT_POINTS_LINE, FontStyle.Regular));
            Abt_lblVersion.ForeColor = ABT_COLOR_MUTED;

            /* What it is, in two lines (its line break is part of the text):   */
            /* every filesystem the engine opens -- ext2, ext3 and ext4         */
            /* through lwext4, FAT and exFAT through FatFs -- and where.        */
            Abt_lblDesc = Abt_NewLabel("A dual-pane file manager for the ext2, ext3, ext4, FAT and exFAT filesystems\r\n" +
                                       "in image files, and on real cards and sticks, from Windows.",
                                       Abt_Font(ABT_POINTS_BODY, FontStyle.Regular));

            /* The features: a bold heading over a bulleted list.  A line    */
            /* that begins with spaces continues the one above and gets no   */
            /* bullet of its own.                                            */
            Abt_lblFeaturesHead = Abt_NewLabel("Features", Abt_Font(ABT_POINTS_BODY, FontStyle.Bold));
            Abt_lblFeatures     = Abt_NewLabel(Abt_BulletedText(Abt_asFeatures), Abt_Font(ABT_POINTS_BODY, FontStyle.Regular));

            /* The licence and the credits, small and grey: the program's own   */
            /* licence, then every open-source library built into the engine,   */
            /* each with its holder and licence -- lwext4 and FatFs (the        */
            /* filesystems), e2fsprogs (the check), and liblzma, zlib and       */
            /* zstd (the compressed images).  lwext4's and zstd's BSD           */
            /* licences ask for their notices in a binary distribution's        */
            /* documentation (lwext4's source headers and zstd's LICENSE say    */
            /* so), as e2fsprogs' GPL asks for its own; FatFs's, liblzma's      */
            /* 0BSD and zlib's ask nothing of a binary (FatFs's LICENSE.txt     */
            /* says so in terms), and are named so the list is the whole of     */
            /* what the engine carries.  A library added to the engine is       */
            /* added here too.                                                  */
            Abt_lblLicence = Abt_NewLabel("Copyright (c) 2026 B. C. Services.  Free software under the GNU General Public License,\r\n" +
                                          "version 2 or (at your option) any later version; it comes with ABSOLUTELY NO WARRANTY.\r\n" +
                                          "Filesystem access by the bundled lwext4 library, copyright (c) Grzegorz Kostka, BSD licence,\r\n" +
                                          "and, for FAT and exFAT, by FatFs R0.16, copyright (c) ChaN, BSD-style licence.\r\n" +
                                          "Filesystem check and repair by e2fsck, from e2fsprogs 1.47.4, copyright (c) Theodore Ts'o, GPL.\r\n" +
                                          "Compressed images by liblzma from XZ Utils 5.8.1, 0BSD licence; by zlib 1.3.1,\r\n" +
                                          "copyright (c) Jean-loup Gailly and Mark Adler, zlib licence; and by zstd 1.5.7,\r\n" +
                                          "copyright (c) Meta Platforms, Inc. and affiliates, BSD licence.\r\n" +
                                          "Engine interface version " + ENG_clsNative.ENG_API_VERSION + ".  " +
                                          "Always keep a backup copy of an image before changing it.",
                                          Abt_Font(ABT_POINTS_LICENCE, FontStyle.Regular));
            Abt_lblLicence.ForeColor = ABT_COLOR_MUTED;

            /* Under the band: the etched line, made by the dialogs' shared   */
            /* helper in json\JsonEditForm.cs, which adds it to the window    */
            /* itself (not the band); Abt_Layout places it.                   */
            Abt_lblFooter = JSN_clsJsonEditForm.JSN_DlgNewEtchedLine(this);

            /* Close, on the window itself (not the band), at the bottom   */
            /* right once Abt_Layout has placed it.                        */
            Abt_btnClose        = new Button();
            Abt_btnClose.Text   = "Close";
            Abt_btnClose.Click += Abt_OnCloseClicked;
            this.Controls.Add(Abt_btnClose);

            /* Enter and Escape both close: there is nothing to accept or   */
            /* cancel, only the window to dismiss.                          */
            this.AcceptButton = Abt_btnClose;
            this.CancelButton = Abt_btnClose;

            /* Place everything, sizing the window to its content, and place   */
            /* it again whenever the size changes.                             */
            Abt_Layout();
            this.Resize += Abt_OnResize;
        }

        /*--------------------------------------------------------------------*/
        /* Abt_Font:                                                          */
        /*                                                                    */
        /* A font in the window's own typeface at a size and style, kept on   */
        /* the list so it is released with the window.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     fPoints : the size, points.                                    */
        /*     enmStyle : the style.                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Font : the font.                                               */
        /*--------------------------------------------------------------------*/
        private Font Abt_Font(float fPoints, FontStyle enmStyle)
        {
            Font fntNew;

            /* The window's face -- WinForms' default, since this window is   */
            /* deliberately given no font (the file header) -- at the size    */
            /* and style asked for, and onto the list Abt_OnDisposed frees.   */
            fntNew = new Font(this.Font.FontFamily, fPoints, enmStyle);
            Abt_lstFonts.Add(fntNew);

            /* Made and remembered. */
            return(fntNew);
        }

        /*--------------------------------------------------------------------*/
        /* Abt_NewLabel:                                                      */
        /*                                                                    */
        /* A label in the body band, with its text and font, for Abt_Layout   */
        /* to size and place.                                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sText : its text.                                              */
        /*     fnt   : its font.                                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Label : the label, parented to the band and not yet placed.    */
        /*--------------------------------------------------------------------*/
        private Label Abt_NewLabel(string sText, Font fnt)
        {
            Label lblNew;

            /* NOT auto-sized: an auto-sized label's size is not final      */
            /* until Windows lays the window out (the framework's           */
            /* behaviour, as found), so Abt_Layout sizes every              */
            /* label from its measured preferred size instead.  Then its    */
            /* text and its own font, and into the band, where Abt_Layout   */
            /* will find and place it.                                      */
            lblNew          = new Label();
            lblNew.AutoSize = false;
            lblNew.Text     = sText;
            lblNew.Font     = fnt;
            Abt_pnlBody.Controls.Add(lblNew);

            /* Placed by Abt_Layout. */
            return(lblNew);
        }

        /*--------------------------------------------------------------------*/
        /* Abt_BulletedText:                                                  */
        /*                                                                    */
        /* The features as one text, a line each: a bullet before every line  */
        /* except one that begins with spaces, which continues the line above */
        /* and is set in under its text instead.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     asLines : the features, in order.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the lines, bulleted and joined.                       */
        /*--------------------------------------------------------------------*/
        private static string Abt_BulletedText(string[] asLines)
        {
            string sText;
            int    i;

            /* Nothing yet; each line is appended in turn. */
            sText = "";

            for (i = 0; i < asLines.Length; i++)
            {   /* One feature per iteration. */
                if (i != 0)
                {   /* A line break between lines. */
                    sText = sText + "\r\n";
                }

                if (asLines[i].StartsWith(" "))
                {   /* A continuation: set in by the bullet's width, no bullet. */
                    sText = sText + new string(' ', ABT_BULLET.Length) + asLines[i].TrimStart(' ');
                }
                else
                {   /* A feature of its own. */
                    sText = sText + ABT_BULLET + asLines[i];
                }
            }

            /* The list. */
            return(sText);
        }

        /*--------------------------------------------------------------------*/
        /* Abt_Layout:                                                        */
        /*                                                                    */
        /* Place every control from its measured size and size the window to  */
        /* hold them -- the window's whole layout, done in code because the   */
        /* sizes come from the text, which differs with the screen and the    */
        /* fonts.  Run once when built and again on every resize; the body    */
        /* says each step.                                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the controls are placed and the client size set.        */
        /*--------------------------------------------------------------------*/
        private void Abt_Layout()
        {
            Label[] alblTop;
            Label[] alblColumn;
            Size    sizButton;
            int     iTopWidth;
            int     iColumnLeft;
            int     iColumnWidth;
            int     iColumnHeight;
            int     iRowTop;
            int     iContentHeight;
            int     iShift;
            int     iBodyHeight;
            int     iPictureTop;
            int     iPictureRight;
            int     iWidth;
            int     y;
            int     i;

            if (Abt_btnClose == null || Abt_bLaying)
            {   /* A resize raised by this very function's own sizing (the    */
                /* flag), which must not start a second layout inside the     */
                /* first.  The Close test is a guard only: Close is made      */
                /* before the constructor's one call here, and Resize is      */
                /* hooked only after it, so no call comes while it is null.   */
                return;
            }

            /* Laying out from here: the client size set at the end raises   */
            /* Resize, which comes straight back here and must return.       */
            Abt_bLaying = true;

            /* The top lines at their measured size; the widest decides how     */
            /* wide the window must at least be.  The running width starts at   */
            /* the accent rule's length, since the rule is centred with them    */
            /* and must fit too.                                                */
            alblTop   = new Label[] { Abt_lblName, Abt_lblTagline };
            iTopWidth = Abt_iAccentWidth;

            for (i = 0; i < alblTop.Length; i++)
            {   /* One top line per iteration. */
                alblTop[i].Size = alblTop[i].PreferredSize;

                if (alblTop[i].Width > iTopWidth)
                {   /* The widest so far. */
                    iTopWidth = alblTop[i].Width;
                }
            }

            /* Every label of the column at its measured size; the column as   */
            /* wide as the widest.                                             */
            alblColumn   = new Label[] { Abt_lblLongName, Abt_lblVersion, Abt_lblDesc, Abt_lblFeaturesHead, Abt_lblFeatures, Abt_lblLicence };
            iColumnWidth = 0;

            for (i = 0; i < alblColumn.Length; i++)
            {   /* One label of the column per iteration. */
                alblColumn[i].Size = alblColumn[i].PreferredSize;

                if (alblColumn[i].Width > iColumnWidth)
                {   /* The widest so far. */
                    iColumnWidth = alblColumn[i].Width;
                }
            }

            /* The window's width: the picture and the column side by side,   */
            /* or the widest top line, whichever needs more.                  */
            iColumnLeft = ABT_PAD + Abt_iPicture + ABT_PAD;
            iWidth      = iColumnLeft + iColumnWidth + ABT_PAD;

            if (ABT_PAD + iTopWidth + ABT_PAD > iWidth)
            {   /* A top line is wider than the row below it. */
                iWidth = ABT_PAD + iTopWidth + ABT_PAD;
            }

            /* The top lines, each centred on that width, top down: the name,   */
            /* the tagline right under it, the accent rule under that -- two    */
            /* line gaps above the rule and three below it, where the row of    */
            /* the picture and the column begins (iRowTop).                     */
            y = ABT_PAD;
            Abt_lblName.Location    = new Point((iWidth - Abt_lblName.Width) / 2, y);
            y                       = Abt_lblName.Bottom;
            Abt_lblTagline.Location = new Point((iWidth - Abt_lblTagline.Width) / 2, y);
            y                       = Abt_lblTagline.Bottom + (ABT_LINEGAP * 2);
            Abt_lblAccent.SetBounds((iWidth - Abt_iAccentWidth) / 2, y, Abt_iAccentWidth, ABT_ACCENT_THICK);
            iRowTop                 = Abt_lblAccent.Bottom + (ABT_LINEGAP * 3);

            /* The text column, right of the picture, top down -- placed from   */
            /* the row's top first, and moved down after if the picture turns   */
            /* out the taller.                                                  */
            y = iRowTop;

            Abt_lblLongName.Location = new Point(iColumnLeft, y);
            y                        = Abt_lblLongName.Bottom + ABT_LINEGAP;
            Abt_lblVersion.Location  = new Point(iColumnLeft, y);
            y                        = Abt_lblVersion.Bottom + (ABT_LINEGAP * 3);
            Abt_lblDesc.Location     = new Point(iColumnLeft, y);
            y                        = Abt_lblDesc.Bottom + (ABT_LINEGAP * 3);
            Abt_lblFeaturesHead.Location = new Point(iColumnLeft, y);
            y                        = Abt_lblFeaturesHead.Bottom + ABT_LINEGAP;
            Abt_lblFeatures.Location = new Point(iColumnLeft, y);
            y                        = Abt_lblFeatures.Bottom + (ABT_LINEGAP * 3);
            Abt_lblLicence.Location  = new Point(iColumnLeft, y);

            /* The row is as tall as the taller of the column and the   */
            /* picture; the shorter one is centred on that height.      */
            iColumnHeight  = Abt_lblLicence.Bottom - iRowTop;
            iContentHeight = iColumnHeight;

            if (Abt_iPicture > iContentHeight)
            {   /* The picture is the taller: the column moves down to centre on it. */
                iContentHeight = Abt_iPicture;
                iShift         = (iContentHeight - iColumnHeight) / 2;

                for (i = 0; i < alblColumn.Length; i++)
                {   /* One label of the column per iteration. */
                    alblColumn[i].Top = alblColumn[i].Top + iShift;
                }
            }

            /* The band: the top lines and the row, with ABT_PAD above and      */
            /* below, across the window -- and never shorter than the picture   */
            /* with ABT_PAD round it.                                           */
            iBodyHeight = Math.Max(iRowTop + iContentHeight + ABT_PAD, Abt_iPicture + (2 * ABT_PAD));

            /* The picture's top, centred on the WHOLE white band -- not on      */
            /* the row under the top lines -- so it sits in the middle of the    */
            /* white region.  Centred that way a tall picture rises into the     */
            /* rows of the top lines, whose centring on the window can then      */
            /* bring their left end over its right edge: at 96 dpi the tagline   */
            /* would begin 12 pixels inside the picture.  Neither                */
            /* centring is given up to fix it; the window is widened instead,    */
            /* just enough that each top line the picture rises beside starts    */
            /* half a pad clear of it.                                           */
            iPictureTop   = (iBodyHeight - Abt_iPicture) / 2;
            iPictureRight = ABT_PAD + Abt_iPicture + (ABT_PAD / 2);

            for (i = 0; i < alblTop.Length; i++)
            {   /* One top line per iteration. */
                if (iPictureTop < alblTop[i].Bottom && (iWidth - alblTop[i].Width) / 2 < iPictureRight)
                {   /* The picture rises beside this line and the line starts inside its clearance: widen until it does not. */
                    iWidth = alblTop[i].Width + (2 * iPictureRight);
                }
            }

            /* The top lines and the accent rule centred again on the width   */
            /* as it now stands (a no-op when nothing was widened).           */
            Abt_lblName.Left    = (iWidth - Abt_lblName.Width) / 2;
            Abt_lblTagline.Left = (iWidth - Abt_lblTagline.Width) / 2;
            Abt_lblAccent.Left   = (iWidth - Abt_iAccentWidth) / 2;

            /* The band and the picture in it. */
            Abt_pnlBody.SetBounds(0, 0, iWidth, iBodyHeight);
            Abt_picArt.Location = new Point(ABT_PAD, iPictureTop);

            /* The footer: the etched line along the band's foot, full         */
            /* width, then Close at the bottom right, at the dialogs'          */
            /* shared button size (JSN_DlgButtonSize) with three quarters of   */
            /* a pad above and to its right.                                   */
            Abt_lblFooter.SetBounds(0, iBodyHeight, iWidth, ABT_ACCENT_THICK);
            sizButton = JSN_clsJsonEditForm.JSN_DlgButtonSize(new Button[] { Abt_btnClose });
            Abt_btnClose.SetBounds(iWidth - (ABT_PAD * 3 / 4) - sizButton.Width, Abt_lblFooter.Bottom + (ABT_PAD * 3 / 4),
                                   sizButton.Width, sizButton.Height);

            /* The window holds it all: the width worked out above, down to   */
            /* Close with its margin under it.  (This raises Resize, which    */
            /* the flag turns away.)                                          */
            this.ClientSize = new Size(iWidth, Abt_btnClose.Bottom + (ABT_PAD * 3 / 4));

            /* Laid out: a later resize may run the layout again. */
            Abt_bLaying = false;
        }

        /*--------------------------------------------------------------------*/
        /* Abt_OnResize:                                                      */
        /*                                                                    */
        /* The window's size changed: place everything again from the         */
        /* measured sizes.  The border style forbids the user a resize; what  */
        /* else may raise one (a display scaling or theme change) is not      */
        /* established here.                                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the window.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the layout fits the new size.                           */
        /*--------------------------------------------------------------------*/
        private void Abt_OnResize(object objSender, EventArgs evArgs)
        {
            /* The whole layout again; Abt_Layout turns away the resize its   */
            /* own sizing raises (Abt_bLaying).                               */
            Abt_Layout();
        }

        /*--------------------------------------------------------------------*/
        /* Abt_OnCloseClicked:                                                */
        /*                                                                    */
        /* Close: close the window.                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the button.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window closes.                                      */
        /*--------------------------------------------------------------------*/
        private void Abt_OnCloseClicked(object objSender, EventArgs evArgs)
        {
            /* Closed.  The window is shown modeless, and Form.Close is   */
            /* documented to dispose of a modeless form, which runs       */
            /* Abt_OnDisposed and frees the fonts.                        */
            this.Close();
        }

        /*--------------------------------------------------------------------*/
        /* Abt_OnDisposed:                                                    */
        /*                                                                    */
        /* The window is gone: release the fonts made for it.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the window.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the fonts are released.                                 */
        /*--------------------------------------------------------------------*/
        private void Abt_OnDisposed(object objSender, EventArgs evArgs)
        {
            int i;

            for (i = 0; i < Abt_lstFonts.Count; i++)
            {   /* One font per iteration. */
                Abt_lstFonts[i].Dispose();
            }

            /* Emptied, so nothing holds the freed fonts. */
            Abt_lstFonts.Clear();
        }

        /*--------------------------------------------------------------------*/
        /* OnKeyDown:                                                         */
        /*                                                                    */
        /* The Framework's key hook for the window: hands every key to        */
        /* Abt_OnKeyDown below, which closes on Escape and then makes the     */
        /* base call itself, so the base event runs exactly once.             */
        /*                                                                    */
        /* An override because the framework requires it: a Form receives     */
        /* its own key events first only through this virtual method.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     evKey : the key pressed; the helper may mark it handled.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window may be closing.                              */
        /*--------------------------------------------------------------------*/
        protected override void OnKeyDown(KeyEventArgs evKey)
        {
            /* Escape's close and the base call are in the helper. */
            Abt_OnKeyDown(evKey);
        }

        /*--------------------------------------------------------------------*/
        /* Abt_OnKeyDown:                                                     */
        /*                                                                    */
        /* Escape closes the window.  The Close button is also the window's   */
        /* cancel button, which closes it on Escape as well; why both exist   */
        /* is not recorded, and which acts first is the framework's (not      */
        /* established here).                                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     evKey : the key pressed.                                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window may be closing.                              */
        /*--------------------------------------------------------------------*/
        private void Abt_OnKeyDown(KeyEventArgs evKey)
        {
            if (evKey.KeyCode == Keys.Escape)
            {   /* Escape -- seen here whichever control has the focus,    */
                /* since the constructor set KeyPreview: close, and mark   */
                /* the key handled so nothing else acts on it.             */
                this.Close();
                evKey.Handled = true;
            }

            /* The base handling as usual -- it raises KeyDown for anyone   */
            /* else listening.                                              */
            base.OnKeyDown(evKey);
        }
    }
}
