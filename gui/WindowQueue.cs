/*----------------------------------------------------------------------------*/
/* WindowQueue.cs                                                             */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* A window's own command queue.  The three editors (TextEditForm.cs,         */
/* HexEditForm.cs, json\JsonEditForm.cs) and the dialogs with work of their   */
/* own (the partition dialog and the disk picker in PartitionForm.cs, New     */
/* Image, Filesystem Properties, the image Save As, the Add Partition         */
/* dialog, the structured editor's Find window) and the debug log window      */
/* (gui\lib\DebugLogForm.cs) each keep one: its controls' handlers only post  */
/* an item to it and return, and the queue runs the items one at a time,      */
/* each to its end, on the window's own thread (the program's one UI          */
/* thread).  A window talks to the main command queue only by posting a       */
/* request from an item, and the reply comes back as another item -- so no    */
/* work is done in a handler, a second click while an item waits or runs is   */
/* dropped, and one item never runs inside another, whatever message loop an  */
/* item's own dialog pumps.                                                   */
/*----------------------------------------------------------------------------*/

/* The framework: the base types and the exceptions caught (System), the     */
/* waiting list (Collections.Generic), and the window the items are posted   */
/* to with BeginInvoke (Windows.Forms' Control and MethodInvoker).           */
using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace BifrostFM
{

    /*------------------------------------------------------------------------*/
    /* WQU_FnRun:                                                             */
    /*                                                                        */
    /* The runner a window gives its queue: one named function of the window  */
    /* that runs one item to its end -- a switch over the window's own item   */
    /* numbers, each case calling that item's named function -- so which code */
    /* an item runs is written in the window, not chosen at run time.         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iItem   : the window's own item number, as posted.                 */
    /*     objArgs : the arguments posted with it; null for none.             */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item has run to its end.                                */
    /*------------------------------------------------------------------------*/
    internal delegate void WQU_FnRun(int iItem, object objArgs);

    /*------------------------------------------------------------------------*/
    /* WQU_clsWindowQueue -- one window's queue of its own commands.  Not     */
    /* thread-safe and not meant to be: every call is made on the window's    */
    /* thread, which is the one thread that ever touches it -- documentation  */
    /* table.                                                                 */
    /*------------------------------------------------------------------------*/
    internal sealed class WQU_clsWindowQueue
    {
        private readonly Control Wqu_ctlWindow;          // the window the drain is posted to (BeginInvoke) and whose thread runs every item
        private readonly WQU_FnRun Wqu_pfnRun;           // the window's runner, called for one item at a time
        private readonly List<int> Wqu_lstItems;         // the items waiting, oldest first (the window's own item numbers)
        private readonly List<object> Wqu_lstArgs;       // each waiting item's arguments, by the same index; null for none
        private bool Wqu_bRunning;                       // true while an item runs: a drain that finds it set does nothing, so items never nest
        private int Wqu_iRunningItem;                    // the item running (meaningful only while Wqu_bRunning)
        private object Wqu_objRunningArgs;               // its arguments
        private bool Wqu_bDrainPosted;                   // true once a drain is posted and has not yet run: no second is posted meanwhile

        /*--------------------------------------------------------------------*/
        /* WQU_clsWindowQueue:                                                */
        /*                                                                    */
        /* An empty queue for one window, which runs its items through the    */
        /* window's runner.                                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     ctlWindow : the window; its handle should exist before the     */
        /*                 first item is posted (BeginInvoke needs it) --     */
        /*                 an item posted earlier waits until a later post,   */
        /*                 new or a repeat, finds the handle.                 */
        /*     pfnRun    : the window's runner.                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the queue; an ArgumentNullException for a      */
        /*                     missing window or runner.                      */
        /*--------------------------------------------------------------------*/
        internal WQU_clsWindowQueue(Control ctlWindow, WQU_FnRun pfnRun)
        {
            if (ctlWindow == null)
            {   /* Nothing to post to: refused at once, rather than failing   */
                /* at the first click.                                        */
                throw new ArgumentNullException("ctlWindow");
            }

            if (pfnRun == null)
            {   /* Nothing to run the items: refused likewise. */
                throw new ArgumentNullException("pfnRun");
            }

            /* The window and its runner kept; nothing waiting, nothing   */
            /* running, no drain posted.                                  */
            Wqu_ctlWindow = ctlWindow;
            Wqu_pfnRun = pfnRun;
            Wqu_lstItems = new List<int>();
            Wqu_lstArgs = new List<object>();
            Wqu_bRunning = false;
            Wqu_iRunningItem = 0;
            Wqu_objRunningArgs = null;
            Wqu_bDrainPosted = false;
        }

        /*--------------------------------------------------------------------*/
        /* WQU_Post:                                                          */
        /*                                                                    */
        /* A window's handler records one item and returns: the item is added */
        /* to the queue and a drain posted to run it later, never now.  An    */
        /* item equal to one waiting or running -- the same number and equal  */
        /* arguments -- is dropped, which is what makes a second click while  */
        /* the first is still in hand do nothing.  A repeat of a WAITING item */
        /* still asks for a drain, so a waiting item whose drain could not be */
        /* posted (no handle yet) is run once a repeat finds the handle.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iItem   : the window's own item number (its enum, as an int).  */
        /*     objArgs : the item's arguments, compared with Equals; null for */
        /*               none.                                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the item was queued; false when it was        */
        /*            dropped as a duplicate, or the window is gone.          */
        /*--------------------------------------------------------------------*/
        internal bool WQU_Post(int iItem, object objArgs)
        {
            int i;

            if (Wqu_ctlWindow.IsDisposed)
            {   /* The window has gone: nothing can run its items. */
                return(false);
            }

            if (Wqu_bRunning && Wqu_iRunningItem == iItem && Object.Equals(Wqu_objRunningArgs, objArgs))
            {   /* The same item is running now: this one is dropped. */
                return(false);
            }

            for (i = 0; i < Wqu_lstItems.Count; i++)
            {   /* One waiting item per iteration: the same one already   */
                /* waiting drops this one.                                */
                if (Wqu_lstItems[i] == iItem && Object.Equals(Wqu_lstArgs[i], objArgs))
                {   /* Already waiting: this one is dropped, but a drain is    */
                    /* asked for again.  It posts nothing when one is on its   */
                    /* way; when the last try found no handle, it is what      */
                    /* lets a repeat of the waiting item get it run.           */
                    Wqu_PostDrain();
                    return(false);
                }
            }

            /* Queued at the end, and a drain posted to run it once this   */
            /* handler has returned.                                       */
            Wqu_lstItems.Add(iItem);
            Wqu_lstArgs.Add(objArgs);
            Wqu_PostDrain();

            /* Queued. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* Wqu_PostDrain:                                                     */
        /*                                                                    */
        /* Post one drain to the window, unless one is already posted and     */
        /* has not run.  The drain always arrives through the window's        */
        /* message queue (BeginInvoke), never as a direct call, so a post     */
        /* made from inside an item never runs the next item inside it.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : a drain is posted, or one already was.                  */
        /*--------------------------------------------------------------------*/
        private void Wqu_PostDrain()
        {
            if (Wqu_bDrainPosted)
            {   /* One is on its way: it runs everything waiting by then. */
                return;
            }

            try
            {   /* Posted; BeginInvoke throws when the window has no handle   */
                /* (being made or already gone).                              */
                Wqu_ctlWindow.BeginInvoke(new MethodInvoker(Wqu_Drain));
                Wqu_bDrainPosted = true;
            }
            catch (InvalidOperationException exPost)
            {   /* No handle: the items stay waiting, and the next post tries   */
                /* again -- a new item, or a repeat of one waiting, which       */
                /* WQU_Post drops but still asks for a drain.  Logged, as the   */
                /* window cannot run them now.                                  */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("window queue", exPost.ToString());
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* Wqu_Drain:                                                         */
        /*                                                                    */
        /* The drain, posted by Wqu_PostDrain: the oldest waiting item taken  */
        /* and run to its end through the window's runner, then -- if more    */
        /* wait -- the next drain posted rather than run here, so the window  */
        /* paints and takes input between items.  A drain that finds an item  */
        /* running (posted while an item's own dialog pumped messages) does   */
        /* nothing: that item's end posts the next drain.                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : one item run, or nothing.                               */
        /*--------------------------------------------------------------------*/
        private void Wqu_Drain()
        {
            /* This drain has arrived: a later post may post another. */
            Wqu_bDrainPosted = false;

            if (Wqu_bRunning || Wqu_lstItems.Count == 0 || Wqu_ctlWindow.IsDisposed)
            {   /* An item is running (this drain arrived inside it), there   */
                /* is nothing waiting, or the window has gone: nothing run.   */
                return;
            }

            /* The oldest item taken off the queue and marked running, so    */
            /* that its own posts and any drain arriving inside it see it.   */
            Wqu_iRunningItem = Wqu_lstItems[0];
            Wqu_objRunningArgs = Wqu_lstArgs[0];
            Wqu_lstItems.RemoveAt(0);
            Wqu_lstArgs.RemoveAt(0);
            Wqu_bRunning = true;
            try
            {   /* Run to its end by the window's own function for it. */
                Wqu_pfnRun(Wqu_iRunningItem, Wqu_objRunningArgs);
            }
            finally
            {   /* Ended, normally or by an exception: no longer running,     */
                /* and the next item, if any, run by a drain of its own.      */
                /* An exception then goes on out of the BeginInvoke call to   */
                /* WinForms' own handling of an exception on the UI thread    */
                /* -- the program sets no handler of its own, so the          */
                /* framework's default answers it.                            */
                Wqu_bRunning = false;
                Wqu_objRunningArgs = null;
                if (Wqu_lstItems.Count != 0)
                {   /* More waiting: the next drain posted. */
                    Wqu_PostDrain();
                }
            }
        }
    }
}
