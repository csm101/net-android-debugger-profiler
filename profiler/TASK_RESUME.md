# Task resume

## Current task
Everything that could be done without new hardware is done. This round closed the
test gaps the catalog had carried and finished the AOT question.

Landed since the last note:
- **Native AOT is complete.** nap and the MCP server both publish natively
  (21 MB and 29 MB), and all four collection paths run from a native build:
  sampling, heap, provider instrumenting and weaver instrumenting - the last of
  them rewriting IL with Mono.Cecil. The MCP server needed no code at all;
  ModelContextProtocol 2.2.0 is AOT-clean. What is left is a distribution
  decision, not a technical blocker (docs/PACKAGING.md).
- **A heap snapshot no longer freezes the app it measures.** Heap sessions
  honoured SuspendOnStart, so the app never ran, the warm-up ticked against a
  frozen process, and the session ended "no objects" - on both frontends'
  defaults. Heap never suspends now.
- **The package was tried the way a customer would**: unpacked outside the
  repository, `nap doctor` reports its own dsrouter, `nap run` profiles,
  `install.cmd /name` registers beside an existing installation, the packaged GUI
  opens a session.
- **Test gaps closed**: Stop() without a duration, startup profiling (OnCreate in
  the tree), symbolication of generics and state machines, the prerequisite
  messages (missing diagnostics component, Release without MONO_DIAGNOSTICS, AOT),
  a session driven end to end over HTTP, and multi-assembly - for which the test
  app gained a second assembly.

## What is left, and who it needs
- **U10, physical devices** - needs hardware. adb reverse, arm64, vendor
  restrictions on run-as: the only risk that cannot be reduced from here.
- **Distribution decision** - native executables per platform, or the
  framework-dependent folder that runs anywhere with .NET 10.
- **A clean machine** - the package has not been run where there is no .NET SDK
  and no repository.
- **the reference application** - U4b (suspend against its watchdogs) and U20 (provider
  instrumenting unusable on net9; it unblocks when V7 moves to net10).
- **U23** - the device stops instrumenting after prolonged profiling; cause
  unknown, restart clears it, three provider tests skip while it lasts.
- **CI** - nothing is automated; the GUI checks, the control path and the device
  suite all run by hand, and a fixed device would make them a real gate.

## Files in focus
tests/NetAndroidProfiler.Tests/{Device/SessionTests.cs, Device/ControlServiceDeviceTests.cs,
Fast/PrerequisiteTests.cs, Fast/GenericAndStateMachineSymbolsTests.cs};
TestTarget.Support/; src/NetAndroidProfiler.Core/Sessions/ProfilerSession.cs.

## Next action if interrupted right now
Nothing is half-done. Pick from the list above; U10 first if a device appears.

## How to run what exists
    dotnet build NetAndroidProfiler.slnx
    dotnet test  NetAndroidProfiler.slnx
    powershell -File build\package.ps1          the redistributable zip, into dist\
    dotnet publish src\NetAndroidProfiler.Cli -c Release -r win-x64 -p:NapAot=true
                                                native nap (needs vswhere on PATH)
    gui\build-gui.cmd                           builds NapGui.exe
    gui\tests\smoke.ps1 -Sessions @(...)        every panel and dialog of the real window
    gui\ControlTests.exe <nap.exe> [serial]     the GUI's control path, end to end

The companion build the prerequisite test wants:
    dotnet build TestTarget/TestTarget.csproj -c Debug -p:EnableDiagnostics=false
        -p:ApplicationId=com.mcasoftware.testtarget.nodiag -t:Install -p:AdbTarget="-s emulator-5556"

## Traps found here
- Evidence collected before a fix stays in the docs and looks authoritative:
  re-test a "known" blocker before investigating it. A whole measurement table
  was written from a degraded device and had to be retracted.
- A GUI that writes its preferences on exit can poison its own next start.
- Child processes inherit stdin: in a stdio frontend that is the client's
  request stream.
- Waiting on a log line with `until ... sleep` leaves a shell spinning for ever
  when the run it watches is killed. Bound every wait.
- A test that names a specific method reports the app's scheduling, not the
  profiler; assert what the profiler guarantees.
