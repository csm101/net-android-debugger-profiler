# Architecture

Living specification. Source of truth is the code; if this document and the
code disagree, the code wins. Update whenever a module's responsibilities,
threading model, or external contract changes.

## High-level wiring

```
MCP client (Claude Code) ── MCP / JSON-RPC 2.0 over stdio ── NetAndroidDebugger.Mcp ─┐
VS Code / nvim-dap       ── DAP / JSON over stdio ────────── NetAndroidDebugger.Dap ─┤
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
| `Core/Launch/AppProjectFinder.cs` | Finds the launchable Android application projects in a solution or folder (android TFM **and** an `ApplicationId` or `Exe`, which is what separates an app from the libraries around it); reads `.sln` and `.slnx` |
| `Core/Launch/AndroidLauncher.cs` (`ForeignDebugPropertyWarning`) | Reports a still-valid `debug.mono.extra` that is not ours before taking it over: the property is device-global and two debuggers on one device silently overwrite each other |
| `Core/Engine/ProcessDebugger.cs` (internal) | One `SoftDebuggerSession` per debuggee pid; connect with retries; event → `Stopped/Resumed/Exited` callbacks; step/continue/pause per process |
| `Core/Engine/DebugSession.cs` | Facade: aggregates N `ProcessDebugger`s, one shared Mono `BreakpointStore`, state machine, monotonic stop generation + `WaitForStopAsync`, inspection (threads, frames, locals, evaluate, expansion handles), app/debugger output buffers |
| `Mcp/` | `ModelContextProtocol` 2.2.0 stdio server; `DebuggerTools` (one tool = one or two facade calls), `SessionHost` (single active session, pid/thread defaults from the last stop), `TextFormat` (plain-text rendering) |
| `src/NetAndroidDebugger.Dap/DapConnection.cs` | DAP wire format: `Content-Length` framing over stdin/stdout, one writer at a time |
| `src/NetAndroidDebugger.Dap/DapIds.cs` | DAP's integer thread/frame/variable ids ↔ the engine's (pid, thread, frame) and expansion handles; frame and variable ids are dropped when a process resumes |
| `src/NetAndroidDebugger.Dap/DapAdapter.cs` | Request dispatch and event forwarding over one `DebugSession`; translation only |
| `src/Shared/ExceptionRuleFile.cs` | Reads exception rules from a JSON file and stands in as the engine's shared rule source; compiled into both frontends and the tests |
| `src/Shared/LaunchConfigFile.cs` | Reads a VS Code `launch.json` (JSONC, `${workspaceFolder}`, `${env:}`) into launch parameters and exception rules; compiled into the MCP frontend and the tests |

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


## DAP frontend

`NetAndroidDebugger.Dap` is a second frontend over the same `DebugSession`, with
no debugging logic of its own. Notes that matter to a client:

- The wire protocol is hand-rolled (framing is a dozen lines). That keeps the
  adapter free of a debug-protocol package whose licence would have to be
  cleared, and there is nothing to keep in sync with upstream.
- `launch` and `attach` take the same arguments (`deviceSerial`, `packageName`,
  and optionally `activityName`, `projectPath`, `deploy`, `basePort`,
  `configuration`, `propertyLifetimeSeconds`, `keepPropertyFresh`). They differ
  only in that `attach` never deploys: on Mono Android attaching *is* a restart
  with the agent enabled.
- `disconnect` always terminates the app. `terminateDebuggee: false` cannot be
  honoured, because the Mono runtime exits when the debugger disconnects.
  The response goes out *before* the teardown runs: stopping the logcat reader,
  clearing the property, force-stopping and removing the forwards take seconds
  on a healthy device and can hang on a sick one, and a client waiting for the
  response cannot tell slow from hung.
- One DAP thread list spans every process of the app, so thread names carry
  their pid.
- Frame and variable ids are invalidated on every stop; a stale id is refused
  with a message rather than silently addressing something else.
- Exception filters are read from **both** `filters` and `filterOptions`. A
  client sends the legacy array until some filter advertises `supportsCondition`
  — ours `types` does — and then sends `filterOptions` with `filters` empty.
  Reading one of the two makes every first-chance filter a silent no-op; the
  Delphi debugger hit exactly that under real VS Code, with a test that masked
  it by populating both. `filterId` is the specified key, `filter` the one some
  clients send; both are accepted.
- Every event goes through one queue with a single sender. They are raised on
  engine threads — a stop, a resume, a line of app output — and a client reads
  them as a sequence: a `continued` delivered after the `stopped` that followed
  it would leave it showing the wrong state.
- Step requests are acknowledged before the step runs, so the client sees the
  response before the `stopped` event that follows it.


## Exception rules

Filters answer "which types should stop the app". That is not enough for an app
that throws on purpose: the reference application raises an MQTT timeout whenever the network
blinks and a handled `InvalidOperationException` on every reconnect, so "stop on
everything" and "stop on nothing" are equally useless. Both exceptions were
measured on the running app: see ANDROID_ATTACH_NOTES.md, "Exception rules beat
filters on a real app".

`DebugSession` therefore carries an ordered rule list (`SetExceptionRules`),
taken from the Delphi debugger's design. The first rule whose set criteria all
match decides: `Break`, `Log`, `LogStack` or `Ignore`. Criteria are AND-ed and an
unset one is a wildcard, so a bare `{action: break}` is the catch-all. Rules
apply to **first-chance** exceptions only — an unhandled one always stops,
because the process is going down either way.

The one structural difference from the Delphi engine: reading an exception's
message means calling into the debuggee, which Mono forbids on its event thread.
So when no rule mentions a message the decision is made inline on that thread;
when one does, the stop is held and decided on a worker, and `ReportStop` is
called from whichever path wins. Type and raise-site criteria never need the
debuggee, which is why they are the cheap ones.

A rule that names a type has a wrinkle worth knowing: a throw site in an
assembly without debug info reaches the stop with **no type at all**
(`GetException()` throws inside Mono.Debugging - KNOWN_UNKNOWNS U15), and those
are precisely the third-party exceptions a rule wants to silence. So when the
stop carries no type and some rule cares about the type, the decision moves to
the worker as well and the type is recovered there through `$exception`, which
also repairs the reported stop. A rule that names no type is unaffected and
still decides on the event thread.


A second, shared source sits behind the session's own rules: a file the user
edits, consulted after them so a project overrides the machine-wide baseline.
`Continue` and every step re-read it when it has changed, which is the point —
edit a rule while the app is stopped and it governs the next resume, with no
restart. Core owns *when* to reload (`IExceptionRuleSource`), never the format:
the JSON reader is `src/Shared/ExceptionRuleFile.cs`, compiled into both
frontends and the tests, so the engine stays JSON-free. A half-written file is
the normal state while someone is editing, so a failed read keeps the rules
already in force and says so rather than failing the resume.

## Launch target and launch configuration

Two different questions: what the project already declares, and what only a
person can decide.

**Deduced** — `Core/Launch/AppProjectFinder.cs`. The package name lives in the
`.csproj` as `<ApplicationId>`, so `launch_app` reads it rather than having it
restated: a copy can contradict the project, and then the debugger launches an
app nobody is building. Targeting an `-android` framework is *not* the test for
launchable, because libraries target it too — in the reference application thirty-odd projects do,
and two are applications. An application declares an `ApplicationId`, or is an
`Exe`. `list_app_projects` is the counterpart of `list_devices` for choosing
between them, and pointing at a solution narrows the search to the projects it
names. The device follows the same rule: one ready device is not a choice,
several are. Nothing ambiguous is ever guessed — the failure lists the
candidates, which is also how the test harness has always behaved.

The device follows one chain, shared by both frontends, most explicit first: the
call's own argument, then `deviceSerial` in the configuration, then the
`NAD_DEVICE_SERIAL` environment variable, then the only ready device. The
environment sits between the file and the deduction on purpose - a serial names
a machine, not an app, so committing one to a shared configuration makes it
wrong on everybody else's desk, while an environment variable is exactly the
per-machine answer. `deviceSerial` is therefore **not** required in a launch
configuration, though `packageName` still is: that is the app's identity.

A serial that was named but is not attached is an error naming where it came
from, never a quiet fallback to whatever else is connected - a variable left
over from an earlier session would otherwise debug the wrong device while
looking like it worked. Several ready devices and no serial is an error too,
listing them. In VS Code, where there is a person present, the extension is the
right place to offer a picker; that is not built yet.

**Chosen** — `launch_from_config`, over `src/Shared/LaunchConfigFile.cs`. What
is left after deduction is real decisions: which exceptions matter, whether to
deploy, whether to keep the debug property fresh, one configuration per device.
The reader deliberately takes **the field names the DAP adapter already reads
from its launch request**, so one file describes the app for both frontends and
pressing F5 cannot drift away from launching through MCP.

What the reader handles, because real files contain it: JSONC (VS Code writes
comments into the file it generates, and people comment configurations out
rather than deleting them), `${workspaceFolder}`, `${env:VAR}`, and paths
relative to the project. `${workspaceFolder}` is the folder *holding* `.vscode`,
not `.vscode` itself — getting that wrong resolves every relative path one level
too deep and surfaces as a missing `.csproj` much later. Variables only VS Code
can answer, `${command:...}` and `${input:...}`, are refused by name rather than
passed through: a device picker reaching adb as a serial fails far from its
cause.

A bare array of configurations, or a single configuration object, is accepted
too, so using this outside VS Code does not mean writing the launch.json
envelope.

`exceptionRules` may be given in the configuration itself, and that is the point
of the file rather than a convenience: "this app throws these on purpose" is a
fact about the app, so it belongs in the app's repository, while the shared file
above stays the machine-wide baseline of one person's preferences.

Only `deviceSerial` and `deploy` can be overridden per call — the serial because
it genuinely changes per run, `deploy` to relaunch without rebuilding. Anything
else that changes per call means the configuration is wrong.

Two things the launch reports rather than doing silently. It reads
`debug.mono.extra` before writing it, and when the value it finds has not yet
expired it says which port that value points at and for how long: the property
is device-global, so taking it over can break a session someone started
elsewhere, and the victim's symptom (an app hanging for the agent timeout on a
port nobody listens on) is nowhere near this cause. And `launch_app` names what
it deduced, so a deduced package or device is never silent.

## Threading model

- Mono.Debugging raises events on its own event thread, one per
  `SoftDebuggerSession` (so one per process). `ProcessDebugger` handlers are
  tiny; `DebugSession` mutates state under one lock and signals waiters through
  a replaced `TaskCompletionSource` (`Signal()`); public events
  (`Stopped`, `StateChanged`) are raised outside the lock on that thread.
- `AndroidLauncher` runs the logcat reader on a thread-pool task; property
  rotation is synchronous on that thread (one adb call) and runs
  at two moments: when ActivityManager announces a process of ours (fork time)
  and when that process announces its agent. Rotating at the first shrinks the
  window in which two processes read the same port; the second is idempotent.
  The forward is created later, for the port a process actually took, so there
  is one forward per attached process rather than one per fork — and the
  rotation window stays as short as it can be.
- A launch waits for the package to have no live process before it publishes the
  port: `am force-stop` is asynchronous, and a leftover (or a sticky service on
  its way back up) would read the property and take the port.
- The agent-detection handler on the logcat thread is on the critical path for
  port rotation: the property is rewritten only after it has decided whether the
  process is ours. Anything slow there (an extra `ps`, a retry, a sleep) widens
  the window in which the next process reads the same port and loses its agent.
  Measured the hard way on 2026-08-21: a 400 ms retry added there was enough to
  make helper processes fail their SDB handshake.
- `LaunchOptions.KeepPropertyFresh` starts a renewal loop in `AndroidLauncher`
  that rewrites `debug.mono.extra` (same port, new deadline) every
  `lifetime/3`, so processes the app starts much later are still attached. All
  property writes — launch, port rotation on the logcat thread, and this loop —
  go through one semaphore.
- Every timestamp the session reports lives on the **device** clock. logcat
  stamps its lines with device local time; output arriving through the SDB
  user-log channel is produced on the host, so it is shifted by
  `AndroidLauncher.DeviceClockOffset`, measured once per launch. Frontends
  render the value as-is, never a host-local conversion.
- Inspection calls (`GetLocals`, `GetCallStack`, `Evaluate`) run on the caller's
  thread and call Mono.Debugging directly; they require the target process to
  be stopped (`InvalidSessionStateException` otherwise).
- `GetSourceFiles` is a metadata query (`VirtualMachine.GetTypesForSourceFile`
  plus `TypeMirror.GetSourceFiles`), so unlike the inspection calls it works
  while the process is running and needs no stop.
- Frontends never see Mono.Debugging types; expansion handles are opaque
  strings (`pid:n`) invalidated when that process resumes.
- Breakpoint binding is asynchronous, so `SetBreakpointAsync` /
  `SetBreakpointsAsync` hold a short settle window (frontends pass 750 ms)
  before answering; the synchronous overloads stay for callers that want the
  raw immediate state. With no process attached they return at once.
- Expression evaluation runs the expression as written, side effects included.
  The evaluator already invokes debuggee code to read ordinary properties, so
  a side-effect-free guarantee is not on offer; `AllowTargetInvoke=false` is
  the way to refuse invocation entirely.
- Attach is deduplicated per pid (`_attaching`): the direct launch path and the
  logcat `AgentDetected` event race on the first process; both await the same
  in-flight task, and a process is exposed in `_processes` only after its SDB
  handshake completes, so pause/continue never touch a half-connected VM.
- An unhandled exception is reported once per process: the runtime raises more
  of them on other threads while the process dies, and those extra stops are
  resumed automatically (`_unhandledReported`) so one Continue is enough and
  the reported details stay those of the first, real failure.
- Reading a frame's locals invokes nothing by itself, but expanding a value
  invokes its property getters in the debuggee — one slow getter therefore
  costs every expansion of that object. Keep deliberately slow members out of
  frames the rest of the suite inspects (TestTarget isolates `SlowProbe` in
  its own method).
- **Every operation that invokes debuggee code disarms the breakpoints first**
  (`WithBreakpointsDisarmed`). Mono resumes all threads for the duration of an
  invocation and only disables breakpoints on the invoking thread, so a
  breakpoint hit by another thread meanwhile suspends the VM with the
  invocation in flight: it never returns, is aborted on timeout, and the abort
  can kill the process. Hits that would have occurred during an evaluation are
  lost by design — an evaluation is not a resume.
- The breakpoint store is shared by all processes of the app, which is what
  makes a breakpoint apply everywhere; the known cost is that Mono's hit
  counter restarts when another process attaches (KNOWN_UNKNOWNS U13).
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
