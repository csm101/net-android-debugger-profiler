@echo off
rem Builds the GUI check programs: the data layer against a session database, and the
rem control client against a device.
setlocal
pushd "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\make-cfg.ps1" || goto :error
copy /y "%~dp0..\NapGui.cfg" "%~dp0StoreTests.cfg" >nul
call rsvars.bat || goto :error
dcc64 -B StoreTests.dpr || goto :error
copy /y "%~dp0..\NapGui.cfg" "%~dp0ControlTests.cfg" >nul
dcc64 -B ControlTests.dpr || goto :error
popd
exit /b 0

:error
popd
echo BUILD FAILED
exit /b 1
