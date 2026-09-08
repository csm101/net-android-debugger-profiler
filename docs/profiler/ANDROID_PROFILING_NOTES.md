# Android profiling notes

Living specification: everything empirically known about collecting profiles
from a .NET for Android app. Facts marked **[verified]** were confirmed on
this machine (emulator DevicePerSviluppoProfiler, API 33 x86_64, .NET SDK
10.0.301, android workload 36.1.43, diagnostics tools 9.0.661903) or in
primary sources; everything else is **[unverified]** until exercised.

## Collection fundamentals

- MonoVM on Android embeds **EventPipe**; diagnostic tools reach it through
  the **dotnet-dsrouter** proxy. **[verified]**
- CPU sampling = dotnet-trace default profile (cpu-sampling =
  Microsoft-DotNETCore-SampleProfiler + runtime JIT/loader events +
  rundown). **[verified - sampling1/sampling2 traces]**
- Profiling works on **Release** builds. **[verified]**

## Build-side switches

    dotnet build -c Release -p:EnableDiagnostics=true   # legacy alias: AndroidEnableProfiler

- EnableDiagnostics=true adds `libmono-component-diagnostics_tracing.so` to
  the APK and bakes `DOTNET_DiagnosticPorts=127.0.0.1:9000,connect,nosuspend`
  into the app environment (obj/.../__environment__.txt -> libxamarin-app.so).
  **[verified]**
- Optional msbuild properties DiagnosticAddress / DiagnosticPort /
  DiagnosticSuspend / DiagnosticListenMode only change that baked
  `DOTNET_DiagnosticPorts` value (target `_GenerateEnvironmentFiles`,
  property `DiagnosticConfiguration`). **[verified - Xamarin.Android.Common.targets]**
- Runtime override without rebuild (device-global, affects every .NET app on
  that device; clear it when done):

      adb -s <serial> shell setprop debug.mono.profile '10.0.2.2:9000,suspend,connect'   # emulator
      adb -s <serial> shell setprop debug.mono.profile '127.0.0.1:9000,suspend,connect'  # device (+ adb reverse)
      adb -s <serial> shell setprop debug.mono.profile ''                                # clear

  `suspend` blocks managed startup until a tool connects and resumes;
  `nosuspend` for attach-at-any-time (gcdump, late sampling). **[verified]**
- Default Release build is profiled-AOT: `libaot-<Assembly>.dll.so` per
  assembly including the app's own. `-p:RunAOTCompilation=false` gives a pure
  JIT build (no libaot-*). Relevant for instrumenting (below). **[verified]**
- Setting a runtime env var (MONO_DIAGNOSTICS) in a Release build: there is no
  `debug.mono.env` in the release libmonodroid (only debug.mono.profile,
  .log, .gc, .trace, .runtime_args, ...). The only way is an environment file
  baked at build time: `@(AndroidEnvironment)` item (lines `VAR=value`), or
  appending to `@(_GeneratedAndroidEnvironment)` with a target
  `BeforeTargets="_GenerateEnvironmentFiles"` (spike hook in
  TestTarget/Profiler/TestTarget.csproj, property `MonoDiagnostics`). The engine must inject it
  without editing the user's csproj (CustomAfterMicrosoftCommonTargets
  import or an env file + item). **[verified]**

## Injecting environment variables without rebuilding (Debug builds)

The Debug flavor of libmonodroid (any `-c Debug` build, fast deployment or
not) does two extra things at startup (`AndroidSystem::setup_environment`,
`#if DEBUG` blocks in src/native/mono/runtime-base/android-system.cc):

1. reads system property `debug.mono.env` (`NAME=VALUE|NAME2=VALUE2`).
   **Unusable for MONO_DIAGNOSTICS**: Android caps property values at 92
   bytes and libmonodroid itself aborts the app when the value exceeds ~90
   bytes (`Attempt to store too much data in a buffer (capacity: 90)`,
   SIGABRT). A value with `enable` + callspec needs ~90+ bytes. **[verified]**
2. loads `files/.__override__/<abi>/environment` from the app's private
   data dir (the file fast deployment pushes for `@(AndroidEnvironment)`),
   applied *after* the baked environment, so it overrides it. Format
   (no newlines): `0x%08X\0` name width, `0x%08X\0` value width (both incl.
   NUL), then records `name` NUL-padded to name width + `value` NUL-padded
   to value width. Writable through `adb shell run-as <pkg>` (debuggable
   app): `rm` the 0400 file, `cp` the new one from /data/local/tmp, `chmod
   400`. **[verified: Debug TestTarget built with EnableDiagnostics only,
   MONO_DIAGNOSTICS injected this way, full instrumenting session OK]**

Consequences: on Debug builds the profiler needs nothing but
`EnableDiagnostics=true` for every mode; it injects MONO_DIAGNOSTICS
(enable/alloc/callspec) per session and restores the file afterwards
(fast deployment rewrites it on the next deploy anyway). Release builds have
neither hook: the environment must be baked at build time. The same
override file also lets the profiler set `DOTNET_DiagnosticPorts` per app
instead of the device-global `debug.mono.profile` property.

## Collecting

One-liner (dotnet-trace >= 9.0.621003; starts dsrouter itself, sampling):

    dotnet-trace collect --dsrouter android-emu -o x.nettrace [--format speedscope] [--duration hh:mm:ss]

Manual (needed for custom providers / gcdump / multiple sessions):

    dotnet-dsrouter android-emu            # IPC server <-> TCP server 127.0.0.1:9000; pid printed
    dotnet-trace collect -p <dsrouter pid> [--providers ...]
    dotnet-gcdump collect -p <dsrouter pid> -o heap.gcdump

- `android-emu` = TCP server on host 127.0.0.1:9000, the app connects to
  10.0.2.2:9000 (emulator -> host loopback). `android` = same plus
  `adb reverse tcp:9000 tcp:9001` for physical devices. **[verified for emu]**
- Choreography that works (suspend mode): setprop -> start dsrouter /
  dotnet-trace -> launch app (`adb shell monkey -p <pkg> -c
  android.intent.category.LAUNCHER 1`) -> app connects, tool resumes it.
  Late tool start with `suspend`: the app sits at the splash screen until the
  tool connects (observed > 1 min without harm on TestTarget). **[verified]**
- The tool-side "process" is dsrouter: `dotnet-trace ps` / `dotnet-gcdump ps`
  list dotnet-dsrouter, not the Android app. **[verified]**
- Multiple emulators: the app's DOTNET_DiagnosticPorts address selects the
  host endpoint, not the device serial. Two emulators would both reach host
  port 9000, so run one dsrouter per device on distinct ports
  (DiagnosticPort / debug.mono.profile port) - dsrouter android-emu has no
  port option in 9.0.661903 (only -rt, -v, -i, -bsig): use the generic
  `server-server` command with explicit `--ipc-server`/`--tcp-server` for a
  second port. **[verified options; multi-port run not yet exercised]**
- Transient failure seen once: dotnet-trace collect -p <dsrouter> right after
  app launch failed with `EndOfStreamException` at session start; immediate
  retry worked. Engine must retry session start. **[verified]**
- Trace sizes on emulator: sampling 30 s TestTarget = 0.9 MB nettrace
  (0.25 MB speedscope); enter/leave with a hot leaf method instrumented =
  12 MB per 20 s (~480k enter + 480k leave). **[verified]**

Known trap: do not launch the app through Visual Studio while a diagnostics
config is active - it freezes on the splash screen. **[verified - docs]**

### Attaching to a running app (no restart)

An app built with EnableDiagnostics carries
`DOTNET_DiagnosticPorts=127.0.0.1:9000,connect,nosuspend`; the runtime keeps
retrying that connection. `adb -s <serial> reverse tcp:9000 tcp:9000`
(emulator: 127.0.0.1 inside the emulator is the emulator itself, so the
reverse is needed there too) + `dotnet-dsrouter android-emu` on the host make
the already-running process connect within seconds: sampling and heap
snapshots of a running Debug app work without restarting it, same pid before
and after (device test `Sampling_attach_to_running_debug_app_without_restart`).
Instrumenting still needs a restart (JIT-time instrumentation). **[verified]**

### Profiling the app the debugger runs

The soft debugger (`debug.mono.extra`) and the diagnostics port are separate parts of the
runtime and work together in one process: an app launched by the debugger and stopped at a
breakpoint accepts a sampling session in attach mode. The runtime connected to dsrouter *while
stopped at the breakpoint* (the diagnostic server thread is not one the debugger suspends), so
the session is Collecting before the app resumes and the first samples after `continue_and_wait`
belong to the code that follows the breakpoint. Same pid throughout; the debug session survives
and a breakpoint set afterwards is hit. The agent's part: `remove_all_breakpoints` and clear the
exception rules first, so nothing suspends the app while it is sampled; the profile measures code
compiled with the debugger attached (reduced optimizations, as in any Debug build). The unified
server's `DeviceArbiter` lets exactly this start through (attach, the same package, an explicit
device) and refuses every other profiler start on a device the debugger holds. Detaching is not
an option: Mono ends the app when the debugger disconnects. Instrumenting through the weaver is
not covered: the weaver session launches the app itself, and an app the debugger launched has no
`NAP_PROFILER_OUT` and nobody to pull its files. **[verified 2026-09-08 -
`DebugAndProfileTogetherTests.SamplingAttach_ToTheAppUnderTheDebugger_ProfilesWhatRunsAfterTheBreakpoint`,
emulator-5554, 22 s]**

Trap found on the way: a weaver session killed half-way leaves `<assembly>.pdb.naporig` in the
override directory. `WeaveDeployer` restores a leftover `.dll.naporig` before weaving again, not
the pdb beside it, so the app runs without symbols from then on: the debugger binds no breakpoint
and `get_source_files` reports no file. Remedy until the engine restores it too:
`run-as <pkg> mv files/.__override__/<abi>/<assembly>.pdb.naporig <assembly>.pdb`. **[seen
2026-09-08 on emulator-5554, left by a session killed on 2026-09-06]**

### Engine collection path (Core)

Core does not spawn dotnet-trace/dotnet-gcdump: it starts dotnet-dsrouter and
drives EventPipe through `Microsoft.Diagnostics.NETCore.Client`
(`DiagnosticsClient(dsrouterPid)`): `GetProcessEnvironment()` as the
"runtime connected" probe, `StartEventPipeSessionAsync(providers,
requestRundown: true)`, `ResumeRuntime()`, `EventStream` copied to
trace.nettrace, `StopAsync()` (rundown arrives before the stream ends). Two guards
added 2026-09-06 after the API 33 emulator: the environment probe has a 3 s deadline
and asks again on a new connection (a runtime connection has been seen whose reply
is held by the emulator's slirp until the app dies), and the drain after the stop ends
when the file has stopped growing for 5 s (dsrouter on that emulator never ends the
stream, although every byte it forwarded is in the file: the runtime writes the rundown
and closes before it acknowledges the stop, so nothing is lost).
Heap snapshots: session with `Microsoft-Windows-DotNETRuntime` keywords
`GCHeapSnapshot` (GC|GCHeapDump|GCHeapCollect|GCHeapAndTypeNames|Type),
parsed live with TraceEvent (`TypeBulkType`, `GCBulkNode`), stopped at the
`GCStop` after the dump; ~10-30 s on the emulator; type names resolve
(`TestTarget.Workloads.AllocHeavyRecord`). Provider sets in
`Collection/EventPipeCollector.cs` (`ProviderSets`). **[verified - device tests]**

## Sampling: what the data looks like

- Provider Microsoft-DotNETCore-SampleProfiler, events appear in TraceEvent
  as `EventWriteString`/`Thread/Sample`; TraceLog attaches managed stacks
  (`ev.CallStackIndex()`), resolves frames through the rundown
  (MethodLoadVerbose / DCStopVerbose). 100 % of stacked samples resolved in
  sampling1 (unresolvedLeaf=0). **[verified]**
- The sampler samples every managed thread, including blocked ones: a thread
  in Thread.Sleep accumulates samples under
  `Interop.Sys.LowLevelMonitor_TimedWait` (93 % of samples in sampling1).
  CPU-only views must classify wait frames (sleep/wait/monitor/epoll PInvoke
  leaves) as blocked time. **[verified]**
- Effective rate on this emulator: ~290 samples/s per thread (8206 samples on
  a thread over 30 s), not the nominal 1 ms. **[verified]**
- Samples without a stack (about half of all sample events) belong to threads
  with no managed frames (main/Java threads); count them separately.
  **[verified]**
- **No line-level sampling on MonoVM**: the runtime emits no
  MethodILToNativeMap events (TraceLog `ILOffset` = -1 everywhere) and the
  sample profiler reports one fixed address per method frame (29 distinct
  code addresses for 6k samples on TestTarget), so samples cannot be mapped
  to IL offsets / source lines. Method tokens (0x06xxxxxx) are in the
  rundown, so source mapping works at method granularity: token -> portable
  pdb sequence points -> line range. **[verified - NetTraceProbe iloffsets]**
- **Leaf attribution characterized (U15, controlled experiment)**: the MonoVM
  sampler attributes samples to the innermost method that owns a real frame,
  and very short leaf methods never get one. TestTarget's LeafProbe runs two
  shapes of equal cost: a single long-bodied call (`LongLeaf`) and a tight
  loop calling a tiny method (`CallTinyLeaf` -> `TinyLeaf`, both NoInlining).
  A 15 s Debug session reports `CallTinyLeaf` 1300 exclusive == 1300
  inclusive and `LongLeaf` 504 exclusive, while `TinyLeaf` does not appear at
  all - not even in inclusive counts. Consequence for every report we
  produce: a method's exclusive samples include the time of its trivial
  callees, so hotspot lists are exact at "method plus its short callees"
  granularity. Long-bodied methods are attributed correctly.
  **[verified - device test Sampling_attributes_a_long_running_leaf_method]**
- AOT vs JIT attribution: with the default profiled-AOT Release build a
  NoInlining leaf method (`CpuBurner.Mix`, 2M calls per iteration) never
  appears in sampled stacks although the rundown lists it as compiled - its
  caller `Busy` absorbs the samples (sampling1). With the JIT build
  (`RunAOTCompilation=false`, sampling2) `Mix` shows up (212 excl. vs 579 for
  `Busy`). Sampling of AOT code loses leaf frames: for precise attribution
  profile JIT builds, or document the caveat for AOT builds. **[verified]**

## A heap snapshot must not suspend the app

Suspending holds the runtime at startup until a session resumes it, which is how sampling
and instrumenting catch app init. A heap dump never resumes it: the app sits frozen, the
warm-up ticks against a process that has allocated nothing, and the session ends with
"Heap snapshot produced no objects" - the same message it gives for an app that is genuinely
still starting, which is what made this look like a timing problem. A longer warm-up does
not help; measured on 2026-08-23 at 5 and 12 seconds, both empty.

Heap sessions therefore launch with `nosuspend` whatever the caller asked for, and say so in
the log. It reached both frontends' defaults - `nap run --mode heap` and the MCP
`profile_run(mode: "heap")` both send SuspendOnStart=true - so the first heap snapshot a new
user took came back empty. Cover: `Heap_snapshot_with_default_settings_captures_objects`.

## Memory: gcdump

- `dotnet-gcdump collect -p <dsrouter pid>` works against MonoVM through
  dsrouter (nosuspend). ~30 s for a 7 MB / 104k-object heap on the emulator
  (the tool waits for the heap walk session to drain), 2 MB .gcdump.
  **[verified]**
- `dotnet-gcdump report` resolves type names (`TestTarget.Workloads.
  AllocHeavyRecord` 50,000 instances, its `[]` 400,032 bytes); its "Object
  Bytes" column is not a per-object size for array types (byte[64] shown as
  32). The raw GCBulkNode sizes are right: Core's own heap-dump session
  reports byte[64] = 96 bytes, AllocHeavyRecord = 40 bytes. **[verified]**

## Runtime instrumenting provider (Microsoft-DotNETRuntimeMonoProfiler)

Present and working in the net10 android workload (strings in
libmono-component-diagnostics_tracing.so; events received). **[verified]**

Enabling - environment variable MONO_DIAGNOSTICS, whitespace-separated
options (parsed by mono_parse_options_from):

    MONO_DIAGNOSTICS=--diagnostic-mono-profiler=enable --diagnostic-mono-profiler=alloc --diagnostic-mono-profiler-callspec=N:My.Namespace

- `--diagnostic-mono-profiler=enable|disable|alloc|exception` (alloc installs
  the allocation hook at startup; exception the exception-clause hook).
- `--diagnostic-mono-profiler-callspec=<callspec>`, Mono callspec grammar:
  `all`, `none`, `program`, `assembly`, `N:Namespace`, `T:Type`,
  `M:Type:Method`, `+EXPR`/`-EXPR`, comma separated. Only matching methods
  get enter/leave instrumentation. Verified: `N:TestTarget.Workloads` -> 7
  instrumented methods, all in that namespace. **[verified]**

Session keywords (ClrEtwAll.man, provider Microsoft-DotNETRuntimeMonoProfiler):

| mask | keyword | notes |
|---|---|---|
| 0x1 | GC | |
| 0x2 | GCHandle | |
| 0x8 | Loader | |
| 0x10 | Jit | JitBegin(8)/JitDone(10: MethodID, ModuleID, token, ...)/JitCodeBuffer(13) |
| 0x4000 / 0x8000 / 0x10000 | Contention / Exception / Threading | |
| 0x100000 | GCHeapDump | |
| 0x200000 | GCAllocation | GCAllocation(39): VTableID u64, ObjectID ptr, ObjectSize u64 |
| 0x400000 / 0x800000 | GCMoves / GCHeapCollect | |
| 0x1000000 / 0x2000000 / 0x4000000 | GCFinalization / GCResize / GCRoot | |
| 0x8000000 | GCHeapDumpVTableClassReference | heap dump emits vtable->class refs |
| 0x20000000 | MethodTracing | MethodEnter(29)/Leave(30)/TailCall(31)/ExceptionLeave(32)/Free(33)/BeginInvoke(34)/EndInvoke(35): MethodID u64 |
| 0x8000000000 | TypeLoading | ClassLoaded(16: ClassID, ModuleID, ClassName), VTableLoaded(19: VTableID, ClassID, AppDomainID) |
| 0x10000000000 | Monitor | |
| 0x40000000000 | MethodInstrumentation | must be on for methods JITted during the session to be instrumented (callspec filters) |

Working probe masks: `0x40020200000:5` (instrumentation+tracing+alloc),
`0x48020200011:5` (+ TypeLoading, Jit, GC). **[verified]**

- Instrumentation is decided at JIT time: the session (with
  MethodInstrumentation keyword) must be running before the methods are
  compiled -> use `suspend`; AOT-compiled methods are never instrumented ->
  build with `-p:RunAOTCompilation=false` for instrumenting sessions.
  **[verified: JIT build instrumented; AOT build not tested, implied by design]**
- Overhead: a hot leaf method (2M calls/iteration) instrumented on the
  emulator runs ~25k call pairs/s (~40 us per enter+leave) - the callspec
  must exclude hot leaves; treat full-app callspecs as unusable. **[verified]**
- Event decoding: **TraceEvent has no parser for this provider** - events
  show as `EventID(n)` with no payload schema; decode by hand from the manifest
  layouts above (DevTools/NetTraceProbe `monoprof`). **[verified]**
- MethodID in MethodEnter/Leave == MethodID of the runtime rundown
  MethodDCStopVerbose events (Microsoft-Windows-DotNETRuntimeRundown, parse
  with ClrRundownTraceEventParser): names resolve for every instrumented
  method. **[verified]**
- VTableID in GCAllocation resolves to a class name only through
  VTableLoaded + ClassLoaded events emitted *during* the session (TypeLoading
  keyword); vtables created before the session (String, char[] ...) stay
  unresolved -> needs a start-of-session type dump (candidate:
  GCHeapDump + GCHeapDumpVTableClassReference keywords) - see KNOWN_UNKNOWNS.
  **[verified gap]**
- **Naming allocations of pre-session types is not possible with this runtime**
  (measured 2026-08-21, see KNOWN_UNKNOWNS U13 for the full list of attempts):
  the heap-dump keywords emit nothing even while a real heap dump walks 104,425
  objects, and the CLR BulkType events Mono does emit use ids that match neither
  VTableID nor ClassID. Unnamed rows keep exact counts and are labelled
  `<type loaded before the session, vtable 0x...>`. **[verified]**
- Unknown `--diagnostic-mono-profiler=` options are ignored silently - no warning
  in logcat, the session still runs - so the accepted option set cannot be
  discovered by trying one. **[verified]**
- `--diagnostic-mono-profiler=alloc` + GCAllocation keyword reports **every**
  allocation with correct sizes: monoprof3 = 123,115 AllocHeavyRecord (40 B)
  + 123,117 byte[] (96 B) for 6 full Allocate() calls of 20,000 each; no
  sampling. Array vtables created during the session resolve through
  ClassLoaded (`System.Byte[]`). **[verified]**
- **A device that has been profiled for a while stops instrumenting methods.** Measured
  on 2026-08-23, net10 workload, emulator-5556. After an afternoon of device tests the
  provider engine returned allocations and not one MethodEnter, whatever the
  MONO_DIAGNOSTICS spelling - `enable alloc`, `enable,alloc`, the order reversed - and
  sometimes nothing at all, allocations included. Traces decoded by hand with
  `DevTools/NetTraceProbe monoprof`, so this is the trace's content, not our analyzer's
  reading of it:

  | MONO_DIAGNOSTICS | GCAllocation keyword | MethodEnter | GCAllocation events |
  |---|---|---|---|
  | `enable` + callspec | off | 551 | 0 |
  | `enable alloc` + callspec | on | 0 | 10,466 |
  | `enable,alloc` + callspec | on | 0 | 10,809 |
  | `alloc enable` + callspec | on | 0 | 10,699 |
  | `enable` + callspec | on | 0 | 0 |

  For two hours this read as "the runtime serves allocations or enter/leave, never both",
  and it was written down as such. It is wrong: **a restarted emulator produced both from
  the same code minutes later** - 50,504 calls of NewRecord with real durations alongside
  the allocations - which is what the August measurement had recorded all along. The
  variable that mattered was not in the table.

  What holds: the degradation costs the method events first and the allocations later, it
  survives force-stopping and relaunching the app, it leaves no trace in the app's data
  directory or in system properties, and **restarting the device clears it**. The session
  warns when allocations arrive without enter/leave and says to restart. The weaver engine
  is unaffected - it does not rely on the runtime instrumenting anything - and is the
  answer for a long profiling session on a tired device.

  It is not the device running out of anything. Measured after the restart, ten sessions
  in a row: 204k-235k enter/leave and 10,933 allocations each, free memory steady at
  774-855 MB of 2 GB, /data steady at 3.4 GB free, the app's data directory steady at
  73-75 MB - and the full device suite in between changed none of it. Provider traces are
  streamed to the host and never stored on the device; the weaver's event files are wiped
  at the start of every session. Calibration worth keeping: sessions that looked healthy
  while diagnosing this recorded 223 to 2,823 enter events, against 200,000+ on the fresh
  emulator - the slope starts long before the number reaches zero. Open: KNOWN_UNKNOWNS U23.
- Enter/leave nesting per thread is consistent (depth 3 = Loop > Busy > Mix;
  enter count = leave count + still-open frames at session end). **[verified]**
- Overhead with a realistic callspec (NewRecord + ctor instrumented, 40k
  enter/leave + 40k alloc events per iteration): ~9 iterations / 20 s vs
  ~100 uninstrumented -> roughly 10 us per event on the emulator. Event
  volume, not instrumentation, is the cost: 15 MB / 20 s. **[verified]**
- Instrumentation persists for the process lifetime: once a method was
  JITted during an instrumenting session, later sessions with the
  MethodTracing keyword receive its enter/leave again (monoprof4, 4 s,
  53k pairs, no JitDone events). Useful for start/stop cycles without
  relaunch; the first session still needs `suspend`. **[verified]**
- Callspec exclusion syntax (`-M:Type:Method`) not yet validated: the run
  that used it crashed for an unrelated reason (below); re-test in P3.
  Positive lists (`M:Type:Method,T:Type,...`) work. **[verified partial]**
- **Trap - incremental build + env change:** changing the `MonoDiagnostics`
  value (i.e. `__environment__.txt`) on an incremental Release build
  produced an APK that crashed at startup only while a diagnostics session
  resumed it (`Java.Lang.LinkageError: No implementation found for
  ...MainActivity.n_onCreate`), both on update-install and after
  uninstall/reinstall. Deleting obj/ and bin/ and rebuilding fixed it. The
  engine must clean-build (or at least wipe obj/) whenever it changes the
  baked environment. **[verified]**
- Unknown-but-harmless event IDs seen alongside: 14 (ClassLoading, one per
  ClassLoaded), 11 (JitChunkCreated), 62 (one per JitDone - not in the
  manifest snapshot used; ignore). **[verified]**

## What a call costs, and why there are two weaver modes

Deterministic instrumenting costs an enter and a leave per call, whichever engine inserts
them. What differs is what happens next, and that is worth an order of magnitude.

**trace** writes a record per event: the order of calls and every single duration survive,
and both the cost and the volume grow with the number of calls.

**tree** keeps a calling context tree in the app - one node per call path, holding calls,
inclusive and exclusive time, minimum and maximum. Everything a call tree, a call graph,
parents/children and a critical path are built from survives; the order of calls and
individual durations beyond min/max do not. This is how AQTime has always worked, and it
is what makes instrumenting a whole application affordable rather than a namespace at a
time.

Measured on the host (`DevTools/WeaveBench`, Fib(27), 1,028,457 calls, x64 Release):

| | ns per call | data produced |
|---|---|---|
| not instrumented | 3 | - |
| woven, collector disabled | 7 | - |
| trace | 123-131 | 16.1 MB |
| tree | 55-66 | 1.6 KB |

Measured on the device (emulator-5556, TestTarget, `N:TestTarget.Workloads`, 8 s):

| engine | calls recorded | data pulled |
|---|---|---|
| weaver (trace) | 346,589 | 8.6 MB |
| weaver-tree | 361,934 | 1 KB |

The tree records *more* calls in the same window because it slows the app down less. The
remaining per-call cost is the two timestamps; the child lookup is a walk of a short array
list rather than a hash, which was worth ~20 ns per call when it was measured.

Allocations stay events in both modes - one record each, in the .napw stream - because
their volume is the caller's choice (trackAllocations). In tree mode that costs their call
site: allocations are reported by type, not by allocating method, since the site came from
replaying enter/leave and nothing is replayed. Choose trace when the site matters.

## Weaver instrumenting (plan B, works on net9)

On-device IL weaving instead of the runtime provider (the provider is unusable
on net9 - U20). Verified 2026-08-20 on TestTarget (net10):

- The app's own managed assemblies are writable files in
  `files/.__override__/<abi>/*.dll` on a Debug/fast-deployment build. The
  engine pulls the selected ones (`run-as cat`), weaves them with Mono.Cecil
  (Enter/try/finally/Leave into filtered methods), pushes the woven copies
  back (backup `<dll>.naporig`, restore afterwards) plus the collector
  assembly `NetAndroidProfiler.Collector.dll`, and points the app at an
  events directory with `NAP_PROFILER_OUT` in the override environment.
  **[verified]**
- The collector must not touch `System.Diagnostics.Process` in its static
  init: on Android it can throw and silently disable itself (observed - no
  events). Files are keyed by a per-process GUID token + managed thread id.
  **[verified]**
- Force-stopping the app fires the collector's ProcessExit flush; the engine
  then pulls the `.napw` files (`run-as cat`) and parses them
  (Core/Weaving/WeaveAnalyzer) into the same InstrumentingResult /
  timing_* tables as the provider path. Loop/Busy that never return in the
  window simply produce no Leave (only completed calls are timed).
  **[verified]**
- Overhead is the same order as the provider (enter/leave per call): a tight
  leaf loop (Mix, 2M/iteration uninstrumented) drops to ~1500 calls in 8 s
  when woven - keep hot leaves out of the weave filter, same as callspec.
  **[verified]**
- MONO_DIAGNOSTICS / dsrouter / EventPipe are NOT used by this path: no
  suspend, no diagnostics port, works regardless of the U20 provider bug.
  **[verified]**
- **Build-time weaving works on the real app as it ships**: the reference application keeps
  `EmbedAssembliesIntoApk=true`, and a build with `-p:NapWeave=true
  -p:NapCallspec="T:App.Droid.AppApplication"` (targets handed to MSBuild with
  `-p:CustomAfterMicrosoftCommonTargets=...`, no csproj edit) wove 13 methods,
  skipped 2 property accessors, flagged 3 async stubs, and produced real
  timings in a 12 s session that deployed nothing: IsMainProcess 173 ms
  (2 calls), InizializzaApplicazione 135 ms (30.9 ms self), ..ctor 133 ms,
  OnCreate 95.6 ms, AttendiTermineInizializzazione 3.4 ms.
  **[verified - Build_time_weaving_session_on_the reference application]**
- **Async bodies are woven by default and work on the real app**: for a matching
  async method the weaver instruments both the synchronous stub and the
  compiler-generated `MoveNext`, the second reported as
  `<Type>.<Method> (async body)` - its calls are resumptions, its time excludes
  the awaits. Measured on App.Droid (net9, build-time weaving): `OnCreate` 297.6 ms
  inclusive / 2.2 ms self as a stub, while `OnCreate (async body)` shows 3
  resumptions and 284.7 ms self - i.e. without the body the async method's own
  work is invisible. Five consecutive launches of the woven build, no crash.
  **[verified - Build_time_weaving_records_async_bodies_on_the reference application]**
- **Iterators are instrumented the same way** (`<Type>.<Method> (iterator body)`):
  one call per item produced, plus the MoveNext that ends the sequence. Measured on
  TestTarget (net10, on-device weaving of a single type): 813 enter / 813 leave and
  29 allocations from 4 woven methods in a 10 s window. Real-world density is low -
  App.Core has 6 iterator methods against 10064 async ones, and one of the six is a
  property getter, skipped with the other accessors.
  **[verified - Weaver_instruments_iterator_bodies_on_the_device,
  Iterator_state_machine_records_every_produced_item]**
- **The collector's periodic flush must be rooted.** Its 1 s flush timer was a local
  kept alive with `GC.KeepAlive` inside the static constructor, so it became garbage
  as soon as the constructor returned. Events then only reached disk when a thread
  filled its 64 KB stream buffer - invisible for a wide weave scope, total data loss
  for a narrow one: a session on a single type pulled two 0-byte .napw files while
  the app was demonstrably running the woven code. The timer now lives in a static
  field. **[verified - the same session records 813 enter/leave after the fix]**
- **Republish nap-weave after changing the weaver.** `build/NetAndroidProfiler.Weaving.targets`
  runs the *published* copy in `build/tools`, so a rebuilt solution still weaves with
  the old tool: a new feature looks like it silently does nothing (iterator support
  appeared to find zero iterators in a 31k-method assembly). `dotnet publish
  src/NetAndroidProfiler.Weave -c Release -o build/tools`.
- **Correction**: an earlier note claimed a build with woven async bodies would
  not start at all (process never forked, nothing in logcat). It does start.
  That symptom is the signature of two defects fixed afterwards - a package left
  in `stopped=true` by force-stop, which swallows a plain `am start`, and an
  override environment file written into an app that embeds its assemblies - and
  the evidence for the async claim was collected before both fixes. Iterators
  (`yield return`) are covered too, see the iterator note above.
- Two prerequisites for weaving *on the device*, both discovered on the reference application:
  the app must not embed its assemblies (`EmbedAssembliesIntoApk=false`, or
  the woven copies are dead files), and the original `.pdb` next to a
  rewritten assembly must be moved aside or the runtime silently does not use
  the woven copy. **[verified - App.Droid: AppApplication..ctor 8.78 s,
  OnCreate 4.54 s recorded]**
- **The environment override file is written by rename, and an empty one is
  tolerated.** Copying onto the live path leaves a truncated or zero-length file
  if anything dies mid-write (seen after the emulator's qemu process crashed
  during a suite run: `files/.__override__/x86_64/environment` left at 0 bytes),
  and the engine then refused every later session on that app with "exists but
  could not be read" - unrecoverable for anyone who does not know about the file.
  The engine now stages the file next to the target and renames it into place,
  compares what it read against `stat -c %s`, and treats an existing empty file
  as "no variables" while still refusing a short read of a non-empty one.
  **[verified - Session_runs_when_the_app_has_an_empty_override_environment_file]**
- The profiler must never delete an app's override environment file it did not
  create: without that file the app does not start, silently. The engine now
  distinguishes "no file" from "file present but unreadable" and refuses to
  touch it in the second case. **[verified]**
- **An app with embedded assemblies must not get an override environment
  file**: writing `files/.__override__/<abi>/environment` creates that
  directory, the runtime then expects to load its assemblies from there and
  the app stops starting (no crash in logcat). Build-time weaving therefore
  bakes `NAP_PROFILER_OUT` / `NAP_PROFILER_MARKER_DIR` into the app through
  an `@(AndroidEnvironment)` file, and the session injects nothing.
  **[verified - V7 build-time session green only after this change]**
- **Switching an app between fast deployment and embedded assemblies leaves a
  stale `files/.__override__/<abi>/` behind** (441 files observed on V7 after
  moving back to `EmbedAssembliesIntoApk=true`): the runtime keeps preferring
  those copies and the app can stop starting entirely, with no crash in
  logcat. Clear the directory (`run-as <pkg> rm -rf files/.__override__`) or
  uninstall before reinstalling in the other shape. **[verified]**
- Reading files out of the app sandbox: `adb exec-out run-as ... cat` must
  drain stdout to EOF *before* waiting for the process to exit, otherwise the
  payload can be truncated (2 KB of a 6656-byte assembly observed); the engine
  also verifies the size against `stat` and retries. `/data/local/tmp` is not
  usable for staging (the app user cannot write there). **[verified]**
- **Launching after a force-stop**: a force-stop leaves the package in
  `stopped=true` (visible in `dumpsys package <pkg>`), and for some apps a
  plain `am start` (even with `-S`) is then accepted without ever forking a
  process - nothing is logged at all. A launcher-style intent
  (`monkey -p <pkg> -c android.intent.category.LAUNCHER 1`) clears that state
  and starts the app, so it is the engine's primary launch path, with
  `am start -n <component>` as the fallback for packages without a launcher
  activity. This was behind repeated "the app will not start any more after a
  session" incidents on the reference application. **[verified]**
- `am start -W` waits for the activity to become idle and times out on a
  heavily instrumented app: start without `-W`. **[verified]**
- Weave scope drives feasibility: on the reference application (emulator) weaving a single type
  (15 methods) starts and records normally, while weaving the whole
  `App.Droid` namespace (7882 methods) leaves the app still in Java-side class
  verification after 2 minutes - alive but nowhere near managed code. Narrow
  callspecs only; use sampling to choose them. **[verified]**

## Analysis

- .nettrace parses with **TraceEvent** (Microsoft.Diagnostics.Tracing.TraceEvent
  3.1.23, MIT, NuGet): `EventPipeEventSource` for raw events,
  `TraceLog.CreateFromEventPipeDataFile` for stacks/symbols. Provider/event
  names only resolve when the Dynamic parser is attached (`src.Dynamic.All +=
  ...`), otherwise `Provider(<guid>)`. **[verified]**
- Output formats: .nettrace (PerfView/VS), speedscope JSON, .gcdump.
  **[verified]**

## Reference implementations (MIT, read-only)

- dotnet/diagnostics (dotnet-trace, dotnet-dsrouter, dotnet-gcdump sources)
- microsoft/perfview (TraceEvent + analysis algorithms)
- dotnet/runtime src/mono/mono/eventpipe/ep-rt-mono-profiler-provider.c
  (MonoProfiler provider), src/coreclr/vm/ClrEtwAll.man (event layouts)
- jonathanpeppers/Mono.Profiler.Android (mono log profiler support for
  .NET Android - alternative/legacy collection path worth reading)
- Fody + MethodTimer.Fody (IL weaving enter/leave pattern for P3)

## Attach mode needs a port the app already has

`LaunchMode.Attach` profiles a process that is already running, so nothing the
session does can give it a diagnostics port: the app must have been started with
one (baked `DOTNET_DiagnosticPorts` in the build, the app's override environment
file, or the device property `debug.mono.profile`). TestTarget bakes none, so an
attach session against it only works when something configured the environment
before the app started - for a long time the device tests were passing on
leftovers of earlier restart sessions, and stopped as soon as those leftovers
were cleaned up correctly. The engine now checks the three sources up front and
fails immediately with that guidance instead of waiting for a runtime that will
never connect. **[verified - the three attach tests now configure the port
themselves and pass in 18-28 s, against 60-90 s of waiting before]**

## Trace growth and the size limit

| what | rate | 512 MB reached in |
|---|---|---|
| sampling TestTarget | ~30 KB/s | ~4.7 h |
| sampling the reference application (real app) | ~1.5 MB/s (38 MB / 25 s) | ~5.7 min |
| instrumenting `N:TestTarget.Workloads` | ~0.72 MB/s (2 MB / 2.9 s) | ~12 min |

Collection stops when the trace reaches `SessionSpec.MaxTraceBytes` (default
512 MB; MCP `maxTraceMb`, 0 = unlimited) and the session carries a warning saying
so. The file overshoots the limit by what is already in flight - measured 2.96 MB
against a 2 MB limit - because the check runs every 250 ms while the copy keeps
draining. Stopping this way is clean: the runtime emits its rundown, so method
names still resolve in the part that was collected. **[verified -
Collection_stops_when_the_trace_reaches_its_size_limit]**

## Child processes must never inherit stdin

Every process the profiler starts - adb, dotnet-dsrouter - is started with
`RedirectStandardInput = true` and its stdin closed straight away, even when we have
nothing to send it. A child started without that inherits the parent's stdin, and both
`adb shell` and dsrouter read from it.

In a frontend that speaks over stdio this is not cosmetic: the MCP server's stdin *is*
the client's request stream. Measured symptom, before the fix: `profile_start` answered,
the next three `profile_status` calls answered, and then the server went silent for ever -
no error, no log line, the process alive and idle, because the request had been swallowed
by a child. A test left running overnight was still waiting in the morning. The same
session driven by `nap` was unaffected, which is what makes it easy to miss: only the
stdio frontend loses its input.

Regression cover: `McpDeviceTests.Profile_start_and_profile_stop_round_trip`, which polls
`profile_status` while a session starts. It hung indefinitely before the fix and takes 17
seconds after it.

## One session at a time per device

dotnet-dsrouter 9.0.x has no port option, so one machine runs one router on
127.0.0.1:9000. A second one exits immediately with "only one usage of each
socket address is normally permitted" - and, because it also prints
`Stopping IPC server (...) <--> TCP server (127.0.0.1:9000) router.`, a startup
check that matched `<--> TCP server` took the dying router for a healthy one and
the session failed minutes later with an unrelated transport error. The engine
now accepts only `Starting IPC server`, reports the router's own error, refuses
to start when the port is already taken, and kills the process on every failure
path including cancellation (a leaked router blocked every later session).

The port is also taken by things that have nothing to do with profiling. Seen on
2026-09-05: Docker Desktop publishing a MinIO container on `127.0.0.1:9000-9001`
(`docker ps` shows the mapping; `Get-NetTCPConnection -LocalPort 9000` names
`com.docker.backend`). The engine refuses to start with the "port 9000 is already in
use" message; until dsrouter can be told another port (U12b) the only remedy is to
stop whatever listens there. 9001 matters too: it is the device-side port of the
`adb reverse` flow for physical devices.

A device serves one profiling session at a time. The diagnostics port
(`DOTNET_DiagnosticPorts`, host side 9000 through dsrouter) is reachable by every
.NET app on the device, a session force-stops and relaunches the app it profiles,
and an app profiled earlier keeps reconnecting to that port until it is stopped.
Two sessions overlapping on one device therefore cross-connect: the engine detects
it (the session marker in the error names the *other* session) and fails rather
than analyzing another app's trace. Consequences: the device test classes run
serialized (xUnit collection `"device"`), and profiling two apps at once needs two
devices (KNOWN_UNKNOWNS U12b). **[verified - four device tests failed in the
2026-08-21 suite run with exactly this signature, one of them naming the other
class's session marker]**

## This machine

- adb 1.0.41 (36.0.0), on PATH; emulators: emulator-5554 =
  pixel_7_-_api_33_0 (debugger project), emulator-5556 =
  DevicePerSviluppoProfiler (this project); both API 33 x86_64. Map serial ->
  AVD with `adb -s <serial> emu avd name`.
- 2026-09-05, fresh checkout on the current machine: the AVDs are
  `pixel_7_-_api_30` (running headless as emulator-5554, shared with the debugger
  project's tests, which keep bringing their own TestTarget to the foreground)
  and `pixel_5_-_api_22_0` (too old for a net10.0-android app); there is **no
  DevicePerSviluppoProfiler**, so the device tests' default serial emulator-5556
  does not exist here (NAP_TEST_SERIAL). A Redmi Note 8 Pro (API 30, arm64-v8a) is
  attached over USB - the first physical device for U10. adb needs its full path
  from a fresh PowerShell (`C:\Program Files (x86)\Android\android-sdk\platform-tools`).
- 2026-09-06, monorepo: one emulator serves both products' suites in turn, emulator-5554 =
  `pixel_7_-_api_30` (API 30); set `NAP_TEST_SERIAL=emulator-5554` for the device tests
  (their default is still emulator-5556) and `NAD_DEVICE_SERIAL=emulator-5554` for the
  debugger's. The two device suites never run at the same time on one device: the debugger
  sets `debug.mono.extra` device-wide while it runs, and any Mono app starting meanwhile
  would wait for a debugger. .NET SDK 10.0.400; adb is not on PATH (both products locate it).
- 2026-09-06, later: an API 33 AVD (`api_33_0`) is back, started as emulator-5556 through
  `AVD=api_33_0 SERIAL=emulator-5556 bash DevTools/scripts/ensure-emulator.sh`. Trap seen all day:
  a TestTarget left alive by an interrupted session keeps `DOTNET_DiagnosticPorts=10.0.2.2:9000,
  suspend,connect` in its override environment and reconnects to the router of every later
  session, from whichever emulator it lives on (all reach the host as 10.0.2.2, and dsrouter
  android-emu has no port option); the session then waits for a runtime that never comes or
  hangs on its stop. Force-stop the package on every emulator and kill leftover dsrouters before
  a run. With port 9000 free (a Docker service had held it), `api_33_0` showed two
  transport stalls, both fixed in `EventPipeCollector` the same evening (see "Engine
  collection path"): a runtime connection whose reply the emulator holds until the app
  dies (the environment probe waited on it for minutes), and an event stream the router
  never ends after the stop although every byte is through. With the two guards the
  device suite passes on `api_33_0`: 25 passed, 7 skipped by design, 7 min 14 s; the
  probe was abandoned 19 times in that run, each retry answered within milliseconds. A
  killed session leaves its override environment behind and an incremental install keeps
  it: uninstall, then install. On `pixel_7_-_api_30` a run broke off with every adb command
  timing out at 60 s (`pm path`, `run-as`) while the guest sat at load average 15; the test
  host crashed after that, and a later run on the idle emulator passed 25/25. Check `adb shell
  uptime` before blaming a session for a stalled device.
- .NET SDK 10.0.301, workloads: android 36.1.43 (VS 18.7); net10.0-android
  templates; no net9 android pack installed (the reference application is net9.0-android35.0 -
  check it builds here before P1 integration).
- dotnet-trace / dotnet-dsrouter / dotnet-gcdump 9.0.661903 (global tools).

## Real target: the reference application

- C:\Work\ReferenceApp\the reference application.sln, app project App.Droid,
  TFM net9.0-android35.0, ApplicationId=App.Droid, RunAOTCompilation=false.
- Builds here with `-c Debug -p:EnableDiagnostics=true` (SDK pack 35.0.105
  auto-resolved); APK 24.7 MB, diagnostics component present, no libaot-*,
  21 portable pdbs. **[verified 2026-08-20]**
- **Sampling works end-to-end**: 25 s restart session -> 12,120 samples,
  4,373 methods, V7 startup hot path resolved (AppApplication.
  InizializzaApplicazione -> IoCContainerDroid.Register ->
  EnumRegistration.Register, Unity container). trace.nettrace ~38 MB for
  25 s (vs 0.7 MB for TestTarget: real app has far more managed activity).
  **[verified]**
- **Instrumenting is unavailable on net9 apps**: any
  `--diagnostic-mono-profiler-callspec=` value makes the net9 MonoVM
  SIGSEGV during runtime init (registration of the instrumentation filter
  callback; confirmed by env bisection - enable and enable+alloc boot fine,
  callspec dies). net10 (TestTarget) is unaffected. See KNOWN_UNKNOWNS U20;
  instrumenting on the reference application goes through the Cecil weaver (P3). **[verified]**
- **Launching is unreliable in both directions and must be verified.** A
  force-stop leaves the package in `stopped=true` and a plain `am start -n
  <component>` is then accepted without forking the process; a launcher-style
  `monkey -p <pkg> -c LAUNCHER 1` clears that state but has been seen doing
  exactly the same (`ActivityTaskManager: START ...` in logcat, no `Start proc`,
  no process, nothing else logged). Both were mistaken for "the app will not
  start" - the wrong conclusion that produced U8. `AdbClient.LaunchAsync` now
  tries monkey, polls `pidof` for up to 15 s, falls back to the resolved
  activity with `am start -n`, and repeats the pair once before failing with
  what it attempted. Do not add `-W`: waiting for the activity to go idle can
  take minutes on an instrumented app. **[verified 2026-08-21 - App.Droid: a
  monkey intent that spawned nothing, an explicit start seconds later that
  worked, same app, same emulator]**
- A restart session that launched the app leaves the injected
  DOTNET_DiagnosticPorts in the app's process environment, so the app keeps
  reconnecting to any later dsrouter on the same port and would be profiled
  instead of the next target. The engine force-stops an app it launched at
  the end of the session (unless KeepAppRunning) and tags the app with
  NAP_SESSION=<id>, which the collector verifies on connect (rejects a
  stranger reconnecting). **[verified]**

## Sources

- https://github.com/dotnet/android/blob/main/Documentation/guides/tracing.md
- https://github.com/dotnet/runtime/blob/main/docs/design/mono/diagnostics-tracing.md
- https://github.com/dotnet/runtime/blob/main/src/coreclr/vm/ClrEtwAll.man
- https://github.com/microsoft/perfview
- https://github.com/jonathanpeppers/Mono.Profiler.Android

## Multi-device rule

This machine can run two emulators at once (debugger project: AVD
pixel_7_-_api_33_0; this project: AVD DevicePerSviluppoProfiler). Never rely
on adb's single-device default: pass the serial explicitly (adb -s <serial>,
or ANDROID_SERIAL env var) in every orchestration command. dsrouter/trace
device selection: see "Collecting" above (one dsrouter port per device).
