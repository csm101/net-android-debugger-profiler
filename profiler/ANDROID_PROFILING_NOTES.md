# Android profiling notes

Living specification: everything empirically known about collecting profiles
from a .NET for Android app. Facts marked **[verified]** were confirmed on
this machine or in primary sources; everything else is **[unverified]** until
exercised by the spike/tests.

## Collection fundamentals

- MonoVM on Android embeds **EventPipe**; diagnostic tools reach it through
  the **dotnet-dsrouter** proxy over adb. **[verified - dotnet/android docs]**
- CPU sampling is dotnet-trace's default cpu-sampling profile.
  **[verified - docs; not yet exercised locally]**
- Profiling works on **Release** builds (unlike the debugger, which needs
  Debug + fast deployment). **[verified - docs]**

## Build-side switches

    dotnet build -c Release -p:EnableDiagnostics=true   # legacy alias: AndroidEnableProfiler

Optional msbuild properties (auto-enable the diagnostics component):
DiagnosticAddress (10.0.2.2 emulator / 127.0.0.1 device), DiagnosticPort
(default 9000), DiagnosticSuspend (block startup until the tool connects -
startup profiling), DiagnosticListenMode.
**[verified - dotnet/android tracing guide]**

Equivalent runtime configuration without rebuilding msbuild props:
DOTNET_DiagnosticPorts (e.g. "10.0.2.2:9000,suspend,connect") or
adb shell setprop debug.mono.profile '10.0.2.2:9000,suspend,connect'.
**[verified - same guide; setprop path to be re-tested in P0]**

## Collecting

Simplified (dotnet-trace >= 9.0.621003, dsrouter integrated):

    dotnet-trace collect --dsrouter android --format speedscope

Manual: dotnet-dsrouter android + dotnet-trace collect -p <pid>.
Memory: dotnet-gcdump collect -p <pid>  ->  .gcdump file.
Physical device: adb reverse tcp:9000 tcp:9001; emulator needs nothing
(10.0.2.2). **[verified - docs]**

Known trap: do not launch the app through Visual Studio while a diagnostics
config is active - it freezes on the splash screen. **[verified - docs]**

## Runtime instrumenting provider

Microsoft-DotNETRuntimeMonoProfiler (experimental, **disabled by default
since .NET 8**):

- enable: MONO_DIAGNOSTICS=--diagnostic-mono-profiler=enable
- method enter/leave filtered by callspec:
  MONO_DIAGNOSTICS=--diagnostic-mono-profiler-callspec=<pattern>
- allocation tracking: --diagnostic-mono-profiler=alloc
- events: enter/leave, JIT, allocations with callstacks, GC events/heap
  dumps, roots, handles, finalization
- constraint: enter/leave instrumentation is decided at JIT time - the
  EventPipe session must be configured before methods get JITted (use
  suspend).

**[verified - dotnet/runtime diagnostics-tracing design doc; availability on
net9/net10 android to be confirmed in P0 - see KNOWN_UNKNOWNS U2]**

## Analysis

- .nettrace parses with **TraceEvent**
  (Microsoft.Diagnostics.Tracing.TraceEvent, MIT, NuGet). **[verified]**
- Output formats: .nettrace (PerfView/VS), speedscope JSON, .gcdump.
  **[verified - docs]**

## Reference implementations (MIT, read-only)

- dotnet/diagnostics (dotnet-trace, dotnet-dsrouter, dotnet-gcdump sources)
- microsoft/perfview (TraceEvent + analysis algorithms)
- jonathanpeppers/Mono.Profiler.Android (mono log profiler support for
  .NET Android - alternative/legacy collection path worth reading)
- Fody + MethodTimer.Fody (IL weaving enter/leave pattern for P3)

## This machine

- adb 1.0.41 (36.0.0), on PATH; emulator AVD DevicePerSviluppoProfiler
- .NET SDK 10.0.301, workloads: android 36.1.43 (VS 18.7)
- dotnet-trace / dotnet-dsrouter / dotnet-gcdump: **not yet installed**
  (dotnet tool install -g ... in P0)

## Real target: the reference application

- C:\Work\ReferenceApp\the reference application.sln, app project App.Droid,
  TFM net9.0-android35.0, ApplicationId=App.Droid

## Sources

- https://github.com/dotnet/android/blob/main/Documentation/guides/tracing.md
- https://github.com/dotnet/runtime/blob/main/docs/design/mono/diagnostics-tracing.md
- https://github.com/microsoft/perfview
- https://github.com/jonathanpeppers/Mono.Profiler.Android

## Multi-device rule

This machine can run two emulators at once (debugger project: AVD
pixel_7_-_api_33_0; this project: AVD DevicePerSviluppoProfiler). Never rely
on adb's single-device default: pass the serial explicitly (adb -s <serial>,
or ANDROID_SERIAL env var) in every orchestration command. Device selection
for dsrouter/dotnet-trace with multiple devices: see KNOWN_UNKNOWNS U12.
