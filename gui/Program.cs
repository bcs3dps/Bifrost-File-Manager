/*----------------------------------------------------------------------------*/
/* Program.cs                                                                 */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* BifrostFM's entry point.  The program starts as a COMMAND-LINE program:    */
/* it attaches to the console it was launched from, reads its arguments, and  */
/* answers --help and --version there without ever creating a window.  Only   */
/* when no such command was given does it let go of the console, load the     */
/* engine and open the main window.                                           */
/*                                                                            */
/*     BifrostFM.exe                 open the window                          */
/*     BifrostFM.exe <image-file>    open the window with that image open     */
/*     BifrostFM.exe --debug [...]   as either of the above, keeping a debug  */
/*                                   log (a development build always does)    */
/*     BifrostFM.exe --open-device, --read-device, --write-device,            */
/*                   --partition-device or --check-device                     */
/*                                   open the window and go straight to that  */
/*                                   device command (how a restart as         */
/*                                   Administrator resumes)                   */
/*     BifrostFM.exe --partition-device --disk <\\.\PhysicalDriveN>           */
/*                   --after <pid>   that disk's partition window, the disk   */
/*                                   and its risk warning taken as already    */
/*                                   answered (how the restart as             */
/*                                   Administrator hands over the disk the    */
/*                                   restarting copy chose); a --disk given   */
/*                                   without --after is dropped, and the      */
/*                                   window asks for the disk as usual        */
/*     BifrostFM.exe --after <pid>   first wait for that process to exit      */
/*     BifrostFM.exe --debug-copy-delay <ms>                                  */
/*                                   a development (DEBUG) build only: pause  */
/*                                   that long before each copied item, for   */
/*                                   trying Exit and Cancel while work runs   */
/*     BifrostFM.exe --help | -h     print usage and exit                     */
/*     BifrostFM.exe --version       print the version and exit               */
/*                                                                            */
/* Exit codes: 0 success; 1 the engine could not be loaded; 2 bad usage.      */
/*----------------------------------------------------------------------------*/

/* The framework: the base types (the console the command line prints to),   */
/* the process --after waits for, the dialog and heading fonts, the log's    */
/* path beside the program and the program's own location, the console       */
/* calls' P/Invoke, the Administrator test, and the window's message loop.   */
/* The debug log (gui\lib\DebugLog.cs) is in this program's own namespace.   */
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Forms;

namespace BifrostFM
{
    internal static class APP_clsProgram
    {
        /* The version, the console's parent, and the two font steps --   */
        /* documentation table.                                           */
        internal const string APP_VERSION        = APP_BuildVersion.APP_BUILD_VERSION;  // program version, shown by --version, the usage text and the About box (the debug log does not record it): generated into obj\Version.cs by make_version.bat from version.txt, whose last number every build steps
        private  const int    APP_ATTACH_PARENT  = -1;                                  // AttachConsole: the console of the process that launched this one
        internal const float  APP_DIALOG_POINTS_UP = 1f;                                // how much larger than the system message box font every dialog's text is, points (two points up looked slightly too big; one is the compromise); read by Main, which makes the dialog font once and passes it down -- no dialog reads it.  Internal, not private, so another build compiled from these same sources can draw the dialogs exactly as the program does
        internal const float  APP_HEADING_POINTS_UP = 5f;                               // how much larger than the system message box font a dialog's WARNING HEADING is, points, shown bold; made once by Main like the dialog font and passed down -- no dialog reads it

        /* The process exit codes -- documentation table (the header's list). */
        private const int APP_EXIT_OK        = 0;             // process exit code: success
        private const int APP_EXIT_NO_ENGINE = 1;             // process exit code: the engine DLL could not be loaded
        private const int APP_EXIT_USAGE     = 2;             // process exit code: an argument was not understood

        /* The debug log and the --after wait -- documentation table. */
        private const string APP_LOG_FOLDER  = "log";                   // the debug log's folder, beside the program -- the only place a log is kept
        private const string APP_LOG_FILE    = "BifrostFM-debug.log";   // the debug log's file name
        private const int    APP_LOG_KEEP    = 10;                      // most debug logs kept, this run's included
        private const int    APP_AFTER_WAIT_MS = 10000;                 // --after: the longest this run waits for the named process to exit before starting its log anyway, milliseconds (the copy that restarts this one closes at once; 10 s covers a slow engine close)

        /* What the start-up decided, for the rest of the program to read --   */
        /* documentation table.  Each is set in Main (the log path through     */
        /* App_StartLog) before the main window is created.                    */
        private static string App_sLogPath     = "";         // this run's debug log; "" when no log is being kept
        private static string App_sStartCommand = "";        // "open-device", "read-device", "write-device", "partition-device" or "check-device" when that option was given: the window runs that command as soon as it is shown (how an elevated restart resumes); "" otherwise
        private static string App_sStartDisk   = "";         // --disk's \\.\PhysicalDriveN, with --partition-device only: the disk the copy that restarted this one chose and warned about; "" when none was given, or one was given without --after
        private static bool   App_bElevated    = false;      // this process runs with Administrator rights (needed to read or write a real device)

        /*--------------------------------------------------------------------*/
        /* APP_ReadLogPath:                                                   */
        /*                                                                    */
        /* This run's debug log, as start-up set it up: for Help > Debug Log  */
        /* and for a restart.  Reading it opens nothing and changes nothing.  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the log's path; "" when no log is kept.               */
        /*--------------------------------------------------------------------*/
        internal static string APP_ReadLogPath()
        {
            /* Set once, by start-up (App_StartLog). */
            return(App_sLogPath);
        }

        /*--------------------------------------------------------------------*/
        /* APP_ReadStartCommand:                                              */
        /*                                                                    */
        /* The device command the command line asked for, which the window    */
        /* runs as soon as it is shown (how an elevated restart resumes).     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the option without its dashes ("open-device" ...); "" */
        /*              when none was given.                                  */
        /*--------------------------------------------------------------------*/
        internal static string APP_ReadStartCommand()
        {
            /* Set by Main's reading of the command line, finished before   */
            /* the window is made; when several were given, the last one.   */
            return(App_sStartCommand);
        }

        /*--------------------------------------------------------------------*/
        /* APP_ReadStartDisk:                                                 */
        /*                                                                    */
        /* The disk --disk named for --partition-device: the one the copy     */
        /* that restarted this one as Administrator chose, and gave the risk  */
        /* warning for, before the restart -- so the partition request finds  */
        /* it in the disk list and asks neither again.  "" when no --disk was */
        /* given, or one came without --after (Main drops it: only the        */
        /* restart passes the two together).                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the disk's \\.\PhysicalDriveN path; "" when none was  */
        /*              given, or it was dropped for want of --after.         */
        /*--------------------------------------------------------------------*/
        internal static string APP_ReadStartDisk()
        {
            /* Set by Main's reading of the command line, finished before   */
            /* the window is made, and only with --partition-device.        */
            return(App_sStartDisk);
        }

        /*--------------------------------------------------------------------*/
        /* APP_IsProcessElevated:                                             */
        /*                                                                    */
        /* Whether this process runs with Administrator rights, as start-up   */
        /* found it before the window was made.  A getter only, so no caller  */
        /* can change what start-up decided.                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the process holds the Administrator role.     */
        /*--------------------------------------------------------------------*/
        internal static bool APP_IsProcessElevated()
        {
            /* Set once, by Main. */
            return(App_bElevated);
        }

        /*--------------------------------------------------------------------*/
        /* App_AttachConsole:                                                 */
        /*                                                                    */
        /* kernel32: attach this process to an existing console, so a program */
        /* built as a windowed exe can still print to the command prompt it   */
        /* was started from.                                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iProcessId : the owning process, or -1 for the parent's.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : non-zero when attached; zero when there is no console    */
        /*           to attach to (started from Explorer).                    */
        /*--------------------------------------------------------------------*/
        [DllImport("kernel32.dll", EntryPoint = "AttachConsole", SetLastError = true)]
        private static extern int App_AttachConsole(int iProcessId);

        /*--------------------------------------------------------------------*/
        /* App_FreeConsole:                                                   */
        /*                                                                    */
        /* kernel32: detach from the console again, so the command prompt     */
        /* that launched the window is not tied to it.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : non-zero on success.                                     */
        /*--------------------------------------------------------------------*/
        [DllImport("kernel32.dll", EntryPoint = "FreeConsole", SetLastError = true)]
        private static extern int App_FreeConsole();

        /*--------------------------------------------------------------------*/
        /* App_PrintUsage:                                                    */
        /*                                                                    */
        /* Write the usage text to the attached console.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the usage lines are written to stdout.                  */
        /*--------------------------------------------------------------------*/
        private static void App_PrintUsage()
        {
            /* The usage text: a title line naming every filesystem the   */
            /* program opens and where, then one line per form of the     */
            /* command.  Main calls this while the console is still       */
            /* attached and before any log could redirect the console's   */
            /* output to a file.                                          */
            Console.WriteLine("");
            Console.WriteLine("BifrostFM " + APP_VERSION + " -- a file manager for ext2 / ext3 / ext4, FAT and exFAT image files and devices");
            Console.WriteLine("");
            Console.WriteLine("Usage:");
            Console.WriteLine("  BifrostFM.exe                 open the window");
            Console.WriteLine("  BifrostFM.exe <image-file>    open the window with that image open");
            Console.WriteLine("  BifrostFM.exe --debug [...]   keep a debug log for this run (log\\BifrostFM-debug.log beside the program)");
            Console.WriteLine("  BifrostFM.exe --open-device   open the window and go straight to File > Open > Device");
            Console.WriteLine("  BifrostFM.exe --read-device   ... to File > Save Device to Image File");
            Console.WriteLine("  BifrostFM.exe --write-device  ... to File > Write Image File to Device");
            Console.WriteLine("  BifrostFM.exe --partition-device ... to File > Partition/Format > Device");
            /* The --disk line names --after beside it: Main drops a --disk    */
            /* given without --after, so the disk is taken as chosen and       */
            /* warned about only as the restart as Administrator passes it,    */
            /* and the line says so rather than promise it for --disk alone.   */
            Console.WriteLine("  BifrostFM.exe --partition-device --disk <\\\\.\\PhysicalDriveN> --after <pid>");
            Console.WriteLine("                                ... that disk, its risk warning taken as answered (the restart as Administrator uses it; a --disk without --after is dropped)");
            Console.WriteLine("  BifrostFM.exe --check-device  ... to File > Check Filesystem's device picker");
            Console.WriteLine("  BifrostFM.exe --after <pid> [...]  wait for that process to exit first (the restart as Administrator uses it)");
#if DEBUG
            /* The DEBUG build's test option, listed only where it exists. */
            Console.WriteLine("  BifrostFM.exe --debug-copy-delay <ms> [...]  pause that long before each copied item (development builds only)");
#endif
            Console.WriteLine("  BifrostFM.exe --help | -h     print this text");
            Console.WriteLine("  BifrostFM.exe --version       print the version");
        }

        /*--------------------------------------------------------------------*/
        /* App_IsElevated:                                                    */
        /*                                                                    */
        /* Whether this process runs with Administrator rights.  Reading or   */
        /* writing a real disk needs them; listing disks does not.  Asked     */
        /* once, by Main at start-up, and kept in App_bElevated for           */
        /* APP_IsProcessElevated, since it cannot change while the process    */
        /* runs.                                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the process token carries the Administrators  */
        /*            role.                                                   */
        /*--------------------------------------------------------------------*/
        private static bool App_IsElevated()
        {
            WindowsIdentity  objIdentity;
            WindowsPrincipal objPrincipal;

            /* This process's own identity and the role test over it.  The   */
            /* identity is not disposed; its token handle goes when it is    */
            /* collected -- a one-off at start-up.                           */
            objIdentity  = WindowsIdentity.GetCurrent();
            objPrincipal = new WindowsPrincipal(objIdentity);

            /* The Administrators role shows in the token only when the    */
            /* process is elevated: an administrator's ordinary process    */
            /* runs with it filtered out (Windows' behaviour, as found).   */
            return(objPrincipal.IsInRole(WindowsBuiltInRole.Administrator));
        }

        /*--------------------------------------------------------------------*/
        /* App_StartLog:                                                      */
        /*                                                                    */
        /* Open this run's debug log, in the "log" folder beside the program  */
        /* and nowhere else, and remember where it is so Help > Debug Log can */
        /* show it (that item is on a DEBUG build's menu only; a release run  */
        /* with --debug keeps the log for reading outside the program).  When */
        /* the folder or the file cannot be made or opened there is no log,   */
        /* and the program runs on without one.                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : App_sLogPath names the open log, or is "".              */
        /*--------------------------------------------------------------------*/
        private static void App_StartLog()
        {
            string sExeDir;
            string sPath;

            /* The log folder beside the program, so the log travels with it.     */
            /* DBGLOG_SetupLogFile makes the folder, renames the earlier run's    */
            /* log aside with its own last-write time and keeps APP_LOG_KEEP      */
            /* files in all (Rotate) -- best effort: a rename that fails leaves   */
            /* the old log for the open to overwrite --, opens the new one,       */
            /* writes its "log started" line with the date, and from then on      */
            /* points the console's output and error streams at it too.  Any      */
            /* failure gives null and leaves logging as it was                    */
            /* (gui\lib\DebugLog.cs, DBGLOG_clsDebugLog).  There is NO fallback   */
            /* folder, on purpose: --after's wait (App_WaitForExit) lets the      */
            /* copy that held the log close first, and a log split across two     */
            /* folders is a log nobody finds.                                     */
            sExeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            sPath = DBGLOG_clsDebugLog.DBGLOG_SetupLogFile(Path.Combine(sExeDir, APP_LOG_FOLDER),
                APP_LOG_FILE, DBGLOG_enmLogFileMode.Rotate, APP_LOG_KEEP);

            if (sPath == null)
            {   /* The folder or the file could not be made or opened (a        */
                /* read-only install, a log still held open by another run):    */
                /* carry on without a log, leaving App_sLogPath "" -- Help >    */
                /* Debug Log (a DEBUG build's menu only) then says none is      */
                /* kept and how to keep one (MainForm.cs, Main_ShowDebugLog).   */
                /* The program never needs a log to run.                        */
                return;
            }

            /* Remember the log, through APP_ReadLogPath, for Help > Debug       */
            /* Log and for a restart as Administrator, which passes --debug      */
            /* on when a log is kept (MainForm.cs).  Nothing more is written     */
            /* here: DBGLOG_SetupLogFile has already written the "log started"   */
            /* line.                                                             */
            App_sLogPath = sPath;
        }

        /*--------------------------------------------------------------------*/
        /* App_WaitForExit:                                                   */
        /*                                                                    */
        /* --after's wait: let the copy of the program that started this one  */
        /* (the restart as Administrator) finish closing first, so this run   */
        /* can open the debug log that copy was still holding.                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iPid : the process to wait for.                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : that process has exited, or the wait gave up.           */
        /*--------------------------------------------------------------------*/
        private static void App_WaitForExit(int iPid)
        {
            Process procAfter;

            try
            {   /* Every failure below is absorbed: a wait that cannot be   */
                /* made is no reason to stop the program starting.          */

                /* The process by its id.  An id that names nothing running   */
                /* -- the copy has already gone -- throws, and the catch      */
                /* below takes that as "nothing to wait for".                 */
                using (procAfter = Process.GetProcessById(iPid))
                {   /* Found: the process handle is released when the block   */
                    /* ends.                                                  */

                    /* Wait for it, but never longer than APP_AFTER_WAIT_MS:   */
                    /* the copy closes as soon as this one is running, so      */
                    /* the wait is normally short.  Once it has gone, its      */
                    /* handles are closed -- the log's among them -- and       */
                    /* App_StartLog can rotate the log aside and open this     */
                    /* run's.  If the time runs out the program goes on        */
                    /* anyway, and this run may have no log -- the log is      */
                    /* never put anywhere else.                                */
                    procAfter.WaitForExit(APP_AFTER_WAIT_MS);
                }
            }
            catch
            {   /* Already gone, or not ours to wait on: nothing to wait   */
                /* for, and the start-up carries on.                       */
            }
        }

        /*--------------------------------------------------------------------*/
        /* APP_MakeDialogFont:                                                */
        /*                                                                    */
        /* Make the font every dialog box of the program shows its text in:   */
        /* the system message box font's face (Segoe UI on current Windows,   */
        /* as found) at its size plus fPointsUp.  The system font is the      */
        /* FLOOR: a dialog's text is never smaller than the message box's.    */
        /* WinForms' inherited default, Microsoft Sans Serif 8.25 pt (as      */
        /* found), is smaller and an older face, and no dialog starts from    */
        /* it.  This is made ONCE, by Main, and handed to the main window,    */
        /* which hands it to every dialog and editor it creates: no dialog    */
        /* has a font of its own written into its code -- it is passed in at  */
        /* or before creation.  The About window is the one exception, by     */
        /* design: it takes no font and keeps its own fixed sizes, below the  */
        /* floor for its licence lines (AboutForm.cs's file header).          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     fPointsUp : how many points above the system size.             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Font : the dialog font, new; the caller owns it.               */
        /*--------------------------------------------------------------------*/
        internal static Font APP_MakeDialogFont(float fPointsUp)
        {
            Font fntDialog;
            Font fntBox;

            /* SystemFonts hands back a NEW font each time (as found): read   */
            /* it, then free it -- the dialog font is a separate object, so   */
            /* freeing this one takes nothing from it.                        */
            using (fntBox = SystemFonts.MessageBoxFont)
            {   /* Its face, at its size in points plus the step -- points,   */
                /* not pixels, so the step means the same at any scaling.     */
                fntDialog = new Font(fntBox.FontFamily, fntBox.SizeInPoints + fPointsUp);
            }

            /* The dialog font; Main frees it once the window has closed. */
            return(fntDialog);
        }

        /*--------------------------------------------------------------------*/
        /* APP_MakeHeadingFont:                                               */
        /*                                                                    */
        /* Make the font a dialog's WARNING HEADING is shown in -- the one    */
        /* line above a message that must not be read past, such as the       */
        /* first-write question's "WARNING: YOU DO THIS AT YOUR OWN RISK".    */
        /* The same face as the dialog font, from the same system floor,      */
        /* larger by fPointsUp and BOLD, so it reads as a heading and not as  */
        /* a louder sentence.  Made ONCE, by Main, and passed down beside the */
        /* dialog font, for the same reason APP_MakeDialogFont says: no       */
        /* dialog decides a font of its own.                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     fPointsUp : how many points above the system size.             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Font : the heading font, new; the caller owns it.              */
        /*--------------------------------------------------------------------*/
        internal static Font APP_MakeHeadingFont(float fPointsUp)
        {
            Font fntHeading;
            Font fntBox;

            /* SystemFonts hands back a NEW font each time (as found): read   */
            /* it, then free it, as in APP_MakeDialogFont.                    */
            using (fntBox = SystemFonts.MessageBoxFont)
            {   /* Its face, at its size in points plus the step, bold. */
                fntHeading = new Font(fntBox.FontFamily, fntBox.SizeInPoints + fPointsUp, FontStyle.Bold);
            }

            /* The heading font; Main frees it once the window has closed. */
            return(fntHeading);
        }

        /*--------------------------------------------------------------------*/
        /* Main:                                                              */
        /*                                                                    */
        /* Attach to the launching console, handle the command-line-only      */
        /* arguments, and otherwise load the engine and run the window.       */
        /*                                                                    */
        /* The engine is loaded BEFORE the window is created, so a missing or */
        /* mismatched DLL is reported once, clearly, instead of surfacing as  */
        /* a failure of the first thing the user tries.  The dialog font and  */
        /* the warning-heading font are made before either, since the         */
        /* engine's failure is itself shown in a dialog, and both live until  */
        /* the window has closed.                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrArgs : the command line.                                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : an APP_EXIT_* code.                                      */
        /*--------------------------------------------------------------------*/
        [STAThread]
        private static int Main(string[] arrArgs)
        {
            string sStartImage;
            string sError;
            bool   bDebug;
            int    i;
            int    iAfterPid;
#if DEBUG
            int    iDelayMs;
#endif
            Font   fntDialog;
            Font   fntHeading;

            /* Attach FIRST, so anything printed below reaches the prompt.   */
            /* The result is not needed: started from Explorer there is no   */
            /* console, the attach fails, and the writes below then go       */
            /* nowhere, harmlessly (as found) -- and the window opens.       */
            App_AttachConsole(APP_ATTACH_PARENT);

            /* Nothing seen on the command line yet: no image to open, no   */
            /* log asked for, no process to wait for -- 0 can stand for     */
            /* "none" because the parse below refuses 0 as an id.           */
            sStartImage = "";
            bDebug      = false;
            iAfterPid   = 0;

            for (i = 0; i < arrArgs.Length; i++)
            {   /* One argument per iteration: a command, an option, or the image. */
                if (arrArgs[i] == "--help" || arrArgs[i] == "-h")
                {   /* Usage, then exit without a window. */
                    App_PrintUsage();
                    return(APP_EXIT_OK);
                }

                if (arrArgs[i] == "--version")
                {   /* The version, then exit without a window. */
                    Console.WriteLine("BifrostFM " + APP_VERSION);
                    return(APP_EXIT_OK);
                }

                if (arrArgs[i] == "--debug")
                {   /* Keep a debug log for this run; the window still opens. */
                    bDebug = true;
                    continue;
                }

                if (arrArgs[i] == "--open-device" || arrArgs[i] == "--read-device" || arrArgs[i] == "--write-device" ||
                    arrArgs[i] == "--partition-device" || arrArgs[i] == "--check-device")
                {   /* Run that device command once the window is up: the    */
                    /* option without its "--" is the command's name, as     */
                    /* App_sStartCommand lists them.  A second such option   */
                    /* replaces the first.                                   */
                    App_sStartCommand = arrArgs[i].Substring(2);
                    continue;
                }

                if (arrArgs[i] == "--disk")
                {   /* The disk for --partition-device, chosen and warned about   */
                    /* by the copy that restarted this one as Administrator.      */
                    /* Only a value that STARTS like a whole disk's path          */
                    /* (\\.\PhysicalDriveN, the one shape the restart passes)     */
                    /* is taken here; the partition sequence then uses it only    */
                    /* when it names a disk in the list it reads                  */
                    /* (Sys_AskPartitionTarget, CommandSequences.cs), and only    */
                    /* on a restart: see the test after this loop.                */
                    if (i + 1 >= arrArgs.Length ||
                        !arrArgs[i + 1].StartsWith(DEV_clsDisk.DEV_PHYSICAL_PREFIX, StringComparison.OrdinalIgnoreCase))
                    {   /* Nothing after it, or not a whole disk's path: bad    */
                        /* usage, said on the console with the usage text, as   */
                        /* for --after.                                         */
                        Console.WriteLine("BifrostFM: --disk needs a \\\\.\\PhysicalDriveN path");
                        App_PrintUsage();
                        return(APP_EXIT_USAGE);
                    }

                    /* The path is consumed with the option, as --after's id      */
                    /* is, so the next pass does not take it for an image file.   */
                    App_sStartDisk = arrArgs[i + 1];
                    i++;
                    continue;
                }

                if (arrArgs[i] == "--after")
                {   /* Wait for that process to exit before starting (the      */
                    /* restart as Administrator names the copy it replaces).   */
                    if (i + 1 >= arrArgs.Length || !int.TryParse(arrArgs[i + 1], out iAfterPid) || iAfterPid <= 0)
                    {   /* Nothing after it, or not a positive whole number:     */
                        /* bad usage, said on the console with the usage text,   */
                        /* and the exit code says so to whatever started this.   */
                        Console.WriteLine("BifrostFM: --after needs a process id");
                        App_PrintUsage();
                        return(APP_EXIT_USAGE);
                    }

                    /* The id is consumed with the option: stepping past it    */
                    /* keeps the next pass from taking it for an image file.   */
                    i++;
                    continue;
                }

#if DEBUG
                if (arrArgs[i] == "--debug-copy-delay")
                {   /* DEBUG BUILDS ONLY: the test pause at the start of each   */
                    /* copied item, in milliseconds, set straight into the      */
                    /* DEBUG global NCB_ProcessCopyEvent reads.  It lasts for   */
                    /* this run only and is never stored; a release build       */
                    /* does not know the option and refuses it as unknown       */
                    /* below.                                                   */
                    if (i + 1 >= arrArgs.Length || !int.TryParse(arrArgs[i + 1], out iDelayMs) || iDelayMs < 0)
                    {   /* Nothing after it, or not a whole number of 0 or   */
                        /* more: bad usage, said on the console with the     */
                        /* usage text, as for --after.                       */
                        Console.WriteLine("BifrostFM: --debug-copy-delay needs a number of milliseconds");
                        App_PrintUsage();
                        return(APP_EXIT_USAGE);
                    }

                    /* Set before the window, and so the worker, exists; the   */
                    /* value is consumed with the option, as --after's is.     */
                    DBG_clsDebugValues.DBG_iCopyDelayMs = iDelayMs;
                    i++;
                    continue;
                }
#endif

                if (arrArgs[i].StartsWith("-"))
                {   /* An option this program does not have -- refused rather   */
                    /* than taken for an image file named "-something".         */
                    Console.WriteLine("BifrostFM: unknown option " + arrArgs[i]);
                    App_PrintUsage();
                    return(APP_EXIT_USAGE);
                }

                if (sStartImage.Length != 0)
                {   /* A second image file: only one can be open. */
                    Console.WriteLine("BifrostFM: more than one image file was named");
                    App_PrintUsage();
                    return(APP_EXIT_USAGE);
                }

                /* Anything else is the image file to open at start-up. */
                sStartImage = arrArgs[i];
            }

            if (App_sStartDisk.Length != 0 && App_sStartCommand != "partition-device")
            {   /* --disk without --partition-device: bad usage -- only the   */
                /* partition window takes a disk from the command line.       */
                Console.WriteLine("BifrostFM: --disk is used with --partition-device");
                App_PrintUsage();
                return(APP_EXIT_USAGE);
            }

            if (App_sStartDisk.Length != 0 && iAfterPid == 0)
            {   /* --disk without --after: not the restart as Administrator,   */
                /* which always passes the two together (MainForm.cs, the      */
                /* elevated start).  The disk is taken as "chosen and warned   */
                /* about" -- the risk question skipped -- only from that       */
                /* restart, so here it is dropped: the partition window asks   */
                /* for the disk and gives its warning as on any other start.   */
                App_sStartDisk = "";
            }

            /* No command-line-only work: this is a window from here on. */
            App_FreeConsole();

#if DEBUG
            /* A development build always keeps the log. */
            bDebug = true;
#endif

            if (iAfterPid != 0)
            {   /* A restart: let the copy it replaces finish closing (and   */
                /* release the log beside the program) first.                */
                App_WaitForExit(iAfterPid);
            }

            if (bDebug)
            {   /* Start the log before anything that could be worth logging. */
                App_StartLog();
            }

            /* The WinForms set-up every window needs, before the first one   */
            /* is made (the engine's failure dialog below included): the      */
            /* current Windows look for the controls, and GDI text drawing    */
            /* -- the framework's documented order, as found.                 */
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            /* Whether a device can be read or written from this process,    */
            /* decided once, before the window exists, and read through      */
            /* APP_IsProcessElevated: before a device command the window     */
            /* asks it, and when it is false posts the restart as            */
            /* Administrator instead -- an Exit that asks the user first     */
            /* (MainForm.cs, Main_RequireElevated).  Partition Device's      */
            /* sequence asks it on the worker, after its disk and warning,   */
            /* and turns itself into that Exit (CommandSequences.cs,         */
            /* Sys_OpenPartitionOperation).                                  */
            App_bElevated = App_IsElevated();

            /* The one dialog font and the one warning-heading font, for the   */
            /* whole run.                                                      */
            fntDialog  = APP_MakeDialogFont(APP_DIALOG_POINTS_UP);
            fntHeading = APP_MakeHeadingFont(APP_HEADING_POINTS_UP);

            using (fntDialog)
            using (fntHeading)
            {   /* Both released once the window is gone (or the engine failed). */
                if (!ENG_clsNative.ENG_Load(out sError))
                {   /* Without the engine there is nothing the window could do:   */
                    /* the reason into the log, then shown in the program's       */
                    /* dialog with no owner (there is no window yet), and the     */
                    /* exit code says so.  The using blocks free both fonts on    */
                    /* this return.                                               */
                    if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                    {   /* Only while the debug log is kept: otherwise the line is never built. */
                        DBGLOG_clsDebugLog.DBGLOG_Write("app", "the engine did not load: " + sError);
                    }

                    /* Then shown, with no owner, and the exit code says why. */
                    MSG_clsMessageForm.MSG_Show(null, fntDialog, sError, "BifrostFM", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1);
                    return(APP_EXIT_NO_ENGINE);
                }

                /* The main window, given the image to open (or "") and the   */
                /* two fonts; Run returns only once it has closed, so the     */
                /* fonts outlive every dialog it shows.                       */
                Application.Run(new MAIN_clsMainForm(sStartImage, fntDialog, fntHeading));
            }

            /* The window has closed -- it unloaded the engine on its way out   */
            /* (MainForm.cs, Main_OnFormClosing): a normal end.                 */
            return(APP_EXIT_OK);
        }
    }
}
