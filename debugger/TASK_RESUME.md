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

## Committed
cb4f7d6 "M0 spike and M1 engine + MCP frontend" (2026-08-20). Server
republished by the user via register-mcp.cmd.

## M3 drive #2 on the reference application (2026-08-20, new server)
- set_breakpoint before launch on AppApplication.cs:91 (startup code, main
  process) and on App.Core/.../ControlloNumeratoriProgressiviImpl.cs:62
  (different assembly) -> attach_to_app -> bp1 hit on main (thread 4, TP),
  bp2 pending until App.Core.dll loaded -> continue -> bp2 hit on thread 1
  (the main/UI thread), locals with nulls (no handles), `this` expanded
  (Unity container, List Count = 0, ...). Helper process auto-attached on
  10001 meanwhile. terminate -> clean.
- Polish found: thread 1 has an empty name; should be labelled as the main
  thread (TEST_CATALOG D "Main/UI thread identified" still open).

## M2 depth (in progress, user delegated the choice: proceed autonomously)
Done in code (uncommitted): main-thread label ("Main" for unnamed id 1,
"Thread N" otherwise); TestTarget Tick extended (Sample object with enum,
List, array, nested, property; Describe() call for step into/out; caught
InvalidOperationException every 5th tick). New tests:
tests/InspectionAndBreakpointTests.cs (MainThread label, conditional bp,
hit-count bp, bp while running, remove-all, pause, step into/out,
first-chance exception filter + details, object expansion, invalid
expression) and McpEndToEndTests.ErrorPaths_ReturnToolErrors_NeverHang.
Run 4: 20/25 (5 red). All 5 fixed (run 5 in flight):
1. AttachProcessAsync deduped per pid (Dictionary _attaching); process added
   to _processes only after handshake -> LaunchAsync awaits the real
   connection (was declaring Running while main still connecting -> pause NRE).
2. Exception message: StopEvent carries only Type (cheap); ExceptionInfo.Message
   returns "Loading..." while evaluating, resolved with a bounded poll in
   GetExceptionDetails (off the event thread). Test asserts message via
   GetExceptionDetails, type via StopEvent.
3. Evaluate wraps GetExpressionValue in try/catch (Mono throws
   NotSupportedException on unknown identifier) -> error VariableSnapshot.
4. Enum: Value is type-qualified, DisplayValue is bare member; test asserts
   DisplayValue.
5. MCP tool errors: SessionHost throws McpException -> surfaced as
   isError=true with the message (verified by stdio smoke test).

## Runs 5-6
Run 5: 23/25. Fixed the two: culture-invariant formatting (MCP Program.cs +
tests ModuleInitializer force InvariantCulture; "0,5" vs "0.5" on it-IT) and
Evaluate hardened (ValidateExpression up front; result must be a concrete
value - IsConcreteValue checks KindMask Object/Array/Primitive or IsNull;
still try/catch). Test now uses `sample.NoSuchMember` and `1 +` instead of
`noSuchVariable + 1` (that one resolves as a namespace and killed the
debuggee - engine now survives it, but it is not a representative case).
Run 6: 15/25 but 8 failures = emulator-5554 CRASHED mid-run (qemu
EXCEPTION_ACCESS_VIOLATION in nvoglv64.dll path, x:\crash_emulatore.txt;
NOT a debugger bug). Emulator relaunched with `-gpu swiftshader_indirect`
(software GPU, avoids the NVIDIA GL path) - use that flag from now on.
Run 7: HUNG after 1 test. Mono logged "Aborting invocation of method
String System.DateTime:ToString () on object System.DateTime" (invoke
timeout on the slow software-GPU emulator), then a synchronous
Mono.Debugging inspection call never returned -> vstest stuck 30 min; the
test-runner killed the tree (including the live registered MCP server -
user must restart the Claude session / republish to get the tools back)
and left the device dirty (cleaned by me: prop, app, 5 forwards).
Fix: DebugSession.RunBounded(20 s) wraps every synchronous inspection call
(GetLocals, GetVariable, Evaluate, ExpandVariable, GetCallStack,
GetThreads, GetExceptionDetails): a stuck debuggee invoke now costs one
leaked thread-pool thread and a TimeoutException instead of hanging the
caller forever. Run 8 in flight.

Run 8: 25/25 GREEN in 2.5 min on the software-GPU emulator; device clean.
TEST_CATALOG updated (A/B/C/D/E/F/G/I). Committing.

## Next action if interrupted right now
Ask nothing: proceed. Next chunk (M2 leftovers, all emulator-only):
SetBreakpoints replace-per-file test, breakpoint on no-code line, Dictionary
expansion, unhandled-exception scenario (dedicated TestTarget hook),
second-thread call stack, launch error paths (bad package / offline serial).
Then: logcat filtering (per-pid app output), SourceResolver, the reference application U6
pause behavior. User should rerun register-mcp.cmd to republish the fixed
server when convenient.
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
