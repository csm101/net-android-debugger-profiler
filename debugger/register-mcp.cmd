@echo off
setlocal

rem Publishes both frontends (Release) to a stable folder outside the repo and registers the MCP
rem server once in Claude Code at user scope. Re-running only republishes.
rem
rem   register-mcp.cmd            publish to %LOCALAPPDATA%\net-android-debugger and register
rem   set NAD_INSTALL_DIR=D:\x    override the install folder before running
rem
rem Close any open MCP session (terminate_app / close Claude Code) before running: a running
rem server keeps the dll locked and the publish step fails.

set "REPO=%~dp0"
set "MCP_PROJECT=%REPO%src\NetAndroidDebugger.Mcp\NetAndroidDebugger.Mcp.csproj"
set "DAP_PROJECT=%REPO%src\NetAndroidDebugger.Dap\NetAndroidDebugger.Dap.csproj"
if "%NAD_INSTALL_DIR%"=="" set "NAD_INSTALL_DIR=%LOCALAPPDATA%\net-android-debugger"
set "MCP_DLL=%NAD_INSTALL_DIR%\NetAndroidDebugger.Mcp.dll"
set "DAP_DLL=%NAD_INSTALL_DIR%\NetAndroidDebugger.Dap.dll"
set "NAME=net-android-debugger"

echo [1/4] Publishing the MCP server to %NAD_INSTALL_DIR% ...
dotnet publish "%MCP_PROJECT%" -c Release -o "%NAD_INSTALL_DIR%" -nologo -v:m
if errorlevel 1 (
    echo Publish failed. If the server is running, stop it and retry.
    exit /b 1
)
if not exist "%MCP_DLL%" (
    echo Publish did not produce %MCP_DLL%
    exit /b 1
)

echo [2/4] Publishing the DAP adapter to %NAD_INSTALL_DIR% ...
dotnet publish "%DAP_PROJECT%" -c Release -o "%NAD_INSTALL_DIR%" -nologo -v:m
if errorlevel 1 (
    echo Publish of the DAP adapter failed. If it is running, stop it and retry.
    exit /b 1
)
if not exist "%DAP_DLL%" (
    echo Publish did not produce %DAP_DLL%
    exit /b 1
)

rem The notices have to travel with the binaries they cover, not stay in the repo.
copy /y "%~dp0THIRD-PARTY-NOTICES.txt" "%NAD_INSTALL_DIR%\THIRD-PARTY-NOTICES.txt" >nul
if errorlevel 1 (
    echo Could not copy THIRD-PARTY-NOTICES.txt next to the published binaries.
    exit /b 1
)

echo [3/4] Checking Claude Code registration ...
claude mcp get %NAME% >nul 2>&1
if errorlevel 1 goto register
echo Already registered. Current entry:
claude mcp get %NAME%
goto done

:register
echo [4/4] Registering %NAME% at user scope ...
claude mcp add --scope user %NAME% -- dotnet "%MCP_DLL%"
if errorlevel 1 (
    echo Registration failed.
    exit /b 1
)
echo Registered. Restart Claude Code sessions to pick up the new server.

:done
echo.
echo MCP server: dotnet "%MCP_DLL%"
echo DAP adapter: dotnet "%DAP_DLL%"
echo The DAP adapter is not registered anywhere - a DAP client runs it directly.
echo See DevTools\vscode\DAP_CLIENTS.md for ready-made client configuration.
exit /b 0
