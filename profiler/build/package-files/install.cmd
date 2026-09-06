@echo off
setlocal

rem Registers this copy of the profiler's MCP server with Claude Code, at user scope.
rem Nothing is copied anywhere: the package runs from where you unpacked it, so keep
rem this folder where it is (or re-run install.cmd after moving it).
rem
rem   install.cmd                  register the MCP server
rem   install.cmd /remove          unregister it
rem   install.cmd /name <name>     register under another name, so this copy can sit
rem                                beside an existing installation (/remove takes it too)
rem
rem Requires: .NET 10 runtime, adb on PATH (or ANDROID_HOME/ANDROID_SDK_ROOT).

set "HERE=%~dp0"
set "DLL=%HERE%bin\NetAndroidProfiler.Mcp.dll"
set "NAME=net-android-profiler"
set "ACTION=add"

:args
if "%~1"=="" goto :args_done
if /i "%~1"=="/remove" (set "ACTION=remove" & shift & goto :args)
if /i "%~1"=="/name" (set "NAME=%~2" & shift & shift & goto :args)
echo Unknown option: %~1
echo Usage: install.cmd [/name ^<name^>] [/remove]
exit /b 2

:args_done
if /i "%ACTION%"=="remove" (
    claude mcp remove --scope user %NAME%
    exit /b %errorlevel%
)

if not exist "%DLL%" (
    echo Not found: %DLL%
    echo Unpack the whole package, then run install.cmd from inside it.
    exit /b 1
)

where adb >nul 2>&1
if errorlevel 1 (
    if "%ANDROID_HOME%%ANDROID_SDK_ROOT%"=="" (
        echo Warning: adb is not on PATH and ANDROID_HOME is not set.
        echo          The profiler needs the Android platform-tools to reach a device.
    )
)

if exist "%HERE%tools\dotnet-dsrouter.exe" (
    echo dotnet-dsrouter: using the copy in this package.
) else (
    echo dotnet-dsrouter: not in this package, will fall back to the global tool.
    echo                  Install it with: dotnet tool install -g dotnet-dsrouter
)

claude mcp get %NAME% >nul 2>&1
if not errorlevel 1 (
    echo Already registered; pointing it at this package.
    claude mcp remove --scope user %NAME% >nul 2>&1
)

claude mcp add --scope user %NAME% -- dotnet "%DLL%"
if errorlevel 1 (
    echo Registration failed. Is the Claude Code CLI on PATH?
    exit /b 1
)

echo.
echo Registered %NAME%. Restart your Claude Code sessions to pick it up.
echo The GUI, when this package carries it, is gui\NapGui.exe.
echo The command line is bin\nap.exe (nap --help).
exit /b 0
