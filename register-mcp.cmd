@echo off
setlocal

rem Publishes the unified MCP server (debugger and profiler in one process, Release) to a stable
rem folder outside the repo and registers it once in Claude Code at user scope as "net-android".
rem Re-running only republishes.
rem
rem   register-mcp.cmd            publish to %LOCALAPPDATA%\net-android and register
rem   set NA_INSTALL_DIR=D:\x     override the install folder before running
rem
rem The two product servers (register-mcp-debugger.cmd, register-mcp-profiler.cmd) are untouched.
rem Their tools are all in this server, so a Claude Code that has all three registered sees every
rem debugger and profiler tool twice; remove the two product registrations when this one is in use:
rem   claude mcp remove --scope user net-android-debugger
rem   claude mcp remove --scope user net-android-profiler
rem
rem Close any open MCP session (close Claude Code) before running: a running server keeps the dll
rem locked and the publish step fails. Profiling needs dotnet-dsrouter on the machine:
rem   dotnet tool install -g dotnet-dsrouter

set "REPO=%~dp0"
set "PROJECT=%REPO%src\NetAndroid.Mcp\NetAndroid.Mcp.csproj"
if "%NA_INSTALL_DIR%"=="" set "NA_INSTALL_DIR=%LOCALAPPDATA%\net-android"
set "DLL=%NA_INSTALL_DIR%\NetAndroid.Mcp.dll"
set "NAME=net-android"

echo [1/3] Publishing %NAME% to %NA_INSTALL_DIR% ...
dotnet publish "%PROJECT%" -c Release -o "%NA_INSTALL_DIR%" -nologo -v:m
if errorlevel 1 (
    echo Publish failed. If the server is running, stop it and retry.
    exit /b 1
)
if not exist "%DLL%" (
    echo Publish did not produce %DLL%
    exit /b 1
)

rem The notices have to travel with the binaries they cover, not stay in the repo.
copy /y "%~dp0THIRD-PARTY-NOTICES.txt" "%NA_INSTALL_DIR%\THIRD-PARTY-NOTICES.txt" >nul
if errorlevel 1 (
    echo Could not copy THIRD-PARTY-NOTICES.txt next to the published binaries.
    exit /b 1
)

echo [2/3] Checking Claude Code registration ...
claude mcp get %NAME% >nul 2>&1
if errorlevel 1 goto register
echo Already registered. Current entry:
claude mcp get %NAME%
echo Done. The published server was updated in place.
goto done

:register
echo [3/3] Registering %NAME% at user scope ...
claude mcp add --scope user %NAME% -- dotnet "%DLL%"
if errorlevel 1 (
    echo Registration failed.
    exit /b 1
)
echo Registered. Restart Claude Code sessions to pick up the new server.

:done
echo.
echo MCP server: dotnet "%DLL%"
exit /b 0
