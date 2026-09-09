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

## U5 - CLOSED: trace size is capped, not rotated
Measured growth: ~30 KB/s sampling TestTarget, ~1.5 MB/s sampling the reference application
(38 MB for 25 s), ~0.72 MB/s instrumenting `N:TestTarget.Workloads`
(2 MB in 2.9 s, confirmed 2026-08-21). A minutes-long session on a real app is
therefore hundreds of MB.
Decision: a session stops collecting when the trace reaches `MaxTraceBytes`
(default 512 MB, `maxTraceMb` in MCP, 0/null = no limit) and records a warning;
what was collected stays a valid trace and is analyzed normally. Rotation was
rejected: a .nettrace is one stream whose rundown - the method names - arrives at
session stop, so splitting it would produce parts that cannot be symbolicated,
and stopping cleanly keeps the names (verified:
`Collection_stops_when_the_trace_reaches_its_size_limit` still resolves
TestTarget.Workloads methods). Live streaming stays reserved for heap snapshots,
which need no rundown.

## U7 - CLOSED: GUI/Core control contract
The Delphi GUI is a frontend beside the MCP server, not a viewer of its output:
both sit on the same Core. The MCP server calls Core in-process (a library
reference, no IPC); a VCL application cannot, so Core gets a second entry point.

Decided:
- One executable, `nap.exe`, with two modes over the same Core: one-shot
  (`nap run ... --json`, for scripts and CI) and `nap serve --port 0` - HTTP +
  JSON on loopback. HTTP over a named pipe because it is equally easy in Delphi,
  can be exercised with curl, and leaves the door open to driving a device
  attached to another machine (U10).
- The GUI owns the process: it spawns `nap serve` and shuts it down with itself,
  or attaches to one already listening.
- **Results do not travel over that channel.** The GUI opens `session.db`
  directly (FireDAC), which is what makes it useful on sessions produced by
  anyone, the MCP server included. The schema stays the contract.
- Progress is polled (`GET /sessions/{id}`, ~500 ms). A session changes state a
  handful of times; streaming would be complexity for nothing.

Control operations the contract must carry, so P4 does not have to graft them on
later (AQTime-style live control):
| operation | weaver engine | provider engine |
|---|---|---|
| start / stop | as today | as today |
| **pause / resume** | real: the collector already has an `Enabled` flag, the channel only has to toggle it - events stop, the app keeps running | no such thing in EventPipe: close the current segment and open another |
| **snapshot** (partial results while running) | natural: pull the current `.napw` files and analyze them against the weave map, which already holds the names | only per segment: method names arrive in the rundown at session stop (same reason rotation was rejected in U5) |
| **clear** | delete the `.napw` files on the device and reset | discard the segments collected so far |

Consequence for the model: **a session is a sequence of segments**, and the
result store must aggregate them. Note that clearing does not remove
instrumentation - woven IL stays woven and JIT-time instrumentation persists for
the life of the process - so the overhead stays while collection is paused. AQTime
has the same property on Win32.

The GUI side of this contract is designed in docs/profiler/GUI_DESIGN.md.

Implemented 2026-08-21 (`nap serve`, src/NetAndroidProfiler.Cli, documented in
docs/profiler/CONTROL_SERVICE.md): health, devices, app check, session list, start, status,
stop and shutdown, verified end to end - a sampling session started over HTTP
produced 1338 samples and a session.db where the GUI will read it. The four live
verbs answer 501 with what is missing. Core grew `SessionRegistry` and
`SessionSpecFactory` for this, and the MCP frontend now sits on both, so the two
frontends cannot drift on what "heap" or "weaver" means.

Live control implemented 2026-08-21 for the weaver engine, and verified on the
device (`Weaver_session_can_snapshot_pause_and_clear_while_the_app_runs`):
snapshot pulls the collector's files and rewrites the results while the app keeps
running, pause and resume toggle a control file the collector polls once a second,
clear wipes both the device files and the result tables. The provider engine
refuses all four with the reason (its names arrive with the rundown at session
end). Exposed over HTTP and as MCP tools (profile_snapshot / pause / resume /
clear).

The segment question resolved itself: results are cumulative, so a snapshot
rewrites the result tables and a `segment` row records the refresh (schema v2).
No segment id on the fact tables, and the GUI contract stays as it was.

Extended 2026-08-26 with what a frontend needs *before* a session, which turned out
to be the rest of the answer to "the GUI must not send anyone to a command prompt":
`/prereqs` and `/prereqs/install` (the machine's tools, and installing the one that
has a command - dsrouter), `/projects` and `/projects/candidates` (the Android
applications a solution holds, and the namespaces and types their assemblies
declare), `/builds` (build and install an app with the properties a session needs)
and `/jobs/{id}` (state and streamed log of the two long-running ones). Anything
that takes minutes is a job the caller polls, not a blocked request.

Nothing open here.

## U9 - CoreCLR on Android
The .NET 10 android workload on this machine already ships
Microsoft.Android.Runtime.CoreCLR.36 and NativeAOT.36 runtime packs next to
Mono (opt-in per project). When the reference application retargets to CoreCLR: sampling path
survives (EventPipe), MonoProfiler provider disappears (weaver is plan B),
the override-environment injection and debug.mono.* properties change
(src/native/clr/ in dotnet/android). Try a CoreCLR TestTarget build in P2/P3
to see what still works.
Update 2026-09-06: `net11.0-android` drops Mono entirely, so the provider engine and the
`debug.mono.*` mechanics end with .NET 10 apps (supported until November 2028); sampling over
EventPipe and the IL weaver are what survives. Repository-level view: `docs/KNOWN_UNKNOWNS.md` R2.
## U10 - Physical devices and handhelds
adb reverse flow on real handhelds; WiFi adb and SSH-tunneled adb for remote
handhelds; port collisions with the debugger project's ports.

## U11 - CLOSED: shared device layer with net-android-debugger
Done 2026-09-06: `src/NetAndroid.Device` holds the process runner, the adb locator, one adb
client, the screen layer, the app environment override and `DevicePropertyOverride`; both
Cores go through it and nothing else touches adb. What it contains and the rule that
follows: `docs/ARCHITECTURE.md`, decision 1. Its tests: `docs/TEST_CATALOG.md`.

## U12b - Two emulators profiled at once
dsrouter android-emu has no port option; the generic `server-server`
command with explicit endpoints plus a per-device DiagnosticPort must be
exercised before the engine supports concurrent sessions on two devices.

## U13 - VTableID -> type name for pre-session types (no path found)
MonoProfiler GCAllocation carries a VTableID; only vtables created during the
session get a name (VTableLoaded + ClassLoaded). Measured gap on a restart
session with suspend (TestTarget, 10 s): 19 of 226 allocation vtables unnamed,
about 9% of the allocation events - but among them the 3rd, 4th and 5th busiest
vtables, so it is visible in a report.

Every documented candidate was tried and none works (2026-08-21):
- **GCHeapDump (0x100000) + GCHeapDumpVTableClassReference (0x8000000)**: no
  events at all, neither during allocation traffic nor during a real heap dump
  of 104,425 objects that ran in the same session. The MonoProfiler provider
  emits nothing while the CLR heap dump walks the heap.
- **CLR BulkType (Type keyword 0x80000)**: Mono does emit it - 411 types with
  names in a 10 s session - but its TypeIDs match neither the MonoProfiler
  VTableIDs (0 of 226) nor its ClassIDs (0 of 3066). Different identity space,
  useless for naming allocations.
- **Type rundown**: the runtime's rundown carries methods only, no types.
- Unknown `--diagnostic-mono-profiler=` options are accepted silently, so the
  accepted option set cannot be probed that way.

Current behaviour: such rows are labelled
`<type loaded before the session, vtable 0x...>` and their counts and sizes stay
exact (`MonoProfilerAnalyzer.UnresolvedTypePrefix`). The weaver engine is
unaffected - it resolves names through RuntimeTypeHandle - so an app that needs
exact per-type allocation names can use engine=weaver.
Next candidates if this becomes important: read the vtable's class pointer from
the app process (needs a debugger-style reader), or a runtime patch.

## U14 - Allocation call sites
GCAllocation has no stack. Options: enable the alloc event inside the
instrumenting session and attribute each allocation to the innermost open
enter/leave frame of that thread (works for instrumented methods only);
or Microsoft-Windows-DotNETRuntime GCSampledObjectAllocation events with
stacks (does MonoVM emit them with stacks?). Decide in P2.

## U17 - Instrumenting Release builds without rebuilding
Resolved for Debug builds (override environment file, see
ANDROID_PROFILING_NOTES "Injecting environment variables without
rebuilding"). Release runtimes have no env hook: instrumenting a Release
APK requires the baked environment file (docs/profiler/APP_SETUP.md). Still open:
measure the always-on cost of `--diagnostic-mono-profiler=alloc` on a real
app, to decide whether `alloc` can be baked into a Release `Profiling`
configuration permanently.

## U18 - APK prerequisite inspection on large apps
Implemented (Apps/AppInspector): `pm path` -> pull every APK -> zip entries
under lib/<abi> (diagnostics component, libaot-*), strings of
libxamarin-app.so for MONO_DIAGNOSTICS, `run-as` for debuggable. Open: cost
on APKs of that size (pull of 50+ MB per session) - cache by package
version / `pm dump` signature, or read only the needed entries remotely.

## U19 - Reuse Desymbolicate's symbol-server lookup
Decided: weaving uses Mono.Cecil, not the reference application's existing Metalama aspect
(C:\Work\ReferenceApp\Metalama.Profiling) - the profiler must not depend on
Metalama. What is still open is the other half: annotate_source needs a local
symbolsDir, while Desymbolicate (C:\Work\ReferenceApp\ExternalTools\Desymbolicate)
fetches the pdbs of a given build from the company symbol server. Adopt that
lookup so a session against a Jenkins-built app can be annotated without having
the build output at hand.

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

## U21 - CLOSED: weaver on the reference application (corrected root cause)
The weaver works on the reference application. Two things had to be fixed, and one earlier
conclusion was wrong and is corrected here:
1. The original `.pdb` next to a rewritten assembly silently prevents the woven
   copy from being used. The deployer moves it aside for the session. This was
   the real blocker.
2. Sessions could leave the app unable to start (stopped=true plus a deleted
   environment file); both are fixed - see the launching and environment notes.
Correction: an earlier note blamed `EmbedAssembliesIntoApk=true` in
App.Droid.csproj. The Debug builds actually used in these runs are
fast-deployed - their APK contains no assemblies at all - so that premise was
wrong. The engine now decides by inspecting the APK (assembly store,
lib_*.dll.so, assemblies/), not by any project property, and refuses on-device
weaving only for apps that really do embed their assemblies.

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


## U23 - The runtime stops instrumenting on a device that has been profiled for a while

Measured on 2026-08-23 (net10 workload, emulator-5556, TestTarget). The
runtime-provider instrumenting engine degrades with use of the device:

1. first the method events go - sessions return allocations and not one MethodEnter,
   whatever the MONO_DIAGNOSTICS spelling;
2. then everything goes - `enter=0 leave=0 allocs=0`, with a correct callspec, a correct
   environment and no error anywhere;
3. **restarting the emulator restores it**: the same code that had produced nothing for an
   hour recorded 50,504 enter/leave pairs alongside the allocations minutes later.

Ruled out along the way: the app process (force-stopped and relaunched between sessions,
the failure survives), state we write (no override environment file, no `debug.mono.*`
property left behind, the failing session's own log shows the right variable applied),
our analyzer (the empty traces contain no MethodEnter when decoded by hand), and the
MONO_DIAGNOSTICS spelling (four variants, same outcome).

What it is not, measured after the restart with the device watched at every step
(free memory, free space in /data, the size of the app's data directory):

- **not exhaustion of anything.** Ten sessions in a row: 204k-235k enter/leave each,
  10,933 allocations each, MemAvailable steady between 774 and 855 MB of 2 GB, /data
  steady at 3.4 GB free, the app's files steady at 73-75 MB. Nothing moves.
- **not where the data goes.** Provider traces are streamed to the host and never touch
  the device; the weaver's event files live in the app's private directory and are wiped
  at the start of every session.
- **not repetition, and not the test suite.** The full device suite - the workload that
  preceded the bad state yesterday - was run again on the fresh emulator and passed
  (76 passed, 7 skipped, 0 failed, 5 minutes), and ten more sessions straight afterwards
  were as healthy as the ten before it.

What the bad state had that the good one does not: an emulator up for three days, which
had also hosted a session left stuck for fourteen hours overnight. That is a correlation
with one observation behind it, not a cause.

Worth noticing for calibration: the sessions called "good" while diagnosing this recorded
223, 2,811 and 2,823 enter events. After the restart the same session records over
200,000. The degradation is a slope, not a switch, and a session can look healthy while
already well down it.

Correction worth keeping: for two hours this was documented as "the runtime serves
allocations or enter/leave, never both", complete with a measurement table. The table was
real; the conclusion was not. Everything in it was measured on a device already in state
(1), and the variable that mattered - how long the device had been profiled - was not in
the table.

Consequences in the product: the session warns when allocations arrive without enter/leave
and tells the user to restart the device; the weaver engine does not depend on the runtime
instrumenting anything and never shows this; and the two provider device tests skip while
a device is in that state, so the suite reports our pipeline rather than the device's age.
