/*----------------------------------------------------------------------------*/
/* CommandClipboard.cs                                                        */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The main window's dealings with the Windows clipboard for paste and cut:   */
/* reading what a paste will use as one consistent snapshot, the key that     */
/* ties copied image items to this run of the program and the image open      */
/* when they were copied, and -- once a cut has been carried out -- clearing  */
/* the clipboard, but only if it still holds that same cut.  Windows numbers  */
/* every change to the clipboard (its sequence number); the snapshot is taken */
/* between two equal readings of it, and the clear compares it while holding  */
/* the clipboard open, so another program cannot put something new there      */
/* between the test and the clear.  Used on the UI thread only.               */
/*----------------------------------------------------------------------------*/

/* The framework: ArgumentException and Guid (System), the invariant     */
/* culture for the key's numbers (Globalization), the DllImport          */
/* declarations (InteropServices), and the clipboard's data object and   */
/* its format names (Windows.Forms), which CLP_ReadPaste reads.          */
using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* CNC_Access:                                                            */
    /*                                                                        */
    /* The shape of the clipboard calls that take no argument and say         */
    /* whether they worked: open, empty and close.                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when the call succeeded.                               */
    /*------------------------------------------------------------------------*/
    internal delegate bool CNC_Access();

    /*------------------------------------------------------------------------*/
    /* CNC_Sequence:                                                          */
    /*                                                                        */
    /* The shape of the call that reads the clipboard's sequence number.      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     uint : the number; 0 when it cannot be read, which never           */
    /*            identifies a cut.                                           */
    /*------------------------------------------------------------------------*/
    internal delegate uint CNC_Sequence();

    /*------------------------------------------------------------------------*/
    /* CNC_clsNativeCalls -- the four clipboard calls CLP_clsClipboard uses,  */
    /* fixed when it is made.  The program's one caller (MainCommand.cs)      */
    /* takes the Windows functions (CWN_clsWindowsNative) -- documentation    */
    /* table.                                                                 */
    /*------------------------------------------------------------------------*/
    internal sealed class CNC_clsNativeCalls
    {
        internal readonly CNC_Access CNC_pfnOpen;        // open the clipboard for this program alone; false: not opened, nothing to close
        internal readonly CNC_Sequence CNC_pfnSequence;  // read the sequence number; 0 identifies no cut
        internal readonly CNC_Access CNC_pfnEmpty;       // empty it -- only while this program holds it open
        internal readonly CNC_Access CNC_pfnClose;       // close it again; owed after every open that succeeded, whatever followed

        /*--------------------------------------------------------------------*/
        /* CNC_clsNativeCalls:                                                */
        /*                                                                    */
        /* The table, refusing one with a call missing.                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pfnOpen     : the open call.                                   */
        /*     pfnSequence : the sequence-number call.                        */
        /*     pfnEmpty    : the empty call.                                  */
        /*     pfnClose    : the close call.                                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the table; ArgumentException when any call is  */
        /*                     null.                                          */
        /*--------------------------------------------------------------------*/
        internal CNC_clsNativeCalls(CNC_Access pfnOpen, CNC_Sequence pfnSequence,
            CNC_Access pfnEmpty, CNC_Access pfnClose)
        {
            if (pfnOpen == null || pfnSequence == null || pfnEmpty == null || pfnClose == null)
            {   /* A call missing: refused, since a table that could open the   */
                /* clipboard with no way to close it would leave it held.       */
                throw new ArgumentException("Clipboard access needs all four synchronous functions.");
            }

            /* The four calls, fixed (readonly): nothing can swap one between   */
            /* the open, the comparison, the empty and the close.               */
            CNC_pfnOpen = pfnOpen;
            CNC_pfnSequence = pfnSequence;
            CNC_pfnEmpty = pfnEmpty;
            CNC_pfnClose = pfnClose;
        }
    }

    /*------------------------------------------------------------------------*/
    /* CWN_clsWindowsNative -- the Windows clipboard functions (user32.dll)   */
    /* the table is filled with.                                              */
    /*------------------------------------------------------------------------*/
    internal static class CWN_clsWindowsNative
    {
        /*--------------------------------------------------------------------*/
        /* CWN_Open:                                                          */
        /*                                                                    */
        /* Open the clipboard for this program, in order to empty it.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when it was opened (Windows refuses while another  */
        /*            program holds it open).                                 */
        /*--------------------------------------------------------------------*/
        internal static bool CWN_Open()
        {
            /* Opened with no owner window (NULL), which ties the open to the   */
            /* calling task, as OpenClipboard is documented to do.  Emptying    */
            /* it then leaves it with no owner; nothing here puts data on it,   */
            /* so no owner window is needed.                                    */
            return(Cwn_OpenNative(IntPtr.Zero));
        }

        /*--------------------------------------------------------------------*/
        /* Cwn_OpenNative:                                                    */
        /*                                                                    */
        /* OpenClipboard (user32.dll).                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hOwner : the window to own the open; zero for the task.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : the Win32 BOOL, true when the clipboard was opened.     */
        /*--------------------------------------------------------------------*/
        [DllImport("user32.dll", EntryPoint = "OpenClipboard", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Cwn_OpenNative(IntPtr hOwner);

        /*--------------------------------------------------------------------*/
        /* CWN_SequenceNative:                                                */
        /*                                                                    */
        /* GetClipboardSequenceNumber (user32.dll): the window station's      */
        /* clipboard sequence number, which changes whenever the clipboard's  */
        /* contents change.                                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     uint : the number (a DWORD); 0 without access to the           */
        /*            clipboard, as documented.                               */
        /*--------------------------------------------------------------------*/
        [DllImport("user32.dll", EntryPoint = "GetClipboardSequenceNumber", ExactSpelling = true)]
        internal static extern uint CWN_SequenceNative();

        /*--------------------------------------------------------------------*/
        /* CWN_EmptyNative:                                                   */
        /*                                                                    */
        /* EmptyClipboard (user32.dll), which needs the clipboard already     */
        /* open; it does not close it.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : the Win32 BOOL, true when it was emptied.               */
        /*--------------------------------------------------------------------*/
        [DllImport("user32.dll", EntryPoint = "EmptyClipboard", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CWN_EmptyNative();

        /*--------------------------------------------------------------------*/
        /* CWN_CloseNative:                                                   */
        /*                                                                    */
        /* CloseClipboard (user32.dll), the matching close of an open that    */
        /* succeeded.                                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : the Win32 BOOL, true when it was closed.                */
        /*--------------------------------------------------------------------*/
        [DllImport("user32.dll", EntryPoint = "CloseClipboard", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CWN_CloseNative();
    }

    /*------------------------------------------------------------------------*/
    /* CPI_clsPasteInput -- what CLP_ReadPaste read from the clipboard for    */
    /* one paste: either Windows file paths or the image pane's own item      */
    /* list, whether it was a cut, and the sequence number the whole read     */
    /* was taken under.  Values only: nothing of the clipboard's data object  */
    /* is kept -- documentation table.                                        */
    /*------------------------------------------------------------------------*/
    internal class CPI_clsPasteInput
    {
        internal string[] CPI_arrHostPaths;  // Windows paths (the file-drop format), a copy CLP_ReadPaste made; null for image items
        internal string CPI_sImageList;      // the image pane's item list as copied (its own format, one line per item), null when absent
        internal string CPI_sRange;          // the key copied with it (CLP_SourceRange: this run, the image, its range and lifetime); null when absent
        internal bool CPI_bCut;              // true when the clipboard's drop effect says Move -- a cut
        internal uint CPI_uiSequence;        // the sequence number the read was taken under; never 0 for a cut (CLP_ReadPaste refuses that)

        /*--------------------------------------------------------------------*/
        /* CPI_clsPasteInput:                                                 */
        /*                                                                    */
        /* The record of one read.                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrHost    : the Windows paths, already copied; null for       */
        /*                  image items.                                      */
        /*     sImageList : the item list, or null.                           */
        /*     sRange     : the key copied with it, or null.                  */
        /*     bCut       : true for a cut.                                   */
        /*     uiSequence : the sequence number the read was taken under.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the record.                                    */
        /*--------------------------------------------------------------------*/
        internal CPI_clsPasteInput(string[] arrHost, string sImageList,
            string sRange, bool bCut, uint uiSequence)
        {
            /* Kept as given.  The paths array is CLP_ReadPaste's own copy,   */
            /* made before any further call to the clipboard's provider, so   */
            /* it is kept rather than copied again; the strings cannot        */
            /* change.  The requests the paste posts copy what they use.      */
            CPI_arrHostPaths = arrHost;
            CPI_sImageList = sImageList;
            CPI_sRange = sRange;
            CPI_bCut = bCut;
            CPI_uiSequence = uiSequence;
        }
    }

    /*------------------------------------------------------------------------*/
    /* CLP_GetData:                                                           */
    /*                                                                        */
    /* The shape of the call that fetches the clipboard's data object (the    */
    /* paste passes Clipboard.GetDataObject).                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     IDataObject : the data object, or null when there is nothing.      */
    /*------------------------------------------------------------------------*/
    internal delegate IDataObject CLP_GetData();

    /*------------------------------------------------------------------------*/
    /* CLP_IsCut:                                                             */
    /*                                                                        */
    /* The shape of the call that says whether the clipboard holds a cut --   */
    /* its Preferred DropEffect says Move (the paste passes MainForm.cs's     */
    /* Main_ClipboardIsCut).                                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     objData : the data object the rest of the read uses.               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true for a cut, false for a copy.                           */
    /*------------------------------------------------------------------------*/
    internal delegate bool CLP_IsCut(IDataObject objData);

    /*------------------------------------------------------------------------*/
    /* CLP_clsClipboard -- the main window's clipboard helper (made once, by  */
    /* MainCommand.cs).  It holds the clipboard open only inside CLP_Consume  */
    /* -- documentation table.                                                */
    /*------------------------------------------------------------------------*/
    internal sealed class CLP_clsClipboard
    {
        /* Used on the UI thread only; it never goes into a request. */
        private readonly CNC_clsNativeCalls Clp_objCalls;  // the four clipboard calls; the clipboard is opened only in CLP_Consume
        private readonly string Clp_sOwner;                // a new GUID per run of the program: another run's copied items, whose numbers may repeat these, never match

        /*--------------------------------------------------------------------*/
        /* CLP_clsClipboard:                                                  */
        /*                                                                    */
        /* The helper, with the Windows calls or a table given.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objCalls : the calls to use; optional, null by default, which  */
        /*                takes the Windows functions (the one caller passes  */
        /*                none).                                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the helper, holding nothing open.              */
        /*--------------------------------------------------------------------*/
        internal CLP_clsClipboard(CNC_clsNativeCalls objCalls = null)
        {
            /* The calls: either table is used the same way. */
            if (objCalls == null)
            {   /* None given: the Windows functions.  Making the table   */
                /* opens nothing.                                         */
                Clp_objCalls = new CNC_clsNativeCalls(CWN_clsWindowsNative.CWN_Open,
                    CWN_clsWindowsNative.CWN_SequenceNative, CWN_clsWindowsNative.CWN_EmptyNative, CWN_clsWindowsNative.CWN_CloseNative);
            }
            else
            {   /* A table given: used as it is. */
                Clp_objCalls = objCalls;
            }

            /* This run's own mark, part of every key CLP_SourceRange makes.   */
            /* The other parts are counters that start again in each run, so   */
            /* without it items copied by an earlier run (or another copy of   */
            /* the program) could match this run's key.                        */
            Clp_sOwner = Guid.NewGuid().ToString("N");
        }

        /*--------------------------------------------------------------------*/
        /* CLP_CurrentSequence:                                               */
        /*                                                                    */
        /* The clipboard's sequence number now.                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     uint : the number; 0 when it cannot be read.                   */
        /*--------------------------------------------------------------------*/
        internal uint CLP_CurrentSequence()
        {
            /* Read as it stands.  It can move while the clipboard is read:     */
            /* a program that renders a format only when asked for it changes   */
            /* the clipboard as it does -- which is why CLP_ReadPaste reads     */
            /* it before and after.                                             */
            return(Clp_objCalls.CNC_pfnSequence());
        }

        /*--------------------------------------------------------------------*/
        /* CLP_ReadPaste:                                                     */
        /*                                                                    */
        /* Read what a paste will use, as values taken from one unchanged     */
        /* clipboard: Windows paths, or the image pane's own items with their */
        /* key; whether it is a cut; and the sequence number it was read      */
        /* under.  Tried twice at most.                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pfnGet       : fetches the data object; it may run nested UI   */
        /*                    messages.                                       */
        /*     pfnCut       : says whether it is a cut, from the same object. */
        /*     sImageFormat : the image pane's item-list format name.         */
        /*     sRangeFormat : the format name of the key copied with it.      */
        /*     sReason      : receives the reason when nothing is returned;   */
        /*                    "" otherwise.                                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     CPI_clsPasteInput : the values; null with the reason when the  */
        /*                         clipboard is empty, unusable, kept         */
        /*                         changing, or is a cut whose number cannot  */
        /*                         be read.                                   */
        /*--------------------------------------------------------------------*/
        internal CPI_clsPasteInput CLP_ReadPaste(CLP_GetData pfnGet, CLP_IsCut pfnCut,
            string sImageFormat, string sRangeFormat, out string sReason)
        {
            IDataObject objData;
            CPI_clsPasteInput objInput;
            uint uiBefore;
            uint uiAfter;
            bool bCut;
            string[] arrHost;
            string[] arrProvider;
            string sImage;
            string sRange;
            int i;

            /* Two tries: a program that renders its formats only when asked    */
            /* can change the clipboard during the first read, and the second   */
            /* then normally finds it settled.  Each try reads everything       */
            /* afresh -- formats from two different clipboards are never put    */
            /* together.  The reason starts as the one for a clipboard that     */
            /* kept changing, which is what falling out of the loop means.      */
            sReason = "The clipboard changed while reading it; try Paste again.";
            for (i = 0; i < 2; i++)
            {   /* One whole read per iteration, between two readings of the   */
                /* sequence number.                                            */
                uiBefore = CLP_CurrentSequence();
                objData = pfnGet();
                if (objData == null)
                {   /* Nothing on the clipboard: nothing to paste. */
                    sReason = "There is nothing on the clipboard to paste.";
                    return(null);
                }

                /* Everything from this one object: cut or copy, then the        */
                /* paths.  Windows paths (the file-drop format) are taken when   */
                /* present, ahead of the image pane's items -- the order the     */
                /* paste in MainForm.cs handles them in.                         */
                bCut = pfnCut(objData);
                arrHost = null;
                sImage = null;
                sRange = null;
                if (objData.GetDataPresent(DataFormats.FileDrop))
                {   /* Windows files: their paths, copied at once, before the   */
                    /* provider is called again and could change its array.     */
                    arrProvider = objData.GetData(DataFormats.FileDrop) as string[];
                    if (arrProvider != null)
                    {   /* An array of paths: this read's own copy of it. */
                        arrHost = (string[])arrProvider.Clone();
                    }
                }
                else if (objData.GetDataPresent(sImageFormat))
                {   /* The image pane's items: the list and the key copied    */
                    /* with it (strings, which cannot change once read; the   */
                    /* key may be absent, which the paste then refuses).      */
                    sImage = objData.GetData(sImageFormat) as string;
                    sRange = objData.GetData(sRangeFormat) as string;
                }

                /* The record, then the second reading of the number: when it   */
                /* has moved, something changed the clipboard during the read   */
                /* -- a format rendered on request, or a new copy by the user   */
                /* -- and this read is thrown away, however usable it looks.    */
                objInput = new CPI_clsPasteInput(arrHost, sImage, sRange, bCut, uiBefore);
                uiAfter = CLP_CurrentSequence();
                if (uiBefore != uiAfter)
                {   /* Changed during the read: try again from the start. */
                    continue;
                }

                if (bCut && uiBefore == 0)
                {   /* A cut whose number cannot be read (0): refused, since    */
                    /* the number is what lets the worker refuse a cut pasted   */
                    /* twice and what CLP_Consume matches before clearing.      */
                    sReason = "The clipboard cut cannot be identified safely; copy or cut the items again.";
                    return(null);
                }

                if (arrHost == null && sImage == null)
                {   /* Neither files nor items (another format, or data of the   */
                    /* wrong type): refused, rather than handed on as a paste    */
                    /* of nothing.                                               */
                    sReason = "The clipboard does not hold files or image items to paste.";
                    return(null);
                }

                /* A usable read, from one unchanged clipboard. */
                sReason = "";

                /* The values, with nothing of the clipboard left to call. */
                return(objInput);
            }

            /* Changed during both reads: refused (with the reason set above)   */
            /* rather than tried again and again on the UI thread.              */
            return(null);
        }

        /*--------------------------------------------------------------------*/
        /* CLP_Consume:                                                       */
        /*                                                                    */
        /* Clear the clipboard after a cut has been carried out -- but only   */
        /* if it still holds that cut, so a copy the user has made since is   */
        /* left alone.  Called when the worker reports the cut spent          */
        /* (MainCommand.cs, from CommandCopySequence.cs).                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uiExpected : the cut's sequence number, as read when it was    */
        /*                  pasted.                                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the clipboard was emptied; false when it was  */
        /*            left as it is -- no number, the clipboard held open by  */
        /*            another program, something new on it, or the empty      */
        /*            failed.                                                 */
        /*--------------------------------------------------------------------*/
        internal bool CLP_Consume(uint uiExpected)
        {
            if (uiExpected == 0 || !Clp_objCalls.CNC_pfnOpen())
            {   /* No number to match, or the clipboard could not be opened   */
                /* (another program holds it): left alone.  Nothing was       */
                /* opened, so there is nothing to close.                      */
                return(false);
            }

            try
            {   /* The clipboard is open, which the try's finally closes.        */
                /* The number is compared only now: while this program holds     */
                /* it open no other program can change it, so the test and       */
                /* the empty see the same contents.  Compared before the open,   */
                /* a copy made in between would be emptied.                      */
                if (Clp_objCalls.CNC_pfnSequence() != uiExpected)
                {   /* It has changed since the cut: someone's new copy, not   */
                    /* this program's to clear.                                */
                    return(false);
                }

                /* Still the cut: emptied, while it is held open. */
                return(Clp_objCalls.CNC_pfnEmpty());
            }
            finally
            {   /* Closed on every path out of the try -- a mismatch, the   */
                /* empty, an exception -- since a successful open owes a    */
                /* close.                                                   */
                if (!Clp_objCalls.CNC_pfnClose())
                {   /* The close failed: logged only; the result stays the   */
                    /* empty's (or the mismatch's).                          */
                    if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                    {   /* Only while the debug log is kept: otherwise the line is never built. */
                        DBGLOG_clsDebugLog.DBGLOG_Write("clipboard", "CloseClipboard failed after cut consumption check.");
                    }
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* CLP_SourceRange:                                                   */
        /*                                                                    */
        /* The key put on the clipboard with copied image items, and compared */
        /* when they are pasted (MainForm.cs): this run of the program, the   */
        /* image open, its range, its lifetime (epoch) and its close count.   */
        /* Items whose key no longer matches belong to an image since closed  */
        /* or reopened, or to another run, and are refused.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objState : the worker's published state; may be null before    */
        /*                anything is open.                                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the key; "" when no image is open, which matches no   */
        /*              copied items.                                         */
        /*--------------------------------------------------------------------*/
        internal string CLP_SourceRange(STV_clsStateView objState)
        {
            if (objState == null || objState.STV_enmMode != MOD_enmMode.Open || objState.STV_objTarget == null)
            {   /* No image open: no key, and nothing copied can be pasted   */
                /* into the image.                                           */
                return("");
            }

            /* The key's parts: this run's mark, the image's path (upper case,   */
            /* since Windows paths ignore case), its offset and length, its      */
            /* epoch -- stepped by every NEW open, so reopening the same range   */
            /* gives a new key -- and its close count, which steps on every      */
            /* close, including the read-only / writable reopens that keep the   */
            /* epoch.  Numbers in the invariant culture, so the key is the       */
            /* same text whatever the user's settings.                           */
            return(RQT_clsRequest.RQT_MakeKey(Clp_sOwner, objState.STV_objTarget.TGT_sPath.ToUpperInvariant(),
                objState.STV_objTarget.TGT_ullOffset.ToString(CultureInfo.InvariantCulture),
                objState.STV_objTarget.TGT_ullLength.ToString(CultureInfo.InvariantCulture),
                objState.STV_lEpoch.ToString(CultureInfo.InvariantCulture),
                objState.STV_lClipboardLifetime.ToString(CultureInfo.InvariantCulture)));
        }
    }
}
