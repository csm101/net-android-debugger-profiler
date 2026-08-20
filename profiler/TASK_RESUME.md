# Task resume

## Current task
Autonomous overnight session (user away, following from phone). All six
planned items are done; the full suite is running through the test-runner
agent for final validation.

## Done tonight (each committed and pushed)
1. V7 weaver with a narrow callspec: green (AppApplication..ctor 7.1 s).
2. MCP end-to-end tests over stdio (initialize, tool surface, read-only tools,
   error paths) - Fast/McpServerTests.
3. U22 build-time weaving: nap-weave + build/NetAndroidProfiler.Weaving.targets
   + SessionSpec.WeaveMapPath; device test green on TestTarget.
4. P2 memory: multi-snapshot sessions, ResultStore.HeapDiff, MCP heap_diff,
   fast + device tests.
5. U15 characterized and closed: the sampler folds tiny leaf methods into
   their caller (LongLeaf 504 exclusive, TinyLeaf absent); recorded in the
   notes, asserted by a device test, stated in the MCP instructions.
6. Weaver refinements: property accessors skipped by default, async stubs
   counted and warned about; docs (ARCHITECTURE engines + .napw format,
   README, docs/USAGE.md).
7. Weaver allocation tracking: newobj/newarr in woven methods report their
   type, collector writes nap-types.txt, analyzer fills alloc_by_type /
   alloc_by_site - the net9 path now has memory data too (fast + device).
8. Build-time weaving scoped to the Android app project so the targets file
   can be imported build-wide without touching the app csproj.

## Next action if interrupted right now
Check the background build of the reference application with build-time weaving
(-p:NapWeave=true -p:NapCallspec="T:App.Droid.AppApplication"
-p:CustomAfterMicrosoftCommonTargets=<repo>/build/NetAndroidProfiler.Weaving.targets,
assemblies stay embedded), then run a weaver session with
WeaveMapPath=<V7 bin>/nap-weave.map and confirm timings. Then the queue below.

## Queue (highest value first)
- U13: type names for allocations of types loaded before the session (attach
  sessions show "<vtable 0x...>"); try GCHeapDump + VTableClassReference
  keywords at session start.
- U8: async/iterator attribution by weaving the compiler-generated MoveNext
  and stitching resumptions per state machine instance.
- the reference application: confirm the build-time weaving session (build running).
- U8: async/iterator attribution (weave the state machine MoveNext).
- U6: call-tree storage at scale (query latency for the GUI).
- P4: Delphi GUI (reads session.db).

## What works
Sampling, memory (allocations, snapshots, growth diff), instrumenting through
the runtime provider (net10) or the weaver (any runtime, on-device or
build-time), all over MCP; fast suite 31 passed / 1 skipped.

## Traps / hypotheses
- Stale .pdb next to a woven assembly silently disables it (handled).
- adb exec-out reads must drain stdout before waiting for exit (handled).
- Collector static ctor must not touch JNI (handled).
- `am start -W` times out on instrumented apps (handled).
- Wide callspecs make startup unusably slow: keep filters narrow.
- The emulator's package service died once mid-session; a reboot fixed it.
