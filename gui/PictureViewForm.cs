/*----------------------------------------------------------------------------*/
/* PictureViewForm.cs                                                         */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The program's own picture viewer, for a PNG or JPEG file inside an image:  */
/* a window that shows the picture and nothing else.  It only LOOKS -- there  */
/* is no way back into the image from here, so nothing is ever saved.         */
/*                                                                            */
/* A picture no larger than the window is shown at its real size, centred; a  */
/* larger one is scaled down to fit, keeping its proportions.  The status bar */
/* gives its real size either way.                                            */
/*                                                                            */
/* How a file gets here: a plain Open of an image entry (MainForm's           */
/* Main_ShowOpened) whose first bytes carry a PNG or JPEG signature and which */
/* is within the scanner's picture size limit (OpenScan.cs, OSC_Classify).    */
/* The signature is only a promise; whether the bytes really decode is found  */
/* out here, and a failure is handed back rather than thrown.  A Windows file */
/* never reaches this viewer: the Windows pane opens only as text, as hex or  */
/* in the structured editor.                                                  */
/*----------------------------------------------------------------------------*/

/* The framework: the base types (event arguments, the exceptions caught),   */
/* GDI+'s picture, sizes and colours, the file read and the memory stream    */
/* the picture is decoded from, and the window and its controls.             */
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* PIC_clsPictureForm -- one picture viewer window.                       */
    /*                                                                        */
    /* Its life: Main_ShowOpened constructs it and reads PIC_ReadLoadError.   */
    /* A picture that would not decode is said so, its content released       */
    /* and the window disposed unshown; otherwise the window is registered    */
    /* with the editor coordinator and then shown (Main_TrackCommandViewer,   */
    /* MainCommand.cs) -- modeless, with no owner, its own window on the      */
    /* taskbar, beside the main window rather than over it.                   */
    /* Closing it releases the decoded picture (Pic_OnFormClosed); the form   */
    /* and its controls are disposed by the framework, as for any modeless    */
    /* window that is closed.                                                 */
    /*                                                                        */
    /* Derives from Form because the framework requires it: WinForms makes a  */
    /* window only as a Form subclass.  That is the one kind of inheritance   */
    /* allowed here -- from a framework base class, never from a class of     */
    /* ours.                                                                  */
    /*------------------------------------------------------------------------*/
    internal sealed class PIC_clsPictureForm : Form
    {
        /* The window's opening size -- documentation table.  The window is   */
        /* NOT sized to the picture: every viewer opens at this size, and     */
        /* Pic_FitPicture then decides how the picture sits inside it.  Why   */
        /* these two values were chosen is not recorded.                      */
        private const int PIC_DEFAULT_WIDTH  = 900;    // client area width when the window opens, pixels
        private const int PIC_DEFAULT_HEIGHT = 640;    // client area height when the window opens, pixels (the status bar comes out of this)

        /* The load result -- documentation table. */
        private string Pic_sLoadError;    // "" when the picture decoded; otherwise the framework's reason, and the window is not to be shown (read by Main_ShowOpened through PIC_ReadLoadError, straight after construction)

        /* The window's parts and the picture it owns -- documentation table. */
        private PictureBox           Pic_picShown;     // shows the picture; docked to fill everything above the status bar; its sizing mode is set by Pic_FitPicture
        private StatusStrip          Pic_stsStatus;    // the status bar, docked to the bottom (a StatusStrip's default dock)
        private ToolStripStatusLabel Pic_tslFacts;     // the picture's size in pixels and the file's size in bytes, fixed for the window's life
        private MemoryStream         Pic_stmBytes;     // the file's bytes; the framework reads from this for as long as the picture is in use, so it stays open until PIC_ReleaseContent (null once released)
        private Image                Pic_imgShown;     // the decoded picture; OWNED here, not by the picture box, and disposed by PIC_ReleaseContent (null once released, or when it would not decode)

        /*--------------------------------------------------------------------*/
        /* PIC_clsPictureForm (constructor):                                  */
        /*                                                                    */
        /* Build the window and load the picture from the entry's temporary   */
        /* file, without holding that file open.  A file that is not a        */
        /* picture the framework can decode, despite its signature, leaves    */
        /* PIC_ReadLoadError saying so; the caller then shows that, releases  */
        /* what was read (PIC_ReleaseContent) and disposes the window without */
        /* showing it.                                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objFile : the image entry, and the temporary Windows file that */
        /*               holds its content.  Only OFD_sTempFile (read) and    */
        /*               OFD_Title (the caption) are used.                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the window, not yet shown.                     */
        /*--------------------------------------------------------------------*/
        internal PIC_clsPictureForm(OFD_clsOpenedFile objFile)
        {
            byte[] abFile;

            /* Assume success; the catch below replaces this with the reason. */
            Pic_sLoadError = "";

            /* The window itself.  The caption names the entry and the image   */
            /* it is in, and is what the taskbar shows for this window.  The   */
            /* window has no owner (Main_TrackViewer), so there is no parent   */
            /* to centre on: it is centred on the screen.                      */
            this.Text          = objFile.OFD_Title() + " - BifrostFM Picture";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.ClientSize    = new Size(PIC_DEFAULT_WIDTH, PIC_DEFAULT_HEIGHT);

            /* Load the picture: the whole file into memory, then decoded       */
            /* from a stream over those bytes.  NOT Image.FromFile, which       */
            /* keeps the file locked until the picture is disposed; and the     */
            /* stream is NOT closed after decoding, because the framework's     */
            /* rule is that a picture's source stream stays open for as long    */
            /* as the picture is in use -- closing it early fails only later,   */
            /* when the picture is drawn.  So the stream is kept in a field     */
            /* and released with the picture when the window closes.  The       */
            /* bytes' count is also kept, for the status bar.                   */
            try
            {   /* Reading can fail like any file; decoding throws on a damaged or mislabelled picture. */
                abFile       = File.ReadAllBytes(objFile.OFD_sTempFile);
                Pic_stmBytes = new MemoryStream(abFile);
                Pic_imgShown = Image.FromStream(Pic_stmBytes);
            }
            catch (Exception exLoad)
            {   /* Nothing to show.  EVERY exception is caught, not only the    */
                /* argument error a bad picture usually raises: the drawing     */
                /* library also reports some damaged pictures as being out of   */
                /* memory, and the caller has been promised a message, never    */
                /* a throw.  A stream made before decoding failed stays in      */
                /* its field: PIC_ReleaseContent, which the caller runs         */
                /* before disposing the unshown window, closes it.              */

                /* Hand the framework's reason to the caller and stop here.   */
                /* The order is load first, then build, for this return: it   */
                /* comes before any control exists or any event handler is    */
                /* attached, so the failed window is an empty shell whose     */
                /* handlers can never run against fields never filled in.     */
                Pic_sLoadError = exLoad.Message;
                return;
            }

            /* The picture box: fills the window above the status bar, and       */
            /* draws the picture it is given.  The area around a picture         */
            /* smaller than the box is painted in the system's AppWorkspace      */
            /* colour, which keeps the picture's own edges visible against       */
            /* it.  The box does NOT take ownership of the picture -- it never   */
            /* disposes an Image it is handed -- which is why the window         */
            /* disposes it itself (Pic_OnFormClosed).                            */
            Pic_picShown           = new PictureBox();
            Pic_picShown.Dock      = DockStyle.Fill;
            Pic_picShown.BackColor = SystemColors.AppWorkspace;
            Pic_picShown.Image     = Pic_imgShown;

            /* The status bar's one label: the picture's real size and the       */
            /* file's size, the bytes grouped by the user's own number format.   */
            /* Spring makes the label take the bar's whole width, and the text   */
            /* is then held at its left rather than centred in it.               */
            Pic_tslFacts           = new ToolStripStatusLabel(Pic_imgShown.Width + " x " + Pic_imgShown.Height + " pixels, " +
                                                              abFile.Length.ToString("N0") + " bytes");
            Pic_tslFacts.Spring    = true;
            Pic_tslFacts.TextAlign = ContentAlignment.MiddleLeft;

            /* The status bar, holding that label; it docks to the bottom by default. */
            Pic_stsStatus = new StatusStrip();
            Pic_stsStatus.Items.Add(Pic_tslFacts);

            /* Docking runs in reverse order of adding: the picture fills     */
            /* what the status bar leaves.  Added the other way round, the    */
            /* picture would take the whole window and the status bar would   */
            /* sit over its bottom edge.                                      */
            this.Controls.Add(Pic_picShown);
            this.Controls.Add(Pic_stsStatus);

            /* The two events, attached only now that the picture and the        */
            /* controls exist.  Attaching Resize does not raise it, so the       */
            /* first fit is made here by hand: without it the picture box        */
            /* would keep its default mode -- the picture drawn unscaled from    */
            /* its top-left corner and cut off at the edges -- until the user    */
            /* happened to resize the window.  Adding the controls has already   */
            /* laid them out, so the box's size is the one the fit needs.        */
            this.Resize     += Pic_OnResize;
            this.FormClosed += Pic_OnFormClosed;
            Pic_FitPicture();
        }

        /*--------------------------------------------------------------------*/
        /* Pic_FitPicture:                                                    */
        /*                                                                    */
        /* Choose how the picture sits in the window at its present size: at  */
        /* its real size and centred when it fits, scaled down to fit (its    */
        /* proportions kept) when it does not.  A small picture is never      */
        /* blown up.                                                          */
        /*                                                                    */
        /* That last rule is why there are two modes rather than one: Zoom    */
        /* alone would also fit every large picture, but it scales a SMALL    */
        /* one UP to fill the window, and an icon-sized picture would then be */
        /* shown blurred and many times its size.  CenterImage draws the      */
        /* picture pixel for pixel and would cut a large one off, so it is    */
        /* used only when the whole picture fits.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the picture box's sizing mode is set.                   */
        /*--------------------------------------------------------------------*/
        private void Pic_FitPicture()
        {
            /* Both dimensions must fit inside the box's client area; an   */
            /* exact fit counts as fitting and is shown at real size.      */
            if (Pic_imgShown.Width <= Pic_picShown.ClientSize.Width && Pic_imgShown.Height <= Pic_picShown.ClientSize.Height)
            {   /* It fits: real size, centred. */
                Pic_picShown.SizeMode = PictureBoxSizeMode.CenterImage;
            }
            else
            {   /* Too large, in either dimension: scaled down to fit, proportions kept. */
                Pic_picShown.SizeMode = PictureBoxSizeMode.Zoom;
            }
        }

        /*--------------------------------------------------------------------*/
        /* Pic_OnResize:                                                      */
        /*                                                                    */
        /* The window changed size -- the user dragging the border,           */
        /* maximising, minimising or restoring: decide again whether the      */
        /* picture fits.                                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the window.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the picture is shown to suit the new size.              */
        /*--------------------------------------------------------------------*/
        private void Pic_OnResize(object objSender, EventArgs evArgs)
        {
            /* Re-choose the sizing mode for the box's new size.  The window   */
            /* re-lays out its docked controls BEFORE it raises this event,    */
            /* so the box's size Pic_FitPicture reads is already the new       */
            /* one.  Minimising shrinks the box to nothing and so selects      */
            /* Zoom; restoring raises this again and puts the right mode       */
            /* back.                                                           */
            Pic_FitPicture();
        }

        /*--------------------------------------------------------------------*/
        /* Pic_OnFormClosed:                                                  */
        /*                                                                    */
        /* The window has closed: release the picture and the bytes it was    */
        /* decoded from.  The form and its controls are disposed by the       */
        /* framework, but the picture box never disposes the picture it was   */
        /* given, and the stream is the window's own -- so without this, both */
        /* would wait for the collector.                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the window.                                        */
        /*     evClosed  : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the picture's memory is released.                       */
        /*--------------------------------------------------------------------*/
        private void Pic_OnFormClosed(object objSender, FormClosedEventArgs evClosed)
        {
            /* The release, through the same function a failed showing         */
            /* uses (PIC_ReleaseContent).  This handler was hooked in the      */
            /* constructor, before the editor coordinator hooks its own        */
            /* FormClosed (EUI_Register), so it runs first -- handlers run     */
            /* in the order they were hooked.  That order changes nothing on   */
            /* disk: the file was read whole and is not held open, so the      */
            /* worker may remove it whenever the window's end is reported.     */
            PIC_ReleaseContent();
        }

        /*--------------------------------------------------------------------*/
        /* PIC_ReadLoadError:                                                 */
        /*                                                                    */
        /* Why the picture would not decode, as the constructor recorded it,  */
        /* for Main_ShowOpened to read before it registers or shows the       */
        /* window.  Reading it does nothing else: nothing is decoded again.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : "" when the picture decoded; otherwise the            */
        /*              framework's message.                                  */
        /*--------------------------------------------------------------------*/
        internal string PIC_ReadLoadError()
        {
            /* The reason as recorded. */
            return(Pic_sLoadError);
        }

        /*--------------------------------------------------------------------*/
        /* PIC_ReleaseContent:                                                */
        /*                                                                    */
        /* Release the decoded picture and the stream it was decoded from --  */
        /* when the window closes (Pic_OnFormClosed), and by hand when it was */
        /* never shown or its showing failed (Main_ShowOpened, and            */
        /* Main_ReleaseViewerResources in MainCommand.cs), since disposing a  */
        /* form does not run its FormClosed.  It may be called more than      */
        /* once: what has been released is set to null and skipped next time. */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : everything released; a failure to release throws, and   */
        /*            the caller does not take the viewer for closed.         */
        /*--------------------------------------------------------------------*/
        internal void PIC_ReleaseContent()
        {
            /* The resize handler unhooked first: a resize during the         */
            /* teardown would run Pic_FitPicture, which reads the picture's   */
            /* size and would fail on a picture already gone.  (Unhooking a   */
            /* handler never hooked -- a picture that did not decode -- is    */
            /* harmless.)                                                     */
            this.Resize -= Pic_OnResize;
            if (Pic_picShown != null)
            {   /* The box exists (the picture decoded): the picture taken   */
                /* out of it first, so nothing can paint a picture being     */
                /* disposed.                                                 */
                Pic_picShown.Image = null;
            }

            if (Pic_imgShown != null)
            {   /* A picture to dispose -- before the stream, which it may   */
                /* read until the moment it is disposed.  The field is       */
                /* cleared only after Dispose returns, so a disposal that    */
                /* throws leaves it set for another try.                     */
                Pic_imgShown.Dispose();
                Pic_imgShown = null;
            }

            if (Pic_stmBytes != null)
            {   /* The stream, last: no picture can read it now.  This also    */
                /* covers a picture that did not decode, which left only the   */
                /* stream behind.                                              */
                Pic_stmBytes.Dispose();
                Pic_stmBytes = null;
            }
        }
    }
}
