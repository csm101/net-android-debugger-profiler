# Task resume

## Current task
P0 spike - prove the collection + analysis chain end-to-end.

## Current substep
Workspace just initialized (2026-08-20). Nothing of P0 started.

## Next action if interrupted right now
Start P0 step 1 below.

## Exact next steps (in order)
1. Install diagnostic tools:
   dotnet tool install -g dotnet-trace dotnet-dsrouter dotnet-gcdump
   (record versions in ANDROID_PROFILING_NOTES.md).
2. Create TestTarget/ minimal net-android app with a busy method (CPU burn
   loop with a recognizable name) and an allocation-heavy method; build with
   -c Release -p:EnableDiagnostics=true.
3. Boot emulator DevicePerSviluppoProfiler; run:
   dotnet-trace collect --dsrouter android --format speedscope
   plus a plain nettrace collection. Confirm the busy method appears in the
   samples. Resolves U1, U4 basics.
4. DevTools probe: parse the collected .nettrace with TraceEvent, print top-N
   hottest methods with inclusive/exclusive counts. Resolves U3 for sampling.
5. gcdump round-trip: dotnet-gcdump collect, verify the allocation-heavy
   type shows up.
6. MonoProfiler provider probe: MONO_DIAGNOSTICS enable + callspec on
   TestTarget namespace; verify enter/leave and alloc events arrive and
   whether TraceEvent decodes them. Resolves U2, rest of U3.
7. Record all findings in ANDROID_PROFILING_NOTES.md / KNOWN_UNKNOWNS.md;
   then design ProfilerSession API and SQLite schema v1 for P1.

## What works
- Solution scaffold builds (Core/Mcp/Tests, net10.0).

## What is failing
- Nothing yet (no functionality exists).

## Traps / hypotheses
- dotnet-trace one-liner --dsrouter android requires dotnet-trace >= 9.0.621003;
  older tool needs the manual dsrouter dance.
- MonoProfiler provider may be absent or renamed in the net10 android
  workload - probe before building anything on it.
- Do not launch the app through Visual Studio while diagnostics env is set
  (documented splash-screen freeze).
- Suspend mode + slow tool startup may trip app watchdogs.

## Open items outside P0
- (none)
