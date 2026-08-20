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

Early scaffold. See `PROJECT_STATE.md` for milestones and `ARCHITECTURE.md`
for the design.

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

Requires: .NET SDK 8+ with the `android` workload, Android SDK platform-tools
(`adb`), and an emulator or attached device for integration tests.
