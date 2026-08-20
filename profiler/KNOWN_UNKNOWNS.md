# Known unknowns

Open questions that block or condition the work. When one is resolved, move
the answer into the owning document (ANDROID_PROFILING_NOTES.md,
ARCHITECTURE.md, PROJECT_STATE.md, TEST_CATALOG.md) and delete the entry.

## U1 - Sampling end-to-end on net9-android
Does the default cpu-sampling profile of dotnet-trace work against TestTarget
on emulator pixel_7_-_api_33_0? Sample rate configurability? Managed frames
fully symbolicated? Overhead?

## U2 - MonoProfiler provider availability on net9/net10 android
Is Microsoft-DotNETRuntimeMonoProfiler still shipped and enableable
(MONO_DIAGNOSTICS) in the android workload we build with? Exact callspec
syntax and its filtering granularity? Enter/leave overhead at realistic
callspec widths?

## U3 - TraceEvent parsing of MonoProfiler events
Does TraceEvent decode MonoProfiler provider events out of the box, or do we
need a custom dynamic-event parser for enter/leave and alloc events?

## U4 - Suspend/startup choreography
Reliability of suspend + dsrouter + trace start ordering; behavior when the
tool connects late; app watchdogs (MQTT) while suspended; differences
Debug vs Release, emulator vs device.

## U5 - Trace size and duration
Realistic .nettrace sizes for minutes-long sessions on the reference application; streaming vs
post-mortem analysis; rotation strategy.

## U6 - SQLite schema at scale
Call-tree storage for millions of nodes: schema, indexing, query latency for
GUI grids. When does schema v1 freeze?

## U7 - GUI/Core control contract
Local control service for the Delphi GUI: REST vs JSON-RPC vs command files;
process lifetime model (GUI spawns Core? separate daemon?). Decide in P4.

## U8 - Weaver design (P3)
Fody-based vs raw Mono.Cecil MSBuild task; async/await state machines
(MoveNext attribution); iterator methods; generic instantiations; on-device
collector transport (socket vs file) and its overhead.

## U9 - CoreCLR on Android
When the reference application retargets to a CoreCLR-based Android runtime: sampling path
survives (EventPipe), MonoProfiler provider disappears (weaver is plan B).
Track the timeline.

## U10 - Physical devices and palmari
adb reverse flow on real handhelds; WiFi adb and SSH-tunneled adb for remote
palmari; port collisions with the debugger project's ports.

## U11 - Shared AndroidCollector with net-android-debugger
When both projects' orchestration stabilizes: extract shared library (repo,
packaging, versioning between the two repos).
