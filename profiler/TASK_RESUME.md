# Task resume

## Current task
THIRD-PARTY-NOTICES (P5 licensing obligation): produce the notices file the
project must ship with any distributed release, and keep it from drifting.
Substep: written, test-guarded, wired into README/PROJECT_STATE/TEST_CATALOG;
fast suite running through the test-runner agent, then commit and push.

## Files in focus
THIRD-PARTY-NOTICES.txt, tests/NetAndroidProfiler.Tests/Fast/ThirdPartyNoticesTests.cs,
README.md, PROJECT_STATE.md, TEST_CATALOG.md (new section H).

## Last completed action
Notices file written from the real dependency closure (dotnet list package
--include-transitive on NetAndroidProfiler.Mcp and NetAndroidProfiler.Weave,
license expressions read from each .nuspec, verbatim license texts fetched from
upstream). The new test immediately caught two packages listed only in an
abbreviated form (Microsoft.Data.Sqlite.Core, ModelContextProtocol.Core) - the
ids are now spelled out and both tests pass.

## Traps found here
- The docs are CRLF; a scripted edit that writes LF corrupts the line endings
  silently. Normalize lone LF to CRLF after any scripted patch.
- The notices must match what is *distributed*, not what the solution
  references: the test reads the .deps.json of the built output, which is why a
  test-only package does not have to be acknowledged.

## Done tonight (each committed and pushed)
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
