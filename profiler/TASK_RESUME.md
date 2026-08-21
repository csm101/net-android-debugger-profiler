# Task resume

## Current task
Delivering the rest of the product on my own judgement (user: "non ti alzi da
tavola finche non hai finito"). Order: live control -> Delphi GUI -> packaging.

Done: live control (U7) works on the weaver engine and is verified on the device.
Snapshot pulls the collector's files and rewrites the results while the app keeps
running; pause/resume toggle a control file the collector polls once a second;
clear wipes the device files and the tables. Schema v2 adds `segment` (the
history of refreshes) - results stay cumulative, so no segment id on fact tables
and the GUI contract is unchanged. Exposed over HTTP (nap serve) and as MCP tools.

Two defects found and fixed on the way:
- A weaver session with no duration stopped after 20 s instead of waiting for
  Stop(), which made open-ended GUI sessions impossible.
- Pulling event files assumed they were not growing; a snapshot pulls files the
  app is still appending to, so a short read is a truncation (retry) but a longer
  one is just growth (keep).

## Next
Delphi GUI (P4) per docs/GUI_DESIGN.md, starting with the shell + Setup + Report
over session.db, then Call Tree with the critical path, Details, Editor (SynEdit),
Summary, Monitor, memory. Then P5 packaging (bundle dotnet-dsrouter, register
script, versioning, SynEdit in THIRD-PARTY-NOTICES).

## Substep
Full suite green: 74 tests, 71 passed, 3 skipped.

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
