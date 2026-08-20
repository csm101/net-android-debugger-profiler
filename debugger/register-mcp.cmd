@echo off
setlocal

rem Publishes the MCP server (Release) to a stable folder outside the repo and
rem registers it once in Claude Code at user scope. Re-running only republishes.
rem
rem   register-mcp.cmd            publish to %LOCALAPPDATA%\net-android-debugger and register
rem   set NAD_INSTALL_DIR=D:\x    override the install folder before running
rem
rem Close any open MCP session (terminate_app / close Claude Code) before running:
rem a running server keeps the dll locked and the publish step fails.

set "REPO=%~dp0"
set "PROJECT=%REPO%src\NetAndroidDebugger.Mcp\NetAndroidDebugger.Mcp.csproj"
if "%NAD_INSTALL_DIR%"=="" set "NAD_INSTALL_DIR=%LOCALAPPDATA%\net-android-debugger"
set "DLL=%NAD_INSTALL_DIR%\NetAndroidDebugger.Mcp.dll"
set "NAME=net-android-debugger"

echo [1/3] Publishing %NAME% to %NAD_INSTALL_DIR% ...
dotnet publish "%PROJECT%" -c Release -o "%NAD_INSTALL_DIR%" -nologo -v:m
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
