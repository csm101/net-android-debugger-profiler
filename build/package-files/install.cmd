@echo off
setlocal

rem Installs this package into Claude Code and, on Windows, puts a shortcut to the GUI on
rem the desktop. Nothing is copied anywhere: the package runs from where you unpacked it,
rem so keep this folder where it is (or re-run install.cmd after moving it).
rem
rem   install.cmd                  register the plugin (server + skill), create the shortcut
rem   install.cmd /remove          undo both
rem   install.cmd /no-shortcut     register only
rem   install.cmd /mcp-only        register the server with `claude mcp add` and copy the
rem                                skill under %USERPROFILE%\.claude\skills, for a Claude Code
rem                                without plugin support (/remove undoes this form too)
rem
rem Requires: .NET 10 runtime; the .NET SDK with the android workload to build apps for
rem profiling; adb on PATH (or ANDROID_HOME/ANDROID_SDK_ROOT).

set "HERE=%~dp0"
if "%HERE:~-1%"=="\" set "HERE=%HERE:~0,-1%"
set "DLL=%HERE%\bin\NetAndroid.Mcp.dll"
set "GUI=%HERE%\gui\NapGui.exe"
set "SKILL_SRC=%HERE%\skills\net-android"
set "SKILL_DST=%USERPROFILE%\.claude\skills\net-android"
set "SHORTCUT=%USERPROFILE%\Desktop\NET Android Profiler.lnk"
set "NAME=net-android"
set "ACTION=add"
set "SHORTCUT_WANTED=1"
set "MCP_ONLY=0"

:args
if "%~1"=="" goto :args_done
if /i "%~1"=="/remove" (set "ACTION=remove" & shift & goto :args)
if /i "%~1"=="/no-shortcut" (set "SHORTCUT_WANTED=0" & shift & goto :args)
if /i "%~1"=="/mcp-only" (set "MCP_ONLY=1" & shift & goto :args)
echo Unknown option: %~1
echo Usage: install.cmd [/remove] [/no-shortcut] [/mcp-only]
exit /b 2

:args_done
if /i "%ACTION%"=="remove" goto :remove

if not exist "%DLL%" (
    echo Not found: %DLL%
    echo Unpack the whole package, then run install.cmd from inside it.
    exit /b 1
)

where adb >nul 2>&1
if errorlevel 1 (
    if "%ANDROID_HOME%%ANDROID_SDK_ROOT%"=="" (
        echo Warning: adb is not on PATH and ANDROID_HOME is not set.
        echo          Both engines need the Android platform-tools to reach a device.
    )
)

if exist "%HERE%\tools\dotnet-dsrouter.exe" (
    echo dotnet-dsrouter: using the copy in this package.
) else (
    echo dotnet-dsrouter: not in this package, will fall back to the global tool.
    echo                  Install it with: dotnet tool install -g dotnet-dsrouter
)

where claude >nul 2>&1
if errorlevel 1 (
    echo The Claude Code command line ^(claude^) is not on PATH: nothing was registered.
    echo Register by hand from a shell that has it:
    echo     claude plugin marketplace add "%HERE%"
    echo     claude plugin install %NAME%@%NAME%
    goto :shortcut
)

if "%MCP_ONLY%"=="1" goto :mcp_only

echo Registering the plugin ^(server and skill^) ...
claude plugin marketplace add "%HERE%" >nul 2>&1
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
    echo Already registered; pointing %NAME% at this package.
    claude mcp remove --scope user %NAME% >nul 2>&1
)
claude mcp add --scope user %NAME% -- dotnet "%DLL%"
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
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$s = (New-Object -ComObject WScript.Shell).CreateShortcut('%SHORTCUT%');" ^
  "$s.TargetPath = '%GUI%'; $s.WorkingDirectory = '%HERE%\gui';" ^
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
echo Unregistered %NAME%. The package folder itself was left in place.
exit /b 0

:done
echo.
echo The GUI is gui\NapGui.exe, the command line bin\nap.exe (nap --help).
exit /b 0
