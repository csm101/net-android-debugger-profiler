# Task resume

## Current task
P0 spike DONE. P1 design: user decisions recorded in PROJECT_STATE.md
(no rebuild by the profiler; separate tables; pdb via Desymbolicate approach).
Revised proposal given in chat; waiting for go-ahead to start P1 coding.

## Current substep
Spike results recorded; committing. Then write the P1 proposal in the reply
(not in code).

## Done in P0 (all facts in ANDROID_PROFILING_NOTES.md)
1. Tools 9.0.661903 installed (dotnet-trace / dsrouter / gcdump).
2. TestTarget/ (net10.0-android, com.mcasoftware.testtarget): CpuBurner.Busy
   + Mix, AllocHog.Allocate/NewRecord + AllocHeavyRecord, WorkloadRunner loop.
   csproj hook `-p:MonoDiagnostics=...` bakes MONO_DIAGNOSTICS.
3. Sampling round-trip on emulator-5556 (sampling1 AOT, sampling2 JIT):
   Busy visible; Mix only visible on the JIT build.
4. DevTools/NetTraceProbe: providers | events | topn | stacks | monoprof.
5. gcdump round-trip: 50,000 AllocHeavyRecord visible.
6. MonoProfiler provider: present, enter/leave + alloc arrive, TraceEvent
   has no parser, manual decode works, MethodID resolves via rundown,
   callspec filters, instrumentation persists across sessions, overhead
   ~10 us/event, clean build required after env change.
7. Recorded traces copied to tests/NetAndroidProfiler.Tests/recorded/.

## Next action if interrupted right now
If user said go: start P1 with U17 test (Debug build + EnableDiagnostics;
debug.mono.env on Debug runtime), then Core skeleton (ProfilerSession,
AndroidCollector prerequisites check, TraceAnalyzer, ResultStore schema v1).

## What works
- Whole collection+analysis chain on emulator for sampling, gcdump,
  instrumenting (probe level).

## What is failing
- Nothing open. Traps recorded in ANDROID_PROFILING_NOTES.md.

## Traps / hypotheses
- debug.mono.profile is device-global: cleared at the end of the spike.
- dotnet-trace -p <dsrouter> can fail once with EndOfStreamException right
  after app launch: retry.
- Incremental build after MONO_DIAGNOSTICS change -> broken APK
  (LinkageError on n_onCreate): wipe obj/ bin/.
- msbuild -p values: commas split properties -> escape as %2C.
- AOT build hides leaf frames in sampling; use RunAOTCompilation=false for
  attribution tests and for instrumenting (JIT-time instrumentation).

## Open items outside P0
- KNOWN_UNKNOWNS U4b, U5, U13-U17 (new).
