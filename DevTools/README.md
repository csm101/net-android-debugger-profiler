# DevTools

Argv-driven diagnostic probes, versioned with the repository (same role as the
Delphi project's DevTools). Each probe is a small console project answering one
empirical question: about the SDB protocol, adb behavior or a live debuggee for
the debugger; about EventPipe providers, trace content or collection behavior for
the profiler. `scripts/` holds the shell drivers both products share, starting
with `ensure-emulator.sh`.

Rules:
- Every path, port, package name, symbol and provider string comes from the
  command line. A probe that only works against a TestTarget is a bug.
- Probes are kept, not deleted: they re-answer their question after upstream,
  toolchain or workload changes.

## Debugger probes

- `SdbProbe` — connects Mono.Debugging.Soft to an SDB agent on
  `127.0.0.1:<port>` (through `adb forward`), sets one source-line breakpoint,
  dumps threads / backtrace / locals on each hit, continues, detaches.
  `SdbProbe --port 10000 --file <abs source path> --line <n> [--hits 2] [--timeout 90] [--app name]`.
  Answered U2/U3 on 2026-08-20 (see `docs/debugger/ANDROID_ATTACH_NOTES.md`).
- `scripts/multiprocess-single-port.sh`, `scripts/multiprocess-port-rotation.sh`
  — bash drivers for the multi-process experiments (same port → helper dies in
  a loop; port rotation → main and helper debugged concurrently). Usage in the
  script header. Results: `docs/debugger/ANDROID_ATTACH_NOTES.md` "Multi-process apps".
- `scripts/ensure-emulator.sh` — brings one named emulator up and healthy, or
  returns at once when its serial already answers. `AVD=<name> SERIAL=emulator-5554`
  select the image and the serial; with several AVDs installed the name is mandatory.
- `scripts/pause-survival-watch.sh` — samples an app's pids and the ANR/watchdog
  logcat lines while the debugger holds it paused (debugger U6).

### DapSmoke

Drives the DAP adapter against any installed app, the way an editor would, and prints what came
back: capabilities, breakpoint binding, the stop, threads, stack, locals, one expansion, an
evaluation, continue, disconnect. It exists to try the adapter against a real app without
republishing anything — the end-to-end tests only ever point it at TestTarget.

```
dotnet run --project DevTools/DapSmoke -- <serial> <package> <sourceFile> <line> [--keep-fresh]
```

For example, against the reference application:

```
dotnet run --project DevTools/DapSmoke -- emulator-5554 App.Droid \
  "C:\Work\ReferenceApp\App.Sync\Threads\SyncThread.cs" 112 --keep-fresh
```

Exit code 0 when every step answered.

### ExceptionTypeProbe

Answers whether a rule that names a type still matches when the throw site has
no debug info (debugger KNOWN_UNKNOWNS U15). Drives a real app with `ignore *Mqtt*` plus
a catch-all `log`, and prints what the rules decided.

```
dotnet run --project DevTools/ExceptionTypeProbe -- <serial> <package> [suspendSeconds] [watchSeconds]
```

It waits for the app to load its MQTT client before suspending it. Suspending
during startup proves nothing: there is no connection yet to time out, which is
how the first two runs of this probe came back empty.

## Profiler probes

- `NetTraceProbe/` - inspect a .nettrace with TraceEvent:
  `providers` (provider/event counts), `events <provider> [max] [event]`
  (payload dump, raw hex when TraceEvent has no schema), `topn [N]`
  (sampling hotspots via TraceLog), `stacks <provider> [max]`, `monoprof [N]`
  (manual decoder for Microsoft-DotNETRuntimeMonoProfiler enter/leave +
  allocation events, MethodID resolved through the rundown).
  Run: `dotnet run --project DevTools/NetTraceProbe -- topn x.nettrace 30`.
- `WeaveProbe/` - console probe over the profiler's IL weaver.
- `WeaveBench/` - weaving benchmark against `tests/WeaveSample` and the Collector.
