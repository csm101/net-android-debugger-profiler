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
