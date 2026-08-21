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
- `sys.boot_completed` stays `1` when `system_server` has crashed and is coming
  back, and a run started then dies with `Can't find service: package`. The
  script's health check asks the package service itself, not just the property.
- 61 tests in five files, ~7-10 min on the headless emulator after deploy. Each
  test launches TestTarget afresh through `DebugSession`.
- Source lines are located by code markers (`TestEnvironment.LineOf`), never
  by hardcoded numbers.
- TestTarget is shared by every test: a member that is deliberately slow or
  throwing must live in its own method, or every test that touches that frame
  pays for it (see `SlowProbe` / `EvaluationProbe`).
- TestTarget's `Sample` gained a lazy `Sequence` property (a `yield` iterator)
  so the enumerable case has a debuggee to exercise; the suite must deploy
  once (drop `NAD_SKIP_DEPLOY`) after pulling this.
- A live MCP debug session on the same device breaks the suite: it owns
  `debug.mono.extra` (and rewrites it when `keepPropertyFresh` is on), so the
  tests' own launches lose the port. Terminate it first.
- Tests that watch a process die must watch the **pid**, not the name: Android
  restarts the app, and rotating the port at fork means those restarts are
  attached too, so a process with the same name reappears within seconds.
- The engine logs from background threads that can outlive a test; the fixture
  ignores the `InvalidOperationException` xUnit's output helper throws once its
  test is over, because an unhandled throw there kills the whole test host.

## A. Launch / attach lifecycle
- [x] Deploy + launch TestTarget on emulator, debugger attaches —
      `Launch_AttachesMainProcess_AndReportsRunning`
- [x] Terminate ends the session, stops the app, clears the property —
      `Terminate_EndsSession_AndStopsApp`
- [x] Launching an app that is already running restarts it under the debugger
      (attach == restart on Mono Android; the pid changes) —
      `LaunchingAnAlreadyRunningApp_RestartsItUnderTheDebugger`
- [x] Detach terminates the app, by design (the Mono runtime exits when the
      debugger disconnects) — `Detach_TerminatesTheApp_ByDesign`
- [x] An app that kills itself is reported as a process exit, and the session
      never claims to be stopped with nothing suspended —
      `AppDyingOnItsOwn_IsReportedAsProcessExit`
- [ ] Debugger disconnect mid-run (recovery behavior). Still uncovered, and
      `adb forward --remove tcp:<port>` is **not** a way to simulate it
      (tried 2026-08-21: the established connection survives, the session
      keeps running). `adb kill-server` would work but takes down every other
      adb client on this machine, so it is out. Needs the WiFi device of U9.
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
- [x] A killed sticky helper is reported gone and the process Android starts in
      its place is attached on a new port —
      `KilledHelperProcess_IsReportedGone_AndReattachedWhenAndroidRestartsIt`
- [x] `GetProcesses` reports a helper that died (`HasExited`) — same test
- [x] A process the app starts after the debug property's deadline runs without
      a debugger by default, and is attached when the session keeps the property
      fresh (`KeepPropertyFresh`) —
      `ProcessStartedAfterThePropertyExpired_IsAttached_OnlyWhenTheLifetimeIsKeptFresh`
      (~2 min: it waits for the property to go stale twice)
- [x] A process whose name has nothing to do with the package (component
      declared with a global `android:process`, as the reference application's
      `the app's own android:process` is) is recognised by uid and attached, not
      refused as foreign — `ProcessWithAGlobalName_IsRecognisedByUid_AndAttached`
- [x] A foreign Mono app process starting during the session is NOT attached
      (warning logged, port rotated, our own processes unaffected) —
      `ForeignMonoApp_StartingDuringTheSession_IsNotAttached` (waits for the
      engine to announce the refusal — a big foreign app on a cold emulator can
      take a minute to reach agent init; skips when no
      second .NET app is installed; abstains, with a note in the test output,
      when that app never reaches its Mono agent init — then the guard had
      nothing to refuse and the run proves nothing)

## B. Breakpoints
- [x] Source-line breakpoint hit with locals — `Breakpoint_InMainProcess_IsHit_WithLocals`
- [x] Continue hits again; generation increases —
      `ContinueAndWait_HitsSameBreakpointAgain_WithIncreasingGeneration`
- [x] Breakpoint set before launch resolves when the assembly loads (covered
      implicitly by all breakpoint tests; `Verified` asserted)
- [x] Conditional breakpoint — `ConditionalBreakpoint_StopsOnlyWhenConditionIsTrue`
- [~] Hit-count breakpoint — `HitCountBreakpoint_StopsAtNthHit` asserts "at
      least N hits": the count can restart when another process attaches (U13)
- [x] Breakpoint set while running is bound and hit — `SetBreakpoint_WhileRunning_IsBoundAndHit`
- [x] Setting a breakpoint waits for the runtime to bind it before reporting
      (binding is asynchronous: the immediate answer says "not bound" with a
      message that reads like a failure), and does not wait when no process is
      attached; a bound breakpoint carries no status message (in a multi-process
      app the first process to answer may be one where the assembly is not
      loaded) — `SetBreakpointAsync_WaitsForTheRuntimeToBindIt`
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
- [x] Step over a throw inside a try/catch lands in the catch, same method,
      session still usable — `StepOver_ACallThatThrows_StaysInTheMethod`
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
- [x] A null local of an async frame that the method has not reached yet has no
      expansion handle and does not claim to have children (Mono reports those
      as `(null)` without its null flag) —
      `AsyncFrame_StopsAfterAwait_WithLocalsAndUserStack`
- [x] Generic types display with their type arguments (List<int>,
      Dictionary<string,int>) — `ObjectExpansion_…`
- [x] Stuck debuggee invoke yields a bounded TimeoutException, never a hang —
      engine `RunBounded` (60 s); timeouts are 12 s/18 s so a first slow invoke
      (DateTime.ToString / ICU init) completes instead of being aborted.
- [x] Safe mode without debuggee invocation (`allowToStringCalls=false`,
      `allowTargetInvoke=false`): primitives, strings and object expansion still
      readable — `EvaluationOptions_WithoutToStringCalls_StillReadsValues`
- [x] An aborted slow invocation is reported as an error and the debugger stays
      responsive (the debuggee may or may not survive it — both outcomes are
      accepted) — `AbortedSlowInvoke_LeavesTheThreadUsable` (U11)
- [x] A value typed as an iterator (`IEnumerable`/`IEnumerable<T>`) shows the
      state machine's own fields; its elements are reachable through the extra
      group child Mono adds ("IEnumerator"), which the engine labels so they
      are not mistaken for absent — seen live on the reference application
      (`UnityContainer.Registrations`) —
      `IEnumerableValue_ExposesItsElements_UnderTheEnumeratorGroup`
- [x] Culture-invariant rendering (0.5 not 0,5) — enforced by frontends +
      test ModuleInitializer, asserted in `ObjectExpansion_…`

## F. Evaluate
- [x] Simple expression and member access — `Evaluate_InvalidExpression_IsErrorNotCrash`
      (`1 + 1`, `sample.Name`), `ConditionalBreakpoint_…`/`HitCountBreakpoint_…` (`_ticks`)
- [x] Method call with side effects. Policy (decided 2026-08-21): an expression
      is evaluated as written, side effects included — the evaluator invokes
      debuggee code for ordinary property reads anyway, so promising otherwise
      would be a lie. Refusing side effects means `allowTargetInvoke=false`,
      which disables invocation altogether —
      `Evaluate_WithSideEffects_MutatesTheDebuggee`
- [x] Invalid expression (unknown member, broken syntax) yields IsError, session
      survives — `Evaluate_InvalidExpression_IsErrorNotCrash`

## G. Exceptions
- [x] First-chance filter with type, message and stack via GetExceptionDetails —
      `FirstChanceExceptionFilter_StopsOnThrow_WithDetails`
- [x] Only the first unhandled exception per process is reported; the ones the
      runtime raises while the process dies are resumed automatically, so one
      continue is enough — `UnhandledException_IsReported_ThenAppExits` (U12)
      The process is *not* guaranteed to die afterwards (measured: still alive
      past 90 s in 2 of 8 runs), so the test accepts both outcomes and asserts
      the session stays coherent instead.
      Details are captured at stop time from the backtrace; the message is
      best-effort and the stop backtrace is the dispatch frame, not the
      original throw site.
- [x] Exception type filtering: several types at once, and clearing the filters
      stops the exception stops — `ExceptionFilters_CanBeNarrowedAndCleared`

## H. Android specifics
- [x] Logcat/app output capture during session, as structured lines with
      level/tag/pid and filters — `AppTraces_GoToAppOutput_NotDebuggerOutput`
- [x] Screen rotation recreates the Activity in the same process and the
      session follows it (OnCreate breakpoint hits again) —
      `ScreenRotation_RecreatesTheActivity_AndTheSessionSurvives`
- [x] App output timestamps are on the device clock whatever the channel
      (logcat stamps device local time, debugger-delivered stdout/stderr is
      produced on the host) —
      `AppOutput_TimestampsAreOnTheDeviceClock_WhateverTheChannel`
- [ ] Attach over `adb connect` (WiFi device) — deferred (U9)


## J. Source discovery
- [x] The runtime's own path for a source file is reported, by file name and by
      full path, with the types compiled from it; an unknown file comes back
      empty rather than guessed, and a blank query is rejected. This is how a
      pending breakpoint is diagnosed (wrong path vs type not loaded yet) —
      `GetSourceFiles_ReportsThePathTheRuntimeWasBuiltWith`

## K. DAP frontend (`DapEndToEndTests`, real adapter process over stdio)
- [x] `initialize` reports the capabilities a client needs and is followed by
      the `initialized` event — `Initialize_ReportsTheCapabilitiesAClientNeeds`
- [x] Editor-shaped round trip: setBreakpoints before launch → launch →
      configurationDone → stopped event → threads (each named with its pid) →
      stackTrace → scopes → variables → nested variables → evaluate → continue →
      disconnect —
      `Roundtrip_Launch_Breakpoint_Stack_Scopes_Variables_Evaluate_Continue_Disconnect`
- [x] `next` steps one line and the stop is reported with reason `step` —
      `Stepping_MovesToTheNextLine_AndReportsAStepStop`
- [x] Every failure is answered rather than leaving the client waiting: unknown
      request, missing `packageName`, stale frame id, stale variablesReference,
      evaluate with nothing stopped — and the adapter still serves requests —
      `ErrorPaths_AreAnswered_NeverLeaveTheClientWaiting`
- [x] A malformed message (wrong `Content-Length`, truncated body) is ignored
      and the adapter keeps serving — it used to crash the process —
      `MalformedInput_IsIgnored_AndTheAdapterKeepsServing`
- [~] Driven by a real editor: the VS Code extension in
      `DevTools/vscode/net-android-debugger` contributes the `net-android` debug
      type, and `node test-extension.js` checks its own logic (adapter path
      resolution, the missing-adapter message, configuration validation) against
      a stand-in for the `vscode` module. Nobody has yet run it inside a real
      VS Code against a real device — that part is still uncovered.
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
- [x] launch_app with deploy=true builds, installs and attaches —
      `LaunchWithDeploy_BuildsInstallsAndAttaches`
- [x] A second launch_app replaces the previous session (new pid, old one gone
      from the status) — `SecondLaunch_ReplacesTheFirstSession`
