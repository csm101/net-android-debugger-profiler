# Task resume

## Current task
M2/M3 hardening done for this session; suite fully green. Last commit about
to land: robustness work after suite runs 9-17 (see below). Registered MCP
server is still the PRE-fix build: the user must rerun register-mcp.cmd
(with MCP sessions closed) to pick up the fixes.

## State (2026-08-20, end of session)
- Suite: 32/32 green in 3.6 min on emulator-5554 (hardware GPU), device
  clean after every run (property, processes, forwards).
- Engine hardening landed since commit 18dc11f:
  - Attach deduplicated per pid; process exposed only after the SDB
    handshake (fixes pause-during-connect NRE).
  - Foreign Mono processes (other apps reading the device-global
    debug.mono.extra, e.g. the reference application auto-started at boot) are NOT attached:
    ps name lookup, WARNING log, port still rotated. Default
    PropertyLifetime 3 min; launch_app has propertyLifetimeSeconds.
  - Evaluation: EvaluationTimeout 12 s / MemberEvaluationTimeout 18 s
    (an invoke ABORTED on timeout wedges the stopped thread - root cause of
    the flaky expansion timeouts; first DateTime.ToString = ICU init is the
    trigger); RunBounded 60 s around every synchronous inspection call;
    SetEvaluationOptions + MCP set_evaluation_options to tune / disable
    ToString-invokes on slow targets.
  - Exception stops: type + stack trace captured at stop time on the event
    thread (no evaluation there - "vm is not suspended"); message resolved
    lazily on the caller thread via $exception._message; survives the
    process dying right after an unhandled exception.
  - Values still evaluating after the wait window are reported as
    "<evaluation timed out>" [error], no expansion handle for them or nulls.
  - Unnamed threads labelled ("Main" for id 1, "Thread N" otherwise).
  - Culture-invariant rendering in frontends (it-IT host produced "0,5").
- Tests: 32 across 4 files (LaunchAndBreakpoint 10, InspectionAndBreakpoint
  10, Robustness 8, McpEndToEnd 4). Timing-sensitive asserts made robust
  (1 s Tick timer races, tick-number-agnostic, unhandled-exception
  continue behavior observed not asserted - U12).

## Environment lessons (also in ANDROID_ATTACH_NOTES.md)
- Stop the emulator with `adb -s emulator-5554 emu kill`; taskkill by PID
  silently fails and a second same-AVD launch refuses to start.
- `-gpu host` fast (one qemu crash in 4 h, NVIDIA GL); swiftshader stable
  but so slow that invokes time out and the suite gets flaky; angle falls
  back to swiftshader on this machine. Current: `-gpu host -cores 4`.
- Suite runs: NAD_DEVICE_SERIAL=emulator-5554, NAD_SKIP_DEPLOY=1 when
  TestTarget unchanged. emulator-5556 belongs to the user - never touch.

## Next action if interrupted right now
Commit (if not yet done), then user reruns register-mcp.cmd. Next chunks:
- U6 experiment on the reference application: break in main, stay paused 3-5 min, observe
  watchdog/MQTT/process survival (via MCP tools after republish).
- U11 probe: does a wedged evaluation thread heal after Continue + next
  stop? Warm-up invoke idea.
- U12: engine policy for repeated unhandled-exception stops.
- Logcat filtering polish, SourceResolver (only when a PDB path mismatch
  actually shows up, e.g. CI-built the reference application APKs).
- PR mono/debugger-libs#419 pending; when merged: .gitmodules back to
  upstream, bump submodule, update ARCHITECTURE.md.
- Pending in-session: claude-code-guide agent answer on disabling the
  Fable->Opus model fallback (user demand, memory saved).

## Traps (permanent ones live in the docs; these are the active few)
- Consumers of the vendored libs must reference Mono.Cecil 0.10.1.
- debug.mono.extra is device-global and read at process start only: no late
  attach; foreign apps read it too (see above).
- Detach == terminate on Mono Android.
