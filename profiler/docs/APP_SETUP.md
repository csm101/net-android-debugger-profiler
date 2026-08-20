# Preparing an app for profiling

The profiler never rebuilds your app: it profiles the APK exactly as you
built it and checks that the build satisfies the prerequisites of the
requested profiling mode. This document explains what each mode needs and
how to add it to the app project with the least impact on normal Debug
builds.

Verified on .NET SDK 10.0.301 / android workload 36.1.43; facts marked
*(to verify)* are still open and tracked in KNOWN_UNKNOWNS.md.

## Short version

- **Debug builds** (`-c Debug`, the normal development build): add
  `EnableDiagnostics=true` and nothing else. Every mode works - sampling,
  heap snapshots, instrumenting with exact allocations. The profiler
  injects the instrumenting settings (`MONO_DIAGNOSTICS`) into the installed
  app's private environment file per session, without rebuilding, and only
  while profiling. Sampling and heap snapshots can also **attach to the app
  while it is already running** (no restart, same process): the runtime keeps
  retrying its default diagnostics connection and the profiler routes it
  through `adb reverse`.
- **Release builds**: `EnableDiagnostics=true` gives sampling and heap
  snapshots. Instrumenting additionally needs `MONO_DIAGNOSTICS` baked into
  the APK (environment file, see below) and a JIT build (no AOT).

## Weaver instrumenting: fast deployment is required

The weaver rewrites the app's assemblies where they live on the device, in
`files/.__override__/<abi>/` (fast deployment). An app built with
`EmbedAssembliesIntoApk=true` loads its assemblies from inside the APK
instead, so the rewritten copies are ignored and no data is recorded - the
profiler detects this and tells you. For a profiling build:

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Debug'">
  <EmbedAssembliesIntoApk>false</EmbedAssembliesIntoApk>  <!-- Debug default; the reference application overrides it to true -->
</PropertyGroup>
```

(Or build with `-p:EmbedAssembliesIntoApk=false` and reinstall.) Sampling and
heap snapshots are unaffected and work with embedded assemblies.

The weaver also needs the original `.pdb` of a rewritten assembly out of the
way; the profiler moves it aside for the duration of the session and restores
it afterwards - nothing to do on your side.

### Apps that must keep their assemblies embedded: weave at build time

When `EmbedAssembliesIntoApk=true` cannot be changed, weave during the build
instead. Publish the tool once and import the targets file in the app project:

```
dotnet publish src/NetAndroidProfiler.Weave -c Release -o build/tools     # once
```

```xml
<Import Project="<net-android-profiler>\build\NetAndroidProfiler.Weaving.targets" />
```

Or, without editing the app project at all, hand the targets file to MSBuild
for that one build with `-p:CustomAfterMicrosoftCommonTargets=<path>`:

```
dotnet build -c Debug -t:Install -p:EnableDiagnostics=true \
  -p:NapWeave=true -p:NapCallspec="T:My.App.Services.SyncService" \
  -p:CustomAfterMicrosoftCommonTargets="C:\tools\net-android-profiler\build\NetAndroidProfiler.Weaving.targets"
```

The build rewrites the app assembly before packaging, copies the collector next
to the output and writes `nap-weave.map` in the output folder. Extra weaver
options go through `-p:NapWeaveArgs="--allocations"`; the build uses the
*published* tool in `build/tools`, so republish it after updating the profiler. Profile it with
the weaver engine pointing at that map (MCP: `engine=weaver`,
`weaveMapPath=<OutDir>\nap-weave.map`): the profiler then changes nothing on the
device, it only configures the run and reads the results. Nothing is woven
unless `NapWeave=true`, so normal builds are unaffected.

If you switch an app between fast deployment and embedded assemblies, clear the
old fast-deployment copies first (`adb shell run-as <package> rm -rf
files/.__override__`, or simply uninstall): the runtime prefers whatever is in
that directory, and stale assemblies can stop the app from starting at all.

**Keep the weave filter narrow.** Every woven method costs an Enter/Leave pair
per call, and the cost is paid from the very first line of startup. Measured on
the reference application (emulator): weaving one type (15 methods) runs normally, weaving the
whole `App.Droid` namespace (7882 methods) makes startup so slow that the app
had not reached managed code after two minutes. Use sampling first to find the
area of interest, then weave that type or a handful of types.

## What each mode needs

| Mode | Build requirement | Notes |
|---|---|---|
| CPU sampling | `EnableDiagnostics=true` | Nothing else. The profiler points the app at the tool at run time (device property `debug.mono.profile`, or the per-app environment file on Debug builds). |
| Heap snapshot (gcdump) | `EnableDiagnostics=true` | Same. |
| Instrumenting (enter/leave, exact allocations) | Debug: `EnableDiagnostics=true` only. Release: `EnableDiagnostics=true` **and** `MONO_DIAGNOSTICS` baked in the app environment **and** JIT (no AOT) | On Debug builds the runtime reads an environment override file from the app's data directory, which the profiler rewrites through `adb run-as` (debuggable app). Release runtimes have no such hook. AOT-compiled methods are never instrumented. |
| Source-line annotation | portable pdb files of the build available to the profiler | Keep `DebugType=portable` (default) and do not delete the pdbs. |

`EnableDiagnostics=true` adds `libmono-component-diagnostics_tracing.so` to
the APK and bakes `DOTNET_DiagnosticPorts=127.0.0.1:9000,connect,nosuspend`
into the app environment. With nothing listening on that port the app starts
normally *(to verify on Debug builds with fast deployment - U17)*.

## Debug builds: one property

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Debug'">
  <EnableDiagnostics>true</EnableDiagnostics>
</PropertyGroup>
```

Cost when not profiling: the diagnostics component is loaded (~270 KB) and
the runtime makes one background connect attempt to 127.0.0.1:9000 at
startup (nosuspend) *(to verify on the real app - U4b)*. Nothing else
changes: no environment variable, no allocation hook, no instrumentation.

## Release builds: a dedicated `Profiling` configuration

Keep Release untouched and add a configuration that inherits from it. the reference application
already defines a `Profiling` configuration in App.Droid.csproj; extend it:

```xml
<!-- App.Droid.csproj (or any app csproj) -->
<PropertyGroup Condition="'$(Configuration)' == 'Profiling'">
  <DebugSymbols>true</DebugSymbols>
  <DebugType>portable</DebugType>
  <Optimize>false</Optimize>              <!-- same as Debug, keep line info exact -->
  <EnableDiagnostics>true</EnableDiagnostics>
  <RunAOTCompilation>false</RunAOTCompilation>
  <AndroidEnableProfiledAot>false</AndroidEnableProfiledAot>
</PropertyGroup>

<ItemGroup Condition="'$(Configuration)' == 'Profiling'">
  <AndroidEnvironment Include="profiling.env" />
</ItemGroup>
```

`profiling.env` next to the csproj (one `NAME=value` per line, the value
may contain spaces):

```
MONO_DIAGNOSTICS=--diagnostic-mono-profiler=enable --diagnostic-mono-profiler=alloc --diagnostic-mono-profiler-callspec=N:V7
```

Build, deploy and run it as you do with Debug:

```
dotnet build -c Profiling -t:Install -p:AdbTarget="-s <serial>" App.Droid.csproj
```

### Alternative: property switch on top of Debug

If you prefer no extra configuration, gate the same settings on a property
and pass `-p:NetAndroidProfiler=true` when you want a profiling build:

```xml
<PropertyGroup Condition="'$(NetAndroidProfiler)' == 'true'">
  <EnableDiagnostics>true</EnableDiagnostics>
</PropertyGroup>
<ItemGroup Condition="'$(NetAndroidProfiler)' == 'true'">
  <AndroidEnvironment Include="profiling.env" />
</ItemGroup>
```

Sampling only needs the `EnableDiagnostics` part; the environment file is
required only for instrumenting.

## MONO_DIAGNOSTICS options

Whitespace-separated options, parsed by the Mono runtime at startup:

| option | effect |
|---|---|
| `--diagnostic-mono-profiler=enable` | enables the `Microsoft-DotNETRuntimeMonoProfiler` EventPipe provider (disabled by default since .NET 8). Required for any instrumenting session. |
| `--diagnostic-mono-profiler=alloc` | installs the allocation hook at startup so allocation events can be emitted. Every allocation goes through the slow path while the app runs, session or not *(overhead on a real app to measure - U17)*. Omit if you only need enter/leave. |
| `--diagnostic-mono-profiler=exception` | installs exception-clause hooks (throw/catch/finally events). Optional. |
| `--diagnostic-mono-profiler-callspec=<callspec>` | which methods get enter/leave instrumentation. Without it nothing is instrumented. |

Callspec grammar (comma separated; `-` prefix excludes): `all`, `none`,
`program`, `assembly`, `N:Namespace`, `T:Full.Type.Name`,
`M:Full.Type.Name:Method`. Examples:

```
N:V7                                   # every namespace starting with V7 (App.Droid, App.Core, ...)
N:App.Core,T:App.Droid.MainActivity
N:V7,-T:App.Core.Utils.FastHash         # exclude a hot leaf type (exclusion syntax still to validate - P3)
```

Sizing rule from the spike: each enter/leave/allocation event costs about
10 us on the emulator and ~60 bytes of trace; a method called 2M times per
second is unusable under instrumentation. Start from the namespaces you
care about, exclude hot leaves, and use sampling to find out what is hot
before instrumenting it.

## Does the baked environment file affect normal runs? (Release `Profiling` builds)

When no profiling session is active:

- `enable` and `callspec`: methods are JIT-compiled normally; enter/leave
  instrumentation is decided at JIT time *only while a session with the
  instrumentation keyword is running*, so an app started without the
  profiler runs uninstrumented. Negligible cost.
- `alloc`: the allocation hook is active for the whole process lifetime -
  this is the one option with a permanent cost, hence the separate
  configuration rather than adding it to Debug.
- `EnableDiagnostics`: a background connect attempt to 127.0.0.1:9000 at
  startup (nosuspend), the diagnostics component loaded (~270 KB).

Once the profiler has instrumented a method in a session, the
instrumentation stays until the process exits (later sessions receive the
events again without a restart).

Practical consequence: on Release builds `alloc` is what justifies a
separate `Profiling` configuration (permanent allocation-hook cost);
`enable` + `callspec` alone would be cheap enough to ship in Release. On
Debug builds none of this applies: the profiler injects the variables only
for the duration of a session.

## Traps

- **Clean build after changing the environment file.** Changing
  `profiling.env` (or switching it on/off) on an incremental build produced
  an APK that crashed at startup under a diagnostics session
  (`LinkageError: No implementation found for ...n_onCreate`). Delete
  `obj/` and `bin/` of the app project, then rebuild.
- **Commas in msbuild `-p:` values** are property separators: when passing
  a callspec through `-p:` escape them as `%2C` (not needed in the .env
  file).
- **Do not start the app from Visual Studio** while the device property
  `debug.mono.profile` is set: it blocks on the splash screen. The profiler
  sets and clears the property itself; if a session dies, clear it with
  `adb -s <serial> shell "setprop debug.mono.profile ''"`.
- **Suspend mode**: the first instrumenting session starts the app
  suspended until the tool connects; watchdogs that expect the app to be
  responsive within seconds may fire *(to verify on the reference application - U4b)*.
- The device property is global: two .NET apps on the same device both
  read it. Profile one app at a time per device.

## What the profiler checks before a session

(planned for P1 - U18) diagnostics component present in the APK; AOT
libraries present (warning: leaf attribution imprecise); environment
contains `MONO_DIAGNOSTICS` with `enable` (instrumenting); `alloc` present
when allocation tracking is requested; pdbs found next to the build output
when source annotation is requested. Each failure names the missing setting
and points to this document.
