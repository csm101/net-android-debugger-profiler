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
| Left tree of saved results (project > session > each Get Results) | every result set collected over time, reopenable | `archive`: a named copy of the result database per Get Results worth keeping, listed in the Explorer under its session |
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

### Call Graph (built 2026-09-09, the shape AQTime draws)

Read from the method in focus outwards, in columns: **callers to its left, callees to its
right, one column per level**, elbow connectors labelled with the number of calls on that
edge, and the heaviest edges drawn thicker and in the accent colour. The method in focus is
the box painted in the accent colour, the way AQTime paints it blue.

Each box carries the type and the method on a title bar (the namespace repeats across the
graph and eats the width; the whole name is a hover away) and its figures underneath -
Calls, Time, Time with children, or Samples for a sampling session.

**Every box that calls something has a [+]**, and that is what makes the graph usable on a
real application: opening it brings that method's callees into the next column, closing it
takes the whole branch away. Only the first level of callees is drawn by itself; nothing
expands on its own, because a complete call graph of an application is unreadable.

**It is a graph, not a tree**: one box per method, and a method called from two places has
two arrows into the same box - which is how AQTime draws it, and what makes "this is called
from three points of this branch" visible at a glance. It is also what ends recursion: a
method is visited once, so A -> B -> A has nowhere to expand for ever. The price is that
the figures in a box are the method's own totals rather than one path's share; the number
on each arrow is that call's own count, and that is where the per-path answer is.

**Every box turns its arrows on a vertical line of its own**, spaced out from its neighbours
in the same column. Routing them all through the middle of the corridor - the obvious way,
and the first way this was written - merges the brackets of two boxes into one, and the
picture then says that each of them calls every callee of the other. That is not a cosmetic
defect: it is a drawing that states something false.

An arrow that goes back to a box in the same column or to the left of one is drawn thin and
faint, routed around the boxes rather than through them: it is a return into the graph, not
another step outwards.

Each column is packed from the top in the order the calls were discovered, and the store
answers heaviest first - so the call that costs most is the box at the top of its column.
Line thickness and the accent colour say the same thing about an arrow: the share it has of
the heaviest call in the graph.

Being open is a property of the method, and walking to another method starts that method's
graph closed. `ExpandGraph(levels)` opens everything down to a depth the way clicking every
[+] would: the control channel's `view` takes `expand`, and the command line `--expand=<n>`
before a `--render=graph:`, which is how a picture of "the graph two levels deep" is taken
without a hand on the mouse.

The callers are one level and no further: a caller's own callers are that method's graph,
one click away. Clicking a box walks there, right-click walks back, double-click leaves the
graph for the source.

Layout: the callee columns are packed from the top, the method in focus sits against the
middle of the column it calls, and the callers against the middle of it. `HasCallees`
decides which boxes get a [+] without reading the callees of every box on screen.

The canvas lives in a **`TcxScrollBox`**, not the VCL one, and this is not cosmetic: the VCL
scroll box paints the system's white behind the drawing, which on a dark theme was a white
sheet flashing on every resize and standing in the open wherever the window was larger than
the graph, with unskinned scrollbars on top. The cx one is painted by the skin; it is
double-buffered, and `OnResize` re-lays the graph out so the canvas always covers the
viewport.

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
- `src/uSetupDialog.pas` - the Setup screen. It starts from the sources: point it
  at a solution, a project or a folder and it lists the Android applications it
  holds, then fills in the package, the build output and the assemblies from the
  project file, and offers the app's own namespaces and types as the callspec.
  **Build & install** runs the build a session needs (`EnableDiagnostics`, fast
  deployment, `-t:Install` on the selected device) and shows msbuild's output while
  it runs, so preparing an app never leaves the window. **Check app** then answers
  for the APK that is actually installed.
- `src/uJobDialog.pas` - the window a background job of the service reports through
  (a build, a tool install): it polls for the log lines it has not seen yet rather
  than waiting for the exit code.
- The live toolbar (New session / Snapshot / Pause / Stop) with a log pane, and a
  Source panel: SynEdit with C# highlighting, the method's line range washed, fed
  by the source locations the analysis recorded in the database.

- **Archive** on the toolbar, beside Snapshot: it refreshes the results and keeps a named
  copy of them, then goes on profiling. The copy appears in the Explorer under its session
  and opens on a double click with no session running - which is what makes the AQTime
  habit work: `Clear`, exercise the feature, `Archive` under a name that will still mean
  something in three days, and keep going. Repeat as often as you like; nothing is kept
  that was not asked for, so the disk holds the result sets you chose and no others.

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
The device pickers show what a person recognises - the AVD or the phone's model, with the
serial after it - and send the serial. A list of bare serials is unreadable the moment two
emulators are up, which is the normal case here.

**One list of profilers, not a mode and an engine.** The dialog asks which profiler runs -
CPU sampling, one of the three instrumenting ones, or the heap snapshot - and explains the
chosen one underneath in the terms that matter when choosing: what it modifies, whether the
app restarts, what a call costs, and what it cannot tell you. Two combos to combine were a
puzzle ("where is sampling?"), and a word like `weaver-tree` is implementation vocabulary,
not a choice a person can weigh.

**Choosing what to instrument is a tree, not a combo.** `Choose...` beside the callspec
opens a picker over what the app declares: namespaces inside namespaces, types inside them,
a box on every node, and the method count next to each - its own plus everything under it,
because that number is what the choice costs (`OxyPlot 3329` is an answer in itself). The
filter matches anywhere in the name, not only at the start. The result is an ordinary
callspec: a ticked namespace becomes `N:Full.Name` and swallows what is under it, so the
string stays short and readable. A second column marks the namespaces that deserve per-line
detail; the engine does not collect that yet, and the dialog says so rather than pretending.

**The build's two properties are choices too**: *Keep assemblies inside the APK (no fast
deployment)* and *Instrument during the build*. An app can have a reason to ship its
assemblies inside the APK, and then weaving during the build is the only way to instrument
it - so both are on the screen rather than assumed, and the combinations that cannot work
are refused **before** Start: a red line says which constraint is broken and Start stays
disabled while it is there. The rules are the engine's, not taste - a rewriting profiler
with the assemblies inside the APK has nothing to rewrite; a build-time weave needs to know
which profiler will read it; the runtime provider crashes a .NET 9 runtime.

2. **Setup** - the sources first (solution / project / folder, configuration),
   then device and package pickers (from `list_devices` / `check_app`), mode,
   engine, callspec chosen from what the app declares, duration and limits.
   Prerequisite problems appear here as blocking errors with the same guidance
   text the engine already produces, and the app can be built and installed from
   the same window.
3. **Live** - state, elapsed, events/s, trace size, log tail, and the run
   controls. Snapshot switches to Analysis without stopping the app.
4. **Analysis** - the AQTime layout: Report grid on top, Details (Calls / Lines)
   below, Call Tree and Editor as dockable panels, Summary as a page.
5. **Memory** - allocations by type / by site, heap snapshots, growth diff.

## When it crashes

Every exception - one escaping the startup, and one raised while the window is running -
opens a dialog holding the whole report and writes the same text to `NapGui.error.log`:
the class, the message, what was happening, and the **call stack resolved to units and
lines**. The dialog is deliberately plain VCL, with Copy and "Open the log": it runs after
something has already failed, possibly inside the skinning or the docking library, and a
reporter that needs the framework that just crashed is no reporter at all. A second
failure while it is up is ignored rather than stacking dialogs (JclDebug, reading the detailed map file the
build produces beside the executable). Before that, a crash report said `EInvalidOperation:
Control 'PanelSummary' has no parent window` and nothing else, and finding the line cost an
hour of reading code.

`--dialog=crash` raises on purpose: it is how the reporter itself is checked, and the only
way to notice that a change to the build has stopped the stack from resolving.

The symbols travel **inside the executable**: `build-gui.cmd` runs the JCL's
`InsertJCLDebugInfo` over the detailed map after compiling, which costs about 3 MB in the
exe instead of the 100 MB map nobody would ship - so a distributed GUI reports lines too.
Point `NAP_JCLDEBUG` at the tool if it lives elsewhere; without it the build still succeeds
and says what is lost. The first frames, which belong to the hook that captures the
exception, are trimmed so the line that matters is the first one.

## The toolbars do not come apart (2026-09-09)

`BarManager.NotDocking` holds every docking style, `dsNone` - which is what dxBar calls
floating - included: `TdxBar.CanMoving` is then false, so the bars cannot be dragged along
their row, moved to another edge, or torn off into a window of their own. Each bar also has
`AllowClose`, `AllowCustomizing` and `AllowQuickCustomizing` off. A toolbar that floats is a
way for a window to be broken by accident with nothing on the other side of the trade; the
dockable *panels* are where arrangement belongs.

Because of that, a saved layout is the panels and nothing else - there is no longer anything
about the bars to remember (an older layout's `<name>.bars.ini` is ignored).

## File, and the run controls (2026-09-09)

Starting a session and opening one are File commands - that is how work begins, not how a
run is controlled - so the Session bar leads with a **File** menu (New session Ctrl+N, Open
session Ctrl+O, "Add a sessions folder...", Exit) and keeps as buttons only what acts on the
session in progress: Refresh, Record, Snapshot, Archive, Pause, Clear, Stop.

The **Theme** combo is gone from the toolbar: a theme is chosen once, and Settings is where
a choice made once belongs. The unit times are shown in stays, because it is not a
preference - "is that 12 ms or 12 us" is a question about the row under the cursor, asked
while reading a result, and a dialog for it is the sort of friction that makes people stop
asking.

## Sessions in the Explorer (2026-09-09)

The Explorer groups sessions by the solution they came from, falling back to the project and
then to the package for sessions taken before a session recorded its origin. A profiler used
on more than one product otherwise shows a wall of timestamps in which yesterday's run of
the thing you care about is indistinguishable from a test run of something else.

A session is shown by its **name** when it has one. The New session dialog asks for one, and
for where to keep it: a session can live beside the product it measures instead of in the
profiler's own folder, and that folder joins the ones the Explorer browses - otherwise
"keep it where I want" would make the session disappear the moment it was created. The
folders are remembered in the settings (`[SessionFolders]`), and "Add a sessions folder..."
points the Explorer at somebody else's recordings.

The context menu on a session is Open, Rename..., Delete..., Show in folder, plus Add a
sessions folder and Refresh; a right-click first selects the node under the pointer, which a
tree list does not do on its own. Rename and Delete go through the control service rather
than rewriting `session.json` from Delphi: what a name is, and that a running session is not
deleted under its own collector, are the engine's rules.

## Recording on demand (2026-09-09)

"Start recording only when I say" in the New session dialog, and a **Record** button that is
enabled exactly while the session is `WaitingToRecord`. The app starts and runs normally, the
status bar says nothing is being measured, and what the results hold begins where the person
pressed Record. This is AQTime's "start with profiling disabled", and the reason for it is
that the part worth measuring is rarely the startup.

## When the Source panel has nothing to show

It now says which of three things is true, because they need different answers: the session
was recorded without a symbols directory (nothing can repair that afterwards - the pdbs of
that build are what source locations are read from); the session is still collecting and the
method has not been resolved yet (Snapshot does it); or that method's pdb genuinely carries
no line, which is ordinary for compiler-generated methods. When the file is simply not on
this machine, it names the solution the session came from.

## Run again (2026-09-09)

A session records everything it was started with, so asking the same question twice is not
a matter of remembering what was typed: **Run again...** - in a session's context menu, and
in the toolbar for the session whose results are open - opens the setup dialog on that
session's own spec, with a name that counts on from it (`Prova1` -> `Prova2`, `startup` ->
`startup 2`, never one already used in that folder). `uSessionSpec` reads the spec back
into a `TSessionRequest`, the same record the dialog fills in and the service is asked
with; `session.json` first, the copy in the database when the file is gone.

What a session does not record keeps coming from the settings: the build options describe
the app, not the run. The configuration is read out of the symbols path, because that is
where it is visible - a session whose symbols came from `bin\Release\...` was a Release
session. A build-time weave map comes back only when the build that produced it is still
there; otherwise the new run weaves on the device.

The toolbar's Run again is on only while nothing is collecting: two sessions on one device
would fight over the diagnostic port.

## The trap that emptied the window: a form with no Name (2026-09-09)

A saved docking layout stores each control's form as `ParentForm=<the form's Name>`, and
`TdxDockingController.LoadLayoutFromIniFile` **skips every dock site whose ParentForm it
cannot resolve** - after having cleared the layout. The main window is built with
`CreateNew` and had no `Name`, so every layout was written with `ParentForm=` empty and
none of them was ever loaded back: the window came up bare, and the bare state was then
saved over the good one. `Name := 'MainForm'` in the constructor is the whole fix; the two
guards below are what keep any future version of this from being permanent.

- A load counts as failed when it raises **or** when it leaves the dock site with no
  children; either way the built-in arrangement is applied (`ApplyBuiltInLayout`), because
  an empty grey rectangle is not something a person can fix from inside the window.
- An arrangement with no panels is never saved: the last good file stays where it is.
- The reason goes to `NapGui.layout.log` beside the executable, because when this happens
  there is no window to say it in.

## Settings and layouts

A Settings window (theme, the unit times are shown in, the font code and logs are read
in with a live preview, the sessions folder, and which nap.exe the GUI starts) writes to
`NapGui.settings.ini` beside the executable - preferences travel with the folder rather
than living in the registry.

Panel arrangements are saved by name under `layouts\`. The docking controller writes a
whole ini per layout, so a layout is a file and its name is the file name; `<name>.ini`
holds the panels, `<name>.bars.ini` the toolbars.

**Layouts is a menu, not a button.** It drops down the saved arrangements - the one the
window opens with marked `[default]` - and, below them, *Save this arrangement as...*,
*Open the window with* (the arrangement you left, or any saved layout, ticked), *Manage
layouts...* for renaming and deleting, and *Back to the built-in arrangement*. The items
are rebuilt on every popup rather than kept in step with the folder, so a layout saved,
renamed or deleted meanwhile is in the list without a restart. With no default chosen,
the window comes back the way it was closed.

**Only a window that was on screen writes its arrangement back.** The exit path used to
save whatever the form was holding, so a run that never showed a window - `--export`,
`--render`, the control channel - wrote an arrangement built for a picture over the one
the user had arranged, and the next window opened empty. The rule is not "which switch is
this": a window that was never shown has no arrangement of its own. `DoShow` records that
it was, and that flag, not the switch, is what allows the save. *Back to the built-in
arrangement* sets the same flag the other way for the rest of the run, until panels are
moved again.

`--dialog=settings` and `--dialog=layouts` open one straight away, which is how the
dialogs get exercised without a hand on the mouse (the same reason `--tab=` exists).
`--export=<file>` writes the report of the session named on the command line and quits,
so a build script can produce the same spreadsheet the Export button produces.

## Starting a session

Nothing about a session has to be typed twice. The dialog starts at **Solution**: a
`.sln`/`.slnx`, a `.csproj`, or a folder to search. `GET /projects` reads the project
files and answers the Android applications it found - a library that targets Android is
not one - and picking a project fills in the package (from `ApplicationId`, or the
manifest), the build output (`bin/<Configuration>/<tfm>`, which is what carries the pdbs)
and the assembly it produces. The callspec is a list rather than a field:
`GET /projects/candidates` reads the app's own assemblies with Cecil and offers their
namespaces and types, largest first - and each candidate names the assemblies it came out
of, so choosing one narrows **Assemblies** to what actually has to be woven. A product
with thirty assemblies must not pay a pull, a rewrite and a push for each of them
because one type is being measured. The last solution and project come back on the next run.

Two things that used to send the user to a command prompt are buttons here:

- **Build & install**, with **Clear deployed assemblies first** beside it (ticked by
  itself when the project says the app embeds its assemblies, which is exactly when
  switching to fast deployment can leave stale ones behind), builds the selected project
  with the properties a session needs -
  `-p:EnableDiagnostics=true`, `-p:EmbedAssembliesIntoApk=false` for the weaver engines,
  `-t:Install -p:AdbTarget=-s <serial>` - as a background job whose output streams into a
  window. The profiler still never rebuilds anything by itself: this runs only on a click.
- **Check app** answers for the APK on the device, which is a different question from
  what the project file says.

The hint under the project says what this build is missing (`EnableDiagnostics` absent,
assemblies embedded in the APK, never built in this configuration) and what Build &
install would add.

At startup, when the control service comes up, the GUI asks `GET /prereqs` for the tools
a session needs. A missing `dotnet-dsrouter` - the one nobody remembers installing - is
offered as a one-click install (`POST /prereqs/install`, the same job machinery), and
anything that cannot be installed automatically (adb) is reported with its fix.

The dialog also asks for device, package, mode, engine, callspec, **assemblies**,
duration and build output. The assemblies field is the one that is easy to leave out and
expensive to get wrong: the service infers the assembly from the first two dotted
segments of the callspec, which is right when the namespace and the assembly agree and
wrong when they do not - `N:TestTarget.Workloads` lives in `TestTarget.dll`. Empty means
"infer"; naming them settles it.

## Windows and dialogs

Every dialog is resizable, with anchors that put the extra room where it is worth having:
the wide fields (paths, callspecs, package names) take the width, the lists and logs take
the height, and the buttons and the answer keep to their edges. Each form sets
`Constraints.MinWidth` / `MinHeight` so it cannot be shrunk into overlapping controls -
the smallest useful size is the size it opens at. A dialog whose content is a path or a
call stack must not be read through a keyhole.

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

## The control channel: the window as a renderer (2026-09-08)

`NapGui.exe --control` is the same window, driven by another program instead of a mouse:
one JSON object per line on standard input, one per line on standard output
(`gui/src/uGuiControl.pas`). It exists so that an agent can show what it found - a call
graph, the growth between two heap snapshots, a hot method beside its source - instead of
describing it in numbers.

- **The window is not shown.** A control that has a handle paints itself into a bitmap
  whether or not it is on screen, which a probe settled before any of this was written: a
  skinned `TcxGrid` and a hand-drawn `TPaintBox` both render on a form that was never
  shown, and one `PaintTo` of their container brings both. So a picture costs nothing on
  the user's screen, works while the screen is locked, and cannot contain anything of
  their desktop. `show` puts the window up when somebody asks for it.
- **Commands**: `status`, `open` (a session.db, or the folder holding one), `view`
  (panel, method to focus, filter, sort), `capture` (panel or whole window, size, an
  optional file, base64 PNG in the answer), `show`, `hide`, `close`.
- **A capture is trimmed** to what was drawn unless `trim: false` says otherwise
  (`uGuiRender.TrimToContent`). The Call graph draws a few boxes on a canvas the size of
  the window: untrimmed, the picture is mostly white. The scan starts inside the panel's
  frame and scroll bars, which are furniture rather than content, and a picture with no
  empty margin comes back as it was. Each answer carries
  the id of its request and either `ok` with what was done, or `error` with what to do
  instead. `uGuiControl.ExecuteCommand` knows nothing about the transport, so a pipe or a
  socket can be put in front of it later without touching the commands.
- **Every command runs on the main thread** (a render is a paint), which is what the
  reader thread's `Synchronize` is for; the answer is written from the same block, so
  answers keep the order of their requests.
- **A driven window is not somebody's window**: it does not load the saved layout
  (`GDrivenWindow`) and, never having been shown, does not write one either, so a picture
  does not depend on where a panel was last dragged, and arranging panels for a capture
  never disturbs the arrangement a person chose. `GiveRoomTo` gives the panel being captured the room to be readable.
- **`--render=<panel>:<file.png>`** is the same rendering from the command line, for a
  script with no channel; `gui/tests/smoke.ps1` checks it.
- The other side is `src/NetAndroidProfiler.Core/Gui/GuiChannel.cs` (the process and the
  protocol) and the `gui_*` MCP tools over it.
## The Setup dialog fills in what it already knows (2026-09-08)

The dialog used to refuse a callspec that reached an assembly missing from the Assemblies
field - "add it there" - and send the reader back to the picker to type a name the dialog
had on screen. It now adds every assembly the callspec reaches (`EnsureAssembliesForCallspec`,
called from `Validate`) and says what it added, without blocking Start. Names are added and
never removed: an assembly the callspec does not reach weaves nothing, while removing one
somebody typed on purpose would lose it. Picking a candidate no longer overwrites the field
either, which used to drop the assemblies of the other parts of a multi-part callspec.

## The window dies with its owner (2026-09-08)

`--parent-pid=<n>` in control mode: the window watches that process and ends when it goes. The
same guard the GUI puts on the `nap serve` it starts, for the same reason - a server that is
killed rather than shut down would otherwise leave a hidden window, and its control service with
it, holding files for the rest of the day. Found exactly that way: a killed test server left a
window whose `nap serve` blocked a build hours later. The ready line reports the owner it was
given, so a client can see its own pid come back and know the guard is on.

Ending it took two findings, both measured under the Delphi debugger on 2026-09-08, and both
worth keeping because they are true of any hidden VCL application:

- **A form that is never shown has no window.** Enumerating the process's windows finds only
  its `TApplication`, a GDI+ hook window and a handful of `TPUtilWindow`s - no `TMainForm` at
  all. So `PostMessage(MainForm.Handle, WM_CLOSE, ...)` reached nobody; worse, reading
  `MainForm.Handle` from a service thread would have *created* the window on that thread,
  whose message queue nobody pumps. `PostThreadMessage(MainThreadID, WM_QUIT, ...)` did not
  end it either. What does is `Application.Terminate` on the main thread, reached through the
  same `TThread.Synchronize` the channel already uses for every command.
- **The wait that actually held the process open was in the shutdown, not in the window.** The
  unit's finalization freed the reader thread, which blocks on standard input: freeing it
  waits for input that may never come. It is only terminated now, and the thread goes with the
  process.

With both in place the window ends about fifty milliseconds after its owner does, by any of the
three routes (owner killed, owner already gone, input closed). The hard exit two seconds later
stays as insurance and no longer fires: a window nobody can see is a window nobody will close
by hand, and nothing is lost by it - a driven window writes neither layout nor settings, and
the session database is the profiler's, not the window's.

## The Source panel scrolls with skinned bars (2026-09-08)

SynEdit scrolls with the window's own non-client scroll bars, which no skin touches: on the
dark theme they stayed bright grey beside a dark editor. They are turned off
(`ScrollBars := ssNone`) and driven from two `TcxScrollBar`s, which the skin paints like every
other bar. The arrangement is the one that already works in CVSTreeGraph's annotate pane, and
it was copied rather than reinvented: a host panel at the bottom holding the horizontal bar and
a square corner, the vertical bar to the right, `SetScrollParams(1, Max, Position, PageSize)`
with 1-based values as the editor's own `TopLine` and `LeftChar` are, `Max` never smaller than
a page (a scroll bar refuses a page as large as its range), `UnlimitedTracking`, and a two-pass
loop for whether each bar is needed, because one bar appearing changes the room the other has.

What raises an update: the editor's `OnStatusChange` (the wheel, the caret, a new file), the
window's resize and the docking layout - SynEdit does not publish `OnResize`, and a resize
moves nothing the editor would report. Two guards, both learned the hard way: nothing runs
while the constructor is still building the panels, and nothing runs at the moment a visible
window is first shown, when the dock panel is creating its own window and has no handle yet.
A hidden window has no handles at all and is exactly the case that must still work, which is
why the second guard asks about `Visible` and not only about the handle.

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
