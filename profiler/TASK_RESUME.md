# Task resume

## Current task
U13 and U5, both done.

U13 (allocation type names for pre-session vtables): no path exists with this
runtime, and the attempts are now recorded so nobody repeats them - the mono
heap-dump keywords emit nothing even while a real dump walks 104,425 objects,
and the CLR BulkType events Mono does emit use ids matching neither VTableID
(0/226) nor ClassID (0/3066). Measured gap: 19 of 226 allocation vtables unnamed,
~9% of allocation events. Those rows now say what they are
(MonoProfilerAnalyzer.UnresolvedTypePrefix) and keep exact counts; the TODO-RED
test became a real test of that contract. The weaver engine is unaffected.

U5 (trace size): collection now stops at SessionSpec.MaxTraceBytes (default
512 MB, MCP maxTraceMb, 0 = unlimited), warns on the session, and keeps a valid
analyzable trace - stopping cleanly still produces the rundown, so names resolve.
Rotation was rejected on purpose: the rundown arrives at session stop, so trace
parts cannot be symbolicated on their own. Growth measured at ~30 KB/s
(TestTarget sampling), ~1.5 MB/s (the reference application sampling), ~0.72 MB/s (instrumenting
N:TestTarget.Workloads).

## Substep
Full suite green (61 tests, 58 passed, 3 skipped). Commit and push.

## Files in focus
src/NetAndroidProfiler.Core/Weaving/CecilWeaver.cs, WeaveDeployer.cs,
Sessions/ProfilerSession.cs, src/NetAndroidProfiler.Weave/Program.cs,
src/NetAndroidProfiler.Mcp/ProfilerTools.cs,
tests/.../Device/ReferenceAppTests.cs (Build_time_weaving_records_async_bodies_on_the reference application),
tests/.../Fast/WeaverTests.cs.

## Next action if interrupted right now
Read the test-runner report; fix anything red, then commit and push. After that
U8 keeps only its iterator half (`yield return` bodies are not woven).

## Traps found here
- Evidence collected before a fix stays in the docs and looks authoritative.
  Re-test a "known" blocker before investigating it: two unrelated defects
  produced exactly the symptom U8 attributed to woven async IL.
- V7 rebuild + install is ~4 min; the woven build is left installed on
  emulator-5556 with its map in the V7 bin folder.

## Done earlier (each committed and pushed)
1. V7 weaver with a narrow callspec: green.
2. MCP end-to-end tests over stdio (tool surface, read-only tools, error paths).
3. U22 build-time weaving: nap-weave + build/NetAndroidProfiler.Weaving.targets
   + SessionSpec.WeaveMapPath, verified on TestTarget and on the reference application in its
   shipped configuration (embedded assemblies, no csproj edit).
4. P2 memory: multi-snapshot sessions, ResultStore.HeapDiff, MCP heap_diff.
5. U15 closed: the sampler folds tiny leaf methods into their caller.
6. Weaver: property accessors skipped by default; async stubs flagged.
7. Weaver allocation tracking (net9 path finally has memory data).
8. U8: async state-machine weaving implemented but OFF by default - it stops a
   real net9 app from starting (evidence and next steps in U8).
9. U6 closed: 200k-node call tree stays interactive (performance test).
10. Shipped MSBuild targets guarded by tests (an XML comment with '--' had
    broken a the reference application build).
11. Build-time weaving bakes NAP_PROFILER_OUT into the app: injecting it on the
    device creates files/.__override__/ and an embedded-assembly app then
    refuses to start.

## Next action if interrupted right now
Read the test-runner report on the fast suite; if green, commit and push the
notices file, its test and the doc wiring. Then pick from the queue.

## Queue (highest value first)
- U8 diagnosis: why does a woven async state machine stop a net9 app from
  starting? (single-method weave, IL dump, verifier).
- Iterator methods (yield return) instrumented like async bodies.
- U13: type names for allocations of types loaded before the session.
- U4b: suspend choreography against the reference application's watchdogs.
- P4: Delphi GUI over session.db.

## What works
Sampling, memory (allocations, snapshots, growth diff), instrumenting through
the runtime provider (net10) or the weaver (any runtime; on-device or
build-time), all over MCP. Fast suite 36 passed / 1 skipped.

## Traps (all handled in code, keep in mind when debugging)
- Three different causes produce "no woven method executed": stale task record
  (use am start -S), stale files/.__override__ after switching deployment
  shape, and injecting an override environment file into an embedded-assembly
  app.
- Stale .pdb next to a woven assembly silently disables it.
- adb exec-out must drain stdout before waiting for exit.
- The collector's static ctor must not touch JNI.
- Wide callspecs make startup unusably slow.
- build/tools holds a published copy of nap-weave: republish after changes.
