# net-android-profiler

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

Sibling project: [net-android-debugger](https://github.com/csm101/net-android-debugger) -
same architecture family (frontend-neutral core, thin frontends, living
specification documents, TDD with integration tests).

## Using it

[docs/USAGE.md](docs/USAGE.md) walks through the flows: find the hot method by
sampling, measure it exactly by instrumenting, hunt leaks with two heap
snapshots, and read the numbers correctly.

## Preparing your app

See [docs/APP_SETUP.md](docs/APP_SETUP.md): what each profiling mode
requires from the app build (EnableDiagnostics, MONO_DIAGNOSTICS environment
file for instrumenting, pdbs) and how to keep it out of normal Debug builds.

## Using the MCP server

    dotnet tool install -g dotnet-dsrouter        # once; adb must be on PATH (or ANDROID_HOME)
    register-mcp.cmd                              # publishes to %LOCALAPPDATA%\net-android-profiler and registers in Claude Code

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
build/                            MSBuild targets for build-time weaving
tests/NetAndroidProfiler.Tests    fast (recorded traces) + device tests (xUnit)
tests/WeaveSample                 assembly used as weaving input in tests
DevTools/                         argv-driven diagnostic probes
THIRD-PARTY-NOTICES.txt           licenses of the components a release ships
TestTarget/                       Android app used by the test suite
gui/                              Delphi + DevExpress GUI (future)
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
