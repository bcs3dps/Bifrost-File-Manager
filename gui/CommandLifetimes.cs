/*----------------------------------------------------------------------------*/
/* CommandLifetimes.cs: the UI-thread watch over every window the command     */
/* worker waits on, so a window that disappears can never hold it for good.   */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The command worker holds work while certain windows live: an input's       */
/* picker holds every request (Exit included) until its InputClosed; the      */
/* partition and check windows hold their exclusive mode until their close    */
/* notice; an editor holds its registration until EditorClosed.  Each window  */
/* normally sends that notice from its own closing path.  This file covers    */
/* the case where that path never runs -- the window's owner unwound with an  */
/* exception, a disposal failed half-way, a form was disposed behind its      */
/* owner's back -- by watching the windows themselves and, when one is gone,  */
/* calling the owner's own "it has ended" handler once.                       */
/*                                                                            */
/* Everything here runs on the UI thread: the check is the managed form's     */
/* own state (disposed, or its window destroyed), read where WinForms keeps   */
/* it, never a window handle tested from the worker.  A worker-side test      */
/* would race WinForms re-creating a live form's handle, could be fooled by   */
/* a recycled handle number, and could not tell "window gone" from "its       */
/* owner has finished posting what the window chose".                         */
/*----------------------------------------------------------------------------*/

/* System for IntPtr, EventArgs and the two exceptions (the null-argument   */
/* refusal, and the end handler's failure caught and logged); Collections   */
/* for the watch list; Interop for the one user32 import; Forms for Form    */
/* and the UI-thread Timer.                                                 */
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* LFT_Lost:                                                              */
    /*                                                                        */
    /* The owner's "this window has ended" handler, called once when a        */
    /* watched window is found gone while its owner is no longer responsible  */
    /* for reporting it.  It must be safe to call after the owner's own path  */
    /* already reported the end (every handler wired to this does nothing     */
    /* for a window it no longer knows).                                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     frmLost : the window that has gone.                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the owner has posted its notice (or found nothing to do).   */
    /*------------------------------------------------------------------------*/
    internal delegate void LFT_Lost(Form frmLost);

    /*------------------------------------------------------------------------*/
    /* LFR_clsWatch -- one watched window: the window itself, what to call    */
    /* when it is gone, whether the watch may make that call yet, and the     */
    /* last native handle it was seen with.  Made, changed and read only by   */
    /* LFT_clsLifetimes, on the UI thread.                                    */
    /*------------------------------------------------------------------------*/
    internal sealed class LFR_clsWatch
    {
        /* The watch's facts -- documentation table. */
        internal Form     LFR_frmWatched; // the window watched; never null
        internal LFT_Lost LFR_pfnLost;    // its owner's end handler, called at most once
        internal bool     LFR_bArmed;     // true once no owner stack will report the end itself -- only then may the watch end it
        internal IntPtr   LFR_hWnd;       // the window's last native handle seen on the UI thread; Zero before one existed

        /*--------------------------------------------------------------------*/
        /* LFR_clsWatch:                                                      */
        /*                                                                    */
        /* One watched window and what to call when it is gone.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmWatched : the window.                                       */
        /*     pfnLost    : its owner's end handler.                          */
        /*     bArmed     : whether the watch may end it from the start.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     LFR_clsWatch : the record; its handle is filled in by the      */
        /*                    registry.                                       */
        /*--------------------------------------------------------------------*/
        internal LFR_clsWatch(Form frmWatched, LFT_Lost pfnLost, bool bArmed)
        {
            /* The three facts the registry acts on; the handle comes later. */
            LFR_frmWatched = frmWatched;
            LFR_pfnLost    = pfnLost;
            LFR_bArmed     = bArmed;
            LFR_hWnd       = IntPtr.Zero;
        }
    }

    /*------------------------------------------------------------------------*/
    /* LFT_clsLifetimes -- the watch itself: the list of watched windows, the */
    /* three calls an owner makes (watch, arm, settle), and the two ways a    */
    /* gone window is found -- its Disposed event, and the audit timer for a  */
    /* window destroyed without being disposed.  One is made by the main      */
    /* window and handed to every owner that shows a window the worker waits  */
    /* on.                                                                    */
    /*------------------------------------------------------------------------*/
    internal sealed class LFT_clsLifetimes
    {
        /* The audit's period -- documentation table.  Slow enough to cost       */
        /* nothing while a window lives, quick enough that a lost window holds   */
        /* the worker for no more than a moment; the Disposed event usually      */
        /* ends a watch before any tick.                                         */
        internal const int LFT_AUDIT_MS = 2000; // the backstop audit's period while an armed watch exists, milliseconds

        /* The registry's state -- documentation table. */
        private readonly List<LFR_clsWatch> Lft_lstWatches; // every watched window, in watch order; UI thread only
        private readonly Timer              Lft_tmrAudit;   // UI-thread timer for the backstop audit; running only while an armed watch exists

        /*--------------------------------------------------------------------*/
        /* Lft_IsWindow:                                                      */
        /*                                                                    */
        /* user32's IsWindow: whether a handle still names a window.  Used    */
        /* only by the UI-thread audit, and only together with the form's own */
        /* "no handle" state, so a recycled handle number can only make the   */
        /* audit WAIT longer, never end a live window.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hWnd : the handle last seen for the window.                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true while some window has that handle.                 */
        /*--------------------------------------------------------------------*/
        [DllImport("user32.dll", EntryPoint = "IsWindow")]
        private static extern bool Lft_IsWindow(IntPtr hWnd);

        /*--------------------------------------------------------------------*/
        /* LFT_clsLifetimes:                                                  */
        /*                                                                    */
        /* An empty registry and its stopped audit timer.  Made once by the   */
        /* main window, on the UI thread, before the command worker can       */
        /* publish a view -- the WinForms Timer ticks on the thread that made */
        /* it, which is what lets the audit read forms safely.                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     LFT_clsLifetimes : nothing watched, the timer stopped.         */
        /*--------------------------------------------------------------------*/
        internal LFT_clsLifetimes()
        {
            /* The list, and the timer: its period set and its tick wired to   */
            /* the audit now, but left stopped -- Lft_UpdateTimer starts it    */
            /* only once a watch is armed.                                     */
            Lft_lstWatches        = new List<LFR_clsWatch>();
            Lft_tmrAudit          = new Timer();
            Lft_tmrAudit.Interval = LFT_AUDIT_MS;
            Lft_tmrAudit.Tick    += Lft_OnAuditTick;
        }

        /*---------------------------------------------------------------------*/
        /* LFT_Watch:                                                          */
        /*                                                                     */
        /* Start watching a window the worker waits on.  An UNARMED watch is   */
        /* for a window whose owner is still on the call stack and will report */
        /* its end itself (a modal input's renderer, the partition window's    */
        /* display call): the watch only remembers it, so it can be armed if   */
        /* that owner unwinds without reporting.  An ARMED watch is for a      */
        /* window whose end is reported from an event (the check window's      */
        /* FormClosed, an editor's): if it is disposed without that event,     */
        /* the watch reports it.  Watching a window twice updates the first    */
        /* watch.                                                              */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     frmWatched : the window; must not be disposed.                  */
        /*     pfnLost    : its owner's end handler.                           */
        /*     bArmed     : true for an armed watch (see above).               */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : the window is watched.                                   */
        /*---------------------------------------------------------------------*/
        internal void LFT_Watch(Form frmWatched, LFT_Lost pfnLost, bool bArmed)
        {
            LFR_clsWatch objWatch;

            if (frmWatched == null)
            {   /* Nothing to watch: a caller's error, refused at once rather   */
                /* than left to fail later inside an event handler.             */
                throw new ArgumentNullException("frmWatched");
            }

            if (pfnLost == null)
            {   /* Nobody to tell when the window goes: a caller's error,   */
                /* refused for the same reason.                             */
                throw new ArgumentNullException("pfnLost");
            }

            /* A second watch of the same window replaces the first's terms. */
            objWatch = Lft_Find(frmWatched);

            if (objWatch != null)
            {   /* Already watched: take the new handler and arming. */
                objWatch.LFR_pfnLost = pfnLost;
                objWatch.LFR_bArmed  = bArmed;
            }
            else
            {   /* New: record it and follow its handle and its disposal. */
                objWatch = new LFR_clsWatch(frmWatched, pfnLost, bArmed);
                Lft_lstWatches.Add(objWatch);
                frmWatched.HandleCreated += Lft_OnHandleCreated;
                frmWatched.Disposed      += Lft_OnDisposed;

                if (frmWatched.IsHandleCreated)
                {   /* Already has a window: remember its handle now. */
                    objWatch.LFR_hWnd = frmWatched.Handle;
                }
            }

            /* The audit runs only while some watch is armed. */
            Lft_UpdateTimer();
        }

        /*--------------------------------------------------------------------*/
        /* LFT_Arm:                                                           */
        /*                                                                    */
        /* The window's owner has unwound without reporting its end (an       */
        /* exception, a refused or failed close): from now on the watch ends  */
        /* the window's lifetime when it is gone -- at once, if it already    */
        /* is.  A window not watched is ignored.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmWatched : the watched window.                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the watch is armed, or ended if the window is gone.     */
        /*--------------------------------------------------------------------*/
        internal void LFT_Arm(Form frmWatched)
        {
            LFR_clsWatch objWatch;

            /* The window's watch, if it still has one. */
            objWatch = Lft_Find(frmWatched);

            if (objWatch == null)
            {   /* Not watched (already ended, or never): nothing to arm. */
                return;
            }

            /* Armed; a window already gone is ended now rather than at a tick. */
            objWatch.LFR_bArmed = true;

            if (Lft_IsGone(objWatch))
            {   /* Gone already: end it. */
                Lft_End(objWatch);
                return;
            }

            /* Still there: the audit and its disposal will catch the end. */
            Lft_UpdateTimer();
        }

        /*--------------------------------------------------------------------*/
        /* LFT_Settle:                                                        */
        /*                                                                    */
        /* The window's owner has reported its end itself: stop watching it,  */
        /* calling nothing.  A window not watched is ignored.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmWatched : the watched window.                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window is no longer watched.                        */
        /*--------------------------------------------------------------------*/
        internal void LFT_Settle(Form frmWatched)
        {
            LFR_clsWatch objWatch;

            /* The window's watch, if it still has one: the owner may settle   */
            /* a window the watch has already ended, which is then a no-op.    */
            objWatch = Lft_Find(frmWatched);

            if (objWatch != null)
            {   /* Watched: forget it without calling its handler. */
                Lft_Forget(objWatch);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Lft_Find:                                                          */
        /*                                                                    */
        /* The watch of a window, by reference.                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmWatched : the window, or null.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     LFR_clsWatch : its watch, or null when it is not watched.      */
        /*--------------------------------------------------------------------*/
        private LFR_clsWatch Lft_Find(Form frmWatched)
        {
            int i;

            for (i = 0; i < Lft_lstWatches.Count; i++)
            {   /* One watch compared per iteration. */
                if (Lft_lstWatches[i].LFR_frmWatched == frmWatched)
                {   /* This window's watch. */
                    return(Lft_lstWatches[i]);
                }
            }

            /* Not watched. */
            return(null);
        }

        /*--------------------------------------------------------------------*/
        /* Lft_IsGone:                                                        */
        /*                                                                    */
        /* Whether a watched window has ended: disposed, or -- having had a   */
        /* window -- holding no window now while its last handle names none.  */
        /* A window that never had a handle is NOT gone: it may simply not be */
        /* shown yet.  It must never call a live window gone: that would      */
        /* release the worker while the window can still post its choice.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objWatch : the watch.                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the window has ended.                         */
        /*--------------------------------------------------------------------*/
        private static bool Lft_IsGone(LFR_clsWatch objWatch)
        {
            Form frmWatched;

            /* The watched window, whose own state is the test. */
            frmWatched = objWatch.LFR_frmWatched;

            if (frmWatched.IsDisposed)
            {   /* Disposed: ended whatever its handle. */
                return(true);
            }

            /* Destroyed but not disposed: it had a window, has none, and the    */
            /* old handle names nothing.  All three are needed.  The form's      */
            /* own "no handle" alone would be true for a moment while WinForms   */
            /* re-creates a live window's handle -- but this runs on the UI      */
            /* thread, which is the thread doing any re-creation, so it never    */
            /* sees one half-way.  The handle test alone could be fooled by a    */
            /* recycled handle number; joined to the form's own state, a         */
            /* recycled number can only make the end come later, never fake      */
            /* one.                                                              */
            return(objWatch.LFR_hWnd != IntPtr.Zero && !frmWatched.IsHandleCreated &&
                   !Lft_IsWindow(objWatch.LFR_hWnd));
        }

        /*--------------------------------------------------------------------*/
        /* Lft_End:                                                           */
        /*                                                                    */
        /* End a watched window's lifetime: the watch is dropped and its      */
        /* owner's end handler called, once.  A handler that throws is        */
        /* logged, never passed on to the caller.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objWatch : the watch to end.                                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the owner has been told.                                */
        /*--------------------------------------------------------------------*/
        private void Lft_End(LFR_clsWatch objWatch)
        {
            /* Out of the list and its events BEFORE the handler runs: a     */
            /* handler that disposes the window raises its Disposed event,   */
            /* and with the watch still listed that would end it a second    */
            /* time.                                                         */
            Lft_Forget(objWatch);

            /* Seen by whoever reads the debug log: the owner's own path did not   */
            /* report this window, the watch did.                                  */
            if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
            {   /* Only while the debug log is kept: otherwise the line is never built. */
                DBGLOG_clsDebugLog.DBGLOG_Write("window watch",
                    "a " + objWatch.LFR_frmWatched.GetType().Name + " ended without its owner reporting it; ending its lifetime");
            }

            try
            {   /* The owner's notice, through its own handler. */
                objWatch.LFR_pfnLost(objWatch.LFR_frmWatched);
            }
            catch (Exception exLost)
            {   /* The handler failed: logged, and not passed on.  This runs     */
                /* from a Disposed event or a timer tick, where an exception     */
                /* would reach the message loop, or from LFT_Arm on an           */
                /* owner's unwinding path, where it would replace the failure    */
                /* that owner is already handling.  The watch is already gone.   */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("window watch", exLost.ToString());
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* Lft_Forget:                                                        */
        /*                                                                    */
        /* Drop a watch: out of the list, its two event handlers detached,    */
        /* the audit stopped if nothing armed remains.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objWatch : the watch.                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the window is no longer watched.                        */
        /*--------------------------------------------------------------------*/
        private void Lft_Forget(LFR_clsWatch objWatch)
        {
            /* The record and its subscriptions go together. */
            Lft_lstWatches.Remove(objWatch);
            objWatch.LFR_frmWatched.HandleCreated -= Lft_OnHandleCreated;
            objWatch.LFR_frmWatched.Disposed      -= Lft_OnDisposed;

            /* The audit runs only while some watch is armed. */
            Lft_UpdateTimer();
        }

        /*---------------------------------------------------------------------*/
        /* Lft_UpdateTimer:                                                    */
        /*                                                                     */
        /* Run the audit while at least one watch is armed and stop it         */
        /* otherwise: an unarmed watch's owner reports its own end, so ticking */
        /* for it would be wasted.                                             */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     None.                                                           */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : the timer runs exactly when an armed watch exists.       */
        /*---------------------------------------------------------------------*/
        private void Lft_UpdateTimer()
        {
            bool bAnyArmed;
            int  i;

            /* None armed until one is found. */
            bAnyArmed = false;

            for (i = 0; i < Lft_lstWatches.Count; i++)
            {   /* One watch per iteration; the first armed one decides. */
                if (Lft_lstWatches[i].LFR_bArmed)
                {   /* Armed: the audit is needed. */
                    bAnyArmed = true;
                    break;
                }
            }

            /* Start or stop (setting the same value again is harmless). */
            Lft_tmrAudit.Enabled = bAnyArmed;
        }

        /*--------------------------------------------------------------------*/
        /* Lft_OnHandleCreated:                                               */
        /*                                                                    */
        /* A watched window got a native window (shown, or re-created by      */
        /* WinForms): remember the new handle for the audit's test.  One      */
        /* handler serves every watched window, because the windows are made  */
        /* at run time and a handler of their own each would mean keeping a   */
        /* second list beside the watch list.                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the window.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the watch holds the current handle.                     */
        /*--------------------------------------------------------------------*/
        private void Lft_OnHandleCreated(object objSender, EventArgs evArgs)
        {
            LFR_clsWatch objWatch;

            /* The sender's watch: the event is detached when a watch ends,   */
            /* so normally there is one.                                      */
            objWatch = Lft_Find(objSender as Form);

            if (objWatch != null)
            {   /* Still watched: take the handle it has now. */
                objWatch.LFR_hWnd = objWatch.LFR_frmWatched.Handle;
            }
        }

        /*--------------------------------------------------------------------*/
        /* Lft_OnDisposed:                                                    */
        /*                                                                    */
        /* A watched window was disposed.  Armed, its lifetime ends now.      */
        /* Unarmed, its owner is still on the call stack and will report the  */
        /* end (or unwind and arm the watch, which then ends it at once) --   */
        /* so nothing is done here, which is what keeps a modal input's       */
        /* chosen value ahead of its InputClosed.  Shared by every watched    */
        /* window, as Lft_OnHandleCreated is.                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the window.                                        */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : an armed watch is ended.                                */
        /*--------------------------------------------------------------------*/
        private void Lft_OnDisposed(object objSender, EventArgs evArgs)
        {
            LFR_clsWatch objWatch;

            /* The sender's watch, if it is still watched. */
            objWatch = Lft_Find(objSender as Form);

            if (objWatch != null && objWatch.LFR_bArmed)
            {   /* Armed and now gone: end it. */
                Lft_End(objWatch);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Lft_OnAuditTick:                                                   */
        /*                                                                    */
        /* The backstop: every armed watch whose window has ended without a   */
        /* Disposed event (its window destroyed but the form never disposed)  */
        /* is ended.                                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objSender : the timer.                                         */
        /*     evArgs    : unused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : every gone armed window is ended.                       */
        /*--------------------------------------------------------------------*/
        private void Lft_OnAuditTick(object objSender, EventArgs evArgs)
        {
            LFR_clsWatch[] arrWatches;
            int            i;

            /* The list is walked from a COPY: ending a watch removes it from   */
            /* the list, and its owner's handler may watch or settle other      */
            /* windows, so the live list can change under the loop.             */
            arrWatches = Lft_lstWatches.ToArray();

            for (i = 0; i < arrWatches.Length; i++)
            {   /* One watch per iteration; skip any already ended this tick. */
                if (arrWatches[i].LFR_bArmed && Lft_lstWatches.Contains(arrWatches[i]) && Lft_IsGone(arrWatches[i]))
                {   /* Armed, still listed, and gone: end it. */
                    Lft_End(arrWatches[i]);
                }
            }
        }
    }
}
