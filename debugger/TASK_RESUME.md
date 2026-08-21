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
- Build green (0 warnings, 0 errors). Suite: **51 tests, 51/51 green**
  (5 m 14 s; the emulator dropped once mid-run and `ensure-emulator.sh`
  recovered it).

## Next steps (in order)
1. New catalogue gap: a member typed `IEnumerable`/`IEnumerable<T>` expands to
   the compiler's iterator state machine instead of its elements (VS shows a
   "Results View"). Seen on `UnityContainer.Registrations`. Needs a TestTarget
   hook plus `IEnumerableMember_ExpandsToItsElements`.
2. The three older gaps, each blocked on something external: a WiFi device
   (U9), a decision about evaluations with side effects, a way to simulate a
   mid-run debugger disconnect.
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
