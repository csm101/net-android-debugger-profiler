# Architecture

Living specification. Source of truth is the code; if this document and the
code disagree, the code wins. Update whenever a module's responsibilities or
external contract changes.

## High-level wiring

    MCP client (Claude Code) -- MCP/stdio --> NetAndroidProfiler.Mcp ------+
    Delphi + DevExpress GUI (gui/, future) -- SQLite (read) + local -------+
                                              control service              |
                                                                           v
                                ProfilerSession  (NetAndroidProfiler.Core)
                                orchestration + analysis, frontend-neutral
                                      |                        |
                        collection pipeline           analysis pipeline
                        (build/deploy with            (TraceEvent parse ->
                         EnableDiagnostics,            call tree, hot paths,
                         dsrouter + dotnet-trace       allocations -> SQLite)
                         / gcdump, MONO_DIAGNOSTICS,
                         adb, logcat)
                                      |
                                      v
                        MonoVM EventPipe agent inside the
                        Android app process (net9.0-android)

Profiling modes:
1. **Sampling** - dotnet-trace default cpu-sampling profile. No app rebuild
   beyond EnableDiagnostics=true. Works on Release builds.
2. **Runtime instrumenting** - EventPipe provider
   Microsoft-DotNETRuntimeMonoProfiler (enter/leave via callspec, allocations
   with stacks, GC events, heap dumps). Experimental upstream; MonoVM-only.
3. **IL-weaving instrumenting** (plan B / precision mode) - post-compile
   Mono.Cecil rewriting injecting Profiler.Enter/Leave into selected methods;
   runtime-independent (survives a future CoreCLR-on-Android switch).
   Includes a small on-device collector library shipped with the
   instrumented APK.

## Design rules

- ProfilerSession is the single facade all frontends consume: owns the
  collection lifecycle (state machine
  Idle -> Deploying -> Collecting -> Analyzing -> Ready / Failed), the trace
  store, and the analysis results.
- **SQLite is the GUI contract.** One database per profiling session, schema
  versioned (schema_info table). The Delphi GUI opens it read-only; the MCP
  frontend queries through Core. Document the schema here as it stabilizes.
- Frontends are thin translators; shared logic belongs in Core.
- MCP tool surface mirrors the proven oracle-profiler-mcp server
  (profile_run / hotspots / flat / annotate_source / report / sessions), plus
  Android-specific tools. Target list in PROJECT_STATE.md.

## Modules (Core implemented in P1; Mcp next)

| Module | Responsibility |
|---|---|
| Core/Sessions/ProfilerSession | Facade: `SessionSpec` -> Preparing (device + APK prerequisites, dsrouter, app config) -> WaitingForApp -> Collecting -> Analyzing -> Ready/Failed; session directory with session.json, trace.nettrace, session.db, session.log; `Results` = ResultStore |
| Core/Devices/AdbClient, ProcessRunner, ToolLocator | serial-explicit adb (shell, exec-out, push/pull, run-as, setprop, launch, pidof, reverse, logcat); tool discovery |
| Core/Apps/AppInspector | pulls the installed APK(s), reports `AppPrerequisites` (diagnostics component, AOT libs, debuggable, baked MONO_DIAGNOSTICS) and `Check(mode)` -> blocking problems / warnings with guidance |
| Core/Collection/AppEnvironment, EnvironmentOverrideFile | per-app DOTNET_DiagnosticPorts / MONO_DIAGNOSTICS injection through the Debug runtime's override environment file (backup + restore); `debug.mono.profile` fallback for release apps |
| Core/Collection/DsRouterProcess, EventPipeCollector | dotnet-dsrouter lifecycle; EventPipe sessions via DiagnosticsClient (sampling / instrumenting to file, live heap snapshot) with start retry and resume |
| Core/Analysis/SamplingAnalyzer, WaitFrameClassifier | TraceLog stacks -> method stats (incl/excl, *_cpu), aggregated call tree, caller/callee edges |
| Core/Analysis/MonoProfilerAnalyzer | manual decoder of the MonoProfiler provider -> timings, timing tree, allocations by type / by innermost instrumented frame |
| Core/Store/ResultStore, ResultSchema | SQLite writer/reader, schema v1 (below) |
| Core/Weaving (P3, in progress) | CecilWeaver (Mono.Cecil): Enter/try/finally/Leave injection into filtered methods (WeaveFilter, callspec-like grammar), id-map sidecar; WeaveAnalyzer parses the collector's .napw event files into the same InstrumentingResult as the runtime provider. Async state machines skipped in v1 (U8) |
| Collector (NetAndroidProfiler.Collector, netstandard2.0, zero deps) | runtime target of woven calls; disabled unless NAP_PROFILER_OUT is set; per-thread .napw event files, 1 s flush |
| Mcp/ | stdio MCP server over ProfilerSession (P1, next) |
| gui/ (P4) | Delphi + DevExpress VCL frontend (AQTime-style), reads SQLite, drives sessions via local control service |

## Instrumenting engines

Two engines produce the same `InstrumentingResult` and therefore the same
`timing_*` tables, so frontends never branch on the engine:

| | Runtime provider | Weaver (Mono.Cecil) |
|---|---|---|
| Mechanism | `Microsoft-DotNETRuntimeMonoProfiler` callspec, events over EventPipe | `Profiler.Enter/Leave` injected into the selected methods, events written to files by the app |
| Requirements | MonoVM, `MONO_DIAGNOSTICS` in the app environment, JIT (AOT methods are never instrumented) | the woven assemblies must be the ones the app loads |
| Availability | broken on .NET 9 runtimes (KNOWN_UNKNOWNS U20) | works on any runtime, including .NET 9 |
| Allocations | exact, with type, size and allocating frame | counts by type and allocating frame (newobj/newarr in woven methods report their type; no sizes) |
| Where the rewrite happens | nowhere (runtime decides at JIT time) | on the device (fast-deployment copies) or during the build (`nap-weave`) |

Weaver data path: `CecilWeaver` wraps each selected method body in
`Profiler.Enter(id); try { ... } finally { Profiler.Leave(id); }` and emits an
id map; `NetAndroidProfiler.Collector` (netstandard2.0, no dependencies) writes
one `.napw` file per thread into `NAP_PROFILER_OUT`; `WeaveAnalyzer` turns those
files plus the map into timings and a timing tree.

`.napw` format: header `"NAPW"`, version byte, `i64` Stopwatch frequency,
`i32` pid (0), `i32` managed thread id; then fixed 13-byte records of
`u8 kind` (1 enter, 2 leave, 3 exception leave, 4 allocation), `i32 id` (method id,
or type id for allocations, resolved through the `nap-types.txt` sidecar the
collector writes), `i64` Stopwatch ticks. Buffers are flushed every second and on process exit, so a killed process
loses at most one second of events.

Two deployment shapes:
- **on device** (`WeaveDeployer`): pulls the assemblies from
  `files/.__override__/<abi>/`, weaves them, pushes the woven copies back with
  backups, moves the stale `.pdb` aside, injects the environment, and restores
  everything at the end. Requires a fast-deployment build.
- **at build time** (`nap-weave` + `build/NetAndroidProfiler.Weaving.targets`):
  the build rewrites the assembly before packaging, bakes the collector's
  output directory into the app environment and writes `nap-weave.map`; the
  session consumes the map (`SessionSpec.WeaveMapPath`), injects nothing and
  only launches the app and reads its results. This is the path for apps that
  embed their assemblies - injecting an override environment file would stop
  such an app from starting at all.

## SQLite schema contract

Source of truth: `src/NetAndroidProfiler.Core/Store/ResultSchema.cs`
(`ResultSchema.Version`, `CreateScript`). One database per session
(`<sessionsRoot>/<sessionId>/session.db`, WAL mode). Readers check
`schema_info.version` and refuse unknown versions.

### Version 1 (2026-08-20)

Design rule: **separate tables per profiling kind**, never mixed units in
one table. Shared dictionaries are `method`, `thread`, `type`.

| table | purpose / units |
|---|---|
| `schema_info(version, created_utc, tool_version)` | one row |
| `session(id, mode, state, package, device_serial, started_utc, duration_ms, trace_file, total_samples, samples_with_stack, spec_json, error)` | one row; `mode` = Sampling / Instrumenting / Memory |
| `method(id, module, namespace, type_name, name, signature, full_name, runtime_method_id, is_wait_frame, token)` | ids local to the database; `runtime_method_id` = MonoVM MethodID for cross-referencing; `is_wait_frame` = leaf denotes blocked time (Sleep/Wait/Monitor PInvoke leaves, `WaitFrameClassifier`); `token` = metadata token (0x06xxxxxx) used with the module's portable pdb for source ranges (`Symbols/PortablePdbSymbols`; MonoVM samples carry no IL offsets, so annotation is per method) |
| `thread(id, os_tid, name, samples, first_ms, last_ms)` | |
| `type(id, name, vtable_id, class_id)` | allocation/heap types; name may be `<vtable 0x...>` when unresolved (U13) |
| `sample_stat(method_id, inclusive, exclusive, inclusive_cpu, exclusive_cpu)` | **samples**; `*_cpu` exclude samples whose leaf is a wait frame |
| `sample_tree(id, parent_id, method_id, thread_id, depth, inclusive, exclusive, inclusive_cpu, exclusive_cpu)` | aggregated call tree, one root per thread with `method_id = -1` |
| `sample_edge(caller_method_id, callee_method_id, samples)` | caller/callee pairs |
| `timing_stat(method_id, calls, total_ns, self_ns, min_ns, max_ns, exception_leaves)` | **nanoseconds / calls** from enter/leave |
| `timing_tree(id, parent_id, method_id, thread_id, depth, calls, total_ns, self_ns)` | aggregated instrumented call tree (instrumented frames only), one root per thread |
| `alloc_by_type(type_id, count, bytes)` | exact allocation events |
| `alloc_by_site(type_id, method_id, count, bytes)` | innermost open instrumented frame on the allocating thread; `method_id = -1` = none |
| `heap_snapshot(id, taken_utc, total_objects, total_bytes, file)` | live-heap snapshots |
| `heap_by_type(snapshot_id, type_id, count, bytes)` | |

Indexes: `method(full_name)`, `sample_tree(parent_id)`, `sample_tree(method_id)`,
`sample_edge(callee_method_id)`, `timing_tree(parent_id)`, `timing_tree(method_id)`.

Scale (U6, measured): a synthetic session with 5,000 methods and 200,000 tree
nodes is written and then queried - hotspots, thread roots, expanding a node,
callers - in about a second in total on this machine, with every query under
half a second. The adjacency-list call tree with its `parent_id` index is
therefore adequate for the GUI; no nested-set or materialized-path rewrite is
needed. Guarded by `ResultStoreScaleTests.Large_call_tree_stays_queryable`.

Core reader API over the same tables: `ResultStore.Hotspots / SampleTreeChildren /
Callers / Callees / Timings / TimingTreeChildren / AllocationsByType /
AllocationsBySite / HeapByType / Threads / FindMethodId`. The GUI reads the
tables directly with equivalent queries.

## Shared code with net-android-debugger

AndroidCollector overlaps the debugger's AndroidLauncher (device listing,
deploy, adb orchestration, logcat). Duplicate first, extract a shared library
when both stabilize - tracked in KNOWN_UNKNOWNS (U11).
