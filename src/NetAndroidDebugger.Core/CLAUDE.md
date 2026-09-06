Guidance specific to the debugger component. The root `CLAUDE.md` holds the rules
shared by both products; read it first.

# Project purpose

Build a debugger for **.NET for Android** applications (C# on MonoVM), exposed
through an **MCP frontend** first and a **DAP frontend** - mirroring the
architecture of the Delphi Win64 debugger project.

Key technical fact (do not re-derive): C# code on Android is **invisible to
JDWP**. It is debugged through the **Mono Soft Debugger protocol (SDB)** - a TCP
agent inside the app's MonoVM, reached via `adb forward`. See
`docs/debugger/ANDROID_ATTACH_NOTES.md`.

# Architecture rules (blocking)

- The engine lives in `src/NetAndroidDebugger.Core` and is **frontend-neutral
  and JSON-free**: no MCP types, no DAP types, no JSON serialization in Core.
  Frontends translate.
- `NetAndroidDebugger.Mcp` and `NetAndroidDebugger.Dap` are thin frontends over
  the same `DebugSession` facade. `src/NetAndroidDebugger.Shared` holds the two
  sources both frontends link (launch configuration and exception rule files: a
  file format is a frontend concern, Core stays JSON-free).
- Third-party debugger libraries (`mono/debugger-libs`: `Mono.Debugger.Soft`,
  `Mono.Debugging`, `Mono.Debugging.Soft`) are consumed as a git submodule under
  `ThirdParty/debugger-libs` - treat them as **read-only upstream**; local patches
  must be documented in `docs/debugger/ARCHITECTURE.md` ("ThirdParty vendoring
  status"). `ThirdParty/Mono.Debugging.overrides` pins their target framework and
  must stay one folder above the submodule.
- **Licensing (blocking):** never reuse code, binaries, or protocol adapters
  from the proprietary C# Dev Kit / .NET MAUI VS Code extension. MIT sources
  (`mono/debugger-libs`, `microsoft/vscode-mono-debug`, `dotnet/android`) are
  fine and are the reference implementations to read.

# Living specifications

Under `docs/debugger/`: `ARCHITECTURE.md` (modules, threading model, session state
machine, frontend contracts, vendoring status of ThirdParty),
`ANDROID_ATTACH_NOTES.md` (everything empirically known about deploying, launching
and attaching to a .NET Android app: msbuild properties, adb, sysprops, SDB
handshake, quirks per device/emulator), `KNOWN_UNKNOWNS.md`, `TEST_CATALOG.md`,
`PROJECT_STATE.md`, `TASK_RESUME.md`, and the user-facing `README.md`.

# Test suite

`tests/NetAndroidDebugger.Tests` drives the real engine against
`TestTarget/Debugger` (package `net.androiddebugger.testtarget`) on the emulator
or an attached device. `NAD_DEVICE_SERIAL` is mandatory with more than one device
attached; `NAD_SKIP_DEPLOY=1` skips the redeploy. The device classes carry no
trait: without a device they fail at fixture initialization rather than skip.

```powershell
dotnet build NetAndroidDebugger.slnx
dotnet test  NetAndroidDebugger.slnx
```

# Frontends and install

`register-mcp-debugger.cmd` publishes the MCP server and the DAP adapter (Release)
to `%LOCALAPPDATA%\net-android-debugger` (override with `NAD_INSTALL_DIR`) and
registers the server in Claude Code as `net-android-debugger`.
`vscode\install-vscode-extension.cmd` installs the VS Code extension kept in
`vscode/net-android-debugger`; `vscode/DAP_CLIENTS.md` documents every launch
argument. The root `.vscode/launch.json` is the example launch configuration for
TestTarget, read by both the DAP frontend and `launch_from_config`.

# Code generation

Beyond the shared rules: prefer records and immutable data for protocol and
session snapshots, and always flow `CancellationToken` through engine waits.
