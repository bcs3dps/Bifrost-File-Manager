@echo off
rem ---------------------------------------------------------------------------
rem build_gui.bat -- build BifrostFM.exe, the WinForms file manager, with the
rem csc.exe that ships with the .NET Framework (C# 5, no Visual Studio).
rem
rem SPDX-License-Identifier: GPL-2.0-or-later
rem Copyright (c) 2026 B. C. Services
rem
rem Usage:
rem     build_gui.bat                 development build
rem     build_gui.bat release         release build
rem     build_gui.bat release same    release build at version.txt's version
rem                                   as it stands, not stepped -- how a
rem                                   release whose number was set by hand
rem                                   (2.3.0, say) is built AS that number
rem
rem Both builds EMBED the engine DLL in the exe as a Win32 RCDATA resource
rem (engine.rc), compiled with windres from the MinGW toolchain on PATH.
rem They differ in two things:
rem
rem   development : the DLL is ALSO copied beside the exe.  The program uses a
rem                 DLL beside it in preference to the embedded one, so a
rem                 freshly built engine is picked up without anything being
rem                 extracted.  DEBUG is defined, so every run keeps a debug
rem                 log (..\out\log\).
rem   release     : no DLL is left beside the exe (one already there is
rem                 removed), so the exe is the whole program: it extracts its
rem                 embedded engine to %TEMP% at start-up and deletes it on
rem                 exit (or at the next start, if it cannot then).  DEBUG is
rem                 not defined: a debug log is kept only when the program
rem                 is started with --debug.
rem
rem The exe is built x64 because the engine DLL is 64-bit.  Build the engine
rem first (..\engine\build_engine.bat).
rem
rem EVERY SOURCE FILE IS NAMED BELOW.  A .cs file in this folder that is not
rem listed is not part of the program, and a listed file that goes missing
rem stops the build.
rem
rem A running BifrostFM.exe is closed first, forcibly -- anything unsaved in
rem it is lost: Windows locks a running exe and the compiler could not
rem replace it.
rem
rem EVERY BUILD STEPS THE VERSION.  version.txt holds the last successful
rem build's version, MAJOR.MINOR.BUILD; make_version.bat writes this build's
rem (BUILD plus one) into obj\Version.cs and obj\version.h, and the new number
rem is copied over version.txt only once the exe has compiled, so a failed
rem build does not use up a number.  A development and a release build step it
rem alike.  version.txt changes with every build and is committed with the
rem work it built.  The one exception is "same" as the second argument: the
rem build then carries version.txt's own number (make_version.bat current),
rem so a release number written into version.txt by hand is the version the
rem release exe reports, and the next ordinary build steps on from it.
rem
rem Output: ..\out\BifrostFM.exe -- the program's folder at the TOP of the
rem tree, beside engine\ and gui\, so what ships sits apart from the sources
rem (and, for a development build, ..\out\bifrost_engine.dll).  The compiled
rem resource script is an intermediate and goes to obj\ here instead, so the
rem output folder holds only what the program needs.
rem ---------------------------------------------------------------------------
rem The variables below stay local to this script.  It works from its own
rem folder (pushd here, popd at each exit), so every path below is RELATIVE
rem to it and none names where the tree is -- which also keeps the compile
rem line, every source named on it, short wherever the tree lives.
setlocal

rem Into this script's own folder; HERE is that folder, relative (it ends in
rem a backslash); then the output folder, the intermediate folder, and the
rem compiler (Windows' own, found through %windir%).
pushd "%~dp0"
set "HERE=.\"
set "OUT=..\out"
set "OBJ=obj"
set "CSC=%windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

rem Close a running copy so its exe can be replaced.  Forced (/f): no save
rem question is asked.  Its result is ignored -- "not running" is the usual
rem answer -- and nothing tests errorlevel before make_version.bat's call
rem sets it again.
taskkill /f /im BifrostFM.exe >nul 2>&1

rem The output and intermediate folders, made the first time.
if not exist "%OUT%" mkdir "%OUT%"
if not exist "%OBJ%" mkdir "%OBJ%"

rem The engine DLL must exist: it is embedded in the exe.
if not exist "%HERE%..\engine\out\bifrost_engine.dll" (
    echo build the engine first: ..\engine\build_engine.bat
    goto :error
)

rem This build's version: obj\Version.cs for the compiler, obj\version.h for
rem the resource script, obj\version.new for version.txt afterwards.  "next"
rem steps the number; "same" as the second argument keeps version.txt's own
rem ("current"), for a release built at a number set by hand.
set "VERMODE=next"
if /i "%2"=="same" set "VERMODE=current"
call "%HERE%make_version.bat" %VERMODE%
if errorlevel 1 goto :error

rem Compile the resource script.  engine.rc names its files with no folder,
rem and windres looks for them in its include folders: the engine's output
rem folder for the DLL, the art folder for the icon and the About picture,
rem and obj\ for version.h.
windres -O res -I "..\engine\out" -I "%HERE%art" -I "%OBJ%" -i "%HERE%engine.rc" -o "%OBJ%\engine.res"
if errorlevel 1 goto :error

rem The sources, one per line.  The first is generated (above), the rest are
rem in the repository.
set SOURCES="%OBJ%\Version.cs"
set SOURCES=%SOURCES% "%HERE%Program.cs"
set SOURCES=%SOURCES% "%HERE%MainForm.cs"
set SOURCES=%SOURCES% "%HERE%MainCommand.cs"
set SOURCES=%SOURCES% "%HERE%CopyForms.cs"
set SOURCES=%SOURCES% "%HERE%PromptForm.cs"
set SOURCES=%SOURCES% "%HERE%MessageForm.cs"
set SOURCES=%SOURCES% "%HERE%NewImageForm.cs"
set SOURCES=%SOURCES% "%HERE%PartitionForm.cs"
set SOURCES=%SOURCES% "%HERE%PartitionTable.cs"
set SOURCES=%SOURCES% "%HERE%DiskLayoutForm.cs"
set SOURCES=%SOURCES% "%HERE%CheckForm.cs"
set SOURCES=%SOURCES% "%HERE%ImageFolderForm.cs"
set SOURCES=%SOURCES% "%HERE%ImageSaveForm.cs"
set SOURCES=%SOURCES% "%HERE%DragData.cs"
set SOURCES=%SOURCES% "%HERE%ShellIcons.cs"
set SOURCES=%SOURCES% "%HERE%EntryIcons.cs"
set SOURCES=%SOURCES% "%HERE%ToolIcons.cs"
set SOURCES=%SOURCES% "%HERE%PaneSorter.cs"
set SOURCES=%SOURCES% "%HERE%PaneListView.cs"
set SOURCES=%SOURCES% "%HERE%PropertiesForm.cs"
set SOURCES=%SOURCES% "%HERE%FsPropertiesForm.cs"
set SOURCES=%SOURCES% "%HERE%OpenScan.cs"
set SOURCES=%SOURCES% "%HERE%TextEditForm.cs"
set SOURCES=%SOURCES% "%HERE%HexEditForm.cs"
set SOURCES=%SOURCES% "%HERE%PictureViewForm.cs"
set SOURCES=%SOURCES% "%HERE%json\JsonEditForm.cs"
set SOURCES=%SOURCES% "%HERE%json\VoorheesDocument.cs"
set SOURCES=%SOURCES% "%HERE%json\VoorheesText.cs"
set SOURCES=%SOURCES% "%HERE%json\VoorheesTreeView.cs"
set SOURCES=%SOURCES% "%HERE%json\VoorheesFormat.cs"
set SOURCES=%SOURCES% "%HERE%json\VoorheesJson.cs"
set SOURCES=%SOURCES% "%HERE%json\VoorheesLines.cs"
set SOURCES=%SOURCES% "%HERE%json\VoorheesIni.cs"
set SOURCES=%SOURCES% "%HERE%json\VoorheesCfg.cs"
set SOURCES=%SOURCES% "%HERE%json\VoorheesReg.cs"
set SOURCES=%SOURCES% "%HERE%AboutForm.cs"
rem The command system: the worker thread that runs every engine call one
rem command at a time, the requests and results it is given and gives back,
rem its steps, and the windows' side that posts requests and shows what comes
rem back (the editors' saves included); then the debug-only values.
set SOURCES=%SOURCES% "%HERE%CommandContracts.cs"
set SOURCES=%SOURCES% "%HERE%CommandQuestions.cs"
set SOURCES=%SOURCES% "%HERE%CommandSystem.cs"
set SOURCES=%SOURCES% "%HERE%CommandInterrupts.cs"
set SOURCES=%SOURCES% "%HERE%CommandSequences.cs"
set SOURCES=%SOURCES% "%HERE%CommandCheckViews.cs"
set SOURCES=%SOURCES% "%HERE%CommandProgressWindows.cs"
set SOURCES=%SOURCES% "%HERE%CommandOperations.cs"
set SOURCES=%SOURCES% "%HERE%CommandCopySequence.cs"
set SOURCES=%SOURCES% "%HERE%CommandRawSequence.cs"
set SOURCES=%SOURCES% "%HERE%CommandRecycleSources.cs"
set SOURCES=%SOURCES% "%HERE%CommandClipboard.cs"
set SOURCES=%SOURCES% "%HERE%CommandNavigation.cs"
set SOURCES=%SOURCES% "%HERE%CommandImageOpening.cs"
set SOURCES=%SOURCES% "%HERE%CommandDeviceOpening.cs"
set SOURCES=%SOURCES% "%HERE%CommandStage.cs"
set SOURCES=%SOURCES% "%HERE%CommandArguments.cs"
set SOURCES=%SOURCES% "%HERE%CommandNativeSteps.cs"
set SOURCES=%SOURCES% "%HERE%CommandNativeOperations.cs"
set SOURCES=%SOURCES% "%HERE%CommandNativeControl.cs"
set SOURCES=%SOURCES% "%HERE%CommandImportControl.cs"
set SOURCES=%SOURCES% "%HERE%CommandLayoutReader.cs"
set SOURCES=%SOURCES% "%HERE%CommandClient.cs"
set SOURCES=%SOURCES% "%HERE%CommandEditorCoordinator.cs"
set SOURCES=%SOURCES% "%HERE%CommandEditorBinding.cs"
set SOURCES=%SOURCES% "%HERE%EditorSaveMenu.cs"
set SOURCES=%SOURCES% "%HERE%CommandQuestionPresenter.cs"
set SOURCES=%SOURCES% "%HERE%CommandPickerBranch.cs"
set SOURCES=%SOURCES% "%HERE%CommandPickerFolder.cs"
set SOURCES=%SOURCES% "%HERE%CommandInputs.cs"
set SOURCES=%SOURCES% "%HERE%CommandLifetimes.cs"
set SOURCES=%SOURCES% "%HERE%WindowQueue.cs"
set SOURCES=%SOURCES% "%HERE%DebugValues.cs"
set SOURCES=%SOURCES% "%HERE%EngineSession.cs"
set SOURCES=%SOURCES% "%HERE%EngineNative.cs"
set SOURCES=%SOURCES% "%HERE%AppSettings.cs"
set SOURCES=%SOURCES% "%HERE%lib\DebugLog.cs"
set SOURCES=%SOURCES% "%HERE%lib\DebugLogForm.cs"
set SOURCES=%SOURCES% "%HERE%lib\BusyModalForm.cs"

rem A development build defines DEBUG, which makes the program keep its debug
rem log on every run; a release build keeps one only when started with --debug.
set "DEFINES=/define:DEBUG"
if /i "%1"=="release" set "DEFINES="

rem The compile: a windowed x64 program at warning level 4, with the
rem compiled resources as its Win32 resources (version, icon, the embedded
rem DLL and picture), against WinForms, GDI+ and Microsoft.VisualBasic.dll
rem (its FileIO shell operations, used by MainForm.cs for Windows-side
rem deletes to the Recycle Bin).  /langversion:5 holds the source to C# 5,
rem the language the program is written to.  The errorlevel test follows csc
rem directly.
"%CSC%" /nologo /langversion:5 /target:winexe /platform:x64 /warn:4 %DEFINES% /out:"%OUT%\BifrostFM.exe" ^
    /win32res:"%OBJ%\engine.res" ^
    /reference:System.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ^
    /reference:Microsoft.VisualBasic.dll ^
    %SOURCES%
if errorlevel 1 goto :error

rem The exe compiled: its version becomes the last successful build's.
copy /Y "%OBJ%\version.new" "%HERE%version.txt" >nul
if errorlevel 1 goto :error
rem The new version read back, for the "built:" line below.
set "VNEW="
set /p VNEW=<"%OBJ%\version.new"

rem Development: the DLL beside the exe.  Release: none.
if /i "%1"=="release" (
    if exist "%OUT%\bifrost_engine.dll" del /q "%OUT%\bifrost_engine.dll"
    echo built: out\BifrostFM.exe version %VNEW%  ^(release: the engine is inside the exe^)
) else (
    copy /Y "%HERE%..\engine\out\bifrost_engine.dll" "%OUT%\bifrost_engine.dll" >nul
    if errorlevel 1 goto :error
    echo built: out\BifrostFM.exe version %VNEW%
)

rem Built: back to the caller's folder.
popd
endlocal
exit /b 0

rem Any step failed: its own message is already on the screen.  A failure
rem before the copy leaves version.txt as it was, so no number is used up.
rem Back to the caller's folder either way.
:error
echo BUILD FAILED
popd
endlocal
exit /b 1
