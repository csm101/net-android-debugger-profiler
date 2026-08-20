# Task resume

## Current task
Autonomous overnight session (user away, following from phone; explicit
instruction: do not stop for confirmations, only for real decisions).

## Overnight plan (in order, commit+push after each step)
1. [done] V7 weaver with a narrow two-type callspec: green
   (AppApplication..ctor 7.1 s, IsMainProcess 5.3 s, OnCreate 1.85 s).
2. [in progress] MCP end-to-end tests (TEST_CATALOG section F): spawn the MCP
   server over stdio from xunit against a prepared sessions root, assert
   initialize/tools/list and the read-only tools, plus error paths.
3. U22 - build-time weaving: MSBuild task that weaves selected assemblies
   after compilation and before packaging, writing the id map next to the
   APK, so apps that keep EmbedAssembliesIntoApk=true (the reference application) can be
   instrumented without a special deployment. Engine consumes the map.
4. P2 memory: diff between two heap snapshots (growth report) + MCP tool;
   attempt U13 (type names for pre-session vtables).
5. U15 experiment: controlled TestTarget workload (long leaf vs tiny leaf) to
   characterize MonoVM sampling leaf attribution.
6. Weaver quality: option to skip property accessors; async/iterator MoveNext
   attribution (U8) if the rest lands early.

## Next action if interrupted right now
Continue at the first unfinished item; run the full suite through the
test-runner agent before the final commit of the night.

## What works
- Sampling, heap snapshots, runtime-provider instrumenting (net10 targets),
  weaver instrumenting (TestTarget and the reference application with a narrow callspec).
- MCP server registered in Claude Code (register-mcp.cmd); tool set complete
  for P1 plus engine=weaver.
- Fast suite 25 passed / 1 skipped (U13).

## What is failing / known limits
- U20: runtime-provider instrumenting crashes the net9 MonoVM (callspec
  option). The weaver is the instrumenting path there.
- Weave scope: whole-namespace weaving on the reference application (7882 methods) never
  reaches managed code within 240 s; narrow filters only.
- the reference application weaver needs EmbedAssembliesIntoApk=false until U22 lands.

## Traps / hypotheses
- Stale .pdb next to a woven assembly silently disables it (deployer moves it
  aside now).
- adb exec-out reads must drain stdout before waiting for exit; sizes are
  verified against stat.
- Collector static ctor must not touch JNI (deadlocks the UI thread).
- `am start -W` times out on instrumented apps.
- A stray dotnet-dsrouter holds port 9000: taskkill before device runs.
- Git Bash mangles /data/... paths: MSYS_NO_PATHCONV=1.
