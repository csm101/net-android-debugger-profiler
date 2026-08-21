# Project state

High-level permanent state. Transient task state lives in TASK_RESUME.md.

## What this is

Profiler for .NET for Android apps (MonoVM): CPU sampling, memory/allocation
analysis, instrumenting (runtime provider now, IL weaving later). Frontends:
MCP server first, then a rich Delphi + DevExpress VCL GUI (AQTime as the
reference bar). Free replacement for the VS Enterprise Android profiler.

Sibling of C:\GitHub\net-android-debugger - same architecture family
(frontend-neutral core, thin frontends, living docs, TDD with integration
tests). Ultimate real target: the reference application (C:\Work\ReferenceApp, net9.0-android35.0).

## Decisions taken (2026-08-20)

- Collection rides the official diagnostics stack (EnableDiagnostics,
  dotnet-dsrouter/dotnet-trace/dotnet-gcdump, EventPipe): we build
  orchestration, analysis, MCP and GUI - not collection.
- Parsing via TraceEvent (NuGet, maintained) - nothing vendored.
- Instrumenting: plan A = Microsoft-DotNETRuntimeMonoProfiler callspec
  (cheap, MonoVM-only, experimental); plan B = IL weaving with Mono.Cecil
  (deterministic, runtime-independent, survives CoreCLR). Native
  mono_profiler API module rejected (redundant).
- Analysis output = SQLite per session, versioned schema = the GUI contract.
  Delphi GUI reads SQLite directly + drives sessions via a small local
  control service.
- Tooling TFM: net10.0. VS-profiler code/binaries excluded (license); MIT
  references only.
- AndroidCollector duplicated from the debugger's AndroidLauncher at first;
  shared library extraction deferred (U11).

- P3 decision (2026-08-20, user): after U20 (runtime instrumenting fails on
  the real app), P3 goes plan B first: **IL weaving with Mono.Cecil - no
  Metalama dependency** (user's explicit choice). P2 memory milestone stays
  queued behind it.
- P1 design decisions (2026-08-20, user):
  - **The profiler never rebuilds the target app.** It profiles the APK as
    the user built it (Debug included - the main use case; the reference application already
    builds with RunAOTCompilation=false). It checks prerequisites on the
    installed/selected APK (diagnostics component present, JIT vs AOT,
    baked environment) and fails with a message explaining how to configure
    the build (EnableDiagnostics, MONO_DIAGNOSTICS env file for
    instrumenting); AOT code only triggers a warning (leaf attribution).
  - Debug builds (the main case) need only EnableDiagnostics=true: the
    engine injects MONO_DIAGNOSTICS / DiagnosticPorts per session through
    the runtime's override environment file (run-as), no rebuild, no
    permanent cost. Release builds need the baked env file for
    instrumenting (docs/APP_SETUP.md).
  - SQLite: separate tables per profiling kind (sampling vs instrumenting
    vs memory), never mixed units in one table.
  - Source-line annotation reuses the pdb approach of the reference application's
    Desymbolicate tool (Microsoft.DiaSymReader + portable pdb, method token
    + IL offset -> sequence points, pdbs fetched per build from the
    company symbol server): C:\Work\ReferenceApp\ExternalTools\Desymbolicate.
- Licensing (2026-08-20): proprietary closed source, copyright MCA Software
  s.a.s. di Sirna Carlo & C.; commercialization kept open; dependency policy
  MIT/BSD/Apache-2.0 only, no GPL.
- THIRD-PARTY-NOTICES.txt (2026-08-21): written and test-guarded. Covers the
  real dependency closure of the shipped build (TraceEvent, DiagnosticsClient,
  Microsoft.Data.Sqlite + SQLitePCLRaw, Mono.Cecil, Microsoft.Extensions.*,
  ModelContextProtocol) with full MIT and Apache-2.0 texts; no GPL/LGPL anywhere
  in the closure. Fast/ThirdPartyNoticesTests reads the .deps.json of
  NetAndroidProfiler.Mcp and nap-weave, so an unacknowledged new dependency fails
  the suite. Test-only packages (xUnit, coverlet) and externally invoked tools
  (adb, dotnet-dsrouter) are excluded, and the exclusion is stated in the file.

## Architecture status (2026-08-20)

- Core (net10.0): Devices (AdbClient), Apps (AppInspector), Collection
  (override-environment injection, DsRouterProcess, EventPipeCollector on
  Microsoft.Diagnostics.NETCore.Client), Analysis (SamplingAnalyzer,
  MonoProfilerAnalyzer), Store (ResultStore, SQLite schema v1),
  Sessions (ProfilerSession). Works end-to-end on emulator-5556 with
  TestTarget (Debug build + EnableDiagnostics) for Sampling (Restart/Attach),
  Instrumenting (Restart, callspec, allocations), HeapSnapshot (Attach or
  Restart with warm-up).
- Mcp: stdio server over ProfilerSession, tool set below (frozen for P1).
  register-mcp.cmd publishes and registers it in Claude Code.
- Weaver instrumenting (P3 plan B) works end-to-end on device **and on the
  real target**: Cecil weaves the app assemblies in the fast-deployment
  directory, the collector writes .napw files, WeaveAnalyzer -> timing_*
  tables; the net9 provider bug (U20) is bypassed. First the reference application timings:
  AppApplication..ctor 8.78 s, OnCreate 4.54 s / 4.02 s self. App-side
  prerequisites (both detected and reported by the engine):
  EmbedAssembliesIntoApk=false and no stale .pdb next to the woven assembly
  (the deployer moves it aside during the session).
- Tests: 23 fast (recorded traces + pdb) + 1 TODO-RED (U13) + 6 device
  (Category=Device).
- Spike assets: TestTarget/, DevTools/NetTraceProbe, tests/.../recorded/.
- Not yet: Release-build weaving, physical devices, Delphi GUI.

## Milestones

- P0 - Spike: DONE 2026-08-20. Whole chain proven on emulator: sampling
  (collect -> nettrace -> TraceEvent -> top-N with Busy visible), gcdump
  (AllocHeavyRecord visible), MonoProfiler provider (enter/leave + every
  allocation, callspec filtering, manual decode). Details in
  ANDROID_PROFILING_NOTES.md; new unknowns U13-U17.
- P1 - Core + MCP sampling: ProfilerSession, AndroidCollector, TraceAnalyzer,
  ResultStore (SQLite schema v1); MCP tools (profile_run, profile_hotspots,
  profile_flat, profile_report, profile_annotate_source, list_devices,
  profile_sessions); integration tests on emulator + fast tests on recorded
  traces.
- P2 - Memory: DONE for the core scenarios - exact allocation events per type
  and per allocating frame (runtime provider), live-heap snapshots, multiple
  snapshots per session with a growth diff, MCP tools alloc_report /
  heap_report / heap_diff. Open: type names for objects whose type was loaded
  before the session (U13), allocation data from the weaver engine.
- P3 - Instrumenting: plan A (callspec) works on net10 targets but crashes
  net9 runtimes (U20), so plan B is the path on the real app: Mono.Cecil
  weaver + on-device collector, working on TestTarget and the reference application, with
  build-time weaving for apps that embed their assemblies (U22, verified on
  the reference application in its shipped configuration). The weaver also records allocations
  by type and allocating method, and instruments async state machines by
  default ("<method> (async body)": calls are resumptions, time excludes the
  awaits) and iterators ("<method> (iterator body)": one call per item produced) -
  verified on the reference application (net9) and TestTarget (net10).
- P4 - Delphi GUI (gui/): DevExpress VCL, call tree (cxTreeList), hot lists
  (cxGrid), allocations (PivotGrid), timeline (chart); reads SQLite, drives
  Core via local control service.
- P5 - Packaging/registration (register-mcp pattern from the debugger
  project).

## MCP tool surface (P1, implemented)

list_devices, check_app, profile_run (one shot; mode sampling | instrumenting
| heap; launch restart | attach), profile_start / profile_stop /
profile_status (long sessions), profile_sessions, profile_hotspots,
profile_flat, profile_tree, profile_callers / profile_callees,
profile_timings, alloc_report, heap_report, profile_threads, profile_report,
profile_annotate_source, heap_diff, get_app_output. profile_run/profile_start take
engine=provider|weaver and weaveAssemblies for P3 weaving. Memory sessions take N snapshots
(snapshots/snapshotIntervalSeconds) so heap_diff can hunt leaks.

## Stable commands

    dotnet build C:\GitHub\net-android-profiler\NetAndroidProfiler.slnx
    dotnet test  C:\GitHub\net-android-profiler\NetAndroidProfiler.slnx

## Important discoveries

(collection facts live in ANDROID_PROFILING_NOTES.md; these are the ones that
shape the architecture)

- TraceEvent has no parser for Microsoft-DotNETRuntimeMonoProfiler: Core
  owns a hand-written decoder (manifest layouts recorded in the notes).
- Instrumentation is JIT-time and AOT-free: instrumenting sessions need
  `RunAOTCompilation=false` builds + `suspend` for the first session; the
  default profiled-AOT Release build also hides leaf frames in sampling.
- MonoProfiler allocation tracking is exact (every object, correct size);
  heap snapshots come from Core's own GC heap-dump EventPipe session
  (per-type live objects; MonoVM signals no dump end -> quiescence).
- Env var injection: Debug runtime = override environment file (run-as, no
  rebuild); Release = baked environment file at build time (user side;
  incremental builds after an env change can produce a broken APK - clean
  obj/ bin/).
- dsrouter is the tool-side process; Core drives EventPipe through
  DiagnosticsClient on it; per-device isolation = one dsrouter port per
  device. Attach to a running Debug app works via adb reverse.
- MonoVM sampling has no IL offsets and loses small leaf frames (U15):
  hotspots are method-level, source annotation is per method.
