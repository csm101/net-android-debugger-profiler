# Task resume

## Current task
Overnight autonomous session (2026-08-20 → 21). M1/M2 engine work is done and
the suite is the safety net; the current loop is: pick a gap from
TEST_CATALOG.md, write the named test, fix whatever it exposes, keep the
specs in the same change set, commit.

## State
- Suite: **49 tests, 49/49 green** (~5 min on the headless emulator). Stability
  was confirmed by consecutive runs, not a single lucky one.
- TEST_CATALOG: 57 covered, 3 open (each blocked on hardware or a decision).
- Commits tonight, newest last: 247cede (stop location falls back to the user
  frame; U11 measured), 9735b4a (one unhandled exception per process; slow
  probe isolated), e93abc0 (async/await), eabb4d0 (structured app output;
  headless emulator), 06f0a9b (**disarm breakpoints during any debuggee
  invocation** — the root cause of the evening's flakiness), 32f444a (honest
  hit-count assertion + U13), 230ca40 (sticky service restart), 538ea28 (app
  dying by itself, throw stepped over, relaunch), 8fd9a1b (foreign app not
  attached; generic type names), 036016b (deploy, detach, relaunch, screen
  rotation), 4ba98ea (U13 evidence).
- Everything above is verified through the test suite. The registered MCP
  server is older than all of it (see next steps).

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
1. **User action**: rerun register-mcp.cmd (with MCP sessions closed). The
   registered server predates every fix listed above, most importantly the
   breakpoint disarming — which is exactly what a timer-heavy app like
   the reference application needs.
2. Then re-drive the reference application through the MCP tools: breakpoints in the main
   process while its services run, expansion of real objects, and the
   evaluation-heavy paths that used to freeze.
3. The three catalogue gaps left, each blocked on something external: a WiFi
   device (U9), a decision about evaluations with side effects, a way to
   simulate a mid-run debugger disconnect.
4. U13 (hit counts): the cheap experiment is logging `CurrentHitCount` per
   stop; the evidence gathered so far is in KNOWN_UNKNOWNS.
5. PR mono/debugger-libs#419 is open, CLA signed, no maintainer review yet.
   When merged: point .gitmodules back to upstream, bump the submodule,
   update ARCHITECTURE.md.
6. M4 candidates when the engine work quiets down: DAP frontend, packaging,
   SourceResolver (only once a real PDB-path mismatch shows up).

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
