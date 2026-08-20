# net-android-debugger

A debugger for **.NET for Android** applications (C# on MonoVM), designed to be
driven by AI agents through **MCP** (Model Context Protocol), with an optional
**DAP** (Debug Adapter Protocol) frontend planned for VS Code.

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

M0 (spike) done, M1 (engine + MCP) in progress: launch/attach with automatic
attach to helper processes, breakpoints, stepping, call stack, locals,
evaluation and an MCP stdio server exposing them. See `PROJECT_STATE.md` for
milestones and `ARCHITECTURE.md` for the design.

## Using the MCP server with Claude Code

```
register-mcp.cmd
```

Publishes the server (Release) to `%LOCALAPPDATA%\net-android-debugger`
(override with `NAD_INSTALL_DIR`) and registers it once at user scope
(`claude mcp add --scope user net-android-debugger -- dotnet <dir>\NetAndroidDebugger.Mcp.dll`).
Re-run after changes to republish; stop running sessions first (the dll is locked).

Then: `list_devices` → `launch_app(deviceSerial, packageName[, projectPath, deploy])`
→ `set_breakpoint(file, line)` → `wait_until_stopped` / `continue_and_wait` →
`get_locals`, `get_call_stack`, `evaluate_expression`, `step_*` → `terminate_app`.
Breakpoint file paths must be the absolute paths compiled into the app's PDB.

## Layout

```
src/NetAndroidDebugger.Core    engine library (frontend-neutral, JSON-free)
src/NetAndroidDebugger.Mcp     MCP stdio server frontend
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
