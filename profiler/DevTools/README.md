# DevTools

Argv-driven diagnostic probes, versioned with the project (same role as in
the sibling debugger project). Each probe is a small console project answering
one empirical question about EventPipe providers, trace content, or collection
behavior.

Rules:
- Every path, port, package name, provider string comes from the command
  line. A probe that only works against TestTarget is a bug.
- Probes are kept, not deleted: they re-answer their question after toolchain
  or workload updates.

## Probes

- `NetTraceProbe/` - inspect a .nettrace with TraceEvent:
  `providers` (provider/event counts), `events <provider> [max] [event]`
  (payload dump, raw hex when TraceEvent has no schema), `topn [N]`
  (sampling hotspots via TraceLog), `stacks <provider> [max]`, `monoprof [N]`
  (manual decoder for Microsoft-DotNETRuntimeMonoProfiler enter/leave +
  allocation events, MethodID resolved through the rundown).
  Run: `dotnet run --project DevTools/NetTraceProbe -- topn x.nettrace 30`.
