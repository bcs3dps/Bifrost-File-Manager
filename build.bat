@echo off
rem ---------------------------------------------------------------------------
rem build.bat -- build all of BifrostFM: the engine DLL first, then the
rem program, which embeds it.
rem
rem SPDX-License-Identifier: GPL-2.0-or-later
rem Copyright (c) 2026 B. C. Services
rem
rem Usage:
rem     build.bat                 development build
rem     build.bat release         release build (one exe, the engine inside)
rem     build.bat release same    release build at gui\version.txt's version
rem                               as it stands, not stepped
rem
rem The arguments are gui\build_gui.bat's, passed on unchanged; its header
rem says what each build is.  The engine is built the same way for both.
rem
rem Needs: the WinLibs MinGW-w64 gcc, with windres, on PATH, and the .NET
rem Framework 4's csc.exe (part of Windows).  Output: engine\out\
rem bifrost_engine.dll, then out\BifrostFM.exe.
rem
rem Run it from anywhere: it works from its own folder (pushd here, popd at
rem each exit), and every path is relative to it.
rem ---------------------------------------------------------------------------
rem The variables below stay local to this script.
setlocal

rem Into this script's own folder, the top of the tree.
pushd "%~dp0"

rem The engine first: the program embeds the DLL it builds.  A failed engine
rem stops here, before the program is touched.
call engine\build_engine.bat
if errorlevel 1 goto :error

rem Then the program, with this script's arguments.
call gui\build_gui.bat %*
if errorlevel 1 goto :error

rem Both built: back to the caller's folder.
echo BUILD SUCCESSFUL
popd
endlocal
exit /b 0

rem Either step failed; its own message is already on the screen.  Back to
rem the caller's folder.
:error
echo BUILD FAILED
popd
endlocal
exit /b 1
