/*-----------------------------------------------------------------------------*/
/* DebugLog.cs -- one diagnostic log for the whole program                     */
/*                                                                             */
/* SPDX-License-Identifier: GPL-2.0-or-later                                   */
/* Copyright (c) 2026 B. C. Services                                           */
/*                                                                             */
/* The single entry point for diagnostic output.  A caller tests the switch    */
/* at its own line, so that with the log off not even the line's text is       */
/* built, and then writes:                                                     */
/*                                                                             */
/*     if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)                                 */
/*     {   ...                                                                 */
/*         DBGLOG_clsDebugLog.DBGLOG_Write("tag", "message");                  */
/*     }                                                                       */
/*                                                                             */
/* and where the line goes is decided by two statics:                          */
/*                                                                             */
/*     DBGLOG_bEnabled false                    nothing is written             */
/*     DBGLOG_bEnabled true, DBGLOG_LogWriter null                             */
/*                                              the line goes to Console.Error */
/*     DBGLOG_bEnabled true, DBGLOG_LogWriter set                              */
/*                                              the line goes to that writer   */
/*                                                                             */
/* A build that defines DEBUG starts with DBGLOG_bEnabled true; any other      */
/* build starts with it false and turns it on explicitly (a --debug            */
/* argument).  DBGLOG_SetupLogFile opens a log file, makes it the writer and   */
/* turns DBGLOG_bEnabled on in one step.                                       */
/*                                                                             */
/* THREADS: every write is made under one lock, so a worker thread and the     */
/* window's own thread may both log and their lines never interleave.          */
/*-----------------------------------------------------------------------------*/

/* The framework only: the console, the clock, files and folders. */
using System;
using System.IO;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* DBGLOG_enmLogFileMode -- what the log file's setup does with a log     */
    /* file an earlier run left behind.  KNOWN UNUSED AT PRESENT: Overwrite   */
    /* and Append, which the program never passes (Program.cs uses Rotate);   */
    /* both are kept on purpose, as part of the log's interface.              */
    /*------------------------------------------------------------------------*/
    public enum DBGLOG_enmLogFileMode
    {
        Overwrite,    // truncate and reopen: the earlier run's lines are lost (DBGLOG_SetupLogFileOverwrite's behaviour)
        Rotate,       // rename the old file to <stem>-yyyy-MM-dd-HH-mm-ss<ext> (its own last-write time) and start a fresh one; older renamed files are pruned
        Append        // keep writing onto the end of the existing file; nothing is ever renamed or pruned, so the file only grows
    }

    /*------------------------------------------------------------------------*/
    /* DBGLOG_clsDebugLog -- the log itself (the file header describes it):   */
    /* the master switch, the writer, the writes, and the log file's setup    */
    /* and rotation.  Static, because the whole program shares the one log.   */
    /*------------------------------------------------------------------------*/
    public static class DBGLOG_clsDebugLog
    {
        private const int DBGLOG_DEFAULT_MAX_BACKUPS = 10;    // Rotate: how many log files (the live one included) are kept when the caller does not say
        private const int DBGLOG_MAX_NAME_SUFFIX     = 99;    // Rotate: the highest "-N" tried when two runs share one second, before giving up

        /* The master switch, read by every caller at its own line before it   */
        /* builds anything: while it is false no line is built or written.     */
        /* A public FIELD rather than an accessor on purpose: every log line   */
        /* in the program tests it, so the test is a plain field read, the     */
        /* cheapest there is, and nothing between the caller and the value.    */
        /* DBGLOG_SetupLogFile is the one writer of it in the program.         */
#if DEBUG
        public static bool DBGLOG_bEnabled = true;        // a DEBUG build logs from its first line
#else
        public static bool DBGLOG_bEnabled = false;       // any other build logs only once it is told to (--debug)
#endif

        private static TextWriter Dbglog_twWriter = null;              // where lines go; null means Console.Error
        private static readonly object Dbglog_objLock = new object();  // held across each write, so two threads' lines never interleave

        /*--------------------------------------------------------------------*/
        /* DBGLOG_LogWriter:                                                  */
        /*                                                                    */
        /* The writer lines go to.  DBGLOG_SetupLogFile sets it; a caller     */
        /* with its own destination may set it directly.  KNOWN UNUSED AT     */
        /* PRESENT: nothing in the program calls this -- the one setup        */
        /* (Program.cs) goes through DBGLOG_SetupLogFile, which sets the      */
        /* field behind it -- and it is kept on purpose, as part of the       */
        /* log's interface.                                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     value : SET only -- the writer to use, or null to send lines   */
        /*             to Console.Error.  The old writer is NOT closed.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     TextWriter : the current writer, or null for Console.Error.    */
        /*--------------------------------------------------------------------*/
        public static TextWriter DBGLOG_LogWriter
        {
            get { return(Dbglog_twWriter); }
            set { Dbglog_twWriter = value; }
        }

        /*--------------------------------------------------------------------*/
        /* DBGLOG_WriteLine:                                                  */
        /*                                                                    */
        /* Write one line, prefixed with the local time to the millisecond    */
        /* ("HH:mm:ss.fff") so the time between two steps can be read         */
        /* straight off the log.  The DATE appears once, in the line          */
        /* DBGLOG_SetupLogFile writes when it opens the file.  The caller has */
        /* already tested DBGLOG_bEnabled: this writes whenever it is called. */
        /*                                                                    */
        /* A failure to write is swallowed: diagnostics must never be the     */
        /* thing that breaks the program.                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sMsg : the line, without a time stamp or a line ending.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the line is written (or its failure swallowed).         */
        /*--------------------------------------------------------------------*/
        public static void DBGLOG_WriteLine(string sMsg)
        {
            string sStamped;

            /* The time stamp is taken BEFORE the lock, so it is the moment   */
            /* the caller wrote the line, not the moment it got its turn.     */
            sStamped = DateTime.Now.ToString("HH:mm:ss.fff") + " " + sMsg;

            lock (Dbglog_objLock)
            {   /* One line at a time, whichever thread is writing. */
                try
                {   /* The write itself; a closed or failing writer must not throw into the caller. */
                    if (Dbglog_twWriter != null)
                    {   /* A log file (or other writer) was set up. */
                        Dbglog_twWriter.WriteLine(sStamped);
                    }
                    else
                    {   /* No writer: the console's error stream. */
                        Console.Error.WriteLine(sStamped);
                    }
                }
                catch
                {   /* Nowhere to report a failed log write; drop the line. */
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* DBGLOG_Write:                                                      */
        /*                                                                    */
        /* Write one line as "[tag] message".  The tag says which part of the */
        /* program is speaking, so a log can be filtered by it.  Called only  */
        /* inside an "if (DBGLOG_bEnabled)" at the caller's own line -- but   */
        /* for DBGLOG_SetupLogFile's "log started" line, which comes straight */
        /* after it has turned the switch on.                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sTag : the category, without brackets.                         */
        /*     sMsg : the message.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the line is written (or its failure swallowed).         */
        /*--------------------------------------------------------------------*/
        public static void DBGLOG_Write(string sTag, string sMsg)
        {
            /* The tag in brackets, then DBGLOG_WriteLine, which stamps   */
            /* the time and takes the lock.  The caller has already       */
            /* tested DBGLOG_bEnabled, so the string is built only when   */
            /* the log is kept.                                           */
            DBGLOG_WriteLine("[" + sTag + "] " + sMsg);
        }

        /*--------------------------------------------------------------------*/
        /* DBGLOG_SetupLogFileOverwrite:                                      */
        /*                                                                    */
        /* Start logging to a file, truncating whatever an earlier run left   */
        /* there.  DBGLOG_SetupLogFile does the work.  KNOWN UNUSED AT        */
        /* PRESENT: nothing in the program calls this -- Program.cs calls     */
        /* DBGLOG_SetupLogFile with Rotate -- and it is kept on purpose, as   */
        /* part of the log's interface.                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sLogDir   : the folder for the log; created when missing.      */
        /*     sFileName : the log file's name within it.                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the log file's full path, or null when it could not   */
        /*              be opened (logging then stays as it was).             */
        /*--------------------------------------------------------------------*/
        public static string DBGLOG_SetupLogFileOverwrite(string sLogDir, string sFileName)
        {
            /* Overwrite, this form's mode; the retention count is unused   */
            /* by that mode.                                                */
            return(DBGLOG_SetupLogFile(sLogDir, sFileName, DBGLOG_enmLogFileMode.Overwrite, DBGLOG_DEFAULT_MAX_BACKUPS));
        }

        /*--------------------------------------------------------------------*/
        /* DBGLOG_SetupLogFileMode:                                           */
        /*                                                                    */
        /* Start logging to a file with a chosen treatment of the old file,   */
        /* keeping the default number of rotated logs.  DBGLOG_SetupLogFile   */
        /* does the work.  KNOWN UNUSED AT PRESENT: nothing in the program    */
        /* calls this -- Program.cs calls DBGLOG_SetupLogFile itself, with    */
        /* its own retention count -- and it is kept on purpose, as part of   */
        /* the log's interface.                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sLogDir   : the folder for the log; created when missing.      */
        /*     sFileName : the log file's name within it.                     */
        /*     enmMode   : Overwrite, Rotate or Append.                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the log file's full path, or null when it could not   */
        /*              be opened.                                            */
        /*--------------------------------------------------------------------*/
        public static string DBGLOG_SetupLogFileMode(string sLogDir, string sFileName, DBGLOG_enmLogFileMode enmMode)
        {
            /* The default retention applies only to Rotate. */
            return(DBGLOG_SetupLogFile(sLogDir, sFileName, enmMode, DBGLOG_DEFAULT_MAX_BACKUPS));
        }

        /*------------------------------------------------------------------------*/
        /* DBGLOG_SetupLogFile:                                                   */
        /*                                                                        */
        /* Open the log file, make it DBGLOG_LogWriter, turn logging on, and      */
        /* write a first line carrying the date.                                  */
        /*                                                                        */
        /* Console.Out and Console.Error are ALSO pointed at the file, so         */
        /* anything a library prints to the console lands in the same log.        */
        /* A program that still wants to print to a real console must do so       */
        /* before calling this.                                                   */
        /*                                                                        */
        /* Any failure -- an unwritable folder, a locked file -- returns null     */
        /* and leaves logging as it was: diagnostics must never break the         */
        /* program they diagnose.  It is meant to be called ONCE: a second        */
        /* call replaces the writer without closing the first, whose file         */
        /* then stays open until the process ends.                                */
        /*                                                                        */
        /* Arguments:                                                             */
        /*     sLogDir     : the folder for the log; created when missing.        */
        /*     sFileName   : the log file's name within it.                       */
        /*     enmMode     : Overwrite, Rotate or Append (DBGLOG_enmLogFileMode). */
        /*     iMaxBackups : Rotate only -- the most log files, the live one      */
        /*                   included, left in the folder.  Zero or less turns    */
        /*                   rotation off, which makes Rotate behave as           */
        /*                   Overwrite.                                           */
        /*                                                                        */
        /* Returns:                                                               */
        /*     string : the log file's full path, or null on any failure.         */
        /*------------------------------------------------------------------------*/
        public static string DBGLOG_SetupLogFile(string sLogDir, string sFileName, DBGLOG_enmLogFileMode enmMode, int iMaxBackups)
        {
            string       sPath;
            bool         bAppend;
            StreamWriter swLog;

            try
            {   /* Everything that can fail on a read-only or missing folder. */
                if (!Directory.Exists(sLogDir))
                {   /* First run in this folder. */
                    Directory.CreateDirectory(sLogDir);
                }

                /* The live log's full path, returned to the caller at the end. */
                sPath = Path.Combine(sLogDir, sFileName);

                /* Move the earlier run's log aside BEFORE opening the new one.   */
                /* Rotation is best-effort: a locked old file must not cost       */
                /* this run its log.                                              */
                if (enmMode == DBGLOG_enmLogFileMode.Rotate && iMaxBackups > 0)
                {   /* Rotation was asked for and is not switched off. */
                    try
                    {   /* Rename the old log and prune the oldest. */
                        Dbglog_RotateOldLog(sPath, iMaxBackups);
                    }
                    catch
                    {   /* Could not rotate (the rename failed): fall through,   */
                        /* and the open below overwrites the old log instead.    */
                    }
                }

                /* Only Append keeps the existing content; Rotate has already   */
                /* renamed the old file, so it opens fresh like Overwrite.      */
                bAppend = (enmMode == DBGLOG_enmLogFileMode.Append);

                /* UTF-8 without a byte-order mark, flushed line by line so a   */
                /* crash loses nothing already logged.                          */
                swLog = new StreamWriter(sPath, bAppend, new System.Text.UTF8Encoding(false));
                swLog.AutoFlush = true;

                /* Route everything to the file, then announce it with the        */
                /* date.  Nothing from here on can throw into the catch: the      */
                /* assignments cannot fail and DBGLOG_Write swallows its own      */
                /* errors, so a null return always means logging was left         */
                /* unchanged.  The switch is set just above, so this line needs   */
                /* no test of it.                                                 */
                Dbglog_twWriter = swLog;
                Console.SetOut(swLog);
                Console.SetError(swLog);
                DBGLOG_bEnabled = true;
                DBGLOG_Write("debug", "log started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " mode=" + enmMode.ToString() + " -> " + sPath);

                /* The log is open: its path, for the caller to keep. */
                return(sPath);
            }
            catch
            {   /* No log this run (the folder or the file could not be made   */
                /* or opened); the caller sees null and carries on.            */
                return(null);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Dbglog_RotateOldLog:                                               */
        /*                                                                    */
        /* If a log already exists at sActivePath, rename it with its own     */
        /* last-write time in the name, then delete the oldest renamed logs   */
        /* so that, once the new live log is created, at most iMaxBackups     */
        /* files remain.                                                      */
        /*                                                                    */
        /* Renamed logs are recognised by name alone -- "<stem>-*<ext>" in    */
        /* the same folder -- so this works for any log file name.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sActivePath : the live log's full path.                        */
        /*     iMaxBackups : the most files to leave, the live one included;  */
        /*                   the caller has checked it is above zero.         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the old log is renamed and the overflow deleted; a      */
        /*            rename failure throws to the caller, which ignores it.  */
        /*--------------------------------------------------------------------*/
        private static void Dbglog_RotateOldLog(string sActivePath, int iMaxBackups)
        {
            string   sDir;
            string   sName;
            string   sStem;
            string   sExt;
            DateTime dtWritten;
            string   sBackupName;
            string   sBackupPath;
            int      iSuffix;
            string   sPattern;
            string[] asBackups;
            int      iToDelete;
            int      i;

            if (!File.Exists(sActivePath))
            {   /* No earlier log: nothing to rotate. */
                return;
            }

            /* Take the path apart; sExt keeps its leading dot. */
            sDir  = Path.GetDirectoryName(sActivePath);
            sName = Path.GetFileName(sActivePath);
            sStem = Path.GetFileNameWithoutExtension(sName);
            sExt  = Path.GetExtension(sName);

            if (sDir == null)
            {   /* A bare file name: the current folder. */
                sDir = "";
            }

            /* Name the old log by ITS last-write time, so the stamp says   */
            /* when that run happened, not when this one started.  The      */
            /* format sorts by name in time order.                          */
            dtWritten   = File.GetLastWriteTime(sActivePath);
            sBackupName = sStem + "-" + dtWritten.ToString("yyyy-MM-dd-HH-mm-ss") + sExt;
            sBackupPath = Path.Combine(sDir, sBackupName);

            /* Two runs inside one second would collide: add "-1", "-2", ...   */
            /* in turn, up to and including DBGLOG_MAX_NAME_SUFFIX.  The       */
            /* limit is tested BEFORE each name is built, so every suffix up   */
            /* to it is built and then tried by the loop's own test.           */
            iSuffix = 1;

            while (File.Exists(sBackupPath))
            {   /* One taken name per iteration: the next suffix is tried. */
                if (iSuffix > DBGLOG_MAX_NAME_SUFFIX)
                {   /* Every suffix up to the limit is taken too: far too    */
                    /* many collisions to be real.  Give up and leave the    */
                    /* old log where it is -- where the caller's open then   */
                    /* overwrites it, so that run's lines are lost.          */
                    return;
                }

                /* The next candidate name, "-<iSuffix>", for the loop's   */
                /* test; the counter moved on for the one after it.        */
                sBackupName = sStem + "-" + dtWritten.ToString("yyyy-MM-dd-HH-mm-ss") + "-" + iSuffix.ToString() + sExt;
                sBackupPath = Path.Combine(sDir, sBackupName);
                iSuffix++;
            }

            /* The old log renamed aside.  A failure here (the file in use   */
            /* by another run) throws to DBGLOG_SetupLogFile, which          */
            /* overwrites it.                                                */
            File.Move(sActivePath, sBackupPath);

            /* Prune.  (iMaxBackups - 1) renamed logs may sit beside the live   */
            /* log that is about to be created.                                 */
            sPattern = sStem + "-*" + sExt;

            try
            {   /* The folder may have become unreadable; pruning is optional. */
                asBackups = Directory.GetFiles(sDir, sPattern);
            }
            catch
            {   /* Could not list: skip pruning this run. */
                return;
            }

            if (asBackups.Length <= (iMaxBackups - 1))
            {   /* Still within the limit.  The pattern takes any file named   */
                /* "<stem>-...<ext>" in the folder, not only renamed logs;     */
                /* a stranger of that shape counts, and may be pruned.         */
                return;
            }

            /* Oldest first, by last-write time, so the deletions below take   */
            /* the oldest logs.                                                */
            Array.Sort(asBackups, Dbglog_CompareByWriteTime);

            /* How many to remove so iMaxBackups - 1 renamed logs remain. */
            iToDelete = asBackups.Length - (iMaxBackups - 1);

            for (i = 0; i < iToDelete; i++)
            {   /* One overflow log per iteration, oldest first. */
                try
                {   /* Best-effort delete. */
                    File.Delete(asBackups[i]);
                }
                catch
                {   /* Locked or gone: the next run tries again. */
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* Dbglog_CompareByWriteTime:                                         */
        /*                                                                    */
        /* Dbglog_RotateOldLog's sort order for the renamed logs: by each     */
        /* file's last-write time, earlier first, so the oldest are pruned    */
        /* first.  The time is read from the file at every comparison rather  */
        /* than from the name's stamp, because the pattern also takes files   */
        /* of that shape that this class did not name.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sPathA : one log's full path.                                  */
        /*     sPathB : the other's.                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : below zero when sPathA was written earlier, zero when    */
        /*           the two times are equal, above zero when it was later.   */
        /*--------------------------------------------------------------------*/
        private static int Dbglog_CompareByWriteTime(string sPathA, string sPathB)
        {
            DateTime dtA;
            DateTime dtB;

            /* Each file's last-write time.  A file deleted meanwhile does   */
            /* not throw: the framework documents a missing file's time as   */
            /* 1601-01-01 (UTC, shown in local time), so it sorts as the     */
            /* oldest and its delete below simply finds nothing.             */
            dtA = File.GetLastWriteTime(sPathA);
            dtB = File.GetLastWriteTime(sPathB);

            /* The two times compared, earlier first. */
            return(dtA.CompareTo(dtB));
        }
    }
}
