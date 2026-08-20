# Task resume

## Current task
P1 - Core + MCP (started 2026-08-20 after P0 spike).

## Current substep
P1 step 5 DONE: MCP server implemented and smoke-tested over stdio
(initialize, tools/list, list_devices, profile_sessions, profile_report);
register-mcp.cmd added; heap snapshot made robust (quiescence + retry +
warm-up). Docs updated. Committing.

## Next action if interrupted right now
P1 step 6: profile_annotate_source (portable pdb via Microsoft.DiaSymReader
or System.Reflection.Metadata; method token + IL offset -> line; needs a
native->IL mapping for sampled addresses - see Desymbolicate in the reference application and
U16). Then run register-mcp.cmd and try the server from Claude Code on
TestTarget; then first the reference application session (U16).

## P1 plan (in order)
1. [done] U17 Debug-build test.
2. [done] Core: 2a analysis+store; 2b devices/apps/collection/session.
3. [done] Fast tests on recorded traces (19 + 1 skipped U13).
4. [done] Device tests (6).
5. [done] MCP server (thin) with the P1 tool set + register-mcp.cmd.
6. annotate_source via portable pdb (DiaSymReader approach from
   Desymbolicate) - last P1 item.
7. Register MCP server for Claude Code and exercise it on TestTarget; first
   the reference application session.

## What works
- ProfilerSession end-to-end on emulator-5556 / TestTarget Debug build for
  Sampling (Restart + Attach), Instrumenting (Restart, callspec, allocations
  with type names), HeapSnapshot (Attach; Restart with warm-up).
- MCP server over stdio with the P1 tool set.

## What is failing
- Nothing open.

## Traps / hypotheses
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
