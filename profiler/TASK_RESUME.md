# Task resume

## Current task
P1 - Core + MCP (started 2026-08-20 after P0 spike). All P1 steps coded.

## Current substep
P1 step 6 DONE: profile_annotate_source (method token -> portable pdb ->
line range; MonoVM has no per-line samples) + PortablePdbSymbols + 4 fast
tests; SamplingAnalyzer uses a private temp .etlx (parallel-safe). Full
suite delegated to test-runner; commit after its report.

## Next action if interrupted right now
Commit/push step 6. Then step 7: run register-mcp.cmd, exercise the server
from Claude Code on TestTarget (fresh session), then first the reference application session
(U16: does App.Droid build here; Debug + EnableDiagnostics; sampling attach).

## P1 plan (in order)
1. [done] U17 Debug-build test.
2. [done] Core: 2a analysis+store; 2b devices/apps/collection/session.
3. [done] Fast tests on recorded traces (23 + 1 skipped U13).
4. [done] Device tests (6).
5. [done] MCP server (thin) with the P1 tool set + register-mcp.cmd.
6. [done] annotate_source via portable pdb (per method).
7. Register MCP server for Claude Code and exercise it on TestTarget; first
   the reference application session.

## What works
- ProfilerSession end-to-end on emulator-5556 / TestTarget Debug build for
  Sampling (Restart + Attach), Instrumenting (Restart, callspec, allocations
  with type names), HeapSnapshot (Attach; Restart with warm-up).
- MCP server over stdio with the P1 tool set incl. annotate_source
  (smoke-tested: Busy [753 753 753] on CpuBurner.cs lines 10-17).

## What is failing
- Nothing open. Leaf-frame loss in sampling tracked as U15 (not a bug of
  ours; MonoVM sampler behavior).

## Traps / hypotheses
- Session DBs created before 2026-08-20 15:50 lack method.token (schema
  still v1, no external consumer yet): delete old test sessions under
  %TEMP%\net-android-profiler-tests\sessions if a tool errors on them.
- A stray dotnet-dsrouter.exe holds port 9000 -> sessions hang in
  WaitingForApp: kill it (taskkill /F /IM dotnet-dsrouter.exe).
- Heap dump requested right after app launch yields nothing (retry/warm-up
  handle it); MonoVM never signals dump completion -> quiescence.
- debug.mono.env >90 bytes aborts the app: never use it for MONO_DIAGNOSTICS.
- Override env file is 0400: rm + cp + chmod 400 via run-as.
- Git Bash converts /data/... paths: MSYS_NO_PATHCONV=1 for adb commands.
- Incremental build after env change -> broken APK: wipe obj/ bin/.
- msbuild -p: commas -> %2C.
- AppInspector pulls every APK of the package per session (U18: cache).
