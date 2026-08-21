# Task resume

## Current task
the reference application re-drive through the freshly published MCP server (2026-08-21), and
the fixes it turns up. M1/M2 engine work is done; the suite is the safety net.

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
- Build green. Suite: **53 tests, 53/53 green** (5 m 37 s, with deploy) —
  including both the new uid case and the foreign-app guard.

## Next steps (in order)
1. The three gaps left, each blocked on something external: a WiFi device
   (U9), a decision about evaluations with side effects, a way to simulate a
   mid-run debugger disconnect.
2. U13 (hit counts): the cheap experiment is logging `CurrentHitCount` per
   stop; evidence so far is in KNOWN_UNKNOWNS.
3. PR mono/debugger-libs#419 is open, CLA signed, no maintainer review yet.
   When merged: point .gitmodules back to upstream, bump the submodule,
   update ARCHITECTURE.md.
4. M4 candidates: DAP frontend, packaging, SourceResolver (only once a real
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
