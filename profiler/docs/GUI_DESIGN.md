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

## What goes inside each panel

Not the names - the contents, taken from the AQTime reference and mapped onto our
data. This is the part an implementer needs.

### Report

One row per method, the anchor of everything else: selecting a row updates
Details, Call Tree, Call Graph and Editor. AQTime splits it into categories
(Routines / Source Files / Modules) and groups rows per thread with an
"All threads" group; we do the same, since `method` carries the module and
`thread` the thread.

Columns, sampling (`sample_stat`): Samples exclusive, Samples inclusive, the two
CPU variants, % of profiled code, Module, Source file. Instrumenting
(`timing_stat`): Calls, Self time, Total time (with children), Avg, Min, Max,
Exception exits, % time, % with children.

### Details - two pages, both about the selected method

- **Calls**: two flat tables, Parents (who called it, with the hit count and time
  of *those* calls) and Children (what it called). Immediate neighbours only,
  from `sample_edge` / the timing tree.
- **Lines**: one row per source line of the method with the same per-line values
  the Editor grid shows. This is our pdb annotation, already implemented for MCP.

### Call Tree - two recursive trees, not one

AQTime's key detail, and the one worth copying exactly:

- **Children pane**: the tree of calls started by the method, in direct order
  (callee is a child node).
- **Parents pane**: the tree of calls that led to it, in *reverse* order (the
  caller appears as a child node). Reading down the tree walks back up the stack.
- The root of the whole hierarchy is a pseudo-method (`<_Root_>` in AQTime).
- The **critical path is drawn in bold**: the longest route through the tree,
  where "longest" is computed on a column the user picks (time, samples, calls).
  This is what turns a 200k-node tree into something answerable at a glance, and
  it is cheap for us: the trees are already in `sample_tree` / `timing_tree`.

### Call Graph

The same relationships drawn instead of nested: the selected method as the
central box, parents above, children below, arrows for the call direction. Each
box has the name on top and one metric underneath (AQTime uses Time with
Children), and the critical path is bold here too. Lower priority than the trees:
same data, prettier, less dense.

### Editor

Source code with a **grid to the left carrying the same columns as the Details
Lines page**, one row per line. AQTime's IDE integration paints each line's row
with a rectangle whose red saturation grows with the alert level (white =
healthy, pink = look at it, deep red = the problem), and shows the values as
hints on hover; the footer sums the selected rows. That colour scale is worth
copying: it is what makes a hot loop visible without reading numbers.

Control: **SynEdit** (`C:\Athens\SynEdit`, TurboPack fork for Delphi 12,
syntax highlighting, code folding, DirectWrite; agent-oriented notes under
`DOCS/`). **Licensing check before this ships**: SynEdit is MPL 1.1 / LGPL 2.1
dual-licensed, and our policy is MIT/BSD/Apache-2.0 with LGPL only on explicit
approval and dynamic linking - which a Delphi build cannot do. Under MPL 1.1 the
copyleft is per-file: linking it into a closed product is allowed, but any change
we make to SynEdit's own files must be published. Decision: use it unmodified,
record the choice in THIRD-PARTY-NOTICES, and keep any customization in our own
units (descendant classes, event handlers) rather than in its sources.

### Summary

Not a wall of totals: AQTime fills it with top-N answers for the selected result
set - worst performing routines, most called routines, deepest call stacks. Ours:
session identity (device, package, mode, engine, duration, segments), then the
top methods by exclusive samples or self time, the top allocating types and
methods, and the warnings the session recorded (size limit hit, no enter/leave
events, unresolved type names).

### Monitor

Live counters while the app runs, on data pages the user assembles (AQTime's
allocation monitor pairs a Number and a Size counter per class). Ours plots what
we can produce cheaply during collection: events per second, trace size against
the limit, session state, and - for heap sessions - live objects and bytes per
selected type across snapshots.

## Shell

DevExpress VCL, sources under `C:\Athens\DevExpress` (`DOCS/` holds per-library
notes written for agents; `Demos/VCL/<component>` is the first place to look).

- **ExpressDockingLibrary** carries the shell, and it is a real docking layout, not a
  page control: Explorer docked left, Report in the centre, and a bottom tab group
  holding Session log, Summary, Monitor, Memory, Source, Call graph, Call tree and
  Details - the same strip AQTime keeps down there. Every panel can be dragged
  elsewhere, tabbed with another, floated or closed, and the arrangement is saved to
  `NapGui.layout.ini` beside the executable and restored on the next run.

  Three things bite when the panels are built in code rather than dropped on a form:
  a panel with no `Parent` has no `ParentForm`, and the docking painter is resolved
  through it, so docking one straight after `Create` walks into a nil form; tabbing
  onto a panel that already has tabs means docking to its `TabContainer`, not to the
  panel; and a saved layout matches controls by `Name`, so runtime panels need one or
  the layout comes back as a tree of strangers. `DockTo` returns quietly when the
  target refuses, so the code raises instead - a panel that looks placed but is not
  fails later with "has no parent window".
- **Light and dark themes**, chosen from the toolbar and remembered in
  `NapGui.settings.ini` beside the executable. Three layers have to agree or the
  window looks half-painted: the DevExpress controls follow a skin
  (Office2019Colorful / Office2019Black), SynEdit and the plain VCL controls take
  explicit colours, and the panels we paint ourselves (pies, call graph, monitor,
  the Source wash) read the palette from `uTheme`. The palette follows the one used
  by CVSTreeGraph, so the tools on this desk look related.

  Use the DevExpress editors (`TcxLabel`, `TcxButton`, `TcxComboBox`, `TcxMemo`,
  `TcxTextEdit`, `TcxCheckBox`, `TcxPageControl`), not their VCL equivalents: they
  follow the skin, so the theme code only has to colour the canvases it paints
  itself and the editor. With plain VCL controls every label has to be recoloured by
  hand - and a `TLabel` needs `ParentFont := False` first, or the skin hands its own
  font colour back.
- **ExpressBars** for the run controls: `TdxBarManager` with two bars, Session and
  View. Bars beat a panel of `TcxButton`s - they are skinned, the user can move,
  hide or customise them, and the items lay themselves out instead of sitting at
  hardcoded pixels that break at another DPI. A ribbon is the wrong shape here: it
  costs a third of the vertical space in a window whose whole point is dense
  panels, and AQTime itself is a toolbar application.
  The glyphs are drawn in code (`uGlyphs.pas`): eight monochrome shapes, drawn
  four times oversized and averaged down for the antialiasing GDI does not give,
  tinted with the theme's text colour so the toolbar follows light and dark.
- **ExpressQuantumGrid** for Report, Details, allocations. Grouping, sorting,
  filtering and layout persistence come for free - that is our "result views".
- **ExpressQuantumTreeList** for the call tree: it must expand lazily, the tree
  can hold hundreds of thousands of nodes (the store is designed for it, see the
  scale test).
- **ExpressPivotGrid** for allocations by type x allocating method.
- **ExpressCharts** for the Monitor panel and for the heap growth diff. Both are
  currently `TPaintBox`es: a handful of bars and a scrolling line do not need a
  chart engine, and painting them ourselves keeps the theme in one place.
- **ExpressFlowChart** is the candidate for the Call Graph panel (later: the
  tree views answer most questions first).
- Source view: DevExpress has no code editor - SynEdit fills that role, see the
  Editor panel above.

## Run control

Toolbar, straight from AQTime's vocabulary: **New session**, **Snapshot**,
**Pause**/**Resume**, **Clear**, **Stop**. They map to the U7 endpoints one to
one, and all but New session are enabled only while a session is running.

Two honest constraints to surface in the UI, not hide:

1. **Pause is real only on the weaver engine** (the collector has an `Enabled`
   flag). On the provider engine, pause closes the current segment and resume
   opens a new one; the button should say so.
1b. **Snapshot, Pause and Clear are greyed out unless the session runs on the weaver
   engine.** A runtime-provider trace only becomes readable when its session ends, so
   the service refuses those calls; the toolbar says so by disabling them rather than
   failing on the click.
2. **Clear does not remove instrumentation.** Woven IL stays woven and JIT-time
   instrumentation persists for the life of the process, so the overhead remains
   while collection is paused. AQTime behaves the same way on Win32.

A session is therefore a sequence of segments, and the Report panel needs a
segment selector ("all segments" by default).

## What exists today

`gui/` builds headless with `build-gui.cmd` (which reads the DevExpress and SynEdit
paths from the installed IDE, so a machine that can open the project can also build
it from a script) and contains:

- `src/uSessionStore.pas` - the read layer over `session.db`: session identity,
  report rows per mode, lazy call-tree children, Details parents/children, segment
  history. It opens the file read-only and tolerates older schemas.
- `src/uMainForm.pas` - the shell: Report grid (cxGrid over the query, with the
  columns captioned and nanoseconds formatted), Details with the Parents and
  Children tables underneath, and a lazy Call Tree whose critical path is bold.
- `tests/StoreTests.dpr` - a console harness that runs the read layer against real
  session databases; this is where a schema mismatch surfaces first.

Two defects were found by looking at the running window rather than the code: the
grid showed negative times, because SQLite has no column widths and FireDAC read
the declared `INTEGER` as 32 bits (fixed in the schema and, defensively, with a
FireDAC map rule), and a cleared session lost its events because the collector kept
writing to files that had been deleted under it.

- `src/uControlClient.pas` - the control-service client: it starts `nap.exe serve`,
  reads the port from the JSON line the service prints, owns the process and shuts
  it down with the GUI.
- `src/uSetupDialog.pas` - the Setup screen: device list, package, mode, engine,
  callspec, duration, build output for the symbols, and the prerequisite check
  before anything starts.
- The live toolbar (New session / Snapshot / Pause / Stop) with a log pane, and a
  Source panel: SynEdit with C# highlighting, the method's line range washed, fed
  by the source locations the analysis recorded in the database.

- The visual layer AQTime is known for: percentage columns drawn as bars in the
  Report, a pie beside each of the Details tables (the share each caller and callee
  has), a Call Graph panel (callers above, the focused method in the middle,
  callees below, with the share on each arrow, and clicking a box walks the graph),
  an Explorer listing the sessions in the root, a Summary page of answers rather
  than a table, and a unit selector (Automatic / s / ms / us / ns) that every panel
  formats with.

- A Memory panel with the three questions the engine can answer: allocations by
  type, allocations by allocating method, and the live heap - with a "growth
  against" switch that diffs two snapshots.
- A Monitor panel that plots how fast the session is producing data (the trace on
  the provider engine, the pulled event files on the weaver) from
  `GET /sessions/{id}/counters`, polled once a second while a session runs.
- The Explorer's categories regroup the Report (Routines flat, Modules and Source
  files grouped by that column).

Deliberately absent: a per-line Details page. MonoVM produces no per-line samples,
so the honest presentation is the method's range painted in the Source panel, not
a table of numbers that would all belong to one line.

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

## Settings and layouts

A Settings window (theme, the unit times are shown in, the font code and logs are read
in with a live preview, the sessions folder, and which nap.exe the GUI starts) writes to
`NapGui.settings.ini` beside the executable - preferences travel with the folder rather
than living in the registry.

Panel arrangements are saved by name under `layouts\`, and the Saved layouts window
loads, renames, deletes, saves the current one, and marks the one to open with. The
docking controller writes a whole ini per layout, so a layout is a file and its name is
the file name. With no default chosen, the window comes back the way it was closed.

`--dialog=settings` and `--dialog=layouts` open one straight away, which is how the
dialogs get exercised without a hand on the mouse (the same reason `--tab=` exists).
`--export=<file>` writes the report of the session named on the command line and quits,
so a build script can produce the same spreadsheet the Export button produces.

## Starting a session

The Setup dialog asks for device, package, mode, engine, callspec, **assemblies**,
duration and build output. The assemblies field is the one that is easy to leave out and
expensive to get wrong: the service infers the assembly from the first two dotted
segments of the callspec, which is right when the namespace and the assembly agree and
wrong when they do not - `N:TestTarget.Workloads` lives in `TestTarget.dll`. Empty means
"infer"; naming them settles it.

## Working with the tables

- **Find panel** on every grid, `Ctrl+F`: filters as you type and highlights the
  matches. A 200k-row report is not readable without it, and it costs no space
  until it is asked for.
- **Export** of the table in front of you - xlsx, csv, html or text - following
  the view, so grouping, sorting and the find filter are part of what comes out.
  The button exports the grid that has focus, not a fixed one.
- **Shortcuts**: `Ctrl+O` opens a session, `F5` re-reads it.
- Saved layouts carry the toolbars as well as the panels: `<name>.ini` holds the
  docking layout, `<name>.bars.ini` the bar manager's.

## Open questions

- (decided) Call Graph: kept. The trees answer "where did the time go"; the graph
  answers "who is around this method", and it is the panel people point at. It
  walks on a click, goes back on a right-click, opens the code on a double click,
  and draws arrowheads so the direction of a call is not a guess. A fan wider than
  six is cut, and the graph says how many it dropped rather than pretending it is
  complete.
- (decided) Sources for the Editor panel: the user points the session at the C#
  project folder, and the tool assumes it is the tree the app was built from. The
  audience is the developer working on that code, not someone profiling an app
  pulled from a store, so asking is legitimate and guessing is not. The pdb paths
  are the fallback when nothing was configured.
- (decided) Critical path on a sampling tree: computed on `inclusive_cpu` /
  `exclusive_cpu`, not on wall samples. A path through a thread parked in a wait is
  not a bottleneck, and the bold path is read as "this is where the time goes".
