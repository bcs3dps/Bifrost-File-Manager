/*----------------------------------------------------------------------------*/
/* bfe_compress.h                                                             */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* Compressed image files, inside the engine: a READER that takes an image    */
/* file which may be .xz, .gz, .zst or .zip (or plain) and hands back the     */
/* raw image's bytes in order, and a WRITER that takes raw bytes and writes   */
/* them to a file as .xz.  Their one user is bfe_image.c: the raw copy        */
/* (BFE_CopyRaw) writes a compressed image to a device by decompressing it    */
/* on the fly, saves a device or image as .xz the other way round, and then   */
/* reads the .xz it wrote back through a reader of its own to verify it; and  */
/* BFE_ImageInfoGet opens a reader to report a file's format and size.        */
/*                                                                            */
/* The format is told by the file's first bytes, never by its name.  The      */
/* libraries are compiled in from compress\, and only the halves used:        */
/* liblzma both ways (LZMA2, with the BCJ filters read but never written),    */
/* zlib's inflate and zstd's decoder -- there is no gzip or zstd WRITER.      */
/* The zip container is read here, by hand.                                   */
/*                                                                            */
/* What the reader CHECKS as it decodes is each format's own: .xz's block     */
/* checks (liblzma), each gzip member's CRC-32 and length (zlib), a .zst      */
/* frame's checksum when the frame carries one (zstd).  A zip entry's size    */
/* and CRC-32, which live only in its directory, are judged by the reader     */
/* itself when the entry's end is read, stored and deflated alike; a read     */
/* that stops short of the end has not been checked.                          */
/*                                                                            */
/* The functions that can fail (the opens, the reads and writes, the finish)  */
/* return a BFE_* status and record the engine's last-error text; the sniff   */
/* and the four reader queries return values, and the closes return nothing.  */
/*----------------------------------------------------------------------------*/

/* Included once. */
#ifndef BFE_COMPRESS_H
#define BFE_COMPRESS_H

/* The fixed-width integers, HANDLE, and the engine's public codes (the      */
/* BFE_IMAGE_* formats, BFE_SIZE_UNKNOWN and the BFE_* statuses) that the    */
/* definitions below are built from.  windows.h is included here, not left   */
/* to the includer, because the prototypes take HANDLE.                      */
#include <stdint.h>
#include <windows.h>

#include "bifrost_engine.h"

/*----------------------------------------------------------------------------*/
/* Formats -- documentation table.  The public BFE_IMAGE_* values, under the  */
/* names this file's code uses.  BFZ_Sniff returns one, and BFE_ImageInfoGet  */
/* hands BFZ_ReaderKind's answer straight to the program as its BFE_IMAGE_*   */
/* format -- so these must stay EQUAL to the public values, not merely map    */
/* to them.                                                                   */
/*----------------------------------------------------------------------------*/
#define BFZ_KIND_RAW     BFE_IMAGE_RAW     // not a format below: the bytes ARE the image
#define BFZ_KIND_XZ      BFE_IMAGE_XZ      // .xz: FD 37 7A 58 5A 00 (one or more streams, LZMA2 with or without a BCJ filter here)
#define BFZ_KIND_GZIP    BFE_IMAGE_GZIP    // .gz: 1F 8B (one or more members)
#define BFZ_KIND_ZSTD    BFE_IMAGE_ZSTD    // .zst: 28 B5 2F FD (one or more frames, skippable frames allowed before them -- BFZ_ReaderOpen looks past those)
#define BFZ_KIND_ZIP     BFE_IMAGE_ZIP     // .zip: 50 4B 03 04 -- the FIRST file entry is the image (stored or deflate, ZIP64 sizes read)

#define BFZ_SIZE_UNKNOWN BFE_SIZE_UNKNOWN  // BFZ_ReaderSize: the uncompressed size cannot be told in advance

typedef struct BFZ_Reader BFZ_Reader;      // one open compressed (or plain) source; its members are private to bfe_compress.c
typedef struct BFZ_Writer BFZ_Writer;      // one .xz being written; likewise opaque

/*----------------------------------------------------------------------------*/
/* BFZ_Sniff         : the format a file's first bytes say it is, by magic    */
/*                     alone -- BFZ_KIND_RAW for anything not recognised,     */
/*                     including a .zst that opens with a skippable frame     */
/*                     (only the open looks past those).  Returns a kind, no  */
/*                     status.  bfe_compress.c is its only caller.            */
/* BFZ_ReaderOpen    : tell an open file's format, learn the image's size     */
/*                     where the format records it, and set up its decoder.   */
/*                     The handle stays the caller's and is read from its     */
/*                     start, wherever its position stood.  It must allow     */
/*                     reading with plain synchronous I/O and NOT be          */
/*                     unbuffered: the reader seeks and reads at any offset   */
/*                     and length.  A reader cannot be rewound: to read from  */
/*                     the start again, close it and open a new one on the    */
/*                     same handle (the copy's verify does).  Nothing else    */
/*                     may move the handle's position while the reader        */
/*                     lives: its reads continue from where the last left     */
/*                     off.  Returns BFE_OK, or a recorded BFE_ERR_IO_IMAGE / */
/*                     _NO_MEMORY / _UNSUPPORTED with *ppReader NULL -- so a  */
/*                     caller may close what it got unconditionally.          */
/* BFZ_ReaderKind    : the BFZ_KIND_* found.                                  */
/* BFZ_ReaderSize    : the image's size as the format records it, or          */
/*                     BFZ_SIZE_UNKNOWN (gzip always; .zst when a frame does  */
/*                     not record it).  A claim, not a proof: a damaged file  */
/*                     can still end early, which BFZ_ReaderRead reports.     */
/* BFZ_ReaderFileSize: the file's own size.                                   */
/* BFZ_ReaderConsumed: how many of the file's bytes the decoder has used --   */
/*                     the progress measure when the image's size is unknown. */
/* BFZ_ReaderRead    : up to cbWant image bytes; fewer only at its end, and   */
/*                     *pcbGot 0 once it has ended.  Returns BFE_OK, or a     */
/*                     recorded BFE_ERR_IO_IMAGE (damaged, cut short or       */
/*                     unreadable) / _UNSUPPORTED (an xz filter not built     */
/*                     in), with *pcbGot 0: what the failing call decoded is  */
/*                     not handed over, and the reader is only fit to close.  */
/* BFZ_ReaderClose   : free the reader (not the handle); NULL is allowed.     */
/* BFZ_WriterOpenXz  : an .xz writer onto an open, empty, ordinary file (not  */
/*                     a device: it writes any length at any position).  It   */
/*                     writes at the handle's current position and never      */
/*                     seeks, so the handle must stand at the file's start.   */
/*                     Returns BFE_OK, or a recorded BFE_ERR_NO_MEMORY with   */
/*                     *ppWriter NULL.                                        */
/* BFZ_WriterWrite   : compress bytes, writing out what is ready; the caller  */
/*                     may reuse its buffer as soon as it returns.  Returns   */
/*                     BFE_OK or a recorded BFE_ERR_IO_HOST (the encoder      */
/*                     failed, or the file refused a write).                  */
/* BFZ_WriterFinish  : end the stream and write the rest; the .xz is then     */
/*                     complete.  Without it the file is not a valid .xz.     */
/*                     Returns as BFZ_WriterWrite does.  After a failure of   */
/*                     either, the writer is only fit to close and the file   */
/*                     is incomplete: the caller deletes it (bfe_image.c      */
/*                     does).                                                 */
/* BFZ_WriterClose   : free the writer (not the handle); NULL is allowed, and */
/*                     so is any point in the stream -- unwritten output is   */
/*                     dropped.                                               */
/*----------------------------------------------------------------------------*/
int      BFZ_Sniff(const uint8_t *pbHead, size_t cbHead);
int      BFZ_ReaderOpen(HANDLE hFile, BFZ_Reader **ppReader);
int      BFZ_ReaderKind(const BFZ_Reader *pReader);
uint64_t BFZ_ReaderSize(const BFZ_Reader *pReader);
uint64_t BFZ_ReaderFileSize(const BFZ_Reader *pReader);
uint64_t BFZ_ReaderConsumed(const BFZ_Reader *pReader);
int      BFZ_ReaderRead(BFZ_Reader *pReader, void *pvBuffer, uint32_t cbWant, uint32_t *pcbGot);
void     BFZ_ReaderClose(BFZ_Reader *pReader);

int      BFZ_WriterOpenXz(HANDLE hFile, BFZ_Writer **ppWriter);
int      BFZ_WriterWrite(BFZ_Writer *pWriter, const void *pvData, uint32_t cbData);
int      BFZ_WriterFinish(BFZ_Writer *pWriter);
void     BFZ_WriterClose(BFZ_Writer *pWriter);

/* The end of the include guard. */
#endif // BFE_COMPRESS_H
