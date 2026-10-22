/*----------------------------------------------------------------------------*/
/* bfe_compress.c                                                             */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The compressed-image reader and the .xz writer declared in                 */
/* bfe_compress.h.  The reader streams: it holds one input buffer of the      */
/* file and one library state, and hands back as many raw bytes as asked,     */
/* so an image of any size goes through in constant memory.                   */
/*                                                                            */
/* THE IMAGE'S SIZE, where the format records it, is learned at open without  */
/* decompressing anything: a zip's central directory, a .xz's index (read     */
/* through liblzma's file-info decoder, which seeks to each stream's footer), */
/* and a .zst's frame headers (walked block header by block header, so a      */
/* file of several frames is summed rather than taken from its first).  A     */
/* gzip trailer holds the size only modulo 4 GiB, which is useless for a      */
/* card image, so gzip's size is unknown.                                     */
/*                                                                            */
/* Compiled with the compression libraries' include path and LZMA_API_STATIC  */
/* (liblzma is linked into the DLL, not imported from one).  zstd's frame     */
/* header reader is one of its "static linking only" functions, which is      */
/* exactly how it is linked here.                                             */
/*----------------------------------------------------------------------------*/

/* The C library; the engine's public codes, shared internals and this        */
/* file's own interface (which brings in windows.h, for HANDLE and the file   */
/* calls); then liblzma (.xz) and zlib (.gz and deflate).                     */
#include <stdlib.h>
#include <string.h>
#include "bifrost_engine.h"
#include "bfe_internal.h"
#include "bfe_compress.h"
#include "lzma.h"
#include "zlib.h"

/* zstd, with its "static linking only" functions made visible -- which       */
/* must be defined BEFORE the header is included, or they are not declared.   */
#define ZSTD_STATIC_LINKING_ONLY         // ZSTD_getFrameHeader: the library is linked in statically
#include "zstd.h"

/*----------------------------------------------------------------------------*/
/* Definitions -- documentation table: buffer sizes and the writer's level,   */
/* then the zip signatures, then the zstd format constants.                   */
/*----------------------------------------------------------------------------*/
#define BFZ_IN_BUFFER   (1u << 20)   // bytes read from the file at a time
#define BFZ_OUT_BUFFER  (1u << 20)   // bytes of .xz held before each write to the file
#define BFZ_XZ_PRESET   3            // the writer's level: the highest of liblzma's FAST-mode levels (hash-chain match finder, 4 MiB dictionary -- lzma_encoder_presets.c); 4 and up switch to the slower NORMAL mode with a binary-tree finder, which xz's default 6 uses with 8 MiB.  Chosen for speed because a card read is waited on; the size cost against 6 was not measured here
#define BFZ_EOCD_SCAN   (65536 + 22) // the end-of-central-directory record (22 bytes plus a comment whose 16-bit length allows at most 65 535) lies within this many bytes of a zip's end -- one byte to spare

#define BFZ_ZIP_LOCAL   0x04034B50u  // zip local file header signature
#define BFZ_ZIP_CENTRAL 0x02014B50u  // zip central directory header signature
#define BFZ_ZIP_EOCD    0x06054B50u  // zip end of central directory signature
#define BFZ_ZIP64_LOC   0x07064B50u  // zip64 end of central directory locator signature
#define BFZ_ZIP64_EOCD  0x06064B50u  // zip64 end of central directory record signature

#define BFZ_ZSTD_MAGIC      0xFD2FB528u  // a zstd frame's magic (28 B5 2F FD in the file)
#define BFZ_ZSTD_SKIP_MASK  0xFFFFFFF0u  // a zstd skippable frame's magic, low four bits ignored
#define BFZ_ZSTD_SKIP_MAGIC 0x184D2A50u  // ... and the value it then has
#define BFZ_ZSTD_HEADER_MAX 18           // the longest a zstd frame header can be (ZSTD_FRAMEHEADERSIZE_MAX)
#define BFZ_ZSTD_BLOCK_RLE  1            // a zstd block header's type field (RFC 8878 section 3.1.1.2.2): one byte repeated (one byte of data, whatever its size says)
#define BFZ_ZSTD_BLOCK_BAD  3            // ... the reserved type, which no valid frame holds (0 raw and 2 compressed carry their size's worth of data)

/*----------------------------------------------------------------------------*/
/* BFZ_Reader -- one open compressed image being read: the file, what it is,  */
/* the input buffer, and the one decoder state its format uses.  The type is  */
/* opaque outside this file (bfe_compress.h declares only its name).          */
/*----------------------------------------------------------------------------*/
struct BFZ_Reader
{
    HANDLE         hFile;        // the image file, read from the start
    int            iKind;        // BFZ_KIND_*
    uint64_t       ullSize;      // the uncompressed size, or BFZ_SIZE_UNKNOWN
    uint64_t       ullFileSize;  // the file's size
    uint64_t       ullDataStart; // where the compressed data starts in the file (a zip entry's data; 0 otherwise)
    uint64_t       ullReadIn;    // bytes read from the file into pbIn, all told
    uint8_t       *pbIn;         // the input buffer, BFZ_IN_BUFFER bytes
    uint32_t       cbIn;         // bytes in pbIn
    uint32_t       offIn;        // the first of them not yet used
    int            bInEof;       // the file (or the zip entry) has no more bytes
    int            bDone;        // the compressed data has ended: reads return 0 bytes
    uint64_t       ullZipLeft;   // zip: bytes of the entry's data not yet read from the file
    int            iZipMethod;   // zip: 0 stored, 8 deflate
    uint32_t       uiZipCrc;     // zip: the entry's CRC-32, as its central directory header records it
    uint32_t       uiZipCrcRun;  // zip: the CRC-32 of every image byte delivered so far
    uint64_t       ullZipOut;    // zip: image bytes delivered so far
    int            bZipInflated; // zip, deflate: inflate has reported the end of the entry's data
    int            bZipChecked;  // zip: the entry's end has been reached and its size and CRC-32 judged good
    lzma_stream    xz;           // BFZ_KIND_XZ
    z_stream       gz;           // BFZ_KIND_GZIP, and a deflated zip entry
    int            bGzOpen;      // gz holds inflate state to free
    ZSTD_DStream  *pZstd;        // BFZ_KIND_ZSTD
    int            bZstdOpen;    // zstd: the last call that made progress left a frame unfinished
};

/*----------------------------------------------------------------------------*/
/* BFZ_Writer -- one .xz file being written: the file, the encoder, and the   */
/* output waiting to be written.  Opaque outside this file, like the reader.  */
/*----------------------------------------------------------------------------*/
struct BFZ_Writer
{
    HANDLE         hFile;        // the .xz being written
    lzma_stream    xz;           // the encoder
    uint8_t       *pbOut;        // BFZ_OUT_BUFFER bytes of output waiting
};

/*----------------------------------------------------------------------------*/
/* Bfz_Le16:                                                                  */
/*                                                                            */
/* A 16-bit little-endian field, read byte by byte so the answer is the same  */
/* on any host and at any alignment (zip and zstd headers are byte-packed).   */
/*                                                                            */
/* Arguments:                                                                 */
/*     pb : the field's first byte.                                           */
/*                                                                            */
/* Returns:                                                                   */
/*     uint32_t : the value.                                                  */
/*----------------------------------------------------------------------------*/
static uint32_t Bfz_Le16(const uint8_t *pb) { return((uint32_t)pb[0] | ((uint32_t)pb[1] << 8)); }

/*----------------------------------------------------------------------------*/
/* Bfz_Le32:                                                                  */
/*                                                                            */
/* A 32-bit little-endian field: two 16-bit halves, low first.  The halves    */
/* come back as uint32_t, so the shift by 16 is done unsigned: a signed int   */
/* would overflow on a high half of 0x8000 or more, which C leaves undefined. */
/*                                                                            */
/* Arguments:                                                                 */
/*     pb : the field's first byte.                                           */
/*                                                                            */
/* Returns:                                                                   */
/*     uint32_t : the value.                                                  */
/*----------------------------------------------------------------------------*/
static uint32_t Bfz_Le32(const uint8_t *pb) { return(Bfz_Le16(pb) | (Bfz_Le16(pb + 2) << 16)); }

/*----------------------------------------------------------------------------*/
/* Bfz_Le64:                                                                  */
/*                                                                            */
/* A 64-bit little-endian field (the zip64 sizes and offsets): two 32-bit     */
/* halves, low first.  Each half is widened to 64 bits BEFORE the shift: a    */
/* 32-bit value shifted by 32 is undefined in C (x86's shift instruction      */
/* masks the count, so it tends to come back unshifted), and a zip64 size     */
/* would lose its high half.                                                  */
/*                                                                            */
/* Arguments:                                                                 */
/*     pb : the field's first byte.                                           */
/*                                                                            */
/* Returns:                                                                   */
/*     uint64_t : the value.                                                  */
/*----------------------------------------------------------------------------*/
static uint64_t Bfz_Le64(const uint8_t *pb) { return((uint64_t)Bfz_Le32(pb) | ((uint64_t)Bfz_Le32(pb + 4) << 32)); }

/*----------------------------------------------------------------------------*/
/* Bfz_ReadAt:                                                                */
/*                                                                            */
/* Read exactly cb bytes at a byte offset of the file -- the random access    */
/* the size-finding at open needs (a zip's directory, a zstd frame header).   */
/* A short read is a failure: every caller needs the whole field.             */
/*                                                                            */
/* IT MOVES THE FILE POSITION, AND THE STREAMING READS DEPEND ON THAT.        */
/* Bfz_Refill and Bfz_TopUp read from wherever the position is, without       */
/* seeking, so after the open's random reads the open puts the position back  */
/* where the data starts by calling this with cb == 0 -- a seek alone: a      */
/* zero-byte ReadFile reads nothing and succeeds.  Every compressed open      */
/* depends on that, so it is exercised on every open.  The offsets and sizes  */
/* here are arbitrary, which is why the handle must not be unbuffered (an     */
/* unbuffered read needs sector-aligned ones) -- bfe_compress.h says so.      */
/*                                                                            */
/* Arguments:                                                                 */
/*     hFile     : the open image file.                                       */
/*     ullOffset : where to read.                                             */
/*     pv        : receives the bytes.                                        */
/*     cb        : how many.                                                  */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK, or BFE_ERR_IO_IMAGE (recorded) when the bytes are not    */
/*           all there.                                                       */
/*----------------------------------------------------------------------------*/
static int Bfz_ReadAt(HANDLE hFile, uint64_t ullOffset, void *pv, uint32_t cb)
{
    LARGE_INTEGER liPos;
    DWORD         dwGot;

    /* The offset as Windows takes it (signed; real offsets are far below 2^63). */
    liPos.QuadPart = (LONGLONG)ullOffset;

    /* Seek, read, and demand every byte, in one test: the first that fails   */
    /* ends it, and all three failures mean the same to the caller -- so      */
    /* an offset past the end (a damaged or truncated file whose fields       */
    /* point beyond it) fails here whichever of the seek or the read is the   */
    /* one Windows refuses.                                                   */
    if (!SetFilePointerEx(hFile, liPos, NULL, FILE_BEGIN) || !ReadFile(hFile, pv, cb, &dwGot, NULL) || dwGot != cb)
    {   /* Past the end, or the read failed. */

        /* Report it with the offset.  No Windows error number is added: a   */
        /* read cut short by the end of the file leaves none to give.  The   */
        /* offset says where the file ran short or failed, which for a       */
        /* truncated file is the useful fact.  Nothing to release -- the     */
        /* handle and the buffer are the caller's.                           */
        return(BFX_Fail(BFE_ERR_IO_IMAGE, "the image file could not be read at byte %llu", (unsigned long long)ullOffset));
    }

    /* All of it read. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFZ_Sniff:                                                                 */
/*                                                                            */
/* The format the first bytes of a file say it is -- by its magic number,     */
/* never by its name, so a renamed file is still read right.                  */
/*                                                                            */
/* What a magic CANNOT do is prove the format: a                              */
/* plain image whose first bytes happened to match would be taken for that    */
/* format and refused as damaged, at open or at the first read.  gzip's is    */
/* the weakest at two bytes (ID1 ID2, RFC 1952 section 2.3.1); the third      */
/* byte, CM, can only be 8 (deflate -- the one method the RFC defines) and is */
/* not tested.  And a zip is recognised only by a                             */
/* local header at byte 0, so one that does not start with one -- a           */
/* self-extracting archive, or an empty zip -- is taken as a plain image.     */
/*                                                                            */
/* Exported in bfe_compress.h; its one caller is BFZ_ReaderOpen.              */
/*                                                                            */
/* Arguments:                                                                 */
/*     pbHead : the file's first bytes.                                       */
/*     cbHead : how many there are.                                           */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFZ_KIND_XZ, _GZIP, _ZSTD, _ZIP, or BFZ_KIND_RAW for anything    */
/*           else.                                                            */
/*----------------------------------------------------------------------------*/
int BFZ_Sniff(const uint8_t *pbHead, size_t cbHead)
{
    static const uint8_t abXz[6] = { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 };  // the xz stream header magic: FD then "7zXZ" then NUL

    /* The four magics, each behind its length test, so a file shorter      */
    /* than a magic is simply not that format rather than a read past the   */
    /* buffer.  They share no first byte (FD, 1F, 28, 50), so the order     */
    /* of the tests decides nothing.                                        */
    if (cbHead >= 6 && memcmp(pbHead, abXz, 6) == 0)
    {   /* An xz stream header. */
        return(BFZ_KIND_XZ);
    }

    if (cbHead >= 2 && pbHead[0] == 0x1F && pbHead[1] == 0x8B)
    {   /* A gzip member. */
        return(BFZ_KIND_GZIP);
    }

    if (cbHead >= 4 && Bfz_Le32(pbHead) == BFZ_ZSTD_MAGIC)
    {   /* A zstd frame.  (One that opens with a skippable frame is told by   */
        /* the reader, which can look past it; four bytes cannot.)            */
        return(BFZ_KIND_ZSTD);
    }

    if (cbHead >= 4 && Bfz_Le32(pbHead) == BFZ_ZIP_LOCAL)
    {   /* A zip local file header ("PK" 3 4). */
        return(BFZ_KIND_ZIP);
    }

    /* None of them: a plain image -- which the open then reads byte for    */
    /* byte as it is, so this is also what any unrecognised file becomes.   */
    return(BFZ_KIND_RAW);
}

/*----------------------------------------------------------------------------*/
/* Bfz_EocdPossible:                                                          */
/*                                                                            */
/* Whether four signature bytes found in a zip's tail can be its end-of-      */
/* central-directory record, judged by the record's own fields.  A match in   */
/* the comment that follows the real record fails at least one of the tests   */
/* unless it was built to pass them -- the search then goes on rather than    */
/* misreading the zip.                                                        */
/*                                                                            */
/* Arguments:                                                                 */
/*     pbTail      : the file's last cbTail bytes.                            */
/*     i           : the candidate's offset in pbTail; i + 22 <= cbTail.      */
/*     cbTail      : the tail's length.                                       */
/*     ullFileSize : the file's length, so the candidate's place in the file  */
/*                   is ullFileSize - cbTail + i.                             */
/*                                                                            */
/* Returns:                                                                   */
/*     int : 1 when the candidate can be the record; 0 when its fields rule   */
/*           it out.                                                          */
/*----------------------------------------------------------------------------*/
static int Bfz_EocdPossible(const uint8_t *pbTail, uint32_t i, uint32_t cbTail, uint64_t ullFileSize)
{
    uint32_t uiComment;
    uint32_t uiCdSize;
    uint32_t uiCdOffset;

    /* The comment's length, at byte 20: the record plus its comment must    */
    /* end exactly where the file does (the format puts nothing after it).   */
    uiComment = Bfz_Le16(pbTail + i + 20);

    if ((uint64_t)i + 22 + uiComment != (uint64_t)cbTail)
    {   /* Ends short of the file's end, or past it: not the record. */
        return(0);
    }

    /* The directory's size and offset, at bytes 12 and 16. */
    uiCdSize   = Bfz_Le32(pbTail + i + 12);
    uiCdOffset = Bfz_Le32(pbTail + i + 16);

    if (uiCdOffset == 0xFFFFFFFFu)
    {   /* ZIP64: the real offset is in the zip64 record, found through the   */
        /* locator in the 20 bytes just before this record -- which must be   */
        /* there.                                                             */
        return(i >= 20 && Bfz_Le32(pbTail + i - 20) == BFZ_ZIP64_LOC);
    }

    /* A 32-bit offset: the directory (offset plus size, the format's fields   */
    /* at 16 and 12) must lie wholly before the record, ending at or before    */
    /* it.                                                                     */
    return((uint64_t)uiCdOffset + uiCdSize <= ullFileSize - cbTail + i);
}

/*----------------------------------------------------------------------------*/
/* Bfz_ZipFirstEntry:                                                         */
/*                                                                            */
/* Find the zip's FIRST file through its central directory (sizes in a local  */
/* header may be zero when a data descriptor follows the data, so the central */
/* directory is the one place they are sure), and leave the reader positioned */
/* at that entry's data.                                                      */
/*                                                                            */
/* Arguments:                                                                 */
/*     pR          : the reader; receives the entry's sizes, method and data  */
/*                   offset.                                                  */
/*     ullFileSize : the zip file's size.                                     */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK, or a recorded BFE_ERR_IO_IMAGE / BFE_ERR_NO_MEMORY.      */
/*----------------------------------------------------------------------------*/
static int Bfz_ZipFirstEntry(BFZ_Reader *pR, uint64_t ullFileSize)
{
    uint8_t  *pbTail;
    uint32_t  cbTail;
    uint8_t   abRec[64];
    uint8_t   abCen[46];
    uint8_t  *pbExtra;
    uint64_t  ullCd;
    uint64_t  ullUsize;
    uint64_t  ullCsize;
    uint64_t  ullLocal;
    uint32_t  cbName;
    uint32_t  cbExtra;
    uint32_t  i;
    uint32_t  offEocd;
    uint32_t  offField;
    int       iStatus;
    int       bFound;

    /* The end-of-central-directory record: searched for backwards in the     */
    /* tail -- the last BFZ_EOCD_SCAN bytes, or the whole of a smaller zip.   */
    cbTail = BFZ_EOCD_SCAN;

    if (ullFileSize < cbTail)
    {   /* A zip smaller than the scan: all of it is the tail. */
        cbTail = (uint32_t)ullFileSize;
    }

    if (cbTail < 22)
    {   /* Too small to hold the 22-byte record at all.  Refused with the   */
        /* same words as a record that is not found, since to the user it   */
        /* is the same fault: this is not a whole zip.  Nothing has been    */
        /* allocated yet, so there is nothing to free.                      */
        return(BFX_Fail(BFE_ERR_IO_IMAGE, "the zip file has no central directory"));
    }

    /* The tail is read whole into memory and searched there: one read of   */
    /* at most 64 KiB + 22 bytes, where searching the file in place would   */
    /* mean a seek and a read for every candidate position.                 */
    pbTail = (uint8_t *)malloc(cbTail);

    if (pbTail == NULL)
    {   /* No memory for the tail: refused before anything else is held. */
        return(BFX_Fail(BFE_ERR_NO_MEMORY, "no memory to read the zip's directory"));
    }

    /* Read the tail, and search it from the last place a 22-byte record      */
    /* could start back towards the beginning; the first match found that     */
    /* could BE the record (Bfz_EocdPossible) is taken.  The zip comment      */
    /* follows the record, so a comment holding the four signature bytes is   */
    /* met first from the end: such a match is passed over and the search     */
    /* goes on.                                                               */
    /*                                                                        */
    /* The search starts at cbTail - 22, the last offset at which a whole     */
    /* 22-byte record still fits, so a record is never read past the          */
    /* buffer's end.  A failed read skips the loop (its condition) and is     */
    /* handled just after it.                                                 */
    iStatus = Bfz_ReadAt(pR->hFile, ullFileSize - cbTail, pbTail, cbTail);
    bFound  = 0;
    offEocd = 0;
    i       = cbTail - 22;

    while (iStatus == BFE_OK && !bFound)
    {   /* One candidate position per iteration, from the end backwards. */
        if (Bfz_Le32(pbTail + i) == BFZ_ZIP_EOCD && Bfz_EocdPossible(pbTail, i, cbTail, ullFileSize))
        {   /* The record. */
            bFound  = 1;
            offEocd = i;
        }
        else if (i == 0)
        {   /* Searched to the start of the tail without finding it.  Tested   */
            /* BEFORE the decrement below: i is unsigned, and stepping back    */
            /* from 0 would wrap to 4 billion and read far outside pbTail.     */
            break;
        }
        else
        {   /* One byte further back. */
            i--;
        }
    }

    if (iStatus != BFE_OK)
    {   /* The tail could not be read. */

        /* Free the tail and pass the failure on.  Bfz_ReadAt has already    */
        /* recorded the reason with the offset it failed at, which is more   */
        /* use to the user than anything this function could add.            */
        free(pbTail);
        return(iStatus);
    }

    if (!bFound)
    {   /* Not a zip after all, or one cut short. */

        /* Free the tail and refuse, in the same words as a file too small   */
        /* to hold the record: either way the zip's end is missing, which    */
        /* is what a download cut short looks like.                          */
        free(pbTail);
        return(BFX_Fail(BFE_ERR_IO_IMAGE, "the zip file has no central directory"));
    }

    /* The central directory's offset, at byte 16 of the record.  0xFFFFFFFF   */
    /* there means "too large for 32 bits: see the zip64 record", whose        */
    /* locator sits in the 20 bytes just before this record.  A record         */
    /* taken with that offset always has its locator inside the tail           */
    /* buffer -- Bfz_EocdPossible passed it only so -- so the two locator      */
    /* tests below are a guard.  The cost of that rule: a ZIP64 zip whose      */
    /* comment is so long that its record sits in the buffer's first 20        */
    /* bytes has no candidate taken at all, and is refused above as having     */
    /* no central directory.                                                   */
    ullCd = Bfz_Le32(pbTail + offEocd + 16);

    if (ullCd == 0xFFFFFFFFu && offEocd >= 20 && Bfz_Le32(pbTail + offEocd - 20) == BFZ_ZIP64_LOC)
    {   /* ZIP64: the locator names the zip64 record, which holds the real offset. */

        /* Read the 56-byte zip64 record from where the locator points.  A    */
        /* record without its signature leaves ullCd at 0xFFFFFFFF, and the   */
        /* central-header read below then refuses the zip as damaged.         */
        iStatus = Bfz_ReadAt(pR->hFile, Bfz_Le64(pbTail + offEocd - 20 + 8), abRec, 56);

        if (iStatus == BFE_OK && Bfz_Le32(abRec) == BFZ_ZIP64_EOCD)
        {   /* The record read: the directory's offset is at byte 48. */
            ullCd = Bfz_Le64(abRec + 48);
        }
    }

    /* The tail has given what it holds: the directory's offset is copied   */
    /* out, so the buffer is freed here, BEFORE the reads below -- every    */
    /* early return after this point then has nothing of its own to free.   */
    free(pbTail);

    if (iStatus != BFE_OK)
    {   /* The zip64 record could not be read: Bfz_ReadAt has recorded where. */
        return(iStatus);
    }

    /* The first central header: method, sizes, where its local header is.   */
    /* Only the FIRST entry is read: the public contract (BFE_IMAGE_ZIP in   */
    /* bifrost_engine.h) is that the archive's first file is the image.      */
    iStatus = Bfz_ReadAt(pR->hFile, ullCd, abCen, sizeof(abCen));

    if (iStatus != BFE_OK)
    {   /* The directory could not be read -- an offset past the file's end   */
        /* lands here, with Bfz_ReadAt's offset in the recorded text.         */
        return(iStatus);
    }

    if (Bfz_Le32(abCen) != BFZ_ZIP_CENTRAL)
    {   /* No first entry where the record says: the zip is damaged, or a    */
        /* record-shaped run of bytes in its comment passed every test of    */
        /* Bfz_EocdPossible (which no tool is known to write).  Either way   */
        /* the sizes cannot be trusted, so nothing more is read.             */
        return(BFX_Fail(BFE_ERR_IO_IMAGE, "the zip file's directory is damaged"));
    }

    /* The fields of the first central header, at their fixed offsets: the     */
    /* method (10), the CRC-32 (16), the compressed (20) and uncompressed      */
    /* (24) sizes, the name and extra-field lengths (28, 30), the local        */
    /* header's offset (42).  The CRC is the central header's, not the local   */
    /* one's: an entry written with a data descriptor has zeros in its local   */
    /* header, and the central directory always has the real value.            */
    pR->iZipMethod = (int)Bfz_Le16(abCen + 10);
    pR->uiZipCrc   = Bfz_Le32(abCen + 16);
    ullCsize       = Bfz_Le32(abCen + 20);
    ullUsize       = Bfz_Le32(abCen + 24);
    cbName         = Bfz_Le16(abCen + 28);
    cbExtra        = Bfz_Le16(abCen + 30);
    ullLocal       = Bfz_Le32(abCen + 42);

    if (pR->iZipMethod != 0 && pR->iZipMethod != 8)
    {   /* Only stored (0) and deflate (8) are read -- the two that zlib's    */
        /* inflate covers, and what zip tools write by default.  Any other    */
        /* (deflate64, bzip2, LZMA, ...) is refused with its method number,   */
        /* so the user can tell which one the archive used.  Nothing is       */
        /* held at this point, so there is nothing to free.                   */
        return(BFX_Fail(BFE_ERR_UNSUPPORTED, "the zip's image is compressed with method %d; only stored and deflate are read", pR->iZipMethod));
    }

    if (cbExtra != 0)
    {   /* ZIP64 sizes: extra field 0x0001 holds, in order, each 32-bit field that reads 0xFFFFFFFF. */

        /* The extra field can be up to 65 535 bytes (a 16-bit length), so   */
        /* it is read into the heap rather than a buffer on the stack.       */
        pbExtra = (uint8_t *)malloc(cbExtra);

        if (pbExtra == NULL)
        {   /* No memory for the field: nothing else is held, so just refuse. */
            return(BFX_Fail(BFE_ERR_NO_MEMORY, "no memory to read the zip's directory"));
        }

        /* The extra field follows the 46-byte header and the name.  A       */
        /* failed read skips the walk below (its condition) and is handled   */
        /* after the buffer is freed.                                        */
        iStatus = Bfz_ReadAt(pR->hFile, ullCd + 46 + cbName, pbExtra, cbExtra);

        for (i = 0; iStatus == BFE_OK && i + 4 <= cbExtra; i += 4 + Bfz_Le16(pbExtra + i + 2))
        {   /* One extra-field block per iteration: a 2-byte id, a 2-byte length, the data. */

            /* The block's data starts after its 4-byte id and length.  Each   */
            /* zip64 field below is present only for a 32-bit field that       */
            /* read 0xFFFFFFFF, in that order, and is read only if it fits.    */
            offField = i + 4;

            if (Bfz_Le16(pbExtra + i) != 0x0001)
            {   /* Not the ZIP64 block (a timestamp or a Unix-owner block, say).   */
                /* The continue still runs the loop's step, which skips this       */
                /* block by its own length; a length that runs past the field's    */
                /* end simply ends the walk at the loop's test, never reading      */
                /* outside pbExtra.                                                */
                continue;
            }

            if (ullUsize == 0xFFFFFFFFu && offField + 8 <= cbExtra)
            {   /* The uncompressed size. */
                ullUsize  = Bfz_Le64(pbExtra + offField);
                offField += 8;
            }

            if (ullCsize == 0xFFFFFFFFu && offField + 8 <= cbExtra)
            {   /* The compressed size. */
                ullCsize  = Bfz_Le64(pbExtra + offField);
                offField += 8;
            }

            if (ullLocal == 0xFFFFFFFFu && offField + 8 <= cbExtra)
            {   /* The local header's offset. */
                ullLocal = Bfz_Le64(pbExtra + offField);
            }
        }

        /* The extra field has given what it holds; freed before the check   */
        /* below so the failure return has nothing left to free.             */
        free(pbExtra);

        if (iStatus != BFE_OK)
        {   /* The extra field could not be read: Bfz_ReadAt recorded where. */
            return(iStatus);
        }
    }

    /* The local header: its own name and extra lengths say where the data starts. */
    iStatus = Bfz_ReadAt(pR->hFile, ullLocal, abRec, 30);

    if (iStatus != BFE_OK)
    {   /* The local header could not be read (an offset past the end lands   */
        /* here); Bfz_ReadAt recorded where.                                  */
        return(iStatus);
    }

    if (Bfz_Le32(abRec) != BFZ_ZIP_LOCAL)
    {   /* The entry is not where the directory says.  Decoding from there   */
        /* would read the wrong bytes as the image, so it is refused here    */
        /* rather than left for the decoder to fail on (stored data has no   */
        /* check at all -- it would be copied to the card as it is).         */
        return(BFX_Fail(BFE_ERR_IO_IMAGE, "the zip file's entry is damaged"));
    }

    /* The entry's facts for the reader: the image's size, the compressed     */
    /* bytes still to read, and where they start -- after the 30-byte local   */
    /* header and the LOCAL name and extra lengths (bytes 26 and 28), which   */
    /* may differ from the central header's, so they are read from here.      */
    pR->ullSize      = ullUsize;
    pR->ullZipLeft   = ullCsize;
    pR->ullDataStart = ullLocal + 30 + Bfz_Le16(abRec + 26) + Bfz_Le16(abRec + 28);

    /* Nothing delivered yet: the running CRC starts as zlib's empty one,   */
    /* and the entry's end is still to come (BFZ_ReaderRead, Bfz_ZipEnd).   */
    pR->uiZipCrcRun  = (uint32_t)crc32(0L, Z_NULL, 0);
    pR->ullZipOut    = 0;
    pR->bZipInflated = 0;
    pR->bZipChecked  = 0;

    /* Positioned at the data: the next ReadFile reads the entry.  A read of    */
    /* zero bytes is used as the seek -- Bfz_ReadAt's seek with nothing read.   */
    return(Bfz_ReadAt(pR->hFile, pR->ullDataStart, abRec, 0));
}

/*----------------------------------------------------------------------------*/
/* Bfz_XzSize:                                                                */
/*                                                                            */
/* The image's size from a .xz file's index, without decompressing it: the    */
/* sum of every concatenated stream's uncompressed size, through liblzma's    */
/* file-info decoder.  A file the decoder rejects is reported, not guessed    */
/* at: an index that cannot be read is what a download cut short looks like,  */
/* and saying so here stops it before a card is touched.                      */
/*                                                                            */
/* Arguments:                                                                 */
/*     pR        : the reader; its input buffer is used as scratch, and the   */
/*                 file position is left wherever the decoder last read.      */
/*     pullSize  : receives the size.                                         */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_IO_IMAGE for an unreadable or damaged .xz;       */
/*           BFE_ERR_UNSUPPORTED for headers this liblzma does not know;      */
/*           BFE_ERR_NO_MEMORY.                                               */
/*----------------------------------------------------------------------------*/
static int Bfz_XzSize(BFZ_Reader *pR, uint64_t *pullSize)
{
    lzma_stream  strm;
    lzma_index  *pIndex;
    lzma_ret     ret;
    lzma_action  action;
    uint64_t     ullPos;
    uint32_t     cbAsk;
    DWORD        dwGot;
    int          iStatus;

    /* A fresh decoder stream, no index yet, and the size "unknown" until   */
    /* the decoder has read every stream's index.                           */
    strm      = (lzma_stream)LZMA_STREAM_INIT;
    pIndex    = NULL;
    *pullSize = BFZ_SIZE_UNKNOWN;

    /* Start liblzma's file-info decoder: no memory limit (UINT64_MAX), and   */
    /* the file's size, which it needs to find the last stream's footer.      */
    if (lzma_file_info_decoder(&strm, &pIndex, UINT64_MAX, pR->ullFileSize) != LZMA_OK)
    {   /* The decoder would not start.  With valid arguments its only      */
        /* failure is memory, hence NO_MEMORY.  liblzma's start-up frees    */
        /* what it took when it fails (lzma_next_strm_init, file_info.c),   */
        /* so there is no lzma_end here and nothing else is held.           */
        return(BFX_Fail(BFE_ERR_NO_MEMORY, "the xz index reader could not be started"));
    }

    /* Reading starts at the file's first byte, with more input to come.     */
    /* ullPos tracks the FILE position of the next byte handed over, which   */
    /* is what a seek request from the decoder replaces.                     */
    ullPos = 0;
    action = LZMA_RUN;

    /* Position the file at the start; the decoder reads the stream header   */
    /* first.  BFZ_ReaderOpen has rewound already after its sniff; the       */
    /* seek here keeps this function correct on its own account.             */
    iStatus = Bfz_ReadAt(pR->hFile, 0, pR->pbIn, 0);

    for (;;)
    {   /* One decoder step per iteration, fed from wherever it last asked. */
        if (iStatus != BFE_OK)
        {   /* A seek or read failed -- tested first in every pass, because   */
            /* the seek requested at the bottom of the previous pass sets     */
            /* iStatus and then continues straight here.  The reason is       */
            /* already recorded; the stream and index are released after      */
            /* the loop.                                                      */
            break;
        }

        if (strm.avail_in == 0)
        {   /* Feed the next piece of the file from ullPos. */
            cbAsk = BFZ_IN_BUFFER;

            if (pR->ullFileSize - ullPos < cbAsk)
            {   /* The file ends sooner. */
                cbAsk = (uint32_t)(pR->ullFileSize - ullPos);
            }

            if (cbAsk != 0 && (!ReadFile(pR->hFile, pR->pbIn, cbAsk, &dwGot, NULL) || dwGot != cbAsk))
            {   /* The read failed, or came back short -- the file was shorter   */
                /* than its size said, or changed under the read.  Recorded      */
                /* with the file offset, then out to the release below.          */
                iStatus = BFX_Fail(BFE_ERR_IO_IMAGE, "the .xz file could not be read at byte %llu", (unsigned long long)ullPos);
                break;
            }

            /* Hand the piece to the decoder and move the file position on. */
            strm.next_in  = pR->pbIn;
            strm.avail_in = cbAsk;
            ullPos       += cbAsk;

            /* At the file's end the decoder is told so; until then, RUN.     */
            /* FINISH is what makes a file cut short come back as an error    */
            /* (LZMA_DATA_ERROR / BUF_ERROR) instead of the decoder waiting   */
            /* for input that will never come.                                */
            action = LZMA_RUN;

            if (ullPos >= pR->ullFileSize)
            {   /* This piece ends the file. */
                action = LZMA_FINISH;
            }
        }

        /* One step of the decoder on what it has been given. */
        ret = lzma_code(&strm, action);

        if (ret == LZMA_SEEK_NEEDED)
        {   /* It wants input from elsewhere: go there, and drop what was      */
            /* buffered.  After the first stream header the decoder asks for   */
            /* each stream's footer and index in turn, walking back from the   */
            /* end over every concatenated stream and its padding.             */

            /* Move to the position it named, empty the buffer so the next   */
            /* pass reads from there, and seek the file (a 0-byte read).     */
            ullPos        = strm.seek_pos;
            strm.avail_in = 0;
            iStatus       = Bfz_ReadAt(pR->hFile, ullPos, pR->pbIn, 0);
            continue;
        }

        if (ret == LZMA_STREAM_END)
        {   /* Every stream's index read: their sizes summed. */
            *pullSize = lzma_index_uncompressed_size(pIndex);
            break;
        }

        if (ret == LZMA_OPTIONS_ERROR)
        {   /* Headers this liblzma does not know.  Kept apart from "damaged"   */
            /* below because it is a different fault: the file may be           */
            /* perfectly good, written with options this build lacks.           */
            iStatus = BFX_Fail(BFE_ERR_UNSUPPORTED, "the .xz file uses headers this program does not read");
            break;
        }

        if (ret != LZMA_OK)
        {   /* Damaged, cut short, or not .xz after all.  liblzma's own code   */
            /* is put in the text, since it is the only clue to which.         */
            iStatus = BFX_Fail(BFE_ERR_IO_IMAGE, "the .xz file is damaged or incomplete: its index cannot be read (liblzma %d)", (int)ret);
            break;
        }
    }

    /* Release the decoder, then the index it built (if it built one).  The   */
    /* same release runs on every way out of the loop -- success, a failed    */
    /* read or seek, an unsupported or damaged file -- which is why every     */
    /* failure above breaks rather than returns.  The size was copied out     */
    /* of the index before this, so freeing it loses nothing.                 */
    lzma_end(&strm);

    if (pIndex != NULL)
    {   /* The combined index the decoder built. */
        lzma_index_end(pIndex, NULL);
    }

    /* The size, or why not. */
    return(iStatus);
}

/*----------------------------------------------------------------------------*/
/* Bfz_ZstdSize:                                                              */
/*                                                                            */
/* The image's size from a .zst file's frame headers, without decompressing:  */
/* the sum of every frame's recorded size, given only when the walk over the  */
/* frames lands exactly on the file's end with every frame's size recorded.   */
/* Anything else -- a frame without one, trailing bytes, a malformed header   */
/* -- leaves it UNKNOWN rather than failing, because the decoder itself is    */
/* the judge of a damaged frame and reports it when it gets there.            */
/*                                                                            */
/* Arguments:                                                                 */
/*     pR       : the reader (its file size is used).                         */
/*     pullSize : receives the size, or BFZ_SIZE_UNKNOWN.                     */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK (an unknown size is not a failure); BFE_ERR_IO_IMAGE when */
/*           a read fails.                                                    */
/*----------------------------------------------------------------------------*/
static int Bfz_ZstdSize(BFZ_Reader *pR, uint64_t *pullSize)
{
    ZSTD_FrameHeader zfh;
    uint8_t          abHead[BFZ_ZSTD_HEADER_MAX];
    uint64_t         ullPos;
    uint64_t         ullTotal;
    uint32_t         cbHead;
    uint32_t         uiBlock;
    int              iStatus;
    int              bLast;

    /* "Unknown" until the walk reaches the file's end exactly; the walk   */
    /* starts at the file's first byte with nothing summed.                */
    *pullSize = BFZ_SIZE_UNKNOWN;
    ullPos    = 0;
    ullTotal  = 0;

    while (ullPos < pR->ullFileSize)
    {   /* One frame per iteration. */

        /* Read as much as the longest frame header, or what is left. */
        cbHead = BFZ_ZSTD_HEADER_MAX;

        if (pR->ullFileSize - ullPos < cbHead)
        {   /* The file ends within a header's length. */
            cbHead = (uint32_t)(pR->ullFileSize - ullPos);
        }

        if (cbHead < 8)
        {   /* Too short for any frame (the smallest, a skippable frame's    */
            /* magic and length, is 8 bytes): trailing bytes.  The size is   */
            /* left UNKNOWN and BFE_OK returned -- an odd tail is the        */
            /* decoder's to judge when it gets there, not a reason to        */
            /* refuse the file now.                                          */
            return(BFE_OK);
        }

        /* The frame's first bytes. */
        iStatus = Bfz_ReadAt(pR->hFile, ullPos, abHead, cbHead);

        if (iStatus != BFE_OK)
        {   /* The read failed: the one failure here that IS reported, since   */
            /* a file that cannot be read cannot be decoded either.  Nothing   */
            /* is held, so the recorded reason is simply passed on.            */
            return(iStatus);
        }

        if ((Bfz_Le32(abHead) & BFZ_ZSTD_SKIP_MASK) == BFZ_ZSTD_SKIP_MAGIC)
        {   /* A skippable frame: its own length follows the magic.  It adds   */
            /* nothing to the image; the walk steps over it.  A length that    */
            /* carries past the file's end ends the loop, and the size then    */
            /* stays unknown (ullPos is not the file's end).                   */
            ullPos += 8 + (uint64_t)Bfz_Le32(abHead + 4);
            continue;
        }

        if (ZSTD_getFrameHeader(&zfh, abHead, cbHead) != 0 || zfh.frameContentSize == ZSTD_CONTENTSIZE_UNKNOWN)
        {   /* Not a frame header, or one that does not record its size (the    */
            /* compressor records it only when it can -- zstd does for a file   */
            /* it was given whole): one frame of unknown size makes the whole   */
            /* image's size unknown, so the walk stops here with BFE_OK, the    */
            /* size still UNKNOWN.                                              */
            return(BFE_OK);
        }

        /* Count the frame's recorded size, step past its header, and walk   */
        /* its blocks to find where it ends -- a frame header does not say   */
        /* how long the frame is, only how much it decompresses to.          */
        ullTotal += zfh.frameContentSize;
        ullPos   += zfh.headerSize;
        bLast     = 0;

        while (!bLast)
        {   /* One block header per iteration, to the frame's last block. */

            /* Room for a block header?  ullPos is tested against the size     */
            /* FIRST: a block's size can carry it past the file's end, and     */
            /* the subtraction alone would then wrap to a huge value and let   */
            /* a read past the end through.  The additions stay far from       */
            /* wrapping: a block adds at most 2^21 + 3 bytes, and ullPos is    */
            /* within the file before each.                                    */
            if (ullPos >= pR->ullFileSize || pR->ullFileSize - ullPos < 3)
            {   /* The frame runs off the file's end (a file cut short): size   */
                /* UNKNOWN, BFE_OK -- the decoder is the judge of the damage.   */
                return(BFE_OK);
            }

            /* The block's 3-byte header. */
            iStatus = Bfz_ReadAt(pR->hFile, ullPos, abHead, 3);

            if (iStatus != BFE_OK)
            {   /* The read failed -- inside the file's length, since the test   */
                /* above has just said three bytes remain, so this is a real     */
                /* read error rather than a short file.  Reported, as the        */
                /* frame-header read's failure is; nothing is held, and          */
                /* Bfz_ReadAt has recorded the offset.                           */
                return(iStatus);
            }

            /* Bit 0: last block; bits 1-2: type; bits 3-23: size. */
            uiBlock = (uint32_t)abHead[0] | ((uint32_t)abHead[1] << 8) | ((uint32_t)abHead[2] << 16);
            bLast   = (int)(uiBlock & 1);
            ullPos += 3;

            if (((uiBlock >> 1) & 3) == BFZ_ZSTD_BLOCK_BAD)
            {   /* The reserved block type: not a valid frame.  Size UNKNOWN,   */
                /* BFE_OK -- the decoder reports the damage in its own words    */
                /* when it reaches this block.                                  */
                return(BFE_OK);
            }

            if (((uiBlock >> 1) & 3) == BFZ_ZSTD_BLOCK_RLE)
            {   /* RLE: one byte stands for the whole block -- its size field     */
                /* is the REGENERATED size, so stepping by it (as for the other   */
                /* types) would land far past the block's real end.               */
                ullPos += 1;
            }
            else
            {   /* Raw or compressed: the size is the bytes that follow. */
                ullPos += uiBlock >> 3;
            }
        }

        if (zfh.checksumFlag)
        {   /* The frame's content checksum follows its last block. */
            ullPos += 4;
        }
    }

    if (ullPos == pR->ullFileSize)
    {   /* Every frame accounted for, exactly to the end. */
        *pullSize = ullTotal;
    }

    /* The size, or unknown. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* Bfz_SniffPastSkippable:                                                    */
/*                                                                            */
/* A file that opens with a zstd SKIPPABLE frame (user metadata a tool may    */
/* write ahead of the data; which tools do is not established here) is zstd   */
/* if the first frame that is not skippable is a zstd frame.  Four bytes      */
/* cannot tell that, so the skippable frames are stepped over by their own    */
/* lengths until something else starts.  A skippable frame says nothing about */
/* what follows it -- the LZ4 frame format defines the same magic range --    */
/* which is why the real frame's magic is checked rather than assumed.        */
/*                                                                            */
/* Only the KIND is decided here.  The skippable frames themselves need no    */
/* handling afterwards: zstd's streaming decoder steps over them on its own   */
/* (its ZSTDds_skipFrame state, zstd_decompress.c), and Bfz_ZstdSize walks    */
/* past them when it adds up the size.  Anything else after them -- an LZ4    */
/* frame, garbage, or the end of the file -- leaves the kind RAW, and the     */
/* file is then read as a plain image, as BFZ_Sniff would have.               */
/*                                                                            */
/* Arguments:                                                                 */
/*     pR     : the reader (its file size is used); the file position is      */
/*              left at the start.                                            */
/*     piKind : set to BFZ_KIND_ZSTD when a zstd frame follows; untouched     */
/*              otherwise.                                                    */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; BFE_ERR_IO_IMAGE when a read fails.                      */
/*----------------------------------------------------------------------------*/
static int Bfz_SniffPastSkippable(BFZ_Reader *pR, int *piKind)
{
    uint8_t  abFrame[8];
    uint64_t ullPos;
    int      iStatus;

    /* From the file's first byte.  The loop's first test keeps the         */
    /* unsigned subtraction from wrapping when a skippable frame's length   */
    /* has carried ullPos past the end.                                     */
    ullPos = 0;

    while (ullPos <= pR->ullFileSize && pR->ullFileSize - ullPos >= 8)
    {   /* One frame's first eight bytes per iteration. */

        /* The frame's magic and, for a skippable frame, its length.  The     */
        /* loop test has made sure eight bytes are there, so a failure here   */
        /* is a real read error, not the end of the file.                     */
        iStatus = Bfz_ReadAt(pR->hFile, ullPos, abFrame, 8);

        if (iStatus != BFE_OK)
        {   /* The read failed: Bfz_ReadAt has recorded where.  The caller   */
            /* closes the reader, so the file position does not matter.      */
            return(iStatus);
        }

        if ((Bfz_Le32(abFrame) & BFZ_ZSTD_SKIP_MASK) != BFZ_ZSTD_SKIP_MAGIC)
        {   /* Past the skippable frames: zstd if this is a zstd frame. */
            if (Bfz_Le32(abFrame) == BFZ_ZSTD_MAGIC)
            {   /* It is. */
                *piKind = BFZ_KIND_ZSTD;
            }

            /* Either way the search is over: the first frame that is not   */
            /* skippable decides, and nothing after it is looked at.        */
            break;
        }

        /* Skippable: its length (the user data after the 8-byte header,    */
        /* RFC 8878 section 3.1.2) follows the magic.  Widened before the   */
        /* add, so a length near 4 GiB cannot wrap; a length that carries   */
        /* the position past the end is caught by the loop test.            */
        ullPos += 8 + (uint64_t)Bfz_Le32(abFrame + 4);
    }

    /* Back to the start.  This seek is what matters when the kind stays   */
    /* RAW: a plain image is streamed from the file position as it is,     */
    /* and the reads above have moved it.  (The zstd branch seeks again    */
    /* after sizing, so for it this one is merely harmless.)               */
    return(Bfz_ReadAt(pR->hFile, 0, abFrame, 0));
}

/*----------------------------------------------------------------------------*/
/* BFZ_ReaderOpen:                                                            */
/*                                                                            */
/* Tell the file's format, learn the image's size where the format records    */
/* it, and set up its decoder.  The file is read from its start; the caller   */
/* keeps the handle open until close.  Any failure closes what was set up.    */
/*                                                                            */
/* Arguments:                                                                 */
/*     hFile    : the open image file (not closed by the reader).             */
/*     ppReader : receives the reader, or NULL on failure.                    */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK, or a recorded BFE_ERR_IO_IMAGE / _NO_MEMORY /            */
/*           _UNSUPPORTED.                                                    */
/*----------------------------------------------------------------------------*/
int BFZ_ReaderOpen(HANDLE hFile, BFZ_Reader **ppReader)
{
    BFZ_Reader    *pR;
    uint8_t        abHead[16];
    uint32_t       cbHead;
    LARGE_INTEGER  liSize;
    int            iStatus;

    /* No reader until it is ready.  Every failure below returns with this   */
    /* NULL, and bfe_image.c relies on it: after a failed reopen it hands    */
    /* the same pointer to BFZ_ReaderClose (BFI_Finish), where NULL is a     */
    /* no-op -- a half-built reader left here would be freed twice.          */
    *ppReader = NULL;

    if (!GetFileSizeEx(hFile, &liSize))
    {   /* Not a file whose size can be read.  (A device never comes here:    */
        /* bfe_image.c hands the reader FILE sources only, and bfe_device.c   */
        /* sizes a disk by its geometry, not by this call.)  Refused before   */
        /* anything is allocated, so there is nothing to free.                */
        return(BFX_Fail(BFE_ERR_IO_IMAGE, "the image file's size could not be read"));
    }

    /* The reader, ZEROED: BFZ_ReaderClose, which every failure below goes    */
    /* through, frees only what bGzOpen, pZstd and the kind say was set up,   */
    /* so those must start empty.  A zeroed lzma_stream is also exactly       */
    /* LZMA_STREAM_INIT.                                                      */
    pR = (BFZ_Reader *)calloc(1, sizeof(BFZ_Reader));

    if (pR == NULL)
    {   /* No memory for the reader: nothing was allocated, so nothing to   */
        /* free, and the caller's handle is the caller's to close.          */
        return(BFX_Fail(BFE_ERR_NO_MEMORY, "no memory for the image reader"));
    }

    /* The input buffer every format's decoder is fed from.  malloc, not    */
    /* calloc: no byte of it is used before a read has filled it (cbIn      */
    /* counts what is there), so zeroing a megabyte would be wasted work.   */
    pR->pbIn = (uint8_t *)malloc(BFZ_IN_BUFFER);

    if (pR->pbIn == NULL)
    {   /* No memory for its input buffer. */

        /* Free the reader itself (nothing else is set up yet) and fail. */
        free(pR);
        return(BFX_Fail(BFE_ERR_NO_MEMORY, "no memory for the image reader"));
    }

    /* The reader's starting facts: its file, the size "unknown" until the   */
    /* format says otherwise (so gzip, which never says, stays unknown),     */
    /* and the file's own size.  The head buffer is cleared so the bytes     */
    /* past a short file's end are defined -- although every test on it      */
    /* below is bounded by cbHead, so none of them is ever read.             */
    pR->hFile       = hFile;
    pR->ullSize     = BFZ_SIZE_UNKNOWN;
    pR->ullFileSize = (uint64_t)liSize.QuadPart;
    memset(abHead, 0, sizeof(abHead));

    /* The first bytes decide -- as many as there are, up to the buffer --   */
    /* then back to the start (the zip branch moves on to its entry).  An    */
    /* EMPTY file reads zero bytes, matches no magic, and opens as a plain   */
    /* image of size 0; refusing it is the caller's business.  Sixteen       */
    /* bytes covers the longest look below, a skippable frame's eight.       */
    cbHead = sizeof(abHead);

    if (pR->ullFileSize < cbHead)
    {   /* A file shorter than the look. */
        cbHead = (uint32_t)pR->ullFileSize;
    }

    /* The look itself. */
    iStatus = Bfz_ReadAt(hFile, 0, abHead, cbHead);

    if (iStatus == BFE_OK)
    {   /* Read: which format, and back to the start.  The rewind is what   */
        /* a plain image relies on -- it is streamed from the position as   */
        /* it stands; the other kinds seek again for themselves.            */
        pR->iKind = BFZ_Sniff(abHead, cbHead);
        iStatus   = Bfz_ReadAt(hFile, 0, abHead, 0);
    }

    /* The one case four bytes cannot settle.  BFZ_Sniff calls a file that   */
    /* opens with a skippable frame RAW; if it has the eight bytes of a      */
    /* skippable frame's header, look past such frames for a zstd frame.     */
    /* Every later branch also tests iStatus, so a failed look or rewind     */
    /* falls straight through to the failure block at the end.               */
    if (iStatus == BFE_OK && pR->iKind == BFZ_KIND_RAW && cbHead >= 8 &&
        (Bfz_Le32(abHead) & BFZ_ZSTD_SKIP_MASK) == BFZ_ZSTD_SKIP_MAGIC)
    {   /* A skippable frame first: zstd only if a zstd frame follows it. */
        iStatus = Bfz_SniffPastSkippable(pR, &pR->iKind);
    }

    /* Now the kind is settled: one branch per kind sets up what reading   */
    /* it needs.  BFZ_Sniff returns no other kind, so there is no else.    */
    /* Where the format records a size (.xz, .zst) the order is: the       */
    /* size (which moves the file position), the rewind, then the          */
    /* decoder -- so the decoder's first read is the file's first byte.    */
    /* A zip is left at its first entry's data instead.                    */
    if (iStatus == BFE_OK && pR->iKind == BFZ_KIND_RAW)
    {   /* Plain: the file's size is the image's, and nothing needs    */
        /* starting -- the file position is already at byte 0, where   */
        /* the sniff (or Bfz_SniffPastSkippable) left it.              */
        pR->ullSize = (uint64_t)liSize.QuadPart;
    }
    else if (iStatus == BFE_OK && pR->iKind == BFZ_KIND_XZ)
    {   /* The size from the index first (it moves the file position, so the   */
        /* decoder is set up after the rewind); then the decoder.              */
        iStatus = Bfz_XzSize(pR, &pR->ullSize);

        if (iStatus == BFE_OK)
        {   /* Back to the start for the data. */
            iStatus = Bfz_ReadAt(hFile, 0, abHead, 0);
        }

        /* A fresh stream structure -- set even after a failure above, so   */
        /* BFZ_ReaderClose's lzma_end always meets an initialised one.      */
        pR->xz = (lzma_stream)LZMA_STREAM_INIT;

        /* The decoder: LZMA_CONCATENATED reads every stream in the file,   */
        /* one after another -- matching Bfz_XzSize, which sums them all,   */
        /* so the size and the bytes delivered agree.  No memory limit      */
        /* (UINT64_MAX): an .xz this machine can decode is read rather      */
        /* than refused; a limit would turn a large-dictionary file from    */
        /* another tool into a failure mid-read, and the program's own      */
        /* writer uses only 4 MiB (BFZ_XZ_PRESET).                          */
        if (iStatus == BFE_OK && lzma_stream_decoder(&pR->xz, UINT64_MAX, LZMA_CONCATENATED) != LZMA_OK)
        {   /* The decoder would not start: with these arguments, only for   */
            /* want of memory.  liblzma's start-up frees what it took on     */
            /* failure (lzma_next_strm_init, stream_decoder.c) and clears    */
            /* the stream's internal pointer -- and lzma_end does nothing    */
            /* for a stream whose pointer is clear (common.c), so the        */
            /* lzma_end BFZ_ReaderClose runs for an xz reader is safe.       */
            iStatus = BFX_Fail(BFE_ERR_NO_MEMORY, "the xz decoder could not be started");
        }
    }
    else if (iStatus == BFE_OK && pR->iKind == BFZ_KIND_GZIP)
    {   /* A gzip file: its size is unknown (the trailer holds it only   */
        /* modulo 4 GiB), so there is nothing to learn first and the     */
        /* position is already at byte 0.                                */

        /* 16 + window bits: decode the gzip format only, header and       */
        /* trailer around the deflate data (zlib.h, inflateInit2) -- not   */
        /* 32, which would also accept a bare zlib stream the sniff        */
        /* never let through.  Several gzip members one after another      */
        /* are NOT followed by zlib on its own; BFZ_ReaderRead resets      */
        /* the decoder at each member's end.  The z_stream is zeroed by    */
        /* the calloc above, which is what tells zlib to use its own       */
        /* allocator (zalloc and zfree NULL).                              */
        if (inflateInit2(&pR->gz, 16 + MAX_WBITS) != Z_OK)
        {   /* The decoder would not start.  With these fixed arguments and   */
            /* the zlib linked in, the only failure left is memory            */
            /* (Z_MEM_ERROR); zlib frees its own state before returning any   */
            /* failure (inflate.c), so nothing is held.                       */
            iStatus = BFX_Fail(BFE_ERR_NO_MEMORY, "the gzip decoder could not be started");
        }

        /* Remember whether there is inflate state for the close to free:      */
        /* the close calls inflateEnd only when this is TRUE.  (zlib's         */
        /* inflateEnd would only return an error for a stream that never       */
        /* started -- inflateStateCheck, inflate.c -- but the flag keeps the   */
        /* close from leaning on that, and serves the zip branch too.)         */
        pR->bGzOpen = (iStatus == BFE_OK);
    }
    else if (iStatus == BFE_OK && pR->iKind == BFZ_KIND_ZSTD)
    {   /* The size from the frame headers, back to the start, then a   */
        /* streaming zstd decoder.                                      */
        iStatus = Bfz_ZstdSize(pR, &pR->ullSize);

        if (iStatus == BFE_OK)
        {   /* Back to the start for the data. */
            iStatus = Bfz_ReadAt(hFile, 0, abHead, 0);
        }

        if (iStatus == BFE_OK)
        {   /* The size learned and the position back at byte 0: the decoder. */

            /* A STREAMING decoder rather than zstd's one-shot call: the      */
            /* one-shot form needs the whole image in memory, and a card      */
            /* image is gigabytes.  The stream is created, then initialised   */
            /* for a new frame sequence -- the step that readies it for its   */
            /* first frame.                                                   */
            pR->pZstd = ZSTD_createDStream();

            if (pR->pZstd == NULL || ZSTD_isError(ZSTD_initDStream(pR->pZstd)))
            {   /* The decoder would not start -- not created (no memory), or   */
                /* created and refusing to initialise.  In the second case      */
                /* pZstd is left set, and BFZ_ReaderClose frees it by that      */
                /* pointer, so nothing leaks either way.                        */
                iStatus = BFX_Fail(BFE_ERR_NO_MEMORY, "the zstd decoder could not be started");
            }
        }
    }
    else if (iStatus == BFE_OK && pR->iKind == BFZ_KIND_ZIP)
    {   /* The first entry, through the central directory.  It sets the    */
        /* entry's size, method and data offset, and leaves the position   */
        /* at the data -- not at byte 0 as for the other kinds -- since    */
        /* that is where the decoder (or the stored copy) must start.      */
        iStatus = Bfz_ZipFirstEntry(pR, (uint64_t)liSize.QuadPart);

        if (iStatus == BFE_OK && pR->iZipMethod == 8)
        {   /* Raw deflate: negative window bits, no zlib or gzip wrapper.   */
            /* A zip entry's data is bare deflate; with positive window      */
            /* bits zlib would look for a zlib header first and reject the   */
            /* entry's first bytes as one.  (A stored entry, method 0,       */
            /* needs no decoder: its bytes are copied as they are.)          */
            if (inflateInit2(&pR->gz, -MAX_WBITS) != Z_OK)
            {   /* The decoder would not start: as for gzip above, only for   */
                /* want of memory, with zlib's own state already freed.       */
                iStatus = BFX_Fail(BFE_ERR_NO_MEMORY, "the deflate decoder could not be started");
            }

            /* Remember whether there is inflate state for the close to free. */
            pR->bGzOpen = (iStatus == BFE_OK);
        }
    }

    if (iStatus != BFE_OK)
    {   /* Undo whatever was set up. */

        /* BFZ_ReaderClose frees exactly what each format started (it        */
        /* checks the kind and the flags), then the buffer and the reader;   */
        /* the failure, already recorded, is passed on.  Going through       */
        /* the close rather than freeing here keeps ONE place that knows     */
        /* the teardown, so open and close cannot disagree about what a      */
        /* half-built reader holds.  It never closes hFile, which stays      */
        /* the caller's, and *ppReader is still NULL.                        */
        BFZ_ReaderClose(pR);
        return(iStatus);
    }

    /* Hand the ready reader over -- only now, so the caller never sees   */
    /* one that is half set up.                                           */
    *ppReader = pR;

    /* Ready to read. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFZ_ReaderKind:                                                            */
/*                                                                            */
/* The format the open found, for the caller's messages and checks.  In       */
/* bfe_image.c it decides whether a source is copied as plain bytes, checks   */
/* that the read-back of a saved .xz really is .xz, and is passed STRAIGHT    */
/* OUT by BFE_ImageInfoGet -- which is why BFZ_KIND_* must stay equal to      */
/* BFE_IMAGE_* (bfe_compress.h).                                              */
/*                                                                            */
/* Arguments:                                                                 */
/*     pReader : the open reader.                                             */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFZ_KIND_*.                                                      */
/*----------------------------------------------------------------------------*/
int      BFZ_ReaderKind(const BFZ_Reader *pReader)     { return(pReader->iKind); }

/*----------------------------------------------------------------------------*/
/* BFZ_ReaderSize:                                                            */
/*                                                                            */
/* The image's uncompressed size as the open learned it: the file's size for  */
/* a plain image, the first entry's for a zip, the index's sum for .xz, the   */
/* frame headers' sum for .zst.  It is what the open was TOLD, not what a     */
/* read has proved -- a damaged file can deliver fewer bytes, which the read  */
/* reports as an error when the data ends early.                              */
/*                                                                            */
/* Arguments:                                                                 */
/*     pReader : the open reader.                                             */
/*                                                                            */
/* Returns:                                                                   */
/*     uint64_t : the size, or BFZ_SIZE_UNKNOWN (gzip; a .zst whose frames do */
/*                not all record their sizes, or whose walk does not end      */
/*                exactly at the file's end).                                 */
/*----------------------------------------------------------------------------*/
uint64_t BFZ_ReaderSize(const BFZ_Reader *pReader)     { return(pReader->ullSize); }

/*----------------------------------------------------------------------------*/
/* BFZ_ReaderFileSize:                                                        */
/*                                                                            */
/* The file's own size on disk -- with BFZ_ReaderConsumed, the progress       */
/* fraction when the image's size is unknown.                                 */
/*                                                                            */
/* Arguments:                                                                 */
/*     pReader : the open reader.                                             */
/*                                                                            */
/* Returns:                                                                   */
/*     uint64_t : the file's size in bytes.                                   */
/*----------------------------------------------------------------------------*/
uint64_t BFZ_ReaderFileSize(const BFZ_Reader *pReader) { return(pReader->ullFileSize); }

/*----------------------------------------------------------------------------*/
/* BFZ_ReaderConsumed:                                                        */
/*                                                                            */
/* The file position the decoder has reached -- where the data started, plus  */
/* everything read in, less what is still waiting in the buffer.  The         */
/* progress measure when the image's own size is unknown: bfe_image.c divides */
/* it by BFZ_ReaderFileSize.  ullDataStart is 0 for every kind but a zip, so  */
/* for a zip the measure starts at the entry's offset, not at 0 -- a          */
/* fraction of the whole file, which is what the denominator is.              */
/*                                                                            */
/* Arguments:                                                                 */
/*     pReader : the open reader.                                             */
/*                                                                            */
/* Returns:                                                                   */
/*     uint64_t : the byte offset in the file the decoder has consumed to.    */
/*----------------------------------------------------------------------------*/
uint64_t BFZ_ReaderConsumed(const BFZ_Reader *pReader)
{
    /* Read so far, less what the decoder has not taken yet (offIn never   */
    /* passes cbIn, so the subtraction cannot wrap).                       */
    return(pReader->ullDataStart + pReader->ullReadIn - (pReader->cbIn - pReader->offIn));
}

/*----------------------------------------------------------------------------*/
/* Bfz_Refill:                                                                */
/*                                                                            */
/* When the input buffer is used up, read the next piece of the file (a zip   */
/* entry: no further than the entry's end, so the decoder never sees the      */
/* zip's directory as data).  Does nothing while bytes are still waiting.     */
/*                                                                            */
/* Arguments:                                                                 */
/*     pR : the reader.                                                       */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK (bInEof set when nothing more came), or a recorded        */
/*           BFE_ERR_IO_IMAGE.                                                */
/*----------------------------------------------------------------------------*/
static int Bfz_Refill(BFZ_Reader *pR)
{
    DWORD    dwGot;
    uint32_t cbAsk;

    if (pR->offIn < pR->cbIn || pR->bInEof)
    {   /* Bytes still waiting, or nothing more to read.  Refilling now   */
        /* would read over the waiting bytes -- this reads into the       */
        /* buffer's start -- and the decoder would silently skip them.    */
        /* (Keeping a remainder while reading more is Bfz_TopUp's job.)   */
        return(BFE_OK);
    }

    /* A whole buffer's worth, or less at a zip entry's end.  Reading    */
    /* from the file position as it stands: the open left it where the   */
    /* data starts, and nothing else moves it while the reader lives.    */
    cbAsk = BFZ_IN_BUFFER;

    if (pR->iKind == BFZ_KIND_ZIP && pR->ullZipLeft < cbAsk)
    {   /* The entry ends sooner. */
        cbAsk = (uint32_t)pR->ullZipLeft;
    }

    if (cbAsk != 0 && !ReadFile(pR->hFile, pR->pbIn, cbAsk, &dwGot, NULL))
    {   /* The read failed.  A SHORT read is not a failure here (dwGot    */
        /* simply counts less; a read at the end of the file returns 0,   */
        /* which is how the end is found below) -- only an error from     */
        /* Windows is.  The buffer is left as it was; the caller stops.   */
        return(BFX_Fail(BFE_ERR_IO_IMAGE, "the image file could not be read"));
    }

    if (cbAsk == 0)
    {   /* A zip entry fully read.  The read above was skipped, so dwGot   */
        /* holds nothing; zero it, so the lines below see "no more" and    */
        /* mark the end exactly as at the end of a file.                   */
        dwGot = 0;
    }

    /* The buffer now holds what came, from its start; the running total   */
    /* of bytes read in grows (BFZ_ReaderConsumed uses it).                */
    pR->cbIn       = dwGot;
    pR->offIn      = 0;
    pR->ullReadIn += dwGot;

    if (pR->iKind == BFZ_KIND_ZIP)
    {   /* Count the entry down.  It cannot go below zero: dwGot is at   */
        /* most cbAsk, which was capped at what was left.                */
        pR->ullZipLeft -= dwGot;
    }

    if (dwGot == 0)
    {   /* The end of the file, or of the entry.  A zip whose file ends    */
        /* BEFORE its entry does also lands here, with ullZipLeft above    */
        /* zero; the reader then sees the data stop short and reports it   */
        /* (BFZ_ReaderRead), so no separate test is needed here.           */
        pR->bInEof = 1;
    }

    /* Refilled, or at the end. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* Bfz_TopUp:                                                                 */
/*                                                                            */
/* Make sure at least cbNeed unused bytes are in the input buffer, if the     */
/* file has them.  For peeking past the end of one gzip member at the         */
/* start of the next, which can fall across a buffer's end -- the one place   */
/* "whatever is left in the buffer" is not the same as "what the file holds   */
/* next".                                                                     */
/*                                                                            */
/* Arguments:                                                                 */
/*     pR     : the reader.                                                   */
/*     cbNeed : the bytes wanted in view, a few at most.                      */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK (with fewer in view only at the file's end);              */
/*           BFE_ERR_IO_IMAGE when the read fails.                            */
/*----------------------------------------------------------------------------*/
static int Bfz_TopUp(BFZ_Reader *pR, uint32_t cbNeed)
{
    DWORD    dwGot;
    uint32_t cbLeft;

    /* The unused bytes still in the buffer. */
    cbLeft = pR->cbIn - pR->offIn;

    if (cbLeft >= cbNeed || pR->bInEof)
    {   /* Enough in view already, or nothing more to read -- in which        */
        /* case the caller sees fewer than cbNeed and treats it as the end.   */
        return(BFE_OK);
    }

    /* The remainder to the front, then the file after it.  memmove, not   */
    /* memcpy: when the remainder is longer than offIn the two ranges      */
    /* overlap, which memcpy leaves undefined.                             */
    memmove(pR->pbIn, pR->pbIn + pR->offIn, cbLeft);
    pR->offIn = 0;
    pR->cbIn  = cbLeft;

    if (!ReadFile(pR->hFile, pR->pbIn + cbLeft, BFZ_IN_BUFFER - cbLeft, &dwGot, NULL))
    {   /* The read failed (a short read is not a failure: the file has       */
        /* simply ended).  The bytes already moved to the front stay valid    */
        /* -- cbIn and offIn were set to them above -- but the caller stops   */
        /* on the failure anyway.                                             */
        return(BFX_Fail(BFE_ERR_IO_IMAGE, "the image file could not be read"));
    }

    /* The buffer and the running total both grow by what came.  (Unlike    */
    /* Bfz_Refill this does not stop at a zip entry's end, and need not:    */
    /* its one caller is BFZ_ReaderRead's gzip member-end test, and a zip   */
    /* never reaches it.)                                                   */
    pR->cbIn      += dwGot;
    pR->ullReadIn += dwGot;

    if (dwGot == 0)
    {   /* The file has ended. */
        pR->bInEof = 1;
    }

    /* As much in view as the file allows. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* Bfz_ZipEnd:                                                                */
/*                                                                            */
/* Judge a zip entry at its end: the image bytes delivered must be exactly    */
/* the size the directory records, the data must END there, and their CRC-32  */
/* must be the one the directory records.  Called by BFZ_ReaderRead when the  */
/* entry's data has ended or the recorded size has been delivered --          */
/* whichever comes first.  The second matters because a caller that asks for  */
/* exactly the image's length stops reading at that size and never asks for   */
/* the empty read that would show the data's end; without it the CRC would    */
/* never be judged on such a copy -- so when the size is reached first, the   */
/* end is found here.                                                         */
/*                                                                            */
/* Only a read to the entry's end is judged: a caller that stops short of it  */
/* has read a part, which the CRC of the whole cannot certify, and nothing    */
/* here claims otherwise.                                                     */
/*                                                                            */
/* Arguments:                                                                 */
/*     pR : the open reader, a zip entry.                                     */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK when the entry is whole and its CRC-32 matches (bDone is  */
/*           then set, so later reads give 0 bytes); otherwise a recorded     */
/*           BFE_ERR_IO_IMAGE naming what is wrong, or Bfz_Refill's failure.  */
/*----------------------------------------------------------------------------*/
static int Bfz_ZipEnd(BFZ_Reader *pR)
{
    uint8_t  abSpare[64];
    int      iStatus;
    int      zret;

    if (pR->bZipChecked)
    {   /* Judged good already (a read after the end lands here). */
        return(BFE_OK);
    }

    if (pR->ullZipOut != pR->ullSize)
    {   /* Fewer bytes than recorded (the data ended early) or more (it ran   */
        /* on within one read): either way not the archive's image.           */
        return(BFX_Fail(BFE_ERR_IO_IMAGE, "the zip's image is %llu bytes, not the %llu its directory records: the archive is damaged",
                        (unsigned long long)pR->ullZipOut, (unsigned long long)pR->ullSize));
    }

    if (pR->iZipMethod == 8 && !pR->bZipInflated)
    {   /* The recorded size is out but inflate has not said the data has    */
        /* ended: run it on, with all the input it wants and a small spare   */
        /* buffer to put anything it still gives, until it reports the end   */
        /* -- any byte it gives there is past the recorded size.             */
        for (;;)
        {   /* One inflate step per iteration. */

            /* More input, if the buffer is used up. */
            iStatus = Bfz_Refill(pR);

            if (iStatus != BFE_OK)
            {   /* The file could not be read: Bfz_Refill has recorded it. */
                return(iStatus);
            }

            /* The unused input in, the spare buffer out; then what it took. */
            pR->gz.next_in   = pR->pbIn + pR->offIn;
            pR->gz.avail_in  = pR->cbIn - pR->offIn;
            pR->gz.next_out  = abSpare;
            pR->gz.avail_out = sizeof(abSpare);
            zret = inflate(&pR->gz, Z_NO_FLUSH);
            pR->offIn = pR->cbIn - pR->gz.avail_in;

            if (pR->gz.avail_out != sizeof(abSpare))
            {   /* It gave a byte past the recorded size. */
                return(BFX_Fail(BFE_ERR_IO_IMAGE, "the zip's image runs past the %llu bytes its directory records: the archive is damaged",
                                (unsigned long long)pR->ullSize));
            }

            if (zret == Z_STREAM_END)
            {   /* The data ends exactly at the recorded size. */
                break;
            }

            if (zret == Z_BUF_ERROR && pR->bInEof)
            {   /* Out of input before the data's end: cut short. */
                return(BFX_Fail(BFE_ERR_IO_IMAGE, "the compressed data ends before the image does"));
            }

            if (zret != Z_OK && zret != Z_BUF_ERROR)
            {   /* Damaged, as the main read reports it. */
                return(BFX_Fail(BFE_ERR_IO_IMAGE, "the compressed data is damaged (zlib %d)", zret));
            }
        }

        /* The decoder is finished. */
        pR->bZipInflated = 1;
    }

    if (pR->iZipMethod == 0 && (pR->ullZipLeft != 0 || pR->offIn != pR->cbIn))
    {   /* A stored entry with bytes left after the recorded size: its   */
        /* compressed size and its size disagree.                        */
        return(BFX_Fail(BFE_ERR_IO_IMAGE, "the zip's image runs past the %llu bytes its directory records: the archive is damaged",
                        (unsigned long long)pR->ullSize));
    }

    if (pR->uiZipCrcRun != pR->uiZipCrc)
    {   /* Whole and the right size, but not the bytes that were zipped: a   */
        /* flipped bit in stored data, or one that still inflated.           */
        return(BFX_Fail(BFE_ERR_IO_IMAGE, "the zip's image does not match its CRC-32 (the archive records %08lX, the data gives %08lX): "
                        "the archive is damaged", (unsigned long)pR->uiZipCrc, (unsigned long)pR->uiZipCrcRun));
    }

    /* Whole and as zipped; every later read gives 0 bytes. */
    pR->bZipChecked = 1;
    pR->bDone       = 1;
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFZ_ReaderRead:                                                            */
/*                                                                            */
/* Up to cbWant raw image bytes; fewer only at the end.  *pcbGot of 0 means   */
/* the image has ended.  On a failure the bytes this call already decoded     */
/* into the buffer are not reported (*pcbGot stays 0).  Every format is       */
/* checked as it ends: liblzma and zlib check each .xz block and gzip member, */
/* zstd each frame's checksum when it has one, and a zip entry's size and     */
/* CRC-32 are judged against its directory (Bfz_ZipEnd) -- the read that      */
/* reaches the entry's end fails if they disagree.                            */
/*                                                                            */
/* Arguments:                                                                 */
/*     pR       : the open reader.                                            */
/*     pvBuffer : receives the bytes.                                         */
/*     cbWant   : how many are wanted.                                        */
/*     pcbGot   : receives how many were given.                               */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK, or a recorded BFE_ERR_IO_IMAGE (damaged, cut short, or   */
/*           unreadable) / BFE_ERR_UNSUPPORTED (an xz filter not built in).   */
/*----------------------------------------------------------------------------*/
int BFZ_ReaderRead(BFZ_Reader *pR, void *pvBuffer, uint32_t cbWant, uint32_t *pcbGot)
{
    uint8_t  *pbOut;
    uint32_t  cbOut;
    uint32_t  cbTake;
    int       iStatus;
    lzma_ret  xret;
    lzma_action xaction;
    int       zret;
    size_t    zsret;
    int       bProgress;
    ZSTD_inBuffer  zin;
    ZSTD_outBuffer zout;

    /* Nothing given yet.  *pcbGot is cleared first, so every failure   */
    /* return below leaves it 0 without saying so again.                */
    pbOut   = (uint8_t *)pvBuffer;
    cbOut   = 0;
    *pcbGot = 0;

    /* Until the caller's buffer is full or the data has ended.  Once      */
    /* bDone is set every later call returns at once with 0 bytes, which   */
    /* is how the caller learns the image has ended.                       */
    while (cbOut < cbWant && !pR->bDone)
    {   /* One step of the decoder (or one copy of plain bytes) per iteration. */

        /* More input, if the buffer is used up.  Bfz_Refill does nothing   */
        /* while bytes wait, so calling it every pass costs nothing.        */
        iStatus = Bfz_Refill(pR);

        if (iStatus != BFE_OK)
        {   /* The file could not be read: Bfz_Refill has recorded it.  The   */
            /* bytes this call decoded so far are dropped with it.            */
            return(iStatus);
        }

        if (pR->iKind == BFZ_KIND_RAW || (pR->iKind == BFZ_KIND_ZIP && pR->iZipMethod == 0))
        {   /* Plain or stored: the bytes are the image. */
            cbTake = pR->cbIn - pR->offIn;

            if (cbTake > cbWant - cbOut)
            {   /* No more than asked. */
                cbTake = cbWant - cbOut;
            }

            /* Copy, advance both sides, and call it done only when nothing   */
            /* was left AND the file has ended: an empty buffer on its own    */
            /* only means the next pass must refill, and stopping on it       */
            /* would cut a plain image short at a buffer boundary.  (For a    */
            /* stored zip entry "the file has ended" is the ENTRY's end --    */
            /* Bfz_Refill reads no further.)  A stored entry's bytes are      */
            /* judged against the archive's CRC-32 at the entry's end, as     */
            /* a deflated one's are (below the loop, Bfz_ZipEnd).             */
            memcpy(pbOut + cbOut, pR->pbIn + pR->offIn, cbTake);
            pR->offIn += cbTake;
            cbOut     += cbTake;
            pR->bDone  = (cbTake == 0 && pR->bInEof);
        }
        else if (pR->iKind == BFZ_KIND_XZ)
        {   /* liblzma: FINISH once the file has no more input.  With          */
            /* LZMA_CONCATENATED the decoder cannot know the last stream is    */
            /* over until it is told no more input is coming: under            */
            /* LZMA_RUN, at the end, it answers LZMA_OK (stream_decoder.c,     */
            /* SEQ_STREAM_PADDING), and a second call that moves nothing       */
            /* becomes LZMA_BUF_ERROR (common.c, allow_buf_error) -- which     */
            /* the code below would report as damage on a perfectly good       */
            /* file.  FINISH is what lets a complete file end with             */
            /* LZMA_STREAM_END; one cut short mid-stream still comes back as   */
            /* LZMA_BUF_ERROR, and is reported as damaged or incomplete.       */

            /* Point the decoder at the unused input and the rest of the   */
            /* caller's buffer.                                            */
            pR->xz.next_in   = pR->pbIn + pR->offIn;
            pR->xz.avail_in  = pR->cbIn - pR->offIn;
            pR->xz.next_out  = pbOut + cbOut;
            pR->xz.avail_out = cbWant - cbOut;
            xaction          = LZMA_RUN;

            if (pR->bInEof)
            {   /* The file has no more: the decoder may finish. */
                xaction = LZMA_FINISH;
            }

            /* One step; then read back how much input it took and how much   */
            /* output it gave, from what it left unused.                      */
            xret = lzma_code(&pR->xz, xaction);
            pR->offIn = pR->cbIn - (uint32_t)pR->xz.avail_in;
            cbOut     = cbWant - (uint32_t)pR->xz.avail_out;

            if (xret == LZMA_STREAM_END)
            {   /* Every stream decoded, each one's check verified by liblzma   */
                /* on the way: the image has ended.                             */
                pR->bDone = 1;
            }
            else if (xret == LZMA_OPTIONS_ERROR)
            {   /* A filter this build does not have (only LZMA2 is built).   */
                /* Kept apart from damage because the file may be perfectly   */
                /* good -- the message says what to do about it.              */
                return(BFX_Fail(BFE_ERR_UNSUPPORTED, "the .xz file uses a filter this program does not read (only LZMA2)"));
            }
            else if (xret != LZMA_OK)
            {   /* Damaged or cut short (LZMA_DATA_ERROR, a bad check, or      */
                /* LZMA_BUF_ERROR after FINISH: the file ended mid-stream).    */
                /* liblzma's number goes in the text as the only clue to       */
                /* which.  The bytes already decoded into the caller's         */
                /* buffer by THIS call are dropped (*pcbGot stays 0); pieces   */
                /* earlier calls returned are already the caller's.            */
                return(BFX_Fail(BFE_ERR_IO_IMAGE, "the .xz data is damaged or incomplete (liblzma %d)", (int)xret));
            }
        }
        else if (pR->iKind == BFZ_KIND_GZIP || pR->iKind == BFZ_KIND_ZIP)
        {   /* zlib: a gzip member, or a zip entry's raw deflate. */

            /* As for xz: point zlib at the unused input and the rest of    */
            /* the caller's buffer, run one step, and read back from what   */
            /* it left unused how much it took and how much it gave.        */
            pR->gz.next_in   = pR->pbIn + pR->offIn;
            pR->gz.avail_in  = pR->cbIn - pR->offIn;
            pR->gz.next_out  = pbOut + cbOut;
            pR->gz.avail_out = cbWant - cbOut;
            zret = inflate(&pR->gz, Z_NO_FLUSH);
            pR->offIn = pR->cbIn - pR->gz.avail_in;
            cbOut     = cbWant - pR->gz.avail_out;

            if (zret == Z_STREAM_END && pR->iKind == BFZ_KIND_GZIP)
            {   /* A member ended: another may follow (concatenated .gz).  Its   */
                /* two magic bytes are brought into view first, since they       */
                /* can straddle the end of the buffer.  (Only a gzip file        */
                /* comes here; a zip entry's own end is the branch below.)       */
                iStatus = Bfz_TopUp(pR, 2);

                if (iStatus != BFE_OK)
                {   /* The file could not be read: Bfz_TopUp has recorded it. */
                    return(iStatus);
                }

                if (pR->cbIn - pR->offIn >= 2 && pR->pbIn[pR->offIn] == 0x1F && pR->pbIn[pR->offIn + 1] == 0x8B)
                {   /* Another member: decode it the same way.  zlib stops   */
                    /* at each member's end and does NOT go on by itself     */
                    /* (zlib.h, inflateInit2: the state "would need to be    */
                    /* reset"); inflateReset keeps the gzip setting and      */
                    /* clears the rest, so the next pass starts the member   */
                    /* from its header.                                      */
                    inflateReset(&pR->gz);
                }
                else
                {   /* Nothing more.  Whatever follows the last member and    */
                    /* does not begin with the gzip magic -- zero padding,    */
                    /* or anything else -- is ignored without a word: every   */
                    /* member delivered has had its CRC and length checked    */
                    /* by zlib against its trailer, so the image is whole.    */
                    pR->bDone = 1;
                }
            }
            else if (zret == Z_STREAM_END)
            {   /* The zip entry's deflate data ended: the image has.  Raw     */
                /* deflate carries no check of its own -- a flipped bit that   */
                /* still inflates passes zlib -- so what it gave is judged     */
                /* against the archive's CRC-32 and size below the loop        */
                /* (Bfz_ZipEnd), which is told the decoder is finished.        */
                pR->bZipInflated = 1;
                pR->bDone        = 1;
            }
            else if (zret == Z_BUF_ERROR && pR->bInEof)
            {   /* Out of input before the end: cut short.  Z_BUF_ERROR on    */
                /* its own only means "no progress this call" and is normal   */
                /* mid-file (the next pass refills); it is a failure only     */
                /* once the file -- or the zip entry -- has no more bytes.    */
                return(BFX_Fail(BFE_ERR_IO_IMAGE, "the compressed data ends before the image does"));
            }
            else if (zret != Z_OK && zret != Z_BUF_ERROR)
            {   /* Damaged: Z_DATA_ERROR (bad data or a bad gzip CRC / length   */
                /* in the trailer), Z_NEED_DICT, or Z_MEM_ERROR.  zlib's code   */
                /* goes in the text as the clue to which; as with xz, only      */
                /* this call's decoded bytes are dropped.                       */
                return(BFX_Fail(BFE_ERR_IO_IMAGE, "the compressed data is damaged (zlib %d)", zret));
            }
        }
        else
        {   /* zstd: frames one after another; 0 back means a frame ended.    */
            /* Which state the stream is in is taken ONLY from a call that    */
            /* moved something: at a frame's end zstd may keep one input      */
            /* byte back and ask for it again (returning 1), and a call       */
            /* with no input left at all returns the NEXT frame's header      */
            /* size -- neither of which means a frame was cut short.          */
            /* (Both verified in ZSTD_decompressStream, zstd_decompress.c:    */
            /* the "hostage byte" kept while output is still unflushed, and   */
            /* the size hint for what it expects next.)                       */
            /*                                                                */
            /* A call that moves nothing happens only when the input is       */
            /* used up: with input and output room zstd always takes or       */
            /* gives something.  The next pass then refills, or -- at the     */
            /* end of the file -- the test below ends the read.  So at most   */
            /* two such calls run back to back (one before the refill finds   */
            /* the end, one after); should more ever do so, zstd itself       */
            /* fails the sixteenth (ZSTD_NO_FORWARD_PROGRESS_MAX), so this    */
            /* loop cannot spin for ever.                                     */

            /* zstd takes its buffers as base, size and position: describe     */
            /* the input and the caller's buffer, run one step, note whether   */
            /* either position moved, and read the new positions back.         */
            zin.src     = pR->pbIn;
            zin.size    = pR->cbIn;
            zin.pos     = pR->offIn;
            zout.dst    = pbOut;
            zout.size   = cbWant;
            zout.pos    = cbOut;
            zsret       = ZSTD_decompressStream(pR->pZstd, &zout, &zin);
            bProgress   = (zin.pos != pR->offIn || zout.pos != cbOut);
            pR->offIn   = (uint32_t)zin.pos;
            cbOut       = (uint32_t)zout.pos;

            if (ZSTD_isError(zsret))
            {   /* Damaged: zstd's own error name (a bad checksum, corrupted    */
                /* block, unsupported frame parameter) goes in the text.  The   */
                /* positions were read back above, so offIn is where the        */
                /* decoder stopped -- but the reader is not used again after    */
                /* a failure.                                                   */
                return(BFX_Fail(BFE_ERR_IO_IMAGE, "the .zst data is damaged: %s", ZSTD_getErrorName(zsret)));
            }

            if (bProgress)
            {   /* A real step: is a frame unfinished after it?  Nonzero is    */
                /* "more to do in this frame" -- including the hostage-byte    */
                /* 1, which the next call with that byte still in the buffer   */
                /* (it was handed back, not consumed) turns into 0.            */
                pR->bZstdOpen = (zsret != 0);
            }
            else if (pR->bInEof && pR->offIn >= pR->cbIn)
            {   /* No input left and nothing more came out: done if the last   */
                /* frame ended, cut short if not.                              */
                if (pR->bZstdOpen)
                {   /* A frame was still open: the file stops mid-frame.  A   */
                    /* .zst need not record its size, so this -- not a size   */
                    /* comparison -- is the only way a truncated one is       */
                    /* noticed.                                               */
                    return(BFX_Fail(BFE_ERR_IO_IMAGE, "the .zst data ends before the image does"));
                }

                /* The last frame ended cleanly: the image has ended. */
                pR->bDone = 1;
            }
        }
    }

    if (pR->iKind == BFZ_KIND_ZIP)
    {   /* A zip entry carries its CRC-32 and size only in the directory,   */
        /* never in the data, so they are checked here: this call's bytes   */
        /* join the running CRC and count, and at the entry's end -- the    */
        /* data having ended, OR the recorded size having been delivered,   */
        /* which a caller reading exactly that much never reads past --     */
        /* Bfz_ZipEnd judges both.  A failure drops this call's bytes, as   */
        /* every other failure here does.                                   */
        if (cbOut > 0)
        {   /* Bytes were given: into the CRC and the count. */
            pR->uiZipCrcRun = (uint32_t)crc32(pR->uiZipCrcRun, pbOut, cbOut);
            pR->ullZipOut  += cbOut;
        }

        if (pR->bDone || pR->ullZipOut >= pR->ullSize)
        {   /* The entry's end, by its data or by its recorded size. */
            iStatus = Bfz_ZipEnd(pR);

            if (iStatus != BFE_OK)
            {   /* Wrong size, damaged, or not the archive's CRC-32:   */
                /* Bfz_ZipEnd has recorded which.                      */
                return(iStatus);
            }
        }
    }

    /* Report what was given -- only here, on success: every failure     */
    /* above returned with *pcbGot still 0.  Short of cbWant only when   */
    /* the data ended during this call.                                  */
    *pcbGot = cbOut;

    /* As much as was there, up to what was asked. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFZ_ReaderClose:                                                           */
/*                                                                            */
/* Free the decoder and the buffer (not the file handle, which is the         */
/* caller's).  Each decoder is freed only if it was started, so it is safe on */
/* a reader that BFZ_ReaderOpen gave up on part-way, and on NULL.             */
/*                                                                            */
/* Arguments:                                                                 */
/*     pR : the reader, or NULL.                                              */
/*                                                                            */
/* Returns:                                                                   */
/*     void : the reader no longer exists.                                    */
/*----------------------------------------------------------------------------*/
void BFZ_ReaderClose(BFZ_Reader *pR)
{
    if (pR == NULL)
    {   /* Nothing open. */
        return;
    }

    if (pR->iKind == BFZ_KIND_XZ)
    {   /* liblzma's state.  Keyed on the KIND, not a flag, because          */
        /* lzma_end is safe on a stream that never started (it does          */
        /* nothing while the internal pointer is clear -- common.c), which   */
        /* is what a failed or skipped start leaves.                         */
        lzma_end(&pR->xz);
    }

    if (pR->bGzOpen)
    {   /* zlib's state -- for a gzip file or a deflated zip entry alike,   */
        /* which is why this is keyed on the flag and not on the kind.      */
        inflateEnd(&pR->gz);
    }

    if (pR->pZstd != NULL)
    {   /* zstd's state -- set whenever ZSTD_createDStream succeeded, even   */
        /* if its initialisation then failed, so this frees that case too.   */
        ZSTD_freeDStream(pR->pZstd);
    }

    /* Then the input buffer and the reader itself, in that order: the     */
    /* buffer's pointer lives in the reader, so freeing the reader first   */
    /* would leave it to be read from freed memory.  (free of a NULL       */
    /* buffer -- a reader whose buffer allocation failed never reaches     */
    /* here, but the C library allows it -- does nothing.)                 */
    free(pR->pbIn);
    free(pR);
}

/*----------------------------------------------------------------------------*/
/* Bfz_WriterFlush:                                                           */
/*                                                                            */
/* Write whatever the encoder has produced to the file, then give the         */
/* encoder the whole output buffer again.                                     */
/*                                                                            */
/* Arguments:                                                                 */
/*     pW : the writer.                                                       */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK, or a recorded BFE_ERR_IO_HOST when the write failed or   */
/*           came up short (a full disk).                                     */
/*----------------------------------------------------------------------------*/
static int Bfz_WriterFlush(BFZ_Writer *pW)
{
    DWORD    dwPut;
    uint32_t cbHave;

    /* What the encoder has written: the buffer less what it left unused. */
    cbHave = BFZ_OUT_BUFFER - (uint32_t)pW->xz.avail_out;

    /* Into the file at its current position -- the writer never seeks, so    */
    /* the pieces land one after another from where the open found it.        */
    /* Nothing to write (an encoder step that produced nothing) is skipped.   */
    if (cbHave != 0 && (!WriteFile(pW->hFile, pW->pbOut, cbHave, &dwPut, NULL) || dwPut != cbHave))
    {   /* The disk refused it, or took only part (a full disk is the usual   */
        /* cause).  The output stays in the buffer and the encoder's state    */
        /* is now ahead of the file, so the writer is only fit to be          */
        /* closed; the caller deletes the incomplete file.  No Windows        */
        /* error number is added, so a full disk and a refused write read     */
        /* the same in the message.                                           */
        return(BFX_Fail(BFE_ERR_IO_HOST, "the .xz file could not be written"));
    }

    /* Hand the encoder the whole buffer again, from its start. */
    pW->xz.next_out  = pW->pbOut;
    pW->xz.avail_out = BFZ_OUT_BUFFER;

    /* The buffer is empty again. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFZ_WriterOpenXz:                                                          */
/*                                                                            */
/* An .xz writer onto an open, empty file, through liblzma's multi-threaded   */
/* encoder.  It exists for saving a device or image to a .xz file             */
/* (bfe_image.c, its only caller), which then                                 */
/* follows a fixed order: BFZ_WriterWrite for each piece of raw data,         */
/* BFZ_WriterFinish once at the end (without it the file is not a valid       */
/* .xz), and BFZ_WriterClose on every path, success or failure.               */
/*                                                                            */
/* The output is written with plain WriteFile calls of any length at the      */
/* file's current position, so the target must be an ordinary file opened     */
/* at its start -- never a device, whose writes must be whole sectors.        */
/* bfe_image.c refuses .xz onto a device before this is called (a device      */
/* takes the image itself; compressing is for a file).                        */
/*                                                                            */
/* Arguments:                                                                 */
/*     hFile    : the open, empty file (not closed by the writer).            */
/*     ppWriter : receives the writer, or NULL on failure.                    */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK, or a recorded BFE_ERR_NO_MEMORY when the writer, its     */
/*           output buffer or the encoder could not be had (nothing is left   */
/*           allocated).                                                      */
/*----------------------------------------------------------------------------*/
int BFZ_WriterOpenXz(HANDLE hFile, BFZ_Writer **ppWriter)
{
    BFZ_Writer *pW;
    lzma_mt     mt;
    uint64_t    ullBudget;

    /* No writer until every step below has succeeded, so a caller that   */
    /* hands *ppWriter to BFZ_WriterClose on any failure frees nothing.   */
    *ppWriter = NULL;

    /* The writer, zeroed so that every field not set below starts empty. */
    pW        = (BFZ_Writer *)calloc(1, sizeof(BFZ_Writer));

    if (pW == NULL)
    {   /* No memory for the writer: nothing was allocated, so nothing to   */
        /* free; the file handle stays the caller's.                        */
        return(BFX_Fail(BFE_ERR_NO_MEMORY, "no memory for the .xz writer"));
    }

    /* The output buffer: the encoder fills it, Bfz_WriterFlush empties it    */
    /* into the file, so the file is written in 1 MiB pieces rather than in   */
    /* whatever small amounts each encoder step happens to produce.           */
    pW->pbOut = (uint8_t *)malloc(BFZ_OUT_BUFFER);

    if (pW->pbOut == NULL)
    {   /* No memory for the output buffer. */

        /* Refuse, freeing the writer -- the only thing held, since the      */
        /* encoder is not started yet.  (BFZ_WriterClose would do as well:   */
        /* its lzma_end does nothing on a stream never started, common.c,    */
        /* and free(NULL) nothing; why the free is written out here is       */
        /* not recorded.)                                                    */
        free(pW);
        return(BFX_Fail(BFE_ERR_NO_MEMORY, "no memory for the .xz writer"));
    }

    /* The encoder's settings: one thread per processor, each compressing     */
    /* its own block, for speed (a card read is waited on); the level of      */
    /* BFZ_XZ_PRESET; and CRC64, xz's own default check.  The zeroed rest     */
    /* leaves the block size to liblzma (three times the preset's             */
    /* dictionary, at least 1 MiB, with about three blocks' worth of memory   */
    /* per thread), no flags, and a timeout of 0, which makes lzma_code       */
    /* wait until all the input is taken or the output buffer is full, so     */
    /* each pass of the loops in BFZ_WriterWrite / Finish makes progress      */
    /* rather than returning early with neither.  memlimit_threading is       */
    /* left at 0: the encoder ignores it (lzma\container.h, "Encoder:         */
    /* Ignored." -- a decoder setting), so the thread count is fitted to      */
    /* the memory below instead.                                              */
    memset(&mt, 0, sizeof(mt));
    mt.threads             = lzma_cputhreads();
    mt.preset              = BFZ_XZ_PRESET;
    mt.check               = LZMA_CHECK_CRC64;

    if (mt.threads == 0)
    {   /* The count could not be read: one thread (0 is refused outright). */
        mt.threads = 1;
    }

    /* The memory budget: a quarter of the machine's physical memory, the   */
    /* share this one copy may take while everything else runs -- a rule    */
    /* of thumb, not a promise of what is free now.  Unknown (0): no        */
    /* budget, every processor used.                                        */
    ullBudget = lzma_physmem() / 4;

    if (ullBudget == 0)
    {   /* The memory could not be read: no budget. */
        ullBudget = UINT64_MAX;
    }

    /* Fewer threads while liblzma's own estimate for these settings is       */
    /* over the budget (UINT64_MAX, its answer for settings it rejects,       */
    /* counts as over) -- each thread holds its own blocks and encoder, so    */
    /* the estimate falls with the count.  One thread is the floor: if even   */
    /* that is over, the encoder is started anyway, and memory it cannot      */
    /* have comes back as LZMA_MEM_ERROR, reported as NO_MEMORY.              */
    while (mt.threads > 1 && lzma_stream_encoder_mt_memusage(&mt) > ullBudget)
    {   /* One thread fewer per iteration. */
        mt.threads--;
    }

    /* The file the output goes to, and the stream in liblzma's documented   */
    /* starting state (calloc has zeroed it already; this says so).          */
    pW->hFile = hFile;
    pW->xz    = (lzma_stream)LZMA_STREAM_INIT;

    if (lzma_stream_encoder_mt(&pW->xz, &mt) != LZMA_OK)
    {   /* The encoder would not start.  With these fixed settings the only   */
        /* failure it can return in practice is LZMA_MEM_ERROR (CRC64 is      */
        /* built in, the preset is valid, and the thread count is refused     */
        /* only above LZMA_THREADS_MAX, 16 384), hence NO_MEMORY.  liblzma    */
        /* frees what it allocated before failing, so there is no lzma_end    */
        /* here: only the buffer and the writer are freed.                    */
        free(pW->pbOut);
        free(pW);
        return(BFX_Fail(BFE_ERR_NO_MEMORY, "the xz encoder could not be started"));
    }

    /* The encoder's output goes into the whole buffer, and the caller has   */
    /* the writer.                                                           */
    pW->xz.next_out  = pW->pbOut;
    pW->xz.avail_out = BFZ_OUT_BUFFER;
    *ppWriter        = pW;

    /* Ready for data. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFZ_WriterWrite:                                                           */
/*                                                                            */
/* Compress cbData raw bytes into the .xz, writing the output buffer to the   */
/* file each time it fills.  On return every byte has been taken in by the    */
/* encoder -- the threaded encoder copies its input into each thread's own    */
/* block buffer -- so the caller may reuse pvData at once (bfe_image.c reads  */
/* the next piece into the same buffer).                                      */
/*                                                                            */
/* Output that has not yet filled the buffer is deliberately NOT written      */
/* here: it waits for a later call or for BFZ_WriterFinish, so the file is    */
/* written in 1 MiB pieces.  Much of the compressed data for the bytes        */
/* passed in is still inside the encoder's threads at this point anyway.      */
/*                                                                            */
/* Arguments:                                                                 */
/*     pW      : the writer, from BFZ_WriterOpenXz.                           */
/*     pvData  : the raw bytes.                                               */
/*     cbData  : how many.                                                    */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK; a recorded BFE_ERR_NO_MEMORY when the encoder ran out of */
/*           memory, BFE_ERR_IO_HOST when liblzma reported another failure;   */
/*           or Bfz_WriterFlush's failure when the file refused a write.      */
/*           After a failure the writer is only fit to be closed.             */
/*----------------------------------------------------------------------------*/
int BFZ_WriterWrite(BFZ_Writer *pW, const void *pvData, uint32_t cbData)
{
    lzma_ret ret;
    int      iStatus;

    /* The encoder reads straight from the caller's bytes. */
    pW->xz.next_in  = (const uint8_t *)pvData;
    pW->xz.avail_in = cbData;

    /* Until the encoder has taken every byte.  With the timeout of 0 set    */
    /* at open, each lzma_code call returns only when the input is used up   */
    /* or the output buffer is full, so every pass makes progress.           */
    while (pW->xz.avail_in != 0)
    {   /* One encoder step per iteration; the output buffer is emptied when full. */
        ret = lzma_code(&pW->xz, LZMA_RUN);

        if (ret == LZMA_MEM_ERROR)
        {   /* The encoder ran out of memory -- its threads are made as the    */
            /* data comes, so this can arrive part-way through: reported as    */
            /* what it is, not as a disk failure.  Nothing is released here:   */
            /* the caller closes the writer and deletes the file.              */
            return(BFX_Fail(BFE_ERR_NO_MEMORY, "the xz encoder ran out of memory"));
        }

        if (ret != LZMA_OK)
        {   /* The encoder failed otherwise (LZMA_RUN never ends a stream, so   */
            /* any other answer than LZMA_OK is a failure).  liblzma's          */
            /* number goes in the text as the clue to which.  Nothing is        */
            /* released here: the caller closes the writer, which stops the     */
            /* threads, and deletes the file.                                   */
            return(BFX_Fail(BFE_ERR_IO_HOST, "the xz encoder failed (liblzma %d)", (int)ret));
        }

        /* Empty a full buffer before the next step: without room for output   */
        /* the encoder cannot go on, and a second call with none would come    */
        /* back as LZMA_BUF_ERROR.                                             */
        if (pW->xz.avail_out == 0)
        {   /* The output buffer is full: empty it into the file. */
            iStatus = Bfz_WriterFlush(pW);

            if (iStatus != BFE_OK)
            {   /* The file could not take it: the failure is already recorded. */
                return(iStatus);
            }
        }
    }

    /* All taken in; any part-filled output waits in the buffer. */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFZ_WriterFinish:                                                          */
/*                                                                            */
/* End the .xz: the encoder finishes the blocks its threads still hold, then  */
/* writes the stream's index and footer, and all of it goes to the file.      */
/* Without this the file holds compressed blocks but no index or footer, and  */
/* no xz reader will accept it.  Called once, after the last                  */
/* BFZ_WriterWrite; nothing may be written after it.                          */
/*                                                                            */
/* Arguments:                                                                 */
/*     pW : the writer, whose input is all taken in (a successful             */
/*          BFZ_WriterWrite leaves none pending).                             */
/*                                                                            */
/* Returns:                                                                   */
/*     int : BFE_OK when the whole .xz is in the file; a recorded             */
/*           BFE_ERR_NO_MEMORY when the encoder ran out of memory,            */
/*           BFE_ERR_IO_HOST when liblzma reported another failure; or        */
/*           Bfz_WriterFlush's failure when the file refused a write.  On a   */
/*           failure the file is incomplete, and bfe_image.c deletes it.      */
/*----------------------------------------------------------------------------*/
int BFZ_WriterFinish(BFZ_Writer *pW)
{
    lzma_ret ret;
    int      iStatus;

    /* LZMA_FINISH until the encoder says the stream has ended: each call   */
    /* returns LZMA_OK while blocks are still being compressed or the       */
    /* output buffer is full, and LZMA_STREAM_END once the footer is out.   */
    do
    {   /* One finishing step per iteration until the stream has ended. */
        ret = lzma_code(&pW->xz, LZMA_FINISH);

        if (ret == LZMA_MEM_ERROR)
        {   /* Out of memory finishing the last blocks, as in               */
            /* BFZ_WriterWrite: the file is incomplete; the caller closes   */
            /* the writer and deletes it.                                   */
            return(BFX_Fail(BFE_ERR_NO_MEMORY, "the xz encoder ran out of memory"));
        }

        if (ret != LZMA_OK && ret != LZMA_STREAM_END)
        {   /* The encoder failed, as in BFZ_WriterWrite: the file has no   */
            /* footer and is incomplete.  Nothing is released here -- the   */
            /* caller closes the writer and deletes the file.               */
            return(BFX_Fail(BFE_ERR_IO_HOST, "the xz encoder failed (liblzma %d)", (int)ret));
        }

        /* THE LAST BUFFER IS WRITTEN EVEN THOUGH IT IS NOT FULL.  Testing    */
        /* only for a full buffer, as BFZ_WriterWrite does, would leave the   */
        /* stream's tail -- its index and footer -- unwritten, and the file   */
        /* would be refused as a damaged .xz by every reader.                 */
        if (pW->xz.avail_out == 0 || ret == LZMA_STREAM_END)
        {   /* The buffer is full, or this is the last of it: into the file. */
            iStatus = Bfz_WriterFlush(pW);

            if (iStatus != BFE_OK)
            {   /* The file could not take it: the failure is already recorded. */
                return(iStatus);
            }
        }
    } while (ret != LZMA_STREAM_END);

    /* The whole .xz has been written to the file (the caller closes it). */
    return(BFE_OK);
}

/*----------------------------------------------------------------------------*/
/* BFZ_WriterClose:                                                           */
/*                                                                            */
/* Free a writer: the encoder, with its threads, and the output buffer.  The  */
/* file handle is NOT closed -- it belongs to the caller, which opened it --  */
/* and nothing is written: output still in the buffer is dropped, so a        */
/* writer closed without a successful BFZ_WriterFinish leaves an incomplete   */
/* file (bfe_image.c deletes it on that path).                                */
/*                                                                            */
/* Safe on a NULL writer and at any point in a stream, which is what lets     */
/* bfe_image.c call it unconditionally from its one clean-up function on      */
/* every path, including a failure before the writer was opened.              */
/*                                                                            */
/* Arguments:                                                                 */
/*     pW : the writer, or NULL.                                              */
/*                                                                            */
/* Returns:                                                                   */
/*     void : nothing; the writer must not be used again.                     */
/*----------------------------------------------------------------------------*/
void BFZ_WriterClose(BFZ_Writer *pW)
{
    if (pW == NULL)
    {   /* Nothing open: the caller's clean-up ran before (or without) an open. */
        return;
    }

    /* The encoder: lzma_end tells each of its threads to stop, waits for    */
    /* them, and frees what they held -- so it is safe mid-stream, after a   */
    /* failed BFZ_WriterWrite as well as after BFZ_WriterFinish.             */
    lzma_end(&pW->xz);

    /* Then the output buffer and the writer itself, in that order: the    */
    /* buffer's pointer lives in the writer, so freeing the writer first   */
    /* would leave it to be read from freed memory.  Whatever output the   */
    /* buffer still held is dropped unwritten, as the header says.         */
    free(pW->pbOut);
    free(pW);
}
