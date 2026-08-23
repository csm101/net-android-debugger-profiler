# Task resume

## Current task
Closing the MCP device gaps in TEST_CATALOG turned into two product findings, both
landed:

1. **Child processes were eating the MCP server's stdin.** adb and dotnet-dsrouter,
   started without redirecting it, inherit the parent's stdin and read from it - and
   in a stdio frontend that stdin is the client's request stream. profile_start was
   answered, the next status calls were answered, then the server went silent for
   good, alive and idle, with no error anywhere. Every child now redirects stdin and
   closes it. The device test that reproduced it hung all night before; it takes 17
   seconds now.

2. **The runtime-provider engine records allocations or enter/leave, never both.**
   Measured one variable at a time with the traces decoded by hand: it is the
   GCAllocation keyword, not the MONO_DIAGNOSTICS spelling, that costs the method
   events. The session warns and names engine=weaver, which records both; the table
   is in ANDROID_PROFILING_NOTES and contradicts an August note, which is left dated
   rather than deleted.

New: McpDeviceTests (profile_run, profile_start/status/stop, profile_annotate_source),
the JSON-RPC fixture moved to Tests/Support with a reader thread and the server's
stderr attached to failures, and nap run --no-allocations.

## Open
KNOWN_UNKNOWNS U23: the provider engine has spells where it records nothing at all -
not the process, not state we write, not our analyzer, all ruled out. Both provider
tests skip during a blackout so the suite reports our pipeline rather than the
runtime's mood. Next diagnostic worth trying: restart the emulator and see whether a
blackout ends with it.

AOT is still parked on the Visual C++ build tools: installing them from here fails at
the elevation prompt, and the VS Installer wants to update itself first
(docs/PACKAGING.md has the exact steps and the resume plan).

## Next
U10, physical devices: never tried, and the adb reverse path differs from the
emulator's.

## Substep
Full suite after the changes: 74-76 passed depending on the blackout, 0 failed.

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
