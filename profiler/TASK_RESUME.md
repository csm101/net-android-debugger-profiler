# Task resume

## Current task
Nothing is half-done. The last piece landed was a third instrumenting mode and an
automatic engine choice.

**The collector can keep a calling context tree instead of streaming every call.** One
node per call path with calls, inclusive and exclusive time, min and max; a call updates
counters instead of writing a record. Everything downstream reads the same tables,
because a call tree, a call graph, parents/children and the critical path are what those
nodes are. Measured, host (Fib(27), one million calls): 123-131 ns and 16.1 MB streaming
against 55-66 ns and 1.6 KB as a tree. Device, same eight seconds of TestTarget: 346,589
calls and 8.6 MB against 361,934 calls and 1 KB - the tree records more because it slows
the app less. `DevTools/WeaveBench` reproduces the host numbers.

**The engine is chosen automatically** unless the caller says otherwise: the session looks
at the app, weaves when its assemblies can be rewritten (weaver-tree), falls back to the
runtime provider when they cannot, and logs which and why. `--engine` still takes
auto | weaver-tree | weaver | provider, and the GUI's Setup dialog offers all four.

What the tree does not keep: the order calls happened in, durations beyond min/max, and
the allocating method - allocations stay events, so in tree mode they are reported by
type and not by site. Those are the reasons to choose `weaver`.

Suite at that point: 94 passed, 7 skipped, 0 failed.

## Picking this up on another machine
The repository carries the code, the docs and the tests. It does not carry:

- **the emulator and the installed apps.** The device tests want TestTarget installed as a
  Debug build with EnableDiagnostics=true, and one test wants the companion build without
  it (both commands under "How to run what exists"). NAP_TEST_SERIAL / NAP_TEST_PACKAGE
  override the defaults (emulator-5556, com.mcasoftware.testtarget).
- **the package** (dist/ is ignored): rebuild it with build\package.ps1.
- **the Native AOT prerequisites**: the Visual C++ build tools and Windows SDK, plus
  vswhere on PATH when publishing. docs/PACKAGING.md has the exact components and the
  installer traps.
- **the Delphi side's inputs**: RAD Studio with DevExpress and SynEdit, found through the
  IDE's own search path by gui\make-cfg.ps1.

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
src/NetAndroidProfiler.Collector/{Profiler.cs, CallTree.cs};
src/NetAndroidProfiler.Core/Weaving/{WeaveTreeAnalyzer.cs, WeaveDeployer.cs};
src/NetAndroidProfiler.Core/Sessions/{ProfilerSession.cs, SessionSpecFactory.cs};
DevTools/WeaveBench/; tests/NetAndroidProfiler.Tests/Fast/CallTreeTests.cs.

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
