# squashfuse -- the changes made to it here, and the defects found in it

This program reads squashfs through **squashfuse 0.6.5**, the
maintainers' release archive (`squashfuse-0.6.5.tar.gz`, SHA-256
`9ec44b8ee6309e09bdcdeba422edc745e876db16136acacb74ecc7e7131e5763`, from
<https://github.com/vasi/squashfuse/releases/tag/0.6.5>), built as a
library for Windows with MinGW-w64 GCC. Only squashfuse's reader is used
-- `fs.c`, `dir.c`, `file.c`, `table.c`, `cache.c`, `decompress.c`,
`swap.c`, `xattr.c` and `nonstd-stat.c` -- with the zlib, liblzma and zstd
decompressors; its FUSE programs, its threaded cache and its tree-walk
helpers are not built. `sqfs_pread` and the port's `config.h` and
`win32.h` are supplied by the host program, as squashfuse expects of a
Windows build.

Every squashfuse file is used exactly as released except the one change
in Part 1. Part 2 lists what was found by reading the code and is not
changed here.

## Part 1 -- changes made

### Patch 1 -- `decompress.c`: the Windows zlib decoder defined twice

**The defect.** On Windows, `decompress.c` includes
`win_decompress.c.inc` -- the port's own small zlib decoder, built on
tinfl -- whenever `_WIN32` is defined. That file defines
`sqfs_decompressor_zlib`. When the real zlib is also available
(`HAVE_ZLIB_H`), the zlib block further down defines
`sqfs_decompressor_zlib` again, and the build stops at the second
definition. So a Windows build can use the port's decoder or the real
zlib, but not choose the real one.

**How it was shown.** Building `decompress.c` with MinGW-w64 GCC 15.2,
`_WIN32` defined (as it always is there) and `HAVE_ZLIB_H` defined:
"redefinition of 'sqfs_decompressor_zlib'".

**The fix.** Include the port's decoder only when the real zlib is not
built in:

```diff
--- squashfuse-0.6.5/decompress.c
+++ squashfuse-0.6.5/decompress.c
@@ -29,7 +29,7 @@
 
 #include <string.h>
 
-#if _WIN32
+#if _WIN32 && !defined(HAVE_ZLIB_H)
 	#include "win_decompress.c.inc"
 #endif
 
```

(The copy here also carries a four-line comment above the changed line,
saying why; it is not part of the fix.)

## Part 2 -- found by reading, not changed

### `file.c`, `sqfs_read_range`: the negative-size test cannot fire where `sqfs_off_t` is unsigned

`sqfs_read_range` begins `if (*size < 0 || start > file_size) return
SQFS_ERR;`. Where `sqfs_off_t` is a signed type (`off_t` on POSIX) the
first test refuses a negative size. Where it is unsigned -- a Windows
port that makes it a 64-bit unsigned integer -- the test is always false
and the compiler may drop it, so a "negative" size from a caller arrives
as an enormous one. squashfuse's own callers never pass one, so nothing
goes wrong through them; a caller of the library should not rely on the
refusal on such a build. Suggested: test the size against the file's
remaining length, which is right for either signedness.

### `file.c`, `sqfs_read_range`: a read that yields nothing is a failure

The function ends `return *size ? SQFS_OK : SQFS_ERR;`, so a request of
zero bytes before the end of a file -- or any request that copies
nothing -- is reported as an error rather than as zero bytes read. A
caller that may ask for zero bytes, or that reads up to a size it has
not clamped, has to clamp first (this program does). Suggested: return
`SQFS_OK` with `*size` 0 when nothing was asked for.

### `fs.c`, `sqfs_readlink`: a buffer size of zero wraps

With a non-NULL buffer, `sqfs_readlink` computes `*size - 1` to leave room
for the terminator. A `*size` of 0 wraps to the largest `size_t`, so the
whole target is copied and terminated past the buffer. Only a caller's
mistake reaches it (this program asks for the length first and allocates
it). Suggested: refuse a zero size.

### `dir.c`, `sqfs_lookup_path`: a path through a file is an error, not "not found"

When a component before the last is not a directory, `sqfs_dir_lookup`
fails in `sqfs_dir_open` and `sqfs_lookup_path` returns an error, so a
caller cannot tell "a path through a file" from a read failure of the
image. This program walks paths itself with the directory reader for that
reason. A design note rather than a defect.

### `nonstd-stat.c`, `sqfs_mode`: the host's `S_IFBLK`

`sqfs_mode` turns an inode type into the host's `S_IF*` value. MinGW's
`S_IFBLK` is 0x3000, not POSIX's 0x6000, so on that build a block
device's mode word does not read as one to POSIX-minded code. This
program takes every entry's type from the inode's type field instead. A
porting note, not a defect in squashfuse.
