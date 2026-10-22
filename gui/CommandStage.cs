/*----------------------------------------------------------------------------*/
/* CommandStage.cs                                                            */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The staged files a command takes its content from -- an editor's save, a   */
/* new file's first content -- and the local writer that makes one in the     */
/* background.  A stage is a file of its own under the program's staging      */
/* folder, written completely, flushed to disk and closed BEFORE any command  */
/* gets it; its token (STG_clsStage) owns the file and deletes it exactly     */
/* once, whether the command uses it, refuses it, or the job making it is     */
/* abandoned.  The writer reads only what the editor captured, never its      */
/* controls.                                                                  */
/*----------------------------------------------------------------------------*/

/* The framework: ArgumentException, Guid, Math and the exceptions (System),   */
/* the hex editor's patch (Collections.Generic), the files and streams (IO),   */
/* and the atomic exchanges between the writer and the UI (Threading).  The    */
/* debug log (gui\lib\DebugLog.cs) is in this program's own namespace.         */
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* STG_WriteContent:                                                      */
    /*                                                                        */
    /* The shape of a stage's writer: it writes the whole content into the    */
    /* new stage's stream (an editor's serialiser, or the stage job's own).   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     stmOutput : the new stage's stream, lent for the call; the writer  */
    /*                 neither flushes nor closes it.                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the content is written, or an exception says why not --     */
    /*            STG_WriteSnapshot then deletes the partial file.            */
    /*------------------------------------------------------------------------*/
    internal delegate void STG_WriteContent(FileStream stmOutput);

    /*------------------------------------------------------------------------*/
    /* STG_clsStage -- a staged file and the token that owns it: the one      */
    /* that may delete it, once.  A transfer hands the ownership to a new     */
    /* token for the same file and leaves this one with nothing to do --      */
    /* documentation table.                                                   */
    /*------------------------------------------------------------------------*/
    internal sealed class STG_clsStage
    {
        private string Stg_sPath;   // the file's full path (a GUID name in the staging folder); kept after a transfer
        private int Stg_iReleased;  // 0: this token owns the file; 1: released or transferred (set atomically, once)

        /*--------------------------------------------------------------------*/
        /* STG_clsStage:                                                      */
        /*                                                                    */
        /* A token owning the file at a path.  Private: tokens are made only  */
        /* by this class -- for a file it has just created, or by a transfer. */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sPath : the file's path.                                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the token, owning; nothing is opened or        */
        /*                     written.                                       */
        /*--------------------------------------------------------------------*/
        private STG_clsStage(string sPath)
        {
            /* The path as given, and the ownership not yet used. */
            Stg_sPath = sPath;
            Stg_iReleased = 0;
        }

        /*--------------------------------------------------------------------*/
        /* STG_ReadPath:                                                      */
        /*                                                                    */
        /* The staged file's path, for whoever reads its content.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the path; the file is not opened.                     */
        /*--------------------------------------------------------------------*/
        internal string STG_ReadPath()
        {
            /* Only the path: the right to delete stays with the token, and    */
            /* the reader must keep the token until it has finished reading.   */
            return(Stg_sPath);
        }

        /*--------------------------------------------------------------------*/
        /* Stg_CreateFile:                                                    */
        /*                                                                    */
        /* Create a new stage file -- a GUID name in the staging folder,      */
        /* made with CreateNew so an existing file is never taken over --     */
        /* and its token.                                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sRoot     : the staging folder (made if missing).              */
        /*     stmOutput : receives the open stream (read / write, others may */
        /*                 read); the caller closes it.                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     STG_clsStage : the token; an exception when there is no folder */
        /*                    or the file cannot be created, with no token    */
        /*                    and nothing to delete.                          */
        /*--------------------------------------------------------------------*/
        private static STG_clsStage Stg_CreateFile(string sRoot, out FileStream stmOutput)
        {
            string sPath;
            STG_clsStage objStage;

            /* No stream until the file exists. */
            stmOutput = null;
            if (String.IsNullOrEmpty(sRoot))
            {   /* No staging folder: refused, nothing created. */
                throw new ArgumentException("A private staging root is required.");
            }

            /* The folder (as a full path, made if missing), a new GUID name   */
            /* in it, the token, and the file created NEW: CreateNew throws    */
            /* if a file of that name exists, so none is ever adopted -- the   */
            /* token is then dropped unreturned, with nothing to delete.       */
            sRoot = Path.GetFullPath(sRoot);
            Directory.CreateDirectory(sRoot);
            sPath = Path.Combine(sRoot, Guid.NewGuid().ToString("N") + ".snapshot");
            objStage = new STG_clsStage(sPath);
            stmOutput = new FileStream(sPath, FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.Read);

            /* The token and the open stream, both the caller's now. */
            return(objStage);
        }

        /*--------------------------------------------------------------------*/
        /* Stg_ReleaseFailed:                                                 */
        /*                                                                    */
        /* Delete the file of a stage that failed while being written,        */
        /* without letting a failure to delete hide why the write failed.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objStage : the token, or null when the file was never created. */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the file deleted, or the deletion's failure logged; the */
        /*            caller then rethrows the write's own failure.           */
        /*--------------------------------------------------------------------*/
        private static void Stg_ReleaseFailed(STG_clsStage objStage)
        {
            if (objStage == null)
            {   /* No file was created (or it already existed and is not   */
                /* ours): nothing to delete.                               */
                return;
            }

            try
            {   /* The stream is already closed -- the caller's using block   */
                /* ended before its catch -- so the file can be deleted.      */
                objStage.STG_Release();
            }
            catch (Exception exCleanup)
            {   /* Could not delete: logged only, so the write's own   */
                /* failure is the one the caller reports.              */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("snapshot cleanup", exCleanup.ToString());
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* STG_WriteSnapshot:                                                 */
        /*                                                                    */
        /* Make a stage from a writer: a new file, the writer's content,      */
        /* flushed to disk and closed.  A stage is returned only whole.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sRoot    : the staging folder.                                 */
        /*     pfnWrite : the content's writer, called once, here; it is not  */
        /*                kept.                                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     STG_clsStage : the stage; on any failure the partial file is   */
        /*                    deleted and the exception rethrown.             */
        /*--------------------------------------------------------------------*/
        internal static STG_clsStage STG_WriteSnapshot(string sRoot, STG_WriteContent pfnWrite)
        {
            STG_clsStage objStage;
            FileStream stmOutput;

            if (pfnWrite == null)
            {   /* No writer: refused before a file is made. */
                throw new ArgumentException("A snapshot writer is required.");
            }

            /* No token until the file has been created. */
            objStage = null;
            try
            {   /* The file made, written and flushed; a failure at any of   */
                /* these goes to the one catch below.                        */
                objStage = Stg_CreateFile(sRoot, out stmOutput);
                using (stmOutput)
                {   /* The writer fills the stream, then Flush(true) puts the   */
                    /* content on the disk itself (not only the OS cache);      */
                    /* the block's end closes it -- before it is handed on.     */
                    pfnWrite(stmOutput);
                    stmOutput.Flush(true);
                }
            }
            catch
            {   /* A part-written file is no command's input: deleted (its   */
                /* stream already closed), and the failure rethrown as it    */
                /* was.                                                      */
                Stg_ReleaseFailed(objStage);
                throw;
            }

            /* Written whole, flushed and closed: the stage. */
            return(objStage);
        }

        /*--------------------------------------------------------------------*/
        /* STG_CopySnapshot:                                                  */
        /*                                                                    */
        /* Make a stage that is a copy of a local file (an editor's work      */
        /* file): a new file, the source's bytes, flushed and closed.  The    */
        /* source is only read.                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sRoot   : the staging folder.                                  */
        /*     sSource : the file to copy; the caller keeps it unchanged      */
        /*               while this runs.                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     STG_clsStage : the stage; on any failure the partial file is   */
        /*                    deleted and the exception rethrown.             */
        /*--------------------------------------------------------------------*/
        internal static STG_clsStage STG_CopySnapshot(string sRoot, string sSource)
        {
            STG_clsStage objStage;
            FileStream stmOutput;
            FileStream stmInput;

            /* No token until the file has been created. */
            objStage = null;
            try
            {   /* The file made, the source copied in and flushed; a       */
                /* failure at any of these goes to the catch below, after   */
                /* both streams have been closed by their using blocks.     */
                objStage = Stg_CreateFile(sRoot, out stmOutput);
                using (stmOutput)
                {   /* The stage stays open through the copy and the flush.    */
                    /* The source opened read-only, sharing read AND write     */
                    /* -- the editor keeps its own handle on it (opened        */
                    /* read with read-write sharing), which a narrower share   */
                    /* would collide with.                                     */
                    stmInput = new FileStream(sSource, FileMode.Open,
                        FileAccess.Read, FileShare.ReadWrite);
                    using (stmInput)
                    {   /* Copied whole; the source is closed at the block's   */
                        /* end and never deleted.                              */
                        stmInput.CopyTo(stmOutput);
                    }

                    /* The copy on the disk itself before it is handed on. */
                    stmOutput.Flush(true);
                }
            }
            catch
            {   /* The partial copy deleted, the failure rethrown as it was. */
                Stg_ReleaseFailed(objStage);
                throw;
            }

            /* A whole copy, flushed and closed: the stage.  The source is   */
            /* as it was.                                                    */
            return(objStage);
        }

        /*--------------------------------------------------------------------*/
        /* STG_Transfer:                                                      */
        /*                                                                    */
        /* Hand this stage's file to a new owner -- a new file's first        */
        /* content passing from its create request to the viewer that opens   */
        /* it (CommandNativeOperations.cs).  This token then owns nothing.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     STG_clsStage : the new token, now the file's only owner;       */
        /*                    ObjectDisposedException when this token was     */
        /*                    already released or transferred.                */
        /*--------------------------------------------------------------------*/
        internal STG_clsStage STG_Transfer()
        {
            STG_clsStage objOwner;

            /* The new token made FIRST: should making it fail, this token   */
            /* still owns the file and the request's clean-up deletes it.    */
            /* Then this token's ownership taken, atomically -- only one     */
            /* transfer or release can ever succeed.                         */
            objOwner = new STG_clsStage(Stg_sPath);
            if (Interlocked.CompareExchange(ref Stg_iReleased, 1, 0) != 0)
            {   /* Already released or transferred: nothing to hand on. */
                throw new ObjectDisposedException("Save snapshot ownership");
            }

            /* The new owner.  The request's release of this token now does   */
            /* nothing; the viewer's release deletes the file.                */
            return(objOwner);
        }

        /*--------------------------------------------------------------------*/
        /* STG_Release:                                                       */
        /*                                                                    */
        /* Use this token's ownership: delete its file.  Only the first       */
        /* release (or a transfer) counts; later ones do nothing.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the file deleted; a failed deletion throws, the token   */
        /*            used all the same.                                      */
        /*--------------------------------------------------------------------*/
        internal void STG_Release()
        {
            if (Interlocked.Exchange(ref Stg_iReleased, 1) != 0)
            {   /* Already released or transferred: nothing to delete. */
                return;
            }

            /* The file deleted.  A failure here is a clean-up failure, for   */
            /* the caller to report or log -- never a reason to repeat the    */
            /* save it served.                                                */
            File.Delete(Stg_sPath);
        }
    }

    /*------------------------------------------------------------------------*/
    /* SJB_clsStageJob -- the job that writes a stage in the background, so a */
    /* large save does not hold the editor's window still.  Its two users:    */
    /* the hex editor's save (HexEditForm.cs), which writes the editor's file */
    /* with its changed bytes laid over it, and the structured editor's save  */
    /* to an image entry (json\JsonEditForm.cs), which writes a whole         */
    /* document's bytes.  The result passes to the UI through one atomic      */
    /* exchange, so the stage is taken by exactly one side -- the UI, or the  */
    /* writer deleting it when the UI has given it up -- documentation table. */
    /*------------------------------------------------------------------------*/
    internal sealed class SJB_clsStageJob
    {
        private const int SJB_STAGE_CHUNK = 1048576;   // bytes per read / write (1 MiB); also how often the job checks it has been given up
        private string Sjb_sRoot;                      // the staging folder
        private byte[] Sjb_abBytes;                    // a whole document's bytes (the structured editor's); null for a file and patch (the hex editor's)
        private string Sjb_sSource;                    // the editor's file the patch is laid over; null with whole bytes
        private Dictionary<long, byte> Sjb_dictPatch;  // the changed bytes, offset -> value, captured by the editor; only read
        private object Sjb_objClosed;                  // the marker that says the result has been taken or given up
        private object Sjb_objResult;                  // null while the job runs; then the finished STG_clsStage, or Sjb_objClosed once taken / given up
        private int Sjb_iAborted;                      // 1 once SJB_Abort was called (a user's cancel), told apart from a failed write

        /*--------------------------------------------------------------------*/
        /* SJB_clsStageJob:                                                   */
        /*                                                                    */
        /* A job for either whole bytes or a file and a patch -- exactly one  */
        /* of the two.                                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sRoot     : the staging folder.                                */
        /*     abBytes   : a whole document's bytes (not changed after), or   */
        /*                 null.                                              */
        /*     sSource   : the file the patch is laid over, or null with      */
        /*                 bytes.                                             */
        /*     dictPatch : the changed bytes, or null with bytes.             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the job, not started; ArgumentException when   */
        /*                     both forms, or neither, are given.             */
        /*--------------------------------------------------------------------*/
        internal SJB_clsStageJob(string sRoot, byte[] abBytes,
            string sSource, Dictionary<long, byte> dictPatch)
        {
            if ((abBytes != null && (sSource != null || dictPatch != null)) ||
                (abBytes == null && (String.IsNullOrEmpty(sSource) || dictPatch == null)))
            {   /* Bytes with a file or patch too, or no bytes without both   */
                /* a file and a patch: refused before anything is written.    */
                throw new ArgumentException("Supply serialized bytes or an original file and patch, exclusively.");
            }

            /* The inputs, kept by reference, not copied: the editor captured   */
            /* them before showing the busy box (its patch is a dictionary of   */
            /* its own), and nothing changes them while the job runs; the       */
            /* editor reads the same patch afterwards.  No window control is    */
            /* touched.  Then the result's marker, and no result yet.           */
            Sjb_sRoot = sRoot;
            Sjb_abBytes = abBytes;
            Sjb_sSource = sSource;
            Sjb_dictPatch = dictPatch;
            Sjb_objClosed = new object();
            Sjb_objResult = null;
            Sjb_iAborted = 0;
        }

        /*--------------------------------------------------------------------*/
        /* SJB_Run:                                                           */
        /*                                                                    */
        /* The job's work, on its background thread: make the stage and hand  */
        /* it over -- or, when the UI has given it up meanwhile, delete it.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the stage published; or deleted, given up; or an        */
        /*            exception (a failure, or OperationCanceledException     */
        /*            when given up part-way), with the partial file deleted. */
        /*--------------------------------------------------------------------*/
        internal void SJB_Run()
        {
            STG_clsStage objStage;

            /* Given up already: stop before a file is made.  A give-up      */
            /* during the writing is caught at the next chunk (Sjb_Write),   */
            /* and STG_WriteSnapshot deletes the partial file on any         */
            /* exception.                                                    */
            Sjb_CheckAbandoned();
            objStage = STG_clsStage.STG_WriteSnapshot(Sjb_sRoot, new STG_WriteContent(Sjb_Write));
            if (Interlocked.CompareExchange(ref Sjb_objResult, objStage, null) != null)
            {   /* Published only if the result is still empty: it is not   */
                /* -- the UI took or gave up the result while the job ran   */
                /* -- so no one will take this stage, and the job deletes   */
                /* it.                                                      */
                objStage.STG_Release();
            }
        }

        /*--------------------------------------------------------------------*/
        /* Sjb_Write:                                                         */
        /*                                                                    */
        /* The stage's content: the whole bytes, or the editor's file copied  */
        /* with each changed byte written over it -- a chunk at a time, with  */
        /* a give-up check before each.                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stmOutput : the new stage's stream, lent by STG_WriteSnapshot. */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the content written; or an exception -- given up, an    */
        /*            I/O failure, or a change outside the file -- before the */
        /*            stage is published.                                     */
        /*--------------------------------------------------------------------*/
        private void Sjb_Write(FileStream stmOutput)
        {
            byte[] abBuffer;
            FileStream stmInput;
            Dictionary<long, byte>.Enumerator stIterator;
            KeyValuePair<long, byte> stPatch;
            int iRead;
            int i;

            if (Sjb_abBytes != null)
            {   /* Whole bytes (the structured editor's save to an image   */
                /* entry, json\JsonEditForm.cs): written out in chunks.    */
                for (i = 0; i < Sjb_abBytes.Length; i += iRead)
                {   /* One chunk per iteration, after the give-up check. */
                    Sjb_CheckAbandoned();
                    iRead = Math.Min(SJB_STAGE_CHUNK, Sjb_abBytes.Length - i);
                    stmOutput.Write(Sjb_abBytes, i, iRead);
                }

                /* All written; the flush and close are STG_WriteSnapshot's. */
                return;
            }

            /* A file and patch: first the editor's file copied whole, so the   */
            /* stage has its length and content.  Opened read-only, sharing     */
            /* read and write -- the editor keeps its own handle on it          */
            /* (HexEditForm.cs opens it read, read-write sharing).              */
            abBuffer = new byte[SJB_STAGE_CHUNK];
            stmInput = new FileStream(Sjb_sSource, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite);
            using (stmInput)
            {   /* The file closed at the block's end, before the patch. */
                for (;;)
                {   /* One chunk per iteration, after the give-up check. */
                    Sjb_CheckAbandoned();
                    iRead = stmInput.Read(abBuffer, 0, abBuffer.Length);
                    if (iRead == 0)
                    {   /* The end of the file: all of it is in the stage. */
                        break;
                    }

                    /* The chunk, as read. */
                    stmOutput.Write(abBuffer, 0, iRead);
                }
            }

            /* Then each changed byte, written at its offset.  The patch's own   */
            /* enumerator (a struct, no interface call), not a copy of its       */
            /* keys -- every entry is an independent offset, so the order does   */
            /* not matter.                                                       */
            stIterator = Sjb_dictPatch.GetEnumerator();
            try
            {   /* The enumerator released by the finally, however the loop   */
                /* ends.                                                      */
                while (stIterator.MoveNext())
                {   /* One changed byte per iteration, after the give-up   */
                    /* check.                                              */
                    Sjb_CheckAbandoned();
                    stPatch = stIterator.Current;
                    if (stPatch.Key < 0 || stPatch.Key >= stmOutput.Length)
                    {   /* An offset outside the file: the hex editor only     */
                        /* overwrites, never extends, so this is bad data --   */
                        /* refused, and the stage is not made.                 */
                        throw new InvalidDataException("A captured hex edit is outside its baseline file.");
                    }

                    /* The byte written at its offset. */
                    stmOutput.Position = stPatch.Key;
                    stmOutput.WriteByte(stPatch.Value);
                }
            }
            finally
            {   /* Released even after a refusal or a give-up. */
                stIterator.Dispose();
            }
        }

        /*--------------------------------------------------------------------*/
        /* Sjb_CheckAbandoned:                                                */
        /*                                                                    */
        /* Stop the job if the UI has given up its result.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : to go on; OperationCanceledException when given up      */
        /*            (STG_WriteSnapshot then deletes the partial file).      */
        /*--------------------------------------------------------------------*/
        private void Sjb_CheckAbandoned()
        {
            if (Interlocked.CompareExchange(ref Sjb_objResult, null, null) == Sjb_objClosed)
            {   /* The result read atomically (a compare that never changes   */
                /* it): the closed marker -- no one will take this file --    */
                /* so stop.                                                   */
                throw new OperationCanceledException("Save snapshot abandoned.");
            }
        }

        /*--------------------------------------------------------------------*/
        /* SJB_Take:                                                          */
        /*                                                                    */
        /* The UI's side: take the finished stage -- and, by the same act,    */
        /* give the result up if it is not finished, so the job deletes       */
        /* whatever it makes later.  Called once the busy box returns.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     STG_clsStage : the stage, now the caller's; null when the job  */
        /*                    had not finished, failed, or the result was     */
        /*                    already taken.                                  */
        /*--------------------------------------------------------------------*/
        internal STG_clsStage SJB_Take()
        {
            object objResult;

            /* One atomic exchange puts the closed marker in and takes out      */
            /* what was there: a finished stage (now the caller's), null (the   */
            /* job has not finished -- it will see the marker and delete its    */
            /* file), or the marker itself (taken before).                      */
            objResult = Interlocked.Exchange(ref Sjb_objResult, Sjb_objClosed);

            /* The stage, or null for the other two. */
            return(objResult as STG_clsStage);
        }

        /*--------------------------------------------------------------------*/
        /* SJB_Abort:                                                         */
        /*                                                                    */
        /* The user's cancel (an Exit agreed to while the save was being      */
        /* written): recorded as such, and the result given up -- a finished  */
        /* stage no one has taken is deleted here.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the cancel recorded; a job still writing stops at its   */
        /*            next chunk or byte and deletes its partial file.        */
        /*--------------------------------------------------------------------*/
        internal void SJB_Abort()
        {
            STG_clsStage objStage;

            /* The cancel recorded first -- so the editor can tell it from a    */
            /* failed write -- then the result taken: a finished stage comes    */
            /* back (only one taker ever gets it), anything else is given up.   */
            Interlocked.Exchange(ref Sjb_iAborted, 1);
            objStage = SJB_Take();
            if (objStage != null)
            {   /* A finished stage no command received: deleted here. */
                try
                {   /* This job's own file only. */
                    objStage.STG_Release();
                }
                catch (Exception exCleanup)
                {   /* Could not delete: logged; the cancel stands.  Whether   */
                    /* anything later clears the staging folder is not         */
                    /* established here.                                       */
                    if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                    {   /* Only while the debug log is kept: otherwise the line is never built. */
                        DBGLOG_clsDebugLog.DBGLOG_Write("snapshot abort cleanup", exCleanup.ToString());
                    }
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* SJB_WasAborted:                                                    */
        /*                                                                    */
        /* Whether the job was cancelled by the user (SJB_Abort), as against  */
        /* failing.                                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true only after SJB_Abort.                              */
        /*--------------------------------------------------------------------*/
        internal bool SJB_WasAborted()
        {
            /* Read atomically (a compare that never changes it), to see the   */
            /* UI thread's write.  A failure without a cancel stays a          */
            /* failure: the editor reports it rather than going quiet.         */
            return(Interlocked.CompareExchange(ref Sjb_iAborted, 0, 0) != 0);
        }
    }
}
