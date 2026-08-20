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

(to be defined in P1 - record every table/column here; version 1 starts when
the first GUI or external consumer reads it)

## Shared code with net-android-debugger

AndroidCollector overlaps the debugger's AndroidLauncher (device listing,
deploy, adb orchestration, logcat). Duplicate first, extract a shared library
when both stabilize - tracked in KNOWN_UNKNOWNS (U11).
