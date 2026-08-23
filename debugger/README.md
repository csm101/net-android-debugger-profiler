# net-android-debugger

A debugger for **.NET for Android** applications (C# on MonoVM), designed to be
driven by AI agents through **MCP** (Model Context Protocol), and by editors
through **DAP** (Debug Adapter Protocol).

C# code on Android is invisible to JDWP: it runs inside MonoVM and is debugged
through the **Mono Soft Debugger protocol** (TCP, via `adb forward`). This
project wraps the open-source Mono debugger client libraries
([mono/debugger-libs](https://github.com/mono/debugger-libs), MIT) in a
frontend-neutral engine, then exposes semantic debugging tools (breakpoints,
stepping, stack, locals, evaluation) to autonomous agents.

Sibling project and architectural template:
[Delphi Win64 debugger](../delphi-visual-studio-code-debugger) — same
two-frontends-over-one-core layout, same development methodology.

## Status

M0-M3 done, M4 in progress: launch/attach with automatic attach to every process
of the app (including ones whose `android:process` name is unrelated to the
package), breakpoints, stepping, call stack, locals, expansion, evaluation,
exception filters, structured app output — exposed through an MCP stdio server
and, since 2026-08-21, a Debug Adapter Protocol adapter. See `PROJECT_STATE.md`
for milestones and `ARCHITECTURE.md` for the design.

## Using the MCP server with Claude Code

```
register-mcp.cmd
```

Publishes both frontends (Release) to `%LOCALAPPDATA%\net-android-debugger`
(override with `NAD_INSTALL_DIR`) and registers the MCP server once at user scope
(`claude mcp add --scope user net-android-debugger -- dotnet <dir>\NetAndroidDebugger.Mcp.dll`).
Re-run after changes to republish; stop running sessions first (the dll is locked).

Then: `list_devices` → `launch_app(deviceSerial, packageName[, projectPath, deploy])`
→ `set_breakpoint(file, line)` → `wait_until_stopped` / `continue_and_wait` →
`get_locals`, `get_call_stack`, `evaluate_expression`, `step_*` → `terminate_app`.
Breakpoint file paths must be the absolute paths compiled into the app's PDB —
`get_source_files(file)` reports the paths the running app was actually built
with, which is how a breakpoint that stays pending is diagnosed.

A project that keeps a `.vscode/launch.json` can skip the arguments:
`launch_from_config()` reads the same file, and the same fields, the DAP
frontend reads, so pressing F5 and launching through MCP cannot drift apart.
Exception rules given in the configuration are applied to the session, which is
where "this app throws these on purpose" belongs — with the app, rather than in
one person's profile. This repository's own `.vscode/launch.json` is an example.

## Using the DAP adapter from an editor

The same engine is also exposed as a Debug Adapter Protocol adapter, published
alongside the MCP server. A DAP client runs it directly:

```
dotnet "%LOCALAPPDATA%\net-android-debugger\NetAndroidDebugger.Dap.dll"
```

`DevTools/vscode/DAP_CLIENTS.md` has the launch/attach argument reference and a
ready-made nvim-dap configuration. For VS Code, which cannot run an arbitrary
adapter from `launch.json`, `DevTools/vscode/net-android-debugger/` is a small
extension that contributes the `net-android` debug type.
## Layout

```
src/NetAndroidDebugger.Core    engine library (frontend-neutral, JSON-free)
src/NetAndroidDebugger.Mcp     MCP stdio server frontend
src/NetAndroidDebugger.Dap     Debug Adapter Protocol frontend
tests/NetAndroidDebugger.Tests integration test suite (xUnit)
ThirdParty/                    vendored upstream (mono/debugger-libs)
DevTools/                      argv-driven diagnostic probes
TestTarget/                    minimal Android app used by the test suite
```

## Build

```powershell
dotnet build NetAndroidDebugger.slnx
dotnet test  NetAndroidDebugger.slnx
```

Clone with `git clone --recurse-submodules` (debugger-libs is a submodule).

Requires: .NET SDK 10 with the `android` workload, Android SDK platform-tools
(`adb`), and an emulator or attached device for integration tests. When more
than one device is attached, select the test device with
`NAD_DEVICE_SERIAL=<serial>`; `NAD_SKIP_DEPLOY=1` skips redeploying TestTarget.
