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
  TestTarget.csproj, property `MonoDiagnostics`). The engine must inject it
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

### Engine collection path (Core)

Core does not spawn dotnet-trace/dotnet-gcdump: it starts dotnet-dsrouter and
drives EventPipe through `Microsoft.Diagnostics.NETCore.Client`
(`DiagnosticsClient(dsrouterPid)`): `GetProcessEnvironment()` as the
"runtime connected" probe, `StartEventPipeSessionAsync(providers,
requestRundown: true)`, `ResumeRuntime()`, `EventStream` copied to
trace.nettrace, `StopAsync()` (rundown arrives before the stream ends).
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
- AOT vs JIT attribution: with the default profiled-AOT Release build a
  NoInlining leaf method (`CpuBurner.Mix`, 2M calls per iteration) never
  appears in sampled stacks although the rundown lists it as compiled - its
  caller `Busy` absorbs the samples (sampling1). With the JIT build
  (`RunAOTCompilation=false`, sampling2) `Mix` shows up (212 excl. vs 579 for
  `Busy`). Sampling of AOT code loses leaf frames: for precise attribution
  profile JIT builds, or document the caveat for AOT builds. **[verified]**

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
- `--diagnostic-mono-profiler=alloc` + GCAllocation keyword reports **every**
  allocation with correct sizes: monoprof3 = 123,115 AllocHeavyRecord (40 B)
  + 123,117 byte[] (96 B) for 6 full Allocate() calls of 20,000 each; no
  sampling. Array vtables created during the session resolve through
  ClassLoaded (`System.Byte[]`). **[verified]**
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

## This machine

- adb 1.0.41 (36.0.0), on PATH; emulators: emulator-5554 =
  pixel_7_-_api_33_0 (debugger project), emulator-5556 =
  DevicePerSviluppoProfiler (this project); both API 33 x86_64. Map serial ->
  AVD with `adb -s <serial> emu avd name`.
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
- **Launching**: `monkey -p <pkg> -c LAUNCHER 1` sometimes issues the START
  intent but never spawns the process after force-stop cycles (observed
  repeatedly with App.Droid; TestTarget unaffected). Resolve the activity
  (`cmd package resolve-activity --brief -c android.intent.category.LAUNCHER
  <pkg>`) and use explicit `am start -W -n <component>`; AdbClient.LaunchAsync
  does this with monkey as fallback. **[verified]**
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
