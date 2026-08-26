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

## Quick start from sources

There is no package to install and no marketplace entry: this is proprietary
software in a private repository, so everything starts from a clone. Steps 1-3
are once per machine.

### 1. Clone and build

```powershell
git clone --recurse-submodules https://github.com/csm101/net-android-debugger.git
cd net-android-debugger
dotnet build NetAndroidDebugger.slnx
```

`debugger-libs` is a submodule pinned to a fork (see ARCHITECTURE.md, "ThirdParty
vendoring status"). After a plain `git clone`, run `git submodule update --init`
or nothing builds.

Requires .NET SDK 10 with the `android` workload, and the Android SDK
platform-tools (`adb` on PATH). `dotnet test` confirms the whole environment,
but it drives a real emulator or device and takes fifteen to twenty minutes.

### 2. Publish and register the frontends

```
register-mcp.cmd
```

Publishes the MCP server and the DAP adapter (Release) to
`%LOCALAPPDATA%\net-android-debugger` (override with `NAD_INSTALL_DIR`), copies
`THIRD-PARTY-NOTICES.txt` beside them, and registers the MCP server with Claude
Code at user scope.

It runs the **published binaries, not the working tree**, so re-run it after any
change you want to use. Close open MCP sessions first: a running server keeps the
dll locked and the publish fails.

### 3. Install the VS Code extension

```
install-vscode-extension.cmd
```

Only needed for VS Code, and only because VS Code refuses to run a debug adapter
named directly in `launch.json` — the debug *type* has to come from an extension.
The script junctions `DevTools/vscode/net-android-debugger` into the user's
extensions folder; it needs no elevation, and the junction tracks the repository,
so a later `git pull` needs no reinstall.

Restart VS Code afterwards. Reloading the window is not enough for a newly
installed extension.

### 4. Say which device, if there is more than one

```powershell
adb devices
setx NAD_DEVICE_SERIAL emulator-5554
```

Skip this with a single device attached: the debugger uses the only one that is
ready. With several it never guesses — it fails listing them. `NAD_DEVICE_SERIAL`
is the per-machine answer, overridden by a serial passed in a call, and it also
selects the device the test suite uses.

### Debugging an app from VS Code

Three conditions on the app, and no change to its project file:

- **built Debug.** A Release APK is not debuggable: it is not marked
  `debuggable` and the Mono agent never starts.
- **installed on the device**, or deployed by the debugger (`deploy: true` with
  `projectPath`).
- **the device visible to `adb`.**

Then one entry in the app's `.vscode/launch.json`:

```json
{
  "version": "0.2.0",
  "configurations": [
    {
      "type": "net-android",
      "request": "launch",
      "name": "My app",
      "packageName": "com.example.myapp",
      "projectPath": "${workspaceFolder}/MyApp/MyApp.csproj",
      "deploy": true
    }
  ]
}
```

`packageName` is the app's `ApplicationId` and is the only required field. Drop
`projectPath` and `deploy` to relaunch an app that is already installed. The
`launch.json` editor offers both shapes as snippets.

Press F5. Breakpoints, stepping, locals, watches and the call stack behave as in
any other debug session. Stopping ends the app: the Mono runtime exits when the
debugger disconnects, so `terminateDebuggee: false` cannot be honoured.

`DevTools/vscode/DAP_CLIENTS.md` documents every launch argument, and configures
editors other than VS Code.

### Debugging an app from the MCP server

The same three conditions on the app. Nothing else has to exist — no
`launch.json`, no arguments that the project already declares:

```
launch_app(solutionOrFolder: "C:\path\to\the\solution")
```

That finds the Android application project, reads its `ApplicationId`, and uses
the device from step 4. `list_app_projects` shows the candidates when a solution
holds more than one application, and `list_devices` does the same for devices.
Then `set_breakpoint(file, line)` → `continue_and_wait` → `get_locals`,
`get_call_stack`, `evaluate_expression`, `step_*` → `terminate_app`.

A project that keeps a `.vscode/launch.json` can use `launch_from_config()`
instead: it reads the same file and the same fields the DAP frontend reads, so
pressing F5 and launching through MCP cannot drift apart.

If a breakpoint stays pending, the path is the usual reason: it must be the
absolute path compiled into the app's PDB, which is a path on the machine that
built it. `get_source_files(file)` reports the paths the running app was actually
built with.

## Status

M0-M3 done, M4 in progress: launch/attach with automatic attach to every process
of the app (including ones whose `android:process` name is unrelated to the
package), breakpoints, stepping, call stack, locals, expansion, evaluation,
exception filters, structured app output — exposed through an MCP stdio server
and, since 2026-08-21, a Debug Adapter Protocol adapter. See `PROJECT_STATE.md`
for milestones and `ARCHITECTURE.md` for the design.

## Using the MCP server with Claude Code

`register-mcp.cmd` registers the server once at user scope, as
`claude mcp add --scope user net-android-debugger -- dotnet <dir>\NetAndroidDebugger.Mcp.dll`.

The flow is `list_devices` and `list_app_projects` → `launch_app(...)` →
`set_breakpoint(file, line)` → `wait_until_stopped` / `continue_and_wait` →
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
alongside the MCP server. A DAP client runs it directly, and nothing registers it
— the client owns that command line:

```
dotnet "%LOCALAPPDATA%\net-android-debugger\NetAndroidDebugger.Dap.dll"
```

`DevTools/vscode/DAP_CLIENTS.md` has the launch and attach argument reference, a
ready-made nvim-dap configuration, and the behaviours worth knowing before wiring
a client up. VS Code needs the extension from step 3 of the quick start, because
a debug *type* can only come from an extension;
`DevTools/vscode/net-android-debugger/` is a small one that contributes
`net-android` and holds no logic.

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

Setting a machine up from scratch is the quick start at the top.
