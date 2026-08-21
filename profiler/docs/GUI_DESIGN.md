# Delphi GUI (P4) - design

The GUI is a frontend beside the MCP server, not a viewer of its output: both
drive the same Core. It reads `session.db` directly (FireDAC) and controls
sessions over the local `nap serve` channel (KNOWN_UNKNOWNS U7).

The model is AQTime, restricted to the two profilers we implement - sampling and
instrumenting - plus the memory views we already produce. Everything else AQTime
does (coverage, static analysis, native allocation, disassembler, PE reader) is
out of scope.

## What AQTime does, and what we take from it

Established from the official documentation (SmartBear, August 2026):

| AQTime | what it is | our equivalent |
|---|---|---|
| Setup / Explorer panel | choose modules, classes and routines to profile ("areas"), before running | session setup: device, package, mode, engine, callspec / weave filter |
| Report panel | one row per routine, per category (Routines / Source Files / Modules), grouped by thread with an "All threads" group | main grid over `sample_stat` or `timing_stat`, joined to `method` and `thread` |
| Details panel | pages for the selected routine: **Calls** (Parents and Children tables) and **Lines** (per-line results) | Callers/Callees from `sample_edge`, per-line from the pdb annotation we already have |
| Call Tree panel | hierarchical call stack | `sample_tree` / `timing_tree` |
| Call Graph panel | boxes per routine with results, arrows for calls | same trees, drawn as a graph |
| Editor panel | source with per-line results next to the code | annotated source view |
| Summary panel | one page describing the whole run | session summary from `session` |
| Monitor panel | live counters while the app runs | live session state: events/s, trace size, state |
| Result views (`.qtview`) | saved column layout + filter per profiler, as a preset question | saved grid layouts (cxGrid persists its own) |
| Run > **Get Results** | produce results from what is collected so far, keep running | `snapshot` |
| Run > **Clear Results** | throw away what was collected, keep running | `clear` |
| Actions: **Enable / Disable Profiling** | toggle collection for all threads, optionally bound to entering/leaving a routine | `pause` / `resume` (routine-bound triggers: later, weaver only) |

Columns AQTime shows, which set the expectation for ours:

- Sampling: **Sample Count**, **Time**, **% Samples**.
- Performance (instrumenting): **Hit Count**, **Time**, **Time with Children**,
  **% Time**, **% with Children**.

## Metric mapping (ours)

Sampling - from `sample_stat`, counts are samples (~1 ms), never milliseconds:

| column | source | note |
|---|---|---|
| Samples (exclusive) | `sample_stat.exclusive` | includes the method's trivial callees: the MonoVM sampler does not report tiny leaf methods |
| Samples (inclusive) | `sample_stat.inclusive` | |
| CPU samples | `*_cpu` columns | excludes threads blocked in Sleep/Wait |
| % of profiled code | computed | |
| Module, Thread | `method`, `thread` | thread grouping plus an "All threads" root, like AQTime |

Instrumenting - from `timing_stat`, real nanoseconds:

| column | source |
|---|---|
| Calls | `calls` |
| Self time | `self_ns` |
| Total time (with children) | `total_ns` |
| Avg / Min / Max | `avg_ns`, `min_ns`, `max_ns` |
| Exception exits | `exception_leaves` |

Async and iterator bodies appear as their own rows (`<method> (async body)`,
`(iterator body)`): calls are resumptions and the time excludes what the method
was suspended on. The GUI must show that in a tooltip - it is the single most
confusing number in an instrumenting profile.

Memory - `alloc_by_type`, `alloc_by_site`, `heap_snapshot`, `heap_by_type`:
allocations per type and per allocating method, live heap per type, and the
growth diff between two snapshots. Rows whose type predates the session carry the
"loaded before the session" label (U13) and must not be hidden: their counts are
exact.

## Shell

DevExpress VCL, sources under `C:\Athens\DevExpress` (`DOCS/` holds per-library
notes written for agents; `Demos/VCL/<component>` is the first place to look).

- **ExpressBars** ribbon or toolbar for the run controls, **ExpressDockingLibrary**
  for the panel layout (AQTime's docked panels are the interaction model).
- **ExpressQuantumGrid** for Report, Details, allocations. Grouping, sorting,
  filtering and layout persistence come for free - that is our "result views".
- **ExpressQuantumTreeList** for the call tree: it must expand lazily, the tree
  can hold hundreds of thousands of nodes (the store is designed for it, see the
  scale test).
- **ExpressPivotGrid** for allocations by type x allocating method.
- **ExpressCharts** for the Monitor panel and for the heap growth diff.
- **ExpressFlowChart** is the candidate for the Call Graph panel (later: the
  tree views answer most questions first).
- Source view: DevExpress has no code editor. Open question below.

## Run control

Toolbar, straight from AQTime's vocabulary: **Start**, **Pause**, **Resume**,
**Snapshot**, **Clear**, **Stop**. They map to the U7 endpoints one to one.

Two honest constraints to surface in the UI, not hide:

1. **Pause is real only on the weaver engine** (the collector has an `Enabled`
   flag). On the provider engine, pause closes the current segment and resume
   opens a new one; the button should say so.
2. **Clear does not remove instrumentation.** Woven IL stays woven and JIT-time
   instrumentation persists for the life of the process, so the overhead remains
   while collection is paused. AQTime behaves the same way on Win32.

A session is therefore a sequence of segments, and the Report panel needs a
segment selector ("all segments" by default).

## Screens

1. **Start page** - devices, recent sessions, "new session".
2. **Setup** - device and package pickers (from `list_devices` / `check_app`),
   mode, engine, callspec or weave filter with a class/method browser, duration
   and limits. Prerequisite problems appear here as blocking errors with the same
   guidance text the engine already produces.
3. **Live** - state, elapsed, events/s, trace size, log tail, and the run
   controls. Snapshot switches to Analysis without stopping the app.
4. **Analysis** - the AQTime layout: Report grid on top, Details (Calls / Lines)
   below, Call Tree and Editor as dockable panels, Summary as a page.
5. **Memory** - allocations by type / by site, heap snapshots, growth diff.

## Open questions

- Which editor control shows annotated source? SynEdit (mature, free, but a
  third-party dependency to license-check) versus a read-only cxGrid with one row
  per line, which fits the "results next to code" model and costs nothing extra.
- Call Graph: worth drawing at all, or do the two tree views cover it?
- Where does the GUI get sources from for the Editor panel - the build machine's
  paths in the pdb, or a configured source root (the reference application builds on Jenkins)?
