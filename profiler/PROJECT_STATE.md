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

## Architecture status

Solution scaffold only (Core, Mcp, Tests - net10.0). No engine code yet.
See ARCHITECTURE.md.

## Milestones

- P0 - Spike (current): install diagnostic tools; TestTarget with
  EnableDiagnostics; sampling round-trip on emulator (collect -> nettrace ->
  TraceEvent parse -> top-N methods printed); gcdump round-trip; MonoProfiler
  provider probe (enable + callspec + observe events). Resolves U1-U4.
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

(record here as they land; collection facts live in ANDROID_PROFILING_NOTES.md)
