@echo off
rem Build the GUI with the DevExpress / SynEdit paths of the installed RAD Studio.
rem The unit search path comes from the IDE's Win64 library path, so a machine that
rem can open the project in the IDE can also build it here.
setlocal
pushd "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0make-cfg.ps1" || goto :error
call rsvars.bat || goto :error
dcc64 -B NapGui.dpr || goto :error

rem The crash report is only useful when it resolves to a unit and a line. The detailed
rem map is 100 MB and nobody would ship that, so the debug data goes inside the executable
rem instead (+3 MB): JclDebug reads it from there, with no file to keep beside it.
rem Without the tool the build still succeeds - reports just lose their line numbers.
if "%NAP_JCLDEBUG%"=="" set "NAP_JCLDEBUG=C:\Athens\binaries_qbf\InsertJCLDebugInfo.exe"
if exist "%NAP_JCLDEBUG%" (
    "%NAP_JCLDEBUG%" NapGui.exe NapGui.map || echo WARNING: the debug data could not be inserted; crash reports will show addresses without lines
) else (
    echo WARNING: %NAP_JCLDEBUG% not found: crash reports will show addresses without lines.
    echo          Set NAP_JCLDEBUG to the JCL InsertJCLDebugInfo tool, or keep NapGui.map beside the exe.
)

popd
echo Built gui\NapGui.exe
exit /b 0

:error
popd
echo BUILD FAILED
exit /b 1
