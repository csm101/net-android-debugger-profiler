# DevTools

Argv-driven diagnostic probes, versioned with the project (same role as the
Delphi project's DevTools). Each probe is a small console project answering one
empirical question about the SDB protocol, adb behavior, or a live debuggee.

Rules:
- Every path, port, package name and symbol comes from the command line.
  A probe that only works against TestTarget is a bug.
- Probes are kept, not deleted: they re-answer their question after upstream
  or toolchain changes.
