/*----------------------------------------------------------------------------*/
/* AppSettings.cs                                                             */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* BifrostFM's remembered settings.  They live in the REGISTRY, under         */
/* HKEY_CURRENT_USER\Software\BifrostFM, rather than in an INI file beside    */
/* the exe: this is a tool that gets run from wherever it was unpacked, and   */
/* it should not leave a settings file behind in every such folder.           */
/*                                                                            */
/* Every read supplies a default and returns it when the value is missing or  */
/* is not of the expected kind, so a first run, a hand-edited key and a key   */
/* written by a later version all start the program normally.                 */
/*----------------------------------------------------------------------------*/

/* The framework: the base types (the exceptions every read and write   */
/* absorbs), and the registry the settings are kept in.                 */
using System;
using Microsoft.Win32;

namespace BifrostFM
{
    internal static class SET_Settings
    {
        /* Where the settings live, and the value names.  Each entry says      */
        /* what the value holds and, where the reader gives one, what an       */
        /* absent value means -- the default each reader passes (MainForm.cs   */
        /* unless named).  The names are stored in users' registries, so a     */
        /* renamed value is a setting forgotten, not migrated.                 */
        private const string SET_KEY_PATH        = "Software\\BifrostFM";      // under HKEY_CURRENT_USER
        internal const string SET_WINDOW_LEFT    = "WindowLeft";               // main window left edge, pixels; absent: centred on the primary screen's working area (DWORD)
        internal const string SET_WINDOW_TOP     = "WindowTop";                // main window top edge, pixels; absent: centred, as for the left edge (DWORD)
        internal const string SET_WINDOW_WIDTH   = "WindowWidth";              // main window width, pixels; absent: MAIN_DEFAULT_WIDTH (DWORD)
        internal const string SET_WINDOW_HEIGHT  = "WindowHeight";             // main window height, pixels; absent: MAIN_DEFAULT_HEIGHT (DWORD)
        internal const string SET_WINDOW_MAX     = "WindowMaximized";          // 1 when the window was maximized; 0 or absent: not (DWORD)
        internal const string SET_SPLIT_PERMILLE = "SplitPermille";            // image pane's share of the width, in thousandths; absent: MAIN_DEFAULT_SPLIT (DWORD)
        internal const string SET_LAST_IMAGE     = "LastImage";                // the image file opened last; absent: "" (string)
        internal const string SET_HOST_FOLDER    = "HostFolder";               // the Windows folder shown last; absent: "" (string)
        internal const string SET_IMAGE_FOLDER   = "ImageFolder";              // the folder of the image file last chosen or made in ANY image-file dialog (Open, Check, Partition/Format, New Image, Read Device, Write Image), where every one of them starts next; absent: "" (string); an older NewImageFolder value, which only New Image used, is no longer read
        internal const string SET_IMAGE_VIEW     = "ImageView";                // the image pane's list view mode, a System.Windows.Forms.View value; absent: Details (DWORD)
        internal const string SET_HOST_VIEW      = "HostView";                 // the Windows pane's list view mode, a System.Windows.Forms.View value; absent: Details (DWORD)
        internal const string SET_LIST_FONT      = "ListFontPoints";           // the size of the text in both lists, points; absent: MAIN_FONT_DEFAULT (DWORD)
        internal const string SET_EDITOR_FONT    = "EditorFontPoints";         // the size of the text in the text editor and the hex editor, points; absent: each editor's own default (TXE_FONT_DEFAULT, HEXF_FONT_DEFAULT) (DWORD)
        internal const string SET_SHOW_HOST      = "ShowWindowsPane";          // 0 when the Windows (right) pane is hidden; 1 or absent when it shows (DWORD)
        internal const string SET_TOOL_SMALL     = "ToolbarIconsAlwaysSmall";  // 1: the toolbars always use the small pictures; 0 or absent: small only when the window is too narrow for the large ones (DWORD)
        internal const string SET_IMAGE_SORT     = "ImageSortColumn";          // the column the image pane is sorted by, 0 = Name; absent: 0 (DWORD)
        internal const string SET_IMAGE_SORT_DESC = "ImageSortDescending";     // 1 when the image pane's order is descending; 0 or absent: ascending (DWORD)
        internal const string SET_HOST_SORT      = "HostSortColumn";           // the column the Windows pane is sorted by, 0 = Name; absent: 0 (DWORD)
        internal const string SET_HOST_SORT_DESC = "HostSortDescending";       // 1 when the Windows pane's order is descending; 0 or absent: ascending (DWORD)
        internal const string SET_IMAGE_COLUMNS  = "ImageColumnWidths";        // the image pane's column widths the user set by dragging: one number per column, comma-separated, pixels; 0 for a column left automatic; absent: every column automatic (string)
        internal const string SET_HOST_COLUMNS   = "HostColumnWidths";         // the same, for the Windows pane (string)
        internal const string SET_OPT_META_OUT   = "ExtractWritesManifest";    // 1: extracting writes the Unix metadata manifest; 0 or absent: it does not (DWORD)
        internal const string SET_OPT_META_IN    = "ImportUsesManifest";       // 1 or absent: importing restores metadata from a manifest when present; 0: it does not (DWORD)
        internal const string SET_OPT_SET_TIMES  = "ExtractSetsTimes";         // 1 or absent: extracted files take their modify time from the image; 0: they do not (DWORD)
        internal const string SET_OPT_JSON_OPEN  = "OpenJsonInJsonEditor";     // 1 or absent: a plain Open of a text entry the program can tell is a config file -- JSON, INI or Klipper config, by its name, or JSON by its content -- goes to the structured editor; 0: to the text editor like any text (DWORD; the name predates INI and Klipper)
        internal const string SET_OPT_SHOW_FIXED = "ShowFixedDisks";           // 1: the Open Device list includes fixed (internal) disks; 0 or absent: removable, USB, SD and MMC only (DWORD)
        internal const string SET_OPT_READ_XZ    = "ReadDeviceCompressXz";     // 1: Read Device's "Compress the image as .xz" box starts ticked (the last choice made); 0 or absent: unticked (DWORD)
        internal const string SET_OPT_VERIFY     = "VerifyDeviceWrites";       // 1: an ordinary session's writes to a device are read back and compared (slower); 0 or absent: only a format, a resize and a repair are (DWORD)
        internal const string SET_ITEM_CHECKS    = "ItemCheckBoxes";           // 1: both panes show a check box on every row, the tick mirroring the selection, as Explorer's "Item check boxes"; 0 or absent: none (DWORD)
        internal const string SET_OPT_WARNINGS   = "RiskWarnings";             // 1 or absent: the red "at your own risk" questions are asked (first write, partition window, check window); 0: turned off, after two confirmations (DWORD)
        internal const string SET_NAME_MAX_CHARS = "NameMaxChars";             // the List and Small Icons views' name cap, in average characters; absent: the list's own default (50); NOT in any menu -- set by hand in the registry (DWORD)

        /*--------------------------------------------------------------------*/
        /* SET_ReadInt:                                                       */
        /*                                                                    */
        /* Read a DWORD setting.  Never fails: every way the value can be     */
        /* missing or wrong -- no key, no value, a value of another kind, a   */
        /* read refused -- gives the caller's default, so a caller needs no   */
        /* error path and a damaged setting can never stop the program.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sName    : the value name.                                     */
        /*     iDefault : what to return when it is missing or not a DWORD.   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : the stored value, or iDefault.                           */
        /*--------------------------------------------------------------------*/
        internal static int SET_ReadInt(string sName, int iDefault)
        {
            object objValue;
            int    iResult;

            /* The default until a stored value replaces it. */
            iResult = iDefault;

            try
            {   /* Everything that touches the registry is in here, so any      */
                /* failure on the way -- opening the key or reading the value   */
                /* -- lands in the catch below and counts as a miss.  The key   */
                /* is opened read-only (OpenSubKey), so a read never creates    */
                /* it.                                                          */
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SET_KEY_PATH))
                {   /* The key opened, or is null when it is absent (a   */
                    /* first run); the using closes it on the way out    */
                    /* either way.                                       */
                    if (key != null)
                    {   /* The key exists; look for the value. */
                        objValue = key.GetValue(sName);

                        /* A DWORD comes back boxed as int (SET_WriteInt      */
                        /* stores the kind by name).  Anything else -- a      */
                        /* missing value (null), a value of another kind --   */
                        /* leaves the default rather than being converted.    */
                        if (objValue is int)
                        {   /* Present and a DWORD. */
                            iResult = (int)objValue;
                        }
                    }
                }
            }
            catch (Exception)
            {   /* Unreadable: fall back to the default.  Every exception is a   */
                /* miss, since no setting is worth an error.  The reset is       */
                /* defensive: the only assignment in the try is its last act.    */
                iResult = iDefault;
            }

            /* The stored value, or the default. */
            return(iResult);
        }

        /*--------------------------------------------------------------------*/
        /* SET_ReadString:                                                    */
        /*                                                                    */
        /* Read a string setting.  Never fails, as SET_ReadInt: anything      */
        /* missing, of another kind or unreadable gives the caller's default. */
        /* A stored empty string is a stored value: it comes back as "", not  */
        /* as the default, so a caller that means "unset" by "" must say so.  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sName    : the value name.                                     */
        /*     sDefault : what to return when it is missing or not a string.  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the stored value, or sDefault.                        */
        /*--------------------------------------------------------------------*/
        internal static string SET_ReadString(string sName, string sDefault)
        {
            object objValue;
            string sResult;

            /* The default until a stored value replaces it. */
            sResult = sDefault;

            try
            {   /* Everything that touches the registry is in here, so any   */
                /* failure lands in the catch below and counts as a miss.    */
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SET_KEY_PATH))
                {   /* The key opened, or is null when it is absent (a   */
                    /* first run); the using closes it on the way out    */
                    /* either way.                                       */
                    if (key != null)
                    {   /* The key exists; look for the value. */
                        objValue = key.GetValue(sName);

                        /* Only a string value is taken; a missing value       */
                        /* (null) or one of another kind leaves the default.   */
                        if (objValue is string)
                        {   /* Present and a string. */
                            sResult = (string)objValue;
                        }
                    }
                }
            }
            catch (Exception)
            {   /* Unreadable: fall back to the default (every exception is a   */
                /* miss, as in SET_ReadInt).                                    */
                sResult = sDefault;
            }

            /* The stored value, or the default. */
            return(sResult);
        }

        /*--------------------------------------------------------------------*/
        /* SET_WriteInt:                                                      */
        /*                                                                    */
        /* Store a DWORD setting.  A failure is ignored: a setting that could */
        /* not be remembered costs the user a default next time, never an     */
        /* error now.                                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sName  : the value name.                                       */
        /*     iValue : the value to store.                                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the value is stored when the registry allows it.        */
        /*--------------------------------------------------------------------*/
        internal static void SET_WriteInt(string sName, int iValue)
        {
            try
            {   /* Open the key for writing, creating it on the first write   */
                /* (CreateSubKey creates it, or opens the existing one, for   */
                /* writing); any failure, here or in the store below, lands   */
                /* in the catch and is ignored.                               */
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SET_KEY_PATH))
                {   /* Store it as a DWORD -- the kind named, so SET_ReadInt's   */
                    /* "is int" test finds it; the using closes the key.         */
                    key.SetValue(sName, iValue, RegistryValueKind.DWord);
                }
            }
            catch (Exception)
            {   /* Not stored; the default applies next time.  Nothing to    */
                /* undo: a failed write changes nothing, and a key created   */
                /* before the failure is harmless and is reused next time.   */
            }
        }

        /*---------------------------------------------------------------------*/
        /* SET_WriteString:                                                    */
        /*                                                                     */
        /* Store a string setting.  A failure is ignored, as for SET_WriteInt. */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     sName  : the value name.                                        */
        /*     sValue : the value to store.                                    */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : the value is stored when the registry allows it.         */
        /*---------------------------------------------------------------------*/
        internal static void SET_WriteString(string sName, string sValue)
        {
            try
            {   /* Open the key for writing, creating it on the first write;   */
                /* any failure lands in the catch and is ignored.              */
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SET_KEY_PATH))
                {   /* Store it as a string -- the kind SET_ReadString's test   */
                    /* takes; the using closes the key.                         */
                    key.SetValue(sName, sValue, RegistryValueKind.String);
                }
            }
            catch (Exception)
            {   /* Not stored; the default applies next time (nothing to   */
                /* undo, as in SET_WriteInt).                              */
            }
        }
    }
}
