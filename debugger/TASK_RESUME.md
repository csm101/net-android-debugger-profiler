# Task resume

## Current task
Overnight autonomous session (2026-08-20 → 21). M1/M2 engine work is done and
the suite is the safety net; the current loop is: pick a gap from
TEST_CATALOG.md, write the named test, fix whatever it exposes, keep the
specs in the same change set, commit.

## State
- Suite: 41 tests in four files. Last full runs: 40/40 and 39/40 (the one
  failure was a genuine engine bug, fixed in 06f0a9b). ~4 min per run on the
  headless emulator.
- Commits tonight, newest last: 247cede (stop location falls back to the user
  frame; U11 measured), 9735b4a (one unhandled exception per process; slow
  probe isolated), e93abc0 (async/await coverage), eabb4d0 (structured app
  output; headless emulator), 06f0a9b (**disarm breakpoints during any
  debuggee invocation** — the root cause of the evening's flakiness), 32f444a
  (honest hit-count assertion + U13).
- The registered MCP server is older than these commits: the user must rerun
  register-mcp.cmd (with MCP sessions closed) before the tools reflect them.
  Everything above is verified through the test suite, not through the server.

## Environment rules (also in ANDROID_ATTACH_NOTES.md / TEST_CATALOG.md)
- Only ever touch `emulator-5554`; `emulator-5556` is the user's other AVD
  (`devicepersviluppoprofiler`).
- `bash DevTools/scripts/ensure-emulator.sh` before unattended runs: headless
  (a windowed emulator cannot start while the desktop is locked) and with the
  hardware GPU (a software GPU is slow enough to make evaluation time out).
  It also clears the locks and snapshot a crashed qemu leaves behind.
- qemu crashed three times tonight; the script recovers it. If a run fails
  with "device offline"/"device not found", restart and repeat the run.
- Suite env: `NAD_DEVICE_SERIAL=emulator-5554`, `NAD_SKIP_DEPLOY=1` when
  TestTarget is unchanged.

## Next steps (in order)
1. Read the last test-runner report; fix anything genuinely red.
2. Remaining TEST_CATALOG gaps worth doing next: step over a call that throws;
   generic type display; app exit reported as session end; second launch_app
   closes the previous session; launch_app with deploy=true.
3. U13: confirm whether a per-process breakpoint store stabilises hit counts
   (log `CurrentHitCount` per stop first — cheap experiment).
4. When the user is back and the server is republished: re-drive the reference application
   through the MCP tools, mainly to see the breakpoint-disarm fix under a real
   app full of timers and services.
5. PR mono/debugger-libs#419 is open, CLA signed, no maintainer review yet.
   When merged: point .gitmodules back to upstream, bump the submodule,
   update ARCHITECTURE.md.

## Traps worth keeping in mind
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
- Never run `python` here (not installed): a heredoc into it hangs the shell.
