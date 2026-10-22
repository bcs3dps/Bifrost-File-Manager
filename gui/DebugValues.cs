/*----------------------------------------------------------------------------*/
/* DebugValues.cs: the values that exist only in a DEBUG build, for testing.  */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* Every value here is a project-wide global, read and written directly by    */
/* the code that needs it -- the one place globals are allowed, because none  */
/* of this is in a release build: the whole file, and every line anywhere     */
/* that touches it, is inside #if DEBUG.  A value is set only from the        */
/* command line at start-up (Program.cs) or is a constant; none is ever read  */
/* from or written to the registry or any file, so nothing a test set lasts   */
/* into the next run.                                                         */
/*----------------------------------------------------------------------------*/

#if DEBUG
namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* DBG_clsDebugValues -- the DEBUG build's test values.  Static only: it  */
    /* is never made, and holds no state but the values below.                */
    /*------------------------------------------------------------------------*/
    internal static class DBG_clsDebugValues
    {
        /* The test values -- documentation table. */
        internal static volatile int DBG_iCopyDelayMs; // --debug-copy-delay <ms>: milliseconds the command worker pauses at the start of each item an extract, import or copy within the image is about to copy, so a short copy runs long enough to try Exit, Cancel and their questions; 0 (the default) = no pause; set once by Program.cs before the window exists, read by NCB_ProcessCopyEvent on the worker thread (volatile, as the two threads differ)
    }
}
#endif
