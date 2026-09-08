@echo off
setlocal enabledelayedexpansion

rem Installs this package for the current user: copies it to a stable place, registers it
rem with Claude Code (the MCP server and the skill, as one plugin) and puts a shortcut to
rem the GUI on the desktop.
rem
rem It copies on purpose. A registration points at an absolute path, so a package that was
rem registered where it was unpacked stops working the day that folder is moved, renamed or
rem emptied - and the failure looks like a broken server, not a moved folder. The copy goes
rem to %LOCALAPPDATA%\Programs\<this package's folder name>, one folder per version, and
rem what you unpacked can be deleted afterwards.
rem
rem   install.cmd                  install, register, create the shortcut
rem   install.cmd /here            register this folder where it is, copying nothing
rem   install.cmd /remove          unregister, remove the shortcut and the installed copy
rem   install.cmd /no-shortcut     do not create the desktop shortcut
rem   install.cmd /mcp-only        register the server with `claude mcp add` and copy the
rem                                skill under %USERPROFILE%\.claude\skills, for a Claude Code
rem                                without plugin support (/remove undoes this form too)
rem
rem Requires: .NET 10 runtime; the .NET SDK with the android workload to build apps for
rem profiling; adb on PATH (or ANDROID_HOME/ANDROID_SDK_ROOT).
rem
rem These binaries are not code-signed. Windows marks anything that came out of a
rem downloaded archive, and SmartScreen warns about it the first time it runs; the install
rem clears that mark from the copy it makes, which is what the warning is about.

set "SOURCE=%~dp0"
if "%SOURCE:~-1%"=="\" set "SOURCE=%SOURCE:~0,-1%"
for %%I in ("%SOURCE%") do set "PACKAGE=%%~nxI"
set "TARGET=%LOCALAPPDATA%\Programs\%PACKAGE%"
set "SKILL_DST=%USERPROFILE%\.claude\skills\net-android"
set "SHORTCUT=%USERPROFILE%\Desktop\NET Android Profiler.lnk"
set "NAME=net-android"
set "ACTION=add"
set "SHORTCUT_WANTED=1"
set "MCP_ONLY=0"
set "IN_PLACE=0"

:args
if "%~1"=="" goto :args_done
if /i "%~1"=="/remove" (set "ACTION=remove" & shift & goto :args)
if /i "%~1"=="/here" (set "IN_PLACE=1" & shift & goto :args)
if /i "%~1"=="/no-shortcut" (set "SHORTCUT_WANTED=0" & shift & goto :args)
if /i "%~1"=="/mcp-only" (set "MCP_ONLY=1" & shift & goto :args)
echo Unknown option: %~1
echo Usage: install.cmd [/here] [/remove] [/no-shortcut] [/mcp-only]
exit /b 2

:args_done
if /i "%ACTION%"=="remove" goto :remove

if not exist "%SOURCE%\bin\NetAndroid.Mcp.exe" (
    echo Not found: %SOURCE%\bin\NetAndroid.Mcp.exe
    echo Unpack the whole package, then run install.cmd from inside it.
    exit /b 1
)

if "%IN_PLACE%"=="1" (
    set "HOME_DIR=%SOURCE%"
    echo Registering this folder where it is: %SOURCE%
) else if /i "%SOURCE%"=="%TARGET%" (
    set "HOME_DIR=%SOURCE%"
    echo Already installed here: %TARGET%
) else (
    echo [1/3] Installing into %TARGET% ...
    robocopy "%SOURCE%" "%TARGET%" /e /njh /njs /ndl /nfl /np >nul
    if errorlevel 8 (
        echo Could not copy the package to %TARGET%.
        exit /b 1
    )
    set "HOME_DIR=%TARGET%"
)

set "SERVER=%HOME_DIR%\bin\NetAndroid.Mcp.exe"
set "GUI=%HOME_DIR%\gui\NapGui.exe"
set "SKILL_SRC=%HOME_DIR%\skills\net-android"

rem Windows keeps a "this came from the internet" mark on every file extracted from a
rem downloaded zip, and that mark is what raises the SmartScreen warning. The files being
rem cleared are the ones just chosen for installation.
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Get-ChildItem -LiteralPath '%HOME_DIR%' -Recurse -File | Unblock-File" >nul 2>&1

where adb >nul 2>&1
if errorlevel 1 (
    if "%ANDROID_HOME%%ANDROID_SDK_ROOT%"=="" (
        echo Warning: adb is not on PATH and ANDROID_HOME is not set.
        echo          Both engines need the Android platform-tools to reach a device.
    )
)

if exist "%HOME_DIR%\tools\dotnet-dsrouter.exe" (
    echo dotnet-dsrouter: using the copy in this package.
) else (
    echo dotnet-dsrouter: not in this package, will fall back to the global tool.
    echo                  Install it with: dotnet tool install -g dotnet-dsrouter
)

where claude >nul 2>&1
if errorlevel 1 (
    echo The Claude Code command line ^(claude^) is not on PATH: nothing was registered.
    echo Register by hand from a shell that has it:
    echo     claude plugin marketplace add "%HOME_DIR%"
    echo     claude plugin install %NAME%@%NAME%
    goto :shortcut
)

if "%MCP_ONLY%"=="1" goto :mcp_only

echo [2/3] Registering the plugin ^(server and skill^) ...
claude plugin marketplace add "%HOME_DIR%" >nul 2>&1
claude plugin install %NAME%@%NAME% --scope user
if errorlevel 1 (
    echo The plugin route failed; falling back to a plain server registration plus the skill.
    goto :mcp_only
)
echo Registered the plugin %NAME%. Restart your Claude Code sessions to pick it up.
goto :shortcut

:mcp_only
claude mcp get %NAME% >nul 2>&1
if not errorlevel 1 (
    echo Already registered; pointing %NAME% at this installation.
    claude mcp remove --scope user %NAME% >nul 2>&1
)
claude mcp add --scope user %NAME% -- "%SERVER%"
if errorlevel 1 (
    echo Registration failed.
    exit /b 1
)
if exist "%SKILL_SRC%\SKILL.md" (
    xcopy /e /i /y /q "%SKILL_SRC%" "%SKILL_DST%" >nul
    echo Skill copied to %SKILL_DST%.
)
echo Registered the server %NAME%. Restart your Claude Code sessions to pick it up.

:shortcut
if "%SHORTCUT_WANTED%"=="0" goto :done
if not exist "%GUI%" (
    echo No GUI in this package ^(gui\NapGui.exe^): no shortcut created.
    goto :done
)
echo [3/3] Creating the desktop shortcut ...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$s = (New-Object -ComObject WScript.Shell).CreateShortcut('%SHORTCUT%');" ^
  "$s.TargetPath = '%GUI%'; $s.WorkingDirectory = '%HOME_DIR%\gui';" ^
  "$s.Description = 'NET Android Profiler'; $s.Save()"
if errorlevel 1 (
    echo Could not create the desktop shortcut; the GUI is %GUI%.
) else (
    echo Desktop shortcut created: %SHORTCUT%
)
goto :done

:remove
where claude >nul 2>&1
if not errorlevel 1 (
    claude plugin uninstall %NAME%@%NAME% >nul 2>&1
    claude plugin marketplace remove %NAME% >nul 2>&1
    claude mcp get %NAME% >nul 2>&1
    if not errorlevel 1 claude mcp remove --scope user %NAME%
)
if exist "%SKILL_DST%\SKILL.md" (
    rmdir /s /q "%SKILL_DST%"
    echo Skill removed from %SKILL_DST%.
)
if exist "%SHORTCUT%" (
    del /q "%SHORTCUT%"
    echo Desktop shortcut removed.
)
rem The installed copy goes too - unless this script is the one running from inside it,
rem in which case deleting the ground it stands on is a mess; it says so instead.
if exist "%TARGET%\bin\NetAndroid.Mcp.exe" (
    if /i "%SOURCE%"=="%TARGET%" (
        echo Unregistered. This copy is the installed one: delete %TARGET% by hand to finish.
    ) else (
        rmdir /s /q "%TARGET%"
        echo Installed copy removed from %TARGET%.
    )
)
echo Unregistered %NAME%.
exit /b 0

:done
echo.
echo Installed in %HOME_DIR%
echo   the GUI:          %GUI%
echo   the command line: %HOME_DIR%\bin\nap.exe (nap --help)
echo What you unpacked can be deleted; this installation does not depend on it.
exit /b 0
