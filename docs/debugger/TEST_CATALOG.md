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
- Unattended runs: `AVD=<name> bash DevTools/scripts/ensure-emulator.sh` first
  — it starts the AVD headless (a windowed emulator cannot start while the
  desktop is locked) and clears the locks a crashed qemu leaves behind. `AVD`
  can be omitted only when one AVD is installed: with several the script lists
  them and stops, the same rule the engine applies to devices.
- `sys.boot_completed` stays `1` when `system_server` has crashed and is coming
  back, and a run started then dies with `Can't find service: package`. The
  script's health check asks the package service itself, not just the property.
- A guard that is right elsewhere can be wrong here: `RunBounded` blocks a
  thread waiting on another thread-pool thread, which is fine on a frontend
  request and starves the pool inside the rule worker, where it runs once per
  first-chance exception. Measured 2026-08-23: eight MCP tests dead at 2m05s
  each and the whole run at 35 min instead of 15. Only a full run shows it -
  the same tests pass in isolation.
- A qemu instance that never registers with adb is invisible to `adb devices`,
  so `adb emu kill` cannot clear it, and the next launch of the same AVD fails
  with "Running multiple emulators with the same AVD". The script now kills it by
  its qemu PID, matched on the AVD name (2026-08-23).
- 139 tests in ten files, ~15-17 min on the headless emulator after deploy. Each
- Stability, measured 2026-08-21: three consecutive full runs, 62/62 each
  (7m00s, 7m18s, 7m52s), no failures and none of the failure signatures the
  day's race fixes were aimed at. Getting those three took four attempts: one
  was lost when the emulator dropped mid-run, which is the environment, not the
  suite. Budget for roughly one lost run in four.
  test launches TestTarget afresh through `DebugSession`.
- `WaitForCurrentOrNextStop_ReturnsCurrentStop_WhenAlreadyStopped` failed once
  in a full run (the breakpoint resolved, then the app exited before the stop was
  reported) and passed in the confirmation run and three times in isolation. Not
  explained; if it returns, the question is whether the app died on its own or
  the stop was lost.
- On the `pixel_7_-_api_30` image Gboard crash-loops (ML Kit initializer) the
  moment a text field gets focus, and Android's "keeps stopping" dialog then
  covers the activity and owns the UI hierarchy. `DeviceControlTests` and the
  MCP screen test close that dialog when they see it; the durable fix on that
  emulator is `adb shell ime disable com.google.android.inputmethod.latin/com.android.inputmethod.latin.LatinIME`
  (`input text` needs no IME). Applied on this machine 2026-09-05.
- `ABreakpointHitByAnotherThread_DuringAStep_IsStillReported` failed once in a
  full run on the api_30 emulator (2026-09-05, 169/170): the `SLOW_STEP`
  broadcast's breakpoint never hit within its 40 s, and the session log shows
  both processes attached and nothing else. It passed twice in isolation right
  after and in the two full runs before. Not explained; if it returns, the
  question is whether the broadcast was delivered at all.
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

- [x] A logcat stream started at the buffer's own boundary does not replay a
      line logged before it, even where `logcat -c` leaves the buffer readable
      (Android 11) — `StreamLogcat_StartedSinceNow_DoesNotReplayLinesLoggedBefore_EvenWhenClearIsIneffective`
      (`LogcatTests`)
- [x] Three launches in a row each attach to their own new process, never to
      the previous session's dead pid (the alternating handshake failure of
      2026-09-05) — `Launch_RightAfterAPreviousSession_AttachesToTheNewProcess_NotTheDeadOne`

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
- [x] Logpoint: the app is not suspended, the message is traced to the debugger
      output with each `{expression}` evaluated in place, and several hits are
      traced because the app keeps running — `Logpoint_TracesWithoutStopping`,
      and through the server `Logpoint_TracesToDebuggerOutput_WithoutStoppingTheApp`
- [x] `%N` hit condition stops on a multiple of N —
      `HitCondition_EveryNthHit_StopsOnAMultiple`
      (asserted on the engine's own `hit count now N` diagnostic, not on the
      app's `_ticks`: those can legitimately disagree, because `Tick` runs on a
      `System.Threading.Timer` that does not serialise its callbacks and
      `_ticks++` is not atomic — the `_ticks` version failed once under load,
      see U13)
- [x] A hit condition that is not one of the spellings is rejected with the ones
      that work — `HitCondition_Nonsense_IsRejectedWithTheSpellingsThatWork`
- [~] Hit-count breakpoint — `HitCountBreakpoint_StopsAtNthHit` asserts "at
      least N hits". Six consecutive runs were exact after the port-rotation
      work; the engine logs `hit count now N (stops at M)` at every such stop, so
      a recurrence says immediately whether the count reset or hits were missed
      (U13)
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
- [x] Step over advances one line in the same method, on the thread asked for —
      `StepOver_AdvancesToNextLine_InSameMethod`. The breakpoint is removed
      before stepping on purpose: `Tick` is called by a `System.Threading.Timer`,
      which does not serialise its callbacks, so an armed breakpoint there lets a
      second thread re-enter `Tick` at the resume and report its breakpoint
      before the step completes. That happened once, under load.
- [x] A breakpoint another thread hits *during* a step is still reported —
      `ABreakpointHitByAnotherThread_DuringAStep_IsStillReported`. Provoking it
      needed a TestTarget receiver holding one deliberately slow line
      (`SlowStepReceiver`, marker `slow-step-line`), because everything is
      suspended while stopped: the race window lasts exactly as long as the step.
      Answered: `StepOverAsync` returns that breakpoint itself — reason
      `Breakpoint`, on the thread that hit it, not the thread being stepped.
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
- [x] Exception filters arrive as `filterOptions` with `filters` left empty (the
      shape a client uses once a filter advertises `supportsCondition`), and the
      condition selects the types to stop on —
      `ExceptionFilters_ArriveAsFilterOptions_AndSelectTheTypes`. Verified to
      fail when `filterOptions` is ignored, which is the silent no-op the Delphi
      debugger hit under real VS Code.
- [x] The legacy `filters` array still selects — `ExceptionFilters_LegacyFiltersArray_StillSelectsAll`
- [x] Events arrive in the order they happened: repeated continue/stop cycles
      with app output flowing never interleave a `continued` after the `stopped`
      that followed it — `Events_ArriveInTheOrderTheyHappened`
- [x] A malformed message (wrong `Content-Length`, truncated body) is ignored
      and the adapter keeps serving — it used to crash the process —
      `MalformedInput_IsIgnored_AndTheAdapterKeepsServing`
- [~] Driven by a real editor: the VS Code extension in
      `vscode/net-android-debugger` contributes the `net-android` debug
      type, and `node test-extension.js` checks its own logic (adapter path
      resolution, the missing-adapter message, configuration validation) against
      a stand-in for the `vscode` module. `vscode\install-vscode-extension.cmd` installs
      it, and section R guards what that script names. Nobody has yet run the
      extension inside a real VS Code against a real device — that part is still
      uncovered.

## L. Exception rules (per-exception engine)
- [x] A noisy exception is let through while the app keeps running —
      `ExceptionRule_Ignore_LetsTheNoisyOneThrough`
- [x] `log` reports it to the debugger output without stopping —
      `ExceptionRule_Log_ReportsWithoutStopping`
- [x] First match wins, so a specific ignore before a general break lets one
      exception through and still stops on the next —
      `ExceptionRules_FirstMatchWins_SoTheGeneralRuleStillBreaks`
- [x] Matching on the message works, which needs a call into the debuggee and so
      is decided off the event thread — `ExceptionRule_MatchingOnMessage_Works`
- [x] A rule that does NOT match leaves the stop alone (without this, the ignore
      tests would pass even if the engine ignored everything) —
      `ExceptionRule_ThatDoesNotMatch_LeavesTheStopAlone`
- [x] A bad regex is rejected when the rule is set, not on the first exception —
      `ExceptionRule_BadRegex_IsRejectedWhenItIsSet`
- [x] Through the server: rules parsed, read back in order, and applied —
      `ExceptionRules_ParsedAndApplied_ThroughTheServer`
- [x] Bad rule input is refused naming what was expected, and leaves the rules
      in force alone — `ExceptionRules_BadInput_IsRejectedWithWhatWasExpected`
- [x] The shared rules file is re-read on resume, so a rule edited while the app
      is stopped governs what happens next —
      `GlobalExceptionRules_AreReReadOnResume`
- [x] Session rules win over the shared file, which is the machine-wide baseline
      — `SessionRules_WinOverTheSharedFile`
- [x] A half-written file (the normal state while it is being edited) keeps the
      rules already in force and says what could not be read —
      `GlobalExceptionRules_BrokenFile_KeepsWhatWasInForce`
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
- [x] Every inspection tool answers in a stopped session (threads, current
      location, one variable, loaded assemblies, source files, breakpoint list,
      app output, step into/out) — `EveryInspectionTool_AnswersInAStoppedSession`
- [x] `get_loaded_assemblies` reports whether each assembly has symbols, and the
      app's own assembly has them — the first thing to check when a breakpoint
      stays pending, since no line in an assembly without symbols can ever bind
      (asserted inside `EveryInspectionTool_AnswersInAStoppedSession`)
- [x] The setup tools take effect and are visible through the server
      (`set_breakpoints` replacing a file's breakpoints, evaluation options,
      exception filters, remove-all) —
      `SetupTools_TakeEffect_AndAreVisibleThroughTheServer`
- [x] `remove_breakpoint` by id, and `pause_execution` on a running app —
      `RemoveBreakpointById_AndPause_WorkThroughTheServer`
- [x] Every way a session ends: attach_to_app, detach_debugger, stop_debugging —
      `LifecycleTools_EndTheSession_HoweverItIsAskedFor`
- [x] The shared rules file is attached and detached through the server, and a
      file that is not JSON is that call's error rather than a surprise later —
      `GlobalExceptionRulesFile_IsAttachedAndDetached_ThroughTheServer`
- [x] `launch_from_config` launches what the project describes, applies the
      exception rules the configuration carries, and honours the deviceSerial
      override — the configuration deliberately names a device that does not
      exist, so the run only succeeds if the override reached the launcher; an
      unknown configName is that call's error before anything is launched —
      `LaunchFromConfig_LaunchesWhatTheProjectDescribes_RulesAndOverrideIncluded`
- [x] `launch_app` given only where to look deduces the package from the
      `.csproj`, and `list_app_projects` lists what could be launched —
      `LaunchApp_DeducesThePackageFromTheProject_AndTheProjectsAreListable`
- [x] A launch with nothing to deduce from (no project named; a tree with no
      Android application) is the caller's error, with the fix in the message —
      `LaunchApp_WithNothingToDeduceFrom_SaysWhatIsMissing`
- [x] A snapshot is folded into a resume or a step only when asked for: reading
      locals invokes code in the debuggee and disarms breakpoints for the
      duration, so nobody pays for it silently —
      `ContinueAndStep_FoldInASnapshot_OnlyWhenAskedFor`
- [x] **No tool vanishes either**: the expected tool names are spelled out and
      compared with what the server exposes. A tool that disappears is otherwise
      invisible — the build passes and only whichever test happened to call it
      fails, with "Unknown tool", which reads like a client problem. That is how
      `set_evaluation_options` went missing for a while —
      `ToolSurface_IsExactlyThis`
- [x] **No tool ships uncovered**: the suite lists the server's tools and fails
      if one is never called through it — a wrong parameter name or a rendering
      that throws is invisible to the Core tests —
      `EveryTool_IsExercisedSomewhereInThisSuite`

## M. Launch configuration (`LaunchConfigTests`, no device)
- [x] A file as VS Code writes it — comment header, trailing comma,
      `${workspaceFolder}` in a path — is read, and what it does not state keeps
      the same defaults as `launch_app` rather than a second set —
      `AFileAsVsCodeWritesIt_IsRead_CommentsTrailingCommasAndVariablesIncluded`
- [x] `${workspaceFolder}` is the project root, not the `.vscode` folder holding
      the file: otherwise every relative path resolves one level too deep and
      surfaces as a missing `.csproj` much later —
      `WorkspaceFolder_IsTheProjectRoot_NotTheDotVscodeFolderHoldingTheFile`
- [x] `configName` picks a configuration, and an unknown one lists what the file
      actually holds —
      `ConfigName_PicksTheConfiguration_AndAnUnknownOneListsWhatTheFileHolds`
- [x] Another debugger's configurations are skipped, and named with their type
      when asked for explicitly —
      `ConfigurationsOfAnotherDebugger_AreSkipped_AndNamedIfAskedForByName`
- [x] `${command:...}` is refused by name: only VS Code can answer it, and a
      device picker reaching adb as a serial would fail far from its cause —
      `VariablesOnlyVsCodeCanAnswer_AreRefusedByName`
- [x] `${env:VAR}` is expanded — `EnvironmentVariables_AreExpanded`
- [x] A configuration missing `packageName` says which configuration and what is
      missing — `AConfigurationMissingThePackage_SaysWhichOneAndWhatIsMissing`
- [x] Exception rules travel with the configuration, and a relative
      `globalExceptionRulesPath` resolves against the project —
      `ExceptionRules_TravelWithTheConfiguration`
- [x] A bad rule names the configuration holding it, not just "rule 2" —
      `ABadExceptionRule_NamesTheConfigurationItIsIn`
- [x] A single hand-written object is a configuration, so using this outside VS
      Code does not mean writing the launch.json envelope —
      `ASingleHandWrittenObject_IsAConfiguration`
- [x] A configuration without `deviceSerial` is valid: the serial names a machine,
      not the app, so the caller resolves it —
      `AConfigurationWithoutADeviceSerial_IsValid_AndLeavesItToTheCaller`,
      and end to end `LaunchFromConfig_WithoutADeviceSerial_ResolvesOneAnyway`
- [x] A serial that was named but is not attached says where it came from, since
      "which of the three places do I fix" is the real question —
      `ASerialThatIsNotAttached_NamesWhereItCameFrom`
- [x] A missing file says where it looked — `AMissingFile_SaysWhereItLooked`
- [x] A file that is not JSON names the file — `AFileThatIsNotJson_NamesTheFile`

## N. Choosing what to launch (`AppProjectFinderTests`, `DeviceChoiceTests`, no device)
- [x] Android libraries are not launchable, only applications are — the filter
      that turns the reference application's thirty-odd Android projects into two —
      `AndroidLibraries_AreNotLaunchable_OnlyApplicationsAre`
- [x] Non-Android projects are not launchable — `NonAndroidProjects_AreNotLaunchable`
- [x] A multi-targeted project is found by its Android framework —
      `AProjectTargetingSeveralFrameworks_IsFoundByItsAndroidOne`
- [x] Several applications fail with the list rather than a guess: launching the
      wrong app looks exactly like the debugger not working —
      `SeveralApplications_FailWithTheList_RatherThanAGuess`
- [x] No application at all says what makes one launchable —
      `NoApplicationAtAll_SaysWhatMakesOneLaunchable`
- [x] A solution narrows the search to the projects it names, which is how an
      ambiguous tree is disambiguated —
      `ASolution_NarrowsTheSearchToTheProjectsItNames`, `ASlnxSolution_IsReadToo`
- [x] Projects a solution names are listed first —
      `ProjectsASolutionNames_ComeFirst`
- [x] An Exe whose ApplicationId is set outside the project file is still
      listed, and says the id is missing rather than showing a blank —
      `AnExeWithoutApplicationId_IsListed_AndSaysTheIdIsMissing`
- [x] `bin`/`obj` are not walked — `BuildOutputIsNotWalked`
- [x] A path that is neither solution nor project says so —
      `APathThatIsNeitherSolutionNorProject_SaysSo`
- [x] This repository yields exactly TestTarget — a real tree with real noise —
      `ThisRepository_YieldsTestTarget`
- [x] One ready device is not a choice — `OneReadyDevice_IsNotAChoice`
- [x] Two ready devices fail listing them: two emulators online is the normal
      state on this machine, and one belongs to another tool —
      `SeveralReadyDevices_FailListingThem`
- [x] A device that is not ready is not a candidate; nothing ready says what is
      attached; nothing attached says to start one —
      `ADeviceThatIsNotReady_IsNotACandidate`, `NothingReady_SaysWhatIsAttached`,
      `NoDeviceAtAll_SaysToStartOne`
- [x] A requested serial that is not attached lists what is — the typical stale
      copy from an earlier session — `ARequestedSerialThatIsNotAttached_ListsWhatIs`
- [x] A named device is used as given — `ANamedDevice_IsUsedAsGiven`

## N2. Finding adb (`AdbLocatorTests`, no device except the last one)

adb is not on PATH on a machine set up by Visual Studio, and the engine used to
assume it was. Every source is injected, so what is tested is the order and the
rule that a named source which is wrong stops the search.

- [x] An explicit path wins and may name the exe, its folder, or the SDK —
      `ExplicitPath_Wins_AndAcceptsTheExe_ItsFolder_OrTheSdk`
- [x] An explicit path that is wrong is an error even when PATH would do —
      `ExplicitPath_ThatIsWrong_IsAnError_NotAFallback`
- [x] `NAD_ADB_PATH` beats the SDK variables and is an error when stale —
      `NadAdbPath_Wins_OverTheSdkVariables_AndIsAnErrorWhenWrong`
- [x] `ANDROID_HOME`, `ANDROID_SDK_ROOT`, the registry, the default folders,
      then PATH, in that order — `SdkVariables_Registry_Defaults_ThenPath_InThatOrder`
- [x] Nothing found: the message lists every source tried —
      `NothingFound_ListsEverySourceTried`
- [x] On the machine running the suite, adb is found with PATH emptied
      (the registry key or the default folder is enough) —
      `OnThisMachine_AdbIsFound_WithoutPath`
- [x] `adbPath` in launch.json is read and made absolute (in
      `LaunchConfigTests.AFileAsVsCodeWritesIt_IsRead_CommentsTrailingCommasAndVariablesIncluded`)
- Verified 2026-09-05: the launch and MCP end-to-end tests pass from a shell
  whose PATH has no adb; the log reads `adb: ...\adb.exe (from the .NET Android
  workload's registry key)`.

## O. Sharing the device with another debugger (`DebugPropertyTests`, no device)
`debug.mono.extra` is device-global, so two debuggers overwrite each other in
silence and the loser's app hangs for the agent timeout on a port nobody listens
on — a symptom nowhere near its cause.
- [x] No property set is nothing to say — `NoPropertySet_IsNothingToSay`
- [x] An expired property is not a conflict: every Mono process ignores a
      deadline in the past, so warning would be noise on every second launch —
      `AnExpiredProperty_IsNotAConflict`
- [x] A fresh one says which port and for how long —
      `AFreshProperty_SaysWhichPortAndForHowLong`
- [x] Anything without a readable deadline is not judged, rather than crying
      wolf — `APropertyWithoutAReadableDeadline_IsNotJudged`
- [x] A fresh value whose port cannot be read is still reported —
      `AFreshPropertyWithAnUnreadablePort_IsStillReported`
- [x] End to end: a foreign fresh property is reported before being taken over,
      and the launch still succeeds (the warning informs, it does not block) —
      `ADebugPropertyLeftByAnotherDebugger_IsReportedBeforeItIsTakenOver`

## P. Third-party notices (`ThirdPartyNoticesTests`, no device)
- [x] Every assembly the frontends ship is named in THIRD-PARTY-NOTICES.txt — a
      dependency arriving without a notice is a licence breach at distribution,
      and nothing else in the build would say so: the reference is transitive,
      the assembly lands in the output folder, and everything keeps working —
      `EveryShippedAssembly_IsNamedInTheNotices`
- [x] The licence texts the file refers to are reproduced in full, Apache-2.0
      included (the short header most packages carry is not the licence) —
      `TheLicenceTextsReferredTo_AreReproducedInFull`

## Q. Where an exception rule gets decided (`ExceptionRuleDecisionTests`, no device)
Reading anything out of the debuggee is forbidden on Mono's event thread, so this
predicate is what sends a decision to a worker. Getting it wrong either wedges
the event thread or matches rules against data that is not there.
- [x] A catch-all rule decides on the event thread, with or without a type —
      `ACatchAllRule_DecidesOnTheEventThread`
- [x] A rule on the message always needs the debuggee —
      `ARuleOnTheMessage_AlwaysNeedsTheDebuggee`
- [x] A rule on the type needs it **only** when the stop carried no type — the
      fix for U15: a throw site without symbols leaves the type empty, and
      matching "Mqtt" against "" silently ignores the rule, on exactly the
      third-party exceptions rules exist to silence —
      `ARuleOnTheType_NeedsTheDebuggee_OnlyWhenTheStopCarriedNoType`
- [x] A rule on the raise site never needs it (the file comes from the backtrace
      the stop already carries) — `ARuleOnTheSourceFile_NeverNeedsTheDebuggee`
- [x] One rule needing it is enough, wherever it sits — `OneRuleThatNeedsIt_IsEnough`
- [x] No rules need nothing — `NoRulesAtAll_NeedNothing`

Verified end to end on the reference application with `DevTools/ExceptionTypeProbe`, not in this
suite: it needs a real app whose exceptions come out of an assembly without
symbols, which TestTarget cannot provide. See KNOWN_UNKNOWNS U15.
## S. Driving the screen (`UiHierarchyTests` no device, `DeviceControlTests` device, one MCP end-to-end)

The screen tools exist so an agent reaches the point worth debugging by itself.
What they promise: seeing the screen (screenshot, hierarchy) never needs more
than USB debugging; acting on it may be refused by a vendor, and then the
refusal says which switch to flip and nothing else is lost.

- [x] The uiautomator XML parses into nodes with bounds, centres, flags and depth;
      layouts are not "interesting", controls are —
      `Parse_ReadsNodes_WithBoundsAndFlags`
- [x] Noise a vendor prints before the XML (MIUI's theme stack trace) is skipped —
      `Parse_SkipsTheNoise_AVendorPrintsBeforeTheXml`
- [x] Output without a hierarchy is an error naming that, not an empty tree —
      `Parse_RejectsOutputWithoutAHierarchy`
- [x] Selectors match short and full resource ids, short and full classes, text
      case-insensitively, and combine — `Find_MatchesShortIds_ShortClasses_AndTextCaseInsensitively`
- [x] Several top-level windows (a dialog over the activity) become one root —
      `Parse_WrapsSeveralTopLevelWindows_InOneRoot`
- [x] Key names with or without `KEYCODE_`, with spaces, or numeric —
      `NormalizeKey_AcceptsNames_WithOrWithoutPrefix_AndCodes`
- [x] Text survives `adb shell input text`: spaces as `%s`, quotes escaped —
      `QuoteForInputText_EscapesSpaces_AndShellQuotes`
- [x] The vendor's injection refusal is recognised by its text, other `input`
      errors are not — `LooksLikeInjectionDenied_RecognisesTheVendorRefusal`
- [x] PNG dimensions come from the header; non-PNG bytes and empty output are
      errors — `ReadPngDimensions_ReadsTheHeader_AndRejectsOtherBytes`
- [x] The device allows injection (fails in words on a MIUI device with the
      security toggle off) — `InputInjection_IsAllowed_OnThisDevice`
- [x] A screenshot is a PNG the size of the display, either orientation —
      `Screenshot_IsAPng_TheSizeOfTheDisplay`
- [x] TestTarget's button, label and text field are in the hierarchy by short
      resource id — `UiHierarchy_ListsTestTargetsControls_ByResourceId`
- [x] **The loop that matters**: a tap on the button hits a breakpoint in the
      click handler, with its locals — `Tap_OnTheButton_HitsTheClickHandlersBreakpoint`
- [x] While the app is suspended the hierarchy fails saying so (uiautomator
      waits for an idle UI) and a screenshot still works —
      `UiHierarchy_FailsInWords_WhileTheAppIsSuspended`
- [x] Typed text lands in the focused field, quote included —
      `TypeText_IntoTheFocusedField_ShowsUpInTheHierarchy`
- [x] Non-ASCII text is refused rather than typed as garbage —
      `TypeText_RefusesNonAscii_InsteadOfTypingGarbage`
- [x] Through the MCP server: the screen tools are listed, `capture_screenshot`
      returns an image block, the device is implied by the session,
      `tap_screen` by selector hits a breakpoint, a selector nobody matches is
      an error, and input tools say when the debuggee is suspended —
      `ScreenTools_ScreenshotIsAnImage_AndATapBySelectorHitsABreakpoint`
- `ScreenTools_ScreenshotIsAnImage_AndATapBySelectorHitsABreakpoint` once ran
  into its 3-minute budget right after `launch_app` (2026-09-05, first run
  after the Gboard dialog episode) and passed in 13 s on the next run. Not
  explained. The test now logs each `get_ui_hierarchy` attempt with its
  duration; if it hangs again, that line says whether uiautomator or the launch
  is what stalls.
- [ ] Deploy refused by a vendor (`INSTALL_FAILED_USER_RESTRICTED`) is explained
      (`AndroidLauncher.DeployHint`) — seen on the Redmi when MIUI's per-install
      confirmation dialog went unanswered; not a test because it needs a person
      not tapping
- The whole section also passed on the Redmi Note 8 Pro (MIUI 12.5, Android 11)
  on 2026-09-05, 10/10 including `LogcatTests`.
- [ ] Long press and swipe on a real control — TestTarget has nothing that reacts
      to either; add a list when a feature needs one

## R. Install scripts (`InstallScriptTests`, no device)
`register-mcp-debugger.cmd` and `vscode\install-vscode-extension.cmd` are the only path a new
machine has to a working setup, and they name folders and file names as strings.
Nothing else in the build reads them, so a rename elsewhere in the repo breaks
them silently and surfaces only on the machine being set up.
- [x] The installer links a folder that really is the extension (it holds a
      `package.json`) — `TheInstaller_PointsAtTheExtensionThatExists`
- [x] Both scripts agree on where the adapter is published, and on its file
      name: the installer warns about a missing adapter by probing the folder
      `register-mcp-debugger.cmd` publishes to —
      `BothScripts_AgreeOnWhereTheAdapterIsPublished`
- [x] The extension contributes the debug type the documented `launch.json`
      entries use; a mismatch shows up only as VS Code refusing to start a
      session — `TheExtension_ContributesTheDocumentedDebugType`
