# Task resume

## Current task
the reference application re-drive through the freshly published MCP server (2026-08-21), and
the fixes it turns up. M1/M2 engine work is done; the suite is the safety net.

## Live run against the reference application on the republished server (2026-08-21)
- `launch_app(keepPropertyFresh: true)` accepted, so the published server is
  today's build.
- **The third process is debugged for real.** `am start-foreground-service -n
  App.Droid/the app's background service` starts `the app's own android:process` (uid 10174,
  same as App.Droid); the launcher recognises it by uid and attaches it on port
  10002 next to the main process and `:crash_report_process`. Breakpoint in
  `BackgroundService.OnStartCommand` hit, stack and locals (`intent`, `startId`,
  `this`) readable. Visual Studio cannot debug this process at all.
- App output timestamps line up across both channels now (device clock).
- One display bug found and fixed on the spot: `set_breakpoint` answered
  `[verified] The breakpoint will not currently be hit` — the status message
  came from a process where the assembly is not loaded while another had the
  breakpoint bound. A bound breakpoint no longer carries a message.
- Trap learned the hard way: a live MCP debug session on the device breaks the
  suite (it owns `debug.mono.extra`, and rewrites it with keepPropertyFresh).
  Terminate it before running tests.

## State
- The MCP server was re-registered by the user, so the tools now run the code
  with the breakpoint-disarm fix.
- **the reference application re-driven live and it held.** Breakpoint on
  `the sync library's base thread:112` (the wait every sync
  thread goes through), five stops on three different threads, deep expansion
  in between (WatchDog/Sender graphs, ThreadsManagerImpl, DatabaseImpl, Unity
  container) and cross-object `evaluate_expression`: no freeze, no aborted
  invocation, no lost process. Async frames, `step_over` and multi-process
  re-attach (`:crash_report_process` restarted mid-session → new port 10002)
  all behaved. Recorded in ANDROID_ATTACH_NOTES.md, the reference application section.
- **Bug found and fixed during the drive**: app output mixed two clocks.
  logcat stamps device local time; stdout/stderr delivered through SDB was
  stamped `DateTime.Now` on the host, so on this emulator (GMT) versus this
  host (GMT+2) one listing interleaved lines two hours apart. Fixed by
  measuring the offset once per launch.
  - `AdbClient.GetDeviceLocalTimeAsync`
  - `AndroidLauncher.DeviceClockOffset` / `DeviceNow` (also used by
    `ParseLogcatTimestamp`, which no longer assumes the host's year)
  - `DebugSession.DeviceNow()` used when stamping SDB output
  - test `AppOutput_TimestampsAreOnTheDeviceClock_WhateverTheChannel`
- Second fix of the day: `set_breakpoint` used to answer with the state of the
  instant it was called ("The breakpoint will not currently be hit") because
  binding is asynchronous; a moment later the same breakpoint was verified.
  Core now has `SetBreakpointAsync` / `SetBreakpointsAsync` with a settle
  window (frontends pass 750 ms, and skip the wait when nothing is attached);
  the MCP tools use them. Test `SetBreakpointAsync_WaitsForTheRuntimeToBindIt`.
- `ConditionalBreakpoint_StopsOnlyWhenConditionIsTrue` failed once in a full
  run and it was the test's fault, not the engine's: `_ticks == 3` is only
  ever true in the app's first three seconds, so a slow attach makes it
  unsatisfiable forever. Now `_ticks % 7 == 3`, which recurs.
- Third item: the "IEnumerable shows the state machine, not the elements" gap
  turned out to be smaller than it looked. Mono already exposes the elements,
  as an extra group child named `IEnumerator` sitting among the iterator's own
  fields — it simply carried no value, so it read as empty. `Describe` now
  labels it `<enumerated elements: expand>`. TestTarget's `Sample` gained a
  lazy `Sequence` property to exercise it; the suite needs one deploy.
  Test `IEnumerableValue_ExposesItsElements_UnderTheEnumeratorGroup`.
- Fourth item, and the one that mattered for the reference application: a component declared with
  a **global `android:process`** name (the reference application has one, `the app's own android:process`)
  runs in a process whose name shares nothing with the package, so the launcher
  used to refuse it as a foreign Mono app and burn its port. Ownership is now
  decided by uid when the name does not settle it:
  - `AdbClient.GetPackageUidAsync` (`pm list packages -U`), and
    `ListPackageProcessesAsync` matches by uid as well
  - `AndroidLauncher.IsOurs` / `LookupProcess` (`ps -A -o PID,UID,NAME`),
    uid resolved lazily so the common case costs nothing
  - TestTarget gained `GlobalProcessReceiver` (process
    `net.androiddebugger.globalproc`) and the test
    `ProcessWithAGlobalName_IsRecognisedByUid_AndAttached`
  Apps sharing a uid match too: intended, they share the sandbox.
- U13 narrowed by reading upstream and then deliberately parked: there is
  exactly one path that zeroes `CurrentHitCount` and our code does not take it,
  so the "shared store gets reset" hypothesis is out; missed hits during
  (re-)registration is what is left. Details in KNOWN_UNKNOWNS.
- Two test-harness repairs from the same run:
  - `ForeignMonoApp_StartingDuringTheSession_IsNotAttached` slept 20 s and then
    asserted the engine had announced the refusal. On a freshly booted emulator
    App.Droid had not reached its Mono agent init yet, so there was nothing to
    refuse and the test failed while the engine was blameless. It now waits for
    the announcement (90 s) and says so when it never comes.
  - `DeviceFixture.NewSession` forwarded engine log lines to xUnit's output
    helper, which throws once its test is over. An engine log callback from a
    background thread after teardown took the whole test host down mid-run.
    That exception is now ignored (the line is kept in the fixture's own log).
- Fifth item, also aimed at the reference application: a process the app starts **after** the
  debug property's deadline runs without a debugger, silently. Measured with a
  25 s lifetime: the process is on the device and simply not attached. New
  `LaunchOptions.KeepPropertyFresh` (MCP: `launch_app(keepPropertyFresh: true)`)
  rewrites the property every `lifetime/3` for as long as the session lives, and
  the same process is then attached on its own port. Off by default — the same
  freshness makes an unrelated Mono app stall on our port. All property writes
  now go through one semaphore (launch, rotation, renewal). Test
  `ProcessStartedAfterThePropertyExpired_IsAttached_OnlyWhenTheLifetimeIsKeptFresh`
  (~2 min on its own).
  Use it on the reference application: `the app's own android:process` is on-demand, the crash reporter
  restarts on its own schedule.
- The suite then caught something the live run had not: a globally-named
  process is only identified through `ps`, and `ps` can miss a pid that has just
  forked — the launcher then calls one of our own processes foreign. Fixed at
  the source: ActivityManager prints the uid right after the process name on its
  `Start proc` line (`u0a174` → 10174), so the process is recognised there,
  without `ps`; the `ps` fallback also retries once before refusing.
- Also found: **port rotation is not race-free**. The property is rotated when
  an agent is *detected*, so two processes starting in the same instant read the
  same value and fight for one port; the loser's agent cannot listen and it
  dies. Documented (ANDROID_ATTACH_NOTES, KNOWN_UNKNOWNS U14) and left alone —
  processes normally start seconds apart. The test that tripped it now waits for
  `:helper` before spawning the second process.
- Next full run caught another one of the same family:
  `AsyncFrame_StopsAfterAwait_WithLocalsAndUserStack` stepped with its
  breakpoint still armed, and TestTarget re-enters that probe, so the stop the
  step call returned was a fresh breakpoint hit on the same line. The test now
  disarms first and asserts `StopReason.Step`, which is what makes the class of
  mistake visible instead of silent.
- The `ps` retry described above was a mistake and is gone: the agent-detection
  handler runs on the logcat thread and the debug property is rotated only
  *after* it decides whether a pid is ours, so a 400 ms delay there widened the
  window in which the next process reads the same port. Removing it was not the
  whole story, though — see below. The Start proc uid recognition (which is
  what actually fixed the globalproc case) stays, and needs no `ps` at all.

- **The real cause of the handshake failures**: `am force-stop` returns before
  the app's processes are actually gone. A leftover — or a sticky `:helper`
  Android is restarting — was still alive when the next launch published the
  port, read the property and took it, so the process we wanted lost its agent
  (`no SDB handshake on port N within 20s`, then the app dies). It only ever
  showed in full-suite runs, with a different victim each time, because the
  leftovers come from the *previous* test. The launcher now waits (up to 8 s)
  for the package to have no live process before writing the property.
- The force-stop wait worked (its warning never fired), and the next run was
  54/55. The last holdout was `Detach_TerminatesTheApp_ByDesign`, which waited
  for the package to have *no* process at all — but Android restarts the sticky
  `:helper` a few seconds later, so that condition is only briefly true. It now
  checks that the pids it saw before the detach are gone, which is what "detach
  terminates the app" actually means.
- New tool `get_source_files` (Core `DebugSession.GetSourceFiles`): given a file
  name or path, each attached process reports the path its runtime actually has,
  with the types compiled from it. This is how a pending breakpoint is
  diagnosed — wrong path, or type not loaded yet — instead of grepping the PDB.
  It is a metadata query, so it works while the app is running.
- `ForeignMonoApp_StartingDuringTheSession_IsNotAttached` now abstains (a note
  in the test output, no assertion) when the foreign app never reaches its Mono
  agent init: the guard had nothing to refuse, so that run proves nothing. It
  was failing on emulators that had just come back from a crash.
- Suite: **56 tests, 56/56 green** (7 m 31 s), with neither the force-stop
  warning nor a handshake failure anywhere in the output. The runs before
  it each failed on a different fragile spot; all are understood and repaired.


## the reference application drive, round 2 (2026-08-21)
Two engine defects, both found on the first two stops:
- Null locals of an async frame claimed to have children. A local the method has
  not reached yet is a field of the state machine, so it is visible and null;
  Mono renders it `(null)` but does not set its null flag, and we handed out an
  expansion handle that expanded to nothing. Reproduced in TestTarget with a
  local declared after the breakpoint line.
- The exception type was empty for a first-chance stop inside MQTTnet (an
  assembly without symbols). `GetExceptionDetails` now falls back to the
  `$exception` value's own type name. **The TestTarget reproduction failed** —
  see U15 for what was tried, why it proved nothing, and why the scaffolding was
  reverted. The fix rests on the V7 observation alone.

## DAP frontend and the port race (2026-08-21)
- **`NetAndroidDebugger.Dap`**: stdio Debug Adapter Protocol frontend over the
  same `DebugSession`. Hand-rolled wire format (no debug-protocol dependency to
  licence-clear). Four end-to-end tests start the real adapter process.
  Contract notes are in ARCHITECTURE (DAP frontend section); the ones that bite:
  `disconnect` answers *before* tearing down, one thread list spans every
  process so names carry their pid, and frame/variable ids die with each stop.
- **U14 resolved**: the port is rotated when ActivityManager announces a process
  of ours (fork time), not only at agent init. That was the top cause of suite
  flakiness — `no SDB handshake on port N`, a different victim each run.
  The worry that rotating early would steal the port was unfounded; see U14.
- Two follow-on defects that fix exposed, both fixed:
  - shutdown cleared the property *before* stopping the logcat reader, which is
    what rotates it, so a restarting process could leave a stale value on the
    device;
  - the DAP `disconnect` did the whole teardown before answering, and a client
    cannot tell slow from hung.
- **U12 corrected**: an unhandled exception does *not* reliably kill the process
  (alive past 90 s in 2 of 8 runs). The test now accepts both outcomes and
  asserts the session stays coherent.
- Suite: **60 tests, 60/60 green** on a clean device, with none of the three
  failure signatures anywhere in the output.
- The emulator crashed nine times today; roughly one full run in three is lost
  to it. `ensure-emulator.sh` recovers it, but the run has to be repeated.
## Next steps (in order)
1. **User action**: rerun `register-mcp.cmd` with the MCP sessions closed. The
   published server is older than everything landed today — including the uid
   fix, which is what makes the reference application's `the app's own android:process` debuggable. The
   publish cannot run while the server process holds the files.
2. The two gaps left both need the WiFi device of U9: attach over
   `adb connect`, and a mid-run debugger disconnect. `adb forward --remove`
   does not simulate one (the established connection survives) and
   `adb kill-server` would take down every other adb client on this machine.
3. U13 (hit counts): the cheap experiment is logging `CurrentHitCount` per
   stop; evidence so far is in KNOWN_UNKNOWNS.
4. PR mono/debugger-libs#419 is open, CLA signed, no maintainer review yet.
   When merged: point .gitmodules back to upstream, bump the submodule,
   update ARCHITECTURE.md.
5. M4 candidates: DAP frontend, packaging, SourceResolver (only once a real
   PDB-path mismatch shows up).

## Environment rules (also in ANDROID_ATTACH_NOTES.md / TEST_CATALOG.md)
- Only ever touch `emulator-5554`; `emulator-5556` is the user's other AVD
  (`devicepersviluppoprofiler`).
- `bash DevTools/scripts/ensure-emulator.sh` before unattended runs: headless
  (a windowed emulator cannot start while the desktop is locked) and with the
  hardware GPU (a software GPU is slow enough to make evaluation time out).
  It also clears the locks and snapshot a crashed qemu leaves behind.
- Suite env: `NAD_DEVICE_SERIAL=emulator-5554`, `NAD_SKIP_DEPLOY=1` when
  TestTarget is unchanged.
- Editing a source file and running the suite at the same time breaks the
  run: the test host rebuilds. Do documentation while the suite is out.

## Traps worth keeping in mind
- **Never run `python` here** (not installed): a heredoc into it hangs the
  shell for the full timeout. It happened again on 2026-08-21.
- Consumers of the vendored libs must reference Mono.Cecil 0.10.1 explicitly.
- `debug.mono.extra` is device-global and read at process start: no late
  attach, and other Mono apps starting meanwhile read it too (the launcher
  refuses to attach them and rotates the burnt port).
- Detach == terminate on Mono Android.
- `adb shell am broadcast` waits for the receiver, so a breakpoint inside a
  receiver hangs the adb command until you resume: fire it without waiting.
- Killing a sticky service makes Android restart it asynchronously; a test
  that does so must tear the app down and wait, or the restarts spill into
  the next test.
- Suspending the reference application makes MQTT time out; the app logs handled exceptions and
  reconnects. Not a debugger defect.
