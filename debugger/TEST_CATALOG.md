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
- Unattended runs: `bash DevTools/scripts/ensure-emulator.sh` first — it starts
  the AVD headless (a windowed emulator cannot start while the desktop is
  locked) and clears the locks a crashed qemu leaves behind.
- 36 tests in four files, ~3 min on the headless emulator after deploy. Each
  test launches TestTarget afresh through `DebugSession`.
- Source lines are located by code markers (`TestEnvironment.LineOf`), never
  by hardcoded numbers.
- TestTarget is shared by every test: a member that is deliberately slow or
  throwing must live in its own method, or every test that touches that frame
  pays for it (see `SlowProbe` / `EvaluationProbe`).

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
- [x] Launch fails cleanly when the package is not installed (error names the
      package, session ends, property cleared) — `Launch_UnknownPackage_FailsCleanly`
- [x] Launch fails cleanly when the serial is unknown — `Launch_UnknownDeviceSerial_FailsCleanly`

## A2. Multi-process (port rotation)
- [x] Helper process (`:helper`) attached on the next port, breakpoint hit
      there — `Breakpoint_InHelperProcess_IsHit_ViaPortRotation`
- [x] A process started by Android (manifest receiver in `:late`) while the main
      process is stopped at a breakpoint is attached on its own port, and its
      own breakpoint is hit — `ProcessSpawnedWhileMainIsStopped_IsAttachedOnItsOwnPort`
      (also covers three processes holding three distinct ports)
- [ ] Helper that exits and is respawned by Android is re-attached on a new port
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
- [x] Step through async/await: stop on the line after the await, locals from
      before the await still readable, user frame in the stack, step stays in
      the method — `AsyncFrame_StopsAfterAwait_WithLocalsAndUserStack`
- [ ] Step over a call that raises an exception
- [x] Step in one process while another stays stopped and inspectable —
      `SteppingOneProcess_LeavesTheOtherStopped`

## D. Stack and threads
- [x] Call stack at breakpoint: user frame on top, external frames below —
      `CallStack_TopFrameIsUserCode_WithExternalFramesBelow`
- [x] Threads listed when stopped, including the stopping thread —
      `Threads_AreListed_WhenStopped`
- [x] Stop location is always the user's line, even when the stop is delivered
      inside an external call (JNI callee on top) —
      `StopLocation_IsAlwaysTheUserLine_EvenWhenStoppedInsideAnExternalCall`
- [x] Stack of a thread other than the stopping one (and the main thread of an
      idle Activity legitimately has no managed frames) —
      `CallStack_OfAnotherThread_IsReadable_WhenStopped`
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
      engine `RunBounded` (60 s); timeouts are 12 s/18 s so a first slow invoke
      (DateTime.ToString / ICU init) completes instead of being aborted.
- [x] Safe mode without debuggee invocation (`allowToStringCalls=false`,
      `allowTargetInvoke=false`): primitives, strings and object expansion still
      readable — `EvaluationOptions_WithoutToStringCalls_StillReadsValues`
- [x] An aborted slow invocation is reported as an error and the debugger stays
      responsive (the debuggee may or may not survive it — both outcomes are
      accepted) — `AbortedSlowInvoke_LeavesTheThreadUsable` (U11)
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
- [x] Only the first unhandled exception per process is reported; the ones the
      runtime raises while the process dies are resumed automatically, so one
      continue is enough — `UnhandledException_IsReported_ThenAppExits` (U12)
      Details are captured at stop time from the backtrace (the process dies
      right after); the message is best-effort and the stop backtrace is the
      dispatch frame, not the original throw site.
- [x] Exception type filtering: several types at once, and clearing the filters
      stops the exception stops — `ExceptionFilters_CanBeNarrowedAndCleared`

## H. Android specifics
- [x] Logcat/app output capture during session, as structured lines with
      level/tag/pid and filters — `AppTraces_GoToAppOutput_NotDebuggerOutput`
- [ ] Activity restart (rotation) mid-session behavior
- [ ] Attach over `adb connect` (WiFi device) — deferred (U9)

## I. MCP end-to-end (`McpEndToEndTests`, real server process over stdio via the SDK client)
- [x] Tool list contains the core tools — `ToolList_ContainsCoreTools`
- [x] Round-trip: launch_app → set_breakpoint → wait_until_stopped → get_locals →
      get_call_stack → evaluate_expression → get_compact_debug_snapshot →
      continue_and_wait → terminate_app —
      `Roundtrip_Launch_Breakpoint_Wait_Locals_Snapshot_Terminate`
- [x] Error paths return MCP errors with a usable message, never hang (no
      session; unknown device; nothing stopped; unknown pid; stale handle) —
      `ErrorPaths_ReturnToolErrors_NeverHang`
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
