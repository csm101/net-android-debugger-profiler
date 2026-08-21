# Task resume

## Current task
P4 groundwork: U7 decided and written (pause / resume / snapshot / clear on top
of start/stop, `nap serve` on loopback, results read straight from session.db),
and docs/GUI_DESIGN.md written against the official AQTime documentation.

What the AQTime reading established: its Report panel is one row per routine with
Sample Count / Time / % Samples for sampling and Hit Count / Time / Time with
Children / % for performance; the Details panel has a Calls page (Parents and
Children) and a Lines page; Call Tree, Call Graph, Editor, Summary and Monitor
are separate docked panels; and its live controls are exactly Get Results,
Clear Results and Enable/Disable Profiling - which is where pause/resume/snapshot/
clear come from. DevExpress sources and agent-oriented docs are at
C:\Athens\DevExpress (DOCS/ per library, Demos/VCL first).

Two honest constraints the GUI must show rather than hide: pause is real only on
the weaver engine (the provider engine closes and reopens a segment), and clearing
results does not remove instrumentation, so the overhead stays.

## Substep
Design committed. No GUI code yet: P4 starts with the shell (docking + ribbon),
the Setup screen and the Report grid over sample_stat/timing_stat.

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
- When the GUI starts: add SynEdit (MPL-1.1, used unmodified) to
  THIRD-PARTY-NOTICES.txt; the dependency policy in CLAUDE.md now allows it.
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
