# Architecture

Living specification. Source of truth is the code; if this document and the
code disagree, the code wins. Update whenever a module's responsibilities,
threading model, or external contract changes.

## High-level wiring

```
MCP client (Claude Code) ── MCP / JSON-RPC 2.0 over stdio ── NetAndroidDebugger.Mcp ─┐
VS Code (future)         ── DAP / JSON over stdio ────────── NetAndroidDebugger.Dap ─┤
                                                                                     ▼
                                             DebugSession  (NetAndroidDebugger.Core)
                                             JSON-free facade: engine + state machine
                                             + source mapping + value formatting
                                                                                     │
                                             Mono.Debugging.Soft / Mono.Debugger.Soft
                                             (ThirdParty/ — vendored mono/debugger-libs)
                                                                                     │  SDB wire protocol over TCP
                                                     adb forward tcp:PORT tcp:PORT ──┤
                                                                                     ▼
                                             MonoVM soft-debugger agent inside the
                                             Android app process (net9.0-android)
```

Deployment/launch orchestration is a separate Core module (`AndroidLauncher`,
planned): wraps `dotnet build -t:Run -p:AndroidAttachDebugger=true ...` and raw
`adb` (device listing, forward, logcat). See `ANDROID_ATTACH_NOTES.md` for the
verified attach recipe.

## Design rules

- `DebugSession` is the single facade both frontends consume. It owns the
  soft-debugger session, breakpoint store, thread/frame snapshots, the value
  formatter, and an explicit session state machine
  (`NotStarted → Deploying → WaitingForConnection → Running ⇄ Stopped → Exited`).
  It exposes neutral records (no MCP/DAP ids) and async waits driven by a
  monotonic stop-generation counter — same pattern as the Delphi project's
  `TDebugSession`.
- Frontends are thin translators. Any logic useful to both belongs in Core.
- The MCP tool surface mirrors the Delphi MCP server (`delphi-win64-debugger`)
  wherever semantics carry over; Android-specific tools replace process-centric
  ones (see PROJECT_STATE.md for the target tool list).

## Modules (planned — nothing implemented yet)

| Module | Responsibility |
|---|---|
| `Core/DebugSession` | Facade, state machine, stop-generation waits |
| `Core/AndroidLauncher` | msbuild Run target wrapper, adb orchestration, logcat capture |
| `Core/BreakpointStore` | Source-line and exception breakpoints, pending resolution |
| `Core/ValueFormatter` | Locals/fields/array expansion, display strings |
| `Core/SourceResolver` | Map debuggee source paths ⇄ local checkout roots |
| `Mcp/` | stdio MCP server over `DebugSession` |
| `Dap/` (future) | stdio DAP adapter over `DebugSession` |

## Threading model

Mono.Debugging delivers debugger events on its own event thread. `DebugSession`
serializes state mutations onto a single logical owner and exposes thread-safe
snapshot reads; frontends never touch Mono.Debugging types directly.
(To be refined during M1 — record the actual model here.)

## ThirdParty vendoring status

Not yet vendored. Plan: `mono/debugger-libs` sources (Mono.Debugger.Soft,
Mono.Debugging, Mono.Debugging.Soft) + required Mono.Cecil dependency, as a git
submodule if it builds cleanly on net8.0, else a pruned source copy (the
approach `microsoft/vscode-mono-debug` took). Official NuGet packages are stale
(2017) — do not use them. Record chosen commit/branch and any local patches
here once vendored.
