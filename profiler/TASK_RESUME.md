# Task resume

## Current task
U8. Outcome: the recorded blocker does not exist. A the reference application build with the 3
async state machines of AppApplication woven starts normally (5 consecutive
launches) and the bodies report their resumptions - `OnCreate (async body)`
3 resumptions, 284.7 ms self, against 2.2 ms self for the stub. The old symptom
(process never forked, nothing in logcat) is the signature of the launch
(`stopped=true`) and override-environment defects fixed after that evidence was
collected.

## Substep
U8 itself is closed (async bodies on by default, verified on the reference application). Fixing the
suite around it exposed four defects, all now fixed and green individually:
1. Device test classes ran in parallel on one emulator and one dsrouter port -
   both now in the xUnit collection "device" (DisableParallelization).
2. AppEnvironment refused to work when the app was left with a 0-byte override
   environment file; it now stages+renames the file, compares the read against
   stat, and treats an empty file as "no variables".
3. dotnet-dsrouter: a router dying of "port already in use" prints
   "Stopping IPC server (...) <--> TCP server ..." and the old startup check
   matched "<--> TCP server", so a dead router passed for healthy; a cancelled
   start also leaked the process, which then held port 9000 for every later
   session. Now: port pre-check, only "Starting IPC server" counts, the router's
   own error is reported, dispose on every failure path.
4. Launching is unreliable both ways: monkey and am start each silently accept
   the intent without forking the process. LaunchAsync now verifies a pid
   appeared and alternates the two methods. This is what U8 originally
   misdiagnosed as woven async IL stopping the app.
Also: attach device tests configured no diagnostics port and were passing on
leftovers from earlier sessions; they now set it up and stop the app afterwards,
and the engine fails fast in Attach mode when no port is configured anywhere.
Full suite green: 59 tests, 55 passed, 4 skipped, 0 failed (5.2 min, fast + Device + the reference application with NAP_REFAPP=1). No orphan dsrouter, no app left running.

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
