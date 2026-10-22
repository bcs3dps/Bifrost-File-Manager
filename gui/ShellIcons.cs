/*----------------------------------------------------------------------------*/
/* ShellIcons.cs                                                              */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The icons and type names the Windows shell itself shows for files, folders */
/* and drives -- the ones Explorer shows -- for a list of Windows items; and  */
/* the shell's own Properties window for one of them (SHI_ShowProperties).    */
/*                                                                            */
/* HOW IT WORKS.  The shell keeps every icon it has ever shown in its         */
/* process-wide "system image lists" -- 16, 32, 48 and 256 pixels -- and the  */
/* SAME index means the same icon in all of them.  SHGetFileInfo answers, for */
/* a path, the index of its icon and the name of its type.  This file relies  */
/* on that shared index (one answer serves every size); the pixel sizes are   */
/* those at 100 % display scaling, and the code asks each list its real size  */
/* rather than trusting them.                                                 */
/*                                                                            */
/* Those lists come only in the shell's fixed sizes, and the Windows pane's   */
/* icons follow View > Text Size, so the pane does NOT draw from them.  This  */
/* file keeps two image lists of its own, small and large, at whatever sizes  */
/* it was last given (SHI_SetIconSizes), and each system icon the pane needs  */
/* is copied into them ONCE, the first time it is asked for, scaled from the  */
/* shell's list nearest above the size wanted (SHI_Map).  Every index this    */
/* file hands out is an index into ITS lists.  When the sizes change, every   */
/* picture is drawn again in the same order, so an index a row already holds  */
/* stays right.  A picture that is not the shell's -- the ".." row's way up   */
/* -- is added the same way (SHI_AddPicture), with the function that draws it */
/* at a given size, so it is redrawn with the rest.                           */
/*                                                                            */
/* WHAT IS CACHED.  Asking the shell about a file by its real path makes it   */
/* open the file's folder entry, which is slow across thousands of rows (as   */
/* found when this was written: a listing stalled for seconds).               */
/* For most files the answer depends only on the extension, so those are      */
/* asked ONCE per extension, by extension alone, and remembered.  The few     */
/* kinds whose icon is the file's own (a program, an icon, a shortcut) and    */
/* every folder (a folder can carry a custom icon) are asked by real path     */
/* each time.                                                                 */
/*                                                                            */
/* Those real-path questions are the slow ones (the shell opens the item;     */
/* for a large installer that is a long read), so a caller filling a list     */
/* does it in TWO STEPS: SHI_LookupQuick for every row, which never touches   */
/* an item and says which rows' answers are only stand-ins, then SHI_Lookup   */
/* for those rows, a few at a time, once the list is on screen.               */
/*                                                                            */
/* Everything here must be called from the UI thread: the cache and the two   */
/* lists are not guarded, and only the UI thread (an STA thread, Program.cs)  */
/* has ever been used to ask the shell.  The one caller is MainForm.cs, and   */
/* only its Windows pane (Main_lstHost) draws from these lists -- which is    */
/* what makes SHI_SetIconSizes safe to dispose the old ones once that pane    */
/* has moved; a second list view would need the same handling.                */
/*                                                                            */
/* This file depends on nothing else in the program.                          */
/*----------------------------------------------------------------------------*/

/* The framework: the base types, the answer cache and the picture record,   */
/* GDI+'s pictures and its resizing settings, P/Invoke and COM interop for   */
/* the shell, and the image lists and list view the pictures go to.  The     */
/* pixel format and file-path calls are named in full where used.            */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BifrostFM
{
    internal static class SHI_ShellIcons
    {
        /* SHGetFileInfo flags -- documentation table.  The values are the      */
        /* Windows SDK's (shellapi.h, SHGFI_*), which is not in this tree;      */
        /* every constant in the tables below is likewise copied from the SDK   */
        /* header named on it, so a wrong one would show as a call the shell    */
        /* answers differently, not as a build error.                           */
        private const uint SHI_SHGFI_LARGEICON         = 0x00000000;   // answer for the LARGE system image list (the absence of SMALLICON); not used -- every question here names the small list
        private const uint SHI_SHGFI_SMALLICON         = 0x00000001;   // answer for the SMALL system image list
        private const uint SHI_SHGFI_PIDL              = 0x00000008;   // the "path" argument is not text but an item identifier list (how a virtual folder such as This PC is named)
        private const uint SHI_SHGFI_USEFILEATTRIBUTES = 0x00000010;   // do not touch the disk: judge by the name and the attributes passed in
        private const uint SHI_SHGFI_ICON              = 0x00000100;   // hand back an icon HANDLE (the caller destroys it)
        private const uint SHI_SHGFI_TYPENAME          = 0x00000400;   // fill in the type's name ("Text Document")
        private const uint SHI_SHGFI_SYSICONINDEX      = 0x00004000;   // fill in the icon's index; the RETURN VALUE is then the system image list

        /* File attributes passed with SHI_SHGFI_USEFILEATTRIBUTES -- documentation table (winnt.h, FILE_ATTRIBUTE_*). */
        private const uint SHI_ATTRIBUTE_DIRECTORY     = 0x00000010;   // "treat the name as a folder's"
        private const uint SHI_ATTRIBUTE_NORMAL        = 0x00000080;   // "treat the name as an ordinary file's"

        /* The shell's system image lists, by SHGetImageList's numbers, and   */
        /* the size of each -- documentation table (shellapi.h, SHIL_*).      */
        /* Same index, same icon, in every one of them.  Number 3, the        */
        /* "system small" list, is not used.                                  */
        private const int  SHI_SHIL_LARGE              = 0;                                                                        // the large list: 32 pixels at 100 % scaling
        private const int  SHI_SHIL_SMALL              = 1;                                                                        // the small list: 16 pixels
        private const int  SHI_SHIL_EXTRALARGE         = 2;                                                                        // the extra-large list: 48 pixels
        private const int  SHI_SHIL_JUMBO              = 4;                                                                        // the jumbo list: 256 pixels (Vista and later)
        private static readonly int[] SHI_aiListIds    = { SHI_SHIL_SMALL, SHI_SHIL_LARGE, SHI_SHIL_EXTRALARGE, SHI_SHIL_JUMBO };  // the lists, smallest first: a picture is scaled from the first whose size (asked of the list itself) reaches the size wanted; the jumbo list is listed but skipped (SHI_SystemPicture says why)

        /* The image-list interface's values (commctrl.h ILD_*, the IID from   */
        /* commoncontrols.h), and the fixed text sizes inside SHFILEINFOW      */
        /* (shellapi.h), which SHI_FileInfo must match exactly.                */
        private const int  SHI_ILD_TRANSPARENT         = 0x00000001;                                         // IImageList.GetIcon: keep the icon's transparency
        private static readonly Guid SHI_IID_IIMAGELIST = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");  // IImageList, the interface SHGetImageList hands a list back as; must equal the Guid on SHI_IImageList below

        private const int  SHI_NAME_CHARS              = 260;          // SHFILEINFO.szDisplayName, characters (MAX_PATH)
        private const int  SHI_TYPE_CHARS              = 80;           // SHFILEINFO.szTypeName, characters

        /* The extensions whose icon comes from the file itself, so that one    */
        /* answer cannot stand for every file of the kind: a program, an icon   */
        /* or cursor file, a shortcut (it shows its target's icon), a screen    */
        /* saver, an installer, a ClickOnce link.  Lower case, with the dot,    */
        /* as SHI_Lookup compares them.  Whether the list is complete is not    */
        /* established; a kind missing from it shows its extension's general    */
        /* icon, which is cosmetic only.                                        */
        private static readonly string[] SHI_asOwnIconExtensions = { ".exe", ".ico", ".cur", ".ani", ".lnk", ".url", ".scr", ".msi", ".appref-ms" };

        /*--------------------------------------------------------------------*/
        /* SHI_FileInfo -- SHFILEINFOW, what SHGetFileInfo fills in.  The     */
        /* fields are the API's, in its order and at its sizes (the two text  */
        /* buffers fixed-length, Unicode): the shell writes into this memory  */
        /* by position, so a field moved or resized here would fill the wrong */
        /* ones.  Its size is passed with every call.                         */
        /*--------------------------------------------------------------------*/
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHI_FileInfo
        {
            public IntPtr hIcon;             // an icon handle, filled only when SHI_SHGFI_ICON is asked for (SHI_SmallPicture, SHI_ThisPcPicture), which then destroy it; zero otherwise
            public int    iIcon;             // the icon's index in the system image lists
            public uint   uiAttributes;      // shell attributes; not asked for here

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = SHI_NAME_CHARS)]
            public string sDisplayName;      // the display name; not asked for here

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = SHI_TYPE_CHARS)]
            public string sTypeName;         // the type's name, when SHI_SHGFI_TYPENAME was given
        }

        /*--------------------------------------------------------------------*/
        /* SHI_Answer -- one remembered answer: an icon index and a type      */
        /* name, kept per extension (SHI_dictByExtension) and once for "a     */
        /* folder" (SHI_answerFolder).  The index is the SYSTEM one, turned   */
        /* into this file's by SHI_Map each time it is handed out.            */
        /*--------------------------------------------------------------------*/
        private struct SHI_Answer
        {
            public int    iIcon;             // the icon's index in the system image lists
            public string sTypeName;         // the type's name ("" when the shell gave none)
        }

        /*--------------------------------------------------------------------*/
        /* SHI_PictureMaker -- draws a picture that is not the shell's at a   */
        /* given size, for SHI_AddPicture; called again whenever the sizes    */
        /* change.  It returns a new bitmap the caller of it then owns (here, */
        /* SHI_AddDrawn, which disposes it once the list has copied it).      */
        /* MainForm.cs's maker draws the ".." picture with EIC_NewPicture.    */
        /*--------------------------------------------------------------------*/
        internal delegate Bitmap SHI_PictureMaker(Size sizPicture);

        /*---------------------------------------------------------------------*/
        /* SHI_Source -- where one picture of this file's lists came from,     */
        /* so it can be drawn again at a new size: a system icon's index, or   */
        /* the maker of a picture of the caller's.  Recording the source,      */
        /* rather than keeping the pictures, is what lets a size change redraw */
        /* every picture sharply at the new size instead of stretching the     */
        /* old ones.                                                           */
        /*---------------------------------------------------------------------*/
        private struct SHI_Source
        {
            public int              iSystem;     // the icon's index in the system image lists; -1 for a picture of the caller's
            public SHI_PictureMaker pfnMaker;    // the caller's picture's maker; null for a system icon
        }

        /*--------------------------------------------------------------------*/
        /* SHI_IImageList -- the shell's image-list interface, as far as its  */
        /* GetIconSize.  Only GetIcon and GetIconSize are called; the methods */
        /* before them are declared so each sits in its own place in the      */
        /* interface's table, which is how a COM call finds its method -- a   */
        /* missing or reordered line here calls the WRONG method.  The names  */
        /* carry this file's prefix (COM finds a method by its place, not its */
        /* name), so none repeats a name used elsewhere in the program.  The  */
        /* order is the one Windows' commoncontrols.h declares (not in this   */
        /* tree); the two methods called working is the only check made of    */
        /* it here.                                                           */
        /* Asked through this interface, not through comctl32's ImageList_*   */
        /* calls: this program has no manifest, so those would load the old   */
        /* comctl32, which -- as found when this was written; not             */
        /* established here -- cannot read the lists the shell's newer one    */
        /* made.                                                              */
        /*--------------------------------------------------------------------*/
        [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface SHI_IImageList
        {
            [PreserveSig] int SHII_Add(IntPtr hbmImage, IntPtr hbmMask, out int iIndex);                                     // IImageList::Add -- not used
            [PreserveSig] int SHII_ReplaceIcon(int iIndex, IntPtr hIcon, out int iNew);                                      // IImageList::ReplaceIcon -- not used
            [PreserveSig] int SHII_SetOverlayImage(int iImage, int iOverlay);                                                // IImageList::SetOverlayImage -- not used
            [PreserveSig] int SHII_Replace(int iIndex, IntPtr hbmImage, IntPtr hbmMask);                                     // IImageList::Replace -- not used
            [PreserveSig] int SHII_AddMasked(IntPtr hbmImage, int iMaskColour, out int iIndex);                              // IImageList::AddMasked -- not used
            [PreserveSig] int SHII_Draw(IntPtr pParams);                                                                     // IImageList::Draw -- not used
            [PreserveSig] int SHII_Remove(int iIndex);                                                                       // IImageList::Remove -- not used
            [PreserveSig] int SHII_GetIcon(int iIndex, int iFlags, out IntPtr hIcon);                                        // IImageList::GetIcon -- USED: a new icon handle for one picture; the caller destroys it
            [PreserveSig] int SHII_GetImageInfo(int iIndex, IntPtr pInfo);                                                   // IImageList::GetImageInfo -- not used
            [PreserveSig] int SHII_Copy(int iDest, IntPtr pSource, int iSource, int iFlags);                                 // IImageList::Copy -- not used
            [PreserveSig] int SHII_Merge(int i1, IntPtr pList2, int i2, int dx, int dy, ref Guid riid, out IntPtr pMerged);  // IImageList::Merge -- not used
            [PreserveSig] int SHII_Clone(ref Guid riid, out IntPtr pClone);                                                  // IImageList::Clone -- not used
            [PreserveSig] int SHII_GetImageRect(int iIndex, IntPtr pRect);                                                   // IImageList::GetImageRect -- not used
            [PreserveSig] int SHII_GetIconSize(out int iWidth, out int iHeight);                                             // IImageList::GetIconSize -- USED: the list's picture size, pixels
        }

        /* State.  All of it belongs to the UI thread, starts empty (null or   */
        /* zero) and is filled on first use; none of it is ever released --    */
        /* the lists and caches live as long as the program, which keeps one   */
        /* Windows pane for its whole life.                                    */
        private static Dictionary<string, SHI_Answer> SHI_dictByExtension;  // remembered answers, keyed by lower-case extension ("" for none); their indexes are SYSTEM indexes
        private static SHI_Answer                     SHI_answerFolder;     // the answer for "a folder", by attributes alone (a system index)
        private static bool                           SHI_bFolderKnown;     // true once SHI_answerFolder has been asked for
        private static ImageList                      SHI_imlSmall;         // this file's small pictures (details, list, small-icon views); null until first needed
        private static ImageList                      SHI_imlLarge;         // this file's large pictures (large-icon and tile views), same indexes
        private static Size                           SHI_sizSmall;         // the small pictures' size, pixels
        private static Size                           SHI_sizLarge;         // the large pictures' size, pixels
        private static List<SHI_Source>               SHI_lstSources;       // where picture N of both lists came from, N in order
        private static Dictionary<int, int>           SHI_dictMapped;       // system index -> this file's index, for every system icon copied so far
        private static SHI_IImageList[]               SHI_aSystemLists;     // the shell's lists, in SHI_aiListIds' order; an entry is null until asked for (or when the shell would not give it)
        private static int[]                          SHI_aiSystemPixels;   // each one's measured size, pixels; -1 until asked for, 0 when unavailable (SHI_LoadSystemList)

        /*--------------------------------------------------------------------*/
        /* SHGetFileInfoW:                                                    */
        /*                                                                    */
        /* shell32: facts about a file, folder or drive as the shell shows    */
        /* it.  With SHI_SHGFI_SYSICONINDEX the return value is the handle of */
        /* the system image list the index refers to (small or large, by the  */
        /* flag), or zero when the shell could not answer.  Asked for an icon */
        /* handle instead (SHI_SHGFI_ICON), it returns nonzero on success.    */
        /* Imported under its Windows name; the second import below is the    */
        /* same function with another first argument.                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sPath        : the item's path; with USEFILEATTRIBUTES it need */
        /*                    not exist and only its name is looked at.       */
        /*     uiAttributes : file attributes, used only with                 */
        /*                    USEFILEATTRIBUTES.                              */
        /*     info         : receives the answer.                            */
        /*     uiInfoBytes  : the size of info, bytes.                        */
        /*     uiFlags      : SHI_SHGFI_* flags.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     IntPtr : the system image list, or zero on failure.            */
        /*--------------------------------------------------------------------*/
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfoW(string sPath, uint uiAttributes, ref SHI_FileInfo info, uint uiInfoBytes, uint uiFlags);

        /*--------------------------------------------------------------------*/
        /* SHI_GetFileInfoByIdList:                                           */
        /*                                                                    */
        /* shell32's SHGetFileInfoW again, imported under a second name for   */
        /* its other calling form: with SHI_SHGFI_PIDL the first argument is  */
        /* an item identifier list instead of a path.  That is the only form  */
        /* that answers for a VIRTUAL folder -- This PC has no path, and the  */
        /* text form given its "::{...}" shell name answers nothing (as found */
        /* when the Places menu's This PC item first showed no icon).  A      */
        /* second import because the first argument has another type here     */
        /* (an IntPtr, not a string), and this program never gives two        */
        /* functions one name.                                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pIdList      : the item's identifier list.                     */
        /*     uiAttributes : unused in this form; 0.                         */
        /*     info         : receives the answer.                            */
        /*     uiInfoBytes  : the size of info, bytes.                        */
        /*     uiFlags      : SHI_SHGFI_* flags, SHI_SHGFI_PIDL among them.   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     IntPtr : nonzero on success, zero on failure.                  */
        /*--------------------------------------------------------------------*/
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
        private static extern IntPtr SHI_GetFileInfoByIdList(IntPtr pIdList, uint uiAttributes, ref SHI_FileInfo info, uint uiInfoBytes, uint uiFlags);

        /*--------------------------------------------------------------------*/
        /* SHGetSpecialFolderLocation:                                        */
        /*                                                                    */
        /* shell32: the item identifier list of one of the shell's special    */
        /* folders, real or virtual.  The list is the shell's memory and the  */
        /* caller frees it (Marshal.FreeCoTaskMem).  It hands back the        */
        /* identifier list the icon question needs; why this older CSIDL      */
        /* call was chosen over a known-folder form is not recorded.          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hOwner  : a window for any prompt; zero for none.              */
        /*     iFolder : which folder, a CSIDL value (SHI_CSIDL_DRIVES).      */
        /*     pIdList : receives the list.                                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : 0 on success, otherwise an error code.                   */
        /*--------------------------------------------------------------------*/
        [DllImport("shell32.dll")]
        private static extern int SHGetSpecialFolderLocation(IntPtr hOwner, int iFolder, out IntPtr pIdList);

        private const int SHI_CSIDL_DRIVES = 0x0011;   // SHGetSpecialFolderLocation: the This PC (My Computer) virtual folder (shlobj.h, CSIDL_DRIVES)

        /*--------------------------------------------------------------------*/
        /* SHGetImageList:                                                    */
        /*                                                                    */
        /* shell32: one of the shell's system image lists, by size (SHIL_*),  */
        /* handed back through the interface asked for.  The list is the      */
        /* shell's and lives as long as the process; the interface reference  */
        /* is ours and is simply kept (SHI_LoadSystemList asks for each list  */
        /* once).  The out argument is marshalled straight to the declared    */
        /* interface, so riid must name that same interface.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iImageList : which list, a SHI_SHIL_* value.                   */
        /*     riid       : the interface wanted (IImageList).                */
        /*     ppv        : receives the list.                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : 0 (S_OK) on success, otherwise an HRESULT.               */
        /*--------------------------------------------------------------------*/
        [DllImport("shell32.dll")]
        private static extern int SHGetImageList(int iImageList, ref Guid riid, out SHI_IImageList ppv);

        /*--------------------------------------------------------------------*/
        /* SHI_ExecuteInfo -- SHELLEXECUTEINFOW, what ShellExecuteEx is told. */
        /* Only the members marked "used" are set; the rest stay zero.  As    */
        /* with SHI_FileInfo, the members are the API's, in its order and at  */
        /* its sizes (shellapi.h): the shell reads them by position.          */
        /*--------------------------------------------------------------------*/
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHI_ExecuteInfo
        {
            public int    iBytes;            // used: the size of this structure, bytes
            public uint   uiMask;            // used: SHI_SEE_MASK_* flags
            public IntPtr hWndOwner;         // used: the window any message the shell shows belongs to
            public string sVerb;             // used: what to do with the item ("properties")
            public string sFile;             // used: the item's path
            public string sParameters;       // not used
            public string sDirectory;        // not used
            public int    iShow;             // used: SHI_SW_SHOW
            public IntPtr hInstance;         // filled by the shell; not read
            public IntPtr pIdList;           // not used
            public string sClass;            // not used
            public IntPtr hKeyClass;         // not used
            public uint   uiHotKey;          // not used
            public IntPtr hIcon;             // not used
            public IntPtr hProcess;          // not used
        }

        /* The two values SHI_ShowProperties passes (shellapi.h, winuser.h). */
        private const uint SHI_SEE_MASK_INVOKEIDLIST = 0x0000000C;   // run the verb through the item's shell context menu, which is where "properties" lives
        private const int  SHI_SW_SHOW               = 5;            // show the window the verb opens, activated

        /*--------------------------------------------------------------------*/
        /* ShellExecuteExW:                                                   */
        /*                                                                    */
        /* shell32: perform a shell verb on an item -- here "properties",     */
        /* which opens the same Properties window Explorer opens.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     info : what to do and to what.                                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : nonzero when the verb was started, zero when the shell   */
        /*           refused (no such item, no such verb).                    */
        /*--------------------------------------------------------------------*/
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int ShellExecuteExW(ref SHI_ExecuteInfo info);

        /*--------------------------------------------------------------------*/
        /* SHI_ShowProperties:                                                */
        /*                                                                    */
        /* Open Windows' own Properties window for a file, folder or drive -- */
        /* the one Explorer's right-click Properties opens.  The window       */
        /* belongs to the shell and stays open on its own; this returns as    */
        /* soon as it has been asked for.                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hWndOwner : the caller's window (for any message the shell     */
        /*                 has to show).                                      */
        /*     sPath     : the item's full path.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the shell accepted the request.               */
        /*--------------------------------------------------------------------*/
        internal static bool SHI_ShowProperties(IntPtr hWndOwner, string sPath)
        {
            SHI_ExecuteInfo info;

            /* The request: a zeroed structure, so every member not set here   */
            /* is zero or null; its own size, which the API requires; the      */
            /* context-menu route the "properties" verb needs; the owner       */
            /* window; and the item, by its full path.                         */
            info           = new SHI_ExecuteInfo();
            info.iBytes    = Marshal.SizeOf(typeof(SHI_ExecuteInfo));
            info.uiMask    = SHI_SEE_MASK_INVOKEIDLIST;
            info.hWndOwner = hWndOwner;
            info.sVerb     = "properties";
            info.sFile     = sPath;
            info.iShow     = SHI_SW_SHOW;

            /* Nonzero means the shell took the request; the window then      */
            /* opens on its own.  A refusal (the item gone, say) comes back   */
            /* as false for the caller to report -- MainForm.cs puts it in    */
            /* the status line.                                               */
            return(ShellExecuteExW(ref info) != 0);
        }

        /*--------------------------------------------------------------------*/
        /* SHGetKnownFolderPath:                                              */
        /*                                                                    */
        /* shell32: where one of the user's "known folders" is.  The path     */
        /* comes back in memory the shell allocated, which the caller frees   */
        /* with CoTaskMemFree (Marshal.FreeCoTaskMem).  Its one user is       */
        /* SHI_DownloadsFolder, which frees the path only on success; whether */
        /* the shell leaves anything to free after a failure is not           */
        /* established here.                                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     guidFolder : which folder (a FOLDERID_* value).                */
        /*     uiFlags    : 0 for the plain lookup.                           */
        /*     hToken     : zero for the current user.                        */
        /*     pPath      : receives the path's address.                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : 0 (S_OK) on success, otherwise an HRESULT.               */
        /*--------------------------------------------------------------------*/
        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath(ref Guid guidFolder, uint uiFlags, IntPtr hToken, out IntPtr pPath);

        /*--------------------------------------------------------------------*/
        /* DestroyIcon:                                                       */
        /*                                                                    */
        /* user32: release an icon handle the shell handed out.  Every icon   */
        /* handle this file receives -- from SHGetFileInfo's icon form or     */
        /* from IImageList.GetIcon, each a new handle made for that call --   */
        /* goes back through here once copied, or it would stay allocated     */
        /* for the life of the program.  Its result is not looked at: a       */
        /* failed release leaves nothing the caller could do.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hIcon : the icon.                                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : nonzero on success.                                      */
        /*--------------------------------------------------------------------*/
        [DllImport("user32.dll")]
        private static extern int DestroyIcon(IntPtr hIcon);

        /*--------------------------------------------------------------------*/
        /* SHI_SmallPicture:                                                  */
        /*                                                                    */
        /* The shell's small icon for an item, as a picture of its own -- for */
        /* a place that is not a list row, such as a menu item, which cannot  */
        /* draw from the system image lists.  The path must be a REAL one, a  */
        /* folder or a drive: the shell does not answer this call for a       */
        /* virtual folder's "::{...}" name (SHI_ThisPcPicture is for that).   */
        /* Its caller is MainForm.cs's Places menu.  The copy-and-release     */
        /* below is repeated in SHI_ThisPcPicture and SHI_SystemPicture       */
        /* rather than shared, so each reads whole where it is used.          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sPath : the item's path.                                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     System.Drawing.Bitmap : the icon, owned by the caller; null    */
        /*                             when the shell has none to give.       */
        /*--------------------------------------------------------------------*/
        internal static System.Drawing.Bitmap SHI_SmallPicture(string sPath)
        {
            SHI_FileInfo          info;
            System.Drawing.Bitmap bmpIcon;

            /* Ask by the real path -- a drive or folder can have an icon of   */
            /* its own, so its name alone would not do -- for an icon HANDLE   */
            /* at the small size (a menu item's), rather than a list index,    */
            /* since a menu cannot draw from the shell's lists.                */
            info = new SHI_FileInfo();

            if (SHGetFileInfoW(sPath, 0, ref info, (uint)Marshal.SizeOf(typeof(SHI_FileInfo)),
                               SHI_SHGFI_ICON | SHI_SHGFI_SMALLICON) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            {   /* The shell could not answer for this item (it does not say   */
                /* why): no picture, and no handle to give back, so the menu   */
                /* item simply shows none.                                     */
                return(null);
            }

            /* Copy the icon into a bitmap of our own, then give the handle back. */
            using (System.Drawing.Icon icoShell = System.Drawing.Icon.FromHandle(info.hIcon))
            {   /* Copy the shell's icon into a bitmap the program owns: the     */
                /* menu keeps its picture for as long as it is shown, long       */
                /* after the handle has to be returned.  The Icon wrapper does   */
                /* not own the handle, so disposing it here frees nothing of     */
                /* the shell's; the handle is destroyed just below.              */
                bmpIcon = icoShell.ToBitmap();
            }

            /* The handle is the shell's gift to this call and leaks unless   */
            /* destroyed; the bitmap above no longer needs it.                */
            DestroyIcon(info.hIcon);

            /* The caller's picture. */
            return(bmpIcon);
        }

        /*--------------------------------------------------------------------*/
        /* SHI_ThisPcPicture:                                                 */
        /*                                                                    */
        /* The small icon Windows itself shows for This PC, as a picture of   */
        /* its own, for the Places menu.  This PC is a virtual folder with no */
        /* path, so the shell is asked by its item identifier list: asking by */
        /* the folder's "::{...}" name as if it were a path gets no answer.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     System.Drawing.Bitmap : the icon, owned by the caller; null    */
        /*                             when the shell has none to give.       */
        /*--------------------------------------------------------------------*/
        internal static System.Drawing.Bitmap SHI_ThisPcPicture()
        {
            SHI_FileInfo          info;
            System.Drawing.Bitmap bmpIcon;
            IntPtr                pIdList;
            IntPtr                hAnswer;

            /* Step one: This PC's item identifier list. */
            if (SHGetSpecialFolderLocation(IntPtr.Zero, SHI_CSIDL_DRIVES, out pIdList) != 0 || pIdList == IntPtr.Zero)
            {   /* The shell would not name the folder: no picture, and no   */
                /* list to free.                                             */
                return(null);
            }

            /* Step two: the small icon for that list, asked as an icon         */
            /* handle as in SHI_SmallPicture.  The list is the shell's memory   */
            /* and is freed straight after the question, whether or not the     */
            /* shell answered, so no exit below has it to free.                 */
            info    = new SHI_FileInfo();
            hAnswer = SHI_GetFileInfoByIdList(pIdList, 0, ref info, (uint)Marshal.SizeOf(typeof(SHI_FileInfo)),
                                              SHI_SHGFI_PIDL | SHI_SHGFI_ICON | SHI_SHGFI_SMALLICON);
            Marshal.FreeCoTaskMem(pIdList);

            if (hAnswer == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            {   /* The shell could not answer for this item: no picture; the   */
                /* list was already freed above.                               */
                return(null);
            }

            /* Copy the icon into a bitmap of our own, then give the handle back. */
            using (System.Drawing.Icon icoShell = System.Drawing.Icon.FromHandle(info.hIcon))
            {   /* Copy the shell's icon into a bitmap the menu can keep, as   */
                /* in SHI_SmallPicture; the wrapper does not own the handle,   */
                /* which is destroyed just below.                              */
                bmpIcon = icoShell.ToBitmap();
            }

            /* The shell's icon handle, destroyed once copied (it would leak). */
            DestroyIcon(info.hIcon);

            /* The caller's picture. */
            return(bmpIcon);
        }

        /*--------------------------------------------------------------------*/
        /* SHI_DownloadsFolder:                                               */
        /*                                                                    */
        /* The current user's Downloads folder.  The framework this program   */
        /* targets has no name for it among its special folders, and it can   */
        /* have been moved off the profile, so the shell is asked by the      */
        /* folder's own identifier; if the shell cannot say, the usual place  */
        /* under the profile is given instead.                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the folder's path (it may not exist).                 */
        /*--------------------------------------------------------------------*/
        internal static string SHI_DownloadsFolder()
        {
            Guid   guidDownloads;
            IntPtr pPath;
            string sPath;

            /* FOLDERID_Downloads, the identifier Windows publishes for the   */
            /* folder (knownfolders.h, not in this tree); and no path until   */
            /* the shell gives one.                                           */
            guidDownloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            sPath         = "";

            if (SHGetKnownFolderPath(ref guidDownloads, 0, IntPtr.Zero, out pPath) == 0)
            {   /* The shell answered: copy the path into a managed string      */
                /* FIRST, then free the shell's memory -- the other order       */
                /* would read freed memory.  No flags were passed, so this is   */
                /* the folder as the user has it, moved or not.                 */
                sPath = Marshal.PtrToStringUni(pPath);
                Marshal.FreeCoTaskMem(pPath);
            }

            if (sPath == null || sPath.Length == 0)
            {   /* No answer: the usual place, which may not be where the    */
                /* user's Downloads really is, or exist at all; what to do   */
                /* with a path that is not there is the caller's choice.     */
                sPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            }

            /* The Downloads folder: the shell's answer, or the usual place. */
            return(sPath);
        }

        /*--------------------------------------------------------------------*/
        /* SHI_Ask:                                                           */
        /*                                                                    */
        /* One question to the shell: the SYSTEM icon index and the type name */
        /* of an item.  The index is the shell's, the same in all its lists;  */
        /* callers outside this file get it translated (SHI_Map).  A failed   */
        /* question is not retried and not reported: the caller gets the      */
        /* "no answer" pair, and SHI_Lookup / SHI_LookupQuick remember it     */
        /* like any other answer.                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sPath        : the item's path, or just a name with            */
        /*                    bByNameOnly.                                    */
        /*     uiAttributes : SHI_ATTRIBUTE_* when bByNameOnly; else 0.       */
        /*     bByNameOnly  : true to judge by name and attributes without    */
        /*                    touching the disk.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     SHI_Answer : the index and type name; index 0 and "" when the  */
        /*                  shell could not answer.                           */
        /*--------------------------------------------------------------------*/
        private static SHI_Answer SHI_Ask(string sPath, uint uiAttributes, bool bByNameOnly)
        {
            SHI_FileInfo info;
            SHI_Answer   answer;
            IntPtr       hList;
            uint         uiFlags;

            /* The question: the icon's index and the type's name, for the   */
            /* small list.  The small-icon flag only names which list the    */
            /* shell answers for -- it has to name one, and the index is     */
            /* the same in every list.                                       */
            info    = new SHI_FileInfo();
            uiFlags = SHI_SHGFI_SYSICONINDEX | SHI_SHGFI_SMALLICON | SHI_SHGFI_TYPENAME;

            if (bByNameOnly)
            {   /* Do not open the item; the name and attributes decide. */
                uiFlags = uiFlags | SHI_SHGFI_USEFILEATTRIBUTES;
            }

            /* Asked.  With SYSICONINDEX the return value is the list the   */
            /* index belongs to; it is used only as the sign that the       */
            /* shell answered, zero being its "no answer".                  */
            hList = SHGetFileInfoW(sPath, uiAttributes, ref info, (uint)Marshal.SizeOf(typeof(SHI_FileInfo)), uiFlags);

            /* The "no answer" pair until the shell's answer replaces it.   */
            /* Index 0 is the first system icon, so a failed question       */
            /* still draws a picture -- whichever the shell keeps there.    */
            answer.iIcon     = 0;
            answer.sTypeName = "";

            if (hList != IntPtr.Zero)
            {   /* The shell answered: take its index, and its type name when   */
                /* one came back (an unregistered extension's can be empty).    */
                answer.iIcon = info.iIcon;

                if (info.sTypeName != null)
                {   /* A type name came back (it can be empty). */
                    answer.sTypeName = info.sTypeName;
                }
            }

            /* The index and the type name, or the "no answer" pair. */
            return(answer);
        }

        /*--------------------------------------------------------------------*/
        /* SHI_SetIconSizes:                                                  */
        /*                                                                    */
        /* Give a list view this file's pictures at the sizes it should show  */
        /* -- the small ones for the details, list and small-icon views, the  */
        /* large ones for large icons and tiles -- and make them the sizes    */
        /* every later picture is drawn at.  Called when the list is made and */
        /* whenever View > Text Size changes the size its icons follow.  A    */
        /* row's icon index stays right across a change of size.              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lstTarget : the list view (the Windows pane's list).           */
        /*     sizSmall  : the small pictures' size, pixels.                  */
        /*     sizLarge  : the large pictures' size, pixels.                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the list view shows this file's pictures at those       */
        /*            sizes.                                                  */
        /*--------------------------------------------------------------------*/
        internal static void SHI_SetIconSizes(ListView lstTarget, Size sizSmall, Size sizLarge)
        {
            ImageList imlOldSmall;
            ImageList imlOldLarge;

            if (SHI_imlSmall != null && SHI_sizSmall == sizSmall && SHI_sizLarge == sizLarge)
            {   /* Already these sizes: nothing is redrawn, and the lists are   */
                /* only handed to the list view, which may not have them yet    */
                /* (the lists can predate it -- SHI_EnsureLists).  Handing a    */
                /* list view the lists it already has changes nothing.          */
                lstTarget.SmallImageList = SHI_imlSmall;
                lstTarget.LargeImageList = SHI_imlLarge;
                return;
            }

            /* The lists in use now (null the first time), kept to be let    */
            /* go after the list view has moved -- SHI_BuildLists replaces   */
            /* the fields with new lists and redraws every picture.          */
            imlOldSmall = SHI_imlSmall;
            imlOldLarge = SHI_imlLarge;

            /* EVERY picture drawn again, in the same order, in two new     */
            /* lists at the new sizes: the index a row already holds then   */
            /* names the same icon at the new size, so no row has to be     */
            /* asked about again.                                           */
            SHI_BuildLists(sizSmall, sizLarge);

            /* The list view moves to the new lists first, BEFORE the old   */
            /* ones are disposed, so it never draws from a list that has    */
            /* gone ...                                                     */
            lstTarget.SmallImageList = SHI_imlSmall;
            lstTarget.LargeImageList = SHI_imlLarge;

            if (imlOldSmall != null)
            {   /* ... then the old ones are let go (the two are always made   */
                /* together, so one test covers both).                         */
                imlOldSmall.Dispose();
                imlOldLarge.Dispose();
            }
        }

        /*--------------------------------------------------------------------*/
        /* SHI_BuildLists:                                                    */
        /*                                                                    */
        /* Make this file's two lists at the given sizes and draw into them,  */
        /* in order, every picture recorded so far (none, the first time).    */
        /* The lists made before, if any, are NOT disposed here -- the caller */
        /* still has a list view drawing from them and lets them go once it   */
        /* has moved.                                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sizSmall : the small pictures' size, pixels.                   */
        /*     sizLarge : the large pictures' size, pixels.                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : SHI_imlSmall and SHI_imlLarge are new and complete.     */
        /*--------------------------------------------------------------------*/
        private static void SHI_BuildLists(Size sizSmall, Size sizLarge)
        {
            int i;

            if (SHI_lstSources == null)
            {   /* First use: nothing recorded yet.  The record of sources and   */
                /* the system-to-own index map are made once and outlive every   */
                /* rebuild -- they are what a rebuild redraws from.              */
                SHI_lstSources = new List<SHI_Source>();
                SHI_dictMapped = new Dictionary<int, int>();
            }

            /* The sizes first -- SHI_AddDrawn draws at these -- then the two   */
            /* empty lists at them.                                             */
            SHI_sizSmall = sizSmall;
            SHI_sizLarge = sizLarge;
            SHI_imlSmall = SHI_NewList(sizSmall);
            SHI_imlLarge = SHI_NewList(sizLarge);

            /* Every recorded picture, in its recorded order: SHI_AddDrawn   */
            /* returns the next free index, which therefore equals i, and    */
            /* does not record it again (its index is below the count).      */
            for (i = 0; i < SHI_lstSources.Count; i++)
            {   /* One recorded picture per iteration, drawn again at the new sizes, at the same index. */
                SHI_AddDrawn(SHI_lstSources[i]);
            }
        }

        /*--------------------------------------------------------------------*/
        /* SHI_NewList:                                                       */
        /*                                                                    */
        /* An empty 32-bit image list of one picture size, with its Windows   */
        /* list already made, so a picture added to it is copied at once.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sizPicture : the pictures' size, pixels.                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ImageList : the list, owned by this file.                      */
        /*--------------------------------------------------------------------*/
        private static ImageList SHI_NewList(Size sizPicture)
        {
            ImageList imlNew;
            IntPtr    hList;

            /* 32-bit, so the shell's icons keep their alpha edges; the size   */
            /* set before the Windows list is made below.                      */
            imlNew            = new ImageList();
            imlNew.ColorDepth = ColorDepth.Depth32Bit;
            imlNew.ImageSize  = sizPicture;

            /* Made now -- reading Handle is what makes it -- so every Add   */
            /* copies its bitmap at once (the value itself is not needed).   */
            /* Until an ImageList has made its Windows list it only keeps    */
            /* a reference to each bitmap added and copies them later, by    */
            /* which time the bitmaps SHI_AddDrawn adds would have been      */
            /* disposed.  That copying is the framework's behaviour as       */
            /* found when the code was written; its source is not in this    */
            /* tree.                                                         */
            hList = imlNew.Handle;

            /* The empty list. */
            return(imlNew);
        }

        /*--------------------------------------------------------------------*/
        /* SHI_EnsureLists:                                                   */
        /*                                                                    */
        /* Make sure the two lists exist before a picture is added to them.   */
        /* The caller normally sets the sizes first (SHI_SetIconSizes); if a  */
        /* picture is asked for before that, the lists are made at the        */
        /* system's own icon sizes, and the first SHI_SetIconSizes redraws    */
        /* them at the sizes it is given (or, when those are the same, just   */
        /* hands them over).  MainForm.cs does ask first: it adds the ".."    */
        /* picture while building its controls, before it sets the sizes.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the lists exist.                                        */
        /*--------------------------------------------------------------------*/
        private static void SHI_EnsureLists()
        {
            if (SHI_imlSmall == null)
            {   /* Not made yet: the system's sizes stand in.  The small list   */
                /* alone is tested: the two are only ever made together.        */
                SHI_BuildLists(SystemInformation.SmallIconSize, SystemInformation.IconSize);
            }
        }

        /*--------------------------------------------------------------------*/
        /* SHI_AddPicture:                                                    */
        /*                                                                    */
        /* Add a picture that is not the shell's -- the ".." row's way up --  */
        /* to this file's lists, drawn by the caller's maker at the current   */
        /* sizes, and drawn by it again whenever the sizes change.  Call it   */
        /* ONCE per picture and keep the index: every call adds another.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pfnMaker : draws the picture at a given size (a new bitmap,    */
        /*                which this file disposes once copied).              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : the picture's index in this file's lists.                */
        /*--------------------------------------------------------------------*/
        internal static int SHI_AddPicture(SHI_PictureMaker pfnMaker)
        {
            SHI_Source source;

            /* Lists to add to -- at the system's sizes if none were set   */
            /* yet; MainForm adds its ".." picture before it sets sizes.   */
            SHI_EnsureLists();

            /* A caller's picture: no system index, only its maker. */
            source.iSystem  = -1;
            source.pfnMaker = pfnMaker;

            /* Drawn into both lists and recorded; its index. */
            return(SHI_AddDrawn(source));
        }

        /*--------------------------------------------------------------------*/
        /* SHI_Map:                                                           */
        /*                                                                    */
        /* This file's index for a SYSTEM icon index.  Every index this file  */
        /* hands out passes through here, so the shell's index never reaches  */
        /* a list view.                                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iSystem : the icon's index in the shell's system image lists.  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : the icon's index in this file's lists.                   */
        /*--------------------------------------------------------------------*/
        private static int SHI_Map(int iSystem)
        {
            SHI_Source source;
            int        iMine;

            /* The lists, and with them the map (SHI_BuildLists makes both). */
            SHI_EnsureLists();

            if (SHI_dictMapped.TryGetValue(iSystem, out iMine))
            {   /* Copied before: the index it was given then, which a size   */
                /* change has not moved (the lists are redrawn in order).     */
                return(iMine);
            }

            /* A system icon not met before: copy it in and remember where.   */
            /* The map is what keeps each system icon to ONE picture here,    */
            /* however many rows show it.                                     */
            source.iSystem  = iSystem;
            source.pfnMaker = null;
            iMine           = SHI_AddDrawn(source);
            SHI_dictMapped.Add(iSystem, iMine);

            /* Its new index. */
            return(iMine);
        }

        /*--------------------------------------------------------------------*/
        /* SHI_AddDrawn:                                                      */
        /*                                                                    */
        /* Draw one picture at both current sizes, add it to both lists, and  */
        /* -- when it is not already recorded, as it is during a redraw --    */
        /* record where it came from.  The two lists therefore always hold    */
        /* the same pictures at the same indexes.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     source : where the picture comes from.                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : its index in both lists.                                 */
        /*--------------------------------------------------------------------*/
        private static int SHI_AddDrawn(SHI_Source source)
        {
            Bitmap bmpPicture;
            int    iIndex;

            /* The picture's index is the next free place -- the same in   */
            /* both lists, since every picture goes into both.             */
            iIndex = SHI_imlSmall.Images.Count;

            /* Each list copies the bitmap as it is added; ours goes at once. */
            bmpPicture = SHI_DrawSource(source, SHI_sizSmall);
            SHI_imlSmall.Images.Add(bmpPicture);
            bmpPicture.Dispose();

            /* The same picture, drawn afresh at the large size. */
            bmpPicture = SHI_DrawSource(source, SHI_sizLarge);
            SHI_imlLarge.Images.Add(bmpPicture);
            bmpPicture.Dispose();

            if (iIndex == SHI_lstSources.Count)
            {   /* A new picture, not a redraw of a recorded one: record it.   */
                /* During a rebuild the index is below the count (the record   */
                /* is complete and is being replayed), so nothing is added     */
                /* twice.                                                      */
                SHI_lstSources.Add(source);
            }

            /* Where it went. */
            return(iIndex);
        }

        /*--------------------------------------------------------------------*/
        /* SHI_DrawSource:                                                    */
        /*                                                                    */
        /* One recorded picture at one size: the caller's maker draws its     */
        /* own; a system icon is copied from the shell (SHI_SystemPicture).   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     source     : where the picture comes from.                     */
        /*     sizPicture : the size wanted, pixels.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Bitmap : a new picture of exactly that size; the caller owns   */
        /*              it.                                                   */
        /*--------------------------------------------------------------------*/
        private static Bitmap SHI_DrawSource(SHI_Source source, Size sizPicture)
        {
            if (source.pfnMaker != null)
            {   /* The caller's picture: its own maker draws it at this size,   */
                /* and the new bitmap is handed straight on.                    */
                return(source.pfnMaker(sizPicture));
            }

            /* A system icon, taken from the shell's lists and resized to   */
            /* this size (blank when the shell will not give it).           */
            return(SHI_SystemPicture(source.iSystem, sizPicture));
        }

        /*--------------------------------------------------------------------*/
        /* SHI_SystemPicture:                                                 */
        /*                                                                    */
        /* A system icon at any size, taken from the nearest of the shell's   */
        /* fixed-size lists.                                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iSystem    : the icon's system index.                          */
        /*     sizPicture : the size wanted, pixels.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Bitmap : a new picture of exactly that size -- transparent     */
        /*              when the shell would not give the icon; the caller    */
        /*              owns it.                                              */
        /*--------------------------------------------------------------------*/
        private static Bitmap SHI_SystemPicture(int iSystem, Size sizPicture)
        {
            Bitmap         bmpPicture;
            Bitmap         bmpIcon;
            SHI_IImageList listFrom;
            IntPtr         hIcon;
            int            iPixels;
            int            k;

            /* The picture to return, transparent until drawn on; no list   */
            /* chosen yet.  It is made first so every exit returns one of   */
            /* exactly the size wanted, which the lists require.            */
            bmpPicture = new Bitmap(sizPicture.Width, sizPicture.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            listFrom   = null;
            iPixels    = 0;

            /* The list to take the icon from: the SMALLEST whose pictures   */
            /* are at least as big as the size wanted, to be scaled DOWN     */
            /* -- an icon scaled up blurs, one scaled down keeps its         */
            /* edges, and taking the nearest one above keeps the scaling     */
            /* slight.  When no list reaches the size, the largest there     */
            /* is is kept, to be scaled up.  Only the height is compared:    */
            /* the shell's pictures and the sizes asked for are square.      */
            for (k = 0; k < SHI_aiListIds.Length; k++)
            {   /* One of the shell's lists per iteration, smallest first. */
                if (SHI_aiListIds[k] == SHI_SHIL_JUMBO)
                {   /* Not used: for most kinds of file the jumbo list holds   */
                    /* the 48-pixel icon centred in a 256-pixel square (as     */
                    /* found when this was written), which would shrink to a   */
                    /* dot.                                                    */
                    continue;
                }

                /* Asked for once; later passes find it (or its absence). */
                SHI_LoadSystemList(k);

                if (SHI_aSystemLists[k] == null)
                {   /* This one is not available: the search goes on to the   */
                    /* next larger, keeping whatever smaller one it has.      */
                    continue;
                }

                /* The best so far: big enough stops the search; until then   */
                /* each larger one replaces the last.                         */
                listFrom = SHI_aSystemLists[k];
                iPixels  = SHI_aiSystemPixels[k];

                if (iPixels >= sizPicture.Height)
                {   /* Big enough: scaled down (or not at all) from this one. */
                    break;
                }
            }

            /* The icon as a new handle, transparency kept; nonzero is a   */
            /* COM failure.                                                */
            if (listFrom == null || listFrom.SHII_GetIcon(iSystem, SHI_ILD_TRANSPARENT, out hIcon) != 0 || hIcon == IntPtr.Zero)
            {   /* No list, or no icon at that index: a blank picture keeps the   */
                /* two lists' indexes in step.  It stays in the lists at that     */
                /* index until the next size change redraws it; the icon is       */
                /* not asked for again meanwhile.                                 */
                return(bmpPicture);
            }

            /* Copy the icon into a bitmap of our own (its transparency kept),   */
            /* then give the handle back.                                        */
            using (Icon icoShell = Icon.FromHandle(hIcon))
            {   /* Copy the icon, at the list's own size, into a bitmap the     */
                /* resizing below can draw from; the wrapper does not own the   */
                /* handle, which is destroyed just below.                       */
                bmpIcon = icoShell.ToBitmap();
            }

            /* GetIcon made a new handle for this call: destroyed once copied. */
            DestroyIcon(hIcon);

            using (Graphics gfx = Graphics.FromImage(bmpPicture))
            {   /* Put the shell's icon into the picture at the size the pane    */
                /* wants.  The ground is cleared to transparent first, so the    */
                /* icon's own see-through parts stay see-through on whatever     */
                /* row colour it is shown on.  The shell's lists come only in    */
                /* fixed sizes, so the icon is copied as it is when it already   */
                /* fits and resized otherwise.                                   */
                gfx.Clear(Color.Transparent);

                if (bmpIcon.Width == sizPicture.Width && bmpIcon.Height == sizPicture.Height)
                {   /* Already the size wanted: copied pixel for pixel, since   */
                    /* any resampling would only soften an icon drawn for       */
                    /* this very size.                                          */
                    gfx.DrawImageUnscaled(bmpIcon, 0, 0);
                }
                else
                {   /* Another size: resized with the smoothest settings GDI+     */
                    /* offers, so a shrunk icon keeps clean edges.  This is the   */
                    /* usual case, as the pane's sizes follow View > Text Size.   */
                    gfx.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    gfx.PixelOffsetMode   = PixelOffsetMode.HighQuality;
                    gfx.DrawImage(bmpIcon, new Rectangle(0, 0, sizPicture.Width, sizPicture.Height));
                }
            }

            /* The list-sized copy is no longer needed. */
            bmpIcon.Dispose();

            /* The picture at the size wanted. */
            return(bmpPicture);
        }

        /*--------------------------------------------------------------------*/
        /* SHI_LoadSystemList:                                                */
        /*                                                                    */
        /* Ask the shell, once, for one of its system image lists and measure */
        /* its picture size.  A list the shell will not give is remembered as */
        /* unavailable (null, size 0) and not asked for again.                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     k : the list's place in SHI_aiListIds.                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : SHI_aSystemLists[k] and SHI_aiSystemPixels[k] are set.  */
        /*--------------------------------------------------------------------*/
        private static void SHI_LoadSystemList(int k)
        {
            SHI_IImageList listShell;
            Guid           guidInterface;
            int            iWidth;
            int            iHeight;
            int            i;

            if (SHI_aSystemLists == null)
            {   /* First use: nothing asked for yet.  -1 marks "not asked",   */
                /* kept apart from 0, "asked and unavailable", so a refused   */
                /* list is never asked for twice.                             */
                SHI_aSystemLists   = new SHI_IImageList[SHI_aiListIds.Length];
                SHI_aiSystemPixels = new int[SHI_aiListIds.Length];

                for (i = 0; i < SHI_aiSystemPixels.Length; i++)
                {   /* One list per iteration. */
                    SHI_aiSystemPixels[i] = -1;
                }
            }

            if (SHI_aiSystemPixels[k] >= 0)
            {   /* Asked for before, whatever the answer was. */
                return;
            }

            /* Unavailable until shown otherwise -- set before asking, so a   */
            /* list the shell refuses is not asked for again.                 */
            SHI_aiSystemPixels[k] = 0;

            /* A copy of the interface's identifier: it is passed by ref,   */
            /* which a readonly field cannot be.                            */
            guidInterface = SHI_IID_IIMAGELIST;

            if (SHGetImageList(SHI_aiListIds[k], ref guidInterface, out listShell) != 0 || listShell == null)
            {   /* The shell would not give this list: it stays unavailable   */
                /* (null, size 0), and SHI_SystemPicture uses the others.     */
                return;
            }

            if (listShell.SHII_GetIconSize(out iWidth, out iHeight) != 0)
            {   /* It would not say its size, so it cannot be chosen by size:   */
                /* treated as unavailable, and the reference simply dropped.    */
                return;
            }

            /* Available: the list kept (the interface reference lives as   */
            /* long as the program) and its height as its size.             */
            SHI_aSystemLists[k]   = listShell;
            SHI_aiSystemPixels[k] = iHeight;
        }

        /*--------------------------------------------------------------------*/
        /* SHI_Lookup:                                                        */
        /*                                                                    */
        /* The icon index and type name the shell shows for one item -- the   */
        /* item's own where its icon can differ from its kind's, the          */
        /* remembered answer for its extension otherwise.                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sPath     : the item's full path.                              */
        /*     bIsFolder : true for a folder or a drive's root.               */
        /*     iIcon     : receives the icon's index in this file's lists.    */
        /*     sTypeName : receives the type's name; "" when there is none.   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the two out arguments are set.                          */
        /*--------------------------------------------------------------------*/
        internal static void SHI_Lookup(string sPath, bool bIsFolder, out int iIcon, out string sTypeName)
        {
            SHI_Answer answer;
            string     sExtension;
            int        i;

            if (bIsFolder)
            {   /* A folder or drive: ask the shell about the real item, so a   */
                /* folder with a custom icon or a drive of a particular kind    */
                /* shows its own, and hand back its index in this file's        */
                /* lists.  Never cached: each folder can differ.  This is the   */
                /* slow question, which is why a listing shows the folder       */
                /* stand-in first and calls this afterwards.                    */
                answer    = SHI_Ask(sPath, 0, false);
                iIcon     = SHI_Map(answer.iIcon);
                sTypeName = answer.sTypeName;
                return;
            }

            /* The extension, lower-cased so ".TXT" and ".txt" share one      */
            /* remembered answer ("" for a name with none, a key like any).   */
            sExtension = System.IO.Path.GetExtension(sPath).ToLowerInvariant();

            for (i = 0; i < SHI_asOwnIconExtensions.Length; i++)
            {   /* One own-icon extension per iteration. */
                if (sExtension == SHI_asOwnIconExtensions[i])
                {   /* This file's icon is its own (a program, a shortcut ...):   */
                    /* ask the shell about the real file, uncached as a folder    */
                    /* is, and hand back its index in this file's lists.          */
                    answer    = SHI_Ask(sPath, 0, false);
                    iIcon     = SHI_Map(answer.iIcon);
                    sTypeName = answer.sTypeName;
                    return;
                }
            }

            if (SHI_dictByExtension == null)
            {   /* First use: the per-extension cache, shared with          */
                /* SHI_LookupQuick, made on first need and kept for good.   */
                SHI_dictByExtension = new Dictionary<string, SHI_Answer>();
            }

            if (!SHI_dictByExtension.TryGetValue(sExtension, out answer))
            {   /* An extension not met before: ask once, by name alone. */

                /* The name "x" plus the extension stands for every file of   */
                /* the kind -- with USEFILEATTRIBUTES the shell judges by     */
                /* name, so the file need not exist.  The answer is kept      */
                /* even when the shell gave none, so a kind it cannot         */
                /* answer for is not asked about again.                       */
                answer = SHI_Ask("x" + sExtension, SHI_ATTRIBUTE_NORMAL, true);
                SHI_dictByExtension.Add(sExtension, answer);
            }

            /* The extension's answer, its index translated to this file's. */
            iIcon     = SHI_Map(answer.iIcon);
            sTypeName = answer.sTypeName;
        }

        /*--------------------------------------------------------------------*/
        /* SHI_LookupQuick:                                                   */
        /*                                                                    */
        /* The icon index and type name for one item WITHOUT TOUCHING THE     */
        /* ITEM: by its name alone.  A folder gets the ordinary folder's      */
        /* answer and a file the remembered answer for its extension, so      */
        /* this costs nothing per row once an extension has been met.         */
        /*                                                                    */
        /* WHY IT EXISTS.  Asking about a real folder, or about a file whose  */
        /* icon is its own (a program, an installer, a shortcut), makes the   */
        /* shell open that item -- milliseconds each, and far longer for a    */
        /* large installer that has not been read lately -- which stalled a   */
        /* listing of thousands of rows for seconds.  A list is therefore     */
        /* filled from THIS, and shown; the rows this says are not final are  */
        /* then asked about properly (SHI_Lookup) a few at a time.  Its body  */
        /* repeats SHI_Lookup's extension steps rather than sharing them, so  */
        /* each function reads whole; the two must keep asking the same       */
        /* question, as they fill one cache.                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sPath     : the item's path (only its extension is used).      */
        /*     bIsFolder : true for a folder.                                 */
        /*     iIcon     : receives the icon's index in this file's lists.    */
        /*     sTypeName : receives the type's name; "" when there is none.   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the answer is FINAL; false when the item's    */
        /*            own icon may differ and SHI_Lookup should be asked.     */
        /*--------------------------------------------------------------------*/
        internal static bool SHI_LookupQuick(string sPath, bool bIsFolder, out int iIcon, out string sTypeName)
        {
            SHI_Answer answer;
            string     sExtension;
            bool       bFinal;
            int        i;

            if (bIsFolder)
            {   /* A folder: the ordinary folder's answer stands in; its own (a customised icon) is asked for later. */

                /* THE ORDER MATTERS: SHI_FolderIcon fills SHI_answerFolder   */
                /* the first time, so the type name is read after it.         */
                iIcon     = SHI_FolderIcon();
                sTypeName = SHI_answerFolder.sTypeName;
                return(false);
            }

            /* The extension, as in SHI_Lookup; final unless it is one of   */
            /* the kinds whose icon is the file's own.                      */
            sExtension = System.IO.Path.GetExtension(sPath).ToLowerInvariant();
            bFinal     = true;

            for (i = 0; i < SHI_asOwnIconExtensions.Length; i++)
            {   /* One own-icon extension per iteration; the loop does not      */
                /* stop at a match, which costs nothing on a list this short.   */
                if (sExtension == SHI_asOwnIconExtensions[i])
                {   /* This file's icon is its own: the extension's answer only stands in. */
                    bFinal = false;
                }
            }

            if (SHI_dictByExtension == null)
            {   /* First use: the per-extension cache, shared with SHI_Lookup. */
                SHI_dictByExtension = new Dictionary<string, SHI_Answer>();
            }

            if (!SHI_dictByExtension.TryGetValue(sExtension, out answer))
            {   /* An extension not met before: ask once, by name alone. */

                /* The same question, and the same cache, as SHI_Lookup:   */
                /* an answer remembered by either serves both.             */
                answer = SHI_Ask("x" + sExtension, SHI_ATTRIBUTE_NORMAL, true);
                SHI_dictByExtension.Add(sExtension, answer);
            }

            /* The extension's answer -- the final one, or a stand-in for an   */
            /* own-icon file -- its index translated to this file's.           */
            iIcon     = SHI_Map(answer.iIcon);
            sTypeName = answer.sTypeName;

            /* Whether the row still needs its own icon asked for. */
            return(bFinal);
        }

        /*--------------------------------------------------------------------*/
        /* SHI_FolderIcon:                                                    */
        /*                                                                    */
        /* This file's index for the shell's ordinary closed-folder icon --   */
        /* the stand-in SHI_LookupQuick gives a folder until its own icon (a  */
        /* customised one, say) has been asked for.  Its one caller is        */
        /* SHI_LookupQuick; the ".." row has a picture of its own             */
        /* (SHI_AddPicture).  It also fills SHI_answerFolder, whose type name */
        /* SHI_LookupQuick reads after calling it.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : the icon's index in this file's lists.                   */
        /*--------------------------------------------------------------------*/
        private static int SHI_FolderIcon()
        {
            if (!SHI_bFolderKnown)
            {   /* First use: ask by attributes alone and remember it.  The   */
                /* name "folder" is any name: with the directory attribute    */
                /* and USEFILEATTRIBUTES the shell answers for a plain        */
                /* folder without looking for one.                            */
                SHI_answerFolder = SHI_Ask("folder", SHI_ATTRIBUTE_DIRECTORY, true);
                SHI_bFolderKnown = true;
            }

            /* The generic folder's index, translated (SHI_Map copies it in   */
            /* the first time and remembers it after).                        */
            return(SHI_Map(SHI_answerFolder.iIcon));
        }
    }
}
