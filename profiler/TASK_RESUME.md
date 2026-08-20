# Task resume

## Current task
P1 - Core + MCP sampling (started 2026-08-20 after P0 spike).

## Current substep
P1 step 1 (U17: Debug builds) DONE: Debug + EnableDiagnostics works;
MONO_DIAGNOSTICS injected per session through the override environment
file (run-as), verified with a full instrumenting trace (debugenv4).
Docs updated (notes, APP_SETUP, KNOWN_UNKNOWNS, PROJECT_STATE). Committing.

## Next action if interrupted right now
Commit/push docs; then start P1 step 2: Core skeleton.

## P1 plan (in order)
1. [done] U17 Debug-build test.
2. Core skeleton in src/NetAndroidProfiler.Core:
   - Devices/AdbClient (serial-explicit adb wrapper, run-as, push/pull,
     setprop, logcat, pidof, launch via monkey/LAUNCHER).
   - Apk/AppInspector: prerequisites of an APK/installed app (diagnostics
     component, AOT libs, debuggable, abi, package) -> PrerequisiteReport.
   - Collection/EnvironmentOverrideFile: read/write the override env file
     format (NUL-padded records) + inject/restore MONO_DIAGNOSTICS and
     DOTNET_DiagnosticPorts.
   - Collection/DsRouterProcess, DotnetTraceProcess, GcDumpProcess: process
     wrappers with retry on EndOfStream at session start, per-device port.
   - Analysis/SamplingAnalyzer (TraceLog -> sample tree, exclusive/
     inclusive, wait-frame classification), MonoProfilerDecoder
     (enter/leave + alloc, manifest layouts), AllocAnalyzer, GcDumpReader.
   - Store/ResultStore: SQLite schema v1 (separate tables per kind).
   - ProfilerSession facade + SessionSpec/SessionInfo records.
3. Fast tests on tests/.../recorded/*.nettrace + .gcdump (no device).
4. Integration tests on emulator-5556 with TestTarget Debug build.
5. MCP server (thin) with the P1 tool set.
6. annotate_source via portable pdb (DiaSymReader approach from
   Desymbolicate) - last P1 item.

## What works
- Entire chain proven at probe level (DevTools/NetTraceProbe); TestTarget
  Debug build installed on emulator-5556 with EnableDiagnostics.

## What is failing
- Nothing open.

## Traps / hypotheses
- debug.mono.env >90 bytes aborts the app (libmonodroid buffer): never use
  it for MONO_DIAGNOSTICS.
- Override env file is 0400: rm + cp + chmod 400 via run-as.
- Git Bash converts /data/... paths: MSYS_NO_PATHCONV=1 for adb commands.
- debug.mono.profile is device-global; clear it after sessions. Currently
  set to suspend on emulator-5556 (clear before normal app use).
- dotnet-trace -p <dsrouter> EndOfStream right after launch: retry.
- Incremental build after env change -> broken APK: wipe obj/ bin/.
- msbuild -p: commas -> %2C.
