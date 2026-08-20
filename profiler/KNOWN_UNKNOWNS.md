# Known unknowns

Open questions that block or condition the work. When one is resolved, move
the answer into the owning document (ANDROID_PROFILING_NOTES.md,
ARCHITECTURE.md, PROJECT_STATE.md, TEST_CATALOG.md) and delete the entry.

Resolved by the P0 spike (2026-08-20) and moved to ANDROID_PROFILING_NOTES.md:
U1 (sampling end-to-end), U2 (MonoProfiler provider availability, callspec,
overhead), U3 (TraceEvent decoding: no parser, manual decode works), U4
basics (suspend choreography), U12 core (serial mapping; one dsrouter port
per device).

## U4b - Suspend choreography on real apps
the reference application has watchdogs (MQTT, services) - behavior when the process sits
suspended for tens of seconds, or when dotnet-trace connects late; Debug vs
Release; device vs emulator. The transient `EndOfStreamException` at session
start (retry works) needs a proper retry policy.

## U5 - Trace size and duration
Measured: sampling ~30 KB/s, instrumenting with a busy callspec ~0.75 MB/s
on TestTarget. Realistic .nettrace sizes for minutes-long the reference application sessions;
streaming (EventPipe session via DiagnosticsClient) vs post-mortem file
analysis; rotation strategy.

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

## U12b - Two emulators profiled at once
dsrouter android-emu has no port option; the generic `server-server`
command with explicit endpoints plus a per-device DiagnosticPort must be
exercised before the engine supports concurrent sessions on two devices.

## U13 - VTableID -> type name for pre-session types
MonoProfiler GCAllocation carries VTableID; only vtables created during the
session resolve (VTableLoaded + ClassLoaded). Candidates for a
start-of-session type dump: GCHeapDump (0x100000) +
GCHeapDumpVTableClassReference (0x8000000) keywords; or correlate with the
runtime provider's BulkType events if Mono emits them with vtable ids.
Needed for P2 per-type allocation reports.

## U14 - Allocation call sites
GCAllocation has no stack. Options: enable the alloc event inside the
instrumenting session and attribute each allocation to the innermost open
enter/leave frame of that thread (works for instrumented methods only);
or Microsoft-Windows-DotNETRuntime GCSampledObjectAllocation events with
stacks (does MonoVM emit them with stacks?). Decide in P2.

## U15 - Sampling leaf attribution on AOT code
Profiled-AOT Release builds lose leaf frames in sampled stacks (Mix absorbed
by Busy). Is it all AOT frames or only leaf frames without a frame pointer?
Does `AndroidEnableProfiledAot=false` + `RunAOTCompilation=true` (full AOT)
behave the same? Determines whether the engine forces JIT builds for
profiling runs or only warns.

## U16 - the reference application builds on this machine
the reference application is net9.0-android35.0; this machine has only the net10 android
workload pack. Verify `dotnet build` of App.Droid works (net9 runtime pack
download) before the first P1 integration run against it.

## U17 - Instrumenting Release builds without rebuilding
Resolved for Debug builds (override environment file, see
ANDROID_PROFILING_NOTES "Injecting environment variables without
rebuilding"). Release runtimes have no env hook: instrumenting a Release
APK requires the baked environment file (docs/APP_SETUP.md). Still open:
measure the always-on cost of `--diagnostic-mono-profiler=alloc` on a real
app, to decide whether `alloc` can be baked into a Release `Profiling`
configuration permanently.

## U18 - APK prerequisite inspection on large apps
Implemented (Apps/AppInspector): `pm path` -> pull every APK -> zip entries
under lib/<abi> (diagnostics component, libaot-*), strings of
libxamarin-app.so for MONO_DIAGNOSTICS, `run-as` for debuggable. Open: cost
on the reference application-size APKs (pull of 50+ MB per session) - cache by package
version / `pm dump` signature, or read only the needed entries remotely.

## U19 - the reference application existing profiling assets
the reference application already has a Metalama-based method timing aspect
(C:\Work\ReferenceApp\Metalama.Profiling, ProfileMethodAttribute /
ProfileFabric, "Profiling" build configuration) and Desymbolicate (pdb ->
line numbers, symbol server per build). Evaluate reuse: Metalama weaving as
P3 plan B instead of Cecil; Desymbolicate's symbol-server lookup for
annotate_source on Jenkins builds.
