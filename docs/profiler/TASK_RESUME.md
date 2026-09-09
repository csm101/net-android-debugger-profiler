# Task resume

This project now lives in the `net-android-debugger-profiler` monorepo
(`C:\Athens\GitHub\net-android-debugger-profiler`, GitHub `csm101/net-android-debugger-profiler`,
private) as its profiler component: sources under `src/NetAndroidProfiler.*`, this document
under `docs/profiler/`. Repository-level state lives in the root `TASK_RESUME.md`; the shared
rules in the root `CLAUDE.md`, the component's own in `src/NetAndroidProfiler.Core/CLAUDE.md`.
## Current task
**Sampling stop that never ends the event stream (2026-09-06, evening).** Device suite on the API 33
emulator (`api_33_0` = emulator-5556) with port 9000 free: `Sampling_restart_session_finds_busy_method`
and `Stop_ends_a_session_that_was_started_without_a_duration` fail; the session log stops at
"stopping session" and the 4-minute cancel lands in `CopyToAsync` of the event stream. Same symptom
as `Sampling_attach_to_running_debug_app_without_restart` on API 30 in the monorepo's phase 3.
Substep: make the log say whether `StopAsync` returned (new "stop acknowledged" line) and what
dsrouter saw (`-v debug`, tail appended to the session log on failure), then reproduce with the
single test on emulator-5556. Done 19:37: the single test PASSES on API 33 (stop acknowledged in 170 ms,
stream drained), so the hang needs the suite's context: a session right after another one's
force-stop, or after a failed weaver/heap session. Next: full device suite on emulator-5556 with
the diagnostics in, then read the failing sessions' dsrouter tails. Done 19:38-20:03 (2 failed, then killed at the cap): attach session
193858: stop acknowledged, dsrouter forwarded 864118 bytes and disposed the stream instance ("Active
instances: 0"), the file holds 864090 = all of it minus the IPC header, the pipe read never ended.
Fix in `EventPipeCollector.DrainAfterStopAsync`: after the acknowledgement the drain ends when the
file stops growing for 5 s. Second failure, `Stop_ends...` (194258): runtime never connected for
2 min after a launch that followed the previous test's force-stop by a second; the dsrouter tail
was lost (cleanup disposed it before the catch), now logged from CleanupAsync. Next: run the attach
test and `Stop_ends` together on emulator-5556. Done 20:06-20:19: attach passes with the drain (stream
never ends on API 33, closed after 5 s with every byte, hotspots found); `Stop_ends` still failed
right after it. Snapshot during the stall (t+55 s): host has two ESTABLISHED runtime connections to
the router; on the device the app's first connection to 10.0.2.2:9000 is already TIME_WAIT while the
second is alive, so the runtime answered the environment request and closed, and the bytes sit in
the emulator's slirp until the app dies (that is when "runtime connected" always appeared). Fix:
`EventPipeCollector.ProbeEnvironmentAsync` gives each request a 10 s deadline and asks again on a
new connection, which the router pairs with the runtime's next one. Pair run 20:19: both pass
(two probes unanswered, the third answered in 8 ms). Also seen: a leftover `adb reverse tcp:9000`
during the stall (`ReverseRemoveAsync` ignores adb's exit code) - to check by hand. Next: full device
suite on emulator-5556 with both fixes, then TEST_CATALOG/NOTES, commit. Done 20:22-20:31: 24 passed,
1 failed (`Two_heap_snapshots_support_a_growth_diff`, 30 s), 7 skipped by design, 8.5 min, no test
host crash, weaver tests green (the marker problem was the stale environment). `adb reverse
--remove tcp:9000` works by hand (exit 0, list empty): the leftover seen mid-stall was timing.
The heap growth
failure was the attach budget (20 s) eaten by two 10 s probes: deadline now 3 s. Final run 20:36 on
emulator-5556: 25 passed, 0 failed, 7 skipped by design, 7 min 14 s, 19 probes abandoned and
retried. Device-free suite 111/111. API 30 (emulator-5554) 20:47: 25 passed, 0 failed, 7 skipped, 6 min 31 s (a first run there
broke off when the guest reached load average 15 and every adb command timed out at 60 s, after
which the test host crashed; idle again, all green). **Task closed.** Files: `Collection/EventPipeCollector.cs`, `Collection/DsRouterProcess.cs`,
`Sessions/ProfilerSession.cs`. Traps: a killed session leaves its override environment on the
device (uninstall + install restores the default); a weaver session on API 33 never sees the
collector marker and a heap snapshot crashed the test host - both still to look at after this.

### Previous task
**The example app (2026-09-05).** `examples/ProfileMeExample.sln` - MAUI Android app plus
`ProfileMeExample.Domain` library, seven screens, one deliberate problem each, a Guide per
screen (`Scenarios/ScenarioCatalog.cs`), README chapter "Example app: ProfileMeExample".
Code done, builds clean (0 warnings), installs and runs on emulator-5554; the Slow search
screen answered in 829 ms with 8,000 products, so the catalog is 30,000 now: 2,852 ms on the
x86_64 emulator (Debug, JIT). The other screens' sizes are first guesses, untimed.

- **Blocked on this machine: port 9000.** Docker Desktop's `sal-minio` container publishes
  127.0.0.1:9000-9001, so dsrouter cannot start and no session can run (the engine's own
  message says so). Stopping that container frees the profiler. Noted in
  ANDROID_PROFILING_NOTES "One session at a time per device".
- **Not yet verified:** that each screen produces the readings its Guide and the README
  table promise. To do, screen by screen, once the port is free: sampling on Slow search
  and Frozen button; instrumenting (`--callspec N:ProfileMeExample.Domain --assemblies
  ProfileMeExample.Domain`) on Chatty pricing, Async waterfall, Re-enumerated query;
  instrumenting with allocations on Allocation storm; heap with two snapshots on Leaky
  dashboard. Adjust the sizes (`CatalogSize`, `LineCount`, `SaleCount`, `StockLines` in the
  pages) so each run lasts a few seconds on the emulator, and correct any Guide text the
  data contradicts. Driving the UI from a prompt: `adb shell input tap`; the Search button
  of Slow search sits at 540,638 on a 1080x2400 emulator.
- emulator-5554 is shared with the debugger project's test runs, which keep bringing
  their own app to the foreground; `am start -n com.mcasoftware.profilemeexample/
  crc643c07963a192dd5bf.MainActivity` brings ours back.

Before that: **the GUI taking over what used to need a command prompt**: it starts from
the sources and prepares the machine and the app itself.

- **The sources drive the setup.** `AppProjectFinder` (Core/Projects) reads a solution, a
  project or a folder and answers the Android applications in it - an application declares
  an ApplicationId, is an Exe, or has a manifest package; a library that targets Android is
  not one. From the project file come the package, `bin/<Configuration>/<tfm>` (the pdbs),
  the assemblies of the referenced projects, and EnableDiagnostics /
  EmbedAssembliesIntoApk as declared. `Candidates` reads those assemblies with Cecil and
  offers their namespaces and types, so the callspec is a list instead of a typed field.
- **The app is built from the window.** `AppBuilder` runs `dotnet build -c <cfg>
  -t:Install -p:EnableDiagnostics=true -p:EmbedAssembliesIntoApk=false -p:AdbTarget=-s
  <serial>` as a background job whose output streams to the caller. The profiler still
  never rebuilds on its own: this runs on a click. An optional first step clears the app's
  `files/.__override__` through run-as - the trap of switching an app to fast deployment -
  and the GUI ticks it by itself when the project says the app embeds its assemblies.
- **The machine is checked.** `ToolLocator.Prerequisites` reports adb, dotnet-dsrouter and
  dotnet with what each is for and how to get it; `InstallAsync` installs the ones that
  have a command. The GUI asks when the service comes up and offers to install dsrouter,
  which is the prerequisite nobody remembers.
- **Service:** `/prereqs`, `/prereqs/install`, `/projects`, `/projects/candidates`,
  `/builds`, `/jobs/{id}` (`?from=` returns only new lines), `/jobs/{id}/cancel`.
- **GUI:** `uSetupDialog` rebuilt around the sources, new `uJobDialog` for jobs,
  `uMainForm.CheckPrerequisites`, `--dialog=setup` so the smoke test opens it, and the
  last solution/project remembered in the settings.
- **MCP:** `list_app_projects` does the same for an agent - sources in, package,
  symbolsDir and assemblies out; `profile_archive` / `profile_archives` keep and list a
  Get Results.
- **Keeping results (AQTime's saved result sets).** `ProfilerSession.ArchiveAsync` takes a
  snapshot and copies the database, through SQLite's backup, to
  `<session>/archives/<name>.db` with a `.json` sidecar for the display name. An archive is
  an ordinary result database: no schema change, opens with no session running, and the
  registry accepts its path as a `sessionId`. Service: `POST /sessions/{id}/archive`,
  `GET /sessions/{id}/archives`. GUI: an **Archive** button beside Snapshot, and the
  Explorer lists each session's archives as children - a double click opens one.
- **`nap serve --parent-pid <n>`** ends the service when the frontend that owns it exits;
  the GUI always passes its own pid. Without it a killed GUI leaves a service listening.

Before that: a third instrumenting mode and an automatic engine choice.

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

## The machine, 2026-08-26
The Visual Studio installer replaced .NET SDK 10.0.301 with **10.0.400** mid-session and
took the android workload down with it for a while. Both are back (workload android
36.1.69) and the solution builds on 10.0.400. Any device failure measured during that
window is worthless: three suite runs overlapped it and each failed different device
tests, all of which passed on their own afterwards.

## Picking this up on another machine
The repository carries the code, the docs and the tests. It does not carry:

- **the emulator and the installed apps.** The device tests want TestTarget installed as a
  Debug build with EnableDiagnostics=true, and one test wants the companion build without
  it (both commands under "How to run what exists"). NAP_TEST_SERIAL / NAP_TEST_PACKAGE
  override the defaults (emulator-5556, com.mcasoftware.testtarget).
- **the package** (dist/ is ignored): rebuild it with build\package.ps1.
- **the Native AOT prerequisites**: the Visual C++ build tools and Windows SDK, plus
  vswhere on PATH when publishing. docs/profiler/PACKAGING.md has the exact components and the
  installer traps.
- **the Delphi side's inputs**: RAD Studio with DevExpress and SynEdit, found through the
  IDE's own search path by gui\make-cfg.ps1.
- **the Delphi crash stacks' inputs**: the JEDI Code Library (JclDebug) on the IDE search
  path, and InsertJCLDebugInfo (C:\Athens\binaries_qbf, NAP_JCLDEBUG overrides) for
  `gui\build-gui.cmd`. Without them the GUI still builds; its stacks lose unit and line.
- **build\tools** (ignored): the weaving targets run the *published* nap-weave, so run
  `dotnet publish src/NetAndroidProfiler.Weave -c Release -o build/tools` once before a
  build-time weave, and again after changing the weaver.

## Done on 2026-08-28
- (done 2026-08-28) **Every dialog resizes.** Setup, Settings and Layouts were bsDialog with
  absolute coordinates; they are sizeable now, with anchors (wide fields take the width,
  lists take the height, buttons and the validation line keep to their edges) and
  `Constraints.Min*` so nothing can overlap. Verified by resizing to 1000x760 and by trying
  to shrink below the minimum.
- (fixed 2026-08-28) The "callspec outside the app" rule never fired: it looked for the
  callspec among the candidates by exact name, and `N:V7` is a prefix, not a name. It now
  asks what an entry *reaches*, with the same prefix rule the engine's filter uses - and on
  the reference application it refuses `N:App` with the assembly that would be missed.
- (done 2026-08-28) **A crash opens a dialog with the stack in it** (`gui/src/uCrashDialog.pas`,
  plain VCL on purpose, Copy and Open-the-log, re-entrancy guarded), not only a line in a file.
- (done 2026-08-28) **The GUI reports its crashes with a resolved call stack** (JclDebug,
  MPL-1.1, declared in the notices): startup and runtime exceptions both land in
  `NapGui.error.log` with unit and line, the hook's own frames trimmed, and `--dialog=crash`
  raises on purpose so the reporter itself can be checked. `make-cfg.ps1` now passes `-GD`
  for the detailed map, and `build-gui.cmd` runs the JCL's `InsertJCLDebugInfo` (found at
  C:\Athens\binaries_qbf, overridable with NAP_JCLDEBUG) to put the debug data **inside the
  executable**: +3 MB against a 100 MB map, so a shipped GUI resolves lines as well.
  Verified by hiding the map and crashing on purpose.

## Open, found in the field on 2026-08-27
(fixed 2026-08-28: `NapAssemblies` weaves the named libraries in the output folder and
appends them to the same map; the GUI sends its Assemblies field, and the rule now says
"name it" rather than refusing. Still to check on a device: that a reference application build-time weave
with App.Core named collects what a session then reads.)

**Build-time weaving reached only the application's own assembly.** The targets weave
`@(IntermediateAssembly)`, so a callspec covering a referenced library (the reference application: `N:V7` and
`N:App` live in App.Core, 19 MB of it) instruments nothing of what the app actually
spends its time in - 6655 methods of the Android shell were woven, they run at startup and
then the session collects a few hundred bytes and nothing more. The GUI now refuses that
combination with the reason, and on-device weaving (which does take a list of assemblies)
remains the way to instrument libraries. The real fix is to let the build weave the chosen
assemblies too: `NapExtraAssemblies` exists in the targets, nothing fills it, and the file
that ends up packaged is the copy in $(OutDir) - which msbuild re-copies from the reference
on every build, so the .naporig dance has to be right there as well.

## GUI backlog, asked for on 2026-08-26 after the first real run
In the user's order. The three defects of that list are fixed and verified; what is left
here is the shell of the window, which nobody had designed yet.

- (fixed 2026-08-27, found in the field) **A build-time weave could not say how it records.**
  The targets bake the collector's environment into the app (they must: writing an override
  file would create files/.__override__, fatal for an app with embedded assemblies) but they
  baked no `NAP_PROFILER_MODE`, so the app always wrote events - while a session asking for
  `weaver-tree` read `.napt` files that never appeared. On the reference application the snapshot simply never
  produced anything. Now: `NapMode` (tree by default) goes into the app's environment **and**
  into the map header (`#napw-map 1 mode=tree`), `nap-weave --mode` records it, and the
  session reads the map and follows the app - warning when that differs from what was asked.
  Maps without the header read as events, which is what those apps do.
- (fixed 2026-08-27, found in the field) **nap-weave re-wove a stale backup.** Build-time
  weaving took `.naporig` as the input whenever it existed, so every build after the first
  discarded the fresh compilation and instrumented code from days earlier. On the reference application the
  app shipped a App.Droid from 20 August against today's libraries and died at startup with
  `TypeLoadException: ... NuoviInterventiM ... in assembly 'App.Core'` - a class deleted in
  the meantime. The input is now chosen by whether the assembly is already woven (it
  references the collector), and an already woven assembly with no backup is refused
  instead of being woven twice. `Fast/WeaveToolBackupTests` covers it; the stale artefacts
  in the reference application obj folder were removed, and build/tools was republished - the targets run
  the **published** tool, so a fix in src/ alone would have changed nothing.
- (done 2026-08-27) The Setup dialog remembers the build options too (no fast deployment,
  instrument during the build, clear deployed assemblies), and refuses **before** Start what
  the engine used to refuse after it: a build-time weave whose `nap-weave.map` is not there
  yet. A condition a frontend can see is a frontend's to check.
- (done 2026-08-27) `gui/src/uCallspecDialog.pas`: the callspec picker - a tree of the
  app's namespaces and types with a box per node, method totals per node (own plus
  descendants), a substring filter, expand/collapse/none, and a "Per line" column whose
  choice is remembered but not yet collected. `--dialog=callspec` opens it directly.
- (done 2026-08-26) The Setup dialog asks for **one profiler** instead of a mode plus an
  engine, explains the chosen one underneath, offers **no fast deployment** and
  **instrument during the build** as explicit choices, refuses impossible combinations in
  red with Start disabled, and remembers the last session's setup so that running the same
  app with a different profiler costs one dropdown and Start.

0. **Startup takes ~7.5 s to the window** (measured; it was 9.8 s). A splash now appears
   at 1.5 s, which is what the user asked for, but the window itself is still slow and
   nobody has measured *which* part - the eight panels of DevExpress controls, the skins,
   the docking layout. Attempts to defer the work to a timer after the window is shown
   were reverted: opening a session that way left the panels unbuilt and the status bar
   stuck on "Opening ...". Deferring is the right idea and needs a real look at what the
   dock panels require before they can be filled, not another timer.
1. **Layouts as a dropdown.** The toolbar button opens a dialog; it should drop down the
   saved layouts and pick one from the list, the factory default among them (that default
   has to become a named layout like the others rather than a hidden reset).
2. **A layout loaded at startup.** `GSettings.DefaultLayout` already exists and is already
   read at startup - what is missing is the way to *say* which one, i.e. a "load this at
   startup" mark in the same dropdown.
3. **A real File menu** with New session and Open session. The window has a bar manager
   and no menu bar at all today; the two verbs live only on the toolbar.
4. (fixed 2026-08-26) The window did not appear in the taskbar.
   Also fixed in the same round, all found while checking the above:
   - **Writing into a dock panel that has no window took the application down.** The Log
     and Summary panels are dockable, so they can be closed, auto-hidden, or a background
     tab - and `Control 'PanelSummary' has no parent window` is what a write into one
     costs. It broke `--export` completely and would break any user whose layout has those
     panels closed. Both now buffer and flush when the panel is next shown.
   - A saved layout written by a crashed run opened the window with no panels at all.
     There is no way back from that in the GUI: it wants a "reset layout" that does not
     depend on the layout being usable.
   - The Explorer was empty on a machine that never set a sessions root: it now defaults
     to the engine's own (NAP_SESSIONS_ROOT, then LOCALAPPDATA), which is where sessions
     actually are.
   - The session list read every session.db to show its mode; it reads session.json now,
     where the mode is the enum's **ordinal**, not its name.
5. (fixed 2026-08-26) The device list showed bare serials.
6. (fixed 2026-08-26) The Setup dialog's hint painted over the fields below it.

Two observations from fixing those, worth keeping:
- Opening the Setup dialog takes ~9 s before the window appears, and that is *before* any
  solution is read: it is the GUI's own startup plus starting `nap serve`, `/prereqs` and
  `/devices` (three devices, one of them a phone over USB). Worth attacking with the same
  medicine as the scan - show the window, fill it in afterwards.
- The dialog is built in code with absolute coordinates, so every row moves by hand. If it
  grows again it wants a layout control (`TdxLayoutControl`) rather than more arithmetic.

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
src/NetAndroidProfiler.Core/Projects/{AppProjectFinder.cs, AppBuilder.cs};
src/NetAndroidProfiler.Core/Devices/{ToolLocator.cs, ProcessRunner.cs};
src/NetAndroidProfiler.Cli/{ControlService.cs, JobRegistry.cs};
gui/src/{uSetupDialog.pas, uJobDialog.pas, uControlClient.pas, uMainForm.pas};
tests/NetAndroidProfiler.Tests/Fast/{AppProjectFinderTests.cs, AppBuilderTests.cs, ControlServiceTests.cs}.

## Done 2026-09-09, built and green, not yet committed: the GUI as a tool somebody uses

One block, from the user's own testing of the GUI. Fast pass 156 passed / 0 failed
(24 device-library + 132 profiler); `gui\build-gui.cmd` clean; headless `--render=explorer:`
shows the grouped tree and writes no layout file.

- **Toolbars cannot float or be dragged** (`BarManager.NotDocking` = every style; per-bar
  AllowClose / AllowCustomizing / AllowQuickCustomizing off). Permanent user rule, in the
  agent's memory. Saved layouts therefore hold panels only - `uLayouts` no longer touches
  the bar manager.
- **Layouts menu** (TdxBarSubItem rebuilt on popup) with the saved layouts, the default
  marked, Save as, "Open the window with" (ticked list), Manage, and a **Back to the
  built-in arrangement** that re-docks the panels *now* (`ApplyBuiltInLayout` /
  `ApplyBuiltInSizes`), not at the next start.
- **A window never shown does not save its arrangement** (`DoShow` -> `FWasShown`): that was
  the empty main window.
- **File menu** (New Ctrl+N, Open Ctrl+O, Add a sessions folder, Exit); Theme combo dropped
  from the toolbar; **Record** button for sessions started paused.
- **Sessions as documents**: `Name`, `ProjectPath`, `SolutionPath` in the spec; a session can
  be kept in another folder (`sessionsRoot`), and those folders are browsed by the Explorer
  (`[SessionFolders]` in the settings); rename/delete in Core, over `POST /sessions/rename`
  and `/sessions/delete`, as MCP tools, and in the Explorer's context menu; the Explorer
  groups by solution.
- **StartPaused** (AQTime's start-with-profiling-disabled): new state `WaitingToRecord`,
  `StartRecordingAsync`, `ResumeAsync` doubles as "begin". Weaver waits with the device
  control file at `pause`; EventPipe simply opens no session yet.
- **Symbols found rather than demanded** (symbolsDir -> project output -> weaver reference
  dir), a warning when a session has none, and **snapshots now resolve source locations**
  (pdbs read once per session). That was the "no source location" the user hit on a running
  the reference application session that had its symbols all along; the Source panel's message now tells the
  three cases apart.

Next: commit this block; then the device test that is still missing - record on demand end
to end (start paused, drive the app, record, assert the results begin where recording did).

## Done 2026-09-08 evening, the layout menu and where the weaver is
Both built and green (`gui\build-gui.cmd`; `dotnet build NetAndroidProfiler.slnx`;
fast pass 144 passed, 0 failed). A headless `--render=report:` run writes no
`NapGui.layout.ini` any more, which is the bug below:

1. `gui/src/uMainForm.pas` - Layouts is now a `TdxBarSubItem` menu (`BuildLayoutMenu` /
   `UpdateLayoutMenu`, rebuilt on `OnPopup`): the saved layouts, the default marked
   `[default]`, then Save as / Open the window with (ticked list) / Manage / Back to the
   built-in arrangement. Same shape as CVSTreeGraph's `MainFormU.BuildLayoutMenu`.
   `FLayoutButton` is gone; `ResetLayoutClick` became `LayoutBuiltInClick`.
   In the same change: `DoShow` sets `FWasShown`, and the destructor saves the
   arrangement only when the window was actually on screen - that is the bug that left
   `gui/NapGui.layout.ini` with `ChildCount=0` and opened an empty main window, written
   by a `--render` / `--export` run; that stale ini has been deleted.
2. `ToolLocator.FindWeaveTool` / `FindCollectorAssembly` (+ `AppBuilder` passing
   `-p:NapWeaveTool=` and `-p:NapCollectorAssembly=`, + the new test
   `AppBuilderTests.The_build_is_told_where_the_weaver_and_the_collector_are`), so an
   instrumenting build from a source tree finds the weaver without a packaging step.

Next: open the menu by hand once (the only path not exercised by a build is the
`OnPopup` rebuild, which frees the previous popup's items), then commit both.

## Next action if interrupted right now
Free port 9000 (stop the `sal-minio` container), then verify the example app's screens
against the profiler as listed under "Current task". A Redmi Note 8 Pro (arm64, API 30) is
attached: U10 can start as soon as that is done.

## How to run what exists
    dotnet build NetAndroidProfiler.slnx
    dotnet test  NetAndroidProfiler.slnx
    powershell -File build\package.ps1          the redistributable zip, into dist\
    dotnet publish src\NetAndroidProfiler.Cli -c Release -r win-x64 -p:NapAot=true
                                                native nap (needs vswhere on PATH)
    gui\build-gui.cmd                           builds NapGui.exe
    gui\tests\smoke.ps1 -Sessions @(...)        every panel and dialog of the real window
    gui\ControlTests.exe <nap.exe> [serial]     the GUI's control path, end to end
        (its first block - prerequisites, the projects under TestTarget, the callspec
        candidates - needs no device and runs anywhere the repository is)

The companion build the prerequisite test wants (clean obj/ and bin/ of TestTarget/Profiler before
switching ApplicationId in either direction: the SDK's incremental state keeps the previous manifest,
the install step then reports XA0132 "package was not installed", and an APK installed by hand from
that state lands on the incremental FS, where the inspector's `adb pull` is refused - seen 2026-09-06):
    dotnet build TestTarget/Profiler/TestTarget.csproj -c Debug -p:EnableDiagnostics=false
        -p:ApplicationId=com.mcasoftware.testtarget.nodiag -t:Install -p:AdbTarget="-s emulator-5554"

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
- The working tree is mixed CRLF/LF. Edit in place (the tools preserve what a file
  has); a `sed`/`Write` that normalises line endings makes a diff unreadable.
- DevExpress publishes `TextHint` on the editor, not on its `Properties`, and an
  editable `TcxComboBox` fires `OnChange` on every keystroke - do not hang a service
  call on it.
- `TcxTreeListNode.SetCheckState` **already** cascades a tick down to the children and
  recomputes the parent from them, and raises `OnNodeCheckChanged` for **every** node it
  touches. Propagating in that handler as well is not belt and braces, it is a fight: a
  ticked child made the parent recompute, which cascaded back down and undid the tick
  (children looked inert), and two branches at once turned it into a storm. Read the
  component's source before adding behaviour to it - the answer was thirty lines of cxTL.pas.
- Anything expensive in `OnNodeCheckChanged` runs hundreds of times per click, because of
  that cascade. The summary is debounced on a timer for exactly this reason.
- The tree is the state; a dictionary kept in step with it by events is not. Reading the
  selection back from the nodes removed a whole class of drift (the summary said "1 chosen"
  while two were ticked), and the dictionary now only seeds a rebuild after filtering.
- In `TcxTreeList` a node shows its box because its **parent** is a check group. Making
  the node itself one turns it into a container whose state is derived from its children,
  so a leaf could not be ticked at all - and a container cannot be ticked directly either:
  restoring a chosen namespace means ticking what is under it.
- A `TcxLabel` with `WordWrap` still has `AutoSize` on: it grows straight over whatever is
  below it. And it eats `&` as an accelerator - `Properties.ShowAccelChar := False` when
  the caption is text rather than a label.
- A VCL form that builds its whole window inside its constructor has a handle before
  `Application.CreateForm` names it the main form, so it is created without
  `WS_EX_APPWINDOW` and never reaches the taskbar. Override `CreateParams`.
- Anything slow between a click and a window - listing devices, reading a solution - must
  happen after the window is on screen, or the application looks hung. The Setup dialog
  defers its scan to a one-shot timer and reads the app's assemblies only when the
  callspec list is dropped down.
- PowerShell `Add-Type` does not survive between tool calls (each is a new process), and a
  `GetWindowText` import without `CharSet.Unicode` silently returns the first letter only:
  both cost a wrong conclusion about the GUI here.
- The device suite does not tolerate company: two of its tests failed while the GUI
  control checks were driving the other emulator, and both passed on their own. Run
  the suite with nothing else touching a device before believing a device failure.
