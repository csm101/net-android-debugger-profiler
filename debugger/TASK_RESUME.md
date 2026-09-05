# Task resume

## Current task (2026-09-05): device control through adb

Screen tools for the MCP server: screenshot, UI hierarchy, tap/swipe/key/text,
so an agent can bring the app to the point worth debugging by itself. Debugging
must keep working when the device refuses input injection (MIUI gates it behind
"USB debugging (Security settings)").

Substep: code and tests done and green in isolation (12 no-device tests, 7
`DeviceControlTests` and the MCP screen test on `emulator-5554`; 3 of the device
tests also on the Redmi). First full run: 141/162, one stale tool-surface list
(fixed) and 20 launch handshake failures caused by `logcat -c` not clearing on
this Android 11 image (fixed in `AndroidLauncher`/`AdbClient`, covered by
`LogcatTests`, recorded in ANDROID_ATTACH_NOTES). Second full run:
**164/164 green, 12m59s** on `emulator-5554` (`NAD_SKIP_DEPLOY=1`).
Task complete; nothing in flight. Docs updated
(README section 5 + MCP paragraph, ARCHITECTURE rows, PROJECT_STATE tool
surface, TEST_CATALOG section S, ANDROID_ATTACH_NOTES screen section).

Found while testing, already handled in code and docs:
- `input` blocks until the focused app consumes the event: with the main thread
  at a breakpoint the adb call never returns. Input tools refuse while the
  session is stopped (`DeviceTools.RefuseWhileSuspended`).
- Suspended app: uiautomator says `could not get idle state` on MIUI and
  `null root node` on the API 30 emulator; both mapped to the same explanation.
- Gboard crash-loops on the api_30 image when a text field gets focus; the
  dialog covers the activity. Tests dismiss it; Gboard disabled on that AVD.
- The MCP screen test hung once for its whole 3-minute budget after launch_app,
  then passed in 13 s. Instrumented, not explained.

Not committed yet.

Files: `Core/Device/DeviceControl.cs`, `Core/Device/UiHierarchy.cs`,
`Core/Adb/AdbClient.cs` (`RunDeviceBytesAsync` for `exec-out`),
`Mcp/DeviceTools.cs` (registered in `Program.cs`), tests `UiHierarchyTests`
(no device), `DeviceControlTests` (device), one MCP end-to-end test.
TestTarget's layout gained an `EditText` (`input_field`) for the type_text
test - the suite must deploy once (drop `NAD_SKIP_DEPLOY`).

Device for this work: the user's Redmi Note 8 Pro, serial `a-physical-device`
(MIUI 12.5, Android 11, 1080x2340, injection allowed - the toggle is on).
The first deploy to it failed (`INSTALL_FAILED_USER_RESTRICTED`): MIUI shows a
confirmation dialog on the phone for every adb install and it went unanswered.
Approved on retry; the 7 `DeviceControlTests`, the 2 `LogcatTests` and the MCP
screen test all pass on the Redmi too. `AndroidLauncher.DeployHint` explains
that failure.

This machine differs from the one the docs describe: the repo is under
`C:\Athens\GitHub`, adb is NOT on PATH in Claude's shells (use
`C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe`, and add
the folder to PATH before `dotnet test`), and the installed AVDs are
`pixel_7_-_api_30` and `pixel_5_-_api_22_0` - there is no `pixel_7_-_api_33_0`.
Device tests run on `AVD=pixel_7_-_api_30` as `emulator-5554`
(`ANDROID_SDK_ROOT` must be set for ensure-emulator.sh to find the binary).

Measured on the Redmi: `screencap -p` 112 KB PNG; `uiautomator dump` 2 s, with
a MIUI stack trace (missing theme_compatibility.xml) on stderr before the XML;
`input keyevent 0` accepted (the injection probe).

Next: build tests, run `UiHierarchyTests` then `DeviceControlTests` and the MCP
test on the Redmi, then docs (README "What to enable on the device",
ARCHITECTURE module row, PROJECT_STATE tool surface, TEST_CATALOG section S,
ANDROID_ATTACH_NOTES device-control facts), then the full suite via test-runner.

## Previous task (2026-08-26)

Nothing in flight.

Last change: `install-vscode-extension.cmd` in the repository root. Installing the
VS Code extension was a PowerShell symbolic-link incantation buried in a README,
which is not something anyone remembers. The script junctions
`DevTools/vscode/net-android-debugger` into `%USERPROFILE%\.vscode\extensions`
(`NAD_VSCODE_EXTENSIONS` retargets it), warns when the adapter has not been
published yet, and is idempotent.

Decisions worth not re-deriving:

- **Junction, not symbolic link.** `mklink` with the junction flag needs neither
  elevation nor developer mode, VS Code follows it identically, and it tracks the
  repository so editing the extension needs no reinstall. A true symbolic link
  would prompt for elevation on a default Windows install. Copy is the fallback,
  and the script says which of the two it did, because a copy is a snapshot.
- **Replacing an existing install**: a plain `rmdir` first, which removes a
  junction without touching its target, then a recursive one only if something is
  still there (a previous copy). Verified both ways: the repository folder
  survives.
- **Guard**: the script refuses when `NAD_VSCODE_EXTENSIONS` would make the
  target equal the sources, since the install deletes the target first.

Covered by `InstallScriptTests` (3 facts, no device, 26 ms) - TEST_CATALOG
section R. They exist because both scripts name folders and file names as
strings that nothing else in the build reads: a rename elsewhere breaks them
silently, and only on the machine being set up.

Installed on this machine already, as a junction.

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
- `AVD=pixel_7_-_api_33_0 bash DevTools/scripts/ensure-emulator.sh` before
  unattended runs (two AVDs are installed here, so the script will not guess):
  headless (a
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
