# Test Catalog

Living index of what the automated suite covers and what is still uncovered.

Status legend:
- `[x]` covered by an automated test in `tests/NetAndroidDebugger.Tests`
- `[ ]` known gap — add a TestTarget hook + assertion before fixing any bug in
        this area
- `[~]` partially covered

Conventions (mirroring the Delphi project's discipline):
- Every brainstormed edge case becomes a **named test**, never prose.
- Runnable-and-green: plain `[Fact]`.
- Real known bug: `[Fact(Skip = "TODO-RED: <root cause>")]`.
- Not yet feasible: stub whose body is `Assert.Fail("not implemented")` plus
  `Skip = "TODO: ..."` — removing Skip forces a real implementation.
- Update this catalog in the same change set as the test or fix.

---

## Running the suite

- Needs a booted device/emulator with TestTarget deployable. Select it with
  `NAD_DEVICE_SERIAL=<serial>` (mandatory when more than one device is
  attached; the suite never relies on the adb default device).
  `NAD_SKIP_DEPLOY=1` skips the one-time `-t:Install` of TestTarget.
- `LaunchAndBreakpointTests` (tests/): 8 tests, ~35 s on the emulator after
  deploy. Each test launches TestTarget afresh through `DebugSession`.
- Source lines are located by code markers (`TestEnvironment.LineOf`), never
  by hardcoded numbers.

## A. Launch / attach lifecycle
- [x] Deploy + launch TestTarget on emulator, debugger attaches —
      `Launch_AttachesMainProcess_AndReportsRunning`
- [x] Terminate ends the session, stops the app, clears the property —
      `Terminate_EndsSession_AndStopsApp`
- [ ] Attach to already-running debuggable app — not possible on Mono Android
      (property read at process start); attach == restart with agent. Test
      that `LaunchAsync` on an already-running app restarts it cleanly.
- [ ] Detach leaves app running — known impossible (agent kills the app);
      name: `Detach_TerminatesApp_ByDesign`
- [ ] App exit is reported as session end (e.g. app calls `Process.KillProcess`)
- [ ] Debugger disconnect mid-run (recovery behavior)
- [x] Pause stops a running process with reason Pause —
      `Pause_StopsRunningProcess_AndReportsPauseReason`
- [ ] Launch fails cleanly when the package is not installed (error, no hang)
- [ ] Launch fails cleanly when the device is offline / serial wrong

## A2. Multi-process (port rotation)
- [x] Helper process (`:helper`) attached on the next port, breakpoint hit
      there — `Breakpoint_InHelperProcess_IsHit_ViaPortRotation`
- [ ] Helper spawned while main is stopped at a breakpoint is still attached
- [ ] Helper that exits and is respawned by Android is re-attached on a new port
- [ ] Three processes (main + two helpers) get three distinct ports
- [ ] `GetProcesses` reports a helper that died (`HasExited`)
- [ ] A foreign Mono app process starting during the session is NOT attached
      (warning logged, port rotated) — needs a second installed .NET app;
      `ForeignMonoProcess_IsNotAttached` (observed live with the reference application, suite run 12)

## B. Breakpoints
- [x] Source-line breakpoint hit with locals — `Breakpoint_InMainProcess_IsHit_WithLocals`
- [x] Continue hits again; generation increases —
      `ContinueAndWait_HitsSameBreakpointAgain_WithIncreasingGeneration`
- [x] Breakpoint set before launch resolves when the assembly loads (covered
      implicitly by all breakpoint tests; `Verified` asserted)
- [ ] Breakpoint set while running (after launch) is bound and hit
- [x] Conditional breakpoint — `ConditionalBreakpoint_StopsOnlyWhenConditionIsTrue`
- [x] Hit-count breakpoint — `HitCountBreakpoint_StopsAtNthHit`
- [x] Breakpoint set while running is bound and hit — `SetBreakpoint_WhileRunning_IsBoundAndHit`
- [x] Remove-all while stopped — no further hits — `RemoveAllBreakpoints_WhileStopped_NoFurtherHits`
- [x] Breakpoint on a comment line — bound to the next statement or pending,
      no crash, session stays usable — `Breakpoint_OnCommentLine_DoesNotCrash_SessionStaysUsable`
- [x] `SetBreakpoints` replaces all breakpoints of a file (empty list clears) —
      `SetBreakpoints_ReplacesAllBreakpointsOfTheFile`

## C. Stepping
- [x] Step over to the next line in the same method —
      `StepOver_AdvancesToNextLine_InSameMethod`
- [x] Step into / step out at a plain call site —
      `StepInto_EntersCallee_AndStepOut_ReturnsToCaller`
- [ ] Step through async/await
- [ ] Step over a call that raises an exception
- [ ] Step in one process while another process is stopped

## D. Stack and threads
- [x] Call stack at breakpoint: user frame on top, external frames below —
      `CallStack_TopFrameIsUserCode_WithExternalFramesBelow`
- [x] Threads listed when stopped, including the stopping thread —
      `Threads_AreListed_WhenStopped`
- [x] Stop location is always the user's line, even when the stop is delivered
      inside an external call (JNI callee on top) —
      `StopLocation_IsAlwaysTheUserLine_EvenWhenStoppedInsideAnExternalCall`
- [ ] Stack of a thread other than the stopping one
- [x] Main/UI thread identified ("Main" label, OnCreate stops on it) —
      `MainThread_IsLabelledMain_AndOnCreateRunsOnIt`; unnamed threads get
      "Thread N"

## E. Locals and values
- [~] Primitives (long, string, double asserted; char/decimal/DateTime display
      not yet pinned) — `Breakpoint_InMainProcess_IsHit_WithLocals`,
      `ObjectExpansion_ShowsProperties_Enum_List_Array_Nested`
- [x] Enums (Value type-qualified, DisplayValue bare member) — `ObjectExpansion_…`
- [x] Object expansion (properties, nested object) — `ObjectExpansion_…`
- [x] Arrays / List<T> expansion — `ObjectExpansion_…`
- [x] Dictionary<K,V> expansion — `DictionaryExpansion_ShowsEntries`
- [x] Null locals: no expansion handle — `NullValue_HasNoExpansionHandle`
- [ ] Generic types display
- [x] Stuck debuggee invoke yields a bounded TimeoutException, never a hang —
      engine `RunBounded` (60 s). Root cause understood: a debuggee invoke
      ABORTED on `EvaluationTimeout` wedges the stopped thread; timeouts raised
      to 12 s/18 s so the first slow invoke (DateTime.ToString / ICU init)
      completes instead (runs 7-15).
- [ ] `set_evaluation_options(allowToStringCalls=false)` makes a DateTime
      render as a struct without invoking — `EvaluationOptions_NoToString_RendersWithoutInvoke`
- [x] An aborted slow invocation leaves the thread usable (evaluation,
      expansion and continue all keep working; the slow member shows as
      `[error]`) — `AbortedSlowInvoke_LeavesTheThreadUsable` (answers U11)
- [~] Dictionary<K,V> expansion — `DictionaryExpansion_ShowsEntries` (flaky on
      the software-GPU emulator: depends on the first DateTime.ToString not
      exceeding the invoke timeout, see U11)
- [x] Culture-invariant rendering (0.5 not 0,5) — enforced by frontends +
      test ModuleInitializer, asserted in `ObjectExpansion_…`

## F. Evaluate
- [x] Simple expression and member access — `Evaluate_InvalidExpression_IsErrorNotCrash`
      (`1 + 1`, `sample.Name`), `ConditionalBreakpoint_…`/`HitCountBreakpoint_…` (`_ticks`)
- [ ] Method call with side effects (policy: AllowTargetInvoke=true; document limits)
- [x] Invalid expression (unknown member, broken syntax) yields IsError, session
      survives — `Evaluate_InvalidExpression_IsErrorNotCrash`

## G. Exceptions
- [x] First-chance filter with type, message and stack via GetExceptionDetails —
      `FirstChanceExceptionFilter_StopsOnThrow_WithDetails`
- [x] Unhandled exception reported with type + stack trace, then the app exits —
      `UnhandledException_IsReported_ThenAppExits` (details captured at stop
      time from the backtrace, since the process dies right after; message is
      best-effort, the stop backtrace is the dispatch frame not the throw site)
- [~] Exception type filtering (single type verified; multiple types + clear open)

## H. Android specifics
- [ ] Logcat/app output capture during session
- [ ] Activity restart (rotation) mid-session behavior
- [ ] Attach over `adb connect` (WiFi device) — deferred (U9)

## I. MCP end-to-end (`McpEndToEndTests`, real server process over stdio via the SDK client)
- [x] Tool list contains the core tools — `ToolList_ContainsCoreTools`
- [x] Round-trip: launch_app → set_breakpoint → wait_until_stopped → get_locals →
      get_call_stack → evaluate_expression → get_compact_debug_snapshot →
      continue_and_wait → terminate_app —
      `Roundtrip_Launch_Breakpoint_Wait_Locals_Snapshot_Terminate`
- [ ] Error paths return MCP errors, never hang (no session; unknown pid; bad expression)
- [x] `set_breakpoint` before `launch_app` is accepted, bound at launch and hit
      on startup code (OnCreate); `wait_until_stopped` without afterGeneration
      returns the current stop when already stopped —
      `SetBreakpoint_BeforeLaunch_IsHitOnStartupCode_AndWaitReturnsCurrentStop`
- [x] Core: `WaitForCurrentOrNextStop_ReturnsCurrentStop_WhenAlreadyStopped`
- [x] Null values carry no expansion handle — `NullValue_HasNoExpansionHandle` (E)
- [x] App trace lines (Debug.WriteLine / Console) appear in app output, not
      debugger output — `AppTraces_GoToAppOutput_NotDebuggerOutput` (H)
- [ ] launch_app with deploy=true redeploys and launches
- [ ] Second launch_app closes the previous session cleanly
