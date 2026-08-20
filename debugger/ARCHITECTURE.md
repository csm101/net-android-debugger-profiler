# Architecture

Living specification. Source of truth is the code; if this document and the
code disagree, the code wins. Update whenever a module's responsibilities,
threading model, or external contract changes.

## High-level wiring

```
MCP client (Claude Code) ── MCP / JSON-RPC 2.0 over stdio ── NetAndroidDebugger.Mcp ─┐
VS Code (future)         ── DAP / JSON over stdio ────────── NetAndroidDebugger.Dap ─┤
                                                                                     ▼
                                             DebugSession  (NetAndroidDebugger.Core)
                                             JSON-free facade: engine + state machine
                                             + source mapping + value formatting
                                                                                     │
                                             Mono.Debugging.Soft / Mono.Debugger.Soft
                                             (ThirdParty/ — vendored mono/debugger-libs)
                                                                                     │  SDB wire protocol over TCP
                                                     adb forward tcp:PORT tcp:PORT ──┤
                                                                                     ▼
                                             MonoVM soft-debugger agent inside the
                                             Android app process (net9.0-android)
```

Deployment/launch orchestration is a separate Core module (`AndroidLauncher`):
msbuild is used only to deploy (`-t:Install`); the attach dance itself
(`debug.mono.extra`, `adb forward`, `am start`, port rotation per process) is
done with raw `adb` by the engine. See `ANDROID_ATTACH_NOTES.md` for the
verified recipe and why the SDK's own `-t:Run` attach wiring is not used.

## Design rules

- `DebugSession` is the single facade both frontends consume. It owns the
  soft-debugger session, breakpoint store, thread/frame snapshots, the value
  formatter, and an explicit session state machine
  (`NotStarted → Deploying → WaitingForConnection → Running ⇄ Stopped → Exited`).
  It exposes neutral records (no MCP/DAP ids) and async waits driven by a
  monotonic stop-generation counter — same pattern as the Delphi project's
  `TDebugSession`.
- Frontends are thin translators. Any logic useful to both belongs in Core.
- The MCP tool surface mirrors the Delphi MCP server (`delphi-win64-debugger`)
  wherever semantics carry over; Android-specific tools replace process-centric
  ones (see PROJECT_STATE.md for the target tool list).

## Modules (M1, implemented 2026-08-20)

| Module | Responsibility |
|---|---|
| `Core/Model/SessionModel.cs` | Public records/enums: `SessionState`, `StopEvent`, `BreakpointSpec/Info`, `ProcessSnapshot`, `ThreadSnapshot`, `FrameSnapshot`, `VariableSnapshot`, `LaunchOptions`, `AppTarget`, exceptions |
| `Core/Adb/AdbClient.cs` | adb wrapper; every device call takes an explicit serial; `StreamLogcatAsync` |
| `Core/Launch/AndroidLauncher.cs` | Deploy (`dotnet build -t:Install -p:AdbTarget=-s <serial>`), `debug.mono.extra` + forward, `am start`, logcat watcher → `AgentDetected(pid, port, name)` with **port rotation**, `ShutdownAsync` (clear property, remove forwards, force-stop) |
| `Core/Engine/ProcessDebugger.cs` (internal) | One `SoftDebuggerSession` per debuggee pid; connect with retries; event → `Stopped/Resumed/Exited` callbacks; step/continue/pause per process |
| `Core/Engine/DebugSession.cs` | Facade: aggregates N `ProcessDebugger`s, one shared Mono `BreakpointStore`, state machine, monotonic stop generation + `WaitForStopAsync`, inspection (threads, frames, locals, evaluate, expansion handles), app/debugger output buffers |
| `Mcp/` | `ModelContextProtocol` 2.2.0 stdio server; `DebuggerTools` (one tool = one or two facade calls), `SessionHost` (single active session, pid/thread defaults from the last stop), `TextFormat` (plain-text rendering) |
| `Dap/` (future) | stdio DAP adapter over `DebugSession` |

Not yet implemented: `ValueFormatter` (Mono.Debugging's `DisplayValue` is used
as-is), `SourceResolver` (breakpoint paths must match the PDB paths), logcat
filtering beyond pid matching.

## Multi-process model

A session debugs one *application* = a set of Android processes. Every process
gets its own SDB session (`ProcessDebugger`); stops are per process and carry
the pid. `SessionState.Stopped` means "at least one process is stopped";
`Continue()` resumes all stopped processes. Port rotation (see
ANDROID_ATTACH_NOTES.md) happens inside `AndroidLauncher` on the logcat reader
thread *before* `AgentDetected` is raised, so the next process always reads a
free port. Detach == terminate (runtime behavior), hence a single shutdown path.

## Threading model

- Mono.Debugging raises events on its own event thread, one per
  `SoftDebuggerSession` (so one per process). `ProcessDebugger` handlers are
  tiny; `DebugSession` mutates state under one lock and signals waiters through
  a replaced `TaskCompletionSource` (`Signal()`); public events
  (`Stopped`, `StateChanged`) are raised outside the lock on that thread.
- `AndroidLauncher` runs the logcat reader on a thread-pool task; property
  rotation is synchronous on that thread (blocking adb calls, ~100 ms).
- Inspection calls (`GetLocals`, `GetCallStack`, `Evaluate`) run on the caller's
  thread and call Mono.Debugging directly; they require the target process to
  be stopped (`InvalidSessionStateException` otherwise).
- Frontends never see Mono.Debugging types; expansion handles are opaque
  strings (`pid:n`) invalidated when that process resumes.
- Attach is deduplicated per pid (`_attaching`): the direct launch path and the
  logcat `AgentDetected` event race on the first process; both await the same
  in-flight task, and a process is exposed in `_processes` only after its SDB
  handshake completes, so pause/continue never touch a half-connected VM.
- Value formatting is culture-sensitive in Mono.Debugging; frontends set
  `InvariantCulture` at startup so numeric/date output is stable for machine
  consumers (MCP server `Program.cs`; tests via a `[ModuleInitializer]`).

## ThirdParty vendoring status

- `ThirdParty/debugger-libs` — git **submodule**. Upstream is
  https://github.com/mono/debugger-libs (`main` @ `e7fbb713`, 2026-02-11);
  the submodule URL currently points at the fork
  https://github.com/csm101/debugger-libs, branch `fix/csharp14-field-keyword`
  (commit `837f524` = upstream `e7fbb713` + one commit), because of the
  patch below. Switch the URL back to upstream once the PR is merged.
- **Local patch (submitted upstream as https://github.com/mono/debugger-libs/pull/419):**
  `Mono.Debugging.Soft/FieldValueReference.cs` member `field` renamed to
  `fieldInfo`. Reason: C# 14 (default for net10.0) makes `field` a contextual
  keyword inside property accessors → 10 CS1061/CS1503 errors. Pure rename, no
  behavior change. Until merged, the fork is the source of truth; do not add
  other patches there without documenting them here.
- Upstream projects are SDK-style (`net6.0;net472`); dependencies (Mono.Cecil
  0.10.1, Roslyn, Microsoft.SymbolStore, Mono.Unix, Newtonsoft.Json) come from
  NuGet — no side-by-side cecil/nrefactory clone needed (README.txt is stale on
  this point).
- Builds unmodified on .NET SDK 10 (verified 2026-08-20). We build **net10.0
  only** via `ThirdParty/Mono.Debugging.overrides` (upstream hook: imported by
  `Mono.Debugging.settings` *after* each csproj's own PropertyGroup, so it can
  override `TargetFrameworks`). If the fork patch above is ever dropped, the
  fallback is `<LangVersion>13</LangVersion>` in the same file. Note: the other hook,
  `Directory.Build.props` → `ThirdParty/debugger-libs.override.props`, is
  imported *before* the csproj body and cannot override `TargetFrameworks`.
- Consumed as `ProjectReference`s from `NetAndroidDebugger.Core` (Mono.Debugging.Soft,
  Mono.Debugging, Mono.Debugger.Soft); the three projects are listed in the
  `/ThirdParty/` solution folder of `NetAndroidDebugger.slnx`.
- Runtime trap: upstream references `Mono.Cecil 0.10.1` with
  `PrivateAssets="all"`, but `MethodMirror.GetCustomAttributes` (ENABLE_CECIL)
  needs it at runtime. Every consumer (`Core`, `DevTools/SdbProbe`) carries its
  own `<PackageReference Include="Mono.Cecil" Version="0.10.1" />`.
- Official NuGet packages are stale (2017) — do not use them.
- Clone with `git clone --recurse-submodules` (or `git submodule update --init`).
