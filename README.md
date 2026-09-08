# net-android-debugger-profiler

A debugger and a profiler for .NET for Android applications, kept in one repository. The
debugger attaches to C# apps running on MonoVM, .NET MAUI apps included, through the Mono
Soft Debugger protocol; an MCP server lets Claude Code and other agents drive it, and a
Debug Adapter Protocol adapter lets VS Code and other editors debug through it. The profiler
collects CPU sampling, memory and instrumenting profiles over EventPipe and IL weaving, and
exposes them to agents as an MCP server, to scripts as the `nap` command line and to people
as a Delphi desktop GUI. Both are proprietary software of MCA Software s.a.s. di Sirna
Carlo & C.; the repository is private.

## The two products

| Product | What it is | Sources | Read first |
|---|---|---|---|
| **net-android-debugger** | Breakpoints, stepping, stack, locals, evaluation and an ordered exception rule engine over the Mono Soft Debugger protocol, plus screen tools (screenshot, UI hierarchy, tap, swipe, keys) so an agent can bring the app to the point worth debugging. Frontends: MCP server (`net-android-debugger`), DAP adapter, VS Code extension. | `src/NetAndroidDebugger.Core`, `.Mcp`, `.Dap`, `.Shared`; `ThirdParty/debugger-libs` (submodule); `vscode/` | [docs/debugger/README.md](docs/debugger/README.md) |
| **net-android-profiler** | CPU sampling, allocation and heap analysis, instrumenting through the runtime's Mono profiler provider or a runtime-independent IL weaver; results in a versioned SQLite database. Frontends: MCP server (`net-android-profiler`), `nap` (one-shot commands and the local control service), the DevExpress GUI `NapGui.exe`. | `src/NetAndroidProfiler.Core`, `.Mcp`, `.Cli`, `.Weave`, `.Collector`; `gui/`; `build/` | [docs/profiler/README.md](docs/profiler/README.md) |

Both can also be driven through **one MCP server**, `net-android` (`src/NetAndroid.Mcp`): every
debugger and profiler tool in one process, the three tools both products define answered once,
and the device-global Mono state arbitrated so that debugging and profiling never start on the
same device at the same time. Why the two products share a repository, the shared device library
`src/NetAndroid.Device` and the unified server are in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Build

```powershell
git clone --recurse-submodules git@github.com:mca-software/net-android-debugger-profiler.git
cd net-android-debugger-profiler
dotnet build NetAndroidDebuggerProfiler.slnx     # everything, the example app included
dotnet build NetAndroidDebugger.slnx             # the debugger only
dotnet build NetAndroidProfiler.slnx             # the profiler only, the example app included
```

Requires .NET SDK 10 with the `android` workload (the MAUI example builds with the
`Microsoft.Maui.Sdk` pack that workload set installs), the Android SDK platform-tools (`adb`;
both products find it without PATH, through `ANDROID_HOME`, the registry or the SDK's default
folders), and for the profiler the global tools `dotnet-dsrouter` and `dotnet-trace`.
`ThirdParty/debugger-libs` is a submodule pinned to a fork: after a plain `git clone`, run
`git submodule update --init` or the debugger does not build.

**Installing on a machine without this repository**: `gui\build-gui.cmd` (needs RAD Studio) and then
`powershell -File build\package.ps1` produce `dist\net-android-<version>\` and its zip: the unified
server with its dependencies, `nap`, `nap-weave`, dotnet-dsrouter, the weaving targets, the GUI and
the Claude Code plugin files, so that the unpacked folder is a plugin. Its `install.cmd` registers the
plugin (server and skill) and puts a shortcut to the GUI on the desktop. The skill
(`plugin/skills/net-android/SKILL.md`) is the operating guide for an agent: preflight, which mode
answers which question, reading the numbers, driving the app's screen, the traps; `docs/PACKAGING`
under the profiler's documents describes the package.

Each product publishes and registers itself with Claude Code from its own script, and the unified
server from `register-mcp.cmd` (a Claude Code with all three registered sees every tool twice; the
script says which two registrations to remove once the unified server is in use):

```
register-mcp.cmd               publishes to %LOCALAPPDATA%\net-android and registers net-android (both products, one server)
register-mcp-debugger.cmd      publishes to %LOCALAPPDATA%\net-android-debugger and registers net-android-debugger
register-mcp-profiler.cmd      publishes to %LOCALAPPDATA%\net-android-profiler and registers net-android-profiler
vscode\install-vscode-extension.cmd   installs the debugger's VS Code extension (junction into the extensions folder)
build-gui.cmd                  builds the profiler GUI, gui\NapGui.exe (RAD Studio with DevExpress and SynEdit)
```

## Test

```powershell
dotnet test NetAndroidDebugger.slnx                                  # the whole debugger suite (needs a device)
dotnet test NetAndroidProfiler.slnx --filter "Category!=Device"     # the profiler's recorded-trace tests
dotnet test NetAndroidProfiler.slnx                                  # plus the profiler's device tests
dotnet test tests/NetAndroid.Device.Tests/NetAndroid.Device.Tests.csproj   # the shared device layer (docs/TEST_CATALOG.md)
dotnet test tests/NetAndroid.Mcp.Tests/NetAndroid.Mcp.Tests.csproj         # the unified MCP server (docs/TEST_CATALOG.md)
```

Both suites drive their real engine against a real app on the Android emulator or an
attached device. With more than one device attached neither guesses: `NAD_DEVICE_SERIAL`
names the debugger's device, `NAP_TEST_SERIAL` the profiler's. `DevTools/scripts/ensure-emulator.sh`
brings an emulator up (`AVD=<name> SERIAL=emulator-5554`). The two device suites share one
device only in turn: the debugger sets a device-wide Mono property while it runs. Each
product's `TEST_CATALOG.md` says what is covered.

## The example app is the profiler GUI's tutorial

`examples/ProfileMeExample.sln` is a .NET MAUI Android app built to be profiled: every
screen carries one deliberate performance problem, chosen so that a different feature of the
profiler is the one that exposes it, and every screen has a Guide saying what to look for.
It is a shipped part of the repository, not a test fixture. The per-screen table, the build
and install command, the package name, the symbols folder and the callspec are in
[docs/profiler/README.md](docs/profiler/README.md#example-app-profilemeexample); the Guide
texts live in `examples/ProfileMeExample/Scenarios/ScenarioCatalog.cs`. Future debugger
examples go beside it under `examples/`.

## Layout

```
src/NetAndroidDebugger.Core, .Mcp, .Dap, .Shared     debugger engine and frontends
src/NetAndroidProfiler.Core, .Mcp, .Cli, .Weave, .Collector   profiler engine and frontends
src/NetAndroid.Device                                 the device layer both products share (adb, locator, screen, device-side state)
ThirdParty/debugger-libs                              mono/debugger-libs, git submodule (MIT)
tests/NetAndroidDebugger.Tests, tests/NetAndroidProfiler.Tests, tests/WeaveSample
TestTarget/Debugger, TestTarget/Profiler (+ TestTarget.Support)   the two test apps
DevTools/                                             probes of both products; scripts/ shared
gui/                                                  the profiler's Delphi + DevExpress GUI
build/                                                profiler packaging and build-time weaving targets
examples/                                             ProfileMeExample, the profiler GUI tutorial
vscode/                                               the debugger's VS Code extension and DAP client notes
docs/                                                 ARCHITECTURE.md, KNOWN_UNKNOWNS.md; docs/debugger/, docs/profiler/
```

Each product keeps its living documents under `docs/<component>/`: `ARCHITECTURE.md`,
`PROJECT_STATE.md`, `TASK_RESUME.md`, `KNOWN_UNKNOWNS.md`, `TEST_CATALOG.md` and the
empirical notes (`ANDROID_ATTACH_NOTES.md`, `ANDROID_PROFILING_NOTES.md`). The root
`TASK_RESUME.md` tracks repository-level work.

## Licensing

Proprietary and closed source: see `LICENSE`. The repository is private and must stay
private. Third-party dependencies are restricted to MIT, BSD and Apache-2.0 (plus MPL
components used unmodified in the GUI); GPL is excluded. `THIRD-PARTY-NOTICES.txt` lists,
in one part per product, every component the shipped builds carry, with the full license
texts; both test suites fail when a distributed package is missing from it.
