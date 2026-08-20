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

## Modules (planned - nothing implemented yet)

| Module | Responsibility |
|---|---|
| Core/ProfilerSession | Facade, state machine, session store |
| Core/AndroidCollector | Build/deploy (EnableDiagnostics), dsrouter + dotnet-trace/gcdump processes, adb, MONO_DIAGNOSTICS setup, logcat |
| Core/TraceAnalyzer | TraceEvent parsing -> call tree, inclusive/exclusive times, hot paths |
| Core/AllocAnalyzer | Allocation events + gcdump -> per-type / per-callsite reports |
| Core/ResultStore | SQLite writer/reader, schema versioning |
| Core/Weaver (P3) | Mono.Cecil IL rewriting, async-state-machine aware; Profiler.RuntimeLib on-device collector |
| Mcp/ | stdio MCP server over ProfilerSession |
| gui/ (P4) | Delphi + DevExpress VCL frontend (AQTime-style), reads SQLite, drives sessions via local control service |

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
| `method(id, module, namespace, type_name, name, signature, full_name, runtime_method_id, is_wait_frame)` | ids local to the database; `runtime_method_id` = MonoVM MethodID for cross-referencing; `is_wait_frame` = leaf denotes blocked time (Sleep/Wait/Monitor PInvoke leaves, `WaitFrameClassifier`) |
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

Core reader API over the same tables: `ResultStore.Hotspots / SampleTreeChildren /
Callers / Callees / Timings / TimingTreeChildren / AllocationsByType /
AllocationsBySite / HeapByType / Threads / FindMethodId`. The GUI reads the
tables directly with equivalent queries.

## Shared code with net-android-debugger

AndroidCollector overlaps the debugger's AndroidLauncher (device listing,
deploy, adb orchestration, logcat). Duplicate first, extract a shared library
when both stabilize - tracked in KNOWN_UNKNOWNS (U11).
