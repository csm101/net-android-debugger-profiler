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
  MIT/BSD/Apache-2.0 only, no GPL; THIRD-PARTY-NOTICES required at first
  distributed release.

## Architecture status

Solution scaffold (Core, Mcp, Tests - net10.0), no engine code yet. Spike
assets outside the solution: TestTarget/ (net10.0-android validation app),
DevTools/NetTraceProbe (TraceEvent probe incl. manual MonoProfiler decoder),
tests/NetAndroidProfiler.Tests/recorded/ (sampling + monoprofiler .nettrace,
.gcdump). See ARCHITECTURE.md.

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
- P2 - Memory: gcdump + alloc events -> per-type/per-callsite reports; MCP
  memory tools.
- P3 - Instrumenting: plan A callspec sessions end-to-end; then plan B weaver
  (Cecil, async-aware) + on-device collector lib + APK strumentation build
  step.
- P4 - Delphi GUI (gui/): DevExpress VCL, call tree (cxTreeList), hot lists
  (cxGrid), allocations (PivotGrid), timeline (chart); reads SQLite, drives
  Core via local control service.
- P5 - Packaging/registration (register-mcp pattern from the debugger
  project).

## Target MCP tool surface

Modeled on oracle-profiler-mcp plus Android specifics:
profile_run (build+deploy+collect one shot), profile_start / profile_stop
(long sessions), profile_status, profile_sessions, profile_hotspots,
profile_flat, profile_report, profile_annotate_source, profile_callers /
profile_callees, memory_snapshot (gcdump), alloc_report, list_devices,
get_app_output (logcat). Exact set frozen in P1.

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
  gcdump gives live-heap by type but unreliable array sizes on Mono.
- Env var injection (MONO_DIAGNOSTICS) needs a build with a baked
  environment file; incremental builds after an env change can produce a
  broken APK -> engine clean-builds on env change.
- dsrouter is the tool-side process; per-device isolation = one dsrouter
  port per device.
