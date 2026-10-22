/*----------------------------------------------------------------------------*/
/* CommandLayoutReader.cs                                                     */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The raw reader the partition-table parsers are given when the command      */
/* system reads a disk's layout (CommandNativeSteps.cs).  The parsers         */
/* (PartitionTable.cs, PartitionForm.cs) take a reader that answers only      */
/* true or false, and on a false they fall back -- a GPT read that fails is   */
/* taken as "no GPT".  So a real read failure would come out of them as a     */
/* plausible, emptier layout.  This reader keeps the FIRST failure, with the  */
/* engine's detail line, so its caller can report the failure instead of      */
/* publishing the fallback.  A read whose range lies past the end of the      */
/* disk is not such a failure -- nothing went wrong reading; the table names  */
/* a place the disk does not have -- so it answers false and is NOT kept,     */
/* leaving the parsers' own bounds to judge it, as they do for every other    */
/* table that does not fit its disk.                                          */
/*----------------------------------------------------------------------------*/

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* LDR_clsLayoutReader -- one disk's reader for one layout read: made,    */
    /* handed to the parsers, asked for its failure, and dropped, all within  */
    /* one native step on the command worker's thread, so nothing in it is    */
    /* shared between threads.                                                */
    /*------------------------------------------------------------------------*/
    internal sealed class LDR_clsLayoutReader
    {
        private readonly string Ldr_sPath;     // the image file or device every read is made from; fixed when the reader is made
        private RSL_clsResult Ldr_objFailure;  // the first failed read's status and detail line; null until a read fails
        private bool Ldr_bSizeAsked;           // true once the disk's length has been asked for (at the first read), whether or not it was learnt
        private bool Ldr_bSizeKnown;           // true when the engine reported the disk's length; false when it could not, and then no range is judged here
        private ulong Ldr_ullSize;             // the disk's length in bytes, as the engine reported it; 0 until known

        /*--------------------------------------------------------------------*/
        /* LDR_clsLayoutReader:                                               */
        /*                                                                    */
        /* A reader for one image file or device, with no failure yet.  It    */
        /* holds no handle: every read is a separate engine call that opens,  */
        /* reads and closes the path itself (BFE_ReadRaw).                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sPath : the file or device path, as the caller captured it.    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the reader, with no failure kept.              */
        /*--------------------------------------------------------------------*/
        internal LDR_clsLayoutReader(string sPath)
        {
            /* The path every read uses, no failure yet, and the disk's        */
            /* length not yet asked for -- it is asked at the first read, so   */
            /* making a reader costs no engine call.                           */
            Ldr_sPath = sPath;
            Ldr_objFailure = null;
            Ldr_bSizeAsked = false;
            Ldr_bSizeKnown = false;
            Ldr_ullSize = 0;
        }

        /*--------------------------------------------------------------------*/
        /* LDR_ReadFailure:                                                   */
        /*                                                                    */
        /* After the parsers have run, whether any read failed -- and if one  */
        /* did, the first failure, which the caller returns in place of the   */
        /* layout the parsers built (that layout may be a fallback).          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objFailure : receives the first failure's result, or null      */
        /*                  when every read succeeded.                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when a read failed; false when every read made     */
        /*            succeeded.                                              */
        /*--------------------------------------------------------------------*/
        internal bool LDR_ReadFailure(out RSL_clsResult objFailure)
        {
            /* The failure as it was recorded -- the same result object, so   */
            /* its status and detail line are exactly the engine's.           */
            objFailure = Ldr_objFailure;

            /* A failure was kept, or not. */
            return(objFailure != null);
        }

        /*--------------------------------------------------------------------*/
        /* LDR_Read:                                                          */
        /*                                                                    */
        /* The parsers' reader (PartitionTable.cs PRT_Reader): fill the whole */
        /* buffer from a byte offset of the disk.  A range that runs past the */
        /* disk's end answers false and is not kept as a failure.  The first  */
        /* real failure is kept, and from then on every read answers false    */
        /* WITHOUT reading, so the reads a parser makes after a failure --    */
        /* its fallbacks -- cannot fail differently and leave the wrong       */
        /* failure to report.                                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     ullOffset : the byte offset from the start of the file or      */
        /*                 device.                                            */
        /*     abBuffer  : the parser's buffer; all of it is read.            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true when the whole buffer was read; false when the     */
        /*            range lies past the disk's end, when this read failed,  */
        /*            or when an earlier one had.                             */
        /*--------------------------------------------------------------------*/
        internal bool LDR_Read(ulong ullOffset, byte[] abBuffer)
        {
            DVI_stDeviceInfo stInfo;
            int iStatus;

            if (Ldr_objFailure != null)
            {   /* A read has already failed: refused without touching the   */
                /* disk, so the failure kept stays the one reported.         */
                return(false);
            }

            if (!Ldr_bSizeAsked)
            {   /* The first read: the disk's length asked for, once.  The    */
                /* engine opens the path for its facts alone (no              */
                /* Administrator needed).  Should it fail, the length stays   */
                /* unknown, no range is judged below, and every failed read   */
                /* is kept as a failure -- the cautious side.                 */
                Ldr_bSizeAsked = true;
                if (ENG_clsNative.ENG_pfnDeviceInfoGet(Ldr_sPath, out stInfo) == ENG_clsNative.ENG_OK)
                {   /* Learnt: the file's size, or the device's length. */
                    Ldr_bSizeKnown = true;
                    Ldr_ullSize = stInfo.DVI_ullSize;
                }
            }

            if (Ldr_bSizeKnown &&
                (ullOffset > Ldr_ullSize || (ulong)abBuffer.Length > Ldr_ullSize - ullOffset))
            {   /* The range runs past the disk's end (tested as the room left   */
                /* after the offset, so a huge offset cannot wrap the sum): a    */
                /* partition that starts past the end -- a damaged entry, or     */
                /* an image saved short of its last partitions -- or a GPT       */
                /* array the header places out there.  No read is made and no    */
                /* failure kept: the parser takes the false as "not there"       */
                /* (PTB_Probe as no content), its bounds checks flag the row,    */
                /* and the rest of the layout -- the reads after this one        */
                /* included -- is read and shown as usual.                       */
                return(false);
            }

            /* The read: one engine call for the whole buffer, which the       */
            /* engine refuses whole when it runs past the end (which cannot    */
            /* reach it once the length is known: the test above has refused   */
            /* such a range) and otherwise fills completely or fails           */
            /* (BFE_ReadRaw).                                                  */
            iStatus = ENG_clsNative.ENG_pfnReadRaw(Ldr_sPath, ullOffset,
                abBuffer, (uint)abBuffer.Length);
            if (iStatus != ENG_clsNative.ENG_OK)
            {   /* The read failed.  The engine's detail line is taken NOW,   */
                /* before any other engine call can replace it, and kept      */
                /* with the status as the layout's failure.                   */
                Ldr_objFailure = new RSL_clsResult(iStatus, ENG_clsNative.ENG_ErrorDetail(), null);
                return(false);
            }

            /* The whole buffer holds the disk's bytes. */
            return(true);
        }
    }
}
