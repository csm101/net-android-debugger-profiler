@echo off
setlocal

rem Installs the VS Code extension that contributes the `net-android` debug type, by pointing the
rem user's extensions folder at the copy in this repository. The extension declares the debug type
rem and nothing else: VS Code refuses to run a debug adapter named directly in launch.json.
rem
rem   install-vscode-extension.cmd     install into %USERPROFILE%\.vscode\extensions
rem   set NAD_VSCODE_EXTENSIONS=...    install elsewhere (VS Code Insiders, a portable install)
rem
rem Run register-mcp.cmd first: it publishes the adapter this extension launches. Re-running this
rem script is safe; it replaces whatever is already installed under that name.

set "REPO=%~dp0"
set "SOURCE=%REPO%DevTools\vscode\net-android-debugger"
if "%NAD_VSCODE_EXTENSIONS%"=="" set "NAD_VSCODE_EXTENSIONS=%USERPROFILE%\.vscode\extensions"
set "TARGET=%NAD_VSCODE_EXTENSIONS%\net-android-debugger"
if "%NAD_INSTALL_DIR%"=="" set "NAD_INSTALL_DIR=%LOCALAPPDATA%\net-android-debugger"
set "DAP_DLL=%NAD_INSTALL_DIR%\NetAndroidDebugger.Dap.dll"

if not exist "%SOURCE%\package.json" (
    echo The extension sources are not at %SOURCE%
    echo Run this script from the repository that contains them.
    exit /b 1
)

rem The install replaces the target, so pointing it at the sources would delete them.
if /i "%TARGET%"=="%SOURCE%" (
    echo NAD_VSCODE_EXTENSIONS points at the extension sources themselves. Refusing.
    exit /b 1
)

echo [1/3] Checking the adapter ...
if exist "%DAP_DLL%" (
    echo     found %DAP_DLL%
) else (
    echo     WARNING: %DAP_DLL% is missing.
    echo     The extension will install, but a debug session fails until you run register-mcp.cmd.
)

echo [2/3] Preparing %NAD_VSCODE_EXTENSIONS% ...
if not exist "%NAD_VSCODE_EXTENSIONS%" (
    echo     that folder does not exist yet - creating it. Is VS Code installed for this user?
    mkdir "%NAD_VSCODE_EXTENSIONS%"
    if errorlevel 1 (
        echo Could not create %NAD_VSCODE_EXTENSIONS%
        exit /b 1
    )
)
if exist "%TARGET%" (
    echo     replacing the previous install
    rem A junction or symlink goes with a plain rmdir, which removes the link and not its target.
    rmdir "%TARGET%" 2>nul
    if exist "%TARGET%" rmdir /s /q "%TARGET%"
    if exist "%TARGET%" (
        echo Could not remove %TARGET% - close VS Code and retry.
        exit /b 1
    )
)

echo [3/3] Installing ...
rem A junction, not a symbolic link: it needs no elevation and no developer mode, and VS Code
rem follows it just the same.
mklink /J "%TARGET%" "%SOURCE%" >nul 2>&1
if not errorlevel 1 (
    set "METHOD=junction"
    goto installed
)
echo     the junction was refused; copying instead
xcopy "%SOURCE%" "%TARGET%\" /e /i /q /y >nul
if errorlevel 1 (
    echo Copy failed.
    exit /b 1
)
set "METHOD=copy"

:installed
echo.
echo Installed as a %METHOD%: %TARGET%
if "%METHOD%"=="junction" echo A junction tracks the repository, so a change to the extension needs no reinstall.
if "%METHOD%"=="copy" echo A copy is a snapshot - re-run this script after changing the extension.
if exist "%USERPROFILE%\.vscode-insiders\extensions" if "%NAD_VSCODE_EXTENSIONS%"=="%USERPROFILE%\.vscode\extensions" (
    echo.
    echo VS Code Insiders is also installed. For that one:
    echo     set NAD_VSCODE_EXTENSIONS=%%USERPROFILE%%\.vscode-insiders\extensions ^&^& install-vscode-extension.cmd
)
echo.
echo Restart VS Code - reloading the window is not enough for a newly installed extension.
echo Then a launch.json entry is enough to debug an app:
echo.
echo     { "type": "net-android", "request": "launch", "name": "My app", "packageName": "com.example.myapp" }
echo.
echo Every argument: DevTools\vscode\DAP_CLIENTS.md
exit /b 0
