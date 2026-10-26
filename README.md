# BifrostFM 2.4.12

A Windows dual-pane file manager for Linux and FAT filesystems -- ext2,
ext3 and ext4, and FAT12, FAT16, FAT32 and exFAT -- in **image files** and
on **real devices** (SD cards, USB sticks, eMMC adapters). The filesystem
inside the image or on the device is shown in the LEFT pane, the Windows
filesystem in the RIGHT, and files and whole folders copy between them in
both directions. Everything is done on the raw bytes, in userspace: no
Linux host, no Windows ext4 driver, nothing installed. An image file needs
no Administrator rights; a real device does.

The name is **Bifrost**, the rainbow bridge between two worlds in Norse
myth: the program is the bridge between a Windows filesystem and a Linux
one.

**Always keep a backup copy of an image or a card before changing it.**

## What is in this folder

| Item | What it is |
|---|---|
| `BifrostFM.exe` | The program, ready to run: one file, the engine inside it. |
| `README.md`, `README.html` | This file, and the same as a page to read in a browser. |
| `LICENSE` | The GNU General Public License, version 2 -- BifrostFM's licence. |
| `SOURCES.md`, `SOURCES.html` | The source tree, file by file: which code is BifrostFM's own and which is open-source code built into the engine, with each one's copyright and licence. |
| `LWEXT4_UPSTREAM.md`, `LWEXT4_UPSTREAM.html` | For lwext4's maintainers and users: every defect found in the lwext4 library while building BifrostFM, each numbered patch that fixes one (marked "BifrostFM patch N" in `engine\lwext4\`) explained and given as a diff against lwext4's v1.0.0 release, with where upstream's master stands, and the defects verified but not yet fixed. |
| `build.bat` | Builds everything: the engine, then the program (see "Building it"). |
| `engine\` | The engine's source: `bifrost_engine.dll`, a C library, and the open-source libraries it builds in. |
| `gui\` | The program's source: `BifrostFM.exe`, C# WinForms. |
| `tools\` | The tool that draws the program's icon and About picture (not needed to build). |

## Running it

`BifrostFM.exe` runs on 64-bit Windows with the .NET Framework 4 installed
(it is part of Windows 10 and Windows 11). Copy it anywhere and run it. It
extracts its engine to a folder of its own under `%TEMP%` when it starts
and deletes it when it exits. Its settings are kept in the registry, under
`HKCU\Software\BifrostFM`.

Reading or writing a real device needs Administrator rights. The program
does not ask for them at start-up: the first device command offers to
restart it as Administrator, and the restarted copy goes straight back to
that command.

## What it does

- **Opens** an ext2, ext3, ext4, FAT12, FAT16, FAT32 or exFAT filesystem in
  an image file, in one partition of a whole-disk image (MBR or GPT), or on
  a device, and browses it: name, size, type, permissions, owner and time
  for every entry.
- **Copies** files and whole trees out of the image and into it -- by menu,
  by clipboard (Ctrl+C / Ctrl+V), or by dragging between the panes or to
  and from Explorer. Copy and paste copies; cut and paste moves, within the
  image, within Windows or across. File contents are copied exactly. What
  Windows cannot hold (a symbolic link, a device node, a name Windows
  refuses) is skipped, and an optional manifest beside the copy records
  every entry's Unix mode, owner, group, times and links, so a tree copied
  out and back in comes back as it was.
- **Edits** the image: new folder, rename, delete, and, in the image,
  permissions, owner and group (as octal and as check boxes). An image is
  always opened **read-only**; the first change asks once, with a warning,
  then reopens it for writing.
- **Opens files inside the image** in the program's own windows: a text
  editor, a hex editor, a picture viewer, and a structured editor for JSON,
  INI and Klipper / Moonraker / KlipperScreen configuration files. Each
  editor's Save writes back into the image, and Save As can write to the
  image or to Windows.
- **Makes images and filesystems**: a new empty image file of an exact
  size, a partition window to create, edit and write MBR and GPT tables,
  and Make Filesystem for an empty ext2, ext3, ext4 (no journal), FAT16
  (FAT12 when small), FAT32 or exFAT filesystem -- in an image or in a
  partition of a device.
- **Checks and repairs** an ext filesystem with e2fsck (e2fsprogs 1.47.4,
  built into the engine), report-only or repairing, and **grows** one to
  fill its partition with resize2fs (Expand Filesystem).
- **Images devices**: Save Device to Image File copies a whole device (or
  its first part) to a raw `.img` or a compressed `.img.xz`, with a SHA-256
  beside it; Write Image File to Device writes a raw, `.xz`, `.gz`, `.zst`
  or `.zip` image to a device, reads it all back and compares it. The disk
  Windows runs from is never written.
- **Tests a card's real capacity** without destroying what is on it, and
  warns about a counterfeit card that claims more than it holds.
- **Shows the image's facts**: Filesystem Properties lists its type, label,
  size, free space, block and inode counts and feature flags.

Started with `--debug`, the program keeps a log of the run in
`log\BifrostFM-debug.log` beside it, to be read outside the program.

## Command line

```
BifrostFM.exe                     open the window
BifrostFM.exe <image-file>        open the window with that image open
BifrostFM.exe --debug [...]       keep a debug log for this run (log\BifrostFM-debug.log beside the program)
BifrostFM.exe --open-device       open the window and go straight to File > Open > Device
BifrostFM.exe --read-device       ... to File > Save Device to Image File
BifrostFM.exe --write-device      ... to File > Write Image File to Device
BifrostFM.exe --partition-device  ... to File > Partition/Format > Device
BifrostFM.exe --check-device      ... to File > Check Filesystem's device picker
BifrostFM.exe --partition-device --disk <\\.\PhysicalDriveN> --after <pid>
                                  that disk's partition window, its risk warning taken
                                  as answered (the restart as Administrator uses it;
                                  a --disk without --after is dropped)
BifrostFM.exe --after <pid> [...] wait for that process to exit first
                                  (the restart as Administrator uses it)
BifrostFM.exe --help | -h         print the usage text
BifrostFM.exe --version           print the version
```

The device options are how the restart as Administrator resumes the
command that asked for it; they can be given by hand too. Exit codes: 0
success; 1 the engine could not be loaded; 2 bad usage.

## How the code is written

BifrostFM's own source -- the program and the engine's own files; the
open-source libraries built into the engine keep their own style --
follows a few rules throughout, chosen so the program can be read,
searched and debugged from the outside, with a text editor and `grep` as
readily as with a debugger, and so that a whole class of timing faults
cannot happen at all.

**One worker runs every command, one at a time.** A click, a key or a
menu item does no work of its own: its handler only records what was
asked and POSTS a request to the command system (`gui\CommandSystem.cs`
and the `Command*.cs` files beside it), then returns. The command system
has ONE worker thread, which takes the requests in order and runs each
one's whole sequence -- every step, every question it asks, every engine
call -- before it takes the next. The worker alone changes the program's
state (the open filesystem, the editors, the windows that own a disk);
the window never does. When the worker needs to ask the user something
(an Overwrite question, a warning, a picker), it hands the question to
the window, which shows it on its own message loop and posts the answer
back while the worker waits. Results, progress and the program's state
come back to the window the same way, as posted "views" it draws from
(`BeginInvoke`); the worker never calls window code directly. The engine
is called from that one thread only, and refuses a second call made
while one is running (`BFE_ERR_BUSY`).

**No re-entrancy, by structure rather than by locks.** Because every
command is a request in one queue run by one thread, two commands cannot
overlap, a double click cannot start one twice (a request that duplicates
one already waiting is dropped), and nothing can be started in the
middle of another: a gate checks the program's state before each request
runs and refuses one that does not fit (Check while a partition window
owns the disk, say), with the reason on the status line. There are no
busy flags or semaphores guarding operations: the command system's one
lock is held only while a request, a question or an answer is handed
between the window and the worker, never while work is done (the only
other lock in the program is the debug log's, round each line it
writes). The window's message loop is never pumped from inside a handler
(no `Application.DoEvents`), so a handler can never be entered again
while it is still running.

**Every name can be found with a text search.** Every class, function,
field and constant outside a function's own locals is named
`GROUP_Name`: a prefix naming the class or module it belongs to (`MAIN_`,
`CFC_`, `SYS_`, `BFE_` for the engine's API, `BFX_` and `BFC_` inside the
engine ...), its case saying whether it is visible outside that class
(`MAIN_` or private `Main_`). Each name is unique across the program --
no overloads, no two classes with a member of the same name, no
inheritance between classes of ours, no interfaces or virtual methods of
ours, and nothing chosen at run time by reflection or by a name in a
string: which code runs is written out in the code. So a search for a
name finds its one definition and every place it is used, in any editor
or with `grep`, and a search for a prefix finds everything that belongs
to that part of the program. Fields carry a short type tag after the
prefix (`Main_bImageOpen` is a bool, `Main_tsbClose` a toolbar button,
`pfn` a function pointer).

**Every event has its own handler, so every event can be traced.** Each
control's event is wired to a handler written for that control alone
(`Main_OnCloseButtonClicked`), never one shared between controls that
decides what to do from the sender; a menu item and a toolbar button for
the same command each have their own, both calling the same function.
There are no lambdas or anonymous delegates: every callback is a named
function. A callback the program holds is a single named function-pointer
field (`pfn...`), set explicitly, never a list of subscribers. So a
breakpoint or a log line in a handler catches exactly one event from
exactly one control, and searching for a handler's name shows exactly
where it is hooked. (The one allowance: controls made at run time, such
as one menu item per disk found, may share a handler.)

**Every function says what it does.** Each one has a boxed header saying
what it does, why, its arguments and what it returns; each group of
lines inside says what it does and why it is done that way. The aim is
that the code could almost be rebuilt from its comments, and that a
reader never has to guess a reason.

## Building it

The program is built in two steps, the engine first, each by one batch
file run with `cmd.exe`; `build.bat` runs both. Every source file is
named in its script, so a file missing from the tree stops the build.

### The tools needed

| Tool | What for | Where to get it |
|---|---|---|
| **64-bit Windows** with `cmd.exe` | Running the two batch files | -- |
| **WinLibs MinGW-w64 GCC**, x86_64, UCRT runtime, POSIX threads (15.2.0 was used for this release), with its `gcc` and `windres` on `PATH` | Compiling the engine DLL (C), and compiling the program's resources (icon, version, the embedded DLL) | https://winlibs.com, or `winget install BrechtSanders.WinLibs.POSIX.UCRT` |
| **.NET Framework 4.x** and its `csc.exe` (C# 5 compiler), at `%windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` | Compiling the program (C#) | Part of Windows 10 and 11; nothing to install |

Nothing else is needed: no Visual Studio, no MSBuild, no CMake, no NuGet,
no make. The engine's libraries (lwext4, FatFs, e2fsprogs, liblzma, zlib
and zstd) are all in `engine\` and are compiled by its batch file. Another
64-bit MinGW-w64 gcc may well work, but only the one above was used.

### The steps

In `cmd.exe`, with `gcc` and `windres` on `PATH`:

```
build.bat release same
```

`build.bat` builds the engine, then the program, passing its arguments on
to the program's build; it stops at the first step that fails, and ends
with BUILD SUCCESSFUL or BUILD FAILED. Every build script works from its
own folder and names every file relative to it, so it can be run from any
folder and the tree builds wherever it is put. The two steps it runs can
also be run by hand:

```
engine\build_engine.bat
gui\build_gui.bat release same
```

1. `engine\build_engine.bat` compiles the engine and writes
   `engine\out\bifrost_engine.dll` (its objects in `engine\out\obj\`). It
   takes a few minutes. It prints one compiler warning, from e2fsprogs'
   `util.c` -- *"Don't know how to detect memory on your platform?"* -- which
   is expected and harmless: the figure that function cannot find on
   Windows is used only by parts of e2fsck the engine does not run.
2. `gui\build_gui.bat release same` compiles the program and writes
   `out\BifrostFM.exe`, with the engine DLL embedded in it: the same
   single-file program as the one in this folder. Its intermediate files go
   to `gui\obj\`.

**The version.** `gui\version.txt` holds the version, `2.4.12`. With `same`,
the build carries that number unchanged, so it is the 2.4.12 release
rebuilt. Without it, every build steps the last number by one (`2.4.13`,
`2.4.14` ...) and writes the new number back to `gui\version.txt`.

**A development build** -- `build.bat` (or `gui\build_gui.bat`) with no
arguments -- also
leaves a copy of the DLL beside the exe (which the program then uses in
preference to its embedded one) and keeps a debug log on every run, in
`out\log\`.

The build closes a running `BifrostFM.exe` first, forcibly (anything
unsaved in it is lost), because Windows will not let a running exe be
replaced.

### The icon (optional)

`gui\art\BifrostFM.ico` and `gui\art\BifrostFM.png` are included, so the
program builds without this. They are drawn in code by `tools\MakeIcon.cs`;
after changing the drawing, `tools\build_icon.bat` compiles it with the same
`csc.exe` and runs it, writing both files again.

## Licence

BifrostFM is copyright (c) 2026 B. C. Services and licensed under the
**GNU General Public License, version 2 or (at your option) any later
version** (`LICENSE`; the same text is beside the engine as
`engine\GPL-2.0.txt`).

The open-source code built into the engine keeps its own copyright and
licence, as its files state:

- **lwext4** (`engine\lwext4\`) -- Grzegorz Kostka and contributors; BSD.
  BifrostFM's changes to it are marked `BifrostFM patch N` where they are
  made.
- **e2fsprogs 1.47.4** (`engine\e2fsprogs\`: e2fsck, resize2fs and the
  libraries they use) -- Theodore Ts'o and contributors; the GNU GPL, the
  GNU LGPL and, for the UUID library, BSD (`engine\e2fsprogs\lib\uuid\COPYING`).
- **liblzma** from XZ Utils 5.8.1 (`engine\compress\xz-5.8.1\`) -- 0BSD
  (`COPYING`, `COPYING.0BSD`).
- **zlib 1.3.1** (`engine\compress\zlib-1.3.1\`) -- the zlib licence
  (`LICENSE`).
- **zstd 1.5.7** (`engine\compress\zstd-1.5.7\`) -- BSD, or GPL-2.0 at your
  choice (`LICENSE`, `COPYING`).
- **FatFs R0.16** (`engine\fatfs\`) -- ChaN; a one-condition BSD-style
  licence (`engine\fatfs\LICENSE.txt`).

`SOURCES.md` gives the detail, folder by folder. Upstream's own test data
and test programs are not included in this source release.
