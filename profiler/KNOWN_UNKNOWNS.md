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
The .NET 10 android workload on this machine already ships
Microsoft.Android.Runtime.CoreCLR.36 and NativeAOT.36 runtime packs next to
Mono (opt-in per project). When the reference application retargets to CoreCLR: sampling path
survives (EventPipe), MonoProfiler provider disappears (weaver is plan B),
the override-environment injection and debug.mono.* properties change
(src/native/clr/ in dotnet/android). Try a CoreCLR TestTarget build in P2/P3
to see what still works.

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

## U15 - Sampling leaf attribution (AOT and not only)
Profiled-AOT Release builds lose leaf frames in sampled stacks (Mix absorbed
by Busy). The Release JIT build shows Mix but under-represented (212 vs 579
for Busy); a Debug-build session (2026-08-20, 12 s) showed Busy
incl == excl == 753 and no Mix at all. Hypothesis: the MonoVM sample
profiler reports the frame of the last managed method with a stack-walk
anchor (LMF) rather than the true leaf, so tiny leaf methods vanish into
their caller. Needs a controlled experiment (leaf that loops for seconds vs
a tiny leaf; interpreter vs JIT). Until understood: hotspot lists are
reliable at "method + its small callees" granularity; document it in the
MCP instructions.

## U16 - First the reference application session
Build verified 2026-08-20: `dotnet build App.Droid.csproj -c Debug
-p:EnableDiagnostics=true` succeeds on this machine (4.5 min, Android SDK
pack 35.0.105 resolved automatically, 0 errors); the APK (24.7 MB) carries
libmono-component-diagnostics_tracing.so, no libaot-*, 21 portable pdbs in
bin/Debug/net9.0-android35.0. Still to do: install on emulator-5556, first
sampling session (attach), instrumenting session with callspec N:V7,
annotate_source with symbolsDir = that bin folder.

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

## U20 - Runtime instrumenting unusable on net9 apps (root cause found)
Root cause isolated 2026-08-20 by env bisection on App.Droid
(net9.0-android35.0): setting `--diagnostic-mono-profiler-callspec=...`
makes the net9 MonoVM fail with SIGSEGV (fault addr 0x20) during
`mono_jit_init_version` -> diagnostics component option handling ->
`mono_profiler_set_call_instrumentation_filter_callback` (crash-buffer
backtrace). `enable` alone and `enable`+`alloc` start fine; only the
callspec option is fatal. TestTarget (net10.0-android) works with identical
options, so the defect is in the .NET 9 Mono runtime and is presumably
fixed in .NET 10. Without a callspec the provider instruments every JITted
method (unusable on a real app), so plan A is effectively unavailable for
net9 targets like the reference application -> P3 plan B (Mono.Cecil weaving, user decision).
Optional later: check dotnet/runtime for the fixing commit; retest when
the reference application moves to net10.

## U21 - CLOSED: weaver now works on the reference application (two prerequisites)
Two independent causes kept the woven assemblies from ever executing on
App.Droid; both are handled now and a weaver session on the reference application is green
(AppApplication..ctor 8.78 s, OnCreate 4.54 s / 4.02 s self, real timings):
1. `EmbedAssembliesIntoApk=True` (set by App.Droid.csproj): the runtime loads
   assemblies from inside the APK, so rewriting the fast-deployment copies has
   no effect. Profiling builds need `-p:EmbedAssembliesIntoApk=false`
   (docs/APP_SETUP.md); the engine detects the situation through the collector
   marker and says so.
2. The original `.pdb` next to the rewritten assembly: with it in place the
   woven assembly was silently not used (Debug builds load the debugger
   component). The deployer now moves `<assembly>.pdb` aside for the duration
   of the session and restores it afterwards. This was the last blocker.

## U22 - CLOSED: build-time weaving
Implemented 2026-08-20 and verified on TestTarget: `nap-weave`
(src/NetAndroidProfiler.Weave) plus build/NetAndroidProfiler.Weaving.targets
weave the app assembly in the intermediate output before packaging, copy the
collector next to the output and write nap-weave.map. A session with
Engine=Weaver and WeaveMapPath consumes that map and touches nothing on the
device (device test `Build_time_weaving_session_uses_the_build_map`). This is
the path for apps that keep EmbedAssembliesIntoApk=true, such as the reference application.
Open follow-ups: exercise it on the reference application itself; decide whether to ship the
tool as a NuGet package with the targets file instead of a published folder.

