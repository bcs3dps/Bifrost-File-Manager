/*----------------------------------------------------------------------------*/
/* bfe_capacity.c                                                             */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The capacity test (BFE_TestCapacity): how much of a device really holds    */
/* data.  A counterfeit-capacity card -- one "1 GB" microSD seen in practice  */
/* held about 120 MiB -- reports a size it does not have, accepts every       */
/* write, says it succeeded, and keeps nothing past its                       */
/* real end: reading there gives filler (0x55 on that card) or, on another    */
/* kind, the data at a lower address, the addresses repeating.  A write's     */
/* own success proves nothing; only writing something recognisable and        */
/* reading it back does.                                                      */
/*                                                                            */
/* THE TEST, and why each part is shaped as it is:                            */
/*                                                                            */
/*   - SAMPLES, not the whole card: 64 blocks of 4 KiB spread evenly from the */
/*     first sixty-fourth of the device to its last block.  Seconds, not the  */
/*     hour a full write-and-read of a large card takes, and enough to find   */
/*     the step a counterfeit's end makes; the step is then narrowed.         */
/*   - NOTHING DESTROYED: what is at every sample is read and kept FIRST,     */
/*     and written back after (BFC_PutBack says in what order, and why).      */
/*   - ALL WRITTEN BEFORE ANY IS READ: a card whose addresses repeat would    */
/*     pass a write-then-read of one block (the read comes from where the     */
/*     write went); writing every sample first lets a high block overwrite    */
/*     a low one, which the low block's read-back then shows -- naming the    */
/*     high block, because every sector carries its own address and this      */
/*     run's random nonce.                                                    */
/*   - THE STEP NARROWED, by halving between the last sample that held and    */
/*     the first that did not.  Not done for a card whose addresses repeat:   */
/*     there a single probe passes.                                           */
/*                                                                            */
/* It opens the device itself with a device write's guards -- never the       */
/* system disk, every volume on the disk locked for the test -- and writes    */
/* through the raw layer (bfe_device.c), unbuffered, flushing before every    */
/* read-back so the reads come from the media.  No image may be open: the     */
/* test writes to the device a session might be using.                        */
/*----------------------------------------------------------------------------*/

/* The C library (snprintf / sscanf for the tags, the memory calls) and      */
/* Windows; then the engine's internals (the gate, the error recorder) and   */
/* the raw I/O layer the test writes through.                                */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>
#include <windows.h>

#include "bfe_internal.h"
#include "bfe_device.h"

/*----------------------------------------------------------------------------*/
/* Definitions -- documentation table.                                        */
/*----------------------------------------------------------------------------*/
#define BFC_SAMPLES        64                       // test blocks spread over the device: enough to find a counterfeit's step, few enough to take seconds
#define BFC_BLOCK_MIN      4096                     // the least test block, bytes (a whole number of any real sector; a larger sector makes the block one sector)
#define BFC_MAGIC          "BIFROST-CAPACITY-TEST"  // opens every sector of a test block, so a block found elsewhere can be named
#define BFC_LABEL          "test"                   // pszImagePath of the test's progress events -- matched by the program (gui\CopyForms.cs, CPY_LABEL_TEST), which counts these events in steps, not bytes: change both together

/*----------------------------------------------------------------------------*/
/* BFC_Sample -- one test point.                                              */
/*----------------------------------------------------------------------------*/
typedef struct BFC_Sample
{
    uint64_t ullAt;                         // the block's byte offset on the device, a multiple of the block size
    uint8_t *pbOriginal;                    // what was there before the test (a slice of BFC_Test.pbOriginals)
    int      bWritten;                      // TRUE once the test block went out, so it must be put back
    int      bHeld;                         // TRUE when the block read back exactly as written
    uint64_t ullFoundAt;                    // when not held: the address named by a test block found here instead (a repeating card), else 0
    int      bFoundOther;                   // TRUE when ullFoundAt is meaningful
} BFC_Sample;

/*----------------------------------------------------------------------------*/
/* BFC_Test -- the run's state, passed between the steps.                     */
/*----------------------------------------------------------------------------*/
typedef struct BFC_Test
{
    BFV_Target       target;                // the device, open for unbuffered writing
    BFE_CopyCallback pfnEvent;              // progress and cancel, or NULL
    void            *pvContext;             // handed back to pfnEvent
    const wchar_t   *pwszDevice;            // the device's path, for the events
    uint32_t         uiBlock;               // the test block's size, bytes
    uint64_t         ullNonce;              // this run's random number, in every tag, so an old run's leftovers are not mistaken for this one's
    uint8_t         *pbOriginals;           // page-aligned: BFC_SAMPLES kept blocks, one after another
    uint8_t         *pbTag;                 // page-aligned: one test block being written
    uint8_t         *pbRead;                // page-aligned: one block being read back
    BFC_Sample       aSamples[BFC_SAMPLES]; // the test points, lowest address first
    uint32_t         uiSamples;             // how many of aSamples are in use (fewer only when two would share a block)
    uint64_t         ullStepsDone;          // progress so far, in steps
    uint64_t         ullStepsTotal;         // the planned steps: read, write and read-back per sample, plus the halvings the narrowing would take
    int              bCancelled;            // TRUE once the callback asked to stop
} BFC_Test;

/*----------------------------------------------------------------------------*/
/* BFC_Report:                                                                */
/*                                                                            */
/* Tell the callback how the test stands, in steps, and learn whether to      */
/* stop.  A cancel is remembered in the run (it is acted on between steps,    */
/* after anything already written has been put back).                         */
/*                                                                            */
/* Arguments:                                                                 */
/*     pTest  : the run.                                                      */
/*     uiKind : BFE_EVENT_START, _PROGRESS or _DONE.                          */
/*                                                                            */
/* Returns:                                                                   */
/*     void : pTest->bCancelled is set when the reply was CANCEL.             */
/*----------------------------------------------------------------------------*/
static void BFC_Report(BFC_Test *pTest, uint32_t uiKind)
{
    BFE_CopyEvent event;

    if (pTest->pfnEvent == NULL)
    {   /* Nobody is listening, and nobody can cancel. */
        return;
    }

    /* A copy's event, zeroed (so the reserved field is 0), carrying the     */
    /* test's STEPS where a copy carries bytes -- the "test" label is what   */
    /* tells the program to read them so -- and the device as the host       */
    /* path.                                                                 */
    memset(&event, 0, sizeof(event));
    event.uiKind        = uiKind;
    event.uiType        = BFE_TYPE_FILE;
    event.pszImagePath  = BFC_LABEL;
    event.pwszHostPath  = pTest->pwszDevice;
    event.ullBytesDone  = pTest->ullStepsDone;
    event.ullBytesTotal = pTest->ullStepsTotal;

    if (pTest->pfnEvent(&event, pTest->pvContext) == BFE_REPLY_CANCEL)
    {   /* The user asked to stop: acted on at the next safe point. */
        pTest->bCancelled = TRUE;
    }
}

/*----------------------------------------------------------------------------*/
/* BFC_Step:                                                                  */
/*                                                                            */
/* One step done: count it, and report it.                                    */
/*                                                                            */
/* Arguments:                                                                 */
/*     pTest : the run.                                                       */
/*                                                                            */
/* Returns:                                                                   */
/*     void : the count and the callback are up to date.                      */
/*----------------------------------------------------------------------------*/
static void BFC_Step(BFC_Test *pTest)
{
    /* One more step done. */
    pTest->ullStepsDone++;

    if (pTest->ullStepsDone > pTest->ullStepsTotal)
    {   /* The narrowing took longer than planned for: the total follows,   */
        /* so the bar never runs past its end.                              */
        pTest->ullStepsTotal = pTest->ullStepsDone;
    }

    /* The callback learns of it, and may cancel. */
    BFC_Report(pTest, BFE_EVENT_PROGRESS);
}

/*----------------------------------------------------------------------------*/
/* BFC_FillTag:                                                               */
/*                                                                            */
/* Make the test block for an address: a readable tag naming the address and  */
/* the run in EVERY 512-byte sector, so a block found at the wrong place      */
/* names where it was meant to go however the card misplaced it, over a byte  */
/* pattern, so a card that returns some fixed filler cannot pass by accident. */
/*                                                                            */
/* Arguments:                                                                 */
/*     pTest  : the run (block size and nonce).                               */
/*     pbOut  : receives uiBlock bytes.                                       */
/*     ullAt  : the address the block is for.                                 */
/*                                                                            */
/* Returns:                                                                   */
/*     void : pbOut holds the block.                                          */
/*----------------------------------------------------------------------------*/
static void BFC_FillTag(const BFC_Test *pTest, uint8_t *pbOut, uint64_t ullAt)
{
    char     szTag[128];
    uint64_t ullState;
    uint32_t uiSector;
    uint32_t i;
    int      cchTag;

    for (uiSector = 0; uiSector < pTest->uiBlock / 512; uiSector++)
    {   /* One 512-byte sector per iteration. */

        /* The pattern first: xorshift64 seeded from the address, the run and   */
        /* the sector, so no two sectors anywhere are alike.                    */
        ullState = (ullAt ^ pTest->ullNonce) + ((uint64_t)uiSector * 0x9E3779B97F4A7C15ull) + 1;

        for (i = 0; i < 512; i++)
        {   /* One byte per iteration. */
            ullState ^= ullState << 13;
            ullState ^= ullState >> 7;
            ullState ^= ullState << 17;
            pbOut[(uiSector * 512) + i] = (uint8_t)ullState;
        }

        /* Then the readable tag over the start of the sector:             */
        /* "BIFROST-CAPACITY-TEST at=<address> nonce=<run> sector=<n>;".   */
        cchTag = snprintf(szTag, sizeof(szTag), BFC_MAGIC " at=%llu nonce=%llu sector=%u;",
                          (unsigned long long)ullAt, (unsigned long long)pTest->ullNonce, (unsigned)uiSector);

        /* The tag is at most about 95 characters (two 20-digit numbers and   */
        /* a 10-digit one in fixed words), so snprintf never truncates it     */
        /* and cchTag is the bytes actually in szTag -- which the copy        */
        /* below relies on.                                                   */
        if (cchTag > 0)
        {   /* Laid over the pattern, without its terminating NUL. */
            memcpy(pbOut + (uiSector * 512), szTag, (size_t)cchTag);
        }
    }
}

/*----------------------------------------------------------------------------*/
/* BFC_TagAddress:                                                            */
/*                                                                            */
/* Whether a block read back carries THIS run's tag in its first sector, and  */
/* for which address -- how a block found at the wrong place is named.  An    */
/* earlier run's tag (another nonce) does not count: a card that was tested   */
/* before and crashed part-way must not be read as repeating.                 */
/*                                                                            */
/* Arguments:                                                                 */
/*     pTest  : the run.                                                      */
/*     pbData : the block read.                                               */
/*     pullAt : receives the address named, when there is one.                */
/*                                                                            */
/* Returns:                                                                   */
/*     int : TRUE when the block carries this run's tag, FALSE otherwise.     */
/*----------------------------------------------------------------------------*/
static int BFC_TagAddress(const BFC_Test *pTest, const uint8_t *pbData, uint64_t *pullAt)
{
    char               szHead[128];
    unsigned long long ullAt;
    unsigned long long ullNonce;

    /* The first sector's opening, made a string -- 127 bytes, all a tag    */
    /* can occupy, from a block of at least 4 KiB.  Only the first sector   */
    /* is read: every sector names the same address, so one is enough.      */
    memcpy(szHead, pbData, sizeof(szHead) - 1);
    szHead[sizeof(szHead) - 1] = '\0';

    if (strncmp(szHead, BFC_MAGIC " at=", strlen(BFC_MAGIC " at=")) != 0)
    {   /* Not a test block at all. */
        return(FALSE);
    }

    if (sscanf(szHead + strlen(BFC_MAGIC " "), "at=%llu nonce=%llu", &ullAt, &ullNonce) != 2 || ullNonce != pTest->ullNonce)
    {   /* Damaged, or another run's. */
        return(FALSE);
    }

    /* The address the block was written for -- where it really belongs. */
    *pullAt = (uint64_t)ullAt;

    /* This run's block, for that address. */
    return(TRUE);
}

/*----------------------------------------------------------------------------*/
/* BFC_ProbeOne:                                                              */
/*                                                                            */
/* The narrowing's single probe: whether one block holds what is written to   */
/* it, the block left as it was found -- so nothing is changed between        */
/* probes.                                                                    */
/*                                                                            */
/* Arguments:                                                                 */
/*     pTest  : the run.                                                      */
/*     ullAt  : the block's address.                                          */
/*     pbHeld : receives TRUE when the block read back as written.            */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_IO_IMAGE when a read failed; BFE_ERR_IO_HOST     */
/*           when a write, a flush or the putting back failed (the detail     */
/*           line says which).                                                */
/*----------------------------------------------------------------------------*/
static int BFC_ProbeOne(BFC_Test *pTest, uint64_t ullAt, int *pbHeld)
{
    uint8_t *pbKeep;
    int      iError;
    int      iStatus;

    /* The kept bytes go in the spare block allocated after the samples'    */
    /* own.  (Nothing reads the samples' kept copies after BFC_PutBack,     */
    /* which runs before any probe, so one of their slices would serve as   */
    /* well; why a separate block was chosen is not recorded.)              */
    pbKeep = pTest->pbOriginals + ((size_t)BFC_SAMPLES * pTest->uiBlock);

    /* "Not held" until the read-back says otherwise, so every early return   */
    /* leaves a defined verdict.                                              */
    *pbHeld = FALSE;

    /* What is at the address now, kept for the put-back. */
    iError = BFV_Read(&pTest->target, ullAt, pbKeep, pTest->uiBlock);

    if (iError != 0)
    {   /* Nothing written yet: a plain read failure, recorded; the caller   */
        /* releases the run.  (Unlike the read-BACK below, a failure here    */
        /* is an error: without the kept bytes nothing could be put back.)   */
        return(BFX_Fail(BFE_ERR_IO_IMAGE, "reading byte %llu failed (Windows error %d)", (unsigned long long)ullAt, iError));
    }

    /* The test block, out and made real. */
    BFC_FillTag(pTest, pTest->pbTag, ullAt);
    iError = BFV_Write(&pTest->target, ullAt, pTest->pbTag, pTest->uiBlock);

    if (iError == 0)
    {   /* Written: to the media before it is read back. */
        iError = BFV_Flush(&pTest->target);
    }

    /* No failure to report yet: only a failed put-back below sets one. */
    iStatus = BFE_OK;

    if (iError != 0)
    {   /* Refused or not confirmed: put back what was there, and stop.     */
        /* Best effort -- the results are not looked at: if the write was   */
        /* refused nothing changed, and if only its flush failed the put-   */
        /* back is all that can be tried; the failure reported is the       */
        /* write's.                                                         */
        BFV_Write(&pTest->target, ullAt, pbKeep, pTest->uiBlock);
        BFV_Flush(&pTest->target);
        return(BFX_Fail(BFE_ERR_IO_HOST, "writing byte %llu failed (Windows error %d)", (unsigned long long)ullAt, iError));
    }

    /* The read-back, and the verdict.  A read that FAILS counts as "not   */
    /* held", not as an error: past a counterfeit card's real end a read   */
    /* may fail outright as readily as return filler, and either way the   */
    /* block does not keep data.                                           */
    iError = BFV_Read(&pTest->target, ullAt, pTest->pbRead, pTest->uiBlock);
    *pbHeld = (iError == 0 && memcmp(pTest->pbRead, pTest->pbTag, pTest->uiBlock) == 0);

    /* Put back what was there, whatever the verdict. */
    iError = BFV_Write(&pTest->target, ullAt, pbKeep, pTest->uiBlock);

    if (iError == 0)
    {   /* Written back: made real. */
        iError = BFV_Flush(&pTest->target);
    }

    if (iError != 0)
    {   /* The original could not be put back: the test block is left there.   */
        /* ("4 KiB" in the message is the block size on every real disk --     */
        /* sectors are 512 or 4096 bytes, so the block is never larger.)       */
        iStatus = BFX_Fail(BFE_ERR_IO_HOST, "putting back the 4 KiB at byte %llu failed (Windows error %d); a test block is left there",
                           (unsigned long long)ullAt, iError);
    }

    /* BFE_OK, or the failure recorded above. */
    return(iStatus);
}

/*----------------------------------------------------------------------------*/
/* BFC_PutBack:                                                               */
/*                                                                            */
/* Write every sample's kept contents back where it came from, so the device  */
/* holds what it held before the test.  Every sample is attempted even after  */
/* one fails, so as little as possible is left changed.                       */
/*                                                                            */
/* Arguments:                                                                 */
/*     pTest : the run.                                                       */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK, or BFE_ERR_IO_HOST naming the first block that could not */
/*           be put back.                                                     */
/*----------------------------------------------------------------------------*/
static int BFC_PutBack(BFC_Test *pTest)
{
    uint32_t i;
    uint64_t ullFailedAt;
    int      iFailError;
    int      iError;

    /* No failure yet: the first one met is the one reported. */
    ullFailedAt = 0;
    iFailError  = 0;

    /* i counts down from uiSamples to 1 and the sample is i - 1, so the   */
    /* unsigned counter never has to pass below zero.                      */
    for (i = pTest->uiSamples; i > 0; i--)
    {   /* One sample per iteration, from the HIGHEST address down: on a    */
        /* card whose addresses repeat, a high write landed on a low        */
        /* block, and the low block's own contents must be the last thing   */
        /* written there.                                                   */
        if (!pTest->aSamples[i - 1].bWritten)
        {   /* Never written (the run stopped before it): nothing to undo. */
            continue;
        }

        /* The kept bytes back at their address. */
        iError = BFV_Write(&pTest->target, pTest->aSamples[i - 1].ullAt, pTest->aSamples[i - 1].pbOriginal, pTest->uiBlock);

        if (iError != 0 && iFailError == 0)
        {   /* The first that would not go back: remembered, and the rest tried. */
            iFailError  = iError;
            ullFailedAt = pTest->aSamples[i - 1].ullAt;
        }
    }

    /* One flush for all of them, after the last write: made real together. */
    iError = BFV_Flush(&pTest->target);

    if (iFailError == 0 && iError != 0)
    {   /* Every write went, but the media did not confirm them. */
        iFailError = iError;
    }

    if (iFailError != 0)
    {   /* Something of the test is left on the device. */
        return(BFX_Fail(BFE_ERR_IO_HOST, "putting back what the test overwrote failed at byte %llu (Windows error %d)",
                        (unsigned long long)ullFailedAt, iFailError));
    }

    /* Every sample holds what it held before. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFC_Release:                                                               */
/*                                                                            */
/* The end of every run that got its volumes locked: nothing of the run is    */
/* held afterwards, and the busy flag is released.                            */
/*                                                                            */
/* Arguments:                                                                 */
/*     pTest  : the run.                                                      */
/*     pLocks : the volumes locked for it.                                    */
/*                                                                            */
/* Returns:                                                                   */
/*     void : nothing of the run is held.                                     */
/*----------------------------------------------------------------------------*/
static void BFC_Release(BFC_Test *pTest, BFV_Locks *pLocks)
{
    /* The three buffers (freeing NULL is allowed, so a run that stopped    */
    /* before allocating them comes here too); the device, BEFORE its       */
    /* volumes are unlocked, because Windows remounts them the moment the   */
    /* locks go; then the gate.  Any failure was recorded by the caller     */
    /* before this, while the flag was still held.                          */
    BFV_FreeAligned(pTest->pbOriginals);
    BFV_FreeAligned(pTest->pbTag);
    BFV_FreeAligned(pTest->pbRead);
    BFV_Close(&pTest->target);
    BFV_UnlockDisk(pLocks);
    BFX_Leave();
}

/*----------------------------------------------------------------------------*/
/* BFE_TestCapacity:                                                          */
/*                                                                            */
/* The public entry: the capacity test the file header describes, its steps   */
/* numbered in the body (STEP 1 to 8).  Nothing of the run is held once it    */
/* returns, and what it wrote is put back unless a put-back itself failed     */
/* (the detail line then says where a test block is left).                    */
/*                                                                            */
/* Arguments:                                                                 */
/*     pwszDevice : the device, e.g. \\.\PhysicalDrive2.                      */
/*     pfnEvent   : progress and cancel, or NULL.                             */
/*     pvContext  : handed back to pfnEvent unchanged.                        */
/*     pResult    : receives the verdict.                                     */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK with pResult filled; otherwise the BFE_ERR_* code         */
/*           bifrost_engine.h's entry lists, with the detail line saying why. */
/*----------------------------------------------------------------------------*/
BFE_API int BFE_TestCapacity(const wchar_t *pwszDevice, BFE_CopyCallback pfnEvent, void *pvContext,
                             BFE_CapacityResult *pResult)
{
    static BFC_Test test;                   // static: it holds 64 sample records, and the engine runs one call at a time
    BFV_Locks       locks;
    LARGE_INTEGER   liCounter;
    uint64_t        ullSpan;
    uint64_t        ullAt;
    uint64_t        ullLow;
    uint64_t        ullHigh;
    uint64_t        ullMid;
    uint64_t        ullPeriod;
    uint32_t        uiSystem;
    uint32_t        uiFirstBad;
    uint32_t        i;
    int             bHeld;
    int             iStatus;
    int             iError;

    /* The gate: the busy flag only -- "no image open" is tested just   */
    /* below with its own message.                                      */
    iStatus = BFX_Enter(FALSE, FALSE);

    if (iStatus != BFE_OK)
    {   /* Another call is running: BFX_Enter has said so, and the flag is   */
        /* not ours, so there is nothing to release.                         */
        return(iStatus);
    }

    if (BFX_IsOpen())
    {   /* A session may be using the very device the test writes to.   */
        /* Refused inside the gate; only the flag is held.              */
        iStatus = BFX_Fail(BFE_ERR_ALREADY_OPEN, "close the open image before testing a device");
        BFX_Leave();
        return(iStatus);
    }

    if (pwszDevice == NULL || pwszDevice[0] == L'\0' || pResult == NULL)
    {   /* Nothing to test, or nowhere to put the answer: refused with only   */
        /* the flag held and nothing opened.                                  */
        iStatus = BFX_Fail(BFE_ERR_ARG, "no device path or no result structure given");
        BFX_Leave();
        return(iStatus);
    }

    /* The run starts clean: an empty result, the run's state zeroed (every   */
    /* buffer NULL and the device "not open", so BFC_Release is safe from     */
    /* here), no locks, and the caller's callback and device path kept.       */
    memset(pResult, 0, sizeof(*pResult));
    memset(&test, 0, sizeof(test));
    memset(&locks, 0, sizeof(locks));
    test.pfnEvent   = pfnEvent;
    test.pvContext  = pvContext;
    test.pwszDevice = pwszDevice;

    /* STEP 1: open the device for writing, with a device write's guards. */
    iError = BFV_Open(pwszDevice, BFV_ACCESS_WRITE, &test.target);

    if (iError != 0)
    {   /* Missing, in use, or no Administrator: the Windows number tells   */
        /* which; nothing opened, so only the flag is released.             */
        iStatus = BFX_Fail(BFE_ERR_IMAGE_OPEN, "cannot open %ls for writing (Windows error %d)", pwszDevice, iError);
        BFX_Leave();
        return(iStatus);
    }

    if (!test.target.bDevice)
    {   /* A file holds what it holds: there is no capacity to test.  The   */
        /* file was opened (nothing written); closed, then the flag.        */
        iStatus = BFX_Fail(BFE_ERR_ARG, "%ls is a file; only a device can be tested", pwszDevice);
        BFV_Close(&test.target);
        BFX_Leave();
        return(iStatus);
    }

    /* The system-disk guard, on the disk number of the open handle -- before   */
    /* the lock, because locking the system disk's volumes would dismount the   */
    /* running Windows.  The handle being open for writing writes nothing.      */
    uiSystem = BFV_SystemDisk();

    if (uiSystem == BFV_NO_DISK || test.target.uiDisk == BFV_NO_DISK || test.target.uiDisk == uiSystem)
    {   /* The disk Windows runs from, or one that cannot be told apart. */
        iStatus = BFX_Fail(BFE_ERR_SYSTEM_DISK, "%ls is the disk Windows is running from, or cannot be told apart from it", pwszDevice);
        BFV_Close(&test.target);
        BFX_Leave();
        return(iStatus);
    }

    /* The test block: 4 KiB, or one sector when a sector is larger -- a     */
    /* whole number of sectors either way, as the unbuffered device needs.   */
    test.uiBlock = BFC_BLOCK_MIN;

    if (test.target.uiSector > test.uiBlock)
    {   /* A sector larger than the block: the block is one sector. */
        test.uiBlock = test.target.uiSector;
    }

    if (test.target.ullSize < (uint64_t)test.uiBlock * BFC_SAMPLES * 2)
    {   /* Too small for the samples to be spread at all (512 KiB at 4 KiB    */
        /* blocks): refused, the device closed -- nothing written, no lock.   */
        iStatus = BFX_Fail(BFE_ERR_ARG, "%ls is too small to test (%llu bytes)", pwszDevice, (unsigned long long)test.target.ullSize);
        BFV_Close(&test.target);
        BFX_Leave();
        return(iStatus);
    }

    /* Lock and dismount every volume on the disk for the test (only the      */
    /* volumes on it are opened for writing; one elsewhere is only asked      */
    /* where it lies).  The device stays open meanwhile -- unlike BFE_Open,   */
    /* BFE_CheckFs and BFE_ResizeFs, which close their probe first.           */
    /* For a VOLUME path (\\.\X:) the test's own open handle makes            */
    /* that volume refuse the lock.  THE DEFECT EXISTS AND IS INTENTIONALLY   */
    /* DEFERRED: the program tests only a whole disk (\\.\PhysicalDriveN),    */
    /* whose handle is not on any of its volumes.                             */
    iError = BFV_LockDisk(test.target.uiDisk, &locks);

    if (iError != 0)
    {   /* The disk could not be locked: a volume on it in use, or one     */
        /* whose disk could not be told.  BFV_LockDisk has released the    */
        /* locks it took, and its sentence (locks.szFailure) says which    */
        /* case and which volume; the device is closed by hand.            */
        /* (BFC_Release would do the same here -- its frees would be of    */
        /* NULL buffers, its unlock of an empty list -- so why it is not   */
        /* used is not recorded.)  Nothing has been written.               */
        iStatus = BFX_Fail(BFE_ERR_VOLUME_LOCK, "%s", locks.szFailure);
        BFV_Close(&test.target);
        BFX_Leave();
        return(iStatus);
    }

    /* The buffers: every sample's kept block plus one for the narrowing's    */
    /* probe, the block being written, the block being read.  Page-aligned,   */
    /* as unbuffered device I/O requires.                                     */
    test.pbOriginals = (uint8_t *)BFV_AllocAligned((size_t)(BFC_SAMPLES + 1) * test.uiBlock);
    test.pbTag       = (uint8_t *)BFV_AllocAligned(test.uiBlock);
    test.pbRead      = (uint8_t *)BFV_AllocAligned(test.uiBlock);

    if (test.pbOriginals == NULL || test.pbTag == NULL || test.pbRead == NULL)
    {   /* Out of memory for the buffers: nothing written yet.  BFC_Release   */
        /* frees whichever did come, closes, unlocks and leaves.              */
        iStatus = BFX_Fail(BFE_ERR_NO_MEMORY, "out of memory");
        BFC_Release(&test, &locks);
        return(iStatus);
    }

    /* This run's nonce, in every tag, different on every run -- so a test     */
    /* block an earlier run left behind (one that stopped before putting       */
    /* back) is never taken for this run's (BFC_TagAddress).  The counter is   */
    /* multiplied by 0x9E3779B97F4A7C15, 2^64 divided by the golden ratio,     */
    /* which spreads its fast-changing low bits over the whole word, then      */
    /* the tick count is XORed in.  Unique enough for its purpose; it is not   */
    /* a secret.                                                               */
    QueryPerformanceCounter(&liCounter);
    test.ullNonce = ((uint64_t)liCounter.QuadPart * 0x9E3779B97F4A7C15ull) ^ (uint64_t)GetTickCount64();

    /* STEP 2: the sample addresses -- the end of each sixty-fourth of the   */
    /* device, on the block grid, the last being the device's last block.    */
    /* Two that would share a block (impossible above the size floor, but    */
    /* cheap to guard) keep only the first.                                  */
    ullSpan = test.target.ullSize - test.uiBlock;

    for (i = 0; i < BFC_SAMPLES; i++)
    {   /* One sample address per iteration. */
        ullAt = (ullSpan / BFC_SAMPLES) * (i + 1);
        ullAt = ullAt - (ullAt % test.uiBlock);

        if (test.uiSamples > 0 && ullAt <= test.aSamples[test.uiSamples - 1].ullAt)
        {   /* No higher than the one before: skipped. */
            continue;
        }

        /* The sample: its address, and its slice of the kept-blocks buffer. */
        test.aSamples[test.uiSamples].ullAt      = ullAt;
        test.aSamples[test.uiSamples].pbOriginal = test.pbOriginals + ((size_t)test.uiSamples * test.uiBlock);
        test.uiSamples++;
    }

    /* The plan: three steps per sample, plus the halvings that narrowing   */
    /* one gap between samples to a block would take (planned whether or    */
    /* not a gap is found; a good card's bar jumps to its end at DONE).     */
    test.ullStepsTotal = (uint64_t)test.uiSamples * 3;
    ullAt              = (ullSpan / BFC_SAMPLES) / test.uiBlock;

    while (ullAt > 1)
    {   /* One halving per iteration. */
        ullAt >>= 1;
        test.ullStepsTotal++;
    }

    /* What the device claims, and how many samples there are, into the   */
    /* result now; then the START event, which may already cancel.        */
    pResult->ullReported = test.target.ullSize;
    pResult->uiSamples   = test.uiSamples;
    BFC_Report(&test, BFE_EVENT_START);

    /* STEP 3: keep what is at every sample. */
    for (i = 0; i < test.uiSamples && !test.bCancelled; i++)
    {   /* One sample per iteration. */

        /* What is there, into the sample's slice -- the copy the put-back   */
        /* restores.                                                         */
        iError = BFV_Read(&test.target, test.aSamples[i].ullAt, test.aSamples[i].pbOriginal, test.uiBlock);

        if (iError != 0)
        {   /* Nothing written yet: a plain read failure ends the test. */
            iStatus = BFX_Fail(BFE_ERR_IO_IMAGE, "reading byte %llu failed (Windows error %d)",
                               (unsigned long long)test.aSamples[i].ullAt, iError);
            BFC_Release(&test, &locks);
            return(iStatus);
        }

        /* Kept: one step, and the callback may cancel. */
        BFC_Step(&test);
    }

    /* STEP 4: write every test block, then make them real -- ALL before any   */
    /* is read back.                                                           */
    for (i = 0; i < test.uiSamples && !test.bCancelled; i++)
    {   /* One sample per iteration. */

        /* The block for this address, marked written BEFORE the write so   */
        /* that a write which fails part-way is still put back.             */
        BFC_FillTag(&test, test.pbTag, test.aSamples[i].ullAt);
        test.aSamples[i].bWritten = TRUE;
        iError = BFV_Write(&test.target, test.aSamples[i].ullAt, test.pbTag, test.uiBlock);

        if (iError != 0)
        {   /* Refused: what went out is put back, and the test ends.  The   */
            /* write's STATUS is what returns; its TEXT is recorded first,   */
            /* so if the put-back fails too, BFC_PutBack's text -- saying    */
            /* where a test block is left -- replaces it, the more urgent    */
            /* fact.  Then the release (buffers, device, locks, flag).       */
            iStatus = BFX_Fail(BFE_ERR_IO_HOST, "writing byte %llu failed (Windows error %d)",
                               (unsigned long long)test.aSamples[i].ullAt, iError);
            BFC_PutBack(&test);
            BFC_Release(&test, &locks);
            return(iStatus);
        }

        /* Written: one step, and the callback may cancel. */
        BFC_Step(&test);
    }

    /* One flush for all the test blocks, so the reads below come from the   */
    /* media.                                                                */
    iError = BFV_Flush(&test.target);

    if (iError != 0)
    {   /* The media did not confirm the test blocks: what they did to the   */
        /* card is unknown, so nothing is judged.  As for a refused write:   */
        /* the failure recorded, everything put back (a put-back failure's   */
        /* text replacing this one), the run released.                       */
        iStatus = BFX_Fail(BFE_ERR_IO_HOST, "the device did not confirm the test writes (Windows error %d)", iError);
        BFC_PutBack(&test);
        BFC_Release(&test, &locks);
        return(iStatus);
    }

    /* STEP 5: read every block back and judge it. */
    for (i = 0; i < test.uiSamples && !test.bCancelled; i++)
    {   /* One sample per iteration. */

        /* The block as it should be (made again: pbTag holds only the last   */
        /* one written), and the block as it is.  A read that fails leaves    */
        /* the sample not held, as in BFC_ProbeOne.                           */
        BFC_FillTag(&test, test.pbTag, test.aSamples[i].ullAt);
        iError = BFV_Read(&test.target, test.aSamples[i].ullAt, test.pbRead, test.uiBlock);

        if (iError == 0 && memcmp(test.pbRead, test.pbTag, test.uiBlock) == 0)
        {   /* Kept exactly. */
            test.aSamples[i].bHeld = TRUE;
        }
        else if (iError == 0)
        {   /* Not kept: is another sample's block here instead? */
            test.aSamples[i].bFoundOther = BFC_TagAddress(&test, test.pbRead, &test.aSamples[i].ullFoundAt);
        }

        /* Judged: one step, and the callback may cancel. */
        BFC_Step(&test);
    }

    /* STEP 6: put back what every sample held -- also after a cancel. */
    iStatus = BFC_PutBack(&test);

    if (iStatus != BFE_OK)
    {   /* Something of the test is left on the device; the text says where. */
        BFC_Release(&test, &locks);
        return(iStatus);
    }

    if (test.bCancelled)
    {   /* Stopped by the user, with everything put back. */
        iStatus = BFX_Fail(BFE_ERR_CANCELLED, "the capacity test was cancelled; nothing on the device was changed");
        BFC_Release(&test, &locks);
        return(iStatus);
    }

    /* STEP 7: the verdict.  First the samples: how many failed, the lowest   */
    /* that did, and whether any held another sample's block (repeating       */
    /* addresses, whose period is the distance between the two).              */
    uiFirstBad = test.uiSamples;
    ullPeriod  = 0;

    for (i = 0; i < test.uiSamples; i++)
    {   /* One sample per iteration. */
        if (test.aSamples[i].bHeld)
        {   /* Kept. */
            continue;
        }

        /* Not kept: counted. */
        pResult->uiBadSamples++;

        if (uiFirstBad == test.uiSamples)
        {   /* The lowest that failed. */
            uiFirstBad = i;
        }

        if (test.aSamples[i].bFoundOther && test.aSamples[i].ullFoundAt > test.aSamples[i].ullAt)
        {   /* A higher block landed here: the addresses repeat every (high -   */
            /* low) bytes or a divisor of it; the smallest such distance seen   */
            /* is the best estimate of the real size.                           */
            pResult->bWraps = TRUE;
            ullAt = test.aSamples[i].ullFoundAt - test.aSamples[i].ullAt;

            if (ullPeriod == 0 || ullAt < ullPeriod)
            {   /* The shortest repeat so far. */
                ullPeriod = ullAt;
            }
        }
    }

    if (pResult->uiBadSamples == 0)
    {   /* Every sample held: the device holds what it reports. */
        pResult->ullGood     = test.target.ullSize;
        pResult->ullFirstBad = test.target.ullSize;
    }
    else if (pResult->bWraps)
    {   /* Repeating addresses: the real size is the repeat; a single-block   */
        /* probe would pass anywhere, so no narrowing.                        */
        pResult->ullGood     = ullPeriod;
        pResult->ullFirstBad = ullPeriod;
    }
    else
    {   /* STEP 8: narrow the step between the last sample that held below    */
        /* the first failure (or the start of the device) and that failure,   */
        /* one block at a time by halving.                                    */
        ullLow  = 0;
        ullHigh = test.aSamples[uiFirstBad].ullAt;

        if (uiFirstBad > 0)
        {   /* The sample below held: everything up to its end is known good. */
            ullLow = test.aSamples[uiFirstBad - 1].ullAt + test.uiBlock;
        }

        while (ullHigh - ullLow > test.uiBlock && !test.bCancelled)
        {   /* One probe per iteration, halving the unknown stretch. */

            /* The middle of the stretch, written as low + half the gap so   */
            /* the sum cannot overflow, and put on the block grid.           */
            ullMid = ullLow + ((ullHigh - ullLow) / 2);
            ullMid = ullMid - (ullMid % test.uiBlock);

            if (ullMid < ullLow)
            {   /* Rounded below the known-good end: probe at that end. */
                ullMid = ullLow;
            }

            /* The probe: kept, written, read back and put back at once.  When   */
            /* the first sample failed, ullLow is 0 and the probes may reach     */
            /* the device's first block (its partition table).  Each is put      */
            /* back before the next, so that is safe WHEN THE PUT-BACK WORKS;    */
            /* if it fails, the probe reports the byte and a test block is       */
            /* left there -- over the partition table, at block 0.               */
            iStatus = BFC_ProbeOne(&test, ullMid, &bHeld);

            if (iStatus != BFE_OK)
            {   /* A probe failed outright; the text says how.  The samples   */
                /* were put back before the narrowing began.                  */
                BFC_Release(&test, &locks);
                return(iStatus);
            }

            if (bHeld)
            {   /* Holds there: the good stretch reaches past this block. */
                ullLow = ullMid + test.uiBlock;
            }
            else
            {   /* Does not hold: the failure starts at or below this block. */
                ullHigh = ullMid;
            }

            /* One halving done: a step, and the callback may cancel. */
            BFC_Step(&test);
        }

        if (test.bCancelled)
        {   /* Stopped during the narrowing: every probe was put back already. */
            iStatus = BFX_Fail(BFE_ERR_CANCELLED, "the capacity test was cancelled; nothing on the device was changed");
            BFC_Release(&test, &locks);
            return(iStatus);
        }

        /* The step found: good up to ullLow, failing from ullHigh (one   */
        /* block apart, or ullLow already at ullHigh).                    */
        pResult->ullGood     = ullLow;
        pResult->ullFirstBad = ullHigh;
    }

    /* Counterfeit: addresses that repeat, or less good than claimed.  A    */
    /* genuine card with one bad stretch also lands here, its "good" size   */
    /* being where the stretch begins -- the test cannot tell the two.      */
    pResult->bCounterfeit = (pResult->bWraps || pResult->ullGood < pResult->ullReported);

    /* The bar to its end, and the run released. */
    test.ullStepsTotal = test.ullStepsDone;
    BFC_Report(&test, BFE_EVENT_DONE);
    BFC_Release(&test, &locks);

    /* The verdict is in the result. */
    return(BFE_OK);
}
