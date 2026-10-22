@echo off
rem ---------------------------------------------------------------------------
rem build_icon.bat -- build the icon-drawing tool (MakeIcon.exe) and run it,
rem which regenerates the program's artwork:
rem
rem     ..\gui\art\BifrostFM.ico    the program icon (compiled into the exe by
rem                                 ..\gui\build_gui.bat through engine.rc)
rem     ..\gui\art\BifrostFM.png    the 256-pixel picture the About window shows
rem
rem SPDX-License-Identifier: GPL-2.0-or-later
rem Copyright (c) 2026 B. C. Services
rem
rem The two files are COMMITTED, so the program builds without running this;
rem run it only after changing the drawing in MakeIcon.cs.  MakeIcon.exe is
rem development tooling and is not part of the program.
rem
rem Output: out\MakeIcon.exe here, and the two art files.  Run it from
rem anywhere: it works from its own folder, every path relative to it.
rem ---------------------------------------------------------------------------
rem The variables below stay local to this script.
setlocal

rem Into this script's own folder (popd at each exit), so every path below is
rem relative to it; HERE is that folder (it ends in a backslash), then the
rem output folder, and the compiler: the 64-bit .NET Framework 4 csc.exe that
rem ships with Windows, found through %windir% so no path is fixed.
pushd "%~dp0"
set "HERE=.\"
set "OUT=out"
set "CSC=%windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

rem The output folder, made the first time.
if not exist "%OUT%" mkdir "%OUT%"

rem The tool is one source file, named here, compiled as a console program
rem against System.dll and System.Drawing.dll (GDI+, for the drawing).  The
rem errorlevel test follows csc directly, with nothing between that could
rem reset it.
"%CSC%" /nologo /target:exe /warn:4 /out:"%OUT%\MakeIcon.exe" ^
    /reference:System.dll /reference:System.Drawing.dll ^
    "%HERE%MakeIcon.cs"
if errorlevel 1 goto :error

rem Draw the artwork into the GUI's art folder, replacing the two committed
rem files in place; MakeIcon.exe exits non-zero on any failure (2 usage,
rem 3 anything else), which stops the script here.
"%OUT%\MakeIcon.exe" "%HERE%..\gui\art"
if errorlevel 1 goto :error

rem Both steps worked: back to the caller's folder.
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
