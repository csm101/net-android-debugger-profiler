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
  callspec filters, allocations with callstacks), and a planned IL-weaving
  mode (Mono.Cecil) that is runtime-independent.
- **GUI frontend (planned)** - Delphi + DevExpress VCL desktop client in the
  AQTime style: call trees, hot lists, allocation pivots, timelines - reading
  the SQLite analysis database directly.

Sibling project: [net-android-debugger](https://github.com/csm101/net-android-debugger) -
same architecture family (frontend-neutral core, thin frontends, living
specification documents, TDD with integration tests).

## Status

Early scaffold. See `PROJECT_STATE.md` for milestones, `ARCHITECTURE.md` for
the design, `ANDROID_PROFILING_NOTES.md` for the collection know-how.

## Layout

```
src/NetAndroidProfiler.Core    engine: orchestration + analysis (frontend-neutral)
src/NetAndroidProfiler.Mcp     MCP stdio server frontend
tests/NetAndroidProfiler.Tests integration + recorded-trace tests (xUnit)
DevTools/                      argv-driven diagnostic probes
TestTarget/                    minimal Android app used by the test suite
gui/                           Delphi + DevExpress GUI (future)
```

## Build

```powershell
dotnet build NetAndroidProfiler.slnx
dotnet test  NetAndroidProfiler.slnx
```

Requires: .NET SDK 10+ with the `android` workload, Android SDK
platform-tools (`adb`), the global tools `dotnet-trace`, `dotnet-dsrouter`,
`dotnet-gcdump`, and an emulator or attached device for integration tests.
