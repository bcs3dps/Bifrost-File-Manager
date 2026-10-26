@echo off
rem ---------------------------------------------------------------------------
rem build_engine.bat -- build bifrost_engine.dll (native 64-bit Windows) with
rem the WinLibs MinGW-w64 gcc, which must be on PATH as plain "gcc".
rem
rem SPDX-License-Identifier: GPL-2.0-or-later
rem Copyright (c) 2026 B. C. Services
rem
rem What it builds: the bundled lwext4 library (lwext4\src, upstream, compiled
rem as-is with no warning flags because it is not held to this project's
rem warning bar) plus the engine's own sources (compiled -Wall -Wextra),
rem linked into ONE DLL with the compiler runtime linked in statically, so the
rem DLL depends on nothing but Windows itself.
rem
rem EVERY SOURCE FILE IS NAMED BELOW, ON PURPOSE.  A file that is in the tree
rem and not in these lists is not part of the DLL, and a listed file that goes
rem missing stops the build.  lwext4\blockdev\ext4_filedev.c is deliberately
rem NOT listed: the engine has its own block device (bfe_blockdev.c).
rem
rem The third list is lwext4\unicode: e2fsprogs' Unicode folding (the tables
rem a casefold filesystem hashes its names with), mirrored verbatim and
rem compiled through the stand-in headers beside it; lwext4\unicode is on the
rem include path for that reason.  Compiled like lwext4, as-is.
rem
rem The lists after that are e2fsprogs 1.47.4's e2fsck and the libraries it
rem stands on (e2fsprogs\e2fsck and e2fsprogs\lib\*, mirrored verbatim), which
rem give the engine its filesystem check.  They are compiled the way upstream's
rem own makefiles compile them on MinGW -- proven by running those makefiles
rem against this gcc before the lists were written -- through e2fsprogs\shim
rem (the configured and generated headers, ours or the build's) and upstream's
rem own MinGW stand-in headers (e2fsprogs\include\mingw).  Each list is the
rem folder's upstream object list minus what its comment names.  Objects are
rem prefixed with the folder's name because two folders share a file name
rem (badblocks, getsize, llseek).
rem
rem Then the compression libraries (liblzma, zlib's inflate side and zstd's
rem decoder, under compress\) and FatFs (fatfs\source, for FAT12 / 16 / 32
rem and exFAT), mirrored as published -- but for FatFs's ffconf.h, its build
rem configuration, whose settings of ours are marked -- and compiled as-is,
rem with no warning flags; their objects are prefixed with the library's
rem name too.
rem
rem Output: out\bifrost_engine.dll, objects in out\obj.  Run it from anywhere:
rem it works from its own folder (pushd at the start, popd at each exit), so
rem every path below is RELATIVE to that folder and none names where the
rem tree is -- which also keeps the command lines short wherever it lives.
rem ---------------------------------------------------------------------------
setlocal

rem Into this script's own folder; HERE is that folder, relative (it ends in
rem a backslash), and the output folders under it.
pushd "%~dp0"
set "HERE=.\"
set "OUT=out"
set "OBJ=out\obj"

rem lwext4 configuration: use its built-in defaults (no CMake-generated
rem header), and compile its stdout debug chatter out.
rem
rem lwext4 IS NO LONGER EXACTLY AS PUBLISHED: it carries numbered fixes and
rem additions of ours, each marked "BifrostFM patch N" where it sits (grep
rem for the phrase; the comment at each marker says what it changes and why).
rem Its assertions are left ON, as published.
rem
rem The block cache holds 4096 blocks, not lwext4's default of 8 (sized for a
rem microcontroller): a mount, the formatter and bfe_mkfs.c all size theirs
rem by CONFIG_BLOCK_DEV_CACHE_SIZE.  Blocks are found through a tree and
rem allocated only as they are used, so the figure costs nothing but memory
rem -- at most 4096 times the block size, 16 MiB with 4 KiB blocks -- and an
rem operation touching more than 8 metadata blocks no longer evicts and
rem re-reads (and, writing, flushes) them over and over.
set "CFG=-DCONFIG_USE_DEFAULT_CFG=1 -DCONFIG_DEBUG_PRINTF=0 -DCONFIG_BLOCK_DEV_CACHE_SIZE=4096"
set "INC=-I%HERE%lwext4\include -I%HERE%lwext4\unicode"
set "OPT=-O2 -std=gnu99"

rem e2fsprogs: upstream's flags on MinGW are its default C standard (no -std:
rem the sources use C23's thread_local through config.h's TLS), -O2 and
rem HAVE_CONFIG_H.  The include order matters: the shim folders first, so the
rem stand-in config.h and the generated headers are found before anything
rem else; the library tree next; upstream's MinGW stand-ins LAST, because they
rem reach the real system headers with #include_next.
set "E2FS=%HERE%e2fsprogs"
set "E2FSINC=-I%E2FS%\shim -I%E2FS%\shim\ext2fs -I%E2FS%\shim\uuid -I%E2FS%\shim\blkid -I%E2FS%\shim\support -I%E2FS%\lib -I%E2FS%\include\mingw"
set "E2FSOPT=-O2 -DHAVE_CONFIG_H"

rem The lwext4 sources, by name (no extension).
set LWEXT4=ext4
set LWEXT4=%LWEXT4% ext4_balloc
set LWEXT4=%LWEXT4% ext4_bcache
set LWEXT4=%LWEXT4% ext4_bitmap
set LWEXT4=%LWEXT4% ext4_block_group
set LWEXT4=%LWEXT4% ext4_blockdev
set LWEXT4=%LWEXT4% ext4_casefold
set LWEXT4=%LWEXT4% ext4_crc32
set LWEXT4=%LWEXT4% ext4_debug
set LWEXT4=%LWEXT4% ext4_dir
set LWEXT4=%LWEXT4% ext4_dir_idx
set LWEXT4=%LWEXT4% ext4_extent
set LWEXT4=%LWEXT4% ext4_fs
set LWEXT4=%LWEXT4% ext4_hash
set LWEXT4=%LWEXT4% ext4_ialloc
set LWEXT4=%LWEXT4% ext4_inline
set LWEXT4=%LWEXT4% ext4_inode
set LWEXT4=%LWEXT4% ext4_journal
set LWEXT4=%LWEXT4% ext4_mbr
set LWEXT4=%LWEXT4% ext4_mkfs
set LWEXT4=%LWEXT4% ext4_super
set LWEXT4=%LWEXT4% ext4_trans
set LWEXT4=%LWEXT4% ext4_xattr

rem The mirrored e2fsprogs Unicode folding, by name (no extension).
set UNICODE=nls_utf8

rem e2fsprogs\lib\ext2fs (libext2fs), by name: upstream's MinGW object list
rem minus ext2_err (generated; compiled from the shim below), nls_utf8 (the
rem lwext4\unicode copy above serves both libraries), version (it wants a
rem header two folders up, and nothing compiled here calls it), undo_io (the
rem undo file is a command-line feature) and the debugfs / e2image / test-only
rem objects (bb_compat, inode_io, imager, write_bb_file, test_io).  dupfs IS
rem compiled: resize2fs duplicates the open filesystem (ext2fs_dup_handle).
set E2FS_EXT2FS=alloc
set E2FS_EXT2FS=%E2FS_EXT2FS% alloc_sb
set E2FS_EXT2FS=%E2FS_EXT2FS% alloc_stats
set E2FS_EXT2FS=%E2FS_EXT2FS% alloc_tables
set E2FS_EXT2FS=%E2FS_EXT2FS% atexit
set E2FS_EXT2FS=%E2FS_EXT2FS% badblocks
set E2FS_EXT2FS=%E2FS_EXT2FS% bb_inode
set E2FS_EXT2FS=%E2FS_EXT2FS% bitmaps
set E2FS_EXT2FS=%E2FS_EXT2FS% bitops
set E2FS_EXT2FS=%E2FS_EXT2FS% blkmap64_ba
set E2FS_EXT2FS=%E2FS_EXT2FS% blkmap64_rb
set E2FS_EXT2FS=%E2FS_EXT2FS% blknum
set E2FS_EXT2FS=%E2FS_EXT2FS% block
set E2FS_EXT2FS=%E2FS_EXT2FS% bmap
set E2FS_EXT2FS=%E2FS_EXT2FS% check_desc
set E2FS_EXT2FS=%E2FS_EXT2FS% closefs
set E2FS_EXT2FS=%E2FS_EXT2FS% crc16
set E2FS_EXT2FS=%E2FS_EXT2FS% crc32c
set E2FS_EXT2FS=%E2FS_EXT2FS% csum
set E2FS_EXT2FS=%E2FS_EXT2FS% dblist
set E2FS_EXT2FS=%E2FS_EXT2FS% dblist_dir
set E2FS_EXT2FS=%E2FS_EXT2FS% dirblock
set E2FS_EXT2FS=%E2FS_EXT2FS% dirhash
set E2FS_EXT2FS=%E2FS_EXT2FS% dir_iterate
set E2FS_EXT2FS=%E2FS_EXT2FS% dupfs
set E2FS_EXT2FS=%E2FS_EXT2FS% expanddir
set E2FS_EXT2FS=%E2FS_EXT2FS% ext_attr
set E2FS_EXT2FS=%E2FS_EXT2FS% extent
set E2FS_EXT2FS=%E2FS_EXT2FS% fallocate
set E2FS_EXT2FS=%E2FS_EXT2FS% fileio
set E2FS_EXT2FS=%E2FS_EXT2FS% finddev
set E2FS_EXT2FS=%E2FS_EXT2FS% flushb
set E2FS_EXT2FS=%E2FS_EXT2FS% freefs
set E2FS_EXT2FS=%E2FS_EXT2FS% gen_bitmap
set E2FS_EXT2FS=%E2FS_EXT2FS% gen_bitmap64
set E2FS_EXT2FS=%E2FS_EXT2FS% get_num_dirs
set E2FS_EXT2FS=%E2FS_EXT2FS% get_pathname
set E2FS_EXT2FS=%E2FS_EXT2FS% getenv
set E2FS_EXT2FS=%E2FS_EXT2FS% getsize
set E2FS_EXT2FS=%E2FS_EXT2FS% getsectsize
set E2FS_EXT2FS=%E2FS_EXT2FS% hashmap
set E2FS_EXT2FS=%E2FS_EXT2FS% i_block
set E2FS_EXT2FS=%E2FS_EXT2FS% icount
set E2FS_EXT2FS=%E2FS_EXT2FS% ind_block
set E2FS_EXT2FS=%E2FS_EXT2FS% initialize
set E2FS_EXT2FS=%E2FS_EXT2FS% inline
set E2FS_EXT2FS=%E2FS_EXT2FS% inline_data
set E2FS_EXT2FS=%E2FS_EXT2FS% inode
set E2FS_EXT2FS=%E2FS_EXT2FS% io_manager
set E2FS_EXT2FS=%E2FS_EXT2FS% ismounted
set E2FS_EXT2FS=%E2FS_EXT2FS% link
set E2FS_EXT2FS=%E2FS_EXT2FS% llseek
set E2FS_EXT2FS=%E2FS_EXT2FS% lookup
set E2FS_EXT2FS=%E2FS_EXT2FS% mkdir
set E2FS_EXT2FS=%E2FS_EXT2FS% mkjournal
set E2FS_EXT2FS=%E2FS_EXT2FS% mmp
set E2FS_EXT2FS=%E2FS_EXT2FS% namei
set E2FS_EXT2FS=%E2FS_EXT2FS% native
set E2FS_EXT2FS=%E2FS_EXT2FS% newdir
set E2FS_EXT2FS=%E2FS_EXT2FS% openfs
set E2FS_EXT2FS=%E2FS_EXT2FS% orphan
set E2FS_EXT2FS=%E2FS_EXT2FS% progress
set E2FS_EXT2FS=%E2FS_EXT2FS% punch
set E2FS_EXT2FS=%E2FS_EXT2FS% qcow2
set E2FS_EXT2FS=%E2FS_EXT2FS% read_bb
set E2FS_EXT2FS=%E2FS_EXT2FS% read_bb_file
set E2FS_EXT2FS=%E2FS_EXT2FS% res_gdt
set E2FS_EXT2FS=%E2FS_EXT2FS% rw_bitmaps
set E2FS_EXT2FS=%E2FS_EXT2FS% sha512
set E2FS_EXT2FS=%E2FS_EXT2FS% swapfs
set E2FS_EXT2FS=%E2FS_EXT2FS% symlink
set E2FS_EXT2FS=%E2FS_EXT2FS% sparse_io
set E2FS_EXT2FS=%E2FS_EXT2FS% unlink
set E2FS_EXT2FS=%E2FS_EXT2FS% valid_blk
set E2FS_EXT2FS=%E2FS_EXT2FS% rbtree
set E2FS_EXT2FS=%E2FS_EXT2FS% windows_io

rem e2fsprogs\lib\et (libcom_err), by name: upstream's whole list.
set E2FS_ET=error_message
set E2FS_ET=%E2FS_ET% et_name
set E2FS_ET=%E2FS_ET% init_et
set E2FS_ET=%E2FS_ET% com_err
set E2FS_ET=%E2FS_ET% com_right

rem e2fsprogs\lib\e2p (libe2p), by name: upstream's whole list.
set E2FS_E2P=feature
set E2FS_E2P=%E2FS_E2P% fgetflags
set E2FS_E2P=%E2FS_E2P% fsetflags
set E2FS_E2P=%E2FS_E2P% fgetversion
set E2FS_E2P=%E2FS_E2P% fsetversion
set E2FS_E2P=%E2FS_E2P% getflags
set E2FS_E2P=%E2FS_E2P% getversion
set E2FS_E2P=%E2FS_E2P% hashstr
set E2FS_E2P=%E2FS_E2P% iod
set E2FS_E2P=%E2FS_E2P% ls
set E2FS_E2P=%E2FS_E2P% ljs
set E2FS_E2P=%E2FS_E2P% mntopts
set E2FS_E2P=%E2FS_E2P% parse_num
set E2FS_E2P=%E2FS_E2P% pe
set E2FS_E2P=%E2FS_E2P% pf
set E2FS_E2P=%E2FS_E2P% ps
set E2FS_E2P=%E2FS_E2P% setflags
set E2FS_E2P=%E2FS_E2P% setversion
set E2FS_E2P=%E2FS_E2P% uuid
set E2FS_E2P=%E2FS_E2P% ostype
set E2FS_E2P=%E2FS_E2P% percent
set E2FS_E2P=%E2FS_E2P% crypto_mode
set E2FS_E2P=%E2FS_E2P% fgetproject
set E2FS_E2P=%E2FS_E2P% fsetproject
set E2FS_E2P=%E2FS_E2P% encoding
set E2FS_E2P=%E2FS_E2P% errcode

rem e2fsprogs\lib\uuid (libuuid), by name: upstream's whole list.
set E2FS_UUID=clear
set E2FS_UUID=%E2FS_UUID% compare
set E2FS_UUID=%E2FS_UUID% copy
set E2FS_UUID=%E2FS_UUID% gen_uuid
set E2FS_UUID=%E2FS_UUID% isnull
set E2FS_UUID=%E2FS_UUID% pack
set E2FS_UUID=%E2FS_UUID% parse
set E2FS_UUID=%E2FS_UUID% unpack
set E2FS_UUID=%E2FS_UUID% unparse
set E2FS_UUID=%E2FS_UUID% uuid_time

rem e2fsprogs\lib\support (libsupport), by name: upstream's list minus
rem prof_err (generated; compiled from the shim below) and the two only the
rem command-line front end unix.c uses (plausible, devname).
set E2FS_SUPPORT=cstring
set E2FS_SUPPORT=%E2FS_SUPPORT% mkquota
set E2FS_SUPPORT=%E2FS_SUPPORT% profile
set E2FS_SUPPORT=%E2FS_SUPPORT% parse_qtype
set E2FS_SUPPORT=%E2FS_SUPPORT% print_fs_flags
set E2FS_SUPPORT=%E2FS_SUPPORT% profile_helpers
set E2FS_SUPPORT=%E2FS_SUPPORT% quotaio
set E2FS_SUPPORT=%E2FS_SUPPORT% quotaio_v2
set E2FS_SUPPORT=%E2FS_SUPPORT% quotaio_tree
set E2FS_SUPPORT=%E2FS_SUPPORT% dict

rem e2fsprogs\lib\blkid (libblkid), by name: upstream's list minus version
rem (the same two-folders-up header as libext2fs's; nothing calls it).
set E2FS_BLKID=cache
set E2FS_BLKID=%E2FS_BLKID% dev
set E2FS_BLKID=%E2FS_BLKID% devname
set E2FS_BLKID=%E2FS_BLKID% devno
set E2FS_BLKID=%E2FS_BLKID% getsize
set E2FS_BLKID=%E2FS_BLKID% llseek
set E2FS_BLKID=%E2FS_BLKID% probe
set E2FS_BLKID=%E2FS_BLKID% read
set E2FS_BLKID=%E2FS_BLKID% resolve
set E2FS_BLKID=%E2FS_BLKID% save
set E2FS_BLKID=%E2FS_BLKID% tag

rem e2fsprogs\e2fsck, by name: upstream's list minus unix (the command-line
rem program; the engine's bfe_fsck.c is its replacement), logfile (fork,
rem pipe and daemon: the -E log_filename feature, not used), sigcatcher
rem (sigaction: a crash reporter for the program) and mtrace (a debug build
rem option).
set E2FS_E2FSCK=e2fsck
set E2FS_E2FSCK=%E2FS_E2FSCK% super
set E2FS_E2FSCK=%E2FS_E2FSCK% pass1
set E2FS_E2FSCK=%E2FS_E2FSCK% pass1b
set E2FS_E2FSCK=%E2FS_E2FSCK% pass2
set E2FS_E2FSCK=%E2FS_E2FSCK% pass3
set E2FS_E2FSCK=%E2FS_E2FSCK% pass4
set E2FS_E2FSCK=%E2FS_E2FSCK% pass5
set E2FS_E2FSCK=%E2FS_E2FSCK% journal
set E2FS_E2FSCK=%E2FS_E2FSCK% badblocks
set E2FS_E2FSCK=%E2FS_E2FSCK% util
set E2FS_E2FSCK=%E2FS_E2FSCK% dirinfo
set E2FS_E2FSCK=%E2FS_E2FSCK% dx_dirinfo
set E2FS_E2FSCK=%E2FS_E2FSCK% ehandler
set E2FS_E2FSCK=%E2FS_E2FSCK% problem
set E2FS_E2FSCK=%E2FS_E2FSCK% message
set E2FS_E2FSCK=%E2FS_E2FSCK% quota
set E2FS_E2FSCK=%E2FS_E2FSCK% recovery
set E2FS_E2FSCK=%E2FS_E2FSCK% region
set E2FS_E2FSCK=%E2FS_E2FSCK% revoke
set E2FS_E2FSCK=%E2FS_E2FSCK% ea_refcount
set E2FS_E2FSCK=%E2FS_E2FSCK% rehash
set E2FS_E2FSCK=%E2FS_E2FSCK% readahead
set E2FS_E2FSCK=%E2FS_E2FSCK% extents
set E2FS_E2FSCK=%E2FS_E2FSCK% encrypted_files

rem e2fsprogs\resize, by name: upstream's list minus main (the command-line
rem program; the engine's bfe_resize.c is its replacement), online (the
rem resize of a MOUNTED filesystem, through a Linux ioctl) and sim_progress
rem (main.c's progress bar).  Its resource tracking defines two functions
rem whose names e2fsck's util.c also defines, with other arguments -- two
rem programs upstream, one DLL here -- so the resize objects are compiled
rem with both names mapped to RSZ_ ones (the sources stay verbatim).
set E2FS_RESIZE=resize2fs
set E2FS_RESIZE=%E2FS_RESIZE% extent
set E2FS_RESIZE=%E2FS_RESIZE% resource_track

rem The engine's own sources, by name (no extension).
set ENGINE=bifrost_engine
set ENGINE=%ENGINE% bfe_copy
set ENGINE=%ENGINE% bfe_util
set ENGINE=%ENGINE% bfe_blockdev
set ENGINE=%ENGINE% bfe_device
set ENGINE=%ENGINE% bfe_image
rem Metadata pieces share one lock, validated together before writing begins.
set ENGINE=%ENGINE% bfe_rawbatch
set ENGINE=%ENGINE% bfe_capacity
set ENGINE=%ENGINE% bfe_mkfs

rem The engine's own sources that include e2fsprogs' headers (the check and
rem its io manager), by name: compiled to the engine's warning bar but with
rem the e2fsprogs include path in place of lwext4's, because the two trees
rem each carry a config.h and an ext2fs.h and these files want e2fsprogs'.
set FSCK=bfe_fsck
set FSCK=%FSCK% bfe_fsck_io
set FSCK=%FSCK% bfe_resize

rem FatFs (ChaN's FAT filesystem module, R0.16), mirrored verbatim under
rem fatfs\source with its configuration in ffconf.h (marked "BifrostFM FatFs
rem patch 1"), by name: compiled as-is, like lwext4, with no warning flags.
rem Upstream's diskio.c (an example glue file, as its 00readme.txt says) is
rem not mirrored at all: the engine's own device layer (bfe_fatio, below)
rem replaces it.
set "FATINC=-I%HERE%fatfs\source"
set FATFS=ff
set FATFS=%FATFS% ffsystem
set FATFS=%FATFS% ffunicode

rem The engine's own sources that include FatFs's headers (its device layer,
rem the five disk_* functions and get_fattime over the engine's block
rem device; the FAT session's calls into FatFs; and the filesystem-neutral
rem file calls, which reach lwext4 too), by name: compiled to the engine's
rem warning bar with lwext4's settings and include paths and FatFs's.
set FATIO=bfe_fatio
set FATIO=%FATIO% bfe_fat
set FATIO=%FATIO% bfe_fs

rem The engine's own source that includes the compression libraries'
rem headers (the compressed-image reader and the .xz writer), by name:
rem compiled to the engine's warning bar, with the libraries' include path
rem (CMPINC, below) and LZMA_API_STATIC, because liblzma is linked into the
rem DLL rather than imported from one of its own.
set COMPRESS=bfe_compress

rem The compressed-image libraries, mirrored verbatim under compress\ with
rem only the files these lists name: liblzma (xz-5.8.1: .xz with LZMA2, read
rem and written, and the BCJ filters, read; configured by
rem compress\lzma_config\config.h), zlib's inflate
rem side (gz and zip) and zstd's decoder.  liblzma's entries carry their
rem folder; every object is prefixed with its library's name.  file_info is
rem liblzma's .xz index reader: it is how an image's size is learned without
rem decompressing it.  stream_buffer_decoder and alone_decoder are the two
rem decoders squashfuse's decompress.c calls for a squashfs "xz" block (a
rem whole .xz stream in one buffer) and a squashfs "lzma" block (LZMA1 in
rem the old .lzma "alone" format).
set "CMP=%HERE%compress"
set "LZMA=%CMP%\xz-5.8.1\src\liblzma"
set "CMPINC=-I%CMP%\lzma_config -I%LZMA%\api -I%LZMA%\common -I%LZMA%\check -I%LZMA%\lz -I%LZMA%\rangecoder -I%LZMA%\lzma -I%LZMA%\delta -I%LZMA%\simple -I%CMP%\xz-5.8.1\src\common -I%CMP%\zlib-1.3.1 -I%CMP%\zstd-1.5.7\lib"
set "CMPOPT=-O2 -DHAVE_CONFIG_H -DLZMA_API_STATIC -DZSTD_DISABLE_ASM"
set XZ=common\common
set XZ=%XZ% common\block_util
set XZ=%XZ% common\easy_preset
set XZ=%XZ% common\filter_common
set XZ=%XZ% common\hardware_physmem
set XZ=%XZ% common\hardware_cputhreads
set XZ=%XZ% common\index
set XZ=%XZ% common\stream_flags_common
set XZ=%XZ% common\vli_size
set XZ=%XZ% common\outqueue
set XZ=%XZ% common\block_buffer_encoder
set XZ=%XZ% common\block_encoder
set XZ=%XZ% common\block_header_encoder
set XZ=%XZ% common\filter_encoder
set XZ=%XZ% common\filter_flags_encoder
set XZ=%XZ% common\index_encoder
set XZ=%XZ% common\stream_encoder
set XZ=%XZ% common\stream_flags_encoder
set XZ=%XZ% common\vli_encoder
set XZ=%XZ% common\stream_encoder_mt
set XZ=%XZ% common\block_decoder
set XZ=%XZ% common\block_header_decoder
set XZ=%XZ% common\filter_decoder
set XZ=%XZ% common\filter_flags_decoder
set XZ=%XZ% common\index_decoder
set XZ=%XZ% common\file_info
set XZ=%XZ% common\index_hash
set XZ=%XZ% common\stream_decoder
set XZ=%XZ% common\stream_buffer_decoder
set XZ=%XZ% common\alone_decoder
set XZ=%XZ% common\stream_flags_decoder
set XZ=%XZ% common\vli_decoder
set XZ=%XZ% check\check
set XZ=%XZ% check\crc32_fast
set XZ=%XZ% check\crc64_fast
set XZ=%XZ% check\sha256
set XZ=%XZ% lz\lz_decoder
set XZ=%XZ% lz\lz_encoder
set XZ=%XZ% lz\lz_encoder_mf
set XZ=%XZ% lzma\fastpos_table
set XZ=%XZ% lzma\lzma_decoder
set XZ=%XZ% lzma\lzma_encoder
set XZ=%XZ% lzma\lzma_encoder_optimum_fast
set XZ=%XZ% lzma\lzma_encoder_optimum_normal
set XZ=%XZ% lzma\lzma_encoder_presets
set XZ=%XZ% lzma\lzma2_decoder
set XZ=%XZ% lzma\lzma2_encoder
set XZ=%XZ% rangecoder\price_table
rem liblzma's BCJ filters, decoders only (lzma_config\config.h turns each
rem on): the shared coder, then one file per processor family.
set XZ=%XZ% simple\simple_coder
set XZ=%XZ% simple\simple_decoder
set XZ=%XZ% simple\x86
set XZ=%XZ% simple\powerpc
set XZ=%XZ% simple\ia64
set XZ=%XZ% simple\arm
set XZ=%XZ% simple\armthumb
set XZ=%XZ% simple\arm64
set XZ=%XZ% simple\sparc
set XZ=%XZ% simple\riscv
rem xz's portable helpers (xz-5.8.1\src\common), which hardware_physmem and
rem hardware_cputhreads call for the memory size and the core count the .xz
rem writer's thread settings are taken from.
set TUKLIB=tuklib_physmem
set TUKLIB=%TUKLIB% tuklib_cpucores
rem zlib's inflate side only, with its two checksums: crc32, gzip's trailer
rem check, and adler32, which inflate.c calls for zlib-wrapped streams and
rem so must be linked -- squashfs "gzip" blocks are zlib-wrapped streams, read
rem through uncompr's uncompress() by squashfuse's decompress.c.
set ZLIB=adler32
set ZLIB=%ZLIB% uncompr
set ZLIB=%ZLIB% crc32
set ZLIB=%ZLIB% inflate
set ZLIB=%ZLIB% inftrees
set ZLIB=%ZLIB% inffast
set ZLIB=%ZLIB% zutil
rem zstd's shared common files and its decoder; no encoder.
set ZSTD=common\debug
set ZSTD=%ZSTD% common\entropy_common
set ZSTD=%ZSTD% common\error_private
set ZSTD=%ZSTD% common\fse_decompress
set ZSTD=%ZSTD% common\xxhash
set ZSTD=%ZSTD% common\zstd_common
set ZSTD=%ZSTD% decompress\huf_decompress
set ZSTD=%ZSTD% decompress\zstd_ddict
set ZSTD=%ZSTD% decompress\zstd_decompress
set ZSTD=%ZSTD% decompress\zstd_decompress_block
rem squashfuse's read-only SquashFS reader (squashfuse\, BSD 2-clause, as
rem released bar BifrostFM squashfuse patch 1 in decompress.c), with this
rem engine's port files beside it (sqfs_port\: its config.h and win32.h).
rem It has an include list of its OWN -- never CMPINC, whose
rem lzma_config\config.h would be found as squashfuse's config.h -- and is
rem built without -Wall -Wextra, as lwext4 is: its warnings are upstream's.
rem The list is upstream's own library list (its Makefile.am's convenience
rem library) less what this engine does not use: the threaded cache
rem (cache_mt), the tree-walk helpers (hash, stack, traverse, util), and
rem nonstd-pread (sqfs_pread is the engine's, bfe_sqfs.c).  nonstd-stat is
rem kept: it holds sqfs_mode, which the directory and inode readers call.
set "SQFSINC=-I%HERE%sqfs_port -I%HERE%squashfuse -I%CMP%\zlib-1.3.1 -I%LZMA%\api -I%CMP%\zstd-1.5.7\lib"
set SQFS=fs
set SQFS=%SQFS% dir
set SQFS=%SQFS% file
set SQFS=%SQFS% table
set SQFS=%SQFS% cache
set SQFS=%SQFS% decompress
set SQFS=%SQFS% swap
set SQFS=%SQFS% xattr
set SQFS=%SQFS% nonstd-stat

rem The engine's own source that includes squashfuse's headers (the squashfs
rem session: its mount, listing and file reads, and the sqfs_pread that
rem squashfuse reads the image through, over the engine's block device), by
rem name: compiled to the engine's warning bar with squashfuse's include
rem list (SQFSINC, above) and nothing of lwext4's.
set SQFSIO=bfe_sqfs

rem Start from an empty object folder, so an object left by a source that has
rem since been removed from the lists above cannot be linked in.
if exist "%OBJ%" rmdir /s /q "%OBJ%"
mkdir "%OBJ%"
if errorlevel 1 goto :error

rem Compile lwext4, as-is.
rem
rem Every compile below is followed by "if errorlevel 1": that form is tested
rem when the line RUNS, so inside a loop's parenthesised block it sees each
rem gcc's own result -- the percent-sign form would be expanded once, before
rem the block runs, and see none of them.  The first failure stops the build.
for %%F in (%LWEXT4%) do (
    gcc -c %OPT% %CFG% %INC% -o "%OBJ%\%%F.o" "%HERE%lwext4\src\%%F.c"
    if errorlevel 1 goto :error
)

rem Compile the mirrored folding, as-is.
for %%F in (%UNICODE%) do (
    gcc -c %OPT% %CFG% %INC% -o "%OBJ%\%%F.o" "%HERE%lwext4\unicode\%%F.c"
    if errorlevel 1 goto :error
)

rem Compile the mirrored e2fsprogs folders, as-is, each object prefixed with
rem its folder's name.
for %%F in (%E2FS_EXT2FS%) do (
    gcc -c %E2FSOPT% %E2FSINC% -o "%OBJ%\ext2fs_%%F.o" "%E2FS%\lib\ext2fs\%%F.c"
    if errorlevel 1 goto :error
)
for %%F in (%E2FS_ET%) do (
    gcc -c %E2FSOPT% %E2FSINC% -o "%OBJ%\et_%%F.o" "%E2FS%\lib\et\%%F.c"
    if errorlevel 1 goto :error
)
for %%F in (%E2FS_E2P%) do (
    gcc -c %E2FSOPT% %E2FSINC% -o "%OBJ%\e2p_%%F.o" "%E2FS%\lib\e2p\%%F.c"
    if errorlevel 1 goto :error
)
for %%F in (%E2FS_UUID%) do (
    gcc -c %E2FSOPT% %E2FSINC% -o "%OBJ%\uuid_%%F.o" "%E2FS%\lib\uuid\%%F.c"
    if errorlevel 1 goto :error
)
for %%F in (%E2FS_SUPPORT%) do (
    gcc -c %E2FSOPT% %E2FSINC% -o "%OBJ%\support_%%F.o" "%E2FS%\lib\support\%%F.c"
    if errorlevel 1 goto :error
)
for %%F in (%E2FS_BLKID%) do (
    gcc -c %E2FSOPT% %E2FSINC% -o "%OBJ%\blkid_%%F.o" "%E2FS%\lib\blkid\%%F.c"
    if errorlevel 1 goto :error
)
rem e2fsck's util.c prints one #warning on every MinGW build ("Don't know how
rem to detect memory on your platform?"); it is expected and harmless, and
rem the comment at the warning says why.  Say so before it appears, so a
rem release build's output does not look broken.
echo note: e2fsck util.c prints one "#warning ... detect memory" below -- EXPECTED AND HARMLESS, see the comment there.
for %%F in (%E2FS_E2FSCK%) do (
    gcc -c %E2FSOPT% %E2FSINC% -o "%OBJ%\e2fsck_%%F.o" "%E2FS%\e2fsck\%%F.c"
    if errorlevel 1 goto :error
)
for %%F in (%E2FS_RESIZE%) do (
    gcc -c %E2FSOPT% %E2FSINC% -Dinit_resource_track=RSZ_init_resource_track -Dprint_resource_track=RSZ_print_resource_track -o "%OBJ%\resize_%%F.o" "%E2FS%\resize\%%F.c"
    if errorlevel 1 goto :error
)

rem Compile the compressed-image libraries, as-is; %%~nF drops a liblzma or
rem zstd entry's folder from its object's name.
for %%F in (%XZ%) do (
    gcc -c %CMPOPT% %CMPINC% -o "%OBJ%\xz_%%~nF.o" "%LZMA%\%%F.c"
    if errorlevel 1 goto :error
)
for %%F in (%TUKLIB%) do (
    gcc -c %CMPOPT% %CMPINC% -o "%OBJ%\xz_%%F.o" "%CMP%\xz-5.8.1\src\common\%%F.c"
    if errorlevel 1 goto :error
)
for %%F in (%ZLIB%) do (
    gcc -c %CMPOPT% %CMPINC% -o "%OBJ%\zlib_%%F.o" "%CMP%\zlib-1.3.1\%%F.c"
    if errorlevel 1 goto :error
)
for %%F in (%ZSTD%) do (
    gcc -c %CMPOPT% %CMPINC% -o "%OBJ%\zstd_%%~nF.o" "%CMP%\zstd-1.5.7\lib\%%F.c"
    if errorlevel 1 goto :error
)
rem Compile squashfuse's reader, as released bar patch 1, with its own include
rem list (SQFSINC, above) and liblzma's headers in static-library form.
for %%F in (%SQFS%) do (
    gcc -c %OPT% -DLZMA_API_STATIC %SQFSINC% -o "%OBJ%\sqfs_%%F.o" "%HERE%squashfuse\%%F.c"
    if errorlevel 1 goto :error
)

rem Compile FatFs, as-is, each object prefixed fatfs_ as the other
rem libraries' are, so none can take the name of an engine or lwext4 object.
for %%F in (%FATFS%) do (
    gcc -c %OPT% %FATINC% -o "%OBJ%\fatfs_%%F.o" "%HERE%fatfs\source\%%F.c"
    if errorlevel 1 goto :error
)

rem The two generated error tables (compile_et's output, kept in the shim),
rem and the shim's own stand-ins for what MinGW lacks (ours, so compiled to
rem the project's warning bar).
gcc -c %E2FSOPT% %E2FSINC% -o "%OBJ%\shim_ext2_err.o" "%E2FS%\shim\ext2fs\ext2_err.c"
if errorlevel 1 goto :error
gcc -c %E2FSOPT% %E2FSINC% -o "%OBJ%\shim_prof_err.o" "%E2FS%\shim\support\prof_err.c"
if errorlevel 1 goto :error
gcc -c %E2FSOPT% -Wall -Wextra %E2FSINC% -o "%OBJ%\shim_mingw_missing.o" "%E2FS%\shim\mingw_missing.c"
if errorlevel 1 goto :error

rem Compile the engine to the project's warning bar.  BFE_BUILDING_DLL makes
rem the public functions exports.  One loop per list, because each list
rem needs its own include paths (the lists' comments above say what each
rem file includes): ENGINE lwext4's; FSCK e2fsprogs' in place of lwext4's;
rem COMPRESS lwext4's and the compression libraries'; FATIO lwext4's and
rem FatFs's; SQFSIO squashfuse's alone.
for %%F in (%ENGINE%) do (
    gcc -c %OPT% -Wall -Wextra -DBFE_BUILDING_DLL %CFG% %INC% -o "%OBJ%\%%F.o" "%HERE%%%F.c"
    if errorlevel 1 goto :error
)
for %%F in (%FSCK%) do (
    gcc -c %OPT% -Wall -Wextra -DBFE_BUILDING_DLL %E2FSINC% -I"%E2FS%" -o "%OBJ%\%%F.o" "%HERE%%%F.c"
    if errorlevel 1 goto :error
)
for %%F in (%COMPRESS%) do (
    gcc -c %OPT% -Wall -Wextra -DBFE_BUILDING_DLL -DLZMA_API_STATIC %CFG% %INC% %CMPINC% -o "%OBJ%\%%F.o" "%HERE%%%F.c"
    if errorlevel 1 goto :error
)
for %%F in (%FATIO%) do (
    gcc -c %OPT% -Wall -Wextra -DBFE_BUILDING_DLL %CFG% %INC% %FATINC% -o "%OBJ%\%%F.o" "%HERE%%%F.c"
    if errorlevel 1 goto :error
)
for %%F in (%SQFSIO%) do (
    gcc -c %OPT% -Wall -Wextra -DBFE_BUILDING_DLL %SQFSINC% -o "%OBJ%\%%F.o" "%HERE%%%F.c"
    if errorlevel 1 goto :error
)

rem Link one DLL.  The objects are named in a response file, one per line,
rem because the whole list is longer than a cmd command line may be; the file
rem is rebuilt from the same name lists each time, so what is linked is
rem exactly what was compiled above.  gcc reads a backslash in a response
rem file as an escape, so the object folder is written with forward slashes.
set "RSP=%OBJ%\objects.rsp"
set "OBJFS=%OBJ:\=/%"
if exist "%RSP%" del "%RSP%"
for %%F in (%LWEXT4% %UNICODE% %ENGINE% %FSCK% %COMPRESS% %FATIO% %SQFSIO%) do echo "%OBJFS%/%%F.o">> "%RSP%"
for %%F in (%FATFS%) do echo "%OBJFS%/fatfs_%%F.o">> "%RSP%"
for %%F in (%E2FS_EXT2FS%) do echo "%OBJFS%/ext2fs_%%F.o">> "%RSP%"
for %%F in (%E2FS_ET%) do echo "%OBJFS%/et_%%F.o">> "%RSP%"
for %%F in (%E2FS_E2P%) do echo "%OBJFS%/e2p_%%F.o">> "%RSP%"
for %%F in (%E2FS_UUID%) do echo "%OBJFS%/uuid_%%F.o">> "%RSP%"
for %%F in (%E2FS_SUPPORT%) do echo "%OBJFS%/support_%%F.o">> "%RSP%"
for %%F in (%E2FS_BLKID%) do echo "%OBJFS%/blkid_%%F.o">> "%RSP%"
for %%F in (%E2FS_E2FSCK%) do echo "%OBJFS%/e2fsck_%%F.o">> "%RSP%"
for %%F in (%E2FS_RESIZE%) do echo "%OBJFS%/resize_%%F.o">> "%RSP%"
for %%F in (%XZ%) do echo "%OBJFS%/xz_%%~nF.o">> "%RSP%"
for %%F in (%TUKLIB%) do echo "%OBJFS%/xz_%%F.o">> "%RSP%"
for %%F in (%ZLIB%) do echo "%OBJFS%/zlib_%%F.o">> "%RSP%"
for %%F in (%ZSTD%) do echo "%OBJFS%/zstd_%%~nF.o">> "%RSP%"
for %%F in (%SQFS%) do echo "%OBJFS%/sqfs_%%F.o">> "%RSP%"
echo "%OBJFS%/shim_ext2_err.o">> "%RSP%"
echo "%OBJFS%/shim_prof_err.o">> "%RSP%"
echo "%OBJFS%/shim_mingw_missing.o">> "%RSP%"

rem The link: -shared makes a DLL, and -static links the compiler's runtime
rem into it, so the DLL needs no MinGW runtime DLL beside it.
gcc -shared -static -o "%OUT%\bifrost_engine.dll" @"%RSP%"
if errorlevel 1 goto :error

rem Built: say where, back to the caller's folder, and end with 0 for whoever
rem ran this.
echo built: engine\%OUT%\bifrost_engine.dll
popd
endlocal
exit /b 0

rem Every failure above lands here: say so and end with 1.  gcc has already
rem printed why; a half-built DLL is not removed, so check the exit code (or
rem the DLL's time), not the file's presence.
:error
echo BUILD FAILED
popd
endlocal
exit /b 1
