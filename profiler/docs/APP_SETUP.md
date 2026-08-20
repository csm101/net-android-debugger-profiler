# Preparing an app for profiling

The profiler never rebuilds your app: it profiles the APK exactly as you
built it and checks that the build satisfies the prerequisites of the
requested profiling mode. This document explains what each mode needs and
how to add it to the app project with the least impact on normal Debug
builds.

Verified on .NET SDK 10.0.301 / android workload 36.1.43; facts marked
*(to verify)* are still open and tracked in KNOWN_UNKNOWNS.md.

## What each mode needs

| Mode | Build requirement | Notes |
|---|---|---|
| CPU sampling | `EnableDiagnostics=true` | Nothing else. The profiler sets the device property `debug.mono.profile` at run time to point the app at the tool. |
| Heap snapshot (gcdump) | `EnableDiagnostics=true` | Same. |
| Instrumenting (enter/leave, exact allocations) | `EnableDiagnostics=true` **and** `MONO_DIAGNOSTICS` in the app environment **and** JIT (no AOT) | The environment variable can only be baked into the APK at build time. AOT-compiled methods are never instrumented. |
| Source-line annotation | portable pdb files of the build available to the profiler | Keep `DebugType=portable` (default) and do not delete the pdbs. |

`EnableDiagnostics=true` adds `libmono-component-diagnostics_tracing.so` to
the APK and bakes `DOTNET_DiagnosticPorts=127.0.0.1:9000,connect,nosuspend`
into the app environment. With nothing listening on that port the app starts
normally *(to verify on Debug builds with fast deployment - U17)*.

## Recommended: a dedicated `Profiling` configuration

Keep Debug untouched and add a configuration that inherits from it. the reference application
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

## Does the environment file affect normal runs?

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
