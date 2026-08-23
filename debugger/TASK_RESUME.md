# Task resume

## Current task

Nothing in flight. `launch_from_config` is committed and the suite is green.

Next, decided after looking at what the configuration file actually carries:
**deduce what the project already declares** instead of restating it. In
the reference application, 30+ projects target `-android` but only 2 declare an `<ApplicationId>`
(`App.Droid` and a tool under `ExternalTools/`), and the libraries declare none —
so "android TFM AND (ApplicationId or OutputType Exe)" is the filter.

1. `list_app_projects(solutionOrFolder)`: the counterpart of `list_devices` —
   path, ApplicationId, TFM per launchable project, solution members first. The
   flow becomes list devices → list apps → launch, with nothing guessed.
2. `launch_app`: `packageName`, `projectPath` and `deviceSerial` become
   optional. One candidate → use it; several → fail listing them with their
   ApplicationIds; `projectPath` without `packageName` → read it from the csproj.
   One device online → use it; more → list them (the test harness already
   refuses to guess here, and that is the behaviour to mirror).

Not doing `save_launch_config`: it would mostly save deducible values, which is
the redundant file this is meant to remove.

The `.vscode/launch.json` keeps `packageName` and `projectPath` even though they
are deducible: VS Code's own schema requires them, and a file that F5 rejects
would lose half its purpose. What only the file can carry is `exceptionRules`,
`deploy`, `keepPropertyFresh`, and one configuration per device.

## State (2026-08-23)

- Suite: **99 tests, 99/99 green** (18m13s, `NAD_DEVICE_SERIAL=emulator-5554`).
  86 before, plus 12 for the launch-configuration parser and one end-to-end.
- `NAD_DEVICE_SERIAL` is mandatory again: emulator-5556 (the profiler's AVD) is
  online too, and the harness refuses to guess between two devices.

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
   disconnect (U9). `adb forward --remove` does *not* simulate the latter — the
   established connection survives it.
2. Run the VS Code extension in a real VS Code against a real device. Everything
   under it is covered; the extension itself has offline checks only.
3. PR mono/debugger-libs#419: open, CLA signed, no maintainer review since
   2026-08-20. When merged: point .gitmodules back at upstream, bump the
   submodule, update ARCHITECTURE.md.
4. Open questions worth a look if they ever bite: U13 (hit counts — six exact
   runs, diagnostic in place), U15 (why `GetException()` returns nothing for a
   throw in a symbol-less assembly), U11, U12.
5. The emulator is the dominant cost: ten crashes today, about one full run in
   four lost (measured). A fresh AVD or a different API level is worth trying.

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
