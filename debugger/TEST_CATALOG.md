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
- [ ] Launch fails cleanly when the package is not installed (error, no hang)
- [ ] Launch fails cleanly when the device is offline / serial wrong

## A2. Multi-process (port rotation)
- [x] Helper process (`:helper`) attached on the next port, breakpoint hit
      there — `Breakpoint_InHelperProcess_IsHit_ViaPortRotation`
- [ ] Helper spawned while main is stopped at a breakpoint is still attached
- [ ] Helper that exits and is respawned by Android is re-attached on a new port
- [ ] Three processes (main + two helpers) get three distinct ports
- [ ] `GetProcesses` reports a helper that died (`HasExited`)

## B. Breakpoints
- [x] Source-line breakpoint hit with locals — `Breakpoint_InMainProcess_IsHit_WithLocals`
- [x] Continue hits again; generation increases —
      `ContinueAndWait_HitsSameBreakpointAgain_WithIncreasingGeneration`
- [x] Breakpoint set before launch resolves when the assembly loads (covered
      implicitly by all breakpoint tests; `Verified` asserted)
- [ ] Breakpoint set while running (after launch) is bound and hit
- [ ] Conditional breakpoint (`Condition`)
- [ ] Hit-count breakpoint
- [ ] Remove / remove-all while running — breakpoint no longer hits
- [ ] Breakpoint on a line without code — reported as not verified, no crash
- [ ] Same file, two breakpoints; `SetBreakpoints` replaces per file

## C. Stepping
- [x] Step over to the next line in the same method —
      `StepOver_AdvancesToNextLine_InSameMethod`
- [ ] Step into / step out at a plain call site
- [ ] Step through async/await
- [ ] Step over a call that raises an exception
- [ ] Step in one process while another process is stopped

## D. Stack and threads
- [x] Call stack at breakpoint: user frame on top, external frames below —
      `CallStack_TopFrameIsUserCode_WithExternalFramesBelow`
- [x] Threads listed when stopped, including the stopping thread —
      `Threads_AreListed_WhenStopped`
- [ ] Stack of a thread other than the stopping one
- [ ] Main/UI thread identified (button-click breakpoint on the UI thread)

## E. Locals and values
- [ ] Primitives (int, long, bool, char, string, double, decimal, DateTime)
- [ ] Enums and flags
- [ ] Object expansion (fields, properties)
- [ ] Arrays / List<T> / Dictionary<K,V> expansion
- [ ] Null and uninitialized locals
- [ ] Generic types display

## F. Evaluate
- [ ] Simple expression (local arithmetic)
- [ ] Member access / method call (side-effect policy documented)
- [ ] Invalid expression yields error, not crash

## G. Exceptions
- [ ] First-chance filter (break on thrown)
- [ ] Unhandled exception reported with details
- [ ] Exception type filtering

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
