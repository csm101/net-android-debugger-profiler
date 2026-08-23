# Task resume

## Current task

Nothing in flight. The suite is green and THIRD-PARTY-NOTICES.txt is in place,
so nothing blocks a first distribution on the licensing side.

What the last two commits added:

1. `AndroidLauncher.ForeignDebugPropertyWarning` - the launch reads
   `debug.mono.extra` before writing it and reports a value that has not
   expired, naming the port and the remaining seconds. It still takes it over.
2. `get_loaded_assemblies` reports `symbols` / `NO SYMBOLS` / `symbols unknown`
   from `AssemblyMirror.HasDebugInfo` (protocol 2.51+, try/catch -> null).
3. `snapshot=true` on continue_and_wait, wait_until_stopped and the three steps
   folds in stack and locals. Opt-in, unlike the Delphi debugger which always
   returns one: reading locals invokes code in the debuggee, which disarms
   breakpoints for the duration.
4. `SlowStepReceiver` in TestTarget holds one deliberately slow line, which made
   the race provokable. Answered: `StepOverAsync` returns the breakpoint another
   thread hit, on that thread, rather than the step - so nothing is lost.

## State (2026-08-23)

- Suite: **130 tests, 130/130 green** (13m22s, `NAD_DEVICE_SERIAL=emulator-5554`).
  No skips left: the named gap became a real test once TestTarget got a slow line.
- Two runs were lost today to qemu crashing mid-run, both with the same
  signature: dozens of failures at ~150 ms each, all `device not found`. That
  shape means the environment, not the code.
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

1. Blocked on hardware: attach over `adb connect` and a mid-run debugger
   disconnect (U9).
2. Run the VS Code extension in a real VS Code against a real device.
3. PR mono/debugger-libs#419: open, CLA signed, awaiting a maintainer.

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
