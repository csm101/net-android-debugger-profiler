# Task resume

## Current task

Nothing in flight. Deduction is committed and the suite is green.

## State (2026-08-23)

- Suite: **121 tests, 120 green, 1 skipped** (13m37s, `NAD_DEVICE_SERIAL=emulator-5554`).
  The skip is a named gap, not a failure:
  `ABreakpointHitByAnotherThread_DuringAStep_IsStillReported`.
- `NAD_DEVICE_SERIAL` is mandatory: emulator-5556 (another project's AVD) is
  online too, and the harness refuses to guess between two devices.
- A run was lost to qemu crashing, as usual. The restart then failed with
  "Running multiple emulators with the same AVD": a qemu instance that never
  registered with adb is invisible to `adb devices`, so `adb emu kill` could not
  clear it. `ensure-emulator.sh` now kills it by qemu PID, matched on the AVD
  name. A non-elevated `Stop-Process -Force` does kill it, contrary to what the
  script's own comment claimed.
- `StepOver_AdvancesToNextLine_InSameMethod` failed once under load: expected
  Step, got Breakpoint. Not a flake — `System.Threading.Timer` does not
  serialise callbacks, so while stopped inside `Tick` the ticks queue up, and at
  the resume for the step a second thread re-enters `Tick` and hits the same
  breakpoint (breakpoints stay armed during a step) before the step completes.
  The test now removes the breakpoint before stepping; the behaviour it exposed
  became the named gap above.

### What today changed, in the order it was found

Every item below came out of driving the real app or re-reading the new code.

1. **Breakpoints are disarmed during any evaluation.** Mono resumes all threads
   for an invocation, so a breakpoint hit meanwhile freezes it for good. This was
   the biggest source of instability and matters most for timer-heavy apps.
2. **Timestamps are on the device clock**, both channels. logcat stamps device
   local time; SDB output was stamped on the host — two hours apart here.
3. **Breakpoint binding is awaited** before answering (it is asynchronous, and
   the instant answer read like a failure), and a bound breakpoint carries no
   stale status message from a process where the assembly is not loaded.
4. **Processes are recognised by uid**, not only by name — a component with a
   global `android:process` (the reference application's App.Background) was refused as foreign. The uid
   comes from ActivityManager's `Start proc` line, so no `ps` is needed.
5. **`keepPropertyFresh`**: the debug property can be kept valid for the whole
   session, so processes the app starts much later are still debugged.
6. **A launch waits for the package to be gone.** `am force-stop` returns early,
   and a leftover would take the port the next process needs.
7. **The port is rotated at fork** (U14), not at agent init — this was the top
   cause of suite flakiness. Shutdown stops the logcat reader *before* clearing
   the property, because the reader is what rotates it.
8. **Nulls carry no expansion handle**, including the state-machine locals Mono
   reports as `(null)` without its null flag.
9. **Exceptions are always named**, falling back to `$exception`'s own type when
   the throw site has no debug info (MQTTnet in the reference application).
10. **`get_source_files`** reports the path the running app was built with —
    which is how a pending breakpoint is diagnosed.
11. **DAP**: events go through one queue (ordering), `disconnect` answers before
    tearing down, malformed input is skipped rather than fatal.

## Next steps

1. Read `debug.mono.extra` before writing it, and say so when a fresh value that
   is not ours is already there — two debuggers on one device overwrite each
   other silently today (see ANDROID_ATTACH_NOTES.md "Sharing a device with
   another debugger"). Symptom without it: the app hangs ~30 s at startup.
2. Symbol status in `get_loaded_assemblies` — `AssemblyMirror.HasDebugInfo`
   (protocol 2.51+, try/catch → null on older runtimes), rendered as `symbols` /
   `noSymbols`. First thing to check when a breakpoint stays pending: without
   symbols no line in that assembly can bind, and comparing paths is wasted time.
3. A compact snapshot folded into step/continue answers, **opt-in**: reading
   locals means invoking code in the debuggee, and breakpoints are disarmed
   during any evaluation. The Delphi debugger returns one always; here that is
   not the same trade.
4. The named gap needs a TestTarget method with a deliberately slow line,
   reachable on its own so no other test pays for it.

## Environment rules

- Only ever touch `emulator-5554`; `emulator-5556` is the user's other AVD
  (`devicepersviluppoprofiler`).
- `bash DevTools/scripts/ensure-emulator.sh` before unattended runs: headless (a
  windowed emulator cannot start while the desktop is locked), hardware GPU (a
  software GPU makes evaluations time out). Its health check asks the package
  service, because `sys.boot_completed` stays 1 while system_server restarts.
- Suite env: `NAD_DEVICE_SERIAL=emulator-5554`, `NAD_SKIP_DEPLOY=1` when
  TestTarget is unchanged.
- A live MCP debug session on the device breaks the suite: it owns
  `debug.mono.extra`. Terminate it first.
- Editing sources while the suite runs breaks the run (the host rebuilds).

## Traps

- **Never run `python`** (not installed): a heredoc into it hangs the shell.
- Consumers of the vendored libs must reference Mono.Cecil 0.10.1 explicitly.
- `debug.mono.extra` is device-global and read at process start: no late attach,
  and other Mono apps starting meanwhile read it too.
- Detach == terminate on Mono Android.
- `adb shell am broadcast` waits for the receiver, so a breakpoint inside one
  hangs the adb command: fire it without waiting.
- Tests that watch a process die must watch the **pid**: Android restarts the
  app and the restart is attached, so the name comes back within seconds.
- Anything slow in the agent-detection handler widens the port-collision window;
  a 400 ms retry there broke three tests.
- Suspending the reference application makes MQTT time out; the app logs it and reconnects. Not a
  debugger defect.
