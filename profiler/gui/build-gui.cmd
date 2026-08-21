@echo off
rem Build the GUI with the DevExpress / SynEdit paths of the installed RAD Studio.
rem The unit search path comes from the IDE's Win64 library path, so a machine that
rem can open the project in the IDE can also build it here.
setlocal
pushd "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0make-cfg.ps1" || goto :error
call rsvars.bat || goto :error
dcc64 -B NapGui.dpr || goto :error
popd
echo Built gui\NapGui.exe
exit /b 0

:error
popd
echo BUILD FAILED
exit /b 1
