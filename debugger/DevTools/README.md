# DevTools

Argv-driven diagnostic probes, versioned with the project (same role as the
Delphi project's DevTools). Each probe is a small console project answering one
empirical question about the SDB protocol, adb behavior, or a live debuggee.

Rules:
- Every path, port, package name and symbol comes from the command line.
  A probe that only works against TestTarget is a bug.
- Probes are kept, not deleted: they re-answer their question after upstream
  or toolchain changes.

## Probes

- `SdbProbe` — connects Mono.Debugging.Soft to an SDB agent on
  `127.0.0.1:<port>` (through `adb forward`), sets one source-line breakpoint,
  dumps threads / backtrace / locals on each hit, continues, detaches.
  `SdbProbe --port 10000 --file <abs source path> --line <n> [--hits 2] [--timeout 90] [--app name]`.
  Answered U2/U3 on 2026-08-20 (see ANDROID_ATTACH_NOTES.md).
- `scripts/multiprocess-single-port.sh`, `scripts/multiprocess-port-rotation.sh`
  — bash drivers for the multi-process experiments (same port → helper dies in
  a loop; port rotation → main and helper debugged concurrently). Usage in the
  script header. Results: ANDROID_ATTACH_NOTES.md "Multi-process apps".

## DapSmoke

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
  "C:\Work\ReferenceApp\App.Sync\Threads\BaseSyncPalmThread.cs" 112 --keep-fresh
```

Exit code 0 when every step answered.
