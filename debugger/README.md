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

Then: `list_devices` and `list_app_projects` → `launch_app(...)`
→ `set_breakpoint(file, line)` → `wait_until_stopped` / `continue_and_wait` →
`get_locals`, `get_call_stack`, `evaluate_expression`, `step_*` → `terminate_app`.
Breakpoint file paths must be the absolute paths compiled into the app's PDB —
`get_source_files(file)` reports the paths the running app was actually built
with, which is how a breakpoint that stays pending is diagnosed.

Nothing that the project already declares needs to be restated:
`launch_app(solutionOrFolder: "C:\path\to\repo")` finds the Android application
project, reads its `ApplicationId`, and uses the only ready device. Libraries
that target Android are not offered — an application declares an `ApplicationId`
or is an `Exe` — and anything ambiguous fails listing the candidates rather than
guessing. `list_app_projects` is the counterpart of `list_devices` for choosing.

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
ready-made nvim-dap configuration.

VS Code cannot run an arbitrary adapter named in `launch.json`: the debug *type*
has to be contributed by an extension. `DevTools/vscode/net-android-debugger/` is
a small one that contributes `net-android` and holds no logic, and

```
install-vscode-extension.cmd
```

installs it, by junctioning that folder into `%USERPROFILE%\.vscode\extensions`
(no elevation, no developer mode; `NAD_VSCODE_EXTENSIONS` retargets it). Restart
VS Code, and from then on a `launch.json` entry naming `"type": "net-android"`
and the app's `packageName` is the entire configuration — everything else is
deduced, including the device when only one is ready.

`THIRD-PARTY-NOTICES.txt` lists every third-party component shipped with the
binaries, with its licence in full; `register-mcp.cmd` copies it next to them.

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

Requires: .NET SDK 10 with the `android` workload, Android SDK platform-tools
(`adb`), and an emulator or attached device for integration tests.
`NAD_SKIP_DEPLOY=1` skips redeploying TestTarget.

### On a new machine

```powershell
git clone --recurse-submodules https://github.com/csm101/net-android-debugger.git
```

`debugger-libs` is a submodule pinned to a fork (see ARCHITECTURE.md, "ThirdParty
vendoring status"), so a plain `git clone` needs `git submodule update --init`
afterwards or nothing builds.

Then `dotnet test` to confirm the environment, and `register-mcp.cmd` to publish
and register the MCP server on that machine — the registration is per machine,
and it runs the published binaries rather than the working tree, so it has to be
re-run after changes you want to use from Claude Code.

Set `NAD_DEVICE_SERIAL=<serial>` if the machine has more than one device
attached. It is the per-machine answer to "which device", and it is used both by
the test suite and by every launch: a call's own argument wins over it, and it
wins over the deduction, so nothing has to name a serial in a committed file.
