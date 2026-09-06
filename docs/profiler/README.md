# net-android-profiler

This project now lives in the `net-android-debugger-profiler` monorepo
(`C:\Athens\GitHub\net-android-debugger-profiler`, GitHub `mca-software/net-android-debugger-profiler`,
private) as its profiler component: sources under `src/NetAndroidProfiler.*`, this document
under `docs/profiler/`. Repository-level state lives in the root `TASK_RESUME.md`; the shared
rules in the root `CLAUDE.md`, the component's own in `src/NetAndroidProfiler.Core/CLAUDE.md`.
A profiler for **.NET for Android** applications (C# on MonoVM): CPU sampling,
memory/allocation analysis, and instrumenting (deterministic enter/leave)
profiling - driven either by AI agents through **MCP** (Model Context
Protocol) or, later, through a rich desktop GUI. A free alternative to the
Visual Studio Enterprise Android profiler.

## How it works

Collection rides the official .NET diagnostics stack - MonoVM's embedded
EventPipe reached over adb via `dotnet-dsrouter`/`dotnet-trace`/`dotnet-gcdump`,
apps built with `-p:EnableDiagnostics=true` (Release builds included). On top
of that this project adds:

- **Orchestration** - one command from "here is my csproj" to "trace
  collected": build, deploy, port wiring, suspend handling, logcat capture.
- **Analysis** - `.nettrace`/`.gcdump` parsing (via the MIT
  [TraceEvent](https://github.com/microsoft/perfview) library) into call
  trees, hot paths and allocation reports, stored in **SQLite** with a
  versioned schema.
- **MCP frontend** - semantic profiling tools (`profile_run`,
  `profile_hotspots`, `profile_annotate_source`, ...) so an autonomous agent
  can profile an app and reason about the results.
- **Instrumenting modes** - the runtime's experimental
  `Microsoft-DotNETRuntimeMonoProfiler` provider (method enter/leave with
  callspec filters, exact allocations), and an IL-weaving mode (Mono.Cecil)
  that is runtime-independent and works on .NET 9 apps, where the runtime
  provider is unusable. Weaving happens either on the device (fast-deployment
  builds) or during the build (`nap-weave`).
- **GUI frontend (planned)** - Delphi + DevExpress VCL desktop client in the
  AQTime style: call trees, hot lists, allocation pivots, timelines - reading
  the SQLite analysis database directly.

Sibling project: [net-android-debugger](../debugger/README.md) -
same architecture family (frontend-neutral core, thin frontends, living
specification documents, TDD with integration tests).

## Using it

[USAGE.md](USAGE.md) walks through the flows: find the hot method by
sampling, measure it exactly by instrumenting, hunt leaks with two heap
snapshots, and read the numbers correctly.

## Preparing your app

See [APP_SETUP.md](APP_SETUP.md): what each profiling mode
requires from the app build (EnableDiagnostics, MONO_DIAGNOSTICS environment
file for instrumenting, pdbs) and how to keep it out of normal Debug builds.

## Example app: ProfileMeExample

`examples/ProfileMeExample.sln` is a .NET MAUI Android app built to be profiled:
every screen carries one performance problem on purpose, chosen so that a
different feature of the profiler is the one that exposes it. The business
logic lives in a class library (`ProfileMeExample.Domain`), as it does in real
apps, so the instrumenting screens also show how to profile a referenced
assembly rather than only the app's own. Each screen has a **Guide** (toolbar
item and button) that says what to look for in the profiler; the same guidance,
in long form, is below. The texts live in
`examples/ProfileMeExample/Scenarios/ScenarioCatalog.cs`, with a slot for a
manual URL per screen for when the manual site exists.

Build and install it like any app to be profiled - from the GUI (open the
solution in the Setup dialog and press Build) or from a prompt:

```powershell
dotnet build examples\ProfileMeExample\ProfileMeExample.csproj -c Debug -t:Install `
             -p:EnableDiagnostics=true -p:AdbTarget="-s emulator-5556"
```

Package: `com.mcasoftware.profilemeexample`. Symbols for source annotation:
`examples\ProfileMeExample\bin\Debug\net10.0-android`. Instrumenting callspec:
`N:ProfileMeExample.Domain`, weaving `ProfileMeExample.Domain`.

| Screen | The problem | What exposes it | What to look for |
|---|---|---|---|
| **Slow search** | A fuzzy search re-tokenizes, lower-cases and edit-distances every word of every product on every query. | CPU sampling: `profile_hotspots`, `profile_callers`, `profile_annotate_source` | `NaiveCatalogSearch.EditDistance` and `Score` at the top by exclusive CPU, `Search` above them by inclusive time. One caller of `EditDistance`, once per word per product. Remember that exclusive samples include very short callees (`ToLowerInvariant`, `Split`). |
| **Frozen button** | A legacy gateway waits for the server on the UI thread (`GetAwaiter().GetResult()` on a Task). | CPU sampling, plain against `*_cpu` columns; `profile_threads` | `LegacyGateway.FetchBalance` has many inclusive samples and almost no CPU samples: the thread was blocked, not computing, and it is the main thread. `Parse` is the contrast: real CPU, and the only one. |
| **Chatty pricing** | Price and tax rate are looked up once per unit instead of once per line: tens of thousands of calls to methods that are each fast. | Instrumenting: `profile_timings`, `profile_tree`, `profile_callers`; Snapshot / Archive / Clear | Sampling names `PriceList.GetUnitPrice` but cannot say why. The Calls column can: one lookup per unit. The call tree shows `Build -> GetUnitPrice` with the count on the edge. Archive one run, clear, run again, compare. |
| **Allocation storm** | A CSV report built with `+=` on a string, numbers boxed into a `List<object>` per row. Nothing leaks; the GC never rests. | Allocations: `alloc_report`, `alloc_report bySite=true` | `System.String` far ahead by bytes, then boxed `Int32` and `Decimal`, `List<Object>`, LINQ enumerators. By site: strings attributed to `SalesReportBuilder.BuildCsv`, boxes and lists to `FormatRow`. The provider engine reports sizes, the weaver reports counts. |
| **Leaky dashboard** | A widget subscribes to a static event in its constructor and never unsubscribes. Closing the screen drops the UI's reference; the event keeps the widget and its 256 KB cache alive. | Memory: `profile_run mode=heap snapshots=2`, `heap_diff`, `launch=attach` | Open and close the dashboard five times between the two snapshots: `DashboardWidget` grows by five instances, `Byte[]` by five arrays of 256 KB. The screen shows it from the inside: Publish reports how many widgets received the notification. |
| **Async waterfall** | Seven forecast requests awaited one after the other; the device is idle the whole time. | Instrumenting of async methods: `<method> (async body)` entries | Sampling first: nothing is hot, no thread is busy - the time is spent awaiting. Then instrumenting: `GetWeekAsync (async body)` resumes eight times and its self time is a fraction of the wall clock the screen shows; `FetchDayAsync (async body)` has seven calls. The gap between self time and wall clock is the waiting. |
| **Re-enumerated query** | A lazy `yield return` query enumerated three times by `Any`, `Count` and `Sum`. | Instrumenting of iterators: `<method> (iterator body)` call counts | `StockQuery.LowStock (iterator body)` is called about three times the number of low-stock lines (one call per item, plus one per pass to end it) and `NeedsReorder` about three times the number of stock lines. An iterator count that is a multiple of the data it should walk once means repeated enumeration. |

Each screen prints its own wall clock after a run, so the profiler's figures can
be checked against what the user of the app experienced. The fixes are not part
of the app: the point is the diagnosis, and every Guide ends with the one-line fix
once the picture is there.

## Using the GUI

`gui\NapGui.exe` (build it with `gui\build-gui.cmd`) owns everything else: it starts
`nap serve` itself, checks the machine's prerequisites and offers to install the missing
ones, reads your solution to find the Android applications in it, builds and installs the
one you pick with the properties a session needs, then runs the session and shows the
results it reads straight from `session.db`. See [GUI_DESIGN.md](GUI_DESIGN.md).

## Using the MCP server

    dotnet tool install -g dotnet-dsrouter        # once; adb must be on PATH (or ANDROID_HOME)
    register-mcp-profiler.cmd                     # publishes to %LOCALAPPDATA%\net-android-profiler and registers in Claude Code

Tools: `list_devices`, `check_app`, `profile_run` (mode sampling | instrumenting |
heap; launch restart | attach), `profile_start` / `profile_stop` /
`profile_status`, `profile_sessions`, `profile_hotspots`, `profile_flat`,
`profile_tree`, `profile_callers` / `profile_callees`, `profile_timings`,
`alloc_report`, `heap_report`, `profile_threads`, `profile_report`,
`profile_annotate_source` (per-method figures on the source file, via the
build's portable pdbs), `heap_diff` (growth between two snapshots of a memory
session), `get_app_output`. Sessions are stored under
`%LOCALAPPDATA%\net-android-profiler\sessions\<id>\` (override with
`NAP_SESSIONS_ROOT`): `session.db` (SQLite, schema in ARCHITECTURE.md),
`trace.nettrace` (opens in PerfView / Visual Studio), `session.log`.

## Status

Working end-to-end on the emulator and on the first real app (the reference application):
CPU sampling, memory (exact allocation events, heap snapshots and growth
diffs) and instrumenting through either the runtime provider or the IL
weaver, all exposed over MCP. See `PROJECT_STATE.md` for milestones,
`ARCHITECTURE.md` for the design, `ANDROID_PROFILING_NOTES.md` for the
collection know-how and `KNOWN_UNKNOWNS.md` for what is still open.

## Layout

```
src/NetAndroidProfiler.Core       engine: orchestration + analysis (frontend-neutral)
src/NetAndroidProfiler.Mcp        MCP stdio server frontend
src/NetAndroidProfiler.Collector  tiny runtime library the woven code calls (netstandard2.0)
src/NetAndroidProfiler.Weave      nap-weave: build-time IL weaver
src/NetAndroidProfiler.Cli        nap: local control service (nap serve) + one-shot commands
gui/                              Delphi + DevExpress GUI (build-gui.cmd; reads session.db)
build/                            MSBuild targets for build-time weaving
tests/NetAndroidProfiler.Tests    fast (recorded traces) + device tests (xUnit)
tests/WeaveSample                 assembly used as weaving input in tests
DevTools/                         argv-driven diagnostic probes
THIRD-PARTY-NOTICES.txt           licenses of the components a release ships
TestTarget/Profiler/              Android app used by the test suite
examples/ProfileMeExample.sln     MAUI Android app with one deliberate problem per screen, one per profiler feature
```

## Build

```powershell
dotnet build NetAndroidProfiler.slnx
dotnet test  NetAndroidProfiler.slnx
```

Requires: .NET SDK 10+ with the `android` workload, Android SDK
platform-tools (`adb`), the global tools `dotnet-trace`, `dotnet-dsrouter`,
`dotnet-gcdump`, and an emulator or attached device for integration tests.

## Licensing

Proprietary and closed source: see `LICENSE`. The repository is private and
must stay private.

Third-party dependencies are restricted to MIT, BSD and Apache-2.0; GPL is
excluded. `THIRD-PARTY-NOTICES.txt` lists every component the shipped build
carries, with its version, license and upstream URL, followed by the full
license texts. It ships with any distributed release. The list is generated
from what the build actually distributes and guarded by
`Fast/ThirdPartyNoticesTests`, which fails when a new dependency is not
acknowledged.
