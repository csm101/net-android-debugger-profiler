@echo off
setlocal

rem Publishes the MCP server (Release) to a stable folder outside the repo and
rem registers it once in Claude Code at user scope. Re-running only republishes.
rem
rem   register-mcp-profiler.cmd   publish to %LOCALAPPDATA%\net-android-profiler and register
rem   set NAP_INSTALL_DIR=D:\x    override the install folder before running
rem
rem Close any open MCP session (close Claude Code) before running: a running
rem server keeps the dll locked and the publish step fails.
rem
rem Prerequisites on this machine: adb on PATH (or ANDROID_HOME), and
rem   dotnet tool install -g dotnet-dsrouter

set "REPO=%~dp0"
set "PROJECT=%REPO%src\NetAndroidProfiler.Mcp\NetAndroidProfiler.Mcp.csproj"
if "%NAP_INSTALL_DIR%"=="" set "NAP_INSTALL_DIR=%LOCALAPPDATA%\net-android-profiler"
set "DLL=%NAP_INSTALL_DIR%\NetAndroidProfiler.Mcp.dll"
set "NAME=net-android-profiler"

echo [1/3] Publishing %NAME% to %NAP_INSTALL_DIR% ...
dotnet publish "%PROJECT%" -c Release -o "%NAP_INSTALL_DIR%" -nologo -v:m
if errorlevel 1 (
    echo Publish failed. If the server is running, stop it and retry.
    exit /b 1
)
if not exist "%DLL%" (
    echo Publish did not produce %DLL%
    exit /b 1
)

echo [2/3] Checking Claude Code registration ...
claude mcp get %NAME% >nul 2>&1
if not errorlevel 1 (
    echo Already registered. Current entry:
    claude mcp get %NAME%
    echo Done. The published server was updated in place.
    exit /b 0
)

echo [3/3] Registering %NAME% at user scope ...
claude mcp add --scope user %NAME% -- dotnet "%DLL%"
if errorlevel 1 (
    echo Registration failed.
    exit /b 1
)
echo Done. Restart Claude Code sessions to pick up the new server.
exit /b 0
