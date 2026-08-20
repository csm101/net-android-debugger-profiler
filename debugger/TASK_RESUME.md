# Task resume

## Current task
M1 - engine + minimal MCP. Started 2026-08-20 after M0 (spike) and the
multi-process probe (U10, resolved: port rotation, see ANDROID_ATTACH_NOTES).
User approved proceeding with the DebugSession API proposed in chat, amended
for multi-process: one SoftDebuggerSession per debuggee process, aggregated
behind DebugSession.

## Current substep
M1 step 3: MCP frontend written (ModelContextProtocol 2.2.0, stdio) and
smoke-tested by hand (initialize/tools/list/list_devices OK). Adding an MCP
end-to-end xunit test (McpEndToEndTests) using the SDK client over stdio.
First integration run of the Core suite: 8/8 green (35 s).
Core files (compiling, tested):
- src/Core/Model/SessionModel.cs (records, enums, exceptions)
- src/Core/Adb/AdbClient.cs (explicit-serial adb wrapper, logcat stream)
- src/Core/Launch/AndroidLauncher.cs (deploy, property+forward, am start,
  logcat watcher -> AgentDetected with port rotation, shutdown)
- src/Core/Engine/ProcessDebugger.cs (one SoftDebuggerSession per pid)
- src/Core/Engine/DebugSession.cs (facade: state, generation, waits,
  breakpoints via shared BreakpointStore, inspection, expansion handles)
- tests/Harness/{TestEnvironment,DeviceFixture}.cs, tests/LaunchAndBreakpointTests.cs
  (8 tests: launch, bp main, bp helper via rotation, continue, callstack,
  step over, threads, terminate). Device from NAD_DEVICE_SERIAL.
test-runner agent launched for the first run (NAD_DEVICE_SERIAL=emulator-5554).

## Last test result
2026-08-20 run 2: 10/10 green in 46 s (8 Core + 2 MCP e2e), device clean,
no orphan MCP processes.

## the reference application test drive (2026-08-20, via the registered MCP server)
Worked first time: attach_to_app(App.Droid) -> main pid on 10000, helper
`App.Droid:crash_report_process` auto-attached on 10001 -> breakpoint on
CrashReportSender.cs:59 hit in the helper -> expand `this` (Android props
evaluated, errors surfaced as [error]) -> evaluate const expression ->
step_over 59->60 -> threads -> terminate; device clean afterwards.

## Usability bugs found (fix next, in this order)
1. wait_until_stopped: default afterGeneration = current generation, so if
   the stop already happened it reports "timeout (state=Stopped)". Default
   must be "generation-1 when already Stopped" (return the current stop).
2. set_breakpoint before launch_app fails ("No active debug session"):
   breakpoints on startup code are impossible from MCP. SessionHost should
   create a NotStarted session eagerly / reuse it in launch_app, and allow
   breakpoint tools on NotStarted.
3. `AttributionTag : string = (null) [expand: ...]` - null values get an
   expansion handle; suppress handle when value is null.
4. App trace lines ("[0:] ...", "Hot Reload initialization error") arrive via
   Mono.Debugging LogWriter and land in get_debugger_output; classify them as
   app output (everything not matching known debugger messages).
All four FIXED in code (2026-08-20): DebugSession.WaitForCurrentOrNextStopAsync
+ wait_until_stopped default; SessionHost.RequireForSetup/ForLaunchAsync
(NotStarted session reused by launch_app); Describe() skips handle on
IsNull; ProcessDebugger sets Session.DebugWriter -> app output. Tests added
(TestTarget Tick now has a null local + Debug/Console traces):
WaitForCurrentOrNextStop_ReturnsCurrentStop_WhenAlreadyStopped,
NullValue_HasNoExpansionHandle, AppTraces_GoToAppOutput_NotDebuggerOutput,
SetBreakpoint_BeforeLaunch_IsHitOnStartupCode_AndWaitReturnsCurrentStop.
Suite run 3 (14 tests, with redeploy): 14/14 green in 67 s, device clean.

## Next action if interrupted right now
User must republish the registered MCP server (register-mcp.cmd, with the
Claude Code session that holds the server closed - the dll is locked while
it runs). Then continue M3 on the reference application (main-process breakpoints on UI code,
multi-assembly, App.Background service). Suggest a commit.
Working tree uncommitted (docs, src, tests, TestTarget, DevTools, submodule
pointer) - suggest a commit to the user.

## Exact next steps (M1, after user approval of the API)
1. Core: `AndroidLauncher` (adb device list, install via msbuild `-t:Install`,
   resolve launcher activity, setprop debug.mono.extra with device-clock
   deadline, forward, am start, logcat capture).
2. Core: `DebugSession` facade over SoftDebuggerSession (state machine,
   stop-generation waits, breakpoint store, thread/frame snapshots, locals,
   value formatting).
3. Tests: harness that deploys TestTarget once, restarts the app per test,
   asserts breakpoint/locals (first green test = SdbProbe scenario).
4. MCP frontend: attach/launch, breakpoints, continue_and_wait, stack, locals.

## What works
- Solution builds (Core/Mcp/Tests/SdbProbe + 3 vendored libs, net10.0 only).
- SdbProbe: `SdbProbe --port 10000 --file <abs path> --line 45 --hits 2`
  against TestTarget after the manual recipe in ANDROID_ATTACH_NOTES.md.

## What is failing
- msbuild `-t:Run -p:AndroidAttachDebugger=true` on this machine writes an
  already-expired freshness deadline -> agent not started -> `DWP Handshake
  failed`. Not a blocker: the engine sets the property itself.

## Last test result
SdbProbe run 2026-08-20: exit 0, two hits on `.NET TP Worker`, locals
`this`, `message="tick 2 at 334534"`, `now=334534` read correctly.

## Traps / hypotheses
- C# 14 `field` keyword broke upstream FieldValueReference.cs; fixed in fork
  csm101/debugger-libs branch fix/csharp14-field-keyword (submodule points
  there), PR mono/debugger-libs#419 pending. When merged: point .gitmodules
  back to upstream, bump submodule, update ARCHITECTURE.md.
- Consumers must reference Mono.Cecil 0.10.1 explicitly (upstream
  PrivateAssets="all") or the first breakpoint hit throws FileNotFoundException
  and the session disconnects.
- `debug.mono.extra` is read at process start only: no late attach. Agent waits
  30 s then the process dies and Android respawns it while the deadline is
  fresh. Always reset the property when abandoning a session.
- Detach kills the app (observed). Engine must model detach == terminate until
  proven otherwise.
- Emulator was left running; TestTarget force-stopped; property cleared.

## Open items outside M0
- (none)
- Initial commit pushed to https://github.com/csm101/net-android-debugger (private); repo-local git author set to csm101 <carlo.sirna@gmail.com>.
