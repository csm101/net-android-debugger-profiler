# Task resume

## Current task
P5, packaging, is done and pushed. `build\package.ps1` produces
dist\net-android-profiler-<version>.zip (63 MB): bin\ with the MCP server, nap and
nap-weave; tools\ with dotnet-dsrouter; build\ with the weaving targets and the copy
of nap-weave they run; gui\NapGui.exe; install.cmd, README, LICENSE, notices.

Verified by running the package, not by reading it: `dist\...\bin\nap.exe doctor`
reports the packaged dsrouter as the one in use, and
`nap run --package com.mcasoftware.testtarget --mode sampling --duration 12` profiled
emulator-5556 and wrote a schema v4 database stamped 0.2.0 (193 methods, 220 tree
nodes).

Landed with it:
- Directory.Build.props holds NapVersion; ProfilerSession.ToolVersion reads it from
  the assembly, so the database stamp cannot drift from what shipped.
- ToolLocator prefers the package's own tools over globally installed ones
  (env override, then package, then ~/.dotnet/tools, then PATH).
- nap gained `run` (one session, for scripts and CI) and `doctor` (which adb and
  dsrouter this machine uses, and whether they came from the package).
- THIRD-PARTY-NOTICES gained the two distributed components the deps.json sweep
  cannot see: dotnet-dsrouter, now redistributed, and SynEdit, compiled into the
  GUI - with the MPL 1.1 in full and a statement of which half of its dual licence
  this product takes.
- The packaged GUI looks for nap.exe in ..\bin\ too: it would otherwise have failed
  on the first click of New session.

## What is deliberately not done
- install.cmd has not been executed here: it re-registers the MCP server at user
  scope and would repoint this machine's net-android-profiler entry at the package.
  Run it (or `install.cmd /remove`) when that is what you want.
- Native AOT and obfuscation: measured, deferred, reasons in docs/PACKAGING.md.

## Next
U10, physical devices: never tried, and the adb reverse path differs from the
emulator's. It is the last thing that can still surprise the product in the field.

## Substep
Full suite green after the change: 72 passed, 7 skipped, 0 failed.

## Files in focus
build/package.ps1, build/package-files/{install.cmd, README.txt},
Directory.Build.props, docs/PACKAGING.md,
src/NetAndroidProfiler.Cli/Program.cs,
src/NetAndroidProfiler.Core/Devices/ToolLocator.cs,
tests/NetAndroidProfiler.Tests/Fast/PackagingTests.cs.

## Next action if interrupted right now
Start U10: put the profiler on a physical device and see what differs from the
emulator (adb reverse, ABI, vendor restrictions on run-as).

## How to run what exists
    dotnet build NetAndroidProfiler.slnx
    dotnet test  NetAndroidProfiler.slnx
    powershell -File build\package.ps1        the redistributable zip, into dist\
    dist\...\bin\nap.exe doctor               which adb and dsrouter a machine uses
    gui\build-gui.cmd                         builds NapGui.exe
    gui\tests\build-tests.cmd                 builds StoreTests.exe and ControlTests.exe
    gui\StoreTests.exe <session.db> [...]     data-layer checks
    gui\ControlTests.exe <nap.exe> [serial]   device path, end to end
    gui\tests\smoke.ps1 -Sessions @(...)      every panel and dialog of the real window

## Traps found here
- Evidence collected before a fix stays in the docs and looks authoritative:
  re-test a "known" blocker before investigating it.
- A GUI that writes its preferences on exit can poison its own next start; treat
  values read from disk as untrusted.
- `TScrollBox` does not publish `OnKeyDown`, and a `TPaintBox` cannot take focus:
  keyboard navigation inside a painted panel needs the form, not the control.
- Filling a `TdxBarCombo` raises the same change event a click does, while half
  the window does not exist yet.
- A packaging script that copies binaries proves nothing: the two defects found in
  this round (the GUI's service lookup, nap's missing `run`) were both visible only
  by running the package.

## Queue (highest value first)
- U10: physical devices - see Next.
- U4b: suspend choreography against the reference application's watchdogs.
- U19: symbol-server lookup for Desymbolicate.
- The MCP device tests still unchecked in TEST_CATALOG (profile_run,
  profile_start/profile_stop, profile_annotate_source).
- U11: the shared adb/deploy library with net-android-debugger.
