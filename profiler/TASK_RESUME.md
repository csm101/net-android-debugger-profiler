# Task resume

## Current task
P1 - Core + MCP (started 2026-08-20 after P0 spike).

## Current substep
P1 step 2b DONE: collection layer + ProfilerSession facade; 5 device tests
green (sampling restart, instrumenting restart, heap snapshot, attach to
running app, missing package). Docs updated. Full suite run delegated to
test-runner; commit after its report.

## Next action if interrupted right now
Commit/push step 2b; then P1 step 5: MCP server (src/NetAndroidProfiler.Mcp)
over ProfilerSession: tools list_devices, profile_run, profile_start/
profile_stop/profile_status, profile_sessions, profile_hotspots, profile_flat,
profile_callers/profile_callees, profile_tree, profile_timings,
alloc_report, heap_snapshot, profile_report, get_app_output. ModelContextProtocol
NuGet 2.2.0 (MIT). Then P1 step 6: annotate_source via portable pdb.

## P1 plan (in order)
1. [done] U17 Debug-build test.
2. [done] Core: 2a analysis+store; 2b devices/apps/collection/session.
3. [done] Fast tests on recorded traces (19 + 1 skipped U13).
4. [done] Device tests (5).
5. MCP server (thin) with the P1 tool set.
6. annotate_source via portable pdb (DiaSymReader approach from
   Desymbolicate) - last P1 item.
7. Register MCP server for Claude Code (register-mcp pattern, P5 preview).

## What works
- ProfilerSession end-to-end on emulator-5556 / TestTarget Debug build for
  Sampling (Restart + Attach), Instrumenting (Restart, callspec, allocations
  with type names), HeapSnapshot (live EventPipe heap dump).

## What is failing
- Nothing open.

## Traps / hypotheses
- A stray dotnet-dsrouter.exe holds port 9000 -> sessions hang in
  WaitingForApp: kill it (taskkill /F /IM dotnet-dsrouter.exe).
- debug.mono.env >90 bytes aborts the app: never use it for MONO_DIAGNOSTICS.
- Override env file is 0400: rm + cp + chmod 400 via run-as.
- Git Bash converts /data/... paths: MSYS_NO_PATHCONV=1 for adb commands.
- Incremental build after env change -> broken APK: wipe obj/ bin/.
- msbuild -p: commas -> %2C.
- AppInspector pulls every APK of the package per session (U18: cache).
